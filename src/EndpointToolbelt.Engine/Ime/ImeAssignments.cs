using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.Ime;

/// <summary>
/// One check-in for the required apps of one scope, as the IME's log shows it: when its policy list
/// arrived, whether it is complete, and every app ID the list names in any role.
/// </summary>
public sealed class RequiredAppsCheckIn
{
    /// <summary>The scope (lower case): a user's object ID, or <see cref="AppState.DeviceScopeId"/> for the device.</summary>
    public string Scope { get; set; } = "";

    /// <summary>When the policy list was written (UTC); for a check-in without a list, when it started.</summary>
    public DateTime Utc { get; set; }

    /// <summary>The list arrived whole, holds at least one policy, and no failure was logged while the check-in ran.</summary>
    public bool Complete { get; set; }

    /// <summary>Why it is not complete; empty when it is.</summary>
    public string Problem { get; set; } = "";

    /// <summary>The policies in the list.</summary>
    public int Policies { get; set; }

    /// <summary>
    /// Every app ID (lower case) the list names anywhere: an assigned app, a dependency or a superseded
    /// app. Taken from the whole text, so an ID in a field this product does not know still counts.
    /// </summary>
    public HashSet<string> Mentioned { get; } = new(StringComparer.Ordinal);
}

/// <summary>The check-ins read from the IME's logs, or why they could not be read.</summary>
public sealed class ImeAssignmentRead
{
    /// <summary>The pattern table had the rules and every log could be read in time.</summary>
    public bool Available { get; set; }

    public string Reason { get; set; } = "";
    public List<RequiredAppsCheckIn> CheckIns { get; } = new();
}

/// <summary>Whether one app record is no longer assigned, and the check-ins that prove it.</summary>
public sealed class AssignmentVerdict
{
    public bool NoLongerAssigned { get; set; }

    /// <summary>Why the record could not be called no longer assigned (for the engine log); empty when it is.</summary>
    public string Reason { get; set; } = "";

    /// <summary>The oldest and the newest check-in that did not list the app.</summary>
    public DateTime? FirstUtc { get; set; }
    public DateTime? LastUtc { get; set; }

    /// <summary>How many check-ins, from the first to the last, did not list the app.</summary>
    public int CheckIns { get; set; }

    internal static AssignmentVerdict Unknown(string reason) => new() { Reason = reason };
}

/// <summary>
/// Whether an IME Win32 app record is no longer assigned. Intune does not document when the IME
/// removes the record of an app that is no longer assigned, and no registry value says so; the only
/// device-side evidence is the list of policies the IME receives when it checks in for the required
/// apps. A record is called no longer assigned only on proof, never on the absence of evidence:
/// every required check-in of its scope after the record's last change is complete and leaves the
/// app out in every role, they span at least <see cref="MinSpan"/>, and the newest is at most
/// <see cref="MaxAge"/> old. Only a record assigned as required, or a dependency, can qualify: an
/// available app is listed only when someone chooses Install in Company Portal, and whether a required
/// check-in lists an uninstall assignment is not known. Read-only: nothing is changed or removed.
/// </summary>
public static class ImeAssignments
{
    /// <summary>The check-ins that leave the app out span at least this long (the IME's own check-in cycle).</summary>
    public static readonly TimeSpan MinSpan = TimeSpan.FromHours(8);

    /// <summary>The newest check-in is at most this old; an older one may miss a newer assignment.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(8);

    /// <summary>The pattern kinds the check-in reading needs; without all three it reads nothing.</summary>
    private static readonly string[] RequiredRuleKinds = { "policies", "session", "required" };

    /// <summary>The days of log read.</summary>
    public const int Days = 2;

    private const string ListMarker = "Get policies = ";

    private static readonly Regex Guid = new(ImeLog.GuidPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex WholeGuid = new("^" + ImeLog.GuidPattern + "\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The intents whose record can be called no longer assigned (as <see cref="AppStateRow.Intent"/> says them).</summary>
    public static bool Qualifies(string intent) => intent is "Required" or "Dependency (not assigned)";

    /// <summary>
    /// The required check-ins of <paramref name="scopes"/> in the IME's logs of the last
    /// <see cref="Days"/> days, read with the same bounded reader as every IME log. Never throws.
    /// </summary>
    public static ImeAssignmentRead Read(RunContext run, ISet<string> scopes, int maxSeconds = 15)
    {
        var result = new ImeAssignmentRead();
        try
        {
            var started = run.Services.Clock.Elapsed;
            TimeSpan Elapsed() => run.Services.Clock.Elapsed - started;
            ImeAppRules table;
            try { table = ImeLog.Rules(run.Services.ImePaths.PatternFile); }
            catch (Exception) { result.Reason = "the pattern table could not be read"; return result; }
            if (table.AssignmentFiles.Count == 0 || !RequiredRuleKinds.All(k => table.AssignmentRules.Any(r => r.Kind == k)))
            {
                result.Reason = "the pattern table has no rules for the IME's check-ins";
                return result;
            }
            var notBefore = run.Services.Clock.UtcNow.AddDays(-Days);
            var records = new List<CmTraceRecord>();
            foreach (var filter in table.AssignmentFiles)
            {
                var read = CmTraceReader.Read(run.Services.ImePaths.LogsFolder, filter, Elapsed, 3, 4L * 1024 * 1024, maxSeconds, notBefore);
                // A log that could not be read, or a read cut short, may hide the check-in that lists the app.
                if (read.Unreadable > 0) { result.Reason = "an IME log could not be read"; return result; }
                if (read.TimedOut) { result.Reason = "the time limit was reached"; return result; }
                records.AddRange(read.Records);
            }
            result.CheckIns.AddRange(CheckIns(records.OrderBy(r => r.Utc), table.AssignmentRules, FailureClaimRules(table), scopes));
            result.Available = true;
        }
        catch (Exception ex)
        {
            result.Reason = "the IME logs could not be read (" + ex.GetType().Name + ")";
            result.Available = false;
            result.CheckIns.Clear();
        }
        return result;
    }

    /// <summary>
    /// The check-in rules that decide whether a record is a failure, in table order: the failure
    /// rules and the ignored ones, so a record an ignored rule claims first is no failure. Not only
    /// failure rules: <see cref="IsFailure"/> reads the kind of the first match.
    /// </summary>
    internal static List<ImeCheckInRule> FailureClaimRules(ImeAppRules table)
        => table.CheckInRules.Where(r => KindIs(r, "failure") || KindIs(r, "ignored")).ToList();

    /// <summary>A record is a failure when the first of <paramref name="claimRules"/> it matches is a failure rule.</summary>
    internal static bool IsFailure(IReadOnlyList<ImeCheckInRule> claimRules, string message)
        => FirstMatch(claimRules, message) is { } claimed && KindIs(claimed.Rule, "failure");

    // The kinds compare as the service's evaluation compares them (ImeCheckInEvidence): ignoring case.
    private static bool KindIs(ImeCheckInRule rule, string kind) => string.Equals(rule.Kind, kind, StringComparison.OrdinalIgnoreCase);

    private sealed class Pending
    {
        public string Scope;
        public bool Required;
        public DateTime StartUtc;
        public bool Failed;
    }

    /// <summary>
    /// The required check-ins of the records, oldest first (separate so it can be tested without
    /// files). A session line names the user ID its thread works for; a required line opens a check-in
    /// on that thread, and its policy list closes it. A failure logged while it ran, a list that is cut
    /// off, empty or not JSON, or a check-in that ends without a list is not complete. An available-only
    /// check-in, a thread whose user is not known and a scope outside <paramref name="scopes"/> give none.
    /// <paramref name="failureClaimRules"/> are <see cref="FailureClaimRules"/>: a record counts as a
    /// failure only when its first match there is a failure rule.
    /// </summary>
    internal static List<RequiredAppsCheckIn> CheckIns(IEnumerable<CmTraceRecord> records, IReadOnlyList<ImeCheckInRule> rules, IReadOnlyList<ImeCheckInRule> failureClaimRules, ISet<string> scopes)
    {
        var result = new List<RequiredAppsCheckIn>();
        var threads = new Dictionary<string, Pending>(StringComparer.Ordinal);
        bool Wanted(Pending p) => p.Scope != null && scopes.Contains(p.Scope);
        void Unfinished(Pending p)
        {
            if (p.Required && Wanted(p)) { result.Add(new RequiredAppsCheckIn { Scope = p.Scope, Utc = p.StartUtc, Problem = "the check-in ended without a policy list" }); }
            p.Required = false;
            p.Failed = false;
        }
        foreach (var record in records)
        {
            var key = record.Thread.ToString(CultureInfo.InvariantCulture) + "|" + record.Source;
            var message = record.Message ?? "";
            var rule = FirstMatch(rules, message);
            if (rule == null)
            {
                // A failure while a check-in runs (on any thread: the location service writes on its own) spoils it.
                if (IsFailure(failureClaimRules, message))
                {
                    foreach (var open in threads.Values.Where(p => p.Required && p.StartUtc <= record.Utc)) { open.Failed = true; }
                }
                continue;
            }
            threads.TryGetValue(key, out var pending);
            switch (rule.Value.Rule.Kind)
            {
                case "session":
                    if (pending != null) { Unfinished(pending); }
                    var user = rule.Value.Match.Groups["user"].Value.ToLowerInvariant();
                    threads[key] = new Pending { Scope = user.Length > 0 ? user : null };
                    break;
                case "required":
                    if (pending == null) { threads[key] = pending = new Pending(); }
                    else { Unfinished(pending); }
                    pending.Required = true;
                    pending.StartUtc = record.Utc;
                    break;
                case "available":
                    if (pending != null) { Unfinished(pending); }
                    break;
                case "policies":
                    if (pending == null || !pending.Required) { break; }
                    if (Wanted(pending))
                    {
                        var checkIn = List(message);
                        checkIn.Scope = pending.Scope;
                        checkIn.Utc = record.Utc;
                        if (checkIn.Complete && (pending.Failed || record.Type == 3))
                        {
                            checkIn.Complete = false;
                            checkIn.Problem = "a failure was logged while the check-in ran";
                        }
                        result.Add(checkIn);
                    }
                    pending.Required = false;
                    pending.Failed = false;
                    break;
            }
        }
        // A check-in still open at the end of the log has no list (yet): it is not complete.
        foreach (var open in threads.Values) { Unfinished(open); }
        return result.OrderBy(c => c.Utc).ToList();
    }

    private static (ImeCheckInRule Rule, Match Match)? FirstMatch(IReadOnlyList<ImeCheckInRule> rules, string message)
    {
        foreach (var rule in rules)
        {
            Match m;
            try { m = rule.Regex.Match(message); }
            catch (RegexMatchTimeoutException) { continue; }
            if (m.Success) { return (rule, m); }
        }
        return null;
    }

    /// <summary>
    /// The policy list of one "Get policies = [...]" message: complete only when the text after the
    /// marker is one whole JSON array of at least one policy, each with an app ID. Every app ID the
    /// text holds anywhere is kept as named.
    /// </summary>
    internal static RequiredAppsCheckIn List(string message)
    {
        var checkIn = new RequiredAppsCheckIn();
        var at = (message ?? "").IndexOf(ListMarker, StringComparison.Ordinal);
        if (at < 0) { checkIn.Problem = "no policy list"; return checkIn; }
        var text = message.Substring(at + ListMarker.Length).Trim();
        if (!text.StartsWith("[", StringComparison.Ordinal) || !text.EndsWith("]", StringComparison.Ordinal))
        {
            checkIn.Problem = "the policy list is cut off";
            return checkIn;
        }
        JsonNode list;
        try { list = JsonNode.Parse(text); }
        catch (Exception) { checkIn.Problem = "the policy list is not valid JSON"; return checkIn; }
        if (list.Kind != JsonNode.NodeKind.Array) { checkIn.Problem = "the policy list is not a list"; return checkIn; }
        var policies = list.Items.ToList();
        checkIn.Policies = policies.Count;
        if (policies.Count == 0) { checkIn.Problem = "the policy list is empty"; return checkIn; }
        if (policies.Any(p => p.Kind != JsonNode.NodeKind.Map || !WholeGuid.IsMatch(p.Get("Id")?.AsText() ?? "")))
        {
            checkIn.Problem = "a policy in the list has no app ID";
            return checkIn;
        }
        try
        {
            for (var m = Guid.Match(text); m.Success; m = m.NextMatch()) { checkIn.Mentioned.Add(m.Value.ToLowerInvariant()); }
        }
        catch (RegexMatchTimeoutException)
        {
            checkIn.Mentioned.Clear();
            checkIn.Problem = "the policy list could not be read in time";
            return checkIn;
        }
        checkIn.Complete = true;
        return checkIn;
    }

    /// <summary>
    /// Whether the record of <paramref name="appId"/> in <paramref name="scopeId"/> is no longer
    /// assigned. It is only when all of these hold; anything else is unknown, and the record keeps the
    /// verdict it has. (A) every required check-in of its scope after the record's last change
    /// (<paramref name="recordWrittenUtc"/>) is complete; (B) there is at least one; (C) none of them
    /// names the app in any role; (D) they span at least <see cref="MinSpan"/> and the newest is at most
    /// <see cref="MaxAge"/> old. The intent must be Required or a dependency (<see cref="Qualifies"/>).
    /// </summary>
    public static AssignmentVerdict Evaluate(string scopeId, string appId, string intent, DateTime? recordWrittenUtc, ImeAssignmentRead read, DateTime nowUtc)
    {
        if (read == null || !read.Available) { return AssignmentVerdict.Unknown("the IME's check-ins were not read" + (read != null && read.Reason.Length > 0 ? " (" + read.Reason + ")" : "")); }
        if (!Qualifies(intent)) { return AssignmentVerdict.Unknown("only an app assigned as required, or a dependency, can be checked"); }
        if (recordWrittenUtc is not DateTime written) { return AssignmentVerdict.Unknown("the record's last change is not known"); }
        var scope = (scopeId ?? "").ToLowerInvariant();
        var id = (appId ?? "").ToLowerInvariant();
        var window = read.CheckIns.Where(c => c.Scope == scope && c.Utc > written).OrderBy(c => c.Utc).ToList();
        if (window.Count == 0) { return AssignmentVerdict.Unknown("no required check-in of its scope after the record's last change"); }
        var incomplete = window.FirstOrDefault(c => !c.Complete);
        if (incomplete != null) { return AssignmentVerdict.Unknown("a check-in after the record's last change is not complete: " + incomplete.Problem); }
        if (window.Any(c => c.Mentioned.Contains(id))) { return AssignmentVerdict.Unknown("a check-in after the record's last change lists the app"); }
        var first = window[0].Utc;
        var last = window[window.Count - 1].Utc;
        if (last - first < MinSpan) { return AssignmentVerdict.Unknown("the check-ins without the app span less than 8 hours"); }
        if (nowUtc - last > MaxAge) { return AssignmentVerdict.Unknown("the newest check-in is more than 8 hours old"); }
        if (last > nowUtc.AddMinutes(5)) { return AssignmentVerdict.Unknown("the newest check-in is later than the clock"); }
        return new AssignmentVerdict { NoLongerAssigned = true, FirstUtc = first, LastUtc = last, CheckIns = window.Count };
    }

    /// <summary>The words of the support package's state column for a record that is no longer assigned.</summary>
    public static string StateText(AppStateRow row, AssignmentVerdict verdict)
        => string.Format(CultureInfo.InvariantCulture,
            "no longer assigned to this {0}: not in the {1} required check-ins of the IME from {2} to {3} (UTC); the record is still on the device (it read {4}: {5})",
            row.ScopeRef == "Device" ? "device" : "user", verdict.CheckIns, Iso(verdict.FirstUtc.Value), Iso(verdict.LastUtc.Value), row.Verdict, row.State);

    /// <summary>The support package's verdict for a record that is no longer assigned.</summary>
    public const string NotAssignedVerdict = "NOT-ASSIGNED";

    /// <summary>
    /// Marks the rows that are no longer assigned (verdict <see cref="NotAssignedVerdict"/>, the
    /// evidence in their state) for the support package; the other rows stay as they are. Returns how
    /// many were marked. Reads the logs only when a row can qualify; never throws.
    /// </summary>
    public static int Mark(RunContext run, IReadOnlyList<AppStateRow> rows, Func<RunContext, ISet<string>, ImeAssignmentRead> read = null)
    {
        try
        {
            if (rows == null || !rows.Any(r => Qualifies(r.Intent))) { return 0; }
            var checkIns = (read ?? ((r, s) => Read(r, s)))(run, AppState.VisibleScopes(run));
            var now = run.Services.Clock.UtcNow;
            var marked = 0;
            foreach (var row in rows)
            {
                var verdict = Evaluate(row.ScopeId, row.AppId, row.Intent, row.KeyWrittenUtc, checkIns, now);
                if (!verdict.NoLongerAssigned) { continue; }
                row.State = StateText(row, verdict);
                row.Verdict = NotAssignedVerdict;
                marked++;
            }
            Report(run, checkIns, marked);
            return marked;
        }
        catch (Exception ex)
        {
            run.Log("Whether app records are still assigned could not be checked (" + ex.Message + ").", LogSeverity.Warning);
            return 0;
        }
    }

    /// <summary>One run log line on the check-ins read: counts only, never a user ID, a time or a list.</summary>
    public static string LogLine(ImeAssignmentRead read, int noLongerAssigned)
    {
        if (read == null || !read.Available)
        {
            return "Assignments: the IME's check-ins were not read" + (read != null && read.Reason.Length > 0 ? " (" + read.Reason + ")" : "") + "; no app record is called no longer assigned.";
        }
        var c = Counts(read);
        return string.Format(CultureInfo.InvariantCulture, "Assignments: {0} of the device read ({1} complete), {2} of the user ({3} complete); {4} no longer assigned.",
            TextFormat.Count(c.Device, "required check-in"), c.DeviceComplete, c.User, c.UserComplete, TextFormat.Count(noLongerAssigned, "app record"));
    }

    /// <summary>
    /// The engine log's one line per diagnosis or package, numbers only, so a support package shows
    /// whether the check-in patterns match on that device: "EndpointToolbelt:APP-ASSIGNMENTS read=1
    /// device=3 devicecomplete=2 user=4 usercomplete=4 marked=1".
    /// </summary>
    public static string MarkerLine(ImeAssignmentRead read, int noLongerAssigned)
    {
        var c = read != null && read.Available ? Counts(read) : (Device: 0, DeviceComplete: 0, User: 0, UserComplete: 0);
        return string.Format(CultureInfo.InvariantCulture, "EndpointToolbelt:APP-ASSIGNMENTS read={0} device={1} devicecomplete={2} user={3} usercomplete={4} marked={5}",
            read != null && read.Available ? 1 : 0, c.Device, c.DeviceComplete, c.User, c.UserComplete, noLongerAssigned);
    }

    /// <summary>Both lines: the run log's words and the engine log's numbers.</summary>
    public static void Report(RunContext run, ImeAssignmentRead read, int noLongerAssigned)
    {
        run.Log(LogLine(read, noLongerAssigned));
        run.PersistentLog(MarkerLine(read, noLongerAssigned));
    }

    private static (int Device, int DeviceComplete, int User, int UserComplete) Counts(ImeAssignmentRead read)
    {
        var device = read.CheckIns.Where(c => c.Scope == AppState.DeviceScopeId).ToList();
        var user = read.CheckIns.Where(c => c.Scope != AppState.DeviceScopeId).ToList();
        return (device.Count, device.Count(c => c.Complete), user.Count, user.Count(c => c.Complete));
    }

    internal static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
