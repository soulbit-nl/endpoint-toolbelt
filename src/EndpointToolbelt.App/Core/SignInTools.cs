using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace EndpointToolbelt.App.Core;

/// <summary>What a user-context tool found: the status line, its card and the history code.</summary>
public sealed class ToolOutcome
{
    public string Status { get; set; }
    public Tone Tone { get; set; }
    public ResultCard Card { get; set; }

    /// <summary>0 done, 1 failed, 2 not verified, 3 nothing to do or not possible here, 4 not supported, 5 apps still open.</summary>
    public int Code { get; set; }

    internal static ToolOutcome Of(FindingSeverity severity, Tone tone, int code, string status, string title, string detail, string next = "")
        => new() { Status = status, Tone = tone, Code = code, Card = ResultCard.From(severity, title, detail, next) };
}

/// <summary>
/// "Refresh work account sign-in": dsregcmd /refreshprt in the user's own context, verified
/// by the sign-in token's times in dsregcmd /status (Microsoft's troubleshooting runs both as the
/// user). Windows renews the token in the background, so they are checked for a minute. A renewal
/// counts when the update time or the expiry time moved forward: on a real device Windows renewed
/// the token (a later expiry) and left AzureAdPrtUpdateTime as it was.
/// </summary>
public static class SignInRefresh
{
    public const int WaitSeconds = 60;
    public const int PollSeconds = 5;

    /// <param name="status">dsregcmd /status in the user's context.</param>
    /// <param name="refresh">dsregcmd /refreshprt; returns its exit code and output.</param>
    /// <param name="sleep">Waits the given seconds between checks.</param>
    public static ToolOutcome Run(Func<DsregInfo> status, Func<(int ExitCode, string Output)> refresh, Action<int> sleep)
    {
        const string name = "Refresh work account sign-in";
        var before = status();
        if (!string.Equals(before.AzureAdJoined, "YES", StringComparison.OrdinalIgnoreCase))
        {
            return ToolOutcome.Of(FindingSeverity.Warning, Tone.Warning, 3, name + ": this device is not joined to Microsoft Entra ID.",
                "This device is not joined to Microsoft Entra ID", "There is no work account sign-in on this device to refresh.", "Contact your IT support.");
        }
        var (exit, output) = refresh();
        if (exit != 0)
        {
            // An older Windows does not know the switch; locking and unlocking also renews the token.
            return ToolOutcome.Of(FindingSeverity.Warning, Tone.Warning, 4, name + ": not supported here. Lock and unlock the device instead.",
                "This Windows version cannot refresh the sign-in on request",
                "dsregcmd /refreshprt ended with " + exit.ToString(CultureInfo.InvariantCulture) + ". " + (output ?? "").Trim(),
                "Lock the device and unlock it while connected to the network: that renews the sign-in too.");
        }
        DsregInfo after = before;
        for (var waited = 0; waited < WaitSeconds; waited += PollSeconds)
        {
            sleep(PollSeconds);
            after = status();
            if (Renewed(before, after)) { break; }
        }
        if (Renewed(before, after))
        {
            var detail = Later(before.PrtExpiresUtc, after.PrtExpiresUtc)
                ? "Valid until " + TimeText.InLocalTime(after.PrtExpiresUtc.Value) + "."
                : "Renewed at " + TimeText.InLocalTime(after.PrtUpdatedUtc.Value) + ".";
            return ToolOutcome.Of(FindingSeverity.Ok, Tone.Success, 0, name + ": the sign-in is renewed.",
                "Your work account sign-in is renewed", detail, "Nothing else to do.");
        }
        return ToolOutcome.Of(FindingSeverity.Warning, Tone.Warning, 2, name + ": requested, but not confirmed within a minute.",
            "The renewal was requested, but not confirmed",
            "Windows accepted the request, but the sign-in token was not renewed within a minute. It may still be renewed in the background.",
            "Lock and unlock the device while connected to the network. If sign-in problems stay, use Reset sign-in cache or contact your IT support.");
    }

    /// <summary>The token was renewed: its update time or its expiry time moved forward.</summary>
    internal static bool Renewed(DsregInfo before, DsregInfo after)
        => Later(before.PrtUpdatedUtc, after.PrtUpdatedUtc) || Later(before.PrtExpiresUtc, after.PrtExpiresUtc);

    private static bool Later(DateTime? before, DateTime? after) => after != null && (before == null || after > before);
}

/// <summary>The apps running in the user's own session; tests replace it with a fake process list.</summary>
public interface ISessionApps
{
    /// <summary>The process names of <paramref name="names"/> with a process in this session, in the order given.</summary>
    IReadOnlyList<string> Open(IReadOnlyList<string> names);

    /// <summary>Asks every process with this name in this session to close its main window, as the user would. Never ends a process.</summary>
    void AskToClose(string name);

    void Sleep(int milliseconds);
}

/// <summary>The apps of one Windows session, from the real process list.</summary>
public sealed class SessionApps : ISessionApps
{
    private readonly int _session;

    public SessionApps(int session)
    {
        _session = session;
    }

    public IReadOnlyList<string> Open(IReadOnlyList<string> names) => UserCaches.OpenInSession(names, _session);

    public void AskToClose(string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                try
                {
                    // The same message as the window's close button: the app may ask to save first.
                    if (p.SessionId == _session) { p.CloseMainWindow(); }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    public void Sleep(int milliseconds) => System.Threading.Thread.Sleep(milliseconds);
}

/// <summary>How Clear Teams cache got Teams closed.</summary>
public enum TeamsClose
{
    /// <summary>Teams was not running in this session.</summary>
    NotOpen,

    /// <summary>Teams ran; the user quit it and chose Continue.</summary>
    QuitByUser,

    /// <summary>Teams ran; the user chose End Teams for me and it was ended.</summary>
    Ended,

    /// <summary>Teams ran; the user chose Cancel and nothing was cleared.</summary>
    Cancelled,

    /// <summary>The user chose End Teams for me, and Teams still ran after it was ended.</summary>
    StillOpen,
}

/// <summary>The user's answer while Teams runs: they quit it and continue, let the app end it, or cancel.</summary>
public enum TeamsChoice
{
    Cancel,
    Continue,
    EndForMe,
}

/// <summary>The names people know the apps by, for the process names the tools check.</summary>
public static class ProcessNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WINWORD"] = "Word",
        ["EXCEL"] = "Excel",
        ["POWERPNT"] = "PowerPoint",
        ["OUTLOOK"] = "Outlook",
        ["olk"] = "Outlook",
        ["ONENOTE"] = "OneNote",
        ["MSACCESS"] = "Access",
        ["ms-teams"] = "Teams",
    };

    /// <summary>The app's name; a process this list does not know keeps its own name.</summary>
    public static string Friendly(string processName)
        => processName != null && Known.TryGetValue(processName, out var name) ? name : processName ?? "";

    /// <summary>The apps' names in the order given, each once (classic and new Outlook are both "Outlook").</summary>
    public static List<string> Friendly(IEnumerable<string> processNames)
        => processNames.Select(Friendly).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>Closing apps and clearing caches in the user's own profile.</summary>
public static class UserCaches
{
    /// <summary>The Microsoft 365 apps that hold work account tokens; they must be closed first (the tool asks, then asks them to close).</summary>
    public static readonly string[] SignInApps = { "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "ONENOTE", "MSACCESS", "ms-teams", "olk" };

    /// <summary>How long the apps get to close after they were asked to.</summary>
    public const int CloseWaitSeconds = 20;

    public const int ClosePollMilliseconds = 500;

    private const string SignInCacheName = "Reset sign-in cache";

    /// <summary>
    /// The folders "Reset sign-in cache" empties, as Microsoft's Microsoft 365 activation
    /// troubleshooting names them: the token broker's account files of two system packages.
    /// </summary>
    public static IEnumerable<string> SignInCacheFolders(string localAppData) => new[]
    {
        Path.Combine(localAppData, "Packages", "Microsoft.AAD.BrokerPlugin_cw5n1h2txyewy", "AC", "TokenBroker", "Accounts"),
        Path.Combine(localAppData, "Packages", "Microsoft.Windows.CloudExperienceHost_cw5n1h2txyewy", "AC", "TokenBroker", "Accounts"),
    };

    /// <summary>The new Teams cache folder Microsoft documents.</summary>
    public static string TeamsCacheFolder(string localAppData)
        => Path.Combine(localAppData, "Packages", "MSTeams_8wekyb3d8bbwe", "LocalCache", "Microsoft", "MSTeams");

    /// <summary>
    /// Empties the sign-in cache folders, refused while a Microsoft 365 app runs in this session
    /// (<paramref name="openApps"/> names them).
    /// </summary>
    public static ToolOutcome ResetSignInCache(string localAppData, string profile, IReadOnlyCollection<string> openApps, DateTime nowUtc)
    {
        if (openApps.Count > 0)
        {
            var open = Words.List(ProcessNames.Friendly(openApps));
            return ToolOutcome.Of(FindingSeverity.Warning, Tone.Warning, 5, SignInCacheName + ": close " + open + " first.",
                "Close your Microsoft 365 apps first", "Still open: " + open + ". Nothing was changed.", "Close them, then run Reset sign-in cache again.");
        }
        return ResetSignInCacheFolders(localAppData, profile, nowUtc, "");
    }

    /// <summary>
    /// Reset sign-in cache with the open Microsoft 365 apps closed first. When apps run in
    /// this session, <paramref name="confirmClose"/> is asked with their names; on yes each is
    /// asked to close its main window (never ended), and the reset runs only once all of them
    /// closed within <see cref="CloseWaitSeconds"/>. Otherwise nothing is changed and the app that
    /// stayed open is named. <paramref name="progress"/> gets the lines for the run log.
    /// </summary>
    public static ToolOutcome ResetSignInCache(string localAppData, string profile, ISessionApps apps, Func<IReadOnlyList<string>, bool> confirmClose, Action<string> progress, DateTime nowUtc)
    {
        if (apps == null) { throw new ArgumentNullException(nameof(apps)); }
        if (confirmClose == null) { throw new ArgumentNullException(nameof(confirmClose)); }
        progress ??= _ => { };
        var open = apps.Open(SignInApps);
        if (open.Count == 0) { return ResetSignInCacheFolders(localAppData, profile, nowUtc, ""); }

        var shown = ProcessNames.Friendly(open);
        var openText = Words.List(shown);
        var seconds = CloseWaitSeconds.ToString(CultureInfo.InvariantCulture);
        progress("Microsoft 365 apps still open: " + openText + ".");
        if (!confirmClose(shown))
        {
            progress("You chose not to close them. Nothing was changed.");
            return ToolOutcome.Of(FindingSeverity.Info, Tone.Neutral, 5, SignInCacheName + ": cancelled. Nothing was changed.",
                "Nothing was changed", "Still open: " + openText + ". You chose not to close " + (shown.Count == 1 ? "it" : "them") + ".",
                "Save your work and close " + openText + ", then run Reset sign-in cache again.");
        }

        progress("Asked to close: " + openText + ".");
        foreach (var name in open) { apps.AskToClose(name); }
        var still = open;
        for (var waited = 0; still.Count > 0 && waited < CloseWaitSeconds * 1000; waited += ClosePollMilliseconds)
        {
            apps.Sleep(ClosePollMilliseconds);
            still = apps.Open(SignInApps);
        }
        var stillShown = ProcessNames.Friendly(still);
        var closed = shown.Where(n => !stillShown.Contains(n)).ToList();
        if (closed.Count > 0) { progress("Closed: " + Words.List(closed) + "."); }
        if (still.Count > 0)
        {
            var who = Words.List(stillShown);
            var one = stillShown.Count == 1;
            progress("Still open after " + seconds + " seconds: " + who + ". Nothing was changed.");
            return ToolOutcome.Of(FindingSeverity.Warning, Tone.Warning, 5, SignInCacheName + ": " + who + " did not close. Nothing was changed.",
                who + " did not close",
                (closed.Count > 0 ? Words.List(closed) + " closed. " : "") + who + (one ? " is" : " are") + " still open after " + seconds
                    + " seconds, for example because " + (one ? "it asks" : "one asks") + " whether to save your work. Nothing was changed, and no app was closed by force.",
                "Switch to " + who + ", save your work and close " + (one ? "it" : "them") + " (also from its icon in the notification area, if it has one), then run Reset sign-in cache again.");
        }
        return ResetSignInCacheFolders(localAppData, profile, nowUtc, Words.List(closed) + (closed.Count == 1 ? " was" : " were") + " closed. ");
    }

    /// <summary>Empties the sign-in cache folders; <paramref name="note"/> starts the card's detail (the apps closed first).</summary>
    private static ToolOutcome ResetSignInCacheFolders(string localAppData, string profile, DateTime nowUtc, string note)
    {
        const string name = SignInCacheName;
        var removed = 0;
        var skipped = 0;
        var found = false;
        foreach (var folder in SignInCacheFolders(localAppData))
        {
            if (!Directory.Exists(folder)) { continue; }
            found = true;
            var result = UserTempCleanup.Run(folder, profile, nowUtc, TimeSpan.Zero);
            if (result.NotRun != null) { skipped++; continue; }
            removed += result.FilesRemoved;
            skipped += result.Skipped;
        }
        if (!found || removed == 0 && skipped == 0)
        {
            // After apps were closed something did change, so the status does not say otherwise.
            return ToolOutcome.Of(FindingSeverity.Info, Tone.Neutral, 3, name + ": there was no cached sign-in to remove." + (note.Length == 0 ? " Nothing was changed." : ""),
                "There was no cached sign-in to remove", note + "The token broker's account folders are empty or missing.", "If sign-in problems stay, contact your IT support.");
        }
        // Items in use are normal (Windows' own sign-in keeps some open); only when most stayed is it a point to check.
        if (CleanupRule.MostLeft(removed, skipped))
        {
            return ToolOutcome.Of(FindingSeverity.Warning, Tone.Warning, 2, name + ": most of the cached sign-in is still in use.",
                "Most of the cached sign-in could not be removed",
                note + Words.Count(removed, "cached account file") + " removed; " + CleanupRule.InUse(skipped, "Windows sign-in") + ".",
                "Restart the device, then run Reset sign-in cache again before you open any app.");
        }
        var detail = note + Words.Count(removed, "cached account file") + " removed." + (skipped > 0 ? " " + CleanupRule.InUse(skipped, "Windows sign-in") + "; that is normal." : "");
        return ToolOutcome.Of(FindingSeverity.Ok, Tone.Success, 0,
            name + ": done. Sign out of Windows and sign in again.", "The sign-in cache was reset", detail,
            "Sign out of Windows and sign in again (or restart). Your Microsoft 365 apps then sign in again; on a device joined to Microsoft Entra ID that normally happens without asking you.");
    }

    /// <summary>The new Teams' process name.</summary>
    public const string TeamsProcessName = "ms-teams";

    /// <summary>The history code of a Teams cache clear the user cancelled while Teams ran: Teams still runs.</summary>
    public const int TeamsCancelledCode = 5;

    private const string TeamsCacheName = "Clear Teams cache";

    /// <summary>
    /// Gets the new Teams in this session closed, after the user confirmed the tool. The app never
    /// closes Teams on its own: while Teams runs, <paramref name="ask"/> asks the user to quit it
    /// themselves (its argument says whether Teams still ran after a Continue). Continue looks again;
    /// End Teams for me ends it through <paramref name="endTeams"/>; Cancel clears nothing. The lines
    /// for the run log go to <paramref name="line"/>.
    /// </summary>
    public static TeamsClose CloseTeams(ISessionApps apps, Func<int> endTeams, Func<bool, TeamsChoice> ask, Action<string> line)
    {
        if (apps == null) { throw new ArgumentNullException(nameof(apps)); }
        if (endTeams == null) { throw new ArgumentNullException(nameof(endTeams)); }
        if (ask == null) { throw new ArgumentNullException(nameof(ask)); }
        line ??= _ => { };
        if (apps.Open(TeamsProcess).Count == 0)
        {
            line("Teams was not running.");
            return TeamsClose.NotOpen;
        }
        line("Teams is running. You were asked to quit it.");
        var again = false;
        while (true)
        {
            switch (ask(again))
            {
                case TeamsChoice.Continue:
                    if (apps.Open(TeamsProcess).Count == 0)
                    {
                        line("You quit Teams.");
                        return TeamsClose.QuitByUser;
                    }
                    line("Teams was still running after Continue.");
                    again = true;
                    break;
                case TeamsChoice.EndForMe:
                    endTeams();
                    if (apps.Open(TeamsProcess).Count > 0)
                    {
                        line("You chose End Teams for me, but Teams is still running.");
                        return TeamsClose.StillOpen;
                    }
                    line("Teams was ended because you chose End Teams for me.");
                    return TeamsClose.Ended;
                default:
                    line("You chose Cancel. Teams was not closed and nothing was cleared.");
                    return TeamsClose.Cancelled;
            }
        }
    }

    /// <summary>
    /// Clear Teams cache, after the user confirmed it: Teams in this session must be closed first
    /// (<see cref="CloseTeams"/>: the user quits it, or chooses End Teams for me), then its cache folder
    /// is emptied. Not installed: nothing is asked. Cancel while Teams runs: nothing is cleared.
    /// </summary>
    public static ToolOutcome ClearTeamsCache(string localAppData, string profile, ISessionApps apps, Func<int> endTeams, Func<bool, TeamsChoice> ask, Action<string> progress, DateTime nowUtc)
    {
        const string name = TeamsCacheName;
        progress ??= _ => { };
        var folder = TeamsCacheFolder(localAppData);
        if (!Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(folder)))))
        {
            return ToolOutcome.Of(FindingSeverity.Info, Tone.Neutral, 3, name + ": the new Teams is not installed for you. Nothing was changed.",
                "The new Teams is not installed for you", "There is no Teams cache to clear.", "");
        }
        var how = CloseTeams(apps, endTeams, ask, progress);
        if (how == TeamsClose.Cancelled)
        {
            return ToolOutcome.Of(FindingSeverity.Info, Tone.Neutral, TeamsCancelledCode, name + ": cancelled. Nothing was changed.",
                "Nothing was changed",
                "Teams is still running and you chose Cancel, so its cache was not cleared.",
                "Quit Teams first: right-click its icon in the notification area and choose Quit. Then run Clear Teams cache again.");
        }
        var note = TeamsNote(how);
        if (!Directory.Exists(folder))
        {
            return ToolOutcome.Of(FindingSeverity.Info, Tone.Neutral, 3, name + ": there was no cache to clear." + (note.Length == 0 ? " Nothing was changed." : ""),
                "There was no Teams cache to clear", note + "The cache folder does not exist yet.", "");
        }
        var result = UserTempCleanup.Run(folder, profile, nowUtc, TimeSpan.Zero);
        if (result.NotRun != null)
        {
            return ToolOutcome.Of(FindingSeverity.Error, Tone.Error, 1, name + " did not succeed: " + result.NotRun + ".",
                "The Teams cache could not be cleared", note + "Not cleared: " + result.NotRun + ".", "Contact your IT support.");
        }
        return ClearTeamsCacheOutcome(how, result.FilesRemoved, result.Skipped, result.BytesFreed);
    }

    /// <summary>What the result card says first about closing Teams.</summary>
    private static string TeamsNote(TeamsClose how) => how switch
    {
        TeamsClose.QuitByUser => "You quit Teams. ",
        TeamsClose.Ended => "Teams was ended because you chose End Teams for me. ",
        _ => "",
    };

    /// <summary>
    /// The result of a Teams cache clear. OK when Teams was closed (or was not running)
    /// and the cache was cleared: files still in use by Teams' helpers, the Teams add-in of Outlook
    /// or WebView2 stay, which is normal, and the detail says so. A point to check only when Teams
    /// still ran after it was ended, or when less than half of the cache could be removed.
    /// </summary>
    public static ToolOutcome ClearTeamsCacheOutcome(TeamsClose how, int removed, int inUse, long bytesFreed)
    {
        const string name = TeamsCacheName;
        const string usedBy = "Teams helpers, the Teams add-in of Outlook or WebView2";
        var removedText = Words.Count(removed, "file") + " removed (" + (bytesFreed / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB)";
        if (how == TeamsClose.StillOpen)
        {
            return ToolOutcome.Of(FindingSeverity.Warning, Tone.Warning, 2, name + ": Teams did not close, so part of its cache stayed.",
                "Teams could not be closed",
                "Teams was still running after it was ended. " + removedText + (inUse > 0 ? "; " + CleanupRule.InUse(inUse, "Teams") : "") + ".",
                "Quit Teams yourself (right-click its icon in the notification area and choose Quit), then run Clear Teams cache again.");
        }
        var note = TeamsNote(how);
        if (CleanupRule.MostLeft(removed, inUse))
        {
            return ToolOutcome.Of(FindingSeverity.Warning, Tone.Warning, 2, name + ": most of the cache is still in use.",
                "Most of the Teams cache could not be removed",
                note + removedText + "; " + CleanupRule.InUse(inUse, usedBy) + ".",
                "Close Outlook too, or restart the device, then run Clear Teams cache again.");
        }
        var detail = note + removedText + "."
            + (inUse > 0 ? " " + CleanupRule.InUse(inUse, usedBy) + "; that is normal." : "");
        return ToolOutcome.Of(FindingSeverity.Ok, Tone.Success, 0,
            name + ": done. Start Teams again.", "The Teams cache was cleared", detail, "Start Teams again; the first start takes a little longer.");
    }

    /// <summary>The running processes with these names in the given session.</summary>
    public static List<string> OpenInSession(IEnumerable<string> names, int session)
    {
        var open = new List<string>();
        foreach (var n in names)
        {
            foreach (var p in Process.GetProcessesByName(n))
            {
                using (p)
                {
                    if (p.SessionId == session && !open.Contains(n)) { open.Add(n); }
                }
            }
        }
        return open;
    }

    private static readonly string[] TeamsProcess = { TeamsProcessName };

    /// <summary>
    /// Ends the new Teams in the given session; how many processes ended. Only after the user
    /// confirmed the second question of Clear Teams cache, once Teams was asked to close and still ran.
    /// </summary>
    public static int EndTeams(int session)
    {
        var closed = 0;
        foreach (var p in Process.GetProcessesByName(TeamsProcessName))
        {
            using (p)
            {
                if (p.SessionId != session) { continue; }
                try
                {
                    p.Kill();
                    p.WaitForExit(10000);
                    closed++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return closed;
    }
}

/// <summary>
/// What a cleaning tool says about files it left because they were in use. Files in use are
/// normal (an app's helpers, an add-in, WebView2, Windows itself keep some open) and never make a
/// clean a point to check by themselves; only when less than half of what was there could be
/// removed is the result CHECK.
/// </summary>
public static class CleanupRule
{
    /// <summary>Most of it stayed: fewer items were removed than were left in use.</summary>
    public static bool MostLeft(int removed, int inUse) => inUse > 0 && removed < inUse;

    /// <summary>"58 items still in use by Teams stayed", in plain words.</summary>
    public static string InUse(int inUse, string usedBy)
        => Words.Count(inUse, "item") + " still in use by " + usedBy + " stayed";
}
