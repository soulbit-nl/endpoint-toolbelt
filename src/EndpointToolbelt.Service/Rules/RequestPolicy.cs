using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.Configuration;

namespace EndpointToolbelt.Service.Rules;

/// <summary>Who is asking.</summary>
public sealed class CallerContext
{
    public CallerContext(string sid, int sessionId, string accountName)
        : this(sid, sessionId, accountName, sid, isTrustedServiceCaller: false)
    {
    }

    /// <param name="scopeSid">Whose user data a run may return; null for the device's data only.</param>
    public CallerContext(string sid, int sessionId, string accountName, string scopeSid, bool isTrustedServiceCaller)
    {
        Sid = sid;
        SessionId = sessionId;
        AccountName = accountName;
        ScopeSid = scopeSid;
        IsTrustedServiceCaller = isTrustedServiceCaller;
    }

    /// <summary>
    /// The caller's own SID: the key of its stored results and rate limits. For the trusted
    /// service caller this is that service's SID (S-1-5-80-...), never a user's.
    /// </summary>
    public string Sid { get; }

    /// <summary>
    /// The session whose user the run is for. For a local user, their own session; for the
    /// trusted service caller, the console user's session when that user is the only one signed
    /// in, otherwise 0. Other sessions are counted relative to it.
    /// </summary>
    public int SessionId { get; }

    /// <summary>
    /// The user whose data the engine may return besides the device's. For a local user, their
    /// own SID; for the trusted service caller, the console user (see <see cref="SessionId"/>) or
    /// null for the device scope only.
    /// </summary>
    public string ScopeSid { get; }

    /// <summary>The caller came through the trusted service caller pipe.</summary>
    public bool IsTrustedServiceCaller { get; }

    /// <summary>For the service log only; never sent to other users.</summary>
    public string AccountName { get; }
}

public sealed class PolicyDecision
{
    private PolicyDecision(ResultCode result, string message)
    {
        Result = result;
        Message = message;
    }

    public ResultCode Result { get; }
    public string Message { get; }
    public bool IsAccepted => Result == ResultCode.Accepted;

    public static PolicyDecision Accept() => new(ResultCode.Accepted, null);
    public static PolicyDecision Reject(ResultCode result, string message) => new(result, message);
}

/// <summary>
/// The rules every RunAction request must pass. The confirmation flags are asserted by the
/// client: a caller that writes its own client can set them, so they protect users from
/// accidental clicks, not from a determined local user. What a direct caller cannot skip is the
/// single worker, the rate limit, <c>AllowMachineWideOnMultiSession</c> and the engine's cooldowns.
/// Cooldowns are not checked here: the engine owns the single cooldown mechanism, because
/// some cooldowns are only decided after a read-only diagnosis.
/// </summary>
public static class RequestPolicy
{
    /// <summary>Support packages per user per hour on a shared device (window of the package limiter).</summary>
    public const int PackageRateLimitPerHour = 4;

    /// <summary>
    /// Read-only diagnoses per caller per hour, on every device: a read does not count toward
    /// <c>UserRateLimitPerHour</c>, but it takes the single worker while it runs.
    /// </summary>
    public const int ReadOnlyRateLimitPerHour = 30;

    /// <param name="multiSessionHost">The device is a multi-session host (see <see cref="Platform.MultiSessionHost"/>).</param>
    /// <param name="packageLimiter">One-hour limiter for support packages on shared devices; null: no package limit.</param>
    /// <param name="readOnlyLimiter">One-hour limiter for read-only actions; null: no limit of their own.</param>
    public static PolicyDecision EvaluateRunAction(
        Request request,
        CallerContext caller,
        int otherSessions,
        ServiceConfig config,
        ToolbeltAction? runningAction,
        RateLimiter rateLimiter,
        bool multiSessionHost = false,
        RateLimiter packageLimiter = null,
        ConfirmationNonces nonces = null,
        RateLimiter readOnlyLimiter = null)
    {
        if (request.ProtocolVersion != ProtocolInfo.Version)
        {
            return PolicyDecision.Reject(ResultCode.ProtocolMismatch, $"The app and the service use different protocol versions (service: {ProtocolInfo.Version}).");
        }
        if (!ActionCatalog.IsDefined(request.Action))
        {
            return PolicyDecision.Reject(ResultCode.InvalidRequest, "Unknown action.");
        }
        // The interface index is a number for Get a new IP address only, and it must name an adapter.
        if (request.Action == ToolbeltAction.RenewIpAddress ? request.InterfaceIndex <= 0 : request.InterfaceIndex != 0)
        {
            return PolicyDecision.Reject(ResultCode.InvalidRequest, request.Action == ToolbeltAction.RenewIpAddress ? "Choose a network adapter first." : "This action takes no network adapter.");
        }
        // A tool the organization switched off is refused to a user; the trusted service caller
        // may still run it, so the service desk keeps the tool.
        if (caller?.IsTrustedServiceCaller != true && ToolPolicy.IsOff(config?.DisabledTools, request.Action))
        {
            return PolicyDecision.Reject(ResultCode.ToolSwitchedOff, "Your organization has switched this tool off on this device. Ask your IT support if you need it.");
        }
        if (runningAction.HasValue)
        {
            return PolicyDecision.Reject(ResultCode.Busy, $"Another action is still running ({runningAction.Value}). Try again when it has finished.");
        }

        var info = ActionCatalog.Get(request.Action);
        if (info.RequiresConfirmation && !request.Confirmed)
        {
            return PolicyDecision.Reject(ResultCode.ConfirmationRequired, "This action needs your confirmation first.");
        }
        if (info.AffectsAllSessions && otherSessions < 0)
        {
            return PolicyDecision.Reject(ResultCode.MultiSessionBlocked, "The number of other signed-in users could not be determined, so this action cannot run now.");
        }
        if (info.AffectsAllSessions && otherSessions > 0)
        {
            if (!config.AllowMachineWideOnMultiSession)
            {
                return PolicyDecision.Reject(ResultCode.MultiSessionBlocked, "Your organization does not allow this action while other users are signed in to this device.");
            }
            if (request.ConfirmedOtherSessions < otherSessions)
            {
                // The count changed since the app asked (or it never asked): ask again.
                return PolicyDecision.Reject(ResultCode.ConfirmationRequired,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture, "Other users are signed in now ({0}). Confirm again to continue.", otherSessions));
            }
        }
        // A confirmation counts only when it belongs to a status this caller was shown. The
        // nonce is spent by the coordinator once the request is accepted, so a rejection further
        // down (rate limit) does not cost the caller its shown warning.
        if (nonces != null && NeedsShownWarning(info, otherSessions) && !nonces.IsValid(caller.Sid, request.ConfirmationNonce))
        {
            return PolicyDecision.Reject(ResultCode.ConfirmationRequired, "The confirmation does not belong to a status shown to you. Ask for the status, show its warning, then confirm.");
        }
        if (info.CountsTowardRateLimit && !rateLimiter.TryAcquire(caller.Sid, config.UserRateLimitPerHour))
        {
            return PolicyDecision.Reject(ResultCode.RateLimited, "Too many requests in the last hour. Try again later.");
        }
        // Support packages: no limit on a single-user device; on a shared device (a multi-session
        // host, or other sessions present or uncountable) a limit of their own per user, so one
        // user cannot keep the single worker busy for everyone.
        if (info.ProducesFile && !info.CountsTowardRateLimit && packageLimiter != null && (multiSessionHost || otherSessions != 0)
            && !packageLimiter.TryAcquire(caller.Sid, PackageRateLimitPerHour))
        {
            return PolicyDecision.Reject(ResultCode.RateLimited, "Support packages were created too often on this shared device in the last hour. Try again later.");
        }
        if (info.ReadOnly && readOnlyLimiter != null && !readOnlyLimiter.TryAcquire(caller.Sid, ReadOnlyRateLimitPerHour))
        {
            // The read-only actions share this limit; the message names the one asked for.
            return PolicyDecision.Reject(ResultCode.RateLimited, request.Action switch
            {
                ToolbeltAction.CheckWindowsUpdate => "Check Windows Update and the app diagnosis were run too often in the last hour. Try again later.",
                ToolbeltAction.CheckDefenderBlocks => "Check Defender blocks and the other read-only checks were run too often in the last hour. Try again later.",
                _ => "The app diagnosis was run too often in the last hour. Try again later.",
            });
        }
        return PolicyDecision.Accept();
    }

    /// <summary>The request carries a confirmation the user had to see a warning for.</summary>
    public static bool NeedsShownWarning(ActionInfo info, int otherSessions) =>
        info.RequiresConfirmation || (info.AffectsAllSessions && otherSessions > 0);
}
