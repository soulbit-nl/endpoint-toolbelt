using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Engine.Support;
using Xunit;
using EnginePackage = EndpointToolbelt.Engine.Support.SupportPackage;

namespace EndpointToolbelt.Tests;

/// <summary>
/// A smaller support package (per IME log the current file and its newest older copy, Sensor
/// and Win32AppInventory left out, each file cut to its newest part, the zip kept under a limit by
/// dropping in a fixed order, an index that says so), Reset Company Portal data that asks before it
/// closes Company Portal and never forces it, and no Retry work apps and settings while the IME is not
/// installed. Files live in the test's folder; nothing reads this machine's IME logs.
/// </summary>
public class SupportPackageSizeTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);

    private readonly TestFolder _folder = new();
    private readonly EngineHarness _h = new();

    public void Dispose() => _folder.Dispose();

    private static ImeLogFile Log(string name, long length = 1000, int hoursAgo = 0) => new() { Name = name, Length = length, LastWriteUtc = T0.AddHours(-hoursAgo) };

    [Theory]
    [InlineData("AppWorkload.log", "AppWorkload", null)]
    [InlineData("AppWorkload-20261112-093000.log", "AppWorkload", "20261112-093000")]
    [InlineData("IntuneManagementExtension-20261101-000000.log", "IntuneManagementExtension", "20261101-000000")]
    [InlineData("NotificationInfraLogs.log", "NotificationInfraLogs", null)]
    [InlineData("readme.txt", "readme.txt", null)]
    public void A_log_s_family_and_its_date_come_from_its_name(string name, string family, string stamp)
    {
        Assert.Equal(family, PackageRules.Family(name));
        Assert.Equal(stamp, PackageRules.Stamp(name));
    }

    [Fact]
    public void Per_log_the_current_file_and_its_newest_older_copy_are_taken_and_Sensor_and_Win32AppInventory_are_left_out()
    {
        var plan = PackageRules.Plan(new[]
        {
            Log("AppWorkload.log"),
            Log("AppWorkload-20261110-080000.log", hoursAgo: 72),
            Log("AppWorkload-20261112-080000.log", hoursAgo: 24),
            Log("AppWorkload-20261111-080000.log", hoursAgo: 48),
            Log("Sensor.log", 3_000_000),
            Log("Sensor-20261112-080000.log"),
            Log("Win32AppInventory.log"),
            Log("DeviceHealthMonitoring.log"),
            Log("AgentExecutor-20261112-070000.log"),
        }).ToDictionary(p => p.File.Name, p => p.Choice);
        Assert.Equal(ImeLogChoice.Take, plan["AppWorkload.log"]);
        Assert.Equal(ImeLogChoice.Take, plan["AppWorkload-20261112-080000.log"]);
        Assert.Equal(ImeLogChoice.OlderCopy, plan["AppWorkload-20261111-080000.log"]);
        Assert.Equal(ImeLogChoice.OlderCopy, plan["AppWorkload-20261110-080000.log"]);
        Assert.Equal(ImeLogChoice.LeftOutByDefault, plan["Sensor.log"]);
        Assert.Equal(ImeLogChoice.LeftOutByDefault, plan["Sensor-20261112-080000.log"]);
        Assert.Equal(ImeLogChoice.LeftOutByDefault, plan["Win32AppInventory.log"]);
        // A log that is not core is still taken; a family with only a dated copy keeps it.
        Assert.Equal(ImeLogChoice.Take, plan["DeviceHealthMonitoring.log"]);
        Assert.Equal(ImeLogChoice.Take, plan["AgentExecutor-20261112-070000.log"]);
        Assert.Equal(new[] { "AppWorkload", "IntuneManagementExtension", "AgentExecutor", "AppActionProcessor", "HealthScripts", "ClientHealth" }, PackageRules.CoreFamilies);
    }

    [Fact]
    public void Every_older_copy_of_an_app_install_log_of_the_last_day_is_taken_other_logs_keep_only_their_newest()
    {
        var plan = PackageRules.Plan(new[]
        {
            Log("AppWorkload.log"),
            // The copy that held a failed uninstall and the one with the user's retry: both of today.
            Log("AppWorkload-20261113-083531.log", hoursAgo: 1),
            Log("AppWorkload-20261113-080514.log", hoursAgo: 2),
            // Ended 23 hours ago: it reaches into the last day.
            Log("AppWorkload-20261112-100000.log", hoursAgo: 23),
            Log("AppWorkload-20261111-080000.log", hoursAgo: 49),
            Log("AgentExecutor-20261113-070000.log", hoursAgo: 2),
            Log("AgentExecutor-20261113-050000.log", hoursAgo: 4),
            Log("ClientHealth.log"),
            Log("ClientHealth-20261113-070000.log", hoursAgo: 2),
            Log("ClientHealth-20261113-050000.log", hoursAgo: 4),
        }, T0).ToDictionary(p => p.File.Name, p => p.Choice);
        Assert.Equal(ImeLogChoice.Take, plan["AppWorkload-20261113-083531.log"]);
        Assert.Equal(ImeLogChoice.Take, plan["AppWorkload-20261113-080514.log"]);
        Assert.Equal(ImeLogChoice.Take, plan["AppWorkload-20261112-100000.log"]);
        Assert.Equal(ImeLogChoice.OlderCopy, plan["AppWorkload-20261111-080000.log"]);
        Assert.Equal(ImeLogChoice.Take, plan["AgentExecutor-20261113-050000.log"]);
        // A log that is not an app install log keeps only its newest older copy.
        Assert.Equal(ImeLogChoice.Take, plan["ClientHealth-20261113-070000.log"]);
        Assert.Equal(ImeLogChoice.OlderCopy, plan["ClientHealth-20261113-050000.log"]);
        Assert.Equal(new[] { "AppWorkload", "IntuneManagementExtension", "AgentExecutor" }, PackageRules.AppInstallFamilies);
        // Without a time the rules by age do not apply: only the newest older copy.
        Assert.Equal(ImeLogChoice.OlderCopy, PackageRules.Plan(new[] { Log("AppWorkload-20261113-083531.log"), Log("AppWorkload-20261113-080514.log", hoursAgo: 1) })[1].Choice);
    }

    [Fact]
    public void PSADT_logs_are_taken_from_the_last_30_days_only()
    {
        var plan = PackageRules.Plan(new[]
        {
            Log("PSADT_Contoso_Agent_1.0_x64_EN_01_Install_2026-11-10T144144.zip", hoursAgo: 24 * 3),
            Log("PSADT_Contoso_Agent_0.9_x64_EN_01_install_2025-03-27-09-56-46.zip", hoursAgo: 24 * 400),
            Log("PSADT_Contoso_Viewer_Uninstall.log", hoursAgo: 24 * 31),
            Log("Contoso_Viewer_2.0_x64_EN_01_PSAppDeployToolkit_Install.log", hoursAgo: 24 * 29),
            // Not a PSADT log: old, but the current file of its own log.
            Log("DISM.log", hoursAgo: 24 * 400),
        }, T0).ToDictionary(p => p.File.Name, p => p.Choice);
        Assert.Equal(ImeLogChoice.Take, plan["PSADT_Contoso_Agent_1.0_x64_EN_01_Install_2026-11-10T144144.zip"]);
        Assert.Equal(ImeLogChoice.OldPsadtLog, plan["PSADT_Contoso_Agent_0.9_x64_EN_01_install_2025-03-27-09-56-46.zip"]);
        Assert.Equal(ImeLogChoice.OldPsadtLog, plan["PSADT_Contoso_Viewer_Uninstall.log"]);
        Assert.Equal(ImeLogChoice.Take, plan["Contoso_Viewer_2.0_x64_EN_01_PSAppDeployToolkit_Install.log"]);
        Assert.Equal(ImeLogChoice.Take, plan["DISM.log"]);
    }

    [Fact]
    public void Over_the_limit_other_older_copies_go_first_then_the_largest_logs_that_are_not_core_and_app_install_copies_last_never_a_core_current_file()
    {
        PackageRules.Taken T(string name, long compressed, int hoursAgo = 0) => new() { Name = name, Compressed = compressed, LastWriteUtc = T0.AddHours(-hoursAgo) };
        var taken = new[]
        {
            T("AppWorkload.log", 9_000_000),
            T("AppWorkload-20261112-080000.log", 3_000_000, 24),
            T("IntuneManagementExtension-20261111-080000.log", 2_000_000, 48),
            T("ClientHealth-20261110-080000.log", 3_000_000, 72),
            T("ClientHealth.log", 3_000_000),
            T("DeviceHealthMonitoring.log", 4_000_000),
            T("NotificationInfraLogs.log", 6_000_000),
            T("IntuneManagementExtension.log", 9_000_000),
        };
        // Well over: everything that may go goes, in the fixed order; the core current files stay.
        Assert.Equal(new[] { "ClientHealth-20261110-080000.log", "NotificationInfraLogs.log", "DeviceHealthMonitoring.log", "IntuneManagementExtension-20261111-080000.log", "AppWorkload-20261112-080000.log" },
            PackageRules.DropOrder(taken, 60_000_000, 10_000_000));
        // Just over: only as much as needed.
        Assert.Equal(new[] { "ClientHealth-20261110-080000.log" }, PackageRules.DropOrder(taken, 11_000_000, 10_000_000));
        Assert.Equal(new[] { "ClientHealth-20261110-080000.log", "NotificationInfraLogs.log" }, PackageRules.DropOrder(taken, 15_000_000, 10_000_000));
        Assert.Empty(PackageRules.DropOrder(taken, 9_000_000, 10_000_000));
    }

    [Fact]
    public void A_large_file_is_cut_to_its_newest_part_starting_at_a_whole_line()
    {
        var source = _folder.Combine("big.log");
        var lines = Enumerable.Range(1, 2000).Select(i => "<![LOG[line " + i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) + "]LOG]!>").ToArray();
        File.WriteAllLines(source, lines);
        var copy = _folder.Combine("copy.log");
        var kept = EnginePackage.CopyTailNoFollow(source, copy, 10_000);
        Assert.True(kept <= 10_000);
        var text = File.ReadAllText(copy);
        Assert.StartsWith("<![LOG[line ", text);
        Assert.EndsWith(lines.Last() + Environment.NewLine, text);
        // A small file is copied whole.
        var small = _folder.Combine("small.copy");
        Assert.Equal(new FileInfo(source).Length, EnginePackage.CopyTailNoFollow(source, small, 10_000_000));
    }

    /// <summary>Bytes that do not compress, so a zip's size is predictable.</summary>
    private static byte[] Noise(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        for (var i = 100; i < length; i += 100) { bytes[i] = (byte)'\n'; }
        return bytes;
    }

    private static List<string> ZipNames(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Entry(string zipPath, string name)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry(name).Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_package_takes_the_rules_logs_once_cuts_a_huge_one_keeps_under_the_limit_and_says_so_in_its_index()
    {
        var output = _folder.Combine("out");
        var ime = _folder.Combine("ime");
        var own = _folder.Combine("own");
        foreach (var d in new[] { output, ime, own }) { Directory.CreateDirectory(d); }
        // Many dated copies, a huge core log, the two left out by default, logs that are not core.
        File.WriteAllBytes(Path.Combine(ime, "AppWorkload.log"), Noise(60_000, 1));
        foreach (var day in new[] { "20261108", "20261109", "20261110", "20261111", "20261112" })
        {
            File.WriteAllBytes(Path.Combine(ime, "AppWorkload-" + day + "-080000.log"), Noise(50_000, day.GetHashCode()));
        }
        File.WriteAllBytes(Path.Combine(ime, "IntuneManagementExtension.log"), Noise(400_000, 2));
        File.WriteAllBytes(Path.Combine(ime, "Sensor.log"), Noise(300_000, 3));
        File.WriteAllBytes(Path.Combine(ime, "Win32AppInventory.log"), Noise(300_000, 4));
        File.WriteAllBytes(Path.Combine(ime, "DeviceHealthMonitoring.log"), Noise(90_000, 5));
        File.WriteAllBytes(Path.Combine(ime, "NotificationInfraLogs.log"), Noise(80_000, 6));
        // PSADT logs: one of last week, one of last year.
        File.WriteAllBytes(Path.Combine(ime, "PSADT_Contoso_Agent_1.0_x64_EN_01_Install_2026-11-10T144144.zip"), Noise(1_000, 7));
        File.WriteAllBytes(Path.Combine(ime, "PSADT_Contoso_Agent_0.9_x64_EN_01_install_2025-03-27-09-56-46.zip"), Noise(1_000, 8));
        var now = _h.Clock.UtcNow;
        File.SetLastWriteTimeUtc(Path.Combine(ime, "PSADT_Contoso_Agent_1.0_x64_EN_01_Install_2026-11-10T144144.zip"), now.AddDays(-6));
        File.SetLastWriteTimeUtc(Path.Combine(ime, "PSADT_Contoso_Agent_0.9_x64_EN_01_install_2025-03-27-09-56-46.zip"), now.AddDays(-400));
        // The dated copies were written on their days, the newest more than a day ago: only it is taken.
        var age = 5;
        foreach (var day in new[] { "20261108", "20261109", "20261110", "20261111", "20261112" })
        {
            File.SetLastWriteTimeUtc(Path.Combine(ime, "AppWorkload-" + day + "-080000.log"), now.AddDays(-age--).AddHours(-1));
        }
        // The engine log: runs of two versions, the older version in the rolled-over file; a time token written out.
        const string tail = "]LOG]!><time=\"10:00:00.000+0\" date=\"11-13-2026\" component=\"Engine.Test\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";
        const string caller = "S-1-12-1-111-222-333-444";
        File.WriteAllLines(Path.Combine(own, "EndpointToolbeltService.log"), new[] { "Run old00001: DiagnoseApps requested by " + caller + " (session 2, 0 other session(s))." });
        File.WriteAllLines(Path.Combine(own, "EndpointToolbelt.lo_"), new[] { "<![LOG[[old00001] Endpoint Toolbelt 1.1.1 engine started. Action: DiagnoseApps. Run old00001." + tail });
        File.WriteAllLines(Path.Combine(own, "EndpointToolbelt.log"), new[]
        {
            "<![LOG[[test0000] Endpoint Toolbelt 1.0.0 engine started. Action: CollectDiag. Run test0000." + tail,
            "<![LOG[[test0000] Contoso Agent: newest attempt [[t:2026-11-16T08:08:49Z;yyyy-MM-dd HH:mm]], started." + tail,
        });
        _h.Services.ImePaths.LogsFolder = ime;
        _h.Processes.Respond = request =>
        {
            if (request.Label == "mdmdiagnosticstool")
            {
                // The MDM tool's own copies of IME logs (all of them, Sensor too) and a file of its own.
                var zipPath = System.Text.RegularExpressions.Regex.Match(request.Arguments, "-zip \"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)).Groups[1].Value;
                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                foreach (var name in new[] { "Sensor.log", "AppWorkload-20261108-080000.log", "IntuneManagementExtension/Logs/Other.log" })
                {
                    using var s = zip.CreateEntry("IntuneManagementExtension/Logs/" + Path.GetFileName(name)).Open();
                    s.Write(new byte[] { 1, 2, 3 }, 0, 3);
                }
                using (var s = zip.CreateEntry("MDMDiagReport.html").Open()) { s.Write(new byte[] { 60, 104, 62 }, 0, 3); }
                return FakeProcessRunner.Exit(0);
            }
            if (request.Label == "dsregcmd /status") { return FakeProcessRunner.Exit(0, "AzureAdJoined : YES"); }
            throw new InvalidOperationException(request.Label + " must not run");
        };
        var run = _h.NewRun();
        run.OutputDirectory = output;
        run.LogDirectory = own;
        run.TimeLimitMinutes = 30;
        run.CallerSid = caller;
        var package = new TestPackage(run)
        {
            OnToolExists = path => path.EndsWith("mdmdiagnosticstool.exe", StringComparison.OrdinalIgnoreCase),
            OnReadAppState = () => new AppStateReadout(),
            OnReadRemediations = () => new RemediationState(),
            OnProfileErrors = () => new List<ProfileErrorRow>(),
            OnEspState = () => new List<EspRow>(),
            FileLimitBytes = 200_000,
            PackageLimitBytes = 450_000,
        };
        package.CollectDiag();

        var zipPath = Path.Combine(output, EnginePackage.PackageName);
        var size = new FileInfo(zipPath).Length;
        Assert.True(size <= 450_000, size + " bytes");
        var names = ZipNames(zipPath);
        // Never: older dated copies beyond the newest one, Sensor, Win32AppInventory.
        Assert.DoesNotContain("IMELogs/Sensor.log", names);
        Assert.DoesNotContain("IMELogs/Win32AppInventory.log", names);
        Assert.DoesNotContain("IMELogs/AppWorkload-20261108-080000.log", names);
        Assert.DoesNotContain("IMELogs/AppWorkload-20261111-080000.log", names);
        // The core logs stay; the huge one is cut to its newest part.
        Assert.Contains("IMELogs/AppWorkload.log", names);
        Assert.Contains("IMELogs/IntuneManagementExtension.log", names);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            Assert.True(zip.GetEntry("IMELogs/IntuneManagementExtension.log").Length <= 200_000);
            // The MDM diagnostics keep their own file, without their IME log copies.
            using var inner = new ZipArchive(zip.GetEntry("MDMDiagnostics.zip").Open(), ZipArchiveMode.Read);
            Assert.Equal(new[] { "MDMDiagReport.html" }, inner.Entries.Select(e => e.FullName));
        }
        var index = Entry(zipPath, "PackageIndex.txt");
        Assert.Contains("IMELogs/IntuneManagementExtension.log: ", index);
        Assert.Contains(", the newest part of 0.4 MB", index);
        Assert.Contains("Sensor.log (0.3 MB): left out by default", index);
        Assert.Contains("Win32AppInventory.log (0.3 MB): left out by default", index);
        Assert.Contains("AppWorkload-20261108-080000.log (0.0 MB): an older copy", index);
        Assert.Contains("MDMDiagnostics.zip: 3 IME logs", index);
        // Over the limit: the largest log that is not core went first; the app install log's older copy stays.
        Assert.Contains("A first zip was ", index);
        Assert.Contains("DeviceHealthMonitoring.log (0.1 MB): over the package limit of 0.4 MB", index);
        Assert.DoesNotContain("IMELogs/DeviceHealthMonitoring.log", names);
        Assert.Contains("IMELogs/AppWorkload-20261112-080000.log", names);
        // PSADT logs of the last 30 days only; the index says which were left out and why.
        Assert.Contains("IMELogs/PSADT_Contoso_Agent_1.0_x64_EN_01_Install_2026-11-10T144144.zip", names);
        Assert.DoesNotContain("IMELogs/PSADT_Contoso_Agent_0.9_x64_EN_01_install_2025-03-27-09-56-46.zip", names);
        Assert.Contains("PSADT_Contoso_Agent_0.9_x64_EN_01_install_2025-03-27-09-56-46.zip (0.0 MB): a PSADT log last written "
            + _h.Clock.UtcNow.AddDays(-400).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ", more than 30 days ago", index);
        // A real time with its zone, never a time token, in the index and in the engine log copy.
        Assert.Matches(@"Created \d{4}-\d\d-\d\d \d\d:\d\d UTC by run test0000\.", index);
        Assert.DoesNotContain("[[t:", index, StringComparison.Ordinal);
        var engineLog = Entry(zipPath, "EndpointToolbelt/EndpointToolbelt.log");
        Assert.Contains("newest attempt 2026-11-16 08:08 UTC, started.", engineLog);
        Assert.DoesNotContain("[[t:", engineLog, StringComparison.Ordinal);
        // The logs hold runs of two versions: the index says so, the older first.
        Assert.Contains("The logs hold runs of versions 1.1.1 and 1.0.0 (in the order they first appear).", index);
        // The requester's own SID is shortened in the service log copy.
        var serviceLog = Entry(zipPath, "EndpointToolbelt/EndpointToolbeltService.log");
        Assert.Contains("requested by User:0000006f (session 2", serviceLog);
        Assert.DoesNotContain(caller, serviceLog, StringComparison.Ordinal);
        // The run log has the size before and after.
        var log = _h.Records("progress").Select(r => r.Str("text")).ToList();
        Assert.Contains(log, t => t.StartsWith("Package size: ", StringComparison.Ordinal) && t.Contains("collected before the package rules") && t.Contains("zipped"));
        Assert.Contains(log, t => t.StartsWith("IME logs: ", StringComparison.Ordinal) && t.Contains("older copies") && t.Contains("left out by default"));
    }

    // --- Reset Company Portal data ------------------------------------------------------------------

    private sealed class FakeApps : ISessionApps
    {
        public bool Open { get; set; }
        public bool ClosesWhenAsked { get; set; }
        public int Asked { get; private set; }

        IReadOnlyList<string> ISessionApps.Open(IReadOnlyList<string> names) => Open && names.Contains(CompanyPortalReset.ProcessName) ? new[] { CompanyPortalReset.ProcessName } : Array.Empty<string>();

        public void AskToClose(string name)
        {
            Asked++;
            if (ClosesWhenAsked) { Open = false; }
        }

        public void Sleep(int milliseconds) { }
    }

    [Fact]
    public void Reset_Company_Portal_data_does_not_ask_when_it_is_closed_and_resets()
    {
        var apps = new FakeApps();
        var runner = new FakeProcessRunner();
        var asked = 0;
        var result = CompanyPortalReset.Run(runner, apps, () => { asked++; return true; });
        Assert.Equal(0, asked);
        Assert.Equal(0, apps.Asked);
        Assert.Single(runner.Requests);
        Assert.Contains("Company Portal was not running.", result.Card.Detail);
    }

    [Fact]
    public void Reset_Company_Portal_data_asks_first_and_changes_nothing_when_the_user_keeps_it_open()
    {
        var apps = new FakeApps { Open = true, ClosesWhenAsked = true };
        var runner = new FakeProcessRunner();
        var result = CompanyPortalReset.Run(runner, apps, () => false);
        Assert.Equal(CompanyPortalReset.CodeCancelled, result.Code);
        Assert.Equal(0, apps.Asked);
        Assert.Empty(runner.Requests);
        Assert.Equal("Reset Company Portal data: cancelled. Nothing was changed.", result.Status);
        Assert.Equal("2026-11-13 10:00 +01:00  Reset Company Portal data: cancelled, Company Portal kept open (6)",
            RunWords.HistoryLine("2026-11-13 10:00:00 +01:00;user;ResetCompanyPortal;6"));
    }

    [Fact]
    public void Reset_Company_Portal_data_never_forces_it_closed_and_stops_when_it_stays_open()
    {
        var apps = new FakeApps { Open = true, ClosesWhenAsked = false };
        var runner = new FakeProcessRunner();
        var lines = new List<string>();
        var result = CompanyPortalReset.Run(runner, apps, () => true, lines.Add);
        Assert.Equal(CompanyPortalReset.CodeStillOpen, result.Code);
        Assert.Equal(1, apps.Asked);
        Assert.Empty(runner.Requests);
        Assert.Equal(Tone.Warning, result.Tone);
        Assert.Contains("it was not closed by force", result.Card.Detail);
        Assert.Contains(lines, l => l.StartsWith("Company Portal is still open after 20 seconds", StringComparison.Ordinal));
        Assert.Equal("2026-11-13 10:00 +01:00  Reset Company Portal data: Company Portal did not close, nothing changed (7)",
            RunWords.HistoryLine("2026-11-13 10:00:00 +01:00;user;ResetCompanyPortal;7"));

        // Closed when asked: then the reset runs.
        var closes = new FakeApps { Open = true, ClosesWhenAsked = true };
        var reset = CompanyPortalReset.Run(runner, closes, () => true);
        Assert.Equal(1, closes.Asked);
        Assert.Single(runner.Requests);
        Assert.Contains("Company Portal was closed.", reset.Card.Detail);
        // The question says it plainly; the confirmation no longer says it closes it.
        Assert.StartsWith("Company Portal is open. Close it to continue?", Confirmations.CloseCompanyPortal);
        Assert.DoesNotContain("it closes Company Portal", Confirmations.CompanyPortalReset);
    }

    [Fact]
    public void Full_IME_reset_uses_the_same_question_and_says_in_the_history_why_it_did_not_start()
    {
        // The shared question: not open, cancelled, still open, closed.
        Assert.Equal(PortalClose.NotOpen, CompanyPortalClose.Ask(new FakeApps(), () => throw new InvalidOperationException("not asked"), null));
        var cancelled = new FakeApps { Open = true, ClosesWhenAsked = true };
        Assert.Equal(PortalClose.Cancelled, CompanyPortalClose.Ask(cancelled, () => false, null));
        Assert.Equal(0, cancelled.Asked);
        Assert.Equal(PortalClose.StillOpen, CompanyPortalClose.Ask(new FakeApps { Open = true }, () => true, null));
        Assert.Equal(PortalClose.Closed, CompanyPortalClose.Ask(new FakeApps { Open = true, ClosesWhenAsked = true }, () => true, null));
        Assert.Equal("2026-11-13 10:00 +01:00  Full IME reset: not started: Company Portal kept open, nothing changed",
            RunWords.HistoryLine("2026-11-13 10:00:00 +01:00;user;FullReset;" + Confirmations.FullResetPortalCancelled));
        Assert.Equal("2026-11-13 10:00 +01:00  Full IME reset: not started: Company Portal did not close, nothing changed",
            RunWords.HistoryLine("2026-11-13 10:00:00 +01:00;user;FullReset;" + Confirmations.FullResetPortalStillOpen));
        Assert.Contains("never closed by force", Confirmations.Text(EndpointToolbelt.ToolbeltAction.FullReset));
        Assert.Contains("never by force", ToolCatalog.ByButton("FullResetButton").Changes);
    }

    [Fact]
    public void The_source_of_the_reset_never_ends_the_process()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "EndpointToolbelt.App", "Core", "UserActions.cs"));
        var start = source.IndexOf("public static class CompanyPortalReset", StringComparison.Ordinal);
        var end = source.IndexOf("public static class Confirmations", StringComparison.Ordinal);
        Assert.DoesNotContain(".Kill(", source.Substring(start, end - start));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    // --- IME not installed ----------------------------------------------------------------------------

    private sealed class Source : IHealthSource
    {
        public string Ime { get; set; }
        public DateTime UtcNow => T0;
        public string ImeServiceStatus() => Ime;
        public DsregInfo Dsreg() => new() { AzureAdJoined = "YES", AzureAdPrt = "YES" };
        public DateTime? MdmCertificateExpiryUtc() => T0.AddDays(300);
        public bool CanReach(string host, int port, int timeoutMs) => true;
        public double FreeSystemDriveGB() => 100;
        public bool ServicingRestartPending() => false;
    }

    private static Protocol.StatusInfo NoImeLog() => new() { Enrolled = true, LastSyncUtc = T0.AddHours(-1).ToString("o"), ImeCheckInUnavailable = "missing" };

    [Fact]
    public void Without_the_IME_no_row_step_card_or_status_offers_Retry_apps_and_policies()
    {
        var detailed = HealthChecks.Run(new Source { Ime = null }, NoImeLog(), out _);
        var service = detailed.Single(r => r.Name == HealthChecks.ImeService);
        var checkIn = detailed.Single(r => r.Name == HealthChecks.ImeCheckIn);
        foreach (var row in new[] { service, checkIn })
        {
            Assert.True(row.ImeNotInstalled, row.Name);
            Assert.Equal(HealthFix.None, row.Fix);
            Assert.Equal(HealthChecks.ImeNotInstalledStep, HealthChecks.NextStep(row));
            Assert.Equal(HealthChecks.ImeNotInstalledHint, row.Hint);
        }
        // The window's combined row, Home's top card, the status, the cards for Copy details for IT.
        var rows = HealthView.Rows(detailed);
        var installer = rows.Single(r => r.Name == HealthView.AppInstaller);
        Assert.Equal(HealthFix.None, installer.Fix);
        Assert.True(installer.ImeNotInstalled);
        Assert.Equal("The Intune Management Extension is not installed: this device has not received a Win32 app, script or remediation from Intune yet, and Intune installs it with the first one.", installer.HintBelow);
        var home = HomeSummary.Build(rows, null, serviceMissing: false);
        Assert.Equal(HomeStep.None, home.Step);
        Assert.Equal("", home.StepText);
        // (Other rows keep their own steps: the configuration profiles, still being read here, may offer a sync.)
        var imeRows = rows.Where(r => r.ImeNotInstalled).ToList();
        var all = string.Join(" ", new[] { HealthChecks.Summary(rows).Text, home.Title, home.Text }
            .Concat(HealthChecks.Cards(imeRows).SelectMany(c => new[] { c.Title, c.Detail, c.NextStep, c.Support }))
            .Concat(imeRows.Select(r => r.HintBelow)));
        Assert.DoesNotContain("Retry", all);
        Assert.StartsWith("1 problem found. App installer (IME): nothing to do on this device", HealthChecks.Summary(rows).Text);
        // Home's title and line in plain words never ask for a new try either, and name no technical word.
        Assert.Equal(HealthView.AppInstaller, home.Row);
        Assert.True(home.RowImeNotInstalled);
        Assert.Equal("Work apps can't be installed yet", HomeSummary.PlainTitle(home));
        Assert.Equal("Your organization sets this up with the first work app it sends, so there is nothing to do on this PC.", HomeSummary.PlainText(home));
        Assert.False(HomeSummary.HasTechnicalWords(HomeSummary.PlainTitle(home) + " " + HomeSummary.PlainText(home)));
    }

    [Fact]
    public void An_installed_IME_that_stopped_still_offers_Retry_apps_and_policies()
    {
        var detailed = HealthChecks.Run(new Source { Ime = "Stopped" }, NoImeLog(), out _);
        var service = detailed.Single(r => r.Name == HealthChecks.ImeService);
        Assert.False(service.ImeNotInstalled);
        Assert.Equal(HealthFix.RetryAppsAndPolicies, service.Fix);
        Assert.Equal(HealthFix.RetryAppsAndPolicies, HealthView.Rows(detailed).Single(r => r.Name == HealthView.AppInstaller).Fix);
        Assert.False(HomeSummary.Build(HealthView.Rows(detailed), null, serviceMissing: false).RowImeNotInstalled);
        Assert.Equal("The part of Windows that installs work apps needs a new try.", HomeSummary.PlainRow(HealthView.AppInstaller, HealthStatus.Fail).Line);
    }

    [Fact]
    public void Without_the_IME_the_app_path_shows_the_installer_row_and_no_Retry_section()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var detailed = HealthChecks.Run(new Source { Ime = null }, NoImeLog(), out var dsreg);
            MainWindowPagesTests.Invoke(window, "ApplyHealth", NoImeLog(), false, detailed, dsreg, false);
            MainWindowPagesTests.Call(window, "ShowAppChecks");
            var checks = ((IEnumerable<HealthRow>)MainWindowPagesTests.Get<System.Windows.Controls.ItemsControl>(window, "AppChecks").ItemsSource).ToList();
            Assert.Equal(HealthView.PlainName(HealthView.AppInstaller), checks[0].Name);
            Assert.Equal(Visibility.Collapsed, checks[0].FixVisibility);
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<System.Windows.Controls.StackPanel>(window, "AppsRetry").Visibility);

            // With the IME the Retry section is there again.
            MainWindowPagesTests.Invoke(window, "ApplyHealth", NoImeLog(), false, CompactLayoutTests.Health(), dsreg, false);
            MainWindowPagesTests.Call(window, "ShowAppChecks");
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<System.Windows.Controls.StackPanel>(window, "AppsRetry").Visibility);
        });
    }
}
