using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace EndpointToolbelt.App.Core;

/// <summary>Finishing the support package in the user's context and other files for Downloads.</summary>
public static class SupportPackage
{
    public const string ReportEntry = "HealthReport.txt";
    public const string HistoryEntry = "history.log";
    public const string DsregEntry = "dsregcmd_status_user.txt";

    /// <summary>
    /// Saves the package the service delivered to Downloads as Toolbelt_yyyyMMdd.zip (the date year
    /// first, in the user's clock; Toolbelt_yyyyMMdd-1.zip, -2 when that name exists). The user's own
    /// files are added: the device report,
    /// the action history and their own dsregcmd view. Short name on purpose: the MDM diagnostics
    /// inside hold long file names. Returns the path and the extra files that could not be added.
    /// </summary>
    /// <remarks>
    /// The new zip is written beside its final name and streamed entry by entry, never rewritten
    /// in place (which would hold the whole package in memory and leave a broken file behind on a
    /// failure). The received file is deleted only once the new one is complete; when saving
    /// fails, it stays where it is and the exception says so.
    /// </remarks>
    public static (string Path, List<string> NotAdded) Save(ReceivedFile file, string downloads, DateTime localNow,
        string reportFile, string historyFile, string[] dsregLines, string userName, string computerName)
    {
        var notAdded = new List<string>();
        var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ReportEntry, HistoryEntry, DsregEntry };
        string destination = null, partial = null;
        try
        {
            Directory.CreateDirectory(downloads);
            // Short: Windows extracts a zip into a folder of its own name, and the MDM diagnostics inside have long names.
            // The day alone; a second package that day gets -1, a third -2.
            var day = "Toolbelt_" + localNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            destination = Path.Combine(downloads, day + ".zip");
            for (var n = 1; File.Exists(destination) || Directory.Exists(destination); n++)
            {
                destination = Path.Combine(downloads, day + "-" + n.ToString(CultureInfo.InvariantCulture) + ".zip");
            }
            partial = destination + ".partial";
            using (var source = ZipFile.OpenRead(file.Path))
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var entry in source.Entries)
                {
                    // The user's own files are added below; a same-named entry is not copied twice.
                    if (ours.Contains(entry.FullName)) { continue; }
                    // Entries stored without compression (already compressed, such as the MDM
                    // diagnostics zip) are not compressed again.
                    var level = entry.CompressedLength >= entry.Length ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                    var copy = zip.CreateEntry(entry.FullName, level);
                    copy.LastWriteTime = entry.LastWriteTime;
                    using var from = entry.Open();
                    using var to = copy.Open();
                    from.CopyTo(to);
                }
                AddFile(zip, reportFile, ReportEntry, notAdded);
                AddFile(zip, historyFile, HistoryEntry, notAdded);
                if (dsregLines != null && dsregLines.Length > 0)
                {
                    var header = string.Format(CultureInfo.InvariantCulture, "Collected by " + ProductInfo.DisplayName + " in the context of {0} on {1} at {2}.",
                        userName, computerName, new DateTimeOffset(localNow).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
                    AddText(zip, DsregEntry, header + "\r\n" + string.Join("\r\n", dsregLines));
                }
            }
            File.Move(partial, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OutOfMemoryException)
        {
            if (partial != null) { TryDelete(partial); }
            throw new PackageSaveException(file.Path, ex);
        }
        TryDelete(file.Path);
        return (destination, notAdded);
    }

    /// <summary>
    /// The log of a run that failed or ended with warnings, for support:
    /// EndpointToolbelt_&lt;Action&gt;_&lt;run ID&gt;.log in Downloads, with a header that says
    /// which run it is, then the result cards and the timeline. Every run gets a file of its own, so
    /// a second run of the same action no longer replaces the first; only the same run written
    /// again replaces its own file. A run without an ID is named by its time instead (with _1, _2
    /// when that name exists).
    /// </summary>
    public static string SaveRunLog(RunLogHeader header, IEnumerable<string> lines, IEnumerable<ResultCard> cards, string downloads)
    {
        Directory.CreateDirectory(downloads);
        var stem = "EndpointToolbelt_" + RunLogStore.ActionPart(header) + "_";
        var id = RunLogStore.IdPart(header.RunId);
        var path = id.Length > 0
            ? Path.Combine(downloads, stem + id + ".log")
            : UserFiles.FreeName(downloads, stem + RunLogStore.TimePart(header.StartUtc ?? header.NowUtc), ".log");
        WriteRunLog(header, lines, cards, path);
        return path;
    }

    /// <summary>
    /// A run log (header, result cards, timeline) at <paramref name="path"/>: the Downloads copy,
    /// or last-run.log that "Open full log" shows.
    /// </summary>
    public static void WriteRunLog(RunLogHeader header, IEnumerable<string> lines, IEnumerable<ResultCard> cards, string path)
    {
        var text = new StringBuilder();
        foreach (var line in header.Lines()) { text.AppendLine(line); }
        text.AppendLine();
        text.AppendLine("----- RESULT -----");
        foreach (var card in cards)
        {
            text.AppendLine("[" + card.Word + "] " + card.Title);
            if (!string.IsNullOrEmpty(card.Detail)) { text.AppendLine("  " + card.Detail.Replace("\r\n", "\n").Replace("\n", Environment.NewLine + "  ")); }
            if (!string.IsNullOrEmpty(card.NextStep)) { text.AppendLine("  Next step: " + card.NextStep); }
            if (!string.IsNullOrEmpty(card.Support)) { text.AppendLine("  For IT support: " + card.Support); }
        }
        text.AppendLine();
        text.AppendLine("----- TECHNICAL LOG (times in " + header.ZoneLabelAt(header.StartUtc ?? header.NowUtc) + ") -----");
        foreach (var line in lines) { text.AppendLine(line); }
        // Written beside its name and then swapped in, so a failed write never leaves half a log
        // where the previous one was.
        // A temporary name of its own per write, removed again when the write fails.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text.ToString(), new UTF8Encoding(true));
            // A short hold by another program (a scan right after the write) is waited out.
            FileSwap.Into(temp, path);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void AddFile(ZipArchive zip, string path, string entryName, List<string> notAdded)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return; }
        byte[] content;
        try
        {
            // Read first: a file that cannot be read is left out; a write error ends the save.
            content = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notAdded.Add(entryName);
            return;
        }
        using var stream = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
        stream.Write(content, 0, content.Length);
    }

    private static void AddText(ZipArchive zip, string entryName, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(entryName, CompressionLevel.Optimal).Open(), new UTF8Encoding(true));
        writer.Write(text);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>The header of a run log: what support needs to place the run.</summary>
public sealed class RunLogHeader
{
    public RunOutcome Outcome { get; set; }

    /// <summary>A check that ran in the app itself (no service run): its name, local run ID and times.</summary>
    public LocalRun Check { get; set; }

    public string AppVersion { get; set; } = "";
    public string DeviceName { get; set; } = "";

    /// <summary>The signed-in user as DOMAIN\user.</summary>
    public string User { get; set; } = "";

    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    /// <summary>The moment of this snapshot (UTC): when the header was written or copied.</summary>
    public DateTime NowUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The name of what ran when it was not a service run and has no <see cref="Check"/> run.</summary>
    public string ActionName { get; set; } = "";

    /// <summary>The run's ID: the service's, or the app's own <c>local-</c> ID for a check in the app; null when there is none.</summary>
    public string RunId
    {
        get
        {
            var id = Outcome != null ? Outcome.RunId : Check?.RunId;
            return string.IsNullOrEmpty(id) ? null : id;
        }
    }

    /// <summary>The name of what ran, in words.</summary>
    public string Name => Outcome != null ? ActionText.Name(Outcome.Action) : (Check?.Name is { Length: > 0 } local ? local : ActionName);

    /// <summary>
    /// The run is still going at <see cref="NowUtc"/>: a service run the app is following, or a
    /// check in the app without its end.
    /// </summary>
    public bool Running => Outcome != null ? Outcome.InProgress : Check != null && !Check.EndedUtc.HasValue;

    /// <summary>
    /// The confirmed end: a service run's summary record, or the end of a check in the app
    /// that the app saw itself. Null while the run goes on, and for a run that stopped without its
    /// summary (crashed, interrupted, stopped at the time limit): its end is not confirmed, so the
    /// header says when it was captured instead. A summary without a time of its own takes the
    /// newest record time before it.
    /// </summary>
    public DateTime? EndedUtc
    {
        get
        {
            if (Running) { return null; }
            if (Outcome == null) { return Check?.EndedUtc; }
            var records = Outcome.Records;
            var at = -1;
            for (var i = records.Count - 1; i >= 0; i--)
            {
                if (records[i]?.Type == "summary") { at = i; break; }
            }
            if (at < 0) { return null; }
            for (var i = at; i >= 0; i--)
            {
                if (records[i]?.Utc is { } utc) { return utc; }
            }
            return null;
        }
    }

    public IEnumerable<string> Lines()
    {
        var outcome = Outcome;
        yield return string.Format(CultureInfo.InvariantCulture, "===== {0} {1} - RUN LOG =====", ProductInfo.DisplayName.ToUpperInvariant(), AppVersion);
        yield return "Device    : " + DeviceName;
        yield return "User      : " + User;
        yield return "Time zone : " + ZoneLabelAt(NowUtc) + ", now";
        if (outcome == null)
        {
            // A check that ran in the app itself: its own local run ID, no result codes or engine times.
            yield return "Action    : " + Name;
            yield return "Run ID    : " + (RunId ?? "none");
            // The outcome in words of a tool that ran in the app (Start requested, Reset requested), for Recent runs.
            if (!string.IsNullOrEmpty(Check?.Outcome)) { yield return "Outcome   : " + Check.Outcome; }
        }
        else
        {
            yield return string.Format(CultureInfo.InvariantCulture, "Action    : {0} ({1})", ActionText.Name(outcome.Action), outcome.Action);
            yield return "Run ID    : " + (RunId ?? "none");
            // A run that goes on has no result yet (its result code is only a default until then).
            yield return outcome.InProgress
                ? "Result    : still running (no result yet)"
                : string.Format(CultureInfo.InvariantCulture, "Result    : {0} (service result {1})", outcome.Result, (int)outcome.Result);
            yield return "Exit code : " + (outcome.ExitCode.HasValue && !outcome.InProgress ? RunWords.Code(outcome.ExitCode.Value) : "none");
        }
        yield return "Started   : " + (StartUtc.HasValue ? Moment(StartUtc.Value) : "unknown");
        // "Ended" only with a confirmed end; otherwise the moment this log or copy was made.
        var ended = EndedUtc;
        yield return ended.HasValue ? "Ended     : " + Moment(ended.Value) : "Captured at : " + Moment(NowUtc);
    }

    /// <summary>When the run started: as the app or the service knew it, else its first record's time.</summary>
    public DateTime? StartUtc => Outcome == null ? Check?.StartedUtc : Outcome.StartedUtc ?? Outcome.Records.FirstOrDefault(r => r?.Utc != null)?.Utc;

    /// <summary>The time zone's label at a moment; the zone's ID when the moment cannot be placed in it.</summary>
    public string ZoneLabelAt(DateTime utc)
    {
        try { return TimeText.ZoneLabel(Zone, utc); }
        catch (ArgumentException) { return Zone.Id; }
    }

    /// <summary>A moment in the user's time to the minute with its offset ("2026-10-15 14:04 UTC+02:00"); in UTC when the calendar cannot hold it there.</summary>
    public string ShortMoment(DateTime utc)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        try
        {
            var offset = Zone.GetUtcOffset(utc);
            return new DateTimeOffset(utc).ToOffset(offset).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" + TimeText.Offset(offset);
        }
        catch (ArgumentException)
        {
            return utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        }
    }

    /// <summary>A moment in the user's time with its offset, and in UTC; one the calendar cannot hold there is shown in UTC only.</summary>
    private string Moment(DateTime utc)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var inUtc = utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
        try
        {
            var local = new DateTimeOffset(utc).ToOffset(Zone.GetUtcOffset(utc));
            return local.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + " (" + inUtc + ")";
        }
        catch (ArgumentException)
        {
            return inUtc;
        }
    }
}

/// <summary>The package could not be saved to Downloads; the received file is still at <see cref="ReceivedPath"/>.</summary>
public sealed class PackageSaveException : Exception
{
    public PackageSaveException(string receivedPath, Exception inner) : base(inner.Message, inner) => ReceivedPath = receivedPath;
    public string ReceivedPath { get; }
}

/// <summary>The app's own per-user settings: window placement and what was already shown.</summary>
[DataContract]
public sealed class AppSettings
{
    [DataMember] public double? Left { get; set; }
    [DataMember] public double? Top { get; set; }
    [DataMember] public double? Width { get; set; }
    [DataMember] public double? Height { get; set; }

    /// <summary>
    /// True when the user sized the window's height; otherwise the window opens as tall as
    /// its content and <see cref="Height"/> is not used.
    /// </summary>
    [DataMember] public bool HeightChosen { get; set; }

    /// <summary>The run whose result was last shown, so a result is shown once after a reopen.</summary>
    [DataMember] public string LastShownRunId { get; set; }

    public static AppSettings Load(string path = null)
    {
        try
        {
            using var stream = File.OpenRead(path ?? UserFiles.SettingsFile());
            var settings = (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(stream) ?? new AppSettings();
            // A placement that is not a real number is dropped; the window then opens at its default.
            if (!IsFinite(settings.Left) || !IsFinite(settings.Top) || !IsFinite(settings.Width) || !IsFinite(settings.Height))
            {
                settings.Left = settings.Top = settings.Width = settings.Height = null;
            }
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SerializationException)
        {
            return new AppSettings();
        }
    }

    private static bool IsFinite(double? value) => value == null || !(double.IsNaN(value.Value) || double.IsInfinity(value.Value));

    public void Save(string path = null)
    {
        try
        {
            // Written beside the file and then swapped in, so a crash never leaves half a file.
            path ??= UserFiles.SettingsFile();
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
            {
                new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(stream, this);
            }
            if (File.Exists(path)) { File.Replace(temp, path, null); } else { File.Move(temp, path); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience.
        }
    }
}

/// <summary>Placement of a window inside the work area of a screen.</summary>
public static class WindowPlacement
{
    /// <summary>
    /// Clamps a window (left, top, width, height) to a work area, so it never opens or grows
    /// partly off screen or under the taskbar (for example on a 1366x768 display).
    /// </summary>
    public static (double Left, double Top, double Width, double Height) Clamp(double left, double top, double width, double height,
        double areaLeft, double areaTop, double areaWidth, double areaHeight, double minWidth, double minHeight)
    {
        // A value that is not a real number puts the window at the work area's corner.
        if (!IsReal(left)) { left = areaLeft; }
        if (!IsReal(top)) { top = areaTop; }
        if (!IsReal(width)) { width = minWidth; }
        if (!IsReal(height)) { height = minHeight; }
        width = Math.Max(Math.Min(width, areaWidth), Math.Min(minWidth, areaWidth));
        height = Math.Max(Math.Min(height, areaHeight), Math.Min(minHeight, areaHeight));
        if (left + width > areaLeft + areaWidth) { left = areaLeft + areaWidth - width; }
        if (top + height > areaTop + areaHeight) { top = areaTop + areaHeight - height; }
        if (left < areaLeft) { left = areaLeft; }
        if (top < areaTop) { top = areaTop; }
        return (left, top, width, height);
    }

    /// <summary>
    /// The window after its screen changed while it was open (the resolution, the display scale or
    /// the work area, as a remote desktop whose client window is resized does): the size the user gave
    /// it (<paramref name="wantedWidth"/>, <paramref name="wantedHeight"/>) where that still fits, else
    /// shrunk to the work area, and grown back to it when the screen grows again; its position kept, then
    /// moved inside the work area, so it is never partly off-screen or under the taskbar. All values in
    /// the window's own units (device-independent pixels at the new scale).
    /// </summary>
    public static (double Left, double Top, double Width, double Height) Refit(double left, double top, double wantedWidth, double wantedHeight,
        double areaLeft, double areaTop, double areaWidth, double areaHeight, double minWidth, double minHeight)
        => Clamp(left, top, wantedWidth, wantedHeight, areaLeft, areaTop, areaWidth, areaHeight, minWidth, minHeight);

    /// <summary>
    /// A work area in physical pixels (left, top, right, bottom) in the window's own units at a
    /// display scale (1.25 for 125 %): 1920 x 1040 pixels at 125 % are 1536 x 832.
    /// </summary>
    public static (double Left, double Top, double Width, double Height) Logical(int left, int top, int right, int bottom, double scale)
    {
        if (!IsReal(scale) || scale <= 0) { scale = 1; }
        return (left / scale, top / scale, (right - left) / scale, (bottom - top) / scale);
    }

    private static bool IsReal(double value) => !(double.IsNaN(value) || double.IsInfinity(value));
}
