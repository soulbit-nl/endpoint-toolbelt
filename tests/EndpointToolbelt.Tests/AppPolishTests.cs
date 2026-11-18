using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Protocol;
using Xunit;

namespace EndpointToolbelt.Tests;

public class AppTimelineTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    private static EngineRecord Record(string json) => EngineRecord.Parse(json);

    [Fact]
    public void The_record_time_is_read_as_utc_and_a_missing_or_odd_one_is_null()
    {
        var record = Record("{\"type\":\"progress\",\"ts\":\"2026-10-15T12:02:05.123Z\",\"text\":\"a\"}");
        Assert.Equal(new DateTime(2026, 10, 15, 12, 2, 5, 123, DateTimeKind.Utc), record.Utc);
        Assert.Equal(DateTimeKind.Utc, record.Utc.Value.Kind);
        Assert.Null(Record("{\"type\":\"progress\",\"text\":\"a\"}").Utc);
        // An unreadable time does not cost the record.
        var odd = Record("{\"type\":\"progress\",\"ts\":\"yesterday\",\"text\":\"a\"}");
        Assert.NotNull(odd);
        Assert.Null(odd.Utc);
        Assert.Equal("a", odd.Text);
        Assert.Null(Record("{\"type\":\"progress\",\"ts\":5,\"text\":\"a\"}").Utc);
        // Only the engine's exact format, with or without milliseconds.
        Assert.Equal(new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc), Record("{\"type\":\"progress\",\"ts\":\"2026-10-15T12:00:00Z\"}").Utc);
        Assert.Null(Record("{\"type\":\"progress\",\"ts\":\"2026-10-15T14:00:00+02:00\"}").Utc);
        Assert.Null(Record("{\"type\":\"progress\",\"ts\":\"2026-10-15 12:00:00\"}").Utc);
        Assert.Null(Record("{\"type\":\"progress\",\"ts\":\"15-10-2026\"}").Utc);
    }

    [Fact]
    public void Every_log_line_has_the_local_time_of_its_record_and_one_without_a_time_has_none()
    {
        var records = new[]
        {
            Record("{\"type\":\"progress\",\"ts\":\"2026-10-15T12:02:05.000Z\",\"text\":\"== Step 1/2: Diagnosis ==\"}"),
            Record("{\"type\":\"progress\",\"text\":\"no time\"}"),
            Record("{\"type\":\"warning\",\"ts\":\"2026-12-01T12:00:00.000Z\",\"text\":\"careful\"}"),
        };
        var lines = ResultPresenter.LogLines(records, Amsterdam).ToList();
        Assert.Equal("14:02:05 == Step 1/2: Diagnosis ==", lines[0]);
        Assert.Equal("no time", lines[1]);
        // Winter time in the same zone: the change is marked once, before the line.
        Assert.Equal("----- 2026-12-01, local time, UTC+01:00 (" + Amsterdam.StandardName + ") -----", lines[2]);
        Assert.Equal("13:00:00 WARNING: careful", lines[3]);
        Assert.Equal(4, lines.Count);
        Assert.Equal("12:02:05 == Step 1/2: Diagnosis ==", ResultPresenter.LogLines(records, TimeZoneInfo.Utc).First());
    }

    [Fact]
    public void Findings_and_changes_are_part_of_the_timeline_with_both_next_steps()
    {
        var records = new[]
        {
            Record("{\"type\":\"finding\",\"ts\":\"2026-10-15T12:00:00.000Z\",\"severity\":\"ERROR\",\"id\":\"WU-X\",\"title\":\"Broken\",\"detail\":\"line one\\nline two\",\"next\":\"Restart.\",\"support\":\"Check the policy.\"}"),
            Record("{\"type\":\"change\",\"ts\":\"2026-10-15T12:00:01.000Z\",\"text\":\"Emptied the download cache\"}"),
        };
        var lines = ResultPresenter.LogLines(records, TimeZoneInfo.Utc).ToList();
        Assert.Equal(new[]
        {
            "12:00:00 FINDING PROBLEM WU-X: Broken",
            "             line one",
            "             line two",
            "             Next step: Restart.",
            "             For IT support: Check the policy.",
            "12:00:01 CHANGE: Emptied the download cache",
        }, lines);
        var card = Assert.Single(ResultPresenter.FindingCards(records));
        Assert.Equal("Restart.", card.NextStep);
        Assert.Equal("Check the policy.", card.Support);
    }

    [Fact]
    public void The_final_log_ends_with_a_result_line()
    {
        var outcome = new RunOutcome { Action = ToolbeltAction.RepairWU, Result = ResultCode.Completed, ExitCode = 2 };
        outcome.Records.Add(new EngineRecord { Type = "change", Text = "x" });
        outcome.Records.Add(new EngineRecord { Type = "summary", Exit = 2, Utc = new DateTime(2026, 10, 15, 12, 30, 0, DateTimeKind.Utc) });
        var view = ResultPresenter.Final(outcome, zone: TimeZoneInfo.Utc);
        Assert.Equal("12:30:00 RESULT: completed with warnings (exit 2), 1 change, restart: no", view.LogLines.Last());

        var restart = new RunOutcome { Action = ToolbeltAction.RepairWU, Result = ResultCode.Completed, ExitCode = 0 };
        restart.Records.Add(new EngineRecord { Type = "summary", Exit = 0, Restart = true });
        var lines = ResultPresenter.Final(restart).LogLines;
        Assert.Equal("RESULT: completed (exit 0), no changes, restart: yes", lines[lines.Count - 2]);
        Assert.Equal(ResultPresenter.RestartNowLine, lines.Last());

        var interrupted = ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.FullReset, Result = ResultCode.Interrupted });
        Assert.Equal("RESULT: interrupted (no exit code, service result Interrupted), no changes, restart: no", interrupted.LogLines.Last());
        var hresult = ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.FullReset, Result = ResultCode.Completed, ExitCode = unchecked((int)0x8007042B) });
        Assert.StartsWith("RESULT: finished with code 0x8007042B (exit 0x8007042B)", hresult.LogLines.Last());
    }

    [Fact]
    public void The_downloads_log_has_a_header_cards_and_the_timeline()
    {
        using var folder = new TestFolder();
        var outcome = new RunOutcome { Action = ToolbeltAction.RetryAppsAndPolicies, RunId = "run-1", Result = ResultCode.Completed, ExitCode = 2, StartedUtc = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc) };
        outcome.Records.Add(new EngineRecord { Type = "summary", Exit = 2, Utc = new DateTime(2026, 10, 15, 12, 4, 30, DateTimeKind.Utc) });
        var view = ResultPresenter.Final(outcome, zone: Amsterdam);
        Assert.True(view.CopyLog);
        var header = new RunLogHeader { Outcome = outcome, AppVersion = "1.2.3", DeviceName = "PC1", User = "CONTOSO\\user", Zone = Amsterdam, NowUtc = new DateTime(2026, 10, 15, 12, 5, 0, DateTimeKind.Utc) };
        var path = SupportPackage.SaveRunLog(header, view.LogLines, view.Cards, folder.Combine("Downloads"));
        Assert.EndsWith("EndpointToolbelt_RetryAppsAndPolicies_run-1.log", path);
        var text = File.ReadAllText(path);
        Assert.Contains("===== ENDPOINT TOOLBELT 1.2.3 - RUN LOG =====", text);
        Assert.Contains("Device    : PC1", text);
        Assert.Contains("User      : CONTOSO\\user", text);
        Assert.Contains("Time zone : local time, UTC+02:00 (" + Amsterdam.DaylightName + "), now", text);
        Assert.Contains("----- TECHNICAL LOG (times in local time, UTC+02:00 (" + Amsterdam.DaylightName + ")) -----", text);
        Assert.Contains("Action    : Retry work apps and settings (RetryAppsAndPolicies)", text);
        Assert.Contains("Run ID    : run-1", text);
        Assert.Contains("Result    : Completed (service result 0)", text);
        Assert.Contains("Exit code : 2", text);
        Assert.Contains("Started   : 2026-10-15 14:00:00 +02:00 (2026-10-15 12:00:00 UTC)", text);
        Assert.Contains("Ended     : 2026-10-15 14:04:30 +02:00 (2026-10-15 12:04:30 UTC)", text);
        Assert.Contains("[CHANGES] Nothing on this device was changed.", text);
        Assert.Contains("14:04:30 RESULT: completed with warnings (exit 2), no changes, restart: no", text);

        // A run without a start time or run ID still gets a header; without its summary its end is
        // not confirmed, so the header says when it was captured.
        var bare = new RunLogHeader { Outcome = new RunOutcome { Action = ToolbeltAction.FullReset, Result = ResultCode.EngineCrashed }, Zone = TimeZoneInfo.Utc, NowUtc = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc) };
        var lines = bare.Lines().ToList();
        Assert.Contains("Run ID    : none", lines);
        Assert.Contains("Exit code : none", lines);
        Assert.Contains("Started   : unknown", lines);
        Assert.Contains("Captured at : 2026-01-01 08:00:00 +00:00 (2026-01-01 08:00:00 UTC)", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("Ended", StringComparison.Ordinal));
    }
}

public class AppWordingTests
{
    private static RunOutcome Outcome(ToolbeltAction action, int exit, params EngineRecord[] records)
    {
        var outcome = new RunOutcome { Action = action, Result = ResultCode.Completed, ExitCode = exit };
        outcome.Records.AddRange(records);
        return outcome;
    }

    private static EngineRecord Finding(string severity, string title) => new() { Type = "finding", Severity = severity, Title = title, Detail = "", NextStep = "" };

    [Fact]
    public void One_word_set_and_counts_without_brackets()
    {
        Assert.Equal("PROBLEM", FindingSeverityText.Word(FindingSeverity.Error));
        Assert.Equal("WARNING", FindingSeverityText.Word(FindingSeverity.Warning));
        Assert.Equal("INFO", FindingSeverityText.Word(FindingSeverity.Info));
        Assert.Equal("OK", FindingSeverityText.Word(FindingSeverity.Ok));
        Assert.Equal("CHANGED ON THIS DEVICE", ResultCard.Change("t", "d").Word);
        Assert.Equal("OK", new HealthResult { Status = HealthStatus.Pass }.StatusWord);
        Assert.Equal("Warning", new HealthResult { Status = HealthStatus.Warn }.StatusWord);
        Assert.Equal("Problem", new HealthResult { Status = HealthStatus.Fail }.StatusWord);
        Assert.Equal("1 check", Words.Count(1, "check"));
        Assert.Equal("0 checks", Words.Count(0, "check"));
        Assert.Equal("2 entries", Words.Count(2, "entry", "entries"));
    }

    [Fact]
    public void The_health_summary_uses_singular_and_plural()
    {
        HealthResult Row(HealthStatus status) => new() { Name = "x", Status = status };
        Assert.Equal(("Device looks healthy.", Tone.Success), HealthChecks.Summary(new[] { Row(HealthStatus.Pass) }));
        Assert.Equal(("1 check needs attention. See the list above.", Tone.Warning), HealthChecks.Summary(new[] { Row(HealthStatus.Warn) }));
        Assert.Equal(("2 checks need attention. See the list above.", Tone.Warning), HealthChecks.Summary(new[] { Row(HealthStatus.Warn), Row(HealthStatus.Warn) }));
        // A row without a fix or a step of its own: no step, and never Retry work apps and settings.
        Assert.Equal(("2 problems found. See the list above.", Tone.Error),
            HealthChecks.Summary(new[] { Row(HealthStatus.Fail), Row(HealthStatus.Fail), Row(HealthStatus.Warn) }));
    }

    [Fact]
    public void A_problem_that_remains_is_an_error_and_warnings_alone_stay_warnings()
    {
        var problem = ResultPresenter.Final(Outcome(ToolbeltAction.RepairWU, 2, Finding("WARNING", "behind"), Finding("ERROR", "install failed"), new EngineRecord { Type = "mdm-queued" }));
        Assert.Equal(Tone.Error, problem.Tone);
        Assert.Equal("Repair Windows Update finished, but a problem remains: install failed", problem.Status);
        var afterSuccess = ResultPresenter.Final(Outcome(ToolbeltAction.RetryAppsAndPolicies, 0, Finding("ERROR", "still failing"), new EngineRecord { Type = "summary", Exit = 0, Restart = true }));
        Assert.Equal(Tone.Error, afterSuccess.Tone);
        Assert.True(afterSuccess.OpenDrawer);
        Assert.StartsWith("Retry work apps and settings finished, but a problem remains", afterSuccess.Status);

        var warning = ResultPresenter.Final(Outcome(ToolbeltAction.RepairWU, 2, Finding("WARNING", "behind")));
        Assert.Equal(Tone.Warning, warning.Tone);
        Assert.Equal("Repair Windows Update completed with warnings: behind", warning.Status);
        Assert.Equal("Repair Windows Update finished; some points need checking. See the details.", ResultPresenter.Final(Outcome(ToolbeltAction.RepairWU, 2)).Status);
        // A failure without a finding names its code in words a person can pass on.
        Assert.Equal("Full IME reset did not succeed (code 1). See the details; create a support package if it repeats.", ResultPresenter.Final(Outcome(ToolbeltAction.FullReset, 1)).Status);
        // The up-to-date sentence stays as it is.
        Assert.Equal("Repair Windows Update: nothing to repair - this device is already up to date.", ResultPresenter.Final(Outcome(ToolbeltAction.RepairWU, 69003)).Status);
    }

    [Fact]
    public void Texts_point_to_the_details_not_to_a_side()
    {
        foreach (var exit in new[] { 1, 2, 87, 69001, 69002, 69003, 5 })
        {
            var view = ResultPresenter.Final(Outcome(ToolbeltAction.FullReset, exit));
            Assert.DoesNotContain("on the right", view.Status);
            if (view.Status.Contains("details")) { Assert.True(view.OpenDrawer); }
        }
        Assert.Contains("See the details", ResultPresenter.Final(Outcome(ToolbeltAction.FullReset, 5)).Status);
    }

    private const string AppsPending = " Whether the failed apps install is not known yet: the IME retries them in the background, which can take a while, especially for large apps.";

    [Fact]
    public void Retry_texts_promise_nothing()
    {
        // The apps' state is unknown after the retry, so nothing says "Done" or shows success.
        var requested = ResultPresenter.RetryDone(0, 0, AppsAfterRetry.Unknown);
        Assert.Equal("Retry requested. Intune was asked to check this device again." + AppsPending, requested.Text);
        Assert.Equal(Tone.Neutral, requested.Tone);
        var remains = ResultPresenter.RetryDone(2, 0, AppsAfterRetry.Unknown);
        Assert.Equal("Retry requested, but 2 problems remain in the health checks." + AppsPending + " Create a support package if the problems stay.", remains.Text);
        Assert.Equal(Tone.Warning, remains.Tone);
        Assert.StartsWith("Retry requested, but 1 problem remains in the health checks.", ResultPresenter.RetryDone(1, 3, AppsAfterRetry.Unknown).Text);
        foreach (var fails in new[] { 0, 1 })
        {
            foreach (var warns in new[] { 0, 2 })
            {
                var text = ResultPresenter.RetryDone(fails, warns, AppsAfterRetry.Unknown);
                Assert.DoesNotContain("Done", text.Text);
                Assert.NotEqual(Tone.Success, text.Tone);
            }
        }
    }

    [Fact]
    public void A_retry_skipped_for_other_users_says_sync_requested_never_retry_requested()
    {
        var outcome = new RunOutcome { Action = ToolbeltAction.RetryAppsAndPolicies };
        Assert.False(ResultPresenter.RetrySkipped(outcome));
        outcome.Records.Add(new EngineRecord { Type = "finding", Id = "IME-REFRESH-SKIPPED", Severity = "info" });
        Assert.True(ResultPresenter.RetrySkipped(outcome));
        foreach (var fails in new[] { 0, 1 })
        {
            foreach (var warns in new[] { 0, 2 })
            {
                foreach (var apps in new[] { AppsAfterRetry.Unknown, AppsAfterRetry.AllDetected, AppsAfterRetry.SomeFailing })
                {
                    var text = ResultPresenter.RetryDone(fails, warns, apps, retrySkipped: true);
                    Assert.StartsWith("Sync requested", text.Text);
                    Assert.DoesNotContain("Retry requested", text.Text);
                    Assert.DoesNotContain("Done", text.Text);
                    Assert.Contains("The app retry was skipped", text.Text);
                    Assert.NotEqual(Tone.Success, text.Tone);
                }
            }
        }
    }

    [Fact]
    public void Retry_with_no_failures_but_a_warning_says_so()
    {
        var one = ResultPresenter.RetryDone(0, 1, AppsAfterRetry.Unknown);
        Assert.Equal("Retry requested. No problems in the health checks, but 1 point needs checking." + AppsPending, one.Text);
        Assert.Equal(Tone.Warning, one.Tone);
        Assert.StartsWith("Retry requested. No problems in the health checks, but 2 points need checking.", ResultPresenter.RetryDone(0, 2, AppsAfterRetry.Unknown).Text);
    }

    [Fact]
    public void Retry_says_done_only_when_the_apps_were_detected_and_the_checks_are_clean()
    {
        // Retry done, app still unknown: requested, not done (above). Apps detected again:
        Assert.Equal(("Done. Every app that was retried is now detected, and the health checks are clean.", Tone.Success), ResultPresenter.RetryDone(0, 0, AppsAfterRetry.AllDetected));
        Assert.Equal(("Every app that was retried is now detected, but 1 point needs checking in the health checks. See the list above.", Tone.Warning), ResultPresenter.RetryDone(0, 1, AppsAfterRetry.AllDetected));
        Assert.Equal(Tone.Error, ResultPresenter.RetryDone(2, 0, AppsAfterRetry.AllDetected).Tone);
        // Apps that still fail are never a success, also with clean health checks.
        Assert.Equal(("Retry requested, but apps still fail to install. Create a support package if this stays.", Tone.Warning), ResultPresenter.RetryDone(0, 0, AppsAfterRetry.SomeFailing));
        Assert.Equal(("Retry requested, but apps still fail to install, and 1 problem remains in the health checks. Create a support package if this stays.", Tone.Error), ResultPresenter.RetryDone(1, 0, AppsAfterRetry.SomeFailing));
    }

    [Fact]
    public void An_interrupted_run_without_a_message_gets_a_card_detail()
    {
        var view = ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.RepairWU, Result = ResultCode.Interrupted });
        Assert.Equal(ResultPresenter.InterruptedDetail, view.Cards[0].Detail);
        Assert.Equal("The run ended without its result. The technical log shows how far it got.", view.Cards[0].Detail);
        var withMessage = ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.RepairWU, Result = ResultCode.Interrupted, Message = "service text" });
        Assert.Equal("service text", withMessage.Cards[0].Detail);
    }

    [Fact]
    public void Running_is_neutral_and_a_background_run_names_its_step()
    {
        var run = new RunOutcome { Action = ToolbeltAction.RepairWU, StartedUtc = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc) };
        run.Records.Add(new EngineRecord { Type = "progress", Text = "== Step 1/3: Diagnosis ==" });
        run.Records.Add(new EngineRecord { Type = "progress", Text = "reading" });
        run.Records.Add(new EngineRecord { Type = "progress", Text = "== Step 2/3: Repair ==" });
        var view = ResultPresenter.Live(run, new DateTime(2026, 10, 15, 12, 5, 0, DateTimeKind.Utc));
        Assert.Equal(Tone.Neutral, view.Tone);
        Assert.Equal("Step 2/3: Repair", view.Step);
        Assert.Equal("Repair Windows Update is running in the background (5 min elapsed). Step 2/3: Repair. You can close this window.", view.Status);
        Assert.Null(ResultPresenter.CurrentStep(new[] { new EngineRecord { Type = "progress", Text = "no step" } }));
        Assert.Equal(Tone.Neutral, ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.RepairWU, InProgress = true }).Tone);
    }

    [Fact]
    public void A_changed_session_count_is_confirmed_again_only_for_actions_that_affect_everyone()
    {
        Assert.True(ResultPresenter.AsksToConfirmSessionsAgain(new RunOutcome { Action = ToolbeltAction.FullReset, Result = ResultCode.ConfirmationRequired }));
        Assert.True(ResultPresenter.AsksToConfirmSessionsAgain(new RunOutcome { Action = ToolbeltAction.RepairWU, Result = ResultCode.ConfirmationRequired }));
        Assert.False(ResultPresenter.AsksToConfirmSessionsAgain(new RunOutcome { Action = ToolbeltAction.ClearAppCache, Result = ResultCode.ConfirmationRequired }));
        Assert.False(ResultPresenter.AsksToConfirmSessionsAgain(new RunOutcome { Action = ToolbeltAction.FullReset, Result = ResultCode.Completed }));
        Assert.False(ResultPresenter.AsksToConfirmSessionsAgain(null));
    }

    [Fact]
    public void A_card_shows_the_support_step_on_its_own_line_and_reads_it_out()
    {
        var card = ResultCard.From(FindingSeverity.Error, "Broken", "evidence", "Restart.", "Check the policy.");
        Assert.Equal("Next step: Restart.", card.NextStepLine);
        Assert.Equal("For IT support: Check the policy.", card.SupportLine);
        Assert.Equal("PROBLEM: Broken. evidence Next step: Restart. For IT support: Check the policy.", card.AccessibleText);
        var plain = ResultCard.From(FindingSeverity.Info, "Note");
        Assert.Equal("", plain.SupportLine);
        Assert.Equal("", plain.NextStepLine);
        Assert.Equal("INFO: Note.", plain.AccessibleText);
    }

    [Fact]
    public void The_app_diagnosis_promises_nothing_and_says_it_changed_nothing()
    {
        // The diagnosis reads; its status never says fixed or done, and success means only "read".
        var now = new DateTime(2026, 11, 10, 14, 0, 0, DateTimeKind.Utc);
        var verdicts = new[] { "PACKAGING", "RESTART-FIRST", "NOT-APPLICABLE", "DEPENDENCY", "RETRY-HELPS", "UNKNOWN", "OK", "IN-PROGRESS", "PENDING", "NO-STATE", "AVAILABLE", "NO-ACTION" };
        foreach (var verdict in verdicts)
        {
            var finding = AppDecisions.Decide(new AppEvidence { AppId = "11111111-2222-3333-4444-555555555555", Name = "Contoso Agent", Scope = "device", Intent = "required", Verdict = verdict, ObservedText = "2026-11-10T13:00:00Z" },
                Array.Empty<AppEvidence>(), now);
            var text = finding.Title + " " + finding.Text + " " + finding.NextText;
            Assert.DoesNotContain("fixed", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Done", text, StringComparison.Ordinal);
            Assert.DoesNotContain("—", text);
            // Only an app the IME found with its detection rule is called detected.
            Assert.Equal(verdict == "OK", finding.Observation == AppObservation.Detected);
            // At most one primary step, and a step the window can act on or say.
            Assert.True(Enum.IsDefined(typeof(AppNextStep), finding.Next));
        }
        var none = new AppDiagnosis { Run = DiagnosisRunState.Finished, Present = true, Records = 4 };
        Assert.Equal(("App diagnosis finished: no app needs attention. Nothing was changed.", Tone.Success), AppDecisions.Status(none));
        Assert.Equal("No app needs attention", Assert.Single(none.Cards()).Title);
        Assert.Equal("App diagnosis", ActionText.Name(ToolbeltAction.DiagnoseApps));
    }
}

public class AppHealthViewTests
{
    private sealed class Source : IHealthSource, IProxyRoutes
    {
        public DateTime UtcNow { get; } = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        public string ImeServiceStatus() => "Running";
        public DsregInfo Dsreg() => new() { AzureAdJoined = "YES", AzureAdPrt = "NO", TenantName = "Contoso" };
        public DateTime? MdmCertificateExpiryUtc() => UtcNow.AddDays(400);
        public bool CanReach(string host, int port, int timeoutMs) => true;
        public double FreeSystemDriveGB() => 50;
        public bool ServicingRestartPending() => false;
        public Uri BrowserProxy(Uri target) => null;
        public (Uri Proxy, string Bypass)? MachineProxy() => null;
        public HttpsObservation Https(HttpsRequest request) => new() { TlsSeen = true, MicrosoftChain = true, Status = 200 };
    }

    [Fact]
    public void Plain_names_on_screen_and_technical_names_in_the_report()
    {
        var results = HealthChecks.Run(new Source(), new StatusInfo { Enrolled = true, LastSyncUtc = "2026-10-15T11:00:00Z" }, out var dsreg);
        Assert.Equal(new[] { "App installer (IME)", "Intune enrollment", "Last Intune sync", "Last IME check-in", "Entra joined", "Work account sign-in", "Windows Hello", "Intune certificate", "Connection to Intune", "Free disk space", "Pending restart", "Compliance checks", "Configuration profiles", "Clock", "Microsoft Defender" },
            results.Select(r => r.Name).ToArray());
        var report = DeviceReport.Build(new DeviceFacts(), dsreg, results, Array.Empty<string>(), new DateTimeOffset(2026, 10, 15, 14, 0, 0, TimeSpan.FromHours(2)), "1.0.0");
        Assert.Contains("[OK     ] IME service ", report);
        Assert.Contains("[OK     ] MDM enrollment ", report);
        Assert.Contains("[OK     ] Last MDM sync ", report);
        Assert.Contains("[Warning] Sign-in token (PRT) ", report);
        Assert.Contains("[OK     ] MDM certificate ", report);
        Assert.Contains("[OK     ] Intune connectivity ", report);
        Assert.Contains("[OK     ] Join type (dsregcmd) ", report);
        Assert.DoesNotContain("Work account sign-in", report);
        // Without readouts the four device settings are not read yet; the report keeps their technical names.
        Assert.Contains("[Warning] Compliance settings (local) Unknown: still being read", report);
        Assert.Contains("[Warning] Time sync ", report);
        Assert.Equal("Unknown name", HealthChecks.ReportName("Unknown name"));
    }

    [Fact]
    public void Only_a_row_that_is_not_OK_shows_its_hint_below()
    {
        var results = HealthChecks.Run(new Source(), new StatusInfo { Enrolled = true, LastSyncUtc = "2026-10-15T11:00:00Z" }, out _);
        var ok = results.Single(r => r.Name == HealthChecks.ImeService);
        var check = results.Single(r => r.Name == HealthChecks.Prt);
        Assert.Equal("", ok.HintBelow);
        // With a fix link only the first sentence goes below; the whole hint is the tooltip.
        Assert.Equal(HealthFix.RefreshSignIn, check.Fix);
        Assert.Equal("Your sign-in token for single sign-on is missing or unknown.", check.HintBelow);
        Assert.Equal(check.Hint, check.HintAsToolTip);

        Assert.Equal(ok.Hint, ok.HintAsToolTip);

        // Without a fix link the whole hint goes below, so the user still reads what to do.
        var noFix = new HealthResult { Name = HealthChecks.Enrollment, Status = HealthStatus.Fail, Detail = "x", Hint = "No enrollment. Contact your IT support." };
        Assert.Equal(noFix.Hint, noFix.HintBelow);
        Assert.Null(noFix.HintAsToolTip);

        // Without the service, the status line says what to do: the rows it answers do not repeat it.
        var unavailable = HealthChecks.Run(new Source(), null, out _).Single(r => r.Name == HealthChecks.Enrollment);
        Assert.Equal(HealthStatus.Warn, unavailable.Status);
        Assert.Equal("", unavailable.HintBelow);
        Assert.Equal(HealthChecks.ServiceUnavailableHint, unavailable.HintAsToolTip);
    }

    [Fact]
    public void The_report_names_the_service_the_time_zone_and_the_last_run()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var generated = new DateTimeOffset(2026, 10, 15, 14, 0, 0, TimeSpan.FromHours(2));
        var status = new StatusInfo { ServiceVersion = "1.2.3", LastResultAction = ToolbeltAction.RepairWU, LastResultRunId = "run-7" };
        var report = DeviceReport.Build(new DeviceFacts(), null, new List<HealthResult>(), Array.Empty<string>(), generated, "1.2.3", status, zone);
        Assert.Contains("Service   : available (version 1.2.3)", report);
        Assert.Contains("Time zone : local time, UTC+02:00 (" + zone.DaylightName + ")", report);
        Assert.Contains("Last run  : Repair Windows Update, finished (run ID run-7)", report);
        status.LastResultCode = ResultCode.Completed;
        status.LastResultExitCode = 69003;
        Assert.Contains("Last run  : Repair Windows Update, nothing to repair (exit 69003) (run ID run-7)",
            DeviceReport.Build(new DeviceFacts(), null, new List<HealthResult>(), Array.Empty<string>(), generated, "1.2.3", status, zone));
        status.LastResultCode = ResultCode.Interrupted;
        status.LastResultExitCode = null;
        Assert.Contains("Last run  : Repair Windows Update, interrupted (run ID run-7)",
            DeviceReport.Build(new DeviceFacts(), null, new List<HealthResult>(), Array.Empty<string>(), generated, "1.2.3", status, zone));
        var none = DeviceReport.Build(new DeviceFacts(), null, new List<HealthResult>(), Array.Empty<string>(), generated, "1.2.3", null, zone);
        Assert.Contains("Service   : not available", none);
        Assert.DoesNotContain("Last run", none);
    }

    [Theory]
    [InlineData("2026-10-15 14:02:11 +02:00;user;RepairWU;69003", "2026-10-15 14:02 +02:00  Repair Windows Update: nothing to repair (69003)")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;RetryAppsAndPolicies;0", "2026-10-15 14:02 +02:00  Retry work apps and settings: completed (0)")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;FullReset;1", "2026-10-15 14:02 +02:00  Full IME reset: failed (1)")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;ClearAppCache;2", "2026-10-15 14:02 +02:00  Clear downloaded app content: completed with warnings (2)")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;FullReset;69001", "2026-10-15 14:02 +02:00  Full IME reset: skipped: cooldown active (69001)")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;RepairWU;69002", "2026-10-15 14:02 +02:00  Repair Windows Update: blocked: restart pending (69002)")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;CollectDiag;timeout", "2026-10-15 14:02 +02:00  Create support package: timed out waiting for the result")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;RepairWU;interrupted", "2026-10-15 14:02 +02:00  Repair Windows Update: interrupted")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;FullReset;not-started", "2026-10-15 14:02 +02:00  Full IME reset: did not start")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;FullReset;EngineCrashed", "2026-10-15 14:02 +02:00  Full IME reset: stopped unexpectedly (EngineCrashed)")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;ConnTest;0", "2026-10-15 14:02 +02:00  Check Microsoft connections: all required endpoints reachable (0)")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;ConnTest;1", "2026-10-15 14:02 +02:00  Check Microsoft connections: 1 required endpoint unreachable (1)")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;ConnTest;3", "2026-10-15 14:02 +02:00  Check Microsoft connections: 3 required endpoints unreachable (3)")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;ResetCompanyPortal;0", "2026-10-15 14:02 +02:00  Reset Company Portal data: reset done (0)")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;ResetCompanyPortal;1", "2026-10-15 14:02 +02:00  Reset Company Portal data: not reset (1)")]
    // Unknown values are shown as they are.
    [InlineData("2026-10-15 14:02:11 +02:00;user;FullReset;42", "2026-10-15 14:02 +02:00  Full IME reset: 42")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;FutureAction;weird", "2026-10-15 14:02 +02:00  FutureAction: weird")]
    [InlineData("2026-10-15 14:02:11 +02:00;user;7;0", "2026-10-15 14:02 +02:00  7: completed (0)")]
    [InlineData("not a history line", "not a history line")]
    [InlineData("yesterday;user;FullReset;0", "yesterday;user;FullReset;0")]
    public void History_lines_are_readable(string line, string expected)
    {
        Assert.Equal(expected, RunWords.HistoryLine(line));
    }

    [Fact]
    public void The_report_renders_the_history_readably()
    {
        var report = DeviceReport.Build(new DeviceFacts(), null, new List<HealthResult>(), new[] { "2026-10-15 14:02:11 +02:00;user;RepairWU;69003" }, DateTimeOffset.Now, "1.0.0");
        Assert.Contains("2026-10-15 14:02 +02:00  Repair Windows Update: nothing to repair (69003)", report);
    }
}

public class AppWindowRuleTests
{
    [Fact]
    public void Without_the_service_only_the_repairs_are_off()
    {
        var all = (AppButton[])Enum.GetValues(typeof(AppButton));
        var needService = new[] { AppButton.Retry, AppButton.Package, AppButton.ClearCache, AppButton.FullReset, AppButton.RepairWu, AppButton.BackupKey, AppButton.SyncClock, AppButton.UpdateDefender, AppButton.FreeSpace, AppButton.Diagnose, AppButton.RenewIp, AppButton.CheckWu };
        foreach (var button in all)
        {
            Assert.True(ButtonRules.IsEnabled(button, busy: false, serviceMissing: false));
            Assert.False(ButtonRules.IsEnabled(button, busy: true, serviceMissing: false));
            Assert.False(ButtonRules.IsEnabled(button, busy: true, serviceMissing: true));
            Assert.Equal(!needService.Contains(button), ButtonRules.IsEnabled(button, busy: false, serviceMissing: true));
        }
        Assert.Contains("service is not available, so repairs cannot start. Press Check this PC again on Home", ButtonRules.ServiceMissing);
        Assert.Contains("You can still view the device report.", ButtonRules.ServiceMissing);
    }

    [Fact]
    public void A_long_run_is_announced_at_start_at_each_step_and_at_the_end()
    {
        // Set once (start, end, any message): always.
        Assert.True(Announcements.ShouldAnnounce(StatusChange.Once, null, null));
        Assert.True(Announcements.ShouldAnnounce(StatusChange.Once, "Step 1/2: A", "Step 1/2: A"));
        // The minute updates: only when the step changed.
        Assert.False(Announcements.ShouldAnnounce(StatusChange.LiveUpdate, null, null));
        Assert.True(Announcements.ShouldAnnounce(StatusChange.LiveUpdate, null, "Step 1/2: A"));
        Assert.False(Announcements.ShouldAnnounce(StatusChange.LiveUpdate, "Step 1/2: A", "Step 1/2: A"));
        Assert.True(Announcements.ShouldAnnounce(StatusChange.LiveUpdate, "Step 1/2: A", "Step 2/2: B"));
    }
}

/// <summary>The helper character's rules, the narrow and wide layout, and Home's severity word.</summary>
public class AppHelperRuleTests
{
    [Fact]
    public void The_helper_shows_unless_high_contrast_is_on()
    {
        // Part of the brand; no user setting or policy turns it off.
        Assert.True(HelperRules.Visible(highContrast: false));
        Assert.False(HelperRules.Visible(highContrast: true));
        // Reduced motion (Windows animations off): still poses.
        Assert.False(HelperRules.Animate(windowsAnimations: false));
        Assert.True(HelperRules.Animate(windowsAnimations: true));
    }

    [Fact]
    public void The_mascots_mood_follows_runs_how_they_ended_the_service_and_the_page()
    {
        // Working wins over everything: something runs.
        Assert.Equal(HelperMood.Working, HelperRules.ForPage(running: true, justEnded: true, Tone.Error, serviceMissing: true, pageShowsProblem: true));
        // For a moment after something ended: the tone it ended with (done is the happy bounce).
        Assert.Equal(HelperMood.Done, HelperRules.ForPage(false, justEnded: true, Tone.Success, serviceMissing: false, pageShowsProblem: true));
        Assert.Equal(HelperMood.Attention, HelperRules.ForPage(false, justEnded: true, Tone.Warning, false, false));
        Assert.Equal(HelperMood.Error, HelperRules.ForPage(false, justEnded: true, Tone.Error, false, false));
        // A neutral end (a start requested) is no reaction: the page's own mood.
        Assert.Equal(HelperMood.Attention, HelperRules.ForPage(false, justEnded: true, Tone.Neutral, false, pageShowsProblem: true));
        // The service cannot be reached; the concerned look while the page shows a problem; idle otherwise.
        Assert.Equal(HelperMood.Error, HelperRules.ForPage(false, false, Tone.Neutral, serviceMissing: true, pageShowsProblem: true));
        Assert.Equal(HelperMood.Attention, HelperRules.ForPage(false, false, Tone.Neutral, false, pageShowsProblem: true));
        Assert.Equal(HelperMood.Idle, HelperRules.ForPage(false, false, Tone.Success, false, false));
    }

    [Fact]
    public void The_small_helper_works_while_a_run_goes_and_then_takes_the_tone_it_ended_with()
    {
        Assert.Equal(HelperMood.Working, HelperRules.ForRun(running: true, Tone.Error));
        Assert.Equal(HelperMood.Done, HelperRules.ForRun(false, Tone.Success));
        Assert.Equal(HelperMood.Attention, HelperRules.ForRun(false, Tone.Warning));
        Assert.Equal(HelperMood.Error, HelperRules.ForRun(false, Tone.Error));
        Assert.Equal(HelperMood.Idle, HelperRules.ForRun(false, Tone.Neutral));
    }

    [Fact]
    public void There_is_no_wide_layout_any_more_only_one_column_of_at_most_900()
    {
        // The old two steps (the left pane, the panes side by side) are gone with their thresholds.
        Assert.Null(typeof(WindowLayout).GetMethod("IsWide"));
        Assert.Null(typeof(WindowLayout).GetMethod("IsSideBySide"));
        Assert.Equal(900, WindowLayout.ContentMaxWidth);
    }

    [Fact]
    public void Homes_severity_is_written_out_for_a_problem_or_a_point_that_needs_attention()
    {
        Assert.Equal("PROBLEM", HomeSummary.SeverityWord(new HomeFinding { Tone = Tone.Error }));
        Assert.Equal("WARNING", HomeSummary.SeverityWord(new HomeFinding { Tone = Tone.Warning }));
        Assert.Equal("", HomeSummary.SeverityWord(new HomeFinding { Tone = Tone.Success }));
        Assert.Equal("", HomeSummary.SeverityWord(new HomeFinding { Tone = Tone.Neutral }));
        Assert.Equal("", HomeSummary.SeverityWord(null));
        // The service being away is a problem.
        Assert.Equal("PROBLEM", HomeSummary.SeverityWord(HomeSummary.Build(Array.Empty<HealthResult>(), null, serviceMissing: true)));
    }

    [Fact]
    public void The_helper_has_no_setting_no_policy_value_and_no_status_field()
    {
        // The former "Show helper character", ShowHelperCharacter and StatusInfo.HelperCharacterOff are gone.
        Assert.Null(typeof(AppSettings).GetProperty("ShowHelper"));
        Assert.Null(typeof(AppSettings).GetProperty("HelperOffByPolicy"));
        Assert.Null(typeof(StatusInfo).GetProperty("HelperCharacterOff"));
        Assert.Null(typeof(EndpointToolbelt.Service.Configuration.ServiceConfig).GetProperty("ShowHelperCharacter"));
        // A settings file an older test build wrote still loads; its helper values are ignored.
        using var folder = new TestFolder();
        var path = folder.Combine("settings.json");
        File.WriteAllText(path, "{\"Left\":10,\"Top\":10,\"Width\":540,\"ShowHelper\":false,\"HelperOffByPolicy\":true}");
        Assert.Equal(540, AppSettings.Load(path).Width);
        // A status from an older test build with the old field: read without it (protocol version 1 unchanged).
        var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(StatusInfo));
        using var old = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"ProtocolVersion\":1,\"ServiceVersion\":\"1.1.1\",\"HelperCharacterOff\":true}"));
        Assert.Equal("1.1.1", ((StatusInfo)serializer.ReadObject(old)).ServiceVersion);
        Assert.Equal(1, ProtocolInfo.Version);
    }

    [Fact]
    public void An_exit_2_with_only_INFO_findings_reads_completed_INFO_in_the_history_not_with_warnings()
    {
        // Retry work apps and settings with a sync Windows already queued: exit 2, findings OK and INFO.
        var outcome = new RunOutcome { Action = ToolbeltAction.RetryAppsAndPolicies, RunId = "8a2a9a11", Result = ResultCode.Completed, ExitCode = 2 }.With(new[]
        {
            new EngineRecord { Type = "finding", Severity = "OK", Id = "APPS-OK", Title = "No Win32 app needs attention" },
            new EngineRecord { Type = "finding", Severity = "INFO", Id = "MDM-SYNC-QUEUED", Title = "A sync is already queued by Windows; no new one was sent" },
        });
        Assert.Equal("2|8a2a9a11|info", ResultPresenter.HistoryResult(outcome));
        Assert.EndsWith("Retry work apps and settings: completed (INFO) (2, reference 8a2a9a11)", RunWords.HistoryLine("2026-11-17 15:08:36 +01:00;user;RetryAppsAndPolicies;2|8a2a9a11|info"));
        // A warning keeps the words of exit 2; an older line (the number alone) too.
        var warned = new RunOutcome { Action = ToolbeltAction.RetryAppsAndPolicies, RunId = "8a2a9a12", Result = ResultCode.Completed, ExitCode = 2 }.With(new[] { new EngineRecord { Type = "finding", Severity = "WARNING", Id = "APPS-FAILED", Title = "Apps need attention" } });
        Assert.Equal("2", ResultPresenter.HistoryResult(warned));
        Assert.EndsWith("completed with warnings (2)", RunWords.HistoryLine("2026-11-17 15:08:36 +01:00;user;RetryAppsAndPolicies;2"));
    }
}
