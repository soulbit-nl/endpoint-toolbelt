using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EndpointToolbelt.App.Core;

/// <summary>Counts in words: "1 check", "3 checks", never "check(s)".</summary>
public static class Words
{
    /// <summary>The number and the noun in its proper form; the plural adds "s" unless given.</summary>
    public static string Count(int count, string singular, string plural = null)
        => count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural ?? singular + "s");

    /// <summary>
    /// The one word for a warning, everywhere: Home's severity line and a row's badge
    /// (in capitals, <see cref="WarningBadge"/>), Recent runs, and every count ("1 warning"). A noun, never
    /// a verb. Change it here only; change <see cref="WarningPlural"/> too when the word has no regular plural.
    /// </summary>
    public const string Warning = "Warning";

    /// <summary>The plural of <see cref="Warning"/>, for the counts.</summary>
    public const string WarningPlural = Warning + "s";

    /// <summary>The word in capitals, as a badge, Home's severity line and a result card write it.</summary>
    public static string WarningBadge => Warning.ToUpperInvariant();

    /// <summary>A count of warnings in lower case: "1 warning", "2 warnings".</summary>
    public static string WarningCount(int count) => Count(count, Warning.ToLowerInvariant(), WarningPlural.ToLowerInvariant());

    /// <summary>The badge word older run logs carry for a warning; still read as one.</summary>
    public const string EarlierWarningBadge = "CHECK";

    /// <summary>The word with its article, as a sentence part: "a warning".</summary>
    public static string AWarning => ("AEIOU".IndexOf(Warning[0]) >= 0 ? "an " : "a ") + Warning.ToLowerInvariant();

    /// <summary>
    /// The result of a run or check in one word, as the health rows write theirs: OK, WARNING,
    /// PROBLEM or INFO (a sync Windows queued); none while it runs or for a neutral result (cancelled, nothing to do).
    /// </summary>
    public static string ResultWord(Tone tone) => tone switch
    {
        Tone.Success => "OK",
        Tone.Warning => WarningBadge,
        Tone.Error => "PROBLEM",
        Tone.Info => "INFO",
        _ => "",
    };

    /// <summary>Names as a sentence part: "Word", "Word and Outlook", "Word, Excel and Outlook".</summary>
    public static string List(IReadOnlyList<string> names)
    {
        if (names == null || names.Count == 0) { return ""; }
        if (names.Count == 1) { return names[0]; }
        return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
    }

    /// <summary>
    /// The one name of the retry action, wherever it shows: Home, a health row, the app page's
    /// button, the confirmations, the history and Copy details for IT.
    /// </summary>
    public const string RetryStep = "Retry work apps and settings";

    /// <summary>The name of the button a health row's fix presses, as a next step for IT support names it.</summary>
    public static string FixLink(HealthFix fix) => fix switch
    {
        HealthFix.RetryAppsAndPolicies => RetryStep,
        HealthFix.ConnectivityTest => "Check Microsoft connections",
        HealthFix.SyncClock => "Sync clock",
        HealthFix.UpdateDefender => "Update Defender definitions",
        HealthFix.FreeDiskSpace => "Free up disk space",
        HealthFix.RefreshSignIn => "Refresh work account sign-in",
        _ => "",
    };

    /// <summary>The link under a health row and Home's step for a health finding: the name of the button it presses.</summary>
    public static string FixText(HealthFix fix) => FixLink(fix);

    /// <summary>The help text of a button that says <see cref="RetryStep"/>: what it does.</summary>
    public const string RetryStepHelp = "Intune is asked to check this device again, and the app installer tries every failed work app again. Nothing is removed.";
}

/// <summary>
/// How a run ended, in words: the closing line of the technical log and the readable history in
/// the device report use the same words.
/// </summary>
public static class RunWords
{
    /// <summary>An engine exit code in words; null for a code without a meaning of its own.</summary>
    public static string Exit(int exit) => exit switch
    {
        EngineExitCode.Success => "completed",
        EngineExitCode.Failed => "failed",
        EngineExitCode.Warnings => "completed with warnings",
        EngineExitCode.InvalidAction => "not run: unknown action",
        EngineExitCode.CooldownActive => "skipped: cooldown active",
        EngineExitCode.RestartPending => "blocked: restart pending",
        EngineExitCode.NothingToRepair => "nothing to repair",
        _ => null,
    };

    /// <summary>A service result other than Completed, in words.</summary>
    public static string Service(ResultCode result) => result switch
    {
        ResultCode.Completed => "completed",
        ResultCode.EngineTimeout => "stopped at the time limit",
        ResultCode.EngineCrashed => "stopped unexpectedly",
        ResultCode.EngineDidNotStart => "did not start",
        ResultCode.EngineNotSigned => "not started: a device policy blocked the engine",
        ResultCode.Interrupted => "interrupted",
        ResultCode.ServiceUnavailable => "not started: the service is not available",
        ResultCode.ProtocolMismatch => "not started: the app and the service are different versions",
        ResultCode.ServerIdentityMismatch => "not started: the connection did not come from the service",
        ResultCode.Busy => "not started: another action was running",
        ResultCode.ConfirmationRequired => "not started: confirmation needed",
        ResultCode.RateLimited => "not started: started too often",
        ResultCode.MultiSessionBlocked => "not started: other users are signed in",
        ResultCode.ToolSwitchedOff => "not started: switched off by your organization",
        _ => "not started (" + result + ")",
    };

    /// <summary>An exit code for display: small codes as numbers, HRESULTs as 0x........</summary>
    public static string Code(int exit)
        => exit >= 0 && exit < 100000 ? exit.ToString(CultureInfo.InvariantCulture) : ResultPresenter.FormatCode(exit);

    /// <summary>
    /// The closing line of the technical log: "RESULT: completed with warnings (exit 2), 1 change,
    /// restart: no".
    /// </summary>
    public static string ResultLine(RunOutcome outcome, int changes, bool restart)
    {
        string words;
        string codes;
        if (outcome.Result == ResultCode.Completed && outcome.ExitCode.HasValue)
        {
            var exit = outcome.ExitCode.Value;
            words = Exit(exit) ?? "finished with code " + Code(exit);
            codes = "exit " + Code(exit);
        }
        else
        {
            words = Service(outcome.Result);
            codes = (outcome.ExitCode.HasValue ? "exit " + Code(outcome.ExitCode.Value) : "no exit code") + ", service result " + outcome.Result;
        }
        return string.Format(CultureInfo.InvariantCulture, "RESULT: {0} ({1}), {2}, restart: {3}",
            words, codes, changes == 0 ? "no changes" : Words.Count(changes, "change"), restart ? "yes" : "no");
    }

    /// <summary>
    /// A history line ("date offset;user;action;result") as the device report shows it:
    /// "2026-10-15 14:02 +02:00  Repair Windows Update: nothing to repair (69003)". The user is
    /// left out (the report names the user once). A line or value this app does not know is
    /// shown as it is.
    /// </summary>
    public static string HistoryLine(string line)
    {
        if (string.IsNullOrEmpty(line)) { return line ?? ""; }
        var parts = line.Split(';');
        if (parts.Length != 4) { return line; }
        if (!DateTimeOffset.TryParseExact(parts[0], "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) { return line; }
        var action = parts[2];
        return when.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture) + "  " + HistoryAction(action) + ": " + HistoryResult(action, parts[3]);
    }

    private static string HistoryAction(string action)
    {
        if (action == ConnectivityTest.HistoryName) { return ConnectivityTest.Name; }
        if (action == WindowRescue.HistoryName) { return WindowRescue.Name; }
        if (action == CompliancePath.HistoryName) { return CompliancePath.CheckName; }
        if (OfficeSafeMode.FromHistoryName(action) is OfficeApp office) { return OfficeSafeMode.ToolName(office); }
        if (action == OneDriveReset.HistoryName) { return OneDriveReset.ToolName; }
        if (action == "ResetCompanyPortal") { return "Reset Company Portal data"; }
        if (action == "RefreshSignIn") { return "Refresh work account sign-in"; }
        if (action == "ResetSignInCache") { return "Reset sign-in cache"; }
        if (action == "ClearTeamsCache") { return "Clear Teams cache"; }
        return Enum.TryParse<ToolbeltAction>(action, false, out var known) && Enum.IsDefined(typeof(ToolbeltAction), known) && !int.TryParse(action, out _)
            ? ActionText.Name(known)
            : action;
    }

    private static string HistoryResult(string action, string result)
    {
        var isNumber = int.TryParse(result, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code);
        if (action == ConnectivityTest.HistoryName && isNumber && code >= 0)
        {
            // An older line: the number alone.
            return code == 0 ? "all required endpoints reachable (0)" : Words.Count(code, "required endpoint") + " unreachable (" + result + ")";
        }
        // "<number>|<run ID>|<outcome>", so two runs with the same number are told apart.
        var fields = (result ?? "").Split('|');
        if (fields.Length == 3 && int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first))
        {
            var words = action == ConnectivityTest.HistoryName ? ConnectivityTest.VerdictWords(fields[2])
                : action == WindowRescue.HistoryName ? WindowRescue.ResultWords(fields[2])
                : action == IpRenewal.HistoryName ? IpRenewal.ResultWords(fields[2])
                : action == WuCheck.HistoryName ? WuCheck.ResultWords(fields[2])
                : action == DefenderCheck.HistoryName ? DefenderCheck.ResultWords(fields[2])
                : action == CompliancePath.HistoryName ? CompliancePath.ResultWords(fields[2])
                : OfficeSafeMode.FromHistoryName(action).HasValue ? OfficeSafeMode.ResultWords(fields[2])
                : action == OneDriveReset.HistoryName ? OneDriveReset.ResultWords(fields[2])
                : null;
            // An exit 2 whose findings were only OK or INFO: completed, not "with warnings".
            if (words == null && first == EngineExitCode.Warnings && fields[2] == ResultPresenter.HistoryInfo) { words = "completed (INFO)"; }
            if (words != null) { return words + " (" + first.ToString(CultureInfo.InvariantCulture) + ", reference " + fields[1] + ")"; }
        }
        // The tools that run in the app (ToolOutcome codes).
        if (action is "RefreshSignIn" or "ResetSignInCache" or "ClearTeamsCache" && isNumber && code is >= 0 and <= 5)
        {
            return code switch
            {
                0 => "done (0)",
                1 => "failed (1)",
                2 => "not confirmed (2)",
                3 => "nothing to do (3)",
                4 => "not supported here (4)",
                _ => "apps still open (5)",
            };
        }
        if (action == "ResetCompanyPortal" && isNumber && code is >= 0 and <= 7 and not 2)
        {
            return code switch
            {
                0 => "reset done (0)",
                1 => "not reset (1)",
                3 => "not installed (3)",
                4 => "not supported on this Windows (4)",
                5 => "blocked by a device policy (5)",
                // Company Portal is never closed by force.
                6 => "cancelled, Company Portal kept open (6)",
                _ => "Company Portal did not close, nothing changed (7)",
            };
        }
        if (isNumber)
        {
            var words = Exit(code);
            // A code without a meaning of its own as the system writes it: a crash code in hex.
            return words == null ? Code(code) : words + " (" + result + ")";
        }
        return result switch
        {
            // A Full IME reset that did not start, because Company Portal stayed open.
            Confirmations.FullResetPortalCancelled => "not started: Company Portal kept open, nothing changed",
            Confirmations.FullResetPortalStillOpen => "not started: Company Portal did not close, nothing changed",
            "timeout" => "timed out waiting for the result",
            "interrupted" => "interrupted",
            "not-started" => "did not start",
            _ => Enum.TryParse<ResultCode>(result, false, out var service) && Enum.IsDefined(typeof(ResultCode), service)
                ? Service(service) + " (" + result + ")"
                : result,
        };
    }
}
