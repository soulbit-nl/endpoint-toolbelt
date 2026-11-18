using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Protocol;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>The user-context tools of 1.0. No network, no real caches.</summary>
public class UserToolTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void WinHttp_proxy_settings_are_read_as_windows_writes_them()
    {
        Assert.Equal("proxy.contoso.example:8080", ProxyRules.Label(ProxyRules.ParseWinHttpProxy("proxy.contoso.example:8080")));
        Assert.Equal("secure.contoso.example:3128", ProxyRules.Label(ProxyRules.ParseWinHttpProxy("http=plain.contoso.example:80;https=secure.contoso.example:3128")));
        Assert.Null(ProxyRules.ParseWinHttpProxy("http=plain.contoso.example:80"));
        Assert.Null(ProxyRules.ParseWinHttpProxy(""));
        Assert.True(ProxyRules.Bypassed("intranet", "<local>"));
        Assert.True(ProxyRules.Bypassed("app.contoso.example", "*.contoso.example;<local>"));
        Assert.False(ProxyRules.Bypassed("manage.microsoft.com", "*.contoso.example;<local>"));
        Assert.False(ProxyRules.Bypassed("manage.microsoft.com", ""));
    }

    private static DsregInfo Dsreg(string joined, string prtUpdate)
        => DsregInfo.Parse(new[] { "AzureAdJoined : " + joined, "AzureAdPrt : YES", "AzureAdPrtUpdateTime : " + prtUpdate });

    [Fact]
    public void Sign_in_refresh_is_verified_by_the_token_update_time()
    {
        Assert.Equal(new DateTime(2026, 10, 16, 10, 12, 34, DateTimeKind.Utc), Dsreg("YES", "2026-10-16 10:12:34.000 UTC").PrtUpdatedUtc);
        Assert.Null(Dsreg("YES", "unknown").PrtUpdatedUtc);

        var calls = 0;
        var renewed = SignInRefresh.Run(() => calls++ < 2 ? Dsreg("YES", "2026-10-16 08:00:00.000 UTC") : Dsreg("YES", "2026-10-16 11:59:00.000 UTC"), () => (0, "PRT refresh scheduled."), _ => { });
        Assert.Equal(0, renewed.Code);
        Assert.Equal(Tone.Success, renewed.Tone);

        var stale = SignInRefresh.Run(() => Dsreg("YES", "2026-10-16 08:00:00.000 UTC"), () => (0, ""), _ => { });
        Assert.Equal(2, stale.Code);
        Assert.Contains("Lock and unlock", stale.Card.NextStep);

        Assert.Equal(4, SignInRefresh.Run(() => Dsreg("YES", ""), () => (87, "Invalid option"), _ => { }).Code);
        Assert.Equal(3, SignInRefresh.Run(() => Dsreg("NO", ""), () => throw new InvalidOperationException("must not run"), _ => { }).Code);
    }

    [Fact]
    public void A_later_expiry_counts_as_renewed_when_Windows_keeps_the_update_time()
    {
        // As on a real device: the token was renewed (later expiry), AzureAdPrtUpdateTime stayed.
        DsregInfo State(string expiry) => DsregInfo.Parse(new[]
        {
            "AzureAdJoined : YES", "AzureAdPrt : YES",
            "AzureAdPrtUpdateTime : 2026-01-05 08:00:00.000 UTC",
            "AzureAdPrtExpiryTime : " + expiry,
        });
        Assert.Equal(new DateTime(2026, 1, 19, 8, 0, 0, DateTimeKind.Utc), State("2026-01-19 08:00:00.000 UTC").PrtExpiresUtc);
        var calls = 0;
        var outcome = SignInRefresh.Run(() => calls++ < 2 ? State("2026-01-19 08:00:00.000 UTC") : State("2026-01-19 09:30:00.000 UTC"), () => (0, "PRT refresh scheduled."), _ => { });
        Assert.Equal(0, outcome.Code);
        Assert.StartsWith("Valid until ", outcome.Card.Detail);
        // Neither time moved: not confirmed, as before.
        Assert.Equal(2, SignInRefresh.Run(() => State("2026-01-19 08:00:00.000 UTC"), () => (0, ""), _ => { }).Code);
    }

    [Fact]
    public void Reset_sign_in_cache_clears_only_the_token_broker_accounts_and_waits_for_closed_apps()
    {
        using var t = new TestFolder();
        var profile = t.Path;
        var local = Path.Combine(profile, "AppData", "Local");
        var folders = UserCaches.SignInCacheFolders(local).ToList();
        foreach (var folder in folders) { Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "account.tbacct"), "x"); }
        var neighbour = Path.Combine(Path.GetDirectoryName(folders[0]), "Other.tbacct");
        File.WriteAllText(neighbour, "keep");

        Assert.Equal(5, UserCaches.ResetSignInCache(local, profile, new[] { "OUTLOOK" }, Now).Code);
        Assert.True(File.Exists(Path.Combine(folders[0], "account.tbacct")));

        var done = UserCaches.ResetSignInCache(local, profile, Array.Empty<string>(), Now);
        Assert.Equal(0, done.Code);
        Assert.Contains("2 cached account files removed", done.Card.Detail);
        Assert.All(folders, f => Assert.Empty(Directory.GetFiles(f)));
        Assert.True(File.Exists(neighbour));
        Assert.Equal(3, UserCaches.ResetSignInCache(local, profile, Array.Empty<string>(), Now).Code);
    }

    [Fact]
    public void A_cache_clear_with_files_in_use_is_OK_and_CHECK_only_when_most_of_it_stayed()
    {
        // The same rule for the sign-in cache and the Teams cache, with real files held open.
        using var t = new TestFolder();
        var profile = t.Path;
        var local = Path.Combine(profile, "AppData", "Local");
        var folder = UserCaches.SignInCacheFolders(local).First();
        Directory.CreateDirectory(folder);
        string Write(string dir, string name) { var p = Path.Combine(dir, name); File.WriteAllText(p, "x"); return p; }
        Write(folder, "a.tbacct");
        Write(folder, "b.tbacct");
        using (File.Open(Write(folder, "c.tbacct"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var some = UserCaches.ResetSignInCache(local, profile, Array.Empty<string>(), Now);
            Assert.Equal(0, some.Code);
            Assert.Equal(Tone.Success, some.Tone);
            Assert.Contains("1 item still in use by Windows sign-in stayed; that is normal.", some.Card.Detail);
        }
        Write(folder, "d.tbacct");
        using (File.Open(Write(folder, "e.tbacct"), FileMode.Open, FileAccess.Read, FileShare.None))
        using (File.Open(Write(folder, "f.tbacct"), FileMode.Open, FileAccess.Read, FileShare.None))
        using (File.Open(Write(folder, "g.tbacct"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var most = UserCaches.ResetSignInCache(local, profile, Array.Empty<string>(), Now);
            Assert.Equal(2, most.Code);
            Assert.Equal("Most of the cached sign-in could not be removed", most.Card.Title);
        }

        var cache = UserCaches.TeamsCacheFolder(local);
        Directory.CreateDirectory(cache);
        Write(cache, "1");
        Write(cache, "2");
        using (File.Open(Write(cache, "3"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var teams = ClearTeams(local, profile, TeamsRunning());
            Assert.Equal(0, teams.Code);
            Assert.StartsWith("You quit Teams. 2 files removed", teams.Card.Detail);
            Assert.EndsWith("stayed; that is normal.", teams.Card.Detail);
        }
        Write(cache, "4");
        // Teams still running after End Teams for me leaves part of the cache: a point to check.
        Assert.Equal(2, ClearTeams(local, profile, TeamsRunning(), end: () => 0, ask: _ => TeamsChoice.EndForMe).Code);
    }

    /// <summary>A fake new Teams that runs in this session.</summary>
    private static FakeApps TeamsRunning()
    {
        var apps = new FakeApps();
        apps.Running.Add(UserCaches.TeamsProcessName);
        return apps;
    }

    /// <summary>
    /// Clear Teams cache with a fake Teams. Unless the test answers itself, the user quits Teams and
    /// chooses Continue; ending Teams fails the test unless given.
    /// </summary>
    private static ToolOutcome ClearTeams(string local, string profile, ISessionApps apps, Func<int> end = null, Func<bool, TeamsChoice> ask = null, List<string> log = null)
        => UserCaches.ClearTeamsCache(local, profile, apps,
            end ?? (() => throw new InvalidOperationException("Teams must not be ended")),
            ask ?? (_ =>
            {
                if (apps is FakeApps fake) { fake.Running.Clear(); }
                return TeamsChoice.Continue;
            }),
            line => log?.Add(line), Now);

    /// <summary>A process list the test decides: an app asked to close leaves it, unless it is stubborn (a save prompt).</summary>
    private sealed class FakeApps : ISessionApps
    {
        public List<string> Running { get; } = new();
        public HashSet<string> Stubborn { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Asked { get; } = new();
        public int SleptMilliseconds { get; private set; }

        public IReadOnlyList<string> Open(IReadOnlyList<string> names) => names.Where(n => Running.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();

        public void AskToClose(string name)
        {
            Asked.Add(name);
            if (!Stubborn.Contains(name)) { Running.RemoveAll(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)); }
        }

        public void Sleep(int milliseconds) => SleptMilliseconds += milliseconds;
    }

    /// <summary>A profile in a folder of its own with one cached account file in each sign-in cache folder.</summary>
    private sealed class SignInProfile : IDisposable
    {
        private readonly TestFolder _t = new();

        public SignInProfile()
        {
            LocalAppData = System.IO.Path.Combine(_t.Path, "AppData", "Local");
            Folders = UserCaches.SignInCacheFolders(LocalAppData).ToList();
            foreach (var folder in Folders) { Directory.CreateDirectory(folder); File.WriteAllText(System.IO.Path.Combine(folder, "account.tbacct"), "x"); }
        }

        public string Path => _t.Path;
        public string LocalAppData { get; }
        public List<string> Folders { get; }

        public void Dispose() => _t.Dispose();
    }

    [Fact]
    public void Reset_sign_in_cache_asks_nothing_when_no_app_is_open()
    {
        using var t = new SignInProfile();
        var (local, folders) = (t.LocalAppData, t.Folders);
        var apps = new FakeApps();
        var lines = new List<string>();
        var done = UserCaches.ResetSignInCache(local, t.Path, apps, _ => throw new InvalidOperationException("must not ask"), lines.Add, Now);
        Assert.Equal(0, done.Code);
        Assert.StartsWith("2 cached account files removed", done.Card.Detail);
        Assert.Empty(apps.Asked);
        Assert.Empty(lines);
        Assert.All(folders, f => Assert.Empty(Directory.GetFiles(f)));
    }

    [Fact]
    public void Reset_sign_in_cache_changes_nothing_when_the_user_keeps_the_apps_open()
    {
        using var t = new SignInProfile();
        var (local, folders) = (t.LocalAppData, t.Folders);
        var apps = new FakeApps();
        apps.Running.Add("olk");
        IReadOnlyList<string> asked = null;
        var lines = new List<string>();
        var outcome = UserCaches.ResetSignInCache(local, t.Path, apps, names => { asked = names; return false; }, lines.Add, Now);
        Assert.Equal(new[] { "Outlook" }, asked);
        Assert.Equal(5, outcome.Code);
        Assert.Equal("Reset sign-in cache: cancelled. Nothing was changed.", outcome.Status);
        Assert.Contains("Still open: Outlook.", outcome.Card.Detail);
        Assert.DoesNotContain("olk", outcome.Status + outcome.Card.Title + outcome.Card.Detail + outcome.Card.NextStep);
        Assert.Empty(apps.Asked);
        Assert.Equal(new[] { "Microsoft 365 apps still open: Outlook.", "You chose not to close them. Nothing was changed." }, lines);
        Assert.All(folders, f => Assert.Single(Directory.GetFiles(f)));
    }

    [Fact]
    public void Reset_sign_in_cache_asks_the_apps_to_close_and_resets_once_all_closed()
    {
        using var t = new SignInProfile();
        var (local, folders) = (t.LocalAppData, t.Folders);
        var apps = new FakeApps();
        apps.Running.AddRange(new[] { "olk", "WINWORD", "OUTLOOK" });
        var lines = new List<string>();
        var done = UserCaches.ResetSignInCache(local, t.Path, apps, _ => true, lines.Add, Now);
        Assert.Equal(0, done.Code);
        Assert.Equal(new[] { "WINWORD", "OUTLOOK", "olk" }, apps.Asked);
        Assert.StartsWith("Word and Outlook were closed. 2 cached account files removed", done.Card.Detail);
        Assert.Equal(new[] { "Microsoft 365 apps still open: Word and Outlook.", "Asked to close: Word and Outlook.", "Closed: Word and Outlook." }, lines);
        Assert.All(folders, f => Assert.Empty(Directory.GetFiles(f)));
    }

    [Fact]
    public void Reset_sign_in_cache_stops_and_names_an_app_that_does_not_close_in_time()
    {
        using var t = new SignInProfile();
        var (local, folders) = (t.LocalAppData, t.Folders);
        var apps = new FakeApps();
        apps.Running.AddRange(new[] { "EXCEL", "olk" });
        apps.Stubborn.Add("EXCEL");
        var lines = new List<string>();
        var outcome = UserCaches.ResetSignInCache(local, t.Path, apps, _ => true, lines.Add, Now);
        Assert.Equal(5, outcome.Code);
        Assert.Equal(Tone.Warning, outcome.Tone);
        Assert.Equal("Reset sign-in cache: Excel did not close. Nothing was changed.", outcome.Status);
        Assert.Equal("Excel did not close", outcome.Card.Title);
        Assert.StartsWith("Outlook closed. Excel is still open after 20 seconds", outcome.Card.Detail);
        Assert.Contains("no app was closed by force", outcome.Card.Detail);
        Assert.Equal(UserCaches.CloseWaitSeconds * 1000, apps.SleptMilliseconds);
        // Still running: asked once, never ended.
        Assert.Contains("EXCEL", apps.Running);
        Assert.Equal(new[] { "EXCEL", "olk" }, apps.Asked);
        Assert.Equal("Still open after 20 seconds: Excel. Nothing was changed.", lines.Last());
        Assert.All(folders, f => Assert.Single(Directory.GetFiles(f)));
    }

    [Fact]
    public void Every_app_the_sign_in_check_knows_has_its_readable_name()
    {
        Assert.Equal(new[] { "Word", "Excel", "PowerPoint", "Outlook", "OneNote", "Access", "Teams" }, ProcessNames.Friendly(UserCaches.SignInApps));
        Assert.All(UserCaches.SignInApps, n => Assert.NotEqual(n, ProcessNames.Friendly(n)));
        Assert.Equal("Outlook", ProcessNames.Friendly("OLK"));
        Assert.Equal("contoso-agent", ProcessNames.Friendly("contoso-agent"));
        Assert.Equal("", Words.List(Array.Empty<string>()));
        Assert.Equal("Word", Words.List(new[] { "Word" }));
        Assert.Equal("Word, Excel and Outlook", Words.List(new[] { "Word", "Excel", "Outlook" }));
        // The older guard names the apps the same way.
        Assert.Equal("Reset sign-in cache: close Outlook and Teams first.", UserCaches.ResetSignInCache("x", "x", new[] { "olk", "ms-teams" }, Now).Status);
        // The question names the apps, warns about unsaved work, and its button's access key differs from Cancel's.
        var body = Confirmations.CloseApps(new[] { "Outlook" });
        Assert.StartsWith("Reset sign-in cache needs this app closed: Outlook.", body);
        Assert.Contains("no app is closed by force", body);
        Assert.Contains("Unsaved work", Confirmations.CloseAppsWarning);
        Assert.Equal('l', char.ToLowerInvariant(Confirmations.CloseAppsButton[Confirmations.CloseAppsButton.IndexOf('_') + 1]));
    }

    [Fact]
    public void Clear_Teams_cache_closes_Teams_and_empties_only_its_cache()
    {
        using var t = new TestFolder();
        var profile = t.Path;
        var local = Path.Combine(profile, "AppData", "Local");
        // Not installed: Teams is not asked anything.
        var notInstalled = new FakeApps();
        notInstalled.Running.Add(UserCaches.TeamsProcessName);
        Assert.Equal(3, ClearTeams(local, profile, notInstalled).Code);
        Assert.Empty(notInstalled.Asked);

        var cache = UserCaches.TeamsCacheFolder(local);
        Directory.CreateDirectory(Path.Combine(cache, "Cache"));
        File.WriteAllText(Path.Combine(cache, "Cache", "data_1"), new string('x', 4096));
        var settings = Path.Combine(local, "Packages", "MSTeams_8wekyb3d8bbwe", "Settings");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "settings.dat"), "keep");
        var apps = TeamsRunning();
        var done = ClearTeams(local, profile, apps);
        Assert.Equal(0, done.Code);
        // The app never asks Teams to close: the user quits it.
        Assert.Empty(apps.Asked);
        Assert.StartsWith("You quit Teams. 1 file removed", done.Card.Detail);
        Assert.True(Directory.Exists(cache));
        Assert.Empty(Directory.GetFileSystemEntries(cache));
        Assert.True(File.Exists(Path.Combine(settings, "settings.dat")));
    }

    /// <summary>A Teams profile with one cache file, for the Teams paths.</summary>
    private static (string Local, string Cache) TeamsProfile(TestFolder t)
    {
        var local = Path.Combine(t.Path, "AppData", "Local");
        var cache = UserCaches.TeamsCacheFolder(local);
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "data_1"), "x");
        return (local, cache);
    }

    [Fact]
    public void The_user_quits_Teams_and_Continue_clears_the_cache_without_the_app_closing_anything()
    {
        using var t = new TestFolder();
        var (local, cache) = TeamsProfile(t);
        var apps = TeamsRunning();
        var log = new List<string>();
        var done = ClearTeams(local, t.Path, apps, log: log);
        Assert.Equal(0, done.Code);
        Assert.Empty(apps.Asked);
        Assert.Equal(new[] { "Teams is running. You were asked to quit it.", "You quit Teams." }, log);
        Assert.Empty(Directory.GetFileSystemEntries(cache));
        Assert.StartsWith("You quit Teams.", done.Card.Detail);
    }

    [Fact]
    public void Continue_while_Teams_still_runs_asks_again_and_End_Teams_for_me_ends_it()
    {
        using var t = new TestFolder();
        var (local, cache) = TeamsProfile(t);
        var apps = TeamsRunning();
        var log = new List<string>();
        var asked = new List<bool>();
        var ended = 0;
        var done = ClearTeams(local, t.Path, apps,
            end: () => { ended++; apps.Running.Clear(); return 2; },
            ask: again => { asked.Add(again); return asked.Count == 1 ? TeamsChoice.Continue : TeamsChoice.EndForMe; }, log: log);
        // First the question, then after Continue the same question saying Teams still runs.
        Assert.Equal(new[] { false, true }, asked);
        Assert.Equal(1, ended);
        Assert.Equal(0, done.Code);
        Assert.Empty(apps.Asked);
        Assert.Equal(new[] { "Teams is running. You were asked to quit it.", "Teams was still running after Continue.", "Teams was ended because you chose End Teams for me." }, log);
        Assert.StartsWith("Teams was ended because you chose End Teams for me. 1 file removed", done.Card.Detail);
        Assert.Empty(Directory.GetFileSystemEntries(cache));
    }

    [Fact]
    public void Cancel_while_Teams_runs_clears_nothing_ends_nothing_and_says_how_to_quit_Teams()
    {
        using var t = new TestFolder();
        var (local, cache) = TeamsProfile(t);
        var apps = TeamsRunning();
        var log = new List<string>();
        var asked = 0;
        var done = ClearTeams(local, t.Path, apps, ask: _ => { asked++; return TeamsChoice.Cancel; }, log: log);
        Assert.Equal(1, asked);
        Assert.Equal(UserCaches.TeamsCancelledCode, done.Code);
        Assert.Equal(Tone.Neutral, done.Tone);
        Assert.Equal("Clear Teams cache: cancelled. Nothing was changed.", done.Status);
        Assert.Equal("Nothing was changed", done.Card.Title);
        Assert.Contains("right-click its icon in the notification area and choose Quit", done.Card.NextStep);
        Assert.Equal("You chose Cancel. Teams was not closed and nothing was cleared.", log.Last());
        Assert.True(File.Exists(Path.Combine(cache, "data_1")));
        Assert.Contains(UserCaches.TeamsProcessName, apps.Running);
        Assert.Equal("2026-10-15 14:02 +02:00  Clear Teams cache: apps still open (5)", RunWords.HistoryLine("2026-10-15 14:02:11 +02:00;user;ClearTeamsCache;5"));
    }

    [Fact]
    public void Teams_not_running_is_neither_asked_nor_ended()
    {
        using var t = new TestFolder();
        var (local, cache) = TeamsProfile(t);
        var apps = new FakeApps();
        var log = new List<string>();
        var done = ClearTeams(local, t.Path, apps, log: log);
        Assert.Equal(0, done.Code);
        Assert.Empty(apps.Asked);
        Assert.Equal(new[] { "Teams was not running." }, log);
        Assert.Equal("1 file removed (0 MB).", done.Card.Detail);
        Assert.Empty(Directory.GetFileSystemEntries(cache));
    }

    [Fact]
    public void The_confirmations_name_the_call_or_meeting_and_the_way_to_quit()
    {
        Assert.Equal("A call or meeting in Teams ends when Teams is quit or ended.", Confirmations.TeamsCacheWarning);
        Assert.Contains("you are asked to quit it yourself, or you can let this app end it", Confirmations.TeamsCache);
        Assert.Equal("Teams is running", Confirmations.TeamsRunningHeading);
        Assert.Contains("right-click its icon in the notification area and choose Quit. Then choose Continue.", Confirmations.TeamsRunning);
        Assert.StartsWith("Teams is still running.", Confirmations.TeamsStillRunning);
        Assert.Contains("a call or meeting in Teams ends", Confirmations.TeamsRunningWarning);
        Assert.Contains("Cancel clears nothing", Confirmations.TeamsRunningWarning);
        Assert.Equal("C_ontinue", Confirmations.TeamsContinueButton);
        Assert.Equal("_End Teams for me", Confirmations.TeamsEndForMeButton);
    }

    [Fact]
    public void A_Teams_cache_clear_with_some_files_in_use_is_OK_and_says_so_in_plain_words()
    {
        // A real case: Teams closed, 4275 files removed, 58 in use by its helpers.
        var done = UserCaches.ClearTeamsCacheOutcome(TeamsClose.QuitByUser, removed: 4275, inUse: 58, bytesFreed: 833_754_316L);
        Assert.Equal(0, done.Code);
        Assert.Equal(Tone.Success, done.Tone);
        Assert.Equal(CardKind.Ok, done.Card.Kind);
        Assert.Equal("Clear Teams cache: done. Start Teams again.", done.Status);
        Assert.Equal("You quit Teams. 4275 files removed (795.1 MB). 58 items still in use by Teams helpers, the Teams add-in of Outlook or WebView2 stayed; that is normal.", done.Card.Detail);
        // Nothing in use, and Teams was not running: OK too.
        var clean = UserCaches.ClearTeamsCacheOutcome(TeamsClose.NotOpen, 10, 0, 1048576);
        Assert.Equal(0, clean.Code);
        Assert.Equal("10 files removed (1 MB).", clean.Card.Detail);
    }

    [Fact]
    public void A_Teams_cache_clear_is_CHECK_only_when_Teams_stayed_open_or_most_of_the_cache_stayed()
    {
        var open = UserCaches.ClearTeamsCacheOutcome(TeamsClose.StillOpen, removed: 4000, inUse: 10, bytesFreed: 1048576);
        Assert.Equal(2, open.Code);
        Assert.Equal(Tone.Warning, open.Tone);
        Assert.Equal("Teams could not be closed", open.Card.Title);
        // Less than half removed.
        var most = UserCaches.ClearTeamsCacheOutcome(TeamsClose.Ended, removed: 40, inUse: 41, bytesFreed: 1048576);
        Assert.Equal(2, most.Code);
        Assert.Equal("Most of the Teams cache could not be removed", most.Card.Title);
        // Exactly half removed is still OK.
        Assert.Equal(0, UserCaches.ClearTeamsCacheOutcome(TeamsClose.QuitByUser, 41, 41, 1048576).Code);
        Assert.True(CleanupRule.MostLeft(0, 1));
        Assert.False(CleanupRule.MostLeft(0, 0));
        Assert.False(CleanupRule.MostLeft(5, 5));
    }

    [Fact]
    public void The_sign_in_row_links_to_the_refresh_and_the_history_names_the_tools()
    {
        Assert.Equal(HealthFix.RefreshSignIn, new HealthResult { Name = HealthChecks.Prt, Status = HealthStatus.Warn }.Fix);
        Assert.Equal("Refresh work account sign-in", Words.FixLink(HealthFix.RefreshSignIn));
        Assert.Equal("2026-10-15 14:02 +02:00  Clear Teams cache: done (0)", RunWords.HistoryLine("2026-10-15 14:02:11 +02:00;user;ClearTeamsCache;0"));
        Assert.Equal("2026-10-15 14:02 +02:00  Refresh work account sign-in: not confirmed (2)", RunWords.HistoryLine("2026-10-15 14:02:11 +02:00;user;RefreshSignIn;2"));
        Assert.False(ButtonRules.NeedsService(AppButton.RefreshSignIn));
        Assert.False(ButtonRules.NeedsService(AppButton.TeamsCache));
    }
}
