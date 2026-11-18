using System;
using System.Collections.Generic;
using System.IO;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// The part of Free up disk space that runs in the app, as the user: files in the user's
/// own temporary folder that nothing touched for a day. Never through a link, never outside the
/// user's own profile; a file in use is skipped.
/// </summary>
public static class UserTempCleanup
{
    /// <summary>A file this recent may belong to something that still runs.</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(24);

    public sealed class Result
    {
        public long BytesFreed { get; set; }
        public int FilesRemoved { get; set; }
        public int Skipped { get; set; }

        /// <summary>Why nothing was done at all; null when the folder was cleaned.</summary>
        public string NotRun { get; set; }
    }

    /// <param name="folder">The user's temporary folder.</param>
    /// <param name="profile">The user's profile folder: the temporary folder must be inside it.</param>
    public static Result Run(string folder, string profile, DateTime nowUtc) => Run(folder, profile, nowUtc, MinimumAge);

    /// <summary>
    /// The contents of a folder in the user's own profile: files and then empty folders that were
    /// neither written nor created within <paramref name="minimumAge"/> (zero: everything that is
    /// not in use). The folder itself stays.
    /// </summary>
    public static Result Run(string folder, string profile, DateTime nowUtc, TimeSpan minimumAge)
    {
        var result = new Result();
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(profile)) { result.NotRun = "the temporary folder is unknown"; return result; }
        var root = Path.GetFullPath(folder).TrimEnd('\\');
        var home = Path.GetFullPath(profile).TrimEnd('\\') + "\\";
        if (!root.StartsWith(home, StringComparison.OrdinalIgnoreCase)) { result.NotRun = "the temporary folder is not in your profile"; return result; }
        if (!Directory.Exists(root)) { result.NotRun = "the temporary folder does not exist"; return result; }
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) { result.NotRun = "the temporary folder is a link"; return result; }

        // Zero: every age (a cache is emptied whole); otherwise only what is older than the cutoff.
        var anyAge = minimumAge <= TimeSpan.Zero;
        var cutoff = nowUtc - minimumAge;
        var folders = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(current).EnumerateFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Skipped++; continue; }
            foreach (var entry in entries)
            {
                try
                {
                    // A link is never followed and never removed: it may point anywhere.
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { result.Skipped++; continue; }
                    if (entry is DirectoryInfo dir)
                    {
                        pending.Push(dir.FullName);
                        folders.Add(dir.FullName);
                        continue;
                    }
                    var file = (FileInfo)entry;
                    if (!anyAge && (file.LastWriteTimeUtc > cutoff || file.CreationTimeUtc > cutoff)) { continue; }
                    var length = file.Length;
                    if ((file.Attributes & FileAttributes.ReadOnly) != 0) { file.Attributes &= ~FileAttributes.ReadOnly; }
                    file.Delete();
                    result.BytesFreed += length;
                    result.FilesRemoved++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use or protected: left alone.
                    result.Skipped++;
                }
            }
        }
        // Empty folders that nothing touched for a day, deepest first; the root stays.
        folders.Sort((a, b) => b.Length.CompareTo(a.Length));
        foreach (var path in folders)
        {
            try
            {
                var dir = new DirectoryInfo(path);
                if (!anyAge && dir.LastWriteTimeUtc > cutoff) { continue; }
                using var any = dir.EnumerateFileSystemInfos().GetEnumerator();
                if (!any.MoveNext()) { dir.Delete(); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }
}
