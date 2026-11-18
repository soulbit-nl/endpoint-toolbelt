using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EndpointToolbelt.App.Core;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// Run-log headers that tell a running run from an ended one, local run IDs for checks in the
/// app, one log per run in a bounded history, and the "Copy details for IT" ticket text.
/// </summary>
public sealed class AppRunLogHandoverTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static RunOutcome Running(string runId = "ab12cd34") => new RunOutcome { Action = ToolbeltAction.RetryAppsAndPolicies, RunId = runId, InProgress = true, StartedUtc = T0 }
        .With(new[] { new EngineRecord { Type = "progress", Text = "== Step 1/2: Check ==", Utc = T0.AddSeconds(5) } });

    private static RunOutcome Ended(string runId = "ab12cd34", int exit = 2, ToolbeltAction action = ToolbeltAction.RetryAppsAndPolicies) => new RunOutcome { Action = action, RunId = runId, Result = ResultCode.Completed, ExitCode = exit, StartedUtc = T0 }
        .With(new[]
        {
            new EngineRecord { Type = "finding", Severity = "ERROR", Id = "IME-MISSING", Title = "The IME service is not installed on PC-CONTOSO-01", NextStep = "Restart the device.", Support = "Check the IME installation.", Utc = T0.AddSeconds(20) },
            new EngineRecord { Type = "summary", Exit = exit, Utc = T0.AddSeconds(30) },
        });

    private static RunLogHeader Header(RunOutcome outcome, DateTime? now = null, LocalRun local = null) => new()
    {
        Outcome = outcome,
        Check = local,
        AppVersion = "1.2.3",
        DeviceName = "PC-CONTOSO-01",
        User = "CONTOSO\\contosouser",
        Zone = Amsterdam,
        NowUtc = now ?? T0.AddMinutes(1),
    };

    // --- Headers -----------------------------------------------------------------------------------

    [Fact]
    public void A_running_header_says_captured_at_and_has_no_end()
    {
        var lines = Header(Running(), T0.AddMinutes(2)).Lines().ToList();
        Assert.Contains("Run ID    : ab12cd34", lines);
        Assert.Contains("Result    : still running (no result yet)", lines);
        Assert.Contains("Exit code : none", lines);
        Assert.Contains("Started   : 2026-10-15 14:00:00 +02:00 (2026-10-15 12:00:00 UTC)", lines);
        Assert.Contains("Captured at : 2026-10-15 14:02:00 +02:00 (2026-10-15 12:02:00 UTC)", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("Ended", StringComparison.Ordinal));

        // A summary record that arrived while the run is still marked running does not end it either.
        var running = Running().With(new[] { new EngineRecord { Type = "summary", Exit = 0, Utc = T0.AddSeconds(9) } });
        Assert.Null(Header(running).EndedUtc);
        Assert.DoesNotContain(Header(running).Lines(), l => l.StartsWith("Ended", StringComparison.Ordinal));
    }

    [Fact]
    public void An_ended_header_has_the_end_from_its_summary()
    {
        var lines = Header(Ended(), T0.AddHours(1)).Lines().ToList();
        Assert.Contains("Result    : Completed (service result 0)", lines);
        Assert.Contains("Exit code : 2", lines);
        Assert.Contains("Ended     : 2026-10-15 14:00:30 +02:00 (2026-10-15 12:00:30 UTC)", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("Captured at", StringComparison.Ordinal));

        // A summary without a time of its own ends at the newest record time before it.
        var untimed = new RunOutcome { Action = ToolbeltAction.SyncClock, Result = ResultCode.Completed, ExitCode = 0 }
            .With(new[] { new EngineRecord { Type = "progress", Text = "a", Utc = T0.AddSeconds(7) }, new EngineRecord { Type = "summary", Exit = 0 } });
        Assert.Equal(T0.AddSeconds(7), Header(untimed).EndedUtc);
    }

    [Fact]
    public void A_check_in_the_app_has_a_local_run_id_and_its_own_times()
    {
        var check = LocalRun.Start("Connectivity test", T0);
        Assert.StartsWith(LocalRun.IdPrefix, check.RunId);
        Assert.Equal("local-".Length + 8, check.RunId.Length);
        Assert.True(LocalRun.IsLocalId(check.RunId));
        Assert.False(LocalRun.IsLocalId("ab12cd34"));
        Assert.NotEqual(check.RunId, LocalRun.Start("Connectivity test", T0).RunId);

        var running = Header(null, T0.AddSeconds(40), check).Lines().ToList();
        Assert.Contains("Action    : Connectivity test", running);
        Assert.Contains("Run ID    : " + check.RunId, running);
        Assert.Contains("Started   : 2026-10-15 14:00:00 +02:00 (2026-10-15 12:00:00 UTC)", running);
        Assert.Contains("Captured at : 2026-10-15 14:00:40 +02:00 (2026-10-15 12:00:40 UTC)", running);
        Assert.DoesNotContain(running, l => l.StartsWith("Ended", StringComparison.Ordinal) || l.StartsWith("Result", StringComparison.Ordinal));

        check.EndedUtc = T0.AddSeconds(30);
        var ended = Header(null, T0.AddMinutes(5), check.Snapshot()).Lines().ToList();
        Assert.Contains("Ended     : 2026-10-15 14:00:30 +02:00 (2026-10-15 12:00:30 UTC)", ended);
        Assert.DoesNotContain(ended, l => l.StartsWith("Captured at", StringComparison.Ordinal));
    }

    // --- The run history ---------------------------------------------------------------------------

    [Fact]
    public void Two_runs_of_the_same_action_are_two_files_that_both_read_back_after_a_reopen()
    {
        var runs = _folder.Combine("runs");
        var first = RunLogStore.Save(Header(Ended("aaaa1111")), new[] { "first run" }, Array.Empty<ResultCard>(), runs);
        var second = RunLogStore.Save(Header(Ended("bbbb2222")), new[] { "second run" }, Array.Empty<ResultCard>(), runs);
        Assert.NotEqual(first, second);
        Assert.Equal("20261015-120000Z_RetryAppsAndPolicies_aaaa1111.log", Path.GetFileName(first));

        // Reopened: nothing in memory, only what the folder holds.
        var stored = RunLogStore.List(runs);
        Assert.Equal(2, stored.Count);
        Assert.Equal(new[] { "aaaa1111", "bbbb2222" }, stored.Select(s => s.RunId).OrderBy(s => s, StringComparer.Ordinal));
        Assert.All(stored, s => Assert.Equal("RetryAppsAndPolicies", s.Action));
        Assert.All(stored, s => Assert.Equal(T0, s.StartedUtc));
        Assert.Contains("first run", File.ReadAllText(stored.Single(s => s.RunId == "aaaa1111").Path));
        Assert.Contains("second run", File.ReadAllText(stored.Single(s => s.RunId == "bbbb2222").Path));
        Assert.Contains("Run ID    : bbbb2222", File.ReadAllText(second));
    }

    [Fact]
    public void A_snapshot_and_the_end_of_one_run_are_one_file()
    {
        var runs = _folder.Combine("runs");
        var snapshot = RunLogStore.Save(Header(Running("cccc3333")), new[] { "so far" }, Array.Empty<ResultCard>(), runs);
        Assert.Contains("Captured at", File.ReadAllText(snapshot));
        // The service's start differs by a few seconds from the app's: the run ID finds the file.
        var final = Ended("cccc3333");
        final.StartedUtc = T0.AddSeconds(2);
        var path = RunLogStore.Save(Header(final), new[] { "all" }, Array.Empty<ResultCard>(), runs);
        Assert.Equal(snapshot, path);
        Assert.Single(Directory.GetFiles(runs));
        var text = File.ReadAllText(path);
        Assert.Contains("Ended     :", text);
        Assert.DoesNotContain("Captured at", text);
        // A snapshot written late (it was taken before the end) does not replace the ended log.
        Assert.Equal(path, RunLogStore.Save(Header(Running("cccc3333")), new[] { "late snapshot" }, Array.Empty<ResultCard>(), runs));
        Assert.Contains("Ended     :", File.ReadAllText(path));

        // A check in the app is stored under its local run ID.
        var check = LocalRun.Start("Reset Company Portal data", T0);
        check.EndedUtc = T0.AddSeconds(3);
        var local = RunLogStore.Save(Header(null, null, check), new[] { "portal" }, Array.Empty<ResultCard>(), runs);
        Assert.Equal("20261015-120000Z_ResetCompanyPortaldata_" + check.RunId + ".log", Path.GetFileName(local));
        Assert.Equal(check.RunId, RunLogStore.Describe(local).RunId);
    }

    [Fact]
    public void The_history_keeps_the_newest_logs_and_trims_the_oldest()
    {
        var runs = _folder.Combine("runs");
        Directory.CreateDirectory(runs);
        for (var i = 0; i < RunLogStore.KeepFiles; i++)
        {
            var old = Path.Combine(runs, RunLogStore.TimePart(T0.AddDays(-100 + i)) + "_SyncClock_" + i.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + ".log");
            File.WriteAllText(old, "old " + i);
            File.SetLastWriteTimeUtc(old, T0.AddDays(-100 + i));
        }
        var newest = RunLogStore.Save(Header(Ended("dddd4444")), new[] { "new" }, Array.Empty<ResultCard>(), runs);
        var left = Directory.GetFiles(runs, "*.log").Select(Path.GetFileName).ToList();
        Assert.Equal(RunLogStore.KeepFiles, left.Count);
        Assert.Contains(Path.GetFileName(newest), left);
        Assert.DoesNotContain(left, n => n.EndsWith("_00000000.log", StringComparison.Ordinal));
        Assert.Contains(left, n => n.EndsWith("_00000001.log", StringComparison.Ordinal));

        // The size cap: everything older than the first log that does not fit goes, never the one just written.
        var removed = RunLogStore.Trim(runs, newest, keepFiles: 100, keepBytes: new FileInfo(newest).Length);
        Assert.Equal(RunLogStore.KeepFiles - 1, removed.Count);
        Assert.Equal(new[] { Path.GetFileName(newest) }, Directory.GetFiles(runs).Select(Path.GetFileName));
        Assert.Empty(RunLogStore.Trim(runs, newest, keepFiles: 1, keepBytes: 1));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void A_write_that_is_cut_off_keeps_the_previous_log()
    {
        // Every step names itself when it fails, so a failure says where it happened.
        var runs = _folder.Combine("runs");
        var path = Step("the first snapshot", () => RunLogStore.Save(Header(Running("eeee5555")), new[] { "valid snapshot" }, Array.Empty<ResultCard>(), runs));

        // The log text fails half way: nothing is written.
        static IEnumerable<string> Broken()
        {
            yield return "half";
            throw new IOException("cut off");
        }
        Step("a log text cut off", () =>
        {
            Assert.Throws<IOException>(() => RunLogStore.Save(Header(Ended("eeee5555")), Broken(), Array.Empty<ResultCard>(), runs));
            Assert.Contains("valid snapshot", File.ReadAllText(path));
        });

        // The swap fails (another program holds the log for longer than the swap waits): the
        // previous log stays, no temporary file is left.
        Step("a swap while another program holds the log", () =>
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Record.Exception(() => RunLogStore.Save(Header(Ended("eeee5555")), new[] { "final" }, Array.Empty<ResultCard>(), runs));
                Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            }
            Assert.Contains("valid snapshot", File.ReadAllText(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(runs));
        });

        // The copy to last-run.log is swapped in the same way.
        Step("the copy to last-run.log", () =>
        {
            var latest = _folder.Combine("last-run.log");
            File.WriteAllText(latest, "older");
            RunLogStore.CopyToLatest(path, latest);
            Assert.Contains("valid snapshot", File.ReadAllText(latest));
            Assert.Single(Directory.GetFiles(_folder.Path));
        });
    }

    [Fact]
    public void A_short_hold_by_another_program_is_waited_out_and_the_new_file_is_swapped_in()
    {
        // A scan right after the write holds the log for a moment; the swap waits it out.
        var target = _folder.Combine("run.log");
        File.WriteAllText(target, "previous");
        var temp = _folder.Combine("run.log.new.tmp");
        File.WriteAllText(temp, "new");
        using var holder = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        var waits = new List<TimeSpan>();
        // The hold ends during the first wait, as a scan would.
        FileSwap.Into(temp, target, wait: w => { waits.Add(w); holder.Dispose(); });
        Assert.Equal(new[] { FileSwap.Waits[0] }, waits);
        Assert.Equal("new", File.ReadAllText(target));
        Assert.False(File.Exists(temp));

        // A new log (no previous one) whose new file is held for a moment is moved in the same way.
        var fresh = _folder.Combine("fresh.log");
        var freshTemp = _folder.Combine("fresh.log.tmp");
        File.WriteAllText(freshTemp, "first");
        using var freshHolder = new FileStream(freshTemp, FileMode.Open, FileAccess.Read, FileShare.Read);
        var freshWaits = 0;
        FileSwap.Into(freshTemp, fresh, wait: _ => { freshWaits++; freshHolder.Dispose(); });
        Assert.Equal(1, freshWaits);
        Assert.Equal("first", File.ReadAllText(fresh));
    }

    [Fact]
    public void A_hold_that_lasts_fails_after_the_bounded_waits_and_keeps_the_previous_file()
    {
        var target = _folder.Combine("run.log");
        File.WriteAllText(target, "previous");
        var temp = _folder.Combine("run.log.new.tmp");
        File.WriteAllText(temp, "new");
        var waits = new List<TimeSpan>();
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Assert.ThrowsAny<IOException>(() => FileSwap.Into(temp, target, wait: waits.Add));
            Assert.True(FileSwap.IsHeldByAnother(error), error.ToString());
        }
        Assert.Equal(FileSwap.Waits, waits);
        Assert.True(waits.Aggregate(TimeSpan.Zero, (a, b) => a + b) <= FileSwap.RetryBudget);
        Assert.Equal("previous", File.ReadAllText(target));
        // The caller removes its own temporary file; the swap leaves it for that.
        Assert.Equal("new", File.ReadAllText(temp));
    }

    [Fact]
    public void An_error_other_than_a_hold_is_not_retried()
    {
        var temp = _folder.Combine("run.log.tmp");
        File.WriteAllText(temp, "new");
        var waits = 0;
        // The target's folder does not exist: no other program can make that go away.
        Assert.ThrowsAny<IOException>(() => FileSwap.Into(temp, _folder.Combine("missing", "run.log"), wait: _ => waits++));
        Assert.Equal(0, waits);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), true)]  // sharing violation
    [InlineData(unchecked((int)0x80070021), true)]  // lock violation
    [InlineData(unchecked((int)0x80070497), true)]  // 1175 unable to remove replaced
    [InlineData(unchecked((int)0x80070498), true)]  // 1176 unable to move replacement
    [InlineData(unchecked((int)0x80070499), true)]  // 1177 unable to move replacement 2
    [InlineData(unchecked((int)0x80070003), false)] // path not found
    [InlineData(unchecked((int)0x80070070), false)] // disk full
    [InlineData(unchecked((int)0x80040020), false)] // not a Win32 error
    public void Only_a_hold_by_another_program_counts_as_one(int hresult, bool held)
    {
        Assert.Equal(held, FileSwap.IsHeldByAnother(new IOException("x", hresult)));
    }

    private static T Step<T>(string name, Func<T> step)
    {
        try { return step(); }
        catch (Exception ex) { throw new Xunit.Sdk.XunitException("Step '" + name + "' failed: " + ex); }
    }

    private static void Step(string name, Action step) => Step<object>(name, () => { step(); return null; });

    [Fact]
    public void Logs_without_a_run_id_are_history_with_unknown_fields()
    {
        var runs = _folder.Combine("runs");
        Directory.CreateDirectory(runs);
        File.WriteAllText(Path.Combine(runs, "last-run.log"), "a log of an earlier version");
        File.WriteAllText(Path.Combine(runs, "20261015-120000Z_FullReset_none.log"), "a run without an ID");
        var stored = RunLogStore.List(runs);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, s => Assert.True(s.Historical));
        var older = stored.Single(s => Path.GetFileName(s.Path) == "last-run.log");
        Assert.Null(older.StartedUtc);
        Assert.Null(older.Action);
        var noId = stored.Single(s => s.Action == "FullReset");
        Assert.Equal(T0, noId.StartedUtc);
        Assert.Null(noId.RunId);

        // A service run without an ID gets a new file every time.
        var bare = new RunOutcome { Action = ToolbeltAction.FullReset, Result = ResultCode.EngineCrashed, StartedUtc = T0 };
        var a = RunLogStore.Save(Header(bare), new[] { "1" }, Array.Empty<ResultCard>(), runs);
        var b = RunLogStore.Save(Header(bare), new[] { "2" }, Array.Empty<ResultCard>(), runs);
        Assert.NotEqual(a, b);
    }

    // --- Copy details for IT -------------------------------------------------------------------------------

    [Fact]
    public void The_ticket_of_an_ended_run_names_its_run_and_no_device_or_user()
    {
        var outcome = Ended("ab12cd34", exit: 1);
        var view = ResultPresenter.Final(outcome, zone: Amsterdam);
        var text = RunTicket.Text(Header(outcome, T0.AddHours(2)), view.Cards, view.Status, view.Tone);
        var lines = text.TrimEnd().Split(new[] { Environment.NewLine }, StringSplitOptions.None);
        Assert.Equal(5, lines.Length);
        Assert.Equal("Endpoint Toolbelt · Retry work apps and settings · Reference ab12cd34", lines[0]);
        Assert.Equal("Evidence · 2026-10-15 14:00 UTC+02:00 · engine summary · PROBLEM: The IME service is not installed on [device]", lines[1]);
        // A run that changed nothing says so on its Action line (the drawer shows it as a muted line, not a card).
        Assert.Equal("Action · Retry work apps and settings, run by the service as SYSTEM; nothing on this device was changed", lines[2]);
        Assert.StartsWith("Outcome · Retry work apps and settings did not succeed", lines[3]);
        Assert.Equal("Next · User: Restart the device. IT support: Check the IME installation.", lines[4]);
        Assert.DoesNotContain("PC-CONTOSO-01", text);
        Assert.DoesNotContain("contosouser", text);
        Assert.DoesNotContain("CONTOSO\\", text);
        // No log lines.
        Assert.DoesNotContain("== Step", text);
        Assert.DoesNotContain("RESULT:", text);
    }

    [Fact]
    public void Nothing_changed_is_a_note_not_a_card_and_the_support_package_has_none()
    {
        // The note is never the ticket's top finding and never a Change card.
        var view = ResultPresenter.Final(Ended("ab12cd34", exit: 0), zone: Amsterdam);
        var note = Assert.Single(view.Cards, c => c.Kind == CardKind.Note);
        Assert.Equal("Nothing on this device was changed.", note.Title);
        Assert.DoesNotContain(view.Cards, c => c.Kind == CardKind.Change);
        Assert.NotEqual(CardKind.Note, RunTicket.TopCard(view.Cards).Kind);
        // The support package says what it wrote in its own card: no note.
        var package = ResultPresenter.Final(Ended("ab12cd34", exit: 0, action: ToolbeltAction.CollectDiag), zone: Amsterdam);
        Assert.DoesNotContain(package.Cards, c => c.Kind == CardKind.Note);
        // A run with changes has the change card and no note.
        var changed = ResultPresenter.Final(Ended("ab12cd34", exit: 0).With(new[] { new EngineRecord { Type = "change", Text = "Restarted the IME", Utc = T0.AddSeconds(25) } }), zone: Amsterdam);
        Assert.Single(changed.Cards, c => c.Kind == CardKind.Change);
        Assert.DoesNotContain(changed.Cards, c => c.Kind == CardKind.Note);
        var ticket = RunTicket.Text(Header(Ended("ab12cd34", exit: 0), T0.AddHours(2)), changed.Cards, changed.Status, changed.Tone);
        Assert.DoesNotContain("nothing on this device was changed", ticket);
    }

    [Fact]
    public void The_ticket_of_a_running_run_points_to_that_run_and_says_it_still_runs()
    {
        var text = RunTicket.Text(Header(Running("ffff6666"), T0.AddMinutes(3)), Array.Empty<ResultCard>(), "Retry work apps and settings is running...", Tone.Neutral);
        Assert.Contains("Reference ffff6666", text);
        Assert.Contains("Evidence · 2026-10-15 14:03 UTC+02:00 · service run, still running · no result yet", text);
        Assert.Contains("Outcome · Still running when this was copied; no result yet.", text);
        Assert.Contains("Next · Wait until it finishes and copy again; the reference stays the same.", text);
        Assert.DoesNotContain("PC-CONTOSO-01", text);

        // A run whose ID has not arrived yet says so.
        Assert.Contains("Reference none", RunTicket.Text(Header(Running(null)), Array.Empty<ResultCard>(), "", Tone.Neutral));
    }

    [Fact]
    public void The_ticket_never_calls_an_unverified_run_a_success()
    {
        // Exit 0 with its summary: verified.
        var done = Ended("ab12cd34", exit: 0, action: ToolbeltAction.SyncClock);
        done.Records.RemoveAll(r => r.Type == "finding");
        var verified = RunTicket.Text(Header(done), Array.Empty<ResultCard>(), "Sync clock completed successfully.", Tone.Success);
        Assert.Contains("Outcome · Sync clock completed successfully.", verified);
        Assert.Contains("Next · None needed.", verified);

        // A success tone without the summary that confirms it: not confirmed.
        var noSummary = new RunOutcome { Action = ToolbeltAction.SyncClock, RunId = "ab12cd35", Result = ResultCode.Completed, ExitCode = 0 };
        var unverified = RunTicket.Text(Header(noSummary), Array.Empty<ResultCard>(), "Sync clock completed successfully.", Tone.Success);
        Assert.Contains("Outcome · Finished, but the result is not confirmed.", unverified);
        Assert.Contains("service result Completed, no engine summary", unverified);
        Assert.Contains("Next · IT support: review the run log of this reference.", unverified);

        // Retry requested with the apps' state unknown is neutral, so it is passed on as it is.
        var (status, tone) = ResultPresenter.RetryDone(0, 0, AppsAfterRetry.Unknown);
        var retry = RunTicket.Text(Header(Ended("ab12cd36", exit: 0)), Array.Empty<ResultCard>(), status, tone);
        Assert.Contains("Outcome · Retry requested.", retry);
        Assert.DoesNotContain("None needed", retry);
    }

    [Fact]
    public void The_ticket_of_a_check_in_the_app_uses_its_local_run_id()
    {
        var check = LocalRun.Start("Connectivity test", T0);
        var running = RunTicket.Text(Header(null, T0.AddSeconds(10), check), Array.Empty<ResultCard>(), "Testing the connection to the Intune endpoints...", Tone.Neutral);
        Assert.StartsWith("Endpoint Toolbelt · Connectivity test · Reference " + check.RunId + Environment.NewLine, running);
        Assert.Contains("check in the app, still running", running);
        Assert.Contains("Outcome · Still running", running);

        check.EndedUtc = T0.AddSeconds(20);
        var cards = new[] { ResultCard.From(FindingSeverity.Warning, "1 endpoint not reachable through the proxy for contosouser", "", "Tell your IT support.") };
        var ended = RunTicket.Text(Header(null, T0.AddMinutes(1), check), cards, "Connectivity test: 1 endpoint needs checking.", Tone.Warning);
        Assert.Contains("Evidence · 2026-10-15 14:00 UTC+02:00 · check in the app · WARNING: 1 endpoint not reachable through the proxy for [user]", ended);
        Assert.Contains("Action · Connectivity test, run in the app as the signed-in user", ended);
        Assert.Contains("Next · User: Tell your IT support.", ended);
        Assert.DoesNotContain("contosouser", ended);
    }

    [Fact]
    public void Scrubbing_replaces_whole_names_only()
    {
        var header = Header(null);
        header.User = "CONTOSO\\user";
        Assert.Equal("the [user] and [user] but not username", RunTicket.Scrub("the CONTOSO\\user and user but not username", header));
        Assert.Equal("[device] is offline", RunTicket.Scrub("pc-contoso-01 is offline", header));
    }
}
