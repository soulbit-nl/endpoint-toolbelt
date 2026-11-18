using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.Actions;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Engine.Support;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The app diagnosis beyond Win32 apps, read-only. MSI line-of-business apps from the MDM stack's
/// EnterpriseDesktopAppManagement records (a fake registry tree per state: the device's SID, the
/// caller's own and another user's), Microsoft Store apps the IME installs through WinGet (by WinGet's
/// own codes or the IME log), and how the app list words them: their kind, one step each, no Company
/// Portal link and no Retry work apps and settings for an MSI app. No SID or URL anywhere.
/// </summary>
public class MsiAndStoreAppTests
{
    private const string Root = @"TEST\EnterpriseDesktopAppManagement";
    private const string DeviceSid = "S-0-0-00-0000000000-0000000000-000000000-000";
    private const string CallerSid = "S-1-12-1-1111111111-2222222222-3333333333-4444444444";
    private const string OtherSid = "S-1-12-1-5555555555-6666666666-7777777777-8888888888";
    private const string Agent = "{AAAAAAAA-1111-2222-3333-444444444444}";
    private const string Viewer = "{BBBBBBBB-1111-2222-3333-444444444444}";
    private const string Theirs = "{CCCCCCCC-1111-2222-3333-444444444444}";
    private static readonly DateTime Written = new(2026, 11, 10, 13, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 11, 10, 14, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("MSI app test zone", TimeSpan.FromHours(1), "MSI app test zone", "MSI app test zone");

    private readonly EngineHarness _h = new();

    /// <summary>One MSI app as the MDM stack keeps it: &lt;Root&gt;\&lt;SID&gt;\MSI\{ProductCode}.</summary>
    private void Msi(string sid, string product, int? status, long lastError = 0, string url = null, string name = null)
    {
        var key = Root + "\\" + sid + @"\MSI\" + product;
        _h.Registry.AddKey(key);
        if (status.HasValue) { _h.Registry.Put(key, "Status", status.Value); }
        if (lastError != 0) { _h.Registry.Put(key, "LastError", lastError > int.MaxValue ? (int)(lastError - 4294967296L) : (int)lastError); }
        if (url != null) { _h.Registry.Put(key, "CurrentDownloadUrl", url); }
        _h.Registry.Put(key, "EnforcementRetryIndex", 2);
        _h.Registry.Put(key, "EnforcementRetryCount", 3);
        _h.Registry.WriteTimes[key] = Written;
        if (name != null) { _h.Registry.Put(@"TEST\Uninstall\" + product, "DisplayName", name); }
    }

    private List<JsonNode> Run(string caller = CallerSid, DiagnoseAppsHandler handler = null)
    {
        var run = _h.NewRun("diag0065", "DiagnoseApps");
        run.CallerSid = caller;
        Assert.Equal(EngineExitCode.Success, (handler ?? new DiagnoseAppsHandler()).Run(run));
        return _h.Records("app-diagnosis");
    }

    // --- Engine: the CSP's status values --------------------------------------------------------------

    [Theory]
    [InlineData(70, 0L, "OK", "installed (Windows Installer finished the install)", false)]
    [InlineData(10, 0L, "IN-PROGRESS", "received, not started yet", false)]
    [InlineData(20, 0L, "IN-PROGRESS", "downloading", false)]
    [InlineData(25, 0x80072EE2L, "IN-PROGRESS", "the download failed; Windows tries it again by itself: ERROR_WINHTTP_TIMEOUT (0x80072EE2)", true)]
    [InlineData(30, 0x80072EE7L, "RETRY-HELPS", "the download failed: ERROR_WINHTTP_NAME_NOT_RESOLVED (0x80072EE7)", true)]
    [InlineData(40, 0L, "IN-PROGRESS", "downloaded; the install has not started yet", false)]
    [InlineData(48, 0L, "PENDING", "waits for the user to sign in", false)]
    [InlineData(50, 0L, "IN-PROGRESS", "installing", false)]
    [InlineData(55, 0x80070652L, "IN-PROGRESS", "the install failed; Windows tries it again by itself: MSI 1618, another installation is running (0x80070652)", false)]
    [InlineData(60, 0x80070643L, "RETRY-HELPS", "the install failed: MSI 1603, fatal error during installation (0x80070643)", false)]
    [InlineData(60, 0x80070BC2L, "RESTART-FIRST", "installed; a restart finishes it: 3010, restart required (0x80070BC2)", false)]
    [InlineData(60, 0x80071234L, "RETRY-HELPS", "the install failed: error 0x80071234", false)]
    [InlineData(99, 0L, "UNKNOWN", "status 99", false)]
    public void Each_documented_status_has_its_words_and_verdict(int status, long code, string verdict, string state, bool download)
    {
        var (v, s, d) = LobApps.Verdict(status, code);
        Assert.Equal(verdict, v);
        Assert.Equal(state, s);
        Assert.Equal(download, d);
    }

    [Fact]
    public void Without_a_status_the_record_says_so()
    {
        Assert.Equal(("NO-STATE", "no status recorded", false), LobApps.Verdict(null, 0));
        Assert.Equal("UNKNOWN", LobApps.Verdict(null, 0x80070643L).Verdict);
        Assert.True(LobApps.IsDeviceScope(DeviceSid));
        Assert.True(LobApps.IsDeviceScope("S-0-0-00-0000000000-00000000000-00000000000-0000000000-00"));
        Assert.False(LobApps.IsDeviceScope(CallerSid));
    }

    // --- Engine: the readout and the records -----------------------------------------------------------

    [Fact]
    public void The_device_and_the_callers_own_MSI_apps_are_listed_others_only_counted()
    {
        Msi(DeviceSid, Agent, 60, 0x80070643L, url: "https://contoso.manage.microsoft.com/content/app.msi?token=secret", name: "Contoso Agent");
        Msi(CallerSid, Viewer, 70, name: "Contoso Viewer");
        Msi(OtherSid, Theirs, 60, 0x80070643L);

        var apps = Run();

        Assert.Equal(new[] { "aaaaaaaa-1111-2222-3333-444444444444", "bbbbbbbb-1111-2222-3333-444444444444" }, apps.Select(a => a.Str("app")));
        var agent = apps[0];
        Assert.Equal("msi", agent.Str("kind"));
        Assert.Equal("Contoso Agent", agent.Str("name"));
        Assert.Equal("device", agent.Str("scope"));
        Assert.Equal("RETRY-HELPS", agent.Str("verdict"));
        Assert.Equal("the install failed: MSI 1603, fatal error during installation (0x80070643)", agent.Str("state"));
        Assert.Equal("0x80070643", agent.Str("code"));
        Assert.Equal("60", agent.Str("msistatus"));
        Assert.Equal("2026-11-10T13:00:00Z", agent.Str("observed"));
        Assert.Equal("contoso.manage.microsoft.com", agent.Str("downloadhost"));
        Assert.Equal("2", agent.Str("retryindex"));
        Assert.Equal("3", agent.Str("retrycount"));
        Assert.StartsWith("MSI 1603, fatal error during installation: the installer itself failed", agent.Str("codemeaning"));
        Assert.Equal("user", apps[1].Str("scope"));
        Assert.Equal("OK", apps[1].Str("verdict"));

        // Never the SIDs, the other user's app or the download address beyond its host.
        Assert.DoesNotContain(_h.Lines, l => l.Contains(CallerSid) || l.Contains(OtherSid) || l.Contains("cccccccc") || l.Contains("token=") || l.Contains("/content/"));
        var close = Assert.Single(_h.Records("apps-diagnosis"));
        Assert.Equal("2", close.Str("msi"));
        Assert.Equal("1", close.Str("msiotherscopes"));
        Assert.Equal("1 app need attention", _h.Findings("DIAG-APPS-ATTENTION").Single().Str("title"));
        Assert.Empty(_h.Records("change"));
    }

    [Fact]
    public void A_finished_install_whose_product_Windows_Installer_no_longer_lists_is_not_installed()
    {
        // An older version of the device's agent, replaced since by a newer product code: no uninstall entry.
        Msi(DeviceSid, Agent, 70);
        // The current version, listed by Windows Installer.
        Msi(DeviceSid, Viewer, 70, name: "Contoso Viewer");
        // A user's own product: its uninstall entry is in that user's hive, which is not read, so no conclusion.
        Msi(CallerSid, Theirs, 70);

        var apps = Run().ToDictionary(a => a.Str("app"));

        var old = apps["aaaaaaaa-1111-2222-3333-444444444444"];
        Assert.Equal("NO-ACTION", old.Str("verdict"));
        Assert.Equal("recorded as installed; Windows Installer no longer has it (replaced or removed)", old.Str("state"));
        Assert.Equal("", old.Str("name"));
        Assert.Equal("OK", apps["bbbbbbbb-1111-2222-3333-444444444444"].Str("verdict"));
        Assert.Equal("OK", apps["cccccccc-1111-2222-3333-444444444444"].Str("verdict"));
        // The app reads it as nothing to do, not as installed.
        var card = Decide(Parse("NO-ACTION", LobApps.NoLongerInstalled, msiStatus: 70));
        Assert.NotEqual(AppObservation.Detected, card.Observation);
        Assert.False(card.NeedsAttention);

        // An uninstall entry without a name still counts as listed; without uninstall keys to read nothing is concluded.
        _h.Registry.AddKey(@"TEST\WOW6432Node\Uninstall\" + Agent);
        Assert.Equal("OK", Run().Last(a => a.Str("app") == "aaaaaaaa-1111-2222-3333-444444444444").Str("verdict"));
        _h.Services.ImePaths.UninstallKeys = Array.Empty<string>();
        Assert.True(LobApps.HasUninstallEntry(_h.NewRun(), "dddddddd-1111-2222-3333-444444444444") == null);
    }

    [Fact]
    public void A_name_comes_from_Windows_Installer_and_is_empty_for_an_app_that_is_not_installed()
    {
        Msi(DeviceSid, Agent, 30, 0x80072EE7L, url: "not a url");
        var apps = Run();
        Assert.Equal("", apps.Single().Str("name"));
        Assert.Equal("", apps.Single().Str("downloadhost"));
        Assert.Equal("true", apps.Single().Str("download"));
    }

    [Fact]
    public void MSI_apps_show_also_on_a_device_without_any_Win32_app_state()
    {
        Msi(DeviceSid, Agent, 50);
        var apps = Run();
        Assert.Equal("msi", apps.Single().Str("kind"));
        Assert.Empty(_h.Findings("APPS-NO-STATE"));
        Assert.Equal("No app needs attention", _h.Findings("DIAG-APPS-OK").Single().Str("title"));
    }

    [Fact]
    public void Without_any_app_state_the_old_finding_stays()
    {
        Run();
        Assert.Single(_h.Findings("APPS-NO-STATE"));
        Assert.Equal("0", Assert.Single(_h.Records("apps-diagnosis")).Str("msi"));
    }

    [Fact]
    public void A_Store_app_is_known_by_a_WinGet_code_or_the_IME_logs_WinGet_lines_and_gets_its_code_in_words()
    {
        const string store = "dddddddd-1111-2222-3333-444444444444";
        const string fromLog = "eeeeeeee-1111-2222-3333-444444444444";
        const string win32 = "ffffffff-1111-2222-3333-444444444444";
        EngineAppDiagnosisTests.AddApp(_h.Registry, AppState.DeviceScopeId, store, 1, intent: 3, state: 5000, code: unchecked((int)0x8A150014));
        EngineAppDiagnosisTests.AddApp(_h.Registry, AppState.DeviceScopeId, fromLog, 1, intent: 3, state: 1000, compliance: 1);
        EngineAppDiagnosisTests.AddApp(_h.Registry, AppState.DeviceScopeId, win32, 1, intent: 3, state: 1000, compliance: 1);
        var handler = new DiagnoseAppsHandler { Parts = new DiagnosisParts { StoreIds = (_, ids) => new HashSet<string>(ids.Where(i => i == fromLog)) } };

        var apps = Run(handler: handler);

        var s = apps.Single(a => a.Str("app") == store);
        Assert.Equal("store", s.Str("kind"));
        Assert.Equal("WinGet: package not found in source: the app was not found in the app source (the Microsoft Store).", s.Str("codemeaning"));
        Assert.Equal("Contact your IT support.", s.Str("codestep"));
        Assert.Equal("it", s.Str("codenext"));
        Assert.Equal("store", apps.Single(a => a.Str("app") == fromLog).Str("kind"));
        Assert.Equal("win32", apps.Single(a => a.Str("app") == win32).Str("kind"));
        Assert.Equal("", apps.Single(a => a.Str("app") == win32).Str("codemeaning"));
    }

    [Fact]
    public void The_WinGet_codes_are_in_the_shared_table_with_their_step()
    {
        Assert.Equal("WinGet: no applicable installer", CodeKnowledge.Get("0x8A150010").Name);
        Assert.Equal("WinGet: download failed", CodeKnowledge.Get(unchecked((int)0x8A150008).ToString(CultureInfo.InvariantCulture)).Name);
        Assert.Equal("connectivity", LobApps.WinGetNext("0x8A150008"));
        Assert.Equal("retry", LobApps.WinGetNext("0x8A15001E"));
        Assert.Equal("restart", LobApps.WinGetNext("0x8A150109"));
        Assert.Equal("wait", LobApps.WinGetNext("0x8A150102"));
        Assert.Equal("it", LobApps.WinGetNext("0x8A150010"));
        Assert.Equal("", LobApps.WinGetNext("0x80070643"));
        Assert.True(LobApps.IsWinGetCode("0x8A150014"));
        Assert.False(LobApps.IsWinGetCode("0x87D1041C"));
    }

    [Fact]
    public void The_MSI_readout_writes_nothing()
    {
        Msi(DeviceSid, Agent, 60, 0x80070643L);
        _h.Registry.FailWrites = true;
        Run();
        Assert.Empty(_h.Processes.Requests);
        Assert.Equal(0, _h.ServiceControl.Calls);
        Assert.Empty(_h.Records("change"));
    }

    // --- App: how the list words them ----------------------------------------------------------------

    private static AppEvidence Parse(string verdict, string state, string code = "", bool download = false, string kind = "msi", string meaning = "", string step = "", string support = "",
        string next = "", int? msiStatus = 60, string host = "contoso.manage.microsoft.com", string scope = "device")
    {
        var json = "{\"type\":\"app-diagnosis\",\"run\":\"diag0065\",\"diagnosis\":\"diag0065\",\"app\":\"aaaaaaaa-1111-2222-3333-444444444444\",\"name\":\"\",\"scope\":\"" + scope
            + "\",\"intent\":\"\",\"revision\":null,\"verdict\":\"" + verdict + "\",\"state\":\"" + state + "\",\"code\":\"" + code + "\",\"observed\":\"2026-11-10T13:00:00Z\",\"download\":" + (download ? "true" : "false")
            + ",\"shared\":false,\"kind\":\"" + kind + "\",\"codemeaning\":\"" + meaning + "\",\"codestep\":\"" + step + "\",\"codesupport\":\"" + support + "\",\"codenext\":\"" + next
            + "\",\"msistatus\":" + (msiStatus?.ToString(CultureInfo.InvariantCulture) ?? "null") + ",\"version\":\"4.2.1\",\"downloadhost\":\"" + host + "\",\"retryindex\":2,\"retrycount\":3}";
        return EngineRecord.Parse(json).App;
    }

    private static AppFinding Decide(AppEvidence app) => AppDecisions.Decide(app, new[] { app }, Now, Zone);

    [Fact]
    public void A_failed_MSI_app_hands_over_to_IT_with_the_codes_meaning_and_never_offers_Retry_or_Company_Portal()
    {
        var app = Parse("RETRY-HELPS", "the install failed: MSI 1603, fatal error during installation (0x80070643)", "0x80070643",
            meaning: "MSI 1603, fatal error during installation: the installer itself failed.", support: "Read the installer's own log.", scope: "user");
        var f = Decide(app);
        Assert.Equal("MSI app aaaaaaaa", app.Label);
        Assert.Equal(AppCase.InstallFailed, f.Case);
        Assert.Equal(AppNextStep.CopyForIt, f.Next);
        Assert.Equal("MSI app aaaaaaaa: the install failed", f.Title);
        Assert.Contains("Windows Installer could not install it (the install failed: MSI 1603", f.Text);
        Assert.Contains("the installer itself failed", f.Text);
        Assert.Contains("Attempt 3 of 3.", f.Text);
        Assert.Contains("Windows Installer's own log has the reason", f.Support);
        // The handover gives IT the record with its code, then the product code (View details and Copy details for IT).
        Assert.Equal("MSI record: the install failed: MSI 1603, fatal error during installation (0x80070643).", f.Record);
        var it = f.Card().Support;
        Assert.Contains("MSI record: the install failed: MSI 1603, fatal error during installation (0x80070643). MSI product code {AAAAAAAA-1111-2222-3333-444444444444}", it, StringComparison.Ordinal);
        Assert.Equal("", f.Caveat);
        Assert.DoesNotContain("Retry work apps and settings", f.Text + f.NextText);
        Assert.False(AppDecisions.HasPortalPage(f));
        Assert.Equal("MSI app, the one assigned to you, version 4.2.1, product code {AAAAAAAA-1111-2222-3333-444444444444}", AppDecisions.Identify(app));
        Assert.Equal("MSI, assigned to you. Copy details for IT and send it to your IT support.", new EndpointToolbelt.App.Ui.AppRowView(f, false).Summary);

        // Retry work apps and settings does not retry an MSI app: it is never part of its group.
        var diagnosis = AppDecisions.From(OutcomeOf(app), Now, Zone);
        Assert.Empty(PostCheck.Retried(diagnosis));
    }

    private static RunOutcome OutcomeOf(params AppEvidence[] apps)
    {
        var outcome = new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "diag0065", Result = ResultCode.Completed, ExitCode = 0 };
        foreach (var app in apps) { outcome.Records.Add(new EngineRecord { Type = "app-diagnosis", Run = "diag0065", App = app }); }
        outcome.Records.Add(EngineRecord.Parse("{\"type\":\"apps-diagnosis\",\"run\":\"diag0065\",\"diagnosis\":\"diag0065\",\"present\":true,\"records\":0,\"listed\":0,\"scopes\":1,\"otherscopes\":0,\"userscope\":true,\"logevidence\":false,\"msi\":1}"));
        return outcome;
    }

    [Fact]
    public void An_MSI_download_problem_offers_the_connection_check_and_names_only_the_host()
    {
        var f = Decide(Parse("RETRY-HELPS", "the download failed: ERROR_WINHTTP_NAME_NOT_RESOLVED (0x80072EE7)", "0x80072EE7", download: true));
        Assert.Equal(AppCase.DownloadError, f.Case);
        Assert.Equal(AppNextStep.ConnectivityTest, f.Next);
        Assert.Contains("The MSI file could not be downloaded (from contoso.manage.microsoft.com)", f.Text);
        Assert.Contains("contoso.manage.microsoft.com", f.Support);
        Assert.Equal("", f.Caveat);
        // Not a handover: the connection check is the step, so no record line for IT.
        Assert.Equal("", f.Record);
    }

    [Fact]
    public void Another_installation_running_waits_and_a_restart_asks_for_the_restart()
    {
        var busy = Decide(Parse("RETRY-HELPS", "the install failed: MSI 1618, another installation is running (0x80070652)", "0x80070652"));
        Assert.Equal(AppNextStep.Wait, busy.Next);
        Assert.Equal(AppObservation.AwaitingEvidence, busy.Observation);
        var restart = Decide(Parse("RESTART-FIRST", "installed; a restart finishes it: 3010, restart required (0x80070BC2)", "0x80070BC2"));
        Assert.Equal(AppNextStep.Restart, restart.Next);
        var installing = Decide(Parse("IN-PROGRESS", "installing", msiStatus: 50));
        Assert.Equal(AppCase.InProgress, installing.Case);
        Assert.Equal(AppNextStep.Wait, installing.Next);
        var installed = Decide(Parse("OK", "installed (Windows Installer finished the install)", msiStatus: 70));
        Assert.Equal(AppObservation.Detected, installed.Observation);
        Assert.Equal("MSI app aaaaaaaa: installed", installed.Title);
        var unknown = Decide(Parse("UNKNOWN", "status 99", msiStatus: 99));
        Assert.Equal(AppNextStep.CopyForIt, unknown.Next);
        Assert.NotEmpty(unknown.Uncertainty);
        Assert.Equal("MSI record: status 99.", unknown.Record);
        Assert.Equal("", busy.Record);
    }

    [Fact]
    public void A_Store_app_with_a_WinGet_code_gets_the_codes_words_and_step()
    {
        var notFound = Decide(Parse("RETRY-HELPS", "failed with an error", "0x8A150014", kind: "store", meaning: "WinGet: package not found in source: the app was not found in the app source (the Microsoft Store).",
            step: "Contact your IT support.", support: "Check that the app is still available in the Microsoft Store.", next: "it", msiStatus: null, host: ""));
        Assert.Equal(AppNextStep.CopyForIt, notFound.Next);
        Assert.Equal("Contact your IT support.", notFound.NextText);
        Assert.Contains("WinGet says: WinGet: package not found in source", notFound.Text);
        Assert.Equal("Check that the app is still available in the Microsoft Store.", notFound.Support);
        Assert.Equal("", notFound.Caveat);
        Assert.Equal("Store, for this device. Contact your IT support.", new EndpointToolbelt.App.Ui.AppRowView(notFound, false).Summary);

        var download = Decide(Parse("RETRY-HELPS", "failed with an error", "0x8A150008", kind: "store", meaning: "WinGet: download failed: the app's installer could not be downloaded.",
            step: "Run Check Microsoft connections.", next: "connectivity", msiStatus: null, host: ""));
        Assert.Equal(AppNextStep.ConnectivityTest, download.Next);
    }

    [Fact]
    public void An_MSI_apps_card_facts_name_its_type_version_and_product_code_and_IT_reads_the_product_code()
    {
        var app = Parse("RETRY-HELPS", "the install failed: MSI 1603, fatal error during installation (0x80070643)", "0x80070643", scope: "user");
        var f = Decide(app);

        // Short facts, one per line: no revision and no Intune app ID for an MSI app.
        var facts = AppDecisions.DetailState(f, Zone).Split('\n');
        Assert.StartsWith("Status: Failed, recorded ", facts[0], StringComparison.Ordinal);
        Assert.Equal(new[] { "Assigned to: you", "Type: MSI line-of-business app", "Version: 4.2.1", "Product code: {AAAAAAAA-1111-2222-3333-444444444444}", "Name: not known on this device" }, facts.Skip(1));
        // IT's line in View details and Copy details for IT: the product code and version, never "App ID".
        Assert.Equal("MSI product code {AAAAAAAA-1111-2222-3333-444444444444}, version 4.2.1 (user assignment).", f.Identity);
        Assert.Contains("MSI product code {AAAAAAAA-1111-2222-3333-444444444444}", f.Card().Support, StringComparison.Ordinal);
        Assert.DoesNotContain("App ID", f.Card().Support, StringComparison.Ordinal);

        // A name from Windows Installer (the name cache's source "installer"): no "not known" fact, and never "from an earlier IME log".
        app.Name = "Contoso Agent";
        app.NameSource = "installer";
        app.Version = "";
        var named = Decide(app);
        Assert.Equal(new[] { "Assigned to: you", "Type: MSI line-of-business app", "Product code: {AAAAAAAA-1111-2222-3333-444444444444}" },
            AppDecisions.DetailFacts(named, Zone).Skip(1).Select(x => x.Label + ": " + x.Value));
        Assert.False(named.NameFromEarlierLog);
        Assert.Equal("MSI product code {AAAAAAAA-1111-2222-3333-444444444444} (user assignment).", named.Identity);
    }

    [Fact]
    public void A_Store_apps_card_facts_say_its_type_and_keep_the_Intune_app_ID()
    {
        var app = Parse("RETRY-HELPS", "failed with an error", "0x8A150014", kind: "store", msiStatus: null, host: "", scope: "user");
        var facts = AppDecisions.DetailFacts(Decide(app), Zone).Select(x => x.Label + ": " + x.Value).ToList();
        Assert.Contains("Type: Microsoft Store app", facts);
        Assert.Contains("App ID: aaaaaaaa-1111-2222-3333-444444444444", facts);
        Assert.DoesNotContain(facts, x => x.StartsWith("Product code", StringComparison.Ordinal));
        // A Win32 app, the usual kind, has no Type line.
        var win32 = Parse("RETRY-HELPS", "failed with an error", "0x87D1041C", kind: "win32", msiStatus: null, host: "", scope: "user");
        Assert.DoesNotContain(AppDecisions.DetailFacts(Decide(win32), Zone), x => x.Label == "Type");
    }

    [Fact]
    public void An_MSI_apps_record_names_Windows_Installer_as_its_name_source()
    {
        Msi(DeviceSid, Agent, 70, name: "Contoso Agent");
        Msi(DeviceSid, Viewer, 60, 0x80070643L);

        var apps = Run();

        Assert.Equal("installer", apps.Single(a => a.Str("name") == "Contoso Agent").Str("namesource"));
        Assert.Equal("", apps.Single(a => a.Str("name") == "").Str("namesource"));
    }

    // --- Engine: the support package -----------------------------------------------------------------

    /// <summary>The package's state tables for the caller, with the other state empty; the MSI apps from the fake registry unless <paramref name="read"/> is given.</summary>
    private (List<string> Missing, string Stage, Dictionary<string, string> Book) StateTables(TestFolder folder, Func<MsiAppReadout> read = null)
    {
        _h.Services.ImePaths.LogsFolder = folder.Combine("no-ime-logs");
        var stage = folder.Combine("stage");
        Directory.CreateDirectory(stage);
        var run = _h.NewRun("pack0065", "CollectDiag");
        run.CallerSid = CallerSid;
        var package = new TestPackage(run)
        {
            OnReadAppState = () => new AppStateReadout(),
            OnReadRemediations = () => new RemediationState(),
            OnProfileErrors = () => new List<ProfileErrorRow>(),
            OnEspState = () => new List<EspRow>(),
            OnReadMsiApps = read,
        };
        var missing = package.AddStateTables(stage);
        using var book = File.OpenRead(Path.Combine(stage, "IntuneState.xlsx"));
        return (missing, stage, EngineWorkbookTests.Parts(book));
    }

    [Fact]
    public void The_support_package_lists_the_MSI_apps_with_product_code_version_and_status_and_never_a_SID_or_the_URL()
    {
        Msi(DeviceSid, Agent, 60, 0x80070643L, url: "https://contoso.manage.microsoft.com/content/app.msi?token=secret", name: "Contoso Agent");
        _h.Registry.Put(Root + "\\" + DeviceSid + @"\MSI\" + Agent, "ProductVersion", "4.2.1");
        Msi(CallerSid, Viewer, 70, name: "Contoso Viewer");
        Msi(OtherSid, Theirs, 60, 0x80070643L);
        using var folder = new TestFolder();

        var (missing, stage, book) = StateTables(folder);

        Assert.DoesNotContain(missing, m => m.Contains("MSI") || m.Contains("Msi"));
        var csv = File.ReadAllLines(Path.Combine(stage, "MsiApps.csv"));
        Assert.Equal("\"Scope\",\"ProductCode\",\"Name\",\"Version\",\"Status\",\"Verdict\",\"State\",\"LastError\",\"DownloadHost\",\"RetryIndex\",\"RetryCount\",\"LastWrittenUtc\"", csv[0]);
        Assert.Equal(3, csv.Length);
        Assert.Equal("\"device\",\"{AAAAAAAA-1111-2222-3333-444444444444}\",\"Contoso Agent\",\"4.2.1\",\"60\",\"RETRY-HELPS\","
            + "\"the install failed: MSI 1603, fatal error during installation (0x80070643)\",\"0x80070643\",\"contoso.manage.microsoft.com\",\"2\",\"3\",\"2026-11-10T13:00:00Z\"", csv[1]);
        Assert.StartsWith("\"user\",\"{BBBBBBBB-1111-2222-3333-444444444444}\",\"Contoso Viewer\",\"\",\"70\",\"OK\",", csv[2], StringComparison.Ordinal);

        // The tab "MSI apps" comes last, so the earlier tabs keep their place.
        var tab = book["xl/worksheets/sheet6.xml"];
        Assert.Contains("{AAAAAAAA-1111-2222-3333-444444444444}", tab, StringComparison.Ordinal);
        Assert.Contains("3 of 3", tab, StringComparison.Ordinal);
        Assert.Contains("name=\"MSI apps\"", book["xl/workbook.xml"], StringComparison.Ordinal);
        Assert.Contains(_h.Lines, l => l.Contains("MSI line-of-business apps: 1 for the device, 1 for the user; 1 needs attention") && l.Contains("Other users: 1 app record of 1 user, counted only."));

        // Never a SID, the other user's app, or the download address beyond its host.
        var everything = string.Join("\n", csv) + string.Join("\n", book.Values) + string.Join("\n", _h.Lines);
        foreach (var never in new[] { DeviceSid, CallerSid, OtherSid, "CCCCCCCC", "cccccccc", "token=", "/content/" })
        {
            Assert.DoesNotContain(never, everything, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Without_an_MSI_record_the_package_has_no_MSI_file_and_the_tab_says_so()
    {
        using var folder = new TestFolder();

        var (missing, stage, book) = StateTables(folder);

        Assert.False(File.Exists(Path.Combine(stage, "MsiApps.csv")));
        Assert.DoesNotContain(missing, m => m.Contains("MSI"));
        Assert.Contains("No MSI line-of-business apps are recorded on this device.", book["xl/worksheets/sheet6.xml"], StringComparison.Ordinal);
        Assert.Contains(_h.Lines, l => l.Contains("MSI line-of-business apps: none recorded on this device."));
    }

    [Fact]
    public void An_unreadable_MSI_record_is_named_as_missing_and_the_rest_of_the_package_stays()
    {
        using var folder = new TestFolder();

        var (missing, stage, book) = StateTables(folder, () => throw new UnauthorizedAccessException("denied"));

        Assert.Contains("the MSI line-of-business apps", missing);
        Assert.False(File.Exists(Path.Combine(stage, "MsiApps.csv")));
        Assert.Contains("The MSI line-of-business apps could not be read; see the log.", book["xl/worksheets/sheet6.xml"], StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(stage, "Remediations.csv")));
    }

    [Fact]
    public void Anything_but_known_tokens_and_a_host_name_is_dropped_from_a_record()
    {
        var odd = Parse("RETRY-HELPS", "x", kind: "something", next: "format-c", host: "https://contoso.example/path?q=1");
        Assert.Equal("", odd.Kind);
        Assert.Equal("", odd.CodeNext);
        Assert.Equal("", odd.DownloadHost);
        Assert.Equal("App aaaaaaaa", odd.Label);
        Assert.True(AppDecisions.HasPortalPage(new AppFinding { App = new AppEvidence { AppId = "aaaaaaaa-1111-2222-3333-444444444444", Scope = "user" } }));
    }
}
