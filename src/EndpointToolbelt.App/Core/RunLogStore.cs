using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// A check or tool that ran in the app itself, not in the service (Check Microsoft connections, the
/// Company Portal reset, the sign-in and Teams tools): it gets a run ID of its own when it starts
///, so its log and its ticket text point to that one attempt. The ID starts with
/// <see cref="IdPrefix"/>, which a service run ID (eight hexadecimal characters) never does.
/// </summary>
public sealed class LocalRun
{
    public const string IdPrefix = "local-";

    public string Name { get; set; } = "";
    public string RunId { get; set; } = "";
    public DateTime StartedUtc { get; set; }

    /// <summary>When the check ended, as the app saw it; null while it runs.</summary>
    public DateTime? EndedUtc { get; set; }

    /// <summary>The outcome in a few words ("Start requested", "Could not start"), written in the run log's header; empty for none.</summary>
    public string Outcome { get; set; } = "";

    public static LocalRun Start(string name, DateTime utcNow)
        => new() { Name = name ?? "", RunId = NewId(), StartedUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc) };

    /// <summary>"local-" and eight hexadecimal characters.</summary>
    public static string NewId() => IdPrefix + Guid.NewGuid().ToString("N").Substring(0, 8);

    public static bool IsLocalId(string runId) => runId != null && runId.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>A copy of this moment, for work off the window's thread.</summary>
    public LocalRun Snapshot() => new() { Name = Name, RunId = RunId, StartedUtc = StartedUtc, EndedUtc = EndedUtc, Outcome = Outcome };
}

/// <summary>A run log in the user's run history, as its file name describes it.</summary>
public sealed class StoredRunLog
{
    public string Path { get; set; }

    /// <summary>When the run started (UTC), from the file name; null for a log without one.</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>The action or check, from the file name; null when unknown.</summary>
    public string Action { get; set; }

    /// <summary>The run ID, from the file name; null when unknown.</summary>
    public string RunId { get; set; }

    /// <summary>When the file was last written (UTC).</summary>
    public DateTime WrittenUtc { get; set; }

    /// <summary>
    /// A log whose name does not carry a run ID (a run that had none, or a file from elsewhere):
    /// it is kept as history, with the fields it does not name unknown.
    /// </summary>
    public bool Historical => RunId == null;
}

/// <summary>
/// The user's run history: one log per run in %LOCALAPPDATA%\EndpointToolbelt\runs, named
/// &lt;start in UTC&gt;_&lt;action&gt;_&lt;run ID&gt;.log, so two runs of the same action never
/// replace each other and both can be read after the app is opened again. The same run written
/// again (a snapshot while it ran, then its end) replaces its own file. Every write goes through a
/// temporary file that is swapped in, so a write that is cut off never costs the previous log.
/// The history is bounded: the newest <see cref="KeepFiles"/> logs, at most
/// <see cref="KeepBytes"/> together; older ones are deleted after each write.
/// </summary>
public static class RunLogStore
{
    public const string FolderName = "runs";

    /// <summary>The number of run logs kept.</summary>
    public const int KeepFiles = 50;

    /// <summary>The size the run logs may take together (20 MB).</summary>
    public const long KeepBytes = 20L * 1024 * 1024;

    private const string TimeFormat = "yyyyMMdd-HHmmss'Z'";
    private const string NoId = "none";

    /// <summary>
    /// Writes the run's log into the history in <paramref name="folder"/> and trims the history.
    /// Returns the log's path.
    /// </summary>
    public static string Save(RunLogHeader header, IEnumerable<string> lines, IEnumerable<ResultCard> cards, string folder)
    {
        // One write at a time: the end of a run and "Open full log" may write the same run's file.
        lock (WriteLock)
        {
            Directory.CreateDirectory(folder);
            var path = PathFor(header, folder);
            // A snapshot taken while the run went on never replaces the log of its confirmed end.
            if (header.Running && HasConfirmedEnd(path)) { return path; }
            SupportPackage.WriteRunLog(header, lines, cards, path);
            Trim(folder, path);
            return path;
        }
    }

    /// <summary>The log's header has an "Ended" line (read from the first lines only).</summary>
    private static bool HasConfirmedEnd(string path)
    {
        try
        {
            if (!File.Exists(path)) { return false; }
            return File.ReadLines(path).Take(20).Any(l => l.StartsWith("Ended     : ", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static readonly object WriteLock = new();

    /// <summary>
    /// Where a run's log goes: the file this run already has (found by its ID, so a snapshot and
    /// the final log are one file even when the app and the service saw slightly different start
    /// times), else a new name. A run without an ID always gets a new file.
    /// </summary>
    internal static string PathFor(RunLogHeader header, string folder)
    {
        var id = IdPart(header.RunId);
        var stem = TimePart(header.StartUtc ?? header.NowUtc) + "_" + ActionPart(header) + "_";
        if (id.Length == 0) { return UserFiles.FreeName(folder, stem + NoId, ".log"); }
        var existing = Directory.GetFiles(folder, "*_" + id + ".log")
            .Where(p => Describe(p).RunId == id)
            .OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
        return existing ?? Path.Combine(folder, stem + id + ".log");
    }

    /// <summary>
    /// Copies the newest run log to last-run.log (the name earlier versions opened), through a
    /// temporary file that is swapped in.
    /// </summary>
    public static void CopyToLatest(string source, string latest)
    {
        var temp = latest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temp);
            FileSwap.Into(temp, latest);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Deletes the oldest run logs beyond the newest <paramref name="keepFiles"/> or beyond
    /// <paramref name="keepBytes"/> together, never <paramref name="keep"/> (the log just written),
    /// and temporary files of a write that was cut off more than a day ago. Returns the names deleted.
    /// </summary>
    internal static List<string> Trim(string folder, string keep, int keepFiles = KeepFiles, long keepBytes = KeepBytes)
    {
        var removed = new List<string>();
        var directory = new DirectoryInfo(folder);
        if (!directory.Exists) { return removed; }
        var keepFull = keep == null ? null : System.IO.Path.GetFullPath(keep);
        var logs = directory.GetFiles("*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();
        var count = 0;
        long total = 0;
        var full = false;
        foreach (var log in logs)
        {
            var isKept = keepFull != null && string.Equals(log.FullName, keepFull, StringComparison.OrdinalIgnoreCase);
            if (!isKept && (full || count >= keepFiles || total + log.Length > keepBytes))
            {
                // Everything older than the first log that does not fit goes too.
                full = true;
                if (TryDelete(log)) { removed.Add(log.Name); }
                continue;
            }
            count++;
            total += log.Length;
        }
        foreach (var temp in directory.GetFiles("*.tmp").Where(t => t.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)))
        {
            if (TryDelete(temp)) { removed.Add(temp.Name); }
        }
        return removed;
    }

    /// <summary>The run logs in the history, newest first.</summary>
    public static List<StoredRunLog> List(string folder)
    {
        if (!Directory.Exists(folder)) { return new List<StoredRunLog>(); }
        return Directory.GetFiles(folder, "*.log")
            .Select(Describe)
            .OrderByDescending(l => l.StartedUtc ?? l.WrittenUtc)
            .ThenByDescending(l => l.WrittenUtc)
            .ToList();
    }

    /// <summary>What a run log's file name says: start, action and run ID; unknown parts stay null.</summary>
    public static StoredRunLog Describe(string path)
    {
        var log = new StoredRunLog { Path = path };
        try { log.WrittenUtc = File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        var parts = System.IO.Path.GetFileNameWithoutExtension(path).Split('_');
        if (parts.Length != 3) { return log; }
        if (!DateTime.TryParseExact(parts[0], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var started))
        {
            return log;
        }
        log.StartedUtc = started;
        log.Action = parts[1].Length > 0 ? parts[1] : null;
        log.RunId = parts[2].Length > 0 && parts[2] != NoId && IdPart(parts[2]) == parts[2] ? parts[2] : null;
        return log;
    }

    /// <summary>The run's start as it appears in a file name: 20261015-120000Z.</summary>
    internal static string TimePart(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>The action as it appears in a file name: the action's enum name, or the check's name without spaces.</summary>
    internal static string ActionPart(RunLogHeader header)
    {
        var name = header.Outcome != null ? header.Outcome.Action.ToString() : header.Name;
        var part = Clean(name, allowHyphen: false);
        return part.Length > 0 ? part : "Check";
    }

    /// <summary>A check's name as its run logs carry it ("Check Microsoft connections" is "CheckMicrosoftconnections"); empty for none.</summary>
    internal static string KeyFor(string checkName) => Clean(checkName, allowHyphen: false);

    /// <summary>A run ID as it may appear in a file name (letters, digits and hyphens, at most 40); empty when there is none.</summary>
    internal static string IdPart(string runId) => Clean(runId, allowHyphen: true);

    private static string Clean(string text, bool allowHyphen)
    {
        if (string.IsNullOrEmpty(text)) { return ""; }
        var result = new StringBuilder();
        foreach (var c in text)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || (allowHyphen && c == '-')) { result.Append(c); }
            if (result.Length == 40) { break; }
        }
        return result.ToString();
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
