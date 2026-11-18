using System;
using System.Collections.Generic;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>Bring my window back, its geometry with synthetic monitor layouts and its results with a fake window system.</summary>
public class WindowRescueTests
{
    private static readonly DateTime T0 = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>A laptop screen of 1920 x 1080 with the taskbar at the bottom (primary, 96 DPI).</summary>
    internal static DisplayMonitor Laptop(int dpi = 96) => new() { Index = 1, Primary = true, Bounds = new ScreenRect(0, 0, 1920, 1080), WorkArea = new ScreenRect(0, 0, 1920, 1032), Dpi = dpi };

    /// <summary>A second monitor to the left of the primary one: negative coordinates.</summary>
    internal static DisplayMonitor LeftMonitor(int dpi = 96) => new() { Index = 2, Bounds = new ScreenRect(-2560, 0, 0, 1440), WorkArea = new ScreenRect(-2560, 0, 0, 1400), Dpi = dpi };

    /// <summary>A window system the test decides: monitors (which may change), windows and what placing does.</summary>
    internal sealed class FakeWindows : IWindowApi
    {
        public List<DisplayMonitor> Screens { get; set; } = new() { Laptop() };
        public List<AppWindow> Listed { get; } = new();
        public Dictionary<IntPtr, WindowFacts> State { get; } = new();
        public Dictionary<int, bool?> Elevation { get; } = new();
        public List<(IntPtr Handle, ScreenRect Rect, bool Maximize)> Placed { get; } = new();
        public PlaceResult PlaceAnswer { get; set; } = PlaceResult.Done;

        /// <summary>Runs before each read of the monitors (a monitor that disappears in between).</summary>
        public Action<FakeWindows> BeforeMonitors { get; set; }

        /// <summary>How a placed rectangle lands (a per-monitor aware app rescales itself); default: as given.</summary>
        public Func<ScreenRect, ScreenRect> Lands { get; set; } = r => r;

        public bool SelfElevated { get; set; }

        public Dictionary<int, bool?> UiAccess { get; } = new();

        public bool? HasUiAccess(int processId) => UiAccess.TryGetValue(processId, out var value) ? value : false;

        public IReadOnlyList<DisplayMonitor> Monitors()
        {
            BeforeMonitors?.Invoke(this);
            return Screens.ToList();
        }

        public IReadOnlyList<AppWindow> Windows() => Listed.ToList();

        public WindowFacts Read(IntPtr handle) => State.TryGetValue(handle, out var facts) ? facts : null;

        public List<IntPtr> Fronted { get; } = new();

        public bool BringToFront(IntPtr handle)
        {
            Fronted.Add(handle);
            return State.ContainsKey(handle);
        }

        public PlaceResult Place(IntPtr handle, ScreenRect normalRect, bool maximize)
        {
            Placed.Add((handle, normalRect, maximize));
            if (PlaceAnswer != PlaceResult.Done) { return PlaceAnswer; }
            if (!State.TryGetValue(handle, out var facts)) { return PlaceResult.Gone; }
            var landed = Lands(normalRect);
            State[handle] = new WindowFacts { ProcessId = facts.ProcessId, Rect = landed, NormalRect = landed, Dpi = facts.Dpi };
            return PlaceResult.Done;
        }

        public bool? IsElevated(int processId) => Elevation.TryGetValue(processId, out var value) ? value : false;

        public AppWindow Add(int id, string app, string title, ScreenRect rect, bool minimized = false, ScreenRect? normal = null, int dpi = 96)
        {
            var window = new AppWindow { Handle = new IntPtr(id), ProcessId = 1000 + id, ProcessName = app.ToLowerInvariant(), AppName = app, Title = title };
            Listed.Add(window);
            State[window.Handle] = new WindowFacts { ProcessId = window.ProcessId, Rect = rect, NormalRect = normal ?? rect, Minimized = minimized, Dpi = dpi };
            return window;
        }
    }

    // --- Geometry ----------------------------------------------------------------------------------

    [Fact]
    public void The_title_bar_counts_only_on_a_current_work_area()
    {
        var screens = new List<DisplayMonitor> { Laptop(), LeftMonitor() };
        Assert.True(WindowGeometry.TitleBarOnScreen(ScreenRect.FromSize(100, 100, 800, 600), screens));
        // On the monitor at the left, with negative coordinates.
        Assert.True(WindowGeometry.TitleBarOnScreen(ScreenRect.FromSize(-2000, 200, 800, 600), screens));
        Assert.Equal(2, WindowGeometry.MonitorOf(ScreenRect.FromSize(-2000, 200, 800, 600), screens).Index);
        // Far off-screen, and right of the primary work area where no monitor is.
        Assert.False(WindowGeometry.TitleBarOnScreen(ScreenRect.FromSize(-6000, -6000, 800, 600), screens));
        Assert.False(WindowGeometry.TitleBarOnScreen(ScreenRect.FromSize(1920 + 200, 100, 800, 600), screens));
        // Only the body on screen, the title bar above the top edge: not grabbable.
        Assert.False(WindowGeometry.TitleBarOnScreen(ScreenRect.FromSize(100, -200, 800, 600), screens));
        // Only a sliver of 40 px of the title bar: not enough.
        Assert.False(WindowGeometry.TitleBarOnScreen(ScreenRect.FromSize(1920 - 40 + 8, 100, 800, 600), new List<DisplayMonitor> { Laptop() }));
        // A maximized window's invisible border sticks out by 8 px: still on screen.
        Assert.True(WindowGeometry.TitleBarOnScreen(new ScreenRect(-8, -8, 1928, 1040), new List<DisplayMonitor> { Laptop() }));
        // The taskbar's strip is not a work area.
        Assert.False(WindowGeometry.TitleBarOnScreen(ScreenRect.FromSize(100, 1040, 800, 600), new List<DisplayMonitor> { Laptop() }));
    }

    [Fact]
    public void A_window_keeps_its_size_when_it_fits_and_shrinks_when_it_does_not()
    {
        var laptop = Laptop();
        var kept = WindowGeometry.Fit(ScreenRect.FromSize(-6000, -6000, 800, 600), laptop, 96);
        Assert.Equal(ScreenRect.FromSize(560, 216, 800, 600), kept);
        var big = WindowGeometry.Fit(ScreenRect.FromSize(-3000, 0, 2560, 1400), laptop, 96);
        Assert.Equal(ScreenRect.FromSize(0, 0, 1920, 1032), big);
    }

    [Fact]
    public void A_different_DPI_is_allowed_for_before_the_app_rescales_itself()
    {
        // From a 96 DPI monitor to a 144 DPI laptop screen (150%): 1600 x 900 would land as 2400 x 1350.
        var laptop = Laptop(dpi: 144);
        var target = WindowGeometry.Fit(ScreenRect.FromSize(-2400, 100, 1600, 900), laptop, 96);
        Assert.Equal(1280, target.Width);
        Assert.Equal(688, target.Height);
        Assert.True(target.Width * 1.5 <= laptop.WorkArea.Width && target.Height * 1.5 <= laptop.WorkArea.Height);
        // From 144 to 96: it gets smaller, so it keeps its size.
        var smaller = WindowGeometry.Fit(ScreenRect.FromSize(-2400, 100, 1500, 900), Laptop(), 144);
        Assert.Equal(1500, smaller.Width);
        Assert.Equal(900, smaller.Height);
    }

    [Fact]
    public void Workspace_coordinates_of_the_restore_rectangle_follow_a_taskbar_at_the_left()
    {
        var taskbarLeft = new DisplayMonitor { Index = 1, Primary = true, Bounds = new ScreenRect(0, 0, 1920, 1080), WorkArea = new ScreenRect(48, 0, 1920, 1080) };
        var workspace = ScreenRect.FromSize(100, 100, 800, 600);
        var screen = WindowGeometry.WorkspaceToScreen(workspace, taskbarLeft);
        Assert.Equal(ScreenRect.FromSize(148, 100, 800, 600), screen);
        Assert.Equal(workspace, WindowGeometry.ScreenToWorkspace(screen, taskbarLeft));
    }

    // --- Results -----------------------------------------------------------------------------------

    [Fact]
    public void A_window_far_off_screen_comes_back_on_the_primary_screen()
    {
        var api = new FakeWindows { Screens = new List<DisplayMonitor> { Laptop(), LeftMonitor() } };
        var window = api.Add(1, "Notepad", "Contoso plan - Notepad", ScreenRect.FromSize(-6000, -6000, 800, 600));
        var outcome = WindowRescue.Run(api, window, () => T0);
        Assert.Equal(WindowRescueResult.MovedBack, outcome.Result);
        Assert.False(outcome.BeforeOnScreen);
        Assert.Null(outcome.BeforeMonitor);
        Assert.True(outcome.AfterOnScreen);
        Assert.Equal(1, outcome.AfterMonitor);
        Assert.Equal(ScreenRect.FromSize(560, 216, 800, 600), api.Placed.Single().Rect);

        var (status, tone, card) = WindowRescue.Present(outcome);
        Assert.Equal("The Notepad window is back on your screen.", status);
        Assert.Equal(Tone.Success, tone);
        Assert.Equal("The Notepad window is back on screen 1 of 2", card.Title);
        Assert.Equal("WINDOW run=local-0a1b2c3d time=2026-11-12T10:00:00.000Z ms=0 context=user-app app=notepad result=moved-back before=off-screen before-monitor=none after=on-screen after-monitor=1 minimized=no monitors=2",
            WindowRescue.LogLine(outcome, "local-0a1b2c3d"));
        Assert.Equal("0|local-0a1b2c3d|moved-back", WindowRescue.HistoryResult(outcome, "local-0a1b2c3d"));
        Assert.Equal("2026-11-12 11:00 +01:00  Bring my window back: moved back (0, reference local-0a1b2c3d)", RunWords.HistoryLine("2026-11-12 11:00:00 +01:00;user;WindowBack;0|local-0a1b2c3d|moved-back"));

        // The window title is never in the status, the card, the log line or the history.
        foreach (var text in new[] { status, card.AccessibleText, WindowRescue.LogLine(outcome, "x"), WindowRescue.HistoryResult(outcome, "x") })
        {
            Assert.DoesNotContain("Contoso plan", text);
        }
    }

    [Fact]
    public void A_window_left_on_a_removed_monitor_and_one_whose_monitor_disappears_while_choosing_come_back()
    {
        // Right of the primary work area, where the dock's monitor was.
        var api = new FakeWindows();
        var window = api.Add(2, "Contoso Editor", "Report", ScreenRect.FromSize(1920 + 200, 100, 1200, 800));
        Assert.Equal(WindowRescueResult.MovedBack, WindowRescue.Run(api, window, () => T0).Result);
        Assert.Equal(ScreenRect.FromSize(360, 116, 1200, 800), api.Placed.Single().Rect);

        // On the left monitor when chosen; that monitor is gone right before the move.
        var undock = new FakeWindows { Screens = new List<DisplayMonitor> { Laptop(), LeftMonitor() } };
        var left = undock.Add(3, "Contoso Editor", "Notes", ScreenRect.FromSize(-2000, 200, 800, 600));
        Assert.Equal(WindowRescueResult.AlreadyVisible, WindowRescue.Run(undock, left, () => T0).Result);
        undock.BeforeMonitors = f => f.Screens = new List<DisplayMonitor> { Laptop() };
        var outcome = WindowRescue.Run(undock, left, () => T0);
        Assert.Equal(WindowRescueResult.MovedBack, outcome.Result);
        Assert.Equal(1, outcome.AfterMonitor);
        Assert.Equal(1, outcome.Monitors);
    }

    [Fact]
    public void A_minimized_window_with_an_off_screen_restore_rectangle_is_restored_onto_the_screen()
    {
        var api = new FakeWindows();
        var window = api.Add(4, "Notepad", "Draft", ScreenRect.FromSize(-32000, -32000, 160, 28), minimized: true, normal: ScreenRect.FromSize(-5000, 300, 900, 700));
        var outcome = WindowRescue.Run(api, window, () => T0);
        Assert.Equal(WindowRescueResult.MovedBack, outcome.Result);
        Assert.True(outcome.WasMinimized);
        Assert.False(outcome.BeforeOnScreen);
        Assert.Equal(ScreenRect.FromSize(510, 166, 900, 700), api.Placed.Single().Rect);
        Assert.Contains("It was minimized and would have opened off-screen.", WindowRescue.Present(outcome).Card.Detail);
        Assert.Contains("minimized=yes", WindowRescue.LogLine(outcome, "r"));

        // Minimized with a restore rectangle on screen: restoring it is all it needs.
        var fine = new FakeWindows();
        var onScreen = fine.Add(5, "Notepad", "Draft", ScreenRect.FromSize(-32000, -32000, 160, 28), minimized: true, normal: ScreenRect.FromSize(100, 100, 900, 700));
        Assert.Equal(WindowRescueResult.MovedBack, WindowRescue.Run(fine, onScreen, () => T0).Result);
        Assert.Equal(ScreenRect.FromSize(100, 100, 900, 700), fine.Placed.Single().Rect);
    }

    [Fact]
    public void A_window_that_grows_on_a_higher_DPI_screen_gets_one_more_fit()
    {
        var api = new FakeWindows { Screens = new List<DisplayMonitor> { Laptop(dpi: 144) } };
        var window = api.Add(6, "Contoso Viewer", "Plan", ScreenRect.FromSize(-6000, 0, 1000, 600), dpi: 144);
        // The first move: the app rescales itself 1.5 times (it was at 96 DPI on the monitor that was removed).
        var first = true;
        api.Lands = r =>
        {
            if (!first) { return r; }
            first = false;
            return ScreenRect.FromSize(r.Left, r.Top, r.Width * 2, r.Height * 2);
        };
        var outcome = WindowRescue.Run(api, window, () => T0);
        Assert.Equal(WindowRescueResult.MovedBack, outcome.Result);
        Assert.Equal(2, api.Placed.Count);
        var final = api.State[window.Handle].Rect;
        Assert.True(final.Left >= 0 && final.Right <= 1920 && final.Top >= 0 && final.Bottom <= 1032, final.ToString());
    }

    [Fact]
    public void The_list_puts_windows_off_the_screen_first_and_names_a_Store_app_by_its_title()
    {
        var api = new FakeWindows();
        api.Add(21, "Notepad", "Draft", ScreenRect.FromSize(100, 100, 800, 600));
        api.Add(22, "ApplicationFrameHost", "Company Portal", ScreenRect.FromSize(200, 100, 800, 600));
        api.Add(23, "Contoso Editor", "Report", ScreenRect.FromSize(1780, 900, 1200, 800));
        api.Add(24, "Contoso Viewer", "Plan", ScreenRect.FromSize(-32000, -32000, 160, 28), minimized: true, normal: ScreenRect.FromSize(-5000, 300, 900, 700));
        var list = WindowRescue.List(api);
        // Off the screen first (partly beyond the edge, and minimized with an off-screen restore place), then by name.
        Assert.Equal(new[] { "Contoso Editor", "Contoso Viewer", "Company Portal", "Notepad" }, list.Select(w => w.AppName));
        Assert.Equal(new[] { true, true, false, false }, list.Select(w => w.OffScreen));
        // The row says it, and a title that only repeats the name is shown once.
        var portal = AppRowViewless(list[2]);
        Assert.Equal("", portal.OffScreenWord);
        Assert.Equal(System.Windows.Visibility.Collapsed, portal.TitleVisibility);
        Assert.Equal("Company Portal", portal.AccessibleName);
        var editor = AppRowViewless(list[0]);
        Assert.Equal("Off screen", editor.OffScreenWord);
        Assert.Equal("Contoso Editor: Report. Off screen", editor.AccessibleName);
    }

    /// <summary>The window list's row as the window shows it.</summary>
    private static EndpointToolbelt.App.Ui.WindowRowView AppRowViewless(AppWindow w) => new(w, false);

    [Fact]
    public void A_visible_window_is_not_moved_but_brought_to_the_front()
    {
        var api = new FakeWindows();
        var window = api.Add(7, "Notepad", "Draft", ScreenRect.FromSize(100, 100, 800, 600));
        var outcome = WindowRescue.Run(api, window, () => T0);
        Assert.Equal(WindowRescueResult.AlreadyVisible, outcome.Result);
        Assert.Empty(api.Placed);
        Assert.Equal(new[] { window.Handle }, api.Fronted);
        var (status, tone, card) = WindowRescue.Present(outcome);
        Assert.Equal("The Notepad window was already on your screen; it is now in front.", status);
        Assert.Equal(Tone.Success, tone);
        Assert.Equal("The whole window is on a current screen, so it was only brought to the front; it was not moved.", card.Detail);
        Assert.Equal("If you still can't see it, click its button on the taskbar.", card.NextStep);
        Assert.Equal("3|r|already-visible", WindowRescue.HistoryResult(outcome, "r"));
    }

    [Fact]
    public void A_window_mostly_beyond_the_screen_edge_comes_back_centred_although_its_title_bar_shows()
    {
        // Bottom right: 140 px of the title bar is on the screen, the rest of the window is beyond the edges.
        var api = new FakeWindows();
        var window = api.Add(11, "Contoso Editor", "Report", ScreenRect.FromSize(1780, 900, 1200, 800));
        Assert.True(WindowGeometry.TitleBarOnScreen(ScreenRect.FromSize(1780, 900, 1200, 800), api.Screens));
        Assert.False(WindowGeometry.WholeOnScreen(ScreenRect.FromSize(1780, 900, 1200, 800), api.Screens));
        var outcome = WindowRescue.Run(api, window, () => T0);
        Assert.Equal(WindowRescueResult.MovedBack, outcome.Result);
        Assert.False(outcome.BeforeOnScreen);
        Assert.Equal(1, outcome.BeforeMonitor);
        Assert.Equal(ScreenRect.FromSize(360, 116, 1200, 800), api.Placed.Single().Rect);
        Assert.Empty(api.Fronted);

        // A few pixels of invisible border beyond the edge (scaled with the DPI) still count as whole.
        Assert.True(WindowGeometry.WholeOnScreen(new ScreenRect(-7, 0, 1927, 1039), api.Screens));
        Assert.False(WindowGeometry.WholeOnScreen(new ScreenRect(-12, 0, 1000, 800), api.Screens));
        Assert.True(WindowGeometry.WholeOnScreen(new ScreenRect(-12, 0, 1000, 800), api.Screens, dpi: 144));

        // A maximized window on a current monitor is on screen; its borders lie beyond the work area.
        var maximized = new FakeWindows();
        var max = maximized.Add(12, "Notepad", "Draft", new ScreenRect(-8, -8, 1928, 1040));
        maximized.State[max.Handle].Maximized = true;
        Assert.Equal(WindowRescueResult.AlreadyVisible, WindowRescue.Run(maximized, max, () => T0).Result);

        // Minimized with a restore rectangle partly beyond the edge: restored centred, not where it was.
        var minimized = new FakeWindows();
        var min = minimized.Add(13, "Notepad", "Draft", ScreenRect.FromSize(-32000, -32000, 160, 28), minimized: true, normal: ScreenRect.FromSize(1780, 900, 900, 700));
        Assert.Equal(WindowRescueResult.MovedBack, WindowRescue.Run(minimized, min, () => T0).Result);
        Assert.Equal(ScreenRect.FromSize(510, 166, 900, 700), minimized.Placed.Single().Rect);
    }

    [Fact]
    public void A_window_closed_meanwhile_or_replaced_by_another_is_said_so()
    {
        var api = new FakeWindows();
        var window = api.Add(8, "Notepad", "Draft", ScreenRect.FromSize(-6000, -6000, 800, 600));
        api.State.Remove(window.Handle);
        var outcome = WindowRescue.Run(api, window, () => T0);
        Assert.Equal(WindowRescueResult.Closed, outcome.Result);
        Assert.Empty(api.Placed);
        Assert.Equal(("That window was closed in the meantime; nothing was changed.", Tone.Neutral), (WindowRescue.Present(outcome).Status, WindowRescue.Present(outcome).Tone));

        // The handle now belongs to another process: another window, so the chosen one was closed.
        var reused = api.Add(9, "Notepad", "Draft", ScreenRect.FromSize(-6000, -6000, 800, 600));
        api.State[reused.Handle].ProcessId = 4242;
        Assert.Equal(WindowRescueResult.Closed, WindowRescue.Run(api, reused, () => T0).Result);

        // Gone between the read and the move.
        var gone = new FakeWindows { PlaceAnswer = PlaceResult.Gone };
        Assert.Equal(WindowRescueResult.Closed, WindowRescue.Run(gone, gone.Add(10, "Notepad", "x", ScreenRect.FromSize(-6000, 0, 800, 600)), () => T0).Result);
    }

    [Fact]
    public void A_window_of_an_app_that_runs_as_administrator_is_not_touched()
    {
        var api = new FakeWindows();
        var window = api.Add(11, "Contoso Admin Tool", "Settings", ScreenRect.FromSize(-6000, -6000, 800, 600));
        api.Elevation[window.ProcessId] = true;
        var outcome = WindowRescue.Run(api, window, () => T0);
        Assert.Equal(WindowRescueResult.RunsAsAdministrator, outcome.Result);
        Assert.Empty(api.Placed);
        var (status, tone, card) = WindowRescue.Present(outcome);
        Assert.Equal("The Contoso Admin Tool window runs as administrator, so it could not be moved.", status);
        Assert.Equal(Tone.Warning, tone);
        Assert.Contains("Alt+Space", card.NextStep);
        Assert.Contains("Win+Shift+Left arrow", card.NextStep);
        Assert.Contains("contact your IT support", card.NextStep);
        Assert.Equal("5|r|runs-as-administrator", WindowRescue.HistoryResult(outcome, "r"));

        // Windows does not show its token (null) and refuses the move: the same result.
        var unknown = new FakeWindows { PlaceAnswer = PlaceResult.AccessDenied };
        var hidden = unknown.Add(12, "Contoso Admin Tool", "Settings", ScreenRect.FromSize(-6000, -6000, 800, 600));
        unknown.Elevation[hidden.ProcessId] = null;
        Assert.Equal(WindowRescueResult.RunsAsAdministrator, WindowRescue.Run(unknown, hidden, () => T0).Result);

        // A normal app that Windows did not move: could not be moved.
        var stuck = new FakeWindows { PlaceAnswer = PlaceResult.Failed };
        Assert.Equal(WindowRescueResult.CouldNotMove, WindowRescue.Run(stuck, stuck.Add(13, "Notepad", "x", ScreenRect.FromSize(-6000, 0, 800, 600)), () => T0).Result);
        Assert.Equal(Tone.Warning, WindowRescue.Present(new WindowRescueOutcome { Result = WindowRescueResult.CouldNotMove, AppName = "Notepad" }).Tone);
    }

    [Fact]
    public void No_windows_is_a_result_of_its_own()
    {
        var (status, tone, card) = WindowRescue.Present(new WindowRescueOutcome { Result = WindowRescueResult.NoWindows });
        Assert.Equal("No open app windows were found to bring back.", status);
        Assert.Equal(Tone.Neutral, tone);
        Assert.Equal(CardKind.Info, card.Kind);
        Assert.Equal("6|r|no-windows", WindowRescue.HistoryResult(new WindowRescueOutcome { Result = WindowRescueResult.NoWindows }, "r"));
        Assert.Equal(WindowRescueResult.NoWindows, WindowRescue.Run(new FakeWindows(), null, () => T0).Result);
    }

    [Fact]
    public void For_IT_finds_it_by_window_off_screen_monitor_and_dock()
    {
        foreach (var query in new[] { "window", "off-screen", "monitor", "dock", "undock", "screen" })
        {
            // "window" also starts "Windows" (Repair Windows Update); the others find it first.
            Assert.Contains(ToolCatalog.Search(query), t => t.Name == WindowRescue.Name);
            if (query != "window") { Assert.Equal(WindowRescue.Name, ToolCatalog.Search(query).First().Name); }
        }
        var tool = ToolCatalog.ByButton("WindowBackButton");
        Assert.Equal(ToolGroup.WindowsSecurity, tool.Group);
        Assert.True(tool.OpensPage);
        Assert.False(tool.InService);
    }

    // --- The page (the real main window, never shown, with the fake window system) -------------------

    private static List<WindowRowView> Rows(App.Ui.MainWindow window)
        => ((IEnumerable<WindowRowView>)MainWindowPagesTests.Get<System.Windows.Controls.ItemsControl>(window, "WindowList").ItemsSource).ToList();

    [Fact]
    public void Choose_a_problem_opens_the_window_list_and_Cancel_moves_nothing()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var api = new FakeWindows();
            api.Add(21, "Notepad", "Contoso plan - Notepad", ScreenRect.FromSize(-6000, -6000, 800, 600));
            api.Add(22, "Contoso Editor", "Report", ScreenRect.FromSize(100, 100, 800, 600));
            window.WindowApi = api;
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            var entry = MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, "ProblemWindowButton");
            Assert.Equal("A window is off the screen. After undocking or using another monitor", System.Windows.Automation.AutomationProperties.GetName(entry));
            MainWindowPagesTests.Call(window, "OnProblemWindow", entry, new System.Windows.RoutedEventArgs());

            Assert.Equal(System.Windows.Visibility.Visible, MainWindowPagesTests.Get<System.Windows.Controls.StackPanel>(window, "WindowsPage").Visibility);
            Assert.Equal("A window is off the screen", MainWindowPagesTests.Get<System.Windows.Controls.TextBlock>(window, "ProblemTitle").Text);
            Assert.Equal("Selected", MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, "ProblemsNav").Tag);
            var rows = Rows(window);
            // Only the window off the screen at first, and it says so; the others behind a link.
            Assert.Equal(new[] { "Notepad: Contoso plan - Notepad. Off screen" }, rows.Select(r => r.AccessibleName));
            Assert.Equal("Off the screen (1)", MainWindowPagesTests.Get<System.Windows.Controls.TextBlock>(window, "WindowsHeading").Text);
            var toggle = MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, "WindowsToggle");
            Assert.Equal("Show the other window", toggle.Content);
            MainWindowPagesTests.Call(window, "OnWindowsToggle", toggle, new System.Windows.RoutedEventArgs());
            rows = Rows(window);
            // Then all of them, the one off the screen first, on one line each (the title after the name).
            Assert.Equal(new[] { "Notepad: Contoso plan - Notepad. Off screen", "Contoso Editor: Report" }, rows.Select(r => r.AccessibleName));
            Assert.Equal("   Report", rows[1].TitleSuffix);
            Assert.Equal("Open windows (2)", MainWindowPagesTests.Get<System.Windows.Controls.TextBlock>(window, "WindowsHeading").Text);
            Assert.Equal("Show only the windows off the screen", toggle.Content);
            // A click on a row brings that window back; there is no separate button below the list.
            Assert.Null(window.FindName("BringBackButton"));
            Assert.True(MainWindowPagesTests.Get<System.Windows.Controls.ItemsControl>(window, "WindowList").IsEnabled);

            MainWindowPagesTests.Call(window, "OnCancelWindows", toggle, new System.Windows.RoutedEventArgs());
            Assert.Equal(System.Windows.Visibility.Visible, MainWindowPagesTests.Get<System.Windows.Controls.StackPanel>(window, "ProblemsPage").Visibility);
            Assert.Empty(api.Placed);
        });
    }

    [Fact]
    public void For_IT_opens_the_same_page_and_Back_returns_there()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var api = new FakeWindows();
            api.Add(23, "Notepad", "Draft", ScreenRect.FromSize(-6000, -6000, 800, 600));
            window.WindowApi = api;
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("It"), false);
            var button = MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, "WindowBackButton");
            Assert.Equal("Choose a window", button.Content);
            MainWindowPagesTests.Call(window, "OnWindowBackTool", button, new System.Windows.RoutedEventArgs());
            Assert.Equal(System.Windows.Visibility.Visible, MainWindowPagesTests.Get<System.Windows.Controls.StackPanel>(window, "WindowsPage").Visibility);
            Assert.Equal("Selected", MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, "ItNav").Tag);
            Assert.Equal("Back to For IT support", System.Windows.Automation.AutomationProperties.GetName(MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, "BackButton")));
            MainWindowPagesTests.Call(window, "OnPathBack", button, new System.Windows.RoutedEventArgs());
            Assert.Equal(System.Windows.Visibility.Visible, MainWindowPagesTests.Get<System.Windows.Controls.StackPanel>(window, "ItPage").Visibility);
            Assert.Empty(api.Placed);
        });
    }

    [Fact]
    public void A_window_of_an_app_with_UI_access_or_admin_rights_reads_Cant_move_and_a_click_moves_nothing()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var api = new FakeWindows();
            api.Add(31, "Accessibility On-Screen Keyboard", "On-Screen Keyboard", ScreenRect.FromSize(-6000, -6000, 800, 300));
            api.Add(32, "Windows Command Processor", "Administrator: Command Prompt", ScreenRect.FromSize(-6000, -6000, 800, 600));
            api.Add(33, "Notepad", "Draft", ScreenRect.FromSize(-6000, -6000, 800, 600));
            api.UiAccess[1031] = true;
            api.Elevation[1032] = true;
            var list = WindowRescue.List(api);
            // Only the window this app may move counts as off the screen; the other two are marked.
            Assert.Equal(new[] { "Notepad" }, list.Where(w => w.OffScreen).Select(w => w.AppName));
            Assert.True(list.Single(w => w.ProcessId == 1031).CannotMove);
            Assert.True(list.Single(w => w.ProcessId == 1032).CannotMove);
            var row = new WindowRowView(list.Single(w => w.ProcessId == 1031), false);
            Assert.Equal("Can't move", row.ActionWord);
            Assert.EndsWith(". Can't move", row.AccessibleName);
            Assert.Contains(WindowRowView.CannotMoveText, row.ToolTipText);
            Assert.Equal("Bring back", new WindowRowView(list.Single(w => w.ProcessId == 1033), false).ActionWord);

            // A click on such a row changes nothing.
            var window = MainWindowPagesTests.NewWindow();
            window.WindowApi = api;
            MainWindowPagesTests.Call(window, "OnWindowChosen", new System.Windows.Controls.Button { DataContext = row }, new System.Windows.RoutedEventArgs());
            Assert.Empty(api.Placed);

            // An app that runs elevated itself may move them.
            api.SelfElevated = true;
            Assert.DoesNotContain(WindowRescue.List(api), w => w.CannotMove);
        });
    }

    [Fact]
    public void With_no_windows_the_page_says_so_and_keeps_the_result_without_titles()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            window.WindowApi = new FakeWindows();
            MainWindowPagesTests.Call(window, "OnProblemWindow", window, new System.Windows.RoutedEventArgs());
            Assert.Equal(System.Windows.Visibility.Visible, MainWindowPagesTests.Get<System.Windows.Controls.TextBlock>(window, "WindowsEmpty").Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, MainWindowPagesTests.Get<System.Windows.FrameworkElement>(window, "WindowListHost").Visibility);
            Assert.Equal("No open app windows were found to bring back.", MainWindowPagesTests.Get<System.Windows.Controls.TextBlock>(window, "StatusText").Text);
            var history = System.IO.File.ReadAllText(window.HistoryOverride);
            Assert.Matches(@";WindowBack;6\|local-[0-9a-f]{8}\|no-windows", history);
        });
    }

    [Fact]
    public void A_run_log_that_could_not_be_saved_is_said_on_the_result()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var check = new LocalRun { Name = ConnectivityTest.Name, RunId = "local-0a1b2c3d", StartedUtc = T0, EndedUtc = T0 };
            MainWindowPagesTests.Call(window, "LogNotSaved", new RunLogHeader { Check = check });
            Assert.Equal("local-0a1b2c3d", MainWindowPagesTests.Field(window, "_logNotSavedRunId"));
            var cards = (List<ResultCard>)MainWindowPagesTests.Field(window, "_cards");
            Assert.Equal("The check ran, but its log could not be saved on this PC.", cards.Last().Title);
        });
    }

    /// <summary>
    /// The sandbox found a copied summary reading an earlier Bring my window back ticket: that was the
    /// clipboard of an earlier app process, not the app. This pins the app's side: after a window run,
    /// the next run (in the app or in the service) replaces what the drawer and Copy details for IT
    /// describe, with its own run ID.
    /// </summary>
    [Fact]
    public void After_a_window_run_the_next_run_is_the_one_the_drawer_and_the_ticket_describe()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Call(window, "ClearResult");
            MainWindowPagesTests.Call(window, "StartCheck", WindowRescue.Name);
            MainWindowPagesTests.Call(window, "ShowWindowOutcome", new WindowRescueOutcome { Result = WindowRescueResult.MovedBack, AppName = "Notepad", ProcessName = "notepad", AfterOnScreen = true, AfterMonitor = 1, Monitors = 1, StartedUtc = T0, EndedUtc = T0 });
            var first = (RunLogHeader)MainWindowPagesTests.Invoke(window, "ShownHeader");
            Assert.Equal(WindowRescue.Name, first.Name);

            // Another tool in the app.
            MainWindowPagesTests.Call(window, "ClearResult");
            MainWindowPagesTests.Call(window, "StartCheck", "Clear Teams cache");
            MainWindowPagesTests.Call(window, "ShowToolOutcome", ToolOutcome.Of(FindingSeverity.Info, Tone.Neutral, 3, "Clear Teams cache: the new Teams is not installed for you. Nothing was changed.", "The new Teams is not installed for you", "There is no Teams cache to clear."), "ClearTeamsCache");
            AssertShows(window, "Clear Teams cache", first.RunId);

            // A run in the service after a window run.
            MainWindowPagesTests.Call(window, "ClearResult");
            MainWindowPagesTests.Call(window, "StartCheck", WindowRescue.Name);
            MainWindowPagesTests.Call(window, "ShowWindowOutcome", new WindowRescueOutcome { Result = WindowRescueResult.AlreadyVisible, AppName = "Notepad", AfterOnScreen = true, StartedUtc = T0, EndedUtc = T0 });
            var second = (RunLogHeader)MainWindowPagesTests.Invoke(window, "ShownHeader");
            MainWindowPagesTests.Call(window, "ClearResult");
            var run = new RunOutcome { Action = ToolbeltAction.SyncClock, RunId = "ab12cd34", Result = ResultCode.Completed, ExitCode = 0, StartedUtc = T0 };
            run.Records.Add(new EngineRecord { Type = "finding", Severity = "OK", Id = "CLOCK-OK", Title = "The clock is in sync", Utc = T0.AddSeconds(5) });
            run.Records.Add(new EngineRecord { Type = "summary", Exit = 0, Utc = T0.AddSeconds(6) });
            // As ShowFinal shows a service run (without its history write and timers, which a never-shown window does not need).
            MainWindowPagesTests.SetField(window, "_shownRun", run);
            MainWindowPagesTests.Call(window, "SetStatus", "Sync clock completed: the clock is in sync.", Tone.Success, true, true);
            MainWindowPagesTests.Call(window, "AddCard", ResultCard.From(FindingSeverity.Ok, "The clock is in sync"));
            AssertShows(window, "Sync clock", second.RunId);
        });
    }

    private static void AssertShows(App.Ui.MainWindow window, string name, string earlierRunId)
    {
        var header = (RunLogHeader)MainWindowPagesTests.Invoke(window, "ShownHeader");
        var cards = (List<ResultCard>)MainWindowPagesTests.Field(window, "_cards");
        var status = MainWindowPagesTests.Get<System.Windows.Controls.TextBlock>(window, "StatusText").Text;
        Assert.Equal(name, header.Name);
        Assert.NotEqual(earlierRunId, header.RunId);
        Assert.Equal(status, MainWindowPagesTests.Get<System.Windows.Controls.TextBlock>(window, "DrawerStatusText").Text);
        var ticket = RunTicket.Text(header, cards, status, (Tone)MainWindowPagesTests.Field(window, "_statusTone"));
        Assert.StartsWith("Endpoint Toolbelt · " + name + " · Reference " + header.RunId, ticket);
        Assert.DoesNotContain(WindowRescue.Name, ticket);
    }
}
