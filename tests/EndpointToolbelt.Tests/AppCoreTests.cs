using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Protocol;
using Xunit;

namespace EndpointToolbelt.Tests;

public class AppRecordTests
{
    [Fact]
    public void Records_are_parsed_and_unreadable_ones_are_ignored()
    {
        var finding = EngineRecord.Parse("{\"type\":\"finding\",\"run\":\"r1\",\"severity\":\"WARNING\",\"id\":\"X\",\"title\":\"T\",\"detail\":\"D\",\"next\":\"N\",\"unknown\":[1,2]}");
        Assert.Equal("finding", finding.Type);
        Assert.Equal("N", finding.NextStep);
        var summary = EngineRecord.Parse("{\"type\":\"summary\",\"run\":\"r1\",\"exit\":2,\"findings\":3,\"changes\":0,\"restart\":true}");
        Assert.Equal(2, summary.Exit);
        Assert.True(summary.Restart);
        Assert.Null(EngineRecord.Parse("not json"));
        Assert.Null(EngineRecord.Parse("{\"type\":\"summary\",\"exit\":\"two\"}"));
        Assert.Null(EngineRecord.Parse(""));
    }

    [Fact]
    public void Time_tokens_are_shown_in_the_users_clock()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        Assert.Equal("Retry at 14:03.", TimeText.ToLocal("Retry at [[t:2026-10-15T12:03:00Z;HH:mm]].", zone));
        Assert.Equal("On 2026-12-01 13:00", TimeText.ToLocal("On [[t:2026-12-01T12:00:00Z;yyyy-MM-dd HH:mm]]", zone));
        Assert.Equal("no token", TimeText.ToLocal("no token", zone));
        Assert.Equal("[[t:bad;HH:mm]]", TimeText.ToLocal("[[t:bad;HH:mm]]", zone));
        Assert.Equal("5 min ago", TimeText.Age(TimeSpan.FromMinutes(5)));
        Assert.Equal("30 hr ago", TimeText.Age(TimeSpan.FromHours(30)));
        // Rounded to the nearest whole unit.
        Assert.Equal("3 days ago", TimeText.Age(TimeSpan.FromDays(3.4)));
        Assert.Equal("4 days ago", TimeText.Age(TimeSpan.FromDays(3.6)));
        Assert.Equal("2 hr ago", TimeText.Age(TimeSpan.FromMinutes(100)));
    }
}

public class AppResultTests
{
    private static EngineRecord Finding(string severity, string title, string next = "") =>
        new() { Type = "finding", Severity = severity, Id = title, Title = title, Detail = "d", NextStep = next };

    private static RunOutcome Outcome(ToolbeltAction action, int exit, params EngineRecord[] records)
    {
        var outcome = new RunOutcome { Action = action, Result = ResultCode.Completed, ExitCode = exit };
        outcome.Records.AddRange(records);
        return outcome;
    }

    [Fact]
    public void Success_is_quiet_and_lists_what_changed()
    {
        var view = ResultPresenter.Final(Outcome(ToolbeltAction.RetryAppsAndPolicies, 0,
            new EngineRecord { Type = "change", Text = "Restarted the IME" },
            new EngineRecord { Type = "summary", Exit = 0, Restart = false }));
        Assert.Equal(Tone.Success, view.Tone);
        Assert.Equal("Retry work apps and settings completed successfully.", view.Status);
        Assert.False(view.OpenDrawer);
        Assert.False(view.CopyLog);
        var change = Assert.Single(view.Cards);
        Assert.Equal(CardKind.Change, change.Kind);
        Assert.Contains("- Restarted the IME", change.Detail);
    }

    [Fact]
    public void Problems_come_first_and_the_top_one_is_on_the_status_line()
    {
        var view = ResultPresenter.Final(Outcome(ToolbeltAction.FullReset, 2,
            Finding("OK", "fine"), Finding("INFO", "note"), Finding("WARNING", "watch this"), Finding("ERROR", "broken"),
            new EngineRecord { Type = "summary", Exit = 2 }));
        Assert.Equal(new[] { "broken", "watch this", "note", "fine", "Nothing on this device was changed." }, view.Cards.Select(c => c.Title).ToArray());
        // A problem that remains is said as such, in the problem colour.
        Assert.Equal("Full IME reset finished, but a problem remains: broken", view.Status);
        Assert.Equal(Tone.Error, view.Tone);
        Assert.Equal("PROBLEM", view.Cards[0].Word);
        Assert.Equal("WARNING", view.Cards[1].Word);
        Assert.True(view.OpenDrawer);
        // Exit 2 also copies the log to Downloads.
        Assert.True(view.CopyLog);
    }

    [Fact]
    public void A_failure_names_its_finding_or_its_code_and_saves_the_log()
    {
        var withFinding = ResultPresenter.Final(Outcome(ToolbeltAction.ClearAppCache, 1, Finding("ERROR", "could not remove")));
        Assert.Equal("Clear downloaded app content did not succeed: could not remove", withFinding.Status);
        Assert.True(withFinding.CopyLog);
        var bare = ResultPresenter.Final(Outcome(ToolbeltAction.ClearAppCache, unchecked((int)0x8007042B)));
        Assert.Contains("0x8007042B", bare.Status);
        Assert.Equal(Tone.Error, bare.Tone);
        Assert.Equal("No result details for this run", Assert.Single(bare.Cards).Title);
    }

    [Theory]
    [InlineData(69001, "skipped: cooldown active")]
    [InlineData(69002, "a restart is pending")]
    [InlineData(69003, "nothing to repair")]
    public void Gate_exit_codes_have_their_own_sentence_and_no_log_copy(int exit, string text)
    {
        var view = ResultPresenter.Final(Outcome(ToolbeltAction.RepairWU, exit));
        Assert.Contains(text, view.Status);
        // The cooldown's status points to the details ("when it can run again"), so they open.
        Assert.Equal(exit == EngineExitCode.CooldownActive, view.OpenDrawer);
        Assert.False(view.CopyLog);
    }

    [Fact]
    public void A_needed_restart_and_a_queued_sync_are_said_plainly()
    {
        var restart = ResultPresenter.Final(Outcome(ToolbeltAction.RepairWU, 0, new EngineRecord { Type = "restart-required" }, new EngineRecord { Type = "summary", Exit = 0, Restart = true }));
        Assert.True(restart.RestartNeeded);
        Assert.Equal("Repair Windows Update completed. Restart the device to finish it.", restart.Status);

        var queued = ResultPresenter.Final(Outcome(ToolbeltAction.RetryAppsAndPolicies, 2,
            Finding("INFO", "queued"), new EngineRecord { Type = "mdm-queued", At = "2026-10-15T12:03:00Z" }));
        // Neither success nor a problem: the info tone, never a warning beside "no action needed".
        Assert.Equal(Tone.Info, queued.Tone);
        Assert.Equal("INFO", Words.ResultWord(queued.Tone));
        // The queued time has passed in this test: "any moment now", never a time in the past.
        Assert.Equal("Retry work apps and settings ran. Windows sends the sync to Intune again any moment now; no action needed.", queued.Status);
        var at = new DateTime(2026, 10, 15, 12, 3, 0, DateTimeKind.Utc);
        Assert.Equal("Full IME reset ran. Windows sends the sync to Intune again at about " + TimeText.InLocalTime(at, "HH:mm") + "; no action needed.",
            ResultPresenter.QueuedSyncStatus("Full IME reset", at, at.AddMinutes(-20), null));
        Assert.EndsWith("again any moment now; no action needed.", ResultPresenter.QueuedSyncStatus("Full IME reset", at, at.AddSeconds(-30), null));
        Assert.EndsWith("again by itself; no action needed.", ResultPresenter.QueuedSyncStatus("Full IME reset", null, at, null));
    }

    [Theory]
    [InlineData(ResultCode.Busy, Tone.Warning, false)]
    [InlineData(ResultCode.RateLimited, Tone.Warning, false)]
    [InlineData(ResultCode.EngineTimeout, Tone.Warning, true)]
    [InlineData(ResultCode.EngineCrashed, Tone.Error, true)]
    [InlineData(ResultCode.ServiceUnavailable, Tone.Error, false)]
    [InlineData(ResultCode.EngineNotSigned, Tone.Error, false)]
    public void Service_results_are_explained(ResultCode code, Tone tone, bool copyLog)
    {
        var view = ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.FullReset, Result = code, Message = "service detail" });
        Assert.Equal(tone, view.Tone);
        Assert.Equal(copyLog, view.CopyLog);
        Assert.True(view.OpenDrawer);
        Assert.Equal("service detail", view.Cards[0].Detail);
        Assert.False(string.IsNullOrWhiteSpace(view.Status));
    }

    [Fact]
    public void Live_view_shows_findings_and_the_log_so_far()
    {
        var outcome = Outcome(ToolbeltAction.RepairWU, 0, Finding("WARNING", "behind"),
            new EngineRecord { Type = "progress", Severity = "info", Text = "step [[t:2026-10-15T12:00:00Z;HH:mm]]" },
            new EngineRecord { Type = "progress", Severity = "warning", Text = "careful" });
        var live = ResultPresenter.Live(outcome, new DateTime(2026, 10, 15, 12, 30, 0, DateTimeKind.Utc));
        Assert.Single(live.Cards);
        Assert.Equal(Tone.Neutral, live.Tone);
        Assert.Equal("WARNING: careful", live.LogLines.Last());
        Assert.Equal("FINDING WARNING behind: behind", live.LogLines[0]);
        Assert.All(live.LogLines, l => Assert.DoesNotContain("[[t:", l));
    }
}

public class AppHealthTests
{
    private sealed class FakeSource : IHealthSource, IProxyRoutes
    {
        public DateTime UtcNow { get; set; } = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        public string Ime { get; set; } = "Running";
        public DsregInfo Info { get; set; } = new() { AzureAdJoined = "YES", AzureAdPrt = "YES", TenantName = "Contoso" };
        public DateTime? Cert { get; set; } = new DateTime(2027, 10, 15, 0, 0, 0, DateTimeKind.Utc);
        public bool Reach { get; set; } = true;
        public double FreeGB { get; set; } = 100;
        public bool Servicing { get; set; }
        public bool ServicingUnreadable { get; set; }

        public string ImeServiceStatus() => Ime;
        public DsregInfo Dsreg() => Info;
        public DateTime? MdmCertificateExpiryUtc() => Cert;
        public bool CanReach(string host, int port, int timeoutMs) => Reach;
        public double FreeSystemDriveGB() => FreeGB;
        public bool ServicingRestartPending() => ServicingUnreadable ? throw new UnauthorizedAccessException("servicing keys") : Servicing;

        // The HTTPS step of the connection check answers as the TCP step does (no proxy).
        public Uri BrowserProxy(Uri target) => null;
        public (Uri Proxy, string Bypass)? MachineProxy() => null;
        public HttpsObservation Https(HttpsRequest request) => Reach ? new HttpsObservation { TlsSeen = true, MicrosoftChain = true, Status = 200 } : HttpsObservation.Failed(RouteOutcome.Refused);
    }

    private static StatusInfo Status(Action<StatusInfo> change = null)
    {
        var s = new StatusInfo { Enrolled = true, LastSyncUtc = "2026-10-15T10:00:00Z", LastImeCheckInUtc = "2026-10-15T11:00:00Z", Readouts = HealthyReadouts() };
        change?.Invoke(s);
        return s;
    }

    /// <summary>The device settings of a healthy device.</summary>
    internal static DeviceReadouts HealthyReadouts() => new()
    {
        Compliance = new ComplianceReadout { BitLocker = "on", RecoveryPassword = true, SecureBoot = "on", Tpm = "ready", TpmVersion = "2.0", FirewallDomain = "on", FirewallPrivate = "on", FirewallPublic = "on" },
        Defender = new DefenderReadout { Mode = "normal", RealTimeProtection = true, SignatureUpdatedUtc = "2026-10-15T06:00:00Z" },
        Clock = new ClockReadout { OffsetSeconds = 0.2, Source = "ntp" },
        Profiles = new ProfileErrorsReadout(),
        IntuneConnection = new IntuneConnectionReadout { Tasks = 4, TasksEnabled = 4 },
    };

    private static HealthResult Check(FakeSource source, StatusInfo status, string name) =>
        HealthChecks.Run(source, status, out _).Single(r => r.Name == name);

    [Fact]
    public void A_healthy_device_passes_all_fifteen_checks()
    {
        var results = HealthChecks.Run(new FakeSource(), Status(), out var dsreg);
        Assert.Equal(15, results.Count);
        Assert.All(results, r => Assert.Equal(HealthStatus.Pass, r.Status));
        Assert.Equal("Contoso", dsreg.TenantName);
        Assert.Equal(("Device looks healthy.", Tone.Success), HealthChecks.Summary(results));
    }

    [Theory]
    [InlineData(23, HealthStatus.Pass)]
    [InlineData(48, HealthStatus.Warn)]
    [InlineData(24 * 8, HealthStatus.Fail)]
    public void Last_sync_age_thresholds(int hoursAgo, HealthStatus expected)
    {
        var source = new FakeSource();
        var status = Status(s => s.LastSyncUtc = source.UtcNow.AddHours(-hoursAgo).ToString("o"));
        Assert.Equal(expected, Check(source, status, HealthChecks.LastSync).Status);
    }

    [Theory]
    [InlineData(23, HealthStatus.Pass)]
    [InlineData(48, HealthStatus.Warn)]
    [InlineData(24 * 8, HealthStatus.Fail)]
    public void Ime_check_in_age_thresholds(int hoursAgo, HealthStatus expected)
    {
        var source = new FakeSource();
        var row = Check(source, Status(s => s.LastImeCheckInUtc = source.UtcNow.AddHours(-hoursAgo).ToString("o")), HealthChecks.ImeCheckIn);
        Assert.Equal(expected, row.Status);
        Assert.EndsWith("(IME log)", row.ReportDetail);
        // The source is said once in the report line: in the detail, not also in the name.
        var line = HealthChecks.ReportName(row.Name) + " " + row.ReportDetail;
        Assert.Equal(line.IndexOf("(IME log)", StringComparison.Ordinal), line.LastIndexOf("(IME log)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_ime_check_in_after_the_last_success_needs_attention_and_unknown_is_never_ok()
    {
        var source = new FakeSource();
        var failed = Check(source, Status(s => { s.ImeCheckInErrorUtc = source.UtcNow.AddMinutes(-5).ToString("o"); s.ImeCheckInError = "401"; }), HealthChecks.ImeCheckIn);
        Assert.Equal(HealthStatus.Warn, failed.Status);
        Assert.Contains("Last attempt failed (sign-in refused, 401)", failed.Detail);
        Assert.False(string.IsNullOrEmpty(failed.HintBelow));
        // Each state that is not OK has its own hint; none promises a check-in at once.
        Assert.Equal(HealthChecks.ImeCheckInFailedHint, failed.Hint);
        var old = Check(source, Status(s => s.LastImeCheckInUtc = source.UtcNow.AddDays(-2).ToString("o")), HealthChecks.ImeCheckIn);
        Assert.Equal(HealthStatus.Warn, old.Status);
        Assert.Equal(HealthChecks.ImeCheckInTooOldHint, old.Hint);
        foreach (var hint in new[] { HealthChecks.ImeCheckInFailedHint, HealthChecks.ImeCheckInTooOldHint, HealthChecks.ImeCheckInUnknownHint })
        {
            Assert.DoesNotContain(" now", hint);
            Assert.DoesNotContain("recently", hint);
        }

        var longAgo = Check(source, Status(s => { s.LastImeCheckInUtc = source.UtcNow.AddDays(-9).ToString("o"); s.ImeCheckInErrorUtc = source.UtcNow.AddHours(-1).ToString("o"); s.ImeCheckInError = "proxy"; }), HealthChecks.ImeCheckIn);
        Assert.Equal(HealthStatus.Fail, longAgo.Status);
        Assert.Contains("proxy error", longAgo.Detail);

        foreach (var (why, text) in new[] { ("missing", "no IME log"), ("unreadable", "could not be read"), (null, "no check-in") })
        {
            var unknown = Check(source, Status(s => { s.LastImeCheckInUtc = null; s.ImeCheckInUnavailable = why; }), HealthChecks.ImeCheckIn);
            Assert.Equal(HealthStatus.Warn, unknown.Status);
            Assert.Contains(text, unknown.Detail);
            Assert.Equal(HealthChecks.ImeCheckInUnknownHint, unknown.Hint);
        }
        Assert.Contains("not available", HealthChecks.Run(source, null, out _).Single(r => r.Name == HealthChecks.ImeCheckIn).Detail);
        Assert.Equal("error ConnectFailure", HealthChecks.ImeCheckInErrorText("ConnectFailure"));
        Assert.Equal("no error code in the log", HealthChecks.ImeCheckInErrorText(""));
    }

    private sealed class BootSource : IHealthSource, IBootTime
    {
        private readonly FakeSource _inner = new();
        public DateTime? Boot { get; set; }
        public DateTime UtcNow => _inner.UtcNow;
        public string ImeServiceStatus() => _inner.ImeServiceStatus();
        public DsregInfo Dsreg() => _inner.Dsreg();
        public DateTime? MdmCertificateExpiryUtc() => _inner.MdmCertificateExpiryUtc();
        public bool CanReach(string host, int port, int timeoutMs) => true;
        public double FreeSystemDriveGB() => 100;
        public bool ServicingRestartPending() => false;
        public DateTime? BootUtc() => Boot;
    }

    [Fact]
    public void While_the_IME_starts_after_a_restart_its_check_in_is_OK_and_says_when_it_checks_in()
    {
        // A real case: last check-in 48 minutes ago, a start-up failure without a code at the
        // restart, the IME started 3 minutes ago and announced its first app check-in for 238 s later.
        var source = new FakeSource();
        var now = source.UtcNow;
        var start = now.AddMinutes(-3);
        StatusInfo Starting(Action<StatusInfo> more = null) => Status(s =>
        {
            s.LastImeCheckInUtc = now.AddMinutes(-48).ToString("o");
            s.ImeCheckInErrorUtc = now.AddMinutes(-2).ToString("o");
            s.ImeCheckInError = "";
            s.ImeStartUtc = start.ToString("o");
            s.ImeFirstCheckInDueUtc = start.AddSeconds(238).ToString("o");
            more?.Invoke(s);
        });
        var row = Check(source, Starting(), HealthChecks.ImeCheckIn);
        Assert.Equal(HealthStatus.Pass, row.Status);
        Assert.True(row.Starting);
        Assert.Equal("The app installer (IME) started again; first check-in in about 1 min", row.Detail);
        Assert.Contains("last check-in", row.ReportDetail);
        // The combined row on Home says the same, OK.
        var appInstaller = HealthView.Rows(HealthChecks.Run(source, Starting(), out _)).Single(r => r.Name == HealthView.AppInstaller);
        Assert.Equal(HealthStatus.Pass, appInstaller.Status);
        Assert.Equal("The app installer (IME) started again; first check-in in about 1 min", appInstaller.Detail);

        // Right after the start: "about 4 min".
        source.UtcNow = start.AddSeconds(5);
        Assert.Equal("The app installer (IME) started again; first check-in in about 4 min", Check(source, Starting(s => s.ImeCheckInErrorUtc = null), HealthChecks.ImeCheckIn).Detail);
        // Its check-in is due: still starting, for a few minutes.
        source.UtcNow = start.AddSeconds(238 + 60);
        Assert.Equal("The app installer (IME) started again; first check-in due now", Check(source, Starting(s => s.ImeCheckInErrorUtc = null), HealthChecks.ImeCheckIn).Detail);
        // A failure after the announced check-in is a failed attempt as before: CHECK.
        var failed = Check(source, Starting(s => { s.ImeCheckInErrorUtc = start.AddSeconds(250).ToString("o"); s.ImeCheckInError = "401"; }), HealthChecks.ImeCheckIn);
        Assert.Equal(HealthStatus.Warn, failed.Status);
        Assert.Contains("Last attempt failed (sign-in refused, 401)", failed.Detail);
        // Long after the start without a check-in: the age counts again (48 min is fine, the failure is not).
        source.UtcNow = start.AddMinutes(20);
        Assert.Equal(HealthStatus.Warn, Check(source, Starting(), HealthChecks.ImeCheckIn).Status);
        // A check-in after the start ends the start.
        source.UtcNow = start.AddMinutes(6);
        var done = Check(source, Starting(s => { s.LastImeCheckInUtc = start.AddMinutes(5).ToString("o"); s.ImeCheckInErrorUtc = null; }), HealthChecks.ImeCheckIn);
        Assert.Equal(HealthStatus.Pass, done.Status);
        Assert.False(done.Starting);
    }

    [Fact]
    public void A_device_that_just_started_with_nothing_in_the_IME_log_yet_is_starting_too()
    {
        var source = new BootSource();
        var now = source.UtcNow;
        StatusInfo Old() => Status(s =>
        {
            s.LastImeCheckInUtc = now.AddHours(-30).ToString("o");
            s.ImeCheckInErrorUtc = now.AddHours(-26).ToString("o");
            s.ImeCheckInError = "";
        });
        source.Boot = now.AddMinutes(-2);
        var row = HealthChecks.Run(source, Old(), out _).Single(r => r.Name == HealthChecks.ImeCheckIn);
        Assert.Equal(HealthStatus.Pass, row.Status);
        Assert.Equal("Starting after the device restarted; first check-in in a few minutes", row.Detail);
        // Once the window has passed, the old evidence counts again.
        source.Boot = now.AddMinutes(-20);
        Assert.Equal(HealthStatus.Warn, HealthChecks.Run(source, Old(), out _).Single(r => r.Name == HealthChecks.ImeCheckIn).Status);
        // An older service (no start fields) and a source without a boot time: as before.
        Assert.Equal(HealthStatus.Warn, Check(new FakeSource(), Old(), HealthChecks.ImeCheckIn).Status);
    }

    [Theory]
    [InlineData(-1, HealthStatus.Fail)]
    [InlineData(10, HealthStatus.Fail)]
    [InlineData(29, HealthStatus.Fail)]
    [InlineData(30, HealthStatus.Warn)]
    [InlineData(59, HealthStatus.Warn)]
    [InlineData(60, HealthStatus.Pass)]
    public void Certificate_thresholds(int daysLeft, HealthStatus expected)
    {
        var source = new FakeSource();
        source.Cert = source.UtcNow.AddDays(daysLeft).AddHours(1);
        Assert.Equal(expected, Check(source, Status(), HealthChecks.MdmCertificate).Status);
    }

    [Theory]
    [InlineData(4.9, HealthStatus.Fail)]
    [InlineData(14.9, HealthStatus.Warn)]
    [InlineData(15, HealthStatus.Pass)]
    public void Free_space_thresholds(double free, HealthStatus expected)
    {
        Assert.Equal(expected, Check(new FakeSource { FreeGB = free }, Status(), HealthChecks.FreeSpace).Status);
    }

    [Fact]
    public void Service_facts_say_when_the_service_is_not_available()
    {
        var results = HealthChecks.Run(new FakeSource(), null, out _);
        Assert.Equal(HealthStatus.Warn, results.Single(r => r.Name == HealthChecks.Enrollment).Status);
        Assert.Contains("not available", results.Single(r => r.Name == HealthChecks.LastSync).Detail);
        Assert.Equal(HealthStatus.Fail, Check(new FakeSource(), Status(s => s.Enrolled = false), HealthChecks.Enrollment).Status);
        Assert.Equal(HealthStatus.Warn, Check(new FakeSource(), Status(s => s.Enrolled = null), HealthChecks.Enrollment).Status);
        Assert.Equal(HealthStatus.Warn, Check(new FakeSource(), Status(s => s.LastSyncUtc = null), HealthChecks.LastSync).Status);
    }

    [Fact]
    public void Other_checks_and_their_hints()
    {
        Assert.Equal("Not installed", Check(new FakeSource { Ime = null }, Status(), HealthChecks.ImeService).Detail);
        Assert.Equal(HealthStatus.Fail, Check(new FakeSource { Ime = "Stopped" }, Status(), HealthChecks.ImeService).Status);
        Assert.Equal(HealthStatus.Warn, Check(new FakeSource { Info = new DsregInfo { AzureAdJoined = "YES", AzureAdPrt = "NO" } }, Status(), HealthChecks.Prt).Status);
        // Not joined is a calm Check, not a problem: a personal or unmanaged device can be fine.
        Assert.Equal(HealthStatus.Warn, Check(new FakeSource { Info = new DsregInfo() }, Status(), HealthChecks.EntraJoined).Status);
        Assert.Equal(HealthStatus.Fail, Check(new FakeSource { Reach = false }, Status(), HealthChecks.Connectivity).Status);
        var servicing = Check(new FakeSource { Servicing = true }, Status(), HealthChecks.PendingRestart);
        Assert.Contains("Windows servicing", servicing.Detail);
        Assert.Contains("Restart when your organization allows it", servicing.Hint);
        var marker = Check(new FakeSource(), Status(s => { s.RestartRequiredSinceUtc = "2026-10-15T09:00:00Z"; s.RestartRequiredBy = "Repair Windows Update"; }), HealthChecks.PendingRestart);
        Assert.Contains("after Repair Windows Update", marker.Detail);
        var summary = HealthChecks.Summary(HealthChecks.Run(new FakeSource { Reach = false, Servicing = true }, Status(), out _));
        Assert.Equal(Tone.Error, summary.Tone);
        Assert.StartsWith("1 problem found.", summary.Text);
    }

    [Fact]
    public void An_unreadable_servicing_state_is_no_pass_and_no_waiting_restart()
    {
        var unknown = Check(new FakeSource { ServicingUnreadable = true }, Status(), HealthChecks.PendingRestart);
        Assert.Equal(HealthStatus.Warn, unknown.Status);
        Assert.StartsWith("Restart status unknown", unknown.Detail);
        Assert.True(unknown.NotRead);
        // Unknown claims no waiting restart: not in the step, the card for IT, Home or the compliance path.
        Assert.StartsWith("Use Check this PC again in a moment", HealthChecks.NextStep(unknown));
        Assert.DoesNotContain("restart is waiting", HealthChecks.Card(unknown).Support);
        Assert.DoesNotContain("A restart is waiting", unknown.Hint);
        var home = HomeSummary.Build(HealthView.Rows(new[] { unknown }), null, false);
        Assert.Equal("Restart status unknown", HomeSummary.PlainTitle(home));
        Assert.DoesNotContain("Restart when", HomeSummary.PlainText(home));
        var compliance = CompliancePath.Rows(HealthChecks.Run(new FakeSource { ServicingUnreadable = true }, Status(), out _), Status());
        var restart = compliance.Single(r => r.Name == CompliancePath.WindowsRestart);
        Assert.Equal(HealthStatus.Warn, restart.Status);
        Assert.DoesNotContain("Restart when", CompliancePath.NextStep(restart));
    }

    [Fact]
    public void The_restart_marker_wins_over_an_unreadable_servicing_state()
    {
        var marker = Check(new FakeSource { ServicingUnreadable = true }, Status(s => { s.RestartRequiredSinceUtc = "2026-10-15T09:00:00Z"; s.RestartRequiredBy = "Repair Windows Update"; }), HealthChecks.PendingRestart);
        Assert.Equal(HealthStatus.Warn, marker.Status);
        Assert.Contains("after Repair Windows Update", marker.Detail);
        Assert.False(marker.NotRead);
        Assert.Contains("Restart when your organization allows it", marker.Hint);
    }

    [Fact]
    public void Dsregcmd_output_is_parsed_by_name()
    {
        var info = DsregInfo.Parse(new[]
        {
            "+-----------------+", "| Device State |", "+-----------------+",
            "             AzureAdJoined : YES",
            "                  TenantName : Contoso",
            "                    TenantId : 11111111-2222-3333-4444-555555555555",
            "                    DeviceId : 66666666-7777-8888-9999-aaaaaaaaaaaa",
            "                  AzureAdPrt : NO",
        });
        Assert.Equal("YES", info.AzureAdJoined);
        Assert.Equal("NO", info.AzureAdPrt);
        Assert.Equal("Contoso", info.TenantName);
        Assert.Equal("66666666-7777-8888-9999-aaaaaaaaaaaa", info.DeviceId);
    }
}

public class AppFileTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void The_report_holds_facts_checks_hints_and_history()
    {
        var health = new List<HealthResult>
        {
            new() { Name = "IME service", Status = HealthStatus.Pass, Detail = "Running", Hint = "fine" },
            new() { Name = "Free disk space", Status = HealthStatus.Warn, Detail = "9 GB", Hint = "Empty the Recycle Bin." },
        };
        var text = DeviceReport.Build(new DeviceFacts { ComputerName = "PC1", Serial = "S1", Model = "M1", OperatingSystem = "Windows 11", User = "user@contoso.com", ImeVersion = "1.2" },
            new DsregInfo { TenantName = "Contoso", TenantId = "T1", DeviceId = "D1" }, health, new[] { "line1" },
            new DateTimeOffset(2026, 10, 15, 14, 0, 0, TimeSpan.FromHours(2)), "1.0.0");
        Assert.Contains("Generated : 2026-10-15 14:00:00 +02:00 (Endpoint Toolbelt 1.0.0)", text);
        Assert.Contains("Serial: S1", text);
        // A summary above the checks, and no advice lines (that is the window's job).
        Assert.Contains("To check  : Free disk space", text);
        Assert.DoesNotContain("->", text);
        Assert.DoesNotContain("Empty the Recycle Bin.", text);
        Assert.Contains("line1", text);
        Assert.Contains("(no actions recorded yet)", DeviceReport.Build(new DeviceFacts(), null, health, Array.Empty<string>(), DateTimeOffset.Now, "1.0.0"));
    }

    [Fact]
    public void The_device_report_names_the_device_clock_when_its_zone_is_not_the_users()
    {
        var europe = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var generated = new DateTimeOffset(2026, 11, 16, 10, 16, 0, TimeSpan.FromHours(1));
        // A session host with time zone redirection: the device in UTC, the user in UTC+01:00.
        var text = DeviceReport.Build(new DeviceFacts { DeviceZone = TimeZoneInfo.Utc }, null, new List<HealthResult>(), Array.Empty<string>(), generated, "1.0.0", null, europe);
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        Assert.StartsWith("Time zone : local time, UTC+01:00", lines[2]);
        Assert.Equal("Device clock: UTC (the IME logs use it; the CSV files are in UTC); times in this report are in your time zone, UTC+01:00.", lines[3]);
        // A device in another named zone gets its offset and name.
        Assert.Equal("Device clock: UTC+01:00 (" + europe.StandardName + ") (the IME logs use it; the CSV files are in UTC); times in this report are in your time zone, UTC+00:00.",
            DeviceReport.DeviceClockLine(europe, TimeZoneInfo.Utc, generated.UtcDateTime));
        // The same zone, or an unknown device zone: no line.
        Assert.Null(DeviceReport.DeviceClockLine(europe, europe, generated.UtcDateTime));
        Assert.Null(DeviceReport.DeviceClockLine(null, europe, generated.UtcDateTime));
        Assert.DoesNotContain("Device clock", DeviceReport.Build(new DeviceFacts { DeviceZone = europe }, null, new List<HealthResult>(), Array.Empty<string>(), generated, "1.0.0", null, europe));
    }

    [Fact]
    public void History_lines_carry_the_offset_and_tail_reads_the_newest()
    {
        var line = History.Line(new DateTimeOffset(2026, 10, 15, 14, 0, 0, TimeSpan.FromHours(2)), "us;er", "FullReset", "0");
        Assert.Equal("2026-10-15 14:00:00 +02:00;us,er;FullReset;0", line);
        var path = _folder.Combine("history.log");
        for (var i = 0; i < 7; i++) { History.Add("A" + i, "0", path); }
        var tail = History.Tail(5, path);
        Assert.Equal(5, tail.Count);
        Assert.EndsWith(";A6;0", tail[4]);
        Assert.Empty(History.Tail(5, _folder.Combine("none.log")));
    }

    [Fact]
    public void The_package_is_copied_to_a_free_name_with_the_users_files()
    {
        var downloads = _folder.Combine("Downloads");
        var report = _folder.Combine("HealthReport.txt");
        File.WriteAllText(report, "report");
        var history = _folder.Combine("history.log");
        File.WriteAllText(history, "history");
        var now = new DateTime(2026, 10, 15, 9, 5, 0);

        ReceivedFile Package()
        {
            var path = _folder.Combine(Guid.NewGuid().ToString("N") + ".zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(zip.CreateEntry("IMELogs/a.log").Open());
                writer.Write("log");
            }
            return new ReceivedFile { Name = "EndpointToolbelt_SupportPackage.zip", Path = path };
        }

        var first = Package();
        var (path1, notAdded) = SupportPackage.Save(first, downloads, now, report, history, new[] { "AzureAdPrt : YES" }, "DOMAIN\\user", "PC1");
        Assert.Equal(Path.Combine(downloads, "Toolbelt_20261015.zip"), path1);
        Assert.Empty(notAdded);
        Assert.False(File.Exists(first.Path));
        using (var zip = ZipFile.OpenRead(path1))
        {
            var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "dsregcmd_status_user.txt", "HealthReport.txt", "history.log", "IMELogs/a.log" }, names);
            using var reader = new StreamReader(zip.GetEntry("dsregcmd_status_user.txt").Open());
            Assert.Contains("in the context of DOMAIN\\user on PC1", reader.ReadToEnd());
        }
        var (path2, _) = SupportPackage.Save(Package(), downloads, now, report, _folder.Combine("missing.log"), null, "u", "PC1");
        Assert.Equal(Path.Combine(downloads, "Toolbelt_20261015-1.zip"), path2);
        var (path3, _) = SupportPackage.Save(Package(), downloads, now, report, history, null, "u", "PC1");
        Assert.Equal(Path.Combine(downloads, "Toolbelt_20261015-2.zip"), path3);

        var header = new RunLogHeader { Outcome = new RunOutcome { Action = ToolbeltAction.FullReset, Result = ResultCode.Completed, ExitCode = 1 }, Zone = TimeZoneInfo.Utc };
        var log = SupportPackage.SaveRunLog(header, new[] { "step 1" }, new[] { ResultCard.From(FindingSeverity.Error, "broken", "detail", "next", "for IT") }, downloads);
        Assert.StartsWith(Path.Combine(downloads, "EndpointToolbelt_FullReset_"), log);
        Assert.EndsWith("Z.log", log);
        var text = File.ReadAllText(log);
        Assert.Contains("[PROBLEM] broken", text);
        Assert.Contains("  For IT support: for IT", text);
        Assert.Contains("step 1", text);
    }

    [Fact]
    public void A_received_file_must_match_its_length_and_hash()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        string Hash(byte[] bytes) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); }
        var incoming = _folder.Combine("Incoming");

        using (var good = new ServiceGateway.FileReceiver(incoming, new Response { FileName = "pkg.zip", FileLength = 5, FileSha256 = Hash(data) }))
        {
            good.Add(Convert.ToBase64String(data, 0, 3));
            good.Add(Convert.ToBase64String(data, 3, 2));
            var file = good.Finish();
            Assert.Equal("pkg.zip", file.Name);
            Assert.Equal(data, File.ReadAllBytes(file.Path));
        }

        var bad = new ServiceGateway.FileReceiver(incoming, new Response { FileName = "pkg.zip", FileLength = 5, FileSha256 = Hash(new byte[] { 9 }) });
        bad.Add(Convert.ToBase64String(data));
        Assert.Throws<IOException>(() => bad.Finish());
        bad.Dispose();
        Assert.Single(Directory.GetFiles(incoming));

        var over = new ServiceGateway.FileReceiver(incoming, new Response { FileName = "pkg.zip", FileLength = 2, FileSha256 = Hash(data) });
        Assert.Throws<IOException>(() => over.Add(Convert.ToBase64String(data)));
        over.Dispose();
        Assert.Throws<IOException>(() => new ServiceGateway.FileReceiver(incoming, new Response { FileName = "..\\evil.zip", FileLength = 1, FileSha256 = "" }));
        Assert.Throws<IOException>(() => new ServiceGateway.FileReceiver(incoming, new Response { FileName = "pkg.exe", FileLength = 1, FileSha256 = "" }));
    }

    [Fact]
    public void Settings_survive_a_round_trip_and_a_broken_file()
    {
        var path = _folder.Combine("settings.json");
        new AppSettings { Left = 10, Top = 20, Width = 540, Height = 700, LastShownRunId = "abc" }.Save(path);
        var loaded = AppSettings.Load(path);
        Assert.Equal(540, loaded.Width);
        Assert.Equal("abc", loaded.LastShownRunId);
        File.WriteAllText(path, "{broken");
        Assert.Null(AppSettings.Load(path).Width);
    }

    [Fact]
    public void A_height_is_kept_only_when_the_user_chose_it()
    {
        // A settings file from 1.1.0 has a height but no choice, so the window fits its content.
        var path = _folder.Combine("settings.json");
        File.WriteAllText(path, "{\"Height\":720,\"Left\":10,\"Top\":20,\"Width\":540}");
        var old = AppSettings.Load(path);
        Assert.False(old.HeightChosen);
        Assert.Equal(720, old.Height);

        new AppSettings { Left = 10, Top = 20, Width = 540, Height = 610, HeightChosen = true }.Save(path);
        var chosen = AppSettings.Load(path);
        Assert.True(chosen.HeightChosen);
        Assert.Equal(610, chosen.Height);
    }
}

public class AppBehaviourTests
{
    [Fact]
    public void Windows_are_kept_inside_the_work_area()
    {
        // Too tall and partly below the taskbar on a 1366x768 screen (work area 1366x728).
        var (left, top, width, height) = WindowPlacement.Clamp(900, 300, 980, 900, 0, 0, 1366, 728, 440, 420);
        Assert.Equal(728, height);
        Assert.Equal(0, top);
        Assert.Equal(980, width);
        Assert.Equal(1366 - 980, left);
        var small = WindowPlacement.Clamp(-50, -20, 300, 300, 0, 0, 1024, 600, 440, 420);
        Assert.Equal((0d, 0d, 440d, 420d), small);
    }

    [Fact]
    public void Confirmations_use_the_catalog_figures_and_warn_about_other_sessions()
    {
        Assert.Contains("stopped after 2 hours at most", Confirmations.Text(ToolbeltAction.RepairWU));
        Assert.Equal("", Confirmations.OtherSessionsWarning(ToolbeltAction.FullReset, 0));
        Assert.Equal("", Confirmations.OtherSessionsWarning(ToolbeltAction.ClearAppCache, 3));
        Assert.StartsWith("3 other user sessions are signed in", Confirmations.OtherSessionsWarning(ToolbeltAction.FullReset, 3));
        Assert.StartsWith("1 other user session is signed in", Confirmations.OtherSessionsWarning(ToolbeltAction.FullReset, 1));
        Assert.Contains("restart", Confirmations.OtherSessionsWarning(ToolbeltAction.RepairWU, 2));
        Assert.Contains("could not be determined", Confirmations.OtherSessionsWarning(ToolbeltAction.FullReset, -1));
        foreach (var action in ActionCatalog.All) { Assert.False(string.IsNullOrEmpty(ActionText.Name(action))); }
    }
}
