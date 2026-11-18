using System;
using System.Collections.Generic;
using System.Linq;
using EndpointToolbelt.App.Core;
using Xunit;

namespace EndpointToolbelt.Tests;

public class AppStepsTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private static EngineRecord Header(int n, int total, string title, int seconds)
        => new() { Type = "progress", Severity = "info", Text = "== Step " + n + "/" + total + ": " + title + " ==", Utc = T0.AddSeconds(seconds) };

    private static EngineRecord Progress(string text, int seconds, string severity = "info")
        => new() { Type = "progress", Severity = severity, Text = text, Utc = T0.AddSeconds(seconds) };

    private static EngineRecord Finding(string severity, int seconds)
        => new() { Type = "finding", Severity = severity, Id = "X", Title = "t", Utc = T0.AddSeconds(seconds) };

    private static List<EngineRecord> Run() => new()
    {
        Progress("before any step", 0, "warning"),
        Header(1, 4, "Check Windows Update", 1),
        Progress("reading", 5),
        Finding("OK", 10),
        Header(2, 4, "Repair the component store", 61),
        Progress("could not read a value", 70, "warning"),
        Header(3, 4, "Install the missing update", 241),
        Finding("WARNING", 250),
        Finding("ERROR", 260),
        new EngineRecord { Type = "summary", Exit = 1, Utc = T0.AddSeconds(301) },
    };

    [Fact]
    public void Findings_and_warnings_belong_to_the_step_before_them()
    {
        var steps = Steps.Build(Run(), RunEnd.Completed, T0);
        Assert.Equal(new[] { StepStatus.Done, StepStatus.Check, StepStatus.Problem, StepStatus.NotReached }, steps.Select(s => s.Status).ToArray());
        Assert.Equal(new[] { "Done", "Warning", "Problem", "Not reached" }, steps.Select(s => s.Word).ToArray());
        Assert.Equal("1. Check Windows Update", steps[0].NumberAndTitle);
        Assert.Equal("4.", steps[3].NumberAndTitle);
        Assert.All(steps, s => Assert.Equal(4, s.Total));
    }

    [Fact]
    public void Durations_run_from_header_to_header_and_the_last_to_the_last_record()
    {
        var steps = Steps.Build(Run(), RunEnd.Completed, T0);
        Assert.Equal(TimeSpan.FromSeconds(60), steps[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(180), steps[1].Duration);
        Assert.Equal(TimeSpan.FromSeconds(60), steps[2].Duration);
        Assert.Null(steps[3].Duration);
        Assert.Equal("1 min", steps[0].DurationText);
        Assert.Equal("Step 2 of 4: Repair the component store. Warning. 3 min.", steps[1].AccessibleText);
        Assert.Equal("Step 4 of 4. Not reached.", steps[3].AccessibleText);
        Assert.Equal("45 s", Steps.FormatDuration(TimeSpan.FromSeconds(45)));
        Assert.Equal("1 h 05 min", Steps.FormatDuration(TimeSpan.FromMinutes(65)));
    }

    [Fact]
    public void While_a_run_goes_on_its_newest_step_is_running_and_later_steps_are_not_shown()
    {
        var records = Run().Take(6).ToList();
        var steps = Steps.Build(records, RunEnd.Running, T0.AddSeconds(61 + 150));
        Assert.Equal(2, steps.Count);
        Assert.Equal(StepStatus.Done, steps[0].Status);
        Assert.Equal(StepStatus.Running, steps[1].Status);
        Assert.Equal("Running...", steps[1].Word);
        Assert.Equal("running for 2 min", steps[1].DurationText);
        // The running time stays out of the name, so it is not read out again every minute.
        Assert.Equal("Step 2 of 4: Repair the component store. Running.", steps[1].AccessibleText);
        steps[1].Tick(T0.AddSeconds(61 + 400));
        Assert.Equal("running for 6 min", steps[1].DurationText);

        var outcome = new RunOutcome { Action = ToolbeltAction.RepairWU, StartedUtc = T0 };
        outcome.Records.AddRange(records);
        Assert.Equal(StepStatus.Running, ResultPresenter.Live(outcome, T0.AddMinutes(5)).Steps.Last().Status);
        var final = ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.RepairWU, Result = ResultCode.Completed, ExitCode = 1 }.With(Run()));
        Assert.Equal(StepStatus.NotReached, final.Steps.Last().Status);
    }

    [Fact]
    public void A_run_without_step_headers_has_no_list()
    {
        Assert.Empty(Steps.Build(new[] { Progress("old style", 1), Finding("ERROR", 2) }, RunEnd.Completed, T0));
        Assert.Empty(ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.ClearAppCache, Result = ResultCode.Busy }).Steps);
    }

    [Fact]
    public void Skipped_steps_only_where_the_engine_says_so_or_went_past_them()
    {
        // The engine's own sentence for steps it leaves out, and a title that says "(skipped)".
        var retry = new List<EngineRecord>
        {
            Header(1, 5, "Read the current app state", 0),
            Progress("Steps 2 to 4 skipped (IME restart, Company Portal close, retry-timer clear): others are signed in", 2),
            Header(5, 5, "Sync with Intune", 3),
        };
        var steps = Steps.Build(retry, RunEnd.Completed, T0);
        Assert.Equal(new[] { StepStatus.Done, StepStatus.Skipped, StepStatus.Skipped, StepStatus.Skipped, StepStatus.Done }, steps.Select(s => s.Status).ToArray());
        var titled = Steps.Build(new[] { Header(1, 2, "Clear app state (skipped)", 0), Header(2, 2, "Start again", 1) }, RunEnd.Completed, T0);
        Assert.Equal(StepStatus.Skipped, titled[0].Status);
        // A gate (nothing to repair, cooldown) decided not to go on: the rest is skipped, not "not reached".
        var gate = Steps.Build(new[] { Header(1, 3, "Diagnosis", 0) }, RunEnd.Gate, T0);
        Assert.Equal(new[] { StepStatus.Done, StepStatus.Skipped, StepStatus.Skipped }, gate.Select(s => s.Status).ToArray());
        Assert.Equal(RunEnd.Gate, Steps.EndOf(new RunOutcome { Result = ResultCode.Completed, ExitCode = 69003 }));
    }

    [Fact]
    public void A_stopped_run_leaves_its_newest_step_to_check_and_the_rest_not_reached()
    {
        var steps = Steps.Build(new[] { Header(1, 3, "A", 0), Header(2, 3, "B", 5) }, RunEnd.Stopped, T0);
        Assert.Equal(new[] { StepStatus.Done, StepStatus.Check, StepStatus.NotReached }, steps.Select(s => s.Status).ToArray());
        Assert.Equal(RunEnd.Stopped, Steps.EndOf(new RunOutcome { Result = ResultCode.Interrupted }));
        Assert.Equal(RunEnd.Running, Steps.EndOf(new RunOutcome { Result = ResultCode.ServiceUnavailable, InProgress = true }));
        Assert.Equal(RunEnd.Completed, Steps.EndOf(new RunOutcome { Result = ResultCode.Completed, ExitCode = 2 }));
    }

    [Fact]
    public void Titles_are_shown_as_the_engine_wrote_them()
    {
        var steps = Steps.Build(new[] { Header(1, 1, "Clear the app retry timers (GRS: the wait before a failed app is tried again)", 0) }, RunEnd.Completed, T0);
        Assert.Equal("1. Clear the app retry timers (GRS: the wait before a failed app is tried again)", steps[0].NumberAndTitle);
    }
}

public class AppHistoryAndEventTests
{
    [Theory]
    [InlineData("2026-10-15 14:02:11 +02:00;user;ResetCompanyPortal;3", "2026-10-15 14:02 +02:00  Reset Company Portal data: not installed (3)")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;ResetCompanyPortal;4", "2026-10-15 14:02 +02:00  Reset Company Portal data: not supported on this Windows (4)")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;FullReset;-1073741819", "2026-10-15 14:02 +02:00  Full IME reset: 0xC0000005")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;ResetCompanyPortal;2", "2026-10-15 14:02 +02:00  Reset Company Portal data: completed with warnings (2)")]
    public void History_lines_for_portal_codes_and_crash_codes(string line, string expected)
    {
        Assert.Equal(expected, RunWords.HistoryLine(line));
    }

    [Fact]
    public void The_package_event_needs_a_package()
    {
        var done = new RunOutcome { Action = ToolbeltAction.CollectDiag, Result = ResultCode.Completed, ExitCode = 0 };
        Assert.True(ResultPresenter.ReportsPackageCreated(done, fileReceived: true));
        Assert.False(ResultPresenter.ReportsPackageCreated(done, fileReceived: false));
        Assert.False(ResultPresenter.ReportsPackageCreated(new RunOutcome { Action = ToolbeltAction.CollectDiag, Result = ResultCode.EngineCrashed }, fileReceived: true));
        Assert.False(ResultPresenter.ReportsPackageCreated(new RunOutcome { Action = ToolbeltAction.FullReset, Result = ResultCode.Completed }, fileReceived: true));
    }

    [Fact]
    public void A_check_that_ran_in_the_app_gets_a_short_header()
    {
        // Without a check of its own (nothing shown): no run ID, captured now.
        var lines = new RunLogHeader { ActionName = "Connectivity test", AppVersion = "1.0.0", Zone = TimeZoneInfo.Utc, NowUtc = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc) }.Lines().ToList();
        Assert.Contains("Action    : Connectivity test", lines);
        Assert.Contains("Run ID    : none", lines);
        Assert.Contains("Captured at : 2026-01-01 08:00:00 +00:00 (2026-01-01 08:00:00 UTC)", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("Result", StringComparison.Ordinal) || l.StartsWith("Ended", StringComparison.Ordinal));
    }
}

public class AppStepsReviewTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private static EngineRecord Header(int n, int total, string title, int? seconds)
        => new() { Type = "progress", Severity = "info", Text = "== Step " + n + "/" + total + ": " + title + " ==", Utc = seconds.HasValue ? T0.AddSeconds(seconds.Value) : null };

    private static EngineRecord Finding(string severity, int seconds)
        => new() { Type = "finding", Severity = severity, Id = "X", Title = "t", Utc = T0.AddSeconds(seconds) };

    [Fact]
    public void A_cut_run_shows_no_rows_after_its_newest_step_and_says_where_they_are()
    {
        var outcome = new RunOutcome { Action = ToolbeltAction.RepairWU, Result = ResultCode.Completed, ExitCode = 2, Truncated = true };
        outcome.Records.Add(Header(1, 3, "A", 0));
        outcome.Records.Add(new EngineRecord { Type = "warning", Text = "cut", IsCut = true });
        var view = ResultPresenter.Final(outcome);
        // The app's own cut warning is not a point of the step.
        var step = Assert.Single(view.Steps);
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal(Steps.TruncatedNote, view.StepsNote);
        Assert.Equal("Later steps are in the full log.", Steps.TruncatedNote);
        Assert.Equal("", ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.RepairWU, Result = ResultCode.Completed, ExitCode = 2 }.With(new[] { Header(1, 3, "A", 0) })).StepsNote);
    }

    [Theory]
    [InlineData("Steps 2 to 99999999999 skipped (x)")]
    [InlineData("Steps 0 to 4 skipped (x)")]
    [InlineData("Step 2147483648 skipped")]
    public void Odd_numbers_never_throw_and_are_not_steps(string text)
    {
        var records = new[] { Header(1, 3, "A", 0), new EngineRecord { Type = "progress", Text = text }, Header(99999, 99999, "huge", 1), Header(3, 100, "too many", 2) };
        var steps = Steps.Build(records, RunEnd.Completed, T0);
        Assert.Equal(3, steps.Count);
        Assert.Equal(StepStatus.Done, steps[0].Status);
    }

    [Fact]
    public void The_first_header_sets_the_total_and_the_step_written_last_is_the_running_one()
    {
        var records = new[]
        {
            Header(1, 3, "One", 0),
            Header(2, 3, "Two", 10),
            Header(3, 3, "Three", 20),
            Finding("WARNING", 25),
            Header(2, 3, "Two again", 30),
            Header(1, 5, "Other total", 35),
            Finding("ERROR", 40),
        };
        var live = Steps.Build(records, RunEnd.Running, T0.AddSeconds(50));
        Assert.Equal(new[] { StepStatus.Done, StepStatus.Running, StepStatus.Check }, live.Select(s => s.Status).ToArray());
        Assert.All(live, s => Assert.Equal(3, s.Total));
        // Step 2 ran twice: 10 s, then from 30 s on; the title stays the first one.
        Assert.Equal("2. Two", live[1].NumberAndTitle);
        Assert.Equal(TimeSpan.FromSeconds(30), live[1].Duration);
        var stopped = Steps.Build(records, RunEnd.Stopped, T0);
        // The problem came while step 2 was current; it ended at its last record (40 s).
        Assert.Equal(StepStatus.Problem, stopped[1].Status);
        Assert.Equal(TimeSpan.FromSeconds(20), stopped[1].Duration);
    }

    [Fact]
    public void Durations_need_real_times()
    {
        // The next header has no time: the step ends at its own last record.
        var noTime = Steps.Build(new[] { Header(1, 2, "A", 0), Finding("OK", 7), Header(2, 2, "B", null), Finding("OK", 20) }, RunEnd.Completed, T0);
        Assert.Equal(TimeSpan.FromSeconds(7), noTime[0].Duration);
        Assert.Null(noTime[1].Duration);
        // More than a day, or backwards: no duration.
        var tooLong = Steps.Build(new[] { Header(1, 2, "A", 0), Header(2, 2, "B", 90000) }, RunEnd.Completed, T0);
        Assert.Null(tooLong[0].Duration);
        var backwards = Steps.Build(new[] { Header(1, 2, "A", 100), Header(2, 2, "B", 50) }, RunEnd.Completed, T0);
        Assert.Null(backwards[0].Duration);
        // A step without a time of its own: no duration, and a running one keeps none.
        var running = Steps.Build(new[] { Header(1, 1, "A", null) }, RunEnd.Running, T0);
        running[0].Tick(T0.AddMinutes(5));
        Assert.Null(running[0].Duration);
    }
}
