using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.Rules;
using Xunit;

namespace EndpointToolbelt.Tests;

public class ActionCatalogTests
{
    [Fact]
    public void Every_action_enum_value_is_in_the_catalog()
    {
        foreach (ToolbeltAction action in Enum.GetValues(typeof(ToolbeltAction)))
        {
            Assert.True(ActionCatalog.IsDefined(action), $"{action} has no catalog entry.");
            Assert.Equal(action, ActionCatalog.Get(action).Action);
        }
        Assert.Equal(Enum.GetValues(typeof(ToolbeltAction)).Length, ActionCatalog.All.Count);
    }

    [Fact]
    public void There_are_exactly_the_nine_button_actions_and_the_read_only_diagnosis()
    {
        var names = Enum.GetNames(typeof(ToolbeltAction)).OrderBy(n => n);
        Assert.Equal(new[] { "BackupRecoveryKey", "CheckDefenderBlocks", "CheckWindowsUpdate", "ClearAppCache", "CollectDiag", "DiagnoseApps", "FreeDiskSpace", "FullReset", "RenewIpAddress", "RepairWU", "RetryAppsAndPolicies", "SyncClock", "UpdateDefender" }, names);
        // The values a caller sends never change; the diagnosis is a new value, not a reused one.
        Assert.Equal(10, (int)ToolbeltAction.DiagnoseApps);
        // Get a new IP address is value 11.
        Assert.Equal(11, (int)ToolbeltAction.RenewIpAddress);
        // Check Windows Update is value 12.
        Assert.Equal(12, (int)ToolbeltAction.CheckWindowsUpdate);
        // Check Defender blocks is value 13.
        Assert.Equal(13, (int)ToolbeltAction.CheckDefenderBlocks);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }, ActionCatalog.All.Select(a => (int)a).OrderBy(v => v));
    }

    [Theory]
    [InlineData(ToolbeltAction.FullReset, true, true)]
    [InlineData(ToolbeltAction.RepairWU, true, true)]
    [InlineData(ToolbeltAction.ClearAppCache, true, false)]
    [InlineData(ToolbeltAction.RetryAppsAndPolicies, false, false)]
    [InlineData(ToolbeltAction.CollectDiag, false, false)]
    [InlineData(ToolbeltAction.RenewIpAddress, true, true)]
    public void Confirmation_and_session_rules(ToolbeltAction action, bool confirmation, bool allSessions)
    {
        var info = ActionCatalog.Get(action);
        Assert.Equal(confirmation, info.RequiresConfirmation);
        Assert.Equal(allSessions, info.AffectsAllSessions);
        Assert.True(info.TimeLimit > TimeSpan.Zero);
    }

    [Fact]
    public void Unknown_action_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ActionCatalog.Get((ToolbeltAction)99));
    }
}

public class ConfigLoaderTests
{
    private sealed class FakeRegistry : IRegistryReader
    {
        public Dictionary<string, object> Policy { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, object> Config { get; } = new(StringComparer.OrdinalIgnoreCase);

        public object GetValue(string subKey, string name)
        {
            var source = subKey == ProductInfo.PolicyKey ? Policy : subKey == ProductInfo.ConfigKey ? Config : null;
            return source != null && source.TryGetValue(name, out var v) ? v : null;
        }
    }

    [Fact]
    public void Defaults_without_any_values()
    {
        var c = ConfigLoader.Load(new FakeRegistry());
        Assert.Equal(1, c.FullResetCooldownHours);
        Assert.Equal(4, c.WURepairCooldownHours);
        Assert.Equal(10, c.WUDeploymentDelayDays);
        Assert.Equal(15, c.WUMinFreeGB);
        Assert.False(c.WURemoveStalePackages);
        Assert.Equal(20, c.UserRateLimitPerHour);
        Assert.True(c.AllowMachineWideOnMultiSession);
        Assert.Equal("", c.SupportText);
        Assert.Equal("", c.SupportUrl);
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public void Policy_wins_over_config_and_strings_are_parsed()
    {
        var r = new FakeRegistry();
        r.Config["FullResetCooldownHours"] = "8";
        r.Policy["FullResetCooldownHours"] = 12;
        r.Config["WURepairCooldownHours"] = "48";
        r.Config["AllowMachineWideOnMultiSession"] = "0";
        var c = ConfigLoader.Load(r);
        Assert.Equal(12, c.FullResetCooldownHours);
        Assert.Equal(48, c.WURepairCooldownHours);
        Assert.False(c.AllowMachineWideOnMultiSession);
    }

    [Theory]
    // The version check's device value, policy before the installer's UPDATECHECK; on by default.
    [InlineData(null, null, true)]
    [InlineData(null, "0", false)]
    [InlineData(null, "1", true)]
    [InlineData(0, "1", false)]
    [InlineData(1, "0", true)]
    public void UpdateCheck_is_on_unless_policy_or_installer_says_0(object policy, string installer, bool on)
    {
        var r = new FakeRegistry();
        if (policy != null) { r.Policy["UpdateCheck"] = policy; }
        if (installer != null) { r.Config["UpdateCheck"] = installer; }
        var c = ConfigLoader.Load(r);
        Assert.Equal(on, c.UpdateCheck);
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public void ShowHelperCharacter_is_no_configuration_value_any_more()
    {
        // The helper is part of the brand. A value left from an older test build is
        // not read: it changes nothing and gives no warning.
        var r = new FakeRegistry();
        r.Policy["ShowHelperCharacter"] = 0;
        r.Config["ShowHelperCharacter"] = "0";
        var c = ConfigLoader.Load(r);
        Assert.DoesNotContain(c.Warnings, w => w.Contains("ShowHelperCharacter"));
        Assert.Equal(ConfigLoader.Load(new FakeRegistry()).AllowMachineWideOnMultiSession, c.AllowMachineWideOnMultiSession);
    }

    [Fact]
    public void Invalid_policy_value_falls_back_to_default_not_to_config()
    {
        var r = new FakeRegistry();
        r.Config["UserRateLimitPerHour"] = "10";
        r.Policy["UserRateLimitPerHour"] = 0;
        var c = ConfigLoader.Load(r);
        Assert.Equal(20, c.UserRateLimitPerHour);
        Assert.Single(c.Warnings);
    }

    [Fact]
    public void A_cooldown_of_zero_switches_it_off_but_the_rate_limit_cannot()
    {
        var r = new FakeRegistry();
        r.Policy["FullResetCooldownHours"] = 0;
        r.Config["WURepairCooldownHours"] = "0";
        r.Policy["UserRateLimitPerHour"] = 0;
        var c = ConfigLoader.Load(r);
        Assert.Equal(0, c.FullResetCooldownHours);
        Assert.Equal(0, c.WURepairCooldownHours);
        Assert.Equal(20, c.UserRateLimitPerHour);
        Assert.Single(c.Warnings);
    }

    [Fact]
    public void Only_actions_that_change_the_device_count_toward_the_rate_limit()
    {
        Assert.False(ActionCatalog.Get(ToolbeltAction.CollectDiag).CountsTowardRateLimit);
        // The read-only diagnosis, the Windows Update check and the Defender check have a limit
        // of their own instead, and nothing else is read-only.
        var readOnly = new[] { ToolbeltAction.DiagnoseApps, ToolbeltAction.CheckWindowsUpdate, ToolbeltAction.CheckDefenderBlocks };
        foreach (var action in readOnly)
        {
            var info = ActionCatalog.Get(action);
            Assert.False(info.CountsTowardRateLimit);
            Assert.True(info.ReadOnly);
            Assert.False(info.RequiresConfirmation || info.AffectsAllSessions || info.ProducesFile);
            Assert.Equal(TimeSpan.FromMinutes(5), info.TimeLimit);
        }
        Assert.All(ActionCatalog.All.Where(a => !readOnly.Contains(a)), a => Assert.False(ActionCatalog.Get(a).ReadOnly));
        foreach (var action in ActionCatalog.All.Where(a => a != ToolbeltAction.CollectDiag && !readOnly.Contains(a)))
        {
            Assert.True(ActionCatalog.Get(action).CountsTowardRateLimit);
        }
    }

    [Fact]
    public void Empty_msi_string_means_not_set()
    {
        var r = new FakeRegistry();
        r.Config["WUMinFreeGB"] = "  ";
        var c = ConfigLoader.Load(r);
        Assert.Equal(15, c.WUMinFreeGB);
        Assert.Empty(c.Warnings);
    }

    [Theory]
    [InlineData("https://support.contoso.example", "https://support.contoso.example")]
    [InlineData("http://support.contoso.example", "")]
    [InlineData("javascript:alert(1)", "")]
    [InlineData("support.contoso.example", "")]
    public void Support_url_must_be_absolute_https(string value, string expected)
    {
        var r = new FakeRegistry();
        r.Config["SupportUrl"] = value;
        Assert.Equal(expected, ConfigLoader.Load(r).SupportUrl);
    }

    [Fact]
    public void Support_text_rejects_control_characters_and_long_values()
    {
        var r = new FakeRegistry();
        r.Config["SupportText"] = "Contoso\r\nInjected";
        Assert.Equal("", ConfigLoader.Load(r).SupportText);
        r.Config["SupportText"] = new string('x', ConfigLoader.MaxSupportTextLength + 1);
        Assert.Equal("", ConfigLoader.Load(r).SupportText);
        r.Config["SupportText"] = "Contoso IT Service Desk - ext. 5555";
        Assert.Equal("Contoso IT Service Desk - ext. 5555", ConfigLoader.Load(r).SupportText);
    }
}

public class RateLimiterTests
{
    [Fact]
    public void Limits_per_key_within_the_window_and_recovers_after_it()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new RateLimiter(TimeSpan.FromHours(1), () => now);
        Assert.True(limiter.TryAcquire("S-1-5-21-1", 2));
        Assert.True(limiter.TryAcquire("S-1-5-21-1", 2));
        Assert.False(limiter.TryAcquire("S-1-5-21-1", 2));
        Assert.True(limiter.TryAcquire("S-1-5-21-2", 2));
        now = now.AddMinutes(59);
        Assert.False(limiter.TryAcquire("S-1-5-21-1", 2));
        now = now.AddMinutes(1);
        Assert.True(limiter.TryAcquire("S-1-5-21-1", 2));
    }

    [Fact]
    public void A_restart_does_not_reset_the_limit_when_it_has_a_state_file()
    {
        using var t = new TestFolder();
        var state = t.Combine("actions.txt");
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var first = new RateLimiter(TimeSpan.FromHours(1), () => now, state);
        Assert.True(first.TryAcquire("S-1-5-21-1", 2));
        now = now.AddMinutes(10);
        Assert.True(first.TryAcquire("S-1-5-21-1", 2));

        // A new service process (an MSI repair restarts the service).
        var second = new RateLimiter(TimeSpan.FromHours(1), () => now, state);
        Assert.False(second.TryAcquire("S-1-5-21-1", 2));
        Assert.True(second.TryAcquire("S-1-5-21-2", 2));
        // The oldest hit leaves the window an hour after it was made, not after the restart.
        now = now.AddMinutes(50);
        Assert.True(new RateLimiter(TimeSpan.FromHours(1), () => now, state).TryAcquire("S-1-5-21-1", 2));
        Assert.False(File.Exists(state + ".tmp"));
    }

    [Fact]
    public void A_damaged_state_file_or_hits_from_a_clock_set_back_are_ignored()
    {
        using var t = new TestFolder();
        var state = t.Combine("packages.txt");
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(state, string.Join("\r\n",
            "S-1-5-21-1\tnot a number",
            "no tab here",
            "S-1-5-21-1\t" + now.AddHours(2).Ticks.ToString(CultureInfo.InvariantCulture),
            "S-1-5-21-1\t" + now.AddHours(-2).Ticks.ToString(CultureInfo.InvariantCulture),
            "S-1-5-21-1\t99999999999999999999999",
            "S-1-5-21-2\t" + now.AddMinutes(-5).Ticks.ToString(CultureInfo.InvariantCulture)));
        var limiter = new RateLimiter(TimeSpan.FromHours(1), () => now, state);
        Assert.True(limiter.TryAcquire("S-1-5-21-1", 1));
        Assert.False(limiter.TryAcquire("S-1-5-21-2", 1));
    }
}

public class RequestPolicyTests
{
    private static readonly CallerContext Caller = new("S-1-5-21-100", 2, "user");

    private static Request Run(ToolbeltAction action, bool confirmed = false, int ack = -1) =>
        new() { Type = RequestType.RunAction, Action = action, Confirmed = confirmed, ConfirmedOtherSessions = ack };

    private static RateLimiter Limiter() => new(TimeSpan.FromHours(1));

    [Fact]
    public void Rejects_other_protocol_versions()
    {
        var r = Run(ToolbeltAction.CollectDiag);
        r.ProtocolVersion = ProtocolInfo.Version + 1;
        Assert.Equal(ResultCode.ProtocolMismatch, RequestPolicy.EvaluateRunAction(r, Caller, 0, new ServiceConfig(), null, Limiter()).Result);
    }

    [Fact]
    public void Rejects_unknown_actions()
    {
        Assert.Equal(ResultCode.InvalidRequest, RequestPolicy.EvaluateRunAction(Run((ToolbeltAction)42), Caller, 0, new ServiceConfig(), null, Limiter()).Result);
    }

    [Fact]
    public void Busy_names_the_action_and_does_not_use_the_rate_limit()
    {
        var limiter = Limiter();
        var config = new ServiceConfig { UserRateLimitPerHour = 1 };
        var busy = RequestPolicy.EvaluateRunAction(Run(ToolbeltAction.CollectDiag), Caller, 0, config, ToolbeltAction.RepairWU, limiter);
        Assert.Equal(ResultCode.Busy, busy.Result);
        Assert.Contains("RepairWU", busy.Message);
        Assert.True(RequestPolicy.EvaluateRunAction(Run(ToolbeltAction.CollectDiag), Caller, 0, config, null, limiter).IsAccepted);
    }

    [Theory]
    [InlineData(ToolbeltAction.FullReset)]
    [InlineData(ToolbeltAction.RepairWU)]
    [InlineData(ToolbeltAction.ClearAppCache)]
    public void Destructive_actions_need_confirmation(ToolbeltAction action)
    {
        Assert.Equal(ResultCode.ConfirmationRequired, RequestPolicy.EvaluateRunAction(Run(action), Caller, 0, new ServiceConfig(), null, Limiter()).Result);
        Assert.True(RequestPolicy.EvaluateRunAction(Run(action, confirmed: true), Caller, 0, new ServiceConfig(), null, Limiter()).IsAccepted);
    }

    [Fact]
    public void Machine_wide_action_with_other_sessions_needs_the_acknowledged_count()
    {
        var config = new ServiceConfig();
        Assert.Equal(ResultCode.ConfirmationRequired,
            RequestPolicy.EvaluateRunAction(Run(ToolbeltAction.FullReset, true, -1), Caller, 3, config, null, Limiter()).Result);
        Assert.Equal(ResultCode.ConfirmationRequired,
            RequestPolicy.EvaluateRunAction(Run(ToolbeltAction.FullReset, true, 2), Caller, 3, config, null, Limiter()).Result);
        Assert.True(RequestPolicy.EvaluateRunAction(Run(ToolbeltAction.FullReset, true, 3), Caller, 3, config, null, Limiter()).IsAccepted);
    }

    [Fact]
    public void Organizations_can_block_machine_wide_actions_on_shared_hosts()
    {
        var config = new ServiceConfig { AllowMachineWideOnMultiSession = false };
        Assert.Equal(ResultCode.MultiSessionBlocked,
            RequestPolicy.EvaluateRunAction(Run(ToolbeltAction.RepairWU, true, 5), Caller, 1, config, null, Limiter()).Result);
        // Without other sessions the block does not apply.
        Assert.True(RequestPolicy.EvaluateRunAction(Run(ToolbeltAction.RepairWU, true), Caller, 0, config, null, Limiter()).IsAccepted);
        // Light actions are never blocked by it.
        Assert.True(RequestPolicy.EvaluateRunAction(Run(ToolbeltAction.RetryAppsAndPolicies), Caller, 4, config, null, Limiter()).IsAccepted);
    }

    [Fact]
    public void Rate_limit_applies_per_user()
    {
        var limiter = Limiter();
        var config = new ServiceConfig { UserRateLimitPerHour = 2 };
        Assert.True(RequestPolicy.EvaluateRunAction(Run(ToolbeltAction.RetryAppsAndPolicies), Caller, 0, config, null, limiter).IsAccepted);
        Assert.True(RequestPolicy.EvaluateRunAction(Run(ToolbeltAction.RetryAppsAndPolicies), Caller, 0, config, null, limiter).IsAccepted);
        Assert.Equal(ResultCode.RateLimited, RequestPolicy.EvaluateRunAction(Run(ToolbeltAction.RetryAppsAndPolicies), Caller, 0, config, null, limiter).Result);
        // A support package only reads: it is not counted and not limited.
        Assert.True(RequestPolicy.EvaluateRunAction(Run(ToolbeltAction.CollectDiag), Caller, 0, config, null, limiter).IsAccepted);
        var other = new CallerContext("S-1-5-21-200", 3, "other");
        Assert.True(RequestPolicy.EvaluateRunAction(Run(ToolbeltAction.RetryAppsAndPolicies), other, 0, config, null, limiter).IsAccepted);
    }
}
