using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using EndpointToolbelt.Platform;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>The window's Office row and path, the safe-mode question, Get a new IP address and the path gaps.</summary>
public class SafeModeAndIpWindowTests
{
    private static List<ToolRowView> PathTools(MainWindow window)
        => ((IEnumerable<ToolRowView>)MainWindowPagesTests.Get<ItemsControl>(window, "ProblemTools").ItemsSource).ToList();

    [Fact]
    public void The_Office_row_shows_only_with_Word_or_Excel_and_its_path_offers_only_the_installed_app()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var row = MainWindowPagesTests.Get<Button>(window, "ProblemOfficeButton");
            window.OfficeApps = OfficeSafeModeTests.FakeOffice.With(word: false, excel: false);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            Assert.Equal(Visibility.Collapsed, row.Visibility);

            window.OfficeApps = OfficeSafeModeTests.FakeOffice.With(word: true, excel: false);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            Assert.Equal(Visibility.Visible, row.Visibility);
            Assert.Equal("An Office app closes right after it opens. Word, Excel, Outlook (classic), PowerPoint", AutomationProperties.GetName(row));
            MainWindowPagesTests.Call(window, "OnProblemOffice", row, new RoutedEventArgs());
            Assert.Equal("An Office app closes right after it opens", MainWindowPagesTests.Get<TextBlock>(window, "ProblemTitle").Text);
            Assert.Equal(new[] { "WordSafeButton" }, PathTools(window).Select(t => t.Tool.Button));
            // No health rows on this path, so no verdict.
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<TextBlock>(window, "ProblemVerdict").Visibility);
            // Back returns to Choose a problem.
            MainWindowPagesTests.Call(window, "OnPathBack", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<StackPanel>(window, "ProblemsPage").Visibility);
        });
    }

    [Fact]
    public void Word_installed_while_the_window_is_open_can_run_from_its_row()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            window.OfficeApps = OfficeSafeModeTests.FakeOffice.With(word: false, excel: false);
            MainWindowPagesTests.Call(window, "UpdateButtons");
            Assert.False(MainWindowPagesTests.Get<Button>(window, "WordSafeButton").IsEnabled);

            // Word arrives while the window is open; nothing else changes state.
            window.OfficeApps = OfficeSafeModeTests.FakeOffice.With(word: true, excel: false);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            MainWindowPagesTests.Call(window, "OnProblemOffice", window, new RoutedEventArgs());
            var row = Assert.Single(PathTools(window));
            Assert.Equal("WordSafeButton", row.Tool.Button);
            Assert.True(row.RunTarget.IsEnabled);

            // For IT sets them again too.
            window.OfficeApps = OfficeSafeModeTests.FakeOffice.With(word: false, excel: false);
            MainWindowPagesTests.Call(window, "ShowIt");
            Assert.False(MainWindowPagesTests.Get<Button>(window, "WordSafeButton").IsEnabled);
        });
    }

    [Fact]
    public void For_IT_says_a_missing_app_is_not_installed_and_its_tool_cannot_run()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            window.OfficeApps = OfficeSafeModeTests.FakeOffice.With(word: true, excel: false);
            MainWindowPagesTests.Call(window, "UpdateButtons");
            var excel = MainWindowPagesTests.Get<Button>(window, "ExcelSafeButton");
            Assert.False(excel.IsEnabled);
            Assert.Equal("Not available: Excel is not installed on this PC", AutomationProperties.GetHelpText(excel));
            Assert.True(MainWindowPagesTests.Get<Button>(window, "WordSafeButton").IsEnabled);
            var row = (ToolRowView)MainWindowPagesTests.Invoke(window, "NewToolRow", ToolCatalog.ByButton("ExcelSafeButton"), false, false);
            Assert.Equal("Not available", row.ResultText);
            Assert.StartsWith("Not available: Excel is not installed on this PC", row.Line);
            // Its detail page says it too.
            MainWindowPagesTests.SetField(window, "_itTool", ToolCatalog.ByButton("ExcelSafeButton"));
            MainWindowPagesTests.Call(window, "ShowToolDetail");
            Assert.Equal("Not available: Excel is not installed on this PC.", MainWindowPagesTests.Get<TextBlock>(window, "ToolLastRunText").Text);
        });
    }

    [Fact]
    public void A_safe_start_from_the_path_asks_the_apps_question_and_the_answer_is_the_same_run_in_the_history_and_the_log()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var office = OfficeSafeModeTests.FakeOffice.With(word: true, excel: true);
            MainWindowPagesTests.Call(window, "ClearResult");
            MainWindowPagesTests.Call(window, "StartCheck", OfficeSafeMode.ToolName(OfficeApp.Word));
            var outcome = OfficeSafeMode.Start(office, OfficeApp.Word, null, asks: true);
            MainWindowPagesTests.Call(window, "ShowSafeModeOutcome", outcome);
            var question = MainWindowPagesTests.Get<WrapPanel>(window, "SafeModeQuestion");
            Assert.Equal(Visibility.Visible, question.Visibility);
            Assert.Equal("Word was started in safe mode. Could you open a document?", MainWindowPagesTests.Get<TextBlock>(window, "StatusText").Text);
            Assert.Equal("Could you open a document?", MainWindowPagesTests.Get<TextBlock>(window, "SafeModeQuestionText").Text);
            Assert.Equal("/safe", office.Starts.Single().Arguments);

            MainWindowPagesTests.Call(window, "AnswerSafeMode", SafeModeAnswer.Yes);
            Assert.Equal(Visibility.Collapsed, question.Visibility);
            Assert.Equal("Word works in safe mode. An add-in or a setting may be the cause; that is not proven.", MainWindowPagesTests.Get<TextBlock>(window, "StatusText").Text);
            // One run, one history line: the answer replaced the start.
            var history = File.ReadAllLines(window.HistoryOverride);
            var line = Assert.Single(history);
            Assert.Contains(";WordSafeMode;0|local-", line);
            Assert.EndsWith("|works-in-safe-mode", line);
            Assert.DoesNotContain("Contoso Office", string.Join("\n", history));
            // An Outlook start from the path asks its own question.
            MainWindowPagesTests.Call(window, "ShowSafeModeOutcome", OfficeSafeMode.Start(OfficeSafeModeTests.FakeOffice.All(), OfficeApp.Outlook, null, asks: true));
            Assert.Equal(Visibility.Visible, question.Visibility);
            Assert.Equal("Did Outlook stay open?", MainWindowPagesTests.Get<TextBlock>(window, "SafeModeQuestionText").Text);
            Assert.Equal("Yes, Outlook stayed open in safe mode", AutomationProperties.GetName(MainWindowPagesTests.Get<Button>(window, "SafeYesButton")));
            MainWindowPagesTests.Call(window, "ClearResult");
            Assert.Equal(Visibility.Collapsed, question.Visibility);
        });
    }

    [Fact]
    public void A_start_from_For_IT_asks_nothing_and_its_run_is_Start_requested_never_ENDED()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var office = OfficeSafeModeTests.FakeOffice.All();
            window.OfficeApps = office;
            MainWindowPagesTests.Call(window, "OpenTool", ToolCatalog.ByButton("PowerPointSafeButton"), false);
            var button = MainWindowPagesTests.Get<Button>(window, "PowerPointSafeButton");
            Assert.Equal("Start in safe mode", button.Content);
            Assert.Equal("Start in safe mode: PowerPoint", System.Windows.Automation.AutomationProperties.GetName(button));
            MainWindowPagesTests.Call(window, "OnPowerPointSafe", button, new RoutedEventArgs());
            MainWindowPagesTests.WaitFor(() => !(bool)MainWindowPagesTests.Field(window, "_busy"));
            Assert.Equal("powerpnt", Path.GetFileNameWithoutExtension(office.Starts.Single().Path).ToLowerInvariant());
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<WrapPanel>(window, "SafeModeQuestion").Visibility);
            Assert.Equal("PowerPoint was started in safe mode.", MainWindowPagesTests.Get<TextBlock>(window, "StatusText").Text);
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<Border>(window, "StatusWordBadge").Visibility);
            // The button has its own text back.
            Assert.Equal("Start in safe mode", button.Content);
            Assert.Equal("Start in safe mode: PowerPoint", System.Windows.Automation.AutomationProperties.GetName(button));
            Assert.EndsWith("|start-requested", Assert.Single(File.ReadAllLines(window.HistoryOverride)));
            MainWindowPagesTests.WaitFor(() => Directory.Exists(window.RunsFolderOverride) && Directory.GetFiles(window.RunsFolderOverride, "*_StartPowerPointinsafemode_*.log").Length == 1);
            MainWindowPagesTests.Call(window, "ShowIt");
            // No badge on the tile, no ENDED anywhere: the row and the recent run say "Start requested".
            Assert.Equal("7 tools", MainWindowPagesTests.Get<TextBlock>(window, "SignInTileSummary").Text);
            var recent = ((IEnumerable<RecentRunView>)MainWindowPagesTests.Get<ItemsControl>(window, "RecentRuns").ItemsSource).ToList();
            Assert.EndsWith(" · PowerPoint · Start requested", recent[0].Text);
            Assert.DoesNotContain("local-", recent[0].Text);
            var row = (ToolRowView)MainWindowPagesTests.Invoke(window, "NewToolRow", ToolCatalog.ByButton("PowerPointSafeButton"), false, false);
            Assert.StartsWith("Start requested · ", row.ResultText);
            MainWindowPagesTests.Call(window, "ShowToolDetail");
            var last = MainWindowPagesTests.Get<TextBlock>(window, "ToolLastRunText").Text;
            Assert.StartsWith("Start requested · ", last);
            Assert.DoesNotContain("reference", last);
            Assert.DoesNotContain("ENDED", recent[0].Text + row.ResultText + last);
        });
    }

    [Fact]
    public void Already_open_keeps_its_reason_and_what_to_do()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var office = OfficeSafeModeTests.FakeOffice.All();
            office.Running.Add(OfficeApp.Excel);
            MainWindowPagesTests.Call(window, "ClearResult");
            MainWindowPagesTests.Call(window, "StartCheck", OfficeSafeMode.ToolName(OfficeApp.Excel));
            MainWindowPagesTests.Call(window, "ShowSafeModeOutcome", OfficeSafeMode.Start(office, OfficeApp.Excel, () => false, asks: true));
            Assert.Equal("Excel is already open, so nothing was started. Close it normally first, then try again.", MainWindowPagesTests.Get<TextBlock>(window, "StatusText").Text);
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<WrapPanel>(window, "SafeModeQuestion").Visibility);
            Assert.Empty(office.Starts);
        });
    }

    [Fact]
    public void The_Office_path_lists_every_installed_supported_app()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            window.OfficeApps = OfficeSafeModeTests.FakeOffice.With(word: false, excel: false, outlook: true, powerPoint: true);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<Button>(window, "ProblemOfficeButton").Visibility);
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.OfficeCloses, false);
            Assert.Equal(new[] { "OutlookSafeButton", "PowerPointSafeButton" }, PathTools(window).Select(t => t.Tool.Button));
            window.OfficeApps = OfficeSafeModeTests.FakeOffice.All();
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.OfficeCloses, false);
            Assert.Equal(new[] { "WordSafeButton", "ExcelSafeButton", "OutlookSafeButton", "PowerPointSafeButton" }, PathTools(window).Select(t => t.Tool.Button));
        });
    }

    [Fact]
    public void A_tools_result_stays_with_its_page_and_never_shows_on_Choose_a_problem()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var status = MainWindowPagesTests.Get<StackPanel>(window, "StatusPanel");
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.CantConnect, false);
            MainWindowPagesTests.Call(window, "SetStatus", "This PC reached all 5 Microsoft connections.", Tone.Success, false, true);
            Assert.Equal(Visibility.Visible, status.Visibility);
            // Away from the page: not on Choose a problem, Home, For IT or another path.
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            Assert.Equal(Visibility.Collapsed, status.Visibility);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false);
            Assert.Equal(Visibility.Collapsed, status.Visibility);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("It"), false);
            Assert.Equal(Visibility.Collapsed, status.Visibility);
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.CantSignIn, false);
            Assert.Equal(Visibility.Collapsed, status.Visibility);
            // Back on its own page it is there again.
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.CantConnect, false);
            Assert.Equal(Visibility.Visible, status.Visibility);
        });
    }

    [Fact]
    public void I_cant_connect_offers_Get_a_new_IP_address_only_while_an_adapter_needs_one()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var network = new FakeNetwork();
            network.Adapters.Add(IpRenewalTests.Adapter(12, usable: true));
            window.Adapters = network;
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.CantConnect, false);
            Assert.Equal(new[] { "ConnectivityButton" }, PathTools(window).Select(t => t.Tool.Button));

            network.Adapters[0].UsableIpv4 = false;
            network.Adapters[0].SelfAssignedOnly = true;
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.CantConnect, false);
            Assert.Equal(new[] { "ConnectivityButton", "RenewIpButton" }, PathTools(window).Select(t => t.Tool.Button));
            Assert.Equal("Choose an adapter", PathTools(window).Last().RunText);
        });
    }

    [Fact]
    public void The_adapter_page_chooses_the_adapter_without_an_address_and_offers_nothing_for_a_fixed_one()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var network = new FakeNetwork();
            network.Adapters.Add(IpRenewalTests.Adapter(5, kind: AdapterKind.WiFi, usable: true));
            network.Adapters.Add(IpRenewalTests.Adapter(12, selfAssigned: true));
            network.Adapters.Add(IpRenewalTests.Adapter(20, dhcp: false, usable: true));
            network.Adapters.Add(IpRenewalTests.Adapter(30, physical: false));
            window.Adapters = network;
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.CantConnect, false);
            MainWindowPagesTests.Call(window, "OnRenewIpTool", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<StackPanel>(window, "IpPage").Visibility);
            var rows = ((IEnumerable<AdapterRowView>)MainWindowPagesTests.Get<ItemsControl>(window, "AdapterList").ItemsSource).ToList();
            // The one without an address first, then by kind (Ethernet before Wi-Fi).
            Assert.Equal(new[] { 12, 20, 5 }, rows.Select(r => r.Adapter.InterfaceIndex));
            Assert.Equal("Selected", rows[0].SelectedTag);
            Assert.Equal("Ethernet · no usable address (169.254.x.x)", rows[0].State);
            var renew = MainWindowPagesTests.Get<Button>(window, "RenewNowButton");
            Assert.True(renew.IsEnabled);
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<TextBlock>(window, "IpNotOffered").Visibility);
            Assert.Equal("Back to No internet, or work apps can't connect", AutomationProperties.GetName(MainWindowPagesTests.Get<Button>(window, "BackButton")));

            MainWindowPagesTests.Call(window, "OnAdapterChosen", new Button { DataContext = rows[1] }, new RoutedEventArgs());
            Assert.False(renew.IsEnabled);
            Assert.StartsWith("Not offered: this adapter uses a fixed address", MainWindowPagesTests.Get<TextBlock>(window, "IpNotOffered").Text);
            Assert.Empty(network.Renewed);

            // Cancel goes back to I can't connect and changes nothing.
            MainWindowPagesTests.Call(window, "OnCancelIp", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<StackPanel>(window, "ProblemPage").Visibility);
            Assert.Empty(network.Renewed);
        });
    }

    [Fact]
    public void For_IT_opens_the_adapter_page_and_Back_returns_there()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("It"), false);
            MainWindowPagesTests.Call(window, "OnRenewIpTool", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<StackPanel>(window, "IpPage").Visibility);
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<TextBlock>(window, "IpEmpty").Visibility);
            Assert.Equal("Back to For IT support", AutomationProperties.GetName(MainWindowPagesTests.Get<Button>(window, "BackButton")));
            Assert.Equal("Selected", MainWindowPagesTests.Get<Button>(window, "ItNav").Tag);
            MainWindowPagesTests.Call(window, "OnPathBack", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<StackPanel>(window, "ItPage").Visibility);
        });
    }

    [Fact]
    public void The_path_gaps_are_filled_install_blockers_on_the_app_path_and_Teams_on_sign_in()
    {
        Assert.Contains("TeamsCacheButton", ProblemPaths.Get(Problem.CantSignIn).Tools);
        var app = ProblemPaths.Get(Problem.AppWontInstall);
        Assert.Contains(HealthChecks.FreeSpace, app.Rows);
        Assert.Contains(HealthChecks.PendingRestart, app.Rows);
        Assert.Equal(new[] { HealthChecks.FreeSpace, HealthChecks.PendingRestart }, ProblemPaths.InstallBlockers);
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, CompactLayoutTests.Health(warn: HealthChecks.PendingRestart), new DsregInfo(), false);
            MainWindowPagesTests.Call(window, "ShowAppChecks");
            var rows = ((IEnumerable<HealthRow>)MainWindowPagesTests.Get<ItemsControl>(window, "AppChecks").ItemsSource).ToList();
            Assert.Equal(new[] { HealthChecks.FreeSpace, HealthView.PlainName(HealthChecks.PendingRestart) }, rows.Select(r => r.Name));
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<Border>(window, "AppChecksBox").Visibility);
        });
    }

    [Fact]
    public void After_a_tool_the_status_says_its_rows_new_state_and_a_new_run_clears_it()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, CompactLayoutTests.Health(warn: HealthChecks.Clock), new DsregInfo(), false);
            var before = (HealthResult)MainWindowPagesTests.Invoke(window, "HealthRowNamed", HealthChecks.Clock);
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, CompactLayoutTests.Health(), new DsregInfo(), true);
            MainWindowPagesTests.Call(window, "ShowHealthLine", ToolCatalog.ByButton("SyncClockButton"), before);
            var line = MainWindowPagesTests.Get<TextBlock>(window, "StatusHealthText");
            Assert.Equal("Clock: now OK", line.Text);
            Assert.Equal(Visibility.Visible, line.Visibility);
            MainWindowPagesTests.Call(window, "ClearResult");
            Assert.Equal(Visibility.Collapsed, line.Visibility);
            // A tool without a row of its own shows no line.
            MainWindowPagesTests.Call(window, "ShowHealthLine", ToolCatalog.ByButton("TeamsCacheButton"), null);
            Assert.Equal(Visibility.Collapsed, line.Visibility);
        });
    }

    [Fact]
    public void Back_returns_to_the_page_a_path_was_opened_from_and_Home_keeps_its_tab()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            string Page() => MainWindowPagesTests.Field(window, "_page").ToString();
            var back = MainWindowPagesTests.Get<Button>(window, "BackButton");
            void Open(string handler) => MainWindowPagesTests.Call(window, handler, window, new RoutedEventArgs());
            void Back() => MainWindowPagesTests.Call(window, "OnPathBack", window, new RoutedEventArgs());

            // A path opened on Home goes back to Home, under Home's tab, with a Back that says so.
            foreach (var handler in new[] { "OnProblemSignIn", "OnProblemConnect", "OnProblemUpdate", "OnProblemApp" })
            {
                MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false);
                Open(handler);
                Assert.NotEqual("Home", Page());
                Assert.Equal("Back to Home", AutomationProperties.GetName(back));
                Assert.Equal("Selected", MainWindowPagesTests.Get<Button>(window, "HomeNav").Tag);
                Assert.Null(MainWindowPagesTests.Get<Button>(window, "ProblemsNav").Tag);
                Back();
                Assert.Equal("Home", Page());
            }

            // The Home tile whose click opened the path is the element Back returns to (also pressed by UI Automation).
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false);
            var tile = MainWindowPagesTests.Get<Button>(window, "HomeSignInTile");
            tile.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, tile));
            Assert.Equal("Problem", Page());
            Assert.Same(tile, MainWindowPagesTests.Field(window, "_pathOpener"));
            Back();
            Assert.Equal("Home", Page());

            // The adapter page and back to the path keep where the path came from.
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false);
            Open("OnProblemConnect");
            Open("OnRenewIpTool");
            Assert.Equal("Ip", Page());
            Assert.Equal("Selected", MainWindowPagesTests.Get<Button>(window, "HomeNav").Tag);
            Back();
            Assert.Equal("Problem", Page());
            Back();
            Assert.Equal("Home", Page());

            // Opened on Choose a problem: back there, under its tab.
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            Open("OnProblemSignIn");
            Assert.Equal("Back to Choose a problem", AutomationProperties.GetName(back));
            Assert.Equal("Selected", MainWindowPagesTests.Get<Button>(window, "ProblemsNav").Tag);
            Back();
            Assert.Equal("Problems", Page());

            // A tab chosen in between: the path opened there goes back there.
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false);
            Open("OnProblemSignIn");
            Open("OnNavProblems");
            Open("OnProblemUpdate");
            Back();
            Assert.Equal("Problems", Page());

            // "My problem is not listed" goes back by Alt+Left and Backspace too (GoBack), not only by the button.
            Open("OnProblemNotListed");
            Assert.Equal("NotListed", Page());
            Assert.True((bool)MainWindowPagesTests.Invoke(window, "GoBack"));
            Assert.Equal("Problems", Page());
        });
    }

    [Fact]
    public void A_focus_that_did_not_come_from_a_key_draws_no_ring_until_a_key_is_pressed()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var button = MainWindowPagesTests.Get<Button>(window, "ProblemSignInButton");
            var ring = button.FocusVisualStyle;
            Assert.NotNull(ring);
            // After a click (or the app moving the focus) the element keeps the focus without its ring.
            window.QuietFocusVisual(button);
            Assert.Null(button.FocusVisualStyle);
            // The next key brings the ring back, from the element's own style.
            window.RestoreFocusVisuals();
            Assert.Same(ring, button.FocusVisualStyle);
            Assert.Equal(DependencyProperty.UnsetValue, button.ReadLocalValue(FrameworkElement.FocusVisualStyleProperty));

            // A key the window handles itself (Backspace goes back) still turns the cues on, through the real
            // event: the cues handler runs before the window's own keys, also for a handled key.
            MainWindowPagesTests.SetField(window, "_keyboardCues", false);
            window.QuietFocusVisual(button);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            MainWindowPagesTests.Call(window, "OnProblemSignIn", window, new RoutedEventArgs());
            MainWindowPagesTests.Press(window, System.Windows.Input.Key.Back);
            Assert.True(window.KeyboardCues);
            Assert.Same(ring, button.FocusVisualStyle);
            Assert.Equal(0, window.QuietFocusCount);

            // A modifier alone or Print Screen (a screenshot shortcut) is no keyboard use: the ring stays hidden.
            foreach (var key in new[] { System.Windows.Input.Key.LeftShift, System.Windows.Input.Key.LWin, System.Windows.Input.Key.Snapshot, System.Windows.Input.Key.LeftCtrl })
            {
                MainWindowPagesTests.SetField(window, "_keyboardCues", false);
                MainWindowPagesTests.Press(window, key);
                Assert.False(window.KeyboardCues, key.ToString());
            }
            MainWindowPagesTests.Press(window, System.Windows.Input.Key.Tab);
            Assert.True(window.KeyboardCues);

            // A dialog hands its last input back: closed after a click, the window's cues are off.
            window.SetKeyboardCues(false);
            Assert.False(window.KeyboardCues);
            window.SetKeyboardCues(true);
            Assert.True(window.KeyboardCues);
        });
    }

    [Fact]
    public void No_path_lists_Bring_my_window_back_so_its_page_never_follows_a_Home_path()
    {
        // The window page keeps its own origin (For IT or Choose a problem); no path shows its tool, so a
        // path opened on Home never leads to it with the Choose a problem tab.
        Assert.All(ProblemPaths.All, p => Assert.DoesNotContain("WindowBackButton", p.Tools));
    }
}
