using System;
using System.Text.RegularExpressions;

namespace EndpointToolbelt.Engine;

/// <summary>
/// The configuration snapshot of a run. The service always sends a validated snapshot; the
/// built-in defaults apply to keys that are missing or out of range, so a partial or malformed
/// request can never switch a guard off or push it out of range. A cooldown of 0 hours is a valid
/// setting (the administrator switched it off) and only counts when the key is present.
/// </summary>
public sealed class EngineSettings
{
    public int FullResetCooldownHours { get; private set; } = 1;
    public int WURepairCooldownHours { get; private set; } = 4;
    public int WUDeploymentDelayDays { get; private set; } = 10;
    public int WUMinFreeGB { get; private set; } = 15;
    public bool WURemoveStalePackages { get; private set; }

    /// <summary>
    /// Every known key present: numbers must be whole (not a fraction or a string) and within
    /// their range (a cooldown may also be 0); flags must be booleans. Anything else keeps the
    /// default. The largest sane value per key guards against a malformed request, not a wish
    /// for a decade-long cooldown.
    /// </summary>
    public static EngineSettings Resolve(JsonNode config)
    {
        var settings = new EngineSettings();
        settings.FullResetCooldownHours = Number(config, "fullResetCooldownHours", 0, 168, settings.FullResetCooldownHours);
        settings.WURepairCooldownHours = Number(config, "wuRepairCooldownHours", 0, 720, settings.WURepairCooldownHours);
        settings.WUDeploymentDelayDays = Number(config, "wuDeploymentDelayDays", 1, 60, settings.WUDeploymentDelayDays);
        settings.WUMinFreeGB = Number(config, "wuMinFreeGB", 1, 500, settings.WUMinFreeGB);
        var stale = config?.Get("wuRemoveStalePackages");
        if (stale != null && stale.Kind == JsonNode.NodeKind.Boolean) { settings.WURemoveStalePackages = stale.Text == "true"; }
        return settings;
    }

    private static int Number(JsonNode config, string key, int minimum, int maximum, int fallback)
    {
        var value = config?.Get(key)?.AsWholeNumber();
        return value.HasValue && value.Value >= minimum && value.Value <= maximum ? (int)value.Value : fallback;
    }
}

/// <summary>
/// The running Windows version, read from the device. ProductName still says "Windows 10" on
/// Windows 11, so the name is derived from the build number.
/// </summary>
public sealed class OsVersionInfo
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private static readonly Regex Windows10 = new("Windows 10", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public string DisplayVersion { get; private set; } = "";
    public string Build { get; private set; } = "";
    public string Ubr { get; private set; } = "";
    public string Edition { get; private set; } = "";
    public string ProductName { get; private set; } = "";
    public string Text { get; private set; } = "unknown";

    public static OsVersionInfo Read(IRegistry registry)
    {
        var info = new OsVersionInfo();
        try
        {
            if (!registry.KeyExists(CurrentVersionKey)) { return info; }
            string Value(string name) => MachineRegistry.AsText(registry.GetValue(CurrentVersionKey, name)) ?? "";
            info.DisplayVersion = Value("DisplayVersion");
            info.Build = Value("CurrentBuildNumber");
            info.Ubr = Value("UBR");
            info.Edition = Value("EditionID");
            var name = Value("ProductName");
            if (int.TryParse(info.Build, out var build) && build >= 22000) { name = Windows10.Replace(name, "Windows 11"); }
            info.ProductName = name;
            info.Text = (name + " " + info.DisplayVersion + ", build " + info.Build + "." + info.Ubr).Trim();
        }
        catch (Exception)
        {
            info.Text = "unknown";
        }
        return info;
    }
}
