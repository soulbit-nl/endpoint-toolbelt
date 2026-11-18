using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Service.State;

/// <summary>The IME's last check-in with the Intune service, as its own log shows it.</summary>
public sealed class ImeCheckIn
{
    /// <summary>The newest successful check-in (UTC), or null when the log shows none.</summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>The newest failed check-in (UTC), only when it is newer than the last success.</summary>
    public DateTime? LastErrorUtc { get; set; }

    /// <summary>The error of that failure: an HTTP status ("401"), "proxy" or "token"; empty when the log names none.</summary>
    public string LastError { get; set; } = "";

    /// <summary>Why nothing could be said: "missing" (no IME log), "unreadable", or null when the log was read.</summary>
    public string Unavailable { get; set; }

    /// <summary>The IME's newest start (UTC), from the line in which it delays its first check-in; null when none was seen.</summary>
    public DateTime? StartUtc { get; set; }

    /// <summary>When the IME said it would check in after that start (UTC); null when no start was seen.</summary>
    public DateTime? FirstCheckInDueUtc { get; set; }
}

/// <summary>
/// Reads the tail of the IME's own log for its last check-in with the Intune service. The service
/// reads it (the IME Logs folder is not every user's to read) and caches the answer, because the
/// app asks for the status often. Bounded: the newest two files of each kind, the last 4 MB of each, records
/// of the last 30 days. The patterns come from the same table the engine uses.
/// </summary>
public sealed class ImeCheckInReader
{
    public static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(2);
    internal const int MaxFiles = 2;
    internal const long MaxBytesPerFile = 4L * 1024 * 1024;

    // <![LOG[message]LOG]!><time="HH:mm:ss.fffffff" date="M-d-yyyy" component="..." ... thread="..." ...>
    // The text is cut at each record start and the pattern is anchored to one piece, so the work
    // stays linear in the size of the log.
    private const string RecordStart = "<![LOG[";
    private static readonly Regex Record = new(
        "\\A<!\\[LOG\\[(?<msg>.*)\\]LOG\\]!><time=\"(?<time>[0-9:.]{8,16})(?<bias>[+-]\\d{1,4})?\"\\s+date=\"(?<date>\\d{1,2}-\\d{1,2}-\\d{4})\"",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    private readonly Func<string> _folder;
    private readonly Func<ImeLogPatternTable> _patterns;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<IEnumerable<(string Id, string Name)>, DateTime> _onPolicyNames;
    private readonly object _lock = new();
    private readonly object _refreshLock = new();
    private ImeCheckIn _cached;
    private DateTime _cachedAt;

    /// <param name="onPolicyNames">
    /// Told the app IDs and names of the IME's "Get policies" lines in the log text this read
    /// loaded anyway (no file is read for it), with the time; the service fills the app name cache
    /// with them. A failure there never changes the check-in answer.
    /// </param>
    public ImeCheckInReader(Func<string> folder, Func<ImeLogPatternTable> patterns, Func<DateTime> utcNow = null,
        Action<IEnumerable<(string Id, string Name)>, DateTime> onPolicyNames = null)
    {
        _folder = folder;
        _patterns = patterns;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _onPolicyNames = onPolicyNames;
    }

    /// <summary>A run ended; the next status reads the logs again instead of the cached answer.</summary>
    public void Invalidate()
    {
        lock (_lock) { _cachedAt = DateTime.MinValue; }
    }

    /// <summary>
    /// The cached answer while it is fresh. Otherwise one caller reads the logs; callers that
    /// arrive during that read get the previous answer instead of waiting (only the very first
    /// read is waited for).
    /// </summary>
    public ImeCheckIn Get()
    {
        var now = _utcNow();
        ImeCheckIn cached;
        lock (_lock)
        {
            cached = _cached;
            if (cached != null && now - _cachedAt < CacheTime && now >= _cachedAt) { return cached; }
        }
        if (cached != null)
        {
            if (!System.Threading.Monitor.TryEnter(_refreshLock)) { return cached; }
        }
        else
        {
            System.Threading.Monitor.Enter(_refreshLock);
        }
        try
        {
            lock (_lock)
            {
                // Another caller may have refreshed it while this one waited for the first read.
                if (_cached != null && now - _cachedAt < CacheTime && now >= _cachedAt) { return _cached; }
            }
            ImeCheckIn result;
            try { result = Read(now); } catch (Exception) { result = new ImeCheckIn { Unavailable = "unreadable" }; }
            lock (_lock)
            {
                _cached = result;
                _cachedAt = now;
            }
            return result;
        }
        finally
        {
            System.Threading.Monitor.Exit(_refreshLock);
        }
    }

    private ImeCheckIn Read(DateTime now)
    {
        var table = _patterns();
        if (table == null) { return new ImeCheckIn { Unavailable = "unreadable" }; }
        var folder = _folder();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { return new ImeCheckIn { Unavailable = "missing" }; }
        // The newest files of each kind: the IME's own log and (newer versions) its app log.
        var directory = new DirectoryInfo(folder);
        var files = table.CheckInFiles.SelectMany(filter => directory.GetFiles(filter)
                .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxFiles))
            .GroupBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (files.Count == 0) { return new ImeCheckIn { Unavailable = "missing" }; }
        var texts = new List<string>();
        foreach (var file in files)
        {
            try { texts.Add(ReadTail(file.FullName, MaxBytesPerFile)); }
            catch (Exception) { /* one unreadable file does not hide the other */ }
        }
        if (texts.Count == 0) { return new ImeCheckIn { Unavailable = "unreadable" }; }
        ReportPolicyNames(texts, now);
        return Evaluate(texts, ImeCheckInEvidence.Matchers(table.CheckIn), now.AddDays(-30));
    }

    /// <summary>The policy lines' app names of the texts just read, to the name cache; best effort.</summary>
    internal void ReportPolicyNames(IEnumerable<string> texts, DateTime now)
    {
        if (_onPolicyNames == null) { return; }
        try
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var text in texts)
            {
                foreach (var pair in AppNameCache.PolicyNamesInText(text)) { names[pair.Key] = pair.Value; }
            }
            if (names.Count > 0) { _onPolicyNames(names.Select(p => (p.Key, p.Value)).ToList(), now); }
        }
        catch (Exception)
        {
            // The cache is a convenience: the check-in answer never depends on it.
        }
    }

    /// <summary>
    /// The newest success, the newest failure when it came after it, and the IME's newest start,
    /// by the evaluation the engine shares (<see cref="ImeCheckInEvidence"/>).
    /// </summary>
    internal static ImeCheckIn Evaluate(IEnumerable<string> texts, IReadOnlyList<ImeCheckInMatcher> rules, DateTime notBeforeUtc)
    {
        var facts = ImeCheckInEvidence.Evaluate(Records(texts, notBeforeUtc), rules);
        var result = new ImeCheckIn { LastSuccessUtc = facts.LastSuccessUtc, StartUtc = facts.StartUtc, FirstCheckInDueUtc = facts.FirstCheckInDueUtc };
        if (facts.LastFailureUtc != null)
        {
            result.LastErrorUtc = facts.LastFailureUtc;
            result.LastError = Clean(facts.Error);
        }
        return result;
    }

    /// <summary>The records of the texts (UTC and message) from <paramref name="notBeforeUtc"/> on.</summary>
    private static IEnumerable<(DateTime Utc, string Message)> Records(IEnumerable<string> texts, DateTime notBeforeUtc)
    {
        foreach (var text in texts)
        {
            var start = text.IndexOf(RecordStart, StringComparison.Ordinal);
            while (start >= 0)
            {
                var next = text.IndexOf(RecordStart, start + 1, StringComparison.Ordinal);
                var end = next < 0 ? text.Length : next;
                Match m;
                try { m = Record.Match(text, start, end - start); }
                catch (RegexMatchTimeoutException) { m = Match.Empty; }
                start = next;
                if (!m.Success) { continue; }
                var utc = ParseTime(m.Groups["date"].Value, m.Groups["time"].Value, m.Groups["bias"].Value);
                if (utc == null || utc.Value < notBeforeUtc) { continue; }
                yield return (utc.Value, m.Groups["msg"].Value);
            }
        }
    }

    /// <summary>Only a short code-like token reaches the app: digits, letters and '-'.</summary>
    private static string Clean(string error)
    {
        var sb = new StringBuilder();
        foreach (var ch in error ?? "")
        {
            if (sb.Length >= 40) { break; }
            if (ch < 128 && (char.IsLetterOrDigit(ch) || ch == '-')) { sb.Append(ch); }
        }
        return sb.ToString();
    }

    private static readonly string[] TimeFormats = { "M-d-yyyy H:mm:ss", "M-d-yyyy H:mm:ss.FFFFFFF" };

    internal static DateTime? ParseTime(string date, string time, string bias)
    {
        var dot = time.IndexOf('.');
        if (dot >= 0 && time.Length - dot - 1 > 7) { time = time.Substring(0, dot + 8); }
        if (!DateTime.TryParseExact(date + " " + time, TimeFormats,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }
        if (!string.IsNullOrEmpty(bias) && int.TryParse(bias, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var minutes))
        {
            // CMTrace: UTC = local + bias.
            return DateTime.SpecifyKind(local.AddMinutes(minutes), DateTimeKind.Utc);
        }
        // Parsed without a kind: ToUniversalTime reads it as the device's own clock.
        return local.ToUniversalTime();
    }

    private static string ReadTail(string path, long maxBytes)
    {
        using var stream = FileSystemSafety.OpenReadNoFollow(path);
        var length = stream.Length;
        var take = Math.Min(length, maxBytes);
        if (take < length) { stream.Seek(-take, SeekOrigin.End); }
        var buffer = new byte[take];
        var read = 0;
        while (read < take)
        {
            var n = stream.Read(buffer, read, (int)(take - read));
            if (n <= 0) { break; }
            read += n;
        }
        var offset = take == length && read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(buffer, offset, read - offset);
    }
}
