using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;
using EndpointToolbelt.Service.State;
using Xunit;

namespace EndpointToolbelt.Tests;

public class ImeCheckInTests
{
    private static string PatternFile()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        Assert.True(dir != null, "Repository root not found.");
        return Path.Combine(dir.FullName, "src", "Engine", "ImeLogPatterns.json");
    }

    /// <summary>One CMTrace record as the IME writes it: the device's own clock, seven fraction digits.</summary>
    private static string Line(string message, DateTime utc, int type = 1)
    {
        var local = utc.ToLocalTime();
        return $"<![LOG[{message}]LOG]!><time=\"{local.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)}\" date=\"{local.ToString("M-d-yyyy", CultureInfo.InvariantCulture)}\" component=\"IntuneManagementExtension\" context=\"\" type=\"{type}\" thread=\"3\" file=\"\">\r\n";
    }

    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private static ImeCheckIn Evaluate(params string[] texts)
    {
        var table = ImeLogPatternTable.Load(PatternFile());
        Assert.NotNull(table);
        var rules = ImeCheckInEvidence.Matchers(table.CheckIn);
        return ImeCheckInReader.Evaluate(texts, rules, Now.AddDays(-30));
    }

    [Fact]
    public void The_shared_table_loads_and_has_check_in_rules_for_both_logs()
    {
        var table = ImeLogPatternTable.Load(PatternFile());
        Assert.Equal(1, table.Version);
        Assert.Contains("IntuneManagementExtension*.log", table.CheckInFiles);
        Assert.Contains(table.CheckIn, r => r.Kind == "success");
        Assert.Contains(table.CheckIn, r => r.Kind == "failure");
        Assert.All(table.CheckIn, r => _ = new Regex(r.Pattern));
    }

    [Fact]
    public void A_missing_or_damaged_table_is_null()
    {
        using var t = new TestFolder();
        Assert.Null(ImeLogPatternTable.Load(t.Combine("none.json")));
        File.WriteAllText(t.Combine("bad.json"), "{ not json");
        Assert.Null(ImeLogPatternTable.Load(t.Combine("bad.json")));
        File.WriteAllText(t.Combine("empty.json"), "{\"version\":1,\"checkInFiles\":[],\"checkIn\":[]}");
        Assert.Null(ImeLogPatternTable.Load(t.Combine("empty.json")));
    }

    [Fact]
    public void The_newest_success_counts_and_an_older_failure_is_not_reported()
    {
        var r = Evaluate(Line("Failed to get AAD token. len = 0 using client id x and resource id y, errorCode = 5", Now.AddHours(-5), 3) +
                         Line("Get policies = [{\"Id\":\"x\"}]", Now.AddHours(-2)));
        Assert.Equal(Now.AddHours(-2), r.LastSuccessUtc.Value, TimeSpan.FromSeconds(1));
        Assert.Null(r.LastErrorUtc);
        Assert.Equal("", r.LastError);
    }

    [Theory]
    [InlineData("Failed to get AAD token. len = 0 using client id x and resource id y, errorCode = 5", "token")]
    [InlineData("TokenAquireException: Attempt to get token, but failed", "token")]
    [InlineData("[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController with url https://contoso.example, thrown WebException status ProtocolError message The remote server returned an error: (401) Unauthorized.", "401")]
    [InlineData("[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController with url https://contoso.example, WebException status ConnectFailure message x\r\n   at stack", "ConnectFailure")]
    [InlineData("[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController, the proxy refused the connection", "proxy")]
    public void A_newer_failure_names_a_code_like_error_only(string message, string error)
    {
        var r = Evaluate(Line("Get policies = [{\"Id\":\"x\"}]", Now.AddHours(-3)) + Line(message, Now.AddHours(-1), 3));
        Assert.Equal(Now.AddHours(-1), r.LastErrorUtc.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(error, r.LastError);
    }

    [Fact]
    public void Old_records_and_unknown_lines_are_no_evidence()
    {
        var r = Evaluate(Line("Get policies = [{\"Id\":\"x\"}]", Now.AddDays(-40)) + Line("something else entirely", Now.AddHours(-1)));
        Assert.Null(r.LastSuccessUtc);
        Assert.Null(r.LastErrorUtc);
    }

    [Fact]
    public void The_reader_says_why_it_has_nothing_and_caches_its_answer()
    {
        using var t = new TestFolder();
        var table = ImeLogPatternTable.Load(PatternFile());
        var now = Now;
        Assert.Equal("missing", new ImeCheckInReader(() => t.Combine("absent"), () => table, () => now).Get().Unavailable);
        Assert.Equal("unreadable", new ImeCheckInReader(() => t.Path, () => null, () => now).Get().Unavailable);
        Assert.Equal("missing", new ImeCheckInReader(() => t.Path, () => table, () => now).Get().Unavailable);

        File.WriteAllText(t.Combine("IntuneManagementExtension.log"), Line("Get policies = [{\"Id\":\"x\"}]", DateTime.UtcNow.AddMinutes(-10)));
        var reads = 0;
        var reader = new ImeCheckInReader(() => t.Path, () => { reads++; return table; }, () => now);
        var first = reader.Get();
        Assert.Null(first.Unavailable);
        Assert.NotNull(first.LastSuccessUtc);
        reader.Get();
        Assert.Equal(1, reads);
        now = now.Add(ImeCheckInReader.CacheTime).AddSeconds(1);
        reader.Get();
        Assert.Equal(2, reads);
    }

    [Fact]
    public void A_bias_in_the_record_wins_over_the_device_clock()
    {
        Assert.Equal(new DateTime(2026, 10, 16, 6, 0, 0, DateTimeKind.Utc), ImeCheckInReader.ParseTime("10-16-2026", "08:00:00.000", "-120"));
        Assert.Null(ImeCheckInReader.ParseTime("10-16-2026", "99:00:00", ""));
    }

    // The IME's own start lines, as it writes them after a start or restart.
    private const string AllDelay = "Delaying all checkins by seconds = 51 in the first check in on start/restart.";
    private const string WorkloadDelay = "Delaying PS and Win32 app workload checkins by seconds = 238 in the first check in on start/restart.";
    private const string RateWindow = "[DciHelper] Persisted rate window failed sanity check (stored window starts in the future); failing closed with cap=2";
    private const string EndpointNoCode = "[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController, the remote name could not be resolved";
    private const string Endpoint401 = "[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController with url https://contoso.example, thrown WebException status ProtocolError message The remote server returned an error: (401) Unauthorized.";

    [Fact]
    public void A_start_with_its_delay_lines_is_a_start_and_nothing_written_while_it_starts_is_a_failed_check_in()
    {
        // The last check-in 48 minutes ago, then a restart three minutes ago: the agent starts, says it
        // delays its check-ins, and writes an unrelated line with "failed" and a start-up endpoint error.
        var start = Now.AddMinutes(-3);
        var r = Evaluate(Line("Get policies = [{\"Id\":\"x\"}]", Now.AddMinutes(-48)) +
                         Line("Intune Management Extension agent starts.", start) +
                         Line(AllDelay, start.AddSeconds(1)) +
                         Line(WorkloadDelay, start.AddSeconds(1)) +
                         Line(RateWindow, start.AddSeconds(2), 2) +
                         Line(EndpointNoCode, start.AddSeconds(3), 3));
        Assert.Null(r.LastErrorUtc);
        Assert.Equal("", r.LastError);
        Assert.Equal(start.AddSeconds(1), r.StartUtc.Value, TimeSpan.FromSeconds(1));
        // The latest check-in it announced: the apps' check-in, 238 seconds after the start line.
        Assert.Equal(start.AddSeconds(1 + 238), r.FirstCheckInDueUtc.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(Now.AddMinutes(-48), r.LastSuccessUtc.Value, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// The IME (seen on 1.108.101.0) after the location service answered: it asks a second address it
    /// wrote without a scheme and logs the UriFormatException as a failed endpoint. No request left the device.
    /// </summary>
    internal const string MalformedAddress = "[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController with url checkin.dm.contoso.example/RestUserAuthLocationService/RestUserAuthLocationService/Certificate/ServiceAddresses, thumbprint 0000000000000000000000000000000000000000,True, ex System.UriFormatException: Invalid URI: The format of the URI could not be determined.\r\n   at System.Uri.CreateThis(String uri, Boolean dontEscape, UriKind uriKind)\r\n   at System.Uri..ctor(String uriString)";

    /// <summary>The same exception for an https address (a malformed proxy, for one): a request was meant to leave, so it is a failure.</summary>
    internal const string MalformedHttpsAddress = "[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController with url https://checkin.dm.contoso.example/RestUserAuthLocationService/RestUserAuthLocationService/Certificate/ServiceAddresses, thumbprint 0000000000000000000000000000000000000000,True, ex System.UriFormatException: Invalid URI: The hostname could not be parsed.\r\n   at System.Uri.CreateThis(String uri, Boolean dontEscape, UriKind uriKind)";

    private const string LocationSuccess = "[Location Service] Success!! LocationService ServiceAddresses Controller with https://manage.contoso.example/RestUserAuthLocationService/RestUserAuthLocationService/Certificate/ServiceAddresses with True, statusCode = OK";

    [Fact]
    public void The_imes_own_malformed_address_after_the_location_services_success_is_no_failure()
    {
        var t = Now.AddMinutes(-7);
        var r = Evaluate(Line("Get policies = [{\"Id\":\"x\"}]", t.AddMinutes(-1)) +
                         Line(LocationSuccess, t) +
                         Line("[Location Service] Calling LocationService ServiceAddresses Controller with checkin.dm.contoso.example/RestUserAuthLocationService/RestUserAuthLocationService/Certificate/ServiceAddresses with True", t.AddMilliseconds(2)) +
                         Line(MalformedAddress, t.AddMilliseconds(4), 2));
        Assert.Null(r.LastErrorUtc);
        Assert.Equal("", r.LastError);
        Assert.Equal(t.AddMinutes(-1), r.LastSuccessUtc.Value, TimeSpan.FromSeconds(1));

        // Also with no Success!! before it at all (a second IME process writes it on its own thread).
        var alone = Evaluate(Line("Get policies = [{\"Id\":\"x\"}]", t.AddMinutes(-1)) + Line(MalformedAddress, t, 2));
        Assert.Null(alone.LastErrorUtc);

        // The exception for an https address is a failure without a code, as before.
        var https = Evaluate(Line("Get policies = [{\"Id\":\"x\"}]", t.AddMinutes(-1)) + Line(MalformedHttpsAddress, t, 2));
        Assert.Equal(t, https.LastErrorUtc.Value, TimeSpan.FromSeconds(1));
        Assert.Equal("", https.LastError);

        // A real error after that success still counts, also without a code.
        var real = Evaluate(Line("Get policies = [{\"Id\":\"x\"}]", t.AddMinutes(-1)) + Line(LocationSuccess, t) + Line(Endpoint401, t.AddSeconds(1), 3));
        Assert.Equal("401", real.LastError);
        var noCode = Evaluate(Line("Get policies = [{\"Id\":\"x\"}]", t.AddMinutes(-1)) + Line(LocationSuccess, t) + Line(EndpointNoCode, t.AddSeconds(1), 3));
        Assert.Equal(t.AddSeconds(1), noCode.LastErrorUtc.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_line_that_merely_contains_failed_is_no_failure()
    {
        var r = Evaluate(Line("Get policies = [{\"Id\":\"x\"}]", Now.AddHours(-2)) + Line(RateWindow, Now.AddHours(-1), 3));
        Assert.Null(r.LastErrorUtc);
        Assert.Null(r.StartUtc);
        Assert.NotNull(r.LastSuccessUtc);
    }

    [Theory]
    [InlineData(Endpoint401, "401")]
    [InlineData(EndpointNoCode, "")]
    public void A_failed_check_in_after_the_announced_check_in_still_counts_with_or_without_a_code(string message, string error)
    {
        var start = Now.AddMinutes(-30);
        var r = Evaluate(Line("Get policies = [{\"Id\":\"x\"}]", Now.AddHours(-2)) +
                         Line(AllDelay, start) + Line(WorkloadDelay, start) +
                         Line(message, Now.AddMinutes(-10), 3));
        Assert.Equal(Now.AddMinutes(-10), r.LastErrorUtc.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(error, r.LastError);
        Assert.Equal(start, r.StartUtc.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_failure_before_the_start_stays_a_failure_and_a_new_start_is_reported()
    {
        // The status shows it; the app decides that it is no problem while the IME starts again.
        var start = Now.AddMinutes(-2);
        var r = Evaluate(Line("Get policies = [{\"Id\":\"x\"}]", Now.AddHours(-3)) +
                         Line(Endpoint401, Now.AddHours(-1), 3) +
                         Line(AllDelay, start));
        Assert.Equal("401", r.LastError);
        Assert.Equal(start, r.StartUtc.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(start.AddSeconds(51), r.FirstCheckInDueUtc.Value, TimeSpan.FromSeconds(1));
    }
}
