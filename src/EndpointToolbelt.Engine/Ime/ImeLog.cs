using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.Ime;

/// <summary>A compiled app rule of the pattern table.</summary>
public sealed class ImeAppRule
{
    public string Id { get; set; }
    public string Phase { get; set; }
    public string Result { get; set; }
    public Regex Regex { get; set; }
}

/// <summary>The pattern table as the engine uses it.</summary>
/// <summary>One IME check-in rule of the pattern table.</summary>
public sealed class ImeCheckInRule
{
    public string Id { get; set; }
    public string Kind { get; set; }
    public string Error { get; set; } = "";
    public Regex Regex { get; set; }
}

public sealed class ImeAppRules
{
    public int Version { get; set; }
    public IReadOnlyList<string> AppWorkloadFiles { get; set; }
    public IReadOnlyList<ImeAppRule> AppRules { get; set; }
    public int CheckInRuleCount { get; set; }

    /// <summary>The log files the check-in rules read (wildcards).</summary>
    public IReadOnlyList<string> CheckInFiles { get; set; } = Array.Empty<string>();

    /// <summary>The check-in rules, in table order: Kind "success", "failure", "start", "recovered", "attempt" or "ignored", the Error a failure gives without a code group.</summary>
    public IReadOnlyList<ImeCheckInRule> CheckInRules { get; set; } = Array.Empty<ImeCheckInRule>();

    /// <summary>The agent log files the detection rules read (wildcards).</summary>
    public IReadOnlyList<string> DetectionFiles { get; set; } = Array.Empty<string>();

    /// <summary>The detection-script rules, in table order: Kind "script", "begin", "bitness", "exit" or "output".</summary>
    public IReadOnlyList<ImeCheckInRule> DetectionRules { get; set; } = Array.Empty<ImeCheckInRule>();

    /// <summary>The IME logs that hold its Win32 app check-ins with their policy lists (wildcards).</summary>
    public IReadOnlyList<string> AssignmentFiles { get; set; } = Array.Empty<string>();

    /// <summary>The rules that tie a policy list to its scope, in table order: Kind "policies", "session", "required" or "available".</summary>
    public IReadOnlyList<ImeCheckInRule> AssignmentRules { get; set; } = Array.Empty<ImeCheckInRule>();
}

/// <summary>
/// The newest run of one app's detection script, from the IME's agent log: only these facts,
/// never the script, its output or a path beyond the app ID and revision in its file name.
/// </summary>
public sealed class ImeDetectionRun
{
    public string AppId { get; set; }
    public int Revision { get; set; } = -1;
    public DateTime Utc { get; set; }

    /// <summary>True: run as a 32-bit process on 64-bit Windows; false: 64-bit; null: not recorded.</summary>
    public bool? Ran32Bit { get; set; }

    /// <summary>The script's exit code; null when the log has none.</summary>
    public int? ExitCode { get; set; }

    /// <summary>The characters the script wrote to its output; null when the log has none.</summary>
    public int? OutputLength { get; set; }

    /// <summary>The script wrote something: more than a line break (two characters); null when unknown.</summary>
    public bool? Output => OutputLength is int length ? length > 2 : null;

    /// <summary>The run says "not there": a non-zero exit code or no output (Intune needs both exit 0 and output).</summary>
    public bool NotPresent => (ExitCode is int code && code != 0) || Output == false;
}

/// <summary>The newest attempt of one app, from the IME's app log.</summary>
public sealed class ImeAppAttempt
{
    public string AppId { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Start, Download, Hash, Unzip, Install or Detection.</summary>
    public string Phase { get; set; } = "Start";

    /// <summary>Started, Progress, Done or Failed.</summary>
    public string Result { get; set; } = "Started";

    public string ExitCode { get; set; } = "";
    public long BytesDone { get; set; } = -1;
    public long BytesTotal { get; set; } = -1;
    public DateTime? BytesChangedUtc { get; set; }
    public bool? Detected { get; set; }

    /// <summary>When the install step of this attempt finished with success (the IME mapped its exit code as success); null otherwise.</summary>
    public DateTime? InstallDoneUtc { get; set; }

    public List<string> Rules { get; } = new();

    /// <summary>The newest record of the log that was read; "stuck" is measured against it.</summary>
    public DateTime? LogNewestUtc { get; set; }

    public ImeAppAttempt Copy() => (ImeAppAttempt)MemberwiseClone();
}

/// <summary>What the app log said about the apps that were looked up.</summary>
public sealed class ImeAttemptRead
{
    /// <summary>False when there is no log or no table: no evidence.</summary>
    public bool Available { get; set; }

    public int Files { get; set; }
    public Dictionary<string, ImeAppAttempt> Attempts { get; } = new(StringComparer.Ordinal);
    public string Reason { get; set; } = "";
    public DateTime? NewestUtc { get; set; }
}

/// <summary>A failed app's recorded failure and the IME's own next attempt (an estimate).</summary>
public sealed class ImeRetryEntry
{
    public DateTime FailedUtc { get; set; }
    public DateTime NextUtc { get; set; }
}

/// <summary>
/// IME log evidence (read-only): how far the newest attempt of each app got, and stuck
/// downloads. The IME's wording is not documented and changes between versions, so every
/// pattern lives in one table (ImeLogPatterns.json, shared with the service) and anything the
/// table does not recognise is "no evidence", never "OK".
/// </summary>
public static class ImeLog
{
    /// <summary>Phases in the order an attempt goes through them.</summary>
    public static readonly string[] PhaseOrder = { "Start", "Download", "Hash", "Unzip", "Install", "Detection" };

    public const int StuckDownloadMinutes = 15;
    public const int StuckStepMinutes = 30;

    /// <summary>At most this many of the newest log records are matched against the table.</summary>
    public const int MaxRecords = 30000;

    /// <summary>A failed app is retried by the IME on its own about this long after the failure it recorded (an estimate).</summary>
    public const int RetryHours = 24;

    public const string GuidPattern = "[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}";
    private static readonly Regex Guid = new(GuidPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly string[] GrsFormats = { "M/d/yyyy h:mm:ss tt", "M/d/yyyy H:mm:ss", "MM/dd/yyyy HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "yyyy-MM-dd HH:mm:ss" };

    private static readonly object CacheLock = new();
    private static string _cachedPath;
    private static ImeAppRules _cached;

    /// <summary>The pattern table, loaded and compiled once. Throws when it is missing or damaged: the callers turn that into "no evidence".</summary>
    public static ImeAppRules Rules(string patternFile)
    {
        lock (CacheLock)
        {
            if (_cached != null && _cachedPath == patternFile) { return _cached; }
            var table = ImeLogPatternTable.Load(patternFile) ?? throw new InvalidOperationException("the IME log pattern table could not be read");
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            var rules = table.AppWorkload.Select(r => new ImeAppRule { Id = r.Id ?? "", Phase = r.Phase ?? "", Result = r.Result ?? "", Regex = new Regex(r.Pattern ?? "", options, TimeSpan.FromSeconds(2)) }).ToList();
            if (rules.Count == 0) { throw new InvalidOperationException("the IME log pattern table has no app rules"); }
            var checkIn = table.CheckIn.Select(r => new ImeCheckInRule { Id = r.Id ?? "", Kind = r.Kind ?? "", Error = r.Error ?? "", Regex = new Regex(r.Pattern ?? "", options, TimeSpan.FromSeconds(2)) }).ToList();
            var detection = (table.Detection ?? new List<ImeLogRule>()).Select(r => new ImeCheckInRule { Id = r.Id ?? "", Kind = r.Kind ?? "", Regex = new Regex(r.Pattern ?? "", options, TimeSpan.FromSeconds(2)) }).ToList();
            var assignment = (table.Assignment ?? new List<ImeLogRule>()).Select(r => new ImeCheckInRule { Id = r.Id ?? "", Kind = r.Kind ?? "", Regex = new Regex(r.Pattern ?? "", options, TimeSpan.FromSeconds(2)) }).ToList();
            _cached = new ImeAppRules
            {
                Version = table.Version, AppWorkloadFiles = table.AppWorkloadFiles, AppRules = rules, CheckInRuleCount = table.CheckIn.Count, CheckInFiles = table.CheckInFiles, CheckInRules = checkIn,
                DetectionFiles = table.DetectionFiles ?? new List<string>(), DetectionRules = detection,
                AssignmentFiles = table.AssignmentFiles ?? new List<string>(), AssignmentRules = assignment,
            };
            _cachedPath = patternFile;
            return _cached;
        }
    }

    private static string Group(Match m, string name) => m.Groups[name] is Group g && g.Success ? g.Value : "";

    private static int Rank(string phase) => Array.IndexOf(PhaseOrder, phase);

    /// <summary>
    /// Applies one recognised record to an attempt. A line of an earlier phase never takes the
    /// attempt back, except a step that starts again: that is a new try, so what the earlier try
    /// recorded after that step is cleared.
    /// </summary>
    internal static void Update(ImeAppAttempt attempt, ImeAppRule rule, Match m, DateTime utc)
    {
        var newRank = Rank(rule.Phase);
        var oldRank = Rank(attempt.Phase);
        // The IME also runs detection before it installs; only a detection after an install counts.
        if (rule.Phase == "Detection" && oldRank < Rank("Install")) { return; }
        var finished = attempt.Result == "Done" || attempt.Result == "Failed";
        var again = rule.Result == "Started" && (newRank < oldRank || (newRank == oldRank && finished));
        if (newRank < oldRank && !again) { return; }
        if (again)
        {
            attempt.ExitCode = "";
            attempt.Detected = null;
            if (newRank <= Rank("Install")) { attempt.InstallDoneUtc = null; }
            if (rule.Phase == "Download")
            {
                attempt.BytesDone = -1;
                attempt.BytesTotal = -1;
                attempt.BytesChangedUtc = null;
            }
        }
        attempt.LastSeenUtc = utc;
        if (!attempt.Rules.Contains(rule.Id)) { attempt.Rules.Add(rule.Id); }
        attempt.Phase = rule.Phase;
        var code = Group(m, "code");
        if (code.Length > 0) { attempt.ExitCode = code; }
        // An exit code line only adds the code: it never undoes the verdict of the same step.
        if (!(rule.Result == "Progress" && newRank == oldRank && (attempt.Result == "Done" || attempt.Result == "Failed")))
        {
            attempt.Result = rule.Result;
        }
        if (rule.Phase == "Install")
        {
            // The proof an install finished with success, for "Intune does not detect this app".
            if (rule.Result == "Done") { attempt.InstallDoneUtc ??= utc; }
            else if (rule.Result == "Failed") { attempt.InstallDoneUtc = null; }
        }
        if (rule.Phase == "Detection")
        {
            var state = Group(m, "state");
            if (state == "Detected") { attempt.Detected = true; attempt.Result = "Done"; }
            else if (state == "NotDetected") { attempt.Detected = false; attempt.Result = "Failed"; }
        }
        var done = Group(m, "done");
        if (done.Length > 0 && long.TryParse(done, NumberStyles.Integer, CultureInfo.InvariantCulture, out var doneValue))
        {
            if (doneValue != attempt.BytesDone) { attempt.BytesChangedUtc = utc; }
            attempt.BytesDone = doneValue;
        }
        var total = Group(m, "total");
        if (total.Length > 0 && long.TryParse(total, NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalValue)) { attempt.BytesTotal = totalValue; }
    }

    /// <summary>
    /// The newest attempt per app ID from the IME's app log. Lines that do not name an app belong
    /// to the app the same thread last named. Only apps in <paramref name="appIds"/> are kept. A
    /// new block replaces an app's attempt only once it records a step beyond its start.
    /// </summary>
    public static ImeAttemptRead Attempts(RunContext run, IEnumerable<string> appIds, int days = 7, int maxSeconds = 20)
    {
        var result = new ImeAttemptRead();
        var started = run.Services.Clock.Elapsed;
        TimeSpan Elapsed() => run.Services.Clock.Elapsed - started;
        var wanted = new HashSet<string>((appIds ?? Enumerable.Empty<string>()).Where(i => !string.IsNullOrEmpty(i)).Select(i => i.ToLowerInvariant()), StringComparer.Ordinal);
        if (wanted.Count == 0) { result.Reason = "no apps to look up"; return result; }
        ImeAppRules table;
        try
        {
            table = Rules(run.Services.ImePaths.PatternFile);
        }
        catch (Exception)
        {
            result.Reason = "the pattern table could not be read";
            return result;
        }
        var notBefore = run.Services.Clock.UtcNow.AddDays(-days);
        CmTraceRead read = null;
        foreach (var filter in table.AppWorkloadFiles)
        {
            read = CmTraceReader.Read(run.Services.ImePaths.LogsFolder, filter, Elapsed, 3, 4L * 1024 * 1024, maxSeconds, notBefore);
            if (read.Files > 0) { break; }
        }
        if (read == null || read.Files == 0) { result.Reason = string.Format(CultureInfo.InvariantCulture, "no IME app log of the last {0} days could be read", days); return result; }
        result.Available = true;
        result.Files = read.Files;
        if (read.TimedOut) { result.Reason = "time limit reached"; }

        var threadApp = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Dictionary<string, ImeAppAttempt>(StringComparer.Ordinal);
        var records = read.Records;
        if (records.Count > MaxRecords) { records = records.GetRange(records.Count - MaxRecords, MaxRecords); }
        foreach (var record in records)
        {
            // Checked for every record: one record can hold the regex timeout once per rule.
            if (Elapsed().TotalSeconds >= maxSeconds) { result.Reason = "time limit reached"; break; }
            if (result.NewestUtc == null || record.Utc > result.NewestUtc) { result.NewestUtc = record.Utc; }
            foreach (var rule in table.AppRules)
            {
                Match m;
                // A pattern that times out on one message is no evidence for that message only.
                try { m = rule.Regex.Match(record.Message); }
                catch (RegexMatchTimeoutException) { if (Elapsed().TotalSeconds >= maxSeconds) { break; } continue; }
                if (!m.Success) { continue; }
                var id = Group(m, "app").ToLowerInvariant();
                var key = record.Thread.ToString(CultureInfo.InvariantCulture) + "|" + record.Source;
                if (rule.Phase == "Start")
                {
                    // A block (the IME's "subgraph": an app with its dependencies) starts a new
                    // attempt for every app it names.
                    var ids = Guid.Matches(Group(m, "apps")).Cast<Match>().Select(g => g.Value.ToLowerInvariant()).ToList();
                    if (id.Length > 0) { ids.Add(id); }
                    threadApp.Remove(key);
                    if (ids.Count == 1) { threadApp[key] = ids[0]; }
                    foreach (var blockId in ids)
                    {
                        if (wanted.Contains(blockId)) { pending[blockId] = new ImeAppAttempt { AppId = blockId, StartUtc = record.Utc, LastSeenUtc = record.Utc }; }
                    }
                    break;
                }
                // A line that names its app also tells which app its thread works on now.
                if (id.Length > 0) { threadApp[key] = id; }
                else if (threadApp.TryGetValue(key, out var known)) { id = known; }
                if (id.Length == 0 || !wanted.Contains(id)) { break; }
                if (pending.TryGetValue(id, out var fresh))
                {
                    // The new block recorded a step: from now on it is the newest attempt.
                    result.Attempts[id] = fresh;
                    pending.Remove(id);
                }
                else if (!result.Attempts.ContainsKey(id))
                {
                    // The start of this attempt is older than the part of the log that was read.
                    result.Attempts[id] = new ImeAppAttempt { AppId = id, StartUtc = record.Utc, LastSeenUtc = record.Utc };
                }
                Update(result.Attempts[id], rule, m, record.Utc);
                break;
            }
        }
        // An app whose only evidence is a block start: say that much rather than nothing.
        foreach (var pair in pending)
        {
            if (!result.Attempts.ContainsKey(pair.Key)) { result.Attempts[pair.Key] = pair.Value; }
        }
        foreach (var attempt in result.Attempts.Values) { attempt.LogNewestUtc = result.NewestUtc; }
        return result;
    }

    /// <summary>
    /// The newest detection-script run per app ID from the IME's agent log (and its rotated
    /// files): when, 32-bit or 64-bit, the exit code and how much output. A run is tied to its app by
    /// the script's file name (DetectionScripts\&lt;app&gt;_&lt;revision&gt;.ps1) and its lines by
    /// the thread that wrote them. Only apps in <paramref name="appIds"/> are kept; nothing else of
    /// the log is kept.
    /// </summary>
    public static Dictionary<string, ImeDetectionRun> DetectionRuns(RunContext run, IEnumerable<string> appIds, int days = 7, int maxSeconds = 10)
    {
        var result = new Dictionary<string, ImeDetectionRun>(StringComparer.Ordinal);
        var wanted = new HashSet<string>((appIds ?? Enumerable.Empty<string>()).Where(i => !string.IsNullOrEmpty(i)).Select(i => i.ToLowerInvariant()), StringComparer.Ordinal);
        if (wanted.Count == 0) { return result; }
        var started = run.Services.Clock.Elapsed;
        TimeSpan Elapsed() => run.Services.Clock.Elapsed - started;
        ImeAppRules table;
        try { table = Rules(run.Services.ImePaths.PatternFile); }
        catch (Exception) { return result; }
        if (table.DetectionRules.Count == 0) { return result; }
        var notBefore = run.Services.Clock.UtcNow.AddDays(-days);
        var records = new List<CmTraceRecord>();
        foreach (var filter in table.DetectionFiles)
        {
            var read = CmTraceReader.Read(run.Services.ImePaths.LogsFolder, filter, Elapsed, 3, 4L * 1024 * 1024, maxSeconds, notBefore);
            records.AddRange(read.Records);
        }
        return DetectionRuns(records.OrderBy(r => r.Utc), table.DetectionRules, wanted, () => Elapsed().TotalSeconds >= maxSeconds);
    }

    /// <summary>The detection runs of the records, oldest first (separate so it can be tested without files).</summary>
    internal static Dictionary<string, ImeDetectionRun> DetectionRuns(IEnumerable<CmTraceRecord> records, IReadOnlyList<ImeCheckInRule> rules, ISet<string> wanted, Func<bool> outOfTime = null)
    {
        var result = new Dictionary<string, ImeDetectionRun>(StringComparer.Ordinal);
        var current = new Dictionary<string, ImeDetectionRun>(StringComparer.Ordinal);
        var bitness = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (outOfTime != null && outOfTime()) { break; }
            var key = record.Thread.ToString(CultureInfo.InvariantCulture) + "|" + record.Source;
            foreach (var rule in rules)
            {
                Match m;
                try { m = rule.Regex.Match(record.Message ?? ""); }
                catch (RegexMatchTimeoutException) { continue; }
                if (!m.Success) { continue; }
                switch (rule.Kind)
                {
                    case "script":
                        var id = Group(m, "app").ToLowerInvariant();
                        if (!wanted.Contains(id)) { current.Remove(key); break; }
                        var detection = new ImeDetectionRun
                        {
                            AppId = id,
                            Revision = int.TryParse(Group(m, "rev"), NumberStyles.None, CultureInfo.InvariantCulture, out var rev) ? rev : -1,
                            Utc = record.Utc,
                        };
                        // The bitness line can come just before the script's command line.
                        if (bitness.TryGetValue(key, out var wow)) { detection.Ran32Bit = wow; bitness.Remove(key); }
                        current[key] = detection;
                        result[id] = detection;
                        break;
                    case "begin":
                        current.Remove(key);
                        bitness.Remove(key);
                        break;
                    case "bitness":
                        var is32 = string.Equals(Group(m, "wow"), "True", StringComparison.OrdinalIgnoreCase);
                        if (current.TryGetValue(key, out var open) && open.Ran32Bit == null) { open.Ran32Bit = is32; }
                        else { bitness[key] = is32; }
                        break;
                    case "exit":
                        if (current.TryGetValue(key, out var exited) && int.TryParse(Group(m, "code"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)) { exited.ExitCode = code; }
                        break;
                    case "output":
                        if (current.TryGetValue(key, out var wrote) && int.TryParse(Group(m, "length"), NumberStyles.None, CultureInfo.InvariantCulture, out var length)) { wrote.OutputLength = length; }
                        break;
                }
                break;
            }
        }
        return result;
    }

    /// <summary>
    /// Why an attempt looks stuck, or '' when it does not. Measured against the newest line of the
    /// log, not the clock: while the IME was not writing, nothing counts as stuck.
    /// </summary>
    public static string Stuck(ImeAppAttempt attempt, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? attempt.LogNewestUtc ?? DateTime.UtcNow;
        if (attempt.Result == "Done" || attempt.Result == "Failed") { return ""; }
        if (attempt.Phase == "Download")
        {
            if (attempt.BytesTotal > 0 && attempt.BytesDone >= attempt.BytesTotal) { return ""; }
            var since = attempt.BytesChangedUtc ?? attempt.LastSeenUtc;
            var minutes = (now - since).TotalMinutes;
            if (minutes >= StuckDownloadMinutes)
            {
                if (attempt.BytesTotal > 0 && attempt.BytesDone >= 0)
                {
                    return string.Format(CultureInfo.InvariantCulture, "the download has stayed at {0} of {1} MB for {2} min",
                        TextFormat.Number(attempt.BytesDone / 1048576d), TextFormat.Number(attempt.BytesTotal / 1048576d), (int)Math.Floor(minutes));
                }
                return string.Format(CultureInfo.InvariantCulture, "the download has not moved for {0} min", (int)Math.Floor(minutes));
            }
            return "";
        }
        if (attempt.Phase == "Hash" || attempt.Phase == "Unzip")
        {
            var minutes = (now - attempt.LastSeenUtc).TotalMinutes;
            if (minutes >= StuckStepMinutes)
            {
                var what = attempt.Phase == "Unzip" ? "unpacking the download" : "the hash check";
                return string.Format(CultureInfo.InvariantCulture, "{0} started {1} min ago and did not finish", what, (int)Math.Floor(minutes));
            }
        }
        return "";
    }

    /// <summary>One short phrase for an attempt, for the finding detail and the summary sheet.</summary>
    public static string FormatPhase(ImeAppAttempt attempt)
    {
        var mb = attempt.BytesTotal > 0 && attempt.BytesDone >= 0
            ? " (" + TextFormat.Number(attempt.BytesDone / 1048576d) + " of " + TextFormat.Number(attempt.BytesTotal / 1048576d) + " MB)"
            : "";
        switch (attempt.Phase)
        {
            case "Start":
                return "started, no step recorded yet";
            case "Download":
                return attempt.Result == "Failed" ? "download failed" + mb : attempt.Result == "Done" ? "downloaded" + mb : "downloading" + mb;
            case "Hash":
                return attempt.Result == "Failed" ? "hash check failed (the download does not match the package)" : attempt.Result == "Done" ? "hash check passed" : "checking the download (hash)";
            case "Unzip":
                return attempt.Result == "Failed" ? "unpacking failed" : attempt.Result == "Done" ? "unpacked" : "unpacking";
            case "Install":
            {
                var code = attempt.ExitCode.Length > 0 ? " with exit code " + attempt.ExitCode : "";
                return attempt.Result == "Failed" ? "install failed" + code : attempt.Result == "Done" ? "installer finished" + code : "installing";
            }
            case "Detection":
                return attempt.Detected == false ? "installed, but not detected after the install" : attempt.Detected == true ? "detected after the install" : "checking detection";
            default:
                return "no evidence";
        }
    }

    /// <summary>
    /// For the apps that need attention or are still in progress: logs how far each one's newest
    /// attempt got, adds a finding per stuck download, and returns the attempts of the apps that
    /// need attention (keyed by app ID). Never throws.
    /// </summary>
    public static Dictionary<string, ImeAppAttempt> WriteEvidence(RunContext run, IReadOnlyList<AppStateRow> rows, IReadOnlyList<AppStateRow> problems, ISet<string> exclude)
    {
        var attempts = new Dictionary<string, ImeAppAttempt>(StringComparer.Ordinal);
        try
        {
            var watch = rows.Where(r => r.Verdict == "IN-PROGRESS" || r.Verdict == "PENDING").Select(r => r.AppId);
            var ids = problems.Select(p => p.AppId).Concat(watch).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // An app that another user also has: a log line could be their attempt, not this one.
            var shared = ids.Where(i => exclude != null && exclude.Contains(i)).ToList();
            if (shared.Count > 0)
            {
                run.Log("IME log: no evidence shown for " + TextFormat.Count(shared.Count, "app") +
                    ", because another user on this device has it too and the log does not always say whose attempt a line is.");
                ids = ids.Where(i => !exclude.Contains(i)).ToList();
            }
            if (ids.Count == 0) { return attempts; }
            var evidence = Attempts(run, ids);
            if (!evidence.Available)
            {
                run.Log("IME log: no evidence of the newest attempts (" + evidence.Reason + ").");
                return attempts;
            }
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in rows) { if (!string.IsNullOrEmpty(row.Name)) { names[row.AppId] = row.Name; } }
            var problemIds = new HashSet<string>(problems.Select(p => p.AppId), StringComparer.Ordinal);
            var found = 0;
            foreach (var id in ids)
            {
                if (!evidence.Attempts.TryGetValue(id, out var attempt)) { continue; }
                found++;
                var label = names.TryGetValue(id, out var name) ? name : id.Substring(0, 8);
                run.Log("  " + label + ": newest attempt " + RunContext.UserTime(attempt.LastSeenUtc, "yyyy-MM-dd HH:mm") + ", " + FormatPhase(attempt) + ".");
                if (problemIds.Contains(id)) { attempts[id] = attempt; }
                var stuck = Stuck(attempt);
                if (stuck.Length > 0)
                {
                    run.AddFinding(FindingLevel.Warning, "APP-DOWNLOAD-STUCK", label + ": the download looks stuck",
                        "In the IME log, " + stuck + ".",
                        "Use Clear downloaded app content, then Retry work apps and settings. If it gets stuck again, check the network or VPN and contact your IT support.",
                        "Check Delivery Optimization and the network path to the content (proxy, VPN split tunnel, bandwidth limits); AppWorkload.log in a support package shows the download.");
                }
            }
            var suffix = evidence.Reason.Length > 0 ? " (" + evidence.Reason + ")" : "";
            run.Log(string.Format(CultureInfo.InvariantCulture, "IME log: {0} of {1} found in {2}{3}.", found, TextFormat.Count(ids.Count, "app"), TextFormat.Count(evidence.Files, "log file"), suffix));
        }
        catch (Exception ex)
        {
            run.Log("The IME log could not be read for app attempts: " + ex.Message, LogSeverity.Warning);
        }
        return attempts;
    }

    /// <summary>A GRS timestamp (UTC), or null when it is empty, the zero date or not a time.</summary>
    public static DateTime? ParseGrsTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        if (!DateTime.TryParseExact(text.Trim(), GrsFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) { return null; }
        if (parsed.Year < 2000) { return null; }
        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    /// <summary>
    /// The IME's recorded retry times (GRS) per app, for the scopes this user may see. GRS keys are
    /// named after a hash of the app's block; the app ID is in a value name (newer IME versions) or
    /// the key name (older ones), the time in the value.
    /// </summary>
    public static Dictionary<string, ImeRetryEntry> RetrySchedule(RunContext run)
    {
        var map = new Dictionary<string, ImeRetryEntry>(StringComparer.Ordinal);
        var registry = run.Services.Registry;
        var root = run.Services.ImePaths.Win32AppsKey;
        if (!registry.KeyExists(root)) { return map; }
        var visible = AppState.VisibleScopes(run);
        foreach (var scope in SafeSubKeys(registry, root))
        {
            if (!visible.Contains(scope.ToLowerInvariant())) { continue; }
            var grs = root + "\\" + scope + "\\GRS";
            if (!registry.KeyExists(grs)) { continue; }
            foreach (var entry in SafeSubKeys(registry, grs))
            {
                var entryKey = grs + "\\" + entry;
                IReadOnlyList<string> names;
                try { names = registry.ValueNames(entryKey); }
                catch (Exception) { continue; }
                var keyMatch = Guid.Match(entry);
                var keyId = keyMatch.Success ? keyMatch.Value.ToLowerInvariant() : "";
                foreach (var name in names)
                {
                    var nameMatch = Guid.Match(name);
                    var id = nameMatch.Success ? nameMatch.Value.ToLowerInvariant() : keyId;
                    if (id.Length == 0) { continue; }
                    var failed = ParseGrsTime(MachineRegistry.AsText(SafeValue(registry, entryKey, name)));
                    if (failed == null) { continue; }
                    if (!map.TryGetValue(id, out var known) || failed > known.FailedUtc)
                    {
                        map[id] = new ImeRetryEntry { FailedUtc = failed.Value, NextUtc = failed.Value.AddHours(RetryHours) };
                    }
                }
            }
        }
        return map;
    }

    internal static IReadOnlyList<string> SafeSubKeys(IRegistry registry, string key)
    {
        try { return registry.SubKeyNames(key); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    internal static object SafeValue(IRegistry registry, string key, string name)
    {
        try { return registry.GetValue(key, name); }
        catch (Exception) { return null; }
    }
}
