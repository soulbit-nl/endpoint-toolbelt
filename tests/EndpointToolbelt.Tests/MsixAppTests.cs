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
/// MSIX and Appx line-of-business apps in the app path, read-only. The MDM stack's
/// EnterpriseModernAppManagement AppInstallation nodes through a fake MDM WMI Bridge (the device's and the
/// caller's own; other users are never asked), the AppX deployment codes in words with one step each, the
/// short card facts, IT's line, and the support package's "MSIX apps" tab. No SID anywhere.
/// </summary>
public class MsixAppTests
{
    private const string CallerSid = "S-1-12-1-1111111111-2222222222-3333333333-4444444444";
    private const string Viewer = "Contoso.Viewer_abcdefghjkmnp";
    private const string Notes = "Contoso.Notes_abcdefghjkmnp";
    private const string Agent = "Contoso.Agent_abcdefghjkmnp";
    private static readonly DateTime Now = new(2026, 11, 14, 14, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("MSIX app test zone", TimeSpan.FromHours(1), "MSIX app test zone", "MSIX app test zone");

    private readonly EngineHarness _h = new();

    public MsixAppTests()
    {
        _h.Clock.UtcNow = Now;
    }

    private List<JsonNode> Run(string caller = CallerSid)
    {
        var run = _h.NewRun("diag0068", "DiagnoseApps");
        run.CallerSid = caller;
        Assert.Equal(EngineExitCode.Success, new DiagnoseAppsHandler().Run(run));
        return _h.Records("app-diagnosis");
    }

    // --- Engine: the CSP's status values and the AppX codes -------------------------------------------

    [Theory]
    [InlineData(0, 0L, "IN-PROGRESS", "received, not started yet", false)]
    [InlineData(1, 0L, "IN-PROGRESS", "installing", false)]
    [InlineData(2, 0x80073D02L, "RETRY-HELPS", "the install failed: ERROR_PACKAGES_IN_USE (0x80073D02)", false)]
    [InlineData(2, 0x80073CF3L, "RETRY-HELPS", "the install failed: ERROR_INSTALL_RESOLVE_DEPENDENCY_FAILED (0x80073CF3)", false)]
    [InlineData(2, 0x80073CFBL, "RETRY-HELPS", "the install failed: ERROR_PACKAGE_ALREADY_EXISTS (0x80073CFB)", false)]
    [InlineData(2, 0x800B0109L, "RETRY-HELPS", "the install failed: CERT_E_UNTRUSTEDROOT (0x800B0109)", false)]
    [InlineData(2, 0x80073CF5L, "RETRY-HELPS", "the download failed: ERROR_INSTALL_NETWORK_FAILURE (0x80073CF5)", true)]
    [InlineData(2, 0x80071234L, "RETRY-HELPS", "the install failed: error 0x80071234", false)]
    [InlineData(2, 0x8051100FL, "RETRY-HELPS", "the install failed: OPC_E_ZIP_MISSING_END_OF_CENTRAL_DIRECTORY (0x8051100F)", false)]
    [InlineData(3, 0L, "OK", "installed (the record is cleaned up next)", false)]
    [InlineData(7, 0L, "UNKNOWN", "status 7", false)]
    public void Each_documented_status_has_its_words_and_verdict(int status, long code, string verdict, string state, bool download)
    {
        Assert.Equal((verdict, state, download), MsixApps.Verdict(status, code));
    }

    [Fact]
    public void Without_a_status_the_record_says_so()
    {
        Assert.Equal(("NO-STATE", "no status recorded", false), MsixApps.Verdict(null, 0));
        Assert.Equal(("UNKNOWN", "no status recorded: CERT_E_CHAINING (0x800B010A)", false), MsixApps.Verdict(null, 0x800B010AL));
    }

    [Theory]
    [InlineData(0x80073CF3L, "it")]
    [InlineData(0x800B0109L, "it")]
    [InlineData(0x800B0100L, "it")]
    [InlineData(0x80073CFFL, "it")]
    [InlineData(0x80073CFBL, "it")]
    [InlineData(0x80073D02L, "wait")]
    [InlineData(0x80073D19L, "wait")]
    [InlineData(0x80073CF5L, "connectivity")]
    [InlineData(0x80073CF4L, "none")]
    [InlineData(0x80079999L, "it")]
    [InlineData(0x8051100FL, "it")]
    public void Each_AppX_code_leads_to_one_step(long code, string next)
    {
        Assert.Equal(next, MsixApps.Code(code).Next);
    }

    [Fact]
    public void The_device_and_the_callers_own_installs_are_read_and_no_other_user_is_asked()
    {
        _h.ModernApps.Add(null, Viewer, 2, 0x80073CF3L);
        _h.ModernApps.Add(CallerSid, Notes, 2, 0x80073D02L);
        _h.ModernApps.Add("S-1-12-1-5555555555-6666666666-7777777777-8888888888", Agent, 2, 0x80073CF9L);
        _h.ModernApps.Versions[CallerSid + "|" + Notes] = "2.0.1.0";

        var apps = Run().Where(a => a.Str("kind") == "msix").ToList();

        Assert.Equal(new[] { "device", CallerSid }, _h.ModernApps.Asked);
        Assert.Equal(2, apps.Count);
        var viewer = apps.Single(a => a.Str("app") == Viewer);
        Assert.Equal("device", viewer.Str("scope"));
        Assert.Equal("RETRY-HELPS", viewer.Str("verdict"));
        Assert.Equal("0x80073CF3", viewer.Str("code"));
        Assert.Equal("it", viewer.Str("codenext"));
        Assert.StartsWith("ERROR_INSTALL_RESOLVE_DEPENDENCY_FAILED: the package failed update, dependency or conflict validation", viewer.Str("codemeaning"), StringComparison.Ordinal);
        Assert.Equal("2026-11-14T14:00:00Z", viewer.Str("observed"));
        var notes = apps.Single(a => a.Str("app") == Notes);
        Assert.Equal("user", notes.Str("scope"));
        Assert.Equal("2.0.1.0", notes.Str("version"));
        Assert.Equal("wait", notes.Str("codenext"));

        var close = Assert.Single(_h.Records("apps-diagnosis"));
        Assert.Equal("2", close.Str("msix"));
        Assert.Empty(_h.Records("change"));
        var everything = string.Join("\n", _h.Lines);
        Assert.DoesNotContain(CallerSid, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("5555555555", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Contoso.Agent", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void A_service_or_well_known_caller_reads_the_device_only()
    {
        _h.ModernApps.Add(null, Viewer, 1);
        Run(caller: "S-1-5-18");
        Assert.Equal(new[] { "device" }, _h.ModernApps.Asked);
    }

    [Fact]
    public void A_node_that_is_not_a_package_family_name_is_skipped_and_a_version_that_is_not_a_version_is_dropped()
    {
        _h.ModernApps.Add(null, "not a family name", 2, 0x80073CF3L);
        _h.ModernApps.Add(null, Viewer, 2, 0x80073CF3L);
        _h.ModernApps.Versions["device|" + Viewer] = "1.0; not a version";

        var apps = Run().Where(a => a.Str("kind") == "msix").ToList();

        Assert.Equal(Viewer, Assert.Single(apps).Str("app"));
        Assert.Equal("", apps[0].Str("version"));
    }

    [Fact]
    public void An_unreadable_bridge_leaves_the_rest_of_the_diagnosis()
    {
        _h.ModernApps.DeviceFails = new InvalidOperationException("denied");
        var apps = Run();
        Assert.DoesNotContain(apps, a => a.Str("kind") == "msix");
        Assert.Contains(_h.Lines, l => l.Contains("The MSIX line-of-business apps could not be read"));
    }

    [Fact]
    public void Only_MSIX_installs_close_the_list_with_their_count()
    {
        _h.ModernApps.Add(null, Viewer, 2, 0x800B0109L);
        Run();
        var finding = _h.Records("finding").Last();
        Assert.Equal("DIAG-APPS-ATTENTION", finding.Str("id"));
        Assert.Contains("1 MSIX line-of-business app record", finding.Str("detail"), StringComparison.Ordinal);
    }

    // --- App: how the list words them ----------------------------------------------------------------

    private static AppEvidence Parse(string verdict, string state, string code = "", string meaning = "", string step = "", string support = "",
        string next = "", string scope = "device", string version = "", bool download = false)
    {
        var json = "{\"type\":\"app-diagnosis\",\"run\":\"diag0068\",\"diagnosis\":\"diag0068\",\"app\":\"" + Viewer + "\",\"name\":\"\",\"scope\":\"" + scope
            + "\",\"intent\":\"\",\"revision\":null,\"verdict\":\"" + verdict + "\",\"state\":\"" + state + "\",\"code\":\"" + code + "\",\"observed\":\"2026-11-14T13:00:00Z\",\"download\":" + (download ? "true" : "false")
            + ",\"shared\":false,\"kind\":\"msix\",\"codemeaning\":\"" + meaning + "\",\"codestep\":\"" + step + "\",\"codesupport\":\"" + support + "\",\"codenext\":\"" + next
            + "\",\"msistatus\":null,\"version\":\"" + version + "\",\"downloadhost\":\"\",\"retryindex\":null,\"retrycount\":null}";
        return EngineRecord.Parse(json).App;
    }

    private static AppFinding Decide(AppEvidence app) => AppDecisions.Decide(app, new[] { app }, Now, Zone);

    private static AppEvidence Failed(long code, string scope = "device", string version = "")
    {
        var entry = MsixApps.Code(code);
        var (verdict, state, download) = MsixApps.Verdict(2, code);
        return Parse(verdict, state, MsixApps.Hex(code), entry.Meaning.Length > 0 ? entry.Name + ": " + entry.Meaning : "", entry.Step, entry.Support, entry.Next, scope, version, download);
    }

    [Theory]
    [InlineData(0x80073CF3L)]
    [InlineData(0x800B0109L)]
    [InlineData(0x800B010AL)]
    [InlineData(0x80073CFFL)]
    public void A_signature_dependency_or_policy_failure_hands_over_to_IT_with_its_record(long code)
    {
        var f = Decide(Failed(code));
        Assert.Equal(AppCase.InstallFailed, f.Case);
        Assert.Equal(AppNextStep.CopyForIt, f.Next);
        Assert.Equal("MSIX app Contoso.Viewer: the install failed", f.Title);
        Assert.Contains(MsixApps.Hex(code), f.Text, StringComparison.Ordinal);
        Assert.Equal("MSIX record: " + MsixApps.Verdict(2, code).State + ".", f.Record);
        // IT's line: the support step, the record with its code, then the package family name.
        var it = f.Card().Support;
        Assert.Contains("MSIX record: the install failed: ", it, StringComparison.Ordinal);
        Assert.EndsWith("MSIX package family " + Viewer + " (device assignment).", it, StringComparison.Ordinal);
        Assert.DoesNotContain("App ID", it, StringComparison.Ordinal);
        Assert.False(AppDecisions.HasPortalPage(f));
        Assert.DoesNotContain("Retry work apps and settings", f.Text + f.NextText, StringComparison.Ordinal);
    }

    [Fact]
    public void An_app_in_use_asks_to_close_it_and_wait_and_a_download_failure_offers_the_connection_check()
    {
        var inUse = Decide(Failed(0x80073D02L));
        Assert.Equal(AppNextStep.Wait, inUse.Next);
        Assert.Equal("Close the app if it is open, then check the apps again later.", inUse.NextText);
        Assert.Equal("MSIX app Contoso.Viewer: the install could not finish", inUse.Title);
        Assert.Equal("", inUse.Record);

        var download = Decide(Failed(0x80073CF5L));
        Assert.Equal(AppCase.DownloadError, download.Case);
        Assert.Equal(AppNextStep.ConnectivityTest, download.Next);
        Assert.Equal("", download.Record);

        var full = Decide(Failed(0x80073CF4L));
        Assert.Equal(AppNextStep.None, full.Next);
        Assert.Equal("Free up space on the system drive, then check the apps again later.", full.NextText);
    }

    [Fact]
    public void An_install_in_progress_waits_and_an_unknown_state_goes_to_IT()
    {
        var installing = Decide(Parse("IN-PROGRESS", "installing"));
        Assert.Equal(AppCase.InProgress, installing.Case);
        Assert.Equal(AppNextStep.Wait, installing.Next);

        var unknown = Decide(Parse("UNKNOWN", "status 7"));
        Assert.Equal(AppNextStep.CopyForIt, unknown.Next);
        Assert.Equal("MSIX record: status 7.", unknown.Record);
        Assert.NotEmpty(unknown.Uncertainty);

        var installed = Decide(Parse("OK", "installed (the record is cleaned up next)"));
        Assert.Equal(AppObservation.Detected, installed.Observation);
    }

    [Fact]
    public void An_MSIX_apps_card_facts_name_its_type_package_family_and_installed_version()
    {
        var f = Decide(Failed(0x80073CF3L, scope: "user", version: "2.0.1.0"));

        var facts = AppDecisions.DetailState(f, Zone).Split('\n');
        Assert.StartsWith("Status: Failed, recorded ", facts[0], StringComparison.Ordinal);
        Assert.Equal(new[] { "Assigned to: you", "Type: MSIX line-of-business app", "Package family: " + Viewer, "Version installed: 2.0.1.0", "Name: not known on this device" }, facts.Skip(1));
        Assert.Equal("MSIX package family " + Viewer + ", version 2.0.1.0 installed (user assignment).", f.Identity);
        Assert.Equal("MSIX app, the one assigned to you, version 2.0.1.0 installed, package family " + Viewer, AppDecisions.Identify(f.App));
        Assert.Equal("MSIX, assigned to you. Copy details for IT and send it to your IT support.", new EndpointToolbelt.App.Ui.AppRowView(f, false).Summary);

        // Without an installed version: no version line.
        var none = Decide(Failed(0x80073CF3L));
        Assert.DoesNotContain(AppDecisions.DetailFacts(none, Zone), x => x.Label.StartsWith("Version", StringComparison.Ordinal));
    }

    [Fact]
    public void Retry_apps_and_policies_never_retries_an_MSIX_app()
    {
        var app = Failed(0x80073CF3L);
        var outcome = new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "diag0068", Result = ResultCode.Completed, ExitCode = 0 };
        outcome.Records.Add(new EngineRecord { Type = "app-diagnosis", Run = "diag0068", App = app });
        outcome.Records.Add(EngineRecord.Parse("{\"type\":\"apps-diagnosis\",\"run\":\"diag0068\",\"diagnosis\":\"diag0068\",\"present\":true,\"records\":0,\"listed\":0,\"scopes\":1,\"otherscopes\":0,\"userscope\":true,\"logevidence\":false,\"msi\":0,\"msix\":1}"));
        var diagnosis = AppDecisions.From(outcome, Now, Zone);
        Assert.Empty(PostCheck.Retried(diagnosis));
    }

    [Fact]
    public void An_unknown_kind_stays_a_Win32_app_and_msix_is_kept()
    {
        Assert.Equal("msix", Parse("RETRY-HELPS", "x").Kind);
    }

    // --- Engine: the support package -----------------------------------------------------------------

    private (List<string> Missing, string Stage, Dictionary<string, string> Book) StateTables(TestFolder folder)
    {
        _h.Services.ImePaths.LogsFolder = folder.Combine("no-ime-logs");
        var stage = folder.Combine("stage");
        Directory.CreateDirectory(stage);
        var run = _h.NewRun("pack0068", "CollectDiag");
        run.CallerSid = CallerSid;
        var package = new TestPackage(run)
        {
            OnReadAppState = () => new AppStateReadout(),
            OnReadRemediations = () => new RemediationState(),
            OnProfileErrors = () => new List<ProfileErrorRow>(),
            OnEspState = () => new List<EspRow>(),
        };
        var missing = package.AddStateTables(stage);
        using var book = File.OpenRead(Path.Combine(stage, "IntuneState.xlsx"));
        return (missing, stage, EngineWorkbookTests.Parts(book));
    }

    [Fact]
    public void The_support_package_lists_the_MSIX_installs_in_a_tab_of_their_own_and_never_a_SID()
    {
        _h.ModernApps.Add(null, Viewer, 2, 0x800B0109L);
        _h.ModernApps.Add(CallerSid, Notes, 1);
        _h.ModernApps.Versions[CallerSid + "|" + Notes] = "2.0.1.0";
        using var folder = new TestFolder();

        var (missing, stage, book) = StateTables(folder);

        Assert.DoesNotContain(missing, m => m.Contains("MSIX"));
        var csv = File.ReadAllLines(Path.Combine(stage, "MsixApps.csv"));
        Assert.Equal("\"Scope\",\"PackageFamilyName\",\"VersionInstalled\",\"Status\",\"Verdict\",\"State\",\"LastError\"", csv[0]);
        Assert.Equal(3, csv.Length);
        Assert.Contains("\"device\",\"" + Viewer + "\",\"\",\"2\",\"RETRY-HELPS\",\"the install failed: CERT_E_UNTRUSTEDROOT (0x800B0109)\",\"0x800B0109\"", csv);
        Assert.Contains("\"user\",\"" + Notes + "\",\"2.0.1.0\",\"1\",\"IN-PROGRESS\",\"installing\",\"\"", csv);

        // "MSIX apps" is the seventh tab, after "MSI apps", so the earlier tabs keep their place.
        var workbook = book["xl/workbook.xml"];
        Assert.True(workbook.IndexOf("name=\"MSI apps\"", StringComparison.Ordinal) < workbook.IndexOf("name=\"MSIX apps\"", StringComparison.Ordinal));
        Assert.Contains(Viewer, book["xl/worksheets/sheet7.xml"], StringComparison.Ordinal);
        Assert.Contains(_h.Lines, l => l.Contains("MSIX line-of-business app installs: 1 for the device, 1 for the user; 1 needs attention") && l.Contains("Other users' are not read."));
        Assert.Contains(_h.Lines, l => l.Contains("tabs: Apps, Remediations, IME log summary, Configuration profiles, Enrollment Status Page, MSI apps, MSIX apps"));

        var everything = string.Join("\n", csv) + string.Join("\n", book.Values) + string.Join("\n", _h.Lines);
        Assert.DoesNotContain(CallerSid, everything, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_an_MSIX_install_the_package_has_no_MSIX_file_and_the_tab_says_so()
    {
        using var folder = new TestFolder();

        var (missing, stage, book) = StateTables(folder);

        Assert.False(File.Exists(Path.Combine(stage, "MsixApps.csv")));
        Assert.DoesNotContain(missing, m => m.Contains("MSIX"));
        Assert.Contains("No MSIX line-of-business app install is pending or failed", book["xl/worksheets/sheet7.xml"], StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_bridge_is_named_as_missing_and_the_rest_of_the_package_stays()
    {
        _h.ModernApps.DeviceFails = new InvalidOperationException("denied");
        using var folder = new TestFolder();

        var (missing, stage, book) = StateTables(folder);

        Assert.Contains("the MSIX line-of-business app installs", missing);
        Assert.False(File.Exists(Path.Combine(stage, "MsixApps.csv")));
        Assert.Contains("The MSIX line-of-business app installs could not be read; see the log.", book["xl/worksheets/sheet7.xml"], StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(stage, "Remediations.csv")));
    }
}
