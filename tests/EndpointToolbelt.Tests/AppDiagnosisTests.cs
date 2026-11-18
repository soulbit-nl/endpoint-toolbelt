using System;
using System.Collections.Generic;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.Actions;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The app diagnosis in the app. Each engine verdict becomes one observed state and one next
/// step; the run's state and the app's state stay apart; Home and "An app won't install" show the
/// same diagnosis; an older service or none is said in words, with the handover.
/// </summary>
public class AppDiagnosisTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 14, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    private const string AppA = "11111111-2222-3333-4444-555555555555";
    private const string AppB = "22222222-3333-4444-5555-666666666666";

    internal static AppEvidence App(string verdict, string intent = "required", string code = "", bool download = false, string id = AppA, string name = "Contoso Agent",
        string observed = "2026-11-10T13:00:00Z", string nextRetry = "", string scope = "device", int revision = 3) => new()
    {
        DiagnosisId = "diag0001",
        AppId = id,
        Name = name,
        Scope = scope,
        Intent = intent,
        Revision = revision,
        Verdict = verdict,
        State = "a state",
        Code = code,
        ObservedText = observed,
        NextRetryText = nextRetry,
        Download = download,
    };

    private static AppFinding Decide(AppEvidence app, params AppEvidence[] others) => AppDecisions.Decide(app, new[] { app }.Concat(others).ToList(), Now, Amsterdam);

    [Fact]
    public void Installed_but_not_detected_hands_over_to_the_packaging_team_and_never_suggests_a_retry()
    {
        var f = Decide(App("PACKAGING", code: "0x87D1041C"));
        Assert.Equal(AppCase.InstalledNotDetected, f.Case);
        Assert.Equal(AppObservation.Failed, f.Observation);
        Assert.Equal(AppNextStep.CopyForIt, f.Next);
        Assert.Contains("packaging team must check the package and its detection rule", f.Text);
        Assert.Equal("", f.Caveat);
        Assert.DoesNotContain("Retry work apps and settings", f.Text + f.NextText);
        Assert.Contains("0x87D1041C", f.Support);
        Assert.Equal(FindingSeverity.Error, f.Severity);
    }

    [Fact]
    public void A_download_error_on_a_required_app_offers_the_connectivity_test_and_warns_about_Retry_first()
    {
        var f = Decide(App("RETRY-HELPS", code: "0x80072EE2", download: true));
        Assert.Equal(AppCase.DownloadError, f.Case);
        Assert.Equal(AppNextStep.ConnectivityTest, f.Next);
        Assert.Equal(AppDecisions.RetryCaveat, f.Caveat);
        Assert.Contains("Retry work apps and settings", f.NextText);
        // A download that is stuck while the IME is still busy with it is the same case.
        Assert.Equal(AppCase.DownloadError, Decide(App("IN-PROGRESS", download: true)).Case);
        // A plain install failure: Retry is the step, with the same warning said beforehand.
        var failed = Decide(App("RETRY-HELPS", code: "0x80070643"));
        Assert.Equal(AppNextStep.RetryAppsAndPolicies, failed.Next);
        Assert.Equal(AppDecisions.RetryCaveat, failed.Caveat);
        Assert.Contains("every failed app", failed.Card().Detail);
    }

    [Fact]
    public void An_available_app_that_failed_goes_to_its_Company_Portal_page()
    {
        var f = Decide(App("RETRY-HELPS", intent: "available", download: true));
        Assert.Equal(AppCase.AvailableFailed, f.Case);
        Assert.Equal(AppNextStep.OpenCompanyPortal, f.Next);
        Assert.Equal("Open in Company Portal", HomeSummary.StepText(f.Next));
        Assert.Equal(AppNextStep.OpenCompanyPortal, Decide(App("AVAILABLE", intent: "available")).Next);
    }

    [Fact]
    public void An_offered_app_with_an_old_record_says_it_may_be_out_of_date_and_keeps_Company_Portal()
    {
        // Fresh: no doubt about the record.
        Assert.True(string.IsNullOrEmpty(Decide(App("AVAILABLE", intent: "available", scope: "user")).Uncertainty));
        // Fourteen days old (as on a shared virtual desktop, where the user's apps are fetched again
        // when Company Portal opens): said so, and Company Portal stays the step, which renews it.
        var old = Decide(App("AVAILABLE", intent: "available", scope: "user", observed: "2026-10-27T08:16:00Z"));
        Assert.Equal(AppCase.Offered, old.Case);
        Assert.Equal(AppNextStep.OpenCompanyPortal, old.Next);
        Assert.Contains("may be out of date: opening Company Portal brings it up to date", old.Uncertainty);
    }

    [Fact]
    public void Restart_dependency_requirements_and_a_planned_retry_each_have_their_own_step()
    {
        var restart = Decide(App("RESTART-FIRST"));
        Assert.Equal(AppNextStep.Restart, restart.Next);
        Assert.Equal(AppObservation.AwaitingEvidence, restart.Observation);
        Assert.Equal("", HomeSummary.StepText(restart.Next));

        var dependency = App("RETRY-HELPS", intent: "dependency", id: AppB, name: "Contoso Runtime");
        var blocked = Decide(App("DEPENDENCY"), dependency);
        Assert.Equal(AppCase.DependencyBlocked, blocked.Case);
        Assert.Contains("Contoso Runtime", blocked.Text);
        var unnamed = Decide(App("DEPENDENCY"));
        Assert.Equal(AppNextStep.CopyForIt, unnamed.Next);
        Assert.Contains("does not record which app", unnamed.Text);

        var applicability = Decide(App("NOT-APPLICABLE"));
        Assert.Equal(AppCase.RequirementsNotMet, applicability.Case);
        Assert.Equal(AppNextStep.CopyForIt, applicability.Next);
        Assert.Contains("IT support", applicability.Text);

        var planned = Decide(App("RETRY-HELPS", nextRetry: "2026-11-11T09:30:00Z"));
        Assert.Equal(AppCase.RetryPlanned, planned.Case);
        Assert.Equal(AppNextStep.Wait, planned.Next);
        // The estimated time in the user's zone, with the zone.
        Assert.Contains("2026-11-11 10:30 (UTC+01:00)", planned.Text);
        Assert.Contains("estimate", planned.Text);
        // A retry time in the past is no plan.
        Assert.Equal(AppCase.InstallFailed, Decide(App("RETRY-HELPS", nextRetry: "2026-11-09T09:30:00Z")).Case);
    }

    [Theory]
    [InlineData("0x87D30065")]
    [InlineData("0x87d30000")]
    [InlineData("0x87D1041D")]
    public void An_Intune_code_Microsoft_does_not_describe_is_said_so_and_handed_to_IT_not_retried(string code)
    {
        foreach (var f in new[] { Decide(App("RETRY-HELPS", code: code)), Decide(App("RETRY-HELPS", code: code, nextRetry: "2026-11-11T09:30:00Z")) })
        {
            Assert.Equal(AppCase.InstallFailed, f.Case);
            Assert.Equal(AppNextStep.CopyForIt, f.Next);
            Assert.Contains("Intune reported an error that Microsoft does not describe (" + code + ").", f.Text);
            Assert.Equal("Use Copy details for IT so IT support can check it.", f.NextText);
            Assert.Equal("", f.Caveat);
        }
        // A documented Intune code keeps its own route.
        Assert.Equal(AppCase.InstalledNotDetected, Decide(App("PACKAGING", code: "0x87D1041C")).Case);
        Assert.False(AppDecisions.IsUndocumentedIntuneCode("0x87D300C9"));
        Assert.False(AppDecisions.IsUndocumentedIntuneCode("0x80070643"));
        Assert.False(AppDecisions.IsUndocumentedIntuneCode(""));
    }

    [Fact]
    public void The_apps_documented_Intune_codes_are_exactly_the_engines()
    {
        var engine = EndpointToolbelt.Engine.Ime.CodeKnowledge.Table.Keys.Where(k => k.StartsWith("0x87d", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal);
        var app = AppDecisions.DocumentedIntuneCodes.Select(c => c.ToLowerInvariant()).OrderBy(k => k, StringComparer.Ordinal);
        Assert.Equal(engine, app);
    }

    [Fact]
    public void A_canceled_install_says_it_was_canceled_and_a_new_attempt_is_the_step()
    {
        var f = Decide(App("RETRY-HELPS", code: "0x80070642"));
        Assert.Equal(AppCase.InstallFailed, f.Case);
        Assert.Equal(AppNextStep.RetryAppsAndPolicies, f.Next);
        Assert.StartsWith("The installation was canceled before it finished (exit code 1602, 0x80070642).", f.Text, StringComparison.Ordinal);
        Assert.Equal("exit code 1602: the installation was canceled before it finished", EndpointToolbelt.Engine.Ime.AppState.ErrorText(0x80070642));
        // Any other failure keeps its words.
        Assert.StartsWith("The install failed on this device (exit code 1603, 0x80070643).", Decide(App("RETRY-HELPS", code: "0x80070643")).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Uncertain_evidence_is_said_and_handed_over()
    {
        var unknown = Decide(App("UNKNOWN", code: "0x12345678"));
        Assert.Equal(AppObservation.Unknown, unknown.Observation);
        Assert.Equal(AppNextStep.CopyForIt, unknown.Next);
        Assert.NotEqual("", unknown.Uncertainty);

        // A stale record: the step becomes the handover, the reason is said.
        var stale = Decide(App("RETRY-HELPS", download: true, observed: "2026-10-01T08:00:00Z"));
        Assert.Equal(AppNextStep.CopyForIt, stale.Next);
        Assert.Contains("2026-10-01 10:00 (UTC+02:00) and may be out of date", stale.Uncertainty);

        // A record without a time: said, not hidden.
        Assert.Contains("time of this record is not known", Decide(App("RETRY-HELPS", observed: "")).Uncertainty);

        // An app another user also has: no log evidence for it.
        var shared = App("RETRY-HELPS");
        shared.Shared = true;
        Assert.Contains("Another user on this device has this app too", Decide(shared).Uncertainty);
    }

    internal static RunOutcome Outcome(params AppEvidence[] apps)
    {
        var records = apps.Select(a => new EngineRecord { Type = "app-diagnosis", App = a, Utc = Now.AddSeconds(-30) }).ToList();
        records.Add(new EngineRecord { Type = "apps-diagnosis", Closing = new DiagnosisClose { Present = true, Records = apps.Length, Listed = apps.Length, UserScope = true }, Utc = Now.AddSeconds(-20) });
        records.Add(new EngineRecord { Type = "summary", Exit = 0, Changes = 0, Utc = Now.AddSeconds(-10) });
        return new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "diag0001", Result = ResultCode.Completed, ExitCode = 0 }.With(records);
    }

    [Fact]
    public void Two_apps_of_one_name_wait_for_the_user_to_choose_and_Home_says_so()
    {
        var diagnosis = AppDecisions.From(Outcome(App("RETRY-HELPS"), App("RETRY-HELPS", id: AppB, scope: "user", revision: 1)), Now, Amsterdam);
        Assert.All(diagnosis.Apps, a => Assert.True(a.SameNameAsAnother));
        Assert.Contains("the one for this device, revision 3, last checked by the device on 10 Nov, ID 11111111-2222-3333-4444-555555555555", diagnosis.Apps[0].Uncertainty);
        Assert.Contains("the one assigned to you, revision 1, last checked by the device on 10 Nov, ID 22222222-3333-4444-5555-666666666666", diagnosis.Apps[1].Uncertainty);
        var home = HomeSummary.Build(new List<HealthResult>(), diagnosis, serviceMissing: false);
        Assert.Equal(AppNextStep.ChooseApp, home.AppStep);
        Assert.StartsWith("More than one app is called Contoso Agent", home.Title);
    }

    [Fact]
    public void Home_and_the_app_path_show_the_same_diagnosis_for_the_same_data()
    {
        var diagnosis = AppDecisions.From(Outcome(App("OK"), App("PACKAGING", id: AppB, name: "Contoso Viewer", code: "0x87D1041C")), Now, Amsterdam);
        var rows = new List<HealthResult> { new() { Name = HealthView.Network, Status = HealthStatus.Warn, Hint = "Check the network." } };
        var home = HomeSummary.Build(rows, diagnosis, serviceMissing: false);
        // The very finding object the app list shows, with its step: not a copy, not another rule.
        Assert.Same(diagnosis.Main, home.App);
        Assert.Same(diagnosis.Apps.Single(a => a.App.AppId == AppB), home.App);
        Assert.Equal(home.App.Title, home.Title);
        Assert.Equal(AppNextStep.CopyForIt, home.AppStep);
        Assert.Equal("Copy details for IT", home.StepText);
        // The same input twice gives the same decisions.
        var again = AppDecisions.From(Outcome(App("OK"), App("PACKAGING", id: AppB, name: "Contoso Viewer", code: "0x87D1041C")), Now, Amsterdam);
        Assert.Equal(diagnosis.Apps.Select(a => (a.Case, a.Next, a.Title)), again.Apps.Select(a => (a.Case, a.Next, a.Title)));

        // Without a diagnosis Home shows the worst health row and its fix; with a clean device, that nothing was found.
        var health = HomeSummary.Build(new List<HealthResult> { new() { Name = HealthChecks.Connectivity, Status = HealthStatus.Fail, Hint = "Not reachable. Run the test." } }, null, false);
        Assert.Equal(HomeStep.HealthFix, health.Step);
        Assert.Equal(HealthFix.ConnectivityTest, health.Fix);
        var clean = HomeSummary.Build(new List<HealthResult> { new() { Name = HealthView.Network, Status = HealthStatus.Pass } }, null, false);
        Assert.Equal(HomeSummary.CleanTitle, clean.Title);
        Assert.Equal(HomeStep.None, clean.Step);
        Assert.Contains("Check my apps", clean.Text);
        Assert.Equal(HomeLink.CheckApps, clean.Link);
    }

    [Fact]
    public void The_run_state_and_the_app_state_are_two_things()
    {
        var running = AppDecisions.From(new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "diag0002", InProgress = true }, Now, Amsterdam);
        Assert.Equal(DiagnosisRunState.Running, running.Run);
        Assert.StartsWith("Diagnosis: running", AppDecisions.RunLine(running, Amsterdam));

        var finished = AppDecisions.From(Outcome(App("RETRY-HELPS")), Now, Amsterdam);
        Assert.Equal(DiagnosisRunState.Finished, finished.Run);
        // The run line has the reading time in the user's zone, with the zone; it never says an app's state.
        var line = AppDecisions.RunLine(finished, Amsterdam);
        Assert.Contains("finished at 2026-11-10 14:59 (local time, UTC+01:00", line);
        // No reference in the status line; it stays in View details, the run log and Copy details for IT.
        Assert.DoesNotContain("diag0001", line);
        Assert.DoesNotContain("Failed", line);
        Assert.Equal(AppObservation.Failed, finished.Apps[0].Observation);
        Assert.Equal(new[] { "Awaiting evidence", "Detected", "Failed", "Unknown" },
            new[] { AppObservation.AwaitingEvidence, AppObservation.Detected, AppObservation.Failed, AppObservation.Unknown }.Select(AppDecisions.Word));

        // A run that ended without its summary is interrupted, whatever its apps said so far.
        var cut = new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "diag0003", Result = ResultCode.Interrupted }
            .With(new[] { new EngineRecord { Type = "app-diagnosis", App = App("OK"), Utc = Now } });
        var interrupted = AppDecisions.From(cut, Now, Amsterdam);
        Assert.Equal(DiagnosisRunState.Interrupted, interrupted.Run);
        Assert.Equal(AppCase.DiagnosisFailed, interrupted.Problem.Case);
        Assert.Equal(AppObservation.Detected, interrupted.Apps[0].Observation);
    }

    [Fact]
    public void An_older_service_or_no_service_is_said_in_words_with_the_handover()
    {
        // A service that does not know DiagnoseApps refuses it as an action outside its table.
        var old = new RunOutcome { Action = ToolbeltAction.DiagnoseApps, Result = ResultCode.InvalidRequest, Message = "Unknown action." };
        var diagnosis = AppDecisions.From(old, Now, Amsterdam);
        Assert.Equal(AppCase.ServiceTooOld, diagnosis.Problem.Case);
        Assert.Equal(AppNextStep.CopyForIt, diagnosis.Problem.Next);
        Assert.Contains("older version", diagnosis.Problem.Text);
        var view = ResultPresenter.Final(old, zone: Amsterdam);
        Assert.Contains("not available on this device yet", view.Status);
        Assert.DoesNotContain("did not accept", view.Status);
        Assert.Equal(Tone.Warning, view.Tone);
        Assert.False(view.OpenDrawer);

        var offline = AppDecisions.From(null, Now);
        Assert.Equal(AppCase.ServiceOffline, offline.Problem.Case);
        Assert.Equal(AppNextStep.CopyForIt, offline.Main.Next);
        Assert.Equal(AppCase.ServiceOffline, AppDecisions.From(new RunOutcome { Action = ToolbeltAction.DiagnoseApps, Result = ResultCode.ServiceUnavailable }, Now).Problem.Case);
        var home = HomeSummary.Build(new List<HealthResult>(), offline, serviceMissing: true);
        Assert.Equal(AppNextStep.CopyForIt, home.AppStep);
        Assert.Contains("service is not available", home.Title);

        // Busy: no handover, just later.
        var busy = AppDecisions.From(new RunOutcome { Action = ToolbeltAction.DiagnoseApps, Result = ResultCode.Busy }, Now);
        Assert.Equal(AppNextStep.None, busy.Problem.Next);
    }

    [Fact]
    public void My_app_is_not_in_the_list_shows_the_uncertainty_and_the_handover()
    {
        var diagnosis = AppDecisions.From(Outcome(App("OK")), Now, Amsterdam);
        diagnosis.UserScope = false;
        diagnosis.Records = 160;
        diagnosis.Listed = 150;
        var f = AppDecisions.NotListed(diagnosis);
        Assert.Equal(AppCase.NotListed, f.Case);
        Assert.Equal(AppNextStep.CopyForIt, f.Next);
        Assert.Contains("may not be assigned to you or to this device", f.Text);
        Assert.Contains("Without a record", f.Uncertainty);
        Assert.Contains("Only the apps of this device could be read", f.Uncertainty);
        Assert.Contains("10 app records are not listed", f.Uncertainty);
    }

    [Fact]
    public void The_ticket_of_an_app_finding_points_to_the_diagnosis_and_carries_the_app_identity()
    {
        var outcome = Outcome(App("PACKAGING", code: "0x87D1041C"));
        var diagnosis = AppDecisions.From(outcome, Now, Amsterdam);
        var header = new RunLogHeader { Outcome = outcome, DeviceName = "PC-CONTOSO-01", User = "CONTOSO\\contosouser", Zone = Amsterdam, NowUtc = Now };
        var (status, tone) = AppDecisions.Status(diagnosis);
        var ticket = RunTicket.Text(header, diagnosis.Cards(), status, tone);
        Assert.Contains("App diagnosis · Reference diag0001", ticket);
        Assert.Contains("PROBLEM: Contoso Agent: installed, but not detected", ticket);
        Assert.Contains("App ID " + AppA + " (device assignment, revision 3)", ticket);
        Assert.Contains("detection rule", ticket);
        Assert.DoesNotContain("Use Retry apps", ticket);
        Assert.Contains("App diagnosis, run by the service as SYSTEM", ticket);
    }

    [Fact]
    public void The_engine_records_reach_the_app_list_end_to_end()
    {
        // The engine's real record lines, read back by the app's parser.
        var h = new EngineHarness();
        h.Clock.UtcNow = Now;
        EngineAppDiagnosisTests.AddApp(h.Registry, EngineAppDiagnosisTests.Device, AppA, 3, intent: 3, state: 5000, code: EndpointToolbelt.Engine.Ime.AppState.NotDetectedCode - 4294967296L);
        EngineAppDiagnosisTests.AddApp(h.Registry, EngineAppDiagnosisTests.Device, AppB, 1, intent: 1, state: 5003);
        var run = h.NewRun("diag0009", "DiagnoseApps");
        Assert.Equal(EngineExitCode.Success, new DiagnoseAppsHandler().Run(run));
        RunFrame.WriteSummary(run, EngineExitCode.Success);
        var outcome = new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "diag0009", Result = ResultCode.Completed, ExitCode = 0 }
            .With(h.Lines.Select(EngineRecord.Parse).ToList());

        var diagnosis = AppDecisions.From(outcome, Now, Amsterdam);
        Assert.Equal(DiagnosisRunState.Finished, diagnosis.Run);
        Assert.Equal(new[] { AppCase.InstalledNotDetected, AppCase.AvailableFailed }, diagnosis.Apps.Select(a => a.Case));
        Assert.All(diagnosis.Apps, a => Assert.Equal("diag0009", a.DiagnosisId));
        Assert.Equal(DateTime.SpecifyKind(new DateTime(2026, 11, 10, 13, 0, 0), DateTimeKind.Utc), diagnosis.Apps[0].App.ObservedUtc);

        var view = ResultPresenter.Final(outcome, zone: Amsterdam);
        Assert.Equal("App diagnosis finished: 2 apps need attention. Nothing was changed.", view.Status);
        Assert.Contains(view.Cards, c => c.Title == "App " + AppA.Substring(0, 8) + ": installed, but not detected");
        Assert.Contains(view.Cards, c => c.Kind == CardKind.Note && c.Title == ResultCard.NothingChangedText);
        Assert.Contains(view.LogLines, l => l.Contains("APP PACKAGING App 11111111 [device, required]") && l.Contains("recorded 2026-11-10 14:00"));
        Assert.False(view.CopyLog);
    }

    [Fact]
    public void A_problem_path_says_when_its_checks_are_clean_and_names_what_needs_attention()
    {
        var path = ProblemPaths.Get(Problem.CantSignIn);
        var clean = path.Rows.Select(n => new HealthResult { Name = n, Status = HealthStatus.Pass }).ToList();
        var (text, tone) = ProblemPaths.Verdict(path, clean);
        Assert.StartsWith("These checks found no problem", text);
        Assert.Equal(Tone.Success, tone);
        var failing = clean.Select(r => r.Name == HealthChecks.Clock ? new HealthResult { Name = r.Name, Status = HealthStatus.Fail, Hint = "The clock is off by 6 minutes." } : r).ToList();
        (text, tone) = ProblemPaths.Verdict(path, failing);
        Assert.StartsWith("Problem: " + HealthChecks.Clock, text);
        Assert.Equal(Tone.Error, tone);
        // Three tools at most; Sync clock and the connection check are the rows' fix links (and the search finds them).
        Assert.Equal(new[] { "RefreshSignInButton", "ResetSignInButton", "TeamsCacheButton" }, path.Tools);
        Assert.Equal(new[] { "RefreshSignInButton", "ResetSignInButton", "TeamsCacheButton", "SyncClockButton", "ConnectivityButton" }, path.SearchTools);
        Assert.Empty(ProblemPaths.Get(Problem.AppWontInstall).Tools);
    }
}
