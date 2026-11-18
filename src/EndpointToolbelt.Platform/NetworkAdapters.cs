using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Threading;

namespace EndpointToolbelt.Platform;

/// <summary>What kind of network adapter it is, as the result and the run log name it (never its name, address or SSID).</summary>
public enum AdapterKind
{
    Ethernet,
    WiFi,
    Other,
}

/// <summary>
/// One network adapter as Windows has it now. The app shows its connection name to let the
/// user choose; logs, history and Copy details for IT carry only its kind and whether it has a usable
/// IPv4 address, never the name, an address or a Wi-Fi network name.
/// </summary>
public sealed class NetAdapter
{
    /// <summary>The interface index: what the app sends and the engine looks up again itself.</summary>
    public int InterfaceIndex { get; set; }

    /// <summary>The connection name ("Ethernet", "Wi-Fi"); shown on the page only.</summary>
    public string Name { get; set; } = "";

    public AdapterKind Kind { get; set; }

    /// <summary>A hardware adapter (not a VPN, tunnel, Bluetooth or other virtual adapter).</summary>
    public bool Physical { get; set; }

    /// <summary>The adapter is turned on (not disabled in Windows).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Its driver works (Device Manager shows no problem other than "disabled").</summary>
    public bool DriverOk { get; set; } = true;

    /// <summary>A cable is plugged in, or the Wi-Fi is connected to a network.</summary>
    public bool MediaConnected { get; set; }

    /// <summary>It gets its IPv4 address from a DHCP server (not a fixed address).</summary>
    public bool Dhcp { get; set; }

    /// <summary>It has an IPv4 address that is not a self-assigned one (169.254.x.x).</summary>
    public bool UsableIpv4 { get; set; }

    /// <summary>It has only a self-assigned IPv4 address (169.254.x.x): no DHCP server answered.</summary>
    public bool SelfAssignedOnly { get; set; }
}

/// <summary>Why an adapter is not offered a new address; None when it is.</summary>
public enum RenewBlock
{
    None,
    NotPhysical,
    Disabled,
    NoDriver,
    NoMedia,
    FixedAddress,
}

/// <summary>What renewing a lease did, before the address is read again.</summary>
public enum RenewCall
{
    Done,
    Failed,
    TimedOut,
}

/// <summary>The rules of Get a new IP address, without system calls (testable).</summary>
public static class AdapterRules
{
    /// <summary>A self-assigned IPv4 address (APIPA, 169.254.0.0/16): no DHCP server answered.</summary>
    public static bool IsSelfAssigned(string address) => (address ?? "").StartsWith("169.254.", StringComparison.Ordinal);

    /// <summary>An IPv4 address in dotted form (an IPv6 address has a colon).</summary>
    public static bool IsIpv4(string address) => !string.IsNullOrEmpty(address) && address.IndexOf(':') < 0 && address.IndexOf('.') > 0;

    /// <summary>An IPv4 address the adapter can use: not self-assigned and not 0.0.0.0.</summary>
    public static bool IsUsableIpv4(string address) => IsIpv4(address) && !IsSelfAssigned(address) && address != "0.0.0.0";

    /// <summary>Why the adapter is not offered a new address, or None. The order is what the user can fix first.</summary>
    public static RenewBlock Block(NetAdapter adapter)
    {
        if (adapter == null || !adapter.Physical) { return RenewBlock.NotPhysical; }
        if (!adapter.DriverOk) { return RenewBlock.NoDriver; }
        if (!adapter.Enabled) { return RenewBlock.Disabled; }
        if (!adapter.MediaConnected) { return RenewBlock.NoMedia; }
        if (!adapter.Dhcp) { return RenewBlock.FixedAddress; }
        return RenewBlock.None;
    }

    /// <summary>
    /// The adapter needs a new address: physical, on, with a driver and a cable or Wi-Fi connection,
    /// using DHCP, and without a usable IPv4 address (none, or only 169.254.x.x). Only then does
    /// "I can't connect" offer Get a new IP address.
    /// </summary>
    public static bool NeedsNewAddress(NetAdapter adapter) => Block(adapter) == RenewBlock.None && !adapter.UsableIpv4;

    /// <summary>The adapters the page lists: the physical ones, those that need a new address first.</summary>
    public static List<NetAdapter> Listed(IEnumerable<NetAdapter> adapters)
        => (adapters ?? Enumerable.Empty<NetAdapter>()).Where(a => a != null && a.Physical)
            .OrderByDescending(NeedsNewAddress).ThenBy(a => a.Kind).ThenBy(a => a.InterfaceIndex).ToList();

    /// <summary>The kind in the run log and the result: "ethernet", "wi-fi" or "other".</summary>
    public static string KindSlug(AdapterKind kind) => kind switch
    {
        AdapterKind.Ethernet => "ethernet",
        AdapterKind.WiFi => "wi-fi",
        _ => "other",
    };

    /// <summary>The kind in words: "Ethernet", "Wi-Fi" or "network".</summary>
    public static string KindWords(AdapterKind kind) => kind switch
    {
        AdapterKind.Ethernet => "Ethernet",
        AdapterKind.WiFi => "Wi-Fi",
        _ => "network",
    };

    /// <summary>The kind from the NDIS physical medium (9 and 1: wireless, 14: 802.3) and WMI's adapter type (0: 802.3).</summary>
    public static AdapterKind KindOf(uint? physicalMedium, int? adapterType)
    {
        if (physicalMedium is 9 or 1) { return AdapterKind.WiFi; }
        if (physicalMedium == 14) { return AdapterKind.Ethernet; }
        return adapterType == 0 ? AdapterKind.Ethernet : AdapterKind.Other;
    }

    /// <summary>
    /// Whether a cable or Wi-Fi connection is there, from Win32_NetworkAdapter's NetConnectionStatus:
    /// 2 connected, 1 connecting, 8 and 9 authenticating, 10 and 12 (802.1X), 11 "invalid address"
    /// (connected, but no usable address: the case this tool is for). 0, 3, 4, 5, 6 and 7 are not.
    /// </summary>
    public static bool MediaFromStatus(int? status) => status is 1 or 2 or 8 or 9 or 10 or 11 or 12;
}

/// <summary>The adapters as Get a new IP address needs them; the real one is <see cref="WmiNetworkAdapters"/>, tests use a fake.</summary>
public interface INetworkAdapters
{
    /// <summary>The network adapters now (physical and others, each marked).</summary>
    IReadOnlyList<NetAdapter> Read();
}

/// <summary>Renews one adapter's DHCP lease (the engine, as SYSTEM).</summary>
public interface ILeaseRenewer
{
    /// <summary>Asks Windows to renew the lease of the adapter with this interface index, within the time limit.</summary>
    RenewCall Renew(int interfaceIndex, TimeSpan limit, out string detail);
}

/// <summary>
/// The adapters from WMI, read-only: Win32_NetworkAdapter (physical, driver, connection status),
/// MSFT_NetAdapter (virtual, the physical medium, the media state) and
/// Win32_NetworkAdapterConfiguration (DHCP, the IPv4 addresses, which are judged here and never kept).
/// Works for a standard user; the engine renews through <see cref="Renew"/> as SYSTEM.
/// </summary>
public sealed class WmiNetworkAdapters : INetworkAdapters, ILeaseRenewer
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    public IReadOnlyList<NetAdapter> Read()
    {
        var result = new List<NetAdapter>();
        var modern = ReadModern();
        foreach (var item in Query(@"root\cimv2", "SELECT Index, InterfaceIndex, NetConnectionID, PhysicalAdapter, NetEnabled, NetConnectionStatus, ConfigManagerErrorCode, AdapterTypeId FROM Win32_NetworkAdapter"))
        {
            using (item)
            {
                var index = AsInt(item["InterfaceIndex"]);
                if (index is not > 0) { continue; }
                var name = item["NetConnectionID"] as string;
                // An adapter without a connection name is not one the user sees in Settings.
                if (string.IsNullOrEmpty(name)) { continue; }
                modern.TryGetValue(index.Value, out var m);
                var error = AsInt(item["ConfigManagerErrorCode"]) ?? 0;
                var status = AsInt(item["NetConnectionStatus"]);
                var adapter = new NetAdapter
                {
                    InterfaceIndex = index.Value,
                    Name = name,
                    Kind = AdapterRules.KindOf(m.Medium, AsInt(item["AdapterTypeId"])),
                    // Win32 alone counts VPN and Bluetooth adapters; MSFT_NetAdapter marks them virtual.
                    Physical = item["PhysicalAdapter"] is true && !m.Virtual,
                    // 22: disabled in Device Manager; 0 the device works; anything else is a driver problem.
                    DriverOk = error is 0 or 22,
                    Enabled = error != 22 && m.State != 3 && status is not (0 or 5),
                    MediaConnected = m.MediaConnected ?? AdapterRules.MediaFromStatus(status),
                };
                ReadConfiguration(AsInt(item["Index"]), adapter);
                result.Add(adapter);
            }
        }
        return result;
    }

    private struct Modern
    {
        public bool Virtual;
        public uint? Medium;
        public int? State;
        public bool? MediaConnected;
    }

    /// <summary>MSFT_NetAdapter by interface index; empty when the class cannot be read (then Win32 alone decides).</summary>
    private static Dictionary<int, Modern> ReadModern()
    {
        var map = new Dictionary<int, Modern>();
        try
        {
            foreach (var item in Query(@"root\StandardCimv2", "SELECT InterfaceIndex, Virtual, NdisPhysicalMedium, State, MediaConnectState FROM MSFT_NetAdapter"))
            {
                using (item)
                {
                    var index = AsInt(item["InterfaceIndex"]);
                    if (index == null) { continue; }
                    var media = AsInt(item["MediaConnectState"]);
                    map[index.Value] = new Modern
                    {
                        Virtual = item["Virtual"] is true,
                        Medium = item["NdisPhysicalMedium"] is uint medium ? medium : null,
                        State = AsInt(item["State"]),
                        // 1 connected, 2 disconnected, 0 unknown (then the Win32 status decides).
                        MediaConnected = media == 1 ? true : media == 2 ? false : null,
                    };
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            map.Clear();
        }
        return map;
    }

    private static void ReadConfiguration(int? index, NetAdapter adapter)
    {
        if (index == null) { return; }
        foreach (var item in Query(@"root\cimv2", "SELECT DHCPEnabled, IPAddress FROM Win32_NetworkAdapterConfiguration WHERE Index = " + index.Value.ToString(CultureInfo.InvariantCulture)))
        {
            using (item)
            {
                adapter.Dhcp = item["DHCPEnabled"] is true;
                var addresses = (item["IPAddress"] as string[] ?? Array.Empty<string>()).Where(AdapterRules.IsIpv4).ToList();
                adapter.UsableIpv4 = addresses.Any(AdapterRules.IsUsableIpv4);
                adapter.SelfAssignedOnly = !adapter.UsableIpv4 && addresses.Any(AdapterRules.IsSelfAssigned);
            }
        }
    }

    /// <summary>
    /// Win32_NetworkAdapterConfiguration.RenewDHCPLease on the one adapter with this interface
    /// index, on a worker thread with a time limit (the call blocks until the DHCP server answers or
    /// Windows gives up). 0 and 1 are success; any other code is a failure, said with its number.
    /// </summary>
    public RenewCall Renew(int interfaceIndex, TimeSpan limit, out string detail)
    {
        string text = null;
        var call = RenewCall.Failed;
        var worker = new Thread(() =>
        {
            try
            {
                ManagementObject target = null;
                foreach (var item in Query(@"root\cimv2", "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE InterfaceIndex = " + interfaceIndex.ToString(CultureInfo.InvariantCulture)))
                {
                    if (target == null) { target = item; } else { item.Dispose(); }
                }
                if (target == null)
                {
                    text = "Windows lists no configuration for this adapter.";
                    return;
                }
                using (target)
                using (var output = target.InvokeMethod("RenewDHCPLease", null, null))
                {
                    var code = AsInt(output?["ReturnValue"]) ?? -1;
                    text = "RenewDHCPLease returned " + code.ToString(CultureInfo.InvariantCulture) + ".";
                    call = code is 0 or 1 ? RenewCall.Done : RenewCall.Failed;
                }
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                text = ex.Message;
                call = RenewCall.Failed;
            }
        }) { IsBackground = true, Name = "DHCP renew" };
        worker.Start();
        if (!worker.Join(limit))
        {
            detail = "Windows did not finish renewing within " + ((int)limit.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " seconds.";
            return RenewCall.TimedOut;
        }
        detail = text ?? "";
        return call;
    }

    private static IEnumerable<ManagementObject> Query(string scope, string query)
    {
        var options = new EnumerationOptions { Timeout = QueryTimeout, ReturnImmediately = false, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), options);
        using var results = searcher.Get();
        foreach (ManagementObject item in results) { yield return item; }
    }

    private static int? AsInt(object value)
    {
        try
        {
            return value == null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}
