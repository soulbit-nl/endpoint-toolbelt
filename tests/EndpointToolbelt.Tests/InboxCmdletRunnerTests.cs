using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The in-box cmdlet runner: only allow-listed
/// constant commands, only checked values, the fixed command line, and the result by exit code.
/// Nothing here runs a cmdlet: the process runner is a fake.
/// </summary>
public class InboxCmdletRunnerTests
{
    // A protector ID made of parts, so no real-looking GUID sits in the source.
    private static readonly string Bare = string.Join("-", new string('a', 8), new string('b', 4), new string('c', 4), new string('d', 4), new string('e', 12));
    private static readonly string Guid1 = "{" + Bare + "}";

    [Fact]
    public void Inserts_only_values_it_checked_itself()
    {
        var text = InboxCmdletRunner.BuildCommand(InboxCommand.BackupBitLockerKeyProtector, "C:", Guid1);
        Assert.Contains("-MountPoint 'C:' -KeyProtectorId '" + Guid1 + "'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("%", text);
        Assert.Throws<ArgumentException>(() => InboxCmdletRunner.BuildCommand(InboxCommand.BackupBitLockerKeyProtector, "C:"));
        Assert.Throws<ArgumentException>(() => InboxCmdletRunner.BuildCommand(InboxCommand.BackupBitLockerKeyProtector, "C:\\", Guid1));
        Assert.Throws<ArgumentException>(() => InboxCmdletRunner.BuildCommand(InboxCommand.BackupBitLockerKeyProtector, "C:", Bare));
        Assert.Throws<ArgumentException>(() => InboxCmdletRunner.BuildCommand(InboxCommand.BackupBitLockerKeyProtector, "C:", Guid1 + "'; Remove-Item C:\\x; '"));
        Assert.Throws<ArgumentException>(() => InboxCmdletRunner.BuildCommand(InboxCommand.BackupBitLockerKeyProtector, "C:", Guid1 + "\n"));
        Assert.Throws<ArgumentException>(() => InboxCmdletRunner.BuildCommand(InboxCommand.DeleteDeliveryOptimizationCache, "C:"));
        Assert.Throws<ArgumentOutOfRangeException>(() => InboxCmdletRunner.BuildCommand((InboxCommand)42));
        Assert.Throws<ArgumentException>(() => InboxCmdletRunner.BuildArguments("Write-Host \"x\""));
    }

    [Fact]
    public void Every_command_on_the_allow_list_uses_only_what_ConstrainedLanguage_mode_and_section_6_allow()
    {
        var cmdlets = new[] { "Get-Command", "BackupToAAD-BitLockerKeyProtector", "Delete-DeliveryOptimizationCache", "Get-AppxPackage", "Reset-AppxPackage" };
        var forbidden = new[] { "-File", "-EncodedCommand", "-ExecutionPolicy", "Invoke-Expression", "iex", "Add-Type", "New-Object", "Start-Process", "pwsh", "Invoke-Command", "&" };
        foreach (var command in Enum.GetValues(typeof(InboxCommand)).Cast<InboxCommand>())
        {
            var definition = InboxCmdletRunner.AllowList[command];
            var text = definition.Text;
            Assert.DoesNotContain("\"", text);
            // No type literals, static members or .NET method calls.
            Assert.DoesNotContain("[", text);
            Assert.DoesNotContain("::", text);
            Assert.DoesNotMatch(@"\.\w+\s*\(", text);
            foreach (var word in forbidden) { Assert.DoesNotMatch(@"(^|[\s;(|])" + Regex.Escape(word) + @"($|[\s;)])", text); }
            // Only in-box cmdlets of the allow-list, and the result by exit code.
            foreach (Match m in Regex.Matches(text, @"\b[A-Z][A-Za-z]+-[A-Z][A-Za-z]+\b")) { Assert.Contains(m.Value, cmdlets); }
            Assert.EndsWith("exit 0", text, StringComparison.Ordinal);
            Assert.Matches(@"exit 4 \}", text);
            var placeholders = Regex.Matches(text, "%\\d").Count;
            Assert.Equal(definition.Values.Count, placeholders);
        }
    }

    [Fact]
    public void Starts_Windows_PowerShell_by_full_path_with_the_fixed_switches_and_the_in_box_modules_only()
    {
        var fake = new FakeProcessRunner { Result = new ProcessResult { Started = true, ExitCode = 0 } };
        var result = new InboxCmdletRunner(fake).Run(InboxCommand.DeleteDeliveryOptimizationCache, TimeSpan.FromMinutes(10), null);
        Assert.Equal(InboxOutcome.Done, result.Outcome);
        var request = Assert.Single(fake.Requests);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), request.FilePath);
        Assert.StartsWith("-NoLogo -NoProfile -NonInteractive -Command \"$ProgressPreference='SilentlyContinue'; ", request.Arguments, StringComparison.Ordinal);
        Assert.EndsWith("Delete-DeliveryOptimizationCache -Force -ErrorAction Stop; exit 0\"", request.Arguments, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "Modules"), request.Environment["PSModulePath"]);
        Assert.Equal(10, request.TimeoutMinutes);
        Assert.Equal("Delete-DeliveryOptimizationCache", request.Label);
        // No room left in the run (a step limit of 0): nothing is started.
        Assert.Throws<ArgumentOutOfRangeException>(() => new InboxCmdletRunner(fake).Run(InboxCommand.DeleteDeliveryOptimizationCache, TimeSpan.Zero, null));
        Assert.Single(fake.Requests);
    }

    [Theory]
    [InlineData(true, 0, false, 0, "", InboxOutcome.Done)]
    [InlineData(true, 3, false, 0, "", InboxOutcome.NotPresent)]
    [InlineData(true, 4, false, 0, "", InboxOutcome.NotAvailable)]
    [InlineData(true, 1, false, 0, "Access denied", InboxOutcome.Failed)]
    [InlineData(true, 5, false, 0, "", InboxOutcome.Failed)]
    [InlineData(true, -2, true, 0, "", InboxOutcome.TimedOut)]
    [InlineData(false, -1, false, 1260, "", InboxOutcome.PolicyRefused)]
    [InlineData(false, -1, false, 577, "", InboxOutcome.PolicyRefused)]
    [InlineData(false, -1, false, 2, "", InboxOutcome.NotStarted)]
    [InlineData(true, 1, false, 0, "Cannot invoke method. Method invocation is supported only on core types in this language mode.", InboxOutcome.PolicyRefused)]
    [InlineData(true, 1, false, 0, "File x cannot be loaded. The file is not digitally signed.", InboxOutcome.PolicyRefused)]
    [InlineData(true, 0, false, 0, "is not digitally signed", InboxOutcome.Done)]
    public void Reports_the_result_by_exit_code_and_names_a_policy_refusal(bool started, int exitCode, bool timedOut, int startError, string errorOutput, InboxOutcome expected)
    {
        var result = InboxCmdletRunner.Classify(new ProcessResult { Started = started, ExitCode = exitCode, TimedOut = timedOut, StartError = startError, ErrorOutput = errorOutput });
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(errorOutput, result.ErrorOutput);
    }
}

/// <summary>The process runner: full path, captured pipes, its own time limit.</summary>
public class ProcessRunnerTests
{
    [Fact]
    public void Stops_a_tool_that_runs_past_its_limit_and_says_so()
    {
        var lines = new System.Collections.Generic.List<string>();
        var result = new ProcessRunner().Run(new ProcessRequest
        {
            FilePath = ProcessRunner.SystemTool("ping.exe"),
            Arguments = "-n 30 127.0.0.1",
            Label = "Slow tool",
            TimeoutMinutes = 0.02,
        }, (_, text) => lines.Add(text));
        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.Equal(-2, result.ExitCode);
        Assert.Contains("Slow tool did not finish within 0.02 minutes and is being stopped.", lines);
    }

    [Fact]
    public void Reports_a_tool_that_could_not_be_started_with_its_Windows_error()
    {
        var lines = new System.Collections.Generic.List<string>();
        var result = new ProcessRunner().Run(new ProcessRequest
        {
            FilePath = ProcessRunner.SystemTool("does-not-exist.exe"),
            Label = "Missing tool",
            TimeoutMinutes = 1,
        }, (_, text) => lines.Add(text));
        Assert.False(result.Started);
        Assert.Equal(2, result.StartError);
        Assert.Equal(-1, result.ExitCode);
        Assert.StartsWith("Missing tool could not be started: ", Assert.Single(lines), StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_standard_error_apart_from_the_output()
    {
        var result = new ProcessRunner().Run(new ProcessRequest
        {
            FilePath = ProcessRunner.SystemTool("cmd.exe"),
            Arguments = "/d /c echo out& echo err 1>&2",
            Label = "Echo",
            TimeoutMinutes = 1,
        }, null);
        Assert.Equal("out", result.Output.Trim());
        Assert.Equal("err", result.ErrorOutput.Trim());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessRunner().Run(new ProcessRequest { FilePath = ProcessRunner.SystemTool("cmd.exe"), Label = "x", TimeoutMinutes = 0 }, null));
        // Never through PATH or the working folder.
        Assert.Throws<ArgumentException>(() => new ProcessRunner().Run(new ProcessRequest { FilePath = "cmd.exe", Label = "x", TimeoutMinutes = 1 }, null));
    }
}
