using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.Engine;
using EndpointToolbelt.Service.Pipe;
using EndpointToolbelt.Service.Platform;
using EndpointToolbelt.Service.Rules;
using EndpointToolbelt.Service.State;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>Tools an organization switches off with the policy value DisabledTools.</summary>
public class DisabledToolsTests : IDisposable
{
    private const string CallerSid = "S-1-5-80-1111111111-1222222222-1333333333-1444444444-1555555555";
    private static readonly CallerContext User = new("S-1-5-21-100", 2, "user");
    private static readonly CallerContext Caller = new(CallerSid, 0, @"NT SERVICE\ContosoAgent", null, isTrustedServiceCaller: true);
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private sealed class Registry : IRegistryReader
    {
        public Dictionary<string, object> Policy { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, object> Config { get; } = new(StringComparer.OrdinalIgnoreCase);

        public object GetValue(string subKey, string name)
        {
            var source = subKey == ProductInfo.PolicyKey ? Policy : subKey == ProductInfo.ConfigKey ? Config : null;
            return source != null && source.TryGetValue(name, out var v) ? v : null;
        }
    }

    private static ServiceConfig Load(object policyValue, object configValue = null)
    {
        var r = new Registry();
        if (policyValue != null) { r.Policy[ToolPolicy.ValueName] = policyValue; }
        if (configValue != null) { r.Config[ToolPolicy.ValueName] = configValue; }
        return ConfigLoader.Load(r);
    }

    // --- The policy value ------------------------------------------------------------------------

    [Fact]
    public void Without_the_value_nothing_is_switched_off()
    {
        var c = Load(null);
        Assert.Empty(c.DisabledTools);
        Assert.Empty(c.Warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ; , ")]
    public void An_empty_value_switches_nothing_off(string value)
    {
        var c = Load(value);
        Assert.Empty(c.DisabledTools);
        Assert.Empty(c.Warnings);
        Assert.Empty(Load(new[] { "", value }).DisabledTools);
    }

    [Fact]
    public void Known_names_are_read_case_insensitively_once_each_in_catalog_order()
    {
        var c = Load(new[] { "clearteamscache", "FULLRESET", " RepairWU ", "FullReset" });
        Assert.Equal(new[] { "FullReset", "RepairWU", "ClearTeamsCache" }, c.DisabledTools);
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public void A_string_value_with_commas_or_semicolons_is_read_too()
    {
        Assert.Equal(new[] { "FullReset", "ResetOneDrive" }, Load("ResetOneDrive, FullReset;").DisabledTools);
    }

    [Fact]
    public void Unknown_names_are_ignored_with_a_warning_and_never_echo_raw_text()
    {
        var c = Load(new[] { "FullReset", "ContosoTool", "Retry apps‮", "RetryAppsAndPolicies", "CollectDiag", "DiagnoseApps" });
        Assert.Equal(new[] { "FullReset" }, c.DisabledTools);
        Assert.Equal(5, c.Warnings.Count);
        Assert.Contains(c.Warnings, w => w.Contains("'ContosoTool'"));
        Assert.DoesNotContain(c.Warnings, w => w.Contains("‮"));
        // Retry work apps and settings, the support package and the app diagnosis are no tools of For IT support.
        Assert.False(ToolPolicy.CanSwitchOff(ToolbeltAction.RetryAppsAndPolicies));
        Assert.False(ToolPolicy.CanSwitchOff(ToolbeltAction.CollectDiag));
        Assert.False(ToolPolicy.CanSwitchOff(ToolbeltAction.DiagnoseApps));
    }

    [Fact]
    public void A_value_under_the_configuration_key_or_of_another_type_is_ignored()
    {
        Assert.Empty(Load(null, configValue: new[] { "FullReset" }).DisabledTools);
        var c = Load(1);
        Assert.Empty(c.DisabledTools);
        Assert.Single(c.Warnings);
    }

    [Fact]
    public void Unknown_names_are_logged_once_until_the_value_changes()
    {
        var r = new Registry();
        r.Policy[ToolPolicy.ValueName] = new[] { "ContosoTool" };
        var now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var warnings = new List<string>();
        var provider = new ConfigProvider(r, TimeSpan.FromMinutes(1), warnings.Add, () => now);
        provider.Get();
        now = now.AddMinutes(2);
        provider.Get();
        Assert.Single(warnings);
    }

    // --- The service ------------------------------------------------------------------------------

    private static PolicyDecision Evaluate(ToolbeltAction action, CallerContext caller, params string[] off)
        => RequestPolicy.EvaluateRunAction(new Request { Type = RequestType.RunAction, Action = action, Confirmed = true, ConfirmedOtherSessions = 0 },
            caller, 0, new ServiceConfig { DisabledTools = off }, null, new RateLimiter(TimeSpan.FromHours(1)));

    [Fact]
    public void The_user_pipe_gets_a_switched_off_tool_refused()
    {
        var d = Evaluate(ToolbeltAction.SyncClock, User, "SyncClock");
        Assert.Equal(ResultCode.ToolSwitchedOff, d.Result);
        Assert.Equal(24, (int)ResultCode.ToolSwitchedOff);
        Assert.True(Evaluate(ToolbeltAction.UpdateDefender, User, "SyncClock").IsAccepted);
        Assert.True(Evaluate(ToolbeltAction.SyncClock, User).IsAccepted);
    }

    [Fact]
    public void The_trusted_service_caller_may_still_run_a_switched_off_tool()
    {
        Assert.True(Evaluate(ToolbeltAction.SyncClock, Caller, "SyncClock").IsAccepted);
    }

    [Fact]
    public void An_action_that_is_no_tool_is_never_refused_as_switched_off()
    {
        Assert.True(Evaluate(ToolbeltAction.RetryAppsAndPolicies, User, "RetryAppsAndPolicies").IsAccepted);
    }

    private ActionCoordinator Coordinator(FakeRunner runner, NullLog log, ServiceConfig config)
    {
        var engine = _folder.Combine("engine");
        Directory.CreateDirectory(engine);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(engine), ProductInfo.EngineExecutable), "test");
        var paths = new ServicePaths(_folder.Combine("work"), engine);
        Directory.CreateDirectory(paths.WorkFolder);
        var runIds = 0;
        return new ActionCoordinator(paths, runner, new ResultStore(paths.ResultsFolder), log, log,
            () => config, new RateLimiter(TimeSpan.FromHours(1)), "1.0.0", () => _folder.Combine("logs"), _ => null,
            () => "run" + Interlocked.Increment(ref runIds).ToString("00000", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_refusal_writes_its_event_and_log_line_and_runs_nothing()
    {
        var log = new NullLog();
        var runner = new FakeRunner();
        var ran = 0;
        runner.Behaviour = (_, _, _) => { ran++; return new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 0 }; };
        var coordinator = Coordinator(runner, log, new ServiceConfig { DisabledTools = new[] { "SyncClock" } });
        try
        {
            var sink = new CollectingSink();
            coordinator.RunAction(new Request { Type = RequestType.RunAction, Action = ToolbeltAction.SyncClock }, User, 0, sink);
            var answer = Assert.Single(sink.Responses);
            Assert.Equal(ResponseType.Rejected, answer.Type);
            Assert.Equal(ResultCode.ToolSwitchedOff, answer.Result);
            Assert.Equal(0, ran);
            Assert.Contains(EventId.ToolSwitchedOff, log.Events);
            Assert.Contains(log.Lines, l => l.StartsWith("W Request SyncClock", StringComparison.Ordinal) && l.Contains("ToolSwitchedOff"));

            var remote = new CollectingSink();
            coordinator.RunAction(new Request { Type = RequestType.RunAction, Action = ToolbeltAction.SyncClock }, Caller, 0, remote);
            Assert.Equal(ResponseType.Accepted, remote.Responses[0].Type);
            Assert.Equal(1, ran);
        }
        finally { coordinator.Shutdown(TimeSpan.FromSeconds(5)); }
    }

    private sealed class EmptyRegistry : IRegistryReader
    {
        public object GetValue(string subKey, string name) => null;
    }

    private sealed class NoOtherSessions : ISessionCounter
    {
        public int CountOtherUserSessions(int callerSessionId) => 0;
    }

    [Fact]
    public void The_status_carries_the_switched_off_tools()
    {
        var log = new NullLog();
        var config = new ServiceConfig { DisabledTools = new[] { "FullReset", "ClearTeamsCache" } };
        var coordinator = Coordinator(new FakeRunner(), log, config);
        try
        {
            var state = new DeviceStateReader(new EmptyRegistry(), () => DateTime.UtcNow, () => DateTime.UtcNow.AddHours(-1), () => Array.Empty<(string, int)>());
            var handler = new RequestHandler(coordinator, new NoOtherSessions(), state, () => config, log, "1.1.0");
            Assert.Equal(new[] { "FullReset", "ClearTeamsCache" }, handler.BuildStatus(User).DisabledTools);
            config.DisabledTools = Array.Empty<string>();
            Assert.Empty(handler.BuildStatus(User).DisabledTools);
        }
        finally { coordinator.Shutdown(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public void The_status_field_is_an_addition_an_older_peer_reads_without_it()
    {
        var bytes = MessageFraming.Serialize(new StatusInfo { ProtocolVersion = 1, DisabledTools = new List<string> { "FullReset" } });
        Assert.Equal(new[] { "FullReset" }, MessageFraming.Deserialize<StatusInfo>(bytes).DisabledTools);
        // A status from an older service: no list, so the app reads the policy itself.
        var old = MessageFraming.Deserialize<StatusInfo>(MessageFraming.Serialize(new StatusInfo { ProtocolVersion = 1 }));
        Assert.Null(old.DisabledTools);
        Assert.Equal(new[] { "SyncClock" }, SwitchedOffTools.From(old, () => new[] { "SyncClock" }));
        Assert.Equal(new[] { "FullReset" }, SwitchedOffTools.From(new StatusInfo { DisabledTools = new List<string> { "FullReset" } }, () => new[] { "SyncClock" }));
        Assert.Empty(SwitchedOffTools.From(null, null));
    }

    // --- The app ----------------------------------------------------------------------------------

    [Fact]
    public void Every_tool_has_exactly_one_policy_name_and_every_name_one_tool()
    {
        var names = ToolCatalog.All.Select(t => t.PolicyName).ToList();
        Assert.Equal(22, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(ToolPolicy.AllTools.OrderBy(n => n, StringComparer.Ordinal), names.OrderBy(n => n, StringComparer.Ordinal));
        // The service tools by their contract action names, the app tools by names of their own.
        Assert.All(ToolCatalog.All.Where(t => t.InService), t => Assert.Contains(t.Action.Value, ToolPolicy.ServiceTools));
        Assert.All(ToolCatalog.All.Where(t => !t.InService), t => Assert.Contains(t.PolicyName, ToolPolicy.AppTools));
    }

    [Fact]
    public void A_switched_off_tool_leaves_its_group_list_count_and_search()
    {
        var off = new[] { "FullReset", "ClearAppCache" };
        Assert.DoesNotContain(ToolCatalog.InGroup(ToolGroup.Apps, off), t => t.Button == "FullResetButton");
        Assert.Equal(ToolCatalog.InGroup(ToolGroup.Apps).Count - 2, ToolCatalog.InGroup(ToolGroup.Apps, off).Count);
        Assert.Equal("Apps (2)", ToolCatalog.Heading(ToolGroup.Apps, off));
        Assert.StartsWith("2 tools", ToolRuns.GroupSummary(ToolGroup.Apps, null, off), StringComparison.Ordinal);
        Assert.StartsWith("4 tools", ToolRuns.GroupSummary(ToolGroup.Apps, null), StringComparison.Ordinal);
        Assert.Contains(ToolCatalog.Search("reset"), t => t.Button == "FullResetButton");
        Assert.DoesNotContain(ToolCatalog.Search("reset", off), t => t.Button == "FullResetButton");
        Assert.Equal(20, ToolCatalog.Search("", off).Count);
    }

    [Fact]
    public void A_group_whose_tools_are_all_off_shows_no_tile()
    {
        var off = ToolCatalog.InGroup(ToolGroup.Apps).Select(t => t.PolicyName.ToLowerInvariant()).ToArray();
        Assert.False(ToolCatalog.GroupOffered(ToolGroup.Apps, off));
        Assert.True(ToolCatalog.GroupOffered(ToolGroup.SignIn, off));
        Assert.True(ToolCatalog.GroupOffered(ToolGroup.Apps, null));
    }

    [Fact]
    public void A_problem_path_does_not_show_a_switched_off_tool()
    {
        var path = ProblemPaths.Get(Problem.CantSignIn);
        Assert.Contains("TeamsCacheButton", ProblemPaths.ToolsShown(path, null));
        var shown = ProblemPaths.ToolsShown(path, null, new[] { "ClearTeamsCache" });
        Assert.DoesNotContain("TeamsCacheButton", shown);
        Assert.Equal(new[] { "RefreshSignInButton", "ResetSignInButton" }, shown);
    }

    [Fact]
    public void Home_does_not_offer_a_switched_off_tool_as_its_step()
    {
        var rows = new[] { new HealthResult { Name = HealthChecks.Clock, Status = HealthStatus.Fail, FixOverride = HealthFix.SyncClock } };
        var on = HomeSummary.Build(rows, null, serviceMissing: false);
        Assert.Equal(HomeStep.HealthFix, on.Step);
        Assert.Equal("Sync clock", on.StepText);
        var off = HomeSummary.Build(rows, null, serviceMissing: false, new[] { "SyncClock" });
        Assert.Equal(HomeStep.None, off.Step);
        Assert.Equal(HealthFix.None, off.Fix);
        Assert.Equal("", off.StepText);
        // Retry work apps and settings is no tool of For IT support: never switched off.
        var retry = new[] { new HealthResult { Name = HealthView.AppInstaller, Status = HealthStatus.Fail, FixOverride = HealthFix.RetryAppsAndPolicies } };
        Assert.Equal(HomeStep.HealthFix, HomeSummary.Build(retry, null, false, ToolPolicy.AllTools).Step);
    }

    [Fact]
    public void A_health_row_does_not_link_to_a_switched_off_tool()
    {
        Assert.True(ToolCatalog.FixOff(HealthFix.ConnectivityTest, new[] { "CheckConnections" }));
        Assert.False(ToolCatalog.FixOff(HealthFix.ConnectivityTest, new[] { "SyncClock" }));
        Assert.False(ToolCatalog.FixOff(HealthFix.RetryAppsAndPolicies, ToolPolicy.AllTools));
        Assert.False(ToolCatalog.FixOff(HealthFix.None, null));
    }

    [Fact]
    public void The_result_of_a_refusal_says_the_organization_switched_it_off()
    {
        Assert.Equal("not started: switched off by your organization", RunWords.Service(ResultCode.ToolSwitchedOff));
    }
}
