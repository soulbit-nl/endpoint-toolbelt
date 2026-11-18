using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using EndpointToolbelt.Platform;
using Microsoft.Win32;

namespace EndpointToolbelt.Engine;

/// <summary>The clock seam: wall-clock time for records and stamps, a monotonic clock for limits.</summary>
public interface IClock
{
    DateTime UtcNow { get; }

    /// <summary>The device's local time with its offset (the stamps in the State key carry it).</summary>
    DateTimeOffset Now { get; }

    /// <summary>Monotonic time since an arbitrary start; a clock change does not move it.</summary>
    TimeSpan Elapsed { get; }

    void Sleep(TimeSpan duration);
}

public sealed class SystemClock : IClock
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public DateTime UtcNow => DateTime.UtcNow;
    public DateTimeOffset Now => DateTimeOffset.Now;
    public TimeSpan Elapsed => _clock.Elapsed;
    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);
}

/// <summary>The registry seam: HKLM in the 64-bit view.</summary>
public interface IRegistry
{
    /// <summary>A value, or null when the key or the value is missing.</summary>
    object GetValue(string subKey, string name);

    bool KeyExists(string subKey);

    /// <summary>Writes a REG_SZ value, creating the key when it is missing.</summary>
    void SetString(string subKey, string name, string value);

    /// <summary>Removes a value; throws when the key or the value is missing.</summary>
    void DeleteValue(string subKey, string name);

    /// <summary>The names of a key's subkeys; throws when the key is missing or cannot be read.</summary>
    IReadOnlyList<string> SubKeyNames(string subKey);

    /// <summary>The names of a key's values; throws when the key is missing or cannot be read.</summary>
    IReadOnlyList<string> ValueNames(string subKey);

    /// <summary>Removes a key with all its subkeys; throws when it cannot.</summary>
    void DeleteKeyTree(string subKey);

    /// <summary>When the key was last written (UTC), or null when it is missing or cannot be read. Read-only.</summary>
    DateTime? KeyLastWriteUtc(string subKey);
}

public sealed class MachineRegistry : IRegistry
{
    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "RegQueryInfoKeyW")]
    private static extern int RegQueryInfoKey(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, IntPtr className, IntPtr classLength, IntPtr reserved,
        IntPtr subKeys, IntPtr maxSubKeyLength, IntPtr maxClassLength, IntPtr values, IntPtr maxValueNameLength, IntPtr maxValueLength,
        IntPtr securityDescriptor, out long lastWriteTime);

    public DateTime? KeyLastWriteUtc(string subKey)
    {
        try
        {
            using var hklm = Hklm();
            using var key = hklm.OpenSubKey(subKey, writable: false);
            if (key == null) { return null; }
            if (RegQueryInfoKey(key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var fileTime) != 0) { return null; }
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException or ArgumentException)
        {
            return null;
        }
    }

    private static RegistryKey Hklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public object GetValue(string subKey, string name)
    {
        using var hklm = Hklm();
        using var key = hklm.OpenSubKey(subKey, writable: false);
        return key?.GetValue(name);
    }

    public bool KeyExists(string subKey)
    {
        using var hklm = Hklm();
        using var key = hklm.OpenSubKey(subKey, writable: false);
        return key != null;
    }

    public void SetString(string subKey, string name, string value)
    {
        using var hklm = Hklm();
        using var key = hklm.CreateSubKey(subKey, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string subKey, string name)
    {
        using var hklm = Hklm();
        using var key = hklm.OpenSubKey(subKey, writable: true) ?? throw new ArgumentException("The key " + subKey + " does not exist.");
        key.DeleteValue(name, throwOnMissingValue: true);
    }

    public IReadOnlyList<string> SubKeyNames(string subKey)
    {
        using var hklm = Hklm();
        using var key = hklm.OpenSubKey(subKey, writable: false) ?? throw new ArgumentException("The key " + subKey + " does not exist.");
        return key.GetSubKeyNames();
    }

    public IReadOnlyList<string> ValueNames(string subKey)
    {
        using var hklm = Hklm();
        using var key = hklm.OpenSubKey(subKey, writable: false) ?? throw new ArgumentException("The key " + subKey + " does not exist.");
        return key.GetValueNames();
    }

    public void DeleteKeyTree(string subKey)
    {
        using var hklm = Hklm();
        hklm.DeleteSubKeyTree(subKey, throwOnMissingSubKey: true);
    }

    /// <summary>A registry value as text, the way the engine reads it (numbers in invariant digits).</summary>
    public static string AsText(object value) => value switch
    {
        null => null,
        string s => s,
        string[] lines => string.Join(" ", lines),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}

/// <summary>
/// What the engine reaches outside its own process through. The real implementations are the
/// defaults; tests replace them with hand-written fakes.
/// </summary>
public sealed class EngineServices
{
    /// <summary>Writes one record line to standard output.</summary>
    public Action<string> Output { get; set; } = _ => { };

    /// <summary>Writes one line to standard error (only when nothing else can report a problem).</summary>
    public Action<string> ErrorOutput { get; set; } = _ => { };

    public IClock Clock { get; set; } = new SystemClock();
    public IRegistry Registry { get; set; } = new MachineRegistry();
    public IProcessRunner Processes { get; set; } = new ProcessRunner();
    public IServiceControl ServiceControl { get; set; } = new WindowsServiceControl();
    public IEventLogReader EventLog { get; set; } = new WindowsEventLogReader();
    public IDeviceInfo Device { get; set; } = new WmiDeviceInfo();

    /// <summary>Free bytes on a drive ("C:"), or null when unknown.</summary>
    public Func<string, long?> FreeBytes { get; set; } = DeviceFacts.FreeBytes;

    /// <summary>When Windows last started, or null when unknown.</summary>
    public Func<DateTime?> BootUtc { get; set; } = DeviceFacts.BootUtc;

    /// <summary>True when a process with this name (without ".exe") runs.</summary>
    public Func<string, bool> IsProcessRunning { get; set; } = DeviceFacts.IsProcessRunning;

    public IMdmEventSource MdmEvents { get; set; } = new WindowsMdmEventSource();
    public IProcessControl ProcessControl { get; set; } = new WindowsProcessControl();
    public ITaskScheduler TaskScheduler { get; set; } = new WindowsTaskScheduler();
    public IWindowsUpdateSystem WindowsUpdate { get; set; } = new WindowsUpdateSystem();

    /// <summary>The network adapters (read) and the one call that renews an adapter's DHCP lease.</summary>
    public INetworkAdapters Network { get; set; } = new WmiNetworkAdapters();
    public ILeaseRenewer LeaseRenewer { get; set; } = new WmiNetworkAdapters();

    /// <summary>Runs a program without capturing its output: the exit code, or null when it did not finish in time.</summary>
    public Func<string, string, int, int?> RunAndWait { get; set; } = DetachedProcess.RunAndWait;

    /// <summary>The MSIX line-of-business app installs through the MDM WMI Bridge (read-only; tests use their own).</summary>
    public Ime.IModernAppSource ModernApps { get; set; } = new Ime.WmiModernAppSource();

    /// <summary>The IME's registry keys and folders (tests point them at their own).</summary>
    public Ime.ImePaths ImePaths { get; set; } = Ime.ImePaths.ForDevice();

    /// <summary>Whether a folder is the SYSTEM-only working folder the app name cache may be written in (tests accept their own).</summary>
    public Func<string, bool> TrustedStateFolder { get; set; } = EndpointToolbelt.Platform.AppNameCache.IsSystemOnlyFolder;

    /// <summary>Turns on RedirectionTrust enforcement for this process (it cannot be turned off again) and says whether it is on.</summary>
    public Func<bool> TurnOnRedirectionGuard { get; set; } = () =>
    {
        EndpointToolbelt.Platform.NativeFile.TryEnableRedirectionGuard();
        return EndpointToolbelt.Platform.NativeFile.IsRedirectionGuardEnabled();
    };

    /// <summary>Deletes one file below a user-writable root by handle (NativeFile); true when it was deleted.</summary>
    public Func<string, string, bool> DeleteFileBelow { get; set; } = EndpointToolbelt.Platform.NativeFile.DeleteFileBelowNoFollow;

    /// <summary>The running Windows version (logged at the start of every action).</summary>
    public Func<EngineServices, OsVersionInfo> ReadOsVersion { get; set; } = s => OsVersionInfo.Read(s.Registry);

    /// <summary>An environment variable of the engine process.</summary>
    public Func<string, string> GetEnvironmentVariable { get; set; } = Environment.GetEnvironmentVariable;

    /// <summary>The process ID written into every engine log record (CMTrace "thread").</summary>
    public int ProcessId { get; set; } = CurrentProcessId();

    private static int CurrentProcessId()
    {
        using var self = Process.GetCurrentProcess();
        return self.Id;
    }
}
