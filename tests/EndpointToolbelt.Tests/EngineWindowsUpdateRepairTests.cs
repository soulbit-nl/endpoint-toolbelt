using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.WindowsUpdate;
using EndpointToolbelt.Platform;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>A clock whose sleep fails, as a mocked Start-Sleep that throws.</summary>
internal sealed class FailingSleepClock : IClock
{
    private readonly IClock _inner;

    public FailingSleepClock(IClock inner) => _inner = inner;

    public DateTime UtcNow => _inner.UtcNow;
    public DateTimeOffset Now => _inner.Now;
    public TimeSpan Elapsed => _inner.Elapsed;
    public void Sleep(TimeSpan duration) => throw new InvalidOperationException("unexpected failure");
}

public class EngineWuCacheResetTests
{
    private readonly EngineHarness _h = new();
    private readonly FakeWuServices _services = FakeWuServices.Standard();
    private readonly List<string> _removed = new();

    public EngineWuCacheResetTests() => _h.Services.ServiceControl = _services;

    /// <summary>A repair on a device nobody else uses, with two items in each cache folder.</summary>
    private TestRepair Repair(int timeLimit = 130, int others = 0, int items = 2) => new(WuTest.Run(_h, timeLimit, others))
    {
        OnOsVersion = () => OsVersionInfo.Read(_h.Registry),
        OnInstallBusy = () => new InstallBusy(),
        OnRemoveOldCacheFolders = () => { },
        OnFolderExists = _ => true,
        OnIsLink = _ => false,
        OnListFolder = path => Enumerable.Range(0, items).Select(i => Path.Combine(path, ((char)('a' + i)).ToString())).ToList(),
        OnDeleteTree = path => _removed.Add(path),
    };

    private static readonly string[] CacheServices = { "wuauserv", "bits", "DoSvc" };

    [Fact]
    public void Clears_only_the_Download_contents_with_Windows_Update_BITS_and_Delivery_Optimization_stopped_and_started_again()
    {
        var r = Repair().CacheReset();
        Assert.True(r.Done);
        Assert.True(r.Changed);
        Assert.Equal(0, r.Warnings);
        foreach (var name in CacheServices)
        {
            Assert.Equal(1, _services.Set[name].Stops);
            Assert.Equal(1, _services.Set[name].Starts);
        }
        Assert.DoesNotContain("cryptsvc", _services.Touched);
        Assert.DoesNotContain("msiserver", _services.Touched);
        Assert.Equal(2, _removed.Count);
        Assert.All(_removed, p => Assert.Contains(@"\SoftwareDistribution\Download\", p, StringComparison.Ordinal));
        Assert.DoesNotContain(_removed, p => p.Contains("catroot2") || p.Contains("DataStore"));
        Assert.Contains(@"SoftwareDistribution\Download", Assert.Single(_h.Records("change")).Str("text"), StringComparison.Ordinal);
        Assert.Empty(_h.Findings("WU-CACHE-RESET-PARTIAL"));
    }

    [Fact]
    public void Clears_the_DataStore_only_when_asked_to()
    {
        var r = Repair(items: 1).CacheReset(true);
        Assert.True(r.Done);
        Assert.Single(_removed, p => p.Contains(@"\SoftwareDistribution\DataStore\"));
        Assert.Single(_removed, p => p.Contains(@"\SoftwareDistribution\Download\"));
        Assert.Single(WuTest.Progress(_h), t => t.Contains("hotfix list"));
    }

    [Fact]
    public void Never_stops_a_service_that_others_depend_on_and_then_deletes_nothing()
    {
        _services.Set["bits"].Dependents.Add(new KeyValuePair<string, string>("SomeAgent", "Running"));
        var repair = Repair();
        repair.OnDeleteTree = _ => throw new InvalidOperationException("must not delete");
        var r = repair.CacheReset();
        Assert.False(r.Done);
        Assert.False(r.Changed);
        // Checked before any stop: none is stopped for nothing.
        foreach (var name in CacheServices)
        {
            Assert.Equal(0, _services.Set[name].Stops);
            Assert.Equal(0, _services.Set[name].Starts);
        }
        Assert.Equal(0, repair.Count("DeleteTree"));
        Assert.False(string.IsNullOrEmpty(_h.Findings("WU-CACHE-RESET-PARTIAL")[0].Str("support")));
    }

    [Fact]
    public void Tries_three_times_for_files_in_use_then_reports_them_and_stops()
    {
        var repair = Repair(items: 1);
        repair.OnDeleteTree = _ => throw new IOException("in use");
        var r = repair.CacheReset(true);
        Assert.False(r.Done);
        Assert.Equal(3, repair.Calls.Count(c => c.StartsWith("ListFolder:", StringComparison.Ordinal) && c.EndsWith(@"\Download", StringComparison.Ordinal)));
        // Download still holds files, so the DataStore is not touched.
        Assert.DoesNotContain(repair.Calls, c => c.StartsWith("ListFolder:", StringComparison.Ordinal) && c.EndsWith(@"\DataStore", StringComparison.Ordinal));
        Assert.Single(_h.Clock.Sleeps, s => s == TimeSpan.FromSeconds(5));
        Assert.Equal(1, _services.Set["wuauserv"].Starts);
        Assert.Single(_h.Findings("WU-CACHE-RESET-PARTIAL"));
        Assert.Single(WuTest.Warnings(_h), t => t.Contains("only partly cleared"));
    }

    [Theory]
    [InlineData(2, "Enterprise", false)]
    [InlineData(-1, "Enterprise", false)]
    [InlineData(0, "ServerRdsh", false)]
    [InlineData(0, "EnterpriseMultiSession", false)]
    [InlineData(0, "ServerDatacenter", true)]
    public void Stops_no_service_on_a_device_that_other_users_share(int others, string edition, bool rdsHost)
    {
        _h.Registry.Put(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", edition);
        var repair = Repair(others: others);
        repair.OnRdsHostRole = () => rdsHost;
        repair.OnInstallBusy = () => throw new InvalidOperationException("must not be asked");
        repair.OnDeleteTree = _ => throw new InvalidOperationException("must not delete");
        var r = repair.CacheReset();
        Assert.True(r.Skipped);
        Assert.False(r.Changed);
        Assert.Equal(0, r.Warnings);
        Assert.Empty(_services.Touched);
        var f = Assert.Single(_h.Findings("WU-CACHE-RESET-SKIPPED"));
        Assert.Equal("INFO", f.Str("severity"));
        Assert.False(string.IsNullOrEmpty(f.Str("support")));
    }

    [Fact]
    public void Stops_no_service_while_an_update_is_being_installed()
    {
        var repair = Repair();
        repair.OnInstallBusy = () => new InstallBusy { Busy = true, Reason = "the update client is installing an update" };
        var r = repair.CacheReset();
        Assert.True(r.Skipped);
        Assert.Equal(1, r.Warnings);
        Assert.Empty(_services.Touched);
        Assert.Single(_h.Findings("WU-CACHE-RESET-BUSY"));
    }

    [Theory]
    [InlineData(false, null, true, 0)]
    [InlineData(true, true, true, 1)]
    [InlineData(true, false, false, 1)]
    [InlineData(true, null, false, 1)]
    public void Reads_an_install_in_progress_from_the_module_installer_and_the_update_client(bool idle, bool? workerBusy, bool busy, int asked)
    {
        var repair = new TestRepair(WuTest.Run(_h, 130))
        {
            OnWaitIdle = () => idle,
            OnWorker = (op, _, _) => op != "Busy" ? throw new InvalidOperationException("only the busy check is expected")
                : workerBusy == null ? new WuWorkerReply { Error = "x" } : new WuWorkerReply { Ok = true, Busy = workerBusy.Value },
        };
        Assert.Equal(busy, repair.InstallBusyCheck().Busy);
        Assert.Equal(asked, repair.Count("RunWorker", "Busy:"));
    }

    [Theory]
    [InlineData(false, 1, true)]
    [InlineData(true, 0, false)]
    public void Stops_a_service_again_once_when_Windows_starts_it_between_the_stop_and_the_delete(bool secondStopFails, int dataStore, bool done)
    {
        if (secondStopFails)
        {
            _services.Set["wuauserv"].OnStop = s =>
            {
                if (s.Stops >= 2) { throw new InvalidOperationException("access denied"); }
                s.Status = "Stopped";
            };
        }
        var repair = Repair(items: 1);
        // Windows starts the update service again while the download cache is deleted.
        repair.OnDeleteTree = path =>
        {
            _removed.Add(path);
            if (path.Contains(@"\Download\")) { _services.Set["wuauserv"].Status = "Running"; }
        };
        var r = repair.CacheReset(true);
        Assert.Equal(done, r.Done);
        Assert.Equal(2, _services.Set["wuauserv"].Stops);
        Assert.Equal(dataStore, _removed.Count(p => p.Contains(@"\DataStore\")));
        // Whatever happened, the services this stopped run again.
        foreach (var name in CacheServices) { Assert.Equal("Running", _services.Set[name].Status); }
        Assert.Equal(done ? 0 : 1, _h.Findings("WU-CACHE-RESET-PARTIAL").Count);
    }

    [Fact]
    public void Stops_neither_BITS_nor_Delivery_Optimization_when_Windows_Update_does_not_stop()
    {
        _services.Set["wuauserv"].OnStop = _ => throw new InvalidOperationException("access denied");
        var repair = Repair();
        repair.OnDeleteTree = _ => throw new InvalidOperationException("must not delete");
        var r = repair.CacheReset();
        Assert.False(r.Done);
        Assert.Equal(1, _services.Set["wuauserv"].Stops);
        Assert.Equal(0, _services.Set["bits"].Stops);
        Assert.Equal(0, _services.Set["DoSvc"].Stops);
        Assert.Single(_h.Findings("WU-CACHE-RESET-PARTIAL"));
    }

    [Fact]
    public void Does_not_start_without_enough_of_the_run_left()
    {
        var repair = Repair(timeLimit: 16);
        repair.OnInstallBusy = () => throw new InvalidOperationException("must not be asked");
        var r = repair.CacheReset();
        Assert.True(r.Skipped);
        Assert.Equal(1, r.Warnings);
        Assert.Empty(_services.Touched);
        Assert.Single(_h.Findings("WU-CACHE-RESET-NO-TIME"));
    }

    [Theory]
    [InlineData("time")]
    [InlineData("throw")]
    public void Starts_the_services_again_when_the_time_runs_out_or_a_step_fails_during_the_deletes(string mode)
    {
        // An unexpected failure after the services were stopped (the pause before the deletes).
        if (mode == "throw") { _h.Services.Clock = new FailingSleepClock(_h.Clock); }
        var repair = Repair();
        repair.OnClearFolder = (_, _, _) => new FolderClearResult { Removed = 3, Remaining = 4, StoppedBy = "time" };
        if (mode == "throw")
        {
            Assert.Contains("unexpected failure", Assert.Throws<InvalidOperationException>(() => repair.CacheReset(true)).Message, StringComparison.Ordinal);
        }
        else
        {
            var r = repair.CacheReset(true);
            Assert.False(r.Done);
            Assert.True(r.Changed);
            // Out of time in Download: the DataStore is not started.
            Assert.Equal(1, repair.Count("ClearFolderContent"));
        }
        foreach (var name in CacheServices)
        {
            Assert.Equal(1, _services.Set[name].Stops);
            Assert.Equal(1, _services.Set[name].Starts);
        }
        if (mode == "time") { Assert.Contains("out of time", _h.Findings("WU-CACHE-RESET-PARTIAL")[0].Str("detail"), StringComparison.Ordinal); }
    }

    [Fact]
    public void Stops_deleting_at_the_deadline_and_before_a_pass_when_a_service_runs_again()
    {
        var repair = new TestRepair(WuTest.Run(_h, 130))
        {
            OnListFolder = _ => new[] { @"X:\D\a", @"X:\D\b" },
            OnDeleteTree = _ => { },
        };
        var now = _h.Clock.Elapsed;
        var late = repair.ClearFolderContent(@"X:\D", now - TimeSpan.FromMinutes(1));
        Assert.Equal("time", late.StoppedBy);
        Assert.Equal(0, late.Removed);
        Assert.Equal(2, late.Remaining);
        var refused = repair.ClearFolderContent(@"X:\D", now + TimeSpan.FromMinutes(10), () => false);
        Assert.Equal("services", refused.StoppedBy);
        Assert.Equal(0, repair.Count("DeleteTree"));
        var ok = repair.ClearFolderContent(@"X:\D", now + TimeSpan.FromMinutes(10));
        Assert.Equal(2, ok.Removed);
        Assert.Equal("", ok.StoppedBy);
    }

    [Theory]
    [InlineData(true, 130, true, 0)]
    [InlineData(false, 130, false, 1)]
    [InlineData(false, 17, false, 1)]
    [InlineData(false, 15, false, 0)]
    public void Does_not_wait_for_the_servicing_stack_again_and_caps_the_busy_check_by_the_time_left(bool remembered, int limit, bool busy, int asked)
    {
        var repair = new TestRepair(WuTest.Run(_h, limit))
        {
            ServicingBusy = remembered,
            OnWaitIdle = () => throw new InvalidOperationException("must not wait again"),
            // The worker was stopped on its time limit.
            OnWorker = (_, _, _) => new WuWorkerReply { TimedOut = true, Error = "stopped after 1 minute" },
        };
        Assert.Equal(busy, repair.InstallBusyCheck().Busy);
        Assert.Equal(asked, repair.Arguments.Count(a => (string)a[0] == "RunWorker" && (string)a[1] == "Busy" && (int)a[3] >= 1 && (int)a[3] <= 2));
        if (asked > 0) { Assert.Single(WuTest.Warnings(_h), t => t.Contains("Could not ask the update client")); }
    }

    [Fact]
    public void Removes_leftover_renamed_folders_from_earlier_versions_without_following_links()
    {
        var repair = new TestRepair(WuTest.Run(_h, 130))
        {
            OnListDirectories = (_, filter) => filter == "SoftwareDistribution.old-*"
                ? new[] { new KeyValuePair<string, string>("SoftwareDistribution.old-20260101-120000", @"X:\W\SoftwareDistribution.old-20260101-120000"), new KeyValuePair<string, string>("SoftwareDistribution.old-mine", @"X:\W\SoftwareDistribution.old-mine") }
                : new[] { new KeyValuePair<string, string>("catroot2.old-20260101-120000", @"X:\W\System32\catroot2.old-20260101-120000") },
            OnDeleteTree = path => _removed.Add(path),
        };
        repair.RemoveOldCacheFolders();
        Assert.Equal(new[] { @"X:\W\SoftwareDistribution.old-20260101-120000", @"X:\W\System32\catroot2.old-20260101-120000" }, _removed);
    }
}

public class EngineWuGhostDeviceTests
{
    private readonly EngineHarness _h = new();
    private List<string> _cbs;

    public EngineWuGhostDeviceTests()
    {
        // CBS writes local time at the start of each line.
        _cbs = new() { Stamp(DateTime.Now.AddDays(-1)) + ", Info CBS Failed to process non-critical driver operations queue [HRESULT = 0x800719e4 - ERROR_LOG_FULL]" };
    }

    private static string Stamp(DateTime local) => local.ToString("yyyy-MM-dd HH:mm:ss", WuTest.Invariant);

    private TestRepair Repair() => new(WuTest.Run(_h, 130)) { OnCbsTail = _ => _cbs };

    [Theory]
    [InlineData("all conditions", 50, "VMBUS", "Net", "netvsc", "queue", true)]
    [InlineData("lowercase instance ID", 50, "vmbus", "Net", "netvsc", "queue", true)]
    [InlineData("PCI with mlx5", 60, "PCI", "Net", "mlx5", "queue", true)]
    [InlineData("PCI with mlx4_bus", 60, "PCI", "Net", "mlx4_bus", "uninstall", true)]
    [InlineData("too few", 49, "VMBUS", "Net", "netvsc", "queue", false)]
    [InlineData("no CBS signature", 80, "VMBUS", "Net", "netvsc", "none", false)]
    [InlineData("phrase without the code", 80, "VMBUS", "Net", "netvsc", "nocode", false)]
    [InlineData("signature before the window", 80, "VMBUS", "Net", "netvsc", "old", false)]
    [InlineData("signature without a timestamp", 80, "VMBUS", "Net", "netvsc", "nostamp", false)]
    [InlineData("other driver", 80, "PCI", "Net", "e1dexpress", "queue", false)]
    [InlineData("class System", 80, "PCI", "System", "netvsc", "queue", false)]
    [InlineData("USB", 80, "USB", "Net", "netvsc", "queue", false)]
    [InlineData("BTH", 80, "BTH", "Net", "netvsc", "queue", false)]
    [InlineData("SWD", 80, "SWD", "Net", "netvsc", "queue", false)]
    [InlineData("ROOT", 80, "ROOT", "Net", "netvsc", "queue", false)]
    public void Allows_the_removal_only_when_every_condition_holds(string name, int count, string bus, string cls, string service, string cbs, bool eligible)
    {
        var stamp = Stamp(DateTime.Now.AddDays(-1));
        var old = Stamp(DateTime.Now.AddDays(-200));
        _cbs = cbs switch
        {
            "queue" => new() { stamp + ", Info CBS Failed to process non-critical driver operations queue [HRESULT = 0x800719e4 - ERROR_LOG_FULL]" },
            "uninstall" => new() { stamp + ", Error CBS Failed uninstalling driver updates [HRESULT = 0x800719E4 - ERROR_LOG_FULL]" },
            "nocode" => new() { stamp + ", Info CBS Failed to process non-critical driver operations queue [HRESULT = 0x80070005]" },
            "old" => new() { old + ", Info CBS Failed to process non-critical driver operations queue [HRESULT = 0x800719e4 - ERROR_LOG_FULL]" },
            "nostamp" => new() { "Info CBS Failed to process non-critical driver operations queue [HRESULT = 0x800719e4 - ERROR_LOG_FULL]" },
            _ => new() { stamp + ", Info CBS Session completed", stamp + ", Error CBS something else 0x800719e4" },
        };
        // The window starts 30 days ago; the "old" line is older.
        var a = Repair().AssessGhosts(RouteDevice.GhostSet(count, bus, cls, service), DateTime.UtcNow.AddDays(-30));
        Assert.True(eligible == a.Eligible, name);
        if (!eligible) { Assert.NotEmpty(a.Reasons); }
    }

    [Fact]
    public void Never_selects_USB_Bluetooth_software_ROOT_or_System_devices_among_removable_ones()
    {
        var devices = RouteDevice.GhostSet(55).Concat(RouteDevice.GhostSet(7, "USB")).Concat(RouteDevice.GhostSet(3, "BTH"))
            .Concat(RouteDevice.GhostSet(2, "SWD")).Concat(RouteDevice.GhostSet(4, "ROOT")).Concat(RouteDevice.GhostSet(5, "PCI", "System")).ToList();
        var a = Repair().AssessGhosts(devices, null);
        Assert.True(a.Eligible);
        Assert.Equal(55, a.Candidates.Count);
        Assert.DoesNotContain(a.Candidates, d => !d.InstanceId.StartsWith(@"VMBUS\", StringComparison.Ordinal) || d.Class != "Net");
        Assert.Equal(55, a.Count("VMBUS"));
        Assert.Equal(7, a.Count("USB"));
        Assert.Equal(5, a.Count("PCI"));
    }

    [Fact]
    public void Handles_devices_without_an_instance_ID_class_or_driver()
    {
        var devices = RouteDevice.GhostSet(50).Concat(new[]
        {
            new PnpDeviceInfo { FriendlyName = "Nothing" },
            new PnpDeviceInfo { InstanceId = @"VMBUS\X\1" },
            new PnpDeviceInfo { InstanceId = @"VMBUS\X\2", Class = "Net" },
        }).ToList();
        var a = Repair().AssessGhosts(devices, null);
        Assert.Equal(50, a.Candidates.Count);
        Assert.Equal(1, a.Count("UNKNOWN"));
        Assert.Equal(52, a.Count("VMBUS"));
    }

    [Fact]
    public void Leaves_a_candidate_alone_that_is_present_again_right_before_the_removal()
    {
        _h.Processes.Result = WuTest.Tool(0);
        var repair = new TestRepair(WuTest.Run(_h, 130))
        {
            OnDeviceNow = id => id == @"VMBUS\DEV\2" ? new PnpDeviceInfo { Present = true } : id == @"VMBUS\DEV\3" ? null : new PnpDeviceInfo { Present = false },
        };
        var r = repair.RemoveGhostDevices(RouteDevice.GhostSet(3));
        Assert.Equal(1, r.Removed);
        Assert.Equal(2, r.Skipped);
        Assert.Contains(@"VMBUS\DEV\1", Assert.Single(_h.Processes.Requests).Arguments, StringComparison.Ordinal);
        Assert.True(repair.RestartOwed);
        Assert.True(repair.ChangedOwed);
    }

    [Theory]
    [InlineData(60, "VMBUS", 60, 0, 1, 1, 0, 0, 1)]
    [InlineData(60, "VMBUS", 0, 0, 1, 0, 0, 0, 0)]
    // Every candidate present again or already gone (pnputil exit 259): nothing changed.
    [InlineData(60, "VMBUS", 0, 60, 1, 0, 0, 1, 0)]
    [InlineData(60, "USB", 0, 0, 0, 0, 1, 0, 0)]
    public void Removes_the_candidates_on_the_route_only_when_eligible_and_otherwise_reports_counts_per_bus(int count, string bus, int removed, int skipped, int removals, int restart, int finding, int noneRemoved, int stamp)
    {
        var device = new RouteDevice { Known = new() { "0x800719e4" }, Ghosts = RouteDevice.GhostSet(count, bus) };
        device.Currency.LastSuccessUtc = DateTime.UtcNow.AddDays(-30);
        device.Component = new ComponentRepairResult { Verdict = "Clean", Completed = true };
        device.WuRun = new WuSearchAndInstall();
        var repair = device.Repair(WuTest.Run(_h, 130));
        // The shipped assessment, on the CBS line of the last day.
        repair.OnAssess = null;
        repair.OnCbsTail = _ => _cbs;
        repair.OnCacheReset = _ => throw new InvalidOperationException("no cache reset on this route");
        repair.OnRemoveGhosts = devices => new GhostRemoval { Removed = removed, Failed = devices.Count - removed - skipped, Skipped = skipped };
        var exit = repair.RepairWU();
        Assert.Equal(removals, repair.Arguments.Count(a => (string)a[0] == "RemoveGhostDevices" && (int)a[1] == 60));
        // A removal that removed nothing needs no restart.
        Assert.Equal(restart, repair.Count("MarkRestartRequired"));
        var f = _h.Findings("WU-GHOST-DEVICES");
        Assert.Equal(finding, f.Count);
        if (finding > 0)
        {
            Assert.Contains(bus + " 60", f[0].Str("detail"), StringComparison.Ordinal);
            Assert.Contains("pnputil", f[0].Str("support"), StringComparison.Ordinal);
        }
        Assert.Equal(stamp, repair.Count("Stamp"));
        var none = _h.Findings("WU-GHOST-NONE-REMOVED");
        Assert.Equal(noneRemoved, none.Count);
        if (noneRemoved > 0)
        {
            Assert.Equal("WARNING", none[0].Str("severity"));
            Assert.Contains("pnputil /enum-devices /disconnected", none[0].Str("support"), StringComparison.Ordinal);
            Assert.Equal(2, exit);
            Assert.Single(WuTest.Progress(_h), t => t == "Completed with 1 warning.");
        }
    }

    [Fact]
    public void Says_the_devices_could_not_be_listed_when_the_device_list_fails()
    {
        _h.WindowsUpdate.DevicesOf = () => throw new InvalidOperationException("no access");
        var repair = new WindowsUpdateRepair(WuTest.Run(_h, 130));
        Assert.Empty(repair.GhostDevices());
        Assert.True(repair.GhostListFailed);
        Assert.Single(WuTest.Warnings(_h), t => t.Contains("Could not list the devices"));
    }

    [Fact]
    public void Lists_only_disconnected_network_and_system_devices()
    {
        _h.WindowsUpdate.DevicesOf = () => new[]
        {
            new PnpDeviceInfo { InstanceId = @"VMBUS\A", Class = "Net", Present = false },
            new PnpDeviceInfo { InstanceId = @"VMBUS\B", Class = "Net", Present = true },
            new PnpDeviceInfo { InstanceId = @"PCI\C", Class = "System", Present = false },
            new PnpDeviceInfo { InstanceId = @"USB\D", Class = "USB", Present = false },
        };
        Assert.Equal(new[] { @"VMBUS\A", @"PCI\C" }, new WindowsUpdateRepair(WuTest.Run(_h, 130)).GhostDevices().Select(d => d.InstanceId));
    }

    [Fact]
    public void Rejects_device_IDs_that_could_break_out_of_the_quoted_argument()
    {
        _h.Processes.Result = WuTest.Tool(0);
        var repair = new TestRepair(WuTest.Run(_h, 130)) { OnDeviceNow = _ => new PnpDeviceInfo { Present = false } };
        var r = repair.RemoveGhostDevices(new[]
        {
            new PnpDeviceInfo { InstanceId = @"ROOT\NET\0001", FriendlyName = "A" },
            new PnpDeviceInfo { InstanceId = @"ROOT\NET\0002\", FriendlyName = "B" },
            new PnpDeviceInfo { InstanceId = @"ROOT\NET\""x", FriendlyName = "C" },
        });
        Assert.Equal(1, r.Removed);
        Assert.Equal(2, r.Failed);
        Assert.Single(_h.Processes.Requests);
    }

    [Fact]
    public void Counts_a_device_pnputil_no_longer_finds_exit_259_as_skipped_not_failed()
    {
        _h.Processes.Respond = q => q.Arguments.Contains(@"DEV\1") ? WuTest.Tool(259) : q.Arguments.Contains(@"DEV\2") ? WuTest.Tool(5) : WuTest.Tool(259, timedOut: true);
        var repair = new TestRepair(WuTest.Run(_h, 130)) { OnDeviceNow = _ => new PnpDeviceInfo { Present = false } };
        var r = repair.RemoveGhostDevices(RouteDevice.GhostSet(3));
        Assert.Equal(0, r.Removed);
        Assert.Equal(1, r.Skipped);
        Assert.Equal(2, r.Failed);
        Assert.False(repair.RestartOwed);
    }
}

public class EngineWuCbsLogTests : IDisposable
{
    private readonly EngineHarness _h = new();
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Reads_only_the_tail_of_a_large_log_and_gives_nothing_for_a_missing_or_locked_file()
    {
        var file = _folder.Combine("CBS.log");
        using (var writer = new StreamWriter(file, false, new UTF8Encoding(false)))
        {
            writer.WriteLine("FIRST LINE OF THE FILE");
            var filler = new string('x', 1000);
            for (var i = 0; i < 9000; i++) { writer.WriteLine(filler); }
            writer.WriteLine("LAST LINE OF THE FILE");
        }
        Assert.True(new FileInfo(file).Length > 8L * 1024 * 1024);
        var repair = new WindowsUpdateRepair(WuTest.Run(_h));
        var lines = repair.ReadCbsTail(file);
        Assert.Equal("LAST LINE OF THE FILE", lines[lines.Count - 1]);
        Assert.DoesNotContain("FIRST LINE OF THE FILE", lines);
        // Every line read is whole: the cut-off first line after the seek is dropped.
        Assert.DoesNotContain(lines, l => l != "LAST LINE OF THE FILE" && l.Length != 1000);
        var missing = _folder.Combine("missing.log");
        Assert.Null(repair.ReadCbsTail(missing));
        Assert.Null(repair.SfcCbs(DateTime.Now, missing));
        using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(repair.ReadCbsTail(file));
        }
    }

    public static IEnumerable<object[]> TailCases => new[]
    {
        new object[] { "repaired", new[] { "Verify complete", @"Repairing corrupted file \??\C:\Windows\System32\a.dll from store", @"Repaired file \SystemRoot\System32\a.dll by copying from backup" }, "Repaired" },
        new object[] { "could not repair", new[] { "Verify complete", "Cannot repair member file [l:5]\"a.dll\" of Contoso-Component in the store, hash mismatch" }, "Unfixed" },
        new object[] { "clean", new[] { "Verifying 100 components", "Beginning Verify and Repair transaction", "Verify complete", "Repairing 0 components", "Repair complete" }, "Clean" },
        new object[] { "no verdict line", new[] { "Verifying 100 components", "Beginning Verify and Repair transaction" }, "Unclear" },
        new object[] { "nothing since the start", Array.Empty<string>(), null },
    };

    [Theory]
    [MemberData(nameof(TailCases))]
    public void Reads_the_SFC_verdict_from_the_SR_lines_written_since_SFC_started_through_the_tail_of_a_synthetic_CBS_log(string name, string[] lines, string verdict)
    {
        var start = new DateTime(2026, 3, 10, 9, 30, 15);
        var file = _folder.Combine("CBS-" + name.Replace(" ", "") + ".log");
        string At(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", WuTest.Invariant);
        using (var writer = new StreamWriter(file, false, new UTF8Encoding(false)))
        {
            // An old SFC run at the start of the file, beyond the tail; filler; then an earlier run
            // inside the tail (one second before the start); then this run.
            writer.WriteLine(At(start.AddDays(-1)) + @", Info                  CSI    00000001 [SR] Repaired file \SystemRoot\far.dll by copying from backup");
            var filler = At(start.AddHours(-1)) + ", Info                  CBS    " + new string('x', 200);
            for (var i = 0; i < 200; i++) { writer.WriteLine(filler); }
            writer.WriteLine(At(start.AddSeconds(-1)) + ", Info                  CSI    00000002 [SR] Cannot repair member file [l:5]\"old.dll\" of Contoso-Old");
            writer.WriteLine(At(start.AddSeconds(-1)) + @", Info                  CSI    00000003 [SR] Repaired file \SystemRoot\old.dll by copying from backup");
            writer.WriteLine("[SR] Cannot repair member file without a timestamp");
            var n = 4;
            foreach (var line in lines)
            {
                // The first line has the start second itself: it counts.
                writer.WriteLine(string.Format(WuTest.Invariant, "{0}, Info                  CSI    {1:x8} [SR] {2}", At(start.AddSeconds(n - 4)), n, line));
                n++;
            }
            writer.WriteLine(At(start.AddMinutes(9)) + ", Info                  CBS    Ending the TrustedInstaller main loop.");
        }
        // A small tail, so the seek path is taken and the first line of the file is outside it.
        var repair = new WindowsUpdateRepair(WuTest.Run(_h)) { CbsTailBytes = 4096 };
        Assert.True(new FileInfo(file).Length > repair.CbsTailBytes);
        // The same offset at the start and now, whatever the time zone of the test machine.
        var r = repair.SfcCbs(start, file, TimeSpan.Zero, TimeSpan.Zero);
        if (verdict == null)
        {
            Assert.Null(r);
        }
        else
        {
            Assert.Equal(verdict, r.Verdict);
            Assert.Equal(lines.Length, r.Lines);
        }
    }

    [Theory]
    // Daylight saving time ended during the run (+02:00 to +01:00): the repair lines written after
    // the change show an earlier local time than the start, and still count.
    [InlineData("DST ended", 2, 1, "Repaired", 4)]
    // Without the change those lines would be before the start: only the verify line counts.
    [InlineData("no change", 2, 2, "Unfixed", 1)]
    // Daylight saving time started (+01:00 to +02:00): later lines only show a later time, so the
    // start is not moved, and the line one second before it does not count.
    [InlineData("DST started", 1, 2, "Unfixed", 1)]
    public void Compares_the_CBS_log_times_with_the_SFC_start_in_UTC_when_the_offset_changed_during_the_run(string name, int begin, int finish, string verdict, int count)
    {
        var start = new DateTime(2026, 10, 25, 2, 30, 0);
        var file = _folder.Combine("CBS-dst-" + name.Replace(" ", "") + ".log");
        string At(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", WuTest.Invariant);
        File.WriteAllLines(file, new[]
        {
            // An earlier run, more than the offset difference before the start: never counts.
            At(start.AddMinutes(-70)) + @", Info                  CSI    00000001 [SR] Could not reproject corrupted file \??\C:\Windows\System32\old.dll; source file in store is also corrupted",
            // One second before the start: counts only when daylight saving time ended.
            At(start.AddSeconds(-1)) + ", Info                  CSI    00000002 [SR] Verifying 100 components",
            // This run, before the change: the verify phase.
            At(start.AddMinutes(10)) + ", Info                  CSI    00000003 [SR] Cannot repair member file [l:16]\"Contoso.Core.dll\" of Contoso-Core, version 10.0.26100.1",
            // After the change at 03:00 local time, which became 02:00: the repair phase.
            At(start.AddMinutes(-25)) + @", Info                  CSI    00000004 [SR] Repairing corrupted file \??\C:\Windows\System32\Contoso.Core.dll from store",
            At(start.AddMinutes(-24)) + @", Info                  CSI    00000005 [SR] Repaired file \SystemRoot\System32\Contoso.Core.dll by copying from backup",
        }, new UTF8Encoding(false));
        var r = new WindowsUpdateRepair(WuTest.Run(_h)).SfcCbs(start, file, TimeSpan.FromHours(begin), TimeSpan.FromHours(finish));
        Assert.True(verdict == r.Verdict, name);
        Assert.Equal(count, r.Lines);
    }
}

public class EngineWuPolicyTests
{
    private const string ServicingKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Servicing";
    private readonly EngineHarness _h = new();

    [Theory]
    [InlineData(true, "", "", 1)]
    [InlineData(true, "RepairContentServerSource", "2", 0)]
    [InlineData(true, "LocalSourcePath", @"wim:X:\sources\install.wim:1", 0)]
    [InlineData(false, "", "", 0)]
    public void Reports_a_missing_repair_source_only_on_a_device_with_a_WSUS_server(bool wsus, string name, string value, int finding)
    {
        if (name == "RepairContentServerSource") { _h.Registry.Put(ServicingKey, name, 2); }
        else if (name.Length > 0) { _h.Registry.Put(ServicingKey, name, value); }
        var repair = new TestRepair(WuTest.Run(_h));
        repair.RepairSourcePolicy(wsus);
        Assert.Equal(1, repair.Count("RegistryValues", ServicingKey));
        var f = _h.Findings("WU-REPAIR-SOURCE-UNSET");
        Assert.Equal(finding, f.Count);
        if (finding > 0)
        {
            Assert.Equal("INFO", f[0].Str("severity"));
            Assert.Contains("0x800f081f", f[0].Str("detail"), StringComparison.Ordinal);
            Assert.Contains("optional component installation and component repair", f[0].Str("support"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Treats_registry_values_that_are_not_numbers_as_not_set()
    {
        Assert.Null(WindowsUpdateRepair.RegistryInt(new Dictionary<string, object> { ["A"] = "abc", ["B"] = "1", ["C"] = new byte[] { 1, 2 } }, "A"));
        Assert.Equal(1, WindowsUpdateRepair.RegistryInt(new Dictionary<string, object> { ["A"] = "abc", ["B"] = "1" }, "B"));
        Assert.Null(WindowsUpdateRepair.RegistryInt(new Dictionary<string, object> { ["C"] = new byte[] { 1, 2 } }, "C"));
        Assert.Null(WindowsUpdateRepair.RegistryInt(new Dictionary<string, object>(), "D"));
        _h.ServiceControl.StartTypes["wlidsvc"] = "Manual";
        var repair = new TestRepair(WuTest.Run(_h))
        {
            OnDefaultService = () => null,
            OnRegistryValues = _ => new Dictionary<string, object> { ["UseWUServer"] = "yes", ["NoAutoUpdate"] = new byte[] { 1 }, ["DisableWindowsUpdateAccess"] = "x" },
        };
        var exception = Record.Exception(() => repair.UpdateSource());
        Assert.Null(exception);
    }

    [Fact]
    public void Links_DisableWindowsUpdateAccess_to_0x8024002F_not_0x8024002E()
    {
        _h.ServiceControl.StartTypes["wlidsvc"] = "Manual";
        var repair = new TestRepair(WuTest.Run(_h))
        {
            OnDefaultService = () => null,
            OnRegistryValues = _ => new Dictionary<string, object> { ["DisableWindowsUpdateAccess"] = 1 },
        };
        repair.UpdateSource();
        var f = Assert.Single(_h.Findings("WU-ACCESS-DISABLED"));
        Assert.Contains("0x8024002F", f.Str("detail"), StringComparison.Ordinal);
        Assert.DoesNotContain("0x8024002E", f.Str("detail"), StringComparison.Ordinal);
    }

    [Fact]
    public void Routes_on_Setup_log_codes_only_when_the_update_client_or_the_history_confirms_them()
    {
        var since = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var repair = new TestRepair(WuTest.Run(_h))
        {
            OnFailureEvents = () => new[]
            {
                new WuFailureEvent { TimeCreatedUtc = since.AddDays(2), Values = new[] { "0x800719E4", "KB5000001" }, Source = "setup" },
                new WuFailureEvent { TimeCreatedUtc = since.AddDays(3), Values = new[] { "0x800F0993", "KB5000001" }, Source = "setup" },
                new WuFailureEvent { TimeCreatedUtc = since.AddDays(4), Values = new[] { "0x80073712", "KB5000001" }, Source = "setup" },
                new WuFailureEvent { TimeCreatedUtc = since.AddDays(5), Values = new[] { "0x80073712" }, Source = "client" },
            },
            OnHistory = _ => new[] { new WuHistoryEntry { DateUtc = since.AddDays(3), Title = "x", ResultCode = 4, Operation = 1, HResult = -2146498157, Client = "x" } },
        };
        var r = repair.FailureCodes(since);
        Assert.Equal(new[] { "0x80073712", "0x800f0993" }, r.Known.OrderBy(c => c, StringComparer.Ordinal));
        Assert.Equal(new[] { "0x800719e4" }, r.Unconfirmed);
    }

    [Fact]
    public void Reads_numeric_error_codes_from_update_client_events()
    {
        _h.WindowsUpdate.EventsOf = log => log == "System"
            ? new[] { new WuRawEvent { TimeCreatedUtc = new DateTime(2026, 10, 1), Properties = new object[] { 2148468767u, 5u, "KB5000001" } } }
            : throw new InvalidOperationException("none");
        var events = new WindowsUpdateRepair(WuTest.Run(_h)).ReadFailureEvents();
        var e = Assert.Single(events);
        Assert.Equal("client", e.Source);
        Assert.Equal(new[] { "0x800f081f", "5", "KB5000001" }, e.Values);
    }

    [Fact]
    public void Does_not_count_a_standalone_servicing_stack_update_as_current()
    {
        var repair = new TestRepair(WuTest.Run(_h))
        {
            OnHistory = _ => new[]
            {
                WuTest.Entry("2026-09-20", "2026-09 Servicing Stack Update for Windows 10", 2),
                WuTest.Entry("2026-08-20", "2026-08 Cumulative Update for Windows 10", 2),
            },
            OnHotFixes = () => Array.Empty<WuHotFix>(),
        };
        var r = repair.Currency(10, new DateTime(2026, 10, 15));
        Assert.False(r.IsCurrent);
        Assert.StartsWith("2026-08 Cumulative", r.LastTitle, StringComparison.Ordinal);
        Assert.True(WindowsUpdateRepair.IsQualityTitle("2026-09 Servicing Stack Update for Windows 10"));
    }
}
