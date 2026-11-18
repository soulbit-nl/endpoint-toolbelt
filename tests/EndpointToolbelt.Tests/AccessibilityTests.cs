using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using EndpointToolbelt.App.Ui;
using Xunit;

namespace EndpointToolbelt.Tests;

public class AccessibilityTests
{
    private static void OnSta(Action action)
    {
        Exception error = null;
        var thread = new Thread(() =>
        {
            try { action(); } catch (Exception ex) { error = ex; }
            // The elements made a dispatcher for this thread; shut it down before the
            // thread ends, so no WPF state of a finished thread is left for the end of the run.
            finally { System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) { throw new Xunit.Sdk.XunitException(error.ToString()); }
    }

    [Fact]
    public void A_health_row_card_or_step_is_a_named_focusable_list_item_for_assistive_technology()
    {
        OnSta(() =>
        {
            var row = new AccessibleRow { Focusable = true };
            AutomationProperties.SetName(row, "App installer (IME): Problem. Not installed");
            AutomationProperties.SetHelpText(row, "What to do");
            var peer = UIElementAutomationPeer.CreatePeerForElement(row);
            Assert.NotNull(peer);
            Assert.Equal("App installer (IME): Problem. Not installed", peer.GetName());
            Assert.Equal("What to do", peer.GetHelpText());
            Assert.Equal(AutomationControlType.ListItem, peer.GetAutomationControlType());
            Assert.True(peer.IsKeyboardFocusable());
            Assert.True(peer.IsControlElement());
        });
    }

    [Fact]
    public void The_health_checks_results_and_steps_are_named_lists()
    {
        OnSta(() =>
        {
            var list = new AccessibleList();
            AutomationProperties.SetName(list, "Device health checks");
            var peer = UIElementAutomationPeer.CreatePeerForElement(list);
            Assert.NotNull(peer);
            Assert.Equal(AutomationControlType.List, peer.GetAutomationControlType());
            Assert.Equal("Device health checks", peer.GetName());
            // A row offers scroll-into-view, which assistive technology uses to show it.
            var row = UIElementAutomationPeer.CreatePeerForElement(new AccessibleRow());
            Assert.NotNull(row.GetPattern(PatternInterface.ScrollItem));
        });
    }

    [Fact]
    public void The_confirmation_dialog_cancels_on_Esc_and_confirms_on_Enter()
    {
        var dir = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        var xaml = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(dir.FullName, "src", "EndpointToolbelt.App", "Ui", "MessageDialog.xaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        System.Xml.Linq.XElement Button(string name) => System.Linq.Enumerable.Single(xaml.Descendants(), e => (string)e.Attribute(x + "Name") == name);
        Assert.Equal("True", (string)Button("CancelButton").Attribute("IsCancel"));
        Assert.Equal("True", (string)Button("ConfirmButton").Attribute("IsDefault"));
    }

    [Fact]
    public void A_plain_border_has_no_peer_which_is_why_the_rows_use_AccessibleRow()
    {
        OnSta(() => Assert.Null(UIElementAutomationPeer.CreatePeerForElement(new Border())));
    }

    private static System.Xml.Linq.XDocument MainWindowXaml()
    {
        var dir = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(dir.FullName, "src", "EndpointToolbelt.App", "Ui", "MainWindow.xaml"));
    }

    [Fact]
    public void The_pages_keep_a_logical_tab_order_and_every_control_says_what_it_is()
    {
        // No TabIndex anywhere, so Tab follows the reading order of each page; every button has
        // text or an automation name; the search box has a label with an access key.
        var xaml = MainWindowXaml();
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.DoesNotContain(xaml.Descendants(), e => e.Attributes().Any(a => a.Name.LocalName == "TabIndex"));
        foreach (var button in xaml.Descendants().Where(e => e.Name.LocalName == "Button"))
        {
            var named = !string.IsNullOrWhiteSpace((string)button.Attribute("Content")) || button.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.Name");
            // A button whose text is set at run time (the primary steps, toggles) is named then; its x:Name says which.
            var setInCode = button.Attribute(x + "Name") != null;
            Assert.True(named || setInCode || button.Elements().Any(), "A button without text or name: " + button);
        }
        var label = xaml.Descendants().Single(e => (string)e.Attribute(x + "Name") == "ToolSearchLabel");
        Assert.Equal("{Binding ElementName=ToolSearch}", (string)label.Attribute("Target"));
        Assert.Contains("_", (string)label.Attribute("Content"));
        // What changes while the user reads is announced: the main finding, the diagnosis run, the search result.
        foreach (var live in new[] { "MainFindingTitle", "DiagnosisRunText", "ToolSearchResult", "ProblemVerdict", "StatusLabel", "AppDetailPostCheck" })
        {
            Assert.Equal("Polite", (string)xaml.Descendants().Single(e => (string)e.Attribute(x + "Name") == live).Attribute("AutomationProperties.LiveSetting"));
        }
        // The app list's rows are buttons named by their text (Enter or Space chooses one).
        var row = xaml.Descendants().Single(e => (string)e.Attribute(x + "Name") == "AppList").Descendants().Single(e => e.Name.LocalName == "Button");
        Assert.Equal("{Binding AccessibleName}", (string)row.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding SelectedTag}", (string)row.Attribute("AutomationProperties.ItemStatus"));
    }

    [Fact]
    public void An_app_row_says_its_state_in_words_never_by_colour_alone()
    {
        OnSta(() =>
        {
            var finding = EndpointToolbelt.App.Core.AppDecisions.Decide(
                new EndpointToolbelt.App.Core.AppEvidence { AppId = "11111111-2222-3333-4444-555555555555", Name = "Contoso Agent", Scope = "user", Verdict = "PACKAGING", ObservedText = "2026-11-10T13:00:00Z" },
                System.Array.Empty<EndpointToolbelt.App.Core.AppEvidence>(), new DateTime(2026, 11, 10, 14, 0, 0, DateTimeKind.Utc));
            var row = new AppRowView(finding, selected: true);
            Assert.Equal("Failed", row.ObservationWord);
            Assert.StartsWith("Contoso Agent: Failed. assigned to you", row.AccessibleName);
            Assert.Equal("Selected", row.SelectedTag);
            Assert.Equal("", new AppRowView(finding, selected: false).SelectedTag);
        });
    }

    // --- The real window, laid out but never shown: what Axe.Windows checks on each page ----------

    private static readonly DateTime Now = DateTime.UtcNow;

    private static EndpointToolbelt.App.Core.RunOutcome Diagnosis(params EndpointToolbelt.App.Core.AppEvidence[] apps)
    {
        var records = apps.Select(a => new EndpointToolbelt.App.Core.EngineRecord { Type = "app-diagnosis", App = a, Utc = Now }).ToList();
        records.Add(new EndpointToolbelt.App.Core.EngineRecord { Type = "apps-diagnosis", Closing = new EndpointToolbelt.App.Core.DiagnosisClose { Present = apps.Length > 0, Records = apps.Length, Listed = apps.Length, UserScope = true }, Utc = Now });
        records.Add(new EndpointToolbelt.App.Core.EngineRecord { Type = "summary", Exit = 0, Utc = Now });
        return new EndpointToolbelt.App.Core.RunOutcome { Action = ToolbeltAction.DiagnoseApps, RunId = "diag0001", Result = ResultCode.Completed, ExitCode = 0 }.With(records);
    }

    private static EndpointToolbelt.App.Core.AppEvidence App(string id, string verdict, string code = "") => new()
    {
        DiagnosisId = "diag0001", AppId = id, Name = "Contoso Agent " + id.Substring(0, 2), Scope = "device", Intent = "required", Revision = 1, Verdict = verdict, Code = code,
        ObservedText = Now.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Shows a diagnosis in the window, the way a finished run does.</summary>
    private static void SetDiagnosis(MainWindow window, EndpointToolbelt.App.Core.RunOutcome outcome)
        => MainWindowPagesTests.Call(window, "SetDiagnosis", EndpointToolbelt.App.Core.AppDecisions.From(outcome, Now), outcome);

    /// <summary>The window's default width (narrow) and a wide one (the wide layout from about 640 px).</summary>
    internal const double Narrow = 520;
    internal const double Wide = 1000;
    internal const double Middle = 700;

    /// <summary>
    /// Lays out the window's content at a fixed size, as it is on screen (the window itself is never
    /// shown). The width decides the layout, as a user's resize does.
    /// </summary>
    internal static void Layout(MainWindow window, double width = Narrow)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new System.Windows.Size(width, 4000));
        content.Arrange(new System.Windows.Rect(0, 0, width, 4000));
        content.UpdateLayout();
    }

    /// <summary>Every element under the window's content that is shown: it and all its parents are Visible; scroll bars left out.</summary>
    private static IEnumerable<UIElement> Shown(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (System.Windows.Media.VisualTreeHelper.GetChild(parent, i) is not UIElement child || child.Visibility != Visibility.Visible || child is System.Windows.Controls.Primitives.ScrollBar) { continue; }
            yield return child;
            foreach (var below in Shown(child)) { yield return below; }
        }
    }

    /// <summary>
    /// The two Axe.Windows rules the app path failed in Windows Sandbox: an element UI Automation
    /// sees must not have an empty bounding rectangle (BoundingRectangleNotNull), and focusable
    /// elements must not share a name and control type (SiblingUniqueAndFocusable; the window's
    /// panels have no automation peer, so all controls of a page are siblings).
    /// </summary>
    internal static void AssertAxeRules(MainWindow window, string page, double width = Narrow)
    {
        Layout(window, width);
        var peers = Shown((DependencyObject)window.Content)
            .Select(e => (Element: e, Peer: UIElementAutomationPeer.CreatePeerForElement(e)))
            // Not IsControlElement(): it is false for every element of a window that is not shown.
            .Where(p => p.Peer != null)
            .ToList();
        var empty = peers.Where(p => p.Element.RenderSize.Width <= 0 || p.Element.RenderSize.Height <= 0)
            .Select(p => p.Peer.GetClassName() + " '" + p.Peer.GetName() + "' #" + (p.Element as FrameworkElement)?.Name).ToList();
        Assert.True(empty.Count == 0, page + ": shown with an empty bounding rectangle: " + string.Join(", ", empty));
        var duplicates = peers.Where(p => p.Element.Focusable && p.Peer.GetName().Length > 0)
            .GroupBy(p => p.Peer.GetName() + "|" + p.Peer.GetLocalizedControlType())
            .Where(g => g.Count() > 1)
            .Select(g => g.Key + " (" + string.Join(", ", g.Select(p => (p.Element as FrameworkElement)?.Name)) + ")").ToList();
        Assert.True(duplicates.Count == 0, page + ": focusable elements with the same name and type: " + string.Join("; ", duplicates));
    }

    [Fact]
    public void Every_page_passes_the_bounding_rectangle_and_sibling_rules_of_Axe()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            // Copy details for IT is shown with any run, as after the diagnosis.
            MainWindowPagesTests.Get<Button>(window, "CopyTicketButton").Visibility = Visibility.Visible;

            // Before the first health check and before any diagnosis.
            AssertAxeRules(window, "Home at start");

            // The diagnosis found no app state (the Sandbox): an empty list, and "not in this list" chosen.
            SetDiagnosis(window, Diagnosis());
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Apps"), false);
            AssertAxeRules(window, "app path without apps");
            MainWindowPagesTests.Call(window, "OnNotListed", window, new RoutedEventArgs());
            Assert.Equal("Copy details for IT", MainWindowPagesTests.Get<Button>(window, "AppStepButton").Content);
            AssertAxeRules(window, "app path with 'not in this list' and Copy details for IT");

            // An app whose step is Retry work apps and settings, next to the Retry button itself.
            SetDiagnosis(window, Diagnosis(App("11111111-2222-3333-4444-555555555555", "RETRY-HELPS", "0x80070643"), App("22222222-3333-4444-5555-666666666666", "OK")));
            Assert.Equal("Retry work apps and settings", MainWindowPagesTests.Get<Button>(window, "AppStepButton").Content);
            AssertAxeRules(window, "app path with an install failure");
            MainWindowPagesTests.Call(window, "OnAppListToggle", window, new RoutedEventArgs());
            AssertAxeRules(window, "app path with every app");

            // Home with the same finding's step (Copy details for IT, as Copy details for IT under the status).
            SetDiagnosis(window, Diagnosis(App("33333333-4444-5555-6666-777777777777", "PACKAGING", "0x87D1041C")));
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false);
            Assert.Equal("Copy details for IT", MainWindowPagesTests.Get<Button>(window, "PrimaryStepButton").Content);
            AssertAxeRules(window, "Home with a primary step");

            // After a remedy (awaiting evidence with Check again), App detected, and an app gone from the list.
            using (var folder = new TestFolder())
            {
                MainWindowPagesTests.UseRemedies(window, folder.Combine(EndpointToolbelt.App.Core.RemedyStore.FileName));
                var now = DateTime.UtcNow;
                var first = MainWindowPagesTests.Diagnosis("diag0011", now.AddMinutes(-2), "RETRY-HELPS", now.AddHours(-1), "0x80070643");
                MainWindowPagesTests.Call(window, "SetDiagnosis", EndpointToolbelt.App.Core.AppDecisions.From(first, now), first);
                MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Apps"), false);
                var finding = (EndpointToolbelt.App.Core.AppFinding)MainWindowPagesTests.Get<Button>(window, "AppStepButton").Tag;
                MainWindowPagesTests.Call(window, "RecordRemedies", new[] { finding }, EndpointToolbelt.App.Core.RemedyKind.RetryAppsAndPolicies, "ab12cd34", now);
                Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<Button>(window, "CheckAgainButton").Visibility);
                AssertAxeRules(window, "app path awaiting evidence");
                MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false);
                Assert.Equal("Check this app again", MainWindowPagesTests.Get<Button>(window, "PrimaryStepButton").Content);
                AssertAxeRules(window, "Home awaiting evidence");
                MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Apps"), false);
                var detected = MainWindowPagesTests.Diagnosis("diag0012", now.AddMinutes(3), "OK", now.AddMinutes(2));
                MainWindowPagesTests.Invoke(window, "ApplyDiagnosis", detected);
                Assert.StartsWith("Post-check: App detected", MainWindowPagesTests.Get<TextBlock>(window, "AppDetailPostCheck").Text);
                AssertAxeRules(window, "app path with App detected");
                var gone = MainWindowPagesTests.Diagnosis("diag0013", now.AddMinutes(4));
                MainWindowPagesTests.Invoke(window, "ApplyDiagnosis", gone);
                Assert.Equal("Copy details for IT", MainWindowPagesTests.Get<Button>(window, "AppStepButton").Content);
                Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<Button>(window, "CheckAgainButton").Visibility);
                AssertAxeRules(window, "app path with an app gone from the list");
            }

            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            AssertAxeRules(window, "Choose a problem");
            foreach (var problem in new[] { EndpointToolbelt.App.Core.Problem.CantSignIn, EndpointToolbelt.App.Core.Problem.WindowsWontUpdate, EndpointToolbelt.App.Core.Problem.CantConnect })
            {
                MainWindowPagesTests.Call(window, "ShowProblem", problem, false);
                AssertAxeRules(window, problem.ToString());
            }
            // For IT's tiles, a group's list, a tool's detail (with the run status in it) and a search, narrow.
            MainWindowPagesTests.Call(window, "OnNavIt", window, new RoutedEventArgs());
            AssertAxeRules(window, "For IT tiles");
            MainWindowPagesTests.Call(window, "OnCategoryTile", MainWindowPagesTests.Get<Button>(window, "AppsTile"), new RoutedEventArgs());
            AssertAxeRules(window, "For IT list");
            MainWindowPagesTests.Call(window, "OpenTool", EndpointToolbelt.App.Core.ToolCatalog.ByButton("FullResetButton"), true);
            AssertAxeRules(window, "For IT detail");
            MainWindowPagesTests.Get<TextBox>(window, "ToolSearch").Text = "clock";
            AssertAxeRules(window, "For IT searching");
            MainWindowPagesTests.Get<TextBox>(window, "ToolSearch").Text = "no such tool";
            AssertAxeRules(window, "For IT finding nothing");
            MainWindowPagesTests.Get<TextBox>(window, "ToolSearch").Text = "";

            // Wider windows only add margin: the same one column and the tabs on top, on every page.
            AssertAxeRules(window, "For IT wide", Wide);
            AssertAxeRules(window, "For IT at 700", Middle);
            Assert.Equal(System.Windows.Controls.Orientation.Horizontal, MainWindowPagesTests.Get<StackPanel>(window, "NavBar").Orientation);
            AssertAxeRules(window, "For IT wide again", Wide);
            Assert.Equal(System.Windows.Controls.Orientation.Horizontal, MainWindowPagesTests.Get<StackPanel>(window, "NavBar").Orientation);
            MainWindowPagesTests.Call(window, "OnCategoryTile", MainWindowPagesTests.Get<Button>(window, "SignInTile"), new RoutedEventArgs());
            AssertAxeRules(window, "For IT wide, another group", Wide);
            MainWindowPagesTests.Get<TextBox>(window, "ToolSearch").Text = "IME";
            AssertAxeRules(window, "For IT wide searching", Wide);
            MainWindowPagesTests.Get<TextBox>(window, "ToolSearch").Text = "";
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false);
            AssertAxeRules(window, "Home wide", Wide);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Apps"), false);
            AssertAxeRules(window, "app path wide", Wide);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            AssertAxeRules(window, "Choose a problem wide", Wide);
            MainWindowPagesTests.Call(window, "ShowProblem", EndpointToolbelt.App.Core.Problem.CantSignIn, false);
            AssertAxeRules(window, "I can't sign in wide", Wide);
            // And narrow again.
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false);
            AssertAxeRules(window, "Home narrow again");
            Assert.Equal(System.Windows.Controls.Orientation.Horizontal, MainWindowPagesTests.Get<StackPanel>(window, "NavBar").Orientation);
        });
    }

    [Fact]
    public void Home_passes_the_Axe_rules_with_a_finding_that_needs_attention_and_without_the_service()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var warn = new List<EndpointToolbelt.App.Core.HealthResult>
            {
                new() { Name = EndpointToolbelt.App.Core.HealthChecks.Clock, Status = EndpointToolbelt.App.Core.HealthStatus.Warn, Detail = "Off by 10 minutes" },
                new() { Name = EndpointToolbelt.App.Core.HealthChecks.FreeSpace, Status = EndpointToolbelt.App.Core.HealthStatus.Pass, Detail = "40 GB" },
            };
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo(), false, warn, new EndpointToolbelt.App.Core.DsregInfo(), false);
            AssertAxeRules(window, "Home needs attention");
            MainWindowPagesTests.Call(window, "OnHealthToggle", window, new RoutedEventArgs());
            AssertAxeRules(window, "Home with every check");
            // A row opened shows its detail and hint; the drawer opened with View details shows the copy links.
            foreach (var item in ((System.Collections.Generic.IEnumerable<HealthRow>)MainWindowPagesTests.Get<System.Windows.Controls.ItemsControl>(window, "HealthList").ItemsSource).Take(1)) { item.Toggle(); }
            AssertAxeRules(window, "Home with a check opened");
            MainWindowPagesTests.Call(window, "SetStatus", "Sync clock: done.", EndpointToolbelt.App.Core.Tone.Success, false, true);
            MainWindowPagesTests.Call(window, "OnDetails", window, new RoutedEventArgs());
            MainWindowPagesTests.Get<Button>(window, "CopyTicketButton").Visibility = Visibility.Visible;
            AssertAxeRules(window, "Home with a result and the drawer open", Wide);
            MainWindowPagesTests.Call(window, "OnDetails", window, new RoutedEventArgs());
            AssertAxeRules(window, "Home needs attention, wide", Wide);
            MainWindowPagesTests.Invoke(window, "ApplyHealth", null, true, warn, new EndpointToolbelt.App.Core.DsregInfo(), false);
            AssertAxeRules(window, "Home without the service");
        });
    }

    [Fact]
    public void The_helper_character_is_invisible_to_assistive_technology_and_never_focusable()
    {
        OnSta(() =>
        {
            var helper = new HelperCharacter { Name = "Mascot" };
            // Its element is in the raw view only (neither a control nor a content element, which screen
            // readers and Axe.Windows read): no name, no children, never focusable; a test finds it by its ID.
            var peer = UIElementAutomationPeer.CreatePeerForElement(helper);
            Assert.NotNull(peer);
            Assert.False(peer.IsControlElement());
            Assert.False(peer.IsContentElement());
            Assert.Equal("", peer.GetName());
            Assert.Equal("Mascot", peer.GetAutomationId());
            Assert.False(peer.IsKeyboardFocusable());
            Assert.Null(peer.GetChildren());
            // Nothing inside it has an element of its own (decorative).
            var parts = new List<DependencyObject>();
            for (var c = 0; c < System.Windows.Media.VisualTreeHelper.GetChildrenCount(helper); c++) { parts.Add(System.Windows.Media.VisualTreeHelper.GetChild(helper, c)); }
            for (var i = 0; i < parts.Count; i++)
            {
                for (var c = 0; c < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parts[i]); c++) { parts.Add(System.Windows.Media.VisualTreeHelper.GetChild(parts[i], c)); }
            }
            Assert.True(parts.Count > 10);
            Assert.All(parts.OfType<UIElement>(), p => Assert.Null(UIElementAutomationPeer.CreatePeerForElement(p)));
            Assert.False(helper.Focusable);
            // A mouse click makes it hop; still no focus and no name.
            Assert.True(helper.IsHitTestVisible);
        });
    }

    [Fact]
    public void Every_mood_is_a_distinct_still_pose_without_Windows_animations_and_animated_with_them()
    {
        OnSta(() =>
        {
            var helper = new HelperCharacter();
            var poses = new Dictionary<EndpointToolbelt.App.Core.HelperMood, List<UIElement>>();
            foreach (EndpointToolbelt.App.Core.HelperMood mood in Enum.GetValues(typeof(EndpointToolbelt.App.Core.HelperMood)))
            {
                helper.Mood = mood;
                // Reduced motion: a still pose, no animation running.
                Assert.False(helper.IsAnimating, mood.ToString());
                poses[mood] = helper.PoseParts.ToList();
                Assert.All(poses[mood], p => Assert.Equal(Visibility.Visible, p.Visibility));
            }
            // Each mood shows parts no other mood shows, and hides the others' parts.
            foreach (var pose in poses)
            {
                helper.Mood = pose.Key;
                foreach (var other in poses.Where(o => o.Key != pose.Key))
                {
                    Assert.NotEmpty(other.Value.Except(pose.Value));
                    Assert.All(other.Value.Except(pose.Value), p => Assert.Equal(Visibility.Collapsed, p.Visibility));
                }
            }
            // With Windows animations on, every mood animates calmly; off again, it stops at once.
            helper.AnimationsEnabled = true;
            foreach (var mood in poses.Keys)
            {
                helper.Mood = mood;
                Assert.True(helper.IsAnimating, mood.ToString());
            }
            helper.AnimationsEnabled = false;
            Assert.False(helper.IsAnimating);
        });
    }

    /// <summary>The pages of the window by their container; a page's own controls live below it.</summary>
    private static readonly string[] PageContainers = { "HomePage", "ProblemsPage", "ProblemHeader", "AppsPage", "ProblemPage", "ItPage" };

    /// <summary>The parts the window hides as a whole: the pages and the drawer (no left pane any more).</summary>
    private static readonly string[] HiddenParts = PageContainers.Concat(new[] { "Drawer" }).ToArray();

    /// <summary>Every element UI Automation reaches from the window, as its peers report their children.</summary>
    private static List<UIElement> AutomationElements(MainWindow window)
    {
        var found = new List<UIElement>();
        void Walk(AutomationPeer peer, int depth)
        {
            if (peer == null || depth > 60) { return; }
            if (peer is UIElementAutomationPeer element) { found.Add(element.Owner); }
            // As UI Automation asks again after a change, not from the peer's cache of an earlier walk.
            peer.ResetChildrenCache();
            foreach (var child in peer.GetChildren() ?? new List<AutomationPeer>()) { Walk(child, depth + 1); }
        }
        // The window's own peer reports what ShownPeers finds below it. A window that was never shown
        // has no template yet (its content is not its visual child), so the walk starts at the
        // content with the same function, which is the window's whole client area.
        var root = UIElementAutomationPeer.CreatePeerForElement(window);
        Assert.IsType<ShownOnlyWindowAutomationPeer>(root);
        foreach (var child in ShownPeers.Children((UIElement)window.Content) ?? new List<AutomationPeer>()) { Walk(child, 1); }
        return found;
    }

    /// <summary>
    /// No control of a page that is not shown reaches UI Automation: a screen reader never
    /// meets a control it cannot see, and Axe.Windows never finds one that is not a control element.
    /// Every element the tree exposes has only Visible ancestors.
    /// </summary>
    private static void AssertOnlyTheShownPage(MainWindow window, string shown, string label, double width)
    {
        Layout(window, width);
        foreach (var name in PageContainers)
        {
            var page = MainWindowPagesTests.Get<FrameworkElement>(window, name);
            var active = name == shown || (shown is "AppsPage" or "ProblemPage" && name == "ProblemHeader");
            Assert.True(active == (page.Visibility == Visibility.Visible), label + ": " + name + " is " + page.Visibility);
        }
        var elements = AutomationElements(window);
        Assert.True(elements.Count > 10, label + ": the automation tree has " + elements.Count + " elements");
        var hidden = elements
            // Nothing below a page, the left pane or the drawer that is not shown, and no control (a
            // focusable element: a tool's Run button, a row's fix link) the window hid; a line of text
            // a shown tile leaves out beside the list is part of that tile, which is named on its own.
            .Where(e => Ancestors(e).Any(a => a.Visibility != Visibility.Visible && a is FrameworkElement { TemplatedParent: null } && (HiddenParts.Contains(((FrameworkElement)a).Name) || e.Focusable)))
            .Select(e => e.GetType().Name + " '" + AutomationProperties.GetName(e) + "' #" + (e as FrameworkElement)?.Name)
            .Distinct().ToList();
        Assert.True(hidden.Count == 0, label + ": UI Automation reaches elements that are not shown: " + string.Join(", ", hidden.Take(20)));
        // Nothing inside the mascot is in the tree; the mascot itself only as a raw element (not a control or content element).
        Assert.DoesNotContain(elements, e => Ancestors(e).Skip(1).Any(a => a is HelperCharacter));
        Assert.All(elements.OfType<HelperCharacter>(), m => Assert.False(UIElementAutomationPeer.CreatePeerForElement(m).IsControlElement()));
    }

    [Fact]
    public void The_mascot_shows_at_rest_on_every_page_and_is_never_a_control_in_the_automation_tree()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            window.WindowsAnimations = true;
            window.WindowShownOverride = true;
            var helper = MainWindowPagesTests.Get<HelperCharacter>(window, "Mascot");
            var pages = new (string Label, string Page, Action Go)[]
            {
                ("Choose a problem", "ProblemsPage", () => MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false)),
                ("I can't sign in", "ProblemPage", () => MainWindowPagesTests.Call(window, "ShowProblem", EndpointToolbelt.App.Core.Problem.CantSignIn, false)),
                ("the app path", "AppsPage", () => MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Apps"), false)),
                ("For IT tiles", "ItPage", () => MainWindowPagesTests.Call(window, "OnNavIt", window, new RoutedEventArgs())),
                ("For IT list", "ItPage", () => MainWindowPagesTests.Call(window, "OnCategoryTile", MainWindowPagesTests.Get<Button>(window, "SignInTile"), new RoutedEventArgs())),
                ("For IT detail", "ItPage", () => MainWindowPagesTests.Call(window, "OpenTool", EndpointToolbelt.App.Core.ToolCatalog.ByButton("TeamsCacheButton"), true)),
            };
            foreach (var width in new[] { Narrow, Wide })
            {
                foreach (var (label, page, go) in pages)
                {
                    go();
                    AssertOnlyTheShownPage(window, page, label, width);
                    // Shown, laid out with a size, at rest, animated (Windows animations on), and decorative.
                    Assert.True(Ancestors(helper).All(a => a.Visibility == Visibility.Visible), label + ": the helper is not shown");
                    Assert.True(helper.RenderSize.Width > 0 && helper.RenderSize.Height > 0, label + ": the helper has no size");
                    Assert.Equal(EndpointToolbelt.App.Core.HelperMood.Idle, helper.Mood);
                    Assert.True(helper.IsAnimating, label);
                    Assert.False(helper.Focusable);
                    // A mouse click makes it hop; still no focus and no name.
            Assert.True(helper.IsHitTestVisible);
                    Assert.IsType<HelperCharacter.MascotAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(helper));
                }
            }
        });
    }

    private static IEnumerable<UIElement> Ancestors(UIElement element)
    {
        for (DependencyObject current = element; current != null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui) { yield return ui; }
        }
    }

    [Fact]
    public void A_page_that_is_not_shown_exposes_none_of_its_controls_narrow_wide_and_after_a_theme_change()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var health = new List<EndpointToolbelt.App.Core.HealthResult>
            {
                new() { Name = EndpointToolbelt.App.Core.HealthChecks.Clock, Status = EndpointToolbelt.App.Core.HealthStatus.Warn, Detail = "Off by 10 minutes" },
                new() { Name = EndpointToolbelt.App.Core.HealthChecks.FreeSpace, Status = EndpointToolbelt.App.Core.HealthStatus.Fail, Detail = "3 GB" },
                new() { Name = EndpointToolbelt.App.Core.HealthChecks.PendingRestart, Status = EndpointToolbelt.App.Core.HealthStatus.Pass, Detail = "None" },
            };
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new Protocol.StatusInfo { SupportUrl = "https://support.contoso.example" }, false, health, new EndpointToolbelt.App.Core.DsregInfo(), false);
            SetDiagnosis(window, Diagnosis(App("11111111-2222-3333-4444-555555555555", "RETRY-HELPS", "0x80070643")));
            foreach (var (label, width) in new[] { ("narrow", Narrow), ("left pane", Middle), ("wide", Wide), ("narrow again", Narrow) })
            {
                MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false);
                AssertOnlyTheShownPage(window, "HomePage", label + ", Home", width);
                MainWindowPagesTests.Call(window, "OnNavIt", window, new RoutedEventArgs());
                AssertOnlyTheShownPage(window, "ItPage", label + ", For IT tiles", width);
                MainWindowPagesTests.Call(window, "OpenTool", EndpointToolbelt.App.Core.ToolCatalog.ByButton("SyncClockButton"), true);
                AssertOnlyTheShownPage(window, "ItPage", label + ", a tool's detail", width);
                MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
                AssertOnlyTheShownPage(window, "ProblemsPage", label + ", Choose a problem", width);
                MainWindowPagesTests.Call(window, "ShowProblem", EndpointToolbelt.App.Core.Problem.CantSignIn, false);
                AssertOnlyTheShownPage(window, "ProblemPage", label + ", I can't sign in", width);
                MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Apps"), false);
                AssertOnlyTheShownPage(window, "AppsPage", label + ", the app path", width);
            }

            // A theme change redraws Home, the app path and the problem paths; it never shows a page again.
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            var light = dictionaries[0];
            try
            {
                foreach (var theme in new[] { "Dark", "HighContrast", "Light" })
                {
                    dictionaries[0] = new ResourceDictionary { Source = new Uri("pack://application:,,,/EndpointToolbelt;component/Ui/Themes/" + theme + ".xaml", UriKind.Absolute) };
                    MainWindowPagesTests.Call(window, "OnNavIt", window, new RoutedEventArgs());
                    MainWindowPagesTests.Call(window, "OnThemeChanged");
                    AssertOnlyTheShownPage(window, "ItPage", theme + ", For IT", Narrow);
                    MainWindowPagesTests.Call(window, "ShowProblem", EndpointToolbelt.App.Core.Problem.CantConnect, false);
                    MainWindowPagesTests.Call(window, "OnThemeChanged");
                    AssertOnlyTheShownPage(window, "ProblemPage", theme + ", I can't connect", Wide);
                }
            }
            finally
            {
                dictionaries[0] = light;
            }
        });
    }

    [Fact]
    public void The_peers_report_only_what_is_shown_where_plain_WPF_reports_collapsed_controls_too()
    {
        OnSta(() =>
        {
            // Plain WPF: a collapsed panel's button is still a child (only "not a control element").
            var hidden = new StackPanel { Visibility = Visibility.Collapsed };
            hidden.Children.Add(new Button { Content = "Hidden" });
            var shown = new StackPanel();
            shown.Children.Add(new Button { Content = "Shown" });
            var collapsedButton = new Button { Content = "Collapsed", Visibility = Visibility.Collapsed };
            shown.Children.Add(collapsedButton);
            var root = new StackPanel();
            root.Children.Add(hidden);
            root.Children.Add(shown);
            var plain = new FrameworkElementAutomationPeer(root).GetChildren() ?? new List<AutomationPeer>();
            Assert.Contains(plain, p => p.GetName() == "Hidden");
            // The window's peer, the shown-only scroll viewer and a row leave them out.
            var names = (ShownPeers.Children(root) ?? new List<AutomationPeer>()).Select(p => p.GetName()).ToList();
            Assert.Equal(new[] { "Shown" }, names);
            var scroll = new ShownOnlyScrollViewer { Content = root };
            scroll.Measure(new Size(300, 300));
            scroll.Arrange(new Rect(0, 0, 300, 300));
            var scrolled = UIElementAutomationPeer.CreatePeerForElement(scroll).GetChildren() ?? new List<AutomationPeer>();
            Assert.DoesNotContain(scrolled, p => p.GetName() is "Hidden" or "Collapsed");
            Assert.Contains(scrolled, p => p.GetName() == "Shown");
            Assert.False(ShownPeers.IsShownWithin(collapsedButton, root));
        });
    }

    [Fact]
    public void The_app_path_has_its_title()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            SetDiagnosis(window, Diagnosis());
            MainWindowPagesTests.Call(window, "ShowProblem", EndpointToolbelt.App.Core.Problem.CantConnect, false);
            // The app path reached from a page that showed another path names itself, not the last one.
            MainWindowPagesTests.Call(window, "OpenAppPath");
            Assert.Equal("An app from Company Portal won't install", MainWindowPagesTests.Get<TextBlock>(window, "ProblemTitle").Text);
        });
    }
}
