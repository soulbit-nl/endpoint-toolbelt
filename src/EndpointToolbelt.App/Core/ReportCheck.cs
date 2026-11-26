using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// One read-only check in the HTML report. The export runs the checks first, so the page holds
/// their results of this moment; a check the service did not run (the hourly limit of read-only
/// checks, another action running, the service not reachable) is shown with its newest result
/// from the user's run history and a line that says why, so an export is never refused.
/// </summary>
public sealed class ReportCheck
{
    /// <summary>The check's name as the app says it ("Check Defender blocks").</summary>
    public string Name { get; set; } = "";

    /// <summary>When the result was made; null when there is none.</summary>
    public DateTime? RanUtc { get; set; }

    /// <summary>The check ran for this report.</summary>
    public bool RanForReport { get; set; }

    /// <summary>The result in a sentence; empty when there is none.</summary>
    public string Status { get; set; } = "";

    public Tone Tone { get; set; } = Tone.Neutral;

    public List<ResultCard> Cards { get; } = new();

    /// <summary>Why the check did not run for this report; empty when it did.</summary>
    public string NotRunBecause { get; set; } = "";

    /// <summary>The check's anchor on the page: "check-" and its name in lower case with dashes.</summary>
    public string Anchor => "check-" + new string(Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    /// <summary>A check that ran for the report.</summary>
    public static ReportCheck Ran(string name, string status, Tone tone, IEnumerable<ResultCard> cards, DateTime ranUtc)
    {
        var check = new ReportCheck { Name = name, Status = status ?? "", Tone = tone, RanUtc = ranUtc, RanForReport = true };
        check.Cards.AddRange((cards ?? Enumerable.Empty<ResultCard>()).Where(c => c != null && c.Kind != CardKind.Note));
        return check;
    }

    /// <summary>Why a check the organization switched off did not run.</summary>
    public const string SwitchedOff = "your organization switched this check off";

    /// <summary>Why the service did not run a check, in words; null when it ran.</summary>
    public static string WhyNot(RunOutcome outcome)
    {
        if (outcome == null) { return "the " + ProductInfo.DisplayName + " service could not be reached"; }
        return outcome.Result switch
        {
            ResultCode.Completed => null,
            ResultCode.RateLimited => "the hourly limit of read-only checks was reached",
            ResultCode.Busy => "another action was running",
            ResultCode.EngineTimeout => "it took longer than its time limit",
            _ => "the service did not run it (" + outcome.Result.ToString() + ")",
        };
    }

    /// <summary>
    /// A check that did not run for the report: its newest result from the run history when there
    /// is one (<paramref name="log"/>), else only why.
    /// </summary>
    public static ReportCheck Stored(string name, string why, StoredRunLog log)
    {
        var check = new ReportCheck { Name = name, NotRunBecause = why ?? "" };
        if (log == null || string.IsNullOrEmpty(log.Path)) { return check; }
        List<string> lines;
        try { lines = File.ReadLines(log.Path).Take(400).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return check;
        }
        var verdict = ToolRuns.Parse(lines);
        var outcome = ToolRuns.OutcomeOf(verdict, lines);
        check.RanUtc = log.StartedUtc ?? log.WrittenUtc;
        check.Status = outcome.Length > 0 ? outcome : ToolRuns.Phrase(verdict);
        check.Tone = ToolRuns.ToneOf(verdict);
        check.Cards.AddRange(CardsFromLog(lines));
        return check;
    }

    /// <summary>The line under the check's heading: when it ran, or why it did not and from when the result is.</summary>
    public string When(TimeZoneInfo zone, DateTime nowUtc)
    {
        var at = RanUtc is DateTime utc ? ToolRuns.ShortWhen(utc, zone, nowUtc) : "";
        if (RanForReport) { return "Run for this report, " + at + "."; }
        if (RanUtc == null) { return "Not run for this report: " + NotRunBecause + "."; }
        return "Not run again for this report: " + NotRunBecause + ". The result below is the newest in your run history, from " + at + ".";
    }

    /// <summary>A card's kind from its word in a run log.</summary>
    internal static CardKind KindOf(string word) => word switch
    {
        "OK" => CardKind.Ok,
        "PROBLEM" => CardKind.Error,
        "CHANGED ON THIS DEVICE" => CardKind.Change,
        _ when word == Words.WarningBadge || word == Words.EarlierWarningBadge => CardKind.Warning,
        _ => CardKind.Info,
    };

    /// <summary>The result cards of a run log ("[WORD] Title", then its indented detail, next step and support lines).</summary>
    internal static List<ResultCard> CardsFromLog(IEnumerable<string> lines)
    {
        var cards = new List<ResultCard>();
        var inResult = false;
        ResultCard card = null;
        var detail = new List<string>();
        void Close()
        {
            if (card == null) { return; }
            card.Detail = string.Join(Environment.NewLine, detail);
            cards.Add(card);
            card = null;
            detail.Clear();
        }
        foreach (var line in lines ?? Enumerable.Empty<string>())
        {
            if (line == null) { continue; }
            if (line.StartsWith("----- RESULT -----", StringComparison.Ordinal)) { inResult = true; continue; }
            if (!inResult) { continue; }
            if (line.StartsWith("----- ", StringComparison.Ordinal)) { break; }
            var close = line.StartsWith("[", StringComparison.Ordinal) ? line.IndexOf(']') : -1;
            if (close > 1)
            {
                Close();
                var word = line.Substring(1, close - 1);
                card = new ResultCard { Kind = KindOf(word), Word = word, Title = line.Substring(close + 1).Trim(), Detail = "", NextStep = "", Support = "" };
                continue;
            }
            if (card == null || line.Trim().Length == 0) { continue; }
            var text = line.Trim();
            if (text.StartsWith("Next step: ", StringComparison.Ordinal)) { card.NextStep = text.Substring("Next step: ".Length); }
            else if (text.StartsWith("For IT support: ", StringComparison.Ordinal)) { card.Support = text.Substring("For IT support: ".Length); }
            else { detail.Add(text); }
        }
        Close();
        return cards;
    }
}
