using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EndpointToolbelt.Protocol;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// The health rows of the device settings the service reads as SYSTEM.
/// The service sends tokens; the words and the thresholds are here.
/// </summary>
public static class DeviceSettingRows
{
    /// <summary>Clock: OK below this offset, Check from it.</summary>
    public const double ClockCheckSeconds = 60;

    /// <summary>Clock: Problem from this offset, Kerberos' default tolerance.</summary>
    public const double ClockProblemSeconds = 300;

    /// <summary>Defender: security intelligence older than this many days is Check, older than the second a Problem.</summary>
    public const int DefenderCheckDays = 3;
    public const int DefenderProblemDays = 7;

    private const string NotReadYet = "Unknown: still being read; check again in a moment";

    public static HealthResult Compliance(StatusInfo status, ComplianceReadout c)
    {
        if (status == null) { return HealthChecks.Unavailable(HealthChecks.Compliance); }
        if (c == null) { return HealthChecks.Make(HealthChecks.Compliance, HealthStatus.Warn, NotReadYet); }
        var off = new List<string>();
        var unknown = new List<string>();
        void Judge(string name, string token, string good)
        {
            if (token == good) { return; }
            if (token == null || token == "unknown") { unknown.Add(name); return; }
            off.Add(name + " " + Word(token));
        }
        Judge("BitLocker", c.BitLocker, "on");
        Judge("Secure Boot", c.SecureBoot, "on");
        Judge("TPM", c.Tpm, "ready");
        var firewallOff = new[] { ("domain", c.FirewallDomain), ("private", c.FirewallPrivate), ("public", c.FirewallPublic) }
            .Where(p => p.Item2 == "off").Select(p => p.Item1).ToList();
        if (firewallOff.Count > 0) { off.Add("firewall off (" + string.Join(", ", firewallOff) + ")"); }
        else if (new[] { c.FirewallDomain, c.FirewallPrivate, c.FirewallPublic }.Any(v => v != "on")) { unknown.Add("firewall"); }

        string detail;
        HealthStatus state;
        if (off.Count == 0 && unknown.Count == 0)
        {
            state = HealthStatus.Pass;
            detail = "BitLocker, Secure Boot, TPM" + (c.TpmVersion == null ? "" : " " + c.TpmVersion) + " and firewall are on";
        }
        else
        {
            // Never a verdict: Intune decides compliance; this says which setting is off.
            state = HealthStatus.Warn;
            var parts = off.ToList();
            if (unknown.Count > 0) { parts.Add(string.Join(", ", unknown) + " unknown"); }
            detail = Capital(string.Join(", ", parts));
        }
        var row = HealthChecks.Make(HealthChecks.Compliance, state, detail);
        row.ReportDetail = string.Format(CultureInfo.InvariantCulture,
            "BitLocker {0} (recovery password: {1}), Secure Boot {2}, TPM {3}{4}, firewall domain {5} / private {6} / public {7}, OS {8}{9}",
            Word(c.BitLocker), c.RecoveryPassword == null ? "unknown" : c.RecoveryPassword.Value ? "yes" : "no", Word(c.SecureBoot), Word(c.Tpm),
            c.TpmVersion == null ? "" : " " + c.TpmVersion, Word(c.FirewallDomain), Word(c.FirewallPrivate), Word(c.FirewallPublic),
            c.OsBuild ?? "unknown", c.OsVersion == null ? "" : " (" + c.OsVersion + ")");
        return row;
    }

    public static HealthResult Profiles(StatusInfo status, ProfileErrorsReadout p)
    {
        if (status == null) { return HealthChecks.Unavailable(HealthChecks.Profiles); }
        if (p == null) { return HealthChecks.Make(HealthChecks.Profiles, HealthStatus.Warn, NotReadYet); }
        if (p.Unavailable != null) { return HealthChecks.Make(HealthChecks.Profiles, HealthStatus.Warn, "Unknown: the MDM log could not be read"); }
        var warnings = (p.Warnings == 0 ? "" : "; " + Words.Count(p.Warnings, "warning"))
            + (p.KnownHarmless == 0 ? "" : "; " + Words.Count(p.KnownHarmless, "known harmless event") + " not counted");
        var settings = p.Settings ?? new List<string>();
        // Settings that cannot apply on this machine (Windows Hello on a virtual machine or a
        // multi-session host) are no failed check: named in the report, never a step.
        var notApplicable = p.NotApplicable ?? new List<string>();
        // A setting whose newest failure was a removal reads "removing ... failed": that is not a
        // setting that failed to apply.
        var removals = new HashSet<string>(p.Removals ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        string Named(string setting, Func<string, string> name) => removals.Contains(setting) ? "removing " + name(setting) + " failed" : name(setting);
        var notApplicableReport = notApplicable.Count == 0 ? ""
            : "; not applicable on this device (" + MachineWords(status.Readouts?.Machine) + "): " + string.Join("; ", notApplicable.Select(s => Named(s, SettingNames.WithToken)));
        if (settings.Count == 0)
        {
            if (notApplicable.Count > 0)
            {
                var info = HealthChecks.Make(HealthChecks.Profiles, HealthStatus.Pass, "Not applicable on this device: " + Words.Count(notApplicable.Count, "Windows Hello setting"));
                info.Info = true;
                info.Hint = NotApplicableHint;
                info.ReportDetail = "No failed settings in the last 7 days" + notApplicableReport + "; " + Words.Count(p.Errors, "other MDM error") + warnings + " (MDM admin log)";
                return info;
            }
            // An MDM error that names no setting (enrollment, a sync) is not a profile failure:
            // it stays in the report and the support package, not on this row.
            var ok = HealthChecks.Make(HealthChecks.Profiles, HealthStatus.Pass, "No failed settings in the last 7 days");
            ok.ReportDetail = "No failed settings in the last 7 days; " + Words.Count(p.Errors, "other MDM error") + warnings + " (MDM admin log)" + AlreadySucceededReport(p);
            return ok;
        }
        // Plain names ("Windows Hello PIN: lowercase letters"), separated by semicolons because a
        // name has a colon of its own; the report keeps the token for IT support.
        // When each last failed: a failure stays in the log for 7 days, also once the setting applied or was unassigned.
        var times = p.SettingTimes ?? new List<DateTime>();
        string When(int i, Func<DateTime, string> format) => i < times.Count && times[i] != DateTime.MinValue ? ", last failed " + format(DateTime.SpecifyKind(times[i], DateTimeKind.Utc)) : "";
        var shown = settings.Take(3).Select((s, i) => Named(s, SettingNames.Friendly) + When(i, t => ToolRuns.ShortWhen(t, TimeZoneInfo.Local, DateTime.UtcNow))).ToList();
        var detail = Words.Count(settings.Count, "setting") + " failed in the last 7 days: " + string.Join("; ", shown) + (settings.Count > shown.Count ? " and more" : "");
        var row = HealthChecks.Make(HealthChecks.Profiles, HealthStatus.Warn, detail);
        row.ReportDetail = Words.Count(p.Errors, "error") + warnings + " in the last 7 days (MDM admin log)"
            + "; settings: " + string.Join("; ", settings.Select((s, i) => Named(s, SettingNames.WithToken) + When(i, t => ToolRuns.FullWhen(t, TimeZoneInfo.Local)))) + notApplicableReport
            + AlreadySucceededReport(p);
        return row;
    }

    /// <summary>
    /// For the report only: failed commands on an Office installation whose own status says it
    /// succeeded (the MDM client sends it again), which are no failed setting.
    /// </summary>
    internal static string AlreadySucceededReport(ProfileErrorsReadout p)
        => p.AlreadySucceeded > 0
            ? "; " + Words.Count(p.AlreadySucceeded, "failed command") + " on an Office installation that succeeded on this device (not counted)"
            : "";

    /// <summary>The hint of a profiles row whose only failed settings cannot apply on this machine.</summary>
    public const string NotApplicableHint =
        "Windows Hello for Business settings from Intune cannot apply on a virtual machine or a multi-session host, so their errors are not a fault on this device. Nothing to do here; IT support can leave such devices out of the Windows Hello for Business policy.";

    /// <summary>The machine kind as the service read it, for the device report.</summary>
    public static string MachineLine(MachineReadout machine)
    {
        static string YesNo(bool? value) => value == null ? "unknown" : value.Value ? "yes" : "no";
        return "Machine kind   : virtual machine " + YesNo(machine?.VirtualMachine) + ", multi-session host " + YesNo(machine?.MultiSession);
    }

    /// <summary>Why a Windows Hello setting cannot apply, for the report.</summary>
    internal static string MachineWords(MachineReadout machine)
        => machine?.MultiSession == true ? "a multi-session host" : machine?.VirtualMachine == true ? "a virtual machine" : "this kind of device";

    /// <summary>The detail of a clock row whose virtual machine host keeps the time.</summary>
    public const string HostSyncedDetail = "Synced by the virtual machine host";

    /// <summary>The hint of that row.</summary>
    public const string HostSyncedHint =
        "This virtual machine gets its time from its host through the Hyper-V time provider. Its time server could not be measured from here, which is common on a virtual machine; the host keeps the clock right.";

    public static HealthResult Clock(StatusInfo status, ClockReadout c)
    {
        if (status == null) { return HealthChecks.Unavailable(HealthChecks.Clock); }
        if (c == null) { return HealthChecks.Make(HealthChecks.Clock, HealthStatus.Warn, NotReadYet); }
        var source = c.Source switch
        {
            "domain" => "domain time source",
            "ntp" => "time server",
            "local" => "local clock",
            "none" => "no time source",
            _ => "time source",
        };
        if (c.OffsetSeconds is not double offset)
        {
            if (c.HostTimeProvider == true && c.Unavailable is "no-answer" or "no-source")
            {
                // On a virtual machine the host keeps the clock (Hyper-V time provider); an NTP
                // server that does not answer (UDP 123 is often blocked) is no fault, and Sync clock
                // is not the fix.
                var host = HealthChecks.Make(HealthChecks.Clock, HealthStatus.Pass, HostSyncedDetail);
                host.Info = true;
                host.Hint = HostSyncedHint;
                host.ReportDetail = HostSyncedDetail + ": Hyper-V time provider on; " + (c.Unavailable == "no-answer" ? "the time source did not answer" : "no time source to compare with") + " (" + source + ")";
                return host;
            }
            var why = c.Unavailable switch
            {
                "not-running" => "The Windows Time service is not running",
                "no-source" => "Unknown: there is no time source to compare with",
                "no-answer" => "Unknown: the time source did not answer",
                _ => "Unknown: could not be measured",
            };
            var unknown = HealthChecks.Make(HealthChecks.Clock, HealthStatus.Warn, why);
            // Sync clock starts a stopped time service; without a source or an answer it cannot help.
            unknown.FixOverride = c.Unavailable == "not-running" ? HealthFix.SyncClock : HealthFix.None;
            unknown.ReportDetail = why + " (" + source + ")";
            return unknown;
        }
        var abs = Math.Abs(offset);
        var state = abs < ClockCheckSeconds ? HealthStatus.Pass : abs < ClockProblemSeconds ? HealthStatus.Warn : HealthStatus.Fail;
        var text = abs < 1 ? "Less than a second off" : abs < ClockCheckSeconds
            ? string.Format(CultureInfo.InvariantCulture, "{0:0} seconds off", abs)
            : Words.Count((int)Math.Round(abs / 60), "minute") + " off";
        var row = HealthChecks.Make(HealthChecks.Clock, state, text + " (" + source + ")");
        row.ReportDetail = string.Format(CultureInfo.InvariantCulture, "Offset {0:+0.000;-0.000} s to the {1} (w32tm)", offset, source);
        return row;
    }

    public static HealthResult Defender(StatusInfo status, DefenderReadout d, DateTime nowUtc)
    {
        if (status == null) { return HealthChecks.Unavailable(HealthChecks.Defender); }
        if (d == null) { return HealthChecks.Make(HealthChecks.Defender, HealthStatus.Warn, NotReadYet); }
        switch (d.Mode)
        {
            case "passive":
            case "edr-block":
                // Another antivirus is in charge: not Defender's job, so not a problem here.
                var passive = HealthChecks.Make(HealthChecks.Defender, HealthStatus.Pass, "Passive: another antivirus is in charge");
                passive.ReportDetail = "Running mode " + d.Mode + ", security intelligence " + (d.SignatureVersion ?? "unknown");
                return passive;
            case "off":
                return NoFix(HealthChecks.Make(HealthChecks.Defender, HealthStatus.Warn, "Off: another antivirus may be in charge"));
            case "missing":
                return NoFix(HealthChecks.Make(HealthChecks.Defender, HealthStatus.Warn, "Not found on this device"));
            case "normal":
                break;
            default:
                return NoFix(HealthChecks.Make(HealthChecks.Defender, HealthStatus.Warn, "Unknown: could not be read"));
        }
        var updated = TimeText.ParseUtc(d.SignatureUpdatedUtc);
        var problems = new List<string>();
        var state = HealthStatus.Pass;
        if (d.RealTimeProtection == false) { problems.Add("real-time protection off"); state = HealthStatus.Fail; }
        string age;
        if (updated == null)
        {
            age = "update time unknown";
            if (state == HealthStatus.Pass) { state = HealthStatus.Warn; }
        }
        else
        {
            var days = (nowUtc - updated.Value).TotalDays;
            age = "updated " + TimeText.Age(nowUtc - updated.Value);
            if (days > DefenderProblemDays) { state = HealthStatus.Fail; problems.Add("security intelligence out of date"); }
            else if (days > DefenderCheckDays && state == HealthStatus.Pass) { state = HealthStatus.Warn; problems.Add("security intelligence getting old"); }
        }
        var detail = problems.Count == 0 ? "On, " + age : Capital(string.Join(", ", problems)) + " (" + age + ")";
        var row = HealthChecks.Make(HealthChecks.Defender, state, detail);
        // Update definitions fixes old security intelligence, not real-time protection that is off.
        row.FixOverride = d.RealTimeProtection != false && (updated == null || (nowUtc - updated.Value).TotalDays > DefenderCheckDays) ? HealthFix.UpdateDefender : HealthFix.None;
        row.ReportDetail = string.Format(CultureInfo.InvariantCulture, "Running mode normal, real-time protection {0}, security intelligence {1} ({2})",
            d.RealTimeProtection == null ? "unknown" : d.RealTimeProtection.Value ? "on" : "off", d.SignatureVersion ?? "unknown", age);
        return row;
    }

    /// <summary>The device report lines of the Intune connection readout.</summary>
    public static IEnumerable<string> IntuneConnectionLines(IntuneConnectionReadout i)
    {
        if (i == null) { yield return "Intune tasks   : unknown (not read yet)"; yield break; }
        if (i.Unavailable == "not-enrolled") { yield return "Intune tasks   : no single Intune enrollment"; yield break; }
        if (i.Unavailable != null) { yield return "Intune tasks   : could not be read"; yield break; }
        // The disabled tasks by name, so IT support knows which one to look at.
        var names = i.DisabledTasks ?? new List<string>();
        var disabledCount = i.Tasks - i.TasksEnabled;
        var disabled = names.Count == 0 || disabledCount <= 0 ? ""
            : "; disabled: " + string.Join(", ", names) + (disabledCount > names.Count ? string.Format(CultureInfo.InvariantCulture, " and {0} more", disabledCount - names.Count) : "");
        yield return string.Format(CultureInfo.InvariantCulture, "Intune tasks   : {0}, {1} enabled{2}", Words.Count(i.Tasks, "task"), i.TasksEnabled, disabled);
        var last = TimeText.ParseUtc(i.LastRunUtc);
        yield return last == null
            ? "Last task run  : none recorded"
            : string.Format(CultureInfo.InvariantCulture, "Last task run  : {0} (result {1})", TimeText.InLocalTime(last.Value), i.LastResult ?? "unknown");
        // Microsoft documents no local repair for the enrollment: a step for IT support.
        if (i.Tasks == 0 || i.TasksEnabled == 0 || TaskRunFailed(i.LastResult))
        {
            yield return "For IT support : the enrollment's scheduled tasks are missing, disabled or failing. Microsoft documents no local repair; check the device in Intune and enroll it again if it no longer syncs.";
        }
    }

    /// <summary>
    /// A newest run that failed: any result except success (0x00000000) and the Task Scheduler
    /// statuses of a task that is still running (0x00041301) or queued (0x00041325), which an
    /// older service can report as the newest result.
    /// </summary>
    public static bool TaskRunFailed(string lastResult)
        => lastResult != null && lastResult != "0x00000000" && lastResult != "0x00041301" && lastResult != "0x00041325";

    private static string Word(string token) => token switch
    {
        null => "unknown",
        "not-ready" => "not ready",
        _ => token,
    };

    private static HealthResult NoFix(HealthResult row)
    {
        row.FixOverride = HealthFix.None;
        return row;
    }

    private static string Capital(string text) => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
}
