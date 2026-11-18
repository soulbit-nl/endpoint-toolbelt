using System.Collections.Generic;
using System.Linq;
using EndpointToolbelt.App.Core;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>The window combines the detailed checks into ten rows (one of them Windows Hello) and shows the ones that need attention.</summary>
public class AppCompactHealthTests
{
    private static HealthResult R(string name, HealthStatus status, string detail = "d")
        => HealthChecks.Make(name, status, detail);

    private static List<HealthResult> Healthy() => new()
    {
        R(HealthChecks.ImeService, HealthStatus.Pass, "Running"),
        R(HealthChecks.Enrollment, HealthStatus.Pass, "Enrolled in Intune"),
        R(HealthChecks.LastSync, HealthStatus.Pass, "10:42 (2 hours ago)"),
        R(HealthChecks.ImeCheckIn, HealthStatus.Pass, "10:15 (3 hours ago)"),
        R(HealthChecks.EntraJoined, HealthStatus.Pass, "Entra joined to Contoso"),
        R(HealthChecks.Prt, HealthStatus.Pass, "Present"),
        R(HealthChecks.WindowsHello, HealthStatus.Pass, "Set up for you"),
        R(HealthChecks.MdmCertificate, HealthStatus.Pass, "Expires 2027-10-01 (347 days)"),
        R(HealthChecks.Connectivity, HealthStatus.Pass, "manage.microsoft.com reachable"),
        R(HealthChecks.FreeSpace, HealthStatus.Pass, "80 GB free on C:"),
        R(HealthChecks.PendingRestart, HealthStatus.Pass, "No restart pending"),
        R(HealthChecks.Compliance, HealthStatus.Warn, "Secure Boot off"),
        R(HealthChecks.Profiles, HealthStatus.Pass, "No failed settings"),
        R(HealthChecks.Clock, HealthStatus.Pass, "In sync"),
        R(HealthChecks.Defender, HealthStatus.Pass, "Up to date"),
    };

    [Fact]
    public void Fifteen_checks_become_ten_rows_in_a_fixed_order_without_the_local_compliance_settings()
    {
        var rows = HealthView.Rows(Healthy());
        Assert.Equal(new[]
        {
            HealthView.IntuneConnection, HealthView.AppInstaller, HealthView.WorkAccount, HealthChecks.WindowsHello, HealthView.Network, HealthChecks.Profiles,
            HealthChecks.FreeSpace, HealthChecks.PendingRestart, HealthChecks.Clock, HealthChecks.Defender,
        }, rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.Equal(HealthStatus.Pass, r.Status));
        Assert.Equal("Last sync 10:42 (2 hours ago)", rows[0].Detail);
        Assert.Equal("Last check-in 10:15 (3 hours ago)", rows[1].Detail);
        Assert.Equal("Entra joined to Contoso; signed in", rows[2].Detail);
        Assert.Equal("All 10 checks OK.", HealthView.Verdict(rows));
        Assert.Empty(HealthView.Visible(rows, showAll: false));
        // A short link on the verdict's line.
        Assert.Equal("Show all 10", HealthView.ToggleText(rows, showingAll: false));
        Assert.Equal("Show all 10 health checks", HealthView.ToggleName(rows, showingAll: false));
        Assert.Equal("Hide the health checks", HealthView.ToggleName(rows, showingAll: true));
        Assert.Equal(10, HealthView.Visible(rows, showAll: true).Count);
    }

    [Fact]
    public void A_combined_row_takes_the_worst_part_names_it_and_links_to_its_fix()
    {
        var checks = Healthy();
        checks[checks.FindIndex(c => c.Name == HealthChecks.MdmCertificate)] = R(HealthChecks.MdmCertificate, HealthStatus.Fail, "Expires 2026-10-31 (12 days)");
        checks[checks.FindIndex(c => c.Name == HealthChecks.LastSync)] = R(HealthChecks.LastSync, HealthStatus.Warn, "3 days ago");
        var row = HealthView.Rows(checks).Single(r => r.Name == HealthView.IntuneConnection);
        Assert.Equal(HealthStatus.Fail, row.Status);
        Assert.Equal("Certificate: Expires 2026-10-31 (12 days) (and 1 more)", row.Detail);
        Assert.Equal(HealthFix.RetryAppsAndPolicies, row.Fix);
        Assert.Equal(HealthChecks.Hint(HealthChecks.MdmCertificate, false), row.Hint);
    }

    [Fact]
    public void A_part_without_a_fix_borrows_the_fix_of_another_part_that_is_not_OK()
    {
        var checks = Healthy();
        checks[checks.FindIndex(c => c.Name == HealthChecks.EntraJoined)] = R(HealthChecks.EntraJoined, HealthStatus.Fail, "The device is not Entra joined");
        checks[checks.FindIndex(c => c.Name == HealthChecks.Prt)] = R(HealthChecks.Prt, HealthStatus.Warn, "Missing");
        var row = HealthView.Rows(checks).Single(r => r.Name == HealthView.WorkAccount);
        Assert.Equal(HealthStatus.Fail, row.Status);
        Assert.Equal(HealthFix.RefreshSignIn, row.Fix);

        // Only the part without a fix is not OK: no link; one sentence and where to go, the whole hint as tooltip.
        checks[checks.FindIndex(c => c.Name == HealthChecks.Prt)] = R(HealthChecks.Prt, HealthStatus.Pass, "Present");
        row = HealthView.Rows(checks).Single(r => r.Name == HealthView.WorkAccount);
        Assert.Equal(HealthFix.None, row.Fix);
        Assert.Equal("The device is not joined to Entra ID, which Intune and single sign-on need. Contact your IT support.", row.HintBelow);
        Assert.Equal(row.Hint, row.HintAsToolTip);
    }

    [Fact]
    public void The_verdict_and_the_link_count_the_rows_on_screen()
    {
        var checks = Healthy();
        checks[checks.FindIndex(c => c.Name == HealthChecks.FreeSpace)] = R(HealthChecks.FreeSpace, HealthStatus.Fail, "3 GB");
        checks[checks.FindIndex(c => c.Name == HealthChecks.Prt)] = R(HealthChecks.Prt, HealthStatus.Warn, "Missing");
        checks[checks.FindIndex(c => c.Name == HealthChecks.Clock)] = R(HealthChecks.Clock, HealthStatus.Warn, "Off");
        var rows = HealthView.Rows(checks);
        Assert.Equal("1 problem, 2 warnings, 7 OK.", HealthView.Verdict(rows));
        // Home shows the count only, also when checks need attention; Show all shows every row.
        Assert.Empty(HealthView.Visible(rows, showAll: false, HealthView.HomeAttentionRows));
        Assert.Equal(10, HealthView.Visible(rows, showAll: true).Count);
        // The verdict counts the OK checks; the link is short, its name says what it shows.
        Assert.Equal("Show all 10", HealthView.ToggleText(rows, showingAll: false));
        Assert.Equal("Show less", HealthView.ToggleText(rows, showingAll: true));
        Assert.Equal("Hide the health checks", HealthView.ToggleName(rows, showingAll: true, HealthView.HomeAttentionRows));
        // The problem row's own step, not Retry work apps and settings for every failed check.
        Assert.Equal(("1 problem found. Free disk space: try Free up disk space.", Tone.Error), HealthChecks.Summary(rows));
    }

    [Fact]
    public void Rows_the_service_could_not_answer_stay_marked_so_and_show_no_hint()
    {
        var checks = Healthy();
        checks[checks.FindIndex(c => c.Name == HealthChecks.Enrollment)] = HealthChecks.Unavailable(HealthChecks.Enrollment);
        checks[checks.FindIndex(c => c.Name == HealthChecks.LastSync)] = HealthChecks.Unavailable(HealthChecks.LastSync);
        var row = HealthView.Rows(checks).Single(r => r.Name == HealthView.IntuneConnection);
        Assert.True(row.ServiceUnavailable);
        Assert.Equal("", row.HintBelow);
        Assert.Equal(HealthFix.None, row.Fix);
    }

    [Fact]
    public void The_report_summary_names_the_detailed_checks()
    {
        var checks = Healthy();
        var lines = DeviceReport.HealthSummaryLines(checks).ToList();
        Assert.Equal(new[] { "To check  : Compliance settings (local)", "" }, lines);
        checks.RemoveAll(c => c.Name == HealthChecks.Compliance);
        Assert.Equal("Summary   : all 14 checks OK", DeviceReport.HealthSummaryLines(checks).First());
    }
}
