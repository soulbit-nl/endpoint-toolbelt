using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Protocol;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// A read-only check in the HTML report: the result of a run for the report, or, when the service
/// did not run it, the newest result from the run history with why. The export is never refused.
/// </summary>
public class ReportCheckTests
{
    [Fact]
    public void Why_a_check_did_not_run_is_said_in_words_and_a_completed_run_has_no_reason()
    {
        Assert.Null(ReportCheck.WhyNot(new RunOutcome { Result = ResultCode.Completed }));
        Assert.Equal("the hourly limit of read-only checks was reached", ReportCheck.WhyNot(new RunOutcome { Result = ResultCode.RateLimited }));
        Assert.Equal("another action was running", ReportCheck.WhyNot(new RunOutcome { Result = ResultCode.Busy }));
        Assert.Equal("the Endpoint Toolbelt service could not be reached", ReportCheck.WhyNot(null));
        Assert.StartsWith("the service did not run it (", ReportCheck.WhyNot(new RunOutcome { Result = ResultCode.InvalidRequest }));
    }

    [Fact]
    public void A_stored_result_is_read_back_from_its_run_log_with_its_cards()
    {
        using var folder = new TestFolder();
        var path = folder.Combine("20261119-130000Z_CheckWindowsUpdate_ab12cd34.log");
        var started = new DateTime(2026, 11, 19, 13, 0, 0, DateTimeKind.Utc);
        var header = new RunLogHeader { Outcome = new RunOutcome { Action = ToolbeltAction.CheckWindowsUpdate, RunId = "ab12cd34", Result = ResultCode.Completed, ExitCode = 2, StartedUtc = started }, Zone = TimeZoneInfo.Utc, NowUtc = started.AddMinutes(1) };
        var cards = new[]
        {
            ResultCard.From(FindingSeverity.Warning, "Updates are paused", "Paused for 7 days.\nSet by a policy.", "Ask IT support.", "Policy PauseUpdates"),
            ResultCard.From(FindingSeverity.Ok, "The update service runs"),
        };
        SupportPackage.WriteRunLog(header, new[] { "a technical line", "[PROBLEM] never a card" }, cards, path);
        var check = ReportCheck.Stored("Check Windows Update", "the hourly limit of read-only checks was reached", new StoredRunLog { Path = path, StartedUtc = started });
        Assert.False(check.RanForReport);
        Assert.Equal(started, check.RanUtc);
        Assert.Equal(Tone.Warning, check.Tone);
        Assert.Equal(2, check.Cards.Count);
        Assert.Equal(CardKind.Warning, check.Cards[0].Kind);
        Assert.Equal("Updates are paused", check.Cards[0].Title);
        Assert.Equal("Paused for 7 days." + Environment.NewLine + "Set by a policy.", check.Cards[0].Detail);
        Assert.Equal("Ask IT support.", check.Cards[0].NextStep);
        Assert.Equal("Policy PauseUpdates", check.Cards[0].Support);
        Assert.Equal(CardKind.Ok, check.Cards[1].Kind);
        Assert.Equal("Not run again for this report: the hourly limit of read-only checks was reached. The result below is the newest in your run history, from 19 Nov 2026 13:00.",
            check.When(TimeZoneInfo.Utc, new DateTime(2027, 1, 2, 9, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Without_a_stored_result_only_the_reason_is_said_and_an_unreadable_log_is_no_result()
    {
        var none = ReportCheck.Stored("Check Windows Update", "another action was running", null);
        Assert.Null(none.RanUtc);
        Assert.Empty(none.Cards);
        Assert.Equal("Not run for this report: another action was running.", none.When(TimeZoneInfo.Utc, DateTime.UtcNow));
        var missing = ReportCheck.Stored("Check Windows Update", "another action was running", new StoredRunLog { Path = @"C:\nowhere\x.log" });
        Assert.Null(missing.RanUtc);
    }

    [Fact]
    public void A_check_run_for_the_report_keeps_its_cards_but_not_a_note_about_the_run()
    {
        var at = new DateTime(2026, 11, 19, 16, 0, 0, DateTimeKind.Utc);
        var check = ReportCheck.Ran("Check Microsoft connections", "All connections work.", Tone.Success, new[] { ResultCard.From(FindingSeverity.Ok, "login.microsoftonline.com"), ResultCard.NothingChanged(), null }, at);
        Assert.True(check.RanForReport);
        Assert.Single(check.Cards);
        Assert.Equal("check-check-microsoft-connections", check.Anchor);
        Assert.Equal("Run for this report, today 16:00.", check.When(TimeZoneInfo.Utc, at.AddMinutes(1)));
    }
}
