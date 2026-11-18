using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Engine.Support;
using EndpointToolbelt.Platform;
using Xunit;

namespace EndpointToolbelt.Tests;

// Support package, remediation readout, workbook writer and the link-safe copy on the compiled
// engine (the app state cases are in EngineImeLogTests). Registry data lives in a fake and files in the test's folder:
// these tests never read this machine's IME state and never run mdmdiagnosticstool or dsregcmd.

/// <summary>The package with replaceable steps: a step with a function set here answers from it.</summary>
internal sealed class TestPackage : SupportPackage
{
    public TestPackage(RunContext run) : base(run) { }

    public Func<string, bool> OnToolExists { get; set; }
    public Func<int> OnStepLimit { get; set; }
    public Func<string, bool> OnAddDsregFile { get; set; }
    public Func<int> OnOemCodePage { get; set; }
    public Func<AppStateReadout> OnReadAppState { get; set; }
    public Func<IEnumerable<string>, Dictionary<string, string>> OnAppNames { get; set; }
    public Func<RemediationState> OnReadRemediations { get; set; }
    public Func<List<ProfileErrorRow>> OnProfileErrors { get; set; }
    public Func<List<EspRow>> OnEspState { get; set; }
    public Func<MsiAppReadout> OnReadMsiApps { get; set; }

    public override MsiAppReadout ReadMsiApps() => OnReadMsiApps != null ? OnReadMsiApps() : base.ReadMsiApps();
    public override bool ToolExists(string path) => OnToolExists != null ? OnToolExists(path) : base.ToolExists(path);
    public override int StepLimit(int limitMinutes, string label) => OnStepLimit != null ? OnStepLimit() : base.StepLimit(limitMinutes, label);
    public override bool AddDsregFile(string stage) => OnAddDsregFile != null ? OnAddDsregFile(stage) : base.AddDsregFile(stage);
    public override int OemCodePage() => OnOemCodePage != null ? OnOemCodePage() : base.OemCodePage();
    public override AppStateReadout ReadAppState() => OnReadAppState != null ? OnReadAppState() : base.ReadAppState();
    public override Dictionary<string, string> AppNames(IEnumerable<string> appIds) => OnAppNames != null ? OnAppNames(appIds) : base.AppNames(appIds);
    public override RemediationState ReadRemediations() => OnReadRemediations != null ? OnReadRemediations() : base.ReadRemediations();
    public override List<ProfileErrorRow> ProfileErrors() => OnProfileErrors != null ? OnProfileErrors() : base.ProfileErrors();
    public override List<EspRow> EspState() => OnEspState != null ? OnEspState() : base.EspState();
}

public class EngineRemediationTests : IDisposable
{
    private readonly EngineHarness _h = new();
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData(4, 0, null, 0L, "OK")]
    [InlineData(1, 1, 0, 0L, "FIXED")]
    [InlineData(2, 1, 0, 0L, "STILL-FAILING")]
    [InlineData(3, 1, 1, 0L, "REMEDIATION-FAILED")]
    [InlineData(0, 1, null, 0L, "ISSUE-FOUND")]
    [InlineData(0, null, null, 0L, "NOT-RUN")]
    [InlineData(0, null, null, 2278752567L, "FILTERED")]
    [InlineData(4, -1, null, 0L, "DETECT-ERROR")]
    public void Classifies(int status, int? first, int? remediationExit, long code, string verdict)
    {
        Assert.Equal(verdict, Remediation.Verdict(true, status, first, remediationExit, code).Verdict);
    }

    [Fact]
    public void Writes_the_last_run_in_ISO_8601_UTC_and_the_remediation_status_in_words()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var row = new RemediationRow { PolicyId = "11111111-2222-3333-4444-555555555555", LastRun = "11/16/2026 9:05:50 AM", Result = 3, RemediationStatus = 4 };
        var cells = row.Cells(berlin);
        Assert.Equal(RemediationRow.Headers.Length, cells.Count);
        Assert.Equal("2026-11-16T08:05:50Z", cells[Array.IndexOf(RemediationRow.Headers, "LastRun")]);
        // A device clock in UTC: the same moment, now with its zone.
        Assert.Equal("2026-11-16T08:05:50Z", new RemediationRow { LastRun = "11/16/2026 8:05:50 AM" }.Cells(TimeZoneInfo.Utc)[Array.IndexOf(RemediationRow.Headers, "LastRun")]);
        Assert.Equal("RemediationStatusText", RemediationRow.Headers[Array.IndexOf(RemediationRow.Headers, "RemediationStatus") + 1]);
        Assert.Equal("no issue detected", cells[Array.IndexOf(RemediationRow.Headers, "RemediationStatusText")]);
        // Result has no documented meaning: the number only, no words column.
        Assert.Equal(3, cells[Array.IndexOf(RemediationRow.Headers, "Result")]);
        Assert.DoesNotContain("ResultText", RemediationRow.Headers);
        Assert.Equal("remediation failed", RemediationRow.StatusText(3));
        Assert.Equal("", RemediationRow.StatusText(9));
        Assert.Equal("", RemediationRow.StatusText(null));
        // A time that cannot be read stays as the IME wrote it; none stays empty.
        Assert.Equal("yesterday", RemediationRow.LastRunUtc("yesterday", TimeZoneInfo.Utc));
        Assert.Equal("", RemediationRow.LastRunUtc("", TimeZoneInfo.Utc));
    }

    [Fact]
    public void Writes_the_app_state_time_in_ISO_8601_UTC()
    {
        Assert.Equal("2026-11-10T11:01:40Z", PackageSheets.IsoUtc("11/10/2026 11:01:40"));
        Assert.Equal("2026-11-10T23:01:40Z", PackageSheets.IsoUtc("11/10/2026 11:01:40 PM"));
        Assert.Equal("not a time", PackageSheets.IsoUtc("not a time"));
        var cells = PackageSheets.AppStateCells(new AppStateRow { LastUpdatedUtc = "11/10/2026 11:01:40" });
        Assert.Equal("2026-11-10T11:01:40Z", cells[Array.IndexOf(PackageSheets.AppStateHeaders, "LastUpdatedUtc")]);
    }

    /// <summary>The SID Windows gives a cloud (Entra) account with this object ID.</summary>
    private static string EntraSid(Guid objectId)
    {
        var b = objectId.ToByteArray();
        return string.Format(CultureInfo.InvariantCulture, "S-1-12-1-{0}-{1}-{2}-{3}", BitConverter.ToUInt32(b, 0), BitConverter.ToUInt32(b, 4), BitConverter.ToUInt32(b, 8), BitConverter.ToUInt32(b, 12));
    }

    [Fact]
    public void Shows_one_row_per_remediation_on_the_device_from_its_latest_run_without_reading_scripts()
    {
        var cache = _folder.Combine("cache");
        const string p1 = "11111111-2222-3333-4444-555555555555";
        const string p2 = "66666666-7777-8888-9999-aaaaaaaaaaaa";
        const string gone = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";
        foreach (var name in new[] { p1 + "_2", p1 + "_1", p2 + "_1" }) { Directory.CreateDirectory(Path.Combine(cache, "HealthScripts", name)); }
        _h.Services.ImePaths.ContentCache = cache;
        const string device = "00000000-0000-0000-0000-000000000000";
        const string user = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        const string other = "cccccccc-dddd-eeee-ffff-000000000000";
        var reports = _h.Services.ImePaths.SideCarKey + @"\Scripts\Reports\";
        var execution = _h.Services.ImePaths.SideCarKey + @"\Scripts\Execution\";
        // Device scope ran p1 v2 last and failed; the user scope ran it earlier and was fine.
        _h.Registry.Put(reports + device + "\\" + p1 + @"_2\Result", "Result", "{\"Result\":3,\"RemediationStatus\":3,\"RunAsAccount\":1,\"Info\":{\"FirstDetectExitCode\":1,\"RemediationExitCode\":5},\"RemediationScriptErrorDetails\":\"secret output\"}");
        _h.Registry.Put(execution + device + "\\" + p1 + "_2", "LastExecution", "10/14/2026 10:00:00");
        _h.Registry.Put(reports + user + "\\" + p1 + @"_2\Result", "Result", "{\"Result\":1,\"RemediationStatus\":4,\"RunAsAccount\":1,\"Info\":{\"FirstDetectExitCode\":0}}");
        _h.Registry.Put(execution + user + "\\" + p1 + "_2", "LastExecution", "10/13/2026 10:00:00");
        _h.Registry.Put(reports + device + "\\" + gone + @"_1\Result", "Result", "{\"Result\":1,\"RemediationStatus\":4}");
        // Another user's newest result: never read.
        _h.Registry.Put(reports + other + "\\" + p1 + @"_2\Result", "Result", "{\"Result\":1,\"RemediationStatus\":2,\"RunAsAccount\":2}");
        _h.Registry.Put(execution + other + "\\" + p1 + "_2", "LastExecution", "10/15/2026 10:00:00");
        var run = _h.NewRun();
        run.CallerSid = EntraSid(new Guid(user));

        var state = Remediation.Read(run);
        Assert.Equal(2, state.Policies);
        Assert.Equal(1, state.Leftover);
        Assert.Equal(1, state.OtherScopes);
        var first = state.Rows.Single(r => r.PolicyId == p1);
        Assert.Equal(2, first.Version);
        Assert.Equal("REMEDIATION-FAILED", first.Verdict);
        Assert.Equal("remediation script failed (exit 5)", first.State);
        Assert.Equal(2, first.Scopes);
        Assert.Equal(1, first.FailingScopes);
        Assert.Equal("Yes", first.RemediationErrorOutput);
        Assert.Equal("Device", first.LastRunScope);
        Assert.Equal("PENDING", state.Rows.Single(r => r.PolicyId == p2).Verdict);
        // Script output itself never reaches a row.
        Assert.DoesNotContain("secret output", string.Join(" ", state.Rows.SelectMany(r => r.Cells()).Select(c => Convert.ToString(c, CultureInfo.InvariantCulture))), StringComparison.Ordinal);

        Remediation.WriteSummary(run, state);
        Assert.Single(_h.Findings("REMEDIATION-REMEDIATION-FAILED"));
        Assert.Equal("1", Assert.Single(_h.Records("remediations-state")).Str("attention"));
    }
}

public class EngineWorkbookTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    internal static Dictionary<string, string> Parts(Stream stream)
    {
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            parts[entry.FullName] = reader.ReadToEnd();
        }
        return parts;
    }

    [Fact]
    public void Writes_a_valid_package_with_escaped_text_invariant_numbers_and_dates_and_an_empty_sheet_without_a_table()
    {
        var path = _folder.Combine("book.xlsx");
        var columns = new[]
        {
            new SheetColumn("Name", 30, CellKind.Text, o => ((string[])o)[0]),
            new SheetColumn("Verdict", 10, CellKind.Verdict, o => ((string[])o)[1]),
            new SheetColumn("Count", 8, CellKind.Number, o => ((string[])o)[2]),
            new SheetColumn("When", 20, CellKind.LocalDate, o => ((string[])o)[3]),
        };
        var saved = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
        try
        {
            Workbook.Write(new[]
            {
                new Sheet { Name = "Data", TableName = "DataTable", Columns = columns, Rows = new object[] { new[] { "A & <B>" + (char)1, "BAD", "1234567", "10/14/2026 10:00:00" }, new[] { "Plain", "OK", null, "not a date" } }, Attention = o => ((string[])o)[1] == "BAD" },
                new Sheet { Name = "Empty", TableName = "EmptyTable", Columns = columns, Rows = Array.Empty<object>(), EmptyText = "Nothing here." },
            }, path);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
        var parts = Parts(File.OpenRead(path));
        Assert.Contains("xl/worksheets/sheet1.xml", parts.Keys);
        Assert.Contains("xl/tables/table1.xml", parts.Keys);
        Assert.DoesNotContain("xl/tables/table2.xml", parts.Keys);
        foreach (var part in parts.Values)
        {
            using var reader = XmlReader.Create(new StringReader(part), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read()) { }
        }
        var sheet = parts["xl/worksheets/sheet1.xml"];
        Assert.Contains("A &amp; &lt;B&gt;</t>", sheet, StringComparison.Ordinal);
        Assert.Contains("<v>1234567</v>", sheet, StringComparison.Ordinal);
        Assert.Contains("<c r=\"B2\" s=\"2\"", sheet, StringComparison.Ordinal);
        var expected = new DateTime(2026, 10, 14, 10, 0, 0).ToOADate().ToString("R", CultureInfo.InvariantCulture);
        Assert.Contains("<c r=\"D2\" s=\"1\"><v>" + expected + "</v>", sheet, StringComparison.Ordinal);
        Assert.Contains("not a date", sheet, StringComparison.Ordinal);
        Assert.Contains("Nothing here.", parts["xl/worksheets/sheet2.xml"], StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_cells_from_starting_a_formula_when_the_CSV_is_opened_in_Excel()
    {
        var path = _folder.Combine("safe.csv");
        Csv.Write(path, new[] { "Name", "Version", "Plain", "Number", "Empty" }, new[] { new object[] { "=HYPERLINK(\"x\")", "-1.0", "Viewer", -5, null } });
        var text = File.ReadAllText(path);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", text, StringComparison.Ordinal);
        Assert.Contains("\"'-1.0\"", text, StringComparison.Ordinal);
        Assert.Contains("\"Viewer\"", text, StringComparison.Ordinal);
        Assert.Contains("\"-5\"", text, StringComparison.Ordinal);
        // As ConvertTo-Csv writes a null: an empty field, no quotes.
        Assert.EndsWith("\"-5\",\r\n", text, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path).Take(3));
    }
}

public class EngineSupportPackageTests : IDisposable
{
    private const string Border = "+----------------------------------------------------------------------+";
    private readonly EngineHarness _h = new();
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [Fact]
    public void Keeps_only_the_device_sections_of_dsregcmd()
    {
        var lines = new[]
        {
            Border, "| Device State                                                         |", Border, "AzureAdJoined : YES",
            Border, "| User State                                                           |", Border, "WorkplaceJoined : NO", "UserEmail : someone",
            Border, "| SSO State                                                            |", Border, "AzureAdPrt : NO",
            Border, "| Tenant Details                                                       |", Border, "TenantName : Contoso",
        };
        var r = SupportPackage.DeviceSections(lines);
        Assert.Equal(2, r.Found);
        var text = string.Join("\n", r.Lines);
        Assert.Contains("AzureAdJoined", text, StringComparison.Ordinal);
        Assert.Contains("TenantName", text, StringComparison.Ordinal);
        Assert.DoesNotContain("UserEmail", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AzureAdPrt", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(42)]
    [InlineData(-1)]
    public void Captures_dsregcmd_when_the_OEM_code_page_is_a_placeholder(int codePage)
    {
        // As SYSTEM the culture's OEM code page was 1 (CP_OEMCP) and the whole step was lost.
        _h.Processes.Respond = request => request.Label == "dsregcmd /status"
            ? FakeProcessRunner.Exit(0, string.Join("\r\n", Border, "| Device State                                                         |", Border, "AzureAdJoined : YES"))
            : throw new InvalidOperationException(request.Label + " must not run");
        // Each case has a test folder of its own.
        var stage = _folder.Combine("stage");
        Directory.CreateDirectory(stage);
        var package = new TestPackage(_h.NewRun()) { OnOemCodePage = () => codePage };

        Assert.True(package.AddDsreg(stage));
        Assert.Equal(ConsoleEncoding.FallbackCodePage, Assert.Single(_h.Processes.Requests).OutputEncoding.CodePage);
        Assert.Contains("AzureAdJoined : YES", File.ReadAllText(Path.Combine(stage, "dsregcmd_status.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_dsregcmd_in_the_systems_own_OEM_code_page()
    {
        _h.Processes.Respond = _ => FakeProcessRunner.Exit(0, string.Join("\r\n", Border, "| Device State                                                         |", Border, "AzureAdJoined : YES"));
        var target = _folder.Combine("dsreg850.txt");
        Assert.True(new TestPackage(_h.NewRun()) { OnOemCodePage = () => 850 }.AddDsregFile(target));
        Assert.Equal(850, Assert.Single(_h.Processes.Requests).OutputEncoding.CodePage);

        // Without a stand-in: this system's OEM code page, a real one.
        var system = new SupportPackage(_h.NewRun()).OemCodePage();
        Assert.Equal(system, ConsoleEncoding.FromCodePage(system).CodePage);
    }

    [Fact]
    public void Refuses_links_and_hard_links_when_copying_into_the_package()
    {
        var source = _folder.Combine("source.log");
        File.WriteAllText(source, "content\r\n");
        var hard = _folder.Combine("hard.log");
        Assert.True(CreateHardLink(hard, source, IntPtr.Zero));
        var real = _folder.Combine("realfolder");
        Directory.CreateDirectory(real);
        var junction = _folder.Combine("junction");
        TestFolder.CreateJunction(junction, real);
        // Two hard links to one file: both names are refused.
        Assert.Contains("hard link", Assert.ThrowsAny<IOException>(() => SupportPackage.CopyNoFollow(hard, _folder.Combine("out1.log"))).Message, StringComparison.Ordinal);
        Assert.Contains("link", Assert.ThrowsAny<IOException>(() => SupportPackage.CopyNoFollow(junction, _folder.Combine("out2.log"))).Message, StringComparison.Ordinal);
        var plain = _folder.Combine("plain.log");
        File.WriteAllText(plain, "abc\r\n");
        Assert.Equal(5, SupportPackage.CopyNoFollow(plain, _folder.Combine("out3.log")));
        Assert.ThrowsAny<IOException>(() => SupportPackage.CopyNoFollow(plain, _folder.Combine("out3.log")));
        Assert.False(File.Exists(_folder.Combine("out1.log")));
    }

    private static AppStateReadout OneApp()
    {
        var state = new AppStateReadout { Present = true, Scopes = 1 };
        state.Rows.Add(new AppStateRow { AppId = "11111111-2222-3333-4444-555555555555", ScopeRef = "Device", Verdict = "OK", State = "installed / succeeded" });
        return state;
    }

    private static List<string> ZipNames(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    [Fact]
    public void Builds_the_package_from_the_parts_leaves_out_what_MDMDiagnostics_zip_already_holds_and_cleans_up()
    {
        var output = _folder.Combine("out");
        var ime = _folder.Combine("ime");
        var own = _folder.Combine("own");
        foreach (var d in new[] { output, ime, own }) { Directory.CreateDirectory(d); }
        File.WriteAllText(Path.Combine(ime, "AgentExecutor.log"), "same as in the MDM zip");
        File.WriteAllText(Path.Combine(ime, "AppWorkload.log"), "only here\r\n");
        File.WriteAllText(Path.Combine(ime, "EndpointToolbelt.log"), "left to the own-log step\r\n");
        File.WriteAllText(Path.Combine(own, "EndpointToolbeltService.log"), "service log\r\n");
        File.WriteAllText(Path.Combine(own, "EndpointToolbelt.log"), "engine log\r\n");
        _h.Services.ImePaths.LogsFolder = ime;
        _h.Processes.Respond = request =>
        {
            if (request.Label == "mdmdiagnosticstool")
            {
                // Stand-in for mdmdiagnosticstool: a zip that already holds AgentExecutor.log.
                var zipPath = System.Text.RegularExpressions.Regex.Match(request.Arguments, "-zip \"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)).Groups[1].Value;
                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                using var stream = zip.CreateEntry("Logs/AgentExecutor.log").Open();
                var bytes = Encoding.ASCII.GetBytes("same as in the MDM zip");
                stream.Write(bytes, 0, bytes.Length);
                return FakeProcessRunner.Exit(0);
            }
            if (request.Label == "dsregcmd /status") { return FakeProcessRunner.Exit(0, string.Join("\r\n", Border, "| Device State                                                         |", Border, "AzureAdJoined : YES")); }
            throw new InvalidOperationException(request.Label + " must not run");
        };
        var run = _h.NewRun();
        run.OutputDirectory = output;
        run.LogDirectory = own;
        run.TimeLimitMinutes = 30;
        var package = new TestPackage(run)
        {
            OnToolExists = path => path.EndsWith("mdmdiagnosticstool.exe", StringComparison.OrdinalIgnoreCase),
            OnReadAppState = OneApp,
            OnAppNames = _ => new Dictionary<string, string> { ["11111111-2222-3333-4444-555555555555"] = "Viewer" },
            OnReadRemediations = () => new RemediationState(),
            OnProfileErrors = () => new List<ProfileErrorRow>(),
            OnEspState = () => new List<EspRow>(),
        };
        Assert.Equal(0, package.CollectDiag());

        var file = Assert.Single(_h.Records("file"));
        Assert.Equal("EndpointToolbelt_SupportPackage.zip", file.Str("name"));
        Assert.False(Directory.Exists(Path.Combine(output, "package")));
        // AgentExecutor.log is taken out of MDMDiagnostics.zip and is in IMELogs once, by the
        // package rules; the package has its index.
        Assert.Equal(new[] { "dsregcmd_status.txt", "EndpointToolbelt/EndpointToolbelt.log", "EndpointToolbelt/EndpointToolbeltService.log", "IMELogs/AgentExecutor.log", "IMELogs/AppWorkload.log", "IntuneState.xlsx", "MDMDiagnostics.zip", "PackageIndex.txt", "Remediations.csv", "Win32AppState.csv" },
            ZipNames(Path.Combine(output, SupportPackage.PackageName)));
        using (var package2 = ZipFile.OpenRead(Path.Combine(output, SupportPackage.PackageName)))
        using (var inner = new ZipArchive(package2.GetEntry("MDMDiagnostics.zip").Open(), ZipArchiveMode.Read))
        {
            Assert.Empty(inner.Entries);
        }
        // One header per step, in order, all of the same shape, with plain titles.
        var headers = _h.Records("progress").Select(r => r.Str("text")).Where(t => t.StartsWith("== Step ", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[]
        {
            "== Step 1/6: Collect the Intune diagnostics ==", "== Step 2/6: Collect the app installer logs ==", "== Step 3/6: Add the device sign-in details ==",
            "== Step 4/6: Read the app and remediation state ==", "== Step 5/6: Add the Endpoint Toolbelt logs ==", "== Step 6/6: Create the zip ==",
        }, headers);
        Assert.Matches(@"created: \d+ files, ", _h.Records("progress").Select(r => r.Str("text")).Single(t => t.StartsWith("Support package created", StringComparison.Ordinal)));
    }

    [Fact]
    public void Keeps_building_the_package_when_dsregcmd_fails_and_writes_plain_times_into_the_workbook()
    {
        var output = _folder.Combine("out3");
        Directory.CreateDirectory(output);
        _h.Services.ImePaths.LogsFolder = Path.Combine(output, "none");
        var run = _h.NewRun();
        run.OutputDirectory = output;
        run.TimeLimitMinutes = 30;
        run.State.Set("LastFullReset", StateStore.ToStateTime(_h.Clock.Now.AddMinutes(-5)));
        var package = new TestPackage(run)
        {
            OnStepLimit = () => 0,
            OnAddDsregFile = _ => throw new InvalidOperationException("dsregcmd crashed"),
            OnReadAppState = () => new AppStateReadout(),
            OnReadRemediations = () => throw new InvalidOperationException("unreadable"),
            OnProfileErrors = () => new List<ProfileErrorRow>(),
            OnEspState = () => new List<EspRow>(),
        };
        // Parts are missing, so the package is delivered with exit 2.
        Assert.Equal(2, package.CollectDiag());

        Dictionary<string, string> book;
        using (var zip = ZipFile.OpenRead(Path.Combine(output, SupportPackage.PackageName)))
        {
            var entry = Assert.Single(zip.Entries, e => e.FullName == "IntuneState.xlsx");
            var memory = new MemoryStream();
            using (var stream = entry.Open()) { stream.CopyTo(memory); }
            memory.Position = 0;
            book = EngineWorkbookTests.Parts(memory);
        }
        Assert.Matches(@"The last Full IME reset ran at \d{4}-\d\d-\d\d \d\d:\d\d \(device time\)", book["xl/worksheets/sheet1.xml"]);
        Assert.DoesNotContain("[[t:", book["xl/worksheets/sheet1.xml"], StringComparison.Ordinal);
        Assert.Contains("could not be read", book["xl/worksheets/sheet2.xml"], StringComparison.Ordinal);
        var partial = Assert.Single(_h.Findings("PACKAGE-PARTIAL"));
        Assert.Contains("MDM diagnostics (no time left)", partial.Str("detail"), StringComparison.Ordinal);
        Assert.Contains("dsregcmd status", partial.Str("detail"), StringComparison.Ordinal);
        Assert.Contains("remediation results", partial.Str("detail"), StringComparison.Ordinal);
        Assert.Single(_h.Records("file"));
    }

    [Fact]
    public void Removes_other_users_SIDs_from_the_service_log_in_the_package()
    {
        var source = _folder.Combine("EndpointToolbeltService.log");
        var target = _folder.Combine("redacted.log");
        const string mine = "S-1-12-1-111-222-333-444";
        File.WriteAllLines(source, new[] { "RetryAppsAndPolicies requested by " + mine, "FullReset requested by S-1-5-21-1000-2000-3000-1001", "Service started by S-1-5-18" });
        var run = _h.NewRun();
        run.CallerSid = mine;
        new SupportPackage(run).CopyServiceLogRedacted(source, target);
        var lines = File.ReadAllLines(target);
        // The requester's own SID is shortened as the CSV files name that user (User: and the object ID's start).
        Assert.Equal("RetryAppsAndPolicies requested by User:0000006f", lines[0]);
        Assert.DoesNotContain(mine, File.ReadAllText(target), StringComparison.Ordinal);
        Assert.Equal("FullReset requested by <another user>", lines[1]);
        Assert.Contains("S-1-5-18", lines[2], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("S-1-12-1-3735928559-1111111111-2222222222-3333333333", "User:deadbeef")]
    [InlineData("S-1-5-21-1000-2000-3000-1001", "S-1-5-21-...-1001")]
    [InlineData("S-1-5-18", "S-1-5-18")]
    [InlineData("", "")]
    public void Shortens_a_users_SID_the_way_the_CSV_files_name_that_user(string sid, string expected)
    {
        Assert.Equal(expected, SupportPackage.ShortUser(sid));
    }

    [Fact]
    public void Leaves_other_users_runs_out_of_the_service_log_in_the_package()
    {
        const string mine = "S-1-12-1-111-222-333-444";
        var source = _folder.Combine("svc-own.log");
        var target = _folder.Combine("svc-own-copy.log");
        File.WriteAllLines(source, new[]
        {
            "Run aaaa0001: RetryAppsAndPolicies requested by " + mine + " (session 1, 0 other session(s)).",
            "Run bbbb0002: FullReset requested by S-1-5-21-1000-2000-3000-1001 (session 2, 1 other session(s)).",
            "Run bbbb0002: could not remove the work folder: busy",
            "Too many open connections from S-1-5-21-1000-2000-3000-1001; a client was refused.",
        });
        var run = _h.NewRun("aaaa0001", "CollectDiag");
        run.CallerSid = mine;
        new SupportPackage(run).CopyServiceLogRedacted(source, target, new HashSet<string> { "aaaa0001" });
        var lines = File.ReadAllLines(target);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Run aaaa0001", lines[0], StringComparison.Ordinal);
        Assert.Equal("Too many open connections from <another user>; a client was refused.", lines[1]);
        Assert.DoesNotContain(lines, l => l.Contains("bbbb0002"));
        Assert.Contains("2 lines of other users' runs were left out", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_only_the_callers_own_runs_in_the_engine_log_in_the_package()
    {
        const string mine = "S-1-12-1-111-222-333-444";
        const string other = "S-1-5-21-1000-2000-3000-1001";
        var service = _folder.Combine("svc-runs.log");
        File.WriteAllLines(service, new[]
        {
            "Run aaaa0001: RetryAppsAndPolicies requested by " + mine + " (session 1, 0 other session(s)).",
            "Run bbbb0002: FullReset requested by " + other + " (session 2, 1 other session(s)).",
            "Run cccc0003: RetryAppsAndPolicies requested by " + mine + " (session 1, 0 other session(s)).",
        });
        var engine = _folder.Combine("engine-runs.log");
        var target = _folder.Combine("engine-copy.log");
        const string tail = "]LOG]!><time=\"10:00:00.000-120\" date=\"10-16-2026\" component=\"Engine.Test\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";
        File.WriteAllLines(engine, new[]
        {
            "<![LOG[[aaaa0001] my retry" + tail, "<![LOG[[bbbb0002] their app Contoso Viewer" + tail, "<![LOG[[bbbb0002] their result" + tail,
            "<![LOG[[cccc0003] my second retry" + tail, "<![LOG[[pkg00004] this package" + tail,
        }, new UTF8Encoding(true));
        var run = _h.NewRun("pkg00004", "CollectDiag");
        run.CallerSid = mine;
        var package = new SupportPackage(run);
        var ownRuns = package.CallerRunIds(new[] { service, service + ".missing" });
        Assert.Equal("aaaa0001,cccc0003,pkg00004", string.Join(",", ownRuns.OrderBy(i => i, StringComparer.Ordinal)));
        package.CopyEngineLogForCaller(engine, target, ownRuns);
        var text = File.ReadAllText(target);
        Assert.Contains("my retry", text, StringComparison.Ordinal);
        Assert.Contains("my second retry", text, StringComparison.Ordinal);
        Assert.Contains("this package", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bbbb0002", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Contoso Viewer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("their result", text, StringComparison.Ordinal);
        Assert.Contains("2 lines of other users' runs were left out", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_only_this_run_in_the_engine_log_when_the_caller_is_unknown()
    {
        Assert.Equal(new[] { "solo0001" }, new SupportPackage(_h.NewRun("solo0001", "CollectDiag")).CallerRunIds(Array.Empty<string>()));
    }

    [Fact]
    public void Reports_a_package_that_could_not_be_created()
    {
        var output = _folder.Combine("out2");
        Directory.CreateDirectory(output);
        var run = _h.NewRun();
        run.OutputDirectory = output;
        run.TimeLimitMinutes = 30;
        var package = new TestPackage(run) { OnStepLimit = () => throw new IOException("disk full") };
        Assert.Equal(1, package.CollectDiag());
        Assert.Single(_h.Findings("PACKAGE-FAILED"));
        Assert.Empty(_h.Records("file"));
        Assert.False(Directory.Exists(Path.Combine(output, "package")));
    }
}

public class EngineDeviceSettingsTests
{
    private readonly EngineHarness _h = new();

    [Fact]
    public void Writes_result_codes_as_unsigned_hex()
    {
        Assert.Equal("0x80070005", PackageSheets.HresultText("0x80070005"));
        Assert.Equal("0x80070005", PackageSheets.HresultText(-2147024891));
        Assert.Equal("0x80070005", PackageSheets.HresultText("-2147024891"));
        Assert.Equal("", PackageSheets.HresultText(5));
        Assert.Equal("", PackageSheets.HresultText("not a code"));
        Assert.Equal("", PackageSheets.HresultText(null));
    }

    private static MdmEvent Event(int id, int level, string processId, DateTime utc, params string[] values)
    {
        var ev = new MdmEvent { Id = id, Level = level, ProcessId = processId, TimeCreatedUtc = utc };
        ev.Values.AddRange(values);
        return ev;
    }

    [Fact]
    public void Lists_failed_settings_with_their_path_and_code_never_their_message_and_skips_events_from_a_user_session()
    {
        var now = DateTime.UtcNow;
        _h.MdmEvents.LevelEvents = _ => new[]
        {
            Event(404, 2, "10", now, "./Device/Vendor/MSFT/Policy/Config/BitLocker/RequireDeviceEncryption", "-2147024891"),
            Event(454, 3, "10", now.AddMinutes(-5), "0x87D1FDE8"),
            Event(404, 2, "999", now.AddMinutes(-10), "./Device/Vendor/MSFT/Policy/Config/Forged/Setting"),
        };
        // Process 999 runs in a user's session: its events are not counted.
        _h.ProcessControl.Sessions[999] = 1;
        var rows = PackageSheets.ProfileErrorRows(_h.NewRun());
        Assert.Equal(2, rows.Count);
        Assert.Equal("Error", rows[0].Level);
        Assert.Equal(404, rows[0].EventId);
        Assert.Equal("./Device/Vendor/MSFT/Policy/Config/BitLocker/RequireDeviceEncryption", rows[0].Setting);
        Assert.Equal("0x80070005", rows[0].Result);
        Assert.Equal("Warning", rows[1].Level);
        Assert.Equal("0x87D1FDE8", rows[1].Result);
        Assert.DoesNotContain(rows, r => r.Setting.Contains("Forged"));
        Assert.Equal("Configuration profiles", PackageSheets.ProfileErrorSheet(rows).Name);
    }

    [Fact]
    public void Marks_the_settings_Windows_reports_on_every_device_as_known_harmless_and_does_not_shade_them()
    {
        var now = DateTime.UtcNow;
        _h.MdmEvents.LevelEvents = _ => new[]
        {
            Event(404, 2, "10", now, "./Vendor/MSFT/Policy/ConfigOperations/ADMXInstall/Receiver/Properties/Policy/FakePolicy/Version"),
            Event(404, 2, "10", now, "./Device/Vendor/MSFT/Policy/Config/BitLocker/RequireDeviceEncryption"),
            Event(404, 2, "10", now, "./Device/Vendor/MSFT/DMClient/Provider/MS DM Server/CertPinning/PinRules"),
        };
        var rows = PackageSheets.ProfileErrorRows(_h.NewRun());
        Assert.Equal("Known harmless: Windows reports this on every device", rows[0].Note);
        Assert.Equal("", rows[1].Note);
        Assert.Equal("Known harmless: Windows reports this on every device", rows[2].Note);
        var sheet = PackageSheets.ProfileErrorSheet(rows);
        Assert.False(sheet.Attention(rows[0]));
        Assert.True(sheet.Attention(rows[1]));
        Assert.False(sheet.Attention(rows[2]));
    }

    [Fact]
    public void Treats_an_empty_MDM_admin_log_as_no_rows()
    {
        Assert.Empty(PackageSheets.ProfileErrorRows(_h.NewRun()));
        Assert.DoesNotContain(_h.Records("progress"), r => r.Str("severity") == "warning");
    }

    [Fact]
    public void Reads_the_Enrollment_Status_Page_state_of_the_device_and_the_requesting_user_only()
    {
        const string root = @"TEST\Esp";
        const string mine = "S-1-12-1-1111-2222-3333-4444";
        const string other = "S-1-12-1-5555-6666-7777-8888";
        _h.Registry.Put(root + @"\Device\Setup\Apps\Tracking\Sidecar\Win32App_aaaa_1", "InstallationState", 4);
        _h.Registry.Put(root + @"\Device\DevicePreparation\PolicyProviders\Sidecar", "InstallationState", 2);
        _h.Registry.Put(root + "\\" + mine + @"\Setup\Apps\Tracking\Sidecar\Win32App_bbbb_1", "InstallationState", 3);
        _h.Registry.Put(root + "\\" + other + @"\Setup\Apps\Tracking\Sidecar\Win32App_cccc_1", "InstallationState", 4);
        _h.Registry.Put(root + @"\ESPTrackingInfo\Diagnostics\ExpectedPolicies", "One", 1);
        _h.Registry.Put(root + @"\ESPTrackingInfo\Diagnostics\ExpectedPolicies", "Two", 1);
        var run = _h.NewRun();
        run.CallerSid = mine;
        var rows = PackageSheets.EspRows(run, root);
        var app = Assert.Single(rows, r => r.Item == "Win32App_aaaa_1");
        Assert.Equal("Device", app.Scope);
        Assert.Equal(@"Setup\Apps\Tracking\Sidecar", app.Category);
        Assert.Equal("Error", app.State);
        Assert.Equal("Not required", rows.Single(r => r.Category == @"DevicePreparation\PolicyProviders").State);
        Assert.Equal("Completed", rows.Single(r => r.Item == "Win32App_bbbb_1").State);
        Assert.DoesNotContain(rows, r => r.Item == "Win32App_cccc_1");
        Assert.Equal("2 entries", rows.Single(r => r.Item == "ExpectedPolicies").State);
        Assert.Equal("Enrollment Status Page", PackageSheets.EspSheet(rows).Name);
        Assert.Empty(PackageSheets.EspRows(run, root + "Missing"));
    }
}

/// <summary>The check-ins and the IME log summary tab.</summary>
public class EngineImeLogSummaryTests : IDisposable
{
    private readonly EngineHarness _h = new();
    private readonly TestFolder _folder = new();

    public EngineImeLogSummaryTests()
    {
        var now = DateTime.UtcNow;
        _h.Services.Clock = new SystemClock();
        _h.Services.ImePaths.LogsFolder = _folder.Path;
        File.WriteAllLines(_folder.Combine("IntuneManagementExtension.log"), new[]
        {
            CmLine("Get policies = [{\"Id\":\"x\"}]", now.AddHours(-3), component: "IntuneManagementExtension"),
            CmLine("[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController with url https://contoso.example, WebException status ConnectFailure message x\r\n   at stack", now.AddHours(-1), type: 3, component: "IntuneManagementExtension"),
            CmLine("a warning", now.AddHours(-1), type: 2, component: "IntuneManagementExtension"),
        });
        File.WriteAllLines(_folder.Combine("AppWorkload.log"), new[]
        {
            CmLine("[Win32App][V3Processor] Processing subgraph with app ids: aaaaaaaa-1111-2222-3333-444444444444", now.AddHours(-2), 5),
            CmLine("[Win32App][ExecutionActionHandler] Handler invoked with execution type: Install for policy with id: aaaaaaaa-1111-2222-3333-444444444444 and version: 1.", now.AddHours(-2).AddSeconds(1), 5),
            CmLine("[Win32App] lpExitCode 1618", now.AddHours(-2).AddSeconds(2), 5),
            CmLine("[Win32App] lpExitCode is defined as Retry", now.AddHours(-2).AddSeconds(3), 5, 3),
        });
    }

    public void Dispose() => _folder.Dispose();

    private static string CmLine(string message, DateTime utc, int thread = 7, int type = 1, string component = "AppWorkload")
    {
        var local = utc.ToLocalTime();
        return string.Format(CultureInfo.InvariantCulture, "<![LOG[{0}]LOG]!><time=\"{1}\" date=\"{2}\" component=\"{3}\" context=\"\" type=\"{4}\" thread=\"{5}\" file=\"\">",
            message, local.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture), local.ToString("M-d-yyyy", CultureInfo.InvariantCulture), component, type, thread);
    }

    [Fact]
    public void Finds_the_last_check_in_and_a_failure_after_it_with_a_code_like_error_only()
    {
        var c = PackageSheets.CheckIn(_h.NewRun());
        Assert.True(c.Available);
        Assert.NotNull(c.LastSuccessUtc);
        Assert.True(c.LastFailureUtc > c.LastSuccessUtc);
        Assert.Equal("ConnectFailure", c.Error);
    }

    [Fact]
    public void Writes_the_IME_log_summary_tab_with_phases_check_ins_and_error_counts_but_no_log_text()
    {
        var state = new AppStateReadout();
        state.Rows.Add(new AppStateRow { AppId = "aaaaaaaa-1111-2222-3333-444444444444", Name = "Contoso App" });
        var summary = PackageSheets.ImeLogSummaryRows(_h.NewRun(), state);
        Assert.Equal("install failed with exit code 1618", Assert.Single(summary, r => r.Kind == "App").Phase);
        Assert.Equal(2, summary.Count(r => r.Kind == "Check-in"));
        Assert.Equal("ConnectFailure", summary.Single(r => r.Name == "Failed check-in after it").ExitCode);
        Assert.StartsWith("1 error, 1 warning in the last 7 days", summary.Single(r => r.Kind == "Log errors" && r.Name == "IntuneManagementExtension.log: IntuneManagementExtension").Detail, StringComparison.Ordinal);
        // No message text of the logs reaches the sheet.
        var text = string.Join(" ", summary.SelectMany(r => new[] { r.Kind, r.Name, r.Phase, r.ExitCode, r.Detail }));
        Assert.DoesNotMatch(@"(?i)contoso\.example|stack|a warning", text);
        var book = _folder.Combine("summary.xlsx");
        Workbook.Write(new[] { PackageSheets.ImeLogSummarySheet(summary) }, book);
        var sheet = EngineWorkbookTests.Parts(File.OpenRead(book))["xl/worksheets/sheet1.xml"];
        Assert.Contains("install failed with exit code 1618", sheet, StringComparison.Ordinal);
        Assert.Contains("ConnectFailure", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public void Fails_a_support_package_without_an_output_folder_and_changes_nothing()
    {
        var file = EngineHarness.WriteRequest(_folder, "req3.json", "{\"contractVersion\":1,\"runId\":\"r3\",\"action\":\"CollectDiag\",\"config\":{}}");
        Assert.Equal(1, RunFrame.Run("CollectDiag", file, ActionHandlers.Shipped, _h.Services));
        Assert.Single(_h.Findings("PACKAGE-FAILED"));
        Assert.Equal("0", Assert.Single(_h.Records("summary")).Str("changes"));
    }
}
