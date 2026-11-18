using System;
using System.Collections.Generic;
using System.Globalization;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.Actions;

// App actions: Retry work apps and settings, Clear downloaded app content, Full IME reset.
// There is deliberately no action that only restarts the IME or only
// clears the retry timers: both are part of Retry work apps and settings.

/// <summary>The parts the app actions are made of; tests replace them.</summary>
public sealed class AppActionParts
{
    public Func<RunContext, string, IReadOnlyList<AppRetry>> Readout { get; set; } = AppState.Readout;
    public Func<RunContext, RefreshDepth, bool, RefreshResult> Refresh { get; set; } = (run, depth, skip) => new ImeRefresh(run).Run(depth, skip);
    public Func<RunContext, MdmSyncResult> Sync { get; set; } = run => Mdm.VerifiedSync(run);
    public Action<RunContext, MdmSyncResult> SyncFinding { get; set; } = (run, sync) => Mdm.AddFinding(run, sync);
    public Func<RunContext, ContentCacheResult> ClearContent { get; set; } = run => new ImeRefresh(run).ClearContentCache(requireIdle: true);
}

/// <summary>Retry work apps and settings.</summary>
public sealed class RetryAppsAndPoliciesHandler : IActionHandler
{
    internal AppActionParts Parts { get; set; } = new();

    /// <summary>
    /// Two halves. The IME half (restart, retry-timer clear) is local and delivers a changed Win32
    /// app. The MDM half is one attempt at the Intune service and may be skipped or queued; that is
    /// a partial result (exit 2). A sync that failed or could not be started ends this action with
    /// exit 1, on purpose: asking Intune to check the device is part of what this action is for.
    /// </summary>
    public int? Run(RunContext run)
    {
        // What each app's state was before anything is restarted or cleared.
        run.StepHeader(1, 5, "Check which apps need attention");
        var retries = Parts.Readout(run, "before the refresh");
        var ime = Parts.Refresh(run, RefreshDepth.RetryTimers, true);
        // Cleared timers mean the IME tries now; otherwise say when it tries by itself.
        if (ime.Skipped) { AppState.AddNextRetryFinding(run, retries, "The retry timers were not cleared, because other people are signed in."); }
        else if (ime.Ok && !ime.TimersCleared) { AppState.AddNextRetryFinding(run, retries, "The retry timers could not be cleared."); }

        run.StepHeader(5, 5, "Ask Intune to check this device (sync)");
        var sync = Parts.Sync(run);
        Parts.SyncFinding(run, sync);

        int exitCode;
        if (!ime.Ok || sync.ExitCode == 1)
        {
            exitCode = EngineExitCode.Failed;
        }
        else if (ime.Warn || sync.ExitCode != 0)
        {
            exitCode = EngineExitCode.Warnings;
        }
        else
        {
            exitCode = EngineExitCode.Success;
            // Exit 0 also when the IME half was skipped (documented): the line says what happened.
            run.Log(ime.Skipped
                ? "The Intune sync was verified. The IME restart and the retry-timer clear were skipped, because other people are signed in or their number could not be read; the IME tries failed apps again by itself, on its own schedule."
                : "The IME restarted and the app retry timers were cleared; the Intune sync was verified. Assigned apps are evaluated now; how long one takes to arrive depends on its size.");
        }

        var imeText = ime.Skipped ? "skipped (other users signed in, or their number unknown)" : ime.Ok && !ime.Warn ? "completed" : "completed with warnings";
        run.Log(string.Format(CultureInfo.InvariantCulture, "Scope: IME restart and retry-timer clear {0}; Intune sync {1}. Not checked: whether a particular app installed or every policy applied.",
            imeText, Mdm.FormatStatus(sync.Status)));
        return exitCode;
    }
}

/// <summary>Clear downloaded app content.</summary>
public sealed class ClearAppCacheHandler : IActionHandler
{
    internal AppActionParts Parts { get; set; } = new();

    /// <summary>
    /// Removes leftover unpacked app content (IMECache) and nothing else: not the Delivery
    /// Optimization cache, and the IME keeps running, so folders in use are left alone.
    /// </summary>
    public int? Run(RunContext run)
    {
        run.StepHeader(1, 1, "Remove downloaded app content (IMECache)");
        var content = Parts.ClearContent(run);

        if (content.Unreadable)
        {
            run.AddFinding(FindingLevel.Error, "APPCACHE-UNREADABLE", "The app content cache could not be read",
                "Nothing was removed. The reason is in the log.", "Restart the device and run this again.");
            return EngineExitCode.Failed;
        }
        if (content.Removed == 0 && content.Failed == 0 && content.Skipped == 0)
        {
            run.AddFinding(FindingLevel.Ok, "APPCACHE-EMPTY", "No unpacked app content was left behind",
                "The IME removes a content folder once its install finishes, so an empty cache is the normal state.",
                "If an app still fails, this cache is not the cause. Use Retry work apps and settings, or create a support package.");
        }
        else if (content.Removed > 0)
        {
            run.AddFinding(FindingLevel.Ok, "APPCACHE-CLEARED",
                "Removed " + TextFormat.Count(content.Removed, "leftover app content folder") + ", " + TextFormat.Number(content.FreedMB) + " MB",
                "A folder left behind means an install did not finish. The app is downloaded and unpacked again at its next attempt.",
                "Use Retry work apps and settings to make the IME try the app now.");
        }
        if (content.Skipped > 0)
        {
            run.AddFinding(FindingLevel.Info, "APPCACHE-IN-USE", TextFormat.Count(content.Skipped, "content folder") + " left alone: an install is running from it",
                "Removing it would pull the files out from under a running installer.", "Run this again when the install has finished.");
        }
        if (content.Failed > 0)
        {
            run.AddFinding(FindingLevel.Warning, "APPCACHE-FAILED", TextFormat.Count(content.Failed, "content folder") + " could not be removed",
                "The reason for each is in the log.", "Restart the device and run this again.");
        }

        run.Log("Scope: " + TextFormat.Count(content.Removed, "folder") + " removed (" + TextFormat.Number(content.FreedMB) + " MB). Not touched: IME service, Delivery Optimization cache.");

        if (content.Failed > 0 && content.Removed == 0) { return EngineExitCode.Failed; }
        // A folder an install still runs from is normal and stays; CHECK only when a folder could
        // not be removed, or when more folders stayed in use than were removed.
        if (content.Failed > 0 || content.Skipped > content.Removed) { return EngineExitCode.Warnings; }
        return EngineExitCode.Success;
    }
}

/// <summary>Full IME reset.</summary>
public sealed class FullResetHandler : IActionHandler
{
    internal AppActionParts Parts { get; set; } = new();

    /// <summary>
    /// The same sequence as Retry work apps and settings, one depth heavier: all Win32 app state instead
    /// of only the retry timers. The cooldown is stamped as soon as the wipe has run. A sync that
    /// failed or could not be started ends this action with exit 2, not 1, on purpose: the purpose
    /// of this action is the wipe, and the sync is only its follow-up.
    /// </summary>
    public int? Run(RunContext run)
    {
        if (!run.State.CooldownAllows("LastFullReset", run.Config.FullResetCooldownHours, "Full IME reset")) { return EngineExitCode.CooldownActive; }

        // The reset removes this state; this readout is the only record of it afterwards.
        run.StepHeader(1, 5, "Check which apps need attention");
        var retries = Parts.Readout(run, "before the full reset (the reset removes this state; this log is the only record of it)");
        // Stamped before the reset, so a run stopped halfway still starts the cooldown.
        var previousStamp = run.State.StampProvisionally("LastFullReset");
        RefreshResult ime = null;
        try
        {
            ime = Parts.Refresh(run, RefreshDepth.FullWin32State, false);
        }
        finally
        {
            if (ime == null || !ime.Wiped) { run.State.Restore("LastFullReset", previousStamp); }
        }
        if (ime.Wiped) { run.State.Stamp("LastFullReset"); }
        else if (ime.Ok) { AppState.AddNextRetryFinding(run, retries, "App state was not cleared, so the retry timers are still set."); }
        if (!ime.Ok) { return EngineExitCode.Failed; }

        run.StepHeader(5, 5, "Ask Intune to check this device (sync)");
        var sync = Parts.Sync(run);
        Parts.SyncFinding(run, sync);

        // Company Portal shows what Intune last received from this device, so it can lag behind the reset.
        const string portalText = "Company Portal can show the previous install status for a while, until this device has reported to Intune again (up to a few hours).";
        const string refreshStep = "Open Company Portal again later, or use Settings > Accounts > Access work or school > Sync to refresh it sooner.";
        if (ime.Wiped && ime.WipeFailures == 0)
        {
            // Company Portal was closed before the reset (by the user); the engine never closes it.
            run.AddFinding(FindingLevel.Ok, "FULLRESET-DONE", "Full IME reset done: all assigned apps are evaluated again",
                "Win32 app state and Company Portal's cached app status were cleared, and the IME was restarted. This takes a while. " + portalText, refreshStep);
            run.Log("Full reset done: Win32 app state and Company Portal's cached app status cleared and the IME restarted. All assigned apps are evaluated again, which takes a while. Intune sync: " +
                Mdm.FormatStatus(sync.Status) + ". " + portalText);
        }
        else if (ime.Wiped)
        {
            // The wipe ran but not every part could be cleared: a partial result, never a clean "done".
            run.AddFinding(FindingLevel.Warning, "FULLRESET-PARTIAL", "Full IME reset ran, but not everything could be cleared",
                TextFormat.Count(ime.WipeFailures, "part") + " could not be cleared; the log says which. The IME was restarted and evaluates the state that remains. " + portalText,
                "Restart the device and run the full reset again. " + refreshStep,
                "The log lists each part that could not be removed (a registry key held open, a file in use or a folder that could not be deleted).");
            run.Log("Full reset partial: " + TextFormat.Count(ime.WipeFailures, "part") + " could not be cleared. Intune sync: " + Mdm.FormatStatus(sync.Status) + ".", LogSeverity.Warning);
        }
        else
        {
            run.Log("Full reset incomplete: the IME could not be stopped, so app state was not cleared. Intune sync: " + Mdm.FormatStatus(sync.Status) + ".", LogSeverity.Warning);
        }
        if (ime.Warn || sync.ExitCode != 0) { return EngineExitCode.Warnings; }
        return EngineExitCode.Success;
    }
}
