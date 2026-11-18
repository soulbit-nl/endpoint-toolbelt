using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;

namespace EndpointToolbelt.Service;

public sealed class ToolbeltWindowsService : ServiceBase
{
    /// <summary>ERROR_EXCEPTION_IN_SERVICE: a stop with this exit code counts as a failure.</summary>
    public const int FaultExitCode = 1064;

    private ToolbeltHost _host;
    private volatile bool _faulted;

    public ToolbeltWindowsService()
    {
        ServiceName = ProductInfo.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanPauseAndContinue = false;
        // Custom control codes are ignored (interactive users may send them).
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        RequestAdditionalTime(30000);
        try
        {
            _host = new ToolbeltHost(ServicePaths.ForService()) { ConfigureRecovery = () => Platform.ServiceRecovery.Apply(ProductInfo.ServiceName) };
            // Raised on the pipe's accept thread, which Stop waits for, so stop from elsewhere.
            _host.Faulted += _ => System.Threading.ThreadPool.QueueUserWorkItem(__ => HandleFault(Stop));
            _host.Start();
        }
        catch (Exception ex)
        {
            // AutoLog is off; without this, a start failure would leave no trace.
            new Logging.EventLogWriter().Write(EndpointToolbelt.EventId.ServiceError, System.Diagnostics.EventLogEntryType.Error,
                $"{ProductInfo.ServiceDisplayName} could not start: {ex}");
            ExitCode = FaultExitCode;
            _host?.Dispose();
            _host = null;
            throw;
        }
    }

    /// <summary>
    /// The pipe could not be kept available. The service stops with a failure exit code, so the
    /// service control manager's recovery actions restart it (the MSI sets them to apply also to
    /// a stop with an error). Apps are not told to close, because the service comes back.
    /// </summary>
    internal void HandleFault(Action stop)
    {
        _faulted = true;
        ExitCode = FaultExitCode;
        stop();
    }

    /// <summary>Whether a stop tells connected apps to close (an SCM stop, not a fault stop).</summary>
    internal bool NotifyAppsOnStop => !_faulted;

    protected override void OnStop()
    {
        RequestAdditionalTime((int)ToolbeltHost.MaxStopTime.TotalMilliseconds);
        StopHost();
    }

    // The service control manager gives little time at system shutdown; RequestAdditionalTime
    // is ignored there, so the host stops with its own bounded waits, and the apps are not told
    // to close (they end with the session).
    protected override void OnShutdown() => StopHost(systemShutdown: true);

    /// <summary>A fault stop and an SCM stop can overlap; only the first one stops the host.</summary>
    private void StopHost(bool systemShutdown = false)
    {
        var host = System.Threading.Interlocked.Exchange(ref _host, null);
        host?.Stop(NotifyAppsOnStop && !systemShutdown);
    }
}

public static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    public static int Main(string[] args)
    {
        // Native DLLs load from System32 only, never from the current or application folder.
        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);

        if (args.Length == 1 && args[0] == "--remove-state") { return RemoveState(); }
#if DEBUG
        if (args.Length == 1 && args[0] == "--console") { return RunConsole(); }
#endif
        if (args.Length > 0)
        {
            Console.Error.WriteLine("Usage: EndpointToolbelt.Service.exe [--remove-state]");
            return 87;
        }
        ServiceBase.Run(new ToolbeltWindowsService());
        return 0;
    }

#if DEBUG
    /// <summary>
    /// Development run in a console window, with a per-user working folder. Debug builds only:
    /// it runs with the caller's rights on the product's pipe name, so in a shipped build a
    /// standard user could take the name while the service is stopped.
    /// </summary>
    private static int RunConsole()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.WorkingFolderName + "-dev");
        var paths = new ServicePaths(root, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ProductInfo.EngineFolderName));
        using var host = new ToolbeltHost(paths, production: false);
        host.Start();
        Console.WriteLine($"{ProductInfo.ServiceDisplayName} (console). Working folder: {root}. Press Enter to stop.");
        Console.ReadLine();
        return 0;
    }
#endif

    /// <summary>
    /// Called by the MSI at uninstall (deferred, as SYSTEM, after it removed its Config values).
    /// Deletes the service's SYSTEM-only working folder without following links, and the State
    /// key and the then empty registry root (64-bit view). Never fails the uninstall.
    /// </summary>
    private static int RemoveState()
    {
        bool IsSystem()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem;
        }
        // LocalSystem's local application data folder, built from the fixed profile path and not
        // asked from the shell: inside a Windows Installer custom action the folder API does not
        // reliably return SYSTEM's profile, and a wrong answer would only mean nothing is removed.
        string root;
        try { root = UninstallCleanup.ServiceWorkingRoot; }
        catch (Exception ex)
        {
            Report("The working folder could not be determined: " + ex.Message);
            return 0;
        }
        // The MSI runs this right after the service reported Stopped; its process can still be
        // exiting and holding its log file. Wait for it, bounded.
        try { UninstallCleanup.WaitForOtherServiceProcesses(TimeSpan.FromSeconds(20)); } catch (Exception) { }
        using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        RemoveState(IsSystem, root, UninstallCleanup.SystemProfileFolder, hklm, ProductInfo.RegistryRoot, Report);
        return 0;
    }

    /// <summary>The steps of <c>--remove-state</c>, with the environment passed in (tests).</summary>
    /// <returns>False when the caller is not SYSTEM and nothing was touched.</returns>
    internal static bool RemoveState(Func<bool> isSystem, string workingRoot, string allowedParent,
        Microsoft.Win32.RegistryKey baseKey, string registryRoot, Action<string> report)
    {
        try
        {
            if (!isSystem()) { return false; }
        }
        catch (Exception)
        {
            return false;
        }

        // A delete that hits a file still in use (the stopped service closing its log) is retried.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var problem = UninstallCleanup.RemoveWorkingFolder(workingRoot, allowedParent);
                if (problem != null) { report($"The working folder {workingRoot} was not removed: {problem}."); }
                break;
            }
            catch (System.IO.IOException) when (attempt < UninstallCleanup.DeleteAttempts)
            {
                System.Threading.Thread.Sleep(UninstallCleanup.DeleteRetryDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < UninstallCleanup.DeleteAttempts)
            {
                System.Threading.Thread.Sleep(UninstallCleanup.DeleteRetryDelay);
            }
            catch (Exception ex)
            {
                // Leftover state must not block an uninstall.
                report("The working folder could not be removed: " + ex.Message);
                break;
            }
        }

        try
        {
            if (!UninstallCleanup.RemoveStateKeys(baseKey, registryRoot))
            {
                report($"HKLM\\{registryRoot} still holds other values and was kept.");
            }
        }
        catch (Exception ex)
        {
            report("The registry state could not be removed: " + ex.Message);
        }
        return true;
    }
    /// <summary>Standard error (MSI log) and, while the event source still exists, the event log.</summary>
    private static void Report(string message)
    {
        try { Console.Error.WriteLine(message); } catch (Exception) { }
        new Logging.EventLogWriter().Write(EndpointToolbelt.EventId.ServiceError, System.Diagnostics.EventLogEntryType.Warning, "Uninstall: " + message);
    }
}
