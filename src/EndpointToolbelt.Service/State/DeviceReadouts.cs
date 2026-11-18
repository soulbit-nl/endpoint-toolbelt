using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;
using System.Threading;
using System.Threading.Tasks;
using EndpointToolbelt.Protocol;
using Microsoft.Win32;

namespace EndpointToolbelt.Service.State;

/// <summary>
/// The device-wide readouts of the health view, cached. A status
/// request never waits for them: it gets the last answer (null before the first one) and a
/// stale answer starts a refresh in the background. Each part is read on its own, so one that
/// fails leaves the others.
/// </summary>
public sealed class DeviceReadoutReader
{
    public static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(5);

    private readonly Func<DeviceReadouts> _compute;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private DeviceReadouts _cached;
    private DateTime _cachedAt;
    private int _refreshing;

    public DeviceReadoutReader(Func<DeviceReadouts> compute = null, Func<DateTime> utcNow = null)
    {
        _compute = compute ?? ComputeAll;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The last answer; starts a refresh when there is none or it is older than <see cref="CacheTime"/>.</summary>
    public DeviceReadouts Get()
    {
        var now = _utcNow();
        DeviceReadouts cached;
        bool fresh;
        lock (_lock)
        {
            cached = _cached;
            fresh = cached != null && now - _cachedAt < CacheTime && now >= _cachedAt;
        }
        if (!fresh) { StartRefresh(); }
        return cached;
    }

    /// <summary>Starts a refresh unless one is running (also called once at service start).</summary>
    public void StartRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) { return; }
        Enqueue(() =>
        {
            try { RefreshNow(); }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        });
    }

    /// <summary>
    /// A run that may have changed the device ended. A fresh read starts after any read already
    /// going on (whose answer may be from before the run's end), and the caller waits for it at most
    /// <paramref name="wait"/>; true when it finished in that time. When it did not, it goes on in the
    /// background and the next status gets it.
    /// </summary>
    public bool RefreshAfterRun(TimeSpan wait)
    {
        var refresh = Enqueue(RefreshNow);
        try { return refresh.Wait(wait < TimeSpan.Zero ? TimeSpan.Zero : wait); }
        catch (AggregateException) { return false; }
    }

    private readonly object _chainLock = new();
    private Task _chain = Task.CompletedTask;

    /// <summary>One read at a time: each refresh runs after the one before it.</summary>
    private Task Enqueue(Action work)
    {
        lock (_chainLock)
        {
            _chain = _chain.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return _chain;
        }
    }

    /// <summary>Reads everything now, on the calling thread (tests and the background refresh).</summary>
    internal void RefreshNow()
    {
        DeviceReadouts result;
        try { result = _compute(); } catch (Exception) { result = new DeviceReadouts(); }
        lock (_lock)
        {
            _cached = result;
            _cachedAt = _utcNow();
        }
    }

    private static DeviceReadouts ComputeAll()
    {
        var now = DateTime.UtcNow;
        // What kind of machine this is changes what the clock and profile rows mean.
        var machine = Safe(MachineProbe.Read) ?? new MachineReadout();
        return new DeviceReadouts
        {
            Compliance = Safe(ComplianceProbe.Read),
            Defender = Safe(DefenderProbe.Read) ?? new DefenderReadout { Mode = "unknown" },
            Clock = Safe(() => ClockProbe.Read(machine)) ?? new ClockReadout { Source = "unknown", Unavailable = "unreadable" },
            Profiles = Safe(() => ProfileErrorsProbe.Read(now, machine)) ?? new ProfileErrorsReadout { Unavailable = "unreadable" },
            IntuneConnection = Safe(IntuneTasksProbe.Read) ?? new IntuneConnectionReadout { Unavailable = "unreadable" },
            Machine = machine,
        };
    }

    private static T Safe<T>(Func<T> read) where T : class
    {
        try { return read(); } catch (Exception) { return null; }
    }
}

/// <summary>Small helpers shared by the probes.</summary>
internal static class ProbeTools
{
    public static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(10);

    public static string System32(string file) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), file);

    /// <summary>
    /// Runs a system tool by full path with fixed arguments and returns its output, or null when
    /// it did not finish within the limit (it is then ended).
    /// </summary>
    public static string Run(string file, string arguments, TimeSpan limit)
    {
        var info = new ProcessStartInfo(System32(file), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
        };
        using var process = Process.Start(info);
        if (process == null) { return null; }
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)limit.TotalMilliseconds))
        {
            try { process.Kill(); } catch (Exception) { }
            return null;
        }
        process.WaitForExit();
        return output.Result + errors.Result;
    }

    /// <summary>The first instance of a WMI class, or null when there is none.</summary>
    public static ManagementObject FirstInstance(string scope, string query)
    {
        var options = new EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = false };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), options);
        foreach (ManagementObject item in searcher.Get()) { return item; }
        return null;
    }

    /// <summary>A COM call through IDispatch (property get, method), culture-independent.</summary>
    public static object Com(object target, string name, BindingFlags flags, params object[] args)
        => target.GetType().InvokeMember(name, flags, null, target, args, CultureInfo.InvariantCulture);

    /// <summary>A short code-like token: only letters, digits and . _ - /, at most <paramref name="max"/> characters.</summary>
    public static string Token(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) { return null; }
        var clean = Regex.Replace(value.Trim(), "[^A-Za-z0-9._/-]", "_", RegexOptions.None, TimeSpan.FromSeconds(1));
        return clean.Length <= max ? clean : clean.Substring(0, max);
    }
}

/// <summary>
/// What kind of machine this is. A Microsoft Hyper-V virtual machine (Hyper-V, Azure, Windows
/// Sandbox) by Win32_ComputerSystem's manufacturer and model; a multi-session host by its edition.
/// The Hyper-V guest registry key is not used: Windows with Hyper-V or virtualization-based
/// security on a physical device has it too.
/// </summary>
internal static class MachineProbe
{
    public static MachineReadout Read()
    {
        var result = new MachineReadout();
        try
        {
            using var system = ProbeTools.FirstInstance(@"\\.\root\CIMV2", "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            result.VirtualMachine = system == null ? null : IsVirtualMachine(system["Manufacturer"] as string, system["Model"] as string);
        }
        catch (Exception) { result.VirtualMachine = null; }
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            result.MultiSession = key == null ? null : IsMultiSession(key.GetValue("EditionID") as string, key.GetValue("ProductName") as string);
        }
        catch (Exception) { result.MultiSession = null; }
        return result;
    }

    /// <summary>Hyper-V (and Azure) guests report the manufacturer "Microsoft Corporation" and the model "Virtual Machine".</summary>
    internal static bool IsVirtualMachine(string manufacturer, string model)
        => string.Equals((manufacturer ?? "").Trim(), "Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
            && string.Equals((model ?? "").Trim(), "Virtual Machine", StringComparison.OrdinalIgnoreCase);

    /// <summary>Windows Enterprise multi-session: the edition "ServerRdsh", or a product name that says multi-session.</summary>
    internal static bool IsMultiSession(string editionId, string productName)
        => string.Equals((editionId ?? "").Trim(), "ServerRdsh", StringComparison.OrdinalIgnoreCase)
            || (productName ?? "").IndexOf("multi-session", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>A machine on which Windows Hello for Business cannot apply: a virtual machine or a multi-session host.</summary>
    internal static bool NoWindowsHello(MachineReadout machine) => machine?.VirtualMachine == true || machine?.MultiSession == true;
}

/// <summary>The local settings Intune compliance policies usually check.</summary>
internal static class ComplianceProbe
{
    private static readonly Regex DriveLetter = new("^[A-Za-z]:$", RegexOptions.CultureInvariant, TimedRegex.Limit);
    private static readonly Regex TpmSpec = new(@"^\s*(\d{1,2}\.\d{1,2})", RegexOptions.CultureInvariant, TimedRegex.Limit);
    private static readonly Regex BuildNumber = new(@"^\d{1,6}$", RegexOptions.CultureInvariant, TimedRegex.Limit);

    public static ComplianceReadout Read()
    {
        var result = new ComplianceReadout();
        try { ReadBitLocker(result); } catch (Exception) { result.BitLocker = "unknown"; }
        try { result.SecureBoot = SecureBootToken(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", null)); }
        catch (Exception) { result.SecureBoot = "unknown"; }
        try { ReadTpm(result); } catch (Exception) { result.Tpm = "unknown"; }
        try { ReadFirewall(result); } catch (Exception) { result.FirewallDomain = result.FirewallPrivate = result.FirewallPublic = "unknown"; }
        try { ReadOs(result); } catch (Exception) { }
        return result;
    }

    private static void ReadBitLocker(ComplianceReadout result)
    {
        var drive = (Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\").TrimEnd('\\');
        if (!TimedRegex.IsMatch(DriveLetter, drive)) { result.BitLocker = "unknown"; return; }
        using var volume = ProbeTools.FirstInstance(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption",
            "SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter='" + drive + "'");
        if (volume == null) { result.BitLocker = "unknown"; return; }
        var protection = volume.InvokeMethod("GetProtectionStatus", null, null);
        var conversion = volume.InvokeMethod("GetConversionStatus", null, null);
        result.BitLocker = BitLockerToken(protection?["ProtectionStatus"] as uint?, conversion?["ConversionStatus"] as uint?);
        // Key protector type 3: numerical password (the recovery password).
        var input = volume.GetMethodParameters("GetKeyProtectors");
        input["KeyProtectorType"] = 3u;
        var protectors = volume.InvokeMethod("GetKeyProtectors", input, null);
        result.RecoveryPassword = protectors?["VolumeKeyProtectorID"] is string[] ids ? ids.Length > 0 : null;
    }

    /// <summary>
    /// BitLocker's protection and conversion status as a token. Conversion: 0 fully decrypted,
    /// 1 fully encrypted, 2 encrypting, 3 decrypting, 4 encryption paused, 5 decryption paused.
    /// Protection: 0 off, 1 on, 2 unknown.
    /// </summary>
    internal static string BitLockerToken(uint? protection, uint? conversion) => conversion switch
    {
        0 => "off",
        2 or 4 => "encrypting",
        3 or 5 => "decrypting",
        1 when protection == 1 => "on",
        1 when protection == 0 => "suspended",
        _ => "unknown",
    };

    internal static string SecureBootToken(object value) => value switch
    {
        null => "unsupported",
        int i when i == 1 => "on",
        int i when i == 0 => "off",
        _ => "unknown",
    };

    private static void ReadTpm(ComplianceReadout result)
    {
        using var tpm = ProbeTools.FirstInstance(@"\\.\root\CIMV2\Security\MicrosoftTpm", "SELECT * FROM Win32_Tpm");
        result.Tpm = TpmToken(tpm != null, tpm?["IsEnabled_InitialValue"] as bool?, tpm?["IsActivated_InitialValue"] as bool?);
        result.TpmVersion = TpmVersionToken(tpm?["SpecVersion"] as string);
    }

    internal static string TpmToken(bool present, bool? enabled, bool? activated)
    {
        if (!present) { return "missing"; }
        if (enabled == null || activated == null) { return "unknown"; }
        return enabled.Value && activated.Value ? "ready" : "not-ready";
    }

    /// <summary>"2.0, 0, 1.38" as "2.0".</summary>
    internal static string TpmVersionToken(string spec)
    {
        if (string.IsNullOrEmpty(spec)) { return null; }
        var m = TimedRegex.MatchOrEmpty(TpmSpec, spec);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static void ReadFirewall(ComplianceReadout result)
    {
        // The effective state per profile, as Windows Firewall applies it (policy included).
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true);
        var policy = Activator.CreateInstance(type);
        try
        {
            string Profile(int id)
            {
                try { return ProbeTools.Com(policy, "FirewallEnabled", BindingFlags.GetProperty, id) is bool on ? (on ? "on" : "off") : "unknown"; }
                catch (Exception) { return "unknown"; }
            }
            result.FirewallDomain = Profile(1);
            result.FirewallPrivate = Profile(2);
            result.FirewallPublic = Profile(4);
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    private static void ReadOs(ComplianceReadout result)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (key == null) { return; }
        var major = key.GetValue("CurrentMajorVersionNumber") as int?;
        var minor = key.GetValue("CurrentMinorVersionNumber") as int?;
        var build = key.GetValue("CurrentBuildNumber") as string;
        var ubr = key.GetValue("UBR") as int?;
        if (major != null && minor != null && build != null && TimedRegex.IsMatch(BuildNumber, build))
        {
            result.OsBuild = string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}", major, minor, build, ubr ?? 0);
        }
        result.OsVersion = ProbeTools.Token(key.GetValue("DisplayVersion") as string, 10);
    }
}

/// <summary>Microsoft Defender Antivirus.</summary>
internal static class DefenderProbe
{
    private static readonly Regex VersionPattern = new(@"^\d{1,5}(\.\d{1,6}){1,3}$", RegexOptions.CultureInvariant, TimedRegex.Limit);

    public static DefenderReadout Read()
    {
        ManagementObject status;
        try
        {
            status = ProbeTools.FirstInstance(@"\\.\root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpComputerStatus");
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace || ex.ErrorCode == ManagementStatus.InvalidClass)
        {
            return new DefenderReadout { Mode = "missing" };
        }
        using (status)
        {
            if (status == null) { return new DefenderReadout { Mode = "missing" }; }
            var readout = new DefenderReadout
            {
                Mode = ModeToken(status["AMRunningMode"] as string, status["AMServiceEnabled"] as bool?),
                RealTimeProtection = status["RealTimeProtectionEnabled"] as bool?,
                SignatureVersion = VersionToken(status["AntivirusSignatureVersion"] as string),
            };
            if (status["AntivirusSignatureLastUpdated"] is string dmtf && dmtf.Length > 0)
            {
                try
                {
                    var local = ManagementDateTimeConverter.ToDateTime(dmtf);
                    if (local.Year > 2000) { readout.SignatureUpdatedUtc = local.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
                }
                catch (ArgumentException) { }
            }
            return readout;
        }
    }

    /// <summary>
    /// The running mode as a token. Microsoft documents "Normal", "Passive" (also written "Passive
    /// Mode") and "EDR Block Mode"; a stopped antimalware service is "off".
    /// </summary>
    internal static string ModeToken(string mode, bool? serviceEnabled)
    {
        if (serviceEnabled == false) { return "off"; }
        if (string.IsNullOrWhiteSpace(mode)) { return "unknown"; }
        if (mode.IndexOf("EDR", StringComparison.OrdinalIgnoreCase) >= 0) { return "edr-block"; }
        if (mode.IndexOf("Passive", StringComparison.OrdinalIgnoreCase) >= 0) { return "passive"; }
        if (mode.Trim().Equals("Normal", StringComparison.OrdinalIgnoreCase)) { return "normal"; }
        return "unknown";
    }

    internal static string VersionToken(string version)
        => version != null && TimedRegex.IsMatch(VersionPattern, version) ? version : null;
}

/// <summary>The clock against the Windows Time service's source.</summary>
internal static class ClockProbe
{
    private static readonly Regex Offset = new(@",\s*([+-]?\d{1,9}(?:[.,]\d{1,9})?)s\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimedRegex.Limit);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    private static readonly char[] LineBreaks = { '\r', '\n' };
    private static readonly char[] Spaces = { ' ', '\t' };
    private static readonly Regex HostName = new(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,62}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,62}[A-Za-z0-9])?){0,10}$", RegexOptions.CultureInvariant, TimedRegex.Limit);

    private const string VmicProvider = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\W32Time\TimeProviders\VMICTimeProvider";

    public static ClockReadout Read(MachineReadout machine)
    {
        var readout = Measure();
        object enabled;
        try { enabled = Registry.GetValue(VmicProvider, "Enabled", null); } catch (Exception) { enabled = null; }
        readout.HostTimeProvider = HostTimeProvider(machine?.VirtualMachine, enabled);
        return readout;
    }

    /// <summary>
    /// The host keeps this clock: a virtual machine (null: unknown) whose Windows Time has the
    /// Hyper-V time provider enabled (Enabled = 1). A physical device has the provider enabled by
    /// default too, so the provider alone says nothing.
    /// </summary>
    internal static bool? HostTimeProvider(bool? virtualMachine, object enabled)
    {
        if (virtualMachine == null) { return null; }
        return virtualMachine.Value && enabled is int on && on == 1;
    }

    private static ClockReadout Measure()
    {
        const string parameters = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\W32Time\Parameters";
        var kind = SourceKind(Registry.GetValue(parameters, "Type", null) as string);
        bool running;
        using (var service = new System.ServiceProcess.ServiceController("W32Time"))
        {
            try { running = service.Status == System.ServiceProcess.ServiceControllerStatus.Running; }
            catch (InvalidOperationException) { running = false; }
        }
        // The running service names its current source. A stopped one (Windows Time starts on
        // demand outside a domain) is normal: then a configured time server is read from the
        // service's own setting. "w32tm /stripchart" measures without the service.
        var source = running ? SourceHost(ProbeTools.Run("w32tm.exe", "/query /source", Limit)) : null;
        if (source == null && kind == "ntp") { source = SourceHost(FirstNtpServer(Registry.GetValue(parameters, "NtpServer", null) as string)); }
        if (source == null)
        {
            return new ClockReadout { Source = kind == "unknown" ? "local" : kind, Unavailable = running || kind != "domain" ? "no-source" : "not-running" };
        }
        // The host comes from the service's own configuration and is checked to be a plain host
        // name before it becomes an argument.
        var output = ProbeTools.Run("w32tm.exe", "/stripchart /computer:" + source + " /dataonly /samples:1", Limit);
        var offset = ParseOffset(output);
        return offset == null
            ? new ClockReadout { Source = kind, Unavailable = "no-answer" }
            : new ClockReadout { Source = kind, OffsetSeconds = offset };
    }

    /// <summary>The first server of the NtpServer setting ("time.windows.com,0x9 other.example,0x1").</summary>
    internal static string FirstNtpServer(string setting)
        => string.IsNullOrWhiteSpace(setting) ? null : setting.Split(Spaces, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    /// <summary>The time service's configured type: NT5DS is the domain hierarchy, NTP and AllSync a configured server, NoSync none.</summary>
    internal static string SourceKind(string type) => (type ?? "").Trim().ToUpperInvariant() switch
    {
        "NT5DS" => "domain",
        "NTP" or "ALLSYNC" => "ntp",
        "NOSYNC" => "none",
        _ => "unknown",
    };

    /// <summary>
    /// The host of "w32tm /query /source" ("time.windows.com,0x9" -> "time.windows.com"), or null
    /// when the source is not a host (the local CMOS clock, the free-running clock, a VM
    /// provider: these come as localized words).
    /// </summary>
    internal static string SourceHost(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) { return null; }
        var line = output.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line == null) { return null; }
        var host = line.Split(',')[0].Trim();
        return host.Contains('.') && TimedRegex.IsMatch(HostName, host) ? host : null;
    }

    /// <summary>The offset of "hh:mm:ss, +00.0123456s" in seconds (either decimal separator); null without one.</summary>
    internal static double? ParseOffset(string output)
    {
        if (string.IsNullOrEmpty(output)) { return null; }
        var m = TimedRegex.MatchOrEmpty(Offset, output);
        if (!m.Success) { return null; }
        return double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
    }
}

/// <summary>MDM policy settings that failed in the last 7 days.</summary>
internal static class ProfileErrorsProbe
{
    internal const string Provider = "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider";
    private const string AdminLog = Provider + "/Admin";
    internal const int MaxSettings = 10;
    private const int MaxEvents = 2000;
    private static readonly string[] PolicyPrefixes = { "Policy/Config/", "Policy/Result/" };

    public static ProfileErrorsReadout Read(DateTime nowUtc, MachineReadout machine = null)
    {
        // Microsoft documents that this log shows policy failures with the setting's CSP path and
        // to filter on its levels, not what each event ID means: so the levels are counted.
        var query = new EventLogQuery(AdminLog, PathType.LogName,
            $"*[System[Provider[@Name='{Provider}'] and (Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= 604800000]]]")
        { ReverseDirection = true };
        var events = new List<(int Level, int? ProcessId, IEnumerable<object> Values, DateTime? Utc)>();
        try
        {
            using var reader = new EventLogReader(query);
            for (var i = 0; i < MaxEvents; i++)
            {
                using var record = reader.ReadEvent();
                if (record == null) { break; }
                events.Add((record.Level ?? 0, record.ProcessId, record.Properties.Select(p => p.Value).ToList(), record.TimeCreated?.ToUniversalTime()));
            }
        }
        catch (EventLogNotFoundException)
        {
            return new ProfileErrorsReadout();
        }
        return SummarizeTimed(events, WrittenBySessionZero, MachineProbe.NoWindowsHello(machine));
    }

    /// <summary>The counts and the failed settings, from events without a time (tests and older callers).</summary>
    internal static ProfileErrorsReadout Summarize(IEnumerable<(int Level, int? ProcessId, IEnumerable<object> Values)> events, Func<int?, bool> trusted, bool noWindowsHello = false)
        => SummarizeTimed(events.Select(e => (e.Level, e.ProcessId, e.Values, (DateTime?)null)), trusted, noWindowsHello);

    /// <summary>
    /// The counts and the failed settings, newest first, from events of a trusted writer. On a
    /// machine without Windows Hello for Business (<paramref name="noWindowsHello"/>: a virtual
    /// machine or a multi-session host), its settings are kept apart as not applicable.
    /// </summary>
    internal static ProfileErrorsReadout SummarizeTimed(IEnumerable<(int Level, int? ProcessId, IEnumerable<object> Values, DateTime? Utc)> events, Func<int?, bool> trusted, bool noWindowsHello = false)
    {
        var result = new ProfileErrorsReadout();
        foreach (var (level, pid, values, utc) in events)
        {
            // The same rule as for the sync events: a writer in a user session is a user.
            if (!trusted(pid)) { continue; }
            var path = values.OfType<string>().FirstOrDefault(v => v.StartsWith("./", StringComparison.Ordinal));
            // Windows reports these on every enrolled device without a consequence: not counted.
            if (KnownHarmless(path)) { result.KnownHarmless++; continue; }
            if (level == 3) { result.Warnings++; continue; }
            var setting = path == null ? null : ShortSetting(path);
            var list = result.Settings;
            if (noWindowsHello && WindowsHelloSetting(path))
            {
                result.NotApplicableErrors++;
                list = result.NotApplicable;
            }
            else
            {
                result.Errors++;
            }
            if (setting != null && list.Count < MaxSettings && !list.Contains(setting, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(setting);
                // The events come newest first: this is when the setting last failed.
                if (ReferenceEquals(list, result.Settings)) { result.SettingTimes.Add(utc ?? DateTime.MinValue); }
                // The newest failure of a setting decides whether it failed on removal.
                if (IsRemoval(values)) { result.Removals.Add(setting); }
            }
        }
        return result;
    }

    /// <summary>
    /// The event's command type is a removal: the MDM client deletes a setting in two phases
    /// ("Clear: first phase of Delete", then the Delete), where an assignment is "Add: from
    /// Replace or Add".
    /// </summary>
    internal static bool IsRemoval(IEnumerable<object> values)
        => values.OfType<string>().Select(v => v.Trim(' ', '(', ')')).Any(v =>
            (v.StartsWith("Clear", StringComparison.OrdinalIgnoreCase) || v.StartsWith("Delete", StringComparison.OrdinalIgnoreCase))
            && v.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>A Windows Hello for Business setting (the PassportForWork CSP, device or user).</summary>
    internal static bool WindowsHelloSetting(string path)
        => path != null && path.IndexOf("/Vendor/MSFT/PassportForWork/", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// CSP paths whose errors Windows writes on every enrolled device without a consequence, so
    /// they are not counted as failed settings (they stay in the support package, marked). The paths
    /// and their reasons:
    /// the ADMX ingestion receiver's "FakePolicy" (written by Windows itself, never assigned by an
    /// administrator), Windows Autopilot device preparation's page status (errors on a
    /// device that does not use device preparation), and the MDM client's certificate pinning
    /// rules (a DMClient node the service addresses on every device; not an assigned setting).
    /// </summary>
    internal static readonly string[] KnownHarmlessPaths =
    {
        "/Policy/ConfigOperations/ADMXInstall/Receiver/Properties/Policy/FakePolicy/",
        "/DevicePreparation/PageStatus",
        "/CertPinning/PinRules",
    };

    internal static bool KnownHarmless(string path)
        => path != null && KnownHarmlessPaths.Any(p => path.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
    /// <summary>The longest setting token; a longer path loses its leading segments, never part of a word.</summary>
    internal const int MaxSettingLength = 80;

    private static readonly Regex GuidSegment = new(@"^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// A CSP path such as "./Device/Vendor/MSFT/Policy/Config/BitLocker/RequireDeviceEncryption"
    /// as "BitLocker/RequireDeviceEncryption"; null for a value that is not a CSP path. A
    /// segment that is a GUID (such as the tenant ID in a Windows Hello path) is left out, and a
    /// path longer than <see cref="MaxSettingLength"/> loses its leading segments, so it never
    /// ends in the middle of a word.
    /// </summary>
    internal static string ShortSetting(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith("./", StringComparison.Ordinal)) { return null; }
        var path = value;
        var vendor = path.IndexOf("/Vendor/MSFT/", StringComparison.OrdinalIgnoreCase);
        if (vendor >= 0) { path = path.Substring(vendor + "/Vendor/MSFT/".Length); }
        foreach (var prefix in PolicyPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { path = path.Substring(prefix.Length); }
        }
        var segments = path.Split('/')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && s != "." && !GuidSegment.IsMatch(s))
            .Select(s => ProbeTools.Token(s, int.MaxValue))
            .ToList();
        if (segments.Count == 0) { return null; }
        while (segments.Count > 1 && string.Join("/", segments).Length > MaxSettingLength) { segments.RemoveAt(0); }
        var token = string.Join("/", segments);
        return token.Length <= MaxSettingLength ? token : WordStart(token, MaxSettingLength);
    }

    /// <summary>
    /// One segment longer than the limit: cut before the last word that still fits (a capital
    /// letter, a digit run or a separator starts a word), never inside one.
    /// </summary>
    internal static string WordStart(string token, int max)
    {
        for (var i = max; i > 0; i--)
        {
            var c = token[i];
            var previous = token[i - 1];
            if (c == '_' || c == '-' || c == '.' || (char.IsUpper(c) && !char.IsUpper(previous)) || (char.IsDigit(c) && !char.IsDigit(previous)))
            {
                return token.Substring(0, i).TrimEnd('_', '-', '.');
            }
        }
        return token.Substring(0, max);
    }

    /// <summary>
    /// The writer still runs in session 0, or has exited (cannot be checked; accepted, as in the
    /// engine). User-scoped MDM sessions also write from the user's session; those events are left
    /// out, because a user can write to the channel too.
    /// </summary>
    internal static bool WrittenBySessionZero(int? processId)
    {
        if (processId == null || processId <= 0) { return true; }
        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return process.SessionId == 0;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }
}

/// <summary>The Intune enrollment's scheduled tasks.</summary>
internal static class IntuneTasksProbe
{
    public static IntuneConnectionReadout Read()
    {
        var id = IntuneEnrollmentId();
        if (id == null) { return new IntuneConnectionReadout { Unavailable = "not-enrolled" }; }
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true);
        var service = Activator.CreateInstance(type);
        var tasks = new List<(bool Enabled, DateTime LastRunLocal, int LastResult)>();
        var disabled = new List<string>();
        try
        {
            ProbeTools.Com(service, "Connect", BindingFlags.InvokeMethod);
            object folder;
            try { folder = ProbeTools.Com(service, "GetFolder", BindingFlags.InvokeMethod, @"\Microsoft\Windows\EnterpriseMgmt\" + id); }
            catch (Exception) { return Summarize(tasks, disabled); }
            var collection = ProbeTools.Com(folder, "GetTasks", BindingFlags.InvokeMethod, 1);
            foreach (var task in (System.Collections.IEnumerable)collection)
            {
                var enabled = ProbeTools.Com(task, "Enabled", BindingFlags.GetProperty) is bool e && e;
                var last = ProbeTools.Com(task, "LastRunTime", BindingFlags.GetProperty) is DateTime d ? d : DateTime.MinValue;
                var result = ProbeTools.Com(task, "LastTaskResult", BindingFlags.GetProperty) is int r ? r : 0;
                tasks.Add((enabled, last, result));
                // The task's name only (IRegisteredTask.Name), never its path with the enrollment ID.
                if (!enabled) { disabled.Add(ProbeTools.Com(task, "Name", BindingFlags.GetProperty) as string); }
                Marshal.ReleaseComObject(task);
            }
            Marshal.ReleaseComObject(collection);
            Marshal.ReleaseComObject(folder);
        }
        finally
        {
            Marshal.ReleaseComObject(service);
        }
        return Summarize(tasks, disabled);
    }

    /// <summary>The most disabled task names the readout carries.</summary>
    internal const int MaxDisabledNames = 5;

    private static readonly Regex TaskNameOther = new(@"[^A-Za-z0-9 #._()-]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex GuidText = new(@"\{?[0-9A-Fa-f]{8}-(?:[0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}\}?", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// A task's name as plain text: letters, digits, spaces and # . _ ( ) -, without a GUID, at
    /// most 80 characters; null when nothing is left.
    /// </summary>
    internal static string TaskName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) { return null; }
        try
        {
            var clean = TaskNameOther.Replace(GuidText.Replace(name, ""), "_");
            while (clean.Contains("  ")) { clean = clean.Replace("  ", " "); }
            clean = clean.Trim('_', ' ');
            if (clean.Length > 80) { clean = clean.Substring(0, 80).TrimEnd(); }
            return clean.Length == 0 ? null : clean;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    internal static IntuneConnectionReadout Summarize(IReadOnlyCollection<(bool Enabled, DateTime LastRunLocal, int LastResult)> tasks, IEnumerable<string> disabledNames = null)
    {
        var result = new IntuneConnectionReadout
        {
            Tasks = tasks.Count,
            TasksEnabled = tasks.Count(t => t.Enabled),
            DisabledTasks = (disabledNames ?? Enumerable.Empty<string>()).Select(TaskName).Where(n => n != null).Take(MaxDisabledNames).ToList(),
        };
        // A task that never ran reports a date around 1899 or 1999. A task that is running or
        // queued reports a Task Scheduler status, not a result: the newest finished run decides,
        // and a running one is shown only when no task has finished a run.
        var ran = tasks.Where(t => t.LastRunLocal.Year > 2000).OrderByDescending(t => t.LastRunLocal).ToList();
        var newest = ran.FirstOrDefault(t => !SchedulerBusy(t.LastResult));
        if (newest.LastRunLocal.Year <= 2000) { newest = ran.FirstOrDefault(); }
        if (newest.LastRunLocal.Year > 2000)
        {
            result.LastRunUtc = newest.LastRunLocal.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            result.LastResult = "0x" + newest.LastResult.ToString("X8", CultureInfo.InvariantCulture);
        }
        return result;
    }

    /// <summary>SCHED_S_TASK_RUNNING (0x00041301) and SCHED_S_TASK_QUEUED (0x00041325): the task has not finished.</summary>
    internal static bool SchedulerBusy(int lastResult) => lastResult == 0x00041301 || lastResult == 0x00041325;

    /// <summary>The one active Intune enrollment ("MS DM Server", state 1), or null when there is none or more than one.</summary>
    internal static string IntuneEnrollmentId()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments");
        if (root == null) { return null; }
        var found = new List<string>();
        foreach (var name in root.GetSubKeyNames())
        {
            if (!Guid.TryParseExact(name, "D", out _)) { continue; }
            using var key = root.OpenSubKey(name);
            if (key == null) { continue; }
            if (string.Equals(key.GetValue("ProviderID") as string, "MS DM Server", StringComparison.Ordinal) && key.GetValue("EnrollmentState") as int? == 1)
            {
                found.Add(name);
            }
        }
        return found.Count == 1 ? found[0] : null;
    }
}
