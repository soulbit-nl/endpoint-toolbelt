using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.Actions;

/// <summary>
/// The read-only parts the app diagnosis is made of; tests replace them. Every part only
/// reads: the IME's Win32 app state, its status reports, its app log and its retry timers.
/// </summary>
public sealed class DiagnosisParts
{
    public Func<RunContext, AppStateReadout> Read { get; set; } = AppState.Read;
    /// <summary>The names from the IME logs, and from the name cache where the logs no longer have them.</summary>
    public Func<RunContext, IEnumerable<string>, AppNameLookup> Names { get; set; } = (run, ids) => AppState.LookUpNames(run, ids);
    public Func<RunContext, IEnumerable<string>, ImeAttemptRead> Attempts { get; set; } = (run, ids) => ImeLog.Attempts(run, ids);
    public Func<RunContext, Dictionary<string, ImeRetryEntry>> Retries { get; set; } = ImeLog.RetrySchedule;

    /// <summary>The newest detection-script run per app, from the IME's agent log.</summary>
    public Func<RunContext, IEnumerable<string>, Dictionary<string, ImeDetectionRun>> Detections { get; set; } = (run, ids) => ImeLog.DetectionRuns(run, ids);

    /// <summary>The MSI line-of-business apps the MDM stack recorded (EnterpriseDesktopAppManagement).</summary>
    public Func<RunContext, MsiAppReadout> Msi { get; set; } = LobApps.ReadMsi;

    /// <summary>The MSIX line-of-business app installs the MDM stack holds (EnterpriseModernAppManagement, through the MDM WMI Bridge).</summary>
    public Func<RunContext, MsixAppReadout> Msix { get; set; } = MsixApps.Read;

    /// <summary>The IME apps its log shows going through WinGet (Microsoft Store apps).</summary>
    public Func<RunContext, IEnumerable<string>, HashSet<string>> StoreIds { get; set; } = (run, ids) => LobApps.WinGetAppIds(run, ids);

    /// <summary>The IME's required check-ins of the scopes, with their policy lists, for whether a record is still assigned.</summary>
    public Func<RunContext, ISet<string>, ImeAssignmentRead> Assignments { get; set; } = (run, scopes) => ImeAssignments.Read(run, scopes);
}

/// <summary>
/// The read-only app diagnosis: per app of the scopes the caller may see (the device and the
/// caller's own, <see cref="AppState.VisibleScopes"/>), what the IME last recorded, as one
/// <c>app-diagnosis</c> record each, then one <c>apps-diagnosis</c> record that closes the list.
/// It starts no sync, resets nothing, clears no retry timer, restarts no service and collects no
/// support package; it writes no change, no state and no cooldown. The app turns the records into
/// one next step per app; the engine gives the evidence, not the advice.
/// </summary>
public sealed class DiagnoseAppsHandler : IActionHandler
{
    /// <summary>The most app records one diagnosis lists (the rest is counted).</summary>
    public const int MaxApps = 150;

    internal DiagnosisParts Parts { get; set; } = new();

    /// <summary>The verdicts that need no log evidence: done, or nothing for this device to do.</summary>
    private static readonly string[] SettledVerdicts = { "OK", "NO-ACTION" };

    public int? Run(RunContext run)
    {
        run.StepHeader(1, 1, "Read the app state (read-only)");
        var userScope = AppState.EntraObjectId(run.CallerSid).Length > 0;
        AppStateReadout state;
        try
        {
            state = Parts.Read(run);
        }
        catch (Exception ex)
        {
            run.Log("The Win32 app state could not be read (" + ex.Message + ").", LogSeverity.Error);
            run.AddFinding(FindingLevel.Error, "DIAG-APPS-UNREADABLE", "The app state on this device could not be read",
                RunContext.PlainText(ex.Message), "Try again in a few minutes. If it repeats, copy the ticket text for your IT support.",
                "The engine log on this device has the reason.");
            Close(run, present: false, records: 0, listed: 0, scopes: 0, otherScopes: 0, userScope, logEvidence: false);
            return EngineExitCode.Failed;
        }

        if (state.OtherScopes > 0)
        {
            run.Log(string.Format(CultureInfo.InvariantCulture, "{0} of {1} on this device are left out; they are not yours to see.",
                TextFormat.Count(state.OtherRecords, "app record"), TextFormat.Count(state.OtherScopes, "other user")));
        }
        if (!userScope)
        {
            run.Log("Your own apps cannot be matched to this account, so only the apps of the device are read.");
        }

        // The MSI line-of-business apps the MDM stack recorded (not the IME): the device's and the caller's own.
        var msi = Safe(() => Parts.Msi(run), new MsiAppReadout());
        if (msi.OtherScopes > 0)
        {
            run.Log(string.Format(CultureInfo.InvariantCulture, "{0} of {1} are left out; they are not yours to see.",
                TextFormat.Count(msi.OtherRecords, "MSI app record"), TextFormat.Count(msi.OtherScopes, "other user")));
        }
        var msiRows = msi.Rows.OrderBy(r => AppState.ListRank(r.Verdict)).ThenBy(r => r.Scope, StringComparer.Ordinal).ThenBy(r => r.ProductCode, StringComparer.Ordinal).Take(MaxApps).ToList();
        if (msi.Present) { run.Log("MSI line-of-business apps (MDM): " + TextFormat.Count(msiRows.Count, "app record") + "."); }

        // The MSIX line-of-business app installs the MDM stack holds (a node exists only until the install succeeded).
        var msix = Safe(() => Parts.Msix(run), new MsixAppReadout());
        var msixRows = msix.Rows.OrderBy(r => AppState.ListRank(r.Verdict)).ThenBy(r => r.Scope, StringComparer.Ordinal)
            .ThenBy(r => r.PackageFamilyName, StringComparer.OrdinalIgnoreCase).Take(MaxApps).ToList();
        if (msix.Present) { run.Log("MSIX line-of-business app installs (MDM): " + TextFormat.Count(msixRows.Count, "record") + "; other users' are not read."); }
        var lobRows = msiRows.Count + msixRows.Count;
        var lobAttention = msiRows.Count(r => AppState.VerdictOrder.Contains(r.Verdict)) + msixRows.Count(r => AppState.VerdictOrder.Contains(r.Verdict));

        if (!state.Present || state.Rows.Count == 0)
        {
            var resetText = AppState.LastFullResetText(run);
            run.Log("The IME holds no Win32 app state on this device; it has not processed a Win32 app here yet, or the state was cleared." + (resetText.Length > 0 ? " " + resetText : ""));
            if (lobRows == 0)
            {
                run.AddFinding(FindingLevel.Info, "APPS-NO-STATE", "No Win32 app state on this device",
                    resetText.Length > 0 ? resetText : "The IME has not processed a Win32 app here yet, or the state was cleared.");
                Close(run, state.Present, 0, 0, state.Scopes, state.OtherScopes, userScope, logEvidence: false, msiRows.Count, msi.OtherScopes, msixRows.Count);
                return EngineExitCode.Success;
            }
            foreach (var row in msiRows) { WriteMsi(run, row); }
            foreach (var row in msixRows) { WriteMsix(run, row); }
            AddClosingFinding(run, lobAttention, msiRows.Count, msixRows.Count, 0);
            LogCounts(run, 0, 0, msiRows.Count, msixRows.Count, msiRows.Select(r => r.Verdict).Concat(msixRows.Select(r => r.Verdict)));
            Close(run, state.Present, 0, 0, state.Scopes, state.OtherScopes, userScope, logEvidence: false, msiRows.Count, msi.OtherScopes, msixRows.Count);
            return EngineExitCode.Success;
        }

        // Attention first, then offered, then installed or on their way, and not applicable or
        // nothing to do last: when the list is cut, those are the first to go.
        var rows = state.Rows
            .OrderBy(r => AppState.ListRank(r.Verdict))
            .ThenBy(r => r.ScopeRef, StringComparer.Ordinal)
            .ThenBy(r => r.AppId, StringComparer.Ordinal)
            .ToList();
        var listed = rows.Take(MaxApps).ToList();

        // Names from the IME's own policy lines, or from the name cache where the logs rotated;
        // an app without one is shown by its ID.
        var names = Safe(() => Parts.Names(run, listed.Select(r => r.AppId)), new AppNameLookup());
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in listed)
        {
            if (!string.IsNullOrWhiteSpace(row.Name)) { sources[row.AppId] = "log"; continue; }
            if (names.Names.TryGetValue(row.AppId, out var name)) { row.Name = name; sources[row.AppId] = names.Source(row.AppId); }
        }

        // How far the newest attempt got, for the apps that are not settled. An app another user on
        // this device also has gets no log evidence: a log line does not always say whose it is.
        var lookUp = listed.Where(r => !SettledVerdicts.Contains(r.Verdict) && !state.OtherAppIds.Contains(r.AppId)).Select(r => r.AppId).Distinct(StringComparer.Ordinal).ToList();
        var evidence = lookUp.Count == 0 ? new ImeAttemptRead { Reason = "no apps to look up" } : Safe(() => Parts.Attempts(run, lookUp), new ImeAttemptRead { Reason = "the IME log could not be read" });
        run.Log(evidence.Available
            ? string.Format(CultureInfo.InvariantCulture, "IME log: {0} of {1} found.", evidence.Attempts.Count, TextFormat.Count(lookUp.Count, "app"))
            : "IME log: no evidence of the newest attempts (" + evidence.Reason + ").");

        var failed = listed.Where(r => r.Verdict == "RETRY-HELPS").ToList();
        var retries = failed.Count == 0 ? new Dictionary<string, ImeRetryEntry>() : Safe(() => Parts.Retries(run), new Dictionary<string, ImeRetryEntry>());

        // The detection-script runs of the same apps (the same rule: no log evidence for an app another user has).
        var detections = lookUp.Count == 0 ? new Dictionary<string, ImeDetectionRun>() : Safe(() => Parts.Detections(run, lookUp), new Dictionary<string, ImeDetectionRun>());

        // Which IME apps are Microsoft Store apps (WinGet): by WinGet's own error range, or the IME log's WinGet lines.
        var store = Safe(() => Parts.StoreIds(run, listed.Select(r => r.AppId)), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // Whether a record is still assigned: only on proof from the IME's own check-ins, read only
        // when a listed record can qualify. Anything short of proof leaves the record as it reads.
        var assignments = listed.Any(r => ImeAssignments.Qualifies(r.Intent))
            ? Safe(() => Parts.Assignments(run, AppState.VisibleScopes(run)), new ImeAssignmentRead { Reason = "the IME logs could not be read" })
            : null;
        var now = run.Services.Clock.UtcNow;
        var noLongerAssigned = 0;

        foreach (var row in listed)
        {
            evidence.Attempts.TryGetValue(row.AppId, out var attempt);
            retries.TryGetValue(row.AppId, out var retry);
            detections.TryGetValue(row.AppId, out var detection);
            var kind = LobApps.IsWinGetCode(row.ErrorCode) || store.Contains(row.AppId) ? "store" : "win32";
            sources.TryGetValue(row.AppId, out var source);
            var assignment = assignments == null ? null : ImeAssignments.Evaluate(row.ScopeId, row.AppId, row.Intent, row.KeyWrittenUtc, assignments, now);
            if (assignment?.NoLongerAssigned == true) { noLongerAssigned++; }
            Write(run, row, attempt, retry, state.OtherAppIds.Contains(row.AppId), detection, source ?? "", kind, assignment);
        }
        if (assignments != null) { ImeAssignments.Report(run, assignments, noLongerAssigned); }
        foreach (var row in msiRows) { WriteMsi(run, row); }
        foreach (var row in msixRows) { WriteMsix(run, row); }

        var attention = rows.Count(r => AppState.VerdictOrder.Contains(r.Verdict));
        run.Log(string.Format(CultureInfo.InvariantCulture, "Win32 app state now: {0} in {1}, {2} need attention.",
            TextFormat.Count(rows.Count, "app record"), TextFormat.Count(state.Scopes, "scope"), attention));
        if (rows.Count > listed.Count)
        {
            run.Log(string.Format(CultureInfo.InvariantCulture, "{0} are not listed, so the list stays readable; a support package lists every app.", TextFormat.Count(rows.Count - listed.Count, "app record")));
        }
        if (lobRows == 0)
        {
            run.AddFinding(FindingLevel.Info, attention == 0 ? "DIAG-APPS-OK" : "DIAG-APPS-ATTENTION",
                attention == 0 ? "No Win32 app needs attention" : TextFormat.Count(attention, "app") + " need attention",
                string.Format(CultureInfo.InvariantCulture, "{0} read in {1}; nothing on this device was changed.", TextFormat.Count(rows.Count, "app record"), TextFormat.Count(state.Scopes, "scope")));
        }
        else
        {
            AddClosingFinding(run, attention + lobAttention, msiRows.Count, msixRows.Count, rows.Count);
        }
        LogCounts(run, rows.Count, listed.Count, msiRows.Count, msixRows.Count,
            rows.Select(r => r.Verdict).Concat(msiRows.Select(r => r.Verdict)).Concat(msixRows.Select(r => r.Verdict)));
        Close(run, true, rows.Count, listed.Count, state.Scopes, state.OtherScopes, userScope, evidence.Available, msiRows.Count, msi.OtherScopes, msixRows.Count);
        return EngineExitCode.Success;
    }

    /// <summary>"Name: meaning" for a code in the shared table; '' otherwise.</summary>
    internal static string CodeMeaningText(string code)
    {
        var entry = string.IsNullOrEmpty(code) ? null : CodeKnowledge.Get(code);
        return entry == null ? "" : entry.Name + ": " + entry.Meaning;
    }

    /// <summary>The closing finding when MSI (and MSIX) line-of-business apps were read too.</summary>
    private static void AddClosingFinding(RunContext run, int attention, int msiCount, int msixCount, int win32Count)
        => run.AddFinding(FindingLevel.Info, attention == 0 ? "DIAG-APPS-OK" : "DIAG-APPS-ATTENTION",
            attention == 0 ? "No app needs attention" : TextFormat.Count(attention, "app") + " need attention",
            msixCount == 0
                ? string.Format(CultureInfo.InvariantCulture, "{0} and {1} read; nothing on this device was changed.",
                    TextFormat.Count(win32Count, "Intune Management Extension app record"), TextFormat.Count(msiCount, "MSI line-of-business app record"))
                : string.Format(CultureInfo.InvariantCulture, "{0}, {1} and {2} read; nothing on this device was changed.",
                    TextFormat.Count(win32Count, "Intune Management Extension app record"), TextFormat.Count(msiCount, "MSI line-of-business app record"),
                    TextFormat.Count(msixCount, "MSIX line-of-business app record")));

    /// <summary>
    /// One MSIX line-of-business app install as an <c>app-diagnosis</c> record with <c>kind</c> "msix": its
    /// package family name as the app, no intent or revision (the MDM stack keeps neither), the CSP's status in
    /// words, the code with its meaning and step, the installed version when one is, and the time it was read
    /// (the CSP gives the state as it is now). Never the SID; the nodes hold no download address.
    /// </summary>
    private static void WriteMsix(RunContext run, MsixAppRow row)
    {
        var observed = row.ObservedUtc is DateTime at && at.Year >= 2000 ? Iso(at) : "";
        var code = row.Code.Length > 0 && StatusCode.Parse(row.Code) is long value ? MsixApps.Code(value) : null;
        run.WriteRecord("app-diagnosis",
            RunContext.Field("diagnosis", run.RunId),
            RunContext.Field("app", row.PackageFamilyName),
            RunContext.Field("name", ""),
            RunContext.Field("namesource", ""),
            RunContext.Field("scope", row.Scope),
            RunContext.Field("intent", ""),
            RunContext.Field("revision", null),
            RunContext.Field("verdict", row.Verdict),
            RunContext.Field("state", row.State),
            RunContext.Field("enforcement", null),
            RunContext.Field("code", row.Code),
            RunContext.Field("installed", ""),
            RunContext.Field("observed", observed),
            RunContext.Field("phase", ""),
            RunContext.Field("attempt", ""),
            RunContext.Field("attemptutc", ""),
            RunContext.Field("nextretry", ""),
            RunContext.Field("download", row.Download),
            RunContext.Field("shared", false),
            RunContext.Field("kind", "msix"),
            RunContext.Field("codemeaning", code == null || code.Meaning.Length == 0 ? "" : code.Name + ": " + code.Meaning),
            RunContext.Field("codestep", code?.Step ?? ""),
            RunContext.Field("codesupport", code?.Support ?? ""),
            RunContext.Field("codenext", code?.Next ?? ""),
            RunContext.Field("msistatus", null),
            RunContext.Field("version", row.Version ?? ""),
            RunContext.Field("downloadhost", ""),
            RunContext.Field("retryindex", null),
            RunContext.Field("retrycount", null));
        if (!LogsApp(row.Verdict, row.Download)) { return; }
        run.PersistentLog(string.Format(CultureInfo.InvariantCulture, "EndpointToolbelt:APP-DIAGNOSIS MSIX {0} [{1}] kind=msix status={2} verdict={3} code={4}",
            row.PackageFamilyName, row.Scope, row.Status?.ToString(CultureInfo.InvariantCulture) ?? "none", row.Verdict, row.Code.Length > 0 ? row.Code : "none"));
    }

    /// <summary>
    /// Whether the engine log names an app: one that needs attention (as the app decides it: an
    /// attention verdict, or a download problem while it waits) or one offered in Company Portal. The
    /// others are counted in one line (<see cref="LogCounts"/>); every app keeps its full record.
    /// </summary>
    internal static bool LogsApp(string verdict, bool download)
        => AppState.VerdictOrder.Contains(verdict) || verdict == "AVAILABLE" || (download && verdict is "IN-PROGRESS" or "PENDING");

    /// <summary>The verdicts in the counts line, with the field names of the apps-state marker.</summary>
    private static readonly (string Field, string Verdict)[] CountFields =
    {
        ("ok", "OK"), ("inprogress", "IN-PROGRESS"), ("pending", "PENDING"), ("nostate", "NO-STATE"), ("available", "AVAILABLE"), ("noaction", "NO-ACTION"),
        ("packaging", "PACKAGING"), ("restart", "RESTART-FIRST"), ("notapplicable", "NOT-APPLICABLE"), ("dependency", "DEPENDENCY"), ("retry", "RETRY-HELPS"), ("unknown", "UNKNOWN"),
    };

    /// <summary>One engine log line with the number of records per verdict, for the apps the log does not name one by one.</summary>
    private static void LogCounts(RunContext run, int records, int listed, int msi, int msix, IEnumerable<string> verdicts)
    {
        var all = verdicts.ToList();
        var counts = CountFields.Select(f => f.Field + "=" + all.Count(v => v == f.Verdict).ToString(CultureInfo.InvariantCulture));
        var other = all.Count(v => !CountFields.Any(f => f.Verdict == v));
        run.PersistentLog(string.Format(CultureInfo.InvariantCulture, "EndpointToolbelt:APPS-DIAGNOSIS records={0} listed={1} msi={2} msix={3} {4}{5}",
            records, listed, msi, msix, string.Join(" ", counts), other > 0 ? " other=" + other.ToString(CultureInfo.InvariantCulture) : ""));
    }

    /// <summary>
    /// One MSI line-of-business app as an <c>app-diagnosis</c> record with <c>kind</c> "msi": its product
    /// code as the app, no intent or revision (the MDM stack keeps neither), the CSP's status in words, the
    /// code, when its key was last written, and whether it is about the download. Never the SID or the URL.
    /// </summary>
    private static void WriteMsi(RunContext run, MsiAppRow row)
    {
        var observed = row.ObservedUtc is DateTime at && at.Year >= 2000 ? Iso(at) : "";
        run.WriteRecord("app-diagnosis",
            RunContext.Field("diagnosis", run.RunId),
            RunContext.Field("app", row.ProductCode),
            RunContext.Field("name", row.Name ?? ""),
            // The name source; an MSI app's name comes from Windows Installer (its uninstall key), never from a log or the name cache.
            RunContext.Field("namesource", string.IsNullOrWhiteSpace(row.Name) ? "" : "installer"),
            RunContext.Field("scope", row.Scope),
            RunContext.Field("intent", ""),
            RunContext.Field("revision", null),
            RunContext.Field("verdict", row.Verdict),
            RunContext.Field("state", row.State),
            RunContext.Field("enforcement", null),
            RunContext.Field("code", row.Code),
            RunContext.Field("installed", ""),
            RunContext.Field("observed", observed),
            RunContext.Field("phase", ""),
            RunContext.Field("attempt", ""),
            RunContext.Field("attemptutc", ""),
            RunContext.Field("nextretry", ""),
            RunContext.Field("download", row.Download),
            RunContext.Field("shared", false),
            RunContext.Field("kind", "msi"),
            RunContext.Field("codemeaning", CodeMeaningText(row.Code)),
            RunContext.Field("codestep", ""),
            RunContext.Field("codesupport", CodeKnowledge.Get(row.Code)?.Support ?? ""),
            RunContext.Field("codenext", ""),
            RunContext.Field("msistatus", row.Status.HasValue ? (object)row.Status.Value : null),
            RunContext.Field("version", row.Version ?? ""),
            RunContext.Field("downloadhost", row.DownloadHost ?? ""),
            RunContext.Field("retryindex", row.RetryIndex.HasValue ? (object)row.RetryIndex.Value : null),
            RunContext.Field("retrycount", row.RetryCount.HasValue ? (object)row.RetryCount.Value : null));
        if (!LogsApp(row.Verdict, row.Download)) { return; }
        var label = string.IsNullOrWhiteSpace(row.Name) ? "MSI " + row.ProductCode : row.Name + " (MSI " + row.ProductCode.Substring(0, 8) + ")";
        run.PersistentLog(string.Format(CultureInfo.InvariantCulture, "EndpointToolbelt:APP-DIAGNOSIS {0} [{1}] kind=msi status={2} verdict={3} code={4} observed={5}",
            label, row.Scope, row.Status?.ToString(CultureInfo.InvariantCulture) ?? "none", row.Verdict, row.Code.Length > 0 ? row.Code : "none", observed.Length > 0 ? observed : "unknown"));
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read() ?? fallback; }
        catch (Exception) { return fallback; }
    }

    /// <summary>"device" or "user": whose scope the record is in, without the user's ID.</summary>
    internal static string Scope(AppStateRow row) => row.ScopeRef == "Device" ? "device" : "user";

    /// <summary>The intent as a token: required, available, uninstall, dependency, or '' when unknown.</summary>
    internal static string Intent(string intent) => intent switch
    {
        "Required" => "required",
        "Available" => "available",
        "Uninstall" => "uninstall",
        "Dependency (not assigned)" => "dependency",
        _ => "",
    };

    /// <summary>
    /// The IME's LastUpdatedTimeUtc as an ISO 8601 UTC instant, or '' when it is missing or not a
    /// time: a time the engine cannot read is unknown, never "now".
    /// </summary>
    internal static string ObservedUtc(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return ""; }
        if (!DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc) || utc.Year < 2000)
        {
            return "";
        }
        return Iso(utc);
    }

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// The failure is a download or network problem: the IME's "content could not be downloaded"
    /// state, a WinHTTP error code, or a newest attempt that failed or stuck while downloading.
    /// </summary>
    internal static bool DownloadProblem(AppStateRow row, ImeAppAttempt attempt)
    {
        if (row.EnforcementState == 5003) { return true; }
        var code = StatusCode.Parse(row.ErrorCode);
        if (code is long value && ((value >> 16) & 0xFFFF) == 0x8007 && (value & 0xFFFF) is >= 12000 and < 12200) { return true; }
        if (attempt == null) { return false; }
        if ((attempt.Phase == "Download" || attempt.Phase == "Hash") && attempt.Result == "Failed") { return true; }
        return attempt.Phase == "Download" && ImeLog.Stuck(attempt).Length > 0;
    }

    private static void Write(RunContext run, AppStateRow row, ImeAppAttempt attempt, ImeRetryEntry retry, bool shared, ImeDetectionRun detection = null, string nameSource = "", string kind = "win32", AssignmentVerdict assignment = null)
    {
        var observed = ObservedUtc(row.LastUpdatedUtc);
        var download = DownloadProblem(row, attempt);
        var gone = assignment?.NoLongerAssigned == true;
        run.WriteRecord("app-diagnosis",
            RunContext.Field("diagnosis", run.RunId),
            RunContext.Field("app", row.AppId),
            RunContext.Field("name", row.Name ?? ""),
            // "log" (the current IME logs), "cache" (an earlier IME log, through the name cache) or "" without a name.
            RunContext.Field("namesource", string.IsNullOrWhiteSpace(row.Name) ? "" : nameSource),
            RunContext.Field("scope", Scope(row)),
            RunContext.Field("intent", Intent(row.Intent)),
            RunContext.Field("revision", row.Revision),
            RunContext.Field("verdict", row.Verdict),
            RunContext.Field("state", row.State),
            RunContext.Field("enforcement", row.EnforcementState.HasValue ? (object)row.EnforcementState.Value : null),
            RunContext.Field("code", row.ErrorCode),
            RunContext.Field("installed", row.Installed.ToLowerInvariant()),
            RunContext.Field("observed", observed),
            RunContext.Field("phase", attempt?.Phase ?? ""),
            RunContext.Field("attempt", attempt == null ? "" : ImeLog.FormatPhase(attempt)),
            RunContext.Field("attemptutc", attempt == null ? "" : Iso(attempt.LastSeenUtc)),
            RunContext.Field("nextretry", retry == null ? "" : Iso(retry.NextUtc)),
            RunContext.Field("download", download),
            RunContext.Field("shared", shared),
            // An earlier revision's failed attempt (evidence only), the install's success in the
            // newest attempt, and the newest detection-script run (facts only).
            RunContext.Field("earlierrev", row.EarlierRevision >= 0 ? (object)row.EarlierRevision : null),
            RunContext.Field("earliercode", row.EarlierCode ?? ""),
            RunContext.Field("earlierutc", ObservedUtc(row.EarlierUpdatedUtc)),
            RunContext.Field("installokutc", attempt?.InstallDoneUtc is DateTime installed ? Iso(installed) : ""),
            RunContext.Field("detectutc", detection == null ? "" : Iso(detection.Utc)),
            RunContext.Field("detectrev", detection != null && detection.Revision >= 0 ? (object)detection.Revision : null),
            RunContext.Field("detectbits", detection?.Ran32Bit switch { true => "32", false => "64", _ => "" }),
            RunContext.Field("detectexit", detection?.ExitCode is int exit ? exit.ToString(CultureInfo.InvariantCulture) : ""),
            RunContext.Field("detectoutput", detection?.Output switch { true => "yes", false => "no", _ => "" }),
            // "win32", or "store" for a Microsoft Store app the IME installs through WinGet; for a
            // Store app the WinGet code in words from the shared table, and the step it leads to.
            RunContext.Field("kind", kind),
            RunContext.Field("codemeaning", kind == "store" ? CodeMeaningText(row.ErrorCode) : ""),
            RunContext.Field("codestep", kind == "store" ? CodeKnowledge.Get(row.ErrorCode)?.NextStep ?? "" : ""),
            RunContext.Field("codesupport", kind == "store" ? CodeKnowledge.Get(row.ErrorCode)?.Support ?? "" : ""),
            RunContext.Field("codenext", kind == "store" ? LobApps.WinGetNext(row.ErrorCode) : ""),
            // "gone" only when the IME's check-ins prove the app is no longer assigned to this scope
            // (ImeAssignments.Evaluate), with the first and last of those check-ins and how many there were.
            RunContext.Field("assignment", gone ? "gone" : ""),
            RunContext.Field("assignfirst", gone ? Iso(assignment.FirstUtc.Value) : ""),
            RunContext.Field("assignlast", gone ? Iso(assignment.LastUtc.Value) : ""),
            RunContext.Field("assigncount", gone ? (object)assignment.CheckIns : null));
        var label = string.IsNullOrWhiteSpace(row.Name) ? row.AppId : row.Name + " (" + row.AppId.Substring(0, 8) + ")";
        if (gone)
        {
            run.PersistentLog(string.Format(CultureInfo.InvariantCulture, "EndpointToolbelt:APP-ASSIGNMENT {0} [{1}] no longer assigned: not in {2} from {3} to {4}",
                label, Scope(row), TextFormat.Count(assignment.CheckIns, "required check-in"), Iso(assignment.FirstUtc.Value), Iso(assignment.LastUtc.Value)));
        }
        if (!LogsApp(row.Verdict, download)) { return; }
        run.PersistentLog(string.Format(CultureInfo.InvariantCulture, "EndpointToolbelt:APP-DIAGNOSIS {0} [{1}] verdict={2} code={3} observed={4}",
            label, Scope(row), row.Verdict, row.ErrorCode.Length > 0 ? row.ErrorCode : "none", observed.Length > 0 ? observed : "unknown"));
    }

    private static void Close(RunContext run, bool present, int records, int listed, int scopes, int otherScopes, bool userScope, bool logEvidence, int msi = 0, int msiOtherScopes = 0, int msix = 0)
    {
        run.WriteRecord("apps-diagnosis",
            RunContext.Field("diagnosis", run.RunId),
            RunContext.Field("present", present),
            RunContext.Field("records", records),
            RunContext.Field("listed", listed),
            RunContext.Field("scopes", scopes),
            RunContext.Field("otherscopes", otherScopes),
            RunContext.Field("userscope", userScope),
            RunContext.Field("logevidence", logEvidence),
            // The MSI line-of-business app records listed, and other users with MSI apps (counted only).
            RunContext.Field("msi", msi),
            RunContext.Field("msiotherscopes", msiOtherScopes),
            // The MSIX line-of-business app installs listed (other users' are not read).
            RunContext.Field("msix", msix));
    }
}
