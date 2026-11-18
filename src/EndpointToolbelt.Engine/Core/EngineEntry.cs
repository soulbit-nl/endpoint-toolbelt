using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace EndpointToolbelt.Engine;

/// <summary>
/// The actions this engine runs, each with exactly one handler. The service routes an action here
/// only when it has a handler in this table (a test checks this); any other action exits 87.
/// </summary>
public static class ActionHandlers
{
    public static IReadOnlyDictionary<string, IActionHandler> Shipped { get; } = new Dictionary<string, IActionHandler>(StringComparer.Ordinal)
    {
        // Create support package.
        [nameof(ToolbeltAction.CollectDiag)] = new Actions.CollectDiagHandler(),
        // Repair Windows Update.
        [nameof(ToolbeltAction.RepairWU)] = new Actions.RepairWUHandler(),
        // The app actions.
        [nameof(ToolbeltAction.RetryAppsAndPolicies)] = new Actions.RetryAppsAndPoliciesHandler(),
        [nameof(ToolbeltAction.ClearAppCache)] = new Actions.ClearAppCacheHandler(),
        [nameof(ToolbeltAction.FullReset)] = new Actions.FullResetHandler(),
        // The device actions.
        [nameof(ToolbeltAction.BackupRecoveryKey)] = new Actions.BackupRecoveryKeyHandler(),
        [nameof(ToolbeltAction.SyncClock)] = new Actions.SyncClockHandler(),
        [nameof(ToolbeltAction.UpdateDefender)] = new Actions.UpdateDefenderHandler(),
        [nameof(ToolbeltAction.FreeDiskSpace)] = new Actions.FreeDiskSpaceHandler(),
        // The read-only app diagnosis.
        [nameof(ToolbeltAction.DiagnoseApps)] = new Actions.DiagnoseAppsHandler(),
        // Get a new IP address.
        [nameof(ToolbeltAction.RenewIpAddress)] = new Actions.RenewIpAddressHandler(),
        // The read-only Windows Update check.
        [nameof(ToolbeltAction.CheckWindowsUpdate)] = new Actions.CheckWindowsUpdateHandler(),
    };
}

/// <summary>
/// Started only by the Endpoint Toolbelt service, as SYSTEM, with a request file the service wrote
/// to its SYSTEM-only working folder:
/// <c>EndpointToolbelt.Engine.exe -Action &lt;name&gt; -RequestFile &lt;path&gt;</c>. Writes JSON records
/// to standard output (UTF-8 without a BOM, one object per line, each carrying the run ID) and
/// exits with the engine exit code: 0 success, 1 failed, 2 warnings, 87 invalid or unhandled
/// action, 69001 cooldown, 69002 restart pending, 69003 nothing to repair.
/// </summary>
public static class EngineEntry
{
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    public static int Run(string[] args, IReadOnlyDictionary<string, IActionHandler> handlers)
    {
        // Native libraries load from System32 only, never from the working folder or PATH.
        SetDefaultDllDirectories(LoadLibrarySearchSystem32);
        var error = Console.Error;
        try
        {
            // The Windows Update worker (started only by this engine for one API call).
            if (args != null && args.Length > 0 && args[0] == WindowsUpdate.WuWorker.Switch)
            {
                using var workerOut = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                return WindowsUpdate.WuWorker.Run(args.Skip(1).ToList(), workerOut);
            }
            if (!TryParseArguments(args, out var action, out var requestFile))
            {
                error.WriteLine("Usage: EndpointToolbelt.Engine.exe -Action <name> -RequestFile <path>");
                return EngineExitCode.Failed;
            }
            // Records are UTF-8 without a BOM for the service, whatever the console code page is.
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            var services = new EngineServices
            {
                Output = stdout.WriteLine,
                ErrorOutput = error.WriteLine,
            };
            return RunFrame.Run(action, requestFile, handlers, services);
        }
        catch (Exception ex)
        {
            error.WriteLine("The engine stopped with an unexpected error: " + ex.Message);
            return EngineExitCode.Failed;
        }
    }

    /// <summary>Exactly -Action and -RequestFile, each once with a value (names ignore case).</summary>
    internal static bool TryParseArguments(string[] args, out string action, out string requestFile)
    {
        action = null;
        requestFile = null;
        if (args == null || args.Length != 4) { return false; }
        for (var i = 0; i < args.Length; i += 2)
        {
            var name = args[i];
            var value = args[i + 1];
            if (string.Equals(name, "-Action", StringComparison.OrdinalIgnoreCase) && action == null) { action = value; }
            else if (string.Equals(name, "-RequestFile", StringComparison.OrdinalIgnoreCase) && requestFile == null) { requestFile = value; }
            else { return false; }
        }
        return action != null && requestFile != null;
    }
}
