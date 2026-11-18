using System;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.App.Core;

/// <summary>The engine's <c>ip-renew</c> record: result, adapter kind, usable lease, how the renew call ended.</summary>
[DataContract]
public sealed class IpRenewRecord
{
    [DataMember(Name = "result")] public string Result { get; set; } = "";
    [DataMember(Name = "kind")] public string Kind { get; set; } = "";
    [DataMember(Name = "lease")] public string Lease { get; set; } = "";
    [DataMember(Name = "renew")] public string Renew { get; set; } = "";

    /// <summary>Only short tokens (lower-case letters, digits and dashes) are kept; anything else becomes empty.</summary>
    public static IpRenewRecord Clean(IpRenewRecord record)
    {
        if (record == null) { return null; }
        record.Result = Token(record.Result);
        record.Kind = Token(record.Kind);
        record.Lease = Token(record.Lease);
        record.Renew = Token(record.Renew);
        return record.Result.Length == 0 ? null : record;
    }

    private static string Token(string text)
        => !string.IsNullOrEmpty(text) && text.Length <= 40 && text.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-') ? text : "";
}

/// <summary>
/// Get a new IP address: the adapter page's lines, the result in words, the run log line and
/// the history. Nothing here names an adapter's connection name, an address or a Wi-Fi network:
/// only its kind and whether it has a usable lease.
/// </summary>
public static class IpRenewal
{
    public const string Name = "Get a new IP address";

    /// <summary>The history's action name (the service action's name, as every service run).</summary>
    public const string HistoryName = nameof(ToolbeltAction.RenewIpAddress);

    /// <summary>The adapter's state on the page (one short line; no address).</summary>
    public static string StateLine(NetAdapter adapter)
    {
        if (adapter == null) { return ""; }
        var kind = AdapterRules.KindWords(adapter.Kind);
        return AdapterRules.Block(adapter) switch
        {
            RenewBlock.NoDriver => kind + " · no working driver",
            RenewBlock.Disabled => kind + " · turned off",
            RenewBlock.NoMedia => kind + (adapter.Kind == AdapterKind.WiFi ? " · not connected to a network" : " · no cable connected"),
            RenewBlock.FixedAddress => kind + " · fixed address",
            RenewBlock.NotPhysical => kind,
            _ => kind + (adapter.UsableIpv4 ? " · has a usable address" : adapter.SelfAssignedOnly ? " · no usable address (169.254.x.x)" : " · no usable address"),
        };
    }

    /// <summary>Why the chosen adapter is not offered a new address, with its next step; empty when it is offered.</summary>
    public static string NotOffered(NetAdapter adapter)
    {
        if (adapter == null) { return ""; }
        return AdapterRules.Block(adapter) switch
        {
            RenewBlock.NoDriver => "Not offered: this adapter has no working driver. Contact your IT support.",
            RenewBlock.Disabled => "Not offered: this adapter is turned off. Turn it on in Settings > Network & internet > Advanced network settings.",
            RenewBlock.NoMedia => adapter.Kind == AdapterKind.WiFi ? "Not offered: this adapter is not connected to Wi-Fi. Connect to Wi-Fi first." : "Not offered: no network cable is connected. Plug in the cable first.",
            RenewBlock.FixedAddress => "Not offered: this adapter uses a fixed address, so there is no lease to renew. If the address seems wrong, contact your IT support.",
            RenewBlock.NotPhysical => "Not offered: this is not a physical network adapter.",
            _ => "",
        };
    }

    /// <summary>The short confirmation before the renew: the connection on that adapter drops for a moment.</summary>
    public static string ConfirmText(NetAdapter adapter)
        => "Windows asks the network for a new address for the " + AdapterRules.KindWords(adapter?.Kind ?? AdapterKind.Other) + " adapter"
            + (string.IsNullOrEmpty(adapter?.Name) ? "" : " (" + adapter.Name + ")") + ". Its connection drops for a moment, also for anyone else signed in to this device.";

    /// <summary>The run's <c>ip-renew</c> record, or null.</summary>
    public static IpRenewRecord Of(RunOutcome outcome) => outcome?.Records.LastOrDefault(r => r.Type == "ip-renew" && r.Renewal != null)?.Renewal;

    /// <summary>The run log line: run, adapter kind, usable lease yes/no, result; never a name or an address.</summary>
    public static string LogLine(string runId, IpRenewRecord record)
        => "RENEW run=" + (string.IsNullOrEmpty(runId) ? "none" : runId) + " context=service adapter-type=" + (record.Kind.Length == 0 ? "unknown" : record.Kind)
            + " usable-lease=" + (record.Lease.Length == 0 ? "no" : record.Lease) + " result=" + record.Result + (record.Renew.Length == 0 ? "" : " renew=" + record.Renew);

    private static string Kind(IpRenewRecord record) => record.Kind switch
    {
        "ethernet" => "Ethernet",
        "wi-fi" => "Wi-Fi",
        _ => "network",
    };

    /// <summary>
    /// The status of a finished run, from its record: honest about what Windows confirmed. A
    /// usable address says nothing about the internet: that is Check Microsoft connections.
    /// </summary>
    public static void Apply(ResultView view, RunOutcome outcome)
    {
        if (outcome.Result == ResultCode.InvalidRequest && (outcome.Message ?? "").StartsWith("Unknown action", StringComparison.Ordinal))
        {
            // An older service does not know the action (the service's gate for an unknown action).
            view.Tone = Tone.Warning;
            view.Status = Name + " is not available on this device yet: its " + ProductInfo.DisplayName + " service is an older version.";
            view.Cards.Clear();
            view.Cards.Add(ResultCard.From(FindingSeverity.Warning, "Not available on this device yet", "The " + ProductInfo.DisplayName + " service on this device does not know this action yet. Nothing was changed.",
                "Ask your IT support to update " + ProductInfo.DisplayName + ".", "The installed service is older than the app; deploy the current version."));
            return;
        }
        var record = Of(outcome);
        if (outcome.Result != ResultCode.Completed || record == null) { return; }
        var kind = Kind(record);
        (view.Status, view.Tone) = record.Result switch
        {
            "got-usable-address" => ("The " + kind + " adapter got a usable address. Check Microsoft connections tests the internet.", Tone.Success),
            "no-answer" => ("Still no usable address: the DHCP server did not answer.", Tone.Error),
            "renew-failed" => ("Windows could not renew the address of the " + kind + " adapter.", Tone.Error),
            "fixed-address" => ("Not offered: the " + kind + " adapter uses a fixed address. Nothing was changed.", Tone.Neutral),
            "disabled" => ("The " + kind + " adapter is turned off. Turn it on, then try again.", Tone.Warning),
            "no-driver" => ("The " + kind + " adapter has no working driver. Contact your IT support.", Tone.Warning),
            "no-media" => (kind == "Wi-Fi" ? "The Wi-Fi adapter is not connected to a network. Connect to Wi-Fi, then try again." : "No network cable is connected to the " + kind + " adapter. Plug it in, then try again.", Tone.Warning),
            "not-found" => ("The chosen adapter was not found. Refresh the adapter list and choose it again.", Tone.Warning),
            _ => (view.Status, view.Tone),
        };
    }

    /// <summary>"&lt;exit&gt;|&lt;run ID&gt;|&lt;result&gt;" (as the other checks write their history), or null when the run has no record.</summary>
    public static string HistoryResult(RunOutcome outcome)
    {
        var record = Of(outcome);
        if (record == null || outcome.Result != ResultCode.Completed || !outcome.ExitCode.HasValue || outcome.ExitCode.Value < 0) { return null; }
        return outcome.ExitCode.Value.ToString(CultureInfo.InvariantCulture) + "|" + (string.IsNullOrEmpty(outcome.RunId) ? "none" : outcome.RunId) + "|" + record.Result;
    }

    /// <summary>A result from the history in words; null for a word this version does not know.</summary>
    public static string ResultWords(string slug) => slug switch
    {
        "got-usable-address" => "got a usable address",
        "no-answer" => "still no usable address: the DHCP server did not answer",
        "renew-failed" => "Windows could not renew the address",
        "fixed-address" => "not offered: the adapter uses a fixed address",
        "disabled" => "the adapter is turned off",
        "no-driver" => "the adapter has no working driver",
        "no-media" => "no cable or Wi-Fi connection",
        "not-found" => "the chosen adapter was not found",
        _ => null,
    };
}
