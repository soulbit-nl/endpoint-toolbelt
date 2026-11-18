using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.Support;

/// <summary>The device sections of dsregcmd /status, and how many were found.</summary>
public sealed class DsregSections
{
    public List<string> Lines { get; } = new();
    public int Found { get; set; }
}

/// <summary>The zip: files added, and those left out.</summary>
public sealed class PackageZipResult
{
    public int Added { get; set; }
    public List<string> LeftOut { get; } = new();
}

/// <summary>
/// Create support package.
///
/// Built in the run's own output folder, which only SYSTEM and Administrators can reach, and handed
/// to the service as one .zip; the service delivers it only to the user who asked for it, and the
/// app adds that user's own files (health report, history, their own dsregcmd view). Every file
/// taken from elsewhere is copied by handle, refusing links and hard links.
///
/// What it holds: the MDM diagnostics export (the same export the signed-in user can make from
/// Settings), the IME logs not already in it (users can read that folder too), the device sections
/// of dsregcmd, the Win32 app state, the MSI line-of-business apps (product code, version and
/// status; no SID, no download address beyond its host) and the remediation results of the device
/// and of the requesting user only, and this product's logs with other users' SIDs removed. Never read: the remediation
/// scripts in the registry and the script output stored with their results.
///
/// Exit codes: 0 complete, 2 delivered with parts missing (PACKAGE-PARTIAL says which), 1 no package.
///
/// The steps are virtual, so a test can replace a step.
/// </summary>
public class SupportPackage
{
    public const string PackageName = "EndpointToolbelt_SupportPackage.zip";
    public const int MdmDiagnosticsLimitMinutes = 15;

    /// <summary>Kept free for zipping and reporting once the parts are collected.</summary>
    public const int PackageZipMinutes = 3;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string[] DsregAllowed = { "Device State", "Device Details", "Tenant Details", "Diagnostic Data", "WinHttp Default Proxy Config" };
    private static readonly Regex DsregBorder = new(@"^\s*\+-{10,}\+\s*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex DsregName = new(@"^\s*\|\s*(.+?)\s*\|\s*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex UserSid = new(@"S-1-(?:5-21|12-1)(?:-\d{1,10}){2,8}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    private static readonly Regex ServiceRunLine = new(@"\bRun ([0-9A-Za-z]{4,32})\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex EngineLine =new(@"^<!\[LOG\[\[([0-9A-Za-z]{4,32})\] ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // "<![LOG[[ab12cd34] Endpoint Toolbelt 1.1.1 engine started. Action: ...": the version before "engine started".
    private static readonly Regex EngineStarted = new(@"^<!\[LOG\[\[[0-9A-Za-z]{4,32}\] .{0,80}? (\S{1,40}) engine started\. Action: ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly string[] StoredExtensions = { ".zip", ".xlsx", ".cab" };
    private static readonly string[] OwnLogNames = { "EndpointToolbelt.log", "EndpointToolbelt.lo_", "EndpointToolbeltService.log", "EndpointToolbeltService.lo_" };

    public SupportPackage(RunContext run)
    {
        Run = run ?? throw new ArgumentNullException(nameof(run));
    }

    public RunContext Run { get; }

    protected EngineServices Services => Run.Services;

    private string ImeLogsFolder => Services.ImePaths.LogsFolder;

    // --- dsregcmd ---------------------------------------------------------------------------------

    /// <summary>
    /// The dsregcmd /status sections that describe the device, not whoever runs it. An allow-list,
    /// so a section a later build adds is left out rather than passed on. A section is a border
    /// line, its name between pipes, and a border line. English section names.
    /// </summary>
    public static DsregSections DeviceSections(IReadOnlyList<string> lines)
    {
        var result = new DsregSections();
        var keep = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i] ?? "";
            if (DsregBorder.IsMatch(line) && i + 2 < lines.Count)
            {
                var name = DsregName.Match(lines[i + 1] ?? "");
                if (DsregBorder.IsMatch(lines[i + 2] ?? "") && name.Success)
                {
                    keep = DsregAllowed.Contains(name.Groups[1].Value);
                    if (keep) { result.Found++; }
                }
            }
            if (keep) { result.Lines.Add(line); }
        }
        return result;
    }

    /// <summary>True while there is time for another collection step, keeping the zip time free.</summary>
    public virtual bool HasTime() => Run.MinutesLeft() >= PackageZipMinutes;

    /// <summary>
    /// The system's OEM code page, in which dsregcmd writes when its output is redirected. Not the
    /// culture's: as SYSTEM that can be a placeholder such as 1, which no encoding has.
    /// </summary>
    public virtual int OemCodePage() => ConsoleEncoding.SystemOemCodePage();

    /// <summary>The device sections of dsregcmd /status, written to <paramref name="target"/> in the package; false when none could be written.</summary>
    public virtual bool AddDsregFile(string target)
    {
        var oem = ConsoleEncoding.FromCodePage(OemCodePage());
        var run = Run.RunTool(new ProcessRequest { FilePath = ProcessRunner.SystemTool("dsregcmd.exe"), Arguments = "/status", Label = "dsregcmd /status", TimeoutMinutes = 2, OutputEncoding = oem });
        if (!run.Started || run.TimedOut)
        {
            Run.Log("dsregcmd /status gave no output; it is not in the package.", LogSeverity.Warning);
            return false;
        }
        var device = DeviceSections(Regex.Split(run.Output ?? "", "\r?\n", RegexOptions.None, TimeSpan.FromSeconds(2)));
        var lines = new List<string>
        {
            string.Format(Invariant, "Collected by " + ProductInfo.DisplayName + " as SYSTEM on {0} at {1}.", Services.GetEnvironmentVariable("COMPUTERNAME"), Services.Clock.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", Invariant)),
            "Device sections only: run as SYSTEM, the user sections describe the SYSTEM context and can name another user.",
            "The signed-in user's own view is added by the app as dsregcmd_status_user.txt.",
        };
        if (device.Found == 0)
        {
            lines.Add("No device section was recognised in the output, so nothing else was kept.");
            Run.Log("dsregcmd output had no recognisable device section; the file holds only its header.", LogSeverity.Warning);
        }
        lines.AddRange(device.Lines);
        File.WriteAllLines(target, lines, new UTF8Encoding(true));
        Run.Log("dsregcmd status captured: " + TextFormat.Count(device.Found, "device section") + ".");
        return device.Found > 0;
    }

    /// <summary>Best effort: a failure is logged and reported as a missing part, never fatal.</summary>
    public virtual bool AddDsreg(string stage)
    {
        try
        {
            if (AddDsregFile(Path.Combine(stage, "dsregcmd_status.txt"))) { return true; }
        }
        catch (Exception ex)
        {
            Run.Log("dsregcmd status could not be captured: " + ex.Message, LogSeverity.Warning);
        }
        return false;
    }

    // --- Files ------------------------------------------------------------------------------------

    /// <summary>
    /// Copies one file into the package. The source is opened by handle without following a link
    /// and refused when it is a link, a folder or has more than one hard link; the destination must
    /// not exist yet (it is in the run's own folder). Returns the number of bytes copied.
    /// </summary>
    public static long CopyNoFollow(string source, string destination)
    {
        long length;
        using (var input = NativeFile.OpenReadNoFollow(source))
        using (var output = new FileStream(NativeFile.ToExtendedPath(destination), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
            length = output.Length;
        }
        // The copy keeps the original's time, which is what a reader of logs goes by.
        try { File.SetLastWriteTimeUtc(destination, new FileInfo(source).LastWriteTimeUtc); }
        catch (Exception) { }
        return length;
    }

    /// <summary>The lines of a text file, read by handle with the same checks.</summary>
    public static List<string> ReadLinesNoFollow(string path)
    {
        using var stream = NativeFile.OpenReadNoFollow(path);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        for (var line = reader.ReadLine(); line != null; line = reader.ReadLine()) { lines.Add(line); }
        return lines;
    }

    /// <summary>
    /// The lines of a text file that contain a text (ordinal, ignoring case), read by handle and
    /// streamed. Bounded: only the last maxBytes of the file are read (the first line there is cut
    /// and skipped), and only the newest maxLines matching lines are kept.
    /// </summary>
    public static List<string> FindLinesNoFollow(string path, string contains, long maxBytes = 32L * 1024 * 1024, int maxLines = 500)
    {
        using var stream = NativeFile.OpenReadNoFollow(path);
        var skipFirst = false;
        if (stream.Length > maxBytes)
        {
            stream.Seek(stream.Length - maxBytes, SeekOrigin.Begin);
            skipFirst = true;
        }
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: !skipFirst);
        var lines = new Queue<string>();
        if (skipFirst) { reader.ReadLine(); }
        for (var line = reader.ReadLine(); line != null; line = reader.ReadLine())
        {
            if (line.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
            lines.Enqueue(line);
            if (lines.Count > maxLines) { lines.Dequeue(); }
        }
        return lines.ToList();
    }

    /// <summary>
    /// Name and size of every file in a zip ("leaf|length"), for deduplication; empty when the zip
    /// cannot be read, so everything is copied.
    /// </summary>
    public virtual HashSet<string> ZipEntryKeys(string path)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) { return keys; }
        try
        {
            using var file = File.OpenRead(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                // The leaf from FullName by hand: ZipArchiveEntry.Name depends on the stored separator.
                var leaf = entry.FullName;
                var separator = Math.Max(leaf.LastIndexOf('/'), leaf.LastIndexOf('\\'));
                if (separator >= 0) { leaf = leaf.Substring(separator + 1); }
                if (leaf.Length > 0) { keys.Add(leaf.ToLowerInvariant() + "|" + entry.Length.ToString(Invariant)); }
            }
        }
        catch (Exception ex)
        {
            Run.Log(string.Format(Invariant, "MDMDiagnostics.zip could not be read for deduplication ({0}); all IME logs are copied.", ex.Message), LogSeverity.Warning);
            return new HashSet<string>(StringComparer.Ordinal);
        }
        return keys;
    }

    /// <summary>The package's own index (PackageIndex.txt): what is in it, what was cut and what was left out, with the sizes.</summary>
    public const string IndexName = "PackageIndex.txt";

    // What the index says, collected while the package is built.
    private readonly List<string> _indexTaken = new();
    private readonly List<string> _indexLeftOut = new();
    private readonly List<string> _indexNotes = new();
    private long _imeFolderBytes;

    // The product versions of the runs in the engine log copy, from its "engine started" lines, in the order they first appear.
    private readonly List<string> _engineVersions = new();

    /// <summary>"a", "a and b", "a, b and c".</summary>
    private static string AndList(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Join("", items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[items.Count - 1];

    /// <summary>The most of one IME log the package takes (its newest part); <see cref="PackageRules.MaxFileBytes"/> (tests use a smaller one).</summary>
    public long FileLimitBytes { get; set; } = PackageRules.MaxFileBytes;

    /// <summary>The largest zip before IME logs are left out; <see cref="PackageRules.MaxPackageBytes"/> (tests use a smaller one).</summary>
    public long PackageLimitBytes { get; set; } = PackageRules.MaxPackageBytes;
    private long _mdmZipBytes;

    /// <summary>
    /// The IME logs by the package rules (<see cref="PackageRules.Plan"/>): per log the current
    /// file and its newest older copy (for the app install logs every older copy of the last day),
    /// PSADT logs of the last 30 days, Sensor and Win32AppInventory left out, each file at most
    /// <see cref="PackageRules.MaxFileBytes"/> (its newest part). A log that MDMDiagnostics.zip still
    /// holds at the same name and size (only when its IME logs could not be taken out of it) is not
    /// copied twice. This product's own logs are added last, separately. Returns what is missing.
    /// </summary>
    public virtual List<string> AddImeLogs(string stage, ISet<string> inMdmZip)
    {
        var missing = new List<string>();
        if (!Directory.Exists(ImeLogsFolder))
        {
            Run.Log("The IME Logs folder does not exist.");
            return missing;
        }
        var target = Path.Combine(stage, "IMELogs");
        Directory.CreateDirectory(target);
        int copied = 0, skipped = 0, failed = 0, notTried = 0, cut = 0, older = 0, byDefault = 0, oldPsadt = 0;
        long bytes = 0, saved = 0;
        FileInfo[] files;
        try { files = new DirectoryInfo(ImeLogsFolder).GetFiles().Where(f => !f.Name.StartsWith("EndpointToolbelt", StringComparison.OrdinalIgnoreCase)).ToArray(); }
        catch (Exception) { files = Array.Empty<FileInfo>(); }
        _imeFolderBytes = files.Sum(f => f.Length);
        var byName = files.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var plan = PackageRules.Plan(files.Select(f => new ImeLogFile { Name = f.Name, Length = f.Length, LastWriteUtc = f.LastWriteTimeUtc }), Services.Clock.UtcNow);
        foreach (var (log, choice) in plan.OrderBy(p => p.File.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (choice == ImeLogChoice.OlderCopy) { older++; _indexLeftOut.Add(log.Name + " (" + PackageRules.Megabytes(log.Length) + "): an older copy"); continue; }
            if (choice == ImeLogChoice.LeftOutByDefault) { byDefault++; _indexLeftOut.Add(log.Name + " (" + PackageRules.Megabytes(log.Length) + "): left out by default"); continue; }
            if (choice == ImeLogChoice.OldPsadtLog)
            {
                oldPsadt++;
                _indexLeftOut.Add(string.Format(Invariant, "{0} ({1}): a PSADT log last written {2:yyyy-MM-dd}, more than {3} days ago", log.Name, PackageRules.Megabytes(log.Length), log.LastWriteUtc, PackageRules.PsadtDays));
                continue;
            }
            var item = byName[log.Name];
            if (inMdmZip.Contains(item.Name.ToLowerInvariant() + "|" + item.Length.ToString(Invariant)))
            {
                skipped++;
                saved += item.Length;
                _indexTaken.Add("MDMDiagnostics.zip holds " + item.Name + " (" + PackageRules.Megabytes(item.Length) + ")");
                continue;
            }
            if (!HasTime()) { notTried++; continue; }
            try
            {
                var kept = CopyTailNoFollow(item.FullName, Path.Combine(target, item.Name), FileLimitBytes);
                bytes += kept;
                copied++;
                if (kept < item.Length)
                {
                    cut++;
                    _indexTaken.Add("IMELogs/" + item.Name + ": " + PackageRules.Megabytes(kept) + ", the newest part of " + PackageRules.Megabytes(item.Length));
                }
                else
                {
                    _indexTaken.Add("IMELogs/" + item.Name + ": " + PackageRules.Megabytes(kept));
                }
            }
            catch (Exception ex)
            {
                failed++;
                Run.Log(string.Format(Invariant, "IME log {0} was not added: {1}", item.Name, ex.Message), LogSeverity.Warning);
            }
        }
        Run.Log(string.Format(Invariant, "IME logs: {0} of {1} taken ({2} MB of {3} MB), {4} cut to their newest {5} MB, {6} older copies and {7} left out by default, {12} PSADT logs older than {13} days left out, {8} left out as already in MDMDiagnostics.zip ({9} MB), {10} failed, {11} not tried for lack of time.",
            copied, files.Length, TextFormat.Number(bytes / 1048576.0), TextFormat.Number(_imeFolderBytes / 1048576.0), cut, FileLimitBytes / 1048576.0,
            older, byDefault, skipped, TextFormat.Number(saved / 1048576.0), failed, notTried, oldPsadt, PackageRules.PsadtDays));
        if (failed > 0) { missing.Add(TextFormat.Count(failed, "IME log") + " that could not be copied"); }
        if (notTried > 0) { missing.Add(TextFormat.Count(notTried, "IME log") + " left out for lack of time"); }
        return missing;
    }

    /// <summary>
    /// Copies at most <paramref name="maxBytes"/> of a file, its newest part (a larger file is read
    /// from its last <paramref name="maxBytes"/>, starting at the first whole line), by handle without
    /// following a link, like <see cref="CopyNoFollow"/>. Returns the number of bytes copied.
    /// </summary>
    public static long CopyTailNoFollow(string source, string destination, long maxBytes)
    {
        long length;
        using (var input = NativeFile.OpenReadNoFollow(source))
        using (var output = new FileStream(NativeFile.ToExtendedPath(destination), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (input.Length > maxBytes)
            {
                input.Seek(input.Length - maxBytes, SeekOrigin.Begin);
                // Start at the first whole line, so the copy never begins in the middle of a record.
                int b;
                while ((b = input.ReadByte()) >= 0 && b != '\n') { }
            }
            input.CopyTo(output);
            length = output.Length;
        }
        try { File.SetLastWriteTimeUtc(destination, new FileInfo(source).LastWriteTimeUtc); }
        catch (Exception) { }
        return length;
    }

    /// <summary>
    /// Takes the IME logs out of MDMDiagnostics.zip (an entry whose file name is one of the IME
    /// Logs folder's, or a .log below an IntuneManagementExtension folder), so each log is in the
    /// package once, by the package rules. The zip is rewritten next to itself and put in place;
    /// everything else in it stays. Returns how many entries were taken out and their size, or null
    /// when the zip could not be rewritten (it then stays as the tool wrote it).
    /// </summary>
    public virtual (int Removed, long Bytes)? StripImeLogs(string mdmZip)
    {
        // Without the IME Logs folder the package has no copy of its own: the MDM zip keeps its logs.
        if (!File.Exists(mdmZip) || !Directory.Exists(ImeLogsFolder)) { return (0, 0); }
        HashSet<string> imeNames;
        try { imeNames = new HashSet<string>(new DirectoryInfo(ImeLogsFolder).GetFiles().Select(f => f.Name), StringComparer.OrdinalIgnoreCase); }
        catch (Exception) { imeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        var temp = mdmZip + ".new";
        try
        {
            int removed = 0;
            long removedBytes = 0;
            using (var input = ZipFile.OpenRead(mdmZip))
            {
                var ime = input.Entries.Where(e => IsImeLogEntry(e.FullName, imeNames)).ToList();
                if (ime.Count == 0) { return (0, 0); }
                using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                using var output = new ZipArchive(file, ZipArchiveMode.Create);
                foreach (var entry in input.Entries)
                {
                    if (ime.Contains(entry)) { removed++; removedBytes += entry.Length; continue; }
                    var copy = output.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    copy.LastWriteTime = entry.LastWriteTime;
                    using var from = entry.Open();
                    using var to = copy.Open();
                    from.CopyTo(to);
                }
            }
            File.Delete(mdmZip);
            File.Move(temp, mdmZip);
            return (removed, removedBytes);
        }
        catch (Exception ex)
        {
            Run.Log(string.Format(Invariant, "The IME logs could not be taken out of MDMDiagnostics.zip ({0}); a log it holds at the same name and size is not copied again.", ex.Message), LogSeverity.Warning);
            try { if (File.Exists(temp)) { File.Delete(temp); } } catch (Exception) { }
            return null;
        }
    }

    /// <summary>An entry of the MDM diagnostics zip that is one of the IME's logs.</summary>
    internal static bool IsImeLogEntry(string fullName, ISet<string> imeNames)
    {
        var path = (fullName ?? "").Replace('\\', '/');
        var leaf = path.Substring(path.LastIndexOf('/') + 1);
        if (leaf.Length == 0) { return false; }
        if (imeNames != null && imeNames.Contains(leaf)) { return true; }
        return leaf.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && path.IndexOf("IntuneManagementExtension/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Writes PackageIndex.txt into the stage (replacing an earlier one): sizes, what is in the package, what was cut and what was left out.</summary>
    public virtual void WriteIndex(string stage, long firstZipBytes = 0)
    {
        var stageBytes = ImeRefresh.TreeFiles(stage).Where(f => !string.Equals(f.Name, IndexName, StringComparison.OrdinalIgnoreCase)).Sum(f => f.Length);
        var lines = new List<string>
        {
            ProductInfo.DisplayName + " support package",
            // A file is never rendered by the window, so a real time with its zone, not a time token.
            "Created " + Services.Clock.UtcNow.ToString("yyyy-MM-dd HH:mm", Invariant) + " UTC by run " + Run.RunId + ".",
            "",
            "Size: the IME Logs folder held " + PackageRules.Megabytes(_imeFolderBytes) + " and the MDM diagnostics " + PackageRules.Megabytes(_mdmZipBytes)
                + "; this package holds " + PackageRules.Megabytes(stageBytes) + " before compression, and its zip is kept at "
                + PackageRules.Megabytes(PackageLimitBytes) + " or less by leaving out the logs listed below.",
        };
        if (firstZipBytes > 0)
        {
            lines.Add("A first zip was " + PackageRules.Megabytes(firstZipBytes) + ", over the limit: older copies of the logs that are not app install logs first, "
                + "then the largest logs that are not core, then older copies of " + AndList(PackageRules.AppInstallFamilies) + ", were left out.");
        }
        if (_engineVersions.Count > 1)
        {
            lines.Add("The logs hold runs of versions " + AndList(_engineVersions) + " (in the order they first appear).");
        }
        lines.Add("");
        lines.Add("IME logs (per log the current file and its newest older copy; for " + AndList(PackageRules.AppInstallFamilies)
            + " every older copy written in the last " + PackageRules.AppInstallCopyHours.ToString(Invariant) + " hours; PSADT logs written in the last "
            + PackageRules.PsadtDays.ToString(Invariant) + " days; Sensor and Win32AppInventory left out; at most "
            + PackageRules.Megabytes(FileLimitBytes) + " per file, its newest part):");
        lines.AddRange(_indexTaken.Select(l => "  " + l));
        if (_indexTaken.Count == 0) { lines.Add("  none"); }
        lines.Add("Left out:");
        lines.AddRange(_indexLeftOut.Select(l => "  " + l));
        if (_indexLeftOut.Count == 0) { lines.Add("  nothing"); }
        lines.AddRange(_indexNotes);
        // The stage folder is the engine's own staging folder (never a caller's path), and IndexName a
        // constant file name: the index goes to exactly <stage>\PackageIndex.txt.
        var stageFolder = new DirectoryInfo(stage);
        File.WriteAllLines(Path.Combine(stageFolder.FullName, Path.GetFileName(IndexName)), lines, new UTF8Encoding(false));
    }

    /// <summary>
    /// The zip, kept at <see cref="PackageRules.MaxPackageBytes"/> or less: when the first zip is
    /// larger, the IME logs of <see cref="PackageRules.DropOrder"/> (by their zipped size in that zip)
    /// are left out, listed in the index, and the zip is made again (at most three times). Returns the
    /// last zip's result and the size of the first zip.
    /// </summary>
    public virtual (PackageZipResult Zip, long FirstBytes) ZipWithinLimit(string stage, string zipPath)
    {
        WriteIndex(stage);
        var zip = Zip(stage, zipPath);
        var first = new FileInfo(zipPath).Length;
        var size = first;
        for (var round = 0; round < 3 && size > PackageLimitBytes; round++)
        {
            List<PackageRules.Taken> taken;
            using (var read = ZipFile.OpenRead(zipPath))
            {
                taken = read.Entries.Where(e => e.FullName.StartsWith("IMELogs/", StringComparison.Ordinal))
                    .Select(e => new PackageRules.Taken { Name = e.FullName.Substring("IMELogs/".Length), Compressed = e.CompressedLength, LastWriteUtc = e.LastWriteTime.UtcDateTime })
                    .ToList();
            }
            var drop = PackageRules.DropOrder(taken, size, PackageLimitBytes);
            if (drop.Count == 0) { break; }
            foreach (var name in drop)
            {
                var stagedFile = Path.Combine(stage, "IMELogs", name);
                var length = File.Exists(stagedFile) ? new FileInfo(stagedFile).Length : 0;
                try { File.Delete(stagedFile); } catch (IOException) { continue; }
                _indexTaken.RemoveAll(l => l.StartsWith("IMELogs/" + name + ":", StringComparison.OrdinalIgnoreCase));
                _indexLeftOut.Add(name + " (" + PackageRules.Megabytes(length) + "): over the package limit of " + PackageRules.Megabytes(PackageLimitBytes));
            }
            Run.Log(string.Format(Invariant, "The package was {0} MB, over the limit of {1} MB: {2} left out ({3}).",
                TextFormat.Number(size / 1048576.0), TextFormat.Number(PackageLimitBytes / 1048576.0), TextFormat.Count(drop.Count, "IME log"), string.Join(", ", drop)), LogSeverity.Warning);
            File.Delete(zipPath);
            WriteIndex(stage, first);
            zip = Zip(stage, zipPath);
            size = new FileInfo(zipPath).Length;
        }
        if (size > PackageLimitBytes)
        {
            Run.Log(string.Format(Invariant, "The package is {0} MB, still over the limit of {1} MB: the core IME logs and the MDM diagnostics are kept.",
                TextFormat.Number(size / 1048576.0), TextFormat.Number(PackageLimitBytes / 1048576.0)), LogSeverity.Warning);
            // The index says so too; one more zip with that line (a few bytes more).
            _indexNotes.Add("Still over the limit: the core IME logs and the MDM diagnostics are kept whole.");
            File.Delete(zipPath);
            WriteIndex(stage, first);
            zip = Zip(stage, zipPath);
        }
        return (zip, first);
    }

    /// <summary>
    /// The service log names every caller by SID. In the package the requesting user's own SID is
    /// shortened the way the CSV files name that user (<see cref="ShortUser"/>); other users' SIDs are
    /// replaced, so the package does not tell one user who else used the product on this device. With
    /// <paramref name="ownRuns"/>, the lines of other users' runs ("Run &lt;id&gt;...": which action,
    /// when, from which session) are left out as well and counted. The service log on the device keeps
    /// the full SID: <see cref="CallerRunIds"/> finds the requester's runs by it, exactly.
    /// </summary>
    public virtual void CopyServiceLogRedacted(string source, string destination, ISet<string> ownRuns = null)
    {
        var own = Run.CallerSid ?? "";
        var ownShort = ShortUser(own);
        var kept = new List<string>();
        var dropped = 0;
        foreach (var line in ReadLinesNoFollow(source))
        {
            var m = ServiceRunLine.Match(line);
            if (ownRuns != null && m.Success && !ownRuns.Contains(m.Groups[1].Value)) { dropped++; continue; }
            kept.Add(UserSid.Replace(line, s => own.Length > 0 && s.Value == own ? ownShort : "<another user>"));
        }
        if (dropped > 0)
        {
            kept.Add(CmTraceFile.FormatLine(string.Format(Invariant, "{0} of other users' runs were left out of this copy for their privacy.", TextFormat.Count(dropped, "line")),
                1, "Engine.Package", Services.Clock.Now, Services.ProcessId));
        }
        File.WriteAllLines(destination, kept, new UTF8Encoding(true));
    }

    /// <summary>
    /// A user's SID as the package's CSV files name that user: a cloud account as "User:" and the first
    /// 8 characters of its Entra object ID (as <see cref="AppState.ScopeRef"/>); any other account as its
    /// SID with the domain part left out ("S-1-5-21-...-1001").
    /// </summary>
    public static string ShortUser(string sid)
    {
        if (string.IsNullOrEmpty(sid)) { return ""; }
        var objectId = AppState.EntraObjectId(sid);
        if (objectId.Length > 0) { return AppState.ScopeRef(objectId); }
        var last = sid.LastIndexOf('-');
        return sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) && last > "S-1-5-21".Length
            ? "S-1-5-21-..." + sid.Substring(last)
            : sid;
    }

    /// <summary>
    /// The run IDs the requesting user started, from the service log ("Run &lt;id&gt;: &lt;action&gt;
    /// requested by &lt;SID&gt;"), and this run. Used to keep other users' runs out of the engine log in
    /// the package: the engine log names their apps and results.
    /// </summary>
    public virtual HashSet<string> CallerRunIds(IEnumerable<string> serviceLogs)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { Run.RunId };
        var own = Run.CallerSid ?? "";
        if (own.Length == 0) { return ids; }
        var pattern = new Regex("Run ([0-9A-Za-z]{4,32}): \\w+ requested by " + Regex.Escape(own) + " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        foreach (var path in serviceLogs)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { continue; }
            foreach (var line in FindLinesNoFollow(path, "requested by"))
            {
                var m = pattern.Match(line);
                if (m.Success) { ids.Add(m.Groups[1].Value); }
            }
        }
        return ids;
    }

    /// <summary>
    /// The engine log with only the requesting user's own runs: each engine line starts with the run
    /// ID in brackets. Lines of other users' runs are left out and counted in a closing line. The
    /// times the window would show in the user's clock are written out in UTC, as nothing renders
    /// them in a file. Returns the product versions of the kept runs ("engine started" lines), in the
    /// order they first appear.
    /// </summary>
    public virtual List<string> CopyEngineLogForCaller(string source, string destination, ISet<string> ownRuns)
    {
        var kept = new List<string>();
        var versions = new List<string>();
        var dropped = 0;
        foreach (var line in ReadLinesNoFollow(source))
        {
            var m = EngineLine.Match(line);
            if (m.Success && !ownRuns.Contains(m.Groups[1].Value)) { dropped++; continue; }
            var started = EngineStarted.Match(line);
            if (started.Success && !versions.Contains(started.Groups[1].Value)) { versions.Add(started.Groups[1].Value); }
            kept.Add(RunContext.UserTimesInUtc(line));
        }
        if (dropped > 0)
        {
            kept.Add(CmTraceFile.FormatLine(string.Format(Invariant, "[{0}] {1} of other users' runs were left out of this copy for their privacy.", Run.RunId, TextFormat.Count(dropped, "line")),
                1, "Engine.Package", Services.Clock.Now, Services.ProcessId));
        }
        File.WriteAllLines(destination, kept, new UTF8Encoding(true));
        return versions;
    }

    /// <summary>This product's engine and service logs, copied last so they hold this run's lines too.</summary>
    public virtual List<string> AddOwnLogs(string stage)
    {
        var missing = new List<string>();
        var folder = Run.LogDirectory;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            Run.Log("The " + ProductInfo.DisplayName + " logs were not found.");
            missing.Add("the " + ProductInfo.DisplayName + " logs");
            return missing;
        }
        var target = Path.Combine(stage, "EndpointToolbelt");
        Directory.CreateDirectory(target);
        HashSet<string> ownRuns = null;
        var versionsByFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in OwnLogNames)
        {
            var source = Path.Combine(folder, name);
            if (!File.Exists(source)) { continue; }
            try
            {
                ownRuns ??= CallerRunIds(new[] { Path.Combine(folder, "EndpointToolbeltService.log"), Path.Combine(folder, "EndpointToolbeltService.lo_") });
                if (name.StartsWith("EndpointToolbeltService", StringComparison.OrdinalIgnoreCase))
                {
                    CopyServiceLogRedacted(source, Path.Combine(target, name), ownRuns);
                }
                else
                {
                    versionsByFile[name] = CopyEngineLogForCaller(source, Path.Combine(target, name), ownRuns) ?? new List<string>();
                }
            }
            catch (Exception ex)
            {
                Run.Log(string.Format(Invariant, "{0} was not added: {1}", name, ex.Message), LogSeverity.Warning);
                missing.Add(name);
            }
        }
        // The rolled-over engine log (.lo_) holds the older runs: its versions come first.
        _engineVersions.Clear();
        foreach (var name in new[] { "EndpointToolbelt.lo_", "EndpointToolbelt.log" })
        {
            if (!versionsByFile.TryGetValue(name, out var versions)) { continue; }
            foreach (var version in versions) { if (!_engineVersions.Contains(version)) { _engineVersions.Add(version); } }
        }
        if (_engineVersions.Count > 1) { Run.Log("The engine logs hold runs of versions " + AndList(_engineVersions) + "."); }
        return missing;
    }

    // --- State tables -----------------------------------------------------------------------------

    public virtual AppStateReadout ReadAppState() => AppState.Read(Run);

    /// <summary>Marks the rows the IME's check-ins prove no longer assigned (<see cref="ImeAssignments.Mark"/>); returns how many.</summary>
    public virtual int MarkNoLongerAssigned(IReadOnlyList<AppStateRow> rows) => ImeAssignments.Mark(Run, rows);

    public virtual Dictionary<string, string> AppNames(IEnumerable<string> appIds) => AppState.NameMap(Run, appIds);

    /// <summary>The MSI line-of-business apps of the device and of the requesting user (other users counted only).</summary>
    public virtual MsiAppReadout ReadMsiApps() => LobApps.ReadMsi(Run);

    /// <summary>The MSIX line-of-business app installs of the device and of the requesting user (other users' are not read).</summary>
    public virtual MsixAppReadout ReadMsixApps() => MsixApps.Read(Run);

    public virtual RemediationState ReadRemediations() => Remediation.Read(Run);

    public virtual List<ImeLogSummaryRow> ImeLogSummary(AppStateReadout appState) => PackageSheets.ImeLogSummaryRows(Run, appState);

    public virtual List<ProfileErrorRow> ProfileErrors() => PackageSheets.ProfileErrorRows(Run);

    public virtual List<EspRow> EspState() => PackageSheets.EspRows(Run);

    /// <summary>
    /// Win32 app state and remediation results: logged, as CSV, and as one workbook. A state is
    /// dropped only when it could not be read; a failing name lookup, summary or CSV leaves it for
    /// the rest. Returns what is missing.
    /// </summary>
    public virtual List<string> AddStateTables(string stage)
    {
        var missing = new List<string>();
        AppStateReadout appState = null;
        try
        {
            appState = ReadAppState();
        }
        catch (Exception ex)
        {
            Run.Log("The Win32 app state could not be read: " + ex.Message, LogSeverity.Warning);
            missing.Add("the Win32 app state");
        }
        if (appState != null)
        {
            if (appState.Rows.Count > 0)
            {
                try
                {
                    var names = AppNames(appState.Rows.Select(r => r.AppId));
                    foreach (var row in appState.Rows) { if (names.TryGetValue(row.AppId, out var name)) { row.Name = name; } }
                }
                catch (Exception ex)
                {
                    Run.Log(string.Format(Invariant, "App names could not be looked up ({0}); the table shows app IDs.", ex.Message), LogSeverity.Warning);
                }
            }
            // The name cache by its size and newest date only; its names are not listed (it may hold other users' available apps).
            if (!string.IsNullOrEmpty(Run.AppNameCacheFile)) { Run.Log(AppState.CacheSummary(AppNameCache.Load(Run.AppNameCacheFile))); }
            try
            {
                // The IME log evidence goes to its own tab below; reading the logs once is enough.
                AppState.WriteSummary(Run, appState, "at collection time", inPackage: true, noLogEvidence: true);
            }
            catch (Exception ex)
            {
                Run.Log("The Win32 app state summary failed: " + ex.Message, LogSeverity.Warning);
            }
            // A record the IME's own check-ins prove no longer assigned gets that verdict in the table
            // (CSV and workbook); every other record keeps the verdict it reads. Never throws.
            MarkNoLongerAssigned(appState.Rows);
            try
            {
                Csv.Write(Path.Combine(stage, "Win32AppState.csv"), PackageSheets.AppStateHeaders, appState.Rows.Select(PackageSheets.AppStateCells));
            }
            catch (Exception ex)
            {
                Run.Log("Win32AppState.csv could not be written: " + ex.Message, LogSeverity.Warning);
                missing.Add("Win32AppState.csv");
            }
        }
        // The MSI line-of-business apps the MDM stack recorded (EnterpriseDesktopAppManagement): the device's
        // and the requesting user's, other users counted only; never a SID or the download address beyond its host.
        MsiAppReadout msiApps = null;
        try
        {
            msiApps = ReadMsiApps();
        }
        catch (Exception ex)
        {
            Run.Log("The MSI line-of-business apps could not be read: " + ex.Message, LogSeverity.Warning);
            missing.Add("the MSI line-of-business apps");
        }
        if (msiApps != null)
        {
            Run.Log(PackageSheets.MsiAppSummary(msiApps));
            if (msiApps.Present)
            {
                try
                {
                    // The stage folder is the engine's own staging folder (never a caller's path).
                    var stageFolder = new DirectoryInfo(stage);
                    Csv.Write(Path.Combine(stageFolder.FullName, "MsiApps.csv"), PackageSheets.MsiAppHeaders, msiApps.Rows.Select(PackageSheets.MsiAppCells));
                }
                catch (Exception ex)
                {
                    Run.Log("MsiApps.csv could not be written: " + ex.Message, LogSeverity.Warning);
                    missing.Add("MsiApps.csv");
                }
            }
        }
        // The MSIX line-of-business app installs the MDM stack holds (EnterpriseModernAppManagement): the
        // device's and the requesting user's; other users' are not read. Never a SID; the nodes hold no address.
        MsixAppReadout msixApps = null;
        try
        {
            msixApps = ReadMsixApps();
        }
        catch (Exception ex)
        {
            Run.Log("The MSIX line-of-business app installs could not be read: " + ex.Message, LogSeverity.Warning);
        }
        if (msixApps == null || !msixApps.Present) { missing.Add("the MSIX line-of-business app installs"); }
        Run.Log(PackageSheets.MsixAppSummary(msixApps));
        if (msixApps != null && msixApps.Rows.Count > 0)
        {
            try
            {
                // The stage folder is the engine's own staging folder (never a caller's path).
                var stageFolder = new DirectoryInfo(stage);
                Csv.Write(Path.Combine(stageFolder.FullName, "MsixApps.csv"), PackageSheets.MsixAppHeaders, msixApps.Rows.Select(PackageSheets.MsixAppCells));
            }
            catch (Exception ex)
            {
                Run.Log("MsixApps.csv could not be written: " + ex.Message, LogSeverity.Warning);
                missing.Add("MsixApps.csv");
            }
        }
        RemediationState remediations = null;
        try
        {
            remediations = ReadRemediations();
        }
        catch (Exception ex)
        {
            Run.Log("The remediation results could not be read: " + ex.Message, LogSeverity.Warning);
            missing.Add("the remediation results");
        }
        if (remediations != null)
        {
            try
            {
                Remediation.WriteSummary(Run, remediations, inPackage: true);
            }
            catch (Exception ex)
            {
                Run.Log("The remediation summary failed: " + ex.Message, LogSeverity.Warning);
            }
            try
            {
                Csv.Write(Path.Combine(stage, "Remediations.csv"), RemediationRow.Headers, remediations.Rows.Select(r => r.Cells()));
            }
            catch (Exception ex)
            {
                Run.Log("Remediations.csv could not be written: " + ex.Message, LogSeverity.Warning);
                missing.Add("Remediations.csv");
            }
        }
        // The IME log summary: own scope only, counts and phases, never log text.
        Sheet logSheet;
        try
        {
            logSheet = PackageSheets.ImeLogSummarySheet(ImeLogSummary(appState));
        }
        catch (Exception ex)
        {
            Run.Log("The IME log summary could not be made: " + ex.Message, LogSeverity.Warning);
            logSheet = PackageSheets.ImeLogSummarySheet(Array.Empty<ImeLogSummaryRow>());
            missing.Add("the IME log summary");
        }
        // Failed configuration profile settings and the Enrollment Status Page.
        Sheet profileSheet;
        try
        {
            profileSheet = PackageSheets.ProfileErrorSheet(ProfileErrors());
        }
        catch (Exception ex)
        {
            Run.Log("The configuration profile errors could not be read: " + ex.Message, LogSeverity.Warning);
            profileSheet = PackageSheets.ProfileErrorSheet(Array.Empty<ProfileErrorRow>());
            missing.Add("the configuration profile errors");
        }
        Sheet espSheet;
        try
        {
            espSheet = PackageSheets.EspSheet(EspState());
        }
        catch (Exception ex)
        {
            Run.Log("The Enrollment Status Page state could not be read: " + ex.Message, LogSeverity.Warning);
            espSheet = PackageSheets.EspSheet(Array.Empty<EspRow>());
            missing.Add("the Enrollment Status Page state");
        }
        var book = Path.Combine(stage, "IntuneState.xlsx");
        try
        {
            // The MSI and MSIX tabs go last, so the earlier tabs keep their place for anyone who reads them by position.
            Workbook.Write(new[] { PackageSheets.AppStateSheet(Run, appState), Remediation.Sheet(remediations), logSheet, profileSheet, espSheet, PackageSheets.MsiAppSheet(msiApps), PackageSheets.MsixAppSheet(msixApps) }, book);
            Run.Log("IntuneState.xlsx written (tabs: Apps, Remediations, IME log summary, Configuration profiles, Enrollment Status Page, MSI apps, MSIX apps).");
        }
        catch (Exception ex)
        {
            Run.Log(string.Format(Invariant, "IntuneState.xlsx could not be written ({0}); the CSV files hold the same data.", ex.Message), LogSeverity.Warning);
            // A half-written workbook would reach the package as a damaged file.
            try { if (File.Exists(book)) { File.Delete(book); } }
            catch (Exception) { }
            missing.Add("IntuneState.xlsx");
        }
        return missing;
    }

    // --- Zip and the action -----------------------------------------------------------------------

    /// <summary>
    /// Zips the staging folder (only files this run wrote). A file that cannot be opened is left out
    /// and reported; any failure while writing the zip ends the package, so a damaged zip is never
    /// delivered. Already-compressed files are stored as they are; every entry keeps its file's time.
    /// </summary>
    public virtual PackageZipResult Zip(string stage, string path)
    {
        var root = new DirectoryInfo(stage).FullName.TrimEnd('\\') + "\\";
        var result = new PackageZipResult();
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var item in ImeRefresh.TreeFiles(stage).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
        {
            var relative = item.FullName.Substring(root.Length).Replace('\\', '/');
            FileStream source;
            try
            {
                source = File.OpenRead(item.FullName);
            }
            catch (Exception ex)
            {
                Run.Log(string.Format(Invariant, "{0} was left out of the package: {1}", relative, ex.Message), LogSeverity.Warning);
                result.LeftOut.Add(relative);
                continue;
            }
            using (source)
            {
                var level = StoredExtensions.Contains(item.Extension.ToLowerInvariant()) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                var entry = zip.CreateEntry(relative, level);
                entry.LastWriteTime = new DateTimeOffset(item.LastWriteTime);
                using (var target = entry.Open()) { source.CopyTo(target); }
                result.Added++;
            }
        }
        return result;
    }

    /// <summary>Whether a system tool is on this device.</summary>
    public virtual bool ToolExists(string path) => File.Exists(path);

    /// <summary>Removes the staging folder without following links.</summary>
    public virtual void RemoveStage(string stage) => ImeRefresh.DeleteTree(stage);

    public virtual int StepLimit(int limitMinutes, string label) => Run.StepLimit(limitMinutes, label);

    public virtual int CollectDiag()
    {
        var outFolder = Run.OutputDirectory;
        if (string.IsNullOrEmpty(outFolder) || !Directory.Exists(outFolder))
        {
            Run.AddFinding(FindingLevel.Error, "PACKAGE-FAILED", "The support package could not be created",
                "The engine was given no output folder.", "Contact your IT support.",
                "Repair or reinstall " + ProductInfo.DisplayName + " on this device; the service did not prepare an output folder for the engine.");
            return EngineExitCode.Failed;
        }
        var stage = Path.Combine(outFolder, "package");
        var zipPath = Path.Combine(outFolder, PackageName);
        var missing = new List<string>();
        var zipStarted = false;
        try
        {
            if (Directory.Exists(stage) || File.Exists(stage)) { throw new IOException("An item with the specified name " + stage + " already exists."); }
            Directory.CreateDirectory(stage);

            // 1. MDM diagnostics (enrollment, provisioning, Autopilot), with a time limit. A tool
            // that fails or cannot start costs only its own part of the package.
            Run.StepHeader(1, 6, "Collect the Intune diagnostics");
            var limit = StepLimit(MdmDiagnosticsLimitMinutes, "mdmdiagnosticstool");
            var tool = ProcessRunner.SystemTool("mdmdiagnosticstool.exe");
            if (limit <= 0)
            {
                missing.Add("the MDM diagnostics (no time left)");
            }
            else if (!ToolExists(tool))
            {
                Run.Log("mdmdiagnosticstool.exe is not on this device; MDM diagnostics are left out.");
            }
            else
            {
                Run.Log("Running mdmdiagnosticstool (this can take a few minutes).");
                var mdmZip = Path.Combine(stage, "MDMDiagnostics.zip");
                var run = Run.RunTool(new ProcessRequest
                {
                    FilePath = tool,
                    Arguments = "-area \"DeviceEnrollment;DeviceProvisioning;Autopilot\" -zip \"" + mdmZip + "\"",
                    Label = "mdmdiagnosticstool",
                    TimeoutMinutes = limit,
                });
                if (run.TimedOut)
                {
                    Run.Log("mdmdiagnosticstool was stopped on its time limit.", LogSeverity.Warning);
                    // A zip it was still writing would be damaged.
                    try { if (File.Exists(mdmZip)) { File.Delete(mdmZip); } }
                    catch (Exception) { }
                    missing.Add("the MDM diagnostics (stopped on the time limit)");
                }
                else if (File.Exists(mdmZip))
                {
                    Run.Log("MDM diagnostics collected.");
                }
                else
                {
                    Run.Log("mdmdiagnosticstool produced no output; MDM diagnostics are left out.", LogSeverity.Warning);
                    missing.Add("the MDM diagnostics");
                }
            }

            // 2. The IME logs by the package rules, each once: taken out of the MDM diagnostics,
            // which may hold its own copies of them all.
            Run.StepHeader(2, 6, "Collect the app installer logs");
            var mdmPath = Path.Combine(stage, "MDMDiagnostics.zip");
            _mdmZipBytes = File.Exists(mdmPath) ? new FileInfo(mdmPath).Length : 0;
            var stripped = StripImeLogs(mdmPath);
            if (stripped is (int removed, long removedBytes) && removed > 0)
            {
                Run.Log(string.Format(Invariant, "MDMDiagnostics.zip: {0} taken out ({1} MB); they are in IMELogs by the package rules.", TextFormat.Count(removed, "IME log"), TextFormat.Number(removedBytes / 1048576.0)));
                _indexNotes.Add("MDMDiagnostics.zip: " + TextFormat.Count(removed, "IME log") + " (" + PackageRules.Megabytes(removedBytes) + ") taken out of it; they are in IMELogs by the rules above.");
            }
            missing.AddRange(AddImeLogs(stage, stripped == null ? ZipEntryKeys(mdmPath) : new HashSet<string>(StringComparer.Ordinal)));
            // 3. dsregcmd, device sections only.
            Run.StepHeader(3, 6, "Add the device sign-in details");
            if (!HasTime()) { missing.Add("dsregcmd status (no time left)"); }
            else if (!AddDsreg(stage)) { missing.Add("dsregcmd status"); }
            // 4. Win32 app state and remediation results.
            Run.StepHeader(4, 6, "Read the app and remediation state");
            if (!HasTime()) { missing.Add("the app and remediation tables (no time left)"); }
            else { missing.AddRange(AddStateTables(stage)); }
            // 5. This product's own logs, last.
            Run.StepHeader(5, 6, "Add the " + ProductInfo.DisplayName + " logs");
            missing.AddRange(AddOwnLogs(stage));

            Run.StepHeader(6, 6, "Create the zip");
            zipStarted = true;
            // The size before the rules (the IME Logs folder and the MDM diagnostics as collected,
            // plus the rest of the stage) and after (the zip), in this log and the package's index.
            var before = _imeFolderBytes + _mdmZipBytes + ImeRefresh.TreeFiles(stage)
                .Where(f => !f.FullName.StartsWith(Path.Combine(stage, "IMELogs") + "\\", StringComparison.OrdinalIgnoreCase) && !string.Equals(f.Name, "MDMDiagnostics.zip", StringComparison.OrdinalIgnoreCase))
                .Sum(f => f.Length);
            var (zip, firstBytes) = ZipWithinLimit(stage, zipPath);
            if (zip.Added == 0) { throw new InvalidOperationException("no file could be added to the package"); }
            missing.AddRange(zip.LeftOut);
            var size = new FileInfo(zipPath).Length;
            Run.Log(string.Format(Invariant, "Package size: {0} MB collected before the package rules, {1} MB zipped{2}; the limit is {3} MB. {4} lists what was cut or left out.",
                TextFormat.Number(before / 1048576.0), TextFormat.Number(size / 1048576.0),
                firstBytes != size ? string.Format(Invariant, " (a first zip was {0} MB)", TextFormat.Number(firstBytes / 1048576.0)) : "",
                TextFormat.Number(PackageLimitBytes / 1048576.0), IndexName));
            Run.Log(string.Format(Invariant, "Support package created: {0}, {1} MB.", TextFormat.Count(zip.Added, "file"), TextFormat.Number(size / 1048576.0)));
            Run.WriteRecord("file", RunContext.Field("name", PackageName), RunContext.Field("bytes", size));
            if (missing.Count > 0)
            {
                Run.AddFinding(FindingLevel.Warning, "PACKAGE-PARTIAL", "The support package is incomplete",
                    "Missing: " + string.Join("; ", missing) + ". Everything else is in the package; the log says why.",
                    "Give the package to your IT support as it is. If they need the missing parts, restart the device and create it again.");
                return EngineExitCode.Warnings;
            }
            return EngineExitCode.Success;
        }
        catch (Exception ex)
        {
            Run.Log("The support package could not be created: " + ex.Message, LogSeverity.Error);
            Run.AddFinding(FindingLevel.Error, "PACKAGE-FAILED", "The support package could not be created",
                "Collection stopped: " + ex.Message,
                "Restart the device and try again. If it fails again, contact your IT support.");
            // A zip that was being written when it failed is damaged; it must not be delivered.
            try { if (zipStarted && File.Exists(zipPath)) { File.Delete(zipPath); } }
            catch (Exception) { }
            return EngineExitCode.Failed;
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                try { RemoveStage(stage); }
                catch (Exception ex) { Run.Log("The staging folder could not be removed: " + ex.Message, LogSeverity.Warning); }
            }
        }
    }
}
