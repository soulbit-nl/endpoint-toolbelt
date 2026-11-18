using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// Home's clean title claims only what was checked, with the way in for a
/// complaint that is still there; after Copy details for IT the status line says what to do with it.
/// </summary>
public class HomeTitleClaimTests
{
    private static List<HealthResult> Clean() => new() { new() { Name = HealthView.Network, Status = HealthStatus.Pass } };

    [Fact]
    public void On_a_clean_device_where_the_app_diagnosis_has_not_run_Homes_title_does_not_say_that_everything_is_fine()
    {
        var home = HomeSummary.Build(Clean(), null, false);
        Assert.Equal("No problems found in these checks", home.Title);
        Assert.DoesNotContain("fine", home.Title);
        Assert.Equal(Tone.Success, home.Tone);
        // What was not checked, and the way in for a complaint that is still there.
        Assert.Contains("not checked yet", home.Text);
        Assert.Contains("Check my apps", home.Text);
        // A clean screen claims no problem: its link checks the apps, it does not "review affected" ones.
        Assert.Equal(HomeLink.CheckApps, home.Link);
        Assert.Contains("Still a problem? Choose it below.", home.Text);
        // No technical word on Home's first screen.
        Assert.DoesNotContain(HomeSummary.TechnicalWords, w => (home.Title + " " + home.Text).Contains(w));
    }

    [Fact]
    public void After_a_clean_app_check_the_line_says_so_and_keeps_the_way_in()
    {
        var finished = new AppDiagnosis { Run = DiagnosisRunState.Finished };
        var home = HomeSummary.Build(Clean(), finished, false);
        Assert.Equal(HomeSummary.CleanTitle, home.Title);
        Assert.Equal("No app needs attention either. Still a problem? Choose it below.", home.Text);
        // Before the first check: still "Checking this device...".
        Assert.Equal("Checking this device...", HomeSummary.Build(new List<HealthResult>(), null, false).Title);
    }

    [Fact]
    public void After_Copy_for_IT_the_status_line_adds_the_next_step_and_the_ticket_never_carries_it()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var status = MainWindowPagesTests.Get<TextBlock>(window, "StatusText");
            // Without a result on the page: the line says it was copied and what to do.
            MainWindowPagesTests.Call(window, "ShowPasteHint");
            Assert.Equal("Copied. " + MainWindow.PasteHint, status.Text);

            // With a result on the page: its words stay, the step comes after them, once.
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.OneDriveSync, false);
            MainWindowPagesTests.Call(window, "SetStatus", "OneDrive reset requested.", Tone.Neutral, true, true);
            MainWindowPagesTests.Call(window, "ShowPasteHint");
            MainWindowPagesTests.Call(window, "ShowPasteHint");
            Assert.Equal("OneDrive reset requested. " + MainWindow.PasteHint, status.Text);
            Assert.Equal("OneDrive reset requested.", (string)MainWindowPagesTests.Invoke(window, "StatusForTicket"));
            Assert.DoesNotContain(MainWindow.PasteHint, window.ViewTicketText());
        });
    }
}
