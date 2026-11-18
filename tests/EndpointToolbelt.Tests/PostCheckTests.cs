using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The post-check. A remedy is tied to the diagnosis and app it came from; a later diagnosis
/// says "App detected" only on a new, traceable, positive measurement of the same app (same app ID
/// and scope, the same revision, an observation time later than the remedy); everything else stays
/// awaiting evidence, failed or unknown. The records survive a reopen, belong to one user and are
/// history after 7 days.
/// </summary>
public class PostCheckTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 14, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    private const string AppA = "11111111-2222-3333-4444-555555555555";
    private const string AppB = "22222222-3333-4444-5555-666666666666";
    private const string UserOne = "S-1-5-21-1000-1000-1000-1001";
    private const string UserTwo = "S-1-5-21-1000-1000-1000-1002";

    private static string Utc(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    private static AppEvidence App(string diagnosis, string verdict, DateTime? observed, string id = AppA, string scope = "device", int revision = 3, string code = "", string intent = "required", string name = "Contoso Agent") => new()
    {
        DiagnosisId = diagnosis,
        AppId = id,
        Name = name,
        Scope = scope,
        Intent = intent,
        Revision = revision,
        Verdict = verdict,
        State = "a state",
        Code = code,
        ObservedText = observed.HasValue ? Utc(observed.Value) : "",
    };

    /// <summary>A finished diagnosis with these apps, read at <paramref name="at"/>.</summary>
    private static AppDiagnosis Diagnosis(string id, DateTime at, params AppEvidence[] apps)
    {
        var records = apps.Select(a => new EngineRecord { Type = "app-diagnosis", App = a, Utc = at }).ToList();
        records.Add(new EngineRecord { Type = "apps-diagnosis", Closing = new DiagnosisClose { Present = apps.Length > 0, Records = apps.Length, Listed = apps.Length, UserScope = true }, Utc = at });
        records.Add(new EngineRecord { Type = "summary", Exit = 0, Utc = at });
        var outcome = new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = id, Result = ResultCode.Completed, ExitCode = 0 }.With(records);
        return AppDecisions.From(outcome, at, Amsterdam);
    }

    /// <summary>The first diagnosis: Contoso Agent failed at 13:00 (0x80070643), read at 14:00.</summary>
    private static AppDiagnosis Before() => Diagnosis("diag0001", T0, App("diag0001", "RETRY-HELPS", T0.AddHours(-1), code: "0x80070643"));

    /// <summary>Retry work apps and settings taken from the first diagnosis at 14:05.</summary>
    private static RemedyRecord Retry(AppDiagnosis before = null, string user = UserOne, DateTime? at = null, string run = "ab12cd34")
        => PostCheck.Remedy((before ?? Before()).Apps[0], RemedyKind.RetryAppsAndPolicies, at ?? T0.AddMinutes(5), user, run);

    private static (PostCheckState State, PostCheckReason Reason) Compare(RemedyRecord remedy, params AppEvidence[] apps)
    {
        var (state, reason, _) = PostCheck.Compare(remedy, Diagnosis("diag0002", T0.AddMinutes(30), apps));
        return (state, reason);
    }

    // --- The remedy record ------------------------------------------------------------------------

    [Fact]
    public void A_remedy_keeps_the_diagnosis_app_scope_revision_observation_remedy_time_and_run()
    {
        var remedy = Retry();
        Assert.Equal("diag0001", remedy.DiagnosisId);
        Assert.Equal(AppA, remedy.AppId);
        Assert.Equal("device", remedy.Scope);
        Assert.Equal(3, remedy.Revision);
        Assert.Equal(T0.AddHours(-1), remedy.ObservedUtc);
        Assert.Equal("0x80070643", remedy.Code);
        Assert.Equal(RemedyKind.RetryAppsAndPolicies, remedy.Kind);
        Assert.Equal("2026-11-10T14:05:00Z", remedy.AtText);
        Assert.Equal("ab12cd34", remedy.RunId);
        Assert.Equal(UserOne, remedy.User);
        Assert.Equal(PostCheckState.AwaitingEvidence, remedy.State);
        Assert.Equal(PostCheckReason.NotCheckedYet, remedy.Reason);
        // A finding without an app (the service offline, "not in this list") has nothing to tie a remedy to.
        Assert.Null(PostCheck.Remedy(AppDecisions.NotListed(Before()), RemedyKind.CopyForIt, T0, UserOne));
        Assert.Null(PostCheck.Remedy(AppDecisions.From(null, T0).Problem, RemedyKind.CopyForIt, T0, UserOne));
    }

    [Fact]
    public void The_same_step_from_the_same_finding_is_one_remedy_and_another_run_is_another()
    {
        var finding = Before().Apps[0];
        var first = PostCheck.Remedy(finding, RemedyKind.CopyForIt, T0, UserOne);
        Assert.True(PostCheck.SameRemedy(first, PostCheck.Remedy(finding, RemedyKind.CopyForIt, T0.AddMinutes(2), UserOne)));
        Assert.False(PostCheck.SameRemedy(first, PostCheck.Remedy(finding, RemedyKind.CompanyPortal, T0, UserOne)));
        Assert.False(PostCheck.SameRemedy(Retry(run: "ab12cd34"), Retry(run: "ab12cd35")));
    }

    // --- The comparison, case by case ---------------------------------------------------------------

    [Fact]
    public void The_same_observation_stays_awaiting_evidence()
    {
        // The IME has not touched the record since the diagnosis the remedy came from.
        Assert.Equal((PostCheckState.AwaitingEvidence, PostCheckReason.Unchanged), Compare(Retry(), App("diag0002", "RETRY-HELPS", T0.AddHours(-1), code: "0x80070643")));
        // A record that keeps its time but now reads as detected changed in content (the IME rewrote its
        // assignment or status report without a new time, as on a shared virtual desktop): detected.
        Assert.Equal((PostCheckState.Detected, PostCheckReason.NowDetected), Compare(Retry(), App("diag0002", "OK", T0.AddHours(-1))));
    }

    [Fact]
    public void An_older_observation_or_one_from_before_the_remedy_stays_awaiting_evidence()
    {
        Assert.Equal((PostCheckState.Detected, PostCheckReason.NowDetected), Compare(Retry(), App("diag0002", "OK", T0.AddHours(-2))));
        // Not detected and not newer: unchanged.
        Assert.Equal((PostCheckState.AwaitingEvidence, PostCheckReason.Unchanged), Compare(Retry(), App("diag0002", "AVAILABLE", T0.AddHours(-2))));
        // Newer than the record the finding was based on, but not later than the remedy itself.
        Assert.Equal((PostCheckState.Detected, PostCheckReason.NowDetected), Compare(Retry(), App("diag0002", "OK", T0.AddMinutes(4))));
        Assert.Equal((PostCheckState.Detected, PostCheckReason.NowDetected), Compare(Retry(), App("diag0002", "OK", T0.AddMinutes(5))));
    }

    [Fact]
    public void A_record_without_a_time_is_no_new_measurement()
    {
        Assert.Equal((PostCheckState.Detected, PostCheckReason.NowDetected), Compare(Retry(), App("diag0002", "OK", null)));
        Assert.Equal((PostCheckState.AwaitingEvidence, PostCheckReason.TimeUnknown), Compare(Retry(), App("diag0002", "RETRY-HELPS", null, code: "0x80070643")));
    }

    [Fact]
    public void Only_a_newer_measurement_of_the_same_app_that_says_detected_is_App_detected()
    {
        var (state, reason, match) = PostCheck.Compare(Retry(), Diagnosis("diag0002", T0.AddMinutes(30), App("diag0002", "OK", T0.AddMinutes(20))));
        Assert.Equal(PostCheckState.Detected, state);
        Assert.Equal(PostCheckReason.NewerDetected, reason);
        Assert.Equal("diag0002", match.DiagnosisId);
        Assert.Equal(T0.AddMinutes(20), match.App.ObservedUtc);
    }

    [Fact]
    public void A_newer_failure_is_failed_with_its_new_code()
    {
        var (state, reason, match) = PostCheck.Compare(Retry(), Diagnosis("diag0002", T0.AddMinutes(30), App("diag0002", "RETRY-HELPS", T0.AddMinutes(20), code: "0x80070005")));
        Assert.Equal((PostCheckState.Failed, PostCheckReason.NewerFailure), (state, reason));
        Assert.Equal("0x80070005", match.App.Code);
        // An install that is not detected (0x87D1041C) is a failure too, never a success.
        Assert.Equal(PostCheckState.Failed, Compare(Retry(), App("diag0002", "PACKAGING", T0.AddMinutes(20), code: "0x87D1041C")).State);
    }

    [Fact]
    public void A_newer_record_that_is_on_its_way_or_unclear_is_never_a_success()
    {
        Assert.Equal((PostCheckState.AwaitingEvidence, PostCheckReason.NewerInProgress), Compare(Retry(), App("diag0002", "IN-PROGRESS", T0.AddMinutes(20))));
        Assert.Equal((PostCheckState.AwaitingEvidence, PostCheckReason.NewerInProgress), Compare(Retry(), App("diag0002", "RESTART-FIRST", T0.AddMinutes(20))));
        // No error is not a success: a record that says nothing certain is unknown.
        Assert.Equal((PostCheckState.Unknown, PostCheckReason.NewerUnclear), Compare(Retry(), App("diag0002", "NO-STATE", T0.AddMinutes(20))));
        Assert.Equal((PostCheckState.Unknown, PostCheckReason.NewerUnclear), Compare(Retry(), App("diag0002", "UNKNOWN", T0.AddMinutes(20))));
        // A removal is not "detected".
        Assert.Equal((PostCheckState.Unknown, PostCheckReason.NewerUnclear), Compare(Retry(), App("diag0002", "OK", T0.AddMinutes(20), intent: "uninstall")));
    }

    [Fact]
    public void Another_scope_is_not_the_same_app()
    {
        Assert.Equal((PostCheckState.Unknown, PostCheckReason.OtherScope), Compare(Retry(), App("diag0002", "OK", T0.AddMinutes(20), scope: "user")));
    }

    [Fact]
    public void A_changed_revision_is_a_different_deployment()
    {
        Assert.Equal((PostCheckState.Unknown, PostCheckReason.RevisionChanged), Compare(Retry(), App("diag0002", "OK", T0.AddMinutes(20), revision: 4)));
        // A revision that became unknown cannot be matched either.
        Assert.Equal((PostCheckState.Unknown, PostCheckReason.RevisionChanged), Compare(Retry(), App("diag0002", "OK", T0.AddMinutes(20), revision: -1)));
    }

    [Fact]
    public void An_app_that_is_gone_from_the_list_is_unknown()
    {
        Assert.Equal((PostCheckState.Unknown, PostCheckReason.Missing), Compare(Retry(), App("diag0002", "OK", T0.AddMinutes(20), id: AppB)));
        Assert.Equal((PostCheckState.Unknown, PostCheckReason.Missing), Compare(Retry()));
    }

    [Fact]
    public void Two_candidates_for_the_same_app_and_scope_are_unknown()
    {
        Assert.Equal((PostCheckState.Unknown, PostCheckReason.TwoCandidates),
            Compare(Retry(), App("diag0002", "OK", T0.AddMinutes(20)), App("diag0002", "RETRY-HELPS", T0.AddMinutes(21))));
    }

    [Fact]
    public void Only_a_finished_later_diagnosis_counts()
    {
        var remedy = Retry();
        // The diagnosis the remedy came from says nothing about its result.
        Assert.Equal(PostCheckReason.NotCheckedYet, PostCheck.Compare(remedy, Before()).Reason);
        // A diagnosis that did not finish compares nothing.
        var interrupted = AppDecisions.From(new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "diag0003", Result = ResultCode.Interrupted }, T0);
        Assert.Equal((PostCheckState.AwaitingEvidence, PostCheckReason.NotCheckedYet, (AppFinding)null), PostCheck.Compare(remedy, interrupted));
        Assert.Empty(PostCheck.Check(new[] { remedy }, interrupted, T0.AddMinutes(30)));
        // Nor does one that read the apps before the remedy was taken.
        Assert.Empty(PostCheck.Check(new[] { remedy }, Diagnosis("diag0004", T0.AddMinutes(1), App("diag0004", "OK", T0.AddMinutes(20))), T0.AddMinutes(30)));
    }

    [Fact]
    public void A_check_keeps_its_result_and_evidence_in_the_record()
    {
        var remedy = Retry();
        var checkedNow = PostCheck.Check(new[] { remedy }, Diagnosis("diag0002", T0.AddMinutes(30), App("diag0002", "RETRY-HELPS", T0.AddMinutes(20), code: "0x80070005")), T0.AddMinutes(31));
        Assert.Same(remedy, Assert.Single(checkedNow));
        Assert.Equal(PostCheckState.Failed, remedy.State);
        Assert.Equal(PostCheckReason.NewerFailure, remedy.Reason);
        Assert.Equal("diag0002", remedy.CheckDiagnosisId);
        Assert.Equal(T0.AddMinutes(20), remedy.CheckObservedUtc);
        Assert.Equal("0x80070005", remedy.CheckCode);
        Assert.Equal("2026-11-10T14:30:00Z", remedy.CheckedAtText);
        // A later check replaces it: the newest measurement decides.
        PostCheck.Check(new[] { remedy }, Diagnosis("diag0003", T0.AddMinutes(50), App("diag0003", "OK", T0.AddMinutes(45))), T0.AddMinutes(51));
        Assert.Equal(PostCheckState.Detected, remedy.State);
        Assert.Equal("diag0003", remedy.CheckDiagnosisId);
        Assert.Equal("diag0001", remedy.DiagnosisId);
    }

    // --- What the window shows -------------------------------------------------------------------

    [Fact]
    public void After_a_remedy_the_app_awaits_evidence_and_offers_Check_again()
    {
        var before = Before();
        var shown = PostCheck.Overlay(before, new[] { Retry(before) }, T0.AddMinutes(6), Amsterdam);
        var app = Assert.Single(shown.Apps);
        Assert.Equal(AppObservation.AwaitingEvidence, app.Observation);
        Assert.Equal(PostCheckState.AwaitingEvidence, app.AfterRemedy);
        Assert.Equal(AppNextStep.CheckAgain, app.Next);
        Assert.Equal("Contoso Agent: awaiting evidence", app.Title);
        Assert.Contains("Check this app again", app.NextText);
        Assert.True(app.NeedsAttention);
        Assert.StartsWith("Post-check: Awaiting evidence · Retry work apps and settings at 2026-11-10 15:05 (UTC+01:00) · from diagnosis diag0001 · not checked again yet", PostCheck.Line(app, Amsterdam));
        // The diagnosis as read is not changed: the post-check is a view over it.
        Assert.Equal(AppObservation.Failed, before.Apps[0].Observation);
        Assert.Null(before.Apps[0].Remedy);
        // Home names the same finding and its step.
        var home = HomeSummary.Build(Array.Empty<HealthResult>(), shown, false);
        Assert.Equal("Contoso Agent: awaiting evidence", home.Title);
        Assert.Equal("Check this app again", home.StepText);
        Assert.Equal(Tone.Neutral, home.Tone);
    }

    [Fact]
    public void The_same_failure_after_a_retry_hands_over_to_IT_instead_of_another_retry()
    {
        // The first diagnosis failed with 0x80070643; after Retry a newer attempt fails with the same code.
        var after = Diagnosis("diag0002", T0.AddMinutes(30), App("diag0002", "RETRY-HELPS", T0.AddMinutes(20), code: "0x80070643"));
        var app = Assert.Single(PostCheck.Overlay(after, new[] { Retry() }, T0.AddMinutes(31), Amsterdam).Apps);
        Assert.Equal(PostCheckState.Failed, app.AfterRemedy);
        Assert.Equal("Contoso Agent: failed again with the same code", app.Title);
        Assert.Equal(AppNextStep.CopyForIt, app.Next);
        Assert.StartsWith("It failed again with the same code after Retry work apps and settings", app.NextText);
        Assert.Contains("The same code (0x80070643) before and after a retry", app.Support);
        // Another code after the retry keeps the finding's own step.
        var other = Diagnosis("diag0003", T0.AddMinutes(30), App("diag0003", "RETRY-HELPS", T0.AddMinutes(20), code: "0x80070005"));
        Assert.Equal(AppNextStep.RetryAppsAndPolicies, Assert.Single(PostCheck.Overlay(other, new[] { Retry() }, T0.AddMinutes(31), Amsterdam).Apps).Next);
    }

    [Fact]
    public void A_failed_removal_is_retried_and_followed_by_the_post_check()
    {
        var before = Diagnosis("diag0001", T0, App("diag0001", "RETRY-HELPS", T0.AddHours(-1), code: "0x80070001", intent: "uninstall"));
        var removal = Assert.Single(before.Apps);
        Assert.Equal(AppCase.RemovalFailed, removal.Case);
        Assert.Same(removal, Assert.Single(PostCheck.Retried(before)));
    }

    [Fact]
    public void One_app_for_the_device_and_for_the_user_with_the_same_case_is_one_row()
    {
        // The same failed removal in both scopes: one row, from the newer record, for both.
        var both = Diagnosis("diag0001", T0,
            App("diag0001", "RETRY-HELPS", T0.AddDays(-6), code: "0x80070001", intent: "uninstall", scope: "device"),
            App("diag0001", "RETRY-HELPS", T0.AddHours(-1), code: "0x80070001", intent: "uninstall", scope: "user"));
        var app = Assert.Single(both.Apps);
        Assert.True(app.ForBothScopes);
        Assert.Equal("user", app.App.Scope);
        Assert.Single(both.Attention);
        Assert.StartsWith("for this device and assigned to you", new AppRowView(app, false).Summary);
        // A remedy taken in one scope is followed on the merged row.
        var remedy = PostCheck.Remedy(app, RemedyKind.RetryAppsAndPolicies, T0.AddMinutes(5), UserOne, "ab12cd34");
        var later = Diagnosis("diag0002", T0.AddMinutes(30),
            App("diag0002", "OK", T0.AddMinutes(20), intent: "uninstall", scope: "device"),
            App("diag0002", "OK", T0.AddMinutes(25), intent: "uninstall", scope: "user"));
        Assert.NotEqual(PostCheckReason.OtherScope, PostCheck.Compare(remedy, later).Reason);
        // A different case in each scope stays two rows, each with its own step.
        var apart = Diagnosis("diag0003", T0,
            App("diag0003", "RETRY-HELPS", T0.AddHours(-1), code: "0x80070643", scope: "device"),
            App("diag0003", "OK", T0.AddHours(-1), scope: "user"));
        Assert.Equal(2, apart.Apps.Count);
        Assert.All(apart.Apps, a => Assert.False(a.ForBothScopes));
    }

    [Fact]
    public void An_app_only_offered_and_never_tried_is_not_in_the_short_list()
    {
        var d = Diagnosis("diag0001", T0,
            App("diag0001", "AVAILABLE", T0.AddHours(-1), intent: "available", scope: "user"),
            App("diag0001", "RETRY-HELPS", T0.AddHours(-1), id: AppB, code: "0x80070643"));
        var offered = d.Apps.Single(a => a.Case == AppCase.Offered);
        Assert.False(AppDecisions.InShortList(offered));
        Assert.True(AppDecisions.InShortList(d.Apps.Single(a => a.App.AppId == AppB)));
        // Once the user took a step for it (opened Company Portal), it is in the short list.
        offered.Remedy = PostCheck.Remedy(offered, RemedyKind.CompanyPortal, T0.AddMinutes(5), UserOne);
        Assert.True(AppDecisions.InShortList(offered));
    }

    [Fact]
    public void App_detected_shows_its_evidence_and_needs_nothing_more()
    {
        var remedy = Retry();
        var after = Diagnosis("diag0002", T0.AddMinutes(30), App("diag0002", "OK", T0.AddMinutes(20)));
        var app = Assert.Single(PostCheck.Overlay(after, new[] { remedy }, T0.AddMinutes(31), Amsterdam).Apps);
        Assert.Equal(PostCheckState.Detected, app.AfterRemedy);
        Assert.Equal("App detected: Contoso Agent", app.Title);
        Assert.Contains("diagnosis diag0002", app.Text);
        Assert.Contains("2026-11-10 15:20 (UTC+01:00)", app.Text);
        Assert.Equal(AppNextStep.None, app.Next);
        Assert.False(app.NeedsAttention);
        Assert.Equal("Post-check: App detected · Retry work apps and settings at 2026-11-10 15:05 (UTC+01:00) · from diagnosis diag0001 · checked in diagnosis diag0002, recorded 2026-11-10 15:20 (UTC+01:00)",
            PostCheck.Line(app, Amsterdam));
    }

    [Fact]
    public void An_app_detected_now_in_a_record_that_kept_its_time_says_so()
    {
        // As on a shared virtual desktop: the IME rewrote the assignment and its status report, not the time.
        var after = Diagnosis("diag0002", T0.AddMinutes(30), App("diag0002", "OK", T0.AddHours(-1)));
        var app = Assert.Single(PostCheck.Overlay(after, new[] { Retry() }, T0.AddMinutes(31), Amsterdam).Apps);
        Assert.Equal(PostCheckState.Detected, app.AfterRemedy);
        Assert.Equal("App detected: Contoso Agent", app.Title);
        Assert.Contains("now reports it installed, though its record still carries the time 2026-11-10 14:00 (UTC+01:00)", app.Text);
        Assert.False(app.NeedsAttention);
    }

    [Fact]
    public void A_newer_failure_keeps_its_own_next_step_and_says_the_new_code()
    {
        var after = Diagnosis("diag0002", T0.AddMinutes(30), App("diag0002", "RETRY-HELPS", T0.AddMinutes(20), code: "0x80070005"));
        var app = Assert.Single(PostCheck.Overlay(after, new[] { Retry() }, T0.AddMinutes(31), Amsterdam).Apps);
        Assert.Equal(AppObservation.Failed, app.Observation);
        Assert.Equal(AppNextStep.RetryAppsAndPolicies, app.Next);
        // Said plainly; the code, the times and the diagnosis are in the technical details.
        Assert.StartsWith("It failed again ", app.Text);
        Assert.Contains(", after Retry work apps and settings ", app.Text);
        Assert.Contains("After Retry work apps and settings (2026-11-10 15:05 (UTC+01:00)), a newer attempt failed with 0x80070005", app.Evidence);
    }

    [Fact]
    public void A_Full_IME_reset_ends_the_steps_before_it_and_the_page_says_the_list_is_read_again()
    {
        var before = Retry(at: T0.AddMinutes(5));
        var afterReset = Retry(at: T0.AddMinutes(40));
        var reset = T0.AddMinutes(30);
        Assert.Equal(new[] { before }, PostCheck.BeforeReset(new[] { before, afterReset }, reset));
        Assert.Empty(PostCheck.BeforeReset(new[] { before, afterReset }, null));
        // Until the IME checked in after the reset, the page says the list is being read again.
        var line = AppDecisions.ResetLine(reset, T0, Amsterdam, T0.AddMinutes(35));
        Assert.StartsWith("The app installer was reset at ", line);
        Assert.EndsWith(". It reads your apps again at its next check-ins, usually within an hour. Check again later.", line);
        Assert.Equal("", AppDecisions.ResetLine(reset, T0.AddMinutes(45), Amsterdam, T0.AddMinutes(50)));
        Assert.Equal("", AppDecisions.ResetLine(null, null, Amsterdam, T0));
    }

    [Fact]
    public void A_newer_failure_after_the_handover_to_IT_offers_no_retry_but_Copy_details_for_IT_again()
    {
        var handover = PostCheck.Remedy(Before().Apps[0], RemedyKind.CopyForIt, T0.AddMinutes(5), UserOne, "ab12cd34");
        var after = Diagnosis("diag0002", T0.AddMinutes(30), App("diag0002", "RETRY-HELPS", T0.AddMinutes(20), code: "0x80070001"));
        var app = Assert.Single(PostCheck.Overlay(after, new[] { handover }, T0.AddMinutes(31), Amsterdam).Apps);
        Assert.Equal(AppNextStep.CopyForIt, app.Next);
        Assert.Equal("Contoso Agent: still failing, IT support has it", app.Title);
        Assert.Contains(", after it was sent to IT support ", app.Text);
        Assert.DoesNotContain("Retry", app.NextText.Replace("Retrying", ""));
        Assert.Equal("", app.Caveat);
    }

    [Fact]
    public void An_app_the_new_diagnosis_does_not_have_is_shown_as_unknown_with_the_handover()
    {
        var after = Diagnosis("diag0002", T0.AddMinutes(30));
        var app = Assert.Single(PostCheck.Overlay(after, new[] { Retry() }, T0.AddMinutes(31), Amsterdam).Apps);
        Assert.Equal(AppCase.Unmatched, app.Case);
        Assert.Equal(AppObservation.Unknown, app.Observation);
        Assert.Equal(AppNextStep.CopyForIt, app.Next);
        Assert.Equal("Contoso Agent: the result is not known", app.Title);
        Assert.Contains("no longer in the list", app.Text);
        Assert.Contains("A missing error is not a success", app.Uncertainty);
        Assert.True(app.NeedsAttention);
        Assert.Equal(AppA, app.App.AppId);
        // A diagnosis that did not finish adds nothing: it knows nothing about the app.
        var interrupted = AppDecisions.From(new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "diag0003", Result = ResultCode.Interrupted }, T0);
        Assert.Empty(PostCheck.Overlay(interrupted, new[] { Retry() }, T0.AddMinutes(31)).Apps);
    }

    [Fact]
    public void A_changed_revision_and_two_candidates_show_unknown_with_the_handover()
    {
        var revised = Assert.Single(PostCheck.Overlay(Diagnosis("diag0002", T0.AddMinutes(30), App("diag0002", "OK", T0.AddMinutes(20), revision: 4)), new[] { Retry() }, T0.AddMinutes(31)).Apps);
        Assert.Equal(PostCheckState.Unknown, revised.AfterRemedy);
        Assert.Contains("new revision", revised.Text);
        Assert.Equal(AppNextStep.CopyForIt, revised.Next);
        var twins = PostCheck.Overlay(Diagnosis("diag0002", T0.AddMinutes(30), App("diag0002", "OK", T0.AddMinutes(20)), App("diag0002", "OK", T0.AddMinutes(21))), new[] { Retry() }, T0.AddMinutes(31)).Apps;
        Assert.Equal(2, twins.Count);
        Assert.All(twins, t => Assert.Equal(PostCheckState.Unknown, t.AfterRemedy));
        Assert.DoesNotContain(twins, t => t.Observation == AppObservation.Detected);
    }

    [Fact]
    public void The_list_row_says_App_detected_in_words()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var after = Diagnosis("diag0002", T0.AddMinutes(30), App("diag0002", "OK", T0.AddMinutes(20)));
            var app = PostCheck.Overlay(after, new[] { Retry() }, T0.AddMinutes(31)).Apps[0];
            var row = new AppRowView(app, selected: false);
            Assert.Equal("App detected", row.ObservationWord);
            Assert.StartsWith("Contoso Agent: App detected.", row.AccessibleName);
            var waiting = new AppRowView(PostCheck.Overlay(Before(), new[] { Retry() }, T0.AddMinutes(6)).Apps[0], selected: false);
            Assert.Equal("Awaiting evidence", waiting.ObservationWord);
        });
    }

    // --- Copy details for IT ----------------------------------------------------------------------------

    [Fact]
    public void Copy_for_IT_for_a_post_checked_app_carries_the_original_diagnosis_the_remedy_and_the_result()
    {
        var after = Diagnosis("diag0002", T0.AddMinutes(30), App("diag0002", "RETRY-HELPS", T0.AddMinutes(20), code: "0x80070005"));
        var app = PostCheck.Overlay(after, new[] { Retry() }, T0.AddMinutes(31), Amsterdam).Apps[0];
        var line = PostCheck.TicketLine(app, Amsterdam);
        Assert.Equal("Post-check · Original diagnosis diag0001 · Remedy Retry work apps and settings (run ab12cd34) at 2026-11-10 15:05 (UTC+01:00) · Result Failed 0x80070005 in diagnosis diag0002, recorded 2026-11-10 15:20 (UTC+01:00)", line);
        var header = new RunLogHeader { Outcome = new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "diag0002", Result = ResultCode.Completed, ExitCode = 0 }, Zone = Amsterdam, NowUtc = T0.AddMinutes(32), DeviceName = "pc-contoso-01", User = "CONTOSO\\user" };
        var ticket = RunTicket.Text(header, new[] { app.Card() }, "App diagnosis finished: 1 app needs attention. Nothing was changed.", Tone.Warning, line);
        var lines = ticket.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(6, lines.Length);
        Assert.StartsWith("Endpoint Toolbelt · App diagnosis · Reference diag0002", lines[0]);
        Assert.Equal(line, lines[5]);
        // Without a remedy the ticket keeps its five lines.
        Assert.Null(PostCheck.TicketLine(Before().Apps[0]));
        Assert.Equal(5, RunTicket.Text(header, new[] { Before().Apps[0].Card() }, "x", Tone.Warning).Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).Length);
        // Not checked yet, and an unknown result with its reason.
        Assert.EndsWith("Result Awaiting evidence, not checked again yet", PostCheck.TicketLine(PostCheck.Overlay(Before(), new[] { Retry() }, T0.AddMinutes(6), Amsterdam).Apps[0], Amsterdam));
        var gone = PostCheck.Overlay(Diagnosis("diag0002", T0.AddMinutes(30)), new[] { Retry() }, T0.AddMinutes(31), Amsterdam).Apps[0];
        Assert.EndsWith("Result Unknown in diagnosis diag0002, record time unknown (app not in the list)", PostCheck.TicketLine(gone, Amsterdam));
    }

    // --- Retry work apps and settings: the closing status ------------------------------------------------

    private static AppDiagnosis TwoFailing() => Diagnosis("diag0001", T0,
        App("diag0001", "RETRY-HELPS", T0.AddHours(-1), code: "0x80070643"),
        App("diag0001", "RETRY-HELPS", T0.AddHours(-1), id: AppB, name: "Contoso Viewer", download: true),
        App("diag0001", "AVAILABLE", T0.AddHours(-1), id: "33333333-4444-5555-6666-777777777777", intent: "available", name: "Contoso Notes"),
        App("diag0001", "OK", T0.AddHours(-1), id: "44444444-5555-6666-7777-888888888888", name: "Contoso Reader"));

    private static AppEvidence App(string diagnosis, string verdict, DateTime? observed, string id, string name, bool download)
    {
        var app = App(diagnosis, verdict, observed, id: id, name: name);
        app.Download = download;
        return app;
    }

    private static List<RemedyRecord> RetryAll(AppDiagnosis before, string run = "ab12cd34")
        => PostCheck.Retried(before).Select(f => PostCheck.Remedy(f, RemedyKind.RetryAppsAndPolicies, T0.AddMinutes(5), UserOne, run)).ToList();

    [Fact]
    public void Retry_retries_the_failed_apps_only()
    {
        // Not the app offered in Company Portal (the user's step), not the installed one.
        Assert.Equal(new[] { AppA, AppB }, PostCheck.Retried(TwoFailing()).Select(f => f.App.AppId));
        Assert.Empty(PostCheck.Retried(null));
        Assert.Empty(PostCheck.Retried(AppDecisions.From(null, T0)));
    }

    [Fact]
    public void Retry_says_Done_only_once_a_post_check_detected_every_retried_app_and_the_checks_are_clean()
    {
        var records = RetryAll(TwoFailing());
        Assert.Equal(AppsAfterRetry.Unknown, PostCheck.AfterRetry(PostCheck.RetryGroup(records, T0.AddMinutes(6))));

        // The automatic post-check right after the run: nothing new yet.
        PostCheck.Check(records, Diagnosis("diag0002", T0.AddMinutes(8),
            App("diag0002", "RETRY-HELPS", T0.AddHours(-1), code: "0x80070643"), App("diag0002", "RETRY-HELPS", T0.AddHours(-1), id: AppB, name: "Contoso Viewer", download: true)), T0.AddMinutes(8));
        var group = PostCheck.RetryGroup(records, T0.AddMinutes(8));
        Assert.Equal(2, group.Count);
        Assert.Equal(AppsAfterRetry.Unknown, PostCheck.AfterRetry(group));
        Assert.StartsWith("Retry requested.", ResultPresenter.RetryDone(0, 0, PostCheck.AfterRetry(group)).Text);

        // One app detected, the other still unchanged: still "Retry requested".
        PostCheck.Check(records, Diagnosis("diag0003", T0.AddMinutes(30),
            App("diag0003", "OK", T0.AddMinutes(20)), App("diag0003", "RETRY-HELPS", T0.AddHours(-1), id: AppB, name: "Contoso Viewer", download: true)), T0.AddMinutes(30));
        Assert.Equal(AppsAfterRetry.Unknown, PostCheck.AfterRetry(group));
        Assert.NotEqual(Tone.Success, ResultPresenter.RetryDone(0, 0, PostCheck.AfterRetry(group)).Tone);

        // Both detected: Done, with clean health checks only.
        PostCheck.Check(records, Diagnosis("diag0004", T0.AddMinutes(50),
            App("diag0004", "OK", T0.AddMinutes(20)), App("diag0004", "OK", T0.AddMinutes(40), id: AppB, name: "Contoso Viewer", download: false)), T0.AddMinutes(50));
        Assert.Equal(AppsAfterRetry.AllDetected, PostCheck.AfterRetry(group));
        Assert.Equal(("Done. Every app that was retried is now detected, and the health checks are clean.", Tone.Success), ResultPresenter.RetryDone(0, 0, PostCheck.AfterRetry(group)));
        Assert.NotEqual(Tone.Success, ResultPresenter.RetryDone(1, 0, PostCheck.AfterRetry(group)).Tone);
    }

    [Fact]
    public void Retry_with_an_app_that_fails_again_or_cannot_be_traced_is_not_done()
    {
        var records = RetryAll(TwoFailing());
        PostCheck.Check(records, Diagnosis("diag0002", T0.AddMinutes(30),
            App("diag0002", "OK", T0.AddMinutes(20)), App("diag0002", "RETRY-HELPS", T0.AddMinutes(25), id: AppB, name: "Contoso Viewer", download: true)), T0.AddMinutes(30));
        Assert.Equal(AppsAfterRetry.SomeFailing, PostCheck.AfterRetry(PostCheck.RetryGroup(records, T0.AddMinutes(30))));

        records = RetryAll(TwoFailing());
        // The second app is gone from the list: not traceable, so not done.
        PostCheck.Check(records, Diagnosis("diag0002", T0.AddMinutes(30), App("diag0002", "OK", T0.AddMinutes(20))), T0.AddMinutes(30));
        Assert.Equal(AppsAfterRetry.Unknown, PostCheck.AfterRetry(PostCheck.RetryGroup(records, T0.AddMinutes(30))));
        // Without a retried app nothing is known.
        Assert.Equal(AppsAfterRetry.Unknown, PostCheck.AfterRetry(new List<RemedyRecord>()));
    }

    [Fact]
    public void The_retry_group_is_the_newest_retry_run()
    {
        var older = RetryAll(TwoFailing(), "ab12cd34");
        var newer = PostCheck.Retried(TwoFailing()).Take(1).Select(f => PostCheck.Remedy(f, RemedyKind.RetryAppsAndPolicies, T0.AddMinutes(20), UserOne, "ab12cd35")).ToList();
        var all = older.Concat(newer).ToList();
        Assert.Equal(new[] { "ab12cd35" }, PostCheck.RetryGroup(all, T0.AddMinutes(21)).Select(r => r.RunId));
        Assert.Equal(2, PostCheck.RetryGroup(all, T0.AddMinutes(21), "ab12cd34").Count);
    }

    // --- Statuses ---------------------------------------------------------------------------------

    [Fact]
    public void The_post_check_status_names_each_result_and_says_nothing_was_changed()
    {
        var detected = Retry();
        detected.State = PostCheckState.Detected;
        Assert.Equal(("Post-check: App detected: Contoso Agent. Nothing was changed.", Tone.Success), PostCheck.Status(new[] { detected }));
        var waiting = Retry();
        Assert.Equal(Tone.Neutral, PostCheck.Status(new[] { waiting }).Tone);
        Assert.Contains("Awaiting evidence: Contoso Agent; check again later.", PostCheck.Status(new[] { waiting }).Text);
        var failed = Retry();
        failed.State = PostCheckState.Failed;
        Assert.Equal(Tone.Warning, PostCheck.Status(new[] { detected, failed }).Tone);
        var unknown = Retry();
        unknown.State = PostCheckState.Unknown;
        Assert.Contains("Result not known: Contoso Agent; copy details for IT.", PostCheck.Status(new[] { unknown }).Text);
    }

    [Fact]
    public void A_post_check_the_service_did_not_run_says_why_in_words()
    {
        Assert.Contains("another action is running", PostCheck.NotChecked(ResultCode.Busy));
        Assert.Contains("at most 30 times an hour", PostCheck.NotChecked(ResultCode.RateLimited));
        Assert.All(new[] { ResultCode.Busy, ResultCode.RateLimited, ResultCode.Interrupted }, r => Assert.EndsWith("Nothing was changed.", PostCheck.NotChecked(r)));
    }

    // --- Stale data, a reopen, a second user ---------------------------------------------------------

    [Fact]
    public void A_remedy_older_than_7_days_is_history()
    {
        var remedy = Retry();
        Assert.True(PostCheck.IsActive(remedy, T0.AddDays(7)));
        Assert.False(PostCheck.IsActive(remedy, T0.AddDays(7).AddMinutes(6)));
        var late = T0.AddDays(8);
        // Not compared, not shown, not part of a Retry status.
        Assert.Empty(PostCheck.Check(new[] { remedy }, Diagnosis("diag0002", late, App("diag0002", "OK", late.AddMinutes(-5))), late));
        var app = Assert.Single(PostCheck.Overlay(Diagnosis("diag0002", late, App("diag0002", "RETRY-HELPS", late.AddMinutes(-5))), new[] { remedy }, late).Apps);
        Assert.Null(app.Remedy);
        Assert.Null(app.AfterRemedy);
        Assert.Empty(PostCheck.Overlay(Diagnosis("diag0002", late), new[] { remedy }, late).Apps);
        Assert.Empty(PostCheck.RetryGroup(new[] { remedy }, late));
        // A remedy dated in the future (the clock went back) is not trusted either.
        Assert.False(PostCheck.IsActive(Retry(at: T0.AddHours(1)), T0));
    }

    [Fact]
    public void The_records_survive_a_reopen()
    {
        using var folder = new TestFolder();
        var path = folder.Combine(RemedyStore.FileName);
        var remedy = Retry();
        PostCheck.Check(new[] { remedy }, Diagnosis("diag0002", T0.AddMinutes(30), App("diag0002", "OK", T0.AddMinutes(20))), T0.AddMinutes(30));
        Assert.True(RemedyStore.Save(new[] { remedy }, path, UserOne, T0.AddMinutes(30)));
        // Written through a temporary file that is swapped in: none is left behind.
        Assert.Equal(new[] { RemedyStore.FileName }, Directory.GetFiles(folder.Path).Select(Path.GetFileName));

        var loaded = Assert.Single(RemedyStore.Load(path, UserOne, T0.AddHours(1)));
        Assert.Equal("diag0001", loaded.DiagnosisId);
        Assert.Equal(AppA, loaded.AppId);
        Assert.Equal("device", loaded.Scope);
        Assert.Equal(3, loaded.Revision);
        Assert.Equal(RemedyKind.RetryAppsAndPolicies, loaded.Kind);
        Assert.Equal("ab12cd34", loaded.RunId);
        Assert.Equal(PostCheckState.Detected, loaded.State);
        Assert.Equal("diag0002", loaded.CheckDiagnosisId);
        // After a reopen the next diagnosis compares with the loaded record as before.
        var app = Assert.Single(PostCheck.Overlay(Diagnosis("diag0003", T0.AddHours(1), App("diag0003", "OK", T0.AddMinutes(20))), new[] { loaded }, T0.AddHours(1)).Apps);
        Assert.Equal(PostCheckState.Detected, app.AfterRemedy);

        // A second save replaces the first.
        Assert.True(RemedyStore.Save(new[] { loaded, Retry(run: "ab12cd35") }, path, UserOne, T0.AddHours(1)));
        Assert.Equal(2, RemedyStore.Load(path, UserOne, T0.AddHours(1)).Count);
    }

    [Fact]
    public void A_record_of_one_user_is_never_shown_to_another()
    {
        using var folder = new TestFolder();
        var path = folder.Combine(RemedyStore.FileName);
        Assert.True(RemedyStore.Save(new[] { Retry(user: UserOne) }, path, UserOne, T0));
        Assert.Empty(RemedyStore.Load(path, UserTwo, T0));
        // Another user's record handed to a save is dropped, not written.
        Assert.True(RemedyStore.Save(new[] { Retry(user: UserOne), Retry(user: UserTwo, run: "ab12cd35") }, path, UserTwo, T0));
        Assert.Equal(new[] { UserTwo }, RemedyStore.Load(path, UserTwo, T0).Select(r => r.User));
        Assert.Empty(RemedyStore.Load(path, UserOne, T0));
        // Without a known user nothing is kept.
        Assert.Empty(RemedyStore.Load(path, "", T0));
    }

    [Fact]
    public void The_records_are_bounded_and_a_bad_file_is_read_as_empty()
    {
        var many = Enumerable.Range(0, RemedyStore.KeepRecords + 20).Select(i => Retry(at: T0.AddMinutes(i), run: "run" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))).ToList();
        var bounded = RemedyStore.Bound(many, UserOne, T0.AddDays(1));
        Assert.Equal(RemedyStore.KeepRecords, bounded.Count);
        Assert.Equal("run0020", bounded[0].RunId);
        Assert.Equal("run0119", bounded[bounded.Count - 1].RunId);
        // Older than 30 days, without an app, or with an unknown remedy: dropped.
        var bad = Retry();
        bad.RemedyText = "Reboot";
        var noApp = Retry();
        noApp.AppId = "";
        Assert.Empty(RemedyStore.Bound(new[] { Retry(), bad, noApp }, UserOne, T0.AddDays(31)));
        Assert.Single(RemedyStore.Bound(new[] { Retry(), bad, noApp }, UserOne, T0.AddDays(1)));

        using var folder = new TestFolder();
        var path = folder.Combine(RemedyStore.FileName);
        File.WriteAllText(path, "{ not json");
        Assert.Empty(RemedyStore.Load(path, UserOne, T0));
        File.WriteAllText(path, new string(' ', (int)RemedyStore.MaxBytes + 1));
        Assert.Empty(RemedyStore.Load(path, UserOne, T0));
        Assert.Empty(RemedyStore.Load(folder.Combine("missing.json"), UserOne, T0));
    }
}
