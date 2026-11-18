using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// The engine's <c>wu-check</c> record: the verdict and the facts it rests on, as short tokens
/// and numbers. It names no update server, path, user or device.
/// </summary>
[DataContract]
public sealed class WuCheckRecord
{
    [DataMember(Name = "verdict")] public string Verdict { get; set; } = "";

    /// <summary>The patch month of the last successful quality update (yyyy-MM), or empty.</summary>
    [DataMember(Name = "lastmonth")] public string LastMonth { get; set; } = "";

    /// <summary>Days since the last successful quality update.</summary>
    [DataMember(Name = "days")] public int? Days { get; set; }

    [DataMember(Name = "behind")] public int? Behind { get; set; }

    /// <summary>Up to four failure codes, 0x plus eight hex digits, separated by spaces.</summary>
    [DataMember(Name = "codes")] public string Codes { get; set; } = "";

    /// <summary>Failed quality update installs in the update history since the last successful one.</summary>
    [DataMember(Name = "failed")] public int? Failed { get; set; }

    /// <summary>Comma-separated policy tokens ("wsus", "paused-policy", "deferred-7"), or "none".</summary>
    [DataMember(Name = "policy")] public string Policy { get; set; } = "";

    [DataMember(Name = "disabled")] public int? Disabled { get; set; }
    [DataMember(Name = "restart")] public bool? Restart { get; set; }
    [DataMember(Name = "freegb")] public double? FreeGB { get; set; }
    [DataMember(Name = "lowspace")] public bool? LowSpace { get; set; }

    /// <summary>The engine suggests Repair Windows Update: it can help here.</summary>
    [DataMember(Name = "repair")] public bool? Repair { get; set; }

    /// <summary>Only known shapes are kept: tokens of lower-case letters, digits, dashes (and commas for the policy), codes as 0x plus eight hex digits.</summary>
    public static WuCheckRecord Clean(WuCheckRecord record)
    {
        if (record == null) { return null; }
        record.Verdict = Token(record.Verdict, comma: false);
        if (record.Verdict.Length == 0) { return null; }
        record.LastMonth = record.LastMonth != null && record.LastMonth.Length == 7 && record.LastMonth[4] == '-' && record.LastMonth.Where((c, i) => i != 4).All(char.IsDigit) ? record.LastMonth : "";
        record.Codes = string.Join(" ", (record.Codes ?? "").Split(' ').Where(IsCode).Take(4));
        record.Policy = Token(record.Policy, comma: true);
        if (record.Days < 0) { record.Days = null; }
        if (record.Behind < 0) { record.Behind = null; }
        if (record.Failed < 0) { record.Failed = null; }
        if (record.Disabled < 0) { record.Disabled = null; }
        if (record.FreeGB < 0 || double.IsNaN(record.FreeGB ?? 0)) { record.FreeGB = null; }
        return record;
    }

    private static bool IsCode(string text)
        => text.Length == 10 && text.StartsWith("0x", StringComparison.Ordinal) && text.Skip(2).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    private static string Token(string text, bool comma)
        => !string.IsNullOrEmpty(text) && text.Length <= 120 && text.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || (comma && c == ',')) ? text : "";
}

/// <summary>What a finished check says on its path: clean, or the verdict line that replaces "no problem".</summary>
public sealed class WuPathCheck
{
    public WuPathCheck(bool clean, string text, Tone tone)
    {
        Clean = clean;
        Text = text;
        Tone = tone;
    }

    /// <summary>The check found nothing to do.</summary>
    public bool Clean { get; }

    /// <summary>"Check Windows Update: ... Next: ...", empty when clean.</summary>
    public string Text { get; }

    public Tone Tone { get; }
}

/// <summary>
/// Check Windows Update: the status of a finished check, its run log line, its history entry
/// and its Copy details for IT line, all from the <c>wu-check</c> record. The findings themselves are the
/// result cards, each with its one next step.
/// </summary>
public static class WuCheck
{
    public const string Name = "Check Windows Update";

    /// <summary>The history's action name (the service action's name, as every service run).</summary>
    public const string HistoryName = nameof(ToolbeltAction.CheckWindowsUpdate);

    /// <summary>The run's <c>wu-check</c> record, or null.</summary>
    public static WuCheckRecord Of(RunOutcome outcome) => outcome?.Records.LastOrDefault(r => r.Type == "wu-check" && r.WuCheck != null)?.WuCheck;

    private static string Number(int? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "unknown";

    /// <summary>The run log line: the verdict and its facts as tokens; never a server, path or name.</summary>
    public static string LogLine(string runId, WuCheckRecord record)
        => "WUCHECK run=" + (string.IsNullOrEmpty(runId) ? "none" : runId) + " context=service verdict=" + record.Verdict
            + " last-quality=" + (record.LastMonth.Length == 0 ? "none" : record.LastMonth)
            + " days-ago=" + Number(record.Days)
            + " months-behind=" + Number(record.Behind)
            + " codes=" + (record.Codes.Length == 0 ? "none" : record.Codes.Replace(' ', ','))
            + " failed-installs=" + Number(record.Failed)
            + " policy=" + (record.Policy.Length == 0 ? "none" : record.Policy)
            + " services-disabled=" + Number(record.Disabled)
            + " restart-pending=" + (record.Restart == true ? "yes" : "no")
            + " free-gb=" + (record.FreeGB.HasValue ? Math.Floor(record.FreeGB.Value).ToString("0", CultureInfo.InvariantCulture) : "unknown")
            + " repair=" + (record.Repair == true ? "suggested" : "not-suggested");

    /// <summary>The one next step of a verdict ("Next: Repair Windows Update."); empty when there is nothing to do.</summary>
    public static string Step(WuCheckRecord record) => record.Verdict switch
    {
        "behind" or "failing" or "no-history" => record.Repair == true ? "Next: Repair Windows Update." : "Next: copy details for IT.",
        "blocked-by-policy" => "Next: copy details for IT.",
        "services-disabled" => "Contact your IT support.",
        "restart-pending" => "Restart, then check again.",
        "low-space" => "Try Free up disk space.",
        _ => "",
    };

    /// <summary>The status line and tone of a verdict, with the one next step.</summary>
    public static (string Status, Tone Tone) Status(WuCheckRecord record)
    {
        var step = " " + Step(record);
        return record.Verdict switch
        {
            "up-to-date" => ("Windows is up to date. Nothing to do.", Tone.Success),
            "behind" => ((record.Repair == true ? "Windows is behind on updates and no failure was logged." : "Windows is behind on updates.") + step, Tone.Warning),
            "failing" => ("Updates failed on this device." + step, Tone.Warning),
            "no-history" => ("No successful quality update was found." + step, Tone.Warning),
            "blocked-by-policy" => ("A policy on this device decides about its updates; a repair here cannot change that." + step, Tone.Warning),
            "services-disabled" => ("A service Windows Update needs is disabled." + step, Tone.Error),
            "restart-pending" => ("Windows is waiting for a restart." + step, Tone.Warning),
            "low-space" => ("Too little free disk space for updates." + step, Tone.Warning),
            _ => ("The check finished; see the details.", Tone.Neutral),
        };
    }

    /// <summary>
    /// What a finished check adds to the verdict of "Windows Update keeps failing": null without a
    /// check (or for an older service, or a run that did not finish), so the health rows decide alone.
    /// </summary>
    public static WuPathCheck ForPath(RunOutcome outcome)
    {
        var record = Of(outcome);
        if (record == null || outcome.Result != ResultCode.Completed || outcome.InProgress) { return null; }
        if (record.Verdict == "up-to-date")
        {
            // A current device can still have a point to check (a pause in Settings, automatic updates off).
            return ResultPresenter.TopFinding(outcome.Records) is EngineRecord top
                ? new WuPathCheck(false, Name + ": Windows is up to date, but one point needs checking: " + top.Title + ".", Tone.Warning)
                : new WuPathCheck(true, "", Tone.Success);
        }
        var (_, tone) = Status(record);
        var words = record.Verdict switch
        {
            "behind" => "Windows is behind on updates",
            "failing" => "updates failed on this device",
            "no-history" => "no successful quality update was found",
            "blocked-by-policy" => "a policy decides about updates on this device",
            "services-disabled" => "a service Windows Update needs is disabled",
            "restart-pending" => "Windows is waiting for a restart",
            "low-space" => "too little free disk space for updates",
            _ => "it found " + Words.AWarning + "; see its details",
        };
        var step = Step(record);
        return new WuPathCheck(false, Name + ": " + words + "." + (step.Length > 0 ? " " + step : ""), tone == Tone.Neutral ? Tone.Warning : tone);
    }

    /// <summary>
    /// The status of a finished check, from its record. An older service that does not know the
    /// action says so, as Get a new IP address does.
    /// </summary>
    public static void Apply(ResultView view, RunOutcome outcome)
    {
        if (outcome.Result == ResultCode.InvalidRequest && (outcome.Message ?? "").StartsWith("Unknown action", StringComparison.Ordinal))
        {
            view.Tone = Tone.Warning;
            view.Status = Name + " is not available on this device yet: its " + ProductInfo.DisplayName + " service is an older version.";
            view.Cards.Clear();
            view.Cards.Add(ResultCard.From(FindingSeverity.Warning, "Not available on this device yet", "The " + ProductInfo.DisplayName + " service on this device does not know this check yet. Nothing was changed.",
                "Ask your IT support to update " + ProductInfo.DisplayName + ".", "The installed service is older than the app; deploy the current version."));
            return;
        }
        var record = Of(outcome);
        if (outcome.Result != ResultCode.Completed || record == null) { return; }
        (view.Status, view.Tone) = Status(record);
        // A current device can still have a point to check (a pause in Settings, automatic updates off).
        if (record.Verdict == "up-to-date" && ResultPresenter.TopFinding(outcome.Records) is EngineRecord top)
        {
            (view.Status, view.Tone) = ("Windows is up to date, but one point needs checking: " + top.Title + ".", Tone.Warning);
        }
    }

    /// <summary>"&lt;exit&gt;|&lt;run ID&gt;|&lt;verdict&gt;" (as the other checks write their history), or null when the run has no record.</summary>
    public static string HistoryResult(RunOutcome outcome)
    {
        var record = Of(outcome);
        if (record == null || outcome.Result != ResultCode.Completed || !outcome.ExitCode.HasValue || outcome.ExitCode.Value < 0) { return null; }
        return outcome.ExitCode.Value.ToString(CultureInfo.InvariantCulture) + "|" + (string.IsNullOrEmpty(outcome.RunId) ? "none" : outcome.RunId) + "|" + record.Verdict;
    }

    /// <summary>A verdict from the history in words; null for a word this version does not know.</summary>
    public static string ResultWords(string slug) => slug switch
    {
        "up-to-date" => "up to date",
        "behind" => "behind on updates, no failure logged",
        "failing" => "updates failed",
        "no-history" => "no successful quality update found",
        "blocked-by-policy" => "a policy decides about the updates",
        "services-disabled" => "an update service is disabled",
        "restart-pending" => "waiting for a restart",
        "low-space" => "too little free disk space",
        _ => null,
    };

    /// <summary>
    /// The facts line Copy details for IT adds for a check: "Windows Update · last quality update
    /// 2026-10 (12 days ago) · failures 0x800f0922 · policy wsus · services OK · restart pending no ·
    /// free 40 GB · Repair Windows Update suggested". Null without a record.
    /// </summary>
    public static string TicketLine(RunOutcome outcome)
    {
        var record = Of(outcome);
        if (record == null) { return null; }
        var parts = new List<string>
        {
            "Windows Update",
            "last quality update " + (record.LastMonth.Length == 0 ? "none found" : record.LastMonth + (record.Days.HasValue ? " (" + Words.Count(record.Days.Value, "day") + " ago)" : "")),
            "failures " + (record.Codes.Length == 0 ? "none" : record.Codes.Replace(" ", ", ")) + (record.Failed > 0 ? " (" + Words.Count(record.Failed.Value, "failed install") + ")" : ""),
            "policy " + (record.Policy.Length == 0 ? "none" : record.Policy.Replace(",", ", ")),
            "services " + (record.Disabled > 0 ? record.Disabled.Value.ToString(CultureInfo.InvariantCulture) + " disabled" : record.Disabled == 0 ? "OK" : "unknown"),
            "restart pending " + (record.Restart == true ? "yes" : "no"),
            "free " + (record.FreeGB.HasValue ? Math.Floor(record.FreeGB.Value).ToString("0", CultureInfo.InvariantCulture) + " GB" : "unknown"),
            "Repair Windows Update " + (record.Repair == true ? "suggested" : "not suggested"),
        };
        return string.Join(RunTicket.Separator, parts);
    }
}
