using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EndpointToolbelt.App.Ui;

/// <summary>
/// Light, dark or high contrast, following Windows: the app colour setting (AppsUseLightTheme)
/// and the contrast theme. Re-applied when the user changes either.
/// </summary>
public static class ThemeManager
{
    private const int DwmUseImmersiveDarkMode = 20;
    private static ResourceDictionary _current;

    public static bool IsDark { get; private set; }

    /// <summary>
    /// The theme was (re)applied. Brushes set from code are snapshots of the old theme, so whoever
    /// set them sets them again.
    /// </summary>
    public static event Action Changed;

    public static void Apply(Application app) => Apply(app, recheck: true);

    private static void Apply(Application app, bool recheck)
    {
        var name = SystemParameters.HighContrast ? "HighContrast" : UsesLightTheme() ? "Light" : "Dark";
        IsDark = name == "Dark";
        var dictionary = new ResourceDictionary { Source = new Uri("pack://application:,,,/EndpointToolbelt;component/Ui/Themes/" + name + ".xaml", UriKind.Absolute) };
        if (_current != null) { app.Resources.MergedDictionaries.Remove(_current); }
        app.Resources.MergedDictionaries.Insert(0, dictionary);
        _current = dictionary;
        foreach (Window window in app.Windows) { ApplyTitleBar(window); }
        Changed?.Invoke();

        // The high contrast theme reads the system colours when it loads. WPF may take the new
        // colours in only after the change notification, so they are read once more shortly after.
        if (recheck && name == "HighContrast")
        {
            var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, app.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => { timer.Stop(); Apply(app, recheck: false); };
            timer.Start();
        }
    }

    /// <summary>A dark title bar for the dark theme (Windows 10 2004 and later; ignored elsewhere).</summary>
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) { return; }
        var dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    private static bool UsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (System.Security.SecurityException)
        {
            return true;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
