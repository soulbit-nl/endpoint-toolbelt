using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.Support;

/// <summary>One row of the remediation table: one remediation on the device. The CSV columns, in order.</summary>
public sealed class RemediationRow
{
    public string PolicyId { get; set; } = "";
    public int Version { get; set; } = -1;
    public string Verdict { get; set; } = "";
    public string State { get; set; } = "";
    public string RunsAs { get; set; } = "";
    public string LastRun { get; set; } = "";
    public string LastRunScope { get; set; } = "";
    public int Scopes { get; set; }
    public int FailingScopes { get; set; }
    public int? DetectExit { get; set; }
    public int? RemediationExit { get; set; }
    public int? PostDetectExit { get; set; }
    public string DetectErrorOutput { get; set; } = "";
    public string RemediationErrorOutput { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public int? Result { get; set; }
    public int? RemediationStatus { get; set; }

    /// <summary>
    /// The CSV columns. LastRun is ISO 8601 in UTC; RemediationStatusText says what the number means
    /// (<see cref="StatusText"/>). Result stays a number: its meaning is not documented.
    /// </summary>
    public static readonly string[] Headers =
    {
        "PolicyId", "Version", "Verdict", "State", "RunsAs", "LastRun", "LastRunScope", "Scopes", "FailingScopes", "DetectExit", "RemediationExit",
        "PostDetectExit", "DetectErrorOutput", "RemediationErrorOutput", "ErrorCode", "Result", "RemediationStatus", "RemediationStatusText",
    };

    /// <summary>
    /// One CSV line. The IME writes the last run in the device clock ("11/16/2026 8:05:50 AM");
    /// the CSV has it in UTC ("2026-11-16T08:05:50Z"), converted from <paramref name="deviceZone"/>
    /// (the engine's own zone, which is the device clock's, when null). A time that cannot be read
    /// stays as the IME wrote it.
    /// </summary>
    public IReadOnlyList<object> Cells(TimeZoneInfo deviceZone = null) => new object[]
    {
        PolicyId, Version, Verdict, State, RunsAs, LastRunUtc(LastRun, deviceZone), LastRunScope, Scopes, FailingScopes, DetectExit, RemediationExit,
        PostDetectExit, DetectErrorOutput, RemediationErrorOutput, ErrorCode, Result, RemediationStatus, StatusText(RemediationStatus),
    };

    /// <summary>An IME time in device time as ISO 8601 UTC; the text as it is when it cannot be read or placed.</summary>
    public static string LastRunUtc(string deviceTime, TimeZoneInfo deviceZone = null)
    {
        var local = Workbook.ParseImeTimestamp(deviceTime);
        if (local == DateTime.MinValue) { return deviceTime ?? ""; }
        try
        {
            var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), deviceZone ?? TimeZoneInfo.Local);
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
        catch (ArgumentException)
        {
            // A time that does not exist in the zone (the hour skipped when the clock goes forward).
            return deviceTime;
        }
    }

    /// <summary>
    /// What the IME's RemediationStatus means, as <see cref="Remediation.Verdict"/> reads it; '' for a
    /// number this product does not know.
    /// </summary>
    public static string StatusText(int? status) => status switch
    {
        0 => "remediation not run",
        1 => "issue found and remediated; detection clean afterwards",
        2 => "remediation ran; detection still finds the issue",
        3 => "remediation failed",
        4 => "no issue detected",
        _ => "",
    };
}

/// <summary>The remediations on the device: the rows, and what is only counted.</summary>
public sealed class RemediationState
{
    public bool Present { get; set; }
    public int Policies { get; set; }
    public int Leftover { get; set; }
    public int OtherScopes { get; set; }
    public List<RemediationRow> Rows { get; } = new();
}

/// <summary>
/// Remediation results readout (read-only). The IME's own result per remediation policy and scope,
/// from SideCarPolicies\Scripts\Reports\&lt;scope&gt;\&lt;policyId&gt;_&lt;version&gt;\Result, with the last
/// run from Scripts\Execution. Never read: the scripts and their output text (only SYSTEM can read
/// the scripts, and the package ends up with the user); only whether a script wrote error output.
/// Policy names are not stored on the device; the policy ID is shown.
/// </summary>
public static class Remediation
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static readonly string[] VerdictOrder = { "DETECT-ERROR", "REMEDIATION-FAILED", "STILL-FAILING", "ISSUE-FOUND", "NOT-RUN", "UNKNOWN" };

    public static readonly IReadOnlyDictionary<string, string> VerdictMeaning = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DETECT-ERROR"] = "the detection script itself failed, so it cannot say whether there is an issue.",
        ["REMEDIATION-FAILED"] = "detection found an issue and the remediation script failed.",
        ["STILL-FAILING"] = "the remediation ran, but detection still finds the issue afterwards.",
        ["ISSUE-FOUND"] = "detection found an issue and no remediation ran.",
        ["NOT-RUN"] = "the remediation did not run, for a reason other than an assignment filter.",
        ["UNKNOWN"] = "a result this product does not recognise; see the codes.",
    };

    /// <summary>
    /// 0x87D30137 on a "not run" result has been seen only on policies with an assignment filter;
    /// it is read as "a filter excludes this device". That is an inference from observed data, not
    /// documented by Microsoft.
    /// </summary>
    public const long FilteredCode = 2278752567;

    private static readonly Regex PolicyFolder = new("^(" + ImeLog.GuidPattern + ")_([0-9]{1,9})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ScopeKey = new("^" + ImeLog.GuidPattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Verdict and state text for one result. RemediationStatus: 4 detection clean; 1 issue found,
    /// remediated and clean afterwards; 2 remediated, still failing; 3 remediation failed; 0 not run.
    /// </summary>
    public static (string Verdict, string State) Verdict(bool hasData, int? status, int? firstDetect, int? remediationExit, long code)
    {
        if (!hasData) { return ("UNKNOWN", "the result could not be read"); }
        if (code == FilteredCode) { return ("FILTERED", "not run: an assignment filter excludes this device"); }
        if (firstDetect != null && firstDetect != 0 && firstDetect != 1) { return ("DETECT-ERROR", string.Format(Invariant, "detection script failed (exit {0})", firstDetect)); }
        if (status == 4) { return ("OK", "no issue detected"); }
        if (status == 1) { return ("FIXED", "issue found and remediated; detection clean afterwards"); }
        if (status == 2) { return ("STILL-FAILING", "remediation ran, but detection still finds the issue"); }
        if (status == 3)
        {
            var text = "remediation script failed";
            if (remediationExit != null) { text += string.Format(Invariant, " (exit {0})", remediationExit); }
            return ("REMEDIATION-FAILED", text);
        }
        if (firstDetect == 1) { return ("ISSUE-FOUND", "issue detected; no remediation ran"); }
        if (status == 0)
        {
            return ("NOT-RUN", code != 0 ? "not run (error " + CodeKnowledge.FormatHex(code.ToString(Invariant)) + ")" : "not run");
        }
        return ("UNKNOWN", "unrecognised result");
    }

    private static int? IntOrNull(JsonNode node) =>
        node == null || node.Kind == JsonNode.NodeKind.Null ? null
        : int.TryParse(node.AsText(), NumberStyles.AllowLeadingSign, Invariant, out var number) ? number : null;

    private static string Text(JsonNode node) => node == null || node.Kind == JsonNode.NodeKind.Null ? "" : node.AsText();

    private sealed class ScopeResult
    {
        public string PolicyId;
        public int Version;
        public string ScopeRef;
        public string RunsAs;
        public string LastRun;
        public DateTime LastRunValue;
        public string Verdict;
        public string State;
        public int? DetectExit;
        public int? RemediationExit;
        public int? PostDetectExit;
        public string DetectErrorOutput;
        public string RemediationErrorOutput;
        public string ErrorCode;
        public int? Result;
        public int? RemediationStatus;
        public bool IsCurrent;
    }

    /// <summary>
    /// One row per remediation on the device now (its folder &lt;policyId&gt;_&lt;version&gt; is in
    /// IMECache\HealthScripts; only folder names are read), carrying the most recent result of its
    /// current version across the visible scopes, the number of those scopes that ran it and how
    /// many of them failed at their own last run. A current version that has not run anywhere yet is
    /// PENDING. Results of removed policies and earlier versions are only counted.
    /// </summary>
    public static RemediationState Read(RunContext run)
    {
        var result = new RemediationState();
        var registry = run.Services.Registry;
        var paths = run.Services.ImePaths;

        var cached = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            foreach (var folder in new DirectoryInfo(Path.Combine(paths.ContentCache, "HealthScripts")).GetDirectories())
            {
                var m = PolicyFolder.Match(folder.Name);
                if (!m.Success) { continue; }
                var policy = m.Groups[1].Value.ToLowerInvariant();
                var version = int.Parse(m.Groups[2].Value, Invariant);
                if (!cached.TryGetValue(policy, out var known) || version > known) { cached[policy] = version; }
            }
        }
        catch (Exception)
        {
            // No HealthScripts folder (or none readable): no remediation is on the device.
        }

        var reportsRoot = paths.SideCarKey + @"\Scripts\Reports";
        var executionRoot = paths.SideCarKey + @"\Scripts\Execution";
        var scopeKeys = new List<string>();
        if (registry.KeyExists(reportsRoot))
        {
            try { scopeKeys = registry.SubKeyNames(reportsRoot).Where(k => ScopeKey.IsMatch(k)).ToList(); }
            catch (Exception) { scopeKeys = new List<string>(); }
        }
        result.Present = cached.Count > 0 || scopeKeys.Count > 0;

        // Only the device scope and the caller's own: other users' results are not theirs to see.
        var visible = AppState.VisibleScopes(run);
        var results = new List<ScopeResult>();
        foreach (var scopeKey in scopeKeys)
        {
            var scopeId = scopeKey.ToLowerInvariant();
            if (!visible.Contains(scopeId)) { result.OtherScopes++; continue; }
            IReadOnlyList<string> policyKeys;
            try { policyKeys = registry.SubKeyNames(reportsRoot + "\\" + scopeKey); }
            catch (Exception) { continue; }
            foreach (var policyKey in policyKeys)
            {
                var m = PolicyFolder.Match(policyKey);
                if (!m.Success) { continue; }
                var policyId = m.Groups[1].Value.ToLowerInvariant();
                var version = int.Parse(m.Groups[2].Value, Invariant);
                var data = AppState.ReadStateMessage(registry, reportsRoot + "\\" + scopeKey + "\\" + policyKey, "Result").Data;
                var lastRun = MachineRegistry.AsText(ImeLog.SafeValue(registry, executionRoot + "\\" + scopeKey + "\\" + policyKey, "LastExecution")) ?? "";

                var info = data?.Get("Info");
                var status = IntOrNull(data?.Get("RemediationStatus"));
                var firstDetect = IntOrNull(info?.Get("FirstDetectExitCode"));
                var remediationExit = IntOrNull(info?.Get("RemediationExitCode"));
                var lastDetect = IntOrNull(info?.Get("LastDetectExitCode"));
                var code = StatusCode.Parse(Text(data?.Get("ErrorCode"))) ?? 0;
                var account = IntOrNull(data?.Get("RunAsAccount"));
                var runAs = account == 1 ? "System" : account == 2 ? "User" : "";
                // Whether a script wrote error output, never what it wrote.
                var detectError = string.IsNullOrWhiteSpace(Text(data?.Get("PreRemediationDetectScriptError"))) ? "" : "Yes";
                var remediationError = !string.IsNullOrWhiteSpace(Text(data?.Get("RemediationScriptErrorDetails"))) || !string.IsNullOrWhiteSpace(Text(data?.Get("PostRemediationDetectScriptError"))) ? "Yes" : "";
                var verdict = Verdict(data != null, status, firstDetect, remediationExit, code);
                results.Add(new ScopeResult
                {
                    PolicyId = policyId,
                    Version = version,
                    ScopeRef = AppState.ScopeRef(scopeId),
                    RunsAs = runAs,
                    LastRun = lastRun,
                    LastRunValue = Workbook.ParseImeTimestamp(lastRun),
                    Verdict = verdict.Verdict,
                    State = verdict.State,
                    DetectExit = firstDetect,
                    RemediationExit = remediationExit,
                    PostDetectExit = lastDetect,
                    DetectErrorOutput = detectError,
                    RemediationErrorOutput = remediationError,
                    ErrorCode = code != 0 ? CodeKnowledge.FormatHex(code.ToString(Invariant)) : "",
                    Result = IntOrNull(data?.Get("Result")),
                    RemediationStatus = status,
                    IsCurrent = cached.TryGetValue(policyId, out var current) && current == version,
                });
            }
        }

        result.Leftover = results.Count(r => !r.IsCurrent);
        result.Policies = cached.Count;
        foreach (var policy in cached.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var version = cached[policy];
            var mine = results.Where(r => r.PolicyId == policy && r.Version == version).ToList();
            var row = new RemediationRow { PolicyId = policy, Version = version };
            if (mine.Count == 0)
            {
                row.Verdict = "PENDING";
                row.State = string.Format(Invariant, "version {0} has not run on this device yet", version);
            }
            else
            {
                var latest = mine.OrderByDescending(r => r.LastRunValue).First();
                row.Verdict = latest.Verdict;
                row.State = latest.State;
                row.RunsAs = latest.RunsAs;
                row.LastRun = latest.LastRun;
                row.DetectExit = latest.DetectExit;
                row.RemediationExit = latest.RemediationExit;
                row.PostDetectExit = latest.PostDetectExit;
                row.DetectErrorOutput = latest.DetectErrorOutput;
                row.RemediationErrorOutput = latest.RemediationErrorOutput;
                row.ErrorCode = latest.ErrorCode;
                row.Result = latest.Result;
                row.RemediationStatus = latest.RemediationStatus;
                row.LastRunScope = latest.ScopeRef;
                row.Scopes = mine.Count;
                row.FailingScopes = mine.Count(r => VerdictOrder.Contains(r.Verdict));
                // The latest run decides; a failure left in another scope is stated, not promoted
                // (on a multi-session host that is often a user who has not signed in for months).
                var otherFailing = row.FailingScopes;
                if (VerdictOrder.Contains(latest.Verdict)) { otherFailing--; }
                if (otherFailing > 0) { row.State += "; failing in " + TextFormat.Count(otherFailing, "other scope") + " at their own last run"; }
            }
            result.Rows.Add(row);
        }
        return result;
    }

    public static int VerdictRank(string verdict)
    {
        var index = Array.IndexOf(VerdictOrder, verdict);
        return index < 0 ? 100 : index;
    }

    private static IEnumerable<RemediationRow> Sorted(IEnumerable<RemediationRow> rows) =>
        rows.OrderBy(r => VerdictRank(r.Verdict)).ThenBy(r => r.PolicyId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Logs and reports the remediations on the device: counts, the ones that need attention
    /// (capped), one line per verdict, findings and one remediations-state record.
    /// </summary>
    public static void WriteSummary(RunContext run, RemediationState state, int maxLines = 10, bool inPackage = false)
    {
        var leftoverText = "";
        if (state.Leftover > 0) { leftoverText = " Not shown: " + TextFormat.Count(state.Leftover, "older result") + " of removed remediations or earlier versions."; }
        if (state.OtherScopes > 0) { leftoverText += " Results of " + TextFormat.Count(state.OtherScopes, "other user") + " on this device are left out; they are not yours to see."; }
        if (state.Rows.Count == 0)
        {
            run.Log("Remediations: none on this device." + leftoverText);
            run.AddFinding(FindingLevel.Info, "REMEDIATIONS-NONE", "No remediations on this device");
            run.WriteRecord("remediations-state", RunContext.Field("policies", 0), RunContext.Field("leftover", state.Leftover));
            run.PersistentLog(string.Format(Invariant, "EndpointToolbelt:REMEDIATIONS-STATE policies=0 leftover={0}", state.Leftover));
            return;
        }
        var rows = state.Rows;
        var attention = rows.Where(r => VerdictOrder.Contains(r.Verdict)).ToList();
        var ok = rows.Count(r => r.Verdict == "OK");
        var fixedCount = rows.Count(r => r.Verdict == "FIXED");
        var filtered = rows.Count(r => r.Verdict == "FILTERED");
        var pending = rows.Count(r => r.Verdict == "PENDING");
        run.Log(string.Format(Invariant, "Remediations: {0} on this device; {1} no issue, {2} fixed, {3} filtered out, {4} not run yet at their current version, {5} need attention.{6}",
            rows.Count, ok, fixedCount, filtered, pending, attention.Count, leftoverText));

        if (attention.Count > 0)
        {
            var sorted = Sorted(attention).ToList();
            var shown = 0;
            foreach (var r in sorted)
            {
                if (shown >= maxLines) { break; }
                var where = r.LastRunScope;
                if (r.RunsAs.Length > 0) { where += ", " + r.RunsAs; }
                var line = string.Format(Invariant, "  {0} {1} v{2} [{3}]: {4}", r.Verdict, r.PolicyId, r.Version, where, r.State);
                if (r.LastRun.Length > 0) { line += "; last run " + r.LastRun; }
                run.Log(line);
                shown++;
            }
            if (sorted.Count > shown) { run.Log(string.Format(Invariant, "  ... and {0} more. The full table is in a support package.", sorted.Count - shown)); }
            foreach (var v in VerdictOrder)
            {
                if (attention.Any(r => r.Verdict == v)) { run.Log("  " + v + ": " + VerdictMeaning[v]); }
            }
            run.Log("  Names are not stored on the device: look the policy ID up under Remediations in Intune, where the script output is shown per device.");
            var findings = 0;
            foreach (var r in sorted)
            {
                if (findings >= 5) { break; }
                run.AddFinding(FindingLevel.Warning, "REMEDIATION-" + r.Verdict, string.Format(Invariant, "Remediation {0} (v{1}): {2}", r.PolicyId.Substring(0, 8), r.Version, r.State),
                    string.Format(Invariant, "{0} Policy ID {1}; last run {2} in {3}.", VerdictMeaning[r.Verdict], r.PolicyId, r.LastRun, r.LastRunScope),
                    "Contact your IT support and mention the policy ID.",
                    "Look the policy ID up under Remediations in Intune; the script output for this device is shown there. This product never reads the scripts.");
                findings++;
            }
            if (sorted.Count > findings)
            {
                run.AddFinding(FindingLevel.Info, "REMEDIATIONS-MORE", TextFormat.Count(sorted.Count - findings, "more remediation") + " need attention", "",
                    inPackage ? "See the Remediations tab of IntuneState.xlsx in this support package." : "Create a support package: the Remediations tab of IntuneState.xlsx lists them all.");
            }
        }
        else
        {
            run.AddFinding(FindingLevel.Ok, "REMEDIATIONS-OK", "No remediation needs attention",
                string.Format(Invariant, "{0} on this device: {1} no issue, {2} fixed, {3} filtered out, {4} not run yet at their current version.", rows.Count, ok, fixedCount, filtered, pending));
        }
        run.WriteRecord("remediations-state",
            RunContext.Field("policies", rows.Count), RunContext.Field("ok", ok), RunContext.Field("fixed", fixedCount), RunContext.Field("filtered", filtered),
            RunContext.Field("pending", pending), RunContext.Field("attention", attention.Count), RunContext.Field("leftover", state.Leftover));
        run.PersistentLog(string.Format(Invariant, "EndpointToolbelt:REMEDIATIONS-STATE policies={0} ok={1} fixed={2} filtered={3} pending={4} attention={5} leftover={6}",
            rows.Count, ok, fixedCount, filtered, pending, attention.Count, state.Leftover));
    }

    /// <summary>The Remediations tab: one row per remediation, those that need attention first.</summary>
    public static Sheet Sheet(RemediationState state)
    {
        var empty = "No remediations on this device.";
        IReadOnlyList<object> rows = Array.Empty<object>();
        if (state == null) { empty = "The remediation results could not be read; see the log."; }
        else if (state.Rows.Count > 0) { rows = Sorted(state.Rows).Cast<object>().ToList(); }
        static Func<object, object> F(Func<RemediationRow, object> get) => o => get((RemediationRow)o);
        return new Sheet
        {
            Name = "Remediations",
            TableName = "RemediationState",
            Columns = new[]
            {
                new SheetColumn("Policy ID", 38, CellKind.Text, F(r => r.PolicyId)),
                new SheetColumn("Version", 9, CellKind.Number, F(r => r.Version)),
                new SheetColumn("Verdict", 20, CellKind.Verdict, F(r => r.Verdict)),
                new SheetColumn("State", 60, CellKind.Text, F(r => r.State)),
                new SheetColumn("Runs as", 9, CellKind.Text, F(r => r.RunsAs)),
                new SheetColumn("Last run (device time)", 22, CellKind.LocalDate, F(r => r.LastRun)),
                new SheetColumn("Last run in scope", 16, CellKind.Text, F(r => r.LastRunScope)),
                new SheetColumn("Scopes", 8, CellKind.Number, F(r => r.Scopes)),
                new SheetColumn("Failing scopes", 9, CellKind.Number, F(r => r.FailingScopes)),
                new SheetColumn("Detection exit", 11, CellKind.Number, F(r => r.DetectExit)),
                new SheetColumn("Remediation exit", 12, CellKind.Number, F(r => r.RemediationExit)),
                new SheetColumn("Post-detection exit", 13, CellKind.Number, F(r => r.PostDetectExit)),
                new SheetColumn("Detection error output", 13, CellKind.Text, F(r => r.DetectErrorOutput)),
                new SheetColumn("Remediation error output", 13, CellKind.Text, F(r => r.RemediationErrorOutput)),
                new SheetColumn("Error code", 13, CellKind.Text, F(r => r.ErrorCode)),
                new SheetColumn("Result", 8, CellKind.Number, F(r => r.Result)),
                new SheetColumn("Remediation status", 12, CellKind.Number, F(r => r.RemediationStatus)),
                new SheetColumn("Remediation status (meaning)", 30, CellKind.Text, F(r => RemediationRow.StatusText(r.RemediationStatus))),
            },
            Rows = rows,
            Attention = o => VerdictOrder.Contains(((RemediationRow)o).Verdict),
            EmptyText = empty,
        };
    }
}
