using System;
using System.Collections.Generic;
using System.Linq;

namespace EndpointToolbelt.App.Core;

/// <summary>One finished run on a problem path: what Copy details for IT names as tried.</summary>
public sealed class CaseRun
{
    public string RunId { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>When the run started (UTC), or when it was saved when its start is not known.</summary>
    public DateTime Utc { get; set; }

    /// <summary>The run's outcome in words, as Recent runs and For IT say it (fix 37); '' when it says none.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>The user's answer to "Does it work now?", apart from the outcome; null when not asked or not answered.</summary>
    public WorksNowAnswer? Answer { get; set; }
}

/// <summary>
/// The case file of each problem path in this app session: the runs that ended while the path was
/// shown, in memory only. Every Copy details for IT of the path (its own and the one under View details for a
/// run of it) adds the same "Tried" lines, so both name a run with the same words, time and reference.
/// </summary>
public sealed class CaseFile
{
    /// <summary>The most runs one path's Tried lines name; earlier ones are counted.</summary>
    public const int MaxRuns = 6;

    private readonly Dictionary<Problem, List<CaseRun>> _runs = new();

    /// <summary>
    /// The outcome in words a run log with this header and these result cards shows in Recent runs and
    /// For IT: <see cref="ToolRuns.OutcomeOf"/> over the same lines, else the result in words.
    /// </summary>
    public static string OutcomeWords(RunLogHeader header, IEnumerable<ResultCard> cards)
    {
        var lines = LogLines(header, cards);
        var result = ToolRuns.Parse(lines);
        var outcome = ToolRuns.OutcomeOf(result, lines);
        return outcome.Length > 0 ? outcome : ToolRuns.Phrase(result);
    }

    /// <summary>The result a run log with this header and these result cards states (<see cref="ToolRuns.Parse"/>).</summary>
    public static RunVerdict VerdictOf(RunLogHeader header, IEnumerable<ResultCard> cards) => ToolRuns.Parse(LogLines(header, cards));

    /// <summary>The run log's header and result lines, as the run history writes them.</summary>
    private static List<string> LogLines(RunLogHeader header, IEnumerable<ResultCard> cards)
    {
        var lines = header.Lines().ToList();
        lines.Add("");
        lines.Add("----- RESULT -----");
        foreach (var card in cards ?? Enumerable.Empty<ResultCard>())
        {
            if (card != null) { lines.Add("[" + card.Word + "] " + card.Title); }
        }
        return lines;
    }

    /// <summary>
    /// Records a run that ended: a run already in a case file is updated there (the same run saved
    /// again, such as a safe-mode answer); a new one goes to <paramref name="path"/>, the path shown
    /// now, or nowhere without one. A run still going or without a reference is not recorded.
    /// </summary>
    public bool Record(Problem? path, RunLogHeader header, IEnumerable<ResultCard> cards)
    {
        var id = header?.RunId;
        if (string.IsNullOrEmpty(id) || header.Running) { return false; }
        var run = new CaseRun { RunId = id, Name = header.Name ?? "", Utc = header.StartUtc ?? header.NowUtc, Outcome = OutcomeWords(header, cards) };
        foreach (var list in _runs.Values)
        {
            var at = list.FindIndex(r => string.Equals(r.RunId, id, StringComparison.Ordinal));
            if (at >= 0)
            {
                // The user's answer stays with the run when the run is saved again.
                run.Answer = list[at].Answer;
                list[at] = run;
                return true;
            }
        }
        if (path == null) { return false; }
        if (!_runs.TryGetValue(path.Value, out var runs)) { _runs[path.Value] = runs = new List<CaseRun>(); }
        runs.Add(run);
        return true;
    }

    /// <summary>The path a run belongs to; null when it ran on none.</summary>
    public Problem? PathOf(string runId)
    {
        if (string.IsNullOrEmpty(runId)) { return null; }
        foreach (var pair in _runs)
        {
            if (pair.Value.Any(r => string.Equals(r.RunId, runId, StringComparison.Ordinal))) { return pair.Key; }
        }
        return null;
    }

    /// <summary>
    /// Keeps the user's answer to "Does it work now?" with a run in a case file; a run
    /// is answered once. False for a run that is in no case file or already has an answer.
    /// </summary>
    public bool Answer(string runId, WorksNowAnswer answer)
    {
        var run = Find(runId);
        if (run == null || run.Answer.HasValue) { return false; }
        run.Answer = answer;
        return true;
    }

    /// <summary>The user's answer for a run; null when it has none or is in no case file.</summary>
    public WorksNowAnswer? AnswerOf(string runId) => Find(runId)?.Answer;

    private CaseRun Find(string runId)
        => string.IsNullOrEmpty(runId) ? null : _runs.Values.SelectMany(l => l).FirstOrDefault(r => string.Equals(r.RunId, runId, StringComparison.Ordinal));

    public IReadOnlyList<CaseRun> Runs(Problem path) => _runs.TryGetValue(path, out var runs) ? runs : new List<CaseRun>();

    /// <summary>
    /// The path's Tried lines, oldest first: "Tried · 2026-11-10 10:42 · Reset OneDrive · Reset requested ·
    /// Reference local-ab12cd34"; at most <see cref="MaxRuns"/>, with one line before them that counts
    /// the earlier runs left out. None when nothing ran on the path. A run the user answered
    /// "Does it work now?" for has the answer after its outcome ("Works now: not yet").
    /// </summary>
    public List<string> TriedLines(Problem? path, TimeZoneInfo zone)
    {
        var lines = new List<string>();
        if (path == null) { return lines; }
        var runs = Runs(path.Value);
        var left = Math.Max(0, runs.Count - MaxRuns);
        if (left > 0) { lines.Add("Tried" + RunTicket.Separator + Words.Count(left, "earlier run") + " on this path " + (left == 1 ? "is" : "are") + " left out"); }
        foreach (var run in runs.Skip(left))
        {
            lines.Add("Tried" + RunTicket.Separator + ToolRuns.FullWhen(run.Utc, zone) + RunTicket.Separator + run.Name
                + (run.Outcome.Length > 0 ? RunTicket.Separator + run.Outcome : "")
                + (run.Answer is WorksNowAnswer answer ? RunTicket.Separator + Handover.AnswerWords(answer) : "")
                + RunTicket.Separator + "Reference " + run.RunId);
        }
        return lines;
    }
}
