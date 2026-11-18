using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.Actions;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Platform;
using EndpointToolbelt.Service;
using EndpointToolbelt.Service.State;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The app name cache. Filled from the IME's "Get policies" lines (the one pattern the engine
/// already used), bounded by count and age, written atomically, ignored and rebuilt when unreadable,
/// and used for an app the current IME logs no longer name; an app neither knows stays unknown.
/// Contoso names and made-up IDs only.
/// </summary>
public class AppNameCacheTests : IDisposable
{
    private const string Viewer = "66666666-7777-8888-9999-aaaaaaaaaaaa";
    private const string Agent = "77777777-8888-9999-aaaa-bbbbbbbbbbbb";
    private const string Never = "dddddddd-eeee-ffff-0000-111111111111";

    private static readonly DateTime T0 = new(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);

    private readonly TestFolder _folder = new();
    private readonly EngineHarness _h = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>A policy line as the IME writes it in AppWorkload.log (a CMTrace record), with two apps and other fields.</summary>
    private static string PolicyLine(params (string Id, string Name)[] apps)
        => "<![LOG[Get policies = [" + string.Join(",", apps.Select(a => "{\"Id\":\"" + a.Id + "\",\"Name\":\"" + a.Name + "\",\"Version\":1,\"Intent\":3,\"TargetType\":1}")) + "]]LOG]!><time=\"09:00:00.0000000\" date=\"11-13-2026\" component=\"AppWorkload\" context=\"\" type=\"1\" thread=\"4\" file=\"\">";

    private string CachePath => _folder.Combine(AppNameCache.FileName);

    private static bool Trusted(string folder) => true;

    [Fact]
    public void A_real_format_policy_line_gives_each_id_and_its_unescaped_name()
    {
        var line = PolicyLine((Viewer.ToUpperInvariant(), "Contoso Viewer \\u0026 Tools"), (Agent, "Contoso \\\"Agent\\\""));
        var names = AppNameCache.PolicyNames(line).ToList();
        Assert.Equal(new[] { (Viewer, "Contoso Viewer & Tools"), (Agent, "Contoso \"Agent\"") }, names);
        Assert.Empty(AppNameCache.PolicyNames("<![LOG[Get policies = []]LOG]!>"));
        Assert.Empty(AppNameCache.PolicyNames("no policy here"));
        // In a log text: each policy record, the newest name winning.
        var text = "noise\r\n" + PolicyLine((Viewer, "Contoso Viewer 1")) + "\r\n" + PolicyLine((Viewer, "Contoso Viewer 2"), (Agent, "Contoso Agent")) + "\r\nmore";
        var inText = AppNameCache.PolicyNamesInText(text);
        Assert.Equal("Contoso Viewer 2", inText[Viewer]);
        Assert.Equal("Contoso Agent", inText[Agent]);
    }

    [Fact]
    public void Update_fills_the_cache_with_the_id_name_and_first_and_last_seen_only()
    {
        var cache = AppNameCache.Update(CachePath, AppNameCache.PolicyNames(PolicyLine((Viewer, "Contoso Viewer"))), T0, Trusted);
        Assert.True(File.Exists(CachePath));
        Assert.Equal("Contoso Viewer", Assert.Single(cache.Entries).Name);
        var json = File.ReadAllText(CachePath);
        Assert.Equal("{\"version\":1,\"apps\":[{\"id\":\"" + Viewer + "\",\"name\":\"Contoso Viewer\",\"first\":\"2026-11-13T09:00:00Z\",\"last\":\"2026-11-13T09:00:00Z\"}]}", json);
        // Seen again the next day: the last seen moves, the first stays.
        var later = AppNameCache.Update(CachePath, new[] { (Viewer, "Contoso Viewer") }, T0.AddDays(2), Trusted);
        var entry = Assert.Single(later.Entries);
        Assert.Equal(T0, entry.FirstSeenUtc);
        Assert.Equal(T0.AddDays(2), entry.LastSeenUtc);
        // Seen again within a day: nothing to write.
        var again = AppNameCache.Load(CachePath);
        again.Observe(Viewer, "Contoso Viewer", T0.AddDays(2).AddHours(3));
        Assert.False(again.Changed);
        // An ID that is not a GUID, or an empty name, is never stored.
        again.Observe("not-a-guid", "Contoso", T0);
        again.Observe(Agent, "  ", T0);
        Assert.Equal(1, again.Count);
    }

    [Fact]
    public void Entries_not_seen_for_180_days_are_dropped_and_at_most_2000_are_kept_the_oldest_first_out()
    {
        var cache = new AppNameCache();
        cache.Observe(Viewer, "Contoso Viewer", T0.AddDays(-181));
        cache.Observe(Agent, "Contoso Agent", T0.AddDays(-179));
        cache.Prune(T0);
        Assert.False(cache.TryGet(Viewer, out _));
        Assert.True(cache.TryGet(Agent, out _));

        var full = new AppNameCache();
        for (var i = 0; i < AppNameCache.MaxEntries + 5; i++)
        {
            full.Observe(new Guid(i, 0, 0, new byte[8]).ToString(), "Contoso App " + i.ToString(CultureInfo.InvariantCulture), T0.AddMinutes(i));
        }
        full.Prune(T0.AddDays(1));
        Assert.Equal(AppNameCache.MaxEntries, full.Count);
        // The five seen first are gone; the newest stays.
        Assert.False(full.TryGet(new Guid(0, 0, 0, new byte[8]).ToString(), out _));
        Assert.False(full.TryGet(new Guid(4, 0, 0, new byte[8]).ToString(), out _));
        Assert.True(full.TryGet(new Guid(5, 0, 0, new byte[8]).ToString(), out _));
        Assert.True(full.TryGet(new Guid(AppNameCache.MaxEntries + 4, 0, 0, new byte[8]).ToString(), out _));
    }

    [Fact]
    public void Save_writes_atomically_with_a_temporary_file_only_in_a_trusted_folder_and_by_its_own_name()
    {
        var cache = new AppNameCache();
        cache.Observe(Viewer, "Contoso Viewer", T0);
        Assert.True(cache.Save(CachePath, Trusted));
        cache.Observe(Agent, "Contoso Agent", T0);
        Assert.True(cache.Save(CachePath, Trusted));
        // Replaced as a whole, no temporary file left.
        Assert.Equal(2, AppNameCache.Load(CachePath).Count);
        Assert.Equal(new[] { AppNameCache.FileName }, Directory.GetFiles(_folder.Path).Select(Path.GetFileName));
        // Never in a folder that is not the SYSTEM-only working folder, never under another name, never relative.
        Assert.False(cache.Save(_folder.Combine("other.json"), Trusted));
        Assert.False(cache.Save(AppNameCache.FileName, Trusted));
        var elsewhere = Path.Combine(_folder.Path, "sub");
        Directory.CreateDirectory(elsewhere);
        Assert.False(cache.Save(Path.Combine(elsewhere, AppNameCache.FileName), _ => false));
        Assert.False(File.Exists(Path.Combine(elsewhere, AppNameCache.FileName)));
        // The test folder is not SYSTEM-only, so the real policy refuses it.
        Assert.False(AppNameCache.IsSystemOnlyFolder(_folder.Path));
        // Never through a junction in its path, even a folder that is otherwise accepted.
        var target = Path.Combine(_folder.Path, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_folder.Path, "link");
        TestFolder.CreateJunction(link, target);
        Assert.False(cache.Save(Path.Combine(link, AppNameCache.FileName), Trusted));
        Assert.False(File.Exists(Path.Combine(target, AppNameCache.FileName)));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"version\":2,\"apps\":[]}")]
    [InlineData("{\"apps\":{}}")]
    [InlineData("")]
    public void A_corrupt_file_is_ignored_and_rebuilt_never_fatal(string content)
    {
        File.WriteAllText(CachePath, content);
        var read = AppNameCache.Load(CachePath);
        Assert.Equal(0, read.Count);
        Assert.True(read.WasUnreadable);
        var rebuilt = AppNameCache.Update(CachePath, new[] { (Viewer, "Contoso Viewer") }, T0, Trusted);
        Assert.Equal(1, rebuilt.Count);
        var again = AppNameCache.Load(CachePath);
        Assert.False(again.WasUnreadable);
        Assert.True(again.TryGet(Viewer, out var name));
        Assert.Equal("Contoso Viewer", name);
        // Entries that cannot be read are skipped, the rest is kept.
        File.WriteAllText(CachePath, "{\"version\":1,\"apps\":[{\"id\":\"bad\",\"name\":\"x\",\"first\":\"2026-11-13T09:00:00Z\",\"last\":\"2026-11-13T09:00:00Z\"},"
            + "{\"id\":\"" + Agent + "\",\"name\":\"Contoso Agent\",\"first\":\"2026-11-13T09:00:00Z\",\"last\":\"not a time\"},"
            + "{\"id\":\"" + Viewer + "\",\"name\":\"Contoso Viewer\",\"first\":\"2026-11-13T09:00:00Z\",\"last\":\"2026-11-13T09:00:00Z\"}]}");
        var partial = AppNameCache.Load(CachePath);
        Assert.False(partial.WasUnreadable);
        Assert.Equal(1, partial.Count);
        // A missing file is simply empty.
        Assert.False(AppNameCache.Load(_folder.Combine("missing", AppNameCache.FileName)).WasUnreadable);
    }

    private RunContext Run()
    {
        _h.Services.TrustedStateFolder = Trusted;
        _h.Clock.UtcNow = T0;
        var run = _h.NewRun();
        run.AppNameCacheFile = CachePath;
        return run;
    }

    [Fact]
    public void After_the_log_rotated_the_name_comes_from_the_cache_and_an_app_neither_knows_stays_unknown()
    {
        var logs = Path.Combine(_folder.Path, "Logs");
        Directory.CreateDirectory(logs);
        _h.Services.ImePaths.LogsFolder = logs;
        // An available app the user chose in Company Portal: its line is in the log for now.
        File.WriteAllLines(Path.Combine(logs, "AppWorkload.log"), new[] { "noise", PolicyLine((Viewer, "Contoso Viewer"), (Agent, "Contoso Agent")) });
        var first = AppState.LookUpNames(Run(), new[] { Viewer, Never });
        Assert.Equal("Contoso Viewer", first.Names[Viewer]);
        Assert.Equal("log", first.Source(Viewer));
        Assert.False(first.Names.ContainsKey(Never));
        // Every name of the line went into the cache, also of an app not asked for.
        Assert.Equal(2, AppNameCache.Load(CachePath).Count);

        // The log rotated: the line is gone.
        File.WriteAllLines(Path.Combine(logs, "AppWorkload.log"), new[] { "only noise now" });
        var second = AppState.LookUpNames(Run(), new[] { Viewer.ToUpperInvariant(), Never });
        Assert.Equal("Contoso Viewer", second.Names[Viewer]);
        Assert.Equal("cache", second.Source(Viewer));
        Assert.Equal("", second.Source(Never));
        Assert.False(second.Names.ContainsKey(Never));
        Assert.Contains(_h.Records("progress"), r => r.Str("text").Contains("1 from earlier IME logs (the name cache holds 2 names)"));

        // Without a cache (an older service gives none) the logs alone, as before.
        var plain = _h.NewRun();
        Assert.Empty(AppState.LookUpNames(plain, new[] { Viewer }).Names);
    }

    [Fact]
    public void The_diagnosis_record_carries_where_the_name_came_from()
    {
        EngineAppDiagnosisTests.AddApp(_h.Registry, AppState.DeviceScopeId, Viewer, 2, intent: 3, state: 5000, code: 1603);
        EngineAppDiagnosisTests.AddApp(_h.Registry, AppState.DeviceScopeId, Agent, 1, intent: 3, state: 5000, code: 1603);
        EngineAppDiagnosisTests.AddApp(_h.Registry, AppState.DeviceScopeId, Never, 1, intent: 3, state: 5000, code: 1603);
        var run = _h.NewRun("diag0061", "DiagnoseApps");
        var lookup = new AppNameLookup();
        lookup.Names[Viewer] = "Contoso Viewer";
        lookup.Names[Agent] = "Contoso Agent";
        lookup.FromCache.Add(Agent);
        var handler = new DiagnoseAppsHandler { Parts = new DiagnosisParts { Names = (_, _) => lookup, Attempts = (_, _) => new ImeAttemptRead { Reason = "test" } } };
        Assert.Equal(EngineExitCode.Success, handler.Run(run));
        var apps = _h.Records("app-diagnosis").ToDictionary(a => a.Str("app"));
        Assert.Equal("log", apps[Viewer].Str("namesource"));
        Assert.Equal("cache", apps[Agent].Str("namesource"));
        Assert.Equal("Contoso Agent", apps[Agent].Str("name"));
        Assert.Equal("", apps[Never].Str("namesource"));
        Assert.Equal("", apps[Never].Str("name"));
    }

    [Fact]
    public void The_user_sees_the_same_name_and_IT_reads_that_it_came_from_an_earlier_IME_log()
    {
        AppFinding Finding(string name, string source) => new()
        {
            Title = (string.IsNullOrEmpty(name) ? "App " + Agent.Substring(0, 8) : name) + " failed to install",
            App = new AppEvidence { AppId = Agent, Name = name, NameSource = source, Scope = "device", Revision = 1, Verdict = "RETRY-HELPS" },
        };
        var cached = Finding("Contoso Agent", "cache");
        var fromLog = Finding("Contoso Agent", "log");
        Assert.Equal(fromLog.App.Label, cached.App.Label);
        Assert.Equal(fromLog.Card().Title, cached.Card().Title);
        Assert.EndsWith("Name from an earlier IME log.", cached.Card().Support);
        Assert.DoesNotContain("earlier IME log", fromLog.Card().Support);
        Assert.DoesNotContain("earlier IME log", cached.Card().Title + cached.Card().Detail + cached.Card().NextStep);
        // Still unknown: the card's fact stays, nothing about a cache; a cached name has no such fact.
        var unknown = Finding("", "");
        Assert.Contains(("Name", "not known on this device"), AppDecisions.DetailFacts(unknown));
        Assert.Equal("Open in Company Portal to see it", AppDecisions.PortalLinkText(unknown));
        Assert.DoesNotContain("earlier IME log", unknown.Card().Support);
        Assert.DoesNotContain(AppDecisions.DetailFacts(cached), f => f.Label == "Name");
        Assert.Equal("Open in Company Portal", AppDecisions.PortalLinkText(cached));
    }

    [Fact]
    public void The_service_fills_the_cache_from_the_log_text_its_check_in_read_already_loaded()
    {
        var reported = new List<(string Id, string Name)>();
        var reader = new ImeCheckInReader(() => _folder.Path, () => null, () => T0, (seen, _) => reported.AddRange(seen));
        reader.ReportPolicyNames(new[] { "noise\n" + PolicyLine((Viewer, "Contoso Viewer")), "other\n" + PolicyLine((Agent, "Contoso Agent")) }, T0);
        Assert.Equal(new[] { (Agent, "Contoso Agent"), (Viewer, "Contoso Viewer") }, reported.OrderBy(r => r.Name));
        // Nothing reported without a policy line, and a failing cache never reaches the check-in.
        reported.Clear();
        reader.ReportPolicyNames(new[] { "no policies" }, T0);
        Assert.Empty(reported);
        var failing = new ImeCheckInReader(() => _folder.Path, () => null, () => T0, (_, _) => throw new IOException("disk full"));
        failing.ReportPolicyNames(new[] { PolicyLine((Viewer, "Contoso Viewer")) }, T0);
        // The cache lives directly in the SYSTEM-only working root, and the engine gets it in its request.
        var paths = new ServicePaths(_folder.Path, _folder.Combine("Engine"));
        Assert.Equal(Path.Combine(paths.WorkingRoot, "AppNames.json"), paths.AppNameCacheFile);
    }

    [Fact]
    public void The_hourly_read_runs_at_most_once_an_hour_by_the_clock_and_fills_the_cache()
    {
        var now = T0;
        var reads = 0;
        var collector = new AppNameCollector(() => _folder.Path, CachePath, Trusted, _ => { }, () => now, folder =>
        {
            reads++;
            Assert.Equal(_folder.Path, folder);
            return new Dictionary<string, string> { [Viewer] = "Contoso Viewer" };
        });
        Assert.True(collector.TryCollect());
        Assert.True(AppNameCache.Load(CachePath).TryGet(Viewer, out var name));
        Assert.Equal("Contoso Viewer", name);
        // The timer asks every few minutes; within the hour nothing is read.
        foreach (var minutes in new[] { 5, 30, 59 })
        {
            now = T0.AddMinutes(minutes);
            Assert.False(collector.TryCollect());
        }
        Assert.Equal(1, reads);
        now = T0.AddHours(1);
        Assert.True(collector.TryCollect());
        Assert.Equal(2, reads);
        // A clock set back does not stop it for good.
        now = T0.AddHours(-5);
        Assert.True(collector.TryCollect());
        Assert.Equal(3, collector.Cycles);
    }

    [Fact]
    public void A_cycle_is_skipped_while_one_runs_and_a_failure_is_logged_once_and_never_thrown()
    {
        var now = T0;
        AppNameCollector collector = null;
        var nested = true;
        collector = new AppNameCollector(() => _folder.Path, CachePath, Trusted, _ => { }, () => now, _ =>
        {
            // A second ask while this read runs (the clock past the hour) is skipped.
            now = T0.AddHours(2);
            nested = collector.TryCollect();
            return new Dictionary<string, string>();
        });
        Assert.True(collector.TryCollect());
        Assert.False(nested);
        Assert.Equal(1, collector.Cycles);

        var warnings = new List<string>();
        var clock = T0;
        var fail = true;
        var failing = new AppNameCollector(() => _folder.Path, CachePath, Trusted, warnings.Add, () => clock, _ =>
            fail ? throw new UnauthorizedAccessException("denied") : new Dictionary<string, string> { [Agent] = "Contoso Agent" });
        Assert.True(failing.TryCollect());
        clock = T0.AddHours(1);
        Assert.True(failing.TryCollect());
        Assert.Single(warnings);
        Assert.Contains("tried again in an hour", warnings[0]);
        // A read that works again resets it: the next failure is logged again.
        fail = false;
        clock = T0.AddHours(2);
        Assert.True(failing.TryCollect());
        Assert.True(AppNameCache.Load(CachePath).TryGet(Agent, out _));
        fail = true;
        clock = T0.AddHours(3);
        Assert.True(failing.TryCollect());
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void The_shared_read_takes_the_newest_logs_policy_lines_only_and_the_newest_name()
    {
        var logs = Path.Combine(_folder.Path, "Logs");
        Directory.CreateDirectory(logs);
        var older = Path.Combine(logs, "AppWorkload-20261112-090000.log");
        File.WriteAllLines(older, new[] { PolicyLine((Viewer, "Contoso Viewer 1")), "Other line {\"Id\":\"" + Agent + "\",\"Name\":\"Not a policy line\"}" });
        File.SetLastWriteTimeUtc(older, T0.AddDays(-1));
        File.WriteAllLines(Path.Combine(logs, "AppWorkload.log"), new[] { PolicyLine((Viewer, "Contoso Viewer 2")) });
        File.WriteAllLines(Path.Combine(logs, "unrelated.log"), new[] { PolicyLine((Agent, "Contoso Agent")) });
        var seen = AppNameCache.ReadPolicyNames(logs);
        Assert.Equal("Contoso Viewer 2", seen[Viewer]);
        Assert.False(seen.ContainsKey(Agent));
        // Only the newest logs: with one file allowed, the older one is not read.
        File.WriteAllLines(older, new[] { PolicyLine((Agent, "Contoso Agent")) });
        File.SetLastWriteTimeUtc(older, T0.AddDays(-1));
        Assert.False(AppNameCache.ReadPolicyNames(logs, maxLogFiles: 1).ContainsKey(Agent));
        Assert.True(AppNameCache.ReadPolicyNames(logs).ContainsKey(Agent));
        Assert.Empty(AppNameCache.ReadPolicyNames(Path.Combine(_folder.Path, "missing")));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\config\systemprofile\AppData\Local\EndpointToolbelt\AppNames.json", true)]
    [InlineData(@"C:\Temp\other.json", false)]
    [InlineData(@"AppNames.json", false)]
    [InlineData(@"\\server\share\AppNames.json", false)]
    [InlineData("", false)]
    public void The_engine_takes_the_cache_path_only_when_absolute_local_and_named_as_the_cache(string text, bool taken)
    {
        Assert.Equal(taken, RunFrame.AppNameCachePath(text).Length > 0);
    }

    [Fact]
    public void The_support_package_names_the_cache_by_count_and_newest_date_only()
    {
        var cache = new AppNameCache();
        cache.Observe(Viewer, "Contoso Viewer", T0.AddDays(-3));
        cache.Observe(Agent, "Contoso Agent", T0);
        var line = AppState.CacheSummary(cache);
        Assert.Equal("App name cache: 2 names, newest seen 2026-11-13 (UTC); the names are not listed here.", line);
        Assert.DoesNotContain("Contoso", line);
        Assert.Equal("App name cache: empty.", AppState.CacheSummary(new AppNameCache()));
    }
}
