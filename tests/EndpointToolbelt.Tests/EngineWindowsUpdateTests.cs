using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Engine.WindowsUpdate;
using EndpointToolbelt.Platform;
using Xunit;

namespace EndpointToolbelt.Tests;

// Repair Windows Update on the compiled engine. Every call that would touch Windows Update, DISM, SFC,
// devices or the registry goes to a fake: these tests never change the machine they run on.

/// <summary>The Windows Update reads, answered by the test.</summary>
internal sealed class FakeWindowsUpdate : IWindowsUpdateSystem
{
    public Func<int, IReadOnlyList<WuHistoryEntry>> HistoryOf { get; set; } = _ => throw new InvalidOperationException("must not read the history");
    public Func<IReadOnlyList<WuHotFix>> HotFixesOf { get; set; } = () => Array.Empty<WuHotFix>();
    public Func<string, IReadOnlyList<WuRawEvent>> EventsOf { get; set; } = _ => Array.Empty<WuRawEvent>();
    public Func<Tuple<string, string>> DefaultServiceOf { get; set; } = () => null;
    public Func<IReadOnlyList<PnpDeviceInfo>> DevicesOf { get; set; } = () => Array.Empty<PnpDeviceInfo>();
    public Func<string, PnpDeviceInfo> DeviceOf { get; set; } = _ => null;
    public Func<EspVolume> EspOf { get; set; } = () => null;

    public IReadOnlyList<WuHistoryEntry> History(int max) => HistoryOf(max);
    public IReadOnlyList<WuHotFix> HotFixes() => HotFixesOf();
    public IReadOnlyList<WuRawEvent> Events(string logName, string provider, IReadOnlyList<int> ids, DateTime sinceUtc, int max) => EventsOf(logName);
    public Tuple<string, string> DefaultService() => DefaultServiceOf();
    public IReadOnlyList<PnpDeviceInfo> Devices() => DevicesOf();
    public PnpDeviceInfo Device(string instanceId) => DeviceOf(instanceId);
    public EspVolume Esp() => EspOf();
}

/// <summary>Service stand-ins for the cache reset: stop and start only count.</summary>
internal sealed class FakeWuService
{
    public string Status { get; set; } = "Running";
    public int Stops { get; set; }
    public int Starts { get; set; }
    public List<KeyValuePair<string, string>> Dependents { get; } = new();

    /// <summary>Called on every stop request; throw to refuse it.</summary>
    public Action<FakeWuService> OnStop { get; set; } = s => s.Status = "Stopped";
}

internal sealed class FakeWuServices : IServiceControl
{
    public Dictionary<string, FakeWuService> Set { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every service touched by name, so a test can show a service that must never be read.</summary>
    public List<string> Touched { get; } = new();

    public static FakeWuServices Standard()
    {
        var services = new FakeWuServices();
        // Includes services the reset must never touch, so a call for them would show.
        foreach (var name in new[] { "wuauserv", "bits", "DoSvc", "cryptsvc", "msiserver" }) { services.Set[name] = new FakeWuService(); }
        return services;
    }

    private FakeWuService Get(string name)
    {
        Touched.Add(name);
        return Set.TryGetValue(name, out var service) ? service : null;
    }

    public bool IsRunning(string name) => Get(name)?.Status == "Running";
    public void Start(string name, TimeSpan wait) => throw new InvalidOperationException("not used");
    public string Status(string name) => Get(name)?.Status;

    public void RequestStop(string name)
    {
        var service = Get(name);
        service.Stops++;
        service.OnStop(service);
    }

    public void RequestStart(string name)
    {
        var service = Get(name);
        service.Starts++;
        service.Status = "Running";
    }

    public void WaitForStatus(string name, string status, TimeSpan wait)
    {
        var service = Get(name);
        if (service.Status != status) { throw new InvalidOperationException("the service is " + service.Status); }
    }

    public string StartType(string name) => Get(name) == null ? throw new InvalidOperationException("Cannot find any service with service name '" + name + "'.") : "Manual";
    public IReadOnlyList<KeyValuePair<string, string>> Dependents(string name) => Get(name).Dependents;
}

/// <summary>
/// The repair with replaceable steps: a step with a function set here answers from it (and is
/// counted); any other step runs as shipped.
/// </summary>
internal sealed class TestRepair : WindowsUpdateRepair
{
    public TestRepair(RunContext run) : base(run) { }

    public List<string> Calls { get; } = new();

    public int Count(string name) => Calls.Count(c => c == name || c.StartsWith(name + ":", StringComparison.Ordinal));

    public int Count(string name, string argument) => Calls.Count(c => c == name + ":" + argument);

    private T Call<T>(string name, Func<T> replaced, Func<T> shipped, string argument = null)
    {
        Calls.Add(argument == null ? name : name + ":" + argument);
        return replaced != null ? replaced() : shipped();
    }

    public Func<int, IReadOnlyList<WuHistoryEntry>> OnHistory { get; set; }
    public Func<IReadOnlyList<WuHotFix>> OnHotFixes { get; set; }
    public Func<int, WuCurrency> OnCurrency { get; set; }
    public Func<IReadOnlyList<WuFailureEvent>> OnFailureEvents { get; set; }
    public Func<DateTime?, WuFailureCodes> OnFailureCodes { get; set; }
    public Func<IReadOnlyList<PnpDeviceInfo>> OnGhostDevices { get; set; }
    public Func<string, IReadOnlyList<string>> OnCbsTail { get; set; }
    public Func<IReadOnlyList<PnpDeviceInfo>, GhostAssessment> OnAssess { get; set; }
    public Func<IReadOnlyList<StaleRollup>> OnStale { get; set; }
    public Func<double?> OnFreeGB { get; set; }
    public Func<double?> OnEsp { get; set; }
    public Func<Tuple<string, string>> OnDefaultService { get; set; }
    public Func<string, IDictionary<string, object>> OnRegistryValues { get; set; }
    public Func<UpdateSourceState> OnUpdateSource { get; set; }
    public Func<bool, bool> OnRepairSource { get; set; }
    public Func<bool> OnPolicyState { get; set; }
    public Action<IReadOnlyList<string>> OnWriteHistory { get; set; }
    public Func<string, string, int, WuWorkerReply> OnWorker { get; set; }
    public Func<WuSearch> OnSearch { get; set; }
    public Func<IReadOnlyList<WuOffer>, WuInstallSet> OnInstall { get; set; }
    public Func<bool, WuSearch, bool, WuSearchAndInstall> OnSearchAndInstall { get; set; }
    public Func<bool> OnWaitIdle { get; set; }
    public Func<OsVersionInfo> OnOsVersion { get; set; }
    public Func<string, IReadOnlyList<string>> OnListFolder { get; set; }
    public Func<string, string, IReadOnlyList<KeyValuePair<string, string>>> OnListDirectories { get; set; }
    public Func<string, bool> OnFolderExists { get; set; }
    public Func<string, bool> OnIsLink { get; set; }
    public Action<string> OnDeleteTree { get; set; }
    public Action OnRemoveOldCacheFolders { get; set; }
    public Func<bool> OnRdsHostRole { get; set; }
    public Func<InstallBusy> OnInstallBusy { get; set; }
    public Func<string, TimeSpan, Func<bool>, FolderClearResult> OnClearFolder { get; set; }
    public Func<bool, CacheResetResult> OnCacheReset { get; set; }
    public Func<ComponentRepairResult> OnComponentRepair { get; set; }
    public Func<string, PnpDeviceInfo> OnDeviceNow { get; set; }
    public Func<IReadOnlyList<PnpDeviceInfo>, GhostRemoval> OnRemoveGhosts { get; set; }
    public Func<string, bool> OnRemoveStale { get; set; }
    public Func<bool> OnPendingRestart { get; set; }
    public Func<bool> OnCooldown { get; set; }

    /// <summary>Arguments of the last call of a step, for the checks on them.</summary>
    public List<object[]> Arguments { get; } = new();

    public override IReadOnlyList<WuHistoryEntry> HistoryEntries(int max) => Call("HistoryEntries", OnHistory == null ? null : () => OnHistory(max), () => base.HistoryEntries(max));
    public override IReadOnlyList<WuHotFix> HotFixEntries() => Call("HotFixEntries", OnHotFixes, () => base.HotFixEntries());
    public override WuCurrency Currency(int deploymentDelayDays, DateTime? now = null) => Call("Currency", OnCurrency == null ? null : () => OnCurrency(deploymentDelayDays), () => base.Currency(deploymentDelayDays, now));
    public override IReadOnlyList<WuFailureEvent> ReadFailureEvents() => Call("ReadFailureEvents", OnFailureEvents, () => base.ReadFailureEvents());

    public override WuFailureCodes FailureCodes(DateTime? since)
    {
        Arguments.Add(new object[] { "FailureCodes", since });
        return Call("FailureCodes", OnFailureCodes == null ? null : () => OnFailureCodes(since), () => base.FailureCodes(since));
    }

    public override IReadOnlyList<PnpDeviceInfo> GhostDevices() => Call("GhostDevices", OnGhostDevices, () => base.GhostDevices());
    public override IReadOnlyList<string> ReadCbsTail(string path = null) => Call("ReadCbsTail", OnCbsTail == null ? null : () => OnCbsTail(path), () => base.ReadCbsTail(path));
    public override GhostAssessment AssessGhosts(IReadOnlyList<PnpDeviceInfo> devices, DateTime? since) => Call("AssessGhosts", OnAssess == null ? null : () => OnAssess(devices), () => base.AssessGhosts(devices, since));
    public override IReadOnlyList<StaleRollup> StaleRollupFixes() => Call("StaleRollupFixes", OnStale, () => base.StaleRollupFixes());
    public override double? SystemDriveFreeGB() => Call("SystemDriveFreeGB", OnFreeGB, () => base.SystemDriveFreeGB());
    public override double? EspFreeMB() => Call("EspFreeMB", OnEsp, () => base.EspFreeMB());
    public override Tuple<string, string> DefaultService() => Call("DefaultService", OnDefaultService, () => base.DefaultService());
    public override IDictionary<string, object> RegistryValues(string subKey) => Call("RegistryValues", OnRegistryValues == null ? null : () => OnRegistryValues(subKey), () => base.RegistryValues(subKey), subKey);
    public override UpdateSourceState UpdateSource() => Call("UpdateSource", OnUpdateSource, () => base.UpdateSource());
    public override bool RepairSourcePolicy(bool wsusPolicy) => Call("RepairSourcePolicy", OnRepairSource == null ? null : () => OnRepairSource(wsusPolicy), () => base.RepairSourcePolicy(wsusPolicy), wsusPolicy.ToString());
    public override bool PolicyState() => Call("PolicyState", OnPolicyState, () => base.PolicyState());

    public override void WriteHistory(IReadOnlyList<string> updateIds = null, int newest = 6, string heading = "Recent update history")
    {
        Call<object>("WriteHistory", OnWriteHistory == null ? null : () => { OnWriteHistory(updateIds); return null; }, () => { base.WriteHistory(updateIds, newest, heading); return null; });
    }

    public override WuWorkerReply RunWorker(string operation, string updateId, int timeoutMinutes)
    {
        Arguments.Add(new object[] { "RunWorker", operation, updateId, timeoutMinutes });
        return Call("RunWorker", OnWorker == null ? null : () => OnWorker(operation, updateId, timeoutMinutes), () => base.RunWorker(operation, updateId, timeoutMinutes), operation + ":" + updateId);
    }

    public override WuSearch Search() => Call("Search", OnSearch, () => base.Search());
    public override WuInstallSet InstallUpdates(IReadOnlyList<WuOffer> updates) => Call("InstallUpdates", OnInstall == null ? null : () => OnInstall(updates), () => base.InstallUpdates(updates));

    public override WuSearchAndInstall SearchAndInstall(bool resetOnSearchFailure = false, WuSearch searchResult = null, bool dataStoreEvidence = false)
    {
        Arguments.Add(new object[] { "SearchAndInstall", resetOnSearchFailure, searchResult, dataStoreEvidence });
        return Call("SearchAndInstall", OnSearchAndInstall == null ? null : () => OnSearchAndInstall(resetOnSearchFailure, searchResult, dataStoreEvidence),
            () => base.SearchAndInstall(resetOnSearchFailure, searchResult, dataStoreEvidence), string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2}", resetOnSearchFailure, searchResult != null, dataStoreEvidence));
    }

    public override bool WaitServicingIdle() => Call("WaitServicingIdle", OnWaitIdle, () => base.WaitServicingIdle());
    public override OsVersionInfo OsVersion() => Call("OsVersion", OnOsVersion, () => base.OsVersion());
    public override IReadOnlyList<string> ListFolder(string path) => Call("ListFolder", OnListFolder == null ? null : () => OnListFolder(path), () => base.ListFolder(path), path);
    public override IReadOnlyList<KeyValuePair<string, string>> ListDirectories(string parent, string filter) => Call("ListDirectories", OnListDirectories == null ? null : () => OnListDirectories(parent, filter), () => base.ListDirectories(parent, filter), filter);
    public override bool FolderExists(string path) => Call("FolderExists", OnFolderExists == null ? null : () => OnFolderExists(path), () => base.FolderExists(path));
    public override bool IsLink(string path) => Call("IsLink", OnIsLink == null ? null : () => OnIsLink(path), () => base.IsLink(path));

    public override void DeleteTree(string path)
    {
        Call<object>("DeleteTree", OnDeleteTree == null ? null : () => { OnDeleteTree(path); return null; }, () => { base.DeleteTree(path); return null; }, path);
    }

    public override void RemoveOldCacheFolders()
    {
        Call<object>("RemoveOldCacheFolders", OnRemoveOldCacheFolders == null ? null : () => { OnRemoveOldCacheFolders(); return null; }, () => { base.RemoveOldCacheFolders(); return null; });
    }

    public override bool RdsHostRole() => Call("RdsHostRole", OnRdsHostRole, () => base.RdsHostRole());
    public override InstallBusy InstallBusyCheck() => Call("InstallBusyCheck", OnInstallBusy, () => base.InstallBusyCheck());
    public override FolderClearResult ClearFolderContent(string path, TimeSpan deadline, Func<bool> beforePass = null) => Call("ClearFolderContent", OnClearFolder == null ? null : () => OnClearFolder(path, deadline, beforePass), () => base.ClearFolderContent(path, deadline, beforePass));
    public override CacheResetResult CacheReset(bool includeDataStore = false) => Call("CacheReset", OnCacheReset == null ? null : () => OnCacheReset(includeDataStore), () => base.CacheReset(includeDataStore), includeDataStore.ToString());
    public override ComponentRepairResult ComponentRepair() => Call("ComponentRepair", OnComponentRepair, () => base.ComponentRepair());
    public override PnpDeviceInfo DeviceNow(string instanceId) => Call("DeviceNow", OnDeviceNow == null ? null : () => OnDeviceNow(instanceId), () => base.DeviceNow(instanceId));

    public override GhostRemoval RemoveGhostDevices(IReadOnlyList<PnpDeviceInfo> devices)
    {
        Arguments.Add(new object[] { "RemoveGhostDevices", devices.Count });
        return Call("RemoveGhostDevices", OnRemoveGhosts == null ? null : () => OnRemoveGhosts(devices), () => base.RemoveGhostDevices(devices));
    }

    public override bool RemoveStalePackage(string name) => Call("RemoveStalePackage", OnRemoveStale == null ? null : () => OnRemoveStale(name), () => base.RemoveStalePackage(name));
    public override bool PendingRestart() => Call("PendingRestart", OnPendingRestart, () => base.PendingRestart());
    public override bool CooldownAllows(int hours) => Call("CooldownAllows", OnCooldown, () => base.CooldownAllows(hours));
    public override string StampProvisionally() => Call<string>("StampProvisionally", () => null, () => null);
    public override void Stamp() => Call<object>("Stamp", () => null, () => null);
    public override void RestoreStamp(string previous) => Call<object>("RestoreStamp", () => null, () => null);
    public override void MarkRestartRequired(string causedBy) => Call<object>("MarkRestartRequired", () => null, () => null, causedBy);
}

internal static class WuTest
{
    public static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>A run with the service's time limit, as New-EngineContext -TimeLimitMinutes made one.</summary>
    public static RunContext Run(EngineHarness h, int timeLimitMinutes = 0, int otherSessions = -1)
    {
        var run = h.NewRun("wu00001", "RepairWU");
        run.TimeLimitMinutes = timeLimitMinutes;
        run.OtherSessions = otherSessions;
        return run;
    }

    public static ProcessResult Tool(int exit, string output = "", bool timedOut = false, bool started = true) =>
        new() { ExitCode = exit, Output = output, TimedOut = timedOut, Started = started };

    public static WuHistoryEntry Entry(string date, string title, int result, long hresult = 0, string updateId = "") =>
        new() { DateUtc = DateTime.SpecifyKind(DateTime.Parse(date, Invariant), DateTimeKind.Utc), Title = title, ResultCode = result, Operation = 1, HResult = hresult, Client = "x", UpdateId = updateId };

    public static List<string> Progress(EngineHarness h) => h.Records("progress").Select(r => r.Str("text")).ToList();

    public static List<string> Warnings(EngineHarness h) => h.Records("progress").Where(r => r.Str("severity") == "warning").Select(r => r.Str("text")).ToList();
}

public class EngineWuCodeKnowledgeTests
{
    private readonly EngineHarness _h = new();

    [Fact]
    public void Has_well_formed_entries_and_explains_every_code_that_routes()
    {
        Assert.All(CodeKnowledge.Table, e => Assert.Matches("^0x[0-9a-f]{8}$", e.Key));
        Assert.All(WuCodes.KnownCodes, c => Assert.True(CodeKnowledge.Table.ContainsKey(c), c));
    }

    [Fact]
    public void Keeps_administrator_work_out_of_the_user_next_step()
    {
        var stale = CodeKnowledge.Get("0x800f0993");
        Assert.Contains("contact your IT support", stale.NextStep, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WURemoveStalePackages", stale.NextStep, StringComparison.Ordinal);
        Assert.Contains("WURemoveStalePackages", stale.Support, StringComparison.Ordinal);
        Assert.Contains("policy", CodeKnowledge.Get("0x8024002e").Support, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("component repair", CodeKnowledge.Get("0x800f081f").Support, StringComparison.OrdinalIgnoreCase);
        // An entry without a support step gives "".
        Assert.Equal("", CodeKnowledge.Get("0x801901ad").Support);
        // No user next step of a Windows Update entry asks for policy, ring or WSUS work.
        foreach (var code in new[] { "0x800f0993", "0x80070422", "0x8024002e", "0x80244007", "0x80244018", "0x80240022", "0xc1900101" })
        {
            Assert.DoesNotMatch("(?i)policy|ring|WSUS|DISM|exclusion", CodeKnowledge.Get(code).NextStep);
        }
    }

    [Fact]
    public void Explains_the_repair_source_policy_and_plain_DISM_codes()
    {
        Assert.Equal("CBS_E_DOWNLOAD_FAILURE", CodeKnowledge.Get("0x800F0906").Name);
        Assert.Contains("network connection", CodeKnowledge.Get("0x800F0906").NextStep, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("component repair", CodeKnowledge.Get("0x800F0906").Support, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("CBS_E_GROUPPOLICY_DISALLOWED", CodeKnowledge.Get("0x800F0907").Name);
        Assert.Contains("repair source", CodeKnowledge.Get("0x800F0907").Support, StringComparison.OrdinalIgnoreCase);
        // 0x800F0906 is on the source-missing route; the policy codes are their own group.
        Assert.Contains("0x800f0906", WuCodes.SourceCodes);
        Assert.Equal(new[] { "0x800f0906" }, WuCodes.SourceDownloadCodes);
        Assert.Contains("unmanaged", CodeKnowledge.Get("0x8024002E").Meaning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DisableWindowsUpdateAccess", CodeKnowledge.Get("0x8024002E").Support, StringComparison.Ordinal);
        Assert.Equal("WU_E_CALL_CANCELLED_BY_POLICY", CodeKnowledge.Get("0x8024002F").Name);
        Assert.Contains("DisableWindowsUpdateAccess", CodeKnowledge.Get("0x8024002F").Support, StringComparison.Ordinal);
        foreach (var code in new[] { "0x800f0906", "0x800f0907", "0x8024002f" })
        {
            Assert.DoesNotMatch("(?i)policy|ring|WSUS|DISM|exclusion", CodeKnowledge.Get(code).NextStep);
        }
    }

    [Fact]
    public void Routes_the_common_errors_page_codes_by_the_cause_Microsoft_names()
    {
        // Component store damage takes the system file repair; a network path problem never does.
        foreach (var code in new[] { "0x800f0831", "0x80073701", "0x8007371b", "0x80070570", "0x800f0825" })
        {
            Assert.Contains(code, WuCodes.ServicingCodes);
            Assert.DoesNotContain(code, WuCodes.DeliveryCodes);
        }
        foreach (var code in new[] { "0x80072efd", "0x80072efe", "0x80d02002", "0x8024401b", "0x80072f8f", "0x80200053" })
        {
            Assert.Contains(code, WuCodes.DeliveryCodes);
            Assert.DoesNotContain(code, WuCodes.ServicingCodes);
        }
        // No code is in two route groups.
        var sets = new[] { WuCodes.ServicingCodes, WuCodes.TxLogCodes, WuCodes.SpaceCodes, WuCodes.SourceCodes, WuCodes.DeliveryCodes, WuCodes.PolicyCodes, WuCodes.NoiseCodes };
        var all = sets.SelectMany(s => s).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        // Explained only (no route): their causes need IT, a restart or nothing.
        foreach (var code in new[] { "0x800f0821", "0x80242006", "0x80242014", "0x80246017", "0x8024000e", "0x8024a10a", "0x8024d009" })
        {
            Assert.NotNull(CodeKnowledge.Get(code));
            Assert.DoesNotContain(code, WuCodes.KnownCodes);
        }
        // 0x800F0922 keeps the space route and names the licensing token fix for IT, never for the user.
        Assert.Contains("0x800f0922", WuCodes.SpaceCodes);
        Assert.Contains(@"System32\spp", CodeKnowledge.Get("0x800f0922").Support, StringComparison.Ordinal);
        Assert.DoesNotContain("spp", CodeKnowledge.Get("0x800f0922").NextStep, StringComparison.Ordinal);
    }

    [Fact]
    public void Groups_the_policy_codes_apart_from_every_repair_route_each_with_the_policy_it_names()
    {
        Assert.Equal(new[] { "0x800f0907", "0x8024002e", "0x8024002f" }, WuCodes.PolicyCodes.OrderBy(c => c, StringComparer.Ordinal));
        foreach (var code in WuCodes.PolicyCodes)
        {
            Assert.Contains(code, WuCodes.KnownCodes);
            foreach (var set in new[] { WuCodes.ServicingCodes, WuCodes.TxLogCodes, WuCodes.SpaceCodes, WuCodes.SourceCodes, WuCodes.DeliveryCodes, WuCodes.NoiseCodes })
            {
                Assert.DoesNotContain(code, set);
            }
            Assert.False(string.IsNullOrEmpty(WuCodes.PolicyNames[code]));
            Assert.False(string.IsNullOrEmpty(CodeKnowledge.Get(code).Support));
        }
        Assert.Contains("DisableWindowsUpdateAccess", WuCodes.PolicyNames["0x8024002f"], StringComparison.Ordinal);
        Assert.Contains("component repair", WuCodes.PolicyNames["0x800f0907"], StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_the_plain_DISM_exits_out_of_the_shared_code_table()
    {
        // An app installer's exit 87 or 1726 must never read as a DISM failure.
        foreach (var code in new[] { "87", "740", "1726", "0x00000057", "0x000002e4", "0x000006be" })
        {
            Assert.Null(CodeKnowledge.Get(code));
            Assert.Equal("", CodeKnowledge.Format(code));
        }
        Assert.Equal("ERROR_INVALID_PARAMETER", WindowsUpdateRepair.DismExitKnowledge(87).Name);
        Assert.Contains("defect", WindowsUpdateRepair.DismExitKnowledge(740).Support, StringComparison.Ordinal);
        Assert.Contains("Restart", WindowsUpdateRepair.DismExitKnowledge(1726).NextStep, StringComparison.Ordinal);
        // An HRESULT exit comes from the shared table.
        Assert.Equal("CBS_E_SOURCE_MISSING", WindowsUpdateRepair.DismExitKnowledge(-2146498529).Name);
        Assert.Null(WindowsUpdateRepair.DismExitKnowledge(5));
        foreach (var entry in WuCodes.DismExitKnowledge)
        {
            Assert.Matches("^0x[0-9a-f]{8}$", entry.Key);
            Assert.InRange(entry.Value.Length, 3, 4);
            Assert.DoesNotMatch("(?i)policy|ring|WSUS|DISM|exclusion", entry.Value[2]);
        }
    }

    [Fact]
    public void Describes_0x800719e4_with_both_documented_causes()
    {
        var k = CodeKnowledge.Get("0x800719E4");
        Assert.Contains("transaction log", k.Meaning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("driver operations", k.Meaning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deallocated", k.Meaning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("documents no", k.Meaning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pnputil", k.Support, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Passes_the_support_step_of_a_code_to_its_finding()
    {
        new WindowsUpdateRepair(WuTest.Run(_h)).AddCodeFindings(new[] { "0x800f0993", "0x12345678" });
        Assert.Contains("WURemoveStalePackages", _h.Findings("WU-CODE-0x800f0993")[0].Str("support"), StringComparison.Ordinal);
        Assert.Contains("contact your IT support", _h.Findings("WU-CODE-0x800f0993")[0].Str("next"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CBS log", _h.Findings("WU-CODE-0x12345678")[0].Str("support"), StringComparison.Ordinal);
    }
}

public class EngineWuBudgetTests
{
    private readonly EngineHarness _h = new();

    [Fact]
    public void Plans_steps_within_the_time_limit_the_service_gave()
    {
        Assert.Equal(120, WuTest.Run(_h, 130).MinutesLeft());
        Assert.Equal(10, WuTest.Run(_h, 20).StepLimit(30, "X"));
        Assert.Equal(0, WuTest.Run(_h, 14).StepLimit(30, "X"));
        Assert.Equal(110, WuTest.Run(_h).MinutesLeft());
    }

    [Fact]
    public void Captures_output_through_pipes_and_stops_a_tool_on_its_time_limit()
    {
        var runner = new ProcessRunner();
        var cmd = ProcessRunner.SystemTool("cmd.exe");
        var ok = runner.Run(new ProcessRequest { FilePath = cmd, Arguments = "/c echo captured", Label = "echo", TimeoutMinutes = 1 }, (_, _) => { });
        Assert.True(ok.Started);
        Assert.Equal(0, ok.ExitCode);
        Assert.Contains("captured", ok.Output, StringComparison.Ordinal);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var slow = runner.Run(new ProcessRequest { FilePath = cmd, Arguments = "/c ping -n 30 127.0.0.1", Label = "slow", TimeoutMinutes = 0.05 }, (_, _) => { });
        Assert.True(slow.TimedOut);
        Assert.True(clock.Elapsed.TotalSeconds < 25);
        var missing = runner.Run(new ProcessRequest { FilePath = ProcessRunner.SystemTool("does-not-exist.exe"), Label = "missing", TimeoutMinutes = 1 }, (_, _) => { });
        Assert.False(missing.Started);
    }

    [Fact]
    public void Keeps_standard_error_apart_from_the_output_results_are_read_from()
    {
        var r = new ProcessRunner().Run(new ProcessRequest { FilePath = ProcessRunner.SystemTool("cmd.exe"), Arguments = "/c \"echo result& echo {err} 1>&2& exit 3\"", Label = "mixed", TimeoutMinutes = 1 }, (_, _) => { });
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("result", r.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("err", r.Output, StringComparison.Ordinal);
        Assert.Contains("{err}", r.ErrorOutput, StringComparison.Ordinal);
    }
}

public class EngineWuDiagnosisTests
{
    private readonly EngineHarness _h = new();

    [Fact]
    public void Computes_Patch_Tuesday()
    {
        Assert.Equal(13, WindowsUpdateRepair.PatchTuesday(2026, 10).Day);
        Assert.Equal(8, WindowsUpdateRepair.PatchTuesday(2026, 9).Day);
    }

    [Theory]
    [InlineData("2026-09 Cumulative Update for Windows 11", "2026-10-02", true, 0)]
    [InlineData("2026-08 Cumulative Update for Windows 11", "2026-09-20", false, 1)]
    [InlineData("2026-10 Cumulative Update for .NET Framework", "2026-10-14", false, null)]
    public void Reads_currency_from_the_history_title_not_from_the_install_date_or_other_products(string title, string date, bool current, int? behind)
    {
        var repair = new TestRepair(WuTest.Run(_h))
        {
            OnHistory = _ => new[] { WuTest.Entry(date, title, 2), WuTest.Entry("2026-10-14", "2026-10 Cumulative Update for Windows 11", 4) },
            OnHotFixes = () => Array.Empty<WuHotFix>(),
        };
        var r = repair.Currency(10, new DateTime(2026, 10, 15));
        Assert.Equal(new DateTime(2026, 9, 1), r.ExpectedMonth);
        Assert.Equal(current, r.IsCurrent);
        Assert.Equal(behind, r.MonthsBehind);
    }

    [Fact]
    public void Expects_the_current_month_once_the_deployment_delay_has_passed()
    {
        var repair = new TestRepair(WuTest.Run(_h)) { OnHistory = _ => Array.Empty<WuHistoryEntry>(), OnHotFixes = () => Array.Empty<WuHotFix>() };
        var r = repair.Currency(10, new DateTime(2026, 10, 24));
        Assert.Equal(new DateTime(2026, 10, 1), r.ExpectedMonth);
        Assert.False(r.IsCurrent);
        Assert.Equal("none", r.Source);
    }

    [Theory]
    [InlineData("2026-09-20", true)]
    [InlineData("2026-09-05", false)]
    public void Falls_back_to_the_hotfix_list_and_dates_it_by_Patch_Tuesday(string installed, bool current)
    {
        var repair = new TestRepair(WuTest.Run(_h))
        {
            OnHistory = _ => throw new InvalidOperationException("history unavailable"),
            OnHotFixes = () => new[] { new WuHotFix { Id = "KB5000001", InstalledOn = DateTime.Parse(installed, WuTest.Invariant) } },
        };
        var r = repair.Currency(10, new DateTime(2026, 10, 15));
        Assert.Equal("hotfix", r.Source);
        Assert.Equal(current, r.IsCurrent);
    }

    [Fact]
    public void Reads_the_hotfix_dates_as_Get_HotFix_does()
    {
        Assert.Equal(new DateTime(2026, 10, 14), WindowsUpdateSystem.ParseInstalledOn("10/14/2026"));
        Assert.Equal(DateTime.FromFileTime(0x01DC000000000000), WindowsUpdateSystem.ParseInstalledOn("01dc000000000000"));
        Assert.Null(WindowsUpdateSystem.ParseInstalledOn(""));
        Assert.Null(WindowsUpdateSystem.ParseInstalledOn("not a date"));
        _h.WindowsUpdate.HotFixesOf = () => new[]
        {
            new WuHotFix { Id = "KB1", InstalledOn = new DateTime(2026, 9, 1) },
            new WuHotFix { Id = "KB2", InstalledOn = new DateTime(2026, 10, 1) },
            new WuHotFix { Id = "KB3", InstalledOn = null },
            new WuHotFix { Id = "Update", InstalledOn = new DateTime(2026, 11, 1) },
        };
        Assert.Equal(new[] { "KB2", "KB1" }, new WindowsUpdateRepair(WuTest.Run(_h)).HotFixEntries().Select(h => h.Id));
    }

    [Fact]
    public void Sorts_failure_codes_into_known_unrecognised_and_not_update_codes()
    {
        var since = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var repair = new TestRepair(WuTest.Run(_h))
        {
            OnFailureEvents = () => new[]
            {
                new WuFailureEvent { TimeCreatedUtc = since.AddDays(5), Values = new[] { "0x800F0993", "KB5000001" } },
                new WuFailureEvent { TimeCreatedUtc = since.AddDays(-5), Values = new[] { "0x800719E4" } },
                new WuFailureEvent { TimeCreatedUtc = since.AddDays(6), Values = new[] { "0x80240438" } },
                new WuFailureEvent { TimeCreatedUtc = since.AddDays(7), Values = new[] { "0x800736B3", "2026-09 Cumulative Update for Windows 11" } },
                new WuFailureEvent { TimeCreatedUtc = since.AddDays(8), Values = new[] { "0x80073D02", "SomeStoreApp" } },
                new WuFailureEvent { TimeCreatedUtc = since.AddDays(9), Values = new[] { "0x00000000" } },
                new WuFailureEvent { TimeCreatedUtc = since.AddDays(10), Values = new[] { "x" }, Message = "failed with error 0x80072EE2" },
            },
        };
        var r = repair.FailureCodes(since);
        Assert.Equal(new[] { "0x800f0993", "0x80072ee2" }, r.Known);
        Assert.Equal(new[] { "0x800736b3" }, r.Unknown);
        Assert.Equal(new[] { "0x80073d02" }, r.Ignored);
    }

    [Fact]
    public void Shows_at_most_four_failure_codes_with_their_meaning_when_known()
    {
        new WindowsUpdateRepair(WuTest.Run(_h)).AddCodeFindings(new[] { "0x800F081F", "0x12345678", "0x800f081f", "0x80070005", "0x80070020", "0x8007000d" });
        Assert.Contains("CBS_E_SOURCE_MISSING", _h.Findings("WU-CODE-0x800f081f")[0].Str("title"), StringComparison.Ordinal);
        Assert.Contains("not a recognised code", _h.Findings("WU-CODE-0x12345678")[0].Str("title"), StringComparison.Ordinal);
        Assert.Equal(4, _h.Records("finding").Count(f => f.Str("id").StartsWith("WU-CODE-", StringComparison.Ordinal)));
        Assert.Single(_h.Findings("WU-CODES-MORE"));
    }

    [Fact]
    public void Finds_stale_baseline_packages_only_below_the_newest_installed_one()
    {
        const string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages\";
        _h.Registry.Put(root + "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.1742.1.10", "CurrentState", 0x40);
        _h.Registry.Put(root + "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4000.1.10", "CurrentState", 0x70);
        _h.Registry.Put(root + "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4100.1.10", "CurrentState", 0x40);
        _h.Registry.Put(root + "Package_for_KB5000001~31bf3856ad364e35~amd64~~1.0", "CurrentState", 0x40);
        var stale = new WindowsUpdateRepair(WuTest.Run(_h)).StaleRollupFixes();
        Assert.Contains("26100.1742", Assert.Single(stale).Name, StringComparison.Ordinal);
    }
}

public class EngineWuComponentStoreTests
{
    private const string Repairable = "The component store is repairable.";
    private const string NoViolations = "Windows Resource Protection did not find any integrity violations.";
    private const string German = "Der Windows-Ressourcenschutz hat die Pruefung abgeschlossen.";
    private readonly EngineHarness _h = new();

    /// <summary>DISM and SFC answer by label; a label without an answer must not run.</summary>
    private void Tools(Dictionary<string, ProcessResult> answers) =>
        _h.Processes.Respond = r => answers.TryGetValue(r.Label, out var answer) ? answer : throw new InvalidOperationException(r.Label + " must not run");

    /// <summary>The repair with CBS.log answering these lines (none unless a test sets them).</summary>
    private TestRepair Repair(IReadOnlyList<string> cbs = null) =>
        new(WuTest.Run(_h, 130)) { OnCbsTail = _ => cbs ?? Array.Empty<string>() };

    /// <summary>A CBS line at this local time (the clock's own time zone), as CBS writes it.</summary>
    private static string Cbs(DateTime local, int n, string text) =>
        string.Format(WuTest.Invariant, "{0}, Info                  CSI    {1:x8} [SR] {2}", local.ToString("yyyy-MM-dd HH:mm:ss", WuTest.Invariant), n, text);

    private DateTime LocalNow => _h.Clock.Now.DateTime;

    private int Changes(string match) => _h.Records("change").Count(c => c.Str("text").Contains(match));

    [Fact]
    public void Stops_after_a_clean_ScanHealth()
    {
        Tools(new() { ["DISM ScanHealth"] = WuTest.Tool(0, "No component store corruption detected.") });
        var r = Repair().ComponentRepair();
        Assert.Equal("Clean", r.Verdict);
        Assert.False(r.RestartNeeded);
        Assert.True(r.Completed);
        Assert.Single(_h.Findings("WU-STORE-CLEAN"));
    }

    [Fact]
    public void Repairs_a_damaged_store_and_reports_the_SFC_repair()
    {
        Tools(new()
        {
            ["DISM ScanHealth"] = WuTest.Tool(0, Repairable),
            ["DISM RestoreHealth"] = WuTest.Tool(0, "The restore operation completed successfully."),
            ["SFC"] = WuTest.Tool(0, "Windows Resource Protection found corrupt files and successfully repaired them."),
        });
        var r = Repair().ComponentRepair();
        Assert.Equal("Repairable", r.Verdict);
        Assert.True(r.RestartNeeded);
        Assert.Equal(0, r.Warnings);
        Assert.Equal(Encoding.Unicode, Assert.Single(_h.Processes.Requests, q => q.Label == "SFC").OutputEncoding);
        var restore = Assert.Single(_h.Processes.Requests, q => q.Label == "DISM RestoreHealth");
        Assert.DoesNotContain("/Source", restore.Arguments, StringComparison.Ordinal);
        Assert.Contains("/English", restore.Arguments, StringComparison.Ordinal);
        Assert.Equal(2, _h.Records("change").Count);
    }

    [Fact]
    public void Treats_a_ScanHealth_that_could_not_start_as_failed_and_still_repairs()
    {
        Tools(new()
        {
            ["DISM ScanHealth"] = WuTest.Tool(-1, started: false),
            ["DISM RestoreHealth"] = WuTest.Tool(0, NoViolations),
            ["SFC"] = WuTest.Tool(0, NoViolations),
        });
        var r = Repair().ComponentRepair();
        Assert.Equal("Failed", r.Verdict);
        Assert.True(r.Completed);
        // RestoreHealth ran (a change) but asked for no restart, and SFC repaired nothing.
        Assert.True(r.Changed);
        Assert.False(r.RestartNeeded);
        Assert.Single(_h.Processes.Requests, q => q.Label == "DISM RestoreHealth");
        Assert.Single(_h.Findings("WU-STORE-SCAN-FAILED"));
    }

    [Theory]
    [InlineData(0, false, "did not find any integrity violations", 0, false, false)]
    [InlineData(3010, false, "did not find any integrity violations", 0, false, true)]
    [InlineData(0, false, "found corrupt files and successfully repaired them", 0, false, true)]
    [InlineData(0, false, "found corrupt files but was unable to fix some of them", 0, false, false)]
    [InlineData(0, false, "", 1, false, false)]
    [InlineData(0, false, "", -2, true, false)]
    [InlineData(0, false, "something else", 0, false, false)]
    [InlineData(-2, true, "", 0, false, false)]
    public void Marks_a_restart_only_for_RestoreHealth_3010_or_an_SFC_repair(int restoreExit, bool restoreTimedOut, string sfc, int sfcExit, bool sfcTimedOut, bool restart)
    {
        Tools(new()
        {
            ["DISM ScanHealth"] = WuTest.Tool(0, Repairable),
            ["DISM RestoreHealth"] = WuTest.Tool(restoreExit, timedOut: restoreTimedOut),
            ["SFC"] = WuTest.Tool(sfcExit, sfc, sfcTimedOut),
        });
        var repair = Repair();
        var r = repair.ComponentRepair();
        Assert.Equal(restart, r.RestartNeeded);
        Assert.Equal(restart, repair.RestartOwed);
        Assert.True(r.Changed);
    }

    public static IEnumerable<object[]> SfcLines => new[]
    {
        new object[] { "repaired", new[] { "Verify complete", @"Repairing corrupted file \??\C:\Windows\System32\a.dll from store", @"Repaired file \SystemRoot\System32\a.dll by copying from backup" }, true, 0, 0 },
        new object[] { "repairing and repaired on one line", new[] { "Repairing 1 components", "Repairing file a.dll ... Repaired" }, true, 0, 0 },
        new object[] { "could not repair", new[] { "Verify complete", "Cannot repair member file [l:5]\"a.dll\" of Contoso-Component, version 1.0 in the store, hash mismatch" }, false, 1, 1 },
        new object[] { "some repaired, some not", new[] { @"Repaired file \SystemRoot\System32\b.dll by copying from backup", "Cannot repair member file [l:5]\"a.dll\" of Contoso-Component" }, true, 1, 1 },
        new object[] { "only verification", new[] { "Verifying 100 components", "Beginning Verify and Repair transaction", "Verify complete", "Repairing 0 components", "Repair complete" }, false, 0, 0 },
        // The verify phase logs every damaged file as "Cannot repair member file", also one the
        // repair phase then restores from the store.
        new object[] { "cannot repair in the verify phase, then repaired", new[]
        {
            "Verifying 100 components", "Beginning Verify and Repair transaction",
            "Cannot repair member file [l:16]\"Contoso.Core.dll\" of Contoso-Core, version 10.0.26100.1, arch amd64, nonSxS, pkt {l:8 b:31bf3856ad364e35}",
            "Verify complete", "Repairing 1 components", "Beginning Verify and Repair transaction",
            @"Repairing corrupted file [ml:520{260},l:84{42}]""\??\C:\Windows\System32\Contoso.Core.dll"" from store",
            @"Repaired file [ml:520{260},l:70{35}]""\SystemRoot\System32\Contoso.Core.dll"" by copying from backup", "Repair complete",
        }, true, 0, 0 },
        new object[] { "store copy damaged as well", new[]
        {
            "Cannot repair member file [l:16]\"Contoso.Core.dll\" of Contoso-Core, version 10.0.26100.1, arch amd64, nonSxS, Type neutral, TypeName neutral, PublicKey neutral in the store, hash mismatch",
            "This component was referenced by [l:40]\"Contoso-Package~31bf3856ad364e35~amd64~~10.0.1\"",
            @"Could not reproject corrupted file [ml:520{260},l:84{42}]""\??\C:\Windows\System32\Contoso.Core.dll""; source file in store is also corrupted", "Repair complete",
        }, false, 1, 1 },
        new object[] { "could not reproject", new[] { "Verify complete", @"Could not reproject corrupted file \??\C:\Windows\System32\Contoso.Core.dll; source file in store is also corrupted" }, false, 1, 1 },
        new object[] { "another file repaired, this one not", new[]
        {
            "Cannot repair member file [l:9]\"other.dll\" of Contoso-Other, version 1.0",
            @"Repairing corrupted file \??\C:\Windows\System32\Contoso.Core.dll from store",
            @"Repaired file \SystemRoot\System32\Contoso.Core.dll by copying from backup",
        }, true, 1, 1 },
    };

    [Theory]
    [MemberData(nameof(SfcLines))]
    public void Reads_the_SFC_verdict_from_the_SR_lines_of_this_run_in_CBS_log_on_any_display_language(string name, string[] lines, bool restart, int unfixed, int warnings)
    {
        var future = LocalNow.AddMinutes(5);
        var past = LocalNow.AddHours(-2);
        // A repair failure of an earlier SFC run must not count.
        var cbs = new List<string>
        {
            Cbs(past, 1, "Cannot repair member file [l:5]\"old.dll\" of Contoso-Old"),
            Cbs(past, 2, @"Repaired file \SystemRoot\old.dll by copying from backup"),
        };
        var n = 3;
        foreach (var line in lines) { cbs.Add(Cbs(future, n++, line)); }
        cbs.Add(future.ToString("yyyy-MM-dd HH:mm:ss", WuTest.Invariant) + ", Info                  CBS    Session: 1_2 finalized.");
        // SFC text in another display language: no English phrase to match.
        Tools(new()
        {
            ["DISM ScanHealth"] = WuTest.Tool(0, Repairable),
            ["DISM RestoreHealth"] = WuTest.Tool(0),
            ["SFC"] = WuTest.Tool(0, German),
        });
        var r = Repair(cbs).ComponentRepair();
        Assert.True(restart == r.RestartNeeded, name);
        Assert.Equal(warnings, r.Warnings);
        Assert.Equal(unfixed, _h.Findings("WU-SFC-UNFIXED").Count);
        Assert.Equal(restart ? 1 : 0, Changes("SFC"));
        Assert.Single(WuTest.Progress(_h), t => t.Contains("read from its"));
    }

    [Fact]
    public void Falls_back_to_the_SFC_text_when_CBS_log_has_no_SR_lines_of_this_run()
    {
        var cbs = new[] { Cbs(LocalNow.AddHours(-2), 1, "Cannot repair member file [l:5]\"old.dll\" of Contoso-Old") };
        Tools(new()
        {
            ["DISM ScanHealth"] = WuTest.Tool(0, Repairable),
            ["DISM RestoreHealth"] = WuTest.Tool(0),
            ["SFC"] = WuTest.Tool(0, NoViolations),
        });
        var r = Repair(cbs).ComponentRepair();
        Assert.False(r.RestartNeeded);
        Assert.Equal(0, r.Warnings);
        Assert.Empty(_h.Findings("WU-SFC-UNFIXED"));
        Assert.Single(WuTest.Progress(_h), t => t.Contains("no integrity violations"));
    }

    [Fact]
    public void Starts_nothing_behind_a_ScanHealth_that_ran_out_of_time()
    {
        Tools(new() { ["DISM ScanHealth"] = WuTest.Tool(-2, timedOut: true) });
        var r = Repair().ComponentRepair();
        Assert.False(r.Completed);
        Assert.Equal("TimedOut", r.Verdict);
        Assert.Single(_h.Findings("WU-STORE-SCAN-TIMEOUT"));
    }

    [Fact]
    public void Explains_a_failed_RestoreHealth_with_the_code_table()
    {
        Tools(new()
        {
            ["DISM ScanHealth"] = WuTest.Tool(0, Repairable),
            ["DISM RestoreHealth"] = WuTest.Tool(-2146498529),
            ["SFC"] = WuTest.Tool(0, NoViolations),
        });
        Assert.Equal(1, Repair().ComponentRepair().Warnings);
        Assert.Matches(@"0x800F081F \(CBS_E_SOURCE_MISSING\)", _h.Findings("WU-STORE-REPAIR-FAILED")[0].Str("detail"));
    }

    [Fact]
    public void Shows_a_plain_DISM_exit_in_the_finding_as_the_log_shows_it_explained_from_the_DISM_table()
    {
        Tools(new()
        {
            ["DISM ScanHealth"] = WuTest.Tool(87),
            ["DISM RestoreHealth"] = WuTest.Tool(1726),
            ["SFC"] = WuTest.Tool(0, NoViolations),
        });
        Repair().ComponentRepair();
        var scan = _h.Findings("WU-STORE-SCAN-FAILED")[0];
        Assert.Matches(@"ended with 87 \(ERROR_INVALID_PARAMETER: ", scan.Str("detail"));
        Assert.DoesNotContain("0x00000057", scan.Str("detail"), StringComparison.Ordinal);
        var restore = _h.Findings("WU-STORE-REPAIR-FAILED")[0];
        Assert.Matches(@"ended with 1726 \(RPC_S_CALL_FAILED\)", restore.Str("detail"));
        Assert.DoesNotContain("0x000006be", restore.Str("detail"), StringComparison.Ordinal);
        Assert.Contains("Restart", restore.Str("next"), StringComparison.Ordinal);
        Assert.Single(WuTest.Progress(_h), t => t == "DISM RestoreHealth failed with 1726.");
    }

    [Theory]
    [InlineData(0, 0, "no integrity violations")]
    [InlineData(1, 1, "did not complete normally")]
    public void Accepts_a_clean_verdict_from_CBS_log_only_when_SFC_exited_with_0(int exit, int warnings, string text)
    {
        var future = LocalNow.AddMinutes(5);
        // Only the last lines of the run are left (the tail lost the rest): they look clean.
        var cbs = new[] { Cbs(future, 1, "Verify complete"), Cbs(future, 2, "Repairing 0 components"), Cbs(future, 3, "Repair complete") };
        Tools(new()
        {
            ["DISM ScanHealth"] = WuTest.Tool(0, Repairable),
            ["DISM RestoreHealth"] = WuTest.Tool(0),
            ["SFC"] = WuTest.Tool(exit, German),
        });
        var r = Repair(cbs).ComponentRepair();
        Assert.Equal(warnings, r.Warnings);
        Assert.False(r.RestartNeeded);
        Assert.Single(WuTest.Progress(_h), t => t.Contains(text));
    }

    [Theory]
    [InlineData("Clean", false, 0, "", "Clean", false, true)]
    [InlineData("Clean", false, 1, "", "Failed", false, true)]
    [InlineData("Clean", false, 1, "Windows Resource Protection found corrupt files and successfully repaired them.", "Repaired", false, true)]
    [InlineData("Clean", false, 0, "Windows Resource Protection found corrupt files but was unable to fix some of them.", "Unfixed", false, true)]
    [InlineData("Repaired", true, 1, "", "Repaired", true, true)]
    [InlineData("Repaired", true, 0, "Windows Resource Protection found corrupt files but was unable to fix some of them.", "Unfixed", true, true)]
    [InlineData("Unfixed", true, 1, "", "Unfixed", true, true)]
    [InlineData("Unclear", false, 0, "something else", "Unclear", false, false)]
    [InlineData("", false, 0, "Windows Resource Protection did not find any integrity violations.", "Clean", false, false)]
    [InlineData("", false, 2, "", "Failed", false, false)]
    public void Combines_the_CBS_log_verdict_with_the_SFC_exit_code_and_text_taking_the_worse(string cbs, bool repaired, int exit, string output, string verdict, bool also, bool fromCbs)
    {
        var cbsResult = cbs.Length == 0 ? null : new SfcCbsVerdict { Lines = 3, Repaired = repaired, Unfixed = cbs == "Unfixed", Verified = true, Verdict = cbs };
        var r = WindowsUpdateRepair.SfcCombined(cbsResult, exit, output);
        Assert.Equal(verdict, r.Verdict);
        Assert.Equal(also, r.AlsoRepaired);
        Assert.Equal(fromCbs, r.FromCbs);
    }

    [Fact]
    public void Owes_no_restart_for_a_RestoreHealth_or_SFC_that_never_started()
    {
        _h.Processes.Respond = r => r.Label == "DISM ScanHealth" ? WuTest.Tool(0, Repairable) : WuTest.Tool(-1, started: false);
        var repair = Repair();
        var result = repair.ComponentRepair();
        Assert.False(result.RestartNeeded);
        Assert.Equal(2, result.Warnings);
        Assert.False(repair.RestartOwed);
        Assert.Single(_h.Findings("WU-STORE-REPAIR-NOT-STARTED"));
        Assert.Empty(_h.Findings("WU-STORE-REPAIR-FAILED"));
    }
}
