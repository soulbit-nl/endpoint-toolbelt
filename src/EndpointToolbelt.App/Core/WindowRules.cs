namespace EndpointToolbelt.App.Core;

/// <summary>The window's buttons, for the rules about when they can be used.</summary>
public enum AppButton
{
    Retry,
    Package,
    Report,
    Logs,
    ClearCache,
    Connectivity,
    Portal,
    FullReset,
    RepairWu,
    Refresh,
    BackupKey,
    SyncClock,
    UpdateDefender,
    FreeSpace,
    RefreshSignIn,
    ResetSignIn,
    TeamsCache,

    /// <summary>The read-only app diagnosis: it runs in the service.</summary>
    Diagnose,

    /// <summary>Bring my window back (its tool button and Bring it back): it runs in the app, as the user.</summary>
    WindowBack,

    /// <summary>Start Word or Excel in safe mode: it runs in the app, as the user.</summary>
    WordSafe,
    ExcelSafe,

    /// <summary>Start Outlook (classic) or PowerPoint in safe mode, and Reset OneDrive: they run in the app, as the user.</summary>
    OutlookSafe,
    PowerPointSafe,
    ResetOneDrive,

    /// <summary>Get a new IP address (its tool button and Get a new address): the renew runs in the service.</summary>
    RenewIp,

    /// <summary>Check Windows Update: the read-only check runs in the service.</summary>
    CheckWu,

    /// <summary>Check Defender blocks: the read-only check runs in the service.</summary>
    CheckDefender,

    /// <summary>Check compliance settings: it reads in the app, as the user; the readouts come from the service's status.</summary>
    Compliance,
}

/// <summary>Which buttons can be used: one thing at a time, and the repairs only while the service runs.</summary>
public static class ButtonRules
{
    /// <summary>The status line, and the help text of the repair buttons, while the service cannot be reached.</summary>
    public const string ServiceMissing =
        "The " + ProductInfo.DisplayName + " service is not available, so repairs cannot start. Press Check this PC again on Home, or restart the device; if this stays, contact your IT support. You can still view the device report.";

    /// <summary>The button starts work in the service (the engine), so it needs the service.</summary>
    public static bool NeedsService(AppButton button)
        => button is AppButton.Retry or AppButton.Package or AppButton.ClearCache or AppButton.FullReset or AppButton.RepairWu
            or AppButton.BackupKey or AppButton.SyncClock or AppButton.UpdateDefender or AppButton.FreeSpace or AppButton.Diagnose or AppButton.RenewIp or AppButton.CheckWu or AppButton.CheckDefender;

    /// <param name="button">The button.</param>
    /// <param name="busy">Something runs (or a run is being followed): nothing else may start.</param>
    /// <param name="serviceMissing">The service could not be reached.</param>
    public static bool IsEnabled(AppButton button, bool busy, bool serviceMissing)
        => !busy && !(serviceMissing && NeedsService(button));
}

/// <summary>
/// The window's state for its buttons: a button handler runs, a run is being followed through the
/// service, and whether the service can be reached. Changed only on the window's thread.
/// </summary>
public sealed class ButtonState
{
    /// <summary>A button handler runs (the busy gate).</summary>
    public bool Busy { get; set; }

    /// <summary>A run goes on in the service and the window follows it.</summary>
    public bool Following { get; private set; }

    /// <summary>The service could not be reached: no repair can start.</summary>
    public bool ServiceMissing { get; private set; }

    public bool IsEnabled(AppButton button) => ButtonRules.IsEnabled(button, Busy || Following, ServiceMissing);

    /// <summary>The button is off because the service is missing (it then says so).</summary>
    public bool OffForService(AppButton button) => ServiceMissing && ButtonRules.NeedsService(button);

    public void StartFollowing() => Following = true;

    /// <summary>The followed run ended (its result is in, or the app stopped waiting for it).</summary>
    public void StopFollowing() => Following = false;

    /// <summary>
    /// The window stopped following a run because the service did not answer for a while: the
    /// service counts as missing until a status request or the watcher reaches it again.
    /// </summary>
    public void FollowingGaveUp()
    {
        Following = false;
        ServiceMissing = true;
    }

    /// <summary>A status request or the watcher reached the service.</summary>
    public void ServiceReached() => ServiceMissing = false;

    /// <summary>A status request found no service, or the watcher lost it.</summary>
    public void ServiceLost() => ServiceMissing = true;
}

/// <summary>What a status change is, for the decision whether a screen reader hears it.</summary>
public enum StatusChange
{
    /// <summary>Any status set once: a run starts or ends, a check, a message.</summary>
    Once,

    /// <summary>A repeated update while a run goes on (the minutes of a background run).</summary>
    LiveUpdate,

    /// <summary>
    /// A status a screen reader does not need: one put back after a short check, or one replaced
    /// at once by another status (which is announced).
    /// </summary>
    Quiet,
}

/// <summary>
/// Which status changes are announced. A long background run updates its status every minute;
/// a screen reader hears it at the start, at each new engine step and at the end, not every
/// minute (the visual status still updates).
/// </summary>
public static class Announcements
{
    /// <param name="change">What kind of status change it is.</param>
    /// <param name="announcedStep">The engine step that was last announced (null when none).</param>
    /// <param name="currentStep">The engine step the run is in now (null when none).</param>
    public static bool ShouldAnnounce(StatusChange change, string announcedStep, string currentStep) => change switch
    {
        StatusChange.Once => true,
        StatusChange.LiveUpdate => currentStep != null && currentStep != announcedStep,
        _ => false,
    };
}
