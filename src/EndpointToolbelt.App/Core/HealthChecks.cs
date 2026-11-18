using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EndpointToolbelt.App.Core;

public enum HealthStatus
{
    Pass,
    Warn,
    Fail,
}

/// <summary>One row of the device health view, with the sentence behind it.</summary>
public sealed class HealthResult
{
    public string Name { get; set; }
    public HealthStatus Status { get; set; }
    public string Detail { get; set; }
    public string Hint { get; set; }

    /// <summary>The detail as the device report shows it (with technical sources); the screen detail when not set.</summary>
    public string ReportDetail
    {
        get => _reportDetail ?? Detail;
        set => _reportDetail = value;
    }

    private string _reportDetail;

    /// <summary>The status in words (colour is never the only signal); "Info" for an OK row that only informs.</summary>
    public string StatusWord => Status switch { HealthStatus.Pass => Info ? "Info" : "OK", HealthStatus.Warn => Words.Warning, _ => "Problem" };

    /// <summary>
    /// An OK row that says something worth knowing, not a fault: a setting that cannot apply on
    /// this device, or a clock its virtual machine host keeps. It counts as OK (no fix, no step, not
    /// a warning), and shows the word Info in a neutral tone.
    /// </summary>
    public bool Info { get; set; }

    /// <summary>What only IT support can do about this row, for its card in Copy details for IT and the report; empty for none.</summary>
    public string Support { get; set; } = "";

    /// <summary>
    /// The row's own title and line on Home's first screen, in plain words, when its row's
    /// usual words do not fit this state (a device that is not joined is no sign-in problem); empty for those.
    /// </summary>
    public string HomeTitle { get; set; } = "";
    public string HomeLine { get; set; } = "";

    /// <summary>
    /// The row could not be checked because the service is not available. Its detail says so and
    /// the status line says what to do, so its hint is not repeated under every such row.
    /// </summary>
    public bool ServiceUnavailable { get; set; }

    /// <summary>
    /// The hint shown as a line under the row: only for a check that is not OK, and only its first
    /// sentence, what is wrong. With a fix link the link says what to do; without one a hint
    /// that sends the user to IT support says so in a short sentence. The whole hint is the
    /// tooltip and help text. An OK row keeps its hint as tooltip and help text only.
    /// </summary>
    public string HintBelow
    {
        get
        {
            if (Status == HealthStatus.Pass || ServiceUnavailable || string.IsNullOrEmpty(Hint)) { return ""; }
            var first = FirstSentence(Hint);
            if (Fix == HealthFix.None && first.Length < Hint.Length && Hint.IndexOf("IT support", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return first + " Contact your IT support.";
            }
            return first;
        }
    }

    /// <summary>The whole hint as a tooltip, where it is not shown below the row in full already.</summary>
    public string HintAsToolTip => !string.IsNullOrEmpty(Hint) && HintBelow != Hint ? Hint : null;

    internal static string FirstSentence(string text)
    {
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? text : text.Substring(0, end + 1);
    }

    /// <summary>
    /// The action that fixes what this row reports, offered as a link under a row that is not OK
    ///. None for an OK row, a row the service could not answer, or a problem no action fixes.
    /// </summary>
    public HealthFix Fix => Status == HealthStatus.Pass || ServiceUnavailable ? HealthFix.None : FixOverride ?? HealthChecks.FixFor(Name);

    /// <summary>Set by a row whose fix depends on why it is not OK (Defender, Clock); null: the row's usual fix.</summary>
    public HealthFix? FixOverride { get; set; }

    /// <summary>The IME is starting: the detail is a sentence of its own ("The app installer (IME) started again; ..."), not a time.</summary>
    public bool Starting { get; set; }

    /// <summary>
    /// The detail is a sentence of its own ("Last attempt failed (...), 16:22"), so a combined
    /// row shows it without its part's label in front.
    /// </summary>
    public bool OwnSentence { get; set; }

    /// <summary>
    /// The row is about the Intune Management Extension not being installed. No action of this
    /// app helps then (Intune installs it with the first Win32 app, script or remediation), so it
    /// never offers Retry work apps and settings: no fix link, no step, nothing in a ticket.
    /// </summary>
    public bool ImeNotInstalled { get; set; }
}

/// <summary>
/// The fix a health row links to. Each value is an existing button of the app; the link starts
/// the same action as the button, with the same confirmation.
/// </summary>
public enum HealthFix
{
    None,
    RetryAppsAndPolicies,
    ConnectivityTest,
    SyncClock,
    UpdateDefender,
    FreeDiskSpace,
    RefreshSignIn,
}

/// <summary>What dsregcmd /status says in the user's own context.</summary>
public sealed class DsregInfo
{
    public string AzureAdJoined { get; set; } = "";
    public string AzureAdPrt { get; set; } = "";
    public string TenantName { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string DeviceId { get; set; } = "";

    /// <summary>"YES" when the device is joined to an on-premises Active Directory domain; with AzureAdJoined that is hybrid joined.</summary>
    public string DomainJoined { get; set; } = "";

    /// <summary>"YES" when the signed-in user's work account registered this device (Microsoft Entra registered: a personal or BYOD device).</summary>
    public string WorkplaceJoined { get; set; } = "";

    /// <summary>The tenant a registered (BYOD) device's work account belongs to, when dsregcmd shows it (WorkplaceTenantName).</summary>
    public string WorkplaceTenantName { get; set; } = "";

    /// <summary>"YES" when a Windows Hello key is set for the signed-in user (NgcSet, User State).</summary>
    public string NgcSet { get; set; } = "";

    /// <summary>Whether dsregcmd showed a Windows Hello key ID (NgcKeyId). Only its presence is kept, never the ID.</summary>
    public bool NgcKeyPresent { get; set; }

    /// <summary>The "Ngc Prerequisite Check" fields: the Windows Hello for Business policy is on, the device meets the hardware requirement, the session is not remote, and the overall result.</summary>
    public string NgcPolicyEnabled { get; set; } = "";
    public string NgcDeviceEligible { get; set; } = "";
    public string NgcSessionIsNotRemote { get; set; } = "";
    public string NgcPreReqResult { get; set; } = "";

    /// <summary>From "Diagnostic Data", shown only when a domain-joined device could not complete the hybrid join: the phase it stopped in and its client error code.</summary>
    public string JoinErrorPhase { get; set; } = "";
    public string JoinClientErrorCode { get; set; } = "";

    /// <summary>When the sign-in token was last renewed (UTC), from AzureAdPrtUpdateTime; null when not shown.</summary>
    public DateTime? PrtUpdatedUtc { get; set; }

    /// <summary>When the sign-in token expires (UTC), from AzureAdPrtExpiryTime; a renewal moves it forward.</summary>
    public DateTime? PrtExpiresUtc { get; set; }
    public string[] Lines { get; set; } = Array.Empty<string>();

    private static readonly string[] DsregTimeFormats = { "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss" };

    /// <summary>A dsregcmd time such as "2026-10-16 10:12:34.000 UTC"; null for anything else.</summary>
    public static DateTime? ParseDsregUtc(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return null; }
        var text = value.Trim();
        if (text.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase)) { text = text.Substring(0, text.Length - 4); }
        return DateTime.TryParseExact(text, DsregTimeFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc) ? utc : null;
    }

    public static DsregInfo Parse(IEnumerable<string> lines)
    {
        var info = new DsregInfo { Lines = lines?.ToArray() ?? Array.Empty<string>() };
        foreach (var line in info.Lines)
        {
            var at = line.IndexOf(':');
            if (at < 0) { continue; }
            var key = line.Substring(0, at).Trim();
            var value = line.Substring(at + 1).Trim();
            switch (key)
            {
                case "AzureAdJoined": if (info.AzureAdJoined.Length == 0) { info.AzureAdJoined = value; } break;
                case "AzureAdPrt": if (info.AzureAdPrt.Length == 0) { info.AzureAdPrt = value; } break;
                case "TenantName": if (info.TenantName.Length == 0) { info.TenantName = value; } break;
                case "TenantId": if (info.TenantId.Length == 0) { info.TenantId = value; } break;
                case "DeviceId": if (info.DeviceId.Length == 0) { info.DeviceId = value; } break;
                case "DomainJoined": if (info.DomainJoined.Length == 0) { info.DomainJoined = value; } break;
                case "WorkplaceJoined": if (info.WorkplaceJoined.Length == 0) { info.WorkplaceJoined = value; } break;
                // The join type's details and Windows Hello's state; the key ID only as present or not.
                case "WorkplaceTenantName": if (info.WorkplaceTenantName.Length == 0) { info.WorkplaceTenantName = value; } break;
                case "NgcSet": if (info.NgcSet.Length == 0) { info.NgcSet = value; } break;
                case "NgcKeyId": info.NgcKeyPresent |= value.Length > 0; break;
                case "PolicyEnabled": if (info.NgcPolicyEnabled.Length == 0) { info.NgcPolicyEnabled = value; } break;
                case "DeviceEligible": if (info.NgcDeviceEligible.Length == 0) { info.NgcDeviceEligible = value; } break;
                case "SessionIsNotRemote": if (info.NgcSessionIsNotRemote.Length == 0) { info.NgcSessionIsNotRemote = value; } break;
                case "PreReqResult": if (info.NgcPreReqResult.Length == 0) { info.NgcPreReqResult = value; } break;
                case "Error Phase": if (info.JoinErrorPhase.Length == 0) { info.JoinErrorPhase = value; } break;
                case "Client ErrorCode": if (info.JoinClientErrorCode.Length == 0) { info.JoinClientErrorCode = value; } break;
                case "AzureAdPrtUpdateTime": info.PrtUpdatedUtc ??= ParseDsregUtc(value); break;
                case "AzureAdPrtExpiryTime": info.PrtExpiresUtc ??= ParseDsregUtc(value); break;
            }
        }
        return info;
    }
}

/// <summary>The facts the health checks read. The real source is <see cref="SystemHealthSource"/>.</summary>
public interface IHealthSource
{
    DateTime UtcNow { get; }

    /// <summary>The IME service status ("Running", "Stopped", ...), or null when it is not installed.</summary>
    string ImeServiceStatus();

    DsregInfo Dsreg();

    /// <summary>Expiry of the newest Intune MDM device certificate (UTC), null when there is none.</summary>
    DateTime? MdmCertificateExpiryUtc();

    bool CanReach(string host, int port, int timeoutMs);

    /// <summary>Free space on the system drive in GB.</summary>
    double FreeSystemDriveGB();

    /// <summary>Windows servicing or Windows Update waits for a restart.</summary>
    bool ServicingRestartPending();
}

/// <summary>When the device last started, for a source that knows it (the IME check-in while the device starts).</summary>
public interface IBootTime
{
    /// <summary>The device's last start (UTC), or null when unknown.</summary>
    DateTime? BootUtc();
}

/// <summary>The Windows Hello for Business Group Policy, for a source that can read it.</summary>
public interface IHelloPolicySource
{
    /// <summary>
    /// "Use Windows Hello for Business" as Group Policy sets it (Software\Policies\Microsoft\PassportForWork,
    /// value Enabled, for the device or the user): true when on, false when off, null when not set.
    /// </summary>
    bool? WindowsHelloPolicy();
}

/// <summary>
/// The health checks: nine device checks with their thresholds and wording, the IME
/// check-in, and the device settings the service reads.
/// </summary>
public static class HealthChecks
{
    // The names on screen are plain words; the device report keeps the technical names (see
    // ReportName), which IT support searches for.
    public const string ImeService = "App installer (IME)";
    public const string Enrollment = "Intune enrollment";
    public const string LastSync = "Last Intune sync";
    public const string ImeCheckIn = "Last IME check-in";
    public const string EntraJoined = "Entra joined";
    public const string Prt = "Work account sign-in";
    public const string MdmCertificate = "Intune certificate";
    public const string Connectivity = "Connection to Intune";
    public const string FreeSpace = "Free disk space";
    public const string PendingRestart = "Pending restart";
    public const string Compliance = "Compliance checks";
    public const string Profiles = "Configuration profiles";
    public const string Clock = "Clock";
    public const string Defender = "Microsoft Defender";

    /// <summary>Windows Hello for this user (a PIN), read-only.</summary>
    public const string WindowsHello = "Windows Hello";

    /// <summary>The technical name of a check, for the device report; a name it does not know stays as it is.</summary>
    public static string ReportName(string name) => name switch
    {
        ImeService => "IME service",
        Enrollment => "MDM enrollment",
        LastSync => "Last MDM sync",
        ImeCheckIn => "Last IME check-in",
        Prt => "Sign-in token (PRT)",
        MdmCertificate => "MDM certificate",
        Connectivity => "Intune connectivity",
        Compliance => "Compliance settings (local)",
        Profiles => "MDM policy errors",
        Clock => "Time sync",
        Defender => "Defender Antivirus",
        EntraJoined => "Join type (dsregcmd)",
        WindowsHello => "Windows Hello for Business (NGC)",
        _ => name,
    };

    public const int CertificateProblemDays = 30;
    public const int CertificateCheckDays = 60;

    /// <summary>
    /// Runs every check. <paramref name="status"/> is the service's status, or null when the
    /// service could not be reached; the checks that come from it then say so.
    /// </summary>
    public static List<HealthResult> Run(IHealthSource source, Protocol.StatusInfo status, out DsregInfo dsreg)
    {
        var results = new List<HealthResult>();
        var now = source.UtcNow;

        // 1. IME service
        string ime;
        try { ime = source.ImeServiceStatus(); } catch (Exception) { ime = "Unknown"; }
        if (ime == null) { results.Add(ImeMissing(Make(ImeService, HealthStatus.Fail, "Not installed"))); }
        else if (ime == "Running") { results.Add(Make(ImeService, HealthStatus.Pass, "Running")); }
        else { results.Add(Make(ImeService, HealthStatus.Fail, "Status: " + ime)); }

        // 2. MDM enrollment, as the service (SYSTEM) sees it.
        if (status == null) { results.Add(Unavailable(Enrollment)); }
        else if (status.Enrolled == true) { results.Add(Make(Enrollment, HealthStatus.Pass, "Enrolled in Intune")); }
        else if (status.Enrolled == false) { results.Add(Make(Enrollment, HealthStatus.Fail, "No Intune enrollment found")); }
        else { results.Add(Make(Enrollment, HealthStatus.Warn, "More than one enrollment, or it could not be read")); }

        // 3. Last MDM sync, from the MDM session log (read by the service).
        var lastSync = status == null ? null : TimeText.ParseUtc(status.LastSyncUtc);
        if (status == null) { results.Add(Unavailable(LastSync)); }
        else if (lastSync == null) { results.Add(Make(LastSync, HealthStatus.Warn, "Unknown")); }
        else
        {
            var age = now - lastSync.Value;
            var state = age.TotalHours <= 24 ? HealthStatus.Pass : age.TotalDays <= 7 ? HealthStatus.Warn : HealthStatus.Fail;
            // The log it came from is for IT support: in the device report, not on screen.
            var from = string.IsNullOrWhiteSpace(status.LastSyncSource) ? "MDM session log" : status.LastSyncSource;
            var row = Make(LastSync, state, string.Format(CultureInfo.InvariantCulture, "{0} ({1})", TimeText.InLocalTime(lastSync.Value), TimeText.Age(age)));
            row.ReportDetail = string.Format(CultureInfo.InvariantCulture, "{0} ({1}, {2})", TimeText.InLocalTime(lastSync.Value), TimeText.Age(age), from);
            results.Add(row);
        }

        // 3b. Last IME check-in, from the IME's own log (read by the service).
        DateTime? boot;
        try { boot = source is IBootTime bootTime ? bootTime.BootUtc() : null; } catch (Exception) { boot = null; }
        // Without the IME there is no check-in to retry either.
        var checkIn = ImeCheckInRow(status, now, boot);
        results.Add(ime == null && checkIn.Status != HealthStatus.Pass && !checkIn.ServiceUnavailable ? ImeMissing(checkIn) : checkIn);

        // 4 and 5. The join type and the sign-in token, in the user's own context (each join type
        // with its own expected checks). 5b. Windows Hello for this user.
        try { dsreg = source.Dsreg() ?? new DsregInfo(); } catch (Exception) { dsreg = new DsregInfo(); }
        results.Add(WorkAccountRows.Join(dsreg));
        results.Add(WorkAccountRows.Prt(dsreg));
        bool? helloPolicy;
        try { helloPolicy = source is IHelloPolicySource policy ? policy.WindowsHelloPolicy() : null; } catch (Exception) { helloPolicy = null; }
        results.Add(WorkAccountRows.Hello(dsreg, status?.Readouts?.Machine, helloPolicy));

        // 6. MDM certificate
        try
        {
            var expiry = source.MdmCertificateExpiryUtc();
            if (expiry == null) { results.Add(Make(MdmCertificate, HealthStatus.Fail, "Not found")); }
            else
            {
                // The certificate renews itself during syncs well before it expires, so one that
                // gets close to expiry means renewal is failing: expired or under 30 days is a
                // problem, under 60 days needs attention. Whole days, so an expired certificate is
                // a problem from its first minute.
                var days = (int)Math.Floor((expiry.Value - now).TotalDays);
                var state = expiry.Value <= now || days < CertificateProblemDays ? HealthStatus.Fail : days < CertificateCheckDays ? HealthStatus.Warn : HealthStatus.Pass;
                results.Add(Make(MdmCertificate, state, string.Format(CultureInfo.InvariantCulture, "Expires {0} ({1})", TimeText.InLocalTime(expiry.Value, "yyyy-MM-dd"), Words.Count(days, "day"))));
            }
        }
        catch (Exception)
        {
            results.Add(Make(MdmCertificate, HealthStatus.Warn, "Unknown"));
        }

        // 7. The device management connection: one quick check with the same answers as Check
        // Microsoft connections: directly, through the user's proxy when that failed (on a
        // network that only allows the proxy, the direct route fails while Intune works) and through
        // the machine proxy when there is one.
        TargetResult quick;
        try { quick = ConnectivityTest.Quick(source); }
        catch (Exception) { quick = null; }
        if (quick == null) { results.Add(Make(Connectivity, HealthStatus.Fail, "Not reachable: the connection failed. Check the network or VPN")); }
        else
        {
            var (connection, words) = ConnectivityTest.HomeRow(quick);
            results.Add(Make(Connectivity, connection, words));
        }

        // 8. Free disk space
        try
        {
            var free = Math.Round(source.FreeSystemDriveGB(), 1);
            var state = free < 5 ? HealthStatus.Fail : free < 15 ? HealthStatus.Warn : HealthStatus.Pass;
            results.Add(Make(FreeSpace, state, string.Format(CultureInfo.InvariantCulture, "{0} GB free on {1}", free, SystemDrive())));
        }
        catch (Exception)
        {
            results.Add(Make(FreeSpace, HealthStatus.Warn, "Unknown"));
        }

        // 9. Pending restart: Windows servicing, or this product's own marker (a repair that
        // needs a restart the servicing keys do not show).
        bool servicing;
        try { servicing = source.ServicingRestartPending(); } catch (Exception) { servicing = false; }
        var markerUtc = status == null ? null : TimeText.ParseUtc(status.RestartRequiredSinceUtc);
        if (servicing) { results.Add(Make(PendingRestart, HealthStatus.Warn, "Restart required before troubleshooting (Windows servicing)")); }
        else if (markerUtc != null)
        {
            var by = string.IsNullOrWhiteSpace(status.RestartRequiredBy) ? "an " + ProductInfo.DisplayName + " repair" : status.RestartRequiredBy;
            results.Add(Make(PendingRestart, HealthStatus.Warn, string.Format(CultureInfo.InvariantCulture, "Restart required after {0}, {1}", by, TimeText.InLocalTime(markerUtc.Value, "HH:mm"))));
        }
        else { results.Add(Make(PendingRestart, HealthStatus.Pass, "No restart pending")); }

        // 10-13. Device settings the service reads as SYSTEM.
        var readouts = status?.Readouts;
        results.Add(DeviceSettingRows.Compliance(status, readouts?.Compliance));
        results.Add(DeviceSettingRows.Profiles(status, readouts?.Profiles));
        results.Add(DeviceSettingRows.Clock(status, readouts?.Clock));
        results.Add(DeviceSettingRows.Defender(status, readouts?.Defender, now));

        return results;
    }

    /// <summary>After the first check-in the IME announced at its start, it is given this long before its age counts again.</summary>
    public static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(5);

    /// <summary>A device that started this recently, whose IME log shows nothing since, is still starting.</summary>
    public static readonly TimeSpan BootWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The IME checks in with the Intune service on its own schedule (about every hour), apart
    /// from the MDM sync. Same age thresholds as the sync. A failed check-in newer than the last
    /// success needs attention whatever its age; no evidence is never OK. While the IME starts
    /// (after its start line, until a few minutes after the first check-in it announced), or while a
    /// device that just started has nothing in the IME log yet, the row is OK and says so: an old
    /// check-in or a failure from before the start is no problem then. A failure the IME wrote
    /// after its announced check-in still counts.
    /// </summary>
    private static HealthResult ImeCheckInRow(Protocol.StatusInfo status, DateTime now, DateTime? bootUtc = null)
    {
        if (status == null) { return Unavailable(ImeCheckIn); }
        var last = TimeText.ParseUtc(status.LastImeCheckInUtc);
        var errorAt = TimeText.ParseUtc(status.ImeCheckInErrorUtc);
        var lastText = last == null ? null : string.Format(CultureInfo.InvariantCulture, "{0} ({1})", TimeText.InLocalTime(last.Value), TimeText.Age(now - last.Value));
        if (StartingText(status, now, bootUtc, last, errorAt) is string starting)
        {
            var row = Make(ImeCheckIn, HealthStatus.Pass, starting);
            row.Starting = true;
            row.ReportDetail = starting + (lastText == null ? "" : "; last check-in " + lastText) + " (IME log)";
            return row;
        }
        if (errorAt != null)
        {
            // One sentence that reads once, also inside the combined row ("Last attempt failed
            // (no error code in the log), 16:22. Last check-in 15:34 (48 min ago)").
            var failed = string.Format(CultureInfo.InvariantCulture, "Last attempt failed ({0}), {1}", ImeCheckInReason(status.ImeCheckInError), TimeText.InLocalTime(errorAt.Value));
            var row = Make(ImeCheckIn, last != null && (now - last.Value).TotalDays > 7 ? HealthStatus.Fail : HealthStatus.Warn,
                lastText == null ? failed : failed + ". Last check-in " + lastText);
            row.OwnSentence = true;
            row.Hint = ImeCheckInFailedHint;
            row.ReportDetail = row.Detail + string.Format(CultureInfo.InvariantCulture, " (IME log, error code '{0}')", status.ImeCheckInError ?? "");
            return row;
        }
        if (last == null)
        {
            var why = status.ImeCheckInUnavailable switch
            {
                "missing" => "no IME log on this device",
                "unreadable" => "the IME log could not be read",
                _ => "no check-in in the IME log of the last 30 days",
            };
            var unknown = Make(ImeCheckIn, HealthStatus.Warn, "Last check-in unknown: " + why);
            unknown.OwnSentence = true;
            unknown.Hint = ImeCheckInUnknownHint;
            unknown.ReportDetail = "Unknown: " + why;
            return unknown;
        }
        var age = now - last.Value;
        var state = age.TotalHours <= 24 ? HealthStatus.Pass : age.TotalDays <= 7 ? HealthStatus.Warn : HealthStatus.Fail;
        var ok = Make(ImeCheckIn, state, lastText);
        ok.ReportDetail = lastText + " (IME log)";
        return ok;
    }

    /// <summary>
    /// "The app installer (IME) started again; first check-in in about 4 min" while the IME starts, or null
    /// when it does not: after its start line and before a check-in since, until
    /// <see cref="StartGrace"/> after the check-in it announced, unless a failure came after that
    /// check-in was due; or, when the log shows no start, a device that started less than
    /// <see cref="BootWindow"/> ago whose IME log has nothing since.
    /// </summary>
    internal static string StartingText(Protocol.StatusInfo status, DateTime now, DateTime? bootUtc, DateTime? last, DateTime? errorAt)
    {
        var start = TimeText.ParseUtc(status.ImeStartUtc);
        var due = TimeText.ParseUtc(status.ImeFirstCheckInDueUtc);
        if (start != null && due != null && start <= now && (last == null || last < start) && now < due + StartGrace && (errorAt == null || errorAt < due))
        {
            var left = due.Value - now;
            return left.TotalSeconds <= 30
                ? "The app installer (IME) started again; first check-in due now"
                : string.Format(CultureInfo.InvariantCulture, "The app installer (IME) started again; first check-in in about {0} min", Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)));
        }
        if (bootUtc != null && bootUtc <= now && now - bootUtc.Value < BootWindow
            && (start == null || start < bootUtc) && (last == null || last < bootUtc) && (errorAt == null || errorAt < bootUtc))
        {
            return "Starting after the device restarted; first check-in in a few minutes";
        }
        return null;
    }

    /// <summary>
    /// The IME row's hint per state that is not OK: its last check-in is older than a day (the
    /// row's default), its last attempt failed, or the log does not show a check-in. After a restart the
    /// IME waits a delay of its own before it checks in, so no hint promises it at once.
    /// </summary>
    public const string ImeCheckInTooOldHint =
        "The app installer (IME) has not checked in with Intune for more than a day, so it may not have fetched its apps and scripts. Retry work apps and settings restarts it, which makes it check in within a few minutes.";

    public const string ImeCheckInFailedHint =
        "The app installer (IME) tried to check in with Intune and that attempt failed. Retry work apps and settings restarts it, so it tries again within a few minutes. If it keeps failing for the same reason, contact your IT support.";

    public const string ImeCheckInUnknownHint =
        "The IME log does not show when the app installer last checked in. Retry work apps and settings restarts it, which makes it check in within a few minutes. If this stays unknown, contact your IT support.";

    /// <summary>The error of a failed check-in in words, with the code IT support searches for.</summary>
    public static string ImeCheckInErrorText(string code) => code switch
    {
        "401" => "sign-in refused (401)",
        "403" => "access refused (403)",
        "proxy" => "proxy error",
        "token" => "no sign-in token",
        null or "" => "no error code in the log",
        _ => "error " + code,
    };

    /// <summary>The same reason for the parentheses of "Last attempt failed (...)": "sign-in refused, 401".</summary>
    public static string ImeCheckInReason(string code) => code switch
    {
        "401" => "sign-in refused, 401",
        "403" => "access refused, 403",
        _ => ImeCheckInErrorText(code),
    };

    /// <summary>The drive Windows is on, as "C:".</summary>
    private static string SystemDrive()
    {
        var root = System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "";
        return root.Length > 0 ? root.TrimEnd('\\') : "the system drive";
    }

    /// <summary>
    /// The status line after a health check. Its next step is the first problem row's own step
    /// (<see cref="NextStep"/>): Retry work apps and settings only for the Intune and IME rows it can help,
    /// another row's own fix or step, or none ("See the list above.").
    /// </summary>
    public static (string Text, Tone Tone) Summary(IEnumerable<HealthResult> results)
    {
        var list = results.ToList();
        var fails = list.Count(r => r.Status == HealthStatus.Fail);
        var warns = list.Count(r => r.Status == HealthStatus.Warn);
        if (fails == 0 && warns == 0) { return ("Device looks healthy.", Tone.Success); }
        if (fails == 0) { return (Words.Count(warns, "check") + (warns == 1 ? " needs" : " need") + " attention. See the list above.", Tone.Warning); }
        var first = list.First(r => r.Status == HealthStatus.Fail);
        var step = NextStep(first);
        return (Words.Count(fails, "problem") + " found. " + (step.Length == 0 ? "See the list above." : first.Name + ": " + LowerFirst(step)), Tone.Error);
    }

    private static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text.Substring(1);

    /// <summary>
    /// A health row that is not OK as a result card for Copy details for IT: its name and detail, what it
    /// means, and its own next step (<see cref="NextStep"/>).
    /// </summary>
    public static ResultCard Card(HealthResult row)
    {
        var severity = row.Status switch { HealthStatus.Fail => FindingSeverity.Error, HealthStatus.Warn => FindingSeverity.Warning, _ => FindingSeverity.Ok };
        var title = string.IsNullOrWhiteSpace(row.Detail) ? row.Name + ": " + row.StatusWord : row.Name + ": " + row.Detail;
        // A pending restart is said to IT as a fact; the user is never asked to restart outright.
        var support = row.Status != HealthStatus.Pass && row.Name == PendingRestart && !row.ServiceUnavailable ? "A restart is waiting on this device."
            // A row's own line for IT (the join type, Windows Hello).
            : row.Status != HealthStatus.Pass ? row.Support ?? "" : "";
        var next = NextStep(row);
        var meaning = row.Status == HealthStatus.Pass ? "" : HealthResult.FirstSentence(row.Hint ?? "");
        // A row whose hint is its step (Windows Hello's PIN setup) says it once, as the next step.
        return ResultCard.From(severity, title, meaning == next ? "" : meaning, next, support);
    }

    /// <summary>The rows that are not OK as cards, problems first, in their order on screen.</summary>
    public static List<ResultCard> Cards(IEnumerable<HealthResult> rows)
        => (rows ?? Enumerable.Empty<HealthResult>()).Where(r => r != null && r.Status != HealthStatus.Pass)
            .OrderByDescending(r => r.Status).Select(Card).ToList();

    /// <summary>
    /// The one next step of a row that is not OK, as a short sentence: its fix where it has one
    /// ("Try Sync clock."; Retry work apps and settings only on the Intune and IME rows, see
    /// <see cref="FixFor"/>), else the row's own step (a pending restart: "Restart when your organization allows it"; a row only IT
    /// support can fix: contact them), else empty. Empty for an OK row.
    /// </summary>
    /// <summary>
    /// The honest text when the Intune Management Extension is not installed (never Retry apps &amp;
    /// policies: Intune installs it with the first Win32 app, script or remediation it assigns).
    /// </summary>
    public const string ImeNotInstalledHint =
        "The Intune Management Extension is not installed: this device has not received a Win32 app, script or remediation from Intune yet, and Intune installs it with the first one. No action here installs it.";

    /// <summary>The step of a row about the missing IME: nothing to do on this device.</summary>
    public const string ImeNotInstalledStep = "Nothing to do on this device: Intune installs the app installer with the first Win32 app, script or remediation it assigns.";

    /// <summary>Marks a row as about the missing IME: no fix link, the honest hint.</summary>
    internal static HealthResult ImeMissing(HealthResult row)
    {
        row.ImeNotInstalled = true;
        row.FixOverride = HealthFix.None;
        row.Hint = ImeNotInstalledHint;
        return row;
    }

    public static string NextStep(HealthResult row)
    {
        if (row == null || row.Status == HealthStatus.Pass) { return ""; }
        if (row.ServiceUnavailable) { return "Contact your IT support."; }
        // Never Retry work apps and settings while the IME is not installed.
        if (row.ImeNotInstalled) { return ImeNotInstalledStep; }
        var fix = row.Fix;
        if (fix != HealthFix.None) { return "Try " + Words.FixLink(fix) + "."; }
        // A signal, never a request to restart: some organizations do not let users restart certain computers.
        if (row.Name == PendingRestart) { return "Restart when your organization allows it, then try again."; }
        // Windows Hello required and not set up: the user's own step, read-only (this app never starts the setup).
        if (row.Name == WindowsHello && (row.Hint ?? "").StartsWith(WorkAccountRows.HelloStep, StringComparison.Ordinal)) { return WorkAccountRows.HelloStep; }
        return (row.Hint ?? "").IndexOf("IT support", StringComparison.OrdinalIgnoreCase) >= 0 ? "Contact your IT support." : "";
    }

    internal static HealthResult Make(string name, HealthStatus status, string detail)
        => new() { Name = name, Status = status, Detail = detail ?? "", Hint = Hint(name, status == HealthStatus.Pass) };

    /// <summary>A check the service answers, while the service cannot be reached: its own hint, not the check's.</summary>
    internal static HealthResult Unavailable(string name)
        => new() { Name = name, Status = HealthStatus.Warn, Detail = "Unknown: the " + ProductInfo.DisplayName + " service is not available", Hint = ServiceUnavailableHint, ServiceUnavailable = true };

    /// <summary>
    /// The hint of failed configuration profile settings. The row reads the MDM log of the last 7 days, so a
    /// failure stays listed after the setting applied or was unassigned; Retry asks for the current settings.
    /// </summary>
    public const string ProfilesFailedHint =
        "These configuration profile settings from Intune failed at least once in the last 7 days. A failure stays listed for 7 days, also when the setting applied later or is no longer assigned. Retry work apps and settings asks Intune for the current settings; if a setting fails again after that, create a support package for your IT support: it lists each setting and its error.";

    public const string ServiceUnavailableHint =
        "The " + ProductInfo.DisplayName + " service answers this check, and it could not be reached. Restart the device; if it repeats, contact your IT support.";

    /// <summary>
    /// The action a row that is not OK links to: the one its hint already names. Rows whose
    /// problem no action of this app fixes (enrollment, Entra join, a pending restart) have none.
    /// </summary>
    public static HealthFix FixFor(string name) => name switch
    {
        ImeService or LastSync or ImeCheckIn or MdmCertificate or Profiles => HealthFix.RetryAppsAndPolicies,
        Connectivity => HealthFix.ConnectivityTest,
        Clock => HealthFix.SyncClock,
        FreeSpace => HealthFix.FreeDiskSpace,
        Prt => HealthFix.RefreshSignIn,
        _ => HealthFix.None,
    };

    /// <summary>What a check means and, when it is not OK, what to do about it.</summary>
    public static string Hint(string name, bool ok) => name switch
    {
        ImeService => ok
            ? "The Intune Management Extension installs Win32 apps and runs remediations on this device. It is running."
            : "The Intune Management Extension is not running. Retry work apps and settings starts it. If it is not installed at all, this device has not received a Win32 app or script from Intune yet.",
        Enrollment => ok
            ? "The device is enrolled in Intune."
            : "The device has no single Intune enrollment. No action here fixes this: contact your IT support.",
        LastSync => ok
            ? "When the device last talked to Intune. Policies and settings arrive at a sync."
            : "The device has not synced with Intune recently. Retry work apps and settings requests a sync. If Intune is rate limiting the device, Windows retries on its own and the result says when.",
        ImeCheckIn => ok
            ? "When the app installer (IME) last fetched its apps and scripts from Intune. It does this on its own, about every hour."
            : ImeCheckInTooOldHint,
        EntraJoined => ok
            ? "The device is joined to Microsoft Entra ID."
            : "The device is not joined to Entra ID, which Intune and single sign-on need. No action here fixes this: contact your IT support.",
        Prt => ok
            ? "Your sign-in token for Microsoft 365 single sign-on is present."
            : "Your sign-in token for single sign-on is missing or unknown. Refresh work account sign-in asks Windows to renew it; locking and unlocking the device, or signing out and in, while connected to the network does the same. If it stays missing, contact your IT support.",
        WindowsHello => ok
            ? "Windows Hello is set up for your account, or your organization does not require it on this device."
            : WorkAccountRows.HelloStep + " If the PIN setup is not offered, contact your IT support.",
        MdmCertificate => ok
            ? "The certificate the device uses to talk to Intune is valid."
            : "The Intune certificate is missing, expires soon or has expired. The device uses it to talk to Intune, and it normally renews itself long before that while the device syncs, so renewal is not working: Retry work apps and settings requests a sync; if this stays, contact your IT support.",
        Connectivity => ok
            ? "This PC reaches manage.microsoft.com, the device management connection, on port 443. The service itself is not tested."
            : "This PC could not reach the device management connection, or could not confirm it. Check the network, VPN or proxy; Check Microsoft connections tests each Microsoft connection and its routes.",
        FreeSpace => ok
            ? "There is enough free space on the system drive for updates and app installs."
            : "Little free space on the system drive. Windows updates and app installs fail when they cannot stage. Free up disk space removes old temporary files and what Windows can safely clean up; also empty the Recycle Bin and clear Downloads and other large personal files.",
        PendingRestart => ok
            ? "No restart is waiting."
            : "A restart is waiting. Installs and updates do not continue until the device restarts. Restart when your organization allows it, then try again.",
        Compliance => ok
            ? "The local settings Intune compliance policies usually check are on: BitLocker, Secure Boot, TPM and the firewall. Whether this device is compliant is decided by Intune, with the policies your organization assigns."
            : "A setting Intune compliance policies usually check is off or could not be read. Intune decides compliance with your organization's policies; if Company Portal says this device is not compliant, contact your IT support with the device report. If your IT support cannot find this device's BitLocker recovery key in Entra ID, Back up recovery key (in For IT) sends it again.",
        Profiles => ok
            ? "No configuration profile setting from Intune failed on this device in the last 7 days."
            : ProfilesFailedHint,
        Clock => ok
            ? "The clock matches its time source. Sign-in and Intune need a correct clock."
            : "The clock is off, or could not be compared with its time source. Sign-in and Intune fail when it is off by more than 5 minutes. Sync clock corrects it; if it stays off, contact your IT support.",
        Defender => ok
            ? "Microsoft Defender Antivirus protects this device and its security intelligence is up to date, or another antivirus is in charge."
            : "Microsoft Defender Antivirus is off, has real-time protection off, or its security intelligence is out of date. Update Defender definitions gets the newest security intelligence now; real-time protection that is off is a setting of your organization: contact your IT support.",
        _ => "",
    };
}
