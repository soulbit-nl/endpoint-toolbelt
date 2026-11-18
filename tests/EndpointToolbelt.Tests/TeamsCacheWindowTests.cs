using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EndpointToolbelt.App.Core;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// Clear Teams cache in the window, with a fake Teams process. The app never closes Teams on its
/// own: Cancel at the first question asks nothing; while Teams runs the user quits it and chooses
/// Continue, chooses End Teams for me, or cancels, and Cancel clears nothing.
/// </summary>
public class TeamsCacheWindowTests
{
    /// <summary>A fake new Teams that runs until the user quits it or it is ended.</summary>
    private sealed class FakeTeams : ISessionApps
    {
        public bool Running { get; set; } = true;
        public int Asked { get; private set; }
        public int Ended { get; private set; }

        public IReadOnlyList<string> Open(IReadOnlyList<string> names)
            => Running && names.Contains(UserCaches.TeamsProcessName) ? new[] { UserCaches.TeamsProcessName } : Array.Empty<string>();

        public void AskToClose(string name) => Asked++;

        public void Sleep(int milliseconds) { }

        public int End()
        {
            Ended++;
            Running = false;
            return 1;
        }
    }

    private static (string Local, string CacheFile) Profile(TestFolder t)
    {
        var local = Path.Combine(t.Path, "AppData", "Local");
        var cache = UserCaches.TeamsCacheFolder(local);
        Directory.CreateDirectory(cache);
        var file = Path.Combine(cache, "data_1");
        File.WriteAllText(file, "x");
        return (local, file);
    }

    [Fact]
    public void Cancel_at_the_first_question_asks_nothing_and_records_nothing()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            using var t = new TestFolder();
            var (local, file) = Profile(t);
            var window = MainWindowPagesTests.NewWindow();
            var teams = new FakeTeams();
            window.TeamsApps = teams;
            window.TeamsLocalAppData = local;
            window.EndTeamsProcesses = teams.End;
            var asked = 0;
            window.ConfirmTeamsCache = () => { asked++; return false; };
            window.AskTeamsRunning = _ => throw new InvalidOperationException("no question about Teams expected");
            var status = MainWindowPagesTests.Get<TextBlock>(window, "StatusText").Text;
            MainWindowPagesTests.Call(window, "OnTeamsCache", window, new RoutedEventArgs());
            MainWindowPagesTests.Drain();
            Assert.Equal(1, asked);
            Assert.Equal(0, teams.Asked);
            Assert.Equal(0, teams.Ended);
            Assert.True(File.Exists(file));
            Assert.Equal(status, MainWindowPagesTests.Get<TextBlock>(window, "StatusText").Text);
            Assert.False(File.Exists(window.HistoryOverride));
        });
    }

    [Fact]
    public void Teams_running_and_Cancel_clears_nothing_and_ends_nothing()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            using var t = new TestFolder();
            var (local, file) = Profile(t);
            var window = MainWindowPagesTests.NewWindow();
            var teams = new FakeTeams();
            window.TeamsApps = teams;
            window.TeamsLocalAppData = local;
            window.EndTeamsProcesses = teams.End;
            window.ConfirmTeamsCache = () => true;
            var second = 0;
            window.AskTeamsRunning = _ => { second++; return TeamsChoice.Cancel; };
            MainWindowPagesTests.Call(window, "OnTeamsCache", window, new RoutedEventArgs());
            MainWindowPagesTests.WaitFor(() => !(bool)MainWindowPagesTests.Field(window, "_busy"));
            Assert.Equal(0, teams.Asked);
            Assert.Equal(1, second);
            Assert.Equal(0, teams.Ended);
            Assert.True(teams.Running);
            Assert.True(File.Exists(file));
            Assert.Equal("Clear Teams cache: cancelled. Nothing was changed.", MainWindowPagesTests.Get<TextBlock>(window, "StatusText").Text);
            Assert.EndsWith(";ClearTeamsCache;5", Assert.Single(File.ReadAllLines(window.HistoryOverride)).Split('|')[0]);
        });
    }

    [Fact]
    public void Teams_running_and_End_Teams_for_me_ends_it_and_clears_the_cache()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            using var t = new TestFolder();
            var (local, file) = Profile(t);
            var window = MainWindowPagesTests.NewWindow();
            var teams = new FakeTeams();
            window.TeamsApps = teams;
            window.TeamsLocalAppData = local;
            window.EndTeamsProcesses = teams.End;
            window.ConfirmTeamsCache = () => true;
            window.AskTeamsRunning = _ => TeamsChoice.EndForMe;
            MainWindowPagesTests.Call(window, "OnTeamsCache", window, new RoutedEventArgs());
            MainWindowPagesTests.WaitFor(() => !(bool)MainWindowPagesTests.Field(window, "_busy"));
            Assert.Equal(0, teams.Asked);
            Assert.Equal(1, teams.Ended);
            Assert.False(File.Exists(file));
            Assert.Equal("Clear Teams cache: done. Start Teams again.", MainWindowPagesTests.Get<TextBlock>(window, "StatusText").Text);
        });
    }

    [Fact]
    public void Teams_the_user_quits_is_never_ended_and_Continue_clears_the_cache()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            using var t = new TestFolder();
            var (local, file) = Profile(t);
            var window = MainWindowPagesTests.NewWindow();
            var teams = new FakeTeams();
            window.TeamsApps = teams;
            window.TeamsLocalAppData = local;
            window.EndTeamsProcesses = teams.End;
            window.ConfirmTeamsCache = () => true;
            window.AskTeamsRunning = _ =>
            {
                teams.Running = false;
                return TeamsChoice.Continue;
            };
            MainWindowPagesTests.Call(window, "OnTeamsCache", window, new RoutedEventArgs());
            MainWindowPagesTests.WaitFor(() => !(bool)MainWindowPagesTests.Field(window, "_busy"));
            Assert.Equal(0, teams.Asked);
            Assert.Equal(0, teams.Ended);
            Assert.False(File.Exists(file));
            Assert.Equal("Clear Teams cache: done. Start Teams again.", MainWindowPagesTests.Get<TextBlock>(window, "StatusText").Text);
        });
    }
}
