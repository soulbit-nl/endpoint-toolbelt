using System;

namespace EndpointToolbelt.App.Core;

/// <summary>How each action is named to the user.</summary>
public static class ActionText
{
    public static string Name(ToolbeltAction action) => action switch
    {
        ToolbeltAction.RetryAppsAndPolicies => "Retry work apps and settings",
        ToolbeltAction.ClearAppCache => "Clear downloaded app content",
        ToolbeltAction.FullReset => "Full IME reset",
        ToolbeltAction.RepairWU => "Repair Windows Update",
        ToolbeltAction.CollectDiag => "Create support package",
        ToolbeltAction.BackupRecoveryKey => "Back up recovery key",
        ToolbeltAction.SyncClock => "Sync clock",
        ToolbeltAction.UpdateDefender => "Update Defender definitions",
        ToolbeltAction.FreeDiskSpace => "Free up disk space",
        ToolbeltAction.DiagnoseApps => "App diagnosis",
        ToolbeltAction.RenewIpAddress => IpRenewal.Name,
        ToolbeltAction.CheckWindowsUpdate => WuCheck.Name,
        ToolbeltAction.CheckDefenderBlocks => DefenderCheck.Name,
        _ => action.ToString(),
    };

    /// <summary>
    /// Repair Windows Update and Free up disk space run in the background: the window may be closed
    /// and shows the result when it is opened again.
    /// </summary>
    public static bool RunsInBackground(ToolbeltAction action) => action is ToolbeltAction.RepairWU or ToolbeltAction.FreeDiskSpace;

    /// <summary>The service's hard limit for the action, in whole minutes.</summary>
    public static int LimitMinutes(ToolbeltAction action) => (int)Math.Round(ActionCatalog.Get(action).TimeLimit.TotalMinutes);

    /// <summary>
    /// Minutes the engine keeps from the limit for its close-out (results, cooldown, restart
    /// marker); the same figure as RunReserveMinutes in the engine.
    /// </summary>
    public const int EngineReserveMinutes = 10;

    /// <summary>How long the work itself may take at most, as "2 hours" or "50 minutes".</summary>
    public static string WorkLimitText(ToolbeltAction action)
    {
        var minutes = LimitMinutes(action) - EngineReserveMinutes;
        if (minutes % 60 == 0) { return minutes == 60 ? "1 hour" : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} hours", minutes / 60); }
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} minutes", minutes);
    }
}
