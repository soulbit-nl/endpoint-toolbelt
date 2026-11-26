using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.Logging;
using EndpointToolbelt.Platform;
using EndpointToolbelt.Service.Platform;
using EndpointToolbelt.Service.Rules;
using EndpointToolbelt.Service.State;

namespace EndpointToolbelt.Service.Pipe;

/// <summary>Dispatches one request per connection.</summary>
public sealed class RequestHandler : IRequestHandler
{
    private const int StatusRequestsPerMinute = 60;
    private const int LastResultRequestsPerMinute = 20;
    private const int ClientEventsPerHour = 30;
    private const int MaxClientEventText = 500;

    private readonly ActionCoordinator _coordinator;
    private readonly ISessionCounter _sessions;
    private readonly DeviceStateReader _deviceState;
    private readonly Func<ServiceConfig> _config;
    private readonly IEventWriter _events;
    private readonly string _serviceVersion;
    private readonly ImeCheckInReader _imeCheckIn;
    private readonly DeviceReadoutReader _readouts;
    private readonly RateLimiter _statusLimiter = new(TimeSpan.FromMinutes(1));
    private readonly RateLimiter _lastResultLimiter = new(TimeSpan.FromMinutes(1));
    private readonly RateLimiter _eventLimiter = new(TimeSpan.FromHours(1));

    public RequestHandler(ActionCoordinator coordinator, ISessionCounter sessions, DeviceStateReader deviceState,
        Func<ServiceConfig> config, IEventWriter events, string serviceVersion, ImeCheckInReader imeCheckIn = null,
        DeviceReadoutReader readouts = null)
    {
        _imeCheckIn = imeCheckIn;
        _readouts = readouts;
        _coordinator = coordinator;
        _sessions = sessions;
        _deviceState = deviceState;
        _config = config;
        _events = events;
        _serviceVersion = serviceVersion;
    }

    public void Handle(Request request, CallerContext caller, PipeConnection connection)
    {
        if (request.ProtocolVersion != ProtocolInfo.Version)
        {
            connection.Send(new Response
            {
                Type = ResponseType.Rejected,
                Result = ResultCode.ProtocolMismatch,
                Message = $"The app and the service use different protocol versions (service: {ProtocolInfo.Version}).",
            });
            return;
        }

        switch (request.Type)
        {
            case RequestType.GetStatus:
                if (!_statusLimiter.TryAcquire(caller.Sid, StatusRequestsPerMinute))
                {
                    connection.Send(new Response { Type = ResponseType.Rejected, Result = ResultCode.RateLimited, Message = "Too many status requests." });
                    return;
                }
                connection.Send(new Response { Type = ResponseType.Status, Status = BuildStatus(caller) });
                return;

            case RequestType.RunAction:
                _coordinator.RunAction(request, caller, _sessions.CountOtherUserSessions(caller.SessionId), connection);
                return;

            case RequestType.GetLastResult:
                if (!_lastResultLimiter.TryAcquire(caller.Sid, LastResultRequestsPerMinute))
                {
                    connection.Send(new Response { Type = ResponseType.Rejected, Result = ResultCode.RateLimited, Message = "Too many requests." });
                    return;
                }
                SendLastResult(caller, connection);
                return;

            case RequestType.ReportClientEvent:
                ReportClientEvent(request, caller, connection);
                return;

            case RequestType.Watch:
                connection.Send(new Response { Type = ResponseType.Ack });
                // Returns when the client closes its end or the service closes the connection.
                connection.WaitForClientClose();
                return;

            default:
                connection.Send(new Response { Type = ResponseType.Rejected, Result = ResultCode.InvalidRequest, Message = "Unknown request." });
                return;
        }
    }

    internal StatusInfo BuildStatus(CallerContext caller)
    {
        var config = _config();
        var (running, since) = _coordinator.GetRunning();
        var (restartSince, restartBy) = _deviceState.GetRestartRequired();
        bool? enrolled;
        try { enrolled = _deviceState.IsEnrolled(); } catch (Exception) { enrolled = null; }
        // The EntDMID only with exactly one enrollment, as the enrollment row says.
        var enrollmentId = enrolled == true ? _deviceState.GetMdmEnrollmentId() : null;
        var lastSync = _deviceState.GetLastSync();
        var hasLast = _coordinator.HasLastResult(caller.Sid);
        LastResultInfo last = null;
        if (hasLast)
        {
            try { last = _coordinator.GetLastResult(caller.Sid); } catch (Exception) { last = null; }
        }
        ImeCheckIn checkIn;
        try { checkIn = _imeCheckIn?.Get() ?? new ImeCheckIn { Unavailable = "unreadable" }; }
        catch (Exception) { checkIn = new ImeCheckIn { Unavailable = "unreadable" }; }
        DeviceReadouts readouts;
        try { readouts = _readouts?.Get(); } catch (Exception) { readouts = null; }
        return new StatusInfo
        {
            ProtocolVersion = ProtocolInfo.Version,
            ServiceVersion = _serviceVersion,
            RunningAction = running,
            RunningSinceUtc = since?.ToString("o", CultureInfo.InvariantCulture),
            OtherSessions = _sessions.CountOtherUserSessions(caller.SessionId),
            AllowMachineWideOnMultiSession = config.AllowMachineWideOnMultiSession,
            SupportText = config.SupportText,
            SupportUrl = config.SupportUrl,
            Enrolled = enrolled,
            RestartRequiredSinceUtc = restartSince?.ToString("o", CultureInfo.InvariantCulture),
            RestartRequiredBy = restartBy,
            Cooldowns = _deviceState.GetCooldowns(config),
            HasLastResult = hasLast,
            LastSyncUtc = lastSync.Utc?.ToString("o", CultureInfo.InvariantCulture),
            LastSyncSource = lastSync.Source,
            LastResultAction = last?.Action,
            LastResultRunId = last?.RunId,
            LastResultInProgress = last?.InProgress ?? false,
            LastResultCode = last == null || last.InProgress ? null : last.Result,
            LastResultExitCode = last == null || last.InProgress ? null : last.ExitCode,
            LastImeCheckInUtc = checkIn.LastSuccessUtc?.ToString("o", CultureInfo.InvariantCulture),
            ImeCheckInErrorUtc = checkIn.LastErrorUtc?.ToString("o", CultureInfo.InvariantCulture),
            ImeCheckInError = checkIn.LastErrorUtc == null ? null : checkIn.LastError,
            ImeCheckInUnavailable = checkIn.Unavailable,
            ImeStartUtc = checkIn.StartUtc?.ToString("o", CultureInfo.InvariantCulture),
            ImeFirstCheckInDueUtc = checkIn.FirstCheckInDueUtc?.ToString("o", CultureInfo.InvariantCulture),
            MdmEnrollmentId = enrollmentId,
            // The switched-off tools, so the app hides them and a caller knows.
            DisabledTools = new System.Collections.Generic.List<string>(config.DisabledTools),
            // The device's value for the version check, which the app cannot read from the Config key.
            UpdateCheck = config.UpdateCheck,
            Readouts = readouts,
            // The confirmation of the caller's next action must return this nonce.
            ConfirmationNonce = _coordinator.Nonces.Issue(caller.Sid),
        };
    }

    /// <summary>
    /// The LastResult message carries the summary; the records follow as separate Record
    /// messages, so no single message exceeds the size limit. The package follows last.
    /// </summary>
    private void SendLastResult(CallerContext caller, PipeConnection connection)
    {
        var last = _coordinator.GetLastResult(caller.Sid);
        if (last == null)
        {
            connection.Send(new Response { Type = ResponseType.LastResult, Result = ResultCode.NoResult });
            return;
        }
        var records = last.Records;
        last.Records = new System.Collections.Generic.List<string>();
        last.RecordCount = records.Count;
        if (!connection.Send(new Response { Type = ResponseType.LastResult, Result = last.Result, LastResult = last })) { return; }
        foreach (var record in records)
        {
            if (!connection.Send(new Response { Type = ResponseType.Record, RunId = last.RunId, Action = last.Action, Record = record })) { return; }
        }
        if (!last.InProgress && last.FileName != null)
        {
            ActionCoordinator.SendFile(connection, _coordinator.GetStoredFile(caller.Sid, last.FileName), last.FileName, last.RunId, last.FileSha256);
        }
    }

    private void ReportClientEvent(Request request, CallerContext caller, PipeConnection connection)
    {
        var id = request.ClientEventId;
        var allowed = id is EndpointToolbelt.EventId.ClientHealthCounts or EndpointToolbelt.EventId.ClientPackageCreated or EndpointToolbelt.EventId.ClientActionNotStarted;
        if (!allowed || !_eventLimiter.TryAcquire(caller.Sid, ClientEventsPerHour))
        {
            connection.Send(new Response { Type = ResponseType.Rejected, Result = allowed ? ResultCode.RateLimited : ResultCode.InvalidRequest });
            return;
        }
        var text = SanitizeEventText(request.ClientEventText, MaxClientEventText);
        var type = id == EndpointToolbelt.EventId.ClientActionNotStarted ? EventLogEntryType.Warning : EventLogEntryType.Information;
        _events.Write(id, type, $"Client-reported (session {caller.SessionId}): {text}");
        connection.Send(new Response { Type = ResponseType.Ack });
    }

    /// <summary>
    /// Keeps only letters, digits, punctuation, symbols and plain spaces. Drops control and format
    /// characters (for example bidirectional overrides and line separators) and '%', which the
    /// event viewer may expand as an insertion sequence.
    /// </summary>
    internal static string SanitizeEventText(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) { return ""; }
        var sb = new StringBuilder(Math.Min(text.Length, max));
        foreach (var ch in text)
        {
            if (sb.Length >= max) { break; }
            if (ch == '%') { continue; }
            var category = char.GetUnicodeCategory(ch);
            switch (category)
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                    continue;
                case UnicodeCategory.SpaceSeparator:
                    sb.Append(' ');
                    continue;
                default:
                    sb.Append(ch);
                    continue;
            }
        }
        return sb.ToString().Trim();
    }
}
