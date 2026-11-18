using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using EndpointToolbelt.Native;
using EndpointToolbelt.Platform;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.Logging;
using EndpointToolbelt.Service.Platform;
using EndpointToolbelt.Service.Rules;

namespace EndpointToolbelt.Service.Pipe;

/// <summary>What the trusted service caller check needs from Windows. Replaced in tests.</summary>
public interface ITrustedCallerSystem
{
    /// <summary>The service's SID (NT SERVICE\name), or null when no such service is installed.</summary>
    SecurityIdentifier ServiceSid(string serviceName);

    /// <summary>The process ID of the running service, or 0.</summary>
    uint ServiceProcessId(string serviceName);

    uint ClientProcessId(NamedPipeServerStream stream);
    uint ClientSessionId(NamedPipeServerStream stream);

    /// <summary>Full path of a process's program file, or null.</summary>
    string ProcessImagePath(uint processId);

    /// <summary>Null when the folder (and every file directly in it) only administrators can change; otherwise why not.</summary>
    string ValidateProgramFolder(string folder);

    /// <summary>Sessions with a signed-in user (active or disconnected), or null when they cannot be listed.</summary>
    IReadOnlyList<int> UserSessions();

    /// <summary>The session attached to the physical console, or -1 when there is none.</summary>
    int ConsoleSessionId();

    /// <summary>The SID of the user signed in to a session, or null.</summary>
    string SessionUserSid(int sessionId);
}

/// <summary>
/// The trusted service caller: one Windows service an administrator names in the policy
/// value <c>TrustedServiceCaller</c>. While it is set, the service opens a second pipe
/// (<see cref="ProductInfo.CallerPipeName"/>) for that service only, with the same requests,
/// rules and limits as the user pipe, minus client events and Watch.
/// </summary>
public static class TrustedCaller
{
    /// <summary>
    /// SYSTEM: full control (the service runs as SYSTEM and creates the instances). The caller
    /// service's SID: read/write data, read attributes, read control, synchronize (0x120083),
    /// never create-instance. NETWORK: denied. Nobody else: no interactive users, no
    /// administrators. The process check in <see cref="VerifyClient"/> is what limits the pipe
    /// to the caller's own process; the DACL is the first gate.
    /// </summary>
    public static string Sddl(SecurityIdentifier callerServiceSid) =>
        "D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;0x120083;;;" + callerServiceSid.Value + ")";

    /// <summary>The request types the trusted caller may send.</summary>
    public static bool IsAllowed(RequestType type) =>
        type == RequestType.GetStatus || type == RequestType.RunAction || type == RequestType.GetLastResult;

    /// <summary>
    /// Null when the client process is the named service's own process, runs in session 0 and
    /// runs a program from a folder only administrators can change; otherwise why not.
    /// </summary>
    public static string VerifyClient(uint clientProcessId, uint clientSessionId, uint serviceProcessId, string imagePath, Func<string, string> validateFolder)
    {
        if (validateFolder == null) { throw new ArgumentNullException(nameof(validateFolder)); }
        if (clientSessionId != 0) { return "the client does not run in session 0"; }
        if (serviceProcessId == 0) { return "the named service is not running"; }
        if (clientProcessId != serviceProcessId) { return "the client is not the named service's process"; }
        if (string.IsNullOrEmpty(imagePath) || !Path.IsPathRooted(imagePath)) { return "the client's program file could not be determined"; }
        var folder = Path.GetDirectoryName(imagePath);
        if (string.IsNullOrEmpty(folder)) { return "the client's program folder could not be determined"; }
        var reason = validateFolder(folder);
        return reason == null ? null : "the client's program folder is not protected (" + reason + ")";
    }

    /// <summary>
    /// Whose user data a run of the trusted caller may return (the scope rule of the
    /// remote support package): the console user when that user is the only one signed in to
    /// the device; otherwise nobody (device scope only, session 0). An uncountable session list
    /// or an unreadable user also means device scope only.
    /// </summary>
    public static (int SessionId, string Sid) ChooseScope(IReadOnlyList<int> userSessions, int consoleSessionId, Func<int, string> sidOf)
    {
        if (sidOf == null) { throw new ArgumentNullException(nameof(sidOf)); }
        if (userSessions == null || userSessions.Count != 1) { return (0, null); }
        var only = userSessions[0];
        if (only <= 0 || only != consoleSessionId) { return (0, null); }
        var sid = sidOf(only);
        return string.IsNullOrEmpty(sid) ? (0, null) : (only, sid);
    }
}

/// <summary>Passes on only the request types <see cref="TrustedCaller.IsAllowed"/> allows.</summary>
public sealed class TrustedCallerHandler : IRequestHandler
{
    private readonly IRequestHandler _inner;

    public TrustedCallerHandler(IRequestHandler inner) => _inner = inner;

    public void Handle(Request request, CallerContext caller, PipeConnection connection)
    {
        if (!TrustedCaller.IsAllowed(request.Type))
        {
            connection.Send(new Response { Type = ResponseType.Rejected, Result = ResultCode.InvalidRequest, Message = "This request is not available to the trusted service caller." });
            return;
        }
        _inner.Handle(request, caller, connection);
    }
}

/// <summary>
/// Opens and closes the trusted service caller pipe to match the policy. <see cref="Refresh"/>
/// is called at start and then periodically; without the policy value no pipe exists.
/// </summary>
public sealed class TrustedCallerPipe : IDisposable
{
    private readonly IRequestHandler _handler;
    private readonly IServiceLog _log;
    private readonly IEventWriter _events;
    private readonly ITrustedCallerSystem _system;
    private readonly string _pipeName;
    private readonly Func<SecurityIdentifier, string> _sddl;
    private readonly RateLimiter _refusalLog = new(TimeSpan.FromMinutes(10));
    private readonly object _lock = new();
    private PipeServer _server;
    private string _serviceName;
    private string _lastProblem;
    private volatile bool _faulted;

    /// <param name="sddl">
    /// The pipe's DACL for the caller's SID; the production one (<see cref="TrustedCaller.Sddl"/>)
    /// unless a test, which cannot create instances as SYSTEM, passes its own.
    /// </param>
    public TrustedCallerPipe(IRequestHandler handler, IServiceLog log, IEventWriter events, ITrustedCallerSystem system = null,
        string pipeName = ProductInfo.CallerPipeName, Func<SecurityIdentifier, string> sddl = null)
    {
        _handler = handler;
        _log = log;
        _events = events;
        _system = system ?? new WindowsTrustedCallerSystem();
        _pipeName = pipeName;
        _sddl = sddl ?? TrustedCaller.Sddl;
    }

    /// <summary>The service the pipe is open for, or null when no pipe exists.</summary>
    public string OpenFor
    {
        get { lock (_lock) { return _server != null ? _serviceName : null; } }
    }

    /// <summary>Opens, keeps, replaces or closes the pipe for <paramref name="serviceName"/> (null: no pipe).</summary>
    public void Refresh(string serviceName)
    {
        lock (_lock)
        {
            if (_server != null && !_faulted && string.Equals(serviceName, _serviceName, StringComparison.OrdinalIgnoreCase)) { return; }
            if (_server != null)
            {
                CloseLocked(_faulted ? "the pipe failed" : serviceName == null ? "the policy value was removed" : "the policy names another service");
            }
            _faulted = false;
            _serviceName = serviceName;
            if (serviceName == null)
            {
                _lastProblem = null;
                return;
            }

            SecurityIdentifier sid;
            try { sid = _system.ServiceSid(serviceName); }
            catch (Exception) { sid = null; }
            if (sid == null)
            {
                ReportProblem($"The trusted service caller '{serviceName}' is not installed; the caller pipe stays closed.");
                return;
            }

            var server = new PipeServer(_pipeName, _sddl(sid), new TrustedCallerHandler(_handler), _log, stream => Identify(serviceName, sid, stream));
            server.Faulted += reason =>
            {
                // Recreated by the next refresh; the user pipe is not affected.
                _faulted = true;
                _log.Error("Trusted service caller pipe: " + reason);
            };
            try
            {
                server.Start();
            }
            catch (PipeNameInUseException ex)
            {
                server.Dispose();
                ReportProblem(ex.Message + " The trusted service caller pipe stays closed.", EndpointToolbelt.EventId.PipeNameInUse, EventLogEntryType.Error);
                return;
            }
            catch (Exception ex)
            {
                server.Dispose();
                ReportProblem("The trusted service caller pipe could not be opened: " + ex.Message);
                return;
            }
            _server = server;
            _lastProblem = null;
            var message = $"The trusted service caller pipe is open for the service '{serviceName}' (policy {ProductInfo.ServiceCallerPolicyValue}).";
            _log.Info(message);
            _events.Write(EndpointToolbelt.EventId.TrustedCallerPipe, EventLogEntryType.Information, message);
        }
    }

    public void StopAccepting()
    {
        PipeServer server;
        lock (_lock) { server = _server; }
        server?.StopAccepting();
    }

    public void CloseAll()
    {
        PipeServer server;
        lock (_lock) { server = _server; }
        server?.CloseAll();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_server != null) { CloseLocked("the service is stopping"); }
        }
    }

    private void CloseLocked(string why)
    {
        try { _server.Stop(); } catch (Exception) { }
        _server = null;
        var message = $"The trusted service caller pipe for the service '{_serviceName}' is closed: {why}.";
        _log.Info(message);
        _events.Write(EndpointToolbelt.EventId.TrustedCallerPipe, EventLogEntryType.Information, message);
    }

    private void ReportProblem(string message, int eventId = EndpointToolbelt.EventId.TrustedCallerPipe, EventLogEntryType type = EventLogEntryType.Warning)
    {
        // Reported once per problem, not at every refresh.
        if (message == _lastProblem) { return; }
        _lastProblem = message;
        _log.Warn(message);
        _events.Write(eventId, type, message);
    }

    private CallerContext Identify(string serviceName, SecurityIdentifier serviceSid, NamedPipeServerStream stream)
    {
        string reason;
        try
        {
            var clientPid = _system.ClientProcessId(stream);
            reason = TrustedCaller.VerifyClient(clientPid, _system.ClientSessionId(stream), _system.ServiceProcessId(serviceName),
                _system.ProcessImagePath(clientPid), _system.ValidateProgramFolder);
        }
        catch (Exception ex)
        {
            reason = "the client could not be checked (" + ex.GetType().Name + ")";
        }
        if (reason != null)
        {
            if (_refusalLog.TryAcquire("refused", 10))
            {
                var message = $"A client of the trusted service caller pipe was refused: {reason}.";
                _log.Warn(message);
                _events.Write(EndpointToolbelt.EventId.TrustedCallerRefused, EventLogEntryType.Warning, message);
            }
            return null;
        }

        (int SessionId, string Sid) scope;
        try { scope = TrustedCaller.ChooseScope(_system.UserSessions(), _system.ConsoleSessionId(), _system.SessionUserSid); }
        catch (Exception) { scope = (0, null); }
        return new CallerContext(serviceSid.Value, scope.SessionId, @"NT SERVICE\" + serviceName, scope.Sid, isTrustedServiceCaller: true);
    }
}

/// <summary>The real Windows calls behind <see cref="ITrustedCallerSystem"/>.</summary>
public sealed class WindowsTrustedCallerSystem : ITrustedCallerSystem
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, [Out] char[] name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

    public SecurityIdentifier ServiceSid(string serviceName)
    {
        try
        {
            return (SecurityIdentifier)new NTAccount("NT SERVICE", serviceName).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    public uint ServiceProcessId(string serviceName) => PipeNative.GetServiceProcessId(serviceName);

    public uint ClientProcessId(NamedPipeServerStream stream) => PipeNative.GetClientProcessId(stream.SafePipeHandle);

    public uint ClientSessionId(NamedPipeServerStream stream) => PipeNative.GetClientSessionId(stream.SafePipeHandle);

    public string ProcessImagePath(uint processId)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) { return null; }
        try
        {
            var size = 32768;
            var name = new char[size];
            return QueryFullProcessImageName(process, 0, name, ref size) ? new string(name, 0, size) : null;
        }
        finally { CloseHandle(process); }
    }

    public string ValidateProgramFolder(string folder) => FileSystemSafety.ValidateInstallFolder(folder);

    public IReadOnlyList<int> UserSessions() => WtsSessionCounter.ListUserSessions();

    public int ConsoleSessionId()
    {
        var id = WTSGetActiveConsoleSessionId();
        return id == 0xFFFFFFFF ? -1 : (int)id;
    }

    public string SessionUserSid(int sessionId)
    {
        // Needs SeTcbPrivilege, which the service has as LocalSystem.
        if (!WTSQueryUserToken(sessionId, out var token)) { return null; }
        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.User?.Value;
        }
        finally { CloseHandle(token); }
    }
}
