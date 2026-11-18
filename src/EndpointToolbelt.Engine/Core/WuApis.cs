using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace EndpointToolbelt.Engine;

/// <summary>One entry of the Windows Update history (the API's dates are UTC already).</summary>
public sealed class WuHistoryEntry
{
    public DateTime DateUtc { get; set; }
    public string Title { get; set; } = "";
    public int ResultCode { get; set; }
    public int Operation { get; set; }
    public long HResult { get; set; }
    public string Client { get; set; } = "";
    public string UpdateId { get; set; } = "";
}

/// <summary>One installed KB of the hotfix list (Win32_QuickFixEngineering).</summary>
public sealed class WuHotFix
{
    public string Id { get; set; } = "";
    public DateTime? InstalledOn { get; set; }
}

/// <summary>One event as read from the log: its UTC time, its property values as written, and its message.</summary>
public sealed class WuRawEvent
{
    public DateTime TimeCreatedUtc { get; set; }
    public IReadOnlyList<object> Properties { get; set; } = Array.Empty<object>();
    public string Message { get; set; } = "";
}

/// <summary>A device node, present or not.</summary>
public sealed class PnpDeviceInfo
{
    public string InstanceId { get; set; } = "";
    public string Class { get; set; } = "";
    public string Service { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public bool Present { get; set; }
}

/// <summary>The EFI system partition's volume: free and total bytes.</summary>
public sealed class EspVolume
{
    public long FreeBytes { get; set; }
    public long SizeBytes { get; set; }
}

/// <summary>What the Windows Update repair reads from Windows outside the registry and the services.</summary>
public interface IWindowsUpdateSystem
{
    /// <summary>The newest history entries (at most <paramref name="max"/>); throws when the history cannot be read.</summary>
    IReadOnlyList<WuHistoryEntry> History(int max);

    /// <summary>The hotfix list; throws when it cannot be read.</summary>
    IReadOnlyList<WuHotFix> HotFixes();

    /// <summary>Events of one log and provider with these IDs since a moment, newest first; throws when the log cannot be read.</summary>
    IReadOnlyList<WuRawEvent> Events(string logName, string provider, IReadOnlyList<int> ids, DateTime sinceUtc, int max);

    /// <summary>The default automatic-update service as (ID, name), or null; throws when it cannot be read.</summary>
    Tuple<string, string> DefaultService();

    /// <summary>Every device node the system knows, present or not; throws when the list cannot be read.</summary>
    IReadOnlyList<PnpDeviceInfo> Devices();

    /// <summary>One device node, or null when there is none with this ID; throws when it cannot be read.</summary>
    PnpDeviceInfo Device(string instanceId);

    /// <summary>
    /// The EFI system partition's volume (the one Windows booted from first, any otherwise), or
    /// null when there is none; throws when the storage information cannot be read.
    /// </summary>
    EspVolume Esp();
}

/// <summary>Runs code on a single-threaded apartment thread, as Windows PowerShell runs its COM calls.</summary>
public static class StaThread
{
    public static T Run<T>(Func<T> work)
    {
        T result = default;
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
        return result;
    }
}

/// <summary>Late-bound COM calls through IDispatch: no generated interop assembly and no code generated at run time.</summary>
public static class LateBound
{
    public static object Create(string progId) => Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true));

    public static object Get(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args, CultureInfo.InvariantCulture);

    public static void Set(object target, string name, object value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value }, CultureInfo.InvariantCulture);

    public static object Call(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    public static int ToInt(object value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    public static string ToText(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    /// <summary>The items of a COM collection with Count and Item(index from 0).</summary>
    public static IEnumerable<object> Items(object collection)
    {
        var count = ToInt(Get(collection, "Count"));
        for (var i = 0; i < count; i++) { yield return Get(collection, "Item", i); }
    }

    /// <summary>The innermost exception's HRESULT, as Windows PowerShell shows a failed COM call.</summary>
    public static long HResult(Exception ex)
    {
        while (ex.InnerException != null) { ex = ex.InnerException; }
        return ex.HResult;
    }
}

/// <summary>The real reads, each the documented API the PowerShell cmdlet used underneath.</summary>
public sealed class WindowsUpdateSystem : IWindowsUpdateSystem
{
    private const string EspType = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";
    private static readonly char[] NullSeparator = { '\0' };

    public IReadOnlyList<WuHistoryEntry> History(int max) => StaThread.Run(() =>
    {
        var searcher = LateBound.Call(LateBound.Create("Microsoft.Update.Session"), "CreateUpdateSearcher");
        var count = Math.Min(LateBound.ToInt(LateBound.Call(searcher, "GetTotalHistoryCount")), max);
        var entries = new List<WuHistoryEntry>();
        if (count <= 0) { return (IReadOnlyList<WuHistoryEntry>)entries; }
        foreach (var entry in LateBound.Items(LateBound.Call(searcher, "QueryHistory", 0, count)))
        {
            var updateId = "";
            try { updateId = LateBound.ToText(LateBound.Get(LateBound.Get(entry, "UpdateIdentity"), "UpdateID")); }
            catch (Exception) { updateId = ""; }
            entries.Add(new WuHistoryEntry
            {
                // The API returns UTC already; converting again would shift it twice.
                DateUtc = DateTime.SpecifyKind((DateTime)LateBound.Get(entry, "Date"), DateTimeKind.Utc),
                Title = LateBound.ToText(LateBound.Get(entry, "Title")),
                ResultCode = LateBound.ToInt(LateBound.Get(entry, "ResultCode")),
                Operation = LateBound.ToInt(LateBound.Get(entry, "Operation")),
                HResult = Convert.ToInt64(LateBound.Get(entry, "HResult"), CultureInfo.InvariantCulture),
                Client = LateBound.ToText(LateBound.Get(entry, "ClientApplicationID")),
                UpdateId = updateId,
            });
        }
        return entries;
    });

    public IReadOnlyList<WuHotFix> HotFixes()
    {
        var list = new List<WuHotFix>();
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering");
        foreach (ManagementObject item in searcher.Get())
        {
            using (item)
            {
                list.Add(new WuHotFix { Id = item["HotFixID"] as string ?? "", InstalledOn = ParseInstalledOn(item["InstalledOn"] as string) });
            }
        }
        return list;
    }

    /// <summary>
    /// The InstalledOn text of the hotfix list as Get-HotFix reads it: a FILETIME in 16 hex digits
    /// on some systems, otherwise a date such as "10/14/2026"; null when empty or not a date.
    /// </summary>
    public static DateTime? ParseInstalledOn(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        text = text.Trim();
        if (text.Length == 16 && long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fileTime))
        {
            try { return DateTime.FromFileTime(fileTime); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    public IReadOnlyList<WuRawEvent> Events(string logName, string provider, IReadOnlyList<int> ids, DateTime sinceUtc, int max)
    {
        var idFilter = string.Join(" or ", ids.Select(i => "EventID=" + i.ToString(CultureInfo.InvariantCulture)));
        var xpath = string.Format(CultureInfo.InvariantCulture, "*[System[Provider[@Name='{0}'] and ({1}) and TimeCreated[@SystemTime>='{2}']]]",
            provider, idFilter, sinceUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        // Newest first, as Get-WinEvent returns them.
        var query = new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true };
        var list = new List<WuRawEvent>();
        using var reader = new EventLogReader(query);
        for (var record = reader.ReadEvent(); record != null && list.Count < max; record = reader.ReadEvent())
        {
            using (record)
            {
                if (record.TimeCreated == null) { continue; }
                string message;
                try { message = record.FormatDescription() ?? ""; }
                catch (EventLogException) { message = ""; }
                list.Add(new WuRawEvent
                {
                    TimeCreatedUtc = record.TimeCreated.Value.ToUniversalTime(),
                    Properties = record.Properties.Select(p => p.Value).ToList(),
                    Message = message,
                });
            }
        }
        return list;
    }

    public Tuple<string, string> DefaultService() => StaThread.Run(() =>
    {
        var manager = LateBound.Create("Microsoft.Update.ServiceManager");
        foreach (var service in LateBound.Items(LateBound.Get(manager, "Services")))
        {
            if (Convert.ToBoolean(LateBound.Get(service, "IsDefaultAUService"), CultureInfo.InvariantCulture))
            {
                return Tuple.Create(LateBound.ToText(LateBound.Get(service, "ServiceID")).ToLowerInvariant(), LateBound.ToText(LateBound.Get(service, "Name")));
            }
        }
        return null;
    });

    public IReadOnlyList<PnpDeviceInfo> Devices()
    {
        var size = 0;
        Check(CM_Get_Device_ID_List_Size(out size, null, CmGetIdListFilterNone));
        var buffer = new char[size];
        Check(CM_Get_Device_ID_List(null, buffer, size, CmGetIdListFilterNone));
        var list = new List<PnpDeviceInfo>();
        foreach (var id in new string(buffer).Split(NullSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var device = Device(id);
            if (device != null) { list.Add(device); }
        }
        return list;
    }

    public PnpDeviceInfo Device(string instanceId)
    {
        // A phantom (not present) node is found only with CM_LOCATE_DEVNODE_PHANTOM.
        if (CM_Locate_DevNode(out var node, instanceId, CmLocateDevnodePhantom) != CrSuccess) { return null; }
        var present = CM_Locate_DevNode(out _, instanceId, CmLocateDevnodeNormal) == CrSuccess;
        var name = Property(node, CmDrpFriendlyName);
        if (name.Length == 0) { name = Property(node, CmDrpDeviceDesc); }
        return new PnpDeviceInfo
        {
            InstanceId = instanceId,
            Class = Property(node, CmDrpClass),
            Service = Property(node, CmDrpService),
            FriendlyName = name,
            Present = present,
        };
    }

    private static string Property(int node, int property)
    {
        var length = 0;
        var result = CM_Get_DevNode_Registry_Property(node, property, out _, null, ref length, 0);
        if (result != CrBufferSmall || length <= 0) { return ""; }
        var buffer = new byte[length];
        if (CM_Get_DevNode_Registry_Property(node, property, out _, buffer, ref length, 0) != CrSuccess) { return ""; }
        return Encoding.Unicode.GetString(buffer, 0, length).TrimEnd('\0');
    }

    private static void Check(int result)
    {
        if (result != CrSuccess) { throw new InvalidOperationException("the device list could not be read (CONFIGRET " + result.ToString(CultureInfo.InvariantCulture) + ")"); }
    }

    public EspVolume Esp()
    {
        using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_Partition");
        var all = searcher.Get().Cast<ManagementObject>()
            .Where(p => string.Equals(p["GptType"] as string, EspType, StringComparison.OrdinalIgnoreCase)).ToList();
        var esp = all.FirstOrDefault(p => p["IsSystem"] is bool system && system) ?? all.FirstOrDefault();
        if (esp == null) { return null; }
        foreach (ManagementObject volume in esp.GetRelated("MSFT_Volume"))
        {
            using (volume)
            {
                return new EspVolume
                {
                    FreeBytes = Convert.ToInt64(volume["SizeRemaining"], CultureInfo.InvariantCulture),
                    SizeBytes = Convert.ToInt64(volume["Size"], CultureInfo.InvariantCulture),
                };
            }
        }
        throw new InvalidOperationException("the EFI system partition has no volume");
    }

    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private const int CmGetIdListFilterNone = 0;
    private const int CmLocateDevnodeNormal = 0;
    private const int CmLocateDevnodePhantom = 1;
    private const int CmDrpDeviceDesc = 0x01;
    private const int CmDrpService = 0x05;
    private const int CmDrpClass = 0x08;
    private const int CmDrpFriendlyName = 0x0D;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_Size(out int length, string filter, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List(string filter, [Out] char[] buffer, int length, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNode(out int node, string deviceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    private static extern int CM_Get_DevNode_Registry_Property(int node, int property, out int type, [Out] byte[] buffer, ref int length, int flags);
}
