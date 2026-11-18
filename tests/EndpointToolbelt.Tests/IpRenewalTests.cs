using System;
using System.Collections.Generic;
using System.Linq;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Engine.Actions;
using EndpointToolbelt.Platform;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.Rules;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>Get a new IP address, with a fake adapter source and a fake renewer (nothing renews for real).</summary>
public class IpRenewalTests
{
    private const string AdapterName = "Contoso Office LAN";

    internal static NetAdapter Adapter(int index = 12, AdapterKind kind = AdapterKind.Ethernet, bool physical = true, bool enabled = true, bool driver = true,
        bool media = true, bool dhcp = true, bool usable = false, bool selfAssigned = false)
        => new()
        {
            InterfaceIndex = index,
            Name = AdapterName,
            Kind = kind,
            Physical = physical,
            Enabled = enabled,
            DriverOk = driver,
            MediaConnected = media,
            Dhcp = dhcp,
            UsableIpv4 = usable,
            SelfAssignedOnly = selfAssigned,
        };

    [Fact]
    public void Only_a_connected_physical_DHCP_adapter_without_a_usable_address_needs_one()
    {
        Assert.True(AdapterRules.NeedsNewAddress(Adapter(selfAssigned: true)));
        Assert.True(AdapterRules.NeedsNewAddress(Adapter()));
        Assert.False(AdapterRules.NeedsNewAddress(Adapter(usable: true)));
        Assert.False(AdapterRules.NeedsNewAddress(Adapter(dhcp: false)));
        Assert.False(AdapterRules.NeedsNewAddress(Adapter(enabled: false)));
        Assert.False(AdapterRules.NeedsNewAddress(Adapter(media: false)));
        Assert.False(AdapterRules.NeedsNewAddress(Adapter(driver: false)));
        Assert.False(AdapterRules.NeedsNewAddress(Adapter(physical: false)));
        Assert.Equal(RenewBlock.FixedAddress, AdapterRules.Block(Adapter(dhcp: false)));
        Assert.Equal(RenewBlock.Disabled, AdapterRules.Block(Adapter(enabled: false)));
        Assert.Equal(RenewBlock.NoMedia, AdapterRules.Block(Adapter(media: false)));
        Assert.Equal(RenewBlock.NoDriver, AdapterRules.Block(Adapter(driver: false, enabled: false)));
        // A virtual adapter (VPN, Bluetooth) is never listed.
        Assert.Single(AdapterRules.Listed(new[] { Adapter(1, usable: true), Adapter(2, physical: false) }));
        // The one that needs an address comes first.
        Assert.Equal(new[] { 7, 3 }, AdapterRules.Listed(new[] { Adapter(3, usable: true), Adapter(7) }).Select(a => a.InterfaceIndex));
    }

    [Theory]
    [InlineData("169.254.12.7", false, true)]
    [InlineData("192.0.2.10", true, false)]
    [InlineData("0.0.0.0", false, false)]
    [InlineData("fe80::1", false, false)]
    public void A_self_assigned_address_is_not_usable(string address, bool usable, bool selfAssigned)
    {
        Assert.Equal(usable, AdapterRules.IsUsableIpv4(address));
        Assert.Equal(selfAssigned, AdapterRules.IsSelfAssigned(address));
    }

    [Fact]
    public void The_kind_comes_from_the_physical_medium_and_the_connection_from_the_status()
    {
        Assert.Equal(AdapterKind.WiFi, AdapterRules.KindOf(9, 0));
        Assert.Equal(AdapterKind.Ethernet, AdapterRules.KindOf(14, null));
        Assert.Equal(AdapterKind.Ethernet, AdapterRules.KindOf(0, 0));
        Assert.Equal(AdapterKind.Other, AdapterRules.KindOf(null, 9));
        Assert.True(AdapterRules.MediaFromStatus(11));
        Assert.True(AdapterRules.MediaFromStatus(2));
        Assert.False(AdapterRules.MediaFromStatus(7));
        Assert.False(AdapterRules.MediaFromStatus(0));
    }

    private static (EngineHarness H, int? Exit, JsonNode Record) Run(Action<FakeNetwork> setup, int index = 12)
    {
        var h = new EngineHarness();
        setup(h.Network);
        RenewIpAddressHandler.SettleTime = TimeSpan.FromSeconds(3);
        var run = h.NewRun("ip000001", nameof(ToolbeltAction.RenewIpAddress));
        run.InterfaceIndex = index;
        var exit = new RenewIpAddressHandler().Run(run);
        return (h, exit, h.Records("ip-renew").Single());
    }

    private static void NoNameOrAddress(EngineHarness h)
    {
        var all = string.Join("\n", h.Lines);
        Assert.DoesNotContain(AdapterName, all, StringComparison.Ordinal);
        Assert.DoesNotContain("169.254.", all.Replace("169.254.x.x", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void An_adapter_with_a_self_assigned_address_gets_a_usable_one_and_Windows_confirms_it()
    {
        var (h, exit, record) = Run(n =>
        {
            n.Adapters.Add(Adapter(selfAssigned: true));
            n.Answer = _ => RenewCall.Done;
            n.AfterRenew = i => n.Adapters[0].UsableIpv4 = true;
        });
        Assert.Equal(EngineExitCode.Success, exit);
        Assert.Equal(new[] { 12 }, h.Network.Renewed);
        Assert.Equal("got-usable-address", record.Str("result"));
        Assert.Equal("ethernet", record.Str("kind"));
        Assert.Equal("yes", record.Str("lease"));
        Assert.Single(h.Findings("IP-USABLE"));
        Assert.Contains("Check Microsoft connections", h.Findings("IP-USABLE")[0].Str("next"));
        Assert.Single(h.Records("change"));
        NoNameOrAddress(h);
    }

    [Fact]
    public void No_answer_from_the_DHCP_server_is_said_as_such()
    {
        var (h, exit, record) = Run(n =>
        {
            n.Adapters.Add(Adapter(kind: AdapterKind.WiFi, selfAssigned: true));
            n.Answer = _ => RenewCall.Done;
        });
        Assert.Equal(EngineExitCode.Failed, exit);
        Assert.Equal("no-answer", record.Str("result"));
        Assert.Equal("wi-fi", record.Str("kind"));
        Assert.Equal("no", record.Str("lease"));
        var finding = h.Findings("IP-NO-ANSWER").Single();
        Assert.Equal("Still no usable address: the DHCP server did not answer", finding.Str("title"));
        Assert.Contains("169.254.x.x", finding.Str("detail"));
        NoNameOrAddress(h);
    }

    [Fact]
    public void A_renew_that_times_out_or_fails_is_not_called_an_answer()
    {
        var (h, exit, record) = Run(n =>
        {
            n.Adapters.Add(Adapter());
            n.Answer = _ => RenewCall.TimedOut;
        });
        Assert.Equal(EngineExitCode.Failed, exit);
        Assert.Equal("no-answer", record.Str("result"));
        Assert.Equal("timeout", record.Str("renew"));
        Assert.Contains("waited", h.Findings("IP-NO-ANSWER").Single().Str("detail"));

        (h, exit, record) = Run(n =>
        {
            n.Adapters.Add(Adapter());
            n.Answer = _ => RenewCall.Failed;
        });
        Assert.Equal(EngineExitCode.Failed, exit);
        Assert.Equal("renew-failed", record.Str("result"));
        Assert.Empty(h.Records("change"));
    }

    [Fact]
    public void A_fixed_address_a_disabled_adapter_no_driver_and_no_cable_change_nothing()
    {
        foreach (var (adapter, slug, exitCode) in new[]
        {
            (Adapter(dhcp: false, usable: true), "fixed-address", EngineExitCode.NothingToRepair),
            (Adapter(enabled: false), "disabled", EngineExitCode.Warnings),
            (Adapter(driver: false), "no-driver", EngineExitCode.Warnings),
            (Adapter(media: false), "no-media", EngineExitCode.Warnings),
        })
        {
            var (h, exit, record) = Run(n => n.Adapters.Add(adapter));
            Assert.Equal(exitCode, exit);
            Assert.Equal(slug, record.Str("result"));
            Assert.Empty(h.Network.Renewed);
            Assert.Empty(h.Records("change"));
            NoNameOrAddress(h);
        }
    }

    [Fact]
    public void Only_a_physical_adapter_under_the_chosen_index_is_renewed()
    {
        var (h, exit, record) = Run(n => n.Adapters.Add(Adapter(12, physical: false)), index: 12);
        Assert.Equal(EngineExitCode.Failed, exit);
        Assert.Equal("not-found", record.Str("result"));
        Assert.Empty(h.Network.Renewed);
        (h, exit, record) = Run(n => n.Adapters.Add(Adapter(12)), index: 0);
        Assert.Equal("not-found", record.Str("result"));
        Assert.Empty(h.Network.Renewed);
    }

    private static RunOutcome Outcome(string result, string kind = "ethernet", int exit = 0, string lease = "yes")
    {
        var record = EngineRecord.Parse("{\"type\":\"ip-renew\",\"run\":\"ab12cd34\",\"ts\":\"2026-11-12T10:00:00.000Z\",\"result\":\"" + result + "\",\"kind\":\"" + kind + "\",\"lease\":\"" + lease + "\",\"renew\":\"done\"}");
        var outcome = new RunOutcome { Action = ToolbeltAction.RenewIpAddress, RunId = "ab12cd34", Result = ResultCode.Completed, ExitCode = exit };
        outcome.Records.Add(record);
        outcome.Records.Add(EngineRecord.Parse("{\"type\":\"summary\",\"run\":\"ab12cd34\",\"exit\":" + exit + ",\"changes\":1,\"restart\":false}"));
        return outcome;
    }

    [Theory]
    [InlineData("got-usable-address", 0, "The Ethernet adapter got a usable address. Check Microsoft connections tests the internet.", Tone.Success)]
    [InlineData("no-answer", 1, "Still no usable address: the DHCP server did not answer.", Tone.Error)]
    [InlineData("fixed-address", 69003, "Not offered: the Ethernet adapter uses a fixed address. Nothing was changed.", Tone.Neutral)]
    [InlineData("disabled", 2, "The Ethernet adapter is turned off. Turn it on, then try again.", Tone.Warning)]
    [InlineData("no-media", 2, "No network cable is connected to the Ethernet adapter. Plug it in, then try again.", Tone.Warning)]
    public void The_result_says_what_Windows_confirmed(string result, int exit, string status, Tone tone)
    {
        var view = ResultPresenter.Final(Outcome(result, exit: exit));
        Assert.Equal(status, view.Status);
        Assert.Equal(tone, view.Tone);
        Assert.Contains(view.LogLines, l => l.Contains("RENEW run=ab12cd34 context=service adapter-type=ethernet usable-lease=yes result=" + result + " renew=done"));
    }

    [Fact]
    public void The_history_line_carries_the_run_and_the_result_and_reads_back_in_words()
    {
        var outcome = Outcome("got-usable-address");
        Assert.Equal("0|ab12cd34|got-usable-address", ResultPresenter.HistoryResult(outcome));
        Assert.Equal("2026-11-12 11:00 +01:00  Get a new IP address: got a usable address (0, reference ab12cd34)",
            RunWords.HistoryLine("2026-11-12 11:00:00 +01:00;CONTOSO\\user;RenewIpAddress;0|ab12cd34|got-usable-address"));
        Assert.Equal("Get a new IP address", ActionText.Name(ToolbeltAction.RenewIpAddress));
    }

    [Fact]
    public void An_older_service_that_does_not_know_the_action_is_said_in_words()
    {
        var view = ResultPresenter.Final(new RunOutcome { Action = ToolbeltAction.RenewIpAddress, Result = ResultCode.InvalidRequest, Message = "Unknown action." });
        Assert.Equal("Get a new IP address is not available on this device yet: its Endpoint Toolbelt service is an older version.", view.Status);
        Assert.Equal(Tone.Warning, view.Tone);
        Assert.Contains("update", view.Cards.Single().NextStep);
    }

    [Fact]
    public void The_page_says_each_adapters_state_and_why_it_is_not_offered_without_an_address()
    {
        Assert.Equal("Ethernet · no usable address (169.254.x.x)", IpRenewal.StateLine(Adapter(selfAssigned: true)));
        Assert.Equal("Wi-Fi · has a usable address", IpRenewal.StateLine(Adapter(kind: AdapterKind.WiFi, usable: true)));
        Assert.Equal("Ethernet · fixed address", IpRenewal.StateLine(Adapter(dhcp: false)));
        Assert.Equal("", IpRenewal.NotOffered(Adapter()));
        Assert.StartsWith("Not offered: this adapter uses a fixed address", IpRenewal.NotOffered(Adapter(dhcp: false)));
        Assert.StartsWith("Not offered: this adapter is turned off", IpRenewal.NotOffered(Adapter(enabled: false)));
        Assert.Contains("drops for a moment", IpRenewal.ConfirmText(Adapter()));
    }

    [Fact]
    public void The_service_accepts_an_interface_index_only_for_Get_a_new_IP_address()
    {
        var caller = new CallerContext("S-1-5-21-1-2-3-1001", 1, "CONTOSO\\user");
        var config = new ServiceConfig();
        PolicyDecision Ask(ToolbeltAction action, int index)
            => RequestPolicy.EvaluateRunAction(new Request { Type = RequestType.RunAction, Action = action, Confirmed = true, ConfirmedOtherSessions = 0, InterfaceIndex = index },
                caller, 0, config, null, new RateLimiter(TimeSpan.FromHours(1)));
        Assert.True(Ask(ToolbeltAction.RenewIpAddress, 12).IsAccepted);
        Assert.Equal(ResultCode.InvalidRequest, Ask(ToolbeltAction.RenewIpAddress, 0).Result);
        Assert.Equal(ResultCode.InvalidRequest, Ask(ToolbeltAction.RenewIpAddress, -3).Result);
        Assert.Equal(ResultCode.InvalidRequest, Ask(ToolbeltAction.SyncClock, 12).Result);
        var info = ActionCatalog.Get(ToolbeltAction.RenewIpAddress);
        Assert.True(info.RequiresConfirmation);
        Assert.True(info.AffectsAllSessions);
        Assert.False(info.ReadOnly);
        Assert.Equal(11, (int)ToolbeltAction.RenewIpAddress);
    }
}
