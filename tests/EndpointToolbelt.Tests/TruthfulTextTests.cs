using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EndpointToolbelt.App.Ui;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>Texts that claim only what is true.</summary>
public sealed class TruthfulTextTests
{
    // A second user on a shared device sees no "last run" of the first
    // user, and the text says the history is their own.
    [Fact]
    public void A_second_user_sees_no_run_of_the_first_and_the_page_says_the_history_is_their_own()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            using var first = new TestFolder();
            using var second = new TestFolder();
            MainWindowPagesTests.WriteRun(first.Combine("20261112-090000Z_SyncClock_ab12cd34.log"), true, "[OK] The clock was synchronised");

            // The first user's own history: the run is there.
            var mine = MainWindowPagesTests.NewWindow();
            mine.RunsFolderOverride = first.Path;
            MainWindowPagesTests.Call(mine, "ShowPage", MainWindowPagesTests.PageValue("It"), false);
            Assert.Equal("7 tools · last run OK", MainWindowPagesTests.Get<TextBlock>(mine, "WindowsTileSummary").Text);

            // The second user's history is another folder (their own %LOCALAPPDATA%): nothing of the first.
            var theirs = MainWindowPagesTests.NewWindow();
            theirs.RunsFolderOverride = second.Path;
            MainWindowPagesTests.Call(theirs, "ShowPage", MainWindowPagesTests.PageValue("It"), false);
            Assert.Equal("7 tools", MainWindowPagesTests.Get<TextBlock>(theirs, "WindowsTileSummary").Text);
            Assert.Equal("No runs in your history yet.", MainWindowPagesTests.Get<TextBlock>(theirs, "NoRecentRuns").Text);
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<TextBlock>(theirs, "NoRecentRuns").Visibility);

            MainWindowPagesTests.Call(theirs, "OnCategoryTile", MainWindowPagesTests.Get<Button>(theirs, "WindowsTile"), new RoutedEventArgs());
            var sync = MainWindowPagesTests.ToolRows(theirs).Single(r => r.Name == "Sync clock");
            Assert.Equal("Not run yet", sync.ResultText);
            MainWindowPagesTests.Call(theirs, "OnToolRow", new Button { DataContext = sync }, new RoutedEventArgs());
            Assert.StartsWith("It has not run in your history yet", MainWindowPagesTests.Get<TextBlock>(theirs, "ToolLastRunText").Text);

            // No text on the page claims the history is the device's.
            var texts = TextsOf(theirs).ToList();
            Assert.Contains("Last run in your history", texts);
            Assert.DoesNotContain(texts, t => t.Contains("on this device yet") || t == "Last run on this device");
        });
    }

    private static IEnumerable<string> TextsOf(DependencyObject root)
    {
        if (root is TextBlock block && !string.IsNullOrEmpty(block.Text)) { yield return block.Text; }
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var text in TextsOf(child)) { yield return text; }
        }
    }
}
