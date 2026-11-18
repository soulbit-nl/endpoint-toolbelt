using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.Actions;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Engine.Support;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// An IME app record that is no longer assigned: called so only when the IME's own check-ins prove
/// it (every required check-in of its scope after the record's last change is complete and leaves the
/// app out, they span at least 8 hours and the newest is at most 8 hours old), never on the absence of
/// evidence. Every log line here is made up for the test; nothing reads this machine's IME state.
/// </summary>
public class NoLongerAssignedTests : IDisposable
{
    private const string Device = AppState.DeviceScopeId;
    private const string UserOne = EngineAppDiagnosisTests.UserOne;
    private const string UserTwo = EngineAppDiagnosisTests.UserTwo;
    private const string Gone = "77777777-8888-9999-aaaa-bbbbbbbbbbbb";
    private const string Kept = "88888888-9999-aaaa-bbbb-cccccccccccc";
    private const string Needed = "99999999-aaaa-bbbb-cccc-dddddddddddd";

    private static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    private static readonly HashSet<string> Scopes = new(StringComparer.OrdinalIgnoreCase) { Device, UserOne };

    private readonly TestFolder _folder = new();
    private readonly EngineHarness _h = new();

    public void Dispose() => _folder.Dispose();

    // --- Made-up log lines ----------------------------------------------------------------------------

    private static string SessionLine(string user) => "[Win32App] ..................... Processing user session 1, userId: " + user + ", ESP status: null .....................";
    private const string RequiredLine = "[Win32App] Requesting required apps";
    private const string AvailableLine = "[Win32App] Requesting available apps only";
    private const string FailureLine = "[Location Service] Failed to Get Endpoint";

    private static string Policies(params string[] ids)
        => "Get policies = [" + string.Join(",", ids.Select(id => "{\"Id\":\"" + id + "\",\"Name\":\"Contoso App\",\"Intent\":3,\"TargetType\":1}")) + "]";

    private static CmTraceRecord R(string message, DateTime utc, int thread = 5, int type = 1) => new() { Message = message, Utc = utc, Thread = thread, Type = type, Source = "AppWorkload.log" };

    /// <summary>A whole check-in for the required apps of one user (or the device) at one moment.</summary>
    private static IEnumerable<CmTraceRecord> CheckIn(string user, DateTime at, string policies, int thread = 5)
    {
        yield return R(SessionLine(user), at.AddSeconds(-3), thread);
        yield return R(RequiredLine, at.AddSeconds(-2), thread);
        yield return R(policies, at, thread);
    }

    private List<RequiredAppsCheckIn> Read(IEnumerable<CmTraceRecord> records)
    {
        var table = ImeLog.Rules(_h.Services.ImePaths.PatternFile);
        return ImeAssignments.CheckIns(records.OrderBy(r => r.Utc), table.AssignmentRules, ImeAssignments.FailureClaimRules(table), Scopes);
    }

    private static RequiredAppsCheckIn Complete(string scope, DateTime utc, params string[] ids)
    {
        var checkIn = new RequiredAppsCheckIn { Scope = scope, Utc = utc, Complete = true, Policies = ids.Length };
        foreach (var id in ids) { checkIn.Mentioned.Add(id); }
        return checkIn;
    }

    private static ImeAssignmentRead ReadOf(params RequiredAppsCheckIn[] checkIns)
    {
        var read = new ImeAssignmentRead { Available = true };
        read.CheckIns.AddRange(checkIns);
        return read;
    }

    /// <summary>The case that holds: two complete check-ins 9 hours apart, the newest an hour old, both after the record's last change.</summary>
    private static ImeAssignmentRead Proof(string scope = UserOne)
        => ReadOf(Complete(scope, Now.AddHours(-10), Kept), Complete(scope, Now.AddHours(-1), Kept));

    private static readonly DateTime Written = Now.AddDays(-3);

    // --- The pattern table ------------------------------------------------------------------------------

    [Fact]
    public void The_pattern_table_has_the_check_in_rules_and_they_match_their_own_lines_only()
    {
        var table = ImeLog.Rules(_h.Services.ImePaths.PatternFile);
        Assert.Equal(new[] { "policies", "session", "required", "available" }, table.AssignmentRules.Select(r => r.Kind));
        Assert.Contains("_IntuneManagementExtension*.log", table.AssignmentFiles);
        Assert.Contains("IntuneManagementExtension*.log", table.AssignmentFiles);
        string KindOf(string line) => table.AssignmentRules.FirstOrDefault(r => r.Regex.IsMatch(line))?.Kind;
        Assert.Equal("session", KindOf(SessionLine(UserOne)));
        Assert.Equal("required", KindOf(RequiredLine));
        Assert.Equal("available", KindOf(AvailableLine));
        Assert.Equal("policies", KindOf(Policies(Gone)));
        Assert.Null(KindOf(FailureLine));
        Assert.Null(KindOf("[Win32App] Asked for required apps"));
        Assert.Equal(UserOne, table.AssignmentRules.Single(r => r.Kind == "session").Regex.Match(SessionLine(UserOne)).Groups["user"].Value);
    }

    // --- Reading the check-ins ------------------------------------------------------------------------

    [Fact]
    public void A_whole_check_in_is_complete_with_every_app_ID_its_list_names_in_any_role()
    {
        var withDependency = "Get policies = [{\"Id\":\"" + Kept + "\",\"Name\":\"Contoso App\",\"FlatDependencies\":[{\"ChildId\":\"" + Needed.ToUpperInvariant() + "\"}]}]";
        var checkIns = Read(CheckIn(UserOne, Now.AddHours(-1), withDependency).Concat(CheckIn(Device, Now.AddHours(-2), Policies(Gone), thread: 9)));
        Assert.Equal(2, checkIns.Count);
        var device = checkIns[0];
        Assert.Equal(Device, device.Scope);
        Assert.True(device.Complete);
        Assert.Contains(Gone, device.Mentioned);
        var user = checkIns[1];
        Assert.Equal(UserOne, user.Scope);
        Assert.True(user.Complete, user.Problem);
        Assert.Equal(1, user.Policies);
        // The dependency counts as named, in any letter case.
        Assert.Contains(Needed, user.Mentioned);
        Assert.Contains(Kept, user.Mentioned);
        Assert.Equal(Now.AddHours(-1), user.Utc);
    }

    [Fact]
    public void An_available_only_check_in_another_users_check_in_and_a_thread_without_a_known_user_give_none()
    {
        var records = new List<CmTraceRecord>
        {
            R(SessionLine(UserOne), Now.AddHours(-3).AddSeconds(-2)),
            R(AvailableLine, Now.AddHours(-3).AddSeconds(-1)),
            R(Policies(Kept), Now.AddHours(-3)),
            // Another user's check-in is never read as one of the caller's.
            R(SessionLine(UserTwo), Now.AddHours(-2).AddSeconds(-2), thread: 6),
            R(RequiredLine, Now.AddHours(-2).AddSeconds(-1), thread: 6),
            R(Policies(Kept), Now.AddHours(-2), thread: 6),
            // A required check-in on a thread whose user no line named.
            R(RequiredLine, Now.AddHours(-1).AddSeconds(-1), thread: 7),
            R(Policies(Kept), Now.AddHours(-1), thread: 7),
        };
        Assert.Empty(Read(records));
    }

    [Theory]
    [InlineData("Get policies = [{\"Id\":\"" + Kept + "\",\"Name\":\"Contoso", "cut off")]
    [InlineData("Get policies = []", "empty")]
    [InlineData("Get policies = [{\"Id\":\"" + Kept + "\" \"Name\"}]", "not valid JSON")]
    [InlineData("Get policies = [{\"Name\":\"Contoso App\"}]", "no app ID")]
    [InlineData("Get policies = [\"" + Kept + "\"]", "no app ID")]
    public void A_list_that_is_cut_off_empty_not_JSON_or_without_app_IDs_is_not_complete(string line, string problem)
    {
        var checkIn = Assert.Single(Read(CheckIn(UserOne, Now.AddHours(-1), line)));
        Assert.False(checkIn.Complete);
        Assert.Contains(problem, checkIn.Problem);
    }

    [Fact]
    public void The_imes_own_malformed_address_while_it_runs_leaves_a_check_in_complete()
    {
        var records = new List<CmTraceRecord>
        {
            R(SessionLine(UserOne), Now.AddHours(-1).AddSeconds(-3)),
            R(RequiredLine, Now.AddHours(-1).AddSeconds(-2)),
            // As the IME writes it: a warning record (type 2), on the location service's own thread.
            R(ImeCheckInTests.MalformedAddress, Now.AddHours(-1).AddSeconds(-1), thread: 40, type: 2),
            R(Policies(Kept), Now.AddHours(-1)),
        };
        Assert.True(Assert.Single(Read(records)).Complete);

        // The same failure for an https address is a real one and spoils the check-in.
        records[2] = R(ImeCheckInTests.MalformedHttpsAddress, Now.AddHours(-1).AddSeconds(-1), thread: 40, type: 2);
        Assert.Contains("failure", Assert.Single(Read(records)).Problem);
    }

    [Fact]
    public void A_failure_while_it_runs_an_error_record_or_no_list_at_all_makes_a_check_in_incomplete()
    {
        var failed = new List<CmTraceRecord>
        {
            R(SessionLine(UserOne), Now.AddHours(-1).AddSeconds(-3)),
            R(RequiredLine, Now.AddHours(-1).AddSeconds(-2)),
            // The location service writes on a thread of its own.
            R(FailureLine, Now.AddHours(-1).AddSeconds(-1), thread: 40),
            R(Policies(Kept), Now.AddHours(-1)),
        };
        Assert.Contains("failure", Assert.Single(Read(failed)).Problem);

        var errorRecord = CheckIn(UserOne, Now.AddHours(-1), Policies(Kept)).ToList();
        errorRecord[2].Type = 3;
        Assert.False(Assert.Single(Read(errorRecord)).Complete);

        // A check-in that never got its list: before the next one, or at the end of the log.
        var noList = new List<CmTraceRecord>
        {
            R(SessionLine(UserOne), Now.AddHours(-3)),
            R(RequiredLine, Now.AddHours(-3).AddSeconds(1)),
            R(RequiredLine, Now.AddHours(-2)),
            R(Policies(Kept), Now.AddHours(-2).AddSeconds(1)),
            R(RequiredLine, Now.AddMinutes(-5)),
        };
        var checkIns = Read(noList);
        Assert.Equal(3, checkIns.Count);
        Assert.Equal(new[] { false, true, false }, checkIns.Select(c => c.Complete));
        Assert.All(checkIns.Where(c => !c.Complete), c => Assert.Contains("without a policy list", c.Problem));
    }

    [Fact]
    public void Reads_the_check_ins_from_the_IME_logs_and_its_rolled_over_copy_with_the_shared_reader()
    {
        var folder = _folder.Combine("logs");
        Directory.CreateDirectory(folder);
        static string Line(CmTraceRecord r) => string.Format(CultureInfo.InvariantCulture,
            "<![LOG[{0}]LOG]!><time=\"{1}+000\" date=\"{2}\" component=\"AppWorkload\" context=\"\" type=\"{3}\" thread=\"{4}\" file=\"\">",
            r.Message, r.Utc.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture), r.Utc.ToString("M-d-yyyy", CultureInfo.InvariantCulture), r.Type, r.Thread);
        File.WriteAllLines(Path.Combine(folder, "_IntuneManagementExtension.log"), CheckIn(UserOne, Now.AddHours(-10), Policies(Kept)).Select(Line));
        File.WriteAllLines(Path.Combine(folder, "AppWorkload.log"), CheckIn(UserOne, Now.AddHours(-1), Policies(Kept)).Select(Line));
        _h.Services.ImePaths.LogsFolder = folder;
        _h.Clock.UtcNow = Now;

        var read = ImeAssignments.Read(_h.NewRun(), Scopes);

        Assert.True(read.Available, read.Reason);
        Assert.Equal(new[] { Now.AddHours(-10), Now.AddHours(-1) }, read.CheckIns.Select(c => c.Utc));
        Assert.True(ImeAssignments.Evaluate(UserOne, Gone, "Required", Written, read, Now).NoLongerAssigned);
    }

    // --- The rule -------------------------------------------------------------------------------------------

    [Fact]
    public void A_to_D_hold_the_record_is_no_longer_assigned_with_the_first_and_last_check_in_as_its_evidence()
    {
        var read = ReadOf(Complete(UserOne, Now.AddHours(-10), Kept), Complete(UserOne, Now.AddHours(-5), Kept), Complete(UserOne, Now.AddHours(-1), Kept));
        var verdict = ImeAssignments.Evaluate(UserOne, Gone, "Required", Written, read, Now);
        Assert.True(verdict.NoLongerAssigned, verdict.Reason);
        Assert.Equal(Now.AddHours(-10), verdict.FirstUtc);
        Assert.Equal(Now.AddHours(-1), verdict.LastUtc);
        Assert.Equal(3, verdict.CheckIns);
        // A dependency qualifies too, and the scope is matched in any letter case.
        Assert.True(ImeAssignments.Evaluate(UserOne.ToUpperInvariant(), Gone.ToUpperInvariant(), "Dependency (not assigned)", Written, Proof(), Now).NoLongerAssigned);
        // A check-in before the record's last change does not count, complete or not.
        var older = Proof();
        older.CheckIns.Insert(0, new RequiredAppsCheckIn { Scope = UserOne, Utc = Written.AddHours(-1), Problem = "the policy list is cut off" });
        Assert.True(ImeAssignments.Evaluate(UserOne, Gone, "Required", Written, older, Now).NoLongerAssigned);
    }

    [Theory]
    [InlineData("Available")]
    [InlineData("Uninstall")]
    [InlineData("")]
    [InlineData("Intent 9")]
    public void Only_a_required_app_or_a_dependency_can_be_called_no_longer_assigned(string intent)
    {
        var verdict = ImeAssignments.Evaluate(UserOne, Gone, intent, Written, Proof(), Now);
        Assert.False(verdict.NoLongerAssigned);
        Assert.Contains("required", verdict.Reason);
    }

    [Fact]
    public void A_The_record_is_unknown_when_a_check_in_after_its_last_change_is_not_complete()
    {
        var read = Proof();
        read.CheckIns.Insert(1, new RequiredAppsCheckIn { Scope = UserOne, Utc = Now.AddHours(-4), Problem = "a failure was logged while the check-in ran" });
        var verdict = ImeAssignments.Evaluate(UserOne, Gone, "Required", Written, read, Now);
        Assert.False(verdict.NoLongerAssigned);
        Assert.Contains("not complete", verdict.Reason);
    }

    [Fact]
    public void B_The_record_is_unknown_without_a_check_in_after_its_last_change_or_when_that_change_is_not_known()
    {
        Assert.Contains("no required check-in", ImeAssignments.Evaluate(UserOne, Gone, "Required", Now.AddMinutes(-30), Proof(), Now).Reason);
        Assert.False(ImeAssignments.Evaluate(UserOne, Gone, "Required", Now.AddHours(-5), Proof(), Now).NoLongerAssigned);
        Assert.Contains("last change is not known", ImeAssignments.Evaluate(UserOne, Gone, "Required", null, Proof(), Now).Reason);
        // The check-ins of another scope prove nothing for this one.
        Assert.False(ImeAssignments.Evaluate(Device, Gone, "Required", Written, Proof(UserOne), Now).NoLongerAssigned);
    }

    [Fact]
    public void C_The_record_is_unknown_when_any_check_in_names_the_app_in_any_role()
    {
        var read = ReadOf(Complete(UserOne, Now.AddHours(-10), Kept), Complete(UserOne, Now.AddHours(-5), Kept, Gone), Complete(UserOne, Now.AddHours(-1), Kept));
        var verdict = ImeAssignments.Evaluate(UserOne, Gone, "Required", Written, read, Now);
        Assert.False(verdict.NoLongerAssigned);
        Assert.Contains("lists the app", verdict.Reason);
    }

    [Fact]
    public void D_The_record_is_unknown_when_the_check_ins_span_less_than_8_hours_or_the_newest_is_older_than_8_hours()
    {
        var short1 = ReadOf(Complete(UserOne, Now.AddHours(-7).AddMinutes(-59), Kept), Complete(UserOne, Now.AddHours(-1), Kept));
        Assert.Contains("less than 8 hours", ImeAssignments.Evaluate(UserOne, Gone, "Required", Written, short1, Now).Reason);
        var single = ReadOf(Complete(UserOne, Now.AddHours(-1), Kept));
        Assert.False(ImeAssignments.Evaluate(UserOne, Gone, "Required", Written, single, Now).NoLongerAssigned);
        var stale = ReadOf(Complete(UserOne, Now.AddHours(-20), Kept), Complete(UserOne, Now.AddHours(-9), Kept));
        Assert.Contains("more than 8 hours old", ImeAssignments.Evaluate(UserOne, Gone, "Required", Written, stale, Now).Reason);
        var future = ReadOf(Complete(UserOne, Now.AddHours(-9), Kept), Complete(UserOne, Now.AddHours(1), Kept));
        Assert.False(ImeAssignments.Evaluate(UserOne, Gone, "Required", Written, future, Now).NoLongerAssigned);
        // Exactly 8 hours apart is enough.
        var exact = ReadOf(Complete(UserOne, Now.AddHours(-9), Kept), Complete(UserOne, Now.AddHours(-1), Kept));
        Assert.True(ImeAssignments.Evaluate(UserOne, Gone, "Required", Written, exact, Now).NoLongerAssigned);
    }

    [Fact]
    public void The_record_is_unknown_when_the_logs_were_not_read()
    {
        Assert.False(ImeAssignments.Evaluate(UserOne, Gone, "Required", Written, null, Now).NoLongerAssigned);
        var unread = new ImeAssignmentRead { Reason = "an IME log could not be read" };
        var verdict = ImeAssignments.Evaluate(UserOne, Gone, "Required", Written, unread, Now);
        Assert.False(verdict.NoLongerAssigned);
        Assert.Contains("an IME log could not be read", verdict.Reason);
        // Without a log folder, or without the rules in the table, nothing is read and nothing is proved.
        _h.Clock.UtcNow = Now;
        var none = ImeAssignments.Read(_h.NewRun(), Scopes);
        Assert.Empty(none.CheckIns);
        Assert.False(ImeAssignments.Evaluate(UserOne, Gone, "Required", Written, none, Now).NoLongerAssigned);
    }

    // --- The engine's app diagnosis --------------------------------------------------------------------

    private void AddRecords()
    {
        // Required apps of the user and the device, and an available one; each key last written three days ago.
        EngineAppDiagnosisTests.AddApp(_h.Registry, UserOne, Gone, 2, intent: 3, state: 1000, compliance: 1);
        EngineAppDiagnosisTests.AddApp(_h.Registry, UserOne, Kept, 1, intent: 3, state: 5000, code: 1603);
        EngineAppDiagnosisTests.AddApp(_h.Registry, UserOne, Needed, 4, intent: 1, state: null, compliance: 2);
        EngineAppDiagnosisTests.AddApp(_h.Registry, Device, Gone, 2, intent: 3, state: 1000, compliance: 1);
        foreach (var key in new[] { UserOne + "\\" + Gone + "_2", UserOne + "\\" + Kept + "_1", UserOne + "\\" + Needed + "_4", Device + "\\" + Gone + "_2" })
        {
            _h.Registry.WriteTimes[@"TEST\Win32Apps\" + key] = Written;
        }
        _h.Clock.UtcNow = Now;
    }

    [Fact]
    public void The_diagnosis_marks_only_the_proven_record_keeps_its_verdict_and_logs_the_evidence()
    {
        AddRecords();
        var run = _h.NewRun("diag0101", "DiagnoseApps");
        run.CallerSid = EngineAppDiagnosisTests.EntraSid(UserOne);
        run.LogDirectory = _folder.Path;
        var handler = new DiagnoseAppsHandler { Parts = new DiagnosisParts { Assignments = (_, scopes) => { Assert.True(scopes.SetEquals(Scopes)); return Proof(); } } };

        Assert.Equal(EngineExitCode.Success, handler.Run(run));

        var apps = _h.Records("app-diagnosis");
        var gone = apps.Single(a => a.Str("app") == Gone && a.Str("scope") == "user");
        Assert.Equal("gone", gone.Str("assignment"));
        Assert.Equal("2026-11-16T02:00:00Z", gone.Str("assignfirst"));
        Assert.Equal("2026-11-16T11:00:00Z", gone.Str("assignlast"));
        Assert.Equal("2", gone.Str("assigncount"));
        Assert.Equal("OK", gone.Str("verdict"));
        // The user's failed app is in the lists; the available one cannot qualify; the device has no proof of its own.
        Assert.Equal("", apps.Single(a => a.Str("app") == Kept).Str("assignment"));
        Assert.Equal("", apps.Single(a => a.Str("app") == Needed).Str("assignment"));
        Assert.Equal("", apps.Single(a => a.Str("app") == Gone && a.Str("scope") == "device").Str("assignment"));
        Assert.Contains(_h.Lines, l => l.Contains("2 of the user (2 complete); 1 app record no longer assigned."));
        var engineLog = File.ReadAllText(_folder.Combine(EndpointToolbelt.ProductInfo.EngineLogFileName)).Split('\n');
        Assert.Contains(engineLog, l => l.Contains("EndpointToolbelt:APP-ASSIGNMENT ") && l.Contains("no longer assigned") && l.Contains("2026-11-16T02:00:00Z"));
        // One engine log line per diagnosis, numbers only.
        Assert.Contains(engineLog, l => l.Contains("EndpointToolbelt:APP-ASSIGNMENTS read=1 device=0 devicecomplete=0 user=2 usercomplete=2 marked=1"));
        Assert.DoesNotContain(_h.Lines.Concat(engineLog), l => l.Contains(UserOne));
        Assert.Empty(_h.Records("change"));

        // The app reads the same record as no longer assigned to the user.
        var line = _h.Lines.First(l => l.Contains("\"app-diagnosis\"") && l.Contains(Gone) && l.Contains("\"gone\""));
        var evidence = EngineRecord.Parse(line).App;
        Assert.True(evidence.NoLongerAssigned);
        Assert.Equal(2, evidence.AssignCount);
    }

    [Fact]
    public void The_diagnosis_reads_no_log_when_no_record_can_qualify_and_marks_nothing_when_the_read_fails()
    {
        EngineAppDiagnosisTests.AddApp(_h.Registry, UserOne, Needed, 4, intent: 1, state: null, compliance: 2);
        _h.Clock.UtcNow = Now;
        var run = _h.NewRun("diag0102", "DiagnoseApps");
        run.CallerSid = EngineAppDiagnosisTests.EntraSid(UserOne);
        var asked = false;
        var handler = new DiagnoseAppsHandler { Parts = new DiagnosisParts { Assignments = (_, _) => { asked = true; return Proof(); } } };
        Assert.Equal(EngineExitCode.Success, handler.Run(run));
        Assert.False(asked);

        var other = new EngineHarness();
        EngineAppDiagnosisTests.AddApp(other.Registry, UserOne, Gone, 2, intent: 3, state: 1000, compliance: 1);
        other.Registry.WriteTimes[@"TEST\Win32Apps\" + UserOne + "\\" + Gone + "_2"] = Written;
        other.Clock.UtcNow = Now;
        var failing = other.NewRun("diag0103", "DiagnoseApps");
        failing.CallerSid = EngineAppDiagnosisTests.EntraSid(UserOne);
        failing.LogDirectory = _folder.Combine("failing");
        Directory.CreateDirectory(failing.LogDirectory);
        var throwing = new DiagnoseAppsHandler { Parts = new DiagnosisParts { Assignments = (_, _) => throw new IOException("the log is locked") } };
        Assert.Equal(EngineExitCode.Success, throwing.Run(failing));
        Assert.Equal("", Assert.Single(other.Records("app-diagnosis")).Str("assignment"));
        Assert.Contains(other.Lines, l => l.Contains("no app record is called no longer assigned"));
        Assert.Contains("EndpointToolbelt:APP-ASSIGNMENTS read=0 device=0 devicecomplete=0 user=0 usercomplete=0 marked=0",
            File.ReadAllText(Path.Combine(failing.LogDirectory, EndpointToolbelt.ProductInfo.EngineLogFileName)), StringComparison.Ordinal);
    }

    // --- The support package -----------------------------------------------------------------------------

    [Fact]
    public void The_support_package_puts_the_verdict_in_its_verdict_column_and_leaves_every_other_row_as_it_reads()
    {
        _h.Clock.UtcNow = Now;
        var run = _h.NewRun();
        run.CallerSid = EngineAppDiagnosisTests.EntraSid(UserOne);
        var rows = new List<AppStateRow>
        {
            new() { ScopeRef = "User:aaaaaaaa", ScopeId = UserOne, AppId = Gone, Intent = "Required", Verdict = "OK", State = "installed / succeeded", KeyWrittenUtc = Written },
            new() { ScopeRef = "User:aaaaaaaa", ScopeId = UserOne, AppId = Kept, Intent = "Required", Verdict = "RETRY-HELPS", State = "failed with an error", KeyWrittenUtc = Written },
            new() { ScopeRef = "User:aaaaaaaa", ScopeId = UserOne, AppId = Needed, Intent = "Available", Verdict = "AVAILABLE", State = "offered", KeyWrittenUtc = Written },
            new() { ScopeRef = "Device", ScopeId = Device, AppId = Gone, Intent = "Required", Verdict = "OK", State = "installed / succeeded", KeyWrittenUtc = null },
        };

        Assert.Equal(1, ImeAssignments.Mark(run, rows, (_, _) => Proof()));

        Assert.Equal(ImeAssignments.NotAssignedVerdict, rows[0].Verdict);
        Assert.StartsWith("no longer assigned to this user: not in the 2 required check-ins of the IME from 2026-11-16T02:00:00Z to 2026-11-16T11:00:00Z (UTC)", rows[0].State);
        Assert.EndsWith("(it read OK: installed / succeeded)", rows[0].State);
        Assert.Equal(new[] { "RETRY-HELPS", "AVAILABLE", "OK" }, rows.Skip(1).Select(r => r.Verdict));
        Assert.Equal("NOT-ASSIGNED", PackageSheets.AppStateCells(rows[0])[Array.IndexOf(PackageSheets.AppStateHeaders, "Verdict")]);
        // The scope's full ID never reaches the table.
        Assert.DoesNotContain(PackageSheets.AppStateCells(rows[0]), c => (c?.ToString() ?? "").Contains(UserOne));
        // A read that throws marks nothing.
        Assert.Equal(0, ImeAssignments.Mark(run, rows.Skip(1).ToList(), (_, _) => throw new IOException("locked")));
    }

    // --- The app -----------------------------------------------------------------------------------------

    private static AppEvidence Record(string scope = "user", string verdict = "RETRY-HELPS", string intent = "required")
    {
        var app = AppDiagnosisTests.App(verdict, intent: intent, scope: scope, code: verdict == "RETRY-HELPS" ? "0x80070643" : "");
        app.Assignment = "gone";
        app.AssignFirstText = "2026-11-16T02:00:00Z";
        app.AssignLastText = "2026-11-16T11:00:00Z";
        app.AssignCount = 3;
        return app;
    }

    private static AppFinding Decide(AppEvidence app) => AppDecisions.Decide(app, new[] { app }, Now, Amsterdam);

    [Fact]
    public void The_app_says_no_longer_assigned_with_no_step_no_retry_and_no_Company_Portal_link()
    {
        var user = Decide(Record());
        Assert.Equal(AppCase.NoLongerAssigned, user.Case);
        Assert.Equal(AppObservation.NoLongerAssigned, user.Observation);
        Assert.Equal(AppNextStep.None, user.Next);
        Assert.Equal("No longer assigned to you", AppDecisions.Word(user));
        Assert.Equal("Contoso Agent: no longer assigned to you", user.Title);
        Assert.Contains("still on this device", user.Text);
        Assert.Contains("Intune no longer sends the app to you", user.Text);
        Assert.Equal("", user.Caveat);
        Assert.DoesNotContain("Retry", user.Text + user.NextText + user.Support);
        Assert.False(AppDecisions.HasPortalPage(user));
        Assert.False(AppDecisions.ShowPortalLink(user, user.Next));
        Assert.False(user.NeedsAttention);
        Assert.False(AppDecisions.InShortList(user));
        Assert.Equal("Status: No longer assigned to you, recorded 2026-11-10 14:00 (UTC+01:00)", AppDecisions.DetailState(user, Amsterdam).Split('\n')[0]);

        var device = Decide(Record("device", "OK"));
        Assert.Equal("No longer assigned to this device", AppDecisions.Word(device));
        Assert.Equal("Contoso Agent: no longer assigned to this device", device.Title);
    }

    [Fact]
    public void The_card_and_Copy_details_for_IT_carry_both_check_in_times_and_what_the_record_read()
    {
        var f = Decide(Record());
        Assert.Equal("Evidence: the Intune Management Extension's check-ins for required apps at 2026-11-16 03:00 (UTC+01:00) and at 2026-11-16 12:00 (UTC+01:00) (3 in all) did not list this app.", f.Evidence);
        var card = f.Card();
        Assert.Contains("2026-11-16 03:00 (UTC+01:00)", card.Detail);
        Assert.Contains("2026-11-16 12:00 (UTC+01:00)", card.Detail);
        Assert.Contains("IME record: a state.", card.Support);
        Assert.Contains("App ID " + AppDiagnosisTestsAppA + " (user assignment, revision 3).", card.Support);
    }

    private const string AppDiagnosisTestsAppA = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void Without_proof_nothing_changes_for_the_record()
    {
        // The flag without its times, an MSI app, and no flag at all: the finding the record reads today.
        var noTimes = Record();
        noTimes.AssignLastText = "";
        Assert.Equal(AppCase.InstallFailed, Decide(noTimes).Case);
        var msi = Record();
        msi.Kind = "msi";
        Assert.NotEqual(AppCase.NoLongerAssigned, Decide(msi).Case);
        var plain = AppDiagnosisTests.App("OK");
        Assert.Equal(AppCase.Detected, Decide(plain).Case);
        Assert.Equal("Detected", AppDecisions.Word(Decide(plain)));
    }

    [Fact]
    public void A_record_no_longer_assigned_is_not_Homes_finding_and_stays_one_row_per_scope()
    {
        var diagnosis = AppDecisions.From(AppDiagnosisTests.Outcome(Record("user"), Record("device")), Now, Amsterdam);
        Assert.Equal(2, diagnosis.Apps.Count);
        Assert.All(diagnosis.Apps, a => Assert.False(a.ForBothScopes));
        Assert.Null(diagnosis.Main);
        Assert.DoesNotContain(diagnosis.Apps, AppDecisions.InShortList);
        // The search still finds them.
        Assert.Equal(2, AppDecisions.Search(diagnosis.Apps, "Contoso").Count);
    }
}
