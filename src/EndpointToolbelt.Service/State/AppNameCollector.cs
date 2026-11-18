using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Service.State;

/// <summary>
/// While the app is closed nothing else reads the IME logs, so an
/// available app a user installs from Company Portal can rotate out of the log before its name is
/// kept. The service reads the policy lines of the newest IME logs once an hour in the background
/// (<see cref="AppNameCache.ReadPolicyNames"/>: the same lines, files and size bounds as the engine)
/// and updates the app name cache. Never more than once per <see cref="Interval"/>; a cycle is skipped
/// while one still runs; it runs on a timer thread, so it never holds up the pipe or an action. A
/// failure is logged once until a cycle works again, and is never fatal.
/// </summary>
public sealed class AppNameCollector : IDisposable
{
    /// <summary>The shortest time between two reads.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>How often the timer asks; a read happens only when <see cref="Interval"/> has passed.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    /// <summary>The first ask after the service started: not during the start itself.</summary>
    public static readonly TimeSpan FirstTick = TimeSpan.FromMinutes(2);

    private readonly Func<string> _logsFolder;
    private readonly string _cacheFile;
    private readonly Func<string, bool> _trustedFolder;
    private readonly Action<string> _warn;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<string, IEnumerable<KeyValuePair<string, string>>> _read;
    private readonly object _gate = new();
    private int _running;
    private DateTime? _lastStartUtc;
    private bool _failureLogged;
    private Timer _timer;

    /// <param name="logsFolder">The IME Logs folder, asked at each read.</param>
    /// <param name="cacheFile">The cache file in the SYSTEM-only working folder.</param>
    /// <param name="trustedFolder">Whether the cache's folder may be written (<see cref="AppNameCache.IsSystemOnlyFolder"/>).</param>
    /// <param name="warn">Where a failure is logged (once until a read works again).</param>
    /// <param name="utcNow">The clock (tests use their own).</param>
    /// <param name="read">The read of the policy lines (tests use their own); by default <see cref="AppNameCache.ReadPolicyNames"/>.</param>
    public AppNameCollector(Func<string> logsFolder, string cacheFile, Func<string, bool> trustedFolder, Action<string> warn,
        Func<DateTime> utcNow = null, Func<string, IEnumerable<KeyValuePair<string, string>>> read = null)
    {
        _logsFolder = logsFolder;
        _cacheFile = cacheFile;
        _trustedFolder = trustedFolder;
        _warn = warn ?? (_ => { });
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _read = read ?? (folder => AppNameCache.ReadPolicyNames(folder));
    }

    /// <summary>How many reads ran (tests).</summary>
    public int Cycles { get; private set; }

    /// <summary>Starts the background timer; the first read comes after <see cref="FirstTick"/>.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _timer ??= new Timer(_ => TryCollect(), null, FirstTick, Tick);
        }
    }

    /// <summary>
    /// Reads and updates the cache when <see cref="Interval"/> has passed since the last read began (or
    /// the clock went back) and no read runs now; otherwise does nothing. Returns whether it read.
    /// Never throws.
    /// </summary>
    public bool TryCollect()
    {
        var now = _utcNow();
        lock (_gate)
        {
            if (_lastStartUtc is DateTime last && now >= last && now - last < Interval) { return false; }
            if (_running != 0) { return false; }
            _running = 1;
            // Counted from the start: a slow read is not followed by another at once.
            _lastStartUtc = now;
        }
        try
        {
            var seen = (_read(_logsFolder?.Invoke()) ?? Enumerable.Empty<KeyValuePair<string, string>>()).Select(p => (p.Key, p.Value)).ToList();
            AppNameCache.Update(_cacheFile, seen, now, _trustedFolder);
            lock (_gate) { _failureLogged = false; }
        }
        catch (Exception ex)
        {
            var log = false;
            lock (_gate)
            {
                if (!_failureLogged) { _failureLogged = log = true; }
            }
            if (log)
            {
                try { _warn("The app name cache could not be updated from the IME logs (" + ex.GetType().Name + ": " + ex.Message + "); it is tried again in an hour."); }
                catch (Exception) { /* the log itself failed; the cache is a convenience */ }
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = 0;
                Cycles++;
            }
        }
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
