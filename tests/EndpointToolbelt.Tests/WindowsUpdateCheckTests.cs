using System;
using System.Collections.Generic;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.Actions;
using EndpointToolbelt.Engine.WindowsUpdate;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.Rules;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// Check Windows Update, read-only. The engine reads the update history, the failure events,
/// the update policies, the services' start types, the restart flags and the free space through
/// fakes; each finding gets one next step, and Repair Windows Update is that step only where it can
/// help. The app turns the <c>wu-check</c> record into the status, the run log line, the history
/// and the Copy details for IT line, none of which names a server, a path or a person.
/// </summary>
public class WindowsUpdateCheckTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
    private const string Current = "2026-11 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5099001)";
    private const string Old = "2026-09 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5097001)";
    private const string Operational = "Microsoft-Windows-WindowsUpdateClient/Operational";

    private readonly TestFolder _folder = new();
    private readonly EngineHarness _h = new();
    private readonly List<WuHistoryEntry> _history = new();
    private readonly List<WuRawEvent> _events = new();

    public WindowsUpdateCheckTests()
    {
        _h.Clock.UtcNow = Now;
        _h.WindowsUpdate.HistoryOf = _ => _history;
        _h.WindowsUpdate.EventsOf = log => log == Operational ? _events : Array.Empty<WuRawEvent>();
        foreach (var service in WuCodes.RequiredServices) { _h.ServiceControl.StartTypes[service.Key] = "Manual"; }
        _h.FreeBytes.Enqueue(80L * 1073741824);
    }

    public void Dispose() => _folder.Dispose();

    private void Installed(string title, DateTime utc) => _history.Add(new WuHistoryEntry { Title = title, ResultCode = 2, DateUtc = utc, Operation = 1 });

    private void FailedInstall(string title, DateTime utc, uint code) => _history.Add(new WuHistoryEntry { Title = title, ResultCode = 4, DateUtc = utc, Operation = 1, HResult = code });

    private void Failure(DateTime utc, uint code, string title = Current) =>
        _events.Add(new WuRawEvent { TimeCreatedUtc = utc, Properties = new object[] { title, unchecked((int)code) } });

    private void Gp(string name, object value) => _h.Registry.Put(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", name, value);

    private void Au(string name, object value) => _h.Registry.Put(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", name, value);

    private void Mdm(string name, object value) => _h.Registry.Put(@"SOFTWARE\Microsoft\PolicyManager\current\device\Update", name, value);

    private (int Exit, JsonNode Record) Check()
    {
        var run = _h.NewRun("wucheck1", nameof(ToolbeltAction.CheckWindowsUpdate));
        var exit = new CheckWindowsUpdateHandler().Run(run) ?? -1;
        return (exit, _h.Records("wu-check").Single());
    }

    private JsonNode Finding(string id) => _h.Findings(id).Single();

    // --- Engine: one case per finding ------------------------------------------------------------

    [Fact]
    public void A_current_device_is_up_to_date_with_nothing_to_do_and_exit_0()
    {
        Installed(Current, new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc));
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Success, exit);
        Assert.Equal("up-to-date", record.Str("verdict"));
        Assert.Equal("2026-11", record.Str("lastmonth"));
        Assert.Equal("13", record.Str("days"));
        Assert.Equal("false", record.Str("repair"));
        Assert.Equal("none", record.Str("policy"));
        var finding = Finding("WUCHECK-CURRENT");
        Assert.Equal("OK", finding.Str("severity"));
        Assert.Contains("13 days ago", finding.Str("detail"));
        Assert.Equal("Nothing to do.", finding.Str("next"));
        Assert.DoesNotContain(_h.Records("finding"), f => f.Str("severity") is "WARNING" or "ERROR");
    }

    [Fact]
    public void A_current_device_names_its_installed_patch_month_not_the_expected_one()
    {
        // The day after the November update: with the deployment delay, October is still the expected month.
        _h.Clock.UtcNow = new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);
        Installed(Current, new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc));
        var (exit, _) = Check();
        Assert.Equal(EngineExitCode.Success, exit);
        Assert.Contains(_h.Records("progress"), r => r.Str("text").Contains("expected by now: 2026-10."));
        Assert.Equal("Windows is up to date (patch month 2026-11)", Finding("WUCHECK-CURRENT").Str("title"));
    }

    [Fact]
    public void Behind_without_a_failure_suggests_the_repair()
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Warnings, exit);
        Assert.Equal("behind", record.Str("verdict"));
        Assert.Equal("2", record.Str("behind"));
        Assert.Equal("true", record.Str("repair"));
        var finding = Finding("WUCHECK-BEHIND");
        Assert.Equal("Windows is 2 months behind on quality updates", finding.Str("title"));
        Assert.Equal(WindowsUpdateCheck.RepairStep, finding.Str("next"));
    }

    [Fact]
    public void A_logged_failure_is_named_in_plain_words_from_the_code_table_with_the_repair_as_step()
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        Failure(new DateTime(2026, 11, 14, 3, 0, 0, DateTimeKind.Utc), 0x800F0922);
        var (_, record) = Check();
        Assert.Equal("failing", record.Str("verdict"));
        Assert.Equal("0x800f0922", record.Str("codes"));
        var finding = Finding("WUCHECK-CODE-0x800f0922");
        Assert.Equal("Update failure 0x800F0922: CBS_E_INSTALLERS_FAILED", finding.Str("title"));
        Assert.Contains("Meaning: an installer inside the update failed", finding.Str("detail"));
        Assert.Equal(WindowsUpdateCheck.RepairStep, finding.Str("next"));
        Assert.Contains("EFI system partition", finding.Str("support"));
    }

    [Fact]
    public void A_failed_install_in_the_history_counts_and_its_code_is_named_even_without_an_event()
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        FailedInstall(Current, new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc), 0x80073712);
        // A definition update that failed is not a quality update and is not counted.
        FailedInstall("Security Intelligence Update for Microsoft Defender Antivirus (KB2267602)", new DateTime(2026, 11, 13, 4, 0, 0, DateTimeKind.Utc), 0x80070643);
        var (_, record) = Check();
        Assert.Equal("1", record.Str("failed"));
        Assert.Equal("0x80073712", record.Str("codes"));
        Assert.Contains("1 failed install since the last successful quality update", Finding("WUCHECK-CODE-0x80073712").Str("detail"));
    }

    [Fact]
    public void An_unknown_code_on_a_Windows_update_is_said_honestly()
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        Failure(new DateTime(2026, 11, 14, 3, 0, 0, DateTimeKind.Utc), 0x80071234);
        Check();
        var finding = Finding("WUCHECK-CODE-0x80071234");
        Assert.Equal("Update failure 0x80071234: not a recognised code", finding.Str("title"));
        Assert.Contains("No documented meaning", finding.Str("detail"));
    }

    [Fact]
    public void More_than_four_codes_are_counted_not_listed()
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        foreach (var code in new uint[] { 0x800F0922, 0x80073712, 0x800F081F, 0x80244022, 0x80072EE2 }) { Failure(new DateTime(2026, 11, 14, 3, 0, 0, DateTimeKind.Utc), code); }
        var (_, record) = Check();
        Assert.Equal(4, record.Str("codes").Split(' ').Length);
        Assert.Equal("1 more failure code in the log", Finding("WUCHECK-CODES-MORE").Str("title"));
    }

    [Fact]
    public void A_failure_after_the_newest_successful_update_on_a_current_device_is_only_information()
    {
        Installed(Current, new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc));
        Failure(new DateTime(2026, 11, 20, 3, 0, 0, DateTimeKind.Utc), 0x80244022);
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Success, exit);
        Assert.Equal("up-to-date", record.Str("verdict"));
        var finding = Finding("WUCHECK-CODE-0x80244022");
        Assert.Equal("INFO", finding.Str("severity"));
        Assert.StartsWith("Nothing to do now", finding.Str("next"));
    }

    [Fact]
    public void A_policy_code_blocks_and_sends_the_user_to_IT_never_to_the_repair()
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        Failure(new DateTime(2026, 11, 14, 3, 0, 0, DateTimeKind.Utc), 0x8024002F);
        var (_, record) = Check();
        Assert.Equal("blocked-by-policy", record.Str("verdict"));
        Assert.Equal("false", record.Str("repair"));
        Assert.Equal(WindowsUpdateCheck.ItStep, Finding("WUCHECK-POLICY-CODE").Str("next"));
        Assert.Equal(WindowsUpdateCheck.ItStep, Finding("WUCHECK-CODE-0x8024002f").Str("next"));
        Assert.StartsWith(WindowsUpdateCheck.ItStep, Finding("WUCHECK-BEHIND").Str("next"));
        Assert.DoesNotContain(_h.Records("finding"), f => (f.Str("next") ?? "").Contains("Repair Windows Update"));
    }

    [Fact]
    public void Access_switched_off_by_policy_is_an_error_for_IT()
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        Gp("DisableWindowsUpdateAccess", 1);
        var (_, record) = Check();
        Assert.Equal("blocked-by-policy", record.Str("verdict"));
        Assert.Equal("access-off", record.Str("policy"));
        var finding = Finding("WUCHECK-ACCESS-OFF");
        Assert.Equal("ERROR", finding.Str("severity"));
        Assert.Equal(WindowsUpdateCheck.ItStep, finding.Str("next"));
    }

    [Fact]
    public void A_pause_by_policy_blocks_and_goes_to_IT()
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        Mdm("PauseQualityUpdates", 1);
        var (_, record) = Check();
        Assert.Equal("blocked-by-policy", record.Str("verdict"));
        Assert.Equal("paused-policy", record.Str("policy"));
        Assert.Equal(WindowsUpdateCheck.ItStep, Finding("WUCHECK-PAUSED-POLICY").Str("next"));
        Assert.Equal("false", record.Str("repair"));
    }

    [Fact]
    public void A_pause_that_neither_a_policy_nor_Settings_explains_counts_as_a_policy_pause()
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        _h.Registry.Put(@"SOFTWARE\Microsoft\WindowsUpdate\UpdatePolicy\Settings", "PausedQualityStatus", 1);
        var (_, record) = Check();
        Assert.Equal("paused-policy", record.Str("policy"));
    }

    [Fact]
    public void A_pause_in_Settings_tells_the_user_how_to_resume_and_does_not_block_the_repair()
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        _h.Registry.Put(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "PauseUpdatesExpiryTime", "2026-12-02T10:00:00Z");
        var (_, record) = Check();
        Assert.Equal("paused-user", record.Str("policy"));
        Assert.Equal("behind", record.Str("verdict"));
        var finding = Finding("WUCHECK-PAUSED-USER");
        Assert.Equal("Open Settings > Windows Update and select Resume updates.", finding.Str("next"));
        Assert.Contains("until", finding.Str("title"));
    }

    [Fact]
    public void A_pause_in_Settings_that_has_expired_is_not_reported()
    {
        Installed(Current, new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc));
        _h.Registry.Put(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "PauseUpdatesExpiryTime", "2026-10-02T10:00:00Z");
        var (_, record) = Check();
        Assert.Equal("none", record.Str("policy"));
        Assert.Empty(_h.Findings("WUCHECK-PAUSED-USER"));
    }

    [Fact]
    public void A_current_device_with_a_pause_in_Settings_still_has_a_point_to_check()
    {
        Installed(Current, new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc));
        _h.Registry.Put(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "PauseUpdatesExpiryTime", "2026-12-02T10:00:00Z");
        var (exit, record) = Check();
        Assert.Equal("up-to-date", record.Str("verdict"));
        Assert.Equal(EngineExitCode.Warnings, exit);
    }

    [Fact]
    public void No_internet_locations_without_an_update_server_leaves_no_source()
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        Mdm("DoNotConnectToWindowsUpdateInternetLocations", 1);
        var (_, record) = Check();
        Assert.Equal("blocked-by-policy", record.Str("verdict"));
        Assert.Equal("ERROR", Finding("WUCHECK-NO-SOURCE").Str("severity"));
    }

    [Fact]
    public void An_update_server_is_information_and_its_address_is_never_written()
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        Au("UseWUServer", 1);
        Gp("WUServer", "https://wsus.contoso.example:8531");
        Gp("DoNotConnectToWindowsUpdateInternetLocations", 1);
        var (_, record) = Check();
        Assert.Equal("wsus,no-internet", record.Str("policy"));
        Assert.Equal("behind", record.Str("verdict"));
        Assert.Equal("true", record.Str("repair"));
        Assert.Equal("INFO", Finding("WUCHECK-WSUS").Str("severity"));
        Assert.Empty(_h.Findings("WUCHECK-NO-SOURCE"));
        Assert.DoesNotContain(_h.Lines, l => l.IndexOf("contoso", StringComparison.OrdinalIgnoreCase) >= 0 || l.Contains("8531"));
    }

    [Fact]
    public void An_update_server_with_quality_updates_sent_back_to_Windows_Update_is_not_WSUS()
    {
        Installed(Current, new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc));
        Au("UseWUServer", 1);
        Gp("SetPolicyDrivenUpdateSourceForQualityUpdates", 0);
        var (_, record) = Check();
        Assert.Equal("none", record.Str("policy"));
        Assert.Empty(_h.Findings("WUCHECK-WSUS"));
    }

    [Fact]
    public void A_deferral_is_named_with_its_days()
    {
        Installed(Current, new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc));
        Mdm("DeferQualityUpdatesPeriodInDays", 7);
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Success, exit);
        Assert.Equal("deferred-7", record.Str("policy"));
        Assert.Equal("A policy defers quality updates by 7 days", Finding("WUCHECK-DEFERRED").Str("title"));
        Assert.Equal("Nothing to do yourself.", Finding("WUCHECK-DEFERRED").Str("next"));
        // Who sets it: the device management (the Update CSP an Intune update ring writes).
        Assert.Contains("set by your organization's device management (Intune or similar)", Finding("WUCHECK-DEFERRED").Str("detail"));
        Assert.Contains("from the Update CSP", Finding("WUCHECK-DEFERRED").Str("support"));
        Assert.Contains(_h.Lines, l => l.Contains("Update policy: quality updates deferred by 7 days (device management)."));
    }

    [Fact]
    public void A_released_update_held_back_by_the_deferral_says_from_when_it_comes_and_who_can_send_it_sooner()
    {
        // The 2026-11 update was released on 2026-11-10; a 5 day deferral offers it from 2026-11-15.
        _h.Clock.UtcNow = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        Installed("2026-10 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5098001)", new DateTime(2026, 10, 15, 3, 0, 0, DateTimeKind.Utc));
        Mdm("DeferQualityUpdatesPeriodInDays", 5);
        Check();
        var held = Finding("WUCHECK-DEFERRED");
        Assert.Equal("INFO", held.Str("severity"));
        Assert.Equal("The 2026-11 update is offered to this device from 2026-11-15", held.Str("title"));
        Assert.Equal("It was released on 2026-11-10. Your organization's device management (Intune or similar) defers each quality update by 5 days after its release, so this device gets it from 2026-11-15. That is a choice of your organization, not a fault, and this device cannot lift it.", held.Str("detail"));
        Assert.Equal("If you need it sooner, ask IT support: they can send this one update sooner.", held.Str("next"));
        Assert.Contains("Expedite", held.Str("support"));
    }

    [Fact]
    public void A_held_back_update_offered_today_says_today()
    {
        _h.Clock.UtcNow = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);
        Installed("2026-10 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5098001)", new DateTime(2026, 10, 15, 3, 0, 0, DateTimeKind.Utc));
        Mdm("DeferQualityUpdatesPeriodInDays", 5);
        Check();
        Assert.Equal("The 2026-11 update is offered to this device from today", Finding("WUCHECK-DEFERRED").Str("title"));
    }

    [Fact]
    public void A_group_policy_deferral_is_named_as_Group_Policy()
    {
        _h.Clock.UtcNow = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        Installed("2026-10 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5098001)", new DateTime(2026, 10, 15, 3, 0, 0, DateTimeKind.Utc));
        Gp("DeferQualityUpdates", 1);
        Gp("DeferQualityUpdatesPeriodInDays", 14);
        Check();
        var held = Finding("WUCHECK-DEFERRED");
        Assert.Equal("The 2026-11 update is offered to this device from 2026-11-24", held.Str("title"));
        Assert.Contains("A Group Policy of your organization defers each quality update by 14 days", held.Str("detail"));
        Assert.Equal("If you need it sooner, ask IT support.", held.Str("next"));
        Assert.Contains("from Group Policy", held.Str("support"));
        Assert.Contains(_h.Lines, l => l.Contains("quality updates deferred by 14 days (Group Policy)"));
    }

    [Fact]
    public void A_group_policy_deferral_counts_only_when_it_is_turned_on()
    {
        Installed(Current, new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc));
        Gp("DeferQualityUpdatesPeriodInDays", 14);
        Assert.Equal("none", Check().Record.Str("policy"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Automatic_updates_switched_off_by_policy_go_to_IT(bool groupPolicy)
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        if (groupPolicy) { Au("NoAutoUpdate", 1); } else { Mdm("AllowAutoUpdate", 5); }
        var (_, record) = Check();
        Assert.Equal("auto-off", record.Str("policy"));
        Assert.Equal(WindowsUpdateCheck.ItStep, Finding("WUCHECK-AUTO-OFF").Str("next"));
    }

    [Fact]
    public void A_disabled_or_missing_update_service_is_an_error_for_IT_and_no_repair_is_suggested()
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        _h.ServiceControl.StartTypes["bits"] = "Disabled";
        _h.ServiceControl.StartTypes.Remove("UsoSvc");
        var (_, record) = Check();
        Assert.Equal("services-disabled", record.Str("verdict"));
        Assert.Equal("2", record.Str("disabled"));
        Assert.Equal("false", record.Str("repair"));
        Assert.Equal("Background Intelligent Transfer Service is disabled", Finding("WUCHECK-SERVICE-bits").Str("title"));
        Assert.Equal("Update Orchestrator Service is missing", Finding("WUCHECK-SERVICE-UsoSvc").Str("title"));
        Assert.Equal(WindowsUpdateCheck.ItStep, Finding("WUCHECK-SERVICE-bits").Str("next"));
        Assert.StartsWith(WindowsUpdateCheck.ItStep, Finding("WUCHECK-BEHIND").Str("next"));
        Assert.Equal(0, _h.ServiceControl.Calls);
        Assert.Empty(_h.ServiceControl.Started);
    }

    [Fact]
    public void A_pending_restart_comes_first_and_the_repair_waits_for_it()
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        _h.Registry.AddKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        var (_, record) = Check();
        Assert.Equal("restart-pending", record.Str("verdict"));
        Assert.Equal("true", record.Str("restart"));
        Assert.Equal("false", record.Str("repair"));
        Assert.Equal("Restart the device when you can, then check again.", Finding("WUCHECK-RESTART").Str("next"));
        Assert.Equal("Restart the device first, then check again.", Finding("WUCHECK-BEHIND").Str("next"));
    }

    [Fact]
    public void Too_little_free_space_points_to_Free_up_disk_space()
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        _h.FreeBytes.Clear();
        _h.FreeBytes.Enqueue(6L * 1073741824);
        var (_, record) = Check();
        Assert.Equal("low-space", record.Str("verdict"));
        Assert.Equal("true", record.Str("lowspace"));
        Assert.Equal("6", record.Str("freegb"));
        var finding = Finding("WUCHECK-LOW-SPACE");
        Assert.Equal("Only 6 GB free on the system drive", finding.Str("title"));
        Assert.StartsWith("Run Free up disk space", finding.Str("next"));
        Assert.Equal("Free up disk space first, then check again.", Finding("WUCHECK-BEHIND").Str("next"));
    }

    [Fact]
    public void Unknown_free_space_is_said_and_not_counted_as_low()
    {
        Installed(Current, new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc));
        _h.FreeBytes.Clear();
        var (_, record) = Check();
        Assert.Equal("false", record.Str("lowspace"));
        Assert.Equal("INFO", Finding("WUCHECK-SPACE-UNKNOWN").Str("severity"));
    }

    [Fact]
    public void Without_any_quality_update_in_the_history_the_patch_level_is_unknown()
    {
        var (_, record) = Check();
        Assert.Equal("no-history", record.Str("verdict"));
        Assert.Equal("", record.Str("lastmonth"));
        Assert.Equal("true", record.Str("repair"));
        Assert.Equal(WindowsUpdateCheck.RepairStep, Finding("WUCHECK-NO-HISTORY").Str("next"));
    }

    [Fact]
    public void An_unreadable_history_is_logged_and_the_check_still_ends()
    {
        _h.WindowsUpdate.HistoryOf = _ => throw new InvalidOperationException("history unavailable");
        var (_, record) = Check();
        Assert.Equal("no-history", record.Str("verdict"));
        Assert.Contains(_h.Records("progress"), p => p.Str("text").Contains("could not be read"));
    }

    [Fact]
    public void Runs_through_the_run_frame_and_changes_nothing()
    {
        Installed(Old, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc));
        Failure(new DateTime(2026, 11, 14, 3, 0, 0, DateTimeKind.Utc), 0x800F0922);
        _h.Registry.FailWrites = true;
        _h.Services.DeleteFileBelow = (_, _) => throw new InvalidOperationException("must not delete");
        _h.Services.TurnOnRedirectionGuard = () => throw new InvalidOperationException("not needed");
        var file = EngineHarness.WriteRequest(_folder, "wucheck.json",
            "{\"contractVersion\":1,\"runId\":\"wucheck2\",\"action\":\"CheckWindowsUpdate\",\"otherSessions\":0,\"callerSessionId\":1,\"callerSid\":\"S-1-5-21-1-2-3-1001\",\"config\":{}}");

        var exit = RunFrame.Run(nameof(ToolbeltAction.CheckWindowsUpdate), file, ActionHandlers.Shipped, _h.Services);

        Assert.Equal(EngineExitCode.Warnings, exit);
        Assert.Empty(_h.Processes.Requests);
        Assert.Equal(0, _h.ServiceControl.Calls);
        Assert.Empty(_h.ServiceControl.Started);
        Assert.Empty(_h.Started);
        Assert.Empty(_h.Clock.Sleeps);
        Assert.Empty(_h.Records("change"));
        Assert.Empty(_h.Records("restart-required"));
        Assert.Equal("failing", _h.Records("wu-check").Single().Str("verdict"));
        var summary = Assert.Single(_h.Records("summary"));
        Assert.Equal("0", summary.Str("changes"));
        Assert.IsType<CheckWindowsUpdateHandler>(ActionHandlers.Shipped[nameof(ToolbeltAction.CheckWindowsUpdate)]);
    }

    // --- Contract and service ----------------------------------------------------------------------

    [Fact]
    public void The_action_is_value_12_read_only_and_shares_the_read_only_limit()
    {
        Assert.Equal(12, (int)ToolbeltAction.CheckWindowsUpdate);
        var info = ActionCatalog.Get(ToolbeltAction.CheckWindowsUpdate);
        Assert.True(info.ReadOnly);
        Assert.False(info.RequiresConfirmation || info.AffectsAllSessions || info.CountsTowardRateLimit || info.ProducesFile);

        var caller = new CallerContext("S-1-5-21-1-2-3-1001", 1, "CONTOSO\\user");
        var readOnly = new RateLimiter(TimeSpan.FromHours(1));
        PolicyDecision Ask(ToolbeltAction action)
            => RequestPolicy.EvaluateRunAction(new Request { Type = RequestType.RunAction, Action = action }, caller, 0, new ServiceConfig(), null, new RateLimiter(TimeSpan.FromHours(1)), readOnlyLimiter: readOnly);
        for (var i = 0; i < RequestPolicy.ReadOnlyRateLimitPerHour / 2; i++)
        {
            Assert.True(Ask(ToolbeltAction.CheckWindowsUpdate).IsAccepted);
            Assert.True(Ask(ToolbeltAction.DiagnoseApps).IsAccepted);
        }
        var refused = Ask(ToolbeltAction.CheckWindowsUpdate);
        Assert.Equal(ResultCode.RateLimited, refused.Result);
        Assert.StartsWith("Check Windows Update and the app diagnosis were run too often", refused.Message);
        Assert.False(Ask(ToolbeltAction.CheckWindowsUpdate).IsAccepted);
    }

    // --- App -------------------------------------------------------------------------------------

    private static RunOutcome Outcome(string verdict, int exit = 2, bool repair = true, string codes = "0x800f0922", string policy = "none", bool restart = false, params string[] findings)
    {
        var outcome = new RunOutcome { Action = ToolbeltAction.CheckWindowsUpdate, RunId = "ab12cd34", Result = ResultCode.Completed, ExitCode = exit };
        foreach (var finding in findings) { outcome.Records.Add(EngineRecord.Parse(finding)); }
        outcome.Records.Add(EngineRecord.Parse("{\"type\":\"wu-check\",\"run\":\"ab12cd34\",\"ts\":\"2026-11-25T10:00:00.000Z\",\"verdict\":\"" + verdict
            + "\",\"lastmonth\":\"2026-09\",\"days\":76,\"behind\":2,\"codes\":\"" + codes + "\",\"failed\":1,\"policy\":\"" + policy
            + "\",\"disabled\":0,\"restart\":" + (restart ? "true" : "false") + ",\"freegb\":80,\"lowspace\":false,\"repair\":" + (repair ? "true" : "false") + "}"));
        outcome.Records.Add(EngineRecord.Parse("{\"type\":\"summary\",\"run\":\"ab12cd34\",\"exit\":" + exit + ",\"changes\":0,\"restart\":false}"));
        return outcome;
    }

    [Theory]
    [InlineData("up-to-date", 0, false, "Windows is up to date. Nothing to do.", Tone.Success)]
    [InlineData("behind", 2, true, "Windows is behind on updates and no failure was logged. Next: Repair Windows Update.", Tone.Warning)]
    [InlineData("failing", 2, true, "Updates failed on this device. Next: Repair Windows Update.", Tone.Warning)]
    [InlineData("failing", 2, false, "Updates failed on this device. Next: copy details for IT.", Tone.Warning)]
    [InlineData("no-history", 2, true, "No successful quality update was found. Next: Repair Windows Update.", Tone.Warning)]
    [InlineData("blocked-by-policy", 2, false, "A policy on this device decides about its updates; a repair here cannot change that. Next: copy details for IT.", Tone.Warning)]
    [InlineData("services-disabled", 2, false, "A service Windows Update needs is disabled. Contact your IT support.", Tone.Error)]
    [InlineData("restart-pending", 2, false, "Windows is waiting for a restart. Restart, then check again.", Tone.Warning)]
    [InlineData("low-space", 2, false, "Too little free disk space for updates. Try Free up disk space.", Tone.Warning)]
    public void The_status_says_the_verdict_and_the_one_next_step(string verdict, int exit, bool repair, string status, Tone tone)
    {
        var view = ResultPresenter.Final(Outcome(verdict, exit, repair));
        Assert.Equal(status, view.Status);
        Assert.Equal(tone, view.Tone);
    }

    [Fact]
    public void A_current_device_with_a_warning_says_the_point_to_check()
    {
        var finding = "{\"type\":\"finding\",\"run\":\"ab12cd34\",\"severity\":\"WARNING\",\"id\":\"WUCHECK-PAUSED-USER\",\"title\":\"Updates are paused in Settings\",\"detail\":\"\",\"next\":\"Open Settings > Windows Update and select Resume updates.\",\"support\":\"\"}";
        var view = ResultPresenter.Final(Outcome("up-to-date", 2, false, "", "paused-user", false, finding));
        Assert.Equal("Windows is up to date, but one point needs checking: Updates are paused in Settings.", view.Status);
        Assert.Equal(Tone.Warning, view.Tone);
        Assert.Equal("Open Settings > Windows Update and select Resume updates.", view.Cards.First().NextStep);
        Assert.Equal("Updates are paused in Settings", view.Cards.First().Title);
    }

    [Fact]
    public void The_run_log_line_carries_the_facts_as_tokens()
    {
        var view = ResultPresenter.Final(Outcome("failing", policy: "wsus,deferred-7"));
        Assert.Contains(view.LogLines, l => l.EndsWith("WUCHECK run=ab12cd34 context=service verdict=failing last-quality=2026-09 days-ago=76 months-behind=2 codes=0x800f0922 failed-installs=1 policy=wsus,deferred-7 services-disabled=0 restart-pending=no free-gb=80 repair=suggested", StringComparison.Ordinal));
    }

    [Fact]
    public void The_history_line_carries_the_run_and_the_verdict_and_reads_back_in_words()
    {
        var outcome = Outcome("failing");
        Assert.Equal("2|ab12cd34|failing", ResultPresenter.HistoryResult(outcome));
        Assert.Equal("2026-11-25 11:00 +01:00  Check Windows Update: updates failed (2, reference ab12cd34)",
            RunWords.HistoryLine("2026-11-25 11:00:00 +01:00;CONTOSO\\user;CheckWindowsUpdate;2|ab12cd34|failing"));
        Assert.Equal("Check Windows Update", ActionText.Name(ToolbeltAction.CheckWindowsUpdate));
    }

    [Fact]
    public void The_Copy_for_IT_line_names_the_facts_and_no_server_path_or_name()
    {
        var line = WuCheck.TicketLine(Outcome("failing", policy: "wsus,deferred-7"));
        Assert.Equal("Windows Update · last quality update 2026-09 (76 days ago) · failures 0x800f0922 (1 failed install) · policy wsus, deferred-7 · services OK · restart pending no · free 80 GB · Repair Windows Update suggested", line);
        Assert.Null(WuCheck.TicketLine(new RunOutcome { Action = ToolbeltAction.CheckWindowsUpdate }));
    }

    [Fact]
    public void A_record_with_anything_but_known_tokens_is_cleaned()
    {
        var record = EngineRecord.Parse("{\"type\":\"wu-check\",\"run\":\"r\",\"verdict\":\"failing\",\"lastmonth\":\"\\\\\\\\server\",\"codes\":\"0x800f0922 C:\\\\Windows 0xZZ\",\"policy\":\"wsus https://contoso.example\",\"days\":-4}").WuCheck;
        Assert.Equal("failing", record.Verdict);
        Assert.Equal("", record.LastMonth);
        Assert.Equal("0x800f0922", record.Codes);
        Assert.Equal("", record.Policy);
        Assert.Null(record.Days);
        Assert.Null(EngineRecord.Parse("{\"type\":\"wu-check\",\"run\":\"r\",\"verdict\":\"Up To Date\"}").WuCheck);
    }

    [Fact]
    public void An_older_service_that_does_not_know_the_check_is_said_in_words()
    {
        var view = ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.CheckWindowsUpdate, Result = ResultCode.InvalidRequest, Message = "Unknown action." });
        Assert.Equal("Check Windows Update is not available on this device yet: its Endpoint Toolbelt service is an older version.", view.Status);
        Assert.Equal(Tone.Warning, view.Tone);
    }

    [Fact]
    public void The_check_is_a_tool_in_For_IT_and_the_first_row_of_Windows_wont_update()
    {
        var tool = ToolCatalog.ByButton("CheckWuButton");
        Assert.Equal(ToolGroup.Windows, tool.Group);
        Assert.Equal(ToolbeltAction.CheckWindowsUpdate, tool.Action);
        Assert.False(tool.Heavy);
        Assert.Equal("", tool.Changes);
        Assert.Same(tool, ToolCatalog.ByAction(ToolbeltAction.CheckWindowsUpdate));
        Assert.Equal("CheckWuButton", ProblemPaths.Get(Problem.WindowsWontUpdate).Tools[0]);
        Assert.Contains("RepairWuButton", ProblemPaths.Get(Problem.WindowsWontUpdate).Tools);
        Assert.Equal(tool, ToolCatalog.Search("wsus").First());
        Assert.True(ButtonRules.NeedsService(AppButton.CheckWu));
    }

    // --- The path's verdict after a check, and the fit -------------------------------------------

    private static ProblemPath UpdatePath => ProblemPaths.Get(Problem.WindowsWontUpdate);

    [Fact]
    public void The_path_has_three_tools_and_Check_Microsoft_connections_only_as_the_network_row_s_link()
    {
        Assert.Equal(new[] { "CheckWuButton", "RepairWuButton", "FreeSpaceButton" }, UpdatePath.Tools);
        Assert.Contains(HealthView.Network, UpdatePath.Rows);
        // A connection problem still links to it from the network row, and For IT's search finds it.
        Assert.Equal(HealthFix.ConnectivityTest, HealthChecks.FixFor(HealthChecks.Connectivity));
        Assert.Equal("ConnectivityButton", ToolCatalog.Search("connection").First().Button);
    }

    [Fact]
    public void Before_a_check_the_health_rows_decide_the_path_s_verdict()
    {
        var rows = HealthView.Rows(CompactLayoutTests.Health());
        Assert.Null(WuCheck.ForPath(null));
        Assert.Equal(("These checks found no problem. If it still does not work, the tools below may help, or copy details for IT.", Tone.Success), ProblemPaths.Verdict(UpdatePath, rows));
        // A run that did not finish, or an older service without the check, adds nothing.
        Assert.Null(WuCheck.ForPath(new RunOutcome { Action = ToolbeltAction.CheckWindowsUpdate, Result = ResultCode.InvalidRequest, Message = "Unknown action." }));
        var running = Outcome("failing");
        running.InProgress = true;
        Assert.Null(WuCheck.ForPath(running));
    }

    [Fact]
    public void A_check_that_found_nothing_says_so_with_the_clean_health_rows()
    {
        var rows = HealthView.Rows(CompactLayoutTests.Health());
        var check = WuCheck.ForPath(Outcome("up-to-date", 0, false, ""));
        Assert.True(check.Clean);
        Assert.Equal(("Check Windows Update and these checks found no problem. If it still does not work, copy details for IT.", Tone.Success), ProblemPaths.Verdict(UpdatePath, rows, check));
        // A health row that needs attention still speaks for itself.
        var full = HealthView.Rows(CompactLayoutTests.Health(fail: HealthChecks.FreeSpace));
        Assert.StartsWith("Problem: " + HealthChecks.FreeSpace, ProblemPaths.Verdict(UpdatePath, full, check).Text);
        // Another path never uses the check.
        Assert.Equal(ProblemPaths.Verdict(ProblemPaths.Get(Problem.CantConnect), rows).Text, ProblemPaths.Verdict(ProblemPaths.Get(Problem.CantConnect), rows, check).Text);
    }

    [Theory]
    [InlineData("blocked-by-policy", false, "Check Windows Update: a policy decides about updates on this device. Next: copy details for IT.", Tone.Warning)]
    [InlineData("failing", true, "Check Windows Update: updates failed on this device. Next: Repair Windows Update.", Tone.Warning)]
    [InlineData("failing", false, "Check Windows Update: updates failed on this device. Next: copy details for IT.", Tone.Warning)]
    [InlineData("services-disabled", false, "Check Windows Update: a service Windows Update needs is disabled. Contact your IT support.", Tone.Error)]
    [InlineData("behind", true, "Check Windows Update: Windows is behind on updates. Next: Repair Windows Update.", Tone.Warning)]
    [InlineData("restart-pending", false, "Check Windows Update: Windows is waiting for a restart. Restart, then check again.", Tone.Warning)]
    [InlineData("low-space", false, "Check Windows Update: too little free disk space for updates. Try Free up disk space.", Tone.Warning)]
    public void A_check_that_found_something_replaces_no_problem_with_what_it_found_and_its_step(string verdict, bool repair, string text, Tone tone)
    {
        // The Sandbox's case: the three health rows are OK, the check found a policy.
        var rows = HealthView.Rows(CompactLayoutTests.Health());
        Assert.Equal((text, tone), ProblemPaths.Verdict(UpdatePath, rows, WuCheck.ForPath(Outcome(verdict, 2, repair))));
    }

    [Fact]
    public void A_current_device_with_a_point_to_check_names_it_on_the_path()
    {
        var finding = "{\"type\":\"finding\",\"run\":\"ab12cd34\",\"severity\":\"WARNING\",\"id\":\"WUCHECK-PAUSED-USER\",\"title\":\"Updates are paused in Settings\",\"detail\":\"\",\"next\":\"Open Settings > Windows Update and select Resume updates.\",\"support\":\"\"}";
        var check = WuCheck.ForPath(Outcome("up-to-date", 2, false, "", "paused-user", false, finding));
        Assert.Equal(("Check Windows Update: Windows is up to date, but one point needs checking: Updates are paused in Settings.", Tone.Warning),
            ProblemPaths.Verdict(UpdatePath, HealthView.Rows(CompactLayoutTests.Health()), check));
    }

    [Fact]
    public void The_path_fits_at_100_125_and_150_percent_with_the_check_s_result_and_a_repair_makes_the_check_old()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            // A Sandbox state at 150 %: the three rows OK, the check found a policy, and a support
            // contact that wraps to two lines.
            var window = MainWindowPagesTests.NewWindow();
            var status = new StatusInfo
            {
                SupportText = "Contoso IT Service Desk, Monday to Friday 08:00 to 18:00, ext. 5555, or open a request in the Contoso service portal any time",
                SupportUrl = "https://support.contoso.example/",
            };
            MainWindowPagesTests.Invoke(window, "ApplyHealth", status, false, CompactLayoutTests.Health(), new DsregInfo(), false);
            MainWindowPagesTests.Call(window, "ShowProblem", Problem.WindowsWontUpdate, false);
            var verdict = MainWindowPagesTests.Get<System.Windows.Controls.TextBlock>(window, "ProblemVerdict");
            Assert.StartsWith("These checks found no problem.", verdict.Text);
            Assert.Equal(2, ((IEnumerable<object>)MainWindowPagesTests.Get<System.Windows.Controls.ItemsControl>(window, "ProblemTools").ItemsSource).Count());
            Assert.Single((IEnumerable<object>)MainWindowPagesTests.Get<System.Windows.Controls.ItemsControl>(window, "ProblemHeavyTools").ItemsSource);
            CompactLayoutTests.AssertFits(window, "Windows Update keeps failing before a check");

            var outcome = Outcome("blocked-by-policy", 2, false, "", "wsus,deferred-7");
            MainWindowPagesTests.Invoke(window, "ShowFinal", outcome, false);
            MainWindowPagesTests.Call(window, "FollowWuCheck", outcome);
            Assert.Equal("Check Windows Update: a policy decides about updates on this device. Next: copy details for IT.", verdict.Text);
            Assert.Equal(System.Windows.Visibility.Visible, MainWindowPagesTests.Get<System.Windows.Controls.StackPanel>(window, "StatusPanel").Visibility);
            Assert.StartsWith("A policy on this device decides about its updates", MainWindowPagesTests.Get<System.Windows.Controls.TextBlock>(window, "StatusText").Text);
            CompactLayoutTests.AssertFits(window, "Windows Update keeps failing with the check's result");

            // A repair can change the answer: the health rows decide again until the next check.
            MainWindowPagesTests.Call(window, "FollowWuCheck", new RunOutcome { Action = ToolbeltAction.RepairWU, Result = ResultCode.Completed, ExitCode = 0 });
            Assert.StartsWith("These checks found no problem.", verdict.Text);
        });
    }
}
