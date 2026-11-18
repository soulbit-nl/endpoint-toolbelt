using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Platform;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The app's Company Portal reset through the shared in-box cmdlet runner. Run
/// itself closes Company Portal in the session, so these tests check what each outcome of the
/// runner means and that nothing else starts Windows PowerShell.
/// </summary>
public class AppCompanyPortalTests
{
    [Theory]
    [InlineData(InboxOutcome.Done, 0, "Company Portal was reset. Start it again and sign in.", Tone.Success)]
    [InlineData(InboxOutcome.NotPresent, 3, "Company Portal is not installed.", Tone.Warning)]
    [InlineData(InboxOutcome.NotAvailable, 4, "An automatic reset is not supported on this Windows version.", Tone.Warning)]
    [InlineData(InboxOutcome.PolicyRefused, 5, "A device policy blocked the automatic reset.", Tone.Warning)]
    [InlineData(InboxOutcome.TimedOut, 1, "The Company Portal reset failed.", Tone.Error)]
    [InlineData(InboxOutcome.NotStarted, 1, "The Company Portal reset failed.", Tone.Error)]
    [InlineData(InboxOutcome.Failed, 1, "The Company Portal reset failed.", Tone.Error)]
    public void Tells_the_user_what_each_outcome_of_the_runner_means(InboxOutcome outcome, int code, string status, Tone tone)
    {
        var result = CompanyPortalReset.Interpret(new InboxResult { Outcome = outcome, ErrorOutput = "Reset-AppxPackage : Deployment failed.\r\n" }, new List<string> { "Company Portal was not running." });
        Assert.Equal(code, result.Code);
        Assert.Equal(status, result.Status);
        Assert.Equal(tone, result.Tone);
    }

    [Fact]
    public void Names_the_policy_and_the_manual_step_when_a_device_policy_blocks_PowerShell()
    {
        var lines = new List<string>();
        CompanyPortalReset.Interpret(new InboxResult { Outcome = InboxOutcome.PolicyRefused }, lines);
        Assert.Contains(lines, l => l.Contains("App Control, AppLocker or the PowerShell execution policy"));
        Assert.Contains("Manual alternative: Settings > Apps > Company Portal > Advanced options > Reset.", lines);
        var failed = new List<string>();
        CompanyPortalReset.Interpret(new InboxResult { Outcome = InboxOutcome.Failed, ErrorOutput = "  Deployment failed.  " }, failed);
        Assert.Equal("The reset failed: Deployment failed.", Assert.Single(failed));
    }

    [Fact]
    public void Runs_the_reset_as_the_allow_listed_command_with_the_in_box_modules_only()
    {
        var runner = new FakeProcessRunner { Result = FakeProcessRunner.Exit(InboxCmdletRunner.ExitNotPresent) };
        var result = new InboxCmdletRunner(runner).Run(InboxCommand.ResetCompanyPortal, TimeSpan.FromMinutes(2), (_, _) => { });
        Assert.Equal(InboxOutcome.NotPresent, result.Outcome);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(InboxCmdletRunner.PowerShellPath, request.FilePath);
        Assert.Contains("Get-AppxPackage -Name 'Microsoft.CompanyPortal'", request.Arguments, StringComparison.Ordinal);
        Assert.StartsWith("-NoLogo -NoProfile -NonInteractive -Command \"", request.Arguments, StringComparison.Ordinal);
        Assert.EndsWith(@"\WindowsPowerShell\v1.0\Modules", request.Environment["PSModulePath"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Shows_a_policy_block_in_the_history_as_its_own_code()
    {
        Assert.Contains("blocked by a device policy (5)", RunWords.HistoryLine("2026-10-15 14:02:11 +02:00;user;ResetCompanyPortal;5"), StringComparison.Ordinal);
        Assert.Contains("not supported on this Windows (4)", RunWords.HistoryLine("2026-10-15 14:02:11 +02:00;user;ResetCompanyPortal;4"), StringComparison.Ordinal);
    }

    /// <summary>The one file below src that may name powershell.exe: the shared in-box cmdlet runner.</summary>
    private static readonly string[] AllowedPowerShellStarters = { @"src\EndpointToolbelt.Platform\InboxCmdletRunner.cs" };

    /// <summary>What is ruled out everywhere below src.</summary>
    private static readonly string[] Forbidden =
    {
        "pwsh.exe", "System.Management.Automation", "CSharpCodeProvider", "System.CodeDom", "XmlSerializer",
        "Reflection.Emit", "RegexOptions.Compiled", "Assembly.Load(",
    };

    [Fact]
    public void Nothing_hosts_PowerShell_names_PowerShell_7_or_generates_code_at_run_time()
    {
        var root = EngineHarness.RepositoryRoot();
        var hits = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"))
            .SelectMany(f => Forbidden.Where(word => File.ReadAllText(f).IndexOf(word, StringComparison.Ordinal) >= 0).Select(word => f.Substring(root.Length + 1) + ": " + word))
            .ToList();
        Assert.Empty(hits);
        // No PowerShell file below src at all; the engine folder holds its data only.
        var scripts = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => new[] { ".ps1", ".psm1", ".psd1", ".ps1xml" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();
        Assert.Empty(scripts);
    }

    [Fact]
    public void The_shared_runner_is_the_only_code_that_starts_Windows_PowerShell()
    {
        var root = EngineHarness.RepositoryRoot();
        var src = Path.Combine(root, "src");
        var naming = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"))
            .Where(f => File.ReadAllText(f).IndexOf("\"powershell.exe\"", StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(f => f.Substring(root.Length + 1))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(AllowedPowerShellStarters.OrderBy(f => f, StringComparer.OrdinalIgnoreCase), naming);
    }
}
