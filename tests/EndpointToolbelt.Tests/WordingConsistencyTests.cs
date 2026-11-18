using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// One set of words for buttons, rows and severities.
/// No tool button reads only "Run", and a warning has the same word
/// on Home, in a row's badge and in the count.
/// </summary>
public class WordingConsistencyTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Labels that mean the same on every page, each page with a handler of its own.</summary>
    // "Yes" and "Not tried" answer the question shown above them: the safe-mode question or the handover's
    // "Does it work now?". The two are never shown together (the handover does not ask after a safe-mode tool).
    private static readonly string[] SameMeaning = { "Cancel", "← Back", "Copy details for IT", "Yes", "Not tried" };

    private static XDocument MainWindowXaml()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return XDocument.Load(Path.Combine(dir.FullName, "src", "EndpointToolbelt.App", "Ui", "MainWindow.xaml"));
    }

    [Fact]
    public void Acceptance_6_no_tool_button_reads_only_Run_over_the_whole_catalog()
    {
        var bare = new[] { "Run", "Open", "Start", "OK", "Go" };
        foreach (var tool in ToolCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.ButtonText), tool.Name);
            Assert.DoesNotContain(tool.ButtonText, bare);
            // It names an action: a verb and what it acts on.
            Assert.True(tool.ButtonText.Split(' ').Length >= 2, tool.ButtonText);
            Assert.Equal(tool.ButtonText + ": " + tool.Name, tool.ButtonName);
        }
        // Each tool's button says something of its own, except the Office apps' "Start in safe mode"
        // (the row and the detail page name the app); every button's automation name is its own.
        var office = ToolCatalog.All.Where(t => t.Office != null).ToList();
        Assert.Equal(4, office.Count);
        Assert.All(office, t => Assert.Equal("Start in safe mode", t.ButtonText));
        var others = ToolCatalog.All.Where(t => t.Office == null).ToList();
        Assert.Equal(others.Count, others.Select(t => t.ButtonText).Distinct().Count());
        Assert.DoesNotContain("Start in safe mode", others.Select(t => t.ButtonText));
        Assert.Equal(ToolCatalog.All.Count, ToolCatalog.All.Select(t => t.ButtonName).Distinct().Count());
        // The text is in the catalog, not in the XAML.
        var buttons = MainWindowXaml().Descendants().Single(e => (string)e.Attribute(X + "Name") == "ToolRunButtons").Elements().ToList();
        Assert.All(buttons, b => Assert.Null(b.Attribute("Content")));
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            foreach (var tool in ToolCatalog.All)
            {
                // The detail page's button and the tool's row say the same.
                Assert.Equal(tool.ButtonText, MainWindowPagesTests.Get<Button>(window, tool.Button).Content);
                var row = new ToolRowView(tool, null, false, false, TimeZoneInfo.Utc, DateTime.UtcNow);
                Assert.Equal(tool.ButtonText, row.RunText);
                Assert.Equal(tool.ButtonName, row.RunName);
            }
        });
        // Examples.
        Assert.Equal("Reset OneDrive", ToolCatalog.ByButton("ResetOneDriveButton").ButtonText);
        Assert.Equal("Start in safe mode", ToolCatalog.ByButton("WordSafeButton").ButtonText);
        Assert.Equal("Check connections", ToolCatalog.ByButton("ConnectivityButton").ButtonText);
    }

    [Fact]
    public void Acceptance_7_a_warning_has_the_same_word_on_Home_in_the_badge_and_in_the_count()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, CompactLayoutTests.Health(warn: HealthChecks.PendingRestart), new DsregInfo(), false);
            var home = MainWindowPagesTests.Get<TextBlock>(window, "SeverityWord").Text;
            MainWindowPagesTests.Call(window, "OnHealthToggle", window, new RoutedEventArgs());
            var rows = ((IEnumerable<HealthRow>)MainWindowPagesTests.Get<ItemsControl>(window, "HealthList").ItemsSource).ToList();
            var badge = rows.Single(r => r.Name == "Restart").BadgeWord;
            var count = MainWindowPagesTests.Get<TextBlock>(window, "HealthVerdict").Text;
            Assert.Equal("WARNING", home);
            Assert.Equal("WARNING", badge);
            Assert.Contains("1 warning,", count);
            Assert.Equal(Words.WarningBadge, home);
            Assert.Equal(home, badge);
            Assert.Contains(Words.WarningCount(1), count);
            Assert.DoesNotContain("check", count, StringComparison.OrdinalIgnoreCase);
        });
        // One constant: every place takes it from Words.Warning.
        Assert.Equal("Warning", Words.Warning);
        Assert.Equal("2 warnings", Words.WarningCount(2));
        Assert.Equal("a warning", Words.AWarning);
        Assert.Equal(Words.WarningBadge, Words.ResultWord(Tone.Warning));
        Assert.Equal(Words.WarningBadge, FindingSeverityText.Word(FindingSeverity.Warning));
        Assert.Equal(Words.WarningBadge, ToolRuns.Word(RunVerdict.Check));
        Assert.Equal(Words.Warning, ToolRuns.Phrase(RunVerdict.Check));
        Assert.Equal(Words.Warning, new HealthResult { Status = HealthStatus.Warn }.StatusWord);
        Assert.Equal(Words.WarningBadge, HomeSummary.SeverityWord(new HomeFinding { Tone = Tone.Warning }));
    }

    [Fact]
    public void An_older_run_log_still_reads_as_a_warning()
    {
        string[] Log(string card) => new[] { "Ended     : 2026-11-14 10:00", "----- RESULT -----", card };
        Assert.Equal(RunVerdict.Check, ToolRuns.Parse(Log("[CHECK] Some items were in use")));
        Assert.Equal(RunVerdict.Check, ToolRuns.Parse(Log("[" + Words.WarningBadge + "] Some items were in use")));
    }

    [Fact]
    public void Rows_have_plain_names_on_screen_and_their_technical_name_in_the_detail_the_report_and_Copy_details_for_IT()
    {
        var rows = HealthView.Rows(CompactLayoutTests.Health());
        Assert.Equal(10, rows.Count);
        foreach (var row in rows)
        {
            var plain = HealthView.PlainName(row.Name);
            Assert.False(HomeSummary.HasTechnicalWords(plain), plain);
            var view = new HealthRow(row);
            Assert.Equal(plain, view.Name);
            if (plain != row.Name) { Assert.StartsWith(row.Name + ": ", view.Detail); }
            else { Assert.Equal(row.Detail, view.Detail); }
        }
        Assert.Equal("Work app installer", HealthView.PlainName(HealthView.AppInstaller));
        Assert.Equal("Connection to work services", HealthView.PlainName(HealthView.Network));
        Assert.Equal("Link to your organization", HealthView.PlainName(HealthView.IntuneConnection));
        Assert.Equal("Work settings", HealthView.PlainName(HealthChecks.Profiles));
        Assert.Equal("Restart", HealthView.PlainName(HealthChecks.PendingRestart));
        // Copy details for IT keeps the technical names.
        var warn = HealthView.Rows(CompactLayoutTests.Health(warn: HealthChecks.Connectivity));
        Assert.Contains("1 warning: Network to Intune", HealthView.OneLine(warn));
        // The retry action has one name everywhere: Home's step, a row's link, the app page's button.
        Assert.Equal("Retry work apps and settings", Words.FixText(HealthFix.RetryAppsAndPolicies));
        Assert.Equal("Retry work apps and settings", Words.FixLink(HealthFix.RetryAppsAndPolicies));
        Assert.DoesNotContain("policies", Words.RetryStepHelp);
        Assert.False(HomeSummary.HasTechnicalWords(Words.RetryStep));
        var ime = new HealthResult { Name = HealthView.AppInstaller, Status = HealthStatus.Warn, Detail = "x" };
        Assert.Equal(Words.RetryStep, HomeSummary.Build(new[] { ime }, null, false).StepText);
        Assert.Equal(Words.RetryStep, new HealthRow(ime).FixText);
        Assert.Equal(Words.RetryStepHelp, new HealthRow(ime).FixHelp);
    }

    [Fact]
    public void M5_every_button_that_reads_again_says_what_it_reads()
    {
        var xaml = MainWindowXaml();
        string Label(string name) => ((string)xaml.Descendants().Single(e => (string)e.Attribute(X + "Name") == name).Attribute("Content")).Replace("_", "");
        var labels = new Dictionary<string, string>
        {
            ["RefreshButton"] = "Check this PC again",
            ["CheckAgainButton"] = "Check this app again",
            ["ReloadAppsButton"] = "Reload the app list",
            ["RefreshWindowsButton"] = "Refresh the window list",
            ["RefreshAdaptersButton"] = "Refresh the adapter list",
        };
        foreach (var pair in labels) { Assert.Equal(pair.Value, Label(pair.Key)); }
        // Home's step for an app says the same as the app page's button it stands for.
        Assert.Equal(Label("CheckAgainButton"), HomeSummary.StepText(AppNextStep.CheckAgain));
        // No two buttons of the window share a label and a different handler (Cancel and Back go back on each page; Copy details for IT copies the case file of the page it is on).
        var clashes = xaml.Descendants()
            .Where(e => e.Name.LocalName == "Button" && e.Attribute("Content") != null && e.Attribute("Click") != null)
            .GroupBy(e => ((string)e.Attribute("Content")).Replace("_", ""))
            .Where(g => !SameMeaning.Contains(g.Key) && g.Select(e => (string)e.Attribute("Click")).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(clashes);
        // The tools' buttons do not take a label of another button.
        var others = xaml.Descendants().Where(e => e.Name.LocalName == "Button" && e.Attribute("Content") != null).Select(e => ((string)e.Attribute("Content")).Replace("_", "")).ToList();
        Assert.Empty(ToolCatalog.All.Select(t => t.ButtonText).Intersect(others));
    }

    [Fact]
    public void Every_action_has_one_name_wherever_it_shows()
    {
        var xaml = MainWindowXaml();
        string Label(string name) => ((string)xaml.Descendants().Single(e => (string)e.Attribute(X + "Name") == name).Attribute("Content")).Replace("_", "");
        // The retry action: the app page's button, Home's step, a health row's link.
        Assert.Equal(Words.RetryStep, Label("RetryButton"));
        Assert.Equal(Words.RetryStep, HomeSummary.StepText(AppNextStep.RetryAppsAndPolicies));
        Assert.Equal(Words.RetryStep, Words.FixLink(HealthFix.RetryAppsAndPolicies));
        Assert.Equal(Words.RetryStep, Words.FixText(HealthFix.RetryAppsAndPolicies));
        // A health row's link names the tool it presses by that tool's own name.
        foreach (var fix in new[] { HealthFix.ConnectivityTest, HealthFix.SyncClock, HealthFix.UpdateDefender, HealthFix.FreeDiskSpace, HealthFix.RefreshSignIn })
        {
            Assert.Equal(ToolCatalog.ForFix(fix).Name, Words.FixText(fix));
        }
        // The names they replaced are nowhere in the product: not in the code, the window or the engine.
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        var retired = new[] { "Retry apps & policies", "Retry apps &amp; policies", "Copy for IT" };
        var hits = Directory.GetFiles(Path.Combine(dir.FullName, "src"), "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0)
            .SelectMany(p => retired.Where(r => File.ReadAllText(p).Contains(r)).Select(r => Path.GetFileName(p) + ": " + r))
            .ToList();
        Assert.Empty(hits);
    }
}
