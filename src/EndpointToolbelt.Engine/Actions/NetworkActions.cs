using System;
using System.Globalization;
using System.Linq;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.Actions;

/// <summary>
/// Get a new IP address: renews the DHCP lease of the one physical adapter the user chose
/// (by its interface index, found again here), then lets Windows confirm whether the adapter now
/// has a usable IPv4 address. No cmd.exe, no ipconfig: one WMI method on that adapter only. The
/// records name the adapter's kind and whether it has a usable lease, never its name, an address
/// or a Wi-Fi network name.
/// </summary>
public sealed class RenewIpAddressHandler : IActionHandler
{
    /// <summary>The renew call's own limit: Windows waits for a DHCP answer, then gives up.</summary>
    internal static TimeSpan RenewLimit { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>How long the address is read again for, once a second, after the renew.</summary>
    internal static TimeSpan SettleTime { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The result slugs of the "ip-renew" record (the app's words and the history follow them).</summary>
    public const string GotAddress = "got-usable-address";
    public const string NoAnswer = "no-answer";
    public const string RenewFailed = "renew-failed";
    public const string FixedAddress = "fixed-address";
    public const string Disabled = "disabled";
    public const string NoDriver = "no-driver";
    public const string NoMedia = "no-media";
    public const string NotFound = "not-found";

    public int? Run(RunContext run)
    {
        run.StepHeader(1, 3, "Check the chosen network adapter");
        var adapter = Find(run, run.InterfaceIndex);
        if (adapter == null)
        {
            run.AddFinding(FindingLevel.Error, "IP-NOT-FOUND", "The chosen network adapter was not found",
                "Windows lists no physical network adapter under that number now; it may have been removed or turned into another one. Nothing was changed.",
                "Refresh the adapter list and choose it again.");
            Record(run, NotFound, null, false, "");
            return EngineExitCode.Failed;
        }
        var kind = AdapterRules.KindWords(adapter.Kind);
        run.Log(string.Format(CultureInfo.InvariantCulture, "Adapter: {0}; turned on: {1}; driver works: {2}; cable or Wi-Fi connected: {3}; DHCP: {4}; usable IPv4 address: {5}.",
            kind, YesNo(adapter.Enabled), YesNo(adapter.DriverOk), YesNo(adapter.MediaConnected), YesNo(adapter.Dhcp), YesNo(adapter.UsableIpv4)));
        switch (AdapterRules.Block(adapter))
        {
            case RenewBlock.NoDriver:
                run.AddFinding(FindingLevel.Warning, "IP-NO-DRIVER", "The " + kind + " adapter has no working driver",
                    "Windows shows a problem with its driver, so it cannot get an address. Nothing was changed.",
                    "Contact your IT support: the adapter's driver needs to be installed or repaired.",
                    "Device Manager shows a driver problem for this network adapter; install or repair its driver.");
                Record(run, NoDriver, adapter, adapter.UsableIpv4, "");
                return EngineExitCode.Warnings;
            case RenewBlock.Disabled:
                run.AddFinding(FindingLevel.Warning, "IP-DISABLED", "The " + kind + " adapter is turned off",
                    "Windows has this adapter disabled, so it cannot get an address. Nothing was changed.",
                    "Turn it on in Settings > Network & internet > Advanced network settings, then try again.");
                Record(run, Disabled, adapter, adapter.UsableIpv4, "");
                return EngineExitCode.Warnings;
            case RenewBlock.NoMedia:
                run.AddFinding(FindingLevel.Warning, "IP-NO-MEDIA", adapter.Kind == AdapterKind.WiFi ? "The Wi-Fi adapter is not connected to a network" : "No network cable is connected to the " + kind + " adapter",
                    "Without a cable or a Wi-Fi connection there is no network to get an address from. Nothing was changed.",
                    adapter.Kind == AdapterKind.WiFi ? "Connect to Wi-Fi, then try again." : "Plug in the network cable (or connect to Wi-Fi), then try again.");
                Record(run, NoMedia, adapter, adapter.UsableIpv4, "");
                return EngineExitCode.Warnings;
            case RenewBlock.FixedAddress:
                run.AddFinding(FindingLevel.Info, "IP-FIXED", "Not offered: the " + kind + " adapter uses a fixed address",
                    "Its address is set by hand, not by a DHCP server, so there is no lease to renew. Nothing was changed.",
                    "If the address seems wrong, contact your IT support.",
                    "The adapter has a static IPv4 configuration; check it against the network's addressing.");
                Record(run, FixedAddress, adapter, adapter.UsableIpv4, "");
                return EngineExitCode.NothingToRepair;
        }

        run.StepHeader(2, 3, "Ask the network for a new address");
        var call = run.Services.LeaseRenewer.Renew(adapter.InterfaceIndex, RenewLimit, out var detail);
        run.Log(call switch
        {
            RenewCall.Done => "Windows renewed the lease. " + detail,
            RenewCall.TimedOut => "Windows did not finish renewing in time. " + detail,
            _ => "Windows could not renew the lease. " + detail,
        }, call == RenewCall.Done ? LogSeverity.Info : LogSeverity.Warning);
        if (call != RenewCall.Failed) { run.AddChange("Asked Windows to renew the DHCP lease of the " + kind + " adapter (its connection dropped for a moment)"); }

        run.StepHeader(3, 3, "Check the address again");
        var after = WaitForAddress(run, adapter.InterfaceIndex);
        var usable = after?.UsableIpv4 ?? false;
        run.Log("Usable IPv4 address now: " + YesNo(usable) + ".");
        if (usable)
        {
            run.AddFinding(FindingLevel.Ok, "IP-USABLE", "The " + kind + " adapter got a usable address",
                "Windows confirms that the adapter now has an IPv4 address from the network. Whether the internet and Microsoft services can be reached is a separate test.",
                "Run Check Microsoft connections to test the internet and Microsoft services.");
            Record(run, GotAddress, after, true, call == RenewCall.Done ? "done" : call == RenewCall.TimedOut ? "timeout" : "failed");
            return EngineExitCode.Success;
        }
        if (call == RenewCall.Failed)
        {
            run.AddFinding(FindingLevel.Error, "IP-RENEW-FAILED", "Windows could not renew the address of the " + kind + " adapter",
                "The request to renew the lease failed (" + detail.TrimEnd('.') + "), and the adapter still has no usable address.",
                "Restart the device and try again. If it stays like this, contact your IT support.",
                "Win32_NetworkAdapterConfiguration.RenewDHCPLease failed on this adapter; check the DHCP Client service.");
            Record(run, RenewFailed, after, false, "failed");
            return EngineExitCode.Failed;
        }
        var selfAssigned = after?.SelfAssignedOnly ?? false;
        run.AddFinding(FindingLevel.Error, "IP-NO-ANSWER", "Still no usable address: the DHCP server did not answer",
            "Windows asked the network for an address" + (call == RenewCall.TimedOut ? " and waited" : "") + ", but none came: the adapter has "
                + (selfAssigned ? "only a self-assigned address (169.254.x.x)." : "no IPv4 address."),
            "Unplug and plug in the network cable, or turn Wi-Fi off and on, then try again. If it stays like this, contact your IT support.",
            "The adapter is connected and uses DHCP, but no usable lease arrived after a renew: check the DHCP server and its scope, the switch port or VLAN, and 802.1X or network access control.");
        Record(run, NoAnswer, after ?? adapter, false, call == RenewCall.TimedOut ? "timeout" : "done");
        return EngineExitCode.Failed;
    }

    /// <summary>The physical adapter under this index, read now; null when there is none.</summary>
    private static NetAdapter Find(RunContext run, int index)
    {
        if (index <= 0) { return null; }
        return run.Services.Network.Read().FirstOrDefault(a => a.InterfaceIndex == index && a.Physical);
    }

    /// <summary>Reads the adapter again once a second until it has a usable address or the settle time is over.</summary>
    private static NetAdapter WaitForAddress(RunContext run, int index)
    {
        var until = run.Services.Clock.Elapsed + SettleTime;
        while (true)
        {
            NetAdapter now;
            try { now = Find(run, index); }
            catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                run.Log("The adapter could not be read again: " + ex.Message, LogSeverity.Warning);
                now = null;
            }
            if (now?.UsableIpv4 == true || run.Services.Clock.Elapsed >= until) { return now; }
            run.Services.Clock.Sleep(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>The record the app reads: the result, the adapter's kind and whether it has a usable lease; never a name or an address.</summary>
    private static void Record(RunContext run, string result, NetAdapter adapter, bool usable, string renew)
        => run.WriteRecord("ip-renew",
            RunContext.Field("result", result),
            RunContext.Field("kind", adapter == null ? "unknown" : AdapterRules.KindSlug(adapter.Kind)),
            RunContext.Field("lease", usable ? "yes" : "no"),
            RunContext.Field("renew", renew));

    private static string YesNo(bool value) => value ? "yes" : "no";
}
