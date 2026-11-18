using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Platform;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The compiled engine's run frame: records and logging, state and cooldowns, and persistent
/// log records.
/// </summary>
public class EngineRecordTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly EngineHarness _h = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Escapes_JSON_strings_and_never_breaks_a_line()
    {
        const string B = "\\";
        Assert.Equal("\"a" + B + "\"b" + B + B + " c" + B + "r" + B + "n" + "d" + B + "t\"", RecordJson.Quote("a\"b" + B + " c\r\nd\t"));
        Assert.Equal("\"\\u2028\"", RecordJson.Quote(((char)0x2028).ToString()));
        Assert.Equal("\"\\u0001\"", RecordJson.Quote(((char)1).ToString()));
        Assert.Equal("null", RecordJson.Quote(null));
        // Records are plain ASCII whatever the console encoding is.
        Assert.Equal("\"caf\\u00e9\\u20ac\\u007f\"", RecordJson.Quote("caf" + (char)0xE9 + (char)0x20AC + (char)0x7F));
    }

    [Fact]
    public void Logs_tool_exit_codes_in_decimal_up_to_99999_and_as_unsigned_hex_above()
    {
        Assert.Equal("0", TextFormat.ExitCode(0));
        Assert.Equal("87", TextFormat.ExitCode(87));
        Assert.Equal("3010", TextFormat.ExitCode(3010));
        Assert.Equal("99999", TextFormat.ExitCode(99999));
        Assert.Equal("0x000186A0", TextFormat.ExitCode(100000));
        Assert.Equal("0x800F081F", TextFormat.ExitCode(-2146498529));
        Assert.Equal("0x800F081F", TextFormat.ExitCode(2148468767));
        Assert.Equal("0xFFFFFFFF", TextFormat.ExitCode(-1));

        // A harmless real process: cmd.exe only sets its exit code.
        _h.Services.Processes = new ProcessRunner();
        var run = _h.NewRun();
        var result = run.RunTool(new ProcessRequest { FilePath = ProcessRunner.SystemTool("cmd.exe"), Arguments = "/d /c exit 3010", Label = "Exit test", TimeoutMinutes = 1 });
        Assert.Equal(3010, result.ExitCode);
        Assert.Single(_h.Records("progress"), r => Regex.IsMatch(r.Str("text"), @"^Exit test finished in .*, exit 3010\.$"));
    }

    [Fact]
    public void Formats_numbers_the_same_on_every_locale()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
        try
        {
            Assert.Equal("12.5", TextFormat.Number(12.5));
            Assert.Equal("1235", TextFormat.Number(1234.6, "0"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Reports_a_log_that_cannot_be_written_once_and_never_stops_the_run()
    {
        var run = _h.NewRun("log2");
        run.LogDirectory = _folder.Combine("missing", "deeper");
        for (var i = 0; i < 5; i++) { run.PersistentLog("line " + i); }
        Assert.Empty(_h.Clock.Sleeps);
        Assert.Single(_h.Records("warning"));
        Assert.True(run.LogFailed);
    }

    [Fact]
    public void Writes_valid_one_line_records_that_carry_the_run_id()
    {
        var run = _h.NewRun("abc123");
        run.AddFinding(FindingLevel.Warning, "T-1", "Title\nwith break", "Quote \" and \\ backslash", "Next");
        run.AddChange("Changed something");
        var records = _h.Records();
        Assert.Equal(2, records.Count);
        Assert.Equal("finding", records[0].Str("type"));
        Assert.Equal("abc123", records[0].Str("run"));
        Assert.Equal("WARNING", records[0].Str("severity"));
        Assert.Equal("Title with break", records[0].Str("title"));
        Assert.Equal("Quote \" and \\ backslash", records[0].Str("detail"));
        Assert.Equal("change", records[1].Str("type"));
        Assert.All(_h.Lines, line => Assert.DoesNotContain("\n", line));
    }

    [Fact]
    public void Stamps_every_record_type_with_the_UTC_moment_in_milliseconds_on_every_locale()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        // A culture with another calendar: the stamp must not follow it.
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
        _h.Services.Clock = new SystemClock();
        try
        {
            var run = _h.NewRun("ts1");
            run.Log("progress line");
            run.AddFinding(FindingLevel.Info, "T-TS", "title");
            run.AddChange("changed");
            run.WriteRecord("restart-required");
            run.WriteRecord("warning");
            RunFrame.WriteSummary(run, 0);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
        var types = _h.Records().Select(r => r.Str("type")).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "change", "finding", "progress", "restart-required", "summary", "warning" }, types);
        var now = DateTime.UtcNow;
        foreach (var line in _h.Lines)
        {
            var match = Regex.Match(line, "^\\{\"type\":\"[a-z-]+\",\"run\":\"ts1\",\"ts\":\"(?<ts>\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z)\"");
            Assert.True(match.Success, line);
            var parsed = DateTime.ParseExact(match.Groups["ts"].Value, "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            Assert.True(Math.Abs((now - parsed).TotalMinutes) < 2, line);
        }
    }

    [Fact]
    public void Writes_the_support_step_after_the_next_step_in_the_record_the_persistent_log_and_the_finding()
    {
        var run = _h.NewRun("sup1");
        run.LogDirectory = _folder.Path;
        run.AddFinding(FindingLevel.Warning, "T-SUP", "Title", "Detail", "Contact your IT support.", "Check the assignment\r\nin Intune | now");
        run.AddFinding(FindingLevel.Info, "T-NOSUP", "Title", nextStep: "Nothing to do.");
        Assert.Equal("Check the assignment in Intune | now", run.Findings[0].Support);
        Assert.Equal("", run.Findings[1].Support);
        Assert.EndsWith("\"next\":\"Contact your IT support.\",\"support\":\"Check the assignment in Intune | now\"}", _h.Lines[0], StringComparison.Ordinal);
        Assert.EndsWith("\"next\":\"Nothing to do.\",\"support\":\"\"}", _h.Lines[1], StringComparison.Ordinal);
        var log = File.ReadAllText(_folder.Combine("EndpointToolbelt.log"));
        Assert.Contains("id=T-SUP | Title | Detail | Contact your IT support. | support: Check the assignment in Intune / now]LOG]", log, StringComparison.Ordinal);
        Assert.Contains("id=T-NOSUP | Title |  | Nothing to do.]LOG]", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Counts_with_the_right_noun_and_writes_step_headers_of_one_shape()
    {
        Assert.Equal("1 profile", TextFormat.Count(1, "profile"));
        Assert.Equal("0 profiles", TextFormat.Count(0, "profile"));
        Assert.Equal("1200 processes", TextFormat.Count(1200, "process", "processes"));
        _h.NewRun().StepHeader(2, 4, "Restart the app installer (IME)");
        Assert.Equal("== Step 2/4: Restart the app installer (IME) ==", _h.Records()[0].Str("text"));
    }

    [Fact]
    public void Writes_time_tokens_in_UTC()
    {
        var when = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("[[t:2026-10-15T12:00:00Z;HH:mm]]", RunContext.UserTime(when));
    }

    [Fact]
    public void Formats_CMTrace_lines_with_the_bias_convention()
    {
        var line = CmTraceFile.FormatLine("x", 2, "Engine.Test", new DateTimeOffset(2026, 10, 15, 14, 30, 0, TimeSpan.FromHours(2)), 7);
        Assert.Contains("time=\"14:30:00.000-120\"", line, StringComparison.Ordinal);
        Assert.Contains("date=\"10-15-2026\"", line, StringComparison.Ordinal);
        Assert.Contains("type=\"2\"", line, StringComparison.Ordinal);
        Assert.Contains("component=\"Engine.Test\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Writes_the_persistent_log_with_one_BOM_and_rolls_it_over()
    {
        var run = _h.NewRun("log1", "SyncClock");
        run.LogDirectory = _folder.Path;
        run.LogMaxBytes = 600;
        for (var i = 0; i < 10; i++) { run.PersistentLog("line " + i); }
        Assert.True(File.Exists(_folder.Combine("EndpointToolbelt.lo_")));
        foreach (var name in new[] { "EndpointToolbelt.log", "EndpointToolbelt.lo_" })
        {
            var bytes = File.ReadAllBytes(_folder.Combine(name));
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
            // Exactly one BOM: no second one further on.
            Assert.Single(Regex.Matches(BitConverter.ToString(bytes), "EF-BB-BF").Cast<Match>());
        }
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(_folder.Combine("EndpointToolbelt.log")));
        Assert.Contains("line 9", text, StringComparison.Ordinal);
        Assert.Contains("[log1] line 9]LOG]!>", text, StringComparison.Ordinal);
        Assert.Contains("component=\"Engine.SyncClock\"", text, StringComparison.Ordinal);
        Assert.Contains("thread=\"" + _h.Services.ProcessId + "\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_text_from_outside_on_one_record_no_line_breaks_no_forged_record_markers()
    {
        var forged = "real text\r\n<![LOG[EndpointToolbelt:SUMMARY exit=0]LOG]!><time=\"10:00:00.000-120\" date=\"10-16-2026\" component=\"x\" context=\"\" type=\"1\" thread=\"1\" file=\"\">" + (char)0x2028 + "tail";
        var line = CmTraceFile.FormatLine(forged, 1, "Engine.Test", DateTimeOffset.Now, 1);
        Assert.DoesNotMatch("[\r\n]", line);
        Assert.Single(Regex.Matches(line, Regex.Escape("<![LOG[")).Cast<Match>());
        Assert.Single(Regex.Matches(line, Regex.Escape("]LOG]!>")).Cast<Match>());
        Assert.Contains("real text <! [LOG[EndpointToolbelt:SUMMARY", line, StringComparison.Ordinal);
        Assert.Equal("", CmTraceFile.CleanText(null));
    }

    [Fact]
    public void Gives_the_log_file_and_an_older_rolled_over_file_a_protected_ACL_without_the_Users_group()
    {
        File.WriteAllText(_folder.Combine("EndpointToolbelt.lo_"), "older version");
        var run = _h.NewRun("acl00001");
        run.LogDirectory = _folder.Path;
        run.PersistentLog("first line");
        run.PersistentLog("second line");
        using var identity = WindowsIdentity.GetCurrent();
        var allowed = new[] { "S-1-5-18", "S-1-5-32-544", identity.User.Value };
        foreach (var name in new[] { "EndpointToolbelt.log", "EndpointToolbelt.lo_" })
        {
            var acl = File.GetAccessControl(_folder.Combine(name));
            Assert.True(acl.AreAccessRulesProtected, name);
            var sids = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value);
            Assert.All(sids, sid => Assert.Contains(sid, allowed));
        }
        Assert.Contains("second line", File.ReadAllText(_folder.Combine("EndpointToolbelt.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void Does_not_protect_a_log_file_that_is_a_link()
    {
        var real = _folder.Combine("realdir");
        Directory.CreateDirectory(real);
        var link = _folder.Combine("linkdir");
        TestFolder.CreateJunction(link, real);
        Assert.False(FileSystemSafety.ProtectLogFile(link));
        Assert.False(FileSystemSafety.ProtectLogFile(Path.Combine(link, "missing.log")));
        Assert.False(Directory.GetAccessControl(real).AreAccessRulesProtected);
        Directory.Delete(link);
    }

    [Fact]
    public void Refuses_to_write_its_log_through_a_hard_link()
    {
        // The compiled engine opens its log by handle, as the service does.
        var target = _folder.Combine("elsewhere.txt");
        File.WriteAllText(target, "not a log");
        Assert.True(CreateHardLink(_folder.Combine("EndpointToolbelt.log"), target, IntPtr.Zero));
        var run = _h.NewRun("hl000001");
        run.LogDirectory = _folder.Path;
        run.PersistentLog("must not arrive");
        Assert.Equal("not a log", File.ReadAllText(target));
        Assert.Single(_h.Records("warning"));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [Fact]
    public void Finds_lines_in_a_bounded_way_the_end_of_a_large_file_and_the_newest_matches_only()
    {
        var path = _folder.Combine("bounded.log");
        var lines = Enumerable.Range(1, 2000).Select(i => string.Format(CultureInfo.InvariantCulture, "line {0:D4} {1}", i, i % 2 == 1 ? "match" : "other")).ToArray();
        File.WriteAllLines(path, lines, new UTF8Encoding(true));
        var all = SafeFileReader.FindLines(path, "MATCH");
        Assert.Equal(500, all.Count);
        Assert.Equal("line 1999 match", all[all.Count - 1]);
        Assert.Equal("line 1001 match", all[0]);
        // Only the last 1000 bytes: the first, cut line there is skipped, the rest is whole.
        var tail = SafeFileReader.FindLines(path, "match", 1000, 10000);
        Assert.InRange(tail.Count, 26, 31);
        Assert.All(tail, l => Assert.Matches(@"^line \d{4} match$", l));
        Assert.Equal("line 1999 match", tail[tail.Count - 1]);
        // A small file is read from the start, BOM included.
        Assert.Equal("line 0001 match", SafeFileReader.FindLines(path, "line 0001 ", maxLines: 1)[0]);
        // The tail of a file skips the BOM only at the start of the file.
        Assert.StartsWith("line 0001", SafeFileReader.ReadTail(path, 1 << 20), StringComparison.Ordinal);
        Assert.Null(SafeFileReader.ReadTail(_folder.Combine("none.log"), 100));
    }
}

public class EngineRunFrameTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly EngineHarness _h = new();

    public void Dispose() => _folder.Dispose();

    private static Dictionary<string, IActionHandler> Handlers(params (string Name, Func<RunContext, int?> Run)[] handlers) =>
        handlers.ToDictionary(h => h.Name, h => (IActionHandler)new DelegateHandler(h.Run), StringComparer.Ordinal);

    [Fact]
    public void Rejects_an_unknown_action_a_wrong_case_and_a_wrong_contract_version_with_exit_87()
    {
        var handlers = Handlers(("FullReset", _ => throw new InvalidOperationException("must not run")), ("ClearAppCache", _ => throw new InvalidOperationException("must not run")));
        var file = EngineHarness.WriteRequest(_folder, "req.json", "{\"contractVersion\":1,\"runId\":\"r1\",\"action\":\"FullReset\",\"config\":{}}");
        Assert.Equal(87, RunFrame.Run("Unknown", file, handlers, _h.Services));
        Assert.Equal(87, RunFrame.Run("fullreset", file, handlers, _h.Services));
        Assert.Equal(87, RunFrame.Run("ClearAppCache", file, handlers, _h.Services));
        file = EngineHarness.WriteRequest(_folder, "req.json", "{\"contractVersion\":2,\"runId\":\"r1\",\"action\":\"FullReset\",\"config\":{}}");
        Assert.Equal(87, RunFrame.Run("FullReset", file, handlers, _h.Services));
        Assert.Equal(87, RunFrame.Run("FullReset", @"C:\does\not\exist.json", handlers, _h.Services));
        var summaries = _h.Records("summary");
        Assert.Equal(5, summaries.Count);
        Assert.All(summaries, s => Assert.Equal("87", s.Str("exit")));
        Assert.Equal(5, _h.Findings("ENGINE-INVALID-REQUEST").Count);
        Assert.Contains(_h.Records("progress"), r => r.Str("text") == "Invalid request for action \"fullreset\"; nothing was run." && r.Str("severity") == "error");
    }

    [Fact]
    public void Exits_87_for_a_validated_action_without_a_handler()
    {
        // A validated action that has no handler in this handler set ends with 87.
        var file = EngineHarness.WriteRequest(_folder, "req.json", "{\"contractVersion\":1,\"runId\":\"r1b\",\"action\":\"SyncClock\",\"config\":{}}");
        Assert.Equal(87, RunFrame.Run("SyncClock", file, Handlers(), _h.Services));
        Assert.Single(_h.Findings("ENGINE-INVALID-REQUEST"));
        Assert.All(_h.Records(), r => Assert.Equal("r1b", r.Str("run")));
    }

    [Fact]
    public void Turns_a_handler_without_an_exit_code_into_1_never_87_and_a_throwing_handler_into_1()
    {
        var file = EngineHarness.WriteRequest(_folder, "req2.json", "{\"contractVersion\":1,\"runId\":\"r2\",\"action\":\"ClearAppCache\",\"otherSessions\":0,\"config\":{}}");
        Assert.Equal(1, RunFrame.Run("ClearAppCache", file, Handlers(("ClearAppCache", _ => null)), _h.Services));
        Assert.Equal(1, RunFrame.Run("ClearAppCache", file, Handlers(("ClearAppCache", _ => throw new InvalidOperationException("boom"))), _h.Services));
        Assert.Single(_h.Findings("ENGINE-NO-RESULT"));
        var error = Assert.Single(_h.Findings("ENGINE-ERROR"));
        Assert.Equal("The action stopped with an unexpected error", error.Str("title"));
        Assert.Equal("boom", error.Str("detail"));
        Assert.Contains(_h.Records("progress"), r => r.Str("text") == "The handler for ClearAppCache returned 0 exit codes instead of one.");
        Assert.All(_h.Records("summary"), s => Assert.Equal("1", s.Str("exit")));
    }

    [Fact]
    public void Treats_malformed_numbers_in_the_request_as_invalid_or_unknown_never_as_a_crash()
    {
        var seen = new List<int>();
        var handlers = Handlers(("ClearAppCache", run => { seen.Add(run.OtherSessions); return 0; }));
        var file = EngineHarness.WriteRequest(_folder, "req4.json", "{\"contractVersion\":\"one\",\"runId\":\"r4\",\"action\":\"ClearAppCache\",\"otherSessions\":\"x\",\"config\":{}}");
        Assert.Equal(87, RunFrame.Run("ClearAppCache", file, handlers, _h.Services));
        file = EngineHarness.WriteRequest(_folder, "req4.json", "{\"contractVersion\":99999999999,\"runId\":\"r4\",\"action\":\"ClearAppCache\",\"config\":{}}");
        Assert.Equal(87, RunFrame.Run("ClearAppCache", file, handlers, _h.Services));
        file = EngineHarness.WriteRequest(_folder, "req4.json", "{\"contractVersion\":1,\"runId\":\"r4\",\"action\":\"ClearAppCache\",\"otherSessions\":99999999999,\"config\":{}}");
        Assert.Equal(0, RunFrame.Run("ClearAppCache", file, handlers, _h.Services));
        Assert.Equal(new[] { -1 }, seen);
        Assert.Equal(3, _h.Records("summary").Count);
    }

    [Fact]
    public void Reads_the_request_fields_as_the_PowerShell_engine_did()
    {
        RunContext seen = null;
        var handlers = Handlers(("CollectDiag", run => { seen = run; return 0; }));
        // A number written as text still counts (PowerShell converts it), a bad SID is dropped,
        // a negative time limit becomes 0, and names match regardless of case.
        var file = EngineHarness.WriteRequest(_folder, "req7.json",
            "{\"ContractVersion\":\"1\",\"runId\":\"r7\",\"action\":\"CollectDiag\",\"productVersion\":\"1.2.3\",\"outputDirectory\":\"out\"," +
            "\"logDirectory\":\"\",\"callerSessionId\":\"3\",\"otherSessions\":2,\"callerSid\":\"S-1-5-21-1-2-3-1001-X\",\"timeLimitMinutes\":-5," +
            "\"config\":{\"fullResetCooldownHours\":0,\"wuRemoveStalePackages\":true}}");
        Assert.Equal(0, RunFrame.Run("CollectDiag", file, handlers, _h.Services));
        Assert.Equal("r7", seen.RunId);
        Assert.Equal("1.2.3", seen.ProductVersion);
        Assert.Equal("out", seen.OutputDirectory);
        Assert.Equal(3, seen.CallerSessionId);
        Assert.Equal(2, seen.OtherSessions);
        Assert.Equal("", seen.CallerSid);
        Assert.Equal(0, seen.TimeLimitMinutes);
        Assert.Equal(0, seen.Config.FullResetCooldownHours);
        Assert.True(seen.Config.WURemoveStalePackages);
        var progress = _h.Records("progress").Select(r => r.Str("text")).ToList();
        Assert.Equal("Endpoint Toolbelt 1.2.3 engine started. Action: CollectDiag. Run r7.", progress[0]);
        Assert.Matches(@"^Device: .*; Windows 11 Enterprise 24H2, build 26100\.4061; edition Enterprise\.$", progress[1]);
        // No line about the engine's clock: the app shows the times in the user's own zone, and the
        // engine's log file has the offset on every line.
        Assert.DoesNotContain(progress, p => p.StartsWith("Clock:", StringComparison.Ordinal));
        Assert.Equal("Other signed-in user sessions: 2.", progress[2]);
        Assert.Equal("Nothing on this device was changed by this run.", progress[progress.Count - 1]);
    }

    [Fact]
    public void Accepts_a_caller_SID_with_ASCII_digits_only()
    {
        var seen = new List<string>();
        var handlers = Handlers(("ClearAppCache", run => { seen.Add(run.CallerSid); return 0; }));
        // An Arabic-Indic one (U+0661), written as a JSON escape: \d would accept it.
        var file = EngineHarness.WriteRequest(_folder, "req10.json", "{\"contractVersion\":1,\"runId\":\"r10\",\"action\":\"ClearAppCache\",\"callerSid\":\"S-1-5-21-1-2-3-100" + "\\" + "u0661\"}");
        Assert.Equal(0, RunFrame.Run("ClearAppCache", file, handlers, _h.Services));
        file = EngineHarness.WriteRequest(_folder, "req10.json", "{\"contractVersion\":1,\"runId\":\"r10\",\"action\":\"ClearAppCache\",\"callerSid\":\"S-1-5-21-1-2-3-1001\"}");
        Assert.Equal(0, RunFrame.Run("ClearAppCache", file, handlers, _h.Services));
        Assert.Equal(new[] { "", "S-1-5-21-1-2-3-1001" }, seen);
    }

    [Fact]
    public void Uses_invalid0_as_the_run_id_when_the_request_has_none_that_is_safe()
    {
        var file = EngineHarness.WriteRequest(_folder, "req8.json", "{\"contractVersion\":1,\"runId\":\"r 8\",\"action\":\"SyncClock\"}");
        Assert.Equal(87, RunFrame.Run("SyncClock", file, Handlers(), _h.Services));
        Assert.All(_h.Records(), r => Assert.Equal("invalid0", r.Str("run")));
    }

    [Fact]
    public void Ends_with_a_summary_and_exit_1_when_the_frame_itself_fails()
    {
        var file = EngineHarness.WriteRequest(_folder, "req5.json", "{\"contractVersion\":1,\"runId\":\"r5\",\"action\":\"ClearAppCache\",\"config\":{}}");
        _h.Services.ReadOsVersion = _ => throw new InvalidOperationException("no OS info");
        Assert.Equal(1, RunFrame.Run("ClearAppCache", file, Handlers(("ClearAppCache", _ => 0)), _h.Services));
        var error = Assert.Single(_h.Findings("ENGINE-ERROR"));
        Assert.Equal("The repair engine stopped with an unexpected error", error.Str("title"));
        Assert.Equal("r5", error.Str("run"));
        Assert.Equal("summary", _h.Records().Last().Str("type"));
        Assert.Equal("1", _h.Records().Last().Str("exit"));
    }

    [Fact]
    public void Writes_the_summary_with_counts_and_the_marker_line()
    {
        var run = _h.NewRun("sum1");
        run.LogDirectory = _folder.Path;
        run.AddFinding(FindingLevel.Error, "A-1", "a");
        run.AddFinding(FindingLevel.Warning, "B-2", "b");
        run.AddFinding(FindingLevel.Ok, "C-3", "c");
        run.AddChange("changed");
        run.State.MarkRestartRequired("Test");
        RunFrame.WriteSummary(run, 2);
        var summary = _h.Records("summary").Single();
        Assert.EndsWith(",\"exit\":2,\"findings\":3,\"errors\":1,\"warnings\":1,\"changes\":1,\"restart\":true}", _h.Lines.Last(), StringComparison.Ordinal);
        Assert.Equal("OK", _h.Findings("C-3")[0].Str("severity"));
        Assert.Empty(_h.Records("progress"));
        Assert.Contains("EndpointToolbelt:SUMMARY exit=2 findings=3 errors=1 warnings=1 changes=1 restart=yes ids=A-1,B-2,C-3", File.ReadAllText(_folder.Combine("EndpointToolbelt.log")), StringComparison.Ordinal);
        Assert.NotNull(summary);
    }
}

public class EngineStateTests
{
    private readonly EngineHarness _h = new();

    [Fact]
    public void Allows_blocks_and_fails_open_as_documented()
    {
        var run = _h.NewRun();
        var now = DateTime.UtcNow;
        Assert.True(run.State.CooldownAllows("LastFullReset", 4, "Full IME reset", now));
        _h.Registry.Put(StateStore.Key, "LastFullReset", StateStore.ToStateTime(new DateTimeOffset(now.AddHours(-1))));
        Assert.False(run.State.CooldownAllows("LastFullReset", 4, "Full IME reset", now));
        _h.Registry.Put(StateStore.Key, "LastFullReset", StateStore.ToStateTime(new DateTimeOffset(now.AddHours(-5))));
        Assert.True(run.State.CooldownAllows("LastFullReset", 4, "Full IME reset", now));
        _h.Registry.Put(StateStore.Key, "LastFullReset", "not a time");
        Assert.True(run.State.CooldownAllows("LastFullReset", 4, "Full IME reset", now));
        // 0 hours: the administrator switched the cooldown off.
        _h.Registry.Put(StateStore.Key, "LastFullReset", StateStore.ToStateTime(new DateTimeOffset(now.AddMinutes(-1))));
        Assert.True(run.State.CooldownAllows("LastFullReset", 0, "Full IME reset", now));
        var cooldown = Assert.Single(_h.Findings("COOLDOWN"));
        Assert.Equal("INFO", cooldown.Str("severity"));
        Assert.Equal("Full IME reset was skipped: it ran recently", cooldown.Str("title"));
        Assert.Matches(@"^Last run that changed this device: \[\[t:[0-9T:-]+Z;yyyy-MM-dd HH:mm\]\]\. The 4-hour cooldown keeps a device from being put through it again and again\.$", cooldown.Str("detail"));
        Assert.Matches(@"^It can run again from about \[\[t:[0-9T:-]+Z;yyyy-MM-dd HH:mm\]\]\. Nothing on this device was changed now\.$", cooldown.Str("next"));
        Assert.Contains(_h.Records("progress"), r => r.Str("text") == "The time of the last Full IME reset could not be read; the cooldown is not applied." && r.Str("severity") == "warning");
        Assert.Contains(_h.Records("progress"), r => Regex.IsMatch(r.Str("text"), @"^Full IME reset skipped: the last run that changed this device was at \[\[t:.*\]\]; cooldown 4 hours\.$"));
    }

    [Fact]
    public void Stamps_a_cooldown_provisionally_and_puts_the_old_value_back_or_removes_it_when_there_was_none()
    {
        var run = _h.NewRun();
        _h.Registry.Put(StateStore.Key, "LastWURepair", "2026-10-15T08:00:00.0000000+02:00");
        Assert.Equal("2026-10-15T08:00:00.0000000+02:00", run.State.StampProvisionally("LastWURepair"));
        var stamped = (string)_h.Registry.GetValue(StateStore.Key, "LastWURepair");
        Assert.NotNull(StateStore.FromStateTime(stamped));
        Assert.NotEqual("2026-10-15T08:00:00.0000000+02:00", stamped);
        run.State.Restore("LastWURepair", "2026-10-15T08:00:00.0000000+02:00");
        Assert.Equal("2026-10-15T08:00:00.0000000+02:00", _h.Registry.GetValue(StateStore.Key, "LastWURepair"));
        run.State.Restore("LastWURepair", null);
        Assert.Null(_h.Registry.GetValue(StateStore.Key, "LastWURepair"));
        // Removing a value that is not there is logged, as Remove-ItemProperty failed before.
        run.State.Restore("LastWURepair", null);
        Assert.Single(_h.Records("progress"), r => r.Str("text").StartsWith("Could not remove state value LastWURepair: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Logs_a_state_value_it_cannot_write_and_goes_on()
    {
        var run = _h.NewRun();
        _h.Registry.FailWrites = true;
        Assert.False(run.State.Set("LastFullReset", "x"));
        Assert.Single(_h.Records("progress"), r => r.Str("severity") == "warning" && r.Str("text") == "Could not write state value LastFullReset: Access to the registry key is denied.");
    }

    [Fact]
    public void Marks_a_restart_and_reads_the_servicing_restart_flags()
    {
        var run = _h.NewRun("rr1");
        Assert.False(run.State.PendingRestart());
        run.State.MarkRestartRequired("RepairWU");
        Assert.True(run.RestartRequired);
        Assert.Equal("RepairWU", _h.Registry.GetValue(StateStore.Key, "RestartRequiredBy"));
        Assert.NotNull(StateStore.FromStateTime((string)_h.Registry.GetValue(StateStore.Key, "RestartRequiredSince")));
        Assert.Matches("^\\{\"type\":\"restart-required\",\"run\":\"rr1\",\"ts\":\"[^\"]+\"\\}$", Assert.Single(_h.Lines));
        _h.Registry.AddKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        Assert.True(run.State.PendingRestart());
    }

    [Fact]
    public void Fills_missing_or_invalid_config_values_with_the_defaults_only_a_present_0_switches_a_cooldown_off()
    {
        var empty = EngineSettings.Resolve(JsonNode.Parse("{}"));
        Assert.Equal(1, empty.FullResetCooldownHours);
        Assert.Equal(4, empty.WURepairCooldownHours);
        Assert.False(empty.WURemoveStalePackages);
        var off = EngineSettings.Resolve(JsonNode.Parse("{\"fullResetCooldownHours\":0,\"wuRepairCooldownHours\":0}"));
        Assert.Equal(0, off.FullResetCooldownHours);
        Assert.Equal(0, off.WURepairCooldownHours);
        var bad = EngineSettings.Resolve(JsonNode.Parse("{\"fullResetCooldownHours\":-1,\"wuRepairCooldownHours\":-3,\"wuMinFreeGB\":\"20\",\"wuRemoveStalePackages\":1}"));
        Assert.Equal(1, bad.FullResetCooldownHours);
        Assert.Equal(4, bad.WURepairCooldownHours);
        Assert.Equal(15, bad.WUMinFreeGB);
        Assert.False(bad.WURemoveStalePackages);
        var good = EngineSettings.Resolve(JsonNode.Parse("{\"fullResetCooldownHours\":8,\"wuRemoveStalePackages\":true}"));
        Assert.Equal(8, good.FullResetCooldownHours);
        Assert.True(good.WURemoveStalePackages);
        Assert.Equal(10, EngineSettings.Resolve(null).WUDeploymentDelayDays);
    }

    [Fact]
    public void Falls_back_to_the_default_above_the_per_key_maximum_and_for_out_of_range_double_or_string_values()
    {
        var atMax = EngineSettings.Resolve(JsonNode.Parse("{\"fullResetCooldownHours\":168,\"wuRepairCooldownHours\":720,\"wuDeploymentDelayDays\":60,\"wuMinFreeGB\":500}"));
        Assert.Equal(168, atMax.FullResetCooldownHours);
        Assert.Equal(720, atMax.WURepairCooldownHours);
        Assert.Equal(60, atMax.WUDeploymentDelayDays);
        Assert.Equal(500, atMax.WUMinFreeGB);
        var over = EngineSettings.Resolve(JsonNode.Parse("{\"fullResetCooldownHours\":169,\"wuRepairCooldownHours\":100000,\"wuDeploymentDelayDays\":61,\"wuMinFreeGB\":501}"));
        Assert.Equal(1, over.FullResetCooldownHours);
        Assert.Equal(4, over.WURepairCooldownHours);
        Assert.Equal(10, over.WUDeploymentDelayDays);
        Assert.Equal(15, over.WUMinFreeGB);
        // An Int64 past Int32.MaxValue must never overflow the cast; it falls back.
        var huge = EngineSettings.Resolve(JsonNode.Parse("{\"wuMinFreeGB\":9999999999,\"fullResetCooldownHours\":9999999999}"));
        Assert.Equal(15, huge.WUMinFreeGB);
        Assert.Equal(1, huge.FullResetCooldownHours);
        // A double or a string is not a whole number: default.
        var wrong = EngineSettings.Resolve(JsonNode.Parse("{\"fullResetCooldownHours\":2.5,\"wuMinFreeGB\":\"20\"}"));
        Assert.Equal(1, wrong.FullResetCooldownHours);
        Assert.Equal(15, wrong.WUMinFreeGB);
    }

    [Fact]
    public void Stores_times_with_their_offset_and_reads_them_back_as_UTC()
    {
        var text = StateStore.ToStateTime(new DateTimeOffset(2026, 10, 15, 14, 0, 0, TimeSpan.FromHours(2)));
        Assert.Matches(@"[+-]\d\d:\d\d$", text);
        Assert.Equal(new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc), StateStore.FromStateTime("2026-10-15T14:00:00+02:00"));
        Assert.Equal(DateTimeKind.Utc, StateStore.FromStateTime(text).Value.Kind);
        Assert.Null(StateStore.FromStateTime(""));
    }

    [Fact]
    public void Plans_steps_within_the_run_and_keeps_the_reserve_for_the_result()
    {
        var run = _h.NewRun();
        run.TimeLimitMinutes = 60;
        Assert.Equal(50, run.MinutesLeft());
        Assert.Equal(45, run.StepLimit(45, "DISM"));
        _h.Clock.Elapsed += TimeSpan.FromMinutes(30);
        Assert.Equal(20, run.StepLimit(45, "DISM"));
        _h.Clock.Elapsed += TimeSpan.FromMinutes(16);
        Assert.Equal(0, run.StepLimit(45, "The component store cleanup"));
        Assert.Single(_h.Records("progress"), r => r.Str("severity") == "warning" &&
            r.Str("text") == "The component store cleanup was not started: this run has 4 minutes left, and that time is kept for reporting the result. Run the repair again to continue.");
        run.TimeLimitMinutes = 0;
        Assert.Equal(120 - 10 - 46, run.MinutesLeft());
    }
}
