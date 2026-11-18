using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.Ime;

/// <summary>How one OMA-DM session ended, from its events.</summary>
public sealed class MdmOutcome
{
    public string Status { get; set; } = "NotVerified";
    public int HttpStatus { get; set; }
    public string HResult { get; set; }
    public string Enrollment { get; set; }
    public string RetryCount { get; set; }
    public bool SessionSeen { get; set; }
    public string Detail { get; set; } = "No end event could be matched to this session.";
    public List<MdmEvent> Evidence { get; } = new();
}

/// <summary>One verified sync attempt.</summary>
public sealed class MdmSyncResult
{
    public string Status { get; set; } = "TriggerFailed";
    public int ExitCode { get; set; } = 1;
    public string EnrollmentId { get; set; }
    public string Route { get; set; } = "none";
    public MdmOutcome Outcome { get; set; }
    public DateTime? RetryAt { get; set; }
    public string RetryCount { get; set; }
    public string Detail { get; set; } = "No route could start a sync.";
}

/// <summary>A retry Windows scheduled after the service refused a session.</summary>
public sealed class MdmRetrySchedule
{
    public int DelayMinutes { get; set; }
    public DateTime RetryAt { get; set; }
    public MdmEvent Event { get; set; }
}

/// <summary>
/// Verified MDM sync. A sync counts only when its OMA-DM session is seen in the MDM event log: a
/// started trigger proves nothing. Events are read by field name (the rendered message is
/// localised), filtered by provider and event ID, and anchored on RecordId.
/// </summary>
public static class Mdm
{
    public const string Provider = "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider";
    public const string SyncLog = "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Sync";
    public const string AdminLog = "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin";

    /// <summary>/Sync: 208 session start, 201 transport result, 209 session end, 287 retry scheduled, 256 the call that opened the session.</summary>
    public static readonly int[] SyncEventIds = { 201, 208, 209, 256, 287 };

    /// <summary>/Admin: only 286, for the delay of a retry Windows scheduled.</summary>
    public static readonly int[] AdminEventIds = { 286 };

    /// <summary>PushRouterOrigin on event 208: the scheduled-task / deviceenroller route this product uses.</summary>
    public const int OriginScheduledTask = 3;

    /// <summary>Windows starts its scheduled retry about a minute after the due time.</summary>
    public const int RetryStartLagSeconds = 120;

    /// <summary>A user can write a fake 286 with a delay of days; a Windows retry is honoured only up to this long.</summary>
    public const int MaxHonouredRetryMinutes = 240;

    private const string TaskFolderRoot = @"\Microsoft\Windows\EnterpriseMgmt\";
    private const string GuidStart = @"^\{?[0-9A-Fa-f]{8}-";
    private const string HexStatus = @"^0[xX][0-9A-Fa-f]{1,8}\z";

    private static readonly Regex EnrollmentKey = new(@"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static bool SameGuid(string a, string b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether an MDM event may count as evidence, from the process that wrote it. An event from a
    /// user session never counts; a writer that has already exited cannot be checked and is accepted.
    /// </summary>
    public static bool IsTrustedWriter(RunContext run, string processId)
    {
        if (!int.TryParse(processId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0) { return true; }
        var session = run.Services.ProcessControl.SessionId(id);
        return session == null || session == 0;
    }

    /// <summary>
    /// The Intune enrollment: exactly one key under HKLM\SOFTWARE\Microsoft\Enrollments with
    /// ProviderID "MS DM Server" and EnrollmentState 1. Never guess between several.
    /// </summary>
    public static string IntuneEnrollmentId(RunContext run)
    {
        var registry = run.Services.Registry;
        var root = run.Services.ImePaths.EnrollmentsKey;
        IReadOnlyList<string> keys;
        try
        {
            keys = registry.SubKeyNames(root).Where(k => EnrollmentKey.IsMatch(k)).ToList();
        }
        catch (Exception ex)
        {
            run.Log("Could not read the enrollment registry: " + ex.Message, LogSeverity.Warning);
            return null;
        }
        var found = new List<string>();
        foreach (var key in keys)
        {
            try
            {
                var provider = MachineRegistry.AsText(registry.GetValue(root + "\\" + key, "ProviderID")) ?? "";
                var stateText = MachineRegistry.AsText(registry.GetValue(root + "\\" + key, "EnrollmentState"));
                var state = stateText == null ? -1 : int.Parse(stateText, NumberStyles.Integer, CultureInfo.InvariantCulture);
                if (string.Equals(provider, "MS DM Server", StringComparison.OrdinalIgnoreCase) && state == 1) { found.Add(key); }
            }
            catch (Exception ex)
            {
                // An unreadable enrollment key is not an active Intune enrollment.
                run.Log("Could not read enrollment " + key + ": " + ex.Message, LogSeverity.Warning);
            }
        }
        if (found.Count == 1) { return found[0]; }
        if (found.Count == 0) { run.Log("No active Intune (MS DM Server) enrollment was found.", LogSeverity.Warning); }
        else { run.Log(found.Count.ToString(CultureInfo.InvariantCulture) + " active Intune enrollments were found; no sync is sent rather than guessing which one.", LogSeverity.Warning); }
        return null;
    }

    /// <summary>
    /// Events of one MDM channel, filtered by provider and event ID, newer than a RecordId, oldest
    /// first. An empty or unreadable channel returns nothing, and that never means success.
    /// </summary>
    public static List<MdmEvent> ReadEvents(RunContext run, string logName, IReadOnlyCollection<int> eventIds, long afterRecordId = 0, int maxEventsToRead = 3000)
    {
        IReadOnlyList<MdmEvent> raw;
        try
        {
            raw = run.Services.MdmEvents.Read(logName, Provider, eventIds, maxEventsToRead);
        }
        catch (Exception ex)
        {
            run.Log("Could not read " + logName + ": " + ex.Message, LogSeverity.Warning);
            return new List<MdmEvent>();
        }
        var output = new List<MdmEvent>();
        var refused = 0;
        foreach (var record in raw)
        {
            if (record.RecordId <= afterRecordId) { continue; }
            if (!IsTrustedWriter(run, record.ProcessId)) { refused++; continue; }
            output.Add(record);
        }
        if (refused > 0)
        {
            // Information, not a warning: user-scoped MDM sessions write these on every sync.
            run.Log(TextFormat.Count(refused, "MDM event") + " in " + logName +
                " came from a user session (user-scoped MDM sessions write there) and are not used as evidence; the device session's events are.");
        }
        return output.OrderBy(e => e.RecordId).ToList();
    }

    /// <summary>Named field first; a value of the expected shape as the fallback for a build that renames the fields.</summary>
    public static string Field(MdmEvent ev, string name, string pattern = null)
    {
        var value = ev.Field(name);
        if (!string.IsNullOrWhiteSpace(value)) { return value; }
        if (pattern != null)
        {
            foreach (var v in ev.Values)
            {
                if (!string.IsNullOrEmpty(v) && Regex.IsMatch(v, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) { return v; }
            }
        }
        return null;
    }

    /// <summary>An enrollment GUID from an event field, without braces.</summary>
    public static string EventGuid(MdmEvent ev, string name) => Field(ev, name, GuidStart)?.Trim('{', '}');

    /// <summary>Session starts (208), optionally for one enrollment and one origin.</summary>
    public static List<MdmEvent> SessionStarts(IEnumerable<MdmEvent> events, string enrollmentId, int requiredOrigin = -1)
    {
        var starts = events.Where(e => e.Id == 208);
        if (!string.IsNullOrEmpty(enrollmentId)) { starts = starts.Where(e => SameGuid(EventGuid(e, "Message1"), enrollmentId)); }
        if (requiredOrigin >= 0)
        {
            starts = starts.Where(e =>
            {
                var code = StatusCode.Parse(Field(e, "HexInt1", @"^0[xX][0-9A-Fa-f]{1,4}\z"));
                return code != null && (int)code.Value == requiredOrigin;
            });
        }
        return starts.OrderBy(e => e.RecordId).ToList();
    }

    /// <summary>
    /// Classifies one session from its 208. The end is the first 209 after it, accepted only when
    /// no other 208 lies between them. HTTP 429 in a 201 inside the session means throttled; a
    /// non-zero end with a 287 means Windows queued its own retry.
    /// </summary>
    public static MdmOutcome SessionOutcome(IReadOnlyList<MdmEvent> events, MdmEvent start)
    {
        var result = new MdmOutcome();
        if (start == null)
        {
            result.Detail = "No session start was found for this enrollment.";
            return result;
        }
        result.SessionSeen = true;
        result.Enrollment = EventGuid(start, "Message1");

        var opener = events.Where(e => e.Id == 256 && e.RecordId < start.RecordId && SameGuid(EventGuid(e, "Message2"), result.Enrollment)).OrderBy(e => e.RecordId).LastOrDefault();
        if (opener != null) { result.Evidence.Add(opener); }
        result.Evidence.Add(start);

        var end = events.Where(e => e.Id == 209 && e.RecordId > start.RecordId).OrderBy(e => e.RecordId).FirstOrDefault();
        if (end == null)
        {
            result.Detail = "The session started but has not ended yet.";
            return result;
        }
        if (events.Any(e => e.Id == 208 && e.RecordId > start.RecordId && e.RecordId < end.RecordId))
        {
            result.Detail = "Another session started before this one ended; the outcome cannot be attributed safely.";
            return result;
        }

        var endCode = StatusCode.Parse(Field(end, "HRESULT", HexStatus));
        var http = 0;
        foreach (var t in events.Where(e => e.Id == 201 && e.RecordId > start.RecordId && e.RecordId < end.RecordId))
        {
            result.Evidence.Add(t);
            var code = StatusCode.Parse(Field(t, "HRESULT", HexStatus));
            if (code != null)
            {
                var status = StatusCode.HttpStatus(code.Value);
                if (status > 0) { http = status; break; }
            }
        }
        result.Evidence.Add(end);

        var retry = events.Where(e => e.Id == 287 && e.RecordId > end.RecordId && (result.Enrollment == null || SameGuid(EventGuid(e, "Message1"), result.Enrollment)))
            .OrderBy(e => e.RecordId).FirstOrDefault();
        if (retry != null)
        {
            result.Evidence.Add(retry);
            result.RetryCount = Field(retry, "UInt1", @"^\d{1,3}\z");
        }

        result.HttpStatus = http;
        if (endCode != null) { result.HResult = "0x" + endCode.Value.ToString("X8", CultureInfo.InvariantCulture); }
        if (endCode == null)
        {
            result.Detail = "The session ended without a readable status; the outcome is unknown.";
            return result;
        }
        if (endCode == 0)
        {
            result.Status = "VerifiedSuccess";
            result.Detail = "The OMA-DM session completed successfully.";
            return result;
        }
        if (http == 429)
        {
            result.Status = "Throttled";
            result.Detail = "The service refused the session with HTTP 429 (too many requests).";
            return result;
        }
        if (retry != null)
        {
            result.Status = "Queued";
            result.Detail = "The session ended with " + result.HResult + " and Windows scheduled its own retry.";
            return result;
        }
        result.Status = "Failed";
        result.Detail = "The OMA-DM session ended with " + result.HResult + ".";
        if (http > 0) { result.Detail += " Transport returned HTTP " + http.ToString(CultureInfo.InvariantCulture) + "."; }
        return result;
    }

    /// <summary>When Windows retries, from the newest event 286 in /Admin for this enrollment.</summary>
    public static MdmRetrySchedule RetrySchedule(RunContext run, string enrollmentId, long afterRecordId = 0)
    {
        var events = ReadEvents(run, AdminLog, AdminEventIds, afterRecordId, 200);
        var match = events.Where(e => SameGuid(EventGuid(e, "Message1"), enrollmentId)).OrderBy(e => e.RecordId).LastOrDefault();
        if (match == null) { return null; }
        var delayText = Field(match, "UInt2", @"^\d{4,9}\z");
        if (!long.TryParse(delayText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delayMs)) { return null; }
        if (delayMs > MaxHonouredRetryMinutes * 60000L)
        {
            run.Log(string.Format(CultureInfo.InvariantCulture,
                "An MDM retry delay of {0} min is longer than any throttling Windows applies ({1} min at most is honoured); it is ignored and a new request is sent.",
                (int)Math.Round(delayMs / 60000.0), MaxHonouredRetryMinutes), LogSeverity.Warning);
            return null;
        }
        return new MdmRetrySchedule
        {
            DelayMinutes = (int)Math.Round(delayMs / 60000.0),
            RetryAt = match.TimeCreatedUtc.AddMilliseconds(delayMs),
            Event = match,
        };
    }

    /// <summary>
    /// A retry Windows queued for this enrollment while the service is still throttling it. Once
    /// any session on this enrollment completed after the retry was scheduled, a new request is sent.
    /// </summary>
    public static (DateTime RetryAt, int MinutesRemaining)? PendingRetry(RunContext run, string enrollmentId, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? run.Services.Clock.UtcNow;
        var schedule = RetrySchedule(run, enrollmentId);
        if (schedule == null) { return null; }
        if ((schedule.RetryAt.AddSeconds(RetryStartLagSeconds) - now).TotalMinutes <= 0) { return null; }
        try
        {
            var syncEvents = ReadEvents(run, SyncLog, SyncEventIds, 0, 400);
            foreach (var start in SessionStarts(syncEvents, enrollmentId).Where(s => s.TimeCreatedUtc > schedule.Event.TimeCreatedUtc))
            {
                if (SessionOutcome(syncEvents, start).Status == "VerifiedSuccess")
                {
                    run.Log("Windows scheduled a retry for " + RunContext.UserTime(schedule.RetryAt) + ", but a session on this enrollment completed at " +
                        RunContext.UserTime(start.TimeCreatedUtc, "HH:mm:ss") + ", so the service is answering again. A new request is sent.");
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            run.Log("Could not check for a completed session after the scheduled retry (" + ex.Message + "); the retry is honoured.", LogSeverity.Warning);
        }
        return (schedule.RetryAt, (int)Math.Max(0, Math.Ceiling((schedule.RetryAt - now).TotalMinutes)));
    }

    private static string Utc(DateTime when) => when.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>For anything that is not a clean success: the events behind the conclusion, in the engine log only.</summary>
    public static void WriteEvidence(RunContext run, IReadOnlyList<MdmEvent> evidence)
    {
        if (evidence == null || evidence.Count == 0)
        {
            run.PersistentLog("MDM sync evidence: none; no session events were matched.");
            return;
        }
        foreach (var e in evidence)
        {
            run.PersistentLog(string.Format(CultureInfo.InvariantCulture, "MDM sync evidence: {0} id={1} rec={2} pid={3} data=[{4}]",
                Utc(e.TimeCreatedUtc), e.Id, e.RecordId, e.ProcessId, string.Join(" / ", e.Values)));
        }
    }

    /// <summary>One record and one grep-able log line per sync, on every exit path.</summary>
    public static void WriteResult(RunContext run, MdmSyncResult result)
    {
        var http = result.Outcome?.HttpStatus ?? 0;
        var hresult = result.Outcome?.HResult ?? "";
        var retryAt = result.RetryAt == null ? "" : Utc(result.RetryAt.Value);
        run.WriteRecord("mdm-result",
            RunContext.Field("status", result.Status ?? ""),
            RunContext.Field("exit", result.ExitCode),
            RunContext.Field("route", result.Route ?? ""),
            RunContext.Field("http", http),
            RunContext.Field("hresult", hresult),
            RunContext.Field("retryCount", result.RetryCount ?? ""),
            RunContext.Field("retryAt", retryAt));
        run.PersistentLog(string.Format(CultureInfo.InvariantCulture, "EndpointToolbelt:MDM-RESULT status={0} exit={1} enrollment={2} route={3} http={4} hresult={5} retrycount={6} retryat={7}",
            result.Status, result.ExitCode, result.EnrollmentId, result.Route, http, hresult, result.RetryCount, retryAt));
    }

    public static void WriteQueued(RunContext run, DateTime? retryAt)
    {
        var at = retryAt == null ? "" : Utc(retryAt.Value);
        run.WriteRecord("mdm-queued", RunContext.Field("at", at));
        run.PersistentLog("EndpointToolbelt:MDM-QUEUED " + at);
    }

    /// <summary>
    /// One verified sync attempt. Routes: the enrollment's demand-startable task whose action runs
    /// "deviceenroller.exe /o &lt;id&gt; /c /b", then deviceenroller.exe directly. The next route is
    /// tried only when the trigger did not fire or no session appeared.
    /// </summary>
    public static MdmSyncResult VerifiedSync(RunContext run, int waitSeconds = 90)
    {
        var result = new MdmSyncResult();
        var enrollment = IntuneEnrollmentId(run);
        if (enrollment == null)
        {
            result.Status = "PreflightFailed";
            result.Detail = "No single Intune enrollment could be identified; nothing was triggered.";
            WriteResult(run, result);
            return result;
        }
        result.EnrollmentId = enrollment;

        var pending = PendingRetry(run, enrollment);
        if (pending != null)
        {
            result.Status = "RetryPending";
            result.ExitCode = 2;
            result.Route = "skipped";
            result.RetryAt = pending.Value.RetryAt;
            var when = pending.Value.MinutesRemaining > 0 ? TextFormat.Count(pending.Value.MinutesRemaining, "minute") + " from now" : "due now";
            result.Detail = "Intune refused the last sync and Windows has a retry queued for " + RunContext.UserTime(pending.Value.RetryAt) + " (" + when +
                "). No new request was sent: while the service is throttling this device, another request would only raise the retry count.";
            run.Log("MDM sync: " + result.Detail);
            WriteQueued(run, pending.Value.RetryAt);
            WriteResult(run, result);
            return result;
        }

        // Two anchors: the newest RecordId before the trigger, and the time.
        var clock = run.Services.Clock;
        var anchorTime = clock.UtcNow.AddSeconds(-5);
        var seed = ReadEvents(run, SyncLog, SyncEventIds, 0, 50);
        var syncMarker = seed.Count > 0 ? seed[seed.Count - 1].RecordId : 0;
        var adminSeed = ReadEvents(run, AdminLog, AdminEventIds, 0, 50);
        var adminMarker = adminSeed.Count > 0 ? adminSeed[adminSeed.Count - 1].RecordId : 0;

        // The enrollment matched a strict GUID pattern, so it cannot inject anything into the path.
        SyncTask task = null;
        try
        {
            task = run.Services.TaskScheduler.FindSyncTask(TaskFolderRoot + enrollment + "\\");
        }
        catch (Exception ex)
        {
            run.Log("Could not list the enrollment tasks: " + ex.Message, LogSeverity.Warning);
        }

        var exe = run.Services.ImePaths.DeviceEnroller;
        var routes = new List<(string Name, SyncTask Task)>();
        if (task != null) { routes.Add(("enrollment task", task)); }
        else { run.Log("No enabled, demand-startable sync task on the Intune enrollment; using deviceenroller directly."); }
        if (File.Exists(exe)) { routes.Add(("deviceenroller", null)); }
        if (routes.Count == 0)
        {
            result.Detail = "Neither a usable task nor deviceenroller.exe is present on this device.";
            run.Log("MDM sync: " + result.Detail);
            WriteResult(run, result);
            return result;
        }

        var anyFired = false;
        foreach (var route in routes)
        {
            var fired = false;
            if (route.Task != null)
            {
                try
                {
                    run.Services.TaskScheduler.Start(route.Task);
                    fired = true;
                    run.Log("MDM sync requested through the enrollment task (" + route.Task.Name + ").");
                }
                catch (Exception ex)
                {
                    run.Log("Could not start the enrollment task: " + ex.Message, LogSeverity.Warning);
                }
            }
            else
            {
                try
                {
                    var exit = run.Services.RunAndWait(exe, "/o \"" + enrollment + "\" /c /b", 60000);
                    if (exit == null)
                    {
                        // It may well have opened a session already, so the session check still runs.
                        fired = true;
                        run.Log("deviceenroller did not finish within 60 seconds; checking for a session anyway.", LogSeverity.Warning);
                    }
                    else if (exit == 0)
                    {
                        fired = true;
                        run.Log("MDM sync requested through deviceenroller.");
                    }
                    else
                    {
                        run.Log("deviceenroller returned " + exit.Value.ToString(CultureInfo.InvariantCulture) + ".", LogSeverity.Warning);
                    }
                }
                catch (Exception ex)
                {
                    run.Log("Could not run deviceenroller: " + ex.Message, LogSeverity.Warning);
                }
            }
            if (!fired) { continue; }
            anyFired = true;
            result.Route = route.Name;

            var deadline = clock.Elapsed + TimeSpan.FromSeconds(waitSeconds);
            MdmOutcome outcome = null;
            while (clock.Elapsed < deadline)
            {
                clock.Sleep(TimeSpan.FromSeconds(2));
                var fresh = ReadEvents(run, SyncLog, SyncEventIds, syncMarker, 200).Where(e => e.TimeCreatedUtc >= anchorTime).ToList();
                var starts = SessionStarts(fresh, enrollment, OriginScheduledTask);
                if (starts.Count == 0) { continue; }
                if (starts.Count > 1)
                {
                    result.Status = "Ambiguous";
                    result.ExitCode = 2;
                    result.Route = route.Name;
                    result.Detail = starts.Count.ToString(CultureInfo.InvariantCulture) + " sessions for this enrollment started in the same window; the outcome cannot be attributed to this run.";
                    run.Log("MDM sync: " + result.Detail);
                    WriteResult(run, result);
                    return result;
                }
                outcome = SessionOutcome(fresh, starts[0]);
                if (outcome.Status != "NotVerified") { break; }
            }

            if (outcome != null && outcome.SessionSeen)
            {
                // The service answered; whatever it said, no further route is tried.
                result.Route = route.Name;
                result.Outcome = outcome;
                result.Status = outcome.Status;
                result.RetryCount = outcome.RetryCount;
                switch (outcome.Status)
                {
                    case "VerifiedSuccess":
                        result.ExitCode = 0;
                        result.Detail = outcome.Detail;
                        break;
                    case "Throttled":
                        result.ExitCode = 2;
                        var schedule = RetrySchedule(run, enrollment, adminMarker);
                        if (schedule != null)
                        {
                            result.RetryAt = schedule.RetryAt;
                            result.Detail = string.Format(CultureInfo.InvariantCulture, "The service is rate limiting this device (HTTP 429). Windows retries by itself at about {0} ({1} minutes).",
                                RunContext.UserTime(schedule.RetryAt), schedule.DelayMinutes);
                        }
                        else
                        {
                            result.Detail = "The service is rate limiting this device (HTTP 429). Windows has scheduled its own retry.";
                        }
                        break;
                    case "Queued":
                        result.ExitCode = 2;
                        result.Detail = outcome.Detail;
                        break;
                    case "Failed":
                        result.ExitCode = 1;
                        result.Detail = outcome.Detail;
                        break;
                    default:
                        result.ExitCode = 2;
                        result.Detail = outcome.Detail;
                        break;
                }
                if (result.Status == "Throttled") { WriteQueued(run, result.RetryAt); }
                if (result.Status != "VerifiedSuccess") { WriteEvidence(run, outcome.Evidence); }
                WriteResult(run, result);
                return result;
            }
            run.Log("MDM sync: the " + route.Name + " route fired but no session appeared within " + waitSeconds.ToString(CultureInfo.InvariantCulture) + " s.");
        }

        if (anyFired)
        {
            result.Status = "NotVerified";
            result.ExitCode = 2;
            result.Detail = "The sync was requested but no OMA-DM session appeared. This is not a confirmed failure; the sync may still complete.";
        }
        else
        {
            // Nothing reached Windows, so nothing can still complete: a failure, not "not verified".
            result.Status = "TriggerFailed";
            result.ExitCode = 1;
            result.Detail = "Every route was attempted, but none of them could request the sync. The reason for each is in the log.";
        }
        WriteResult(run, result);
        return result;
    }

    /// <summary>How long a verified session took and how many /Admin events it wrote; null when unknown.</summary>
    public static (double Seconds, int Events)? SessionWork(RunContext run, MdmOutcome outcome)
    {
        if (outcome == null || outcome.Evidence.Count == 0) { return null; }
        var start = outcome.Evidence.FirstOrDefault(e => e.Id == 208);
        var end = outcome.Evidence.LastOrDefault(e => e.Id == 209);
        if (start == null || end == null) { return null; }
        int count;
        try
        {
            count = run.Services.MdmEvents.Count(AdminLog, Provider, start.TimeCreatedUtc, end.TimeCreatedUtc.AddSeconds(1));
        }
        catch (Exception)
        {
            return null;
        }
        return ((end.TimeCreatedUtc - start.TimeCreatedUtc).TotalSeconds, count);
    }

    /// <summary>The sync status in words for progress lines; the raw status stays in the mdm-result record.</summary>
    public static string FormatStatus(string status) => status switch
    {
        "VerifiedSuccess" => "verified",
        "Throttled" => "rate limited by Intune",
        "Queued" => "queued by Windows",
        "RetryPending" => "already queued by Windows",
        "Failed" => "failed",
        "Ambiguous" => "could not be verified",
        "NotVerified" => "not verified",
        "TriggerFailed" => "could not be started",
        "PreflightFailed" => "not sent (no single Intune enrollment)",
        _ => "unknown",
    };

    /// <summary>The sync result as one finding, and the request as a change when it reached the service.</summary>
    public static void AddFinding(RunContext run, MdmSyncResult sync, Func<RunContext, MdmOutcome, (double Seconds, int Events)?> sessionWork = null)
    {
        var route = sync.Route ?? "";
        if (route.Length > 0 && route != "none" && route != "skipped") { run.AddChange("Requested an Intune sync (" + route + ")"); }
        var retryText = sync.RetryAt == null ? "" : " Windows retries by itself at about " + RunContext.UserTime(sync.RetryAt.Value) + ".";
        // What the session's end code means, when the shared code table knows it.
        var codeText = sync.Outcome != null && !string.IsNullOrEmpty(sync.Outcome.HResult) ? CodeKnowledge.Format(sync.Outcome.HResult) : "";
        var detail = sync.Detail ?? "";
        switch (sync.Status)
        {
            case "VerifiedSuccess":
            {
                var work = (sessionWork ?? SessionWork)(run, sync.Outcome);
                if (work == null)
                {
                    run.AddFinding(FindingLevel.Ok, "MDM-SYNC-OK", "Intune sync completed",
                        "The session was confirmed in the MDM event log, not just requested. Whether it brought anything new could not be read.");
                }
                else if (work.Value.Events == 0)
                {
                    run.AddFinding(FindingLevel.Ok, "MDM-SYNC-OK", "Intune sync completed; nothing new for this device",
                        "The device checked in and Intune answered in " + TextFormat.Number(work.Value.Seconds, "0") + " s with nothing to apply. What was already applied stays as it is.",
                        "If you expect a new app or setting, it has not been sent to this device yet; requesting again gives the same answer. Contact your IT support.",
                        "Intune had nothing new for this device: check the assignment of the expected app or policy (group membership, filters, user or device targeting).");
                }
                else
                {
                    run.AddFinding(FindingLevel.Ok, "MDM-SYNC-OK", "Intune sync completed",
                        "The session took " + TextFormat.Number(work.Value.Seconds, "0") + " s and " + TextFormat.Count(work.Value.Events, "management event was", "management events were") + " logged while it ran.");
                }
                break;
            }
            case "Throttled":
                run.AddFinding(FindingLevel.Info, "MDM-SYNC-THROTTLED", "Intune is rate limiting this device; the sync is queued",
                    "The service answered HTTP 429 (too many requests)." + retryText,
                    "No action needed. Requesting again only raises the retry count and the wait.");
                break;
            case "RetryPending":
                run.AddFinding(FindingLevel.Info, "MDM-SYNC-QUEUED", "A sync is already queued by Windows; no new one was sent",
                    detail, "No action needed. Another request now would only raise the retry count.");
                break;
            case "Queued":
                run.AddFinding(FindingLevel.Info, "MDM-SYNC-QUEUED", "The sync did not complete; Windows has scheduled its own retry",
                    detail + codeText + retryText,
                    "No action needed now. If the last MDM sync stays old for more than a day, create a support package.");
                break;
            case "Failed":
                run.AddFinding(FindingLevel.Error, "MDM-SYNC-FAILED", "The Intune sync failed",
                    detail + codeText,
                    "Run Check Microsoft connections (on I can't connect). If Intune is reachable and this repeats, create a support package: its log shows what the service answered.");
                break;
            case "Ambiguous":
                run.AddFinding(FindingLevel.Warning, "MDM-SYNC-AMBIGUOUS", "A sync ran, but more than one started at the same moment",
                    detail, "Check the last MDM sync in the device health view in a few minutes.");
                break;
            case "NotVerified":
                // A route fired; when none could, the status is TriggerFailed.
                run.AddFinding(FindingLevel.Warning, "MDM-SYNC-UNVERIFIED", "A sync was requested, but no session could be confirmed",
                    detail, "It may still complete. Check the last MDM sync in a few minutes; if it does not change, run Check Microsoft connections.");
                break;
            case "TriggerFailed":
                run.AddFinding(FindingLevel.Error, "MDM-SYNC-NOT-STARTED", "No sync could be started",
                    detail, "Restart the device and try again. If it repeats, create a support package for your IT support.",
                    "The enrollment's sync task is disabled or cannot be started, or deviceenroller.exe is missing; check the enrollment and re-enroll if needed.");
                break;
            case "PreflightFailed":
                run.AddFinding(FindingLevel.Error, "MDM-NO-ENROLLMENT", "No single Intune enrollment found on this device",
                    detail, "Contact your IT support.",
                    @"The device is not enrolled in Intune or has more than one active MDM enrollment. Check the device in Intune and the enrollments under HKLM\SOFTWARE\Microsoft\Enrollments; re-enroll it if needed.");
                break;
            default:
                run.AddFinding(FindingLevel.Warning, "MDM-SYNC-UNKNOWN", "The sync result could not be interpreted",
                    "Status: " + sync.Status + ". " + detail, "Create a support package and contact your IT support.",
                    "Report this as a defect of " + ProductInfo.DisplayName + ", with the support package.");
                break;
        }
    }
}
