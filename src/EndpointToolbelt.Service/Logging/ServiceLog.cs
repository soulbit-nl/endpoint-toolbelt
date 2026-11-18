using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Service.Logging;

public interface IServiceLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

public interface IEventWriter
{
    void Write(int eventId, EventLogEntryType type, string message);
}

/// <summary>
/// The service's CMTrace-format log (see <see cref="CmTraceFile"/>, shared with the engine).
/// A write that fails is dropped: logging must never break the service.
/// </summary>
public sealed class CmTraceLog : IServiceLog
{
    public const long MaxBytes = CmTraceFile.MaxBytes;

    private readonly CmTraceFile _file;
    private readonly string _component;

    public CmTraceLog(string path, string component, long maxBytes = MaxBytes)
    {
        _file = new CmTraceFile(path, maxBytes);
        _component = component;
    }

    public string FilePath => _file.FilePath;

    public void Info(string message) => Write(message, 1);
    public void Warn(string message) => Write(message, 2);
    public void Error(string message) => Write(message, 3);

    private void Write(string message, int type)
    {
        try { _file.Append(message, type, _component, DateTimeOffset.Now, Environment.CurrentManagedThreadId); }
        catch (Exception) { /* Logging must never break the service. */ }
    }
}

/// <summary>
/// Writes to the product's event log. The MSI registers the log and the source; this class never
/// creates them. Entries go through ReportEvent (RegisterEventSource), which never registers a
/// source, unlike <see cref="EventLog.WriteEntry(string, string, EventLogEntryType, int)"/>. As
/// ReportEvent falls back to the Application log for an unknown source, entries are written only
/// while the source is registered under the product's own log, checked every
/// <see cref="SourceRecheck"/>. Otherwise the event is skipped; the CMTrace service log still has
/// the line.
/// </summary>
public sealed class EventLogWriter : IEventWriter
{
    /// <summary>How long a check of the source registration (found or missing) is trusted.</summary>
    public static readonly TimeSpan SourceRecheck = TimeSpan.FromMinutes(10);

    /// <summary>Longest message written, like EventLog.WriteEntry's own limit.</summary>
    public const int MaxMessageLength = 31839;

    public static string SourceKeyPath =>
        $@"SYSTEM\CurrentControlSet\Services\EventLog\{ProductInfo.EventLogName}\{ProductInfo.EventSource}";

    private readonly Func<string, bool> _keyExists;
    private readonly Action<string, string, EventLogEntryType, int> _write;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private bool? _registered;
    private DateTime _checkedUtc;

    /// <param name="keyExists">Whether an HKLM key exists (64-bit view); replaced in tests.</param>
    /// <param name="write">Writes one entry (source, message, type, ID); replaced in tests.</param>
    public EventLogWriter(Func<string, bool> keyExists = null, Action<string, string, EventLogEntryType, int> write = null, Func<DateTime> utcNow = null)
    {
        _keyExists = keyExists ?? HklmKeyExists;
        _write = write ?? ReportEvent;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public void Write(int eventId, EventLogEntryType type, string message)
    {
        try
        {
            if (!IsSourceRegistered()) { return; }
            message ??= "";
            if (message.Length > MaxMessageLength) { message = message.Substring(0, MaxMessageLength); }
            _write(ProductInfo.EventSource, message, type, eventId);
        }
        catch (Exception)
        {
            // Events must never break the service.
        }
    }

    private bool IsSourceRegistered()
    {
        lock (_lock)
        {
            // Checked again now and then either way: a repair may add the source, an uninstall
            // removes it.
            var now = _utcNow();
            if (_registered.HasValue && now - _checkedUtc < SourceRecheck) { return _registered.Value; }
            try { _registered = _keyExists(SourceKeyPath); }
            catch (Exception) { _registered = false; }
            _checkedUtc = now;
            return _registered.Value;
        }
    }

    private static bool HklmKeyExists(string subKey)
    {
        using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var key = hklm.OpenSubKey(subKey, writable: false);
        return key != null;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterEventSource(string uncServerName, string sourceName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeregisterEventSource(IntPtr eventLog);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReportEventW(IntPtr eventLog, ushort type, ushort category, uint eventId, IntPtr userSid,
        ushort numStrings, uint dataSize, string[] strings, IntPtr rawData);

    /// <summary>One entry with one insertion string, as EventLog.WriteEntry writes it.</summary>
    private static void ReportEvent(string source, string message, EventLogEntryType type, int eventId)
    {
        var handle = RegisterEventSource(null, source);
        if (handle == IntPtr.Zero) { return; }
        try
        {
            ReportEventW(handle, (ushort)type, 0, (uint)eventId, IntPtr.Zero, 1, 0, new[] { message }, IntPtr.Zero);
        }
        finally
        {
            DeregisterEventSource(handle);
        }
    }
}
/// <summary>
/// Chooses where logs go. The IME Logs folder belongs to another product, so it is re-checked
/// at most every <see cref="RecheckInterval"/>; when it fails the check, logs go to the
/// service's SYSTEM-only fallback folder and an event is written once per change.
/// </summary>
public sealed class LogLocation
{
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(1);

    private readonly string _preferred;
    private readonly string _fallback;
    private readonly Func<string, string> _validate;
    private readonly Action<string> _onRejected;
    private readonly object _lock = new();
    private readonly Stopwatch _sinceCheck = new();
    private string _current;
    private string _lastReason;

    public LogLocation(string preferred, string fallback, Action<string> onRejected, Func<string, string> validate = null)
    {
        _preferred = preferred;
        _fallback = fallback;
        _onRejected = onRejected;
        _validate = validate ?? FileSystemSafety.ValidateForeignFolder;
    }

    public static string ImeLogsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "IntuneManagementExtension", "Logs");

    /// <summary>The folder to write to now.</summary>
    public string Current
    {
        get
        {
            lock (_lock)
            {
                if (_current != null && _sinceCheck.Elapsed < RecheckInterval) { return _current; }
                var reason = _validate(_preferred);
                _current = reason == null ? _preferred : _fallback;
                _sinceCheck.Restart();
                if (reason != null && reason != _lastReason) { _onRejected?.Invoke(reason); }
                _lastReason = reason;
                return _current;
            }
        }
    }
}

/// <summary>A CMTrace log whose folder is chosen by <see cref="LogLocation"/> at every write.</summary>
public sealed class FolderCheckedLog : IServiceLog
{
    private readonly LogLocation _location;
    private readonly string _fileName;
    private readonly string _component;
    private readonly object _lock = new();
    private CmTraceLog _log;

    public FolderCheckedLog(LogLocation location, string fileName, string component)
    {
        _location = location;
        _fileName = fileName;
        _component = component;
    }

    private CmTraceLog Target
    {
        get
        {
            var path = Path.Combine(_location.Current, _fileName);
            lock (_lock)
            {
                if (_log == null || !string.Equals(_log.FilePath, path, StringComparison.OrdinalIgnoreCase)) { _log = new CmTraceLog(path, _component); }
                return _log;
            }
        }
    }

    public void Info(string message) => Target.Info(message);
    public void Warn(string message) => Target.Warn(message);
    public void Error(string message) => Target.Error(message);
}
