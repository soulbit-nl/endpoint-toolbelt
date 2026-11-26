using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Tests;

/// <summary>A clock that moves only when the test (or a sleep) moves it.</summary>
internal sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = DateTime.UtcNow;
    public DateTimeOffset Now => new DateTimeOffset(UtcNow).ToOffset(TimeSpan.FromHours(2));
    public TimeSpan Elapsed { get; set; }
    public List<TimeSpan> Sleeps { get; } = new();

    public void Sleep(TimeSpan duration)
    {
        Sleeps.Add(duration);
        Elapsed += duration;
    }
}

/// <summary>HKLM as a dictionary of keys and values.</summary>
internal sealed class FakeRegistry : IRegistry
{
    private readonly Dictionary<string, Dictionary<string, object>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public bool FailWrites { get; set; }

    /// <summary>Thrown by SubKeyNames for this key (an unreadable key).</summary>
    public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds a key and every key above it, as the registry has them.</summary>
    public void AddKey(string subKey)
    {
        for (var path = subKey; path.Length > 0; path = path.LastIndexOf('\\') > 0 ? path.Substring(0, path.LastIndexOf('\\')) : "")
        {
            if (!_keys.ContainsKey(path)) { _keys[path] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); }
        }
    }

    public IReadOnlyList<string> SubKeyNames(string subKey)
    {
        if (!_keys.ContainsKey(subKey) || Unreadable.Contains(subKey)) { throw new ArgumentException("The key " + subKey + " cannot be read."); }
        var prefix = subKey + "\\";
        return _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.IndexOf('\\', prefix.Length) < 0).Select(k => k.Substring(prefix.Length)).ToList();
    }

    public IReadOnlyList<string> ValueNames(string subKey) =>
        _keys.TryGetValue(subKey, out var values) ? values.Keys.ToList() : throw new ArgumentException("The key " + subKey + " does not exist.");

    public void DeleteKeyTree(string subKey)
    {
        if (!_keys.ContainsKey(subKey)) { throw new ArgumentException("The key " + subKey + " does not exist."); }
        foreach (var key in _keys.Keys.Where(k => string.Equals(k, subKey, StringComparison.OrdinalIgnoreCase) || k.StartsWith(subKey + "\\", StringComparison.OrdinalIgnoreCase)).ToList()) { _keys.Remove(key); }
    }

    public void Put(string subKey, string name, object value)
    {
        AddKey(subKey);
        _keys[subKey][name] = value;
    }

    public object GetValue(string subKey, string name) =>
        _keys.TryGetValue(subKey, out var values) && values.TryGetValue(name, out var value) ? value : null;

    public bool KeyExists(string subKey) => _keys.ContainsKey(subKey);

    public void SetString(string subKey, string name, string value)
    {
        if (FailWrites) { throw new UnauthorizedAccessException("Access to the registry key is denied."); }
        Put(subKey, name, value);
    }

    public void DeleteValue(string subKey, string name)
    {
        if (!_keys.TryGetValue(subKey, out var values) || !values.Remove(name)) { throw new ArgumentException("No value " + name + " exists with that name."); }
    }

    /// <summary>The last write time per key, as the test sets it; null when none was set.</summary>
    public Dictionary<string, DateTime> WriteTimes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime? KeyLastWriteUtc(string subKey) => _keys.ContainsKey(subKey) && WriteTimes.TryGetValue(subKey, out var at) ? at : null;
}

/// <summary>Records every tool run and answers with a fixed result, or per request.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessRequest> Requests { get; } = new();
    public ProcessResult Result { get; set; } = new() { Started = true, ExitCode = 0 };

    /// <summary>When set, answers each request (null falls back to <see cref="Result"/>).</summary>
    public Func<ProcessRequest, ProcessResult> Respond { get; set; }

    public ProcessResult Run(ProcessRequest request, Action<LogSeverity, string> log)
    {
        Requests.Add(request);
        return Respond?.Invoke(request) ?? Result;
    }

    public static ProcessResult Exit(int code, string output = "", string error = "") => new() { Started = true, ExitCode = code, Output = output, ErrorOutput = error };

    /// <summary>The runs of one tool (by file name), in order.</summary>
    public List<ProcessRequest> Of(string fileName) => Requests.Where(r => string.Equals(Path.GetFileName(r.FilePath), fileName, StringComparison.OrdinalIgnoreCase)).ToList();
}

internal sealed class FakeServiceControl : IServiceControl
{
    public bool Running { get; set; } = true;
    public Exception StartError { get; set; }
    public List<string> Started { get; } = new();

    public bool IsRunning(string name) => Running;

    public void Start(string name, TimeSpan wait)
    {
        if (StartError != null) { throw StartError; }
        Started.Add(name);
    }

    /// <summary>The state per service name; a missing name is not installed.</summary>
    public Dictionary<string, string> States { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The state a wait ends in (the waited-for state when not set).</summary>
    public string After { get; set; }

    /// <summary>A wait that times out.</summary>
    public bool Timeout { get; set; }

    public int Calls { get; set; }

    public string Status(string name) => States.TryGetValue(name, out var state) ? state : null;

    public void RequestStop(string name) => Calls++;

    public void RequestStart(string name) => Calls++;

    /// <summary>Waits for these states time out (a stop that hangs, a start that does not come).</summary>
    public HashSet<string> TimeoutOn { get; } = new(StringComparer.Ordinal);

    public void WaitForStatus(string name, string status, TimeSpan wait)
    {
        if (Timeout || TimeoutOn.Contains(status)) { throw new System.ServiceProcess.TimeoutException("Time out has expired and the operation has not been completed."); }
        States[name] = After ?? status;
        if (States[name] != status) { throw new InvalidOperationException("the service is " + States[name]); }
    }

    /// <summary>The start type per service name; a missing name is not installed.</summary>
    public Dictionary<string, string> StartTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string StartType(string name) => StartTypes.TryGetValue(name, out var type) ? type : throw new InvalidOperationException("Cannot find any service with service name '" + name + "'.");

    public IReadOnlyList<KeyValuePair<string, string>> Dependents(string name) => Array.Empty<KeyValuePair<string, string>>();
}

internal sealed class FakeMdmEvents : IMdmEventSource
{
    /// <summary>Answers a read by log name and the number of events asked for.</summary>
    public Func<string, int, IReadOnlyList<MdmEvent>> Events { get; set; } = (_, _) => Array.Empty<MdmEvent>();

    public int AdminCount { get; set; }

    public IReadOnlyList<MdmEvent> Read(string logName, string provider, IReadOnlyCollection<int> eventIds, int max) =>
        Events(logName, max).Where(e => eventIds.Contains(e.Id)).OrderByDescending(e => e.RecordId).Take(max).ToList();

    public int Count(string logName, string provider, DateTime fromUtc, DateTime toUtc) => AdminCount;

    /// <summary>Answers a read by level (the MDM admin log for the support package); throw to make the log unreadable.</summary>
    public Func<string, IReadOnlyList<MdmEvent>> LevelEvents { get; set; } = _ => Array.Empty<MdmEvent>();

    public IReadOnlyList<MdmEvent> ReadLevels(string logName, string provider, IReadOnlyCollection<int> levels, DateTime sinceUtc, int max) =>
        LevelEvents(logName).Where(e => levels.Contains(e.Level)).Take(max).ToList();

    public static MdmEvent New(int id, long recordId, DateTime utc, params (string Name, string Value)[] fields)
    {
        var ev = new MdmEvent { Id = id, RecordId = recordId, TimeCreatedUtc = utc, ProcessId = "1" };
        foreach (var (name, value) in fields)
        {
            ev.Values.Add(value);
            ev.Fields.Add(new KeyValuePair<string, string>(name, value));
        }
        return ev;
    }
}

internal sealed class FakeProcessControl : IProcessControl
{
    public List<int> Running { get; } = new();
    public Dictionary<int, int> Sessions { get; } = new();

    /// <summary>How often Ids was asked (the engine reads, it never ends a process).</summary>
    public int Reads { get; private set; }

    public IReadOnlyList<int> Ids(string name)
    {
        Reads++;
        return Running.ToList();
    }

    public int? SessionId(int id) => Sessions.TryGetValue(id, out var session) ? session : null;
}

internal sealed class FakeTaskScheduler : ITaskScheduler
{
    public SyncTask Task { get; set; }
    public Exception StartError { get; set; }
    public List<string> Folders { get; } = new();
    public int Starts { get; set; }

    public SyncTask FindSyncTask(string folderPath)
    {
        Folders.Add(folderPath);
        return Task;
    }

    public void Start(SyncTask task)
    {
        if (StartError != null) { throw StartError; }
        Starts++;
    }
}

internal sealed class FakeEventLog : IEventLogReader
{
    public Func<string, IReadOnlyList<EventInfo>> Events { get; set; } = _ => Array.Empty<EventInfo>();

    /// <summary>The events of a log, filtered as the real reader does: by event ID, and by time when the event has one.</summary>
    public IReadOnlyList<EventInfo> Read(string logName, DateTime sinceUtc, int max, int eventId = 0) =>
        Events(logName).Where(e => (eventId <= 0 || e.Id == eventId) && (e.TimeCreatedUtc is not DateTime t || t >= sinceUtc)).ToList();
}

/// <summary>A Microsoft Defender operational log of the test's own: its events, its oldest event and its accounts.</summary>
internal sealed class FakeDefenderEvents : IDefenderEventSource
{
    public List<DefenderEvent> Events { get; } = new();

    /// <summary>Thrown by Read (a log that cannot be read).</summary>
    public Exception Fails { get; set; }

    public DateTime? Oldest { get; set; }

    /// <summary>Account names and their SIDs; a name that is not here cannot be resolved.</summary>
    public Dictionary<string, string> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DefenderEvent> Read(IReadOnlyList<int> ids, DateTime sinceUtc, int max)
    {
        if (Fails != null) { throw Fails; }
        return Events.Where(e => ids.Contains(e.Id) && e.TimeCreatedUtc >= sinceUtc).OrderByDescending(e => e.TimeCreatedUtc).Take(max).ToList();
    }

    public DateTime? OldestUtc() => Oldest;

    public string SidOf(string account) => Accounts.TryGetValue(account, out var sid) ? sid : null;

    /// <summary>The name of a SID; null unless <see cref="ResolveNames"/> is on (as when Windows cannot look it up).</summary>
    public string NameOf(string sid) => Names.TryGetValue(sid ?? "", out var name) ? name : ResolveNames ? Accounts.FirstOrDefault(a => string.Equals(a.Value, sid, StringComparison.OrdinalIgnoreCase)).Key : null;

    /// <summary>Names a SID is given back as, before <see cref="Accounts"/> (a name that resolves to another SID).</summary>
    public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool ResolveNames { get; set; }
}

internal sealed class FakeDeviceInfo : IDeviceInfo
{
    public Func<BitLockerVolumeInfo> BitLocker { get; set; } = () => throw new BitLockerUnavailableException(BitLockerProblem.NotAvailable);
    public Queue<DefenderInfo> Defender { get; } = new();

    public BitLockerVolumeInfo ReadBitLocker(string driveLetter) => BitLocker();

    public DefenderInfo ReadDefender() => Defender.Count > 0 ? Defender.Dequeue() : null;
}

/// <summary>An action handler for the run frame tests.</summary>
internal sealed class DelegateHandler : IActionHandler
{
    private readonly Func<RunContext, int?> _run;

    public DelegateHandler(Func<RunContext, int?> run) => _run = run;

    public int? Run(RunContext run) => _run(run);
}

/// <summary>
/// Network adapters as a list the test sets, and a lease renewer that answers as the test says
/// (and records the indexes it was asked for). By default it refuses to run: nothing renews for real.
/// </summary>
internal sealed class FakeNetwork : INetworkAdapters, ILeaseRenewer
{
    public List<NetAdapter> Adapters { get; } = new();
    public List<int> Renewed { get; } = new();
    public Func<int, RenewCall> Answer { get; set; } = _ => throw new InvalidOperationException("must not renew");

    /// <summary>Called after a renew: the adapters as Windows has them then (the test changes them here).</summary>
    public Action<int> AfterRenew { get; set; } = _ => { };

    public IReadOnlyList<NetAdapter> Read() => Adapters.ToList();

    public RenewCall Renew(int interfaceIndex, TimeSpan limit, out string detail)
    {
        Renewed.Add(interfaceIndex);
        var call = Answer(interfaceIndex);
        AfterRenew(interfaceIndex);
        detail = call == RenewCall.Failed ? "RenewDHCPLease returned 82." : call == RenewCall.TimedOut ? "Windows did not finish renewing within 90 seconds." : "RenewDHCPLease returned 0.";
        return call;
    }
}

/// <summary>The MDM WMI Bridge's AppInstallation nodes and installed versions, per scope (null: the device).</summary>
internal sealed class FakeModernApps : EndpointToolbelt.Engine.Ime.IModernAppSource
{
    public Dictionary<string, List<EndpointToolbelt.Engine.Ime.ModernAppInstallation>> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Exception DeviceFails { get; set; }
    public Exception UserFails { get; set; }
    public List<string> Asked { get; } = new();

    private static string Key(string sid) => sid ?? "device";

    public void Add(string sid, string family, int? status, long? lastError = null)
    {
        if (!Nodes.TryGetValue(Key(sid), out var list)) { Nodes[Key(sid)] = list = new(); }
        list.Add(new EndpointToolbelt.Engine.Ime.ModernAppInstallation { InstanceId = family, Status = status, LastError = lastError });
    }

    public IReadOnlyList<EndpointToolbelt.Engine.Ime.ModernAppInstallation> Installations(string userSid)
    {
        Asked.Add(Key(userSid));
        if (userSid == null && DeviceFails != null) { throw DeviceFails; }
        if (userSid != null && UserFails != null) { throw UserFails; }
        return Nodes.TryGetValue(Key(userSid), out var list) ? list : new();
    }

    public string InstalledVersion(string userSid, string packageFamilyName)
        => Versions.TryGetValue(Key(userSid) + "|" + packageFamilyName, out var version) ? version : "";
}

/// <summary>Engine services with fakes, collecting the records the engine writes.</summary>
internal sealed class EngineHarness
{
    public EngineHarness()
    {
        Services = new EngineServices
        {
            Output = line => { lock (Lines) { Lines.Add(line); } },
            ErrorOutput = line => { lock (Errors) { Errors.Add(line); } },
            Clock = Clock,
            Registry = Registry,
            Processes = Processes,
            ServiceControl = ServiceControl,
            EventLog = EventLog,
            Device = Device,
            FreeBytes = _ => FreeBytes.Count > 0 ? FreeBytes.Dequeue() : null,
            IsProcessRunning = name => RunningProcesses.Contains(name),
            BootUtc = () => BootUtc,
            GetEnvironmentVariable = name => name == "SystemDrive" ? "C:" : Environment.GetEnvironmentVariable(name),
            MdmEvents = MdmEvents,
            ProcessControl = ProcessControl,
            TaskScheduler = TaskScheduler,
            WindowsUpdate = WindowsUpdate,
            // Never this machine's Microsoft Defender log.
            DefenderEvents = DefenderEvents,
            // Never this machine's network adapters.
            Network = Network,
            LeaseRenewer = Network,
            // Never this machine's MDM WMI Bridge.
            ModernApps = ModernApps,
            RunAndWait = (file, args, ms) => { Started.Add(file + " " + args); return RunAndWaitResult(file, args); },
            TurnOnRedirectionGuard = () => false,
            // Nothing here points at this machine's IME: every key and folder is the test's own.
            ImePaths = new EndpointToolbelt.Engine.Ime.ImePaths
            {
                Win32AppsKey = @"TEST\Win32Apps",
                SideCarKey = @"TEST\SideCarPolicies",
                StatusReportsKey = @"TEST\SideCarPolicies\StatusServiceReports",
                ProfileListKey = @"TEST\ProfileList",
                EnrollmentsKey = @"TEST\Enrollments",
                DesktopAppsKey = @"TEST\EnterpriseDesktopAppManagement",
                UninstallKeys = new[] { @"TEST\Uninstall", @"TEST\WOW6432Node\Uninstall" },
                ImeBasePath = Path.Combine(Path.GetTempPath(), "etb-no-ime-" + Guid.NewGuid().ToString("N")),
                ContentCache = Path.Combine(Path.GetTempPath(), "etb-no-imecache-" + Guid.NewGuid().ToString("N")),
                LogsFolder = Path.Combine(Path.GetTempPath(), "etb-no-imelogs-" + Guid.NewGuid().ToString("N")),
                PatternFile = Path.Combine(RepositoryRoot(), "src", "Engine", "ImeLogPatterns.json"),
                DeviceEnroller = Path.Combine(Path.GetTempPath(), "etb-no-deviceenroller-" + Guid.NewGuid().ToString("N") + ".exe"),
            },
        };
        Registry.Put(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", "Windows 10 Enterprise");
        Registry.Put(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "26100");
        Registry.Put(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", 4061);
        Registry.Put(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", "24H2");
        Registry.Put(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", "Enterprise");
    }

    public List<string> Lines { get; } = new();
    public List<string> Errors { get; } = new();
    public FakeClock Clock { get; } = new();
    public FakeRegistry Registry { get; } = new();
    public FakeProcessRunner Processes { get; } = new();
    public FakeServiceControl ServiceControl { get; } = new();
    public FakeEventLog EventLog { get; } = new();
    public FakeDeviceInfo Device { get; } = new();
    public Queue<long?> FreeBytes { get; } = new();
    public HashSet<string> RunningProcesses { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When Windows "started"; unknown unless a test sets it.</summary>
    public DateTime? BootUtc { get; set; }
    public FakeMdmEvents MdmEvents { get; } = new();
    public FakeProcessControl ProcessControl { get; } = new();
    public FakeTaskScheduler TaskScheduler { get; } = new();
    public FakeWindowsUpdate WindowsUpdate { get; } = new();
    public FakeDefenderEvents DefenderEvents { get; } = new();
    public FakeNetwork Network { get; } = new();
    public FakeModernApps ModernApps { get; } = new();

    /// <summary>Programs "started" without capture (deviceenroller), and what they return.</summary>
    public List<string> Started { get; } = new();
    public Func<string, string, int?> RunAndWaitResult { get; set; } = (_, _) => throw new InvalidOperationException("must not run");

    public EngineServices Services { get; }

    public static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public RunContext NewRun(string runId = "test0000", string action = "Test") => new(Services, runId, action);

    public List<JsonNode> Records() => Lines.Select(JsonNode.Parse).ToList();

    public List<JsonNode> Records(string type) => Records().Where(r => r.Get("type").Text == type).ToList();

    public List<JsonNode> Findings(string id) => Records("finding").Where(r => r.Get("id").Text == id).ToList();

    public static string WriteRequest(TestFolder folder, string name, string json)
    {
        var path = folder.Combine(name);
        File.WriteAllText(path, json);
        return path;
    }
}

internal static class JsonNodeExtensions
{
    public static string Str(this JsonNode node, string name) => node.Get(name)?.Text;
}
