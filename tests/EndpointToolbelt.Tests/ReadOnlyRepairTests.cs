using System;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Protocol;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The read-only parts of the two repairs Microsoft documents no safe step for: the
/// Microsoft 365 Apps readout and the Intune connection's step for IT support.
/// </summary>
public class ReadOnlyRepairTests
{
    [Fact]
    public void The_update_channel_is_named_from_its_url()
    {
        Assert.Equal("Current Channel", OfficeInstall.ChannelName("http://officecdn.microsoft.com/pr/492350f6-3a01-4f97-b9c0-c7c6ddf67d60"));
        Assert.Equal("Monthly Enterprise Channel", OfficeInstall.ChannelName("https://officecdn.microsoft.com/pr/55336B82-A18D-4DD6-B5F6-9E5095C314A6/"));
        Assert.Equal("Semi-Annual Enterprise Channel", OfficeInstall.ChannelName("http://officecdn.microsoft.com/pr/7ffbc6bf-bc32-4f92-8982-f9dd17fd3114"));
        Assert.Equal("other (11111111-2222-3333-4444-555555555555)", OfficeInstall.ChannelName("http://officecdn.microsoft.com/pr/11111111-2222-3333-4444-555555555555"));
        Assert.Equal("other (not a known channel URL)", OfficeInstall.ChannelName("a local source folder"));
        Assert.Equal("unknown", OfficeInstall.ChannelName(null));
    }

    [Fact]
    public void The_report_names_the_installation_and_the_repair_in_settings()
    {
        Assert.Equal("Installed      : no (no Click-to-Run Microsoft 365 Apps)", OfficeInstall.ReportLines(null).Single());
        var lines = OfficeInstall.ReportLines(new OfficeInstall { Version = "16.0.17928.20156", Platform = "x64", Channel = "Current Channel" }).ToList();
        Assert.Equal("Installed      : 16.0.17928.20156 (x64), Current Channel", lines[0]);
        Assert.Contains("Quick Repair (needs an administrator)", lines[1]);

        var report = DeviceReport.Build(new DeviceFacts(), null, Array.Empty<HealthResult>(), Array.Empty<string>(), DateTimeOffset.Now, "1.0.0");
        Assert.Contains("----- MICROSOFT 365 APPS -----", report);
    }

    [Fact]
    public void Failing_enrollment_tasks_get_a_step_for_IT_support_and_healthy_ones_do_not()
    {
        string Step(IntuneConnectionReadout i) => DeviceSettingRows.IntuneConnectionLines(i).FirstOrDefault(l => l.StartsWith("For IT support", StringComparison.Ordinal));
        Assert.Null(Step(new IntuneConnectionReadout { Tasks = 4, TasksEnabled = 4, LastRunUtc = "2026-10-16T10:00:00Z", LastResult = "0x00000000" }));
        Assert.NotNull(Step(new IntuneConnectionReadout { Tasks = 4, TasksEnabled = 4, LastRunUtc = "2026-10-16T10:00:00Z", LastResult = "0x80072EE7" }));
        Assert.Null(Step(new IntuneConnectionReadout { Tasks = 4, TasksEnabled = 4, LastRunUtc = "2026-10-16T10:00:00Z", LastResult = "0x00041301" }));
        Assert.NotNull(Step(new IntuneConnectionReadout { Tasks = 0 }));
        Assert.NotNull(Step(new IntuneConnectionReadout { Tasks = 3, TasksEnabled = 0 }));
        Assert.Contains("Microsoft documents no local repair", Step(new IntuneConnectionReadout { Tasks = 0 }));
    }

    [Fact]
    public void Neither_undocumented_repair_has_a_button_or_an_action()
    {
        Assert.DoesNotContain(Enum.GetNames(typeof(ToolbeltAction)), n => n.Contains("Office") || n.Contains("IntuneConnection"));
        Assert.DoesNotContain(Enum.GetNames(typeof(HealthFix)), n => n.Contains("Office") || n.Contains("IntuneConnection"));
    }
}
