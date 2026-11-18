using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace EndpointToolbelt.Service.Rules;

/// <summary>
/// Binds a confirmation to a status the caller was shown. Every status answer carries a
/// fresh nonce for its caller; a confirmed action must return one of that caller's recent
/// nonces, which is then spent. A client that never asked for the status, and so never showed
/// the warning, cannot confirm; a nonce issued to another account is worthless.
/// </summary>
public sealed class ConfirmationNonces
{
    /// <summary>How long a shown warning stays confirmable.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>Nonces kept per caller: the app refreshes the status while a dialog is open.</summary>
    public const int PerCaller = 8;

    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<(string Nonce, DateTime IssuedUtc)>> _issued = new(StringComparer.OrdinalIgnoreCase);

    public ConfirmationNonces(Func<DateTime> now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>A new nonce for <paramref name="sid"/>; the oldest is dropped beyond <see cref="PerCaller"/>.</summary>
    public string Issue(string sid)
    {
        if (string.IsNullOrEmpty(sid)) { throw new ArgumentException("A caller is needed.", nameof(sid)); }
        var bytes = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(bytes); }
        var nonce = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        lock (_lock)
        {
            if (!_issued.TryGetValue(sid, out var list))
            {
                list = new List<(string, DateTime)>();
                _issued[sid] = list;
            }
            Prune(list);
            list.Add((nonce, _now()));
            while (list.Count > PerCaller) { list.RemoveAt(0); }
        }
        return nonce;
    }

    /// <summary>True when <paramref name="nonce"/> was issued to <paramref name="sid"/> within the lifetime and not spent.</summary>
    public bool IsValid(string sid, string nonce)
    {
        if (string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(nonce)) { return false; }
        lock (_lock)
        {
            if (!_issued.TryGetValue(sid, out var list)) { return false; }
            Prune(list);
            return list.Exists(e => string.Equals(e.Nonce, nonce, StringComparison.Ordinal));
        }
    }

    /// <summary>Spends the nonce: true when it was valid. A spent nonce cannot confirm again.</summary>
    public bool Consume(string sid, string nonce)
    {
        if (string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(nonce)) { return false; }
        lock (_lock)
        {
            if (!_issued.TryGetValue(sid, out var list)) { return false; }
            Prune(list);
            var index = list.FindIndex(e => string.Equals(e.Nonce, nonce, StringComparison.Ordinal));
            if (index < 0) { return false; }
            list.RemoveAt(index);
            return true;
        }
    }

    private void Prune(List<(string Nonce, DateTime IssuedUtc)> list)
    {
        var cutoff = _now() - Lifetime;
        list.RemoveAll(e => e.IssuedUtc < cutoff);
    }
}