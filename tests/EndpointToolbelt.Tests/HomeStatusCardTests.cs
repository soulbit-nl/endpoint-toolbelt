using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// Home on a virtual desktop: a link on the status card that
/// fits its finding, Retry work apps and settings primary only where it can help, the heading's focus box
/// only with keyboard cues, a centered column in a maximized window, and Choose a problem's rows.
/// </summary>
public class HomeStatusCardTests
{
    private static MainWindow Home(string warn = null, bool missing = false)
    {
        var window = MainWindowPagesTests.NewWindow();
        MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), missing, CompactLayoutTests.Health(warn: warn), new DsregInfo(), false);
        MainWindowPagesTests.Call(window, "ShowHome");
        return window;
    }

    private static Button Get(MainWindow window, string name) => MainWindowPagesTests.Get<Button>(window, name);

    private static Style Style(string key) => (Style)Application.Current.FindResource(key);

    [Theory]
    [InlineData(null, "CheckApps")]
    [InlineData(HealthChecks.ImeCheckIn, "ReviewApps")]
    [InlineData(HealthChecks.LastSync, "ReviewApps")]
    [InlineData(HealthChecks.Profiles, "ViewFailedSettings")]
    [InlineData(HealthChecks.Clock, "None")]
    [InlineData(HealthChecks.FreeSpace, "None")]
    [InlineData(HealthChecks.PendingRestart, "None")]
    [InlineData(HealthChecks.Defender, "None")]
    public void Each_top_card_kind_has_only_a_link_that_fits_it(string warn, string link)
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = Home(warn);
            var finding = (HomeFinding)MainWindowPagesTests.Field(window, "_home");
            Assert.Equal(link, finding.Link.ToString());
            var button = Get(window, "ReviewAppsLink");
            switch (finding.Link)
            {
                case HomeLink.ReviewApps:
                    Assert.Equal(Visibility.Visible, button.Visibility);
                    Assert.Equal("Review affected _apps", button.Content);
                    break;
                case HomeLink.CheckApps:
                    Assert.Equal(Visibility.Visible, button.Visibility);
                    Assert.Equal("Check my _apps", button.Content);
                    Assert.Equal("Check my apps", AutomationProperties.GetName(button));
                    break;
                case HomeLink.ViewFailedSettings:
                    Assert.Equal(Visibility.Visible, button.Visibility);
                    Assert.Equal("View failed settings", button.Content);
                    Assert.Equal("View failed settings: Configuration profiles", AutomationProperties.GetName(button));
                    // After the Retry link, with a gap a reader sees (the two links read as one line otherwise).
                    Assert.True(button.Margin.Left >= 12);
                    break;
                default:
                    Assert.Equal(Visibility.Collapsed, button.Visibility);
                    break;
            }
        });
    }

    [Fact]
    public void Without_the_service_and_with_an_app_finding_the_card_has_the_link_that_fits()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = Home(missing: true);
            Assert.Equal(HomeLink.None, ((HomeFinding)MainWindowPagesTests.Field(window, "_home")).Link);
            Assert.Equal(Visibility.Collapsed, Get(window, "ReviewAppsLink").Visibility);
        });
        var app = HomeSummary.Build(CompactLayoutTests.Health(), AppDecisions.From(AppDiagnosisTests.Outcome(AppDiagnosisTests.App("RETRY-HELPS")), DateTime.UtcNow), false);
        Assert.Equal(HomeLink.ReviewApps, app.Link);
        Assert.True(app.StepIsPrimary);
    }

    [Theory]
    [InlineData(HealthChecks.ImeCheckIn, true)]
    [InlineData(HealthChecks.ImeService, true)]
    [InlineData(HealthChecks.LastSync, true)]
    [InlineData(HealthChecks.MdmCertificate, true)]
    [InlineData(HealthChecks.Profiles, false)]
    [InlineData(HealthChecks.Clock, true)]
    public void Retry_apps_and_policies_is_primary_only_where_it_can_help(string warn, bool stepPrimary)
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = Home(warn);
            var finding = (HomeFinding)MainWindowPagesTests.Field(window, "_home");
            Assert.Equal(stepPrimary, finding.StepIsPrimary);
            var step = Get(window, "PrimaryStepButton");
            var check = Get(window, "RefreshButton");
            Assert.Equal(Visibility.Visible, step.Visibility);
            Assert.Same(Style(stepPrimary ? "PrimaryButton" : "LinkButton"), step.Style);
            Assert.Same(Style(stepPrimary ? "SecondaryButton" : "PrimaryButton"), check.Style);
            var panel = (Panel)step.Parent;
            // The primary button comes first, the links after it.
            Assert.Equal(stepPrimary ? 0 : 1, panel.Children.IndexOf(step));
            if (!stepPrimary) { Assert.Equal(Words.RetryStep, step.Content); }
        });
        Assert.True(HomeSummary.RetryHelps(HealthView.AppInstaller));
        Assert.True(HomeSummary.RetryHelps(HealthView.IntuneConnection));
        Assert.False(HomeSummary.RetryHelps(HealthChecks.Profiles));
    }

    [Fact]
    public void View_failed_settings_opens_the_profiles_row()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = Home(HealthChecks.Profiles);
            MainWindowPagesTests.Call(window, "OnReviewApps", window, new RoutedEventArgs());
            var rows = ((System.Collections.Generic.IEnumerable<HealthRow>)MainWindowPagesTests.Get<ItemsControl>(window, "HealthList").ItemsSource).ToList();
            Assert.True(rows.Single(r => r.Name == HealthView.PlainName(HealthChecks.Profiles)).IsOpen);
            // It stays on Home: no app path, nothing ran.
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<StackPanel>(window, "HomePage").Visibility);
        });
    }

    [Fact]
    public void The_headings_focus_box_shows_only_with_keyboard_cues()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = Home();
            var title = MainWindowPagesTests.Get<TextBlock>(window, "MainFindingTitle");
            // On open (no key pressed in the window): the focus is kept for screen readers, without the box.
            Assert.Null(title.FocusVisualStyle);
            Assert.True(title.Focusable);
            MainWindowPagesTests.Call(window, "FocusTitle");
            Assert.Null(title.FocusVisualStyle);
            // After a key press in the window the box is drawn.
            MainWindowPagesTests.SetField(window, "_keyboardCues", true);
            MainWindowPagesTests.Call(window, "FocusTitle");
            Assert.Same(Style("FocusOutline"), title.FocusVisualStyle);
        });
    }

    [Fact]
    public void A_maximized_window_centers_the_column_and_the_compact_layout_stays()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = Home();
            var grid = MainWindowPagesTests.Get<Grid>(window, "ContentGrid");
            var readable = (double)Application.Current.FindResource("ContentMaxWidth");
            Assert.Equal(readable, grid.MaxWidth);
            Assert.Equal(HorizontalAlignment.Stretch, grid.HorizontalAlignment);
            var content = (FrameworkElement)window.Content;
            // Maximized on a wide screen: the column keeps its readable width, with equal space on both sides.
            content.Measure(new Size(1900, 1000));
            content.Arrange(new Rect(0, 0, 1900, 1000));
            content.UpdateLayout();
            var scroll = MainWindowPagesTests.Get<ScrollViewer>(window, "MainScroll");
            var left = grid.TranslatePoint(new Point(0, 0), scroll).X;
            var right = scroll.ViewportWidth - left - grid.ActualWidth;
            Assert.Equal(readable, grid.ActualWidth, 1);
            Assert.True(Math.Abs(left - right) <= 1, "left " + left + ", right " + right);
            // Compact: the column fills the window as before.
            CompactLayoutTests.Fit(window, WindowLayout.DefaultClientWidth, WindowLayout.CompactClientHeight);
            Assert.Equal(0, grid.TranslatePoint(new Point(0, 0), scroll).X, 1);
        });
    }

    [Fact]
    public void Choose_a_problem_has_one_column_at_every_size()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var rows = MainWindowPagesTests.Get<System.Windows.Controls.Primitives.UniformGrid>(window, "ProblemRows");
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            // One column at every width, also when wide.
            foreach (var width in new[] { 540.0, 900, 1600 })
            {
                CompactLayoutTests.Fit(window, width, 900);
                Assert.Equal(1, rows.Columns);
            }
        });
    }
}
