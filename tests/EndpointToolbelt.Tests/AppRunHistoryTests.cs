using System;
using System.Collections.Generic;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Protocol;
using Xunit;

namespace EndpointToolbelt.Tests;

public class AppLogUpdateTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private static List<EngineRecord> Records() => new()
    {
        new EngineRecord { Type = "progress", Text = "== Step 1/2: Check ==", Utc = T0 },
        new EngineRecord { Type = "finding", Severity = "WARNING", Id = "X", Title = "Behind", Detail = "a\nb", NextStep = "Wait.", Support = "Check.", Utc = T0.AddSeconds(5) },
        new EngineRecord { Type = "progress", Text = "== Step 2/2: Repair ==", Utc = T0.AddSeconds(9) },
        new EngineRecord { Type = "change", Text = "Emptied a folder", Utc = T0.AddSeconds(30) },
        new EngineRecord { Type = "summary", Exit = 2, Utc = T0.AddSeconds(31) },
    };

    [Fact]
    public void The_live_log_is_the_start_of_the_final_log_and_is_only_appended_to()
    {
        var all = Records();
        var run = new RunOutcome { Action = ToolbeltAction.RepairWU, StartedUtc = T0 };
        var timeline = new Timeline(TimeZoneInfo.Utc);
        var shown = new List<string>();
        foreach (var record in all)
        {
            run.Records.Add(record);
            var live = ResultPresenter.Live(run, T0.AddMinutes(1), TimeZoneInfo.Utc, timeline);
            // The cursor formats only new records; the result is the same as a full build.
            Assert.Equal(ResultPresenter.LogLines(run.Records, TimeZoneInfo.Utc), live.LogLines);
            var append = LogUpdate.Append(shown, live.LogLines);
            Assert.NotNull(append);
            shown.AddRange(append);
        }
        var final = ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.RepairWU, Result = ResultCode.Completed, ExitCode = 2 }.With(all), zone: TimeZoneInfo.Utc);
        var rest = LogUpdate.Append(shown, final.LogLines);
        Assert.NotNull(rest);
        Assert.StartsWith("12:00:31 RESULT:", rest.Last());
    }

    [Fact]
    public void A_stored_result_that_dropped_a_record_in_the_middle_is_rebuilt()
    {
        var all = Records();
        var shown = ResultPresenter.LogLines(all.Take(4), TimeZoneInfo.Utc);
        var evicted = ResultPresenter.LogLines(all.Where((_, i) => i != 1), TimeZoneInfo.Utc);
        Assert.Null(LogUpdate.Append(shown, evicted));
        // Fewer lines than shown is a rebuild too; the same lines need nothing.
        Assert.Null(LogUpdate.Append(shown, shown.Take(2).ToList()));
        Assert.Empty(LogUpdate.Append(shown, shown));
    }

    [Fact]
    public void A_time_the_calendar_cannot_hold_costs_only_the_time()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var lines = ResultPresenter.LogLines(new[]
        {
            new EngineRecord { Type = "progress", Text = "end of time", Utc = DateTime.MaxValue },
            new EngineRecord { Type = "progress", Text = "start of time", Utc = DateTime.MinValue },
        }, zone);
        Assert.Equal(2, lines.Count(l => l.EndsWith("of time", StringComparison.Ordinal)));
    }
}

internal static class RunOutcomeTestExtensions
{
    public static RunOutcome With(this RunOutcome outcome, IEnumerable<EngineRecord> records)
    {
        outcome.Records.AddRange(records);
        return outcome;
    }
}

public class AppRunLogRuleTests
{
    private static RunOutcome Outcome(int exit, params EngineRecord[] records)
        => new RunOutcome { Action = ToolbeltAction.RetryAppsAndPolicies, Result = ResultCode.Completed, ExitCode = exit }.With(records);

    private static EngineRecord Finding(string severity, string id) => new() { Type = "finding", Severity = severity, Id = id, Title = id };

    [Fact]
    public void A_queued_sync_alone_is_no_reason_for_a_log_in_Downloads()
    {
        var queuedOnly = Outcome(2, new EngineRecord { Type = "mdm-queued", At = "2026-10-15T12:03:00Z" }, Finding("INFO", "MDM-SYNC-THROTTLED"), Finding("OK", "IME-RUNNING"));
        Assert.True(ResultPresenter.OnlyQueuedSync(queuedOnly.Records));
        Assert.False(ResultPresenter.Final(queuedOnly).CopyLog);
        Assert.False(ResultPresenter.Final(Outcome(2, Finding("INFO", "MDM-SYNC-QUEUED"))).CopyLog);

        var withMore = Outcome(2, new EngineRecord { Type = "mdm-queued" }, Finding("WARNING", "APPS-FAILED"));
        Assert.False(ResultPresenter.OnlyQueuedSync(withMore.Records));
        Assert.True(ResultPresenter.Final(withMore).CopyLog);
        Assert.True(ResultPresenter.Final(Outcome(2, Finding("WARNING", "APPS-FAILED"))).CopyLog);
    }

    [Fact]
    public void A_problem_that_remains_after_exit_0_is_copied_too()
    {
        Assert.True(ResultPresenter.Final(Outcome(0, Finding("ERROR", "STILL-BROKEN"))).CopyLog);
        Assert.False(ResultPresenter.Final(Outcome(0, Finding("OK", "FINE"))).CopyLog);
    }

    [Fact]
    public void The_header_survives_extreme_dates_and_names_both_sides_of_a_clock_change()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        // The night summer time ends: 02:30 CEST, then 02:30 CET an hour later.
        var outcome = new RunOutcome { Action = ToolbeltAction.RepairWU, Result = ResultCode.Completed, ExitCode = 0, StartedUtc = new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc) };
        outcome.Records.Add(new EngineRecord { Type = "summary", Exit = 0, Utc = new DateTime(2026, 10, 25, 1, 30, 0, DateTimeKind.Utc) });
        var lines = new RunLogHeader { Outcome = outcome, Zone = zone, NowUtc = new DateTime(2026, 10, 25, 1, 31, 0, DateTimeKind.Utc) }.Lines().ToList();
        Assert.Contains("Started   : 2026-10-25 02:30:00 +02:00 (2026-10-25 00:30:00 UTC)", lines);
        Assert.Contains("Ended     : 2026-10-25 02:30:00 +01:00 (2026-10-25 01:30:00 UTC)", lines);
        Assert.Contains("Time zone : local time, UTC+01:00 (" + zone.StandardName + "), now", lines);

        foreach (var extreme in new[] { DateTime.MaxValue, DateTime.MinValue })
        {
            var odd = new RunOutcome { Action = ToolbeltAction.FullReset, Result = ResultCode.EngineCrashed, StartedUtc = extreme };
            odd.Records.Add(new EngineRecord { Type = "progress", Text = "x", Utc = extreme });
            var header = new RunLogHeader { Outcome = odd, Zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"), NowUtc = extreme };
            var text = header.Lines().ToList();
            var started = Assert.Single(text, l => l.StartsWith("Started   : ", StringComparison.Ordinal));
            Assert.True(started.EndsWith(" UTC", StringComparison.Ordinal) || started.EndsWith(" UTC)", StringComparison.Ordinal));
            Assert.False(string.IsNullOrEmpty(header.ZoneLabelAt(extreme)));
        }
    }

    [Fact]
    public void The_start_falls_back_to_the_first_record()
    {
        var outcome = new RunOutcome { Action = ToolbeltAction.FullReset, Result = ResultCode.Interrupted };
        outcome.Records.Add(new EngineRecord { Type = "progress", Text = "no time" });
        outcome.Records.Add(new EngineRecord { Type = "progress", Text = "a", Utc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) });
        var lines = new RunLogHeader { Outcome = outcome, Zone = TimeZoneInfo.Utc }.Lines().ToList();
        Assert.Contains("Started   : 2026-01-02 03:04:05 +00:00 (2026-01-02 03:04:05 UTC)", lines);
        Assert.Contains("Result    : Interrupted (service result 34)", lines);
    }
}

public class AppRecentRunsTests
{
    [Fact]
    public void For_IT_lists_a_run_by_its_time_its_name_and_how_it_ended_without_its_reference()
    {
        // The recent runs come from the run history: "10:42 · Word · Start requested", no reference.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var now = new DateTime(2026, 11, 10, 15, 0, 0, DateTimeKind.Utc);
        var diagnosis = new EndpointToolbelt.App.Ui.RecentRunView(RunLogStore.Describe(@"C:\runs\20261110-130000Z_DiagnoseApps_ab12cd34.log"), zone, nowUtc: now);
        Assert.Equal("14:00 · App diagnosis", diagnosis.Text);
        Assert.Equal(@"C:\runs\20261110-130000Z_DiagnoseApps_ab12cd34.log", diagnosis.Path);
        var check = new EndpointToolbelt.App.Ui.RecentRunView(RunLogStore.Describe(@"C:\runs\20260715-080000Z_Connectivitytest_local-0a1b2c3d.log"), zone, RunVerdict.Ok, nowUtc: now);
        // A check in the app is named as its tool is, and a run says how it ended; another day has its date.
        Assert.Equal("15 Jul 10:00 · Check Microsoft connections · OK", check.Text);
        Assert.StartsWith("Open the log of App diagnosis", diagnosis.AccessibleName);
        Assert.Equal("Open the log of Check Microsoft connections, OK, 2026-07-15 10:00:00 (UTC+02:00)", check.AccessibleName);
        // A safe-mode start by its app, with its outcome in words; never "ENDED" and never the reference.
        var word = new EndpointToolbelt.App.Ui.RecentRunView(RunLogStore.Describe(@"C:\runs\20261110-094200Z_StartWordinsafemode_local-b7e05a66.log"), zone, RunVerdict.Requested, "Start requested", now);
        Assert.Equal("10:42 · Word · Start requested", word.Text);
        var failed = new EndpointToolbelt.App.Ui.RecentRunView(RunLogStore.Describe(@"C:\runs\20261110-094200Z_StartWordinsafemode_local-c8f16b77.log"), zone, RunVerdict.Problem, "Could not start", now);
        Assert.Equal("10:42 · Word · Could not start", failed.Text);
        var outlook = new EndpointToolbelt.App.Ui.RecentRunView(RunLogStore.Describe(@"C:\runs\20261110-094200Z_StartOutlookclassicinsafemode_local-d9a27c88.log"), zone, RunVerdict.Requested, "Start requested", now);
        Assert.Equal("10:42 · Outlook (classic) · Start requested", outlook.Text);
        foreach (var view in new[] { diagnosis, check, word, failed, outlook })
        {
            Assert.DoesNotContain("local-", view.Text + view.AccessibleName);
            Assert.DoesNotContain("ab12cd34", view.Text + view.AccessibleName);
            Assert.DoesNotContain("ENDED", view.Text.ToUpperInvariant());
        }
        Assert.Equal("Contoso", EndpointToolbelt.App.Ui.RecentRunView.Name("Contoso"));
    }
}

/// <summary>For IT's last runs per tool, from the run history.</summary>
public class AppToolRunsTests
{
    private static string[] Log(bool ended, params string[] cards)
    {
        var lines = new List<string> { "===== ENDPOINT TOOLBELT 1.1.1 - RUN LOG =====", "Started   : 2026-11-12 10:00" };
        lines.Add(ended ? "Ended     : 2026-11-12 10:01" : "Captured at : 2026-11-12 10:01");
        lines.Add("----- RESULT -----");
        lines.AddRange(cards);
        lines.Add("----- TECHNICAL LOG (times in UTC) -----");
        lines.Add("[PROBLEM] a technical log line is never a result");
        return lines.ToArray();
    }

    [Fact]
    public void A_logs_result_comes_from_its_header_and_result_cards_and_OK_needs_an_OK_card()
    {
        Assert.Equal(RunVerdict.Ok, ToolRuns.Parse(Log(true, "[OK] The clock was synchronised", "[INFO] Source: time.contoso.example")));
        Assert.Equal(RunVerdict.Check, ToolRuns.Parse(Log(true, "[OK] Cleared", "[CHECK] Some items were in use")));
        Assert.Equal(RunVerdict.Problem, ToolRuns.Parse(Log(true, "[CHECK] x", "[PROBLEM] y")));
        // Ended, but nothing that says it worked: never shown as OK.
        Assert.Equal(RunVerdict.Ended, ToolRuns.Parse(Log(true, "[INFO] Nothing to do", "[CHANGED ON THIS DEVICE] 1 change")));
        Assert.Equal(RunVerdict.Ended, ToolRuns.Parse(Log(true)));
        // No confirmed end (Captured at) is not finished; a run still going says so.
        Assert.Equal(RunVerdict.NotFinished, ToolRuns.Parse(Log(false, "[OK] Step one")));
        var running = Log(false).ToList();
        running.Insert(1, "Result    : still running (no result yet)");
        Assert.Equal(RunVerdict.Running, ToolRuns.Parse(running));
        // A requested start or reset is its own verdict, without a word (no badge), never ENDED.
        Assert.Equal(RunVerdict.Requested, ToolRuns.Parse(Log(true, "[REQUESTED] Start requested: Word in safe mode")));
        Assert.Equal(RunVerdict.Problem, ToolRuns.Parse(Log(true, "[PROBLEM] Word could not be started in safe mode")));
        Assert.Equal("", ToolRuns.Word(RunVerdict.Requested));
        var withOutcome = Log(true, "[REQUESTED] Start requested: Word in safe mode").ToList();
        withOutcome.Insert(1, "Outcome   : Start requested");
        Assert.Equal("Start requested", ToolRuns.ParseOutcome(withOutcome));
        Assert.Equal("", ToolRuns.ParseOutcome(Log(true, "[OK] x")));
        Assert.Equal(RunVerdict.None, ToolRuns.Read(@"C:\no\such\folder\run.log"));
        // A run that only ended has no word ("ENDED" says nothing), in capitals or in sentence case.
        Assert.Equal(new[] { "OK", "WARNING", "PROBLEM", "RUNNING", "NOT FINISHED", "", "" },
            new[] { RunVerdict.Ok, RunVerdict.Check, RunVerdict.Problem, RunVerdict.Running, RunVerdict.NotFinished, RunVerdict.Ended, RunVerdict.None }.Select(ToolRuns.Word));
        Assert.Equal("", ToolRuns.Phrase(RunVerdict.Ended));
    }

    [Fact]
    public void Fix37_a_run_that_only_ended_says_its_outcome_from_its_header_or_its_exit_code_or_nothing()
    {
        // An app check's outcome line wins: a safe-mode start answered Not confirmed.
        var notConfirmed = Log(true, "[INFO] Not confirmed whether Word works in safe mode").ToList();
        notConfirmed.Insert(1, "Outcome   : Not confirmed");
        Assert.Equal(RunVerdict.Ended, ToolRuns.Parse(notConfirmed));
        Assert.Equal("Not confirmed", ToolRuns.OutcomeOf(RunVerdict.Ended, notConfirmed));
        // An engine run with only information: its exit code in the app's words.
        string[] Engine(int exit, params string[] cards)
        {
            var lines = Log(true, cards).ToList();
            lines.Insert(1, "Exit code : " + exit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return lines.ToArray();
        }
        Assert.Equal("Nothing to repair", ToolRuns.OutcomeOf(RunVerdict.Ended, Engine(EngineExitCode.NothingToRepair, "[INFO] Nothing more could be freed by Windows")));
        Assert.Equal("Skipped: cooldown active", ToolRuns.OutcomeOf(RunVerdict.Ended, Engine(EngineExitCode.CooldownActive, "[INFO] Ran less than an hour ago")));
        Assert.Equal("Blocked: restart pending", ToolRuns.OutcomeOf(RunVerdict.Ended, Engine(EngineExitCode.RestartPending, "[INFO] A restart is pending")));
        Assert.Equal("Completed", ToolRuns.OutcomeOf(RunVerdict.Ended, Engine(EngineExitCode.Success, "[INFO] Done")));
        // Nothing to say: no outcome line and no exit code, or a code without a meaning of its own.
        Assert.Equal("", ToolRuns.OutcomeOf(RunVerdict.Ended, Log(true, "[INFO] x")));
        Assert.Equal("", ToolRuns.OutcomeOf(RunVerdict.Ended, Engine(42, "[INFO] x")));
        // The exit code speaks only for a run that only ended: OK, a check or a problem keep their own word.
        Assert.Equal("", ToolRuns.OutcomeOf(RunVerdict.Check, Engine(EngineExitCode.RestartPending, "[CHECK] x")));
        // An exit code below the result section is a card's text, not the header.
        var below = Log(true, "Exit code : 69003").ToList();
        Assert.Equal("", ToolRuns.ParseExitOutcome(below));
    }

    [Fact]
    public void Fix37_a_tile_says_how_its_newest_run_went_in_the_apps_words_and_never_ended()
    {
        ToolRun Run(RunVerdict result, string outcome = "") => new() { Log = new StoredRunLog { StartedUtc = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc) }, Result = result, Outcome = outcome };
        string SignIn(ToolRun run) => ToolRuns.GroupSummary(ToolGroup.Microsoft365, new Dictionary<string, ToolRun> { ["WordSafeButton"] = run });
        string Network(ToolRun run) => ToolRuns.GroupSummary(ToolGroup.NetworkStorage, new Dictionary<string, ToolRun> { ["FreeSpaceButton"] = run });
        // A real case: a safe-mode start that answered Not confirmed.
        Assert.Equal("5 tools · last run not confirmed", SignIn(Run(RunVerdict.Ended, "Not confirmed")));
        Assert.Equal("5 tools · last run not installed", SignIn(Run(RunVerdict.Ended, "Not installed")));
        // An engine run with only information, by its exit code.
        Assert.Equal("4 tools · last run nothing to repair", Network(Run(RunVerdict.Ended, "Nothing to repair")));
        Assert.Equal("4 tools · last run skipped: cooldown active", Network(Run(RunVerdict.Ended, "Skipped: cooldown active")));
        Assert.Equal("4 tools · last run blocked: restart pending", Network(Run(RunVerdict.Ended, "Blocked: restart pending")));
        Assert.Equal("4 tools · last run completed", Network(Run(RunVerdict.Ended, "Completed")));
        // Nothing to say: the line is left out.
        Assert.Equal("4 tools", Network(Run(RunVerdict.Ended)));
        // The other verdicts keep their words; a requested start or reset adds nothing, with its outcome too.
        Assert.Equal("5 tools · last run OK", SignIn(Run(RunVerdict.Ok)));
        Assert.Equal("5 tools · one running", SignIn(Run(RunVerdict.Running)));
        Assert.Equal("5 tools · last run not finished", SignIn(Run(RunVerdict.NotFinished)));
        Assert.Equal("5 tools · 1 warning", SignIn(Run(RunVerdict.Check, "Works in safe mode")));
        Assert.Equal("5 tools · 1 problem", SignIn(Run(RunVerdict.Problem, "Could not start")));
        Assert.Equal("5 tools", SignIn(Run(RunVerdict.Requested, "Start requested")));
        Assert.Equal("4 tools", ToolRuns.GroupSummary(ToolGroup.NetworkStorage, new Dictionary<string, ToolRun> { ["ResetOneDriveButton"] = Run(RunVerdict.Requested, "Reset requested") }));
        Assert.Equal("5 tools", SignIn(Run(RunVerdict.None)));
        foreach (var result in Enum.GetValues(typeof(RunVerdict)).Cast<RunVerdict>())
        {
            Assert.DoesNotContain("ended", SignIn(Run(result)), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Fix37_a_tool_row_and_a_recent_run_say_the_outcome_of_a_run_that_only_ended_or_only_its_time()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var now = new DateTime(2026, 11, 14, 15, 0, 0, DateTimeKind.Utc);
        var tool = ToolCatalog.ByButton("FreeSpaceButton");
        ToolRun Run(string outcome) => new() { Log = new StoredRunLog { StartedUtc = new DateTime(2026, 11, 14, 9, 42, 0, DateTimeKind.Utc) }, Result = RunVerdict.Ended, Outcome = outcome };
        Assert.Equal("OK · today 10:42", ToolRuns.RowText(tool, Run("Nothing to repair"), zone, now));
        // A result older than 24 hours may no longer be true: the row says nothing.
        Assert.Equal("", ToolRuns.RowText(tool, Run("Nothing to repair"), zone, now.AddHours(25)));
        Assert.Equal("Ran today 10:42", ToolRuns.RowText(tool, Run(""), zone, now));
        Assert.Equal("10:42 · Word · Not confirmed", ToolRuns.RecentLine("Word", new DateTime(2026, 11, 14, 9, 42, 0, DateTimeKind.Utc), RunVerdict.Ended, "Not confirmed", zone, now));
        Assert.Equal("10:42 · Free up disk space", ToolRuns.RecentLine("Free up disk space", new DateTime(2026, 11, 14, 9, 42, 0, DateTimeKind.Utc), RunVerdict.Ended, "", zone, now));
    }

    [Fact]
    public void Each_tool_gets_its_newest_run_by_the_name_its_logs_carry()
    {
        var logs = new[]
        {
            RunLogStore.Describe(@"C:\runs\20261112-080000Z_SyncClock_aa11bb22.log"),
            RunLogStore.Describe(@"C:\runs\20261112-090000Z_SyncClock_cc33dd44.log"),
            RunLogStore.Describe(@"C:\runs\20261112-091000Z_Connectivitytest_local-0a1b2c3d.log"),
            RunLogStore.Describe(@"C:\runs\20261112-092000Z_DiagnoseApps_ee55ff66.log"),
            RunLogStore.Describe(@"C:\runs\20261112-093000Z_Resetsignincache_local-1a2b3c4d.log"),
        };
        var latest = ToolRuns.Latest(logs, path => path.Contains("cc33dd44") ? RunVerdict.Ok : RunVerdict.Check);
        Assert.Equal("cc33dd44", latest["SyncClockButton"].Log.RunId);
        Assert.Equal(RunVerdict.Ok, latest["SyncClockButton"].Result);
        Assert.Equal("local-0a1b2c3d", latest["ConnectivityButton"].Log.RunId);
        Assert.Equal("local-1a2b3c4d", latest["ResetSignInButton"].Log.RunId);
        // The app diagnosis is not one of the tools; Open IME logs leaves no run.
        Assert.Equal(3, latest.Count);
        Assert.Equal("", ToolCatalog.ByButton("LogsButton").LogKey);
        // Every other tool's log name is its action or its check's name.
        Assert.Equal(23, ToolCatalog.All.Count(t => t.LogKey.Length > 0));
        Assert.Equal(23, ToolCatalog.All.Select(t => t.LogKey).Where(k => k.Length > 0).Distinct().Count());
        // The compliance check's run logs carry its name.
        Assert.Equal("Checkcompliancesettings", ToolCatalog.ByButton("ComplianceButton").LogKey);
        Assert.Equal("ClearTeamscache", ToolCatalog.ByButton("TeamsCacheButton").LogKey);
        Assert.Equal("FreeDiskSpace", ToolCatalog.ByButton("FreeSpaceButton").LogKey);
        // The connection check's new name, and its run logs under the earlier name still count.
        Assert.Equal("CheckMicrosoftconnections", ToolCatalog.ByButton("ConnectivityButton").LogKey);
        Assert.Same(ToolCatalog.ByButton("ConnectivityButton"), ToolCatalog.ByLogKey("Connectivitytest"));
        Assert.Equal("Bringmywindowback", ToolCatalog.ByButton("WindowBackButton").LogKey);
    }

    [Fact]
    public void A_groups_summary_counts_its_tools_and_says_how_their_last_runs_ended_in_words()
    {
        ToolRun Run(string time, RunVerdict result) => new() { Log = new StoredRunLog { StartedUtc = DateTime.Parse(time, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal) }, Result = result };
        Assert.Equal("5 tools", ToolRuns.GroupSummary(ToolGroup.Apps, new Dictionary<string, ToolRun>()));
        Assert.Equal("4 tools · last run OK", ToolRuns.GroupSummary(ToolGroup.Windows, new Dictionary<string, ToolRun> { ["SyncClockButton"] = Run("2026-11-12T09:00:00Z", RunVerdict.Ok) }));
        Assert.Equal("5 tools · 1 warning", ToolRuns.GroupSummary(ToolGroup.Microsoft365, new Dictionary<string, ToolRun>
        {
            ["TeamsCacheButton"] = Run("2026-11-12T09:00:00Z", RunVerdict.Check),
            ["WordSafeButton"] = Run("2026-11-12T10:00:00Z", RunVerdict.Ok),
        }));
        Assert.Equal("5 tools · 1 problem · 1 warning", ToolRuns.GroupSummary(ToolGroup.Apps, new Dictionary<string, ToolRun>
        {
            ["FullResetButton"] = Run("2026-11-12T09:00:00Z", RunVerdict.Problem),
            ["PortalButton"] = Run("2026-11-12T08:00:00Z", RunVerdict.Check),
        }));
        Assert.Equal("4 tools · last run not finished", ToolRuns.GroupSummary(ToolGroup.NetworkStorage, new Dictionary<string, ToolRun> { ["FreeSpaceButton"] = Run("2026-11-12T09:00:00Z", RunVerdict.NotFinished) }));
        // A tool of another group does not count.
        Assert.Equal("4 tools", ToolRuns.GroupSummary(ToolGroup.NetworkStorage, new Dictionary<string, ToolRun> { ["SyncClockButton"] = Run("2026-11-12T09:00:00Z", RunVerdict.Problem) }));
        // A requested start adds no badge to its tile.
        Assert.Equal("5 tools", ToolRuns.GroupSummary(ToolGroup.Microsoft365, new Dictionary<string, ToolRun> { ["WordSafeButton"] = Run("2026-11-12T09:00:00Z", RunVerdict.Requested) }));
    }

    [Fact]
    public void A_tool_row_says_its_last_run_with_a_short_local_time()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var now = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("today 11:31", ToolRuns.ShortWhen(new DateTime(2026, 11, 12, 10, 31, 0, DateTimeKind.Utc), zone, now));
        // Today, yesterday by the local calendar day, else the day and month in English (the year when it is not this year or lies ahead).
        Assert.Equal("10 Nov 14:05", ToolRuns.ShortWhen(new DateTime(2026, 11, 10, 13, 5, 0, DateTimeKind.Utc), zone, now));
        Assert.Equal("yesterday 23:59", ToolRuns.ShortWhen(new DateTime(2026, 11, 11, 22, 59, 0, DateTimeKind.Utc), zone, now));
        Assert.Equal("today 00:00", ToolRuns.ShortWhen(new DateTime(2026, 11, 11, 23, 0, 0, DateTimeKind.Utc), zone, now));
        Assert.Equal("28 Dec 2025 09:00", ToolRuns.ShortWhen(new DateTime(2025, 12, 28, 8, 0, 0, DateTimeKind.Utc), zone, new DateTime(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("yesterday 23:30", ToolRuns.ShortWhen(new DateTime(2025, 12, 31, 22, 30, 0, DateTimeKind.Utc), zone, new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("14 Nov 2026 10:00", ToolRuns.ShortWhen(new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc), zone, now));
        // Across the end of daylight saving time in this zone: yesterday is the calendar day, not 24 hours.
        Assert.Equal("yesterday 00:30", ToolRuns.ShortWhen(new DateTime(2026, 10, 24, 22, 30, 0, DateTimeKind.Utc), zone, new DateTime(2026, 10, 25, 23, 30, 0, DateTimeKind.Utc)));
        // English whatever the Windows language.
        var culture = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("nl-NL");
            Assert.Equal("10 Oct 14:05", ToolRuns.ShortWhen(new DateTime(2026, 10, 10, 12, 5, 0, DateTimeKind.Utc), zone, now));
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = culture;
        }
        // Recent runs drop the word today; text that leaves the screen is always in full.
        Assert.Equal("11:31", ToolRuns.ClockTime(new DateTime(2026, 11, 12, 10, 31, 0, DateTimeKind.Utc), zone, now));
        Assert.Equal("yesterday 11:31", ToolRuns.ClockTime(new DateTime(2026, 11, 11, 10, 31, 0, DateTimeKind.Utc), zone, now));
        Assert.Equal("2026-11-12 11:31", ToolRuns.FullWhen(new DateTime(2026, 11, 12, 10, 31, 0, DateTimeKind.Utc), zone));
        var run = new ToolRun { Log = new StoredRunLog { StartedUtc = new DateTime(2026, 11, 12, 10, 31, 0, DateTimeKind.Utc) }, Result = RunVerdict.Check };
        Assert.Equal("WARNING · today 11:31", ToolRuns.RowText(ToolCatalog.ByButton("TeamsCacheButton"), run, zone, now));
        // A run with an outcome in words says it instead of a result word.
        var started = new ToolRun { Log = run.Log, Result = RunVerdict.Requested, Outcome = "Start requested" };
        Assert.Equal("Start requested · today 11:31", ToolRuns.RowText(ToolCatalog.ByButton("WordSafeButton"), started, zone, now));
        Assert.Equal("", ToolRuns.RowText(ToolCatalog.ByButton("TeamsCacheButton"), null, zone, now));
        Assert.Equal("Opens a folder", ToolRuns.RowText(ToolCatalog.ByButton("LogsButton"), null, zone, now));
        // The row's name for assistive technology says it all, so it never equals the Run button's name (the tool's name).
        EndpointToolbelt.Tests.MainWindowPagesTests.OnSta(() =>
        {
            var row = new EndpointToolbelt.App.Ui.ToolRowView(ToolCatalog.ByButton("TeamsCacheButton"), run, showGroup: true, selected: true, zone, now);
            // The short time on screen; the full date and time in the name and on the status's tooltip.
            Assert.Equal("WARNING · today 11:31", row.ResultText);
            Assert.Equal("2026-11-12 11:31", row.ResultToolTip);
            Assert.Equal("Clear Teams cache, Microsoft 365. Closes Teams and clears its cache in your profile. Last run: WARNING, 2026-11-12 11:31.", row.AccessibleName);
            Assert.NotEqual(row.Name, row.AccessibleName);
            Assert.Equal("Selected", row.SelectedTag);
            Assert.Equal("ToolRow_TeamsCache", row.RowId);
        });
    }

    [Fact]
    public void A_diagnosis_followed_after_a_lost_connection_is_running_until_its_result_is_in()
    {
        var followed = new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "ab12cd34", InProgress = true }
            .With(new[] { new EngineRecord { Type = "app-diagnosis", App = new AppEvidence { AppId = "11111111-2222-3333-4444-555555555555", Verdict = "OK" } } });
        var diagnosis = AppDecisions.From(followed, DateTime.UtcNow);
        Assert.Equal(DiagnosisRunState.Running, diagnosis.Run);
        Assert.Null(diagnosis.Problem);
        Assert.Equal(("Checking the apps on this device (read-only)...", Tone.Neutral), AppDecisions.Status(diagnosis));
        // The live view while it runs, then its final view: no drawer for the diagnosis, the list is in the window.
        Assert.False(ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "ab12cd34", Result = ResultCode.Completed, ExitCode = 0 }).OpenDrawer);
    }
}

public class AppTimeZoneTests
{
    [Fact]
    public void A_time_zone_is_named_with_the_name_and_offset_of_the_moment()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        Assert.Equal("local time, UTC+02:00 (" + zone.DaylightName + ")", TimeText.ZoneLabel(zone, new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("local time, UTC+01:00 (" + zone.StandardName + ")", TimeText.ZoneLabel(zone, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)));
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        Assert.StartsWith("local time, UTC-04:00 (", TimeText.ZoneLabel(eastern, new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("local time, UTC+00:00 (" + TimeZoneInfo.Utc.StandardName + ")", TimeText.ZoneLabel(TimeZoneInfo.Utc, DateTime.UtcNow));
        Assert.Equal("-05:30", TimeText.Offset(TimeSpan.FromMinutes(-330)));
        Assert.Equal("+05:45", TimeText.Offset(TimeSpan.FromMinutes(345)));
    }
}

public class AppWindowStateTests
{
    [Fact]
    public void Following_a_run_disables_everything_and_giving_up_counts_as_a_missing_service()
    {
        var state = new ButtonState();
        Assert.True(state.IsEnabled(AppButton.Retry));
        state.StartFollowing();
        Assert.False(state.IsEnabled(AppButton.Report));
        Assert.False(state.IsEnabled(AppButton.Refresh));
        state.FollowingGaveUp();
        Assert.False(state.Following);
        Assert.True(state.ServiceMissing);
        Assert.False(state.IsEnabled(AppButton.Retry));
        Assert.True(state.OffForService(AppButton.Retry));
        Assert.True(state.IsEnabled(AppButton.Refresh));
        Assert.False(state.OffForService(AppButton.Report));
        state.Busy = true;
        Assert.False(state.IsEnabled(AppButton.Refresh));
        state.Busy = false;
        state.ServiceReached();
        Assert.True(state.IsEnabled(AppButton.RepairWu));
        state.ServiceLost();
        Assert.False(state.IsEnabled(AppButton.FullReset));
        state.StopFollowing();
        Assert.False(state.Following);
    }

    [Fact]
    public void The_second_confirmation_comes_once_and_says_the_number_changed()
    {
        var changed = new RunOutcome { Action = ToolbeltAction.FullReset, Result = ResultCode.ConfirmationRequired, Message = "Other users are signed in now (3). Confirm again to continue." };
        Assert.True(ResultPresenter.AskSessionsAgain(0, changed));
        Assert.False(ResultPresenter.AskSessionsAgain(1, changed));
        Assert.False(ResultPresenter.AskSessionsAgain(0, new RunOutcome { Action = ToolbeltAction.FullReset, Result = ResultCode.Completed, ExitCode = 0 }));
        Assert.Equal("The number of people signed in to this device changed since you confirmed (now 3).", Confirmations.SessionsChanged(3));
    }

    [Fact]
    public void A_status_replaced_at_once_is_not_read_out()
    {
        Assert.False(Announcements.ShouldAnnounce(StatusChange.Quiet, null, "Step 1/2: A"));
        var retry = new RunOutcome { Action = ToolbeltAction.RetryAppsAndPolicies, Result = ResultCode.Completed, ExitCode = 0 };
        var view = ResultPresenter.Final(retry);
        Assert.Equal(Tone.Success, view.Tone);
        Assert.True(ResultPresenter.HealthStatusFollows(retry, view));
        var problem = new RunOutcome { Action = ToolbeltAction.RetryAppsAndPolicies, Result = ResultCode.Completed, ExitCode = 0 }
            .With(new[] { new EngineRecord { Type = "finding", Severity = "ERROR", Title = "x" } });
        Assert.False(ResultPresenter.HealthStatusFollows(problem, ResultPresenter.Final(problem)));
        var reset = new RunOutcome { Action = ToolbeltAction.FullReset, Result = ResultCode.Completed, ExitCode = 0 };
        Assert.False(ResultPresenter.HealthStatusFollows(reset, ResultPresenter.Final(reset)));
    }

    [Fact]
    public void An_interrupted_run_points_to_Check_again()
    {
        var view = ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.RepairWU, Result = ResultCode.Interrupted });
        Assert.Contains("press Check this PC again on Home to see the device's state", view.Status);
        Assert.DoesNotContain("check the result", view.Status);
    }
}

public class AppCertificateWordingTests
{
    private sealed class Source : IHealthSource
    {
        public DateTime UtcNow { get; } = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        public DateTime? Expiry { get; set; }
        public string ImeServiceStatus() => "Running";
        public DsregInfo Dsreg() => new() { AzureAdJoined = "YES", AzureAdPrt = "YES" };
        public DateTime? MdmCertificateExpiryUtc() => Expiry;
        public bool CanReach(string host, int port, int timeoutMs) => true;
        public double FreeSystemDriveGB() => 50;
        public bool ServicingRestartPending() => false;
    }

    [Theory]
    [InlineData(1, "(1 day)")]
    [InlineData(314, "(314 days)")]
    public void Certificate_days_are_words(int days, string expected)
    {
        var source = new Source();
        source.Expiry = source.UtcNow.AddDays(days).AddHours(1);
        var row = HealthChecks.Run(source, new StatusInfo { Enrolled = true }, out _).Single(r => r.Name == HealthChecks.MdmCertificate);
        Assert.EndsWith(expected, row.Detail);
    }
}
