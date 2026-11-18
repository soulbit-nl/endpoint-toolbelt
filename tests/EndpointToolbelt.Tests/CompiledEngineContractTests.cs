using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.Engine;
using EndpointToolbelt.Service.Rules;
using EndpointToolbelt.Service.State;
using Xunit;
using EngineCheck = EndpointToolbelt.Platform.EngineCheck;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The route table and the dispatch of both engines: every action has exactly one
/// route, and the engine it is routed to has exactly one handler for it.
/// </summary>
public class EngineDispatchTests
{
    private static readonly string[] ActionNames = Enum.GetNames(typeof(ToolbeltAction));

    /// <summary>
    /// An action the contract test host has no handler for (it handles CollectDiag only): the
    /// contract tests use it to get a real engine run that changes nothing on this machine (87).
    /// Every action has a handler in the shipped engine, which would run it for real.
    /// </summary>
    internal const ToolbeltAction Unported = ToolbeltAction.SyncClock;

    /// <summary>An action name no engine knows: the shipped engine answers it with 87 without running anything.</summary>
    internal const string UnknownAction = "NoSuchAction";

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        Assert.True(dir != null, "Repository root not found.");
        return dir.FullName;
    }

    [Fact]
    public void Every_action_has_exactly_one_handler_in_the_engine()
    {
        Assert.Equal(ActionNames.OrderBy(n => n, StringComparer.Ordinal), ActionHandlers.Shipped.Keys.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(ActionCatalog.All.Select(a => a.ToString()).OrderBy(n => n, StringComparer.Ordinal), ActionHandlers.Shipped.Keys.OrderBy(n => n, StringComparer.Ordinal));
    }
}

/// <summary>
/// The real service coordinator and Job Object runner start a real compiled engine process: the
/// test host (the real entry point and run frame with a harmless support package handler, since
/// every real action changes or collects on the device) and the shipped engine itself, which has
/// an action name it does not know, which it must answer with 87.
/// </summary>
public class CompiledEngineContractTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static ToolbeltAction Unported => EngineDispatchTests.Unported;

    private static string Beside(string fileName)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
        Assert.True(File.Exists(path), path + " was not built next to the tests.");
        return path;
    }

    private ActionCoordinator Coordinator(string engineExecutable, string logs, NullLog log, Func<string, EngineCheck> validate = null) =>
        new(new ServicePaths(_folder.Combine("work"), _folder.Combine("Engine"), engineExecutable),
            new JobObjectEngineRunner(),
            new ResultStore(Path.Combine(_folder.Combine("work"), "Results")), log, log, () => new ServiceConfig(), new RateLimiter(TimeSpan.FromHours(1)),
            "1.0.0", () => logs, validateEngine: validate ?? (_ => null), newRunId: () => "c0mpiled");

    [Fact]
    public void The_service_runs_the_compiled_engine_and_accepts_its_records()
    {
        var logs = _folder.Combine("logs");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(_folder.Combine("work"));
        var log = new NullLog();
        var coordinator = Coordinator(Beside("EndpointToolbelt.Engine.TestHost.exe"), logs, log);
        var caller = new CallerContext("S-1-5-21-1-2-3-1001", 1, "user");
        var sink = new CollectingSink();

        coordinator.RunAction(new Request { Type = RequestType.RunAction, Action = ToolbeltAction.CollectDiag }, caller, 0, sink);

        // The produced file follows the Completed response.
        var completed = sink.Responses.LastOrDefault(r => r.Type == ResponseType.Completed);
        Assert.True(completed != null, "No Completed response: " + string.Join(" | ", sink.Responses.Select(r => r.Type)) + " | " + string.Join(" | ", log.Lines));
        Assert.Equal(ResponseType.FileEnd, sink.Responses.Last().Type);
        Assert.Equal(ResultCode.Completed, completed.Result);
        Assert.Equal(EngineExitCode.Success, completed.ExitCode);

        var records = sink.Responses.Where(r => r.Type == ResponseType.Record).Select(r => r.Record).ToList();
        Assert.Contains(records, r => r.Contains("\"type\":\"finding\"") && r.Contains("CONTRACT-TEST"));
        Assert.All(records, r => Assert.Contains("\"run\":\"c0mpiled\"", r));
        Assert.Contains(records, r => r.Contains("\"type\":\"summary\"") && r.Contains("\"exit\":0"));
        Assert.Contains(records, r => r.Contains("engine started. Action: CollectDiag. Run c0mpiled."));
        Assert.Contains(sink.Responses, r => r.Type == ResponseType.FileStart && r.FileName == "contract.zip" && r.FileLength == 22);
        Assert.NotNull(coordinator.GetStoredFile(caller.Sid, "contract.zip"));

        var stored = coordinator.GetLastResult(caller.Sid);
        Assert.Equal(records.Count, stored.Records.Count);
        Assert.Contains("[c0mpiled] EndpointToolbelt:SUMMARY exit=0", File.ReadAllText(Path.Combine(logs, "EndpointToolbelt.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_engine_runs_the_read_only_app_diagnosis_through_the_service_and_changes_nothing()
    {
        // The one action the shipped engine may run for real here, because it only reads. This
        // machine may have no IME (exit 0, no app state) or an IME whose keys a test user cannot
        // read (exit 1); either way the run closes its list and reports no change.
        var logs = _folder.Combine("logs-diagnose");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(_folder.Combine("work"));
        var log = new NullLog();
        var coordinator = Coordinator(Beside("EndpointToolbelt.Engine.exe"), logs, log);
        var caller = new CallerContext("S-1-5-21-1-2-3-1001", 1, "user");
        var sink = new CollectingSink();

        coordinator.RunAction(new Request { Type = RequestType.RunAction, Action = ToolbeltAction.DiagnoseApps }, caller, 0, sink);

        var completed = sink.Responses.Last(r => r.Type == ResponseType.Completed);
        Assert.Equal(ResultCode.Completed, completed.Result);
        Assert.Contains(completed.ExitCode, new int?[] { EngineExitCode.Success, EngineExitCode.Failed });
        var records = sink.Responses.Where(r => r.Type == ResponseType.Record).Select(r => r.Record).ToList();
        Assert.Contains(records, r => r.Contains("engine started. Action: DiagnoseApps. Run c0mpiled."));
        Assert.Single(records, r => r.Contains("\"type\":\"apps-diagnosis\"") && r.Contains("\"diagnosis\":\"c0mpiled\""));
        Assert.DoesNotContain(records, r => r.Contains("\"type\":\"change\""));
        Assert.Contains(records, r => r.Contains("\"type\":\"summary\"") && r.Contains("\"changes\":0") && r.Contains("\"restart\":false"));
        Assert.DoesNotContain(sink.Responses, r => r.Type == ResponseType.FileStart);
        Assert.Equal(ToolbeltAction.DiagnoseApps, coordinator.GetLastResult(caller.Sid).Action);
    }

    [Fact]
    public void The_engine_answers_87_for_an_action_it_has_no_handler_for()
    {
        Directory.CreateDirectory(_folder.Combine("work"));
        var log = new NullLog();
        var coordinator = Coordinator(Beside("EndpointToolbelt.Engine.TestHost.exe"), "", log);
        var caller = new CallerContext("S-1-5-21-1-2-3-1001", 1, "user");
        var sink = new CollectingSink();

        coordinator.RunAction(new Request { Type = RequestType.RunAction, Action = Unported, Confirmed = true, ConfirmedOtherSessions = 0 }, caller, 0, sink);

        var completed = sink.Responses.Last(r => r.Type == ResponseType.Completed);
        Assert.Equal(ResultCode.Completed, completed.Result);
        Assert.Equal(EngineExitCode.InvalidAction, completed.ExitCode);
        var records = sink.Responses.Where(r => r.Type == ResponseType.Record).Select(r => r.Record).ToList();
        Assert.Contains(records, r => r.Contains("\"id\":\"ENGINE-INVALID-REQUEST\""));
        Assert.All(records, r => Assert.Contains("\"run\":\"c0mpiled\"", r));
    }

    [Fact]
    public void A_missing_or_unprotected_compiled_engine_is_not_started()
    {
        Directory.CreateDirectory(_folder.Combine("work"));
        var caller = new CallerContext("S-1-5-21-1-2-3-1001", 1, "user");

        var log = new NullLog();
        var sink = new CollectingSink();
        Coordinator(_folder.Combine("missing.exe"), "", log).RunAction(new Request { Type = RequestType.RunAction, Action = Unported, Confirmed = true, ConfirmedOtherSessions = 0 }, caller, 0, sink);
        Assert.Equal(ResultCode.EngineDidNotStart, sink.Responses.Last(r => r.Type == ResponseType.Completed).Result);

        log = new NullLog();
        sink = new CollectingSink();
        Coordinator(Beside("EndpointToolbelt.Engine.exe"), "", log, _ => new EngineCheck("writable by other principals", couldNotOpen: false))
            .RunAction(new Request { Type = RequestType.RunAction, Action = Unported, Confirmed = true, ConfirmedOtherSessions = 0 }, caller, 0, sink);
        Assert.Equal(ResultCode.EngineDidNotStart, sink.Responses.Last(r => r.Type == ResponseType.Completed).Result);
        Assert.Contains(EventId.ServiceError, log.Events);
        Assert.DoesNotContain(sink.Responses, r => r.Type == ResponseType.Record);
    }

    [Fact]
    public void Starts_the_engine_with_fixed_arguments_and_no_PowerShell_module_path()
    {
        var info = EngineLaunch.Create(@"C:\Program Files\SoulBit\Endpoint Toolbelt\EndpointToolbelt.Engine.exe", ToolbeltAction.SyncClock, @"C:\work\request.json", @"C:\work\temp", TimeSpan.FromMinutes(10));
        Assert.Equal(@"C:\Program Files\SoulBit\Endpoint Toolbelt\EndpointToolbelt.Engine.exe", info.FileName);
        Assert.Equal(new[] { "-Action", "SyncClock", "-RequestFile", @"C:\work\request.json" }, info.Arguments);
        Assert.Equal(@"C:\Program Files\SoulBit\Endpoint Toolbelt", info.WorkingDirectory);
        Assert.False(info.Environment.ContainsKey("PSModulePath"));
        Assert.Equal(@"C:\work\temp", info.Environment["TEMP"]);
        Assert.Equal(TimeSpan.FromMinutes(10), info.TimeLimit);
        var paths = new ServicePaths(@"C:\work", @"C:\Program Files\SoulBit\Endpoint Toolbelt\Engine");
        Assert.Equal(@"C:\Program Files\SoulBit\Endpoint Toolbelt\EndpointToolbelt.Engine.exe", paths.EngineExecutable);
    }
}

/// <summary>The engine process itself: its standard output, exit codes and arguments.</summary>
public class CompiledEngineProcessTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static (int ExitCode, byte[] Output, string Error) Start(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi);
        var error = process.StandardError.ReadToEndAsync();
        using var buffer = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(buffer);
        Assert.True(process.WaitForExit(60000));
        return (process.ExitCode, buffer.ToArray(), error.Result);
    }

    private string Request(string name, string action, string runId)
    {
        // The product version carries non-ASCII text; it reaches the "engine started" line.
        var version = "v" + (char)0xE9 + (char)0x20AC;
        var path = _folder.Combine(name);
        File.WriteAllText(path, "{\"contractVersion\":1,\"runId\":\"" + runId + "\",\"action\":\"" + action + "\",\"otherSessions\":0,\"productVersion\":\"" + version +
            "\",\"outputDirectory\":\"" + _folder.Path.Replace("\\", "\\\\") + "\",\"config\":{}}", new UTF8Encoding(false));
        return path;
    }

    private static void AssertRecordStream(byte[] bytes, string runId)
    {
        Assert.True(bytes.Length > 3);
        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB, "standard output starts with a BOM");
        Assert.DoesNotContain(bytes, b => b >= 0x80);
        var lines = Encoding.UTF8.GetString(bytes).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, l => Assert.StartsWith("{\"type\":", l, StringComparison.Ordinal));
        Assert.All(lines, l => Assert.Equal(runId, JsonNode.Parse(l).Str("run")));
        Assert.Equal("summary", JsonNode.Parse(lines.Last()).Str("type"));
    }

    [Fact]
    public void Writes_ASCII_records_without_a_BOM_with_the_run_id_and_exits_with_the_engine_code()
    {
        var (exitCode, output, _) = Start("EndpointToolbelt.Engine.TestHost.exe", "-Action CollectDiag -RequestFile \"" + Request("entry.json", "CollectDiag", "e2e00001") + "\"");
        Assert.Equal(0, exitCode);
        AssertRecordStream(output, "e2e00001");
        var progress = Encoding.UTF8.GetString(output).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(JsonNode.Parse).Where(r => r.Str("type") == "progress").Select(r => r.Str("text"));
        Assert.Single(progress, t => t.Contains("v" + (char)0xE9 + (char)0x20AC));
    }

    [Fact]
    public void The_shipped_engine_exits_87_with_its_records_for_an_action_without_a_handler()
    {
        var unported = EngineDispatchTests.UnknownAction;
        var (exitCode, output, _) = Start("EndpointToolbelt.Engine.exe", "-Action " + unported + " -RequestFile \"" + Request("entry87.json", unported, "e2e00087") + "\"");
        Assert.Equal(87, exitCode);
        AssertRecordStream(output, "e2e00087");
        Assert.Contains("\"id\":\"ENGINE-INVALID-REQUEST\"", Encoding.ASCII.GetString(output), StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_other_arguments_with_exit_1_and_writes_no_record()
    {
        var (exitCode, output, error) = Start("EndpointToolbelt.Engine.exe", "-Action SyncClock");
        Assert.Equal(1, exitCode);
        Assert.Empty(output);
        Assert.Contains("Usage:", error, StringComparison.Ordinal);
        Assert.True(EngineEntry.TryParseArguments(new[] { "-requestfile", "r.json", "-ACTION", "SyncClock" }, out var action, out var file));
        Assert.Equal("SyncClock", action);
        Assert.Equal("r.json", file);
        Assert.False(EngineEntry.TryParseArguments(new[] { "-Action", "A", "-Action", "B" }, out _, out _));
        Assert.False(EngineEntry.TryParseArguments(new[] { "-Action", "A", "-File", "x" }, out _, out _));
    }

    [Fact]
    public void Turns_on_RedirectionGuard_for_real_read_back_from_the_system_in_a_process_of_its_own()
    {
        var (exitCode, output, _) = Start("EndpointToolbelt.Engine.TestHost.exe", "--redirection-guard");
        Assert.Equal(0, exitCode);
        // Some machines turn it on for every process already (the CI runner does); what counts is after.
        Assert.EndsWith(" True", Encoding.ASCII.GetString(output).Trim(), StringComparison.Ordinal);
    }
}
