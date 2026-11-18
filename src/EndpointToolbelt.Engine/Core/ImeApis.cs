using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;

namespace EndpointToolbelt.Engine;

/// <summary>One event of the MDM channels, as the engine uses it: fields by name, never the rendered message.</summary>
public sealed class MdmEvent
{
    public int Id { get; set; }
    public long RecordId { get; set; }

    /// <summary>The event's level (1 critical, 2 error, 3 warning, 4 information).</summary>
    public int Level { get; set; }
    public DateTime TimeCreatedUtc { get; set; }

    /// <summary>The writer's process ID as the event log recorded it, or null.</summary>
    public string ProcessId { get; set; }

    /// <summary>Every EventData value, in order.</summary>
    public List<string> Values { get; } = new();

    /// <summary>Named EventData values (the first of a name), in order.</summary>
    public List<KeyValuePair<string, string>> Fields { get; } = new();

    public string Field(string name)
    {
        foreach (var pair in Fields) { if (pair.Key == name) { return pair.Value; } }
        return null;
    }
}

/// <summary>The seam for the MDM event channels.</summary>
public interface IMdmEventSource
{
    /// <summary>The newest events of one channel from one provider with these IDs (newest first, at most <paramref name="max"/>); throws when the channel cannot be read.</summary>
    IReadOnlyList<MdmEvent> Read(string logName, string provider, IReadOnlyCollection<int> eventIds, int max);

    /// <summary>How many events of a provider a channel holds between two moments (inclusive); throws when it cannot be read.</summary>
    int Count(string logName, string provider, DateTime fromUtc, DateTime toUtc);

    /// <summary>The newest events of one channel from one provider at these levels since a moment (newest first, at most <paramref name="max"/>); throws when the channel cannot be read.</summary>
    IReadOnlyList<MdmEvent> ReadLevels(string logName, string provider, IReadOnlyCollection<int> levels, DateTime sinceUtc, int max);
}

/// <summary>
/// The seam for processes: whether Company Portal runs, and the session of an event's writer.
/// Read-only: the engine never ends a process (it never closes Company Portal; the app asks
/// the user to close it).
/// </summary>
public interface IProcessControl
{
    /// <summary>The IDs of the packaged app's processes with this name, as found now.</summary>
    IReadOnlyList<int> Ids(string name);

    /// <summary>The session a process runs in, or null when it has exited.</summary>
    int? SessionId(int id);
}

/// <summary>An enrollment's sync task as the engine uses it.</summary>
public sealed class SyncTask
{
    public string Name { get; set; }
    public string Path { get; set; }
}

/// <summary>The seam for the Task Scheduler.</summary>
public interface ITaskScheduler
{
    /// <summary>
    /// The first enabled, demand-startable task in a folder whose action runs deviceenroller.exe
    /// with "/o" and "/c /b" (selected by its action, not its localised name), or null. Throws
    /// when the folder cannot be listed.
    /// </summary>
    SyncTask FindSyncTask(string folderPath);

    /// <summary>Starts the task; throws when it cannot.</summary>
    void Start(SyncTask task);
}

public sealed class WindowsMdmEventSource : IMdmEventSource
{
    public IReadOnlyList<MdmEvent> Read(string logName, string provider, IReadOnlyCollection<int> eventIds, int max)
    {
        var ids = string.Join(" or ", eventIds.Select(i => "EventID=" + i.ToString(CultureInfo.InvariantCulture)));
        var query = new EventLogQuery(logName, PathType.LogName, "*[System[Provider[@Name='" + provider + "'] and (" + ids + ")]]") { ReverseDirection = true };
        var list = new List<MdmEvent>();
        using var reader = new EventLogReader(query);
        for (var record = reader.ReadEvent(); record != null && list.Count < max; record = reader.ReadEvent())
        {
            using (record)
            {
                // An event whose XML cannot be read is skipped; it is never evidence of anything.
                var parsed = Parse(record);
                if (parsed != null) { list.Add(parsed); }
            }
        }
        return list;
    }

    public IReadOnlyList<MdmEvent> ReadLevels(string logName, string provider, IReadOnlyCollection<int> levels, DateTime sinceUtc, int max)
    {
        var filter = string.Join(" or ", levels.Select(l => "Level=" + l.ToString(CultureInfo.InvariantCulture)));
        var since = sinceUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var query = new EventLogQuery(logName, PathType.LogName, "*[System[Provider[@Name='" + provider + "'] and (" + filter + ") and TimeCreated[@SystemTime>='" + since + "']]]") { ReverseDirection = true };
        var list = new List<MdmEvent>();
        using var reader = new EventLogReader(query);
        for (var record = reader.ReadEvent(); record != null && list.Count < max; record = reader.ReadEvent())
        {
            using (record)
            {
                var parsed = Parse(record);
                if (parsed != null) { list.Add(parsed); }
            }
        }
        return list;
    }

    public int Count(string logName, string provider, DateTime fromUtc, DateTime toUtc)
    {
        string Time(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var query = new EventLogQuery(logName, PathType.LogName,
            "*[System[Provider[@Name='" + provider + "'] and TimeCreated[@SystemTime>='" + Time(fromUtc) + "' and @SystemTime<='" + Time(toUtc) + "']]]");
        var count = 0;
        using var reader = new EventLogReader(query);
        for (var record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
        {
            record.Dispose();
            count++;
        }
        return count;
    }

    private static MdmEvent Parse(EventRecord record)
    {
        try
        {
            if (record.TimeCreated == null || record.RecordId == null) { return null; }
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(record.ToXml()), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                doc.Load(reader);
            }
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("e", "http://schemas.microsoft.com/win/2004/08/events/event");
            var ev = new MdmEvent { Id = record.Id, RecordId = record.RecordId.Value, Level = record.Level ?? 0, TimeCreatedUtc = record.TimeCreated.Value.ToUniversalTime() };
            foreach (XmlElement node in doc.SelectNodes("/e:Event/e:EventData/e:Data", ns))
            {
                var text = node.InnerText;
                ev.Values.Add(text);
                var name = node.GetAttribute("Name");
                if (name.Length > 0 && ev.Field(name) == null) { ev.Fields.Add(new KeyValuePair<string, string>(name, text)); }
            }
            if (doc.SelectSingleNode("/e:Event/e:System/e:Execution", ns) is XmlElement exec && exec.HasAttribute("ProcessID")) { ev.ProcessId = exec.GetAttribute("ProcessID"); }
            return ev;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public sealed class WindowsProcessControl : IProcessControl
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public IReadOnlyList<int> Ids(string name)
    {
        var processes = Process.GetProcessesByName(name);
        var ids = new List<int>();
        foreach (var p in processes)
        {
            using (p)
            {
                using var handle = Open(p.Id, PROCESS_QUERY_LIMITED_INFORMATION);
                // A program a user named like the app is not the app; it is left alone.
                if (handle.IsInvalid || !IsPackagedApp(handle)) { continue; }
                ids.Add(p.Id);
            }
        }
        return ids;
    }

    private static Microsoft.Win32.SafeHandles.SafeProcessHandle Open(int id, uint access) => OpenProcess(access, false, id);

    // The engine is a 64-bit process, so this is the 64-bit Program Files folder.
    private static readonly string WindowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + @"\";

    private static bool IsPackagedApp(Microsoft.Win32.SafeHandles.SafeProcessHandle handle)
    {
        var buffer = new char[1024];
        var size = buffer.Length;
        return QueryFullProcessImageName(handle, 0, buffer, ref size)
            && new string(buffer, 0, size).StartsWith(WindowsApps, StringComparison.OrdinalIgnoreCase);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process, int flags, [System.Runtime.InteropServices.Out] char[] name, ref int size);


    public int? SessionId(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.SessionId;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// The Task Scheduler through its own COM API, late bound through IDispatch (as the service
/// reads the enrollment tasks): no generated interop assembly and no code generated at
/// run time.
/// </summary>
public sealed class WindowsTaskScheduler : ITaskScheduler
{
    private const int TaskStateDisabled = 1;
    private const int TaskActionExec = 0;
    private const int TaskEnumHidden = 1;

    private static readonly System.Text.RegularExpressions.Regex DeviceEnroller = new(@"deviceenroller\.exe", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly System.Text.RegularExpressions.Regex OptionO = new("/o", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly System.Text.RegularExpressions.Regex OptionCB = new(@"/c\s+/b(\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static object Com(object target, string name, BindingFlags flags, params object[] args) =>
        target.GetType().InvokeMember(name, flags, null, target, args, CultureInfo.InvariantCulture);

    private static object Get(object target, string name, params object[] args) => Com(target, name, BindingFlags.GetProperty, args);

    private static object Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true);
        var service = Activator.CreateInstance(type);
        Com(service, "Connect", BindingFlags.InvokeMethod);
        return service;
    }

    public SyncTask FindSyncTask(string folderPath)
    {
        var service = Connect();
        var folder = Com(service, "GetFolder", BindingFlags.InvokeMethod, folderPath.TrimEnd('\\'));
        var tasks = Com(folder, "GetTasks", BindingFlags.InvokeMethod, TaskEnumHidden);
        var count = Convert.ToInt32(Get(tasks, "Count"), CultureInfo.InvariantCulture);
        for (var i = 1; i <= count; i++)
        {
            var task = Get(tasks, "Item", i);
            if (Convert.ToInt32(Get(task, "State"), CultureInfo.InvariantCulture) == TaskStateDisabled) { continue; }
            var definition = Get(task, "Definition");
            if (!Convert.ToBoolean(Get(Get(definition, "Settings"), "AllowDemandStart"), CultureInfo.InvariantCulture)) { continue; }
            var actions = Get(definition, "Actions");
            var actionCount = Convert.ToInt32(Get(actions, "Count"), CultureInfo.InvariantCulture);
            for (var a = 1; a <= actionCount; a++)
            {
                var action = Get(actions, "Item", a);
                if (Convert.ToInt32(Get(action, "Type"), CultureInfo.InvariantCulture) != TaskActionExec) { continue; }
                var path = Get(action, "Path") as string ?? "";
                var arguments = Get(action, "Arguments") as string ?? "";
                if (DeviceEnroller.IsMatch(path) && OptionO.IsMatch(arguments) && OptionCB.IsMatch(arguments))
                {
                    var taskPath = Get(task, "Path") as string ?? "";
                    var name = Get(task, "Name") as string ?? "";
                    return new SyncTask { Name = name, Path = taskPath.Substring(0, Math.Max(0, taskPath.Length - name.Length)) };
                }
            }
        }
        return null;
    }

    public void Start(SyncTask task)
    {
        var service = Connect();
        var folder = Com(service, "GetFolder", BindingFlags.InvokeMethod, task.Path.TrimEnd('\\'));
        var registered = Com(folder, "GetTask", BindingFlags.InvokeMethod, task.Name);
        Com(registered, "Run", BindingFlags.InvokeMethod, new object[] { null });
    }
}

/// <summary>A program started without capturing its output, as Start-Process did.</summary>
public static class DetachedProcess
{
    /// <summary>The exit code, or null when it did not finish within the wait (it is left running). Throws when it cannot start.</summary>
    public static int? RunAndWait(string fileName, string arguments, int milliseconds)
    {
        var psi = new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = EndpointToolbelt.Platform.ProcessRunner.System32 };
        using var process = Process.Start(psi);
        return process.WaitForExit(milliseconds) ? process.ExitCode : null;
    }
}
