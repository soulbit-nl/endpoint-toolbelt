using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using Microsoft.Win32;

namespace EndpointToolbelt.App.Core;

/// <summary>The device facts at the top of the report.</summary>
public sealed class DeviceFacts
{
    public string ComputerName { get; set; } = "";
    public string Serial { get; set; } = "Unknown";
    public string Model { get; set; } = "Unknown";
    public string OperatingSystem { get; set; } = "Unknown";
    public string User { get; set; } = "";
    public string ImeVersion { get; set; } = "Unknown";

    /// <summary>Microsoft 365 Apps (Click-to-Run): version, platform and update channel; null when not installed.</summary>
    public OfficeInstall Office { get; set; }

    /// <summary>
    /// The time zone of the device's own clock, which the IME logs use; null when it could not be
    /// read. With time zone redirection on a session host it differs from the user's.
    /// </summary>
    public TimeZoneInfo DeviceZone { get; set; }

    /// <summary>Reads the facts on this device; a fact that cannot be read says so.</summary>
    public static DeviceFacts Read()
    {
        var facts = new DeviceFacts { ComputerName = Environment.MachineName, User = UserPrincipalName(), DeviceZone = SystemZone() };
        facts.Serial = Wmi("Win32_BIOS", "SerialNumber") ?? "Unknown";
        facts.Model = Wmi("Win32_ComputerSystem", "Model") ?? "Unknown";
        facts.OperatingSystem = OsText();
        try
        {
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft Intune Management Extension\Microsoft.Management.Services.IntuneWindowsAgent.exe");
            facts.ImeVersion = File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "Unknown" : "Not installed";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            facts.ImeVersion = "Unknown";
        }
        facts.Office = OfficeInstall.Read();
        return facts;
    }

    private static string Wmi(string className, string property)
    {
        try
        {
            // WMI is often broken on exactly the devices this tool is for: a time limit keeps the
            // report from hanging on it.
            var options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(10), ReturnImmediately = true, Rewindable = false };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"), new ObjectQuery("SELECT " + property + " FROM " + className), options);
            foreach (var item in searcher.Get())
            {
                using (item) { return item[property]?.ToString()?.Trim(); }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
        }
        return null;
    }

    private static string OsText()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key == null) { return "Unknown"; }
            var name = key.GetValue("ProductName") as string ?? "Windows";
            var build = key.GetValue("CurrentBuildNumber") as string ?? "";
            // ProductName still says "Windows 10" on Windows 11.
            if (int.TryParse(build, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 22000) { name = name.Replace("Windows 10", "Windows 11"); }
            var display = key.GetValue("DisplayVersion") as string ?? "";
            // Build plus revision: the revision is what every monthly update moves.
            var ubr = key.GetValue("UBR");
            var full = ubr is int u ? build + "." + u.ToString(CultureInfo.InvariantCulture) : build;
            return string.Format(CultureInfo.InvariantCulture, "{0} {1} (build {2})", name, display, full).Replace("  ", " ");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// The device's own time zone, from the system setting (TimeZoneKeyName), not from this process:
    /// with time zone redirection the user's session has the remote client's zone instead.
    /// </summary>
    private static TimeZoneInfo SystemZone()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\TimeZoneInformation");
            // The value can carry trailing null characters.
            var id = (key?.GetValue("TimeZoneKeyName") as string ?? "").Trim('\0', ' ');
            return id.Length > 0 ? TimeZoneInfo.FindSystemTimeZoneById(id) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static string UserPrincipalName()
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe"), "/upn")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            _ = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return Environment.UserName;
            }
            var upn = output.Result.Trim();
            if (process.ExitCode == 0 && upn.Length > 0) { return upn; }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
        return Environment.UserName;
    }
}

/// <summary>
/// The device report: device facts, every health check with the next step where it is not OK,
/// and the user's recent actions. Written as text to send to IT support.
/// </summary>
public static class DeviceReport
{
    /// <summary>The summary above the health checks: which checks are a problem and which need a look.</summary>
    internal static IEnumerable<string> HealthSummaryLines(IEnumerable<HealthResult> health)
    {
        var list = health.ToList();
        var fails = list.Where(r => r.Status == HealthStatus.Fail).Select(r => HealthChecks.ReportName(r.Name)).ToList();
        var warns = list.Where(r => r.Status == HealthStatus.Warn).Select(r => HealthChecks.ReportName(r.Name)).ToList();
        if (list.Count == 0) { yield break; }
        if (fails.Count == 0 && warns.Count == 0) { yield return string.Format(CultureInfo.InvariantCulture, "Summary   : all {0} checks OK", list.Count); }
        if (fails.Count > 0) { yield return "Problems  : " + string.Join(", ", fails); }
        if (warns.Count > 0) { yield return "To check  : " + string.Join(", ", warns); }
        yield return "";
    }

    /// <summary>
    /// One line when the device's clock runs in another time zone than the user's (time zone
    /// redirection on a session host): the IME logs use the device's clock, this report the user's,
    /// and the CSV files of the support package UTC. Null when the zones are the same or the device's
    /// is unknown.
    /// </summary>
    internal static string DeviceClockLine(TimeZoneInfo device, TimeZoneInfo user, DateTime utc)
    {
        if (device == null) { return null; }
        user ??= TimeZoneInfo.Local;
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var deviceOffset = device.GetUtcOffset(utc);
        var userOffset = user.GetUtcOffset(utc);
        if (string.Equals(device.Id, user.Id, StringComparison.OrdinalIgnoreCase) && deviceOffset == userOffset) { return null; }
        return "Device clock: " + ZoneText(device, utc) + " (the IME logs use it; the CSV files are in UTC); times in this report are in your time zone, UTC"
            + TimeText.Offset(userOffset) + ".";
    }

    /// <summary>"UTC" for UTC itself, else "UTC+01:00 (W. Europe Standard Time)" at that moment.</summary>
    private static string ZoneText(TimeZoneInfo zone, DateTime utc)
    {
        var offset = zone.GetUtcOffset(utc);
        if (offset == TimeSpan.Zero && !zone.SupportsDaylightSavingTime && zone.Id.StartsWith("UTC", StringComparison.OrdinalIgnoreCase)) { return "UTC"; }
        var daylight = zone.SupportsDaylightSavingTime && zone.IsDaylightSavingTime(utc);
        return "UTC" + TimeText.Offset(offset) + " (" + (daylight ? zone.DaylightName : zone.StandardName) + ")";
    }

    /// <summary>
    /// The lines above the health checks, each "Label : text": when and by which version the report
    /// was made, the time zone, the first-line facts, then the device, the user, the tenant and
    /// the service with its last run. An empty line separates the first-line facts from the device.
    /// </summary>
    internal static List<string> HeaderLines(DeviceFacts facts, DsregInfo dsreg, DateTimeOffset generated, string productVersion,
        Protocol.StatusInfo status, TimeZoneInfo zone, FirstLineFacts firstLine)
    {
        zone ??= TimeZoneInfo.Local;
        var lines = new List<string>
        {
            string.Format(CultureInfo.InvariantCulture, "Generated : {0} (" + ProductInfo.DisplayName + " {1})", generated.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture), productVersion),
            "Time zone : " + TimeText.ZoneLabel(zone, generated.UtcDateTime),
        };
        var clock = DeviceClockLine(facts?.DeviceZone, zone, generated.UtcDateTime);
        if (clock != null) { lines.Add(clock); }
        if (firstLine != null)
        {
            lines.AddRange(firstLine.ReportLines(generated.UtcDateTime, zone));
            lines.Add("");
        }
        lines.AddRange(new[]
        {
            string.Format(CultureInfo.InvariantCulture, "Device    : {0}  |  Serial: {1}  |  Model: {2}", facts.ComputerName, facts.Serial, facts.Model),
            "OS        : " + facts.OperatingSystem,
            "User      : " + facts.User,
            string.Format(CultureInfo.InvariantCulture, "Tenant    : {0}  ({1})", dsreg?.TenantName ?? "", dsreg?.TenantId ?? ""),
            "Device ID : " + (dsreg?.DeviceId ?? ""),
            "IME ver.  : " + facts.ImeVersion,
            "Service   : " + (status == null ? "not available"
                : string.IsNullOrWhiteSpace(status.ServiceVersion) ? "available" : "available (version " + status.ServiceVersion + ")"),
        });
        if (status?.LastResultAction is ToolbeltAction last)
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture, "Last run  : {0}, {1} (run ID {2})", ActionText.Name(last),
                LastRunWords(status), string.IsNullOrEmpty(status.LastResultRunId) ? "unknown" : status.LastResultRunId));
        }
        return lines;

        static string LastRunWords(Protocol.StatusInfo s)
        {
            if (s.LastResultInProgress) { return "still running"; }
            if (s.LastResultCode is not ResultCode code) { return "finished"; }
            if (code == ResultCode.Completed && s.LastResultExitCode is int exit)
            {
                return (RunWords.Exit(exit) ?? "finished") + " (exit " + RunWords.Code(exit) + ")";
            }
            return RunWords.Service(code);
        }
    }

    /// <param name="status">The service's status, or null when the service could not be reached.</param>
    /// <param name="zone">The user's time zone (the local one when null), named in the report.</param>
    /// <param name="firstLine">
    /// The first-line facts (last restart, join type, MDM enrollment ID, network), right below
    /// the time zone, before the device's own facts; null leaves them out.
    /// </param>
    public static string Build(DeviceFacts facts, DsregInfo dsreg, IEnumerable<HealthResult> health, IEnumerable<string> history, DateTimeOffset generated, string productVersion,
        Protocol.StatusInfo status = null, TimeZoneInfo zone = null, FirstLineFacts firstLine = null)
    {
        zone ??= TimeZoneInfo.Local;
        var lines = new List<string> { "===== ENDPOINT TOOLBELT - DEVICE REPORT =====" };
        lines.AddRange(HeaderLines(facts, dsreg, generated, productVersion, status, zone, firstLine));
        lines.Add("");
        lines.Add("----- HEALTH CHECKS -----");
        // A summary first; then every detailed check on one line. The advice for the user
        // stays in the window: the report is for IT support.
        lines.AddRange(HealthSummaryLines(health));
        foreach (var r in health)
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture, "[{0,-7}] {1,-22} {2}", r.StatusWord, HealthChecks.ReportName(r.Name), r.ReportDetail));
        }
        lines.Add("");
        lines.Add("----- MICROSOFT 365 APPS -----");
        lines.AddRange(OfficeInstall.ReportLines(facts.Office));
        if (status != null)
        {
            // Read by the service as SYSTEM; the other device settings are rows above.
            lines.Add("");
            lines.Add("----- INTUNE CONNECTION -----");
            lines.AddRange(DeviceSettingRows.IntuneConnectionLines(status.Readouts?.IntuneConnection));
            // The machine kind changes what the clock and profile rows mean.
            lines.Add(DeviceSettingRows.MachineLine(status.Readouts?.Machine));
        }
        lines.Add("");
        lines.Add("----- RECENT ENDPOINT TOOLBELT ACTIONS -----");
        var recent = history?.ToList();
        if (recent == null) { lines.Add("(history unavailable)"); }
        else if (recent.Count == 0) { lines.Add("(no actions recorded yet)"); }
        else { lines.AddRange(recent.Select(RunWords.HistoryLine)); }
        lines.Add("=============================================");
        return string.Join("\r\n", lines);
    }
}
