using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.Actions;
using EndpointToolbelt.Engine.Ime;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The read-only app diagnosis in the engine: one record per app of the visible scopes, no
/// other user's apps, and no change of any kind on the device. Registry data comes from a fake;
/// nothing here reads this machine's IME state.
/// </summary>
public class EngineAppDiagnosisTests : IDisposable
{
    internal const string Device = AppState.DeviceScopeId;
    internal const string UserOne = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    internal const string UserTwo = "cccccccc-dddd-eeee-ffff-000000000000";
    private const string Packaged = "11111111-2222-3333-4444-555555555555";
    private const string Download = "22222222-3333-4444-5555-666666666666";
    private const string OwnOk = "33333333-4444-5555-6666-777777777777";
    private const string Theirs = "44444444-5555-6666-7777-888888888888";

    // 0x87D1041C as the IME writes it: a signed 32-bit number.
    private const long NotDetectedSigned = AppState.NotDetectedCode - 4294967296L;

    private readonly TestFolder _folder = new();
    private readonly EngineHarness _h = new();

    public void Dispose() => _folder.Dispose();

    internal static string EntraSid(string objectId)
    {
        var b = new Guid(objectId).ToByteArray();
        return string.Format(CultureInfo.InvariantCulture, "S-1-12-1-{0}-{1}-{2}-{3}", BitConverter.ToUInt32(b, 0), BitConverter.ToUInt32(b, 4), BitConverter.ToUInt32(b, 8), BitConverter.ToUInt32(b, 12));
    }

    /// <summary>One app record as the IME keeps it: Win32Apps\&lt;scope&gt;\&lt;app&gt;_&lt;revision&gt;.</summary>
    internal static void AddApp(FakeRegistry registry, string scope, string app, int revision, int intent, int? state, long code = 0, int? compliance = null, string updated = "11/10/2026 13:00:00")
    {
        var key = @"TEST\Win32Apps\" + scope + "\\" + app + "_" + revision.ToString(CultureInfo.InvariantCulture);
        registry.Put(key, "Intent", intent);
        if (updated != null) { registry.Put(key, "LastUpdatedTimeUtc", updated); }
        if (state.HasValue)
        {
            registry.Put(key + @"\EnforcementStateMessage", "EnforcementStateMessage",
                string.Format(CultureInfo.InvariantCulture, "{{\"EnforcementState\":{0},\"ErrorCode\":{1}}}", state.Value, code));
        }
        if (compliance.HasValue)
        {
            registry.Put(key + @"\ComplianceStateMessage", "ComplianceStateMessage",
                string.Format(CultureInfo.InvariantCulture, "{{\"ComplianceState\":{0},\"ErrorCode\":0}}", compliance.Value));
        }
    }

    private void Fixture()
    {
        // The device: an app that installs but is never detected (packaging).
        AddApp(_h.Registry, Device, Packaged, 3, intent: 3, state: 5000, code: NotDetectedSigned);
        // User one: a required app whose content could not be downloaded, and one that is fine.
        AddApp(_h.Registry, UserOne, Download, 1, intent: 3, state: 5003);
        AddApp(_h.Registry, UserOne, OwnOk, 2, intent: 1, state: 1000, compliance: 1);
        // User two: never shown to user one.
        AddApp(_h.Registry, UserTwo, Theirs, 1, intent: 3, state: 5000, code: 1603);
    }

    private List<JsonNode> Apps() => _h.Records("app-diagnosis");

    [Fact]
    public void Writes_one_record_per_app_of_the_device_and_the_callers_own_scope()
    {
        Fixture();
        var run = _h.NewRun("diag0001", "DiagnoseApps");
        run.CallerSid = EntraSid(UserOne);

        Assert.Equal(EngineExitCode.Success, new DiagnoseAppsHandler().Run(run));

        var apps = Apps();
        Assert.Equal(new[] { Packaged, Download, OwnOk }, apps.Select(a => a.Str("app")));
        Assert.All(apps, a => Assert.Equal("diag0001", a.Str("diagnosis")));
        var packaged = apps[0];
        Assert.Equal("PACKAGING", packaged.Str("verdict"));
        Assert.Equal("device", packaged.Str("scope"));
        Assert.Equal("required", packaged.Str("intent"));
        Assert.Equal("3", packaged.Str("revision"));
        Assert.Equal("0x87D1041C", packaged.Str("code"));
        Assert.Equal("5000", packaged.Str("enforcement"));
        Assert.Equal("2026-11-10T13:00:00Z", packaged.Str("observed"));
        Assert.Equal("false", packaged.Str("download"));
        var download = apps[1];
        Assert.Equal("RETRY-HELPS", download.Str("verdict"));
        Assert.Equal("user", download.Str("scope"));
        Assert.Equal("true", download.Str("download"));
        Assert.Equal("OK", apps[2].Str("verdict"));
        Assert.Equal("available", apps[2].Str("intent"));

        var close = Assert.Single(_h.Records("apps-diagnosis"));
        Assert.Equal("diag0001", close.Str("diagnosis"));
        Assert.Equal("true", close.Str("present"));
        Assert.Equal("3", close.Str("records"));
        Assert.Equal("3", close.Str("listed"));
        Assert.Equal("1", close.Str("otherscopes"));
        Assert.Equal("true", close.Str("userscope"));
        Assert.Single(_h.Findings("DIAG-APPS-ATTENTION"));
        Assert.Empty(_h.Records("change"));
    }

    [Fact]
    public void A_second_user_sees_the_device_apps_and_their_own_and_nothing_of_the_first_user()
    {
        var first = new EngineHarness();
        AddApp(first.Registry, Device, Packaged, 3, intent: 3, state: 5000, code: NotDetectedSigned);
        AddApp(first.Registry, UserOne, Download, 1, intent: 3, state: 5003);
        AddApp(first.Registry, UserTwo, Theirs, 1, intent: 3, state: 5000, code: 1603);

        // User two on the same device.
        var run = first.NewRun("diag0002", "DiagnoseApps");
        run.CallerSid = EntraSid(UserTwo);
        Assert.Equal(EngineExitCode.Success, new DiagnoseAppsHandler().Run(run));
        var apps = first.Records("app-diagnosis");
        Assert.Equal(new[] { Packaged, Theirs }, apps.Select(a => a.Str("app")));
        Assert.Equal(new[] { "device", "user" }, apps.Select(a => a.Str("scope")));
        // No line of the run names user one's app or scope.
        Assert.DoesNotContain(first.Lines, l => l.Contains(Download) || l.Contains(UserOne));

        // An account Entra cannot match (a local or Active Directory account): the device only.
        var local = new EngineHarness();
        AddApp(local.Registry, Device, Packaged, 3, intent: 3, state: 5000, code: NotDetectedSigned);
        AddApp(local.Registry, UserOne, Download, 1, intent: 3, state: 5003);
        var localRun = local.NewRun("diag0003", "DiagnoseApps");
        localRun.CallerSid = "S-1-5-21-1000-2000-3000-1001";
        Assert.Equal(EngineExitCode.Success, new DiagnoseAppsHandler().Run(localRun));
        Assert.Equal(new[] { Packaged }, local.Records("app-diagnosis").Select(a => a.Str("app")));
        Assert.Equal("false", Assert.Single(local.Records("apps-diagnosis")).Str("userscope"));
        Assert.DoesNotContain(local.Lines, l => l.Contains(Download));
    }

    [Fact]
    public void Runs_through_the_run_frame_and_touches_none_of_the_mutating_parts()
    {
        Fixture();
        // Tripwires: any write to the registry, any service, process, task or file change fails the test.
        var registry = new WriteTrap(_h.Registry);
        _h.Services.Registry = registry;
        _h.Services.DeleteFileBelow = (_, _) => throw new InvalidOperationException("must not delete");
        _h.Services.TurnOnRedirectionGuard = () => throw new InvalidOperationException("only a run that works in user folders needs this");
        _h.ProcessControl.Running.Add(1234);
        var file = EngineHarness.WriteRequest(_folder, "diagnose.json",
            "{\"contractVersion\":1,\"runId\":\"diag0004\",\"action\":\"DiagnoseApps\",\"otherSessions\":0,\"callerSessionId\":1,\"callerSid\":\"" + EntraSid(UserOne) + "\",\"config\":{}}");

        var exit = RunFrame.Run(nameof(ToolbeltAction.DiagnoseApps), file, ActionHandlers.Shipped, _h.Services);

        Assert.Equal(EngineExitCode.Success, exit);
        Assert.Empty(registry.Writes);
        Assert.Empty(_h.Processes.Requests);
        Assert.Equal(0, _h.ServiceControl.Calls);
        Assert.Empty(_h.ServiceControl.Started);
        Assert.Equal(0, _h.TaskScheduler.Starts);
        Assert.Empty(_h.TaskScheduler.Folders);
        Assert.Empty(_h.Started);
        Assert.Equal(new[] { 1234 }, _h.ProcessControl.Running);

        Assert.Empty(_h.Records("change"));
        Assert.Empty(_h.Records("mdm-result"));
        Assert.Empty(_h.Records("mdm-queued"));
        Assert.Empty(_h.Records("file"));
        Assert.Empty(_h.Records("restart-required"));
        var summary = Assert.Single(_h.Records("summary"));
        Assert.Equal("0", summary.Str("changes"));
        Assert.Equal("false", summary.Str("restart"));
        Assert.Equal(3, Apps().Count);

        // The handler is built from read-only parts only: nothing of the repair actions.
        var handler = (DiagnoseAppsHandler)ActionHandlers.Shipped[nameof(ToolbeltAction.DiagnoseApps)];
        var types = typeof(DiagnoseAppsHandler).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(p => p.PropertyType)
            .Concat(typeof(DiagnosisParts).GetProperties().Select(p => p.PropertyType)).ToList();
        Assert.DoesNotContain(types, t => t == typeof(AppActionParts) || t.ToString().Contains("RefreshResult") || t.ToString().Contains("MdmSyncResult") || t.ToString().Contains("ContentCacheResult"));
        Assert.NotNull(handler);
        Assert.True(ActionCatalog.Get(ToolbeltAction.DiagnoseApps).ReadOnly);
        // The Windows Update check is the only other read-only action.
        Assert.Equal(new[] { ToolbeltAction.DiagnoseApps, ToolbeltAction.CheckWindowsUpdate }, ActionCatalog.All.Where(a => ActionCatalog.Get(a).ReadOnly).OrderBy(a => (int)a));
    }

    [Fact]
    public void Without_app_state_says_so_and_a_read_that_fails_ends_with_exit_1()
    {
        var run = _h.NewRun("diag0005", "DiagnoseApps");
        Assert.Equal(EngineExitCode.Success, new DiagnoseAppsHandler().Run(run));
        Assert.Single(_h.Findings("APPS-NO-STATE"));
        Assert.Equal("false", Assert.Single(_h.Records("apps-diagnosis")).Str("present"));
        Assert.Empty(Apps());

        var failing = new EngineHarness();
        var handler = new DiagnoseAppsHandler { Parts = new DiagnosisParts { Read = _ => throw new UnauthorizedAccessException("Access to the registry key is denied.") } };
        Assert.Equal(EngineExitCode.Failed, handler.Run(failing.NewRun("diag0006", "DiagnoseApps")));
        Assert.Single(failing.Findings("DIAG-APPS-UNREADABLE"));
        Assert.Single(failing.Records("apps-diagnosis"));
    }

    [Fact]
    public void Lists_at_most_the_cap_and_counts_the_rest()
    {
        for (var i = 0; i < DiagnoseAppsHandler.MaxApps + 2; i++)
        {
            AddApp(_h.Registry, Device, new Guid(i, 0, 0, new byte[8]).ToString(), 1, intent: 3, state: 1000);
        }
        Assert.Equal(EngineExitCode.Success, new DiagnoseAppsHandler().Run(_h.NewRun("diag0007", "DiagnoseApps")));
        Assert.Equal(DiagnoseAppsHandler.MaxApps, Apps().Count);
        var close = Assert.Single(_h.Records("apps-diagnosis"));
        Assert.Equal((DiagnoseAppsHandler.MaxApps + 2).ToString(CultureInfo.InvariantCulture), close.Str("records"));
        Assert.Single(_h.Findings("DIAG-APPS-OK"));
    }

    [Fact]
    public void A_cut_list_drops_records_with_nothing_to_do_first_never_an_offered_or_installed_app()
    {
        // Required, never attempted and not detected (nothing to do here), with GUIDs that sort
        // before the others, as they did on a real device.
        for (var i = 0; i < DiagnoseAppsHandler.MaxApps + 20; i++)
        {
            AddApp(_h.Registry, Device, new Guid(i, 0, 0, new byte[8]).ToString(), 1, intent: 3, state: null, compliance: 2);
        }
        var offered = new Guid(0x7FFFFFF0, 0, 0, new byte[8]).ToString();
        var installed = new Guid(0x7FFFFFF1, 0, 0, new byte[8]).ToString();
        var failed = new Guid(0x7FFFFFF2, 0, 0, new byte[8]).ToString();
        AddApp(_h.Registry, Device, offered, 1, intent: 1, state: null, compliance: 2);
        AddApp(_h.Registry, Device, installed, 1, intent: 3, state: 1000, compliance: 1);
        AddApp(_h.Registry, Device, failed, 1, intent: 3, state: 5000, code: 1603);

        Assert.Equal(EngineExitCode.Success, new DiagnoseAppsHandler().Run(_h.NewRun("diag0010", "DiagnoseApps")));

        var apps = Apps();
        Assert.Equal(DiagnoseAppsHandler.MaxApps, apps.Count);
        // Attention first, then offered, then installed; the cut fell on the records with nothing to do.
        Assert.Equal(new[] { failed, offered, installed }, apps.Take(3).Select(a => a.Str("app")));
        Assert.Equal(new[] { "RETRY-HELPS", "AVAILABLE", "OK" }, apps.Take(3).Select(a => a.Str("verdict")));
        Assert.All(apps.Skip(3), a => Assert.Equal("NO-ACTION", a.Str("verdict")));
        Assert.Equal((DiagnoseAppsHandler.MaxApps + 23).ToString(CultureInfo.InvariantCulture), Assert.Single(_h.Records("apps-diagnosis")).Str("records"));
        Assert.True(AppState.ListRank("AVAILABLE") < AppState.ListRank("OK"));
        Assert.Equal(AppState.ListRank("OK"), AppState.ListRank("IN-PROGRESS"));
        Assert.True(AppState.ListRank("NO-STATE") < AppState.ListRank("NO-ACTION"));
        Assert.True(AppState.ListRank("UNKNOWN") < AppState.ListRank("AVAILABLE"));
    }

    [Fact]
    public void The_engine_log_names_only_the_apps_that_need_attention_or_are_offered_and_counts_the_rest()
    {
        Fixture();
        var offered = new Guid(0x7FFFFFF0, 0, 0, new byte[8]).ToString();
        AddApp(_h.Registry, UserOne, offered, 1, intent: 1, state: null, compliance: 2);
        for (var i = 0; i < 10; i++) { AddApp(_h.Registry, Device, new Guid(i, 0, 0, new byte[8]).ToString(), 1, intent: 3, state: 1000, compliance: 1); }
        var run = _h.NewRun("diag0011", "DiagnoseApps");
        run.CallerSid = EntraSid(UserOne);
        run.LogDirectory = _folder.Path;

        Assert.Equal(EngineExitCode.Success, new DiagnoseAppsHandler().Run(run));

        // Every app keeps its full record.
        Assert.Equal(14, Apps().Count);
        var lines = System.IO.File.ReadAllText(_folder.Combine(EndpointToolbelt.ProductInfo.EngineLogFileName)).Split('\n');
        var named = lines.Where(l => l.Contains("EndpointToolbelt:APP-DIAGNOSIS ")).ToList();
        // The packaging app, the download that failed and the offered app; not the installed ones.
        Assert.Equal(3, named.Count);
        Assert.Contains(named, l => l.Contains(Packaged) && l.Contains("verdict=PACKAGING"));
        Assert.Contains(named, l => l.Contains(Download) && l.Contains("verdict=RETRY-HELPS"));
        Assert.Contains(named, l => l.Contains(offered) && l.Contains("verdict=AVAILABLE"));
        var counts = Assert.Single(lines, l => l.Contains("EndpointToolbelt:APPS-DIAGNOSIS "));
        Assert.Contains("records=14 listed=14 msi=0 msix=0 ok=11 inprogress=0 pending=0 nostate=0 available=1 noaction=0 packaging=1 restart=0 notapplicable=0 dependency=0 retry=1 unknown=0", counts, StringComparison.Ordinal);
        Assert.True(DiagnoseAppsHandler.LogsApp("IN-PROGRESS", download: true));
        Assert.False(DiagnoseAppsHandler.LogsApp("IN-PROGRESS", download: false));
        Assert.False(DiagnoseAppsHandler.LogsApp("NO-ACTION", download: false));
    }

    [Fact]
    public void Reads_the_observation_time_as_UTC_and_recognises_a_download_problem()
    {
        Assert.Equal("2026-11-10T13:00:00Z", DiagnoseAppsHandler.ObservedUtc("11/10/2026 13:00:00"));
        Assert.Equal("2026-11-10T13:00:00Z", DiagnoseAppsHandler.ObservedUtc("2026-11-10T13:00:00Z"));
        Assert.Equal("", DiagnoseAppsHandler.ObservedUtc("not a time"));
        Assert.Equal("", DiagnoseAppsHandler.ObservedUtc(""));
        Assert.Equal("", DiagnoseAppsHandler.ObservedUtc("1/1/0001 00:00:00"));

        Assert.True(DiagnoseAppsHandler.DownloadProblem(new AppStateRow { EnforcementState = 5003 }, null));
        Assert.True(DiagnoseAppsHandler.DownloadProblem(new AppStateRow { EnforcementState = 5000, ErrorCode = "0x80072EE2" }, null));
        Assert.False(DiagnoseAppsHandler.DownloadProblem(new AppStateRow { EnforcementState = 5000, ErrorCode = "0x80070643" }, null));
        Assert.True(DiagnoseAppsHandler.DownloadProblem(new AppStateRow { EnforcementState = 5000 }, new ImeAppAttempt { Phase = "Download", Result = "Failed" }));
        Assert.False(DiagnoseAppsHandler.DownloadProblem(new AppStateRow { EnforcementState = 5000 }, new ImeAppAttempt { Phase = "Install", Result = "Failed", LastSeenUtc = DateTime.UtcNow }));
        Assert.Equal("dependency", DiagnoseAppsHandler.Intent("Dependency (not assigned)"));
        Assert.Equal("", DiagnoseAppsHandler.Intent("Intent 9"));
    }

    /// <summary>A registry that reads through and refuses (and records) every write.</summary>
    private sealed class WriteTrap : IRegistry
    {
        private readonly IRegistry _inner;

        public WriteTrap(IRegistry inner) => _inner = inner;

        public List<string> Writes { get; } = new();

        public object GetValue(string subKey, string name) => _inner.GetValue(subKey, name);

        public bool KeyExists(string subKey) => _inner.KeyExists(subKey);

        public IReadOnlyList<string> SubKeyNames(string subKey) => _inner.SubKeyNames(subKey);

        public IReadOnlyList<string> ValueNames(string subKey) => _inner.ValueNames(subKey);

        public DateTime? KeyLastWriteUtc(string subKey) => _inner.KeyLastWriteUtc(subKey);

        public void SetString(string subKey, string name, string value)
        {
            Writes.Add("set " + subKey + "\\" + name);
            throw new InvalidOperationException("must not write");
        }

        public void DeleteValue(string subKey, string name)
        {
            Writes.Add("delete " + subKey + "\\" + name);
            throw new InvalidOperationException("must not delete");
        }

        public void DeleteKeyTree(string subKey)
        {
            Writes.Add("delete tree " + subKey);
            throw new InvalidOperationException("must not delete");
        }
    }
}
