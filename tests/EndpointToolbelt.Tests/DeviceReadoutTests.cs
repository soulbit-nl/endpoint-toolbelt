using System;
using System.Collections.Generic;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.State;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>The device settings the service reads and their health rows.</summary>
public class DeviceReadoutTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Compliance_values_become_tokens()
    {
        Assert.Equal("on", ComplianceProbe.BitLockerToken(1, 1));
        Assert.Equal("suspended", ComplianceProbe.BitLockerToken(0, 1));
        Assert.Equal("off", ComplianceProbe.BitLockerToken(0, 0));
        Assert.Equal("encrypting", ComplianceProbe.BitLockerToken(0, 2));
        Assert.Equal("decrypting", ComplianceProbe.BitLockerToken(1, 5));
        Assert.Equal("unknown", ComplianceProbe.BitLockerToken(null, null));
        Assert.Equal("on", ComplianceProbe.SecureBootToken(1));
        Assert.Equal("off", ComplianceProbe.SecureBootToken(0));
        Assert.Equal("unsupported", ComplianceProbe.SecureBootToken(null));
        Assert.Equal("ready", ComplianceProbe.TpmToken(true, true, true));
        Assert.Equal("not-ready", ComplianceProbe.TpmToken(true, true, false));
        Assert.Equal("missing", ComplianceProbe.TpmToken(false, null, null));
        Assert.Equal("2.0", ComplianceProbe.TpmVersionToken("2.0, 0, 1.38"));
        Assert.Null(ComplianceProbe.TpmVersionToken("garbage"));
    }

    [Fact]
    public void Defender_mode_follows_the_documented_values()
    {
        Assert.Equal("normal", DefenderProbe.ModeToken("Normal", true));
        Assert.Equal("passive", DefenderProbe.ModeToken("Passive Mode", true));
        Assert.Equal("passive", DefenderProbe.ModeToken("Passive", true));
        Assert.Equal("edr-block", DefenderProbe.ModeToken("EDR Block Mode", true));
        Assert.Equal("off", DefenderProbe.ModeToken("Normal", false));
        Assert.Equal("unknown", DefenderProbe.ModeToken("", true));
        Assert.Equal("1.421.123.0", DefenderProbe.VersionToken("1.421.123.0"));
        Assert.Null(DefenderProbe.VersionToken("1.4; whoami"));
    }

    [Fact]
    public void The_clock_source_must_be_a_plain_host_before_it_becomes_an_argument()
    {
        Assert.Equal("domain", ClockProbe.SourceKind("NT5DS"));
        Assert.Equal("ntp", ClockProbe.SourceKind("NTP"));
        Assert.Equal("none", ClockProbe.SourceKind("NoSync"));
        Assert.Equal("time.windows.com", ClockProbe.SourceHost("time.windows.com,0x9\r\n"));
        Assert.Equal("dc01.contoso.example", ClockProbe.SourceHost("dc01.contoso.example\r\n"));
        Assert.Null(ClockProbe.SourceHost("Local CMOS Clock\r\n"));
        Assert.Null(ClockProbe.SourceHost("VM IC Time Synchronization Provider"));
        Assert.Null(ClockProbe.SourceHost("evil.example /config /update"));
        Assert.Null(ClockProbe.SourceHost("a.example\"&calc"));
        Assert.Null(ClockProbe.SourceHost(""));
        // A stopped Windows Time service: the configured server is read from its own setting.
        Assert.Equal("time.windows.com", ClockProbe.SourceHost(ClockProbe.FirstNtpServer("time.windows.com,0x9 other.example,0x1")));
        Assert.Null(ClockProbe.FirstNtpServer("  "));
    }

    [Fact]
    public void The_offset_is_read_from_stripchart_output_in_either_decimal_form()
    {
        Assert.Equal(0.0123456, ClockProbe.ParseOffset("Tracking time.windows.com.\r\nCollecting 1 samples.\r\n16:30:00, +00.0123456s\r\n")!.Value, 6);
        Assert.Equal(-312.5, ClockProbe.ParseOffset("16:30:00, -312,5000000s")!.Value, 3);
        Assert.Null(ClockProbe.ParseOffset("16:30:00, error: 0x800705B4"));
        Assert.Null(ClockProbe.ParseOffset(null));
    }

    [Fact]
    public void Failed_settings_are_counted_from_session_zero_writers_and_named_by_their_csp_path()
    {
        Assert.Equal("BitLocker/RequireDeviceEncryption", ProfileErrorsProbe.ShortSetting("./Device/Vendor/MSFT/Policy/Config/BitLocker/RequireDeviceEncryption"));
        Assert.Equal("Firewall/MdmStore/Global", ProfileErrorsProbe.ShortSetting("./Vendor/MSFT/Firewall/MdmStore/Global"));
        Assert.Null(ProfileErrorsProbe.ShortSetting("not a path"));
        Assert.Equal("A_B/C", ProfileErrorsProbe.ShortSetting("./Vendor/MSFT/A B/C"));

        (int, int?, IEnumerable<object>) Event(int level, int pid, params object[] values) => (level, pid, values);
        var events = new List<(int Level, int? ProcessId, IEnumerable<object> Values)>
        {
            Event(2, 4, "./Device/Vendor/MSFT/Policy/Config/BitLocker/RequireDeviceEncryption", -2147024891),
            Event(2, 4, "./Device/Vendor/MSFT/Policy/Config/BitLocker/RequireDeviceEncryption"),
            Event(3, 4, "./Device/Vendor/MSFT/Policy/Config/Update/Pause"),
            Event(2, 666, "./Device/Vendor/MSFT/Policy/Config/Forged/Setting"),
        };
        for (var i = 0; i < 15; i++) { events.Add(Event(2, 4, "./Device/Vendor/MSFT/Policy/Config/Area" + i + "/Setting")); }
        var result = ProfileErrorsProbe.Summarize(events, pid => pid != 666);
        Assert.Equal(17, result.Errors);
        Assert.Equal(1, result.Warnings);
        Assert.Equal(ProfileErrorsProbe.MaxSettings, result.Settings.Count);
        Assert.Equal("BitLocker/RequireDeviceEncryption", result.Settings[0]);
        Assert.DoesNotContain(result.Settings, s => s.Contains("Forged"));
    }

    [Fact]
    public void Intune_tasks_report_the_newest_run_and_ignore_tasks_that_never_ran()
    {
        // Unspecified kind: the task scheduler gives local times, and ToUniversalTime treats it as local.
        var newest = new DateTime(2026, 9, 30, 14, 0, 0);
        var summary = IntuneTasksProbe.Summarize(new[]
        {
            (true, newest, unchecked((int)0x80072EE7)),
            (true, newest.AddHours(-8), 0),
            (false, new DateTime(1899, 12, 30), 0),
        });
        Assert.Equal(3, summary.Tasks);
        Assert.Equal(2, summary.TasksEnabled);
        Assert.Equal("0x80072EE7", summary.LastResult);
        Assert.Equal(newest.ToUniversalTime(), DateTime.Parse(summary.LastRunUtc, null, System.Globalization.DateTimeStyles.RoundtripKind));
        Assert.Null(IntuneTasksProbe.Summarize(Array.Empty<(bool, DateTime, int)>()).LastRunUtc);
    }

    [Fact]
    public void A_running_enrollment_task_does_not_count_as_the_newest_result()
    {
        var newest = new DateTime(2026, 10, 29, 15, 0, 0);
        // Running and queued tasks report a Task Scheduler status: the newest finished run decides.
        var summary = IntuneTasksProbe.Summarize(new[]
        {
            (true, newest, 0x00041301),
            (true, newest.AddMinutes(-1), 0x00041325),
            (true, newest.AddHours(-1), 0),
        });
        Assert.Equal("0x00000000", summary.LastResult);
        Assert.Equal(newest.AddHours(-1).ToUniversalTime(), DateTime.Parse(summary.LastRunUtc, null, System.Globalization.DateTimeStyles.RoundtripKind));

        // Only running tasks: shown as running, and not a failure.
        var running = IntuneTasksProbe.Summarize(new[] { (true, newest, 0x00041301) });
        Assert.Equal("0x00041301", running.LastResult);
        Assert.False(DeviceSettingRows.TaskRunFailed("0x00041301"));
        Assert.False(DeviceSettingRows.TaskRunFailed("0x00041325"));
        Assert.True(DeviceSettingRows.TaskRunFailed("0x80072EE7"));
    }

    [Fact]
    public void A_status_request_never_waits_for_the_readouts()
    {
        var now = Now;
        var reads = 0;
        using var gate = new System.Threading.ManualResetEventSlim(false);
        var reader = new DeviceReadoutReader(() =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            System.Threading.Interlocked.Increment(ref reads);
            return new DeviceReadouts { Clock = new ClockReadout { OffsetSeconds = reads } };
        }, () => now);
        // The first request gets nothing at once; the read runs in the background.
        Assert.Null(reader.Get());
        // A second request while that read still runs starts no second read.
        Assert.Null(reader.Get());
        gate.Set();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        DeviceReadouts got = null;
        while (got == null && DateTime.UtcNow < deadline) { got = reader.Get(); if (got == null) { System.Threading.Thread.Sleep(20); } }
        Assert.NotNull(got);
        Assert.Equal(1, got.Clock.OffsetSeconds);
        // Fresh: answered from the cache without another read.
        Assert.Same(got, reader.Get());
        Assert.Equal(1, reads);
        // A throwing read leaves an empty answer, not an exception.
        var failing = new DeviceReadoutReader(() => throw new InvalidOperationException(), () => now);
        failing.RefreshNow();
        Assert.NotNull(failing.Get());
    }
    private static StatusInfo Status(DeviceReadouts readouts) => new() { Readouts = readouts };

    [Fact]
    public void Compliance_row_names_what_is_off_and_never_gives_a_verdict()
    {
        var all = new ComplianceReadout { BitLocker = "on", SecureBoot = "on", Tpm = "ready", TpmVersion = "2.0", FirewallDomain = "on", FirewallPrivate = "on", FirewallPublic = "on", OsBuild = "10.0.26100.1", RecoveryPassword = true };
        var ok = DeviceSettingRows.Compliance(Status(null), all);
        Assert.Equal(HealthStatus.Pass, ok.Status);
        Assert.Equal("BitLocker, Secure Boot, TPM 2.0 and firewall are on", ok.Detail);
        Assert.Contains("recovery password: yes", ok.ReportDetail);
        Assert.DoesNotContain("compliant", ok.Detail, StringComparison.OrdinalIgnoreCase);

        all.BitLocker = "suspended";
        all.FirewallPublic = "off";
        all.Tpm = null;
        var check = DeviceSettingRows.Compliance(Status(null), all);
        Assert.Equal(HealthStatus.Warn, check.Status);
        Assert.Equal("BitLocker suspended, firewall off (public), TPM unknown", check.Detail);
        Assert.Contains("Intune decides compliance", check.Hint);

        Assert.True(DeviceSettingRows.Compliance(null, null).ServiceUnavailable);
        Assert.StartsWith("Unknown: still being read", DeviceSettingRows.Compliance(Status(null), null).Detail);
    }

    [Fact]
    public void Settings_Windows_reports_on_every_device_are_not_counted_as_failures()
    {
        (int, int?, IEnumerable<object>) Event(int level, params object[] values) => (level, 4, values);
        var events = new List<(int Level, int? ProcessId, IEnumerable<object> Values)>
        {
            Event(2, "./Vendor/MSFT/Policy/ConfigOperations/ADMXInstall/Receiver/Properties/Policy/FakePolicy/Version", -2147024809),
            Event(2, "./Device/Vendor/MSFT/DevicePreparation/PageStatus"),
            Event(3, "./Device/Vendor/MSFT/DevicePreparation/PageStatus"),
            Event(2, "./Device/Vendor/MSFT/DMClient/Provider/MS DM Server/CertPinning/PinRules"),
            Event(2, "./Device/Vendor/MSFT/Policy/Config/BitLocker/RequireDeviceEncryption"),
        };
        var result = ProfileErrorsProbe.Summarize(events, _ => true);
        Assert.Equal(1, result.Errors);
        Assert.Equal(0, result.Warnings);
        Assert.Equal(4, result.KnownHarmless);
        Assert.Equal("BitLocker/RequireDeviceEncryption", result.Settings.Single());

        // Only harmless ones: the row is OK and the report says how many were left out.
        var onlyHarmless = ProfileErrorsProbe.Summarize(events.Take(4).ToList(), _ => true);
        var row = DeviceSettingRows.Profiles(Status(null), onlyHarmless);
        Assert.Equal(HealthStatus.Pass, row.Status);
        Assert.Contains("4 known harmless events not counted", row.ReportDetail);
    }

    [Fact]
    public void Profile_row_lists_failed_settings_and_links_to_retry()
    {
        var p = new ProfileErrorsReadout { Errors = 5, Warnings = 2, Settings = new List<string> { "A/B", "C/D", "E/F", "G/H" } };
        var row = DeviceSettingRows.Profiles(Status(null), p);
        Assert.Equal(HealthStatus.Warn, row.Status);
        Assert.Equal("4 settings failed in the last 7 days: A: b; C: d; E: f and more", row.Detail);
        Assert.Equal(HealthFix.RetryAppsAndPolicies, row.Fix);
        Assert.Contains("G/H", row.ReportDetail);
        var ok = DeviceSettingRows.Profiles(Status(null), new ProfileErrorsReadout { Warnings = 1 });
        Assert.Equal(HealthStatus.Pass, ok.Status);
        Assert.Equal(HealthFix.None, ok.Fix);
        Assert.Equal(HealthStatus.Warn, DeviceSettingRows.Profiles(Status(null), new ProfileErrorsReadout { Unavailable = "unreadable" }).Status);
        // An MDM error that names no setting is not a profile failure: in the report only.
        var other = DeviceSettingRows.Profiles(Status(null), new ProfileErrorsReadout { Errors = 2 });
        Assert.Equal(HealthStatus.Pass, other.Status);
        Assert.Contains("2 other MDM errors", other.ReportDetail);
    }

    [Theory]
    [InlineData(0.4, HealthStatus.Pass, "Less than a second off")]
    [InlineData(-42, HealthStatus.Pass, "42 seconds off")]
    [InlineData(60, HealthStatus.Warn, "1 minute off")]
    [InlineData(-299, HealthStatus.Warn, "5 minutes off")]
    [InlineData(300, HealthStatus.Fail, "5 minutes off")]
    public void Clock_row_uses_one_minute_and_five_minutes(double offset, HealthStatus expected, string text)
    {
        var row = DeviceSettingRows.Clock(Status(null), new ClockReadout { OffsetSeconds = offset, Source = "ntp" });
        Assert.Equal(expected, row.Status);
        Assert.StartsWith(text, row.Detail);
    }

    [Fact]
    public void Clock_row_without_a_measurement_says_why()
    {
        Assert.Equal("The Windows Time service is not running", DeviceSettingRows.Clock(Status(null), new ClockReadout { Unavailable = "not-running" }).Detail);
        Assert.StartsWith("Unknown: there is no time source", DeviceSettingRows.Clock(Status(null), new ClockReadout { Unavailable = "no-source" }).Detail);
    }

    [Fact]
    public void Defender_row_uses_three_and_seven_days_and_accepts_another_antivirus()
    {
        DefenderReadout Normal(double ageDays, bool realTime = true) => new()
        {
            Mode = "normal",
            RealTimeProtection = realTime,
            SignatureUpdatedUtc = Now.AddDays(-ageDays).ToString("o"),
            SignatureVersion = "1.421.1.0",
        };
        Assert.Equal(HealthStatus.Pass, DeviceSettingRows.Defender(Status(null), Normal(1), Now).Status);
        Assert.Equal(HealthStatus.Warn, DeviceSettingRows.Defender(Status(null), Normal(4), Now).Status);
        Assert.Equal(HealthStatus.Fail, DeviceSettingRows.Defender(Status(null), Normal(8), Now).Status);
        var off = DeviceSettingRows.Defender(Status(null), Normal(1, realTime: false), Now);
        Assert.Equal(HealthStatus.Fail, off.Status);
        Assert.StartsWith("Real-time protection off", off.Detail);
        Assert.Equal(HealthStatus.Pass, DeviceSettingRows.Defender(Status(null), new DefenderReadout { Mode = "passive" }, Now).Status);
        Assert.Equal(HealthStatus.Warn, DeviceSettingRows.Defender(Status(null), new DefenderReadout { Mode = "missing" }, Now).Status);
    }

    [Fact]
    public void The_device_report_has_the_intune_connection_and_the_readouts_survive_the_pipe()
    {
        var readouts = new DeviceReadouts
        {
            Compliance = new ComplianceReadout { BitLocker = "on" },
            Defender = new DefenderReadout { Mode = "normal" },
            Clock = new ClockReadout { OffsetSeconds = 1.5, Source = "domain" },
            Profiles = new ProfileErrorsReadout { Errors = 1, Settings = new List<string> { "A/B" } },
            IntuneConnection = new IntuneConnectionReadout { Tasks = 4, TasksEnabled = 4, LastRunUtc = "2026-10-16T10:00:00.0000000Z", LastResult = "0x00000000" },
        };
        var back = MessageFraming.Deserialize<StatusInfo>(MessageFraming.Serialize(new StatusInfo { Readouts = readouts }));
        Assert.Equal(1.5, back.Readouts.Clock.OffsetSeconds);
        Assert.Equal("A/B", back.Readouts.Profiles.Settings.Single());
        Assert.Equal("0x00000000", back.Readouts.IntuneConnection.LastResult);

        var lines = DeviceSettingRows.IntuneConnectionLines(readouts.IntuneConnection).ToList();
        Assert.Equal("Intune tasks   : 4 tasks, 4 enabled", lines[0]);
        Assert.Contains("result 0x00000000", lines[1]);
        Assert.Equal("Intune tasks   : no single Intune enrollment", DeviceSettingRows.IntuneConnectionLines(new IntuneConnectionReadout { Unavailable = "not-enrolled" }).Single());
    }

    [Fact]
    public void The_report_names_a_disabled_Intune_task_by_its_name_only()
    {
        var tasks = Enumerable.Range(0, 15).Select(i => (i != 3, new DateTime(2026, 10, 16, 10, 0, 0), 0)).ToList();
        var summary = IntuneTasksProbe.Summarize(tasks, new[] { "Schedule #3 created by enrollment client" });
        Assert.Equal(new[] { "Schedule #3 created by enrollment client" }, summary.DisabledTasks);
        var back = MessageFraming.Deserialize<StatusInfo>(MessageFraming.Serialize(new StatusInfo { Readouts = new DeviceReadouts { IntuneConnection = summary } }));
        Assert.Equal("Intune tasks   : 15 tasks, 14 enabled; disabled: Schedule #3 created by enrollment client",
            DeviceSettingRows.IntuneConnectionLines(back.Readouts.IntuneConnection).First());

        // More disabled tasks than names: the rest is counted. An older service sends no names.
        Assert.Equal("Intune tasks   : 15 tasks, 12 enabled; disabled: PushLaunch and 2 more",
            DeviceSettingRows.IntuneConnectionLines(new IntuneConnectionReadout { Tasks = 15, TasksEnabled = 12, DisabledTasks = new List<string> { "PushLaunch" } }).First());
        Assert.Equal("Intune tasks   : 15 tasks, 14 enabled",
            DeviceSettingRows.IntuneConnectionLines(new IntuneConnectionReadout { Tasks = 15, TasksEnabled = 14, DisabledTasks = null }).First());

        // Plain text only, no GUID, at most 80 characters.
        Assert.Equal("Schedule for (renewal)", IntuneTasksProbe.TaskName("Schedule for {11111111-2222-3333-4444-555555555555} (renewal)\u0007"));
        Assert.Null(IntuneTasksProbe.TaskName("11111111-2222-3333-4444-555555555555"));
        Assert.Null(IntuneTasksProbe.TaskName("  "));
        Assert.Equal(80, IntuneTasksProbe.TaskName(new string('a', 200)).Length);
    }
}
