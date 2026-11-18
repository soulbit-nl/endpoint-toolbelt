using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using EndpointToolbelt.Client;
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

/// <summary>
/// The pentest questions as a standing test. A real pipe server with the real request
/// handler, policy and coordinator (the engine is a fake), and a real trusted caller pipe with
/// the Windows calls replaced, attacked the way a local user or a foreign process would.
/// </summary>
public class PipeAttackTests : IDisposable
{
    private const string RunId = "atk00001";
    private static readonly string MySid = WindowsIdentity.GetCurrent().User.Value;
    private static readonly uint MyPid = (uint)Process.GetCurrentProcess().Id;

    // The production DACL grants INTERACTIVE; the tests grant the test user instead, so they also
    // run on a build agent without an interactive session (see PipeServerTests).
    private static string TestSddl => $"D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{MySid})";

    private readonly TestFolder _folder = new();
    private readonly NullLog _log = new();
    private readonly FakeRunner _runner = new();
    private readonly ActionCoordinator _coordinator;
    private readonly RequestHandler _handler;

    public PipeAttackTests()
    {
        var engine = _folder.Combine("engine");
        Directory.CreateDirectory(engine);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(engine), ProductInfo.EngineExecutable), "test");
        var paths = new ServicePaths(_folder.Combine("work"), engine);
        Directory.CreateDirectory(paths.WorkFolder);
        _runner.Behaviour = (_, _, _) => new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 0 };
        _coordinator = new ActionCoordinator(paths, _runner, new ResultStore(paths.ResultsFolder), _log, _log,
            () => new ServiceConfig(), new RateLimiter(TimeSpan.FromHours(1)), "1.1.0", () => _folder.Combine("logs"), _ => null, () => RunId);
        var state = new DeviceStateReader(new EmptyRegistry(), () => DateTime.UtcNow, () => DateTime.UtcNow.AddHours(-1), () => Array.Empty<(string, int)>());
        _handler = new RequestHandler(_coordinator, new NoOtherSessions(), state, () => new ServiceConfig(), _log, "1.1.0");
    }

    public void Dispose()
    {
        _coordinator.Shutdown(TimeSpan.FromSeconds(5));
        _folder.Dispose();
    }

    private sealed class EmptyRegistry : IRegistryReader
    {
        public object GetValue(string subKey, string name) => null;
    }

    private sealed class NoOtherSessions : ISessionCounter
    {
        public int CountOtherUserSessions(int callerSessionId) => 0;
    }

    private static string NewPipeName() => "etb-attack-" + Guid.NewGuid().ToString("N").Substring(0, 10);

    private static ToolbeltClient Open(string name) => ToolbeltClient.Connect(TimeSpan.FromSeconds(5), name, new ThisProcessVerifier());

    private static Response Ask(string name, Request request)
    {
        using var client = Open(name);
        client.Send(request);
        return client.Receive();
    }

    /// <summary>Sends raw bytes as the whole request and returns the first answer (null when the server just closed).</summary>
    private static Response SendRaw(string name, byte[] bytes)
    {
        using var client = Open(name);
        client.Stream.Write(bytes, 0, bytes.Length);
        client.Stream.Flush();
        try { return client.Receive(); }
        catch (IOException) { return null; }
    }

    private static byte[] Framed(byte[] body)
    {
        var frame = new byte[4 + body.Length];
        Buffer.BlockCopy(BitConverter.GetBytes(body.Length), 0, frame, 0, 4);
        Buffer.BlockCopy(body, 0, frame, 4, body.Length);
        return frame;
    }

    private static List<Response> RunToEnd(string name, Request request)
    {
        var responses = new List<Response>();
        using var client = Open(name);
        client.Send(request);
        while (true)
        {
            var response = client.Receive();
            if (response == null) { break; }
            responses.Add(response);
            if (response.Type == ResponseType.Rejected || response.Type == ResponseType.Completed) { break; }
        }
        return responses;
    }

    private PipeServer Serve(string name)
    {
        var server = new PipeServer(name, TestSddl, _handler, _log);
        server.Start();
        return server;
    }

    // --- The user pipe ----------------------------------------------------------------------------

    [Fact]
    public void A_request_with_another_protocol_version_is_rejected_before_anything_else()
    {
        var name = NewPipeName();
        using var server = Serve(name);
        var response = Ask(name, new Request { Type = RequestType.RunAction, Action = ToolbeltAction.SyncClock, ProtocolVersion = ProtocolInfo.Version + 1 });
        Assert.Equal(ResponseType.Rejected, response.Type);
        Assert.Equal(ResultCode.ProtocolMismatch, response.Result);
        Assert.Equal(ProtocolInfo.Version, Ask(name, new Request { Type = RequestType.GetStatus }).Status.ProtocolVersion);
    }

    [Fact]
    public void A_replayed_confirmation_runs_once_and_a_confirmation_without_a_status_never()
    {
        var name = NewPipeName();
        using var server = Serve(name);

        // Confirmed, but the warning was never shown: no status was asked for.
        var blind = Ask(name, new Request { Type = RequestType.RunAction, Action = ToolbeltAction.ClearAppCache, Confirmed = true, ConfirmationNonce = "forged" });
        Assert.Equal(ResultCode.ConfirmationRequired, blind.Result);

        var nonce = Ask(name, new Request { Type = RequestType.GetStatus }).Status.ConfirmationNonce;
        Assert.False(string.IsNullOrEmpty(nonce));
        var confirmed = new Request { Type = RequestType.RunAction, Action = ToolbeltAction.ClearAppCache, Confirmed = true, ConfirmationNonce = nonce };
        var first = RunToEnd(name, confirmed);
        Assert.Equal(ResponseType.Accepted, first[0].Type);
        Assert.Equal(ResponseType.Completed, first[first.Count - 1].Type);

        // The same bytes again: the nonce is spent.
        var replay = RunToEnd(name, confirmed);
        Assert.Equal(ResponseType.Rejected, Assert.Single(replay).Type);
        Assert.Equal(ResultCode.ConfirmationRequired, replay[0].Result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(ProtocolInfo.MaxRequestBytes + 1)]
    public void A_bad_length_header_is_rejected_without_reading_a_body(int length)
    {
        var name = NewPipeName();
        using var server = Serve(name);
        var response = SendRaw(name, BitConverter.GetBytes(length));
        Assert.NotNull(response);
        Assert.Equal(ResultCode.InvalidRequest, response.Result);
        Assert.Equal(ResponseType.Ack, Ask(name, new Request { Type = RequestType.Watch }).Type);
    }

    [Fact]
    public void Bad_UTF8_bad_JSON_an_unknown_type_and_a_cut_off_message_are_refused_and_the_server_goes_on()
    {
        var name = NewPipeName();
        using var server = Serve(name);

        var badUtf8 = SendRaw(name, Framed(new byte[] { 0x7B, 0xFF, 0xFE, 0x7D }));
        Assert.Equal(ResultCode.InvalidRequest, badUtf8.Result);

        var notJson = SendRaw(name, Framed(Encoding.UTF8.GetBytes("hello service")));
        Assert.Equal(ResultCode.InvalidRequest, notJson.Result);

        var unknownType = SendRaw(name, Framed(Encoding.UTF8.GetBytes("{\"ProtocolVersion\":" + ProtocolInfo.Version + ",\"Type\":99}")));
        Assert.Equal(ResultCode.InvalidRequest, unknownType.Result);
        Assert.Contains("Unknown request", unknownType.Message);

        // Header promises 50 bytes, 10 arrive, then the client goes away.
        var cut = new byte[14];
        Buffer.BlockCopy(BitConverter.GetBytes(50), 0, cut, 0, 4);
        using (var client = Open(name)) { client.Stream.Write(cut, 0, cut.Length); client.Stream.Flush(); }

        // A JSON object that is not a request shape at all.
        // A JSON value that is not a request object: refused (as unreadable, or as a request of
        // no version, which the serializer leaves at 0).
        var array = SendRaw(name, Framed(Encoding.UTF8.GetBytes("[1,2,3]")));
        Assert.True(array == null || array.Type == ResponseType.Rejected, "answer: " + array?.Result);

        Assert.Equal(ResponseType.Status, Ask(name, new Request { Type = RequestType.GetStatus }).Type);
    }

    [Fact]
    public void A_silent_client_is_dropped_and_does_not_hold_the_server()
    {
        var name = NewPipeName();
        using var server = Serve(name);
        using var silent = Open(name);
        var watch = Stopwatch.StartNew();
        Response answer;
        try { answer = silent.Receive(); }
        catch (IOException) { answer = null; }
        Assert.Null(answer);
        Assert.True(watch.Elapsed < PipeServer.RequestReadTimeout + TimeSpan.FromSeconds(10), "dropped after " + watch.Elapsed);
        Assert.Equal(ResponseType.Status, Ask(name, new Request { Type = RequestType.GetStatus }).Type);
    }

    [Fact]
    public void A_flood_of_garbage_connections_leaves_the_server_serving()
    {
        var name = NewPipeName();
        using var server = Serve(name);
        for (var i = 0; i < 120; i++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", name, PipeAccessRights.ReadData | PipeAccessRights.WriteData, PipeOptions.None, TokenImpersonationLevel.Identification, HandleInheritability.None);
                client.Connect(5000);
                if (i % 3 == 0) { client.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }, 0, 4); client.Flush(); }
                if (i % 3 == 1) { client.Write(BitConverter.GetBytes(1), 0, 4); client.Flush(); }
            }
            catch (IOException) { }
        }
        Assert.Equal(ResponseType.Status, Ask(name, new Request { Type = RequestType.GetStatus }).Type);
    }

    [Fact]
    public void A_huge_client_event_text_is_accepted_only_capped_and_never_breaks_the_service()
    {
        var name = NewPipeName();
        using var server = Serve(name);
        var text = new string('A', 60 * 1024);
        var response = Ask(name, new Request { Type = RequestType.ReportClientEvent, ClientEventId = EventId.ClientHealthCounts, ClientEventText = text });
        Assert.Equal(ResponseType.Ack, response.Type);
        Assert.Contains(EventId.ClientHealthCounts, _log.Events);
        var forged = Ask(name, new Request { Type = RequestType.ReportClientEvent, ClientEventId = EventId.ActionCompleted, ClientEventText = "done" });
        Assert.Equal(ResultCode.InvalidRequest, forged.Result);
        Assert.DoesNotContain(EventId.ActionCompleted, _log.Events);
    }

    // --- The trusted caller pipe ------------------------------------------------------------------

    private sealed class AttackSystem : ITrustedCallerSystem
    {
        public SecurityIdentifier CallerSid { get; set; } = new(MySid);
        public uint ServicePid { get; set; } = MyPid;
        public uint ClientSession { get; set; }
        public string Image { get; set; } = Process.GetCurrentProcess().MainModule.FileName;
        public string FolderProblem { get; set; }

        public SecurityIdentifier ServiceSid(string serviceName) => CallerSid;
        public uint ServiceProcessId(string serviceName) => ServicePid;
        public uint ClientProcessId(NamedPipeServerStream stream) => MyPid;
        public uint ClientSessionId(NamedPipeServerStream stream) => ClientSession;
        public string ProcessImagePath(uint processId) => Image;
        public string ValidateProgramFolder(string folder) => FolderProblem;
        public IReadOnlyList<int> UserSessions() => Array.Empty<int>();
        public int ConsoleSessionId() => -1;
        public string SessionUserSid(int sessionId) => null;
    }

    /// <summary>
    /// The caller pipe with the real identity check. The production DACL lets only SYSTEM create
    /// instances (checked in TrustedCallerTests); this test process is not SYSTEM, so it serves
    /// the pipe under the test DACL and attacks the process check behind it.
    /// </summary>
    private TrustedCallerPipe CallerPipe(string name, AttackSystem system, bool productionDacl = false)
    {
        var pipe = new TrustedCallerPipe(_handler, _log, _log, system, name, productionDacl ? null : _ => TestSddl);
        pipe.Refresh("ContosoAgent");
        Assert.Equal("ContosoAgent", pipe.OpenFor);
        return pipe;
    }

    [Fact]
    public void A_process_the_caller_DACL_does_not_name_cannot_open_the_caller_pipe()
    {
        var name = NewPipeName();
        // The policy names a service with its own SID; this test process is not it.
        using var pipe = CallerPipe(name, new AttackSystem { CallerSid = new SecurityIdentifier("S-1-5-80-1234567890-1234567890-1234567890-1234567890-1234567890") }, productionDacl: true);
        Assert.ThrowsAny<Exception>(() =>
        {
            using var client = new NamedPipeClientStream(".", name, PipeAccessRights.ReadData | PipeAccessRights.WriteData, PipeOptions.None, TokenImpersonationLevel.Identification, HandleInheritability.None);
            client.Connect(2000);
            client.Write(BitConverter.GetBytes(1), 0, 4);
            client.Flush();
            client.ReadByte();
        });
        Assert.DoesNotContain(EventId.TrustedCallerRefused, _log.Events);
    }

    [Theory]
    [InlineData("other process")]
    [InlineData("user session")]
    [InlineData("unprotected folder")]
    [InlineData("stopped service")]
    public void A_client_that_is_not_the_named_running_service_gets_no_answer_and_is_logged(string why)
    {
        var name = NewPipeName();
        var system = new AttackSystem();
        switch (why)
        {
            case "other process": system.ServicePid = MyPid + 4; break;
            case "user session": system.ClientSession = 1; break;
            case "unprotected folder": system.FolderProblem = "writable by other principals"; break;
            case "stopped service": system.ServicePid = 0; break;
        }
        using var pipe = CallerPipe(name, system);
        using (var client = Open(name))
        {
            client.Send(new Request { Type = RequestType.GetStatus });
            Response answer;
            try { answer = client.Receive(); }
            catch (IOException) { answer = null; }
            Assert.Null(answer);
        }
        Assert.Contains(EventId.TrustedCallerRefused, _log.Events);
    }

    [Fact]
    public void The_named_service_gets_status_actions_and_results_but_never_events_or_the_watch()
    {
        var name = NewPipeName();
        using var pipe = CallerPipe(name, new AttackSystem());

        var status = Ask(name, new Request { Type = RequestType.GetStatus });
        Assert.Equal(ResponseType.Status, status.Type);
        Assert.False(string.IsNullOrEmpty(status.Status.ConfirmationNonce));

        var watch = Ask(name, new Request { Type = RequestType.Watch });
        Assert.Equal(ResultCode.InvalidRequest, watch.Result);
        Assert.Contains("not available to the trusted service caller", watch.Message);
        var evt = Ask(name, new Request { Type = RequestType.ReportClientEvent, ClientEventId = EventId.ClientHealthCounts, ClientEventText = "x" });
        Assert.Equal(ResultCode.InvalidRequest, evt.Result);
        Assert.DoesNotContain(EventId.ClientHealthCounts, _log.Events);

        // The caller's confirmation needs its own nonce, like a user's.
        var blind = Ask(name, new Request { Type = RequestType.RunAction, Action = ToolbeltAction.FullReset, Confirmed = true, ConfirmedOtherSessions = 0 });
        Assert.Equal(ResultCode.ConfirmationRequired, blind.Result);
        var run = RunToEnd(name, new Request { Type = RequestType.RunAction, Action = ToolbeltAction.SyncClock });
        Assert.Equal(ResponseType.Completed, run[run.Count - 1].Type);
        var last = Ask(name, new Request { Type = RequestType.GetLastResult });
        Assert.Equal(ResponseType.LastResult, last.Type);
        Assert.Equal(ToolbeltAction.SyncClock, last.LastResult.Action);
    }

    [Fact]
    public void A_flood_of_read_only_diagnoses_is_limited_on_its_own_and_never_uses_up_the_repairs()
    {
        // The app diagnosis does not count toward UserRateLimitPerHour (20), but has a limit of its own.
        var name = NewPipeName();
        using var server = Serve(name);
        for (var i = 0; i < RequestPolicy.ReadOnlyRateLimitPerHour; i++)
        {
            var run = RunToEnd(name, new Request { Type = RequestType.RunAction, Action = ToolbeltAction.DiagnoseApps });
            Assert.Equal(ResponseType.Completed, run[run.Count - 1].Type);
        }
        var refused = Ask(name, new Request { Type = RequestType.RunAction, Action = ToolbeltAction.DiagnoseApps });
        Assert.Equal(ResultCode.RateLimited, refused.Result);
        var repair = RunToEnd(name, new Request { Type = RequestType.RunAction, Action = ToolbeltAction.SyncClock });
        Assert.Equal(ResponseType.Completed, repair[repair.Count - 1].Type);
        // An action value this service does not know (a newer client): refused before anything runs.
        Assert.Equal(ResultCode.InvalidRequest, Ask(name, new Request { Type = RequestType.RunAction, Action = (ToolbeltAction)99 }).Result);
    }

    // --- The contract --------------------------------------------------------------------------------

    [Fact]
    public void The_contract_document_lists_every_action_with_its_value()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        var contract = File.ReadAllText(Path.Combine(dir.FullName, "docs", "trusted-service-caller.md"));
        foreach (var action in ActionCatalog.All)
        {
            Assert.Contains("| `" + action + "` | " + ((int)action).ToString(System.Globalization.CultureInfo.InvariantCulture) + " |", contract);
        }
    }

    [Fact]
    public void The_contract_document_names_the_protocol_version_the_code_has()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        var contract = File.ReadAllText(Path.Combine(dir.FullName, "docs", "trusted-service-caller.md"));
        Assert.Contains("| Protocol version | **" + ProtocolInfo.Version + "**", contract);
        Assert.Equal(1, ProtocolInfo.Version);
    }
}