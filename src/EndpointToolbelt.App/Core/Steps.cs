using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.App.Core;

public enum StepStatus
{
    Running,
    Done,
    Check,
    Problem,
    Skipped,
    NotReached,
}

/// <summary>How a run stands, for its steps: still going, finished, stopped by a gate, or stopped part-way.</summary>
public enum RunEnd
{
    /// <summary>The run still goes on: its newest step is running.</summary>
    Running,

    /// <summary>The engine finished the run (exit 0, 1, 2 or 87).</summary>
    Completed,

    /// <summary>The engine decided not to go on (cooldown, pending restart, nothing to repair).</summary>
    Gate,

    /// <summary>The run ended without its result (time limit, crash, interrupted).</summary>
    Stopped,
}

/// <summary>One row of the STEPS list: what the step did, in words, and how long it took.</summary>
public sealed class StepRow
{
    public int Number { get; set; }
    public int Total { get; set; }

    /// <summary>The engine's title, shown as it is; empty for a step the run never wrote a header for.</summary>
    public string Title { get; set; } = "";

    public StepStatus Status { get; set; }

    /// <summary>How long the step took (or runs, while it runs); null when the records do not say.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>A running step: when its current stretch began (UTC), and what it took before that.</summary>
    public DateTime? RunningFromUtc { get; set; }

    public TimeSpan PriorDuration { get; set; }

    /// <summary>The running time of a running step at <paramref name="utcNow"/>, without building the list again.</summary>
    public void Tick(DateTime utcNow)
    {
        if (Status != StepStatus.Running || !RunningFromUtc.HasValue) { return; }
        Duration = Steps.Plausible(PriorDuration + (utcNow - RunningFromUtc.Value));
    }

    public string Word => Status switch
    {
        StepStatus.Running => "Running...",
        StepStatus.Done => "Done",
        StepStatus.Check => Words.Warning,
        StepStatus.Problem => "Problem",
        StepStatus.Skipped => "Skipped",
        _ => "Not reached",
    };

    /// <summary>"1. Check Windows Update", or "4." for a step without a title.</summary>
    public string NumberAndTitle => Number.ToString(CultureInfo.InvariantCulture) + "." + (Title.Length > 0 ? " " + Title : "");

    /// <summary>"3 min", "12 s"; "running for 3 min" while it runs; empty when not known.</summary>
    public string DurationText => Duration is not TimeSpan d ? "" : Status == StepStatus.Running ? "running for " + Steps.FormatDuration(d) : Steps.FormatDuration(d);

    /// <summary>
    /// The whole row in one sentence, for a screen reader. A running step leaves out its running
    /// time, so the name does not change every minute (the time is in the help text).
    /// </summary>
    public string AccessibleText
    {
        get
        {
            var text = string.Format(CultureInfo.InvariantCulture, "Step {0} of {1}{2}. {3}.", Number, Total, Title.Length > 0 ? ": " + Title : "", Word.TrimEnd('.'));
            return DurationText.Length > 0 && Status != StepStatus.Running ? text + " " + DurationText + "." : text;
        }
    }
}

/// <summary>
/// The STEPS list: one row per engine step, built from the step headers the engine writes
/// ("== Step n/N: title =="). A run without step headers has no list. Pure, and it never throws:
/// a header or line it cannot read is not a step.
/// </summary>
/// <remarks>
/// Attribution: a finding, a warning record or a warning/error progress line belongs to the step
/// whose header came last before it (the step that was current), including the close-out after
/// the last step's work (its findings and warnings are about that step's outcome). Other records
/// (the summary, info progress, changes) never change a step's status.
/// </remarks>
public static class Steps
{
    public const string TruncatedNote = "Later steps are in the full log.";

    private const int MaxSteps = 99;

    private static readonly Regex Header = new(@"^\s*==\s*Step\s+(\d{1,9})\s*/\s*(\d{1,9})\s*:\s*(.*?)\s*==\s*$", RegexOptions.CultureInvariant, TimedRegex.Limit);

    // The engine's own sentence when it leaves steps out: "Steps 2 to 4 skipped (...)".
    private static readonly Regex SkippedSteps = new(@"^\s*Steps?\s+(\d{1,9})(?:\s+to\s+(\d{1,9}))?\s+skipped\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimedRegex.Limit);

    private sealed class Draft
    {
        public StepRow Row;
        public bool SkippedTitle;
        public bool TimeKnown = true;
        public TimeSpan Elapsed;
        public DateTime? SegmentStart;
        public DateTime? LastUtc;
    }

    /// <param name="records">The run's records so far.</param>
    /// <param name="end">How the run stands.</param>
    /// <param name="utcNow">Now, for the time a running step has taken.</param>
    /// <param name="truncated">The app cut the run's records: steps after the newest header are not shown.</param>
    public static List<StepRow> Build(IReadOnlyList<EngineRecord> records, RunEnd end, DateTime utcNow, bool truncated = false)
    {
        try
        {
            return BuildCore(records, end, utcNow, truncated);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or InvalidOperationException)
        {
            // A list it cannot build is no list: the cards and the log still say everything.
            return new List<StepRow>();
        }
    }

    private static List<StepRow> BuildCore(IReadOnlyList<EngineRecord> records, RunEnd end, DateTime utcNow, bool truncated)
    {
        var drafts = new Dictionary<int, Draft>();
        var skipped = new HashSet<int>();
        var total = 0;
        Draft current = null;
        foreach (var r in records)
        {
            if (r == null || r.IsCut) { continue; }
            if (r.Type == "progress" && r.Text != null && TryHeader(r.Text, out var number, out var count, out var title))
            {
                // The first header sets the number of steps; a header that disagrees is not one of them.
                if (total == 0) { total = count; }
                if (count != total) { continue; }
                // Leaving a step ends its stretch: at this header's time, else at the step's own last record.
                if (current != null) { CloseSegment(current, r.Utc ?? current.LastUtc); }
                if (!drafts.TryGetValue(number, out var draft))
                {
                    var plain = TimeText.ToLocal(title).Trim();
                    draft = new Draft
                    {
                        Row = new StepRow { Number = number, Title = plain, Status = StepStatus.Done },
                        SkippedTitle = plain.EndsWith("(skipped)", StringComparison.OrdinalIgnoreCase),
                    };
                    drafts[number] = draft;
                }
                // A step written again (2 after 3) continues the same row, with a new stretch.
                current = draft;
                current.SegmentStart = r.Utc;
                if (!r.Utc.HasValue) { current.TimeKnown = false; }
                current.LastUtc = r.Utc;
                continue;
            }
            if (r.Type == "progress" && r.Text != null && TimedRegex.MatchOrEmpty(SkippedSteps, r.Text) is { Success: true } s
                && TryStep(s.Groups[1].Value, out var from))
            {
                var to = s.Groups[2].Success && TryStep(s.Groups[2].Value, out var upTo) ? upTo : from;
                for (var n = from; n <= to; n++) { skipped.Add(n); }
            }
            if (current == null) { continue; }
            if (r.Utc.HasValue) { current.LastUtc = r.Utc; }
            var severity = r.Type switch
            {
                "finding" => FindingSeverityText.Parse(r.Severity) switch
                {
                    FindingSeverity.Error => StepStatus.Problem,
                    FindingSeverity.Warning => StepStatus.Check,
                    _ => StepStatus.Done,
                },
                "warning" => StepStatus.Check,
                "progress" when r.Severity is "warning" or "error" => StepStatus.Check,
                _ => StepStatus.Done,
            };
            current.Row.Status = Worse(current.Row.Status, severity);
        }
        if (drafts.Count == 0) { return new List<StepRow>(); }

        // The newest step is the one written last (not the highest number).
        var newest = current.Row.Number;
        var highest = drafts.Keys.Max();
        var rows = new List<StepRow>();
        for (var n = 1; n <= total; n++)
        {
            if (drafts.TryGetValue(n, out var draft))
            {
                var row = draft.Row;
                row.Total = total;
                if (draft.SkippedTitle && row.Status == StepStatus.Done) { row.Status = StepStatus.Skipped; }
                if (n == newest && end == RunEnd.Running)
                {
                    row.Status = StepStatus.Running;
                    row.PriorDuration = draft.Elapsed;
                    row.RunningFromUtc = draft.TimeKnown ? draft.SegmentStart : null;
                    row.Tick(utcNow);
                }
                else
                {
                    if (n == newest) { CloseSegment(draft, draft.LastUtc); }
                    row.Duration = draft.TimeKnown ? Plausible(draft.Elapsed) : null;
                }
                // A run stopped part-way did not finish its newest step: it needs checking at least.
                if (n == newest && end == RunEnd.Stopped) { row.Status = Worse(row.Status, StepStatus.Check); }
                rows.Add(row);
            }
            else if (n < highest || skipped.Contains(n))
            {
                // The run went past it without doing it, or said it left it out.
                rows.Add(new StepRow { Number = n, Total = total, Status = StepStatus.Skipped });
            }
            else if (end != RunEnd.Running && !truncated)
            {
                // After the newest step: a gate decided not to go on; otherwise the run ended early.
                // (When the app cut the records, what came after is not known: no row.)
                rows.Add(new StepRow { Number = n, Total = total, Status = end == RunEnd.Gate ? StepStatus.Skipped : StepStatus.NotReached });
            }
        }
        return rows;
    }

    private static void CloseSegment(Draft draft, DateTime? endUtc)
    {
        if (draft.SegmentStart.HasValue && endUtc.HasValue) { draft.Elapsed += endUtc.Value - draft.SegmentStart.Value; }
        else { draft.TimeKnown = false; }
        draft.SegmentStart = null;
    }

    private static bool TryHeader(string text, out int number, out int count, out string title)
    {
        number = count = 0;
        title = "";
        var m = TimedRegex.MatchOrEmpty(Header, text);
        if (!m.Success || !TryStep(m.Groups[1].Value, out number) || !TryStep(m.Groups[2].Value, out count) || number > count) { return false; }
        title = m.Groups[3].Value;
        return true;
    }

    /// <summary>A step number from 1 to 99; anything else is not one.</summary>
    private static bool TryStep(string text, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= MaxSteps;

    /// <summary>A step time that can be true: not negative and not more than a day; otherwise none.</summary>
    internal static TimeSpan? Plausible(TimeSpan duration) => duration < TimeSpan.Zero || duration > TimeSpan.FromHours(24) ? null : duration;

    /// <summary>How a run stands for its steps.</summary>
    public static RunEnd EndOf(RunOutcome outcome)
    {
        if (outcome.InProgress) { return RunEnd.Running; }
        if (outcome.Result != ResultCode.Completed) { return RunEnd.Stopped; }
        return outcome.ExitCode is EngineExitCode.CooldownActive or EngineExitCode.RestartPending or EngineExitCode.NothingToRepair ? RunEnd.Gate : RunEnd.Completed;
    }

    private static StepStatus Worse(StepStatus a, StepStatus b) => Rank(b) > Rank(a) ? b : a;

    private static int Rank(StepStatus status) => status switch
    {
        StepStatus.Problem => 3,
        StepStatus.Check => 2,
        StepStatus.Done => 1,
        _ => 0,
    };

    /// <summary>"45 s", "3 min", "1 h 05 min".</summary>
    public static string FormatDuration(TimeSpan d)
    {
        if (d.TotalSeconds < 60) { return Math.Max(0, (int)Math.Round(d.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " s"; }
        if (d.TotalMinutes < 60) { return ((int)d.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min"; }
        return string.Format(CultureInfo.InvariantCulture, "{0} h {1:00} min", (int)d.TotalHours, d.Minutes);
    }
}
