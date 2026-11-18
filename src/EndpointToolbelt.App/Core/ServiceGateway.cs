using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;
using System.Threading;
using EndpointToolbelt.Client;
using EndpointToolbelt.Protocol;

namespace EndpointToolbelt.App.Core;

/// <summary>A file the service delivered (the support package), already checked.</summary>
public sealed class ReceivedFile
{
    public string Name { get; set; }
    public string Path { get; set; }

    /// <summary>Deletes a received file nobody is going to use.</summary>
    public static void Discard(ReceivedFile file)
    {
        if (file == null) { return; }
        try { File.Delete(file.Path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Everything the app asks of the service. One connection per request.</summary>
public interface IServiceGateway
{
    /// <summary>The service's status; throws <see cref="ServiceException"/> when it cannot be reached.</summary>
    StatusInfo GetStatus();

    /// <summary>
    /// Runs an action and returns its outcome. Records arrive through <paramref name="onRecord"/>
    /// while the run goes on (on a worker thread). Never throws for service problems: those are
    /// in the outcome's result code.
    /// </summary>
    RunOutcome Run(ToolbeltAction action, bool confirmed, int confirmedOtherSessions, Action<EngineRecord> onRecord, out ReceivedFile file, Action<string> onAccepted = null, int interfaceIndex = 0);

    /// <summary>
    /// The caller's stored result (also of a run still in progress), or null when there is none or
    /// the service cannot be reached. Never starts the service: it is asked while following a run.
    /// </summary>
    RunOutcome GetLastResult(out ReceivedFile file);

    /// <summary>A client-reported event; best effort.</summary>
    void ReportEvent(int eventId, string text);
}

public sealed class ServiceException : Exception
{
    public ServiceException(ResultCode code, string message, Exception inner = null) : base(message, inner) => Code = code;
    public ResultCode Code { get; }
}

/// <summary>One request's connection to the service (the real one is a <see cref="ToolbeltClient"/>).</summary>
public interface IServiceConnection : IDisposable
{
    void Send(Request request);

    /// <summary>The next response, or null when the service closed the connection.</summary>
    Response Receive();
}

public sealed class ServiceGateway : IServiceGateway
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex FileNamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\.zip\z", RegexOptions.CultureInvariant, TimedRegex.Limit);

    // The largest package the app accepts.
    internal const long MaxFileBytes = 4L * 1024 * 1024 * 1024;

    // A run's records the app keeps; a run that writes more is cut off with a warning.
    internal const int MaxRecords = 20000;

    private readonly string _incomingFolder;
    private readonly Func<bool, IServiceConnection> _open;

    /// <param name="incomingFolder">Where streamed files are received.</param>
    /// <param name="open">Opens a connection; the argument says whether a stopped service may be started first.</param>
    public ServiceGateway(string incomingFolder = null, Func<bool, IServiceConnection> open = null)
    {
        _incomingFolder = incomingFolder ?? Path.Combine(UserFiles.DataFolder(), "Incoming");
        _open = open ?? OpenPipe;
    }

    private static IServiceConnection OpenPipe(bool startService)
    {
        // Interactive users may start the service; it is Automatic (Delayed) and may not run yet.
        if (startService) { ToolbeltClient.EnsureServiceRunning(StartTimeout); }
        return new PipeConnection(ToolbeltClient.Connect(ConnectTimeout));
    }

    private sealed class PipeConnection : IServiceConnection
    {
        private readonly ToolbeltClient _client;
        public PipeConnection(ToolbeltClient client) => _client = client;
        public void Send(Request request) => _client.Send(request);
        public Response Receive() => _client.Receive();
        public void Dispose() => _client.Dispose();
    }

    private IServiceConnection Open(bool startService)
    {
        try
        {
            return _open(startService);
        }
        catch (ServerIdentityException ex)
        {
            throw new ServiceException(ResultCode.ServerIdentityMismatch, ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not installed, disabled, stopped, not answering, or refused.
            throw new ServiceException(ResultCode.ServiceUnavailable, "The " + ProductInfo.DisplayName + " service is not available.", ex);
        }
    }

    /// <summary>
    /// What a failure while talking to the service means: a cut connection is the service going
    /// away; a message the app cannot read is a different version of the service.
    /// </summary>
    internal static ResultCode Classify(Exception ex)
        => ex is IOException ? ResultCode.ServiceUnavailable : ResultCode.ProtocolMismatch;

    /// <summary>The nonce of the last status fetched, sent with the next action.</summary>
    private string _confirmationNonce;

    public StatusInfo GetStatus()
    {
        using var client = Open(startService: true);
        try
        {
            client.Send(new Request { Type = RequestType.GetStatus });
            var response = client.Receive();
            if (response?.Type == ResponseType.Status && response.Status != null)
            {
                // The warning the window shows comes from this status; its nonce confirms it.
                _confirmationNonce = response.Status.ConfirmationNonce;
                return response.Status;
            }
            throw new ServiceException(response?.Result ?? ResultCode.ServiceUnavailable, response?.Message ?? "The service did not answer.");
        }
        catch (Exception ex) when (ex is not ServiceException and not OutOfMemoryException)
        {
            throw new ServiceException(Classify(ex), "The connection to the service failed: " + ex.Message, ex);
        }
    }

    /// <param name="onAccepted">Called with the run ID as soon as the service accepted the run, so the window can name the run while it goes on.</param>
    /// <param name="interfaceIndex">The network adapter the user chose, for Get a new IP address only; 0 otherwise.</param>
    public RunOutcome Run(ToolbeltAction action, bool confirmed, int confirmedOtherSessions, Action<EngineRecord> onRecord, out ReceivedFile file, Action<string> onAccepted = null, int interfaceIndex = 0)
    {
        file = null;
        var outcome = new RunOutcome { Action = action, Result = ResultCode.ServiceUnavailable, StartedUtc = DateTime.UtcNow };
        IServiceConnection client;
        try { client = Open(startService: true); }
        catch (ServiceException ex)
        {
            outcome.Result = ex.Code;
            outcome.Message = ex.Code == ResultCode.ServerIdentityMismatch ? ex.Message : null;
            return outcome;
        }
        var completed = false;
        FileReceiver receiver = null;
        using (client)
        {
            try
            {
                client.Send(new Request { Type = RequestType.RunAction, Action = action, Confirmed = confirmed, ConfirmedOtherSessions = confirmedOtherSessions, ConfirmationNonce = _confirmationNonce, InterfaceIndex = interfaceIndex });
                while (true)
                {
                    var response = client.Receive();
                    if (response == null) { break; }
                    switch (response.Type)
                    {
                        case ResponseType.Accepted:
                            outcome.RunId = response.RunId;
                            onAccepted?.Invoke(response.RunId);
                            break;
                        case ResponseType.Rejected:
                            outcome.Result = response.Result;
                            outcome.Message = response.Message;
                            return outcome;
                        case ResponseType.Record:
                            AddRecord(outcome, EngineRecord.Parse(response.Record), onRecord);
                            break;
                        case ResponseType.Completed:
                            completed = true;
                            outcome.Result = response.Result;
                            outcome.ExitCode = response.ExitCode;
                            outcome.Message = response.Message;
                            break;
                        case ResponseType.FileStart:
                        case ResponseType.FileChunk:
                        case ResponseType.FileEnd:
                            // A package that cannot be received is reported on its own; it never
                            // changes the action's result.
                            try
                            {
                                if (response.Type == ResponseType.FileStart)
                                {
                                    receiver?.Dispose();
                                    receiver = new FileReceiver(_incomingFolder, response);
                                }
                                else if (response.Type == ResponseType.FileChunk) { receiver?.Add(response.Data); }
                                else if (receiver != null)
                                {
                                    var received = receiver.Finish();
                                    receiver.Dispose();
                                    receiver = null;
                                    // Only one package per run; a second one replaces nothing.
                                    if (file == null) { file = received; } else { ReceivedFile.Discard(received); }
                                }
                            }
                            catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
                            {
                                receiver?.Dispose();
                                receiver = null;
                                outcome.FileError = ex.Message;
                            }
                            break;
                    }
                }
                if (receiver != null) { outcome.FileError = "The connection ended before the whole package arrived."; }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (completed)
                {
                    // The result is in; only the package that followed it was lost.
                    if (receiver != null || file == null) { outcome.FileError ??= "The connection ended before the whole package arrived: " + ex.Message; }
                }
                else
                {
                    outcome.Result = Classify(ex);
                    outcome.Message = "The connection to the service was lost: " + ex.Message;
                }
            }
            finally
            {
                receiver?.Dispose();
            }
        }
        if (!completed && !string.IsNullOrEmpty(outcome.RunId) && outcome.Result is ResultCode.ServiceUnavailable or ResultCode.ProtocolMismatch)
        {
            // The service accepted the run and the connection ended before its result: the run
            // itself goes on in the service, which keeps the result for this user.
            outcome.InProgress = true;
        }
        return outcome;
    }

    private static void AddRecord(RunOutcome outcome, EngineRecord record, Action<EngineRecord> onRecord)
    {
        if (record == null) { return; }
        if (outcome.Records.Count >= MaxRecords)
        {
            if (outcome.Records.Count == MaxRecords)
            {
                var cut = new EngineRecord { Type = "warning", Text = "This run wrote more records than the app shows; the rest is in the support package logs.", IsCut = true };
                outcome.Truncated = true;
                outcome.Records.Add(cut);
                onRecord?.Invoke(cut);
            }
            return;
        }
        outcome.Records.Add(record);
        onRecord?.Invoke(record);
    }

    public RunOutcome GetLastResult(out ReceivedFile file)
    {
        file = null;
        IServiceConnection client;
        try { client = Open(startService: false); }
        catch (ServiceException) { return null; }
        FileReceiver receiver = null;
        using (client)
        {
            try
            {
                client.Send(new Request { Type = RequestType.GetLastResult });
                var first = client.Receive();
                if (first?.Type != ResponseType.LastResult || first.LastResult == null) { return null; }
                var last = first.LastResult;
                var outcome = new RunOutcome
                {
                    Action = last.Action,
                    RunId = last.RunId,
                    Result = last.Result,
                    ExitCode = last.ExitCode,
                    InProgress = last.InProgress,
                    StartedUtc = TimeText.ParseUtc(last.StartedUtc),
                };
                for (var read = 0; ;)
                {
                    var response = client.Receive();
                    if (response == null) { break; }
                    if (response.Type == ResponseType.Record && read < last.RecordCount)
                    {
                        read++;
                        AddRecord(outcome, EngineRecord.Parse(response.Record), null);
                        continue;
                    }
                    try
                    {
                        if (response.Type == ResponseType.FileStart) { receiver?.Dispose(); receiver = new FileReceiver(_incomingFolder, response); }
                        else if (response.Type == ResponseType.FileChunk) { receiver?.Add(response.Data); }
                        else if (response.Type == ResponseType.FileEnd && receiver != null)
                        {
                            var received = receiver.Finish();
                            receiver.Dispose();
                            receiver = null;
                            if (file == null) { file = received; } else { ReceivedFile.Discard(received); }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
                    {
                        receiver?.Dispose();
                        receiver = null;
                        outcome.FileError = ex.Message;
                    }
                }
                return outcome;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReceivedFile.Discard(file);
                file = null;
                return null;
            }
            finally
            {
                receiver?.Dispose();
            }
        }
    }

    public void ReportEvent(int eventId, string text)
    {
        try
        {
            using var client = Open(startService: false);
            client.Send(new Request { Type = RequestType.ReportClientEvent, ClientEventId = eventId, ClientEventText = text });
            client.Receive();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Client events are informational; nothing depends on them.
        }
    }

    /// <summary>Removes received files left behind by an app that closed mid-transfer.</summary>
    public void CleanIncoming(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(_incomingFolder)) { return; }
            foreach (var path in Directory.GetFiles(_incomingFolder, "*.zip"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow - olderThan) { File.Delete(path); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Writes a streamed file to the user's own folder and checks it against the length and
    /// SHA-256 the service announced; a file that does not match is deleted.
    /// </summary>
    internal sealed class FileReceiver : IDisposable
    {
        // Room left on the drive besides the file itself.
        private const long FreeSpaceMargin = 256L * 1024 * 1024;

        private readonly string _path;
        private readonly string _name;
        private readonly long _length;
        private readonly string _sha256;
        private readonly FileStream _stream;
        private readonly SHA256 _hash = SHA256.Create();
        private long _written;
        private bool _finished;

        public FileReceiver(string folder, Response start, Func<string, long> freeBytes = null)
        {
            if (!TimedRegex.IsMatch(FileNamePattern, start.FileName ?? "")) { throw new IOException("The service named an invalid file."); }
            if (start.FileLength < 0 || start.FileLength > MaxFileBytes) { throw new IOException("The file is larger than the app accepts."); }
            if (string.IsNullOrEmpty(start.FileSha256)) { throw new IOException("The service sent no checksum for the file."); }
            Directory.CreateDirectory(folder);
            var free = (freeBytes ?? FreeBytes)(folder);
            if (free >= 0 && free < start.FileLength + FreeSpaceMargin)
            {
                throw new IOException("There is not enough free disk space to receive the support package.");
            }
            _name = start.FileName;
            _length = start.FileLength;
            _sha256 = start.FileSha256;
            _path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".zip");
            _stream = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }

        private static long FreeBytes(string folder)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))).AvailableFreeSpace; }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return -1; }
        }

        public void Add(string base64)
        {
            var bytes = Convert.FromBase64String(base64 ?? "");
            _written += bytes.Length;
            if (_written > _length) { throw new IOException("The service sent more data than it announced."); }
            _hash.TransformBlock(bytes, 0, bytes.Length, null, 0);
            _stream.Write(bytes, 0, bytes.Length);
        }

        public ReceivedFile Finish()
        {
            _hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            _stream.Dispose();
            var actual = BitConverter.ToString(_hash.Hash).Replace("-", "");
            if (_written != _length || !string.Equals(actual, _sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The received file does not match what the service sent.");
            }
            _finished = true;
            return new ReceivedFile { Name = _name, Path = _path };
        }

        public void Dispose()
        {
            _stream.Dispose();
            _hash.Dispose();
            if (!_finished) { try { File.Delete(_path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        }
    }
}

/// <summary>
/// Keeps a Watch connection open. When the service announces that it stops (an upgrade or an
/// uninstall), <see cref="ShuttingDown"/> fires so the app can close itself; that also covers
/// sessions the installer's Restart Manager cannot reach.
/// </summary>
public sealed class ServiceWatcher : IDisposable
{
    private readonly Thread _thread;
    private readonly Func<IServiceConnection> _connect;
    private readonly TimeSpan _retry;
    private volatile bool _stopped;
    private volatile IServiceConnection _client;

    public event Action ShuttingDown;

    /// <summary>The watch connection to the service was made (again). Raised on the watcher's thread.</summary>
    public event Action Connected;

    /// <summary>
    /// The watch connection ended without a stop announcement, or could not be made (again) after
    /// it was there: the service went away. Raised once per loss, on the watcher's thread.
    /// </summary>
    public event Action Lost;

    private bool _connected;

    public ServiceWatcher(Func<IServiceConnection> connect = null, TimeSpan? retry = null)
    {
        _connect = connect ?? (() => new WatchConnection(ToolbeltClient.Connect(TimeSpan.FromSeconds(5))));
        _retry = retry ?? TimeSpan.FromSeconds(30);
        _thread = new Thread(Loop) { IsBackground = true, Name = "Service watcher" };
    }

    private sealed class WatchConnection : IServiceConnection
    {
        private readonly ToolbeltClient _client;
        public WatchConnection(ToolbeltClient client) => _client = client;
        public void Send(Request request) => _client.Send(request);
        public Response Receive() => _client.Receive();
        public void Dispose() => _client.Dispose();
    }

    public void Start() => _thread.Start();

    /// <summary>The watcher's thread has ended (for tests).</summary>
    public bool Join(TimeSpan timeout) => _thread.Join(timeout);

    private void Loop()
    {
        while (!_stopped)
        {
            try
            {
                using var client = _connect();
                _client = client;
                if (_stopped) { return; }
                client.Send(new Request { Type = RequestType.Watch });
                if (!_connected)
                {
                    _connected = true;
                    Raise(Connected);
                }
                while (!_stopped)
                {
                    var response = client.Receive();
                    if (response == null) { break; }
                    if (response.Type == ResponseType.ShuttingDown)
                    {
                        try { ShuttingDown?.Invoke(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Not running, not reachable, or an unreadable message; tried again below. The
                // watcher is a convenience and must never take the app down.
            }
            finally
            {
                _client = null;
            }
            if (_connected && !_stopped)
            {
                _connected = false;
                Raise(Lost);
            }
            var waited = TimeSpan.Zero;
            while (!_stopped && waited < _retry)
            {
                Thread.Sleep(200);
                waited += TimeSpan.FromMilliseconds(200);
            }
        }
    }

    private static void Raise(Action handler)
    {
        try { handler?.Invoke(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    public void Dispose()
    {
        _stopped = true;
        try { _client?.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}
