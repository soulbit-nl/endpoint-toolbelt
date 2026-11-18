using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// Home and the For IT landing fit the window's default size without a vertical scroll bar in
/// their normal states, and the details drawer is closed by default at every size.
/// </summary>
public class CompactLayoutTests
{
    /// <summary>The ten health rows' detailed checks, all OK; <paramref name="warn"/> names one that needs checking.</summary>
    internal static List<HealthResult> Health(string warn = null, string fail = null)
    {
        HealthResult R(string name, string detail) => new()
        {
            Name = name,
            Status = name == warn ? HealthStatus.Warn : name == fail ? HealthStatus.Fail : HealthStatus.Pass,
            Detail = detail,
            Hint = HealthChecks.Hint(name, name != warn && name != fail),
        };
        return new List<HealthResult>
        {
            R(HealthChecks.ImeService, "Running"),
            R(HealthChecks.Enrollment, "Enrolled in Intune"),
            R(HealthChecks.LastSync, "2026-11-12 15:34 (48 min ago)"),
            R(HealthChecks.ImeCheckIn, "2026-11-12 15:34 (48 min ago). Last attempt failed: no error code in the log, 2026-11-12 16:22"),
            R(HealthChecks.EntraJoined, "Entra joined to Contoso"),
            R(HealthChecks.Prt, "Present"),
            R(HealthChecks.WindowsHello, "Set up for you"),
            R(HealthChecks.MdmCertificate, "Expires 2027-10-01 (323 days)"),
            R(HealthChecks.Connectivity, "manage.microsoft.com reachable"),
            R(HealthChecks.FreeSpace, "120.5 GB free on C:"),
            R(HealthChecks.PendingRestart, "No restart pending"),
            R(HealthChecks.Profiles, "No errors"),
            R(HealthChecks.Clock, "In sync"),
            R(HealthChecks.Defender, "Up to date"),
        };
    }

    /// <summary>Five runs in the history, as a device that was used a while has them (For IT shows the newest five).</summary>
    private static void FiveRuns(MainWindow window)
    {
        var folder = window.RunsFolderOverride;
        var start = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        var actions = new[] { "SyncClock", "ClearTeamsCache", "ConnTest", "DiagnoseApps", "RetryAppsAndPolicies" };
        for (var i = 0; i < actions.Length; i++)
        {
            var name = RunLogStore.TimePart(start.AddMinutes(i * 7)) + "_" + actions[i] + "_ab12cd3" + i + ".log";
            MainWindowPagesTests.WriteRun(Path.Combine(folder, name), true, "[OK] Done", "    It worked.");
        }
    }

    /// <summary>The page area's content height and its viewport, laid out in a window of this client size.</summary>
    internal static (double Extent, double Viewport) Fit(MainWindow window, double width, double height, double scale = 1.0)
    {
        var content = (FrameworkElement)window.Content;
        // The display scale (100, 125, 150 %), set on the root of the visual tree: layout rounding to
        // device pixels then adds up differently.
        System.Windows.Media.Visual root = content;
        while (System.Windows.Media.VisualTreeHelper.GetParent(root) is System.Windows.Media.Visual parent) { root = parent; }
        System.Windows.Media.VisualTreeHelper.SetRootDpi(root, new System.Windows.DpiScale(scale, scale));
        content.InvalidateMeasure();
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var scroll = MainWindowPagesTests.Get<ScrollViewer>(window, "MainScroll");
        return (scroll.ExtentHeight, scroll.ViewportHeight);
    }

    /// <summary>The client height a page must spare above 100 % scale.</summary>
    internal const double ScaleMargin = 12;

    /// <summary>The page fits the default size at 100, 125 and 150 % display scale.</summary>
    internal static void AssertFits(MainWindow window, string what)
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5 })
        {
            // A margin above 100 %: the title bar and the borders round differently there (a virtual
            // desktop at 125 % showed a scroll bar on Home at the default size).
            var (extent, viewport) = Fit(window, WindowLayout.DefaultClientWidth, WindowLayout.CompactClientHeight - (scale > 1 ? ScaleMargin : 0), scale);
            Assert.True(extent <= viewport + 0.5, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0} at {3:0} %: the page needs {1:0.#} px but has {2:0.#} px at the default size: it would scroll.", what, extent, viewport, scale * 100));
        }
        Fit(window, WindowLayout.DefaultClientWidth, WindowLayout.CompactClientHeight);
    }

    [Fact]
    public void The_app_page_with_six_apps_that_need_attention_fits_and_its_action_bar_is_one_row()
    {
        // On a device at 150 %, with the actions below the list, Reload fell below the window.
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, Health(), new DsregInfo(), false);
            var now = DateTime.UtcNow;
            var names = new[] { "Uninstall Contoso Agent versions", "App 3de8eab0", "Contoso Vault", "Contoso Inventor Base Tools 2.0.1.1 (Version 2024)", "Contoso Drive Mappings", "Update for Contoso Remote Manager 2026.3.18.0", "Fabrikam Notes" };
            var records = names.Select((name, i) => new EngineRecord
            {
                Type = "app-diagnosis",
                Utc = now,
                App = new AppEvidence
                {
                    DiagnosisId = "diag0004", AppId = (i + 1).ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + "-2222-3333-4444-555555555555", Name = name,
                    Scope = "device", Intent = "required", Revision = 1, Verdict = i < 6 ? "PACKAGING" : "OK", Code = i < 6 ? "0x87D1041C" : "",
                    ObservedText = now.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                },
            }).ToList();
            records.Add(new EngineRecord { Type = "apps-diagnosis", Closing = new DiagnosisClose { Present = true, Records = names.Length, Listed = names.Length, UserScope = true }, Utc = now });
            records.Add(new EngineRecord { Type = "summary", Exit = 0, Utc = now });
            var outcome = new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "diag0004", Result = ResultCode.Completed, ExitCode = 0 }.With(records);
            MainWindowPagesTests.Call(window, "SetDiagnosis", AppDecisions.From(outcome, now), outcome);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Apps"), false);
            MainWindowPagesTests.Call(window, "ShowAppChecks");
            MainWindowPagesTests.Call(window, "StartCheck", "App diagnosis");
            MainWindowPagesTests.Call(window, "SetStatus", "App diagnosis: 6 apps need attention.", Tone.Warning, true, true);

            // The checks are one status line while they are OK; the action bar sits above the list.
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<Border>(window, "AppChecksBox").Visibility);
            Assert.Equal("Free disk space OK · Restart OK", MainWindowPagesTests.Get<Button>(window, "AppChecksLink").Content);
            AssertFits(window, "The app page with six apps that need attention");
            foreach (var scale in new[] { 1.0, 1.25, 1.5 })
            {
                Fit(window, WindowLayout.DefaultClientWidth, WindowLayout.CompactClientHeight, scale);
                var retry = MainWindowPagesTests.Get<Button>(window, "RetryButton");
                var reload = MainWindowPagesTests.Get<Button>(window, "ReloadAppsButton");
                var top = MainWindowPagesTests.Get<StackPanel>(window, "AppsListPanel");
                var page = MainWindowPagesTests.Get<Grid>(window, "AppsPage");
                var retryY = retry.TranslatePoint(new Point(0, 0), page).Y;
                Assert.True(Math.Abs(retryY - reload.TranslatePoint(new Point(0, 0), page).Y) < 1, "Retry and Reload are one row at " + (scale * 100) + " %");
                Assert.True(retryY < top.TranslatePoint(new Point(0, 0), page).Y, "the action bar is above the list at " + (scale * 100) + " %: " + retryY + " vs " + top.TranslatePoint(new Point(0, 0), page).Y);
            }
            Fit(window, WindowLayout.DefaultClientWidth, WindowLayout.CompactClientHeight);
            // A click on the status line opens the checks' rows.
            MainWindowPagesTests.Call(window, "OnAppChecksLink", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<Border>(window, "AppChecksBox").Visibility);
        });
    }

    [Fact]
    public void Home_and_the_For_IT_landing_fit_the_default_size_without_scrolling()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            FiveRuns(window);
            // All OK.
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, Health(), new DsregInfo(), false);
            AssertFits(window, "Home, all OK");
            // One check needs attention (a real case: the app installer).
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, Health(warn: HealthChecks.ImeCheckIn), new DsregInfo(), false);
            AssertFits(window, "Home, one check");
            // Two: one to check and one problem.
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, Health(warn: HealthChecks.Clock, fail: HealthChecks.FreeSpace), new DsregInfo(), false);
            AssertFits(window, "Home, two checks");

            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("It"), false);
            AssertFits(window, "For IT, five recent runs");
        });
    }

    [Fact]
    public void Fix37_the_For_IT_landing_fits_with_every_tile_saying_its_longest_outcome()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = NewWindowWithLongTileOutcomes();
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("It"), false);
            Assert.Equal("7 tools · last run not confirmed", MainWindowPagesTests.Get<TextBlock>(window, "SignInTileSummary").Text);
            Assert.Equal("4 tools · last run skipped: cooldown active", MainWindowPagesTests.Get<TextBlock>(window, "NetworkTileSummary").Text);
            Assert.Equal("4 tools · last run blocked: restart pending", MainWindowPagesTests.Get<TextBlock>(window, "AppsTileSummary").Text);
            Assert.Equal("7 tools · last run nothing to repair", MainWindowPagesTests.Get<TextBlock>(window, "WindowsTileSummary").Text);
            AssertFits(window, "For IT, every tile with a long outcome");
        });
    }

    /// <summary>
    /// A run history whose newest run per group only ended, each with a long outcome in words:
    /// a safe-mode start answered Not confirmed, and engine runs that skipped, were blocked or had
    /// nothing to repair.
    /// </summary>
    private static MainWindow NewWindowWithLongTileOutcomes()
    {
        var window = MainWindowPagesTests.NewWindow();
        var folder = window.RunsFolderOverride;
        Directory.CreateDirectory(folder);
        var start = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
        var runs = new[]
        {
            ("StartWordinsafemode", "local-0a1b2c3d", "Outcome   : Not confirmed"),
            ("FreeDiskSpace", "ab12cd31", "Exit code : 69001"),
            ("FullReset", "ab12cd32", "Exit code : 69002"),
            ("SyncClock", "ab12cd33", "Exit code : 69003"),
        };
        for (var i = 0; i < runs.Length; i++)
        {
            var (action, id, line) = runs[i];
            var name = RunLogStore.TimePart(start.AddMinutes(i * 7)) + "_" + action + "_" + id + ".log";
            File.WriteAllLines(Path.Combine(folder, name), new[]
            {
                "===== ENDPOINT TOOLBELT 1.1.1 - RUN LOG =====", "Action    : test", "Run ID    : " + id, line,
                "Started   : 2026-11-14 11:00", "Ended     : 2026-11-14 11:01", "",
                "----- RESULT -----", "[INFO] Nothing was changed", "",
                "----- TECHNICAL LOG (times in UTC+01:00) -----",
            });
        }
        return window;
    }

    [Fact]
    public void Choose_a_problem_and_the_window_page_fit_the_default_size()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            AssertFits(window, "Choose a problem with its compact window row");
            var api = new WindowRescueTests.FakeWindows();
            for (var i = 1; i <= 4; i++) { api.Add(30 + i, "Contoso Editor", "Report " + i, ScreenRect.FromSize(-6000, -6000, 800, 600)); }
            window.WindowApi = api;
            MainWindowPagesTests.Call(window, "OnProblemWindow", window, new RoutedEventArgs());
            AssertFits(window, "the window page with four windows");
        });
    }

    [Fact]
    public void Every_problem_path_the_Office_path_and_the_adapter_page_fit_the_default_size()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            // The worst case: every supported Office app installed (all seven rows, four tools on the Office path), an adapter without an address.
            window.OfficeApps = OfficeSafeModeTests.FakeOffice.All();
            var network = new FakeNetwork();
            network.Adapters.Add(IpRenewalTests.Adapter(12, selfAssigned: true));
            network.Adapters.Add(IpRenewalTests.Adapter(5, kind: EndpointToolbelt.Platform.AdapterKind.WiFi, usable: true));
            network.Adapters.Add(IpRenewalTests.Adapter(20, dhcp: false, usable: true));
            window.Adapters = network;
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, Health(warn: HealthChecks.Clock), new DsregInfo(), false);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            AssertFits(window, "Choose a problem with the window and the Office rows");
            foreach (var problem in new[] { Problem.CantSignIn, Problem.WindowsWontUpdate, Problem.CantConnect, Problem.OfficeCloses, Problem.OneDriveSync, Problem.DeviceBlocked })
            {
                MainWindowPagesTests.Call(window, "ShowProblem", problem, false);
                AssertFits(window, ProblemPaths.Get(problem).Title);
            }
            MainWindowPagesTests.Call(window, "OnRenewIpTool", window, new RoutedEventArgs());
            AssertFits(window, "the adapter page with three adapters");
            MainWindowPagesTests.Call(window, "OnAdapterChosen", new Button { DataContext = ((IEnumerable<AdapterRowView>)MainWindowPagesTests.Get<ItemsControl>(window, "AdapterList").ItemsSource).Last() }, new RoutedEventArgs());
            AssertFits(window, "the adapter page with a fixed address chosen");
            // The sign-in path with a result and the safe-mode question below it.
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.OfficeCloses, false);
            MainWindowPagesTests.Call(window, "ClearResult");
            MainWindowPagesTests.Call(window, "StartCheck", OfficeSafeMode.ToolName(OfficeApp.Word));
            MainWindowPagesTests.Call(window, "ShowSafeModeOutcome", OfficeSafeMode.Start(OfficeSafeModeTests.FakeOffice.All(), OfficeApp.Word, null, asks: true));
            AssertFits(window, "the Office path with the safe-mode question");
            // The app path with its two install blockers above the (empty) list.
            MainWindowPagesTests.Call(window, "ClearResult");
            MainWindowPagesTests.Call(window, "ShowAppChecks");
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Apps"), false);
            AssertFits(window, "An app won't install with the install blockers");
        });
    }

    /// <summary>
    /// The Sandbox's Home at 150 %: not enrolled, two problems and two to check, a two-line support contact.
    /// The Sandbox is not joined (the work account row a calm Check, no sign-in token expected) and a
    /// virtual machine (Windows Hello Info), as the rows say there.
    /// </summary>
    internal static List<HealthResult> SandboxHealth()
    {
        var health = Health();
        foreach (var r in health)
        {
            if (r.Name is HealthChecks.Enrollment or HealthChecks.ImeService) { r.Status = HealthStatus.Fail; r.Hint = HealthChecks.Hint(r.Name, false); }
            if (r.Name == HealthChecks.Defender) { r.Status = HealthStatus.Warn; r.Hint = HealthChecks.Hint(r.Name, false); }
        }
        var notJoined = DsregInfo.Parse(new[] { "AzureAdJoined : NO", "DomainJoined : NO", "WorkplaceJoined : NO" });
        health[health.FindIndex(c => c.Name == HealthChecks.EntraJoined)] = WorkAccountRows.Join(notJoined);
        health[health.FindIndex(c => c.Name == HealthChecks.Prt)] = WorkAccountRows.Prt(notJoined);
        health[health.FindIndex(c => c.Name == HealthChecks.WindowsHello)] = WorkAccountRows.Hello(notJoined, new Protocol.MachineReadout { VirtualMachine = true }, null);
        return health;
    }

    [Fact]
    public void Home_fits_in_its_worst_real_state_with_a_problem_card_four_rows_and_a_support_contact()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var status = new Protocol.StatusInfo { SupportText = "Contoso IT Service Desk - ext. 5555", SupportUrl = "https://support.contoso.example/" };
            MainWindowPagesTests.Invoke(window, "ApplyHealth", status, false, SandboxHealth(), new DsregInfo(), false);
            // The status is a problem, four rows need attention; Home shows their count only.
            Assert.Equal("PROBLEM", MainWindowPagesTests.Get<TextBlock>(window, "SeverityWord").Text);
            var rows = ((IEnumerable<HealthRow>)MainWindowPagesTests.Get<ItemsControl>(window, "HealthList").ItemsSource).ToList();
            Assert.Empty(rows);
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<ItemsControl>(window, "HealthList").Visibility);
            Assert.Equal("Show all 10", MainWindowPagesTests.Get<Button>(window, "HealthToggle").Content);
            Assert.Equal("2 problems, 2 warnings, 6 OK.", MainWindowPagesTests.Get<TextBlock>(window, "HealthVerdict").Text);
            AssertFits(window, "Home in the Sandbox's state with a support contact");
            // Show all is the user's choice: then the page may scroll; Show less brings the count back.
            MainWindowPagesTests.Call(window, "OnHealthToggle", window, new RoutedEventArgs());
            Assert.Equal(10, ((IEnumerable<HealthRow>)MainWindowPagesTests.Get<ItemsControl>(window, "HealthList").ItemsSource).Count());
            MainWindowPagesTests.Call(window, "OnHealthToggle", window, new RoutedEventArgs());
            Assert.Empty((IEnumerable<HealthRow>)MainWindowPagesTests.Get<ItemsControl>(window, "HealthList").ItemsSource);
        });
    }

    /// <summary>
    /// fix/33: the edge phases' states of Home (the harness's restart marker, no network, a proxy that
    /// refuses), each with the Sandbox's problems, a restart waiting, Defender to check and a support
    /// contact on two lines: Home shows its plain status and the health as a count, and fits.
    /// </summary>
    [Theory]
    [InlineData("restart-pending", null)]
    [InlineData("offline", HealthChecks.Connectivity)]
    [InlineData("proxy refuses", HealthChecks.Connectivity)]
    [InlineData("restart the main finding", HealthChecks.PendingRestart)]
    [InlineData("every row a problem", "all")]
    public void Fix33_Home_fits_in_the_edge_states_with_a_restart_waiting_and_a_support_contact(string state, string alsoFailing)
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            window.BootClock = () => DateTime.UtcNow.AddMinutes(-12);
            var health = SandboxHealth();
            foreach (var r in health)
            {
                if (r.Name == HealthChecks.PendingRestart) { r.Status = HealthStatus.Warn; r.Detail = "A restart is pending"; r.Hint = HealthChecks.Hint(r.Name, false); }
                if (r.Name == alsoFailing || (alsoFailing == "all" && r.Name != HealthChecks.PendingRestart))
                {
                    r.Status = HealthStatus.Fail;
                    r.Hint = HealthChecks.Hint(r.Name, false);
                }
            }
            if (alsoFailing == HealthChecks.PendingRestart)
            {
                foreach (var r in health.Where(h => h.Status == HealthStatus.Fail && h.Name != HealthChecks.PendingRestart)) { r.Status = HealthStatus.Pass; }
                health.Single(h => h.Name == HealthChecks.PendingRestart).Status = HealthStatus.Fail;
            }
            var status = new Protocol.StatusInfo { SupportText = "Contoso IT Service Desk - ext. 5555", SupportUrl = "https://support.contoso.example/" };
            MainWindowPagesTests.Invoke(window, "ApplyHealth", status, false, health, new DsregInfo(), false);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false);
            Assert.EndsWith(" · a restart is waiting", MainWindowPagesTests.Get<TextBlock>(window, "UptimeText").Text);
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<ItemsControl>(window, "HealthList").Visibility);
            Assert.False(HomeSummary.HasTechnicalWords(MainWindowPagesTests.Get<TextBlock>(window, "MainFindingTitle").Text), state);
            AssertFits(window, "Home, " + state + ": " + MainWindowPagesTests.Get<TextBlock>(window, "MainFindingTitle").Text);
        });
    }

    [Fact]
    public void Home_shows_no_row_before_Show_all_and_a_cap_shows_problems_first()
    {
        var rows = HealthView.Rows(SandboxHealth());
        var shown = HealthView.Visible(rows, showAll: false, HealthView.HomeAttentionRows);
        // None on Home before Show all (a cap of none); the cap itself still puts problems first.
        Assert.Empty(shown);
        Assert.Equal(4, HealthView.Hidden(rows, HealthView.HomeAttentionRows));
        shown = HealthView.Visible(rows, showAll: false, 2);
        Assert.Equal(2, shown.Count);
        Assert.All(shown, r => Assert.Equal(HealthStatus.Fail, r.Status));
        Assert.Equal(2, HealthView.Hidden(rows, 2));
        Assert.Equal(4, HealthView.Visible(rows, showAll: false).Count);
        // A warning comes before nothing, but after the problems.
        var oneEach = HealthView.Rows(Health(warn: HealthChecks.Clock, fail: HealthChecks.FreeSpace));
        Assert.Equal(new[] { HealthChecks.FreeSpace, HealthChecks.Clock }, HealthView.Visible(oneEach, false, 2).Select(r => r.Name));
        Assert.Empty(HealthView.Visible(oneEach, false, HealthView.HomeAttentionRows));
        // Every check needs attention and some are hidden: the link still shows them all.
        var all = Health();
        foreach (var r in all) { r.Status = HealthStatus.Fail; }
        var allRows = HealthView.Rows(all);
        Assert.Equal("Show all " + allRows.Count, HealthView.ToggleText(allRows, false, HealthView.HomeAttentionRows));
        Assert.Equal("Show all " + allRows.Count + " health checks", HealthView.ToggleName(allRows, false, HealthView.HomeAttentionRows));
        Assert.Equal("", HealthView.ToggleText(allRows, false));
    }

    [Fact]
    public void A_health_row_is_one_line_until_it_is_opened()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, Health(warn: HealthChecks.ImeCheckIn), new DsregInfo(), false);
            // Home shows the count until Show all.
            MainWindowPagesTests.Call(window, "OnHealthToggle", window, new RoutedEventArgs());
            var list = MainWindowPagesTests.Get<ItemsControl>(window, "HealthList");
            var row = Assert.Single((IEnumerable<HealthRow>)list.ItemsSource, r => r.Name == "Work app installer");
            Assert.Equal("Work app installer", row.Name);
            Assert.StartsWith("App installer (IME): ", row.Detail);
            Assert.Equal("WARNING", row.BadgeWord);
            Assert.False(row.IsOpen);
            Assert.Equal(Visibility.Collapsed, row.DetailsVisibility);
            Assert.Contains("Last attempt failed", row.Detail);
            row.Toggle();
            Assert.True(row.IsOpen);
            Assert.Equal(Visibility.Visible, row.DetailsVisibility);
            Assert.Null(window.FindName("HealthPointHint"));
        });
    }

    [Theory]
    [InlineData(WindowLayout.DefaultClientWidth, false)]
    [InlineData(1000, false)]
    [InlineData(1900, true)]
    public void The_details_drawer_is_closed_by_default_at_every_size(double width, bool maximised)
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            if (maximised) { window.WindowState = WindowState.Maximized; }
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, Health(), new DsregInfo(), false);
            Fit(window, width, 1000);
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<Border>(window, "Drawer").Visibility);
            Assert.False((bool)MainWindowPagesTests.Field(window, "_drawerOpen"));
            // A tool's result keeps it closed: the result word and one line show, the rest is behind View details.
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("It"), false);
            MainWindowPagesTests.Call(window, "ClearResult");
            MainWindowPagesTests.Call(window, "StartCheck", "Clear Teams cache");
            MainWindowPagesTests.Call(window, "ShowToolOutcome", UserCaches.ClearTeamsCacheOutcome(TeamsClose.QuitByUser, 4275, 58, 833_754_316L), "ClearTeamsCache");
            Fit(window, width, 1000);
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<Border>(window, "Drawer").Visibility);
            Assert.Equal("OK", MainWindowPagesTests.Get<TextBlock>(window, "StatusWord").Text);
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<StackPanel>(window, "StatusPanel").Visibility);
            // View details opens it, with the full result and Copy details for IT.
            MainWindowPagesTests.Call(window, "OnDetails", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<Border>(window, "Drawer").Visibility);
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<Button>(window, "CopyTicketButton").Visibility);
            // The choice holds for this session: the next result shows in the open drawer.
            MainWindowPagesTests.Call(window, "ClearResult");
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<Border>(window, "Drawer").Visibility);
            MainWindowPagesTests.Call(window, "OnDetails", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<Border>(window, "Drawer").Visibility);
        });
    }

    [Fact]
    public void A_drawer_opened_or_closed_while_maximized_fits_the_window_once_it_is_restored()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            window.Width = 760;
            // Opened while maximized: nothing is added then; once restored the window grows by the
            // drawer (as far as the screen allows), so the page keeps its width and the drawer shows whole.
            window.WindowState = WindowState.Maximized;
            MainWindowPagesTests.Call(window, "OnDetails", window, new RoutedEventArgs());
            Assert.Equal(760, window.Width);
            window.WindowState = WindowState.Normal;
            window.FitDrawerAfterRestore();
            var added = (double)MainWindowPagesTests.Field(window, "_drawerAdded");
            Assert.True(added > 0, "the drawer added no width after the restore");
            Assert.Equal(760 + added, window.Width);
            window.FitDrawerAfterRestore();
            Assert.Equal(760 + added, window.Width);

            // Closed while maximized: the width it added comes off once restored.
            window.WindowState = WindowState.Maximized;
            MainWindowPagesTests.Call(window, "OnDetails", window, new RoutedEventArgs());
            Assert.Equal(760 + added, window.Width);
            window.WindowState = WindowState.Normal;
            window.FitDrawerAfterRestore();
            Assert.Equal(760, window.Width);

            // Opened in a normal window, closed and opened again while maximized: its room is still
            // in the restored width, so nothing is added twice.
            MainWindowPagesTests.Call(window, "OnDetails", window, new RoutedEventArgs());
            var opened = window.Width;
            window.WindowState = WindowState.Maximized;
            MainWindowPagesTests.Call(window, "OnDetails", window, new RoutedEventArgs());
            MainWindowPagesTests.Call(window, "OnDetails", window, new RoutedEventArgs());
            window.WindowState = WindowState.Normal;
            window.FitDrawerAfterRestore();
            Assert.Equal(opened, window.Width);
            MainWindowPagesTests.Call(window, "OnDetails", window, new RoutedEventArgs());
            Assert.Equal(760, window.Width);
        });
    }

    [Fact]
    public void A_health_row_opens_with_Enter_and_through_its_expand_pattern_and_shows_its_detail_only_then()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, Health(warn: HealthChecks.ImeCheckIn), new DsregInfo(), false);
            MainWindowPagesTests.Call(window, "OnHealthToggle", window, new RoutedEventArgs());
            Fit(window, WindowLayout.DefaultClientWidth, WindowLayout.CompactClientHeight);
            var list = MainWindowPagesTests.Get<ItemsControl>(window, "HealthList");
            var index = ((IEnumerable<HealthRow>)list.ItemsSource).ToList().FindIndex(r => r.Name == "Work app installer");
            var container = (ContentPresenter)list.ItemContainerGenerator.ContainerFromIndex(index);
            var row = (AccessibleRow)System.Windows.Media.VisualTreeHelper.GetChild(container, 0);
            var item = (HealthRow)row.DataContext;
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(row);
            var pattern = (System.Windows.Automation.Provider.IExpandCollapseProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.ExpandCollapse);
            Assert.NotNull(pattern);
            Assert.Equal(System.Windows.Automation.ExpandCollapseState.Collapsed, pattern.ExpandCollapseState);
            // Closed: the detail is not shown, so it is not in the automation tree either.
            Assert.DoesNotContain((IEnumerable<System.Windows.Automation.Peers.AutomationPeer>)peer.GetChildren() ?? Array.Empty<System.Windows.Automation.Peers.AutomationPeer>(), p => p.GetName().Contains("Last attempt failed"));
            pattern.Expand();
            Assert.True(item.IsOpen);
            Assert.Equal(System.Windows.Automation.ExpandCollapseState.Expanded, pattern.ExpandCollapseState);
            pattern.Collapse();
            Assert.False(item.IsOpen);
            // Enter on the row itself opens it; on its fix link it does not.
            var enter = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, new HwndlessSource(), 0, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent };
            row.RaiseEvent(enter);
            Assert.True(item.IsOpen);
            // The hint line under the rows is gone; the result cards are not expandable.
            Assert.Null(window.FindName("HealthPointHint"));
        });
    }

    private sealed class HwndlessSource : PresentationSource
    {
        public override System.Windows.Media.Visual RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget GetCompositionTargetCore() => null;
    }

    [Fact]
    public void The_status_area_shows_only_for_runs_with_the_result_in_a_word()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var panel = MainWindowPagesTests.Get<StackPanel>(window, "StatusPanel");
            // The health checks only: Home's card says it; no status block repeats it.
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, Health(warn: HealthChecks.Clock), new DsregInfo(), false);
            Assert.Equal(Visibility.Collapsed, panel.Visibility);
            // A run: shown, without a word while it runs, with its word when it ended.
            MainWindowPagesTests.Call(window, "SetStatus", "Synchronising the clock...", Tone.Neutral, true, true);
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<Border>(window, "StatusWordBadge").Visibility);
            MainWindowPagesTests.Call(window, "SetStatus", "Sync clock could not finish. See the details.", Tone.Error, true, true);
            Assert.Equal("PROBLEM", MainWindowPagesTests.Get<TextBlock>(window, "StatusWord").Text);
            // One line on screen; the whole text in its tooltip, its automation name and the drawer.
            var text = MainWindowPagesTests.Get<TextBlock>(window, "StatusLabel");
            Assert.Equal("Sync clock could not finish. See the details.", System.Windows.Automation.AutomationProperties.GetName(text));
            Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
            Assert.Equal("Sync clock could not finish. See the details.", text.ToolTip);
            Assert.Equal("Sync clock could not finish. See the details.", MainWindowPagesTests.Get<TextBlock>(window, "DrawerStatusText").Text);
            // A result of a run shows only its name on screen; View details has the rest.
            MainWindowPagesTests.Call(window, "StartCheck", "Clear Teams cache");
            MainWindowPagesTests.Call(window, "SetStatus", "Clear Teams cache: done. Start Teams again.", Tone.Success, true, true);
            Assert.Equal("Clear Teams cache", text.Text);
            Assert.Equal("Clear Teams cache: done. Start Teams again.", System.Windows.Automation.AutomationProperties.GetName(text));
            Assert.Equal("Clear Teams cache: done. Start Teams again.", MainWindowPagesTests.Get<TextBlock>(window, "StatusText").Text);
            // Check again hides it again.
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, Health(), new DsregInfo(), false);
            Assert.Equal(Visibility.Collapsed, panel.Visibility);
            Assert.Equal(new[] { "OK", "WARNING", "PROBLEM", "" }, new[] { Tone.Success, Tone.Warning, Tone.Error, Tone.Neutral }.Select(Words.ResultWord));
        });
    }
}
