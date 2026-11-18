using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EndpointToolbelt.App.Core;

namespace EndpointToolbelt.App.Ui;

/// <summary>Brushes from the current theme, by key.</summary>
internal static class ThemeBrush
{
    public static Brush Get(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Black;

    public static Brush For(Tone tone) => Get(tone switch
    {
        Tone.Success => "ToneSuccess",
        Tone.Warning => "ToneWarning",
        Tone.Error => "ToneError",
        Tone.Info => "ToneInfo",
        _ => "ToneNeutral",
    });
}

/// <summary>A list row that opens to show more (a health row); its <see cref="AccessibleRow"/> toggles it.</summary>
public interface IExpandableRow
{
    bool IsOpen { get; }

    void Toggle();
}

/// <summary>
/// One health row as the window shows it. One line (name, fix link, badge); the detail and
/// what the check means only when the row is opened.
/// </summary>
public sealed class HealthRow : IExpandableRow, INotifyPropertyChanged
{
    private bool _open;

    /// <param name="fixOff">the fix presses a tool the organization switched off: the row shows no fix link.</param>
    public HealthRow(HealthResult result, bool fixOff = false)
    {
        Key = result.Name;
        // The plain name on screen; the technical one starts the detail.
        Name = HealthView.PlainName(result.Name);
        StatusWord = result.StatusWord;
        Detail = HealthView.PlainDetail(result.Name, result.Detail);
        Hint = result.Hint;
        HintBelow = result.HintBelow;
        ToolTipText = result.HintAsToolTip;
        Fix = fixOff ? HealthFix.None : result.Fix;
        FixText = Words.FixText(Fix);
        FixHelp = Fix == HealthFix.RetryAppsAndPolicies ? Words.RetryStepHelp : null;
        // An Info row (an OK row that only informs) in the neutral tone; its word says Info.
        StatusBrush = ThemeBrush.Get(result.Status switch
        {
            HealthStatus.Pass when result.Info => "ToneNeutral",
            HealthStatus.Pass => "ToneSuccess",
            HealthStatus.Warn => "ToneWarning",
            _ => "ToneError",
        });
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public string Name { get; }
    /// <summary>The row's technical name (<see cref="HealthResult.Name"/>), by which the window finds it; <see cref="Name"/> is the plain one on screen.</summary>
    public string Key { get; }

    public string StatusWord { get; }

    /// <summary>The state in the row's badge: OK, WARNING or PROBLEM, always written out.</summary>
    public string BadgeWord => StatusWord.ToUpperInvariant();

    public string Detail { get; }
    public string Hint { get; }

    /// <summary>The first sentence of the hint for a row that is not OK; empty for an OK row.</summary>
    public string HintBelow { get; }

    /// <summary>The tooltip: the whole hint where the first sentence is not all of it.</summary>
    public string ToolTipText { get; }

    /// <summary>The row is open: its detail and hint show below its line.</summary>
    public bool IsOpen => _open;

    public Visibility DetailsVisibility => _open ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HintVisibility => string.IsNullOrEmpty(Hint) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The arrow before the name: pointing right when closed, down when open.</summary>
    public double ChevronAngle => _open ? 90 : 0;

    public void Toggle()
    {
        _open = !_open;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailsVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChevronAngle)));
    }

    /// <summary>The fix this row links to (only a row that is not OK has one).</summary>
    public HealthFix Fix { get; }

    /// <summary>The link's tooltip and help text where its text is a plain name (Retry work apps and settings); null otherwise.</summary>
    public string FixHelp { get; }

    /// <summary>The link's text: the name of the button it presses.</summary>
    public string FixText { get; }

    /// <summary>The link's name for assistive technology: which check it fixes and how.</summary>
    public string FixAccessibleName => Name + ": " + FixText;

    public Visibility FixVisibility => Fix == HealthFix.None ? Visibility.Collapsed : Visibility.Visible;
    public Brush StatusBrush { get; }
    public string AccessibleName => Name + ": " + StatusWord + ". " + Detail;
}

/// <summary>One result card as the window shows it.</summary>
public sealed class CardRow
{
    public CardRow(ResultCard card)
    {
        Word = card.Word;
        Title = card.Title;
        Detail = card.Detail;
        NextStepText = card.NextStepLine;
        SupportText = card.SupportLine;
        AccessibleName = card.AccessibleText;
        AccentBrush = ThemeBrush.Get(card.Kind switch
        {
            CardKind.Error => "ToneError",
            CardKind.Warning => "ToneWarning",
            CardKind.Ok => "ToneSuccess",
            CardKind.Change => "ToneChange",
            _ => "ToneInfo",
        });
    }

    public string Word { get; }
    public string Title { get; }
    public string Detail { get; }
    public string NextStepText { get; }

    /// <summary>"For IT support: ...", a separate muted line; empty when there is nothing for IT.</summary>
    public string SupportText { get; }

    public Brush AccentBrush { get; }
    public Visibility DetailVisibility => string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NextStepVisibility => string.IsNullOrEmpty(NextStepText) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SupportVisibility => string.IsNullOrEmpty(SupportText) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The whole card, so a screen reader reads the evidence and both next steps too.</summary>
    public string AccessibleName { get; }
}

/// <summary>
/// One row of the STEPS list as the window shows it. The same object is updated in place while a
/// run goes on, so its row on screen (and a keyboard focus on it) stays.
/// </summary>
public sealed class StepView : INotifyPropertyChanged
{
    private StepStatus _status;

    public StepView(StepRow step) => Update(step);

    public event PropertyChangedEventHandler PropertyChanged;

    public string Word { get; private set; }
    public string NumberAndTitle { get; private set; }
    public string Duration { get; private set; }

    /// <summary>The whole row; for a running step without its running time, so it is not re-read every minute.</summary>
    public string AccessibleName { get; private set; }

    /// <summary>The running time of a running step (help text, not announced).</summary>
    public string HelpText { get; private set; }

    public Brush WordBrush { get; private set; }
    public Brush TitleBrush { get; private set; }

    /// <summary>Takes the step's current state; only what changed is announced to the binding.</summary>
    public void Update(StepRow step)
    {
        _status = step.Status;
        Set(nameof(Word), Word, step.Word, v => Word = v);
        Set(nameof(NumberAndTitle), NumberAndTitle, step.NumberAndTitle, v => NumberAndTitle = v);
        Set(nameof(Duration), Duration, step.DurationText, v => Duration = v);
        Set(nameof(AccessibleName), AccessibleName, step.AccessibleText, v => AccessibleName = v);
        Set(nameof(HelpText), HelpText, step.Status == StepStatus.Running ? step.DurationText : "", v => HelpText = v);
        RefreshBrushes();
    }

    /// <summary>The brushes of the current theme (after a theme change too). The word carries the meaning; the colour repeats it.</summary>
    public void RefreshBrushes()
    {
        WordBrush = ThemeBrush.Get(_status switch
        {
            StepStatus.Done => "ToneSuccess",
            StepStatus.Check => "ToneWarning",
            StepStatus.Problem => "ToneError",
            StepStatus.Running => "ToneInfo",
            _ => "TextMuted",
        });
        TitleBrush = ThemeBrush.Get(_status is StepStatus.Skipped or StepStatus.NotReached ? "TextMuted" : "TextPrimary");
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WordBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TitleBrush)));
    }

    private void Set(string name, string old, string value, System.Action<string> assign)
    {
        if (old == value) { return; }
        assign(value);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
/// <summary>One app of the app diagnosis as the list shows it: its name, its observed state and its next step.</summary>
public sealed class AppRowView
{
    public AppRowView(AppFinding finding, bool selected)
    {
        Finding = finding;
        Label = finding.App.Label;
        // Two apps of one name are told apart by the start of their ID, also on the one-line row.
        RowLabel = finding.SameNameAsAnother && !string.IsNullOrEmpty(finding.App.AppId) ? Label + " (ID " + (finding.App.AppId.Length >= 8 ? finding.App.AppId.Substring(0, 8) : finding.App.AppId) + ")" : Label;
        // After a remedy the post-check's state is the one shown: "App detected" only on new evidence.
        ObservationWord = finding.AfterRemedy is PostCheckState state ? PostCheck.Word(state) : AppDecisions.Word(finding);
        var where = finding.ForBothScopes ? "for this device and assigned to you" : finding.App.Scope == "user" ? "assigned to you" : "for this device";
        // An MSI or Store app says its kind first ("MSI, for this device"); a Win32 app, the usual kind, does not.
        if (finding.App.KindWord.Length > 0) { where = finding.App.KindWord + ", " + where; }
        if (finding.SameNameAsAnother) { where += ", ID " + (finding.App.AppId.Length >= 8 ? finding.App.AppId.Substring(0, 8) : finding.App.AppId); }
        Summary = where + (string.IsNullOrEmpty(finding.NextText) ? "" : ". " + finding.NextText);
        SelectedTag = selected ? "Selected" : "";
        AccessibleName = Label + ": " + ObservationWord + ". " + Summary;
        ObservationBrush = ThemeBrush.Get(finding.Observation switch
        {
            AppObservation.Detected => "ToneSuccess",
            AppObservation.Failed => finding.Severity == FindingSeverity.Error ? "ToneError" : "ToneWarning",
            AppObservation.AwaitingEvidence => "ToneInfo",
            _ => "TextMuted",
        });
    }

    public AppFinding Finding { get; }
    public string Label { get; }

    /// <summary>The row's one line: the name, with the start of the ID when another app has the same name.</summary>
    public string RowLabel { get; }

    public string ObservationWord { get; }
    public string Summary { get; }

    /// <summary>"Selected" for the app whose details are shown (the button's Tag and item status), else empty.</summary>
    public string SelectedTag { get; }

    public string AccessibleName { get; }
    public Brush ObservationBrush { get; }
}

/// <summary>
/// A run of the run history in For IT. Its time, what ran and how it ended, without its
/// reference ("10:42 · Word · Start requested"); the reference stays in View details, the run log
/// and Copy details for IT.
/// </summary>
public sealed class RecentRunView
{
    public RecentRunView(StoredRunLog log, TimeZoneInfo zone, RunVerdict result = RunVerdict.None, string outcome = "", DateTime? nowUtc = null)
    {
        Path = log.Path;
        var when = log.StartedUtc ?? log.WrittenUtc;
        var name = ShortName(log.Action);
        Text = ToolRuns.RecentLine(name, when, result, outcome, zone, nowUtc ?? DateTime.UtcNow);
        var said = !string.IsNullOrEmpty(outcome) ? outcome : ToolRuns.Phrase(result);
        // Unique per run by its start to the second (two runs of a minute would read the same otherwise).
        AccessibleName = "Open the log of " + name + ", " + (said.Length > 0 ? said + ", " : "") + Seconds(when, zone);
    }

    /// <summary>The start in the user's time to the second, with the zone: "2026-11-13 10:42:05 (UTC+01:00)".</summary>
    private static string Seconds(DateTime utc, TimeZoneInfo zone)
    {
        zone ??= TimeZoneInfo.Local;
        if (!TimeText.TryLocal(utc, zone, out var local)) { return utc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC"; }
        return local.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " (UTC" + TimeText.Offset(zone.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc))) + ")";
    }

    /// <summary>What ran, as people know it: the action's name, a tool's name for a check in the app, else the name in the file.</summary>
    internal static string Name(string action)
    {
        if (action == null) { return "Earlier run"; }
        if (Enum.TryParse<ToolbeltAction>(action, false, out var known) && Enum.IsDefined(typeof(ToolbeltAction), known)) { return ActionText.Name(known); }
        return ToolCatalog.ByLogKey(action)?.Name ?? action;
    }

    /// <summary>What ran in Recent runs: a tool's short name ("Word", "OneDrive"), else <see cref="Name"/>.</summary>
    internal static string ShortName(string action)
        => action != null && !(Enum.TryParse<ToolbeltAction>(action, false, out var known) && Enum.IsDefined(typeof(ToolbeltAction), known)) && ToolCatalog.ByLogKey(action) is { } tool
            ? tool.ShortName
            : Name(action);

    public string Path { get; }
    public string Text { get; }
    public string AccessibleName { get; }
}

/// <summary>
/// One tool in For IT's list and on a problem path: its name and line, its group when
/// the list shows the tools a search found, and how its last run ended, in words. Its automation
/// name says all of that, so it never shares a name with the tool's button. The row has
/// its own button (it names its action), which presses the tool's own button on its detail page
/// (<see cref="RunTarget"/>): the same handler, confirmations, rules and help text.
/// </summary>
public sealed class ToolRowView
{
    /// <param name="unavailable">why the tool cannot run on this PC ("Not available: Word is not installed on this PC"); null when it can.</param>
    public ToolRowView(ToolInfo tool, ToolRun run, bool showGroup, bool selected, TimeZoneInfo zone, DateTime nowUtc, Button runTarget = null, string unavailable = null)
    {
        Tool = tool;
        Name = tool.Name;
        Line = unavailable == null ? tool.Line : unavailable + ".";
        GroupLine = showGroup ? ToolCatalog.GroupName(tool.Group) : "";
        ResultText = unavailable == null ? ToolRuns.RowText(tool, run, zone, nowUtc) : "Not available";
        // The short time on screen; the full date and time on the status's own tooltip and in the row's name.
        ResultToolTip = unavailable == null ? ToolRuns.RowFullWhen(tool, run, zone) : null;
        var spoken = unavailable == null ? ToolRuns.RowTextFull(tool, run, zone) : ResultText;
        ResultBrush = run == null || unavailable != null ? ThemeBrush.Get("TextMuted") : ThemeBrush.For(ToolRuns.ToneOf(run.Result));
        SelectedTag = selected ? "Selected" : "";
        RowId = "ToolRow_" + tool.Button.Replace("Button", "");
        AccessibleName = Name + (showGroup ? ", " + GroupLine : "") + ". " + Line + " Last run: " + spoken.Replace(" · ", ", ") + ".";
        RunTarget = runTarget;
        // The button names its action, from the tool catalog.
        RunText = tool.ButtonText;
        RunName = tool.ButtonName;
        RunId = "ToolRowRun_" + tool.Button.Replace("Button", "");
        // The two heavy tools keep their red outline on the row too; the others a plain button.
        RunStyle = Application.Current?.TryFindResource(tool.Heavy ? "DangerRunButton" : "RunButton") as Style;
    }

    /// <summary>The tool's own button on its detail page, which the row's Run button presses (its enabled state and help text follow it).</summary>
    public Button RunTarget { get; }

    /// <summary>The action the button names (<see cref="ToolInfo.ButtonText"/>): the same text as the tool's button on its detail page.</summary>
    public string RunText { get; }

    /// <summary>The row button's automation name (<see cref="ToolInfo.ButtonName"/>, "Check connections: Check Microsoft connections").</summary>
    public string RunName { get; }

    /// <summary>A stable automation ID per row button ("ToolRowRun_SyncClock"), for UI tests.</summary>
    public string RunId { get; }

    public Style RunStyle { get; }

    public ToolInfo Tool { get; }
    public string Name { get; }
    public string Line { get; }
    public string GroupLine { get; }
    public Visibility GroupLineVisibility => GroupLine.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string ResultText { get; }

    /// <summary>The last run's full date and time ("2026-11-15 19:03") beside the short one on screen; null when it has not run.</summary>
    public string ResultToolTip { get; }
    public Brush ResultBrush { get; }

    /// <summary>"Selected" for a selected tool; the window shows no detail beside the list, so it is empty there.</summary>
    public string SelectedTag { get; }

    /// <summary>A stable automation ID per tool ("ToolRow_SyncClock"), for UI tests.</summary>
    public string RowId { get; }

    public string AccessibleName { get; }
}

/// <summary>
/// One window in the list of Bring my window back: the app's name and the window title, which
/// the user needs to pick the right one. The title is shown here only, never logged.
/// </summary>
public sealed class WindowRowView
{
    public WindowRowView(AppWindow window, bool selected)
    {
        Window = window;
        AppName = window.AppName;
        // A title that only repeats the name ("Company Portal" / "Company Portal") is shown once.
        var sameAsName = string.Equals((window.Title ?? "").Trim(), (window.AppName ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        Title = sameAsName ? "" : window.Title;
        TitleSuffix = sameAsName || string.IsNullOrWhiteSpace(window.Title) ? "" : "   " + window.Title;
        TitleVisibility = sameAsName || string.IsNullOrWhiteSpace(window.Title) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        OffScreenWord = window.OffScreen ? "Off screen" : "";
        OffScreenVisibility = window.OffScreen ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        SelectedTag = selected ? "Selected" : "";
        AccessibleName = window.AppName + (sameAsName ? "" : ": " + window.Title) + (window.OffScreen ? ". Off screen" : "") + (window.CannotMove ? ". Can't move" : "");
        // The row's action: Bring back, or why it cannot be done for this window.
        ActionWord = window.CannotMove ? "Can't move" : "Bring back";
        ActionBrush = ThemeBrush.Get(window.CannotMove ? "TextMuted" : "LinkText");
        ToolTipText = window.CannotMove ? AccessibleName + ". " + CannotMoveText : AccessibleName;
        RowId = "WindowRow_" + window.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + window.Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public AppWindow Window { get; }
    public string AppName { get; }
    public string Title { get; }
    public System.Windows.Visibility TitleVisibility { get; }

    /// <summary>The title after the name on the row's one line, with a gap; empty when there is none.</summary>
    public string TitleSuffix { get; }

    /// <summary>"Off screen" for a window not wholly on a current screen, else empty.</summary>
    public string OffScreenWord { get; }

    /// <summary>Collapsed for a window on the screen: an empty text would be an element without a size.</summary>
    public System.Windows.Visibility OffScreenVisibility { get; }

    /// <summary>"Bring back", or "Can't move" for a window of an app with higher rights.</summary>
    public string ActionWord { get; }

    public System.Windows.Media.Brush ActionBrush { get; }

    /// <summary>The row's tooltip: its name and title, and why a window cannot be moved.</summary>
    public string ToolTipText { get; }

    /// <summary>Why a window cannot be moved, and the way that does work.</summary>
    public const string CannotMoveText = "Windows lets no app move this window: its app runs with extra rights (as administrator, or as an accessibility tool such as the On-Screen Keyboard). Use the app's own button or its taskbar button.";

    /// <summary>"Selected" for the window chosen, else empty.</summary>
    public string SelectedTag { get; }
    public string AccessibleName { get; }

    /// <summary>A stable automation ID per row, for UI tests.</summary>
    public string RowId { get; }
}

/// <summary>
/// One network adapter in the list of Get a new IP address: its connection name and its state in
/// a few words. The name is shown here only, never logged; no address is shown at all.
/// </summary>
public sealed class AdapterRowView
{
    public AdapterRowView(EndpointToolbelt.Platform.NetAdapter adapter, string state, bool selected)
    {
        Adapter = adapter;
        Name = string.IsNullOrWhiteSpace(adapter.Name) ? EndpointToolbelt.Platform.AdapterRules.KindWords(adapter.Kind) : adapter.Name;
        State = state ?? "";
        SelectedTag = selected ? "Selected" : "";
        AccessibleName = Name + ": " + State.Replace(" · ", ", ");
        RowId = "AdapterRow_" + adapter.InterfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public EndpointToolbelt.Platform.NetAdapter Adapter { get; }
    public string Name { get; }
    public string State { get; }

    /// <summary>"Selected" for the adapter chosen, else empty.</summary>
    public string SelectedTag { get; }
    public string AccessibleName { get; }

    /// <summary>A stable automation ID per row ("AdapterRow_12"), for UI tests.</summary>
    public string RowId { get; }
}