using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.Engine;
using EndpointToolbelt.Service.Logging;
using EndpointToolbelt.Platform;
using Xunit;

namespace EndpointToolbelt.Tests;

public class OutputReadingTests
{
    [Fact]
    public void Over_long_lines_are_dropped_and_reading_continues()
    {
        var input = "short\n" + new string('x', 100) + "\nafter\r\nlast";
        var lines = new List<string>();
        JobObjectEngineRunner.ReadLines(new StringReader(input), 50, lines.Add);
        Assert.Equal(new[] { "short", "after", "last" }, lines);
    }

    [Fact]
    public void Non_ascii_engine_output_arrives_intact_when_the_engine_writes_utf8()
    {
        using var t = new TestFolder();
        var script = t.Combine("engine.ps1");
        // The engine contract: set UTF-8 output first. The script itself is ASCII and builds the
        // non-ASCII text from code points.
        File.WriteAllText(script,
            "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)\r\n" +
            "$text = 'caf' + [char]0x00E9 + ' ' + [char]0x0141 + 'odz' + [char]0x0105\r\n" +
            "Write-Output ('{\"type\":\"progress\",\"text\":\"' + $text + '\"}')\r\n", Encoding.ASCII);
        var info = new EngineStartInfo
        {
            FileName = TestShell.WindowsPowerShell,
            Arguments = { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script },
            WorkingDirectory = t.Path,
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
                ["TEMP"] = Path.GetTempPath(),
                ["USERPROFILE"] = Environment.GetEnvironmentVariable("USERPROFILE"),
                ["LOCALAPPDATA"] = Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            },
            // A cold Windows PowerShell start on a CI runner took over a minute.
            TimeLimit = TimeSpan.FromSeconds(180),
        };
        var records = new List<string>();
        var result = new JobObjectEngineRunner().Run(info, records.Add, CancellationToken.None);
        Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} ({result.Outcome}); output: '{result.OtherOutputTail}', errors: '{result.ErrorTail}'.");
        Assert.Contains("caf\u00e9 \u0141odz\u0105", Assert.Single(records));
    }
}

public class CmTraceLogTests
{
    [Fact]
    public void Writes_one_bom_appends_lines_and_rolls_over()
    {
        using var t = new TestFolder();
        var path = t.Combine("svc.log");
        var log = new CmTraceLog(path, "Service", maxBytes: 600);
        log.Info("first");
        log.Warn("second");
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        Assert.Equal(1, CountBom(bytes));
        var text = File.ReadAllText(path);
        Assert.Contains("<![LOG[first]LOG]!>", text);
        Assert.Contains("type=\"2\"", text);

        for (var i = 0; i < 10; i++) { log.Info("filler line " + i); }
        Assert.True(File.Exists(Path.ChangeExtension(path, ".lo_")));
        Assert.True(new FileInfo(path).Length < 600 + 400);
    }

    [Fact]
    public void Bias_follows_the_cmtrace_convention()
    {
        var line = CmTraceFile.FormatLine("x", 1, "C", new DateTimeOffset(2026, 10, 15, 14, 30, 0, TimeSpan.FromHours(2)), 7);
        Assert.Contains("time=\"14:30:00.000-120\"", line);
        Assert.Contains("date=\"10-15-2026\"", line);
        var west = CmTraceFile.FormatLine("x", 1, "C", new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.FromHours(-5)), 7);
        Assert.Contains("time=\"08:00:00.000+300\"", west);
    }

    private static int CountBom(byte[] b)
    {
        var count = 0;
        for (var i = 0; i + 2 < b.Length; i++) { if (b[i] == 0xEF && b[i + 1] == 0xBB && b[i + 2] == 0xBF) { count++; } }
        return count;
    }
}

public class ConfigAndLocationTests
{
    private sealed class CountingRegistry : IRegistryReader
    {
        public int Reads;
        public object Value;
        public object GetValue(string subKey, string name)
        {
            Interlocked.Increment(ref Reads);
            return subKey == ProductInfo.ConfigKey && name == "UserRateLimitPerHour" ? Value : null;
        }
    }

    [Fact]
    public void Configuration_is_cached_and_warnings_are_reported_once_per_change()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var registry = new CountingRegistry { Value = "999" };
        var warnings = new List<string>();
        var provider = new ConfigProvider(registry, TimeSpan.FromSeconds(30), warnings.Add, () => now);

        provider.Get();
        var reads = registry.Reads;
        provider.Get();
        Assert.Equal(reads, registry.Reads);
        Assert.Single(warnings);

        now = now.AddMinutes(1);
        provider.Get();
        Assert.True(registry.Reads > reads);
        Assert.Single(warnings); // same warning, not repeated

        registry.Value = "5";
        now = now.AddMinutes(1);
        Assert.Equal(5, provider.Get().UserRateLimitPerHour);
    }

    [Fact]
    public void Log_location_falls_back_and_reports_each_change_once()
    {
        var reason = "writable by other principals";
        var rejected = new List<string>();
        var location = new LogLocation(@"C:\preferred", @"C:\fallback", rejected.Add, _ => reason);
        Assert.Equal(@"C:\fallback", location.Current);
        Assert.Equal(@"C:\fallback", location.Current);
        Assert.Single(rejected);

        var trusted = new LogLocation(@"C:\preferred", @"C:\fallback", rejected.Add, _ => null);
        Assert.Equal(@"C:\preferred", trusted.Current);
    }
}

public class FolderRuleTests
{
    [Theory]
    [InlineData("NUL.zip", true)]
    [InlineData("con", true)]
    [InlineData("COM1.tar.zip", true)]
    [InlineData("EndpointToolbelt_2026-10-15_1200.zip", false)]
    [InlineData("console.zip", false)]
    public void Device_names_are_recognized(string name, bool reserved)
    {
        Assert.Equal(reserved, FileSystemSafety.IsReservedDeviceName(name));
    }

    [Theory]
    [InlineData("GW")] // generic write, as found on inherit-only entries
    [InlineData("GA")] // generic all
    [InlineData("0x1301bf")] // modify
    [InlineData("WD")] // write data / add file
    public void Generic_and_specific_write_rights_for_users_are_rejected(string rights)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm($"O:SYD:(A;OICI;FA;;;SY)(A;OICIIO;{rights};;;BU)");
        Assert.NotNull(FileSystemSafety.ValidateTrustedSecurity(security));
    }

    [Fact]
    public void Read_only_access_for_users_is_accepted()
    {
        var security = new DirectorySecurity();
        security.SetOwner(WellKnownSids.TrustedInstaller);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        Assert.Null(FileSystemSafety.ValidateTrustedSecurity(security));
    }

    [Fact]
    public void An_engine_in_a_user_writable_folder_is_not_trusted()
    {
        using var t = new TestFolder();
        var engine = t.Combine("Engine");
        Directory.CreateDirectory(engine);
        File.WriteAllText(Path.Combine(engine, "x.ps1"), "1");
        Assert.NotNull(FileSystemSafety.ValidateEngineTree(engine));
        Assert.NotNull(FileSystemSafety.ValidateEngineTree(t.Combine("missing")));
    }

    [Fact]
    public void Subfolders_with_their_own_permissions_are_replaced()
    {
        using var t = new TestFolder();
        var sub = t.Combine("Results");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "old.json"), "x");
        Assert.Null(FileSystemSafety.ValidateInheritingFolder(sub));
        FileSystemSafety.EnsureInheritingFolder(sub);
        Assert.True(File.Exists(Path.Combine(sub, "old.json")));

        var info = new DirectoryInfo(sub);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(security);
        Assert.NotNull(FileSystemSafety.ValidateInheritingFolder(sub));

        FileSystemSafety.EnsureInheritingFolder(sub);
        Assert.Null(FileSystemSafety.ValidateInheritingFolder(sub));
        Assert.False(File.Exists(Path.Combine(sub, "old.json")));
    }

    [NonElevatedFact]
    public void Leftover_new_folders_are_removed_and_foreign_rejected_folders_are_kept()
    {
        using var t = new TestFolder();
        var root = t.Combine("EndpointToolbelt");
        Directory.CreateDirectory(root + ".new-abc");
        Directory.CreateDirectory(root + ".rejected-def"); // owned by the test user, not SYSTEM
        Directory.CreateDirectory(root + ".other");
        var outside = t.Combine("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");
        TestFolder.CreateJunction(root + ".new-link", outside);

        ToolbeltHost.CleanUpSiblingLeftovers(root);

        // The test user owns these, so only the link is removed; SYSTEM never deletes inside
        // a tree someone else controls.
        Assert.True(Directory.Exists(root + ".new-abc"));
        Assert.True(Directory.Exists(root + ".rejected-def"));
        Assert.True(Directory.Exists(root + ".other"));
        Assert.False(Directory.Exists(root + ".new-link"));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }
}

public class ProtocolDefaultsTests
{
    [Fact]
    public void Omitted_fields_get_default_values_not_initializer_values()
    {
        // DataContractJsonSerializer does not run property initializers. A request without
        // ConfirmedOtherSessions therefore acknowledges 0 other sessions, which never satisfies
        // the rule when other sessions exist; a missing ProtocolVersion is rejected as 0.
        var request = MessageFraming.Deserialize<Request>(Encoding.UTF8.GetBytes("{\"Type\":2,\"Action\":3}"));
        Assert.Equal(0, request.ConfirmedOtherSessions);
        Assert.Equal(0, request.ProtocolVersion);
        Assert.False(request.Confirmed);
    }
}
