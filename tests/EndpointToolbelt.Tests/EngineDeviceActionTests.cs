using System;
using System.IO;
using System.Linq;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.Actions;
using EndpointToolbelt.Platform;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The device actions of the compiled engine. Every system call goes to a fake: these tests never
/// change BitLocker, the clock, Defender or the disk of this machine.
/// </summary>
public class EngineTrustedToolTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Trusts_a_Windows_file_and_refuses_one_a_user_owns()
    {
        var kernel = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ntoskrnl.exe");
        Assert.True(FileSystemSafety.IsAdminOnlyPath(kernel));
        Assert.True(FileSystemSafety.IsAdminOnlyPath(Path.GetDirectoryName(kernel)));
        Assert.True(Authenticode.IsMicrosoftSigned(kernel));
        // Signed only through a catalog: refused, so a tool SYSTEM runs needs its own signature.
        Assert.False(Authenticode.IsMicrosoftSigned(ProcessRunner.SystemTool("cmd.exe")));
        var mine = _folder.Combine("mine.exe");
        File.WriteAllText(mine, "x");
        Assert.False(FileSystemSafety.IsAdminOnlyPath(mine));
        Assert.False(Authenticode.IsMicrosoftSigned(mine));
        Assert.False(FileSystemSafety.IsAdminOnlyPath(mine + ".missing"));
    }

    [Fact]
    public void Refuses_a_link()
    {
        var real = _folder.Combine("realdir");
        Directory.CreateDirectory(real);
        var link = _folder.Combine("linkdir");
        TestFolder.CreateJunction(link, real);
        Assert.False(FileSystemSafety.IsAdminOnlyPath(link));
        Directory.Delete(link);
    }
}

public class EngineBackupRecoveryKeyTests
{
    private const string Recovery = "{22222222-2222-2222-2222-222222222222}";
    private readonly EngineHarness _h = new();

    private int? Run() => new BackupRecoveryKeyHandler().Run(_h.NewRun(action: "BackupRecoveryKey"));

    private void Joined(bool joined, int backupExit = 0, string backupError = "") =>
        _h.Processes.Respond = r => Path.GetFileName(r.FilePath) == "dsregcmd.exe"
            ? FakeProcessRunner.Exit(0, joined ? "  AzureAdJoined : YES\r\n" : "  AzureAdJoined : NO\r\n")
            : FakeProcessRunner.Exit(backupExit, "", backupError);

    private static BitLockerVolumeInfo Volume(string status, params string[] ids) =>
        new() { VolumeStatus = status, ProtectionStatus = status == "FullyDecrypted" ? "Off" : "On", RecoveryPasswordIds = ids };

    [Fact]
    public void Backs_up_existing_recovery_passwords_only_through_the_in_box_cmdlet_runner()
    {
        _h.Device.BitLocker = () => Volume("FullyEncrypted", Recovery);
        Joined(true);
        _h.EventLog.Events = _ => new[] { new EventInfo { Id = 845, Level = 4 } };
        Assert.Equal(0, Run());
        var backup = Assert.Single(_h.Processes.Of("powershell.exe"));
        Assert.Contains("BackupToAAD-BitLockerKeyProtector -MountPoint 'C:' -KeyProtectorId '" + Recovery + "'", backup.Arguments, StringComparison.Ordinal);
        Assert.Single(_h.Findings("BITLOCKER-BACKUP-DONE"));
        Assert.Equal("Backed up 1 BitLocker recovery key of the system drive to Microsoft Entra ID", Assert.Single(_h.Records("change")).Str("text"));
        Assert.Contains(_h.Records("progress"), r => r.Str("text") == "BitLocker on C:: volume status FullyEncrypted, protection On, 1 recovery password.");
    }

    [Fact]
    public void Changes_nothing_without_a_recovery_password_when_BitLocker_is_off_or_when_the_device_is_not_joined()
    {
        Joined(false);
        _h.Device.BitLocker = () => Volume("FullyEncrypted");
        Assert.Equal(2, Run());
        _h.Device.BitLocker = () => Volume("FullyDecrypted");
        Assert.Equal(2, Run());
        _h.Device.BitLocker = () => Volume("FullyEncrypted", Recovery);
        Assert.Equal(2, Run());
        _h.Device.BitLocker = () => throw new BitLockerUnavailableException(BitLockerProblem.NotAvailable);
        Assert.Equal(2, Run());
        // No BitLocker volume at all (Windows Sandbox) is not a failure.
        _h.Device.BitLocker = () => throw new BitLockerUnavailableException(BitLockerProblem.NoVolume);
        Assert.Equal(2, Run());
        _h.Device.BitLocker = () => throw new UnauthorizedAccessException("Access denied.");
        Assert.Equal(1, Run());
        foreach (var id in new[] { "BITLOCKER-NO-RECOVERY-PASSWORD", "BITLOCKER-OFF", "BITLOCKER-NOT-JOINED", "BITLOCKER-UNREADABLE" }) { Assert.Single(_h.Findings(id)); }
        Assert.Equal(2, _h.Findings("BITLOCKER-NOT-AVAILABLE").Count);
        Assert.Empty(_h.Records("change"));
        Assert.Empty(_h.Processes.Of("powershell.exe"));
    }

    [Fact]
    public void Is_not_verified_without_an_event_and_failed_on_an_error()
    {
        _h.Device.BitLocker = () => Volume("FullyEncrypted", Recovery);
        Joined(true);
        Assert.Equal(2, Run());
        _h.EventLog.Events = _ => new[] { new EventInfo { Id = 846, Level = 2 } };
        Assert.Equal(1, Run());
        _h.EventLog.Events = _ => Array.Empty<EventInfo>();
        Joined(true, 1, "no network");
        Assert.Equal(1, Run());
        Assert.Single(_h.Findings("BITLOCKER-BACKUP-UNVERIFIED"));
        Assert.Equal(2, _h.Findings("BITLOCKER-BACKUP-FAILED").Count);
        Assert.Empty(_h.Records("change"));
        Assert.Contains(_h.Records("progress"), r => r.Str("text") == "The backup of protector " + Recovery + " failed: no network");
        // The event wait: six times five seconds at most, on the run's clock.
        Assert.Equal(TimeSpan.FromSeconds(5), _h.Clock.Sleeps.Distinct().Single());
    }

    private const string DeviceId = "33333333-3333-3333-3333-333333333333";
    private const string TenantId = "44444444-4444-4444-4444-444444444444";

    private static EventInfo[] KeyLimitEvents(DateTime at) => new[]
    {
        new EventInfo { Id = 846, Level = 2, TimeCreatedUtc = at, Message =
            "Failed to backup BitLocker Drive Encryption recovery information for volume C: to your Entra ID.\r\n\r\nTraceId: {55555555-5555-5555-5555-555555555555}\r\n"
            + "Error: We couldn't save your BitLocker recovery information to your Microsoft Entra ID because the maximum number of recovery passwords have already been saved for this device. Please try again later." },
        new EventInfo { Id = 873, Level = 2, TimeCreatedUtc = at.AddSeconds(1), Message =
            "Server reported a failure while attempting to backup a recovery password.\r\nError: Bad request\r\nHTTP Status Code: 400\r\nRetryRequest: false" },
        new EventInfo { Id = 847, Level = 2, TimeCreatedUtc = at.AddSeconds(2), Message =
            "Failed to save BitLocker Drive Encryption recovery information to your Entra ID due to an error.\r\nRequest Id: 66666666-6666-6666-6666-666666666666\r\n"
            + "Error Code: directory_error\r\nError Subcode: error_keys_exceed_max_limit\r\n"
            + "Error message: Max limit for BitLocker keys has been reached for device: '" + DeviceId + "' in tenant: '" + TenantId + "'" },
    };

    [Fact]
    public void Logs_each_error_event_and_names_the_Entra_key_limit_without_the_device_or_tenant_ID()
    {
        _h.Device.BitLocker = () => Volume("FullyEncrypted", Recovery);
        Joined(true, 1, "We couldn't save your BitLocker recovery information to your Microsoft Entra ID");
        var now = _h.Clock.UtcNow;
        _h.BootUtc = now.AddHours(-1);
        const string autoInitText = "Device Encryption initialized automatically for volume D:.";
        var autoInit = new[]
        {
            // Two earlier starts and this one: the disk is encrypted again after each start.
            new EventInfo { Id = 4146, Level = 4, TimeCreatedUtc = now.AddDays(-2), Message = autoInitText },
            new EventInfo { Id = 4146, Level = 4, TimeCreatedUtc = now.AddDays(-1), Message = autoInitText },
            new EventInfo { Id = 4146, Level = 4, TimeCreatedUtc = now.AddMinutes(-50), Message = autoInitText },
        };
        _h.EventLog.Events = _ => KeyLimitEvents(now).Concat(autoInit).ToArray();
        Assert.Equal(1, Run());

        var progress = _h.Records("progress").Select(r => r.Str("text")).ToList();
        Assert.Contains("BitLocker error event 846: Failed to backup BitLocker Drive Encryption recovery information for volume C: to your Entra ID. Error: We couldn't save your BitLocker recovery information to your Microsoft Entra ID because the maximum number of recovery passwords have already been saved for this device. Please try again later.", progress);
        Assert.Contains("BitLocker error event 873: Server reported a failure while attempting to backup a recovery password. Error: Bad request HTTP Status Code: 400 RetryRequest: false", progress);
        Assert.Contains("BitLocker error event 847: Failed to save BitLocker Drive Encryption recovery information to your Entra ID due to an error. Error Code: directory_error Error Subcode: error_keys_exceed_max_limit Error message: Max limit for BitLocker keys has been reached", progress);
        Assert.Contains("The backup of C: failed in the same start of Windows in which device encryption was initialized automatically for volume D: (event 4146).", progress);

        Assert.Empty(_h.Findings("BITLOCKER-BACKUP-FAILED"));
        var limit = Assert.Single(_h.Findings("BITLOCKER-KEY-LIMIT"));
        Assert.Contains("Trying again does not help", limit.Str("detail"), StringComparison.Ordinal);
        Assert.Contains("Remove old recovery keys of this device in Microsoft Entra ID, or find what creates new ones.", limit.Str("support"), StringComparison.Ordinal);
        Assert.Contains("for volume D: 3 times in the last 30 days", limit.Str("support"), StringComparison.Ordinal);
        Assert.Contains("Event 847: Failed to save", limit.Str("support"), StringComparison.Ordinal);
        Assert.Contains("error_keys_exceed_max_limit", limit.Str("support"), StringComparison.Ordinal);
        foreach (var line in _h.Lines)
        {
            Assert.DoesNotContain(DeviceId, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(TenantId, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("55555555", line, StringComparison.Ordinal);
            Assert.DoesNotContain("66666666", line, StringComparison.Ordinal);
            Assert.DoesNotContain("TraceId", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Request Id", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Names_the_key_limit_from_the_846_text_alone_and_writes_only_what_the_events_show()
    {
        _h.Device.BitLocker = () => Volume("FullyEncrypted", Recovery);
        Joined(true, 1, "failed");
        _h.EventLog.Events = _ => KeyLimitEvents(_h.Clock.UtcNow).Take(1).ToArray();
        Assert.Equal(1, Run());
        var limit = Assert.Single(_h.Findings("BITLOCKER-KEY-LIMIT"));
        // No event 4146: no word about automatically encrypted disks; no boot time: no line about this start.
        Assert.DoesNotContain("4146", limit.Str("support"), StringComparison.Ordinal);
        Assert.Contains("Event 846: Failed to backup", limit.Str("support"), StringComparison.Ordinal);
        Assert.DoesNotContain(_h.Records("progress"), r => r.Str("text").Contains("same start of Windows"));
    }

    [Fact]
    public void A_failure_that_is_not_the_key_limit_still_says_try_again_and_names_the_telling_event()
    {
        _h.Device.BitLocker = () => Volume("FullyEncrypted", Recovery);
        Joined(true);
        _h.EventLog.Events = _ => new[]
        {
            new EventInfo { Id = 846, Level = 2, Message = "Failed to backup BitLocker Drive Encryption recovery information for volume C: to your Entra ID.\r\nError: The request timed out." },
            new EventInfo { Id = 847, Level = 2, Message = "Request Id: 77777777-7777-7777-7777-777777777777\r\nError Code: service_unavailable" },
        };
        Assert.Equal(1, Run());
        var failed = Assert.Single(_h.Findings("BITLOCKER-BACKUP-FAILED"));
        Assert.StartsWith("Try again later", failed.Str("next"), StringComparison.Ordinal);
        Assert.Equal("Event 847: Error Code: service_unavailable", failed.Str("support"));
        Assert.Empty(_h.Findings("BITLOCKER-KEY-LIMIT"));
    }

    [Fact]
    public void Cleans_an_event_message_to_one_line_without_IDs()
    {
        Assert.Equal("", BitLockerEventText.Clean(null));
        Assert.Equal("", BitLockerEventText.Clean(" \r\n "));
        Assert.Equal("Protector (ID left out) failed.", BitLockerEventText.Clean("Protector {11111111-2222-3333-4444-555555555555} failed.\r\n\tCorrelation Id: abc"));
        var longText = BitLockerEventText.Clean(new string('x', 1000));
        Assert.Equal(BitLockerEventText.MaxLength, longText.Length);
        Assert.EndsWith("...", longText, StringComparison.Ordinal);
        Assert.Equal("D:", BitLockerEventText.VolumeOf("Device Encryption initialized automatically for volume d:."));
        Assert.Equal("", BitLockerEventText.VolumeOf("no volume here"));
        Assert.True(BitLockerEventText.IsKeyLimit("Error Subcode: error_keys_exceed_max_limit"));
        Assert.False(BitLockerEventText.IsKeyLimit("HTTP Status Code: 400"));
    }

    [Fact]
    public void Names_the_policy_when_one_refuses_the_backup_cmdlet()
    {
        _h.Device.BitLocker = () => Volume("FullyEncrypted", Recovery);
        Joined(true, 1, "File BitLocker.psm1 cannot be loaded. The file is not digitally signed.");
        Assert.Equal(1, Run());
        var blocked = Assert.Single(_h.Findings("ENGINE-POLICY-BLOCKED"));
        Assert.Contains("BackupToAAD-BitLockerKeyProtector", blocked.Str("support"), StringComparison.Ordinal);
        Assert.Single(_h.Findings("BITLOCKER-BACKUP-FAILED"));
    }
}

public class EngineSyncClockTests
{
    private const string Parameters = @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters";
    private readonly EngineHarness _h = new();

    [Fact]
    public void Reads_the_offset_from_stripchart_output()
    {
        Assert.Equal(0.0123456, SyncClockHandler.ParseStripchart("Tracking time.windows.com.\r\n16:30:00, +00.0123456s\r\n"));
        Assert.Equal(-312.5, SyncClockHandler.ParseStripchart("16:30:00, -312,5s"));
        Assert.Null(SyncClockHandler.ParseStripchart("16:30:00, error: 0x800705B4"));
    }

    [Fact]
    public void Uses_only_a_plain_host_name_as_the_time_source_from_the_service_or_its_NtpServer_setting()
    {
        var run = _h.NewRun();
        _h.Processes.Result = FakeProcessRunner.Exit(0, "time.windows.com,0x9\r\n");
        Assert.Equal("time.windows.com", SyncClockHandler.ClockSourceHost(run, true));
        _h.Processes.Result = FakeProcessRunner.Exit(0, "evil.example /config /update");
        _h.Registry.Put(Parameters, "Type", "NTP");
        _h.Registry.Put(Parameters, "NtpServer", "time.contoso.example,0x9 other.example,0x1");
        Assert.Equal("time.contoso.example", SyncClockHandler.ClockSourceHost(run, true));
        Assert.Equal("time.contoso.example", SyncClockHandler.ClockSourceHost(run, false));
        _h.Registry.Put(Parameters, "Type", "NT5DS");
        _h.Registry.Put(Parameters, "NtpServer", "");
        Assert.Null(SyncClockHandler.ClockSourceHost(run, false));
    }

    [Fact]
    public void Starts_a_stopped_time_service_resyncs_and_succeeds_only_when_the_clock_is_right_afterwards()
    {
        _h.ServiceControl.Running = false;
        _h.Registry.Put(Parameters, "Type", "NTP");
        _h.Registry.Put(Parameters, "NtpServer", "time.contoso.example,0x9");
        var offsets = new System.Collections.Generic.Queue<string>(new[] { "+400.0s", "+00.4s", "+400.0s", "+120.0s", "error", "error" });
        _h.Processes.Respond = r => r.Arguments.StartsWith("/stripchart", StringComparison.Ordinal)
            ? FakeProcessRunner.Exit(0, "16:30:00, " + offsets.Dequeue() + "\r\n")
            : FakeProcessRunner.Exit(0);
        var handler = new SyncClockHandler();
        Assert.Equal(0, handler.Run(_h.NewRun(action: "SyncClock")));
        Assert.Single(_h.ServiceControl.Started);
        Assert.Single(_h.Processes.Requests, r => r.Arguments == "/resync");
        Assert.Contains(_h.Processes.Requests, r => r.Arguments == "/stripchart /computer:time.contoso.example /dataonly /samples:1");
        // Never a configuration change of the time service.
        Assert.DoesNotContain(_h.Processes.Requests, r => r.Arguments.Contains("/config") || r.Arguments.Contains("/register") || r.Arguments.Contains("/unregister"));

        Assert.Equal(1, handler.Run(_h.NewRun(action: "SyncClock")));
        Assert.Equal(2, handler.Run(_h.NewRun(action: "SyncClock")));
        Assert.Single(_h.Findings("CLOCK-OK"));
        Assert.Equal("It is 0.4 seconds off its time source.", _h.Findings("CLOCK-OK")[0].Str("detail"));
        Assert.Single(_h.Findings("CLOCK-STILL-OFF"));
        Assert.Single(_h.Findings("CLOCK-NOT-VERIFIED"));
        Assert.Single(_h.Records("change"), r => r.Str("text") == "Synchronised the clock with its time source");
        Assert.Equal(3, _h.Records("change").Count(r => r.Str("text") == "Started the Windows Time service"));
        Assert.Contains(_h.Records("progress"), r => r.Str("text") == "Offset before: 400 seconds to time.contoso.example.");
    }

    [Fact]
    public void Fails_when_the_time_service_cannot_start_or_the_resync_fails_unmeasured()
    {
        _h.ServiceControl.Running = false;
        _h.ServiceControl.StartError = new InvalidOperationException("Cannot start service W32Time.");
        Assert.Equal(1, new SyncClockHandler().Run(_h.NewRun()));
        Assert.Equal("Cannot start service W32Time.", Assert.Single(_h.Findings("CLOCK-SERVICE-NOT-STARTED")).Str("detail"));
        _h.ServiceControl.Running = true;
        _h.Processes.Result = FakeProcessRunner.Exit(-2147023174);
        Assert.Equal(1, new SyncClockHandler().Run(_h.NewRun()));
        Assert.Equal("w32tm /resync ended with 0x800706BA, and the offset could not be measured.", Assert.Single(_h.Findings("CLOCK-SYNC-FAILED")).Str("detail"));
    }
}

public class EngineUpdateDefenderTests
{
    private readonly EngineHarness _h = new();

    private static DefenderInfo Status(string mode, bool enabled, string version, DateTime? updated = null) =>
        new() { Mode = mode, ServiceEnabled = enabled, Version = version, UpdatedUtc = updated };

    [Fact]
    public void Changes_nothing_when_another_antivirus_is_in_charge_or_Defender_is_missing()
    {
        var handler = new UpdateDefenderHandler { FindMpCmdRun = _ => throw new InvalidOperationException("must not run") };
        _h.Device.Defender.Enqueue(Status("Passive Mode", true, "1.1.1.0"));
        Assert.Equal(69003, handler.Run(_h.NewRun()));
        _h.Device.Defender.Enqueue(Status("Normal", false, "1.1.1.0"));
        Assert.Equal(69003, handler.Run(_h.NewRun()));
        Assert.Equal(69003, handler.Run(_h.NewRun()));
        Assert.Equal(2, _h.Findings("DEFENDER-NOT-PRIMARY").Count);
        Assert.Single(_h.Findings("DEFENDER-MISSING"));
        Assert.Empty(_h.Processes.Requests);
    }

    [Fact]
    public void Runs_the_trusted_MpCmdRun_and_succeeds_only_when_the_version_changed()
    {
        const string tool = @"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.1-0\MpCmdRun.exe";
        var handler = new UpdateDefenderHandler { FindMpCmdRun = _ => tool };
        var old = Status("Normal", true, "1.421.1.0", DateTime.UtcNow.AddDays(-9));
        var @new = Status("Normal", true, "1.421.9.0", DateTime.UtcNow);
        RunContext Run25() { var r = _h.NewRun(action: "UpdateDefender"); r.TimeLimitMinutes = 25; return r; }
        _h.Device.Defender.Enqueue(old); _h.Device.Defender.Enqueue(@new);
        Assert.Equal(0, handler.Run(Run25()));
        var update = Assert.Single(_h.Processes.Requests);
        Assert.Equal(tool, update.FilePath);
        Assert.Equal("-SignatureUpdate", update.Arguments);
        Assert.Equal(10, update.TimeoutMinutes);
        _h.Device.Defender.Enqueue(old); _h.Device.Defender.Enqueue(old);
        Assert.Equal(69003, handler.Run(Run25()));
        _h.Processes.Result = FakeProcessRunner.Exit(-2147012889);
        _h.Device.Defender.Enqueue(old); _h.Device.Defender.Enqueue(old);
        Assert.Equal(1, handler.Run(Run25()));
        handler.FindMpCmdRun = _ => null;
        _h.Device.Defender.Enqueue(old);
        Assert.Equal(1, handler.Run(Run25()));
        Assert.Single(_h.Findings("DEFENDER-UPDATED"));
        Assert.Equal("Version 1.421.9.0 (was 1.421.1.0).", _h.Findings("DEFENDER-UPDATED")[0].Str("detail"));
        Assert.Single(_h.Findings("DEFENDER-ALREADY-CURRENT"));
        Assert.Equal("MpCmdRun ended with 0x80072EE7.", Assert.Single(_h.Findings("DEFENDER-UPDATE-FAILED")).Str("detail"));
        Assert.Single(_h.Findings("DEFENDER-TOOL-UNTRUSTED"));
    }

    [Fact]
    public void Finds_MpCmdRun_only_in_a_protected_folder_with_a_Microsoft_signature()
    {
        // The real lookup, read-only: on a device with Defender it finds a trusted copy.
        var path = UpdateDefenderHandler.MpCmdRunPath(_h.NewRun());
        if (path != null)
        {
            Assert.EndsWith("MpCmdRun.exe", path, StringComparison.OrdinalIgnoreCase);
            Assert.True(Authenticode.IsMicrosoftSigned(path));
        }
    }
}

public class EngineFreeDiskSpaceTests
{
    private const long GB = 1024L * 1024 * 1024;
    private readonly EngineHarness _h = new();

    private RunContext Run60()
    {
        var run = _h.NewRun(action: "FreeDiskSpace");
        run.TimeLimitMinutes = 60;
        return run;
    }

    [Fact]
    public void Clears_the_Delivery_Optimization_cache_and_the_component_store_never_with_ResetBase_and_reports_the_bytes_freed()
    {
        var handler = new FreeDiskSpaceHandler();
        _h.FreeBytes.Enqueue(10 * GB); _h.FreeBytes.Enqueue(12 * GB);
        Assert.Equal(0, handler.Run(Run60()));
        var cache = Assert.Single(_h.Processes.Of("powershell.exe"));
        Assert.Contains("Delete-DeliveryOptimizationCache -Force", cache.Arguments, StringComparison.Ordinal);
        var dism = Assert.Single(_h.Processes.Of("dism.exe"));
        Assert.Contains("StartComponentCleanup", dism.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetBase", dism.Arguments, StringComparison.Ordinal);
        _h.FreeBytes.Enqueue(10 * GB); _h.FreeBytes.Enqueue(10 * GB);
        Assert.Equal(69003, handler.Run(Run60()));
        _h.RunningProcesses.Add("TiWorker");
        _h.FreeBytes.Enqueue(10 * GB); _h.FreeBytes.Enqueue(10 * GB);
        Assert.Equal(2, handler.Run(Run60()));
        var change = Assert.Single(_h.Records("change"));
        Assert.StartsWith("Freed 2 GB on ", change.Str("text"), StringComparison.Ordinal);
        // Every result shows the free space before and after.
        var freed = Assert.Single(_h.Findings("DISK-FREED"));
        Assert.Equal("2 GB freed on the system drive", freed.Str("title"));
        Assert.Matches(@"^Free space on drive [A-Z]: was 10 GB before and is 12 GB now\.$", freed.Str("detail"));
        Assert.EndsWith(" is 10 GB, as before.", Assert.Single(_h.Findings("DISK-NOTHING-TO-FREE")).Str("detail"), StringComparison.Ordinal);
        Assert.StartsWith("See the technical details for the step that stopped. Free space on drive ", Assert.Single(_h.Findings("DISK-CLEANUP-INCOMPLETE")).Str("detail"), StringComparison.Ordinal);
        Assert.Contains(_h.Records("progress"), r => r.Str("text") == "The servicing stack is still busy after the wait.");
    }

    [Fact]
    public void A_size_under_one_GB_is_said_in_MB()
    {
        Assert.Equal("350 MB", FreeDiskSpaceHandler.Size(350L * 1024 * 1024));
        Assert.Equal("1 GB", FreeDiskSpaceHandler.Size(GB));
        Assert.Equal("12.4 GB", FreeDiskSpaceHandler.Size(12 * GB + 400L * 1024 * 1024));

        var handler = new FreeDiskSpaceHandler();
        _h.FreeBytes.Enqueue(10 * GB); _h.FreeBytes.Enqueue(10 * GB + 350L * 1024 * 1024);
        Assert.Equal(0, handler.Run(Run60()));
        var freed = Assert.Single(_h.Findings("DISK-FREED"));
        Assert.Equal("350 MB freed on the system drive", freed.Str("title"));
        Assert.EndsWith(" was 10 GB before and is 10.3 GB now.", freed.Str("detail"), StringComparison.Ordinal);
    }

    [Fact]
    public void Skips_a_missing_cache_cmdlet_and_names_a_policy_that_refuses_it()
    {
        var handler = new FreeDiskSpaceHandler();
        _h.Processes.Respond = r => Path.GetFileName(r.FilePath) == "powershell.exe" ? FakeProcessRunner.Exit(4) : FakeProcessRunner.Exit(0);
        _h.FreeBytes.Enqueue(10 * GB); _h.FreeBytes.Enqueue(10 * GB);
        Assert.Equal(69003, handler.Run(Run60()));
        Assert.Contains(_h.Records("progress"), r => r.Str("text") == "Delivery Optimization has no cache cmdlet on this device; skipped.");
        _h.Processes.Respond = r => Path.GetFileName(r.FilePath) == "powershell.exe"
            ? FakeProcessRunner.Exit(1, "", "Cannot invoke method. Method invocation is supported only on core types in this language mode.")
            : FakeProcessRunner.Exit(0);
        _h.FreeBytes.Enqueue(10 * GB); _h.FreeBytes.Enqueue(10 * GB);
        Assert.Equal(2, handler.Run(Run60()));
        Assert.Single(_h.Findings("ENGINE-POLICY-BLOCKED"));
        Assert.Single(_h.Findings("DISK-CLEANUP-INCOMPLETE"));
    }
}
