using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

namespace EndpointToolbelt.Service.Configuration;

/// <summary>Reads one registry value under HKLM (64-bit view). Abstracted for tests.</summary>
public interface IRegistryReader
{
    object GetValue(string subKey, string name);
}

public sealed class RegistryReader : IRegistryReader
{
    public object GetValue(string subKey, string name)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(subKey, writable: false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }
}

/// <summary>Effective configuration. Only the service reads configuration.</summary>
public sealed class ServiceConfig
{
    // Cooldowns: 0 switches one off. The per-user rate limit stays as the safety net.
    public int FullResetCooldownHours { get; set; } = 1;
    public int WURepairCooldownHours { get; set; } = 4;
    public int WUDeploymentDelayDays { get; set; } = 10;
    public int WUMinFreeGB { get; set; } = 15;
    public bool WURemoveStalePackages { get; set; }
    public string SupportText { get; set; } = "";
    public string SupportUrl { get; set; } = "";
    public int UserRateLimitPerHour { get; set; } = 20;
    public bool AllowMachineWideOnMultiSession { get; set; } = true;

    /// <summary>
    /// The Windows service an administrator trusts to call the service on behalf of IT support,
    /// or null. Read from the policy key only; while it is null the caller pipe does not exist.
    /// </summary>
    public string TrustedServiceCaller { get; set; }

    /// <summary>
    /// The tools the organization switched off (<see cref="ToolPolicy"/> names), from the policy key
    /// only; empty when none is off. The service refuses a switched-off service tool to a user.
    /// </summary>
    public IReadOnlyList<string> DisabledTools { get; set; } = Array.Empty<string>();

    /// <summary>Values that were present but invalid and therefore replaced by the default.</summary>
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Caches the configuration for a short time, so requests do not read the registry each time,
/// and reports warnings only when they change.
/// </summary>
public sealed class ConfigProvider
{
    private readonly IRegistryReader _reader;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _onWarning;
    private readonly object _lock = new();
    private ServiceConfig _cached;
    private DateTime _loadedUtc;
    private string _lastWarnings = "";

    public ConfigProvider(IRegistryReader reader, TimeSpan ttl, Action<string> onWarning, Func<DateTime> utcNow = null)
    {
        _reader = reader;
        _ttl = ttl;
        _onWarning = onWarning;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public ServiceConfig Get()
    {
        lock (_lock)
        {
            var now = _utcNow();
            if (_cached != null && now - _loadedUtc < _ttl) { return _cached; }
            _cached = ConfigLoader.Load(_reader);
            _loadedUtc = now;
            var warnings = string.Join("\n", _cached.Warnings);
            if (warnings != _lastWarnings)
            {
                _lastWarnings = warnings;
                foreach (var w in _cached.Warnings) { _onWarning?.Invoke(w); }
            }
            return _cached;
        }
    }
}

/// <summary>
/// Precedence: policy key, then config key (written by the MSI), then built-in default.
/// Values may be REG_SZ (MSI) or REG_DWORD (policy). Invalid values fall back to the default
/// and produce a warning; they never fall through to a lower-precedence source.
/// </summary>
public static class ConfigLoader
{
    public const int MaxSupportTextLength = 200;
    public const int MaxSupportUrlLength = 500;

    public static ServiceConfig Load(IRegistryReader reader)
    {
        var c = new ServiceConfig();
        c.FullResetCooldownHours = ReadInt(reader, c, "FullResetCooldownHours", c.FullResetCooldownHours, 0, 168);
        c.WURepairCooldownHours = ReadInt(reader, c, "WURepairCooldownHours", c.WURepairCooldownHours, 0, 720);
        c.WUDeploymentDelayDays = ReadInt(reader, c, "WUDeploymentDelayDays", c.WUDeploymentDelayDays, 1, 60);
        c.WUMinFreeGB = ReadInt(reader, c, "WUMinFreeGB", c.WUMinFreeGB, 1, 500);
        c.WURemoveStalePackages = ReadInt(reader, c, "WURemoveStalePackages", 0, 0, 1) == 1;
        c.UserRateLimitPerHour = ReadInt(reader, c, "UserRateLimitPerHour", c.UserRateLimitPerHour, 1, 60);
        c.AllowMachineWideOnMultiSession = ReadInt(reader, c, "AllowMachineWideOnMultiSession", 1, 0, 1) == 1;
        c.SupportText = ReadText(reader, c, "SupportText", MaxSupportTextLength, isUrl: false);
        c.SupportUrl = ReadText(reader, c, "SupportUrl", MaxSupportUrlLength, isUrl: true);
        c.TrustedServiceCaller = ReadServiceName(reader, c, ProductInfo.TrustedServiceCallerValue);
        // From the policy key only, as TrustedServiceCaller: the MSI never switches a tool off.
        c.DisabledTools = ToolPolicy.Parse(reader.GetValue(ProductInfo.PolicyKey, ToolPolicy.ValueName), c.Warnings);
        return c;
    }

    private static object ReadRaw(IRegistryReader reader, string name, out string source)
    {
        var value = reader.GetValue(ProductInfo.PolicyKey, name);
        if (value != null) { source = "policy"; return value; }
        value = reader.GetValue(ProductInfo.ConfigKey, name);
        source = value != null ? "config" : null;
        return value;
    }

    private static int ReadInt(IRegistryReader reader, ServiceConfig c, string name, int fallback, int min, int max)
    {
        var raw = ReadRaw(reader, name, out var source);
        if (raw == null) { return fallback; }
        // An empty string from the MSI means "not set".
        if (raw is string s && s.Trim().Length == 0) { return fallback; }
        if (TryToInt(raw, out var value) && value >= min && value <= max) { return value; }
        c.Warnings.Add($"{name} ({source}) is invalid or outside {min}..{max}; using {fallback}.");
        return fallback;
    }

    private static bool TryToInt(object raw, out int value)
    {
        switch (raw)
        {
            case int i: value = i; return true;
            case string s: return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            default: value = 0; return false;
        }
    }

    private static string ReadText(IRegistryReader reader, ServiceConfig c, string name, int maxLength, bool isUrl)
    {
        var raw = ReadRaw(reader, name, out var source);
        if (raw == null) { return ""; }
        if (raw is not string s)
        {
            c.Warnings.Add($"{name} ({source}) is not a string; ignored.");
            return "";
        }
        s = s.Trim();
        if (s.Length == 0) { return ""; }
        if (s.Length > maxLength || HasControlCharacters(s))
        {
            c.Warnings.Add($"{name} ({source}) is too long or contains control characters; ignored.");
            return "";
        }
        if (isUrl && !(Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
        {
            c.Warnings.Add($"{name} ({source}) is not an absolute https URL; ignored.");
            return "";
        }
        return s;
    }

    /// <summary>Service names Windows accepts and the SCM does not need quoting for.</summary>
    private static readonly System.Text.RegularExpressions.Regex ServiceNamePattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,79}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A service name from the policy key only: a value under the configuration key (which the
    /// MSI writes) is ignored, so only an administrator's policy can open the caller pipe.
    /// </summary>
    private static string ReadServiceName(IRegistryReader reader, ServiceConfig c, string name)
    {
        var raw = reader.GetValue(ProductInfo.PolicyKey, name);
        if (raw == null) { return null; }
        if (raw is not string s)
        {
            c.Warnings.Add($"{name} (policy) is not a string; ignored.");
            return null;
        }
        s = s.Trim();
        if (s.Length == 0) { return null; }
        if (!ServiceNamePattern.IsMatch(s))
        {
            c.Warnings.Add($"{name} (policy) is not a valid service name; ignored.");
            return null;
        }
        if (string.Equals(s, ProductInfo.ServiceName, StringComparison.OrdinalIgnoreCase))
        {
            c.Warnings.Add($"{name} (policy) names this service itself; ignored.");
            return null;
        }
        return s;
    }

    private static bool HasControlCharacters(string s)
    {
        // Format characters too: a bidirectional override could make the support text read
        // differently from what it is.
        foreach (var ch in s) { if (char.IsControl(ch) || char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format) { return true; } }
        return false;
    }
}
