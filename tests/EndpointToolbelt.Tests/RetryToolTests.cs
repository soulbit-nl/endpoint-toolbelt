using System;
using System.Linq;
using EndpointToolbelt.App.Core;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// Retry work apps and settings is also a tool of For IT support, first in the group Apps, so
/// it can be pressed at once. It stays on the app path, cannot be switched off, and its page says
/// in one sentence how the work apps stood at the last diagnosis.
/// </summary>
public class RetryToolTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    private static readonly DateTime Now = new(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Read = new(2026, 11, 25, 8, 12, 0, DateTimeKind.Utc);

    private static ToolInfo Tool => ToolCatalog.ByButton(RetryTool.Button);

    private static AppDiagnosis Finished(params (string Name, string Verdict)[] apps)
    {
        var diagnosis = new AppDiagnosis { Run = DiagnosisRunState.Finished, CapturedUtc = Read };
        foreach (var (name, verdict) in apps)
        {
            diagnosis.Apps.Add(new AppFinding { App = new AppEvidence { Name = name, Verdict = verdict, AppId = "11111111-2222-3333-4444-555555555555" }, Case = AppCase.InstallFailed, Observation = verdict == "OK" ? AppObservation.Detected : AppObservation.Failed });
        }
        return diagnosis;
    }

    private static (string, string)[] Ok(int count) => Enumerable.Range(1, count).Select(i => ("Contoso App " + i, "OK")).ToArray();

    [Fact]
    public void It_is_the_first_tool_of_Apps_runs_the_retry_and_is_no_IT_only_tool()
    {
        var apps = ToolCatalog.InGroup(ToolGroup.Apps);
        Assert.Same(Tool, apps[0]);
        Assert.Equal(5, apps.Count);
        Assert.Equal("Retry work apps and settings", Tool.Name);
        Assert.Equal("Retry apps and settings", Tool.ButtonText);
        Assert.Equal(ToolbeltAction.RetryAppsAndPolicies, Tool.Action);
        Assert.Same(Tool, ToolCatalog.ByAction(ToolbeltAction.RetryAppsAndPolicies));
        Assert.False(ToolCatalog.ForItOnly(Tool));
        Assert.DoesNotContain(ToolCatalog.ItOnlyNote, Tool.Line, StringComparison.Ordinal);
        Assert.Contains(Tool, ToolCatalog.Search("retry"));
    }

    [Fact]
    public void No_policy_value_switches_it_off()
    {
        var warnings = new System.Collections.Generic.List<string>();
        Assert.Empty(ToolPolicy.Parse("RetryAppsAndPolicies", warnings));
        Assert.Single(warnings);
        // Also a list that names it anyway, and every tool of the policy.
        var everything = ToolPolicy.AllTools.Concat(new[] { "RetryAppsAndPolicies" }).ToList();
        Assert.True(ToolCatalog.Offered(Tool, everything));
        Assert.Equal(new[] { Tool }, ToolCatalog.InGroup(ToolGroup.Apps, everything));
        Assert.All(ToolCatalog.All.Where(t => t != Tool), t => Assert.False(ToolCatalog.Offered(t, everything)));
    }

    [Fact]
    public void A_retry_on_a_problem_path_never_asks_whether_it_works_now()
    {
        // Its own post-check and closing status say how the apps are.
        Assert.False(Handover.AsksWorksNow(Problem.CantSignIn, Tool, RunVerdict.Ok));
        Assert.False(Handover.AsksWorksNow(Problem.AppWontInstall, Tool, RunVerdict.Requested));
    }

    [Fact]
    public void The_sentence_names_the_apps_that_need_attention_at_most_two_and_when_they_were_read()
    {
        Assert.Equal("2 of 14 work apps need attention (Contoso Agent, Contoso VPN), read today 09:12.",
            RetryTool.Summary(Finished(Ok(12).Concat(new[] { ("Contoso Agent", "RETRY-HELPS"), ("Contoso VPN", "PACKAGING") }).ToArray()), Amsterdam, Now));
        Assert.Equal("1 of 3 work apps needs attention (Contoso Agent), read today 09:12.",
            RetryTool.Summary(Finished(Ok(2).Concat(new[] { ("Contoso Agent", "RETRY-HELPS") }).ToArray()), Amsterdam, Now));
        Assert.Equal("5 of 14 work apps need attention (Contoso Agent, Contoso VPN Client and 3 more), read today 09:12.",
            RetryTool.Summary(Finished(Ok(9).Concat(new[] { ("Contoso Agent", "RETRY-HELPS"), ("Contoso VPN Client", "UNKNOWN"), ("Contoso Viewer", "RETRY-HELPS"), ("Contoso Notes", "RESTART-FIRST"), ("Contoso Chat", "DEPENDENCY") }).ToArray()), Amsterdam, Now));
    }

    [Fact]
    public void The_sentence_counts_as_the_app_path_and_names_each_app_once()
    {
        // An app on its way counts as the app path's "Apps that need attention" does; the same name twice is named once.
        var diagnosis = Finished(Ok(10).Concat(new[] { ("Contoso Agent", "RETRY-HELPS"), ("Contoso Agent", "RETRY-HELPS") }).ToArray());
        diagnosis.Apps.Add(new AppFinding { App = new AppEvidence { Name = "Contoso VPN", Verdict = "IN-PROGRESS", AppId = "22222222-3333-4444-5555-666666666666" }, Case = AppCase.InstallFailed, Observation = AppObservation.AwaitingEvidence });
        Assert.Equal(diagnosis.Apps.Count(AppDecisions.InShortList).ToString(System.Globalization.CultureInfo.InvariantCulture) + " of 13 work apps need attention (Contoso Agent, Contoso VPN), read today 09:12.",
            RetryTool.Summary(diagnosis, Amsterdam, Now));
        Assert.Equal(3, diagnosis.Apps.Count(AppDecisions.InShortList));
    }

    [Theory]
    [InlineData("Contoso Agent", "Contoso Agent")]
    [InlineData("Contoso Agent Enterprise Ed 30", "Contoso Agent Enterprise Ed 30")]
    [InlineData("Contoso Agent Enterprise Edit 32", "Contoso Agent Enterprise Edit\u2026")]
    [InlineData("ContosoAgentEnterpriseEditionForDevices", "ContosoAgentEnterpriseEdition\u2026")]
    [InlineData("Contoso ContosoAgentEnterpriseEditionForDevices", "Contoso ContosoAgentEnterpris\u2026")]
    [InlineData("Contoso PL2302/PL2303 Windows USB Serial Adapter driver", "Contoso PL2302/PL2303 Windows\u2026")]
    [InlineData("ContosoAgentEnterpriseEditio\uD83D\uDE00xyz", "ContosoAgentEnterpriseEditio\u2026")]
    public void A_long_name_is_cut_at_a_word_with_an_ellipsis_never_inside_a_character(string name, string said)
    {
        Assert.Equal(said, RetryTool.ShortName(name));
        Assert.True(said.Length <= RetryTool.NameChars);
        Assert.False(char.IsHighSurrogate(said[said.Length - 2]));
    }

    [Fact]
    public void The_sentence_says_when_nothing_needs_attention_and_when_the_apps_were_not_read()
    {
        Assert.Equal("None of the 14 work apps needs attention, read today 09:12.", RetryTool.Summary(Finished(Ok(14)), Amsterdam, Now));
        Assert.Equal("The one work app needs no attention, read today 09:12.", RetryTool.Summary(Finished(Ok(1)), Amsterdam, Now));
        Assert.Equal("No work apps were found, read today 09:12.", RetryTool.Summary(Finished(), Amsterdam, Now));
        Assert.Equal("The work apps were not read in this window yet.", RetryTool.Summary(null, Amsterdam, Now));
        Assert.Equal("The work apps were not read in this window yet.", RetryTool.Summary(new AppDiagnosis(), Amsterdam, Now));
        Assert.Equal("Reading the work apps...", RetryTool.Summary(new AppDiagnosis { Run = DiagnosisRunState.Running }, Amsterdam, Now));
        Assert.Equal("The work apps could not be read.", RetryTool.Summary(new AppDiagnosis { Run = DiagnosisRunState.Interrupted }, Amsterdam, Now));
    }
}

/// <summary>In the window: the Retry tool's page in For IT support, its sentence, its link and the missing IME.</summary>
public class RetryToolPageTests
{
    private static object Field(System.Windows.Window window, string name)
        => typeof(EndpointToolbelt.App.Ui.MainWindow).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(window);

    [Fact]
    public void Its_page_says_how_the_work_apps_stood_and_Show_the_apps_opens_the_app_path()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("It"), false);
            MainWindowPagesTests.Call(window, "OnCategoryTile", MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, "AppsTile"), new System.Windows.RoutedEventArgs());
            MainWindowPagesTests.Call(window, "OpenTool", ToolCatalog.ByButton(RetryTool.Button), false);
            var summary = MainWindowPagesTests.Get<System.Windows.Controls.TextBlock>(window, "RetryToolSummary");
            Assert.Equal(System.Windows.Visibility.Visible, summary.Visibility);
            Assert.Equal("The work apps were not read in this window yet.", MainWindowPagesTests.Get<System.Windows.Documents.Run>(window, "RetryToolSummaryText").Text);
            Assert.Equal(RetryTool.ShowAppsText, ((System.Windows.Documents.Run)MainWindowPagesTests.Get<System.Windows.Documents.Hyperlink>(window, "RetryToolAppsLink").Inlines.FirstInline).Text);
            Assert.Equal(System.Windows.Visibility.Visible, MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, RetryTool.Button).Visibility);
            Assert.Equal("Retry apps and settings", MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, RetryTool.Button).Content);

            // Another tool's page has no such sentence.
            MainWindowPagesTests.Call(window, "OpenTool", ToolCatalog.ByButton("ClearCacheButton"), false);
            Assert.Equal(System.Windows.Visibility.Collapsed, summary.Visibility);

            MainWindowPagesTests.Call(window, "OpenTool", ToolCatalog.ByButton(RetryTool.Button), false);
            MainWindowPagesTests.Call(window, "OnRetryToolApps", window, new System.Windows.RoutedEventArgs());
            Assert.Equal(MainWindowPagesTests.PageValue("Apps"), Field(window, "_page"));
            // The app path stays under For IT, and Back returns to the Retry tool's page.
            var back = MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, "BackButton");
            Assert.Equal("Back to For IT support", System.Windows.Automation.AutomationProperties.GetName(back));
            Assert.Equal("Selected", MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, "ItNav").Tag);
            MainWindowPagesTests.Call(window, "OnPathBack", back, new System.Windows.RoutedEventArgs());
            Assert.Equal(MainWindowPagesTests.PageValue("It"), Field(window, "_page"));
            Assert.Equal(System.Windows.Visibility.Visible, MainWindowPagesTests.Get<System.Windows.Controls.Border>(window, "ToolDetail").Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, RetryTool.Button).Visibility);
        });
    }

    [Fact]
    public void Its_page_fits_the_default_window_with_the_longest_sentence_and_a_last_run()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var tool = ToolCatalog.ByButton(RetryTool.Button);
            MainWindowPagesTests.WriteRun(System.IO.Path.Combine(window.RunsFolderOverride, MainWindowPagesTests.RecentName(20, tool.LogKey + "_ab12cd50.log")), true, "[OK] Retry requested");
            var diagnosis = new AppDiagnosis { Run = DiagnosisRunState.Finished, CapturedUtc = DateTime.UtcNow.AddMinutes(-30) };
            foreach (var name in new[] { "Contoso PL2302/PL2303 Windows USB Serial Adapter driver", "Contoso FluidPlan Excel Plug-in for Engineering", "Contoso Viewer", "Contoso Notes", "Contoso Chat" })
            {
                diagnosis.Apps.Add(new AppFinding { App = new AppEvidence { Name = name, Verdict = "RETRY-HELPS", AppId = "11111111-2222-3333-4444-555555555555" }, Case = AppCase.InstallFailed, Observation = AppObservation.Failed });
            }
            MainWindowPagesTests.SetField(window, "_diagnosis", diagnosis);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("It"), false);
            MainWindowPagesTests.Call(window, "OnCategoryTile", MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, "AppsTile"), new System.Windows.RoutedEventArgs());
            MainWindowPagesTests.Call(window, "OpenTool", tool, false);
            Assert.StartsWith("5 of 5 work apps need attention (Contoso PL2302/PL2303 Windows…, Contoso FluidPlan Excel… and 3 more), read ",
                MainWindowPagesTests.Get<System.Windows.Documents.Run>(window, "RetryToolSummaryText").Text, StringComparison.Ordinal);
            Assert.Equal(System.Windows.Visibility.Visible, MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, "ToolLastRunLink").Visibility);
            CompactLayoutTests.AssertFits(window, "The page of Retry work apps and settings");

            // Right after a press: the longest closing status of a retry sits on this page, under its button.
            MainWindowPagesTests.Call(window, "SetStatusTool", tool);
            var (longest, tone) = ResultPresenter.RetryDone(2, 0, AppsAfterRetry.Unknown, otherApps: 5);
            MainWindowPagesTests.Call(window, "SetStatus", longest, tone, false, true);
            Assert.Equal(System.Windows.Visibility.Visible, MainWindowPagesTests.Get<System.Windows.Controls.Border>(window, "DetailStatusHost").Visibility);
            Assert.NotNull(MainWindowPagesTests.Get<System.Windows.Controls.Border>(window, "DetailStatusHost").Child);
            CompactLayoutTests.AssertFits(window, "The page of Retry work apps and settings with its status");
        });
    }

    [Fact]
    public void Without_the_app_installer_it_is_off_and_says_why_as_the_app_path_offers_no_Retry()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.SetField(window, "_healthRows", new System.Collections.Generic.List<HealthResult>
            {
                new() { Name = HealthView.AppInstaller, Status = HealthStatus.Fail, ImeNotInstalled = true },
            });
            MainWindowPagesTests.Call(window, "UpdateButtons");
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("It"), false);
            MainWindowPagesTests.Call(window, "OnCategoryTile", MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, "AppsTile"), new System.Windows.RoutedEventArgs());
            MainWindowPagesTests.Call(window, "OpenTool", ToolCatalog.ByButton(RetryTool.Button), false);
            var button = MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, RetryTool.Button);
            Assert.False(button.IsEnabled);
            Assert.Equal(RetryTool.ImeMissingText, button.ToolTip);
            Assert.Equal(System.Windows.Visibility.Collapsed, MainWindowPagesTests.Get<System.Windows.Controls.TextBlock>(window, "RetryToolSummary").Visibility);
            Assert.Equal(RetryTool.ImeMissingText + ".", MainWindowPagesTests.Get<System.Windows.Controls.TextBlock>(window, "ToolLastRunText").Text);
        });
    }
}
