using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.Actions;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Platform;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.State;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// What a virtual machine and a real device showed. Readable failed settings without a GUID,
/// Windows Hello and the clock on a virtual machine or a multi-session host, the IME row's wording,
/// the location service's fallback, an earlier revision's failed attempt, detection evidence,
/// "Not applicable", the plain post-check text, the Company Portal link and the full app ID.
/// Every ID here is made up for the test.
/// </summary>
public class DeviceObservationTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 14, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.CreateCustomTimeZone("Test+1", TimeSpan.FromHours(1), "Test+1", "Test+1");

    // A made-up tenant ID inside a Windows Hello path, and made-up app IDs.
    private const string TenantLike = "eeeeeeee-ffff-0000-1111-222222222222";
    private const string AppA = "99999999-8888-7777-6666-555555555555";
    private const string AppB = "77777777-6666-5555-4444-333333333333";

    private static StatusInfo Status(DeviceReadouts readouts) => new() { Readouts = readouts };

    // ---- 1. Machine kind -------------------------------------------------------------------

    [Theory]
    [InlineData("Microsoft Corporation", "Virtual Machine", true)]
    [InlineData(" microsoft corporation ", "virtual machine", true)]
    [InlineData("Contoso Ltd", "Contoso Laptop 13", false)]
    [InlineData("Microsoft Corporation", "Surface Laptop 7", false)]
    [InlineData(null, null, false)]
    public void A_virtual_machine_is_the_Hyper_V_manufacturer_and_model(string manufacturer, string model, bool expected)
        => Assert.Equal(expected, MachineProbe.IsVirtualMachine(manufacturer, model));

    [Theory]
    [InlineData("ServerRdsh", "Windows 10 Enterprise multi-session", true)]
    [InlineData("EnterpriseMultiSession", "Windows 11 Enterprise multi-session", true)]
    [InlineData("Enterprise", "Windows 11 Enterprise", false)]
    [InlineData("Professional", "Windows 11 Pro", false)]
    public void A_multi_session_host_is_its_edition_or_its_product_name(string edition, string product, bool expected)
        => Assert.Equal(expected, MachineProbe.IsMultiSession(edition, product));

    [Fact]
    public void Windows_Hello_cannot_apply_on_a_virtual_machine_or_a_multi_session_host()
    {
        Assert.True(MachineProbe.NoWindowsHello(new MachineReadout { VirtualMachine = true, MultiSession = false }));
        Assert.True(MachineProbe.NoWindowsHello(new MachineReadout { VirtualMachine = false, MultiSession = true }));
        Assert.False(MachineProbe.NoWindowsHello(new MachineReadout { VirtualMachine = false, MultiSession = false }));
        Assert.False(MachineProbe.NoWindowsHello(new MachineReadout()));
        Assert.False(MachineProbe.NoWindowsHello(null));
    }

    [Fact]
    public void The_device_report_says_the_machine_kind()
    {
        Assert.Equal("Machine kind   : virtual machine yes, multi-session host yes", DeviceSettingRows.MachineLine(new MachineReadout { VirtualMachine = true, MultiSession = true }));
        Assert.Equal("Machine kind   : virtual machine no, multi-session host no", DeviceSettingRows.MachineLine(new MachineReadout { VirtualMachine = false, MultiSession = false }));
        Assert.Equal("Machine kind   : virtual machine unknown, multi-session host unknown", DeviceSettingRows.MachineLine(null));
    }

    // ---- 1. Failed settings: no GUID, never cut inside a word, plain names ------------------

    [Fact]
    public void A_GUID_segment_never_reaches_the_setting_token()
    {
        var token = ProfileErrorsProbe.ShortSetting("./User/Vendor/MSFT/PassportForWork/" + TenantLike + "/Policies/PINComplexity/LowercaseLetters");
        Assert.Equal("PassportForWork/Policies/PINComplexity/LowercaseLetters", token);
        Assert.Equal("PassportForWork/Policies/UsePassportForWork",
            ProfileErrorsProbe.ShortSetting("./Device/Vendor/MSFT/PassportForWork/{" + TenantLike.ToUpperInvariant() + "}/Policies/UsePassportForWork"));
        Assert.DoesNotContain(TenantLike, token, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_long_path_loses_its_leading_segments_never_part_of_a_word()
    {
        var path = "./Device/Vendor/MSFT/ContosoAreaWithALongName/SomeGroupOfSettingsThatIsLong/AnotherNestedGroupOfSettings/TheFinalSettingName";
        var token = ProfileErrorsProbe.ShortSetting(path);
        Assert.True(token.Length <= ProfileErrorsProbe.MaxSettingLength, token);
        Assert.EndsWith("/TheFinalSettingName", token);
        // Every segment it keeps is a whole segment of the path.
        Assert.All(token.Split('/'), s => Assert.Contains("/" + s + "/", path + "/"));

        // One segment longer than the limit: cut before a word, never inside one.
        var single = ProfileErrorsProbe.ShortSetting("./Vendor/MSFT/" + string.Concat(Enumerable.Repeat("LowercaseLetters", 8)));
        Assert.True(single.Length <= ProfileErrorsProbe.MaxSettingLength);
        Assert.EndsWith("Letters", single);
    }

    [Theory]
    [InlineData("PassportForWork/Policies/PINComplexity/LowercaseLetters", "Windows Hello PIN: lowercase letters")]
    [InlineData("PassportForWork/Policies/PINComplexity/MinimumPINLength", "Windows Hello PIN: minimum PIN length")]
    [InlineData("PassportForWork/Policies/UseCertificateForOnPremAuth", "Windows Hello: use certificate for on prem auth")]
    [InlineData("BitLocker/RequireDeviceEncryption", "BitLocker: require device encryption")]
    [InlineData("Defender/AllowRealtimeMonitoring", "Microsoft Defender: allow realtime monitoring")]
    // Unknown areas: the last two segments with spaces between the words.
    [InlineData("ContosoArea/Policies/PINComplexity/LowercaseLetters", "PIN complexity: lowercase letters")]
    [InlineData("ContosoArea/SomeSetting", "Contoso area: some setting")]
    [InlineData("ContosoSetting", "Contoso setting")]
    public void A_failed_setting_reads_in_plain_words(string token, string expected)
        => Assert.Equal(expected, SettingNames.Friendly(token));

    [Fact]
    public void Windows_Hello_settings_on_a_virtual_machine_are_kept_apart_and_not_counted()
    {
        (int, int?, IEnumerable<object>) Event(string path) => (2, 4, new object[] { path, -2147024891 });
        var events = new List<(int Level, int? ProcessId, IEnumerable<object> Values)>
        {
            Event("./User/Vendor/MSFT/PassportForWork/" + TenantLike + "/Policies/PINComplexity/LowercaseLetters"),
            Event("./User/Vendor/MSFT/PassportForWork/" + TenantLike + "/Policies/PINComplexity/UppercaseLetters"),
        };
        var onVm = ProfileErrorsProbe.Summarize(events, _ => true, noWindowsHello: true);
        Assert.Equal(0, onVm.Errors);
        Assert.Empty(onVm.Settings);
        Assert.Equal(2, onVm.NotApplicableErrors);
        Assert.Equal(2, onVm.NotApplicable.Count);

        // A physical device: the same settings are failed settings.
        var physical = ProfileErrorsProbe.Summarize(events, _ => true);
        Assert.Equal(2, physical.Errors);
        Assert.Equal(2, physical.Settings.Count);
        Assert.Empty(physical.NotApplicable);
    }

    [Fact]
    public void A_failed_command_on_an_Office_installation_that_succeeded_is_no_failed_setting()
    {
        // The MDM client sends an Office installation again and logs an error, while the Office CSP's
        // own status of that installation says it succeeded (FinalStatus 70; 60 is failed).
        (int, int?, IEnumerable<object>) Event(string path) => (2, 4, new object[] { path, -2102722558 });
        var events = new List<(int Level, int? ProcessId, IEnumerable<object> Values)>
        {
            Event("./Vendor/MSFT/Office/Installation/install-one/install"),
            Event("./Device/Vendor/MSFT/Office/Installation/install-two/install"),
            Event("./Vendor/MSFT/Office/Installation/install-three"),
            Event("./Vendor/MSFT/Policy/Config/Defender/AllowRealtimeMonitoring"),
        };
        var status = new Dictionary<string, object> { ["install-one"] = 70, ["install-two"] = 60 };
        object FinalStatus(string id) => status.TryGetValue(id, out var v) ? v : null;
        var summary = ProfileErrorsProbe.SummarizeTimed(events.Select(e => (e.Item1, e.Item2, e.Item3, (DateTime?)null)), _ => true,
            succeeded: path => ProfileErrorsProbe.OfficeInstallationSucceeded(path, FinalStatus));
        Assert.Equal(1, summary.AlreadySucceeded);
        // A failed installation and one without a status stay failed settings, as does any other setting.
        Assert.Equal(3, summary.Errors);
        Assert.Equal(3, summary.Settings.Count);
        Assert.DoesNotContain(summary.Settings, s => s.Contains("install-one"));

        Assert.Equal("install-one", ProfileErrorsProbe.OfficeInstallationId("./Vendor/MSFT/Office/Installation/install-one/install"));
        Assert.Null(ProfileErrorsProbe.OfficeInstallationId("./Vendor/MSFT/Office/Installation/..\\other/install"));
        Assert.Null(ProfileErrorsProbe.OfficeInstallationId("./Vendor/MSFT/Policy/Config/Defender/AllowRealtimeMonitoring"));
        Assert.True(ProfileErrorsProbe.OfficeInstallationSucceeded("./Vendor/MSFT/Office/Installation/x/install", _ => "70"));
        Assert.False(ProfileErrorsProbe.OfficeInstallationSucceeded("./Vendor/MSFT/Office/Installation/x/install", _ => 0));

        // The row names it as a user knows it, and the report counts the commands left out.
        Assert.Equal("Installing Office (Microsoft 365 Apps)", SettingNames.Friendly("Office/Installation/install"));
        Assert.Contains("1 failed command on an Office installation that succeeded on this device (not counted)", DeviceSettingRows.AlreadySucceededReport(new ProfileErrorsReadout { AlreadySucceeded = 1 }));
    }

    [Fact]
    public void Not_applicable_settings_make_an_Info_row_with_no_step_and_no_attention()
    {
        var readouts = new DeviceReadouts
        {
            Machine = new MachineReadout { VirtualMachine = true, MultiSession = true },
            Profiles = new ProfileErrorsReadout { NotApplicableErrors = 2, NotApplicable = new List<string> { "PassportForWork/Policies/PINComplexity/LowercaseLetters" } },
        };
        var row = DeviceSettingRows.Profiles(Status(readouts), readouts.Profiles);
        Assert.Equal(HealthStatus.Pass, row.Status);
        Assert.True(row.Info);
        Assert.Equal("Info", row.StatusWord);
        Assert.Equal(HealthFix.None, row.Fix);
        Assert.Equal("", HealthChecks.NextStep(row));
        Assert.Equal("Not applicable on this device: 1 Windows Hello setting", row.Detail);
        Assert.Contains("Windows Hello PIN: lowercase letters", row.ReportDetail);
        Assert.Contains("multi-session host", row.ReportDetail);

        // Home: not "to check", and the status line says healthy.
        var rows = HealthView.Rows(new[] { row });
        Assert.Equal("All 1 checks OK.", HealthView.Verdict(rows));
        Assert.Equal(Tone.Success, HealthChecks.Summary(new[] { row }).Tone);
        Assert.Empty(HealthChecks.Cards(new[] { row }));
        Assert.True(rows.Single().Info);
    }

    [Fact]
    public void Real_failures_still_count_and_read_plainly_without_a_GUID()
    {
        var readouts = new DeviceReadouts
        {
            Machine = new MachineReadout { VirtualMachine = false, MultiSession = false },
            Profiles = new ProfileErrorsReadout { Errors = 1, Settings = new List<string> { "PassportForWork/Policies/PINComplexity/LowercaseLetters" } },
        };
        var row = DeviceSettingRows.Profiles(Status(readouts), readouts.Profiles);
        Assert.Equal(HealthStatus.Warn, row.Status);
        Assert.False(row.Info);
        Assert.Equal("1 setting failed in the last 7 days: Windows Hello PIN: lowercase letters", row.Detail);
        Assert.Equal(HealthFix.RetryAppsAndPolicies, row.Fix);
        var card = HealthChecks.Card(row);
        Assert.DoesNotContain(TenantLike, card.Title + card.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_setting_that_failed_on_removal_reads_removing_failed_and_one_that_failed_to_apply_does_not()
    {
        const string certificate = "./Device/Vendor/MSFT/RootCATrustedCertificates/TrustedPublisher/abcdef0123456789abcdef0123456789abcdef01/EncodedCertificate";
        (int, int?, IEnumerable<object>) Event(string commandType, string path) =>
            (2, 4, new object[] { "Configuration source", "MDMDeviceWithAAD", "RootCATrustedCertificates", commandType, path, "Unknown Win32 Error code: 0x86000011" });
        var events = new List<(int Level, int? ProcessId, IEnumerable<object> Values)>
        {
            // Newest first: the removal of the certificate failed; the OOBE setting failed on Add.
            Event("Clear: first phase of Delete", certificate),
            Event("Add: from Replace or Add", "./Device/Vendor/MSFT/Policy/Config/System/AllowOOBEUpdates"),
            Event("Add: from Replace or Add", certificate),
        };
        var summary = ProfileErrorsProbe.Summarize(events, _ => true);
        Assert.Equal(3, summary.Errors);
        Assert.Equal(new[] { "TrustedPublisher/abcdef0123456789abcdef0123456789abcdef01/EncodedCertificate" }, summary.Removals);
        Assert.False(ProfileErrorsProbe.IsRemoval(new object[] { "Add: from Replace or Add", "./Vendor/MSFT/Policy/Config/A/B" }));
        Assert.True(ProfileErrorsProbe.IsRemoval(new object[] { "(Clear: first phase of Delete)" }));

        var readouts = new DeviceReadouts { Machine = new MachineReadout { VirtualMachine = false, MultiSession = false }, Profiles = summary };
        var row = DeviceSettingRows.Profiles(Status(readouts), readouts.Profiles);
        Assert.Equal("2 settings failed in the last 7 days: removing Trusted Publisher certificate ABCDEF01… (encoded certificate) failed; System: allow OOBE updates", row.Detail);
        Assert.Contains("settings: removing Trusted Publisher certificate ABCDEF01… (encoded certificate) (TrustedPublisher/abcdef0123456789abcdef0123456789abcdef01/EncodedCertificate) failed; System: allow OOBE updates (System/AllowOOBEUpdates)", row.ReportDetail, StringComparison.Ordinal);

        // An older service sends no removals: every setting reads as before.
        summary.Removals = null;
        Assert.Equal("2 settings failed in the last 7 days: Trusted Publisher certificate ABCDEF01… (encoded certificate); System: allow OOBE updates",
            DeviceSettingRows.Profiles(Status(readouts), summary).Detail);
    }

    [Fact]
    public void A_failed_setting_says_when_it_last_failed_and_the_hint_says_a_failure_stays_listed()
    {
        var newest = DateTime.UtcNow.AddHours(-2);
        (int, int?, IEnumerable<object>, DateTime?) Event(string path, DateTime? utc) =>
            (2, 4, new object[] { "Configuration source", "MDMDeviceWithAAD", "Policy", "Add: from Replace or Add", path, "Unknown Win32 Error code: 0x86000011" }, utc);
        var events = new List<(int Level, int? ProcessId, IEnumerable<object> Values, DateTime? Utc)>
        {
            // Newest first: the OOBE setting twice, the newest time is the one kept.
            Event("./Device/Vendor/MSFT/Policy/Config/System/AllowOOBEUpdates", newest),
            Event("./Device/Vendor/MSFT/Policy/Config/System/AllowOOBEUpdates", newest.AddDays(-1)),
            Event("./Device/Vendor/MSFT/Policy/Config/Update/ActiveHoursStart", null),
        };
        var summary = ProfileErrorsProbe.SummarizeTimed(events, _ => true);
        Assert.Equal(new[] { newest, DateTime.MinValue }, summary.SettingTimes);

        var readouts = new DeviceReadouts { Machine = new MachineReadout { VirtualMachine = false, MultiSession = false }, Profiles = summary };
        var row = DeviceSettingRows.Profiles(Status(readouts), readouts.Profiles);
        // The row names when each last failed; a setting without a time has no date.
        Assert.StartsWith("2 settings failed in the last 7 days: System: allow OOBE updates, last failed ", row.Detail, StringComparison.Ordinal);
        Assert.EndsWith(ToolRuns.ShortWhen(newest, TimeZoneInfo.Local, DateTime.UtcNow) + "; " + SettingNames.Friendly("Update/ActiveHoursStart"), row.Detail, StringComparison.Ordinal);
        Assert.Contains("(System/AllowOOBEUpdates), last failed " + ToolRuns.FullWhen(newest, TimeZoneInfo.Local), row.ReportDetail, StringComparison.Ordinal);
        // The hint promises no fix: a failure stays listed for 7 days, Retry asks for the current settings.
        Assert.Equal(HealthChecks.ProfilesFailedHint, HealthChecks.Hint(HealthChecks.Profiles, false));
        Assert.Contains("stays listed for 7 days", HealthChecks.ProfilesFailedHint, StringComparison.Ordinal);
        Assert.DoesNotContain("applies them again", HealthChecks.ProfilesFailedHint, StringComparison.Ordinal);
    }

    // ---- 2. The clock on a virtual machine ------------------------------------------------

    [Theory]
    [InlineData(true, 1, true)]
    [InlineData(true, 0, false)]
    [InlineData(false, 1, false)]
    public void The_host_keeps_the_clock_only_on_a_virtual_machine_with_the_Hyper_V_provider(bool vm, int enabled, bool expected)
        => Assert.Equal(expected, ClockProbe.HostTimeProvider(vm, enabled));

    [Fact]
    public void An_unknown_machine_kind_says_nothing_about_the_host_clock()
    {
        Assert.Null(ClockProbe.HostTimeProvider(null, 1));
        Assert.False(ClockProbe.HostTimeProvider(true, null));
    }

    [Theory]
    [InlineData("no-answer")]
    [InlineData("no-source")]
    public void A_virtual_machine_with_the_Hyper_V_time_provider_is_synced_by_its_host(string unavailable)
    {
        var row = DeviceSettingRows.Clock(Status(null), new ClockReadout { Source = "ntp", Unavailable = unavailable, HostTimeProvider = true });
        Assert.Equal(HealthStatus.Pass, row.Status);
        Assert.True(row.Info);
        Assert.Equal("Synced by the virtual machine host", row.Detail);
        Assert.Equal(HealthFix.None, row.Fix);
        Assert.Contains("Hyper-V time provider", row.ReportDetail);
    }

    [Fact]
    public void A_virtual_machine_without_the_provider_and_a_physical_device_keep_the_check()
    {
        var withoutProvider = DeviceSettingRows.Clock(Status(null), new ClockReadout { Source = "ntp", Unavailable = "no-answer", HostTimeProvider = false });
        Assert.Equal(HealthStatus.Warn, withoutProvider.Status);
        Assert.Equal("Unknown: the time source did not answer", withoutProvider.Detail);
        var physical = DeviceSettingRows.Clock(Status(null), new ClockReadout { Source = "ntp", Unavailable = "no-answer" });
        Assert.Equal(HealthStatus.Warn, physical.Status);
        Assert.False(physical.Info);
        // A stopped time service stays a check with Sync clock, also on a virtual machine.
        var stopped = DeviceSettingRows.Clock(Status(null), new ClockReadout { Source = "ntp", Unavailable = "not-running", HostTimeProvider = true });
        Assert.Equal(HealthStatus.Warn, stopped.Status);
        Assert.Equal(HealthFix.SyncClock, stopped.Fix);
        // A measured offset is judged as everywhere.
        var measured = DeviceSettingRows.Clock(Status(null), new ClockReadout { Source = "ntp", OffsetSeconds = 400, HostTimeProvider = true });
        Assert.Equal(HealthStatus.Fail, measured.Status);
    }

    // ---- 3. The IME row reads once ---------------------------------------------------------

    [Fact]
    public void A_failed_check_in_reads_once_in_the_row()
    {
        var status = new StatusInfo { Enrolled = true, ImeCheckInErrorUtc = "2026-11-12T13:22:00Z" };
        var source = new FakeHealthSource { Now = Now };
        var rows = HealthView.Rows(HealthChecks.Run(source, status, out _));
        var ime = rows.Single(r => r.Name == HealthView.AppInstaller);
        Assert.StartsWith("Last attempt failed (no error code in the log), ", ime.Detail);
        Assert.DoesNotContain("Last check-in: Last", ime.Detail);
        Assert.DoesNotContain("failed:", ime.Detail);

        // With a check-in before it, and a code.
        status.LastImeCheckInUtc = "2026-11-12T12:40:00Z";
        status.ImeCheckInError = "401";
        var withLast = HealthView.Rows(HealthChecks.Run(source, status, out _)).Single(r => r.Name == HealthView.AppInstaller);
        Assert.StartsWith("Last attempt failed (sign-in refused, 401), ", withLast.Detail);
        Assert.Contains(". Last check-in ", withLast.Detail);
    }

    // ---- 13. The location service's fallback is no failed check-in ---------------------------

    private static ImeCheckInFacts EvaluateCheckIn(params (DateTime Utc, string Message)[] records)
    {
        var table = ImeLogPatternTable.Load(PatternFile());
        var rules = ImeCheckInEvidence.Matchers(table.CheckIn);
        return ImeCheckInEvidence.Evaluate(records, rules);
    }

    [Fact]
    public void A_failure_followed_by_the_location_services_success_is_no_failure()
    {
        var t = Now.AddMinutes(-30);
        var result = EvaluateCheckIn(
            (t.AddHours(-2), "Get policies = [{\"Id\":\"" + AppA + "\"}]"),
            (t, "[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController with url checkin.dm.contoso.example/RestUserAuthLocationService/RestUserAuthLocationService/Certificate/ServiceAddresses"),
            (t, "[Location Service] Calling LocationService ServiceAddresses Controller with https://manage.contoso.example/RestUserAuthLocationService/RestUserAuthLocationService/Certificate/ServiceAddresses"),
            (t, "[Location Service] Success!! LocationService ServiceAddresses Controller with https://manage.contoso.example/RestUserAuthLocationService/RestUserAuthLocationService/Certificate/ServiceAddresses statusCode = OK"),
            (t.AddSeconds(1), "Got address for service: SideCarGatewayService https://contoso.example/SideCar/"));
        Assert.Null(result.LastFailureUtc);
        Assert.Equal(t.AddHours(-2), result.LastSuccessUtc);
    }

    [Fact]
    public void A_failure_without_a_following_success_is_still_a_failure()
    {
        var t = Now.AddMinutes(-30);
        var result = EvaluateCheckIn(
            (t.AddHours(-2), "Get policies = [{\"Id\":\"" + AppA + "\"}]"),
            (t, "[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController with url checkin.dm.contoso.example/RestUserAuthLocationService/ServiceAddresses"));
        Assert.Equal(t, result.LastFailureUtc);

        // A success before the failure does not cancel it.
        var earlier = EvaluateCheckIn(
            (t.AddMinutes(-10), "[Location Service] Success!! LocationService ServiceAddresses Controller statusCode = OK"),
            (t, "[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController with url checkin.dm.contoso.example/x"));
        Assert.Equal(t, earlier.LastFailureUtc);

        // A later successful call (the next attempt) recovers it too; the success is no check-in itself.
        var later = EvaluateCheckIn(
            (t, "[Location Service] Failed to Get Endpoint From LocationServiceServiceAddressesController with url checkin.dm.contoso.example/x"),
            (t.AddMinutes(10), "[Location Service] Success!! LocationService ServiceAddresses Controller statusCode = OK"));
        Assert.Null(later.LastFailureUtc);
        Assert.Null(later.LastSuccessUtc);
    }

    private sealed class FakeHealthSource : IHealthSource
    {
        public DateTime Now { get; set; }

        public DateTime UtcNow => Now;
        public string ImeServiceStatus() => "Running";
        public DsregInfo Dsreg() => new() { AzureAdJoined = "YES", AzureAdPrt = "YES", TenantName = "Contoso" };
        public DateTime? MdmCertificateExpiryUtc() => Now.AddDays(300);
        public bool CanReach(string host, int port, int timeoutMs) => true;
        public double FreeSystemDriveGB() => 100;
        public bool ServicingRestartPending() => false;
    }

    private static string PatternFile()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        Assert.True(dir != null, "Repository root not found.");
        return Path.Combine(dir.FullName, "src", "Engine", "ImeLogPatterns.json");
    }

    // ---- 12. A connect task that faults after the time-out is observed -----------------------

    // No GC.Collect / WaitForPendingFinalizers here: in a test process with WPF windows on STA
    // threads that wait can block for good (it hung a CI test run). The test checks
    // the mechanism instead: after a time-out a continuation reads the late fault, which observes it.
    [Fact]
    public async Task A_connect_task_that_faults_after_the_time_out_is_observed()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.False(SystemHealthSource.WaitObserved(pending.Task, 10, out var observer));
        Assert.NotNull(observer);
        pending.SetException(new ObjectDisposedException("TcpClient", "connect ended after the client was disposed"));
        Assert.True(await Task.WhenAny(observer, Task.Delay(TimeSpan.FromSeconds(10))) == observer, "The continuation that observes the fault did not run.");
        Assert.Equal(TaskStatus.RanToCompletion, observer.Status);
        Assert.True(pending.Task.IsFaulted);
    }

    [Fact]
    public void A_connect_task_that_finishes_in_time_needs_no_observer()
    {
        var done = Task.FromResult(true);
        Assert.True(SystemHealthSource.WaitObserved(done, 10, out var observer));
        Assert.Null(observer);
    }

    // ---- 6. An earlier revision's failed attempt ---------------------------------------------

    // 0x8007EA68 as the IME writes it: a signed 32-bit number (60008, PSAppDeployToolkit's range).
    private const long PsadtSigned = -2146964888L;

    [Fact]
    public void The_engine_keeps_an_earlier_revisions_failed_attempt_as_evidence()
    {
        var h = new EngineHarness();
        var user = EngineAppDiagnosisTests.UserOne;
        EngineAppDiagnosisTests.AddApp(h.Registry, user, AppA, 1, intent: 1, state: 5000, code: PsadtSigned, updated: "11/11/2026 09:30:00");
        EngineAppDiagnosisTests.AddApp(h.Registry, user, AppA, 2, intent: 1, state: null, compliance: 2, updated: "11/11/2026 09:35:00");
        // Another app whose earlier revision succeeded: nothing to show.
        EngineAppDiagnosisTests.AddApp(h.Registry, user, AppB, 1, intent: 3, state: 1000, compliance: 1);
        EngineAppDiagnosisTests.AddApp(h.Registry, user, AppB, 2, intent: 3, state: null, compliance: 2);
        var run = h.NewRun("diag0057", "DiagnoseApps");
        run.CallerSid = EngineAppDiagnosisTests.EntraSid(user);

        Assert.Equal(EngineExitCode.Success, new DiagnoseAppsHandler().Run(run));

        var apps = h.Records("app-diagnosis");
        var a = apps.Single(x => x.Str("app") == AppA);
        Assert.Equal("2", a.Str("revision"));
        Assert.Equal("AVAILABLE", a.Str("verdict"));
        Assert.Equal("1", a.Str("earlierrev"));
        Assert.Equal("0x8007EA68", a.Str("earliercode"));
        Assert.Equal("2026-11-11T09:30:00Z", a.Str("earlierutc"));
        var b = apps.Single(x => x.Str("app") == AppB);
        Assert.Equal("", b.Str("earliercode"));
    }

    [Fact]
    public void An_earlier_versions_failure_shows_as_the_last_attempt_with_the_decoded_code()
    {
        var app = Evidence(verdict: "AVAILABLE", intent: "available");
        app.EarlierRevision = 1;
        app.EarlierCode = "0x8007EA68";
        app.EarlierText = "2026-11-11T09:30:00Z";
        var f = AppDecisions.Decide(app, new[] { app }, Now, Amsterdam);

        // The current revision's state stays the verdict.
        Assert.Equal(AppCase.Offered, f.Case);
        Assert.StartsWith("Last attempt (an earlier version of this app, 11 Nov): failed, exit code 60008.", f.Evidence);
        Assert.Contains("PSAppDeployToolkit's own range (60000 to 68999)", f.Evidence);
        Assert.Contains(@"%ProgramData%\Logs\Software", f.Support);
        // Copy details for IT carries it.
        var card = f.Card();
        Assert.Contains("exit code 60008", card.Detail);
        Assert.Contains(@"%WINDIR%\Logs\Software", card.Support);
    }

    [Theory]
    [InlineData("0x8007EA68", 60008)]
    [InlineData("0x80070643", 1603)]
    public void An_HRESULT_from_the_IME_carries_the_installers_exit_code(string code, int exit)
        => Assert.Equal(exit, AppDecisions.InstallerExitCode(code));

    [Fact]
    public void Only_the_PSAppDeployToolkit_range_is_named_and_no_code_gets_a_meaning()
    {
        Assert.Null(AppDecisions.InstallerExitCode("0x87D1041C"));
        Assert.Equal("", AppDecisions.PsadtSentence("0x80070643"));
        Assert.Equal("", AppDecisions.PsadtSentence("0x87D1041C"));
        Assert.NotEqual("", AppDecisions.PsadtSentence("0x8007EA68"));
        Assert.Equal("exit code 1603", AppDecisions.CodeWords("0x80070643"));
        Assert.Equal("error 0x87D1041C", AppDecisions.CodeWords("0x87D1041C"));
        // The current attempt's code is said the same way.
        var f = AppDecisions.Decide(Evidence(verdict: "RETRY-HELPS", code: "0x8007EA68"), Array.Empty<AppEvidence>(), Now, Amsterdam);
        Assert.Contains("(exit code 60008, 0x8007EA68)", f.Text);
        Assert.Contains("PSAppDeployToolkit", f.Evidence);
    }

    [Fact]
    public void A_current_attempt_hides_the_earlier_one()
    {
        var app = Evidence(verdict: "RETRY-HELPS", code: "0x80070643");
        app.Enforcement = 5000;
        app.EarlierRevision = 1;
        app.EarlierCode = "0x8007EA68";
        Assert.Equal("", AppDecisions.EarlierAttempt(app, Amsterdam));
    }

    // ---- 7. Detection evidence --------------------------------------------------------------

    private static CmTraceRecord Rec(string message, DateTime utc, long thread = 21) => new() { Message = message, Utc = utc, Thread = thread, Source = "AgentExecutor.log", Component = "AgentExecutor" };

    [Fact]
    public void The_newest_detection_script_run_is_read_from_the_agent_log()
    {
        var table = ImeLog.Rules(PatternFile());
        var t = Now.AddHours(-1);
        var records = new[]
        {
            Rec("ExecutorLog AgentExecutor gets invoked", t),
            Rec(@"cmd line for running powershell is -NoProfile -executionPolicy bypass -file  ""C:\Program Files (x86)\Microsoft Intune Management Extension\Content\DetectionScripts\" + AppA + @"_2.ps1"" ", t.AddSeconds(1)),
            Rec("runAs32BitOn64 = False, so Disable Wow64FsRedirection", t.AddSeconds(1)),
            Rec("Powershell exit code is 1", t.AddSeconds(3)),
            Rec("length of out=2", t.AddSeconds(3)),
            Rec("length of error=0", t.AddSeconds(3)),
            Rec("Powershell script is failed to execute", t.AddSeconds(3)),
            // Another app's script on another thread, and a remediation script that is no detection.
            Rec(@"cmd line for running powershell is -file ""C:\Windows\IMECache\DetectionScripts\" + AppB + @"_1.ps1""", t.AddSeconds(2), 22),
            Rec("runAs32BitOn64 = True, so Enable Wow64FsRedirection", t.AddSeconds(2), 22),
            Rec("Powershell exit code is 0", t.AddSeconds(4), 22),
            Rec("length of out=14", t.AddSeconds(4), 22),
            Rec("ExecutorLog AgentExecutor gets invoked", t.AddSeconds(10), 22),
            Rec(@"cmd line for running powershell is -file ""C:\Windows\IMECache\HealthScripts\remediate.ps1""", t.AddSeconds(11), 22),
            Rec("Powershell exit code is 5", t.AddSeconds(12), 22),
        };
        var runs = ImeLog.DetectionRuns(records, table.DetectionRules, new HashSet<string>(new[] { AppA, AppB }, StringComparer.Ordinal));

        var a = runs[AppA];
        Assert.Equal(2, a.Revision);
        Assert.False(a.Ran32Bit);
        Assert.Equal(1, a.ExitCode);
        Assert.False(a.Output);
        Assert.True(a.NotPresent);
        var b = runs[AppB];
        Assert.True(b.Ran32Bit);
        Assert.Equal(0, b.ExitCode);
        Assert.True(b.Output);
        Assert.False(b.NotPresent);
    }

    [Fact]
    public void The_diagnosis_record_carries_the_detection_facts_only()
    {
        var h = new EngineHarness();
        var user = EngineAppDiagnosisTests.UserOne;
        EngineAppDiagnosisTests.AddApp(h.Registry, user, AppA, 2, intent: 1, state: null, compliance: 2);
        var run = h.NewRun("diag0058", "DiagnoseApps");
        run.CallerSid = EngineAppDiagnosisTests.EntraSid(user);
        var handler = new DiagnoseAppsHandler();
        handler.Parts.Detections = (_, ids) => ids.ToDictionary(i => i, i => new ImeDetectionRun { AppId = i, Revision = 2, Utc = Now.AddHours(-1), Ran32Bit = false, ExitCode = 1, OutputLength = 2 });
        Assert.Equal(EngineExitCode.Success, handler.Run(run));
        var a = h.Records("app-diagnosis").Single();
        Assert.Equal("2", a.Str("detectrev"));
        Assert.Equal("64", a.Str("detectbits"));
        Assert.Equal("1", a.Str("detectexit"));
        Assert.Equal("no", a.Str("detectoutput"));
        Assert.Equal("2026-11-12T13:00:00Z", a.Str("detectutc"));
    }

    [Fact]
    public void Intune_does_not_detect_this_app_only_on_proof()
    {
        // The facts on the card and in Copy details for IT, without a verdict of their own.
        var noInstall = Evidence(verdict: "AVAILABLE", intent: "available");
        Detected(noInstall, exit: "1", output: "no", revision: 3);
        var awaiting = AppDecisions.Decide(noInstall, new[] { noInstall }, Now, Amsterdam);
        Assert.Equal(AppCase.Offered, awaiting.Case);
        Assert.Contains("Last detection run: 2026-11-12 14:00 (UTC+01:00), 64-bit, exit code 1, no output.", awaiting.Evidence);
        Assert.Contains("exit code 1, no output", awaiting.Card().Detail);

        // An install of this revision finished with success, and a detection after it says "not there".
        var proven = Evidence(verdict: "IN-PROGRESS");
        proven.InstallOkText = "2026-11-12T12:50:00Z";
        Detected(proven, exit: "1", output: "no", revision: 3);
        var f = AppDecisions.Decide(proven, new[] { proven }, Now, Amsterdam);
        Assert.Equal(AppCase.NotDetectedByIntune, f.Case);
        Assert.Equal("Contoso Agent: Intune does not detect this app", f.Title);
        Assert.Equal("If the app is installed and works, there is nothing to do for you; tell your IT support.", f.NextText);
        Assert.Contains("WOW6432Node", f.Support);
        Assert.True(f.NeedsAttention);
        Assert.Contains("App ID " + AppA, f.Card().Support);

        // A detection of another revision, or before the install: no proof.
        var otherRevision = Evidence(verdict: "IN-PROGRESS");
        otherRevision.InstallOkText = "2026-11-12T12:50:00Z";
        Detected(otherRevision, exit: "1", output: "no", revision: 2);
        Assert.NotEqual(AppCase.NotDetectedByIntune, AppDecisions.Decide(otherRevision, new[] { otherRevision }, Now, Amsterdam).Case);
        var before = Evidence(verdict: "IN-PROGRESS");
        before.InstallOkText = "2026-11-12T13:10:00Z";
        Detected(before, exit: "1", output: "no", revision: 3);
        Assert.False(AppDecisions.ProvenNotDetected(before));
        // A detection that found it: no proof either.
        var found = Evidence(verdict: "IN-PROGRESS");
        found.InstallOkText = "2026-11-12T12:50:00Z";
        Detected(found, exit: "0", output: "yes", revision: 3);
        Assert.False(AppDecisions.ProvenNotDetected(found));
    }

    private static void Detected(AppEvidence app, string exit, string output, int revision)
    {
        app.DetectText = "2026-11-12T13:00:00Z";
        app.DetectRevision = revision;
        app.DetectBits = "64";
        app.DetectExit = exit;
        app.DetectOutput = output;
    }

    // ---- 8. Revisions of an available app ----------------------------------------------------

    [Fact]
    public void A_new_revision_is_read_as_soon_as_the_IME_writes_it()
    {
        var h = new EngineHarness();
        var user = EngineAppDiagnosisTests.UserOne;
        EngineAppDiagnosisTests.AddApp(h.Registry, user, AppA, 1, intent: 1, state: null, compliance: 2);
        EngineAppDiagnosisTests.AddApp(h.Registry, user, AppA, 2, intent: 1, state: null, compliance: 2);
        var first = h.NewRun("diag0059", "DiagnoseApps");
        first.CallerSid = EngineAppDiagnosisTests.EntraSid(user);
        Assert.Equal(2, AppState.Read(first).Rows.Single().Revision);

        // The IME writes revision 3 between two reads (Check again, Reload the app list).
        EngineAppDiagnosisTests.AddApp(h.Registry, user, AppA, 3, intent: 1, state: null, compliance: 2);
        var second = h.NewRun("diag0060", "DiagnoseApps");
        second.CallerSid = EngineAppDiagnosisTests.EntraSid(user);
        Assert.Equal(3, AppState.Read(second).Rows.Single().Revision);
    }

    [Fact]
    public void The_revision_shows_its_day_and_an_available_app_may_have_a_newer_version()
    {
        var app = Evidence(verdict: "AVAILABLE", intent: "available", observed: "2026-11-11T09:35:00Z", revision: 2);
        var f = AppDecisions.Decide(app, new[] { app }, Now, Amsterdam, lastImeCheckInUtc: Now.AddMinutes(-20));
        Assert.Contains(AppDecisions.NewerVersionLine, f.Evidence);
        Assert.Contains("Revision: 2, last checked by the device on 11 Nov", AppDecisions.DetailState(f, Amsterdam));
        // Checked after the last check-in: no such line.
        var fresh = AppDecisions.Decide(app, new[] { app }, Now, Amsterdam, lastImeCheckInUtc: new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc));
        Assert.DoesNotContain(AppDecisions.NewerVersionLine, fresh.Evidence);
        // A required app: never.
        var required = Evidence(verdict: "IN-PROGRESS", observed: "2026-11-11T09:35:00Z");
        Assert.DoesNotContain(AppDecisions.NewerVersionLine, AppDecisions.Decide(required, new[] { required }, Now, Amsterdam, Now).Evidence);
    }

    // ---- 9. Not applicable instead of Unknown --------------------------------------------------

    [Theory]
    [InlineData("not applicable: ScriptRequirementRuleNotMet", "Not meant for this device: the app's requirement script said it does not apply here. Nothing to do.")]
    [InlineData("not applicable: MinimumOSVersionNotMet", "Not meant for this device: the app needs another Windows version. Nothing to do.")]
    [InlineData("not applicable: ProcessorArchitectureNotApplicable", "Not meant for this device: the app is for another processor architecture. Nothing to do.")]
    [InlineData("not applicable: MinimumDiskSpaceNotMet", "Not meant for this device: the app needs more free disk space than its requirement allows here. Nothing to do.")]
    [InlineData("not applicable: MinimumMemoryNotMet", "Not meant for this device: the app needs more memory than this device has. Nothing to do.")]
    [InlineData("not applicable: FileSystemRequirementRuleNotMet", "Not meant for this device: the app's file requirement is not met here. Nothing to do.")]
    [InlineData("not applicable: RegistryRequirementRuleNotMet", "Not meant for this device: the app's registry requirement is not met here. Nothing to do.")]
    [InlineData("not applicable: ContosoRuleNotMet", "Not meant for this device: one of the app's requirements is not met. Nothing to do.")]
    [InlineData("not applicable (IME status)", "Not meant for this device: one of the app's requirements is not met. Nothing to do.")]
    public void Not_applicable_says_plainly_that_the_app_is_not_meant_for_this_device(string state, string text)
    {
        var app = Evidence(verdict: "NO-ACTION");
        app.State = state;
        var f = AppDecisions.Decide(app, new[] { app }, Now, Amsterdam);
        Assert.Equal(AppCase.NotApplicable, f.Case);
        Assert.Equal(AppObservation.NotApplicable, f.Observation);
        Assert.Equal("Not applicable", AppDecisions.Word(f.Observation));
        Assert.Equal(text, f.Text);
        Assert.Equal(AppNextStep.None, f.Next);
        Assert.False(f.NeedsAttention);
        // The raw code only in View details and Copy details for IT.
        Assert.DoesNotContain("RuleNotMet", f.Text);
        Assert.Contains(state, f.Card().Support);
    }

    [Fact]
    public void A_genuinely_unknown_state_stays_Unknown()
    {
        var app = Evidence(verdict: "NO-ACTION");
        app.State = "no state recorded";
        var f = AppDecisions.Decide(app, new[] { app }, Now, Amsterdam);
        Assert.Equal(AppCase.NothingToDo, f.Case);
        Assert.Equal("Unknown", AppDecisions.Word(f.Observation));
    }

    // ---- 10. Plain post-check text ------------------------------------------------------------

    [Fact]
    public void After_opening_Company_Portal_nothing_new_is_said_plainly()
    {
        var t0 = new DateTime(2026, 11, 12, 13, 0, 0, DateTimeKind.Utc);
        var before = Diagnosis("diag0100", Evidence(verdict: "AVAILABLE", intent: "available", observed: "2026-11-12T12:00:00Z", scope: "user"));
        var finding = before.Apps.Single();
        var remedy = PostCheck.Remedy(finding, RemedyKind.CompanyPortal, t0, "S-1-12-1-1-2-3-4");
        var after = Diagnosis("diag0101", Evidence(verdict: "AVAILABLE", intent: "available", observed: "2026-11-12T12:00:00Z", scope: "user", diagnosis: "diag0101"));
        var view = PostCheck.Overlay(after, new[] { remedy }, t0.AddMinutes(5), Amsterdam);
        var shown = view.Apps.Single();
        Assert.Equal("Nothing new from this app since you opened Company Portal (2026-11-12 14:00 (UTC+01:00)).", shown.Text);
        Assert.Equal("Choose Install there, then Check this app again.", shown.NextText);
        // The precise times stay in View details and Copy details for IT.
        Assert.Contains("recorded 2026-11-12 13:00", PostCheck.Line(shown, Amsterdam));
        Assert.Contains("recorded 2026-11-12 13:00", PostCheck.TicketLine(shown, Amsterdam));
    }

    private static AppDiagnosis Diagnosis(string id, params AppEvidence[] apps)
    {
        var d = new AppDiagnosis { DiagnosisId = id, Run = DiagnosisRunState.Finished, CapturedUtc = Now, Present = true };
        foreach (var a in apps) { a.DiagnosisId = id; d.Apps.Add(AppDecisions.Decide(a, apps, Now, Amsterdam)); }
        return d;
    }

    // ---- 4 and 5. The Company Portal link and the full app ID -------------------------------------

    [Fact]
    public void The_Company_Portal_link_is_for_apps_assigned_to_the_user_in_every_state()
    {
        foreach (var verdict in new[] { "OK", "RETRY-HELPS", "AVAILABLE", "NO-ACTION" })
        {
            var user = AppDecisions.Decide(Evidence(verdict: verdict, scope: "user"), Array.Empty<AppEvidence>(), Now, Amsterdam);
            Assert.True(AppDecisions.HasPortalPage(user), verdict);
            Assert.True(AppDecisions.ShowPortalLink(user, AppNextStep.CheckAgain), verdict);
            Assert.True(AppDecisions.ShowPortalLink(user, AppNextStep.None), verdict);
            var device = AppDecisions.Decide(Evidence(verdict: verdict, scope: "device"), Array.Empty<AppEvidence>(), Now, Amsterdam);
            Assert.False(AppDecisions.HasPortalPage(device), verdict);
        }
        // Left out only while the step button is the same action.
        var offered = AppDecisions.Decide(Evidence(verdict: "AVAILABLE", intent: "available", scope: "user"), Array.Empty<AppEvidence>(), Now, Amsterdam);
        Assert.False(AppDecisions.ShowPortalLink(offered, AppNextStep.OpenCompanyPortal));
        // Not without a GUID app ID.
        var odd = AppDecisions.Decide(Evidence(verdict: "AVAILABLE", scope: "user", id: "not-a-guid"), Array.Empty<AppEvidence>(), Now, Amsterdam);
        Assert.False(AppDecisions.HasPortalPage(odd));
    }

    [Fact]
    public void An_app_without_a_name_keeps_its_short_label_and_the_card_carries_the_full_ID()
    {
        var app = Evidence(verdict: "AVAILABLE", intent: "available", name: "", scope: "user");
        var f = AppDecisions.Decide(app, new[] { app }, Now, Amsterdam);
        Assert.Equal("App 99999999", app.Label);
        var state = AppDecisions.DetailState(f, Amsterdam);
        Assert.Contains("App ID: " + AppA, state);
        Assert.Contains("Name: not known on this device", state);
        // The link's own label, never inside the facts.
        Assert.DoesNotContain("Company Portal", state);
        Assert.Equal("Open in Company Portal to see it", AppDecisions.PortalLinkText(f));
        Assert.Contains("App ID " + AppA, f.Card().Support);
        var device = Evidence(verdict: "AVAILABLE", name: "", scope: "device");
        var onDevice = AppDecisions.Decide(device, new[] { device }, Now, Amsterdam);
        Assert.Contains("Name: not known on this device", AppDecisions.DetailState(onDevice, Amsterdam));
        Assert.False(AppDecisions.HasPortalPage(onDevice));
    }

    private static AppEvidence Evidence(string verdict, string intent = "required", string code = "", string id = AppA, string name = "Contoso Agent",
        string observed = "2026-11-12T12:00:00Z", string scope = "device", int revision = 3, string diagnosis = "diag0001") => new()
    {
        DiagnosisId = diagnosis,
        AppId = id,
        Name = name,
        Scope = scope,
        Intent = intent,
        Revision = revision,
        Verdict = verdict,
        State = "a state",
        Code = code,
        ObservedText = observed,
    };
}
