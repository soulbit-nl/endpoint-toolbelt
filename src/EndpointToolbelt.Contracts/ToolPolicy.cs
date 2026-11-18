using System;
using System.Collections.Generic;
using System.Linq;

namespace EndpointToolbelt;

/// <summary>
/// The tools of For IT support an organization can switch off with the policy value
/// <c>HKLM\SOFTWARE\Policies\EndpointToolbelt\DisabledTools</c>, and how that value is read. The
/// service refuses a switched-off service tool to a user (the trusted service caller may still run
/// it); the app hides every switched-off tool. Without the value nothing is switched off.
/// </summary>
public static class ToolPolicy
{
    /// <summary>The policy value (under <see cref="ProductInfo.PolicyKey"/> only): REG_MULTI_SZ, or REG_SZ with names separated by commas or semicolons.</summary>
    public const string ValueName = "DisabledTools";

    /// <summary>At most this many entries are read; the rest are ignored with a warning.</summary>
    public const int MaxEntries = 64;

    /// <summary>A longer entry is not a tool name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The nine tools that run in the service, by their contract action names.</summary>
    public static readonly IReadOnlyList<ToolbeltAction> ServiceTools = new[]
    {
        ToolbeltAction.ClearAppCache,
        ToolbeltAction.FullReset,
        ToolbeltAction.UpdateDefender,
        ToolbeltAction.BackupRecoveryKey,
        ToolbeltAction.SyncClock,
        ToolbeltAction.CheckWindowsUpdate,
        ToolbeltAction.RepairWU,
        ToolbeltAction.FreeDiskSpace,
        ToolbeltAction.RenewIpAddress,
    };

    // The thirteen tools that run in the app, as the signed-in user, by names of their own.
    public const string OpenImeLogs = "OpenImeLogs";
    public const string ResetCompanyPortal = "ResetCompanyPortal";
    public const string RefreshSignIn = "RefreshSignIn";
    public const string ResetSignInCache = "ResetSignInCache";
    public const string ClearTeamsCache = "ClearTeamsCache";
    public const string WordSafeMode = "WordSafeMode";
    public const string ExcelSafeMode = "ExcelSafeMode";
    public const string OutlookSafeMode = "OutlookSafeMode";
    public const string PowerPointSafeMode = "PowerPointSafeMode";
    public const string CheckConnections = "CheckConnections";
    public const string BringWindowBack = "BringWindowBack";
    public const string CheckCompliance = "CheckCompliance";
    public const string ResetOneDrive = "ResetOneDrive";

    /// <summary>The tools that run in the app: the policy hides them, but cannot stop a user from doing the same by hand.</summary>
    public static readonly IReadOnlyList<string> AppTools = new[]
    {
        OpenImeLogs, ResetCompanyPortal, RefreshSignIn, ResetSignInCache, ClearTeamsCache,
        WordSafeMode, ExcelSafeMode, OutlookSafeMode, PowerPointSafeMode,
        CheckConnections, BringWindowBack, CheckCompliance, ResetOneDrive,
    };

    /// <summary>Every name the policy value knows: the service tools first, then the app tools.</summary>
    public static readonly IReadOnlyList<string> AllTools = ServiceTools.Select(a => a.ToString()).Concat(AppTools).ToArray();

    /// <summary>The service action is a tool an organization can switch off (Retry work apps and settings, the support package and the app diagnosis are not).</summary>
    public static bool CanSwitchOff(ToolbeltAction action) => ServiceTools.Contains(action);

    /// <summary>The tool with this name is in the list of switched-off tools (case-insensitive); false for a null list.</summary>
    public static bool IsOff(IEnumerable<string> switchedOff, string name)
        => switchedOff != null && !string.IsNullOrEmpty(name) && switchedOff.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The service action is a tool and in the list of switched-off tools.</summary>
    public static bool IsOff(IEnumerable<string> switchedOff, ToolbeltAction action)
        => CanSwitchOff(action) && IsOff(switchedOff, action.ToString());

    /// <summary>
    /// The switched-off tools from the raw registry value: a string array (REG_MULTI_SZ) or a string
    /// (REG_SZ), each entry possibly holding several names separated by commas or semicolons. Names
    /// are matched case-insensitively and returned in their own spelling, each once, in the order of
    /// <see cref="AllTools"/>. Anything else (an unknown name, another value type) is ignored and
    /// described in <paramref name="warnings"/>, which never repeats the raw text of an entry that is
    /// not a plain name. Null (no value) and an empty value give an empty list.
    /// </summary>
    public static List<string> Parse(object raw, List<string> warnings)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (raw == null) { return new List<string>(); }
        IEnumerable<string> entries;
        switch (raw)
        {
            case string s: entries = new[] { s }; break;
            case string[] many: entries = many; break;
            default:
                warnings?.Add(ValueName + " (policy) is not a string or a list of strings; ignored.");
                return new List<string>();
        }
        var count = 0;
        foreach (var entry in entries)
        {
            if (entry == null) { continue; }
            foreach (var part in entry.Split(Separators, StringSplitOptions.None))
            {
                var name = part.Trim();
                if (name.Length == 0) { continue; }
                if (++count > MaxEntries)
                {
                    warnings?.Add(ValueName + " (policy) has more than " + MaxEntries + " entries; the rest are ignored.");
                    return Ordered(found);
                }
                var known = AllTools.FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
                if (known != null) { found.Add(known); continue; }
                warnings?.Add(IsPlainName(name)
                    ? ValueName + " (policy) names an unknown tool '" + name + "'; ignored."
                    : ValueName + " (policy) holds an entry that is not a tool name; ignored.");
            }
        }
        return Ordered(found);
    }

    private static readonly char[] Separators = { ',', ';' };

    private static List<string> Ordered(HashSet<string> found) => AllTools.Where(found.Contains).ToList();

    /// <summary>Letters and digits only, not too long: safe to repeat in a log line.</summary>
    private static bool IsPlainName(string name)
        => name.Length <= MaxNameLength && name.All(c => c < 128 && char.IsLetterOrDigit(c));
}
