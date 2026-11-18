using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.Engine;
using EndpointToolbelt.Service.Logging;
using EndpointToolbelt.Service.Pipe;
using EndpointToolbelt.Service.Rules;
using EndpointToolbelt.Service.State;
using Microsoft.Win32;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>Interrupted runs, the stored-record cap, stop timing, fault stops, events and uninstall cleanup.</summary>
public class InterruptedRunTests : IDisposable
{
    private const string RunId = "run00002";
    private readonly TestFolder _folder = new();
    private readonly NullLog _log = new();
    private readonly FakeRunner _runner = new();
    private readonly ServicePaths _paths;
    private readonly CallerContext _caller = new("S-1-5-21-1000-2000-3000-1001", 2, "user");

    public InterruptedRunTests()
    {
        var engine = _folder.Combine("engine");
        Directory.CreateDirectory(engine);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(engine), ProductInfo.EngineExecutable), "test");
        _paths = new ServicePaths(_folder.Combine("work"), engine);
        Directory.CreateDirectory(_paths.WorkFolder);
    }

    public void Dispose() => _folder.Dispose();

    private ActionCoordinator Create() =>
        new(_paths, _runner, new ResultStore(_paths.ResultsFolder), _log, _log,
            () => new ServiceConfig(), new RateLimiter(TimeSpan.FromHours(1)), "1.0.0", () => _folder.Combine("logs"), _ => null, () => RunId);

    private static string Rec(string type, string extra = "") => "{\"type\":\"" + type + "\",\"run\":\"" + RunId + "\"" + extra + "}";

    [Fact]
    public void An_accepted_run_is_stored_as_in_progress_before_the_engine_starts()
    {
        LastResultInfo seenByEngine = null;
        _runner.Behaviour = (_, _, _) =>
        {
            // What a new service process would find after a power loss at this point.
            seenByEngine = new ResultStore(_paths.ResultsFolder).Load(_caller.Sid);
            return new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 0 };
        };
        Create().RunAction(new Request { Type = RequestType.RunAction, Action = ToolbeltAction.RetryAppsAndPolicies }, _caller, 0, new CollectingSink());

        Assert.NotNull(seenByEngine);
        Assert.True(seenByEngine.InProgress);
        Assert.Equal(RunId, seenByEngine.RunId);
        Assert.False(new ResultStore(_paths.ResultsFolder).Load(_caller.Sid).InProgress);
    }

    [Fact]
    public void A_run_still_in_progress_at_service_start_becomes_interrupted_with_its_records()
    {
        var store = new ResultStore(_paths.ResultsFolder);
        var stale = new LastResultInfo { Action = ToolbeltAction.RepairWU, RunId = "old00001", StartedUtc = "2026-10-15T10:00:00.0000000Z", InProgress = true };
        stale.Records.Add("{\"type\":\"change\",\"run\":\"old00001\"}");
        store.Save(_caller.Sid, stale);
        var otherSid = "S-1-5-21-1000-2000-3000-1002";
        store.Save(otherSid, new LastResultInfo { Action = ToolbeltAction.CollectDiag, RunId = "done0001", Result = ResultCode.Completed, ExitCode = 0 });

        var coordinator = Create();
        coordinator.RecoverInterruptedRuns();

        var result = coordinator.GetLastResult(_caller.Sid);
        Assert.False(result.InProgress);
        Assert.Equal(ResultCode.Interrupted, result.Result);
        Assert.Null(result.ExitCode);
        Assert.False(string.IsNullOrEmpty(result.EndedUtc));
        Assert.Single(result.Records);
        Assert.Equal(ResultCode.Completed, coordinator.GetLastResult(otherSid).Result);
        Assert.Single(_log.Events, e => e == EventId.ActionFailed);
    }

    [Fact]
    public void A_run_cancelled_by_a_service_stop_is_interrupted_not_crashed()
    {
        var coordinator = Create();
        using var started = new ManualResetEventSlim();
        _runner.Behaviour = (_, onRecord, token) =>
        {
            onRecord(Rec("change", ",\"text\":\"stopped wuauserv\""));
            started.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            return new EngineRunResult { Outcome = EngineOutcome.Cancelled };
        };
        var sink = new CollectingSink();
        var t = new Thread(() => coordinator.RunAction(new Request { Type = RequestType.RunAction, Action = ToolbeltAction.RepairWU, Confirmed = true, ConfirmationNonce = coordinator.Nonces.Issue(_caller.Sid) }, _caller, 0, sink));
        t.Start();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        coordinator.Shutdown(TimeSpan.FromSeconds(10));
        t.Join();

        Assert.Equal(ResultCode.Interrupted, sink.Responses.Last().Result);
        var stored = new ResultStore(_paths.ResultsFolder).Load(_caller.Sid);
        Assert.Equal(ResultCode.Interrupted, stored.Result);
        Assert.False(stored.InProgress);
        Assert.Single(stored.Records);
    }

    [Fact]
    public void The_record_cap_drops_progress_first_and_keeps_the_result_records_in_order()
    {
        var big = new string('x', 20_000);
        _runner.Behaviour = (_, onRecord, _) =>
        {
            onRecord(Rec("finding", ",\"id\":\"FIRST\""));
            for (var i = 0; i < 10; i++) { onRecord(Rec("progress", ",\"text\":\"p" + i + big + "\"")); }
            onRecord(Rec("warning", ",\"text\":\"w" + big + "\""));
            for (var i = 10; i < 20; i++) { onRecord(Rec("progress", ",\"text\":\"p" + i + big + "\"")); }
            onRecord(Rec("change", ",\"text\":\"c" + big + "\""));
            onRecord(Rec("finding", ",\"id\":\"SECOND\",\"text\":\"" + big + "\""));
            onRecord(Rec("restart-required"));
            onRecord(Rec("mdm-queued"));
            onRecord(Rec("summary", ",\"exit\":0"));
            return new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 0 };
        };
        var sink = new CollectingSink();
        var coordinator = Create();
        coordinator.RunAction(new Request { Type = RequestType.RunAction, Action = ToolbeltAction.RetryAppsAndPolicies }, _caller, 0, sink);

        Assert.Equal(27, sink.Responses.Count(r => r.Type == ResponseType.Record));
        var stored = coordinator.GetLastResult(_caller.Sid);
        Assert.True(stored.RecordsTruncated);
        Assert.True(stored.Records.Sum(r => r.Length) <= ActionCoordinator.MaxStoredRecordChars);
        var types = stored.Records.Select(r => MessageFraming.Deserialize<EngineRecordHeader>(System.Text.Encoding.UTF8.GetBytes(r)).Type).ToList();
        foreach (var kept in new[] { "change", "restart-required", "mdm-queued", "summary" }) { Assert.Contains(kept, types); }
        Assert.Equal(2, types.Count(t => t == "finding"));
        Assert.Equal("finding", types[0]);
        Assert.Equal("summary", types[types.Count - 1]);
        Assert.Contains("progress", types);
        // Order is kept: the earliest progress records were the ones dropped.
        var keptProgress = stored.Records.Where(r => r.Contains("\"progress\"")).Select(r => int.Parse(r.Split(new[] { "\"text\":\"p" }, StringSplitOptions.None)[1].Substring(0, 2).TrimEnd('x'))).ToList();
        Assert.Equal(keptProgress.OrderBy(i => i), keptProgress);
        Assert.DoesNotContain(0, keptProgress);
    }

    [Fact]
    public void Interrupted_is_explained_in_the_app_and_kept_in_the_history()
    {
        var outcome = new RunOutcome { Action = ToolbeltAction.RepairWU, Result = ResultCode.Interrupted, Message = "The service stopped while the action was running." };
        var view = ResultPresenter.Final(outcome);
        Assert.Equal(Tone.Warning, view.Tone);
        Assert.True(view.CopyLog);
        Assert.Contains("was interrupted: the Endpoint Toolbelt service stopped while it ran (for example a restart or an update)", view.Status);
        Assert.True(ResultPresenter.BelongsInHistory(outcome));
        Assert.Equal("interrupted", ResultPresenter.HistoryResult(outcome));
    }

    [Theory]
    [InlineData(ResultCode.ConfirmationRequired)]
    [InlineData(ResultCode.MultiSessionBlocked)]
    public void The_app_shows_the_services_own_message_for_session_rejections(ResultCode code)
    {
        const string message = "Other users are signed in now (3). Confirm again to continue.";
        var view = ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.FullReset, Result = code, Message = message });
        Assert.Equal(message, view.Status);
        Assert.Equal(message, view.Cards[0].Title);
        Assert.Equal("", view.Cards[0].Detail);
        Assert.Equal(Tone.Warning, view.Tone);

        var fallback = ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.FullReset, Result = code });
        Assert.StartsWith(ActionText.Name(ToolbeltAction.FullReset), fallback.Status);
    }
}

public class ResultCodeCompatibilityTests
{
    [Fact]
    public void An_unknown_result_code_is_read_as_its_number_not_rejected()
    {
        // Why a new ResultCode value needs no protocol version change: enums travel as
        // numbers, so an older app reads a newer code and falls back to its generic text.
        var response = MessageFraming.Deserialize<Response>(System.Text.Encoding.UTF8.GetBytes("{\"Type\":8,\"Result\":99}"));
        Assert.Equal(99, (int)response.Result);
        Assert.Contains("\"Result\":34", System.Text.Encoding.UTF8.GetString(MessageFraming.Serialize(new Response { Type = ResponseType.Completed, Result = ResultCode.Interrupted })));
    }
}

public class ServiceRejectionTextTests
{
    private static readonly CallerContext Caller = new("S-1-5-21-100", 2, "user");

    [Fact]
    public void Session_rejections_are_plain_sentences()
    {
        var mismatch = RequestPolicy.EvaluateRunAction(
            new Request { Type = RequestType.RunAction, Action = ToolbeltAction.FullReset, Confirmed = true, ConfirmedOtherSessions = 1 },
            Caller, 3, new ServiceConfig(), null, new RateLimiter(TimeSpan.FromHours(1)));
        Assert.Equal(ResultCode.ConfirmationRequired, mismatch.Result);
        Assert.Equal("Other users are signed in now (3). Confirm again to continue.", mismatch.Message);

        var unknown = RequestPolicy.EvaluateRunAction(
            new Request { Type = RequestType.RunAction, Action = ToolbeltAction.RepairWU, Confirmed = true },
            Caller, -1, new ServiceConfig(), null, new RateLimiter(TimeSpan.FromHours(1)));
        Assert.Equal(ResultCode.MultiSessionBlocked, unknown.Result);
        Assert.Equal("The number of other signed-in users could not be determined, so this action cannot run now.", unknown.Message);
    }

    [Theory]
    [InlineData("Cannot invoke method. Method invocation is supported only on core types in this language mode.")]
    [InlineData("    + FullyQualifiedErrorId : MethodInvocationNotSupportedInConstrainedLanguage")]
    [InlineData("    + FullyQualifiedErrorId : CannotCreateTypeConstrainedLanguage")]
    [InlineData("File C:\\x.ps1 cannot be loaded because running scripts is disabled on this system.")]
    [InlineData("File C:\\x.ps1 cannot be loaded. The file is not digitally signed.")]
    public void Constrained_language_and_policy_errors_count_as_policy_failures(string line)
    {
        Assert.True(EndpointToolbelt.Platform.ScriptPolicy.IsPolicyFailure(new[] { "first line", line }));
    }

    [Fact]
    public void Other_errors_are_not_policy_failures()
    {
        Assert.False(EndpointToolbelt.Platform.ScriptPolicy.IsPolicyFailure(new[] { "System.UnauthorizedAccessException: Access to the path is denied." }));
    }
}

public class StopTimingTests
{
    [Fact]
    public void A_broadcast_is_bounded_however_slow_the_clients_are()
    {
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var slow = Enumerable.Range(0, 20).ToList();
        var watch = Stopwatch.StartNew();
        var allDone = PipeServer.SendToAll(slow, _ =>
        {
            Interlocked.Increment(ref calls);
            release.Wait(TimeSpan.FromSeconds(10));
            return false;
        }, TimeSpan.FromMilliseconds(500));
        watch.Stop();
        // Every send was started, even though the broadcast stopped waiting for them.
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 20, 5000));
        release.Set();

        Assert.False(allDone);
        // Parallel: twenty clients that each block for 10 s do not add up.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), "took " + watch.Elapsed);
        Assert.Equal(20, Volatile.Read(ref calls));
    }

    [Fact]
    public void A_broadcast_to_responsive_clients_reaches_everyone()
    {
        var reached = new List<int>();
        Assert.True(PipeServer.SendToAll(Enumerable.Range(0, 10).ToList(), i => { lock (reached) { reached.Add(i); } return true; }, TimeSpan.FromSeconds(5)));
        Assert.Equal(10, reached.Count);
        Assert.True(PipeServer.SendToAll(new List<int>(), _ => true, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void The_whole_stop_stays_below_25_seconds()
    {
        Assert.True(PipeServer.BroadcastOverallTimeout <= TimeSpan.FromSeconds(3));
        Assert.True(ToolbeltHost.RunStopWait <= TimeSpan.FromSeconds(15));
        Assert.True(ToolbeltHost.MaxStopTime < TimeSpan.FromSeconds(25), ToolbeltHost.MaxStopTime.ToString());
    }

    [Fact]
    public void A_fault_stop_exits_with_1064_and_does_not_tell_apps_to_close()
    {
        using var service = new ToolbeltWindowsService();
        Assert.True(service.NotifyAppsOnStop);
        var stopped = false;

        service.HandleFault(() => stopped = true);

        Assert.True(stopped);
        Assert.Equal(1064, service.ExitCode);
        Assert.False(service.NotifyAppsOnStop);
    }
}

public class EventSourceTests
{
    [Fact]
    public void Events_are_skipped_when_the_source_is_not_registered_and_never_create_it()
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var checks = new List<string>();
        var written = 0;
        var registered = false;
        var writer = new EventLogWriter(
            path => { checks.Add(path); return registered; },
            (_, _, _, _) => written++,
            () => now);

        writer.Write(EventId.ActionStarted, EventLogEntryType.Information, "one");
        writer.Write(EventId.ActionStarted, EventLogEntryType.Information, "two");
        Assert.Equal(0, written);
        Assert.Single(checks);
        Assert.Equal(@"SYSTEM\CurrentControlSet\Services\EventLog\Endpoint Toolbelt\Endpoint Toolbelt", checks[0]);

        // Registered later (for example by a repair): found at the next check.
        registered = true;
        now += EventLogWriter.SourceRecheck;
        writer.Write(EventId.ActionStarted, EventLogEntryType.Information, "three");
        Assert.Equal(1, written);
        writer.Write(EventId.ActionStarted, EventLogEntryType.Information, "four");
        Assert.Equal(2, written);
        Assert.Equal(2, checks.Count);
    }

    [Fact]
    public void Writes_go_to_the_products_source_and_failures_are_swallowed()
    {
        string source = null;
        var writer = new EventLogWriter(_ => true, (s, _, _, _) => { source = s; throw new InvalidOperationException("log full"); });
        writer.Write(EventId.ServiceError, EventLogEntryType.Error, "x");
        Assert.Equal(ProductInfo.EventSource, source);
    }
}

public class UninstallCleanupTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void The_working_folder_is_removed_only_below_the_allowed_parent()
    {
        var parent = _folder.Combine("systemprofile");
        var root = Path.Combine(parent, "AppData", "Local", "EndpointToolbelt");
        Directory.CreateDirectory(Path.Combine(root, "Results"));
        File.WriteAllText(Path.Combine(root, "Results", "x.json"), "{}");
        var outside = _folder.Combine("elsewhere", "EndpointToolbelt");
        Directory.CreateDirectory(outside);

        Assert.NotNull(UninstallCleanup.RemoveWorkingFolder(outside, parent));
        Assert.True(Directory.Exists(outside));

        Assert.Null(UninstallCleanup.RemoveWorkingFolder(root, parent));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(@"AppData\Local\EndpointToolbelt")]
    [InlineData(@"..\elsewhere")]
    [InlineData(@"\\server\share\EndpointToolbelt")]
    [InlineData("")]
    public void Relative_network_or_empty_paths_are_refused(string root)
    {
        Assert.NotNull(UninstallCleanup.CheckWorkingRoot(root, _folder.Combine("systemprofile")));
    }

    [Fact]
    public void The_parent_itself_a_sibling_and_unnormalized_paths_are_refused()
    {
        var parent = _folder.Combine("systemprofile");
        Directory.CreateDirectory(parent);
        Assert.NotNull(UninstallCleanup.CheckWorkingRoot(parent, parent));
        Assert.NotNull(UninstallCleanup.CheckWorkingRoot(parent + "2", parent));
        Assert.NotNull(UninstallCleanup.CheckWorkingRoot(parent + @"\AppData\..\..\elsewhere", parent));
        Assert.Null(UninstallCleanup.CheckWorkingRoot(parent + @"\AppData\Local\EndpointToolbelt", parent));
    }

    [Fact]
    public void The_production_parent_is_the_system_profile()
    {
        Assert.EndsWith(@"\System32\config\systemprofile", UninstallCleanup.SystemProfileFolder, StringComparison.OrdinalIgnoreCase);
        Assert.Null(UninstallCleanup.CheckWorkingRoot(UninstallCleanup.SystemProfileFolder + @"\AppData\Local\EndpointToolbelt", UninstallCleanup.SystemProfileFolder));
    }

    [Fact]
    public void State_and_an_empty_root_are_removed_but_other_values_keep_the_root()
    {
        // A scratch key under HKCU stands in for HKLM.
        var rootPath = @"Software\EndpointToolbelt-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            using (var root = Registry.CurrentUser.CreateSubKey(rootPath))
            {
                using (var state = root.CreateSubKey("State")) { state.SetValue("LastFullReset", "2026-10-15T12:00:00+02:00"); }
                root.CreateSubKey("Config").Dispose();
            }
            Assert.True(UninstallCleanup.RemoveStateKeys(Registry.CurrentUser, rootPath));
            using (var gone = Registry.CurrentUser.OpenSubKey(rootPath)) { Assert.Null(gone); }

            using (var root = Registry.CurrentUser.CreateSubKey(rootPath))
            {
                root.CreateSubKey("State").Dispose();
                using (var config = root.CreateSubKey("Config")) { config.SetValue("SupportText", "Contoso IT Service Desk"); }
            }
            Assert.False(UninstallCleanup.RemoveStateKeys(Registry.CurrentUser, rootPath));
            using (var kept = Registry.CurrentUser.OpenSubKey(rootPath))
            {
                Assert.NotNull(kept);
                Assert.DoesNotContain("State", kept.GetSubKeyNames());
                Assert.Contains("Config", kept.GetSubKeyNames());
            }

            Assert.True(UninstallCleanup.RemoveStateKeys(Registry.CurrentUser, rootPath + "-missing"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(rootPath, throwOnMissingSubKey: false);
        }
    }
}

public class ReviewRoundTwoCoordinatorTests : IDisposable
{
    private const string RunId = "run00003";
    private readonly TestFolder _folder = new();
    private readonly NullLog _log = new();
    private readonly FakeRunner _runner = new();
    private readonly ServicePaths _paths;
    private readonly CallerContext _caller = new("S-1-5-21-1000-2000-3000-1001", 2, "user");

    public ReviewRoundTwoCoordinatorTests()
    {
        var engine = _folder.Combine("engine");
        Directory.CreateDirectory(engine);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(engine), ProductInfo.EngineExecutable), "test");
        _paths = new ServicePaths(_folder.Combine("work"), engine);
        Directory.CreateDirectory(_paths.WorkFolder);
    }

    public void Dispose() => _folder.Dispose();

    private ActionCoordinator Create(bool multiSessionHost = false) =>
        new(_paths, _runner, new ResultStore(_paths.ResultsFolder), _log, _log,
            () => new ServiceConfig(), new RateLimiter(TimeSpan.FromHours(1)), "1.0.0", () => _folder.Combine("logs"), _ => null, () => RunId, () => multiSessionHost);

    private static string Rec(string type, string extra = "") => "{\"type\":\"" + type + "\",\"run\":\"" + RunId + "\"" + extra + "}";

    private static Request Run(ToolbeltAction action) => new() { Type = RequestType.RunAction, Action = action, Confirmed = true };

    private static List<string> Types(LastResultInfo info) =>
        info.Records.Select(r => MessageFraming.Deserialize<EngineRecordHeader>(System.Text.Encoding.UTF8.GetBytes(r)).Type).ToList();

    private LastResultInfo OnDisk() => new ResultStore(_paths.ResultsFolder).Load(_caller.Sid);

    [Fact]
    public void A_result_record_is_checkpointed_to_disk_while_the_run_goes_on_throttled()
    {
        LastResultInfo afterFirst = null, afterSecond = null;
        _runner.Behaviour = (_, onRecord, _) =>
        {
            onRecord(Rec("progress", ",\"text\":\"step\""));
            onRecord(Rec("change", ",\"text\":\"stopped wuauserv\""));
            afterFirst = OnDisk();
            onRecord(Rec("finding", ",\"id\":\"SECOND\""));
            afterSecond = OnDisk();
            return new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 0 };
        };
        Create().RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, new CollectingSink());

        // A power loss after the change would leave the change on disk.
        Assert.True(afterFirst.InProgress);
        Assert.Equal(new[] { "progress", "change" }, Types(afterFirst));
        // The second result record came within the checkpoint interval: saved with the final result.
        Assert.Equal(2, afterSecond.Records.Count);
        var final = OnDisk();
        Assert.False(final.InProgress);
        Assert.Equal(3, final.Records.Count);
    }

    [Fact]
    public void Summary_and_restart_required_are_kept_when_result_records_alone_exceed_the_cap()
    {
        var big = new string('f', 20_000);
        _runner.Behaviour = (_, onRecord, _) =>
        {
            for (var i = 0; i < 20; i++) { onRecord(Rec("finding", ",\"id\":\"F" + i.ToString("00") + "\",\"text\":\"" + big + "\"")); }
            onRecord(Rec("restart-required", ",\"text\":\"" + new string('r', 5_000) + "\""));
            onRecord(Rec("summary", ",\"exit\":0,\"text\":\"" + new string('s', 30_000) + "\""));
            return new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 0 };
        };
        var coordinator = Create();
        coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, new CollectingSink());

        var stored = coordinator.GetLastResult(_caller.Sid);
        var types = Types(stored);
        Assert.True(stored.RecordsTruncated);
        Assert.True(stored.Records.Sum(r => r.Length) <= ActionCoordinator.MaxStoredRecordChars);
        Assert.Equal("restart-required", types[types.Count - 2]);
        Assert.Equal("summary", types[types.Count - 1]);
        // The oldest findings made room; the kept ones are still in order.
        Assert.DoesNotContain(stored.Records, r => r.Contains("\"F00\""));
        var ids = stored.Records.Where(r => r.Contains("\"finding\"")).Select(r => r.Substring(r.IndexOf("\"F", StringComparison.Ordinal) + 2, 2)).ToList();
        Assert.Equal(ids.OrderBy(i => i), ids);
    }

    [Fact]
    public void Info_progress_makes_room_before_warning_progress()
    {
        var big = new string('x', 20_000);
        _runner.Behaviour = (_, onRecord, _) =>
        {
            for (var i = 0; i < 6; i++) { onRecord(Rec("progress", ",\"severity\":\"warning\",\"text\":\"W" + big + "\"")); }
            for (var i = 0; i < 6; i++) { onRecord(Rec("progress", ",\"severity\":\"info\",\"text\":\"I" + big + "\"")); }
            onRecord(Rec("change", ",\"text\":\"" + big + "\""));
            onRecord(Rec("summary", ",\"exit\":0"));
            return new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 0 };
        };
        var coordinator = Create();
        coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, new CollectingSink());

        var stored = coordinator.GetLastResult(_caller.Sid);
        Assert.Equal(6, stored.Records.Count(r => r.Contains("\"text\":\"W")));
        Assert.True(stored.Records.Count(r => r.Contains("\"text\":\"I")) < 6);
        Assert.Contains("change", Types(stored));
        Assert.Equal("summary", Types(stored).Last());
    }

    [Fact]
    public void The_policy_backstop_does_not_override_a_run_that_reported_records()
    {
        _runner.Behaviour = (_, onRecord, _) =>
        {
            onRecord(Rec("finding", ",\"id\":\"ENGINE-CONSTRAINED-LANGUAGE\""));
            var r = new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 1 };
            r.ErrorTail.Add("    + FullyQualifiedErrorId : MethodInvocationNotSupportedInConstrainedLanguage");
            return r;
        };
        var sink = new CollectingSink();
        Create().RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, sink);
        Assert.Equal(ResultCode.Completed, sink.Responses.Last().Result);
        Assert.Equal(1, sink.Responses.Last().ExitCode);
    }

    [Fact]
    public void A_finished_run_is_served_from_memory_until_it_is_released_and_a_stuck_disk_copy_reads_as_interrupted()
    {
        var coordinator = Create();
        FileStream blocker = null;
        _runner.Behaviour = (_, _, _) =>
        {
            // The final save cannot replace the in-progress copy while this handle is open.
            blocker = new FileStream(Path.Combine(_paths.ResultsFolder, _caller.Sid + ".json"), FileMode.Open, FileAccess.Read, FileShare.None);
            return new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 0 };
        };
        var t = new Thread(() => coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, new CollectingSink()));
        t.Start();
        var seenFromMemory = false;
        var watch = Stopwatch.StartNew();
        while (t.IsAlive && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            var result = coordinator.GetLastResult(_caller.Sid);
            var running = coordinator.GetRunning().Action;
            if (result != null && !result.InProgress && running != null)
            {
                Assert.Equal(ResultCode.Completed, result.Result);
                Assert.Equal(0, result.ExitCode);
                seenFromMemory = true;
            }
        }
        t.Join();
        blocker?.Dispose();
        Assert.True(seenFromMemory);
        Assert.Contains(_log.Lines, l => l.Contains("could not store the result (attempt 1)"));

        // The final save failed every time: the disk still says "in progress", which is not
        // the current run any more, so it reads as interrupted.
        var stuck = coordinator.GetLastResult(_caller.Sid);
        Assert.Equal(ResultCode.Interrupted, stuck.Result);
        Assert.False(stuck.InProgress);
        Assert.False(string.IsNullOrEmpty(stuck.EndedUtc));
    }

    [Fact]
    public void An_in_progress_copy_of_another_process_reads_as_interrupted()
    {
        new ResultStore(_paths.ResultsFolder).Save(_caller.Sid, new LastResultInfo { Action = ToolbeltAction.FullReset, RunId = "gone0001", InProgress = true });
        var result = Create().GetLastResult(_caller.Sid);
        Assert.Equal(ResultCode.Interrupted, result.Result);
        Assert.False(result.InProgress);
        Assert.False(string.IsNullOrEmpty(result.EndedUtc));
    }

    [Fact]
    public void Unreadable_stored_results_are_skipped_and_the_others_still_recovered()
    {
        var store = new ResultStore(_paths.ResultsFolder);
        File.WriteAllBytes(Path.Combine(_paths.ResultsFolder, "S-1-5-21-1-2-3-500.json"), new byte[4096]);
        File.WriteAllText(Path.Combine(_paths.ResultsFolder, "S-1-5-21-1-2-3-501.json"), "{not json");
        store.Save(_caller.Sid, new LastResultInfo { Action = ToolbeltAction.RepairWU, RunId = "old00002", InProgress = true });
        var problems = new List<string>();

        var changed = store.MarkInterrupted(DateTime.UtcNow, problems.Add);

        Assert.Single(changed);
        Assert.Equal(2, problems.Count);
        Assert.Equal(ResultCode.Interrupted, store.Load(_caller.Sid).Result);
        Assert.Null(store.Load("S-1-5-21-1-2-3-500"));
    }

    [Fact]
    public void Begin_shutdown_cancels_at_once_and_wait_idle_is_bounded()
    {
        var coordinator = Create();
        using var started = new ManualResetEventSlim();
        using var cancelled = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        _runner.Behaviour = (_, _, token) =>
        {
            started.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            if (token.IsCancellationRequested) { cancelled.Set(); }
            finish.Wait(TimeSpan.FromSeconds(10));
            return new EngineRunResult { Outcome = EngineOutcome.Cancelled };
        };
        var confirmed = Run(ToolbeltAction.RepairWU);
        confirmed.ConfirmationNonce = coordinator.Nonces.Issue(_caller.Sid);
        var t = new Thread(() => coordinator.RunAction(confirmed, _caller, 0, new CollectingSink()));
        t.Start();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        var watch = Stopwatch.StartNew();
        coordinator.BeginShutdown();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(cancelled.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(coordinator.WaitIdle(TimeSpan.FromMilliseconds(200)));

        finish.Set();
        Assert.True(coordinator.WaitIdle(TimeSpan.FromSeconds(10)));
        t.Join();
    }

    [Fact]
    public void Support_packages_have_no_limit_on_a_single_user_device()
    {
        _runner.Behaviour = (_, _, _) => new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 0 };
        var coordinator = Create(multiSessionHost: false);
        for (var i = 0; i < 8; i++)
        {
            var sink = new CollectingSink();
            coordinator.RunAction(Run(ToolbeltAction.CollectDiag), _caller, 0, sink);
            Assert.Equal(ResultCode.Completed, sink.Responses.Last().Result);
        }
    }

    [Fact]
    public void Support_packages_are_limited_per_user_on_a_multi_session_host()
    {
        _runner.Behaviour = (_, _, _) => new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 0 };
        var coordinator = Create(multiSessionHost: true);
        for (var i = 0; i < RequestPolicy.PackageRateLimitPerHour; i++)
        {
            var ok = new CollectingSink();
            coordinator.RunAction(Run(ToolbeltAction.CollectDiag), _caller, 0, ok);
            Assert.Equal(ResultCode.Completed, ok.Responses.Last().Result);
        }
        var sink = new CollectingSink();
        coordinator.RunAction(Run(ToolbeltAction.CollectDiag), _caller, 0, sink);
        var rejected = Assert.Single(sink.Responses);
        Assert.Equal(ResultCode.RateLimited, rejected.Result);
        Assert.Equal("Support packages were created too often on this shared device in the last hour. Try again later.", rejected.Message);

        // Another user has a limit of their own, and the package limit does not use the action limit.
        var other = new CallerContext("S-1-5-21-1000-2000-3000-1002", 3, "other");
        sink = new CollectingSink();
        coordinator.RunAction(Run(ToolbeltAction.CollectDiag), other, 0, sink);
        Assert.Equal(ResultCode.Completed, sink.Responses.Last().Result);
        sink = new CollectingSink();
        coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, sink);
        Assert.Equal(ResultCode.Completed, sink.Responses.Last().Result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Other_or_uncountable_sessions_make_a_single_user_device_shared_for_packages(int otherSessions)
    {
        var limiter = new RateLimiter(TimeSpan.FromHours(1));
        var packages = new RateLimiter(TimeSpan.FromHours(1));
        var request = Run(ToolbeltAction.CollectDiag);
        for (var i = 0; i < RequestPolicy.PackageRateLimitPerHour; i++)
        {
            Assert.True(RequestPolicy.EvaluateRunAction(request, _caller, otherSessions, new ServiceConfig(), null, limiter, false, packages).IsAccepted);
        }
        Assert.Equal(ResultCode.RateLimited, RequestPolicy.EvaluateRunAction(request, _caller, otherSessions, new ServiceConfig(), null, limiter, false, packages).Result);
        Assert.True(RequestPolicy.EvaluateRunAction(request, _caller, 0, new ServiceConfig(), null, limiter, false, packages).IsAccepted);
    }
}

public class MultiSessionHostTests
{
    private sealed class Registry : IRegistryReader
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public object GetValue(string subKey, string name) => Values.TryGetValue(subKey + "|" + name, out var v) ? v : null;
    }

    private const string Cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    [Theory]
    [InlineData("ServerRdsh", "Client", null, true)]
    [InlineData("EnterpriseMultiSession", "Client", null, true)]
    [InlineData("Enterprise", "Client", null, false)]
    [InlineData("Professional", "Client", 1, false)]
    [InlineData("ServerDatacenter", "Server", 1, true)]
    [InlineData("ServerDatacenter", "Server", 0, false)]
    public void Detects_multi_session_editions_and_session_hosts(string edition, string installation, int? appCompat, bool expected)
    {
        var r = new Registry();
        r.Values[Cv + "|EditionID"] = edition;
        r.Values[Cv + "|InstallationType"] = installation;
        if (appCompat.HasValue) { r.Values[@"SYSTEM\CurrentControlSet\Control\Terminal Server|TSAppCompat"] = appCompat.Value; }
        Assert.Equal(expected, EndpointToolbelt.Service.Platform.MultiSessionHost.Detect(r));
    }
}

public class StopOrderTests
{
    [Fact]
    public void The_run_is_cancelled_first_and_waited_for_after_the_broadcast()
    {
        var steps = new List<string>();
        TimeSpan waited = TimeSpan.MaxValue;
        ToolbeltHost.StopSequence(() => steps.Add("cancel"), () => steps.Add("accept"), () => steps.Add("broadcast"),
            w => { waited = w; steps.Add("wait"); return true; }, () => steps.Add("close"));
        Assert.Equal(new[] { "cancel", "accept", "broadcast", "wait", "close" }, steps);
        Assert.True(waited <= ToolbeltHost.RunStopWait);
    }

    [Fact]
    public void Without_a_broadcast_the_order_stays_the_same()
    {
        var steps = new List<string>();
        ToolbeltHost.StopSequence(() => steps.Add("cancel"), () => steps.Add("accept"), null, _ => { steps.Add("wait"); return true; }, () => steps.Add("close"));
        Assert.Equal(new[] { "cancel", "accept", "wait", "close" }, steps);
    }
}

public class EventSourceRecheckTests
{
    [Fact]
    public void A_source_removed_later_is_noticed_at_the_next_check()
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var registered = true;
        var written = 0;
        var writer = new EventLogWriter(_ => registered, (_, _, _, _) => written++, () => now);
        writer.Write(EventId.ActionStarted, EventLogEntryType.Information, "one");
        Assert.Equal(1, written);
        registered = false;
        now += EventLogWriter.SourceRecheck;
        writer.Write(EventId.ActionStarted, EventLogEntryType.Information, "two");
        Assert.Equal(1, written);
    }
}

public class UninstallCleanupEdgeTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void A_junction_above_the_root_is_refused()
    {
        var parent = _folder.Combine("systemprofile");
        var elsewhere = _folder.Combine("elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "Local", "EndpointToolbelt"));
        File.WriteAllText(Path.Combine(elsewhere, "Local", "EndpointToolbelt", "keep.txt"), "x");
        Directory.CreateDirectory(parent);
        TestFolder.CreateJunction(Path.Combine(parent, "AppData"), elsewhere);

        Assert.NotNull(UninstallCleanup.RemoveWorkingFolder(Path.Combine(parent, "AppData", "Local", "EndpointToolbelt"), parent));
        Assert.True(File.Exists(Path.Combine(elsewhere, "Local", "EndpointToolbelt", "keep.txt")));
    }

    [Fact]
    public void A_root_that_is_itself_a_junction_loses_only_the_link()
    {
        var parent = _folder.Combine("systemprofile");
        var local = Path.Combine(parent, "AppData", "Local");
        Directory.CreateDirectory(local);
        var target = _folder.Combine("target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        var root = Path.Combine(local, "EndpointToolbelt");
        TestFolder.CreateJunction(root, target);

        Assert.Null(UninstallCleanup.RemoveWorkingFolder(root, parent));
        Assert.False(Directory.Exists(root));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void The_root_key_is_kept_when_config_has_subkeys_or_the_root_has_values()
    {
        var rootPath = @"Software\EndpointToolbelt-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            using (var root = Registry.CurrentUser.CreateSubKey(rootPath))
            {
                root.CreateSubKey("State").Dispose();
                using (var config = root.CreateSubKey("Config")) { config.CreateSubKey("Extra").Dispose(); }
            }
            Assert.False(UninstallCleanup.RemoveStateKeys(Registry.CurrentUser, rootPath));
            using (var kept = Registry.CurrentUser.OpenSubKey(rootPath)) { Assert.NotNull(kept); Assert.DoesNotContain("State", kept.GetSubKeyNames()); }

            Registry.CurrentUser.DeleteSubKeyTree(rootPath);
            using (var root = Registry.CurrentUser.CreateSubKey(rootPath)) { root.SetValue("Owner", "someone else"); }
            Assert.False(UninstallCleanup.RemoveStateKeys(Registry.CurrentUser, rootPath));
            using (var kept = Registry.CurrentUser.OpenSubKey(rootPath)) { Assert.NotNull(kept); }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(rootPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Remove_state_does_nothing_unless_it_runs_as_system()
    {
        var parent = _folder.Combine("systemprofile");
        var root = Path.Combine(parent, "AppData", "Local", "EndpointToolbelt");
        Directory.CreateDirectory(root);
        var rootPath = @"Software\EndpointToolbelt-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            Registry.CurrentUser.CreateSubKey(rootPath + @"\State").Dispose();
            var reports = new List<string>();

            Assert.False(Program.RemoveState(() => false, root, parent, Registry.CurrentUser, rootPath, reports.Add));
            Assert.True(Directory.Exists(root));
            using (var kept = Registry.CurrentUser.OpenSubKey(rootPath)) { Assert.NotNull(kept); }

            Assert.True(Program.RemoveState(() => true, root, parent, Registry.CurrentUser, rootPath, reports.Add));
            Assert.False(Directory.Exists(root));
            using (var gone = Registry.CurrentUser.OpenSubKey(rootPath)) { Assert.Null(gone); }
            Assert.Empty(reports);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(rootPath, throwOnMissingSubKey: false);
        }
    }
}