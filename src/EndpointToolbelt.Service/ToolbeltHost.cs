using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Service.Configuration;
using EndpointToolbelt.Service.Engine;
using EndpointToolbelt.Service.Logging;
using EndpointToolbelt.Service.Pipe;
using EndpointToolbelt.Platform;
using EndpointToolbelt.Service.Platform;
using EndpointToolbelt.Service.Rules;
using EndpointToolbelt.Service.State;

namespace EndpointToolbelt.Service;

/// <summary>Wires the service together. Used by the Windows service and by console runs.</summary>
public sealed class ToolbeltHost : IDisposable
{
    /// <summary>How long a stop waits for a cancelled run to store its result.</summary>
    public static readonly TimeSpan RunStopWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Upper bound of <see cref="Stop"/>: accept-thread join, ShuttingDown broadcast and the run
    /// wait (which overlaps them, counted as if it did not), with a margin. Stays below 25 seconds
    /// so an upgrade or SCM stop is not timed out.
    /// </summary>
    public static readonly TimeSpan MaxStopTime = PipeServer.AcceptJoinTimeout + PipeServer.BroadcastOverallTimeout + RunStopWait + TimeSpan.FromSeconds(2);

    private readonly ServicePaths _paths;
    private readonly string _pipeName;
    private readonly string _pipeSddl;
    private readonly bool _production;
    private readonly IEventWriter _events = new EventLogWriter();
    private PipeServer _server;
    private ActionCoordinator _coordinator;
    private IServiceLog _log;
    private TrustedCallerPipe _callerPipe;
    private Timer _callerRefresh;
    private AppNameCollector _appNames;

    /// <summary>How often the trusted service caller policy is read again.</summary>
    public static readonly TimeSpan CallerRefreshInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long the end of a run waits for the readouts read after it (they go on in the background after that).</summary>
    public static readonly TimeSpan RunEndRefreshWait = TimeSpan.FromSeconds(20);

    /// <param name="production">
    /// True for the Windows service: the working folder must be SYSTEM-only and the engine files
    /// must be protected. False for console development runs in a per-user folder.
    /// </param>
    public ToolbeltHost(ServicePaths paths, string pipeName = ProductInfo.PipeName, string pipeSddl = PipeServer.DefaultSddl, bool production = true)
    {
        _paths = paths;
        _pipeName = pipeName;
        _pipeSddl = pipeSddl;
        _production = production;
    }

    /// <summary>Raised when the service can no longer serve requests and should stop.</summary>
    public event Action<string> Faulted;

    /// <summary>
    /// Sets the service's recovery actions at start (the Windows service only); returns null on
    /// success or why not. See <see cref="Platform.ServiceRecovery"/>.
    /// </summary>
    public Func<string> ConfigureRecovery { get; set; }

    public static string ProductVersion =>
        typeof(ToolbeltHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    public void Start()
    {
        if (_production)
        {
            FileSystemSafety.EnsureProtectedFolder(_paths.WorkingRoot, FolderPolicy.SystemOnly);
            CleanUpSiblingLeftovers(_paths.WorkingRoot);
        }
        else
        {
            Directory.CreateDirectory(_paths.WorkingRoot);
        }

        // Subfolders must simply inherit the root's DACL; anything else is replaced.
        FileSystemSafety.EnsureInheritingFolder(_paths.FallbackLogFolder);
        FileSystemSafety.EnsureInheritingFolder(_paths.ResultsFolder);
        FileSystemSafety.EnsureInheritingFolder(_paths.LimitsFolder);
        // Leftovers from a run that was interrupted (service stop, power loss).
        if (Directory.Exists(_paths.WorkFolder)) { FileSystemSafety.DeleteTreeNoFollow(_paths.WorkFolder); }
        Directory.CreateDirectory(_paths.WorkFolder);

        var location = new LogLocation(LogLocation.ImeLogsFolder, _paths.FallbackLogFolder, reason =>
        {
            var message = $"The IME Logs folder is not used for logging ({reason}); logs are written to the service's working folder.";
            _events.Write(EndpointToolbelt.EventId.LogFolderRejected, EventLogEntryType.Warning, message);
        });
        _log = new FolderCheckedLog(location, ProductInfo.ServiceLogFileName, "Service");
        _log.Info($"{ProductInfo.ServiceDisplayName} {ProductVersion} starting.");
        if (_production)
        {
            // The service runs as SYSTEM from its install folder. Installed where a user can
            // change a file (for example a folder below C:\ that inherits its ACL), it refuses to
            // start; the reason goes to the event log (1020) and to this log.
            var installReason = FileSystemSafety.ValidateInstallFolder(AppDomain.CurrentDomain.BaseDirectory);
            if (installReason != null)
            {
                _log.Error("Not starting: " + installReason + ". Install the product in Program Files.");
                throw new InvalidOperationException("The install folder is not protected: " + installReason + ". Install the product in Program Files.");
            }
            // Before the configuration is read: only SYSTEM and Administrators may read or change it.
            try
            {
                var changed = ProductRegistryKeys.Enforce();
                if (changed.Count > 0) { _log.Info("Registry keys protected (SYSTEM and Administrators only): " + string.Join(", ", changed) + "."); }
            }
            catch (Exception ex)
            {
                _log.Warn("The product's registry keys could not be protected: " + ex.Message);
            }
        }
        if (ConfigureRecovery != null)
        {
            string recovery;
            try { recovery = ConfigureRecovery(); } catch (Exception ex) { recovery = ex.Message; }
            if (recovery == null) { _log.Info("Recovery actions set: restart after 1, 1 and 5 minutes, also after a stop with an error."); }
            else { _log.Warn("Recovery actions: " + recovery); }
        }

        var registry = new RegistryReader();
        var config = new ConfigProvider(registry, TimeSpan.FromSeconds(30), w => _log.Warn("Configuration: " + w));
        var multiSessionHost = MultiSessionHost.Detect(registry);
        if (multiSessionHost) { _log.Info("Multi-session host: support packages are limited per user."); }

        // The IME's own log is read where the IME writes it, not where this product logs; the
        // pattern table ships with the engine (its tree is checked before every run).
        var patternFile = Path.Combine(_paths.EngineFolder, "ImeLogPatterns.json");
        // The policy lines in the log text the check-in read already loaded fill the app name cache.
        var appNames = _paths.AppNameCacheFile;
        var checkIn = new ImeCheckInReader(() => LogLocation.ImeLogsFolder, () => ImeLogPatternTable.Load(patternFile),
            onPolicyNames: (seen, now) => AppNameCache.Update(appNames, seen, now, AppNameCache.IsSystemOnlyFolder));
        var readouts = new DeviceReadoutReader();

        _coordinator = new ActionCoordinator(
            _paths,
            new JobObjectEngineRunner(),
            new ResultStore(_paths.ResultsFolder),
            _log,
            _events,
            config.Get,
            new RateLimiter(TimeSpan.FromHours(1), statePath: Path.Combine(_paths.LimitsFolder, "actions.txt")),
            ProductVersion,
            () => location.Current,
            _production ? FileSystemSafety.CheckEngineExecutable : _ => null,
            isMultiSessionHost: () => multiSessionHost,
            packageLimiter: new RateLimiter(TimeSpan.FromHours(1), statePath: Path.Combine(_paths.LimitsFolder, "packages.txt")),
            // The read-only app diagnosis has a limit of its own per caller.
            readOnlyLimiter: new RateLimiter(TimeSpan.FromHours(1), statePath: Path.Combine(_paths.LimitsFolder, "diagnoses.txt")),
            // A run that may have changed the device reads the readouts again before it reports its end.
            runEnded: _ =>
            {
                checkIn.Invalidate();
                readouts.RefreshAfterRun(RunEndRefreshWait);
            });
        if (!_production) { _log.Warn("Development run: the engine and its folder are not checked for protection."); }
        // Before the pipe opens, so no new run can be mistaken for an interrupted one.
        _coordinator.RecoverInterruptedRuns();

        // Read once now, so the first status a user asks for already has them.
        readouts.StartRefresh();
        var handler = new RequestHandler(_coordinator, new WtsSessionCounter(), new DeviceStateReader(registry), config.Get, _events, ProductVersion, checkIn, readouts);
        _server = new PipeServer(_pipeName, _pipeSddl, handler, _log);
        _server.Faulted += reason =>
        {
            _log.Error(reason);
            _events.Write(EndpointToolbelt.EventId.ServiceError, EventLogEntryType.Error, reason);
            Faulted?.Invoke(reason);
        };
        try
        {
            _server.Start();
        }
        catch (PipeNameInUseException ex)
        {
            _log.Error(ex.Message);
            _events.Write(EndpointToolbelt.EventId.PipeNameInUse, EventLogEntryType.Error, ex.Message + " The service cannot accept requests.");
            throw;
        }
        _log.Info("Listening.");

        // The trusted service caller pipe exists only while the policy names a service.
        // Without the policy value nothing is created and the service behaves as before.
        _callerPipe = new TrustedCallerPipe(handler, _log, _events);
        RefreshCallerPipe(config);
        _callerRefresh = new Timer(_ => RefreshCallerPipe(config), null, CallerRefreshInterval, CallerRefreshInterval);

        // The app name cache is also kept up to date while the app is
        // closed, from the IME logs' policy lines once an hour, in the background.
        var log = _log;
        _appNames = new AppNameCollector(() => LogLocation.ImeLogsFolder, _paths.AppNameCacheFile, AppNameCache.IsSystemOnlyFolder, m => log.Warn(m));
        _appNames.Start();
    }

    /// <summary>
    /// Stops within <see cref="MaxStopTime"/>; safe to call more than once and from several
    /// threads (only the first call stops). See <see cref="StopSequence"/> for the order.
    /// </summary>
    /// <param name="notifyApps">
    /// False when the service stops after a pipe fault (the service control manager's recovery
    /// restarts it, so apps stay open and reconnect) and at system shutdown (the apps close with
    /// the session anyway, and the time is short).
    /// </param>
    public void Stop(bool notifyApps = true)
    {
        var server = Interlocked.Exchange(ref _server, null);
        if (server == null) { return; }
        var coordinator = _coordinator;
        var callerPipe = _callerPipe;
        _callerRefresh?.Dispose();
        _appNames?.Dispose();
        _log?.Info(notifyApps ? "Stopping." : "Stopping without asking the apps to close.");
        StopSequence(
            () => coordinator?.BeginShutdown(),
            () =>
            {
                server.StopAccepting();
                callerPipe?.StopAccepting();
            },
            notifyApps ? () => server.Broadcast(new Response { Type = ResponseType.ShuttingDown, Message = "The " + ProductInfo.DisplayName + " service is stopping." }) : null,
            wait => coordinator?.WaitIdle(wait) ?? true,
            () =>
            {
                server.CloseAll();
                callerPipe?.Dispose();
            });
        _log?.Info("Stopped.");
    }

    /// <summary>
    /// Stop order: cancel a running engine first (it needs the most time to wind down), refuse
    /// new connections, tell connected apps (bounded), wait for the run's result to be stored
    /// (up to <see cref="RunStopWait"/> counted from the cancel), then close all connections.
    /// </summary>
    internal static void StopSequence(Action beginShutdown, Action stopAccepting, Action broadcast, Func<TimeSpan, bool> waitIdle, Action closeAll)
    {
        var sinceCancel = Stopwatch.StartNew();
        beginShutdown();
        stopAccepting();
        broadcast?.Invoke();
        waitIdle(RunStopWait - sinceCancel.Elapsed);
        closeAll();
    }
    public void Dispose() => Stop();

    /// <summary>Opens, keeps or closes the trusted service caller pipe to match the policy. Never throws.</summary>
    private void RefreshCallerPipe(ConfigProvider config)
    {
        try
        {
            if (_server == null) { return; }
            _callerPipe?.Refresh(config.Get().TrustedServiceCaller);
        }
        catch (Exception ex)
        {
            _log?.Warn("Trusted service caller pipe: " + ex.Message);
        }
    }

    /// <summary>
    /// Removes "<root>.new-*" folders left by an interrupted creation, and "<root>.rejected-*"
    /// folders that are owned by SYSTEM or Administrators (they may hold old results). Rejected
    /// folders with another owner are left alone: SYSTEM never deletes inside a tree someone
    /// else controls.
    /// </summary>
    internal static void CleanUpSiblingLeftovers(string root)
    {
        var parent = Path.GetDirectoryName(root);
        var name = Path.GetFileName(root);
        if (parent == null || !Directory.Exists(parent)) { return; }
        foreach (var dir in Directory.EnumerateDirectories(parent, name + ".*"))
        {
            var suffix = Path.GetFileName(dir).Substring(name.Length);
            if (!suffix.StartsWith(".new-", StringComparison.Ordinal) && !suffix.StartsWith(".rejected-", StringComparison.Ordinal)) { continue; }
            try
            {
                var info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    FileSystemSafety.DeleteTreeNoFollow(dir); // removes the link only
                    continue;
                }
                var owner = info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (owner != null && (owner.Equals(WellKnownSids.LocalSystem) || owner.Equals(WellKnownSids.Administrators)))
                {
                    FileSystemSafety.DeleteTreeNoFollow(dir);
                }
            }
            catch (Exception)
            {
                // Best effort; never block the service start.
            }
        }
    }
}
