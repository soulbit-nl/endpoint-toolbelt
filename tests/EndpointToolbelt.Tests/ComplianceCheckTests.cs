using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.App.Ui;
using EndpointToolbelt.Protocol;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// "My device is blocked or 'not compliant'": one row per item the device can read itself, from
/// the readouts that exist; one next step per item that is not OK (for the user, or "IT:"); Retry apps
/// &amp; policies only where a sync can change the verdict; never a compliance verdict; the check's run
/// log, history and Copy details for IT without names, addresses or paths.
/// </summary>
public class ComplianceCheckTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

    private static ComplianceReadout AllOn() => new()
    {
        BitLocker = "on", RecoveryPassword = true, SecureBoot = "on", Tpm = "ready", TpmVersion = "2.0",
        FirewallDomain = "on", FirewallPrivate = "on", FirewallPublic = "on", OsBuild = "10.0.26100.1234", OsVersion = "24H2",
    };

    private static StatusInfo Status(ComplianceReadout c) => new() { Readouts = new DeviceReadouts { Compliance = c } };

    /// <summary>The detailed checks, all OK unless named; <paramref name="defender"/> replaces the Defender row.</summary>
    private static List<HealthResult> Detailed(string warn = null, string fail = null, HealthResult defender = null)
    {
        var list = CompactLayoutTests.Health(warn, fail);
        if (defender != null) { list[list.FindIndex(r => r.Name == HealthChecks.Defender)] = defender; }
        return list;
    }

    private static HealthResult Named(IEnumerable<HealthResult> rows, string name) => rows.Single(r => r.Name == name);

    [Fact]
    public void The_path_has_the_owner_s_title_and_subtitle_its_own_rows_and_three_tools()
    {
        var path = ProblemPaths.Get(Problem.DeviceBlocked);
        Assert.Equal("My device is blocked or 'not compliant'", path.Title);
        Assert.Equal("Company Portal or a sign-in says it doesn't meet requirements", path.Subtitle);
        Assert.Contains(path, ProblemPaths.All);
        Assert.Equal(new[] { "ComplianceButton", "UpdateDefenderButton", "RepairWuButton" }, path.Tools);
        Assert.All(path.Tools, b => Assert.NotNull(ToolCatalog.ByButton(b)));
        Assert.Equal(CompliancePath.RowNames, path.Rows);
        // The intro says plainly what cannot be known, and where the verdict is.
        Assert.Contains("cannot see the compliance verdict or why sign-in was blocked", path.Intro);
        Assert.Contains("Company Portal", path.Intro);
        Assert.Contains("Check status", path.Intro);
        // The check is a tool of For IT that runs in the app, read-only.
        var tool = ToolCatalog.ByButton("ComplianceButton");
        Assert.Equal(ToolGroup.WindowsSecurity, tool.Group);
        Assert.Equal(CompliancePath.CheckName, tool.CheckName);
        Assert.False(tool.InService);
        Assert.Empty(ToolCatalog.Facts(tool));
        Assert.False(ButtonRules.NeedsService(AppButton.Compliance));
    }

    [Fact]
    public void All_on_gives_five_OK_rows_and_a_verdict_that_never_says_compliant()
    {
        var rows = CompliancePath.Rows(Detailed(), Status(AllOn()));
        Assert.Equal(CompliancePath.RowNames, rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.Equal(HealthStatus.Pass, r.Status));
        Assert.Equal("Enrolled; last sync 2026-11-12 15:34 (48 min ago)", Named(rows, CompliancePath.Intune).Detail);
        Assert.Equal("24H2, build 10.0.26100.1234; no restart pending", Named(rows, CompliancePath.WindowsRestart).Detail);
        Assert.Equal("Defender up to date; firewall on for every network", Named(rows, CompliancePath.DefenderFirewall).Detail);
        Assert.Equal("Secure Boot on; TPM ready (2.0)", Named(rows, CompliancePath.SecureBootTpm).Detail);
        var (text, tone) = CompliancePath.Verdict(rows);
        Assert.Equal(Tone.Neutral, tone);
        Assert.Equal("The settings this device can check look fine.", text);
        Assert.DoesNotContain("compliant", text, StringComparison.OrdinalIgnoreCase);
        Assert.All(rows, r => Assert.Equal("", CompliancePath.NextStep(r)));
        // ProblemPaths.Verdict uses the path's own words.
        Assert.Equal(text, ProblemPaths.Verdict(ProblemPaths.Get(Problem.DeviceBlocked), rows).Text);
    }

    [Fact]
    public void Each_item_that_is_off_gets_exactly_one_next_step_for_the_user_or_for_IT()
    {
        var c = AllOn();
        c.BitLocker = "off";
        c.FirewallPublic = "off";
        c.SecureBoot = "unsupported";
        c.Tpm = "not-ready";
        c.OsBuild = null;
        var rows = CompliancePath.Rows(Detailed(warn: HealthChecks.PendingRestart), Status(c));
        var notOk = rows.Where(r => r.Status != HealthStatus.Pass).Select(r => r.Name).ToList();
        Assert.Equal(new[] { CompliancePath.BitLocker, CompliancePath.DefenderFirewall, CompliancePath.SecureBootTpm, CompliancePath.WindowsRestart }, notOk);
        foreach (var row in rows.Where(r => r.Status != HealthStatus.Pass))
        {
            var step = CompliancePath.NextStep(row);
            Assert.False(string.IsNullOrWhiteSpace(step), row.Name);
            // One step: one sentence, shown whole under the row.
            Assert.DoesNotContain(". ", step.TrimEnd('.'));
            Assert.Equal(step, row.HintBelow);
            // No fix link on these: a sync does not turn a setting on.
            Assert.Equal(HealthFix.None, row.Fix);
        }
        Assert.Equal("Off for the Windows drive", Named(rows, CompliancePath.BitLocker).Detail);
        Assert.StartsWith("IT: turn on BitLocker", CompliancePath.NextStep(Named(rows, CompliancePath.BitLocker)));
        // To fit: the firewall shares a row with Defender; the item that is not OK comes first, with its step.
        Assert.Equal("Firewall off for public networks; Defender up to date", Named(rows, CompliancePath.DefenderFirewall).Detail);
        Assert.StartsWith("IT: turn Windows Firewall back on", CompliancePath.NextStep(Named(rows, CompliancePath.DefenderFirewall)));
        // Both items of a row not OK: the first one's step (the Windows version), both details.
        Assert.StartsWith("Unknown: could not be read; ", Named(rows, CompliancePath.WindowsRestart).Detail);
        Assert.StartsWith("IT: check the Windows version", CompliancePath.NextStep(Named(rows, CompliancePath.WindowsRestart)));
        // Secure Boot and the TPM: one row, one step for both.
        Assert.Equal("Secure Boot not supported; TPM not ready (2.0)", Named(rows, CompliancePath.SecureBootTpm).Detail);
        Assert.StartsWith("IT: check Secure Boot and the TPM", CompliancePath.NextStep(Named(rows, CompliancePath.SecureBootTpm)));
        c.Tpm = "ready";
        Assert.Equal("IT: this device cannot use Secure Boot, so a policy that requires it cannot be met.",
            CompliancePath.NextStep(Named(CompliancePath.Rows(Detailed(), Status(c)), CompliancePath.SecureBootTpm)));
        // A restart is never asked outright.
        var restartOnly = Named(CompliancePath.Rows(Detailed(warn: HealthChecks.PendingRestart), Status(AllOn())), CompliancePath.WindowsRestart);
        Assert.Equal("Restart when your organization allows it, then check the status in Company Portal again.", CompliancePath.NextStep(restartOnly));
        Assert.False(CompliancePath.ForIt(CompliancePath.NextStep(restartOnly)));
        var (text, tone) = CompliancePath.Verdict(rows);
        Assert.Equal(Tone.Warning, tone);
        // One line on the path at every scale: a count, the rows show which.
        Assert.Equal("To look at: 4 of 5 items.", text);
        Assert.DoesNotContain("not compliant", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("suspended", "Suspended", false)]
    [InlineData("encrypting", "Still encrypting", false)]
    [InlineData("decrypting", "Being turned off (decrypting)", true)]
    [InlineData("unknown", "Unknown: could not be read", true)]
    public void BitLocker_s_states_each_have_their_own_step(string token, string detail, bool forIt)
    {
        var c = AllOn();
        c.BitLocker = token;
        var row = Named(CompliancePath.Rows(Detailed(), Status(c)), CompliancePath.BitLocker);
        Assert.Equal(HealthStatus.Warn, row.Status);
        Assert.Equal(detail, row.Detail);
        Assert.Equal(forIt, CompliancePath.ForIt(CompliancePath.NextStep(row)));
    }

    [Fact]
    public void Retry_apps_and_policies_is_offered_only_on_an_old_sync_never_without_an_enrollment()
    {
        // An old sync: a sync now lets Intune judge the current settings.
        var old = Named(CompliancePath.Rows(Detailed(fail: HealthChecks.LastSync), Status(AllOn())), CompliancePath.Intune);
        Assert.Equal(HealthStatus.Fail, old.Status);
        Assert.Equal(HealthFix.RetryAppsAndPolicies, old.Fix);
        Assert.Equal("Try Retry work apps and settings.", CompliancePath.NextStep(old));
        // No enrollment: a sync cannot help; a step for IT.
        var none = Named(CompliancePath.Rows(Detailed(fail: HealthChecks.Enrollment), Status(AllOn())), CompliancePath.Intune);
        Assert.Equal(HealthStatus.Fail, none.Status);
        Assert.Equal(HealthFix.None, none.Fix);
        Assert.StartsWith("IT: enroll this device in Intune", CompliancePath.NextStep(none));
        // No other row of the path ever offers it.
        var c = AllOn();
        c.BitLocker = "off";
        c.FirewallDomain = "unknown";
        var rows = CompliancePath.Rows(Detailed(fail: HealthChecks.LastSync, warn: HealthChecks.PendingRestart), Status(c));
        Assert.Equal(new[] { CompliancePath.Intune }, rows.Where(r => r.Fix == HealthFix.RetryAppsAndPolicies).Select(r => r.Name));
        Assert.DoesNotContain("Retry", string.Join(" ", rows.Where(r => r.Name != CompliancePath.Intune).Select(r => r.Hint)));
    }

    [Fact]
    public void Defender_keeps_its_own_fix_for_old_definitions_and_sends_real_time_protection_to_IT()
    {
        var status = Status(AllOn());
        var old = DeviceSettingRows.Defender(status, new DefenderReadout { Mode = "normal", RealTimeProtection = true, SignatureUpdatedUtc = Now.AddDays(-9).ToString("o") }, Now);
        var row = Named(CompliancePath.Rows(Detailed(defender: old), status), CompliancePath.DefenderFirewall);
        Assert.Equal(HealthFix.UpdateDefender, row.Fix);
        Assert.Equal("Try Update Defender definitions.", CompliancePath.NextStep(row));
        var off = DeviceSettingRows.Defender(status, new DefenderReadout { Mode = "normal", RealTimeProtection = false, SignatureUpdatedUtc = Now.ToString("o") }, Now);
        var rtp = Named(CompliancePath.Rows(Detailed(defender: off), status), CompliancePath.DefenderFirewall);
        Assert.Equal(HealthFix.None, rtp.Fix);
        Assert.Equal("IT: real-time protection is off; it is a setting of your organization.", CompliancePath.NextStep(rtp));
    }

    [Fact]
    public void Without_the_service_or_its_readouts_the_rows_say_so_and_offer_no_fix()
    {
        // The service is not available: one row for the settings it reads, saying so, without a fix link.
        var away = CompliancePath.Rows(Detailed(), null);
        Assert.Equal(new[] { CompliancePath.Intune, CompliancePath.DeviceSettings, CompliancePath.Defender, CompliancePath.Restart }, away.Select(r => r.Name));
        var settings = Named(away, CompliancePath.DeviceSettings);
        Assert.True(settings.ServiceUnavailable);
        Assert.Equal(HealthFix.None, settings.Fix);
        Assert.Equal("", settings.HintBelow);
        // The readouts are still being read: one row too.
        var reading = CompliancePath.Rows(Detailed(), new StatusInfo());
        Assert.Equal(4, reading.Count);
        Assert.Equal(HealthStatus.Warn, Named(reading, CompliancePath.DeviceSettings).Status);
        Assert.StartsWith("Unknown: still being read", Named(reading, CompliancePath.DeviceSettings).Detail);
        // No checks yet: no rows, and the verdict says so.
        Assert.Empty(CompliancePath.Rows(new List<HealthResult>(), Status(AllOn())));
        Assert.Equal("These checks have not run yet. Use Check this PC again on Home.", CompliancePath.Verdict(new List<HealthResult>()).Text);
    }

    [Fact]
    public void The_check_s_log_history_and_cards_carry_items_and_states_only()
    {
        var c = AllOn();
        c.BitLocker = "off";
        var rows = CompliancePath.Rows(Detailed(fail: HealthChecks.LastSync), Status(c));
        var outcome = CompliancePath.Check(rows, "local-0a1b2c3d", Now);
        Assert.Equal(Tone.Error, outcome.Tone);
        Assert.Equal("2 items to look at: Intune enrollment and sync, BitLocker. Each has its next step.", outcome.Status);
        Assert.Equal(2, outcome.NotOk);
        Assert.Equal("to-look-at", outcome.Slug);
        // A card per item that is not OK (problems first), the items that are fine in one card,
        // then where the verdict is.
        Assert.Equal(new[] { CardKind.Error, CardKind.Warning, CardKind.Ok, CardKind.Info }, outcome.Cards.Select(k => k.Kind));
        Assert.Equal("Retry work apps and settings", outcome.Cards[0].NextStep.Replace("Try ", "").TrimEnd('.'));
        Assert.Equal("", outcome.Cards[1].NextStep);
        Assert.StartsWith("turn on BitLocker", outcome.Cards[1].Support);
        Assert.Equal(Words.Count(rows.Count - 2, "setting") + " look fine", outcome.Cards[2].Title);
        Assert.Equal(rows.Count - 2, outcome.Cards[2].Detail.Split('\n').Length);
        Assert.Equal("Company Portal shows whether this device meets your organization's requirements", outcome.Cards[3].Title);
        Assert.Contains("Company Portal", outcome.Cards[3].NextStep);
        // First each item and its state in words, then one record per item and the result; tokens,
        // never a name, an address or a path.
        Assert.Equal(2 * rows.Count + 1, outcome.LogLines.Count);
        Assert.Equal("BitLocker: " + Words.Warning + ".", outcome.LogLines[1]);
        Assert.Equal("COMPLIANCE run=local-0a1b2c3d time=2026-11-13T10:00:00.000Z context=user-app item=bitlocker status=check step=it", outcome.LogLines[rows.Count + 1]);
        Assert.Equal("COMPLIANCE run=local-0a1b2c3d result=to-look-at not-ok=2 verdict=not-visible-on-device", outcome.LogLines.Last());
        var all = string.Join("\n", outcome.LogLines);
        Assert.DoesNotContain(Environment.MachineName, all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@":\", all);
        // The history line, and how the device report reads it back.
        Assert.Equal("2|local-0a1b2c3d|to-look-at", CompliancePath.HistoryResult(outcome, "local-0a1b2c3d"));
        Assert.Equal("2026-11-13 11:00 +01:00  Check compliance settings: settings to look at (2, reference local-0a1b2c3d)",
            RunWords.HistoryLine("2026-11-13 11:00:00 +01:00;user;ComplianceCheck;2|local-0a1b2c3d|to-look-at"));

        // All fine: OK in the run's own words, still pointing to Company Portal.
        var fine = CompliancePath.Check(CompliancePath.Rows(Detailed(), Status(AllOn())), "local-1", Now);
        Assert.Equal(Tone.Success, fine.Tone);
        Assert.Equal("all-fine", fine.Slug);
        Assert.Equal(new[] { CardKind.Ok, CardKind.Info }, fine.Cards.Select(k => k.Kind));
        Assert.Equal(string.Format("The settings this device can check look fine: {0} of {0}.", fine.LogLines.Count / 2), fine.Status);
        Assert.Contains("BitLocker: ", fine.Cards[0].Detail);
        // Not read: the readouts were missing.
        Assert.Equal("not-read", CompliancePath.Check(CompliancePath.Rows(Detailed(), new StatusInfo()), "local-2", Now).Slug);
        Assert.Equal("not-read", CompliancePath.Check(new List<HealthResult>(), "local-3", Now).Slug);
    }

    private static MainWindow Window(List<HealthResult> detailed, StatusInfo status)
    {
        var window = MainWindowPagesTests.NewWindow();
        MainWindowPagesTests.Invoke(window, "ApplyHealth", status, false, detailed, new DsregInfo(), false);
        return window;
    }

    [Fact]
    public void Choose_a_problem_opens_the_path_with_its_rows_verdict_tools_and_Back_returns_to_its_button()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var c = AllOn();
            c.BitLocker = "off";
            var window = Window(Detailed(), Status(c));
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            MainWindowPagesTests.Call(window, "OnProblemBlocked", window, new RoutedEventArgs());
            Assert.Equal(CompliancePath.Title, MainWindowPagesTests.Get<TextBlock>(window, "ProblemTitle").Text);
            Assert.Equal(CompliancePath.NotKnown, MainWindowPagesTests.Get<TextBlock>(window, "ProblemIntro").Text);
            var checks = ((IEnumerable<HealthRow>)MainWindowPagesTests.Get<ItemsControl>(window, "ProblemChecks").ItemsSource).ToList();
            Assert.Equal(CompliancePath.RowNames, checks.Select(r => r.Name));
            Assert.Equal("To look at: 1 of 5 items.", MainWindowPagesTests.Get<TextBlock>(window, "ProblemVerdict").Text);
            var tools = ((IEnumerable<ToolRowView>)MainWindowPagesTests.Get<ItemsControl>(window, "ProblemTools").ItemsSource).ToList();
            Assert.Equal(new[] { "ComplianceButton", "UpdateDefenderButton" }, tools.Select(t => t.Tool.Button));
            // The heavy repair in its own group: with IT support.
            Assert.Equal(new[] { "RepairWuButton" }, ((IEnumerable<ToolRowView>)MainWindowPagesTests.Get<ItemsControl>(window, "ProblemHeavyTools").ItemsSource).Select(t => t.Tool.Button));
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<StackPanel>(window, "ProblemHeavyGroup").Visibility);
            Assert.Same(MainWindowPagesTests.Get<Button>(window, "ComplianceButton"), tools[0].RunTarget);
            Assert.True(tools[0].RunTarget.IsEnabled);
            // Copy details for IT is on the path, named after it.
            var copy = MainWindowPagesTests.Get<Button>(window, "PathCopyTicketButton");
            Assert.Equal(Visibility.Visible, copy.Visibility);
            Assert.Equal("Copy details for IT: " + CompliancePath.Title, AutomationProperties.GetName(copy));
            MainWindowPagesTests.Call(window, "OnPathBack", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<StackPanel>(window, "ProblemsPage").Visibility);
        });
    }

    [Fact]
    public void Copy_for_IT_on_the_path_names_each_item_s_step_and_what_the_device_cannot_know_and_no_names()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var c = AllOn();
            c.FirewallPrivate = "off";
            var window = Window(Detailed(), Status(c));
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.DeviceBlocked, false);
            var text = window.PathTicketText();
            var lines = text.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("Endpoint Toolbelt · " + CompliancePath.Title + " · Reference none", lines[0]);
            Assert.Contains(lines, l => l.StartsWith("Evidence · ", StringComparison.Ordinal) && l.EndsWith("WARNING: Defender and firewall: Firewall off for private networks; Defender up to date", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("Outcome · To look at: 1 of 5 items.", StringComparison.Ordinal));
            Assert.Equal("Next · IT support: turn Windows Firewall back on for these networks; it is set by your organization's policy.", lines.Last());
            Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"C:\", text);
            // Nothing off: the next step is the way to the verdict.
            window = Window(Detailed(), Status(AllOn()));
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.DeviceBlocked, false);
            var clean = window.PathTicketText().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            Assert.StartsWith("Next · User: Open Company Portal, choose Devices, then this device, then Check status (or Check access).", clean.Last());
            Assert.DoesNotContain("Retry work apps and settings", string.Join("\n", clean));
        });
    }

    [Fact]
    public void Running_the_check_shows_its_result_and_keeps_it_in_the_run_history_and_the_history()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var c = AllOn();
            c.SecureBoot = "off";
            var window = Window(Detailed(), Status(c));
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.DeviceBlocked, false);
            MainWindowPagesTests.Call(window, "OnComplianceCheck", window, new RoutedEventArgs());
            Assert.Equal("1 item to look at: Secure Boot and TPM. Each has its next step.", MainWindowPagesTests.Get<TextBlock>(window, "StatusText").Text);
            Assert.Equal("WARNING", MainWindowPagesTests.Get<TextBlock>(window, "StatusWord").Text);
            Assert.Equal(Visibility.Visible, MainWindowPagesTests.Get<Button>(window, "CopyTicketButton").Visibility);
            // The history line: the item count, the run ID and the result, nothing else.
            var history = File.ReadAllLines(window.HistoryOverride).Last().Split(';');
            Assert.Equal("ComplianceCheck", history[2]);
            Assert.Matches("^1\\|local-[0-9a-f]{8}\\|to-look-at$", history[3]);
            // The run log goes into the run history under the check's name.
            var folder = window.RunsFolderOverride;
            string log = null;
            for (var i = 0; i < 50 && log == null; i++)
            {
                log = Directory.Exists(folder) ? Directory.GetFiles(folder, "*_Checkcompliancesettings_local-*.log").FirstOrDefault() : null;
                if (log == null) { Thread.Sleep(100); }
            }
            Assert.NotNull(log);
            // The file can still be open while the run history writes it (a slow host, coverage): read it again for a moment.
            string body = null;
            for (var i = 0; i < 50 && body == null; i++)
            {
                try { body = File.ReadAllText(log); }
                catch (IOException) { Thread.Sleep(100); }
            }
            Assert.NotNull(body);
            Assert.Contains("COMPLIANCE run=local-", body);
            Assert.Contains("item=secure-boot-and-tpm status=check step=it", body);
            Assert.DoesNotContain(Environment.MachineName + " ", body.Substring(body.IndexOf("COMPLIANCE", StringComparison.Ordinal)), StringComparison.OrdinalIgnoreCase);
        });
    }

    // ---- The fit at 100, 125 and 150 % display scale --------------------------------------------------

    /// <summary>The client height a page must spare above 100 % scale (the title bar and the borders round differently there).</summary>
    private const double ScaleMargin = 12;

    /// <summary>
    /// The page area's content height and viewport at a display scale, set on the root of the visual tree as
    /// the fit tests do, so layout rounding to device pixels applies.
    /// </summary>
    private static (double Extent, double Viewport) FitAt(MainWindow window, double scale)
    {
        var content = (FrameworkElement)window.Content;
        System.Windows.Media.Visual root = content;
        while (System.Windows.Media.VisualTreeHelper.GetParent(root) is System.Windows.Media.Visual parent) { root = parent; }
        System.Windows.Media.VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        var height = WindowLayout.CompactClientHeight - (scale > 1 ? ScaleMargin : 0);
        content.InvalidateMeasure();
        content.Measure(new Size(WindowLayout.DefaultClientWidth, height));
        content.Arrange(new Rect(0, 0, WindowLayout.DefaultClientWidth, height));
        content.UpdateLayout();
        var scroll = MainWindowPagesTests.Get<ScrollViewer>(window, "MainScroll");
        return (scroll.ExtentHeight, scroll.ViewportHeight);
    }

    private static void AssertFitsAtEveryScale(MainWindow window, string what)
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5 })
        {
            var (extent, viewport) = FitAt(window, scale);
            Assert.True(extent <= viewport + 0.5, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0} at {1:0} %: the page needs {2:0.#} px but has {3:0.#} px at 540 x 640: it would scroll.", what, scale * 100, extent, viewport));
        }
        FitAt(window, 1.0);
    }

    private static StatusInfo WithSupport(StatusInfo status, string text)
    {
        status.SupportText = text;
        status.SupportUrl = "https://support.contoso.example/";
        return status;
    }

    [Fact]
    public void Choose_a_problem_and_the_path_fit_540_x_640_at_100_125_and_150_percent_with_real_rows_and_a_support_contact()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            // A Sandbox run at 150 %: not enrolled, BitLocker not read, Defender off, Secure Boot off
            // and no TPM, with the organization's support contact in the footer.
            var c = AllOn();
            c.BitLocker = null;
            c.SecureBoot = "off";
            c.Tpm = "missing";
            c.TpmVersion = null;
            c.OsBuild = "10.0.26100.9974";
            var defenderOff = HealthChecks.Make(HealthChecks.Defender, HealthStatus.Warn, "Off: another antivirus may be in charge");
            var window = Window(Detailed(fail: HealthChecks.Enrollment, defender: defenderOff), WithSupport(Status(c), "Contoso IT Service Desk - ext. 5555"));
            MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Problems"), false);
            AssertFitsAtEveryScale(window, "Choose a problem with five paths");
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.DeviceBlocked, false);
            Assert.Equal(5, ((IEnumerable<HealthRow>)MainWindowPagesTests.Get<ItemsControl>(window, "ProblemChecks").ItemsSource).Count());
            Assert.Equal("To look at: 4 of 5 items.", MainWindowPagesTests.Get<TextBlock>(window, "ProblemVerdict").Text);
            AssertFitsAtEveryScale(window, CompliancePath.Title + " as in the Sandbox");

            // The worst case: every row not OK, two with a fix link, the longest details, and a support
            // contact that wraps to two lines; the intro wraps to two lines too.
            var worst = AllOn();
            worst.BitLocker = "decrypting";
            worst.FirewallDomain = "off";
            worst.FirewallPrivate = "off";
            worst.FirewallPublic = "off";
            worst.SecureBoot = "unsupported";
            worst.Tpm = "not-ready";
            worst.OsBuild = null;
            var oldDefinitions = HealthChecks.Make(HealthChecks.Defender, HealthStatus.Warn, "Security intelligence 9 days old");
            oldDefinitions.FixOverride = HealthFix.UpdateDefender;
            var detailed = Detailed(fail: HealthChecks.LastSync, defender: oldDefinitions);
            detailed[detailed.FindIndex(r => r.Name == HealthChecks.PendingRestart)] = HealthChecks.Make(HealthChecks.PendingRestart, HealthStatus.Warn, "Windows servicing waits for a restart");
            window = Window(detailed, WithSupport(Status(worst), "Contoso IT Service Desk, Monday to Friday 08:00 to 18:00, ext. 5555, or open a request in the Contoso service portal any time"));
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.DeviceBlocked, false);
            var rows = ((IEnumerable<HealthRow>)MainWindowPagesTests.Get<ItemsControl>(window, "ProblemChecks").ItemsSource).ToList();
            Assert.All(rows, r => Assert.NotEqual("OK", r.StatusWord));
            Assert.Equal("To look at: 5 of 5 items.", MainWindowPagesTests.Get<TextBlock>(window, "ProblemVerdict").Text);
            AssertFitsAtEveryScale(window, CompliancePath.Title + ", every row not OK");
            Assert.True(MainWindowPagesTests.Get<TextBlock>(window, "ProblemIntro").ActualHeight > 20, "the intro wraps to two lines");
            // A result below the page may make it scroll, as on the other paths; the page itself fits.
        });
    }
}
