using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.App.Core;

/// <summary>How asking Company Portal to close ended.</summary>
public enum PortalClose
{
    /// <summary>It was not running in this session.</summary>
    NotOpen,

    /// <summary>The user said yes and it closed when asked.</summary>
    Closed,

    /// <summary>The user kept it open; nothing was asked of it.</summary>
    Cancelled,

    /// <summary>It was asked to close and is still open; it was not closed by force.</summary>
    StillOpen,
}

/// <summary>
/// Company Portal open in this session is never closed by force, by any tool (Reset Company
/// Portal data, Full IME reset). The user is asked (<paramref name="confirmClose"/>, Cancel the
/// default); on yes Company Portal is asked to close as the window's close button does, and the
/// tool waits up to <see cref="UserCaches.CloseWaitSeconds"/>. The lines for the run log go to
/// <paramref name="line"/>.
/// </summary>
public static class CompanyPortalClose
{
    public static PortalClose Ask(ISessionApps apps, Func<bool> confirmClose, Action<string> line)
    {
        if (apps == null) { throw new ArgumentNullException(nameof(apps)); }
        if (confirmClose == null) { throw new ArgumentNullException(nameof(confirmClose)); }
        line ??= _ => { };
        var names = new[] { CompanyPortalReset.ProcessName };
        if (apps.Open(names).Count == 0)
        {
            line("Company Portal was not running.");
            return PortalClose.NotOpen;
        }
        line("Company Portal is open.");
        if (!confirmClose())
        {
            line("You chose to keep it open. Nothing was changed.");
            return PortalClose.Cancelled;
        }
        line("Asked Company Portal to close.");
        apps.AskToClose(CompanyPortalReset.ProcessName);
        var open = true;
        for (var waited = 0; open && waited < UserCaches.CloseWaitSeconds * 1000; waited += UserCaches.ClosePollMilliseconds)
        {
            apps.Sleep(UserCaches.ClosePollMilliseconds);
            open = apps.Open(names).Count > 0;
        }
        if (open)
        {
            line("Company Portal is still open after " + UserCaches.CloseWaitSeconds.ToString(CultureInfo.InvariantCulture) + " seconds. Nothing was changed; it was not closed by force.");
            return PortalClose.StillOpen;
        }
        line("Company Portal was closed.");
        return PortalClose.Closed;
    }
}

/// <summary>
/// Reset Company Portal data, in the user's own context: resets the app's data (it signs the user
/// out of Company Portal; apps are not removed). Company Portal open in this
/// session is never closed by force. The user is asked first (Cancel is the default); on yes it is
/// asked to close as if the user closed it, and the reset runs only once it did, within
/// <see cref="UserCaches.CloseWaitSeconds"/>. Still open: nothing is changed, and the result says so.
/// </summary>
public static class CompanyPortalReset
{
    /// <summary>Company Portal's process name.</summary>
    public const string ProcessName = "CompanyPortal";

    /// <summary>The history codes when the user kept Company Portal open, or it did not close.</summary>
    public const int CodeCancelled = 6;
    public const int CodeStillOpen = 7;

    private const string Tool = "Reset Company Portal data";

    /// <returns>The status, its card, and the history code: 0 reset, 1 failed, 3 not installed, 4 not supported, 5 blocked by a device policy, 6 cancelled (it stayed open), 7 it did not close.</returns>
    public static (string Status, Tone Tone, ResultCard Card, int Code) Run(ISessionApps apps, Func<bool> confirmClose, Action<string> progress = null) => Run(null, apps, confirmClose, progress);

    internal static (string Status, Tone Tone, ResultCard Card, int Code) Run(IProcessRunner runner, ISessionApps apps, Func<bool> confirmClose, Action<string> progress = null)
    {
        if (apps == null) { throw new ArgumentNullException(nameof(apps)); }
        if (confirmClose == null) { throw new ArgumentNullException(nameof(confirmClose)); }
        progress ??= _ => { };
        var lines = new List<string>();
        void Line(string text)
        {
            lines.Add(text);
            progress(text);
        }
        try
        {
            switch (CompanyPortalClose.Ask(apps, confirmClose, Line))
            {
                case PortalClose.Cancelled:
                    return (Tool + ": cancelled. Nothing was changed.", Tone.Neutral,
                        ResultCard.From(FindingSeverity.Info, "Nothing was changed", string.Join(Environment.NewLine, lines), "Close Company Portal when it suits you, then run Reset Company Portal data again."), CodeCancelled);
                case PortalClose.StillOpen:
                    return (Tool + ": Company Portal did not close. Nothing was changed.", Tone.Warning,
                        ResultCard.From(FindingSeverity.Warning, "Company Portal did not close", string.Join(Environment.NewLine, lines), "Close Company Portal yourself, then run Reset Company Portal data again."), CodeStillOpen);
            }

            // Reset-AppxPackage has no .NET Framework equivalent; it runs in the user's own
            // Windows PowerShell through the shared in-box cmdlet runner: a fixed,
            // readable command from its allow-list, never an encoded one.
            var result = new InboxCmdletRunner(runner).Run(InboxCommand.ResetCompanyPortal, TimeSpan.FromMinutes(2), (_, _) => { });
            return Interpret(result, lines);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            lines.Add("The reset failed: " + ex.Message);
            return Fail(lines);
        }
    }

    private const string ManualStep = "Manual alternative: Settings > Apps > Company Portal > Advanced options > Reset.";

    private static (string, Tone, ResultCard, int) Fail(List<string> lines)
        => ("The Company Portal reset failed.", Tone.Error, ResultCard.From(FindingSeverity.Error, "The Company Portal reset failed.", string.Join(Environment.NewLine, lines)), 1);

    /// <summary>What the runner's outcome means for the user, after the lines already collected.</summary>
    internal static (string Status, Tone Tone, ResultCard Card, int Code) Interpret(InboxResult result, List<string> lines)
    {
        switch (result.Outcome)
        {
            case InboxOutcome.Done:
                lines.Add("Company Portal app data was reset.");
                return ("Company Portal was reset. Start it again and sign in.", Tone.Success, ResultCard.From(FindingSeverity.Ok, "Company Portal was reset. Start it again and sign in.", string.Join(Environment.NewLine, lines)), 0);
            case InboxOutcome.NotPresent:
                lines.Add("Company Portal is not installed on this device.");
                return ("Company Portal is not installed.", Tone.Warning, ResultCard.From(FindingSeverity.Warning, "Company Portal is not installed.", string.Join(Environment.NewLine, lines)), 3);
            case InboxOutcome.NotAvailable:
                lines.Add("This Windows version cannot reset apps automatically.");
                lines.Add(ManualStep);
                return ("An automatic reset is not supported on this Windows version.", Tone.Warning, ResultCard.From(FindingSeverity.Warning, "An automatic reset is not supported on this Windows version.", string.Join(Environment.NewLine, lines)), 4);
            case InboxOutcome.PolicyRefused:
                // A policy that blocks Windows PowerShell for users is named, with the manual step.
                lines.Add("A device policy (App Control, AppLocker or the PowerShell execution policy) blocked the Windows command that resets apps.");
                lines.Add(ManualStep);
                return ("A device policy blocked the automatic reset.", Tone.Warning, ResultCard.From(FindingSeverity.Warning, "A device policy blocked the automatic reset.", string.Join(Environment.NewLine, lines)), 5);
            case InboxOutcome.TimedOut:
                lines.Add("The reset did not finish within two minutes.");
                return Fail(lines);
            case InboxOutcome.NotStarted:
                lines.Add("The reset failed: Windows PowerShell could not be started.");
                return Fail(lines);
            default:
                lines.Add("The reset failed: " + (result.ErrorOutput ?? "").Trim());
                return Fail(lines);
        }
    }
}

/// <summary>The six resets whose confirmation says what connection they need afterwards.</summary>
public enum ResetTool
{
    SignInCache,
    TeamsCache,
    CompanyPortal,
    OneDrive,
    AppContent,
    FullImeReset,
}

/// <summary>
/// What a reset needs afterwards. Clearing works offline, but signing in, syncing or
/// downloading again needs a connection. Every reset's confirmation says which one and what the
/// user is without until it is back; when the Network to Intune row is not OK, a warning line is
/// added before the button. It warns and never refuses.
/// </summary>
public static class ConnectionNeeds
{
    public const string SignInCache = "Signing in again needs a connection to Microsoft; without one, your Microsoft 365 apps stay signed out until it is back.";
    public const string TeamsCache = "Teams needs a connection to Microsoft to start again; without one, you are without Teams chats and calls until it is back.";
    public const string CompanyPortal = "Signing in to Company Portal again needs a connection; without one, you cannot install apps from Company Portal until it is back.";
    public const string OneDrive = "OneDrive needs a connection to sync again; without one, your files do not sync until it is back.";
    public const string AppContent = "Apps that install later download their content again, which needs a connection to Intune; without one, those installs wait until it is back.";
    public const string FullImeReset = "Detecting and installing the apps again needs a connection to Intune; without one, apps from Intune and Company Portal do not install or update until it is back.";

    /// <summary>The sentence of the confirmation's body.</summary>
    public static string After(ResetTool tool) => tool switch
    {
        ResetTool.SignInCache => SignInCache,
        ResetTool.TeamsCache => TeamsCache,
        ResetTool.CompanyPortal => CompanyPortal,
        ResetTool.OneDrive => OneDrive,
        ResetTool.AppContent => AppContent,
        _ => FullImeReset,
    };

    /// <summary>The Network to Intune row is there and not OK (a warning or a problem).</summary>
    public static bool NetworkNotOk(IEnumerable<HealthResult> rows)
        => rows?.FirstOrDefault(r => r != null && r.Name == HealthView.Network) is { } row && row.Status != HealthStatus.Pass;

    /// <summary>The warning line before the button when the Network to Intune row is not OK; "" otherwise.</summary>
    public static string Warning(ResetTool tool, IEnumerable<HealthResult> rows)
    {
        if (!NetworkNotOk(rows)) { return ""; }
        // The row by the plain name it shows on Home.
        return HealthView.PlainName(HealthView.Network) + " is not OK right now: " + tool switch
        {
            ResetTool.SignInCache => "signing in again may not work",
            ResetTool.TeamsCache => "Teams may not start again",
            ResetTool.CompanyPortal => "you may not be able to sign in to Company Portal",
            ResetTool.OneDrive => "OneDrive may not sync",
            ResetTool.AppContent => "apps that install later may not download",
            _ => "apps may not install or update",
        } + " until the connection is back.";
    }

    /// <summary>Warning lines joined, empty ones left out; null when there is none (no warning block).</summary>
    public static string Join(params string[] lines)
    {
        var text = string.Join(Environment.NewLine, lines.Where(l => !string.IsNullOrEmpty(l)));
        return text.Length == 0 ? null : text;
    }
}

/// <summary>The confirmation texts. Figures come from the action catalog, never typed twice.</summary>
public static class Confirmations
{
    /// <summary>The lead-in of the second confirmation, when the count changed after the first one.</summary>
    public static string SessionsChanged(int otherSessions)
        => string.Format(CultureInfo.InvariantCulture, "The number of people signed in to this device changed since you confirmed (now {0}).", otherSessions);

    public static string OtherSessionsWarning(ToolbeltAction action, int otherSessions)
    {
        if (otherSessions == 0 || !ActionCatalog.Get(action).AffectsAllSessions) { return ""; }
        if (otherSessions < 0)
        {
            return "The number of other people signed in to this device could not be determined, so this cannot run now.";
        }
        var who = Words.Count(otherSessions, "other user session") + (otherSessions == 1 ? " is" : " are") + " signed in to this device. ";
        var all = otherSessions == 1 ? "that session" : "all of them";
        return action switch
        {
            ToolbeltAction.RepairWU => who + "A repair or an update install slows the device for everyone, and a restart it may need affects " + all + ".",
            ToolbeltAction.FreeDiskSpace => who + "The cleanup slows the device for everyone while it runs.",
            ToolbeltAction.RenewIpAddress => who + "Their connection on this adapter drops for a moment too.",
            _ => who + "The reset affects " + all + ".",
        };
    }

    public static string Text(ToolbeltAction action) => action switch
    {
        ToolbeltAction.ClearAppCache =>
            "This removes app content that installs left behind on this device. Folders that are in use are left alone, and apps are downloaded again when they next install. " + ConnectionNeeds.AppContent,
        ToolbeltAction.FullReset =>
            "The full reset removes all Intune app state on this device. Every app is detected again and pending installs start from scratch. This can take a while and is only needed when Retry work apps and settings did not help. Company Portal must be closed: if it is open, you are asked before it is closed, and it is never closed by force. " + ConnectionNeeds.FullImeReset,
        ToolbeltAction.RepairWU => string.Join(Environment.NewLine,
            "This first checks whether the device is actually behind on updates, then runs only the repair that fits what it finds.",
            "",
            "- If nothing is wrong, it stops within seconds and changes nothing.",
            "- If Windows Update offers updates that were never installed, it downloads and installs them now.",
            string.Format(CultureInfo.InvariantCulture, "- A repair or an install usually takes 15 to 60 minutes and is stopped after {0} at most. The device stays usable but may be slower.", ActionText.WorkLimitText(ToolbeltAction.RepairWU)),
            "- Do not shut down the device or let it sleep while the repair runs. You can close this window.",
            "- A restart may be needed afterwards; the result says so."),
        ToolbeltAction.FreeDiskSpace => string.Join(Environment.NewLine,
            "This frees space on the system drive:",
            "",
            "- Old files in your own temporary folder (not used for a day) are removed first.",
            "- Windows' Delivery Optimization cache is cleared; updates are downloaded again when needed.",
            "- Old versions of Windows components that newer updates replaced are removed. Installed updates can still be removed afterwards.",
            "- This can take 10 to 30 minutes and slows the device while it runs. You can close this window."),
        _ => "",
    };

    public const string SignInCacheReset =
        "This removes your cached work account sign-in from this device. Word, Excel, PowerPoint, Outlook, OneNote, Access and Teams must be closed first; if one is open, you are asked before it is closed. Afterwards, sign out of Windows and sign in again; your Microsoft 365 apps then sign in again, on a work device usually without asking you. Use it only when Refresh work account sign-in did not help. " + ConnectionNeeds.SignInCache;

    /// <summary>The question before Reset sign-in cache closes open apps.</summary>
    public const string CloseAppsHeading = "Close open apps first";

    /// <summary>The button that closes the apps; Cancel stays the default.</summary>
    public const string CloseAppsButton = "C_lose them and continue";

    public const string CloseAppsWarning = "Unsaved work in these apps may be lost. Save your work first.";

    /// <summary>The body of the question, naming the open apps as people know them.</summary>
    public static string CloseApps(IReadOnlyList<string> apps)
    {
        var one = apps != null && apps.Count == 1;
        return "Reset sign-in cache needs " + (one ? "this app" : "these apps") + " closed: " + Words.List(apps) + "." + Environment.NewLine + Environment.NewLine
            + "This tool can ask " + (one ? "it" : "them") + " to close now, as if you closed " + (one ? "it" : "them") + " yourself, and waits up to "
            + UserCaches.CloseWaitSeconds.ToString(CultureInfo.InvariantCulture) + " seconds. If an app stays open, for example because it asks whether to save your work, nothing is changed and no app is closed by force.";
    }

    /// <summary>
    /// The first question of Clear Teams cache. The app never closes Teams on its own; the app cannot
    /// see whether a call is running, so the warning says that closing Teams ends one. Cancel is the default.
    /// </summary>
    public const string TeamsCache =
        "This clears Teams' cache in your profile. Teams must be closed for that: if it is running, you are asked to quit it yourself, or you can let this app end it. Your chats and files stay in Microsoft 365. Teams starts a little slower the first time afterwards. " + ConnectionNeeds.TeamsCache;

    public const string TeamsCacheWarning = "A call or meeting in Teams ends when Teams is quit or ended.";

    public const string TeamsCacheButton = "_Clear Teams cache";

    /// <summary>The question while Teams runs: quit it yourself and Continue (the default), End Teams for me, or Cancel.</summary>
    public const string TeamsRunningHeading = "Teams is running";

    public const string TeamsRunning = "Quit Teams yourself: right-click its icon in the notification area and choose Quit. Then choose Continue.";

    /// <summary>The same question after Continue, while Teams still runs.</summary>
    public const string TeamsStillRunning = "Teams is still running. Quit it yourself: right-click its icon in the notification area and choose Quit. Then choose Continue.";

    public const string TeamsRunningWarning = "End Teams for me ends Teams at once: a call or meeting in Teams ends. Cancel clears nothing.";

    public const string TeamsContinueButton = "C_ontinue";

    public const string TeamsEndForMeButton = "_End Teams for me";

    public const string CompanyPortalReset =
        "This resets the Company Portal app: it clears its data and signs you out of it. Your installed apps are not removed. If Company Portal is open, you are asked before it is closed. " + ConnectionNeeds.CompanyPortal;

    /// <summary>The question before Reset Company Portal data closes Company Portal; Cancel stays the default.</summary>
    public const string CloseCompanyPortalHeading = "Company Portal is open";

    public const string CloseCompanyPortal =
        "Company Portal is open. Close it to continue? This tool asks it to close, as if you closed it yourself, and waits up to 20 seconds. If it stays open, nothing is changed and it is not closed by force.";

    public const string CloseCompanyPortalButton = "C_lose it and continue";

    /// <summary>The history results of a Full IME reset that did not start because Company Portal stayed open.</summary>
    public const string FullResetPortalCancelled = "cp-cancelled";

    public const string FullResetPortalStillOpen = "cp-open";

    public const string RepairWuRestartPending =
        "A restart is already pending on this device. Restart first, then check for updates again. Only run this repair if updates still fail after the restart.";
}
