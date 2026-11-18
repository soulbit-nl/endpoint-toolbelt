using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.Ime;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// IME log evidence and the Win32 app state readout of the engine, and the code table.
/// Logs are written to the test's folder and
/// registry data to a fake: nothing here reads this machine's IME state.
/// </summary>
public class EngineImeLogTests : IDisposable
{
    private const string AppA = "aaaaaaaa-1111-2222-3333-444444444444";
    private const string AppB = "bbbbbbbb-1111-2222-3333-444444444444";
    private const string AppC = "cccccccc-1111-2222-3333-444444444444";
    private const string Other = "dddddddd-1111-2222-3333-444444444444";
    private readonly TestFolder _folder = new();
    private readonly EngineHarness _h = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>One CMTrace record as the IME writes it: the device's own clock, seven fraction digits.</summary>
    private static string CmLine(string message, DateTime utc, int thread = 7, int type = 1, string component = "AppWorkload")
    {
        var local = utc.ToLocalTime();
        return string.Format(CultureInfo.InvariantCulture, "<![LOG[{0}]LOG]!><time=\"{1}\" date=\"{2}\" component=\"{3}\" context=\"\" type=\"{4}\" thread=\"{5}\" file=\"\">",
            message, local.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture), local.ToString("M-d-yyyy", CultureInfo.InvariantCulture), component, type, thread);
    }

    [Fact]
    public void Splits_on_records_keeps_multi_line_messages_honours_a_bias_and_drops_old_or_timeless_records()
    {
        var text = "cut-off tail]LOG]!><time=\"10:00:00.0000000\" date=\"10-16-2026\" component=\"X\" context=\"\" type=\"1\" thread=\"1\" file=\"\">\r\n" +
            "<![LOG[first line\r\nsecond line]LOG]!><time=\"10:00:01.1234567\" date=\"10-16-2026\" component=\"AppWorkload\" context=\"\" type=\"3\" thread=\"12\" file=\"\">\r\n" +
            "<![LOG[with bias]LOG]!><time=\"08:00:00.000-120\" date=\"10-16-2026\" component=\"Engine\" context=\"\" type=\"2\" thread=\"4\" file=\"\">\r\n" +
            "<![LOG[bad time]LOG]!><time=\"99:99:99.0\" date=\"10-16-2026\" component=\"X\" context=\"\" type=\"1\" thread=\"1\" file=\"\">\r\n" +
            "<![LOG[old]LOG]!><time=\"10:00:00.0000000\" date=\"1-2-2020\" component=\"X\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";
        var records = CmTraceReader.ParseText(text, new DateTime(2026, 1, 1));
        Assert.Equal(2, records.Count);
        Assert.Equal("first line\r\nsecond line", records[0].Message);
        Assert.Equal(12, records[0].Thread);
        Assert.Equal(3, records[0].Type);
        Assert.Equal("AppWorkload", records[0].Component);
        Assert.Equal(DateTimeKind.Utc, records[0].Utc.Kind);
        // CMTrace: UTC = local + bias, so 08:00 with bias -120 is 06:00 UTC.
        Assert.Equal(new DateTime(2026, 10, 16, 6, 0, 0, DateTimeKind.Utc), records[1].Utc);
        Assert.Equal(2, records[1].Type);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [Fact]
    public void Reads_only_the_tail_of_a_file_and_refuses_a_hard_linked_file()
    {
        var folder = _folder.Combine("tail");
        Directory.CreateDirectory(folder);
        var now = DateTime.UtcNow;
        File.WriteAllLines(Path.Combine(folder, "Big.log"), Enumerable.Range(1, 200).Select(i => CmLine("line " + i.ToString(CultureInfo.InvariantCulture) + " " + new string('x', 100), now)));
        Assert.True(CreateHardLink(Path.Combine(folder, "Linked-copy.txt"), Path.Combine(folder, "Big.log"), IntPtr.Zero));
        var read = CmTraceReader.Read(folder, "Big.log", () => TimeSpan.Zero, maxBytesPerFile: 2000);
        Assert.Equal(0, read.Files);
        Assert.Equal(1, read.Unreadable);
        File.Delete(Path.Combine(folder, "Linked-copy.txt"));
        read = CmTraceReader.Read(folder, "Big.log", () => TimeSpan.Zero, maxBytesPerFile: 2000);
        Assert.Equal(1, read.Files);
        Assert.InRange(read.Records.Count, 1, 19);
        Assert.StartsWith("line 200 ", read.Records[read.Records.Count - 1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loads_the_pattern_table_compiles_every_rule_and_names_only_known_phases_and_results()
    {
        var table = ImeLog.Rules(_h.Services.ImePaths.PatternFile);
        Assert.Equal(1, table.Version);
        Assert.True(table.AppRules.Count > 10);
        Assert.True(table.CheckInRuleCount > 3);
        Assert.All(table.AppRules, r => Assert.Contains(r.Phase, ImeLog.PhaseOrder));
        Assert.All(table.AppRules, r => Assert.Contains(r.Result, new[] { "Started", "Progress", "Done", "Failed", "Result" }));
        Assert.Equal(table.AppRules.Count, table.AppRules.Select(r => r.Id).Distinct().Count());
        Assert.DoesNotContain(File.ReadAllBytes(_h.Services.ImePaths.PatternFile), b => b > 127);
    }

    private string WriteAppLog()
    {
        var now = DateTime.UtcNow;
        var t = now.AddMinutes(-60);
        var lines = new[]
        {
            // An earlier attempt of A that the newer block replaces.
            CmLine("[Win32App][V3Processor] Processing subgraph with app ids: " + AppA, t.AddMinutes(-30), 5),
            CmLine("[Win32App] lpExitCode 5", t.AddMinutes(-29), 5),
            // A: the newest block, its dependency (Other, not this user's) on the same block.
            CmLine("[Win32App][V3Processor] Processing subgraph with app ids: " + AppA + ", " + Other, t, 5),
            CmLine("[Win32App][DetectionActionHandler] Detection for policy with id: " + AppA + " resulted in action status: Success and detection state: NotDetected.", t.AddSeconds(1), 5),
            CmLine("[Win32App] Downloading app on session 1. App: " + AppA, t.AddSeconds(2), 5),
            CmLine("[StatusService] Downloading app (id = " + AppA + ", name Contoso App) via DO, bytes 500/1000 for user 00000000-0000-0000-0000-000000000000", t.AddSeconds(3), 9),
            CmLine("[Win32App DO] DO download and decryption is successfully done", t.AddSeconds(4), 5),
            CmLine("[Win32App] Starts verifying encrypted hash", t.AddSeconds(5), 5),
            CmLine("[Win32App] file hash validation pass, starts decrypting", t.AddSeconds(6), 5),
            CmLine("[Win32App] Start unzipping.", t.AddSeconds(7), 5),
            CmLine("[Win32App][ExecutionActionHandler] Handler invoked with execution type: Install for policy with id: " + AppA + " and version: 1.", t.AddSeconds(8), 5),
            CmLine("[Win32App] lpExitCode 1603", t.AddSeconds(9), 5),
            CmLine("[Win32App] lpExitCode is defined as Failed", t.AddSeconds(10), 5),
            // A configuration line that mentions a timeout is not a failed download.
            CmLine("[Win32App DO] DO job timeout is set to 3600 seconds", t.AddSeconds(11), 5),
            // The next check-in passes the block again and skips the app: this must not hide the failure above.
            CmLine("[Win32App][V3Processor] Processing subgraph with app ids: " + AppA, now.AddMinutes(-5), 5),
            CmLine("[Win32App][GRSManager] App with id: " + AppA + " is not expired.", now.AddMinutes(-5), 5),
            // B: installs, then is not detected.
            CmLine("[Win32App][V3Processor] Processing subgraph with app ids: " + AppB, t.AddSeconds(20), 6),
            CmLine("[Win32App][ExecutionActionHandler] Handler invoked with execution type: Install for policy with id: " + AppB + " and version: 3.", t.AddSeconds(21), 6),
            CmLine("[Win32App] lpExitCode 0", t.AddSeconds(22), 6),
            CmLine("[Win32App] lpExitCode is defined as Success", t.AddSeconds(23), 6),
            CmLine("[Win32App][DetectionActionHandler] Detection for policy with id: " + AppB + " resulted in action status: Success and detection state: NotDetected.", t.AddSeconds(24), 6),
            // C: a download that stopped moving 40 minutes ago.
            CmLine("[Win32App][V3Processor] Processing subgraph with app ids: " + AppC, now.AddMinutes(-50), 8),
            CmLine("[StatusService] Downloading app (id = " + AppC + ", name Contoso Tool) via DO, bytes 1048576/10485760 for user 00000000-0000-0000-0000-000000000000", now.AddMinutes(-41), 3),
            CmLine("[StatusService] Downloading app (id = " + AppC + ", name Contoso Tool) via DO, bytes 2097152/10485760 for user 00000000-0000-0000-0000-000000000000", now.AddMinutes(-40), 3),
            CmLine("[StatusService] Downloading app (id = " + AppC + ", name Contoso Tool) via DO, bytes 2097152/10485760 for user 00000000-0000-0000-0000-000000000000", now.AddMinutes(-2), 3),
            // Another user's app on its own thread: never returned.
            CmLine("[Win32App][ExecutionActionHandler] Handler invoked with execution type: Install for policy with id: " + Other + " and version: 1.", t.AddSeconds(30), 11),
        };
        var folder = _folder.Combine("attempts");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "AppWorkload.log"), lines);
        return folder;
    }

    [Fact]
    public void Follows_each_app_through_its_phases_by_thread_newest_block_only_own_apps_only()
    {
        _h.Services.Clock = new SystemClock();
        _h.Services.ImePaths.LogsFolder = WriteAppLog();
        var r = ImeLog.Attempts(_h.NewRun(), new[] { AppA, AppB, AppC });
        Assert.True(r.Available);
        Assert.False(r.Attempts.ContainsKey(Other));
        var a = r.Attempts[AppA];
        Assert.Equal("Install", a.Phase);
        Assert.Equal("Failed", a.Result);
        Assert.Equal("1603", a.ExitCode);
        Assert.Equal(500, a.BytesDone);
        // The detection before the install does not count.
        Assert.Null(a.Detected);
        Assert.Equal("install failed with exit code 1603", ImeLog.FormatPhase(a));

        var b = r.Attempts[AppB];
        Assert.False(b.Detected);
        Assert.Equal("installed, but not detected after the install", ImeLog.FormatPhase(b));

        var c = r.Attempts[AppC];
        Assert.Equal("Download", c.Phase);
        // Measured against the newest line of the log (2 minutes ago), not the clock.
        Assert.Matches(@"^the download has stayed at 2 of 10 MB for 3\d min$", ImeLog.Stuck(c));
        Assert.Equal("", ImeLog.Stuck(a));
        // Nothing is stuck while the log shows no activity after the last change.
        var quiet = c.Copy();
        quiet.LogNewestUtc = quiet.BytesChangedUtc.Value.AddMinutes(1);
        Assert.Equal("", ImeLog.Stuck(quiet));
        // A download with all its bytes is not stuck, even without a completion line.
        var full = c.Copy();
        full.BytesDone = full.BytesTotal;
        Assert.Equal("", ImeLog.Stuck(full));
    }

    [Fact]
    public void The_read_only_diagnosis_takes_the_newest_attempt_from_the_log_except_for_an_app_another_user_has()
    {
        // The same IME log evidence as the readout of Retry work apps and settings, without its finding.
        _h.Services.Clock = new SystemClock();
        _h.Services.ImePaths.LogsFolder = WriteAppLog();
        EngineAppDiagnosisTests.AddApp(_h.Registry, AppState.DeviceScopeId, AppA, 1, intent: 3, state: 5000, code: 1603);
        EngineAppDiagnosisTests.AddApp(_h.Registry, AppState.DeviceScopeId, AppB, 3, intent: 3, state: 5000, code: AppState.NotDetectedCode - 4294967296L);
        EngineAppDiagnosisTests.AddApp(_h.Registry, AppState.DeviceScopeId, AppC, 1, intent: 3, state: 2000);
        // Another user has app A as well: its log lines may be theirs.
        EngineAppDiagnosisTests.AddApp(_h.Registry, EngineAppDiagnosisTests.UserTwo, AppA, 1, intent: 3, state: 5000);
        var run = _h.NewRun("diag0010", "DiagnoseApps");
        run.CallerSid = EngineAppDiagnosisTests.EntraSid(EngineAppDiagnosisTests.UserOne);

        Assert.Equal(EngineExitCode.Success, new EndpointToolbelt.Engine.Actions.DiagnoseAppsHandler().Run(run));

        var apps = _h.Records("app-diagnosis").ToDictionary(r => r.Str("app"));
        Assert.Equal("true", apps[AppA].Str("shared"));
        Assert.Equal("", apps[AppA].Str("attempt"));
        Assert.Equal("installed, but not detected after the install", apps[AppB].Str("attempt"));
        Assert.Equal("Detection", apps[AppB].Str("phase"));
        // The stuck download of an app still in progress counts as a download problem.
        Assert.Equal("IN-PROGRESS", apps[AppC].Str("verdict"));
        Assert.Equal("Download", apps[AppC].Str("phase"));
        Assert.Equal("true", apps[AppC].Str("download"));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", apps[AppC].Str("attemptutc"));
        Assert.Equal("true", Assert.Single(_h.Records("apps-diagnosis")).Str("logevidence"));
        // The diagnosis reads the log; it adds no finding of the repair actions.
        Assert.Empty(_h.Findings("APP-DOWNLOAD-STUCK"));
        Assert.Empty(_h.Records("change"));
    }

    [Fact]
    public void Is_no_evidence_without_a_log_or_without_apps_to_look_up()
    {
        Assert.False(ImeLog.Attempts(_h.NewRun(), new[] { AppA }).Available);
        Assert.False(ImeLog.Attempts(_h.NewRun(), Array.Empty<string>()).Available);
    }

    [Fact]
    public void Adds_the_newest_attempt_to_the_finding_of_a_failed_app_and_a_finding_for_a_stuck_download()
    {
        _h.Services.Clock = new SystemClock();
        _h.Services.ImePaths.LogsFolder = WriteAppLog();
        var state = new AppStateReadout { Present = true, Scopes = 1 };
        state.Rows.Add(new AppStateRow { AppId = AppA, Name = "Contoso App", Verdict = "RETRY-HELPS", ScopeRef = "Device", Intent = "Required", State = "failed with an error" });
        state.Rows.Add(new AppStateRow { AppId = AppC, Name = "Contoso Tool", Verdict = "IN-PROGRESS", ScopeRef = "Device", Intent = "Required", State = "enforcement in progress" });
        AppState.WriteSummary(_h.NewRun(), state, "in a test");
        Assert.Matches(@"Newest attempt in the IME log \(\[\[t:[^\]]+\]\]\): install failed with exit code 1603\.", Assert.Single(_h.Findings("APP-RETRY-HELPS")).Str("detail"));
        var stuck = Assert.Single(_h.Findings("APP-DOWNLOAD-STUCK"));
        Assert.Equal("Contoso Tool: the download looks stuck", stuck.Str("title"));
        Assert.Contains("Clear downloaded app content", stuck.Str("next"), StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_the_recorded_failure_per_app_of_the_visible_scopes_and_estimates_the_next_attempt()
    {
        var device = @"TEST\Win32Apps\" + AppState.DeviceScopeId + @"\GRS";
        var other = @"TEST\Win32Apps\eeeeeeee-1111-2222-3333-444444444444\GRS";
        _h.Registry.Put(device + @"\hashA", "aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa", "10/15/2026 9:41:28 PM");
        _h.Registry.Put(device + @"\hashB", "cccccccc-0000-0000-0000-cccccccccccc_1", "1/1/0001 12:00:00 AM");
        _h.Registry.Put(device + @"\bbbbbbbb-0000-0000-0000-bbbbbbbbbbbb", "Time", "10/16/2026 01:00:00");
        _h.Registry.Put(other + @"\hashC", "dddddddd-0000-0000-0000-dddddddddddd", "10/15/2026 9:41:28 PM");
        var map = ImeLog.RetrySchedule(_h.NewRun());
        Assert.Equal(2, map.Count);
        Assert.Equal(new DateTime(2026, 10, 15, 21, 41, 28, DateTimeKind.Utc), map["aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa"].FailedUtc);
        Assert.Equal(new DateTime(2026, 10, 16, 21, 41, 28, DateTimeKind.Utc), map["aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa"].NextUtc);
        Assert.True(map.ContainsKey("bbbbbbbb-0000-0000-0000-bbbbbbbbbbbb"));
        // The zero date is no retry time; another user's scope is not read.
        Assert.False(map.ContainsKey("cccccccc-0000-0000-0000-cccccccccccc"));
        Assert.False(map.ContainsKey("dddddddd-0000-0000-0000-dddddddddddd"));
    }

    [Fact]
    public void Says_when_the_IME_retries_by_itself_only_when_it_was_not_made_to_retry_now()
    {
        var run = _h.NewRun();
        AppState.AddNextRetryFinding(run, Array.Empty<AppRetry>(), "x");
        AppState.AddNextRetryFinding(run, null, "x");
        AppState.AddNextRetryFinding(run, new[]
        {
            new AppRetry { Name = "Contoso App", AppId = "a", NextUtc = _h.Clock.UtcNow.AddHours(5) },
            new AppRetry { Name = "Contoso Tool", AppId = "b", NextUtc = _h.Clock.UtcNow.AddHours(-1) },
        }, "The retry timers were not cleared, because other people are signed in.");
        var f = Assert.Single(_h.Findings("APPS-NEXT-RETRY"));
        Assert.Equal("INFO", f.Str("severity"));
        Assert.StartsWith("The retry timers were not cleared", f.Str("detail"), StringComparison.Ordinal);
        Assert.Contains("Contoso Tool at its next check", f.Str("detail"), StringComparison.Ordinal);
        Assert.Contains("Contoso App around [[t:", f.Str("detail"), StringComparison.Ordinal);
        Assert.Contains("estimates", f.Str("detail"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The support package reads the IME's check-ins with the service's evaluation. A restart:
    /// the agent starts, delays its check-ins, writes an unrelated line with "failed" and a start-up
    /// endpoint error; none of it is a failed check-in, and the start is a row of its own.
    /// </summary>
    [Fact]
    public void The_support_package_sees_a_start_with_its_delay_lines_and_no_failed_check_in()
    {
        var folder = _folder.Combine("restart");
        Directory.CreateDirectory(folder);
        var now = DateTime.UtcNow;
        var start = now.AddMinutes(-10);
        File.WriteAllLines(Path.Combine(folder, "IntuneManagementExtension.log"), new[]
        {
            CmLine("Get policies = [{\"Id\":\"x\"}]", now.AddMinutes(-48), component: "IntuneManagementExtension"),
            CmLine("Intune Management Extension agent starts.", start, component: "IntuneManagementExtension"),
            CmLine("Delaying all checkins by seconds = 51 in the first check in on start/restart.", start.AddSeconds(1), component: "IntuneManagementExtension"),
            CmLine("Delaying PS and Win32 app workload checkins by seconds = 238 in the first check in on start/restart.", start.AddSeconds(1), component: "IntuneManagementExtension"),
            CmLine("[DciHelper] Persisted rate window failed sanity check (stored window starts in the future); failing closed with cap=2", start.AddSeconds(2), type: 2, component: "IntuneManagementExtension"),
            CmLine("[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController, the remote name could not be resolved", start.AddSeconds(3), type: 3, component: "IntuneManagementExtension"),
        });
        _h.Services.Clock = new SystemClock();
        _h.Services.ImePaths.LogsFolder = folder;
        var c = EndpointToolbelt.Engine.Support.PackageSheets.CheckIn(_h.NewRun());
        Assert.True(c.Available);
        Assert.Null(c.LastFailureUtc);
        Assert.Equal(start.AddSeconds(1), c.StartUtc.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(start.AddSeconds(239), c.FirstCheckInDueUtc.Value, TimeSpan.FromSeconds(1));
        var rows = EndpointToolbelt.Engine.Support.PackageSheets.ImeLogSummaryRows(_h.NewRun(), new EndpointToolbelt.Engine.Ime.AppStateReadout());
        Assert.DoesNotContain(rows, r => r.Name == "Failed check-in after it");
        Assert.Contains(rows, r => r.Name == "Last IME start" && r.NextRetryUtc.Length > 0);

        // A real failed check-in after the announced one still counts, with its code.
        File.AppendAllLines(Path.Combine(folder, "IntuneManagementExtension.log"), new[]
        {
            CmLine("[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController with url https://contoso.example, thrown WebException status ProtocolError message The remote server returned an error: (401) Unauthorized.", start.AddSeconds(300), type: 3, component: "IntuneManagementExtension"),
        });
        c = EndpointToolbelt.Engine.Support.PackageSheets.CheckIn(_h.NewRun());
        Assert.Equal("401", c.Error);
        Assert.NotNull(c.LastFailureUtc);
    }
}

public class EngineAppStateTests
{
    private readonly EngineHarness _h = new();

    [Theory]
    [InlineData("installed", 1000, null, 3, 0L, null, "OK")]
    [InlineData("uninstalled", 1000, null, 4, 0L, null, "OK")]
    [InlineData("a dependency success state", 1010, null, 3, 0L, null, "OK")]
    [InlineData("a failure", 5000, null, 3, 2147944003L, null, "RETRY-HELPS")]
    [InlineData("installed but not detected", 5000, null, 3, 2278622236L, null, "PACKAGING")]
    [InlineData("a pending restart", 6002, null, 3, 0L, null, "RESTART-FIRST")]
    [InlineData("an unknown state", 7777, null, 3, 0L, null, "UNKNOWN")]
    [InlineData("an error without a state", null, null, 3, 2147944003L, null, "UNKNOWN")]
    [InlineData("detected without a record", null, 1, 3, 0L, null, "OK")]
    [InlineData("available, not installed", null, 2, 1, 0L, null, "AVAILABLE")]
    [InlineData("a dependency not installed", null, 2, 0, 0L, null, "NO-ACTION")]
    [InlineData("required, never attempted", null, 2, 3, 0L, null, "NO-ACTION")]
    [InlineData("nothing at all", null, null, 3, 0L, null, "NO-STATE")]
    [InlineData("a failure only in the status report", null, null, 3, 0L, "Failed", "PACKAGING")]
    [InlineData("not applicable by the status report", null, null, 3, 0L, "NotApplicable", "NO-ACTION")]
    public void Classifies_app_records(string @case, int? state, int? compliance, int? intent, long code, string reportStatus, string verdict)
    {
        AppStatusReport report = reportStatus switch
        {
            "Failed" => new AppStatusReport { Status = "Failed", ErrorCode = 2278622236 },
            "NotApplicable" => new AppStatusReport { Status = "NotApplicable", Applicability = "RequirementsNotMet" },
            _ => null,
        };
        var r = AppState.Verdict(state, compliance, intent, code, report);
        Assert.True(verdict == r.Verdict, @case + ": " + r.Verdict);
        if (@case == "uninstalled") { Assert.Equal("uninstalled / succeeded", r.State); }
        if (@case == "a failure only in the status report") { Assert.Equal(2278622236, r.Code); }
        if (@case == "not applicable by the status report") { Assert.Equal("not applicable: RequirementsNotMet", r.State); }
    }

    [Fact]
    public void Says_the_installer_exit_code_inside_a_Win32_error_code_as_the_app_does()
    {
        Assert.Equal("exit code 1603: fatal error during installation", AppState.ErrorText(2147944003));
        Assert.Equal("", AppState.ErrorText(0));
        // An uninstall script that ended with exit code 1: not Windows' "Incorrect function" for error 1.
        Assert.Equal("exit code 1", AppState.ErrorText(0x80070001L));
        Assert.Equal(1, EndpointToolbelt.App.Core.AppDecisions.InstallerExitCode("0x80070001"));
        Assert.Equal("exit code 3010: restart required", AppState.ErrorText(0x80070BC2L));
        Assert.Equal("exit code 1618: another installation is running", AppState.ErrorText(0x80070652L));
        // Exit code 2 means what that installer says: the table's entry for it is about Windows Update.
        Assert.Equal("exit code 2", AppState.ErrorText(0x80070002L));
        Assert.Equal("exit code 60008", AppState.ErrorText(0x8007EA68L));
        // The facility is 11 bits (0x7FF): the N and reserved bits above it do not change it.
        Assert.Equal("exit code 1603: fatal error during installation", AppState.ErrorText(2416379459));
        // Facility 0x7D1 (the IME's own range) is not a wrapped Win32 code.
        Assert.False(AppState.ErrorText(2278622236).StartsWith("exit code ", StringComparison.Ordinal));
        // A WinHTTP code is the download's, not an installer's: it keeps Windows' code.
        Assert.StartsWith("code 12002", AppState.ErrorText(0x80072EE2L), StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_detection_and_the_wanted_state_and_an_app_absent_as_assigned_needs_nothing()
    {
        // An uninstall assignment that is not detected: the assignment is met.
        var gone = AppState.Verdict(null, 2, 4, 0, null, desiredValue: 1);
        Assert.Equal("OK", gone.Verdict);
        Assert.Equal("not present, as assigned", gone.State);
        Assert.Equal("OK", AppState.Verdict(null, 2, 4, 0, null).Verdict);
        // Still detected: it waits for its removal.
        Assert.Equal("PENDING", AppState.Verdict(null, 1, 4, 0, null, 1).Verdict);
        // A required app Intune wants absent (a superseded one) and not detected: nothing to do,
        // and not "OK", which for an install assignment means installed and detected.
        var superseded = AppState.Verdict(null, 2, 3, 0, null, 1);
        Assert.Equal("NO-ACTION", superseded.Verdict);
        Assert.StartsWith("not present, as assigned: Intune wants it absent", superseded.State, StringComparison.Ordinal);
        // Wanted present (or not recorded) and never attempted stays the IME's ambiguous record.
        Assert.Equal("not detected and never attempted: requirements not met, or superseded", AppState.Verdict(null, 2, 3, 0, null, 2).State);
        Assert.Equal("not detected and never attempted: requirements not met, or superseded", AppState.Verdict(null, 2, 3, 0, null).State);
        // A dependency keeps its own words; an offered app stays offered.
        Assert.Equal("not installed; a dependency, not assigned itself", AppState.Verdict(null, 2, 0, 0, null, 1).State);
        Assert.Equal("AVAILABLE", AppState.Verdict(null, 2, 1, 0, null, 2).Verdict);

        // Installed answers for an uninstall assignment too.
        Assert.Equal("No", AppState.Installed(null, 2, 4, null));
        Assert.Equal("Yes", AppState.Installed(5000, 1, 4, null));
        Assert.Equal("No", AppState.Installed(1000, null, 4, null));
        Assert.Equal("Yes", AppState.Installed(null, null, 4, new AppStatusReport { Status = "Installed" }));
        Assert.Equal("", AppState.Installed(null, null, 4, null));
        Assert.Equal("Yes", AppState.Installed(1000, 2, 3, null));
        Assert.Equal("No", AppState.Installed(null, null, 3, new AppStatusReport { Status = "Failed" }));
    }

    /// <summary>One app record of one scope with an enforcement and a compliance message, as the IME keeps it.</summary>
    private void PutApp(string scope, string app, int intent, string enforcementJson, string complianceJson)
    {
        var key = @"TEST\Win32Apps\" + scope + @"\" + app + "_1";
        _h.Registry.Put(key, "Intent", intent);
        _h.Registry.Put(key, "LastUpdatedTimeUtc", "11/16/2026 08:00:25");
        if (enforcementJson != null) { _h.Registry.Put(key + @"\EnforcementStateMessage", "EnforcementStateMessage", enforcementJson); }
        if (complianceJson != null) { _h.Registry.Put(key + @"\ComplianceStateMessage", "ComplianceStateMessage", complianceJson); }
    }

    [Fact]
    public void Reads_the_detection_result_and_the_wanted_state_from_the_compliance_message()
    {
        const string user = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        const string removed = "11111111-2222-3333-4444-555555555555";
        const string failing = "22222222-3333-4444-5555-666666666666";
        const string required = "33333333-4444-5555-6666-777777777777";
        PutApp(AppState.DeviceScopeId, removed, 4, null, "{\"Applicability\":0,\"ComplianceState\":2,\"DesiredState\":1,\"ErrorCode\":null}");
        PutApp(AppState.DeviceScopeId, failing, 4, "{\"EnforcementState\":5000,\"ErrorCode\":-2147024895}", "{\"ComplianceState\":1,\"DesiredState\":1,\"ErrorCode\":null}");
        PutApp(user, required, 3, "{\"EnforcementState\":1000,\"ErrorCode\":0}", "{\"ComplianceState\":1,\"DesiredState\":2}");
        var run = _h.NewRun();
        run.CallerSid = EntraSid(new Guid(user));

        var rows = AppState.Read(run).Rows.ToDictionary(r => r.AppId);

        Assert.Equal("OK", rows[removed].Verdict);
        Assert.Equal("not present, as assigned", rows[removed].State);
        Assert.Equal("Not detected", rows[removed].ComplianceState);
        Assert.Equal("Absent", rows[removed].DesiredState);
        Assert.Equal("No", rows[removed].Installed);
        Assert.Equal("RETRY-HELPS", rows[failing].Verdict);
        Assert.Equal("Detected", rows[failing].ComplianceState);
        Assert.Equal("Yes", rows[failing].Installed);
        Assert.Equal("0x80070001", rows[failing].ErrorCode);
        Assert.Equal("exit code 1", rows[failing].ErrorText);
        Assert.Equal("Present", rows[required].DesiredState);
        Assert.Equal("Detected", rows[required].ComplianceState);

        // The support package's table has the wanted state next to the detection result.
        var headers = EndpointToolbelt.Engine.Support.PackageSheets.AppStateHeaders;
        Assert.Equal(Array.IndexOf(headers, "ComplianceState") + 1, Array.IndexOf(headers, "DesiredState"));
        Assert.Equal("Absent", EndpointToolbelt.Engine.Support.PackageSheets.AppStateCells(rows[removed])[Array.IndexOf(headers, "DesiredState")]);
    }

    [Fact]
    public void The_readout_before_an_action_names_an_app_that_already_failed_once_as_information()
    {
        const string user = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        const string app = "22222222-3333-4444-5555-666666666666";
        // The same uninstall assignment failed for the device and for the user, with exit code 1.
        foreach (var scope in new[] { AppState.DeviceScopeId, user })
        {
            PutApp(scope, app, 4, "{\"EnforcementState\":5000,\"ErrorCode\":-2147024895}", "{\"ComplianceState\":1,\"DesiredState\":1}");
            _h.Registry.Put(@"TEST\Win32Apps\" + scope + @"\GRS\hash" + scope.Substring(0, 4), app, "11/16/2026 8:00:25 AM");
        }
        var run = _h.NewRun();
        run.CallerSid = EntraSid(new Guid(user));

        var retries = AppState.Readout(run, "before the refresh");

        // One retry time per app, logged once.
        Assert.Single(retries);
        Assert.Single(_h.Records("progress"), r => r.Str("text").Contains("its own next attempt is due around"));
        // No warning per app and scope: one information finding that names the app once.
        Assert.Empty(_h.Findings("APP-RETRY-HELPS"));
        Assert.DoesNotContain(_h.Records("finding"), f => f.Str("severity") == "WARNING");
        var before = Assert.Single(_h.Findings("APPS-BEFORE"));
        Assert.Equal("INFO", before.Str("severity"));
        Assert.Equal("Before this run, App 22222222 already needed attention", before.Str("title"));
        Assert.Contains("Read before the refresh, so this is the state this run started from, not its result: App 22222222 failed to uninstall on this device (device and user, exit code 1, 0x80070001).", before.Str("detail"), StringComparison.Ordinal);

        // The support package's readout of the same state keeps a finding per record.
        var package = new EngineHarness();
        var state = new AppStateReadout { Present = true, Scopes = 1 };
        state.Rows.Add(new AppStateRow { AppId = app, Name = "Contoso Agent", Verdict = "RETRY-HELPS", ScopeRef = "Device", Intent = "Uninstall", State = "failed with an error", ErrorCode = "0x80070001", ErrorText = "exit code 1" });
        AppState.WriteSummary(package.NewRun(), state, "at collection time", inPackage: true, noLogEvidence: true);
        var finding = Assert.Single(package.Findings("APP-RETRY-HELPS"));
        Assert.Equal("Contoso Agent failed to uninstall on this device", finding.Str("title"));
        Assert.Contains("Error 0x80070001 (exit code 1).", finding.Str("detail"), StringComparison.Ordinal);
    }

    [Fact]
    public void Retry_work_apps_and_settings_ends_on_its_own_steps_whatever_failed_before_it()
    {
        const string app = "22222222-3333-4444-5555-666666666666";
        PutApp(AppState.DeviceScopeId, app, 3, "{\"EnforcementState\":5000,\"ErrorCode\":-2147024895}", null);
        var run = _h.NewRun("retry001", "RetryAppsAndPolicies");
        run.OtherSessions = 0;
        var handler = new EndpointToolbelt.Engine.Actions.RetryAppsAndPoliciesHandler
        {
            Parts = new EndpointToolbelt.Engine.Actions.AppActionParts
            {
                Refresh = (_, _, _) => new RefreshResult { Ok = true, TimersCleared = true },
                Sync = _ => new MdmSyncResult { Status = "VerifiedSuccess", ExitCode = 0, Route = "none", Detail = "" },
                SyncFinding = (_, _) => { },
            },
        };

        Assert.Equal(EngineExitCode.Success, handler.Run(run));
        Assert.Single(_h.Findings("APPS-BEFORE"));
        Assert.DoesNotContain(_h.Records("finding"), f => f.Str("severity") is "WARNING" or "ERROR");
    }

    /// <summary>The SID Windows gives a cloud (Entra) account with this object ID.</summary>
    private static string EntraSid(Guid objectId)
    {
        var b = objectId.ToByteArray();
        return string.Format(CultureInfo.InvariantCulture, "S-1-12-1-{0}-{1}-{2}-{3}", BitConverter.ToUInt32(b, 0), BitConverter.ToUInt32(b, 4), BitConverter.ToUInt32(b, 8), BitConverter.ToUInt32(b, 12));
    }

    [Fact]
    public void Reads_the_newest_revision_per_app_and_shortens_user_scopes()
    {
        const string user = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        const string device = AppState.DeviceScopeId;
        const string app1 = "11111111-2222-3333-4444-555555555555";
        const string app2 = "66666666-7777-8888-9999-aaaaaaaaaaaa";
        var root = @"TEST\Win32Apps\";
        // Revision 9 failed, revision 10 installed: 10 must win, compared as a number.
        _h.Registry.Put(root + user + @"\" + app1 + "_9", "Intent", 3);
        _h.Registry.Put(root + user + @"\" + app1 + @"_9\EnforcementStateMessage", "EnforcementStateMessage", "{\"EnforcementState\":5000,\"ErrorCode\":-2147023293}");
        _h.Registry.Put(root + user + @"\" + app1 + "_10", "Intent", 3);
        _h.Registry.Put(root + user + @"\" + app1 + @"_10\EnforcementStateMessage", "EnforcementStateMessage", "{\"EnforcementState\":1000,\"ErrorCode\":0}");
        _h.Registry.Put(root + device + @"\" + app2 + "_1", "Intent", 3);
        _h.Registry.Put(root + device + @"\" + app2 + @"_1\EnforcementStateMessage", "EnforcementStateMessage", "{\"EnforcementState\":5000,\"ErrorCode\":-2016345060}");
        _h.Registry.Put(root + device + @"\" + app2 + @"_1\ComplianceStateMessage", "ComplianceStateMessage", "not json");
        _h.Registry.AddKey(root + device + @"\GRS");
        _h.Registry.AddKey(root + "Reporting");
        // Another user's app: never read, only counted.
        _h.Registry.Put(root + @"cccccccc-dddd-eeee-ffff-000000000000\" + app2 + "_3", "Intent", 1);
        _h.Registry.Put(root + @"cccccccc-dddd-eeee-ffff-000000000000\" + app2 + "_4", "Intent", 1);
        var sid = EntraSid(new Guid(user));
        Assert.Equal(user, AppState.EntraObjectId(sid));
        Assert.Equal("", AppState.EntraObjectId("S-1-5-21-1-2-3-1001"));
        // Digits of another script (U+0661) are no SID digits.
        Assert.Equal("", AppState.EntraObjectId("S-1-12-1-1-2-3-" + (char)0x0661));

        var run = _h.NewRun();
        run.CallerSid = sid;
        var state = AppState.Read(run);
        Assert.Equal(2, state.Scopes);
        Assert.Equal(1, state.OtherScopes);
        Assert.Equal(1, state.OtherRecords);
        var rows = state.Rows.OrderBy(r => r.AppId, StringComparer.Ordinal).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(10, rows[0].Revision);
        Assert.Equal("OK", rows[0].Verdict);
        Assert.Equal("Yes", rows[0].Installed);
        Assert.Equal("User:aaaaaaaa", rows[0].ScopeRef);
        Assert.Equal("PACKAGING", rows[1].Verdict);
        Assert.Equal("0x87D1041C", rows[1].ErrorCode);
        Assert.Equal("Device", rows[1].ScopeRef);
        Assert.Equal("not json", rows[1].ComplianceJson);

        AppState.WriteSummary(run, state, "in a test", noLogEvidence: true);
        var packaging = _h.Findings("APP-PACKAGING")[0];
        Assert.Contains("Installed, not detected", packaging.Str("detail"), StringComparison.Ordinal);
        // Fixing the package is IT support's step, never the user's.
        Assert.False(string.IsNullOrEmpty(packaging.Str("support")));
        var summary = Assert.Single(_h.Records("apps-state"));
        Assert.Equal("2", summary.Str("records"));
        Assert.Equal("1", summary.Str("packaging"));
    }

    [Fact]
    public void Keeps_Intune_work_out_of_the_users_next_step_and_in_the_support_step()
    {
        var row = new AppStateRow { AppId = "11111111-2222-3333-4444-555555555555", Name = "Viewer", ScopeRef = "Device", Intent = "Required", Verdict = "NOT-APPLICABLE", State = "not applicable" };
        var count = new Dictionary<string, int> { ["OK"] = 0, ["AVAILABLE"] = 0, ["NO-ACTION"] = 0, ["IN-PROGRESS"] = 0, ["PENDING"] = 0 };
        AppState.AddFindings(_h.NewRun(), new[] { row }, new[] { row }, count, "in a test");
        var finding = _h.Findings("APP-NOT-APPLICABLE")[0];
        Assert.Contains("contact your IT support", finding.Str("next"), StringComparison.Ordinal);
        Assert.DoesNotContain("Intune", finding.Str("next"), StringComparison.Ordinal);
        Assert.Contains("requirement rules", finding.Str("support"), StringComparison.Ordinal);
    }

    [Fact]
    public void Names_apps_from_the_newest_policy_line_in_the_IME_logs()
    {
        using var folder = new TestFolder();
        const string id = "11111111-2222-3333-4444-555555555555";
        var old = "<![LOG[Get policies = [{\"Id\":\"" + id + "\",\"Name\":\"Old name\",\"Other\":1}]]LOG]!>";
        var @new = "<![LOG[Get policies = [{\"Id\":\"" + id + "\",\"Name\":\"Viewer & Tools\",\"Other\":1}]]LOG]!>";
        File.WriteAllLines(folder.Combine("AppWorkload.log"), new[] { "noise", old, @new });
        _h.Services.ImePaths.LogsFolder = folder.Path;
        var map = AppState.NameMap(_h.NewRun(), new[] { id.ToUpperInvariant(), "99999999-9999-9999-9999-999999999999" });
        Assert.Equal("Viewer & Tools", map[id]);
        Assert.Single(map);
    }

    [Fact]
    public void Never_stops_an_action_when_the_readout_fails()
    {
        _h.Registry.AddKey(@"TEST\Win32Apps");
        _h.Registry.Unreadable.Add(@"TEST\Win32Apps");
        var retries = AppState.Readout(_h.NewRun(), "before");
        Assert.Empty(retries);
        Assert.Single(_h.Records("progress"), r => r.Str("severity") == "warning");
    }

    [Fact]
    public void Explains_codes_from_one_shared_table()
    {
        Assert.Equal("CBS_E_SOURCE_MISSING", CodeKnowledge.Get("0x800F081F").Name);
        Assert.Equal("0x800f081f", CodeKnowledge.Get("-2146498529").Code);
        Assert.Equal("0x800f081f", CodeKnowledge.Get("2148468767").Code);
        Assert.Null(CodeKnowledge.Get("0x12345678"));
        Assert.Null(CodeKnowledge.Get("result 4"));
        Assert.Equal("", CodeKnowledge.Format("0x12345678"));
        Assert.StartsWith(" Code 0x801901ad (HTTP 429", CodeKnowledge.Format("0x801901AD"), StringComparison.Ordinal);
        Assert.Equal("0x87D1041C", CodeKnowledge.FormatHex("-2016345060"));
        Assert.All(CodeKnowledge.Table.Keys, k => Assert.Matches("^0x[0-9a-f]{8}$", k));
    }
}
