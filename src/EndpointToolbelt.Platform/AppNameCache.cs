using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace EndpointToolbelt.Platform;

/// <summary>One app of the name cache: its ID, its name and when the product first and last saw it (UTC).</summary>
public sealed class AppNameEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// App ID to name, kept by the SYSTEM side (the service and its engine) in the service's
/// SYSTEM-only working folder. The IME writes an app's name only in its "Get policies = [...]" lines,
/// and an available app appears there only when a user chooses Install in Company Portal; the log
/// rotates within hours, so a name seen once is kept here. Nothing but the app ID (a GUID), its name
/// (capped and unescaped as the log lines are read) and when it was first and last seen: no user, no
/// device, no intent, no path. At most <see cref="MaxEntries"/> entries, the oldest by last seen
/// dropped first, and none older than <see cref="MaxAge"/>. A file that cannot be read is ignored and
/// rebuilt, never fatal.
/// </summary>
public sealed class AppNameCache
{
    public const string FileName = "AppNames.json";
    public const int MaxEntries = 2000;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(180);

    /// <summary>A name seen again is written again only when its last-seen day moved this far, so a cache read every few minutes is not rewritten every time.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(1);

    /// <summary>The largest cache file read (2,000 names of at most 2,048 characters, written as ASCII, stay below it).</summary>
    public const long MaxFileBytes = 32L * 1024 * 1024;

    public const string GuidPattern = "[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}";

    /// <summary>The text that starts the IME's policy lines, for a line search before the pattern.</summary>
    public const string PolicyLineMarker = "Get policies = [";

    // The IME's policy list: {"Id":"<guid>","Name":"<name>",... Only the Id and the Name directly
    // after it are taken; the name is at most 2,048 characters of JSON string text.
    private static readonly Regex PolicyName = new("\\{\"Id\":\"(" + GuidPattern + ")\",\"Name\":\"((?:[^\"\\\\]|\\\\.){0,2048})\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex IdPattern = new("^" + GuidPattern + "\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly Dictionary<string, AppNameEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>The file was there but could not be read as a cache (it is rebuilt at the next save).</summary>
    public bool WasUnreadable { get; private set; }

    /// <summary>Something changed since it was loaded: a new name, a new last-seen day, or entries dropped.</summary>
    public bool Changed { get; private set; }

    public int Count => _entries.Count;

    public IReadOnlyCollection<AppNameEntry> Entries => _entries.Values;

    /// <summary>The newest last-seen time of any entry; null for an empty cache.</summary>
    public DateTime? NewestSeenUtc => _entries.Count == 0 ? null : _entries.Values.Max(e => e.LastSeenUtc);

    // --- The IME's policy lines ----------------------------------------------------------------

    /// <summary>
    /// The app IDs (lower case) and names in one "Get policies = [...]" line, as the IME wrote them,
    /// the name unescaped. A line without the pattern gives none.
    /// </summary>
    public static IEnumerable<(string Id, string Name)> PolicyNames(string line)
    {
        if (string.IsNullOrEmpty(line)) { yield break; }
        MatchCollection matches;
        try { matches = PolicyName.Matches(line); _ = matches.Count; }
        catch (RegexMatchTimeoutException) { yield break; }
        foreach (Match match in matches)
        {
            var name = CleanName(match.Groups[2].Value);
            if (name.Length == 0) { continue; }
            yield return (match.Groups[1].Value.ToLowerInvariant(), name);
        }
    }

    /// <summary>
    /// The app IDs and names of every policy line in a log text (CMTrace records, as the service reads
    /// a log's tail): each "Get policies = [" up to the end of its record, at most 1 MB of it, through
    /// <see cref="PolicyNames"/>. The newest line comes last in a log, so a later name replaces an earlier one.
    /// </summary>
    public static Dictionary<string, string> PolicyNamesInText(string text)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) { return names; }
        var at = text.IndexOf(PolicyLineMarker, StringComparison.Ordinal);
        while (at >= 0)
        {
            var end = text.IndexOf("]LOG]!>", at, StringComparison.Ordinal);
            var newline = text.IndexOf('\n', at);
            if (end < 0 || (newline >= 0 && newline < end)) { end = newline < 0 ? text.Length : newline; }
            var length = Math.Min(end - at, 1024 * 1024);
            foreach (var (id, name) in PolicyNames(text.Substring(at, length))) { names[id] = name; }
            at = text.IndexOf(PolicyLineMarker, at + PolicyLineMarker.Length, StringComparison.Ordinal);
        }
        return names;
    }

    /// <summary>The most IME logs read for policy lines (the newest by last write, of AppWorkload*.log and IntuneManagementExtension*.log).</summary>
    public const int MaxLogFiles = 4;

    /// <summary>
    /// The app IDs and names of the policy lines in the newest IME logs, the newest line first (so a
    /// renamed app keeps its newest name). Bounded as the engine has always read them: the newest
    /// <paramref name="maxLogFiles"/> logs (links left out), of each only the last 32 MB and the newest
    /// 500 policy lines (<see cref="SafeFileReader.FindLines"/>, by handle, no link followed).
    /// <paramref name="stop"/>, when given, ends the read early once it says the names are enough.
    /// A log that cannot be read is skipped; a missing folder gives none.
    /// </summary>
    public static Dictionary<string, string> ReadPolicyNames(string logsFolder, int maxLogFiles = MaxLogFiles, Func<IReadOnlyDictionary<string, string>, bool> stop = null)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(logsFolder)) { return seen; }
        IEnumerable<FileInfo> Files(string filter)
        {
            try { return new DirectoryInfo(logsFolder).GetFiles(filter).Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { return Enumerable.Empty<FileInfo>(); }
        }
        var logs = Files("AppWorkload*.log").Concat(Files("IntuneManagementExtension*.log")).OrderByDescending(f => f.LastWriteTime).Take(maxLogFiles);
        foreach (var log in logs)
        {
            IReadOnlyList<string> lines;
            try { lines = SafeFileReader.FindLines(log.FullName, PolicyLineMarker); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException) { continue; }
            foreach (var line in lines.Reverse())
            {
                foreach (var (id, name) in PolicyNames(line))
                {
                    if (!seen.ContainsKey(id)) { seen[id] = name; }
                }
                if (stop != null && stop(seen)) { return seen; }
            }
        }
        return seen;
    }

    /// <summary>The name as the log has it, JSON-unescaped (the text as it stands when it cannot be), without surrounding blanks.</summary>
    public static string CleanName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) { return ""; }
        string name;
        try { name = Regex.Unescape(raw); } catch (ArgumentException) { name = raw; }
        return name.Trim();
    }

    // --- The cache ------------------------------------------------------------------------------

    /// <summary>
    /// Records that a name was seen now. A new ID, a changed name, or a last seen that moved by
    /// <see cref="RefreshAfter"/> or more marks the cache as changed.
    /// </summary>
    public void Observe(string id, string name, DateTime utcNow)
    {
        var key = (id ?? "").ToLowerInvariant();
        if (!IdPattern.IsMatch(key)) { return; }
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) { return; }
        if (clean.Length > 2048) { clean = clean.Substring(0, 2048); }
        var now = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        if (_entries.TryGetValue(key, out var known))
        {
            if (!string.Equals(known.Name, clean, StringComparison.Ordinal)) { known.Name = clean; Changed = true; }
            if (now - known.LastSeenUtc >= RefreshAfter) { known.LastSeenUtc = now; Changed = true; }
            return;
        }
        _entries[key] = new AppNameEntry { Id = key, Name = clean, FirstSeenUtc = now, LastSeenUtc = now };
        Changed = true;
    }

    /// <summary>The cached name of an app ID, or false when none is cached.</summary>
    public bool TryGet(string id, out string name)
    {
        name = null;
        if (string.IsNullOrEmpty(id) || !_entries.TryGetValue(id.ToLowerInvariant(), out var entry)) { return false; }
        name = entry.Name;
        return true;
    }

    /// <summary>Drops the entries not seen for <see cref="MaxAge"/>, then the oldest by last seen beyond <see cref="MaxEntries"/>.</summary>
    public void Prune(DateTime utcNow)
    {
        var oldest = utcNow - MaxAge;
        foreach (var stale in _entries.Values.Where(e => e.LastSeenUtc < oldest).Select(e => e.Id).ToList())
        {
            _entries.Remove(stale);
            Changed = true;
        }
        if (_entries.Count <= MaxEntries) { return; }
        foreach (var extra in _entries.Values.OrderBy(e => e.LastSeenUtc).ThenBy(e => e.Id, StringComparer.Ordinal).Take(_entries.Count - MaxEntries).Select(e => e.Id).ToList())
        {
            _entries.Remove(extra);
        }
        Changed = true;
    }

    // --- The file -------------------------------------------------------------------------------

    /// <summary>The cache as JSON: {"version":1,"apps":[{"id":..,"name":..,"first":..,"last":..},...]}, plain ASCII.</summary>
    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\"version\":1,\"apps\":[");
        var first = true;
        foreach (var e in _entries.Values.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            if (!first) { sb.Append(','); }
            first = false;
            sb.Append("{\"id\":").Append(Quote(e.Id))
                .Append(",\"name\":").Append(Quote(e.Name))
                .Append(",\"first\":").Append(Quote(Iso(e.FirstSeenUtc)))
                .Append(",\"last\":").Append(Quote(Iso(e.LastSeenUtc)))
                .Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>
    /// The cache from its JSON. An entry with an ID that is not a GUID, an empty name or a time
    /// that cannot be read is skipped; text that is not such a cache gives an empty cache marked
    /// <see cref="WasUnreadable"/>.
    /// </summary>
    public static AppNameCache FromJson(string text)
    {
        var cache = new AppNameCache();
        XElement root;
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(text ?? "");
            using var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, XmlDictionaryReaderQuotas.Max);
            root = XElement.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or InvalidOperationException)
        {
            cache.WasUnreadable = true;
            return cache;
        }
        var apps = root.Element("apps");
        if ((string)root.Attribute("type") != "object" || (string)root.Element("version") != "1" || apps == null || (string)apps.Attribute("type") != "array")
        {
            cache.WasUnreadable = true;
            return cache;
        }
        foreach (var item in apps.Elements())
        {
            var id = ((string)item.Element("id") ?? "").ToLowerInvariant();
            var name = ((string)item.Element("name") ?? "").Trim();
            var seenFirst = ParseIso((string)item.Element("first"));
            var seenLast = ParseIso((string)item.Element("last"));
            if (!IdPattern.IsMatch(id) || name.Length == 0 || seenFirst == null || seenLast == null) { continue; }
            if (name.Length > 2048) { name = name.Substring(0, 2048); }
            cache._entries[id] = new AppNameEntry { Id = id, Name = name, FirstSeenUtc = seenFirst.Value, LastSeenUtc = seenLast.Value };
        }
        return cache;
    }

    /// <summary>
    /// Reads the cache file without following a link (a link or a hard-linked file is refused). A
    /// missing file is an empty cache; one that cannot be read, is too large or is not a cache is an
    /// empty cache marked <see cref="WasUnreadable"/>. Never throws.
    /// </summary>
    public static AppNameCache Load(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return new AppNameCache(); }
            using var stream = FileSystemSafety.OpenReadNoFollow(path);
            if (stream.Length > MaxFileBytes) { return Unreadable(); }
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            return FromJson(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException)
        {
            return Unreadable();
        }
    }

    private static AppNameCache Unreadable() => new() { WasUnreadable = true };

    /// <summary>
    /// Writes the cache to <paramref name="path"/> atomically: a temporary file next to it, written
    /// through to the disk, then put in place with one replace (or a move when there was none), so a
    /// reader or a power loss sees the old or the new cache, never a torn one. Only below a folder
    /// <paramref name="trustedFolder"/> accepts (the SYSTEM-only working folder) and without a
    /// reparse point in the path: SYSTEM never writes where a standard user can. Returns whether it
    /// was written; never throws.
    /// </summary>
    public bool Save(string path, Func<string, bool> trustedFolder)
    {
        string temp = null;
        try
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || !string.Equals(Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase)) { return false; }
            var full = Path.GetFullPath(path);
            var folder = Path.GetDirectoryName(full);
            if (folder == null || !Directory.Exists(folder) || FileSystemSafety.HasReparsePointInPath(folder)) { return false; }
            if (trustedFolder == null || !trustedFolder(folder)) { return false; }
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) { return false; }
            var bytes = new UTF8Encoding(false).GetBytes(ToJson());
            temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(full)) { File.Replace(temp, full, null); } else { File.Move(temp, full); }
            temp = null;
            Changed = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
        finally
        {
            if (temp != null)
            {
                try { if (File.Exists(temp)) { File.Delete(temp); } } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>The policy of the folder the cache lives in: the service's SYSTEM-only working folder.</summary>
    public static bool IsSystemOnlyFolder(string folder) => FileSystemSafety.ValidateFolder(folder, FolderPolicy.SystemOnly) == null;

    /// <summary>
    /// Loads the cache, records the names seen now, drops what is too old or too many and saves it
    /// when something changed. Returns the cache as it is now (also when it could not be saved).
    /// </summary>
    public static AppNameCache Update(string path, IEnumerable<(string Id, string Name)> seen, DateTime utcNow, Func<string, bool> trustedFolder)
    {
        var cache = Load(path);
        foreach (var (id, name) in seen ?? Enumerable.Empty<(string, string)>()) { cache.Observe(id, name, utcNow); }
        cache.Prune(utcNow);
        if (cache.Changed || cache.WasUnreadable) { cache.Save(path, trustedFolder); }
        return cache;
    }

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static DateTime? ParseIso(string text)
        => DateTime.TryParseExact(text ?? "", "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc) ? utc : null;

    /// <summary>A JSON string in plain ASCII (everything outside printable ASCII as \uXXXX).</summary>
    private static string Quote(string text)
    {
        var sb = new StringBuilder((text ?? "").Length + 2);
        sb.Append('"');
        foreach (var ch in text ?? "")
        {
            int code = ch;
            if (ch == '"') { sb.Append("\\\""); }
            else if (ch == '\\') { sb.Append("\\\\"); }
            else if (code < 32 || code >= 0x7F) { sb.Append("\\u").Append(code.ToString("x4", CultureInfo.InvariantCulture)); }
            else { sb.Append(ch); }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
