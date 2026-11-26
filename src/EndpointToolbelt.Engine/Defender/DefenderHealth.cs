using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.Defender;

/// <summary>
/// The few health facts Check Defender blocks adds to its result: the last quick and full scan,
/// whether protection was or is switched off, a change tamper protection blocked, a failed feature
/// and a failed update. Device facts only: no path, no account and no value of a setting is read
/// into the result.
/// </summary>
public sealed class DefenderHealthFacts
{
    public DateTime? QuickScanUtc { get; set; }
    public DateTime? FullScanUtc { get; set; }

    /// <summary>Real-time protection now: "on", "off" or "unknown".</summary>
    public string RealTimeProtection { get; set; } = "unknown";

    /// <summary>Real-time protection, the antivirus or the antispyware is switched off now.</summary>
    public bool OffNow { get; set; }

    /// <summary>Events that real-time protection, the antivirus or the antispyware was switched off (5001, 5012, 5010).</summary>
    public int ProtectionOff { get; set; }

    /// <summary>Real-time protection features that failed (3002).</summary>
    public int FeatureFailures { get; set; }

    /// <summary>Changes tamper protection blocked (5013).</summary>
    public int TamperBlocked { get; set; }

    /// <summary>Failed updates (2001, 2003, 2004).</summary>
    public int UpdateFailures { get; set; }

    /// <summary>A failed update that no later update is known to have fixed.</summary>
    public bool UpdateWarning { get; set; }

    /// <summary>How far the health events were read: "full" (the whole period), "short" (the log or the read reaches less far) or "none" (not read).</summary>
    public string Read { get; set; } = "none";

    /// <summary>A fact that makes the run end with warnings.</summary>
    public bool Warning { get; set; }
}

public sealed partial class DefenderBlocksCheck
{
    /// <summary>Real-time protection was switched on (5000): only to say it is on again after a 5001.</summary>
    public const int RtpEnabled = 5000;
    public const int RtpDisabled = 5001;
    public const int AntispywareDisabled = 5010;
    public const int AntivirusDisabled = 5012;

    /// <summary>Tamper protection blocked a change to a protected setting.</summary>
    public const int TamperBlocked = 5013;

    /// <summary>A real-time protection feature failed.</summary>
    public const int RtpFeatureFailure = 3002;
    public const int SignatureUpdateFailed = 2001;
    public const int EngineUpdateFailed = 2003;

    /// <summary>Defender could not load its security intelligence and went back to an earlier version.</summary>
    public const int SignatureReversion = 2004;

    /// <summary>
    /// The health events, read apart from the blocks so they never use up the blocks' event cap.
    /// Never 5007 (its values can be exclusion paths) and never the scan events 1000 to 1005 (other
    /// software asks for hundreds of custom scans a day).
    /// </summary>
    public static readonly IReadOnlyList<int> HealthEventIds = new[] { RtpEnabled, RtpDisabled, AntispywareDisabled, AntivirusDisabled, TamperBlocked, RtpFeatureFailure, SignatureUpdateFailed, EngineUpdateFailed, SignatureReversion };

    /// <summary>The most health events it reads.</summary>
    public const int MaxHealthEvents = 500;

    /// <summary>
    /// The settings a 5013 may name, with what the change would have done. A setting that is not
    /// here (an exclusion among them) is never named.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> TamperSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [@"Real-Time Protection\DisableRealtimeMonitoring"] = "switch off real-time protection",
        [@"Real-Time Protection\DisableBehaviorMonitoring"] = "switch off behavior monitoring",
        [@"Real-Time Protection\DisableIOAVProtection"] = "switch off the scan of downloaded files and attachments",
        [@"Real-Time Protection\DisableScriptScanning"] = "switch off script scanning",
        [@"Real-Time Protection\DisableOnAccessProtection"] = "switch off on-access protection",
    };

    private static readonly Regex HexCode = new("^0x[0-9a-fA-F]{1,8}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex FeatureWord = new(@"^[A-Za-z][A-Za-z0-9 ()-]{0,59}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The registry roots a 5013 writes before the setting (a real device wrote the first, with HKLM).</summary>
    private static readonly string[] TamperRoots =
    {
        @"SOFTWARE\Microsoft\Windows Defender\",
        @"SOFTWARE\Policies\Microsoft\Windows Defender\",
    };

    /// <summary>
    /// The setting a 5013 names, as a key of the fixed list; empty for any other setting. The value
    /// is "HKLM\SOFTWARE\Microsoft\Windows Defender\Real-Time Protection\DisableRealtimeMonitoring =
    /// (Current)": the registry root and what follows " = " are dropped before the comparison.
    /// </summary>
    internal static string TamperSetting(string value)
    {
        var text = (value ?? "").Trim();
        var cut = text.IndexOf(" = ", StringComparison.Ordinal);
        var key = (cut >= 0 ? text.Substring(0, cut) : text).Trim();
        foreach (var hive in new[] { @"HKLM\", @"HKEY_LOCAL_MACHINE\" })
        {
            if (key.StartsWith(hive, StringComparison.OrdinalIgnoreCase)) { key = key.Substring(hive.Length); }
        }
        foreach (var root in TamperRoots)
        {
            if (key.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { key = key.Substring(root.Length); break; }
        }
        return TamperSettings.ContainsKey(key) ? TamperSettings.Keys.First(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) : "";
    }

    /// <summary>An error code as "0x8024402C"; empty for anything else.</summary>
    internal static string ErrorCode(string value)
    {
        var text = (value ?? "").Trim();
        return HexCode.IsMatch(text) ? "0x" + text.Substring(2).ToUpperInvariant() : "";
    }

    /// <summary>A feature name of a few plain words ("On Access", "Behavior Monitoring"); empty for anything else.</summary>
    internal static string FeatureName(string value)
    {
        var text = Text(value);
        return FeatureWord.IsMatch(text) ? text : "";
    }

    private static string Times(int count) => count == 1 ? "once" : count.ToString(Invariant) + " times";

    private static string When(int count, DateTime newestUtc) => (count == 1 ? "on " : "last on ") + RunContext.UserTime(newestUtc, "yyyy-MM-dd HH:mm");

    /// <summary>
    /// The health facts and their cards. The scan times and what is on now come from Defender's
    /// status; the rest from the log of the period. "Not switched off" is said only for a period
    /// the log covers.
    /// </summary>
    private DefenderHealthFacts Health(DefenderInfo info, DateTime since, DateTime? oldest)
    {
        var health = new DefenderHealthFacts
        {
            QuickScanUtc = info?.QuickScanEndUtc,
            FullScanUtc = info?.FullScanEndUtc,
            RealTimeProtection = info?.RealTimeProtection == true ? "on" : info?.RealTimeProtection == false ? "off" : "unknown",
            OffNow = info?.RealTimeProtection == false || info?.Antivirus == false || info?.Antispyware == false,
        };
        IReadOnlyList<DefenderEvent> events;
        try
        {
            events = Services.DefenderEvents.Read(HealthEventIds, since, MaxHealthEvents + 1);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Run.Log("The health events of the Microsoft Defender log could not be read: " + ex.GetType().Name + ".", LogSeverity.Warning);
            events = null;
        }
        var list = events?.Take(MaxHealthEvents).ToList() ?? new List<DefenderEvent>();
        health.Read = events == null ? "none" : oldest is not DateTime first || first > since || events.Count > MaxHealthEvents ? "short" : "full";

        if (info != null) { AddScanFinding(health); }
        AddSwitchedOffFindings(health, info, list, oldest);
        AddFeatureFindings(health, list);
        AddTamperFindings(health, list);
        AddUpdateFindings(health, info, list);
        Run.Log(string.Format(Invariant, "Microsoft Defender health: last quick scan {0}, last full scan {1}, real-time protection {2}; switched off {3}, feature failures {4}, tamper protection blocks {5}, update failures {6}; log read {7}.",
            health.QuickScanUtc.HasValue ? RunContext.UserTime(health.QuickScanUtc.Value, "yyyy-MM-dd HH:mm") : "none", health.FullScanUtc.HasValue ? RunContext.UserTime(health.FullScanUtc.Value, "yyyy-MM-dd HH:mm") : "none",
            health.RealTimeProtection, health.ProtectionOff, health.FeatureFailures, health.TamperBlocked, health.UpdateFailures, health.Read));
        return health;
    }

    private void AddScanFinding(DefenderHealthFacts health)
    {
        var now = Services.Clock.UtcNow;
        var full = health.FullScanUtc is DateTime f ? " Its last full scan ended on " + RunContext.UserTime(f, "yyyy-MM-dd HH:mm") + "." : " No full scan is recorded on this device.";
        const string NotCounted = " Scans other software asks Defender for (custom scans) are not counted.";
        const string It = "Microsoft Defender's own record of its scans (MSFT_MpComputerStatus: QuickScanEndTime, FullScanEndTime).";
        if (health.QuickScanUtc is DateTime quick)
        {
            var recent = now - quick <= TimeSpan.FromDays(Days);
            Run.AddFinding(recent ? FindingLevel.Ok : FindingLevel.Info, "DEFCHECK-SCAN", "Last quick scan: " + RunContext.UserTime(quick, "yyyy-MM-dd HH:mm"),
                "Microsoft Defender's last quick scan ended on " + RunContext.UserTime(quick, "yyyy-MM-dd HH:mm") + "." + full + NotCounted,
                recent ? "Nothing to do." : "Nothing to do now. If your IT support asks for a scan, they or Windows Security can start one.", It);
            return;
        }
        Run.AddFinding(FindingLevel.Info, "DEFCHECK-SCAN", "No quick scan recorded",
            "Microsoft Defender has no record of a quick scan on this device." + full + NotCounted,
            "Nothing to do now. If your IT support asks for a scan, they or Windows Security can start one.", It);
    }

    private void AddSwitchedOffFindings(DefenderHealthFacts health, DefenderInfo info, List<DefenderEvent> list, DateTime? oldest)
    {
        var kinds = new (int Id, string Title, string Symbol, bool? Now)[]
        {
            (RtpDisabled, "Real-time protection was switched off", "MALWAREPROTECTION_RTP_DISABLED", info?.RealTimeProtection),
            (AntivirusDisabled, "Microsoft Defender Antivirus was switched off", "MALWAREPROTECTION_ANTIVIRUS_DISABLED", info?.Antivirus),
            (AntispywareDisabled, "Microsoft Defender Antispyware was switched off", "MALWAREPROTECTION_ANTISPYWARE_DISABLED", info?.Antispyware),
        };
        var number = 0;
        foreach (var kind in kinds)
        {
            var events = list.Where(e => e.Id == kind.Id).ToList();
            if (events.Count == 0) { continue; }
            number++;
            health.ProtectionOff += events.Count;
            var newest = events[0].TimeCreatedUtc;
            // Real-time protection writes 5000 when it is switched on again; the others are said from Defender's status now.
            var onAgain = kind.Id == RtpDisabled ? list.Where(e => e.Id == RtpEnabled && e.TimeCreatedUtc > newest).Select(e => (DateTime?)e.TimeCreatedUtc).OrderBy(t => t).FirstOrDefault() : null;
            var nowText = kind.Now == false ? " It is off now." : kind.Now == true ? " It is on now." : "";
            var again = onAgain is DateTime on ? " It was switched on again on " + RunContext.UserTime(on, "yyyy-MM-dd HH:mm") + "." : "";
            var warning = kind.Now == false || (kind.Now == null && onAgain == null);
            if (warning) { health.Warning = true; }
            Run.AddFinding(warning ? FindingLevel.Warning : FindingLevel.Info, "DEFCHECK-OFF-" + number.ToString(Invariant), kind.Title,
                "Microsoft Defender logged it " + Times(events.Count) + ", " + When(events.Count, newest) + "." + again + nowText,
                kind.Now == false ? "Copy details for IT: this device is not fully protected while it is off."
                    : "If you did not expect it, copy details for IT.",
                string.Format(Invariant, "Event {0} ({1}). Compare with the device's Defender policy and whether tamper protection is on.", kind.Id, kind.Symbol));
        }
        if (health.OffNow && number == 0)
        {
            // Off now without an event in the period: said from Defender's status.
            var off = new List<string>();
            if (info?.RealTimeProtection == false) { off.Add("real-time protection"); }
            if (info?.Antivirus == false) { off.Add("the antivirus"); }
            if (info?.Antispyware == false) { off.Add("the antispyware"); }
            health.Warning = true;
            Run.AddFinding(FindingLevel.Warning, "DEFCHECK-OFF-NOW", "Microsoft Defender protection is switched off now",
                "Microsoft Defender says " + (off.Count == 1 ? off[0] : string.Join(", ", off.Take(off.Count - 1)) + " and " + off[off.Count - 1]) + " " + (off.Count == 1 ? "is" : "are") + " off now.",
                "Copy details for IT: this device is not fully protected while it is off.",
                "MSFT_MpComputerStatus: RealTimeProtectionEnabled, AntivirusEnabled and AntispywareEnabled. Compare with the device's Defender policy.");
            return;
        }
        if (number > 0 || health.OffNow || list.Any(e => e.Id == RtpFeatureFailure) || health.Read == "none") { return; }
        if (health.Read == "full")
        {
            Run.AddFinding(FindingLevel.Ok, "DEFCHECK-NOT-OFF", "Protection was not switched off in the last " + Days.ToString(Invariant) + " days",
                "The Microsoft Defender log of the last " + Days.ToString(Invariant) + " days has no event that real-time protection, the antivirus or the antispyware was switched off"
                    + (health.RealTimeProtection == "on" ? ", and real-time protection is on now." : "."),
                "Nothing to do.", "Events 5001, 5010 and 5012 (switched off) and 3002 (a feature failed): none in the period.");
        }
        else if (oldest is DateTime first)
        {
            Run.AddFinding(FindingLevel.Ok, "DEFCHECK-NOT-OFF", "Protection was not switched off since " + RunContext.UserTime(first, "yyyy-MM-dd HH:mm"),
                "The Microsoft Defender log reaches back to " + RunContext.UserTime(first, "yyyy-MM-dd HH:mm") + " only, and has no event since then that real-time protection, the antivirus or the antispyware was switched off.",
                "Nothing to do.", "Events 5001, 5010 and 5012 (switched off) and 3002 (a feature failed): none in the log.");
        }
    }

    private void AddFeatureFindings(DefenderHealthFacts health, List<DefenderEvent> list)
    {
        var events = list.Where(e => e.Id == RtpFeatureFailure).ToList();
        if (events.Count == 0) { return; }
        health.FeatureFailures = events.Count;
        health.Warning = true;
        var feature = FeatureName(Value(events[0], "Feature Name"));
        var code = ErrorCode(Value(events[0], "Error Code"));
        Run.AddFinding(FindingLevel.Warning, "DEFCHECK-FEATURE", "A real-time protection feature failed",
            "Microsoft Defender logged it " + Times(events.Count) + ", " + When(events.Count, events[0].TimeCreatedUtc) + "." + (feature.Length > 0 ? " Feature: " + feature + "." : ""),
            "Restart the device, then run a full scan: the device may not have been protected for some time. Copy details for IT if it comes back.",
            "Event 3002 (MALWAREPROTECTION_RTP_FEATURE_FAILURE)" + (code.Length > 0 ? ", error " + code : "") + ". Microsoft's action for it: restart the device, then run a full scan.");
    }

    private void AddTamperFindings(DefenderHealthFacts health, List<DefenderEvent> list)
    {
        // Only a change tamper protection blocked; an event that names another kind of change is not counted.
        var events = list.Where(e => e.Id == TamperBlocked && (Value(e, "Changed Type") is not string type || type.Trim().Equals("Blocked", StringComparison.OrdinalIgnoreCase))).ToList();
        if (events.Count == 0) { return; }
        health.TamperBlocked = events.Count;
        var number = 0;
        foreach (var group in events.GroupBy(e => TamperSetting(Value(e, "Value"))))
        {
            number++;
            var setting = group.Key;
            var change = setting.Length > 0 ? TamperSettings[setting] : "change a protected Defender setting";
            var count = group.Count();
            Run.AddFinding(FindingLevel.Info, "DEFCHECK-TAMPER-" + number.ToString(Invariant),
                setting.Length > 0 && setting.StartsWith(@"Real-Time Protection\", StringComparison.OrdinalIgnoreCase) ? "Tamper protection kept real-time protection on" : "Tamper protection blocked a change to Defender",
                "Something tried to " + change + " " + Times(count) + ", " + When(count, group.First().TimeCreatedUtc) + ". Tamper protection blocked it, so the setting stayed as it was.",
                "Nothing to do: protection stayed on. If you did not try to change it, copy details for IT.",
                "Event 5013 (tamper protection blocked a change)" + (setting.Length > 0 ? "; setting " + setting : "; a setting this check does not name") + ".");
        }
    }

    private void AddUpdateFindings(DefenderHealthFacts health, DefenderInfo info, List<DefenderEvent> list)
    {
        var kinds = new (int Id, string Title, string Symbol)[]
        {
            (SignatureUpdateFailed, "A security intelligence update failed", "MALWAREPROTECTION_SIGNATURE_UPDATE_FAILED"),
            (SignatureReversion, "Microsoft Defender could not load its security intelligence", "MALWAREPROTECTION_SIGNATURE_REVERSION"),
            (EngineUpdateFailed, "An antimalware engine update failed", "MALWAREPROTECTION_ENGINE_UPDATE_FAILED"),
        };
        var number = 0;
        foreach (var kind in kinds)
        {
            var events = list.Where(e => e.Id == kind.Id).ToList();
            if (events.Count == 0) { continue; }
            number++;
            health.UpdateFailures += events.Count;
            var newest = events[0].TimeCreatedUtc;
            var code = ErrorCode(Value(events[0], "Error Code"));
            // Security intelligence has its own update time; for the engine this check cannot see a later update.
            var fixedSince = kind.Id != EngineUpdateFailed && info?.UpdatedUtc is DateTime updated && updated > newest ? updated : (DateTime?)null;
            var after = fixedSince is DateTime u ? " Security intelligence was updated since, on " + RunContext.UserTime(u, "yyyy-MM-dd HH:mm") + "."
                : kind.Id == EngineUpdateFailed ? " This check cannot see whether a later engine update succeeded."
                : info?.UpdatedUtc is DateTime last ? " Its last successful update was on " + RunContext.UserTime(last, "yyyy-MM-dd HH:mm") + ", before that." : "";
            var warning = fixedSince == null;
            if (warning) { health.UpdateWarning = true; health.Warning = true; }
            Run.AddFinding(warning ? FindingLevel.Warning : FindingLevel.Info, "DEFCHECK-UPDATE-" + number.ToString(Invariant), kind.Title,
                "Microsoft Defender logged it " + Times(events.Count) + ", " + When(events.Count, newest) + (kind.Id == SignatureReversion ? "; it went back to an earlier version." : ".") + after,
                warning ? "If it comes back, copy details for IT: Defender may not have its newest protection." : "Nothing to do: a later update succeeded.",
                string.Format(Invariant, "Event {0} ({1}){2}. The update source is not read (it can be a share path); check it and the device's connection to it.",
                    kind.Id, kind.Symbol, code.Length > 0 ? ", error " + code : ""));
        }
    }
}
