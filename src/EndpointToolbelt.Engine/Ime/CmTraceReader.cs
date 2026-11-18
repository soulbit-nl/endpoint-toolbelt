using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.Ime;

/// <summary>The IME's registry keys and folders. Tests point them at their own.</summary>
public sealed class ImePaths
{
    public string Win32AppsKey { get; set; }
    public string SideCarKey { get; set; }

    /// <summary>The status reports the IME has already sent; only this subkey of SideCarPolicies is ever removed.</summary>
    public string StatusReportsKey { get; set; }

    public string ProfileListKey { get; set; }
    public string EnrollmentsKey { get; set; }
    public string ImeBasePath { get; set; }

    /// <summary>Extracted Win32 app content (the unpacked payload installers run from).</summary>
    public string ContentCache { get; set; }

    public string LogsFolder { get; set; }
    public string PatternFile { get; set; }
    public string DeviceEnroller { get; set; }

    /// <summary>The MDM stack's state of MSI line-of-business apps (EnterpriseDesktopAppManagement), one subkey per SID.</summary>
    public string DesktopAppsKey { get; set; } = "";

    /// <summary>The uninstall keys (both registry views) where Windows Installer names an installed product.</summary>
    public string[] UninstallKeys { get; set; } = Array.Empty<string>();

    public static ImePaths ForDevice()
    {
        var sideCar = @"SOFTWARE\Microsoft\IntuneManagementExtension\SideCarPolicies";
        return new ImePaths
        {
            Win32AppsKey = @"SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps",
            SideCarKey = sideCar,
            StatusReportsKey = sideCar + @"\StatusServiceReports",
            ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList",
            EnrollmentsKey = @"SOFTWARE\Microsoft\Enrollments",
            ImeBasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Intune Management Extension"),
            ContentCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "IMECache"),
            LogsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "IntuneManagementExtension", "Logs"),
            // The compiled engine sits in the install folder; the shared table stays in Engine\.
            PatternFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ProductInfo.EngineFolderName, "ImeLogPatterns.json"),
            DeviceEnroller = ProcessRunner.SystemTool("deviceenroller.exe"),
            DesktopAppsKey = @"SOFTWARE\Microsoft\EnterpriseDesktopAppManagement",
            UninstallKeys = new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" },
        };
    }
}

/// <summary>One record of a CMTrace log written by another component (the IME).</summary>
public sealed class CmTraceRecord
{
    public string Message { get; set; }
    public DateTime Utc { get; set; }
    public string Component { get; set; }
    public long Thread { get; set; }

    /// <summary>1 information, 2 warning, 3 error, 0 when the record has none.</summary>
    public int Type { get; set; }

    public string Source { get; set; }
}

/// <summary>What a bounded read of CMTrace logs returned.</summary>
public sealed class CmTraceRead
{
    public List<CmTraceRecord> Records { get; } = new();
    public int Files { get; set; }
    public int Unreadable { get; set; }
    public bool TimedOut { get; set; }
}

/// <summary>
/// Reading CMTrace logs written by other components, read-only and bounded. A record's message
/// can span several lines, so a log is split on records, never on lines: the text is cut at each
/// record start first and the pattern is anchored to one piece, so the work stays linear. Only the
/// tail of a file is read, the reader stops on a time cap, and every file is opened by handle
/// without following a link.
/// </summary>
public static class CmTraceReader
{
    private const string RecordStart = "<![LOG[";

    private static readonly Regex RecordPattern = new(
        "\\A<!\\[LOG\\[(?<msg>.*)\\]LOG\\]!><time=\"(?<time>[0-9:.]{8,16})(?<bias>[+-]\\d{1,4})?\"\\s+date=\"(?<date>\\d{1,2}-\\d{1,2}-\\d{4})\"\\s+component=\"(?<comp>[^\"]{0,128})\"(?:\\s+context=\"[^\"]{0,256}\")?(?:\\s+type=\"(?<type>\\d{1,2})\")?(?:[^>]{0,256}?\\s+thread=\"(?<thread>\\d{1,10})\")?",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    private static readonly string[] TimeFormats = { "M-d-yyyy H:mm:ss", "M-d-yyyy H:mm:ss.FFFFFFF" };

    /// <summary>
    /// The UTC moment of a record. With a bias (UTC = local + bias, in minutes) the record says its
    /// own offset; without one it is in the device's own clock. Null when it is not a valid time.
    /// </summary>
    public static DateTime? ParseTime(string date, string time, string bias)
    {
        var clock = time;
        var dot = clock.IndexOf('.');
        // Seven fraction digits from the IME; .NET parses at most seven.
        if (dot >= 0 && clock.Length - dot - 1 > 7) { clock = clock.Substring(0, dot + 8); }
        if (!DateTime.TryParseExact(date + " " + clock, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) { return null; }
        if (!string.IsNullOrEmpty(bias) && int.TryParse(bias, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var minutes))
        {
            return DateTime.SpecifyKind(local.AddMinutes(minutes), DateTimeKind.Utc);
        }
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).UtcDateTime;
    }

    /// <summary>
    /// The records in a piece of CMTrace text, oldest first. Text before the first record (a record
    /// cut off by the tail read) is skipped; records older than <paramref name="notBeforeUtc"/> or
    /// without a valid time are dropped, so a record is never placed at the wrong moment.
    /// </summary>
    public static List<CmTraceRecord> ParseText(string text, DateTime notBeforeUtc, string source = "", Func<TimeSpan> elapsed = null, int maxSeconds = 0)
    {
        var records = new List<CmTraceRecord>();
        if (string.IsNullOrEmpty(text)) { return records; }
        var start = text.IndexOf(RecordStart, StringComparison.Ordinal);
        while (start >= 0)
        {
            var next = text.IndexOf(RecordStart, start + 1, StringComparison.Ordinal);
            var end = next < 0 ? text.Length : next;
            // The time cap is checked as the records are parsed, not only between files.
            if (elapsed != null && maxSeconds > 0 && elapsed().TotalSeconds >= maxSeconds) { break; }
            var m = RecordPattern.Match(text, start, end - start);
            start = next;
            if (!m.Success) { continue; }
            var utc = ParseTime(m.Groups["date"].Value, m.Groups["time"].Value, m.Groups["bias"].Value);
            if (utc == null || utc < notBeforeUtc) { continue; }
            records.Add(new CmTraceRecord
            {
                Message = m.Groups["msg"].Value,
                Utc = utc.Value,
                Component = m.Groups["comp"].Value,
                Thread = m.Groups["thread"].Success ? long.Parse(m.Groups["thread"].Value, CultureInfo.InvariantCulture) : 0,
                Type = m.Groups["type"].Success ? int.Parse(m.Groups["type"].Value, CultureInfo.InvariantCulture) : 0,
                Source = source,
            });
        }
        return records;
    }

    /// <summary>
    /// Records from the newest files matching <paramref name="filter"/> in <paramref name="folder"/>
    /// (the newest file read last, so the result is oldest first across files), within the caps:
    /// files, bytes per file (tail only), seconds for the whole read, and records older than
    /// <paramref name="notBeforeUtc"/> dropped.
    /// </summary>
    public static CmTraceRead Read(string folder, string filter, Func<TimeSpan> elapsed, int maxFiles = 3, long maxBytesPerFile = 4L * 1024 * 1024, int maxSeconds = 20, DateTime? notBeforeUtc = null)
    {
        var result = new CmTraceRead();
        var notBefore = notBeforeUtc ?? DateTime.MinValue;
        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(folder).GetFiles(filter)
                .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 && f.LastWriteTimeUtc >= notBefore)
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(maxFiles).Reverse().ToArray();
        }
        catch (Exception)
        {
            return result;
        }
        foreach (var file in files)
        {
            if (elapsed().TotalSeconds >= maxSeconds) { result.TimedOut = true; break; }
            var text = SafeFileReader.ReadTail(file.FullName, maxBytesPerFile);
            if (text == null) { result.Unreadable++; continue; }
            List<CmTraceRecord> parsed;
            try
            {
                parsed = ParseText(text, notBefore, file.Name, elapsed, maxSeconds);
            }
            catch (Exception)
            {
                // A pattern timeout or anything else odd in one file costs only that file.
                result.Unreadable++;
                continue;
            }
            result.Files++;
            result.Records.AddRange(parsed);
            if (elapsed().TotalSeconds >= maxSeconds) { result.TimedOut = true; }
        }
        return result;
    }
}
