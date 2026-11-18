using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.Engine;
using EndpointToolbelt.Service.Pipe;
using EndpointToolbelt.Service.Rules;
using EndpointToolbelt.Service.State;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>The trusted service caller extension point.</summary>
public class TrustedCallerTests : IDisposable
{
    private const string CallerSid = "S-1-5-80-1111111111-1222222222-1333333333-1444444444-1555555555";
    private const string ConsoleUserSid = "S-1-12-1-1111111111-1222222222-1333333333-1444444444";
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

    // --- Configuration ---------------------------------------------------------------------------

    [Fact]
    public void Without_the_policy_value_there_is_no_trusted_caller()
    {
        var c = ConfigLoader.Load(new Registry());
        Assert.Null(c.TrustedServiceCaller);
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public void The_trusted_caller_comes_from_the_policy_key()
    {
        var r = new Registry();
        r.Policy[ProductInfo.ServiceCallerPolicyValue] = "  ContosoAgent  ";
        Assert.Equal("ContosoAgent", ConfigLoader.Load(r).TrustedServiceCaller);
    }

    [Fact]
    public void A_value_under_the_configuration_key_is_ignored()
    {
        // The MSI writes the configuration key; only an administrator's policy may open the pipe.
        var r = new Registry();
        r.Config[ProductInfo.ServiceCallerPolicyValue] = "ContosoAgent";
        Assert.Null(ConfigLoader.Load(r).TrustedServiceCaller);
    }

    [Theory]
    [InlineData("Contoso Agent")]
    [InlineData("..\\evil")]
    [InlineData("-leading")]
    [InlineData("name;other")]
    [InlineData(ProductInfo.ServiceName)]
    public void Invalid_or_own_service_names_are_ignored_with_a_warning(string value)
    {
        var r = new Registry();
        r.Policy[ProductInfo.ServiceCallerPolicyValue] = value;
        var c = ConfigLoader.Load(r);
        Assert.Null(c.TrustedServiceCaller);
        Assert.Single(c.Warnings);
    }

    [Fact]
    public void A_service_name_longer_than_80_characters_is_ignored()
    {
        var r = new Registry();
        r.Policy[ProductInfo.ServiceCallerPolicyValue] = new string('a', 81);
        Assert.Null(ConfigLoader.Load(r).TrustedServiceCaller);
    }

    [Fact]
    public void A_non_string_value_is_ignored_with_a_warning()
    {
        var r = new Registry();
        r.Policy[ProductInfo.ServiceCallerPolicyValue] = 1;
        var c = ConfigLoader.Load(r);
        Assert.Null(c.TrustedServiceCaller);
        Assert.Single(c.Warnings);
    }

    // --- Pipe security -------------------------------------------------------------------------------

    [Fact]
    public void The_pipe_DACL_names_only_SYSTEM_and_the_caller_service()
    {
        var sddl = TrustedCaller.Sddl(new SecurityIdentifier(CallerSid));
        Assert.Equal("D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;0x120083;;;" + CallerSid + ")", sddl);
        Assert.DoesNotContain(";IU)", sddl);
        Assert.DoesNotContain(";BA)", sddl);
        Assert.DoesNotContain(";AU)", sddl);
        Assert.DoesNotContain(";WD)", sddl);
    }

    [Theory]
    [InlineData(RequestType.GetStatus, true)]
    [InlineData(RequestType.RunAction, true)]
    [InlineData(RequestType.GetLastResult, true)]
    [InlineData(RequestType.ReportClientEvent, false)]
    [InlineData(RequestType.Watch, false)]
    [InlineData((RequestType)99, false)]
    public void Only_status_actions_and_last_result_are_allowed(RequestType type, bool allowed)
    {
        Assert.Equal(allowed, TrustedCaller.IsAllowed(type));
    }

    // --- Client check --------------------------------------------------------------------------------

    private static string Verify(uint clientPid = 500, uint clientSession = 0, uint servicePid = 500,
        string image = @"C:\Program Files\Contoso\Agent\agent.exe", string folderProblem = null)
    {
        string checkedFolder = null;
        var result = TrustedCaller.VerifyClient(clientPid, clientSession, servicePid, image, f =>
        {
            checkedFolder = f;
            return folderProblem;
        });
        if (result == null) { Assert.Equal(@"C:\Program Files\Contoso\Agent", checkedFolder); }
        return result;
    }

    [Fact]
    public void The_named_service_process_in_session_0_from_a_protected_folder_is_accepted()
    {
        Assert.Null(Verify());
    }

    [Fact]
    public void A_client_in_a_user_session_is_refused()
    {
        Assert.Contains("session 0", Verify(clientSession: 1));
    }

    [Fact]
    public void Another_process_is_refused()
    {
        Assert.Contains("not the named service", Verify(clientPid: 501));
    }

    [Fact]
    public void A_stopped_service_is_refused()
    {
        Assert.Contains("not running", Verify(servicePid: 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("agent.exe")]
    public void An_unknown_program_file_is_refused(string image)
    {
        Assert.NotNull(Verify(image: image));
    }

    [Fact]
    public void A_program_folder_users_can_change_is_refused()
    {
        Assert.Contains("not protected", Verify(folderProblem: "writable by Users"));
    }

    // --- Scope (the remote support package rule) ------------------------------------------------------

    [Fact]
    public void The_console_user_is_the_scope_when_they_are_the_only_user()
    {
        var (session, sid) = TrustedCaller.ChooseScope(new[] { 2 }, 2, s => s == 2 ? ConsoleUserSid : null);
        Assert.Equal(2, session);
        Assert.Equal(ConsoleUserSid, sid);
    }

    [Theory]
    [InlineData(new int[0], 2)]           // nobody signed in
    [InlineData(new[] { 2, 3 }, 2)]       // two users
    [InlineData(new[] { 3 }, 2)]          // the only user is remote, not at the console
    [InlineData(new[] { 2 }, -1)]         // no console session
    public void Otherwise_only_the_device_is_in_scope(int[] sessions, int console)
    {
        Assert.Equal((0, (string)null), TrustedCaller.ChooseScope(sessions, console, _ => ConsoleUserSid));
    }

    [Fact]
    public void Uncountable_sessions_or_an_unreadable_user_mean_device_scope_only()
    {
        Assert.Equal((0, (string)null), TrustedCaller.ChooseScope(null, 2, _ => ConsoleUserSid));
        Assert.Equal((0, (string)null), TrustedCaller.ChooseScope(new[] { 2 }, 2, _ => null));
    }

    // --- Pipe lifecycle ------------------------------------------------------------------------------

    private sealed class FakeSystem : ITrustedCallerSystem
    {
        public Func<string, SecurityIdentifier> Sid { get; set; } = _ => new SecurityIdentifier(CallerSid);
        public SecurityIdentifier ServiceSid(string serviceName) => Sid(serviceName);
        public uint ServiceProcessId(string serviceName) => 0;
        public uint ClientProcessId(NamedPipeServerStream stream) => 0;
        public uint ClientSessionId(NamedPipeServerStream stream) => 0;
        public string ProcessImagePath(uint processId) => null;
        public string ValidateProgramFolder(string folder) => "not used";
        public IReadOnlyList<int> UserSessions() => Array.Empty<int>();
        public int ConsoleSessionId() => -1;
        public string SessionUserSid(int sessionId) => null;
    }

    private sealed class NoHandler : IRequestHandler
    {
        public void Handle(Request request, CallerContext caller, PipeConnection connection) => throw new InvalidOperationException("must not be called");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, int timeout);

    private const int ERROR_FILE_NOT_FOUND = 2;

    /// <summary>True when a pipe of that name exists (WaitNamedPipe fails with "not found" otherwise).</summary>
    private static bool PipeExists(string name)
    {
        if (WaitNamedPipe(@"\\.\pipe\" + name, 1)) { return true; }
        return Marshal.GetLastWin32Error() != ERROR_FILE_NOT_FOUND;
    }

    private static string TestPipeName() => "etb-test-caller-" + Guid.NewGuid().ToString("N").Substring(0, 12);

    [Fact]
    public void Without_the_policy_value_no_caller_pipe_is_created()
    {
        var name = TestPipeName();
        var log = new NullLog();
        using var pipe = new TrustedCallerPipe(new NoHandler(), log, log, new FakeSystem(), name);
        pipe.Refresh(null);
        Assert.Null(pipe.OpenFor);
        Assert.False(PipeExists(name));
        Assert.Empty(log.Events);
    }

    [Fact]
    public void The_pipe_opens_while_the_policy_names_an_installed_service_and_closes_when_it_is_removed()
    {
        var name = TestPipeName();
        var log = new NullLog();
        using var pipe = new TrustedCallerPipe(new NoHandler(), log, log, new FakeSystem(), name);

        pipe.Refresh("ContosoAgent");
        Assert.Equal("ContosoAgent", pipe.OpenFor);
        Assert.True(PipeExists(name));
        Assert.Contains(EventId.TrustedCallerPipe, log.Events);

        pipe.Refresh("ContosoAgent"); // unchanged: kept
        Assert.Equal("ContosoAgent", pipe.OpenFor);

        pipe.Refresh(null);
        Assert.Null(pipe.OpenFor);
        Assert.False(PipeExists(name));
    }

    [Fact]
    public void A_service_that_is_not_installed_gets_no_pipe_and_one_warning()
    {
        var name = TestPipeName();
        var log = new NullLog();
        using var pipe = new TrustedCallerPipe(new NoHandler(), log, log, new FakeSystem { Sid = _ => null }, name);
        pipe.Refresh("ContosoAgent");
        pipe.Refresh("ContosoAgent");
        Assert.Null(pipe.OpenFor);
        Assert.False(PipeExists(name));
        Assert.Single(log.Events);
    }

    [Fact]
    public void A_squatted_pipe_name_keeps_the_caller_pipe_closed()
    {
        var name = TestPipeName();
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances);
        var log = new NullLog();
        using var pipe = new TrustedCallerPipe(new NoHandler(), log, log, new FakeSystem(), name);
        pipe.Refresh("ContosoAgent");
        Assert.Null(pipe.OpenFor);
        Assert.Contains(EventId.PipeNameInUse, log.Events);
    }

    // --- Results and scope in the coordinator --------------------------------------------------------

    private (ActionCoordinator Coordinator, FakeRunner Runner) CreateCoordinator()
    {
        var engine = _folder.Combine("engine");
        Directory.CreateDirectory(engine);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(engine), ProductInfo.EngineExecutable), "test");
        var paths = new ServicePaths(_folder.Combine("work"), engine);
        Directory.CreateDirectory(paths.WorkFolder);
        var log = new NullLog();
        var runner = new FakeRunner();
        var runIds = 0;
        var coordinator = new ActionCoordinator(paths, runner, new ResultStore(paths.ResultsFolder), log, log,
            () => new ServiceConfig(), new RateLimiter(TimeSpan.FromHours(1)), "1.0.0", () => _folder.Combine("logs"), _ => null,
            () => "run" + Interlocked.Increment(ref runIds).ToString("00000", System.Globalization.CultureInfo.InvariantCulture));
        return (coordinator, runner);
    }

    private static Request Run(ToolbeltAction action) => new() { Type = RequestType.RunAction, Action = action };

    [Fact]
    public void A_trusted_caller_run_in_device_scope_passes_no_user_to_the_engine()
    {
        var (coordinator, runner) = CreateCoordinator();
        EngineRequest seen = null;
        runner.Behaviour = (start, _, _) =>
        {
            seen = FakeRunner.ReadRequest(start);
            return new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 0 };
        };
        var caller = new CallerContext(CallerSid, 0, @"NT SERVICE\ContosoAgent", null, isTrustedServiceCaller: true);
        coordinator.RunAction(Run(ToolbeltAction.SyncClock), caller, 0, new CollectingSink());
        Assert.Null(seen.CallerSid);
        Assert.Equal(0, seen.CallerSessionId);
    }

    [Fact]
    public void A_trusted_caller_run_for_the_console_user_passes_that_user_as_the_scope()
    {
        var (coordinator, runner) = CreateCoordinator();
        EngineRequest seen = null;
        runner.Behaviour = (start, _, _) =>
        {
            seen = FakeRunner.ReadRequest(start);
            return new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 0 };
        };
        var caller = new CallerContext(CallerSid, 2, @"NT SERVICE\ContosoAgent", ConsoleUserSid, isTrustedServiceCaller: true);
        coordinator.RunAction(Run(ToolbeltAction.CollectDiag), caller, 0, new CollectingSink());
        Assert.Equal(ConsoleUserSid, seen.CallerSid);
        Assert.Equal(2, seen.CallerSessionId);
    }

    [Fact]
    public void Results_of_the_trusted_caller_and_of_a_local_user_stay_apart()
    {
        var (coordinator, runner) = CreateCoordinator();
        runner.Behaviour = (_, _, _) => new EngineRunResult { Outcome = EngineOutcome.Exited, ExitCode = 0 };
        var remote = new CallerContext(CallerSid, 2, @"NT SERVICE\ContosoAgent", ConsoleUserSid, isTrustedServiceCaller: true);
        var local = new CallerContext(ConsoleUserSid, 2, "user");

        coordinator.RunAction(Run(ToolbeltAction.SyncClock), remote, 0, new CollectingSink());
        // The console user was the scope of the remote run, yet does not see its result.
        Assert.Null(coordinator.GetLastResult(local.Sid));
        Assert.Equal(ToolbeltAction.SyncClock, coordinator.GetLastResult(remote.Sid).Action);

        coordinator.RunAction(Run(ToolbeltAction.UpdateDefender), local, 0, new CollectingSink());
        // The trusted caller never sees the local user's result.
        Assert.Equal(ToolbeltAction.SyncClock, coordinator.GetLastResult(remote.Sid).Action);
        Assert.Equal(ToolbeltAction.UpdateDefender, coordinator.GetLastResult(local.Sid).Action);
    }

    [Fact]
    public void The_trusted_caller_gets_the_same_confirmation_and_multi_session_gates()
    {
        var (coordinator, runner) = CreateCoordinator();
        runner.Behaviour = (_, _, _) => throw new InvalidOperationException("must not run");
        var caller = new CallerContext(CallerSid, 0, @"NT SERVICE\ContosoAgent", null, isTrustedServiceCaller: true);

        var sink = new CollectingSink();
        coordinator.RunAction(Run(ToolbeltAction.FullReset), caller, 0, sink);
        Assert.Equal(ResultCode.ConfirmationRequired, Assert.Single(sink.Responses).Result);

        sink = new CollectingSink();
        coordinator.RunAction(new Request { Type = RequestType.RunAction, Action = ToolbeltAction.FullReset, Confirmed = true }, caller, 2, sink);
        Assert.Equal(ResultCode.ConfirmationRequired, Assert.Single(sink.Responses).Result);

        sink = new CollectingSink();
        coordinator.RunAction(new Request { Type = RequestType.RunAction, Action = ToolbeltAction.FullReset, Confirmed = true }, caller, -1, sink);
        Assert.Equal(ResultCode.MultiSessionBlocked, Assert.Single(sink.Responses).Result);
    }

    [Fact]
    public void A_local_caller_keeps_its_own_SID_as_the_scope()
    {
        var caller = new CallerContext("S-1-5-21-1000-2000-3000-1001", 2, "user");
        Assert.Equal(caller.Sid, caller.ScopeSid);
        Assert.False(caller.IsTrustedServiceCaller);
    }
}
