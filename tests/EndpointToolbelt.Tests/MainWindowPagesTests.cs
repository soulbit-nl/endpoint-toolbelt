using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The real main window, built with the app's own styles but never shown: it loads, its
/// pages switch, the tool search filters the For IT groups, and Home and the app path show the
/// same diagnosis. Never shown, so it never contacts the service and writes nothing.
/// </summary>
public class MainWindowPagesTests
{
    // One UI thread for every window of these tests: the application object and its resources
    // belong to the thread that made them.
    private static readonly Lazy<System.Windows.Threading.Dispatcher> Ui = new(() =>
    {
        System.Windows.Threading.Dispatcher dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true, Name = "Window tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        uiThread = thread;
        return dispatcher;
    });

    private static Thread uiThread;

    /// <summary>
    /// Ends the shared UI thread at the end of the test run, before xUnit unloads the run's
    /// application domain (<see cref="TestRunFramework"/>). Left running, the unload aborted it:
    /// its COM teardown then waited in CoUninitialize while the test host's own STA thread tore
    /// down as well, and the test host never exited after all tests had passed (two hang dumps).
    /// A dispatcher no longer runs work once the unload has begun, so this cannot wait until then.
    /// </summary>
    internal static void StopSharedUi()
    {
        if (Ui.IsValueCreated) { StopUi(Ui.Value, uiThread); }
    }

    /// <summary>
    /// Closes the test windows (their Closed handlers detach the static handlers), shuts the
    /// dispatcher down on its own thread and waits for that thread to end. Every wait is bounded,
    /// so it can never hold up the end of the run itself.
    /// </summary>
    internal static void StopUi(System.Windows.Threading.Dispatcher dispatcher, Thread thread)
    {
        if (!dispatcher.HasShutdownStarted)
        {
            try
            {
                dispatcher.Invoke(() =>
                {
                    var app = Application.Current;
                    if (app == null || app.Dispatcher != dispatcher) { return; }
                    foreach (var window in app.Windows.OfType<Window>().ToList())
                    {
                        try { window.Close(); } catch (InvalidOperationException) { }
                    }
                }, System.Windows.Threading.DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or OperationCanceledException) { }
            dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
        }
        thread?.Join(TimeSpan.FromSeconds(10));
    }

    internal static void OnSta(Action action)
    {
        Exception error = null;
        Ui.Value.Invoke(() =>
        {
            try { action(); } catch (Exception ex) { error = ex; }
        });
        if (error != null) { throw new Xunit.Sdk.XunitException(error.ToString()); }
    }

    /// <summary>Runs the window thread's queued work (an await's continuation) once.</summary>
    internal static void Drain()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>Runs the window thread's queued work until <paramref name="condition"/> holds, at most 10 seconds.</summary>
    internal static void WaitFor(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) { throw new TimeoutException("The condition did not hold within 10 seconds."); }
            Drain();
            System.Threading.Thread.Sleep(20);
        }
    }

    internal static MainWindow NewWindow()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.MergedDictionaries.Count == 0)
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/EndpointToolbelt;component/Ui/Themes/Light.xaml", UriKind.Absolute) });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/EndpointToolbelt;component/Ui/Themes/Styles.xaml", UriKind.Absolute) });
        }
        var window = new MainWindow();
        // No window of a test reads or writes the user's own remedy records.
        UseRemedies(window, Path.Combine(Path.GetTempPath(), "etb-test-remedies-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".json"));
        // Nor the user's settings or run history (For IT's last runs).
        var stem = Path.Combine(Path.GetTempPath(), "etb-test-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        SetField(window, "_settings", new AppSettings());
        window.SettingsPath = stem + "-settings.json";
        window.RunsFolderOverride = stem + "-runs";
        window.HistoryOverride = stem + "-history.log";
        // Nor this PC's Office apps and network adapters, nor its health checks after a tool.
        window.OfficeApps = OfficeSafeModeTests.FakeOffice.With(word: true, excel: true);
        // Nor this PC's OneDrive (not installed unless a test says so), and no pause after a safe-mode start.
        window.OneDrive = new OneDriveResetTests.FakeOneDrive();
        window.StartingHold = TimeSpan.Zero;
        window.Adapters = new FakeNetwork();
        window.HealthAfterTools = false;
        // Never this PC's uptime: a fixed start three hours ago, unless a test sets its own.
        window.BootClock = () => DateTime.UtcNow.AddHours(-3);
        Call(window, "ApplyHelpers");
        Call(window, "ShowHome");
        return window;
    }

    internal static void SetField(MainWindow window, string name, object value)
    {
        var field = typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(window, value);
    }

    internal static object Field(MainWindow window, string name)
    {
        var field = typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(window);
    }

    /// <summary>The window keeps its remedy records in <paramref name="path"/>, starting with none.</summary>
    internal static void UseRemedies(MainWindow window, string path)
    {
        typeof(MainWindow).GetField("_remediesPath", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, path);
        typeof(MainWindow).GetField("_remedies", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, new List<RemedyRecord>());
    }

    internal static T Get<T>(Window window, string name) where T : class => (T)window.FindName(name);

    internal static void Call(Window window, string method, params object[] args) => Invoke(window, method, args);

    internal static object Invoke(Window window, string method, params object[] args)
    {
        var info = typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(info);
        return info.Invoke(window, args);
    }

    internal static object PageValue(string name) => Enum.Parse(typeof(MainWindow).GetNestedType("Page", BindingFlags.NonPublic), name);

    [Fact]
    public void The_end_of_run_cleanup_ends_a_UI_thread_with_a_window_within_its_bound()
    {
        // What runs when the test run's application domain is unloaded, on a UI thread of
        // its own (the shared one must stay up for the other tests).
        System.Windows.Threading.Dispatcher dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _ = new Window { Content = new TextBlock { Text = "Contoso" } };
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true, Name = "Cleanup test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        StopUi(dispatcher, thread);
        Assert.False(thread.IsAlive);
        Assert.True(dispatcher.HasShutdownFinished);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());

        // Twice is harmless: a stopped dispatcher is left alone.
        StopUi(dispatcher, thread);
    }

    [Fact]
    public void The_window_loads_with_Home_and_switches_between_its_three_entries()
    {
        OnSta(() =>
        {
            var window = NewWindow();
            Assert.Equal(Visibility.Visible, Get<StackPanel>(window, "HomePage").Visibility);
            Assert.Equal("Selected", Get<Button>(window, "HomeNav").Tag);

            Call(window, "ShowPage", PageValue("It"), false);
            Assert.Equal(Visibility.Visible, Get<StackPanel>(window, "ItPage").Visibility);
            Assert.Equal(Visibility.Collapsed, Get<StackPanel>(window, "HomePage").Visibility);
            Assert.Equal("Selected", Get<Button>(window, "ItNav").Tag);
            Assert.Equal("Selected", AutomationProperties.GetItemStatus(Get<Button>(window, "ItNav")));
            Assert.Null(Get<Button>(window, "HomeNav").Tag);
            Assert.Equal("", AutomationProperties.GetItemStatus(Get<Button>(window, "HomeNav")));

            // A problem path sits under Choose a problem, with Back.
            Call(window, "ShowProblem", Problem.CantConnect, false);
            Assert.Equal(Visibility.Visible, Get<StackPanel>(window, "ProblemPage").Visibility);
            Assert.Equal(Visibility.Visible, Get<StackPanel>(window, "ProblemHeader").Visibility);
            Assert.Equal("Selected", Get<Button>(window, "ProblemsNav").Tag);
            Assert.Equal("No internet, or work apps can't connect", Get<TextBlock>(window, "ProblemTitle").Text);
            // Its tools as rows, each with its own Run button.
            var tools = ((IEnumerable<ToolRowView>)Get<ItemsControl>(window, "ProblemTools").ItemsSource).ToList();
            Assert.Equal(new[] { "Check Microsoft connections" }, tools.Select(t => t.Name));
            Assert.Equal(new[] { "Check connections: Check Microsoft connections" }, tools.Select(t => t.RunName));
            Assert.Same(Get<Button>(window, "ConnectivityButton"), tools[0].RunTarget);
        });
    }

    /// <summary>The rows For IT's tool list shows.</summary>
    internal static List<ToolRowView> ToolRows(MainWindow window)
        => (Get<ItemsControl>(window, "ToolList").ItemsSource as IEnumerable<ToolRowView> ?? Enumerable.Empty<ToolRowView>()).ToList();

    /// <summary>For IT's narrow view: Tiles, List or Detail.</summary>
    internal static string ItViewOf(MainWindow window) => Field(window, "_itView").ToString();

    /// <summary>A key press in an element, as the keyboard sends it (the window is never shown).</summary>
    internal static void Press(UIElement target, System.Windows.Input.Key key)
    {
        var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, new FakeSource(), 0, key) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args);
    }

    private sealed class FakeSource : PresentationSource
    {
        public override System.Windows.Media.Visual RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget GetCompositionTargetCore() => null;
    }

    /// <summary>A run log in the history as the app writes it: its header, its result cards, its technical log.</summary>
    /// <summary>A run log's file name with a start of <paramref name="minutesAgo"/> minutes ago: a tool row and a tile show only a result of the last 24 hours.</summary>
    [Fact]
    public void A_problem_path_whose_verdict_says_the_checks_answer_shows_no_answer_line_but_For_IT_does()
    {
        OnSta(() =>
        {
            var window = NewWindow();
            RunOutcome Check()
            {
                var outcome = new RunOutcome { Action = ToolbeltAction.CheckWindowsUpdate, RunId = "ab12cd34", Result = ResultCode.Completed, ExitCode = 0, StartedUtc = DateTime.UtcNow };
                outcome.Records.Add(EngineRecord.Parse("{\"type\":\"finding\",\"run\":\"ab12cd34\",\"severity\":\"OK\",\"id\":\"WU-OK\",\"title\":\"Windows Update works\",\"detail\":\"Nothing holds it back.\",\"next\":\"\"}"));
                outcome.Records.Add(EngineRecord.Parse("{\"type\":\"summary\",\"run\":\"ab12cd34\",\"exit\":0,\"changes\":0,\"restart\":false}"));
                return outcome;
            }
            // On "Windows Update keeps failing" the path's verdict says the check's answer: no second sentence
            // below the status, which pushed the page into scrolling at the default size.
            Call(window, "ShowProblem", Problem.WindowsWontUpdate, false);
            Call(window, "ShowFinal", Check(), false);
            Assert.Equal(Visibility.Collapsed, Get<TextBlock>(window, "StatusAnswerText").Visibility);
            // In For IT the answer line stays.
            Call(window, "ShowPage", PageValue("It"), false);
            Call(window, "ShowFinal", Check(), false);
            Assert.Equal(Visibility.Visible, Get<TextBlock>(window, "StatusAnswerText").Visibility);
            Assert.True(ProblemPaths.VerdictSays(Problem.WindowsWontUpdate, ToolbeltAction.CheckWindowsUpdate));
            Assert.False(ProblemPaths.VerdictSays(Problem.DeviceBlocked, ToolbeltAction.CheckWindowsUpdate));
            Assert.False(ProblemPaths.VerdictSays(Problem.WindowsWontUpdate, ToolbeltAction.CheckDefenderBlocks));
        });
    }

    [Fact]
    public void A_check_answer_shows_on_the_page_and_the_details_stay_closed()
    {
        OnSta(() =>
        {
            var window = NewWindow();
            Call(window, "ShowAnswer", "Microsoft Defender logged no detection in the last 7 days.");
            var line = Get<TextBlock>(window, "StatusAnswerText");
            Assert.Equal(Visibility.Visible, line.Visibility);
            Assert.Equal("Microsoft Defender logged no detection in the last 7 days.", line.Text);
            // Not a live region: the status line's name is the same sentence and is announced, so it is heard once.
            Assert.Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(line));
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(Get<TextBlock>(window, "StatusLabel")));
            // The drawer opens only on View details.
            Assert.NotEqual(Visibility.Visible, Get<Border>(window, "Drawer").Visibility);
        });
    }

    internal static string RecentName(int minutesAgo, string rest)
        => RunLogStore.TimePart(DateTime.UtcNow.AddMinutes(-minutesAgo)) + "_" + rest;

    internal static void WriteRun(string path, bool ended, params string[] cards)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var lines = new List<string> { "===== ENDPOINT TOOLBELT 1.1.1 - RUN LOG =====", "Action    : test", "Run ID    : test", "Started   : 2026-11-12 10:00" };
        lines.Add(ended ? "Ended     : 2026-11-12 10:01" : "Captured at : 2026-11-12 10:01");
        lines.Add("");
        lines.Add("----- RESULT -----");
        lines.AddRange(cards);
        lines.Add("");
        lines.Add("----- TECHNICAL LOG (times in UTC+01:00) -----");
        lines.Add("[PROBLEM] a line of the technical log is never read as a result");
        File.WriteAllLines(path, lines);
    }

    [Fact]
    public void The_tool_search_finds_tools_by_word_start_across_groups_with_a_count_opens_the_first_on_Enter_and_clears_on_Esc()
    {
        OnSta(() =>
        {
            var window = NewWindow();
            Call(window, "ShowPage", PageValue("It"), false);
            Assert.Equal(Visibility.Visible, Get<System.Windows.Controls.Primitives.UniformGrid>(window, "CategoryTiles").Visibility);
            var search = Get<TextBox>(window, "ToolSearch");

            search.Text = "BitLocker";
            // Check compliance settings reads BitLocker too; the tool whose job it is comes first.
            Assert.Equal(new[] { "Back up recovery key", "Check compliance settings" }, ToolRows(window).Select(r => r.Name));
            Assert.Equal("2 tools found: Back up recovery key, Check compliance settings.", Get<TextBlock>(window, "ToolSearchResult").Text);
            // Narrow, the tools found take the tiles' place, each with its group.
            Assert.Equal(Visibility.Collapsed, Get<System.Windows.Controls.Primitives.UniformGrid>(window, "CategoryTiles").Visibility);
            Assert.Equal(Visibility.Visible, Get<StackPanel>(window, "ToolListPanel").Visibility);
            Assert.Equal("Defender & security", ToolRows(window)[0].GroupLine);

            // Word starts only, across the groups: "ime" finds the IME tools, never the clock ("time").
            search.Text = "ime";
            Assert.Equal(new[] { "LogsButton", "FullResetButton" }, ToolRows(window).Select(r => r.Tool.Button).Take(2));
            Assert.DoesNotContain(ToolRows(window), r => r.Tool.Button == "SyncClockButton");
            search.Text = "cache";
            Assert.StartsWith("3 tools found: ", Get<TextBlock>(window, "ToolSearchResult").Text);
            Assert.Equal(new[] { ToolGroup.Apps, ToolGroup.SignIn, ToolGroup.Microsoft365 }, ToolRows(window).Select(r => r.Tool.Group).Distinct().OrderBy(g => g));
            search.Text = "Company Portal";
            Assert.Equal("PortalButton", ToolRows(window)[0].Tool.Button);
            search.Text = "nothing like this";
            Assert.StartsWith("No tool matches", Get<TextBlock>(window, "ToolSearchResult").Text);
            Assert.Equal(Visibility.Collapsed, Get<StackPanel>(window, "ToolListPanel").Visibility);

            // Enter opens the first tool found on its detail page, with only its own button.
            search.Text = "clock";
            Press(search, System.Windows.Input.Key.Enter);
            Assert.Equal("Detail", ItViewOf(window));
            Assert.Equal("Sync clock", Get<TextBlock>(window, "ToolDetailTitle").Text);
            Assert.Equal("title", Field(window, "_focusIntent"));
            Assert.Equal(Visibility.Visible, Get<Button>(window, "SyncClockButton").Visibility);
            Assert.All(ToolCatalog.All.Where(t => t.Button != "SyncClockButton"), t => Assert.Equal(Visibility.Collapsed, Get<Button>(window, t.Button).Visibility));
            // The search stays at the top.
            Assert.Equal(Visibility.Visible, search.Visibility);

            // Esc clears the search, and the tiles come back.
            Press(search, System.Windows.Input.Key.Escape);
            Assert.Equal("", search.Text);
            Assert.Equal("", Get<TextBlock>(window, "ToolSearchResult").Text);
            Assert.Equal("Tiles", ItViewOf(window));
            Assert.Equal(Visibility.Visible, Get<System.Windows.Controls.Primitives.UniformGrid>(window, "CategoryTiles").Visibility);
        });
    }

    [Fact]
    public void Ctrl_F_and_Ctrl_K_go_to_the_search_and_Alt_Left_and_Backspace_go_back()
    {
        OnSta(() =>
        {
            var window = NewWindow();
            Assert.Equal(Visibility.Visible, Get<StackPanel>(window, "HomePage").Visibility);
            System.Windows.Input.Keyboard.ClearFocus();
            // Ctrl is not down here, so the handler is asked directly with its keys.
            Call(window, "FocusToolSearch");
            Assert.Equal(Visibility.Visible, Get<StackPanel>(window, "ItPage").Visibility);
            Assert.Equal("search", Field(window, "_focusIntent"));
            Call(window, "OpenTool", ToolCatalog.ByButton("TeamsCacheButton"), true);
            Assert.True((bool)Invoke(window, "GoBack"));
            Assert.Equal("List", ItViewOf(window));
            Assert.True((bool)Invoke(window, "GoBack"));
            Assert.Equal("Tiles", ItViewOf(window));
            Assert.False((bool)Invoke(window, "GoBack"));
            // A problem path goes back to the four paths.
            Call(window, "ShowProblem", Problem.CantSignIn, false);
            Assert.True((bool)Invoke(window, "GoBack"));
            Assert.Equal(Visibility.Visible, Get<StackPanel>(window, "ProblemsPage").Visibility);
            // Backspace goes back only outside a text box.
            Call(window, "ShowProblem", Problem.CantSignIn, false);
            Press(Get<Button>(window, "BackButton"), System.Windows.Input.Key.Back);
            Assert.Equal(Visibility.Visible, Get<StackPanel>(window, "ProblemsPage").Visibility);
        });
    }

    [Fact]
    public void A_tile_shows_its_tools_with_their_last_runs_a_tool_its_detail_and_Back_restores_the_list_and_the_tiles()
    {
        OnSta(() =>
        {
            using var folder = new TestFolder();
            var window = NewWindow();
            window.RunsFolderOverride = folder.Path;
            // Runs of the last hours: a tool row and a tile show only a result of the last 24 hours.
            WriteRun(folder.Combine(RecentName(90, "SyncClock_ab12cd34.log")), true, "[OK] The clock was synchronised");
            WriteRun(folder.Combine(RecentName(60, "ClearTeamscache_local-0a1b2c3d.log")), true, "[CHECK] Some items in use were left alone", "[OK] The Teams cache was cleared");
            Call(window, "ShowPage", PageValue("It"), false);
            // Each tile: its tools and how their last runs ended, in words.
            Assert.Equal("4 tools · last run OK", Get<TextBlock>(window, "WindowsTileSummary").Text);
            Assert.Equal("5 tools · 1 warning", Get<TextBlock>(window, "MicrosoftTileSummary").Text);
            Assert.Equal("2 tools", Get<TextBlock>(window, "SignInTileSummary").Text);
            Assert.Equal("4 tools", Get<TextBlock>(window, "DefenderTileSummary").Text);
            Assert.Equal("5 tools", Get<TextBlock>(window, "AppsTileSummary").Text);
            Assert.Equal("4 tools", Get<TextBlock>(window, "NetworkTileSummary").Text);
            Assert.Equal("Microsoft 365, 5 tools, 1 warning", AutomationProperties.GetName(Get<Button>(window, "MicrosoftTile")));
            // The recent runs say how each ended, and name a check in the app as people know it.
            var recent = ((IEnumerable<RecentRunView>)Get<ItemsControl>(window, "RecentRuns").ItemsSource).ToList();
            // "<time> · <tool> · <how it ended>", without the reference.
            Assert.Matches(@"^(yesterday |\d{1,2} [A-Z][a-z]{2} (\d{4} )?)?\d{2}:\d{2} · Clear Teams cache · Warning$", recent[0].Text);
            Assert.EndsWith(" · Sync clock · OK", recent[1].Text);
            Assert.DoesNotContain("local-0a1b2c3d", recent[0].Text);
            Assert.DoesNotContain("ab12cd34", recent[1].Text);

            Call(window, "OnCategoryTile", Get<Button>(window, "MicrosoftTile"), new RoutedEventArgs());
            Assert.Equal("List", ItViewOf(window));
            Assert.Equal("list", Field(window, "_focusIntent"));
            Assert.Equal(new[] { "Clear Teams cache", "Start Word in safe mode", "Start Excel in safe mode", "Start Outlook (classic) in safe mode", "Start PowerPoint in safe mode" }, ToolRows(window).Select(r => r.Name));
            Assert.Equal("", ToolRows(window)[1].ResultText);
            Assert.StartsWith("WARNING · ", ToolRows(window)[0].ResultText);
            Assert.Equal(Visibility.Visible, Get<DockPanel>(window, "ItBackBar").Visibility);
            Assert.Equal(Visibility.Collapsed, Get<System.Windows.Controls.Primitives.UniformGrid>(window, "CategoryTiles").Visibility);

            Call(window, "OnToolRow", new Button { DataContext = ToolRows(window)[0] }, new RoutedEventArgs());
            Assert.Equal("Detail", ItViewOf(window));
            Assert.Equal("Clear Teams cache", Get<TextBlock>(window, "ToolDetailTitle").Text);
            // No admin-rights fact; it has no typical time of a minute or more, so only what it closes.
            Assert.Null(window.FindName("FactRights"));
            Assert.Contains("Closes Teams", Get<TextBlock>(window, "FactChanges").Text);
            Assert.Equal(Visibility.Collapsed, Get<StackPanel>(window, "FactTimePanel").Visibility);
            Assert.Equal(1, Get<System.Windows.Controls.Primitives.UniformGrid>(window, "ToolFacts").Columns);
            Assert.StartsWith("WARNING · ", Get<TextBlock>(window, "ToolLastRunText").Text);
            // No reference on the detail page; it stays in View details, the run log and Copy details for IT.
            Assert.DoesNotContain("local-0a1b2c3d", Get<TextBlock>(window, "ToolLastRunText").Text);
            Assert.Equal(Visibility.Visible, Get<Button>(window, "ToolLastRunLink").Visibility);
            // The status of a run of this tool sits on its detail page while it shows.
            Call(window, "StartCheck", "Clear Teams cache");
            Call(window, "SetStatus", "Clear Teams cache: done.", Tone.Success, false, true);
            Assert.Same(Get<Border>(window, "DetailStatusHost"), Get<StackPanel>(window, "StatusPanel").Parent);

            // Back: the list again, with the focus on the tool's row; then the tiles, on the group's tile.
            Assert.True((bool)Invoke(window, "ItBack"));
            Assert.Equal("List", ItViewOf(window));
            Assert.Equal("row:TeamsCacheButton", Field(window, "_focusIntent"));
            Assert.Equal(5, ToolRows(window).Count);
            Assert.Same(Get<Border>(window, "MainStatusHost"), Get<StackPanel>(window, "StatusPanel").Parent);
            Assert.True((bool)Invoke(window, "ItBack"));
            Assert.Equal("Tiles", ItViewOf(window));
            Assert.Equal("tile:Microsoft365", Field(window, "_focusIntent"));
            Assert.False((bool)Invoke(window, "ItBack"));
        });
    }

    [Fact]
    public void A_problem_paths_tool_name_opens_its_detail_page_and_its_Run_button_starts_it_there()
    {
        OnSta(() =>
        {
            var window = NewWindow();
            var pressed = new List<string>();
            window.ToolInvoker = b => pressed.Add(b.Name);
            Get<TextBox>(window, "ToolSearch").Text = "Teams";
            Call(window, "ShowProblem", Problem.CantSignIn, false);
            var rows = ((IEnumerable<ToolRowView>)Get<ItemsControl>(window, "ProblemTools").ItemsSource).ToList();
            Assert.Equal(new[] { "Refresh work account sign-in", "Reset sign-in cache", "Clear Teams cache" }, rows.Select(r => r.Name));
            // Run on the path presses the tool's own button (one click, its usual confirmation); the page stays.
            Call(window, "OnToolRowRun", new Button { DataContext = rows[2] }, new RoutedEventArgs());
            Assert.Equal(new[] { "TeamsCacheButton" }, pressed);
            Assert.Equal(Visibility.Visible, Get<StackPanel>(window, "ProblemPage").Visibility);
            // The name opens the tool's detail page in its group (an earlier search is cleared).
            Call(window, "OnToolRow", new Button { DataContext = rows[1] }, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, Get<StackPanel>(window, "ItPage").Visibility);
            Assert.Equal("Detail", ItViewOf(window));
            Assert.Equal("Reset sign-in cache", Get<TextBlock>(window, "ToolDetailTitle").Text);
            Assert.Equal("", Get<TextBox>(window, "ToolSearch").Text);
            Assert.Equal("title", Field(window, "_focusIntent"));
            // Back from there: the tool's group.
            Assert.True((bool)Invoke(window, "ItBack"));
            Assert.Equal(new[] { "Refresh work account sign-in", "Reset sign-in cache" }, ToolRows(window).Select(r => r.Name));
        });
    }

    [Fact]
    public void Every_For_IT_row_has_a_Run_button_that_presses_the_tools_own_button_with_its_rules()
    {
        OnSta(() =>
        {
            var window = NewWindow();
            var pressed = new List<Button>();
            window.ToolInvoker = pressed.Add;
            Call(window, "ShowPage", PageValue("It"), false);
            // Every tool, through the search for all of its group's rows, narrow.
            foreach (var group in new[] { "AppsTile", "SignInTile", "MicrosoftTile", "WindowsTile", "DefenderTile", "NetworkTile" })
            {
                Call(window, "OnCategoryTile", Get<Button>(window, group), new RoutedEventArgs());
                foreach (var row in ToolRows(window))
                {
                    var own = Get<Button>(window, row.Tool.Button);
                    // The same button as the detail page: its word, its red outline for the heavy tools, a name of its own.
                    Assert.Same(own, row.RunTarget);
                    Assert.Equal(own.Content, row.RunText);
                    Assert.Equal("ToolRowRun_" + row.Tool.Button.Replace("Button", ""), row.RunId);
                    Assert.Equal(row.RunText + ": " + row.Tool.Name, row.RunName);
                    Assert.NotEqual(AutomationProperties.GetName(own), row.RunName);
                    Assert.Same(Application.Current.FindResource(row.Tool.Heavy ? "DangerRunButton" : "RunButton"), row.RunStyle);
                    Call(window, "OnToolRowRun", new Button { DataContext = row }, new RoutedEventArgs());
                    Assert.Same(own, pressed.Last());
                    // Narrow, the list stays: the status shows below it.
                    Assert.Equal("List", ItViewOf(window));
                }
                Call(window, "ItBack");
            }
            Assert.Equal(ToolCatalog.All.Select(t => t.Button).OrderBy(b => b), pressed.Select(b => b.Name).OrderBy(b => b));

            // The row's button is laid out beside the name, bound to the tool's own button: off while
            // something runs (the same rule), with the same help text.
            Call(window, "OnCategoryTile", Get<Button>(window, "WindowsTile"), new RoutedEventArgs());
            AccessibilityTests.Layout(window);
            var list = Get<ItemsControl>(window, "ToolList");
            var container = (ContentPresenter)list.ItemContainerGenerator.ContainerFromIndex(0);
            var run = (Button)container.ContentTemplate.FindName("RowRun", container);
            var name = (Button)container.ContentTemplate.FindName("RowName", container);
            Assert.Equal("Sync clock", run.Content);
            Assert.Equal("ToolRowRun_SyncClock", AutomationProperties.GetAutomationId(run));
            Assert.Equal("ToolRow_SyncClock", AutomationProperties.GetAutomationId(name));
            Assert.Equal(AutomationProperties.GetHelpText(Get<Button>(window, "SyncClockButton")), AutomationProperties.GetHelpText(run));
            Assert.True(run.IsEnabled);
            Assert.True(run.IsTabStop && run.Focusable);
            Assert.True(name.TranslatePoint(new Point(0, 0), run).X < 0);
            Call(window, "ShowBusy", true);
            Assert.False(Get<Button>(window, "SyncClockButton").IsEnabled);
            Assert.False(run.IsEnabled);
            Call(window, "ShowBusy", false);
            Assert.True(run.IsEnabled);
            // The heavy tool's row keeps the red outline.
            var repair = (ContentPresenter)list.ItemContainerGenerator.ContainerFromIndex(2);
            Assert.Same(Application.Current.FindResource("DangerRunButton"), ((Button)repair.ContentTemplate.FindName("RowRun", repair)).Style);
        });
    }

    [Fact]
    public void A_rows_Run_button_presses_the_tools_button_and_the_list_stays()
    {
        OnSta(() =>
        {
            var window = NewWindow();
            var pressed = new List<string>();
            window.ToolInvoker = b => pressed.Add(b.Name);
            Call(window, "OpenTool", ToolCatalog.ByButton("CheckWuButton"), true);
            Assert.True((bool)Invoke(window, "ItBack"));
            var sync = ToolRows(window).Single(r => r.Tool.Button == "SyncClockButton");
            Call(window, "OnToolRowRun", new Button { DataContext = sync }, new RoutedEventArgs());
            Assert.Equal(new[] { "SyncClockButton" }, pressed);
            // One column at every size: the list stays, the run's status shows below it.
            Assert.Equal("List", ItViewOf(window));
            Assert.Equal(Visibility.Collapsed, Get<Border>(window, "ToolDetail").Visibility);
        });
    }

    [Fact]
    public void A_tools_detail_page_shows_only_the_status_of_its_own_tool()
    {
        OnSta(() =>
        {
            var window = NewWindow();
            var panel = Get<StackPanel>(window, "StatusPanel");
            // A run of Sync clock.
            Call(window, "OpenTool", ToolCatalog.ByButton("SyncClockButton"), true);
            Call(window, "SetStatusTool", ToolCatalog.ByButton("SyncClockButton"));
            Call(window, "SetStatus", "Sync clock: the clock was synchronised.", Tone.Success, false, true);
            Assert.Same(Get<Border>(window, "DetailStatusHost"), panel.Parent);
            Assert.Equal(Visibility.Visible, Get<Border>(window, "MainStatusHost").Visibility);
            // Another tool's page: nothing of Sync clock's status.
            Call(window, "OpenTool", ToolCatalog.ByButton("BackupKeyButton"), true);
            Assert.Same(Get<Border>(window, "MainStatusHost"), panel.Parent);
            Assert.Equal(Visibility.Collapsed, Get<Border>(window, "MainStatusHost").Visibility);
            Assert.Null(Get<Border>(window, "DetailStatusHost").Child);
            // A status of no tool (the device report) is not shown on a detail page either.
            Call(window, "SetStatusTool", (ToolInfo)null);
            Call(window, "OpenTool", ToolCatalog.ByButton("SyncClockButton"), true);
            Assert.Equal(Visibility.Collapsed, Get<Border>(window, "MainStatusHost").Visibility);
            // The list and the tiles show the status below the page as before.
            Assert.True((bool)Invoke(window, "ItBack"));
            Assert.Equal(Visibility.Visible, Get<Border>(window, "MainStatusHost").Visibility);
            Assert.Same(Get<Border>(window, "MainStatusHost"), panel.Parent);
            // A message of Open IME logs belongs to it.
            Call(window, "LogsMessage", "The IME Logs folder does not exist on this device.", Tone.Warning);
            Call(window, "OpenTool", ToolCatalog.ByButton("LogsButton"), true);
            Assert.Same(Get<Border>(window, "DetailStatusHost"), panel.Parent);
        });
    }

    [Fact]
    public void The_mascot_follows_the_state_on_Home_and_goes_only_with_high_contrast()
    {
        OnSta(() =>
        {
            var window = NewWindow();
            window.WindowsAnimations = false;
            window.WindowShownOverride = true;
            var mascot = Get<HelperCharacter>(window, "Mascot");
            Assert.Equal(Visibility.Visible, mascot.Visibility);
            // Before the first check: working.
            Assert.Equal(HelperMood.Working, mascot.Mood);
            var clean = new List<HealthResult> { new() { Name = HealthChecks.Clock, Status = HealthStatus.Pass, Detail = "In sync" } };
            Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, clean, new DsregInfo(), false);
            Assert.Equal(HelperMood.Idle, mascot.Mood);
            Assert.Equal(Visibility.Collapsed, Get<TextBlock>(window, "SeverityWord").Visibility);
            Assert.Equal(HomeSummary.CleanTitle, Get<TextBlock>(window, "MainFindingTitle").Text);
            // Just checked: the happy bounce for a moment.
            SetField(window, "_doneUntilUtc", DateTime.UtcNow.AddMinutes(1));
            Call(window, "ApplyHelpers");
            Assert.Equal(HelperMood.Done, mascot.Mood);
            // The checks run again: working.
            SetField(window, "_checkingHealth", true);
            Call(window, "ApplyHelpers");
            Assert.Equal(HelperMood.Working, mascot.Mood);
            SetField(window, "_checkingHealth", false);
            SetField(window, "_doneUntilUtc", DateTime.MinValue);
            // Something needs attention: the severity written out and the concerned look.
            var warn = new List<HealthResult> { new() { Name = HealthChecks.Clock, Status = HealthStatus.Warn, Detail = "Off by 10 minutes" } };
            Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, warn, new DsregInfo(), false);
            Assert.Equal(HelperMood.Attention, mascot.Mood);
            Assert.Equal("WARNING", Get<TextBlock>(window, "SeverityWord").Text);
            Assert.StartsWith("Needs attention: ", AutomationProperties.GetName(Get<TextBlock>(window, "MainFindingTitle")));
            // Just checked with a problem on the page: no bounce, the concerned look stays.
            SetField(window, "_doneUntilUtc", DateTime.UtcNow.AddMinutes(1));
            Call(window, "ApplyHelpers");
            Assert.Equal(HelperMood.Attention, mascot.Mood);
            SetField(window, "_doneUntilUtc", DateTime.MinValue);
            // The service cannot be reached: error, and the severity says PROBLEM.
            Invoke(window, "ApplyHealth", null, true, clean, new DsregInfo(), false);
            Assert.Equal(HelperMood.Error, mascot.Mood);
            Assert.Equal("PROBLEM", Get<TextBlock>(window, "SeverityWord").Text);
            // No animation without Windows animations: still poses.
            Assert.False(mascot.IsAnimating);
            window.WindowsAnimations = true;
            Call(window, "ApplyHelpers");
            Assert.True(mascot.IsAnimating);
            window.WindowsAnimations = false;
            Call(window, "ApplyHelpers");
            Assert.False(mascot.IsAnimating);

            // No setting and no policy turns it off.
            Assert.Null(window.FindName("HelperSettingBox"));
            Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, clean, new DsregInfo(), false);
            Assert.Equal(Visibility.Visible, mascot.Visibility);

            // High contrast hides it and keeps it still; the words stay.
            window.WindowsAnimations = true;
            SetField(window, "_highContrast", true);
            Call(window, "ApplyHelpers");
            Assert.Equal(Visibility.Collapsed, mascot.Visibility);
            Assert.False(mascot.IsAnimating);
            Assert.Equal(Visibility.Visible, Get<TextBlock>(window, "MainFindingTitle").Visibility);
        });
    }

    [Fact]
    public void The_one_mascot_is_on_every_page_at_rest_and_follows_a_run()
    {
        OnSta(() =>
        {
            var window = NewWindow();
            window.WindowsAnimations = false;
            window.WindowShownOverride = true;
            var mascot = Get<HelperCharacter>(window, "Mascot");
            var parent = mascot.Parent;
            // One mascot, never moved: the same parent, size and place on every page and view.
            foreach (var view in new Action[]
            {
                () => Call(window, "ShowPage", PageValue("Problems"), false),
                () => Call(window, "ShowProblem", Problem.CantConnect, false),
                () => Call(window, "ShowPage", PageValue("Apps"), false),
                () => Call(window, "OnNavIt", window, new RoutedEventArgs()),
                () => Call(window, "OnCategoryTile", Get<Button>(window, "AppsTile"), new RoutedEventArgs()),
                () => Call(window, "OpenTool", ToolCatalog.ByButton("SyncClockButton"), false),
                () => Call(window, "ShowPage", PageValue("Home"), false),
            })
            {
                view();
                Assert.Same(parent, mascot.Parent);
                Assert.Equal(Visibility.Visible, mascot.Visibility);
                Assert.Equal(HelperRules.Size, mascot.Width);
            }
            // The old helpers are gone.
            foreach (var old in new[] { "HomeHelper", "PageHelper", "RunHelper", "ProblemsHelperHost", "PathHelperHost", "ItHelperHost" }) { Assert.Null(window.FindName(old)); }

            // Working while something runs, then the tone it ended with for a few seconds, then idle again.
            Call(window, "ShowPage", PageValue("Problems"), false);
            Assert.Equal(HelperMood.Idle, mascot.Mood);
            SetField(window, "_busy", true);
            Call(window, "ShowBusy", true);
            Assert.Equal(HelperMood.Working, mascot.Mood);
            // The progress bar and the status text carry the information; the mascot only decorates.
            Assert.Equal(Visibility.Visible, Get<ProgressBar>(window, "Progress").Visibility);
            SetField(window, "_busy", false);
            Call(window, "SetStatus", "Done.", Tone.Success, false, true);
            Assert.Equal("OK", Get<TextBlock>(window, "StatusWord").Text);
            Call(window, "ShowBusy", false);
            Assert.Equal(HelperMood.Done, mascot.Mood);
            Call(window, "SetStatus", "Sync clock could not finish.", Tone.Error, false, true);
            Assert.Equal(HelperMood.Error, mascot.Mood);
            SetField(window, "_runHelperUntilUtc", DateTime.MinValue);
            Call(window, "ApplyHelpers");
            Assert.Equal(Visibility.Visible, mascot.Visibility);
            Assert.Equal(HelperMood.Idle, mascot.Mood);
            Assert.False(mascot.IsAnimating);
            window.WindowsAnimations = true;
            Call(window, "ApplyHelpers");
            Assert.True(mascot.IsAnimating);
            // Minimized or not visible: paused.
            window.WindowShownOverride = false;
            Call(window, "ApplyHelpers");
            Assert.False(mascot.IsAnimating);
            window.WindowShownOverride = true;
            Call(window, "ApplyHelpers");
            Assert.True(mascot.IsAnimating);
            window.WindowsAnimations = false;
        });
    }

    [Fact]
    public void Switching_pages_keeps_the_window_size_narrow_wide_and_maximized()
    {
        OnSta(() =>
        {
            var window = NewWindow();
            Assert.Equal(SizeToContent.Manual, window.SizeToContent);
            Assert.Equal(WindowLayout.DefaultWindowWidth, window.Width);
            Assert.Equal(WindowLayout.DefaultWindowHeight, window.Height);
            var switches = new (string Label, Action Go)[]
            {
                ("Choose a problem", () => Call(window, "ShowPage", PageValue("Problems"), false)),
                ("a problem path", () => Call(window, "ShowProblem", Problem.CantSignIn, false)),
                ("the app path", () => Call(window, "ShowPage", PageValue("Apps"), false)),
                ("For IT", () => Call(window, "OnNavIt", window, new RoutedEventArgs())),
                ("a group", () => Call(window, "OnCategoryTile", Get<Button>(window, "AppsTile"), new RoutedEventArgs())),
                ("a tool", () => Call(window, "OpenTool", ToolCatalog.ByButton("FullResetButton"), true)),
                ("a search", () => Get<TextBox>(window, "ToolSearch").Text = "cache"),
                ("Home with every check", () =>
                {
                    Call(window, "ShowPage", PageValue("Home"), false);
                    Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, CompactLayoutTests.Health(warn: HealthChecks.Clock, fail: HealthChecks.FreeSpace), new DsregInfo(), false);
                    Call(window, "OnHealthToggle", window, new RoutedEventArgs());
                }),
            };
            foreach (var (layout, width) in new[] { ("narrow", AccessibilityTests.Narrow), ("wide", AccessibilityTests.Wide) })
            {
                foreach (var state in new[] { WindowState.Normal, WindowState.Maximized })
                {
                    window.WindowState = state;
                    foreach (var (label, go) in switches)
                    {
                        go();
                        AccessibilityTests.Layout(window, width);
                        var where = layout + ", " + state + ", " + label;
                        Assert.True(window.Width == WindowLayout.DefaultWindowWidth && window.Height == WindowLayout.DefaultWindowHeight, where + ": " + window.Width + " x " + window.Height);
                        Assert.Equal(SizeToContent.Manual, window.SizeToContent);
                        Assert.Equal(state, window.WindowState);
                    }
                }
            }
            window.WindowState = WindowState.Normal;
        });
    }

    [Fact]
    public void A_page_taller_than_the_window_scrolls_inside_it_with_the_same_viewport_on_every_page()
    {
        OnSta(() =>
        {
            var window = NewWindow();
            double Viewport(out double extent)
            {
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(WindowLayout.DefaultClientWidth, WindowLayout.CompactClientHeight));
                content.Arrange(new Rect(0, 0, WindowLayout.DefaultClientWidth, WindowLayout.CompactClientHeight));
                content.UpdateLayout();
                var scroll = Get<ScrollViewer>(window, "MainScroll");
                extent = scroll.ExtentHeight;
                return scroll.ViewportHeight;
            }
            var home = Viewport(out _);
            // Every check opened on Home: taller than the window, so the page scrolls; the viewport stays.
            Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, CompactLayoutTests.Health(warn: HealthChecks.Clock, fail: HealthChecks.FreeSpace), new DsregInfo(), false);
            Call(window, "OnHealthToggle", window, new RoutedEventArgs());
            foreach (var row in (IEnumerable<HealthRow>)Get<ItemsControl>(window, "HealthList").ItemsSource) { row.Toggle(); }
            Assert.Equal(home, Viewport(out var tall));
            Assert.True(tall > home, "the page with every check opened is " + tall + " high in a viewport of " + home);
            Call(window, "ShowPage", PageValue("Problems"), false);
            Assert.Equal(home, Viewport(out _));
            Call(window, "OpenTool", ToolCatalog.ByButton("RepairWuButton"), true);
            Assert.Equal(home, Viewport(out _));
        });
    }

    [Fact]
    public void Home_and_the_app_path_show_the_same_finding_and_step_for_the_same_diagnosis()
    {
        OnSta(() =>
        {
            var window = NewWindow();
            var now = DateTime.UtcNow;
            AppEvidence App(string id, string name, string verdict, string code = "") => new()
            {
                DiagnosisId = "diag0001", AppId = id, Name = name, Scope = "device", Intent = "required", Revision = 1, Verdict = verdict, Code = code,
                ObservedText = now.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            };
            var outcome = new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "diag0001", Result = ResultCode.Completed, ExitCode = 0 }.With(new[]
            {
                new EngineRecord { Type = "app-diagnosis", App = App("11111111-2222-3333-4444-555555555555", "Contoso Agent", "PACKAGING", "0x87D1041C"), Utc = now },
                new EngineRecord { Type = "app-diagnosis", App = App("22222222-3333-4444-5555-666666666666", "Contoso Viewer", "OK"), Utc = now },
                new EngineRecord { Type = "apps-diagnosis", Closing = new DiagnosisClose { Present = true, Records = 2, Listed = 2, UserScope = true }, Utc = now },
                new EngineRecord { Type = "summary", Exit = 0, Utc = now },
            });
            Call(window, "SetDiagnosis", AppDecisions.From(outcome, now), outcome);

            var title = Get<TextBlock>(window, "MainFindingTitle").Text;
            Assert.Equal("Contoso Agent: installed, but not detected", title);
            Assert.Equal(title, Get<TextBlock>(window, "AppDetailTitle").Text);
            Assert.Equal("Copy details for IT", Get<Button>(window, "PrimaryStepButton").Content);
            Assert.Equal("Copy details for IT", Get<Button>(window, "AppStepButton").Content);
            Assert.StartsWith("Status: Failed", Get<TextBlock>(window, "AppDetailState").Text);
            // The run's line is short on the page; the whole line is its automation name.
            var runText = Get<TextBlock>(window, "DiagnosisRunText");
            Assert.StartsWith("Checked", runText.Text);
            Assert.StartsWith("Diagnosis: finished at", System.Windows.Automation.AutomationProperties.GetName(runText));
            // Only the app that needs attention is listed at first, under its heading; the detected one behind the link.
            var rows = ((IEnumerable<AppRowView>)Get<ItemsControl>(window, "AppList").ItemsSource).ToList();
            Assert.Equal(new[] { "Contoso Agent" }, rows.Select(r => r.Label));
            Assert.Equal("Selected", rows[0].SelectedTag);
            Assert.Equal("Apps that need attention (1)", Get<TextBlock>(window, "AppListHeading").Text);
            Assert.Equal("Show all 2 apps", Get<Button>(window, "AppListToggle").Content);
            Call(window, "ShowPage", PageValue("Apps"), false);
            // Show all opens the all-apps view: the list alone, under its own heading.
            Call(window, "OnAppListToggle", window, new RoutedEventArgs());
            rows = ((IEnumerable<AppRowView>)Get<ItemsControl>(window, "AppList").ItemsSource).ToList();
            Assert.Equal(new[] { "Contoso Agent", "Contoso Viewer" }, rows.Select(r => r.Label));
            Assert.Equal("Detected", rows[1].ObservationWord);
            Assert.Equal("All apps (2)", Get<TextBlock>(window, "AppListHeading").Text);
            Assert.Equal(Visibility.Collapsed, Get<StackPanel>(window, "AppsTop").Visibility);
            Assert.Equal(Visibility.Collapsed, Get<Border>(window, "AppDetail").Visibility);
            Assert.Equal(Visibility.Collapsed, Get<StackPanel>(window, "AppsRetry").Visibility);
            Assert.Equal(Visibility.Collapsed, Get<Button>(window, "AppListToggle").Visibility);
            Assert.Equal("Back to the apps that need attention", System.Windows.Automation.AutomationProperties.GetName(Get<Button>(window, "BackButton")));
            // Two apps: no search box (it shows from AppDecisions.SearchFrom apps on).
            Assert.Equal(Visibility.Collapsed, Get<Grid>(window, "AppSearchBox").Visibility);
            // Choosing an app opens its own view: its details alone, the record behind Technical details.
            Call(window, "OnAppSelected", new Button { DataContext = rows[1] }, new RoutedEventArgs());
            Assert.Equal(Visibility.Collapsed, Get<StackPanel>(window, "AppsTop").Visibility);
            Assert.Equal(Visibility.Collapsed, Get<StackPanel>(window, "AppsListPanel").Visibility);
            Assert.Equal(Visibility.Collapsed, Get<WrapPanel>(window, "AppsActions").Visibility);
            Assert.Equal(Visibility.Visible, Get<Border>(window, "AppDetail").Visibility);
            Assert.Equal(Visibility.Collapsed, Get<StackPanel>(window, "AppTechPanel").Visibility);
            Assert.Equal("Back to the apps that need attention", System.Windows.Automation.AutomationProperties.GetName(Get<Button>(window, "BackButton")));
            Call(window, "OnAppTechToggle", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, Get<StackPanel>(window, "AppTechPanel").Visibility);
            Assert.Equal("Hide technical details", Get<Button>(window, "AppTechToggle").Content);
            // Back returns to the list, the details hidden again.
            Call(window, "OnPathBack", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, Get<StackPanel>(window, "AppsListPanel").Visibility);
            Assert.Equal(Visibility.Collapsed, Get<Border>(window, "AppDetail").Visibility);
            Assert.Equal(new[] { "Contoso Agent" }, ((IEnumerable<AppRowView>)Get<ItemsControl>(window, "AppList").ItemsSource).Select(r => r.Label));
            // Back from the all-apps view returns to the page too.
            Call(window, "OnAppListToggle", window, new RoutedEventArgs());
            Call(window, "OnPathBack", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, Get<Grid>(window, "AppsPage").Visibility);
            Assert.Equal("Apps that need attention (1)", Get<TextBlock>(window, "AppListHeading").Text);
            Assert.Equal(Visibility.Collapsed, Get<Border>(window, "AppDetail").Visibility);
            // The Retry warning is said on the app path before the button.
            Assert.Equal(AppDecisions.RetryCaveat, Get<TextBlock>(window, "RetryCaveatText").Text);
        });
    }

    [Fact]
    public void Many_apps_get_a_search_over_all_of_them_that_Esc_clears()
    {
        OnSta(() =>
        {
            var window = NewWindow();
            var now = DateTime.UtcNow;
            var names = new[] { "Contoso Agent", "Contoso Drive Mappings", "Contoso Viewer", "Fabrikam Notes", "Fabrikam Drive Tool", "Litware Reader", "Litware Writer" };
            var records = names.Select((name, i) => new EngineRecord
            {
                Type = "app-diagnosis",
                Utc = now,
                App = new AppEvidence
                {
                    DiagnosisId = "diag0002", AppId = (i + 1).ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + "-2222-3333-4444-555555555555", Name = name,
                    Scope = "device", Intent = "required", Revision = 1, Verdict = i == 0 ? "PACKAGING" : "OK", Code = i == 0 ? "0x87D1041C" : "",
                    ObservedText = now.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                },
            }).ToList();
            records.Add(new EngineRecord { Type = "apps-diagnosis", Closing = new DiagnosisClose { Present = true, Records = names.Length, Listed = names.Length, UserScope = true }, Utc = now });
            records.Add(new EngineRecord { Type = "summary", Exit = 0, Utc = now });
            var outcome = new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "diag0002", Result = ResultCode.Completed, ExitCode = 0 }.With(records);
            Call(window, "SetDiagnosis", AppDecisions.From(outcome, now), outcome);
            Call(window, "ShowPage", PageValue("Apps"), false);
            List<string> Rows() => ((IEnumerable<AppRowView>)Get<ItemsControl>(window, "AppList").ItemsSource).Select(r => r.Label).ToList();

            // Seven apps: a search, behind Find another app; at first only the app that needs attention is listed.
            Assert.Equal(Visibility.Collapsed, Get<Grid>(window, "AppSearchBox").Visibility);
            Assert.Equal(Visibility.Visible, Get<Button>(window, "AppFindLink").Visibility);
            Assert.Equal(new[] { "Contoso Agent" }, Rows());
            Call(window, "OnAppFind", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, Get<Grid>(window, "AppSearchBox").Visibility);
            Assert.Equal(Visibility.Collapsed, Get<Button>(window, "AppFindLink").Visibility);
            // A search looks through every app, also the detected ones, and hides the link between the lists.
            var search = Get<TextBox>(window, "AppSearch");
            search.Text = "drive";
            Assert.Equal(new[] { "Contoso Drive Mappings", "Fabrikam Drive Tool" }, Rows());
            Assert.Equal("2 apps of 7 found.", Get<TextBlock>(window, "AppSearchResult").Text);
            Assert.Equal(Visibility.Collapsed, Get<Button>(window, "AppListToggle").Visibility);
            Assert.Equal(Visibility.Hidden, Get<TextBlock>(window, "AppSearchLabel").Visibility);
            // Every word must match.
            search.Text = "fabrikam drive";
            Assert.Equal(new[] { "Fabrikam Drive Tool" }, Rows());
            search.Text = "nothing like this";
            Assert.Empty(Rows());
            Assert.Equal("No app's name holds \"nothing like this\". Esc clears the search.", Get<TextBlock>(window, "AppSearchResult").Text);
            // Esc clears: the short list, the link and the placeholder again.
            Press(search, System.Windows.Input.Key.Escape);
            Assert.Equal("", search.Text);
            Assert.Equal(new[] { "Contoso Agent" }, Rows());
            Assert.Equal(Visibility.Visible, Get<Button>(window, "AppListToggle").Visibility);
            Assert.Equal(Visibility.Collapsed, Get<TextBlock>(window, "AppSearchResult").Visibility);
            Assert.Equal(Visibility.Visible, Get<TextBlock>(window, "AppSearchLabel").Visibility);
            // Esc on the empty search closes it again.
            Press(search, System.Windows.Input.Key.Escape);
            Assert.Equal(Visibility.Collapsed, Get<Grid>(window, "AppSearchBox").Visibility);
            Assert.Equal(Visibility.Visible, Get<Button>(window, "AppFindLink").Visibility);
        });
    }

    [Fact]
    public void The_short_list_shows_three_apps_and_Show_all_lists_every_app_alphabetically()
    {
        OnSta(() =>
        {
            var window = NewWindow();
            var now = DateTime.UtcNow;
            var names = new[] { "Contoso One", "Contoso Two", "Contoso Three", "Contoso Four", "Contoso Five", "Fabrikam Fine", "Litware Fine" };
            var records = names.Select((name, i) => new EngineRecord
            {
                Type = "app-diagnosis",
                Utc = now,
                App = new AppEvidence
                {
                    DiagnosisId = "diag0003", AppId = (i + 1).ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + "-2222-3333-4444-555555555555", Name = name,
                    Scope = "device", Intent = "required", Revision = 1, Verdict = i < 5 ? "PACKAGING" : "OK", Code = i < 5 ? "0x87D1041C" : "",
                    ObservedText = now.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                },
            }).ToList();
            records.Add(new EngineRecord { Type = "apps-diagnosis", Closing = new DiagnosisClose { Present = true, Records = names.Length, Listed = names.Length, UserScope = true }, Utc = now });
            records.Add(new EngineRecord { Type = "summary", Exit = 0, Utc = now });
            var outcome = new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "diag0003", Result = ResultCode.Completed, ExitCode = 0 }.With(records);
            Call(window, "SetDiagnosis", AppDecisions.From(outcome, now), outcome);
            Call(window, "ShowPage", PageValue("Apps"), false);
            List<string> Rows() => ((IEnumerable<AppRowView>)Get<ItemsControl>(window, "AppList").ItemsSource).Select(r => r.Label).ToList();

            // Five need attention: three rows, the heading says so, and the links sit above the list.
            Assert.Equal(AppDecisions.ShortListRows, Rows().Count);
            Assert.Equal("Apps that need attention (3 of 5)", Get<TextBlock>(window, "AppListHeading").Text);
            Assert.Equal("Show all 7 apps", Get<Button>(window, "AppListToggle").Content);
            var panel = Get<StackPanel>(window, "AppsListPanel");
            Assert.True(panel.Children.IndexOf(Get<WrapPanel>(window, "AppListLinks")) < panel.Children.IndexOf(Get<Border>(window, "AppListHost")));
            // Show all: every app in alphabetical order.
            Call(window, "OnAppListToggle", window, new RoutedEventArgs());
            Assert.Equal(new[] { "Contoso Five", "Contoso Four", "Contoso One", "Contoso Three", "Contoso Two", "Fabrikam Fine", "Litware Fine" }, Rows());
        });
    }

    /// <summary>A finished diagnosis read at <paramref name="at"/>, with one app observed at <paramref name="observed"/>.</summary>
    internal static RunOutcome Diagnosis(string id, DateTime at, string verdict = null, DateTime? observed = null, string code = "")
    {
        var records = new List<EngineRecord>();
        if (verdict != null)
        {
            records.Add(new EngineRecord
            {
                Type = "app-diagnosis",
                Utc = at,
                App = new AppEvidence
                {
                    DiagnosisId = id, AppId = "11111111-2222-3333-4444-555555555555", Name = "Contoso Agent", Scope = "device", Intent = "required", Revision = 1, Verdict = verdict, Code = code,
                    ObservedText = (observed ?? at.AddMinutes(-5)).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                },
            });
        }
        records.Add(new EngineRecord { Type = "apps-diagnosis", Closing = new DiagnosisClose { Present = verdict != null, Records = records.Count, Listed = records.Count, UserScope = true }, Utc = at });
        records.Add(new EngineRecord { Type = "summary", Exit = 0, Utc = at });
        return new RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = id, Result = ResultCode.Completed, ExitCode = 0 }.With(records);
    }

    [Fact]
    public void A_remedy_turns_the_app_into_awaiting_evidence_and_a_newer_detection_into_App_detected()
    {
        OnSta(() =>
        {
            using var folder = new TestFolder();
            var window = NewWindow();
            UseRemedies(window, folder.Combine(RemedyStore.FileName));
            var now = DateTime.UtcNow;
            var first = Diagnosis("diag0001", now.AddMinutes(-2), "RETRY-HELPS", now.AddHours(-1), "0x80070643");
            Call(window, "SetDiagnosis", AppDecisions.From(first, now), first);
            Call(window, "ShowPage", PageValue("Apps"), false);
            var finding = (AppFinding)Get<Button>(window, "AppStepButton").Tag;
            Assert.Equal("Retry work apps and settings", Get<Button>(window, "AppStepButton").Content);
            Assert.Equal(Visibility.Collapsed, Get<Button>(window, "CheckAgainButton").Visibility);

            // The handover is the remedy: the app awaits evidence and offers Check again (not the step button).
            Call(window, "RecordRemedies", new[] { finding }, RemedyKind.CopyForIt, "", now);
            Assert.Equal(Visibility.Visible, Get<Button>(window, "CheckAgainButton").Visibility);
            Assert.Equal(Visibility.Collapsed, Get<Button>(window, "AppStepButton").Visibility);
            Assert.StartsWith("Post-check: Awaiting evidence · The handover to IT at", Get<TextBlock>(window, "AppDetailPostCheck").Text);
            Assert.Equal("Contoso Agent: awaiting evidence", Get<TextBlock>(window, "MainFindingTitle").Text);
            Assert.Equal("Check this app again", Get<Button>(window, "PrimaryStepButton").Content);
            // The run's state keeps its own line.
            Assert.StartsWith("Checked", Get<TextBlock>(window, "DiagnosisRunText").Text);
            Assert.StartsWith("Diagnosis: finished at", System.Windows.Automation.AutomationProperties.GetName(Get<TextBlock>(window, "DiagnosisRunText")));
            // Kept in the user's file, so it survives a reopen.
            var stored = Assert.Single(RemedyStore.Load(folder.Combine(RemedyStore.FileName), RemedyStore.CurrentUser(), DateTime.UtcNow));
            Assert.Equal("diag0001", stored.DiagnosisId);
            Assert.Equal(RemedyKind.CopyForIt, stored.Kind);

            // The service did not run the check (another action, or the hourly limit): the list stays.
            var busy = new RunOutcome { Action = ToolbeltAction.DiagnoseApps, Result = ResultCode.RateLimited };
            Assert.False((bool)Invoke(window, "ApplyDiagnosis", busy));
            Assert.Equal("Contoso Agent: awaiting evidence", Get<TextBlock>(window, "AppDetailTitle").Text);

            // An unchanged record in a later diagnosis: still awaiting evidence.
            var same = Diagnosis("diag0002", now.AddMinutes(1), "RETRY-HELPS", now.AddHours(-1), "0x80070643");
            Assert.True((bool)Invoke(window, "ApplyDiagnosis", same));
            Assert.StartsWith("Post-check: Awaiting evidence", Get<TextBlock>(window, "AppDetailPostCheck").Text);
            Assert.Contains("checked in diagnosis diag0002", Get<TextBlock>(window, "AppDetailPostCheck").Text);

            // A newer record that says detected: App detected, with its evidence.
            var later = Diagnosis("diag0003", now.AddMinutes(3), "OK", now.AddMinutes(2));
            Assert.True((bool)Invoke(window, "ApplyDiagnosis", later));
            Assert.Equal("App detected: Contoso Agent", Get<TextBlock>(window, "AppDetailTitle").Text);
            Assert.StartsWith("Post-check: App detected", Get<TextBlock>(window, "AppDetailPostCheck").Text);
            Assert.Equal(Visibility.Collapsed, Get<Button>(window, "CheckAgainButton").Visibility);
            var row = Assert.Single((IEnumerable<AppRowView>)Get<ItemsControl>(window, "AppList").ItemsSource);
            Assert.Equal("App detected", row.ObservationWord);
            Assert.Equal(PostCheckState.Detected, Assert.Single(RemedyStore.Load(folder.Combine(RemedyStore.FileName), RemedyStore.CurrentUser(), DateTime.UtcNow)).State);
        });
    }
}
