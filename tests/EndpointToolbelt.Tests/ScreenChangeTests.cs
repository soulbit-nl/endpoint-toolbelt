using System;
using System.ComponentModel;
using System.Windows;
using EndpointToolbelt.App.Core;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// A screen that changes while the app is open (a remote desktop whose client window is resized,
/// with "update the resolution on resize" on, and its scale from the client): the window keeps the
/// user's size where it fits, else shrinks; it stays inside the work area; it grows back.
/// </summary>
public class ScreenChangeTests
{
    private const double MinW = 440;
    private const double MinH = 420;

    private static (double Left, double Top, double Width, double Height) Refit(double left, double top, double width, double height, (double Left, double Top, double Width, double Height) area)
        => WindowPlacement.Refit(left, top, width, height, area.Left, area.Top, area.Width, area.Height, MinW, MinH);

    private static void Inside((double Left, double Top, double Width, double Height) w, (double Left, double Top, double Width, double Height) area)
    {
        Assert.True(w.Left >= area.Left - 0.01 && w.Top >= area.Top - 0.01, "left or top outside: " + w);
        Assert.True(w.Left + w.Width <= area.Left + area.Width + 0.01 && w.Top + w.Height <= area.Top + area.Height + 0.01, "right or bottom outside: " + w);
    }

    [Fact]
    public void The_owners_virtual_desktop_at_125_percent_on_1920_x_1111_fits_the_default_window()
    {
        // 1920 x 1111 physical pixels at 125 %, a 52-pixel taskbar at the bottom: about 1536 x 847 logical.
        var area = WindowPlacement.Logical(0, 0, 1920, 1111 - 52, 1.25);
        Assert.Equal(1536, area.Width, 1);
        Assert.Equal(847.2, area.Height, 1);
        var w = Refit(500, 100, WindowLayout.DefaultWindowWidth, WindowLayout.DefaultWindowHeight, area);
        Assert.Equal((500d, 100d, 540d, 640d), w);
        Inside(w, area);
    }

    [Fact]
    public void A_shrinking_resolution_shrinks_the_window_and_a_growing_one_gives_the_users_size_back()
    {
        // The user made the window 900 x 800 on 1920 x 1040; the client window is made small: 1024 x 600.
        var large = WindowPlacement.Logical(0, 0, 1920, 1040, 1.0);
        var small = WindowPlacement.Logical(0, 0, 1024, 600, 1.0);
        var w = Refit(800, 200, 900, 800, small);
        Assert.Equal(900, w.Width);
        Assert.Equal(600, w.Height);
        Inside(w, small);
        Assert.Equal(124, w.Left);
        Assert.Equal(0, w.Top);
        // Smaller than the window's minimum: the work area wins.
        var tiny = WindowPlacement.Logical(0, 0, 800, 400, 1.0);
        var t = Refit(w.Left, w.Top, 900, 800, tiny);
        Assert.Equal((0d, 0d, 800d, 400d), t);
        // The client window is made large again: the user's own size comes back, where the window was.
        var back = Refit(t.Left, t.Top, 900, 800, large);
        Assert.Equal((0d, 0d, 900d, 800d), back);
        Inside(back, large);
    }

    [Theory]
    [InlineData(1.0, 900, 800)]
    [InlineData(1.25, 900, 800)]
    [InlineData(1.5, 900, 693.3)]
    public void A_scale_change_from_100_to_125_to_150_keeps_the_size_where_it_fits(double scale, double width, double height)
    {
        // 1920 x 1040 physical pixels: 1536 x 832 logical at 125 %, 1280 x 693 at 150 %.
        var area = WindowPlacement.Logical(0, 0, 1920, 1040, scale);
        var w = Refit(300, 100, 900, 800, area);
        Assert.Equal(width, w.Width, 1);
        Assert.Equal(height, w.Height, 1);
        Inside(w, area);
        // The default window fits at every scale, at its own size.
        var d = Refit(300, 100, WindowLayout.DefaultWindowWidth, WindowLayout.DefaultWindowHeight, area);
        Assert.Equal((WindowLayout.DefaultWindowWidth, WindowLayout.DefaultWindowHeight), (d.Width, d.Height));
    }

    [Fact]
    public void A_window_that_ends_up_partly_outside_is_moved_back_in()
    {
        var area = WindowPlacement.Logical(0, 0, 1366, 728, 1.0);
        // It was on the right of a wider screen.
        var right = Refit(1500, 300, 540, 640, area);
        Assert.Equal((826d, 88d, 540d, 640d), right);
        // Above and left of the screen (a monitor that was removed on the left).
        var left = Refit(-900, -50, 540, 640, area);
        Assert.Equal((0d, 0d, 540d, 640d), left);
        Inside(right, area);
        Inside(left, area);
    }

    [Theory]
    [InlineData("left")]
    [InlineData("top")]
    [InlineData("right")]
    public void A_taskbar_on_another_edge_is_never_covered(string edge)
    {
        // 1920 x 1080 with a 48-pixel taskbar on one edge.
        var area = edge switch
        {
            "left" => WindowPlacement.Logical(48, 0, 1920, 1080, 1.0),
            "top" => WindowPlacement.Logical(0, 48, 1920, 1080, 1.0),
            _ => WindowPlacement.Logical(0, 0, 1872, 1080, 1.0),
        };
        var w = Refit(edge == "right" ? 1500 : 0, 0, 540, 640, area);
        Inside(w, area);
        Assert.Equal((540d, 640d), (w.Width, w.Height));
        // At 150 % the same taskbar is 32 logical pixels.
        var scaled = WindowPlacement.Logical(edge == "left" ? 48 : 0, edge == "top" ? 48 : 0, edge == "right" ? 1872 : 1920, 1080, 1.5);
        var s = Refit(0, 0, 540, 640, scaled);
        Inside(s, scaled);
    }

    [Fact]
    public void The_window_follows_a_screen_change_and_leaves_the_static_event_when_it_closes()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            // Only a shown window follows the static event; a test window starts it itself.
            Assert.False(window.ScreenWatched);
            window.WatchScreen();
            Assert.True(window.ScreenWatched);
            window.Left = 1200;
            window.Top = 400;
            window.Width = 540;
            window.Height = 640;
            // A smaller session: the window shrinks into the work area.
            window.WorkAreaOverride = () => new Rect(0, 0, 1280, 600);
            window.RefitToScreen();
            Assert.Equal(540, window.Width);
            Assert.Equal(600, window.Height);
            Assert.True(window.Left + window.Width <= 1280 && window.Top >= 0);
            // The session grows again: the size comes back, inside the new work area with a taskbar on the left.
            window.WorkAreaOverride = () => new Rect(40, 0, 1880, 1040);
            window.RefitToScreen();
            Assert.Equal(640, window.Height);
            Assert.True(window.Left >= 40);
            // Closing leaves SystemEvents.DisplaySettingsChanged.
            MainWindowPagesTests.Call(window, "OnClosingWindow", window, new CancelEventArgs());
            Assert.False(window.ScreenWatched);
        });
    }

    [Fact]
    public void The_logical_work_area_follows_the_scale()
    {
        Assert.Equal((0d, 0d, 1536d, 832d), WindowPlacement.Logical(0, 0, 1920, 1040, 1.25));
        Assert.Equal((-1280d, 0d, 1280d, 693.3), Round(WindowPlacement.Logical(-1920, 0, 0, 1040, 1.5)));
        Assert.Equal((0d, 0d, 1920d, 1040d), WindowPlacement.Logical(0, 0, 1920, 1040, 0));
    }

    private static (double, double, double, double) Round((double A, double B, double C, double D) v)
        => (Math.Round(v.A, 1), Math.Round(v.B, 1), Math.Round(v.C, 1), Math.Round(v.D, 1));
}
