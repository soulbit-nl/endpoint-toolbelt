using System;
using System.Linq;
using EndpointToolbelt.App.Core;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// A Retry work apps and settings never retries an app offered in Company Portal: the IME does not start
/// its next attempt, only the user does there. So no step is recorded for it, and a step an earlier version
/// recorded ends at the next diagnosis, instead of "awaiting evidence" for 7 days.
/// </summary>
public class PostCheckOfferedTests
{
    private static readonly DateTime T0 = new(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    private const string Offered = "11111111-2222-3333-4444-555555555555";
    private const string Required = "22222222-3333-4444-5555-666666666666";

    private static AppEvidence App(string id, string name, string verdict, string intent, string code, string scope = "user", int hoursAgo = 2) => new()
    {
        DiagnosisId = "diag0001",
        AppId = id,
        Name = name,
        Scope = scope,
        Intent = intent,
        Revision = 1,
        Verdict = verdict,
        State = "a state",
        Code = code,
        ObservedText = T0.AddHours(-hoursAgo).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
    };

    private static AppDiagnosis Diagnosis(params AppEvidence[] apps)
    {
        var records = apps.Select(a => new EngineRecord { Type = "app-diagnosis", App = a, Utc = T0 }).ToList();
        records.Add(new EngineRecord { Type = "apps-diagnosis", Closing = new DiagnosisClose { Present = true, Records = apps.Length, Listed = apps.Length, UserScope = true }, Utc = T0 });
        records.Add(new EngineRecord { Type = "summary", Exit = 0, Utc = T0 });
        var outcome = new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "diag0001", Result = ResultCode.Completed, ExitCode = 0 }.With(records);
        return AppDecisions.From(outcome, T0, Amsterdam);
    }

    private static AppDiagnosis Device() => Diagnosis(
        App(Offered, "Contoso Viewer", "PACKAGING", "available", "0x87D1041C"),
        App(Offered.Replace('1', '3'), "Contoso Notes", "RETRY-HELPS", "available", "0x87D300C9"),
        App(Required, "Contoso Agent", "RETRY-HELPS", "required", "0x80070643"));

    [Fact]
    public void A_retry_records_no_step_for_an_app_offered_in_Company_Portal()
    {
        // Contoso Viewer (installed, not detected, available) is the change; a failed available app was never retried.
        var retried = PostCheck.Retried(Device());
        Assert.Equal(new[] { "Contoso Agent" }, retried.Select(a => a.App.Name));
        var viewer = Device().Apps.Single(a => a.App.Name == "Contoso Viewer");
        Assert.Equal(AppCase.InstalledNotDetected, viewer.Case);
        Assert.True(PostCheck.OfferedOnly(viewer));
    }

    [Fact]
    public void An_app_required_for_the_device_and_offered_to_the_user_is_still_retried_and_its_step_kept()
    {
        // The same case in both scopes merges into one row; the newer, available record is the one kept.
        var device = Diagnosis(
            App(Offered, "Contoso Viewer", "PACKAGING", "required", "0x87D1041C", scope: "device", hoursAgo: 3),
            App(Offered, "Contoso Viewer", "PACKAGING", "available", "0x87D1041C", scope: "user", hoursAgo: 2));
        var merged = Assert.Single(device.Apps);
        Assert.True(merged.ForBothScopes);
        Assert.Equal("available", merged.App.Intent);
        Assert.False(PostCheck.OfferedOnly(merged));
        Assert.Single(PostCheck.Retried(device));
        var step = PostCheck.Remedy(merged, RemedyKind.RetryAppsAndPolicies, T0.AddMinutes(5), "S-1-5-21-1", "ab12cd34");
        Assert.Empty(PostCheck.NotRetriedByIme(new[] { step }, device));
    }

    [Fact]
    public void A_step_for_the_same_app_in_another_scope_that_is_not_offered_is_kept()
    {
        // Two cases, two rows: the device row is required, the user row only offered.
        var device = Diagnosis(
            App(Offered, "Contoso Viewer", "RETRY-HELPS", "required", "0x80070643", scope: "device"),
            App(Offered, "Contoso Viewer", "PACKAGING", "available", "0x87D1041C", scope: "user"));
        Assert.Equal(2, device.Apps.Count);
        var deviceRow = device.Apps.Single(a => a.App.Scope == "device");
        var userRow = device.Apps.Single(a => a.App.Scope == "user");
        var steps = new[]
        {
            PostCheck.Remedy(deviceRow, RemedyKind.RetryAppsAndPolicies, T0.AddMinutes(5), "S-1-5-21-1", "ab12cd34"),
            PostCheck.Remedy(userRow, RemedyKind.RetryAppsAndPolicies, T0.AddMinutes(5), "S-1-5-21-1", "ab12cd34"),
        };
        Assert.Equal(new[] { "user" }, PostCheck.NotRetriedByIme(steps, device).Select(r => r.Scope));
    }

    [Fact]
    public void A_retry_with_nothing_to_retry_says_so_instead_of_the_IME_retrying_them()
    {
        // Nothing needed a retry: no detour to Company Portal.
        var (clean, cleanTone) = ResultPresenter.RetryDone(0, 0, AppsAfterRetry.NoneRetried);
        Assert.Equal("Retry requested: Intune was asked to check this device again. No app needed a retry.", clean);
        Assert.Equal(Tone.Neutral, cleanTone);
        // Apps that need attention but are not retried (offered in Company Portal, or for IT): a warning that says so.
        var (offered, offeredTone) = ResultPresenter.RetryDone(0, 0, AppsAfterRetry.NoneRetried, otherApps: 6);
        Assert.Equal("Retry requested: Intune was asked to check this device again. 6 apps need attention, and none is one the app installer retries by itself: install an app offered in Company Portal there, and see the list for the next step of the others.", offered);
        Assert.Equal(Tone.Warning, offeredTone);
        Assert.DoesNotContain("retries them in the background", offered, StringComparison.Ordinal);
        var (problem, problemTone) = ResultPresenter.RetryDone(1, 0, AppsAfterRetry.NoneRetried, otherApps: 1);
        Assert.StartsWith("Retry requested: Intune was asked to check this device again, but 1 problem remains in the health checks. 1 app needs attention", problem, StringComparison.Ordinal);
        Assert.Equal(Tone.Error, problemTone);
        // Other signed-in users: only the sync was requested, as before.
        Assert.Equal(ResultPresenter.RetryDone(0, 0, AppsAfterRetry.Unknown, retrySkipped: true), ResultPresenter.RetryDone(0, 0, AppsAfterRetry.NoneRetried, retrySkipped: true));
    }

    [Fact]
    public void A_retry_step_an_earlier_version_recorded_for_an_offered_app_ends_at_the_next_diagnosis()
    {
        var device = Device();
        var steps = device.Apps.Select(a => PostCheck.Remedy(a, RemedyKind.RetryAppsAndPolicies, T0.AddMinutes(5), "S-1-5-21-1", "ab12cd34")).ToList();
        var other = PostCheck.Remedy(device.Apps.Single(a => a.App.Name == "Contoso Viewer"), RemedyKind.CompanyPortal, T0.AddMinutes(6), "S-1-5-21-1", "ab12cd35");
        var ended = PostCheck.NotRetriedByIme(steps.Concat(new[] { other }), device);
        Assert.Equal(new[] { "Contoso Viewer", "Contoso Notes" }, ended.Select(r => r.Name));
        // Without them the offered apps show their own finding again, not "awaiting evidence".
        var view = PostCheck.Overlay(device, steps.Except(ended), T0.AddMinutes(30), Amsterdam);
        Assert.Null(view.Apps.Single(a => a.App.Name == "Contoso Viewer").Remedy);
        Assert.Null(view.Apps.Single(a => a.App.Name == "Contoso Notes").Remedy);
        Assert.NotNull(view.Apps.Single(a => a.App.Name == "Contoso Agent").Remedy);
        // An unfinished diagnosis ends nothing.
        Assert.Empty(PostCheck.NotRetriedByIme(steps, new AppDiagnosis { Run = DiagnosisRunState.Running }));
    }
}
