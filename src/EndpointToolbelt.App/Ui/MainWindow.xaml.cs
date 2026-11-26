using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Platform;
using EndpointToolbelt.Protocol;
using Microsoft.Win32;

namespace EndpointToolbelt.App.Ui;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A WPF Window is not disposable; the watcher is disposed when the window closes.")]
public partial class MainWindow : Window
{
    private const double FullDrawerWidth = 440;
    private const double SmallestDrawerWidth = 280;

    // Polls of a followed run that may fail in a row (5 s apart) before the app stops following it.
    private const int MaxFailedPolls = 6;

    private readonly ServiceGateway _gateway = new();
    private readonly ServiceWatcher _watcher = new();
    private readonly AppSettings _settings = AppSettings.Load();

    // Where the settings are saved; null is the user's own file (a test points it elsewhere).
    internal string SettingsPath { get; set; }
    private readonly string _version;

    // Every button that starts something, what it is for the button rules, and its own help text
    // (replaced by the reason while the service is missing).
    private readonly Dictionary<Button, AppButton> _actionButtons;
    private readonly Dictionary<Button, string> _helpTexts = new();
    private readonly HashSet<Button> _reasonToolTips = new();

    // One thing at a time, and the repairs only while the service can be reached.
    private readonly ButtonState _buttons = new();

    // The tools the organization switched off (policy DisabledTools), hidden everywhere in the window.
    private IReadOnlyList<string> _switchedOff = SwitchedOffTools.ReadPolicy();

    private StatusInfo _status;
    private List<HealthResult> _health = new();
    // The rows the window shows; _health keeps every detailed check for the report.
    private List<HealthResult> _healthRows = new();
    private bool _showAllHealth;
    private DsregInfo _dsreg = new();
    private Tone _statusTone = Tone.Neutral;

    // The drawer, and how much it added to the window's width.
    private bool _drawerOpen;
    private double _drawerAdded;

    // A drawer opened while the window was maximized (it still needs room once the window is
    // restored), and the width a drawer added before it was closed while maximized (it comes off then).
    private bool _drawerNeedsRoom;
    private double _shrinkOnRestore;

    // Whether the user chose the window's height by dragging a border (only that height is
    // saved; otherwise the window opens at its default height), and the window's height when a move
    // or drag of a border began. The window never takes its height from its content.
    private bool _heightChosen;
    private double? _sizeStartHeight;

    // One thing at a time: every button handler runs through Guarded, and nothing else starts
    // while one runs or while a run is being followed.
    private bool _busy;
    private bool _following;

    // Keyboard focus. A result that was just shown gets it when the run ends (if the drawer is
    // open); the element that had it before gets it back when the drawer closes. A followed run
    // remembers the element that had it when the run started.
    private bool _focusResultAtEnd;
    private IInputElement _startFocus;
    private IInputElement _followFocus;
    private IInputElement _returnFocus;

    // The engine step a screen reader last heard about, during a long run.
    private string _announcedStep;

    // A run in progress: records arrive on a worker thread and are rendered by a timer. Its
    // records only grow, so the timeline formats each record once.
    private readonly object _liveLock = new();
    private RunOutcome _live;
    private Timeline _liveTimeline;
    private bool _liveDirty;
    private readonly DispatcherTimer _liveTimer;

    // A run followed through the service's stored result (reattached, or the connection was lost).
    private DispatcherTimer _followTimer;
    private RunOutcome _followed;
    private int _failedPolls;
    private bool _polling;

    // What the result panel shows, so it is only rebuilt when it changes. The generation changes
    // with every new result, so work that finishes later (the run log copy) knows whether its
    // result is still the one shown.
    private List<ResultCard> _cards = new();
    private readonly List<string> _shownLog = new();
    // The STEPS rows on screen (updated in place), and a live run's last built steps, whose
    // running time moves between records.
    private readonly ObservableCollection<StepView> _stepViews = new();
    private List<StepRow> _liveSteps = new();
    private string _liveStepsNote = "";
    private int _resultGeneration;

    // What "Open full log", Copy summary and Copy details for IT write a header for: the run shown (a
    // service run), or a check that ran in the app itself with its own local run ID.
    private RunOutcome _shownRun;

    // The last connection check's extra Copy details for IT line (the inspecting proxy's certificate authority), by its run ID.
    private (string RunId, string Line) _connectionTicket;
    private LocalRun _shownCheck;

    // What the latest Check Windows Update found, for the verdict of "Windows Update keeps failing";
    // null before a check and after a repair or a cleanup on that path, which can change its answer.
    private WuPathCheck _wuPathCheck;

    /// <summary>The support package saved last, for Copy details for IT (its name and SHA-256).</summary>
    private string _lastPackagePath;

    // The page shown, the app diagnosis that Home and "An app won't install" both show, the
    // run it came from (for Copy details for IT), the app whose details are shown, and the problem path.
    // NotListed is "My problem is not listed", the handover page; not a problem path.
    private enum Page { Home, Problems, Apps, Problem, It, Windows, Ip, NotListed }

    // Bring my window back: the window system (a test replaces it), the windows listed, the one
    // chosen, and the page the window page was opened from (Back and Cancel return there).
    private IWindowApi _windowApi;

    internal IWindowApi WindowApi
    {
        get => _windowApi ??= new NativeWindowApi();
        set => _windowApi = value;
    }

    private List<AppWindow> _windows = new();
    private AppWindow _chosenWindow;
    private Page _windowsFrom = Page.Problems;

    // The Office desktop apps (a test replaces them), the safe-mode start that waits for the user's
    // answer, the network adapters (a test replaces them), the adapters listed and the one chosen, and the
    // page Get a new IP address was opened from.
    private IOfficeApps _officeApps;

    /// <summary>The apps of this session for Reset Company Portal data; null uses the real process list (a test sets a fake).</summary>
    internal ISessionApps PortalApps { get; set; }

    internal IOfficeApps OfficeApps
    {
        get => _officeApps ??= new OfficeAppsOnThisPc();
        set => _officeApps = value;
    }

    private SafeModeOutcome _safeMode;

    // The runs that ended on each problem path in this session, for the Tried lines of Copy details for IT.
    private readonly CaseFile _caseFile = new();

    // The run "Does it work now?" asks about while it is shown; null when none.
    private string _worksNowRunId;

    // The OneDrive sync app (a test replaces it).
    private IOneDrive _oneDrive;

    internal IOneDrive OneDrive
    {
        get => _oneDrive ??= new OneDriveOnThisPc();
        set => _oneDrive = value;
    }

    /// <summary>How long a safe-mode start keeps its button on "Starting Word..." at least, so a second click cannot start the app twice; a test sets zero.</summary>
    internal TimeSpan StartingHold { get; set; } = TimeSpan.FromSeconds(2);
    private INetworkAdapters _adapters;

    internal INetworkAdapters Adapters
    {
        get => _adapters ??= new WmiNetworkAdapters();
        set => _adapters = value;
    }

    private List<NetAdapter> _adapterList = new();
    private NetAdapter _chosenAdapter;
    private Page _ipFrom = Page.Problem;

    /// <summary>The health checks run again after every tool; a test turns this off so nothing reads the real device.</summary>
    internal bool HealthAfterTools { get; set; } = true;

    // The network summary of the last health check (the adapters are read with the checks, off
    // the window's thread) and when the device last started (a test sets its own).
    private NetworkSummary _network = new();

    internal Func<DateTime?> BootClock { get; set; } = () => new SystemHealthSource().BootUtc();

    // The run whose log could not be saved on this PC (its ID): Copy details for IT then never points to it.
    private string _logNotSavedRunId;

    private Page _page = Page.Home;
    private AppDiagnosis _diagnosis;
    private RunOutcome _diagnosisRun;
    private LocalRun _diagnosisCheck;
    private AppFinding _selectedApp;
    // The all-apps view (Show all), and the search opened by Find another app; both close when the app path is left.
    private bool _showAllApps;
    private bool _appSearchOpen;
    // The chosen app as a view of its own (a click or Enter on a row); Back returns to the list.
    private bool _appDetailView;
    // Without the IME the app path offers no Retry work apps and settings (ShowAppChecks).
    private bool _imeMissingForApps;
    // The install blockers' rows, opened from the status line while they are all OK.
    private bool _appChecksOpen;
    // The window list shows the windows off the screen only, until Show the other windows.
    private bool _showAllWindows;
    private Problem _problem = Problem.CantSignIn;

    // The diagnosis as it was read (the post-check compares with it; _diagnosis is what the
    // window shows, with the remedies), the user's remedy records and their file, the remedies the
    // newest diagnosis checked, and the apps a Retry work apps and settings started here retries.
    private AppDiagnosis _diagnosisBase;
    private readonly string _remedyUser = RemedyStore.CurrentUser();
    private string _remediesPath;
    private List<RemedyRecord> _remedies;
    private List<RemedyRecord> _checkedNow = new();
    private List<AppFinding> _retryApps;
    private DateTime _retryPressedUtc;

    // For IT support shows the tiles, a list (a group's tools or the tools the search
    // found) or one tool's detail, one at a time at every size. The group and the
    // tool chosen last, and each tool's newest run from the run history.
    internal enum ItView { Tiles, List, Detail }

    private ItView _itView = ItView.Tiles;
    private ToolGroup _itGroup = ToolGroup.Apps;
    private ToolInfo _itTool;
    private Dictionary<string, ToolRun> _toolRuns = new();
    private readonly Dictionary<Button, ToolGroup> _tiles;

    // The mascot. The health checks run (it works), when they last finished (it shows how
    // they ended for a moment after) and until when it shows the tone a run ended with. One one-shot
    // timer brings the page's own mood back when that moment is over.
    // It is part of the brand; only high contrast hides it.
    private bool _checkingHealth;
    private DateTime? _healthCheckedUtc;
    private DateTime _doneUntilUtc;
    private DateTime _runHelperUntilUtc;
    private readonly DispatcherTimer _helperTimer;
    private bool _highContrast = SystemParameters.HighContrast;

    // Windows animations as a test sets them; null reads SystemParameters.ClientAreaAnimation.
    internal bool? WindowsAnimations { get; set; }

    // Where the keyboard focus was last sent ("row:SyncClockButton", "tile:SignIn", "title",
    // "search", "list"; "rowrun:SyncClockButton", a row's Run button), so a test can check it
    // without showing the window.
    private string _focusIntent;

    // The tool whose run (or message) the status shows, or null for anything else (a health
    // check, the report, the support package, the app diagnosis). A tool's detail page shows only
    // the status of its own tool.
    private ToolInfo _statusTool;

    /// <summary>
    /// How a tool's row presses the tool's own button on its detail page (the same handler,
    /// confirmations and rules); a test replaces it so nothing runs.
    /// </summary>
    internal Action<Button> ToolInvoker { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        _version = Assembly.GetExecutingAssembly().GetName().Version is { } v ? string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", v.Major, v.Minor, v.Build) : "";
        Title = ProductInfo.ShortDisplayName;
        VersionText.Text = ProductInfo.DisplayName + " " + _version;
        // Each tool's button names its action; the text lives in the tool catalog.
        foreach (var tool in ToolCatalog.All)
        {
            if (FindName(tool.Button) is Button toolButton)
            {
                toolButton.Content = tool.ButtonText;
                // "Start in safe mode" is the same on the four Office apps' pages: the name starts with the visible
                // label (speech input matches on it) and says which app; it differs from the row button's name.
                if (tool.Office != null) { AutomationProperties.SetName(toolButton, tool.ButtonText + ": " + tool.ShortName); }
            }
        }
        _actionButtons = new Dictionary<Button, AppButton>
        {
            [RetryButton] = AppButton.Retry,
            [RetryToolButton] = AppButton.Retry,
            [PackageButton] = AppButton.Package,
            [ReportButton] = AppButton.Report,
            [HtmlReportButton] = AppButton.Report,
            [LogsButton] = AppButton.Logs,
            [ClearCacheButton] = AppButton.ClearCache,
            [ConnectivityButton] = AppButton.Connectivity,
            [WindowBackButton] = AppButton.WindowBack,
            [WordSafeButton] = AppButton.WordSafe,
            [ExcelSafeButton] = AppButton.ExcelSafe,
            [OutlookSafeButton] = AppButton.OutlookSafe,
            [PowerPointSafeButton] = AppButton.PowerPointSafe,
            [ResetOneDriveButton] = AppButton.ResetOneDrive,
            [RenewIpButton] = AppButton.RenewIp,
            [ComplianceButton] = AppButton.Compliance,
            [PortalButton] = AppButton.Portal,
            [FullResetButton] = AppButton.FullReset,
            [RepairWuButton] = AppButton.RepairWu,
            [CheckWuButton] = AppButton.CheckWu,
            [RefreshButton] = AppButton.Refresh,
            [BackupKeyButton] = AppButton.BackupKey,
            [SyncClockButton] = AppButton.SyncClock,
            [UpdateDefenderButton] = AppButton.UpdateDefender,
            [CheckDefenderButton] = AppButton.CheckDefender,
            [FreeSpaceButton] = AppButton.FreeSpace,
            [RefreshSignInButton] = AppButton.RefreshSignIn,
            [ResetSignInButton] = AppButton.ResetSignIn,
            [TeamsCacheButton] = AppButton.TeamsCache,
            [ReloadAppsButton] = AppButton.Diagnose,
            [CheckAgainButton] = AppButton.Diagnose,
            // The same action from Home.
            [HomePackageLink] = AppButton.Package,
        };
        _tiles = new Dictionary<Button, ToolGroup>
        {
            [AppsTile] = ToolGroup.Apps,
            [SignInTile] = ToolGroup.SignIn,
            [MicrosoftTile] = ToolGroup.Microsoft365,
            [WindowsTile] = ToolGroup.Windows,
            [DefenderTile] = ToolGroup.Defender,
            [NetworkTile] = ToolGroup.NetworkStorage,
        };
        _helperTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _helperTimer.Tick += (_, _) =>
        {
            _helperTimer.Stop();
            ApplyHelpers();
        };
        // The mascot pauses while the window is minimized or not visible (instance events, not static ones).
        StateChanged += (_, _) =>
        {
            ApplyHelpers();
            FitDrawerAfterRestore();
        };
        IsVisibleChanged += (_, _) => ApplyHelpers();
        // "today" and "yesterday" are said when a page is built: a window left open past midnight builds them again.
        Activated += (_, _) => RefreshDatesIfTheDayChanged();
        // ... and at midnight, for a window that stays the active one overnight.
        ScheduleMidnight();
        Closed += (_, _) => _midnightTimer?.Stop();
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        // A key turns the keyboard cues on before the window's own keys are handled (Backspace and
        // Alt+Left move the focus there), and also for a key a control already handled.
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler((_, e) => { if (IsNavigationKey(e)) { OnKeyInput(); } }), handledEventsToo: true);
        PreviewKeyDown += OnWindowKeyDown;
        // Focus that arrives while the last input was not a key (a click, or the app moving it
        // after a run, a page change or a dialog) keeps the focus for a screen reader, without the ring;
        // the element gets its ring back when it loses the focus, so at most one is held.
        PreviewGotKeyboardFocus += (_, e) => { if (!_keyboardCues && e.NewFocus is FrameworkElement element) { QuietFocusVisual(element); } };
        PreviewLostKeyboardFocus += (_, e) => { if (e.OldFocus is FrameworkElement element) { RestoreFocusVisual(element); } };
        // After its own handler, a button's click tells which Home element opened a path (handled clicks too).
        AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnAnyButtonClick), handledEventsToo: true);
        PreviewMouseDown += (_, _) => _keyboardCues = false;
        PreviewStylusDown += (_, _) => _keyboardCues = false;
        _remediesPath = UserFiles.RemediesFile();
        _remedies = RemedyStore.Load(_remediesPath, _remedyUser, DateTime.UtcNow);
        foreach (var button in _actionButtons.Keys) { _helpTexts[button] = AutomationProperties.GetHelpText(button); }
        RetryCaveatText.Text = AppDecisions.RetryCaveat;
        AppsIntro.Text = ProblemPaths.Get(Problem.AppWontInstall).Intro;
        StepsList.ItemsSource = _stepViews;
        _liveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _liveTimer.Tick += (_, _) => RenderLive();
        SourceInitialized += (_, _) =>
        {
            ThemeManager.ApplyTitleBar(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
            WatchScreen();
            RestorePlacement();
            // A static SystemEvents handler only once the window is really shown (it is removed on
            // closing): a window that is created but never shown or closed (the unit tests make
            // many) would otherwise keep a SystemEvents link to its STA thread, and the test host
            // could hang when it exits (seen in CI).
            SystemEvents.TimeChanged += OnTimeChanged;
        };
        Loaded += OnLoaded;
        Closing += OnClosingWindow;
        ThemeManager.Changed += OnThemeChanged;
        ToolInvoker = RaiseTool;
        // A screen that changes while the app is open (scale here; resolution and work area once shown).
        DpiChanged += (_, _) => ScheduleRefit();
        _watcher.ShuttingDown += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            // The service stops for an upgrade or uninstall; the app closes so its files can be replaced.
            Close();
        }));
        _watcher.Connected += () => Dispatcher.BeginInvoke(new Action(OnServiceConnected));
        _watcher.Lost += () => Dispatcher.BeginInvoke(new Action(OnServiceLost));
        ShowHome();
        ShowSupportContact();
    }

    /// <summary>
    /// UI Automation sees only what is shown: the controls of a page that is not shown (and of
    /// the drawer while it is closed) are not in its tree at all.
    /// </summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new ShownOnlyWindowAutomationPeer(this);

    // --- One thing at a time --------------------------------------------------------------

    /// <summary>
    /// Runs a button's work with every action button disabled, so nothing can start twice or
    /// overlap; an unexpected error is shown instead of closing the app. Keyboard focus returns
    /// to where it was, or goes to the result that was just shown.
    /// </summary>
    private async void Guarded(Func<Task> work, bool byUser = true, bool showStatus = true)
    {
        if (_busy) { return; }
        var focused = Keyboard.FocusedElement as IInputElement;
        // A tool shows its progress and result in the status below the page it was started
        // from; the page stays, and the status is brought into view. A check the app starts by
        // itself scrolls nothing.
        if (byUser && showStatus) { _ = Dispatcher.BeginInvoke(new Action(() => StatusPanel.BringIntoView()), DispatcherPriority.Loaded); }
        _startFocus = focused;
        // What this work shows in the status belongs to no tool, unless it starts a tool's run
        // (RunActionAsync, StartCheck); work that shows nothing (a cancelled confirmation) leaves the
        // status and its tool as they were.
        // What this work shows in the status belongs to the page it was started from.
        _runPage = PageKey();
        var statusBefore = StatusText.Text;
        var toolBefore = _statusTool;
        SetStatusTool(null);
        _busy = true;
        ShowBusy(true);
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            App.LogError(ex);
            ShowUnexpectedError(ex);
        }
        finally
        {
            if (_statusTool == null && StatusText.Text == statusBefore) { SetStatusTool(toolBefore); }
            // A run that is being followed keeps the window busy until its result is in.
            if (!_following) { EndBusy(focused); }
        }
    }

    /// <summary>The tool the status is about from now on (null: none); a tool's detail page shows only its own.</summary>
    private void SetStatusTool(ToolInfo tool)
    {
        _statusTool = tool;
        PlaceStatus();
    }

    /// <summary>
    /// Ends the busy state. A result that was just shown in the open drawer gets the keyboard
    /// focus, so a keyboard or screen-reader user lands on it; otherwise the focus returns to
    /// where it was (the button that started the run). Nothing moves while the window is not
    /// active or the user is already reading the drawer.
    /// </summary>
    private void EndBusy(IInputElement focused = null)
    {
        _busy = false;
        ShowBusy(false);
        var toResult = _focusResultAtEnd && _drawerOpen;
        _focusResultAtEnd = false;
        if (Drawer.IsKeyboardFocusWithin) { return; }
        if (toResult && IsActive && FocusResult())
        {
            _returnFocus = focused;
            return;
        }
        if (focused is UIElement { IsEnabled: true, IsVisible: true } element) { element.Focus(); return; }
        // A tool's row was rebuilt while its run went on (its last run changed): the same row's
        // button gets the focus back.
        if (focused is FrameworkElement { DataContext: ToolRowView row } stale && !stale.IsVisible) { FocusToolRow(row.Tool, run: stale.Name == "RowRun"); }
    }

    /// <summary>
    /// Moves the keyboard focus to the result: to the first card when it is a problem or a point
    /// to check, otherwise to the RESULT heading, which reads out the status.
    /// </summary>
    private bool FocusResult()
    {
        AutomationProperties.SetName(ResultHeading, "Result: " + StatusText.Text);
        if (_cards.Count > 0 && _cards[0].Kind is CardKind.Error or CardKind.Warning && FocusCard(0)) { return true; }
        return ResultHeading.Focus();
    }

    private bool FocusCard(int index)
    {
        Cards.UpdateLayout();
        return index >= 0 && index < Cards.Items.Count && Cards.ItemContainerGenerator.ContainerFromIndex(index) is ContentPresenter container
            && VisualTreeHelper.GetChildrenCount(container) > 0 && VisualTreeHelper.GetChild(container, 0) is UIElement card && card.Focus();
    }

    /// <summary>The card that has the keyboard focus, or -1.</summary>
    private int FocusedCardIndex()
    {
        if (!Cards.IsKeyboardFocusWithin) { return -1; }
        for (var i = 0; i < Cards.Items.Count; i++)
        {
            if (Cards.ItemContainerGenerator.ContainerFromIndex(i) is UIElement { IsKeyboardFocusWithin: true }) { return i; }
        }
        return -1;
    }

    /// <summary>An error no handler expected: said in the status line, written to the error log.</summary>
    internal void ShowUnexpectedError(Exception ex)
    {
        SetStatus("Something went wrong: " + ex.Message + " Try again; if it repeats, contact your IT support.", Tone.Error);
    }

    // --- Start-up -------------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e) => Guarded(StartupAsync, byUser: false);

    private async Task StartupAsync()
    {
        _watcher.Start();
        SetStatus("Checking this device...", Tone.Neutral, show: false);
        // Packages left behind by an app that closed while one arrived.
        _ = Task.Run(() => _gateway.CleanIncoming(TimeSpan.FromHours(1)));
        var counts = await RefreshHealthAsync(keepStatus: false);
        var text = string.Format(CultureInfo.InvariantCulture, ProductInfo.DisplayName + " {0}: health check: {1}, {2}.", _version,
            Words.Count(counts.Fails, "problem"), Words.WarningCount(counts.Warns));
        _ = Task.Run(() => _gateway.ReportEvent(EventId.ClientHealthCounts, text));
        await ShowEarlierRunAsync();
        await CheckForUpdateAsync();
    }

    /// <summary>The newer version the window shows, or null.</summary>
    private UpdateOffer _update;

    /// <summary>
    /// While the version check is on (policy, then the user's choice, then the installer's value),
    /// shows the newer version the feed named last time, then, at most once a day, reads the feed
    /// again in the background. Off means no request and nothing shown. A feed that cannot be read
    /// keeps the last answer; nothing is downloaded or installed.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        try
        {
            if (!UpdateCheck.Setting(_settings.UpdateCheckOn, _status?.UpdateCheck).On)
            {
                ShowUpdate(null);
                return;
            }
            ShowUpdate(UpdateCheck.Clean(_settings.UpdateOffer));
            var now = DateTime.UtcNow;
            if (!UpdateCheck.Due(_settings.UpdateCheckedUtc, now)) { return; }
            var offer = await Task.Run(UpdateCheck.Fetch);
            _settings.UpdateCheckedUtc = now;
            if (offer != null) { _settings.UpdateOffer = offer; }
            _settings.Save(SettingsPath);
            ShowUpdate(UpdateCheck.Clean(_settings.UpdateOffer));
        }
        catch (Exception)
        {
            // A look in the background that fails changes nothing on screen and never stops the start-up.
        }
    }

    /// <summary>
    /// Keeps the newer version for About, and shows the start-up notice when it is due: a newer
    /// version not yet shown. Once shown, it is not shown again for the same version.
    /// </summary>
    private void ShowUpdate(UpdateOffer offer)
    {
        _update = UpdateCheck.IsNewer(_version, offer) ? offer : null;
        if (!UpdateCheck.NoticeDue(_update, _settings.UpdateNoticeShownFor))
        {
            if (_update == null) { UpdateNotice.Visibility = Visibility.Collapsed; }
            return;
        }
        UpdateNoticeText.Text = UpdateCheck.NoticeLine(_update);
        UpdateNotice.Visibility = Visibility.Visible;
        // A polite live region: a screen reader says the sentence once, as the other live texts.
        Announce(UpdateNoticeText);
        _settings.UpdateNoticeShownFor = _update.Version;
        _settings.Save(SettingsPath);
    }

    private void OnUpdateNoticeClose(object sender, RoutedEventArgs e) => UpdateNotice.Visibility = Visibility.Collapsed;

    /// <summary>The notice's link: the product's page on the publisher's site, opened in the browser.</summary>
    private void OnUpdateReleaseNotes(object sender, RoutedEventArgs e) => WebLink.Open(UpdateCheck.ReleaseNotesUrl);

    /// <summary>
    /// A repair still running from an earlier session is followed again; one that finished while the
    /// window was closed is shown once.
    /// </summary>
    private async Task ShowEarlierRunAsync()
    {
        if (_status?.LastResultAction != ToolbeltAction.RepairWU) { return; }
        if (_status.LastResultInProgress)
        {
            ClearResult();
            StartFollowing(new RunOutcome { Action = ToolbeltAction.RepairWU, RunId = _status.LastResultRunId, InProgress = true });
            return;
        }
        if (_status.LastResultRunId == _settings.LastShownRunId) { return; }
        ReceivedFile file = null;
        var outcome = await Task.Run(() => _gateway.GetLastResult(out file));
        ReceivedFile.Discard(file);
        if (outcome == null || outcome.InProgress) { return; }
        ClearResult();
        ShowFinal(outcome, earlierRun: true);
        // Shown at start-up, not the end of something the user just did: the focus stays put.
        _focusResultAtEnd = false;
        RememberShown(outcome.RunId);
    }

    private void RememberShown(string runId)
    {
        if (string.IsNullOrEmpty(runId)) { return; }
        _settings.LastShownRunId = runId;
        _settings.Save(SettingsPath);
    }

    // --- The service coming and going ---------------------------------------------------

    /// <summary>The watcher reached the service: repairs can start again once the checks agree.</summary>
    private void OnServiceConnected()
    {
        if (!_buttons.ServiceMissing) { return; }
        _buttons.ServiceReached();
        UpdateButtons();
        ShowHome();
        if (!_busy && !_following)
        {
            Guarded(async () =>
            {
                SetStatus("Checking this device...", Tone.Neutral, show: false);
                await RefreshHealthAsync(keepStatus: false);
            }, byUser: false);
        }
    }

    /// <summary>The watcher lost the service: no repair can start until it is back.</summary>
    private void OnServiceLost()
    {
        if (_buttons.ServiceMissing) { return; }
        _buttons.ServiceLost();
        UpdateButtons();
        ShowHome();
        // A run in progress says what happens to it itself.
        if (!_busy && !_following) { SetStatus(ButtonRules.ServiceMissing, Tone.Error, show: false); }
    }

    /// <summary>The clock or the time zone changed: times are converted with the new zone from now on.</summary>
    private static void OnTimeChanged(object sender, EventArgs e) => TimeZoneInfo.ClearCachedData();

    // --- Following a run through the service ----------------------------------------------

    /// <summary>
    /// Follows a run that goes on in the service without a connection to it: one reattached after
    /// the window was opened again, or one whose connection ended before its result. The window
    /// stays busy until the result is in, and shows it then.
    /// </summary>
    private void StartFollowing(RunOutcome run, string status = null)
    {
        _following = true;
        _buttons.StartFollowing();
        _followed = run;
        SetStatusTool(ToolCatalog.ByAction(run.Action));
        // The run followed is the run shown: its log and ticket name it while it goes on.
        _shownRun = run;
        ShowCopyLinks();
        _failedPolls = 0;
        _followFocus = _startFocus;
        ShowBusy(true);
        // Announced once, with the step it names (if any), so the next update does not repeat it.
        _announcedStep = ResultPresenter.CurrentStep(run.Records);
        SetStatus(status ?? ResultPresenter.LiveStatus(run, DateTime.UtcNow).Status, Tone.Neutral);
        _followTimer ??= CreateFollowTimer();
        _followTimer.Start();
        _ = PollFollowedAsync();
    }

    private DispatcherTimer CreateFollowTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += async (_, _) => await PollFollowedAsync();
        return timer;
    }

    private void StopFollowing()
    {
        _followTimer?.Stop();
        _following = false;
        _buttons.StopFollowing();
        _followed = null;
        ShowStatusForPage();
    }

    private async Task PollFollowedAsync()
    {
        if (_polling || !_following) { return; }
        _polling = true;
        try
        {
            ReceivedFile file = null;
            var outcome = await Task.Run(() => _gateway.GetLastResult(out file));
            if (!_following) { ReceivedFile.Discard(file); return; }
            var name = ActionText.Name(_followed.Action);
            if (outcome == null || (!string.IsNullOrEmpty(_followed.RunId) && outcome.RunId != _followed.RunId))
            {
                ReceivedFile.Discard(file);
                if (++_failedPolls < MaxFailedPolls) { return; }
                var gaveUp = _followed.Action;
                StopFollowing();
                // The service does not answer: no repair can start until it does again.
                _buttons.FollowingGaveUp();
                if (gaveUp == ToolbeltAction.DiagnoseApps) { SetDiagnosis(AppDecisions.From(null, DateTime.UtcNow), null); }
                _retryApps = null;
                SetStatus(name + " may still be running, but the " + ProductInfo.DisplayName + " service does not answer. Open this window again later to see the result.", Tone.Warning);
                EndBusy(_followFocus);
                return;
            }
            _failedPolls = 0;
            if (outcome.InProgress)
            {
                ReceivedFile.Discard(file);
                _shownRun = outcome;
                // A stored result is read whole each time (it may drop records in the middle), so
                // there is no cursor here. A background run's status counts its minutes; another
                // keeps "continues in the background".
                RenderView(ResultPresenter.Live(outcome, DateTime.UtcNow), final: false, setStatus: ActionText.RunsInBackground(outcome.Action), StatusChange.LiveUpdate);
                return;
            }
            StopFollowing();
            try
            {
                var view = ShowFinal(outcome);
                var applied = outcome.Action != ToolbeltAction.DiagnoseApps || ApplyDiagnosis(outcome);
                await FinishRunAsync(new RunResult { Outcome = outcome, File = file, View = view });
                if (outcome.Action == ToolbeltAction.DiagnoseApps) { await ShowPostCheckStatusAsync(outcome, applied); }
            }
            finally
            {
                EndBusy(_followFocus);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            App.LogError(ex);
            StopFollowing();
            ShowUnexpectedError(ex);
            EndBusy(_followFocus);
        }
        finally
        {
            _polling = false;
        }
    }

    // --- Health ---------------------------------------------------------------------------

    private async Task<(int Fails, int Warns)> RefreshHealthAsync(bool keepStatus)
    {
        // Home's helper works while the checks run, and shows Done for a moment after a clean one.
        _checkingHealth = true;
        ShowHome();
        (StatusInfo Status, bool Missing, List<HealthResult> Health, DsregInfo Dsreg, NetworkSummary Network) read;
        try
        {
            read = await Task.Run(() =>
            {
                StatusInfo s;
                var m = false;
                try { s = _gateway.GetStatus(); }
                catch (ServiceException ex)
                {
                    s = null;
                    // Not installed, stopped and not startable, or not answering. A service of another
                    // version or identity is there: its own message comes when an action starts.
                    m = ex.Code == ResultCode.ServiceUnavailable;
                }
                var h = HealthChecks.Run(new SystemHealthSource(), s, out var d);
                // The network summary of the first-line facts, read with the checks.
                return (s, m, h, d, NetworkSummary.Of(AdaptersOrNull()));
            });
        }
        finally
        {
            _checkingHealth = false;
        }
        _network = read.Network;
        _healthCheckedUtc = DateTime.UtcNow;
        _doneUntilUtc = _healthCheckedUtc.Value.AddSeconds(5);
        return ApplyHealth(read.Status, read.Missing, read.Health, read.Dsreg, keepStatus);
    }

    /// <summary>A health row whose fix presses a tool the organization switched off shows no fix link.</summary>
    private HealthRow NewHealthRow(HealthResult result) => new(result, ToolCatalog.FixOff(result.Fix, _switchedOff));

    /// <summary>The health checks as read: shown on Home, the problem paths and the buttons.</summary>
    private (int Fails, int Warns) ApplyHealth(StatusInfo status, bool missing, List<HealthResult> health, DsregInfo dsreg, bool keepStatus)
    {
        _status = status;
        _switchedOff = SwitchedOffTools.From(missing ? null : status, SwitchedOffTools.ReadPolicy);
        _health = health;
        _dsreg = dsreg;
        if (missing) { _buttons.ServiceLost(); } else { _buttons.ServiceReached(); }
        // The rows first: the buttons read them (Retry work apps and settings is off without the IME).
        var imeMissingBefore = ImeMissing();
        _healthRows = HealthView.Rows(health);
        UpdateButtons();
        // For IT says it on the Retry tool's row and page as soon as the IME comes or goes (a rebuilt list keeps
        // the focused row, see ShowToolList).
        if (_page == Page.It && imeMissingBefore != ImeMissing())
        {
            ShowToolList();
            ShowToolDetail();
        }
        ShowHealth();
        ShowSupportContact();
        ShowHome();
        if (_page == Page.Problem) { ShowProblem(_problem, focus: false); }
        ShowAppChecks();
        if (!keepStatus)
        {
            var (text, tone) = missing ? (ButtonRules.ServiceMissing, Tone.Error) : HealthChecks.Summary(_healthRows);
            // The health checks are the status card and the health strip; the status area stays for runs.
            SetStatus(text, tone, announce: false, show: false);
            Announce(MainFindingTitle);
        }
        return (_healthRows.Count(r => r.Status == HealthStatus.Fail), _healthRows.Count(r => r.Status == HealthStatus.Warn));
    }

    /// <summary>
    /// The health block: the verdict, the rows that need attention, and a link that shows
    /// or hides the OK ones. A healthy device shows the verdict line only.
    /// </summary>
    private void ShowHealth()
    {
        // At most two rows that need attention, problems first; the others behind Show all (Home must fit 540 x 640 at 150 %).
        var visible = HealthView.Visible(_healthRows, _showAllHealth, HealthView.HomeAttentionRows);
        HealthList.ItemsSource = visible.Select(NewHealthRow).ToList();
        HealthList.Visibility = visible.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HealthVerdict.Text = _healthRows.Count == 0 ? "" : HealthView.Verdict(_healthRows);
        // No empty, sizeless text before the first check is in.
        HealthVerdict.Visibility = _healthRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HealthVerdict.Foreground = ThemeBrush.For(
            _healthRows.Any(r => r.Status == HealthStatus.Fail) ? Tone.Error
            : _healthRows.Any(r => r.Status == HealthStatus.Warn) ? Tone.Warning
            : Tone.Success);
        var toggle = HealthView.ToggleText(_healthRows, _showAllHealth, HealthView.HomeAttentionRows);
        HealthToggle.Content = toggle;
        // A short text on the verdict's line; its name says what it shows or hides.
        AutomationProperties.SetName(HealthToggle, HealthView.ToggleName(_healthRows, _showAllHealth, HealthView.HomeAttentionRows));
        // In the health view Back is the way out, so the link is not shown there.
        HealthToggle.Visibility = toggle.Length == 0 || _showAllHealth ? Visibility.Collapsed : Visibility.Visible;
        ShowHealthView();
    }

    /// <summary>
    /// Home or its health view. Show all opens the device health as a view of its own under
    /// Home's tab (its title and Back beside the mascot, every row); the status, the problem tiles and
    /// the line for a person come back with Back. Home then always fits its window.
    /// </summary>
    private void ShowHealthView()
    {
        var view = _showAllHealth;
        HealthViewHeader.Visibility = view ? Visibility.Visible : Visibility.Collapsed;
        // The view's title already says Device health; the card keeps only its verdict there.
        HealthCardTitle.Visibility = view ? Visibility.Collapsed : Visibility.Visible;
        foreach (var part in new UIElement[] { StatusCard, StatusButtons, HomeProblemsHeading, HomeTiles, HomePersonLine })
        {
            part.Visibility = view ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Show all 10: the health view, the focus on Back (its first control).</summary>
    private void OnHealthToggle(object sender, RoutedEventArgs e)
    {
        if (_showAllHealth)
        {
            OnHealthBack(sender, e);
            return;
        }
        _showAllHealth = true;
        ShowHealth();
        Dispatcher.BeginInvoke(new Action(() => HealthBackButton.Focus()), DispatcherPriority.Loaded);
    }

    /// <summary>Back from the health view to Home, the focus on Show all.</summary>
    private void OnHealthBack(object sender, RoutedEventArgs e)
    {
        LeaveHealthView();
        Dispatcher.BeginInvoke(new Action(() => { if (HealthToggle.IsVisible) { HealthToggle.Focus(); } else { FocusTitle(); } }), DispatcherPriority.Loaded);
    }

    private void LeaveHealthView()
    {
        if (!_showAllHealth) { return; }
        _showAllHealth = false;
        ShowHealth();
    }

    private void ShowSupportContact()
    {
        var text = _status?.SupportText;
        SupportText.Text = text ?? "";
        SupportText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        // The service accepts only https addresses; checked again here before it becomes a link.
        // An address with a user name part ("https://name@host") is not shown: it reads as a
        // different host than the one it opens.
        var url = Handover.SupportLink(_status?.SupportUrl);
        if (url != null)
        {
            // Contact IT on Home's "Need a person?" and on "My problem is not listed" opens the link.
            foreach (var link in new[] { ContactLink, NotListedContactLink })
            {
                link.NavigateUri = url;
                link.ToolTip = url.AbsoluteUri;
                AutomationProperties.SetHelpText(link, "Opens " + url.AbsoluteUri);
            }
        }
        ContactBlock.Visibility = url != null ? Visibility.Visible : Visibility.Collapsed;
        NotListedContactBlock.Visibility = ContactBlock.Visibility;
        // The way to IT support in words: the organization's text, or the user's own way without a link or a text.
        var (hasLink, hasText) = SupportSet();
        ContactEndText.Text = Handover.HomeEnd(hasLink, hasText);
        ContactEndText.Visibility = ContactEndText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        HomeContactLine.Visibility = hasLink || hasText || ContactEndText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotListedEndText.Text = Handover.NotListedWay(hasLink, text);
        NotListedEndText.Visibility = NotListedEndText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowHandoverLine();
    }

    /// <summary>Whether the organization set a support link (one the window shows) and a support text.</summary>
    private (bool HasLink, bool HasText) SupportSet()
        => (Handover.SupportLink(_status?.SupportUrl) != null, !string.IsNullOrWhiteSpace(_status?.SupportText));

    /// <summary>
    /// The line under a result that names IT support: Copy details for IT, then the way there,
    /// also when no support link is set. The paste hint does not count.
    /// </summary>
    private void ShowHandoverLine(string status = null)
    {
        var text = WithoutPasteHint(status ?? StatusText.Text);
        // Not on For IT support: its page fits the default window only without the extra line, and
        // Copy details for IT is in View details there too.
        var show = _page != Page.It && Handover.ResultNamesItSupport(text, _cards);
        var (hasLink, _) = SupportSet();
        HandoverText.Text = show ? Handover.ResultLine(hasLink, _status?.SupportText) : "";
        HandoverText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Guarded(RefreshFromButtonAsync, showStatus: false);

    private async Task RefreshFromButtonAsync()
    {
        SetStatus("Checking this device...", Tone.Neutral, show: false);
        await RefreshHealthAsync(keepStatus: false);
    }

    // --- Running actions ------------------------------------------------------------------

    private sealed class RunResult
    {
        public RunOutcome Outcome { get; set; }

        /// <summary>The view shown; null when the outcome was handed back without being shown.</summary>
        public ResultView View { get; set; }

        public ReceivedFile File { get; set; }
    }

    /// <summary>
    /// Runs an action in the service, shows its records live and its result at the end. Returns
    /// null when the connection ended while the run went on: the window then follows it, and
    /// <see cref="FinishRunAsync"/> runs when its result is in.
    /// </summary>
    /// <param name="handBack">
    /// Decides, before anything is shown, that the caller handles the outcome itself (a request
    /// the service wants confirmed again); the result then has no view.
    /// </param>
    private async Task<RunResult> RunActionAsync(ToolbeltAction action, bool confirmed, int confirmedOtherSessions, string runningText, Func<RunOutcome, bool> handBack = null, int interfaceIndex = 0)
    {
        // The status is this action's from now on (a tool's, or none for the package and the diagnosis).
        SetStatusTool(ToolCatalog.ByAction(action));
        SetStatus(runningText, Tone.Neutral);
        _announcedStep = null;
        ClearResult();
        // Running until its result is in; the run ID arrives when the service accepts the run.
        var live = new RunOutcome { Action = action, StartedUtc = DateTime.UtcNow, InProgress = true };
        lock (_liveLock) { _live = live; _liveTimeline = new Timeline(); _liveDirty = false; }
        _shownRun = live;
        ShowCopyLinks();
        _liveTimer.Start();
        RunOutcome outcome;
        ReceivedFile file = null;
        try
        {
            outcome = await Task.Run(() => _gateway.Run(action, confirmed, confirmedOtherSessions, record =>
            {
                lock (_liveLock)
                {
                    live.Records.Add(record);
                    if (record.IsCut) { live.Truncated = true; }
                    _liveDirty = true;
                }
            }, out file, runId =>
            {
                lock (_liveLock) { live.RunId = runId; }
            }, interfaceIndex));
        }
        finally
        {
            _liveTimer.Stop();
            lock (_liveLock) { _live = null; _liveTimeline = null; }
        }

        if (outcome.InProgress)
        {
            // "Taking longer than expected; it continues in the background."
            RenderView(ResultPresenter.Live(outcome, DateTime.UtcNow), final: false, setStatus: false, StatusChange.LiveUpdate);
            StartFollowing(outcome, ResultPresenter.Final(outcome).Status);
            return null;
        }
        if (handBack?.Invoke(outcome) == true)
        {
            ReceivedFile.Discard(file);
            return new RunResult { Outcome = outcome };
        }
        var view = ShowFinal(outcome);
        if (outcome.Result == ResultCode.EngineDidNotStart)
        {
            var text = string.Format(CultureInfo.InvariantCulture, ProductInfo.DisplayName + " {0}: {1} did not start.", _version, ActionText.Name(action));
            _ = Task.Run(() => _gateway.ReportEvent(EventId.ClientActionNotStarted, text));
        }
        var result = new RunResult { Outcome = outcome, View = view, File = file };
        return result;
    }

    /// <summary>What follows every run once its result is in: history, the package, the health view.</summary>
    private async Task FinishRunAsync(RunResult run)
    {
        var outcome = run.Outcome;
        if (ResultPresenter.BelongsInHistory(outcome)) { History.Add(outcome.Action.ToString(), ResultPresenter.HistoryResult(outcome), HistoryOverride); }
        RememberShown(outcome.RunId);

        FollowWuCheck(outcome);

        // The apps were read before the retry and none of them is one it retries (none failed, or each is only offered).
        var noneRetried = false;
        if (outcome.Action == ToolbeltAction.RetryAppsAndPolicies)
        {
            // A retry the engine ran is a remedy for every app it retries, tied to the
            // diagnosis they came from and to this run; a run whose IME half stood down for
            // other signed-in users retried nothing.
            var retried = _retryApps;
            _retryApps = null;
            noneRetried = retried is { Count: 0 } && _diagnosisBase is { Run: DiagnosisRunState.Finished };
            if (ResultPresenter.BelongsInHistory(outcome) && retried is { Count: > 0 } && !ResultPresenter.RetrySkipped(outcome))
            {
                RecordRemedies(retried, RemedyKind.RetryAppsAndPolicies, outcome.RunId ?? "", _retryPressedUtc);
            }
        }

        if (outcome.Action == ToolbeltAction.CollectDiag)
        {
            await FinishPackageAsync(run);
        }
        else
        {
            ReceivedFile.Discard(run.File);
        }

        // The health view agrees with the result: after the actions that change the device, whenever a
        // restart is now needed (the pending-restart row flips) and after every tool that ran (the
        // service read its readouts again before it reported the end); the tool's own row then says its
        // new state in one line.
        var tool = ToolCatalog.ByAction(outcome.Action);
        var afterTool = tool != null && HealthAfterTools && ResultPresenter.BelongsInHistory(outcome) && !outcome.InProgress;
        if (run.View.RestartNeeded || outcome.Action is ToolbeltAction.RetryAppsAndPolicies or ToolbeltAction.FullReset or ToolbeltAction.RepairWU || afterTool)
        {
            var before = HealthRowNamed(tool?.HealthRow);
            var counts = await RefreshHealthAsync(keepStatus: true);
            ShowHealthLine(tool, before);
            if (ResultPresenter.HealthStatusFollows(outcome, run.View))
            {
                // "Retry requested" until a post-check detected every retried app. One
                // automatic post-check when the retry ends; after that only the user's Check again.
                var apps = noneRetried ? AppsAfterRetry.NoneRetried : AppsAfterRetry.Unknown;
                var group = PostCheck.RetryGroup(_remedies, DateTime.UtcNow, outcome.RunId ?? "");
                if (group.Count > 0 && !_buttons.ServiceMissing)
                {
                    SetStatus("Retry work apps and settings finished. Checking the retried apps once (read-only)...", Tone.Neutral, announce: false);
                    if (await ReadAppsInBackgroundAsync()) { apps = PostCheck.AfterRetry(group); }
                }
                // Skipped for other signed-in users: only the sync was requested, and no app was retried.
                // The other apps of the same diagnosis that need attention: never "Done." while they do.
                var attention = noneRetried ? _diagnosis?.Apps.Count(AppDecisions.InShortList) ?? 0 : PostCheck.OtherAttention(_diagnosis, group);
                var (text, tone) = ResultPresenter.RetryDone(counts.Fails, counts.Warns, apps, ResultPresenter.RetrySkipped(outcome), attention);
                SetStatus(text, tone);
            }
        }
    }

    /// <summary>
    /// The verdict of "Windows Update keeps failing" follows the latest Check Windows Update, not the
    /// health rows alone; a repair or a cleanup afterwards can change its answer, so it no longer counts.
    /// </summary>
    private void FollowWuCheck(RunOutcome outcome)
    {
        if (outcome.Action == ToolbeltAction.CheckWindowsUpdate && !outcome.InProgress) { _wuPathCheck = WuCheck.ForPath(outcome); }
        else if (outcome.Action is ToolbeltAction.RepairWU or ToolbeltAction.FreeDiskSpace) { _wuPathCheck = null; }
        else { return; }
        if (_page == Page.Problem && _problem == Problem.WindowsWontUpdate) { ShowProblem(_problem, focus: false); }
    }
    /// <summary>A health row as the window shows it now, by its name; null when there is none.</summary>
    private HealthResult HealthRowNamed(string name) => string.IsNullOrEmpty(name) ? null : _healthRows.FirstOrDefault(r => r.Name == name);

    /// <summary>
    /// A tool that ran in the app (a check, Bring my window back, a safe-mode start) is followed by
    /// the health checks too, and its row's new state is said in one line under the status.
    /// </summary>
    private async Task RefreshAfterToolAsync(ToolInfo tool)
    {
        if (!HealthAfterTools) { return; }
        var before = HealthRowNamed(tool?.HealthRow);
        await RefreshHealthAsync(keepStatus: true);
        ShowHealthLine(tool, before);
    }

    /// <summary>The line under the status with the tool's health row as it is now ("Clock: now OK"); hidden when the tool has none.</summary>
    private void ShowHealthLine(ToolInfo tool, HealthResult before)
    {
        var text = HealthView.AfterRun(tool?.HealthRow, before, HealthRowNamed(tool?.HealthRow));
        StatusHealthText.Text = text;
        StatusHealthText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (text.Length > 0)
        {
            var after = HealthRowNamed(tool.HealthRow);
            StatusHealthText.Foreground = ThemeBrush.For(after?.Status switch { HealthStatus.Pass => Tone.Success, HealthStatus.Warn => Tone.Warning, _ => Tone.Error });
        }
        // The health line sits above "Does it work now?", so the question moved down.
        KeepWorksNowInView();
    }

    /// <summary>
    /// The live view of a run the app started. Without new records only the status changes (a
    /// background run counts its minutes); new records are added to the timeline from where it
    /// stopped.
    /// </summary>
    private void RenderLive()
    {
        ResultView view = null;
        (string Status, string Step) status;
        lock (_liveLock)
        {
            if (_live == null) { return; }
            var background = ActionText.RunsInBackground(_live.Action);
            if (_liveDirty)
            {
                _liveDirty = false;
                view = ResultPresenter.Live(_live, DateTime.UtcNow, null, _liveTimeline);
            }
            status = background ? ResultPresenter.LiveStatus(_live, DateTime.UtcNow) : (null, null);
        }
        if (view != null)
        {
            // The step structure is built only when records arrived; between them only the
            // running step's time moves (below, outside the lock).
            _liveSteps = view.Steps;
            _liveStepsNote = view.StepsNote;
            RenderView(view, final: false, setStatus: false, StatusChange.LiveUpdate);
        }
        else
        {
            foreach (var step in _liveSteps) { step.Tick(DateTime.UtcNow); }
            ShowSteps(_liveSteps, _liveStepsNote);
        }
        if (status.Status != null) { SetLiveStatus(status.Status, status.Step); }
    }

    /// <summary>A live status: shown at once, heard only at a new engine step.</summary>
    private void SetLiveStatus(string text, string step)
    {
        var announce = Announcements.ShouldAnnounce(StatusChange.LiveUpdate, _announcedStep, step);
        if (announce) { _announcedStep = step; }
        SetStatus(text, Tone.Neutral, announce);
    }

    private ResultView ShowFinal(RunOutcome outcome, bool earlierRun = false)
    {
        var view = ResultPresenter.Final(outcome, earlierRun);
        _shownRun = outcome;
        // A status that the health checks replace at once is not read out twice.
        RenderView(view, final: true, setStatus: true, ResultPresenter.HealthStatusFollows(outcome, view) ? StatusChange.Quiet : StatusChange.Once);
        // The drawer opens only when the user asks (View details). A read-only check says its answer on
        // the page.
        // Not on a problem path whose verdict line already says this check's answer.
        if (view.ShowAnswer && !(_page == Page.Problem && ProblemPaths.VerdictSays(_problem, outcome.Action))) { ShowAnswer(view.Status); }
        if (view.CopyLog) { CopyRunLog(outcome, view); }
        // Every run the engine was asked to do is kept in the run history; a request the service
        // turned down before anything ran is not (as in history.log).
        if (ResultPresenter.BelongsInHistory(outcome)) { SaveToHistory(NewHeader(outcome, null), view.LogLines.ToList(), view.Cards.ToList()); }
        _focusResultAtEnd = true;
        // A repair that ended while the window was closed asks for a restart only while the device
        // has not restarted since; the service clears its marker at the restart.
        var restart = view.RestartNeeded && (!earlierRun || !string.IsNullOrEmpty(_status?.RestartRequiredSinceUtc));
        if (restart)
        {
            RestartBanner.Visibility = Visibility.Visible;
            Announce(RestartBanner);
        }
        return view;
    }

    /// <summary>
    /// Saves the run's log to Downloads for support, off the window's thread: a redirected
    /// Downloads folder that is offline must not freeze the window. Its card and log line are
    /// added when it is done, if the same result is still shown.
    /// </summary>
    private void CopyRunLog(RunOutcome outcome, ResultView view)
    {
        var generation = _resultGeneration;
        var header = NewHeader(outcome, null);
        var lines = view.LogLines.ToList();
        var cards = view.Cards.ToList();
        Task.Run(() => SupportPackage.SaveRunLog(header, lines, cards, UserFiles.Downloads())).ContinueWith(task =>
        {
            var error = task.Exception?.GetBaseException();
            if (error != null && error is not OutOfMemoryException) { App.LogError(error); }
            if (generation != _resultGeneration) { return; }
            if (error == null)
            {
                AddCard(ResultCard.From(FindingSeverity.Info, "The log of this run is in your Downloads folder", task.Result,
                    "If you contact your IT support about this, give them this file."));
                AppendLog("Log copied to " + task.Result + " for support.");
            }
            else
            {
                AppendLog("The log could not be copied to Downloads: " + error.Message);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>A run-log header for a service run or a check in the app, at this moment.</summary>
    private RunLogHeader NewHeader(RunOutcome run, LocalRun check) => new()
    {
        Outcome = run,
        Check = run == null ? check : null,
        ActionName = "(none: no run shown)",
        AppVersion = _version,
        DeviceName = Environment.MachineName,
        User = Environment.UserDomainName + "\\" + Environment.UserName,
        Zone = TimeZoneInfo.Local,
        NowUtc = DateTime.UtcNow,
    };

    /// <summary>
    /// The header of what is shown now: a copy of the service run (a live run gets more records
    /// on another thread, so its log is of this moment) or of the check in the app.
    /// </summary>
    private RunLogHeader ShownHeader()
    {
        RunOutcome run = null;
        if (_shownRun != null)
        {
            lock (_liveLock)
            {
                run = new RunOutcome { Action = _shownRun.Action, RunId = _shownRun.RunId, Result = _shownRun.Result, ExitCode = _shownRun.ExitCode, StartedUtc = _shownRun.StartedUtc, InProgress = _shownRun.InProgress, Truncated = _shownRun.Truncated };
                run.Records.AddRange(_shownRun.Records.ToList());
            }
        }
        return NewHeader(run, _shownCheck?.Snapshot());
    }

    /// <summary>
    /// Keeps the run's log in the user's run history, off the window's thread. The history
    /// is a convenience: a write that fails is logged and said on the result while it is shown
    /// ("its log could not be saved on this PC"); Copy details for IT then names no run log.
    /// </summary>
    private void SaveToHistory(RunLogHeader header, List<string> lines, List<ResultCard> cards)
    {
        if (header.Outcome == null && header.Check == null) { return; }
        // The run goes into the case file of the path shown (or updates its line there).
        if (_caseFile.Record(ShownPath(), header, cards)) { AskWorksNow(header, cards); }
        var generation = _resultGeneration;
        var folder = RunsFolderOverride ?? UserFiles.RunsFolder();
        _ = Task.Run(() => RunLogStore.Save(header, lines, cards, folder)).ContinueWith(task =>
        {
            var error = task.Exception?.GetBaseException();
            if (error != null && error is not OutOfMemoryException) { App.LogError(error); }
            // For IT shows each tool's last run, so it reads the history again once the log is in.
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (error != null && generation == _resultGeneration) { LogNotSaved(header); }
                if (_page == Page.It) { ShowIt(); }
            }));
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// After a repair on a problem path, "Does it work now?" with Yes, Not yet and Not
    /// tried (<see cref="Handover.AsksWorksNow"/>); a run already answered is not asked again.
    /// </summary>
    private void AskWorksNow(RunLogHeader header, List<ResultCard> cards)
    {
        var runId = header.RunId;
        var tool = header.Outcome != null ? ToolCatalog.ByAction(header.Outcome.Action) : ToolCatalog.ByCheckName(header.Check?.Name);
        if (!Handover.AsksWorksNow(_caseFile.PathOf(runId), tool, CaseFile.VerdictOf(header, cards)) || _caseFile.AnswerOf(runId).HasValue) { return; }
        if (string.Equals(_worksNowRunId, runId, StringComparison.Ordinal)) { return; }
        _worksNowRunId = runId;
        WorksNowText.Text = Handover.Question;
        AutomationProperties.SetName(WorksNowText, Handover.Question + " After " + header.Name);
        WorksNowQuestion.Visibility = Visibility.Visible;
        WorksNowAnswerText.Visibility = Visibility.Collapsed;
        KeepWorksNowInView();
    }

    /// <summary>
    /// The status panel was brought into view when the run started and grew since (the result, the
    /// line to IT support, and later the health line), so the question can sit below the window's edge.
    /// Brings it into view once layout is done; the keyboard focus stays put.
    /// </summary>
    private void KeepWorksNowInView()
    {
        if (WorksNowQuestion.Visibility != Visibility.Visible) { return; }
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (WorksNowQuestion.Visibility == Visibility.Visible) { WorksNowQuestion.BringIntoView(); }
        }), DispatcherPriority.ContextIdle);
    }

    private void OnWorksNowYes(object sender, RoutedEventArgs e) => AnswerWorksNow(WorksNowAnswer.Yes);

    private void OnWorksNowNotYet(object sender, RoutedEventArgs e) => AnswerWorksNow(WorksNowAnswer.NotYet);

    private void OnWorksNowNotTried(object sender, RoutedEventArgs e) => AnswerWorksNow(WorksNowAnswer.NotTried);

    /// <summary>
    /// The user's answer goes into the case file (both Copy details for IT texts name it); the
    /// run's own result, log and history line stay as they are. The line under the status says what is next.
    /// </summary>
    private void AnswerWorksNow(WorksNowAnswer answer)
    {
        if (_worksNowRunId == null || !_caseFile.Answer(_worksNowRunId, answer)) { return; }
        var (hasLink, _) = SupportSet();
        WorksNowQuestion.Visibility = Visibility.Collapsed;
        WorksNowAnswerText.Text = Handover.AfterAnswer(answer, hasLink, _status?.SupportText);
        WorksNowAnswerText.Visibility = Visibility.Visible;
        Announce(WorksNowAnswerText);
        // The buttons are gone: the keyboard focus goes to View details, as after the safe-mode answer.
        Dispatcher.BeginInvoke(new Action(() => DetailsButton.Focus()), DispatcherPriority.Loaded);
    }

    /// <summary>The run's log could not be saved: a short note on the result; Copy details for IT stays and names no run log.</summary>
    private void LogNotSaved(RunLogHeader header)
    {
        _logNotSavedRunId = header.RunId ?? "";
        AddCard(ResultCard.From(FindingSeverity.Info, LogNotSavedNote(header.Check != null), "Copy details for IT still works; it names no run log."));
        AppendLog("The run log could not be saved on this PC.");
    }

    internal static string LogNotSavedNote(bool check)
        => check ? "The check ran, but its log could not be saved on this PC." : "The run ended, but its log could not be saved on this PC.";

    /// <summary>A check in the app starts: it is shown with its own local run ID.</summary>
    private void StartCheck(string name)
    {
        _shownCheck = LocalRun.Start(name, DateTime.UtcNow);
        ShowCopyLinks();
        SetStatusTool(ToolCatalog.ByCheckName(name));
    }

    /// <summary>The check in the app ended: its end is confirmed and its log goes into the run history.</summary>
    private void FinishCheck()
    {
        if (_shownCheck == null) { return; }
        _shownCheck.EndedUtc = DateTime.UtcNow;
        SaveToHistory(NewHeader(null, _shownCheck.Snapshot()), _shownLog.ToList(), _cards.ToList());
    }

    /// <summary>
    /// Shows a view. The cards are rebuilt only when they changed. The log grows while what is
    /// shown is the start of the new lines, so a keyboard or screen-reader user keeps their place
    /// while a run writes; when it is not (a stored result dropped records), it is rebuilt.
    /// </summary>
    /// <param name="change">
    /// Whether the status is set once (announced), is a repeated live update (announced only at a
    /// new engine step), or is replaced at once (not announced).
    /// </param>
    private void RenderView(ResultView view, bool final, bool setStatus, StatusChange change)
    {
        if (final || view.Cards.Count != _cards.Count)
        {
            _cards = view.Cards.ToList();
            ShowCards();
        }
        ShowSteps(view.Steps, view.StepsNote);
        var append = LogUpdate.Append(_shownLog, view.LogLines);
        var atEnd = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 2;
        if (append == null)
        {
            _shownLog.Clear();
            _shownLog.AddRange(view.LogLines);
            LogBox.Text = string.Join(Environment.NewLine, _shownLog);
            if (atEnd) { LogBox.ScrollToEnd(); }
        }
        else if (append.Count > 0)
        {
            LogBox.AppendText((LogBox.Text.Length > 0 ? Environment.NewLine : "") + string.Join(Environment.NewLine, append));
            _shownLog.AddRange(append);
            if (atEnd) { LogBox.ScrollToEnd(); }
        }
        if (setStatus)
        {
            var announce = Announcements.ShouldAnnounce(change, _announcedStep, view.Step);
            if (announce && view.Step != null) { _announcedStep = view.Step; }
            SetStatus(view.Status, view.Tone, announce);
        }
    }

    // --- Primary buttons ------------------------------------------------------------------

    /// <summary>
    /// Why? beside the one-line "Try every failed app again": shows (or hides) the whole explanation
    /// below the line; the link's name says which.
    /// </summary>
    private void OnRetryWhy(object sender, RoutedEventArgs e)
    {
        var open = RetryCaveatText.Visibility != Visibility.Visible;
        RetryCaveatText.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        RetryWhyLink.Content = open ? "Hide why" : "Why?";
        AutomationProperties.SetName(RetryWhyLink, (open ? "Hide why: " : "Why: ") + "Retry work apps and settings");
    }

    private void OnRetry(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        // Pressed before this window read the apps (For IT, Home, a fix link): read them first (read-only), so
        // the retry has its apps and its post-check, and the Retry tool's sentence says how they stood.
        if (_diagnosisBase is not { Run: DiagnosisRunState.Finished } && !_buttons.ServiceMissing)
        {
            // The status is the retry's from now on, so it shows under the Retry tool's button too.
            SetStatusTool(ToolCatalog.ByAction(ToolbeltAction.RetryAppsAndPolicies));
            SetStatus("Reading the work apps first (read-only), so the retry can be checked afterwards...", Tone.Neutral, announce: false);
            await ReadAppsInBackgroundAsync();
        }
        // The apps this retry retries, from the diagnosis as read, and when it was asked for.
        _retryApps = PostCheck.Retried(_diagnosisBase);
        _retryPressedUtc = DateTime.UtcNow;
        var run = await RunActionAsync(ToolbeltAction.RetryAppsAndPolicies, false, -1, "Retry work apps and settings is running. This takes a few minutes.");
        if (run != null) { await FinishRunAsync(run); }
    });

    private void OnPackage(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        SetStatus("Building the device report...", Tone.Neutral);
        await SaveReportAsync();
        var run = await RunActionAsync(ToolbeltAction.CollectDiag, false, -1, "Creating the support package. This can take several minutes.");
        if (run != null) { await FinishRunAsync(run); }
    });

    /// <summary>The package the service delivered, saved to Downloads with the user's own files.</summary>
    private async Task FinishPackageAsync(RunResult run)
    {
        var outcome = run.Outcome;
        if (ResultPresenter.ReportsPackageCreated(outcome, run.File != null))
        {
            var text = string.Format(CultureInfo.InvariantCulture, ProductInfo.DisplayName + " {0}: support package created.", _version);
            _ = Task.Run(() => _gateway.ReportEvent(EventId.ClientPackageCreated, text));
        }
        if (run.File == null)
        {
            if (!string.IsNullOrEmpty(outcome.FileError))
            {
                AddCard(ResultCard.From(FindingSeverity.Warning, "The support package could not be received", outcome.FileError, "Create the support package again."));
            }
            return;
        }
        try
        {
            var dsreg = _dsreg?.Lines ?? Array.Empty<string>();
            var (path, notAdded) = await Task.Run(() => SupportPackage.Save(run.File, UserFiles.Downloads(), DateTime.Now,
                UserFiles.ReportFile(), UserFiles.HistoryFile(), dsreg, Environment.UserDomainName + "\\" + Environment.UserName, Environment.MachineName));
            var partial = outcome.ExitCode == EngineExitCode.Warnings;
            SetStatus(partial ? "Support package saved to your Downloads folder, but parts are missing. See the details." : "Support package saved to your Downloads folder.",
                partial ? Tone.Warning : Tone.Success);
            var detail = path + Environment.NewLine + "Contains the MDM diagnostics, the IME logs, the app and remediation state (IntuneState.xlsx), dsregcmd, the " + ProductInfo.DisplayName + " logs and this device report.";
            if (notAdded.Count > 0) { detail += Environment.NewLine + "Not added: " + string.Join(", ", notAdded) + "."; }
            _lastPackagePath = path;
            AddCard(ResultCard.From(FindingSeverity.Ok, "Support package saved to your Downloads folder", detail, "Give this zip to your IT support. File Explorer has opened with it selected."));
            AppendLog("Package: " + path);
            // Explorer with the file selected, so the user sees exactly which file to send.
            try { Process.Start(ExplorerPath, "/select,\"" + path + "\""); } catch (Win32Exception) { }
        }
        catch (PackageSaveException ex)
        {
            SetStatus("The support package was created, but could not be saved to your Downloads folder.", Tone.Warning);
            AddCard(ResultCard.From(FindingSeverity.Warning, "The support package could not be saved to your Downloads folder",
                ex.Message + Environment.NewLine + "It is still at " + ex.ReceivedPath,
                "Copy it from there, or free up space in Downloads and create the support package again."));
        }
    }

    private void OnReport(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        SetStatus("Building the device report...", Tone.Neutral);
        if (!await SaveReportAsync())
        {
            SetStatus("The device report could not be written.", Tone.Error);
            return;
        }
        var path = UserFiles.ReportFile();
        switch (OpenTextFile(path))
        {
            case TextFileOpened.InEditor:
                SetStatus("Report opened. Copy the text into a message to your IT support, or attach the file.", Tone.Success);
                break;
            case TextFileOpened.InFolder:
                SetStatus("No text editor could open the report, so its folder is open with the file selected. Attach it to a message to your IT support: " + path, Tone.Warning);
                break;
            default:
                SetStatus("The report could not be opened. It is saved at " + path, Tone.Error);
                break;
        }
    });

    private void OnHtmlReport(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        SetStatus("Building the device report...", Tone.Neutral);
        var path = await SaveHtmlReportAsync();
        if (path == null)
        {
            SetStatus("The report could not be saved to your Downloads folder.", Tone.Error);
            return;
        }
        var name = Path.GetFileName(path);
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            SetStatus("Report saved to your Downloads folder as " + name + " and opened. Give this file to your IT support.", Tone.Success);
            return;
        }
        catch (Win32Exception) { }
        // Nothing opens .html here: Explorer with the file selected, so the user sees which file to send.
        try { Process.Start(ExplorerPath, "/select,\"" + path + "\"")?.Dispose(); } catch (Win32Exception) { }
        SetStatus("Report saved to your Downloads folder as " + name + ". Give this file to your IT support.", Tone.Success);
    });

    /// <summary>
    /// Writes the device report as a web page to Downloads, under a new name each time. The health
    /// checks run again first, as for the text report, and then the read-only checks, as the support
    /// package collects its data, so the page always holds results of this moment.
    /// Returns the file's path, or null when it could not be written.
    /// </summary>
    private async Task<string> SaveHtmlReportAsync()
    {
        await RefreshHealthAsync(keepStatus: true);
        var (checks, appsWhy) = await RunReportChecksAsync();
        SetStatus("Building the device report...", Tone.Neutral);
        if (_page == Page.It) { ShowIt(); }
        var health = _health;
        var dsreg = _dsreg;
        var status = _status;
        var diagnosis = _diagnosis;
        var facts = CurrentFacts();
        return await Task.Run(() =>
        {
            try
            {
                var now = DateTimeOffset.Now;
                var html = HtmlReport.Build(DeviceFacts.Read(), dsreg, health, History.Tail(HtmlReport.HistoryLines), now, _version, status, TimeZoneInfo.Local, facts, diagnosis, checks, appsWhy);
                var path = UserFiles.FreeName(UserFiles.Downloads(), HtmlReport.FileName(now.LocalDateTime), ".html");
                File.WriteAllText(path, html, new UTF8Encoding(false));
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });
    }

    /// <summary>The number of read-only checks the HTML report runs first.</summary>
    internal const int ReportCheckCount = 5;

    /// <summary>
    /// The read-only checks the HTML report runs first: the app diagnosis, Check Windows Update and
    /// Check Defender blocks through the service (they count toward its hourly limit of read-only
    /// checks, as any run of them does), then Check Microsoft connections and Check compliance
    /// settings in the app. Each goes into the run history as a run of its own. A check the service
    /// does not run never stops the export: the page then shows its newest result from the run
    /// history and says why. A tool the organization switched off is not run.
    /// </summary>
    private async Task<(List<ReportCheck> Checks, string AppsNotRunBecause)> RunReportChecksAsync()
    {
        var checks = new List<ReportCheck>();
        var step = 0;
        void Say(string what) => SetStatus("Building the device report: " + what + " (" + (++step).ToString(CultureInfo.InvariantCulture) + " of "
            + ReportCheckCount.ToString(CultureInfo.InvariantCulture) + ")...", Tone.Neutral);

        Say("reading the work apps");
        var apps = await RunServiceForReportAsync(ToolbeltAction.DiagnoseApps);
        string appsWhy = apps.Why;
        if (appsWhy == null) { ApplyDiagnosis(apps.Outcome); }

        foreach (var action in new[] { ToolbeltAction.CheckWindowsUpdate, ToolbeltAction.CheckDefenderBlocks })
        {
            var tool = ToolCatalog.ByAction(action);
            var name = tool?.Name ?? ActionText.Name(action);
            Say(name);
            var (outcome, why) = await RunServiceForReportAsync(action);
            if (why == null)
            {
                var view = ResultPresenter.Final(outcome);
                checks.Add(ReportCheck.Ran(name, view.Status, view.Tone, view.Cards, outcome.StartedUtc ?? DateTime.UtcNow));
            }
            else
            {
                checks.Add(ReportCheck.Stored(name, why, why == ReportCheck.SwitchedOff ? null : NewestLog(tool)));
            }
        }

        Say(ConnectivityTest.Name);
        var connections = ToolCatalog.ByButton(nameof(ConnectivityButton));
        if (connections != null && !ToolCatalog.Offered(connections, _switchedOff)) { checks.Add(ReportCheck.Stored(ConnectivityTest.Name, ReportCheck.SwitchedOff, null)); }
        else
        {
            var run = LocalRun.Start(ConnectivityTest.Name, DateTime.UtcNow);
            var routes = ConnectionRoutes();
            Func<string, int, int, ProbeAnswer> connect = routes is IDirectRoute direct ? direct.Connect : (_, _, _) => ProbeAnswer.Failed(RouteOutcome.Unknown);
            var result = await Task.Run(() => ConnectivityTest.Run(connect, routes, run.RunId));
            var contact = ManagementContact.From(_status);
            var (status, tone, cards) = ConnectivityTest.Present(result, contact, TimeZoneInfo.Local);
            run.EndedUtc = DateTime.UtcNow;
            var lines = result.Lines.ToList();
            if (ConnectivityTest.ContactLine(run.RunId, contact) is string seen) { lines.Add(seen); }
            SaveToHistory(NewHeader(null, run.Snapshot()), lines, cards.ToList());
            History.Add(ConnectivityTest.HistoryName, ConnectivityTest.HistoryResult(result), HistoryOverride);
            checks.Add(ReportCheck.Ran(ConnectivityTest.Name, status, tone, cards, run.StartedUtc));
        }

        Say(CompliancePath.CheckName);
        var compliance = ToolCatalog.ByCheckName(CompliancePath.CheckName);
        if (compliance != null && !ToolCatalog.Offered(compliance, _switchedOff)) { checks.Add(ReportCheck.Stored(CompliancePath.CheckName, ReportCheck.SwitchedOff, null)); }
        else
        {
            var run = LocalRun.Start(CompliancePath.CheckName, DateTime.UtcNow);
            var outcome = CompliancePath.Check(CompliancePath.Rows(_health, _buttons.ServiceMissing ? null : _status), run.RunId, DateTime.UtcNow);
            run.EndedUtc = DateTime.UtcNow;
            SaveToHistory(NewHeader(null, run.Snapshot()), outcome.LogLines.ToList(), outcome.Cards.ToList());
            History.Add(CompliancePath.HistoryName, CompliancePath.HistoryResult(outcome, run.RunId), HistoryOverride);
            checks.Add(ReportCheck.Ran(CompliancePath.CheckName, outcome.Status, outcome.Tone, outcome.Cards, run.StartedUtc));
        }
        return (checks, appsWhy);
    }

    /// <summary>
    /// A read-only service check for the HTML report, kept in the run history as any run; and why it
    /// did not run (null when it did): switched off, no service, or the service's own answer.
    /// </summary>
    private async Task<(RunOutcome Outcome, string Why)> RunServiceForReportAsync(ToolbeltAction action)
    {
        var tool = ToolCatalog.ByAction(action);
        if (tool != null && !ToolCatalog.Offered(tool, _switchedOff)) { return (null, ReportCheck.SwitchedOff); }
        if (_buttons.ServiceMissing) { return (null, ReportCheck.WhyNot(null)); }
        var outcome = await Task.Run(() =>
        {
            var result = _gateway.Run(action, false, -1, null, out var file);
            ReceivedFile.Discard(file);
            return result;
        });
        if (ResultPresenter.BelongsInHistory(outcome))
        {
            History.Add(outcome.Action.ToString(), ResultPresenter.HistoryResult(outcome), HistoryOverride);
            var view = ResultPresenter.Final(outcome);
            SaveToHistory(NewHeader(outcome, null), view.LogLines.ToList(), view.Cards.ToList());
        }
        return (outcome, ReportCheck.WhyNot(outcome));
    }

    /// <summary>A tool's newest run log in the run history; null when it has none.</summary>
    private StoredRunLog NewestLog(ToolInfo tool)
    {
        if (tool == null) { return null; }
        List<StoredRunLog> logs;
        try { logs = RunLogStore.List(RunsFolderOverride ?? UserFiles.RunsFolder()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return logs.Where(l => tool.OwnsLogKey(l.Action)).OrderByDescending(l => l.StartedUtc ?? l.WrittenUtc).FirstOrDefault();
    }

    private enum TextFileOpened { InEditor, InFolder, No }

    /// <summary>
    /// Opens a text file in the user's editor for .txt, then in Notepad. When neither works (no
    /// .txt handler, and Notepad removed from the image or not yet set up for a new profile),
    /// shows the file selected in Explorer, so the user can still open or attach it.
    /// </summary>
    private static TextFileOpened OpenTextFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            return TextFileOpened.InEditor;
        }
        catch (Win32Exception) { }
        try
        {
            Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), "\"" + path + "\"")?.Dispose();
            return TextFileOpened.InEditor;
        }
        catch (Win32Exception) { }
        try
        {
            Process.Start(ExplorerPath, "/select,\"" + path + "\"")?.Dispose();
            return TextFileOpened.InFolder;
        }
        catch (Win32Exception)
        {
            return TextFileOpened.No;
        }
    }

    /// <summary>
    /// Writes the device report. The checks run again first: every line of a report must be of the
    /// moment the report says it was generated.
    /// </summary>
    private async Task<bool> SaveReportAsync()
    {
        await RefreshHealthAsync(keepStatus: true);
        var health = _health;
        var dsreg = _dsreg;
        var status = _status;
        // The first-line facts at the top of the report (and so of the support package).
        var facts = CurrentFacts();
        return await Task.Run(() =>
        {
            try
            {
                var text = DeviceReport.Build(DeviceFacts.Read(), dsreg, health, History.Tail(5), DateTimeOffset.Now, _version, status, TimeZoneInfo.Local, facts);
                File.WriteAllText(UserFiles.ReportFile(), text, new UTF8Encoding(true));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        });
    }

    // --- Navigation: Home, Choose a problem, For IT -------------------------------------

    /// <summary>The Home tab opens Home itself, also from its health view.</summary>
    private void OnNavHome(object sender, RoutedEventArgs e)
    {
        LeaveHealthView();
        ShowPage(Page.Home);
    }

    private void OnNavProblems(object sender, RoutedEventArgs e) => ShowPage(Page.Problems);

    /// <summary>The For IT entry opens For IT at its start: the tiles, without a search.</summary>
    private void OnNavIt(object sender, RoutedEventArgs e)
    {
        _itView = ItView.Tiles;
        ToolSearch.Text = "";
        ShowPage(Page.It);
    }

    /// <summary>
    /// Back on a path: the window and adapter pages go back to where they came from; a path
    /// goes back to the page it was opened from: Home (the focus on what opened it, or Home's title
    /// when that is gone), or Choose a problem (the focus on the path's row).
    /// </summary>
    private void OnPathBack(object sender, RoutedEventArgs e)
    {
        if (_page == Page.Apps && _appDetailView)
        {
            LeaveAppDetail();
            return;
        }
        if (_page == Page.Apps && _showAllApps)
        {
            LeaveAllApps();
            return;
        }
        if (_page == Page.Windows)
        {
            LeaveWindows();
            return;
        }
        if (_page == Page.Ip)
        {
            LeaveIp();
            return;
        }
        if (_pathFrom == Page.It)
        {
            // Show the apps on the Retry tool's page: back to that page, as the window and adapter pages go back to For IT.
            ShowPage(Page.It, focus: false);
            Dispatcher.BeginInvoke(new Action(() => ToolDetailTitle.Focus()), DispatcherPriority.Loaded);
            return;
        }
        if (_pathFrom == Page.Home)
        {
            var opener = _pathOpener;
            ShowPage(Page.Home, focus: false);
            // The element that opened the path, while it is still on Home and can take the focus; else Home's title.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (opener is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } element && IsOnHome(element)) { element.Focus(); }
                else { FocusTitle(); }
            }), DispatcherPriority.Loaded);
            return;
        }
        var from = _page == Page.NotListed ? ProblemNotListedButton : _page == Page.Apps ? ProblemAppButton : _problem switch
        {
            Problem.CantSignIn => ProblemSignInButton,
            Problem.WindowsWontUpdate => ProblemUpdateButton,
            Problem.OfficeCloses => ProblemOfficeButton,
            Problem.OneDriveSync => ProblemOneDriveButton,
            Problem.DeviceBlocked => ProblemBlockedButton,
            _ => ProblemConnectButton,
        };
        ShowPage(Page.Problems, focus: false);
        from.Focus();
    }

    // The page a path (the app path, a problem path, "My problem is not listed") was opened from,
    // Home, Choose a problem or For IT (Show the apps on the Retry tool's page), and on Home the element that opened it. Set once when a path opens
    // from a page that is no path; going on to the window or adapter page and back keeps it.
    private Page _pathFrom = Page.Problems;
    private UIElement _pathOpener;

    // Show the apps on the Retry tool's page is opening the app path: its origin is For IT then.
    private bool _appsFromRetryTool;
    private bool _awaitingOpener;

    /// <summary>A button's Click reached the window: when it just opened a path from Home, it is the element Back returns to.</summary>
    private void OnAnyButtonClick(object sender, RoutedEventArgs e)
    {
        if (!_awaitingOpener || !IsPathPage(_page)) { return; }
        _awaitingOpener = false;
        if (e.OriginalSource is UIElement button && IsOnHome(button)) { _pathOpener = button; }
    }

    private static bool IsPathPage(Page page) => page is Page.Apps or Page.Problem or Page.NotListed;

    private bool IsOnHome(DependencyObject element)
    {
        for (var current = element; current != null; current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, HomePage)) { return true; }
        }
        return false;
    }

    /// <summary>
    /// Shows one page below the three entries; the status stays below every page. The entry of the
    /// page has the "Selected" look and item status, and the keyboard focus moves to the page's
    /// first control (unless the caller places it).
    /// </summary>
    private void ShowPage(Page page, bool focus = true)
    {
        // A page that is no path ends the wait for the click that opened one.
        if (!IsPathPage(page)) { _awaitingOpener = false; }
        // A path opened from a page that is no path (a tab's page) remembers where it came from.
        if (IsPathPage(page) && !IsPathPage(_page) && _page is not (Page.Windows or Page.Ip))
        {
            _pathFrom = _page == Page.Home ? Page.Home : _page == Page.It && _appsFromRetryTool ? Page.It : Page.Problems;
            // The focused element for now; the button whose click opened the path replaces it when its
            // Click reaches the window (also when it was pressed by an access key or UI Automation).
            _pathOpener = _page == Page.Home ? Keyboard.FocusedElement as UIElement : null;
            _awaitingOpener = _page == Page.Home;
        }
        _page = page;
        HomePage.Visibility = page == Page.Home ? Visibility.Visible : Visibility.Collapsed;
        ProblemsPage.Visibility = page == Page.Problems ? Visibility.Visible : Visibility.Collapsed;
        ProblemHeader.Visibility = page is Page.Apps or Page.Problem or Page.Windows or Page.Ip or Page.NotListed ? Visibility.Visible : Visibility.Collapsed;
        AppsPage.Visibility = page == Page.Apps ? Visibility.Visible : Visibility.Collapsed;
        if (page != Page.Apps)
        {
            _showAllApps = false;
            _appSearchOpen = false;
            _appDetailView = false;
            _appChecksOpen = false;
        }
        ProblemPage.Visibility = page == Page.Problem ? Visibility.Visible : Visibility.Collapsed;
        NotListedPage.Visibility = page == Page.NotListed ? Visibility.Visible : Visibility.Collapsed;
        // Copy details for IT on every problem path (not on the window and adapter pages, whose tools have their
        // own); "My problem is not listed" has it as a button among its steps instead.
        PathCopyTicketButton.Visibility = page is Page.Apps or Page.Problem ? Visibility.Visible : Visibility.Collapsed;
        if (page is Page.Apps or Page.Problem) { AutomationProperties.SetName(PathCopyTicketButton, "Copy details for IT: " + PathTitle()); }
        WindowsPage.Visibility = page == Page.Windows ? Visibility.Visible : Visibility.Collapsed;
        IpPage.Visibility = page == Page.Ip ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(BackButton, (page == Page.Windows && _windowsFrom == Page.It) || (page == Page.Ip && _ipFrom == Page.It) ? "Back to For IT support"
            : page == Page.Ip ? "Back to " + ProblemPaths.Get(Problem.CantConnect).Title
            : IsPathPage(page) && _pathFrom == Page.Home ? "Back to Home"
            : IsPathPage(page) && _pathFrom == Page.It ? "Back to For IT support" : "Back to Choose a problem");
        ItPage.Visibility = page == Page.It ? Visibility.Visible : Visibility.Collapsed;
        // The Office row shows only when a supported Office app is installed (read again each time).
        if (page == Page.Problems) { ProblemOfficeButton.Visibility = AnyOfficeApp() ? Visibility.Visible : Visibility.Collapsed; }
        // A tool's result stays with the page it ran on; it is not shown on another page.
        ShowStatusForPage();
        // A path opened from Home stays under Home's tab, as a page opened from For IT stays under For IT.
        var entry = page switch
        {
            Page.Home => HomeNav,
            Page.It => ItNav,
            Page.Windows when _windowsFrom == Page.It => ItNav,
            Page.Ip when _ipFrom == Page.It => ItNav,
            Page.Apps or Page.Problem or Page.NotListed or Page.Ip when _pathFrom == Page.Home => HomeNav,
            Page.Apps or Page.Problem or Page.NotListed when _pathFrom == Page.It => ItNav,
            _ => ProblemsNav,
        };
        foreach (var nav in new[] { HomeNav, ProblemsNav, ItNav })
        {
            var selected = ReferenceEquals(nav, entry);
            nav.Tag = selected ? "Selected" : null;
            AutomationProperties.SetItemStatus(nav, selected ? "Selected" : "");
        }
        if (page == Page.It) { ShowIt(); } else { PlaceStatus(); }
        ApplyContentWidth();
        ApplyHelpers();
        if (!focus) { return; }
        UIElement target = page switch
        {
            Page.Home => MainFindingTitle,
            Page.Problems => ProblemAppButton,
            Page.Apps or Page.Problem or Page.Windows or Page.Ip or Page.NotListed => BackButton,
            // The search is always at the top of For IT.
            _ => ToolSearch,
        };
        Dispatcher.BeginInvoke(new Action(() => { if (ReferenceEquals(target, MainFindingTitle)) { FocusTitle(); } else { target.Focus(); } }), DispatcherPriority.Loaded);
    }

    // The last input in this window was a key (keyboard cues are active); a mouse, pen or touch press ends it.
    private bool _keyboardCues;

    /// <summary>The last input in this window was a key; a dialog it opens shows its focus ring only then.</summary>
    internal bool KeyboardCues => _keyboardCues;

    // The focus rings hidden since the last key press, with the local value each had (unset: its style's).
    private readonly Dictionary<FrameworkElement, object> _quietFocus = new();

    /// <summary>
    /// The element keeps the keyboard focus but draws no focus ring until the next key press;
    /// used for every focus that does not come from a key (the status card's heading has its own rule).
    /// </summary>
    internal void QuietFocusVisual(FrameworkElement element)
    {
        if (element == null || ReferenceEquals(element, MainFindingTitle) || element.FocusVisualStyle == null) { return; }
        if (!_quietFocus.ContainsKey(element)) { _quietFocus[element] = element.ReadLocalValue(FocusVisualStyleProperty); }
        element.FocusVisualStyle = null;
    }

    /// <summary>A key was pressed: every ring hidden by <see cref="QuietFocusVisual"/> is back.</summary>
    internal void RestoreFocusVisuals()
    {
        foreach (var element in _quietFocus.Keys.ToList()) { RestoreFocusVisual(element); }
    }

    /// <summary>One element's ring is back (it lost the focus, or a key was pressed).</summary>
    private void RestoreFocusVisual(FrameworkElement element)
    {
        if (!_quietFocus.TryGetValue(element, out var value)) { return; }
        if (value == DependencyProperty.UnsetValue) { element.ClearValue(FocusVisualStyleProperty); }
        else { element.SetValue(FocusVisualStyleProperty, value); }
        _quietFocus.Remove(element);
    }

    /// <summary>
    /// A key that moves or acts (Tab, arrows, Enter, Space, a letter, an access key with Alt), not a
    /// modifier pressed alone or Print Screen: a screenshot shortcut (Win+Shift+S) must not turn the
    /// focus rings on for a mouse user.
    /// </summary>
    internal static bool IsNavigationKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.Snapshot or Key.None);
    }

    /// <summary>Any key in this window: the keyboard cues are on and the hidden rings are back.</summary>
    internal void OnKeyInput()
    {
        _keyboardCues = true;
        RestoreFocusVisuals();
    }

    /// <summary>A dialog this window opened closed; its last input decides the cues here.</summary>
    internal void SetKeyboardCues(bool keyboard)
    {
        if (keyboard) { OnKeyInput(); }
        else { _keyboardCues = false; }
    }

    /// <summary>The number of elements whose ring is hidden now (for tests).</summary>
    internal int QuietFocusCount => _quietFocus.Count;

    /// <summary>
    /// The status card's heading gets the keyboard focus for screen readers (on open and on Home),
    /// but its focus box is drawn only while keyboard cues are active: on open or after a click it looked
    /// like an input field to mouse users.
    /// </summary>
    private void FocusTitle()
    {
        // While the health view shows, the title is hidden; its Back is the top of the page.
        if (_showAllHealth && _page == Page.Home)
        {
            HealthBackButton.Focus();
            return;
        }
        MainFindingTitle.FocusVisualStyle = _keyboardCues ? (Style)FindResource("FocusOutline") : null;
        MainFindingTitle.Focus();
    }

    // --- Home ---------------------------------------------------------------------------

    private HomeFinding _home;

    /// <summary>
    /// Home: the one main finding, from the same health rows and app diagnosis the other pages show,
    /// as the page's title (in plain words, without technical words; the rows and For IT
    /// support keep them): its severity written out, one short line, when the checks last ran, and
    /// the mascot's mood.
    /// </summary>
    private void ShowHome()
    {
        _home = HomeSummary.Build(_healthRows, _diagnosis, _buttons.ServiceMissing, _switchedOff);
        var checking = _checkingHealth && !_buttons.ServiceMissing && (_home.App == null || _healthRows.Count == 0);
        var title = checking ? "Checking this device..." : HomeSummary.PlainTitle(_home);
        var severity = checking ? "" : HomeSummary.SeverityWord(_home);
        MainFindingTitle.Text = title;
        MainFindingTitle.Foreground = ThemeBrush.For(checking ? Tone.Neutral : _home.Tone);
        // A screen reader hears the severity with the finding; the colour never says it alone.
        AutomationProperties.SetName(MainFindingTitle, severity.Length == 0 ? title : (_home.Tone == Tone.Error ? "Problem: " : "Needs attention: ") + title);
        SeverityWord.Text = severity;
        SeverityWord.Foreground = ThemeBrush.For(_home.Tone);
        SeverityWord.Visibility = severity.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        // One short line under the verdict: the first sentence; the whole text is its tooltip and help text.
        // In plain words; the finding's own text (which may name IME, MDM or a code) stays the tooltip.
        var full = checking ? "This takes a few seconds; nothing is changed." : _home.Text ?? "";
        var text = checking ? full : HomeSummary.PlainText(_home);
        MainFindingText.Text = text;
        MainFindingText.ToolTip = full != text && full.Length > 0 ? full : null;
        AutomationProperties.SetHelpText(MainFindingText, full != text ? full : "");
        MainFindingText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        LastCheckedText.Text = _healthCheckedUtc is DateTime checkedUtc ? "Last checked " + ToolRuns.ShortWhen(checkedUtc, TimeZoneInfo.Local, DateTime.UtcNow) : "";
        LastCheckedText.Visibility = LastCheckedText.Text.Length == 0 || checking ? Visibility.Collapsed : Visibility.Visible;
        ApplyHelpers();
        PrimaryStepButton.Content = _home.StepText;
        // The button says the step; its name also says which finding, so it never shares a name with
        // the button or link it stands for (Copy details for IT under the status, Retry on the app path).
        AutomationProperties.SetName(PrimaryStepButton, StepName(_home.StepText, _home.Title));
        // A step in plain words names its technical name in its help text.
        var stepHelp = _home.StepText == Words.RetryStep ? Words.RetryStepHelp : null;
        AutomationProperties.SetHelpText(PrimaryStepButton, stepHelp ?? "");
        PrimaryStepButton.ToolTip = stepHelp;
        PrimaryStepButton.Visibility = _home.StepText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyHomeButtons(checking);
        RefreshStepButtons();
        ShowUptime();
    }

    // --- First-line facts, Copy details for IT on Home and the paths, the uptime signal ---------------

    /// <summary>When the device last started, as Windows says; null when it cannot be read.</summary>
    private DateTime? SafeBoot()
    {
        try { return BootClock?.Invoke(); }
        catch (Exception ex) when (ex is InvalidOperationException or EntryPointNotFoundException or DllNotFoundException) { return null; }
    }

    /// <summary>The Pending restart row says a restart is waiting (Windows servicing or a repair of this app).</summary>
    private bool RestartPending() => _healthRows.Any(r => r.Name == HealthChecks.PendingRestart && r.Status != HealthStatus.Pass && !r.ServiceUnavailable && !r.NotRead);

    /// <summary>
    /// Home's uptime line ("Restarted 12 days ago"); after <see cref="Uptime.LongUptime"/> or longer,
    /// or while a restart is pending, it says so in the warning tone ("Restarted 12 days ago · a
    /// restart is waiting"). A signal only: the app never restarts the device.
    /// </summary>
    private void ShowUptime()
    {
        var boot = SafeBoot();
        var now = DateTime.UtcNow;
        var line = Uptime.HomeLine(boot, now, RestartPending());
        UptimeText.Text = line;
        UptimeLine.Visibility = line.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var signal = Uptime.Signal(boot, now, RestartPending()).Length > 0;
        if (signal) { UptimeText.Foreground = ThemeBrush.For(Tone.Warning); }
        else { UptimeText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted"); }
    }

    /// <summary>The first-line facts now: the boot time, the join type and enrollment ID last read, the network of the last check, and whether a restart is waiting.</summary>
    private FirstLineFacts CurrentFacts() => FirstLineFacts.From(SafeBoot(), _dsreg, _buttons.ServiceMissing ? null : _status, _network, RestartPending());

    /// <summary>The facts block of Copy details for IT, with the health checks in one line.</summary>
    private List<string> TicketFacts() => CurrentFacts().TicketLines(_version, _healthRows, DateTime.UtcNow, TimeZoneInfo.Local);

    /// <summary>The ticket text Home's Copy details for IT copies for a health finding (or for no finding).</summary>
    internal string HomeTicketText()
    {
        var header = NewHeader(null, null);
        var cards = HealthChecks.Cards(_healthRows);
        var home = _home ?? HomeSummary.Build(_healthRows, _diagnosis, _buttons.ServiceMissing, _switchedOff);
        var status = string.IsNullOrEmpty(home.Text) ? home.Title : home.Title + ". " + HealthResult.FirstSentence(home.Text);
        return RunTicket.Page(header, "Home", cards, status, _healthCheckedUtc, TicketFacts());
    }

    /// <summary>
    /// The ticket of "My problem is not listed": the first-line facts and the health
    /// checks, nothing run, and an outcome that says the user's own words follow.
    /// </summary>
    internal string NotListedTicketText()
    {
        var home = _home ?? HomeSummary.Build(_healthRows, _diagnosis, _buttons.ServiceMissing, _switchedOff);
        return RunTicket.Page(NewHeader(null, null), Handover.NotListedTitle, HealthChecks.Cards(_healthRows), Handover.NotListedOutcome + home.Title + ".", _healthCheckedUtc, TicketFacts());
    }

    /// <summary>The row "My problem is not listed" opens the handover page.</summary>
    private void OnProblemNotListed(object sender, RoutedEventArgs e)
    {
        ProblemTitle.Text = Handover.NotListedTitle;
        NotListedIntro.Text = Handover.NotListedIntro;
        NotListedAsk.Text = Handover.NotListedAsk;
        NotListedCopyText.Text = Handover.NotListedCopyText;
        NotListedPackageText.Text = Handover.NotListedPackageText;
        NotListedItText.Text = Handover.NotListedItText;
        AutomationProperties.SetName(NotListedCopyButton, "Copy details for IT: " + Handover.NotListedTitle);
        ShowSupportContact();
        ShowPage(Page.NotListed);
    }

    /// <summary>Copy details for IT on "My problem is not listed": the page's ticket, with this button's feedback.</summary>
    private void OnNotListedCopy(object sender, RoutedEventArgs e)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, NotListedTicketText());
        PutOnClipboard(data, NotListedCopyButton, "Copy details for IT", "Copy details for IT: " + Handover.NotListedTitle, "Details for IT copied", "The ticket text could not be copied: another program is using the clipboard. Try again.");
    }

    /// <summary>
    /// Copy details for IT on Home: an app finding (or the service being away) copies the same ticket as
    /// its step; a health finding, or none, copies Home's own ticket with the health rows.
    /// </summary>
    private void OnHomeCopyTicket(object sender, RoutedEventArgs e)
    {
        if (_home?.App != null || _buttons.ServiceMissing)
        {
            var finding = _home?.App;
            if (finding == null)
            {
                SetDiagnosis(AppDecisions.From(null, DateTime.UtcNow), null);
                finding = _diagnosis.Problem;
            }
            CopyFindingTicket(finding, HomeCopyTicketButton);
            return;
        }
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, HomeTicketText());
        PutOnClipboard(data, HomeCopyTicketButton, "Copy details for IT", "Copy details for IT: Home", "Details for IT copied", "The ticket text could not be copied: another program is using the clipboard. Try again.");
    }

    /// <summary>The problem path shown, whose case file a run that ends now joins; null on Home, For IT and the other pages.</summary>
    private Problem? ShownPath() => _page == Page.Apps ? Problem.AppWontInstall : _page == Page.Problem ? _problem : (Problem?)null;

    /// <summary>The Tried lines of a path's case file, now.</summary>
    private List<string> TriedLines(Problem? path) => _caseFile.TriedLines(path, TimeZoneInfo.Local);

    /// <summary>The problem path shown: the app path, or one of the others (Office included).</summary>
    private string PathTitle() => _page == Page.NotListed ? Handover.NotListedTitle : _page == Page.Apps ? ProblemPaths.Get(Problem.AppWontInstall).Title : ProblemPaths.Get(_problem).Title;

    /// <summary>
    /// The ticket text a problem path's Copy details for IT copies: on the app path after a diagnosis,
    /// the diagnosis's own ticket (its reference, every app finding); otherwise the path's checks and
    /// verdict. Both with the case file's Tried lines: what ran on this path in this session.
    /// </summary>
    internal string PathTicketText()
    {
        if (_page == Page.NotListed) { return NotListedTicketText(); }
        if (_page == Page.Apps && _diagnosis != null && (_diagnosisRun != null || _diagnosisCheck != null))
        {
            var (status, tone) = AppDecisions.Status(_diagnosis);
            return RunTicket.Text(NewHeader(_diagnosisRun, _diagnosisCheck?.Snapshot()), _diagnosis.Cards(), status, tone, facts: TicketFacts(), tried: TriedLines(Problem.AppWontInstall));
        }
        var path = _page == Page.Apps ? ProblemPaths.Get(Problem.AppWontInstall) : ProblemPaths.Get(_problem);
        var tried = TriedLines(path.Problem);
        if (path.Problem == Problem.DeviceBlocked)
        {
            // One card per item that is not OK, with its step for the user or for IT, and what
            // this device cannot know (the verdict and the sign-in block's reason).
            var mine = PathRows(path);
            var cards = CompliancePath.Cards(mine);
            cards.Add(CompliancePath.NotKnownCard());
            return RunTicket.Page(NewHeader(null, null), path.Title, cards, ProblemPaths.Verdict(path, mine).Text, _healthCheckedUtc, TicketFacts(), tried);
        }
        var rows = _page == Page.Apps
            ? _healthRows.Where(r => ProblemPaths.InstallBlockers.Contains(r.Name) || path.Rows.Contains(r.Name)).ToList()
            : _healthRows.Where(r => path.Rows.Contains(r.Name)).ToList();
        var verdict = path.Rows.Count == 0 ? "No health checks belong to this problem; its tools are on the page." : ProblemPaths.Verdict(path, _healthRows, _wuPathCheck).Text;
        return RunTicket.Page(NewHeader(null, null), path.Title, HealthChecks.Cards(rows), verdict, _healthCheckedUtc, TicketFacts(), tried);
    }

    /// <summary>
    /// The rows a path looks at: the window's health rows, or the compliance path's own rows, one
    /// per item, from the detailed checks and the service's readouts.
    /// </summary>
    private List<HealthResult> PathRows(ProblemPath path)
        => path.Problem == Problem.DeviceBlocked ? CompliancePath.Rows(_health, _buttons.ServiceMissing ? null : _status) : _healthRows;

    private void OnPathCopyTicket(object sender, RoutedEventArgs e)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, PathTicketText());
        PutOnClipboard(data, PathCopyTicketButton, "Copy details for IT", "Copy details for IT: " + PathTitle(), "Details for IT copied", "The ticket text could not be copied: another program is using the clipboard. Try again.");
    }

    /// <summary>
    /// The automation name of a step button: the step and the finding it is for ("Copy details for IT:
    /// Contoso Agent: installed, but not detected"), so it starts with the text the button shows.
    /// </summary>
    internal static string StepName(string step, string finding)
        => string.IsNullOrEmpty(step) ? "" : string.IsNullOrWhiteSpace(finding) ? step : step + ": " + finding.Trim();

    /// <summary>The primary step of Home: the same handler as the button or link it names.</summary>
    private void OnPrimaryStep(object sender, RoutedEventArgs e)
    {
        if (_home == null) { return; }
        if (_home.Step == HomeStep.HealthFix)
        {
            RaiseFix(_home.Fix);
            return;
        }
        if (_home.AppStep == AppNextStep.ChooseApp)
        {
            ShowApps(focusList: true);
            return;
        }
        var finding = _home.App;
        var step = _home.AppStep;
        if (finding == null && _buttons.ServiceMissing)
        {
            // The service is missing: the handover names this attempt with a reference of its own.
            SetDiagnosis(AppDecisions.From(null, DateTime.UtcNow), null);
            finding = _diagnosis.Problem;
        }
        DoAppStep(finding, step, PrimaryStepButton);
    }

    /// <summary>The card's link: Review affected apps opens the app path; View failed settings opens the profiles row.</summary>
    private void OnReviewApps(object sender, RoutedEventArgs e)
    {
        if (_home?.Link == HomeLink.ViewFailedSettings) { OpenHealthRow(HealthChecks.Profiles); return; }
        OpenAppPath();
    }

    /// <summary>
    /// The status card's buttons fit its finding: the link beside Check again is the one that fits
    /// (Review affected apps, View failed settings, or none), and where Retry work apps and settings is
    /// unlikely to help (configuration profiles) Check again is the primary button and Retry a link after it.
    /// </summary>
    private void ApplyHomeButtons(bool checking)
    {
        var link = checking ? HomeLink.CheckApps : _home.Link;
        ReviewAppsLink.Visibility = link == HomeLink.None ? Visibility.Collapsed : Visibility.Visible;
        var settings = link == HomeLink.ViewFailedSettings;
        ReviewAppsLink.Content = settings ? "View failed settings" : link == HomeLink.CheckApps ? "Check my _apps" : "Review affected _apps";
        AutomationProperties.SetName(ReviewAppsLink, settings ? "View failed settings: Configuration profiles" : link == HomeLink.CheckApps ? "Check my apps" : "Review affected apps");
        AutomationProperties.SetHelpText(ReviewAppsLink, settings
            ? "Opens the Configuration profiles check with the settings that failed. Nothing on this device is changed."
            : "Checks the apps on this device without changing anything, and shows each one's next step.");
        var stepPrimary = checking || _home.StepIsPrimary || _home.StepText.Length == 0;
        PrimaryStepButton.Style = (Style)FindResource(stepPrimary ? "PrimaryButton" : "LinkButton");
        PrimaryStepButton.VerticalAlignment = stepPrimary ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        PrimaryStepButton.Margin = stepPrimary ? new Thickness(0, 0, 8, 8) : new Thickness(4, 0, 0, 8);
        RefreshButton.Style = (Style)FindResource(stepPrimary ? "SecondaryButton" : "PrimaryButton");
        // Two links side by side (Retry, then View failed settings) need a gap a reader sees, as the links of a list have.
        ReviewAppsLink.Margin = new Thickness(stepPrimary ? 4 : 16, 0, 0, 8);
        // The primary button first, then the links.
        if (PrimaryStepButton.Parent is Panel panel)
        {
            var want = stepPrimary ? 0 : 1;
            if (panel.Children.IndexOf(PrimaryStepButton) != want)
            {
                panel.Children.Remove(PrimaryStepButton);
                panel.Children.Insert(want, PrimaryStepButton);
            }
        }
    }

    /// <summary>Opens one health row on Home (all rows shown when it is among the OK ones) and gives it the keyboard focus.</summary>
    private void OpenHealthRow(string name)
    {
        if (!HealthView.Visible(_healthRows, _showAllHealth, HealthView.HomeAttentionRows).Any(r => r.Name == name))
        {
            _showAllHealth = true;
            ShowHealth();
        }
        if (HealthList.ItemsSource is not IEnumerable<HealthRow> rows || rows.FirstOrDefault(r => r.Key == name) is not HealthRow row) { return; }
        if (!row.IsOpen) { row.Toggle(); }
        HealthList.UpdateLayout();
        if (HealthList.ItemContainerGenerator.ContainerFromItem(row) is DependencyObject container)
        {
            var target = FindChild<AccessibleRow>(container);
            target?.Focus();
            target?.BringIntoView();
        }
    }

    private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) { return found; }
            if (FindChild<T>(child) is T deeper) { return deeper; }
        }
        return null;
    }

    // --- Choose a problem ---------------------------------------------------------------

    private void OnProblemApp(object sender, RoutedEventArgs e) => OpenAppPath();

    private void OnProblemSignIn(object sender, RoutedEventArgs e) => ShowProblem(Problem.CantSignIn);

    private void OnProblemUpdate(object sender, RoutedEventArgs e) => ShowProblem(Problem.WindowsWontUpdate);

    private void OnProblemConnect(object sender, RoutedEventArgs e) => ShowProblem(Problem.CantConnect);

    private void OnProblemBlocked(object sender, RoutedEventArgs e) => ShowProblem(Problem.DeviceBlocked);

    // --- An Office app closes right after it opens: start it in safe mode ------------------

    private void OnProblemOffice(object sender, RoutedEventArgs e) => ShowProblem(Problem.OfficeCloses);

    /// <summary>The app is installed, as Windows finds it now; a registry that cannot be read counts as not installed.</summary>
    private bool OfficeInstalled(OfficeApp app)
    {
        try { return OfficeApps.Find(app) != null; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { return false; }
    }

    private bool AnyOfficeApp() => OfficeSafeMode.Apps.Any(OfficeInstalled);

    /// <summary>OneDrive is installed, as the documented places say now; a failure to look counts as not installed.</summary>
    private bool OneDriveInstalled()
    {
        try { return OneDrive.Find() != null; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { return false; }
    }

    /// <summary>
    /// Whether a problem path shows this tool now: a safe-mode tool only when its app is installed,
    /// Get a new IP address only while an adapter needs a new address; every other tool always (also
    /// Reset OneDrive, which then says that OneDrive is not installed).
    /// </summary>
    private bool ToolOffered(string button)
    {
        var tool = ToolCatalog.ByButton(button);
        if (tool?.Office is OfficeApp app) { return OfficeInstalled(app); }
        if (button == nameof(RenewIpButton)) { return AnyAdapterNeedsAddress(); }
        return true;
    }

    private void OnWordSafe(object sender, RoutedEventArgs e) => StartSafeMode(OfficeApp.Word);

    private void OnExcelSafe(object sender, RoutedEventArgs e) => StartSafeMode(OfficeApp.Excel);

    private void OnOutlookSafe(object sender, RoutedEventArgs e) => StartSafeMode(OfficeApp.Outlook);

    private void OnPowerPointSafe(object sender, RoutedEventArgs e) => StartSafeMode(OfficeApp.PowerPoint);

    // The row's Run button that started the tool last (its text says "Starting Word..." too).
    private Button _pressedRowRun;

    /// <summary>
    /// Starts an Office app with /safe in the user's own context. While it starts, its button
    /// says "Starting Word…" and a second click is blocked for a moment; then the button has its own
    /// text back. An app that is already open is not started a second time: the user is asked to close
    /// it normally first, and it is never closed for them. Only a start from the problem path asks the
    /// app's question; a start from For IT asks nothing ("Start requested").
    /// </summary>
    private void StartSafeMode(OfficeApp app) => Guarded(async () =>
    {
        var name = OfficeSafeMode.AppName(app);
        var apps = OfficeApps;
        var asks = _page == Page.Problem && _problem == Problem.OfficeCloses;
        var buttons = new List<Button> { (Button)FindName(OfficeSafeMode.Button(app)) };
        if (_pressedRowRun != null && _pressedRowRun.IsVisible) { buttons.Add(_pressedRowRun); }
        _pressedRowRun = null;
        var texts = buttons.Select(b => b.Content).ToList();
        foreach (var button in buttons) { button.Content = OfficeSafeMode.StartingText(app); }
        try
        {
            var clock = Stopwatch.StartNew();
            ClearResult();
            StartCheck(OfficeSafeMode.ToolName(app));
            SetStatus(OfficeSafeMode.StartingText(app), Tone.Neutral);
            bool AskToClose() => Dispatcher.Invoke(() => MessageDialog.Confirm(this, OfficeSafeMode.ToolName(app),
                name + " is open. Save your work and close " + name + " normally, then choose Start in safe mode. " + ProductInfo.DisplayName + " never closes it for you.",
                null, "_Start in safe mode"));
            var outcome = await Task.Run(() => OfficeSafeMode.Start(apps, app, AskToClose, asks: asks));
            // A second click stays blocked a moment after Windows accepted the start.
            if (outcome.Result == SafeModeResult.StartRequested && clock.Elapsed < StartingHold) { await Task.Delay(StartingHold - clock.Elapsed); }
            ShowSafeModeOutcome(outcome);
        }
        finally
        {
            for (var i = 0; i < buttons.Count; i++) { buttons[i].Content = texts[i]; }
        }
        await RefreshAfterToolAsync(ToolCatalog.ByButton(OfficeSafeMode.Button(app)));
    });

    /// <summary>
    /// A safe-mode start's result: status, card, run log line and history (the start and its
    /// answer are one run: the run log and the history line of that run are written again); the
    /// app's question while it awaits an answer.
    /// </summary>
    private void ShowSafeModeOutcome(SafeModeOutcome outcome)
    {
        var runId = _shownCheck?.RunId ?? "";
        var (status, tone, card) = OfficeSafeMode.Present(outcome);
        SetStatus(status, tone);
        _cards = new List<ResultCard> { card };
        ShowCards();
        AppendLog(OfficeSafeMode.LogLine(outcome, runId));
        History.Put(OfficeSafeMode.HistoryName(outcome.App), OfficeSafeMode.HistoryResult(outcome, runId), runId, HistoryOverride);
        if (_shownCheck != null) { _shownCheck.Outcome = OfficeSafeMode.OutcomeWords(outcome.Result); }
        _safeMode = outcome.AwaitsAnswer ? outcome : null;
        SafeModeQuestion.Visibility = outcome.AwaitsAnswer ? Visibility.Visible : Visibility.Collapsed;
        SafeModeQuestionText.Text = OfficeSafeMode.Question(outcome.App);
        AutomationProperties.SetName(SafeModeQuestionText, OfficeSafeMode.AppName(outcome.App) + ": " + OfficeSafeMode.Question(outcome.App));
        var (yes, no, notTried) = OfficeSafeMode.AnswerNames(outcome.App);
        AutomationProperties.SetName(SafeYesButton, yes);
        AutomationProperties.SetName(SafeNoButton, no);
        AutomationProperties.SetName(SafeNotTriedButton, notTried);
        _focusResultAtEnd = !outcome.AwaitsAnswer;
        FinishCheck();
    }

    private void OnSafeModeYes(object sender, RoutedEventArgs e) => AnswerSafeMode(SafeModeAnswer.Yes);

    private void OnSafeModeNo(object sender, RoutedEventArgs e) => AnswerSafeMode(SafeModeAnswer.No);

    private void OnSafeModeNotTried(object sender, RoutedEventArgs e) => AnswerSafeMode(SafeModeAnswer.NotTried);

    /// <summary>The answer to the app's question: the run's result, kept in the same run log and history line.</summary>
    private void AnswerSafeMode(SafeModeAnswer answer)
    {
        if (_safeMode == null || _shownCheck == null || _busy) { return; }
        var outcome = OfficeSafeMode.Answer(_safeMode, answer);
        ShowSafeModeOutcome(outcome);
        // The question is gone: the keyboard focus goes to the result (open drawer) or to View details.
        Dispatcher.BeginInvoke(new Action(() => { if (!_drawerOpen || !FocusResult()) { DetailsButton.Focus(); } }), DispatcherPriority.Loaded);
    }

    // --- OneDrive doesn't sync: Reset OneDrive ------------------------------------------------

    private void OnProblemOneDrive(object sender, RoutedEventArgs e) => ShowProblem(Problem.OneDriveSync);

    /// <summary>
    /// Reset OneDrive: Microsoft's documented <c>onedrive.exe /reset</c>, as the user, after a
    /// confirmation in plain words whose default is Cancel; then OneDrive is started again when it did
    /// not start by itself within a few minutes. The result is "Reset requested", never that files sync.
    /// </summary>
    private void OnResetOneDrive(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        var oneDrive = OneDrive;
        if (!OneDriveInstalled())
        {
            ClearResult();
            StartCheck(OneDriveReset.ToolName);
            ShowOneDriveOutcome(new OneDriveResetOutcome { Result = OneDriveResetResult.NotInstalled, StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow });
            return;
        }
        if (!ConfirmOneDriveReset()) { return; }
        ClearResult();
        StartCheck(OneDriveReset.ToolName);
        SetStatus("Resetting OneDrive...", Tone.Neutral);
        void Waiting() => Dispatcher.Invoke(new Action(() => SetStatus("OneDrive was reset. Waiting for it to start again (up to " + OneDriveReset.RestartWait.TotalMinutes.ToString(CultureInfo.InvariantCulture) + " minutes)...", Tone.Neutral)));
        var outcome = await Task.Run(() => OneDriveReset.Run(oneDrive, waiting: Waiting));
        ShowOneDriveOutcome(outcome);
        await RefreshAfterToolAsync(ToolCatalog.ByButton(nameof(ResetOneDriveButton)));
    });

    /// <summary>The confirmation before the reset; Cancel is its default. A test replaces it.</summary>
    internal Func<bool> ConfirmOneDriveReset
    {
        get => _confirmOneDriveReset ?? (() => MessageDialog.Confirm(this, OneDriveReset.ToolName, OneDriveReset.ConfirmText, NetworkWarning(ResetTool.OneDrive), "_Reset OneDrive", destructive: true));
        set => _confirmOneDriveReset = value;
    }

    private Func<bool> _confirmOneDriveReset;

    /// <summary>The reset's result: status, card, run log line and history; no account, path or file name.</summary>
    private void ShowOneDriveOutcome(OneDriveResetOutcome outcome)
    {
        var runId = _shownCheck?.RunId ?? "";
        var (status, tone, card) = OneDriveReset.Present(outcome);
        SetStatus(status, tone);
        _cards = new List<ResultCard> { card };
        ShowCards();
        AppendLog(OneDriveReset.LogLine(outcome, runId));
        History.Add(OneDriveReset.HistoryName, OneDriveReset.HistoryResult(outcome, runId), HistoryOverride);
        if (_shownCheck != null) { _shownCheck.Outcome = OneDriveReset.OutcomeWords(outcome.Result); }
        _focusResultAtEnd = true;
        FinishCheck();
    }

    // --- I can't see an open window: Bring my window back ------------------------------------

    private void OnProblemWindow(object sender, RoutedEventArgs e) => OpenWindows(Page.Problems);

    /// <summary>The tool's own button (For IT, its row and its detail page): opens the same page.</summary>
    private void OnWindowBackTool(object sender, RoutedEventArgs e) => OpenWindows(_page == Page.It ? Page.It : Page.Problems);

    /// <summary>
    /// The window page: the user's open app windows to choose from. Nothing is moved until the user
    /// presses Bring it back; with no window to list, the result says so (and is kept like a run).
    /// </summary>
    private void OpenWindows(Page from)
    {
        if (_page != Page.Windows) { _windowsFrom = from; }
        ProblemTitle.Text = WindowRescue.ProblemTitle;
        _chosenWindow = null;
        _showAllWindows = false;
        LoadWindows();
        ShowPage(Page.Windows);
        if (_windows.Count == 0) { ShowNoWindows(); }
    }

    /// <summary>Reads the windows again; the chosen one stays chosen while it is still open.</summary>
    private void LoadWindows()
    {
        try { _windows = WindowRescue.List(WindowApi); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            App.LogError(ex);
            _windows = new List<AppWindow>();
        }
        if (_chosenWindow != null) { _chosenWindow = _windows.FirstOrDefault(w => w.Handle == _chosenWindow.Handle && w.ProcessId == _chosenWindow.ProcessId); }
        ShowWindows();
    }

    /// <summary>
    /// The windows off the screen only (and the one chosen), until Show the other windows; with none off
    /// the screen, every window at once under a line that says so.
    /// </summary>
    private void ShowWindows()
    {
        var off = _windows.Count(w => w.OffScreen);
        var all = _showAllWindows || off == 0;
        var shown = all ? _windows : _windows.Where(w => w.OffScreen || ReferenceEquals(w, _chosenWindow)).ToList();
        WindowList.ItemsSource = shown.Select(w => new WindowRowView(w, ReferenceEquals(w, _chosenWindow))).ToList();
        WindowListHost.Visibility = _windows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        WindowsEmpty.Visibility = _windows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WindowsHeading.Text = WindowRescue.ListHeading(off, _windows.Count, all);
        WindowsHeading.Visibility = _windows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var others = _windows.Count - shown.Count;
        WindowsToggle.Content = _showAllWindows ? "Show only the windows off the screen" : (others == 1 ? "Show the other window" : "Show the other " + Words.Count(others, "window"));
        WindowsToggle.Visibility = off > 0 && (_showAllWindows || others > 0) ? Visibility.Visible : Visibility.Collapsed;
        RefreshBringBack();
    }

    private void OnWindowsToggle(object sender, RoutedEventArgs e)
    {
        _showAllWindows = !_showAllWindows;
        ShowWindows();
        Dispatcher.BeginInvoke(new Action(() => { if (WindowsToggle.IsVisible) { WindowsToggle.Focus(); } }), DispatcherPriority.Loaded);
    }

    /// <summary>Bring it back needs a chosen window, and nothing else may run (one thing at a time).</summary>
    private void RefreshBringBack() => WindowList.IsEnabled = _buttons.IsEnabled(AppButton.WindowBack);

    private void OnWindowChosen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WindowRowView row) { return; }
        // Windows does not let this app move it: the row says so, and a click changes nothing.
        if (row.Window.CannotMove) { return; }
        _chosenWindow = row.Window;
        ShowWindows();
        Dispatcher.BeginInvoke(new Action(() => FocusWindowRow(row.Window)), DispatcherPriority.Loaded);
        // One click: the chosen window comes back at once (one thing at a time, as every tool).
        if (_buttons.IsEnabled(AppButton.WindowBack)) { OnBringBack(sender, e); }
    }

    private void FocusWindowRow(AppWindow window)
    {
        WindowList.UpdateLayout();
        for (var i = 0; i < WindowList.Items.Count; i++)
        {
            if (WindowList.Items[i] is WindowRowView view && ReferenceEquals(view.Window, window)
                && WindowList.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter container
                && VisualTreeHelper.GetChildrenCount(container) > 0 && VisualTreeHelper.GetChild(container, 0) is UIElement element)
            {
                element.Focus();
                return;
            }
        }
    }

    private void OnRefreshWindows(object sender, RoutedEventArgs e)
    {
        LoadWindows();
        if (_windows.Count == 0) { ShowNoWindows(); }
    }

    private void OnCancelWindows(object sender, RoutedEventArgs e) => LeaveWindows();

    /// <summary>Back or Cancel: to the page the window page was opened from; nothing is moved.</summary>
    private void LeaveWindows()
    {
        _chosenWindow = null;
        if (_windowsFrom == Page.It)
        {
            ShowPage(Page.It, focus: false);
            var tool = ToolCatalog.ByButton(nameof(WindowBackButton));
            if (tool != null) { FocusToolRow(tool, run: true); }
            return;
        }
        ShowPage(Page.Problems, focus: false);
        ProblemWindowButton.Focus();
    }

    /// <summary>No window to list: a result of its own, in the status, the history and the run log.</summary>
    private void ShowNoWindows() => Guarded(() =>
    {
        ClearResult();
        StartCheck(WindowRescue.Name);
        var now = DateTime.UtcNow;
        ShowWindowOutcome(new WindowRescueOutcome { Result = WindowRescueResult.NoWindows, StartedUtc = now, EndedUtc = now, Monitors = SafeMonitorCount() });
        return Task.CompletedTask;
    }, byUser: false);

    private int SafeMonitorCount()
    {
        try { return WindowApi.Monitors().Count; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) { return 0; }
    }

    /// <summary>Bring it back: only the chosen window, with the user's own rights, then its result.</summary>
    private void OnBringBack(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        var chosen = _chosenWindow;
        if (chosen == null) { return; }
        ClearResult();
        StartCheck(WindowRescue.Name);
        SetStatus("Bringing the " + chosen.AppName + " window back...", Tone.Neutral);
        var api = WindowApi;
        var outcome = await Task.Run(() => WindowRescue.Run(api, chosen));
        ShowWindowOutcome(outcome);
        LoadWindows();
        await RefreshAfterToolAsync(ToolCatalog.ByButton(nameof(WindowBackButton)));
    });

    /// <summary>The result of Bring my window back: status, card, run log line and history; never the window title.</summary>
    private void ShowWindowOutcome(WindowRescueOutcome outcome)
    {
        var runId = _shownCheck?.RunId ?? "";
        var (status, tone, card) = WindowRescue.Present(outcome);
        SetStatus(status, tone);
        AddCard(card);
        AppendLog(WindowRescue.LogLine(outcome, runId));
        _focusResultAtEnd = true;
        History.Add(WindowRescue.HistoryName, WindowRescue.HistoryResult(outcome, runId), HistoryOverride);
        FinishCheck();
    }

    // --- Get a new IP address ------------------------------------------------------------------

    /// <summary>The adapters as Windows has them now; an adapter source that fails counts as none.</summary>
    private List<NetAdapter> ReadAdapters()
    {
        try { return Adapters.Read().ToList(); }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            App.LogError(ex);
            return new List<NetAdapter>();
        }
    }

    /// <summary>The adapters for the network summary; null when they could not be read (the summary then says unknown).</summary>
    private List<NetAdapter> AdaptersOrNull()
    {
        try { return Adapters.Read().ToList(); }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            App.LogError(ex);
            return null;
        }
    }

    /// <summary>A physical, connected DHCP adapter without a usable IPv4 address: only then does I can't connect offer Get a new IP address.</summary>
    private bool AnyAdapterNeedsAddress() => ReadAdapters().Any(AdapterRules.NeedsNewAddress);

    /// <summary>The tool's own button (For IT, its row, its detail page, and its row on I can't connect): opens the adapter page.</summary>
    private void OnRenewIpTool(object sender, RoutedEventArgs e) => OpenIpPage(_page == Page.It ? Page.It : Page.Problem);

    /// <summary>The adapter page: the physical adapters, those that need a new address first (and the first one of them chosen).</summary>
    private void OpenIpPage(Page from)
    {
        if (_page != Page.Ip) { _ipFrom = from; }
        ProblemTitle.Text = IpRenewal.Name;
        _chosenAdapter = null;
        LoadAdapters();
        _chosenAdapter = _adapterList.FirstOrDefault(AdapterRules.NeedsNewAddress);
        ShowAdapters();
        ShowPage(Page.Ip);
    }

    /// <summary>Reads the adapters again; the chosen one stays chosen while it is still there.</summary>
    private void LoadAdapters()
    {
        _adapterList = AdapterRules.Listed(ReadAdapters());
        if (_chosenAdapter != null) { _chosenAdapter = _adapterList.FirstOrDefault(a => a.InterfaceIndex == _chosenAdapter.InterfaceIndex); }
        ShowAdapters();
    }

    private void ShowAdapters()
    {
        AdapterList.ItemsSource = _adapterList.Select(a => new AdapterRowView(a, IpRenewal.StateLine(a), ReferenceEquals(a, _chosenAdapter))).ToList();
        AdapterListHost.Visibility = _adapterList.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        IpEmpty.Visibility = _adapterList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var why = IpRenewal.NotOffered(_chosenAdapter);
        IpNotOffered.Text = why;
        IpNotOffered.Visibility = why.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshRenewNow();
    }

    /// <summary>Get a new address needs a chosen adapter that is offered one, the service, and nothing else running.</summary>
    private void RefreshRenewNow()
        => RenewNowButton.IsEnabled = _chosenAdapter != null && AdapterRules.Block(_chosenAdapter) == RenewBlock.None && _buttons.IsEnabled(AppButton.RenewIp);

    private void OnAdapterChosen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AdapterRowView row) { return; }
        _chosenAdapter = row.Adapter;
        ShowAdapters();
        Dispatcher.BeginInvoke(new Action(() => FocusAdapterRow(row.Adapter)), DispatcherPriority.Loaded);
    }

    private void FocusAdapterRow(NetAdapter adapter)
    {
        AdapterList.UpdateLayout();
        for (var i = 0; i < AdapterList.Items.Count; i++)
        {
            if (AdapterList.Items[i] is AdapterRowView view && ReferenceEquals(view.Adapter, adapter)
                && AdapterList.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter container
                && VisualTreeHelper.GetChildrenCount(container) > 0 && VisualTreeHelper.GetChild(container, 0) is UIElement element)
            {
                element.Focus();
                return;
            }
        }
    }

    private void OnRefreshAdapters(object sender, RoutedEventArgs e) => LoadAdapters();

    private void OnCancelIp(object sender, RoutedEventArgs e) => LeaveIp();

    /// <summary>Back or Cancel: to the page the adapter page was opened from; nothing is changed.</summary>
    private void LeaveIp()
    {
        _chosenAdapter = null;
        if (_ipFrom == Page.It)
        {
            ShowPage(Page.It, focus: false);
            var tool = ToolCatalog.ByButton(nameof(RenewIpButton));
            if (tool != null) { FocusToolRow(tool, run: true); }
            return;
        }
        ShowProblem(Problem.CantConnect, focus: false);
        var renew = ToolCatalog.ByButton(nameof(RenewIpButton));
        if (renew != null) { FocusToolRow(renew, run: true); }
    }

    /// <summary>
    /// Get a new address: a short confirmation (its connection drops for a moment, also for anyone
    /// else signed in), then the service renews only the chosen adapter and Windows confirms the address.
    /// </summary>
    private void OnRenewNow(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        var adapter = _chosenAdapter;
        if (adapter == null || AdapterRules.Block(adapter) != RenewBlock.None) { return; }
        await RunForAllSessionsAsync(ToolbeltAction.RenewIpAddress, "_Get a new address", "Getting a new IP address. The connection drops for a moment...",
            confirmText: IpRenewal.ConfirmText(adapter), interfaceIndex: adapter.InterfaceIndex);
        LoadAdapters();
    });

    /// <summary>
    /// One of the three other paths: its health rows with their fix links, a verdict that also says
    /// when these checks are clean, and links to its tools in For IT (where each tool lives).
    /// </summary>
    private void ShowProblem(Problem problem, bool focus = true)
    {
        _problem = problem;
        var path = ProblemPaths.Get(problem);
        ProblemTitle.Text = path.Title;
        AutomationProperties.SetName(PathCopyTicketButton, "Copy details for IT: " + path.Title);
        ProblemIntro.Text = path.Intro;
        var pathRows = PathRows(path);
        var (text, tone) = ProblemPaths.Verdict(path, pathRows, _wuPathCheck);
        ProblemVerdict.Text = text;
        ProblemVerdict.Foreground = ThemeBrush.For(tone);
        // A path without health rows (Word or Excel closes) shows no verdict.
        ProblemVerdict.Visibility = path.Rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // The compliance path's rows are its own already (one "Device settings" row while they cannot be read).
        var checks = (path.Problem == Problem.DeviceBlocked ? pathRows : pathRows.Where(r => path.Rows.Contains(r.Name))).Select(NewHealthRow).ToList();
        ProblemChecks.ItemsSource = checks;
        // No empty, sizeless list before the first check is in.
        ProblemChecksBox.Visibility = checks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // The path's tools as For IT's rows: the name opens the detail page, Run starts the tool.
        // The safe-mode tools only for an installed app; Get a new IP address only while an adapter needs one.
        // The tools' own buttons are set again first: a row's Run follows its tool's button, which
        // would otherwise stay off for an app installed while the window was open.
        UpdateButtons();
        LoadToolRuns();
        var kept = RowFocus(ProblemTools);
        if (kept.Tool == null) { kept = RowFocus(ProblemHeavyTools); }
        // Try these, then a heavy tool (a long repair) in its own group: with IT support.
        var tools = ProblemPaths.ToolsShown(path, ToolOffered, _switchedOff).Select(ToolCatalog.ByButton).Where(t => t != null).ToList();
        ProblemTools.ItemsSource = tools.Where(t => !t.Heavy).Select(t => NewToolRow(t, showGroup: false, selected: false)).ToList();
        ProblemHeavyTools.ItemsSource = tools.Where(t => t.Heavy).Select(t => NewToolRow(t, showGroup: false, selected: false)).ToList();
        ProblemToolsHeading.Visibility = tools.Any(t => !t.Heavy) ? Visibility.Visible : Visibility.Collapsed;
        ProblemHeavyGroup.Visibility = tools.Any(t => t.Heavy) ? Visibility.Visible : Visibility.Collapsed;
        if (kept.Tool != null) { FocusToolRow(kept.Tool, kept.Run); }
        if (_page != Page.Problem) { ShowPage(Page.Problem, focus); }
        else { ShowStatusForPage(); }
    }

    // --- An app won't install -----------------------------------------------------------

    /// <summary>The app path: the list shows the diagnosis there is, and runs one when there is none yet.</summary>
    private void OpenAppPath()
    {
        ProblemTitle.Text = ProblemPaths.Get(Problem.AppWontInstall).Title;
        AutomationProperties.SetName(PathCopyTicketButton, "Copy details for IT: " + ProblemTitle.Text);
        ShowAppChecks();
        ShowApps(focusList: false);
        ShowPage(Page.Apps);
        if (_diagnosis == null || _diagnosis.Run == DiagnosisRunState.NotRun) { RunDiagnosis(); }
    }

    /// <summary>When this device's last Full IME reset ran without failing, from the run history; null without one.</summary>
    private DateTime? LastImeResetUtc()
    {
        LoadToolRuns();
        return _toolRuns.TryGetValue("FullResetButton", out var run) && run.Result is RunVerdict.Ok or RunVerdict.Check ? run.Log?.StartedUtc ?? run.Log?.WrittenUtc : null;
    }

    /// <summary>The steps taken at or before the last Full IME reset end: removed from the user's file.</summary>
    private void DropStepsBeforeReset()
    {
        var ended = PostCheck.BeforeReset(_remedies, LastImeResetUtc());
        if (ended.Count == 0) { return; }
        foreach (var remedy in ended) { _remedies.Remove(remedy); }
        SaveRemedies();
    }

    /// <summary>The Retry steps of apps this diagnosis lists as offered in Company Portal: removed from the user's file.</summary>
    private void DropRetriesOfOfferedApps(AppDiagnosis diagnosis)
    {
        var ended = PostCheck.NotRetriedByIme(_remedies, diagnosis);
        if (ended.Count == 0) { return; }
        foreach (var remedy in ended) { _remedies.Remove(remedy); }
        SaveRemedies();
    }

    /// <summary>The usual install blockers (free disk space, a pending restart) above the app list, one line each.</summary>
    private void ShowAppChecks()
    {
        // Without the IME the app path says so on its own row (no fix) and offers no Retry work apps and settings.
        var imeMissing = _healthRows.Any(r => r.ImeNotInstalled);
        var rows = _healthRows.Where(r => (imeMissing && r.Name == HealthView.AppInstaller) || ProblemPaths.InstallBlockers.Contains(r.Name)).Select(NewHealthRow).ToList();
        AppChecks.ItemsSource = rows;
        // All OK: one status line after the time ("Free disk space OK · Restart OK"), its rows on a click.
        // One that is not OK: its rows at once, as before.
        var allOk = _healthRows.Where(r => (imeMissing && r.Name == HealthView.AppInstaller) || ProblemPaths.InstallBlockers.Contains(r.Name)).All(r => r.Status == HealthStatus.Pass);
        AppChecksBox.Visibility = rows.Count == 0 ? Visibility.Collapsed : !allOk || _appChecksOpen ? Visibility.Visible : Visibility.Collapsed;
        AppChecksLink.Content = string.Join(" · ", rows.Select(r => r.Name + " " + r.BadgeWord));
        AppChecksLink.Visibility = rows.Count > 0 && allOk ? Visibility.Visible : Visibility.Collapsed;
        AppChecksDot.Visibility = AppChecksLink.Visibility;
        AutomationProperties.SetName(AppChecksLink, (_appChecksOpen ? "Hide: " : "Show: ") + AppChecksLink.Content);
        AutomationProperties.SetItemStatus(AppChecksLink, _appChecksOpen ? "Expanded" : "Collapsed");
        _imeMissingForApps = imeMissing;
        AppsRetry.Visibility = imeMissing || _showAllApps || (_appDetailView && _selectedApp != null && _selectedApp.Next != AppNextStep.Wait) ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = AppsRetry.Visibility;
        RetryWhyLink.Visibility = AppsRetry.Visibility;
        AppsFooter.Visibility = AppsRetry.Visibility == Visibility.Visible || AppsActions.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnReloadApps(object sender, RoutedEventArgs e) => RunDiagnosis();

    /// <summary>The status line's checks: their rows open below it, or close again.</summary>
    private void OnAppChecksLink(object sender, RoutedEventArgs e)
    {
        _appChecksOpen = !_appChecksOpen;
        ShowAppChecks();
    }

    /// <summary>
    /// Runs the read-only app diagnosis in the service. Without the service it does not try:
    /// the list then says that nothing could be read and offers the handover.
    /// </summary>
    private void RunDiagnosis()
    {
        if (_busy || _following) { return; }
        if (_buttons.ServiceMissing)
        {
            SetDiagnosis(AppDecisions.From(null, DateTime.UtcNow), null);
            return;
        }
        Guarded(async () =>
        {
            var chosen = _selectedApp;
            _diagnosis = new AppDiagnosis { Run = DiagnosisRunState.Running };
            ShowApps(focusList: false);
            var run = await RunActionAsync(ToolbeltAction.DiagnoseApps, false, -1, "Checking the apps on this device (read-only)...");
            if (run == null)
            {
                // The connection ended while it ran: the window follows it and shows it when it is in.
                return;
            }
            _selectedApp = chosen;
            var applied = ApplyDiagnosis(run.Outcome);
            await FinishRunAsync(run);
            await ShowPostCheckStatusAsync(run.Outcome, applied);
        }, showStatus: false);
    }

    /// <summary>"Check again": the same read-only diagnosis as Reload, which is the post-check of every remedy taken before it.</summary>
    private void OnCheckAgain(object sender, RoutedEventArgs e) => RunDiagnosis();

    /// <summary>
    /// Shows the outcome of a diagnosis run. A run the service did not start because another action
    /// runs or the limit of 30 an hour was reached leaves the previous list as it was;
    /// returns false then.
    /// </summary>
    private bool ApplyDiagnosis(RunOutcome outcome)
    {
        if (outcome.Result is ResultCode.Busy or ResultCode.RateLimited && _diagnosisBase is { Run: DiagnosisRunState.Finished })
        {
            _checkedNow = new List<RemedyRecord>();
            _diagnosis = PostCheck.Overlay(_diagnosisBase, _remedies, DateTime.UtcNow);
            ShowApps(focusList: false);
            ShowHome();
            return false;
        }
        // The IME's last check-in tells whether an available app may have a newer version in Intune.
        SetDiagnosis(AppDecisions.From(outcome, DateTime.UtcNow, null, TimeText.ParseUtc(_status?.LastImeCheckInUtc)), outcome);
        return true;
    }

    /// <summary>
    /// The status after a diagnosis that was a post-check: the Retry closing status while a
    /// Retry work apps and settings of the last 7 days was checked ("Done" only when every retried app
    /// was detected and the health checks are clean), else what the post-check found. A diagnosis
    /// that checked no remedy keeps its own status.
    /// </summary>
    private async Task ShowPostCheckStatusAsync(RunOutcome outcome, bool applied)
    {
        if (!applied)
        {
            SetStatus(PostCheck.NotChecked(outcome.Result), Tone.Warning);
            return;
        }
        if (_checkedNow.Count == 0) { return; }
        var group = PostCheck.RetryGroup(_remedies, DateTime.UtcNow);
        if (group.Any(r => _checkedNow.Contains(r)))
        {
            var counts = await RefreshHealthAsync(keepStatus: true);
            var (retry, retryTone) = ResultPresenter.RetryDone(counts.Fails, counts.Warns, PostCheck.AfterRetry(group), otherApps: PostCheck.OtherAttention(_diagnosis, group.Concat(_checkedNow)));
            SetStatus(retry, retryTone);
            return;
        }
        var (text, tone) = PostCheck.Status(_checkedNow, PostCheck.OtherAttention(_diagnosis, _checkedNow));
        SetStatus(text, tone);
    }

    /// <summary>
    /// The read-only diagnosis in the background, without replacing a result in the drawer: once before a
    /// Retry work apps and settings when the window has not read the apps, and once as its post-check
    /// when it ends. Its log goes into the run history. True when it read the apps.
    /// </summary>
    private async Task<bool> ReadAppsInBackgroundAsync()
    {
        var outcome = await Task.Run(() =>
        {
            var result = _gateway.Run(ToolbeltAction.DiagnoseApps, false, -1, null, out var file);
            ReceivedFile.Discard(file);
            return result;
        });
        if (ResultPresenter.BelongsInHistory(outcome))
        {
            History.Add(outcome.Action.ToString(), ResultPresenter.HistoryResult(outcome), HistoryOverride);
            var view = ResultPresenter.Final(outcome);
            SaveToHistory(NewHeader(outcome, null), view.LogLines.ToList(), view.Cards.ToList());
        }
        if (outcome.InProgress) { return false; }
        return ApplyDiagnosis(outcome) && _diagnosisBase?.Run == DiagnosisRunState.Finished;
    }

    /// <summary>
    /// Keeps a remedy taken from app findings with the diagnosis, app, scope, revision and
    /// observation it came from, in the user's own file, and shows those apps as awaiting evidence.
    /// </summary>
    private void RecordRemedies(IEnumerable<AppFinding> findings, RemedyKind kind, string runId, DateTime atUtc)
    {
        var added = false;
        foreach (var finding in findings ?? Enumerable.Empty<AppFinding>())
        {
            var remedy = PostCheck.Remedy(finding, kind, atUtc, _remedyUser, runId);
            if (remedy == null || _remedies.Any(r => PostCheck.SameRemedy(r, remedy))) { continue; }
            _remedies.Add(remedy);
            added = true;
        }
        if (!added) { return; }
        SaveRemedies();
        if (_diagnosisBase != null && _diagnosis?.Run != DiagnosisRunState.Running)
        {
            _diagnosis = PostCheck.Overlay(_diagnosisBase, _remedies, DateTime.UtcNow);
            ShowApps(focusList: false);
            ShowHome();
        }
    }

    /// <summary>Writes the remedy records; a convenience like the run history, so a failed write is logged and costs nothing else.</summary>
    private void SaveRemedies()
    {
        if (!RemedyStore.Save(_remedies, _remediesPath, _remedyUser, DateTime.UtcNow))
        {
            App.LogError(new IOException("The remedy records could not be written."));
        }
    }

    /// <summary>The diagnosis Home and the app path show from now on.</summary>
    private void SetDiagnosis(AppDiagnosis diagnosis, RunOutcome run)
    {
        _diagnosisBase = diagnosis;
        _diagnosisRun = run;
        _diagnosisCheck = null;
        // A finished diagnosis is the post-check of every active remedy taken before it.
        var now = DateTime.UtcNow;
        // A Full IME reset is a new start: the steps before it end (their apps' records are gone).
        DropStepsBeforeReset();
        // A Retry never retries an app only offered in Company Portal: a step recorded for one is removed.
        DropRetriesOfOfferedApps(diagnosis);
        var before = _remedies.ToDictionary(r => r, r => r.State);
        _checkedNow = PostCheck.Check(_remedies, diagnosis, now);
        if (_checkedNow.Count > 0) { SaveRemedies(); }
        var detectedNow = _checkedNow.Any(r => r.State == PostCheckState.Detected && before[r] != PostCheckState.Detected);
        _diagnosis = PostCheck.Overlay(diagnosis, _remedies, now);
        if (run == null || string.IsNullOrEmpty(run.RunId))
        {
            // No service run to point to: the handover gets a reference of its own, kept in the run history.
            _diagnosisCheck = LocalRun.Start(ActionText.Name(ToolbeltAction.DiagnoseApps), DateTime.UtcNow);
            _diagnosisCheck.EndedUtc = _diagnosisCheck.StartedUtc;
            SaveToHistory(NewHeader(null, _diagnosisCheck.Snapshot()), new List<string> { "The app diagnosis could not run: " + (diagnosis.Problem?.Text ?? "") }, diagnosis.Cards());
        }
        // A chosen app stays chosen in the new diagnosis; "not in this list" and a problem of the old one do not.
        if (_selectedApp != null && (_selectedApp.Case == AppCase.NotListed || string.IsNullOrEmpty(_selectedApp.App.AppId))) { _selectedApp = null; }
        ShowApps(focusList: false);
        ShowHome();
        // A screen reader hears it when an app turns "App detected" (the status line says it too).
        if (detectedNow && AppDetailPostCheck.Visibility == Visibility.Visible) { Announce(AppDetailPostCheck); }
    }

    /// <summary>
    /// The app list: the run's state on its own line, then the apps that need attention or are on
    /// their way (all apps behind a link), each with its observed state, and the selected one's
    /// details. The first app that needs attention is selected, unless another app has its name:
    /// then the user chooses.
    /// </summary>
    private void ShowApps(bool focusList)
    {
        var diagnosis = _diagnosis;
        // The Retry tool's sentence follows every change of the diagnosis, also while its page is shown.
        ShowRetryToolSummary();
        // Short on the page ("Checked at 12:07"); the whole line (date, zone, nothing changed) is its tooltip and name.
        var runLine = AppDecisions.RunLine(diagnosis);
        DiagnosisRunText.Text = AppDecisions.ShortRunLine(diagnosis, TimeZoneInfo.Local, DateTime.UtcNow);
        DiagnosisRunText.ToolTip = runLine;
        AutomationProperties.SetName(DiagnosisRunText, runLine);
        DiagnosisRunText.Foreground = ThemeBrush.Get(diagnosis?.Run == DiagnosisRunState.Interrupted ? "ToneWarning" : "TextMuted");
        var apps = diagnosis?.Apps ?? new List<AppFinding>();
        // A search looks through every app, also the installed ones; without one, an app with a
        // remedy stays in the short list, also once it is detected.
        var shortList = apps.Where(AppDecisions.InShortList).ToList();
        var canSearch = apps.Count >= AppDecisions.SearchFrom;
        var query = (AppSearch.Text ?? "").Trim();
        var searching = query.Length > 0 && canSearch;
        var view = _showAllApps && apps.Count > 0;
        // The short list shows a few rows, so the page fits its window; the rest are first in Show all.
        var capped = shortList.Take(AppDecisions.ShortListRows).ToList();
        // The search shows in the all-apps view, after Find another app, and while no app needs attention.
        var searchShown = canSearch && (view || _appSearchOpen || searching || shortList.Count == 0);
        // No app needs attention: the page says so and lists every app at once, not an empty list with only Show all.
        var allFine = !view && !searching && shortList.Count == 0 && apps.Count > 0;
        var listAll = allFine && apps.Count <= AppDecisions.NothingNeedsAttentionListsUpTo;
        var shown = searching ? AppDecisions.Search(apps, query) : view || listAll ? apps.OrderBy(a => a.App.Label, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.App.Scope, StringComparer.Ordinal).ToList() : capped;
        AppSearchBox.Visibility = searchShown ? Visibility.Visible : Visibility.Collapsed;
        AppSearchResult.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        AppSearchResult.Text = !searching ? ""
            : shown.Count == 0 ? "No app's name holds \"" + query + "\". Esc clears the search."
            : Words.Count(shown.Count, "app") + " of " + apps.Count.ToString(CultureInfo.InvariantCulture) + " found.";
        if (_selectedApp != null && diagnosis != null && !ReferenceEquals(_selectedApp, diagnosis.Problem) && _selectedApp.Case != AppCase.NotListed && !apps.Contains(_selectedApp))
        {
            // The app chosen before, in a newer diagnosis (by its ID and scope); else the main finding again.
            var old = _selectedApp;
            _selectedApp = string.IsNullOrEmpty(old.App.AppId) ? null : apps.FirstOrDefault(a => a.App.AppId == old.App.AppId && a.App.Scope == old.App.Scope);
        }
        if (_selectedApp == null && diagnosis != null)
        {
            var main = diagnosis.Main;
            _selectedApp = main != null && !main.SameNameAsAnother ? main : null;
        }
        AppList.ItemsSource = shown.Select(a => new AppRowView(a, ReferenceEquals(a, _selectedApp))).ToList();
        AppListHost.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // After a Full IME reset, until the IME checked in: the list is being read again.
        var resetLine = view ? "" : AppDecisions.ResetLine(LastImeResetUtc(), TimeText.ParseUtc(_status?.LastImeCheckInUtc), TimeZoneInfo.Local, DateTime.UtcNow);
        AppResetNote.Text = resetLine;
        AppResetNote.Visibility = resetLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AppListHeading.Text = allFine ? AppDecisions.NothingNeedsAttention(apps.Count)
            : view ? "All apps (" + apps.Count.ToString(CultureInfo.InvariantCulture) + ")"
            : shortList.Count > capped.Count ? "Apps that need attention (" + capped.Count.ToString(CultureInfo.InvariantCulture) + " of " + shortList.Count.ToString(CultureInfo.InvariantCulture) + ")"
            : shortList.Count > 0 ? "Apps that need attention (" + shortList.Count.ToString(CultureInfo.InvariantCulture) + ")" : "";
        AppListHeading.Visibility = !searching && AppListHeading.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Find another app opens the search; Show all opens the all-apps view. In the view, Back is the way out.
        AppFindLink.Visibility = !view && canSearch && !searchShown ? Visibility.Visible : Visibility.Collapsed;
        AppListToggle.Content = "Show all " + Words.Count(apps.Count, "app");
        AppListToggle.Visibility = !view && !searching && !listAll && apps.Count > capped.Count ? Visibility.Visible : Visibility.Collapsed;
        AppListLinks.Visibility = AppFindLink.Visibility == Visibility.Visible || AppListToggle.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        // Three ways to show the page: the list (the top, the short list, the actions), the all-apps view (the
        // list alone) and the chosen app's view (its details alone, with Retry when its step is to wait).
        var detail = _appDetailView && _selectedApp != null && !view;
        AppsTop.Visibility = view || detail ? Visibility.Collapsed : Visibility.Visible;
        AppsListPanel.Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
        AppsActions.Visibility = view || detail ? Visibility.Collapsed : Visibility.Visible;
        AppsRetry.Visibility = _imeMissingForApps || view || (detail && _selectedApp.Next != AppNextStep.Wait) ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = AppsRetry.Visibility;
        RetryWhyLink.Visibility = AppsRetry.Visibility;
        NotListedButton.Visibility = view ? Visibility.Collapsed : Visibility.Visible;
        AppListLinks.Visibility = AppFindLink.Visibility == Visibility.Visible || AppListToggle.Visibility == Visibility.Visible || NotListedButton.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        AppsFooter.Visibility = AppsRetry.Visibility == Visibility.Visible || AppsActions.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (_page == Page.Apps)
        {
            AutomationProperties.SetName(BackButton, view || detail ? "Back to the apps that need attention" : _pathFrom == Page.Home ? "Back to Home" : _pathFrom == Page.It ? "Back to For IT support" : "Back to Choose a problem");
        }
        // The details are filled for the chosen app (Home and the tests read them) but shown only in its view.
        ShowAppDetail(_selectedApp ?? (diagnosis?.Main is { SameNameAsAnother: true } twin ? ChooseOne(twin) : null));
        if (!detail) { AppDetail.Visibility = Visibility.Collapsed; }
        if (focusList && !detail) { Dispatcher.BeginInvoke(new Action(() => FocusFirst(AppList)), DispatcherPriority.Loaded); }
    }

    /// <summary>The detail panel while two apps of one name wait for the user's choice.</summary>
    private static AppFinding ChooseOne(AppFinding twin) => new()
    {
        App = new AppEvidence(),
        DiagnosisId = twin.DiagnosisId,
        Case = twin.Case,
        Observation = AppObservation.Unknown,
        Next = AppNextStep.None,
        Title = "More than one app is called " + twin.App.Label,
        Text = "Choose the one you mean in the list above; each has its own state and next step.",
    };

    private static void FocusFirst(ItemsControl list)
    {
        if (list.Items.Count > 0 && list.ItemContainerGenerator.ContainerFromIndex(0) is ContentPresenter first
            && VisualTreeHelper.GetChildrenCount(first) > 0 && VisualTreeHelper.GetChild(first, 0) is UIElement item)
        {
            item.Focus();
        }
    }

    private void ShowAppDetail(AppFinding finding)
    {
        AppDetail.Visibility = finding == null ? Visibility.Collapsed : Visibility.Visible;
        if (finding == null) { return; }
        AppDetailTitle.Text = finding.Title;
        AppDetailState.Text = AppDecisions.DetailState(finding);
        // The post-check on a line of its own, in words and in its tone.
        SetOptional(AppDetailPostCheck, PostCheck.Line(finding));
        AppDetailPostCheck.Foreground = ThemeBrush.For(finding.AfterRemedy switch
        {
            PostCheckState.Detected => Tone.Success,
            PostCheckState.Failed => Tone.Error,
            PostCheckState.Unknown => Tone.Warning,
            _ => Tone.Neutral,
        });
        AppDetailText.Text = finding.Text;
        SetOptional(AppDetailEvidence, finding.Evidence);
        SetOptional(AppDetailUncertainty, finding.Uncertainty);
        SetOptional(AppDetailCaveat, finding.Caveat);
        SetOptional(AppDetailNext, string.IsNullOrEmpty(finding.NextText) ? "" : "Next step: " + finding.NextText);
        // "Check again" has a button of its own with an access key; the step button never repeats it.
        var stepText = HomeSummary.StepText(finding.Next is AppNextStep.ChooseApp or AppNextStep.CheckAgain ? AppNextStep.None : finding.Next);
        AppStepButton.Content = stepText;
        AutomationProperties.SetName(AppStepButton, StepName(stepText, finding.Title));
        AutomationProperties.SetHelpText(AppStepButton, finding.NextText);
        AppStepButton.Visibility = stepText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AppStepButton.Tag = finding;
        // The standing link to the app's page in Company Portal (an app assigned to the user),
        // left out only while the step button is that same action.
        AppPortalLink.Visibility = AppDecisions.ShowPortalLink(finding, stepText.Length > 0 ? finding.Next : AppNextStep.None) ? Visibility.Visible : Visibility.Collapsed;
        AppPortalLink.Tag = finding;
        // Its own label says what it shows when the name is not on this device.
        AppPortalLink.Content = AppDecisions.PortalLinkText(finding);
        AutomationProperties.SetName(AppPortalLink, "Open " + finding.App.Label + " in Company Portal" + (string.IsNullOrWhiteSpace(finding.App.Name) ? " to see its name" : ""));
        CheckAgainButton.Visibility = finding.Remedy != null && finding.AfterRemedy != PostCheckState.Detected ? Visibility.Visible : Visibility.Collapsed;
        RefreshStepButtons();
        if (finding.Next == AppNextStep.Restart && finding.Remedy == null && !string.IsNullOrEmpty(finding.App.AppId))
        {
            // The advice to restart, shown on the app path, is the remedy for this app; kept once
            // it was shown (after this layout pass, so the list is not rebuilt while it is built).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_page == Page.Apps && ReferenceEquals(AppStepButton.Tag, finding)) { RecordRemedies(new[] { finding }, RemedyKind.RestartAdvice, "", DateTime.UtcNow); }
            }), DispatcherPriority.Background);
        }
    }

    private static void SetOptional(TextBlock block, string text)
    {
        block.Text = text ?? "";
        block.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>An app in the list was chosen (Enter, Space or a click): its details and its one next step.</summary>
    private void OnAppSelected(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppRowView row) { return; }
        SelectApp(row.Finding);
    }

    /// <summary>An app chosen in the list (a click, or Enter in the search): its details, focused.</summary>
    private void SelectApp(AppFinding finding)
    {
        _selectedApp = finding;
        // The app's view: its details alone, from the top; Back returns to the list.
        _showAllApps = false;
        _appDetailView = true;
        ShowApps(focusList: false);
        AppTechPanel.Visibility = Visibility.Collapsed;
        ShowAppTechToggle();
        Dispatcher.BeginInvoke(new Action(() => { MainScroll.ScrollToTop(); AppDetailTitle.Focus(); }), DispatcherPriority.Loaded);
    }

    /// <summary>The app search: the list shows the matches over all apps while there is text.</summary>
    private void OnAppSearch(object sender, TextChangedEventArgs e)
    {
        AppSearchLabel.Visibility = (AppSearch.Text ?? "").Length == 0 ? Visibility.Visible : Visibility.Hidden;
        if (_page == Page.Apps) { ShowApps(focusList: false); }
    }

    /// <summary>Esc clears the app search; Enter selects the first app found.</summary>
    private void OnAppSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (AppSearch.Text ?? "").Length > 0)
        {
            AppSearch.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _appSearchOpen)
        {
            _appSearchOpen = false;
            ShowApps(focusList: false);
            Dispatcher.BeginInvoke(new Action(() => { if (AppFindLink.IsVisible) { AppFindLink.Focus(); } }), DispatcherPriority.Loaded);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && AppList.ItemsSource is IEnumerable<AppRowView> rows && rows.FirstOrDefault() is AppRowView first)
        {
            SelectApp(first.Finding);
            e.Handled = true;
        }
    }

    /// <summary>Show all: the all-apps view, from its top (Back leaves it).</summary>
    private void OnAppListToggle(object sender, RoutedEventArgs e)
    {
        if (_showAllApps)
        {
            LeaveAllApps();
            return;
        }
        _showAllApps = true;
        ShowApps(focusList: false);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            MainScroll.ScrollToTop();
            if (AppSearchBox.IsVisible) { AppSearch.Focus(); } else { FocusFirst(AppList); }
        }), DispatcherPriority.Loaded);
    }

    /// <summary>Back from the all-apps view to the page, the focus on Show all.</summary>
    private void LeaveAllApps()
    {
        _showAllApps = false;
        ShowApps(focusList: false);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (AppListToggle.IsVisible) { AppListToggle.Focus(); AppListToggle.BringIntoView(); } else { FocusTitle(); }
        }), DispatcherPriority.Loaded);
    }

    /// <summary>Back from the app's view to the list, the focus on its row (or the list's first).</summary>
    private void LeaveAppDetail()
    {
        _appDetailView = false;
        var chosen = _selectedApp;
        ShowApps(focusList: false);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            AppList.UpdateLayout();
            for (var i = 0; i < AppList.Items.Count; i++)
            {
                if (AppList.Items[i] is AppRowView row && ReferenceEquals(row.Finding, chosen)
                    && AppList.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter container
                    && VisualTreeHelper.GetChildrenCount(container) > 0 && VisualTreeHelper.GetChild(container, 0) is UIElement element)
                {
                    element.Focus();
                    return;
                }
            }
            FocusFirst(AppList);
        }), DispatcherPriority.Loaded);
    }

    /// <summary>Technical details: the record (state, IDs, post-check, detection run), shown on request.</summary>
    private void OnAppTechToggle(object sender, RoutedEventArgs e)
    {
        AppTechPanel.Visibility = AppTechPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ShowAppTechToggle();
    }

    private void ShowAppTechToggle()
    {
        var open = AppTechPanel.Visibility == Visibility.Visible;
        AppTechToggle.Content = open ? "Hide technical details" : "Show technical details";
        AutomationProperties.SetItemStatus(AppTechToggle, open ? "Expanded" : "Collapsed");
    }

    /// <summary>Find another app: the search over all apps, focused.</summary>
    private void OnAppFind(object sender, RoutedEventArgs e) => OpenAppSearch();

    private void OpenAppSearch()
    {
        _appSearchOpen = true;
        ShowApps(focusList: false);
        Dispatcher.BeginInvoke(new Action(() => { AppSearch.Focus(); AppSearch.SelectAll(); }), DispatcherPriority.Loaded);
    }

    /// <summary>"My app is not in this list": the uncertainty and the handover.</summary>
    private void OnNotListed(object sender, RoutedEventArgs e)
    {
        _selectedApp = AppDecisions.NotListed(_diagnosis);
        if (AppList.ItemsSource is IEnumerable<AppRowView> rows) { AppList.ItemsSource = rows.Select(r => new AppRowView(r.Finding, false)).ToList(); }
        // Shown as the app view too: its details alone, Back returns to the list.
        _showAllApps = false;
        _appDetailView = true;
        ShowApps(focusList: false);
        AppTechPanel.Visibility = Visibility.Collapsed;
        ShowAppTechToggle();
        Dispatcher.BeginInvoke(new Action(() => { MainScroll.ScrollToTop(); AppDetailTitle.Focus(); }), DispatcherPriority.Loaded);
    }

    private void OnAppStep(object sender, RoutedEventArgs e)
    {
        if (AppStepButton.Tag is AppFinding finding) { DoAppStep(finding, finding.Next, AppStepButton); }
    }

    /// <summary>The standing Company Portal link: the same step as the step button, and it stays on the card.</summary>
    private void OnAppPortalLink(object sender, RoutedEventArgs e)
    {
        if (AppPortalLink.Tag is AppFinding finding) { DoAppStep(finding, AppNextStep.OpenCompanyPortal, AppPortalLink); }
    }

    /// <summary>
    /// One next step of an app finding. A step that is a tool raises that tool's own button (the
    /// same confirmation and rules); Copy details for IT copies the ticket of the diagnosis with this
    /// finding on top; Company Portal opens on the app's own page.
    /// </summary>
    private void DoAppStep(AppFinding finding, AppNextStep step, Button source)
    {
        switch (step)
        {
            case AppNextStep.CopyForIt:
                CopyFindingTicket(finding, source);
                break;
            case AppNextStep.ConnectivityTest:
                var before = _shownCheck;
                RaiseTool(ConnectivityButton);
                // The test is a remedy for this app once it started, with its local run ID.
                if (_shownCheck != null && !ReferenceEquals(_shownCheck, before)) { RecordRemedies(new[] { finding }, RemedyKind.ConnectivityTest, _shownCheck.RunId, _shownCheck.StartedUtc); }
                break;
            case AppNextStep.RetryAppsAndPolicies:
                // Retry work apps and settings records a remedy for every app it retries (OnRetry, FinishRunAsync).
                RaiseTool(RetryButton);
                break;
            case AppNextStep.OpenCompanyPortal:
                if (OpenCompanyPortal(finding?.App.AppId)) { RecordRemedies(new[] { finding }, RemedyKind.CompanyPortal, "", DateTime.UtcNow); }
                break;
            case AppNextStep.ChooseApp:
                ShowApps(focusList: true);
                ShowPage(Page.Apps, focus: false);
                break;
            case AppNextStep.CheckAgain:
                RunDiagnosis();
                break;
        }
    }

    /// <summary>A tool's own button, pressed for the user: off while something runs or without the service, and then says why.</summary>
    private void RaiseTool(Button target)
    {
        if (!ToolCatalog.Offered(target.Name, _switchedOff) && ToolCatalog.ByButton(target.Name) != null)
        {
            SetStatus(SwitchedOffText, Tone.Warning);
            return;
        }
        if (!target.IsEnabled)
        {
            SetStatus(_buttons.Busy || _following ? "Another action is running. Wait until it has finished." : ButtonRules.ServiceMissing, Tone.Warning);
            return;
        }
        target.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, target));
    }

    /// <summary>
    /// Company Portal on the app's page: the documented <c>companyportal:ApplicationId=</c> link,
    /// only for an app ID that is a GUID; Company Portal itself when there is none.
    /// </summary>
    private bool OpenCompanyPortal(string appId)
    {
        var link = Guid.TryParse(appId ?? "", out var id) ? "companyportal:ApplicationId=" + id.ToString("D") : "companyportal:";
        SetStatusTool(null);
        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose();
            SetStatus("Company Portal is opening on the app's page. Choose Install or Retry there, then Check this app again here.", Tone.Neutral);
            return true;
        }
        catch (Win32Exception)
        {
            SetStatus("Company Portal could not be opened. Open it from the Start menu and find the app there.", Tone.Warning);
            return false;
        }
    }

    /// <summary>
    /// Copy details for IT for one app finding: the ticket of the diagnosis run (or of the
    /// attempt that could not reach the service), with this finding as its evidence and next step.
    /// </summary>
    private void CopyFindingTicket(AppFinding finding, Button source)
    {
        if (_diagnosis == null || (_diagnosisRun == null && _diagnosisCheck == null))
        {
            // Nothing ran (the service is missing): this attempt gets a reference of its own.
            SetDiagnosis(AppDecisions.From(null, DateTime.UtcNow), null);
        }
        var diagnosis = _diagnosis;
        finding ??= diagnosis.Problem;
        var header = NewHeader(_diagnosisRun, _diagnosisCheck?.Snapshot());
        var cards = new List<ResultCard>();
        if (finding != null) { cards.Add(finding.Card()); }
        cards.AddRange(diagnosis.Cards().Where(c => finding == null || c.Title != finding.Title));
        var (status, tone) = AppDecisions.Status(diagnosis);
        var data = new DataObject();
        // An app after a remedy also carries the original diagnosis, the remedy and the post-check result.
        // On the app path, its case file's Tried lines too.
        data.SetData(DataFormats.UnicodeText, RunTicket.Text(header, cards, status, tone, PostCheck.TicketLine(finding), facts: TicketFacts(), tried: _page == Page.Apps ? TriedLines(Problem.AppWontInstall) : null));
        var label = source.Content as string ?? "Copy details for IT";
        if (!PutOnClipboard(data, source, label, AutomationProperties.GetName(source), "Details for IT copied", "The ticket text could not be copied: another program is using the clipboard. Try again.")) { return; }
        // The handover is a remedy for this app: the app then awaits evidence that IT fixed it.
        RecordRemedies(new[] { finding }, RemedyKind.CopyForIt, "", DateTime.UtcNow);
        if (ReferenceEquals(source, AppStepButton) && AppStepButton.Visibility != Visibility.Visible && CheckAgainButton.Visibility == Visibility.Visible)
        {
            // The step button made way for Check again: the keyboard focus goes there, not to nowhere.
            Dispatcher.BeginInvoke(new Action(() => CheckAgainButton.Focus()), DispatcherPriority.Loaded);
        }
    }

    /// <summary>The primary steps follow the button they press: off when it is (busy, or the service is missing).</summary>
    private void RefreshStepButtons()
    {
        PrimaryStepButton.IsEnabled = StepEnabled(_home?.Step == HomeStep.HealthFix ? FixButton(_home.Fix) : StepButton(_home?.AppStep ?? AppNextStep.None));
        AppStepButton.IsEnabled = StepEnabled(StepButton((AppStepButton.Tag as AppFinding)?.Next ?? AppNextStep.None));
    }

    private static bool StepEnabled(Button target) => target == null || target.IsEnabled;

    private Button StepButton(AppNextStep step) => step switch
    {
        AppNextStep.ConnectivityTest => ConnectivityButton,
        AppNextStep.RetryAppsAndPolicies => RetryButton,
        AppNextStep.CheckAgain => CheckAgainButton,
        _ => null,
    };

    private Button FixButton(HealthFix fix) => fix switch
    {
        HealthFix.RetryAppsAndPolicies => RetryButton,
        HealthFix.ConnectivityTest => ConnectivityButton,
        HealthFix.SyncClock => SyncClockButton,
        HealthFix.UpdateDefender => UpdateDefenderButton,
        HealthFix.FreeDiskSpace => FreeSpaceButton,
        HealthFix.RefreshSignIn => RefreshSignInButton,
        _ => null,
    };

    // --- For IT --------------------------------------------------------------------

    // The narrow list shows the tools the search found because typing in the search opened it;
    // clearing the search then goes back to the tiles.
    private bool _listFromSearch;

    // The run history For IT reads; a test points it elsewhere.
    internal string RunsFolderOverride { get; set; }

    // The history file (history.log) the window writes to; a test points it elsewhere (null: the user's own).
    internal string HistoryOverride { get; set; }

    private bool Searching => (ToolSearch.Text ?? "").Trim().Length > 0;

    /// <summary>
    /// For IT as it is now: each tool's newest run from the run history, the tiles with
    /// their summaries, the list, the detail of the chosen tool, the recent runs, and which of
    /// them the layout shows.
    /// </summary>
    // The problem page a tool's detail was opened from ("Suggested for: ..."), else null.
    private string _toolSuggestedFor;

    private void ShowIt()
    {
        // The intro, or on a tool opened from a problem page, the problem that suggested it.
        if (_itView != ItView.Detail) { _toolSuggestedFor = null; }
        ItIntro.Text = _toolSuggestedFor != null ? "Suggested for: " + _toolSuggestedFor + "." : ToolCatalog.ItIntro;
        // As on a problem path: the rows' Run buttons follow the tools' own buttons, set again now.
        UpdateButtons();
        var logs = LoadToolRuns();
        // Never a tool the organization switched off; another group when this one has none on.
        if (_itTool != null && !ToolCatalog.Offered(_itTool, _switchedOff)) { _itTool = null; }
        if (!ToolCatalog.GroupOffered(_itGroup, _switchedOff) && ToolCatalog.Search("", _switchedOff).FirstOrDefault() is ToolInfo anyTool) { _itGroup = anyTool.Group; }
        _itTool ??= ToolCatalog.InGroup(_itGroup, _switchedOff).FirstOrDefault() ?? ToolCatalog.InGroup(_itGroup)[0];
        ShowTiles();
        ShowToolList();
        ShowToolDetail();
        ShowRecentRuns(logs);
        ApplyItLayout();
    }

    /// <summary>The local day the short times on screen ("today 18:38") were said for.</summary>
    private DateTime _datesDay = DateTime.Now.Date;

    private DispatcherTimer _midnightTimer;

    /// <summary>A one-shot timer a few seconds after the next local midnight: the short times are said again, then it waits for the next one.</summary>
    private void ScheduleMidnight()
    {
        if (_midnightTimer == null)
        {
            _midnightTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _midnightTimer.Tick += (_, _) =>
            {
                RefreshDatesIfTheDayChanged();
                ScheduleMidnight();
            };
        }
        _midnightTimer.Stop();
        var now = DateTime.Now;
        _midnightTimer.Interval = now.Date.AddDays(1).AddSeconds(5) - now;
        _midnightTimer.Start();
    }

    /// <summary>
    /// When the local day changed since the short times were said (the window stayed open past
    /// midnight): Home's "Last checked", For IT (rows and Recent runs) and the open path's rows are
    /// built again, so yesterday's run never reads "today". The focus stays where it is.
    /// </summary>
    internal void RefreshDatesIfTheDayChanged()
    {
        var today = DateTime.Now.Date;
        if (today == _datesDay) { return; }
        _datesDay = today;
        if (_healthCheckedUtc is DateTime checkedUtc) { LastCheckedText.Text = "Last checked " + ToolRuns.ShortWhen(checkedUtc, TimeZoneInfo.Local, DateTime.UtcNow); }
        if (_page == Page.It) { ShowIt(); }
        if (_page == Page.Problem) { ShowProblem(_problem, focus: false); }
    }

    /// <summary>The four group tiles: each with its tools' summary in words.</summary>
    private void ShowTiles()
    {
        // A tile sums up the last 24 hours only, as the tool rows do: an older result may no longer be true.
        var recent = ToolRuns.Recent(_toolRuns, DateTime.UtcNow);
        foreach (var pair in _tiles)
        {
            var group = pair.Value;
            // A group whose tools are all switched off shows no tile.
            pair.Key.Visibility = ToolCatalog.GroupOffered(group, _switchedOff) ? Visibility.Visible : Visibility.Collapsed;
            var summary = ToolRuns.GroupSummary(group, recent, _switchedOff);
            var results = ToolCatalog.InGroup(group, _switchedOff).Where(t => recent.ContainsKey(t.Button)).Select(t => recent[t.Button].Result).ToList();
            if (FindName(pair.Key.Name + "Summary") is TextBlock line)
            {
                line.Text = summary;
                line.Foreground = results.Contains(RunVerdict.Problem) ? ThemeBrush.For(Tone.Error)
                    : results.Contains(RunVerdict.Check) ? ThemeBrush.For(Tone.Warning)
                    : ThemeBrush.Get("TextMuted");
            }
            AutomationProperties.SetName(pair.Key, ToolCatalog.GroupName(group) + ", " + summary.Replace(" · ", ", "));
            AutomationProperties.SetHelpText(pair.Key, "Shows the tools of this group: " + string.Join(", ", ToolCatalog.InGroup(group, _switchedOff).Select(t => t.Name)) + ".");
        }
    }

    /// <summary>The tools of the group shown, or the tools the search found, best first; each with how its last run ended.</summary>
    private void ShowToolList()
    {
        var searching = Searching;
        var tools = searching ? ToolCatalog.Search(ToolSearch.Text, _switchedOff) : ToolCatalog.InGroup(_itGroup, _switchedOff);
        ToolListHeading.Text = searching ? "Tools found" : ToolCatalog.GroupName(_itGroup);
        // A row rebuilt while it had the keyboard focus keeps it (its last run changed).
        var kept = RowFocus(ToolList);
        ToolList.ItemsSource = tools.Select(t => NewToolRow(t, searching, selected: false)).ToList();
        if (kept.Tool != null) { FocusToolRow(kept.Tool, kept.Run); }
    }

    /// <summary>Each tool's newest run from the run history, for For IT and the problem paths' rows; returns the history's logs.</summary>
    private List<StoredRunLog> LoadToolRuns()
    {
        List<StoredRunLog> logs;
        try { logs = RunLogStore.List(RunsFolderOverride ?? UserFiles.RunsFolder()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logs = new List<StoredRunLog>();
        }
        _toolRuns = ToolRuns.Latest(logs, ToolRuns.Read, path => ToolRuns.ReadRun(path).Outcome);
        return logs;
    }

    /// <summary>A tool's row: its last run, and its Run button bound to the tool's own button.</summary>
    private ToolRowView NewToolRow(ToolInfo tool, bool showGroup, bool selected)
        => new(tool, _toolRuns.TryGetValue(tool.Button, out var run) ? run : null, showGroup, selected, TimeZoneInfo.Local, DateTime.UtcNow, FindName(tool.Button) as Button, Unavailable(tool));

    /// <summary>Why a tool cannot run on this PC ("Not available: Word is not installed on this PC"), or null.</summary>
    private string Unavailable(ToolInfo tool)
        => tool?.Office is OfficeApp app && !OfficeInstalled(app) ? OfficeSafeMode.NotInstalledText(app)
            : tool?.NeedsOneDrive == true && !OneDriveInstalled() ? OneDriveReset.NotInstalledText
            : tool?.Button == RetryTool.Button && ImeMissing() ? RetryTool.ImeMissingText : null;

    /// <summary>The health checks found no app installer (IME): no Retry work apps and settings then, as on the app path.</summary>
    private bool ImeMissing() => _healthRows.Any(r => r.ImeNotInstalled);

    /// <summary>The Retry tool's sentence on its detail page, from the diagnosis in this window; hidden on every other tool and without the IME.</summary>
    private void ShowRetryToolSummary()
    {
        var shown = _itTool?.Button == RetryTool.Button && !ImeMissing();
        RetryToolSummary.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (shown) { RetryToolSummaryText.Text = RetryTool.Summary(_diagnosis, TimeZoneInfo.Local, DateTime.UtcNow); }
    }

    /// <summary>Show the apps on the Retry tool's page: the app path, which reads the apps when this window has not yet.</summary>
    private void OnRetryToolApps(object sender, RoutedEventArgs e)
    {
        _appsFromRetryTool = true;
        try { OpenAppPath(); }
        finally { _appsFromRetryTool = false; }
    }

    /// <summary>The row of a list that has the keyboard focus, and whether on its Run button; no tool when none has it.</summary>
    private static (ToolInfo Tool, bool Run) RowFocus(ItemsControl list)
        => list.IsKeyboardFocusWithin && Keyboard.FocusedElement is FrameworkElement { DataContext: ToolRowView row } focused ? (row.Tool, focused.Name == "RowRun") : (null, false);

    /// <summary>
    /// A tool's Run (or Open) button on its row: it presses the tool's own button on its detail
    /// page, so the same handler, confirmations, rules and help texts apply. The run's status shows
    /// below the list (one column at every size).
    /// </summary>
    private void OnToolRowRun(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ToolRowView row || row.RunTarget == null) { return; }
        _pressedRowRun = sender as Button;
        ToolInvoker(row.RunTarget);
        // Only the tool this click started may use it (a safe-mode start reads it before its first wait).
        _pressedRowRun = null;
    }

    /// <summary>
    /// The chosen tool's detail page: its group, name and line, the facts that say something,
    /// its own button (the others are collapsed), and its last run with a link to its log.
    /// </summary>
    private void ShowToolDetail()
    {
        var tool = _itTool;
        ToolDetailGroup.Text = "For IT support · " + ToolCatalog.GroupName(tool.Group);
        ToolDetailTitle.Text = tool.Name;
        ToolDetailLine.Text = tool.Line;
        // No admin-rights fact; the time only from a minute, what it closes or changes only when it does.
        var facts = ToolCatalog.Facts(tool);
        FactTime.Text = tool.Time;
        FactChanges.Text = tool.Changes;
        ShowRetryToolSummary();
        FactTimePanel.Visibility = tool.Time.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FactChangesPanel.Visibility = tool.Changes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolFacts.Columns = Math.Max(1, facts.Count);
        ToolFacts.Visibility = facts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var other in ToolCatalog.All)
        {
            if (FindName(other.Button) is Button button) { button.Visibility = ReferenceEquals(other, tool) ? Visibility.Visible : Visibility.Collapsed; }
        }
        _toolRuns.TryGetValue(tool.Button, out var run);
        var unavailable = Unavailable(tool);
        if (unavailable != null && run == null)
        {
            // A customer without Office gets no Office action: the tool says why.
            ToolLastRunText.Text = unavailable + ".";
        }
        else if (tool.OpensOnly)
        {
            ToolLastRunText.Text = "This tool opens a folder; it leaves no run.";
        }
        else if (run == null)
        {
            ToolLastRunText.Text = "It has not run in your history yet, or not since the newest " + RunLogStore.KeepFiles.ToString(CultureInfo.InvariantCulture) + " runs.";
        }
        else
        {
            // No reference here; it stays in View details, the run log and Copy details for IT.
            var word = !string.IsNullOrEmpty(run.Outcome) ? run.Outcome : ToolRuns.Word(run.Result);
            ToolLastRunText.Text = (word.Length > 0 ? word + " · " : "") + AppDecisions.When(run.Log.StartedUtc ?? run.Log.WrittenUtc);
        }
        ToolLastRunText.Foreground = ThemeBrush.For(run == null ? Tone.Neutral : ToolRuns.ToneOf(run.Result));
        ToolLastRunLink.Tag = run?.Log.Path;
        ToolLastRunLink.Visibility = run == null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(ToolLastRunLink, "Open the log of the last run of " + tool.Name);
    }

    /// <summary>
    /// Which parts of For IT support show (the same at every size): one view at a time (the
    /// tiles with the report, the package and the recent runs; a list; or a tool's detail in place),
    /// with Back except on the tiles, as on a problem path.
    /// </summary>
    private void ApplyItLayout()
    {
        var searching = Searching;
        var showList = ToolList.Items.Count > 0;
        var view = searching && _itView == ItView.Tiles ? ItView.List : _itView;
        ItBackBar.Visibility = view == ItView.Tiles ? Visibility.Collapsed : Visibility.Visible;
        CategoryTiles.Visibility = view == ItView.Tiles ? Visibility.Visible : Visibility.Collapsed;
        ToolListPanel.Visibility = view == ItView.List && showList ? Visibility.Visible : Visibility.Collapsed;
        ToolDetail.Visibility = view == ItView.Detail ? Visibility.Visible : Visibility.Collapsed;
        ItExtras.Visibility = view == ItView.Tiles ? Visibility.Visible : Visibility.Collapsed;
        // The Back bar names the list or the group already.
        var back = view == ItView.Detail ? (searching ? "the tools found" : ToolCatalog.GroupName(_itGroup)) : "For IT support";
        ItPathText.Text = view == ItView.Detail ? ToolCatalog.GroupName(_itTool.Group) : searching ? "Tools found" : ToolCatalog.GroupName(_itGroup);
        AutomationProperties.SetName(ItBackButton, "Back to " + back);
        PlaceStatus();
        ApplyHelpers();
    }

    /// <summary>A group tile opens the group's tools in place, with the focus on the first one.</summary>
    private void OnCategoryTile(object sender, RoutedEventArgs e)
    {
        if (sender is not Button tile || !_tiles.TryGetValue(tile, out var group)) { return; }
        _itGroup = group;
        _listFromSearch = false;
        ToolSearch.Text = "";
        _itView = ItView.List;
        ShowIt();
        _focusIntent = "list";
        Dispatcher.BeginInvoke(new Action(() => FocusFirst(ToolList)), DispatcherPriority.Loaded);
    }

    /// <summary>A tool row: its detail page in place, with the focus on its title.</summary>
    private void OnToolRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ToolRowView row) { return; }
        // From a problem path, For IT opens on the tool in its group, not on an earlier search.
        if (_page != Page.It) { ToolSearch.Text = ""; }
        // From a problem page the detail says which problem suggested it, not the tab's intro.
        _toolSuggestedFor = _page != Page.It ? PathTitle() : null;
        OpenTool(row.Tool, focusTitle: true);
    }

    /// <summary>Shows a tool's detail page in For IT.</summary>
    private void OpenTool(ToolInfo tool, bool focusTitle)
    {
        _itTool = tool;
        if (!Searching) { _itGroup = tool.Group; }
        _itView = ItView.Detail;
        if (_page != Page.It) { ShowPage(Page.It, focus: false); } else { ShowIt(); }
        if (!focusTitle) { return; }
        _focusIntent = "title";
        Dispatcher.BeginInvoke(new Action(() => ToolDetailTitle.Focus()), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The keyboard focus on a tool's row (its name) or on the row's Run button, in the list
    /// of the page shown (For IT, or a problem path), once it is laid out.
    /// </summary>
    private void FocusToolRow(ToolInfo tool, bool run = false)
    {
        _focusIntent = (run ? "rowrun:" : "row:") + tool.Button;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var list = _page == Page.It ? ToolList : tool.Heavy ? ProblemHeavyTools : ProblemTools;
            list.UpdateLayout();
            for (var i = 0; i < list.Items.Count; i++)
            {
                if (list.Items[i] is ToolRowView row && ReferenceEquals(row.Tool, tool) && list.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter container
                    && container.ContentTemplate?.FindName(run ? "RowRun" : "RowName", container) is UIElement button)
                {
                    button.Focus();
                    return;
                }
            }
        }), DispatcherPriority.Loaded);
    }

    private void OnItBack(object sender, RoutedEventArgs e) => ItBack();

    /// <summary>
    /// Back in For IT support: from a tool's detail to its list, with the focus on its row; from a
    /// list to the tiles, with the focus on the group's tile (or on the search, after a search).
    /// False when there is nothing to go back to.
    /// </summary>
    private bool ItBack()
    {
        if (_page != Page.It) { return false; }
        if (_itView == ItView.Detail)
        {
            var tool = _itTool;
            _itView = ItView.List;
            ShowIt();
            FocusToolRow(tool);
            return true;
        }
        if (_itView != ItView.List && !Searching) { return false; }
        var wasSearch = Searching;
        var tile = _tiles.First(t => t.Value == _itGroup).Key;
        _itView = ItView.Tiles;
        _listFromSearch = false;
        ToolSearch.Text = "";
        ShowIt();
        _focusIntent = wasSearch ? "search" : "tile:" + _itGroup;
        Dispatcher.BeginInvoke(new Action(() => (wasSearch ? (UIElement)ToolSearch : tile).Focus()), DispatcherPriority.Loaded);
        return true;
    }

    /// <summary>Back on any page (Alt+Left, Backspace, Alt+B in For IT): a problem path, or For IT's list or detail.</summary>
    private bool GoBack()
    {
        if (_page == Page.Home && _showAllHealth)
        {
            OnHealthBack(this, new RoutedEventArgs());
            return true;
        }
        if (_page is Page.Apps or Page.Problem or Page.Windows or Page.Ip or Page.NotListed)
        {
            OnPathBack(this, new RoutedEventArgs());
            return true;
        }
        return ItBack();
    }

    /// <summary>
    /// The window's keys: Ctrl+F and Ctrl+K go to the tool search; Alt+Left, and Backspace
    /// outside a text box, go back; Alt+B goes back in For IT as on a problem path.
    /// </summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        // On the app path with its search shown, Ctrl+F searches the apps.
        if (modifiers == ModifierKeys.Control && key == Key.F && _page == Page.Apps && (_diagnosis?.Apps?.Count ?? 0) >= AppDecisions.SearchFrom)
        {
            if (AppSearchBox.Visibility == Visibility.Visible)
            {
                AppSearch.Focus();
                AppSearch.SelectAll();
            }
            else { OpenAppSearch(); }
            e.Handled = true;
            return;
        }
        // Alt+H too, also while the search label (its access key) is hidden behind typed text.
        if ((modifiers == ModifierKeys.Control && key is Key.F or Key.K) || (modifiers == ModifierKeys.Alt && key == Key.H))
        {
            FocusToolSearch();
            e.Handled = true;
            return;
        }
        var back = (modifiers == ModifierKeys.Alt && key == Key.Left)
            || (modifiers == ModifierKeys.None && key == Key.Back && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
            || (modifiers == ModifierKeys.Alt && key == Key.B && _page == Page.It);
        if (back && GoBack()) { e.Handled = true; }
    }

    /// <summary>For IT with the keyboard focus in its search, its text selected.</summary>
    private void FocusToolSearch()
    {
        if (_page != Page.It) { ShowPage(Page.It, focus: false); }
        _focusIntent = "search";
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ToolSearch.Focus();
            ToolSearch.SelectAll();
        }), DispatcherPriority.Loaded);
    }

    /// <summary>How many of the newest runs For IT lists; All runs opens the folder with every one.</summary>
    internal const int RecentRunsShown = 3;

    /// <summary>The newest runs of the run history with how each ended, each opening its log.</summary>
    private void ShowRecentRuns(List<StoredRunLog> logs)
    {
        var runs = logs.Take(RecentRunsShown).Select(l =>
        {
            var (result, outcome) = ToolRuns.ReadRun(l.Path);
            return new RecentRunView(l, TimeZoneInfo.Local, result, outcome);
        }).ToList();
        RecentRuns.ItemsSource = runs;
        RecentRuns.Visibility = runs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoRecentRuns.Visibility = runs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenRun(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) { OpenRunLog(path, null); }
    }

    /// <summary>Opens a run log of the history; a message about it belongs to <paramref name="tool"/>, or to none.</summary>
    private void OpenRunLog(string path, ToolInfo tool)
    {
        if (!File.Exists(path))
        {
            SetStatusTool(tool);
            SetStatus("That run log is no longer there: the run history keeps the newest 50 logs.", Tone.Warning);
            if (_page == Page.It) { ShowIt(); }
            return;
        }
        if (OpenTextFile(path) == TextFileOpened.No)
        {
            SetStatusTool(tool);
            SetStatus("The run log could not be opened. It is saved at " + path, Tone.Error);
        }
    }

    /// <summary>The log of the shown tool's last run, as a recent run opens it.</summary>
    private void OnOpenToolRun(object sender, RoutedEventArgs e)
    {
        // A message about this log (it is gone, or could not be opened) belongs to the tool shown.
        if ((sender as FrameworkElement)?.Tag is string path) { OpenRunLog(path, _itTool); }
    }

    private void OnOpenRunsFolder(object sender, RoutedEventArgs e)
    {
        var folder = UserFiles.RunsFolder();
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(ExplorerPath, "\"" + folder + "\"")?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            SetStatusTool(null);
            SetStatus("The runs folder could not be opened: " + ex.Message, Tone.Error);
        }
    }

    /// <summary>
    /// The tool search: it finds every tool whose words start with the query, across the
    /// groups, and says how many; narrow, typing shows the tools found in place of the tiles, and
    /// clearing the search brings the tiles back.
    /// </summary>
    private void OnToolSearch(object sender, TextChangedEventArgs e)
    {
        var query = ToolSearch.Text ?? "";
        var found = ToolCatalog.Search(query, _switchedOff);
        var searching = query.Trim().Length > 0;
        if (searching && _itView != ItView.List)
        {
            _itView = ItView.List;
            _listFromSearch = true;
        }
        else if (!searching && _listFromSearch)
        {
            _itView = ItView.Tiles;
            _listFromSearch = false;
        }
        ToolSearchResult.Text = !searching
            ? ""
            : found.Count == 0
                ? "No tool matches \"" + query.Trim() + "\"."
                : Words.Count(found.Count, "tool") + " found: " + string.Join(", ", found.Select(t => t.Name)) + ".";
        // No empty line under the search; the label is the placeholder while the box is empty.
        ToolSearchResult.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        ToolSearchLabel.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        if (_page != Page.It) { return; }
        ShowTiles();
        ShowToolList();
        ApplyItLayout();
    }

    /// <summary>Enter opens the first tool found; Esc clears the search.</summary>
    private void OnToolSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Searching && ToolCatalog.Search(ToolSearch.Text, _switchedOff).FirstOrDefault() is ToolInfo first)
        {
            _toolSuggestedFor = null;
            OpenTool(first, focusTitle: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && ToolSearch.Text.Length > 0)
        {
            ToolSearch.Text = "";
            e.Handled = true;
        }
    }

    /// <summary>
    /// The footer sits below the scrolling area, so it is as wide as the main column only when it
    /// leaves out the scroll bar's width too.
    /// </summary>
    private void OnMainScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange == 0 && e.ExtentHeightChange == 0) { return; }
        ApplyContentWidth();
    }

    /// <summary>
    /// One readable width for every page (ContentMaxWidth); a wider window centers the column
    /// and only adds margin. The tabs and the footer follow it, leaving out the scroll bar's width.
    /// </summary>
    private void ApplyContentWidth()
    {
        var readable = (double)FindResource("ContentMaxWidth");
        ContentColumn.MaxWidth = readable;
        // The column is centered in a wider window (a maximized one), not kept at the left with empty space beside it.
        ContentGrid.MaxWidth = readable;
        FooterGrid.MaxWidth = readable;
        TabsGrid.MaxWidth = readable;
        var viewport = MainScroll.ViewportWidth > 0 ? MainScroll.ViewportWidth : double.PositiveInfinity;
        FooterColumn.MaxWidth = Math.Min(readable, viewport);
        TabsColumn.MaxWidth = Math.Min(readable, viewport);
    }

    /// <summary>
    /// The status of a run sits in a tool's detail page while that page shows, else below the
    /// page. The detail page shows only the status of its own tool's run (or message); any other
    /// status is not shown there.
    /// </summary>
    private void PlaceStatus()
    {
        ShowStatusForPage();
        var onDetail = _page == Page.It && ToolDetail.Visibility == Visibility.Visible;
        var own = onDetail && _statusTool != null && ReferenceEquals(_statusTool, _itTool);
        var host = own ? DetailStatusHost : MainStatusHost;
        MainStatusHost.Visibility = onDetail && !own ? Visibility.Collapsed : Visibility.Visible;
        if (ReferenceEquals(StatusPanel.Parent, host)) { return; }
        var focused = StatusPanel.IsKeyboardFocusWithin ? Keyboard.FocusedElement as UIElement : null;
        if (StatusPanel.Parent is Border old) { old.Child = null; }
        host.Child = StatusPanel;
        if (focused != null) { Dispatcher.BeginInvoke(new Action(() => focused.Focus()), DispatcherPriority.Loaded); }
    }

    // The page the status belongs to (the page a run was started from), the page the work in
    // progress was started from, and whether the status area has something to show.
    private string _statusPage;
    private string _runPage;
    private bool _statusWanted;

    /// <summary>The page shown, as the status remembers it: one problem path is not another ("Problem:CantConnect").</summary>
    private string PageKey() => _page == Page.Problem ? "Problem:" + _problem : _page.ToString();

    /// <summary>
    /// A tool's result stays with the page it ran on: shown there (also when the user comes
    /// back), and not on Choose a problem or any other page; the run itself stays in the history.
    /// While something runs its status shows on every page, so its progress is never hidden.
    /// </summary>
    private void ShowStatusForPage()
    {
        var running = _busy || _following;
        var here = _statusPage == null || _statusPage == PageKey();
        StatusPanel.Visibility = _statusWanted && (running || here) ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- The mascot ---------------------------------------------------------------

    private bool HelpersAllowed => HelperRules.Visible(_highContrast);

    // The window is shown, as a test sets it (a test's window is never shown); null reads IsVisible.
    internal bool? WindowShownOverride { get; set; }

    /// <summary>
    /// The one mascot, the same on every page (its place and size are the layout's): its mood from
    /// the app's state (<see cref="HelperRules.ForPage"/>), still and paused as the conditions say:
    /// Windows' animation effects off, high contrast (which also hides it), or the window minimized or
    /// not visible. Hidden only in high contrast (no setting turns it off).
    /// </summary>
    private void ApplyHelpers()
    {
        var allowed = HelpersAllowed;
        var now = DateTime.UtcNow;
        Mascot.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        var missing = _buttons.ServiceMissing;
        var running = _busy || _following || _checkingHealth || (_page == Page.Home && _healthRows.Count == 0 && !missing);
        var problem = PageShowsProblem();
        var ranJustNow = now < _runHelperUntilUtc;
        // The health checks just ended: a happy bounce when the page shows nothing wrong.
        var checkedJustNow = !ranJustNow && now < _doneUntilUtc && !problem;
        Mascot.Mood = HelperRules.ForPage(running, ranJustNow || checkedJustNow, ranJustNow ? _statusTone : Tone.Success, missing, problem);
        Mascot.HighContrast = _highContrast;
        Mascot.Paused = !(WindowShownOverride ?? IsVisible) || WindowState == WindowState.Minimized;
        Mascot.AnimationsEnabled = HelperRules.Animate(WindowsAnimations ?? SystemParameters.ClientAreaAnimation);
        // One one-shot timer brings the page's own mood back when the moment after a run or check is over.
        var until = _runHelperUntilUtc > _doneUntilUtc ? _runHelperUntilUtc : _doneUntilUtc;
        _helperTimer.Stop();
        if (now < until)
        {
            _helperTimer.Interval = until - now + TimeSpan.FromMilliseconds(50);
            _helperTimer.Start();
        }
    }

    /// <summary>The page shown has a problem on it: Home's finding, a path's verdict, or the app path's main finding.</summary>
    private bool PageShowsProblem() => _page switch
    {
        Page.Home => _healthRows.Count > 0 && _home?.Tone is Tone.Warning or Tone.Error,
        Page.Problem => ProblemPaths.Verdict(ProblemPaths.Get(_problem), _healthRows).Tone is Tone.Warning or Tone.Error,
        Page.Apps => _diagnosis?.Main?.Severity is FindingSeverity.Warning or FindingSeverity.Error,
        _ => false,
    };

    /// <summary>Windows animations or high contrast changed: the helpers follow at once.</summary>
    private void OnSystemParameterChanged(object sender, PropertyChangedEventArgs e)
    {
        // The work area changed (the taskbar moved, or the resolution changed): the window is fitted again.
        if (e.PropertyName == nameof(SystemParameters.WorkArea))
        {
            Dispatcher.BeginInvoke(new Action(ScheduleRefit));
            return;
        }
        if (e.PropertyName is not (nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))) { return; }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _highContrast = SystemParameters.HighContrast;
            ApplyHelpers();
        }));
    }

    /// <summary>
    /// The link under a health row: the same handler as the button it names, so the same
    /// confirmation and the same checks apply.
    /// </summary>
    private void OnHealthFix(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HealthFix fix) { RaiseFix(fix); }
    }

    /// <summary>A health fix: its tool's own button, which follows its rules (nothing while something runs, no repair without the service).</summary>
    private void RaiseFix(HealthFix fix)
    {
        if (FixButton(fix) is Button target) { RaiseTool(target); }
    }

    private void OnDetails(object sender, RoutedEventArgs e)
    {
        if (_drawerOpen) { CloseDrawer(); } else { OpenDrawer(); }
    }

    // --- The tools -------------------------------------------------------------

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\IntuneManagementExtension\Logs");
        if (!Directory.Exists(folder))
        {
            LogsMessage("The IME Logs folder does not exist on this device.", Tone.Warning);
            return;
        }
        try { Process.Start(ExplorerPath, "\"" + folder + "\""); }
        catch (Win32Exception ex) { LogsMessage("The IME Logs folder could not be opened: " + ex.Message, Tone.Error); }
    }

    /// <summary>A message of Open IME logs: it belongs to that tool, so its detail page shows it.</summary>
    private void LogsMessage(string text, Tone tone)
    {
        SetStatusTool(ToolCatalog.ByButton(nameof(LogsButton)));
        SetStatus(text, tone);
    }

    private void OnClearCache(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        if (!MessageDialog.Confirm(this, ActionText.Name(ToolbeltAction.ClearAppCache), Confirmations.Text(ToolbeltAction.ClearAppCache), NetworkWarning(ResetTool.AppContent), "C_lear content")) { return; }
        var run = await RunActionAsync(ToolbeltAction.ClearAppCache, true, -1, "Clearing downloaded app content...");
        if (run != null) { await FinishRunAsync(run); }
    });

    /// <summary>The routes Check Microsoft connections uses (direct and the two proxies); a test replaces them.</summary>
    internal Func<IProxyRoutes> ConnectionRoutes { get; set; } = () => new SystemHealthSource();

    /// <summary>
    /// Check Microsoft connections: one card per target, in the same words as Copy details for IT, Copy
    /// summary and the run log (one line per route); the history line carries the run ID and the verdict.
    /// </summary>
    private void OnConnectivity(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        SetStatus("Checking the Microsoft connections...", Tone.Neutral);
        ClearResult();
        StartCheck(ConnectivityTest.Name);
        var runId = _shownCheck.RunId;
        var routes = ConnectionRoutes();
        Func<string, int, int, ProbeAnswer> connect = routes is IDirectRoute direct ? direct.Connect : (_, _, _) => ProbeAnswer.Failed(RouteOutcome.Unknown);
        var result = await Task.Run(() => ConnectivityTest.Run(connect, routes, runId));
        var contact = ManagementContact.From(_status);
        foreach (var line in result.Lines) { AppendLog(line); }
        if (ConnectivityTest.ContactLine(runId, contact) is string seen) { AppendLog(seen); }
        var (status, tone, cards) = ConnectivityTest.Present(result, contact, TimeZoneInfo.Local);
        SetStatus(status, tone);
        foreach (var card in cards) { AddCard(card); }
        // A read-only check: its answer on the page, also when all is well.
        ShowAnswer(status);
        // An inspecting proxy's certificate authority goes to Copy details for IT (and the log) only.
        _connectionTicket = (runId, ConnectivityTest.TicketLine(result));
        FinishCheck();
        _focusResultAtEnd = true;
        History.Add(ConnectivityTest.HistoryName, ConnectivityTest.HistoryResult(result), HistoryOverride);
        await RefreshAfterToolAsync(ToolCatalog.ByButton(nameof(ConnectivityButton)));
    });

    /// <summary>
    /// Check compliance settings. Reads the health checks again (the settings come from the
    /// service's readouts, nothing new is probed), then shows each item that is not OK with its one
    /// step and what this device cannot know. The run log, the history line and Copy details for IT carry
    /// item names and states only: no device or user name, no address, no path.
    /// </summary>
    private void OnComplianceCheck(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        SetStatus("Reading the compliance settings...", Tone.Neutral);
        ClearResult();
        StartCheck(CompliancePath.CheckName);
        var runId = _shownCheck.RunId;
        if (HealthAfterTools) { await RefreshHealthAsync(keepStatus: true); }
        var outcome = CompliancePath.Check(CompliancePath.Rows(_health, _buttons.ServiceMissing ? null : _status), runId, DateTime.UtcNow);
        foreach (var line in outcome.LogLines) { AppendLog(line); }
        SetStatus(outcome.Status, outcome.Tone);
        foreach (var card in outcome.Cards) { AddCard(card); }
        // A read-only check: its answer on the page, also when all is well.
        ShowAnswer(outcome.Status);
        FinishCheck();
        _focusResultAtEnd = true;
        History.Add(CompliancePath.HistoryName, CompliancePath.HistoryResult(outcome, runId), HistoryOverride);
    });

    private void OnResetPortal(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        if (!MessageDialog.Confirm(this, "Reset Company Portal data", Confirmations.CompanyPortalReset, NetworkWarning(ResetTool.CompanyPortal), "_Reset Company Portal")) { return; }
        SetStatus("Resetting Company Portal...", Tone.Neutral);
        ClearResult();
        StartCheck("Reset Company Portal data");
        // An open Company Portal is closed only after the user says so, and never by force.
        int session;
        using (var self = Process.GetCurrentProcess()) { session = self.SessionId; }
        bool ConfirmClose() => Dispatcher.Invoke(() =>
        {
            if (!MessageDialog.Confirm(this, Confirmations.CloseCompanyPortalHeading, Confirmations.CloseCompanyPortal, null, Confirmations.CloseCompanyPortalButton, destructive: true)) { return false; }
            SetStatus("Waiting for Company Portal to close (up to " + UserCaches.CloseWaitSeconds.ToString(CultureInfo.InvariantCulture) + " seconds)...", Tone.Neutral);
            return true;
        });
        var apps = PortalApps ?? new SessionApps(session);
        var (status, tone, card, code) = await Task.Run(() => CompanyPortalReset.Run(apps, ConfirmClose, line => Dispatcher.Invoke(() => AppendLog(line))));
        SetStatus(status, tone);
        AddCard(card);
        FinishCheck();
        _focusResultAtEnd = true;
        History.Add("ResetCompanyPortal", code.ToString(CultureInfo.InvariantCulture), HistoryOverride);
        await RefreshAfterToolAsync(ToolCatalog.ByButton(nameof(PortalButton)));
    });

    private void OnBackupKey(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        var run = await RunActionAsync(ToolbeltAction.BackupRecoveryKey, false, -1, "Backing up the BitLocker recovery key to Microsoft Entra ID...");
        if (run != null) { await FinishRunAsync(run); }
    });

    private void OnSyncClock(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        var run = await RunActionAsync(ToolbeltAction.SyncClock, false, -1, "Synchronising the clock...");
        if (run != null) { await FinishRunAsync(run); }
    });

    private void OnUpdateDefender(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        var run = await RunActionAsync(ToolbeltAction.UpdateDefender, false, -1, "Updating Microsoft Defender's security intelligence. This can take a few minutes.");
        if (run != null) { await FinishRunAsync(run); }
    });

    /// <summary>The read-only check of the Microsoft Defender log; no confirmation, it changes nothing.</summary>
    private void OnCheckDefender(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        var run = await RunActionAsync(ToolbeltAction.CheckDefenderBlocks, false, -1, "Reading what Microsoft Defender detected or blocked (read-only)...");
        if (run != null) { await FinishRunAsync(run); }
    });

    private void OnFreeSpace(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        UserTempCleanup.Result temp = null;
        await RunForAllSessionsAsync(ToolbeltAction.FreeDiskSpace, "_Free up space",
            "Free up disk space is running in the background (typically 10 to 30 minutes). You can close this window; it shows the result when you open it again.",
            // The user's own temporary folder, in the user's context, once the user said yes.
            async () => temp = await Task.Run(() => UserTempCleanup.Run(Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DateTime.UtcNow)));
        if (temp == null) { return; }
        var freed = string.Format(CultureInfo.InvariantCulture, "{0:0.#} MB", temp.BytesFreed / 1048576.0);
        var detail = temp.NotRun != null
            ? "Not cleaned: " + temp.NotRun + "."
            : Words.Count(temp.FilesRemoved, "file") + " removed (" + freed + ")" + "." + (temp.Skipped > 0 ? " " + Words.Count(temp.Skipped, "item") + " still in use or protected stayed; that is normal." : "");
        AddCard(ResultCard.From(temp.NotRun == null ? FindingSeverity.Ok : FindingSeverity.Info, "Your temporary files", detail, ""));
        AppendLog("Own temporary folder: " + detail);
    });

    // --- Sign-in and Microsoft 365, in the user's own context ---------------------------------

    private void OnRefreshSignIn(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        SetStatus("Renewing your work account sign-in. This takes up to a minute...", Tone.Neutral);
        ClearResult();
        StartCheck("Refresh work account sign-in");
        var outcome = await Task.Run(() => SignInRefresh.Run(() => DsregInfo.Parse(SystemHealthSource.RunDsregcmd()), SystemHealthSource.RunDsregcmdRefresh, s => System.Threading.Thread.Sleep(s * 1000)));
        ShowToolOutcome(outcome, "RefreshSignIn");
        // The work account row then says its new state; without that step (a test) the checks still run.
        if (HealthAfterTools) { await RefreshAfterToolAsync(ToolCatalog.ByButton(nameof(RefreshSignInButton))); }
        else { await RefreshHealthAsync(keepStatus: true); }
    });

    private void OnResetSignIn(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        if (!MessageDialog.Confirm(this, "Reset sign-in cache", Confirmations.SignInCacheReset, NetworkWarning(ResetTool.SignInCache), "_Reset sign-in cache")) { return; }
        ClearResult();
        StartCheck("Reset sign-in cache");
        int session;
        using (var self = Process.GetCurrentProcess()) { session = self.SessionId; }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Open Microsoft 365 apps are named and closed only after a second question, whose
        // default is Cancel; they are asked to close, never ended.
        bool ConfirmClose(IReadOnlyList<string> apps) => Dispatcher.Invoke(() =>
        {
            if (!MessageDialog.Confirm(this, Confirmations.CloseAppsHeading, Confirmations.CloseApps(apps), Confirmations.CloseAppsWarning, Confirmations.CloseAppsButton, destructive: true)) { return false; }
            SetStatus("Waiting for " + Words.List(apps) + " to close (up to " + UserCaches.CloseWaitSeconds.ToString(CultureInfo.InvariantCulture) + " seconds)...", Tone.Neutral);
            return true;
        });
        var outcome = await Task.Run(() => UserCaches.ResetSignInCache(local, profile, new SessionApps(session), ConfirmClose,
            line => Dispatcher.Invoke(() => AppendLog(line)), DateTime.UtcNow));
        ShowToolOutcome(outcome, "ResetSignInCache");
        await RefreshAfterToolAsync(ToolCatalog.ByButton(nameof(ResetSignInButton)));
    });

    private void OnTeamsCache(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        // The first question always says that a call or meeting ends; Cancel is its default.
        if (!ConfirmTeamsCache()) { return; }
        ClearResult();
        StartCheck("Clear Teams cache");
        SetStatus("Clearing the Teams cache...", Tone.Neutral);
        int session;
        using (var self = Process.GetCurrentProcess()) { session = self.SessionId; }
        var local = TeamsLocalAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = TeamsLocalAppData != null ? Path.GetDirectoryName(Path.GetDirectoryName(TeamsLocalAppData)) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var apps = TeamsApps ?? new SessionApps(session);
        var end = EndTeamsProcesses ?? (() => UserCaches.EndTeams(session));
        // The app never closes Teams on its own: while it runs, the user quits it and continues, or chooses End Teams for me.
        TeamsChoice Ask(bool again) => Dispatcher.Invoke(() =>
        {
            var choice = AskTeamsRunning(again);
            if (choice == TeamsChoice.EndForMe) { SetStatus("Ending Teams...", Tone.Neutral); }
            return choice;
        });
        var outcome = await Task.Run(() => UserCaches.ClearTeamsCache(local, profile, apps, end, Ask,
            line => Dispatcher.Invoke(() => AppendLog(line)), DateTime.UtcNow));
        ShowToolOutcome(outcome, "ClearTeamsCache");
        await RefreshAfterToolAsync(ToolCatalog.ByButton(nameof(TeamsCacheButton)));
    });

    /// <summary>The warning line before a reset's button when the Network to Intune row is not OK; null otherwise.</summary>
    internal string NetworkWarning(ResetTool tool) => ConnectionNeeds.Join(ConnectionNeeds.Warning(tool, _healthRows));

    /// <summary>The first question of Clear Teams cache; Cancel is its default. A test replaces it.</summary>
    internal Func<bool> ConfirmTeamsCache
    {
        get => _confirmTeamsCache ?? (() => MessageDialog.Confirm(this, "Clear Teams cache", Confirmations.TeamsCache, ConnectionNeeds.Join(Confirmations.TeamsCacheWarning, ConnectionNeeds.Warning(ResetTool.TeamsCache, _healthRows)), Confirmations.TeamsCacheButton, destructive: true));
        set => _confirmTeamsCache = value;
    }

    private Func<bool> _confirmTeamsCache;

    /// <summary>
    /// The question while Teams runs (its argument: Teams still ran after Continue): quit it yourself
    /// and Continue (the default), End Teams for me, or Cancel. A test replaces it.
    /// </summary>
    internal Func<bool, TeamsChoice> AskTeamsRunning
    {
        get => _askTeamsRunning ?? (again => MessageDialog.Choose(this, Confirmations.TeamsRunningHeading,
            again ? Confirmations.TeamsStillRunning : Confirmations.TeamsRunning, Confirmations.TeamsRunningWarning,
            Confirmations.TeamsContinueButton, Confirmations.TeamsEndForMeButton) switch
        {
            1 => TeamsChoice.Continue,
            2 => TeamsChoice.EndForMe,
            _ => TeamsChoice.Cancel,
        });
        set => _askTeamsRunning = value;
    }

    private Func<bool, TeamsChoice> _askTeamsRunning;

    /// <summary>Tests: a fake Teams process list, the step that ends Teams, and a profile of their own.</summary>
    internal ISessionApps TeamsApps { get; set; }

    internal Func<int> EndTeamsProcesses { get; set; }

    internal string TeamsLocalAppData { get; set; }

    /// <summary>The result of a tool that ran in the app: status, card, technical log, history.</summary>
    private void ShowToolOutcome(ToolOutcome outcome, string historyName)
    {
        SetStatus(outcome.Status, outcome.Tone);
        AddCard(outcome.Card);
        AppendLog(_shownCheck?.Name + ": " + outcome.Card.Title + (string.IsNullOrEmpty(outcome.Card.Detail) ? "" : " - " + outcome.Card.Detail));
        _focusResultAtEnd = true;
        History.Add(historyName, outcome.Code.ToString(CultureInfo.InvariantCulture), HistoryOverride);
        FinishCheck();
    }

    /// <summary>
    /// The ticket's line for a support package run: the zip's name and SHA-256, so IT support can
    /// tell that the file they receive is the one this run made. Null for any other run, or when
    /// the zip is gone or cannot be read.
    /// </summary>
    private string PackageTicketLine(RunOutcome run)
    {
        if (run?.Action != ToolbeltAction.CollectDiag || _lastPackagePath == null || !File.Exists(_lastPackagePath)) { return null; }
        try
        {
            using var stream = File.OpenRead(_lastPackagePath);
            using var hash = System.Security.Cryptography.SHA256.Create();
            return "Support package: " + Path.GetFileName(_lastPackagePath) + " (SHA-256 " + BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "") + ")";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Support package: " + Path.GetFileName(_lastPackagePath);
        }
    }

    /// <summary>
    /// "Copy details for IT": a short ticket text for the attempt shown, with its run ID, also while
    /// it runs; no device or user name and no log lines.
    /// </summary>
    private void OnCopyTicket(object sender, RoutedEventArgs e)
    {
        var header = ShownHeader();
        // A run whose log could not be saved: the ticket names no run log.
        var saved = _logNotSavedRunId == null || !string.Equals(_logNotSavedRunId, header.RunId ?? "", StringComparison.Ordinal);
        // Check Microsoft connections adds the inspecting proxy's certificate authority, when it saw one.
        // A Windows Update check adds one line with its facts (no server, path or name).
        var extra = _connectionTicket.Line != null && string.Equals(_connectionTicket.RunId, header.RunId ?? "", StringComparison.Ordinal) ? _connectionTicket.Line : null;
        if (extra == null && _shownRun is { Action: ToolbeltAction.CheckWindowsUpdate } shown)
        {
            lock (_liveLock) { extra = WuCheck.TicketLine(shown); }
        }
        // A Defender check adds one line with its counts (no file, rule or name).
        if (extra == null && _shownRun is { Action: ToolbeltAction.CheckDefenderBlocks } defender)
        {
            lock (_liveLock) { extra = DefenderCheck.TicketLine(defender); }
        }
        // A support package run adds the zip's name and SHA-256.
        extra ??= PackageTicketLine(header.Outcome);
        var text = ViewTicketText(header, extra, saved);
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        PutOnClipboard(data, CopyTicketButton, "Copy details for I_T", "Copy details for IT", "Details for IT copied", "The ticket text could not be copied: another program is using the clipboard. Try again.");
    }

    /// <summary>
    /// The ticket under View details for the run shown; with the Tried lines of the path that
    /// run belongs to, so it names the run as the path's Copy details for IT does.
    /// </summary>
    internal string ViewTicketText(RunLogHeader header = null, string extra = null, bool logSaved = true)
    {
        header ??= ShownHeader();
        return RunTicket.Text(header, _cards.ToList(), StatusForTicket(), _statusTone, extra, logSaved: logSaved, facts: TicketFacts(), tried: TriedLines(_caseFile.PathOf(header.RunId)));
    }

    /// <summary>The next step after Copy details for IT, said on the status line.</summary>
    internal const string PasteHint = "Paste it (Ctrl+V) into your message to IT support.";

    /// <summary>The status line as the run's result, without the paste hint (a ticket never carries it).</summary>
    private string StatusForTicket() => WithoutPasteHint(StatusText.Text);

    /// <summary>A status line without the paste hint.</summary>
    internal static string WithoutPasteHint(string status)
    {
        var text = status ?? "";
        return text.EndsWith(" " + PasteHint, StringComparison.Ordinal) ? text.Substring(0, text.Length - PasteHint.Length - 1) : text == PasteHint ? "" : text;
    }

    /// <summary>
    /// After Copy details for IT the status line says what to do with it. A result shown on this
    /// page keeps its words and tone and gets the step after it; without one, the line says it was copied.
    /// </summary>
    private void ShowPasteHint()
    {
        var shown = StatusPanel.Visibility == Visibility.Visible && !string.IsNullOrEmpty(StatusText.Text);
        if (!shown)
        {
            SetStatus("Copied. " + PasteHint, Tone.Neutral);
            return;
        }
        if (StatusText.Text.EndsWith(PasteHint, StringComparison.Ordinal)) { return; }
        var text = StatusText.Text + " " + PasteHint;
        StatusText.Text = text;
        DrawerStatusText.Text = text;
        StatusLabel.ToolTip = text;
        AutomationProperties.SetName(StatusLabel, text);
        Announce(StatusLabel);
    }

    /// <summary>
    /// Puts the data on the clipboard; the link says "Copied" for three seconds and the status line
    /// keeps the result. The focus stays on the link.
    /// </summary>
    private bool PutOnClipboard(DataObject data, Button link, string label, string name, string copiedName, string failure)
    {
        try
        {
            // Another program may hold the clipboard for a moment: a few short tries.
            for (var attempt = 1; ; attempt++)
            {
                try { Clipboard.SetDataObject(data, true); break; }
                catch (COMException) when (attempt < 5) { System.Threading.Thread.Sleep(100); }
            }
            link.Content = "Copied";
            AutomationProperties.SetName(link, copiedName);
            // Every Copy details for IT says the next step on the status line.
            if (copiedName == "Details for IT copied") { ShowPasteHint(); }
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) => { timer.Stop(); link.Content = label; AutomationProperties.SetName(link, name); };
            timer.Start();
            return true;
        }
        catch (COMException)
        {
            SetStatus(failure, Tone.Warning);
            return false;
        }
    }

    /// <summary>
    /// Copy details for IT goes with any run or check shown, also while it runs, so the ticket can
    /// name the attempt before its result is in. It is the one copy link: a second one for
    /// the same message left a user to choose.
    /// </summary>
    private void ShowCopyLinks()
    {
        CopyTicketButton.Visibility = _cards.Count > 0 || _shownRun != null || _shownCheck != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFullReset(object sender, RoutedEventArgs e) => Guarded(()
        => RunForAllSessionsAsync(ToolbeltAction.FullReset, "_Reset all app state", "Full IME reset is running. This takes several minutes.",
            mayRun: CloseCompanyPortalForFullResetAsync));

    /// <summary>
    /// Before the Full IME reset starts, an open Company Portal is closed only after the user
    /// says so, and never by force. Declined, or still open: the reset does not start, nothing is
    /// changed, and the status and the history say why. True when the reset may start.
    /// </summary>
    private async Task<bool> CloseCompanyPortalForFullResetAsync()
    {
        int session;
        using (var self = Process.GetCurrentProcess()) { session = self.SessionId; }
        bool ConfirmClose() => Dispatcher.Invoke(() =>
        {
            if (!MessageDialog.Confirm(this, Confirmations.CloseCompanyPortalHeading, Confirmations.CloseCompanyPortal, null, Confirmations.CloseCompanyPortalButton, destructive: true)) { return false; }
            SetStatus("Waiting for Company Portal to close (up to " + UserCaches.CloseWaitSeconds.ToString(CultureInfo.InvariantCulture) + " seconds)...", Tone.Neutral);
            return true;
        });
        var apps = PortalApps ?? new SessionApps(session);
        var lines = new List<string>();
        var result = await Task.Run(() => CompanyPortalClose.Ask(apps, ConfirmClose, lines.Add));
        if (result is PortalClose.NotOpen or PortalClose.Closed) { return true; }
        var name = ActionText.Name(ToolbeltAction.FullReset);
        ClearResult();
        _focusResultAtEnd = true;
        SetStatusTool(ToolCatalog.ByAction(ToolbeltAction.FullReset));
        if (result == PortalClose.Cancelled)
        {
            SetStatus(name + " was not started: Company Portal stayed open. Nothing was changed.", Tone.Neutral);
            AddCard(ResultCard.From(FindingSeverity.Info, "Nothing was changed", string.Join(Environment.NewLine, lines), "Close Company Portal when it suits you, then run " + name + " again."));
            History.Add(ToolbeltAction.FullReset.ToString(), Confirmations.FullResetPortalCancelled, HistoryOverride);
        }
        else
        {
            SetStatus(name + " was not started: Company Portal did not close. Nothing was changed.", Tone.Warning);
            AddCard(ResultCard.From(FindingSeverity.Warning, "Company Portal did not close", string.Join(Environment.NewLine, lines), "Close Company Portal yourself, then run " + name + " again."));
            History.Add(ToolbeltAction.FullReset.ToString(), Confirmations.FullResetPortalStillOpen, HistoryOverride);
        }
        return false;
    }

    /// <summary>The read-only Windows Update check; no confirmation, it changes nothing.</summary>
    private void OnCheckWu(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        var run = await RunActionAsync(ToolbeltAction.CheckWindowsUpdate, false, -1, "Checking Windows Update (read-only)...");
        if (run != null) { await FinishRunAsync(run); }
    });

    private void OnRepairWu(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        // Answered at once in the user's context instead of a round trip to the service.
        bool pending;
        try { pending = new SystemHealthSource().ServicingRestartPending(); } catch (Exception ex) when (ex is IOException or System.Security.SecurityException or UnauthorizedAccessException) { pending = false; }
        if (pending)
        {
            MessageDialog.Show(this, ActionText.Name(ToolbeltAction.RepairWU), Confirmations.RepairWuRestartPending);
            return;
        }
        await RunForAllSessionsAsync(ToolbeltAction.RepairWU, "_Start the repair",
            "Repair Windows Update is running in the background (typically 15 to 60 minutes). You can close this window; it shows the result when you open it again.");
    });

    /// <summary>
    /// An action that affects every signed-in session: the sessions are counted, the user
    /// confirms with that count, and the action runs. When the count changed before the service
    /// got the request, the service asks for a new confirmation: that answer is not shown; the app
    /// counts again and asks once more, saying that the number changed. A second change in a row
    /// is shown as it is.
    /// </summary>
    /// <param name="afterConfirm">Work the app does itself once the user said yes, before the service starts (Free up disk space cleans the user's own temporary folder).</param>
    /// <param name="confirmText">the confirmation's own text (Get a new IP address names the adapter); the action's text when null.</param>
    /// <param name="interfaceIndex">the adapter the user chose, for Get a new IP address only.</param>
    /// <param name="mayRun">asked once after the first confirmation; false: the action does not start (it told the user why).</param>
    private async Task RunForAllSessionsAsync(ToolbeltAction action, string confirmLabel, string runningText, Func<Task> afterConfirm = null, string confirmText = null, int interfaceIndex = 0,
        Func<Task<bool>> mayRun = null)
    {
        var name = ActionText.Name(action);
        for (var round = 0; round < 2; round++)
        {
            var sessions = await CheckSessionsAsync(action);
            if (sessions == null)
            {
                if (round > 0) { NotStarted(name); }
                return;
            }
            var warning = Confirmations.OtherSessionsWarning(action, sessions.Value);
            if (round > 0) { warning = Confirmations.SessionsChanged(sessions.Value) + (warning.Length > 0 ? " " + warning : ""); }
            // A reset that needs a connection afterwards warns when the Network to Intune row is not OK.
            if (action == ToolbeltAction.FullReset) { warning = ConnectionNeeds.Join(warning, NetworkWarning(ResetTool.FullImeReset)); }
            if (!MessageDialog.Confirm(this, name, confirmText ?? Confirmations.Text(action), warning, confirmLabel, destructive: true))
            {
                if (round > 0) { NotStarted(name); }
                return;
            }
            var thisRound = round;
            if (round == 0 && mayRun != null && !await mayRun()) { return; }
            if (round == 0 && afterConfirm != null) { await afterConfirm(); }
            var run = await RunActionAsync(action, true, sessions.Value, runningText, outcome => ResultPresenter.AskSessionsAgain(thisRound, outcome), interfaceIndex);
            if (run == null) { return; }
            if (run.View == null) { continue; }
            await FinishRunAsync(run);
            return;
        }
    }

    /// <summary>The second question was not answered with yes: nothing ran, and the window says so.</summary>
    private void NotStarted(string name)
    {
        ClearResult();
        _focusResultAtEnd = false;
        SetStatus(name + " was not started.", Tone.Neutral);
    }

    /// <summary>
    /// The number of other signed-in sessions, fresh from the service, for an action that affects
    /// everyone; null when the action cannot run now (the user has been told why).
    /// </summary>
    private async Task<int?> CheckSessionsAsync(ToolbeltAction action)
    {
        // The check's own status line goes away again (without being read out a second time), so
        // a declined confirmation changes nothing.
        var (beforeText, beforeTone, beforeTool) = (StatusText.Text, _statusTone, _statusTool);
        SetStatusTool(ToolCatalog.ByAction(action));
        SetStatus("Checking whether " + ActionText.Name(action) + " can run now...", Tone.Neutral);
        void Restore()
        {
            SetStatus(beforeText, beforeTone, announce: false);
            SetStatusTool(beforeTool);
        }
        StatusInfo status;
        try { status = await Task.Run(_gateway.GetStatus); }
        catch (ServiceException ex)
        {
            Restore();
            MessageDialog.Show(this, ActionText.Name(action), ex.Code == ResultCode.ServerIdentityMismatch
                ? "The connection did not come from the " + ProductInfo.DisplayName + " service, so nothing was started. Contact your IT support."
                : ex.Code == ResultCode.ProtocolMismatch
                    ? "The app and the service are different versions. Restart the device; if it repeats, " + ProductInfo.DisplayName + " needs to be reinstalled."
                    : "The " + ProductInfo.DisplayName + " service is not available. Restart the device and try again; if it repeats, contact your IT support.");
            return null;
        }
        _status = status;
        _switchedOff = SwitchedOffTools.From(status, SwitchedOffTools.ReadPolicy);
        Restore();
        string reason = null;
        if (ToolPolicy.IsOff(_switchedOff, action))
        {
            // Switched off since the window last looked; the service would refuse it too.
            reason = SwitchedOffText;
            UpdateButtons();
        }
        else if (status.RunningAction.HasValue)
        {
            reason = "Another action is still running (" + ActionText.Name(status.RunningAction.Value) + "). Wait until it has finished.";
        }
        else if (status.OtherSessions < 0)
        {
            reason = Confirmations.OtherSessionsWarning(action, -1);
        }
        else if (status.OtherSessions > 0 && !status.AllowMachineWideOnMultiSession)
        {
            reason = "Other people are signed in to this device, and your organization does not allow this action while they are. Try again when you are the only one signed in.";
        }
        if (reason != null)
        {
            MessageDialog.Show(this, ActionText.Name(action), reason);
            return null;
        }
        return status.OtherSessions;
    }

    // By full path: a bare name would also be looked up in the current folder.
    private static readonly string ExplorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>About: the version, a newer one when the feed named it, and the check box for the start-up notice.</summary>
    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var (on, locked) = UpdateCheck.Setting(_settings.UpdateCheckOn, _status?.UpdateCheck);
        // A policy decides for the organization: then About shows neither the line nor the check box.
        // While the device's value is unknown (no answer of the service) and the user chose nothing,
        // the box is not offered either: a tick would outrank an installer's "0" it cannot see.
        var option = UpdateCheck.OfferChoice(locked, _settings.UpdateCheckOn, _status?.UpdateCheck) ? UpdateCheck.CheckOption : null;
        // Check now looks at once, also with the check box cleared (the user asks for it); not when a
        // policy switched the check off. The same request as at start-up; its answer is kept as one.
        var chosen = MessageDialog.Show(this, AboutHeading, AboutBody(_version, on ? _update : null), AboutLinks, option, on,
            locked ? null : UpdateCheck.CheckNowButton, UpdateCheck.CheckingNow, locked ? null : CheckNowAsync,
            "Release notes", UpdateCheck.ReleaseNotesUrl, () => _update != null);
        if (option == null || chosen == on) { return; }
        _settings.UpdateCheckOn = chosen;
        _settings.Save(SettingsPath);
        // Off: no request any more and nothing shown; on: the next start looks again.
        if (!chosen) { ShowUpdate(null); }
    }

    /// <summary>About's Check now: one look at the feed now, kept as the latest answer; it says what it found.</summary>
    private async Task<string> CheckNowAsync()
    {
        var offer = await Task.Run(UpdateCheck.Fetch);
        if (offer != null)
        {
            _settings.UpdateCheckedUtc = DateTime.UtcNow;
            _settings.UpdateOffer = offer;
            _settings.Save(SettingsPath);
            _update = UpdateCheck.IsNewer(_version, offer) ? offer : null;
        }
        return UpdateCheck.CheckNowResult(_version, offer);
    }

    /// <summary>The About dialog's links beside OK: the publisher's site with all products and the source code. Each opens the browser only when clicked.</summary>
    internal static readonly IReadOnlyList<WebLink> AboutLinks = new[]
    {
        new WebLink("soulbit.app", "SoulBit products website", ProductInfo.ProductsUrl),
        new WebLink("GitHub", "Source code and releases on GitHub", ProductInfo.SourceUrl),
    };

    /// <summary>The About dialog's heading; its title bar adds the product's name ("Endpoint Toolbelt - About").</summary>
    internal const string AboutHeading = "About";

    /// <summary>
    /// The About dialog's text: the publisher line and version, the description (which says
    /// what the product is for, Intune only as an adjective), copyright and licence, the statement
    /// that Microsoft does not endorse it and the trademark notice. No compatibility sentence: "Works
    /// with Microsoft Intune" read as if it operated Intune.
    /// </summary>
    internal static string AboutBody(string version, UpdateOffer newer = null) => string.Join(Environment.NewLine,
        ProductInfo.NameWithPublisher + ", version " + version + (newer != null ? Environment.NewLine + UpdateCheck.AboutLine(newer) : ""),
        ProductInfo.Description + ".",
        "",
        "Copyright (c) 2026 " + ProductInfo.Publisher + ".",
        "Licensed under the MIT License.",
        "",
        ProductInfo.DisplayName + " is not affiliated with or endorsed by Microsoft.",
        ProductInfo.TrademarkNotice);

    private void OnSupportLink(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri?.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(e.Uri.UserInfo))
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (Win32Exception) { }
        }
        e.Handled = true;
    }

    // --- Window state ---------------------------------------------------------------------

    private void ShowBusy(bool busy)
    {
        _buttons.Busy = busy;
        UpdateButtons();
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        // The mascot shows the tone the run ended with for a few seconds, then rests.
        if (!busy) { _runHelperUntilUtc = DateTime.UtcNow.AddSeconds(HelperRules.EndedFor.TotalSeconds); }
        // Once nothing runs, a result shows only on the page it belongs to.
        ShowStatusForPage();
        ApplyHelpers();
    }

    /// <summary>
    /// Enables the buttons by the rules: nothing while something runs, and no repair while the
    /// service is missing. A repair button that is off for that reason says why (also on hover);
    /// only the tooltips set for that reason are taken away again.
    /// </summary>
    /// <summary>Why a tool the organization switched off cannot be used.</summary>
    internal const string SwitchedOffText = "Your organization has switched this tool off on this device.";

    private void UpdateButtons()
    {
        RefreshBringBack();
        RefreshRenewNow();
        // A safe-mode tool is off while its app is not installed, and Reset OneDrive while OneDrive is not; they say so.
        var notInstalled = new Dictionary<Button, string>();
        foreach (var app in OfficeSafeMode.Apps) { notInstalled[(Button)FindName(OfficeSafeMode.Button(app))] = OfficeInstalled(app) ? null : OfficeSafeMode.NotInstalledText(app); }
        notInstalled[ResetOneDriveButton] = OneDriveInstalled() ? null : OneDriveReset.NotInstalledText;
        notInstalled[RetryToolButton] = ImeMissing() ? RetryTool.ImeMissingText : null;
        foreach (var pair in _actionButtons)
        {
            var button = pair.Key;
            // A tool the organization switched off is off, so no shortcut, fix link or step starts it.
            var unavailable = ToolCatalog.ByButton(button.Name) is ToolInfo tool && !ToolCatalog.Offered(tool, _switchedOff)
                ? SwitchedOffText
                : notInstalled.TryGetValue(button, out var why) ? why : null;
            button.IsEnabled = _buttons.IsEnabled(pair.Value) && unavailable == null;
            var missing = _buttons.OffForService(pair.Value);
            AutomationProperties.SetHelpText(button, unavailable ?? (missing ? ButtonRules.ServiceMissing : _helpTexts[button] ?? ""));
            if (unavailable != null)
            {
                button.ToolTip = unavailable;
                ToolTipService.SetShowOnDisabled(button, true);
                _reasonToolTips.Add(button);
            }
            else if (missing)
            {
                button.ToolTip = ButtonRules.ServiceMissing;
                ToolTipService.SetShowOnDisabled(button, true);
                _reasonToolTips.Add(button);
            }
            else if (_reasonToolTips.Remove(button))
            {
                button.ClearValue(ToolTipProperty);
                button.ClearValue(ToolTipService.ShowOnDisabledProperty);
            }
        }
        RefreshStepButtons();
    }

    /// <param name="announce">Tell a screen reader; false for a status it has already heard or does not need.</param>
    /// <param name="show">
    /// Show the status area: a run, a check or a message. False for the health checks' own status,
    /// which Home's status card and health strip already say (the area is not a second copy).
    /// </param>
    private void SetStatus(string text, Tone tone, bool announce = true, bool show = true)
    {
        _statusTone = tone;
        StatusLabel.Foreground = ThemeBrush.For(tone);
        // The result in a word (OK, WARNING, PROBLEM) and one line; the whole text is the tooltip and in the drawer.
        var word = Words.ResultWord(tone);
        StatusWord.Text = word;
        StatusWord.Foreground = ThemeBrush.For(tone);
        StatusWordBadge.BorderBrush = ThemeBrush.For(tone);
        StatusWordBadge.Visibility = word.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        // The status belongs to the page the run was started from (a status set outside a run: the page shown).
        _statusWanted = show;
        if (show) { _statusPage = _busy || _following ? _runPage ?? PageKey() : PageKey(); }
        ShowStatusForPage();
        ApplyHelpers();
        ShowHandoverLine(text);
        // A result shows the name of what ran; while it runs, or without a run, the line itself.
        var label = StatusLabelText(text, tone);
        var changed = StatusText.Text != text || StatusLabel.Text != label;
        StatusLabel.Text = label;
        if (!changed) { return; }
        StatusText.Text = text;
        StatusLabel.ToolTip = string.IsNullOrEmpty(text) ? null : text;
        AutomationProperties.SetName(StatusLabel, text ?? "");
        DrawerStatusText.Text = text;
        if (announce) { Announce(StatusLabel); }
    }

    /// <summary>
    /// The short status line: the name of the run or check a result belongs to ("App diagnosis",
    /// "Clear Teams cache"), so the line never ends in the middle of a sentence; View details has the
    /// rest. A neutral line (a run in progress, a message without a run) is shown as it is.
    /// </summary>
    internal string StatusLabelText(string text, Tone tone)
    {
        if (tone == Tone.Neutral) { return text ?? ""; }
        var name = _shownCheck?.Name ?? (_shownRun != null ? ActionText.Name(_shownRun.Action) : null);
        return string.IsNullOrWhiteSpace(name) ? text ?? "" : name;
    }

    /// <summary>Tells a screen reader that a live region changed (WPF does not do this by itself).</summary>
    private static void Announce(UIElement element)
    {
        var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void ClearResult()
    {
        _resultGeneration++;
        _shownRun = null;
        _shownCheck = null;
        _cards = new List<ResultCard>();
        Cards.ItemsSource = null;
        Cards.Visibility = Visibility.Collapsed;
        ShowCopyLinks();
        _stepViews.Clear();
        _liveSteps = new List<StepRow>();
        _liveStepsNote = "";
        StepsNote.Visibility = Visibility.Collapsed;
        StepsPanel.Visibility = Visibility.Collapsed;
        LogBox.Clear();
        _shownLog.Clear();
        RestartBanner.Visibility = Visibility.Collapsed;
        // The health line and the safe-mode question belong to the result they came with.
        StatusHealthText.Text = "";
        StatusHealthText.Visibility = Visibility.Collapsed;
        StatusAnswerText.Text = "";
        StatusAnswerText.Visibility = Visibility.Collapsed;
        _safeMode = null;
        SafeModeQuestion.Visibility = Visibility.Collapsed;
        // So do "Does it work now?" and the line to IT support.
        _worksNowRunId = null;
        WorksNowQuestion.Visibility = Visibility.Collapsed;
        WorksNowAnswerText.Visibility = Visibility.Collapsed;
        ShowHandoverLine();
    }

    /// <summary>Shows the cards; a card that had the keyboard focus keeps it (by position).</summary>
    private void ShowCards()
    {
        var focused = FocusedCardIndex();
        // A note (nothing on this device was changed) is a muted line above the cards, never a card.
        var shown = _cards.Where(c => c.Kind != CardKind.Note).ToList();
        var note = _cards.FirstOrDefault(c => c.Kind == CardKind.Note);
        Cards.ItemsSource = shown.Count == 0 ? null : shown.Select(c => new CardRow(c)).ToList();
        // View details can open the drawer before a result card is in; an empty list has no size.
        Cards.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ChangeNoteText.Text = note?.Title ?? "";
        ChangeNoteText.Visibility = note == null ? Visibility.Collapsed : Visibility.Visible;
        ShowCopyLinks();
        ShowHandoverLine();
        if (focused >= 0 && shown.Count > 0) { FocusCard(Math.Min(focused, shown.Count - 1)); }
    }

    /// <summary>A result card; it is shown in the drawer, which opens only when the user asks.</summary>
    private void AddCard(ResultCard card)
    {
        _cards.Add(card);
        ShowCards();
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText((LogBox.Text.Length > 0 ? Environment.NewLine : "") + line);
        _shownLog.Add(line);
        LogBox.ScrollToEnd();
    }

    /// <summary>Brushes set from code belong to the theme they were set in; they follow a theme change.</summary>
    private void OnThemeChanged()
    {
        ShowHealth();
        ShowHome();
        if (_page == Page.Apps) { ShowApps(focusList: false); ShowAppChecks(); }
        if (_page == Page.Problem) { ShowProblem(_problem, focus: false); }
        ShowCards();
        foreach (var step in _stepViews) { step.RefreshBrushes(); }
        StatusLabel.Foreground = ThemeBrush.For(_statusTone);
        StatusWord.Foreground = ThemeBrush.For(_statusTone);
        StatusWordBadge.BorderBrush = ThemeBrush.For(_statusTone);
        if (_page == Page.It) { ShowIt(); }
        // The helpers are hidden in high contrast.
        _highContrast = SystemParameters.HighContrast;
        ApplyHelpers();
    }

    /// <summary>
    /// Shows the STEPS list (hidden for a run without step headers) and its note. The rows are
    /// updated in place, added or removed at the end, never rebuilt: a row on screen stays, with
    /// its keyboard focus and the drawer's scroll position, while a run updates it.
    /// </summary>
    private void ShowSteps(List<StepRow> steps, string note)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (i < _stepViews.Count) { _stepViews[i].Update(steps[i]); } else { _stepViews.Add(new StepView(steps[i])); }
        }
        while (_stepViews.Count > steps.Count) { _stepViews.RemoveAt(_stepViews.Count - 1); }
        StepsPanel.Visibility = steps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        StepsNote.Text = note ?? "";
        StepsNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The technical log passes the mouse wheel on to the drawer at its top and bottom, so the
    /// drawer keeps scrolling over it.
    /// </summary>
    private void OnLogWheel(object sender, MouseWheelEventArgs e)
    {
        var atTop = LogBox.VerticalOffset <= 0;
        var atBottom = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 1;
        if ((e.Delta > 0 && atTop) || (e.Delta < 0 && atBottom))
        {
            e.Handled = true;
            DrawerScroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
        }
    }

    /// <summary>
    /// "Open full log": the run shown, with the same header, cards and timeline as the run log in
    /// Downloads, written off the window's thread to the run's own file in the run history,
    /// copied to last-run.log, and opened in the text editor. A run that still goes on is written
    /// as it is at this moment ("Captured at"); its file is replaced once it ends.
    /// </summary>
    private async void OnOpenFullLog(object sender, RoutedEventArgs e)
    {
        // One write at a time: a second click waits for the first.
        if (!FullLogButton.IsEnabled) { return; }
        FullLogButton.IsEnabled = false;
        try
        {
            var header = ShownHeader();
            var lines = _shownLog.ToList();
            var cards = _cards.ToList();
            var path = await Task.Run(() =>
            {
                var latest = UserFiles.LastRunLogFile();
                if (header.Outcome == null && header.Check == null)
                {
                    // Nothing shown: no run for the history, only the log of this moment.
                    SupportPackage.WriteRunLog(header, lines, cards, latest);
                    return latest;
                }
                var file = RunLogStore.Save(header, lines, cards, UserFiles.RunsFolder());
                try { RunLogStore.CopyToLatest(file, latest); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.LogError(ex); }
                return file;
            });
            switch (OpenTextFile(path))
            {
                case TextFileOpened.InFolder:
                    SetStatus("No text editor could open the full log, so its folder is open with the file selected.", Tone.Warning);
                    break;
                case TextFileOpened.No:
                    SetStatus("The full log could not be opened. It is saved at " + path, Tone.Error);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            App.LogError(ex);
            SetStatus("The full log could not be opened. It may be open in another program.", Tone.Error);
        }
        finally
        {
            FullLogButton.IsEnabled = true;
        }
    }

    private void OpenDrawer()
    {
        if (_drawerOpen) { return; }
        _drawerOpen = true;
        // Narrower on a small screen, so the window still fits next to the main column.
        var area = CurrentWorkArea();
        var width = Math.Max(SmallestDrawerWidth, Math.Min(FullDrawerWidth, area.Width - MinWidth));
        DrawerColumn.Width = new GridLength(width);
        Drawer.Visibility = Visibility.Visible;
        DetailsButton.Content = "Hide _details";
        DetailsButton.SetValue(AutomationProperties.ItemStatusProperty, "Expanded");
        if (WindowState == WindowState.Normal)
        {
            var before = Width;
            Width += width;
            ClampToWorkArea();
            _drawerAdded = Width - before;
        }
        else
        {
            // Maximized (or minimized): the room is made when the window is restored, unless the
            // restored width still holds the room of a drawer closed meanwhile.
            _drawerAdded = _shrinkOnRestore;
            _drawerNeedsRoom = _shrinkOnRestore <= 0;
            _shrinkOnRestore = 0;
        }
    }

    /// <summary>
    /// The window is restored: a drawer opened while it was maximized gets its room now, so the page
    /// keeps its width and the drawer is not cut off; a drawer that was closed while it was maximized
    /// gives back the width it added.
    /// </summary>
    internal void FitDrawerAfterRestore()
    {
        if (WindowState != WindowState.Normal) { return; }
        if (_drawerOpen && _drawerNeedsRoom)
        {
            var before = Width;
            Width += DrawerColumn.Width.Value;
            ClampToWorkArea();
            _drawerAdded = Width - before;
        }
        else if (!_drawerOpen && _shrinkOnRestore > 0)
        {
            Width = Math.Max(MinWidth, Width - _shrinkOnRestore);
        }
        _drawerNeedsRoom = false;
        _shrinkOnRestore = 0;
    }

    private void OnDrawerClose(object sender, RoutedEventArgs e) => CloseDrawer();

    /// <summary>
    /// A read-only check's answer: one sentence on the page under the result word. It is not
    /// announced: the status line's name is the same sentence and is. The page says it; the app
    /// shows no Windows notification.
    /// </summary>
    private void ShowAnswer(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) { return; }
        StatusAnswerText.Text = answer;
        StatusAnswerText.Foreground = ThemeBrush.For(_statusTone);
        StatusAnswerText.Visibility = Visibility.Visible;
    }

    private void CloseDrawer()
    {
        if (!_drawerOpen) { return; }
        // The focus was moved into the drawer at the end of a run: it goes back where it came from.
        if (Drawer.IsKeyboardFocusWithin)
        {
            if (_returnFocus is UIElement { IsEnabled: true, IsVisible: true } back) { back.Focus(); } else { DetailsButton.Focus(); }
        }
        _returnFocus = null;
        _drawerOpen = false;
        Drawer.Visibility = Visibility.Collapsed;
        DrawerColumn.Width = new GridLength(0);
        DetailsButton.Content = "View _details";
        DetailsButton.SetValue(AutomationProperties.ItemStatusProperty, "Collapsed");
        // Only what opening the drawer added comes off again; while maximized, once it is restored.
        if (WindowState == WindowState.Normal) { Width = Math.Max(MinWidth, Width - _drawerAdded); }
        else { _shrinkOnRestore += _drawerAdded; }
        _drawerAdded = 0;
        _drawerNeedsRoom = false;
    }

    // The size the user gave the window (or the default), the width when a move or drag began,
    // and whether a refit after a screen change is already queued.
    private double _wantedWidth = WindowLayout.DefaultWindowWidth;
    private double _wantedHeight = WindowLayout.DefaultWindowHeight;
    private double _sizeStartWidth;
    private bool _refitPending;

    /// <summary>The window follows SystemEvents.DisplaySettingsChanged (a static event, left again when the window closes).</summary>
    internal bool ScreenWatched { get; private set; }

    /// <summary>The work area a test gives the window instead of its screen's (in the window's own units); null for the real one.</summary>
    internal Func<Rect> WorkAreaOverride { get; set; }

    /// <summary>Follows a new resolution (SystemEvents.DisplaySettingsChanged); only a window that is shown, and left again when it closes.</summary>
    internal void WatchScreen()
    {
        if (ScreenWatched) { return; }
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        ScreenWatched = true;
    }

    private void OnDisplaySettingsChanged(object sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(ScheduleRefit));

    /// <summary>
    /// The screen changed while the app is open (a new resolution or display scale, a work area
    /// that grew or shrank, as a remote desktop with "update the resolution on resize" does): one refit
    /// once Windows and WPF have applied it, however many messages announced it.
    /// </summary>
    private void ScheduleRefit()
    {
        if (_refitPending) { return; }
        _refitPending = true;
        Dispatcher.BeginInvoke(new Action(RefitToScreen), DispatcherPriority.Background);
    }

    /// <summary>
    /// After a screen change the window keeps the size the user gave it where that fits, else it
    /// is shrunk to the work area (and grows back when the screen grows again); it stays inside the
    /// work area of its monitor, never partly off-screen or under the taskbar; and the page is laid out
    /// again at the new scale, so a page that fits shows no scroll bar. A maximized window is left to
    /// Windows; only its layout is applied again.
    /// </summary>
    internal void RefitToScreen()
    {
        _refitPending = false;
        if (WindowState == WindowState.Normal)
        {
            var area = WorkAreaOverride?.Invoke() ?? CurrentWorkArea();
            var drawer = _drawerOpen ? DrawerColumn.Width.Value : 0;
            var (left, top, width, height) = WindowPlacement.Refit(Left, Top, _wantedWidth + drawer, _wantedHeight, area.Left, area.Top, area.Width, area.Height, MinWidth, MinHeight);
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            if (_drawerOpen) { _drawerAdded = Math.Max(0, width - _wantedWidth); }
        }
        MainGrid.InvalidateMeasure();
        ApplyContentWidth();
        UpdateLayout();
    }

    /// <summary>
    /// Keeps the window inside the work area of its own screen, so nothing ends up under the taskbar
    /// or off screen. The window has one size (the default, or what the user gave it); on a
    /// smaller work area it is made to fit once, and its pages scroll inside it.
    /// </summary>
    private void ClampToWorkArea()
    {
        var area = CurrentWorkArea();
        var current = double.IsNaN(Height) ? WindowLayout.DefaultWindowHeight : Height;
        var (left, top, width, height) = WindowPlacement.Clamp(Left, Top, Width, current, area.Left, area.Top, area.Width, area.Height, MinWidth, MinHeight);
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// The end of a move or a drag of a border: a drag that changed the height is the
    /// user's choice, which is kept and saved; a move or a drag of the left or right border changes
    /// nothing about the height. The window never sizes itself to its content.
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_ENTERSIZEMOVE)
        {
            _sizeStartHeight = ActualHeight;
            _sizeStartWidth = ActualWidth;
        }
        else if (msg == WM_EXITSIZEMOVE && _sizeStartHeight is double start)
        {
            _sizeStartHeight = null;
            if (Math.Abs(ActualHeight - start) > 1)
            {
                _heightChosen = true;
                _wantedHeight = Height;
            }
            // The size the user gave the window is the size it returns to when a smaller screen grows again.
            if (Math.Abs(ActualWidth - _sizeStartWidth) > 1) { _wantedWidth = Math.Max(MinWidth, Width - _drawerAdded); }
        }
        else if (msg == WM_DISPLAYCHANGE || (msg == WM_SETTINGCHANGE && wParam.ToInt64() == SPI_SETWORKAREA))
        {
            // A new resolution or work area (the taskbar moved or changed size).
            ScheduleRefit();
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// The work area of the screen the window is on (not always the primary one), in the window's
    /// own units; the primary screen's when the window has no handle yet.
    /// </summary>
    private Rect CurrentWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var source = PresentationSource.FromVisual(this);
        if (handle == IntPtr.Zero || source?.CompositionTarget == null) { return SystemParameters.WorkArea; }
        var monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) { return SystemParameters.WorkArea; }
        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = fromDevice.Transform(new Point(info.rcWork.Left, info.rcWork.Top));
        var bottomRight = fromDevice.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private void RestorePlacement()
    {
        if (_settings.Width is double w && _settings.Left is double l && _settings.Top is double t)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
            Width = w;
        }
        // Only a height the user chose is kept; otherwise the window opens at its default height.
        if (_settings.HeightChosen && _settings.Height is double h)
        {
            _heightChosen = true;
            Height = h;
        }
        // The size the user gave the window (or the default), before it is made to fit this screen.
        _wantedWidth = double.IsNaN(Width) ? WindowLayout.DefaultWindowWidth : Width;
        _wantedHeight = double.IsNaN(Height) ? WindowLayout.DefaultWindowHeight : Height;
        ClampToWorkArea();
    }

    private void OnClosingWindow(object sender, CancelEventArgs e)
    {
        // Closing never waits for an action: it keeps running in the service, and a repair shows its
        // result when the window is opened again.
        ThemeManager.Changed -= OnThemeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        // A static event: it would keep the window alive after it closed.
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        ScreenWatched = false;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        _helperTimer.Stop();
        _followTimer?.Stop();
        _liveTimer.Stop();
        if (WindowState == WindowState.Normal)
        {
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Width = _drawerOpen ? Width - _drawerAdded : Width;
            // Only a height the user chose by dragging a border is kept (OnWindowMessage).
            _settings.HeightChosen = _heightChosen;
            _settings.Height = _heightChosen ? Height : null;
            _settings.Save(SettingsPath);
        }
        _watcher.Dispose();
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const long SPI_SETWORKAREA = 0x002F;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
