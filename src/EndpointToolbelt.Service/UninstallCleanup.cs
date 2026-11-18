using System;
using System.IO;
using EndpointToolbelt.Platform;
using Microsoft.Win32;

namespace EndpointToolbelt.Service;

/// <summary>
/// The work of <c>--remove-state</c>, which the MSI runs at uninstall (deferred, as SYSTEM):
/// the service's SYSTEM-only working folder and the product's State key. Every step refuses
/// rather than guesses; nothing here may fail an uninstall.
/// </summary>
internal static class UninstallCleanup
{
    public const int DeleteAttempts = 10;
    public static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Waits until no other process runs from this executable (the stopped service may still be
    /// exiting when the MSI runs --remove-state), at most <paramref name="timeout"/>.
    /// </summary>
    public static void WaitForOtherServiceProcesses(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var name = self.ProcessName;
        while (DateTime.UtcNow < until)
        {
            var others = System.Diagnostics.Process.GetProcessesByName(name);
            var running = false;
            foreach (var p in others)
            {
                using (p)
                {
                    if (p.Id != self.Id && !p.HasExitedSafe()) { running = true; }
                }
            }
            if (!running) { return; }
            System.Threading.Thread.Sleep(250);
        }
    }

    private static bool HasExitedSafe(this System.Diagnostics.Process process)
    {
        try { return process.HasExited; } catch (Exception) { return false; }
    }

    /// <summary>LocalSystem's profile folder: %SystemRoot%\System32\config\systemprofile (x64 only).</summary>
    public static string SystemProfileFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "config", "systemprofile");

    /// <summary>
    /// The service's working folder as LocalSystem sees it (its %LOCALAPPDATA%), built from the
    /// fixed profile path; the same folder <see cref="ServicePaths.ForService"/> resolves inside
    /// the service.
    /// </summary>
    public static string ServiceWorkingRoot =>
        Path.Combine(SystemProfileFolder, "AppData", "Local", ProductInfo.WorkingFolderName);

    /// <summary>
    /// Null when <paramref name="root"/> may be deleted: an absolute, normalized path strictly
    /// below <paramref name="allowedParent"/>, with no reparse point above it. Otherwise the reason.
    /// </summary>
    public static string CheckWorkingRoot(string root, string allowedParent)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(allowedParent)) { return "no path"; }
        if (!Path.IsPathRooted(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) { return "not an absolute local path"; }
        string full, parent;
        try
        {
            full = Path.GetFullPath(root).TrimEnd('\\');
            parent = Path.GetFullPath(allowedParent).TrimEnd('\\');
        }
        catch (Exception) { return "not a valid path"; }
        // GetFullPath removes "..", so a path that changes under it was not normalized.
        if (!string.Equals(full, root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { return "not a normalized path"; }
        if (!full.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase)) { return "not below " + parent; }
        if (FileSystemSafety.HasReparsePointInPath(Path.GetDirectoryName(full))) { return "a reparse point in its path"; }
        return null;
    }

    /// <summary>Deletes the working folder (without following links) and its leftover siblings.</summary>
    /// <returns>Null when done (or nothing to do), otherwise why nothing was deleted.</returns>
    public static string RemoveWorkingFolder(string root, string allowedParent)
    {
        var problem = CheckWorkingRoot(root, allowedParent);
        if (problem != null) { return problem; }
        if (Directory.Exists(root)) { FileSystemSafety.DeleteTreeNoFollow(root); }
        ToolbeltHost.CleanUpSiblingLeftovers(root);
        return null;
    }

    /// <summary>
    /// Deletes <c>&lt;root&gt;\State</c>, then the root key itself when nothing else is left in it,
    /// or only an empty Config key (the MSI removes its own Config values first). A root with
    /// other values or keys is kept.
    /// </summary>
    /// <param name="baseKey">HKLM in the 64-bit view in production; a test key in tests.</param>
    /// <param name="rootPath">Relative to <paramref name="baseKey"/>, normally <see cref="ProductInfo.RegistryRoot"/>.</param>
    /// <returns>True when the root key was removed or did not exist.</returns>
    public static bool RemoveStateKeys(RegistryKey baseKey, string rootPath)
    {
        using (var root = baseKey.OpenSubKey(rootPath, writable: true))
        {
            if (root == null) { return true; }
            root.DeleteSubKeyTree("State", throwOnMissingSubKey: false);
            if (root.ValueCount > 0) { return false; }
            foreach (var name in root.GetSubKeyNames())
            {
                if (!string.Equals(name, "Config", StringComparison.OrdinalIgnoreCase)) { return false; }
                using var config = root.OpenSubKey(name, writable: false);
                if (config == null || config.ValueCount > 0 || config.SubKeyCount > 0) { return false; }
            }
        }
        baseKey.DeleteSubKeyTree(rootPath, throwOnMissingSubKey: false);
        return true;
    }
}
