using System;
using System.Collections.Generic;

namespace EndpointToolbelt;

/// <summary>The only actions the service runs. Each one has a button in the app.</summary>
public enum ToolbeltAction
{
    RetryAppsAndPolicies = 1,
    ClearAppCache = 2,
    FullReset = 3,
    RepairWU = 4,
    CollectDiag = 5,

    // Additions for 1.0.
    BackupRecoveryKey = 6,
    SyncClock = 7,
    UpdateDefender = 8,
    FreeDiskSpace = 9,

    // The read-only app diagnosis. It changes nothing on the device.
    DiagnoseApps = 10,

    // Renews the DHCP lease of the one network adapter the user chose.
    RenewIpAddress = 11,

    // The read-only Windows Update check. It changes nothing on the device.
    CheckWindowsUpdate = 12,

    // The read-only check of what Microsoft Defender detected or blocked. It changes nothing on the device.
    CheckDefenderBlocks = 13,
}

/// <summary>Static rules per action. The service enforces these for every caller.</summary>
public sealed class ActionInfo
{
    internal ActionInfo(ToolbeltAction action, bool requiresConfirmation, bool affectsAllSessions, TimeSpan timeLimit, bool producesFile, bool countsTowardRateLimit, bool readOnly = false)
    {
        ReadOnly = readOnly;
        Action = action;
        RequiresConfirmation = requiresConfirmation;
        AffectsAllSessions = affectsAllSessions;
        TimeLimit = timeLimit;
        ProducesFile = producesFile;
        CountsTowardRateLimit = countsTowardRateLimit;
    }

    public ToolbeltAction Action { get; }

    /// <summary>The request must carry <c>Confirmed = true</c>.</summary>
    public bool RequiresConfirmation { get; }

    /// <summary>
    /// The action affects every signed-in user. While other user sessions exist, the request
    /// must acknowledge the number of other sessions, and policy may block it.
    /// </summary>
    public bool AffectsAllSessions { get; }

    /// <summary>Hard limit after which the service stops the engine.</summary>
    public TimeSpan TimeLimit { get; }

    /// <summary>The action returns a file (support package) to the caller.</summary>
    public bool ProducesFile { get; }

    /// <summary>
    /// The action counts toward the per-user rate limit (<c>UserRateLimitPerHour</c>). Collecting
    /// a support package only reads, so it does not; on shared devices the service gives it a
    /// limit of its own.
    /// </summary>
    public bool CountsTowardRateLimit { get; }

    /// <summary>
    /// The action only reads: no sync, reset, timer change, service restart or package. It
    /// does not count toward <c>UserRateLimitPerHour</c>; the service gives it a limit of its own
    /// per caller, so a read cannot use up the repairs and cannot keep the single worker busy.
    /// </summary>
    public bool ReadOnly { get; }
}

public static class ActionCatalog
{
    private static readonly Dictionary<ToolbeltAction, ActionInfo> Actions = new()
    {
        [ToolbeltAction.RetryAppsAndPolicies] = new(ToolbeltAction.RetryAppsAndPolicies, requiresConfirmation: false, affectsAllSessions: false, TimeSpan.FromMinutes(15), producesFile: false, countsTowardRateLimit: true),
        [ToolbeltAction.ClearAppCache] = new(ToolbeltAction.ClearAppCache, requiresConfirmation: true, affectsAllSessions: false, TimeSpan.FromMinutes(15), producesFile: false, countsTowardRateLimit: true),
        [ToolbeltAction.FullReset] = new(ToolbeltAction.FullReset, requiresConfirmation: true, affectsAllSessions: true, TimeSpan.FromMinutes(20), producesFile: false, countsTowardRateLimit: true),
        // The engine receives this limit and keeps the last 10 minutes as a reserve for its close-out.
        [ToolbeltAction.RepairWU] = new(ToolbeltAction.RepairWU, requiresConfirmation: true, affectsAllSessions: true, TimeSpan.FromMinutes(130), producesFile: false, countsTowardRateLimit: true),
        [ToolbeltAction.CollectDiag] = new(ToolbeltAction.CollectDiag, requiresConfirmation: false, affectsAllSessions: false, TimeSpan.FromMinutes(30), producesFile: true, countsTowardRateLimit: false),
        // Backs up existing protectors only; changes nothing on the volume.
        [ToolbeltAction.BackupRecoveryKey] = new(ToolbeltAction.BackupRecoveryKey, requiresConfirmation: false, affectsAllSessions: false, TimeSpan.FromMinutes(10), producesFile: false, countsTowardRateLimit: true),
        // A resync only; never a new time source.
        [ToolbeltAction.SyncClock] = new(ToolbeltAction.SyncClock, requiresConfirmation: false, affectsAllSessions: false, TimeSpan.FromMinutes(10), producesFile: false, countsTowardRateLimit: true),
        [ToolbeltAction.UpdateDefender] = new(ToolbeltAction.UpdateDefender, requiresConfirmation: false, affectsAllSessions: false, TimeSpan.FromMinutes(25), producesFile: false, countsTowardRateLimit: true),
        // Component store cleanup slows the device for everyone while it runs.
        [ToolbeltAction.FreeDiskSpace] = new(ToolbeltAction.FreeDiskSpace, requiresConfirmation: true, affectsAllSessions: true, TimeSpan.FromMinutes(60), producesFile: false, countsTowardRateLimit: true),
        // Reads the app state of the visible scopes and the IME log; changes nothing.
        [ToolbeltAction.DiagnoseApps] = new(ToolbeltAction.DiagnoseApps, requiresConfirmation: false, affectsAllSessions: false, TimeSpan.FromMinutes(5), producesFile: false, countsTowardRateLimit: false, readOnly: true),
        // Drops the connection on the chosen adapter for a moment, for everyone on the device.
        [ToolbeltAction.RenewIpAddress] = new(ToolbeltAction.RenewIpAddress, requiresConfirmation: true, affectsAllSessions: true, TimeSpan.FromMinutes(5), producesFile: false, countsTowardRateLimit: true),
        // Reads the update history, failure events, policies, service start types, the restart
        // flags and the free space; changes nothing. It shares the read-only limit of the app diagnosis.
        [ToolbeltAction.CheckWindowsUpdate] = new(ToolbeltAction.CheckWindowsUpdate, requiresConfirmation: false, affectsAllSessions: false, TimeSpan.FromMinutes(5), producesFile: false, countsTowardRateLimit: false, readOnly: true),
        // Reads the block and detection events of the Microsoft Defender operational log; changes
        // nothing. It shares the read-only limit of the app diagnosis.
        [ToolbeltAction.CheckDefenderBlocks] = new(ToolbeltAction.CheckDefenderBlocks, requiresConfirmation: false, affectsAllSessions: false, TimeSpan.FromMinutes(5), producesFile: false, countsTowardRateLimit: false, readOnly: true),
    };

    public static IReadOnlyCollection<ToolbeltAction> All => Actions.Keys;

    public static bool IsDefined(ToolbeltAction action) => Actions.ContainsKey(action);

    public static ActionInfo Get(ToolbeltAction action)
    {
        if (!Actions.TryGetValue(action, out var info))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action.");
        }
        return info;
    }
}
