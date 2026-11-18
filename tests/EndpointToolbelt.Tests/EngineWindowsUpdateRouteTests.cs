using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.Actions;
using EndpointToolbelt.Engine.WindowsUpdate;
using EndpointToolbelt.Platform;
using Xunit;

namespace EndpointToolbelt.Tests;

public class EngineWuSearchInstallTests
{
    private readonly EngineHarness _h = new();

    private static WuSearch Searched(bool ok = true, string code = "", string error = "", bool timedOut = false, IEnumerable<WuOffer> install = null, IEnumerable<string> hidden = null) =>
        new() { Ok = ok, ErrorCode = code, Error = error, TimedOut = timedOut, Install = (install ?? Array.Empty<WuOffer>()).ToList(), Hidden = (hidden ?? Array.Empty<string>()).ToList() };

    private static CacheResetResult ResetDone(bool done = true) => new() { Changed = true, Done = done };

    [Fact]
    public void Reads_the_worker_result_line_and_reports_a_stopped_or_broken_worker()
    {
        var repair = new WindowsUpdateRepair(WuTest.Run(_h, 130));
        _h.Processes.Result = WuTest.Tool(0, "noise\r\n" + "{\"ok\":true,\"errorCode\":\"\",\"error\":\"\",\"install\":[{\"id\":\"11111111-2222-3333-4444-555555555555\",\"title\":\"2026-10 Cumulative Update\"}],\"other\":[\"Feature update\"],\"hidden\":[]}");
        var s = repair.Search();
        Assert.True(s.Ok);
        Assert.Equal("11111111-2222-3333-4444-555555555555", Assert.Single(s.Install).Id);
        Assert.Equal(new[] { "Feature update" }, s.Other);
        var request = Assert.Single(_h.Processes.Requests);
        Assert.Equal(repair.WorkerPath, request.FilePath);
        Assert.Equal("--wu-worker Search", request.Arguments);
        Assert.EndsWith("EndpointToolbelt.Engine.exe", request.FilePath, StringComparison.OrdinalIgnoreCase);

        _h.Processes.Result = WuTest.Tool(-2, timedOut: true);
        var t = repair.Search();
        Assert.False(t.Ok);
        Assert.True(t.TimedOut);

        _h.Processes.Result = WuTest.Tool(1, "not json");
        Assert.Equal("the worker returned no result", repair.Search().Error);
        Assert.Throws<InvalidOperationException>(() => repair.RunWorker("Install", "x; calc", 1));
    }

    [Fact]
    public void Gets_one_JSON_line_back_from_the_real_worker_also_when_its_call_is_invalid()
    {
        // An install without an update ID fails before the Windows Update API is touched.
        _h.Services.Processes = new ProcessRunner();
        var r = new WindowsUpdateRepair(WuTest.Run(_h, 130)).RunWorker("Install", "", 2);
        Assert.False(r.Ok);
        Assert.False(r.TimedOut);
        Assert.Equal("Install needs -UpdateId.", r.Error);
    }

    [Fact]
    public void Installs_one_update_at_a_time_and_counts_every_outcome()
    {
        var replies = new Dictionary<string, WuWorkerReply>
        {
            ["11111111-1111-1111-1111-111111111111"] = new() { Ok = true, DownloadResult = 2, DownloadCode = "0x00000000", InstallResult = 2, InstallCode = "0x00000000", RebootRequired = true },
            ["22222222-2222-2222-2222-222222222222"] = new() { Ok = true, DownloadResult = 2, DownloadCode = "0x00000000", InstallResult = 4, InstallCode = "0x800f0922" },
            ["44444444-4444-4444-4444-444444444444"] = new() { TimedOut = true },
            ["55555555-5555-5555-5555-555555555555"] = new() { NotOffered = true, Error = "the update is no longer offered" },
            ["66666666-6666-6666-6666-666666666666"] = new() { Started = false },
            ["77777777-7777-7777-7777-777777777777"] = new() { DownloadResult = 3, DownloadCode = "0x80240022", ErrorCode = "0x80070005", Error = "install threw" },
            ["33333333-3333-3333-3333-333333333333"] = new() { RestartFirst = true },
        };
        var repair = new TestRepair(WuTest.Run(_h, 130))
        {
            OnWorker = (op, id, _) => replies.TryGetValue(id, out var reply) ? reply : throw new InvalidOperationException("must not be asked after a restart-first answer"),
        };
        var updates = new[] { "11111111", "22222222", "44444444", "55555555", "66666666", "77777777", "33333333", "88888888" }
            .Select((p, i) => new WuOffer { Id = string.Join("-", p, p.Substring(0, 4), p.Substring(0, 4), p.Substring(0, 4), p + p.Substring(0, 4)), Title = ((char)('A' + i)).ToString() }).ToList();
        var r = repair.InstallUpdates(updates);
        Assert.Equal(1, r.Installed);
        Assert.Equal(3, r.Failed);
        Assert.Equal(1, r.TimedOut);
        // No longer offered, worker not started, restart first, and the one after it.
        Assert.Equal(4, r.NotAttempted);
        Assert.Equal(1, r.DownloadErrors);
        Assert.True(r.RebootRequired);
        Assert.Equal(new[] { "0x800f0922", "0x80240022", "0x80070005" }, r.Codes);
        Assert.Equal(new[] { "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222", "44444444-4444-4444-4444-444444444444", "77777777-7777-7777-7777-777777777777" }, r.Attempted);
        Assert.Equal(0, repair.Count("RunWorker", "Install:88888888-8888-8888-8888-888888888888"));
    }

    [Fact]
    public void Counts_nothing_as_a_change_when_no_update_was_attempted()
    {
        var repair = new TestRepair(WuTest.Run(_h, 130))
        {
            OnSearch = () => Searched(install: new[] { new WuOffer { Id = "55555555-5555-5555-5555-555555555555", Title = "E" } }),
            OnWorker = (_, _, _) => new WuWorkerReply { NotOffered = true },
            OnWriteHistory = _ => { },
        };
        Assert.False(repair.SearchAndInstall().ChangedDevice);
        Assert.Empty(_h.Findings("WU-INSTALL-FAILED"));
        Assert.Single(_h.Findings("WU-NOT-ATTEMPTED"));
    }

    [Fact]
    public void Reports_a_search_that_never_started_without_a_reset_or_a_change()
    {
        var repair = new TestRepair(WuTest.Run(_h, 14))
        {
            OnWorker = (_, _, _) => throw new InvalidOperationException("must not run"),
            OnCacheReset = _ => throw new InvalidOperationException("must not run"),
        };
        var r = repair.SearchAndInstall(resetOnSearchFailure: true);
        Assert.False(r.ChangedDevice);
        Assert.Equal(1, r.Warnings);
        Assert.Single(_h.Findings("WU-SEARCH-NOT-STARTED"));
        Assert.Empty(_h.Findings("WU-SEARCH-FAILED"));
    }

    [Fact]
    public void Clears_the_download_cache_after_a_failed_search_and_the_DataStore_only_when_the_search_still_fails()
    {
        var repair = new TestRepair(WuTest.Run(_h, 130))
        {
            OnSearch = () => Searched(ok: false, error: "stopped after 30 minutes", timedOut: true),
            OnCacheReset = _ => ResetDone(),
        };
        var r = repair.SearchAndInstall(resetOnSearchFailure: true);
        Assert.True(r.SearchFailed);
        Assert.True(r.ChangedDevice);
        Assert.Equal(2, repair.Count("CacheReset"));
        Assert.Equal(1, repair.Count("CacheReset", "False"));
        Assert.Equal(1, repair.Count("CacheReset", "True"));
        Assert.Equal(3, repair.Count("Search"));
    }

    [Theory]
    [InlineData(true, "0x80244022", true, 1, 2)]
    [InlineData(false, "0x80248007", true, 1, 2)]
    [InlineData(false, "0x80244022", false, 1, 1)]
    public void Includes_the_DataStore_in_the_first_reset_on_evidence_and_searches_no_more_after_a_reset_that_did_not_finish(bool evidence, string code, bool done, int resets, int searches)
    {
        var repair = new TestRepair(WuTest.Run(_h, 130))
        {
            OnSearch = () => Searched(ok: false, code: code, error: "x"),
            OnCacheReset = _ => ResetDone(done),
        };
        repair.SearchAndInstall(resetOnSearchFailure: true, dataStoreEvidence: evidence);
        Assert.Equal(resets, repair.Count("CacheReset"));
        Assert.Equal(done ? 1 : 0, repair.Count("CacheReset", "True"));
        Assert.Equal(searches, repair.Count("Search"));
    }

    [Fact]
    public void Starts_no_install_without_enough_time_left()
    {
        var repair = new TestRepair(WuTest.Run(_h, 40)) { OnWorker = (_, _, _) => throw new InvalidOperationException("must not run") };
        var r = repair.InstallUpdates(new[] { new WuOffer { Id = "11111111-1111-1111-1111-111111111111", Title = "A" } });
        Assert.Equal(1, r.NotAttempted);
        Assert.Empty(r.Attempted);
    }

    [Fact]
    public void Resets_the_cache_only_when_the_search_fails_on_the_nothing_failing_route()
    {
        var calls = 0;
        var repair = new TestRepair(WuTest.Run(_h, 130))
        {
            OnSearch = () => ++calls == 1 ? Searched(ok: false, code: "0x80244022", error: "x") : Searched(hidden: new[] { "Hidden CU" }),
            OnCacheReset = _ => ResetDone(),
        };
        var r = repair.SearchAndInstall(resetOnSearchFailure: true);
        Assert.True(r.NothingOffered);
        Assert.True(r.ChangedDevice);
        Assert.Equal(1, r.HiddenCount);
        Assert.Equal(1, repair.Count("CacheReset", "False"));
        Assert.Single(_h.Findings("WU-HIDDEN-UPDATES"));
    }

    [Fact]
    public void Explains_a_failed_search_and_changes_nothing_without_the_reset_switch()
    {
        var repair = new TestRepair(WuTest.Run(_h, 130))
        {
            OnSearch = () => Searched(ok: false, code: "0x8024002E", error: "x"),
            OnCacheReset = _ => throw new InvalidOperationException("must not run"),
        };
        var r = repair.SearchAndInstall();
        Assert.True(r.SearchFailed);
        Assert.False(r.ChangedDevice);
        Assert.Contains("WU_E_WU_DISABLED", _h.Findings("WU-SEARCH-FAILED")[0].Str("detail"), StringComparison.Ordinal);
    }

    [Fact]
    public void Skips_an_update_the_worker_cannot_be_asked_about_or_that_waits_for_another_installation()
    {
        var repair = new TestRepair(WuTest.Run(_h, 130)) { OnWorker = (_, id, _) => id == "bad" ? throw new InvalidOperationException("invalid update ID") : new WuWorkerReply { Busy = true } };
        var r = repair.InstallUpdates(new[] { new WuOffer { Id = "bad", Title = "A" }, new WuOffer { Id = "11111111-1111-1111-1111-111111111111", Title = "B" } });
        Assert.Equal(2, r.NotAttempted);
        Assert.Empty(r.Attempted);
        Assert.Equal(0, r.Failed);
    }
}

/// <summary>The device state of a route test: two months behind with nothing logged; each test changes what it needs.</summary>
internal sealed class RouteDevice
{
    public bool PendingRestart { get; set; }
    public WuCurrency Currency { get; set; } = new()
    {
        ExpectedMonth = new DateTime(2026, 9, 1),
        LastPatchMonth = new DateTime(2026, 7, 1),
        LastSuccessUtc = new DateTime(2026, 7, 20),
        LastTitle = "2026-07 Cumulative Update",
        MonthsBehind = 2,
        Source = "history",
    };
    public List<string> Known { get; set; } = new();
    public List<string> Unknown { get; set; } = new();
    public List<string> Ignored { get; set; } = new();
    public List<PnpDeviceInfo> Ghosts { get; set; } = new();
    public bool GhostEligible { get; set; }
    public double FreeGB { get; set; } = 100;
    public double FreeAfter { get; set; } = 100;
    public bool UsesWsus { get; set; }
    public bool CooldownOk { get; set; } = true;
    public List<StaleRollup> Stale { get; set; } = new();
    public ComponentRepairResult Component { get; set; } = new() { Verdict = "Clean", Completed = true };
    public WuSearchAndInstall WuRun { get; set; } = new() { Installed = 1, ChangedDevice = true };
    public WuSearch Search { get; set; } = new() { Ok = true };

    public static List<PnpDeviceInfo> GhostSet(int count, string bus = "VMBUS", string cls = "Net", string service = "netvsc") =>
        Enumerable.Range(1, count).Select(i => new PnpDeviceInfo { InstanceId = bus + @"\DEV\" + i, Class = cls, Service = service, FriendlyName = "Adapter" }).ToList();

    /// <summary>The repair with every step of the diagnosis and the repairs answered from this state.</summary>
    public TestRepair Repair(RunContext run)
    {
        var freeCalls = 0;
        return new TestRepair(run)
        {
            OnPendingRestart = () => PendingRestart,
            OnCurrency = _ => Currency,
            OnWriteHistory = _ => { },
            OnSearch = () => Search,
            OnFailureCodes = _ => new WuFailureCodes { Known = Known.ToList(), Unknown = Unknown.ToList(), Ignored = Ignored.ToList() },
            OnGhostDevices = () => Ghosts,
            OnAssess = _ => new GhostAssessment
            {
                Eligible = GhostEligible,
                Candidates = Ghosts.ToList(),
                Signature = GhostEligible,
                Reasons = { "not all conditions hold" },
                ByEnumerator = Ghosts.Count > 0 ? new List<KeyValuePair<string, int>> { new("VMBUS", Ghosts.Count) } : new List<KeyValuePair<string, int>>(),
            },
            OnRepairSource = _ => false,
            OnFreeGB = () => ++freeCalls == 1 ? FreeGB : FreeAfter,
            OnUpdateSource = () => new UpdateSourceState { UsesWsus = UsesWsus },
            OnPolicyState = () => false,
            OnEsp = () => 200,
            OnCooldown = () => CooldownOk,
            OnWaitIdle = () => true,
            OnCacheReset = _ => new CacheResetResult { Changed = true, Done = true },
            OnComponentRepair = () => Component,
            OnRemoveGhosts = devices => new GhostRemoval { Removed = Ghosts.Count },
            OnStale = () => Stale,
            OnRemoveStale = _ => true,
            OnSearchAndInstall = (_, _, _) => WuRun,
        };
    }
}

public class EngineWuRouteTests
{
    private readonly EngineHarness _h = new();
    private readonly RouteDevice _device = new();

    public EngineWuRouteTests()
    {
        // Every DISM or other tool run must be asked for by the test.
        _h.Processes.Result = WuTest.Tool(0);
    }

    private TestRepair Run(out int exit, EngineSettings config = null)
    {
        var run = WuTest.Run(_h, 130);
        if (config != null) { run.Config = config; }
        var repair = _device.Repair(run);
        exit = repair.RepairWU();
        return repair;
    }

    private List<string> Headers() => WuTest.Progress(_h).Where(t => t.StartsWith("== Step ", StringComparison.Ordinal)).ToList();

    [Fact]
    public void Stops_on_a_pending_restart_before_reading_anything()
    {
        _device.PendingRestart = true;
        var repair = Run(out var exit);
        Assert.Equal(69002, exit);
        Assert.Equal(0, repair.Count("Currency"));
        Assert.Single(_h.Findings("WU-RESTART-PENDING"));
    }

    [Fact]
    public void Ends_with_nothing_to_repair_on_a_current_device_without_a_cooldown()
    {
        _device.Currency.IsCurrent = true;
        _device.Currency.MonthsBehind = 0;
        var repair = Run(out var exit);
        Assert.Equal(69003, exit);
        Assert.Equal(0, repair.Count("CooldownAllows"));
        Assert.Equal(0, repair.Count("Stamp"));
        Assert.Equal(0, repair.Count("StampProvisionally"));
        Assert.Equal(0, repair.Count("Search"));
        Assert.Single(_h.Findings("WU-CURRENT"));
    }

    [Fact]
    public void A_current_device_names_its_installed_patch_month_when_it_is_newer_than_the_expected_one()
    {
        _device.Currency = new WuCurrency
        {
            ExpectedMonth = new DateTime(2026, 10, 1),
            LastPatchMonth = new DateTime(2026, 11, 1),
            LastSuccessUtc = new DateTime(2026, 11, 12),
            LastTitle = "2026-11 Cumulative Update",
            MonthsBehind = -1,
            IsCurrent = true,
            Source = "history",
        };
        Run(out var exit);
        Assert.Equal(69003, exit);
        Assert.Equal("Windows is up to date (patch month 2026-11)", Assert.Single(_h.Findings("WU-CURRENT")).Str("title"));
    }

    [Fact]
    public void Checks_a_current_verdict_from_the_hotfix_list_with_a_search_and_reuses_that_search()
    {
        _device.Currency.IsCurrent = true;
        _device.Currency.Source = "hotfix";
        _device.Search.Install.Add(new WuOffer { Id = "11111111-1111-1111-1111-111111111111", Title = "2026-09 Cumulative Update for Windows 11" });
        var repair = Run(out var exit);
        Assert.Equal(0, exit);
        Assert.Equal(1, repair.Count("Search"));
        Assert.Equal(1, repair.Count("SearchAndInstall", "True:True:False"));
        Assert.Empty(_h.Findings("WU-CURRENT"));
    }

    [Fact]
    public void Asks_Windows_Update_first_on_a_device_with_no_quality_update_on_record()
    {
        _device.Currency = new WuCurrency { ExpectedMonth = new DateTime(2026, 9, 1), Source = "none" };
        var repair = Run(out _);
        Assert.Null(Assert.Single(repair.Arguments, a => (string)a[0] == "FailureCodes")[1]);
        Assert.Equal(1, repair.Count("SearchAndInstall", "True:False:False"));
        Assert.Equal(0, repair.Count("ComponentRepair"));
        Assert.Single(_h.Findings("WU-NO-HISTORY"));
    }

    [Fact]
    public void Shows_the_diagnosis_but_repairs_nothing_during_the_cooldown()
    {
        _device.CooldownOk = false;
        _device.Known = new() { "0x800f0993" };
        var repair = Run(out var exit);
        Assert.Equal(69001, exit);
        Assert.Equal(0, repair.Count("CacheReset"));
        Assert.Equal(0, repair.Count("ComponentRepair"));
        Assert.Single(_h.Findings("WU-BEHIND"));
        Assert.Single(_h.Findings("WU-CODE-0x800f0993"));
    }

    [Theory]
    [InlineData("log full, removable ghosts", "0x800719e4", "", 2, true, 100, 0, 0, 0, 1, 1, "")]
    [InlineData("log full, ghosts reported only", "0x800719e4", "", 2, false, 100, 0, 1, 1, 0, 0, "WU-GHOST-DEVICES")]
    [InlineData("log full, no ghosts", "0x800719e4", "", 0, false, 100, 0, 1, 1, 0, 0, "")]
    [InlineData("servicing", "0x800f0993", "", 0, false, 100, 0, 1, 1, 0, 0, "")]
    [InlineData("space code", "0x800f0922", "", 0, false, 100, 1, 0, 1, 0, 0, "")]
    [InlineData("low space", "", "", 0, false, 5, 1, 0, 1, 0, 0, "WU-LOW-SPACE")]
    [InlineData("source missing", "0x800f081f", "", 0, false, 100, 0, 0, 1, 0, 0, "WU-NO-REPAIR-SOURCE")]
    [InlineData("repair download failed", "0x800f0906", "", 0, false, 100, 0, 0, 1, 0, 0, "WU-NO-REPAIR-SOURCE")]
    [InlineData("delivery", "0x80072ee2", "", 0, false, 100, 1, 0, 1, 0, 0, "WU-DELIVERY")]
    [InlineData("unrecognised", "", "0x800736b3", 0, false, 100, 0, 1, 1, 0, 0, "")]
    [InlineData("nothing failing", "", "", 0, false, 100, 0, 0, 1, 0, 0, "")]
    public void Reaches_every_route(string name, string known, string unknown, int ghosts, bool eligible, double freeGB, int reset, int component, int search, int ghost, int restart, string finding)
    {
        _device.Known = known.Length > 0 ? new() { known } : new();
        _device.Unknown = unknown.Length > 0 ? new() { unknown } : new();
        _device.Ghosts = RouteDevice.GhostSet(ghosts);
        _device.GhostEligible = eligible;
        _device.FreeGB = freeGB;
        var repair = Run(out _);
        Assert.True(reset == repair.Count("CacheReset"), name);
        Assert.Equal(component, repair.Count("ComponentRepair"));
        Assert.Equal(search, repair.Count("SearchAndInstall"));
        Assert.Equal(ghost, repair.Count("RemoveGhostDevices"));
        Assert.Equal(restart, repair.Count("MarkRestartRequired"));
        Assert.Equal(1, repair.Count("Stamp"));
        // Stamped before the repair (a stopped run keeps it) and kept: the device was changed.
        Assert.Equal(1, repair.Count("StampProvisionally"));
        Assert.Equal(0, repair.Count("RestoreStamp"));
        if (finding.Length > 0) { Assert.Single(_h.Findings(finding)); }
        if (name == "nothing failing") { Assert.Equal(1, repair.Count("SearchAndInstall", "True:False:False")); }
    }

    [Theory]
    [InlineData("0x800719e4", "", 2, true, 100, "Repair: Remove disconnected network adapters")]
    [InlineData("0x800719e4", "", 0, false, 100, "Repair: Check and repair system files (DISM and SFC)")]
    [InlineData("0x800f0993", "", 0, false, 100, "Repair: Check and repair system files (DISM and SFC)")]
    [InlineData("", "0x800736b3", 0, false, 100, "Repair: Check and repair system files (DISM and SFC)")]
    [InlineData("0x800f0922", "", 0, false, 100, "Repair: Free up space, then look for missing updates")]
    [InlineData("", "", 0, false, 5, "Repair: Free up space, then look for missing updates")]
    [InlineData("0x800f0906", "", 0, false, 100, "Repair: Install the missing updates")]
    [InlineData("0x80072ee2", "", 0, false, 100, "Repair: Clear the update download cache, then look for missing updates")]
    [InlineData("0x8024002f", "", 0, false, 100, "Repair: Report the policy that blocks the update")]
    [InlineData("", "", 0, false, 100, "Repair: Look for missing updates and install them")]
    public void Names_each_repair_route_in_plain_words_in_the_step_titles(string known, string unknown, int ghosts, bool eligible, double freeGB, string title)
    {
        _device.Known = known.Length > 0 ? new() { known } : new();
        _device.Unknown = unknown.Length > 0 ? new() { unknown } : new();
        _device.Ghosts = RouteDevice.GhostSet(ghosts);
        _device.GhostEligible = eligible;
        _device.FreeGB = freeGB;
        Run(out _);
        Assert.Equal(new[] { "== Step 1/3: Check Windows Update (read-only) ==", "== Step 2/3: " + title + " ==", "== Step 3/3: Finish ==" }, Headers());
    }

    [Fact]
    public void Writes_the_step_2_header_in_general_words_when_the_disconnected_device_assessment_fails()
    {
        _device.Known = new() { "0x800719e4" };
        _device.Ghosts = RouteDevice.GhostSet(1);
        var run = WuTest.Run(_h, 130);
        var repair = _device.Repair(run);
        repair.OnAssess = _ => throw new InvalidOperationException("device query failed");
        // The route goes on as for an assessment that does not allow the removal.
        Assert.Equal(2, repair.RepairWU());
        Assert.Equal(new[] { "== Step 1/3: Check Windows Update (read-only) ==", "== Step 2/3: Repair: Fix what the diagnosis found ==", "== Step 3/3: Finish ==" }, Headers());
        Assert.Single(WuTest.Warnings(_h), t => t.Contains("could not be assessed (device query failed)"));
        Assert.Equal(0, repair.Count("RemoveGhostDevices"));
        Assert.Equal(1, repair.Count("ComponentRepair"));
        Assert.Contains("the assessment failed", _h.Findings("WU-GHOST-DEVICES")[0].Str("detail"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0x800f0907", "component repair")]
    [InlineData("0x8024002e", "Do not connect to any Windows Update Internet locations")]
    [InlineData("0x8024002f", "DisableWindowsUpdateAccess")]
    public void Reports_a_policy_code_without_any_repair_when_nothing_else_routes(string known, string policy)
    {
        _device.Known = new() { known };
        var repair = Run(out var exit);
        Assert.Equal(2, exit);
        Assert.Equal(0, repair.Count("ComponentRepair"));
        Assert.Equal(0, repair.Count("CacheReset"));
        Assert.Equal(0, repair.Count("SearchAndInstall"));
        Assert.Empty(_h.Processes.Requests);
        Assert.Equal(0, repair.Count("Stamp"));
        // Nothing changed: the provisional stamp is put back.
        Assert.Equal(1, repair.Count("RestoreStamp"));
        var f = Assert.Single(_h.Findings("WU-POLICY-BLOCKS"));
        Assert.Equal("WARNING", f.Str("severity"));
        Assert.Contains(policy, f.Str("detail"), StringComparison.Ordinal);
        Assert.Equal("Contact your IT support.", f.Str("next"));
        Assert.Contains("policy", f.Str("support"), StringComparison.Ordinal);
        var texts = WuTest.Progress(_h);
        Assert.Single(texts, t => t == "Unrecognised codes: none.");
        Assert.DoesNotContain(texts, t => t.StartsWith("Unrecognised codes: 0x", StringComparison.Ordinal));
        Assert.Single(texts, t => t.StartsWith("Failure codes: ", StringComparison.Ordinal) && t.Contains(known));
    }

    [Fact]
    public void Leaves_a_policy_code_to_the_route_of_another_code_with_the_policy_reported()
    {
        _device.Known = new() { "0x8024002f", "0x80072ee2" };
        var repair = Run(out _);
        Assert.Equal(1, repair.Count("CacheReset"));
        Assert.Equal(0, repair.Count("ComponentRepair"));
        Assert.Single(_h.Findings("WU-POLICY-BLOCKS"));
        Assert.Single(_h.Findings("WU-DELIVERY"));
    }

    [Theory]
    [InlineData(new[] { "0x800f0906" }, 0, 1, "The repair files for the update could not be downloaded")]
    [InlineData(new[] { "0x800f081f" }, 1, 0, "The component store lacks files the update needs")]
    [InlineData(new[] { "0x800f0915" }, 1, 0, "The component store lacks files the update needs")]
    [InlineData(new[] { "0x800f081f", "0x800f0906" }, 1, 1, "The component store lacks files the update needs")]
    public void Words_the_source_missing_diagnosis_by_its_code(string[] known, int store, int download, string title)
    {
        _device.Known = known.ToList();
        Run(out _);
        var texts = WuTest.Progress(_h);
        Assert.Equal(store, texts.Count(t => t.Contains("usually a damaged store, not a connection problem")));
        Assert.Equal(download, texts.Count(t => t.Contains("repair files could not be downloaded (0x800F0906)")));
        var f = _h.Findings("WU-NO-REPAIR-SOURCE")[0];
        Assert.Equal(title, f.Str("title"));
        if (download > 0)
        {
            Assert.Contains("network connection", f.Str("detail"), StringComparison.Ordinal);
            Assert.Contains("Check the network connection", f.Str("next"), StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("network connection", f.Str("detail"), StringComparison.Ordinal);
        }
        foreach (var code in known) { Assert.Contains("0x" + code.Substring(2).ToUpperInvariant(), f.Str("detail"), StringComparison.Ordinal); }
    }

    [Theory]
    [InlineData("0x80072ee2", "0x80248007", 100, 1)]
    [InlineData("0x80072ee2", "", 100, 0)]
    [InlineData("", "0x80248014", 5, 1)]
    [InlineData("", "0x80240438", 5, 0)]
    public void Resets_the_update_database_only_on_a_DataStore_code(string known, string ignored, double freeGB, int dataStore)
    {
        _device.Known = known.Length > 0 ? new() { known } : new();
        _device.Ignored = ignored.Length > 0 ? new() { ignored } : new();
        _device.FreeGB = freeGB;
        var repair = Run(out _);
        Assert.Equal(1, repair.Count("CacheReset"));
        Assert.Equal(dataStore, repair.Count("CacheReset", "True"));
    }

    [Theory]
    [InlineData("0x800f0993", "", false, 0)]
    [InlineData("", "0x800736b3", false, 0)]
    [InlineData("0x800719e4", "", false, 0)]
    [InlineData("", "0x800736b3", true, 1)]
    public void Starts_no_cooldown_after_a_clean_store_check_and_a_search_that_offers_nothing(string known, string unknown, bool changed, int stamps)
    {
        _device.Known = known.Length > 0 ? new() { known } : new();
        _device.Unknown = unknown.Length > 0 ? new() { unknown } : new();
        _device.Component = new ComponentRepairResult { Changed = changed, Verdict = "Clean", Completed = true };
        _device.WuRun = new WuSearchAndInstall { NothingOffered = true };
        var repair = Run(out _);
        Assert.Equal(1, repair.Count("ComponentRepair"));
        Assert.Equal(stamps, repair.Count("Stamp"));
    }

    [Fact]
    public void Passes_DataStore_evidence_to_the_search_on_the_nothing_failing_route()
    {
        _device.Ignored = new() { "0x80248007" };
        var repair = Run(out _);
        Assert.Equal(1, repair.Count("SearchAndInstall", "True:False:True"));
        Assert.Equal(0, repair.Count("CacheReset"));
    }

    [Fact]
    public void Reports_the_repair_source_policy_in_the_diagnosis()
    {
        var run = WuTest.Run(_h, 130);
        var repair = _device.Repair(run);
        repair.OnUpdateSource = () => new UpdateSourceState { UsesWsus = true, WsusPolicy = true };
        repair.RepairWU();
        Assert.Equal(1, repair.Count("RepairSourcePolicy", "True"));
    }

    [Theory]
    [InlineData(false, 0, 1)]
    [InlineData(true, 1, 0)]
    public void Reports_stale_packages_and_removes_them_only_when_the_setting_allows_it(bool allowed, int removals, int finding)
    {
        _device.Known = new() { "0x800f0993" };
        _device.Stale = new() { new StaleRollup { Name = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.1742.1.10", Ubr = 1742, State = 0x40 } };
        var repair = Run(out _, EngineSettings.Resolve(JsonNode.Parse("{\"wuRemoveStalePackages\":" + (allowed ? "true" : "false") + "}")));
        Assert.Equal(removals, repair.Count("RemoveStalePackage"));
        Assert.Equal(finding, _h.Findings("WU-STALE-PACKAGE").Count);
        if (finding > 0) { Assert.Contains("26100.1742", _h.Findings("WU-STALE-PACKAGE")[0].Str("detail"), StringComparison.Ordinal); }
    }

    [Fact]
    public void Installs_nothing_while_space_stays_too_low()
    {
        _device.FreeGB = 5;
        _device.FreeAfter = 6;
        var repair = Run(out var exit);
        Assert.Equal(2, exit);
        Assert.Equal(0, repair.Count("SearchAndInstall"));
        Assert.Single(_h.Findings("WU-SPACE-STILL-LOW"));
    }

    [Fact]
    public void Restarts_instead_of_searching_when_the_store_check_repaired_something()
    {
        _device.Unknown = new() { "0x800736b3" };
        _device.Component = new ComponentRepairResult { RestartNeeded = true, Changed = true, Verdict = "Repairable", Completed = true };
        var repair = Run(out var exit);
        Assert.Equal(0, exit);
        Assert.Equal(0, repair.Count("SearchAndInstall"));
        Assert.Equal(1, repair.Count("MarkRestartRequired", "Repair Windows Update"));
        Assert.Single(_h.Findings("WU-RESTART-REQUIRED"));
    }

    [Fact]
    public void Attempts_nothing_further_when_the_store_check_did_not_complete()
    {
        _device.Unknown = new() { "0x800736b3" };
        _device.Component = new ComponentRepairResult { Warnings = 1, Verdict = "TimedOut", Completed = false };
        var repair = Run(out var exit);
        Assert.Equal(2, exit);
        Assert.Equal(0, repair.Count("SearchAndInstall"));
    }

    [Theory]
    [InlineData("", "WU-NO-DEVICE-FAULT")]
    [InlineData("0x80072ee2", "WU-OFFERED-NOTHING")]
    public void Reports_a_device_that_is_offered_nothing_starts_no_cooldown_and_names_WSUS_when_used(string known, string id)
    {
        _device.Known = known.Length > 0 ? new() { known } : new();
        _device.UsesWsus = true;
        _device.WuRun = new WuSearchAndInstall { NothingOffered = true };
        var repair = Run(out var exit);
        Assert.Equal(2, exit);
        Assert.Contains("WSUS", _h.Findings(id)[0].Str("detail"), StringComparison.Ordinal);
        if (known.Length == 0) { Assert.Equal(0, repair.Count("Stamp")); }
    }

    [Theory]
    [InlineData("0x800719e4", 2, 1)]
    [InlineData("0x800f0993", 0, 0)]
    public void Still_starts_the_cooldown_and_marks_the_restart_when_a_step_fails_after_a_change(string known, int ghosts, int restart)
    {
        _device.Known = new() { known };
        _device.Ghosts = RouteDevice.GhostSet(ghosts);
        _device.GhostEligible = ghosts > 0;
        var repair = _device.Repair(WuTest.Run(_h, 130));
        // Each step marks what it changed before it fails.
        repair.OnRemoveGhosts = _ => { repair.ChangedOwed = true; repair.RestartOwed = true; throw new InvalidOperationException("unexpected failure"); };
        repair.OnComponentRepair = () => { repair.ChangedOwed = true; throw new InvalidOperationException("unexpected failure"); };
        Assert.Contains("unexpected failure", Assert.Throws<InvalidOperationException>(() => repair.RepairWU()).Message, StringComparison.Ordinal);
        Assert.Equal(1, repair.Count("Stamp"));
        Assert.Equal(restart, repair.Count("MarkRestartRequired"));
    }

    [Fact]
    public void Marks_the_restart_when_RestoreHealth_ran_before_a_later_failure()
    {
        _device.Unknown = new() { "0x800736b3" };
        var repair = _device.Repair(WuTest.Run(_h, 130));
        repair.OnComponentRepair = () => { repair.RestartOwed = true; throw new InvalidOperationException("failed after RestoreHealth"); };
        Assert.Throws<InvalidOperationException>(() => repair.RepairWU());
        Assert.Equal(1, repair.Count("MarkRestartRequired"));
    }

    [Fact]
    public void Turns_an_unexpected_failure_into_exit_1_with_the_cooldown_kept_through_the_run_frame()
    {
        using var folder = new TestFolder();
        _device.Known = new() { "0x80072ee2" };
        TestRepair repair = null;
        var handler = new RepairWUHandler
        {
            Create = run =>
            {
                repair = _device.Repair(run);
                repair.OnSearchAndInstall = (_, _, _) => throw new InvalidOperationException("search crashed");
                return repair;
            },
        };
        var file = EngineHarness.WriteRequest(folder, "wu-req.json", "{\"contractVersion\":1,\"runId\":\"wu00001\",\"action\":\"RepairWU\",\"otherSessions\":0,\"timeLimitMinutes\":130,\"config\":{}}");
        var exit = RunFrame.Run("RepairWU", file, new Dictionary<string, IActionHandler> { ["RepairWU"] = handler }, _h.Services);
        Assert.Equal(1, exit);
        Assert.Equal(1, repair.Count("Stamp"));
        Assert.Single(_h.Findings("ENGINE-ERROR"));
    }

    [Theory]
    [InlineData(FindingLevel.Error, "WU-SERVICE-DISABLED-UsoSvc", 2)]
    [InlineData(FindingLevel.Warning, "WU-PAUSED", 2)]
    [InlineData(FindingLevel.Info, "WU-WSUS-POLICY", 0)]
    public void Ends_with_warnings_when_a_finding_still_needs_attention_but_not_for_the_starting_state(FindingLevel level, string id, int expected)
    {
        var run = WuTest.Run(_h, 130);
        var repair = _device.Repair(run);
        repair.OnUpdateSource = () =>
        {
            run.AddFinding(level, id, "x");
            return new UpdateSourceState();
        };
        // Behind, nothing failing, one update installed: the starting state alone gives 0.
        Assert.Equal(expected, repair.RepairWU());
        Assert.Single(_h.Findings("WU-BEHIND"));
    }
}
