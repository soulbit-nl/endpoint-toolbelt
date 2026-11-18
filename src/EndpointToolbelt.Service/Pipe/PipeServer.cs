using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using EndpointToolbelt.Native;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.Logging;
using EndpointToolbelt.Service.Rules;
using Microsoft.Win32.SafeHandles;

namespace EndpointToolbelt.Service.Pipe;

/// <summary>Handles one request on an accepted connection.</summary>
public interface IRequestHandler
{
    void Handle(Request request, CallerContext caller, PipeConnection connection);
}

/// <summary>
/// A connected client. Sends are serialized and bounded in time: a client that stops reading
/// is disconnected instead of blocking the service.
/// </summary>
public sealed class PipeConnection : IResponseSink
{
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private readonly NamedPipeServerStream _stream;
    private readonly object _lock = new();
    private volatile bool _gone;

    internal PipeConnection(NamedPipeServerStream stream) => _stream = stream;

    public bool IsGone => _gone;

    public bool Send(Response response) => Send(response, SendTimeout);

    public bool Send(Response response, TimeSpan timeout)
    {
        if (_gone) { return false; }
        if (!Monitor.TryEnter(_lock, timeout))
        {
            Close();
            return false;
        }
        try
        {
            if (_gone) { return false; }
            var frame = MessageFraming.Frame(response);
            // The handle is overlapped, so the write can be abandoned after the timeout.
            var write = _stream.WriteAsync(frame, 0, frame.Length);
            if (!write.Wait(timeout))
            {
                Close();
                return false;
            }
            return true;
        }
        catch (Exception)
        {
            Close();
            return false;
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    /// <summary>Blocks until the client closes its end or the connection is closed (Watch connections).</summary>
    public void WaitForClientClose()
    {
        var buffer = new byte[1];
        try
        {
            while (!_gone && _stream.Read(buffer, 0, 1) > 0) { }
        }
        catch (Exception) { }
        _gone = true;
    }

    internal void Close()
    {
        _gone = true;
        try { _stream.Dispose(); } catch (Exception) { }
    }
}

/// <summary>
/// Named pipe server. The pipe is created with FILE_FLAG_FIRST_PIPE_INSTANCE (fails if anyone
/// else already owns the name) and PIPE_REJECT_REMOTE_CLIENTS, with an explicit DACL. A
/// listening instance always exists while the server runs, so the name never becomes free.
/// </summary>
public sealed class PipeServer : IDisposable
{
    /// <summary>
    /// SYSTEM and Administrators: full control. INTERACTIVE: read/write data, read attributes,
    /// read control, synchronize (0x120083), never FILE_APPEND_DATA (= create pipe instance).
    /// NETWORK: denied.
    /// </summary>
    public const string DefaultSddl = "D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x120083;;;IU)";

    public const int MaxConcurrentConnections = 128;

    /// <summary>Open connections per user (including Watch connections), so one user cannot use up the slots.</summary>
    public const int MaxConnectionsPerUser = 4;

    public static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan BroadcastTimeout = TimeSpan.FromSeconds(2);

    /// <summary>A broadcast returns after this time at the latest, however many clients are slow.</summary>
    public static readonly TimeSpan BroadcastOverallTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long stopping waits for the accept thread.</summary>
    public static readonly TimeSpan AcceptJoinTimeout = TimeSpan.FromSeconds(3);

    private readonly string _pipeName;
    private readonly string _sddl;
    private readonly IRequestHandler _handler;
    private readonly IServiceLog _log;
    private readonly HashSet<PipeConnection> _connections = new();
    private readonly Dictionary<string, int> _perUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly RateLimiter _unknownOwnerLog = new(TimeSpan.FromMinutes(10));
    // Refusals at the pipe are logged at most RefusalLogLimit times per account per 10 minutes,
    // so a user cannot push the who-ran-what lines out of the rolling service log.
    private readonly RateLimiter _refusalLog = new(TimeSpan.FromMinutes(10));
    internal const int RefusalLogLimit = 10;

    /// <summary>The account of the client's process, or null. Replaced only by tests.</summary>
    internal Func<NamedPipeServerStream, string> OwnerLookup { get; set; } = ProcessOwner.TryGetSid;
    private readonly object _lock = new();
    private int _open;
    private Thread _acceptThread;
    private NamedPipeServerStream _listening;
    private volatile bool _stopping;

    private readonly Func<NamedPipeServerStream, CallerContext> _identifyCaller;

    /// <param name="identifyCaller">
    /// Who the client is, after its request was read; null refuses the connection. Default: the
    /// user and session from the client's own token (the user pipe). The trusted service caller
    /// pipe passes its own check.
    /// </param>
    public PipeServer(string pipeName, string sddl, IRequestHandler handler, IServiceLog log, Func<NamedPipeServerStream, CallerContext> identifyCaller = null)
    {
        _pipeName = pipeName;
        _sddl = sddl;
        _handler = handler;
        _log = log;
        _identifyCaller = identifyCaller ?? IdentifyCaller;
    }

    /// <summary>Called when the pipe could not be kept available (the service should stop).</summary>
    public event Action<string> Faulted;

    /// <summary>Creates the first instance (throws <see cref="PipeNameInUseException"/> if the name is taken) and starts accepting.</summary>
    public void Start()
    {
        _listening = CreateInstance(first: true);
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "PipeAccept" };
        _acceptThread.Start();
    }

    /// <summary>
    /// Sends <paramref name="message"/> to every open connection in parallel. Each send is bounded
    /// by <see cref="BroadcastTimeout"/> and the whole broadcast by <see cref="BroadcastOverallTimeout"/>.
    /// </summary>
    public void Broadcast(Response message)
    {
        List<PipeConnection> snapshot;
        lock (_lock) { snapshot = new List<PipeConnection>(_connections); }
        SendToAll(snapshot, c => c.Send(message, BroadcastTimeout), BroadcastOverallTimeout);
    }

    /// <summary>
    /// Runs <paramref name="send"/> for every target on its own thread and returns when all are
    /// done or <paramref name="overall"/> has passed, whichever is first. Sends still running then
    /// end on their own time limit. Returns true when every send finished in time.
    /// </summary>
    internal static bool SendToAll<T>(IReadOnlyCollection<T> targets, Func<T, bool> send, TimeSpan overall)
    {
        if (targets.Count == 0) { return true; }
        var remaining = targets.Count;
        using var done = new ManualResetEvent(false);
        var finished = false;
        var gate = new object();
        void CountDone()
        {
            // The event may already be disposed when the broadcast gave up waiting.
            if (Interlocked.Decrement(ref remaining) == 0)
            {
                lock (gate) { if (!finished) { done.Set(); } }
            }
        }
        foreach (var target in targets)
        {
            try
            {
                var thread = new Thread(() =>
                {
                    try { send(target); }
                    catch (Exception) { }
                    finally { CountDone(); }
                }) { IsBackground = true, Name = "PipeBroadcast" };
                thread.Start();
            }
            catch (Exception)
            {
                // No thread (out of resources): that client is skipped, the others still get it.
                CountDone();
            }
        }
        var allDone = done.WaitOne(overall);
        lock (gate) { finished = true; }
        return allDone;
    }

    /// <summary>Stops accepting new connections; open connections stay open.</summary>
    public void StopAccepting()
    {
        lock (_lock)
        {
            if (_stopping) { return; }
            _stopping = true;
            try { _listening?.Dispose(); } catch (Exception) { }
            _listening = null;
        }
        _acceptThread?.Join(AcceptJoinTimeout);
    }

    /// <summary>Closes every open connection.</summary>
    public void CloseAll()
    {
        List<PipeConnection> snapshot;
        lock (_lock) { snapshot = new List<PipeConnection>(_connections); }
        foreach (var c in snapshot) { c.Close(); }
    }

    public void Stop()
    {
        StopAccepting();
        CloseAll();
    }

    public void Dispose() => Stop();

    private void AcceptLoop()
    {
        while (!_stopping)
        {
            NamedPipeServerStream current;
            lock (_lock) { current = _listening; }
            if (current == null) { return; }

            try
            {
                current.WaitForConnection();
            }
            catch (Exception ex)
            {
                if (_stopping) { return; }
                // For example ERROR_NO_DATA: a client connected and closed before the wait.
                // That instance cannot be reused; replace it (the new one exists first).
                _log.Warn($"Pipe accept failed ({ex.GetType().Name}); replacing the instance.");
                if (!ReplaceListening()) { return; }
                current.Dispose();
                continue;
            }

            // Create the next listening instance before handling this one, so an instance of
            // the pipe always exists.
            if (!ReplaceListening())
            {
                current.Dispose();
                return;
            }

            if (!TryReserve(current, out var ownerSid))
            {
                current.Dispose();
                continue;
            }
            var thread = new Thread(() => HandleConnection(current, ownerSid)) { IsBackground = true, Name = "PipeConnection" };
            thread.Start();
        }
    }

    /// <summary>Creates the next listening instance, retrying with back-off. False only when stopping or faulted.</summary>
    private bool ReplaceListening()
    {
        var delay = TimeSpan.FromMilliseconds(200);
        var failures = 0;
        while (!_stopping)
        {
            try
            {
                var next = CreateInstance(first: false);
                lock (_lock)
                {
                    if (_stopping)
                    {
                        next.Dispose();
                        return false;
                    }
                    _listening = next;
                }
                return true;
            }
            catch (Exception ex)
            {
                failures++;
                _log.Error($"Could not create the next pipe instance (attempt {failures}): {ex.Message}");
                if (failures >= 10)
                {
                    Faulted?.Invoke("The pipe could not be kept available: " + ex.Message);
                    return false;
                }
                Thread.Sleep(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
            }
        }
        return false;
    }

    private bool TryReserve(NamedPipeServerStream stream, out string ownerSid)
    {
        ownerSid = OwnerLookup(stream);
        if (ownerSid == null)
        {
            // Every client the service serves runs in a process of its own account. Without an
            // account the connection cannot be counted per user; a shared bucket would let one
            // user (for example one whose client exits right after connecting) lock others out.
            if (_unknownOwnerLog.TryAcquire("unknown", 1)) { _log.Warn("A client whose account could not be determined (its process may have ended) was refused; further ones are not logged for 10 minutes."); }
            return false;
        }
        lock (_lock)
        {
            if (_stopping) { return false; }
            if (_open >= MaxConcurrentConnections)
            {
                WarnLimited(ownerSid, "Too many open connections; a client was refused.");
                return false;
            }
            _perUser.TryGetValue(ownerSid, out var count);
            if (count >= MaxConnectionsPerUser)
            {
                WarnLimited(ownerSid, $"Too many open connections from {ownerSid}; a client was refused.");
                return false;
            }
            _perUser[ownerSid] = count + 1;
            _open++;
            return true;
        }
    }

    private void WarnLimited(string ownerSid, string message)
    {
        if (_refusalLog.TryAcquire(ownerSid, RefusalLogLimit))
        {
            _log.Warn(message);
            return;
        }
        // Only the first refusal over the limit says that further ones are not logged.
        if (_refusalLog.TryAcquire(ownerSid + "|over", 1)) { _log.Warn($"Further refusals for {ownerSid} are not logged for 10 minutes."); }
    }

    private void Release(string ownerSid)
    {
        lock (_lock)
        {
            _open--;
            if (_perUser.TryGetValue(ownerSid, out var count))
            {
                if (count <= 1) { _perUser.Remove(ownerSid); } else { _perUser[ownerSid] = count - 1; }
            }
        }
    }

    private void HandleConnection(NamedPipeServerStream stream, string ownerSid)
    {
        var connection = new PipeConnection(stream);
        try
        {
            lock (_lock)
            {
                if (_stopping) { return; }
                _connections.Add(connection);
            }

            Request request;
            var requestRead = 0;
            // A client that connects but never sends a request is dropped after the timeout.
            using (new Timer(_ => { if (Interlocked.CompareExchange(ref requestRead, 2, 0) == 0) { connection.Close(); } },
                       null, RequestReadTimeout, Timeout.InfiniteTimeSpan))
            {
                try
                {
                    request = MessageFraming.Read<Request>(stream, ProtocolInfo.MaxRequestBytes);
                }
                catch (Exception ex)
                {
                    if (Interlocked.CompareExchange(ref requestRead, 1, 0) != 0) { return; }
                    WarnLimited(ownerSid, $"Invalid request: {ex.GetType().Name}.");
                    connection.Send(new Response { Type = ResponseType.Rejected, Result = ResultCode.InvalidRequest, Message = "The request could not be read." });
                    return;
                }
                if (Interlocked.CompareExchange(ref requestRead, 1, 0) != 0) { return; }
            }
            if (request == null || connection.IsGone) { return; }

            // Windows allows impersonating a pipe client only after data was read from it.
            var caller = _identifyCaller(stream);
            if (caller == null)
            {
                WarnLimited(ownerSid, "A client without a usable identity was refused.");
                return;
            }
            _handler.Handle(request, caller, connection);
        }
        catch (Exception ex)
        {
            _log.Error($"Connection failed: {ex}");
        }
        finally
        {
            lock (_lock) { _connections.Remove(connection); }
            connection.Close();
            Release(ownerSid);
        }
    }

    private const int TokenSessionIdClass = 12;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int length, out int returnLength);

    /// <summary>User SID and session ID, both taken from the client's own token.</summary>
    private static CallerContext IdentifyCaller(NamedPipeServerStream stream)
    {
        string sid = null, name = null;
        var anonymous = true;
        var session = -1;
        stream.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            sid = identity.User?.Value;
            name = identity.Name;
            anonymous = identity.IsAnonymous;
            if (GetTokenInformation(identity.Token, TokenSessionIdClass, out var value, sizeof(int), out _)) { session = value; }
        });
        if (anonymous || sid == null || session < 0) { return null; }
        return new CallerContext(sid, session, name);
    }

    // --- Native pipe creation -----------------------------------------------------------------------

    private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
    private const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint PIPE_TYPE_BYTE = 0x00000000;
    private const uint PIPE_READMODE_BYTE = 0x00000000;
    private const uint PIPE_WAIT = 0x00000000;
    private const uint PIPE_REJECT_REMOTE_CLIENTS = 0x00000008;
    private const uint PIPE_UNLIMITED_INSTANCES = 255;
    private const int ERROR_ACCESS_DENIED = 5;
    private const uint SDDL_REVISION_1 = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SECURITY_ATTRIBUTES securityAttributes);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private NamedPipeServerStream CreateInstance(bool first)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(_sddl, SDDL_REVISION_1, out var descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Invalid pipe security descriptor.");
        }
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = descriptor, bInheritHandle = false };
            // Overlapped, so a pending read (Watch) never blocks a concurrent write (ShuttingDown)
            // and writes can be abandoned after a timeout.
            var openMode = PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | (first ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0);
            var handle = CreateNamedPipe(@"\\.\pipe\" + _pipeName, openMode,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                PIPE_UNLIMITED_INSTANCES, 64 * 1024, 64 * 1024, 0, ref sa);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (first && error == ERROR_ACCESS_DENIED) { throw new PipeNameInUseException(_pipeName); }
                throw new Win32Exception(error, "Could not create the pipe.");
            }
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }
}

/// <summary>Owner of the client process, used only to cap connections per user before a request is read.</summary>
internal static class ProcessOwner
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public static string TryGetSid(NamedPipeServerStream stream)
    {
        try
        {
            var pid = PipeNative.GetClientProcessId(stream.SafePipeHandle);
            var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) { return null; }
            try
            {
                if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) { return null; }
                try
                {
                    using var identity = new WindowsIdentity(token);
                    return identity.User?.Value;
                }
                finally { CloseHandle(token); }
            }
            finally { CloseHandle(process); }
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Another process already owns the pipe name (possible squatting).</summary>
public sealed class PipeNameInUseException : IOException
{
    public PipeNameInUseException(string name) : base($"The pipe name '{name}' is already in use by another process.") { }
}
