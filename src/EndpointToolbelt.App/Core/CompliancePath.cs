using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EndpointToolbelt.Protocol;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// "My device is blocked or 'not compliant'", for a user whom Company Portal or a sign-in
/// tells that the device does not meet the organization's requirements. It shows what the device
/// itself can know, one row per item, from readouts that already exist (the enrollment and the last
/// sync, the local compliance settings, the Defender row, the pending restart): nothing new is
/// probed. It never gives a compliance verdict and never names a Conditional Access reason: the
/// device cannot see either, and the path says so and points to Company Portal's device status.
/// Each item that is not OK gets one next step, for the user or for IT ("IT: ..."); Retry apps
/// &amp; policies (which requests a sync) is offered only on the sync row, where a sync can change
/// what Intune decides.
/// </summary>
public static class CompliancePath
{
    /// <summary>The path's title and subtitle.</summary>
    public const string Title = "My device is blocked or 'not compliant'";
    public const string Subtitle = "Company Portal or a sign-in says it doesn't meet requirements";

    /// <summary>The path's read-only check, a tool of For IT that runs in the app (its run log carries this name).</summary>
    public const string CheckName = "Check compliance settings";

    /// <summary>The history line's action name.</summary>
    public const string HistoryName = "ComplianceCheck";

    // The rows, as the path shows them.
    public const string Intune = "Intune enrollment and sync";
    public const string BitLocker = "BitLocker";
    public const string Defender = HealthChecks.Defender;
    public const string Firewall = "Firewall";
    public const string SecureBootTpm = "Secure Boot and TPM";
    public const string WindowsVersion = "Windows version";
    public const string Restart = HealthChecks.PendingRestart;

    /// <summary>
    /// Two rows of two items each, so the path fits 540 x 640 at 150 % display scale with real texts
    /// and a support contact (the Sandbox's run): Defender with the firewall, the Windows version with
    /// the pending restart. Each shows the worst of its two items and that item's step.
    /// </summary>
    public const string DefenderFirewall = "Defender and firewall";
    public const string WindowsRestart = "Windows version and restart";

    /// <summary>One row in place of the service's settings while they cannot be read (the service is away, or still reading).</summary>
    public const string DeviceSettings = "Device settings";

    /// <summary>The path's rows in the order it shows them once the service's readouts are in.</summary>
    public static readonly IReadOnlyList<string> RowNames = new[] { Intune, BitLocker, DefenderFirewall, SecureBootTpm, WindowsRestart };

    /// <summary>What this device cannot know, in one sentence, and where the verdict is (the path's intro).</summary>
    public const string NotKnown =
        "This device cannot see the compliance verdict or why sign-in was blocked; Company Portal shows it (Devices, this device, Check status).";

    private const string NotReadYet = "Unknown: still being read; check again in a moment";

    /// <summary>
    /// The path's rows from the detailed health checks (<see cref="HealthChecks.Run"/>) and the service's
    /// status: <paramref name="status"/> null means the service could not be reached; its readouts may
    /// still be missing (being read). Each row that is not OK has its one next step as its hint.
    /// </summary>
    public static List<HealthResult> Rows(IReadOnlyList<HealthResult> detailed, StatusInfo status)
    {
        var list = detailed ?? Array.Empty<HealthResult>();
        if (list.Count == 0) { return new List<HealthResult>(); }
        var byName = new Dictionary<string, HealthResult>(StringComparer.Ordinal);
        foreach (var r in list) { byName[r.Name] = r; }
        var c = status?.Readouts?.Compliance;
        var rows = new List<HealthResult> { IntuneRow(byName) };
        var defender = byName.TryGetValue(HealthChecks.Defender, out var d) ? DefenderRow(d) : null;
        var restart = byName.TryGetValue(HealthChecks.PendingRestart, out var p) ? RestartRow(p) : null;
        if (status == null || c == null)
        {
            // The same unknown for every setting the service reads: one row, not five.
            rows.Add(SettingsUnknown(status));
            if (defender != null) { rows.Add(defender); }
            if (restart != null) { rows.Add(restart); }
            return rows;
        }
        rows.Add(BitLockerRow(c));
        rows.Add(Pair(DefenderFirewall, defender, "Defender", FirewallRow(c), "firewall"));
        rows.Add(SecureBootTpmRow(c));
        rows.Add(Pair(WindowsRestart, WindowsRow(c), "", restart, ""));
        return rows;
    }

    /// <summary>
    /// Two items in one row: the worst of the two, its detail first, and its step (its fix, or its hint);
    /// both details when both are OK. A missing first item leaves the second alone under the pair's name.
    /// </summary>
    internal static HealthResult Pair(string name, HealthResult first, string firstLabel, HealthResult second, string secondLabel)
    {
        // "Defender up to date; firewall on for every network", "Firewall off for public networks; Defender up to date".
        string Part(HealthResult r, string label) => (label.Length == 0 ? "" : label + " ") + LowerStart(r.Detail);
        var parts = new[] { (Row: first, Label: firstLabel), (Row: second, Label: secondLabel) }.Where(x => x.Row != null).ToList();
        if (parts.Count == 0) { return Row(name, HealthStatus.Warn, NotReadYet, "Use Check this PC again on Home in a moment."); }
        var worst = parts.OrderByDescending(x => x.Row.Status).First();
        var others = parts.Where(x => !ReferenceEquals(x.Row, worst.Row)).Select(x => Part(x.Row, x.Label));
        var lead = Part(worst.Row, worst.Label);
        lead = lead.Length == 0 ? lead : char.ToUpperInvariant(lead[0]) + lead.Substring(1);
        var detail = string.Join("; ", new[] { lead }.Concat(others));
        var pass = worst.Row.Status == HealthStatus.Pass;
        var row = Row(name, worst.Row.Status, detail, pass ? string.Join(" ", parts.Select(x => x.Row.Hint).Where(h => !string.IsNullOrEmpty(h))) : worst.Row.Hint, worst.Row.Fix);
        row.ServiceUnavailable = !pass && worst.Row.ServiceUnavailable;
        return row;
    }

    private static string LowerStart(string text)
        => string.IsNullOrEmpty(text) || (text.Length > 1 && char.IsUpper(text[1])) ? text ?? "" : char.ToLowerInvariant(text[0]) + text.Substring(1);

    /// <summary>
    /// The path's verdict: which items to look at (each has its step on its row), or that the settings
    /// this device can check look fine; never that the device is compliant.
    /// </summary>
    public static (string Text, Tone Tone) Verdict(IReadOnlyList<HealthResult> rows)
    {
        var mine = rows ?? Array.Empty<HealthResult>();
        if (mine.Count == 0) { return ("These checks have not run yet. Use Check this PC again on Home.", Tone.Neutral); }
        var notOk = mine.Where(r => r.Status != HealthStatus.Pass).OrderByDescending(r => r.Status).ToList();
        if (notOk.Count == 0) { return ("The settings this device can check look fine.", Tone.Neutral); }
        var tone = notOk.Any(r => r.Status == HealthStatus.Fail) ? Tone.Error : Tone.Warning;
        return (string.Format(CultureInfo.InvariantCulture, "To look at: {0} of {1} items.", notOk.Count, mine.Count), tone);
    }

    /// <summary>
    /// The one next step of a row that is not OK, as the row's hint says it: the row's fix (a link on
    /// the row), a step for the user, or a step for IT ("IT: ..."). Empty for an OK row.
    /// </summary>
    public static string NextStep(HealthResult row)
    {
        if (row == null || row.Status == HealthStatus.Pass) { return ""; }
        if (row.Fix != HealthFix.None) { return "Try " + Words.FixLink(row.Fix) + "."; }
        return row.Hint ?? "";
    }

    /// <summary>A step is for IT when it starts with "IT:".</summary>
    public static bool ForIt(string step) => !string.IsNullOrEmpty(step) && step.StartsWith("IT:", StringComparison.Ordinal);

    /// <summary>
    /// The rows that are not OK as result cards (problems first), with each one's step as the user's
    /// next step or as IT support's; for Copy details for IT and the check's result.
    /// </summary>
    public static List<ResultCard> Cards(IEnumerable<HealthResult> rows)
        => (rows ?? Enumerable.Empty<HealthResult>()).Where(r => r != null && r.Status != HealthStatus.Pass)
            .OrderByDescending(r => r.Status).Select(Card).ToList();

    private static ResultCard Card(HealthResult row)
    {
        var severity = row.Status == HealthStatus.Fail ? FindingSeverity.Error : FindingSeverity.Warning;
        var step = NextStep(row);
        var it = ForIt(step);
        var title = string.IsNullOrWhiteSpace(row.Detail) ? row.Name + ": " + row.StatusWord : row.Name + ": " + row.Detail;
        return ResultCard.From(severity, title, "", it ? "" : step, it ? step.Substring(3).Trim() : "");
    }

    /// <summary>The card that says where the verdict is (Company Portal) and why this device cannot know it.</summary>
    public static ResultCard NotKnownCard()
        => ResultCard.From(FindingSeverity.Info, "Company Portal shows whether this device meets your organization's requirements",
            "This device cannot see that verdict, or why a sign-in was blocked: your organization's policies and Conditional Access decide them, and neither is stored on the device.",
            "Open Company Portal, choose Devices, then this device, then Check status (or Check access)",
            "Check the device's compliance and the sign-in logs in the admin centers");

    /// <summary>
    /// The card with the settings that are fine, one line each with what was seen ("BitLocker: On for
    /// the Windows drive"), so the user sees what was checked; null when none is fine.
    /// </summary>
    public static ResultCard FineCard(IEnumerable<HealthResult> rows)
    {
        var fine = (rows ?? Enumerable.Empty<HealthResult>()).Where(r => r != null && r.Status == HealthStatus.Pass).ToList();
        if (fine.Count == 0) { return null; }
        var lines = fine.Select(r => string.IsNullOrWhiteSpace(r.Detail) ? r.Name + ": " + r.StatusWord : r.Name + ": " + r.Detail);
        return ResultCard.From(FindingSeverity.Ok, Words.Count(fine.Count, "setting") + (fine.Count == 1 ? " looks fine" : " look fine"), string.Join("\n", lines), "");
    }

    /// <summary>What the check (<see cref="CheckName"/>) shows, records and copies.</summary>
    public sealed class Outcome
    {
        public string Status { get; set; } = "";
        public Tone Tone { get; set; }
        public List<ResultCard> Cards { get; } = new();
        public List<string> LogLines { get; } = new();

        /// <summary>The number of items that are not OK.</summary>
        public int NotOk { get; set; }

        /// <summary>"all-fine", "to-look-at" or "not-read" (the service or its readouts were missing).</summary>
        public string Slug { get; set; } = "";
    }

    /// <summary>
    /// The check: its status line, a card per item that is not OK with its step, the card that says
    /// what this device cannot know, and one run log line per item (its state as a token, never a
    /// name, an address or a path).
    /// </summary>
    public static Outcome Check(IReadOnlyList<HealthResult> rows, string runId, DateTime utcNow)
    {
        var list = rows ?? Array.Empty<HealthResult>();
        var outcome = new Outcome();
        var notOk = list.Where(r => r.Status != HealthStatus.Pass).OrderByDescending(r => r.Status).ToList();
        outcome.NotOk = notOk.Count;
        var unread = list.Count == 0 || list.Any(r => r.ServiceUnavailable || (r.Detail ?? "").StartsWith(NotReadYet, StringComparison.Ordinal));
        outcome.Slug = unread ? "not-read" : notOk.Count == 0 ? "all-fine" : "to-look-at";
        if (list.Count == 0)
        {
            outcome.Status = "The settings could not be read. Use Check this PC again on Home, then run this again.";
            outcome.Tone = Tone.Warning;
        }
        else if (notOk.Count == 0)
        {
            // Short, so the line under the tools is not cut off; the card says where the verdict is.
            outcome.Status = string.Format(CultureInfo.InvariantCulture, "The settings this device can check look fine: {0} of {0}.", list.Count);
            outcome.Tone = Tone.Success;
        }
        else
        {
            outcome.Status = Words.Count(notOk.Count, "item") + " to look at: " + string.Join(", ", notOk.Select(r => r.Name)) + ". Each has its next step.";
            outcome.Tone = notOk.Any(r => r.Status == HealthStatus.Fail) ? Tone.Error : Tone.Warning;
        }
        outcome.Cards.AddRange(Cards(list));
        var fineCard = FineCard(list);
        if (fineCard != null) { outcome.Cards.Add(fineCard); }
        outcome.Cards.Add(NotKnownCard());
        var id = string.IsNullOrEmpty(runId) ? "none" : runId;
        // First what was checked in words (the item and its state, nothing read from the device),
        // then the records IT support's tools read.
        foreach (var row in list) { outcome.LogLines.Add(row.Name + ": " + row.StatusWord + "."); }
        foreach (var row in list)
        {
            outcome.LogLines.Add(string.Format(CultureInfo.InvariantCulture, "COMPLIANCE run={0} time={1:yyyy-MM-ddTHH:mm:ss.fffZ} context=user-app item={2} status={3} step={4}",
                id, utcNow, Token(row.Name), StatusToken(row), StepToken(row)));
        }
        outcome.LogLines.Add(string.Format(CultureInfo.InvariantCulture, "COMPLIANCE run={0} result={1} not-ok={2} verdict=not-visible-on-device", id, outcome.Slug, notOk.Count));
        return outcome;
    }

    /// <summary>The state in the technical log, as older versions wrote it (the screen says Warning, the log keeps "check").</summary>
    private static string StatusToken(HealthResult row) => row.Status switch { HealthStatus.Pass => row.Info ? "info" : "ok", HealthStatus.Warn => "check", _ => "problem" };

    /// <summary>"&lt;count&gt;|&lt;run ID&gt;|&lt;result&gt;", as the other checks in the app write their history.</summary>
    public static string HistoryResult(Outcome outcome, string runId)
        => outcome.NotOk.ToString(CultureInfo.InvariantCulture) + "|" + (string.IsNullOrEmpty(runId) ? "none" : runId) + "|" + outcome.Slug;

    /// <summary>The history result in words; null for a result this app does not know.</summary>
    public static string ResultWords(string slug) => slug switch
    {
        "all-fine" => "the settings this device can check look fine",
        "to-look-at" => "settings to look at",
        "not-read" => "the settings could not all be read",
        _ => null,
    };

    private static string Token(string text) => string.IsNullOrEmpty(text) ? "none" : text.Replace(' ', '-').ToLowerInvariant();

    private static string StepToken(HealthResult row)
    {
        if (row.Status == HealthStatus.Pass) { return "none"; }
        if (row.Fix != HealthFix.None) { return "fix-" + row.Fix.ToString().ToLowerInvariant(); }
        return ForIt(NextStep(row)) ? "it" : "user";
    }

    // --- The rows --------------------------------------------------------------------------------

    private static HealthResult Row(string name, HealthStatus status, string detail, string hint, HealthFix fix = HealthFix.None)
        => new() { Name = name, Status = status, Detail = detail ?? "", Hint = hint ?? "", FixOverride = fix };

    /// <summary>The local compliance settings while they cannot be read: unavailable without the service, "being read" while the readout is missing.</summary>
    private static HealthResult SettingsUnknown(StatusInfo status)
    {
        if (status == null)
        {
            var unavailable = HealthChecks.Unavailable(DeviceSettings);
            unavailable.Hint = "IT: the " + ProductInfo.DisplayName + " service is not available, so BitLocker, the firewall, Secure Boot, the TPM and the Windows version could not be read.";
            return unavailable;
        }
        return Row(DeviceSettings, HealthStatus.Warn, NotReadYet, "Use Check this PC again on Home in a moment.");
    }

    /// <summary>
    /// The enrollment and the last sync in one row: without an enrollment Intune cannot judge the device
    /// at all (IT); an old sync means Intune judged older settings, and a sync now lets it judge again
    /// (Retry work apps and settings requests one). The sync row is the only one with that step.
    /// </summary>
    private static HealthResult IntuneRow(Dictionary<string, HealthResult> byName)
    {
        byName.TryGetValue(HealthChecks.Enrollment, out var enrollment);
        byName.TryGetValue(HealthChecks.LastSync, out var sync);
        if (enrollment == null || enrollment.ServiceUnavailable)
        {
            var unavailable = HealthChecks.Unavailable(Intune);
            unavailable.Hint = "IT: the " + ProductInfo.DisplayName + " service is not available, so the enrollment could not be read.";
            return unavailable;
        }
        if (enrollment.Status == HealthStatus.Fail)
        {
            return Row(Intune, HealthStatus.Fail, "No Intune enrollment found", "IT: enroll this device in Intune; until then it cannot meet a compliance policy.");
        }
        if (enrollment.Status == HealthStatus.Warn)
        {
            return Row(Intune, HealthStatus.Warn, enrollment.Detail, "IT: check this device's Intune enrollment; it could not be read as one enrollment.");
        }
        if (sync == null || sync.ServiceUnavailable)
        {
            return Row(Intune, HealthStatus.Warn, "Enrolled; last sync unknown", "Intune judges the settings it received at the last sync; a sync now sends the current ones.", HealthFix.RetryAppsAndPolicies);
        }
        if (sync.Status == HealthStatus.Pass)
        {
            return Row(Intune, HealthStatus.Pass, "Enrolled; last sync " + sync.Detail, "Intune judges this device with the settings it received at the last sync.");
        }
        return Row(Intune, sync.Status, "Enrolled; last sync " + sync.Detail,
            "Intune judges the settings it received at the last sync; a sync now sends the current ones.", HealthFix.RetryAppsAndPolicies);
    }

    private static HealthResult BitLockerRow(ComplianceReadout c) => c.BitLocker switch
    {
        "on" => Row(BitLocker, HealthStatus.Pass, "On for the Windows drive", "BitLocker encrypts the Windows drive."),
        "suspended" => Row(BitLocker, HealthStatus.Warn, "Suspended", "Restart when your organization allows it; BitLocker usually resumes after a restart."),
        "encrypting" => Row(BitLocker, HealthStatus.Warn, "Still encrypting", "Keep the device on and plugged in until encryption finishes, then check the status in Company Portal again."),
        "decrypting" => Row(BitLocker, HealthStatus.Warn, "Being turned off (decrypting)", "IT: BitLocker is being turned off on this device; check whether that is intended."),
        "off" => Row(BitLocker, HealthStatus.Warn, "Off for the Windows drive", "IT: turn on BitLocker for the Windows drive; it is set by your organization's policy."),
        _ => Row(BitLocker, HealthStatus.Warn, "Unknown: could not be read", "IT: check BitLocker on this device; Windows did not report its state."),
    };

    private static HealthResult FirewallRow(ComplianceReadout c)
    {
        var profiles = new[] { ("domain", c.FirewallDomain), ("private", c.FirewallPrivate), ("public", c.FirewallPublic) };
        var off = profiles.Where(p => p.Item2 == "off").Select(p => p.Item1).ToList();
        if (off.Count > 0)
        {
            return Row(Firewall, HealthStatus.Warn, "Off for " + string.Join(", ", off) + " networks", "IT: turn Windows Firewall back on for these networks; it is set by your organization's policy.");
        }
        if (profiles.Any(p => p.Item2 != "on"))
        {
            return Row(Firewall, HealthStatus.Warn, "Unknown: could not be read", "IT: check Windows Firewall on this device; Windows did not report all its networks.");
        }
        return Row(Firewall, HealthStatus.Pass, "On for every network", "Windows Firewall is on for domain, private and public networks.");
    }

    /// <summary>
    /// Secure Boot and the TPM in one row: both are set in the device's firmware, and both are for IT
    /// when they are not on. One step: the one of the part that is not OK, or one for both.
    /// </summary>
    private static HealthResult SecureBootTpmRow(ComplianceReadout c)
    {
        var (bootText, bootStep) = c.SecureBoot switch
        {
            "on" => ("Secure Boot on", null),
            "off" => ("Secure Boot off", "IT: turn on Secure Boot in the device's firmware (UEFI) settings."),
            "unsupported" => ("Secure Boot not supported", "IT: this device cannot use Secure Boot, so a policy that requires it cannot be met."),
            _ => ("Secure Boot unknown", "IT: check Secure Boot in the device's firmware (UEFI) settings."),
        };
        var version = c.TpmVersion == null ? "" : " (" + c.TpmVersion + ")";
        var (tpmText, tpmStep) = c.Tpm switch
        {
            "ready" => ("TPM ready" + version, null),
            "not-ready" => ("TPM not ready" + version, "IT: prepare the TPM in the device's firmware settings or in TPM management."),
            "missing" => ("no TPM found", "IT: Windows finds no TPM on this device, so a policy that requires one cannot be met."),
            _ => ("TPM unknown", "IT: check the TPM in the device's firmware settings or in TPM management."),
        };
        var detail = bootText + "; " + tpmText;
        if (bootStep == null && tpmStep == null) { return Row(SecureBootTpm, HealthStatus.Pass, detail, "Secure Boot lets only trusted software start the device; the TPM keeps its keys."); }
        var step = bootStep != null && tpmStep != null
            ? "IT: check Secure Boot and the TPM in the device's firmware (UEFI) settings; a policy that requires them cannot be met until both are on."
            : bootStep ?? tpmStep;
        return Row(SecureBootTpm, HealthStatus.Warn, detail, step);
    }

    /// <summary>
    /// The Windows version and build. The minimum an organization requires is not known on the device, so
    /// a known version is never a problem here; the hint says where a version requirement shows.
    /// </summary>
    private static HealthResult WindowsRow(ComplianceReadout c)
    {
        if (string.IsNullOrWhiteSpace(c.OsBuild))
        {
            return Row(WindowsVersion, HealthStatus.Warn, "Unknown: could not be read", "IT: check the Windows version of this device; Windows did not report its build.");
        }
        var detail = (string.IsNullOrWhiteSpace(c.OsVersion) ? "" : c.OsVersion + ", ") + "build " + c.OsBuild;
        return Row(WindowsVersion, HealthStatus.Pass, detail,
            "Your organization may require a minimum Windows version, which this device cannot see; Company Portal names it when it is the reason, and Windows Update installs newer builds.");
    }

    /// <summary>The Defender row, with one step: its own fix (Update Defender definitions) or IT's.</summary>
    private static HealthResult DefenderRow(HealthResult source)
    {
        var row = Row(Defender, source.Status, source.Detail, "", source.Fix);
        row.ServiceUnavailable = source.ServiceUnavailable;
        if (source.Status == HealthStatus.Pass) { row.Hint = "Microsoft Defender Antivirus protects this device and its security intelligence is up to date, or another antivirus is in charge."; }
        else if (source.ServiceUnavailable) { row.Hint = "IT: the " + ProductInfo.DisplayName + " service is not available, so Defender could not be read."; }
        else if (source.Fix != HealthFix.None) { row.Hint = "Old security intelligence can make the device fail a policy; updating it takes a few minutes."; }
        else if ((source.Detail ?? "").StartsWith("Unknown: still", StringComparison.Ordinal)) { row.Hint = "Use Check this PC again on Home in a moment."; }
        else if ((source.Detail ?? "").IndexOf("real-time protection off", StringComparison.OrdinalIgnoreCase) >= 0) { row.Hint = "IT: real-time protection is off; it is a setting of your organization."; }
        else { row.Hint = "IT: check which antivirus this device should use; Microsoft Defender Antivirus is off, missing or could not be read."; }
        return row;
    }

    /// <summary>The pending restart (a signal; the user restarts when the organization allows it).</summary>
    private static HealthResult RestartRow(HealthResult source)
    {
        var row = Row(Restart, source.Status, source.Detail, source.Status == HealthStatus.Pass ? "No restart is waiting." : "Restart when your organization allows it, then check the status in Company Portal again.");
        row.ServiceUnavailable = source.ServiceUnavailable;
        return row;
    }
}
