using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using EndpointToolbelt.Client;
using EndpointToolbelt.Native;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.Engine;
using EndpointToolbelt.Service.Logging;
using EndpointToolbelt.Service.Pipe;
using EndpointToolbelt.Service.Rules;
using EndpointToolbelt.Service.State;
using Xunit;
using EngineCheck = EndpointToolbelt.Platform.EngineCheck;

namespace EndpointToolbelt.Tests;

internal sealed class NullLog : IServiceLog, IEventWriter
{
    public List<string> Lines { get; } = new();
    public List<int> Events { get; } = new();
    public void Info(string message) { lock (Lines) { Lines.Add("I " + message); } }
    public void Warn(string message) { lock (Lines) { Lines.Add("W " + message); } }
    public void Error(string message) { lock (Lines) { Lines.Add("E " + message); } }
    public void Write(int eventId, EventLogEntryType type, string message) { lock (Events) { Events.Add(eventId); } }
}

/// <summary>Trusts a pipe server only if it is this test process (stands in for the service check).</summary>
internal sealed class ThisProcessVerifier : IServerVerifier
{
    public string Verify(NamedPipeClientStream pipe) =>
        PipeNative.GetServerProcessId(pipe.SafePipeHandle) == (uint)Process.GetCurrentProcess().Id ? null : "not this process";
}

internal sealed class CollectingSink : IResponseSink
{
    public Func<Response, bool> OnSend { get; set; }
    public List<Response> Responses { get; } = new();
    public bool Send(Response response)
    {
        lock (Responses) { Responses.Add(response); }
        return OnSend?.Invoke(response) ?? true;
    }
}

internal sealed class FakeRunner : IEngineRunner
{
    public Func<EngineStartInfo, Action<string>, CancellationToken, EngineRunResult> Behaviour { get; set; }

    public EngineRunResult Run(EngineStartInfo startInfo, Action<string> onRecord, CancellationToken cancellation) =>
        Behaviour(startInfo, onRecord, cancellation);

    public static EngineRequest ReadRequest(EngineStartInfo start) =>
        MessageFraming.Deserialize<EngineRequest>(File.ReadAllBytes(start.Arguments[start.Arguments.IndexOf("-RequestFile") + 1]));
}

public class PipeServerTests
{
    private static readonly string MySid = WindowsIdentity.GetCurrent().User.Value;

    // Like the production DACL, but granted to the test user instead of INTERACTIVE, so the
    // tests also run on build agents that are not interactive sessions. In the tests the
    // server runs as that same user, and creating further instances of a duplex pipe needs
    // full read/write access plus create-instance; in production the server is SYSTEM, which
    // has full control. Client rights are tested separately against the production DACL.
    private static string TestSddl => $"D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{MySid})";

    private sealed class EchoHandler : IRequestHandler
    {
        public CallerContext LastCaller { get; private set; }
        public void Handle(Request request, CallerContext caller, PipeConnection connection)
        {
            LastCaller = caller;
            connection.Send(new Response { Type = ResponseType.Ack, Message = request.Type.ToString() });
        }
    }

    private sealed class WatchHandler : IRequestHandler
    {
        public SemaphoreSlim Watching { get; } = new(0);
        public void Handle(Request request, CallerContext caller, PipeConnection connection)
        {
            if (request.Type != RequestType.Watch)
            {
                connection.Send(new Response { Type = ResponseType.Ack });
                return;
            }
            connection.Send(new Response { Type = ResponseType.Ack });
            Watching.Release();
            connection.WaitForClientClose();
        }
    }

    private static string NewPipeName() => "EndpointToolbelt-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);

    private static Response Ask(string name, Request request)
    {
        using var client = ToolbeltClient.Connect(TimeSpan.FromSeconds(5), name, new ThisProcessVerifier());
        client.Send(request);
        return client.Receive();
    }

    [Fact]
    public void Serves_a_request_and_identifies_the_caller_from_the_token()
    {
        var name = NewPipeName();
        var handler = new EchoHandler();
        var log = new NullLog();
        using var server = new PipeServer(name, TestSddl, handler, log);
        server.Start();

        var response = Ask(name, new Request { Type = RequestType.GetStatus });

        Assert.True(response != null, "No response. Server log: " + string.Join(" | ", log.Lines));
        Assert.Equal(ResponseType.Ack, response.Type);
        Assert.Equal("GetStatus", response.Message);
        Assert.Equal(MySid, handler.LastCaller.Sid);
        Assert.Equal(Process.GetCurrentProcess().SessionId, handler.LastCaller.SessionId);
    }

    [Fact]
    public void A_second_server_cannot_take_an_existing_pipe_name()
    {
        var name = NewPipeName();
        using var first = new PipeServer(name, TestSddl, new EchoHandler(), new NullLog());
        first.Start();
        using var second = new PipeServer(name, TestSddl, new EchoHandler(), new NullLog());
        Assert.Throws<PipeNameInUseException>(() => second.Start());
    }

    [NonElevatedFact]
    public void The_service_identity_check_refuses_a_server_that_is_not_the_service()
    {
        // This test process plays a user who squatted the pipe name: the pipe is owned by a
        // normal user and is not served by the service.
        var name = NewPipeName();
        using var squatter = new PipeServer(name, TestSddl, new EchoHandler(), new NullLog());
        squatter.Start();
        var ex = Assert.Throws<ServerIdentityException>(() => ToolbeltClient.Connect(TimeSpan.FromSeconds(5), name, new ServiceProcessVerifier()));
        Assert.Contains("owned", ex.Message);
    }

    [NonElevatedFact]
    public void Clients_cannot_open_the_pipe_with_generic_write_rights()
    {
        // GENERIC_WRITE includes FILE_APPEND_DATA (create pipe instance), which the production
        // DACL never grants to users. Only the first instance is needed, so the production DACL
        // is used here.
        var name = NewPipeName();
        using var server = new PipeServer(name, PipeServer.DefaultSddl, new EchoHandler(), new NullLog());
        server.Start();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        Assert.Throws<UnauthorizedAccessException>(() => client.Connect(5000));
    }

    [Fact]
    public void The_rights_the_client_asks_for_fit_inside_the_interactive_users_grant()
    {
        // Under the production DACL only SYSTEM can create further instances, so a test server
        // running as a normal user cannot serve it end to end. Instead the requested mask is
        // checked against the INTERACTIVE entry exactly (.NET adds SYNCHRONIZE to every open).
        var requested = (int)ToolbeltClient.RequestedRights | 0x100000;
        var granted = Convert.ToInt32(PipeServer.DefaultSddl.Split(new[] { "(A;;" }, StringSplitOptions.None)
            .Single(p => p.EndsWith(";;;IU)")).Split(';')[0], 16);
        Assert.Equal(0x120083, granted);
        Assert.Equal(requested, requested & granted);
        Assert.Equal(0, requested & 0x4); // never FILE_CREATE_PIPE_INSTANCE
    }

    [Fact]
    public void Oversized_requests_are_rejected()
    {
        var name = NewPipeName();
        using var server = new PipeServer(name, TestSddl, new EchoHandler(), new NullLog());
        server.Start();
        using var client = ToolbeltClient.Connect(TimeSpan.FromSeconds(5), name, new ThisProcessVerifier());
        client.Stream.Write(BitConverter.GetBytes(ProtocolInfo.MaxRequestBytes + 1), 0, 4);
        client.Stream.Flush();
        var response = client.Receive();
        Assert.Equal(ResponseType.Rejected, response.Type);
        Assert.Equal(ResultCode.InvalidRequest, response.Result);
    }

    [Fact]
    public void Clients_that_connect_and_close_at_once_do_not_stop_the_server()
    {
        var name = NewPipeName();
        using var server = new PipeServer(name, TestSddl, new EchoHandler(), new NullLog());
        server.Start();
        for (var i = 0; i < 30; i++)
        {
            using var c = new NamedPipeClientStream(".", name, PipeAccessRights.ReadData | PipeAccessRights.WriteData, PipeOptions.None, TokenImpersonationLevel.Identification, HandleInheritability.None);
            c.Connect(5000);
        }
        Assert.Equal(ResponseType.Ack, Ask(name, new Request { Type = RequestType.GetStatus }).Type);
    }

    [Fact]
    public void Watch_connections_receive_the_shutdown_notice()
    {
        var name = NewPipeName();
        var handler = new WatchHandler();
        using var server = new PipeServer(name, TestSddl, handler, new NullLog());
        server.Start();
        using var client = ToolbeltClient.Connect(TimeSpan.FromSeconds(5), name, new ThisProcessVerifier());
        client.Send(new Request { Type = RequestType.Watch });
        Assert.Equal(ResponseType.Ack, client.Receive().Type);
        Assert.True(handler.Watching.Wait(TimeSpan.FromSeconds(5)));

        server.StopAccepting();
        server.Broadcast(new Response { Type = ResponseType.ShuttingDown });
        Assert.Equal(ResponseType.ShuttingDown, client.Receive().Type);
    }

    [Fact]
    public void One_user_cannot_hold_more_than_the_per_user_number_of_connections()
    {
        var name = NewPipeName();
        var handler = new WatchHandler();
        using var server = new PipeServer(name, TestSddl, handler, new NullLog());
        server.Start();
        var watchers = new List<ToolbeltClient>();
        try
        {
            for (var i = 0; i < PipeServer.MaxConnectionsPerUser; i++)
            {
                var c = ToolbeltClient.Connect(TimeSpan.FromSeconds(5), name, new ThisProcessVerifier());
                watchers.Add(c);
                c.Send(new Request { Type = RequestType.Watch });
                Assert.Equal(ResponseType.Ack, c.Receive().Type);
            }
            // The next connection of the same user is closed without a response.
            Response extra = null;
            try { extra = Ask(name, new Request { Type = RequestType.GetStatus }); }
            catch (IOException) { }
            Assert.Null(extra);
        }
        finally
        {
            foreach (var w in watchers) { w.Dispose(); }
        }
        // Once the watchers are gone, the user is served again.
        Thread.Sleep(300);
        Assert.Equal(ResponseType.Ack, Ask(name, new Request { Type = RequestType.GetStatus }).Type);
    }

    [Fact]
    public void A_client_whose_account_cannot_be_determined_is_refused_and_logged_once()
    {
        var name = NewPipeName();
        var log = new NullLog();
        using var server = new PipeServer(name, TestSddl, new WatchHandler(), log) { OwnerLookup = _ => null };
        server.Start();
        for (var i = 0; i < 3; i++)
        {
            Response answer = null;
            try { answer = Ask(name, new Request { Type = RequestType.GetStatus }); }
            catch (IOException) { }
            Assert.Null(answer);
        }
        lock (log.Lines) { Assert.Single(log.Lines, l => l.StartsWith("W A client whose account could not be determined", StringComparison.Ordinal)); }

        // The refusals hold no slot: a client with a known account is served.
        server.OwnerLookup = ProcessOwner.TryGetSid;
        Thread.Sleep(300);
        Assert.Equal(ResponseType.Ack, Ask(name, new Request { Type = RequestType.GetStatus }).Type);
    }
}

public class ActionCoordinatorTests : IDisposable
{
    private const string RunId = "run00001";
    private readonly TestFolder _folder = new();
    private readonly NullLog _log = new();
    private readonly FakeRunner _runner = new();
    private readonly ServicePaths _paths;
    private readonly CallerContext _caller = new("S-1-5-21-1000-2000-3000-1001", 2, "user");
    private string _engineProblem;
    private readonly Queue<EngineCheck> _engineChecks = new();
    private ActionCoordinator _coordinator;

    public ActionCoordinatorTests()
    {
        var engine = _folder.Combine("engine");
        Directory.CreateDirectory(engine);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(engine), ProductInfo.EngineExecutable), "test");
        _paths = new ServicePaths(_folder.Combine("work"), engine);
        Directory.CreateDirectory(_paths.WorkFolder);
        _coordinator = Create(new ServiceConfig());
    }

    private ActionCoordinator Create(ServiceConfig config)
    {
        return new ActionCoordinator(_paths, _runner, new ResultStore(_paths.ResultsFolder), _log, _log,
            () => config, new RateLimiter(TimeSpan.FromHours(1)), "1.0.0", () => _folder.Combine("logs"), _ => NextEngineCheck(), () => RunId, engineRecheckDelay: TimeSpan.Zero);
    }

    /// <summary>The queued checks first, then the fixed problem (null: trusted).</summary>
    private EngineCheck NextEngineCheck()
        => _engineChecks.Count > 0 ? _engineChecks.Dequeue() : _engineProblem == null ? null : new EngineCheck(_engineProblem, couldNotOpen: false);

    public void Dispose() => _folder.Dispose();

    private static Request Run(ToolbeltAction action, bool confirmed = false) =>
        new() { Type = RequestType.RunAction, Action = action, Confirmed = confirmed };

    private static string Rec(string type, string extra = "") => "{\"type\":\"" + type + "\",\"run\":\"" + RunId + "\"" + extra + "}";

    private static EngineRunResult Exit(int code) => new() { Outcome = EngineOutcome.Exited, ExitCode = code };

    [Fact]
    public void A_run_streams_records_completes_and_stores_the_result()
    {
        _runner.Behaviour = (start, onRecord, _) =>
        {
            var request = FakeRunner.ReadRequest(start);
            Assert.Equal("RetryAppsAndPolicies", request.Action);
            Assert.Equal(RunId, request.RunId);
            Assert.Equal(2, request.CallerSessionId);
            Assert.Equal(1, request.Config.FullResetCooldownHours);
            Assert.Equal((int)ActionCatalog.Get(ToolbeltAction.RetryAppsAndPolicies).TimeLimit.TotalMinutes, request.TimeLimitMinutes);
            Assert.Equal(_caller.Sid, request.CallerSid);
            Assert.StartsWith(Path.Combine(_paths.WorkFolder, RunId), start.Environment["TEMP"]);
            onRecord(Rec("finding", ",\"id\":\"MDM-SYNC-OK\""));
            onRecord(Rec("summary"));
            return Exit(0);
        };
        var sink = new CollectingSink();

        _coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, sink);

        Assert.Equal(new[] { ResponseType.Accepted, ResponseType.Record, ResponseType.Record, ResponseType.Completed }, sink.Responses.Select(r => r.Type));
        var completed = sink.Responses.Last();
        Assert.Equal(ResultCode.Completed, completed.Result);
        Assert.Equal(0, completed.ExitCode);

        var stored = _coordinator.GetLastResult(_caller.Sid);
        Assert.False(stored.InProgress);
        Assert.Equal(2, stored.Records.Count);
        Assert.True(_coordinator.HasLastResult(_caller.Sid));
        Assert.Null(_coordinator.GetLastResult("S-1-5-21-1000-2000-3000-1002"));
        Assert.Empty(Directory.GetDirectories(_paths.WorkFolder));
        Assert.Null(_coordinator.GetRunning().Action);
        Assert.Contains(EventId.ActionStarted, _log.Events);
        Assert.Contains(EventId.ActionCompleted, _log.Events);
    }

    [Fact]
    public void Lines_without_the_current_run_id_are_ignored()
    {
        _runner.Behaviour = (_, onRecord, _) =>
        {
            onRecord("{\"type\":\"finding\",\"id\":\"FORGED\"}");
            onRecord("{\"type\":\"file\",\"run\":\"other\",\"name\":\"x.zip\"}");
            onRecord(Rec("summary"));
            return Exit(0);
        };
        var sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.CollectDiag), _caller, 0, sink);
        Assert.Single(sink.Responses, r => r.Type == ResponseType.Record);
        Assert.Single(_coordinator.GetLastResult(_caller.Sid).Records);
    }

    [Fact]
    public void A_vanished_client_does_not_keep_the_service_busy()
    {
        _runner.Behaviour = (_, onRecord, _) =>
        {
            for (var i = 0; i < 100; i++) { onRecord(Rec("progress")); }
            return Exit(0);
        };
        var sink = new CollectingSink { OnSend = _ => false };
        _coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, sink);
        Assert.Null(_coordinator.GetRunning().Action);
        Assert.Equal(100, _coordinator.GetLastResult(_caller.Sid).Records.Count);
    }

    [Fact]
    public void Only_one_action_runs_at_a_time_and_others_see_busy()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _runner.Behaviour = (_, _, _) =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return Exit(0);
        };
        var first = new Thread(() => _coordinator.RunAction(Run(ToolbeltAction.CollectDiag), _caller, 0, new CollectingSink()));
        first.Start();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        var other = new CallerContext("S-1-5-21-9", 3, "other");
        var sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), other, 0, sink);
        var rejected = Assert.Single(sink.Responses);
        Assert.Equal(ResultCode.Busy, rejected.Result);
        Assert.DoesNotContain("S-1-5-21-1000", rejected.Message);

        // The running user can watch progress; the other user gets no access to it.
        Assert.True(_coordinator.GetLastResult(_caller.Sid).InProgress);
        Assert.Null(_coordinator.GetLastResult(other.Sid));

        release.Set();
        first.Join();
    }

    [Fact]
    public void A_confirmation_without_a_shown_status_never_starts_the_engine_and_a_nonce_is_spent_by_one_run()
    {
        // The coordinator wires the nonce store into the policy and spends the nonce on acceptance.
        var starts = 0;
        _runner.Behaviour = (_, _, _) => { starts++; return new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 0 }; };
        var sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.ClearAppCache, true), _caller, 0, sink);
        Assert.Equal(ResultCode.ConfirmationRequired, Assert.Single(sink.Responses).Result);
        Assert.Equal(0, starts);

        var nonce = _coordinator.Nonces.Issue(_caller.Sid);
        var confirmed = Run(ToolbeltAction.ClearAppCache, true);
        confirmed.ConfirmationNonce = nonce;
        _coordinator.RunAction(confirmed, _caller, 0, new CollectingSink());
        Assert.Equal(1, starts);
        Assert.False(_coordinator.Nonces.IsValid(_caller.Sid, nonce));
        var again = new CollectingSink();
        _coordinator.RunAction(confirmed, _caller, 0, again);
        Assert.Equal(ResultCode.ConfirmationRequired, Assert.Single(again.Responses).Result);
        Assert.Equal(1, starts);
    }

    [Fact]
    public void Missing_confirmation_never_starts_the_engine()
    {
        _runner.Behaviour = (_, _, _) => throw new InvalidOperationException("must not run");
        var sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.FullReset), _caller, 0, sink);
        Assert.Equal(ResultCode.ConfirmationRequired, Assert.Single(sink.Responses).Result);
        Assert.Contains(EventId.ConfirmationMissing, _log.Events);
    }

    [Fact]
    public void Rejections_are_logged_only_up_to_a_limit()
    {
        _runner.Behaviour = (_, _, _) => throw new InvalidOperationException("must not run");
        for (var i = 0; i < 50; i++) { _coordinator.RunAction(Run(ToolbeltAction.FullReset), _caller, 0, new CollectingSink()); }
        Assert.Equal(10, _log.Events.Count(e => e == EventId.ConfirmationMissing));
        Assert.Equal(10, _log.Lines.Count(l => l.Contains("rejected")));
    }

    [Fact]
    public void Unknown_session_count_blocks_machine_wide_actions_only()
    {
        _runner.Behaviour = (_, _, _) => Exit(0);
        var sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.FullReset, confirmed: true), _caller, -1, sink);
        Assert.Equal(ResultCode.MultiSessionBlocked, Assert.Single(sink.Responses).Result);
        sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, -1, sink);
        Assert.Equal(ResultCode.Completed, sink.Responses.Last().Result);
    }

    [Fact]
    public void A_produced_package_is_stored_for_the_user_and_streamed_with_its_hash_after_the_worker_is_free()
    {
        var content = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();
        _runner.Behaviour = (start, onRecord, _) =>
        {
            File.WriteAllBytes(Path.Combine(FakeRunner.ReadRequest(start).OutputDirectory, "EndpointToolbelt_2026-10-15_1200.zip"), content);
            onRecord(Rec("file", ",\"name\":\"EndpointToolbelt_2026-10-15_1200.zip\""));
            return Exit(0);
        };
        ToolbeltAction? runningAtFileStart = ToolbeltAction.RepairWU;
        var sink = new CollectingSink();
        sink.OnSend = r =>
        {
            if (r.Type == ResponseType.FileStart) { runningAtFileStart = _coordinator.GetRunning().Action; }
            return true;
        };

        _coordinator.RunAction(Run(ToolbeltAction.CollectDiag), _caller, 0, sink);

        Assert.Null(runningAtFileStart);
        var start = sink.Responses.Single(r => r.Type == ResponseType.FileStart);
        var data = sink.Responses.Where(r => r.Type == ResponseType.FileChunk).SelectMany(r => Convert.FromBase64String(r.Data)).ToArray();
        Assert.Equal(content, data);
        Assert.Equal(content.Length, start.FileLength);
        using var sha = SHA256.Create();
        var expectedHash = BitConverter.ToString(sha.ComputeHash(content)).Replace("-", "").ToLowerInvariant();
        Assert.Equal(expectedHash, start.FileSha256);
        Assert.Equal(ResponseType.FileEnd, sink.Responses.Last().Type);
        var stored = _coordinator.GetLastResult(_caller.Sid);
        Assert.Equal("EndpointToolbelt_2026-10-15_1200.zip", stored.FileName);
        Assert.Equal(expectedHash, stored.FileSha256);
        Assert.NotNull(_coordinator.GetStoredFile(_caller.Sid, "EndpointToolbelt_2026-10-15_1200.zip"));
    }

    [Fact]
    public void A_failure_while_sending_the_package_does_not_overwrite_the_result()
    {
        _runner.Behaviour = (start, onRecord, _) =>
        {
            File.WriteAllBytes(Path.Combine(FakeRunner.ReadRequest(start).OutputDirectory, "pkg.zip"), new byte[] { 1, 2, 3 });
            onRecord(Rec("file", ",\"name\":\"pkg.zip\""));
            return Exit(0);
        };
        var sink = new CollectingSink { OnSend = r => r.Type == ResponseType.FileStart ? throw new IOException("client gone") : true };

        _coordinator.RunAction(Run(ToolbeltAction.CollectDiag), _caller, 0, sink);

        Assert.Single(sink.Responses, r => r.Type == ResponseType.Completed);
        var stored = _coordinator.GetLastResult(_caller.Sid);
        Assert.Equal(ResultCode.Completed, stored.Result);
        Assert.Equal("pkg.zip", stored.FileName);
    }

    [Theory]
    [InlineData(@"..\..\evil.zip")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("notes.txt")]
    [InlineData("NUL.zip")]
    [InlineData("pkg.zip\n")]
    public void Invalid_package_names_from_the_engine_are_ignored(string name)
    {
        _runner.Behaviour = (_, onRecord, _) =>
        {
            onRecord(Rec("file", ",\"name\":\"" + name.Replace("\\", "\\\\").Replace("\n", "\\n") + "\""));
            return Exit(0);
        };
        var sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.CollectDiag), _caller, 0, sink);
        Assert.DoesNotContain(sink.Responses, r => r.Type == ResponseType.FileStart);
        Assert.Null(_coordinator.GetLastResult(_caller.Sid).FileName);
    }

    [Fact]
    public void Stored_records_are_capped_by_size()
    {
        var big = Rec("progress", ",\"text\":\"" + new string('x', 10_000) + "\"");
        _runner.Behaviour = (_, onRecord, _) =>
        {
            for (var i = 0; i < 100; i++) { onRecord(big); }
            return Exit(0);
        };
        var sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, sink);
        Assert.Equal(100, sink.Responses.Count(r => r.Type == ResponseType.Record));
        var stored = _coordinator.GetLastResult(_caller.Sid);
        Assert.True(stored.RecordsTruncated);
        Assert.True(stored.Records.Sum(r => r.Length) <= ActionCoordinator.MaxStoredRecordChars);
    }

    [Fact]
    public void Timeouts_and_policy_refusals_map_to_service_result_codes()
    {
        _runner.Behaviour = (_, _, _) => new EngineRunResult { Outcome = EngineOutcome.TimedOut };
        var sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, sink);
        Assert.Equal(ResultCode.EngineTimeout, sink.Responses.Last().Result);
        Assert.Contains("servicing may still be running", sink.Responses.Last().Message);

        // App Control (577) or AppLocker (1260) refused the engine executable; or an attack
        // surface reduction rule or security software (access denied, 5).
        foreach (var code in new[] { 577, 1260, 5 })
        {
            _runner.Behaviour = (_, _, _) => new EngineRunResult { Outcome = EngineOutcome.FailedToStart, StartError = "blocked", StartErrorCode = code };
            sink = new CollectingSink();
            _coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, sink);
            Assert.Equal(ResultCode.EngineNotSigned, sink.Responses.Last().Result);
            Assert.Contains("attack surface reduction rule", sink.Responses.Last().Message);
        }
        Assert.Contains(EventId.EngineNotSigned, _log.Events);
        _runner.Behaviour = (_, _, _) => new EngineRunResult { Outcome = EngineOutcome.FailedToStart, StartError = "not found", StartErrorCode = 2 };
        sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, sink);
        Assert.Equal(ResultCode.EngineDidNotStart, sink.Responses.Last().Result);

        // A policy text on standard error is the engine's own business now: its exit code counts.
        _runner.Behaviour = (_, _, _) =>
        {
            var r = Exit(1);
            r.ErrorTail.Add("File x.ps1 cannot be loaded. The file is not digitally signed.");
            return r;
        };
        sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, sink);
        Assert.Equal(ResultCode.Completed, sink.Responses.Last().Result);

        _runner.Behaviour = (_, _, _) =>
        {
            var r = Exit(1);
            r.ErrorTail.Add("System.UnauthorizedAccessException: Access to the path is denied.");
            return r;
        };
        sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, sink);
        Assert.Equal(ResultCode.Completed, sink.Responses.Last().Result);
        Assert.Equal(1, sink.Responses.Last().ExitCode);
    }

    [Fact]
    public void A_missing_or_unprotected_engine_is_reported_without_starting_anything()
    {
        _runner.Behaviour = (_, _, _) => throw new InvalidOperationException("must not run");

        _engineProblem = "the engine folder is not protected (writable by other principals)";
        var sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, sink);
        Assert.Equal(ResultCode.EngineDidNotStart, sink.Responses.Last().Result);
        Assert.Contains(EventId.ServiceError, _log.Events);

        _engineProblem = null;
        File.Delete(_paths.EngineExecutable);
        sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, sink);
        Assert.Equal(ResultCode.EngineDidNotStart, sink.Responses.Last().Result);
    }

    [Fact]
    public void A_file_that_could_not_be_opened_is_checked_once_more_and_a_link_is_not()
    {
        var opened = new EngineCheck("EndpointToolbelt.Engine.exe could not be opened (Windows error 32: in use)", couldNotOpen: true);

        // Free a moment later: the run goes on.
        _runner.Behaviour = (_, _, _) => Exit(0);
        _engineChecks.Enqueue(opened);
        var sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, sink);
        Assert.Equal(ResultCode.Completed, sink.Responses.Last(r => r.Type == ResponseType.Completed).Result);
        Assert.Contains(_log.Lines, l => l.StartsWith("W ", StringComparison.Ordinal) && l.Contains("could not open a file: EndpointToolbelt.Engine.exe could not be opened (Windows error 32"));
        Assert.Contains(_log.Lines, l => l.Contains("the second engine check passed"));

        // Still in use: try again in a minute, not repair or reinstall.
        _runner.Behaviour = (_, _, _) => throw new InvalidOperationException("must not run");
        _engineChecks.Enqueue(opened);
        _engineChecks.Enqueue(opened);
        sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, sink);
        var refused = sink.Responses.Last();
        Assert.Equal(ResultCode.EngineDidNotStart, refused.Result);
        Assert.Contains("Try again in a minute", refused.Message);
        Assert.DoesNotContain("reinstall", refused.Message);
        Assert.Empty(_engineChecks);

        // A link or an extra hard link cannot be transient: one check, repair or reinstall.
        _engineChecks.Enqueue(new EngineCheck("EndpointToolbelt.Engine.exe is a link or has more than one hard link", couldNotOpen: false));
        _engineChecks.Enqueue(null);
        sink = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.RetryAppsAndPolicies), _caller, 0, sink);
        Assert.Contains("Repair or reinstall", sink.Responses.Last().Message);
        Assert.Single(_engineChecks);
        Assert.Contains(_log.Lines, l => l.Contains("the engine is not trusted: EndpointToolbelt.Engine.exe is a link or has more than one hard link"));
    }

    [Fact]
    public void Shutdown_cancels_the_run_stores_its_result_and_rejects_new_runs()
    {
        using var started = new ManualResetEventSlim();
        _runner.Behaviour = (_, _, token) =>
        {
            started.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            return new EngineRunResult { Outcome = EngineOutcome.Cancelled };
        };
        var sink = new CollectingSink();
        var confirmed = Run(ToolbeltAction.RepairWU, true);
        confirmed.ConfirmationNonce = _coordinator.Nonces.Issue(_caller.Sid);
        var t = new Thread(() => _coordinator.RunAction(confirmed, _caller, 0, sink));
        t.Start();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        _coordinator.Shutdown(TimeSpan.FromSeconds(10));
        t.Join();

        Assert.Equal(ResultCode.Interrupted, sink.Responses.Last().Result);
        Assert.Equal(ResultCode.Interrupted, _coordinator.GetLastResult(_caller.Sid).Result);
        var after = new CollectingSink();
        _coordinator.RunAction(Run(ToolbeltAction.CollectDiag), _caller, 0, after);
        Assert.Equal(ResultCode.ServiceUnavailable, Assert.Single(after.Responses).Result);
    }
}

public class RequestHandlerTests
{
    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("a\r\nb", "ab")]
    [InlineData("100% done", "100 done")]
    [InlineData("evil\u202Etxt.exe", "eviltxt.exe")]
    [InlineData("line\u2028sep", "linesep")]
    public void Client_event_text_is_cleaned(string input, string expected)
    {
        Assert.Equal(expected, RequestHandler.SanitizeEventText(input, 500));
    }

    [Fact]
    public void Client_event_text_is_capped()
    {
        Assert.Equal(10, RequestHandler.SanitizeEventText(new string('a', 100), 10).Length);
    }
}
