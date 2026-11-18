using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// The window system for Bring my window back, with the user's own rights: the current
/// monitors (EnumDisplayMonitors, GetMonitorInfo, GetDpiForMonitor), the user's top-level app
/// windows (EnumWindows), and GetWindowPlacement / SetWindowPlacement for one window. The app is
/// per-monitor DPI aware (v2), so every coordinate is in physical pixels.
/// </summary>
public sealed class NativeWindowApi : IWindowApi
{
    private readonly int _ownProcess;

    public NativeWindowApi()
    {
        using var self = Process.GetCurrentProcess();
        _ownProcess = self.Id;
        SelfElevated = Elevated(_ownProcess) == true;
    }

    public bool SelfElevated { get; }

    public IReadOnlyList<DisplayMonitor> Monitors()
    {
        var found = new List<DisplayMonitor>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info)) { return true; }
            var dpi = 96;
            try
            {
                if (NativeMethods.GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x > 0) { dpi = (int)x; }
            }
            catch (EntryPointNotFoundException) { dpi = 96; }
            catch (DllNotFoundException) { dpi = 96; }
            found.Add(new DisplayMonitor { Bounds = info.Monitor.ToRect(), WorkArea = info.Work.ToRect(), Primary = (info.Flags & 1) != 0, Dpi = dpi });
            return true;
        }, IntPtr.Zero);
        var ordered = found.OrderByDescending(m => m.Primary).ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
        for (var i = 0; i < ordered.Count; i++) { ordered[i].Index = i + 1; }
        return ordered;
    }

    public IReadOnlyList<AppWindow> Windows()
    {
        var shell = NativeMethods.GetShellWindow();
        var handles = new List<IntPtr>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            handles.Add(handle);
            return true;
        }, IntPtr.Zero);
        var names = new Dictionary<int, (string Process, string App)>();
        var windows = new List<AppWindow>();
        foreach (var handle in handles)
        {
            if (handle == shell || !NativeMethods.IsWindowVisible(handle)) { continue; }
            if (NativeMethods.GetWindow(handle, 4 /* GW_OWNER */) != IntPtr.Zero) { continue; }
            var exStyle = NativeMethods.GetWindowLongPtr(handle, -20 /* GWL_EXSTYLE */).ToInt64();
            if ((exStyle & 0x80 /* WS_EX_TOOLWINDOW */) != 0) { continue; }
            if (Cloaked(handle)) { continue; }
            var title = Text(handle);
            if (title.Length == 0) { continue; }
            if (IsShellClass(ClassName(handle))) { continue; }
            if (NativeMethods.GetWindowThreadProcessId(handle, out var pid) == 0) { continue; }
            if (pid == 0 || pid == _ownProcess) { continue; }
            if (!names.TryGetValue(pid, out var name)) { names[pid] = name = ProcessNames(pid); }
            windows.Add(new AppWindow { Handle = handle, ProcessId = pid, ProcessName = name.Process, AppName = name.App, Title = title });
        }
        return windows;
    }

    public WindowFacts Read(IntPtr handle)
    {
        if (!NativeMethods.IsWindow(handle)) { return null; }
        if (NativeMethods.GetWindowThreadProcessId(handle, out var pid) == 0) { return null; }
        if (!NativeMethods.GetWindowRect(handle, out var rect)) { return null; }
        var placement = new NativeMethods.WindowPlacement { Length = Marshal.SizeOf<NativeMethods.WindowPlacement>() };
        if (!NativeMethods.GetWindowPlacement(handle, ref placement)) { return null; }
        var normal = placement.NormalPosition.ToRect();
        var exStyle = NativeMethods.GetWindowLongPtr(handle, -20).ToInt64();
        // rcNormalPosition is in workspace coordinates, except for tool windows.
        if ((exStyle & 0x80) == 0) { normal = WindowGeometry.WorkspaceToScreen(normal, Monitors().FirstOrDefault(m => m.Primary)); }
        var dpi = 96;
        try { dpi = (int)Math.Max(96, NativeMethods.GetDpiForWindow(handle)); }
        catch (EntryPointNotFoundException) { dpi = 96; }
        return new WindowFacts
        {
            ProcessId = pid,
            Rect = rect.ToRect(),
            NormalRect = normal,
            Minimized = NativeMethods.IsIconic(handle),
            Maximized = NativeMethods.IsZoomed(handle),
            RestoreToMaximized = (placement.Flags & 2 /* WPF_RESTORETOMAXIMIZED */) != 0,
            Dpi = dpi,
        };
    }

    public PlaceResult Place(IntPtr handle, ScreenRect normalRect, bool maximize)
    {
        if (!NativeMethods.IsWindow(handle)) { return PlaceResult.Gone; }
        var placement = new NativeMethods.WindowPlacement { Length = Marshal.SizeOf<NativeMethods.WindowPlacement>() };
        if (!NativeMethods.GetWindowPlacement(handle, ref placement)) { return Error(Marshal.GetLastWin32Error()); }
        var exStyle = NativeMethods.GetWindowLongPtr(handle, -20).ToInt64();
        var rect = (exStyle & 0x80) == 0 ? WindowGeometry.ScreenToWorkspace(normalRect, Monitors().FirstOrDefault(m => m.Primary)) : normalRect;
        placement.NormalPosition = NativeMethods.Rect.From(rect);
        // SW_SHOWMAXIMIZED, SW_RESTORE (a minimized window) or SW_SHOWNORMAL.
        placement.ShowCmd = maximize ? 3 : NativeMethods.IsIconic(handle) ? 9 : 1;
        placement.Flags = 0;
        if (!NativeMethods.SetWindowPlacement(handle, ref placement)) { return Error(Marshal.GetLastWin32Error()); }
        NativeMethods.SetForegroundWindow(handle);
        return PlaceResult.Done;
    }

    public bool BringToFront(IntPtr handle)
        => NativeMethods.IsWindow(handle) && NativeMethods.SetForegroundWindow(handle);

    private static PlaceResult Error(int code) => code switch
    {
        5 => PlaceResult.AccessDenied,
        1400 => PlaceResult.Gone,
        _ => PlaceResult.Failed,
    };

    public bool? IsElevated(int processId) => TokenFlag(processId, 20 /* TokenElevation */);

    public bool? HasUiAccess(int processId) => TokenFlag(processId, 26 /* TokenUIAccess */);

    private static bool? Elevated(int processId) => TokenFlag(processId, 20 /* TokenElevation */);

    /// <summary>
    /// A yes/no field of a process's token (TokenElevation, TokenUIAccess); null when Windows refuses to
    /// show its token (an app with higher rights, or another account).
    /// </summary>
    private static bool? TokenFlag(int processId, int infoClass)
    {
        var process = NativeMethods.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
        if (process == IntPtr.Zero) { return null; }
        try
        {
            if (!NativeMethods.OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token)) { return null; }
            try
            {
                return NativeMethods.GetTokenInformation(token, infoClass, out var value, sizeof(int), out _) ? value != 0 : null;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    private static bool Cloaked(IntPtr handle)
        => NativeMethods.DwmGetWindowAttribute(handle, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool IsShellClass(string name) => name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    private static string Text(IntPtr handle)
    {
        var length = NativeMethods.GetWindowTextLength(handle);
        if (length <= 0) { return ""; }
        var buffer = new char[length + 1];
        var read = NativeMethods.GetWindowText(handle, buffer, buffer.Length);
        return read <= 0 ? "" : new string(buffer, 0, read).Trim();
    }

    private static string ClassName(IntPtr handle)
    {
        var buffer = new char[256];
        var read = NativeMethods.GetClassName(handle, buffer, buffer.Length);
        return read <= 0 ? "" : new string(buffer, 0, read);
    }

    /// <summary>The process name, and the app's name as people know it (its file description) when Windows shows its path.</summary>
    private static (string Process, string App) ProcessNames(int processId)
    {
        string processName;
        try
        {
            using var process = Process.GetProcessById(processId);
            processName = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            processName = "";
        }
        var app = "";
        var handle = NativeMethods.OpenProcess(0x1000, false, processId);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var buffer = new char[1024];
                var size = buffer.Length;
                if (NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
                {
                    try { app = FileVersionInfo.GetVersionInfo(new string(buffer, 0, size)).FileDescription?.Trim() ?? ""; }
                    catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException) { app = ""; }
                }
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }
        if (app.Length == 0) { app = processName.Length > 0 ? processName : "Unknown app"; }
        return (processName, app);
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public ScreenRect ToRect() => new(Left, Top, Right, Bottom);

            public static Rect From(ScreenRect r) => new() { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowPlacement
        {
            public int Length;
            public int Flags;
            public int ShowCmd;
            public Point MinPosition;
            public Point MaxPosition;
            public Rect NormalPosition;
        }

        internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        internal delegate bool WindowEnumProc(IntPtr window, IntPtr data);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(WindowEnumProc callback, IntPtr data);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsZoomed(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
        internal static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr window, [Out] char[] text, int max);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr window, [Out] char[] text, int max);

        [DllImport("user32.dll")]
        internal static extern int GetWindowThreadProcessId(IntPtr window, out int processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPlacement(IntPtr window, ref WindowPlacement placement);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(IntPtr process, int flags, [Out] char[] name, ref int size);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int length, out int returned);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
