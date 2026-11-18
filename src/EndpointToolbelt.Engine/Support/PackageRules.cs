using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace EndpointToolbelt.Engine.Support;

/// <summary>One file of the IME Logs folder, as the package rules see it.</summary>
public sealed class ImeLogFile
{
    public string Name { get; set; } = "";
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }
}

/// <summary>What the rules decided for one IME log.</summary>
public enum ImeLogChoice
{
    /// <summary>Taken: the current file of its log, its newest older copy, or a recent older copy of an app install log.</summary>
    Take,

    /// <summary>Left out: an older dated copy of a log whose newest older copy is taken.</summary>
    OlderCopy,

    /// <summary>Left out by default: Sensor and Win32AppInventory (large, rarely useful).</summary>
    LeftOutByDefault,

    /// <summary>Left out: a PSADT log last written more than <see cref="PackageRules.PsadtDays"/> days ago.</summary>
    OldPsadtLog,
}

/// <summary>
/// What goes into the support package from the IME Logs folder, and what is dropped when the
/// package is too large. A package from a virtual desktop was about 40 MB, and the useful
/// logs were what had to be cut by hand. Per log (a family: "AppWorkload.log" and its dated copies
/// "AppWorkload-20261112-093000.log"), the current file and the newest dated copy; for the app
/// install logs (<see cref="AppInstallFamilies"/>) also every dated copy written in the last
/// <see cref="AppInstallCopyHours"/> hours, so a failed install and the user's retry of it are both
/// in the package; PSADT logs only when written in the last <see cref="PsadtDays"/> days; Sensor and
/// Win32AppInventory are left out by default; each file is at most <see cref="MaxFileBytes"/> (the
/// newest part); the whole zip is at most <see cref="MaxPackageBytes"/>. Pure decisions, no I/O.
/// </summary>
public static class PackageRules
{
    /// <summary>The most of one log the package takes: its newest part.</summary>
    public const long MaxFileBytes = 8L * 1024 * 1024;

    /// <summary>The largest package (zipped) before files are dropped in <see cref="DropOrder"/>.</summary>
    public const long MaxPackageBytes = 25L * 1024 * 1024;

    /// <summary>The logs IT support needs most: never dropped for the size limit (their older copies can be).</summary>
    public static readonly IReadOnlyList<string> CoreFamilies = new[] { "AppWorkload", "IntuneManagementExtension", "AgentExecutor", "AppActionProcessor", "HealthScripts", "ClientHealth" };

    /// <summary>
    /// The core logs that record an app install from start to end: every older copy that reaches
    /// into the last <see cref="AppInstallCopyHours"/> hours is taken, and their older copies are the
    /// last files dropped for the size limit.
    /// </summary>
    public static readonly IReadOnlyList<string> AppInstallFamilies = new[] { "AppWorkload", "IntuneManagementExtension", "AgentExecutor" };

    /// <summary>How far back the older copies of the app install logs are all taken.</summary>
    public const int AppInstallCopyHours = 24;

    /// <summary>How far back PSADT logs in the IME Logs folder are taken (by their last write).</summary>
    public const int PsadtDays = 30;

    /// <summary>Large and rarely useful: left out by default.</summary>
    public static readonly IReadOnlyList<string> LeftOutFamilies = new[] { "Sensor", "Win32AppInventory" };

    // "AppWorkload-20261112-093000.log": the family, then the date and time of the copy.
    private static readonly Regex Dated = new(@"^(?<family>.+?)-(?<stamp>\d{8}-\d{6})\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Current = new(@"^(?<family>.+?)\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The log a file belongs to ("AppWorkload" for AppWorkload.log and its dated copies); a file that is not a .log is its own.</summary>
    public static string Family(string fileName)
    {
        var name = fileName ?? "";
        var dated = Dated.Match(name);
        if (dated.Success) { return dated.Groups["family"].Value; }
        var current = Current.Match(name);
        return current.Success ? current.Groups["family"].Value : name;
    }

    /// <summary>A dated older copy ("-yyyyMMdd-HHmmss.log"); its date and time as written, or null for the current file.</summary>
    public static string Stamp(string fileName)
    {
        var dated = Dated.Match(fileName ?? "");
        return dated.Success ? dated.Groups["stamp"].Value : null;
    }

    public static bool IsCore(string family) => CoreFamilies.Any(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));

    public static bool IsLeftOutByDefault(string family) => LeftOutFamilies.Any(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));

    public static bool IsAppInstallLog(string family) => AppInstallFamilies.Any(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A log of the PowerShell App Deployment Toolkit that an app package wrote into the IME Logs
    /// folder: a name that starts with "PSADT_" (a common naming for packages that log there), or the
    /// toolkit's own default name ("..._PSAppDeployToolkit_Install.log").
    /// </summary>
    public static bool IsPsadtLog(string fileName)
    {
        var name = fileName ?? "";
        return name.StartsWith("PSADT_", StringComparison.OrdinalIgnoreCase) || name.IndexOf("PSAppDeployToolkit", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// The choice per file: per family the current file (the newest one without a date) and the newest
    /// dated copy (by its date in the name, then its last write) are taken; for the app install logs
    /// also every dated copy last written within <see cref="AppInstallCopyHours"/> hours before
    /// <paramref name="nowUtc"/> (a copy's content ends at its last write, so that copy reaches into
    /// that window). Older dated copies and the left-out families are not taken, nor a PSADT log last
    /// written more than <see cref="PsadtDays"/> days before <paramref name="nowUtc"/>. Without
    /// <paramref name="nowUtc"/> the rules by age do not apply. In the order of the input.
    /// </summary>
    public static List<(ImeLogFile File, ImeLogChoice Choice)> Plan(IEnumerable<ImeLogFile> files, DateTime? nowUtc = null)
    {
        var list = (files ?? Enumerable.Empty<ImeLogFile>()).Where(f => f != null).ToList();
        var taken = new HashSet<ImeLogFile>();
        var oldPsadt = new HashSet<ImeLogFile>();
        foreach (var family in list.GroupBy(f => Family(f.Name), StringComparer.OrdinalIgnoreCase))
        {
            if (IsLeftOutByDefault(family.Key)) { continue; }
            var current = family.Where(f => Stamp(f.Name) == null).OrderByDescending(f => f.LastWriteUtc).FirstOrDefault();
            var newestCopy = family.Where(f => Stamp(f.Name) != null)
                .OrderByDescending(f => Stamp(f.Name), StringComparer.Ordinal).ThenByDescending(f => f.LastWriteUtc).FirstOrDefault();
            if (current != null) { taken.Add(current); }
            if (newestCopy != null) { taken.Add(newestCopy); }
            if (nowUtc is DateTime now && IsAppInstallLog(family.Key))
            {
                var since = now.AddHours(-AppInstallCopyHours);
                foreach (var copy in family.Where(f => Stamp(f.Name) != null && f.LastWriteUtc >= since)) { taken.Add(copy); }
            }
        }
        if (nowUtc is DateTime at)
        {
            var since = at.AddDays(-PsadtDays);
            foreach (var old in taken.Where(f => IsPsadtLog(f.Name) && f.LastWriteUtc < since).ToList())
            {
                taken.Remove(old);
                oldPsadt.Add(old);
            }
        }
        return list.Select(f => (f, taken.Contains(f) ? ImeLogChoice.Take
            : oldPsadt.Contains(f) ? ImeLogChoice.OldPsadtLog
            : IsLeftOutByDefault(Family(f.Name)) ? ImeLogChoice.LeftOutByDefault : ImeLogChoice.OlderCopy)).ToList();
    }

    /// <summary>One taken IME log in the package, for the size limit: its name, how much it takes zipped, and when it was written.</summary>
    public sealed class Taken
    {
        public string Name { get; set; } = "";
        public long Compressed { get; set; }
        public DateTime LastWriteUtc { get; set; }
    }

    /// <summary>
    /// The IME logs to drop so the package gets to <see cref="MaxPackageBytes"/> or below, in a fixed
    /// order: first the dated older copies of the logs that are not app install logs (the oldest
    /// first), then the current files of the logs that are not core (the largest first), and last the
    /// older copies of the app install logs (<see cref="AppInstallFamilies"/>, the oldest first), which
    /// hold the installs IT support asks about. Core logs' current files are never dropped; when even
    /// that is not enough, the list is all there is to drop and the package stays over the limit.
    /// </summary>
    public static List<string> DropOrder(IEnumerable<Taken> taken, long packageBytes, long limit = MaxPackageBytes)
    {
        var list = (taken ?? Enumerable.Empty<Taken>()).ToList();
        var copies = list.Where(t => Stamp(t.Name) != null).OrderBy(t => Stamp(t.Name), StringComparer.Ordinal).ThenBy(t => t.LastWriteUtc).ToList();
        var order = copies.Where(t => !IsAppInstallLog(Family(t.Name)))
            .Concat(list.Where(t => Stamp(t.Name) == null && !IsCore(Family(t.Name))).OrderByDescending(t => t.Compressed).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            .Concat(copies.Where(t => IsAppInstallLog(Family(t.Name))))
            .ToList();
        var drop = new List<string>();
        var size = packageBytes;
        foreach (var t in order)
        {
            if (size <= limit) { break; }
            drop.Add(t.Name);
            size -= t.Compressed;
        }
        return drop;
    }

    /// <summary>A size in megabytes with one decimal ("3.2 MB").</summary>
    public static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
}
