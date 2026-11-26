using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace EndpointToolbelt.Engine;

/// <summary>The Windows services seam.</summary>
public interface IServiceControl
{
    /// <summary>True when the service is running; false when it is not, or cannot be read.</summary>
    bool IsRunning(string name);

    /// <summary>Starts the service and waits until it runs; throws when it does not.</summary>
    void Start(string name, TimeSpan wait);

    /// <summary>The service's state as ServiceController names it ("Running", "StopPending", ...), or null when it is not installed.</summary>
    string Status(string name);

    /// <summary>Asks the service to stop, without waiting; throws when it refuses.</summary>
    void RequestStop(string name);

    /// <summary>Asks the service to start, without waiting; throws when it refuses.</summary>
    void RequestStart(string name);

    /// <summary>Waits until the service reaches a state; throws when it does not in time.</summary>
    void WaitForStatus(string name, string status, TimeSpan wait);

    /// <summary>The start type as ServiceController names it ("Automatic", "Manual", "Disabled", ...); throws when the service is not installed.</summary>
    string StartType(string name);

    /// <summary>The services that depend on this one, each with its state; throws when it is not installed.</summary>
    IReadOnlyList<KeyValuePair<string, string>> Dependents(string name);
}

/// <summary>One event as the engine uses it.</summary>
public sealed class EventInfo
{
    public int Id { get; set; }

    /// <summary>0 LogAlways, 1 Critical, 2 Error, 3 Warning, 4 Information, 5 Verbose.</summary>
    public int Level { get; set; }

    /// <summary>When the event was written, or null when unknown.</summary>
    public DateTime? TimeCreatedUtc { get; set; }

    /// <summary>
    /// The event's rendered message, or "" when Windows cannot render it. Only the BitLocker
    /// backup uses it, and only after cleaning (BitLockerEventText).
    /// </summary>
    public string Message { get; set; } = "";
}

/// <summary>The event log seam.</summary>
public interface IEventLogReader
{
    /// <summary>
    /// The newest events of a log since a moment (at most <paramref name="max"/>), only those with
    /// <paramref name="eventId"/> when it is above 0; throws when the log cannot be read.
    /// </summary>
    IReadOnlyList<EventInfo> Read(string logName, DateTime sinceUtc, int max, int eventId = 0);
}

/// <summary>Why BitLocker could not be read.</summary>
public enum BitLockerProblem
{
    /// <summary>Windows has no BitLocker management (no BitLocker WMI namespace or class).</summary>
    NotAvailable,

    /// <summary>BitLocker management exists, but has no volume for the drive.</summary>
    NoVolume,
}

public sealed class BitLockerUnavailableException : Exception
{
    public BitLockerUnavailableException() { }
    public BitLockerUnavailableException(string message) : base(message) { }
    public BitLockerUnavailableException(string message, Exception inner) : base(message, inner) { }
    public BitLockerUnavailableException(BitLockerProblem problem) : base(problem.ToString()) => Problem = problem;

    public BitLockerProblem Problem { get; }
}

/// <summary>A BitLocker volume as the engine uses it: protector IDs and states, never a recovery password.</summary>
public sealed class BitLockerVolumeInfo
{
    /// <summary>As Get-BitLockerVolume names it: FullyDecrypted, FullyEncrypted, EncryptionInProgress, ...</summary>
    public string VolumeStatus { get; set; } = "";

    /// <summary>Off, On or Unknown.</summary>
    public string ProtectionStatus { get; set; } = "";

    /// <summary>The IDs of the recovery-password (numerical password) protectors.</summary>
    public IReadOnlyList<string> RecoveryPasswordIds { get; set; } = Array.Empty<string>();
}

/// <summary>Microsoft Defender Antivirus as the engine uses it.</summary>
public sealed class DefenderInfo
{
    public string Mode { get; set; } = "";
    public bool ServiceEnabled { get; set; }
    public string Version { get; set; } = "";
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>When the last quick scan ended (not a custom scan other software asks for); null when none is recorded or it cannot be read.</summary>
    public DateTime? QuickScanEndUtc { get; set; }

    /// <summary>When the last full scan ended; null when none is recorded or it cannot be read.</summary>
    public DateTime? FullScanEndUtc { get; set; }

    /// <summary>Whether real-time protection is on now; null when it cannot be read.</summary>
    public bool? RealTimeProtection { get; set; }

    /// <summary>Whether Microsoft Defender Antivirus is on now; null when it cannot be read.</summary>
    public bool? Antivirus { get; set; }

    /// <summary>Whether Microsoft Defender Antispyware is on now; null when it cannot be read.</summary>
    public bool? Antispyware { get; set; }
}

/// <summary>The WMI seam: the device readouts the actions need.</summary>
public interface IDeviceInfo
{
    /// <summary>The volume; throws <see cref="BitLockerUnavailableException"/> when there is none, other exceptions when it cannot be read.</summary>
    BitLockerVolumeInfo ReadBitLocker(string driveLetter);

    /// <summary>Defender's status, or null when it cannot be read.</summary>
    DefenderInfo ReadDefender();
}

public sealed class WindowsServiceControl : IServiceControl
{
    public bool IsRunning(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            return service.Status == ServiceControllerStatus.Running;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Start(string name, TimeSpan wait)
    {
        using var service = new ServiceController(name);
        if (service.Status != ServiceControllerStatus.Running && service.Status != ServiceControllerStatus.StartPending) { service.Start(); }
        service.WaitForStatus(ServiceControllerStatus.Running, wait);
    }

    public string Status(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            return service.Status.ToString();
        }
        catch (InvalidOperationException)
        {
            // Not installed (or not readable): Get-Service found no such service.
            return null;
        }
    }

    public void RequestStop(string name)
    {
        using var service = new ServiceController(name);
        service.Stop();
    }

    public void RequestStart(string name)
    {
        using var service = new ServiceController(name);
        service.Start();
    }

    public void WaitForStatus(string name, string status, TimeSpan wait)
    {
        using var service = new ServiceController(name);
        service.WaitForStatus((ServiceControllerStatus)Enum.Parse(typeof(ServiceControllerStatus), status), wait);
        service.Refresh();
        if (service.Status.ToString() != status) { throw new InvalidOperationException("the service is " + service.Status); }
    }

    public string StartType(string name)
    {
        using var service = new ServiceController(name);
        return service.StartType.ToString();
    }

    public IReadOnlyList<KeyValuePair<string, string>> Dependents(string name)
    {
        using var service = new ServiceController(name);
        var list = new List<KeyValuePair<string, string>>();
        foreach (var dependent in service.DependentServices)
        {
            using (dependent) { list.Add(new KeyValuePair<string, string>(dependent.ServiceName, dependent.Status.ToString())); }
        }
        return list;
    }
}

public sealed class WindowsEventLogReader : IEventLogReader
{
    public IReadOnlyList<EventInfo> Read(string logName, DateTime sinceUtc, int max, int eventId = 0)
    {
        var since = sinceUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var idFilter = eventId > 0 ? "(EventID=" + eventId.ToString(CultureInfo.InvariantCulture) + ") and " : "";
        var query = new EventLogQuery(logName, PathType.LogName, "*[System[" + idFilter + "TimeCreated[@SystemTime>='" + since + "']]]") { ReverseDirection = true };
        var list = new List<EventInfo>();
        using var reader = new EventLogReader(query);
        for (var record = reader.ReadEvent(); record != null && list.Count < max; record = reader.ReadEvent())
        {
            using (record)
            {
                list.Add(new EventInfo
                {
                    Id = record.Id,
                    Level = record.Level ?? 0,
                    TimeCreatedUtc = record.TimeCreated?.ToUniversalTime(),
                    Message = Describe(record),
                });
            }
        }
        return list;
    }

    private static string Describe(EventRecord record)
    {
        try { return record.FormatDescription() ?? ""; }
        catch (EventLogException) { return ""; }
    }
}

public sealed class WmiDeviceInfo : IDeviceInfo
{
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex DriveLetter = new(@"\A[A-Za-z]:\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly string[] ConversionNames = { "FullyDecrypted", "FullyEncrypted", "EncryptionInProgress", "DecryptionInProgress", "EncryptionSuspended", "DecryptionSuspended" };
    private static readonly string[] ProtectionNames = { "Off", "On", "Unknown" };

    private static ManagementObject FirstInstance(string scope, string query)
    {
        var options = new EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = false };
        // Packet privacy, as Microsoft documents for Win32_EncryptableVolume.
        var connection = new ConnectionOptions { Authentication = AuthenticationLevel.PacketPrivacy, Impersonation = ImpersonationLevel.Impersonate };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope, connection), new ObjectQuery(query), options);
        foreach (ManagementObject item in searcher.Get()) { return item; }
        return null;
    }

    /// <summary>Calls a method and returns its output; a non-zero ReturnValue is an error, as the cmdlet would throw.</summary>
    private static ManagementBaseObject Call(ManagementObject target, string method, ManagementBaseObject input = null)
    {
        var output = target.InvokeMethod(method, input, null) ?? throw new InvalidOperationException(method + " returned nothing.");
        if (output["ReturnValue"] is uint code && code != 0)
        {
            throw new InvalidOperationException(method + " failed with " + EndpointToolbelt.Platform.TextFormat.ExitCode(code) + ".");
        }
        return output;
    }

    private static bool IsMissing(ManagementException ex) =>
        ex.ErrorCode == ManagementStatus.InvalidNamespace || ex.ErrorCode == ManagementStatus.InvalidClass || ex.ErrorCode == ManagementStatus.NotFound;

    public BitLockerVolumeInfo ReadBitLocker(string driveLetter)
    {
        if (driveLetter == null || !DriveLetter.IsMatch(driveLetter)) { throw new ArgumentException("Not a drive letter.", nameof(driveLetter)); }
        ManagementObject volume;
        try
        {
            volume = FirstInstance(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption", "SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter='" + driveLetter + "'");
        }
        catch (ManagementException ex) when (IsMissing(ex))
        {
            throw new BitLockerUnavailableException(BitLockerProblem.NotAvailable);
        }
        if (volume == null) { throw new BitLockerUnavailableException(BitLockerProblem.NoVolume); }
        using (volume)
        {
            var conversion = Call(volume, "GetConversionStatus")["ConversionStatus"] as uint?;
            var protection = Call(volume, "GetProtectionStatus")["ProtectionStatus"] as uint?;
            // Key protector type 3: numerical password (the recovery password). Only IDs are read.
            var input = volume.GetMethodParameters("GetKeyProtectors");
            input["KeyProtectorType"] = 3u;
            var ids = Call(volume, "GetKeyProtectors", input)["VolumeKeyProtectorID"] as string[];
            return new BitLockerVolumeInfo
            {
                VolumeStatus = conversion is uint c && c < ConversionNames.Length ? ConversionNames[c] : "Unknown",
                ProtectionStatus = protection is uint p && p < ProtectionNames.Length ? ProtectionNames[p] : "Unknown",
                RecoveryPasswordIds = ids ?? Array.Empty<string>(),
            };
        }
    }

    public DefenderInfo ReadDefender()
    {
        try
        {
            using var status = FirstInstance(@"\\.\root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpComputerStatus");
            if (status == null) { return null; }
            var info = new DefenderInfo
            {
                Mode = status["AMRunningMode"] as string ?? "",
                ServiceEnabled = status["AMServiceEnabled"] as bool? ?? false,
                Version = status["AntivirusSignatureVersion"] as string ?? "",
            };
            if (status["AntivirusSignatureLastUpdated"] is string dmtf && dmtf.Length > 0)
            {
                info.UpdatedUtc = ManagementDateTimeConverter.ToDateTime(dmtf).ToUniversalTime();
            }
            // The health facts of Check Defender blocks: each one on its own, so a property an older
            // Defender does not have leaves only that fact unknown.
            info.QuickScanEndUtc = DefenderTime(status, "QuickScanEndTime");
            info.FullScanEndUtc = DefenderTime(status, "FullScanEndTime");
            info.RealTimeProtection = DefenderFlag(status, "RealTimeProtectionEnabled");
            info.Antivirus = DefenderFlag(status, "AntivirusEnabled");
            info.Antispyware = DefenderFlag(status, "AntispywareEnabled");
            return info;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A date property of MSFT_MpComputerStatus in UTC; null when missing, unreadable or before 2000 (Defender's "never").</summary>
    private static DateTime? DefenderTime(ManagementBaseObject status, string name)
    {
        try
        {
            if (status[name] is not string dmtf || dmtf.Length == 0) { return null; }
            var utc = ManagementDateTimeConverter.ToDateTime(dmtf).ToUniversalTime();
            return utc.Year < 2000 ? null : utc;
        }
        catch (Exception ex) when (ex is ManagementException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>A flag of MSFT_MpComputerStatus; null when missing or unreadable.</summary>
    private static bool? DefenderFlag(ManagementBaseObject status, string name)
    {
        try { return status[name] as bool?; }
        catch (ManagementException) { return null; }
    }
}

/// <summary>Small readouts of the device itself.</summary>
public static class DeviceFacts
{
    /// <summary>When Windows last started (from the time since the start), or null when unknown.</summary>
    public static DateTime? BootUtc()
    {
        try { return DateTime.UtcNow - TimeSpan.FromMilliseconds(GetTickCount64()); }
        catch (Exception) { return null; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    /// <summary>Free bytes on a drive ("C:"), or null when unknown.</summary>
    public static long? FreeBytes(string drive)
    {
        try { return new DriveInfo(drive).AvailableFreeSpace; }
        catch (Exception) { return null; }
    }

    /// <summary>True when a process with this name (without ".exe") runs.</summary>
    public static bool IsProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes) { p.Dispose(); }
        return processes.Length > 0;
    }
}
