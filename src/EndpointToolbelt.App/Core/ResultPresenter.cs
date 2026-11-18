using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EndpointToolbelt.App.Core;

/// <summary>What is known about the retried apps once Retry work apps and settings has run.</summary>
public enum AppsAfterRetry
{
    /// <summary>Not known yet: the IME retries the apps in the background and they are not detected again.</summary>
    Unknown,

    /// <summary>Every retried app was detected again.</summary>
    AllDetected,

    /// <summary>At least one retried app still fails.</summary>
    SomeFailing,
}

/// <summary>How the status line reads: its colour follows this, its words carry the meaning.</summary>
public enum Tone
{
    Neutral,
    Success,
    Warning,
    Error,

    /// <summary>Neither success nor a problem, said in the info colour with the word INFO (a sync Windows queued: nothing to do).</summary>
    Info,
}

public enum CardKind
{
    Error,
    Warning,
    Info,
    Ok,
    Change,

    /// <summary>A note about the run, not a finding: shown as one muted line under the result's header, never as a card.</summary>
    Note,
}

/// <summary>One card in the result panel: severity in words, what was found, evidence, next steps.</summary>
public sealed class ResultCard
{
    public CardKind Kind { get; set; }
    public string Word { get; set; }
    public string Title { get; set; }
    public string Detail { get; set; }

    /// <summary>What the user can do.</summary>
    public string NextStep { get; set; }

    /// <summary>What only IT support can do; empty when there is nothing.</summary>
    public string Support { get; set; }

    /// <summary>The user's step as the card shows it: "Next step: ...", or empty.</summary>
    public string NextStepLine => string.IsNullOrEmpty(NextStep) ? "" : "Next step: " + NextStep;

    /// <summary>The step for IT as the card shows it, on its own muted line: "For IT support: ...", or empty.</summary>
    public string SupportLine => string.IsNullOrEmpty(Support) ? "" : "For IT support: " + Support;

    /// <summary>The whole card in one sentence, so a screen reader reads the evidence and both steps too.</summary>
    public string AccessibleText => string.Join(" ", new[] { Word + ": " + Sentence(Title), Detail, NextStepLine, SupportLine }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>A title as a sentence: one closing period, not two when the title already has one.</summary>
    internal static string Sentence(string text)
    {
        var trimmed = (text ?? "").TrimEnd();
        return trimmed.Length == 0 || ".!?:".IndexOf(trimmed[trimmed.Length - 1]) >= 0 ? trimmed : trimmed + ".";
    }

    public static ResultCard From(FindingSeverity severity, string title, string detail = "", string next = "", string support = "")
    {
        var kind = severity switch
        {
            FindingSeverity.Error => CardKind.Error,
            FindingSeverity.Warning => CardKind.Warning,
            FindingSeverity.Ok => CardKind.Ok,
            _ => CardKind.Info,
        };
        return new ResultCard { Kind = kind, Word = FindingSeverityText.Word(severity), Title = title ?? "", Detail = detail ?? "", NextStep = next ?? "", Support = support ?? "" };
    }

    public static ResultCard Change(string title, string detail) => new() { Kind = CardKind.Change, Word = "CHANGED ON THIS DEVICE", Title = title, Detail = detail, NextStep = "", Support = "" };

    /// <summary>The note that a run changed nothing on this device: a muted line, not a card; the run log keeps it as "[CHANGES] ...".</summary>
    public static ResultCard NothingChanged() => new() { Kind = CardKind.Note, Word = "CHANGES", Title = NothingChangedText, Detail = "", NextStep = "", Support = "" };

    public const string NothingChangedText = "Nothing on this device was changed.";
}

/// <summary>What a run ended with, as the app received it.</summary>
public sealed class RunOutcome
{
    public ToolbeltAction Action { get; set; }
    public string RunId { get; set; }
    public ResultCode Result { get; set; }
    public int? ExitCode { get; set; }

    /// <summary>The service's own sentence for a rejection or an engine failure.</summary>
    public string Message { get; set; }

    public List<EngineRecord> Records { get; } = new();

    /// <summary>The run is still going: the app reattached to it, or lost the connection while it ran.</summary>
    public bool InProgress { get; set; }

    /// <summary>When the run started (UTC), for the time it has been running.</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>The run produced a file the app could not receive, and why.</summary>
    public string FileError { get; set; }

    /// <summary>The app cut the run's records (too many): what came after the cut is only in the logs.</summary>
    public bool Truncated { get; set; }
}

/// <summary>How a run is shown: the status line, the cards and what the window does next.</summary>
public sealed class ResultView
{
    public string Status { get; set; }
    public Tone Tone { get; set; }
    public List<ResultCard> Cards { get; } = new();
    public List<string> LogLines { get; } = new();

    /// <summary>
    /// There is something to read beyond the status line. The window does not open the drawer
    /// for it (only View details does); kept as the presenter's verdict, which its tests pin.
    /// </summary>
    public bool OpenDrawer { get; set; }

    /// <summary>Copy the run's log to Downloads for support (a run that failed or ended with warnings).</summary>
    public bool CopyLog { get; set; }

    /// <summary>The run changed something that needs a restart.</summary>
    public bool RestartNeeded { get; set; }

    /// <summary>The engine step the run is in ("Step 2/4: ..."), while it runs; null when there is none.</summary>
    public string Step { get; set; }

    /// <summary>The STEPS list; empty when the run wrote no step headers.</summary>
    public List<StepRow> Steps { get; } = new();

    /// <summary>A muted line under the STEPS list ("Later steps are in the full log."), or empty.</summary>
    public string StepsNote { get; set; } = "";
}

/// <summary>
/// Turns a run's outcome into the status line and the result cards. Pure, so every rule can be
/// tested without a window.
/// </summary>
public static class ResultPresenter
{
    private static readonly int[] QuietExitCodes = { EngineExitCode.Success, EngineExitCode.CooldownActive, EngineExitCode.RestartPending, EngineExitCode.NothingToRepair };

    /// <summary>The subject of the status line for a Windows Update repair that ended while the window was closed.</summary>
    public const string EarlierRepairSubject = "Previous Windows Update repair";

    public const string RestartNowLine = ">>> RESTART THIS DEVICE NOW, then check for updates again. <<<";
    public const string RestartIfNotDoneLine = ">>> RESTART THIS DEVICE if you have not done so yet. <<<";

    /// <summary>The card detail of an interrupted run, whose stored result has no message of its own.</summary>
    public const string InterruptedDetail = "The run ended without its result. The technical log shows how far it got.";

    /// <summary>
    /// The status after Retry work apps and settings succeeded, once the health checks ran again.
    /// The run asks Intune to check the device again and the IME to retry its failed apps; whether
    /// those apps then install is only known once they are detected again. So the status says
    /// "Retry requested" while the apps' state is unknown, says "Done" only when every app was
    /// detected and the health checks are clean, and names problems and points to check that remain.
    /// </summary>
    /// <param name="failedChecks">Health checks with a problem.</param>
    /// <param name="warnChecks">Health checks with a point to check.</param>
    /// <param name="apps">What is known about the apps after the retry.</param>
    public static (string Text, Tone Tone) RetryDone(int failedChecks, int warnChecks, AppsAfterRetry apps)
    {
        var checks = failedChecks > 0
            ? Words.Count(failedChecks, "problem") + (failedChecks == 1 ? " remains" : " remain") + " in the health checks"
            : warnChecks > 0
                ? Words.Count(warnChecks, "point") + (warnChecks == 1 ? " needs" : " need") + " checking in the health checks"
                : "";
        switch (apps)
        {
            case AppsAfterRetry.AllDetected when failedChecks == 0 && warnChecks == 0:
                return ("Done. Every app that was retried is now detected, and the health checks are clean.", Tone.Success);
            case AppsAfterRetry.AllDetected:
                return ("Every app that was retried is now detected, but " + checks + ". See the list above.", failedChecks > 0 ? Tone.Error : Tone.Warning);
            case AppsAfterRetry.SomeFailing:
                return ("Retry requested, but apps still fail to install" + (checks.Length > 0 ? ", and " + checks : "") + ". Create a support package if this stays.", failedChecks > 0 ? Tone.Error : Tone.Warning);
        }
        // The apps' state is not known yet: the request went out, the result is still to come.
        const string pending = " Whether the failed apps install is not known yet: the IME retries them in the background, which can take a while, especially for large apps.";
        if (failedChecks > 0) { return ("Retry requested, but " + checks + "." + pending + " Create a support package if the problems stay.", Tone.Warning); }
        if (warnChecks > 0) { return ("Retry requested. No problems in the health checks, but " + Words.Count(warnChecks, "point") + (warnChecks == 1 ? " needs" : " need") + " checking." + pending, Tone.Warning); }
        return ("Retry requested. Intune was asked to check this device again." + pending, Tone.Neutral);
    }

    /// <summary>The findings as cards: problems first, then the points to check, then the rest in order.</summary>
    public static List<ResultCard> FindingCards(IEnumerable<EngineRecord> records)
    {
        return records
            .Where(r => r.Type == "finding")
            .Select((r, index) => (Record: r, Index: index))
            .OrderBy(x => FindingSeverityText.Rank(x.Record.Severity)).ThenBy(x => x.Index)
            .Select(x => ResultCard.From(FindingSeverityText.Parse(x.Record.Severity), TimeText.ToLocal(x.Record.Title), TimeText.ToLocal(x.Record.Detail),
                TimeText.ToLocal(x.Record.NextStep), TimeText.ToLocal(x.Record.Support)))
            .ToList();
    }

    /// <summary>The finding the status line names: the first problem, else the first point to check.</summary>
    public static EngineRecord TopFinding(IEnumerable<EngineRecord> records)
    {
        var findings = records.Where(r => r.Type == "finding").ToList();
        return findings.FirstOrDefault(r => FindingSeverityText.Parse(r.Severity) == FindingSeverity.Error)
            ?? findings.FirstOrDefault(r => FindingSeverityText.Parse(r.Severity) == FindingSeverity.Warning);
    }

    /// <summary>The run as a timeline, for the technical log (see <see cref="Timeline"/>).</summary>
    public static List<string> LogLines(IEnumerable<EngineRecord> records, TimeZoneInfo zone = null)
        => new Timeline(zone).Add(records as IReadOnlyList<EngineRecord> ?? records.ToList()).Lines;

    private static string Time(EngineRecord record, TimeZoneInfo zone)
        => record.Utc.HasValue && TimeText.TryLocal(record.Utc.Value, zone, out var local) ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " : "";

    /// <summary>The engine step a run is in: the newest "== Step n/N: ... ==" header, without the equals signs.</summary>
    public static string CurrentStep(IReadOnlyList<EngineRecord> records)
    {
        // From the end: the newest header is near it, and a long run has many records.
        for (var i = records.Count - 1; i >= 0; i--)
        {
            var r = records[i];
            if (r != null && r.Type == "progress" && r.Text != null && r.Text.TrimStart().StartsWith("== Step ", StringComparison.Ordinal))
            {
                return r.Text.Trim().Trim('=').Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// The status line while a run goes on, and the engine step it names. A background run counts
    /// its minutes, so this is asked every few seconds without building the rest of the view.
    /// </summary>
    public static (string Status, string Step) LiveStatus(RunOutcome outcome, DateTime utcNow)
    {
        var name = ActionText.Name(outcome.Action);
        var current = CurrentStep(outcome.Records);
        if (!ActionText.RunsInBackground(outcome.Action)) { return (name + " is running...", current); }
        var minutes = outcome.StartedUtc.HasValue ? Math.Max(0, (int)(utcNow - outcome.StartedUtc.Value).TotalMinutes) : 0;
        var step = current == null ? "" : " " + current + ".";
        return (string.Format(CultureInfo.InvariantCulture, "{0} is running in the background ({1} min elapsed).{2} You can close this window.", name, minutes, step), current);
    }

    /// <summary>
    /// The view while a run is still going: findings so far, no closing cards. The drawer is
    /// opened once when the run starts, not by every update, so a user who hides it keeps it hidden.
    /// </summary>
    /// <param name="timeline">
    /// The timeline of this run so far, when its records only grow (a run the app started): only
    /// the new records are formatted. Without one the whole log is built.
    /// </param>
    public static ResultView Live(RunOutcome outcome, DateTime utcNow, TimeZoneInfo zone = null, Timeline timeline = null)
    {
        var (status, step) = LiveStatus(outcome, utcNow);
        // Running is not a warning: the colour says "needs attention" only when something does.
        var view = new ResultView { Tone = Tone.Neutral, Status = status, Step = step };
        view.Steps.AddRange(Core.Steps.Build(outcome.Records, RunEnd.Running, utcNow, outcome.Truncated));
        view.StepsNote = outcome.Truncated && view.Steps.Count > 0 ? Core.Steps.TruncatedNote : "";
        view.Cards.AddRange(FindingCards(outcome.Records));
        view.LogLines.AddRange(timeline == null ? LogLines(outcome.Records, zone) : timeline.Add(outcome.Records).Lines);
        return view;
    }

    /// <summary>
    /// The status after a run whose sync Windows queued (Intune is rate limiting the device), in the
    /// info tone: "Full IME reset ran. Windows sends the sync to Intune again at about 09:30; no action
    /// needed.", or "any moment now" once that time is less than a minute away or has passed.
    /// </summary>
    internal static string QueuedSyncStatus(string name, DateTime? atUtc, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (atUtc is not DateTime at) { return name + " ran. Windows sends the sync to Intune again by itself; no action needed."; }
        var when = at - nowUtc <= TimeSpan.FromMinutes(1) ? "any moment now" : "at about " + TimeText.InLocalTime(at, "HH:mm", zone);
        return name + " ran. Windows sends the sync to Intune again " + when + "; no action needed.";
    }

    /// <param name="outcome">The run.</param>
    /// <param name="earlierRun">A Windows Update repair that ended while the window was closed.</param>
    /// <param name="zone">The user's time zone for the log (the local one when null).</param>
    public static ResultView Final(RunOutcome outcome, bool earlierRun = false, TimeZoneInfo zone = null)
    {
        if (outcome.Action == ToolbeltAction.DiagnoseApps && !outcome.InProgress) { return Diagnosis(outcome, zone); }
        var general = General(outcome, earlierRun, zone);
        // Get a new IP address says what Windows confirmed about the adapter, in its own words.
        if (outcome.Action == ToolbeltAction.RenewIpAddress && !outcome.InProgress) { IpRenewal.Apply(general, outcome); }
        // Check Windows Update says its verdict and the one next step in the status.
        if (outcome.Action == ToolbeltAction.CheckWindowsUpdate && !outcome.InProgress) { WuCheck.Apply(general, outcome); }
        return general;
    }

    private static ResultView General(RunOutcome outcome, bool earlierRun, TimeZoneInfo zone)
    {
        var name = earlierRun ? EarlierRepairSubject : ActionText.Name(outcome.Action);
        var view = new ResultView();
        view.Steps.AddRange(Core.Steps.Build(outcome.Records, Core.Steps.EndOf(outcome), DateTime.UtcNow, outcome.Truncated));
        view.StepsNote = outcome.Truncated && view.Steps.Count > 0 ? Core.Steps.TruncatedNote : "";
        view.Cards.AddRange(FindingCards(outcome.Records));
        view.LogLines.AddRange(LogLines(outcome.Records, zone));
        var summary = outcome.Records.LastOrDefault(r => r.Type == "summary");
        view.RestartNeeded = (summary?.Restart ?? false) || outcome.Records.Any(r => r.Type == "restart-required");

        if (outcome.InProgress)
        {
            // The connection ended while the run went on in the service: still running, not a warning.
            view.Tone = Tone.Neutral;
            view.OpenDrawer = true;
            view.Status = name + " is taking longer than expected. It continues in the background.";
            return view;
        }

        if (outcome.Result != ResultCode.Completed)
        {
            ApplyServiceResult(view, outcome, name);
            // The service's message is the detail, unless it already is the status line.
            var detail = string.IsNullOrWhiteSpace(outcome.Message) || outcome.Message == view.Status ? "" : outcome.Message;
            if (detail.Length == 0 && outcome.Result == ResultCode.Interrupted) { detail = InterruptedDetail; }
            view.Cards.Insert(0, ResultCard.From(view.Tone == Tone.Error ? FindingSeverity.Error : FindingSeverity.Warning, view.Status, detail));
            // A run stopped part-way may already have changed things: say what.
            AddClosingCards(view, outcome, summary);
            AddClosingLines(view, outcome, earlierRun, zone);
            return view;
        }

        var exit = outcome.ExitCode ?? -1;
        var quiet = Array.IndexOf(QuietExitCodes, exit) >= 0;
        // The cooldown's own card says when the action can run again, and the status points to it.
        view.OpenDrawer = !quiet || exit == EngineExitCode.CooldownActive;
        switch (exit)
        {
            case EngineExitCode.Success:
                view.Tone = Tone.Success;
                view.Status = name + " completed successfully.";
                break;
            case EngineExitCode.Warnings:
                view.Tone = Tone.Warning;
                view.Status = name + " finished; some points need checking. See the details.";
                break;
            case EngineExitCode.CooldownActive:
                view.Tone = Tone.Warning;
                view.Status = name + " skipped: cooldown active. The details say when it can run again.";
                break;
            case EngineExitCode.RestartPending:
                view.Tone = Tone.Warning;
                view.Status = name + " blocked: a restart is pending. Restart the device first.";
                break;
            case EngineExitCode.NothingToRepair:
                view.Tone = Tone.Success;
                view.Status = name + ": nothing to repair - this device is already up to date.";
                break;
            default:
                view.Tone = Tone.Error;
                view.Status = string.Format(CultureInfo.InvariantCulture, "{0} did not succeed (code {1}). See the details; create a support package if it repeats.", name, RunWords.Code(exit));
                break;
        }

        // The engine's own conclusion goes on the status line where there is one. A problem that
        // remains after a run that otherwise completed is said as such, in the problem colour.
        var top = TopFinding(outcome.Records);
        var problemRemains = top != null && FindingSeverityText.Parse(top.Severity) == FindingSeverity.Error;
        // "Nothing to repair" is Repair Windows Update's own answer; the other actions say what they found.
        var answer = outcome.Records.FirstOrDefault(r => r.Type == "finding");
        if (exit == EngineExitCode.NothingToRepair && outcome.Action != ToolbeltAction.RepairWU && answer != null)
        {
            view.Status = name + ": " + TimeText.ToLocal(answer.Title) + ". Nothing was changed.";
        }
        if (top != null && (exit == EngineExitCode.Failed || exit == EngineExitCode.InvalidAction))
        {
            view.Status = name + " did not succeed: " + TimeText.ToLocal(top.Title);
        }
        else if (problemRemains && (exit == EngineExitCode.Success || exit == EngineExitCode.Warnings))
        {
            view.Tone = Tone.Error;
            view.OpenDrawer = true;
            view.Status = name + " finished, but a problem remains: " + TimeText.ToLocal(top.Title);
        }
        else if (top != null && exit == EngineExitCode.Warnings)
        {
            view.Status = name + " completed with warnings: " + TimeText.ToLocal(top.Title);
        }
        if (exit == EngineExitCode.Success && (summary?.Restart ?? false) && !problemRemains)
        {
            view.Tone = Tone.Warning;
            view.Status = name + " completed. Restart the device to finish it.";
        }

        // The log goes to Downloads for support after a run that did not go well: not after a
        // success, a gate (cooldown, pending restart, nothing to repair), or a run whose only
        // point is a sync that Windows queued (no action is needed); always while a problem remains.
        view.CopyLog = (!quiet && !(exit == EngineExitCode.Warnings && OnlyQueuedSync(outcome.Records))) || problemRemains;

        // A sync that Windows queued is neither success nor failure: the action ran, nothing is needed.
        var queued = outcome.Records.FirstOrDefault(r => r.Type == "mdm-queued");
        if (exit == EngineExitCode.Warnings && queued != null && !problemRemains)
        {
            view.Tone = Tone.Info;
            view.Status = QueuedSyncStatus(name, TimeText.ParseUtc(queued.At), DateTime.UtcNow, zone);
        }

        AddClosingCards(view, outcome, summary);
        AddClosingLines(view, outcome, earlierRun, zone);
        return view;
    }

    /// <summary>
    /// The read-only app diagnosis: its cards are the app findings with their decision texts,
    /// so the drawer, the run log, Copy summary and Copy details for IT say what the window says. The list
    /// itself is in the window, so the drawer stays closed; a service that does not know the action
    /// (an older version) is said in words, not as a refused request.
    /// </summary>
    private static ResultView Diagnosis(RunOutcome outcome, TimeZoneInfo zone)
    {
        var diagnosis = AppDecisions.From(outcome, DateTime.UtcNow, zone);
        var view = new ResultView();
        view.Steps.AddRange(Core.Steps.Build(outcome.Records, Core.Steps.EndOf(outcome), DateTime.UtcNow, outcome.Truncated));
        view.LogLines.AddRange(LogLines(outcome.Records, zone));
        view.Cards.AddRange(diagnosis.Cards());
        (view.Status, view.Tone) = AppDecisions.Status(diagnosis);
        view.CopyLog = outcome.Result is ResultCode.EngineTimeout or ResultCode.EngineCrashed or ResultCode.Interrupted
            || (outcome.Result == ResultCode.Completed && outcome.ExitCode != EngineExitCode.Success);
        var summary = outcome.Records.LastOrDefault(r => r.Type == "summary");
        AddClosingCards(view, outcome, summary);
        AddClosingLines(view, outcome, false, zone);
        return view;
    }

    // The findings the engine writes for a sync that Windows queued or Intune rate limits.
    private static readonly string[] QueuedSyncIds = { "MDM-SYNC-QUEUED", "MDM-SYNC-THROTTLED" };

    /// <summary>
    /// The run's only point is a sync that Windows queued: it says so (an mdm-queued record or the
    /// queued-sync finding), and no other finding is a problem or a point to check.
    /// </summary>
    public static bool OnlyQueuedSync(IEnumerable<EngineRecord> records)
    {
        var list = records.ToList();
        var queued = list.Any(r => r.Type == "mdm-queued" || (r.Type == "finding" && QueuedSyncIds.Contains(r.Id)));
        return queued && !list.Any(r => r.Type == "finding" && !QueuedSyncIds.Contains(r.Id)
            && FindingSeverityText.Parse(r.Severity) is FindingSeverity.Error or FindingSeverity.Warning);
    }

    /// <summary>What changed, or that nothing did; and a card when the run left nothing to show.</summary>
    private static void AddClosingCards(ResultView view, RunOutcome outcome, EngineRecord summary)
    {
        var changes = outcome.Records.Where(r => r.Type == "change").Select(r => TimeText.ToLocal(r.Text)).ToList();
        if (changes.Count > 0)
        {
            view.Cards.Add(ResultCard.Change(Words.Count(changes.Count, "change") + " made by this run",
                string.Join(Environment.NewLine, changes.Select(c => "- " + c))));
        }
        else if (summary != null && outcome.Action != ToolbeltAction.CollectDiag)
        {
            // A line, not a card. The support package says what it wrote in its own card.
            view.Cards.Add(ResultCard.NothingChanged());
        }
        // A run without findings or a summary ended before its close-out. (A request the service
        // turned down has no engine output at all, and its own card says why.)
        var engineRan = outcome.Result == ResultCode.Completed || outcome.Records.Count > 0;
        if (engineRan && summary == null && !outcome.Records.Any(r => r.Type == "finding"))
        {
            view.Cards.Add(ResultCard.From(FindingSeverity.Info, "No result details for this run", "The technical log is all there is for this run."));
        }
    }

    /// <summary>The closing RESULT line of the log, and the restart reminder.</summary>
    private static void AddClosingLines(ResultView view, RunOutcome outcome, bool earlierRun, TimeZoneInfo zone)
    {
        // The time of the engine's summary, when there is one: the moment the run closed.
        var summary = outcome.Records.LastOrDefault(r => r.Type == "summary");
        var time = summary == null ? "" : Time(summary, zone ?? TimeZoneInfo.Local);
        view.LogLines.Add(time + RunWords.ResultLine(outcome, outcome.Records.Count(r => r.Type == "change"), view.RestartNeeded));
        if (view.RestartNeeded) { view.LogLines.Add(RestartNowLine); }
        if (earlierRun && outcome.Result == ResultCode.Completed && outcome.ExitCode is EngineExitCode.Success or EngineExitCode.Warnings)
        {
            view.LogLines.Add(RestartIfNotDoneLine);
        }
    }

    private static void ApplyServiceResult(ResultView view, RunOutcome outcome, string name)
    {
        view.OpenDrawer = true;
        view.Tone = Tone.Error;
        switch (outcome.Result)
        {
            case ResultCode.ServiceUnavailable:
                view.Status = name + " could not start: the " + ProductInfo.DisplayName + " service is not available. Restart the device and try again; if it repeats, contact your IT support.";
                break;
            case ResultCode.ProtocolMismatch:
                view.Status = name + " could not start: the app and the service are different versions. Restart the device; if it repeats, " + ProductInfo.DisplayName + " needs to be reinstalled.";
                break;
            case ResultCode.ServerIdentityMismatch:
                view.Status = name + " was not started: the connection did not come from the " + ProductInfo.DisplayName + " service. Contact your IT support.";
                break;
            case ResultCode.Busy:
                view.Tone = Tone.Warning;
                view.Status = "Another action is still running. Please wait until it has finished.";
                break;
            case ResultCode.ConfirmationRequired:
                view.Tone = Tone.Warning;
                // The service says why (for example: the number of signed-in users changed).
                view.Status = string.IsNullOrWhiteSpace(outcome.Message) ? name + " needs your confirmation first." : outcome.Message;
                break;
            case ResultCode.RateLimited:
                view.Tone = Tone.Warning;
                view.Status = name + " was not started: actions were started too often in the last hour. Try again later.";
                break;
            case ResultCode.MultiSessionBlocked:
                view.Tone = Tone.Warning;
                view.Status = string.IsNullOrWhiteSpace(outcome.Message)
                    ? name + " was not started: it affects everyone signed in to this device, and your organization does not allow that while others are signed in."
                    : outcome.Message;
                break;
            case ResultCode.ToolSwitchedOff:
                // The organization switched the tool off with a policy value.
                view.Tone = Tone.Warning;
                view.Status = name + " was not started: your organization has switched this tool off on this device.";
                break;
            case ResultCode.EngineNotSigned:
                view.Status = name + " could not start: a device policy or security software blocked the repair engine. Contact your IT support.";
                break;
            case ResultCode.EngineTimeout:
                view.Tone = Tone.Warning;
                view.CopyLog = true;
                view.Status = name + " was stopped before it finished (time limit). Work it handed to Windows may still be running; restart the device and check again.";
                break;
            case ResultCode.EngineCrashed:
                view.CopyLog = true;
                view.Status = name + " stopped unexpectedly. Try again; if it repeats, create a support package.";
                break;
            case ResultCode.EngineDidNotStart:
                view.Status = name + " did not start. Restart the device and try again; if it repeats, contact your IT support.";
                break;
            case ResultCode.Interrupted:
                view.Tone = Tone.Warning;
                view.CopyLog = true;
                view.Status = name + " was interrupted: the " + ProductInfo.DisplayName + " service stopped while it ran (for example a restart or an update). Work it handed to Windows may have finished; press Check this PC again on Home to see the device's state, and run it again if needed.";
                break;
            default:
                view.Status = name + " could not start: the service did not accept the request.";
                break;
        }
    }

    /// <summary>
    /// The service asked for a new confirmation of an action that affects every session: the
    /// number of other signed-in users changed between the app's check and the request (the app
    /// always sends the confirmation itself). The app then checks again and asks once more.
    /// </summary>
    public static bool AsksToConfirmSessionsAgain(RunOutcome outcome)
        => outcome != null && outcome.Result == ResultCode.ConfirmationRequired && ActionCatalog.Get(outcome.Action).AffectsAllSessions;

    /// <summary>
    /// After the first confirmation round the app counts and asks again (the result is not shown);
    /// after the second one, the result is shown as it is.
    /// </summary>
    public static bool AskSessionsAgain(int round, RunOutcome outcome) => round == 0 && AsksToConfirmSessionsAgain(outcome);

    /// <summary>
    /// The status of a finished run is followed at once by another one (Retry work apps and settings
    /// that succeeded is followed by the health checks' verdict): a screen reader hears only that one.
    /// </summary>
    public static bool HealthStatusFollows(RunOutcome outcome, ResultView view)
        => outcome.Action == ToolbeltAction.RetryAppsAndPolicies && outcome.Result == ResultCode.Completed && !outcome.InProgress
            && outcome.ExitCode == EngineExitCode.Success && view.Tone == Tone.Success;

    /// <summary>
    /// The "support package created" event is reported only when a package actually arrived: a
    /// run that completed without delivering its file created nothing the user can send.
    /// </summary>
    public static bool ReportsPackageCreated(RunOutcome outcome, bool fileReceived)
        => outcome.Action == ToolbeltAction.CollectDiag && outcome.Result == ResultCode.Completed && fileReceived;

    /// <summary>
    /// Whether a run belongs in the user's history: every run the engine was asked to do, not a
    /// request the service turned down before anything ran.
    /// </summary>
    public static bool BelongsInHistory(RunOutcome outcome)
        => outcome.InProgress || outcome.Result is ResultCode.Completed or ResultCode.EngineTimeout or ResultCode.EngineCrashed or ResultCode.EngineDidNotStart or ResultCode.EngineNotSigned or ResultCode.Interrupted;

    /// <summary>The result word of a history line: the exit code, or what happened instead.</summary>
    public static string HistoryResult(RunOutcome outcome)
    {
        if (outcome.InProgress) { return "timeout"; }
        // The exit code with the run ID and the result, so the history says what Windows confirmed.
        if (outcome.Action == ToolbeltAction.RenewIpAddress && IpRenewal.HistoryResult(outcome) is string renewal) { return renewal; }
        if (outcome.Action == ToolbeltAction.CheckWindowsUpdate && WuCheck.HistoryResult(outcome) is string check) { return check; }
        // Exit 2 says "with warnings"; a run whose findings were only OK or INFO (a sync Windows already queued)
        // keeps that word in the history, with its reference, so the device report does not call it a warning.
        if (outcome.Result == ResultCode.Completed && outcome.ExitCode == EngineExitCode.Warnings && OnlyInfo(outcome))
        {
            return EngineExitCode.Warnings.ToString(CultureInfo.InvariantCulture) + "|" + (string.IsNullOrEmpty(outcome.RunId) ? "none" : outcome.RunId) + "|" + HistoryInfo;
        }
        if (outcome.Result == ResultCode.Completed && outcome.ExitCode.HasValue) { return outcome.ExitCode.Value.ToString(CultureInfo.InvariantCulture); }
        return outcome.Result switch
        {
            ResultCode.EngineDidNotStart => "not-started",
            ResultCode.Interrupted => "interrupted",
            _ => outcome.Result.ToString(),
        };
    }

    /// <summary>The history's word for an exit 2 whose findings were only OK or INFO.</summary>
    public const string HistoryInfo = "info";

    /// <summary>The run had findings, and none of them was a warning or a problem.</summary>
    private static bool OnlyInfo(RunOutcome outcome)
    {
        var findings = outcome.Records.Where(r => r?.Type == "finding").ToList();
        return findings.Count > 0 && findings.All(f => string.Equals(f.Severity, "OK", StringComparison.OrdinalIgnoreCase) || string.Equals(f.Severity, "INFO", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An exit code or HRESULT as 0x........ for display.</summary>
    public static string FormatCode(long code)
    {
        if (code < 0) { code += 4294967296L; }
        return "0x" + code.ToString("X8", CultureInfo.InvariantCulture);
    }
}
