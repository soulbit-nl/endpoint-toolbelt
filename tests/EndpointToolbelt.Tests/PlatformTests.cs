using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.Engine;
using EndpointToolbelt.Platform;
using EndpointToolbelt.Service.State;
using Xunit;

namespace EndpointToolbelt.Tests;

public class FileSystemSafetyTests
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User;
    private static readonly FolderPolicy MyPolicy = new(new[] { Me }, new[] { Me });

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr reserved);

    [Fact]
    public void Delete_removes_a_junction_but_never_its_target()
    {
        using var t = new TestFolder();
        var outside = t.Combine("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep");

        var tree = t.Combine("tree");
        Directory.CreateDirectory(Path.Combine(tree, "sub"));
        File.WriteAllText(Path.Combine(tree, "sub", "a.txt"), "x");
        TestFolder.CreateJunction(Path.Combine(tree, "sub", "link"), outside);

        FileSystemSafety.DeleteTreeNoFollow(tree);

        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
    }

    [Fact]
    public void Delete_of_a_root_that_is_a_junction_removes_only_the_link()
    {
        using var t = new TestFolder();
        var outside = t.Combine("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep");
        var link = t.Combine("link");
        TestFolder.CreateJunction(link, outside);

        FileSystemSafety.DeleteTreeNoFollow(link);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
    }

    [Fact]
    public void Reparse_points_anywhere_in_the_path_are_detected()
    {
        using var t = new TestFolder();
        var real = t.Combine("real");
        Directory.CreateDirectory(Path.Combine(real, "deep"));
        var link = t.Combine("link");
        TestFolder.CreateJunction(link, real);
        Assert.True(FileSystemSafety.HasReparsePointInPath(Path.Combine(link, "deep")));
        Assert.False(FileSystemSafety.HasReparsePointInPath(Path.Combine(real, "deep")));
    }

    [Fact]
    public void Open_refuses_hard_linked_files()
    {
        using var t = new TestFolder();
        var original = t.Combine("original.txt");
        File.WriteAllText(original, "secret");
        var alias = t.Combine("alias.txt");
        Assert.True(CreateHardLink(alias, original, IntPtr.Zero));

        Assert.Throws<LinkRefusedException>(() => FileSystemSafety.OpenReadNoFollow(alias));
        Assert.Throws<LinkRefusedException>(() => FileSystemSafety.OpenAppendNoFollow(original));
    }

    [Fact]
    public void Append_creates_and_appends_to_a_plain_file()
    {
        using var t = new TestFolder();
        var file = t.Combine("log.txt");
        using (var s = FileSystemSafety.OpenAppendNoFollow(file)) { s.Write(new byte[] { 65 }, 0, 1); }
        using (var s = FileSystemSafety.OpenAppendNoFollow(file)) { s.Write(new byte[] { 66 }, 0, 1); }
        Assert.Equal("AB", File.ReadAllText(file));
    }

    [Fact]
    public void A_product_log_file_is_readable_only_by_system_administrators_and_its_writer()
    {
        using var t = new TestFolder();
        var file = t.Combine("EndpointToolbeltService.log");
        File.WriteAllText(file, "x");
        var allowed = FileSystemSafety.LogReaders();
        Assert.False(FileSystemSafety.OnlyAllows(File.GetAccessControl(file), allowed));

        Assert.True(FileSystemSafety.ProtectLogFile(file));

        var security = File.GetAccessControl(file);
        Assert.True(security.AreAccessRulesProtected);
        Assert.True(FileSystemSafety.OnlyAllows(security, allowed));
        Assert.DoesNotContain(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
        Assert.Equal("x", File.ReadAllText(file));
        Assert.False(FileSystemSafety.ProtectLogFile(t.Combine("missing.log")));
    }

    [Fact]
    public void Protecting_a_log_file_refuses_a_hard_link()
    {
        using var t = new TestFolder();
        var original = t.Combine("original.txt");
        File.WriteAllText(original, "x");
        var alias = t.Combine("EndpointToolbeltService.log");
        Assert.True(CreateHardLink(alias, original, IntPtr.Zero));

        Assert.Throws<LinkRefusedException>(() => FileSystemSafety.ProtectLogFile(alias));
        Assert.False(File.GetAccessControl(original).AreAccessRulesProtected);
    }

    [Fact]
    public void The_service_log_protects_its_file_and_the_rolled_over_one()
    {
        using var t = new TestFolder();
        var path = t.Combine("EndpointToolbeltService.log");
        var old = Path.ChangeExtension(path, ".lo_");
        File.WriteAllText(old, "from an older version");

        new EndpointToolbelt.Service.Logging.CmTraceLog(path, "Test").Info("first line");

        var allowed = FileSystemSafety.LogReaders();
        Assert.True(FileSystemSafety.OnlyAllows(File.GetAccessControl(path), allowed));
        Assert.True(FileSystemSafety.OnlyAllows(File.GetAccessControl(old), allowed));
    }

    [NonElevatedFact]
    public void Born_locked_folder_matches_its_policy_and_tampering_is_detected()
    {
        using var t = new TestFolder();
        var folder = t.Combine("locked");
        FileSystemSafety.EnsureProtectedFolder(folder, MyPolicy);
        Assert.Null(FileSystemSafety.ValidateFolder(folder, MyPolicy));

        // Add an extra entry: the folder no longer matches and is replaced on the next ensure.
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify, AccessControlType.Allow));
        info.SetAccessControl(security);
        Assert.NotNull(FileSystemSafety.ValidateFolder(folder, MyPolicy));

        FileSystemSafety.EnsureProtectedFolder(folder, MyPolicy);
        Assert.Null(FileSystemSafety.ValidateFolder(folder, MyPolicy));
        Assert.Single(Directory.GetDirectories(t.Path, "locked.rejected-*"));
    }

    [NonElevatedFact]
    public void A_junction_at_the_protected_path_is_moved_aside_not_followed()
    {
        using var t = new TestFolder();
        var outside = t.Combine("outside");
        Directory.CreateDirectory(outside);
        var folder = t.Combine("locked");
        TestFolder.CreateJunction(folder, outside);

        FileSystemSafety.EnsureProtectedFolder(folder, MyPolicy);

        Assert.Equal(FileAttributes.Directory, new DirectoryInfo(folder).Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint));
        Assert.Null(FileSystemSafety.ValidateFolder(folder, MyPolicy));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void A_user_owned_folder_is_not_trusted_as_a_log_folder()
    {
        using var t = new TestFolder();
        Assert.NotNull(FileSystemSafety.ValidateForeignFolder(t.Path));
        Assert.Equal("missing", FileSystemSafety.ValidateForeignFolder(t.Combine("nope")));
    }

    [Fact]
    public void The_service_does_not_trust_an_install_folder_users_can_change()
    {
        using var t = new TestFolder();
        File.WriteAllText(t.Combine("EndpointToolbelt.Service.exe"), "x");
        Assert.StartsWith("the install folder is not protected", FileSystemSafety.ValidateInstallFolder(t.Path));

        // A folder Windows protects: owned by TrustedInstaller, read-only for everyone else.
        var windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0");
        Assert.Null(FileSystemSafety.ValidateInstallFolder(windowsPowerShell));
    }

    [Fact]
    public void The_engine_check_tells_a_file_in_use_from_a_link()
    {
        using var t = new TestFolder();
        var path = t.Combine("EndpointToolbelt.Engine.exe");
        File.WriteAllText(path, "x");
        var file = new FileInfo(path);
        Assert.Null(FileSystemSafety.OpenProblem(file));

        // Another process holds it without sharing: it could not be opened, which can pass.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var inUse = FileSystemSafety.OpenProblem(file);
            Assert.True(inUse.CouldNotOpen);
            Assert.StartsWith("EndpointToolbelt.Engine.exe could not be opened (Windows error 32: ", inUse.Problem, StringComparison.Ordinal);
        }

        // A link or a hard link cannot pass a second check.
        var alias = t.Combine("alias.dll");
        Assert.True(CreateHardLink(alias, path, IntPtr.Zero));
        Assert.Equal("alias.dll is a link or has more than one hard link", FileSystemSafety.OpenProblem(new FileInfo(alias)).Problem);
        var link = FileSystemSafety.OpenProblem(file, _ => throw new LinkRefusedException("Refusing it."));
        Assert.False(link.CouldNotOpen);
        Assert.Equal("EndpointToolbelt.Engine.exe is a link or has more than one hard link", link.Problem);
        Assert.True(FileSystemSafety.OpenProblem(file, _ => throw new UnauthorizedAccessException()).CouldNotOpen);
    }

    [Fact]
    public void The_service_does_not_trust_an_install_folder_whose_publisher_folder_users_can_change()
    {
        DirectorySecurity Acl(bool usersMayChange)
        {
            var s = new DirectorySecurity();
            s.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            s.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            s.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                usersMayChange ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            return s;
        }
        var install = @"C:\Program Files\Contoso\Endpoint Toolbelt";
        var checkedFolders = new List<string>();
        Assert.Null(FileSystemSafety.ValidateInstallParents(install, d => { checkedFolders.Add(d.FullName); return Acl(false); }));
        // The publisher folder and Program Files; never the volume root.
        Assert.Equal(new[] { @"C:\Program Files\Contoso", @"C:\Program Files" }, checkedFolders);

        var reason = FileSystemSafety.ValidateInstallParents(install, d => Acl(d.Name == "Contoso"));
        Assert.StartsWith(@"a parent of the install folder is not protected (C:\Program Files\Contoso:", reason);
    }
}

public class EngineRunnerTests
{
    private static EngineStartInfo PowerShell(string scriptPath, TimeSpan limit) => new()
    {
        FileName = TestShell.WindowsPowerShell,
        Arguments = { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath },
        WorkingDirectory = Path.GetDirectoryName(scriptPath),
        Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System),
            ["TEMP"] = Path.GetTempPath(),
            ["TMP"] = Path.GetTempPath(),
            ["USERPROFILE"] = Environment.GetEnvironmentVariable("USERPROFILE"),
            ["LOCALAPPDATA"] = Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            ["APPDATA"] = Environment.GetEnvironmentVariable("APPDATA"),
            ["ENDPOINTTOOLBELT_TEST"] = "yes",
        },
        TimeLimit = limit,
    };

    [Fact]
    public void Captures_records_other_output_errors_and_the_exit_code()
    {
        using var t = new TestFolder();
        var script = t.Combine("engine.ps1");
        File.WriteAllText(script,
            "Write-Output '{\"type\":\"progress\",\"text\":\"one\"}'\r\n" +
            "Write-Output 'plain text'\r\n" +
            "[Console]::Error.WriteLine('to stderr')\r\n" +
            "Write-Output ('{\"type\":\"env\",\"value\":\"' + $env:ENDPOINTTOOLBELT_TEST + '\",\"psmp\":\"' + ($env:PSModulePath -ne $null) + '\"}')\r\n" +
            "exit 2\r\n");
        var records = new List<string>();

        var result = new JobObjectEngineRunner().Run(PowerShell(script, TimeSpan.FromSeconds(60)), records.Add, CancellationToken.None);

        Assert.Equal(EngineOutcome.Exited, result.Outcome);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(2, records.Count);
        Assert.Contains("\"text\":\"one\"", records[0]);
        Assert.Contains("\"value\":\"yes\"", records[1]);
        Assert.Contains("plain text", result.OtherOutputTail);
        Assert.Contains("to stderr", result.ErrorTail);
    }

    [Fact]
    public void Time_limit_kills_the_whole_process_tree()
    {
        using var t = new TestFolder();
        var script = t.Combine("hang.ps1");
        // Start a child process, report its PID, then hang.
        File.WriteAllText(script,
            // Without the shell: a build machine without an interactive desktop cannot use Start-Process -WindowStyle.
            "$si = New-Object System.Diagnostics.ProcessStartInfo(\"$env:SystemRoot\\System32\\ping.exe\", '-n 120 127.0.0.1')\r\n" +
            "$si.UseShellExecute = $false; $si.CreateNoWindow = $true\r\n" +
            "$p = [System.Diagnostics.Process]::Start($si)\r\n" +
            "Write-Output ('{\"type\":\"child\",\"pid\":' + $p.Id + '}')\r\n" +
            "Start-Sleep -Seconds 120\r\n");
        var records = new List<string>();

        // The first Windows PowerShell start on a fresh build machine can take well over a minute
        // (a CI runner once needed more than the 100-second limit below); one warm-up start first,
        // so the timed run measures the runner, not the machine.
        using (var warmUp = Process.Start(new ProcessStartInfo(TestShell.WindowsPowerShell, "-NoLogo -NoProfile -NonInteractive -Command exit") { UseShellExecute = false, CreateNoWindow = true }))
        {
            warmUp.WaitForExit(240_000);
        }
        var sw = Stopwatch.StartNew();

        // Long enough for a slow start to report its child; the script itself sleeps longer than the limit.
        var result = new JobObjectEngineRunner().Run(PowerShell(script, TimeSpan.FromSeconds(100)), records.Add, CancellationToken.None);

        Assert.Equal(EngineOutcome.TimedOut, result.Outcome);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(150), "The runner did not stop at its time limit.");
        Assert.True(records.Count == 1, $"Expected one child record; output: '{string.Join(" | ", result.OtherOutputTail)}', errors: '{string.Join(" | ", result.ErrorTail)}'.");
        var childRecord = records[0];
        var pid = int.Parse(childRecord.Split(':').Last().TrimEnd('}'));
        Thread.Sleep(500);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    [Fact]
    public void Cancellation_stops_the_engine()
    {
        using var t = new TestFolder();
        var script = t.Combine("sleep.ps1");
        File.WriteAllText(script, "Start-Sleep -Seconds 60\r\n");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = new JobObjectEngineRunner().Run(PowerShell(script, TimeSpan.FromMinutes(5)), _ => { }, cts.Token);
        Assert.Equal(EngineOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public void A_missing_executable_is_reported_as_failed_to_start()
    {
        var info = new EngineStartInfo
        {
            FileName = @"C:\does\not\exist.exe",
            WorkingDirectory = Path.GetTempPath(),
            Environment = new Dictionary<string, string> { ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot") },
            TimeLimit = TimeSpan.FromSeconds(5),
        };
        var result = new JobObjectEngineRunner().Run(info, _ => { }, CancellationToken.None);
        Assert.Equal(EngineOutcome.FailedToStart, result.Outcome);
        Assert.NotNull(result.StartError);
    }
}

public class ScriptPolicyTests
{
    [Fact]
    public void Policy_failures_on_stderr_are_recognized()
    {
        Assert.True(EndpointToolbelt.Platform.ScriptPolicy.IsPolicyFailure(new[] { "File x.ps1 cannot be loaded. The file is not digitally signed." }));
        Assert.True(EndpointToolbelt.Platform.ScriptPolicy.IsPolicyFailure(new[] { "+ FullyQualifiedErrorId : UnauthorizedAccess" }));
        Assert.False(EndpointToolbelt.Platform.ScriptPolicy.IsPolicyFailure(new[] { "some other error" }));
    }
}

public class DeviceStateReaderTests
{
    private sealed class Registry : IRegistryReader
    {
        public Dictionary<string, object> State { get; } = new(StringComparer.OrdinalIgnoreCase);
        public object GetValue(string subKey, string name) =>
            subKey == EndpointToolbelt.ProductInfo.StateKey && State.TryGetValue(name, out var v) ? v : null;
    }

    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Last_MDM_sync_ignores_events_written_from_a_user_session()
    {
        // Session 0 and a writer that has exited count; a writer in a user session does not.
        Assert.True(DeviceStateReader.IsTrustedWriter(100, _ => 0));
        Assert.True(DeviceStateReader.IsTrustedWriter(100, _ => null));
        Assert.True(DeviceStateReader.IsTrustedWriter(null, _ => 1));
        Assert.False(DeviceStateReader.IsTrustedWriter(100, _ => 1));
    }

    [Fact]
    public void Restart_marker_counts_only_after_the_last_boot()
    {
        var r = new Registry();
        r.State["RestartRequiredSince"] = "2026-10-15T10:00:00.0000000+00:00";
        r.State["RestartRequiredBy"] = "RepairWU";
        var afterBoot = new DeviceStateReader(r, () => Now, () => Now.AddHours(-5), () => Array.Empty<(string, int)>());
        Assert.Equal(new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc), afterBoot.GetRestartRequired().SinceUtc);
        var restarted = new DeviceStateReader(r, () => Now, () => Now.AddHours(-1), () => Array.Empty<(string, int)>());
        Assert.Null(restarted.GetRestartRequired().SinceUtc);
        var unknownBoot = new DeviceStateReader(r, () => Now, () => throw new InvalidOperationException(), () => Array.Empty<(string, int)>());
        Assert.Null(unknownBoot.GetRestartRequired().SinceUtc);
    }

    [Fact]
    public void Last_sync_is_reported_within_thirty_days_only()
    {
        var r = new Registry();
        DeviceStateReader With(Func<(DateTime?, string)> last) => new(r, () => Now, () => Now, () => Array.Empty<(string, int)>(), last);
        (DateTime? Utc, string Source) none = (null, null);
        Assert.Equal(((DateTime?)Now.AddDays(-2), "MDM session log"), With(() => (Now.AddDays(-2), "MDM session log")).GetLastSync());
        Assert.Equal(none, With(() => (Now.AddDays(-31), "MDM session log")).GetLastSync());
        Assert.Equal(none, With(() => (null, "MDM session log")).GetLastSync());
        // A log that cannot be read is told apart from no sync.
        Assert.Equal(((DateTime?)null, DeviceStateReader.LastSyncUnreadable), With(() => throw new UnauthorizedAccessException()).GetLastSync());
    }

    [Fact]
    public void Offsets_are_respected()
    {
        Assert.Equal(new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc), DeviceStateReader.ParseTimestamp("2026-10-15T10:00:00+02:00"));
        Assert.Null(DeviceStateReader.ParseTimestamp("not a date"));
        Assert.Null(DeviceStateReader.ParseTimestamp(5));
    }

    [Fact]
    public void Cooldowns_are_listed_only_while_active()
    {
        var r = new Registry();
        r.State["LastFullReset"] = Now.AddMinutes(-30).ToString("o");
        r.State["LastWURepair"] = Now.AddHours(-5).ToString("o");
        var reader = new DeviceStateReader(r, () => Now, () => Now.AddDays(-1), () => Array.Empty<(string, int)>());
        var list = reader.GetCooldowns(new ServiceConfig());
        var item = Assert.Single(list);
        Assert.Equal(ToolbeltAction.FullReset, item.Action);
        // A cooldown of 0 hours is off: never listed.
        Assert.Empty(reader.GetCooldowns(new ServiceConfig { FullResetCooldownHours = 0, WURepairCooldownHours = 0 }));
    }

    [Fact]
    public void Enrollment_needs_exactly_one_active_mdm_enrollment()
    {
        DeviceStateReader With(params (string, int)[] e) => new(new Registry(), () => Now, () => Now, () => e);
        Assert.False(With().IsEnrolled());
        Assert.False(With(("MS DM Server", 0)).IsEnrolled());
        Assert.True(With(("MS DM Server", 1), ("Microsoft Device Management", 1)).IsEnrolled());
        Assert.Null(With(("MS DM Server", 1), ("MS DM Server", 1)).IsEnrolled());
    }
}

/// <summary>The encoding console tools are read in (dsregcmd as SYSTEM gave code page 1 through the culture).</summary>
public class ConsoleEncodingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(42)]
    [InlineData(-1)]
    [InlineData(12345)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void A_placeholder_or_unknown_code_page_falls_back_to_437_without_throwing(int codePage)
    {
        Assert.Null(ConsoleEncoding.TryGet(codePage));
        Assert.Equal(437, ConsoleEncoding.FromCodePage(codePage).CodePage);
    }

    [Theory]
    [InlineData(437)]
    [InlineData(850)]
    [InlineData(65001)]
    public void A_real_code_page_is_used_as_it_is(int codePage)
    {
        Assert.Equal(codePage, ConsoleEncoding.FromCodePage(codePage).CodePage);
    }

    [Fact]
    public void Decodes_OEM_text_and_ends_in_UTF8_when_the_fallback_is_not_usable_either()
    {
        // An e with an acute accent is 0x82 in code page 850, but another letter in an ANSI code page.
        Assert.Equal("\u00E9", ConsoleEncoding.FromCodePage(850).GetString(new byte[] { 0x82 }));
        Assert.Equal(65001, ConsoleEncoding.Resolve(1, 1).CodePage);
        Assert.Equal(65001, ConsoleEncoding.Resolve(12345, -1).CodePage);
        // This system's own OEM code page is a real one.
        Assert.NotNull(ConsoleEncoding.TryGet(ConsoleEncoding.SystemOemCodePage()));
        Assert.NotNull(ConsoleEncoding.Oem());
    }
}
