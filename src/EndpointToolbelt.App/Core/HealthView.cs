using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// The device health as the window shows it: the detailed checks combined into ten rows,
/// so the window opens short. A combined row takes the status, hint and fix of its worst part
/// and names that part in its detail. The device report keeps every detailed check.
/// </summary>
public static class HealthView
{
    public const string IntuneConnection = "Intune connection";
    public const string AppInstaller = HealthChecks.ImeService;
    public const string WorkAccount = "Work account";
    public const string Network = "Network to Intune";

    private sealed class Group
    {
        public string Name;
        public string[] Parts;
        public Func<IReadOnlyDictionary<string, HealthResult>, string> OkDetail;
    }

    private static readonly Group[] Groups =
    {
        new() { Name = IntuneConnection, Parts = new[] { HealthChecks.Enrollment, HealthChecks.MdmCertificate, HealthChecks.LastSync },
            OkDetail = p => "Last sync " + p[HealthChecks.LastSync].Detail },
        new() { Name = AppInstaller, Parts = new[] { HealthChecks.ImeService, HealthChecks.ImeCheckIn },
            // While the IME starts, its row says so in a sentence of its own.
            OkDetail = p => p[HealthChecks.ImeCheckIn].Starting ? p[HealthChecks.ImeCheckIn].Detail : "Last check-in " + p[HealthChecks.ImeCheckIn].Detail },
        // The join type in words; "signed in" only where Windows gives a sign-in token (Entra or hybrid joined).
        new() { Name = WorkAccount, Parts = new[] { HealthChecks.EntraJoined, HealthChecks.Prt },
            OkDetail = p => p[HealthChecks.EntraJoined].Detail + (p[HealthChecks.Prt].Info ? "" : "; signed in") },
        // Windows Hello for this user (Info on a virtual machine or a multi-session host).
        new() { Name = HealthChecks.WindowsHello, Parts = new[] { HealthChecks.WindowsHello } },
        new() { Name = Network, Parts = new[] { HealthChecks.Connectivity } },
        new() { Name = HealthChecks.Profiles, Parts = new[] { HealthChecks.Profiles } },
        new() { Name = HealthChecks.FreeSpace, Parts = new[] { HealthChecks.FreeSpace } },
        new() { Name = HealthChecks.PendingRestart, Parts = new[] { HealthChecks.PendingRestart } },
        new() { Name = HealthChecks.Clock, Parts = new[] { HealthChecks.Clock } },
        new() { Name = HealthChecks.Defender, Parts = new[] { HealthChecks.Defender } },
    };

    /// <summary>
    /// A row's name on screen, in plain words, for the rows whose name is a technical
    /// one; the others keep theirs. The technical name (the row's <see cref="HealthResult.Name"/>, its
    /// key everywhere) starts the row's detail, and the device report and Copy details for IT keep it.
    /// </summary>
    public static string PlainName(string row) => row switch
    {
        IntuneConnection => "Link to your organization",
        AppInstaller => "Work app installer",
        Network => "Connection to work services",
        HealthChecks.Profiles => "Work settings",
        HealthChecks.PendingRestart => "Restart",
        _ => row ?? "",
    };

    /// <summary>The row's detail on screen: its technical name first when the row shows a plain one.</summary>
    public static string PlainDetail(string row, string detail)
    {
        if (PlainName(row) == (row ?? "")) { return detail ?? ""; }
        return string.IsNullOrWhiteSpace(detail) ? row : row + ": " + detail;
    }

    /// <summary>The short name of a part inside a combined row's detail.</summary>
    private static string PartLabel(string name) => name switch
    {
        HealthChecks.Enrollment => "Enrollment",
        HealthChecks.MdmCertificate => "Certificate",
        HealthChecks.LastSync => "Last sync",
        HealthChecks.ImeService => "Service",
        HealthChecks.ImeCheckIn => "Last check-in",
        HealthChecks.EntraJoined => "Join",
        HealthChecks.Prt => "Sign-in token",
        _ => name,
    };

    /// <summary>
    /// The rows for the window, in a fixed order. Checks only the report shows (the local
    /// compliance settings) are left out; a part that is missing leaves its row out.
    /// </summary>
    public static List<HealthResult> Rows(IEnumerable<HealthResult> detailed)
    {
        var byName = new Dictionary<string, HealthResult>(StringComparer.Ordinal);
        foreach (var r in detailed) { byName[r.Name] = r; }
        var rows = new List<HealthResult>();
        foreach (var g in Groups)
        {
            var parts = g.Parts.Where(byName.ContainsKey).Select(n => byName[n]).ToList();
            if (parts.Count == 0) { continue; }
            if (parts.Count == 1 && g.Parts.Length == 1)
            {
                var only = parts[0];
                rows.Add(only.Name == g.Name ? only : Copy(only, g.Name, only.Detail));
                continue;
            }
            rows.Add(Combine(g, parts, byName));
        }
        return rows;
    }

    private static HealthResult Combine(Group g, List<HealthResult> parts, Dictionary<string, HealthResult> byName)
    {
        // Worst first; among equals, the order of the group (the more basic check first).
        var notOk = parts.Where(p => p.Status != HealthStatus.Pass).OrderByDescending(p => p.Status).ToList();
        if (notOk.Count == 0)
        {
            var all = g.Parts.All(byName.ContainsKey);
            var detail = all && g.OkDetail != null ? g.OkDetail(byName) : string.Join("; ", parts.Select(p => p.Detail));
            var ok = new HealthResult { Name = g.Name, Status = HealthStatus.Pass, Detail = detail, Hint = string.Join(" ", parts.Select(p => p.Hint).Where(h => !string.IsNullOrEmpty(h))) };
            ok.FixOverride = HealthFix.None;
            return ok;
        }
        var worst = notOk[0];
        // The worst part only, so the row stays short; the report lists every part.
        // A part whose detail is a sentence of its own goes without its label in front.
        var text = (worst.OwnSentence ? worst.Detail : PartLabel(worst.Name) + ": " + worst.Detail) + (notOk.Count > 1 ? string.Format(CultureInfo.InvariantCulture, " (and {0} more)", notOk.Count - 1) : "");
        var row = Copy(worst, g.Name, text);
        // The fix of the worst part; when that part has none (enrollment, Entra join), the fix of
        // another part that is not OK.
        var fix = notOk.Select(p => p.Fix).FirstOrDefault(f => f != HealthFix.None);
        row.FixOverride = fix;
        // While the IME is not installed no part of the row offers a fix (not Retry work apps and settings either).
        if (notOk.Any(p => p.ImeNotInstalled))
        {
            row.ImeNotInstalled = true;
            row.FixOverride = HealthFix.None;
            row.Hint = HealthChecks.ImeNotInstalledHint;
        }
        return row;
    }

    private static HealthResult Copy(HealthResult source, string name, string detail) => new()
    {
        Name = name,
        Status = source.Status,
        Detail = detail,
        Hint = source.Hint,
        ServiceUnavailable = source.ServiceUnavailable,
        NotRead = source.NotRead,
        Info = source.Info,
        Support = source.Support,
        HomeTitle = source.HomeTitle,
        HomeLine = source.HomeLine,
        OwnSentence = source.OwnSentence,
        ImeNotInstalled = source.ImeNotInstalled,
        FixOverride = source.Fix,
    };

    /// <summary>The line above the rows: the verdict in a few words.</summary>
    public static string Verdict(IReadOnlyCollection<HealthResult> rows)
    {
        var fails = rows.Count(r => r.Status == HealthStatus.Fail);
        var warns = rows.Count(r => r.Status == HealthStatus.Warn);
        if (fails == 0 && warns == 0) { return string.Format(CultureInfo.InvariantCulture, "All {0} checks OK.", rows.Count); }
        var parts = new List<string>();
        if (fails > 0) { parts.Add(Words.Count(fails, "problem")); }
        if (warns > 0) { parts.Add(Words.WarningCount(warns)); }
        // The strip says how many checks are OK too.
        var ok = rows.Count - fails - warns;
        if (ok > 0) { parts.Add(ok.ToString(CultureInfo.InvariantCulture) + " OK"); }
        return string.Join(", ", parts) + ".";
    }

    /// <summary>
    /// The health checks in one line for Copy details for IT, with the rows that are not OK by name:
    /// "1 problem: Clock; 1 warning: Pending restart; 7 OK", "all 9 checks OK", or "not checked yet".
    /// </summary>
    public static string OneLine(IReadOnlyCollection<HealthResult> rows)
    {
        if (rows == null || rows.Count == 0) { return "not checked yet"; }
        var fails = rows.Where(r => r.Status == HealthStatus.Fail).Select(r => r.Name).ToList();
        var warns = rows.Where(r => r.Status == HealthStatus.Warn).Select(r => r.Name).ToList();
        if (fails.Count == 0 && warns.Count == 0) { return string.Format(CultureInfo.InvariantCulture, "all {0} checks OK", rows.Count); }
        var parts = new List<string>();
        if (fails.Count > 0) { parts.Add(Words.Count(fails.Count, "problem") + ": " + string.Join(", ", fails)); }
        if (warns.Count > 0) { parts.Add(Words.WarningCount(warns.Count) + ": " + string.Join(", ", warns)); }
        var ok = rows.Count - fails.Count - warns.Count;
        if (ok > 0) { parts.Add(ok.ToString(CultureInfo.InvariantCulture) + " OK"); }
        return string.Join("; ", parts);
    }

    /// <summary>
    /// The text of the link that shows or hides the rows. Short, on the verdict's line (the
    /// verdict already counts them): "Show all 9", then "Show less"; empty before the first check.
    /// Home shows the count only until Show all, also when a check needs attention.
    /// </summary>
    public static string ToggleText(IReadOnlyCollection<HealthResult> rows, bool showingAll, int? cap = null)
    {
        var ok = rows.Count(r => r.Status == HealthStatus.Pass);
        if (ok == 0 && Hidden(rows, cap) == 0) { return ""; }
        return showingAll ? "Show less" : string.Format(CultureInfo.InvariantCulture, "Show all {0}", rows.Count);
    }

    /// <summary>The link's name for assistive technology: what it shows or hides.</summary>
    public static string ToggleName(IReadOnlyCollection<HealthResult> rows, bool showingAll, int? cap = null)
    {
        var ok = rows.Count(r => r.Status == HealthStatus.Pass);
        if (ok == 0 && Hidden(rows, cap) == 0) { return ""; }
        // Show less on Home (no row before Show all) hides every row again.
        if (showingAll) { return ok == rows.Count || cap == 0 ? "Hide the health checks" : "Show only the checks that need attention"; }
        return string.Format(CultureInfo.InvariantCulture, "Show all {0} health checks", rows.Count);
    }

    /// <summary>
    /// The rows on screen: all of them, or only those that are not OK; at most <paramref name="cap"/>
    /// of those, problems first (the others behind "Show all", the verdict counts them all).
    /// </summary>
    public static List<HealthResult> Visible(IEnumerable<HealthResult> rows, bool showAll, int? cap = null)
    {
        if (showAll) { return rows.ToList(); }
        var attention = rows.Where(r => r.Status != HealthStatus.Pass).ToList();
        if (cap is not int most || attention.Count <= most) { return attention; }
        return attention.Where(r => r.Status == HealthStatus.Fail).Concat(attention.Where(r => r.Status != HealthStatus.Fail)).Take(most).ToList();
    }

    /// <summary>
    /// How many rows that need attention Home shows before "Show all": none. Home shows the health
    /// as a count until Show all.
    /// </summary>
    public const int HomeAttentionRows = 0;

    /// <summary>The rows that need attention that the cap leaves out.</summary>
    public static int Hidden(IReadOnlyCollection<HealthResult> rows, int? cap)
        => cap is int most ? Math.Max(0, rows.Count(r => r.Status != HealthStatus.Pass) - most) : 0;

    /// <summary>
    /// The one line a tool's result shows once the health checks ran again after it: the row
    /// the tool acts on, in its new state, compared with before the tool ran ("Clock: now OK", "Clock:
    /// still a problem", "Clock: OK"). Empty when the tool has no row or the row was not checked.
    /// </summary>
    public static string AfterRun(string row, HealthResult before, HealthResult after)
    {
        if (string.IsNullOrEmpty(row) || after == null) { return ""; }
        // The row by its plain name, as the row shows it.
        var name = PlainName(row);
        static string State(HealthStatus status) => status switch { HealthStatus.Pass => "OK", HealthStatus.Warn => Words.AWarning, _ => "a problem" };
        if (after.ServiceUnavailable) { return name + ": not checked (the service is not available)"; }
        var now = State(after.Status);
        if (before == null || before.ServiceUnavailable) { return name + ": " + now; }
        if (before.Status == after.Status) { return name + ": " + (after.Status == HealthStatus.Pass ? "OK" : "still " + now); }
        return name + ": now " + now;
    }
}
