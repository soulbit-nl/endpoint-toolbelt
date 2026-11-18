using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.Engine;
using EndpointToolbelt.Service.Logging;
using EndpointToolbelt.Platform;
using EndpointToolbelt.Service.Platform;
using EndpointToolbelt.Service.Rules;
using EndpointToolbelt.Service.State;

namespace EndpointToolbelt.Service;

/// <summary>Receives responses for one connection. Returns false once the client is gone.</summary>
public interface IResponseSink
{
    bool Send(Response response);
}

/// <summary>
/// Runs one action at a time for any user. Every request passes <see cref="RequestPolicy"/>;
/// records go only to the requesting connection and to that user's stored result.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "One instance lives as long as the service process; _idle is an in-process event that the process exit releases.")]
public sealed class ActionCoordinator
{
    /// <summary>
    /// Total size of records stored per run. All records are still streamed live. When the cap
    /// is reached, older records of a lower rank make room (see <see cref="Rank"/>): info
    /// progress first, then warning or error progress, then warning records. The closing
    /// records (summary, restart-required) are always kept; for them even findings and changes
    /// make room, oldest first.
    /// </summary>
    public const int MaxStoredRecordChars = 256 * 1024;

    /// <summary>Room within the cap that only the closing records may use.</summary>
    public const int ClosingRecordReserveChars = 8 * 1024;

    /// <summary>Records that make the stored in-progress copy worth saving (throttled).</summary>
    private static readonly string[] CheckpointRecordTypes = { "finding", "change", "restart-required", "mdm-queued", "summary", "file" };

    /// <summary>Minimum time between two checkpoint saves of a run in progress.</summary>
    internal static TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromSeconds(5);

    private const int FinalSaveAttempts = 3;
    internal static TimeSpan FinalSaveRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>The wait before the engine check runs once more after a file could not be opened.</summary>
    internal static readonly TimeSpan EngineRecheckDelay = TimeSpan.FromSeconds(3);

    private const int RankInfoProgress = 0;
    private const int RankOtherProgress = 1;
    private const int RankWarning = 2;
    private const int RankResult = 3;
    private const int RankClosing = 4;

    /// <summary>Eviction rank of a stored record; lower ranks make room first.</summary>
    private static int Rank(EngineRecordHeader header) => header.Type switch
    {
        "progress" => string.IsNullOrEmpty(header.Severity) || header.Severity.Equals("info", StringComparison.OrdinalIgnoreCase) ? RankInfoProgress : RankOtherProgress,
        "warning" => RankWarning,
        "summary" or "restart-required" => RankClosing,
        _ => RankResult,
    };

    private static readonly Regex FileNamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\.zip\z", RegexOptions.CultureInvariant, TimedRegex.Limit);

    /// <summary>The result text when a device policy or security software refused the engine executable.</summary>
    internal const string EngineBlockedMessage = "A device policy (App Control, AppLocker or a Microsoft Defender attack surface reduction rule) or security software blocked the repair engine. Your IT support can allow it; see the deployment documentation.";

    /// <summary>Rejection log lines (and events) per user per 10 minutes; more are counted, not logged.</summary>
    private const int RejectionLogsPerWindow = 10;

    private readonly ServicePaths _paths;
    private readonly IEngineRunner _runner;
    private readonly ResultStore _results;
    private readonly IServiceLog _log;
    private readonly IEventWriter _events;
    private readonly Func<ServiceConfig> _config;
    private readonly RateLimiter _rateLimiter;
    private readonly RateLimiter _rejectionLogLimiter = new(TimeSpan.FromMinutes(10));
    private readonly string _productVersion;
    private readonly Func<string> _engineLogFolder;
    private readonly Func<string, EngineCheck> _validateEngine;
    private readonly TimeSpan _engineRecheckDelay;
    private readonly Func<string> _newRunId;
    private readonly Func<bool> _isMultiSessionHost;
    private readonly RateLimiter _packageLimiter;
    private readonly RateLimiter _readOnlyLimiter;
    private readonly Action<ToolbeltAction> _runEnded;

    /// <summary>The nonces that bind a confirmation to a shown status; the status handler issues them.</summary>
    public ConfirmationNonces Nonces { get; }

    private readonly object _lock = new();
    private readonly ManualResetEventSlim _idle = new(initialState: true);
    private RunState _current;
    private bool _stopping;

    /// <summary>State of the one run in progress. Records from a finished run are never accepted.</summary>
    private sealed class RunState
    {
        public RunState(ToolbeltAction action, string runId, string sid, DateTime startedUtc, CancellationTokenSource cancellation, int interfaceIndex = 0)
        {
            InterfaceIndex = interfaceIndex;
            Action = action;
            RunId = runId;
            Sid = sid;
            StartedUtc = startedUtc;
            Cancellation = cancellation;
            Info = new LastResultInfo { Action = action, RunId = runId, StartedUtc = Iso(startedUtc), InProgress = true };
        }

        public ToolbeltAction Action { get; }

        /// <summary>The adapter the user chose for Get a new IP address; 0 for every other action.</summary>
        public int InterfaceIndex { get; }
        public string RunId { get; }
        public string Sid { get; }
        public DateTime StartedUtc { get; }
        public CancellationTokenSource Cancellation { get; }
        public LastResultInfo Info { get; }
        public object Sync { get; } = new();
        public int StoredChars { get; set; }

        /// <summary>Eviction rank of each entry in <c>Info.Records</c>, same order.</summary>
        public List<int> StoredRanks { get; } = new();

        /// <summary>Records of this run received so far (stored or not).</summary>
        public int Received { get; set; }

        /// <summary>When the in-progress copy was last saved; null before the first checkpoint.</summary>
        public Stopwatch SinceCheckpoint { get; set; }
        public bool CheckpointPending { get; set; }
        public bool Closed { get; set; }
        public bool Finished { get; set; }
        public string ProducedFile { get; set; }
    }

    public ActionCoordinator(
        ServicePaths paths,
        IEngineRunner runner,
        ResultStore results,
        IServiceLog log,
        IEventWriter events,
        Func<ServiceConfig> config,
        RateLimiter rateLimiter,
        string productVersion,
        Func<string> engineLogFolder,
        Func<string, EngineCheck> validateEngine = null,
        Func<string> newRunId = null,
        Func<bool> isMultiSessionHost = null,
        RateLimiter packageLimiter = null,
        ConfirmationNonces nonces = null,
        RateLimiter readOnlyLimiter = null,
        Action<ToolbeltAction> runEnded = null,
        TimeSpan? engineRecheckDelay = null)
    {
        _runEnded = runEnded;
        Nonces = nonces ?? new ConfirmationNonces();
        _isMultiSessionHost = isMultiSessionHost ?? (() => false);
        _packageLimiter = packageLimiter ?? new RateLimiter(TimeSpan.FromHours(1));
        _readOnlyLimiter = readOnlyLimiter ?? new RateLimiter(TimeSpan.FromHours(1));
        _paths = paths;
        _runner = runner;
        _results = results;
        _log = log;
        _events = events;
        _config = config;
        _rateLimiter = rateLimiter;
        _productVersion = productVersion;
        _engineLogFolder = engineLogFolder;
        _validateEngine = validateEngine ?? FileSystemSafety.CheckEngineExecutable;
        _engineRecheckDelay = engineRecheckDelay ?? EngineRecheckDelay;
        _newRunId = newRunId ?? (() => Guid.NewGuid().ToString("N").Substring(0, 8));
    }

    public (ToolbeltAction? Action, DateTime? SinceUtc) GetRunning()
    {
        lock (_lock) { return _current != null ? (_current.Action, _current.StartedUtc) : (null, (DateTime?)null); }
    }

    /// <summary>The caller's own result: the run in progress if it is theirs, else the stored one.</summary>
    public LastResultInfo GetLastResult(string sid)
    {
        RunState current;
        lock (_lock) { current = _current; }
        if (current != null && string.Equals(current.Sid, sid, StringComparison.OrdinalIgnoreCase))
        {
            lock (current.Sync)
            {
                // A finished run may not be stored yet (or its package is still being sent):
                // its final result comes from memory, never the "in progress" copy on disk.
                var info = current.Info;
                return new LastResultInfo
                {
                    Action = info.Action,
                    RunId = info.RunId,
                    StartedUtc = info.StartedUtc,
                    InProgress = !current.Finished,
                    Records = new List<string>(info.Records),
                    RecordsTruncated = info.RecordsTruncated,
                    EndedUtc = current.Finished ? info.EndedUtc : null,
                    Result = current.Finished ? info.Result : ResultCode.Completed,
                    ExitCode = current.Finished ? info.ExitCode : null,
                    FileName = current.Finished ? info.FileName : null,
                    FileSha256 = current.Finished ? info.FileSha256 : null,
                };
            }
        }
        var stored = _results.Load(sid);
        if (stored != null && stored.InProgress)
        {
            // Not this process's run (for example its final result could not be stored).
            stored.InProgress = false;
            stored.Result = ResultCode.Interrupted;
            stored.ExitCode = null;
            if (string.IsNullOrEmpty(stored.EndedUtc)) { stored.EndedUtc = Iso(DateTime.UtcNow); }
        }
        return stored;
    }

    /// <summary>
    /// At service start: stored runs still marked in progress were cut off by a restart, an
    /// upgrade or a power loss. They become <see cref="ResultCode.Interrupted"/>, with their records.
    /// </summary>
    public void RecoverInterruptedRuns()
    {
        try
        {
            foreach (var run in _results.MarkInterrupted(DateTime.UtcNow, problem => _log.Warn("Stored results: " + problem)))
            {
                var text = $"{run.Action} ended: {ResultCode.Interrupted}. Run {run.RunId}.";
                _log.Warn(text + " The service stopped while it ran.");
                _events.Write(EndpointToolbelt.EventId.ActionFailed, EventLogEntryType.Warning, text);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not check the stored results for interrupted runs: {ex.Message}");
        }
    }

    public bool HasLastResult(string sid)
    {
        lock (_lock)
        {
            if (_current != null && string.Equals(_current.Sid, sid, StringComparison.OrdinalIgnoreCase)) { return true; }
        }
        return _results.Exists(sid);
    }

    public string GetStoredFile(string sid, string fileName) => _results.GetFile(sid, fileName);

    /// <summary>
    /// Rejects new runs, stops a running engine and waits (bounded) until its result is stored.
    /// </summary>
    public void Shutdown(TimeSpan wait)
    {
        BeginShutdown();
        WaitIdle(wait);
    }

    /// <summary>Rejects new runs and cancels a running engine. Does not wait.</summary>
    public void BeginShutdown()
    {
        lock (_lock)
        {
            _stopping = true;
            try { _current?.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Waits (bounded) until no run is in progress. True when idle.</summary>
    public bool WaitIdle(TimeSpan wait) => _idle.Wait(wait < TimeSpan.Zero ? TimeSpan.Zero : wait);

    public void RunAction(Request request, CallerContext caller, int otherSessions, IResponseSink sink)
    {
        var config = _config();
        RunState run;
        lock (_lock)
        {
            if (_stopping)
            {
                sink.Send(new Response { Type = ResponseType.Rejected, Result = ResultCode.ServiceUnavailable, Action = request.Action, Message = "The service is stopping." });
                return;
            }
            var decision = RequestPolicy.EvaluateRunAction(request, caller, otherSessions, config, _current?.Action, _rateLimiter,
                _isMultiSessionHost(), _packageLimiter, Nonces, _readOnlyLimiter);
            if (!decision.IsAccepted)
            {
                LogRejection(request, caller, decision);
                sink.Send(new Response { Type = ResponseType.Rejected, Result = decision.Result, Action = request.Action, Message = decision.Message });
                return;
            }
            // The shown warning is spent by this run; a second run needs a new status.
            if (RequestPolicy.NeedsShownWarning(ActionCatalog.Get(request.Action), otherSessions)) { Nonces.Consume(caller.Sid, request.ConfirmationNonce); }
            run = new RunState(request.Action, _newRunId(), caller.Sid, DateTime.UtcNow, new CancellationTokenSource(), request.InterfaceIndex);
            _current = run;
            _idle.Reset();
        }

        try
        {
            // Stored as "in progress" first, so a run cut off by a restart or an upgrade is found
            // (and reported as interrupted) when the service starts again.
            try { _results.Save(caller.Sid, run.Info); }
            catch (Exception ex) { _log.Warn($"Run {run.RunId}: could not store the run in progress: {ex.Message}"); }
            sink.Send(new Response { Type = ResponseType.Accepted, Result = ResultCode.Accepted, Action = request.Action, RunId = run.RunId });
            Execute(run, caller, otherSessions, config, sink);
        }
        catch (Exception ex)
        {
            _log.Error($"Run {run.RunId}: unexpected error: {ex}");
            Finish(run, caller, sink, ResultCode.EngineCrashed, null, null, "The service could not complete the action.");
        }
        finally
        {
            Release(run);
            run.Cancellation.Dispose();
        }
    }

    private void Release(RunState run)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_current, run))
            {
                _current = null;
                _idle.Set();
            }
        }
    }

    private void Execute(RunState run, CallerContext caller, int otherSessions, ServiceConfig config, IResponseSink sink)
    {
        var info = ActionCatalog.Get(run.Action);
        _log.Info(caller.IsTrustedServiceCaller
            ? $"Run {run.RunId}: {run.Action} requested by {caller.Sid} (trusted service caller; user scope: {caller.ScopeSid ?? "none, device only"}, session {caller.SessionId}, {otherSessions} other session(s))."
            : $"Run {run.RunId}: {run.Action} requested by {caller.Sid} (session {caller.SessionId}, {otherSessions} other session(s)).");
        _events.Write(EndpointToolbelt.EventId.ActionStarted, EventLogEntryType.Information, $"{run.Action} started. Run {run.RunId}.");

        if (!File.Exists(_paths.EngineExecutable))
        {
            Finish(run, caller, sink, ResultCode.EngineDidNotStart, null, null, "The repair engine is missing. Repair or reinstall " + ProductInfo.DisplayName + ".");
            return;
        }
        var engineCheck = _validateEngine(_paths.EngineExecutable);
        if (engineCheck?.CouldNotOpen == true)
        {
            // A file that could not be opened (another process had it open without sharing, such
            // as a scanner) is often free a moment later: checked once more. A link, a hard link or
            // a folder that is not protected cannot pass a second check, so it is not repeated.
            _log.Warn($"Run {run.RunId}: the engine check could not open a file: {engineCheck.Problem}. Checking again in {_engineRecheckDelay.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s.");
            Thread.Sleep(_engineRecheckDelay);
            engineCheck = _validateEngine(_paths.EngineExecutable);
            if (engineCheck == null) { _log.Info($"Run {run.RunId}: the second engine check passed."); }
        }
        if (engineCheck != null)
        {
            if (engineCheck.CouldNotOpen)
            {
                _log.Error($"Run {run.RunId}: the engine was not started: {engineCheck.Problem}, also on the second check.");
                _events.Write(EndpointToolbelt.EventId.ServiceError, EventLogEntryType.Error, "The repair engine was not started because one of its files could not be opened: " + engineCheck.Problem);
                Finish(run, caller, sink, ResultCode.EngineDidNotStart, null, null, "The repair engine could not be checked because one of its files was in use. Try again in a minute.");
                return;
            }
            _log.Error($"Run {run.RunId}: the engine is not trusted: {engineCheck.Problem}");
            _events.Write(EndpointToolbelt.EventId.ServiceError, EventLogEntryType.Error, "The repair engine was not started because its files are not protected: " + engineCheck.Problem);
            Finish(run, caller, sink, ResultCode.EngineDidNotStart, null, null, "The repair engine was not started because its files are not protected. Repair or reinstall " + ProductInfo.DisplayName + ".");
            return;
        }

        // Everything of one run lives under its own work folder (request, output, temp), so a
        // following run never shares or cleans up anything of this one.
        var runFolder = Path.Combine(_paths.WorkFolder, run.RunId);
        var outFolder = Path.Combine(runFolder, "out");
        var tempFolder = Path.Combine(runFolder, "temp");
        Directory.CreateDirectory(outFolder);
        Directory.CreateDirectory(tempFolder);
        try
        {
            var requestFile = Path.Combine(runFolder, "request.json");
            var engineRequest = new EngineRequest
            {
                RunId = run.RunId,
                Action = run.Action.ToString(),
                ProductVersion = _productVersion,
                OutputDirectory = outFolder,
                LogDirectory = _engineLogFolder(),
                CallerSessionId = caller.SessionId,
                OtherSessions = otherSessions,
                Config = EngineConfig.From(config),
                TimeLimitMinutes = (int)info.TimeLimit.TotalMinutes,
                // The user whose data the run may return: the caller, or for the trusted service
                // caller the console user or nobody.
                CallerSid = caller.ScopeSid,
                InterfaceIndex = run.InterfaceIndex,
                // The engine fills the name cache from the IME's policy lines it reads.
                AppNameCache = _paths.AppNameCacheFile,
            };
            File.WriteAllBytes(requestFile, MessageFraming.Serialize(engineRequest));

            var startInfo = EngineLaunch.Create(_paths.EngineExecutable, run.Action, requestFile, tempFolder, info.TimeLimit);
            var result = _runner.Run(startInfo, record => OnRecord(run, info, record, sink), run.Cancellation.Token);
            lock (run.Sync) { run.Closed = true; }

            foreach (var line in result.ErrorTail) { _log.Warn($"Run {run.RunId} stderr: {line}"); }

            switch (result.Outcome)
            {
                case EngineOutcome.FailedToStart when EngineLaunch.IsPolicyBlock(result.StartErrorCode):
                    // App Control, AppLocker, an ASR rule or security software refused the engine executable itself.
                    _log.Error($"Run {run.RunId}: a device policy blocked the engine: {result.StartError}");
                    _events.Write(EndpointToolbelt.EventId.EngineNotSigned, EventLogEntryType.Error, EngineBlockedMessage + " " + result.StartError);
                    Finish(run, caller, sink, ResultCode.EngineNotSigned, null, null, EngineBlockedMessage);
                    return;
                case EngineOutcome.FailedToStart:
                    _log.Error($"Run {run.RunId}: engine did not start: {result.StartError}");
                    Finish(run, caller, sink, ResultCode.EngineDidNotStart, null, null, "The repair engine could not be started.");
                    return;
                case EngineOutcome.TimedOut:
                    Finish(run, caller, sink, ResultCode.EngineTimeout, null, null,
                        "The action reached its time limit and was stopped. Windows servicing may still be running in the background.");
                    return;
                case EngineOutcome.Cancelled:
                    // Only Shutdown cancels a run: the device restarts or the product is updated.
                    Finish(run, caller, sink, ResultCode.Interrupted, null, null, "The service stopped while the action was running.");
                    return;
            }

            string storedName = null, storedHash = null;
            if (run.ProducedFile != null && (result.ExitCode == EngineExitCode.Success || result.ExitCode == EngineExitCode.Warnings))
            {
                storedName = StoreProducedFile(caller.Sid, outFolder, run.ProducedFile, run.RunId, out storedHash);
            }
            Finish(run, caller, sink, ResultCode.Completed, result.ExitCode, storedName, null, storedHash);

            if (storedName != null)
            {
                try { SendFile(sink, _results.GetFile(caller.Sid, storedName), storedName, run.RunId, storedHash); }
                catch (Exception ex) { _log.Warn($"Run {run.RunId}: sending the package failed: {ex.Message}"); }
            }
        }
        finally
        {
            try { FileSystemSafety.DeleteTreeNoFollow(runFolder); }
            catch (Exception ex) { _log.Warn($"Run {run.RunId}: could not remove the work folder: {ex.Message}"); }
        }
    }

    private void OnRecord(RunState run, ActionInfo info, string record, IResponseSink sink)
    {
        var header = TryReadHeader(record);
        // Lines that are not records of this run (for example echoed text) are ignored.
        if (header == null || !string.Equals(header.Run, run.RunId, StringComparison.Ordinal)) { return; }
        if (header.Type == "progress") { header.Severity = TryReadSeverity(record); }
        lock (run.Sync)
        {
            if (run.Closed) { return; }
            run.Received++;
            if (header.Type == "file" && info.ProducesFile && run.ProducedFile == null) { run.ProducedFile = header.Name; }
            StoreRecord(run, Rank(header), record);
            if (Array.IndexOf(CheckpointRecordTypes, header.Type) >= 0) { run.CheckpointPending = true; }
            Checkpoint(run);
        }
        sink.Send(new Response { Type = ResponseType.Record, RunId = run.RunId, Action = run.Action, Record = record });
    }

    /// <summary>
    /// Saves the in-progress copy when a result record arrived since the last save, at most once
    /// per <see cref="CheckpointInterval"/>; a pending save goes out with a later record or with
    /// the final result. So a run cut off by a power loss keeps its findings and changes.
    /// Caller holds run.Sync, so a checkpoint can never overwrite the final result.
    /// </summary>
    private void Checkpoint(RunState run)
    {
        if (!run.CheckpointPending) { return; }
        if (run.SinceCheckpoint != null && run.SinceCheckpoint.Elapsed < CheckpointInterval) { return; }
        try { _results.Save(run.Sid, run.Info); }
        catch (Exception ex) { _log.Warn($"Run {run.RunId}: could not save the run in progress: {ex.Message}"); }
        run.CheckpointPending = false;
        run.SinceCheckpoint = Stopwatch.StartNew();
    }

    /// <summary>
    /// Adds a record to the stored result within <see cref="MaxStoredRecordChars"/>. When it does
    /// not fit, older records of a lower rank make room, lowest rank and oldest first; order is
    /// kept. Records other than the closing ones leave <see cref="ClosingRecordReserveChars"/>
    /// free. A record that still does not fit is not stored. Caller holds run.Sync.
    /// </summary>
    private static void StoreRecord(RunState run, int rank, string record)
    {
        var records = run.Info.Records;
        var limit = rank == RankClosing ? MaxStoredRecordChars : MaxStoredRecordChars - ClosingRecordReserveChars;
        if (run.StoredChars + record.Length > limit)
        {
            run.Info.RecordsTruncated = true;
            // Closing records may push out anything but other closing records.
            for (var victim = RankInfoProgress; victim < rank && run.StoredChars + record.Length > limit; victim++)
            {
                for (var i = 0; i < records.Count && run.StoredChars + record.Length > limit; i++)
                {
                    if (run.StoredRanks[i] != victim) { continue; }
                    run.StoredChars -= records[i].Length;
                    lock (records) { records.RemoveAt(i); }
                    run.StoredRanks.RemoveAt(i);
                    i--;
                }
            }
            if (run.StoredChars + record.Length > limit) { return; }
        }
        lock (records) { records.Add(record); }
        run.StoredRanks.Add(rank);
        run.StoredChars += record.Length;
    }
    private string StoreProducedFile(string sid, string outFolder, string name, string runId, out string hash)
    {
        hash = null;
        if (!TimedRegex.IsMatch(FileNamePattern, name ?? "") || FileSystemSafety.IsReservedDeviceName(name))
        {
            _log.Warn($"Run {runId}: the engine named an invalid output file; ignored.");
            return null;
        }
        try
        {
            var source = Path.Combine(outFolder, name);
            // Opening with the no-follow checks rejects links and hard-linked files.
            using (var stream = FileSystemSafety.OpenReadNoFollow(source))
            using (var sha = SHA256.Create())
            {
                hash = ToHex(sha.ComputeHash(stream));
            }
            _results.StoreFile(sid, source, name);
            return name;
        }
        catch (Exception ex)
        {
            hash = null;
            _log.Warn($"Run {runId}: the output file was rejected: {ex.Message}");
            return null;
        }
    }

    /// <summary>Streams a stored file to the client: FileStart, chunks, FileEnd.</summary>
    public static void SendFile(IResponseSink sink, string path, string name, string runId, string knownHash = null)
    {
        if (path == null) { return; }
        using var stream = FileSystemSafety.OpenReadNoFollow(path);
        var hash = knownHash;
        if (hash == null)
        {
            using var sha = SHA256.Create();
            hash = ToHex(sha.ComputeHash(stream));
            stream.Position = 0;
        }
        if (!sink.Send(new Response { Type = ResponseType.FileStart, RunId = runId, FileName = name, FileLength = stream.Length, FileSha256 = hash })) { return; }
        var buffer = new byte[ProtocolInfo.FileChunkBytes];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (!sink.Send(new Response { Type = ResponseType.FileChunk, RunId = runId, Data = Convert.ToBase64String(buffer, 0, read) })) { return; }
        }
        sink.Send(new Response { Type = ResponseType.FileEnd, RunId = runId, FileName = name });
    }

    /// <summary>
    /// Stores the result and frees the worker, then tells the client. Runs at most once per run,
    /// so a later failure (for example while sending the package) cannot overwrite the result.
    /// </summary>
    private void Finish(RunState run, CallerContext caller, IResponseSink sink, ResultCode result, int? exitCode, string fileName, string message, string fileHash = null)
    {
        LastResultInfo final;
        lock (run.Sync)
        {
            if (run.Finished) { return; }
            run.Finished = true;
            run.Closed = true;
            final = run.Info;
            final.EndedUtc = Iso(DateTime.UtcNow);
            final.Result = result;
            final.ExitCode = exitCode;
            final.FileName = fileName;
            final.FileSha256 = fileHash;
            final.InProgress = false;
        }

        // A few attempts: a result left "in progress" on disk would later read as interrupted.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _results.Save(caller.Sid, final);
                break;
            }
            catch (Exception ex) when (attempt < FinalSaveAttempts)
            {
                _log.Warn($"Run {final.RunId}: could not store the result (attempt {attempt}): {ex.Message}");
                Thread.Sleep(FinalSaveRetryDelay);
            }
            catch (Exception ex)
            {
                _log.Warn($"Run {final.RunId}: could not store the result: {ex.Message}");
                break;
            }
        }

        // Other users must not see "busy" while this user's package downloads.
        Release(run);

        // The device's readouts are read again before the end is reported, so the app's checks
        // after the run show the device as the run left it (not a cached answer from before it).
        if (result == ResultCode.Completed && _runEnded != null && !_stopping && ChangesTheDevice(final.Action))
        {
            try { _runEnded(final.Action); }
            catch (Exception ex) { _log.Warn($"Run {final.RunId}: the readouts could not be refreshed: {ex.Message}"); }
        }

        var text = $"{final.Action} ended: {result}{(exitCode.HasValue ? " (exit " + exitCode.Value.ToString(CultureInfo.InvariantCulture) + ")" : "")}. Run {final.RunId}.";
        _log.Info(text + (message != null ? " " + message : ""));
        var ok = result == ResultCode.Completed && exitCode is EngineExitCode.Success or EngineExitCode.NothingToRepair;
        var warning = result == ResultCode.Completed && exitCode is EngineExitCode.Warnings or EngineExitCode.CooldownActive or EngineExitCode.RestartPending;
        // Event IDs: 1000 also marks "nothing to repair" (69003), 1001 success or warnings, 1002 failure.
        _events.Write(
            ok ? (exitCode == EngineExitCode.NothingToRepair ? EndpointToolbelt.EventId.ActionStarted : EndpointToolbelt.EventId.ActionCompleted)
               : warning ? EndpointToolbelt.EventId.ActionCompleted : EndpointToolbelt.EventId.ActionFailed,
            ok ? EventLogEntryType.Information : EventLogEntryType.Warning,
            text);

        sink.Send(new Response { Type = ResponseType.Completed, Result = result, ExitCode = exitCode, RunId = final.RunId, Action = final.Action, FileName = fileName, Message = message });
    }

    /// <summary>An action that may change what the readouts show (not a read-only diagnosis or a support package).</summary>
    internal static bool ChangesTheDevice(ToolbeltAction action)
    {
        var info = ActionCatalog.Get(action);
        return !info.ReadOnly && !info.ProducesFile;
    }

    private void LogRejection(Request request, CallerContext caller, PolicyDecision decision)
    {
        // Throttled per user, so a flood of rejected requests cannot roll over the logs.
        if (!_rejectionLogLimiter.TryAcquire(caller.Sid, RejectionLogsPerWindow)) { return; }
        _log.Warn($"Request {request.Action} from {caller.Sid} rejected: {decision.Result}.");
        var id = decision.Result switch
        {
            ResultCode.ConfirmationRequired => EndpointToolbelt.EventId.ConfirmationMissing,
            ResultCode.RateLimited => EndpointToolbelt.EventId.RateLimited,
            ResultCode.MultiSessionBlocked => EndpointToolbelt.EventId.MultiSessionBlocked,
            ResultCode.ToolSwitchedOff => EndpointToolbelt.EventId.ToolSwitchedOff,
            _ => 0,
        };
        if (id != 0) { _events.Write(id, EventLogEntryType.Warning, $"{request.Action} rejected: {decision.Result}."); }
    }

    private static EngineRecordHeader TryReadHeader(string record)
    {
        try { return MessageFraming.Deserialize<EngineRecordHeader>(Encoding.UTF8.GetBytes(record)); }
        catch (Exception) { return null; }
    }

    private static string TryReadSeverity(string record)
    {
        try { return MessageFraming.Deserialize<EngineRecordSeverity>(Encoding.UTF8.GetBytes(record))?.Severity; }
        catch (Exception) { return null; }
    }

    private static string Iso(DateTime utc) => utc.ToString("o", CultureInfo.InvariantCulture);

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) { sb.Append(b.ToString("x2", CultureInfo.InvariantCulture)); }
        return sb.ToString();
    }
}
