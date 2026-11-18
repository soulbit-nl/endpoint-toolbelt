using System;
using System.Collections.Generic;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Protocol;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The work account row by join type (Entra joined, hybrid joined, registered, domain only, not
/// joined), each with the checks that apply to it, and a read-only Windows Hello row. The dsregcmd
/// outputs below are made up in the shape of "dsregcmd /status"; every name and ID is a placeholder.
/// </summary>
public class WorkAccountAndHelloTests
{
    private const string KeyId = "66666666-7777-8888-9999-aaaaaaaaaaaa";
    private const string DeviceId = "11111111-2222-3333-4444-555555555555";
    private const string Upn = "someone@contoso.example";

    private static readonly MachineReadout Physical = new() { VirtualMachine = false, MultiSession = false };

    /// <summary>A dsregcmd /status output with the sections a test names; values not given are left out.</summary>
    private static DsregInfo Output(string entra, string domain, string workplace, string prt = null, string ngcSet = null,
        string policy = null, string eligible = null, string notRemote = null, string preReq = null, string errorPhase = null, string clientError = null)
    {
        var lines = new List<string>
        {
            "+----------------------------------------------------------------------+",
            "| Device State                                                         |",
            "+----------------------------------------------------------------------+",
            "",
            "             AzureAdJoined : " + entra,
            "          EnterpriseJoined : NO",
            "              DomainJoined : " + domain,
        };
        if (entra == "YES")
        {
            lines.Add("                  DeviceId : " + DeviceId);
            lines.Add("                TenantName : Contoso");
        }
        lines.Add("| User State |");
        if (ngcSet != null)
        {
            lines.Add("                    NgcSet : " + ngcSet);
            if (ngcSet == "YES") { lines.Add("                  NgcKeyId : {" + KeyId + "}"); }
        }
        lines.Add("           WorkplaceJoined : " + workplace);
        if (workplace == "YES")
        {
            lines.Add("       WorkplaceTenantName : Contoso");
            lines.Add("         WorkplaceDeviceId : " + DeviceId);
        }
        if (prt != null)
        {
            lines.Add("| SSO State |");
            lines.Add("                AzureAdPrt : " + prt);
        }
        if (errorPhase != null || clientError != null)
        {
            lines.Add("| Diagnostic Data |");
            lines.Add("    Executing Account Name : CONTOSO\\someone, " + Upn);
            if (errorPhase != null) { lines.Add("               Error Phase : " + errorPhase); }
            if (clientError != null) { lines.Add("          Client ErrorCode : " + clientError); }
        }
        if (policy != null || eligible != null || notRemote != null || preReq != null)
        {
            lines.Add("| Ngc Prerequisite Check |");
            if (policy != null) { lines.Add("             PolicyEnabled : " + policy); }
            if (eligible != null) { lines.Add("            DeviceEligible : " + eligible); }
            if (notRemote != null) { lines.Add("        SessionIsNotRemote : " + notRemote); }
            if (preReq != null) { lines.Add("              PreReqResult : " + preReq); }
        }
        return DsregInfo.Parse(lines);
    }

    private static void NothingPrivate(params HealthResult[] rows)
    {
        foreach (var row in rows)
        {
            foreach (var text in new[] { row.Detail, row.Hint, row.ReportDetail ?? "", row.Support, row.HintBelow })
            {
                Assert.DoesNotContain(KeyId, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(DeviceId, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(Upn, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("someone", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("—", text);
            }
        }
    }

    // ---- The new dsregcmd fields ------------------------------------------------------------

    [Fact]
    public void Dsregcmd_gives_the_registration_tenant_Hello_state_and_the_hybrid_join_error_but_never_keeps_the_key_ID()
    {
        var info = Output("NO", "YES", "YES", ngcSet: "YES", policy: "YES", eligible: "YES", notRemote: "YES", preReq: "WillProvision", errorPhase: "join", clientError: "0x801c03f2");
        Assert.Equal("Contoso", info.WorkplaceTenantName);
        Assert.Equal("YES", info.NgcSet);
        Assert.True(info.NgcKeyPresent);
        Assert.Equal("YES", info.NgcPolicyEnabled);
        Assert.Equal("YES", info.NgcDeviceEligible);
        Assert.Equal("YES", info.NgcSessionIsNotRemote);
        Assert.Equal("WillProvision", info.NgcPreReqResult);
        Assert.Equal("join", info.JoinErrorPhase);
        Assert.Equal("0x801c03f2", info.JoinClientErrorCode);
        // Only whether a key exists is kept: no property holds the key ID.
        Assert.DoesNotContain(typeof(DsregInfo).GetProperties().Where(p => p.Name != nameof(DsregInfo.Lines) && p.PropertyType == typeof(string)),
            p => ((string)p.GetValue(info)).Contains(KeyId));
        Assert.False(Output("YES", "NO", "NO", ngcSet: "NO").NgcKeyPresent);
    }

    // ---- The work account rows by join type -------------------------------------------------

    [Fact]
    public void Entra_joined_expects_a_sign_in_token()
    {
        var dsreg = Output("YES", "NO", "NO", prt: "YES");
        var join = WorkAccountRows.Join(dsreg);
        Assert.Equal(HealthStatus.Pass, join.Status);
        Assert.Equal("Entra joined to Contoso", join.Detail);
        Assert.Equal("Microsoft Entra joined, tenant Contoso", join.ReportDetail);
        Assert.Equal(HealthStatus.Pass, WorkAccountRows.Prt(dsreg).Status);
        Assert.False(WorkAccountRows.Prt(dsreg).Info);

        var noToken = Output("YES", "NO", "NO", prt: "NO");
        var prt = WorkAccountRows.Prt(noToken);
        Assert.Equal(HealthStatus.Warn, prt.Status);
        Assert.Equal(HealthFix.RefreshSignIn, prt.Fix);
        NothingPrivate(join, prt);
    }

    [Fact]
    public void Hybrid_joined_complete_is_OK_and_expects_a_sign_in_token()
    {
        var dsreg = Output("YES", "YES", "NO", prt: "YES");
        var join = WorkAccountRows.Join(dsreg);
        Assert.Equal(HealthStatus.Pass, join.Status);
        Assert.Equal("Hybrid joined to Contoso", join.Detail);
        Assert.Equal(HealthStatus.Pass, WorkAccountRows.Prt(dsreg).Status);
        Assert.Equal(HealthStatus.Warn, WorkAccountRows.Prt(Output("YES", "YES", "NO", prt: "NO")).Status);
    }

    [Fact]
    public void Hybrid_join_incomplete_is_a_Check_with_a_step_for_IT_and_no_token_check()
    {
        var failed = Output("NO", "YES", "NO", errorPhase: "join", clientError: "0x801c03f2");
        var join = WorkAccountRows.Join(failed);
        Assert.Equal(HealthStatus.Warn, join.Status);
        Assert.Equal(HealthFix.None, join.Fix);
        Assert.Equal("Domain joined; the Microsoft Entra hybrid join did not complete", join.Detail);
        Assert.Contains("error phase join, client error 0x801c03f2", join.Support);
        Assert.Contains("Microsoft Entra Connect", join.Support);
        Assert.Equal("Contact your IT support.", HealthChecks.NextStep(join));
        Assert.EndsWith("hybrid join stopped in phase join", join.ReportDetail);
        var card = HealthChecks.Card(join);
        Assert.Equal(join.Support, card.Support);
        // The token does not apply until the join completes: OK as information, never a Check.
        var prt = WorkAccountRows.Prt(failed);
        Assert.Equal(HealthStatus.Pass, prt.Status);
        Assert.True(prt.Info);
        Assert.Equal("Not given until the hybrid join completes", prt.Detail);
        NothingPrivate(join, prt);

        // Without the pre-join diagnostics: domain joined, not in Microsoft Entra.
        var plain = WorkAccountRows.Join(Output("NO", "YES", "NO"));
        Assert.Equal(HealthStatus.Warn, plain.Status);
        Assert.Equal("Domain joined, not in Microsoft Entra", plain.Detail);
        Assert.DoesNotContain("error phase", plain.Support);
    }

    [Fact]
    public void A_registered_device_without_a_sign_in_token_is_normal()
    {
        var dsreg = Output("NO", "NO", "YES");
        var join = WorkAccountRows.Join(dsreg);
        Assert.Equal(HealthStatus.Pass, join.Status);
        Assert.Equal("Registered (personal device) to Contoso", join.Detail);
        Assert.Contains("registered", join.ReportDetail);
        var prt = WorkAccountRows.Prt(dsreg);
        Assert.Equal(HealthStatus.Pass, prt.Status);
        Assert.True(prt.Info);
        Assert.Equal(HealthFix.None, prt.Fix);
        Assert.Equal("Not used on a registered device: each app signs in with your work account", prt.Detail);
        NothingPrivate(join, prt);
    }

    [Fact]
    public void Not_joined_is_said_calmly_with_no_token_check()
    {
        var dsreg = Output("NO", "NO", "NO");
        var join = WorkAccountRows.Join(dsreg);
        Assert.Equal(HealthStatus.Warn, join.Status);
        Assert.Equal("Not joined to Microsoft Entra", join.Detail);
        foreach (var word in new[] { "error", "fail", "problem", "broken", "danger", "!" })
        {
            Assert.DoesNotContain(word, join.Detail + " " + join.HintBelow, StringComparison.OrdinalIgnoreCase);
        }
        var prt = WorkAccountRows.Prt(dsreg);
        Assert.Equal(HealthStatus.Pass, prt.Status);
        Assert.True(prt.Info);
        Assert.Equal("Not used: the device is not joined", prt.Detail);
    }

    [Fact]
    public void An_unread_join_state_is_unknown_and_never_OK()
    {
        Assert.Equal(HealthStatus.Warn, WorkAccountRows.Join(new DsregInfo()).Status);
        Assert.Equal(HealthStatus.Warn, WorkAccountRows.Prt(new DsregInfo()).Status);
        Assert.StartsWith("Unknown", WorkAccountRows.Join(new DsregInfo()).Detail);
    }

    [Fact]
    public void The_Home_row_names_the_join_type_and_adds_signed_in_only_when_a_token_applies()
    {
        List<HealthResult> Checks(DsregInfo dsreg) => new() { WorkAccountRows.Join(dsreg), WorkAccountRows.Prt(dsreg) };
        Assert.Equal("Entra joined to Contoso; signed in", HealthView.Rows(Checks(Output("YES", "NO", "NO", prt: "YES"))).Single().Detail);
        Assert.Equal("Registered (personal device) to Contoso", HealthView.Rows(Checks(Output("NO", "NO", "YES"))).Single().Detail);
    }

    // ---- Windows Hello ---------------------------------------------------------------------

    [Fact]
    public void Hello_set_up_is_OK()
    {
        var row = WorkAccountRows.Hello(Output("YES", "NO", "NO", ngcSet: "YES"), Physical, null);
        Assert.Equal(HealthStatus.Pass, row.Status);
        Assert.False(row.Info);
        Assert.Equal("Set up for you", row.Detail);
        Assert.Contains("set up for this user: yes; key: present", row.ReportDetail);
        Assert.Equal("Set up for you (required by your organization)",
            WorkAccountRows.Hello(Output("YES", "NO", "NO", ngcSet: "YES", policy: "YES"), Physical, null).Detail);
        NothingPrivate(row);
    }

    [Theory]
    [InlineData("YES", null)]
    [InlineData("NO", true)]
    [InlineData(null, true)]
    public void Hello_required_and_not_set_up_is_a_Check_with_the_users_step_and_a_line_for_IT(string policyEnabled, bool? groupPolicy)
    {
        var row = WorkAccountRows.Hello(Output("YES", "NO", "NO", ngcSet: "NO", policy: policyEnabled, eligible: "YES", notRemote: "YES", preReq: "WillProvision"), Physical, groupPolicy);
        Assert.Equal(HealthStatus.Warn, row.Status);
        Assert.Equal("Required by your organization, not set up for you", row.Detail);
        Assert.Equal(HealthFix.None, row.Fix);
        Assert.Equal("Set up a PIN: Settings > Accounts > Sign-in options > PIN (Windows Hello).", row.HintBelow);
        Assert.Equal(WorkAccountRows.HelloStep, HealthChecks.NextStep(row));
        Assert.Contains("PreReqResult WillProvision", row.Support);
        var card = HealthChecks.Card(row);
        Assert.Equal(WorkAccountRows.HelloStep, card.NextStep);
        Assert.Equal("", card.Detail);
        Assert.Equal(row.Support, card.Support);
        NothingPrivate(row);
    }

    [Fact]
    public void Hello_required_on_a_device_that_cannot_use_it_is_a_step_for_IT()
    {
        var row = WorkAccountRows.Hello(Output("YES", "NO", "NO", ngcSet: "NO", policy: "YES", eligible: "NO"), Physical, null);
        Assert.Equal(HealthStatus.Warn, row.Status);
        Assert.Equal("Required, but this device cannot use it", row.Detail);
        Assert.Equal("Contact your IT support.", HealthChecks.NextStep(row));
        Assert.Contains("DeviceEligible NO", row.Support);
    }

    [Fact]
    public void Hello_required_in_a_remote_session_is_information()
    {
        var row = WorkAccountRows.Hello(Output("YES", "NO", "NO", ngcSet: "NO", policy: "YES", eligible: "YES", notRemote: "NO"), Physical, null);
        Assert.Equal(HealthStatus.Pass, row.Status);
        Assert.True(row.Info);
        Assert.Equal("Not checked in a remote session", row.Detail);
    }

    [Theory]
    // The values of a real Azure Virtual Desktop host's dsregcmd, from an older service without the
    // machine kind (null) and from a physical PC used over Remote Desktop (also DeviceEligible NO).
    [InlineData(false)]
    [InlineData(true)]
    public void A_remote_session_comes_before_eligibility_so_Remote_Desktop_without_the_machine_kind_is_never_a_Check(bool physical)
    {
        var dsreg = Output("YES", "NO", "NO", prt: "YES", ngcSet: "NO", policy: "YES", eligible: "NO", notRemote: "NO", preReq: "WillNotProvision");
        var row = WorkAccountRows.Hello(dsreg, physical ? Physical : null, null);
        Assert.Equal(HealthStatus.Pass, row.Status);
        Assert.True(row.Info);
        Assert.Equal("Not checked in a remote session", row.Detail);
        Assert.Equal("", HealthChecks.NextStep(row));
        Assert.Empty(HealthChecks.Cards(new[] { row }));
        // With the machine kind the virtual machine rule comes first.
        Assert.Equal("Not available on this device", WorkAccountRows.Hello(dsreg, new MachineReadout { VirtualMachine = true, MultiSession = false }, null).Detail);
        // A remote session also comes before "set up": Windows does not say whether it can be used there.
        Assert.Equal("Not checked in a remote session", WorkAccountRows.Hello(Output("YES", "NO", "NO", ngcSet: "YES", notRemote: "NO"), Physical, null).Detail);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("NO", false)]
    public void Hello_not_required_and_not_set_up_is_information(string policyEnabled, bool? groupPolicy)
    {
        var row = WorkAccountRows.Hello(Output("YES", "NO", "NO", ngcSet: "NO", policy: policyEnabled), Physical, groupPolicy);
        Assert.Equal(HealthStatus.Pass, row.Status);
        Assert.True(row.Info);
        Assert.Equal("Not set up; not required", row.Detail);
        Assert.Contains("Group Policy: " + (groupPolicy == false ? "off" : "not set"), row.ReportDetail);
    }

    [Fact]
    public void Hello_on_a_virtual_machine_in_a_remote_session_says_the_remote_session_first_and_the_facts_in_words()
    {
        // The values of a remote session on a virtual machine host's dsregcmd.
        var dsreg = Output("YES", "NO", "NO", prt: "YES", ngcSet: "NO", policy: "YES", eligible: "NO", notRemote: "NO", preReq: "WillNotProvision");
        var row = WorkAccountRows.Hello(dsreg, new MachineReadout { VirtualMachine = true, MultiSession = false }, null);
        Assert.Equal("Not available on this device", row.Detail);
        Assert.StartsWith(WorkAccountRows.RemoteSessionHint + " Besides, Windows Hello for Business cannot be set up on a virtual machine", row.Hint, StringComparison.Ordinal);
        Assert.Equal("Not available on this device (set up for this user: no; key: none; required by policy: yes; Group Policy: not set; device meets the requirements: no; remote session: yes; prerequisite check: WillNotProvision; virtual machine)",
            row.ReportDetail);
        Assert.DoesNotContain("SessionIsNotRemote", row.ReportDetail, StringComparison.Ordinal);

        // At the device itself the machine kind is the only reason.
        var local = WorkAccountRows.Hello(Output("YES", "NO", "NO", ngcSet: "NO", notRemote: "YES"), new MachineReadout { VirtualMachine = true, MultiSession = false }, null);
        Assert.StartsWith("Windows Hello for Business cannot be set up on a virtual machine", local.Hint, StringComparison.Ordinal);
        Assert.Contains("remote session: no", local.ReportDetail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, "virtual machine")]
    [InlineData(false, true, "multi-session host")]
    [InlineData(true, true, "multi-session host")]
    public void Hello_on_a_virtual_machine_or_a_multi_session_host_is_not_available_even_when_required(bool vm, bool multi, string kind)
    {
        var row = WorkAccountRows.Hello(Output("YES", "NO", "NO", ngcSet: "NO", policy: "YES"), new MachineReadout { VirtualMachine = vm, MultiSession = multi }, true);
        Assert.Equal(HealthStatus.Pass, row.Status);
        Assert.True(row.Info);
        Assert.Equal("Not available on this device", row.Detail);
        Assert.Contains(kind, row.ReportDetail);
        Assert.Contains("a " + kind, row.Hint);
    }

    [Fact]
    public void Hello_unread_is_information_and_says_so()
    {
        var row = WorkAccountRows.Hello(new DsregInfo(), Physical, null);
        Assert.Equal(HealthStatus.Pass, row.Status);
        Assert.True(row.Info);
        Assert.StartsWith("Unknown", row.Detail);
        // An unknown machine kind does not count as a virtual machine.
        Assert.Equal("Set up for you", WorkAccountRows.Hello(Output("YES", "NO", "NO", ngcSet: "YES"), null, null).Detail);
    }

    // ---- In the health checks, on Home and in the report ------------------------------------

    private sealed class Source : IHealthSource, IHelloPolicySource
    {
        public DsregInfo Info { get; set; }
        public bool? Policy { get; set; }
        public bool PolicyThrows { get; set; }
        public DateTime UtcNow { get; } = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        public string ImeServiceStatus() => "Running";
        public DsregInfo Dsreg() => Info;
        public DateTime? MdmCertificateExpiryUtc() => UtcNow.AddDays(400);
        public bool CanReach(string host, int port, int timeoutMs) => true;
        public double FreeSystemDriveGB() => 50;
        public bool ServicingRestartPending() => false;
        public bool? WindowsHelloPolicy() => PolicyThrows ? throw new UnauthorizedAccessException() : Policy;
    }

    private static StatusInfo Status(MachineReadout machine) => new() { Enrolled = true, LastSyncUtc = "2026-11-13T11:00:00Z", Readouts = new DeviceReadouts { Machine = machine } };

    [Fact]
    public void The_health_checks_read_the_Group_Policy_and_the_machine_kind_for_the_Hello_row()
    {
        var source = new Source { Info = Output("YES", "NO", "NO", prt: "YES", ngcSet: "NO"), Policy = true };
        var hello = HealthChecks.Run(source, Status(Physical), out _).Single(r => r.Name == HealthChecks.WindowsHello);
        Assert.Equal(HealthStatus.Warn, hello.Status);
        Assert.Contains("Group Policy: on", hello.ReportDetail);

        var onVm = HealthChecks.Run(source, Status(new MachineReadout { VirtualMachine = true, MultiSession = false }), out _).Single(r => r.Name == HealthChecks.WindowsHello);
        Assert.Equal("Not available on this device", onVm.Detail);

        // A policy that cannot be read is not set; a dsregcmd that throws is unread.
        source.PolicyThrows = true;
        Assert.Contains("Group Policy: not set", HealthChecks.Run(source, Status(Physical), out _).Single(r => r.Name == HealthChecks.WindowsHello).ReportDetail);
    }

    [Fact]
    public void Home_shows_Windows_Hello_as_its_own_row_after_the_work_account_and_the_report_uses_the_technical_names()
    {
        var source = new Source { Info = Output("YES", "NO", "NO", prt: "YES", ngcSet: "YES") };
        var results = HealthChecks.Run(source, Status(Physical), out var dsreg);
        var rows = HealthView.Rows(results);
        var names = rows.Select(r => r.Name).ToList();
        Assert.Equal(names.IndexOf(HealthView.WorkAccount) + 1, names.IndexOf(HealthChecks.WindowsHello));
        Assert.Equal(10, rows.Count);
        var report = DeviceReport.Build(new DeviceFacts(), dsreg, results, Array.Empty<string>(), new DateTimeOffset(2026, 11, 13, 13, 0, 0, TimeSpan.FromHours(1)), "1.0.0");
        Assert.Contains("[OK     ] Join type (dsregcmd)   Microsoft Entra joined, tenant Contoso", report);
        Assert.Contains("[OK     ] Windows Hello for Business (NGC) Set up for you (set up for this user: yes; key: present", report);
        Assert.DoesNotContain(KeyId, report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_sign_in_path_keeps_its_three_rows_because_a_fourth_does_not_fit_the_default_size()
    {
        // "I can't sign in" with the Windows Hello row needs 513 px where 490 px are free at 540 x 640;
        // the row stays on Home (Show all) and in the report.
        var path = ProblemPaths.Get(Problem.CantSignIn);
        Assert.DoesNotContain(HealthChecks.WindowsHello, path.Rows);
        Assert.Contains(HealthView.WorkAccount, path.Rows);
    }

    // ---- The calmer Home: plain words on the first screen, the fit at 100, 125 and 150 % -----

    /// <summary>The ten rows' checks, all OK but the work account and Windows Hello, as the work account check reads them.</summary>
    private static List<HealthResult> HealthWith(DsregInfo dsreg, MachineReadout machine)
    {
        var health = CompactLayoutTests.Health();
        health[health.FindIndex(c => c.Name == HealthChecks.EntraJoined)] = WorkAccountRows.Join(dsreg);
        health[health.FindIndex(c => c.Name == HealthChecks.Prt)] = WorkAccountRows.Prt(dsreg);
        health[health.FindIndex(c => c.Name == HealthChecks.WindowsHello)] = WorkAccountRows.Hello(dsreg, machine, null);
        return health;
    }

    /// <summary>The states that put the work account or Windows Hello on Home's status.</summary>
    private static DsregInfo State(string state) => state switch
    {
        "not joined" => Output("NO", "NO", "NO"),
        "hybrid join incomplete" => Output("NO", "YES", "NO", errorPhase: "join", clientError: "0x801c03f2"),
        "join unread" => new DsregInfo(),
        "sign-in token missing" => Output("YES", "NO", "NO", prt: "NO", ngcSet: "YES"),
        "Hello required, not set up" => Output("YES", "NO", "NO", prt: "YES", ngcSet: "NO", policy: "YES", eligible: "YES", notRemote: "YES"),
        "Hello required, cannot use it" => Output("YES", "NO", "NO", prt: "YES", ngcSet: "NO", policy: "YES", eligible: "NO", notRemote: "YES"),
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    [Theory]
    [InlineData("not joined", "This PC isn't connected to a work account", "If your organization manages this PC, your IT support can connect it.")]
    [InlineData("hybrid join incomplete", "This PC isn't fully connected to your work account", "Your IT support needs to finish setting it up.")]
    [InlineData("join unread", "Your work account needs a check", null)]
    [InlineData("sign-in token missing", "Your work account needs a check", "Teams, Outlook and other work apps may keep asking you to sign in.")]
    [InlineData("Hello required, not set up", "Set up a PIN for Windows Hello", "Your organization requires it: Settings > Accounts > Sign-in options > PIN.")]
    [InlineData("Hello required, cannot use it", "Windows Hello can't be used on this PC", "Your organization requires it, so your IT support needs to look at this PC.")]
    public void Homes_status_says_the_join_type_and_Windows_Hello_in_plain_words(string state, string title, string line)
    {
        var home = HomeSummary.Build(HealthView.Rows(HealthWith(State(state), Physical)), null, serviceMissing: false);
        Assert.Equal(title, HomeSummary.PlainTitle(home));
        if (line != null) { Assert.Equal(line, HomeSummary.PlainText(home)); }
        var first = HomeSummary.PlainTitle(home) + " " + HomeSummary.PlainText(home);
        // No technical word on the first screen, and none of the work account's own: they stay in the rows' details and For IT support.
        Assert.False(HomeSummary.HasTechnicalWords(first), first);
        foreach (var word in new[] { "NGC", "Ngc", "dsregcmd", "PRT", "Entra", "hybrid", "TPM" }) { Assert.DoesNotContain(word, first, StringComparison.Ordinal); }
        // A missing sign-in token has a fix here (Refresh work account sign-in); the join type and Windows Hello are the user's own step or IT's.
        if (state != "join unread") { Assert.Equal(state == "sign-in token missing" ? HomeStep.HealthFix : HomeStep.None, home.Step); }
        // The user's texts below Show all name no tool either.
        foreach (var row in HealthView.Rows(HealthWith(State(state), Physical)).Where(r => r.Name is HealthView.WorkAccount or HealthChecks.WindowsHello))
        {
            Assert.DoesNotContain("dsregcmd", row.Hint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Home_fits_at_100_125_and_150_percent_in_the_worst_work_account_states_with_a_two_line_support_contact()
    {
        MainWindowPagesTests.OnSta(() =>
        {
            var window = MainWindowPagesTests.NewWindow();
            var status = new StatusInfo
            {
                SupportText = "Contoso IT Service Desk, Monday to Friday 08:00 to 18:00, ext. 5555, or open a request in the Contoso service portal any time",
                SupportUrl = "https://support.contoso.example/",
            };
            foreach (var state in new[] { "hybrid join incomplete", "Hello required, cannot use it", "Hello required, not set up", "not joined" })
            {
                var dsreg = State(state);
                MainWindowPagesTests.Invoke(window, "ApplyHealth", status, false, HealthWith(dsreg, Physical), dsreg, false);
                MainWindowPagesTests.Call(window, "ShowPage", MainWindowPagesTests.PageValue("Home"), false);
                var title = MainWindowPagesTests.Get<System.Windows.Controls.TextBlock>(window, "MainFindingTitle").Text;
                Assert.False(HomeSummary.HasTechnicalWords(title), state + ": " + title);
                // The count only; the rows (with their technical details) after Show all.
                Assert.Equal(System.Windows.Visibility.Collapsed, MainWindowPagesTests.Get<System.Windows.Controls.ItemsControl>(window, "HealthList").Visibility);
                Assert.Equal("Show all 10", MainWindowPagesTests.Get<System.Windows.Controls.Button>(window, "HealthToggle").Content);
                CompactLayoutTests.AssertFits(window, "Home, " + state);
            }
            // The Sandbox's state with the work account and Windows Hello rows (not joined, Windows Hello not available on a virtual machine).
            MainWindowPagesTests.Invoke(window, "ApplyHealth", status, false, CompactLayoutTests.SandboxHealth(), new DsregInfo(), false);
            CompactLayoutTests.AssertFits(window, "Home in the Sandbox's state");
        });
    }
}
