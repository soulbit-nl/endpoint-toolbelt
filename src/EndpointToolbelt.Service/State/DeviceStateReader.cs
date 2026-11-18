using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.Configuration;
using Microsoft.Win32;

namespace EndpointToolbelt.Service.State;

/// <summary>
/// Read-only device facts for the status request: enrollment, the product's restart marker and
/// cooldowns. The engine writes the State values; the service only reads them.
/// </summary>
public sealed class DeviceStateReader
{
    private readonly IRegistryReader _registry;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<DateTime> _bootTimeUtc;
    private readonly Func<IEnumerable<(string Provider, int State)>> _enrollments;
    private readonly Func<(DateTime? Utc, string Source)> _lastSession;
    private readonly Func<string> _entDmId;

    private const string MdmProvider = "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider";
    private const string MdmSyncLog = MdmProvider + "/Sync";
    private const string MdmAdminLog = MdmProvider + "/Admin";

    /// <param name="entDmId">
    /// The EntDMID of the one active Intune enrollment, as written; by default read from the
    /// registry, and when the enrollments are a test's own, none (a test never reads this device).
    /// </param>
    public DeviceStateReader(
        IRegistryReader registry,
        Func<DateTime> utcNow = null,
        Func<DateTime> bootTimeUtc = null,
        Func<IEnumerable<(string Provider, int State)>> enrollments = null,
        Func<(DateTime? Utc, string Source)> lastSession = null,
        Func<string> entDmId = null)
    {
        _registry = registry;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _bootTimeUtc = bootTimeUtc ?? GetBootTimeUtc;
        _enrollments = enrollments ?? ReadEnrollments;
        _lastSession = lastSession ?? ReadLastSession;
        _entDmId = entDmId ?? (enrollments == null ? ReadEntDmId : () => null);
    }

    /// <summary>
    /// The MDM enrollment ID (EntDMID) of the one active Intune enrollment, only as a GUID, in its
    /// "D" form whatever form it was written in; null for none, anything that is not a GUID, or a
    /// value that cannot be read.
    /// </summary>
    public string GetMdmEnrollmentId()
    {
        string raw;
        try { raw = _entDmId(); } catch (Exception) { return null; }
        return Guid.TryParse((raw ?? "").Trim(), out var id) ? id.ToString("D") : null;
    }

    /// <summary>
    /// The EntDMID under the one active "MS DM Server" enrollment (EnrollmentState 1):
    /// Enrollments\{GUID}\DMClient\MS DM Server\EntDMID; null for none or more than one.
    /// </summary>
    private static string ReadEntDmId()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var root = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments", writable: false);
        if (root == null) { return null; }
        var found = new List<string>();
        foreach (var name in root.GetSubKeyNames())
        {
            if (!Guid.TryParse(name, out _)) { continue; }
            using var key = root.OpenSubKey(name, writable: false);
            if (key == null) { continue; }
            if (!string.Equals(key.GetValue("ProviderID") as string, "MS DM Server", StringComparison.OrdinalIgnoreCase) || key.GetValue("EnrollmentState") as int? != 1) { continue; }
            using var client = key.OpenSubKey(@"DMClient\MS DM Server", writable: false);
            found.Add(client?.GetValue("EntDMID") as string);
        }
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>
    /// The newest event in the MDM session log within 30 days, and the log it came from. The time
    /// is null when there is none or the log cannot be read; the source then says which.
    /// Session events are written for every session, however it was started (Windows' schedule,
    /// Settings, or this product).
    /// </summary>
    public (DateTime? Utc, string Source) GetLastSync()
    {
        (DateTime? Utc, string Source) last;
        try { last = _lastSession(); } catch (Exception) { return (null, LastSyncUnreadable); }
        if (last.Utc == null || last.Utc.Value < _utcNow().AddDays(-30)) { return (null, null); }
        return last;
    }

    /// <summary>The source value when the MDM logs could not be read.</summary>
    public const string LastSyncUnreadable = "unreadable";

    private static (DateTime?, string) ReadLastSession()
    {
        // Filtered by provider, not by event ID: the session event IDs differ between Windows
        // builds, and a wrong one would silently find nothing. /Admin never carries session
        // boundaries, so it is read only on a build without the /Sync log, under its own name.
        try
        {
            return (ReadNewest(MdmSyncLog), "MDM session log");
        }
        catch (System.Diagnostics.Eventing.Reader.EventLogNotFoundException)
        {
            return (ReadNewest(MdmAdminLog), "MDM admin log");
        }
    }

    private static DateTime? ReadNewest(string log)
    {
        // Newest first, within the last 30 days (2592000000 ms).
        var query = new System.Diagnostics.Eventing.Reader.EventLogQuery(log, System.Diagnostics.Eventing.Reader.PathType.LogName,
            $"*[System[Provider[@Name='{MdmProvider}'] and TimeCreated[timediff(@SystemTime) <= 2592000000]]]") { ReverseDirection = true };
        using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
        // The channels accept writes from interactive users: like the engine (Mdm.IsTrustedWriter),
        // an event whose writer still runs in a user session is not counted. Bounded, newest first.
        for (var i = 0; i < MaxEventsRead; i++)
        {
            using var record = reader.ReadEvent();
            if (record == null) { return null; }
            if (IsTrustedWriter(record.ProcessId)) { return record.TimeCreated?.ToUniversalTime(); }
        }
        return null;
    }

    private const int MaxEventsRead = 500;

    /// <summary>A writer in session 0, or one that has exited and cannot be checked.</summary>
    internal static bool IsTrustedWriter(int? processId, Func<int, int?> sessionOf = null)
    {
        if (processId == null || processId <= 0) { return true; }
        sessionOf ??= SessionOf;
        var session = sessionOf(processId.Value);
        return session == null || session == 0;
    }

    private static int? SessionOf(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.SessionId;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>True for exactly one active MDM enrollment, false for none, null when ambiguous.</summary>
    public bool? IsEnrolled()
    {
        var count = 0;
        foreach (var (provider, state) in _enrollments())
        {
            if (string.Equals(provider, "MS DM Server", StringComparison.OrdinalIgnoreCase) && state == 1) { count++; }
        }
        return count switch { 0 => false, 1 => true, _ => null };
    }

    /// <summary>The restart marker, only while the device has not restarted since it was set.</summary>
    public (DateTime? SinceUtc, string By) GetRestartRequired()
    {
        var since = ParseTimestamp(_registry.GetValue(ProductInfo.StateKey, "RestartRequiredSince"));
        if (since == null) { return (null, null); }
        // If the boot time cannot be read, the marker is ignored rather than shown wrongly.
        DateTime boot;
        try { boot = _bootTimeUtc(); } catch (Exception) { return (null, null); }
        if (since.Value <= boot) { return (null, null); }
        return (since, _registry.GetValue(ProductInfo.StateKey, "RestartRequiredBy") as string);
    }

    public List<CooldownInfo> GetCooldowns(ServiceConfig config)
    {
        var list = new List<CooldownInfo>();
        Add(ToolbeltAction.FullReset, "LastFullReset", config.FullResetCooldownHours);
        Add(ToolbeltAction.RepairWU, "LastWURepair", config.WURepairCooldownHours);
        return list;

        void Add(ToolbeltAction action, string valueName, int hours)
        {
            var last = ParseTimestamp(_registry.GetValue(ProductInfo.StateKey, valueName));
            if (last == null) { return; }
            var available = last.Value.AddHours(hours);
            if (available > _utcNow())
            {
                list.Add(new CooldownInfo { Action = action, AvailableAtUtc = available.ToString("o", CultureInfo.InvariantCulture) });
            }
        }
    }

    /// <summary>State timestamps are ISO 8601 with an offset (written by the engine).</summary>
    public static DateTime? ParseTimestamp(object raw)
    {
        if (raw is not string s || s.Length == 0) { return null; }
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.UtcDateTime
            : null;
    }

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    private DateTime GetBootTimeUtc() => _utcNow() - TimeSpan.FromMilliseconds(GetTickCount64());

    private static IEnumerable<(string, int)> ReadEnrollments()
    {
        var result = new List<(string, int)>();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var root = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments", writable: false);
        if (root == null) { return result; }
        foreach (var name in root.GetSubKeyNames())
        {
            if (!Guid.TryParse(name, out _)) { continue; }
            using var key = root.OpenSubKey(name, writable: false);
            if (key == null) { continue; }
            var provider = key.GetValue("ProviderID") as string;
            var state = key.GetValue("EnrollmentState") is int i ? i : -1;
            result.Add((provider, state));
        }
        return result;
    }
}
