using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EndpointToolbelt.App.Core;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>Start an Office app in safe mode, with a fake installed-apps reader and a fake process starter.</summary>
public class OfficeSafeModeTests
{
    private const string WordPath = @"C:\Program Files\Contoso Office\WINWORD.EXE";

    /// <summary>Installed apps by path, running apps, and every start the code asked for (nothing starts for real).</summary>
    internal sealed class FakeOffice : IOfficeApps
    {
        public Dictionary<OfficeApp, string> Installed { get; } = new();
        public HashSet<OfficeApp> Running { get; } = new();
        public List<(string Path, string Arguments)> Starts { get; } = new();
        public bool StartWorks { get; set; } = true;

        public static FakeOffice With(bool word, bool excel, bool outlook = false, bool powerPoint = false)
        {
            var fake = new FakeOffice();
            if (word) { fake.Installed[OfficeApp.Word] = WordPath; }
            if (excel) { fake.Installed[OfficeApp.Excel] = @"C:\Program Files\Contoso Office\EXCEL.EXE"; }
            if (outlook) { fake.Installed[OfficeApp.Outlook] = @"C:\Program Files\Contoso Office\OUTLOOK.EXE"; }
            if (powerPoint) { fake.Installed[OfficeApp.PowerPoint] = @"C:\Program Files\Contoso Office\POWERPNT.EXE"; }
            return fake;
        }

        public static FakeOffice All() => With(true, true, true, true);

        public string Find(OfficeApp app) => Installed.TryGetValue(app, out var path) ? path : null;

        public bool IsRunning(OfficeApp app) => Running.Contains(app);

        public bool Start(string path, string arguments)
        {
            Starts.Add((path, arguments));
            return StartWorks;
        }
    }

    [Fact]
    public void A_start_from_For_IT_asks_nothing_and_is_start_requested_without_a_badge()
    {
        var office = FakeOffice.With(word: true, excel: false);
        var outcome = OfficeSafeMode.Start(office, OfficeApp.Word, () => throw new InvalidOperationException("not running: no question"));
        Assert.Equal(SafeModeResult.StartRequested, outcome.Result);
        Assert.False(outcome.AwaitsAnswer);
        var start = Assert.Single(office.Starts);
        Assert.Equal(WordPath, start.Path);
        Assert.Equal("/safe", start.Arguments);
        var (status, tone, card) = OfficeSafeMode.Present(outcome);
        Assert.Equal("Word was started in safe mode.", status);
        Assert.Equal(Tone.Neutral, tone);
        Assert.Equal("", Words.ResultWord(tone));
        Assert.Equal("REQUESTED", card.Word);
        Assert.Equal("Start requested: Word in safe mode", card.Title);
        Assert.Contains("the normal start is not changed", card.Detail);
        Assert.Equal("", card.NextStep);
        Assert.Equal("Start requested", OfficeSafeMode.OutcomeWords(outcome.Result));
        // An answer does nothing to a start that asked no question.
        OfficeSafeMode.Answer(outcome, SafeModeAnswer.Yes);
        Assert.Equal(SafeModeResult.StartRequested, outcome.Result);
    }

    [Theory]
    [InlineData(OfficeApp.Word, "winword.exe", "Could you open a document?")]
    [InlineData(OfficeApp.Excel, "excel.exe", "Could you open a workbook?")]
    [InlineData(OfficeApp.Outlook, "outlook.exe", "Did Outlook stay open?")]
    [InlineData(OfficeApp.PowerPoint, "powerpnt.exe", "Could you open a presentation?")]
    public void A_start_from_the_path_asks_the_apps_own_question(OfficeApp app, string exe, string question)
    {
        Assert.Equal(exe, OfficeSafeMode.ExeName(app));
        Assert.Equal(question, OfficeSafeMode.Question(app));
        var outcome = OfficeSafeMode.Start(FakeOffice.All(), app, null, asks: true);
        Assert.True(outcome.AwaitsAnswer);
        var (status, _, card) = OfficeSafeMode.Present(outcome);
        Assert.Equal(OfficeSafeMode.AppName(app) + " was started in safe mode. " + question, status);
        Assert.Contains("answer the question", card.NextStep);
        var names = OfficeSafeMode.AnswerNames(app);
        Assert.StartsWith("Yes", names.Yes);
        Assert.StartsWith("No", names.No);
        Assert.StartsWith("Not tried", names.NotTried);
        Assert.Equal("Starting " + OfficeSafeMode.AppName(app) + "\u2026", OfficeSafeMode.StartingText(app));
    }

    [Fact]
    public void Outlook_is_named_classic_and_never_the_new_Outlook()
    {
        Assert.Equal("Outlook (classic)", OfficeSafeMode.AppName(OfficeApp.Outlook));
        Assert.Equal("Start Outlook (classic) in safe mode", OfficeSafeMode.ToolName(OfficeApp.Outlook));
        Assert.Equal("outlook.exe", OfficeSafeMode.ExeName(OfficeApp.Outlook));
        Assert.DoesNotContain(OfficeSafeMode.Apps, a => OfficeSafeMode.ExeName(a).Contains("olk"));
        Assert.Equal(new[] { OfficeApp.Word, OfficeApp.Excel, OfficeApp.Outlook, OfficeApp.PowerPoint }, OfficeSafeMode.Apps);
        Assert.Equal("outlook", OfficeSafeMode.LogName(OfficeApp.Outlook));
    }

    [Fact]
    public void A_missing_app_is_not_started_and_says_it_is_not_installed()
    {
        var office = FakeOffice.With(word: false, excel: true);
        var outcome = OfficeSafeMode.Start(office, OfficeApp.Word, () => true);
        Assert.Equal(SafeModeResult.NotInstalled, outcome.Result);
        Assert.Empty(office.Starts);
        Assert.Equal("Not available: Word is not installed on this PC", OfficeSafeMode.NotInstalledText(OfficeApp.Word));
        Assert.Equal("Not available: PowerPoint is not installed on this PC", OfficeSafeMode.NotInstalledText(OfficeApp.PowerPoint));
    }

    [Fact]
    public void An_app_that_is_already_running_is_never_started_a_second_time_blindly_or_closed()
    {
        var office = FakeOffice.With(word: true, excel: true);
        office.Running.Add(OfficeApp.Excel);
        var asked = 0;
        // The user does not close it: nothing starts.
        var outcome = OfficeSafeMode.Start(office, OfficeApp.Excel, () => { asked++; return false; });
        Assert.Equal(SafeModeResult.AlreadyRunning, outcome.Result);
        Assert.Equal(1, asked);
        Assert.Empty(office.Starts);
        // The user says it is closed, but it still runs: nothing starts either.
        outcome = OfficeSafeMode.Start(office, OfficeApp.Excel, () => true);
        Assert.Equal(SafeModeResult.AlreadyRunning, outcome.Result);
        Assert.Empty(office.Starts);
        // Closed normally: then it starts in safe mode.
        outcome = OfficeSafeMode.Start(office, OfficeApp.Excel, () => { office.Running.Clear(); return true; });
        Assert.Equal(SafeModeResult.StartRequested, outcome.Result);
        Assert.Equal("/safe", Assert.Single(office.Starts).Arguments);
        var (status, tone, card) = OfficeSafeMode.Present(new SafeModeOutcome { App = OfficeApp.Excel, Result = SafeModeResult.AlreadyRunning });
        Assert.Equal("Excel is already open, so nothing was started. Close it normally first, then try again.", status);
        Assert.Equal(Tone.Warning, tone);
        Assert.Contains("never closes it for you", card.Detail);
        Assert.Equal("Already open", OfficeSafeMode.OutcomeWords(SafeModeResult.AlreadyRunning));
    }

    [Theory]
    [InlineData(OfficeApp.Word, SafeModeAnswer.Yes, SafeModeResult.WorksInSafeMode, "works-in-safe-mode", Tone.Warning)]
    [InlineData(OfficeApp.Excel, SafeModeAnswer.No, SafeModeResult.AlsoFailsInSafeMode, "also-fails-in-safe-mode", Tone.Error)]
    [InlineData(OfficeApp.PowerPoint, SafeModeAnswer.NotTried, SafeModeResult.NotConfirmed, "not-confirmed", Tone.Neutral)]
    [InlineData(OfficeApp.Outlook, SafeModeAnswer.Yes, SafeModeResult.WorksInSafeMode, "works-in-safe-mode", Tone.Warning)]
    [InlineData(OfficeApp.Outlook, SafeModeAnswer.No, SafeModeResult.AlsoFailsInSafeMode, "also-fails-in-safe-mode", Tone.Error)]
    [InlineData(OfficeApp.Outlook, SafeModeAnswer.NotTried, SafeModeResult.NotConfirmed, "not-confirmed", Tone.Neutral)]
    public void Each_answer_is_its_own_result_and_never_says_the_normal_start_is_fixed(OfficeApp app, SafeModeAnswer answer, SafeModeResult result, string slug, Tone tone)
    {
        var outcome = OfficeSafeMode.Start(FakeOffice.All(), app, null, asks: true);
        OfficeSafeMode.Answer(outcome, answer);
        Assert.Equal(result, outcome.Result);
        Assert.False(outcome.AwaitsAnswer);
        var (status, actualTone, card) = OfficeSafeMode.Present(outcome);
        Assert.Equal(tone, actualTone);
        Assert.DoesNotMatch("(?i)(is|was|now) fixed", status + card.Detail + card.NextStep);
        Assert.DoesNotContain("  ", card.Detail);
        Assert.Equal(slug, OfficeSafeMode.Slug(outcome.Result));
        if (answer == SafeModeAnswer.Yes)
        {
            Assert.Contains("not proven", status);
            Assert.Contains("add-in", card.Support);
        }
        // A second answer changes nothing.
        OfficeSafeMode.Answer(outcome, SafeModeAnswer.No);
        Assert.Equal(result, outcome.Result);
    }

    [Fact]
    public void The_run_log_and_the_history_carry_the_app_and_the_result_only()
    {
        var outcome = OfficeSafeMode.Answer(OfficeSafeMode.Start(FakeOffice.With(true, false), OfficeApp.Word, null, asks: true), SafeModeAnswer.Yes);
        var line = OfficeSafeMode.LogLine(outcome, "local-ab12cd34");
        Assert.StartsWith("OFFICE run=local-ab12cd34 time=", line);
        Assert.EndsWith(" context=user-app app=word switch=/safe result=works-in-safe-mode", line);
        Assert.DoesNotContain("Program Files", line);
        Assert.Equal("0|local-ab12cd34|works-in-safe-mode", OfficeSafeMode.HistoryResult(outcome, "local-ab12cd34"));
        Assert.Equal("2026-11-12 11:00 +01:00  Start Word in safe mode: works in safe mode (0, reference local-ab12cd34)",
            RunWords.HistoryLine("2026-11-12 11:00:00 +01:00;CONTOSO\\user;WordSafeMode;0|local-ab12cd34|works-in-safe-mode"));
        // A start without an answer is "start requested" (the earlier slug still reads the same).
        var requested = OfficeSafeMode.Start(FakeOffice.All(), OfficeApp.PowerPoint, null);
        Assert.Equal("3|local-ab12cd34|start-requested", OfficeSafeMode.HistoryResult(requested, "local-ab12cd34"));
        Assert.Equal("2026-11-12 11:00 +01:00  Start PowerPoint in safe mode: start requested (3, reference local-ab12cd34)",
            RunWords.HistoryLine("2026-11-12 11:00:00 +01:00;CONTOSO\\user;PowerPointSafeMode;3|local-ab12cd34|start-requested"));
        Assert.Equal("2026-11-12 11:00 +01:00  Start Word in safe mode: start requested (3, reference local-ab12cd34)",
            RunWords.HistoryLine("2026-11-12 11:00:00 +01:00;CONTOSO\\user;WordSafeMode;3|local-ab12cd34|safe-start-requested"));
        Assert.EndsWith("app=powerpoint switch=/safe result=start-requested", OfficeSafeMode.LogLine(requested, "local-ab12cd34"));
        Assert.Equal(OfficeApp.Outlook, OfficeSafeMode.FromHistoryName("OutlookSafeMode"));
        Assert.Null(OfficeSafeMode.FromHistoryName("OlkSafeMode"));
    }

    [Fact]
    public void One_run_is_one_history_line_the_answer_replaces_the_start()
    {
        using var folder = new TestFolder();
        var file = folder.Combine("history.log");
        History.Add("SyncClock", "0", file);
        History.Put("WordSafeMode", "3|local-ab12cd34|start-requested", "local-ab12cd34", file);
        History.Put("ExcelSafeMode", "3|local-99887766|start-requested", "local-99887766", file);
        History.Put("WordSafeMode", "0|local-ab12cd34|works-in-safe-mode", "local-ab12cd34", file);
        var lines = File.ReadAllLines(file);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith(";WordSafeMode;0|local-ab12cd34|works-in-safe-mode", lines[1]);
        Assert.EndsWith(";ExcelSafeMode;3|local-99887766|start-requested", lines[2]);
        Assert.DoesNotContain(lines, l => l.Contains("ENDED"));
    }

    [Fact]
    public void A_start_Windows_refuses_is_said_without_a_path_and_keeps_its_reason()
    {
        var office = FakeOffice.With(true, false);
        office.StartWorks = false;
        var outcome = OfficeSafeMode.Start(office, OfficeApp.Word, null);
        Assert.Equal(SafeModeResult.CouldNotStart, outcome.Result);
        var (status, tone, card) = OfficeSafeMode.Present(outcome);
        Assert.Equal(Tone.Error, tone);
        Assert.Contains("Restart the device and try again", status);
        Assert.Equal("Could not start", OfficeSafeMode.OutcomeWords(outcome.Result));
        Assert.DoesNotContain("Program Files", status + card.Detail + card.Support);
    }

    [Fact]
    public void Only_a_full_path_to_an_existing_program_counts_as_installed()
    {
        using var folder = new TestFolder();
        var exe = folder.Combine("WINWORD.EXE");
        File.WriteAllText(exe, "not a real program");
        Assert.Equal(exe, OfficeSafeMode.CleanPath("\"" + exe + "\""));
        Assert.Null(OfficeSafeMode.CleanPath(folder.Combine("EXCEL.EXE")));
        Assert.Null(OfficeSafeMode.CleanPath("winword.exe"));
        Assert.Null(OfficeSafeMode.CleanPath(folder.Combine("readme.txt")));
        Assert.Null(OfficeSafeMode.CleanPath(""));
    }

    [Fact]
    public void The_tools_live_in_Sign_in_and_Microsoft_365_and_are_found_by_office_words()
    {
        var expected = new[] { "WordSafeButton", "ExcelSafeButton", "OutlookSafeButton", "PowerPointSafeButton" };
        foreach (var app in OfficeSafeMode.Apps)
        {
            var tool = ToolCatalog.ByButton(OfficeSafeMode.Button(app));
            Assert.Equal(OfficeSafeMode.ToolName(app), tool.Name);
            Assert.Equal(ToolGroup.SignIn, tool.Group);
            Assert.Equal(app, tool.Office);
            Assert.Equal(OfficeSafeMode.AppName(app), tool.ShortName);
            Assert.False(tool.InService);
        }
        foreach (var query in new[] { "word", "safe mode", "crash", "closes" })
        {
            Assert.Contains(ToolCatalog.Search(query), t => t.Button == "WordSafeButton");
        }
        Assert.Equal("WordSafeButton", ToolCatalog.Search("word").First().Button);
        Assert.Equal("OutlookSafeButton", ToolCatalog.Search("outlook classic").First().Button);
        Assert.Equal("PowerPointSafeButton", ToolCatalog.Search("powerpoint").First().Button);
        Assert.Equal(expected, ProblemPaths.Get(Problem.OfficeCloses).Tools);
        Assert.Equal(new[] { "ExcelSafeButton" }, ProblemPaths.ToolsShown(ProblemPaths.Office, b => b == "ExcelSafeButton"));
        Assert.Equal("An Office app closes right after it opens", ProblemPaths.Office.Title);
        Assert.Equal("Word, Excel, Outlook (classic), PowerPoint", ProblemPaths.Office.Subtitle);
        Assert.Empty(ProblemPaths.Office.Rows);
    }
}
