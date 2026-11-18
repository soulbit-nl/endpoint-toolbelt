using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;
using Microsoft.Win32;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// Microsoft 365 Apps as Click-to-Run reports it: read-only, from its own configuration
/// key, which every user can read. Microsoft documents no command line for the quick repair
/// (only an archived blog does), so the product does not run it; the report names the repair
/// in Settings instead.
/// </summary>
public sealed class OfficeInstall
{
    private static readonly Regex VersionPattern = new(@"^\d{1,3}(\.\d{1,6}){1,3}$", RegexOptions.CultureInvariant, TimedRegex.Limit);
    private static readonly Regex PlatformPattern = new("^(x86|x64|arm64)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimedRegex.Limit);

    public string Version { get; set; }
    public string Platform { get; set; }

    /// <summary>The update channel by name, or the raw value when it is not one Microsoft names.</summary>
    public string Channel { get; set; }

    /// <summary>
    /// The update channels by the GUID at the end of their update URL. Current, Monthly Enterprise and
    /// Semi-Annual Enterprise are listed in Microsoft's Intune documentation for Microsoft 365 Apps
    /// updates; the preview and beta GUIDs are only known from community sources, and any other value
    /// is shown as it is.
    /// </summary>
    private static readonly Dictionary<string, string> Channels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["492350f6-3a01-4f97-b9c0-c7c6ddf67d60"] = "Current Channel",
        ["64256afe-f5d9-4f86-8936-8840a6a4f5be"] = "Current Channel (Preview)",
        ["55336b82-a18d-4dd6-b5f6-9e5095c314a6"] = "Monthly Enterprise Channel",
        ["7ffbc6bf-bc32-4f92-8982-f9dd17fd3114"] = "Semi-Annual Enterprise Channel",
        ["b8f9b850-328d-4355-9145-c59439a0c4cf"] = "Semi-Annual Enterprise Channel (Preview)",
        ["5440fd1f-7ecb-4221-8110-145efaa6372f"] = "Beta Channel",
    };

    /// <summary>The GUID at the end of an update URL. The time limit keeps an odd registry value from stalling the report.</summary>
    private static readonly Regex ChannelGuid = new("([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The channel of an update URL (UpdateChannel or CDNBaseUrl): its name, or "other" with the value cut short.</summary>
    public static string ChannelName(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) { return "unknown"; }
        Match m;
        try { m = ChannelGuid.Match(url); }
        catch (RegexMatchTimeoutException) { m = Match.Empty; }
        if (m.Success && Channels.TryGetValue(m.Groups[1].Value, out var name)) { return name; }
        return "other (" + (m.Success ? m.Groups[1].Value : "not a known channel URL") + ")";
    }

    /// <summary>The installation from HKLM (64-bit view); null when Click-to-Run Microsoft 365 Apps are not installed.</summary>
    public static OfficeInstall Read()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
            if (key == null) { return null; }
            var version = key.GetValue("VersionToReport") as string;
            if (string.IsNullOrWhiteSpace(version)) { return null; }
            return new OfficeInstall
            {
                Version = TimedRegex.IsMatch(VersionPattern, version) ? version : "unknown",
                Platform = key.GetValue("Platform") as string is { } p && TimedRegex.IsMatch(PlatformPattern, p) ? p : "unknown",
                // UpdateChannel is set by policy and wins; CDNBaseUrl is the channel of the installation.
                Channel = ChannelName(key.GetValue("UpdateChannel") as string ?? key.GetValue("CDNBaseUrl") as string),
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }

    /// <summary>The device report's lines.</summary>
    public static IEnumerable<string> ReportLines(OfficeInstall office)
    {
        if (office == null)
        {
            yield return "Installed      : no (no Click-to-Run Microsoft 365 Apps)";
            yield break;
        }
        yield return "Installed      : " + office.Version + " (" + office.Platform + "), " + office.Channel;
        yield return "Repair         : Settings > Apps > Installed apps > Microsoft 365 > Modify > Quick Repair (needs an administrator)";
    }
}
