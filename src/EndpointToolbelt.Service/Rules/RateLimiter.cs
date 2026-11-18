using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace EndpointToolbelt.Service.Rules;

/// <summary>
/// Sliding-window limit per key (the caller's SID). Thread-safe. With a state file the hits
/// survive a service restart: a standard user can restart the service (an MSI repair does), and
/// that must not reset the limit.
/// </summary>
public sealed class RateLimiter
{
    // A hit stamped further ahead than this is from a clock that was set back; it is dropped.
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _window;
    private readonly string _statePath;
    private readonly Dictionary<string, Queue<DateTime>> _hits = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <param name="statePath">
    /// Optional file in the service's SYSTEM-only working folder that keeps the hits across
    /// restarts. A missing or unreadable file starts empty; a failed write keeps the limit in memory.
    /// </param>
    public RateLimiter(TimeSpan window, Func<DateTime> utcNow = null, string statePath = null)
    {
        _window = window;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _statePath = statePath;
        if (_statePath != null) { Load(); }
    }

    /// <summary>Records a hit and returns true when the key is still within <paramref name="limit"/>.</summary>
    public bool TryAcquire(string key, int limit)
    {
        var now = _utcNow();
        lock (_lock)
        {
            if (!_hits.TryGetValue(key, out var queue))
            {
                queue = new Queue<DateTime>();
                _hits[key] = queue;
            }
            while (queue.Count > 0 && now - queue.Peek() >= _window) { queue.Dequeue(); }
            if (queue.Count >= limit) { return false; }
            queue.Enqueue(now);

            // Keep the table small: drop keys that have no recent hits.
            if (_hits.Count > 1000)
            {
                var stale = new List<string>();
                foreach (var pair in _hits)
                {
                    if (pair.Value.Count == 0 || now - pair.Value.Peek() >= _window) { stale.Add(pair.Key); }
                }
                foreach (var k in stale) { _hits.Remove(k); }
            }
            if (_statePath != null) { Save(now); }
            return true;
        }
    }

    // One line per hit: "<key>\t<UTC ticks>".
    private void Load()
    {
        try
        {
            if (!File.Exists(_statePath)) { return; }
            var now = _utcNow();
            foreach (var line in File.ReadAllLines(_statePath, Encoding.UTF8))
            {
                var parts = line.Split('\t');
                if (parts.Length != 2 || parts[0].Length == 0 || parts[0].Length > 200) { continue; }
                if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) { continue; }
                if (ticks > DateTime.MaxValue.Ticks) { continue; }
                var hit = new DateTime(ticks, DateTimeKind.Utc);
                if (now - hit >= _window || hit - now > ClockSkew) { continue; }
                if (!_hits.TryGetValue(parts[0], out var queue))
                {
                    queue = new Queue<DateTime>();
                    _hits[parts[0]] = queue;
                }
                queue.Enqueue(hit);
            }
            // The file is written in order, but a damaged one may not be.
            foreach (var key in _hits.Keys.ToList()) { _hits[key] = new Queue<DateTime>(_hits[key].OrderBy(t => t)); }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _hits.Clear();
        }
    }

    private void Save(DateTime now)
    {
        try
        {
            var builder = new StringBuilder();
            foreach (var pair in _hits)
            {
                foreach (var hit in pair.Value)
                {
                    if (now - hit >= _window) { continue; }
                    builder.Append(pair.Key).Append('\t').Append(hit.Ticks.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                }
            }
            var temp = _statePath + ".tmp";
            File.WriteAllText(temp, builder.ToString(), new UTF8Encoding(false));
            if (File.Exists(_statePath)) { File.Replace(temp, _statePath, null); } else { File.Move(temp, _statePath); }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // The limit still holds in memory for this process.
        }
    }
}
