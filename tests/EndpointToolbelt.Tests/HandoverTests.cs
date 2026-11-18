using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using EndpointToolbelt.Protocol;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The handover. Without a support link a result that sends the user to
/// IT support still names a next step; after a repair on a problem path the user
/// answers "Does it work now?", apart from the run's result and into the case file; More problems on
/// Home; and "My problem is not listed" on Choose a problem leads to the handover.
/// </summary>
public class HandoverTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Handover test zone", TimeSpan.FromHours(1), "Handover test zone", "Handover test zone");
    private static readonly DateTime Now = new(2026, 11, 14, 10, 50, 0, DateTimeKind.Utc);

    private static string[] Lines(string text) => text.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

    private static RunLogHeader Local(string name, string runId, string outcome = "", bool ended = true)
        => new()
        {
            Check = new LocalRun { Name = name, RunId = runId, StartedUtc = Now.AddMinutes(-8), EndedUtc = ended ? Now.AddMinutes(-7) : null, Outcome = outcome },
            Zone = Zone,
            NowUtc = Now,
        };

    private static void Apply(MainWindow window, string supportText, string supportUrl)
        => MainWindowPagesTests.Invoke(window, "ApplyHealth", new StatusInfo { SupportText = supportText, SupportUrl = supportUrl }, false, CompactLayoutTests.Health(), new DsregInfo(), false);

    // --- A next step without a support link -------------------------------

    [Fact]
    public void Scenario_8_without_a_support_link_a_result_that_says_to_contact_IT_support_still_names_a_next_step()
    {
        // The line itself: Copy details for IT, then the way the user normally reaches IT support.
        Assert.Equal("Copy details for IT, then send it the way you normally reach IT support.", Handover.ResultLine(hasLink: false, supportText: ""));
        Assert.Equal("Copy details for IT, then reach IT support: SoulBit Service Desk - ext. 5555.", Handover.ResultLine(hasLink: false, supportText: "SoulBit Service Desk - ext. 5555."));
        Assert.Equal("Copy details for IT, then use Contact IT on Home.", Handover.ResultLine(hasLink: true, supportText: null));
        Assert.True(Handover.ResultNamesItSupport("Contact your IT support.", null));
        Assert.True(Handover.ResultNamesItSupport("", new[] { ResultCard.From(FindingSeverity.Error, "The app failed", "", "Ask your IT support to check the assignment.") }));
        // A card's line for IT support is for them, not a step to them.
        Assert.False(Handover.ResultNamesItSupport("Done.", new[] { ResultCard.From(FindingSeverity.Ok, "Done", "", "", "Review the log.") }));

        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            Apply(window, "", "");
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.CantSignIn, false);
            MainWindowPagesTests.Call(window, "SetStatus", "Your work account could not be refreshed. Contact your IT support.", Tone.Error, true, true);
            var line = MainWindowPagesTests.Get<TextBlock>(window, "HandoverText");
            Assert.Equal(Visibility.Visible, line.Visibility);
            Assert.Equal("Copy details for IT, then send it the way you normally reach IT support.", line.Text);
            // Home's "Need a person?" line: no Contact IT, the way in words.
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<TextBlock>(window, "ContactBlock").Visibility);
            var end = MainWindowPagesTests.Get<TextBlock>(window, "ContactEndText");
            Assert.Equal(Visibility.Visible, end.Visibility);
            Assert.Equal("Then send it the way you normally reach IT support.", end.Text);

            // A result that does not name IT support has no such line; nor has the paste hint.
            MainWindowPagesTests.Call(window, "SetStatus", "The clock is in sync.", Tone.Success, true, true);
            Assert.Equal(Visibility.Collapsed, line.Visibility);
            MainWindowPagesTests.Call(window, "ClearResult");
            MainWindowPagesTests.Call(window, "SetStatus", "Copied. " + MainWindow.PasteHint, Tone.Neutral, true, true);
            Assert.Equal(Visibility.Collapsed, line.Visibility);
        });
    }

    [Fact]
    public void With_a_support_link_or_text_the_line_names_that_way_and_Home_keeps_Contact_IT()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            Apply(window, "Contoso IT Service Desk - ext. 5555", "https://support.contoso.example/");
            MainWindowPagesTests.Call(window, "SetStatus", "Contact your IT support.", Tone.Error, true, true);
            Assert.Equal("Copy details for IT, then reach IT support: Contoso IT Service Desk - ext. 5555.", MainWindowPagesTests.Get<TextBlock>(window, "HandoverText").Text);
            // The support text and Contact IT on a line of their own under "Need a person?".
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<System.Windows.Controls.WrapPanel>(window, "HomeContactLine").Visibility);
            Assert.Equal("Contoso IT Service Desk - ext. 5555", MainWindowPagesTests.Get<TextBlock>(window, "SupportText").Text);
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<TextBlock>(window, "ContactBlock").Visibility);
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<TextBlock>(window, "ContactEndText").Visibility);

            // Only a support text: the line points to it.
            Apply(window, "Contoso IT Service Desk - ext. 5555", "");
            MainWindowPagesTests.Call(window, "SetStatus", "Contact your IT support.", Tone.Error, true, true);
            Assert.Equal("Copy details for IT, then reach IT support: Contoso IT Service Desk - ext. 5555.", MainWindowPagesTests.Get<TextBlock>(window, "HandoverText").Text);
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<TextBlock>(window, "ContactEndText").Visibility);
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<TextBlock>(window, "ContactBlock").Visibility);
            // Only a link: the line points to Contact IT on Home.
            Apply(window, "", "https://support.contoso.example/");
            MainWindowPagesTests.Call(window, "SetStatus", "Contact your IT support.", Tone.Error, true, true);
            Assert.Equal("Copy details for IT, then use Contact IT on Home.", MainWindowPagesTests.Get<TextBlock>(window, "HandoverText").Text);
            // An address the window never shows as a link counts as none.
            Assert.Null(Handover.SupportLink("http://support.contoso.example/"));
            Assert.Null(Handover.SupportLink("https://name@support.contoso.example/"));
            Assert.NotNull(Handover.SupportLink("https://support.contoso.example/"));
        });
    }

    // --- Does it work now? -----------------------------------------------------------------------

    [Fact]
    public void Does_it_work_now_follows_a_repair_on_a_problem_path_only()
    {
        var teams = ToolCatalog.ByButton("TeamsCacheButton");
        Assert.True(Handover.AsksWorksNow(Problem.CantSignIn, teams, RunVerdict.Ok));
        Assert.True(Handover.AsksWorksNow(Problem.OneDriveSync, ToolCatalog.ByButton("ResetOneDriveButton"), RunVerdict.Requested));
        Assert.True(Handover.AsksWorksNow(Problem.CantSignIn, teams, RunVerdict.Check));
        // Not on Home, For IT or the app path (Check again reads the app's state there).
        Assert.False(Handover.AsksWorksNow(null, teams, RunVerdict.Ok));
        Assert.False(Handover.AsksWorksNow(Problem.AppWontInstall, ToolCatalog.ByButton("ClearCacheButton"), RunVerdict.Ok));
        // Not after a problem result, nor while it runs or without its end.
        Assert.False(Handover.AsksWorksNow(Problem.CantSignIn, teams, RunVerdict.Problem));
        Assert.False(Handover.AsksWorksNow(Problem.CantSignIn, teams, RunVerdict.Running));
        Assert.False(Handover.AsksWorksNow(Problem.CantSignIn, teams, RunVerdict.NotFinished));
        // Not for a tool that only reads (Check Microsoft connections, Check Windows Update) or a safe-mode start (its own question).
        Assert.False(Handover.AsksWorksNow(Problem.CantConnect, ToolCatalog.ByButton("ConnectivityButton"), RunVerdict.Ok));
        Assert.False(Handover.AsksWorksNow(Problem.WindowsWontUpdate, ToolCatalog.ByButton("CheckWuButton"), RunVerdict.Ok));
        Assert.False(Handover.AsksWorksNow(Problem.OfficeCloses, ToolCatalog.ByButton(OfficeSafeMode.Button(OfficeApp.Word)), RunVerdict.Requested));
        Assert.False(Handover.AsksWorksNow(Problem.CantSignIn, null, RunVerdict.Ok));
    }

    [Fact]
    public void The_answer_goes_into_the_case_file_after_the_outcome_once_and_stays_when_the_run_is_saved_again()
    {
        var file = new CaseFile();
        var cards = new[] { ResultCard.From(FindingSeverity.Ok, "Teams cache cleared", "") };
        Assert.True(file.Record(Problem.CantSignIn, Local("Clear Teams cache", "local-ab12cd34"), cards));
        Assert.Equal(RunVerdict.Ok, CaseFile.VerdictOf(Local("Clear Teams cache", "local-ab12cd34"), cards));
        Assert.Null(file.AnswerOf("local-ab12cd34"));

        Assert.True(file.Answer("local-ab12cd34", WorksNowAnswer.NotYet));
        Assert.False(file.Answer("local-ab12cd34", WorksNowAnswer.Yes));
        Assert.False(file.Answer("local-00000000", WorksNowAnswer.Yes));
        Assert.Equal("Tried · 2026-11-14 11:42 · Clear Teams cache · OK · Works now: not yet · Reference local-ab12cd34", Assert.Single(file.TriedLines(Problem.CantSignIn, Zone)));

        // Saved again (the same run): the answer stays with it.
        file.Record(null, Local("Clear Teams cache", "local-ab12cd34"), cards);
        Assert.Equal(WorksNowAnswer.NotYet, file.AnswerOf("local-ab12cd34"));
        Assert.Equal(new[] { "Works now: yes", "Works now: not yet", "Works now: not tried" },
            new[] { WorksNowAnswer.Yes, WorksNowAnswer.NotYet, WorksNowAnswer.NotTried }.Select(Handover.AnswerWords));
    }

    [Fact]
    public void After_Reset_OneDrive_on_its_path_the_user_answers_and_both_Copy_for_IT_texts_carry_it_apart_from_the_result()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            Apply(window, "", "");
            window.OneDrive = OneDriveResetTests.FakeOneDrive.Installed();
            window.ConfirmOneDriveReset = () => true;
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.OneDriveSync, false);
            MainWindowPagesTests.Call(window, "OnResetOneDrive", window, new RoutedEventArgs());
            MainWindowPagesTests.WaitFor(() => !(bool)MainWindowPagesTests.Field(window, "_busy"));

            var question = MainWindowPagesTests.Get<WrapPanel>(window, "WorksNowQuestion");
            Assert.Equal(Visibility.Visible, question.Visibility);
            Assert.Equal(Handover.Question, MainWindowPagesTests.Get<TextBlock>(window, "WorksNowText").Text);
            var status = MainWindowPagesTests.Get<TextBlock>(window, "StatusText").Text;
            var word = MainWindowPagesTests.Get<TextBlock>(window, "StatusWord").Text;

            MainWindowPagesTests.Call(window, "OnWorksNowNotYet", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Collapsed, question.Visibility);
            var after = MainWindowPagesTests.Get<TextBlock>(window, "WorksNowAnswerText");
            Assert.Equal(Visibility.Visible, after.Visibility);
            Assert.Equal("Your answer is in Copy details for IT with this run. Copy details for IT, then send it the way you normally reach IT support.", after.Text);
            // Apart from the technical outcome: the status line and its word stay as the run left them.
            Assert.Equal(status, MainWindowPagesTests.Get<TextBlock>(window, "StatusText").Text);
            Assert.Equal(word, MainWindowPagesTests.Get<TextBlock>(window, "StatusWord").Text);

            var view = window.ViewTicketText();
            var path = window.PathTicketText();
            var tried = Lines(path).Single(l => l.StartsWith("Tried · ", StringComparison.Ordinal));
            Assert.Contains(" · " + OneDriveReset.ToolName + " · Reset requested · Works now: not yet · Reference local-", tried);
            Assert.Contains(tried, Lines(view));
            // The outcome line is the run's own, never the answer.
            Assert.DoesNotContain("not yet", Lines(view).Single(l => l.StartsWith("Outcome · ", StringComparison.Ordinal)));

            // A second answer changes nothing; a new result removes the question.
            MainWindowPagesTests.Call(window, "OnWorksNowYes", window, new RoutedEventArgs());
            Assert.Contains("Works now: not yet", window.PathTicketText());
            MainWindowPagesTests.Call(window, "ClearResult");
            Assert.Equal(Visibility.Collapsed, after.Visibility);
        });
    }

    // A Sandbox run: Reset sign-in cache (a repair in the app, not the service) ended OK on the
    // sign-in path, and the question did not show.
    [Fact]
    public void After_Reset_sign_in_cache_on_its_path_the_app_asks_Does_it_work_now()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.CantSignIn, false);
            MainWindowPagesTests.Call(window, "ClearResult");
            MainWindowPagesTests.Call(window, "StartCheck", "Reset sign-in cache");
            var outcome = ToolOutcome.Of(FindingSeverity.Ok, Tone.Success, 0, "Reset sign-in cache: done. Sign out of Windows and sign in again.",
                "The sign-in cache was reset", "1 cached account file removed.", "Sign out of Windows and sign in again (or restart).");
            MainWindowPagesTests.Call(window, "ShowToolOutcome", outcome, "ResetSignInCache");
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<WrapPanel>(window, "WorksNowQuestion").Visibility);
        });
    }

    [Fact]
    public void A_read_only_check_on_a_path_asks_nothing()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.CantConnect, false);
            MainWindowPagesTests.Call(window, "ClearResult");
            MainWindowPagesTests.Call(window, "StartCheck", "Check Microsoft connections");
            MainWindowPagesTests.Call(window, "FinishCheck");
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<WrapPanel>(window, "WorksNowQuestion").Visibility);
        });
    }

    // --- More problems on Home -------------------------------------------------------------------

    [Fact]
    public void More_problems_on_Home_opens_Choose_a_problem_and_Home_still_fits()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            Apply(window, "Contoso IT Service Desk - ext. 5555", "https://support.contoso.example/");
            var more = MainWindowPagesTests.Get<Button>(window, "MoreProblemsLink");
            Assert.Equal("More problems", more.Content);
            CompactLayoutTests.AssertFits(window, "Home with More problems and a support contact");
            Apply(window, "", "");
            CompactLayoutTests.AssertFits(window, "Home with More problems and the way to IT support in words");
            more.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<StackPanel>(window, "ProblemsPage").Visibility);
        });
    }

    // --- My problem is not listed ---------------------------------------------------------------

    [Fact]
    public void My_problem_is_not_listed_leads_to_the_handover_with_the_first_line_facts_a_package_and_the_way_to_IT()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            Apply(window, "Contoso IT Service Desk - ext. 5555", "https://support.contoso.example/");
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            var row = MainWindowPagesTests.Get<Button>(window, "ProblemNotListedButton");
            Assert.Equal(Handover.NotListedName, System.Windows.Automation.AutomationProperties.GetName(row));
            CompactLayoutTests.AssertFits(window, "Choose a problem with My problem is not listed and a support contact");

            row.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<StackPanel>(window, "NotListedPage").Visibility);
            Assert.Equal(Handover.NotListedTitle, MainWindowPagesTests.Get<TextBlock>(window, "ProblemTitle").Text);
            // Contact IT support first; then what they may ask for, each with its own button. Copy details for IT
            // is one of them, so the link next to Back is not shown here.
            Assert.Equal("Contact your IT support and tell them in your own words what goes wrong.", MainWindowPagesTests.Get<TextBlock>(window, "NotListedIntro").Text);
            Assert.Equal("They may ask you to:", MainWindowPagesTests.Get<TextBlock>(window, "NotListedAsk").Text);
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<Button>(window, "PathCopyTicketButton").Visibility);
            var copy = MainWindowPagesTests.Get<Button>(window, "NotListedCopyButton");
            Assert.Equal("Copy details for IT", copy.Content);
            Assert.Equal("Copy details for IT: " + Handover.NotListedTitle, System.Windows.Automation.AutomationProperties.GetName(copy));
            Assert.Equal(Handover.NotListedCopyText, MainWindowPagesTests.Get<TextBlock>(window, "NotListedCopyText").Text);
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<Button>(window, "NotListedPackageButton").Visibility);
            Assert.Equal(Handover.NotListedPackageText, MainWindowPagesTests.Get<TextBlock>(window, "NotListedPackageText").Text);
            Assert.Equal("Open For IT support", MainWindowPagesTests.Get<Button>(window, "NotListedItButton").Content);
            Assert.Equal(Handover.NotListedItText, MainWindowPagesTests.Get<TextBlock>(window, "NotListedItText").Text);
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<TextBlock>(window, "NotListedContactBlock").Visibility);
            // The organization's own words beside Contact IT (the window no longer shows them in its footer).
            Assert.Equal("Reach them: Contoso IT Service Desk - ext. 5555.", MainWindowPagesTests.Get<TextBlock>(window, "NotListedEndText").Text);
            CompactLayoutTests.AssertFits(window, "My problem is not listed");

            // The ticket: the first-line facts, nothing run, the user's own words to follow.
            var ticket = Lines(window.PathTicketText());
            Assert.Equal("Endpoint Toolbelt · " + Handover.NotListedTitle + " · Reference none", ticket[0]);
            Assert.Contains("Action · None: copied from " + Handover.NotListedTitle + "; nothing was run or changed", ticket);
            Assert.Contains(ticket, l => l.StartsWith("Outcome · " + Handover.NotListedOutcome, StringComparison.Ordinal));
            foreach (var fact in new[] { "App version · ", "Last restart · ", "Join type · ", "MDM enrollment ID · ", "Network · ", "Health · " })
            {
                Assert.Contains(ticket, l => l.StartsWith(fact, StringComparison.Ordinal));
            }

            // Without a link: the way to IT support in words instead of Contact IT.
            Apply(window, "", "");
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<TextBlock>(window, "NotListedContactBlock").Visibility);
            var end = MainWindowPagesTests.Get<TextBlock>(window, "NotListedEndText");
            Assert.Equal(Visibility.Visible, end.Visibility);
            Assert.Equal("Reach them the way you normally do.", end.Text);
            Apply(window, "Contoso IT Service Desk - ext. 5555", "");
            Assert.Equal("Reach them: Contoso IT Service Desk - ext. 5555.", end.Text);

            // Back returns to Choose a problem, on the row it came from.
            MainWindowPagesTests.Call(window, "OnPathBack", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<StackPanel>(window, "ProblemsPage").Visibility);
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<StackPanel>(window, "NotListedPage").Visibility);
        });
    }

    [Fact]
    public void My_problem_is_not_listed_is_not_a_problem_path()
    {
        Assert.DoesNotContain(ProblemPaths.All, p => p.Title == Handover.NotListedTitle);
        Assert.Empty(ToolCatalog.Search("not listed"));
        Assert.Equal(Handover.NotListedTitle + ". " + Handover.NotListedSubtitle, Handover.NotListedName);
    }
}
