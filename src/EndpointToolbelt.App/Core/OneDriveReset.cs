using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace EndpointToolbelt.App.Core;

/// <summary>How a started program went: Windows did not start it, it ended, or it still ran when the wait was over.</summary>
public enum ProgramRun
{
    NotStarted,
    Exited,
    StillRunning,
}

/// <summary>The OneDrive sync app as the reset needs it; the real one is <see cref="OneDriveOnThisPc"/>, tests use a fake.</summary>
public interface IOneDrive
{
    /// <summary>The full path of OneDrive.exe in one of the places Microsoft documents, or null when it is not installed.</summary>
    string Find();

    /// <summary>OneDrive runs in this user's session.</summary>
    bool IsRunning();

    /// <summary>Starts the program with these arguments as the user and waits at most <paramref name="wait"/> for it to end.</summary>
    ProgramRun Start(string path, string arguments, TimeSpan wait);

    /// <summary>Waits until OneDrive runs in this session, at most <paramref name="limit"/>; true when it does.</summary>
    bool WaitUntilRunning(TimeSpan limit);
}

/// <summary>
/// OneDrive as Microsoft's "Reset OneDrive" article names it, read-only until the user confirms:
/// <c>%LOCALAPPDATA%\Microsoft\OneDrive\onedrive.exe</c> (per user), then the per-machine installs under
/// Program Files and Program Files (x86). Only an existing file counts.
/// </summary>
public sealed class OneDriveOnThisPc : IOneDrive
{
    private const string ProcessName = "OneDrive";

    public string Find()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramW6432");
        if (string.IsNullOrEmpty(programFiles)) { programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles); }
        foreach (var root in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive"),
            Path.Combine(programFiles, "Microsoft OneDrive"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft OneDrive"),
        })
        {
            if (string.IsNullOrEmpty(root) || !Path.IsPathRooted(root)) { continue; }
            var path = OfficeSafeMode.CleanPath(Path.Combine(root, "OneDrive.exe"));
            if (path != null) { return path; }
        }
        return null;
    }

    public bool IsRunning()
    {
        int session;
        using (var self = Process.GetCurrentProcess()) { session = self.SessionId; }
        var running = false;
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            using (p)
            {
                if (p.SessionId == session) { running = true; }
            }
        }
        return running;
    }

    public ProgramRun Start(string path, string arguments, TimeSpan wait)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, arguments ?? "") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
            if (process == null) { return ProgramRun.NotStarted; }
            if (wait <= TimeSpan.Zero) { return ProgramRun.StillRunning; }
            return process.WaitForExit((int)Math.Min(int.MaxValue, wait.TotalMilliseconds)) ? ProgramRun.Exited : ProgramRun.StillRunning;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return ProgramRun.NotStarted;
        }
    }

    public bool WaitUntilRunning(TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (IsRunning()) { return true; }
            if (clock.Elapsed >= limit) { return false; }
            Thread.Sleep(TimeSpan.FromSeconds(5));
        }
    }
}

/// <summary>How a OneDrive reset went. Every reset that ran is "Reset requested"; whether files sync again is a separate outcome that is never claimed.</summary>
public enum OneDriveResetResult
{
    /// <summary>The reset ran and OneDrive started again by itself.</summary>
    ResetRequested,

    /// <summary>The reset ran; OneDrive did not start again within the wait, so the app started it.</summary>
    ResetRequestedStarted,

    /// <summary>The reset ran; OneDrive did not start again and Windows did not start it either.</summary>
    ResetRequestedNotRunning,
    NotInstalled,

    /// <summary>Windows did not start <c>onedrive.exe /reset</c>: nothing was reset.</summary>
    CouldNotReset,
}

/// <summary>One OneDrive reset: the result only; no account, path or file name.</summary>
public sealed class OneDriveResetOutcome
{
    public OneDriveResetResult Result { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }
}

/// <summary>
/// "OneDrive doesn't sync": Microsoft's documented reset, <c>onedrive.exe /reset</c>, run as the
/// user after a confirmation (Cancel is its default), then OneDrive started again when it did not
/// restart itself within a few minutes, as the article says to do by hand. A paused OneDrive has no
/// documented user-level resume command, so the path says how to resume it from its icon instead.
/// </summary>
public static class OneDriveReset
{
    public const string ProblemTitle = "OneDrive doesn't sync";
    public const string ProblemSubtitle = "Files stay on \"pending\" or show red crosses";
    public const string ToolName = "Reset OneDrive";
    public const string ShortName = "OneDrive";
    public const string HistoryName = "ResetOneDrive";
    public const string Argument = "/reset";
    public const string NotInstalledText = "Not available: OneDrive is not installed on this PC";

    /// <summary>How long the reset command itself may take before the app stops waiting for it.</summary>
    public static readonly TimeSpan ResetWait = TimeSpan.FromMinutes(2);

    /// <summary>How long OneDrive gets to start again by itself ("a few minutes") before the app starts it.</summary>
    public static readonly TimeSpan RestartWait = TimeSpan.FromMinutes(3);

    /// <summary>The confirmation's text, word for word.</summary>
    public const string ConfirmText = "All accounts in OneDrive, work and personal, sync again from the start. Files stay where they are. This can take a while. " + ConnectionNeeds.OneDrive;

    /// <summary>The path's line about a paused OneDrive: no documented command resumes it, so the icon's own steps.</summary>
    public const string ResumeHint = "Paused? Select the OneDrive icon in the notification area, then the pause icon or the yellow box in its activity center to resume syncing. Try that first.";

    /// <summary>Runs the reset after the user confirmed; <paramref name="waiting"/> is told when the app waits for OneDrive to start again.</summary>
    public static OneDriveResetOutcome Run(IOneDrive oneDrive, Func<DateTime> utcNow = null, Action waiting = null)
    {
        var now = utcNow ?? (() => DateTime.UtcNow);
        var outcome = new OneDriveResetOutcome { StartedUtc = now() };
        OneDriveResetOutcome End(OneDriveResetResult result)
        {
            outcome.Result = result;
            outcome.EndedUtc = now();
            return outcome;
        }
        var path = oneDrive.Find();
        if (path == null) { return End(OneDriveResetResult.NotInstalled); }
        var reset = oneDrive.Start(path, Argument, ResetWait);
        if (reset == ProgramRun.NotStarted) { return End(OneDriveResetResult.CouldNotReset); }
        // The reset command still running counts as OneDrive running: no second instance is started.
        if (reset == ProgramRun.StillRunning) { return End(OneDriveResetResult.ResetRequested); }
        waiting?.Invoke();
        if (oneDrive.WaitUntilRunning(RestartWait)) { return End(OneDriveResetResult.ResetRequested); }
        return End(oneDrive.Start(path, "", TimeSpan.Zero) == ProgramRun.NotStarted ? OneDriveResetResult.ResetRequestedNotRunning : OneDriveResetResult.ResetRequestedStarted);
    }

    public static string Slug(OneDriveResetResult result) => result switch
    {
        OneDriveResetResult.ResetRequested => "reset-requested",
        OneDriveResetResult.ResetRequestedStarted => "reset-requested-started",
        OneDriveResetResult.ResetRequestedNotRunning => "reset-requested-not-running",
        OneDriveResetResult.NotInstalled => "not-installed",
        _ => "could-not-reset",
    };

    /// <summary>The history code: 0 reset requested, 1 reset requested and started again, 2 reset requested but not running, 3 not installed, 4 could not reset.</summary>
    public static int Code(OneDriveResetResult result) => (int)result;

    /// <summary>What OneDrive did after the reset, for the run log: started by itself, started by the app, not running, or none.</summary>
    private static string Restart(OneDriveResetResult result) => result switch
    {
        OneDriveResetResult.ResetRequested => "self",
        OneDriveResetResult.ResetRequestedStarted => "started",
        OneDriveResetResult.ResetRequestedNotRunning => "failed",
        _ => "none",
    };

    public static string ResultWords(string slug) => slug switch
    {
        "reset-requested" => "reset requested",
        "reset-requested-started" => "reset requested, OneDrive started again",
        "reset-requested-not-running" => "reset requested, OneDrive not running",
        "not-installed" => "not installed",
        "could-not-reset" => "could not reset",
        _ => null,
    };

    /// <summary>The outcome in a few words, for Recent runs and the tool's row.</summary>
    public static string OutcomeWords(OneDriveResetResult result) => result switch
    {
        OneDriveResetResult.NotInstalled => "Not installed",
        OneDriveResetResult.CouldNotReset => "Could not reset",
        _ => "Reset requested",
    };

    public static string HistoryResult(OneDriveResetOutcome outcome, string runId)
        => Code(outcome.Result).ToString(CultureInfo.InvariantCulture) + "|" + (string.IsNullOrEmpty(runId) ? "none" : runId) + "|" + Slug(outcome.Result);

    /// <summary>The run log line: the switch, the restart and the result; never an account, a path or a file name.</summary>
    public static string LogLine(OneDriveResetOutcome outcome, string runId)
        => string.Format(CultureInfo.InvariantCulture, "ONEDRIVE run={0} time={1:yyyy-MM-ddTHH:mm:ss.fffZ} context=user-app switch={2} restart={3} result={4}",
            string.IsNullOrEmpty(runId) ? "none" : runId, outcome.EndedUtc, Argument, Restart(outcome.Result), Slug(outcome.Result));

    /// <summary>The status, its tone and the card: "Reset requested", never that files sync again.</summary>
    public static (string Status, Tone Tone, ResultCard Card) Present(OneDriveResetOutcome outcome)
    {
        const string Syncing = "OneDrive now syncs every account again from the start; files stay where they are. Whether a file that was stuck uploads now is not known yet.";
        const string Check = "Give it time, then look at the OneDrive icon. If files still do not sync, contact your IT support and give them Copy details for IT.";
        ResultCard Requested(string detail, string next, string support = "") => new()
        {
            Kind = CardKind.Info,
            Word = "REQUESTED",
            Title = "Reset requested: OneDrive",
            Detail = detail,
            NextStep = next,
            Support = support,
        };
        switch (outcome.Result)
        {
            case OneDriveResetResult.ResetRequested:
                return ("OneDrive reset requested. It syncs all accounts again from the start; this can take a while.", Tone.Neutral,
                    Requested("OneDrive was reset and started again. " + Syncing, Check));
            case OneDriveResetResult.ResetRequestedStarted:
                return ("OneDrive reset requested and OneDrive started again. It syncs all accounts again from the start.", Tone.Neutral,
                    Requested("OneDrive was reset; it did not start again by itself within a few minutes, so it was started. " + Syncing, Check));
            case OneDriveResetResult.ResetRequestedNotRunning:
                return ("OneDrive reset requested, but OneDrive is not running. Start OneDrive from the Start menu.", Tone.Warning,
                    ResultCard.From(FindingSeverity.Warning, "OneDrive is not running after the reset",
                        "The reset ran, but OneDrive did not start again and Windows did not start it either. Files stay where they are.",
                        "Type OneDrive in the Start menu search and open the OneDrive app. If it does not start, contact your IT support and give them Copy details for IT.",
                        "onedrive.exe /reset ran in the user's context; OneDrive did not restart and a plain start failed."));
            case OneDriveResetResult.NotInstalled:
                return (NotInstalledText + ".", Tone.Neutral,
                    ResultCard.From(FindingSeverity.Info, NotInstalledText, "The OneDrive sync app is not in any of the places Microsoft documents. Nothing was changed."));
            default:
                return ("OneDrive could not be reset: Windows did not start the reset. Nothing was changed.", Tone.Error,
                    ResultCard.From(FindingSeverity.Error, "OneDrive could not be reset", "Windows did not start the reset. Nothing was changed.",
                        "Restart the device and try again. If it repeats, contact your IT support and give them Copy details for IT.",
                        "OneDrive is installed, but starting onedrive.exe /reset failed in the user's context."));
        }
    }
}
