using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine;

/// <summary>Severity of a finding: what the run concluded.</summary>
public enum FindingLevel
{
    Error,
    Warning,
    Info,
    Ok,
}

/// <summary>One conclusion of a run.</summary>
public sealed class RunFinding
{
    public FindingLevel Level { get; set; }
    public string Id { get; set; }
    public string Title { get; set; }
    public string Detail { get; set; }
    public string NextStep { get; set; }
    public string Support { get; set; }
}

/// <summary>
/// One run of the engine: what the request asked for, what the run found and changed, and the
/// writers for both. Every conclusion reaches the service as a JSON record on standard output,
/// one object per line, carrying the run ID from the request file (lines without it are ignored
/// by the service). The same content goes to the engine log, with grep-able marker strings
/// (EndpointToolbelt:FINDING, :CHANGE, :SUMMARY, :RESTART-REQUIRED).
/// </summary>
public sealed class RunContext
{
    /// <summary>Kept free at the end of the run for the close-out and the result.</summary>
    public const int RunReserveMinutes = 10;

    /// <summary>Below this, a long step is not started at all.</summary>
    public const int MinStepMinutes = 5;

    /// <summary>Used when the request carries no time limit (tests, or an older service).</summary>
    public const int DefaultRunLimitMinutes = 120;

    private const int LogWriteAttempts = 4;

    private bool _redirectionGuardTried;

    private readonly List<RunFinding> _findings = new();
    private readonly List<string> _changes = new();
    private readonly TimeSpan _started;
    private CmTraceFile _log;

    public RunContext(EngineServices services, string runId = "test0000", string action = "Test")
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        RunId = runId;
        Action = action;
        _started = services.Clock.Elapsed;
        State = new StateStore(this);
    }

    public EngineServices Services { get; }
    public StateStore State { get; }

    public string RunId { get; }
    public string Action { get; }
    public string LogDirectory { get; set; } = "";
    public string OutputDirectory { get; set; } = "";

    /// <summary>-1 when unknown, which every gate treats as "others may be signed in".</summary>
    public int CallerSessionId { get; set; } = -1;

    public int OtherSessions { get; set; } = -1;
    public EngineSettings Config { get; set; } = EngineSettings.Resolve(null);
    public string ProductVersion { get; set; } = "0.0.0";

    /// <summary>The service's hard limit for this run in minutes; 0 when not given.</summary>
    public int TimeLimitMinutes { get; set; }

    /// <summary>Who asked for this run; what is returned to them holds no other user's data.</summary>
    public string CallerSid { get; set; } = "";

    /// <summary>Get a new IP address only: the interface index the user chose; 0 when none was given.</summary>
    public int InterfaceIndex { get; set; }

    /// <summary>
    /// The app name cache in the service's SYSTEM-only working folder, from the request; empty
    /// when the service did not give one (an older service), and then no cache is read or written.
    /// </summary>
    public string AppNameCacheFile { get; set; } = "";

    public IReadOnlyList<RunFinding> Findings => _findings;
    public IReadOnlyList<string> Changes => _changes;
    public bool RestartRequired { get; internal set; }

    /// <summary>After one failure the engine log is left alone for the rest of the run.</summary>
    public bool LogFailed { get; private set; }

    /// <summary>Size at which the engine log rolls over (tests use a small one).</summary>
    internal long LogMaxBytes { get; set; } = CmTraceFile.MaxBytes;

    /// <summary>Time this run has used, on the monotonic clock.</summary>
    public TimeSpan Elapsed => Services.Clock.Elapsed - _started;

    // --- Records ---------------------------------------------------------------------------------

    /// <summary>
    /// Writes one record: {"type":..., "run":..., "ts":..., fields...}. "ts" is the UTC moment the
    /// record was written, with milliseconds; the app shows it in the user's own time zone.
    /// </summary>
    public void WriteRecord(string type, params KeyValuePair<string, object>[] fields)
    {
        var ts = Services.Clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        Services.Output(RecordJson.Record(type, RunId, ts, fields));
    }

    public static KeyValuePair<string, object> Field(string name, object value) => new(name, value);

    /// <summary>A progress line: shown live in the app and written to the engine log.</summary>
    public void Log(string message, LogSeverity severity = LogSeverity.Info)
    {
        var text = PlainText(message);
        WriteRecord("progress", Field("severity", SeverityName(severity)), Field("text", text));
        PersistentLog(text, severity);
    }

    /// <summary>A step of an action, the same shape everywhere: "== Step 2/5: what it does ==".</summary>
    public void StepHeader(int number, int total, string title) =>
        Log(string.Format(CultureInfo.InvariantCulture, "== Step {0}/{1}: {2} ==", number, total, title));

    /// <summary>
    /// One conclusion of this run: what was measured, its evidence, and the next step. NextStep is
    /// what the user can do; Support is what only IT support can do (policies, assignments,
    /// enrollment), which the user can pass on. Nothing is added for a check that could not run;
    /// that is a Warning finding of its own.
    /// </summary>
    public void AddFinding(FindingLevel level, string id, string title, string detail = "", string nextStep = "", string support = "")
    {
        var finding = new RunFinding
        {
            Level = level,
            Id = id,
            Title = PlainText(title),
            Detail = PlainText(detail),
            NextStep = PlainText(nextStep),
            Support = PlainText(support),
        };
        _findings.Add(finding);
        var severity = LevelName(level);
        WriteRecord("finding",
            Field("severity", severity),
            Field("id", finding.Id),
            Field("title", finding.Title),
            Field("detail", finding.Detail),
            Field("next", finding.NextStep),
            Field("support", finding.Support));
        var logSeverity = level == FindingLevel.Error ? LogSeverity.Error : level == FindingLevel.Warning ? LogSeverity.Warning : LogSeverity.Info;
        // '|' separates the fields of this log line, so it is replaced inside them.
        var line = string.Format(CultureInfo.InvariantCulture, "EndpointToolbelt:FINDING sev={0} id={1} | {2} | {3} | {4}",
            severity, finding.Id, finding.Title.Replace('|', '/'), finding.Detail.Replace('|', '/'), finding.NextStep.Replace('|', '/'));
        if (finding.Support.Length > 0) { line += " | support: " + finding.Support.Replace('|', '/'); }
        PersistentLog(line, logSeverity);
    }

    /// <summary>One thing this run changed on the device.</summary>
    public void AddChange(string text)
    {
        var clean = PlainText(text);
        _changes.Add(clean);
        WriteRecord("change", Field("text", clean));
        PersistentLog("EndpointToolbelt:CHANGE " + clean);
    }

    public static string LevelName(FindingLevel level) => level switch
    {
        FindingLevel.Error => "ERROR",
        FindingLevel.Warning => "WARNING",
        FindingLevel.Info => "INFO",
        _ => "OK",
    };

    private static string SeverityName(LogSeverity severity) => severity switch
    {
        LogSeverity.Error => "error",
        LogSeverity.Warning => "warning",
        _ => "info",
    };

    // --- Text ------------------------------------------------------------------------------------

    /// <summary>One field: line breaks become one space, and the text is trimmed.</summary>
    public static string PlainText(string text)
    {
        if (text == null) { return ""; }
        var sb = new StringBuilder(text.Length);
        var inBreak = false;
        foreach (var ch in text)
        {
            if (ch == '\r' || ch == '\n')
            {
                if (!inBreak) { sb.Append(' '); }
                inBreak = true;
                continue;
            }
            inBreak = false;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// A moment the user is told about, as a token the app shows in the user's own clock:
    /// [[t:&lt;UTC&gt;;&lt;format&gt;]]. The engine runs as SYSTEM and does not share the user's
    /// clock or time zone (for example with time zone redirection on a session host).
    /// </summary>
    public static string UserTime(DateTime when, string format = "HH:mm") =>
        "[[t:" + when.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + ";" + format + "]]";

    private static readonly System.Text.RegularExpressions.Regex UserTimeToken = new(@"\[\[t:(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z);([^\]]{1,40})\]\]",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimedRegex.Limit);

    /// <summary>
    /// Text with every <see cref="UserTime"/> token written out in UTC ("2026-11-16 08:08 UTC"), for a
    /// file that leaves the window (a log copy in the support package), where no token is rendered.
    /// The text as it is when the tokens cannot be read in time.
    /// </summary>
    public static string UserTimesInUtc(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf("[[t:", StringComparison.Ordinal) < 0) { return text; }
        try
        {
            return UserTimeToken.Replace(text, m =>
            {
                if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
                {
                    return m.Value;
                }
                try { return utc.ToString(m.Groups[2].Value, CultureInfo.InvariantCulture) + " UTC"; }
                catch (FormatException) { return m.Value; }
            });
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return text;
        }
    }

    // --- Engine log ------------------------------------------------------------------------------

    /// <summary>
    /// Appends to &lt;LogDirectory&gt;\EndpointToolbelt.log. The service chose and checked the
    /// folder (owned by SYSTEM, Administrators or TrustedInstaller, not writable by users). The
    /// file is opened by handle and never written through a link or a hard link. A failure is
    /// reported once and never stops the run.
    /// </summary>
    public void PersistentLog(string message, LogSeverity severity = LogSeverity.Info)
    {
        if (string.IsNullOrEmpty(LogDirectory) || LogFailed) { return; }
        var type = severity == LogSeverity.Error ? 3 : severity == LogSeverity.Warning ? 2 : 1;
        try
        {
            _log ??= new CmTraceFile(Path.Combine(LogDirectory, ProductInfo.EngineLogFileName), LogMaxBytes);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    _log.Append("[" + RunId + "] " + message, type, "Engine." + Action, Services.Clock.Now, Services.ProcessId);
                    return;
                }
                // Only a sharing or lock violation (another writer) is worth a retry.
                catch (Exception ex) when (attempt < LogWriteAttempts && IsLockViolation(ex))
                {
                    Services.Clock.Sleep(TimeSpan.FromMilliseconds(40 * attempt));
                }
            }
        }
        catch (Exception ex)
        {
            LogFailed = true;
            WriteRecord("warning", Field("text", "The persistent log could not be written: " + ex.Message));
        }
    }

    private static bool IsLockViolation(Exception ex)
    {
        var code = ex is Win32Exception win32 ? win32.NativeErrorCode : ex is IOException ? ex.HResult & 0xFFFF : 0;
        return code == 32 || code == 33;
    }

    // --- Time plan -------------------------------------------------------------------------------

    /// <summary>
    /// Whole minutes that may still be spent on work: the run's limit, less the reserve for the
    /// close-out, less the time already used.
    /// </summary>
    public int MinutesLeft()
    {
        var limit = TimeLimitMinutes > 0 ? TimeLimitMinutes : DefaultRunLimitMinutes;
        return (int)Math.Floor(limit - RunReserveMinutes - Elapsed.TotalMinutes);
    }

    /// <summary>
    /// Minutes one long step may take: its own limit, or what is left of the run when that is
    /// less. 0 means there is no room left and the step must not start; that is logged here, so
    /// every caller says the same thing.
    /// </summary>
    public int StepLimit(int limitMinutes, string label)
    {
        var left = MinutesLeft();
        if (left < MinStepMinutes)
        {
            Log(string.Format(CultureInfo.InvariantCulture,
                "{0} was not started: this run has {1} left, and that time is kept for reporting the result. Run the repair again to continue.",
                label, TextFormat.Count(Math.Max(0, left), "minute")), LogSeverity.Warning);
            return 0;
        }
        return Math.Min(limitMinutes, left);
    }

    // --- Tools -----------------------------------------------------------------------------------

    /// <summary>Runs a tool (full path, captured pipes, own time limit); its lines go to the progress log.</summary>
    public ProcessResult RunTool(ProcessRequest request) => Services.Processes.Run(request, (severity, text) => Log(text, severity));

    /// <summary>Runs one allow-listed in-box cmdlet; its lines go to the progress log.</summary>
    public InboxResult RunInboxCmdlet(InboxCommand command, TimeSpan timeLimit, params string[] values) =>
        new InboxCmdletRunner(Services.Processes).Run(command, timeLimit, (severity, text) => Log(text, severity), values);

    /// <summary>
    /// Turns on RedirectionTrust enforcement for this process, so the kernel refuses to follow a
    /// junction or object-manager symlink planted by a less-trusted process while SYSTEM works in
    /// a user-writable folder. Best effort and done once: the policy is missing before Windows 10
    /// 1809, and a failure only means the per-handle checks stand alone.
    /// </summary>
    public void EnableRedirectionGuard()
    {
        if (_redirectionGuardTried) { return; }
        _redirectionGuardTried = true;
        bool enabled;
        try
        {
            enabled = Services.TurnOnRedirectionGuard();
        }
        catch (Exception)
        {
            enabled = false;
        }
        // Logged either way: the per-handle checks hold without it, but IT support should know.
        PersistentLog(enabled
            ? "RedirectionGuard is on for this run: the system refuses links planted by less-trusted processes."
            : "RedirectionGuard could not be turned on on this Windows build; the per-handle link checks still apply.");
    }

    internal int CountFindings(FindingLevel level) => _findings.Count(f => f.Level == level);
}
