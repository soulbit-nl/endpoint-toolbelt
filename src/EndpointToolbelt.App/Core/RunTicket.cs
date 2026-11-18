using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// "Copy details for IT": a short ticket text for the attempt shown, next to "Copy summary".
/// Five lines, each "Label · text":
/// <code>
/// Endpoint Toolbelt · Retry work apps and settings · Reference ab12cd34
/// Evidence · 2026-10-15 14:04 UTC+02:00 · engine summary · PROBLEM: The IME service is not running
/// Action · Retry work apps and settings, run by the service as SYSTEM
/// Outcome · Retry work apps and settings did not succeed: The IME service is not running
/// Next · User: Restart the device. IT support: Check the IME installation.
/// </code>
/// It points to the run by its run ID (the service's, or the app's own local- ID), also while the
/// run goes on; it names no device, no user and no log line, and it never calls a run a success
/// unless that was verified. For an app after a remedy, a sixth line carries the original
/// diagnosis, the remedy and the post-check result. The window puts the first-line facts
/// right after the first line (the app version, the last restart, the join type, the MDM enrollment
/// ID, the network and the health checks in one line), and Home and the problem paths copy a
/// ticket of the same form (<see cref="Page"/>).
/// </summary>
public static class RunTicket
{
    public const string Separator = " · ";

    private const int MaxObserved = 120;
    private const int MaxOutcome = 300;
    private const int MaxPostCheck = 400;
    private static readonly char[] LineBreaks = { '\r', '\n', '\t' };

    /// <param name="header">The run shown, at the moment of copying.</param>
    /// <param name="cards">The result cards shown.</param>
    /// <param name="status">The status line shown for that run.</param>
    /// <param name="tone">The status line's tone.</param>
    /// <param name="postCheck">
    /// For an app finding after a remedy: a sixth line with the original diagnosis ID, the
    /// remedy and the post-check result (<see cref="PostCheck.TicketLine"/>); null keeps five lines.
    /// </param>
    /// <param name="logSaved">
    /// False when the run's log could not be saved on this PC; the ticket then never points to a
    /// run log that does not exist.
    /// </param>
    /// <param name="facts">
    /// The first-line facts block (<see cref="FirstLineFacts.TicketLines"/>), right after the
    /// first line; null leaves it out. The window always passes it.
    /// </param>
    /// <param name="tried">
    /// The case file's Tried lines of the problem path the run belongs to (<see cref="CaseFile.TriedLines"/>),
    /// after the Action line; null or none leaves them out.
    /// </param>
    public static string Text(RunLogHeader header, IEnumerable<ResultCard> cards, string status, Tone tone, string postCheck = null, bool logSaved = true, IReadOnlyList<string> facts = null, IReadOnlyList<string> tried = null)
    {
        var list = (cards ?? Enumerable.Empty<ResultCard>()).Where(c => c != null).ToList();
        var running = header.Running;
        var ended = header.EndedUtc;
        var verified = Verified(header, tone, running, ended);
        var top = TopCard(list);
        var name = string.IsNullOrEmpty(header.Name) ? "(no run shown)" : header.Name;
        var lines = new List<string> { ProductInfo.DisplayName + Separator + name + Separator + "Reference " + (header.RunId ?? "none") };
        lines.AddRange(FactLines(facts, header));
        lines.Add("Evidence" + Separator + header.ShortMoment(ended ?? header.NowUtc) + Separator + Source(header, running, ended) + Separator + Scrub(Observed(top, running), header));
        // The note that nothing on this device was changed is said here, not as a finding.
        lines.Add("Action" + Separator + ActionLine(header, name) + (list.Any(c => c.Kind == CardKind.Note && c.Title == ResultCard.NothingChangedText) ? "; nothing on this device was changed" : ""));
        lines.AddRange(TriedLines(tried, header));
        lines.Add("Outcome" + Separator + Scrub(Outcome(status, tone, running, verified, logSaved), header));
        lines.Add("Next" + Separator + Scrub(Next(top, running, verified, logSaved), header));
        var text = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        if (!string.IsNullOrWhiteSpace(postCheck)) { text += Scrub(Short(postCheck, MaxPostCheck), header) + Environment.NewLine; }
        return text;
    }

    /// <summary>
    /// Copy details for IT from Home or a problem path, where nothing ran: the same lines as a tool's
    /// ticket. The first line names the page; the facts block follows; the evidence is the health
    /// checks of <paramref name="checkedUtc"/> with the worst finding; the action says that nothing
    /// was run or changed; the outcome is the page's verdict; the next step is the top finding's own
    /// step (Retry work apps and settings only for a row it can help). A problem path adds its
    /// case file's Tried lines after the Action line, which then says that what ran is under Tried.
    /// </summary>
    /// <param name="header">For the user and device names (scrubbed) and the time zone; its run is not used.</param>
    public static string Page(RunLogHeader header, string page, IEnumerable<ResultCard> cards, string status, DateTime? checkedUtc, IReadOnlyList<string> facts, IReadOnlyList<string> tried = null)
    {
        var list = (cards ?? Enumerable.Empty<ResultCard>()).Where(c => c != null).ToList();
        var top = TopCard(list);
        var title = string.IsNullOrEmpty(page) ? "(no page)" : page;
        var lines = new List<string> { ProductInfo.DisplayName + Separator + title + Separator + "Reference none" };
        lines.AddRange(FactLines(facts, header));
        var when = checkedUtc is DateTime at ? header.ShortMoment(at) + Separator + "health checks in the app" : header.ShortMoment(header.NowUtc) + Separator + "health checks not run yet";
        lines.Add("Evidence" + Separator + when + Separator + Scrub(top == null ? "no findings" : Short(top.Word + ": " + top.Title, MaxObserved), header));
        var triedLines = TriedLines(tried, header);
        lines.Add("Action" + Separator + (triedLines.Count == 0 ? "None: copied from " + title + "; nothing was run or changed" : "Copied from " + title + "; what ran on it in this session is under Tried"));
        lines.AddRange(triedLines);
        var outcome = (status ?? "").Trim();
        lines.Add("Outcome" + Separator + Scrub(outcome.Length == 0 ? "No verdict shown." : Short(outcome, MaxOutcome), header));
        var steps = new List<string>();
        if (top != null && top.Kind != CardKind.Change)
        {
            if (!string.IsNullOrWhiteSpace(top.NextStep)) { steps.Add("User: " + ResultCard.Sentence(top.NextStep.Trim())); }
            if (!string.IsNullOrWhiteSpace(top.Support)) { steps.Add("IT support: " + ResultCard.Sentence(top.Support.Trim())); }
        }
        lines.Add("Next" + Separator + Scrub(steps.Count > 0 ? string.Join(" ", steps) : "IT support: no check found a step for the user; the facts and the health line above are this device's state.", header));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private const int MaxFact = 300;

    /// <summary>The Tried lines, each on one line and scrubbed like the rest; none when there are none.</summary>
    private static List<string> TriedLines(IReadOnlyList<string> tried, RunLogHeader header)
        => (tried ?? Array.Empty<string>()).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => Scrub(Short(l, MaxFact), header)).ToList();

    /// <summary>The facts lines, each on one line and scrubbed like the rest; none when there are none.</summary>
    private static IEnumerable<string> FactLines(IReadOnlyList<string> facts, RunLogHeader header)
        => (facts ?? Array.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Scrub(Short(f, MaxFact), header));

    /// <summary>
    /// A success the evidence carries: a service run that completed with its summary and exit 0 (or
    /// "nothing to repair"), or a check in the app that ended with a success it checked itself.
    /// </summary>
    private static bool Verified(RunLogHeader header, Tone tone, bool running, DateTime? ended)
    {
        if (tone != Tone.Success || running || !ended.HasValue) { return false; }
        var outcome = header.Outcome;
        if (outcome == null) { return header.Check != null; }
        return outcome.Result == ResultCode.Completed && outcome.ExitCode is EngineExitCode.Success or EngineExitCode.NothingToRepair;
    }

    /// <summary>Where the evidence comes from.</summary>
    private static string Source(RunLogHeader header, bool running, DateTime? ended)
    {
        if (header.Outcome != null)
        {
            if (running) { return "service run, still running"; }
            return ended.HasValue ? "engine summary" : "service result " + header.Outcome.Result + ", no engine summary";
        }
        if (header.Check != null) { return running ? "check in the app, still running" : "check in the app"; }
        return "no run shown";
    }

    /// <summary>
    /// The card the ticket names: the first problem, else the first point to check, else the
    /// first other finding that carries a next step (a note on an Info or OK result, such as the
    /// download optimization that could not be reached), else the first finding.
    /// </summary>
    internal static ResultCard TopCard(List<ResultCard> cards)
        => cards.FirstOrDefault(c => c.Kind == CardKind.Error)
            ?? cards.FirstOrDefault(c => c.Kind == CardKind.Warning)
            ?? cards.FirstOrDefault(c => c.Kind == CardKind.Info && HasSteps(c))
            ?? cards.FirstOrDefault(c => c.Kind == CardKind.Ok && HasSteps(c))
            ?? cards.FirstOrDefault(c => c.Kind != CardKind.Change && c.Kind != CardKind.Note);

    private static bool HasSteps(ResultCard card) => !string.IsNullOrWhiteSpace(card.NextStep) || !string.IsNullOrWhiteSpace(card.Support);

    private static string Observed(ResultCard top, bool running)
    {
        if (running) { return top == null ? "no result yet" : "so far " + Short(top.Word + ": " + top.Title, MaxObserved); }
        return top == null ? "no findings" : Short(top.Word + ": " + top.Title, MaxObserved);
    }

    private static string ActionLine(RunLogHeader header, string name)
    {
        if (header.Outcome != null) { return name + ", run by the service as SYSTEM"; }
        if (header.Check != null) { return name + ", run in the app as the signed-in user"; }
        return name;
    }

    private static string Outcome(string status, Tone tone, bool running, bool verified, bool logSaved)
    {
        if (running) { return "Still running when this was copied; no result yet."; }
        if (tone == Tone.Success && !verified) { return "Finished, but the result is not confirmed."; }
        var text = (status ?? "").Trim();
        return text.Length > 0 ? Short(text, MaxOutcome) : logSaved ? "Finished; the run log has the details." : "Finished; no run log was saved on this PC.";
    }

    /// <summary>
    /// The top card's steps, whatever its kind (a next step on an Info or OK card reaches the
    /// ticket too); "None needed." only after a verified success without any step.
    /// </summary>
    private static string Next(ResultCard top, bool running, bool verified, bool logSaved)
    {
        if (running) { return "Wait until it finishes and copy again; the reference stays the same."; }
        var steps = new List<string>();
        if (top != null && top.Kind != CardKind.Change)
        {
            if (!string.IsNullOrWhiteSpace(top.NextStep)) { steps.Add("User: " + ResultCard.Sentence(top.NextStep.Trim())); }
            if (!string.IsNullOrWhiteSpace(top.Support)) { steps.Add("IT support: " + ResultCard.Sentence(top.Support.Trim())); }
        }
        if (steps.Count > 0) { return string.Join(" ", steps); }
        if (verified) { return "None needed."; }
        return logSaved ? "IT support: review the run log of this reference." : "IT support: no run log of this attempt was saved on this PC; this text is its record.";
    }

    /// <summary>One line, at most <paramref name="max"/> characters.</summary>
    private static string Short(string text, int max)
    {
        var line = string.Join(" ", (text ?? "").Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())).Trim();
        return line.Length <= max ? line : line.Substring(0, max - 3).TrimEnd() + "...";
    }

    /// <summary>
    /// The device and user names never go into the ticket, also not inside a finding's title or
    /// the status: as whole words they are replaced by [device] and [user]. Only the texts that
    /// come from the run are scrubbed; the ticket's own words name nobody.
    /// </summary>
    internal static string Scrub(string text, RunLogHeader header)
    {
        if (string.IsNullOrEmpty(text)) { return text ?? ""; }
        var user = header.User ?? "";
        var slash = user.LastIndexOf('\\');
        var account = slash >= 0 ? user.Substring(slash + 1) : user;
        // Marked first with characters no name holds, so a later name never matches inside a mark.
        const string userMark = "\u0001\u0002", deviceMark = "\u0001\u0003";
        foreach (var (value, mark) in new[] { (user, userMark), (header.DeviceName ?? "", deviceMark), (account, userMark) })
        {
            if (value.Length >= 2) { text = ReplaceWord(text, value, mark); }
        }
        return text.Replace(userMark, "[user]").Replace(deviceMark, "[device]");
    }

    /// <summary>Replaces <paramref name="value"/> where it stands as a word of its own, ignoring case.</summary>
    private static string ReplaceWord(string text, string value, string mark)
    {
        var result = new StringBuilder();
        var from = 0;
        var search = 0;
        while (search <= text.Length - value.Length)
        {
            var at = text.IndexOf(value, search, StringComparison.OrdinalIgnoreCase);
            if (at < 0) { break; }
            var end = at + value.Length;
            if ((at > 0 && IsWordChar(text[at - 1])) || (end < text.Length && IsWordChar(text[end])))
            {
                search = at + 1;
                continue;
            }
            result.Append(text, from, at - from).Append(mark);
            from = search = end;
        }
        return result.Append(text, from, text.Length - from).ToString();
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';
}
