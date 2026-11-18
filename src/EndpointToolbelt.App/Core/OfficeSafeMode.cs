using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace EndpointToolbelt.App.Core;

/// <summary>The Office desktop apps that can start in safe mode: Word and Excel, Outlook (classic) and PowerPoint.</summary>
public enum OfficeApp
{
    Word,
    Excel,

    /// <summary>Classic Outlook (outlook.exe), never the new Outlook (olk.exe).</summary>
    Outlook,
    PowerPoint,
}

/// <summary>The Office desktop apps on this PC as the safe-mode start needs them; the real one is <see cref="OfficeAppsOnThisPc"/>, tests use a fake.</summary>
public interface IOfficeApps
{
    /// <summary>The full path of the installed app's executable, or null when it is not installed.</summary>
    string Find(OfficeApp app);

    /// <summary>The app runs in this user's session.</summary>
    bool IsRunning(OfficeApp app);

    /// <summary>Starts the program with these arguments, as the user; false when Windows did not start it.</summary>
    bool Start(string path, string arguments);
}

/// <summary>
/// The Office desktop apps as Windows finds them, read-only: the App Paths entry Windows
/// itself uses to start "winword.exe", "excel.exe", "outlook.exe" or "powerpnt.exe" (the user's own
/// first, then the machine's in both views), then the Click-to-Run installation folder. An entry
/// counts only when it names an existing program file.
/// </summary>
public sealed class OfficeAppsOnThisPc : IOfficeApps
{
    private const string AppPaths = @"Software\Microsoft\Windows\CurrentVersion\App Paths\";

    public string Find(OfficeApp app)
    {
        var exe = OfficeSafeMode.ExeName(app);
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            if (OfficeSafeMode.CleanPath(ReadDefault(hive, view, AppPaths + exe)) is string found) { return found; }
        }
        // Click-to-Run Microsoft 365 Apps: <InstallationPath>\root\Office16\<exe>.
        var root = ReadValue(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration", "InstallationPath");
        return string.IsNullOrWhiteSpace(root) ? null : OfficeSafeMode.CleanPath(Path.Combine(root.Trim(), "root", "Office16", exe));
    }

    public bool IsRunning(OfficeApp app)
    {
        int session;
        using (var self = Process.GetCurrentProcess()) { session = self.SessionId; }
        var running = false;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(OfficeSafeMode.ExeName(app))))
        {
            using (p)
            {
                if (p.SessionId == session) { running = true; }
            }
        }
        return running;
    }

    public bool Start(string path, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
            return process != null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }

    private static string ReadDefault(RegistryHive hive, RegistryView view, string subKey) => ReadValue(hive, view, subKey, "");

    private static string ReadValue(RegistryHive hive, RegistryView view, string subKey, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>How a safe-mode start ended, kept apart honestly.</summary>
public enum SafeModeResult
{
    /// <summary>Windows accepted the start with /safe; nothing more is known unless the user answers the question (path only).</summary>
    StartRequested,
    WorksInSafeMode,
    AlsoFailsInSafeMode,
    NotConfirmed,
    AlreadyRunning,
    NotInstalled,
    CouldNotStart,
}

/// <summary>The user's answer to the app's question ("Could you open a document?", "Did Outlook stay open?").</summary>
public enum SafeModeAnswer
{
    Yes,
    No,
    NotTried,
}

/// <summary>One safe-mode start: the app and the result; no path, document or window title.</summary>
public sealed class SafeModeOutcome
{
    public OfficeApp App { get; set; }
    public SafeModeResult Result { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }

    /// <summary>The start came from the problem path "An Office app closes right after it opens", which asks the app's question; a start from For IT asks nothing.</summary>
    public bool Asks { get; set; }

    /// <summary>The start asks the user one question (path only) and has no answer yet.</summary>
    public bool AwaitsAnswer => Asks && Result == SafeModeResult.StartRequested;
}

/// <summary>
/// "An Office app closes right after it opens": starts Word, Excel, Outlook (classic) or
/// PowerPoint with Microsoft's documented <c>/safe</c> switch ("Open Office apps in safe mode on a
/// Windows PC") in the user's own context. From the problem path it then asks one question per app;
/// from For IT it asks nothing and the result is "Start requested". Safe mode starts the app without
/// add-ins and with default settings; it never changes the normal start, and the result never claims
/// that it is fixed. An app that is already running is not started a second time.
/// </summary>
public static class OfficeSafeMode
{
    public const string ProblemTitle = "An Office app closes right after it opens";
    public const string ProblemSubtitle = "Word, Excel, Outlook (classic), PowerPoint";
    public const string Argument = "/safe";

    /// <summary>The apps in the order the path and For IT list them.</summary>
    public static readonly OfficeApp[] Apps = { OfficeApp.Word, OfficeApp.Excel, OfficeApp.Outlook, OfficeApp.PowerPoint };

    /// <summary>The app as people know it: "Word", "Outlook (classic)".</summary>
    public static string AppName(OfficeApp app) => app switch
    {
        OfficeApp.Word => "Word",
        OfficeApp.Excel => "Excel",
        OfficeApp.Outlook => "Outlook (classic)",
        _ => "PowerPoint",
    };

    /// <summary>The app in the run log and the history: one word, lower case ("outlook").</summary>
    public static string LogName(OfficeApp app) => app.ToString().ToLowerInvariant();

    public static string ExeName(OfficeApp app) => app switch
    {
        OfficeApp.Word => "winword.exe",
        OfficeApp.Excel => "excel.exe",
        OfficeApp.Outlook => "outlook.exe",
        _ => "powerpnt.exe",
    };

    /// <summary>The For IT tool's name: "Start Word in safe mode".</summary>
    public static string ToolName(OfficeApp app) => "Start " + AppName(app) + " in safe mode";

    /// <summary>The For IT tool's button name ("WordSafeButton").</summary>
    public static string Button(OfficeApp app) => app + "SafeButton";

    /// <summary>The history's action name ("WordSafeMode").</summary>
    public static string HistoryName(OfficeApp app) => app + "SafeMode";

    public static OfficeApp? FromHistoryName(string action)
    {
        foreach (var app in Apps)
        {
            if (action == HistoryName(app)) { return app; }
        }
        return null;
    }

    /// <summary>The question the path asks after a start, per app.</summary>
    public static string Question(OfficeApp app) => app switch
    {
        OfficeApp.Word => "Could you open a document?",
        OfficeApp.Excel => "Could you open a workbook?",
        OfficeApp.Outlook => "Did Outlook stay open?",
        _ => "Could you open a presentation?",
    };

    /// <summary>What the user opens to try the app: "a document", "a workbook", "a presentation" (Outlook: none).</summary>
    private static string Thing(OfficeApp app) => app switch
    {
        OfficeApp.Word => "a document",
        OfficeApp.Excel => "a workbook",
        OfficeApp.PowerPoint => "a presentation",
        _ => "",
    };

    /// <summary>The answer buttons' automation names, per app.</summary>
    public static (string Yes, string No, string NotTried) AnswerNames(OfficeApp app) => app == OfficeApp.Outlook
        ? ("Yes, Outlook stayed open in safe mode", "No, Outlook also closed in safe mode", "Not tried: I did not check Outlook")
        : ("Yes, I could open " + Thing(app) + " in safe mode", "No, it also closes in safe mode", "Not tried: I did not open " + Thing(app));

    /// <summary>The text of the button while the app starts: "Starting Word…".</summary>
    public static string StartingText(OfficeApp app) => "Starting " + AppName(app) + "…";

    /// <summary>What a tool says when its app is missing: "Not available: Word is not installed on this PC".</summary>
    public static string NotInstalledText(OfficeApp app) => "Not available: " + AppName(app) + " is not installed on this PC";

    /// <summary>
    /// A program path from an App Paths entry or the Click-to-Run folder: quotes and spaces trimmed,
    /// variables expanded; only a full path to an existing .exe counts. Null otherwise.
    /// </summary>
    public static string CleanPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return null; }
        var path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"').Trim());
        try
        {
            if (!Path.IsPathRooted(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { return null; }
            return File.Exists(path) ? path : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts the app in safe mode. Not installed: nothing happens. Already running: the user is asked
    /// to close it normally first (<paramref name="askToClose"/>, never forced); when they do not, or
    /// it still runs, nothing is started. Otherwise the app is started with /safe.
    /// </summary>
    /// <param name="asks">The start comes from the problem path, which asks the app's question afterwards.</param>
    public static SafeModeOutcome Start(IOfficeApps apps, OfficeApp app, Func<bool> askToClose, Func<DateTime> utcNow = null, bool asks = false)
    {
        var now = utcNow ?? (() => DateTime.UtcNow);
        var outcome = new SafeModeOutcome { App = app, StartedUtc = now(), Asks = asks };
        SafeModeOutcome End(SafeModeResult result)
        {
            outcome.Result = result;
            outcome.EndedUtc = now();
            return outcome;
        }
        var path = apps.Find(app);
        if (path == null) { return End(SafeModeResult.NotInstalled); }
        if (apps.IsRunning(app))
        {
            // Ask, never close it for the user; a second instance would not start in safe mode.
            if (askToClose == null || !askToClose() || apps.IsRunning(app)) { return End(SafeModeResult.AlreadyRunning); }
        }
        return End(apps.Start(path, Argument) ? SafeModeResult.StartRequested : SafeModeResult.CouldNotStart);
    }

    /// <summary>The user's answer turns a requested start into its result; any other result stays.</summary>
    public static SafeModeOutcome Answer(SafeModeOutcome outcome, SafeModeAnswer answer, Func<DateTime> utcNow = null)
    {
        if (outcome == null || !outcome.AwaitsAnswer) { return outcome; }
        outcome.Result = answer switch
        {
            SafeModeAnswer.Yes => SafeModeResult.WorksInSafeMode,
            SafeModeAnswer.No => SafeModeResult.AlsoFailsInSafeMode,
            _ => SafeModeResult.NotConfirmed,
        };
        outcome.EndedUtc = (utcNow ?? (() => DateTime.UtcNow))();
        return outcome;
    }

    public static string Slug(SafeModeResult result) => result switch
    {
        SafeModeResult.StartRequested => "start-requested",
        SafeModeResult.WorksInSafeMode => "works-in-safe-mode",
        SafeModeResult.AlsoFailsInSafeMode => "also-fails-in-safe-mode",
        SafeModeResult.NotConfirmed => "not-confirmed",
        SafeModeResult.AlreadyRunning => "already-running",
        SafeModeResult.NotInstalled => "not-installed",
        _ => "could-not-start",
    };

    /// <summary>The history code: 0 works in safe mode, 1 also fails, 2 not confirmed, 3 start requested, 4 already running, 5 not installed, 6 could not start.</summary>
    public static int Code(SafeModeResult result) => result switch
    {
        SafeModeResult.WorksInSafeMode => 0,
        SafeModeResult.AlsoFailsInSafeMode => 1,
        SafeModeResult.NotConfirmed => 2,
        SafeModeResult.StartRequested => 3,
        SafeModeResult.AlreadyRunning => 4,
        SafeModeResult.NotInstalled => 5,
        _ => 6,
    };

    /// <summary>The history's result in words; "safe-start-requested" is how older versions wrote a requested start.</summary>
    public static string ResultWords(string slug) => slug switch
    {
        "start-requested" or "safe-start-requested" => "start requested",
        "works-in-safe-mode" => "works in safe mode",
        "also-fails-in-safe-mode" => "also fails in safe mode",
        "not-confirmed" => "not confirmed",
        "already-running" => "already open, nothing started",
        "not-installed" => "not installed",
        "could-not-start" => "could not start",
        _ => null,
    };

    /// <summary>The run's outcome in a few words, as Recent runs and the tool's row show it ("Start requested", "Could not start").</summary>
    public static string OutcomeWords(SafeModeResult result) => result switch
    {
        SafeModeResult.StartRequested => "Start requested",
        SafeModeResult.WorksInSafeMode => "Works in safe mode",
        SafeModeResult.AlsoFailsInSafeMode => "Also fails in safe mode",
        SafeModeResult.NotConfirmed => "Not confirmed",
        SafeModeResult.AlreadyRunning => "Already open",
        SafeModeResult.NotInstalled => "Not installed",
        _ => "Could not start",
    };

    /// <summary>"&lt;code&gt;|&lt;run ID&gt;|&lt;result&gt;", as the other checks write their history.</summary>
    public static string HistoryResult(SafeModeOutcome outcome, string runId)
        => Code(outcome.Result).ToString(CultureInfo.InvariantCulture) + "|" + (string.IsNullOrEmpty(runId) ? "none" : runId) + "|" + Slug(outcome.Result);

    /// <summary>The run log line: the app and the result category; never a path, a document or a window title.</summary>
    public static string LogLine(SafeModeOutcome outcome, string runId)
        => string.Format(CultureInfo.InvariantCulture, "OFFICE run={0} time={1:yyyy-MM-ddTHH:mm:ss.fffZ} context=user-app app={2} switch={3} result={4}",
            string.IsNullOrEmpty(runId) ? "none" : runId, outcome.EndedUtc, LogName(outcome.App), Argument, Slug(outcome.Result));

    /// <summary>
    /// The status, its tone and the card: honest about what was observed; the normal start
    /// is never said to be fixed. A requested start has no result word (no badge) and its card says
    /// "REQUESTED"; only a start from the path asks the app's question.
    /// </summary>
    public static (string Status, Tone Tone, ResultCard Card) Present(SafeModeOutcome outcome)
    {
        var app = AppName(outcome.App);
        var outlook = outcome.App == OfficeApp.Outlook;
        var thing = Thing(outcome.App);
        const string WhatSafeModeIs = "Safe mode starts it without add-ins and with default settings, for this start only; the normal start is not changed.";
        switch (outcome.Result)
        {
            case SafeModeResult.StartRequested:
                var card = new ResultCard
                {
                    Kind = CardKind.Info,
                    Word = "REQUESTED",
                    Title = "Start requested: " + app + " in safe mode",
                    Detail = "Windows accepted the start. " + WhatSafeModeIs,
                    NextStep = outcome.Asks
                        ? (outlook ? "See whether Outlook stays open, then answer the question below the result." : "Open " + thing + " in " + app + ", then answer the question below the result.")
                        : "",
                    Support = "",
                };
                return (app + " was started in safe mode." + (outcome.Asks ? " " + Question(outcome.App) : ""), Tone.Neutral, card);
            case SafeModeResult.WorksInSafeMode:
                return (app + " works in safe mode. An add-in or a setting may be the cause; that is not proven.", Tone.Warning,
                    ResultCard.From(FindingSeverity.Warning, app + " works in safe mode",
                        (outlook ? "Outlook stayed open in safe mode" : "You could open " + thing + " in safe mode") + ", where " + app + " starts without add-ins and with default settings. An add-in or a setting may be why the normal start closes; that is not proven, and the normal start is not fixed.",
                        "Close " + app + " and start it normally. If it closes again, contact your IT support and give them Copy details for IT.",
                        app + (outlook ? " stayed open" : " opened " + thing) + " with /safe but closes at a normal start: an add-in (COM or web) or the user's " + app + " settings may be the cause, not proven. Check the add-ins one by one."));
            case SafeModeResult.AlsoFailsInSafeMode:
                return (app + " also fails in safe mode. Contact your IT support.", Tone.Error,
                    ResultCard.From(FindingSeverity.Error, app + " also fails in safe mode",
                        (outlook ? "Outlook also closed" : "It could not open " + thing) + " even without add-ins and with default settings, so they are probably not the cause.",
                        "Contact your IT support and give them Copy details for IT.",
                        app + " also fails with /safe: add-ins and user settings are unlikely causes. Consider an Office repair (Online Repair) or a reinstall."));
            case SafeModeResult.NotConfirmed:
                return ("Not confirmed whether " + app + " works in safe mode.", Tone.Neutral,
                    ResultCard.From(FindingSeverity.Info, "Not confirmed whether " + app + " works in safe mode",
                        outlook ? "Nobody checked whether Outlook stayed open in safe mode, so nothing is known yet." : "No " + thing.Substring(2) + " was opened in safe mode, so nothing is known yet.",
                        "If " + app + " keeps closing, start it in safe mode again and try it, or contact your IT support."));
            case SafeModeResult.AlreadyRunning:
                return (app + " is already open, so nothing was started. Close it normally first, then try again.", Tone.Warning,
                    ResultCard.From(FindingSeverity.Warning, app + " is already running",
                        "A second " + app + " would not start in safe mode while " + app + " runs, and " + ProductInfo.DisplayName + " never closes it for you. Nothing was started.",
                        "Save your work and close " + app + " normally, then start it in safe mode again. If " + app + " does not respond, contact your IT support."));
            case SafeModeResult.NotInstalled:
                return (NotInstalledText(outcome.App) + ".", Tone.Neutral,
                    ResultCard.From(FindingSeverity.Info, NotInstalledText(outcome.App), "Windows lists no " + app + " desktop app on this PC. Nothing was started."));
            default:
                return (app + " could not be started in safe mode. Restart the device and try again; if it repeats, contact your IT support.", Tone.Error,
                    ResultCard.From(FindingSeverity.Error, app + " could not be started in safe mode", "Windows did not start it. Nothing was changed.",
                        "Restart the device and try again. If it repeats, contact your IT support.",
                        app + " is registered on this PC, but starting it with /safe failed in the user's context."));
        }
    }
}
