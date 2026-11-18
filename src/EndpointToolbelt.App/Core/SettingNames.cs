using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// A failed configuration profile setting in plain words. The service sends a short token
/// of the CSP path ("PassportForWork/Policies/PINComplexity/LowercaseLetters", without a GUID and
/// never cut inside a word); this turns it into "Windows Hello PIN: lowercase letters". A small
/// table names the common areas; any other path shows its last two segments with spaces between
/// the words ("PIN complexity: lowercase letters").
/// </summary>
public static class SettingNames
{
    /// <summary>The areas with a name of their own: the first segment of the token, and its name.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Areas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["PassportForWork"] = "Windows Hello",
        ["BitLocker"] = "BitLocker",
        ["Defender"] = "Microsoft Defender",
        ["Update"] = "Windows Update",
        ["DeviceLock"] = "Device lock",
        ["Firewall"] = "Firewall",
        ["LAPS"] = "Local administrator password",
        ["WiFi"] = "Wi-Fi",
        ["VPNv2"] = "VPN",
        ["DeviceGuard"] = "Device Guard",
        ["Browser"] = "Browser",
    };

    /// <summary>The stores of the RootCATrustedCertificates CSP, by their names in the path.</summary>
    internal static readonly IReadOnlyDictionary<string, string> CertificateStores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Root"] = "Trusted Root",
        ["CA"] = "Intermediate CA",
        ["TrustedPublisher"] = "Trusted Publisher",
        ["TrustedPeople"] = "Trusted People",
    };

    /// <summary>A certificate thumbprint: 40 (SHA-1) or 64 (SHA-256) hexadecimal characters.</summary>
    internal static bool IsThumbprint(string segment)
        => segment != null && (segment.Length == 40 || segment.Length == 64) && segment.All(Uri.IsHexDigit);

    /// <summary>A thumbprint shortened for the row: its first 8 characters in capitals and "…" (the report keeps it whole).</summary>
    internal static string ShortThumbprint(string thumbprint) => thumbprint.Substring(0, 8).ToUpperInvariant() + "…";

    /// <summary>
    /// A segment that is a code, not words (a long hexadecimal or base64 value, or one with many
    /// digits): never split into "words".
    /// </summary>
    internal static bool IsOpaque(string segment)
    {
        if (string.IsNullOrEmpty(segment) || segment.Length < 12) { return false; }
        if (segment.All(Uri.IsHexDigit) && segment.Any(char.IsDigit)) { return true; }
        var digits = segment.Count(char.IsDigit);
        return digits >= 4 || segment.IndexOf('+') >= 0 || segment.IndexOf('=') >= 0;
    }

    private static readonly char[] Slash = { '/' };

    /// <summary>Segments that only group settings inside an area, so they never name one.</summary>
    private static readonly HashSet<string> Grouping = new(StringComparer.OrdinalIgnoreCase) { "Policies", "Settings", "Config", "Profile", "Device", "User" };

    /// <summary>The setting in plain words; the token itself when it has no words to show.</summary>
    public static string Friendly(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) { return ""; }
        var segments = token.Split(Slash, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count == 0) { return token; }
        var last = segments[segments.Count - 1];
        // A certificate in a trusted store: "Trusted Publisher certificate ABCDEF01… (encoded
        // certificate)". The service may have dropped the leading "RootCATrustedCertificates" of a
        // long path, so the store's name before the thumbprint is enough.
        var thumbprint = segments.FindIndex(IsThumbprint);
        var storeAt = thumbprint >= 1 && CertificateStores.ContainsKey(segments[thumbprint - 1]) ? thumbprint - 1
            : segments[0].Equals("RootCATrustedCertificates", StringComparison.OrdinalIgnoreCase) && segments.Count > 1 ? 1 : -1;
        if (storeAt >= 0)
        {
            var store = CertificateStores.TryGetValue(segments[storeAt], out var storeName) ? storeName : Words(segments[storeAt], capitalFirst: true);
            var setting = thumbprint >= 0 && thumbprint < segments.Count - 1 ? " (" + Words(last, capitalFirst: false) + ")" : "";
            return store + " certificate" + (thumbprint >= 0 ? " " + ShortThumbprint(segments[thumbprint]) : "") + setting;
        }
        if (segments.Count > 1 && Areas.TryGetValue(segments[0], out var area))
        {
            // Windows Hello's PIN rules read as "Windows Hello PIN: lowercase letters".
            if (segments[0].Equals("PassportForWork", StringComparison.OrdinalIgnoreCase)
                && segments.Count > 2 && segments[segments.Count - 2].Equals("PINComplexity", StringComparison.OrdinalIgnoreCase))
            {
                return "Windows Hello PIN: " + Words(last, capitalFirst: false);
            }
            return area + ": " + Words(last, capitalFirst: false);
        }
        var named = segments.Where(s => !Grouping.Contains(s)).ToList();
        if (named.Count == 0) { named = segments; }
        if (named.Count == 1) { return Words(named[0], capitalFirst: true); }
        return Words(named[named.Count - 2], capitalFirst: true) + ": " + Words(named[named.Count - 1], capitalFirst: false);
    }

    /// <summary>The setting in plain words with its token for IT support: "Windows Hello PIN: lowercase letters (PassportForWork/...)".</summary>
    public static string WithToken(string token)
    {
        var friendly = Friendly(token);
        return friendly == token ? token : friendly + " (" + token + ")";
    }

    /// <summary>
    /// "PINComplexity" as "PIN complexity", "LowercaseLetters" as "lowercase letters": words split at
    /// a capital, a digit run or a separator; an abbreviation (two capitals or more) keeps its case.
    /// </summary>
    internal static string Words(string segment, bool capitalFirst)
    {
        // A thumbprint or another code is shown as it is, never split into "words".
        if (IsThumbprint(segment)) { return ShortThumbprint(segment); }
        if (IsOpaque(segment)) { return segment; }
        var words = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (c == '_' || c == '-' || c == '.' || c == ' ')
            {
                Flush();
                continue;
            }
            if (current.Length > 0)
            {
                var previous = segment[i - 1];
                var nextIsLower = i + 1 < segment.Length && char.IsLower(segment[i + 1]);
                var split = (char.IsUpper(c) && (char.IsLower(previous) || char.IsDigit(previous)))
                    || (char.IsUpper(c) && char.IsUpper(previous) && nextIsLower)
                    || (char.IsDigit(c) && !char.IsDigit(previous))
                    || (!char.IsDigit(c) && char.IsDigit(previous));
                if (split) { Flush(); }
            }
            current.Append(c);
        }
        Flush();
        if (words.Count == 0) { return segment; }
        // A long segment that splits into many tiny "words" is a code (such as base64), not words.
        if (segment.Length >= 12 && words.Count * 3 > segment.Length) { return segment; }
        var shown = words.Select(w => w.Length > 1 && w.All(ch => char.IsUpper(ch) || char.IsDigit(ch)) ? w : w.ToLowerInvariant()).ToList();
        if (capitalFirst) { shown[0] = char.ToUpperInvariant(shown[0][0]) + shown[0].Substring(1); }
        return string.Join(" ", shown);

        void Flush()
        {
            if (current.Length > 0) { words.Add(current.ToString()); }
            current.Clear();
        }
    }
}
