using System;
using System.IO;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// Release gate 6: the published MSI is never built on the sandbox runner. The staging build's first
/// step fails on a v* tag while the runner is the sandbox label, before anything is checked out.
/// </summary>
public class ReleaseGateTests
{
    private static string Workflow()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir.FullName, ".github", "workflows", "build.yml")).Replace("\r\n", "\n");
    }

    [Fact]
    public void A_release_tag_fails_first_off_the_production_runner_and_names_WINDOWS_RUNNER()
    {
        var yaml = Workflow();
        var guard = yaml.IndexOf("- name: A release tag needs the production runner", StringComparison.Ordinal);
        var checkout = yaml.IndexOf("- uses: actions/checkout@", StringComparison.Ordinal);
        Assert.True(guard > 0, "the guard step is missing");
        // The first step of the build job: before the checkout and the build.
        Assert.True(guard < checkout, "the guard must come before the checkout");
        var step = yaml.Substring(guard, checkout - guard);
        Assert.Contains("if: startsWith(github.ref, 'refs/tags/v')", step);
        Assert.Contains("vars.WINDOWS_RUNNER", step);
        // Only the production label passes: not the sandbox (soulbit-azure-windows-test), not windows-latest.
        Assert.Contains("-notmatch '\"soulbit-azure-windows\"'", step);
        foreach (var other in new[] { "[\"self-hosted\", \"Windows\", \"soulbit-azure-windows-test\"]", "[\"windows-latest\"]" })
        {
            Assert.DoesNotMatch("\"soulbit-azure-windows\"", other);
        }
        Assert.Matches("\"soulbit-azure-windows\"", "[\"self-hosted\", \"Windows\", \"soulbit-azure-windows\"]");
        Assert.Contains("WINDOWS_RUNNER", step.Substring(step.IndexOf("::error::", StringComparison.Ordinal)));
        Assert.Contains("exit 1", step);
    }

    [Fact]
    public void The_Windows_build_runs_on_the_production_runner_by_default()
    {
        var yaml = Workflow();
        Assert.Contains("runs-on: ${{ fromJSON(vars.WINDOWS_RUNNER || '[\"self-hosted\", \"Windows\", \"soulbit-azure-windows\"]') }}", yaml);
        Assert.Contains("RUNS_ON: ${{ vars.WINDOWS_RUNNER || '[\"self-hosted\", \"Windows\", \"soulbit-azure-windows\"]' }}", yaml);
        // The sandbox label is named only by the release gate's check.
        Assert.DoesNotContain("'[\"self-hosted\", \"Windows\", \"soulbit-azure-windows-test\"]'", yaml);
    }
}
