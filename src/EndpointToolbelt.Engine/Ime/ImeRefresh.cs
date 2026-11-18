using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.Ime;

/// <summary>How deep the IME refresh clears app state.</summary>
public enum RefreshDepth
{
    /// <summary>Only the retry timers (GRS).</summary>
    RetryTimers,

    /// <summary>All Win32 app state.</summary>
    FullWin32State,
}

/// <summary>What the IME refresh did.</summary>
public sealed class RefreshResult
{
    public bool Ok { get; set; } = true;
    public bool Warn { get; set; }
    public bool Skipped { get; set; }
    public bool Wiped { get; set; }

    /// <summary>Company Portal was not running when the refresh began (the engine never closes it).</summary>
    public bool PortalClosed { get; set; }

    /// <summary>The full reset did not start because Company Portal runs; nothing was changed.</summary>
    public bool PortalOpen { get; set; }
    public int WipeFailures { get; set; }
    public bool TimersCleared { get; set; }
}

/// <summary>What clearing the unpacked app content did.</summary>
public sealed class ContentCacheResult
{
    public int Removed { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public double FreedMB { get; set; }
    public bool Unreadable { get; set; }
}

/// <summary>
/// Intune Management Extension: service control, Company Portal, app state and content. Every step affects all sessions.
/// </summary>
public sealed class ImeRefresh
{
    public const string ServiceName = "IntuneManagementExtension";

    /// <summary>How long a stop or start may take; a hung stop must not run into the engine's time limit.</summary>
    public const int ServiceWaitSeconds = 60;

    /// <summary>Company Portal's cached install status, per user profile. Only this file: the rest of the package folder holds the user's sign-in.</summary>
    public const string CompanyPortalStatusFile = @"AppData\Local\Packages\Microsoft.CompanyPortal_8wekyb3d8bbwe\LocalState\Win32LocalAppStatus.json";

    /// <summary>Never removed from IMECache: the remediation script cache is not app content.</summary>
    private static readonly string[] ContentKeep = { "HealthScripts" };

    private static readonly Regex ProfileSid = new(@"^S-1-(5-21|12-1)(-[0-9]{1,10}){1,15}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex LocalPath = new(@"^[A-Za-z]:\\[^\\]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly RunContext _run;

    public ImeRefresh(RunContext run) => _run = run;

    private ImePaths Paths => _run.Services.ImePaths;
    private IRegistry Registry => _run.Services.Registry;

    // --- Files ------------------------------------------------------------------------------------

    /// <summary>
    /// Deletes a folder tree without following junctions or symbolic links: a link is removed as a
    /// link and its target is never entered. Keeps going past an item that cannot be removed, and
    /// throws the first error at the end. The checks are by path, so they rely on the folders
    /// being writable by administrators and SYSTEM only (true for the IME folders it is used on).
    /// </summary>
    public static void DeleteTree(string path)
    {
        var attributes = File.GetAttributes(path);
        var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
        // Read-only is cleared on real items only; a link's own attributes are left alone.
        if (!isLink && (attributes & FileAttributes.ReadOnly) != 0) { File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly); }
        if ((attributes & FileAttributes.Directory) == 0)
        {
            File.Delete(path);
            return;
        }
        if (isLink)
        {
            Directory.Delete(path, false);
            return;
        }
        Exception first = null;
        foreach (var child in new DirectoryInfo(path).GetFileSystemInfos())
        {
            try { DeleteTree(child.FullName); }
            catch (Exception ex) { first ??= ex; }
        }
        if (first != null) { throw first; }
        Directory.Delete(path, false);
    }

    /// <summary>Every regular file below a folder, without entering junctions or directory links.</summary>
    public static List<FileInfo> TreeFiles(string path)
    {
        var files = new List<FileInfo>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(path));
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().GetFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { continue; }
                if (entry is DirectoryInfo dir) { pending.Push(dir); } else { files.Add((FileInfo)entry); }
            }
        }
        return files;
    }

    /// <summary>
    /// True when another process holds the file open. Only a sharing violation counts. The probe
    /// opens without sharing, asking for read/write access first (only that conflicts with a
    /// running program), then read access when write access is refused. Nothing is written.
    /// </summary>
    public static bool IsFileLocked(string path)
    {
        foreach (var access in new[] { FileAccess.ReadWrite, FileAccess.Read })
        {
            try
            {
                using (File.Open(path, FileMode.Open, access, FileShare.None)) { }
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        return false;
    }

    // --- Service and Company Portal -----------------------------------------------------------------

    /// <summary>True only when the service is confirmed Stopped; any other outcome is a failed stop.</summary>
    public bool StopService()
    {
        var services = _run.Services.ServiceControl;
        try
        {
            var status = services.Status(ServiceName) ?? throw new InvalidOperationException("Cannot find any service with service name '" + ServiceName + "'.");
            if (status == "Stopped")
            {
                _run.Log("The service is already stopped.");
                return true;
            }
            _run.Log("Stopping the Intune Management Extension service (state: " + status + ").");
            if (status != "StopPending") { services.RequestStop(ServiceName); }
            services.WaitForStatus(ServiceName, "Stopped", TimeSpan.FromSeconds(ServiceWaitSeconds));
            // Give its worker processes a moment to exit after the service itself stopped.
            _run.Services.Clock.Sleep(TimeSpan.FromSeconds(3));
            _run.Log("The service stopped.");
            return true;
        }
        catch (Exception ex)
        {
            _run.Log(string.Format(CultureInfo.InvariantCulture, "Could not stop the service within {0} s: {1}", ServiceWaitSeconds, ex.Message), LogSeverity.Warning);
            return false;
        }
    }

    /// <summary>True only when the service is confirmed Running; always bounded in time.</summary>
    public bool StartService()
    {
        var services = _run.Services.ServiceControl;
        try
        {
            var status = services.Status(ServiceName) ?? throw new InvalidOperationException("Cannot find any service with service name '" + ServiceName + "'.");
            if (status == "Running")
            {
                _run.Log("The service is already running.");
                return true;
            }
            _run.Log("Starting the Intune Management Extension service (state: " + status + ").");
            if (status != "StartPending") { services.RequestStart(ServiceName); }
            services.WaitForStatus(ServiceName, "Running", TimeSpan.FromSeconds(ServiceWaitSeconds));
            _run.Log("The service is running.");
            return true;
        }
        catch (Exception ex)
        {
            _run.Log(string.Format(CultureInfo.InvariantCulture, "Could not start the service within {0} s: {1}", ServiceWaitSeconds, ex.Message), LogSeverity.Error);
            return false;
        }
    }

    /// <summary>
    /// The Company Portal processes that run now, and how many of them run in another session
    /// than the caller's. The engine never closes Company Portal (the app asks the user first);
    /// it only reads whether it runs. Null when that could not be read.
    /// </summary>
    public (int Open, int OtherSessions)? CompanyPortalOpen()
    {
        var processes = _run.Services.ProcessControl;
        try
        {
            var ids = processes.Ids("CompanyPortal");
            var other = ids.Count(id => processes.SessionId(id) is int session && _run.CallerSessionId >= 0 && session != _run.CallerSessionId);
            return (ids.Count, other);
        }
        catch (Exception ex)
        {
            _run.Log("Whether Company Portal runs could not be read: " + ex.Message, LogSeverity.Warning);
            return null;
        }
    }

    // --- App state ----------------------------------------------------------------------------------

    /// <summary>
    /// Removes the unpacked Win32 app content folders under IMECache (not HealthScripts). With
    /// <paramref name="requireIdle"/> a whole folder is skipped when any file in it is open: an
    /// installer may be running from it in any session.
    /// </summary>
    public ContentCacheResult ClearContentCache(bool requireIdle)
    {
        var result = new ContentCacheResult();
        var cache = Paths.ContentCache;
        if (!Directory.Exists(cache) && !File.Exists(cache))
        {
            _run.Log("IMECache is not present on this device; nothing to clear.");
            return result;
        }
        List<DirectoryInfo> folders;
        try
        {
            // A cache root that is a link is never entered; the deletes below open every entry
            // relative to the real root handle.
            if ((new DirectoryInfo(cache).Attributes & FileAttributes.ReparsePoint) != 0) { throw new IOException("IMECache is a link or another reparse point; nothing is cleared."); }
            folders = new DirectoryInfo(cache).GetDirectories().Where(d => !ContentKeep.Contains(d.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception ex)
        {
            // Never reported as an empty cache: nothing is known about its content.
            result.Unreadable = true;
            _run.Log("IMECache could not be read: " + ex.Message, LogSeverity.Warning);
            return result;
        }
        if (folders.Count == 0)
        {
            _run.Log("IMECache holds no app content folders; nothing to clear.");
            return result;
        }

        foreach (var folder in folders)
        {
            var files = new List<FileInfo>();
            var sizeMB = 0.0;
            var measured = true;
            if ((folder.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                try
                {
                    files = TreeFiles(folder.FullName);
                    if (files.Count > 0) { sizeMB = Math.Round(files.Sum(f => f.Length) / 1048576d, 1); }
                }
                catch (Exception ex)
                {
                    measured = false;
                    _run.Log("IMECache: could not measure " + folder.Name + ": " + ex.Message, LogSeverity.Warning);
                }
            }

            if (requireIdle)
            {
                // A folder whose files could not be listed cannot be shown to be idle.
                var openFile = measured ? null : "(its files could not be listed)";
                foreach (var file in files)
                {
                    if (openFile != null) { break; }
                    if (IsFileLocked(file.FullName)) { openFile = file.Name; }
                }
                if (openFile != null)
                {
                    result.Skipped++;
                    _run.Log("IMECache: left " + folder.Name + " alone; " + openFile + " is open, so an install is very likely running from it.");
                    continue;
                }
            }

            try
            {
                NativeFile.DeleteTreeBelowNoFollow(cache, folder.Name);
                result.Removed++;
                result.FreedMB += sizeMB;
                _run.Log("IMECache: removed " + folder.Name + " (" + TextFormat.Number(sizeMB) + " MB, last written " + RunContext.UserTime(folder.LastWriteTime, "yyyy-MM-dd HH:mm") + ").");
            }
            catch (Exception ex)
            {
                result.Failed++;
                _run.Log("IMECache: could not remove " + folder.Name + ": " + ex.Message, LogSeverity.Warning);
            }
        }

        _run.Log(string.Format(CultureInfo.InvariantCulture, "IMECache: {0} removed, {1} left alone because they are in use, {2} failed; {3} MB freed.",
            TextFormat.Count(result.Removed, "folder"), result.Skipped, result.Failed, TextFormat.Number(result.FreedMB)), result.Failed > 0 ? LogSeverity.Warning : LogSeverity.Info);
        if (result.Removed > 0)
        {
            _run.AddChange("Removed " + TextFormat.Count(result.Removed, "unpacked app content folder") + " from IMECache (" + TextFormat.Number(result.FreedMB) + " MB); they are downloaded again when needed");
        }
        return result;
    }

    /// <summary>Removes only the GRS subkeys (the IME's retry timers) per scope, keeping detection and reporting state.</summary>
    public bool ClearRetryTimers()
    {
        var root = Paths.Win32AppsKey;
        if (!Registry.KeyExists(root))
        {
            _run.Log("The Win32Apps state key does not exist; no retry timers to clear.");
            return true;
        }
        var removed = 0;
        try
        {
            foreach (var scope in Registry.SubKeyNames(root))
            {
                var grs = root + "\\" + scope + "\\GRS";
                if (Registry.KeyExists(grs))
                {
                    Registry.DeleteKeyTree(grs);
                    removed++;
                }
            }
            var scopes = TextFormat.Count(removed, "scope");
            _run.Log("Cleared the app retry timers (GRS) for " + scopes + ".");
            if (removed > 0) { _run.AddChange("Cleared the app retry timers (GRS) for " + scopes + ", so failed apps are retried now instead of after the retry wait"); }
            return true;
        }
        catch (Exception ex)
        {
            _run.Log("Clearing the retry timers failed: " + ex.Message, LogSeverity.Warning);
            return false;
        }
    }

    /// <summary>
    /// The folders of real user profiles, from the profile list: local and domain accounts and
    /// Microsoft Entra accounts. System and service profiles, backup keys (.bak) and anything that
    /// is not a local absolute path are skipped.
    /// </summary>
    public IReadOnlyList<string> UserProfilePaths()
    {
        var paths = new List<string>();
        var root = Paths.ProfileListKey;
        foreach (var sid in Registry.SubKeyNames(root))
        {
            if (!ProfileSid.IsMatch(sid)) { continue; }
            var path = MachineRegistry.AsText(ImeLog.SafeValue(Registry, root + "\\" + sid, "ProfileImagePath"));
            if (path == null || !LocalPath.IsMatch(path) || paths.Contains(path)) { continue; }
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>
    /// Deletes Company Portal's cached app install status in every user profile, by handle, and
    /// refuses it when any folder below the profile root is a link, or the file is a link or has
    /// more than one hard link (see docs/security.md). Profiles are never named in the log.
    /// Returns the number of profiles whose cache could not be cleared.
    /// </summary>
    public int ClearCompanyPortalStatusCache(Func<IReadOnlyList<string>> profiles = null)
    {
        // SYSTEM deletes in a user-writable path here, so the redirection guard goes on first.
        _run.EnableRedirectionGuard();
        IReadOnlyList<string> list;
        try
        {
            list = (profiles ?? UserProfilePaths)();
        }
        catch (Exception ex)
        {
            _run.Log("Could not list the user profiles, so Company Portal's status cache was not cleared: " + ex.Message, LogSeverity.Warning);
            return 1;
        }
        var removed = 0;
        var failed = 0;
        foreach (var profile in list)
        {
            try
            {
                if (_run.Services.DeleteFileBelow(profile, CompanyPortalStatusFile)) { removed++; }
            }
            catch (Exception ex)
            {
                failed++;
                var reason = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                _run.Log("Could not clear Company Portal's status cache in one profile: " + reason, LogSeverity.Warning);
            }
        }
        // Said per outcome: a profile without the cache file is not a failure.
        var absent = list.Count - removed - failed;
        var parts = new List<string>();
        if (removed > 0) { parts.Add("cleared in " + TextFormat.Count(removed, "profile")); }
        if (absent > 0) { parts.Add("no cache file in " + TextFormat.Count(absent, "profile") + " (nothing to clear)"); }
        if (failed > 0) { parts.Add("could not be cleared in " + TextFormat.Count(failed, "profile")); }
        if (parts.Count == 0) { parts.Add("no user profiles"); }
        _run.Log("Company Portal's status cache: " + string.Join("; ", parts) + ".", failed > 0 ? LogSeverity.Warning : LogSeverity.Info);
        if (removed > 0) { _run.AddChange("Company Portal's local app status cache cleared for " + TextFormat.Count(removed, "profile")); }
        return failed;
    }

    /// <summary>Only the StatusServiceReports subkey; true when it is gone (or was already absent).</summary>
    public bool ClearStatusReports()
    {
        if (!Registry.KeyExists(Paths.StatusReportsKey))
        {
            _run.Log("The IME status reports key does not exist (already clean).");
            return true;
        }
        try
        {
            Registry.DeleteKeyTree(Paths.StatusReportsKey);
            _run.Log("Removed the IME status reports.");
            _run.AddChange("Removed the status reports the IME had already sent (StatusServiceReports), so it reports every app to Intune again");
            return true;
        }
        catch (Exception ex)
        {
            _run.Log("Could not remove the IME status reports: " + ex.Message, LogSeverity.Warning);
            return false;
        }
    }

    /// <summary>
    /// Removes all Win32 app state: the Win32Apps key, the status reports already sent, the
    /// downloaded packages, the IME policy cache, the unpacked content in IMECache and, once
    /// Company Portal is closed, its cached install status. Only called after the IME service was
    /// confirmed stopped. Never touched: the platform script run tracking and the remediation
    /// state. Returns the number of parts that could not be cleared.
    /// </summary>
    public int ClearFullWin32State(bool companyPortalClosed, Func<IReadOnlyList<string>> profiles = null)
    {
        var failures = 0;
        if (Registry.KeyExists(Paths.Win32AppsKey))
        {
            try
            {
                Registry.DeleteKeyTree(Paths.Win32AppsKey);
                _run.Log("Removed the Win32 app state key.");
                _run.AddChange("Removed all Win32 app state (detection, reporting and retry timers); every assigned app is evaluated again");
            }
            catch (Exception ex)
            {
                failures++;
                _run.Log("Could not remove the Win32 app state: " + ex.Message, LogSeverity.Warning);
            }
        }
        else
        {
            _run.Log("The Win32 app state key does not exist (already clean).");
        }

        if (!ClearStatusReports()) { failures++; }

        foreach (var (relative, change, name) in new[]
        {
            (@"Content\Staging", @"Removed the downloaded app packages (Content\Staging)", "downloaded app packages"),
            // The IME's file cache of fetched policies, not the registry key named Policies.
            ("Policies", "Removed the IME policy cache; it is fetched again", "IME policy cache"),
        })
        {
            var folder = Path.Combine(Paths.ImeBasePath, relative);
            if (Directory.Exists(folder) || File.Exists(folder))
            {
                try
                {
                    // Opened relative to the IME folder's handle, never through a link.
                    NativeFile.DeleteTreeBelowNoFollow(Paths.ImeBasePath, relative);
                    _run.Log("Removed the " + name + ".");
                    _run.AddChange(change);
                }
                catch (Exception ex)
                {
                    failures++;
                    _run.Log("Could not remove the " + name + ": " + ex.Message, LogSeverity.Warning);
                }
            }
            else
            {
                _run.Log("The " + name + " folder does not exist (already clean).");
            }
        }

        var content = ClearContentCache(requireIdle: true);
        if (content.Failed > 0 || content.Unreadable) { failures++; }

        // A running Company Portal holds and rewrites its cache, so it is cleared only once closed.
        if (companyPortalClosed)
        {
            failures += ClearCompanyPortalStatusCache(profiles);
        }
        else
        {
            failures++;
            _run.Log("Company Portal's status cache was not cleared, because Company Portal is open (it was opened during the reset).", LogSeverity.Warning);
        }
        return failures;
    }

    /// <summary>
    /// Stop the IME service, clear app state, start the service. The restart is what makes the IME
    /// evaluate assigned apps now. With <paramref name="skipWhenOthersSignedIn"/> the light action
    /// stands down when other users are signed in (or could not be counted). Company Portal is
    /// never closed here. The full depth does not start while it runs (the app asked the user to
    /// close it first; one that runs now came in between, or runs in another session): it stops with
    /// a finding and changes nothing. The light depth leaves an open Company Portal open. Writes the
    /// headers of steps 2 to 4.
    /// </summary>
    public RefreshResult Run(RefreshDepth depth, bool skipWhenOthersSignedIn = false, int stepTotal = 5,
        Func<bool, int> clearFullState = null)
    {
        var result = new RefreshResult();
        _run.StepHeader(2, stepTotal, "Stop the app installer (IME)");
        if (_run.Services.ServiceControl.Status(ServiceName) == null)
        {
            _run.Log("The Intune Management Extension service is not installed.", LogSeverity.Error);
            _run.AddFinding(FindingLevel.Error, "IME-MISSING", "The Intune Management Extension is not installed",
                "Intune installs it with the first Win32 app, PowerShell script or remediation assigned to the device or its user. No action here installs it.",
                "Contact your IT support.",
                "Check in Intune that the device is enrolled and has a Win32 app, platform script or remediation assigned to the device or its user.");
            result.Ok = false;
            return result;
        }

        var others = _run.OtherSessions;
        if (skipWhenOthersSignedIn && others != 0)
        {
            result.Skipped = true;
            var detail = others < 0
                ? "The number of other signed-in users could not be determined. Restarting the IME could interrupt their installs; only the sync ran."
                : TextFormat.Count(others, "other user session is", "other user sessions are") + " signed in on this device. Restarting the IME would interrupt their installs; only the sync ran.";
            _run.Log("Steps 2 to 4 skipped (IME restart, retry-timer clear): " + detail);
            _run.AddFinding(FindingLevel.Info, "IME-REFRESH-SKIPPED", "App retry was skipped: other people are signed in to this device",
                detail, "If an app stays stuck, run this at a quiet moment or contact your IT support.");
            return result;
        }

        var portal = CompanyPortalOpen();
        if (depth == RefreshDepth.FullWin32State && (portal == null || portal.Value.Open > 0))
        {
            // Never closed by force; the reset waits for a closed Company Portal and changes nothing.
            result.Ok = false;
            result.PortalOpen = true;
            var where = portal == null ? "Whether Company Portal runs could not be read"
                : portal.Value.OtherSessions > 0 ? "Company Portal is open in " + TextFormat.Count(portal.Value.OtherSessions, "other user session") + " on this device"
                : "Company Portal is open";
            _run.Log(where + ", so the full reset did not start. Nothing was changed; Company Portal was not closed.", LogSeverity.Warning);
            _run.AddFinding(FindingLevel.Error, "FULLRESET-PORTAL-OPEN", "Full IME reset did not start: Company Portal is open",
                where + ". The reset clears Company Portal's cached app status, which it can only do while Company Portal is closed, and it never closes it for you. Nothing was changed.",
                portal != null && portal.Value.OtherSessions > 0 ? "Ask the other people signed in to this device to close Company Portal, then run the full reset again." : "Close Company Portal, then run the full reset again.");
            return result;
        }
        if (portal != null && portal.Value.Open > 0)
        {
            _run.Log("Company Portal is open; it was not closed and shows the current app state once it is opened again.");
        }
        result.PortalClosed = portal != null && portal.Value.Open == 0;
        var stopped = StopService();

        // A failed stop blocks the clear: removing state while the service writes it leaves it half written.
        if (stopped)
        {
            if (depth == RefreshDepth.FullWin32State)
            {
                _run.StepHeader(3, stepTotal, "Clear all app installation state");
                // Read again just before the clear: a Company Portal opened meanwhile keeps its cache.
                var stillClosed = CompanyPortalOpen() is (int open, _) && open == 0;
                result.WipeFailures = (clearFullState ?? (closed => ClearFullWin32State(closed)))(stillClosed);
                result.Wiped = true;
                if (result.WipeFailures > 0) { result.Warn = true; }
            }
            else
            {
                _run.StepHeader(3, stepTotal, "Clear the retry timers");
                if (ClearRetryTimers()) { result.TimersCleared = true; } else { result.Warn = true; }
            }
        }
        else
        {
            result.Warn = true;
            _run.StepHeader(3, stepTotal, "Clear the app installation state (skipped)");
            _run.Log("The IME service could not be stopped, so app state was not cleared.", LogSeverity.Warning);
            _run.AddFinding(FindingLevel.Warning, "IME-STOP-FAILED", "The Intune Management Extension could not be stopped, so app state was not cleared",
                "Clearing it while the service writes to it would leave it half written; the service was asked to start again instead.",
                "Restart the device and run this again if the app is still stuck.");
        }

        _run.StepHeader(4, stepTotal, "Start the app installer (IME) again");
        if (!StartService())
        {
            result.Ok = false;
            _run.AddFinding(FindingLevel.Error, "IME-START-FAILED", "The Intune Management Extension did not start again",
                "Without it no Win32 app installs and no remediation runs on this device.",
                "Restart the device. If the IME service still does not run, create a support package and contact your IT support.");
        }
        else if (stopped)
        {
            _run.AddChange("Restarted the Intune Management Extension service, which makes it evaluate all assigned apps now");
        }
        return result;
    }
}
