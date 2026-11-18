using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace EndpointToolbelt.Engine.Ime;

/// <summary>One MSI line-of-business app as the MDM stack recorded it.</summary>
public sealed class MsiAppRow
{
    /// <summary>The MSI product code, lower case, without braces.</summary>
    public string ProductCode { get; set; } = "";

    /// <summary>"device" or "user" (the caller's own SID); never the SID itself.</summary>
    public string Scope { get; set; } = "";

    /// <summary>The name Windows Installer shows for an installed product; '' when it is not installed (yet).</summary>
    public string Name { get; set; } = "";

    public string Version { get; set; } = "";
    public int? Status { get; set; }

    /// <summary>LastError as 0x plus eight upper-case hex digits; '' for none.</summary>
    public string Code { get; set; } = "";

    public string Verdict { get; set; } = "";
    public string State { get; set; } = "";
    public bool Download { get; set; }

    /// <summary>When the MDM stack last wrote the app's key (UTC); null when unknown.</summary>
    public DateTime? ObservedUtc { get; set; }

    /// <summary>The host of the download address only (CurrentDownloadUrl), never its path or query.</summary>
    public string DownloadHost { get; set; } = "";

    public int? RetryIndex { get; set; }
    public int? RetryCount { get; set; }
}

public sealed class MsiAppReadout
{
    public bool Present { get; set; }
    public List<MsiAppRow> Rows { get; } = new();

    /// <summary>Other users' SIDs with MSI apps, and their app records: counted, never read.</summary>
    public int OtherScopes { get; set; }
    public int OtherRecords { get; set; }
}

/// <summary>
/// Line-of-business apps that Intune deploys without the IME, read-only.
/// <para>
/// MSI apps go through the EnterpriseDesktopAppManagement CSP (learn.microsoft.com, "EnterpriseDesktopAppManagement
/// CSP": the MSI/{ProductID} nodes Status, LastError, Version, and the Status values 10 to 70). The MDM stack keeps
/// each app's state under HKLM\SOFTWARE\Microsoft\EnterpriseDesktopAppManagement\&lt;SID&gt;\MSI\{ProductCode}, the
/// device's under a SID that starts with S-0-0-00- and a user's under that user's SID (Microsoft Intune support
/// team, "Support Tip: Troubleshooting MSI App deployments in Microsoft Intune": Status, LastError,
/// CurrentDownloadUrl, EnforcementRetryIndex, EnforcementRetryCount). Only the device and the caller's own SID are
/// read; other users are counted. The download address is reduced to its host.
/// </para>
/// </summary>
public static class LobApps
{
    private static readonly Regex ProductCodeName = new(@"^\{?(" + ImeLog.GuidPattern + @")\}?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The MDM stack's device scope: a SID that starts with S-0-0-00-.</summary>
    public static bool IsDeviceScope(string sid) => (sid ?? "").StartsWith("S-0-0-00-", StringComparison.OrdinalIgnoreCase);

    /// <summary>The CSP's Status in words, with the verdict the app list uses and whether it is about the download.</summary>
    public static (string Verdict, string State, bool Download) Verdict(int? status, long code)
    {
        var meaning = CodeMeaning(code);
        var withCode = meaning.Length > 0 ? ": " + meaning : "";
        switch (status)
        {
            case 70: return ("OK", "installed (Windows Installer finished the install)", false);
            case 10: return ("IN-PROGRESS", "received, not started yet", false);
            case 20: return ("IN-PROGRESS", "downloading", false);
            case 25: return ("IN-PROGRESS", "the download failed; Windows tries it again by itself" + withCode, true);
            case 30: return ("RETRY-HELPS", "the download failed" + withCode, true);
            case 40: return ("IN-PROGRESS", "downloaded; the install has not started yet", false);
            case 48: return ("PENDING", "waits for the user to sign in", false);
            case 50: return ("IN-PROGRESS", "installing", false);
            case 55: return ("IN-PROGRESS", "the install failed; Windows tries it again by itself" + withCode, false);
            case 60:
                // 3010 and 1641: Windows Installer finished and asks for a restart.
                if (code is 0x80070BC2L or 0x80070669L) { return ("RESTART-FIRST", "installed; a restart finishes it" + withCode, false); }
                return ("RETRY-HELPS", "the install failed" + withCode, false);
            case null: return code != 0 ? ("UNKNOWN", "no status recorded" + withCode, false) : ("NO-STATE", "no status recorded", false);
            default: return ("UNKNOWN", "status " + status.Value.ToString(CultureInfo.InvariantCulture) + withCode, false);
        }
    }

    /// <summary>A code from the shared table in words ("MSI 1603, fatal error during installation"), else its hex form; '' for none.</summary>
    public static string CodeMeaning(long code)
    {
        if (code == 0) { return ""; }
        var text = code.ToString(CultureInfo.InvariantCulture);
        var entry = CodeKnowledge.Get(text);
        return entry != null ? entry.Name + " (" + CodeKnowledge.FormatHex(text) + ")" : "error " + CodeKnowledge.FormatHex(text);
    }

    public static MsiAppReadout ReadMsi(RunContext run)
    {
        var result = new MsiAppReadout();
        var registry = run.Services.Registry;
        var root = run.Services.ImePaths.DesktopAppsKey;
        if (string.IsNullOrEmpty(root) || !registry.KeyExists(root)) { return result; }
        result.Present = true;
        IReadOnlyList<string> sids;
        try { sids = registry.SubKeyNames(root); }
        catch (Exception) { return result; }
        foreach (var sid in sids)
        {
            var device = IsDeviceScope(sid);
            var own = !device && !string.IsNullOrEmpty(run.CallerSid) && string.Equals(sid, run.CallerSid, StringComparison.OrdinalIgnoreCase);
            var msiKey = root + "\\" + sid + "\\MSI";
            IReadOnlyList<string> products;
            try { products = registry.KeyExists(msiKey) ? registry.SubKeyNames(msiKey) : Array.Empty<string>(); }
            catch (Exception) { continue; }
            var apps = products.Where(p => ProductCodeName.IsMatch(p)).ToList();
            if (!device && !own)
            {
                if (apps.Count > 0) { result.OtherScopes++; result.OtherRecords += apps.Count; }
                continue;
            }
            foreach (var product in apps)
            {
                try { result.Rows.Add(ReadOne(run, msiKey + "\\" + product, product, device ? "device" : "user")); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
                {
                    run.Log("An MSI app record could not be read (" + ex.Message + ").", Platform.LogSeverity.Warning);
                }
            }
        }
        return result;
    }

    private static MsiAppRow ReadOne(RunContext run, string key, string product, string scope)
    {
        var registry = run.Services.Registry;
        var code = ProductCodeName.Match(product).Groups[1].Value.ToLowerInvariant();
        int? Number(string name) => StatusCode.Parse(MachineRegistry.AsText(registry.GetValue(key, name)) ?? "") is long n && n <= int.MaxValue ? (int)n : null;
        var status = Number("Status");
        var error = StatusCode.Parse(MachineRegistry.AsText(registry.GetValue(key, "LastError")) ?? "") ?? 0;
        var (verdict, state, download) = Verdict(status, error);
        var name = DisplayName(run, code);
        // A per-machine install recorded as finished whose product Windows no longer lists was
        // replaced (an upgrade with a new product code) or removed since: not installed. Only for the
        // device: a per-user product's uninstall entry is in that user's own hive, which is not read here.
        if (status == 70 && scope == "device" && name.Length == 0 && HasUninstallEntry(run, code) == false)
        {
            verdict = "NO-ACTION";
            state = NoLongerInstalled;
        }
        var url = MachineRegistry.AsText(registry.GetValue(key, "CurrentDownloadUrl")) ?? "";
        var version = (MachineRegistry.AsText(registry.GetValue(key, "ProductVersion")) ?? MachineRegistry.AsText(registry.GetValue(key, "Version")) ?? "").Trim();
        return new MsiAppRow
        {
            ProductCode = code,
            Scope = scope,
            Name = name,
            Version = version.Length <= 64 ? version : version.Substring(0, 64),
            Status = status,
            Code = error != 0 ? CodeKnowledge.FormatHex(error.ToString(CultureInfo.InvariantCulture)) : "",
            Verdict = verdict,
            State = state,
            Download = download,
            ObservedUtc = registry.KeyLastWriteUtc(key),
            DownloadHost = Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ? uri.Host : "",
            RetryIndex = Number("EnforcementRetryIndex"),
            RetryCount = Number("EnforcementRetryCount"),
        };
    }

    /// <summary>The state of a finished install (Status 70) whose product Windows Installer no longer lists.</summary>
    public const string NoLongerInstalled = "recorded as installed; Windows Installer no longer has it (replaced or removed)";

    /// <summary>
    /// Whether the product has an uninstall entry in either view: true or false, or null when that
    /// cannot be told (no uninstall keys to read, or a key that could not be read).
    /// </summary>
    internal static bool? HasUninstallEntry(RunContext run, string productCode)
    {
        var roots = run.Services.ImePaths.UninstallKeys ?? Array.Empty<string>();
        if (roots.Length == 0) { return null; }
        foreach (var root in roots)
        {
            try
            {
                if (run.Services.Registry.KeyExists(root + "\\{" + productCode.ToUpperInvariant() + "}")) { return true; }
            }
            catch (Exception)
            {
                return null;
            }
        }
        return false;
    }

    /// <summary>The name Windows Installer records for an installed product (DisplayName in the uninstall key of either view); '' when not installed.</summary>
    internal static string DisplayName(RunContext run, string productCode)
    {
        foreach (var root in run.Services.ImePaths.UninstallKeys ?? Array.Empty<string>())
        {
            try
            {
                var name = MachineRegistry.AsText(run.Services.Registry.GetValue(root + "\\{" + productCode.ToUpperInvariant() + "}", "DisplayName"));
                if (!string.IsNullOrWhiteSpace(name)) { return name.Trim().Length <= 200 ? name.Trim() : name.Trim().Substring(0, 200); }
            }
            catch (Exception)
            {
                // An unreadable key names nothing.
            }
        }
        return "";
    }

    /// <summary>
    /// The next step a WinGet code leads to, as a token for the app: "connectivity", "retry", "it",
    /// "restart", "wait" or "none"; '' for a code that is not WinGet's or not in the table.
    /// </summary>
    public static string WinGetNext(string code)
    {
        if (!IsWinGetCode(code)) { return ""; }
        return CodeKnowledge.Key(code) switch
        {
            "0x8a150008" or "0x8a15000f" or "0x8a150045" => "connectivity",
            "0x8a150011" or "0x8a15001e" => "retry",
            "0x8a15000b" or "0x8a150010" or "0x8a150014" or "0x8a15001b" or "0x8a15001c" or "0x8a150080" or "0x8a150082" or "0x8a15010f" or "0x8a150105" => "it",
            "0x8a150109" => "restart",
            "0x8a150101" or "0x8a150102" => "wait",
            "0x8a150061" => "none",
            _ => "",
        };
    }

    /// <summary>A code in WinGet's own range (0x8A15xxxx): the app came through the IME's WinGet path (Microsoft Store apps).</summary>
    public static bool IsWinGetCode(string code)
        => StatusCode.Parse(code ?? "") is long value && (value >> 16) == 0x8A15;

    /// <summary>
    /// The IME apps whose ID appears on a "[WinGetApp]" line of the IME's logs (the IME writes its
    /// WinGet handling for Microsoft Store apps with that tag; to be confirmed on a real device). Read
    /// by handle, newest logs first, bounded; an unreadable log names nothing.
    /// </summary>
    public static HashSet<string> WinGetAppIds(RunContext run, IEnumerable<string> appIds, int maxLogFiles = 4)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wanted = appIds.Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0) { return found; }
        System.IO.FileInfo[] Files(string filter)
        {
            try { return new System.IO.DirectoryInfo(run.Services.ImePaths.LogsFolder).GetFiles(filter); }
            catch (Exception) { return Array.Empty<System.IO.FileInfo>(); }
        }
        foreach (var log in Files("IntuneManagementExtension*.log").Concat(Files("AppWorkload*.log")).OrderByDescending(f => f.LastWriteTime).Take(maxLogFiles))
        {
            IReadOnlyList<string> lines;
            try { lines = Platform.SafeFileReader.FindLines(log.FullName, "WinGetApp"); }
            catch (Exception) { continue; }
            foreach (var line in lines)
            {
                foreach (var id in wanted) { if (line.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0) { found.Add(id); } }
            }
        }
        return found;
    }
}
