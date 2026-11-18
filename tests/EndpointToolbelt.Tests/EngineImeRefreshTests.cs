using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.Actions;
using EndpointToolbelt.Engine.Ime;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// IME refresh, Full IME reset state and the app actions of the compiled engine.
/// The registry and services are fakes; files are the test's own.
/// </summary>
public class EngineImeRefreshTests : IDisposable
{
    private const string Ime = ImeRefresh.ServiceName;
    private readonly EngineHarness _h = new();
    private readonly TestFolder _folder = new();

    public EngineImeRefreshTests() => _h.ServiceControl.States[Ime] = "Running";

    public void Dispose() => _folder.Dispose();

    private RunContext Run(int others = 0)
    {
        var run = _h.NewRun();
        run.OtherSessions = others;
        return run;
    }

    [Fact]
    public void Reports_a_missing_IME_service()
    {
        _h.ServiceControl.States.Remove(Ime);
        Assert.False(new ImeRefresh(Run()).Run(RefreshDepth.RetryTimers).Ok);
        Assert.Single(_h.Findings("IME-MISSING"));
    }

    [Fact]
    public void Stands_down_when_other_users_are_signed_in_or_cannot_be_counted()
    {
        _h.ServiceControl.Timeout = true;
        foreach (var others in new[] { 2, -1 })
        {
            var r = new ImeRefresh(Run(others)).Run(RefreshDepth.RetryTimers, skipWhenOthersSignedIn: true);
            Assert.True(r.Skipped);
            Assert.True(r.Ok);
        }
        Assert.Equal(0, _h.ServiceControl.Calls);
        Assert.Equal(2, _h.Findings("IME-REFRESH-SKIPPED").Count);
    }

    [Fact]
    public void Never_clears_state_when_the_service_did_not_stop()
    {
        _h.ServiceControl.TimeoutOn.Add("Stopped");
        _h.Registry.AddKey(@"TEST\Win32Apps\" + AppState.DeviceScopeId + @"\GRS\a");
        var r = new ImeRefresh(Run()).Run(RefreshDepth.FullWin32State, clearFullState: _ => throw new InvalidOperationException("must not clear"));
        Assert.True(r.Warn);
        Assert.False(r.Wiped);
        Assert.True(r.Ok);
        Assert.True(_h.Registry.KeyExists(@"TEST\Win32Apps\" + AppState.DeviceScopeId + @"\GRS\a"));
        Assert.Single(_h.Findings("IME-STOP-FAILED"));
    }

    [Fact]
    public void Wipes_the_full_state_after_a_confirmed_stop_and_reports_a_failed_start()
    {
        _h.ServiceControl.TimeoutOn.Add("Running");
        var wipes = 0;
        var r = new ImeRefresh(Run(3)).Run(RefreshDepth.FullWin32State, clearFullState: _ => { wipes++; return 0; });
        Assert.True(r.Wiped);
        Assert.False(r.Ok);
        Assert.Equal(1, wipes);
        Assert.Single(_h.Findings("IME-START-FAILED"));
    }

    [Theory]
    [InlineData("Running", "Stopped", false, "Stop", true, 1)]
    [InlineData("Stopped", "Stopped", false, "Stop", true, 0)]
    [InlineData("StopPending", "StopPending", true, "Stop", false, 0)]
    [InlineData("StartPending", "StartPending", true, "Stop", false, 1)]
    [InlineData("Stopped", "Running", false, "Start", true, 1)]
    [InlineData("Running", "Running", false, "Start", true, 0)]
    [InlineData("StartPending", "StartPending", true, "Start", false, 0)]
    public void Counts_a_stop_or_start_as_done_only_when_the_service_reached_that_state_in_time(string initial, string after, bool timeout, string op, bool expected, int calls)
    {
        _h.ServiceControl.States[Ime] = initial;
        _h.ServiceControl.After = after;
        _h.ServiceControl.Timeout = timeout;
        var refresh = new ImeRefresh(Run());
        Assert.Equal(expected, op == "Stop" ? refresh.StopService() : refresh.StartService());
        Assert.Equal(calls, _h.ServiceControl.Calls);
    }

    [Fact]
    public void Reads_whether_Company_Portal_runs_and_in_which_sessions_and_never_ends_it()
    {
        _h.ProcessControl.Running.AddRange(new[] { 101, 102 });
        _h.ProcessControl.Sessions[101] = 1;
        _h.ProcessControl.Sessions[102] = 2;
        var run = Run();
        run.CallerSessionId = 1;
        Assert.Equal((2, 1), new ImeRefresh(run).CompanyPortalOpen());
        Assert.Equal(new[] { 101, 102 }, _h.ProcessControl.Running);
        Assert.Empty(_h.Records("change"));
        // The seam has nothing to end a process with.
        Assert.Null(typeof(IProcessControl).GetMethod("Kill"));
        Assert.Null(typeof(WindowsProcessControl).GetMethod("Kill"));
    }

    [Fact]
    public void Clears_only_the_retry_timers_and_keeps_the_rest_of_the_app_state()
    {
        foreach (var k in new[] { AppState.DeviceScopeId + @"\GRS\a+b=c", @"user1\GRS\x", @"user1\app1", @"user2\app2" }) { _h.Registry.AddKey(@"TEST\Win32Apps\" + k); }
        Assert.True(new ImeRefresh(Run()).ClearRetryTimers());
        Assert.False(_h.Registry.KeyExists(@"TEST\Win32Apps\" + AppState.DeviceScopeId + @"\GRS"));
        Assert.False(_h.Registry.KeyExists(@"TEST\Win32Apps\user1\GRS"));
        Assert.True(_h.Registry.KeyExists(@"TEST\Win32Apps\user1\app1"));
        Assert.True(_h.Registry.KeyExists(@"TEST\Win32Apps\user2\app2"));
        Assert.Contains("for 2 scope", Assert.Single(_h.Records("change")).Str("text"), StringComparison.Ordinal);
    }

    [Fact]
    public void Lists_real_user_profiles_only()
    {
        foreach (var (sid, path) in new[]
        {
            ("S-1-5-18", @"C:\Windows\system32\config\systemprofile"),
            ("S-1-5-20", @"C:\Windows\ServiceProfiles\NetworkService"),
            ("S-1-5-21-1-2-3-1001", @"C:\Users\alex"),
            ("S-1-12-1-11-22-33-44", @"C:\Users\sam"),
            ("S-1-5-21-1-2-3-1002.bak", @"C:\Users\old"),
            ("S-1-5-21-1-2-3-1003", @"\\server\profiles\remote"),
            ("S-1-5-21-1-2-3-1004", @"C:\Users\alex"),
        })
        {
            _h.Registry.Put(@"TEST\ProfileList\" + sid, "ProfileImagePath", path);
        }
        _h.Registry.AddKey(@"TEST\ProfileList\S-1-5-21-1-2-3-1005");
        Assert.Equal(new[] { @"C:\Users\alex", @"C:\Users\sam" }, new ImeRefresh(Run()).UserProfilePaths().OrderBy(p => p, StringComparer.Ordinal));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetLongPathName(string shortPath, [Out] char[] longPath, int size);

    /// <summary>The temp folder can be a short (8.3) path; the link-safe delete compares the long final path.</summary>
    private static string LongPath(string path)
    {
        var buffer = new char[1024];
        var length = GetLongPathName(path, buffer, buffer.Length);
        return length > 0 && length < buffer.Length ? new string(buffer, 0, length) : path;
    }

    [Fact]
    public void Removes_the_status_reports_and_the_Company_Portal_status_file_only_and_refuses_a_link_in_a_profile()
    {
        const string cpFolder = @"AppData\Local\Packages\Microsoft.CompanyPortal_8wekyb3d8bbwe\LocalState";
        var root = LongPath(_folder.Path);
        // Profile 1: a normal profile. Profile 2: its Packages folder is a junction to a folder
        // elsewhere, which must never be entered. Profile 3: no Company Portal.
        var p1 = Path.Combine(root, @"profiles\one");
        var p2 = Path.Combine(root, @"profiles\two");
        var p3 = Path.Combine(root, @"profiles\three");
        var elsewhere = Path.Combine(root, "elsewhere");
        foreach (var d in new[] { Path.Combine(p1, cpFolder), Path.Combine(p2, @"AppData\Local"), p3, Path.Combine(elsewhere, @"Microsoft.CompanyPortal_8wekyb3d8bbwe\LocalState") }) { Directory.CreateDirectory(d); }
        File.WriteAllText(Path.Combine(p1, cpFolder, "Win32LocalAppStatus.json"), "{}");
        File.WriteAllText(Path.Combine(p1, cpFolder, "signin.dat"), "keep");
        File.WriteAllText(Path.Combine(elsewhere, @"Microsoft.CompanyPortal_8wekyb3d8bbwe\LocalState\Win32LocalAppStatus.json"), "{}");
        TestFolder.CreateJunction(Path.Combine(p2, @"AppData\Local\Packages"), elsewhere);
        try
        {
            foreach (var k in new[] { @"SideCarPolicies\StatusServiceReports\app1", @"SideCarPolicies\Scripts\Execution\script1", @"Policies\user1\script1", @"Win32Apps\user1\app1" }) { _h.Registry.AddKey(@"TEST\" + k); }
            _h.Services.DeleteFileBelow = EndpointToolbelt.Platform.NativeFile.DeleteFileBelowNoFollow;
            new ImeRefresh(Run()).ClearFullWin32State(companyPortalClosed: true, profiles: () => new[] { p1, p2, p3 });

            Assert.False(_h.Registry.KeyExists(@"TEST\SideCarPolicies\StatusServiceReports"));
            Assert.True(_h.Registry.KeyExists(@"TEST\SideCarPolicies\Scripts\Execution\script1"));
            Assert.True(_h.Registry.KeyExists(@"TEST\Policies\user1\script1"));
            Assert.False(_h.Registry.KeyExists(@"TEST\Win32Apps"));
            Assert.False(File.Exists(Path.Combine(p1, cpFolder, "Win32LocalAppStatus.json")));
            Assert.True(File.Exists(Path.Combine(p1, cpFolder, "signin.dat")));
            Assert.True(File.Exists(Path.Combine(elsewhere, @"Microsoft.CompanyPortal_8wekyb3d8bbwe\LocalState\Win32LocalAppStatus.json")));
            var changes = _h.Records("change").Select(r => r.Str("text")).ToList();
            Assert.Contains("Company Portal's local app status cache cleared for 1 profile", changes);
            Assert.Single(changes, c => c.Contains("StatusServiceReports"));
            var refused = Assert.Single(_h.Records("progress"), r => r.Str("severity") == "warning" && r.Str("text").Contains("status cache in one profile"));
            Assert.Contains("link", refused.Str("text"), StringComparison.Ordinal);
            // No profile path is named in what goes back to the user.
            Assert.DoesNotContain(_h.Records("progress"), r => r.Str("text").Contains(@"profiles\"));
        }
        finally
        {
            Directory.Delete(Path.Combine(p2, @"AppData\Local\Packages"), false);
        }
    }

    [Fact]
    public void Says_per_profile_what_happened_to_the_Company_Portal_status_cache_so_a_missing_file_is_not_read_as_a_failure()
    {
        var outcomes = new Queue<string>(new[] { "absent", "removed", "error" });
        _h.Services.DeleteFileBelow = (_, _) => outcomes.Dequeue() switch { "absent" => false, "removed" => true, _ => throw new IOException("in use") };
        var refresh = new ImeRefresh(Run());
        Assert.Equal(1, refresh.ClearCompanyPortalStatusCache(() => new[] { @"C:\Users\a", @"C:\Users\b", @"C:\Users\c" }));
        _h.Services.DeleteFileBelow = (_, _) => false;
        Assert.Equal(0, refresh.ClearCompanyPortalStatusCache(() => new[] { @"C:\Users\a", @"C:\Users\b", @"C:\Users\c" }));
        var lines = _h.Records("progress").Where(r => r.Str("text").StartsWith("Company Portal's status cache:", StringComparison.Ordinal)).ToList();
        Assert.Equal("Company Portal's status cache: cleared in 1 profile; no cache file in 1 profile (nothing to clear); could not be cleared in 1 profile.", lines[0].Str("text"));
        Assert.Equal("warning", lines[0].Str("severity"));
        Assert.Equal("Company Portal's status cache: no cache file in 3 profiles (nothing to clear).", lines[1].Str("text"));
        Assert.NotEqual("warning", lines[1].Str("severity"));
    }

    [Fact]
    public void Leaves_the_Company_Portal_status_file_alone_when_Company_Portal_could_not_be_closed()
    {
        var listed = 0;
        new ImeRefresh(Run()).ClearFullWin32State(companyPortalClosed: false, profiles: () => { listed++; return Array.Empty<string>(); });
        Assert.Equal(0, listed);
    }

    [Fact]
    public void The_full_reset_does_not_start_while_Company_Portal_runs_and_changes_nothing()
    {
        _h.ServiceControl.States[Ime] = "Running";
        _h.ProcessControl.Running.Add(7);
        var cleared = new List<bool>();
        var r = new ImeRefresh(Run()).Run(RefreshDepth.FullWin32State, clearFullState: closed => { cleared.Add(closed); return 0; });
        Assert.False(r.Ok);
        Assert.True(r.PortalOpen);
        Assert.False(r.Wiped);
        Assert.Empty(cleared);
        // The IME was not stopped, nothing changed, Company Portal still runs.
        Assert.Equal(0, _h.ServiceControl.Calls);
        Assert.Empty(_h.Records("change"));
        Assert.Equal(new[] { 7 }, _h.ProcessControl.Running);
        var finding = Assert.Single(_h.Findings("FULLRESET-PORTAL-OPEN"));
        Assert.Contains("Nothing was changed", finding.Str("detail"));
        Assert.Equal("Close Company Portal, then run the full reset again.", finding.Str("next"));
    }

    [Fact]
    public void Company_Portal_open_in_another_session_is_named_and_the_light_depth_leaves_it_open()
    {
        _h.ServiceControl.States[Ime] = "Running";
        _h.ProcessControl.Running.Add(7);
        _h.ProcessControl.Sessions[7] = 3;
        var run = Run();
        run.CallerSessionId = 1;
        new ImeRefresh(run).Run(RefreshDepth.FullWin32State);
        var finding = Assert.Single(_h.Findings("FULLRESET-PORTAL-OPEN"));
        Assert.StartsWith("Company Portal is open in 1 other user session on this device.", finding.Str("detail"));
        Assert.StartsWith("Ask the other people signed in", finding.Str("next"));

        // Retry work apps and settings (the light depth) goes on and leaves Company Portal open.
        var cleared = new List<bool>();
        var light = new ImeRefresh(Run()).Run(RefreshDepth.RetryTimers, clearFullState: closed => { cleared.Add(closed); return 0; });
        Assert.False(light.PortalOpen);
        Assert.False(light.Warn && !light.Ok);
        Assert.Empty(cleared);
        Assert.Equal(new[] { 7 }, _h.ProcessControl.Running);
        Assert.Contains(_h.Records("progress"), p => p.Str("text").StartsWith("Company Portal is open; it was not closed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_full_reset_with_Company_Portal_closed_clears_its_cache_too()
    {
        _h.ServiceControl.States[Ime] = "Running";
        _h.ServiceControl.After = "Stopped";
        var cleared = new List<bool>();
        var r = new ImeRefresh(Run()).Run(RefreshDepth.FullWin32State, clearFullState: closed => { cleared.Add(closed); return 0; });
        Assert.True(r.PortalClosed);
        Assert.Equal(new[] { true }, cleared);
    }

    [Fact]
    public void No_Kill_of_Company_Portal_anywhere_in_the_engine_source()
    {
        var root = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "EndpointToolbelt.slnx"))) { root = root.Parent; }
        Assert.NotNull(root);
        var sources = Directory.GetFiles(Path.Combine(root.FullName, "src", "EndpointToolbelt.Engine"), "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(sources);
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            Assert.False(text.Contains(".Kill(") || text.Contains("TerminateProcess"), Path.GetFileName(file));
        }
    }
}

public class EngineAppActionTests
{
    private readonly EngineHarness _h = new();

    private static RefreshResult Ime(bool ok = true, bool warn = false, bool skipped = false, bool wiped = false, bool portalClosed = true, int wipeFailures = 0, bool cleared = true) =>
        new() { Ok = ok, Warn = warn, Skipped = skipped, Wiped = wiped, PortalClosed = portalClosed, WipeFailures = wipeFailures, TimersCleared = cleared };

    private static MdmSyncResult Sync(int exit = 0, string status = "VerifiedSuccess", string route = "none") => new() { Status = status, ExitCode = exit, Route = route, Detail = "" };

    private static AppActionParts Parts(Func<RefreshResult> refresh, Func<MdmSyncResult> sync = null, IReadOnlyList<AppRetry> retries = null, List<string> order = null) => new()
    {
        Readout = (_, _) => { order?.Add("readout"); return retries ?? Array.Empty<AppRetry>(); },
        Refresh = (_, _, _) => { order?.Add("refresh"); return refresh(); },
        Sync = _ => (sync ?? (() => Sync()))(),
        SyncFinding = (_, _) => { },
        ClearContent = _ => throw new InvalidOperationException("must not run"),
    };

    private RunContext Run()
    {
        var run = _h.NewRun();
        run.OtherSessions = 0;
        return run;
    }

    [Fact]
    public void Reads_the_app_state_before_Retry_apps_and_policies_and_Full_IME_reset_change_anything()
    {
        var order = new List<string>();
        new RetryAppsAndPoliciesHandler { Parts = Parts(() => Ime(wiped: true), order: order) }.Run(Run());
        new FullResetHandler { Parts = Parts(() => Ime(wiped: true), order: order) }.Run(Run());
        Assert.Equal(new[] { "readout", "refresh", "readout", "refresh" }, order);
        // An active cooldown stops the reset before the readout.
        _h.Registry.Put(StateStore.Key, "LastFullReset", StateStore.ToStateTime(new DateTimeOffset(_h.Clock.UtcNow)));
        new FullResetHandler { Parts = Parts(() => Ime(wiped: true), order: order) }.Run(Run());
        Assert.Equal(4, order.Count);
    }

    [Theory]
    [InlineData(true, false, false, 0, 0)]
    [InlineData(true, false, true, 0, 0)]
    [InlineData(true, true, false, 0, 2)]
    [InlineData(true, false, false, 2, 2)]
    [InlineData(true, false, false, 1, 1)]
    [InlineData(false, false, false, 0, 1)]
    public void Retry_apps_and_policies_maps_both_halves_to_the_exit_code(bool ok, bool warn, bool skipped, int sync, int expected)
    {
        var handler = new RetryAppsAndPoliciesHandler { Parts = Parts(() => Ime(ok, warn, skipped, cleared: !skipped && !warn), () => Sync(sync, "X")) };
        Assert.Equal(expected, handler.Run(Run()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Retry_apps_and_policies_logs_only_what_happened_with_a_verified_sync(bool skipped)
    {
        var handler = new RetryAppsAndPoliciesHandler { Parts = Parts(() => Ime(skipped: skipped, cleared: !skipped), () => Sync(0)) };
        Assert.Equal(0, handler.Run(Run()));
        var log = string.Join("\n", _h.Lines);
        Assert.Contains("the Intune sync was verified", log, StringComparison.OrdinalIgnoreCase);
        if (skipped)
        {
            Assert.DoesNotContain("The IME restarted", log);
            Assert.Contains("The IME restart and the retry-timer clear were skipped", log);
        }
        else { Assert.Contains("The IME restarted and the app retry timers were cleared", log); }
    }

    [Theory]
    [InlineData("cleared", false, true, true, 0)]
    [InlineData("skipped for other users", true, false, true, 1)]
    [InlineData("clear failed", false, false, true, 1)]
    [InlineData("IME missing", false, false, false, 0)]
    public void Retry_apps_and_policies_says_when_the_IME_retries_by_itself_only_when_the_timers_stayed(string @case, bool skipped, bool cleared, bool ok, int expected)
    {
        var retries = new[] { new AppRetry { Name = "Contoso App", AppId = "a", NextUtc = _h.Clock.UtcNow.AddHours(3) } };
        new RetryAppsAndPoliciesHandler { Parts = Parts(() => Ime(ok, !cleared, skipped, cleared: cleared), () => Sync(0, "X"), retries) }.Run(Run());
        Assert.True(expected == _h.Findings("APPS-NEXT-RETRY").Count, @case);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, "APPCACHE-EMPTY")]
    [InlineData(2, 0, 0, 0, "APPCACHE-CLEARED")]
    // A folder still in use is normal; CHECK (2) only when more stayed in use than were removed.
    [InlineData(1, 1, 0, 0, "APPCACHE-IN-USE")]
    [InlineData(0, 2, 0, 2, "APPCACHE-IN-USE")]
    [InlineData(1, 0, 1, 2, "APPCACHE-FAILED")]
    [InlineData(0, 0, 2, 1, "APPCACHE-FAILED")]
    public void Clear_downloaded_app_content_maps_removed_skipped_and_failed_folders(int removed, int skipped, int failed, int expected, string id)
    {
        var handler = new ClearAppCacheHandler { Parts = new AppActionParts { ClearContent = _ => new ContentCacheResult { Removed = removed, Skipped = skipped, Failed = failed, FreedMB = 1.5 } } };
        Assert.Equal(expected, handler.Run(Run()));
        Assert.Single(_h.Findings(id));
    }

    [Fact]
    public void Retry_apps_and_policies_and_Full_IME_reset_log_five_numbered_steps_and_the_sync_status_in_words()
    {
        _h.ServiceControl.States[ImeRefresh.ServiceName] = "Running";
        foreach (var full in new[] { false, true })
        {
            _h.Lines.Clear();
            var parts = new AppActionParts
            {
                Readout = (_, _) => Array.Empty<AppRetry>(),
                Refresh = (run, depth, skip) => new ImeRefresh(run).Run(depth, skip, clearFullState: _ => 0),
                Sync = _ => Sync(),
                SyncFinding = (_, _) => { },
            };
            IActionHandler handler = full ? new FullResetHandler { Parts = parts } : new RetryAppsAndPoliciesHandler { Parts = parts };
            Assert.Equal(0, handler.Run(Run()));
            var texts = _h.Records("progress").Select(r => r.Str("text")).ToList();
            var headers = texts.Where(t => t.StartsWith("== Step ", StringComparison.Ordinal)).ToList();
            Assert.Equal(5, headers.Count);
            var titles = headers.Select(h => System.Text.RegularExpressions.Regex.Replace(h, @"^== Step (\d)/5: (.+) ==$", "$1 $2")).ToList();
            var clear = full ? "Clear all app installation state" : "Clear the retry timers";
            Assert.Equal(new[] { "1 Check which apps need attention", "2 Stop the app installer (IME)", "3 " + clear, "4 Start the app installer (IME) again", "5 Ask Intune to check this device (sync)" }, titles);
            Assert.Single(texts, t => System.Text.RegularExpressions.Regex.IsMatch(t, "Intune sync:? verified"));
            Assert.DoesNotContain(texts, t => t.Contains("VerifiedSuccess"));
        }
        var done = Assert.Single(_h.Findings("FULLRESET-DONE"));
        Assert.Contains("up to a few hours", done.Str("detail"), StringComparison.Ordinal);
        Assert.Contains("Settings > Accounts > Access work or school > Sync", done.Str("next"), StringComparison.Ordinal);
    }

    [Fact]
    public void Clear_downloaded_app_content_logs_its_step_and_a_short_scope()
    {
        var handler = new ClearAppCacheHandler { Parts = new AppActionParts { ClearContent = _ => new ContentCacheResult { Removed = 1, FreedMB = 1.5 } } };
        Assert.Equal(0, handler.Run(Run()));
        var texts = _h.Records("progress").Select(r => r.Str("text")).ToList();
        Assert.Equal("== Step 1/1: Remove downloaded app content (IMECache) ==", texts[0]);
        Assert.Contains("Scope: 1 folder removed (1.5 MB). Not touched: IME service, Delivery Optimization cache.", texts);
        Assert.Equal("Removed 1 leftover app content folder, 1.5 MB", _h.Findings("APPCACHE-CLEARED")[0].Str("title"));
    }

    [Fact]
    public void Full_IME_reset_honours_the_cooldown_before_touching_anything()
    {
        _h.Registry.Put(StateStore.Key, "LastFullReset", StateStore.ToStateTime(new DateTimeOffset(_h.Clock.UtcNow)));
        Assert.Equal(69001, new FullResetHandler { Parts = Parts(() => throw new InvalidOperationException("must not run")) }.Run(Run()));
    }

    [Fact]
    public void Full_IME_reset_stamps_the_cooldown_as_soon_as_the_wipe_ran_even_when_the_start_failed()
    {
        var handler = new FullResetHandler { Parts = Parts(() => Ime(ok: false, wiped: true), () => throw new InvalidOperationException("must not sync")) };
        Assert.Equal(1, handler.Run(Run()));
        Assert.NotNull(StateStore.FromStateTime((string)_h.Registry.GetValue(StateStore.Key, "LastFullReset")));
    }

    [Fact]
    public void Full_IME_reset_puts_the_old_stamp_back_when_nothing_was_wiped()
    {
        _h.Registry.Put(StateStore.Key, "LastFullReset", "2026-10-01T08:00:00.0000000+02:00");
        var handler = new FullResetHandler { Parts = Parts(() => Ime(ok: true, wiped: false)) };
        handler.Run(Run());
        Assert.Equal("2026-10-01T08:00:00.0000000+02:00", _h.Registry.GetValue(StateStore.Key, "LastFullReset"));
    }

    [Fact]
    public void Full_IME_reset_is_a_partial_result_when_the_sync_is_not_verified()
    {
        var handler = new FullResetHandler { Parts = Parts(() => Ime(wiped: true), () => Sync(2, "Throttled", "enrollment task")) };
        Assert.Equal(2, handler.Run(Run()));
    }

    [Fact]
    public void Full_IME_reset_reports_a_partial_result_not_success_when_part_of_the_wipe_failed()
    {
        var handler = new FullResetHandler { Parts = Parts(() => Ime(warn: true, wiped: true, wipeFailures: 2)) };
        Assert.Equal(2, handler.Run(Run()));
        Assert.Empty(_h.Findings("FULLRESET-DONE"));
        var partial = Assert.Single(_h.Findings("FULLRESET-PARTIAL"));
        Assert.Contains("2 parts could not be cleared", partial.Str("detail"), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(partial.Str("support")));
    }

    [Fact]
    public void Full_IME_reset_with_Company_Portal_open_fails_changes_nothing_and_keeps_no_cooldown()
    {
        var handler = new FullResetHandler { Parts = Parts(() => new RefreshResult { Ok = false, PortalOpen = true }) };
        var run = Run();
        Assert.Equal(EngineExitCode.Failed, handler.Run(run));
        Assert.Empty(_h.Findings("FULLRESET-DONE"));
        // The provisional cooldown stamp is put back: nothing was changed.
        Assert.Null(run.State.Get("LastFullReset"));
        // Done, it names Company Portal's cache as cleared, never "closed" by the engine.
        var done = new FullResetHandler { Parts = Parts(() => Ime(wiped: true)) };
        done.Run(Run());
        var text = Assert.Single(_h.Findings("FULLRESET-DONE")).Str("detail");
        Assert.Contains("Company Portal's cached app status were cleared", text, StringComparison.Ordinal);
        Assert.DoesNotContain("was closed", text, StringComparison.Ordinal);
    }
}

public class EngineImeFileTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly EngineHarness _h = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Deletes_a_tree_without_following_a_junction()
    {
        var outside = _folder.Combine("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");
        var tree = _folder.Combine("tree");
        Directory.CreateDirectory(Path.Combine(tree, "sub"));
        File.WriteAllText(Path.Combine(tree, @"sub\a.txt"), "x");
        TestFolder.CreateJunction(Path.Combine(tree, @"sub\link"), outside);
        ImeRefresh.DeleteTree(tree);
        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void Deletes_read_only_folders_and_files()
    {
        var sub = _folder.Combine("readonly", "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "a.txt"), "x");
        File.SetAttributes(Path.Combine(sub, "a.txt"), FileAttributes.ReadOnly);
        new DirectoryInfo(sub).Attributes = FileAttributes.Directory | FileAttributes.ReadOnly;
        ImeRefresh.DeleteTree(_folder.Combine("readonly"));
        Assert.False(Directory.Exists(_folder.Combine("readonly")));
    }

    [Fact]
    public void Lists_files_without_entering_a_junction()
    {
        var outside = _folder.Combine("outside2");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "x");
        var tree = _folder.Combine("walk");
        Directory.CreateDirectory(Path.Combine(tree, "sub"));
        File.WriteAllText(Path.Combine(tree, @"sub\a.txt"), "x");
        TestFolder.CreateJunction(Path.Combine(tree, "link"), outside);
        Assert.Equal(new[] { "a.txt" }, ImeRefresh.TreeFiles(tree).Select(f => f.Name));
    }

    [Fact]
    public void Treats_only_a_sharing_violation_as_a_lock_also_for_a_read_only_file()
    {
        var file = _folder.Combine("locked.bin");
        File.WriteAllText(file, "x");
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(ImeRefresh.IsFileLocked(file));
            Assert.False(ImeRefresh.IsFileLocked(file + ".missing"));
        }
        Assert.False(ImeRefresh.IsFileLocked(file));
        File.SetAttributes(file, FileAttributes.ReadOnly);
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read)) { Assert.True(ImeRefresh.IsFileLocked(file)); }
        Assert.False(ImeRefresh.IsFileLocked(file));
        File.SetAttributes(file, FileAttributes.Normal);
    }

    [Fact]
    public void Measures_and_clears_folders_with_several_files_and_empty_folders()
    {
        var cache = _h.Services.ImePaths.ContentCache = _folder.Combine("IMECache2");
        foreach (var d in new[] { @"big_1\sub", "busy_1", "empty_1" }) { Directory.CreateDirectory(Path.Combine(cache, d)); }
        File.WriteAllBytes(Path.Combine(cache, @"big_1\a.bin"), new byte[2 * 1024 * 1024]);
        File.WriteAllBytes(Path.Combine(cache, @"big_1\sub\b.bin"), new byte[1024 * 1024]);
        foreach (var n in new[] { "a.bin", "b.bin", "c.bin" }) { File.WriteAllText(Path.Combine(cache, "busy_1", n), "x"); }
        ContentCacheResult r;
        using (File.Open(Path.Combine(cache, @"busy_1\c.bin"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            r = new ImeRefresh(_h.NewRun()).ClearContentCache(requireIdle: true);
        }
        Assert.Equal(2, r.Removed);
        Assert.Equal(1, r.Skipped);
        Assert.Equal(0, r.Failed);
        Assert.Equal(3, r.FreedMB);
        Assert.False(Directory.Exists(Path.Combine(cache, "big_1")));
        Assert.False(Directory.Exists(Path.Combine(cache, "empty_1")));
        Assert.True(File.Exists(Path.Combine(cache, @"busy_1\c.bin")));
    }

    [Fact]
    public void Reports_an_unreadable_cache_as_a_failure_never_as_empty()
    {
        // A file where the cache folder belongs: it exists, but cannot be listed.
        File.WriteAllText(_h.Services.ImePaths.ContentCache = _folder.Combine("IMECache-file"), "x");
        Assert.Equal(1, new ClearAppCacheHandler().Run(_h.NewRun()));
        Assert.Single(_h.Findings("APPCACHE-UNREADABLE"));
        Assert.Empty(_h.Findings("APPCACHE-EMPTY"));
    }

    [Fact]
    public void Clears_unpacked_content_keeps_HealthScripts_and_skips_folders_in_use()
    {
        var cache = _h.Services.ImePaths.ContentCache = _folder.Combine("IMECache");
        foreach (var d in new[] { "HealthScripts", "app1_1", "app2_1" }) { Directory.CreateDirectory(Path.Combine(cache, d)); }
        File.WriteAllText(Path.Combine(cache, @"app1_1\setup.exe"), "x");
        File.WriteAllText(Path.Combine(cache, @"app2_1\setup.exe"), "x");
        ContentCacheResult r;
        using (File.Open(Path.Combine(cache, @"app2_1\setup.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            r = new ImeRefresh(_h.NewRun()).ClearContentCache(requireIdle: true);
        }
        Assert.Equal(1, r.Removed);
        Assert.Equal(1, r.Skipped);
        Assert.True(Directory.Exists(Path.Combine(cache, "HealthScripts")));
        Assert.False(Directory.Exists(Path.Combine(cache, "app1_1")));
        Assert.True(Directory.Exists(Path.Combine(cache, "app2_1")));
    }
}
