using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace EndpointToolbelt.App.Core;

/// <summary>How a run in the run history ended, as its log says; always shown as a word.</summary>
public enum RunVerdict
{
    /// <summary>No run, or a log that could not be read.</summary>
    None,
    Ok,
    Check,
    Problem,

    /// <summary>The log was written while the run went on ("still running").</summary>
    Running,

    /// <summary>The log has no confirmed end ("Captured at"): the run stopped without its summary.</summary>
    NotFinished,

    /// <summary>
    /// A confirmed end without a result that says OK, a point to check or a problem. Fix 37: never
    /// shown as a word ("ENDED" says nothing); the run's outcome in words stands in for it
    /// where its log names one, else nothing is said.
    /// </summary>
    Ended,

    /// <summary>
    /// A start or reset the app requested and Windows accepted ("Start requested", "Reset
    /// requested"), with no result of its own: no badge, never "ENDED".
    /// </summary>
    Requested,
}

/// <summary>The newest run of one tool in the run history.</summary>
public sealed class ToolRun
{
    public StoredRunLog Log { get; set; }
    public RunVerdict Result { get; set; }

    /// <summary>The outcome in words from the run log's header ("Start requested", "Could not start"); empty when the log has none.</summary>
    public string Outcome { get; set; } = "";
}

/// <summary>
/// The last runs of the tools of For IT, from the run history: each tool's newest run
/// log (by the name its logs carry), the result its log states, and a summary per group. Only the
/// log's header and result cards are read, never the technical log.
/// </summary>
public static class ToolRuns
{
    private const string ResultSection = "----- RESULT -----";
    private const string TechnicalSection = "----- TECHNICAL LOG";

    private const string OutcomeLine = "Outcome   : ";

    /// <summary>
    /// The result in words (never colour alone). A requested start or reset has no word (no
    /// badge); fix 37: nor has a run that only ended, which shows its outcome in words instead.
    /// </summary>
    public static string Word(RunVerdict result) => result switch
    {
        RunVerdict.Ok => "OK",
        RunVerdict.Check => Words.WarningBadge,
        RunVerdict.Problem => "PROBLEM",
        RunVerdict.Running => "RUNNING",
        RunVerdict.NotFinished => "NOT FINISHED",
        _ => "",
    };

    /// <summary>
    /// The result as Recent runs says it, in sentence case ("OK", "Warning", "Not finished"); a
    /// requested start says "Requested"; fix 37: a run that only ended says nothing of its own.
    /// </summary>
    public static string Phrase(RunVerdict result) => result switch
    {
        RunVerdict.Ok => "OK",
        RunVerdict.Check => Words.Warning,
        RunVerdict.Problem => "Problem",
        RunVerdict.Running => "Running",
        RunVerdict.NotFinished => "Not finished",
        RunVerdict.Requested => "Requested",
        _ => "",
    };

    public static Tone ToneOf(RunVerdict result) => result switch
    {
        RunVerdict.Ok => Tone.Success,
        RunVerdict.Check or RunVerdict.NotFinished => Tone.Warning,
        RunVerdict.Problem => Tone.Error,
        _ => Tone.Neutral,
    };

    /// <summary>
    /// The result a run log states: still running; a problem or a point to check among its result
    /// cards; no confirmed end; an OK card; a requested start or reset (a "REQUESTED" card);
    /// else only that it ended. "OK" needs an OK card: a run whose log says nothing about success is
    /// never shown as one.
    /// </summary>
    public static RunVerdict Parse(IEnumerable<string> lines)
    {
        bool running = false, ended = false, inResult = false, ok = false, check = false, problem = false, requested = false;
        foreach (var line in lines ?? Enumerable.Empty<string>())
        {
            if (line == null) { continue; }
            if (line.StartsWith(TechnicalSection, StringComparison.Ordinal)) { break; }
            if (line.StartsWith(ResultSection, StringComparison.Ordinal)) { inResult = true; continue; }
            if (!inResult)
            {
                if (line.StartsWith("Result    : still running", StringComparison.Ordinal)) { running = true; }
                if (line.StartsWith("Ended     : ", StringComparison.Ordinal)) { ended = true; }
                continue;
            }
            if (!line.StartsWith("[", StringComparison.Ordinal)) { continue; }
            var close = line.IndexOf(']');
            if (close < 0) { continue; }
            switch (line.Substring(1, close - 1))
            {
                case "PROBLEM": problem = true; break;
                case Words.EarlierWarningBadge: check = true; break;
                case var word when word == Words.WarningBadge: check = true; break;
                case "OK": ok = true; break;
                case "REQUESTED": requested = true; break;
            }
        }
        if (running) { return RunVerdict.Running; }
        if (problem) { return RunVerdict.Problem; }
        if (check) { return RunVerdict.Check; }
        if (!ended) { return RunVerdict.NotFinished; }
        if (ok) { return RunVerdict.Ok; }
        return requested ? RunVerdict.Requested : RunVerdict.Ended;
    }

    /// <summary>The outcome in words that a run log's header names ("Outcome   : Start requested"); empty when it names none.</summary>
    public static string ParseOutcome(IEnumerable<string> lines)
    {
        foreach (var line in lines ?? Enumerable.Empty<string>())
        {
            if (line == null) { continue; }
            if (line.StartsWith(ResultSection, StringComparison.Ordinal) || line.StartsWith(TechnicalSection, StringComparison.Ordinal)) { break; }
            if (line.StartsWith(OutcomeLine, StringComparison.Ordinal)) { return line.Substring(OutcomeLine.Length).Trim(); }
        }
        return "";
    }

    private const string ExitLine = "Exit code : ";

    /// <summary>
    /// Fix 37: an engine run's outcome in words from its header's exit code, as the app's result line
    /// says it ("Nothing to repair", "Skipped: cooldown active", "Blocked: restart pending",
    /// "Completed"); empty when the log has no exit code or the code has no meaning of its own.
    /// </summary>
    public static string ParseExitOutcome(IEnumerable<string> lines)
    {
        foreach (var line in lines ?? Enumerable.Empty<string>())
        {
            if (line == null) { continue; }
            if (line.StartsWith(ResultSection, StringComparison.Ordinal) || line.StartsWith(TechnicalSection, StringComparison.Ordinal)) { break; }
            if (!line.StartsWith(ExitLine, StringComparison.Ordinal)) { continue; }
            if (!int.TryParse(line.Substring(ExitLine.Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var exit)) { return ""; }
            var words = RunWords.Exit(exit);
            return string.IsNullOrEmpty(words) ? "" : char.ToUpperInvariant(words[0]) + words.Substring(1);
        }
        return "";
    }

    /// <summary>
    /// Fix 37: the outcome in words of a run: the one its header names, else for a run that
    /// only ended the one its exit code says; empty when neither says anything.
    /// </summary>
    public static string OutcomeOf(RunVerdict result, IEnumerable<string> lines)
    {
        var list = (lines ?? Enumerable.Empty<string>()).ToList();
        var outcome = ParseOutcome(list);
        return outcome.Length == 0 && result == RunVerdict.Ended ? ParseExitOutcome(list) : outcome;
    }

    /// <summary>The result of the run log at <paramref name="path"/>; <see cref="RunVerdict.None"/> when it cannot be read.</summary>
    public static RunVerdict Read(string path) => ReadRun(path).Result;

    /// <summary>The result and the outcome in words of the run log at <paramref name="path"/>; none when it cannot be read.</summary>
    public static (RunVerdict Result, string Outcome) ReadRun(string path)
    {
        try
        {
            if (!File.Exists(path)) { return (RunVerdict.None, ""); }
            var lines = File.ReadLines(path).Take(400).ToList();
            var result = Parse(lines);
            return (result, OutcomeOf(result, lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (RunVerdict.None, "");
        }
    }

    /// <summary>
    /// The newest run of every tool that leaves runs, by the tool's button name. A tool that has
    /// none in the history is left out. <paramref name="read"/> gives a log's result;
    /// <paramref name="outcome"/> its outcome in words (none when null).
    /// </summary>
    public static Dictionary<string, ToolRun> Latest(IEnumerable<StoredRunLog> logs, Func<string, RunVerdict> read, Func<string, string> outcome = null)
    {
        var result = new Dictionary<string, ToolRun>(StringComparer.Ordinal);
        var newestFirst = (logs ?? Enumerable.Empty<StoredRunLog>()).OrderByDescending(l => l.StartedUtc ?? l.WrittenUtc).ThenByDescending(l => l.WrittenUtc).ToList();
        foreach (var tool in ToolCatalog.All)
        {
            if (tool.LogKey.Length == 0) { continue; }
            var log = newestFirst.FirstOrDefault(l => tool.OwnsLogKey(l.Action));
            if (log == null) { continue; }
            result[tool.Button] = new ToolRun { Log = log, Result = read == null ? RunVerdict.None : read(log.Path), Outcome = outcome?.Invoke(log.Path) ?? "" };
        }
        return result;
    }

    /// <summary>Whether a run is recent enough for its result to stand on a tool row or tile (the last 24 hours).</summary>
    public static bool IsRecent(ToolRun run, DateTime nowUtc)
    {
        var when = run?.Log.StartedUtc ?? run?.Log.WrittenUtc;
        return when != null && nowUtc - when.Value <= RowResultFor;
    }

    /// <summary>The last runs of the last 24 hours only, for the tiles.</summary>
    public static IReadOnlyDictionary<string, ToolRun> Recent(IReadOnlyDictionary<string, ToolRun> latest, DateTime nowUtc)
        => latest == null ? new Dictionary<string, ToolRun>() : latest.Where(p => IsRecent(p.Value, nowUtc)).ToDictionary(p => p.Key, p => p.Value);

    /// <summary>
    /// A group's summary on its tile: its number of tools, then the problems and points to
    /// check among its tools' last runs, else how its newest run ended ("4 tools · last run OK").
    /// A requested start or reset adds nothing (no badge on the tile). Fix 37: a run that only
    /// ended says its outcome in words ("7 tools · last run not confirmed", "4 tools · last run nothing
    /// to repair"), or nothing when its log names none; never "ended".
    /// </summary>
    /// <param name="switchedOff">The tools the organization switched off: not counted, and their runs not summed up.</param>
    public static string GroupSummary(ToolGroup group, IReadOnlyDictionary<string, ToolRun> latest, IEnumerable<string> switchedOff = null)
    {
        var tools = ToolCatalog.InGroup(group, switchedOff);
        var text = Words.Count(tools.Count, "tool");
        var runs = tools.Where(t => latest != null && latest.ContainsKey(t.Button)).Select(t => latest[t.Button]).ToList();
        var problems = runs.Count(r => r.Result == RunVerdict.Problem);
        var checks = runs.Count(r => r.Result == RunVerdict.Check);
        if (problems > 0) { text += " · " + Words.Count(problems, "problem"); }
        if (checks > 0) { text += " · " + Words.WarningCount(checks); }
        if (problems > 0 || checks > 0) { return text; }
        var newest = runs.OrderByDescending(r => r.Log.StartedUtc ?? r.Log.WrittenUtc).FirstOrDefault();
        return newest?.Result switch
        {
            RunVerdict.Ok => text + " · last run OK",
            RunVerdict.Running => text + " · one running",
            RunVerdict.NotFinished => text + " · last run not finished",
            RunVerdict.Ended when !string.IsNullOrEmpty(newest.Outcome) => text + " · last run " + char.ToLowerInvariant(newest.Outcome[0]) + newest.Outcome.Substring(1),
            _ => text,
        };
    }

    private enum WhenDay { Today, Yesterday, Date, NoZone }

    /// <summary>
    /// A moment on screen, in parts: the local calendar day compared with today's after the zone
    /// conversion (never "less than 24 hours ago", so midnight and the daylight saving days are
    /// right), the date in English whatever the Windows language ("15 Nov", with the year when it is
    /// not this year or lies ahead), and the time. Without a usable zone: UTC with the year.
    /// </summary>
    private static (WhenDay Day, string Date, string Time) WhenParts(DateTime utc, TimeZoneInfo zone, DateTime nowUtc)
    {
        zone ??= TimeZoneInfo.Local;
        if (!TimeText.TryLocal(utc, zone, out var local)) { return (WhenDay.NoZone, utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC", ""); }
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (!TimeText.TryLocal(nowUtc, zone, out var now)) { return (WhenDay.Date, local.ToString("d MMM yyyy", CultureInfo.InvariantCulture), time); }
        if (local.Date == now.Date) { return (WhenDay.Today, "", time); }
        if (local.Date == now.Date.AddDays(-1)) { return (WhenDay.Yesterday, "", time); }
        var withYear = local.Year != now.Year || local.Date > now.Date;
        return (WhenDay.Date, local.ToString(withYear ? "d MMM yyyy" : "d MMM", CultureInfo.InvariantCulture), time);
    }

    /// <summary>
    /// A short local time on screen (a tool row, "Last checked"): "today 14:05", "yesterday 14:05",
    /// "10 Nov 14:05" ("28 Dec 2025 14:05" in another year). Screen only: text that is copied or
    /// saved uses <see cref="FullWhen"/>.
    /// </summary>
    public static string ShortWhen(DateTime utc, TimeZoneInfo zone, DateTime nowUtc)
    {
        var (day, date, time) = WhenParts(utc, zone, nowUtc);
        return day switch
        {
            WhenDay.Today => "today " + time,
            WhenDay.Yesterday => "yesterday " + time,
            WhenDay.Date => date + " " + time,
            _ => date,
        };
    }

    /// <summary>The time as Recent runs starts a line: "10:42" today, else as <see cref="ShortWhen"/> ("yesterday 10:42", "10 Nov 10:42").</summary>
    public static string ClockTime(DateTime utc, TimeZoneInfo zone, DateTime nowUtc)
    {
        var parts = WhenParts(utc, zone, nowUtc);
        return parts.Day == WhenDay.Today ? parts.Time : ShortWhen(utc, zone, nowUtc);
    }

    /// <summary>
    /// The full local date and time ("2026-11-10 14:05"), for every text that leaves the screen (a
    /// ticket read days later) and for the tooltip and automation name beside a short time.
    /// </summary>
    public static string FullWhen(DateTime utc, TimeZoneInfo zone)
    {
        zone ??= TimeZoneInfo.Local;
        return TimeText.TryLocal(utc, zone, out var local)
            ? local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
    }

    /// <summary>When the run in a tool row started, in full (<see cref="FullWhen"/>); null when it has not run here.</summary>
    public static string RowFullWhen(ToolInfo tool, ToolRun run, TimeZoneInfo zone)
        => tool.OpensOnly || run == null ? null : FullWhen(run.Log.StartedUtc ?? run.Log.WrittenUtc, zone);

    /// <summary>The tool row's line with the full date and time, for its automation name: "OK · 2026-11-15 19:03".</summary>
    public static string RowTextFull(ToolInfo tool, ToolRun run, TimeZoneInfo zone)
        => RowLine(tool, run, r => FullWhen(r.Log.StartedUtc ?? r.Log.WrittenUtc, zone));

    /// <summary>
    /// A run as Recent runs shows it, without its reference: "10:42 · Word · Start requested",
    /// "10:42 · Sync clock · OK". The outcome in words when the log names one, else the result.
    /// </summary>
    public static string RecentLine(string name, DateTime utc, RunVerdict result, string outcome, TimeZoneInfo zone, DateTime nowUtc)
    {
        var said = !string.IsNullOrEmpty(outcome) ? outcome : Phrase(result);
        return ClockTime(utc, zone, nowUtc) + " · " + name + (said.Length > 0 ? " · " + said : "");
    }

    /// <summary>How long a run's result stays on its tool row: older results may no longer be true.</summary>
    public static readonly TimeSpan RowResultFor = TimeSpan.FromHours(24);

    /// <summary>
    /// The run's line in a tool row: its result word (or its outcome in words) and short time, for a run
    /// of the last 24 hours only; empty for an older run or none (an old OK or PROBLEM may no longer be
    /// true, and "Not run yet" says nothing useful). Nothing to repair reads OK.
    /// </summary>
    public static string RowText(ToolInfo tool, ToolRun run, TimeZoneInfo zone, DateTime nowUtc)
    {
        if (run != null && !IsRecent(run, nowUtc)) { run = null; }
        return RowLine(tool, run, r => ShortWhen(r.Log.StartedUtc ?? r.Log.WrittenUtc, zone, nowUtc));
    }

    private static string RowLine(ToolInfo tool, ToolRun run, Func<ToolRun, string> when)
    {
        if (tool.OpensOnly) { return "Opens a folder"; }
        if (run == null) { return ""; }
        // "Nothing to repair" is a good result: the row says OK, as for every good result.
        var word = !string.IsNullOrEmpty(run.Outcome) && !run.Outcome.Equals("Nothing to repair", StringComparison.OrdinalIgnoreCase) ? run.Outcome : Word(run.Result);
        if (word.Length == 0 && run.Outcome != null && run.Outcome.Equals("Nothing to repair", StringComparison.OrdinalIgnoreCase)) { word = "OK"; }
        var at = when(run);
        return word.Length == 0 ? "Ran " + at : word + " · " + at;
    }
}
