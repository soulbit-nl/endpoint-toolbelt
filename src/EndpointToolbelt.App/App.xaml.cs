using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using Microsoft.Win32;

namespace EndpointToolbelt.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A WPF Application is not disposable; the mutex and event are disposed in OnExit.")]
public partial class App : Application
{
    // One instance per session; a second start brings the open window to the front.
    private const string InstanceMutexName = @"Local\EndpointToolbelt.App";
    private const string ActivateEventName = @"Local\EndpointToolbelt.App.Activate";
    private const int ASFW_ANY = -1;

    private Mutex _instance;
    private EventWaitHandle _activate;
    private RegisteredWaitHandle _activateWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { LogError(args.Exception); args.SetObserved(); };


        if (!TakeInstance())
        {
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        ThemeManager.Apply(this);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// True when this is the session's first instance. A second start wakes the first one and
    /// ends. When the names cannot be used at all (another process of the same user holds them
    /// as something else), the app runs without the single-instance check rather than not at all.
    /// </summary>
    private bool TakeInstance()
    {
        try
        {
            _instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var created);
            if (!created)
            {
                try
                {
                    using var existing = EventWaitHandle.OpenExisting(ActivateEventName);
                    // The running instance may bring its window to the front only when this one,
                    // which the user just started, allows it.
                    _ = AllowSetForegroundWindow(ASFW_ANY);
                    existing.Set();
                }
                catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
                {
                    // The other instance is starting or closing; nothing to bring forward.
                }
                return false;
            }
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => Dispatcher.BeginInvoke(new Action(BringToFront)), null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            LogError(ex);
        }
        return true;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => ThemeManager.Apply(this)));
        }
    }

    private void BringToFront()
    {
        if (MainWindow == null) { return; }
        if (MainWindow.WindowState == WindowState.Minimized) { MainWindow.WindowState = WindowState.Normal; }
        MainWindow.Activate();
    }

    /// <summary>
    /// The last line of defence: an error no handler expected is written to the user's error log
    /// and shown, and the app stays open (an action keeps running in the service regardless).
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        if (e.Exception is OutOfMemoryException) { return; }
        e.Handled = true;
        try
        {
            if (MainWindow is MainWindow window) { window.ShowUnexpectedError(e.Exception); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogError(ex);
        }
    }

    /// <summary>Appends an unexpected error to %LOCALAPPDATA%\EndpointToolbelt\error.log; never throws.</summary>
    internal static void LogError(Exception ex)
    {
        if (ex == null) { return; }
        try
        {
            var path = Path.Combine(UserFiles.DataFolder(), "error.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) { File.Delete(path); }
            var text = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + " " + ex + Environment.NewLine;
            File.AppendAllText(path, text, new UTF8Encoding(false));
        }
        catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Nowhere left to report it.
        }
    }

    // Session end is never blocked: the default handling lets Windows end the session, and any
    // action keeps running in the service.

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _activateWait?.Unregister(null);
        _activate?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
