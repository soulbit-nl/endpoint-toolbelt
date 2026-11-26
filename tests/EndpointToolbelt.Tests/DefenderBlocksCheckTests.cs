using System;
using System.Collections.Generic;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.Actions;
using EndpointToolbelt.Engine.Defender;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.Rules;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// Check Defender blocks, read-only. The engine reads threat detections and attack surface
/// reduction events through a fake log, groups them per rule or threat and process, and shows the
/// caller the device's events and their own: another user's events and events without a user are
/// counted, a path in another profile (in any form) is not written, a network destination is reduced to
/// its host name, and only the values the check uses are read from an event (never a command line).
/// A passive Defender and an empty log are named. Controlled folder access and network protection
/// are read next to threat detections and attack surface reduction rules. The app turns the
/// <c>defender-check</c> record into the status, the run log line, the history and the Copy details
/// for IT line, which hold counts only. Every event here is made up for the test.
/// </summary>
public class DefenderBlocksCheckTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 16, 0, 0, DateTimeKind.Utc);
    private const string Caller = "S-1-5-21-1-2-3-1001";
    private const string OtherUser = "S-1-5-21-1-2-3-1002";
    private const string LsassRule = "9e6c4e1f-7d60-472f-ba1a-a39ef669e4b2";
    private const string PrevalenceRule = "01443614-cd74-433a-b99e-2ecdc07bfc25";

    private readonly EngineHarness _h = new();

    public DefenderBlocksCheckTests()
    {
        _h.Clock.UtcNow = Now;
        _h.DefenderEvents.Accounts[@"CONTOSO\user"] = Caller;
        _h.DefenderEvents.Accounts[@"CONTOSO\other"] = OtherUser;
        _h.DefenderEvents.Accounts[@"NT AUTHORITY\SYSTEM"] = "S-1-5-18";
        _h.Registry.Put(DefenderBlocksCheck.ProfileListKey + @"\" + Caller, "ProfileImagePath", @"C:\Users\user");
        // Microsoft Defender Antivirus is the active antivirus unless a test says otherwise.
        _h.Device.Defender.Enqueue(new DefenderInfo { Mode = "Normal", ServiceEnabled = true });
    }

    private void Rule(int id, DateTime utc, string rule, string process, string path, string user = @"NT AUTHORITY\SYSTEM")
        => _h.DefenderEvents.Events.Add(new DefenderEvent
        {
            Id = id,
            TimeCreatedUtc = utc,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID"] = rule, ["User"] = user, ["Process Name"] = process, ["Path"] = path,
                ["Target Commandline"] = "setup.exe /secret=hunter2", ["Parent Commandline"] = "cmd.exe /c secret",
            },
        });

    private void Detection(DateTime utc, string threat, string path, string user)
        => _h.DefenderEvents.Events.Add(new DefenderEvent
        {
            Id = DefenderBlocksCheck.Detection,
            TimeCreatedUtc = utc,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Threat Name"] = threat, ["Severity Name"] = "Severe", ["Path"] = path, ["Detection User"] = user, ["Process Name"] = @"C:\Windows\explorer.exe",
            },
        });

    private (int Exit, JsonNode Record) Check()
    {
        var run = _h.NewRun("def00001", nameof(ToolbeltAction.CheckDefenderBlocks));
        run.CallerSid = Caller;
        var exit = new CheckDefenderBlocksHandler().Run(run) ?? -1;
        return (exit, _h.Records("defender-check").Single());
    }

    private string AllText() => string.Join("\n", _h.Lines);

    // --- Engine ----------------------------------------------------------------------------------

    [Fact]
    public void A_log_without_these_events_says_nothing_was_found_only_for_what_was_read_with_exit_0()
    {
        _h.DefenderEvents.Oldest = Now.AddDays(-30);
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Success, exit);
        Assert.Equal("none", record.Str("verdict"));
        Assert.Equal("14", record.Str("days"));
        Assert.Equal("0", record.Str("blocks"));
        var finding = _h.Findings("DEFCHECK-NONE").Single();
        Assert.Equal("OK", finding.Str("severity"));
        Assert.Equal("No threat detection and no Defender block", finding.Str("title"));
        Assert.Contains("of the last 14 days", finding.Str("detail"));
        Assert.Contains("attack surface reduction rules, controlled folder access or network protection", finding.Str("detail"));
        Assert.Contains(DefenderBlocksCheck.NotRead, finding.Str("detail"));
    }

    [Fact]
    public void An_empty_log_is_a_warning_that_the_log_holds_nothing_never_nothing_found()
    {
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Warnings, exit);
        Assert.Equal("empty", record.Str("verdict"));
        Assert.Equal("WARNING", _h.Findings("DEFCHECK-EMPTY").Single().Str("severity"));
        Assert.Empty(_h.Findings("DEFCHECK-NONE"));
    }

    [Theory]
    [InlineData("Passive Mode", true, "passive")]
    [InlineData("EDR Block Mode", true, "passive")]
    [InlineData("Normal", false, "off")]
    public void A_defender_that_is_not_the_active_antivirus_is_named_instead_of_nothing_found(string mode, bool enabled, string state)
    {
        _h.DefenderEvents.Oldest = Now.AddDays(-30);
        _h.Device.Defender.Clear();
        _h.Device.Defender.Enqueue(new DefenderInfo { Mode = mode, ServiceEnabled = enabled });
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Success, exit);
        Assert.Equal("not-active", record.Str("verdict"));
        Assert.Equal(state, record.Str("defender"));
        Assert.Equal("INFO", _h.Findings("DEFCHECK-NOT-ACTIVE").Single().Str("severity"));
        Assert.Empty(_h.Findings("DEFCHECK-NONE"));
    }

    [Fact]
    public void An_active_defender_is_recorded_and_blocks_still_win_over_a_passive_one()
    {
        _h.DefenderEvents.Oldest = Now.AddDays(-30);
        Assert.Equal("active", Check().Record.Str("defender"));

        var passive = new EngineHarness();
        passive.Clock.UtcNow = Now;
        passive.Device.Defender.Enqueue(new DefenderInfo { Mode = "Passive Mode", ServiceEnabled = true });
        passive.DefenderEvents.Accounts[@"NT AUTHORITY\SYSTEM"] = "S-1-5-18";
        passive.DefenderEvents.Events.Add(new DefenderEvent { Id = DefenderBlocksCheck.Detection, TimeCreatedUtc = Now.AddHours(-1), Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Threat Name"] = "Virus:DOS/EICAR_Test_File", ["Detection User"] = @"NT AUTHORITY\SYSTEM" } });
        var run = passive.NewRun("def00002", nameof(ToolbeltAction.CheckDefenderBlocks));
        run.CallerSid = Caller;
        new CheckDefenderBlocksHandler().Run(run);
        Assert.Equal("detections", passive.Records("defender-check").Single().Str("verdict"));
        Assert.Single(passive.Findings("DEFCHECK-NOT-ACTIVE"));
    }

    [Fact]
    public void A_log_that_is_shorter_than_the_period_says_how_far_it_reaches()
    {
        _h.DefenderEvents.Oldest = Now.AddDays(-3);
        Check();
        var detail = _h.Findings("DEFCHECK-NONE").Single().Str("detail");
        Assert.Contains("the log reaches no further back", detail);
        Assert.DoesNotContain("of the last 14 days", detail);
    }

    [Fact]
    public void A_rule_that_blocked_many_times_is_one_finding_with_its_name_count_and_newest_time()
    {
        for (var i = 0; i < 47; i++) { Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-i - 1), LsassRule, @"C:\Program Files\Contoso\agent.exe", @"C:\Windows\System32\lsass.exe"); }
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Warnings, exit);
        Assert.Equal("blocks", record.Str("verdict"));
        Assert.Equal("47", record.Str("blocks"));
        Assert.Equal("1", record.Str("groups"));
        var finding = _h.Findings("DEFCHECK-BLOCK-1").Single();
        Assert.Equal("WARNING", finding.Str("severity"));
        Assert.Equal("Blocked by Defender: Block credential stealing from the Windows local security authority subsystem", finding.Str("title"));
        // The rule stops what the process does (reading the LSA's memory): the card names the process.
        Assert.Equal(@"agent.exe was blocked 47 times, last on [[t:2026-11-19T15:00:00Z;yyyy-MM-dd HH:mm]], for a Windows or service process. Target: C:\Windows\System32\lsass.exe.", finding.Str("detail"));
        Assert.Contains(LsassRule, finding.Str("support"));
        Assert.Contains(@"process C:\Program Files\Contoso\agent.exe", finding.Str("support"));
        Assert.Empty(_h.Findings("DEFCHECK-BLOCK-2"));
    }

    [Fact]
    public void An_audit_is_information_and_leaves_exit_0()
    {
        Rule(DefenderBlocksCheck.RuleAudit, Now.AddDays(-1), PrevalenceRule, @"C:\Windows\explorer.exe", @"C:\Tools\setup.exe", @"CONTOSO\user");
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Success, exit);
        Assert.Equal("audits", record.Str("verdict"));
        var finding = _h.Findings("DEFCHECK-AUDIT-1").Single();
        Assert.Equal("INFO", finding.Str("severity"));
        Assert.StartsWith("Audited by Defender, not blocked: Block executable files from running", finding.Str("title"));
        // The rule keeps a program from starting: the card names the program, then what started it.
        Assert.StartsWith("setup.exe would have been blocked once, on ", finding.Str("detail"));
        Assert.EndsWith(@"in your session. Started by explorer.exe. File: C:\Tools\setup.exe.", finding.Str("detail"));
        Assert.Contains("in your session", finding.Str("detail"));
    }

    [Fact]
    public void A_detection_comes_first_with_its_threat_severity_and_file()
    {
        Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-1), LsassRule, @"C:\Windows\System32\svchost.exe", @"C:\Windows\System32\lsass.exe");
        Detection(Now.AddDays(-2), "Virus:DOS/EICAR_Test_File", @"file:_C:\Users\user\Downloads\eicar.com; file:_C:\Users\user\Downloads\copy.com", @"CONTOSO\user");
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Warnings, exit);
        Assert.Equal("detections", record.Str("verdict"));
        Assert.Equal("1", record.Str("detections"));
        Assert.Equal("1", record.Str("blocks"));
        var finding = _h.Findings("DEFCHECK-DETECTION-1").Single();
        Assert.Equal("Microsoft Defender detected Virus:DOS/EICAR_Test_File", finding.Str("title"));
        Assert.Contains("Severity: Severe.", finding.Str("detail"));
        Assert.Contains(@"File: %USERPROFILE%\Downloads\eicar.com.", finding.Str("detail"));
        Assert.DoesNotContain("copy.com", finding.Str("detail"));
        Assert.Single(_h.Findings("DEFCHECK-BLOCK-2"));
    }

    [Fact]
    public void Another_user_s_events_are_counted_and_nothing_of_them_is_written()
    {
        Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-1), PrevalenceRule, @"C:\Users\other\AppData\Local\Temp\secretproject.exe", @"C:\Users\other\Documents\private.docx", @"CONTOSO\other");
        Detection(Now.AddHours(-2), "Trojan:Win32/Private", @"file:_C:\Users\other\Downloads\payslip.exe", @"CONTOSO\other");
        // An account Windows cannot resolve is never taken for the caller.
        Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-3), PrevalenceRule, @"C:\Users\gone\x.exe", @"C:\Users\gone\y.exe", @"CONTOSO\gone");
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Success, exit);
        Assert.Equal("none", record.Str("verdict"));
        Assert.Equal("3", record.Str("others"));
        Assert.Equal("3 events are not shown", _h.Findings("DEFCHECK-OTHER-USERS").Single().Str("title"));
        Assert.Equal("No threat detection and no Defender block for this device or for you", _h.Findings("DEFCHECK-NONE").Single().Str("title"));
        foreach (var never in new[] { "secretproject", "private.docx", "payslip", "Trojan:Win32/Private", @"C:\Users\other", "gone", @"CONTOSO\other" })
        {
            Assert.DoesNotContain(never, AllText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_device_event_does_not_show_a_path_in_another_user_s_profile_and_no_event_shows_a_command_line_or_account()
    {
        Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-1), PrevalenceRule, @"C:\Windows\System32\msiexec.exe", @"C:\Users\other\AppData\Local\Temp\installer.exe");
        Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-2), PrevalenceRule, @"C:\Users\user\AppData\Local\tool.exe", @"C:\Users\Public\Documents\shared.exe", @"CONTOSO\user");
        Check();
        var text = AllText();
        Assert.Contains("Target: " + DefenderBlocksCheck.OtherProfile + ".", _h.Findings("DEFCHECK-BLOCK-1").Single().Str("detail"));
        Assert.DoesNotContain("installer.exe", text);
        Assert.DoesNotContain(@"Users\other", text);
        var own = _h.Findings("DEFCHECK-BLOCK-2").Single();
        Assert.StartsWith("shared.exe was blocked once", own.Str("detail"));
        Assert.Contains(@"Started by tool.exe. File: C:\Users\Public\Documents\shared.exe.", own.Str("detail"));
        Assert.Contains(@"process %USERPROFILE%\AppData\Local\tool.exe", own.Str("support"));
        foreach (var never in new[] { "hunter2", "/secret", "cmd.exe /c", "CONTOSO", "NT AUTHORITY", Caller })
        {
            Assert.DoesNotContain(never, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_rule_without_a_known_id_is_still_counted_and_said_to_be_unknown()
    {
        Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-1), "11111111-2222-3333-4444-555555555555", @"C:\Windows\notepad.exe", "");
        Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-2), "", "", "");
        var (_, record) = Check();
        Assert.Equal("2", record.Str("blocks"));
        Assert.Equal("Blocked by Defender: a rule this version does not know (11111111-2222-3333-4444-555555555555)", _h.Findings("DEFCHECK-BLOCK-1").Single().Str("title"));
        var unnamed = _h.Findings("DEFCHECK-BLOCK-2").Single();
        Assert.Equal("Blocked by Defender: a rule the event does not name", unnamed.Str("title"));
        // A rule this version does not know only says that it fired, and for what.
        Assert.StartsWith("The rule fired once, on ", unnamed.Str("detail"));
        Assert.StartsWith("The rule fired once for notepad.exe, on ", _h.Findings("DEFCHECK-BLOCK-1").Single().Str("detail"));
        Assert.DoesNotContain("was blocked", AllText());
        // A rule ID in braces is still found; only the "ID" value is read.
        Assert.Equal(LsassRule, DefenderBlocksCheck.RuleId(new DefenderEvent { Data = new Dictionary<string, string> { ["ID"] = "{" + LsassRule.ToUpperInvariant() + "}" } }));
        Assert.Equal("", DefenderBlocksCheck.RuleId(new DefenderEvent { Data = new Dictionary<string, string> { ["Rule"] = LsassRule } }));
    }

    [Fact]
    public void More_groups_than_ten_are_counted_and_events_older_than_the_period_are_not_read()
    {
        for (var i = 0; i < 12; i++) { Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-i - 1), LsassRule, @"C:\Tools\tool" + i + ".exe", ""); }
        Rule(DefenderBlocksCheck.RuleBlock, Now.AddDays(-20), LsassRule, @"C:\Tools\old.exe", "");
        var (_, record) = Check();
        Assert.Equal("12", record.Str("blocks"));
        Assert.Equal("12", record.Str("groups"));
        Assert.Single(_h.Findings("DEFCHECK-BLOCK-10"));
        Assert.Empty(_h.Findings("DEFCHECK-BLOCK-11"));
        Assert.Equal("2 more block or detections in the log", _h.Findings("DEFCHECK-MORE").Single().Str("title"));
        Assert.DoesNotContain("old.exe", AllText());
    }

    [Fact]
    public void A_log_that_cannot_be_read_is_a_warning_never_nothing_found()
    {
        _h.DefenderEvents.Fails = new InvalidOperationException("The log is not there.");
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Warnings, exit);
        Assert.Equal("unreadable", record.Str("verdict"));
        Assert.Equal("WARNING", _h.Findings("DEFCHECK-UNREADABLE").Single().Str("severity"));
        Assert.Empty(_h.Findings("DEFCHECK-NONE"));
    }

    private static ProfilePaths Profiles(string caller = @"C:\Users\user", params string[] others)
    {
        var profiles = new ProfilePaths { Caller = caller, Root = @"C:\Users" };
        profiles.Others.AddRange(others);
        return profiles;
    }

    [Fact]
    public void Paths_are_cleaned_for_the_caller()
    {
        Assert.Equal(@"%USERPROFILE%\a.exe", DefenderBlocksCheck.CleanPath(@"c:\users\USER\a.exe", Profiles()));
        Assert.Equal(DefenderBlocksCheck.OtherProfile, DefenderBlocksCheck.CleanPath(@"C:\Users\user2\a.exe", Profiles()));
        Assert.Equal(DefenderBlocksCheck.OtherProfile, DefenderBlocksCheck.CleanPath(@"C:\Users\someone\a.exe", Profiles("")));
        Assert.Equal(@"C:\Users\Default\a.exe", DefenderBlocksCheck.CleanPath(@"C:\Users\Default\a.exe", Profiles()));
        Assert.Equal(@"C:\Program Files\a.exe", DefenderBlocksCheck.CleanPath(@"C:\Program Files\a.exe" + "\r\n", Profiles()));
        Assert.Equal("", DefenderBlocksCheck.CleanPath(null, Profiles("")));
        Assert.Equal(DefenderBlocksCheck.MaxTextLength + 3, DefenderBlocksCheck.CleanPath(@"C:\" + new string('a', 600), Profiles("")).Length);
        Assert.Equal(@"C:\a.exe", DefenderBlocksCheck.FirstResource(@"file:_C:\a.exe; file:_C:\b.exe"));
        Assert.Equal(@"C:\a.zip", DefenderBlocksCheck.FirstResource(@"containerfile:_C:\a.zip->inner.exe"));
        Assert.Equal(@"C:\a.exe", DefenderBlocksCheck.FirstResource(@"webfile:_C:\a.exe|https://contoso.example/a.exe|pid:12"));
        Assert.Equal("a.exe", DefenderBlocksCheck.FileName(@"%USERPROFILE%\x\a.exe"));
    }

    [Fact]
    public void A_path_in_another_form_or_below_another_profile_anywhere_is_not_written()
    {
        var profiles = Profiles(@"C:\Users\user", @"D:\Profiles\other", @"C:\Users\other2");
        // The long-path prefixes are dropped before the profile rules.
        Assert.Equal(DefenderBlocksCheck.OtherProfile, DefenderBlocksCheck.CleanPath(@"\\?\C:\Users\other2\a.exe", profiles));
        Assert.Equal(DefenderBlocksCheck.OtherProfile, DefenderBlocksCheck.CleanPath(@"\??\C:\Users\someone\a.exe", profiles));
        Assert.Equal(@"%USERPROFILE%\Downloads\a.exe", DefenderBlocksCheck.CleanPath(@"\\?\C:\Users\user\Downloads\a.exe", profiles));
        Assert.Equal(@"\\server\share\a.exe", DefenderBlocksCheck.CleanPath(@"\\?\UNC\server\share\a.exe", profiles));
        // A device or volume path is compared below its volume, and otherwise written as its file name.
        Assert.Equal(DefenderBlocksCheck.OtherProfile, DefenderBlocksCheck.CleanPath(@"\Device\HarddiskVolume3\Users\other2\a.exe", profiles));
        Assert.Equal(DefenderBlocksCheck.OtherProfile, DefenderBlocksCheck.CleanPath(@"\Device\HarddiskVolume3\Users\someone\a.exe", profiles));
        Assert.Equal(DefenderBlocksCheck.OtherProfile, DefenderBlocksCheck.CleanPath(@"\\?\Volume{volume}\Profiles\other\a.exe", profiles));
        Assert.Equal("a.exe", DefenderBlocksCheck.CleanPath(@"\Device\HarddiskVolume3\Program Files\Contoso\a.exe", profiles));
        Assert.Equal("a.exe", DefenderBlocksCheck.CleanPath(@"\Device\HarddiskVolume3\Users\user\a.exe", profiles));
        // A profile outside the caller's profile folder, a short (8.3) name and the old profile folder name.
        Assert.Equal(DefenderBlocksCheck.OtherProfile, DefenderBlocksCheck.CleanPath(@"D:\Profiles\other\Documents\a.docx", profiles));
        Assert.Equal(DefenderBlocksCheck.OtherProfile, DefenderBlocksCheck.CleanPath(@"C:\Users\OTHER2~1\a.exe", profiles));
        Assert.Equal(DefenderBlocksCheck.OtherProfile, DefenderBlocksCheck.CleanPath(@"C:\Documents and Settings\other2\a.exe", profiles));
        Assert.Equal(@"D:\Profiles\shared.exe", DefenderBlocksCheck.CleanPath(@"D:\Profiles\shared.exe", profiles));
    }

    [Fact]
    public void Profiles_of_the_device_come_from_the_profile_list_and_a_profile_outside_the_users_folder_is_hidden()
    {
        _h.Registry.Put(DefenderBlocksCheck.ProfileListKey + @"\" + OtherUser, "ProfileImagePath", @"D:\Profiles\other");
        _h.Registry.Put(DefenderBlocksCheck.ProfileListKey + @"\S-1-5-18", "ProfileImagePath", @"%systemroot%\system32\config\systemprofile");
        Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-1), PrevalenceRule, @"C:\Windows\explorer.exe", @"D:\Profiles\other\Downloads\private.exe");
        Check();
        Assert.Contains("Target: " + DefenderBlocksCheck.OtherProfile + ".", _h.Findings("DEFCHECK-BLOCK-1").Single().Str("detail"));
        Assert.DoesNotContain("private.exe", AllText());
    }

    [Fact]
    public void An_event_that_names_no_user_is_counted_and_never_shown()
    {
        _h.DefenderEvents.Oldest = Now.AddDays(-30);
        _h.DefenderEvents.Events.Add(new DefenderEvent
        {
            Id = DefenderBlocksCheck.RuleBlock,
            TimeCreatedUtc = Now.AddHours(-1),
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ID"] = PrevalenceRule, ["Process Name"] = @"C:\Windows\explorer.exe", ["Path"] = @"C:\Tools\unnamed.exe" },
        });
        var (_, record) = Check();
        Assert.Equal("none", record.Str("verdict"));
        Assert.Equal("1", record.Str("others"));
        Assert.DoesNotContain("unnamed.exe", AllText());
    }

    [Fact]
    public void The_caller_is_also_known_by_the_name_windows_gives_their_sid()
    {
        _h.DefenderEvents.ResolveNames = true;
        _h.DefenderEvents.Accounts[@"AzureAD\ContosoUser"] = Caller;
        _h.DefenderEvents.Accounts.Remove(@"CONTOSO\user");
        Rule(DefenderBlocksCheck.RuleAudit, Now.AddHours(-1), PrevalenceRule, @"C:\Windows\explorer.exe", @"C:\Tools\setup.exe", @"azuread\contosouser");
        var (_, record) = Check();
        Assert.Equal("audits", record.Str("verdict"));
        Assert.Contains("in your session", _h.Findings("DEFCHECK-AUDIT-1").Single().Str("detail"));
    }

    [Fact]
    public void A_rule_that_keeps_programs_from_starting_has_one_card_per_program()
    {
        Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-1), PrevalenceRule, @"C:\Windows\explorer.exe", @"C:\Tools\c.exe");
        Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-2), PrevalenceRule, @"C:\Windows\explorer.exe", @"C:\Tools\b.exe");
        Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-3), PrevalenceRule, @"C:\Windows\explorer.exe", @"C:\Tools\a.exe");
        Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-4), PrevalenceRule, @"C:\Windows\System32\cmd.exe", @"C:\TOOLS\a.exe");
        var (_, record) = Check();
        Assert.Equal("4", record.Str("blocks"));
        Assert.Equal("3", record.Str("groups"));
        Assert.Equal(@"c.exe was blocked once, on [[t:2026-11-19T15:00:00Z;yyyy-MM-dd HH:mm]], for a Windows or service process. Started by explorer.exe. File: C:\Tools\c.exe.",
            _h.Findings("DEFCHECK-BLOCK-1").Single().Str("detail"));
        var twice = _h.Findings("DEFCHECK-BLOCK-3").Single().Str("detail");
        Assert.StartsWith("a.exe was blocked 2 times, last on ", twice);
        Assert.Contains("Started by explorer.exe and 1 other program.", twice);
        Assert.DoesNotContain("explorer.exe was blocked", AllText());
    }

    [Fact]
    public void A_rule_that_stops_what_a_process_does_names_the_process_and_its_newest_target()
    {
        const string InjectRule = "75668c1f-73b5-4cf0-bb93-3ecf5cb7cc84";
        Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-1), InjectRule, @"C:\Program Files\Contoso\Office\word.exe", @"C:\Windows\System32\notepad.exe", @"CONTOSO\user");
        Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-2), InjectRule, @"C:\Program Files\Contoso\Office\word.exe", @"C:\Windows\System32\calc.exe", @"CONTOSO\user");
        Check();
        var detail = _h.Findings("DEFCHECK-BLOCK-1").Single().Str("detail");
        Assert.StartsWith("word.exe was blocked 2 times", detail);
        Assert.Contains(@"Newest target: C:\Windows\System32\notepad.exe (2 different).", detail);
    }

    [Fact]
    public void Controlled_folder_access_names_the_app_that_was_kept_out_of_a_protected_folder()
    {
        // Controlled folder access names the protected folder, not the file, as a real device logs it.
        _h.DefenderEvents.Events.Add(Event(DefenderBlocksCheck.FolderBlock, Now.AddHours(-1), @"C:\Users\user\AppData\Local\Contoso\scan.exe", @"C:\Users\user\Documents\Projects\", @"CONTOSO\user"));
        _h.DefenderEvents.Events.Add(Event(DefenderBlocksCheck.FolderBlock, Now.AddHours(-2), @"C:\Users\user\AppData\Local\Contoso\scan.exe", @"C:\Users\user\Documents\", @"CONTOSO\user"));
        _h.DefenderEvents.Events.Add(Event(DefenderBlocksCheck.SectorBlock, Now.AddHours(-3), @"C:\Tools\disk.exe", @"\Device\Harddisk0\DR0", @"NT AUTHORITY\SYSTEM"));
        _h.DefenderEvents.Events.Add(Event(DefenderBlocksCheck.FolderAudit, Now.AddHours(-4), @"C:\Tools\sync.exe", @"C:\Users\user\Pictures\", @"CONTOSO\user"));
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Warnings, exit);
        Assert.Equal("blocks", record.Str("verdict"));
        Assert.Equal("3", record.Str("folderBlocks"));
        Assert.Equal("0", record.Str("blocks"));
        Assert.Equal("1", record.Str("audits"));
        var app = _h.Findings("DEFCHECK-BLOCK-1").Single();
        Assert.Equal("Controlled folder access blocked scan.exe", app.Str("title"));
        Assert.StartsWith("scan.exe was not allowed to change files in a protected folder 2 times, last on ", app.Str("detail"));
        Assert.Contains(@"Newest folder: %USERPROFILE%\Documents\Projects\ (2 different).", app.Str("detail"));
        Assert.Contains("event 1123", app.Str("support"));
        Assert.Contains("can allow the app", app.Str("next"));
        Assert.StartsWith("disk.exe was not allowed to write to a disk's boot sectors once", _h.Findings("DEFCHECK-BLOCK-2").Single().Str("detail"));
        Assert.Contains("event 1127", _h.Findings("DEFCHECK-BLOCK-2").Single().Str("support"));
        var audit = _h.Findings("DEFCHECK-AUDIT-3").Single();
        Assert.Equal("INFO", audit.Str("severity"));
        Assert.StartsWith("sync.exe would not have been allowed to change files", audit.Str("detail"));
    }

    [Fact]
    public void Network_protection_names_the_host_only_and_never_another_user_s_destination()
    {
        _h.DefenderEvents.Events.Add(Event(DefenderBlocksCheck.NetworkBlock, Now.AddHours(-1), @"C:\Program Files\Contoso\browser.exe", "https://Malware.Contoso.example:8443/private/path?token=secret123", @"CONTOSO\user"));
        _h.DefenderEvents.Events.Add(Event(DefenderBlocksCheck.NetworkBlock, Now.AddHours(-2), @"C:\Program Files\Contoso\browser.exe", "malware.contoso.example", @"CONTOSO\user"));
        _h.DefenderEvents.Events.Add(Event(DefenderBlocksCheck.NetworkAudit, Now.AddHours(-3), @"C:\Windows\System32\svchost.exe", "updates.contoso.example:443", @"NT AUTHORITY\SYSTEM"));
        _h.DefenderEvents.Events.Add(Event(DefenderBlocksCheck.NetworkBlock, Now.AddHours(-4), @"C:\Program Files\Contoso\browser.exe", "https://private-site.contoso.example/", @"CONTOSO\other"));
        var (_, record) = Check();
        Assert.Equal("2", record.Str("networkBlocks"));
        Assert.Equal("1", record.Str("others"));
        var block = _h.Findings("DEFCHECK-BLOCK-1").Single();
        Assert.Equal("Network protection blocked a connection to malware.contoso.example", block.Str("title"));
        Assert.StartsWith("browser.exe could not connect to malware.contoso.example 2 times", block.Str("detail"));
        Assert.Contains("SmartScreen", block.Str("support"));
        Assert.Equal("Network protection audited, not blocked: a connection to updates.contoso.example", _h.Findings("DEFCHECK-AUDIT-2").Single().Str("title"));
        foreach (var never in new[] { "token", "secret123", "/private", "8443", "private-site" })
        {
            Assert.DoesNotContain(never, AllText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_detection_says_what_defender_did_from_its_action_event_which_is_never_counted_on_its_own()
    {
        void Add(int id, DateTime utc, string detectionId, string action, string user, string threat)
            => _h.DefenderEvents.Events.Add(new DefenderEvent
            {
                Id = id,
                TimeCreatedUtc = utc,
                Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Detection ID"] = detectionId, ["Action Name"] = action, ["Threat Name"] = threat, ["Detection User"] = user, ["Path"] = @"file:_C:\Users\user\Downloads\eicar.com",
                },
            });
        Add(DefenderBlocksCheck.Detection, Now.AddHours(-2), "detection-one", "Not Applicable", @"CONTOSO\user", "Virus:DOS/EICAR_Test_File");
        Add(DefenderBlocksCheck.ActionTaken, Now.AddHours(-1), "detection-one", "Quarantine", @"CONTOSO\user", "Virus:DOS/EICAR_Test_File");
        // Another user's detection and its action: neither is shown, the action is not counted.
        Add(DefenderBlocksCheck.Detection, Now.AddHours(-3), "detection-two", "Not Applicable", @"CONTOSO\other", "Trojan:Win32/Private");
        Add(DefenderBlocksCheck.ActionTaken, Now.AddHours(-3), "detection-two", "Remove", @"CONTOSO\other", "Trojan:Win32/Private");
        var (_, record) = Check();
        Assert.Equal("1", record.Str("detections"));
        Assert.Equal("1", record.Str("others"));
        Assert.EndsWith(" What Defender did: Quarantine.", _h.Findings("DEFCHECK-DETECTION-1").Single().Str("detail"));
        Assert.DoesNotContain("Remove", AllText());
        Assert.DoesNotContain("Private", AllText());
    }

    [Fact]
    public void A_detection_without_an_action_event_says_so()
    {
        Detection(Now.AddHours(-1), "Virus:DOS/EICAR_Test_File", @"file:_C:\Users\user\Downloads\eicar.com", @"CONTOSO\user");
        Check();
        var finding = _h.Findings("DEFCHECK-DETECTION-1").Single();
        Assert.EndsWith(" This check found no action for it.", finding.Str("detail"));
        Assert.DoesNotContain("handles", finding.Str("next"));
    }

    private void Threat(int id, DateTime utc, string detectionId, string action, string path = @"file:_C:\Users\user\Downloads\eicar.com")
        => _h.DefenderEvents.Events.Add(new DefenderEvent
        {
            Id = id,
            TimeCreatedUtc = utc,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Detection ID"] = detectionId, ["Action Name"] = action, ["Threat Name"] = "Virus:DOS/EICAR_Test_File", ["Detection User"] = @"CONTOSO\user", ["Path"] = path,
            },
        });

    [Fact]
    public void A_failed_action_is_an_error_with_a_step_for_it_support()
    {
        Threat(DefenderBlocksCheck.Detection, Now.AddHours(-2), "detection-one", "Not Applicable");
        Threat(DefenderBlocksCheck.ActionFailed, Now.AddHours(-1), "detection-one", "Quarantine");
        Check();
        var finding = _h.Findings("DEFCHECK-DETECTION-1").Single();
        Assert.Equal("ERROR", finding.Str("severity"));
        Assert.EndsWith(" What Defender did: Quarantine failed.", finding.Str("detail"));
        Assert.StartsWith("Copy details for IT now", finding.Str("next"));
        Assert.Contains("event 1118 or 1119", finding.Str("support"));
    }

    [Fact]
    public void A_group_says_the_action_of_its_newest_detection_only()
    {
        Threat(DefenderBlocksCheck.Detection, Now.AddHours(-1), "detection-new", "Not Applicable");
        Threat(DefenderBlocksCheck.Detection, Now.AddHours(-3), "detection-old", "Not Applicable");
        Threat(DefenderBlocksCheck.ActionTaken, Now.AddHours(-2), "detection-old", "Quarantine");
        Check();
        var detail = _h.Findings("DEFCHECK-DETECTION-1").Single().Str("detail");
        Assert.StartsWith("Detected 2 times", detail);
        Assert.EndsWith(" This check found no action for it.", detail);
    }

    [Fact]
    public void A_detection_that_found_a_command_line_never_shows_it()
    {
        Threat(DefenderBlocksCheck.Detection, Now.AddHours(-1), "detection-one", "Not Applicable", @"CmdLine:_C:\Windows\System32\cmd.exe /c secret-token-123; regkey:_HKLM\SOFTWARE\Contoso");
        Check();
        var detail = _h.Findings("DEFCHECK-DETECTION-1").Single().Str("detail");
        Assert.Contains("Found in a command line (not shown).", detail);
        Assert.DoesNotContain("File:", detail);
        Assert.DoesNotContain("secret-token", AllText());
        Assert.DoesNotContain("cmd.exe", AllText());
        Assert.Equal("", DefenderBlocksCheck.FirstResource(@"CmdLine:_C:\Windows\System32\cmd.exe /c x"));
        Assert.Equal(@"C:\a.exe", DefenderBlocksCheck.FirstResource(@"CmdLine:_C:\b.exe /c x; file:_C:\a.exe"));
        Assert.Equal("", DefenderBlocksCheck.ResourceWord(@"CmdLine:_C:\b.exe /c x; file:_C:\a.exe"));
        Assert.Equal("a registry value", DefenderBlocksCheck.ResourceWord(@"regkey:_HKLM\SOFTWARE\Contoso"));
        Assert.Equal("something that is not a file", DefenderBlocksCheck.ResourceWord("amsi:_text"));

        // A ";" inside a command line continues that command line; it never starts a file.
        const string WithSemicolon = @"CmdLine:_C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -c ""$k='secret-token-123'; Invoke-Thing $k""";
        Assert.Equal("", DefenderBlocksCheck.FirstResource(WithSemicolon));
        Assert.Equal("a command line", DefenderBlocksCheck.ResourceWord(WithSemicolon));
        Assert.Equal(@"C:\Tools\real.exe", DefenderBlocksCheck.FirstResource(WithSemicolon + @"; file:_C:\Tools\real.exe"));
        Assert.Equal("", DefenderBlocksCheck.ResourceWord(WithSemicolon + @"; file:_C:\Tools\real.exe"));
        // A first part without a kind is not a file either.
        Assert.Equal("", DefenderBlocksCheck.FirstResource("Invoke-Thing $k; more"));
        Assert.Equal("something that is not a file", DefenderBlocksCheck.ResourceWord("Invoke-Thing $k; more"));
    }

    [Fact]
    public void A_command_line_with_a_semicolon_never_reaches_the_card()
    {
        Threat(DefenderBlocksCheck.Detection, Now.AddHours(-1), "detection-one", "Not Applicable",
            @"CmdLine:_C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -c ""$k='secret-token-123'; Invoke-Thing $k""");
        Check();
        var detail = _h.Findings("DEFCHECK-DETECTION-1").Single().Str("detail");
        Assert.Contains("Found in a command line (not shown).", detail);
        Assert.DoesNotContain("File:", detail);
        foreach (var never in new[] { "Invoke-Thing", "secret-token", "powershell" })
        {
            Assert.DoesNotContain(never, AllText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void An_unreadable_defender_state_with_nothing_found_is_never_the_green_result()
    {
        _h.DefenderEvents.Oldest = Now.AddDays(-30);
        _h.Device.Defender.Clear();
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Success, exit);
        Assert.Equal("state-unknown", record.Str("verdict"));
        Assert.Equal("unknown", record.Str("defender"));
        Assert.Equal("INFO", _h.Findings("DEFCHECK-STATE-UNKNOWN").Single().Str("severity"));
        Assert.Empty(_h.Findings("DEFCHECK-NONE"));
    }

    [Fact]
    public void A_network_destination_is_read_from_its_destination_value_first()
    {
        _h.DefenderEvents.Events.Add(new DefenderEvent
        {
            Id = DefenderBlocksCheck.NetworkBlock,
            TimeCreatedUtc = Now.AddHours(-1),
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User"] = @"CONTOSO\user", ["Process Name"] = @"C:\Tools\browser.exe", ["Destination"] = "https://blocked.contoso.example/x", ["Path"] = @"C:\Tools\browser.exe",
            },
        });
        Check();
        Assert.Equal("Network protection blocked a connection to blocked.contoso.example", _h.Findings("DEFCHECK-BLOCK-1").Single().Str("title"));
    }

    [Fact]
    public void An_account_that_resolves_to_another_sid_is_not_the_caller_by_name()
    {
        _h.DefenderEvents.Names[Caller] = @"CONTOSO\shared";
        _h.DefenderEvents.Accounts[@"CONTOSO\shared"] = OtherUser;
        Rule(DefenderBlocksCheck.RuleBlock, Now.AddHours(-1), PrevalenceRule, @"C:\Windows\explorer.exe", @"C:\Tools\private.exe", @"CONTOSO\shared");
        var (_, record) = Check();
        Assert.Equal("1", record.Str("others"));
        Assert.DoesNotContain("private.exe", AllText());
    }

    [Theory]
    [InlineData("ftp://files.contoso.example/x", "files.contoso.example")]
    [InlineData("https://a.contoso.example/x?y=1", "a.contoso.example")]
    [InlineData("a.contoso.example:443", "a.contoso.example")]
    [InlineData("A.Contoso.Example/path", "a.contoso.example")]
    [InlineData("198.51.100.7", "198.51.100.7")]
    [InlineData("", "a destination the event does not name")]
    [InlineData("not a host <b>", "a destination in a form this check does not show")]
    public void A_destination_is_reduced_to_its_host_name(string value, string expected)
        => Assert.Equal(expected, DefenderBlocksCheck.Destination(value));

    private static DefenderEvent Event(int id, DateTime utc, string process, string path, string user)
        => new()
        {
            Id = id,
            TimeCreatedUtc = utc,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["User"] = user, ["Process Name"] = process, ["Path"] = path },
        };

    [Fact]
    public void Format_characters_are_dropped_from_names_and_paths()
    {
        Assert.Equal("evilexe.txt", DefenderBlocksCheck.Text("evil\u202Eexe.txt"));
        Assert.Equal("ab", DefenderBlocksCheck.Text("a\u200Bb\uFEFF"));
        Assert.Equal("a.exe", DefenderBlocksCheck.FileName(DefenderBlocksCheck.CleanPath(@"C:\Tools\a" + "\u200E.exe", Profiles())));
    }

    [Fact]
    public void The_event_s_named_values_are_read_from_its_xml_without_a_dtd()
    {
        var data = WindowsDefenderEventSource.DataOf("<Event><System><EventID>1121</EventID></System><EventData><Data Name=\"ID\">" + LsassRule
            + "</Data><Data Name=\"Process Name\">C:\\a &amp; b.exe</Data><Data Name=\"Path\"/><Data>no name</Data>"
            + "<Data Name=\"Target Commandline\">setup.exe /secret=hunter2</Data><Data Name=\"Parent Commandline\">cmd.exe</Data></EventData></Event>");
        Assert.Equal(LsassRule, data["id"]);
        // Only the values the check uses are read: never a command line.
        Assert.Equal(2, data.Count);
        Assert.False(data.ContainsKey("Target Commandline"));
        Assert.All(data.Keys, k => Assert.Contains(k, WindowsDefenderEventSource.ValuesRead));
        Assert.Equal(@"C:\a & b.exe", data["Process Name"]);
        Assert.False(data.ContainsKey("Path"));
        Assert.Empty(WindowsDefenderEventSource.DataOf("<!DOCTYPE x [<!ENTITY e \"boom\">]><Event><EventData><Data Name=\"ID\">&e;</Data></EventData></Event>"));
        Assert.Empty(WindowsDefenderEventSource.DataOf("not xml"));
        Assert.Empty(WindowsDefenderEventSource.DataOf(null));
    }

    [Fact]
    public void Every_rule_of_the_table_has_a_lower_case_id_and_a_name()
    {
        Assert.Equal(19, DefenderBlocksCheck.RuleNames.Count);
        Assert.All(DefenderBlocksCheck.RuleNames, r =>
        {
            Assert.True(Guid.TryParseExact(r.Key, "D", out _));
            Assert.Equal(r.Key.ToLowerInvariant(), r.Key);
            Assert.True(r.Value.StartsWith("Block ", StringComparison.Ordinal) || r.Value.StartsWith("Use ", StringComparison.Ordinal));
        });
    }

    // --- Contract and service --------------------------------------------------------------------

    [Fact]
    public void The_action_is_value_13_read_only_shares_the_read_only_limit_and_can_be_switched_off()
    {
        Assert.Equal(13, (int)ToolbeltAction.CheckDefenderBlocks);
        var info = ActionCatalog.Get(ToolbeltAction.CheckDefenderBlocks);
        Assert.True(info.ReadOnly);
        Assert.False(info.RequiresConfirmation || info.AffectsAllSessions || info.CountsTowardRateLimit || info.ProducesFile);
        Assert.IsType<CheckDefenderBlocksHandler>(ActionHandlers.Shipped[nameof(ToolbeltAction.CheckDefenderBlocks)]);
        Assert.True(ToolPolicy.CanSwitchOff(ToolbeltAction.CheckDefenderBlocks));

        var caller = new CallerContext(Caller, 1, "CONTOSO\\user");
        var readOnly = new RateLimiter(TimeSpan.FromHours(1));
        PolicyDecision Ask(ToolbeltAction action)
            => RequestPolicy.EvaluateRunAction(new Request { Type = RequestType.RunAction, Action = action }, caller, 0, new ServiceConfig(), null, new RateLimiter(TimeSpan.FromHours(1)), readOnlyLimiter: readOnly);
        for (var i = 0; i < RequestPolicy.ReadOnlyRateLimitPerHour; i++) { Assert.True(Ask(ToolbeltAction.CheckDefenderBlocks).IsAccepted); }
        var refused = Ask(ToolbeltAction.CheckDefenderBlocks);
        Assert.Equal(ResultCode.RateLimited, refused.Result);
        Assert.StartsWith("Check Defender blocks and the other read-only checks were run too often", refused.Message);
        Assert.False(Ask(ToolbeltAction.DiagnoseApps).IsAccepted);
    }

    // --- Health facts ----------------------------------------------------------------------------

    private void HealthEvent(int id, DateTime utc, params (string Name, string Value)[] values)
        => _h.DefenderEvents.Events.Add(new DefenderEvent
        {
            Id = id,
            TimeCreatedUtc = utc,
            Data = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase),
        });

    private void DefenderStatus(DefenderInfo info)
    {
        _h.Device.Defender.Clear();
        _h.Device.Defender.Enqueue(info);
    }

    private static DefenderInfo Healthy(DateTime? quick = null, DateTime? full = null, DateTime? updated = null) => new()
    {
        Mode = "Normal", ServiceEnabled = true, RealTimeProtection = true, Antivirus = true, Antispyware = true,
        QuickScanEndUtc = quick, FullScanEndUtc = full, UpdatedUtc = updated,
    };

    [Fact]
    public void A_good_result_names_the_last_scans_and_that_protection_was_not_switched_off_in_the_period()
    {
        _h.DefenderEvents.Oldest = Now.AddDays(-30);
        DefenderStatus(Healthy(quick: Now.AddHours(-12), full: Now.AddDays(-20)));
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Success, exit);
        Assert.Equal("none", record.Str("verdict"));
        Assert.Equal(Now.AddHours(-12).ToString("yyyy-MM-ddTHH:mm:ssZ"), record.Str("quickScan"));
        Assert.Equal(Now.AddDays(-20).ToString("yyyy-MM-ddTHH:mm:ssZ"), record.Str("fullScan"));
        Assert.Equal("on", record.Str("rtp"));
        Assert.Equal("0", record.Str("protectionOff"));
        Assert.Equal("full", record.Str("healthRead"));
        var scan = _h.Findings("DEFCHECK-SCAN").Single();
        Assert.Equal("OK", scan.Str("severity"));
        Assert.StartsWith("Last quick scan: ", scan.Str("title"));
        Assert.Contains("custom scans", scan.Str("detail"));
        var notOff = _h.Findings("DEFCHECK-NOT-OFF").Single();
        Assert.Equal("OK", notOff.Str("severity"));
        Assert.Equal("Protection was not switched off in the last 14 days", notOff.Str("title"));
        Assert.EndsWith("and real-time protection is on now.", notOff.Str("detail"));
    }

    [Fact]
    public void Not_switched_off_is_said_only_for_the_time_the_log_covers()
    {
        _h.DefenderEvents.Oldest = Now.AddDays(-3);
        Rule(DefenderBlocksCheck.RuleAudit, Now.AddHours(-1), LsassRule, @"C:\Windows\System32\a.exe", "");
        DefenderStatus(Healthy());
        var (_, record) = Check();
        Assert.Equal("short", record.Str("healthRead"));
        var notOff = _h.Findings("DEFCHECK-NOT-OFF").Single();
        Assert.StartsWith("Protection was not switched off since ", notOff.Str("title"));
        Assert.DoesNotContain("14 days", notOff.Str("title"));
        Assert.Equal("No quick scan recorded", _h.Findings("DEFCHECK-SCAN").Single().Str("title"));
    }

    [Fact]
    public void Real_time_protection_switched_off_and_on_again_is_information_and_off_now_is_a_warning()
    {
        _h.DefenderEvents.Oldest = Now.AddDays(-30);
        HealthEvent(DefenderBlocksCheck.RtpDisabled, Now.AddDays(-2));
        HealthEvent(DefenderBlocksCheck.RtpEnabled, Now.AddDays(-2).AddMinutes(10));
        DefenderStatus(Healthy(quick: Now.AddHours(-1)));
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Success, exit);
        Assert.Equal("1", record.Str("protectionOff"));
        var off = _h.Findings("DEFCHECK-OFF-1").Single();
        Assert.Equal("INFO", off.Str("severity"));
        Assert.Equal("Real-time protection was switched off", off.Str("title"));
        Assert.Contains("It was switched on again on ", off.Str("detail"));
        Assert.Contains("It is on now.", off.Str("detail"));
        Assert.Contains("Event 5001 (MALWAREPROTECTION_RTP_DISABLED)", off.Str("support"));
        Assert.Empty(_h.Findings("DEFCHECK-NOT-OFF"));

        var still = new DefenderBlocksCheckTests();
        still._h.DefenderEvents.Oldest = Now.AddDays(-30);
        still.HealthEvent(DefenderBlocksCheck.AntivirusDisabled, Now.AddDays(-1));
        var info = Healthy();
        info.Antivirus = false;
        still.DefenderStatus(info);
        var (stillExit, stillRecord) = still.Check();
        Assert.Equal(EngineExitCode.Warnings, stillExit);
        Assert.Equal("true", stillRecord.Str("offNow"));
        var warning = still._h.Findings("DEFCHECK-OFF-1").Single();
        Assert.Equal("WARNING", warning.Str("severity"));
        Assert.Equal("Microsoft Defender Antivirus was switched off", warning.Str("title"));
        Assert.Contains("It is off now.", warning.Str("detail"));
    }

    [Fact]
    public void Protection_off_now_without_an_event_is_a_warning_from_Defender_s_status()
    {
        _h.DefenderEvents.Oldest = Now.AddDays(-30);
        var info = Healthy(quick: Now.AddHours(-1));
        info.RealTimeProtection = false;
        DefenderStatus(info);
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Warnings, exit);
        Assert.Equal("off", record.Str("rtp"));
        var off = _h.Findings("DEFCHECK-OFF-NOW").Single();
        Assert.Equal("WARNING", off.Str("severity"));
        Assert.Equal("Microsoft Defender says real-time protection is off now.", off.Str("detail"));
        Assert.Empty(_h.Findings("DEFCHECK-NOT-OFF"));
    }

    [Fact]
    public void A_change_tamper_protection_blocked_names_a_known_setting_only_never_another_value()
    {
        _h.DefenderEvents.Oldest = Now.AddDays(-30);
        // As a real device logged it, three times, with the registry root before the setting.
        for (var i = 0; i < 3; i++)
        {
            HealthEvent(DefenderBlocksCheck.TamperBlocked, Now.AddMinutes(-30 - i), ("Changed Type", "Blocked"), ("Value", @"HKLM\SOFTWARE\Microsoft\Windows Defender\Real-Time Protection\DisableRealtimeMonitoring = (Current)"));
        }
        // A protected setting this check does not name: its value (here a made-up exclusion path) is never shown.
        HealthEvent(DefenderBlocksCheck.TamperBlocked, Now.AddHours(-2), ("Changed Type", "Blocked"), ("Value", @"HKLM\SOFTWARE\Microsoft\Windows Defender\Exclusions\Paths\C:\Users\other\secret = 0"));
        DefenderStatus(Healthy(quick: Now.AddHours(-1)));
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Success, exit);
        Assert.Equal("4", record.Str("tamperBlocked"));
        var rtp = _h.Findings("DEFCHECK-TAMPER-1").Single();
        Assert.Equal("INFO", rtp.Str("severity"));
        Assert.Equal("Tamper protection kept real-time protection on", rtp.Str("title"));
        Assert.StartsWith("Something tried to switch off real-time protection 3 times, last on ", rtp.Str("detail"));
        Assert.Contains(@"setting Real-Time Protection\DisableRealtimeMonitoring", rtp.Str("support"));
        var other = _h.Findings("DEFCHECK-TAMPER-2").Single();
        Assert.Equal("Tamper protection blocked a change to Defender", other.Str("title"));
        Assert.Contains("a setting this check does not name", other.Str("support"));
        Assert.DoesNotContain("secret", AllText());
        Assert.DoesNotContain(@"\Users\other", AllText());
        Assert.Equal("", DefenderBlocksCheck.TamperSetting(@"Exclusions\Paths\C:\x = 0"));
        Assert.Equal(@"Real-Time Protection\DisableRealtimeMonitoring", DefenderBlocksCheck.TamperSetting(@"real-time protection\disablerealtimemonitoring = 1"));
        // The registry root in its forms: the device's own key and the policy key, with HKLM or in full.
        Assert.Equal(@"Real-Time Protection\DisableRealtimeMonitoring", DefenderBlocksCheck.TamperSetting(@"HKLM\SOFTWARE\Microsoft\Windows Defender\Real-Time Protection\DisableRealtimeMonitoring = (Current)"));
        Assert.Equal(@"Real-Time Protection\DisableBehaviorMonitoring", DefenderBlocksCheck.TamperSetting(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection\DisableBehaviorMonitoring = 1"));
        Assert.Equal("", DefenderBlocksCheck.TamperSetting(@"HKLM\SOFTWARE\Contoso\Real-Time Protection\DisableRealtimeMonitoring = 1"));
    }

    [Fact]
    public void A_failed_update_is_a_warning_until_a_later_update_succeeded()
    {
        _h.DefenderEvents.Oldest = Now.AddDays(-30);
        HealthEvent(DefenderBlocksCheck.SignatureUpdateFailed, Now.AddDays(-1), ("Error Code", "0x8024402c"), ("Update Source", @"\\server\share\updates"));
        DefenderStatus(Healthy(quick: Now.AddHours(-1), updated: Now.AddDays(-3)));
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Warnings, exit);
        Assert.Equal("1", record.Str("updateFailures"));
        Assert.Equal("true", record.Str("updateWarning"));
        var failed = _h.Findings("DEFCHECK-UPDATE-1").Single();
        Assert.Equal("WARNING", failed.Str("severity"));
        Assert.Equal("A security intelligence update failed", failed.Str("title"));
        Assert.Contains("Its last successful update was on ", failed.Str("detail"));
        Assert.Contains("error 0x8024402C", failed.Str("support"));
        Assert.DoesNotContain(@"\\server", AllText());

        var recovered = new DefenderBlocksCheckTests();
        recovered._h.DefenderEvents.Oldest = Now.AddDays(-30);
        recovered.HealthEvent(DefenderBlocksCheck.SignatureUpdateFailed, Now.AddDays(-1), ("Error Code", "not a code"));
        recovered.DefenderStatus(Healthy(quick: Now.AddHours(-1), updated: Now.AddHours(-2)));
        var (recoveredExit, recoveredRecord) = recovered.Check();
        Assert.Equal(EngineExitCode.Success, recoveredExit);
        Assert.Equal("false", recoveredRecord.Str("updateWarning"));
        var info = recovered._h.Findings("DEFCHECK-UPDATE-1").Single();
        Assert.Equal("INFO", info.Str("severity"));
        Assert.Contains("Security intelligence was updated since, on ", info.Str("detail"));
        Assert.DoesNotContain("error", info.Str("support"));
    }

    [Fact]
    public void A_failed_real_time_protection_feature_is_a_warning_with_Microsoft_s_action()
    {
        _h.DefenderEvents.Oldest = Now.AddDays(-30);
        HealthEvent(DefenderBlocksCheck.RtpFeatureFailure, Now.AddDays(-1), ("Feature Name", "On Access"), ("Error Code", "0x80004005"));
        DefenderStatus(Healthy(quick: Now.AddHours(-1)));
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Warnings, exit);
        Assert.Equal("1", record.Str("featureFailures"));
        var feature = _h.Findings("DEFCHECK-FEATURE").Single();
        Assert.Equal("WARNING", feature.Str("severity"));
        Assert.Contains("Feature: On Access.", feature.Str("detail"));
        Assert.StartsWith("Restart the device, then run a full scan", feature.Str("next"));
        Assert.Empty(_h.Findings("DEFCHECK-NOT-OFF"));
        Assert.Equal("", DefenderBlocksCheck.FeatureName(@"C:\Users\user\x.exe"));
    }

    [Fact]
    public void The_health_events_are_read_apart_from_the_blocks_and_never_use_up_their_cap()
    {
        _h.DefenderEvents.Oldest = Now.AddDays(-30);
        for (var i = 0; i < DefenderBlocksCheck.MaxEvents + 5; i++) { Rule(DefenderBlocksCheck.RuleAudit, Now.AddMinutes(-i), LsassRule, @"C:\Windows\System32\a.exe", ""); }
        HealthEvent(DefenderBlocksCheck.RtpDisabled, Now.AddDays(-5));
        DefenderStatus(Healthy(quick: Now.AddHours(-1)));
        var (_, record) = Check();
        Assert.Equal("true", record.Str("truncated"));
        Assert.Equal("1", record.Str("protectionOff"));
        Assert.DoesNotContain(5007, DefenderBlocksCheck.HealthEventIds);
        Assert.DoesNotContain(1001, DefenderBlocksCheck.HealthEventIds);
        Assert.Empty(DefenderBlocksCheck.HealthEventIds.Intersect(DefenderBlocksCheck.EventIds));
    }

    [Fact]
    public void A_passive_Defender_and_an_empty_log_have_no_health_facts()
    {
        _h.DefenderEvents.Oldest = Now.AddDays(-30);
        DefenderStatus(new DefenderInfo { Mode = "Passive Mode", ServiceEnabled = true, RealTimeProtection = false, QuickScanEndUtc = Now.AddHours(-1) });
        var (exit, record) = Check();
        Assert.Equal(EngineExitCode.Success, exit);
        Assert.Null(record.Str("healthRead"));
        Assert.Empty(_h.Findings("DEFCHECK-SCAN"));
        Assert.Empty(_h.Findings("DEFCHECK-OFF-NOW"));
    }

    [Fact]
    public void A_5013_reads_only_its_change_type_and_value_and_a_2001_never_its_update_source()
    {
        var tamper = WindowsDefenderEventSource.DataOf("<Event><EventData><Data Name=\"Product Name\">Microsoft Defender Antivirus</Data><Data Name=\"Changed Type\">Blocked</Data>"
            + "<Data Name=\"Value\">Real-Time Protection\\DisableRealtimeMonitoring = (Current)</Data></EventData></Event>");
        Assert.Equal(new[] { "Changed Type", "Value" }, tamper.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var update = WindowsDefenderEventSource.DataOf("<Event><EventData><Data Name=\"Error Code\">0x8024402c</Data><Data Name=\"Error Description\">x</Data>"
            + "<Data Name=\"Update Source\">\\\\server\\share</Data></EventData></Event>");
        Assert.Equal(new[] { "Error Code" }, update.Keys);
    }

    [Fact]
    public void A20_controlled_folder_access_names_the_protected_folder_as_a_folder()
    {
        // As a real device logged it: the folder with the profile as a token, not the written file.
        _h.DefenderEvents.Oldest = Now.AddDays(-30);
        _h.DefenderEvents.Events.Add(Event(DefenderBlocksCheck.FolderBlock, Now.AddHours(-1), @"C:\Tools\writer.exe", @"%userprofile%\OneDrive - Contoso\Documents\", @"CONTOSO\user"));
        Check();
        var card = _h.Findings("DEFCHECK-BLOCK-1").Single();
        Assert.Contains(@" Folder: %USERPROFILE%\OneDrive - Contoso\Documents\.", card.Str("detail"));
        Assert.DoesNotContain("File:", card.Str("detail"));
        Assert.Equal(@"%USERPROFILE%\Documents", DefenderBlocksCheck.CleanPath(@"%userprofile%\Documents", Profiles()));
    }

    // --- App -------------------------------------------------------------------------------------

    private static RunOutcome HealthOutcome(string verdict, int exit, string health, int detections = 0, int blocks = 0)
    {
        var outcome = new RunOutcome { Action = ToolbeltAction.CheckDefenderBlocks, RunId = "ab12cd34", Result = ResultCode.Completed, ExitCode = exit };
        outcome.Records.Add(EngineRecord.Parse("{\"type\":\"defender-check\",\"run\":\"ab12cd34\",\"ts\":\"2026-11-19T16:00:00.000Z\",\"verdict\":\"" + verdict
            + "\",\"days\":14,\"detections\":" + detections + ",\"blocks\":" + blocks + ",\"folderBlocks\":0,\"networkBlocks\":0,\"audits\":0,\"others\":0,\"groups\":0,\"truncated\":false," + health + "}"));
        return outcome;
    }

    private const string GoodHealth = "\"quickScan\":\"2026-11-19T02:34:00Z\",\"fullScan\":\"\",\"rtp\":\"on\",\"offNow\":false,\"protectionOff\":0,\"featureFailures\":0,\"tamperBlocked\":0,\"updateFailures\":0,\"updateWarning\":false,\"healthRead\":\"full\"";

    [Fact]
    public void The_status_of_a_good_result_says_the_last_quick_scan_and_that_protection_was_not_switched_off()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("CET-test", TimeSpan.FromHours(1), "CET-test", "CET-test");
        var record = DefenderCheck.Of(HealthOutcome("none", 0, GoodHealth));
        Assert.Equal(("No threat detection and no block by attack surface reduction rules, controlled folder access or network protection in the last 14 days. Last quick scan today 03:34. Protection was not switched off in the last 14 days. SmartScreen in Microsoft Edge and the firewall are not part of this check.", Tone.Success),
            DefenderCheck.Status(record, Now, zone));
        var off = DefenderCheck.Of(HealthOutcome("none", 2, GoodHealth.Replace("\"offNow\":false", "\"offNow\":true").Replace("\"rtp\":\"on\"", "\"rtp\":\"off\"")));
        var (offText, offTone) = DefenderCheck.Status(off, Now, zone);
        Assert.Contains("Protection is switched off now.", offText);
        Assert.Equal(Tone.Warning, offTone);
        var again = DefenderCheck.Of(HealthOutcome("none", 0, GoodHealth.Replace("\"protectionOff\":0", "\"protectionOff\":2").Replace("\"tamperBlocked\":0", "\"tamperBlocked\":3")));
        Assert.Contains("Protection was switched off 2 times in the last 14 days; it is on now. Tamper protection blocked 3 changes to Defender's settings.", DefenderCheck.Status(again, Now, zone).Status);
        var update = DefenderCheck.Of(HealthOutcome("blocks", 2, GoodHealth.Replace("\"updateWarning\":false", "\"updateWarning\":true").Replace("\"updateFailures\":0", "\"updateFailures\":1")));
        Assert.EndsWith("A Microsoft Defender update failed; see the details.", DefenderCheck.Status(update, Now, zone).Status);
        var shortLog = DefenderCheck.Of(HealthOutcome("none", 0, GoodHealth.Replace("\"healthRead\":\"full\"", "\"healthRead\":\"short\"")));
        Assert.DoesNotContain("not switched off", DefenderCheck.Status(shortLog, Now, zone).Status);
    }

    [Fact]
    public void With_blocks_or_detections_the_status_stays_short_and_keeps_only_a_warning_fact()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("CET-test", TimeSpan.FromHours(1), "CET-test", "CET-test");
        // Blocks with a healthy Defender: the verdict and its next step only; the scan, not switched off and
        // tamper protection are in the cards.
        var blocks = DefenderCheck.Of(HealthOutcome("blocks", 2, GoodHealth.Replace("\"tamperBlocked\":0", "\"tamperBlocked\":3"), blocks: 47));
        Assert.Equal(("Microsoft Defender blocked or detected something in the last 14 days. See the details.", Tone.Warning),
            DefenderCheck.Status(blocks, Now, zone));
        // Switched off and on again is information: in the cards only.
        var again = DefenderCheck.Of(HealthOutcome("detections", 2, GoodHealth.Replace("\"protectionOff\":0", "\"protectionOff\":2"), detections: 1));
        Assert.Equal("Microsoft Defender blocked or detected something in the last 14 days. See the details.", DefenderCheck.Status(again, Now, zone).Status);
        // A warning fact stays in the sentence: protection off now, a failed feature, an update that did not recover.
        var warnings = DefenderCheck.Of(HealthOutcome("detections", 2, GoodHealth.Replace("\"offNow\":false", "\"offNow\":true").Replace("\"featureFailures\":0", "\"featureFailures\":1")
            .Replace("\"updateWarning\":false", "\"updateWarning\":true").Replace("\"tamperBlocked\":0", "\"tamperBlocked\":3"), detections: 1));
        Assert.Equal("Microsoft Defender blocked or detected something in the last 14 days. Protection is switched off now. A real-time protection feature failed; see the details. A Microsoft Defender update failed; see the details.",
            DefenderCheck.Status(warnings, Now, zone).Status);
        // A good result keeps the facts that say "you are fine".
        Assert.Contains("Last quick scan today 03:34. Protection was not switched off in the last 14 days.", DefenderCheck.Status(DefenderCheck.Of(HealthOutcome("audits", 0, GoodHealth)), Now, zone).Status);
    }

    [Fact]
    public void The_log_line_and_the_ticket_line_carry_the_health_facts_as_device_facts()
    {
        var outcome = HealthOutcome("none", 0, GoodHealth.Replace("\"tamperBlocked\":0", "\"tamperBlocked\":3"));
        Assert.EndsWith("defender=unknown quick-scan=2026-11-19T02:34:00Z rtp=on off-now=no protection-off=0 feature-failures=0 tamper-blocked=3 update-failures=0 health-read=full",
            DefenderCheck.LogLine("ab12cd34", DefenderCheck.Of(outcome)));
        Assert.Contains(" · last quick scan 2026-11-19 02:34 UTC · no full scan recorded · real-time protection on now · switched off 0 · tamper protection blocks 3 · ", DefenderCheck.TicketLine(outcome));
        var bad = EngineRecord.Parse("{\"type\":\"defender-check\",\"run\":\"r\",\"verdict\":\"none\",\"quickScan\":\"yesterday\",\"rtp\":\"maybe\",\"protectionOff\":-1,\"healthRead\":\"all\"}").DefenderCheck;
        Assert.Equal("", bad.QuickScan);
        Assert.Equal("unknown", bad.RealTimeProtection);
        Assert.Null(bad.ProtectionOff);
        Assert.Equal("none", bad.HealthRead);
    }

    // --- App (blocks) ----------------------------------------------------------------------------

    private static RunOutcome Outcome(string verdict, int exit, int detections = 0, int blocks = 0, int audits = 0, int others = 0, int folderBlocks = 0, int networkBlocks = 0)
    {
        var outcome = new RunOutcome { Action = ToolbeltAction.CheckDefenderBlocks, RunId = "ab12cd34", Result = ResultCode.Completed, ExitCode = exit };
        outcome.Records.Add(EngineRecord.Parse("{\"type\":\"defender-check\",\"run\":\"ab12cd34\",\"ts\":\"2026-11-19T16:00:00.000Z\",\"verdict\":\"" + verdict
            + "\",\"days\":14,\"detections\":" + detections + ",\"blocks\":" + blocks + ",\"folderBlocks\":" + folderBlocks + ",\"networkBlocks\":" + networkBlocks + ",\"audits\":" + audits + ",\"others\":" + others + ",\"groups\":1,\"truncated\":false}"));
        outcome.Records.Add(EngineRecord.Parse("{\"type\":\"summary\",\"run\":\"ab12cd34\",\"exit\":" + exit + ",\"changes\":0,\"restart\":false}"));
        return outcome;
    }

    [Fact]
    public void The_status_says_what_the_log_held_in_counts()
    {
        Assert.Equal(("No threat detection and no block by attack surface reduction rules, controlled folder access or network protection in the last 14 days. SmartScreen in Microsoft Edge and the firewall are not part of this check.", Tone.Success), DefenderCheck.Status(DefenderCheck.Of(Outcome("none", 0))));
        Assert.Equal(("No threat detection and no Defender block in the last 14 days; 3 audits recorded only. SmartScreen in Microsoft Edge and the firewall are not part of this check.", Tone.Success), DefenderCheck.Status(DefenderCheck.Of(Outcome("audits", 0, audits: 3))));
        Assert.Equal(("Microsoft Defender blocked or detected something in the last 14 days. See the details.", Tone.Warning),
            DefenderCheck.Status(DefenderCheck.Of(Outcome("blocks", 2, folderBlocks: 1, networkBlocks: 2))));
        Assert.Equal(Tone.Warning, DefenderCheck.Status(DefenderCheck.Of(Outcome("empty", 2))).Tone);
        Assert.Equal(Tone.Info, DefenderCheck.Status(DefenderCheck.Of(Outcome("not-active", 0))).Tone);
        Assert.Equal(Tone.Info, DefenderCheck.Status(DefenderCheck.Of(Outcome("state-unknown", 0))).Tone);
        Assert.Contains("could not be read", DefenderCheck.Status(DefenderCheck.Of(Outcome("state-unknown", 0))).Status);
        Assert.StartsWith("Microsoft Defender Antivirus is not the active antivirus", DefenderCheck.Status(DefenderCheck.Of(Outcome("not-active", 0))).Status);
        foreach (var verdict in new[] { "none", "audits", "blocks", "detections", "empty", "not-active", "state-unknown", "unreadable" })
        {
            Assert.NotNull(DefenderCheck.ResultWords(verdict));
            Assert.DoesNotContain("blocked nothing", DefenderCheck.Status(DefenderCheck.Of(Outcome(verdict, 0))).Status);
        }
        Assert.Equal(("Microsoft Defender blocked or detected something in the last 14 days. See the details.", Tone.Warning),
            DefenderCheck.Status(DefenderCheck.Of(Outcome("blocks", 2, blocks: 47))));
        Assert.Equal(("Microsoft Defender blocked or detected something in the last 14 days. See the details.", Tone.Warning),
            DefenderCheck.Status(DefenderCheck.Of(Outcome("detections", 2, 2, 47, 1))));
        Assert.Equal(Tone.Warning, DefenderCheck.Status(DefenderCheck.Of(Outcome("unreadable", 2))).Tone);
        var view = ResultPresenter.Final(Outcome("blocks", 2, blocks: 47));
        Assert.Equal("Microsoft Defender blocked or detected something in the last 14 days. See the details.", view.Status);
        // No counts in the status: they are in the cards and the details.
        Assert.DoesNotContain("47", view.Status);
        Assert.Equal(Tone.Warning, view.Tone);
    }

    [Fact]
    public void The_log_line_the_history_and_the_ticket_line_hold_counts_only()
    {
        var outcome = Outcome("detections", 2, 2, 47, 0, 3);
        Assert.Equal("DEFENDERCHECK run=ab12cd34 context=service verdict=detections days=14 detections=2 rule-blocks=47 folder-blocks=0 network-blocks=0 audits=0 other-users=3 truncated=no defender=unknown",
            DefenderCheck.LogLine("ab12cd34", DefenderCheck.Of(outcome)));
        Assert.Contains(ResultPresenter.Final(outcome).LogLines, l => l.Contains("DEFENDERCHECK run=ab12cd34"));
        Assert.Equal("2|ab12cd34|detections", ResultPresenter.HistoryResult(outcome));
        Assert.Equal("2026-11-19 17:00 +01:00  Check Defender blocks: detections logged (2, reference ab12cd34)",
            RunWords.HistoryLine("2026-11-19 17:00:00 +01:00;CONTOSO\\user;CheckDefenderBlocks;2|ab12cd34|detections"));
        Assert.Equal("Microsoft Defender · last 14 days · detections 2 · rule blocks 47 · folder blocks 0 · network blocks 0 · audits 0 · other users' events 3 (not shown) · not read: SmartScreen in Edge, the firewall", DefenderCheck.TicketLine(outcome));
        Assert.Equal("Microsoft Defender · its operational log could not be read", DefenderCheck.TicketLine(Outcome("unreadable", 2)));
        Assert.Null(DefenderCheck.TicketLine(new RunOutcome { Action = ToolbeltAction.CheckDefenderBlocks }));
        Assert.Equal("Check Defender blocks", ActionText.Name(ToolbeltAction.CheckDefenderBlocks));
    }

    [Fact]
    public void A_record_with_anything_but_a_known_shape_is_cleaned()
    {
        var record = EngineRecord.Parse("{\"type\":\"defender-check\",\"run\":\"r\",\"verdict\":\"blocks\",\"days\":-1,\"blocks\":-5,\"others\":2}").DefenderCheck;
        Assert.Equal("blocks", record.Verdict);
        Assert.Null(record.Days);
        Assert.Null(record.Blocks);
        Assert.Equal(2, record.Others);
        Assert.Null(EngineRecord.Parse("{\"type\":\"defender-check\",\"run\":\"r\",\"verdict\":\"Blocks 47\"}").DefenderCheck);
        Assert.Equal("", EngineRecord.Parse("{\"type\":\"defender-check\",\"run\":\"r\",\"verdict\":\"none\",\"defender\":\"Passive Mode\"}").DefenderCheck.Defender);
        Assert.Equal("passive", EngineRecord.Parse("{\"type\":\"defender-check\",\"run\":\"r\",\"verdict\":\"none\",\"defender\":\"passive\"}").DefenderCheck.Defender);
        Assert.Null(EngineRecord.Parse("{\"type\":\"defender-check\",\"run\":\"r\"}").DefenderCheck);
    }

    [Fact]
    public void An_older_service_that_does_not_know_the_check_is_said_in_words()
    {
        var view = ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.CheckDefenderBlocks, Result = ResultCode.InvalidRequest, Message = "Unknown action." });
        Assert.Equal("Check Defender blocks is not available on this device yet: its Endpoint Toolbelt service is an older version.", view.Status);
        Assert.Equal(Tone.Warning, view.Tone);
    }

    [Fact]
    public void The_check_is_a_tool_in_For_IT_after_Update_Defender_definitions()
    {
        var tool = ToolCatalog.ByButton("CheckDefenderButton");
        Assert.Equal(ToolGroup.Defender, tool.Group);
        Assert.Equal(ToolbeltAction.CheckDefenderBlocks, tool.Action);
        Assert.Equal("Check blocks", tool.ButtonText);
        Assert.False(tool.Heavy);
        Assert.Equal("", tool.Changes);
        Assert.Same(tool, ToolCatalog.ByAction(ToolbeltAction.CheckDefenderBlocks));
        Assert.Equal(tool, ToolCatalog.Search("ASR").First());
        Assert.Equal(tool, ToolCatalog.Search("blocked").First());
        Assert.Equal(tool, ToolCatalog.Search("controlled folder access").First());
        Assert.Equal(tool, ToolCatalog.Search("network protection").First());
        Assert.Equal("UpdateDefenderButton", ToolCatalog.Search("Defender").First().Button);
        Assert.True(ButtonRules.NeedsService(AppButton.CheckDefender));
    }
}
