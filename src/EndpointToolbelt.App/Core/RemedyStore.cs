using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// The user's remedy records: %LOCALAPPDATA%\EndpointToolbelt\remedies.json, next to the run
/// history, so a remedy and its post-check survive a reopen. Per user twice over: the file is
/// in the user's own profile, and every record carries the user's SID, so a record of another user
/// is never read or written back. Bounded: the newest <see cref="KeepRecords"/> records of the last
/// <see cref="KeepFor"/>, and a file larger than <see cref="MaxBytes"/> is not read. Every write
/// goes to a temporary file that is swapped in, so a write that fails never costs the previous file.
/// </summary>
public static class RemedyStore
{
    public const string FileName = "remedies.json";

    /// <summary>The number of remedy records kept.</summary>
    public const int KeepRecords = 100;

    /// <summary>The largest file that is read (256 KB); a larger one is treated as empty.</summary>
    public const long MaxBytes = 256 * 1024;

    /// <summary>How long a record is kept (it is history after 7 days, <see cref="PostCheck.HistoricalAfter"/>).</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    [DataContract]
    private sealed class RemedyFile
    {
        [DataMember(Name = "version", Order = 1)] public int Version { get; set; } = 1;
        [DataMember(Name = "records", Order = 2)] public List<RemedyRecord> Records { get; set; } = new();
    }

    /// <summary>The SID of the signed-in user, which every record carries; empty when it cannot be read.</summary>
    public static string CurrentUser()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? "";
        }
        catch (System.Security.SecurityException)
        {
            return "";
        }
    }

    /// <summary>The user's remedy records from <paramref name="path"/>, bounded; empty when there are none or the file cannot be read.</summary>
    public static List<RemedyRecord> Load(string path, string user, DateTime nowUtc)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxBytes) { return new List<RemedyRecord>(); }
            using var stream = file.OpenRead();
            var content = new DataContractJsonSerializer(typeof(RemedyFile)).ReadObject(stream) as RemedyFile;
            return Bound(content?.Records, user, nowUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SerializationException or ArgumentException)
        {
            return new List<RemedyRecord>();
        }
    }

    /// <summary>Writes the user's records to <paramref name="path"/>, bounded; false when it could not be written.</summary>
    public static bool Save(IEnumerable<RemedyRecord> records, string path, string user, DateTime nowUtc)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var content = new RemedyFile { Records = Bound(records, user, nowUtc) };
            using (var stream = File.Create(temp))
            {
                new DataContractJsonSerializer(typeof(RemedyFile)).WriteObject(stream, content);
            }
            if (File.Exists(path)) { File.Replace(temp, path, null); } else { File.Move(temp, path); }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SerializationException)
        {
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    /// <summary>
    /// Only this user's records, with an app ID, a known remedy and a time, of the last
    /// <see cref="KeepFor"/>, the newest <see cref="KeepRecords"/>, oldest first.
    /// </summary>
    internal static List<RemedyRecord> Bound(IEnumerable<RemedyRecord> records, string user, DateTime nowUtc)
        => (records ?? Enumerable.Empty<RemedyRecord>())
            .Where(r => r != null && !string.IsNullOrEmpty(user) && string.Equals(r.User, user, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(r.AppId) && r.Kind.HasValue && r.AtUtc is DateTime at && nowUtc - at <= KeepFor)
            .OrderByDescending(r => r.AtUtc)
            .Take(KeepRecords)
            .OrderBy(r => r.AtUtc)
            .ToList();
}
