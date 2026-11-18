using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// One case file behind every Copy details for IT on a problem path. The path's Copy for
/// IT and the one under View details add the same Tried lines (time, tool, outcome in words, run
/// reference) for the runs of this session on that path, at most six, never a UPN, path or SID.
/// </summary>
public class CaseFileTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Case file test zone", TimeSpan.FromHours(1), "Case file test zone", "Case file test zone");
    private static readonly DateTime Now = new(2026, 11, 14, 10, 50, 0, DateTimeKind.Utc);

    private static RunLogHeader Local(string name, string runId, DateTime started, string outcome = "", bool ended = true)
        => new()
        {
            Check = new LocalRun { Name = name, RunId = runId, StartedUtc = started, EndedUtc = ended ? started.AddSeconds(20) : null, Outcome = outcome },
            AppVersion = "1.1.1",
            DeviceName = "SOULBIT-0042",
            User = "CONTOSO\\soulbit.user",
            Zone = Zone,
            NowUtc = started.AddSeconds(30),
        };

    private static string[] Lines(string text) => text.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

    // --- The case file -----------------------------------------------------------------------------

    [Fact]
    public void A_run_is_named_with_its_time_tool_outcome_in_words_and_reference()
    {
        var file = new CaseFile();
        Assert.True(file.Record(Problem.OneDriveSync, Local("Reset OneDrive", "local-ab12cd34", Now.AddMinutes(-8), "Reset requested"),
            new[] { ResultCard.From(FindingSeverity.Info, "Reset requested: OneDrive", "") }));

        Assert.Equal(new[] { "Tried · 2026-11-14 11:42 · Reset OneDrive · Reset requested · Reference local-ab12cd34" }, file.TriedLines(Problem.OneDriveSync, Zone));
        Assert.Equal(Problem.OneDriveSync, file.PathOf("local-ab12cd34"));
        Assert.Empty(file.TriedLines(Problem.CantSignIn, Zone));
        Assert.Empty(file.TriedLines(null, Zone));
    }

    [Fact]
    public void The_outcome_in_words_is_the_one_Recent_runs_shows_else_the_result()
    {
        // A run whose log names an outcome: that one.
        Assert.Equal("Works in safe mode", CaseFile.OutcomeWords(Local("Start Word in safe mode", "local-00000001", Now, "Works in safe mode"), Array.Empty<ResultCard>()));
        // A run with an OK card and no outcome of its own: "OK", as Recent runs says it.
        Assert.Equal("OK", CaseFile.OutcomeWords(Local("Sync clock", "local-00000002", Now), new[] { ResultCard.From(FindingSeverity.Ok, "The clock is in sync", "") }));
        // A warning: "Warning".
        Assert.Equal("Warning", CaseFile.OutcomeWords(Local("Check Microsoft connections", "local-00000003", Now), new[] { ResultCard.From(FindingSeverity.Warning, "One service did not answer", "") }));
    }

    [Fact]
    public void A_run_saved_again_updates_its_line_where_it_is_and_a_running_one_is_not_recorded()
    {
        var file = new CaseFile();
        file.Record(Problem.OfficeCloses, Local("Start Word in safe mode", "local-0000000a", Now.AddMinutes(-5), "Start requested"), Array.Empty<ResultCard>());
        // The answer comes while another page is shown: the run stays on its own path.
        file.Record(null, Local("Start Word in safe mode", "local-0000000a", Now.AddMinutes(-5), "Works in safe mode"), Array.Empty<ResultCard>());
        Assert.Equal("Tried · 2026-11-14 11:45 · Start Word in safe mode · Works in safe mode · Reference local-0000000a", Assert.Single(file.TriedLines(Problem.OfficeCloses, Zone)));

        Assert.False(file.Record(Problem.OfficeCloses, Local("Start Excel in safe mode", "local-0000000b", Now, ended: false), Array.Empty<ResultCard>()));
        Assert.False(file.Record(null, Local("Sync clock", "local-0000000c", Now), Array.Empty<ResultCard>()));
        Assert.Single(file.Runs(Problem.OfficeCloses));
    }

    [Fact]
    public void At_most_six_runs_are_named_and_the_earlier_ones_counted()
    {
        var file = new CaseFile();
        for (var i = 0; i < 8; i++)
        {
            file.Record(Problem.CantConnect, Local("Check Microsoft connections", "local-0000001" + i, Now.AddMinutes(-20 + i), "Check"), Array.Empty<ResultCard>());
        }
        var lines = file.TriedLines(Problem.CantConnect, Zone);
        Assert.Equal(7, lines.Count);
        Assert.Equal("Tried · 2 earlier runs on this path are left out", lines[0]);
        Assert.EndsWith("Reference local-00000012", lines[1]);
        Assert.EndsWith("Reference local-00000017", lines[6]);
    }

    [Fact]
    public void The_tickets_carry_the_Tried_lines_after_the_Action_line_and_scrub_names()
    {
        var header = Local("Reset OneDrive", "local-ab12cd34", Now.AddMinutes(-8), "Reset requested");
        var tried = new[] { "Tried · 10:42 · Reset soulbit.user's OneDrive · Reset requested · Reference local-ab12cd34" };

        var page = Lines(RunTicket.Page(header, "OneDrive doesn't sync", Array.Empty<ResultCard>(), "No problem found in these checks.", Now, null, tried));
        Assert.Equal("Action · Copied from OneDrive doesn't sync; what ran on it in this session is under Tried", page[2]);
        Assert.Equal("Tried · 10:42 · Reset [user]'s OneDrive · Reset requested · Reference local-ab12cd34", page[3]);
        Assert.StartsWith("Outcome · ", page[4], StringComparison.Ordinal);
        // Without runs the line is unchanged.
        Assert.Equal("Action · None: copied from OneDrive doesn't sync; nothing was run or changed", Lines(RunTicket.Page(header, "OneDrive doesn't sync", Array.Empty<ResultCard>(), "", Now, null))[2]);

        var text = Lines(RunTicket.Text(header, Array.Empty<ResultCard>(), "OneDrive reset requested.", Tone.Neutral, tried: tried));
        Assert.StartsWith("Action · ", text[2], StringComparison.Ordinal);
        Assert.StartsWith("Tried · ", text[3], StringComparison.Ordinal);
        Assert.StartsWith("Outcome · ", text[4], StringComparison.Ordinal);
    }

    // --- The window: Reset OneDrive on its path, the Office path and the app path ------------

    private static string TriedFor(string ticket, string tool) => Lines(ticket).SingleOrDefault(l => l.StartsWith("Tried · ", StringComparison.Ordinal) && l.Contains(" · " + tool + " · "));

    [Fact]
    public void After_Reset_OneDrive_the_paths_Copy_for_IT_and_the_one_under_View_details_name_the_reset_its_outcome_and_its_reference()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            window.OneDrive = OneDriveResetTests.FakeOneDrive.Installed();
            window.ConfirmOneDriveReset = () => true;
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.OneDriveSync, false);
            Assert.Equal("Action · None: copied from " + ProblemPaths.Get(Problem.OneDriveSync).Title + "; nothing was run or changed",
                Lines(window.PathTicketText()).Single(l => l.StartsWith("Action · ", StringComparison.Ordinal)));

            MainWindowPagesTests.Call(window, "OnResetOneDrive", window, new RoutedEventArgs());
            MainWindowPagesTests.WaitFor(() => !(bool)MainWindowPagesTests.Field(window, "_busy"));

            var view = window.ViewTicketText();
            var reference = Lines(view)[0].Split(new[] { " · Reference " }, StringSplitOptions.None)[1];
            Assert.StartsWith("local-", reference, StringComparison.Ordinal);
            var path = window.PathTicketText();
            var line = TriedFor(path, OneDriveReset.ToolName);
            Assert.NotNull(line);
            Assert.EndsWith(" · " + OneDriveReset.ToolName + " · Reset requested · Reference " + reference, line);
            // Both tickets name the same run with the same words, time and reference.
            Assert.Equal(line, TriedFor(view, OneDriveReset.ToolName));
            Assert.Contains("Action · Copied from " + ProblemPaths.Get(Problem.OneDriveSync).Title + "; what ran on it in this session is under Tried", Lines(path));
            // Privacy: no user name and no path in either ticket.
            foreach (var ticket in new[] { path, view })
            {
                Assert.DoesNotContain(@"C:\", ticket, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("S-1-5-", ticket, StringComparison.Ordinal);
                if (Environment.UserName.Length >= 3) { Assert.DoesNotContain(Environment.UserName, ticket, StringComparison.OrdinalIgnoreCase); }
            }
            // Another path has its own case file: nothing tried there.
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.CantSignIn, false);
            Assert.DoesNotContain(Lines(window.PathTicketText()), l => l.StartsWith("Tried · ", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void On_the_Office_path_the_safe_mode_answer_is_the_runs_outcome_in_both_tickets()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.OfficeCloses, false);
            MainWindowPagesTests.Call(window, "ClearResult");
            MainWindowPagesTests.Call(window, "StartCheck", OfficeSafeMode.ToolName(OfficeApp.Word));
            MainWindowPagesTests.Call(window, "ShowSafeModeOutcome", OfficeSafeMode.Start(OfficeSafeModeTests.FakeOffice.With(word: true, excel: true), OfficeApp.Word, null, asks: true));
            Assert.EndsWith(" · Start requested · Reference " + Lines(window.ViewTicketText())[0].Split(new[] { " · Reference " }, StringSplitOptions.None)[1],
                TriedFor(window.PathTicketText(), OfficeSafeMode.ToolName(OfficeApp.Word)));

            MainWindowPagesTests.Call(window, "AnswerSafeMode", SafeModeAnswer.Yes);

            var path = TriedFor(window.PathTicketText(), OfficeSafeMode.ToolName(OfficeApp.Word));
            Assert.Contains(" · Works in safe mode · Reference local-", path);
            Assert.Equal(path, TriedFor(window.ViewTicketText(), OfficeSafeMode.ToolName(OfficeApp.Word)));
            // One run, one line: the answer replaced the start.
            Assert.Single(Lines(window.PathTicketText()), l => l.StartsWith("Tried · ", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void On_the_app_path_a_check_that_ran_there_is_in_its_Copy_for_IT_and_a_run_from_For_IT_is_on_no_path()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Apps"), false);
            MainWindowPagesTests.Call(window, "ClearResult");
            MainWindowPagesTests.Call(window, "StartCheck", "Check Microsoft connections");
            MainWindowPagesTests.Call(window, "FinishCheck");
            var line = TriedFor(window.PathTicketText(), "Check Microsoft connections");
            Assert.NotNull(line);
            Assert.Equal(line, TriedFor(window.ViewTicketText(), "Check Microsoft connections"));

            // A run that ends on For IT belongs to no path: no Tried line anywhere.
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("It"), false);
            MainWindowPagesTests.Call(window, "ClearResult");
            MainWindowPagesTests.Call(window, "StartCheck", "Sync clock");
            MainWindowPagesTests.Call(window, "FinishCheck");
            Assert.DoesNotContain(Lines(window.ViewTicketText()), l => l.StartsWith("Tried · ", StringComparison.Ordinal));
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Apps"), false);
            Assert.Null(TriedFor(window.PathTicketText(), "Sync clock"));
        });
    }
}
