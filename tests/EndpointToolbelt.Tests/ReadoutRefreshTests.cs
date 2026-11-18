using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.Engine;
using EndpointToolbelt.Service.Rules;
using EndpointToolbelt.Service.State;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The health checks are fresh after every tool. The service reads its device readouts again when
/// a run that may change the device ends, before it reports the end; the app says the tool's row in one line.
/// </summary>
public sealed class ReadoutRefreshTests : IDisposable
{
    private const string RunId = "run00055";
    private readonly TestFolder _folder = new();
    private readonly NullLog _log = new();
    private readonly FakeRunner _runner = new();
    private readonly CallerContext _caller = new("S-1-5-21-1000-2000-3000-1001", 2, "user");

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void A_finished_action_makes_the_next_status_fresh_within_the_cache_time()
    {
        var now = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        var offset = 400.0;
        var reads = 0;
        var reader = new DeviceReadoutReader(() =>
        {
            reads++;
            return new DeviceReadouts { Clock = new ClockReadout { Source = "ntp", OffsetSeconds = offset } };
        }, () => now);
        reader.RefreshNow();
        Assert.Equal(400.0, reader.Get().Clock.OffsetSeconds);
        // Sync clock ran; a minute later the cached answer (5 minutes) would still say 400 seconds.
        offset = 0.2;
        now = now.AddMinutes(1);
        Assert.Equal(400.0, reader.Get().Clock.OffsetSeconds);
        Assert.True(reader.RefreshAfterRun(TimeSpan.FromSeconds(10)));
        Assert.Equal(0.2, reader.Get().Clock.OffsetSeconds);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void A_refresh_that_takes_too_long_goes_on_and_the_next_status_gets_it()
    {
        var release = new System.Threading.ManualResetEventSlim(false);
        var calls = 0;
        var reader = new DeviceReadoutReader(() =>
        {
            if (++calls > 1) { release.Wait(TimeSpan.FromSeconds(10)); }
            return new DeviceReadouts { Defender = new DefenderReadout { Mode = "call" + calls } };
        });
        reader.RefreshNow();
        Assert.False(reader.RefreshAfterRun(TimeSpan.FromMilliseconds(50)));
        release.Set();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (reader.Get().Defender.Mode != "call2" && DateTime.UtcNow < until) { System.Threading.Thread.Sleep(20); }
        Assert.Equal("call2", reader.Get().Defender.Mode);
    }

    private ActionCoordinator Coordinator(List<string> order)
    {
        var engine = _folder.Combine("engine");
        Directory.CreateDirectory(engine);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(engine), ProductInfo.EngineExecutable), "test");
        var paths = new ServicePaths(_folder.Combine("work"), engine);
        Directory.CreateDirectory(paths.WorkFolder);
        return new ActionCoordinator(paths, _runner, new ResultStore(paths.ResultsFolder), _log, _log,
            () => new ServiceConfig(), new RateLimiter(TimeSpan.FromHours(1)), "1.0.0", () => _folder.Combine("logs"), _ => null, () => RunId,
            runEnded: action => order.Add("refreshed " + action));
    }

    private sealed class OrderSink : IResponseSink
    {
        private readonly List<string> _order;

        public OrderSink(List<string> order) => _order = order;

        public bool Send(Response response)
        {
            _order.Add(response.Type.ToString());
            return true;
        }
    }

    [Fact]
    public void The_service_reads_the_readouts_again_before_it_reports_the_end_of_a_run_that_changes_the_device()
    {
        _runner.Behaviour = (_, onRecord, _) =>
        {
            onRecord("{\"type\":\"summary\",\"run\":\"" + RunId + "\"}");
            return new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 0 };
        };
        var order = new List<string>();
        var coordinator = Coordinator(order);
        coordinator.RunAction(new Request { Type = RequestType.RunAction, Action = ToolbeltAction.SyncClock }, _caller, 0, new OrderSink(order));
        Assert.Equal(new[] { "Accepted", "Record", "refreshed SyncClock", "Completed" }, order);

        // A read-only diagnosis or a support package changes nothing the readouts show: no wait.
        foreach (var action in new[] { ToolbeltAction.DiagnoseApps, ToolbeltAction.CollectDiag })
        {
            order.Clear();
            coordinator.RunAction(new Request { Type = RequestType.RunAction, Action = action }, _caller, 0, new OrderSink(order));
            Assert.DoesNotContain(order, o => o.StartsWith("refreshed", StringComparison.Ordinal));
            Assert.Equal("Completed", order.Last());
        }

        // A request the service turned down ran nothing.
        order.Clear();
        coordinator.RunAction(new Request { Type = RequestType.RunAction, Action = ToolbeltAction.ClearAppCache }, _caller, 0, new OrderSink(order));
        Assert.Equal(new[] { "Rejected" }, order);
        Assert.True(ActionCoordinator.ChangesTheDevice(ToolbeltAction.RenewIpAddress));
        Assert.False(ActionCoordinator.ChangesTheDevice(ToolbeltAction.DiagnoseApps));
    }

    [Fact]
    public void A_refresh_that_fails_never_costs_the_result()
    {
        _runner.Behaviour = (_, onRecord, _) => new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 0 };
        var engine = _folder.Combine("engine");
        Directory.CreateDirectory(engine);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(engine), ProductInfo.EngineExecutable), "test");
        var paths = new ServicePaths(_folder.Combine("work"), engine);
        Directory.CreateDirectory(paths.WorkFolder);
        var coordinator = new ActionCoordinator(paths, _runner, new ResultStore(paths.ResultsFolder), _log, _log,
            () => new ServiceConfig(), new RateLimiter(TimeSpan.FromHours(1)), "1.0.0", () => _folder.Combine("logs"), _ => null, () => RunId,
            runEnded: _ => throw new InvalidOperationException("WMI is not answering"));
        var order = new List<string>();
        coordinator.RunAction(new Request { Type = RequestType.RunAction, Action = ToolbeltAction.SyncClock }, _caller, 0, new OrderSink(order));
        Assert.Equal("Completed", order.Last());
    }

    [Fact]
    public void The_result_says_the_tools_row_in_its_new_state_in_one_line()
    {
        HealthResult Row(HealthStatus status) => new() { Name = HealthChecks.Clock, Status = status };
        Assert.Equal("Clock: now OK", HealthView.AfterRun(HealthChecks.Clock, Row(HealthStatus.Fail), Row(HealthStatus.Pass)));
        Assert.Equal("Clock: still a problem", HealthView.AfterRun(HealthChecks.Clock, Row(HealthStatus.Fail), Row(HealthStatus.Fail)));
        Assert.Equal("Clock: still a warning", HealthView.AfterRun(HealthChecks.Clock, Row(HealthStatus.Warn), Row(HealthStatus.Warn)));
        Assert.Equal("Clock: OK", HealthView.AfterRun(HealthChecks.Clock, Row(HealthStatus.Pass), Row(HealthStatus.Pass)));
        Assert.Equal("Clock: now a problem", HealthView.AfterRun(HealthChecks.Clock, Row(HealthStatus.Pass), Row(HealthStatus.Fail)));
        Assert.Equal("Clock: OK", HealthView.AfterRun(HealthChecks.Clock, null, Row(HealthStatus.Pass)));
        Assert.Equal("", HealthView.AfterRun("", Row(HealthStatus.Fail), Row(HealthStatus.Pass)));
        Assert.Equal("", HealthView.AfterRun(HealthChecks.Clock, Row(HealthStatus.Fail), null));
        // The tools that act on a health row name it.
        Assert.Equal(HealthChecks.Clock, ToolCatalog.ByButton("SyncClockButton").HealthRow);
        Assert.Equal(HealthChecks.FreeSpace, ToolCatalog.ByButton("FreeSpaceButton").HealthRow);
        Assert.Equal(HealthView.Network, ToolCatalog.ByButton("RenewIpButton").HealthRow);
        Assert.All(ToolCatalog.All.Where(t => t.HealthRow.Length > 0), t => Assert.Contains(t.HealthRow, new[]
        {
            HealthView.AppInstaller, HealthView.WorkAccount, HealthView.Network, HealthView.IntuneConnection,
            HealthChecks.FreeSpace, HealthChecks.PendingRestart, HealthChecks.Clock, HealthChecks.Defender, HealthChecks.Profiles,
        }));
    }
}
