using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace EndpointToolbelt.App.Core;

/// <summary>What one step of a route showed.</summary>
public enum RouteOutcome
{
    /// <summary>Not tried for this host, while that proxy is set (bypassed, or not used by system components).</summary>
    Skipped,

    /// <summary>The TCP step: a connection to the host opened (nothing about the service yet).</summary>
    TcpConnected,

    /// <summary>TLS with a certificate valid for exactly this host, from a Microsoft chain, and an HTTP answer from the server.</summary>
    Answered,

    /// <summary>Answered, but the chain does not end in a Microsoft chain: a proxy inspects the secure connection.</summary>
    Inspected,

    /// <summary>The proxy answered itself (it refused the tunnel), or a gateway error (502, 504): the service was not reached.</summary>
    ProxyAnswered,

    /// <summary>The proxy asks for sign-in (HTTP 407).</summary>
    ProxyAuthRequired,

    /// <summary>The network's own page answered: a redirect away from Microsoft, or an HTML page where JSON was expected.</summary>
    CaptivePortal,

    /// <summary>The certificate that answered is for another name.</summary>
    CertificateWrongHost,

    /// <summary>The certificate chain does not end in a root this PC trusts.</summary>
    CertificateUntrusted,

    /// <summary>TLS with the host and an HTTP answer, but not the answer expected (the sign-in service's document).</summary>
    UnexpectedAnswer,

    NameNotResolved,
    Refused,
    TimedOut,

    /// <summary>The TLS handshake failed.</summary>
    TlsFailed,

    /// <summary>The connection failed for another reason.</summary>
    Unknown,
}

/// <summary>The three routes the check tries.</summary>
public enum RouteKind
{
    /// <summary>From this PC to the host without a proxy: the TCP step, and the HTTPS request when the user's apps go direct.</summary>
    Direct,

    /// <summary>The signed-in user's WinINet proxy, the one the user's apps use.</summary>
    UserProxy,

    /// <summary>The machine's WinHTTP proxy, which system components running as SYSTEM use.</summary>
    MachineProxy,
}

/// <summary>The two steps of a target: a TCP connection first (so "no route at all" stays visible), then one HTTPS request.</summary>
public enum RouteStep
{
    Tcp,
    Https,
}

/// <summary>What one step returned: its outcome and, for an HTTP answer, the status code.</summary>
public sealed class ProbeAnswer
{
    public ProbeAnswer(RouteOutcome outcome, int? httpStatus = null)
    {
        Outcome = outcome;
        HttpStatus = httpStatus;
    }

    public RouteOutcome Outcome { get; }
    public int? HttpStatus { get; }

    /// <summary>The organization of an inspecting proxy's certificate authority (IT-facing text only).</summary>
    public string IssuerOrganization { get; set; } = "";

    /// <summary>The host a captive portal's redirect points to (a host name only).</summary>
    public string RedirectHost { get; set; } = "";

    public static readonly ProbeAnswer Connected = new(RouteOutcome.TcpConnected);

    public static ProbeAnswer Failed(RouteOutcome outcome) => new(outcome);
}

/// <summary>A direct TCP connection with the reason it failed; the real one is <see cref="SystemHealthSource"/>.</summary>
public interface IDirectRoute
{
    ProbeAnswer Connect(string host, int port, int timeoutMs);
}

/// <summary>The rules that turn what a step saw into words and slugs, without system calls (testable).</summary>
public static class RouteRules
{
    /// <summary>The service answered on this route (also through an inspecting proxy).</summary>
    public static bool Answered(RouteOutcome outcome) => outcome is RouteOutcome.Answered or RouteOutcome.Inspected;

    /// <summary>A proxy answered on the route, but the service was not reached through it.</summary>
    public static bool ProxyOnly(RouteOutcome outcome) => outcome is RouteOutcome.ProxyAnswered or RouteOutcome.ProxyAuthRequired;

    /// <summary>The secure connection itself failed: TLS, a certificate for another name, or an untrusted chain.</summary>
    public static bool SecureFailure(RouteOutcome outcome) => outcome is RouteOutcome.TlsFailed or RouteOutcome.CertificateWrongHost or RouteOutcome.CertificateUntrusted;

    public static RouteOutcome FromSocketError(SocketError error) => error switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain or SocketError.NoRecovery => RouteOutcome.NameNotResolved,
        SocketError.ConnectionRefused => RouteOutcome.Refused,
        SocketError.TimedOut => RouteOutcome.TimedOut,
        _ => RouteOutcome.Unknown,
    };

    public static RouteOutcome FromWebException(WebException ex)
    {
        switch (ex.Status)
        {
            case WebExceptionStatus.NameResolutionFailure:
            case WebExceptionStatus.ProxyNameResolutionFailure:
                return RouteOutcome.NameNotResolved;
            case WebExceptionStatus.Timeout:
                return RouteOutcome.TimedOut;
            case WebExceptionStatus.TrustFailure:
            case WebExceptionStatus.SecureChannelFailure:
                return RouteOutcome.TlsFailed;
            default:
                return ex.InnerException is SocketException socket ? FromSocketError(socket.SocketErrorCode) : RouteOutcome.Unknown;
        }
    }

    /// <summary>The outcome in the run log: "answered", "proxy-auth" ...</summary>
    public static string Slug(RouteOutcome outcome) => outcome switch
    {
        RouteOutcome.Skipped => "skipped",
        RouteOutcome.TcpConnected => "tcp-connected",
        RouteOutcome.Answered => "answered",
        RouteOutcome.Inspected => "inspected",
        RouteOutcome.ProxyAnswered => "proxy-answered",
        RouteOutcome.ProxyAuthRequired => "proxy-auth",
        RouteOutcome.CaptivePortal => "captive-portal",
        RouteOutcome.CertificateWrongHost => "cert-wrong-host",
        RouteOutcome.CertificateUntrusted => "cert-untrusted",
        RouteOutcome.UnexpectedAnswer => "unexpected-answer",
        RouteOutcome.NameNotResolved => "dns-failed",
        RouteOutcome.Refused => "refused",
        RouteOutcome.TimedOut => "timeout",
        RouteOutcome.TlsFailed => "tls-failed",
        _ => "unknown",
    };

    public static string RouteSlug(RouteKind route) => route switch
    {
        RouteKind.Direct => "direct",
        RouteKind.UserProxy => "user-proxy",
        _ => "machine-proxy",
    };
}

/// <summary>How much a target matters.</summary>
public enum TargetRole
{
    /// <summary>Device management needs it: unreachable is a problem.</summary>
    Core,

    /// <summary>Download optimization: never makes the result a problem on its own.</summary>
    Optional,

    /// <summary>A service the user uses, apart from device management (Outlook on the web).</summary>
    Separate,
}

/// <summary>One Microsoft connection the check tries, with its plain-language purpose.</summary>
public sealed class ConnectionTarget
{
    internal ConnectionTarget(string key, string purpose, string host, TargetRole role, bool machineRoute, string path = "/")
    {
        Key = key;
        Purpose = purpose;
        Host = host;
        Role = role;
        UsesMachineRoute = machineRoute;
        Path = path;
    }

    /// <summary>A short ID: "management", "sign-in" ...</summary>
    public string Key { get; }

    /// <summary>What it is for, as the window says it: "Device management connection".</summary>
    public string Purpose { get; }
    public string Host { get; }
    public TargetRole Role { get; }

    /// <summary>System components reach it through the machine proxy, so that route is tested too.</summary>
    public bool UsesMachineRoute { get; }

    /// <summary>What the HTTPS request asks for: "/" (a HEAD), or the sign-in service's OpenID document (a GET).</summary>
    public string Path { get; }

    /// <summary>The answer counts only when it is the OpenID document with Microsoft's issuer.</summary>
    public bool ExpectsOpenId => Path != "/";

    public const int Port = 443;
}

/// <summary>What one step of one route showed, with when and how long.</summary>
public sealed class RouteResult
{
    public RouteKind Route { get; set; }
    public RouteStep Step { get; set; } = RouteStep.Https;
    public RouteOutcome Outcome { get; set; }
    public int? HttpStatus { get; set; }

    /// <summary>The proxy as host and port, for a proxy route; empty otherwise.</summary>
    public string Proxy { get; set; } = "";

    /// <summary>Why a proxy route was not tried while that proxy is set: "bypassed", "not-used".</summary>
    public string SkipReason { get; set; } = "";

    /// <summary>The inspecting proxy's certificate authority (IT-facing text only).</summary>
    public string IssuerOrganization { get; set; } = "";

    /// <summary>The host a captive portal's redirect points to.</summary>
    public string RedirectHost { get; set; } = "";

    public DateTime StartedUtc { get; set; }
    public int DurationMs { get; set; }
    public int TimeoutMs { get; set; }
}

/// <summary>The one answer per target.</summary>
public enum TargetAnswer
{
    /// <summary>The service answered over a secure connection valid for its name.</summary>
    Answered,

    /// <summary>The user's route was answered; the machine proxy, which system components use, was not.</summary>
    AnsweredForUserOnly,

    /// <summary>Answered, through a proxy that inspects the secure connection.</summary>
    Inspected,

    /// <summary>The network's own sign-in page answered instead.</summary>
    CaptivePortal,

    /// <summary>The secure connection failed (TLS, a certificate for another name, an untrusted chain).</summary>
    SecureConnectionFailed,

    /// <summary>A route exists, but the service's answer was not confirmed (a proxy answered, a time-out after the connection, an unexpected answer).</summary>
    NotConfirmed,

    /// <summary>No route reached it.</summary>
    NotReachable,
}

/// <summary>One target with what each step showed.</summary>
public sealed class TargetResult
{
    public ConnectionTarget Target { get; set; }
    public List<RouteResult> Routes { get; } = new();

    private IEnumerable<RouteResult> Tried => Routes.Where(r => r.Outcome != RouteOutcome.Skipped);

    /// <summary>The TCP step; null when it was not taken.</summary>
    public RouteResult Tcp => Tried.FirstOrDefault(r => r.Step == RouteStep.Tcp);

    /// <summary>The HTTPS request on the user's route (direct or the user's proxy); null when none was made.</summary>
    public RouteResult User => Tried.FirstOrDefault(r => r.Step == RouteStep.Https && r.Route != RouteKind.MachineProxy);

    /// <summary>The HTTPS request through the machine proxy; null when it was not tried.</summary>
    public RouteResult Machine => Tried.FirstOrDefault(r => r.Step == RouteStep.Https && r.Route == RouteKind.MachineProxy);

    public TargetAnswer Answer
    {
        get
        {
            var tried = Tried.ToList();
            if (tried.Any(r => r.Outcome == RouteOutcome.CaptivePortal)) { return TargetAnswer.CaptivePortal; }
            var answered = tried.Where(r => RouteRules.Answered(r.Outcome)).ToList();
            if (answered.Count > 0)
            {
                var user = User;
                var machine = Machine;
                if (user != null && RouteRules.Answered(user.Outcome) && machine != null && !RouteRules.Answered(machine.Outcome)) { return TargetAnswer.AnsweredForUserOnly; }
                return answered.Any(r => r.Outcome == RouteOutcome.Inspected) ? TargetAnswer.Inspected : TargetAnswer.Answered;
            }
            if (tried.Any(r => RouteRules.SecureFailure(r.Outcome))) { return TargetAnswer.SecureConnectionFailed; }
            if (tried.Any(r => RouteRules.ProxyOnly(r.Outcome) || r.Outcome == RouteOutcome.UnexpectedAnswer) || Tcp?.Outcome == RouteOutcome.TcpConnected)
            {
                return TargetAnswer.NotConfirmed;
            }
            return TargetAnswer.NotReachable;
        }
    }

    /// <summary>The step that best explains the answer.</summary>
    public RouteResult Best
        => Tried.FirstOrDefault(r => r.Outcome == RouteOutcome.CaptivePortal)
            ?? Tried.FirstOrDefault(r => RouteRules.Answered(r.Outcome))
            ?? Tried.FirstOrDefault(r => RouteRules.SecureFailure(r.Outcome))
            ?? Tried.FirstOrDefault(r => RouteRules.ProxyOnly(r.Outcome) || r.Outcome == RouteOutcome.UnexpectedAnswer)
            ?? User
            ?? Tried.FirstOrDefault()
            ?? Routes.FirstOrDefault();
}

/// <summary>The whole check's verdict, worst first.</summary>
public enum ConnectionVerdict
{
    AllAnswered,
    Offline,
    CaptivePortal,
    NotReachable,
    SecureConnectionFailed,
    NotConfirmed,
    DeviceRouteNotConfirmed,
    Inspected,
    OutlookNotAnswered,
    DownloadOptimizationNotReachable,
}

/// <summary>Earlier contact with Intune the app already knows from the status: a separate observation, never part of the check.</summary>
public sealed class ManagementContact
{
    public DateTime? LastSyncUtc { get; set; }
    public string LastSyncSource { get; set; } = "";
    public DateTime? LastImeCheckInUtc { get; set; }

    public bool Known => LastSyncUtc.HasValue || LastImeCheckInUtc.HasValue;

    public static ManagementContact From(Protocol.StatusInfo status) => status == null ? new ManagementContact() : new ManagementContact
    {
        LastSyncUtc = TimeText.ParseUtc(status.LastSyncUtc),
        LastSyncSource = string.IsNullOrWhiteSpace(status.LastSyncSource) || status.LastSyncSource == "unreadable" ? "MDM session log" : status.LastSyncSource,
        LastImeCheckInUtc = TimeText.ParseUtc(status.LastImeCheckInUtc),
    };
}

public sealed class ConnectivityResult
{
    public string RunId { get; set; } = "";
    public List<TargetResult> Targets { get; } = new();

    /// <summary>Whether the user's apps and system components have a proxy (for the PROXY line).</summary>
    public bool UserProxySet { get; set; }
    public bool MachineProxySet { get; set; }

    /// <summary>The run log lines: the PROXY line, one per step tried, one per target and the verdict.</summary>
    public List<string> Lines { get; } = new();

    /// <summary>Targets device management needs that no route reached (the history's number, as in older versions).</summary>
    public int RequiredFailed => Targets.Count(t => t.Target.Role == TargetRole.Core && t.Answer == TargetAnswer.NotReachable);

    public ConnectionVerdict Verdict => ConnectivityTest.VerdictOf(Targets);

    /// <summary>The certificate authorities of inspecting proxies seen, for Copy details for IT and the log only.</summary>
    public IReadOnlyList<string> InspectedBy => Targets.SelectMany(t => t.Routes).Where(r => r.Outcome == RouteOutcome.Inspected && r.IssuerOrganization.Length > 0)
        .Select(r => r.IssuerOrganization).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>
/// "Check Microsoft connections": each Microsoft connection the device and the user
/// need, on port 443. Per target a TCP connection first (so "no route at all" stays visible), then
/// one unauthenticated HTTPS request the way the user's apps make it (directly or through the user's
/// proxy) and, for the connections system components make, through the machine proxy. "Answered"
/// means a TLS handshake with a certificate valid for exactly that host and any HTTP answer from the
/// server; for sign-in, its OpenID document. Nothing here says that Microsoft is down.
/// </summary>
public static class ConnectivityTest
{
    public const string Name = "Check Microsoft connections";

    /// <summary>The check's earlier name: older run logs carry it.</summary>
    public const string EarlierName = "Connectivity test";

    public const string HistoryName = "ConnTest";

    /// <summary>The sign-in service's public OpenID document.</summary>
    public const string OpenIdPath = "/common/v2.0/.well-known/openid-configuration";

    public static readonly IReadOnlyList<ConnectionTarget> Targets = new[]
    {
        new ConnectionTarget("management", "Device management connection", "manage.microsoft.com", TargetRole.Core, true),
        new ConnectionTarget("sign-in", "Work account sign-in connection", "login.microsoftonline.com", TargetRole.Core, true, OpenIdPath),
        new ConnectionTarget("registration", "Device registration connection", "enterpriseregistration.windows.net", TargetRole.Core, true),
        // The bare do.dsp.mp.microsoft.com apex does not listen, so a real Delivery Optimization host is probed.
        new ConnectionTarget("download-optimization", "App and update download optimization", "geo.prod.do.dsp.mp.microsoft.com", TargetRole.Optional, true),
        new ConnectionTarget("outlook-web", "Outlook on the web connection", "outlook.office.com", TargetRole.Separate, false),
    };

    public static ConnectionTarget Management => Targets[0];

    public const int DirectTimeoutMs = 4000;
    public const int HttpsTimeoutMs = 6000;
    public const int QuickDirectTimeoutMs = 3000;
    public const int QuickHttpsTimeoutMs = 4000;

    /// <summary>A direct check from a yes/no TCP test (a source without <see cref="IDirectRoute"/>).</summary>
    public static Func<string, int, int, ProbeAnswer> FromBool(Func<string, int, int, bool> canReach)
        => (host, port, timeout) => canReach(host, port, timeout) ? ProbeAnswer.Connected : ProbeAnswer.Failed(RouteOutcome.Unknown);

    /// <summary>
    /// Runs the check: the targets side by side, the steps of one target one after another; the log
    /// lines in the fixed order of the targets.
    /// </summary>
    public static ConnectivityResult Run(Func<string, int, int, ProbeAnswer> connect, IProxyRoutes routes, string runId, Func<DateTime> utcNow = null, Action<string> progress = null)
    {
        var now = utcNow ?? (() => DateTime.UtcNow);
        var result = new ConnectivityResult { RunId = runId ?? "" };
        var machine = MachineOf(routes);
        var work = Targets.Select(target => Task.Run(() => Check(target, connect, routes, machine, now, quick: false))).ToArray();
        Task.WaitAll(work);
        foreach (var task in work) { result.Targets.Add(task.Result); }
        result.MachineProxySet = machine != null;
        result.UserProxySet = result.Targets.Any(t => t.Routes.Any(r => r.Route == RouteKind.UserProxy && r.Outcome != RouteOutcome.Skipped));
        // The proxies in one line; skipped steps are written only while that proxy is set.
        result.Lines.Add(string.Format(CultureInfo.InvariantCulture, "PROXY run={0} user={1} machine={2}", Id(result.RunId), result.UserProxySet ? "set" : "none", result.MachineProxySet ? "set" : "none"));
        foreach (var target in result.Targets)
        {
            foreach (var route in target.Routes)
            {
                if (route.Outcome == RouteOutcome.Skipped && !(route.Route == RouteKind.MachineProxy ? result.MachineProxySet : result.UserProxySet)) { continue; }
                result.Lines.Add(RouteLine(result.RunId, target.Target, route));
            }
            result.Lines.Add(TargetLine(result.RunId, target));
        }
        result.Lines.Add(string.Format(CultureInfo.InvariantCulture, "RESULT run={0} verdict={1} required-not-reachable={2}", Id(result.RunId), VerdictSlug(result.Verdict), result.RequiredFailed));
        foreach (var line in result.Lines) { progress?.Invoke(line); }
        return result;
    }

    /// <summary>
    /// Home's quick check: only the device management connection, the TCP step (3 seconds),
    /// the HTTPS request the user's apps would make, and the machine proxy when there is one (4
    /// seconds each); the same answers as the full check.
    /// </summary>
    public static TargetResult Quick(IHealthSource source, Func<DateTime> utcNow = null)
    {
        Func<string, int, int, ProbeAnswer> connect = source is IDirectRoute direct ? direct.Connect : FromBool(source.CanReach);
        var routes = source as IProxyRoutes;
        return Check(Management, connect, routes, MachineOf(routes), utcNow ?? (() => DateTime.UtcNow), quick: true);
    }

    private static (Uri Proxy, string Bypass)? MachineOf(IProxyRoutes routes)
    {
        try { return routes?.MachineProxy(); }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException) { return null; }
    }

    private static TargetResult Check(ConnectionTarget target, Func<string, int, int, ProbeAnswer> connect, IProxyRoutes routes, (Uri Proxy, string Bypass)? machine, Func<DateTime> now, bool quick)
    {
        var result = new TargetResult { Target = target };
        var tcpTimeout = quick ? QuickDirectTimeoutMs : DirectTimeoutMs;
        var httpsTimeout = quick ? QuickHttpsTimeoutMs : HttpsTimeoutMs;
        result.Routes.Add(Probe(RouteKind.Direct, RouteStep.Tcp, "", tcpTimeout, now, () => connect(target.Host, ConnectionTarget.Port, tcpTimeout)));
        if (routes == null) { return result; }

        // The HTTPS request the user's apps would make: through the user's proxy, or directly.
        Uri browser = null;
        try { browser = routes.BrowserProxy(new Uri("https://" + target.Host + "/")); }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException) { browser = null; }
        var userRoute = browser == null ? RouteKind.Direct : RouteKind.UserProxy;
        result.Routes.Add(Probe(userRoute, RouteStep.Https, ProxyRules.Label(browser), httpsTimeout, now,
            () => Https(routes, target, browser, proxyCredentials: browser != null, httpsTimeout)));
        if (browser == null) { result.Routes.Add(Skipped(RouteKind.UserProxy, "bypassed", now)); }

        if (machine is not { } m) { return result; }
        if (!target.UsesMachineRoute) { result.Routes.Add(Skipped(RouteKind.MachineProxy, "not-used", now)); }
        else if (ProxyRules.Bypassed(target.Host, m.Bypass)) { result.Routes.Add(Skipped(RouteKind.MachineProxy, "bypassed", now)); }
        else { result.Routes.Add(Probe(RouteKind.MachineProxy, RouteStep.Https, ProxyRules.Label(m.Proxy), httpsTimeout, now, () => Https(routes, target, m.Proxy, proxyCredentials: false, httpsTimeout))); }
        return result;
    }

    private static ProbeAnswer Https(IProxyRoutes routes, ConnectionTarget target, Uri proxy, bool proxyCredentials, int timeoutMs)
    {
        var observation = routes.Https(new HttpsRequest
        {
            Host = target.Host,
            Path = target.Path,
            Get = target.ExpectsOpenId,
            Proxy = proxy,
            ProxyCredentials = proxyCredentials,
            TimeoutMs = timeoutMs,
        });
        return HttpsRules.Classify(observation, target, proxy != null);
    }

    private static RouteResult Probe(RouteKind route, RouteStep step, string proxy, int timeoutMs, Func<DateTime> now, Func<ProbeAnswer> probe)
    {
        var started = now();
        ProbeAnswer answer;
        try { answer = probe() ?? ProbeAnswer.Failed(RouteOutcome.Unknown); }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or SocketException or WebException or UriFormatException or ArgumentException)
        {
            answer = ProbeAnswer.Failed(RouteOutcome.Unknown);
        }
        var duration = (int)Math.Max(0, Math.Round((now() - started).TotalMilliseconds));
        return new RouteResult
        {
            Route = route, Step = step, Outcome = answer.Outcome, HttpStatus = answer.HttpStatus, Proxy = proxy,
            IssuerOrganization = answer.IssuerOrganization ?? "", RedirectHost = answer.RedirectHost ?? "",
            StartedUtc = started, DurationMs = duration, TimeoutMs = timeoutMs,
        };
    }

    private static RouteResult Skipped(RouteKind route, string reason, Func<DateTime> now)
        => new() { Route = route, Step = RouteStep.Https, Outcome = RouteOutcome.Skipped, SkipReason = reason, StartedUtc = now() };

    // --- The verdict and the words ---------------------------------------------------------------

    internal static ConnectionVerdict VerdictOf(IReadOnlyCollection<TargetResult> targets)
    {
        if (targets.Count == 0) { return ConnectionVerdict.NotReachable; }
        if (targets.All(t => t.Answer == TargetAnswer.NotReachable)) { return ConnectionVerdict.Offline; }
        if (targets.Any(t => t.Answer == TargetAnswer.CaptivePortal && t.Target.Role != TargetRole.Optional)) { return ConnectionVerdict.CaptivePortal; }
        var core = targets.Where(t => t.Target.Role == TargetRole.Core).ToList();
        if (core.Any(t => t.Answer == TargetAnswer.NotReachable)) { return ConnectionVerdict.NotReachable; }
        if (core.Any(t => t.Answer == TargetAnswer.SecureConnectionFailed)) { return ConnectionVerdict.SecureConnectionFailed; }
        if (core.Any(t => t.Answer == TargetAnswer.NotConfirmed)) { return ConnectionVerdict.NotConfirmed; }
        if (core.Any(t => t.Answer == TargetAnswer.AnsweredForUserOnly)) { return ConnectionVerdict.DeviceRouteNotConfirmed; }
        if (targets.Any(t => t.Answer == TargetAnswer.Inspected && t.Target.Role != TargetRole.Optional)) { return ConnectionVerdict.Inspected; }
        if (targets.Any(t => t.Target.Role == TargetRole.Separate && !IsAnswered(t.Answer))) { return ConnectionVerdict.OutlookNotAnswered; }
        if (targets.Any(t => t.Target.Role == TargetRole.Optional && t.Answer != TargetAnswer.Answered)) { return ConnectionVerdict.DownloadOptimizationNotReachable; }
        return ConnectionVerdict.AllAnswered;
    }

    private static bool IsAnswered(TargetAnswer answer) => answer is TargetAnswer.Answered or TargetAnswer.Inspected or TargetAnswer.AnsweredForUserOnly;

    /// <summary>The verdict in the run log and the history: "all-answered", "captive-portal" ...</summary>
    public static string VerdictSlug(ConnectionVerdict verdict) => verdict switch
    {
        ConnectionVerdict.AllAnswered => "all-answered",
        ConnectionVerdict.Offline => "offline",
        ConnectionVerdict.CaptivePortal => "captive-portal",
        ConnectionVerdict.NotReachable => "not-reachable",
        ConnectionVerdict.SecureConnectionFailed => "secure-connection-failed",
        ConnectionVerdict.NotConfirmed => "not-confirmed",
        ConnectionVerdict.DeviceRouteNotConfirmed => "device-route-not-confirmed",
        ConnectionVerdict.Inspected => "inspected",
        ConnectionVerdict.OutlookNotAnswered => "outlook-not-answered",
        _ => "download-optimization-not-reachable",
    };

    public static string AnswerSlug(TargetAnswer answer) => answer switch
    {
        TargetAnswer.Answered => "answered",
        TargetAnswer.AnsweredForUserOnly => "answered-for-user-only",
        TargetAnswer.Inspected => "inspected",
        TargetAnswer.CaptivePortal => "captive-portal",
        TargetAnswer.SecureConnectionFailed => "secure-connection-failed",
        TargetAnswer.NotConfirmed => "not-confirmed",
        _ => "not-reachable",
    };

    private static string Id(string runId) => string.IsNullOrEmpty(runId) ? "none" : runId;

    /// <summary>
    /// One run log line per step tried: run ID, time, duration, target, host, port, route,
    /// step, context, outcome and the HTTP status when there is one; the proxy as host and port; the
    /// certificate's verdict; an inspecting proxy's certificate authority; a captive portal's host.
    /// No URL, header, cookie, body or credential.
    /// </summary>
    internal static string RouteLine(string runId, ConnectionTarget target, RouteResult route)
    {
        var line = string.Format(CultureInfo.InvariantCulture, "ROUTE run={0} time={1:yyyy-MM-ddTHH:mm:ss.fffZ} ms={2} target=\"{3}\" host={4} port={5} route={6} step={7} context=user-app outcome={8}",
            Id(runId), route.StartedUtc, route.DurationMs, target.Purpose, target.Host, ConnectionTarget.Port, RouteRules.RouteSlug(route.Route),
            route.Step == RouteStep.Tcp ? "tcp" : "https", RouteRules.Slug(route.Outcome));
        if (route.HttpStatus.HasValue) { line += " http=" + route.HttpStatus.Value.ToString(CultureInfo.InvariantCulture); }
        if (route.Proxy.Length > 0) { line += " proxy=" + route.Proxy; }
        var cert = route.Outcome switch
        {
            RouteOutcome.Answered => "microsoft",
            RouteOutcome.Inspected => "inspected",
            RouteOutcome.CertificateWrongHost => "wrong-host",
            RouteOutcome.CertificateUntrusted => "untrusted",
            _ => "",
        };
        if (cert.Length > 0) { line += " cert=" + cert; }
        if (route.IssuerOrganization.Length > 0) { line += " issuer-org=\"" + route.IssuerOrganization + "\""; }
        if (route.RedirectHost.Length > 0) { line += " redirect-host=" + route.RedirectHost; }
        if (route.Outcome == RouteOutcome.Skipped) { line += " reason=" + route.SkipReason; }
        else if (route.Outcome == RouteOutcome.TimedOut) { line += " timeout-ms=" + route.TimeoutMs.ToString(CultureInfo.InvariantCulture); }
        return line;
    }

    internal static string TargetLine(string runId, TargetResult target)
        => string.Format(CultureInfo.InvariantCulture, "TARGET run={0} target=\"{1}\" host={2} answer={3}", Id(runId), target.Target.Purpose, target.Target.Host, AnswerSlug(target.Answer));

    /// <summary>The separate observation of earlier contact with Intune, as a log line; null when none is known.</summary>
    internal static string ContactLine(string runId, ManagementContact contact)
    {
        if (contact == null || !contact.Known) { return null; }
        var parts = new List<string> { "OBSERVATION run=" + Id(runId) };
        if (contact.LastSyncUtc is DateTime sync) { parts.Add(string.Format(CultureInfo.InvariantCulture, "last-mdm-sync={0:yyyy-MM-ddTHH:mm:ssZ} source=\"{1}\"", sync, contact.LastSyncSource)); }
        if (contact.LastImeCheckInUtc is DateTime ime) { parts.Add(string.Format(CultureInfo.InvariantCulture, "last-ime-check-in={0:yyyy-MM-ddTHH:mm:ssZ} source=\"IME log\"", ime)); }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// The line Copy details for IT adds when a proxy inspects the secure connections: the organizations
    /// of their certificate authorities (IT-facing text only); null without one.
    /// </summary>
    public static string TicketLine(ConnectivityResult result)
    {
        var by = result?.InspectedBy ?? Array.Empty<string>();
        return by.Count == 0 ? null : "Connections" + RunTicket.Separator + "TLS inspection by the certificate authority of " + string.Join(", ", by);
    }

    /// <summary>A step's name as the window says it.</summary>
    public static string RouteName(RouteResult route) => route.Step == RouteStep.Tcp ? "Connection" : route.Route switch
    {
        RouteKind.Direct => "Secure request",
        RouteKind.UserProxy => "Through your proxy",
        _ => "Through the machine proxy",
    };

    /// <summary>What one step showed, in words.</summary>
    public static string RouteWords(RouteResult route) => route.Outcome switch
    {
        RouteOutcome.Skipped => "not tried: " + SkipWords(route),
        RouteOutcome.TcpConnected => "a connection opened",
        RouteOutcome.Answered => "the service answered (HTTP " + Status(route) + ")",
        RouteOutcome.Inspected => "the service answered (HTTP " + Status(route) + "), through a proxy that inspects the secure connection",
        RouteOutcome.ProxyAnswered => route.HttpStatus is 502 or 504
            ? "the proxy answered HTTP " + Status(route) + ": it could not reach the service"
            : route.HttpStatus == null
                ? "the proxy answered itself, so the service was not reached through it"
                : "the proxy answered itself (HTTP " + Status(route) + "), so the service was not reached through it",
        RouteOutcome.ProxyAuthRequired => "the proxy asks for sign-in (HTTP 407)",
        RouteOutcome.CaptivePortal => route.RedirectHost.Length > 0
            ? "the network answered with its own page (a redirect to " + route.RedirectHost + ")"
            : "the network answered with its own page instead of the service",
        RouteOutcome.CertificateWrongHost => "the certificate that answered is for another name",
        RouteOutcome.CertificateUntrusted => "the certificate chain is not trusted on this PC",
        RouteOutcome.UnexpectedAnswer => "an unexpected answer (HTTP " + Status(route) + "), not the sign-in service's document",
        RouteOutcome.NameNotResolved => route.Route == RouteKind.Direct ? "the name could not be resolved" : "the name of the proxy or the service could not be resolved",
        RouteOutcome.Refused => "the connection was refused",
        RouteOutcome.TimedOut => "no answer within " + Words.Count(Math.Max(1, (route.TimeoutMs + 500) / 1000), "second"),
        RouteOutcome.TlsFailed => "the secure connection (TLS) failed",
        _ => "the connection failed",
    };

    private static string Status(RouteResult route) => route.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "?";

    private static string SkipWords(RouteResult route) => route.SkipReason switch
    {
        "not-used" => "system components do not use it for this connection",
        _ => route.Route == RouteKind.MachineProxy ? "the machine proxy is bypassed for this address" : "your proxy is bypassed for this address",
    };

    /// <summary>The purpose as part of a sentence: "the device management connection".</summary>
    private static string Lower(ConnectionTarget target) => "the " + char.ToLowerInvariant(target.Purpose[0]) + target.Purpose.Substring(1);

    /// <summary>
    /// The status line, its tone and one card per target in a fixed order. The card, Copy for
    /// IT, Copy summary and the run log all carry the same answer per target.
    /// </summary>
    public static (string Status, Tone Tone, List<ResultCard> Cards) Present(ConnectivityResult result, ManagementContact contact = null, TimeZoneInfo zone = null)
    {
        var cards = result.Targets.Select(t => Card(t, t.Target.Key == "management" ? contact : null, zone)).ToList();
        var (status, tone) = Status(result);
        return (status, tone, cards);
    }

    internal static (string Status, Tone Tone) Status(ConnectivityResult result)
    {
        var targets = result.Targets;
        var core = targets.Where(t => t.Target.Role == TargetRole.Core).ToList();
        switch (result.Verdict)
        {
            case ConnectionVerdict.AllAnswered:
                return ("The " + Words.Count(targets.Count, "Microsoft service") + " answered.", Tone.Success);
            case ConnectionVerdict.Offline:
                return ("This PC couldn't reach any Microsoft connection. Check the network connection, Wi-Fi or VPN.", Tone.Error);
            case ConnectionVerdict.CaptivePortal:
                return ("This network shows its own sign-in page instead of the Microsoft services. Sign in to the network in a browser, then check again.", Tone.Error);
            case ConnectionVerdict.NotReachable:
                return ("This PC couldn't reach " + Words.List(core.Where(t => t.Answer == TargetAnswer.NotReachable).Select(t => Lower(t.Target)).ToList()) + ". Check the network, VPN or proxy.", Tone.Error);
            case ConnectionVerdict.SecureConnectionFailed:
                return ("The secure connection to " + Words.List(core.Where(t => t.Answer == TargetAnswer.SecureConnectionFailed).Select(t => Lower(t.Target)).ToList()) + " failed. Copy details for IT.", Tone.Error);
            case ConnectionVerdict.NotConfirmed:
            {
                var unsure = core.Where(t => t.Answer == TargetAnswer.NotConfirmed).ToList();
                var best = unsure.Select(t => t.Best?.Outcome).ToList();
                var why = best.Contains(RouteOutcome.ProxyAuthRequired) ? ". The proxy asks for sign-in."
                    : best.Any(o => o is RouteOutcome.ProxyAnswered) ? ". The proxy answered instead of the service."
                    : best.Contains(RouteOutcome.UnexpectedAnswer) ? ". The service gave an unexpected answer."
                    : ". A connection opened, but the service did not answer.";
                return ("Not confirmed: " + Words.List(unsure.Select(t => Lower(t.Target)).ToList()) + why, Tone.Warning);
            }
            case ConnectionVerdict.DeviceRouteNotConfirmed:
            {
                var auth = core.Any(t => t.Answer == TargetAnswer.AnsweredForUserOnly && t.Machine?.Outcome == RouteOutcome.ProxyAuthRequired);
                return ("Your route works; the device's own route (machine proxy) is not confirmed: " + (auth ? "the proxy asks for sign-in." : "it did not reach the services."), Tone.Warning);
            }
            case ConnectionVerdict.Inspected:
                return ("The Microsoft services answered, but a proxy on this network looks inside the secure connections. Copy details for IT.", Tone.Warning);
            case ConnectionVerdict.OutlookNotAnswered:
                return ("The device management services answered; Outlook on the web did not.", Tone.Warning);
            default:
                return ("This PC couldn't reach the download optimization service. The app download itself wasn't tested.", Tone.Warning);
        }
    }

    /// <summary>One target's card: its answer as the title, each step as evidence, and one next step.</summary>
    internal static ResultCard Card(TargetResult target, ManagementContact contact = null, TimeZoneInfo zone = null)
    {
        var t = target.Target;
        var lines = new List<string>();
        var optional = t.Role == TargetRole.Optional;
        if (optional && target.Answer != TargetAnswer.Answered)
        {
            lines.Add("This PC couldn't reach the download optimization service. The app download itself wasn't tested. Device management is not affected.");
        }
        lines.Add(t.Host + ", port " + ConnectionTarget.Port.ToString(CultureInfo.InvariantCulture) + ".");
        foreach (var route in target.Routes.Where(r => r.Outcome != RouteOutcome.Skipped))
        {
            var proxy = route.Proxy.Length > 0 ? " (" + route.Proxy + ")" : "";
            lines.Add(RouteName(route) + proxy + ": " + RouteWords(route) + ".");
        }
        if (t.Role == TargetRole.Separate)
        {
            lines.Add("Only the web service's answer is measured. Signing in, opening the mailbox and sending mail are separate steps and were not tested.");
        }
        if (contact != null && contact.Known)
        {
            var seen = new List<string>();
            if (contact.LastSyncUtc is DateTime sync) { seen.Add("last MDM sync " + TimeText.InLocalTime(sync, zone: zone) + " (" + contact.LastSyncSource + ")"); }
            if (contact.LastImeCheckInUtc is DateTime ime) { seen.Add("last IME check-in " + TimeText.InLocalTime(ime, zone: zone) + " (IME log)"); }
            lines.Add("Separate observation, not part of this check: " + string.Join("; ", seen) + ".");
        }
        var detail = string.Join(Environment.NewLine, lines);
        var best = target.Best;
        var problem = t.Role switch { TargetRole.Core => FindingSeverity.Error, TargetRole.Separate => FindingSeverity.Warning, _ => FindingSeverity.Info };
        var check = optional ? FindingSeverity.Info : FindingSeverity.Warning;
        const string NothingForManagement = "Nothing to do for device management. If app or update downloads are slow, tell your IT support.";
        switch (target.Answer)
        {
            case TargetAnswer.Answered:
                return ResultCard.From(FindingSeverity.Ok, t.Purpose + (t.Role == TargetRole.Separate ? ": answered (signing in was not tested)" : ": answered"), detail);
            case TargetAnswer.AnsweredForUserOnly:
            {
                var machine = target.Machine;
                var auth = machine?.Outcome == RouteOutcome.ProxyAuthRequired;
                return ResultCard.From(check, t.Purpose + ": answered for you, not confirmed for the device", detail,
                    auth
                        ? "Contact your IT support: system components on this PC use the machine proxy, and it asks for a sign-in they cannot give."
                        : "Contact your IT support: system components on this PC use the machine proxy, and it did not reach this service.",
                    auth
                        ? "Allow " + t.Host + " through the machine (WinHTTP) proxy without sign-in, or bypass the proxy for it."
                        : "Check the machine (WinHTTP) proxy for " + t.Host + ": " + RouteWords(machine) + ".");
            }
            case TargetAnswer.Inspected:
                return ResultCard.From(check, t.Purpose + ": answered through an inspecting proxy", detail,
                    optional ? NothingForManagement : "Contact your IT support: a proxy on this network looks inside the secure connection to this service.",
                    "Exclude " + t.Host + " from TLS inspection on the proxy; Microsoft does not support inspecting this traffic. Copy details for IT names the inspecting certificate authority.");
            case TargetAnswer.CaptivePortal:
                return ResultCard.From(problem, t.Purpose + ": the network's sign-in page answered instead", detail,
                    optional ? NothingForManagement : "Open a web page in your browser and sign in to the network (as in a hotel or on public Wi-Fi), then check again.",
                    t.Host + " on port 443: " + RouteWords(best) + ".");
            case TargetAnswer.SecureConnectionFailed:
            {
                var (user, support) = best?.Outcome switch
                {
                    RouteOutcome.CertificateWrongHost => (
                        "If you are on a hotel or public Wi-Fi network, sign in to it in a browser first. Otherwise contact your IT support.",
                        "The certificate that answered for " + t.Host + " is for another name: something on the network answers in the service's place."),
                    RouteOutcome.CertificateUntrusted => (
                        "Contact your IT support.",
                        "The certificate chain for " + t.Host + " does not end in a root this PC trusts: a proxy may inspect TLS with a certificate authority that is not deployed to this PC."),
                    _ => (
                        "Check that this PC's date and time are right, then check again. If it stays, contact your IT support.",
                        "The TLS handshake with " + t.Host + " failed (" + RouteName(best).ToLowerInvariant() + "). Check the proxy, firewall or security software that may interrupt it."),
                };
                return ResultCard.From(problem, t.Purpose + ": the secure connection failed", detail, optional ? NothingForManagement : user, support);
            }
            case TargetAnswer.NotConfirmed:
            {
                var auth = best?.Outcome == RouteOutcome.ProxyAuthRequired;
                var timedOut = best?.Outcome is RouteOutcome.TimedOut or RouteOutcome.Unknown or RouteOutcome.Refused or RouteOutcome.NameNotResolved;
                var unexpected = best?.Outcome == RouteOutcome.UnexpectedAnswer;
                var user = optional ? NothingForManagement
                    : auth ? "Contact your IT support: the proxy asks for a sign-in this check could not give."
                    : timedOut ? "Check again in a few minutes. If it stays, contact your IT support."
                    : unexpected ? "Contact your IT support."
                    : "Contact your IT support: the proxy may block this connection.";
                var support = best == null ? "" : t.Host + " on port 443, " + RouteName(best).ToLowerInvariant() + ": " + RouteWords(best) + "."
                    + (timedOut ? " A connection opened, so the route exists; the HTTPS answer did not come." : auth || best.Outcome == RouteOutcome.ProxyAnswered ? " Check the proxy rules for it." : "");
                return ResultCard.From(check, t.Purpose + ": not confirmed", detail, user, support);
            }
            default:
            {
                var next = optional
                    ? NothingForManagement
                    : "Check that this PC is connected to the network and, if your organization uses one, to the VPN. Then check again.";
                var support = optional
                    ? "If your organization uses Delivery Optimization, allow " + t.Host + " on port 443."
                    : "Check the firewall, proxy and name resolution for " + t.Host + " on port 443.";
                return ResultCard.From(problem, t.Purpose + ": not reachable", detail, next, support);
            }
        }
    }

    /// <summary>
    /// Home's row for the device management connection, with the same answers as the full
    /// check: answered (and by which route), through an inspecting proxy, answered for you but not for
    /// the device, a network sign-in page, a failed secure connection, not confirmed, or not reachable.
    /// </summary>
    public static (HealthStatus Status, string Detail) HomeRow(TargetResult quick)
    {
        var host = quick.Target.Host;
        var best = quick.Best;
        switch (quick.Answer)
        {
            case TargetAnswer.Answered:
                return (HealthStatus.Pass, host + " answered" + (best.Route switch { RouteKind.UserProxy => " through your proxy", RouteKind.MachineProxy => " through the machine proxy", _ => "" }) + " (HTTP " + Status(best) + ")");
            case TargetAnswer.Inspected:
                return (HealthStatus.Warn, host + " answered through a proxy that looks inside the secure connection");
            case TargetAnswer.AnsweredForUserOnly:
                return (HealthStatus.Warn, "Answered for you, not confirmed for the device: the machine proxy " + (quick.Machine?.Outcome == RouteOutcome.ProxyAuthRequired ? "asks for sign-in" : "did not reach it"));
            case TargetAnswer.CaptivePortal:
                return (HealthStatus.Fail, "The network's sign-in page answered instead. Sign in to the network in a browser");
            case TargetAnswer.SecureConnectionFailed:
                return (HealthStatus.Fail, "The secure connection failed: " + RouteWords(best));
            case TargetAnswer.NotConfirmed:
                return (HealthStatus.Warn, "Not confirmed: " + RouteWords(best));
            default:
                return (HealthStatus.Fail, "Not reachable: " + RouteWords(best) + ". Check the network or VPN");
        }
    }

    // --- The history line -------------------------------------------------------------------

    /// <summary>
    /// The history's result field: "&lt;required not reachable&gt;|&lt;run ID&gt;|&lt;verdict&gt;", so two
    /// runs with the same number are told apart and lead to their run logs. An older line
    /// holds the number alone and still reads.
    /// </summary>
    public static string HistoryResult(ConnectivityResult result)
        => result.RequiredFailed.ToString(CultureInfo.InvariantCulture) + "|" + Id(result.RunId) + "|" + VerdictSlug(result.Verdict);

    /// <summary>A verdict from the history in words (also older slugs); null for a word this version does not know.</summary>
    public static string VerdictWords(string slug) => slug switch
    {
        "all-answered" => "all services answered",
        "all-reachable" => "all connections reachable",
        "offline" => "no connection reachable",
        "captive-portal" => "the network's sign-in page answered instead",
        "not-reachable" => "a device management connection not reachable",
        "secure-connection-failed" => "the secure connection to a device management service failed",
        "not-confirmed" => "a device management connection not confirmed",
        "device-route-not-confirmed" => "answered for the user, the device's route (machine proxy) not confirmed",
        "inspected" => "answered through a proxy that inspects the secure connections",
        "outlook-not-answered" => "Outlook on the web did not answer",
        "outlook-not-reachable" => "Outlook on the web not reachable",
        "download-optimization-not-reachable" => "download optimization not reachable",
        _ => null,
    };
}
