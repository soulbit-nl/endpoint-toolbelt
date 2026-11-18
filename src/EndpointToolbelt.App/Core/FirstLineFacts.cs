using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EndpointToolbelt.Platform;
using EndpointToolbelt.Protocol;

namespace EndpointToolbelt.App.Core;

/// <summary>How the device is joined, from the dsregcmd status the health checks already read.</summary>
public enum JoinType
{
    /// <summary>The join state could not be read.</summary>
    Unknown,

    NotJoined,

    /// <summary>Microsoft Entra joined (cloud only).</summary>
    EntraJoined,

    /// <summary>Microsoft Entra hybrid joined: Entra joined and joined to an on-premises domain.</summary>
    HybridJoined,

    /// <summary>Microsoft Entra registered: a personal or BYOD device the user's work account registered.</summary>
    Registered,

    /// <summary>Joined to an on-premises domain only, not known to Microsoft Entra.</summary>
    DomainOnly,
}

/// <summary>
/// The network in one line: whether a physical adapter is connected, its kind and whether it
/// has a usable IPv4 address. Never a name, an address or a Wi-Fi network name.
/// </summary>
public sealed class NetworkSummary
{
    /// <summary>The adapters could be read.</summary>
    public bool Known { get; set; }

    /// <summary>A physical, enabled adapter with a working driver has a cable or Wi-Fi connection.</summary>
    public bool Connected { get; set; }

    /// <summary>The kind of the connected adapter the line names (the one with a usable address first).</summary>
    public AdapterKind Kind { get; set; }

    /// <summary>That adapter has a usable IPv4 address (not none, not 169.254.x.x).</summary>
    public bool UsableAddress { get; set; }

    /// <summary>
    /// The summary from the adapters as Windows has them: physical ones only (a VPN, tunnel or
    /// Bluetooth adapter is not the connection). Null adapters (they could not be read) give an
    /// unknown summary.
    /// </summary>
    public static NetworkSummary Of(IEnumerable<NetAdapter> adapters)
    {
        if (adapters == null) { return new NetworkSummary(); }
        var connected = adapters.Where(a => a != null && a.Physical && a.Enabled && a.DriverOk && a.MediaConnected)
            .OrderByDescending(a => a.UsableIpv4).ThenBy(a => a.Kind).ThenBy(a => a.InterfaceIndex).ToList();
        if (connected.Count == 0) { return new NetworkSummary { Known = true }; }
        var first = connected[0];
        return new NetworkSummary { Known = true, Connected = true, Kind = first.Kind, UsableAddress = first.UsableIpv4 };
    }

    /// <summary>"connected yes, Wi-Fi, usable address yes", "connected no" or "unknown".</summary>
    public string Text()
    {
        if (!Known) { return "unknown (the network adapters could not be read)"; }
        if (!Connected) { return "connected no (no cable or Wi-Fi connection)"; }
        var kind = Kind switch { AdapterKind.Ethernet => "Ethernet", AdapterKind.WiFi => "Wi-Fi", _ => "other adapter" };
        return "connected yes, " + kind + ", usable address " + (UsableAddress ? "yes" : "no");
    }
}

/// <summary>
/// The first-line facts at the top of the device report (HealthReport.txt, also in the support
/// package) and of every Copy details for IT: the last restart, the join type, the MDM enrollment ID
/// (EntDMID) and a network summary. Read-only, from what the app already reads: the boot time, the
/// dsregcmd status of the health checks, the service's status and the network adapters.
/// </summary>
public sealed class FirstLineFacts
{
    /// <summary>When the device last started (UTC); null when unknown.</summary>
    public DateTime? BootUtc { get; set; }

    public JoinType Join { get; set; }

    /// <summary>The EntDMID of the one active Intune enrollment, as the service read it; null when it has none.</summary>
    public string MdmEnrollmentId { get; set; }

    /// <summary>The service's enrollment answer: true one enrollment, false none, null more than one or unknown.</summary>
    public bool? Enrolled { get; set; }

    /// <summary>The service answered the status (its facts are known).</summary>
    public bool ServiceAnswered { get; set; }

    public NetworkSummary Network { get; set; } = new();

    public static FirstLineFacts From(DateTime? bootUtc, DsregInfo dsreg, StatusInfo status, NetworkSummary network, bool restartPending = false) => new()
    {
        BootUtc = bootUtc,
        Join = JoinOf(dsreg),
        MdmEnrollmentId = status == null ? null : CleanId(status.MdmEnrollmentId),
        Enrolled = status?.Enrolled,
        ServiceAnswered = status != null,
        Network = network ?? new NetworkSummary(),
        RestartPending = restartPending,
    };

    /// <summary>The Pending restart row is not OK (Windows servicing or this product's restart marker).</summary>
    public bool RestartPending { get; set; }

    /// <summary>Only a GUID is shown as the enrollment ID; anything else counts as none.</summary>
    internal static string CleanId(string id) => Guid.TryParseExact((id ?? "").Trim(), "D", out var guid) ? guid.ToString("D") : null;

    /// <summary>
    /// The join type from dsregcmd's own fields: AzureAdJoined with DomainJoined is hybrid joined,
    /// AzureAdJoined alone is Entra joined, WorkplaceJoined is registered, DomainJoined alone is a
    /// domain without Entra; no field at all is unknown.
    /// </summary>
    public static JoinType JoinOf(DsregInfo dsreg)
    {
        if (dsreg == null) { return JoinType.Unknown; }
        static bool Yes(string value) => string.Equals((value ?? "").Trim(), "YES", StringComparison.OrdinalIgnoreCase);
        static bool Known(string value) => !string.IsNullOrWhiteSpace(value);
        if (!Known(dsreg.AzureAdJoined) && !Known(dsreg.DomainJoined) && !Known(dsreg.WorkplaceJoined)) { return JoinType.Unknown; }
        var entra = Yes(dsreg.AzureAdJoined);
        var domain = Yes(dsreg.DomainJoined);
        if (entra) { return domain ? JoinType.HybridJoined : JoinType.EntraJoined; }
        if (Yes(dsreg.WorkplaceJoined)) { return JoinType.Registered; }
        return domain ? JoinType.DomainOnly : JoinType.NotJoined;
    }

    public static string JoinWords(JoinType join) => join switch
    {
        JoinType.EntraJoined => "Microsoft Entra joined",
        JoinType.HybridJoined => "Microsoft Entra hybrid joined",
        JoinType.Registered => "Microsoft Entra registered (personal or BYOD device)",
        JoinType.DomainOnly => "domain joined only, not in Microsoft Entra",
        JoinType.NotJoined => "not joined",
        _ => "unknown (the join state could not be read)",
    };

    /// <summary>The EntDMID, or why there is none.</summary>
    public string MdmText()
    {
        if (!string.IsNullOrEmpty(MdmEnrollmentId)) { return MdmEnrollmentId; }
        if (!ServiceAnswered) { return "unknown (the " + ProductInfo.DisplayName + " service is not available)"; }
        return Enrolled switch
        {
            false => "none (not enrolled in Intune)",
            null => "unknown (more than one enrollment, or it could not be read)",
            _ => "unknown (the service did not report it)",
        };
    }

    /// <summary>
    /// "2026-11-01 08:12 UTC+01:00 (12 days ago)", or unknown. After a week or longer, or while a
    /// restart is waiting, the signal for IT follows ("; for IT: a restart is waiting"); it never asks
    /// the user to restart, since some organizations do not allow it.
    /// </summary>
    public string RestartText(DateTime nowUtc, TimeZoneInfo zone)
    {
        var signal = Uptime.ItSignal(BootUtc, nowUtc, RestartPending);
        var known = BootUtc is DateTime boot ? Moment(boot, zone) + " (" + Uptime.Ago(boot, nowUtc) + ")" : "unknown";
        return signal.Length == 0 ? known : known + "; for IT: " + signal;
    }

    /// <summary>A moment in the user's time with its offset ("2026-11-01 08:12 UTC+01:00").</summary>
    internal static string Moment(DateTime utc, TimeZoneInfo zone)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        zone ??= TimeZoneInfo.Local;
        try
        {
            var offset = zone.GetUtcOffset(utc);
            return new DateTimeOffset(utc).ToOffset(offset).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" + TimeText.Offset(offset);
        }
        catch (ArgumentException)
        {
            return utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        }
    }

    /// <summary>
    /// The facts block of Copy details for IT, each line "Label · text": the app version, the last restart,
    /// the join type, the MDM enrollment ID, the network and the health checks in one line.
    /// </summary>
    public List<string> TicketLines(string appVersion, IReadOnlyCollection<HealthResult> rows, DateTime nowUtc, TimeZoneInfo zone)
    {
        const string s = RunTicket.Separator;
        return new List<string>
        {
            "App version" + s + (string.IsNullOrEmpty(appVersion) ? "unknown" : appVersion),
            "Last restart" + s + RestartText(nowUtc, zone),
            "Join type" + s + JoinWords(Join),
            "MDM enrollment ID" + s + MdmText(),
            "Network" + s + (Network ?? new NetworkSummary()).Text(),
            "Health" + s + HealthView.OneLine(rows),
        };
    }

    /// <summary>The facts block at the top of the device report, in its "Label     : text" form.</summary>
    public List<string> ReportLines(DateTime nowUtc, TimeZoneInfo zone) => new()
    {
        "Restarted : " + RestartText(nowUtc, zone),
        "Join type : " + JoinWords(Join),
        "MDM ID    : " + MdmText() + " (MDM enrollment ID, EntDMID)",
        "Network   : " + (Network ?? new NetworkSummary()).Text(),
    };
}

/// <summary>
/// The uptime on Home and when a restart is advised. Windows counts it from its last start;
/// with Fast Startup a shutdown does not reset it, only a restart does, so the line says
/// "Restarted ... ago".
/// </summary>
public static class Uptime
{
    /// <summary>From this uptime on, Home and the facts say so: a week, as long as many update and app installs wait for a restart.</summary>
    public static readonly TimeSpan LongUptime = TimeSpan.FromDays(7);

    /// <summary>"just now", "1 minute ago", "3 hours ago", "12 days ago".</summary>
    public static string Ago(DateTime bootUtc, DateTime nowUtc)
    {
        var age = nowUtc - bootUtc;
        if (age < TimeSpan.FromMinutes(1)) { return "just now"; }
        if (age < TimeSpan.FromHours(1)) { return Words.Count((int)age.TotalMinutes, "minute") + " ago"; }
        if (age < TimeSpan.FromDays(1)) { return Words.Count((int)age.TotalHours, "hour") + " ago"; }
        return Words.Count((int)age.TotalDays, "day") + " ago";
    }

    /// <summary>Home's line: "Restarted 12 days ago"; empty when the start time is unknown.</summary>
    public static string Line(DateTime? bootUtc, DateTime nowUtc) => bootUtc is DateTime boot && boot <= nowUtc ? "Restarted " + Ago(boot, nowUtc) : "";

    /// <summary>
    /// A signal only (the app never restarts the device, and some organizations do not
    /// let users restart certain computers): "a restart is waiting" while a restart is pending, "no
    /// restart for over a week" from <see cref="LongUptime"/>, else empty. It never tells the user to restart.
    /// </summary>
    public static string Signal(DateTime? bootUtc, DateTime nowUtc, bool restartPending)
    {
        if (restartPending) { return "a restart is waiting"; }
        return bootUtc is DateTime boot && nowUtc - boot >= LongUptime ? "no restart for over a week" : "";
    }

    /// <summary>Home's line: "Restarted 12 days ago", with the signal when there is one ("Restarted 12 days ago · a restart is waiting").</summary>
    public static string HomeLine(DateTime? bootUtc, DateTime nowUtc, bool restartPending)
    {
        var line = Line(bootUtc, nowUtc);
        var signal = Signal(bootUtc, nowUtc, restartPending);
        return line.Length == 0 || signal.Length == 0 ? line : line + " · " + signal;
    }

    /// <summary>The signal as IT reads it in the facts: "a restart is waiting", "the device has not restarted for 12 days", or empty.</summary>
    public static string ItSignal(DateTime? bootUtc, DateTime nowUtc, bool restartPending)
    {
        if (restartPending) { return "a restart is waiting"; }
        return bootUtc is DateTime boot && nowUtc - boot >= LongUptime ? "the device has not restarted for " + Words.Count((int)(nowUtc - boot).TotalDays, "day") : "";
    }
}
