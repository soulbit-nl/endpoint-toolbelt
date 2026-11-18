using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EndpointToolbelt.App.Core;

/// <summary>A rectangle in screen pixels (physical; the app is per-monitor DPI aware), right and bottom exclusive.</summary>
public readonly struct ScreenRect : IEquatable<ScreenRect>
{
    public ScreenRect(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public int Left { get; }
    public int Top { get; }
    public int Right { get; }
    public int Bottom { get; }
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static ScreenRect FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);

    public ScreenRect Intersect(ScreenRect other)
    {
        var r = new ScreenRect(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
        return r.IsEmpty ? default : r;
    }

    public ScreenRect Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public bool Equals(ScreenRect other) => Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
    public override bool Equals(object obj) => obj is ScreenRect other && Equals(other);
    public override int GetHashCode() => unchecked((((Left * 397) ^ Top) * 397 ^ Right) * 397 ^ Bottom);
    public static bool operator ==(ScreenRect a, ScreenRect b) => a.Equals(b);
    public static bool operator !=(ScreenRect a, ScreenRect b) => !a.Equals(b);

    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0},{1})-({2},{3})", Left, Top, Right, Bottom);
}

/// <summary>A monitor as Windows has it now: its bounds, its work area (without the taskbar) and its DPI.</summary>
public sealed class DisplayMonitor
{
    /// <summary>1 for the primary monitor, then by position from the left: the number the result names.</summary>
    public int Index { get; set; }
    public ScreenRect Bounds { get; set; }
    public ScreenRect WorkArea { get; set; }
    public bool Primary { get; set; }
    public int Dpi { get; set; } = 96;
}

/// <summary>A normal top-level window of an app in the user's own session, as the list shows it.</summary>
public sealed class AppWindow
{
    public IntPtr Handle { get; set; }
    public int ProcessId { get; set; }

    /// <summary>The process name ("notepad"): what the run log records.</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>The app's name as people know it ("Notepad"), from its file description.</summary>
    public string AppName { get; set; } = "";

    /// <summary>The window title: shown to the user only, never written to a log, the history or Copy details for IT.</summary>
    public string Title { get; set; } = "";

    /// <summary>The window is not wholly on a current screen (or, minimized, would not open wholly on one): the list puts it first.</summary>
    public bool OffScreen { get; set; }

    /// <summary>
    /// Windows does not let this app move it: its app runs as administrator, or with UI access as the
    /// On-Screen Keyboard does (User Interface Privilege Isolation). The list shows it, marked; it is never "off the screen".
    /// </summary>
    public bool CannotMove { get; set; }
}

/// <summary>Where a window is now.</summary>
public sealed class WindowFacts
{
    public int ProcessId { get; set; }

    /// <summary>The window's rectangle on the screen (for a minimized window, where Windows parked it).</summary>
    public ScreenRect Rect { get; set; }

    /// <summary>The rectangle it has when it is restored, in screen coordinates.</summary>
    public ScreenRect NormalRect { get; set; }

    public bool Minimized { get; set; }
    public bool Maximized { get; set; }

    /// <summary>A minimized window that comes back maximized.</summary>
    public bool RestoreToMaximized { get; set; }

    /// <summary>The DPI the window renders at (96 when unknown).</summary>
    public int Dpi { get; set; } = 96;
}

/// <summary>What placing a window did.</summary>
public enum PlaceResult
{
    Done,

    /// <summary>Windows refused (an app with higher rights owns the window, UIPI).</summary>
    AccessDenied,

    /// <summary>The window no longer exists.</summary>
    Gone,

    Failed,
}

/// <summary>The window system as Bring my window back needs it; the real one is <c>NativeWindowApi</c>, tests use a fake.</summary>
public interface IWindowApi
{
    /// <summary>The monitors connected now, read fresh on every call.</summary>
    IReadOnlyList<DisplayMonitor> Monitors();

    /// <summary>The user's normal app windows in this session: visible or minimized, with a title, no tool windows, not this app.</summary>
    IReadOnlyList<AppWindow> Windows();

    /// <summary>Where the window is now, or null when it was closed.</summary>
    WindowFacts Read(IntPtr handle);

    /// <summary>Restores the window if it is minimized and gives it this rectangle (screen coordinates), maximized again when asked.</summary>
    PlaceResult Place(IntPtr handle, ScreenRect normalRect, bool maximize);

    /// <summary>Brings the window to the front (the user just chose it in this app, so Windows allows it); false when it refused.</summary>
    bool BringToFront(IntPtr handle);

    /// <summary>The process runs elevated: true, false, or null when Windows does not say (its token is refused).</summary>
    bool? IsElevated(int processId);

    /// <summary>The process has UI access (an accessibility app such as the On-Screen Keyboard): true, false, or null when Windows does not say.</summary>
    bool? HasUiAccess(int processId);

    /// <summary>This app itself runs elevated.</summary>
    bool SelfElevated { get; }
}

/// <summary>The geometry of Bring my window back, without system calls (testable).</summary>
public static class WindowGeometry
{
    /// <summary>The height of a title bar at 96 DPI, which must be on a work area for the user to grab the window.</summary>
    public const int TitleBarHeight = 30;

    /// <summary>The part of the title bar's width that must be on a work area: at least this many pixels (or half a narrow window).</summary>
    public const int MinVisibleTitle = 100;

    /// <summary>The title bar strip of a window (the top 30 px at its DPI, without the invisible side borders).</summary>
    public static ScreenRect TitleBar(ScreenRect window, int dpi = 96)
    {
        var height = Math.Max(1, TitleBarHeight * Math.Max(96, dpi) / 96);
        var inset = Math.Min(8, Math.Max(0, (window.Width - 2) / 2));
        return new ScreenRect(window.Left + inset, window.Top, window.Right - inset, Math.Min(window.Bottom, window.Top + height));
    }

    /// <summary>
    /// At least part of the title bar is inside a current work area: 100 px of its width (or half a
    /// narrow window) and half its height, so the user can grab and move it.
    /// </summary>
    public static bool TitleBarOnScreen(ScreenRect window, IReadOnlyList<DisplayMonitor> monitors, int dpi = 96)
    {
        if (window.IsEmpty || monitors == null) { return false; }
        var title = TitleBar(window, dpi);
        var needWidth = Math.Max(1, Math.Min(MinVisibleTitle, title.Width / 2));
        var needHeight = Math.Max(1, title.Height / 2);
        return monitors.Any(m =>
        {
            var inside = title.Intersect(m.WorkArea);
            return !inside.IsEmpty && inside.Width >= needWidth && inside.Height >= needHeight;
        });
    }

    /// <summary>
    /// The whole window is within the work area of one current monitor, so the user sees all of it;
    /// a few pixels of invisible border (8 at 96 DPI, scaled with the DPI) may stick out. A window that
    /// is only partly on a screen, with the rest beyond its edge, is not.
    /// </summary>
    public static bool WholeOnScreen(ScreenRect window, IReadOnlyList<DisplayMonitor> monitors, int dpi = 96)
    {
        if (window.IsEmpty || monitors == null) { return false; }
        var border = 8 * Math.Max(96, dpi) / 96;
        return monitors.Any(m => window.Left >= m.WorkArea.Left - border && window.Top >= m.WorkArea.Top - border
            && window.Right <= m.WorkArea.Right + border && window.Bottom <= m.WorkArea.Bottom + border);
    }

    /// <summary>The monitor the window overlaps most, or null when it is on none.</summary>
    public static DisplayMonitor MonitorOf(ScreenRect window, IReadOnlyList<DisplayMonitor> monitors)
        => monitors?.Select(m => (Monitor: m, Area: window.Intersect(m.Bounds).Area)).Where(x => x.Area > 0).OrderByDescending(x => x.Area).Select(x => x.Monitor).FirstOrDefault();

    /// <summary>
    /// The monitor a window goes to: the one it overlaps most, when it overlaps one; otherwise the
    /// primary monitor (a window wholly off-screen, for example on a monitor that was removed, comes
    /// back where the user looks first).
    /// </summary>
    public static DisplayMonitor TargetMonitor(ScreenRect window, IReadOnlyList<DisplayMonitor> monitors)
        => MonitorOf(window, monitors) ?? monitors.FirstOrDefault(m => m.Primary) ?? (monitors.Count > 0 ? monitors[0] : null);

    /// <summary>
    /// The rectangle to give the window on the target monitor: its size when that fits the work area,
    /// else shrunk to it, centred in the work area. <paramref name="sourceDpi"/> is the DPI the window
    /// has now: a per-monitor aware app scales itself by target/source when it lands on a monitor
    /// with another DPI, so the size is chosen to fit after that scaling.
    /// </summary>
    public static ScreenRect Fit(ScreenRect window, DisplayMonitor target, int sourceDpi)
    {
        var work = target.WorkArea;
        var scale = (double)Math.Max(1, target.Dpi) / Math.Max(1, sourceDpi <= 0 ? 96 : sourceDpi);
        int Size(int length, int room)
        {
            var scaled = length * scale;
            return scaled <= room ? length : Math.Max(1, (int)Math.Floor(room / scale));
        }
        var width = Size(Math.Max(1, window.Width), work.Width);
        var height = Size(Math.Max(1, window.Height), work.Height);
        var landedWidth = Math.Min(work.Width, (int)Math.Round(width * scale));
        var landedHeight = Math.Min(work.Height, (int)Math.Round(height * scale));
        var left = work.Left + (work.Width - landedWidth) / 2;
        var top = work.Top + (work.Height - landedHeight) / 2;
        return ScreenRect.FromSize(left, top, width, height);
    }

    /// <summary>
    /// A rectangle from GetWindowPlacement (workspace coordinates: relative to the primary monitor's
    /// work area) in screen coordinates; they differ when the taskbar is at the left or the top.
    /// </summary>
    public static ScreenRect WorkspaceToScreen(ScreenRect rect, DisplayMonitor primary)
        => primary == null ? rect : rect.Offset(primary.WorkArea.Left - primary.Bounds.Left, primary.WorkArea.Top - primary.Bounds.Top);

    public static ScreenRect ScreenToWorkspace(ScreenRect rect, DisplayMonitor primary)
        => primary == null ? rect : rect.Offset(primary.Bounds.Left - primary.WorkArea.Left, primary.Bounds.Top - primary.WorkArea.Top);
}

/// <summary>How Bring my window back ended.</summary>
public enum WindowRescueResult
{
    MovedBack,
    AlreadyVisible,
    Closed,
    CouldNotMove,

    /// <summary>The window belongs to an app that runs as administrator: Windows does not let a normal app move it (UIPI).</summary>
    RunsAsAdministrator,

    NoWindows,
}

/// <summary>What one attempt did; the window title is not in it.</summary>
public sealed class WindowRescueOutcome
{
    public WindowRescueResult Result { get; set; }
    public string ProcessName { get; set; } = "";
    public string AppName { get; set; } = "";
    public bool BeforeOnScreen { get; set; }
    public int? BeforeMonitor { get; set; }
    public bool AfterOnScreen { get; set; }
    public int? AfterMonitor { get; set; }
    public bool WasMinimized { get; set; }
    public int Monitors { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }
}

/// <summary>
/// "Bring my window back": an app window that is off-screen after undocking comes back onto a
/// current monitor. It runs in the app with the user's own rights, moves only the window the user
/// chose, keeps its size when that fits, and saves nothing about monitors or dock layouts.
/// </summary>
public static class WindowRescue
{
    public const string Name = "Bring my window back";
    // The row's title and subtitle on Choose a problem and the page's title.
    public const string ProblemTitle = "A window is off the screen";

    /// <summary>
    /// The heading above the window list: "Off the screen (1)" while only those show, "Open windows (9)"
    /// once all show, and "No window is off the screen. Open windows (9)" when none is.
    /// </summary>
    public static string ListHeading(int offScreen, int total, bool all)
    {
        var open = "Open windows (" + total.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        return offScreen == 0 ? "No window is off the screen. " + open
            : all ? open : "Off the screen (" + offScreen.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
    }
    public const string ProblemSubtitle = "After undocking or using another monitor";
    public const string HistoryName = "WindowBack";

    /// <summary>
    /// The process that hosts the windows of Store apps (Company Portal, Settings): its own name says
    /// nothing to a user, so such a window is named by its title.
    /// </summary>
    internal const string StoreAppHost = "ApplicationFrameHost";

    /// <summary>
    /// The user's windows to choose from: the ones off the screen first (what the user came for),
    /// then by app name and title. A Store app's window is named by its title, not its host process.
    /// </summary>
    public static List<AppWindow> List(IWindowApi api)
    {
        var monitors = api.Monitors();
        var windows = api.Windows().ToList();
        foreach (var w in windows)
        {
            if (string.Equals(w.ProcessName, StoreAppHost, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(w.Title)) { w.AppName = w.Title.Trim(); }
            var facts = api.Read(w.Handle);
            // A window of an app with higher rights cannot be moved from here: shown, marked, never counted as off the screen.
            w.CannotMove = !api.SelfElevated && (api.IsElevated(w.ProcessId) == true || api.HasUiAccess(w.ProcessId) == true);
            w.OffScreen = !w.CannotMove && facts != null && (facts.Minimized
                ? !WindowGeometry.WholeOnScreen(facts.NormalRect, monitors, facts.Dpi)
                : facts.Maximized ? !WindowGeometry.TitleBarOnScreen(facts.Rect, monitors, facts.Dpi) : !WindowGeometry.WholeOnScreen(facts.Rect, monitors, facts.Dpi));
        }
        return windows.OrderByDescending(w => w.OffScreen)
            .ThenBy(w => w.AppName, StringComparer.CurrentCultureIgnoreCase).ThenBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Brings the chosen window back: restores it when it is minimized and moves it into the work area
    /// of a current monitor (monitors are read again right before the move), then checks that its
    /// title bar is on a work area.
    /// </summary>
    public static WindowRescueOutcome Run(IWindowApi api, AppWindow chosen, Func<DateTime> utcNow = null)
    {
        var now = utcNow ?? (() => DateTime.UtcNow);
        var outcome = new WindowRescueOutcome { ProcessName = chosen?.ProcessName ?? "", AppName = chosen?.AppName ?? "", StartedUtc = now() };
        WindowRescueOutcome End(WindowRescueResult result)
        {
            outcome.Result = result;
            outcome.EndedUtc = now();
            return outcome;
        }
        if (chosen == null) { return End(WindowRescueResult.NoWindows); }

        var facts = api.Read(chosen.Handle);
        // A handle that now belongs to another process is another window: the chosen one was closed.
        if (facts == null || facts.ProcessId != chosen.ProcessId) { return End(WindowRescueResult.Closed); }
        var monitors = api.Monitors();
        outcome.Monitors = monitors.Count;
        outcome.WasMinimized = facts.Minimized;
        var seen = facts.Minimized ? facts.NormalRect : facts.Rect;
        // On screen means the user sees the whole window: a maximized one on a current monitor, any
        // other one within a work area. A window that is only partly on a screen is moved, because
        // the user chose it as a window they can't see.
        outcome.BeforeOnScreen = !facts.Minimized && (facts.Maximized
            ? WindowGeometry.TitleBarOnScreen(facts.Rect, monitors, facts.Dpi)
            : WindowGeometry.WholeOnScreen(facts.Rect, monitors, facts.Dpi));
        outcome.BeforeMonitor = facts.Minimized ? null : WindowGeometry.MonitorOf(facts.Rect, monitors)?.Index;
        if (outcome.BeforeOnScreen)
        {
            outcome.AfterOnScreen = true;
            outcome.AfterMonitor = outcome.BeforeMonitor;
            // It may be behind other windows: the chosen one comes to the front, nothing else changes.
            api.BringToFront(chosen.Handle);
            return End(WindowRescueResult.AlreadyVisible);
        }
        // An app with higher rights: Windows would refuse; nothing is tried, so nothing changes.
        if (api.IsElevated(chosen.ProcessId) == true && !api.SelfElevated) { return End(WindowRescueResult.RunsAsAdministrator); }

        var maximize = facts.Maximized || (facts.Minimized && facts.RestoreToMaximized);
        ScreenRect target;
        if (facts.Minimized && WindowGeometry.WholeOnScreen(facts.NormalRect, monitors, facts.Dpi))
        {
            // Minimized with a restore position on a current monitor: restoring is all it needs.
            target = facts.NormalRect;
        }
        else
        {
            var basis = facts.Maximized ? facts.NormalRect : seen;
            var monitor = WindowGeometry.TargetMonitor(basis, monitors);
            if (monitor == null) { return End(WindowRescueResult.CouldNotMove); }
            var source = WindowGeometry.MonitorOf(basis, monitors)?.Dpi ?? facts.Dpi;
            target = WindowGeometry.Fit(basis, monitor, source);
        }

        var placed = api.Place(chosen.Handle, target, maximize);
        if (placed == PlaceResult.Gone) { return End(WindowRescueResult.Closed); }
        if (placed != PlaceResult.Done) { return End(Refused(api, chosen, placed)); }

        // Verify against the monitors as they are now; a window that grew on a monitor with another
        // DPI gets one more fit on the monitor it landed on.
        for (var attempt = 0; ; attempt++)
        {
            var after = api.Read(chosen.Handle);
            if (after == null) { return End(WindowRescueResult.Closed); }
            var current = api.Monitors();
            outcome.Monitors = current.Count;
            outcome.AfterMonitor = WindowGeometry.MonitorOf(after.Rect, current)?.Index;
            outcome.AfterOnScreen = !after.Minimized && WindowGeometry.TitleBarOnScreen(after.Rect, current, after.Dpi);
            var fits = outcome.AfterOnScreen && (after.Maximized || WindowGeometry.WholeOnScreen(after.Rect, current, after.Dpi));
            if (fits || (outcome.AfterOnScreen && attempt > 0)) { return End(WindowRescueResult.MovedBack); }
            if (attempt > 0) { return End(Refused(api, chosen, PlaceResult.Failed)); }
            var landed = WindowGeometry.TargetMonitor(after.Rect, current);
            if (landed == null) { return End(WindowRescueResult.CouldNotMove); }
            placed = api.Place(chosen.Handle, WindowGeometry.Fit(after.Rect, landed, landed.Dpi), after.Maximized);
            if (placed == PlaceResult.Gone) { return End(WindowRescueResult.Closed); }
            if (placed != PlaceResult.Done) { return End(outcome.AfterOnScreen ? WindowRescueResult.MovedBack : Refused(api, chosen, placed)); }
        }
    }

    /// <summary>Windows refused or the window did not move: an app with higher rights (or one whose rights Windows does not show) owns it, or something else stopped it.</summary>
    private static WindowRescueResult Refused(IWindowApi api, AppWindow chosen, PlaceResult placed)
    {
        if (api.SelfElevated) { return WindowRescueResult.CouldNotMove; }
        var elevated = api.IsElevated(chosen.ProcessId);
        return elevated == true || (elevated == null && placed is PlaceResult.AccessDenied or PlaceResult.Failed) ? WindowRescueResult.RunsAsAdministrator : WindowRescueResult.CouldNotMove;
    }

    /// <summary>The result in the run log and the history.</summary>
    public static string Slug(WindowRescueResult result) => result switch
    {
        WindowRescueResult.MovedBack => "moved-back",
        WindowRescueResult.AlreadyVisible => "already-visible",
        WindowRescueResult.Closed => "closed",
        WindowRescueResult.CouldNotMove => "could-not-move",
        WindowRescueResult.RunsAsAdministrator => "runs-as-administrator",
        _ => "no-windows",
    };

    /// <summary>The history code: 0 moved back, 1 could not move, 3 already visible, 4 closed, 5 runs as administrator, 6 no windows.</summary>
    public static int Code(WindowRescueResult result) => result switch
    {
        WindowRescueResult.MovedBack => 0,
        WindowRescueResult.CouldNotMove => 1,
        WindowRescueResult.AlreadyVisible => 3,
        WindowRescueResult.Closed => 4,
        WindowRescueResult.RunsAsAdministrator => 5,
        _ => 6,
    };

    /// <summary>A result from the history in words; null for a word this version does not know.</summary>
    public static string ResultWords(string slug) => slug switch
    {
        "moved-back" => "moved back",
        "already-visible" => "already on screen, nothing changed",
        "closed" => "the window was closed meanwhile",
        "could-not-move" => "could not be moved",
        "runs-as-administrator" => "could not be moved: the app runs as administrator",
        "no-windows" => "no windows found",
        _ => null,
    };

    /// <summary>"&lt;code&gt;|&lt;run ID&gt;|&lt;result&gt;", as the connection check writes its history.</summary>
    public static string HistoryResult(WindowRescueOutcome outcome, string runId)
        => Code(outcome.Result).ToString(CultureInfo.InvariantCulture) + "|" + (string.IsNullOrEmpty(runId) ? "none" : runId) + "|" + Slug(outcome.Result);

    /// <summary>
    /// The run log line: the app's process name, the result, before and after (on screen, which
    /// monitor), whether it was minimized, the number of monitors, the time; never the window title.
    /// </summary>
    public static string LogLine(WindowRescueOutcome outcome, string runId)
    {
        static string Monitor(int? index) => index?.ToString(CultureInfo.InvariantCulture) ?? "none";
        return string.Format(CultureInfo.InvariantCulture,
            "WINDOW run={0} time={1:yyyy-MM-ddTHH:mm:ss.fffZ} ms={2} context=user-app app={3} result={4} before={5} before-monitor={6} after={7} after-monitor={8} minimized={9} monitors={10}",
            string.IsNullOrEmpty(runId) ? "none" : runId, outcome.StartedUtc, (int)Math.Max(0, (outcome.EndedUtc - outcome.StartedUtc).TotalMilliseconds),
            string.IsNullOrEmpty(outcome.ProcessName) ? "none" : outcome.ProcessName.Replace(' ', '_'), Slug(outcome.Result),
            outcome.BeforeOnScreen ? "on-screen" : "off-screen", Monitor(outcome.BeforeMonitor),
            outcome.AfterOnScreen ? "on-screen" : "off-screen", Monitor(outcome.AfterMonitor), outcome.WasMinimized ? "yes" : "no", outcome.Monitors);
    }

    private const string KeyboardWay = "Click the app's button on the taskbar, then press Alt+Space, choose Move and use the arrow keys; or press Win+Shift+Left arrow or Right arrow. If that does not help, contact your IT support.";

    /// <summary>The status, its tone and the card: the app's name, never the window title.</summary>
    public static (string Status, Tone Tone, ResultCard Card) Present(WindowRescueOutcome outcome)
    {
        var app = string.IsNullOrWhiteSpace(outcome.AppName) ? "app" : outcome.AppName.Trim();
        var screens = outcome.Monitors > 1 ? " of " + outcome.Monitors.ToString(CultureInfo.InvariantCulture) : "";
        string On(int? index) => index is int i ? "screen " + i.ToString(CultureInfo.InvariantCulture) + screens : "your screen";
        switch (outcome.Result)
        {
            case WindowRescueResult.MovedBack:
            {
                var was = outcome.WasMinimized ? "It was minimized and would have opened off-screen." : "Its title bar was on no current screen.";
                return ("The " + app + " window is back on your screen.", Tone.Success,
                    ResultCard.From(FindingSeverity.Ok, "The " + app + " window is back on " + On(outcome.AfterMonitor), was + " It now fits the work area of " + On(outcome.AfterMonitor) + "; only this window was moved."));
            }
            case WindowRescueResult.AlreadyVisible:
                return ("The " + app + " window was already on your screen; it is now in front.", Tone.Success,
                    ResultCard.From(FindingSeverity.Ok, "The " + app + " window was already on " + On(outcome.BeforeMonitor), "The whole window is on a current screen, so it was only brought to the front; it was not moved.",
                        "If you still can't see it, click its button on the taskbar."));
            case WindowRescueResult.Closed:
                return ("That window was closed in the meantime; nothing was changed.", Tone.Neutral,
                    ResultCard.From(FindingSeverity.Info, "The " + app + " window was closed before it could be moved", "Nothing was changed.",
                        "If the app is still open, refresh the list and choose its window again."));
            case WindowRescueResult.RunsAsAdministrator:
                return ("The " + app + " window runs as administrator, so it could not be moved.", Tone.Warning,
                    ResultCard.From(FindingSeverity.Warning, "The " + app + " window could not be moved: the app runs as administrator",
                        "Windows does not let an app that runs with normal rights move a window of an app that runs as administrator (or with other higher rights). Nothing was changed.",
                        KeyboardWay,
                        "The window belongs to an elevated process (" + (string.IsNullOrEmpty(outcome.ProcessName) ? "unknown" : outcome.ProcessName) + "); User Interface Privilege Isolation blocks moving it from a standard user app."));
            case WindowRescueResult.CouldNotMove:
                return ("The " + app + " window could not be moved.", Tone.Warning,
                    ResultCard.From(FindingSeverity.Warning, "The " + app + " window could not be moved", "Windows did not move it onto a current screen.", KeyboardWay));
            default:
                return ("No open app windows were found to bring back.", Tone.Neutral,
                    ResultCard.From(FindingSeverity.Info, "No open app windows were found",
                        "Only normal app windows in your own session are listed: windows with a title, not tool windows and not this app.",
                        "If an app shows on the taskbar but no window, click its taskbar button, or close and start the app again."));
        }
    }
}
