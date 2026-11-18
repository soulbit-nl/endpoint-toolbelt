using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using EndpointToolbelt.Protocol;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Service.State;

/// <summary>
/// The last result and the last produced file per user (SID), kept in the SYSTEM-only working
/// folder so a user can see their own result again after closing the app or a restart.
/// Another user's result is never returned.
/// </summary>
public sealed class ResultStore
{
    // \z, not $: "$" would also accept a trailing newline.
    private static readonly Regex SidPattern = new(@"^S-1-[0-9]+(-[0-9]+){1,14}\z", RegexOptions.CultureInvariant, TimedRegex.Limit);
    private readonly string _folder;
    private readonly object _lock = new();

    public ResultStore(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(_folder);
    }

    public static bool IsValidSid(string sid) => sid != null && TimedRegex.IsMatch(SidPattern, sid);

    private string ResultPath(string sid) => Path.Combine(_folder, CheckSid(sid) + ".json");

    public string FileFolder(string sid) => Path.Combine(_folder, CheckSid(sid) + ".files");

    private static string CheckSid(string sid) =>
        IsValidSid(sid) ? sid : throw new ArgumentException("Invalid SID.", nameof(sid));

    public bool Exists(string sid) => IsValidSid(sid) && File.Exists(ResultPath(sid));

    public void Save(string sid, LastResultInfo result)
    {
        lock (_lock) { SaveLocked(sid, result); }
    }

    private void SaveLocked(string sid, LastResultInfo result)
    {
        byte[] bytes;
        lock (result.Records) { bytes = MessageFraming.Serialize(result); }
        var path = ResultPath(sid);
        var temp = path + ".tmp";
        // Written through to the disk before it replaces the old file, so a power loss leaves
        // either the old or the new result, never a torn or zero-filled one.
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) { File.Replace(temp, path, null); } else { File.Move(temp, path); }
    }

    public LastResultInfo Load(string sid)
    {
        lock (_lock)
        {
            if (!IsValidSid(sid)) { return null; }
            var path = ResultPath(sid);
            if (!File.Exists(path)) { return null; }
            try { return MessageFraming.Deserialize<LastResultInfo>(File.ReadAllBytes(path)); }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// Marks every stored result that is still "in progress" as <see cref="ResultCode.Interrupted"/>,
    /// keeping its records. Called at service start, before any run can begin: a stored run in
    /// progress then belongs to a service process that no longer exists. Returns the changed results.
    /// </summary>
    /// <param name="onProblem">Told about each file that could not be read or rewritten; the others are still handled.</param>
    public List<LastResultInfo> MarkInterrupted(DateTime nowUtc, Action<string> onProblem = null)
    {
        var changed = new List<LastResultInfo>();
        lock (_lock)
        {
            foreach (var path in Directory.EnumerateFiles(_folder, "*.json"))
            {
                var sid = Path.GetFileNameWithoutExtension(path);
                if (!IsValidSid(sid)) { continue; }
                try
                {
                    var result = MessageFraming.Deserialize<LastResultInfo>(File.ReadAllBytes(path));
                    if (result == null || !result.InProgress) { continue; }
                    result.InProgress = false;
                    result.Result = ResultCode.Interrupted;
                    result.ExitCode = null;
                    result.EndedUtc = nowUtc.ToString("o", CultureInfo.InvariantCulture);
                    SaveLocked(sid, result);
                    changed.Add(result);
                }
                catch (Exception ex)
                {
                    // For example a file left empty or zero-filled by a power loss. It is skipped
                    // (and reads as "no result"); the next run of that user replaces it.
                    onProblem?.Invoke($"{Path.GetFileName(path)} is unreadable and was skipped ({ex.GetType().Name}).");
                }
            }
        }
        return changed;
    }

    /// <summary>Moves a produced file into the user's file folder, replacing the previous one.</summary>
    public string StoreFile(string sid, string sourcePath, string fileName)
    {
        lock (_lock)
        {
            var folder = FileFolder(sid);
            if (Directory.Exists(folder)) { FileSystemSafety.DeleteTreeNoFollow(folder); }
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, fileName);
            File.Move(sourcePath, target);
            return target;
        }
    }

    public string GetFile(string sid, string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || !IsValidSid(sid)) { return null; }
        var path = Path.Combine(FileFolder(sid), fileName);
        return File.Exists(path) ? path : null;
    }
}
