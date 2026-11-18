using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Protocol;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>A connection that plays back scripted responses; a step may also throw.</summary>
internal sealed class ScriptedConnection : IServiceConnection
{
    private readonly Queue<Func<Response>> _steps;

    public ScriptedConnection(params Func<Response>[] steps) => _steps = new Queue<Func<Response>>(steps);

    public List<Request> Sent { get; } = new();
    public bool Disposed { get; private set; }

    public void Send(Request request) => Sent.Add(request);

    public Response Receive() => _steps.Count == 0 ? null : _steps.Dequeue()();

    public void Dispose() => Disposed = true;

    public static Func<Response> R(Response response) => () => response;
    public static Func<Response> Throw(Exception ex) => () => throw ex;
}

public class AppGatewayTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Incoming => _folder.Combine("Incoming");

    private ServiceGateway Gateway(ScriptedConnection connection) => new(Incoming, _ => connection);

    private static Response Record(string json) => new() { Type = ResponseType.Record, Record = json };

    private static readonly Response Accepted = new() { Type = ResponseType.Accepted, RunId = "run1" };
    private static readonly Response Progress = Record("{\"type\":\"progress\",\"run\":\"run1\",\"text\":\"step\"}");

    private static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }

    [Fact]
    public void The_nonce_of_the_last_status_is_sent_with_the_next_action()
    {
        // One gateway, one connection per request; the status's nonce confirms the action.
        var status = new ScriptedConnection(ScriptedConnection.R(new Response { Type = ResponseType.Status, Status = new StatusInfo { ConfirmationNonce = "n-42" } }));
        var run = new ScriptedConnection(ScriptedConnection.R(new Response { Type = ResponseType.Rejected, Result = ResultCode.Busy }));
        var connections = new Queue<IServiceConnection>(new IServiceConnection[] { status, run });
        var gateway = new ServiceGateway(_folder.Combine("in"), _ => connections.Dequeue());
        Assert.Equal("n-42", gateway.GetStatus().ConfirmationNonce);
        gateway.Run(ToolbeltAction.FullReset, true, 0, null, out _);
        Assert.Equal("n-42", Assert.Single(run.Sent).ConfirmationNonce);
    }

    [Fact]
    public void A_rejection_is_the_result_and_the_request_is_sent_as_asked()
    {
        var connection = new ScriptedConnection(ScriptedConnection.R(new Response { Type = ResponseType.Rejected, Result = ResultCode.Busy, Message = "busy" }));
        var outcome = Gateway(connection).Run(ToolbeltAction.FullReset, true, 2, null, out var file);
        Assert.Equal(ResultCode.Busy, outcome.Result);
        Assert.Equal("busy", outcome.Message);
        Assert.False(outcome.InProgress);
        Assert.Null(file);
        var request = Assert.Single(connection.Sent);
        Assert.Equal(RequestType.RunAction, request.Type);
        Assert.Equal(2, request.ConfirmedOtherSessions);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public void A_connection_that_ends_after_acceptance_leaves_the_run_going()
    {
        // Cleanly closed before the result...
        var closed = Gateway(new ScriptedConnection(ScriptedConnection.R(Accepted), ScriptedConnection.R(Progress)))
            .Run(ToolbeltAction.RepairWU, true, 0, null, out _);
        Assert.True(closed.InProgress);
        Assert.Equal("run1", closed.RunId);

        // ...or cut, with or without records so far.
        var cut = Gateway(new ScriptedConnection(ScriptedConnection.R(Accepted), ScriptedConnection.Throw(new EndOfStreamException())))
            .Run(ToolbeltAction.RepairWU, true, 0, null, out _);
        Assert.True(cut.InProgress);
        var view = ResultPresenter.Final(cut);
        Assert.Contains("continues in the background", view.Status);
        Assert.DoesNotContain("could not start", view.Status);
    }

    [Fact]
    public void A_connection_lost_before_acceptance_is_a_failure_to_start()
    {
        var outcome = Gateway(new ScriptedConnection(ScriptedConnection.Throw(new IOException("pipe broken"))))
            .Run(ToolbeltAction.RetryAppsAndPolicies, false, -1, null, out _);
        Assert.False(outcome.InProgress);
        Assert.Equal(ResultCode.ServiceUnavailable, outcome.Result);
    }

    [Theory]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(SerializationException))]
    [InlineData(typeof(ArgumentException))]
    public void An_unreadable_message_is_a_version_mismatch_not_a_crash(Type type)
    {
        var ex = (Exception)Activator.CreateInstance(type, "bad frame");
        var outcome = Gateway(new ScriptedConnection(ScriptedConnection.Throw(ex))).Run(ToolbeltAction.ClearAppCache, true, -1, null, out _);
        Assert.Equal(ResultCode.ProtocolMismatch, outcome.Result);
        var status = Assert.Throws<ServiceException>(() => Gateway(new ScriptedConnection(ScriptedConnection.Throw(ex))).GetStatus());
        Assert.Equal(ResultCode.ProtocolMismatch, status.Code);
    }

    [Fact]
    public void A_service_that_cannot_be_opened_is_unavailable()
    {
        var gateway = new ServiceGateway(Incoming, _ => throw new InvalidOperationException("not installed"));
        Assert.Equal(ResultCode.ServiceUnavailable, gateway.Run(ToolbeltAction.CollectDiag, false, -1, null, out _).Result);
        Assert.Equal(ResultCode.ServiceUnavailable, Assert.Throws<ServiceException>(() => gateway.GetStatus()).Code);
        Assert.Null(gateway.GetLastResult(out _));
        gateway.ReportEvent(1003, "ignored");
    }

    [Fact]
    public void Following_never_starts_the_service_but_a_new_request_does()
    {
        var started = new List<bool>();
        var gateway = new ServiceGateway(Incoming, start => { started.Add(start); throw new InvalidOperationException(); });
        gateway.GetLastResult(out _);
        gateway.ReportEvent(1003, "x");
        Assert.Throws<ServiceException>(() => gateway.GetStatus());
        Assert.Equal(new[] { false, false, true }, started);
    }

    [Fact]
    public void A_cut_after_the_result_keeps_the_result_and_reports_the_lost_package()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var connection = new ScriptedConnection(
            ScriptedConnection.R(Accepted),
            ScriptedConnection.R(new Response { Type = ResponseType.Completed, Result = ResultCode.Completed, ExitCode = 0 }),
            ScriptedConnection.R(new Response { Type = ResponseType.FileStart, FileName = "pkg.zip", FileLength = 4, FileSha256 = Hash(data) }),
            ScriptedConnection.R(new Response { Type = ResponseType.FileChunk, Data = Convert.ToBase64String(data, 0, 2) }),
            ScriptedConnection.Throw(new IOException("cut")));
        var outcome = Gateway(connection).Run(ToolbeltAction.CollectDiag, false, -1, null, out var file);
        Assert.Equal(ResultCode.Completed, outcome.Result);
        Assert.Equal(0, outcome.ExitCode);
        Assert.False(outcome.InProgress);
        Assert.Null(file);
        Assert.False(string.IsNullOrEmpty(outcome.FileError));
        Assert.Empty(Directory.GetFiles(Incoming));
    }

    [Fact]
    public void A_complete_package_is_received_and_a_second_one_is_discarded()
    {
        var data = new byte[] { 5, 6, 7 };
        Func<Response>[] Package() => new[]
        {
            ScriptedConnection.R(new Response { Type = ResponseType.FileStart, FileName = "pkg.zip", FileLength = 3, FileSha256 = Hash(data) }),
            ScriptedConnection.R(new Response { Type = ResponseType.FileChunk, Data = Convert.ToBase64String(data) }),
            ScriptedConnection.R(new Response { Type = ResponseType.FileEnd }),
        };
        var steps = new List<Func<Response>>
        {
            ScriptedConnection.R(Accepted),
            ScriptedConnection.R(new Response { Type = ResponseType.Completed, Result = ResultCode.Completed, ExitCode = 0 }),
        };
        steps.AddRange(Package());
        steps.AddRange(Package());
        var outcome = Gateway(new ScriptedConnection(steps.ToArray())).Run(ToolbeltAction.CollectDiag, false, -1, null, out var file);
        Assert.Null(outcome.FileError);
        Assert.Equal(data, File.ReadAllBytes(file.Path));
        Assert.Single(Directory.GetFiles(Incoming));
    }

    [Fact]
    public void A_stored_result_with_a_broken_package_is_still_shown()
    {
        var connection = new ScriptedConnection(
            ScriptedConnection.R(new Response { Type = ResponseType.LastResult, LastResult = new LastResultInfo { Action = ToolbeltAction.RepairWU, RunId = "r9", Result = ResultCode.Completed, ExitCode = 2, RecordCount = 1, StartedUtc = "2026-10-15T10:00:00Z" } }),
            ScriptedConnection.R(Record("{\"type\":\"finding\",\"severity\":\"WARNING\",\"title\":\"t\"}")),
            ScriptedConnection.R(new Response { Type = ResponseType.FileStart, FileName = "pkg.zip", FileLength = 3, FileSha256 = "00" }),
            ScriptedConnection.R(new Response { Type = ResponseType.FileChunk, Data = "not base64!" }));
        var outcome = Gateway(connection).GetLastResult(out var file);
        Assert.NotNull(outcome);
        Assert.Equal("r9", outcome.RunId);
        Assert.Single(outcome.Records);
        Assert.Equal(new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc), outcome.StartedUtc);
        Assert.Null(file);
        Assert.False(string.IsNullOrEmpty(outcome.FileError));
        Assert.Empty(Directory.GetFiles(Incoming));

        // An unreadable message ends the request with no result instead of an error.
        Assert.Null(Gateway(new ScriptedConnection(ScriptedConnection.Throw(new SerializationException()))).GetLastResult(out _));
    }

    [Fact]
    public void Records_beyond_the_cap_are_dropped_with_one_warning()
    {
        var steps = new List<Func<Response>> { ScriptedConnection.R(Accepted) };
        steps.AddRange(Enumerable.Repeat(ScriptedConnection.R(Progress), ServiceGateway.MaxRecords + 5));
        steps.Add(ScriptedConnection.R(new Response { Type = ResponseType.Completed, Result = ResultCode.Completed, ExitCode = 0 }));
        var seen = 0;
        var outcome = Gateway(new ScriptedConnection(steps.ToArray())).Run(ToolbeltAction.RepairWU, true, 0, _ => seen++, out _);
        Assert.Equal(ServiceGateway.MaxRecords + 1, outcome.Records.Count);
        Assert.Equal("warning", outcome.Records.Last().Type);
        Assert.Equal(ServiceGateway.MaxRecords + 1, seen);
        // The cut is marked, so the STEPS list knows that later steps are only in the logs.
        Assert.True(outcome.Truncated);
        Assert.True(outcome.Records.Last().IsCut);
    }

    [Fact]
    public void Old_received_files_are_cleaned_up()
    {
        Directory.CreateDirectory(Incoming);
        var old = Path.Combine(Incoming, "old.zip");
        var fresh = Path.Combine(Incoming, "fresh.zip");
        File.WriteAllText(old, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-3));
        new ServiceGateway(Incoming, _ => throw new InvalidOperationException()).CleanIncoming(TimeSpan.FromHours(1));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void A_file_needs_a_checksum_and_room_on_the_drive()
    {
        Assert.Throws<IOException>(() => new ServiceGateway.FileReceiver(Incoming, new Response { FileName = "pkg.zip", FileLength = 1, FileSha256 = "" }));
        Assert.Throws<IOException>(() => new ServiceGateway.FileReceiver(Incoming, new Response { FileName = "pkg.zip", FileLength = ServiceGateway.MaxFileBytes + 1, FileSha256 = "00" }));
        var full = Assert.Throws<IOException>(() => new ServiceGateway.FileReceiver(Incoming, new Response { FileName = "pkg.zip", FileLength = 1000, FileSha256 = "00" }, _ => 10));
        Assert.Contains("free disk space", full.Message);

        // A zero-length file is a valid (if useless) file.
        using var empty = new ServiceGateway.FileReceiver(Incoming, new Response { FileName = "pkg.zip", FileLength = 0, FileSha256 = Hash(Array.Empty<byte>()) });
        var received = empty.Finish();
        Assert.True(File.Exists(received.Path));
    }

    [Fact]
    public void The_watcher_survives_errors_and_reports_a_shutdown()
    {
        var attempts = 0;
        using var fired = new ManualResetEventSlim();
        var watcher = new ServiceWatcher(() =>
        {
            attempts++;
            if (attempts == 1) { throw new SerializationException("odd"); }
            if (attempts == 2) { return new ScriptedConnection(ScriptedConnection.Throw(new InvalidDataException())); }
            return new ScriptedConnection(ScriptedConnection.R(new Response { Type = ResponseType.ShuttingDown }));
        }, TimeSpan.FromMilliseconds(10));
        watcher.ShuttingDown += () => { fired.Set(); throw new InvalidOperationException("a subscriber that fails"); };
        watcher.Start();
        Assert.True(fired.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(watcher.Join(TimeSpan.FromSeconds(10)));
        watcher.Dispose();
    }

    [Fact]
    public void The_watcher_stops_when_disposed()
    {
        var watcher = new ServiceWatcher(() => throw new IOException("not running"), TimeSpan.FromSeconds(30));
        watcher.Start();
        watcher.Dispose();
        Assert.True(watcher.Join(TimeSpan.FromSeconds(5)));
    }
}

public class AppPackageSaveTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static RunLogHeader Header(ToolbeltAction action) => new() { Outcome = new RunOutcome { Action = action, Result = ResultCode.Completed, ExitCode = 1 }, Zone = TimeZoneInfo.Utc };

    private ReceivedFile Package(params (string Name, string Text)[] entries)
    {
        var path = _folder.Combine(Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        return new ReceivedFile { Name = "pkg.zip", Path = path };
    }

    [Fact]
    public void Same_named_entries_from_the_service_are_not_doubled()
    {
        var downloads = _folder.Combine("Downloads");
        var report = _folder.Combine("report.txt");
        File.WriteAllText(report, "mine");
        var (path, _) = SupportPackage.Save(Package(("HealthReport.txt", "theirs"), ("MDMDiagnostics/a.txt", "x")), downloads, DateTime.Now, report, null, null, "u", "pc");
        using var zip = ZipFile.OpenRead(path);
        var entry = Assert.Single(zip.Entries, e => e.FullName == SupportPackage.ReportEntry);
        using var reader = new StreamReader(entry.Open());
        Assert.Equal("mine", reader.ReadToEnd());
        Assert.Empty(Directory.GetFiles(downloads, "*.partial"));
    }

    [Fact]
    public void A_package_that_cannot_be_saved_stays_where_it_was()
    {
        var broken = _folder.Combine("broken.zip");
        File.WriteAllText(broken, "this is not a zip");
        var downloads = _folder.Combine("Downloads");
        var ex = Assert.Throws<PackageSaveException>(() => SupportPackage.Save(new ReceivedFile { Name = "pkg.zip", Path = broken }, downloads, DateTime.Now, null, null, null, "u", "pc"));
        Assert.Equal(broken, ex.ReceivedPath);
        Assert.True(File.Exists(broken));
        Assert.Empty(Directory.GetFiles(downloads));
    }

    [Fact]
    public void A_users_file_that_cannot_be_read_is_left_out()
    {
        var downloads = _folder.Combine("Downloads");
        var history = _folder.Combine("history.log");
        File.WriteAllText(history, "h");
        using (new FileStream(history, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var (path, notAdded) = SupportPackage.Save(Package(("a.txt", "x")), downloads, DateTime.Now, null, history, null, "u", "pc");
            Assert.Equal(new[] { SupportPackage.HistoryEntry }, notAdded);
            Assert.True(File.Exists(path));
        }
    }

    [Fact]
    public void Two_runs_of_the_same_action_get_two_logs_in_Downloads()
    {
        // A second run no longer replaces the first; the same run written again replaces its own.
        var downloads = _folder.Combine("Downloads");
        RunLogHeader WithId(string id) { var h = Header(ToolbeltAction.ClearAppCache); h.Outcome.RunId = id; return h; }
        var first = SupportPackage.SaveRunLog(WithId("aaaa1111"), new[] { "one" }, Array.Empty<ResultCard>(), downloads);
        var second = SupportPackage.SaveRunLog(WithId("bbbb2222"), new[] { "two" }, Array.Empty<ResultCard>(), downloads);
        Assert.Equal(Path.Combine(downloads, "EndpointToolbelt_ClearAppCache_aaaa1111.log"), first);
        Assert.Equal(Path.Combine(downloads, "EndpointToolbelt_ClearAppCache_bbbb2222.log"), second);
        Assert.Contains("one", File.ReadAllText(first));
        Assert.Contains("two", File.ReadAllText(second));
        var again = SupportPackage.SaveRunLog(WithId("bbbb2222"), new[] { "two, later" }, Array.Empty<ResultCard>(), downloads);
        Assert.Equal(second, again);
        Assert.Contains("two, later", File.ReadAllText(again));
        Assert.Equal(2, Directory.GetFiles(downloads).Length);

        // Without a run ID: named by the time, with _1 for a second one in the same second.
        var at = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        RunLogHeader NoId() { var h = Header(ToolbeltAction.ClearAppCache); h.NowUtc = at; return h; }
        Assert.Equal(Path.Combine(downloads, "EndpointToolbelt_ClearAppCache_20261015-120000Z.log"), SupportPackage.SaveRunLog(NoId(), new[] { "x" }, Array.Empty<ResultCard>(), downloads));
        Assert.Equal(Path.Combine(downloads, "EndpointToolbelt_ClearAppCache_20261015-120000Z_1.log"), SupportPackage.SaveRunLog(NoId(), new[] { "y" }, Array.Empty<ResultCard>(), downloads));
    }

    [Fact]
    public void Settings_that_are_not_numbers_are_dropped()
    {
        var path = _folder.Combine("settings.json");
        File.WriteAllText(path, "{\"Left\":\"NaN\",\"Top\":1,\"Width\":\"Infinity\",\"Height\":2,\"LastShownRunId\":\"r\"}");
        var loaded = AppSettings.Load(path);
        Assert.Null(loaded.Left);
        Assert.Null(loaded.Width);
        Assert.Equal("r", loaded.LastShownRunId);
        File.WriteAllText(path, "null");
        Assert.Null(AppSettings.Load(path).Width);
        // Saving replaces the file whole.
        new AppSettings { Width = 500 }.Save(path);
        new AppSettings { Width = 600 }.Save(path);
        Assert.Equal(600, AppSettings.Load(path).Width);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void History_is_trimmed_and_an_unreadable_one_is_said_so()
    {
        var path = _folder.Combine("history.log");
        File.WriteAllLines(path, Enumerable.Range(0, 6000).Select(i => "2026-10-15 10:00:00 +02:00;user;Action" + i + ";0"));
        History.Add("Last", "0", path);
        var lines = File.ReadAllLines(path);
        Assert.Equal(History.KeepLines, lines.Length);
        Assert.EndsWith(";Last;0", lines.Last());
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(History.Tail(5, path));
            Assert.Contains("(history unavailable)", DeviceReport.Build(new DeviceFacts(), null, new List<HealthResult>(), History.Tail(5, path), DateTimeOffset.Now, "1.0.0"));
        }
    }
}

public class AppPresentationTests
{
    private static EngineRecord Finding(string severity, string title) => new() { Type = "finding", Severity = severity, Title = title, Detail = "", NextStep = "" };

    [Fact]
    public void A_stopped_run_still_says_what_it_changed()
    {
        var outcome = new RunOutcome { Action = ToolbeltAction.FullReset, Result = ResultCode.EngineTimeout };
        outcome.Records.Add(new EngineRecord { Type = "change", Text = "Removed app state" });
        var view = ResultPresenter.Final(outcome);
        Assert.Equal(CardKind.Warning, view.Cards[0].Kind);
        Assert.Contains(view.Cards, c => c.Kind == CardKind.Change && c.Title == "1 change made by this run");
        Assert.True(view.CopyLog);
    }

    [Fact]
    public void An_earlier_repair_is_named_as_such_and_asks_for_a_restart_only_if_not_done()
    {
        var outcome = new RunOutcome { Action = ToolbeltAction.RepairWU, Result = ResultCode.Completed, ExitCode = 0 };
        outcome.Records.Add(new EngineRecord { Type = "summary", Exit = 0 });
        var view = ResultPresenter.Final(outcome, earlierRun: true);
        Assert.Equal("Previous Windows Update repair completed successfully.", view.Status);
        Assert.Contains(ResultPresenter.RestartIfNotDoneLine, view.LogLines);
        Assert.DoesNotContain(ResultPresenter.RestartIfNotDoneLine, ResultPresenter.Final(outcome).LogLines);
    }

    [Fact]
    public void A_completed_run_without_a_result_gets_the_no_details_card_but_one_with_changes_too()
    {
        var outcome = new RunOutcome { Action = ToolbeltAction.ClearAppCache, Result = ResultCode.Completed, ExitCode = 1 };
        outcome.Records.Add(new EngineRecord { Type = "change", Text = "Removed a folder" });
        var titles = ResultPresenter.Final(outcome).Cards.Select(c => c.Title).ToList();
        Assert.Equal(new[] { "1 change made by this run", "No result details for this run" }, titles);
        Assert.DoesNotContain(ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.ClearAppCache, Result = ResultCode.Busy }).Cards,
            c => c.Title == "No result details for this run");
    }

    [Fact]
    public void An_unknown_severity_goes_last()
    {
        var cards = ResultPresenter.FindingCards(new[] { Finding("CRITICAL?", "odd"), Finding("OK", "fine"), Finding("ERROR", "broken") });
        Assert.Equal(new[] { "broken", "fine", "odd" }, cards.Select(c => c.Title).ToArray());
        Assert.Equal("INFO", cards[2].Word);
    }

    [Fact]
    public void The_live_view_counts_the_minutes_of_a_background_run_and_never_reopens_the_drawer()
    {
        var run = new RunOutcome { Action = ToolbeltAction.RepairWU, StartedUtc = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc) };
        var view = ResultPresenter.Live(run, new DateTime(2026, 10, 15, 12, 17, 30, DateTimeKind.Utc));
        Assert.Contains("(17 min elapsed)", view.Status);
        Assert.False(view.OpenDrawer);
        Assert.Equal("Retry work apps and settings is running...", ResultPresenter.Live(new RunOutcome { Action = ToolbeltAction.RetryAppsAndPolicies }, DateTime.UtcNow).Status);
    }

    [Fact]
    public void Requests_the_service_turned_down_stay_out_of_the_history()
    {
        Assert.False(ResultPresenter.BelongsInHistory(new RunOutcome { Result = ResultCode.Busy }));
        Assert.False(ResultPresenter.BelongsInHistory(new RunOutcome { Result = ResultCode.ServiceUnavailable }));
        Assert.True(ResultPresenter.BelongsInHistory(new RunOutcome { Result = ResultCode.Completed, ExitCode = 2 }));
        Assert.Equal("2", ResultPresenter.HistoryResult(new RunOutcome { Result = ResultCode.Completed, ExitCode = 2 }));
        Assert.Equal("not-started", ResultPresenter.HistoryResult(new RunOutcome { Result = ResultCode.EngineDidNotStart }));
        Assert.Equal("timeout", ResultPresenter.HistoryResult(new RunOutcome { Result = ResultCode.ServiceUnavailable, InProgress = true }));
    }

    [Fact]
    public void Record_text_cannot_pose_as_other_text()
    {
        var rlo = ((char)0x202E).ToString();
        var zwsp = ((char)0x200B).ToString();
        var bell = ((char)7).ToString();
        var record = EngineRecord.Parse("{\"type\":\"finding\",\"title\":\"app" + "\\u202E" + "gpj.exe\",\"detail\":\"a\\nb" + "\\u0007" + "\"}");
        Assert.Equal("appgpj.exe", record.Title);
        Assert.Equal("a\nb", record.Detail);
        Assert.Equal("ab", EngineRecord.Clean("a" + rlo + zwsp + bell + "b"));
        Assert.Equal("line\r\n\ttab", EngineRecord.Clean("line\r\n\ttab"));
    }

    [Fact]
    public void Several_tokens_and_bad_formats_are_handled()
    {
        var zone = TimeZoneInfo.Utc;
        Assert.Equal("10:00 and 11:00", TimeText.ToLocal("[[t:2026-10-15T10:00:00Z;HH:mm]] and [[t:2026-10-15T11:00:00Z;HH:mm]]", zone));
        // A format longer than the token allows is left as it is.
        var longFormat = "[[t:2026-10-15T10:00:00Z;" + new string('H', 41) + "]]";
        Assert.Equal(longFormat, TimeText.ToLocal(longFormat, zone));
    }

    [Fact]
    public void A_window_with_odd_numbers_or_on_another_screen_is_placed_sensibly()
    {
        var (left, top, width, height) = WindowPlacement.Clamp(double.NaN, double.PositiveInfinity, double.NaN, 500, 0, 0, 1920, 1040, 440, 420);
        Assert.Equal((0d, 0d, 440d, 500d), (left, top, width, height));
        // A secondary screen left of the primary one has negative coordinates.
        var secondary = WindowPlacement.Clamp(-1500, 100, 540, 700, -1920, 0, 1920, 1040, 440, 420);
        Assert.Equal((-1500d, 100d, 540d, 700d), secondary);
        // A work area smaller than the minimum size: the window fills it.
        var tiny = WindowPlacement.Clamp(0, 0, 540, 700, 0, 0, 400, 380, 440, 420);
        Assert.Equal((0d, 0d, 400d, 380d), tiny);
    }
}

public class AppHealthEdgeTests
{
    private sealed class Source : IHealthSource
    {
        public DateTime UtcNow { get; } = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        public string ImeServiceStatus() => "Running";
        public DsregInfo Dsreg() => new() { AzureAdJoined = "YES", AzureAdPrt = "YES" };
        public DateTime? MdmCertificateExpiryUtc() => UtcNow.AddHours(-1);
        public bool CanReach(string host, int port, int timeoutMs) => true;
        public double FreeSystemDriveGB() => 50;
        public bool ServicingRestartPending() => false;
    }

    [Fact]
    public void Checks_the_service_answers_have_their_own_hint_when_it_is_down()
    {
        var results = HealthChecks.Run(new Source(), null, out _);
        foreach (var name in new[] { HealthChecks.Enrollment, HealthChecks.LastSync })
        {
            var row = results.Single(r => r.Name == name);
            Assert.Equal(HealthChecks.ServiceUnavailableHint, row.Hint);
        }
    }

    [Fact]
    public void Last_sync_unknown_and_its_source()
    {
        var unknown = HealthChecks.Run(new Source(), new StatusInfo { Enrolled = true, LastSyncSource = "unreadable" }, out _).Single(r => r.Name == HealthChecks.LastSync);
        Assert.Equal("Unknown", unknown.Detail);
        Assert.Equal(HealthStatus.Warn, unknown.Status);
        var admin = HealthChecks.Run(new Source(), new StatusInfo { Enrolled = true, LastSyncUtc = "2026-10-15T11:00:00Z", LastSyncSource = "MDM admin log" }, out _).Single(r => r.Name == HealthChecks.LastSync);
        // The source is for IT support: in the device report, not on screen.
        Assert.EndsWith("(1 hr ago)", admin.Detail);
        Assert.EndsWith("(1 hr ago, MDM admin log)", admin.ReportDetail);
    }

    [Fact]
    public void A_certificate_that_expired_an_hour_ago_is_a_problem()
    {
        var row = HealthChecks.Run(new Source(), new StatusInfo { Enrolled = true }, out _).Single(r => r.Name == HealthChecks.MdmCertificate);
        Assert.Equal(HealthStatus.Fail, row.Status);
    }

    [Fact]
    public void The_free_space_names_the_drive()
    {
        var row = HealthChecks.Run(new Source(), new StatusInfo(), out _).Single(r => r.Name == HealthChecks.FreeSpace);
        Assert.Matches(@"^50 GB free on [A-Z]:$", row.Detail);
    }
}
