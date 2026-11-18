using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.Support;

/// <summary>One row of the "IME log summary" tab.</summary>
public sealed class ImeLogSummaryRow
{
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string AppId { get; set; } = "";
    public string Phase { get; set; } = "";
    public string ExitCode { get; set; } = "";
    public long? DownloadBytes { get; set; }
    public long? SizeBytes { get; set; }
    public string LastSeenUtc { get; set; } = "";
    public string NextRetryUtc { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>The IME's newest successful check-in and the newest failed one after it.</summary>
public sealed class ImeCheckIn
{
    public bool Available { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastFailureUtc { get; set; }
    public string Error { get; set; } = "";

    /// <summary>The IME's newest start and the first check-in it announced then.</summary>
    public DateTime? StartUtc { get; set; }
    public DateTime? FirstCheckInDueUtc { get; set; }
}

/// <summary>One failed or warned configuration profile setting.</summary>
public sealed class ProfileErrorRow
{
    public string TimeUtc { get; set; } = "";
    public string Level { get; set; } = "";
    public int EventId { get; set; }
    public string Setting { get; set; } = "";
    public string Result { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>One Enrollment Status Page entry.</summary>
public sealed class EspRow
{
    public string Scope { get; set; } = "";
    public string Category { get; set; } = "";
    public string Item { get; set; } = "";
    public string State { get; set; } = "";
    public string Raw { get; set; } = "";
}

/// <summary>
/// The tabs of IntuneState.xlsx besides Remediations, and the rows behind them. Read-only.
/// </summary>
public static class PackageSheets
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static Func<object, object> F<T>(Func<T, object> get) => o => get((T)o);

    // --- Apps ------------------------------------------------------------------------------------

    public static readonly string[] AppStateHeaders =
    {
        "ScopeRef", "AppId", "Name", "Revision", "Intent", "Verdict", "State", "Installed", "EnforcementState", "ErrorCode", "ErrorText",
        "ComplianceState", "DesiredState", "ImeStatus", "ImeApplicability", "ProductVersion", "LastUpdatedUtc", "EnforcementJson", "ComplianceJson",
    };

    /// <summary>One app row as Win32AppState.csv has it, in the column order of the table; LastUpdatedUtc in ISO 8601 UTC.</summary>
    public static IReadOnlyList<object> AppStateCells(AppStateRow r) => new object[]
    {
        r.ScopeRef, r.AppId, r.Name, r.Revision, r.Intent, r.Verdict, r.State, r.Installed, r.EnforcementState, r.ErrorCode, r.ErrorText,
        r.ComplianceState, r.DesiredState, r.ImeStatus, r.ImeApplicability, r.ProductVersion, IsoUtc(r.LastUpdatedUtc), r.EnforcementJson, r.ComplianceJson,
    };

    /// <summary>
    /// A time the IME wrote in UTC without saying so ("11/10/2026 11:01:40") as ISO 8601 UTC
    /// ("2026-11-10T11:01:40Z"), so no reader takes it for a local time or a day-first date; the
    /// text as it is when it cannot be read.
    /// </summary>
    public static string IsoUtc(string imeUtc)
    {
        if (string.IsNullOrEmpty(imeUtc)) { return ""; }
        return DateTime.TryParseExact(imeUtc, Workbook.ImeTimestampFormats, Invariant, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)
            ? utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Invariant)
            : imeUtc;
    }

    /// <summary>The Apps tab: attention first in verdict order, then by name.</summary>
    public static Sheet AppStateSheet(RunContext run, AppStateReadout state)
    {
        IReadOnlyList<object> rows = Array.Empty<object>();
        var empty = "No Win32 app state was recorded at collection time.";
        if (state == null)
        {
            empty = "The Win32 app state could not be read; see the log.";
        }
        else if (state.Rows.Count > 0)
        {
            rows = state.Rows.OrderBy(r => AppState.VerdictRank(r.Verdict)).ThenBy(r => r.Name, StringComparer.InvariantCultureIgnoreCase)
                .ThenBy(r => r.AppId, StringComparer.InvariantCultureIgnoreCase).Cast<object>().ToList();
        }
        else
        {
            var resetText = AppState.LastFullResetText(run, plainTime: true);
            if (resetText.Length > 0) { empty = empty + " " + resetText; }
        }
        return new Sheet
        {
            Name = "Apps",
            TableName = "AppState",
            Columns = new[]
            {
                new SheetColumn("Name", 48, CellKind.Text, F<AppStateRow>(r => r.Name)),
                new SheetColumn("Verdict", 16, CellKind.Verdict, F<AppStateRow>(r => r.Verdict)),
                new SheetColumn("State", 56, CellKind.Text, F<AppStateRow>(r => r.State)),
                new SheetColumn("Installed", 11, CellKind.Text, F<AppStateRow>(r => r.Installed)),
                new SheetColumn("IME status", 16, CellKind.Text, F<AppStateRow>(r => r.ImeStatus)),
                new SheetColumn("IME applicability", 28, CellKind.Text, F<AppStateRow>(r => r.ImeApplicability)),
                new SheetColumn("Assignment", 26, CellKind.Text, F<AppStateRow>(r => r.Intent)),
                new SheetColumn("Scope", 15, CellKind.Text, F<AppStateRow>(r => r.ScopeRef)),
                new SheetColumn("Error code", 13, CellKind.Text, F<AppStateRow>(r => r.ErrorCode)),
                new SheetColumn("Error text", 40, CellKind.Text, F<AppStateRow>(r => r.ErrorText)),
                new SheetColumn("Product version", 17, CellKind.Text, F<AppStateRow>(r => r.ProductVersion)),
                new SheetColumn("Last updated (device time)", 22, CellKind.UtcDate, F<AppStateRow>(r => r.LastUpdatedUtc)),
                new SheetColumn("App ID", 38, CellKind.Text, F<AppStateRow>(r => r.AppId)),
                new SheetColumn("Enforcement state", 12, CellKind.Number, F<AppStateRow>(r => r.EnforcementState)),
                new SheetColumn("Enforcement message (raw)", 44, CellKind.Text, F<AppStateRow>(r => r.EnforcementJson)),
                new SheetColumn("Compliance message (raw)", 70, CellKind.Text, F<AppStateRow>(r => r.ComplianceJson)),
            },
            Rows = rows,
            Attention = o => AppState.VerdictOrder.Contains(((AppStateRow)o).Verdict),
            EmptyText = empty,
        };
    }

    // --- MSI line-of-business apps -----------------------------------------------------------------

    /// <summary>
    /// The columns of MsiApps.csv: the device's and the requesting user's MSI line-of-business apps as the
    /// MDM stack recorded them. Never the SID (the scope is "device" or "user") and never the download address
    /// beyond its host.
    /// </summary>
    public static readonly string[] MsiAppHeaders =
    {
        "Scope", "ProductCode", "Name", "Version", "Status", "Verdict", "State", "LastError", "DownloadHost", "RetryIndex", "RetryCount", "LastWrittenUtc",
    };

    /// <summary>One MSI app as MsiApps.csv has it, in the column order of <see cref="MsiAppHeaders"/>.</summary>
    public static IReadOnlyList<object> MsiAppCells(MsiAppRow r) => new object[]
    {
        r.Scope, ProductCode(r.ProductCode), r.Name, r.Version, r.Status, r.Verdict, r.State, r.Code, r.DownloadHost, r.RetryIndex, r.RetryCount,
        r.ObservedUtc is DateTime at ? at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Invariant) : "",
    };

    /// <summary>A product code as Windows Installer writes it: upper case, in braces.</summary>
    public static string ProductCode(string code) => "{" + (code ?? "").Trim('{', '}').ToUpperInvariant() + "}";

    /// <summary>The attempt as "3 of 3" (the MDM stack counts from 0); '' when it is not recorded.</summary>
    public static string MsiAttempt(MsiAppRow r)
        => r.RetryIndex is int index && r.RetryCount is int count && count > 0
            ? Math.Min(index + 1, count).ToString(Invariant) + " of " + count.ToString(Invariant)
            : "";

    /// <summary>The "MSI apps" tab: attention first in verdict order, then by name and product code.</summary>
    public static Sheet MsiAppSheet(MsiAppReadout readout)
    {
        IReadOnlyList<object> rows = Array.Empty<object>();
        string empty;
        if (readout == null) { empty = "The MSI line-of-business apps could not be read; see the log."; }
        else if (!readout.Present) { empty = "No MSI line-of-business apps are recorded on this device."; }
        else
        {
            empty = "No MSI line-of-business app is recorded for the device or for the user who created this package.";
            rows = readout.Rows.OrderBy(r => AppState.VerdictRank(r.Verdict)).ThenBy(r => r.Name, StringComparer.InvariantCultureIgnoreCase)
                .ThenBy(r => r.ProductCode, StringComparer.OrdinalIgnoreCase).Cast<object>().ToList();
        }
        if (readout != null && readout.OtherScopes > 0) { empty += " " + MsiOthers(readout); }
        return new Sheet
        {
            Name = "MSI apps",
            TableName = "MsiApps",
            Columns = new[]
            {
                new SheetColumn("Name", 40, CellKind.Text, F<MsiAppRow>(r => r.Name)),
                new SheetColumn("Verdict", 16, CellKind.Verdict, F<MsiAppRow>(r => r.Verdict)),
                new SheetColumn("State", 56, CellKind.Text, F<MsiAppRow>(r => r.State)),
                new SheetColumn("Status", 9, CellKind.Number, F<MsiAppRow>(r => r.Status)),
                new SheetColumn("Error code", 13, CellKind.Text, F<MsiAppRow>(r => r.Code)),
                new SheetColumn("Version", 17, CellKind.Text, F<MsiAppRow>(r => r.Version)),
                new SheetColumn("Scope", 9, CellKind.Text, F<MsiAppRow>(r => r.Scope)),
                new SheetColumn("Product code", 40, CellKind.Text, F<MsiAppRow>(r => ProductCode(r.ProductCode))),
                new SheetColumn("Download host", 32, CellKind.Text, F<MsiAppRow>(r => r.DownloadHost)),
                new SheetColumn("Attempt", 9, CellKind.Text, F<MsiAppRow>(MsiAttempt)),
                new SheetColumn("Last written (device time)", 22, CellKind.UtcDate, F<MsiAppRow>(r => Workbook.SheetUtc(r.ObservedUtc))),
            },
            Rows = rows,
            Attention = o => AppState.VerdictOrder.Contains(((MsiAppRow)o).Verdict),
            EmptyText = empty,
        };
    }

    /// <summary>The MSI apps for the package's log, counts only: how many, whose, how many need attention, and other users counted.</summary>
    public static string MsiAppSummary(MsiAppReadout readout)
    {
        if (readout == null) { return "MSI line-of-business apps: could not be read."; }
        if (!readout.Present) { return "MSI line-of-business apps: none recorded on this device."; }
        var device = readout.Rows.Count(r => r.Scope == "device");
        var attention = readout.Rows.Count(r => AppState.VerdictOrder.Contains(r.Verdict));
        var text = string.Format(Invariant, "MSI line-of-business apps: {0} for the device, {1} for the user; {2} attention (MsiApps.csv and the tab \"MSI apps\").",
            device, readout.Rows.Count - device, TextFormat.Count(attention, "needs", "need"));
        return readout.OtherScopes > 0 ? text + " " + MsiOthers(readout) : text;
    }

    private static string MsiOthers(MsiAppReadout readout)
        => string.Format(Invariant, "Other users: {0} of {1}, counted only.", TextFormat.Count(readout.OtherRecords, "app record"), TextFormat.Count(readout.OtherScopes, "user"));

    // --- MSIX line-of-business apps ----------------------------------------------------------------

    /// <summary>
    /// The columns of MsixApps.csv: the device's and the requesting user's MSIX line-of-business app installs
    /// the MDM stack holds (a successful install leaves no record). A sheet of its own next to "MSI apps": an MSIX
    /// app is known by its package family name, and its record has no download host, attempts or write time, so
    /// one table would leave half its columns empty and change the columns of MsiApps.csv. Never the SID.
    /// </summary>
    public static readonly string[] MsixAppHeaders = { "Scope", "PackageFamilyName", "VersionInstalled", "Status", "Verdict", "State", "LastError" };

    /// <summary>One MSIX app install as MsixApps.csv has it, in the column order of <see cref="MsixAppHeaders"/>.</summary>
    public static IReadOnlyList<object> MsixAppCells(MsixAppRow r) => new object[] { r.Scope, r.PackageFamilyName, r.Version, r.Status, r.Verdict, r.State, r.Code };

    /// <summary>The "MSIX apps" tab: attention first in verdict order, then by package family name.</summary>
    public static Sheet MsixAppSheet(MsixAppReadout readout)
    {
        IReadOnlyList<object> rows = Array.Empty<object>();
        string empty;
        if (readout == null || !readout.Present) { empty = "The MSIX line-of-business app installs could not be read; see the log."; }
        else
        {
            empty = "No MSIX line-of-business app install is pending or failed for the device or for the user who created this package (a successful install leaves no record).";
            rows = readout.Rows.OrderBy(r => AppState.VerdictRank(r.Verdict)).ThenBy(r => r.PackageFamilyName, StringComparer.OrdinalIgnoreCase).Cast<object>().ToList();
        }
        if (readout != null && readout.UserUnreadable) { empty += " The user's own installs could not be read."; }
        return new Sheet
        {
            Name = "MSIX apps",
            TableName = "MsixApps",
            Columns = new[]
            {
                new SheetColumn("Package family", 48, CellKind.Text, F<MsixAppRow>(r => r.PackageFamilyName)),
                new SheetColumn("Verdict", 16, CellKind.Verdict, F<MsixAppRow>(r => r.Verdict)),
                new SheetColumn("State", 56, CellKind.Text, F<MsixAppRow>(r => r.State)),
                new SheetColumn("Status", 9, CellKind.Number, F<MsixAppRow>(r => r.Status)),
                new SheetColumn("Error code", 13, CellKind.Text, F<MsixAppRow>(r => r.Code)),
                new SheetColumn("Version installed", 17, CellKind.Text, F<MsixAppRow>(r => r.Version)),
                new SheetColumn("Scope", 9, CellKind.Text, F<MsixAppRow>(r => r.Scope)),
            },
            Rows = rows,
            Attention = o => AppState.VerdictOrder.Contains(((MsixAppRow)o).Verdict),
            EmptyText = empty,
        };
    }

    /// <summary>The MSIX app installs for the package's log, counts only.</summary>
    public static string MsixAppSummary(MsixAppReadout readout)
    {
        if (readout == null || !readout.Present) { return "MSIX line-of-business app installs: could not be read."; }
        var device = readout.Rows.Count(r => r.Scope == "device");
        var attention = readout.Rows.Count(r => AppState.VerdictOrder.Contains(r.Verdict));
        return string.Format(Invariant, "MSIX line-of-business app installs: {0} for the device, {1} for the user; {2} attention (MsixApps.csv and the tab \"MSIX apps\"). Other users' are not read.",
            device, readout.Rows.Count - device, TextFormat.Count(attention, "needs", "need"));
    }

    // --- IME log summary ---------------------------------------------------------------------------

    /// <summary>The IME logs whose error and warning records are counted per component. Counts and times only: their messages can hold script output, command lines or paths.</summary>
    public static readonly string[] ErrorCountFiles = { "IntuneManagementExtension*.log", "AppWorkload*.log", "AgentExecutor*.log", "ClientHealth*.log" };

    private static readonly Regex NotCodeChar = new("[^A-Za-z0-9-]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The IME's newest successful check-in and the newest failed one after it, from the same rules
    /// the service uses for the health view. Available false: no log or no table.
    /// </summary>
    public static ImeCheckIn CheckIn(RunContext run, int days = 30, int maxSeconds = 20)
    {
        var result = new ImeCheckIn();
        ImeAppRules table;
        try { table = ImeLog.Rules(run.Services.ImePaths.PatternFile); }
        catch (Exception) { return result; }
        var notBefore = run.Services.Clock.UtcNow.AddDays(-days);
        var records = new List<(DateTime Utc, string Message)>();
        foreach (var filter in table.CheckInFiles)
        {
            var read = CmTraceReader.Read(run.Services.ImePaths.LogsFolder, filter, () => run.Services.Clock.Elapsed, 2, 4L * 1024 * 1024, maxSeconds, notBefore);
            if (read.Files > 0) { result.Available = true; }
            records.AddRange(read.Records.Select(r => (r.Utc, r.Message)));
        }
        // The same evaluation as the service's health view, start-up included.
        var matchers = table.CheckInRules.Select(r => new ImeCheckInMatcher { Kind = r.Kind, Error = r.Error, Regex = r.Regex }).ToList();
        var facts = ImeCheckInEvidence.Evaluate(records, matchers);
        result.LastSuccessUtc = facts.LastSuccessUtc;
        result.StartUtc = facts.StartUtc;
        result.FirstCheckInDueUtc = facts.FirstCheckInDueUtc;
        if (facts.LastFailureUtc != null)
        {
            result.LastFailureUtc = facts.LastFailureUtc;
            // Only a short code-like token, never log text.
            result.Error = NotCodeChar.Replace(facts.Error ?? "", "");
            if (result.Error.Length > 40) { result.Error = result.Error.Substring(0, 40); }
        }
        return result;
    }

    private sealed class ComponentCount
    {
        public int Errors;
        public int Warnings;
        public DateTime? LastErrorUtc;
    }

    /// <summary>
    /// Rows for the "IME log summary" tab: per app of the scopes this user may see, how far its
    /// newest attempt got and when the IME retries it; the last check-in and a failure after it; and
    /// per log and component how many errors and warnings the last days hold. Never install command
    /// lines, scripts or log text.
    /// </summary>
    public static List<ImeLogSummaryRow> ImeLogSummaryRows(RunContext run, AppStateReadout appState, int days = 7)
    {
        var rows = new List<ImeLogSummaryRow>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = new List<string>();
        if (appState != null)
        {
            foreach (var r in appState.Rows) { names[r.AppId] = r.Name; }
            // An app another user also has gets no log evidence: a line could be their attempt.
            ids = appState.Rows.Select(r => r.AppId).Distinct().Where(id => !appState.OtherAppIds.Contains(id)).ToList();
        }
        var attempts = new Dictionary<string, ImeAppAttempt>(StringComparer.Ordinal);
        if (ids.Count > 0)
        {
            var evidence = ImeLog.Attempts(run, ids, days);
            if (evidence.Available) { attempts = evidence.Attempts; }
        }
        IReadOnlyDictionary<string, ImeRetryEntry> schedule;
        try { schedule = ImeLog.RetrySchedule(run); }
        catch (Exception) { schedule = new Dictionary<string, ImeRetryEntry>(); }
        foreach (var id in ids)
        {
            if (!attempts.ContainsKey(id) && !schedule.ContainsKey(id)) { continue; }
            var row = new ImeLogSummaryRow { Kind = "App", Name = names.TryGetValue(id, out var name) ? name ?? "" : "", AppId = id };
            if (attempts.TryGetValue(id, out var a))
            {
                row.Phase = ImeLog.FormatPhase(a);
                row.ExitCode = a.ExitCode;
                if (a.BytesDone >= 0) { row.DownloadBytes = a.BytesDone; }
                if (a.BytesTotal >= 0) { row.SizeBytes = a.BytesTotal; }
                row.LastSeenUtc = Workbook.SheetUtc(a.LastSeenUtc);
                var stuck = ImeLog.Stuck(a);
                if (stuck.Length > 0) { row.Detail = "Looks stuck: " + stuck; }
            }
            else
            {
                row.Phase = string.Format(Invariant, "no attempt in the last {0} days of the log", days);
            }
            if (schedule.TryGetValue(id, out var retry))
            {
                row.NextRetryUtc = Workbook.SheetUtc(retry.NextUtc);
                var note = string.Format(Invariant, "Retry estimate: {0} h after the failure the IME recorded.", ImeLog.RetryHours);
                row.Detail = row.Detail.Length > 0 ? row.Detail + " " + note : note;
            }
            rows.Add(row);
        }

        var checkIn = CheckIn(run);
        var success = new ImeLogSummaryRow { Kind = "Check-in", Name = "Last successful check-in" };
        if (checkIn.Available)
        {
            success.LastSeenUtc = Workbook.SheetUtc(checkIn.LastSuccessUtc);
            if (checkIn.LastSuccessUtc == null) { success.Detail = "None in the IME logs of the last 30 days."; }
        }
        else
        {
            success.Detail = "No IME log could be read.";
        }
        rows.Add(success);
        if (checkIn.LastFailureUtc != null)
        {
            rows.Add(new ImeLogSummaryRow
            {
                Kind = "Check-in",
                Name = "Failed check-in after it",
                ExitCode = checkIn.Error,
                LastSeenUtc = Workbook.SheetUtc(checkIn.LastFailureUtc),
                Detail = "The newest attempt failed; the code is the HTTP status, \"token\", \"proxy\" or the network error the log names.",
            });
        }
        if (checkIn.StartUtc != null)
        {
            rows.Add(new ImeLogSummaryRow
            {
                Kind = "Check-in",
                Name = "Last IME start",
                LastSeenUtc = Workbook.SheetUtc(checkIn.StartUtc),
                NextRetryUtc = Workbook.SheetUtc(checkIn.FirstCheckInDueUtc),
                Detail = "The IME delays its first check-in after a start or restart; the next column is when it said it would check in. A failure logged in that time does not count as a failed check-in.",
            });
        }

        var notBefore = run.Services.Clock.UtcNow.AddDays(-days);
        foreach (var filter in ErrorCountFiles)
        {
            var read = CmTraceReader.Read(run.Services.ImePaths.LogsFolder, filter, () => run.Services.Clock.Elapsed, 2, 4L * 1024 * 1024, 15, notBefore);
            if (read.Files == 0) { continue; }
            var byComponent = new Dictionary<string, ComponentCount>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in read.Records)
            {
                if (record.Type != 2 && record.Type != 3) { continue; }
                var component = string.IsNullOrEmpty(record.Component) ? "(none)" : record.Component;
                if (!byComponent.TryGetValue(component, out var c)) { c = byComponent[component] = new ComponentCount(); }
                if (record.Type == 3)
                {
                    c.Errors++;
                    if (c.LastErrorUtc == null || record.Utc > c.LastErrorUtc) { c.LastErrorUtc = record.Utc; }
                }
                else
                {
                    c.Warnings++;
                }
            }
            var logName = filter.Replace("*", "");
            if (byComponent.Count == 0)
            {
                rows.Add(new ImeLogSummaryRow { Kind = "Log errors", Name = logName, Detail = string.Format(Invariant, "No errors or warnings in the last {0} days.", days) });
                continue;
            }
            foreach (var component in byComponent.Keys.OrderBy(k => k, StringComparer.InvariantCultureIgnoreCase))
            {
                var c = byComponent[component];
                rows.Add(new ImeLogSummaryRow
                {
                    Kind = "Log errors",
                    Name = logName + ": " + component,
                    LastSeenUtc = Workbook.SheetUtc(c.LastErrorUtc),
                    Detail = string.Format(Invariant, "{0}, {1} in the last {2} days (newest error at Last seen).", TextFormat.Count(c.Errors, "error"), TextFormat.Count(c.Warnings, "warning"), days),
                });
            }
        }
        return rows;
    }

    private static readonly Regex FailedOrNotDetected = new("failed|not detected", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The "IME log summary" tab. Rows that failed, look stuck or show a failed check-in are shaded.</summary>
    public static Sheet ImeLogSummarySheet(IReadOnlyList<ImeLogSummaryRow> rows, string emptyText = "The IME logs could not be read; see the log.") => new()
    {
        Name = "IME log summary",
        TableName = "ImeLogSummary",
        Columns = new[]
        {
            new SheetColumn("Kind", 12, CellKind.Text, F<ImeLogSummaryRow>(r => r.Kind)),
            new SheetColumn("Name", 44, CellKind.Text, F<ImeLogSummaryRow>(r => r.Name)),
            new SheetColumn("Last phase", 40, CellKind.Verdict, F<ImeLogSummaryRow>(r => r.Phase)),
            new SheetColumn("Exit or error code", 18, CellKind.Text, F<ImeLogSummaryRow>(r => r.ExitCode)),
            new SheetColumn("Downloaded (bytes)", 18, CellKind.Number, F<ImeLogSummaryRow>(r => r.DownloadBytes)),
            new SheetColumn("Size (bytes)", 16, CellKind.Number, F<ImeLogSummaryRow>(r => r.SizeBytes)),
            new SheetColumn("Last seen (device time)", 22, CellKind.UtcDate, F<ImeLogSummaryRow>(r => r.LastSeenUtc)),
            new SheetColumn("Next retry, estimate (device time)", 22, CellKind.UtcDate, F<ImeLogSummaryRow>(r => r.NextRetryUtc)),
            new SheetColumn("Detail", 70, CellKind.Text, F<ImeLogSummaryRow>(r => r.Detail)),
            new SheetColumn("App ID", 38, CellKind.Text, F<ImeLogSummaryRow>(r => r.AppId)),
        },
        Rows = (rows ?? Array.Empty<ImeLogSummaryRow>()).Where(r => r != null).Cast<object>().ToList(),
        Attention = o =>
        {
            var r = (ImeLogSummaryRow)o;
            return FailedOrNotDetected.IsMatch(r.Phase ?? "") || (r.Detail ?? "").StartsWith("Looks stuck", StringComparison.OrdinalIgnoreCase) || (r.Kind == "Check-in" && !string.IsNullOrEmpty(r.ExitCode));
        },
        EmptyText = emptyText,
    };

    // --- Configuration profiles -------------------------------------------------------------------

    public const int ProfileErrorMaxRows = 500;

    /// <summary>Settings Windows reports on every enrolled device without a consequence (the same list as the service's KnownHarmlessPaths).</summary>
    public static readonly string[] HarmlessPaths = { "/Policy/ConfigOperations/ADMXInstall/Receiver/Properties/Policy/FakePolicy/", "/DevicePreparation/PageStatus", "/CertPinning/PinRules" };

    private static readonly Regex HexText = new("^0x[0-9A-Fa-f]{8}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly int[] ProblemLevels = { 1, 2, 3 };

    /// <summary>
    /// A result value as "0x80070005": a "0x" string as it is (upper-case digits), a negative 32-bit
    /// number as its unsigned form; "" otherwise.
    /// </summary>
    public static string HresultText(object value)
    {
        if (value == null) { return ""; }
        var text = Convert.ToString(value, Invariant) ?? "";
        if (HexText.IsMatch(text)) { return "0x" + text.Substring(2).ToUpperInvariant(); }
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, Invariant, out var number) && number < 0 && number >= -2147483648L)
        {
            return "0x" + (number + 4294967296L).ToString("X8", Invariant);
        }
        return "";
    }

    /// <summary>
    /// MDM policy settings that failed or warned in the last 7 days, newest first, from the MDM
    /// admin log. Microsoft documents that this log shows policy failures with the setting's CSP path
    /// and to filter on its levels, not what each event ID means: so rows are taken by level, and
    /// each row holds the CSP path and the result code found in the event's fields. Message text is
    /// never kept (it can hold policy values). Events written from a user session are not counted.
    /// </summary>
    public static List<ProfileErrorRow> ProfileErrorRows(RunContext run)
    {
        IReadOnlyList<MdmEvent> raw;
        try
        {
            raw = run.Services.MdmEvents.ReadLevels(Mdm.AdminLog, Mdm.Provider, ProblemLevels, run.Services.Clock.UtcNow.AddDays(-7), ProfileErrorMaxRows);
        }
        catch (Exception ex)
        {
            run.Log("Could not read the MDM admin log: " + ex.Message, LogSeverity.Warning);
            return new List<ProfileErrorRow>();
        }
        var rows = new List<ProfileErrorRow>();
        foreach (var record in raw)
        {
            if (!Mdm.IsTrustedWriter(run, record.ProcessId)) { continue; }
            var row = new ProfileErrorRow
            {
                TimeUtc = Workbook.SheetUtc(record.TimeCreatedUtc),
                Level = record.Level == 3 ? "Warning" : "Error",
                EventId = record.Id,
            };
            foreach (var value in record.Values)
            {
                if (row.Setting.Length == 0 && value != null && value.StartsWith("./", StringComparison.Ordinal))
                {
                    var setting = RunContext.PlainText(value);
                    if (setting.Length > 300) { setting = setting.Substring(0, 300); }
                    row.Setting = setting;
                    if (HarmlessPaths.Any(h => setting.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)) { row.Note = "Known harmless: Windows reports this on every device"; }
                }
                else if (row.Result.Length == 0)
                {
                    row.Result = HresultText(value);
                }
            }
            rows.Add(row);
        }
        return rows;
    }

    public static Sheet ProfileErrorSheet(IReadOnlyList<ProfileErrorRow> rows) => new()
    {
        Name = "Configuration profiles",
        TableName = "ConfigurationProfiles",
        Columns = new[]
        {
            new SheetColumn("Time (device time)", 22, CellKind.UtcDate, F<ProfileErrorRow>(r => r.TimeUtc)),
            new SheetColumn("Level", 10, CellKind.Verdict, F<ProfileErrorRow>(r => r.Level)),
            new SheetColumn("Event ID", 10, CellKind.Number, F<ProfileErrorRow>(r => r.EventId)),
            new SheetColumn("Setting (CSP path)", 90, CellKind.Text, F<ProfileErrorRow>(r => r.Setting)),
            new SheetColumn("Result code", 14, CellKind.Text, F<ProfileErrorRow>(r => r.Result)),
            new SheetColumn("Note", 50, CellKind.Text, F<ProfileErrorRow>(r => r.Note)),
        },
        Rows = (rows ?? Array.Empty<ProfileErrorRow>()).Where(r => r != null).Cast<object>().ToList(),
        Attention = o => ((ProfileErrorRow)o).Level == "Error" && string.IsNullOrEmpty(((ProfileErrorRow)o).Note),
        EmptyText = "No failed or warned configuration profile settings in the MDM admin log of the last 7 days.",
    };

    // --- Enrollment Status Page -------------------------------------------------------------------

    public const int EspMaxRows = 2000;
    public const string EspRoot = @"SOFTWARE\Microsoft\Windows\Autopilot\EnrollmentStatusTracking";
    private static readonly Regex Sid = new("^S-1-[0-9]+(-[0-9]+){1,14}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// An Enrollment Status Page tracking state in words, as Microsoft's ESP troubleshooting
    /// describes them: 1 not installed, 2 in progress (not required for device preparation), 3
    /// completed, 4 error.
    /// </summary>
    public static string EspStateText(object value, string category) => (MachineRegistry.AsText(value) ?? "") switch
    {
        "1" => "Not installed",
        "2" => category.StartsWith("DevicePreparation", StringComparison.OrdinalIgnoreCase) ? "Not required" : "In progress",
        "3" => "Completed",
        "4" => "Error",
        _ => "Unknown",
    };

    /// <summary>Every key below a key, as Get-ChildItem -Recurse lists them: a key's subkeys, then each of their trees.</summary>
    private static IEnumerable<string> KeyTree(IRegistry registry, string key)
    {
        IReadOnlyList<string> children;
        try { children = registry.SubKeyNames(key); }
        catch (Exception) { yield break; }
        foreach (var child in children) { yield return key + "\\" + child; }
        foreach (var child in children)
        {
            foreach (var below in KeyTree(registry, key + "\\" + child)) { yield return below; }
        }
    }

    /// <summary>
    /// The Enrollment Status Page's tracking state: every key with an InstallationState value under
    /// EnrollmentStatusTracking for the device and for the requesting user only, the number of
    /// entries per expected-item list, and whether the device and user phases were skipped
    /// (FirstSync of the Intune enrollment). States and names only, no secrets.
    /// </summary>
    public static List<EspRow> EspRows(RunContext run, string root = EspRoot)
    {
        var registry = run.Services.Registry;
        var rows = new List<EspRow>();
        if (!registry.KeyExists(root)) { return rows; }
        var scopes = new List<KeyValuePair<string, string>> { new("Device", root + @"\Device") };
        if (Sid.IsMatch(run.CallerSid ?? "")) { scopes.Add(new("User", root + "\\" + run.CallerSid)); }
        foreach (var scope in scopes)
        {
            if (!registry.KeyExists(scope.Value)) { continue; }
            var prefix = scope.Value + "\\";
            foreach (var key in KeyTree(registry, scope.Value).Take(EspMaxRows))
            {
                var state = registry.GetValue(key, "InstallationState");
                if (state == null) { continue; }
                // Key names relative to the scope key, as "Setup\Apps\Tracking\Sidecar\Win32App_...".
                var parts = key.Substring(prefix.Length).Split('\\');
                var category = string.Join("\\", parts.Take(parts.Length - 1));
                rows.Add(new EspRow
                {
                    Scope = scope.Key,
                    Category = category,
                    Item = parts[parts.Length - 1],
                    State = EspStateText(state, category),
                    Raw = MachineRegistry.AsText(state) ?? "",
                });
            }
        }
        var diagnostics = root + @"\ESPTrackingInfo\Diagnostics";
        if (registry.KeyExists(diagnostics))
        {
            IReadOnlyList<string> lists;
            try { lists = registry.SubKeyNames(diagnostics); }
            catch (Exception) { lists = Array.Empty<string>(); }
            foreach (var name in lists)
            {
                var key = diagnostics + "\\" + name;
                int count;
                try { count = registry.ValueNames(key).Count + registry.SubKeyNames(key).Count; }
                catch (Exception) { count = 0; }
                rows.Add(new EspRow { Scope = "Device", Category = "Expected items", Item = name, State = TextFormat.Count(count, "entry", "entries") });
            }
        }
        var enrollment = Mdm.IntuneEnrollmentId(run);
        if (!string.IsNullOrEmpty(enrollment))
        {
            var firstSync = run.Services.ImePaths.EnrollmentsKey + "\\" + enrollment + @"\FirstSync";
            if (registry.KeyExists(firstSync))
            {
                foreach (var name in new[] { "SkipDeviceStatusPage", "SkipUserStatusPage" })
                {
                    var value = registry.GetValue(firstSync, name);
                    if (value == null) { continue; }
                    var number = Convert.ToInt64(value, Invariant);
                    // 0xffffffff (-1 as a DWORD read here) means the phase is skipped.
                    rows.Add(new EspRow
                    {
                        Scope = "Device",
                        Category = "Settings",
                        Item = name,
                        State = number == -1 || number == 4294967295L ? "Skipped" : "Shown",
                        Raw = MachineRegistry.AsText(value) ?? "",
                    });
                }
            }
        }
        return rows;
    }

    public static Sheet EspSheet(IReadOnlyList<EspRow> rows) => new()
    {
        Name = "Enrollment Status Page",
        TableName = "EnrollmentStatusPage",
        Columns = new[]
        {
            new SheetColumn("Scope", 10, CellKind.Text, F<EspRow>(r => r.Scope)),
            new SheetColumn("Category", 40, CellKind.Text, F<EspRow>(r => r.Category)),
            new SheetColumn("Item", 56, CellKind.Text, F<EspRow>(r => r.Item)),
            new SheetColumn("State", 16, CellKind.Verdict, F<EspRow>(r => r.State)),
            new SheetColumn("Raw value", 12, CellKind.Text, F<EspRow>(r => r.Raw)),
        },
        Rows = (rows ?? Array.Empty<EspRow>()).Where(r => r != null).Cast<object>().ToList(),
        Attention = o => ((EspRow)o).State == "Error" || ((EspRow)o).State == "In progress",
        EmptyText = "No Enrollment Status Page tracking state on this device (it did not go through Autopilot or the ESP).",
    };
}
