using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.Ime;

/// <summary>One app record of one scope, as the readout and the support package show it.</summary>
public sealed class AppStateRow
{
    public string ScopeRef { get; set; } = "";
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Revision { get; set; } = -1;
    public string Intent { get; set; } = "";
    public string Verdict { get; set; } = "";
    public string State { get; set; } = "";
    public string Installed { get; set; } = "";
    public int? EnforcementState { get; set; }
    public string ErrorCode { get; set; } = "";
    public string ErrorText { get; set; } = "";
    /// <summary>
    /// The IME's detection result in words ("Detected", "Not detected"): the ComplianceState of its
    /// compliance message says whether the detection rule found the app, not whether the device is compliant.
    /// </summary>
    public string ComplianceState { get; set; } = "";

    /// <summary>Whether Intune wants the app on this device: "Present", "Absent", or '' when the IME recorded neither.</summary>
    public string DesiredState { get; set; } = "";
    public string ImeStatus { get; set; } = "";
    public string ImeApplicability { get; set; } = "";
    public string ProductVersion { get; set; } = "";
    public string LastUpdatedUtc { get; set; } = "";
    public string EnforcementJson { get; set; } = "";
    public string ComplianceJson { get; set; } = "";

    /// <summary>
    /// When this revision has no install attempt of its own, the newest earlier revision of the
    /// same app and scope whose attempt failed (-1 when there is none), its error code (as
    /// <see cref="ErrorCode"/>) and when the IME last updated that record.
    /// </summary>
    public int EarlierRevision { get; set; } = -1;
    public string EarlierCode { get; set; } = "";
    public string EarlierUpdatedUtc { get; set; } = "";

    /// <summary>The scope's full ID (lower case), for the engine only: never in a table, a record or a log.</summary>
    public string ScopeId { get; set; } = "";

    /// <summary>When the record's key was last written (UTC), or null when unknown.</summary>
    public DateTime? KeyWrittenUtc { get; set; }
}

/// <summary>The app state of the scopes this user may see.</summary>
public sealed class AppStateReadout
{
    public bool Present { get; set; }
    public int Scopes { get; set; }
    public int OtherScopes { get; set; }
    public int OtherRecords { get; set; }
    public List<AppStateRow> Rows { get; } = new();

    /// <summary>App IDs that also have state in another user's scope.</summary>
    public HashSet<string> OtherAppIds { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>App names by app ID (lower case), and which of them came from the name cache rather than the current IME logs.</summary>
public sealed class AppNameLookup
{
    public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FromCache { get; } = new(StringComparer.Ordinal);

    /// <summary>How many names the cache holds after this lookup; 0 without a cache.</summary>
    public int CacheCount { get; set; }

    /// <summary>The cache file was there but could not be read; it was rebuilt.</summary>
    public bool CacheUnreadable { get; set; }

    /// <summary>"log", "cache", or "" when the app has no name.</summary>
    public string Source(string appId)
    {
        var id = (appId ?? "").ToLowerInvariant();
        return !Names.ContainsKey(id) ? "" : FromCache.Contains(id) ? "cache" : "log";
    }
}

/// <summary>What the IME last reported to Intune for one app and scope.</summary>
public sealed class AppStatusReport
{
    public string Status { get; set; } = "";
    public string Applicability { get; set; } = "";
    public long? ErrorCode { get; set; }
}

/// <summary>A failed app the IME retries on its own, with its estimated next attempt.</summary>
public sealed class AppRetry
{
    public string Name { get; set; }
    public string AppId { get; set; }
    public DateTime NextUtc { get; set; }
}

/// <summary>
/// Win32 app state readout (read-only). Reads the per-app state the IME keeps and classifies it,
/// so the log and the support package say which apps another attempt can fix and which it
/// cannot. Informational only: nothing here changes the device or decides an exit code.
/// </summary>
public static class AppState
{
    public const string DeviceScopeId = "00000000-0000-0000-0000-000000000000";

    /// <summary>The verdicts that need no attention, counted for the summary.</summary>
    private static readonly string[] OtherVerdicts = { "OK", "IN-PROGRESS", "PENDING", "NO-STATE", "AVAILABLE", "NO-ACTION" };

    /// <summary>0x87D1041C: the installer reported success and the detection rule then did not find the app.</summary>
    public const long NotDetectedCode = 2278622236;

    private static readonly Regex ScopeName = new("^" + ImeLog.GuidPattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex AppKeyName = new("^(" + ImeLog.GuidPattern + ")(?:_([0-9]{1,9}))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex EntraSid = new(@"^S-1-12-1-([0-9]{1,10})-([0-9]{1,10})-([0-9]{1,10})-([0-9]{1,10})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Dictionary<int, string[]> EnforcementStates = new()
    {
        [1000] = new[] { "OK", "installed / succeeded" },
        [1003] = new[] { "IN-PROGRESS", "install command received" },
        [1004] = new[] { "OK", "installed / succeeded (state 1004)" },
        [1005] = new[] { "OK", "installed / succeeded (state 1005)" },
        [1006] = new[] { "OK", "installed / succeeded (state 1006)" },
        [1007] = new[] { "OK", "installed / succeeded (state 1007)" },
        [1008] = new[] { "OK", "installed / succeeded (state 1008)" },
        [1009] = new[] { "OK", "installed / succeeded (state 1009)" },
        [1010] = new[] { "OK", "installed / succeeded (state 1010)" },
        [1011] = new[] { "OK", "installed / succeeded (state 1011)" },
        [1012] = new[] { "OK", "installed / succeeded (state 1012)" },
        [1013] = new[] { "OK", "installed / succeeded (state 1013)" },
        [1014] = new[] { "OK", "installed / succeeded (state 1014)" },
        [1015] = new[] { "OK", "installed / succeeded (state 1015)" },
        [1016] = new[] { "OK", "installed / succeeded (state 1016)" },
        [1017] = new[] { "OK", "installed / succeeded (state 1017)" },
        [1018] = new[] { "OK", "installed / succeeded (state 1018)" },
        [2000] = new[] { "IN-PROGRESS", "enforcement in progress" },
        [2007] = new[] { "DEPENDENCY", "waits until the apps it depends on are installed" },
        [2008] = new[] { "RESTART-FIRST", "installed, but not usable until the device restarts" },
        [2009] = new[] { "IN-PROGRESS", "downloaded, install not attempted yet" },
        [3000] = new[] { "NOT-APPLICABLE", "requirements not met on this device" },
        [4000] = new[] { "RETRY-HELPS", "failed for an unknown reason" },
        [5000] = new[] { "RETRY-HELPS", "failed with an error" },
        [5003] = new[] { "RETRY-HELPS", "content could not be downloaded" },
        [5006] = new[] { "UNKNOWN", "blocked: conflicts prevent the installation" },
        [5999] = new[] { "RETRY-HELPS", "failed; the IME retries it by itself" },
        [6000] = new[] { "PENDING", "not attempted yet, no reason given" },
        [6001] = new[] { "DEPENDENCY", "blocked: an app it depends on failed to install" },
        [6002] = new[] { "RESTART-FIRST", "blocked: a restart is pending" },
        [6003] = new[] { "NOT-APPLICABLE", "blocked: an app it depends on does not meet its requirements" },
        [6004] = new[] { "NOT-APPLICABLE", "a dependency of another app, not set to install on its own" },
        [6005] = new[] { "NOT-APPLICABLE", "blocked: an app it depends on is not set to install automatically" },
    };

    private static readonly Dictionary<int, string> Intents = new()
    {
        [0] = "Dependency (not assigned)",
        [1] = "Available",
        [3] = "Required",
        [4] = "Uninstall",
    };

    // The compliance message's ComplianceState is the detection result: 1 the rule found the app, 2 it did not.
    private static readonly Dictionary<int, string> ComplianceStates = new()
    {
        [0] = "Unknown",
        [1] = "Detected",
        [2] = "Not detected",
        [3] = "Conflict",
        [4] = "Error",
        [5] = "Not installed",
        [6] = "Not evaluated",
    };

    // The compliance message's DesiredState: whether Intune wants the app present (2) or absent (1).
    private static readonly Dictionary<int, string> DesiredStates = new()
    {
        [0] = "",
        [1] = "Absent",
        [2] = "Present",
    };

    /// <summary>
    /// Installer exit codes whose meaning is the same for every installer (Windows Installer's own,
    /// and 3010 for a restart), so their name from the shared table can be shown next to the number.
    /// Any other exit code means what that installer says it means.
    /// </summary>
    private static readonly int[] KnownInstallerCodes = { 1602, 1603, 1618, 1619, 1625, 1638, 1641, 3010 };

    /// <summary>Verdicts that need attention, in listing order: the ones another retry does not fix first.</summary>
    public static readonly string[] VerdictOrder = { "PACKAGING", "RESTART-FIRST", "NOT-APPLICABLE", "DEPENDENCY", "RETRY-HELPS", "UNKNOWN" };

    private static readonly Dictionary<string, string> VerdictMeaning = new()
    {
        ["PACKAGING"] = "the installer reported success, but the detection rule does not find the app. Another attempt repeats exactly that; the package or its detection rule needs fixing.",
        ["RESTART-FIRST"] = "restart the device first. Until then no retry or reset changes this.",
        ["NOT-APPLICABLE"] = "not a device fault: requirement or dependency settings keep it off this device. Retrying or resetting changes nothing; check the assignment and the requirement rules in Intune.",
        ["DEPENDENCY"] = "blocked by an app it depends on. That app is the one to fix; if it failed here, it is listed as well.",
        ["RETRY-HELPS"] = "the install failed on this device; a new attempt is the right next step.",
        ["UNKNOWN"] = "the IME recorded an error with a state this product does not recognise; see the error code.",
    };

    // Per verdict: severity, wording, the user next step and IT support step of a finding about one app.
    private static readonly Dictionary<string, string[]> FindingText = new()
    {
        ["PACKAGING"] = new[] { "Error", "installs, but Intune never detects it", "Retrying repeats the same result. Contact your IT support.", "The detection rule or the package of this app needs fixing in Intune." },
        ["RESTART-FIRST"] = new[] { "Warning", "waits for a restart", "Restart the device. No retry or reset changes this before that.", "" },
        ["NOT-APPLICABLE"] = new[] { "Info", "does not apply to this device", "Not a device fault: nothing on this device changes it. If this app should install here, contact your IT support.", "Check the requirement rules and the assignment of this app in Intune." },
        ["DEPENDENCY"] = new[] { "Warning", "is blocked by an app it depends on", "Fix the app it depends on first; if that one failed here, it is listed as well.", "" },
        ["RETRY-HELPS"] = new[] { "Warning", "failed on this device", "A new attempt is the right next step: Retry work apps and settings makes the IME try it now. If it fails again with the same code, contact your IT support.", "If it fails again with the same code, the code is the lead; the IME logs in a support package show the installer run." },
        ["UNKNOWN"] = new[] { "Warning", "is in a state this product does not recognise", "Create a support package and contact your IT support.", "Read the error code and the IME logs in the support package." },
    };

    /// <summary>
    /// The Entra object ID of a cloud account from its SID (S-1-12-1-a-b-c-d: the four parts are
    /// the object ID's bytes as little-endian 32-bit values), or '' for any other account.
    /// </summary>
    public static string EntraObjectId(string sid)
    {
        if (string.IsNullOrEmpty(sid)) { return ""; }
        var m = EntraSid.Match(sid);
        if (!m.Success) { return ""; }
        var bytes = new byte[16];
        for (var i = 0; i < 4; i++)
        {
            if (!ulong.TryParse(m.Groups[i + 1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > uint.MaxValue) { return ""; }
            BitConverter.GetBytes((uint)value).CopyTo(bytes, i * 4);
        }
        return new Guid(bytes).ToString();
    }

    /// <summary>The scopes whose data may be returned to the caller: the device, and the caller's own.</summary>
    public static HashSet<string> VisibleScopes(RunContext run)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DeviceScopeId };
        var own = EntraObjectId(run.CallerSid);
        if (own.Length > 0) { scopes.Add(own); }
        return scopes;
    }

    /// <summary>'Device', or 'User:' plus the first eight characters of the user's object ID.</summary>
    public static string ScopeRef(string scopeId) => scopeId == DeviceScopeId ? "Device" : "User:" + scopeId.Substring(0, 8);

    /// <summary>
    /// The words for a Win32 app record's error code, or ''. An HRESULT that wraps a Win32 code
    /// (facility 7) carries the installer's exit code: it is said as "exit code N", the way the app
    /// says it, with a meaning only for an exit code every installer shares (1603, 1618, 3010 and
    /// the like). Windows' text for the same number (1 is "Incorrect function") is not the
    /// installer's meaning and is left out. WinHTTP's codes (12000 to 12199) are the download's,
    /// not an installer's, and keep Windows' text. For any other code Windows' text; the fallback
    /// text that only names the code counts as "no text".
    /// </summary>
    public static string ErrorText(long code)
    {
        if (code <= 0) { return ""; }
        var lookup = code;
        var prefix = "";
        if (((code >> 31) & 1) == 1 && ((code >> 16) & 0x7FF) == 7)
        {
            lookup = code & 0xFFFF;
            if (lookup is < 12000 or >= 12200) { return InstallerExitText((int)lookup); }
            prefix = "code " + lookup.ToString(CultureInfo.InvariantCulture);
        }
        var signed = lookup > 2147483647 ? lookup - 4294967296 : lookup;
        string text;
        try
        {
            text = new Win32Exception((int)signed).Message;
        }
        catch (Exception)
        {
            return prefix;
        }
        text = (text ?? "").Trim();
        if (text.Length == 0) { return prefix; }
        if (text.ToLowerInvariant().Contains("0x" + lookup.ToString("x", CultureInfo.InvariantCulture))) { return prefix; }
        if (Regex.IsMatch(text, "(^|[^0-9])" + Regex.Escape(signed.ToString(CultureInfo.InvariantCulture)) + "([^0-9]|$)", RegexOptions.None, TimeSpan.FromSeconds(1))) { return prefix; }
        return prefix.Length > 0 ? prefix + ": " + text : text;
    }

    /// <summary>"exit code 1", or "exit code 1603: fatal error during installation" for an exit code every installer shares.</summary>
    internal static string InstallerExitText(int exitCode)
    {
        var text = "exit code " + exitCode.ToString(CultureInfo.InvariantCulture);
        if (Array.IndexOf(KnownInstallerCodes, exitCode) < 0) { return text; }
        var entry = CodeKnowledge.Get("0x8007" + exitCode.ToString("x4", CultureInfo.InvariantCulture));
        if (entry == null) { return text; }
        // The table's name repeats the number ("MSI 1603, fatal error during installation"); only its words follow it here.
        var name = entry.Name;
        var comma = name.IndexOf(", ", StringComparison.Ordinal);
        return text + ": " + (comma >= 0 ? name.Substring(comma + 2) : name);
    }

    /// <summary>
    /// The IME keeps a state message as JSON in a value named after its own subkey. Returns the raw
    /// text (for the package) and the parsed data, or null: one damaged record never costs the
    /// readout of the others.
    /// </summary>
    internal static (string Raw, JsonNode Data) ReadStateMessage(IRegistry registry, string keyPath, string name)
    {
        var raw = "";
        try
        {
            raw = MachineRegistry.AsText(registry.GetValue(keyPath + "\\" + name, name)) ?? "";
            return (raw, string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw));
        }
        catch (Exception)
        {
            return (raw, null);
        }
    }

    private static int? IntOrNull(string text) =>
        text != null && int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static int? IntOrNull(JsonNode node) => node == null || node.Kind == JsonNode.NodeKind.Null ? null : IntOrNull(node.AsText());

    private static string Text(JsonNode data, string name) => data?.Get(name) is JsonNode n && n.Kind != JsonNode.NodeKind.Null ? n.AsText() : "";

    private static object Value(IRegistry registry, string key, string name) => ImeLog.SafeValue(registry, key, name);

    /// <summary>
    /// State wiped by a recent Full IME reset looks like a device the IME never worked on; the
    /// reset's own timestamp tells them apart.
    /// </summary>
    public static string LastFullResetText(RunContext run, bool plainTime = false)
    {
        var when = StateStore.FromStateTime(run.State.Get("LastFullReset"));
        if (when == null) { return ""; }
        var minutes = (int)Math.Floor((run.Services.Clock.UtcNow - when.Value).TotalMinutes);
        if (minutes < 0) { return ""; }
        var whenText = plainTime
            ? when.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (device time)"
            : RunContext.UserTime(when.Value, "yyyy-MM-dd HH:mm");
        return string.Format(CultureInfo.InvariantCulture, "The last Full IME reset ran at {0} ({1} min ago); the IME rebuilds this state as it evaluates each app again.", whenText, minutes);
    }

    /// <summary>What the IME last reported to Intune per app and scope; keyed '&lt;scope&gt;|&lt;appId&gt;'.</summary>
    public static Dictionary<string, AppStatusReport> StatusReports(RunContext run)
    {
        var map = new Dictionary<string, AppStatusReport>(StringComparer.Ordinal);
        var registry = run.Services.Registry;
        var root = run.Services.ImePaths.SideCarKey + "\\StatusServiceReports";
        if (!registry.KeyExists(root)) { return map; }
        var visible = VisibleScopes(run);
        foreach (var scope in ImeLog.SafeSubKeys(registry, root))
        {
            if (!visible.Contains(scope.ToLowerInvariant())) { continue; }
            foreach (var app in ImeLog.SafeSubKeys(registry, root + "\\" + scope))
            {
                var key = root + "\\" + scope + "\\" + app;
                if (!registry.KeyExists(key)) { continue; }
                // The newer value wins only when it holds something (an empty string or a 0 does not).
                string Prefer(string newer, string older)
                {
                    var value = Value(registry, key, newer);
                    if (value == null || (value is string s && s.Length == 0) || (value is int i && i == 0)) { value = Value(registry, key, older); }
                    return MachineRegistry.AsText(value) ?? "";
                }
                map[scope.ToLowerInvariant() + "|" + app.ToLowerInvariant()] = new AppStatusReport
                {
                    Status = Prefer("Status2", "Status"),
                    Applicability = Prefer("ApplicabilityCode2", "ApplicabilityCode"),
                    ErrorCode = StatusCode.Parse(MachineRegistry.AsText(Value(registry, key, "ErrorCode")) ?? ""),
                };
            }
        }
        return map;
    }

    /// <summary>
    /// Verdict and state text for one app record. Separate so it can be tested without the registry.
    /// <paramref name="complianceValue"/> is the detection result (1 detected, 2 not detected) and
    /// <paramref name="desiredValue"/> whether Intune wants the app present (2) or absent (1).
    /// </summary>
    public static (string Verdict, string State, long Code) Verdict(int? stateValue, int? complianceValue, int? intentValue, long code, AppStatusReport report, int? desiredValue = null)
    {
        var verdict = "NO-STATE";
        var stateText = "no state recorded";
        if (stateValue != null)
        {
            if (EnforcementStates.TryGetValue(stateValue.Value, out var known))
            {
                verdict = known[0];
                stateText = known[1];
                if (stateValue == 1000 && intentValue == 4) { stateText = "uninstalled / succeeded"; }
            }
            else
            {
                verdict = "UNKNOWN";
                stateText = "enforcement state " + stateValue.Value.ToString(CultureInfo.InvariantCulture);
            }
        }
        if (verdict == "NO-STATE" && code != 0) { verdict = "UNKNOWN"; }

        if (verdict == "NO-STATE" && report != null)
        {
            // Without an enforcement record, what the IME last reported is the best evidence.
            switch (report.Status)
            {
                // For an uninstall assignment "installed" means it waits to be removed.
                case "Installed" when intentValue == 4: verdict = "PENDING"; stateText = "uninstall assignment; still installed (IME status)"; break;
                case "Installed": verdict = "OK"; stateText = "installed (IME status)"; break;
                case "NotApplicable": verdict = "NO-ACTION"; stateText = "not applicable (IME status)"; break;
                case "Failed": verdict = "RETRY-HELPS"; stateText = "install failed (IME status)"; break;
                case "UninstallFailed": verdict = "RETRY-HELPS"; stateText = "uninstall failed (IME status)"; break;
                case "Installing": verdict = "IN-PROGRESS"; stateText = "installing (IME status)"; break;
            }
            if (report.Status == "NotApplicable" && report.Applicability.Length > 0) { stateText = "not applicable: " + report.Applicability; }
            // The report's error code, for a failure the enforcement record does not carry.
            if ((report.Status == "Failed" || report.Status == "UninstallFailed") && code == 0 && report.ErrorCode is long reported && reported != 0) { code = reported; }
        }

        if (verdict == "NO-STATE")
        {
            // After a full re-evaluation the IME writes an enforcement record only when it actually
            // installs; an app it finds present, not requested or not applicable gets a compliance
            // state only. Classified from intent, detection and the state Intune wants.
            if (intentValue == 4 && complianceValue == 1)
            {
                // Still detected: the removal has not happened yet.
                verdict = "PENDING";
                stateText = "uninstall assignment; still detected, no uninstall attempt recorded";
            }
            else if (intentValue == 4 && complianceValue == 2)
            {
                // Wanted absent and not detected: the assignment is met, with nothing to remove.
                verdict = "OK";
                stateText = "not present, as assigned";
            }
            else if (intentValue == 4)
            {
                verdict = "NO-ACTION";
                stateText = "uninstall assignment; no uninstall attempt recorded";
            }
            else if (complianceValue == 1)
            {
                verdict = "OK";
                stateText = "installed (detected); nothing to do";
            }
            else if (complianceValue == 2)
            {
                if (intentValue == 1) { verdict = "AVAILABLE"; stateText = "offered in Company Portal, not installed"; }
                else if (intentValue == 0) { verdict = "NO-ACTION"; stateText = "not installed; a dependency, not assigned itself"; }
                // Intune wants it absent (as for an app a newer one supersedes), and it is: nothing to
                // do. Not "OK": for an install assignment that verdict means installed and detected.
                else if (desiredValue == 1) { verdict = "NO-ACTION"; stateText = "not present, as assigned: Intune wants it absent on this device, as for an app that a newer one supersedes"; }
                else { verdict = "NO-ACTION"; stateText = "not detected and never attempted: requirements not met, or superseded"; }
            }
            else if (complianceValue == 4)
            {
                verdict = "UNKNOWN";
                stateText = "compliance error with no enforcement record";
            }
        }
        if (code == NotDetectedCode && (verdict == "RETRY-HELPS" || verdict == "UNKNOWN"))
        {
            verdict = "PACKAGING";
            stateText = "installed, but the detection rule does not find it";
        }
        return (verdict, stateText, code);
    }

    /// <summary>
    /// One row per app per scope from Win32Apps\&lt;scope&gt;\&lt;appId&gt;_&lt;revision&gt;, newest
    /// revision only (compared as a number). Only the device scope and the caller's own scope are
    /// read; other scopes are counted, nothing more.
    /// </summary>
    public static AppStateReadout Read(RunContext run)
    {
        var result = new AppStateReadout();
        var registry = run.Services.Registry;
        var root = run.Services.ImePaths.Win32AppsKey;
        if (!registry.KeyExists(root)) { return result; }
        result.Present = true;

        Dictionary<string, AppStatusReport> reports;
        try { reports = StatusReports(run); }
        catch (Exception) { reports = new Dictionary<string, AppStatusReport>(StringComparer.Ordinal); }

        // The root also holds keys that are not scopes; only GUID-named ones are.
        var scopeKeys = registry.SubKeyNames(root).Where(n => ScopeName.IsMatch(n)).ToList();
        var visible = VisibleScopes(run);
        foreach (var scopeKey in scopeKeys)
        {
            var scopeId = scopeKey.ToLowerInvariant();
            var scopePath = root + "\\" + scopeKey;
            if (!visible.Contains(scopeId))
            {
                result.OtherScopes++;
                var otherIds = ImeLog.SafeSubKeys(registry, scopePath).Where(n => AppKeyName.IsMatch(n)).Select(n => n.Substring(0, 36).ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
                result.OtherRecords += otherIds.Count;
                foreach (var otherId in otherIds) { result.OtherAppIds.Add(otherId); }
                continue;
            }
            result.Scopes++;
            var newest = new Dictionary<string, (int Revision, string KeyPath)>(StringComparer.Ordinal);
            // Every revision of each app in this scope, for an earlier revision's failed attempt.
            var revisions = new Dictionary<string, List<(int Revision, string KeyPath)>>(StringComparer.Ordinal);
            foreach (var appKey in ImeLog.SafeSubKeys(registry, scopePath))
            {
                var m = AppKeyName.Match(appKey);
                if (!m.Success) { continue; }
                var appId = m.Groups[1].Value.ToLowerInvariant();
                var revision = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : -1;
                if (!newest.TryGetValue(appId, out var known) || revision > known.Revision) { newest[appId] = (revision, scopePath + "\\" + appKey); }
                if (!revisions.TryGetValue(appId, out var all)) { revisions[appId] = all = new List<(int Revision, string KeyPath)>(); }
                all.Add((revision, scopePath + "\\" + appKey));
            }

            foreach (var pair in newest)
            {
                var appId = pair.Key;
                var entry = pair.Value;
                reports.TryGetValue(scopeId + "|" + appId, out var report);

                var intentValue = IntOrNull(MachineRegistry.AsText(Value(registry, entry.KeyPath, "Intent")));
                var intentText = intentValue == null ? "" : Intents.TryGetValue(intentValue.Value, out var it) ? it : "Intent " + intentValue.Value.ToString(CultureInfo.InvariantCulture);
                var enforcement = ReadStateMessage(registry, entry.KeyPath, "EnforcementStateMessage");
                var compliance = ReadStateMessage(registry, entry.KeyPath, "ComplianceStateMessage");
                var stateValue = IntOrNull(enforcement.Data?.Get("EnforcementState"));
                var complianceValue = IntOrNull(compliance.Data?.Get("ComplianceState"));
                var desiredValue = IntOrNull(compliance.Data?.Get("DesiredState"));

                var code = StatusCode.Parse(Text(enforcement.Data, "ErrorCode"));
                if (code == null || code == 0) { code = StatusCode.Parse(Text(compliance.Data, "ErrorCode")); }
                var verdict = Verdict(stateValue, complianceValue, intentValue, code ?? 0, report, desiredValue);
                var finalCode = verdict.Code;

                var installed = Installed(stateValue, complianceValue, intentValue, report);
                var complianceText = complianceValue == null ? "" : ComplianceStates.TryGetValue(complianceValue.Value, out var ct) ? ct : "ComplianceState " + complianceValue.Value.ToString(CultureInfo.InvariantCulture);
                var desiredText = desiredValue == null ? "" : DesiredStates.TryGetValue(desiredValue.Value, out var dt) ? dt : "DesiredState " + desiredValue.Value.ToString(CultureInfo.InvariantCulture);
                var earlier = stateValue == null ? EarlierFailure(registry, revisions[appId], entry.Revision) : null;

                result.Rows.Add(new AppStateRow
                {
                    EarlierRevision = earlier?.Revision ?? -1,
                    EarlierCode = earlier?.Code ?? "",
                    EarlierUpdatedUtc = earlier?.UpdatedUtc ?? "",
                    ScopeRef = ScopeRef(scopeId),
                    ScopeId = scopeId,
                    KeyWrittenUtc = registry.KeyLastWriteUtc(entry.KeyPath),
                    AppId = appId,
                    Revision = entry.Revision,
                    Intent = intentText,
                    Verdict = verdict.Verdict,
                    State = verdict.State,
                    Installed = installed,
                    EnforcementState = stateValue,
                    ErrorCode = finalCode != 0 ? CodeKnowledge.FormatHex(finalCode.ToString(CultureInfo.InvariantCulture)) : "",
                    ErrorText = ErrorText(finalCode),
                    ComplianceState = complianceText,
                    DesiredState = desiredText,
                    ImeStatus = report?.Status ?? "",
                    ImeApplicability = report?.Applicability ?? "",
                    ProductVersion = Text(compliance.Data, "ProductVersion"),
                    LastUpdatedUtc = MachineRegistry.AsText(Value(registry, entry.KeyPath, "LastUpdatedTimeUtc")) ?? "",
                    EnforcementJson = enforcement.Raw,
                    ComplianceJson = compliance.Raw,
                });
            }
        }
        return result;
    }

    /// <summary>
    /// "Yes", "No" or '' (not known): whether the app is on the device, the question people actually
    /// ask. A finished enforcement (state 1000) decides it first: installed, or for an uninstall
    /// assignment removed. Then detection, for an uninstall assignment too, then the IME's status report.
    /// </summary>
    internal static string Installed(int? stateValue, int? complianceValue, int? intentValue, AppStatusReport report)
    {
        if (intentValue == 4)
        {
            if (stateValue == 1000 || complianceValue == 2) { return "No"; }
            if (complianceValue == 1) { return "Yes"; }
            return report?.Status == "Installed" ? "Yes" : "";
        }
        if (stateValue == 1000 || complianceValue == 1) { return "Yes"; }
        if (complianceValue == 2) { return "No"; }
        return report?.Status switch { "Installed" => "Yes", "Failed" => "No", _ => "" };
    }

    /// <summary>
    /// The newest revision older than <paramref name="current"/> whose enforcement record says
    /// the install failed (a failure state, or an error code), with its code and its record time;
    /// null when there is none. Evidence only: the current revision's state stays the verdict.
    /// </summary>
    internal static (int Revision, string Code, string UpdatedUtc)? EarlierFailure(IRegistry registry, IEnumerable<(int Revision, string KeyPath)> revisions, int current)
    {
        foreach (var (revision, keyPath) in revisions.Where(r => r.Revision < current).OrderByDescending(r => r.Revision))
        {
            var enforcement = ReadStateMessage(registry, keyPath, "EnforcementStateMessage");
            var state = IntOrNull(enforcement.Data?.Get("EnforcementState"));
            if (state == null) { continue; }
            var code = StatusCode.Parse(Text(enforcement.Data, "ErrorCode")) ?? 0;
            var verdict = Verdict(state, null, null, code, null).Verdict;
            if (verdict is not ("RETRY-HELPS" or "UNKNOWN" or "PACKAGING")) { return null; }
            return (revision, code != 0 ? CodeKnowledge.FormatHex(code.ToString(CultureInfo.InvariantCulture)) : "",
                MachineRegistry.AsText(Value(registry, keyPath, "LastUpdatedTimeUtc")) ?? "");
        }
        return null;
    }

    /// <summary>
    /// Display names for app IDs, from the "Get policies = [...]" lines the IME writes when it
    /// fetches its Win32 app policy. Only the Id and the Name directly after it are taken, by
    /// pattern. Bounded: the newest few IME logs, newest line first, stopping once every ID is named.
    /// </summary>
    public static Dictionary<string, string> NameMap(RunContext run, IEnumerable<string> appIds, int maxLogFiles = 4)
        => LookUpNames(run, appIds, maxLogFiles).Names;

    /// <summary>
    /// The names of app IDs and where each came from. First the newest few IME logs' "Get
    /// policies" lines (the pattern of <see cref="AppNameCache.PolicyNames"/>); every name seen there,
    /// of any app, goes into the name cache when the service gave one (<see cref="RunContext.AppNameCacheFile"/>).
    /// An ID the logs no longer name (they rotate within hours) takes its name from the cache: a name
    /// from an earlier IME log. An ID neither knows stays without a name. Without a cache the logs are
    /// read as before, stopping once every ID is named.
    /// </summary>
    public static AppNameLookup LookUpNames(RunContext run, IEnumerable<string> appIds, int maxLogFiles = 4)
    {
        var lookup = new AppNameLookup();
        var wanted = new HashSet<string>((appIds ?? Enumerable.Empty<string>()).Where(i => !string.IsNullOrEmpty(i)).Select(i => i.ToLowerInvariant()), StringComparer.Ordinal);
        if (wanted.Count == 0) { return lookup; }
        var useCache = !string.IsNullOrEmpty(run.AppNameCacheFile);
        // Every name the lines hold, newest first (a renamed app keeps its newest name); the same
        // read as the service's hourly one (AppNameCache.ReadPolicyNames). Without a cache it stops
        // once every wanted ID is named, as before.
        var seen = AppNameCache.ReadPolicyNames(run.Services.ImePaths.LogsFolder, maxLogFiles,
            useCache ? null : names => wanted.All(names.ContainsKey));
        foreach (var id in wanted) { if (seen.TryGetValue(id, out var name)) { lookup.Names[id] = name; } }
        if (!useCache) { return lookup; }

        AppNameCache cache;
        try { cache = AppNameCache.Update(run.AppNameCacheFile, seen.Select(p => (p.Key, p.Value)), run.Services.Clock.UtcNow, run.Services.TrustedStateFolder); }
        catch (Exception) { cache = new AppNameCache(); }
        foreach (var id in wanted)
        {
            if (lookup.Names.ContainsKey(id) || !cache.TryGet(id, out var cached)) { continue; }
            lookup.Names[id] = cached;
            lookup.FromCache.Add(id);
        }
        lookup.CacheCount = cache.Count;
        lookup.CacheUnreadable = cache.WasUnreadable;
        run.Log(string.Format(CultureInfo.InvariantCulture, "App names: {0} from the IME logs, {1} from earlier IME logs (the name cache holds {2}){3}.",
            lookup.Names.Count - lookup.FromCache.Count, lookup.FromCache.Count, TextFormat.Count(cache.Count, "name"),
            cache.WasUnreadable ? "; its file could not be read and is rebuilt" : ""));
        return lookup;
    }

    /// <summary>One line on the name cache for the support package: its size and newest date, never its names.</summary>
    public static string CacheSummary(AppNameCache cache)
    {
        if (cache == null || cache.Count == 0)
        {
            return cache != null && cache.WasUnreadable ? "App name cache: its file could not be read; it is rebuilt at the next app lookup." : "App name cache: empty.";
        }
        return string.Format(CultureInfo.InvariantCulture, "App name cache: {0}, newest seen {1} (UTC); the names are not listed here.",
            TextFormat.Count(cache.Count, "name"), cache.NewestSeenUtc.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    public static int VerdictRank(string verdict)
    {
        var index = Array.IndexOf(VerdictOrder, verdict);
        return index < 0 ? 100 : index;
    }

    /// <summary>
    /// The order of a list that may be cut: the verdicts that need attention first (in
    /// <see cref="VerdictOrder"/>), then the apps offered in Company Portal, then the ones installed or
    /// on their way, then records without a state, and the ones with nothing to do here (not
    /// applicable, not assigned) last. A cut then drops those first.
    /// </summary>
    public static int ListRank(string verdict)
    {
        var index = Array.IndexOf(VerdictOrder, verdict);
        if (index >= 0) { return index; }
        return verdict switch
        {
            "AVAILABLE" => 100,
            "OK" or "IN-PROGRESS" or "PENDING" => 101,
            "NO-ACTION" => 103,
            _ => 102,
        };
    }

    private static IOrderedEnumerable<AppStateRow> Sorted(IEnumerable<AppStateRow> rows) =>
        rows.OrderBy(r => VerdictRank(r.Verdict)).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.AppId, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>One finding per app that needs attention (those a retry cannot fix first, capped), or one OK finding when nothing does.</summary>
    public static void AddFindings(RunContext run, IReadOnlyList<AppStateRow> problems, IReadOnlyList<AppStateRow> rows, IReadOnlyDictionary<string, int> count, string moment,
        int maxFindings = 8, bool inPackage = false, IReadOnlyDictionary<string, ImeAppAttempt> attempts = null)
    {
        if (problems.Count == 0)
        {
            run.AddFinding(FindingLevel.Ok, "APPS-OK", "No Win32 app needs attention",
                string.Format(CultureInfo.InvariantCulture, "{0} read {1}: {2} installed or done, {3} offered in Company Portal, {4} not applicable, {5} in progress or pending.",
                    TextFormat.Count(rows.Count, "app record"), moment, count["OK"], count["AVAILABLE"], count["NO-ACTION"], count["IN-PROGRESS"] + count["PENDING"]));
            return;
        }
        var moreNext = inPackage
            ? "IntuneState.xlsx in this support package lists every app with its verdict."
            : "Create a support package: IntuneState.xlsx lists every app with its verdict.";
        var sorted = Sorted(problems).ToList();
        var shown = 0;
        foreach (var p in sorted)
        {
            if (shown >= maxFindings) { break; }
            var text = FindingText.TryGetValue(p.Verdict, out var known) ? known : FindingText["UNKNOWN"];
            var name = string.IsNullOrWhiteSpace(p.Name) ? "App " + p.AppId.PadRight(8).Substring(0, 8).Trim() : p.Name;
            var verb = text[1];
            if (p.Verdict == "RETRY-HELPS" && (p.Intent == "Uninstall" || p.State.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase))) { verb = "failed to uninstall on this device"; }
            var detail = string.Format(CultureInfo.InvariantCulture, "{0}, {1}: {2}.", p.ScopeRef, p.Intent, p.State);
            var next = text[2];
            var support = text[3];
            if (p.ErrorCode.Length > 0)
            {
                var knowledge = CodeKnowledge.Get(p.ErrorCode);
                if (knowledge != null)
                {
                    detail += string.Format(CultureInfo.InvariantCulture, " Error {0} ({1}): {2}", p.ErrorCode, knowledge.Name, knowledge.Meaning);
                    if (knowledge.NextStep.Length > 0) { next = knowledge.NextStep; }
                    if (knowledge.Support.Length > 0) { support = knowledge.Support; }
                }
                else if (p.ErrorText.Length > 0)
                {
                    detail += " Error " + p.ErrorCode + " (" + p.ErrorText + ").";
                }
                else
                {
                    detail += " Error " + p.ErrorCode + ".";
                }
            }
            detail += " Read " + moment + ".";
            if (attempts != null && attempts.TryGetValue(p.AppId, out var attempt))
            {
                detail += " Newest attempt in the IME log (" + RunContext.UserTime(attempt.LastSeenUtc, "yyyy-MM-dd HH:mm") + "): " + ImeLog.FormatPhase(attempt) + ".";
            }
            if (p.Verdict == "RETRY-HELPS" && p.Intent == "Available")
            {
                next = "This app is offered in Company Portal: only the user can start the next attempt there (Retry or Install).";
            }
            var level = text[0] switch { "Error" => FindingLevel.Error, "Warning" => FindingLevel.Warning, "Info" => FindingLevel.Info, _ => FindingLevel.Ok };
            run.AddFinding(level, "APP-" + p.Verdict, name + " " + verb, detail, next, support);
            shown++;
        }
        if (sorted.Count > shown)
        {
            run.AddFinding(FindingLevel.Info, "APPS-MORE", TextFormat.Count(sorted.Count - shown, "more app") + " need attention",
                "Not listed here, so the list stays readable.", moreNext);
        }
    }

    /// <summary>The most apps <see cref="AddBeforeFinding"/> names one by one.</summary>
    private const int MaxBeforeApps = 8;

    /// <summary>
    /// One information finding that names the apps that needed attention before an action changed
    /// anything: once per app (its scopes together), with what was wrong and its code. Information,
    /// because it is the state the action starts from; whether the action worked is its own result.
    /// </summary>
    internal static void AddBeforeFinding(RunContext run, IReadOnlyList<AppStateRow> problems, string moment)
    {
        var apps = Sorted(problems).GroupBy(p => p.AppId, StringComparer.OrdinalIgnoreCase).ToList();
        if (apps.Count == 0) { return; }
        static string Label(AppStateRow p) => string.IsNullOrWhiteSpace(p.Name) ? "App " + p.AppId.PadRight(8).Substring(0, 8).Trim() : p.Name;
        var parts = new List<string>();
        foreach (var app in apps.Take(MaxBeforeApps))
        {
            var p = app.First();
            var text = FindingText.TryGetValue(p.Verdict, out var known) ? known : FindingText["UNKNOWN"];
            var uninstall = p.Intent == "Uninstall" || p.State.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase);
            var verb = p.Verdict == "RETRY-HELPS" && uninstall ? "failed to uninstall on this device" : text[1];
            var where = new List<string>();
            if (app.Any(a => a.ScopeRef == "Device")) { where.Add("device"); }
            if (app.Any(a => a.ScopeRef != "Device")) { where.Add("user"); }
            var facts = string.Join(" and ", where);
            if (p.ErrorCode.Length > 0) { facts += ", " + (p.ErrorText.Length > 0 ? p.ErrorText + ", " : "") + p.ErrorCode; }
            parts.Add(Label(p) + " " + verb + " (" + facts + ")");
        }
        var more = apps.Count > MaxBeforeApps ? "; and " + TextFormat.Count(apps.Count - MaxBeforeApps, "more app") : "";
        var names = apps.Take(3).Select(a => Label(a.First())).ToList();
        var title = apps.Count == 1
            ? "Before this run, " + names[0] + " already needed attention"
            : "Before this run, " + TextFormat.Count(apps.Count, "app") + " already needed attention: " + string.Join(", ", names) + (apps.Count > 3 ? " and " + TextFormat.Count(apps.Count - 3, "more") : "");
        run.AddFinding(FindingLevel.Info, "APPS-BEFORE", title,
            string.Format(CultureInfo.InvariantCulture, "Read {0}, so this is the state this run started from, not its result: {1}{2}.", moment, string.Join("; ", parts), more),
            "Check the apps again in a few minutes: the app list then shows whether the new attempt worked.");
    }

    /// <summary>
    /// Logs and reports what the readout found: counts, then the records that need attention
    /// (capped), one line per verdict on what it means, the findings, and one apps-state record.
    /// </summary>
    /// <remarks>
    /// <paramref name="beforeChange"/>: the readout an action takes before it changes anything. The
    /// apps that need attention then are the state the action starts from, not its result: one
    /// information finding names them, instead of a warning per app and scope.
    /// </remarks>
    public static void WriteSummary(RunContext run, AppStateReadout state, string moment, int maxLines = 15, bool inPackage = false, bool noLogEvidence = false, bool beforeChange = false)
    {
        if (state.OtherScopes > 0)
        {
            run.Log(string.Format(CultureInfo.InvariantCulture, "Win32 app state {0}: {1} of {2} on this device are left out; they are not yours to see.",
                moment, TextFormat.Count(state.OtherRecords, "app record"), TextFormat.Count(state.OtherScopes, "other user")));
        }
        if (!state.Present || state.Rows.Count == 0)
        {
            run.Log(string.Format(CultureInfo.InvariantCulture, "Win32 app state {0}: the IME holds no Win32 app state on this device; it has not processed a Win32 app here yet, or the state was cleared.", moment));
            var resetText = LastFullResetText(run);
            if (resetText.Length > 0) { run.Log(resetText); }
            run.AddFinding(FindingLevel.Info, "APPS-NO-STATE", "No Win32 app state on this device",
                resetText.Length > 0 ? resetText : "The IME has not processed a Win32 app here yet, or the state was cleared.");
            run.WriteRecord("apps-state", RunContext.Field("records", 0), RunContext.Field("scopes", state.Scopes), RunContext.Field("otherscopes", state.OtherScopes));
            run.PersistentLog("EndpointToolbelt:APPS-STATE records=0 scopes=" + state.Scopes.ToString(CultureInfo.InvariantCulture));
            return;
        }

        var rows = state.Rows;
        var problems = rows.Where(r => VerdictOrder.Contains(r.Verdict)).ToList();
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var v in OtherVerdicts.Concat(VerdictOrder)) { count[v] = 0; }
        foreach (var r in rows) { if (count.TryGetValue(r.Verdict, out var n)) { count[r.Verdict] = n + 1; } }

        // Names only for what is listed, and only where no earlier step found them.
        var unnamed = problems.Where(p => string.IsNullOrWhiteSpace(p.Name)).Select(p => p.AppId).ToList();
        if (unnamed.Count > 0)
        {
            Dictionary<string, string> map;
            try { map = NameMap(run, unnamed); } catch (Exception) { map = new Dictionary<string, string>(); }
            foreach (var p in problems) { if (string.IsNullOrWhiteSpace(p.Name) && map.TryGetValue(p.AppId, out var n)) { p.Name = n; } }
        }

        run.Log(string.Format(CultureInfo.InvariantCulture,
            "Win32 app state {0}: {1} in {2}; {3} installed or done, {4} offered and not installed, {5} not applicable or nothing to do, {6} in progress or pending, {7} without any state, {8} need attention.",
            moment, TextFormat.Count(rows.Count, "app record"), TextFormat.Count(state.Scopes, "scope"), count["OK"], count["AVAILABLE"], count["NO-ACTION"],
            count["IN-PROGRESS"] + count["PENDING"], count["NO-STATE"], problems.Count));
        if (problems.Count > 0)
        {
            var sorted = Sorted(problems).ToList();
            var shown = 0;
            foreach (var p in sorted)
            {
                if (shown >= maxLines) { break; }
                var label = string.IsNullOrWhiteSpace(p.Name) ? p.AppId : p.Name + " (" + p.AppId.Substring(0, 8) + ")";
                var where = p.Intent.Length > 0 ? p.ScopeRef + ", " + p.Intent : p.ScopeRef;
                var line = "  " + p.Verdict + " " + label + " [" + where + "]: " + p.State;
                if (p.ErrorCode.Length > 0)
                {
                    line += "; error " + p.ErrorCode;
                    if (p.ErrorText.Length > 0) { line += " (" + p.ErrorText + ")"; }
                }
                if (p.Verdict == "RETRY-HELPS" && p.Intent == "Available") { line += "; the user retries it from Company Portal"; }
                run.Log(line);
                shown++;
            }
            if (sorted.Count > shown) { run.Log(string.Format(CultureInfo.InvariantCulture, "  ... and {0} more. The full table is Win32AppState.csv in a support package.", sorted.Count - shown)); }
            foreach (var v in VerdictOrder)
            {
                if (problems.Any(p => p.Verdict == v)) { run.Log("  " + v + ": " + VerdictMeaning[v]); }
            }
            if (problems.Any(p => p.Verdict == "RETRY-HELPS" && p.Intent == "Available"))
            {
                run.Log("  Available apps: the next attempt is the user's, with Retry or Install in Company Portal. Clearing the retry timers does not retry them.");
            }
        }
        var attempts = noLogEvidence ? new Dictionary<string, ImeAppAttempt>() : ImeLog.WriteEvidence(run, rows, problems, state.OtherAppIds);
        if (beforeChange && problems.Count > 0) { AddBeforeFinding(run, problems, moment); }
        else { AddFindings(run, problems, rows, count, moment, inPackage: inPackage, attempts: attempts); }

        var fields = new List<KeyValuePair<string, object>>
        {
            RunContext.Field("records", rows.Count),
            RunContext.Field("scopes", state.Scopes),
            RunContext.Field("otherscopes", state.OtherScopes),
        };
        // The same names as in the log marker below.
        var names = new[]
        {
            ("ok", "OK"), ("inprogress", "IN-PROGRESS"), ("pending", "PENDING"), ("nostate", "NO-STATE"), ("available", "AVAILABLE"), ("noaction", "NO-ACTION"),
            ("packaging", "PACKAGING"), ("restart", "RESTART-FIRST"), ("notapplicable", "NOT-APPLICABLE"), ("dependency", "DEPENDENCY"), ("retry", "RETRY-HELPS"), ("unknown", "UNKNOWN"),
        };
        foreach (var (field, verdict) in names) { fields.Add(RunContext.Field(field, count[verdict])); }
        run.WriteRecord("apps-state", fields.ToArray());
        run.PersistentLog(string.Format(CultureInfo.InvariantCulture,
            "EndpointToolbelt:APPS-STATE records={0} scopes={1} ok={2} inprogress={3} pending={4} nostate={5} available={6} noaction={7} packaging={8} restart={9} notapplicable={10} dependency={11} retry={12} unknown={13}",
            rows.Count, state.Scopes, count["OK"], count["IN-PROGRESS"], count["PENDING"], count["NO-STATE"], count["AVAILABLE"], count["NO-ACTION"],
            count["PACKAGING"], count["RESTART-FIRST"], count["NOT-APPLICABLE"], count["DEPENDENCY"], count["RETRY-HELPS"], count["UNKNOWN"]));
    }

    /// <summary>
    /// The entry point for the app actions, read before they change anything: the apps that need
    /// attention are named in one information finding (the state the action starts from, never its
    /// result). Never throws: whatever happens, the action goes on. Returns the failed apps the IME
    /// retries on its own, once per app (the IME keeps one retry time per app, for all its scopes).
    /// </summary>
    public static IReadOnlyList<AppRetry> Readout(RunContext run, string moment)
    {
        var retries = new List<AppRetry>();
        AppStateReadout state;
        try
        {
            state = Read(run);
            WriteSummary(run, state, moment, beforeChange: true);
        }
        catch (Exception ex)
        {
            run.Log("The Win32 app state could not be read (" + ex.Message + "). The action continues; this readout is informational only.", LogSeverity.Warning);
            return retries;
        }
        try
        {
            var failed = state.Rows.Where(r => r.Verdict == "RETRY-HELPS" && r.Intent != "Available")
                .GroupBy(r => r.AppId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            if (failed.Count > 0)
            {
                var schedule = ImeLog.RetrySchedule(run);
                foreach (var row in failed)
                {
                    if (!schedule.TryGetValue(row.AppId, out var entry)) { continue; }
                    var name = string.IsNullOrWhiteSpace(row.Name) ? "App " + row.AppId.Substring(0, 8) : row.Name;
                    run.Log(string.Format(CultureInfo.InvariantCulture, "  {0}: the IME recorded its failure at {1}; its own next attempt is due around {2} (estimate: {3} h later).",
                        name, RunContext.UserTime(entry.FailedUtc, "yyyy-MM-dd HH:mm"), RunContext.UserTime(entry.NextUtc, "yyyy-MM-dd HH:mm"), ImeLog.RetryHours));
                    retries.Add(new AppRetry { Name = name, AppId = row.AppId, NextUtc = entry.NextUtc });
                }
            }
        }
        catch (Exception ex)
        {
            run.Log("The IME retry timers could not be read (" + ex.Message + "); the times of its own next attempts are not known.", LogSeverity.Warning);
        }
        return retries;
    }

    /// <summary>When the retry timers were not cleared, when the IME tries each failed app again on its own.</summary>
    public static void AddNextRetryFinding(RunContext run, IReadOnlyList<AppRetry> retries, string reason)
    {
        var list = (retries ?? Array.Empty<AppRetry>()).Where(r => r != null).ToList();
        if (list.Count == 0) { return; }
        var now = run.Services.Clock.UtcNow;
        var parts = new List<string>();
        foreach (var r in list.OrderBy(r => r.NextUtc).Take(5))
        {
            parts.Add(r.NextUtc > now
                ? r.Name + " around " + RunContext.UserTime(r.NextUtc, "yyyy-MM-dd HH:mm")
                : r.Name + " at its next check (due since " + RunContext.UserTime(r.NextUtc, "yyyy-MM-dd HH:mm") + ")");
        }
        var more = list.Count > 5 ? " and " + TextFormat.Count(list.Count - 5, "more app") : "";
        run.AddFinding(FindingLevel.Info, "APPS-NEXT-RETRY", "When the IME tries the failed apps again by itself",
            string.Format(CultureInfo.InvariantCulture, "{0} The IME retries a failed app about {1} hours after the failure it recorded: {2}{3}. These times are estimates.", reason, ImeLog.RetryHours, string.Join("; ", parts), more),
            "Wait for that attempt, or run Retry work apps and settings at a quiet moment to make the IME try now.");
    }
}
