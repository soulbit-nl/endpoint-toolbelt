using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>Reset OneDrive, with a fake OneDrive (nothing is started or reset for real).</summary>
public class OneDriveResetTests
{
    private const string OneDrivePath = @"C:\Users\Contoso\AppData\Local\Microsoft\OneDrive\OneDrive.exe";

    /// <summary>OneDrive installed or not, how the reset and a plain start go, whether it comes back by itself, and every start asked for.</summary>
    internal sealed class FakeOneDrive : IOneDrive
    {
        public string Path { get; set; }
        public ProgramRun ResetRun { get; set; } = ProgramRun.Exited;
        public ProgramRun PlainStart { get; set; } = ProgramRun.StillRunning;
        public bool ComesBack { get; set; } = true;
        public List<(string Path, string Arguments, TimeSpan Wait)> Starts { get; } = new();
        public List<TimeSpan> Waits { get; } = new();

        public static FakeOneDrive Installed() => new() { Path = OneDrivePath };

        public string Find() => Path;

        public bool IsRunning() => false;

        public ProgramRun Start(string path, string arguments, TimeSpan wait)
        {
            Starts.Add((path, arguments, wait));
            return arguments == "/reset" ? ResetRun : PlainStart;
        }

        public bool WaitUntilRunning(TimeSpan limit)
        {
            Waits.Add(limit);
            return ComesBack;
        }
    }

    [Fact]
    public void The_reset_runs_the_documented_switch_and_OneDrive_that_comes_back_is_reset_requested()
    {
        var oneDrive = FakeOneDrive.Installed();
        var waited = 0;
        var outcome = OneDriveReset.Run(oneDrive, waiting: () => waited++);
        Assert.Equal(OneDriveResetResult.ResetRequested, outcome.Result);
        var reset = Assert.Single(oneDrive.Starts);
        Assert.Equal(OneDrivePath, reset.Path);
        Assert.Equal("/reset", reset.Arguments);
        Assert.Equal(OneDriveReset.ResetWait, reset.Wait);
        Assert.Equal(new[] { OneDriveReset.RestartWait }, oneDrive.Waits);
        Assert.Equal(1, waited);
        var (status, tone, card) = OneDriveReset.Present(outcome);
        Assert.Equal(Tone.Neutral, tone);
        Assert.Equal("REQUESTED", card.Word);
        Assert.Equal("Reset requested: OneDrive", card.Title);
        Assert.StartsWith("OneDrive reset requested.", status);
        Assert.Equal("Reset requested", OneDriveReset.OutcomeWords(outcome.Result));
        // Never a claim that a stuck file uploads now.
        Assert.DoesNotMatch("(?i)(is|was|now) (fixed|synced|uploaded)|sync works", status + card.Detail + card.NextStep);
        Assert.Contains("not known yet", card.Detail);
    }

    [Fact]
    public void OneDrive_that_does_not_come_back_within_a_few_minutes_is_started_again()
    {
        var oneDrive = FakeOneDrive.Installed();
        oneDrive.ComesBack = false;
        var outcome = OneDriveReset.Run(oneDrive);
        Assert.Equal(OneDriveResetResult.ResetRequestedStarted, outcome.Result);
        Assert.Equal(new[] { "/reset", "" }, oneDrive.Starts.Select(s => s.Arguments));
        Assert.True(OneDriveReset.RestartWait >= TimeSpan.FromMinutes(2));
        Assert.Equal("Reset requested", OneDriveReset.OutcomeWords(outcome.Result));
        Assert.EndsWith("restart=started result=reset-requested-started", OneDriveReset.LogLine(outcome, "local-ab12cd34"));

        // Windows does not start it either: the user is told how to start it.
        oneDrive = FakeOneDrive.Installed();
        oneDrive.ComesBack = false;
        oneDrive.PlainStart = ProgramRun.NotStarted;
        outcome = OneDriveReset.Run(oneDrive);
        Assert.Equal(OneDriveResetResult.ResetRequestedNotRunning, outcome.Result);
        var (status, tone, card) = OneDriveReset.Present(outcome);
        Assert.Equal(Tone.Warning, tone);
        Assert.Contains("Start menu", status + card.NextStep);
    }

    [Fact]
    public void A_reset_still_running_after_its_wait_starts_no_second_OneDrive()
    {
        var oneDrive = FakeOneDrive.Installed();
        oneDrive.ResetRun = ProgramRun.StillRunning;
        var outcome = OneDriveReset.Run(oneDrive);
        Assert.Equal(OneDriveResetResult.ResetRequested, outcome.Result);
        Assert.Single(oneDrive.Starts);
        Assert.Empty(oneDrive.Waits);
    }

    [Fact]
    public void Not_installed_and_a_refused_reset_change_nothing_and_say_so()
    {
        var missing = new FakeOneDrive();
        var outcome = OneDriveReset.Run(missing);
        Assert.Equal(OneDriveResetResult.NotInstalled, outcome.Result);
        Assert.Empty(missing.Starts);
        Assert.Equal("Not available: OneDrive is not installed on this PC.", OneDriveReset.Present(outcome).Status);

        var refused = FakeOneDrive.Installed();
        refused.ResetRun = ProgramRun.NotStarted;
        outcome = OneDriveReset.Run(refused);
        Assert.Equal(OneDriveResetResult.CouldNotReset, outcome.Result);
        Assert.Single(refused.Starts);
        var (status, tone, card) = OneDriveReset.Present(outcome);
        Assert.Equal(Tone.Error, tone);
        Assert.Contains("Nothing was changed", status);
        Assert.Equal("Could not reset", OneDriveReset.OutcomeWords(outcome.Result));
    }

    [Fact]
    public void The_records_carry_the_result_only_never_a_path()
    {
        var outcome = OneDriveReset.Run(FakeOneDrive.Installed());
        var line = OneDriveReset.LogLine(outcome, "local-ab12cd34");
        Assert.StartsWith("ONEDRIVE run=local-ab12cd34 time=", line);
        Assert.EndsWith(" context=user-app switch=/reset restart=self result=reset-requested", line);
        Assert.DoesNotContain("Users", line);
        Assert.Equal("0|local-ab12cd34|reset-requested", OneDriveReset.HistoryResult(outcome, "local-ab12cd34"));
        Assert.Equal("2026-11-13 10:42 +01:00  Reset OneDrive: reset requested (0, reference local-ab12cd34)",
            RunWords.HistoryLine("2026-11-13 10:42:00 +01:00;CONTOSO\\user;ResetOneDrive;0|local-ab12cd34|reset-requested"));
        var (status, _, card) = OneDriveReset.Present(outcome);
        Assert.DoesNotContain("AppData", status + card.Detail + card.NextStep + card.Support);
    }

    [Fact]
    public void The_confirmation_says_what_the_owner_wrote_and_the_path_says_how_to_resume_from_the_icon()
    {
        // Followed by the connection OneDrive needs afterwards.
        Assert.Equal("All accounts in OneDrive, work and personal, sync again from the start. Files stay where they are. This can take a while. " + ConnectionNeeds.OneDrive, OneDriveReset.ConfirmText);
        // No documented command resumes a paused OneDrive: the path says how, from its icon.
        var path = ProblemPaths.Get(Problem.OneDriveSync);
        Assert.Equal("OneDrive doesn't sync", path.Title);
        Assert.Equal("Files stay on \"pending\" or show red crosses", path.Subtitle);
        Assert.Contains("OneDrive icon", path.Intro);
        Assert.Contains("resume", path.Intro);
        Assert.Equal(new[] { "ResetOneDriveButton" }, path.Tools);
        Assert.Empty(path.Rows);
        var tool = ToolCatalog.ByButton("ResetOneDriveButton");
        Assert.Equal("Reset OneDrive", tool.Name);
        Assert.Equal("OneDrive", tool.ShortName);
        Assert.Equal(ToolGroup.NetworkStorage, tool.Group);
        Assert.True(tool.NeedsOneDrive);
        Assert.False(tool.InService);
    }

    [Theory]
    [InlineData("pending", "ResetOneDriveButton")]
    [InlineData("red crosses", "ResetOneDriveButton")]
    [InlineData("offline", "ConnectivityButton")]
    [InlineData("monitor", "WindowBackButton")]
    [InlineData("undocking", "WindowBackButton")]
    [InlineData("password", "ResetSignInButton")]
    [InlineData("keeps asking", "RefreshSignInButton")]
    [InlineData("comes back", "RepairWuButton")]
    [InlineData("stuck", "ClearCacheButton")]
    public void The_For_IT_search_finds_the_tools_by_the_problem_rows_symptom_words(string query, string button)
        => Assert.Contains(ToolCatalog.Search(query), t => t.Button == button);

    [Fact]
    public void The_path_shows_Reset_OneDrive_off_without_OneDrive_and_on_with_it()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            var row = MainWindowPagesTests.Get<Button>(window, "ProblemOneDriveButton");
            Assert.Equal(Visibility.Visible, row.Visibility);
            Assert.Equal("OneDrive doesn't sync. Files stay on \"pending\" or show red crosses", AutomationProperties.GetName(row));
            MainWindowPagesTests.Call(window, "OnProblemOneDrive", row, new RoutedEventArgs());
            Assert.Equal("OneDrive doesn't sync", MainWindowPagesTests.Get<TextBlock>(window, "ProblemTitle").Text);
            Assert.Contains("OneDrive icon", MainWindowPagesTests.Get<TextBlock>(window, "ProblemIntro").Text);
            var tools = ((IEnumerable<ToolRowView>)MainWindowPagesTests.Get<ItemsControl>(window, "ProblemTools").ItemsSource).ToList();
            var reset = Assert.Single(tools);
            // The Sandbox case: OneDrive not installed is said honestly; the row stays, its Run is off.
            Assert.Equal("Not available", reset.ResultText);
            Assert.StartsWith(OneDriveReset.NotInstalledText, reset.Line);
            Assert.False(reset.RunTarget.IsEnabled);

            window.OneDrive = FakeOneDrive.Installed();
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.OneDriveSync, false);
            reset = Assert.Single((IEnumerable<ToolRowView>)MainWindowPagesTests.Get<ItemsControl>(window, "ProblemTools").ItemsSource);
            Assert.True(reset.RunTarget.IsEnabled);
            Assert.Equal("", reset.ResultText);
        });
    }

    [Fact]
    public void Cancel_on_the_confirmation_resets_nothing_and_records_nothing()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var oneDrive = FakeOneDrive.Installed();
            window.OneDrive = oneDrive;
            var asked = 0;
            window.ConfirmOneDriveReset = () => { asked++; return false; };
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.OneDriveSync, false);
            MainWindowPagesTests.Call(window, "OnResetOneDrive", window, new RoutedEventArgs());
            MainWindowPagesTests.Drain();
            Assert.Equal(1, asked);
            Assert.Empty(oneDrive.Starts);
            Assert.False(File.Exists(window.HistoryOverride));
        });
    }

    [Fact]
    public void A_confirmed_reset_is_reset_requested_in_the_status_the_history_and_the_run_log()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var oneDrive = FakeOneDrive.Installed();
            window.OneDrive = oneDrive;
            window.ConfirmOneDriveReset = () => true;
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.OneDriveSync, false);
            MainWindowPagesTests.Call(window, "OnResetOneDrive", window, new RoutedEventArgs());
            MainWindowPagesTests.WaitFor(() => !(bool)MainWindowPagesTests.Field(window, "_busy"));
            Assert.Equal("/reset", oneDrive.Starts.First().Arguments);
            Assert.StartsWith("OneDrive reset requested.", MainWindowPagesTests.Get<TextBlock>(window, "StatusText").Text);
            Assert.Equal("", MainWindowPagesTests.Get<TextBlock>(window, "StatusWord").Text);
            var history = File.ReadAllLines(window.HistoryOverride);
            Assert.EndsWith("|reset-requested", Assert.Single(history));
            Assert.Contains(";ResetOneDrive;0|local-", history[0]);
            MainWindowPagesTests.WaitFor(() => Directory.Exists(window.RunsFolderOverride) && Directory.GetFiles(window.RunsFolderOverride, "*_ResetOneDrive_*.log").Length == 1);
            // The run history writes the file on a background task, so it can still be open or unfinished here
            // (a slow host, coverage, a CI runner): read it again for a moment, as ComplianceCheckTests does.
            var path = Directory.GetFiles(window.RunsFolderOverride, "*_ResetOneDrive_*.log").Single();
            string[] log = null;
            for (var i = 0; i < 50; i++)
            {
                try
                {
                    log = File.ReadAllLines(path);
                    if (log.Any(l => l.StartsWith("Outcome", StringComparison.Ordinal))) { break; }
                }
                catch (IOException)
                {
                    log = null;
                }
                System.Threading.Thread.Sleep(100);
            }
            Assert.NotNull(log);
            Assert.Contains("Outcome   : Reset requested", log);
            Assert.Equal(RunVerdict.Requested, ToolRuns.Parse(log));
            Assert.DoesNotContain(log, l => l.Contains("AppData"));
        });
    }
}
