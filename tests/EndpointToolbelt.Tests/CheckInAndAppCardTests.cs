using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Platform;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.State;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The second round on a virtual machine. The IME's hourly check-in lines count as a check-in
/// (with a failure inside a check-in kept), a certificate thumbprint is never split into words, one
/// app in two scopes is no name conflict, an uninstall assignment says what it means, and the app
/// card's facts are short lines. Every ID and thumbprint here is made up for the test.
/// </summary>
public class CheckInAndAppCardTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.CreateCustomTimeZone("Test+1", TimeSpan.FromHours(1), "Test+1", "Test+1");
    private const string AppA = "99999999-8888-7777-6666-555555555555";
    private const string AppB = "77777777-6666-5555-4444-333333333333";
    private const string Thumbprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";

    // ---- 1. The IME's check-in lines --------------------------------------------------------

    private static string PatternFile()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        Assert.True(dir != null, "Repository root not found.");
        return Path.Combine(dir.FullName, "src", "Engine", "ImeLogPatterns.json");
    }

    private static ImeCheckInFacts Evaluate(IEnumerable<(DateTime Utc, string Message)> records)
        => ImeCheckInEvidence.Evaluate(records, ImeCheckInEvidence.Matchers(ImeLogPatternTable.Load(PatternFile()).CheckIn));

    /// <summary>One hour of the IME's own log on the virtual machine: no "Get policies" anywhere.</summary>
    private static IEnumerable<(DateTime Utc, string Message)> Hour(DateTime t, bool failInside = false)
    {
        yield return (t, "[ServiceBase], check in using device check in AAD App");
        yield return (t.AddSeconds(1), "Successfully updated throttling info. workload AgentCheckIn, currentCnt = 3");
        yield return (t.AddSeconds(1), "Finish throttle checking.");
        yield return (t.AddSeconds(2), "[Win32AppAsync] Starting app check in");
        if (failInside)
        {
            yield return (t.AddSeconds(3), "[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController with url checkin.dm.contoso.example/RestUserAuthLocationService/x");
        }
        yield return (t.AddSeconds(5), "[Win32AppAsync] End app check in");
        yield return (t.AddSeconds(6), "[Win32AvailableAppAsync] Starting app check in");
        if (failInside)
        {
            yield return (t.AddSeconds(7), "[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController with url checkin.dm.contoso.example/RestUserAuthLocationService/y");
        }
        yield return (t.AddSeconds(8), "[Win32AvailableAppAsync] End app check in");
    }

    [Fact]
    public void The_hourly_check_in_lines_count_without_any_Get_policies_line()
    {
        var records = Enumerable.Range(0, 5).SelectMany(h => Hour(Now.AddHours(-5 + h))).ToList();
        var facts = Evaluate(records);
        Assert.Equal(Now.AddHours(-1).AddSeconds(8), facts.LastSuccessUtc);
        Assert.Null(facts.LastFailureUtc);

        // The health row: OK, with the last check-in time.
        var status = new StatusInfo { Enrolled = true, LastImeCheckInUtc = facts.LastSuccessUtc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) };
        var row = HealthChecks.Run(new Source(), status, out _).Single(r => r.Name == HealthChecks.ImeCheckIn);
        Assert.Equal(HealthStatus.Pass, row.Status);
        Assert.DoesNotContain("unknown", row.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Both_app_workloads_and_the_throttling_line_are_each_a_check_in()
    {
        Assert.Equal(Now, Evaluate(new[] { (Now, "[Win32AppAsync] End app check in") }).LastSuccessUtc);
        Assert.Equal(Now, Evaluate(new[] { (Now, "[Win32AvailableAppAsync] End app check in") }).LastSuccessUtc);
        Assert.Equal(Now, Evaluate(new[] { (Now, "Successfully updated throttling info. workload AgentCheckIn, currentCnt = 1") }).LastSuccessUtc);
        // Neither the start of a check-in nor "Finish throttle checking." is a check-in.
        Assert.Null(Evaluate(new[] { (Now, "[Win32AppAsync] Starting app check in"), (Now, "Finish throttle checking.") }).LastSuccessUtc);
    }

    [Fact]
    public void A_failure_inside_a_check_in_keeps_the_failure()
    {
        var records = Hour(Now.AddHours(-2)).Concat(Hour(Now.AddHours(-1), failInside: true)).ToList();
        var facts = Evaluate(records);
        // The End lines of the failed hour do not count (each has a failure since its start); the
        // throttling line came before the failures, so the newest failure stays.
        Assert.Equal(Now.AddHours(-1).AddSeconds(7), facts.LastFailureUtc);
        Assert.Equal(Now.AddHours(-1).AddSeconds(1), facts.LastSuccessUtc);
    }

    [Fact]
    public void A_later_check_in_without_a_failure_inside_ends_the_failure()
    {
        var t = Now.AddHours(-1);
        var facts = Evaluate(new[]
        {
            (t, "[Win32AppAsync] Starting app check in"),
            (t.AddSeconds(1), "[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController with url checkin.dm.contoso.example/x"),
            (t.AddSeconds(2), "[Win32AppAsync] End app check in"),
            (t.AddMinutes(60), "[Win32AppAsync] Starting app check in"),
            (t.AddMinutes(60).AddSeconds(2), "[Win32AppAsync] End app check in"),
        });
        Assert.Null(facts.LastFailureUtc);
        Assert.Equal(t.AddMinutes(60).AddSeconds(2), facts.LastSuccessUtc);
    }

    [Fact]
    public void A_failure_with_no_later_success_stays_a_failure()
    {
        var facts = Evaluate(Hour(Now.AddHours(-3)).Concat(new[]
        {
            (Now.AddMinutes(-30), "[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController with url checkin.dm.contoso.example/x"),
        }));
        Assert.Equal(Now.AddMinutes(-30), facts.LastFailureUtc);
    }

    [Fact]
    public void A_recovered_failure_inside_a_check_in_does_not_cancel_its_end()
    {
        var t = Now.AddHours(-1);
        var facts = Evaluate(new[]
        {
            (t, "[Win32AppAsync] Starting app check in"),
            (t.AddSeconds(1), "[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController with url checkin.dm.contoso.example/x"),
            (t.AddSeconds(1), "[Location Service] Success!! LocationService ServiceAddresses Controller statusCode = OK"),
            (t.AddSeconds(4), "[Win32AppAsync] End app check in"),
        });
        Assert.Null(facts.LastFailureUtc);
        Assert.Equal(t.AddSeconds(4), facts.LastSuccessUtc);
    }

    private sealed class Source : IHealthSource
    {
        public DateTime UtcNow => Now;
        public string ImeServiceStatus() => "Running";
        public DsregInfo Dsreg() => new() { AzureAdJoined = "YES", AzureAdPrt = "YES", TenantName = "Contoso" };
        public DateTime? MdmCertificateExpiryUtc() => Now.AddDays(300);
        public bool CanReach(string host, int port, int timeoutMs) => true;
        public double FreeSystemDriveGB() => 100;
        public bool ServicingRestartPending() => false;
    }

    // ---- 2. Certificate thumbprints -----------------------------------------------------------

    [Theory]
    [InlineData("RootCATrustedCertificates/TrustedPublisher/" + Thumbprint + "/EncodedCertificate", "Trusted Publisher certificate ABCDEF01… (encoded certificate)")]
    // The service drops the leading area of a long path: the store before the thumbprint is enough.
    [InlineData("TrustedPublisher/" + Thumbprint + "/EncodedCertificate", "Trusted Publisher certificate ABCDEF01… (encoded certificate)")]
    [InlineData("RootCATrustedCertificates/Root/" + Thumbprint + "/EncodedCertificate", "Trusted Root certificate ABCDEF01… (encoded certificate)")]
    [InlineData("RootCATrustedCertificates/CA/" + Thumbprint, "Intermediate CA certificate ABCDEF01…")]
    // A SHA-256 thumbprint (64 characters).
    [InlineData("RootCATrustedCertificates/TrustedPeople/" + Thumbprint + "ABCDEF0123456789ABCDEF01/EncodedCertificate", "Trusted People certificate ABCDEF01… (encoded certificate)")]
    public void A_certificate_setting_names_its_store_and_a_short_thumbprint(string token, string expected)
        => Assert.Equal(expected, SettingNames.Friendly(token));

    [Fact]
    public void A_thumbprint_or_another_code_is_never_split_into_words()
    {
        var lower = Thumbprint.ToLowerInvariant();
        Assert.Equal("ABCDEF01…", SettingNames.Words(lower, capitalFirst: true));
        // An unknown area: the thumbprint stays whole in short form, never "F 2 be 45".
        Assert.Equal("ABCDEF01…: encoded certificate", SettingNames.Friendly("ContosoStore/" + lower + "/EncodedCertificate"));
        Assert.Equal("0A1B2C3D4E5F60718293", SettingNames.Words("0A1B2C3D4E5F60718293", capitalFirst: true));
        Assert.Equal("QUJDRDEyMzQ1Njc4OTA_", SettingNames.Words("QUJDRDEyMzQ1Njc4OTA_", capitalFirst: false));
        // Words still split as before.
        Assert.Equal("Minimum PIN length", SettingNames.Words("MinimumPINLength", capitalFirst: true));
        // The report and Copy details for IT may carry the whole thumbprint.
        Assert.Contains(Thumbprint, SettingNames.WithToken("RootCATrustedCertificates/TrustedPublisher/" + Thumbprint + "/EncodedCertificate"));
    }

    [Fact]
    public void The_service_keeps_a_thumbprint_whole()
    {
        var token = ProfileErrorsProbe.ShortSetting("./Device/Vendor/MSFT/RootCATrustedCertificates/TrustedPublisher/" + Thumbprint + "/EncodedCertificate");
        Assert.Contains("/" + Thumbprint + "/", "/" + token + "/");
        Assert.Equal("Trusted Publisher certificate ABCDEF01… (encoded certificate)", SettingNames.Friendly(token));
    }

    // ---- 3. One app in two scopes ---------------------------------------------------------------

    private static RunOutcome Outcome(params AppEvidence[] apps)
    {
        var records = apps.Select(a => new EngineRecord { Type = "app-diagnosis", App = a, Utc = Now.AddSeconds(-30) }).ToList();
        records.Add(new EngineRecord { Type = "apps-diagnosis", Closing = new DiagnosisClose { Present = true, Records = apps.Length, Listed = apps.Length, UserScope = true }, Utc = Now.AddSeconds(-20) });
        records.Add(new EngineRecord { Type = "summary", Exit = 0, Changes = 0, Utc = Now.AddSeconds(-10) });
        return new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "diag0029", Result = ResultCode.Completed, ExitCode = 0 }.With(records);
    }

    [Fact]
    public void One_app_in_two_scopes_is_no_name_conflict()
    {
        var device = Evidence("RETRY-HELPS", scope: "device", name: "Contoso Remover");
        var user = Evidence("AVAILABLE", intent: "available", scope: "user", name: "Contoso Remover");
        var diagnosis = AppDecisions.From(Outcome(device, user), Now, Amsterdam);
        Assert.All(diagnosis.Apps, a => Assert.False(a.SameNameAsAnother));
        Assert.Equal(new[] { AppCase.InstallFailed, AppCase.Offered }, diagnosis.Apps.Select(a => a.Case));
        var home = HomeSummary.Build(new List<HealthResult>(), diagnosis, serviceMissing: false);
        Assert.NotEqual(AppNextStep.ChooseApp, home.AppStep);

        // Two different apps of one name still are.
        var other = Evidence("RETRY-HELPS", scope: "device", name: "Contoso Remover", id: AppB);
        var conflict = AppDecisions.From(Outcome(device, user, other), Now, Amsterdam);
        Assert.All(conflict.Apps, a => Assert.True(a.SameNameAsAnother));
    }

    // ---- 4. An uninstall assignment -----------------------------------------------------------

    [Fact]
    public void An_app_Intune_wants_absent_and_an_MSI_app_Windows_no_longer_has_read_as_not_installed_with_nothing_to_do()
    {
        // An uninstall assignment already absent: nothing was removed, so the card does not say "removed".
        var absent = Evidence("OK", intent: "uninstall", state: "not present, as assigned");
        var a = AppDecisions.Decide(absent, new[] { absent }, Now, Amsterdam);
        Assert.Equal(AppObservation.NotInstalledAsAssigned, a.Observation);
        Assert.Equal("Contoso Agent: not installed (as assigned)", a.Title);
        Assert.False(a.NeedsAttention);
        // An app that a newer one supersedes, wanted absent and absent.
        var superseded = Evidence("NO-ACTION", state: "not present, as assigned: Intune wants it absent on this device, as for an app that a newer one supersedes");
        var s = AppDecisions.Decide(superseded, new[] { superseded }, Now, Amsterdam);
        Assert.Equal(AppObservation.NotInstalledAsAssigned, s.Observation);
        Assert.StartsWith("Intune wants this app absent on this device", s.Text);
        // An MSI app recorded as installed whose product Windows Installer no longer has.
        var replaced = Evidence("NO-ACTION", state: "recorded as installed; Windows Installer no longer has it (replaced or removed)");
        var r = AppDecisions.Decide(replaced, new[] { replaced }, Now, Amsterdam);
        Assert.Equal("Contoso Agent: not installed (replaced or removed)", r.Title);
        Assert.Equal(AppObservation.NotInstalledAsAssigned, r.Observation);
    }

    [Fact]
    public void The_engine_tells_an_uninstall_assignment_still_detected_from_one_with_nothing_to_remove()
    {
        Assert.Equal("PENDING", AppState.Verdict(null, 1, 4, 0, null).Verdict);
        // Not detected: the assignment is met ("not present, as assigned"), which the app shows as
        // not installed (as assigned), nothing to do.
        var met = AppState.Verdict(null, 2, 4, 0, null);
        Assert.Equal("OK", met.Verdict);
        var app = Evidence(met.Verdict, intent: "uninstall", state: met.State);
        var f = AppDecisions.Decide(app, new[] { app }, Now, Amsterdam);
        Assert.Equal(AppObservation.NotInstalledAsAssigned, f.Observation);
        Assert.False(f.NeedsAttention);
        Assert.Equal(AppNextStep.None, f.Next);
        Assert.Equal("NO-ACTION", AppState.Verdict(null, null, 4, 0, null).Verdict);
        Assert.Equal("PENDING", AppState.Verdict(null, null, 4, 0, new AppStatusReport { Status = "Installed" }).Verdict);
        Assert.Equal("OK", AppState.Verdict(null, null, 3, 0, new AppStatusReport { Status = "Installed" }).Verdict);
    }

    [Fact]
    public void An_uninstall_assignment_with_nothing_to_remove_is_not_installed_as_assigned()
    {
        var app = Evidence("NO-ACTION", intent: "uninstall", scope: "user", state: "uninstall assignment; no uninstall attempt recorded");
        var f = AppDecisions.Decide(app, new[] { app }, Now, Amsterdam);
        Assert.Equal(AppCase.NotInstalledAsAssigned, f.Case);
        Assert.Equal("Not installed (as assigned)", AppDecisions.Word(f.Observation));
        Assert.Equal("This app is assigned to be removed, and it is not on this device. Nothing to do.", f.Text);
        Assert.Equal(AppNextStep.None, f.Next);
        Assert.False(f.NeedsAttention);
        // Company Portal does not offer an app assigned to be removed.
        Assert.False(AppDecisions.HasPortalPage(f));
        Assert.False(AppDecisions.ShowPortalLink(f, AppNextStep.None));
        Assert.Contains("uninstall assignment", f.Card().Support);
    }

    [Fact]
    public void An_uninstall_assignment_still_on_the_device_waits_and_a_failed_one_says_so()
    {
        var waiting = Evidence("PENDING", intent: "uninstall", state: "uninstall assignment; still detected, no uninstall attempt recorded");
        var w = AppDecisions.Decide(waiting, new[] { waiting }, Now, Amsterdam);
        Assert.Equal(AppCase.WaitingForRemoval, w.Case);
        Assert.Equal("Contoso Agent: waiting to be removed", w.Title);
        Assert.Equal("Awaiting evidence", AppDecisions.Word(w.Observation));

        var failed = Evidence("RETRY-HELPS", intent: "uninstall", code: "0x80070643", state: "failed with an error");
        var x = AppDecisions.Decide(failed, new[] { failed }, Now, Amsterdam);
        Assert.Equal(AppCase.RemovalFailed, x.Case);
        Assert.Equal("Contoso Agent: the removal failed", x.Title);
        Assert.Contains("exit code 1603", x.Text);
        Assert.Equal("Failed", AppDecisions.Word(x.Observation));

        var removed = Evidence("OK", intent: "uninstall", state: "uninstalled / succeeded");
        var r = AppDecisions.Decide(removed, new[] { removed }, Now, Amsterdam);
        Assert.Equal("Contoso Agent: removed as assigned", r.Title);
        Assert.Equal("Not installed (as assigned)", AppDecisions.Word(r.Observation));
    }

    [Fact]
    public void A_dependency_that_is_not_installed_has_nothing_to_do_and_an_empty_state_stays_unknown()
    {
        var dependency = Evidence("NO-ACTION", intent: "dependency", state: "not installed; a dependency, not assigned itself");
        var d = AppDecisions.Decide(dependency, new[] { dependency }, Now, Amsterdam);
        Assert.Equal(AppCase.NotInstalledAsAssigned, d.Case);
        Assert.Equal("Not installed (as assigned)", AppDecisions.Word(d.Observation));

        var empty = Evidence("NO-STATE", state: "no state recorded");
        Assert.Equal("Unknown", AppDecisions.Word(AppDecisions.Decide(empty, new[] { empty }, Now, Amsterdam).Observation));
        // Ambiguous on purpose: the IME's record does not say which of the two.
        var unsure = Evidence("NO-ACTION", state: "not detected and never attempted: requirements not met, or superseded");
        Assert.Equal("Unknown", AppDecisions.Word(AppDecisions.Decide(unsure, new[] { unsure }, Now, Amsterdam).Observation));
    }

    // ---- 5. The card's facts --------------------------------------------------------------------

    [Fact]
    public void The_card_says_its_facts_in_short_lines()
    {
        var app = Evidence("AVAILABLE", intent: "available", scope: "user", name: "", observed: "2026-11-11T09:35:00Z", revision: 2);
        var f = AppDecisions.Decide(app, new[] { app }, Now, Amsterdam);
        var facts = AppDecisions.DetailFacts(f, Amsterdam);
        Assert.Equal(new[] { "Status", "Assigned to", "Revision", "App ID", "Name" }, facts.Select(x => x.Label));
        Assert.Equal("Awaiting evidence, recorded 2026-11-11 10:35 (UTC+01:00)", facts[0].Value);
        Assert.Equal("you", facts[1].Value);
        Assert.Equal("2, last checked by the device on 11 Nov", facts[2].Value);
        Assert.Equal(AppA, facts[3].Value);
        Assert.Equal("not known on this device", facts[4].Value);
        var lines = AppDecisions.DetailState(f, Amsterdam).Split('\n');
        Assert.Equal(5, lines.Length);
        Assert.All(lines, l => Assert.DoesNotContain(" · ", l));
        Assert.Equal("Open in Company Portal to see it", AppDecisions.PortalLinkText(f));

        // A named app for the device: no name line, its own link label.
        var named = Evidence("RETRY-HELPS");
        var n = AppDecisions.Decide(named, new[] { named }, Now, Amsterdam);
        Assert.Equal(new[] { "Status", "Assigned to", "Revision", "App ID" }, AppDecisions.DetailFacts(n, Amsterdam).Select(x => x.Label));
        Assert.Equal("this device", AppDecisions.DetailFacts(n, Amsterdam)[1].Value);
        Assert.Equal("Open in Company Portal", AppDecisions.PortalLinkText(n));
        // A finding without an app: its status only.
        Assert.Equal("Status: Unknown", AppDecisions.DetailState(AppDecisions.NotListed(null), Amsterdam));
    }

    private static AppEvidence Evidence(string verdict, string intent = "required", string code = "", string id = AppA, string name = "Contoso Agent",
        string observed = "2026-11-13T12:00:00Z", string scope = "device", int revision = 3, string state = "a state") => new()
    {
        DiagnosisId = "diag0029",
        AppId = id,
        Name = name,
        Scope = scope,
        Intent = intent,
        Revision = revision,
        Verdict = verdict,
        State = state,
        Code = code,
        ObservedText = observed,
    };
}
