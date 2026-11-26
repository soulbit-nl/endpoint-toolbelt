using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// The engine's <c>defender-check</c> record: the verdict and the counts it rests on. It names no
/// file, process, rule, user or device; those are in the findings, for the user who asked.
/// </summary>
[DataContract]
public sealed class DefenderCheckRecord
{
    /// <summary>"none", "audits", "blocks", "detections", "empty" (the log holds no events), "not-active" (Microsoft Defender Antivirus is passive or off and nothing was found), "state-unknown" (nothing was found and Defender's status could not be read) or "unreadable".</summary>
    [DataMember(Name = "verdict")] public string Verdict { get; set; } = "";

    /// <summary>Microsoft Defender Antivirus: "active", "passive", "off" or "unknown"; empty from an older service.</summary>
    [DataMember(Name = "defender")] public string Defender { get; set; } = "";

    /// <summary>How many days back the check read.</summary>
    [DataMember(Name = "days")] public int? Days { get; set; }

    [DataMember(Name = "detections")] public int? Detections { get; set; }

    /// <summary>Blocks by attack surface reduction rules.</summary>
    [DataMember(Name = "blocks")] public int? Blocks { get; set; }

    /// <summary>Blocks by controlled folder access (protected folders and disk sectors); null from an older service.</summary>
    [DataMember(Name = "folderBlocks")] public int? FolderBlocks { get; set; }

    /// <summary>Blocks by network protection; null from an older service.</summary>
    [DataMember(Name = "networkBlocks")] public int? NetworkBlocks { get; set; }

    /// <summary>Audits of every protection: recorded, not blocked.</summary>
    [DataMember(Name = "audits")] public int? Audits { get; set; }

    /// <summary>Events of other users of the device: counted, never shown.</summary>
    [DataMember(Name = "others")] public int? Others { get; set; }

    /// <summary>Different rules, threats and processes among the events shown.</summary>
    [DataMember(Name = "groups")] public int? Groups { get; set; }

    /// <summary>The log held more events than the check reads.</summary>
    [DataMember(Name = "truncated")] public bool? Truncated { get; set; }

    // The health facts of an active Defender; all null from an older service, and when Defender is
    // passive, off or its log empty.

    /// <summary>When the last quick scan ended, UTC ("yyyy-MM-ddTHH:mm:ssZ"); empty when none is recorded.</summary>
    [DataMember(Name = "quickScan")] public string QuickScan { get; set; }

    /// <summary>When the last full scan ended, UTC; empty when none is recorded.</summary>
    [DataMember(Name = "fullScan")] public string FullScan { get; set; }

    /// <summary>Real-time protection now: "on", "off" or "unknown".</summary>
    [DataMember(Name = "rtp")] public string RealTimeProtection { get; set; }

    /// <summary>Real-time protection, the antivirus or the antispyware is off now.</summary>
    [DataMember(Name = "offNow")] public bool? OffNow { get; set; }

    /// <summary>Times protection was switched off in the period.</summary>
    [DataMember(Name = "protectionOff")] public int? ProtectionOff { get; set; }

    /// <summary>Real-time protection features that failed in the period.</summary>
    [DataMember(Name = "featureFailures")] public int? FeatureFailures { get; set; }

    /// <summary>Changes tamper protection blocked in the period.</summary>
    [DataMember(Name = "tamperBlocked")] public int? TamperBlocked { get; set; }

    /// <summary>Failed updates in the period.</summary>
    [DataMember(Name = "updateFailures")] public int? UpdateFailures { get; set; }

    /// <summary>A failed update that no later update is known to have fixed.</summary>
    [DataMember(Name = "updateWarning")] public bool? UpdateWarning { get; set; }

    /// <summary>How far the health events were read: "full", "short" or "none"; null from an older service.</summary>
    [DataMember(Name = "healthRead")] public string HealthRead { get; set; }

    /// <summary>The record has the health facts.</summary>
    public bool HasHealth => HealthRead != null;

    /// <summary>A scan time as UTC, or null.</summary>
    public static DateTime? Utc(string iso)
        => DateTime.TryParseExact(iso ?? "", "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc) ? utc : null;

    /// <summary>Only a known shape is kept: a verdict of lower-case letters, and no negative count.</summary>
    public static DefenderCheckRecord Clean(DefenderCheckRecord record)
    {
        if (record == null) { return null; }
        var verdict = record.Verdict ?? "";
        if (!IsWord(verdict)) { return null; }
        if (!IsWord(record.Defender ?? "")) { record.Defender = ""; }
        if (record.Days < 0) { record.Days = null; }
        if (record.Detections < 0) { record.Detections = null; }
        if (record.Blocks < 0) { record.Blocks = null; }
        if (record.FolderBlocks < 0) { record.FolderBlocks = null; }
        if (record.NetworkBlocks < 0) { record.NetworkBlocks = null; }
        if (record.Audits < 0) { record.Audits = null; }
        if (record.Others < 0) { record.Others = null; }
        if (record.Groups < 0) { record.Groups = null; }
        if (record.QuickScan != null && Utc(record.QuickScan) == null) { record.QuickScan = ""; }
        if (record.FullScan != null && Utc(record.FullScan) == null) { record.FullScan = ""; }
        if (record.RealTimeProtection != null && record.RealTimeProtection is not ("on" or "off" or "unknown")) { record.RealTimeProtection = "unknown"; }
        if (record.ProtectionOff < 0) { record.ProtectionOff = null; }
        if (record.FeatureFailures < 0) { record.FeatureFailures = null; }
        if (record.TamperBlocked < 0) { record.TamperBlocked = null; }
        if (record.UpdateFailures < 0) { record.UpdateFailures = null; }
        if (record.HealthRead != null && record.HealthRead is not ("full" or "short" or "none")) { record.HealthRead = "none"; }
        return record;
    }

    private static bool IsWord(string text) => text.Length > 0 && text.Length <= 40 && text.All(c => (c >= 'a' && c <= 'z') || c == '-');
}

/// <summary>
/// Check Defender blocks: the status of a finished check, its run log line, its history entry and
/// its Copy details for IT line, all from the <c>defender-check</c> record. The findings themselves
/// are the result cards, one per thing that was stopped.
/// </summary>
public static class DefenderCheck
{
    public const string Name = "Check Defender blocks";

    /// <summary>The history's action name (the service action's name, as every service run).</summary>
    public const string HistoryName = nameof(ToolbeltAction.CheckDefenderBlocks);

    /// <summary>The run's <c>defender-check</c> record, or null.</summary>
    public static DefenderCheckRecord Of(RunOutcome outcome) => outcome?.Records.LastOrDefault(r => r.Type == "defender-check" && r.DefenderCheck != null)?.DefenderCheck;

    private static string Number(int? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "unknown";

    /// <summary>The run log line: the verdict and the counts as tokens; never a file, a rule or a name.</summary>
    public static string LogLine(string runId, DefenderCheckRecord record)
        => "DEFENDERCHECK run=" + (string.IsNullOrEmpty(runId) ? "none" : runId) + " context=service verdict=" + record.Verdict
            + " days=" + Number(record.Days)
            + " detections=" + Number(record.Detections)
            + " rule-blocks=" + Number(record.Blocks)
            + " folder-blocks=" + Number(record.FolderBlocks)
            + " network-blocks=" + Number(record.NetworkBlocks)
            + " audits=" + Number(record.Audits)
            + " other-users=" + Number(record.Others)
            + " truncated=" + (record.Truncated == true ? "yes" : "no")
            + " defender=" + (string.IsNullOrEmpty(record.Defender) ? "unknown" : record.Defender)
            + (!record.HasHealth ? "" : " quick-scan=" + (DefenderCheckRecord.Utc(record.QuickScan) is DateTime q ? q.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : "none")
                + " rtp=" + (record.RealTimeProtection ?? "unknown") + " off-now=" + (record.OffNow == true ? "yes" : "no")
                + " protection-off=" + Number(record.ProtectionOff) + " feature-failures=" + Number(record.FeatureFailures)
                + " tamper-blocked=" + Number(record.TamperBlocked) + " update-failures=" + Number(record.UpdateFailures) + " health-read=" + record.HealthRead);

    private static string Period(DefenderCheckRecord record) => record.Days > 0 ? "the last " + Words.Count(record.Days.Value, "day") : "the log";

    /// <summary>The counts in words: "2 detections, 47 rule blocks and 1 controlled folder access block".</summary>
    private static string Counts(DefenderCheckRecord record)
    {
        var parts = new List<string>();
        if (record.Detections > 0) { parts.Add(Words.Count(record.Detections.Value, "detection")); }
        if (record.Blocks > 0) { parts.Add(Words.Count(record.Blocks.Value, "rule block")); }
        if (record.FolderBlocks > 0) { parts.Add(Words.Count(record.FolderBlocks.Value, "controlled folder access block")); }
        if (record.NetworkBlocks > 0) { parts.Add(Words.Count(record.NetworkBlocks.Value, "network protection block")); }
        if (record.Audits > 0) { parts.Add(Words.Count(record.Audits.Value, "audit")); }
        return parts.Count == 0 ? "nothing" : parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[parts.Count - 1];
    }

    /// <summary>What every "nothing found" adds: what the check does not read.</summary>
    public const string NotRead = "SmartScreen in Microsoft Edge and the firewall are not part of this check.";

    /// <summary>The status line and tone of a verdict, with the one next step. "Nothing found" is said only for what was read.</summary>
    public static (string Status, Tone Tone) Status(DefenderCheckRecord record) => Status(record, DateTime.UtcNow, TimeZoneInfo.Local);

    /// <summary>
    /// As above, with the health facts of an active Defender between the verdict and its next step:
    /// the last quick scan, and whether protection was switched off ("Last quick scan today 03:34.
    /// Protection was not switched off in the last 14 days."). A switch-off that lasts, a failed
    /// feature or an update that did not recover makes the tone a warning. With blocks or
    /// detections the sentence stays short, so it fits below For IT without a scroll: no counts,
    /// only those warnings join it, and the counts, the scan, switch-off and tamper protection facts
    /// are in the cards only.
    /// </summary>
    public static (string Status, Tone Tone) Status(DefenderCheckRecord record, DateTime nowUtc, TimeZoneInfo zone)
    {
        var (lead, tail, tone) = record.Verdict switch
        {
            "none" => ("No threat detection and no block by attack surface reduction rules, controlled folder access or network protection in " + Period(record) + ".", NotRead, Tone.Success),
            "audits" => ("No threat detection and no Defender block in " + Period(record) + "; " + Counts(record) + " recorded only.", NotRead, Tone.Success),
            "empty" => ("The Microsoft Defender log holds no events, so this check cannot say what Defender blocked.", "", Tone.Warning),
            "not-active" => ("Microsoft Defender Antivirus is not the active antivirus on this device: another security product is in charge, and its blocks are not in this log.", "", Tone.Info),
            "state-unknown" => ("No threat detection and no Defender block in " + Period(record) + ", but whether Microsoft Defender Antivirus is active could not be read.", NotRead, Tone.Info),
            // Short, so the status fits below For IT without a scroll: the counts and the next steps are in the cards.
            "blocks" or "detections" => ("Microsoft Defender blocked or detected something in " + Period(record) + ".", "See the details.", Tone.Warning),
            "unreadable" => ("The Microsoft Defender log could not be read, so this check cannot say what Defender blocked.", "", Tone.Warning),
            _ => ("The check finished; see the details.", "", Tone.Neutral),
        };
        var (health, warn) = record.Verdict is "empty" or "not-active" or "unreadable" ? ("", false) : Health(record, nowUtc, zone, brief: record.Verdict is "blocks" or "detections");
        if (warn && tone is Tone.Success or Tone.Info or Tone.Neutral) { tone = Tone.Warning; }
        // A health warning already says "see the details": the short result does not say it twice.
        if (record.Verdict is "blocks" or "detections" && health.Length > 0) { tail = ""; }
        return (string.Join(" ", new[] { lead, health, tail }.Where(p => p.Length > 0)), tone);
    }

    /// <summary>
    /// The health facts in a few short sentences, and whether one of them is a warning; empty from
    /// an older service. <paramref name="brief"/> (a result with blocks or detections) keeps only
    /// the warnings: protection off now, a failed feature, an update that did not recover.
    /// </summary>
    internal static (string Text, bool Warning) Health(DefenderCheckRecord record, DateTime nowUtc, TimeZoneInfo zone, bool brief = false)
    {
        if (!record.HasHealth) { return ("", false); }
        var parts = new List<string>();
        var warning = false;
        if (!brief)
        {
            if (DefenderCheckRecord.Utc(record.QuickScan) is DateTime quick) { parts.Add("Last quick scan " + ToolRuns.ShortWhen(quick, zone, nowUtc) + "."); }
            else if (record.QuickScan != null) { parts.Add("No quick scan is recorded."); }
        }
        if (record.OffNow == true)
        {
            parts.Add("Protection is switched off now.");
            warning = true;
        }
        else if (!brief && record.ProtectionOff > 0)
        {
            parts.Add("Protection was switched off " + (record.ProtectionOff == 1 ? "once" : Words.Count(record.ProtectionOff.Value, "time")) + " in " + Period(record)
                + (record.RealTimeProtection == "on" ? "; it is on now." : "."));
        }
        else if (!brief && record.HealthRead == "full" && !(record.FeatureFailures > 0))
        {
            parts.Add("Protection was not switched off in " + Period(record) + ".");
        }
        if (record.FeatureFailures > 0)
        {
            parts.Add("A real-time protection feature failed; see the details.");
            warning = true;
        }
        if (!brief && record.TamperBlocked > 0) { parts.Add("Tamper protection blocked " + Words.Count(record.TamperBlocked.Value, "change") + " to Defender's settings."); }
        if (record.UpdateWarning == true)
        {
            parts.Add("A Microsoft Defender update failed; see the details.");
            warning = true;
        }
        return (string.Join(" ", parts), warning);
    }

    /// <summary>
    /// The status of a finished check, from its record. An older service that does not know the
    /// action says so, as the other newer checks do.
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
        "none" => "no threat detection and no Defender block logged",
        "audits" => "no Defender block; audits recorded",
        "empty" => "the Defender log was empty",
        "not-active" => "Microsoft Defender Antivirus not active",
        "state-unknown" => "nothing logged; Defender's status not read",
        "blocks" => "Defender blocks logged",
        "detections" => "detections logged",
        "unreadable" => "the Defender log could not be read",
        _ => null,
    };

    /// <summary>
    /// The facts line Copy details for IT adds for a check: "Microsoft Defender · last 14 days ·
    /// detections 2 · rule blocks 47 · folder blocks 0 · network blocks 0 · audits 0 · other users'
    /// events 3". Null without a record.
    /// </summary>
    public static string TicketLine(RunOutcome outcome)
    {
        var record = Of(outcome);
        if (record == null) { return null; }
        if (record.Verdict == "unreadable") { return "Microsoft Defender" + RunTicket.Separator + "its operational log could not be read"; }
        var parts = new List<string>
        {
            "Microsoft Defender",
            record.Days > 0 ? "last " + Words.Count(record.Days.Value, "day") : "period unknown",
            "detections " + Number(record.Detections),
            "rule blocks " + Number(record.Blocks),
            "folder blocks " + Number(record.FolderBlocks),
            "network blocks " + Number(record.NetworkBlocks),
            "audits " + Number(record.Audits),
        };
        if (record.Others > 0) { parts.Add("other users' events " + Number(record.Others) + " (not shown)"); }
        if (record.Truncated == true) { parts.Add("more events in the log than were read"); }
        if (record.Verdict == "empty") { parts.Add("the log holds no events"); }
        if (record.Defender is "passive" or "off") { parts.Add("Defender Antivirus " + record.Defender); }
        if (record.Defender == "unknown") { parts.Add("Defender Antivirus status not read"); }
        if (record.HasHealth)
        {
            // Device facts: in UTC, as IT compares them with the portal.
            parts.Add(DefenderCheckRecord.Utc(record.QuickScan) is DateTime q ? "last quick scan " + q.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : "no quick scan recorded");
            parts.Add(DefenderCheckRecord.Utc(record.FullScan) is DateTime f ? "last full scan " + f.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : "no full scan recorded");
            parts.Add("real-time protection " + (record.RealTimeProtection ?? "unknown") + " now");
            if (record.OffNow == true) { parts.Add("protection off now"); }
            parts.Add("switched off " + Number(record.ProtectionOff));
            if (record.FeatureFailures > 0) { parts.Add("feature failures " + Number(record.FeatureFailures)); }
            if (record.TamperBlocked > 0) { parts.Add("tamper protection blocks " + Number(record.TamperBlocked)); }
            if (record.UpdateFailures > 0) { parts.Add("update failures " + Number(record.UpdateFailures) + (record.UpdateWarning == true ? " (not recovered)" : " (updated since)")); }
            if (record.HealthRead == "short") { parts.Add("the log reaches less than the period"); }
        }
        parts.Add("not read: SmartScreen in Edge, the firewall");
        return string.Join(RunTicket.Separator, parts);
    }
}
