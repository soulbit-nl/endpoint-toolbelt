using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.WindowsUpdate;

// The repairs: the cache reset, the component store check and repair (DISM and SFC), and the
// removal of disconnected devices and stale packages.
public partial class WindowsUpdateRepair
{
    private static readonly RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan MatchLimit = TimeSpan.FromSeconds(2);

    private static string Dism => ProcessRunner.SystemTool("dism.exe");

    /// <summary>
    /// Waits (bounded) while the servicing stack is busy. Returns false when it is still busy after
    /// the wait; the caller decides whether to go on.
    /// </summary>
    public virtual bool WaitServicingIdle() => Servicing.WaitIdle(Run);

    /// <summary>The running Windows version.</summary>
    public virtual OsVersionInfo OsVersion() => Services.ReadOsVersion(Services);

    // --- Files ------------------------------------------------------------------------------------

    /// <summary>The full paths of the items directly in a folder; throws when it cannot be listed.</summary>
    public virtual IReadOnlyList<string> ListFolder(string path) =>
        new DirectoryInfo(path).GetFileSystemInfos().Select(i => i.FullName).ToList();

    /// <summary>The folders directly in a folder whose name matches a wildcard, as (name, full path); none when it cannot be listed.</summary>
    public virtual IReadOnlyList<KeyValuePair<string, string>> ListDirectories(string parent, string filter)
    {
        try
        {
            return new DirectoryInfo(parent).GetDirectories(filter).Select(d => new KeyValuePair<string, string>(d.Name, d.FullName)).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }
    }

    public virtual bool FolderExists(string path) => Directory.Exists(path);

    /// <summary>True when the item is a junction or a symbolic link; throws when it does not exist.</summary>
    public virtual bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>Deletes a file or folder tree without following links (a link is removed as a link).</summary>
    public virtual void DeleteTree(string path) => ImeRefresh.DeleteTree(path);

    /// <summary>
    /// One-time cleanup: SoftwareDistribution.old-* and catroot2.old-* folders that earlier versions
    /// of this repair renamed and could not delete. Nothing renames these folders any more, but
    /// devices may still have them. Deleted without following links.
    /// </summary>
    public virtual void RemoveOldCacheFolders()
    {
        foreach (var spec in new[] { Tuple.Create(SystemRoot, "SoftwareDistribution.old-*"), Tuple.Create(Path.Combine(SystemRoot, "System32"), "catroot2.old-*") })
        {
            foreach (var folder in ListDirectories(spec.Item1, spec.Item2))
            {
                if (!WuCodes.OldCacheFolder.IsMatch(folder.Key)) { continue; }
                if (Run.MinutesLeft() < RunContext.MinStepMinutes) { return; }
                try
                {
                    DeleteTree(folder.Value);
                    Run.Log(string.Format(Invariant, "Removed {0}, left by an earlier version of this repair.", folder.Key));
                }
                catch (Exception)
                {
                    Run.Log(string.Format(Invariant, "Could not remove {0} yet (in use); a run after a restart removes it.", folder.Key), LogSeverity.Warning);
                }
            }
        }
    }

    // --- Cache reset ------------------------------------------------------------------------------

    /// <summary>
    /// Why the update services must not be stopped because other people use this device, or ""
    /// when nothing speaks against it: other signed-in user sessions (counted by the service; a
    /// count it could not make is treated as signed in, like every other gate), or a multi-session
    /// host, where users can sign in at any moment: EditionID ServerRdsh or an edition whose name
    /// contains MultiSession, or Windows Server with the RD Session Host role.
    /// </summary>
    public virtual string SharedDevice()
    {
        var others = Run.OtherSessions;
        if (others > 0) { return string.Format(Invariant, "other users are signed in to this device ({0})", TextFormat.Count(others, "other session")); }
        if (others < 0) { return "the number of other signed-in users could not be determined"; }
        var edition = OsVersion().Edition ?? "";
        if (edition == "ServerRdsh" || edition.IndexOf("MultiSession", StringComparison.OrdinalIgnoreCase) >= 0 || RdsHostRole())
        {
            return "this is a multi-session host, shared by several users";
        }
        return "";
    }

    /// <summary>
    /// Windows Server with the RD Session Host role: Terminal Server application compatibility mode
    /// is on (TSAppCompat = 1). False on client Windows or when it cannot be read.
    /// </summary>
    public virtual bool RdsHostRole()
    {
        try
        {
            var type = MachineRegistry.AsText(Services.Registry.GetValue(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "InstallationType")) ?? "";
            if (!type.StartsWith("Server", StringComparison.OrdinalIgnoreCase)) { return false; }
            var value = Services.Registry.GetValue(@"SYSTEM\CurrentControlSet\Control\Terminal Server", "TSAppCompat");
            return value != null && Convert.ToInt32(value, Invariant) == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether an update is being installed right now. The checks are the Windows module installer
    /// (TiWorker, the process that applies updates) and the update client itself
    /// (UpdateInstaller.IsBusy, asked through the worker). The Update Orchestrator has no
    /// documented, read-only install state, so it is not asked. The repair already waited for
    /// TiWorker at the start of its repair step and remembered the outcome in ServicingBusy; that
    /// wait is not repeated here. An update client that cannot be asked does not block on its own:
    /// the module installer check covers the part of an install that must not be interrupted, and a
    /// hung update client is exactly what a reset is for.
    /// </summary>
    public virtual InstallBusy InstallBusyCheck()
    {
        var servicingBusy = ServicingBusy;
        if (servicingBusy == null)
        {
            servicingBusy = !WaitServicingIdle();
            ServicingBusy = servicingBusy;
        }
        if (servicingBusy.Value) { return new InstallBusy { Busy = true, Reason = "the Windows module installer (TiWorker) is still applying an update" }; }
        if (ApiHung)
        {
            Run.Log("The update client was not asked whether it is installing: a Windows Update call already had to be stopped on this run.", LogSeverity.Warning);
            return new InstallBusy();
        }
        // Capped by the run's time left, keeping the minimum for the steps after it.
        var limit = Math.Min(WuCodes.BusyCheckMinutes, Math.Max(0, Run.MinutesLeft() - RunContext.MinStepMinutes));
        if (limit <= 0)
        {
            Run.Log("The update client was not asked whether it is installing: this run has no time left for it.", LogSeverity.Warning);
            return new InstallBusy();
        }
        var raw = RunWorker("Busy", "", limit);
        if (!raw.Ok)
        {
            Run.Log(string.Format(Invariant, "Could not ask the update client whether it is installing ({0}); the module installer is idle, so the reset goes ahead.", raw.Error), LogSeverity.Warning);
            return new InstallBusy();
        }
        return raw.Busy ? new InstallBusy { Busy = true, Reason = "the update client is installing an update" } : new InstallBusy();
    }

    /// <summary>
    /// Deletes everything inside one folder (the folder itself stays) without following links, in up
    /// to three passes with a short pause for files that are still in use. Before every pass
    /// beforePass runs; when it returns false (a cache service runs again and could not be stopped)
    /// nothing more is deleted. No pass starts after the deadline (on the run's monotonic clock), and
    /// a pass stops there.
    /// </summary>
    public virtual FolderClearResult ClearFolderContent(string path, TimeSpan deadline, Func<bool> beforePass = null)
    {
        var result = new FolderClearResult();
        var pauses = new[] { 2, 5 };
        var clock = Services.Clock;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (clock.Elapsed >= deadline) { result.StoppedBy = "time"; break; }
            if (beforePass != null && !beforePass()) { result.StoppedBy = "services"; break; }
            var items = ListFolder(path);
            result.Remaining = 0;
            for (var i = 0; i < items.Count; i++)
            {
                if (clock.Elapsed >= deadline)
                {
                    result.Remaining += items.Count - i;
                    result.StoppedBy = "time";
                    break;
                }
                try
                {
                    DeleteTree(items[i]);
                    result.Removed++;
                }
                catch (Exception)
                {
                    result.Remaining++;
                }
            }
            if (result.StoppedBy.Length > 0 || result.Remaining == 0 || attempt == 3) { break; }
            Run.Log(string.Format(Invariant, "{0} still in use; trying again in {1} seconds.", TextFormat.Count(result.Remaining, "item is", "items are"), pauses[attempt - 1]));
            clock.Sleep(TimeSpan.FromSeconds(pauses[attempt - 1]));
        }
        if (result.StoppedBy.Length > 0 && result.Remaining == 0)
        {
            // Stopped before a pass: whatever is in the folder is left.
            try { result.Remaining = ListFolder(path).Count; }
            catch (Exception) { result.Remaining = 0; }
        }
        return result;
    }

    /// <summary>Stops one service with a bounded wait; true when it is stopped. status is the state it was last read in.</summary>
    public virtual bool StopCacheService(string name, string status)
    {
        var control = Services.ServiceControl;
        try
        {
            if (status == "Stopped") { return true; }
            if (status != "StopPending") { control.RequestStop(name); }
            control.WaitForStatus(name, "Stopped", TimeSpan.FromSeconds(30));
            return true;
        }
        catch (Exception ex)
        {
            Run.Log(string.Format(Invariant, "Could not stop {0}: {1}", name, ex.Message), LogSeverity.Warning);
            return false;
        }
    }

    /// <summary>
    /// Clears the Windows Update download cache: the contents of SoftwareDistribution\Download, with
    /// Windows Update (wuauserv), BITS and Delivery Optimization (DoSvc) stopped, and the services
    /// this stopped started again afterwards, also when a step fails or the time runs out.
    /// includeDataStore also clears SoftwareDistribution\DataStore, the update database. That is done
    /// only on evidence (a 0x80248xxx code, or a search that still fails right after the download
    /// cache was cleared), because it also removes the update history: the currency check then falls
    /// back to the hotfix list until the next update installs. catroot2 and Windows Installer are
    /// never touched.
    ///
    /// Not done on a shared device, while an update is being installed, or without enough of the
    /// run's time left. Every service is checked first: when one has running dependents outside
    /// this set, none is stopped. Then wuauserv is stopped first, and the first service that does
    /// not stop ends the stopping. Before each folder and each delete pass the services are read
    /// again: one that runs again (Windows Update Medic can start them at any time) is stopped once
    /// more; when that fails nothing more is deleted. Files still in use after three passes are
    /// reported, not fought over.
    /// </summary>
    public virtual CacheResetResult CacheReset(bool includeDataStore = false)
    {
        var result = new CacheResetResult();
        var what = includeDataStore ? "the Windows Update download cache and update database (DataStore)" : "the Windows Update download cache";

        var shared = SharedDevice();
        if (shared.Length > 0)
        {
            result.Skipped = true;
            Run.Log(string.Format(Invariant, "Clearing {0} is skipped: {1}. It would stop the update services for everyone on this device.", what, shared));
            Run.AddFinding(FindingLevel.Info, "WU-CACHE-RESET-SKIPPED", "The update cache was not cleared because other users share this device",
                string.Format(Invariant, "Clearing the cache stops the update services for every user of the device, so it is not done here ({0}).", shared),
                "If updates keep failing, contact your IT support.",
                "Clear the contents of SoftwareDistribution\\Download while nobody else uses the device (for example in a maintenance window, with the session host drained), or run this repair then.");
            return result;
        }
        // The services stay stopped for the deletes; leave the minimum for what follows.
        var needed = RunContext.MinStepMinutes + WuCodes.CacheResetMarginMinutes;
        var minutesLeft = Run.MinutesLeft();
        if (minutesLeft < needed)
        {
            result.Skipped = true;
            result.Warnings++;
            Run.Log(string.Format(Invariant, "Clearing {0} is skipped: {1} of this run left, and it needs at least {2}.", what,
                TextFormat.Count(Math.Max(0, minutesLeft), "minute"), TextFormat.Count(needed, "minute")), LogSeverity.Warning);
            Run.AddFinding(FindingLevel.Warning, "WU-CACHE-RESET-NO-TIME", "The update cache was not cleared because this run had no time left",
                "Clearing the cache stops the update services; it is not started when the run could end before they are started again.",
                "Run the repair again.");
            return result;
        }
        var busy = InstallBusyCheck();
        if (busy.Busy)
        {
            result.Skipped = true;
            result.Warnings++;
            Run.Log(string.Format(Invariant, "Clearing {0} is skipped: {1}. Stopping the update services now could break that install.", what, busy.Reason), LogSeverity.Warning);
            Run.AddFinding(FindingLevel.Warning, "WU-CACHE-RESET-BUSY", "The update cache was not cleared because an update is being installed",
                string.Format(Invariant, "{0}; the update services were left running.", busy.Reason.Substring(0, 1).ToUpperInvariant() + busy.Reason.Substring(1)),
                "Let the installation finish, restart the device, then run the repair again.");
            return result;
        }

        Run.Log(string.Format(Invariant, "Clearing {0}.", what));
        RemoveOldCacheFolders();
        var root = Path.Combine(SystemRoot, "SoftwareDistribution");
        var folders = new List<string> { Path.Combine(root, "Download") };
        if (includeDataStore) { folders.Add(Path.Combine(root, "DataStore")); }
        // Deletes end here, so the services are started again within the run.
        var deadline = Services.Clock.Elapsed + TimeSpan.FromMinutes(Math.Max(1, Run.MinutesLeft() - RunContext.MinStepMinutes));
        var control = Services.ServiceControl;

        var stopped = new List<string>();
        var left = new List<string>();
        var cleared = new List<string>();
        try
        {
            // 1. Check every service before stopping any.
            var services = new List<KeyValuePair<string, string>>();
            var blocked = new List<string>();
            foreach (var name in WuCodes.CacheServices)
            {
                var status = control.Status(name);
                // Not installed on this edition: nothing to stop.
                if (status == null) { continue; }
                services.Add(new KeyValuePair<string, string>(name, status));
                if (status == "Stopped") { continue; }
                var dependents = control.Dependents(name).Where(d => d.Value != "Stopped" && !WuCodes.CacheServices.Contains(d.Key)).Select(d => d.Key).ToList();
                if (dependents.Count > 0)
                {
                    Run.Log(string.Format(Invariant, "{0} must stay running: other services depend on it and would stop with it ({1}).", name, string.Join(", ", dependents)), LogSeverity.Warning);
                    blocked.Add(name);
                }
            }
            var ready = blocked.Count == 0;
            if (!ready) { left.Add(string.Format(Invariant, "everything ({0} had to stay running, so no service was stopped)", string.Join(", ", blocked))); }

            // 2. Stop them in order; the first failure ends the stopping.
            if (ready)
            {
                foreach (var service in services)
                {
                    var name = service.Key;
                    if (service.Value == "Stopped") { continue; }
                    stopped.Add(name);
                    if (!StopCacheService(name, service.Value))
                    {
                        result.Warnings++;
                        left.Add(string.Format(Invariant, "everything ({0} did not stop)", name));
                        ready = false;
                        break;
                    }
                }
            }

            // 3. Before each folder and each pass: every cache service must still be stopped. One
            //    that runs again is stopped once more; a second time, or a failed stop, ends it.
            var restopped = new HashSet<string>(StringComparer.Ordinal);
            bool Check()
            {
                foreach (var name in services.Select(s => s.Key))
                {
                    var status = control.Status(name);
                    if (status == null || status == "Stopped") { continue; }
                    if (restopped.Contains(name))
                    {
                        Run.Log(string.Format(Invariant, "{0} was started again a second time; nothing more is deleted.", name), LogSeverity.Warning);
                        return false;
                    }
                    restopped.Add(name);
                    Run.Log(string.Format(Invariant, "{0} was started again (by Windows); stopping it once more.", name), LogSeverity.Warning);
                    if (!stopped.Contains(name)) { stopped.Add(name); }
                    if (!StopCacheService(name, status)) { return false; }
                }
                return true;
            }

            if (ready)
            {
                Services.Clock.Sleep(TimeSpan.FromSeconds(2));
                foreach (var folder in folders)
                {
                    var leaf = "SoftwareDistribution\\" + Path.GetFileName(folder);
                    try
                    {
                        if (!FolderExists(folder)) { continue; }
                        foreach (var path in new[] { root, folder })
                        {
                            if (IsLink(path)) { throw new IOException(path + " is a link"); }
                        }
                        var clear = ClearFolderContent(folder, deadline, Check);
                        if (clear.Removed > 0)
                        {
                            result.Changed = true;
                            cleared.Add(leaf);
                        }
                        if (clear.StoppedBy == "services")
                        {
                            result.Warnings++;
                            left.Add(leaf + " and what follows it (an update service kept starting again)");
                            break;
                        }
                        if (clear.StoppedBy == "time")
                        {
                            result.Warnings++;
                            left.Add(string.Format(Invariant, "{0} ({1}; this run ran out of time)", leaf, TextFormat.Count(clear.Remaining, "item")));
                            break;
                        }
                        if (clear.Remaining > 0)
                        {
                            left.Add(string.Format(Invariant, "{0} ({1} still in use)", leaf, TextFormat.Count(clear.Remaining, "item")));
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Run.Log(string.Format(Invariant, "Could not clear {0}: {1}", leaf, ex.Message), LogSeverity.Warning);
                        result.Warnings++;
                        left.Add(leaf);
                        break;
                    }
                }
            }
        }
        finally
        {
            // Start what this reset stopped, each with a bounded wait, whatever happened above.
            foreach (var name in stopped)
            {
                try
                {
                    var status = control.Status(name);
                    if (status == null)
                    {
                        Run.Log(string.Format(Invariant, "Could not start {0} again: Cannot find any service with service name '{0}'.", name), LogSeverity.Warning);
                        result.Warnings++;
                        continue;
                    }
                    if (status != "Running")
                    {
                        if (status != "StartPending") { control.RequestStart(name); }
                        control.WaitForStatus(name, "Running", TimeSpan.FromSeconds(60));
                    }
                }
                catch (Exception ex)
                {
                    Run.Log(string.Format(Invariant, "Could not start {0} again: {1}", name, ex.Message), LogSeverity.Warning);
                    result.Warnings++;
                }
            }
        }

        if (cleared.Count > 0) { Run.AddChange("Cleared the Windows Update cache: the contents of " + string.Join(" and ", cleared)); }
        if (cleared.Contains("SoftwareDistribution\\DataStore"))
        {
            Run.Log("The update database was cleared, and the update history with it: until the next update installs, the patch level is read from the hotfix list.");
        }
        if (left.Count > 0)
        {
            result.Warnings++;
            Run.Log(string.Format(Invariant, "The update cache was only partly cleared: {0} is left. Not tried again: Windows can start the update services again by itself at any time.", string.Join(", ", left)), LogSeverity.Warning);
            Run.AddFinding(FindingLevel.Warning, "WU-CACHE-RESET-PARTIAL", "The Windows Update cache was only partly cleared",
                string.Format(Invariant, "Left: {0}. Whatever needed a clean cache did not get one.", string.Join(", ", left)),
                "Restart the device and run the repair again; a restart releases what holds the files.",
                "If the files stay in use after a restart, find the process that holds them (security software, or Windows Update Medic restarting the services) and clear the folder in a maintenance window.");
        }
        else
        {
            result.Done = true;
            Run.Log(string.Format(Invariant, "Cleared {0}.", what));
        }
        return result;
    }

    // --- Component store --------------------------------------------------------------------------

    /// <summary>
    /// What a DISM exit code means: a plain DISM exit from the DISM table, otherwise the shared code
    /// table (an HRESULT such as 0x800F081F). Null when neither knows it.
    /// </summary>
    public static DismExitEntry DismExitKnowledge(long exitCode)
    {
        var key = CodeKnowledge.Key(exitCode.ToString(CultureInfo.InvariantCulture));
        if (key == null) { return null; }
        if (WuCodes.DismExitKnowledge.TryGetValue(key, out var entry))
        {
            return new DismExitEntry { Code = key, Name = entry[0], Meaning = entry[1], NextStep = entry[2], Support = entry.Length > 3 ? entry[3] : "" };
        }
        var shared = CodeKnowledge.Get(key);
        return shared == null ? null : new DismExitEntry { Code = shared.Code, Name = shared.Name, Meaning = shared.Meaning, NextStep = shared.NextStep, Support = shared.Support };
    }

    /// <summary>
    /// Component store check (DISM ScanHealth) and, only when it does not come back clean,
    /// RestoreHealth (from the repair source set by policy, otherwise Windows Update) and SFC. The
    /// component store (WinSxS) holds the files Windows builds every update from; DISM is the
    /// Windows image servicing tool and SFC the system file checker. RestartNeeded is set only on
    /// evidence: RestoreHealth exited with 3010, or SFC repaired files. Changed says that a repair
    /// ran (RestoreHealth started, or SFC repaired files). Completed is false when the check reached
    /// no answer (no time left, or ScanHealth stopped): nothing further is started behind it,
    /// because the servicing stack may still be busy. DISM is asked for English output, because its
    /// verdict is read from the text.
    /// </summary>
    public virtual ComponentRepairResult ComponentRepair()
    {
        var result = new ComponentRepairResult();

        var limit = Run.StepLimit(WuCodes.ScanHealthMinutes, "DISM ScanHealth");
        if (limit <= 0) { result.Warnings++; return result; }
        Run.Log("Checking the Windows component store (WinSxS, where Windows keeps the files updates are built from) with DISM ScanHealth; usually 2 to 15 minutes.");
        var scan = Run.RunTool(new ProcessRequest { FilePath = Dism, Arguments = "/Online /English /Cleanup-Image /ScanHealth", Label = "DISM ScanHealth", TimeoutMinutes = limit });
        if (scan.TimedOut)
        {
            result.Verdict = "TimedOut";
            result.Warnings++;
            Run.Log("ScanHealth was stopped on its time limit, so the state of the component store is not known. RestoreHealth and SFC are not started behind it.", LogSeverity.Warning);
            Run.AddFinding(FindingLevel.Warning, "WU-STORE-SCAN-TIMEOUT", "The component store check did not finish in time",
                string.Format(Invariant, "DISM ScanHealth was stopped after {0}, so the state of the store is not known and nothing was repaired.", TextFormat.Count(limit, "minute")),
                "Run the repair again when the device is idle and on power.");
            return result;
        }
        // A ScanHealth that could not even start counts as failed: the store state is unknown, so
        // the repair is attempted anyway.
        result.Completed = true;
        var output = scan.Output ?? "";
        if (scan.ExitCode != 0)
        {
            result.Verdict = "Failed";
            result.Warnings++;
            Run.Log("ScanHealth failed, so the state of the component store is not known. The DISM log is in the support package.", LogSeverity.Warning);
        }
        else if (Regex.IsMatch(output, "component store is repairable", MatchOptions, MatchLimit))
        {
            result.Verdict = "Repairable";
            Run.Log("Component store corruption was found.");
        }
        else if (Regex.IsMatch(output, "No component store corruption", MatchOptions, MatchLimit))
        {
            result.Verdict = "Clean";
        }
        else if (Regex.IsMatch(output, "component store is not repairable|component store cannot be repaired", MatchOptions, MatchLimit))
        {
            // Both wordings: the exact text for this case has not been captured on a device.
            result.Verdict = "NotRepairable";
            result.Warnings++;
            Run.Log("The component store is not repairable by DISM.", LogSeverity.Warning);
        }
        else
        {
            // Unrecognised text errs on the side of trying the repair.
            result.Verdict = "Unclear";
            Run.Log("ScanHealth finished, but its verdict line was not recognised.");
        }

        if (result.Verdict == "Clean")
        {
            Run.Log("No component store corruption; RestoreHealth and SFC are skipped.");
            Run.AddFinding(FindingLevel.Ok, "WU-STORE-CLEAN", "The Windows component store is healthy", "DISM ScanHealth found no corruption, so the store needed no repair.");
            return result;
        }
        if (result.Verdict == "NotRepairable")
        {
            Run.AddFinding(FindingLevel.Error, "WU-STORE-NOT-REPAIRABLE", "The Windows component store is damaged beyond what DISM can repair",
                "DISM ScanHealth reported the store as not repairable.",
                "Contact your IT support.",
                "An in-place upgrade from installation media repairs it and keeps apps and files.");
        }
        else if (result.Verdict == "Failed")
        {
            var scanText = TextFormat.ExitCode(scan.ExitCode);
            var scanKnowledge = DismExitKnowledge(scan.ExitCode);
            if (scanKnowledge != null) { scanText = string.Format(Invariant, "{0} ({1}: {2})", scanText, scanKnowledge.Name, scanKnowledge.Meaning); }
            Run.AddFinding(FindingLevel.Warning, "WU-STORE-SCAN-FAILED", "The component store check itself failed",
                string.Format(Invariant, "DISM ScanHealth ended with {0}; a repair is attempted anyway.", scanText),
                "If updates still fail, create a support package and contact your IT support.",
                "The DISM log in the support package has the cause.");
        }

        if (result.Verdict != "NotRepairable")
        {
            limit = Run.StepLimit(WuCodes.RestoreHealthMinutes, "DISM RestoreHealth");
            if (limit <= 0)
            {
                result.Warnings++;
            }
            else
            {
                Run.Log("Repairing the component store with DISM RestoreHealth, from the repair source set by policy or otherwise Windows Update; usually 10 to 30 minutes.");
                var restore = Run.RunTool(new ProcessRequest { FilePath = Dism, Arguments = "/Online /English /Cleanup-Image /RestoreHealth", Label = "DISM RestoreHealth", TimeoutMinutes = limit });
                if (restore.Started)
                {
                    result.Changed = true;
                    ChangedOwed = true;
                }
                if (restore.TimedOut)
                {
                    result.Warnings++;
                    Run.Log("RestoreHealth was stopped on its time limit; SFC is not started behind it.", LogSeverity.Warning);
                    return result;
                }
                if (!restore.Started)
                {
                    result.Warnings++;
                    Run.Log("DISM RestoreHealth could not be started.", LogSeverity.Warning);
                    Run.AddFinding(FindingLevel.Warning, "WU-STORE-REPAIR-NOT-STARTED", "The component store repair could not be started",
                        "DISM RestoreHealth did not start, so the store was not repaired.",
                        "Restart the device and run the repair again. If it repeats, create a support package and contact your IT support.");
                }
                else if (restore.ExitCode == 0 || restore.ExitCode == 3010)
                {
                    Run.AddChange("Repaired the Windows component store (DISM RestoreHealth)");
                    if (restore.ExitCode == 3010)
                    {
                        // Marked now, so the close-out still marks the restart when a later step fails.
                        result.RestartNeeded = true;
                        RestartOwed = true;
                        Run.Log("DISM reports that the repair needs a restart to complete.");
                    }
                }
                else
                {
                    result.Warnings++;
                    // Shown as the log shows it (decimal for a plain exit, hex for an HRESULT); the
                    // hex key is only for the lookup.
                    var code = TextFormat.ExitCode(restore.ExitCode);
                    Run.Log(string.Format(Invariant, "DISM RestoreHealth failed with {0}.", code), LogSeverity.Warning);
                    var knowledge = DismExitKnowledge(restore.ExitCode);
                    var detail = string.Format(Invariant, "DISM RestoreHealth ended with {0}.", code);
                    var next = "Create a support package and contact your IT support.";
                    var support = "The DISM log in the support package has the cause.";
                    if (knowledge != null)
                    {
                        detail = string.Format(Invariant, "DISM RestoreHealth ended with {0} ({1}): {2}", code, knowledge.Name, knowledge.Meaning);
                        next = knowledge.NextStep;
                        support = knowledge.Support;
                    }
                    Run.AddFinding(FindingLevel.Error, "WU-STORE-REPAIR-FAILED", "The component store could not be repaired", detail, next, support);
                }
            }
        }

        limit = Run.StepLimit(WuCodes.SfcMinutes, "SFC");
        if (limit <= 0) { result.Warnings++; return result; }
        Run.Log(string.Format(Invariant, "Checking the Windows system files with SFC /scannow (the system file checker); usually 5 to 15 minutes, stopped after {0}.", TextFormat.Count(limit, "minute")));
        // Second precision, like the CBS log lines: a line written in the second SFC started counts.
        var started = Services.Clock.Now;
        var sfcStart = started.DateTime.AddTicks(-(started.DateTime.Ticks % TimeSpan.TicksPerSecond));
        var sfc = Run.RunTool(new ProcessRequest { FilePath = ProcessRunner.SystemTool("sfc.exe"), Arguments = "/scannow", Label = "SFC", TimeoutMinutes = limit, OutputEncoding = Encoding.Unicode });
        // SFC has no English switch and prints its verdict in the display language, so the verdict
        // comes from the [SR] lines it wrote to CBS.log during this run (CBS logs in English on every
        // Windows). SFC's own text is the fallback when the log has no usable [SR] lines. The verdict
        // only sets the log line, the warning count and the finding; only a repair needs a restart.
        if (!sfc.Started)
        {
            result.Warnings++;
            Run.Log("SFC could not be started.", LogSeverity.Warning);
        }
        else if (sfc.TimedOut)
        {
            result.Warnings++;
            Run.Log("SFC did not finish and was stopped. Its log is CBS.log, in the support package.", LogSeverity.Warning);
        }
        else
        {
            var cbs = SfcCbs(sfcStart, null, started.Offset, null);
            var combined = SfcCombined(cbs, sfc.ExitCode, sfc.Output ?? "");
            if (combined.FromCbs)
            {
                Run.Log(string.Format(Invariant, "SFC verdict read from its {0} in CBS.log and its exit code {1}.", TextFormat.Count(cbs.Lines, "[SR] line"), TextFormat.ExitCode(sfc.ExitCode)));
            }
            if (combined.Verdict == "Repaired" || combined.AlsoRepaired)
            {
                result.RestartNeeded = true;
                result.Changed = true;
                ChangedOwed = true;
                RestartOwed = true;
                Run.Log("SFC found damaged files and repaired them.");
                Run.AddChange("Repaired damaged Windows system files (SFC)");
            }
            switch (combined.Verdict)
            {
                case "Clean":
                    Run.Log("SFC found no integrity violations.");
                    break;
                case "Unfixed":
                    result.Warnings++;
                    Run.Log("SFC found damaged files it could not repair.", LogSeverity.Warning);
                    Run.AddFinding(FindingLevel.Warning, "WU-SFC-UNFIXED", "The system file check found damaged files it could not repair",
                        "SFC ran after the component store repair and still found files it could not replace.",
                        "Restart, then run the repair again. If it repeats, contact your IT support.",
                        "An in-place upgrade repairs it; CBS.log in a support package names the files.");
                    break;
                case "Failed":
                    result.Warnings++;
                    Run.Log("SFC did not complete normally. Its log is CBS.log, in the support package.", LogSeverity.Warning);
                    break;
                case "Unclear":
                    Run.Log("SFC completed; its verdict line was not recognised.");
                    break;
            }
        }
        return result;
    }

    private sealed class PendingRepair
    {
        public string Name = "";
        public string Line = "";
        public bool Resolved;
    }

    private static readonly Regex StoreFailure = new(@"\[SR\] Could not reproject corrupted file|\[SR\] Cannot repair\b.*\bin the store\b|source file in store is also corrupted", MatchOptions, MatchLimit);
    private static readonly Regex CannotRepair = new(@"\[SR\] Cannot repair member file", MatchOptions, MatchLimit);
    private static readonly Regex CannotRepairName = new(@"\[SR\] Cannot repair member file \[[^\]]*\]""([^""]+)""", MatchOptions, MatchLimit);
    private static readonly Regex RepairedLine = new(@"\[SR\] (Repairing corrupted file|Repaired file)|\[SR\] Repairing\b.*\bRepaired\b", MatchOptions, MatchLimit);
    private static readonly Regex VerifyComplete = new(@"\[SR\] Verify complete", MatchOptions, MatchLimit);

    /// <summary>
    /// The SFC verdict from the [SR] lines in CBS.log written at or after since, the moment SFC
    /// started. Null when the log cannot be read or has no such line (for example when CBS archived
    /// the log while SFC ran, or SFC wrote more than the tail holds); otherwise the count, Repaired
    /// and Unfixed, and the Verdict: Unfixed (also when other files were repaired), Repaired, Clean
    /// (only verification lines) or Unclear.
    ///
    /// What the lines mean: "Could not reproject corrupted file", "Cannot repair ... in the store"
    /// and "source file in store is also corrupted": the copy in the component store is damaged too,
    /// so SFC could not repair the file (Unfixed). A plain "Cannot repair member file [l:N]"name""
    /// is what the verify phase logs for every damaged file, also for one it then repairs from the
    /// store: it counts as Unfixed only when no later "Repairing corrupted file" or "Repaired file"
    /// line names the same file.
    ///
    /// Time: CBS writes local time without an offset. startOffset is the UTC offset at since,
    /// endOffset the one now (both default to the local time zone). A line counts when it is at or
    /// after the start, both in UTC: when daylight saving time ended during the run (lines written
    /// after the change show a local time up to the difference earlier) the start is moved back by
    /// the difference, never forward. Kept that simple on purpose: after a change, a line written up
    /// to the difference (one hour) before the start can count as well.
    /// </summary>
    public virtual SfcCbsVerdict SfcCbs(DateTime since, string path = null, TimeSpan? startOffset = null, TimeSpan? endOffset = null)
    {
        var lines = ReadCbsTail(path);
        if (lines == null) { return null; }
        var start = startOffset ?? TimeZoneInfo.Local.GetUtcOffset(since);
        var end = endOffset ?? Services.Clock.Now.Offset;
        // Earliest local line time that counts: the start, moved back by the offset difference
        // when daylight saving time ended during the run (never forward).
        var shift = start - end;
        if (shift < TimeSpan.Zero) { shift = TimeSpan.Zero; }
        var from = since - shift;
        var result = new SfcCbsVerdict();
        var storeFailures = new List<string>();
        var pending = new List<PendingRepair>();
        foreach (var line in lines)
        {
            if (line.Length < 19 || line.IndexOf("[SR]", StringComparison.Ordinal) < 0) { continue; }
            if (!DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", Invariant, DateTimeStyles.None, out var stamp)) { continue; }
            if (stamp < from) { continue; }
            result.Lines++;
            if (StoreFailure.IsMatch(line))
            {
                storeFailures.Add(line.Trim());
            }
            else if (CannotRepair.IsMatch(line))
            {
                // The member file's name, without a folder; "" when the line names none.
                var name = "";
                var match = CannotRepairName.Match(line);
                if (match.Success) { name = match.Groups[1].Value.Split('\\').Last(); }
                pending.Add(new PendingRepair { Name = name, Line = line.Trim() });
            }
            else if (RepairedLine.IsMatch(line))
            {
                result.Repaired = true;
                foreach (var item in pending)
                {
                    if (item.Resolved || item.Name.Length == 0) { continue; }
                    if (Regex.IsMatch(line, "[\\\\\"\\s]" + Regex.Escape(item.Name) + "([\"\\s]|$)", MatchOptions, MatchLimit)) { item.Resolved = true; }
                }
            }
            else if (VerifyComplete.IsMatch(line))
            {
                result.Verified = true;
            }
        }
        if (result.Lines == 0) { return null; }
        var unfixedLines = storeFailures.Concat(pending.Where(p => !p.Resolved).Select(p => p.Line)).ToList();
        // The first few go into the log; CBS.log in the support package has them all.
        foreach (var text in unfixedLines.Take(5)) { Run.PersistentLog("CBS.log: " + text); }
        result.Unfixed = unfixedLines.Count > 0;
        if (result.Unfixed) { result.Verdict = "Unfixed"; }
        else if (result.Repaired) { result.Verdict = "Repaired"; }
        else if (result.Verified) { result.Verdict = "Clean"; }
        return result;
    }

    /// <summary>
    /// The SFC verdict from its [SR] lines in CBS.log together with its exit code and its own text.
    /// A Clean verdict from CBS.log is accepted only when SFC exited with 0: lines can be missing from
    /// the log (the tail holds 8 MB, and CBS can archive the log during the run), and a log with only
    /// its last lines left looks clean. Otherwise the worse of the two counts: Unfixed from either
    /// source wins, then Repaired; anything else with a non-zero exit is Failed. Without usable [SR]
    /// lines, SFC's own text decides (English only), then the exit code.
    /// </summary>
    public static SfcVerdict SfcCombined(SfcCbsVerdict cbs, long exitCode, string output)
    {
        output ??= "";
        var text = "Unclear";
        if (Regex.IsMatch(output, "did not find any integrity violations", MatchOptions, MatchLimit)) { text = "Clean"; }
        else if (Regex.IsMatch(output, "found corrupt files and successfully repaired", MatchOptions, MatchLimit)) { text = "Repaired"; }
        else if (Regex.IsMatch(output, "found corrupt files but was unable to fix", MatchOptions, MatchLimit)) { text = "Unfixed"; }
        else if (exitCode != 0) { text = "Failed"; }
        var result = new SfcVerdict { Verdict = text };
        if (cbs == null || cbs.Verdict == "Unclear") { return result; }
        result.FromCbs = true;
        result.AlsoRepaired = cbs.Repaired;
        if (cbs.Verdict == "Unfixed" || text == "Unfixed") { result.Verdict = "Unfixed"; }
        else if (cbs.Verdict == "Repaired" || text == "Repaired") { result.Verdict = "Repaired"; }
        else if (exitCode == 0) { result.Verdict = "Clean"; }
        else { result.Verdict = "Failed"; }
        return result;
    }

    // --- Disconnected devices and stale packages ---------------------------------------------------

    /// <summary>One device read again right before its removal; null when it cannot be read.</summary>
    public virtual PnpDeviceInfo DeviceNow(string instanceId)
    {
        try { return Services.WindowsUpdate.Device(instanceId); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Removes disconnected devices (the device node only, with pnputil /remove-device; the driver
    /// package stays), each with its own time limit and while the run has time left. Only called
    /// with the candidates AssessGhosts selected. Skipped counts a device that is present again, or
    /// not readable, right before the removal, or already gone when pnputil looked (exit 259).
    /// </summary>
    public virtual GhostRemoval RemoveGhostDevices(IReadOnlyList<PnpDeviceInfo> devices)
    {
        var result = new GhostRemoval();
        // What is about to be removed, by name, for the log: the removal cannot be undone.
        foreach (var group in devices.GroupBy(d => d.FriendlyName ?? "").OrderByDescending(g => g.Count()).Take(8))
        {
            Run.PersistentLog(string.Format(Invariant, "  {0} x {1}", group.Count(), group.Key));
        }
        var pnputil = ProcessRunner.SystemTool("pnputil.exe");
        foreach (var device in devices)
        {
            if (Run.MinutesLeft() < RunContext.MinStepMinutes)
            {
                result.NotTried = devices.Count - result.Removed - result.Failed - result.Skipped;
                break;
            }
            var id = device.InstanceId ?? "";
            // A quote or a trailing backslash would break out of the quoted argument.
            if (id.Length == 0 || id.Contains("\"") || id.EndsWith("\\", StringComparison.Ordinal)) { result.Failed++; continue; }
            // Read again right before the removal: a device that came back since the diagnosis, or
            // one that cannot be read now, is left alone.
            var now = DeviceNow(id);
            if (now == null || now.Present)
            {
                result.Skipped++;
                continue;
            }
            var run = Run.RunTool(new ProcessRequest { FilePath = pnputil, Arguments = "/remove-device \"" + id + "\"", Label = "pnputil /remove-device", TimeoutMinutes = WuCodes.GhostRemoveMinutes });
            if (!run.TimedOut && run.Started && (run.ExitCode == 0 || run.ExitCode == 3010))
            {
                result.Removed++;
                // Marked now, so the close-out still marks them when a later step fails.
                ChangedOwed = true;
                RestartOwed = true;
            }
            else if (!run.TimedOut && run.Started && run.ExitCode == 259)
            {
                // ERROR_NO_MORE_ITEMS: pnputil found no such device (already gone), nothing removed.
                result.Skipped++;
            }
            else
            {
                result.Failed++;
            }
        }
        return result;
    }

    /// <summary>Removes one baseline package with DISM; true when it was removed.</summary>
    public virtual bool RemoveStalePackage(string name)
    {
        if (!WuCodes.StalePackageName.IsMatch(name ?? "")) { return false; }
        var limit = Run.StepLimit(WuCodes.CleanupMinutes, "DISM Remove-Package");
        if (limit <= 0) { return false; }
        Run.Log("Removing the baseline package " + name + ".");
        var run = Run.RunTool(new ProcessRequest
        {
            FilePath = Dism,
            Arguments = "/Online /English /Remove-Package /PackageName:" + name + " /NoRestart",
            Label = "DISM Remove-Package",
            TimeoutMinutes = limit,
        });
        return !run.TimedOut && run.Started && (run.ExitCode == 0 || run.ExitCode == 3010);
    }
}
