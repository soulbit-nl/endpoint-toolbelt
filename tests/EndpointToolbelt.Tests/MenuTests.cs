using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EndpointToolbelt.App.Core;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>The main window's menu: fix links under health rows and the More tools groups.</summary>
public class MenuTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument MainWindow()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return XDocument.Load(Path.Combine(dir.FullName, "src", "EndpointToolbelt.App", "Ui", "MainWindow.xaml"));
    }

    [Fact]
    public void A_row_that_is_not_ok_links_to_the_action_its_hint_names()
    {
        HealthResult Row(string name, HealthStatus status, bool unavailable = false)
            => new() { Name = name, Status = status, ServiceUnavailable = unavailable };

        Assert.Equal(HealthFix.RetryAppsAndPolicies, Row(HealthChecks.LastSync, HealthStatus.Warn).Fix);
        Assert.Equal(HealthFix.RetryAppsAndPolicies, Row(HealthChecks.ImeService, HealthStatus.Fail).Fix);
        Assert.Equal(HealthFix.RetryAppsAndPolicies, Row(HealthChecks.ImeCheckIn, HealthStatus.Warn).Fix);
        Assert.Equal(HealthFix.RetryAppsAndPolicies, Row(HealthChecks.MdmCertificate, HealthStatus.Warn).Fix);
        Assert.Equal(HealthFix.ConnectivityTest, Row(HealthChecks.Connectivity, HealthStatus.Fail).Fix);
        // An OK row, a row the service could not answer, and a problem no action fixes: no link.
        Assert.Equal(HealthFix.None, Row(HealthChecks.LastSync, HealthStatus.Pass).Fix);
        Assert.Equal(HealthFix.None, Row(HealthChecks.LastSync, HealthStatus.Warn, unavailable: true).Fix);
        Assert.Equal(HealthFix.None, Row(HealthChecks.Enrollment, HealthStatus.Fail).Fix);
        Assert.Equal(HealthFix.None, Row(HealthChecks.PendingRestart, HealthStatus.Warn).Fix);
    }

    [Fact]
    public void Every_fix_has_link_text_and_a_hint_that_names_it()
    {
        foreach (HealthFix fix in Enum.GetValues(typeof(HealthFix)))
        {
            if (fix == HealthFix.None) { Assert.Equal("", Words.FixLink(fix)); continue; }
            Assert.False(string.IsNullOrWhiteSpace(Words.FixLink(fix)), fix.ToString());
        }
        // The hint of a row with a link speaks of the same action.
        Assert.Contains("Retry work apps and settings", HealthChecks.Hint(HealthChecks.LastSync, false));
        Assert.Contains("Check Microsoft connections", HealthChecks.Hint(HealthChecks.Connectivity, false));
    }

    [Fact]
    public void Copy_for_IT_is_the_one_copy_link_with_its_own_access_key_and_name()
    {
        // One copy link for a message to IT, hidden until a run or check is shown.
        var buttons = MainWindow().Descendants().Where(e => e.Name.LocalName == "Button").ToList();
        Assert.DoesNotContain(buttons, b => (string)b.Attribute(X + "Name") == "CopySummaryButton");
        var ticket = buttons.Single(b => (string)b.Attribute(X + "Name") == "CopyTicketButton");
        Assert.Equal("Copy details for I_T", (string)ticket.Attribute("Content"));
        Assert.Equal("OnCopyTicket", (string)ticket.Attribute("Click"));
        Assert.Equal("Collapsed", (string)ticket.Attribute("Visibility"));
        Assert.Equal("Copy details for IT", (string)ticket.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name"));
        Assert.Contains("leaves out the device and user name", (string)ticket.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.HelpText"));
    }

    [Fact]
    public void Access_keys_in_the_main_window_are_unique()
    {
        var keys = MainWindow().Descendants()
            .SelectMany(e => new[] { (string)e.Attribute("Content"), (string)e.Attribute("Header") })
            .Where(text => text != null && !text.StartsWith("{", StringComparison.Ordinal))
            .Select(text => Regex.Match(text.Replace("__", ""), "_(.)"))
            .Where(m => m.Success)
            .Select(m => char.ToUpperInvariant(m.Groups[1].Value[0]))
            .ToList();
        var duplicates = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "Duplicate access keys: " + string.Join(", ", duplicates));
    }

    private static XElement Named(XDocument xaml, string name) => xaml.Descendants().Single(e => (string)e.Attribute(X + "Name") == name);

    [Fact]
    public void The_window_has_three_entries_and_no_More_tools_page()
    {
        // Home, Choose a problem, For IT; no More tools page.
        var xaml = MainWindow();
        var nav = Named(xaml, "NavBar").Elements().Where(e => e.Name.LocalName == "Button").ToList();
        Assert.Equal(new[] { "HomeNav", "ProblemsNav", "ItNav" }, nav.Select(b => (string)b.Attribute(X + "Name")));
        Assert.Equal(new[] { "Ho_me", "Choose a _problem", "_For IT support" }, nav.Select(b => (string)b.Attribute("Content")));
        Assert.Equal(new[] { "Home", "Choose a problem", "For IT support" }, nav.Select(b => (string)b.Attribute("AutomationProperties.Name")));
        // Home is shown when the window opens; every other page waits until it is chosen.
        Assert.Equal("Selected", (string)nav[0].Attribute("Tag"));
        foreach (var page in new[] { "ProblemsPage", "AppsPage", "ProblemPage", "ItPage", "ProblemHeader" })
        {
            Assert.Equal("Collapsed", (string)Named(xaml, page).Attribute("Visibility"));
        }
        Assert.Null(Named(xaml, "HomePage").Attribute("Visibility"));
        Assert.DoesNotContain(xaml.Descendants(), e => (string)e.Attribute(X + "Name") is "MoreToolsButton" or "MoreToolsPanel" or "MainPage");
    }

    [Fact]
    public void Home_has_the_status_card_with_one_step_the_health_strip_the_four_problem_tiles_and_a_line_for_a_person()
    {
        var home = Named(MainWindow(), "HomePage");
        var names = home.Descendants().Select(e => (string)e.Attribute(X + "Name")).Where(n => n != null).ToList();
        foreach (var name in new[] { "StatusCard", "SeverityWord", "MainFindingTitle", "MainFindingText", "LastCheckedText", "PrimaryStepButton",
            "RefreshButton", "ReviewAppsLink", "HealthVerdict", "HealthList", "HealthToggle", "HomeAppTile", "HomeSignInTile", "HomeUpdateTile", "HomeConnectTile", "HomePackageLink", "ContactLink" })
        {
            Assert.Contains(name, names);
        }
        // At most one primary step; Check this PC again is a secondary button with the key Refresh had (Alt+E).
        Assert.Single(home.Descendants(), e => e.Name.LocalName == "Button" && (string)e.Attribute("Style") == "{StaticResource PrimaryButton}");
        Assert.Equal("Check my _apps", (string)Named(MainWindow(), "ReviewAppsLink").Attribute("Content"));
        var check = Named(MainWindow(), "RefreshButton");
        Assert.Equal("Ch_eck this PC again", (string)check.Attribute("Content"));
        Assert.Equal("{StaticResource SecondaryButton}", (string)check.Attribute("Style"));
        Assert.StartsWith("Check this PC again", (string)check.Attribute("AutomationProperties.Name"));
        // The four problem tiles open the same paths as Choose a problem, with the same titles.
        var tiles = Named(MainWindow(), "HomeTiles").Elements().Where(e => e.Name.LocalName == "Button").ToList();
        Assert.Equal(ProblemPaths.HomeTiles.Select(p => ProblemPaths.Get(p).Title), tiles.Select(t => (string)t.Attribute("AutomationProperties.Name")));
        // The compliance path is on Choose a problem only.
        Assert.DoesNotContain(ProblemPaths.OnHome, p => p.Problem == Problem.DeviceBlocked);
        // A short label on one line on Home (the full title is the tile's name and tooltip), so Home fits 540 x 640 at 150 %.
        Assert.Equal(new[] { "App won't install", "Keeps asking to sign in", "Can't connect", "Windows Update fails" },
            tiles.Select(t => (string)t.Descendants().Single(e => e.Name.LocalName == "TextBlock").Attribute("Text")));
        Assert.Equal(tiles.Select(t => (string)t.Attribute("AutomationProperties.Name")), tiles.Select(t => (string)t.Descendants().Single(e => e.Name.LocalName == "TextBlock").Attribute("ToolTip")));
        Assert.All(tiles, t => Assert.Equal("{StaticResource HomeTileLabel}", (string)t.Descendants().Single(e => e.Name.LocalName == "TextBlock").Attribute("Style")));
        Assert.Equal(new[] { "OnProblemApp", "OnProblemSignIn", "OnProblemConnect", "OnProblemUpdate" }, tiles.Select(t => (string)t.Attribute("Click")));
        Assert.Equal("OnPackage", (string)Named(MainWindow(), "HomePackageLink").Attribute("Click"));
        // The one mascot sits beside the pages (in the column), not in Home itself.
        Assert.DoesNotContain(home.Descendants(), e => e.Name.LocalName == "HelperCharacter");
    }

    [Fact]
    public void Choose_a_problem_has_seven_equal_rows_in_order_and_the_app_path_offers_Retry_with_its_warning_first()
    {
        var xaml = MainWindow();
        // One kind of row for every problem, in a fixed order, named "<title>. <subtitle>"; no links.
        var rows = Named(xaml, "ProblemRows").Elements().Where(e => e.Name.LocalName == "Button").ToList();
        Assert.Equal(new[] { "ProblemAppButton", "ProblemSignInButton", "ProblemConnectButton", "ProblemOfficeButton", "ProblemOneDriveButton", "ProblemBlockedButton", "ProblemUpdateButton", "ProblemWindowButton" },
            rows.Select(b => (string)b.Attribute(X + "Name")));
        Assert.Equal(ProblemPaths.All.Select(p => p.AccessibleName), rows.Select(b => (string)b.Attribute("AutomationProperties.Name")));
        Assert.All(rows, b => Assert.Equal("{StaticResource ProblemRowButton}", (string)b.Attribute("Style")));
        // No orange links (orange reads as a warning); "My problem is not listed" is a link under the
        // list in the text colour, the way out when no row fits.
        var links = Named(xaml, "ProblemsPage").Descendants().Where(e => (string)e.Attribute("Style") == "{StaticResource LinkButton}").ToList();
        var notListed = Assert.Single(links);
        Assert.Equal("ProblemNotListedButton", (string)notListed.Attribute(X + "Name"));
        Assert.Equal("{DynamicResource TextPrimary}", (string)notListed.Attribute("Foreground"));
        Assert.Same(Named(xaml, "ProblemRows").Parent, notListed.Parent);
        Assert.True(Named(xaml, "ProblemRows").IsBefore(notListed));
        foreach (var (row, path) in rows.Zip(ProblemPaths.All, (r, p) => (r, p)))
        {
            var texts = row.Descendants().Where(e => e.Name.LocalName is "TextBlock" or "AccessText").Select(e => ((string)e.Attribute("Text") ?? "").Replace("_", "")).ToList();
            Assert.Equal(new[] { path.Title, path.Subtitle }, texts);
            Assert.Single(row.Descendants(), e => e.Name.LocalName == "Path");
        }
        Assert.Equal(new[] { "An app from Company Portal won't install", "Teams or Office keeps asking me to sign in", "No internet, or work apps can't connect",
            "An Office app closes right after it opens", "OneDrive doesn't sync", "My device is blocked or 'not compliant'", "Windows Update keeps failing", "A window is off the screen" }, ProblemPaths.All.Select(p => p.Title));
        Assert.Equal(new[] { "Stuck, failed, or not in the list", "Or says your work account has a problem", "Wi-Fi, network cable, or Teams and Outlook stay offline",
            "Word, Excel, Outlook (classic), PowerPoint", "Files stay on \"pending\" or show red crosses", "Company Portal or a sign-in says it doesn't meet requirements", "Or the same update comes back every time", "After undocking or using another monitor" },
            ProblemPaths.All.Select(p => p.Subtitle));
        // The access keys of the four main paths stay (W, S, N, U); the Office row is hidden until an Office app is found.
        Assert.Equal(new[] { "An app from Company Portal _won't install", "Teams or Office keeps asking me to _sign in", "No internet, or work apps can't co_nnect", "Windows _Update keeps failing" },
            Named(xaml, "ProblemRows").Descendants().Where(e => e.Name.LocalName == "AccessText").Select(e => (string)e.Attribute("Text")));
        Assert.Equal("Collapsed", (string)Named(xaml, "ProblemOfficeButton").Attribute("Visibility"));
        Assert.Equal("1", (string)Named(xaml, "ProblemRows").Attribute("Columns"));
        Assert.Equal(CompliancePath.Subtitle, ProblemPaths.Get(Problem.DeviceBlocked).Subtitle);
        // The existing Retry lives on the app path only, after the text that says what it affects.
        var apps = Named(xaml, "AppsPage").Descendants().ToList();
        var caveat = apps.FindIndex(e => (string)e.Attribute(X + "Name") == "RetryCaveatText");
        var retry = apps.FindIndex(e => (string)e.Attribute(X + "Name") == "RetryButton");
        Assert.True(caveat >= 0 && retry > caveat);
        // Retry is the first button of the one row under the list, before Reload, so it is in view without a scroll.
        var reload = apps.FindIndex(e => (string)e.Attribute(X + "Name") == "ReloadAppsButton");
        Assert.True(reload > retry);
        Assert.Contains(Named(xaml, "RetryButton").Ancestors(), e => (string)e.Attribute(X + "Name") == "AppsFooter");
        Assert.Same(Named(xaml, "RetryButton").Parent, Named(xaml, "AppsActions").Parent);
        Assert.Single(xaml.Descendants(), e => (string)e.Attribute(X + "Name") == "RetryButton");
        Assert.Contains("every failed app", AppDecisions.RetryCaveat);
        Assert.Contains("all policies", AppDecisions.RetryCaveat);
        foreach (var name in new[] { "DiagnosisRunText", "AppList", "AppDetail", "AppStepButton", "ReloadAppsButton", "NotListedButton", "CheckAgainButton", "AppDetailPostCheck" })
        {
            Assert.Contains(Named(xaml, "AppsPage").Descendants(), e => (string)e.Attribute(X + "Name") == name);
        }
    }

    [Fact]
    public void For_IT_has_the_search_four_tiles_a_list_a_detail_page_with_every_tool_in_exactly_one_place_and_no_expanders()
    {
        var xaml = MainWindow();
        var it = Named(xaml, "ItPage");
        foreach (var name in new[] { "ToolSearch", "ToolSearchLabel", "ToolSearchResult", "ItBackButton", "CategoryTiles", "ToolList", "ToolDetail", "ToolDetailTitle",
            "FactTime", "FactChanges", "ToolLastRunText", "ToolLastRunLink", "DetailStatusHost", "RecentRuns", "RunsFolderLink", "ReportButton", "HtmlReportButton", "PackageButton" })
        {
            Assert.Contains(it.Descendants(), e => (string)e.Attribute(X + "Name") == name);
        }
        // No expanders for the tool groups any more (the drawer's technical log keeps its own).
        Assert.DoesNotContain(it.Descendants(), e => e.Name.LocalName == "Expander");
        Assert.DoesNotContain(xaml.Descendants(), e => (string)e.Attribute("Style") == "{StaticResource ToolGroup}");
        // The search comes first, then the four tiles in their fixed order.
        var main = Named(xaml, "ItMain").Descendants().Select(e => (string)e.Attribute(X + "Name")).Where(n => n != null).ToList();
        Assert.True(main.IndexOf("ToolSearch") < main.IndexOf("CategoryTiles") && main.IndexOf("CategoryTiles") < main.IndexOf("ToolList"));
        var tiles = Named(xaml, "CategoryTiles").Elements().Where(e => e.Name.LocalName == "Button").Select(e => (string)e.Attribute(X + "Name")).ToList();
        Assert.Equal(new[] { "AppsTile", "SignInTile", "MicrosoftTile", "WindowsTile", "DefenderTile", "NetworkTile" }, tiles);
        Assert.All(tiles, t => Assert.Contains(xaml.Descendants(), e => (string)e.Attribute(X + "Name") == t + "Summary"));
        // The thirteen tools: each has its one button on the detail page, in their fixed order and groups,
        // named by the tool, with no access key, hidden until shown; no text in the XAML (it comes from the catalog).
        var buttons = Named(xaml, "ToolRunButtons").Elements().Where(e => e.Name.LocalName == "Button").ToList();
        Assert.Equal(ToolCatalog.All.Select(t => t.Button), buttons.Select(b => (string)b.Attribute(X + "Name")));
        Assert.Equal(ToolCatalog.All.Select(t => t.Name), buttons.Select(b => (string)b.Attribute("AutomationProperties.Name")));
        Assert.All(buttons, b => Assert.Null(b.Attribute("Content")));
        Assert.All(buttons, b => Assert.Equal("Collapsed", (string)b.Attribute("Visibility")));
        // The detail page's button is its dark primary button; the two heavy tools keep their red outline.
        Assert.Equal(new[] { "FullResetButton", "RepairWuButton" }, buttons.Where(b => (string)b.Attribute("Style") == "{StaticResource DangerPrimaryRunButton}").Select(b => (string)b.Attribute(X + "Name")));
        Assert.All(buttons.Where(b => (string)b.Attribute("Style") != "{StaticResource DangerPrimaryRunButton}"), b => Assert.Equal("{StaticResource PrimaryRunButton}", (string)b.Attribute("Style")));
        Assert.Equal(new[] { "FullResetButton", "RepairWuButton" }, ToolCatalog.All.Where(t => t.Heavy).Select(t => t.Button));
        Assert.Equal(24, ToolCatalog.All.Count);
        Assert.Equal(new[] { 5, 2, 5, 4, 4, 4 }, new[] { ToolGroup.Apps, ToolGroup.SignIn, ToolGroup.Microsoft365, ToolGroup.Windows, ToolGroup.Defender, ToolGroup.NetworkStorage }.Select(g => ToolCatalog.InGroup(g).Count));
        foreach (var tool in ToolCatalog.All)
        {
            Assert.Single(xaml.Descendants(), e => (string)e.Attribute(X + "Name") == tool.Button);
            Assert.Single(xaml.Descendants(), e => e.Name.LocalName == "Button" && (string)e.Attribute("AutomationProperties.Name") == tool.Name);
        }
        // No admin-rights fact anywhere (no tool needs admin rights, so it said nothing).
        Assert.DoesNotContain(xaml.Descendants(), e => (string)e.Attribute(X + "Name") == "FactRights" || ((string)e.Attribute("Text") ?? "").IndexOf("ADMIN RIGHTS", StringComparison.OrdinalIgnoreCase) >= 0);
        Assert.Null(typeof(ToolCatalog).GetMethod("RightsFact"));
        // The facts row holds only the two facts that can say something.
        Assert.Equal(new[] { "FactTimePanel", "FactChangesPanel" }, Named(xaml, "ToolFacts").Elements().Select(e => (string)e.Attribute(X + "Name")));
        // A problem path links to tools that exist there; it holds none of its own.
        Assert.All(ProblemPaths.All.SelectMany(p => p.Tools), b => Assert.NotNull(ToolCatalog.ByButton(b)));
    }

    [Fact]
    public void A_detail_page_shows_only_the_facts_that_say_something()
    {
        // No fact for a tool that only opens a folder or tests; the time only from a minute on.
        Assert.Empty(ToolCatalog.Facts(ToolCatalog.ByButton("LogsButton")));
        Assert.Empty(ToolCatalog.Facts(ToolCatalog.ByButton("ConnectivityButton")));
        Assert.Equal(new[] { "CLOSES OR CHANGES" }, ToolCatalog.Facts(ToolCatalog.ByButton("RefreshSignInButton")).Select(f => f.Label));
        Assert.Equal(new[] { "TYPICAL TIME", "CLOSES OR CHANGES" }, ToolCatalog.Facts(ToolCatalog.ByButton("RepairWuButton")).Select(f => f.Label));
        foreach (var tool in ToolCatalog.All)
        {
            foreach (var (label, text) in ToolCatalog.Facts(tool))
            {
                Assert.False(string.IsNullOrWhiteSpace(text), tool.Name + " " + label);
                Assert.DoesNotContain("Not needed", text, StringComparison.Ordinal);
                Assert.False(text.StartsWith("Nothing", StringComparison.Ordinal), tool.Name + ": a fact that says nothing changes says nothing");
            }
            // A typical time is a minute or longer (never "at once", "up to a minute" or "varies").
            if (tool.Time.Length > 0) { Assert.Matches("minute|hour", tool.Time); }
            Assert.DoesNotMatch("(?i)at once|up to a minute|varies", tool.Time);
        }
    }

    [Fact]
    public void The_status_of_a_run_is_fixed_above_the_footer_and_never_scrolls_away()
    {
        var xaml = MainWindow();
        var host = Named(xaml, "MainStatusHost");
        // In the footer, above the version line; not inside the page that scrolls.
        Assert.Contains(host.Ancestors(), e => (string)e.Attribute(X + "Name") == "FooterGrid");
        Assert.DoesNotContain(host.Ancestors(), e => e.Name.LocalName == "ShownOnlyScrollViewer");
        Assert.True(host.IsBefore(Named(xaml, "VersionText")));
        // The support contact is on Home and on My problem is not listed, not in the footer.
        Assert.DoesNotContain(Named(xaml, "SupportText").Ancestors(), e => (string)e.Attribute(X + "Name") == "FooterGrid");
        // A tool's detail page keeps its own place for the status.
        Assert.DoesNotContain(Named(xaml, "DetailStatusHost").Ancestors(), e => (string)e.Attribute(X + "Name") == "FooterGrid");
    }

    [Fact]
    public void A_page_scrolls_as_one_with_no_list_that_scrolls_on_its_own_inside_it()
    {
        // A scroll inside the scrolling page (the app list, the window list, the adapter list) caught the
        // wheel halfway and hid rows under the search box; a list grows in the page instead.
        var xaml = MainWindow();
        var inner = Named(xaml, "MainScroll").Descendants().Where(e => e.Name.LocalName.EndsWith("ScrollViewer", StringComparison.Ordinal));
        Assert.Empty(inner.Select(e => (string)e.Attribute(X + "Name")));
        foreach (var name in new[] { "AppListHost", "WindowListHost", "AdapterListHost" })
        {
            Assert.Null(Named(xaml, name).Attribute("MaxHeight"));
        }
    }

    [Fact]
    public void Every_tool_row_has_its_own_Run_button_beside_the_name_that_opens_the_detail_page()
    {
        var xaml = MainWindow();
        var row = xaml.Descendants().Single(e => (string)e.Attribute(X + "Key") == "ToolRowTemplate");
        var buttons = row.Descendants().Where(e => e.Name.LocalName == "Button").ToList();
        Assert.Equal(new[] { "RowName", "RowRun" }, buttons.Select(b => (string)b.Attribute(X + "Name")));
        Assert.Equal(new[] { "OnToolRow", "OnToolRowRun" }, buttons.Select(b => (string)b.Attribute("Click")));
        var run = buttons[1];
        // The row's button follows the tool's own button: its word, enabled state and help text; no access key.
        Assert.Equal("{Binding RunText}", (string)run.Attribute("Content"));
        Assert.Equal("{Binding RunStyle}", (string)run.Attribute("Style"));
        Assert.StartsWith("{Binding RunTarget.IsEnabled", (string)run.Attribute("IsEnabled"), StringComparison.Ordinal);
        Assert.Contains("RunTarget.(AutomationProperties.HelpText)", (string)run.Attribute("AutomationProperties.HelpText"));
        // One button width in every list and group: a fixed column, so a long label never narrows the names.
        var columns = row.Descendants().Where(e => e.Name.LocalName == "ColumnDefinition").ToList();
        Assert.Equal("{StaticResource ToolRunColumnWidth}", (string)columns[1].Attribute("Width"));
        Assert.Null(columns[1].Attribute("SharedSizeGroup"));
        // The name gets the whole card; how the last run ended is the line below it, with the full time as its tooltip.
        var texts = row.Descendants().Where(e => e.Name.LocalName == "TextBlock").ToList();
        Assert.Equal(new[] { "{Binding Name}", "{Binding ResultText}" }, texts.Select(t => (string)t.Attribute("Text")));
        Assert.Equal("StackPanel", texts[1].Parent.Name.LocalName);
        Assert.Equal("{Binding ResultToolTip}", (string)texts[1].Attribute("ToolTip"));
        Assert.Equal("{Binding RunId}", (string)run.Attribute("AutomationProperties.AutomationId"));
        // For IT's list and the problem paths use the same row.
        Assert.Equal("{StaticResource ToolRowTemplate}", (string)Named(xaml, "ToolList").Attribute("ItemTemplate"));
        Assert.Equal("{StaticResource ToolRowTemplate}", (string)Named(xaml, "ProblemTools").Attribute("ItemTemplate"));
        Assert.DoesNotContain(xaml.Descendants(), e => (string)e.Attribute("Click") == "OnGoToTool");
    }

    [Fact]
    public void The_window_has_one_stable_size_and_never_takes_its_height_from_its_content()
    {
        var window = MainWindow().Root;
        Assert.Null(window.Attribute("SizeToContent"));
        Assert.Equal(WindowLayout.DefaultWindowWidth, double.Parse((string)window.Attribute("Width"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(WindowLayout.DefaultWindowHeight, double.Parse((string)window.Attribute("Height"), System.Globalization.CultureInfo.InvariantCulture));
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        var code = File.ReadAllText(Path.Combine(dir.FullName, "src", "EndpointToolbelt.App", "Ui", "MainWindow.xaml.cs"));
        Assert.DoesNotContain("SizeToContent", code);
        // Nor does it cap its own height (the old cap for a window that grew with its content).
        Assert.DoesNotMatch(@"(?m)^\s+MaxHeight\s*=", code);
    }

    [Fact]
    public void One_mascot_at_the_top_right_of_the_column_and_no_other_helper()
    {
        var xaml = MainWindow();
        // One HelperCharacter in the whole window: the mascot, in the column beside the pages, at the theme's size.
        var mascot = Assert.Single(xaml.Descendants(), e => e.Name.LocalName == "HelperCharacter");
        Assert.Equal("Mascot", (string)mascot.Attribute(X + "Name"));
        Assert.Equal("ContentGrid", (string)mascot.Parent.Attribute(X + "Name"));
        Assert.Equal("{StaticResource MascotSize}", (string)mascot.Attribute("Width"));
        Assert.Equal("{StaticResource MascotSize}", (string)mascot.Attribute("Height"));
        Assert.Equal("Right", (string)mascot.Attribute("HorizontalAlignment"));
        Assert.Equal("Top", (string)mascot.Attribute("VerticalAlignment"));
        foreach (var old in new[] { "HomeHelper", "PageHelper", "RunHelper", "ProblemsHelperHost", "PathHelperHost", "ItHelperHost" })
        {
            Assert.DoesNotContain(xaml.Descendants(), e => (string)e.Attribute(X + "Name") == old);
        }
        // Each page's first line leaves the mascot its room.
        foreach (var first in new[] { "StatusCard", "ProblemsHeader", "ProblemHeader" })
        {
            Assert.Equal("{StaticResource BesideMascot}", (string)Named(xaml, first).Attribute("Margin"));
        }
        Assert.Equal("0,0,60,8", (string)Named(xaml, "ItHeader").Attribute("Margin"));
    }

    [Fact]
    public void The_tabs_are_always_at_the_top_and_there_is_no_left_pane()
    {
        // No left pane: the three tabs in one row at the top, For IT support the last one.
        var xaml = MainWindow();
        var bar = Named(xaml, "NavBar");
        Assert.Equal("Horizontal", (string)bar.Attribute("Orientation"));
        Assert.Equal(new[] { "HomeNav", "ProblemsNav", "ItNav" }, bar.Elements().Select(b => (string)b.Attribute(X + "Name")));
        Assert.Equal("0", (string)Named(xaml, "TabsGrid").Attribute("Grid.Row"));
        foreach (var gone in new[] { "NavPane", "NavTasks", "NavColumn", "NavReportButton", "NavPackageButton", "ItDetailColumn", "ItListColumn", "AppsDetailColumn", "AppsListColumn" })
        {
            Assert.DoesNotContain(xaml.Descendants(), e => (string)e.Attribute(X + "Name") == gone);
        }
    }

    [Fact]
    public void Existing_access_keys_stay_where_their_controls_are()
    {
        // Every access key whose control still exists stays; Check again on Home took Refresh's Alt+E.
        var xaml = MainWindow();
        string Content(string name) => (string)Named(xaml, name).Attribute("Content");
        Assert.Equal("Ho_me", Content("HomeNav"));
        Assert.Equal("Choose a _problem", Content("ProblemsNav"));
        Assert.Equal("_For IT support", Content("ItNav"));
        Assert.Equal("Check my _apps", Content("ReviewAppsLink"));
        Assert.Equal("← _Back", Content("BackButton"));
        Assert.Equal("_Retry work apps and settings", Content("RetryButton"));
        Assert.Equal("Re_load the app list", Content("ReloadAppsButton"));
        Assert.Equal("M_y app is not in this list", Content("NotListedButton"));
        Assert.Equal("Check this app aga_in", Content("CheckAgainButton"));
        Assert.Equal("_View report", Content("ReportButton"));
        // The HTML export has no access key: no letter of its label is free.
        Assert.Equal("Export HTML", Content("HtmlReportButton"));
        Assert.Equal("Support pac_kage", Content("PackageButton"));
        Assert.Equal("Searc_h tools", Content("ToolSearchLabel"));
        Assert.Equal("Copy details for I_T", Content("CopyTicketButton"));
        Assert.Equal("Ch_eck this PC again", Content("RefreshButton"));
    }


    [Theory]
    [InlineData("Company Portal", "PortalButton")]
    [InlineData("company portal", "PortalButton")]
    [InlineData("IME", "LogsButton")]
    [InlineData("BitLocker", "BackupKeyButton")]
    [InlineData("bitlocker", "BackupKeyButton")]
    [InlineData("clock", "SyncClockButton")]
    [InlineData("Teams", "TeamsCacheButton")]
    [InlineData("Defender", "UpdateDefenderButton")]
    [InlineData("VPN", "ConnectivityButton")]
    public void The_tool_search_finds_the_right_tool_first(string query, string button)
    {
        Assert.Equal(button, ToolCatalog.Search(query).First().Button);
    }

    [Fact]
    public void The_tool_search_matches_word_starts_only_and_an_empty_search_shows_every_tool()
    {
        var ime = ToolCatalog.Search("IME").Select(t => t.Button).ToList();
        Assert.Equal(new[] { "LogsButton", "FullResetButton" }, ime.Take(2));
        // "ime" is not the end of "time": the clock is not an IME tool.
        Assert.DoesNotContain("SyncClockButton", ime);
        Assert.Equal(new[] { "SyncClockButton" }, ToolCatalog.Search("clock").Select(t => t.Button));
        // The app row's title ("An app from Company Portal won't install") finds the app tools too; the named tool comes first.
        Assert.Equal("PortalButton", ToolCatalog.Search("Company Portal").First().Button);
        // The compliance check reads BitLocker and points to Company Portal; the tool named so comes first.
        Assert.Contains(ToolCatalog.Search("Company Portal"), t => t.Button == "ComplianceButton");
        Assert.Equal(new[] { "BackupKeyButton", "ComplianceButton" }, ToolCatalog.Search("BitLocker").Select(t => t.Button));
        Assert.Equal("ComplianceButton", ToolCatalog.Search("not compliant").First().Button);
        Assert.Equal(24, ToolCatalog.Search("").Count);
        Assert.Equal(24, ToolCatalog.Search("   ").Count);
        Assert.Empty(ToolCatalog.Search("no such tool"));
        Assert.Contains(ToolCatalog.Search("sign-in"), t => t.Button == "RefreshSignInButton");
    }

    [Fact]
    public void No_header_one_line_rows_the_copy_links_behind_View_details_and_Close_in_the_footer_line()
    {
        var xaml = MainWindow();
        string Name(XElement e) => (string)e.Attribute(X + "Name");
        // No in-window title or subtitle: the window title names the product.
        Assert.DoesNotContain(xaml.Descendants(), e => Name(e) is "HeaderPanel" or "HeaderText" or "DescriptionText" or "HealthPointHint" or "HelperSettingBox");
        // Copy details for IT lives in the drawer, which is collapsed.
        var drawer = Named(xaml, "Drawer");
        Assert.Equal("Collapsed", (string)drawer.Attribute("Visibility"));
        Assert.Contains(drawer.Descendants(), e => Name(e) == "CopyTicketButton");
        // The status area is collapsed until a run shows it; its word and one line.
        var status = Named(xaml, "StatusPanel");
        Assert.Equal("Collapsed", (string)status.Attribute("Visibility"));
        Assert.Equal("NoWrap", (string)Named(xaml, "StatusLabel").Attribute("TextWrapping"));
        Assert.Contains(status.Descendants(), e => Name(e) == "StatusWord");
        Assert.Contains(status.Descendants(), e => Name(e) == "DetailsButton");
        // Close shares the footer line with the version and About, and keeps its key and automation ID.
        var close = Named(xaml, "CloseButton");
        Assert.Equal("_Close", (string)close.Attribute("Content"));
        Assert.Contains(close.Parent.Elements(), e => Name(e) == "VersionText");
        // The health link sits on the verdict's line.
        Assert.Same(Named(xaml, "HealthVerdict").Parent, Named(xaml, "HealthToggle").Parent);
        // A tool row is one line: no line on what it does under the name.
        var toolRow = xaml.Descendants().Single(e => (string)e.Attribute(X + "Key") == "ToolRowTemplate");
        Assert.DoesNotContain(toolRow.Descendants(), e => (string)e.Attribute("Text") is "{Binding Line}" or "{Binding GroupLine}");
    }
}
