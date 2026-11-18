using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.Ime;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// Verified MDM sync and MDM status parsing of the compiled engine. Events, the task
/// scheduler and deviceenroller are fakes: nothing here asks this machine to sync.
/// </summary>
public class EngineMdmTests : IDisposable
{
    private const string Guid1 = "11111111-2222-3333-4444-555555555555";
    private readonly EngineHarness _h = new();
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static MdmEvent Ev(int id, long rec, DateTime utc, params (string, string)[] fields) => FakeMdmEvents.New(id, rec, utc, fields);

    [Fact]
    public void Normalises_status_codes_and_HTTP_statuses()
    {
        Assert.Equal(2149122477, StatusCode.Parse("0x801901AD"));
        Assert.Equal(2149122477, StatusCode.Parse("-2145844819"));
        Assert.Equal(0, StatusCode.Parse("0"));
        Assert.Null(StatusCode.Parse("abc"));
        Assert.Null(StatusCode.Parse(""));
        Assert.Equal(429, StatusCode.HttpStatus(2149122477));
        Assert.Equal(0, StatusCode.HttpStatus(0));
    }

    [Fact]
    public void Classifies_sessions_from_their_events()
    {
        var t = DateTime.UtcNow;
        var start = Ev(208, 10, t, ("Message1", "{" + Guid1 + "}"), ("HexInt1", "0x3"));
        Assert.Equal("VerifiedSuccess", Mdm.SessionOutcome(new[] { start, Ev(209, 12, t, ("HRESULT", "0x00000000")) }, start).Status);
        Assert.Equal("Throttled", Mdm.SessionOutcome(new[] { start, Ev(201, 11, t, ("HRESULT", "0x801901AD")), Ev(209, 12, t, ("HRESULT", "0x82AC000B")) }, start).Status);
        var queued = Mdm.SessionOutcome(new[] { start, Ev(209, 12, t, ("HRESULT", "0x80072EE2")), Ev(287, 13, t, ("Message1", Guid1), ("UInt1", "2")) }, start);
        Assert.Equal("Queued", queued.Status);
        Assert.Equal("2", queued.RetryCount);
        Assert.Equal("Failed", Mdm.SessionOutcome(new[] { start, Ev(209, 12, t, ("HRESULT", "0x80072EE2")) }, start).Status);
        var overlap = new[] { start, Ev(208, 11, t, ("Message1", Guid1), ("HexInt1", "0x25")), Ev(209, 12, t, ("HRESULT", "0x0")) };
        Assert.Equal("NotVerified", Mdm.SessionOutcome(overlap, start).Status);
        Assert.Equal("NotVerified", Mdm.SessionOutcome(new[] { start }, start).Status);
        Assert.Single(Mdm.SessionStarts(overlap, Guid1, 3));
    }

    [Fact]
    public void Requires_exactly_one_active_Intune_enrollment()
    {
        const string b = "66666666-7777-8888-9999-000000000000";
        _h.Registry.Put(@"TEST\Enrollments\" + Guid1, "ProviderID", "MS DM Server");
        _h.Registry.Put(@"TEST\Enrollments\" + Guid1, "EnrollmentState", 1);
        _h.Registry.Put(@"TEST\Enrollments\" + b, "ProviderID", "Microsoft Device Management");
        _h.Registry.Put(@"TEST\Enrollments\" + b, "EnrollmentState", 1);
        _h.Registry.AddKey(@"TEST\Enrollments\Status");
        var run = _h.NewRun();
        Assert.Equal(Guid1, Mdm.IntuneEnrollmentId(run));
        _h.Registry.Put(@"TEST\Enrollments\" + b, "ProviderID", "MS DM Server");
        Assert.Null(Mdm.IntuneEnrollmentId(run));
    }

    [Fact]
    public void Stops_without_a_single_enrollment()
    {
        var r = Mdm.VerifiedSync(_h.NewRun());
        Assert.Equal("PreflightFailed", r.Status);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("PreflightFailed", Assert.Single(_h.Records("mdm-result")).Str("status"));
    }

    private void Enroll()
    {
        _h.Registry.Put(@"TEST\Enrollments\" + Guid1, "ProviderID", "MS DM Server");
        _h.Registry.Put(@"TEST\Enrollments\" + Guid1, "EnrollmentState", 1);
    }

    [Fact]
    public void Honours_a_retry_Windows_has_queued_and_sends_nothing()
    {
        Enroll();
        var t = _h.Clock.UtcNow;
        _h.MdmEvents.Events = (log, _) => log == Mdm.AdminLog ? new[] { Ev(286, 10, t, ("Message1", Guid1), ("UInt2", "1800000")) } : Array.Empty<MdmEvent>();
        _h.TaskScheduler.StartError = new InvalidOperationException("must not run");
        var r = Mdm.VerifiedSync(_h.NewRun());
        Assert.Equal("RetryPending", r.Status);
        Assert.Equal(2, r.ExitCode);
        Assert.Equal(0, _h.TaskScheduler.Starts);
        Assert.Single(_h.Records("mdm-queued"));
    }

    private void CompletedSession() =>
        _h.MdmEvents.Events = (log, max) => log == Mdm.SyncLog && max == 200
            ? new[] { Ev(208, 5, _h.Clock.UtcNow, ("Message1", Guid1), ("HexInt1", "0x3")), Ev(209, 6, _h.Clock.UtcNow, ("HRESULT", "0x0")) }
            : Array.Empty<MdmEvent>();

    [Fact]
    public void Verifies_a_session_that_the_task_route_started_and_tries_no_further_route()
    {
        Enroll();
        CompletedSession();
        File.WriteAllText(_h.Services.ImePaths.DeviceEnroller = _folder.Combine("deviceenroller.exe"), "x");
        _h.TaskScheduler.Task = new SyncTask { Name = "Schedule", Path = @"\Microsoft\Windows\EnterpriseMgmt\" + Guid1 + @"\" };
        var r = Mdm.VerifiedSync(_h.NewRun(), 10);
        Assert.Equal("VerifiedSuccess", r.Status);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("enrollment task", r.Route);
        Assert.Equal(1, _h.TaskScheduler.Starts);
        Assert.Equal(@"\Microsoft\Windows\EnterpriseMgmt\" + Guid1 + @"\", Assert.Single(_h.TaskScheduler.Folders));
        Assert.Empty(_h.Started);
    }

    [Fact]
    public void Reads_the_retry_Windows_scheduled_for_this_enrollment_only()
    {
        var t = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        _h.MdmEvents.Events = (_, _) => new[]
        {
            Ev(286, 10, t, ("Message1", Guid1), ("UInt2", "900000")),
            Ev(286, 11, t.AddMinutes(1), ("Message1", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), ("UInt2", "60000")),
        };
        var s = Mdm.RetrySchedule(_h.NewRun(), Guid1);
        Assert.Equal(15, s.DelayMinutes);
        Assert.Equal(t.AddMinutes(15), s.RetryAt);
        Assert.Null(Mdm.RetrySchedule(_h.NewRun(), "99999999-8888-7777-6666-555555555555"));
    }

    [Fact]
    public void Ignores_a_retry_delay_longer_than_Windows_ever_throttles()
    {
        var t = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        // 999,999,999 ms is about 11.5 days.
        _h.MdmEvents.Events = (_, _) => new[] { Ev(286, 10, t, ("Message1", Guid1), ("UInt2", "999999999")) };
        Assert.Null(Mdm.RetrySchedule(_h.NewRun(), Guid1));
        // Up to the cap it is still honoured.
        _h.MdmEvents.Events = (_, _) => new[] { Ev(286, 11, t, ("Message1", Guid1), ("UInt2", (Mdm.MaxHonouredRetryMinutes * 60000).ToString(CultureInfo.InvariantCulture))) };
        Assert.Equal(Mdm.MaxHonouredRetryMinutes, Mdm.RetrySchedule(_h.NewRun(), Guid1).DelayMinutes);
    }

    [Fact]
    public void Does_not_use_an_MDM_event_that_a_process_in_a_user_session_wrote()
    {
        _h.Services.ProcessControl = new WindowsProcessControl();
        var run = _h.NewRun();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        // This test runs in a user session: an event "written" by this process is a user's.
        if (self.SessionId != 0) { Assert.False(Mdm.IsTrustedWriter(run, self.Id.ToString(CultureInfo.InvariantCulture))); }
        // Unknown or exited writers cannot be checked and are accepted.
        Assert.True(Mdm.IsTrustedWriter(run, null));
        Assert.True(Mdm.IsTrustedWriter(run, "0"));
        Assert.True(Mdm.IsTrustedWriter(run, "2147483000"));
        // A session-0 process (the System process, PID 4) is accepted.
        Assert.True(Mdm.IsTrustedWriter(run, "4"));
    }

    [Fact]
    public void Notes_events_from_a_user_session_as_information_not_as_a_warning()
    {
        var ev = Ev(208, 5, DateTime.UtcNow);
        ev.ProcessId = "1234";
        _h.ProcessControl.Sessions[1234] = 1;
        _h.MdmEvents.Events = (_, _) => new[] { ev };
        Assert.Empty(Mdm.ReadEvents(_h.NewRun(), Mdm.SyncLog, new[] { 208 }));
        var note = Assert.Single(_h.Records("progress"), r => r.Str("text").Contains("came from a user session"));
        Assert.NotEqual("warning", note.Str("severity"));
    }

    [Fact]
    public void Honours_a_pending_retry_until_its_start_lag_has_passed_unless_a_later_session_completed()
    {
        var t = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var schedule = Ev(286, 10, t, ("Message1", Guid1), ("UInt2", "900000"));
        _h.MdmEvents.Events = (log, _) => log == Mdm.AdminLog ? new[] { schedule } : Array.Empty<MdmEvent>();
        Assert.Equal(10, Mdm.PendingRetry(_h.NewRun(), Guid1, t.AddMinutes(5)).Value.MinutesRemaining);
        // Due, but still inside the start lag: Windows is about to run it.
        Assert.NotNull(Mdm.PendingRetry(_h.NewRun(), Guid1, t.AddMinutes(16)));
        Assert.Null(Mdm.PendingRetry(_h.NewRun(), Guid1, t.AddMinutes(18)));

        _h.MdmEvents.Events = (log, _) => log == Mdm.AdminLog
            ? new[] { schedule }
            : new[] { Ev(208, 20, t.AddMinutes(2), ("Message1", Guid1), ("HexInt1", "0x3")), Ev(209, 21, t.AddMinutes(3), ("HRESULT", "0x0")) };
        Assert.Null(Mdm.PendingRetry(_h.NewRun(), Guid1, t.AddMinutes(5)));
    }

    [Theory]
    [InlineData("Throttled", "enrollment task", "MDM-SYNC-THROTTLED", "INFO", 1)]
    [InlineData("RetryPending", "skipped", "MDM-SYNC-QUEUED", "INFO", 0)]
    [InlineData("Queued", "deviceenroller", "MDM-SYNC-QUEUED", "INFO", 1)]
    [InlineData("Failed", "deviceenroller", "MDM-SYNC-FAILED", "ERROR", 1)]
    [InlineData("Ambiguous", "enrollment task", "MDM-SYNC-AMBIGUOUS", "WARNING", 1)]
    [InlineData("NotVerified", "enrollment task", "MDM-SYNC-UNVERIFIED", "WARNING", 1)]
    [InlineData("TriggerFailed", "none", "MDM-SYNC-NOT-STARTED", "ERROR", 0)]
    [InlineData("PreflightFailed", "none", "MDM-NO-ENROLLMENT", "ERROR", 0)]
    [InlineData("Something", "none", "MDM-SYNC-UNKNOWN", "WARNING", 0)]
    public void Maps_every_sync_status_to_its_finding(string status, string route, string id, string severity, int changes)
    {
        Mdm.AddFinding(_h.NewRun(), new MdmSyncResult { Status = status, Route = route, Detail = "d" });
        var f = Assert.Single(_h.Findings(id));
        Assert.Equal(severity, f.Str("severity"));
        Assert.Equal(changes, _h.Records("change").Count);
        if (status == "TriggerFailed")
        {
            Assert.Equal("No sync could be started", f.Str("title"));
            Assert.Contains("sync task is disabled or cannot be started", f.Str("support"), StringComparison.Ordinal);
            Assert.Contains("deviceenroller.exe is missing", f.Str("support"), StringComparison.Ordinal);
            Assert.Contains("re-enroll", f.Str("support"), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("VerifiedSuccess", "verified")]
    [InlineData("Throttled", "rate limited by Intune")]
    [InlineData("Queued", "queued by Windows")]
    [InlineData("Failed", "failed")]
    [InlineData("Ambiguous", "could not be verified")]
    [InlineData("NotVerified", "not verified")]
    [InlineData("TriggerFailed", "could not be started")]
    [InlineData("Something", "unknown")]
    public void Names_the_sync_status_in_words(string status, string text) => Assert.Equal(text, Mdm.FormatStatus(status));

    [Fact]
    public void Keeps_raw_event_evidence_out_of_the_progress_lines_and_in_the_persistent_log()
    {
        var run = _h.NewRun();
        run.LogDirectory = _folder.Path;
        Mdm.WriteEvidence(run, new[] { Ev(209, 42, DateTime.UtcNow, ("a", "x"), ("b", "y")) });
        Mdm.WriteEvidence(run, Array.Empty<MdmEvent>());
        Assert.Empty(_h.Lines);
        var log = File.ReadAllText(_folder.Combine("EndpointToolbelt.log"));
        Assert.Matches(@"MDM sync evidence: \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z id=209 rec=42 pid=1 data=\[x / y\]", log);
        Assert.Contains("MDM sync evidence: none", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Describes_a_verified_session_with_locale_independent_numbers()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
        try
        {
            Mdm.AddFinding(_h.NewRun(), new MdmSyncResult { Status = "VerifiedSuccess", Route = "enrollment task", Detail = "" }, (_, _) => (1234.6, 0));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
        Assert.Contains("answered in 1235 s", _h.Findings("MDM-SYNC-OK")[0].Str("detail"), StringComparison.Ordinal);
    }

    [Fact]
    public void Still_looks_for_a_session_when_deviceenroller_does_not_finish_in_time()
    {
        Enroll();
        CompletedSession();
        File.WriteAllText(_h.Services.ImePaths.DeviceEnroller = _folder.Combine("deviceenroller.exe"), "x");
        _h.RunAndWaitResult = (_, _) => null;
        var r = Mdm.VerifiedSync(_h.NewRun(), 10);
        Assert.Equal("VerifiedSuccess", r.Status);
        Assert.Equal("deviceenroller", r.Route);
        Assert.EndsWith("deviceenroller.exe /o \"" + Guid1 + "\" /c /b", Assert.Single(_h.Started), StringComparison.Ordinal);
    }

    [Fact]
    public void Fails_exit_1_never_not_verified_when_no_route_could_request_the_sync()
    {
        Enroll();
        File.WriteAllText(_h.Services.ImePaths.DeviceEnroller = _folder.Combine("deviceenroller.exe"), "x");
        _h.TaskScheduler.Task = new SyncTask { Name = "Schedule", Path = @"\Microsoft\Windows\EnterpriseMgmt\" + Guid1 + @"\" };
        _h.TaskScheduler.StartError = new InvalidOperationException("task refused");
        _h.RunAndWaitResult = (_, _) => throw new InvalidOperationException("no process");
        var run = _h.NewRun();
        var r = Mdm.VerifiedSync(run, 10);
        Assert.Equal("TriggerFailed", r.Status);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("none", r.Route);
        Assert.StartsWith("Every route was attempted", r.Detail, StringComparison.Ordinal);
        Assert.Equal("could not be started", Mdm.FormatStatus(r.Status));
        Mdm.AddFinding(run, r);
        Assert.Single(_h.Findings("MDM-SYNC-NOT-STARTED"));
        Assert.Empty(_h.Findings("MDM-SYNC-UNVERIFIED"));
        Assert.Empty(_h.Records("change"));
    }
}
