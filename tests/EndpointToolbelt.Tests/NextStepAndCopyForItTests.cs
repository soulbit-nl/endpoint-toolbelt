using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Platform;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.State;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// Copy details for IT on Home and every problem path, the next step that fits each row (Retry apps &amp;
/// policies only where it can help), the first-line facts in the report and in Copy details for IT, the
/// uptime on Home with its signal after a week or with a waiting restart (the app never restarts the device).
/// </summary>
public class NextStepAndCopyForItTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Next step test zone", TimeSpan.FromHours(1), "Next step test zone", "Next step test zone");
    private const string EnrollmentId = "aaaaaaaa-1111-2222-3333-444444444444";

    private static DsregInfo Dsreg(string entra, string domain, string workplace)
    {
        var lines = new List<string>();
        if (entra != null) { lines.Add("AzureAdJoined : " + entra); }
        if (domain != null) { lines.Add("DomainJoined : " + domain); }
        if (workplace != null) { lines.Add("WorkplaceJoined : " + workplace); }
        return DsregInfo.Parse(lines);
    }

    private static NetAdapter Adapter(int index, AdapterKind kind, bool physical = true, bool media = true, bool usable = true, string name = "Contoso Adapter")
        => new() { InterfaceIndex = index, Name = name, Kind = kind, Physical = physical, Enabled = true, DriverOk = true, MediaConnected = media, Dhcp = true, UsableIpv4 = usable };

    // --- The join type, the network, the uptime -------------------------------------------------------

    [Fact]
    public void The_join_type_comes_from_the_dsregcmd_fields_the_health_checks_already_read()
    {
        Assert.Equal(JoinType.EntraJoined, FirstLineFacts.JoinOf(Dsreg("YES", "NO", "NO")));
        Assert.Equal(JoinType.HybridJoined, FirstLineFacts.JoinOf(Dsreg("YES", "YES", "NO")));
        Assert.Equal(JoinType.Registered, FirstLineFacts.JoinOf(Dsreg("NO", "NO", "YES")));
        Assert.Equal(JoinType.DomainOnly, FirstLineFacts.JoinOf(Dsreg("NO", "YES", "NO")));
        Assert.Equal(JoinType.NotJoined, FirstLineFacts.JoinOf(Dsreg("NO", "NO", "NO")));
        // Nothing read (dsregcmd failed or gave no device state): unknown, never "not joined".
        Assert.Equal(JoinType.Unknown, FirstLineFacts.JoinOf(new DsregInfo()));
        Assert.Equal(JoinType.Unknown, FirstLineFacts.JoinOf(null));
        Assert.Equal("Microsoft Entra hybrid joined", FirstLineFacts.JoinWords(JoinType.HybridJoined));
        Assert.Equal("Microsoft Entra registered (personal or BYOD device)", FirstLineFacts.JoinWords(JoinType.Registered));
    }

    [Fact]
    public void The_network_summary_names_the_kind_and_the_address_state_never_a_name_or_address()
    {
        Assert.Equal("unknown (the network adapters could not be read)", NetworkSummary.Of(null).Text());
        Assert.Equal("connected no (no cable or Wi-Fi connection)", NetworkSummary.Of(new[] { Adapter(3, AdapterKind.Ethernet, media: false) }).Text());
        // A VPN adapter is not the connection.
        Assert.Equal("connected no (no cable or Wi-Fi connection)", NetworkSummary.Of(new[] { Adapter(9, AdapterKind.Other, physical: false) }).Text());
        // The adapter with a usable address is named first; then Ethernet before Wi-Fi.
        var both = NetworkSummary.Of(new[] { Adapter(5, AdapterKind.Ethernet, usable: false), Adapter(7, AdapterKind.WiFi, name: "Contoso Guest") });
        Assert.Equal("connected yes, Wi-Fi, usable address yes", both.Text());
        Assert.DoesNotContain("Contoso", both.Text());
        Assert.Equal("connected yes, Ethernet, usable address no", NetworkSummary.Of(new[] { Adapter(5, AdapterKind.Ethernet, usable: false) }).Text());
    }

    [Fact]
    public void The_uptime_is_said_in_plain_words_and_signalled_after_a_week_or_with_a_pending_restart()
    {
        Assert.Equal("Restarted just now", Uptime.Line(Now.AddSeconds(-20), Now));
        Assert.Equal("Restarted 1 minute ago", Uptime.Line(Now.AddMinutes(-1), Now));
        Assert.Equal("Restarted 3 hours ago", Uptime.Line(Now.AddHours(-3.5), Now));
        Assert.Equal("Restarted 1 day ago", Uptime.Line(Now.AddHours(-30), Now));
        Assert.Equal("Restarted 12 days ago", Uptime.Line(Now.AddDays(-12.4), Now));
        Assert.Equal("", Uptime.Line(null, Now));
        Assert.Equal("", Uptime.Line(Now.AddHours(1), Now));

        Assert.Equal(TimeSpan.FromDays(7), Uptime.LongUptime);
        Assert.Equal("", Uptime.Signal(Now.AddDays(-6.9), Now, restartPending: false));
        Assert.Equal("no restart for over a week", Uptime.Signal(Now.AddDays(-7), Now, restartPending: false));
        Assert.Equal("a restart is waiting", Uptime.Signal(Now.AddHours(-1), Now, restartPending: true));
        Assert.Equal("", Uptime.Signal(null, Now, restartPending: false));
        Assert.Equal("Restarted 12 days ago · no restart for over a week", Uptime.HomeLine(Now.AddDays(-12.4), Now, restartPending: false));
        Assert.Equal("the device has not restarted for 12 days", Uptime.ItSignal(Now.AddDays(-12.4), Now, restartPending: false));
        Assert.Equal("a restart is waiting", Uptime.ItSignal(Now.AddDays(-12.4), Now, restartPending: true));
    }

    // --- The facts block ------------------------------------------------------------------------------

    [Fact]
    public void The_enrollment_ID_is_shown_only_as_a_GUID_and_otherwise_says_why_there_is_none()
    {
        FirstLineFacts Facts(StatusInfo status) => FirstLineFacts.From(Now.AddDays(-2), Dsreg("YES", "NO", "NO"), status, new NetworkSummary());
        Assert.Equal(EnrollmentId, Facts(new StatusInfo { Enrolled = true, MdmEnrollmentId = EnrollmentId.ToUpperInvariant() }).MdmText());
        Assert.Equal("unknown (the service did not report it)", Facts(new StatusInfo { Enrolled = true, MdmEnrollmentId = "not a guid" }).MdmText());
        Assert.Equal("none (not enrolled in Intune)", Facts(new StatusInfo { Enrolled = false }).MdmText());
        Assert.Equal("unknown (more than one enrollment, or it could not be read)", Facts(new StatusInfo { Enrolled = null }).MdmText());
        Assert.Equal("unknown (the Endpoint Toolbelt service is not available)", Facts(null).MdmText());
    }

    private static FirstLineFacts Sample() => FirstLineFacts.From(Now.AddDays(-12).AddHours(-1), Dsreg("YES", "YES", "NO"),
        new StatusInfo { Enrolled = true, MdmEnrollmentId = EnrollmentId }, NetworkSummary.Of(new[] { Adapter(7, AdapterKind.WiFi) }));

    [Fact]
    public void The_ticket_facts_are_the_version_restart_join_type_enrollment_ID_network_and_one_health_line()
    {
        var rows = HealthView.Rows(CompactLayoutTests.Health(warn: HealthChecks.PendingRestart, fail: HealthChecks.Clock));
        Assert.Equal(new[]
        {
            "App version · 1.2.3",
            "Last restart · 2026-11-01 09:00 UTC+01:00 (12 days ago); for IT: the device has not restarted for 12 days",
            "Join type · Microsoft Entra hybrid joined",
            "MDM enrollment ID · " + EnrollmentId,
            "Network · connected yes, Wi-Fi, usable address yes",
            "Health · 1 problem: Clock; 1 warning: Pending restart; 8 OK",
        }, Sample().TicketLines("1.2.3", rows, Now, Zone));
        Assert.Equal("all 10 checks OK", HealthView.OneLine(HealthView.Rows(CompactLayoutTests.Health())));
        Assert.Equal("not checked yet", HealthView.OneLine(new List<HealthResult>()));
    }

    [Fact]
    public void The_device_report_starts_with_the_first_line_facts_before_the_device_facts()
    {
        var generated = new DateTimeOffset(Now).ToOffset(TimeSpan.FromHours(1));
        var report = DeviceReport.Build(new DeviceFacts { ComputerName = "PC1" }, null, new List<HealthResult>(), Array.Empty<string>(), generated, "1.2.3", null, Zone, Sample());
        var lines = report.Split(new[] { "\r\n" }, StringSplitOptions.None);
        Assert.StartsWith("===== ENDPOINT TOOLBELT - DEVICE REPORT =====", lines[0]);
        Assert.StartsWith("Generated : ", lines[1]);
        Assert.StartsWith("Time zone : ", lines[2]);
        Assert.Equal("Restarted : 2026-11-01 09:00 UTC+01:00 (12 days ago); for IT: the device has not restarted for 12 days", lines[3]);
        Assert.Equal("Join type : Microsoft Entra hybrid joined", lines[4]);
        Assert.Equal("MDM ID    : " + EnrollmentId + " (MDM enrollment ID, EntDMID)", lines[5]);
        Assert.Equal("Network   : connected yes, Wi-Fi, usable address yes", lines[6]);
        Assert.Equal("", lines[7]);
        Assert.StartsWith("Device    : PC1", lines[8]);
        // Without facts the report is as before.
        Assert.StartsWith("Device    : PC1", DeviceReport.Build(new DeviceFacts { ComputerName = "PC1" }, null, new List<HealthResult>(), Array.Empty<string>(), generated, "1.2.3", null, Zone)
            .Split(new[] { "\r\n" }, StringSplitOptions.None)[3]);
    }

    [Fact]
    public void A_tool_ticket_carries_the_facts_right_after_its_first_line_and_is_unchanged_without_them()
    {
        var header = new RunLogHeader { Check = new LocalRun { Name = "Sync clock", RunId = "local-ab12cd34", StartedUtc = Now, EndedUtc = Now }, User = @"CONTOSO\user", DeviceName = "PC-CONTOSO-01", Zone = Zone, NowUtc = Now };
        var cards = new[] { ResultCard.From(FindingSeverity.Ok, "The clock is in sync") };
        var facts = new[] { "App version · 1.2.3", "Network · connected yes, Ethernet, usable address yes" };
        var lines = RunTicket.Text(header, cards, "Done.", Tone.Success, facts: facts).Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(7, lines.Length);
        Assert.Equal("Endpoint Toolbelt · Sync clock · Reference local-ab12cd34", lines[0]);
        Assert.Equal(facts, lines.Skip(1).Take(2));
        Assert.StartsWith("Evidence · ", lines[3]);
        Assert.StartsWith("Next · ", lines[6]);
        Assert.Equal(5, RunTicket.Text(header, cards, "Done.", Tone.Success).Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    // --- The next step per row ------------------------------------------------------------------------

    [Fact]
    public void Retry_apps_and_policies_is_the_next_step_only_for_the_Intune_and_IME_rows()
    {
        string Step(string name, HealthStatus status = HealthStatus.Fail) => HealthChecks.NextStep(HealthView.Rows(CompactLayoutTests.Health(
            warn: status == HealthStatus.Warn ? name : null, fail: status == HealthStatus.Fail ? name : null)).Single(r => r.Status != HealthStatus.Pass));
        Assert.Equal("Try Retry work apps and settings.", Step(HealthChecks.ImeService));
        Assert.Equal("Try Retry work apps and settings.", Step(HealthChecks.ImeCheckIn, HealthStatus.Warn));
        Assert.Equal("Try Retry work apps and settings.", Step(HealthChecks.LastSync));
        Assert.Equal("Try Retry work apps and settings.", Step(HealthChecks.MdmCertificate));
        Assert.Equal("Try Retry work apps and settings.", Step(HealthChecks.Profiles));
        // Every other row has its own step, or none.
        Assert.Equal("Try Sync clock.", Step(HealthChecks.Clock));
        Assert.Equal("Try Free up disk space.", Step(HealthChecks.FreeSpace));
        Assert.Equal("Try Check Microsoft connections.", Step(HealthChecks.Connectivity));
        Assert.Equal("Try Refresh work account sign-in.", Step(HealthChecks.Prt, HealthStatus.Warn));
        Assert.Equal("Restart when your organization allows it, then try again.", Step(HealthChecks.PendingRestart, HealthStatus.Warn));
        Assert.Equal("Contact your IT support.", Step(HealthChecks.EntraJoined));
        Assert.Equal("Contact your IT support.", Step(HealthChecks.Enrollment));
        Assert.Equal("", HealthChecks.NextStep(new HealthResult { Name = "x", Status = HealthStatus.Fail }));
        Assert.Equal("", HealthChecks.NextStep(new HealthResult { Name = HealthChecks.Clock, Status = HealthStatus.Pass }));

        // The status after the checks names the first problem's own step.
        Assert.Equal(("1 problem found. Clock: try Sync clock.", Tone.Error), HealthChecks.Summary(HealthView.Rows(CompactLayoutTests.Health(fail: HealthChecks.Clock))));
        Assert.Equal(("1 problem found. Work account: contact your IT support.", Tone.Error), HealthChecks.Summary(HealthView.Rows(CompactLayoutTests.Health(fail: HealthChecks.EntraJoined))));
        Assert.Equal(("1 problem found. App installer (IME): try Retry work apps and settings.", Tone.Error), HealthChecks.Summary(HealthView.Rows(CompactLayoutTests.Health(fail: HealthChecks.ImeService))));
        Assert.DoesNotContain("Retry", HealthChecks.Summary(HealthView.Rows(CompactLayoutTests.Health(fail: HealthChecks.FreeSpace))).Text);
    }

    // --- Home and the problem paths -----------------------------------------------------------------

    private static App.Ui.MainWindow Window(List<HealthResult> health, StatusInfo status = null, DsregInfo dsreg = null)
    {
        var window = MainWindowPagesTests.NewWindow();
        MainWindowPagesTests.Invoke(window, "ApplyHealth", status ?? new StatusInfo { Enrolled = true, MdmEnrollmentId = EnrollmentId }, false, health, dsreg ?? Dsreg("YES", "NO", "NO"), false);
        return window;
    }

    private static string[] Lines(string text) => text.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Home_offers_Copy_for_IT_and_its_ticket_has_the_facts_the_health_line_and_the_finding_s_own_step()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = Window(CompactLayoutTests.Health(fail: HealthChecks.Clock));
            var link = MainWindowPagesTests.Get<Button>(window, "HomeCopyTicketButton");
            Assert.Equal("Copy details for IT", link.Content);
            Assert.Equal("Copy details for IT: Home", AutomationProperties.GetName(link));
            var lines = Lines(window.HomeTicketText());
            Assert.Equal("Endpoint Toolbelt · Home · Reference none", lines[0]);
            Assert.StartsWith("App version · ", lines[1]);
            Assert.Equal("Last restart · ", lines[2].Substring(0, "Last restart · ".Length));
            Assert.EndsWith("(3 hours ago)", lines[2]);
            Assert.Equal("Join type · Microsoft Entra joined", lines[3]);
            Assert.Equal("MDM enrollment ID · " + EnrollmentId, lines[4]);
            Assert.StartsWith("Network · ", lines[5]);
            Assert.Equal("Health · 1 problem: Clock; 9 OK", lines[6]);
            Assert.StartsWith("Evidence · ", lines[7]);
            Assert.Contains("health checks not run yet · PROBLEM: Clock: ", lines[7]);
            Assert.Equal("Action · None: copied from Home; nothing was run or changed", lines[8]);
            Assert.StartsWith("Outcome · Clock: Problem. ", lines[9]);
            Assert.Equal("Next · User: Try Sync clock.", lines[10]);
            Assert.Equal(11, lines.Length);
            Assert.DoesNotContain(Environment.MachineName, string.Join("\n", lines), StringComparison.OrdinalIgnoreCase);

            // A clean device: no step for the user; IT support is pointed to the facts.
            window = Window(CompactLayoutTests.Health());
            Assert.Equal("Next · IT support: no check found a step for the user; the facts and the health line above are this device's state.", Lines(window.HomeTicketText()).Last());
        });
    }

    [Fact]
    public void Every_problem_path_offers_Copy_for_IT_with_its_checks_and_verdict_and_never_Retry_for_a_row_it_cannot_help()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = Window(CompactLayoutTests.Health(fail: HealthChecks.Clock));
            var link = MainWindowPagesTests.Get<Button>(window, "PathCopyTicketButton");
            Assert.Equal(Visibility.Collapsed, link.Visibility);
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.CantSignIn, false);
            Assert.Equal(Visibility.Visible, link.Visibility);
            Assert.Equal("Copy details for IT: Teams or Office keeps asking me to sign in", AutomationProperties.GetName(link));
            var lines = Lines(window.PathTicketText());
            Assert.Equal("Endpoint Toolbelt · Teams or Office keeps asking me to sign in · Reference none", lines[0]);
            Assert.Equal("Health · 1 problem: Clock; 9 OK", lines[6]);
            Assert.StartsWith("Outcome · Problem: Clock.", lines[9]);
            Assert.Equal("Next · User: Try Sync clock.", lines[10]);

            foreach (var problem in new[] { Problem.WindowsWontUpdate, Problem.CantConnect, Problem.OfficeCloses, Problem.OneDriveSync })
            {
                MainWindowPagesTests.Call(window, "ShowProblem", problem, false);
                Assert.Equal(Visibility.Visible, link.Visibility);
                var ticket = Lines(window.PathTicketText());
                Assert.Equal("Endpoint Toolbelt · " + ProblemPaths.Get(problem).Title + " · Reference none", ticket[0]);
                Assert.DoesNotContain("Retry work apps and settings", string.Join("\n", ticket));
            }

            // The app path before a diagnosis: its install blockers and the IME rows.
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Apps"), false);
            Assert.Equal(Visibility.Visible, link.Visibility);
            Assert.StartsWith("Endpoint Toolbelt · An app from Company Portal won't install · Reference none", window.PathTicketText());
            // Not on the window page or For IT.
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Windows"), false);
            Assert.Equal(Visibility.Collapsed, link.Visibility);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false);
            Assert.Equal(Visibility.Collapsed, link.Visibility);
        });
    }

    [Fact]
    public void The_app_path_s_Copy_for_IT_after_a_diagnosis_is_the_diagnosis_ticket_with_the_facts()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = Window(CompactLayoutTests.Health());
            var now = DateTime.UtcNow;
            var run = MainWindowPagesTests.Diagnosis("diag0056", now.AddMinutes(-1), "RETRY-HELPS", now.AddHours(-1), "0x80070643");
            MainWindowPagesTests.Call(window, "SetDiagnosis", AppDecisions.From(run, now), run);
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Apps"), false);
            var lines = Lines(window.PathTicketText());
            Assert.Equal("Endpoint Toolbelt · App diagnosis · Reference diag0056", lines[0]);
            Assert.StartsWith("App version · ", lines[1]);
            Assert.Equal("Health · all 10 checks OK", lines[6]);
            Assert.StartsWith("Evidence · ", lines[7]);
        });
    }

    // --- The uptime signal (no restart in the app) ------------------------------------------------------

    [Fact]
    public void Home_shows_the_uptime_and_signals_a_week_without_a_restart_or_a_waiting_restart_with_no_restart_button()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = Window(CompactLayoutTests.Health());
            var text = MainWindowPagesTests.Get<TextBlock>(window, "UptimeText");
            Assert.Equal("Restarted 3 hours ago", text.Text);
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<DockPanel>(window, "UptimeLine").Visibility);

            window.BootClock = () => DateTime.UtcNow.AddDays(-12).AddMinutes(-5);
            MainWindowPagesTests.Call(window, "ShowHome");
            Assert.Equal("Restarted 12 days ago · no restart for over a week", text.Text);

            window.BootClock = () => DateTime.UtcNow.AddMinutes(-61);
            MainWindowPagesTests.Invoke(window, "ApplyHealth", new StatusInfo(), false, CompactLayoutTests.Health(warn: HealthChecks.PendingRestart), new DsregInfo(), false);
            Assert.Equal("Restarted 1 hour ago · a restart is waiting", text.Text);

            // The app never restarts the device; nothing in the window offers it.
            Assert.Null(window.FindName("RestartNowButton"));
            Assert.Null(typeof(App.Ui.MainWindow).GetMethod("OnRestartNow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
            var root = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "EndpointToolbelt.slnx"))) { root = root.Parent; }
            Assert.NotNull(root);
            var xaml = File.ReadAllText(Path.Combine(root.FullName, "src", "EndpointToolbelt.App", "Ui", "MainWindow.xaml"));
            Assert.DoesNotContain("Restart now", xaml);

            // An unknown start time shows no line.
            window.BootClock = () => null;
            MainWindowPagesTests.Call(window, "ShowHome");
            Assert.Equal(Visibility.Collapsed, MainWindowPagesTests.Get<DockPanel>(window, "UptimeLine").Visibility);
        });
    }

    [Fact]
    public void Home_with_the_restart_signal_and_Copy_for_IT_still_fits_the_default_size_and_passes_the_Axe_rules()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = Window(CompactLayoutTests.Health(warn: HealthChecks.Clock, fail: HealthChecks.PendingRestart));
            Assert.EndsWith(" · a restart is waiting", MainWindowPagesTests.Get<TextBlock>(window, "UptimeText").Text);
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(WindowLayout.DefaultClientWidth, WindowLayout.CompactClientHeight));
            content.Arrange(new Rect(0, 0, WindowLayout.DefaultClientWidth, WindowLayout.CompactClientHeight));
            content.UpdateLayout();
            var scroll = MainWindowPagesTests.Get<ScrollViewer>(window, "MainScroll");
            Assert.True(scroll.ExtentHeight <= scroll.ViewportHeight + 0.5, "Home needs " + scroll.ExtentHeight + " px but has " + scroll.ViewportHeight + " px.");
            AccessibilityTests.AssertAxeRules(window, "Home with the restart signal");
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.CantSignIn, false);
            AccessibilityTests.AssertAxeRules(window, "a path with Copy details for IT");
        });
    }

    [Fact]
    public void A_waiting_restart_reaches_the_tickets_as_a_fact_for_IT_and_never_as_an_order_to_restart()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = Window(CompactLayoutTests.Health(warn: HealthChecks.PendingRestart));
            var lines = Lines(window.HomeTicketText());
            Assert.EndsWith("(3 hours ago); for IT: a restart is waiting", lines[2]);
            Assert.Equal("Next · User: Restart when your organization allows it, then try again. IT support: A restart is waiting on this device.", lines.Last());
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.WindowsWontUpdate, false);
            var path = window.PathTicketText();
            Assert.Contains("for IT: a restart is waiting", path);
            Assert.DoesNotContain("Restart the device", path);
            Assert.DoesNotContain("Restart now", path);

            // A week without a restart, nothing pending: said to IT with the days.
            window = Window(CompactLayoutTests.Health());
            window.BootClock = () => DateTime.UtcNow.AddDays(-9).AddMinutes(-1);
            Assert.EndsWith("(9 days ago); for IT: the device has not restarted for 9 days", Lines(window.HomeTicketText())[2]);
        });
    }

    // --- The service ---------------------------------------------------------------------------------

    private sealed class NoRegistry : IRegistryReader
    {
        public object GetValue(string subKey, string name) => null;
    }

    [Fact]
    public void The_service_reports_the_enrollment_ID_only_as_a_GUID()
    {
        DeviceStateReader Reader(Func<string> id) => new(new NoRegistry(), () => Now, () => Now.AddHours(-1), () => new[] { ("MS DM Server", 1) }, () => (null, null), id);
        Assert.Equal(EnrollmentId, Reader(() => "{" + EnrollmentId.ToUpperInvariant() + "}").GetMdmEnrollmentId());
        Assert.Equal(EnrollmentId, Reader(() => " " + EnrollmentId + " ").GetMdmEnrollmentId());
        Assert.Null(Reader(() => "not-a-guid").GetMdmEnrollmentId());
        Assert.Null(Reader(() => null).GetMdmEnrollmentId());
        Assert.Null(Reader(() => throw new UnauthorizedAccessException()).GetMdmEnrollmentId());
        // A test's own enrollments never read this PC's enrollment ID.
        Assert.Null(new DeviceStateReader(new NoRegistry(), () => Now, () => Now, () => Array.Empty<(string, int)>()).GetMdmEnrollmentId());
    }
}
