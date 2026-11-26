using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// The device report as one HTML page: the same facts, health checks and recent actions as the
/// text report, plus every work app of the app diagnosis the window shows, each with what the
/// device recorded, its next step and what IT support needs to find it in Intune, and the
/// read-only checks the export ran first. A summary and a table of contents with links to each
/// section come first; the work apps that need attention come first and the rest are folded.
/// The page is a file to hand to IT support, so it is made to be opened anywhere: one file,
/// no script, nothing loaded from elsewhere (its own policy forbids both; its only links point
/// inside the page), and every value is HTML-encoded, because app names and log values come
/// from outside this app.
/// </summary>
public static class HtmlReport
{
    /// <summary>The start of the file name in Downloads; the time of the report follows.</summary>
    public const string FileStem = "EndpointToolbelt-Report";

    /// <summary>"EndpointToolbelt-Report-20261119-162038": the stem with the report's local time.</summary>
    public static string FileName(DateTime local) => FileStem + "-" + local.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>How many of the user's newest actions the page lists.</summary>
    public const int HistoryLines = 20;

    /// <summary>The page forbids script and anything loaded from elsewhere; its own style block is all it needs.</summary>
    internal const string ContentPolicy = "default-src 'none'; style-src 'unsafe-inline'";

    private const string Style =
        ":root{color-scheme:light dark;--text:#1b1b1f;--muted:#5c5f66;--line:#d6d8dd;--card:#f6f7f9;--ok:#0b6a2b;--warn:#8a4b00;--bad:#b3261e;--info:#1f5fa8}"
        + "@media (prefers-color-scheme:dark){:root{--text:#ececf0;--muted:#a9adb6;--line:#3a3d44;--card:#232429;--ok:#6fd08c;--warn:#f0b35a;--bad:#ff8a80;--info:#8ab8f0}}"
        + "body{font:14px/1.45 'Segoe UI',system-ui,sans-serif;color:var(--text);max-width:1100px;margin:24px auto;padding:0 16px}"
        + "h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px;border-bottom:1px solid var(--line);padding-bottom:4px}"
        + "p{margin:6px 0}.note{color:var(--muted)}"
        + "table{border-collapse:collapse;width:100%}th,td{text-align:left;vertical-align:top;padding:5px 10px 5px 0;border-bottom:1px solid var(--line)}"
        + "th{font-weight:600;white-space:nowrap}table.facts th{width:1%;color:var(--muted);font-weight:400}"
        + ".s{font-weight:600;white-space:nowrap}.ok{color:var(--ok)}.warn{color:var(--warn)}.bad{color:var(--bad)}.info{color:var(--info)}.muted{color:var(--muted)}"
        + "details{background:var(--card);border:1px solid var(--line);border-radius:6px;margin:6px 0;padding:6px 12px}"
        + "summary{cursor:pointer;font-weight:600}details table{margin-top:6px}details td,details th{border-bottom:none;padding:2px 10px 2px 0}"
        + "ul{margin:6px 0;padding-left:20px}code{font-family:Consolas,monospace;font-size:13px}"
        + "h3{font-size:15px;margin:18px 0 4px}a{color:var(--info)}nav ul{columns:2;list-style:none;padding-left:0}nav li{margin:2px 0}"
        + ".top{font-size:12px;margin:10px 0 0}.top a{color:var(--muted)}details.rest>summary{font-weight:400}"
        + "@media print{body{margin:0;max-width:none}details{break-inside:avoid}}";

    /// <param name="status">The service's status, or null when the service could not be reached.</param>
    /// <param name="zone">The user's time zone (the local one when null); times on the page are in it.</param>
    /// <param name="firstLine">The first-line facts; null leaves them out.</param>
    /// <param name="apps">The app diagnosis as the window shows it; null when the apps were not read.</param>
    /// <param name="readOnlyChecks">The read-only checks the export ran first (or their newest results); null or empty leaves the section out.</param>
    /// <param name="appsNotRunBecause">Why the app diagnosis did not run for this report; null when it did or was not asked.</param>
    public static string Build(DeviceFacts facts, DsregInfo dsreg, IEnumerable<HealthResult> health, IEnumerable<string> history, DateTimeOffset generated, string productVersion,
        Protocol.StatusInfo status = null, TimeZoneInfo zone = null, FirstLineFacts firstLine = null, AppDiagnosis apps = null,
        IReadOnlyList<ReportCheck> readOnlyChecks = null, string appsNotRunBecause = null)
    {
        zone ??= TimeZoneInfo.Local;
        var checks = (health ?? Enumerable.Empty<HealthResult>()).ToList();
        var ran = (readOnlyChecks ?? Array.Empty<ReportCheck>()).Where(c => c != null).ToList();
        var nowUtc = generated.UtcDateTime;
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\r\n<html lang=\"en\">\r\n<head>\r\n<meta charset=\"utf-8\">\r\n");
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(ContentPolicy).Append("\">\r\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\r\n");
        html.Append("<title>").Append(E(ProductInfo.DisplayName + " device report")).Append("</title>\r\n");
        html.Append("<style>").Append(Style).Append("</style>\r\n</head>\r\n<body>\r\n");
        html.Append("<h1>").Append(E(ProductInfo.DisplayName + " device report")).Append("</h1>\r\n");
        html.Append("<p class=\"note\">For IT support. This page names the device, its user and its work apps; send it only to your IT support.</p>\r\n");

        // The contents: a link to each section, in the order of the page.
        var sections = new List<(string Id, string Title)> { ("summary", "Summary"), ("device", "Device"), ("health", "Health checks") };
        if (ran.Count > 0) { sections.Add(("checks", "Checks")); }
        sections.Add(("apps", "Work apps"));
        sections.Add(("office", "Microsoft 365 Apps"));
        if (status != null) { sections.Add(("intune", "Intune connection")); }
        sections.Add(("recent", "Recent " + ProductInfo.DisplayName + " actions"));
        html.Append("<nav id=\"contents\"><h2>Contents</h2>\r\n<ul>\r\n");
        foreach (var (id, title) in sections) { html.Append("<li><a href=\"#").Append(id).Append("\">").Append(E(title)).Append("</a></li>\r\n"); }
        html.Append("</ul>\r\n</nav>\r\n");

        Section(html, "summary", "Summary");
        Summary(html, checks, ran, apps, appsNotRunBecause, zone, nowUtc);
        Top(html);

        Section(html, "device", "Device");
        FactTable(html, DeviceReport.HeaderLines(facts, dsreg, generated, productVersion, status, zone, firstLine));
        Top(html);

        Section(html, "health", "Health checks");
        foreach (var line in DeviceReport.HealthSummaryLines(checks).Where(l => l.Length > 0)) { Paragraph(html, Pair(line).Label + ": " + Pair(line).Value); }
        if (checks.Count == 0) { Paragraph(html, "No health check was read."); }
        else
        {
            html.Append("<table>\r\n<tr><th>Status</th><th>Check</th><th>What was found</th></tr>\r\n");
            foreach (var r in checks)
            {
                html.Append("<tr><td class=\"s ").Append(HealthClass(r)).Append("\">").Append(E(r.StatusWord)).Append("</td><td>").Append(E(HealthChecks.ReportName(r.Name)))
                    .Append("</td><td>").Append(E(r.ReportDetail));
                if (!string.IsNullOrWhiteSpace(r.Support)) { html.Append("<br><span class=\"muted\">For IT support: ").Append(E(r.Support)).Append("</span>"); }
                html.Append("</td></tr>\r\n");
            }
            html.Append("</table>\r\n");
        }
        Top(html);

        if (ran.Count > 0)
        {
            Section(html, "checks", "Checks");
            Paragraph(html, ran.All(c => c.RanForReport)
                ? "The read-only checks the export ran before it wrote this page. They change nothing on the device."
                : "The read-only checks the export ran before it wrote this page; for a check it could not run, the newest result from the run history, with when and why. They change nothing on the device.");
            foreach (var check in ran) { Check(html, check, zone, nowUtc); }
            Top(html);
        }

        Section(html, "apps", "Work apps");
        if (!string.IsNullOrWhiteSpace(appsNotRunBecause)) { Paragraph(html, "The apps were not read again for this report: " + appsNotRunBecause + ". The list below is the newest the app has."); }
        Apps(html, apps, zone);
        Top(html);

        Section(html, "office", "Microsoft 365 Apps");
        FactTable(html, OfficeInstall.ReportLines(facts?.Office));
        Top(html);

        if (status != null)
        {
            Section(html, "intune", "Intune connection");
            FactTable(html, DeviceSettingRows.IntuneConnectionLines(status.Readouts?.IntuneConnection).Concat(new[] { DeviceSettingRows.MachineLine(status.Readouts?.Machine) }));
            Top(html);
        }

        Section(html, "recent", "Recent " + ProductInfo.DisplayName + " actions");
        var recent = history?.ToList();
        if (recent == null) { Paragraph(html, "The history could not be read."); }
        else if (recent.Count == 0) { Paragraph(html, "No actions recorded yet."); }
        else
        {
            html.Append("<ul>\r\n");
            foreach (var line in recent) { html.Append("<li>").Append(E(RunWords.HistoryLine(line))).Append("</li>\r\n"); }
            html.Append("</ul>\r\n");
        }
        Top(html);
        html.Append("</body>\r\n</html>\r\n");
        return html.ToString();
    }

    /// <summary>
    /// The summary on top: the health checks in counts, the work apps that need attention, and each
    /// read-only check's result in a word, each with a link to its section.
    /// </summary>
    private static void Summary(StringBuilder html, List<HealthResult> health, List<ReportCheck> ran, AppDiagnosis apps, string appsNotRunBecause, TimeZoneInfo zone, DateTime nowUtc)
    {
        html.Append("<table class=\"facts\">\r\n");
        var pass = health.Count(h => h.Status == HealthStatus.Pass);
        var warn = health.Count(h => h.Status == HealthStatus.Warn);
        var fail = health.Count - pass - warn;
        LinkRow(html, "Health checks", "health", health.Count == 0 ? "none read" : Words.Count(pass, "OK", "OK") + ", " + Words.Count(warn, "warning") + ", " + Words.Count(fail, "problem"),
            fail > 0 ? "bad" : warn > 0 ? "warn" : "ok");
        if (apps == null || apps.Run is DiagnosisRunState.NotRun or DiagnosisRunState.Running) { LinkRow(html, "Work apps", "apps", "not read", "muted"); }
        else if (apps.Problem != null) { LinkRow(html, "Work apps", "apps", apps.Problem.Title, "warn"); }
        else
        {
            var attention = apps.Apps.Count(a => a.NeedsAttention);
            var counts = Words.Count(apps.Apps.Count, "app") + ", " + (attention == 0 ? "none needs attention" : attention == 1 ? "1 needs attention" : attention.ToString(CultureInfo.InvariantCulture) + " need attention");
            // Not read again for this report: the row says from when its reading is, and is never shown as a fresh OK.
            var stale = !string.IsNullOrWhiteSpace(appsNotRunBecause);
            var read = stale ? " (" + (apps.CapturedUtc is DateTime captured ? ToolRuns.ShortWhen(captured, zone, nowUtc) + ", " : "") + "not read again)" : "";
            LinkRow(html, "Work apps", "apps", counts + read, attention > 0 ? "warn" : stale ? "muted" : "ok");
        }
        foreach (var check in ran)
        {
            var when = check.RanUtc is DateTime utc ? " (" + ToolRuns.ShortWhen(utc, zone, nowUtc) + (check.RanForReport ? "" : ", not run again") + ")" : "";
            LinkRow(html, check.Name, check.Anchor, (check.Status.Length > 0 ? check.Status : "no result") + when, ToneClass(check.Tone));
        }
        html.Append("</table>\r\n");
    }

    private static void LinkRow(StringBuilder html, string label, string id, string value, string css)
        => html.Append("<tr><th><a href=\"#").Append(id).Append("\">").Append(E(label)).Append("</a></th><td class=\"").Append(css).Append("\">").Append(E(value)).Append("</td></tr>\r\n");

    /// <summary>A read-only check: when it ran (or why not and from when its result is), its result and its cards.</summary>
    private static void Check(StringBuilder html, ReportCheck check, TimeZoneInfo zone, DateTime nowUtc)
    {
        html.Append("<h3 id=\"").Append(check.Anchor).Append("\">").Append(E(check.Name)).Append("</h3>\r\n");
        html.Append("<p class=\"note\">").Append(E(check.When(zone, nowUtc))).Append("</p>\r\n");
        if (check.Status.Length > 0) { html.Append("<p class=\"").Append(ToneClass(check.Tone)).Append("\">").Append(E(check.Status)).Append("</p>\r\n"); }
        foreach (var card in check.Cards)
        {
            var css = CardClass(card.Kind);
            html.Append(card.Kind is CardKind.Error or CardKind.Warning ? "<details open>" : "<details>").Append("<summary><span class=\"").Append(css).Append("\">")
                .Append(E(card.Word)).Append("</span> ").Append(E(card.Title)).Append("</summary>\r\n");
            foreach (var line in (card.Detail ?? "").Replace("\r\n", "\n").Split('\n')) { Paragraph(html, line); }
            if (!string.IsNullOrWhiteSpace(card.NextStep)) { Paragraph(html, "Next step: " + card.NextStep); }
            if (!string.IsNullOrWhiteSpace(card.Support)) { Paragraph(html, "For IT support: " + card.Support); }
            html.Append("</details>\r\n");
        }
    }

    // The colour only repeats the word or sentence next to it.
    private static string ToneClass(Tone tone) => tone switch
    {
        Tone.Success => "ok",
        Tone.Warning => "warn",
        Tone.Error => "bad",
        Tone.Info => "info",
        _ => "muted",
    };

    private static string CardClass(CardKind kind) => kind switch
    {
        CardKind.Ok => "ok",
        CardKind.Warning => "warn",
        CardKind.Error => "bad",
        CardKind.Change => "info",
        _ => "info",
    };

    /// <summary>The link back to the contents at the end of a section.</summary>
    private static void Top(StringBuilder html) => html.Append("<p class=\"top\"><a href=\"#contents\">Back to contents</a></p>\r\n");

    /// <summary>The apps of the diagnosis: the run, a table of every app, then each app's own block (open when it needs attention).</summary>
    private static void Apps(StringBuilder html, AppDiagnosis apps, TimeZoneInfo zone)
    {
        if (apps == null || apps.Run is DiagnosisRunState.NotRun or DiagnosisRunState.Running)
        {
            Paragraph(html, "The work apps were not read before this report was made. In the app, choose \"Check my apps\" on Home (it changes nothing), then save the report again.");
            return;
        }
        Paragraph(html, AppDecisions.RunLine(apps, zone));
        if (apps.Problem != null) { Finding(html, apps.Problem, zone, open: true); }
        var list = apps.Apps.OrderByDescending(a => a.NeedsAttention).ThenBy(a => a.App.Label, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.App.Scope, StringComparer.Ordinal).ToList();
        if (list.Count == 0)
        {
            if (apps.Problem == null) { Paragraph(html, apps.Present ? "No app is recorded for this device or this user." : "The Intune Management Extension has no app state on this device."); }
            return;
        }
        var attention = list.Count(a => a.NeedsAttention);
        var counts = Words.Count(list.Count, "app") + " listed, " + (attention == 0 ? "none needs attention" : attention == 1 ? "1 needs attention" : attention.ToString(CultureInfo.InvariantCulture) + " need attention") + ".";
        if (apps.Records > apps.Listed && apps.Listed > 0) { counts += " The device has " + Words.Count(apps.Records, "app record") + "; the first " + apps.Listed.ToString(CultureInfo.InvariantCulture) + " are listed."; }
        if (apps.OtherScopes > 0) { counts += " Apps of " + Words.Count(apps.OtherScopes, "other user") + " of this device are not shown."; }
        if (!apps.UserScope) { counts += " The apps assigned to this user could not be read; only the device's apps are listed."; }
        Paragraph(html, counts);

        // The apps that need attention first, open; the rest folded in one block, so a long list
        // does not bury them.
        var first = list.Where(a => a.NeedsAttention).ToList();
        var rest = list.Where(a => !a.NeedsAttention).ToList();
        if (first.Count > 0)
        {
            AppTable(html, first);
            foreach (var a in first) { Finding(html, a, zone, open: true); }
        }
        if (rest.Count > 0)
        {
            html.Append(first.Count == 0 ? "<details class=\"rest\" open>" : "<details class=\"rest\">").Append("<summary>")
                .Append(E(first.Count == 0 ? Words.Count(rest.Count, "app") + ", none needs attention" : rest.Count == 1 ? "1 other app that needs no attention" : Words.Count(rest.Count, "other app") + " that need no attention")).Append("</summary>\r\n");
            AppTable(html, rest);
            foreach (var a in rest) { Finding(html, a, zone, open: false); }
            html.Append("</details>\r\n");
        }
    }

    private static void AppTable(StringBuilder html, IEnumerable<AppFinding> apps)
    {
        html.Append("<table>\r\n<tr><th>Status</th><th>App</th><th>Assigned to</th><th>Next step</th></tr>\r\n");
        foreach (var a in apps)
        {
            html.Append("<tr><td class=\"s ").Append(AppClass(a)).Append("\">").Append(E(StatusWord(a))).Append("</td><td>").Append(E(a.App.Label))
                .Append("</td><td>").Append(E(Where(a))).Append("</td><td>").Append(E(a.NextText)).Append("</td></tr>\r\n");
        }
        html.Append("</table>\r\n");
    }

    /// <summary>One app (or the diagnosis's own problem) with everything the app's card and Copy details for IT say about it.</summary>
    private static void Finding(StringBuilder html, AppFinding finding, TimeZoneInfo zone, bool open)
    {
        var hasApp = !string.IsNullOrEmpty(finding.App?.AppId);
        html.Append(open ? "<details open>" : "<details>").Append("<summary>").Append(E(hasApp ? finding.App.Label : finding.Title));
        if (hasApp) { html.Append(" <span class=\"").Append(AppClass(finding)).Append("\">(").Append(E(StatusWord(finding))).Append(")</span>"); }
        html.Append("</summary>\r\n");
        if (hasApp) { Paragraph(html, finding.Title); }
        foreach (var text in new[] { finding.Text, finding.Evidence, finding.Uncertainty, finding.Caveat }) { Paragraph(html, text); }
        if (!string.IsNullOrWhiteSpace(finding.NextText)) { Paragraph(html, "Next step: " + finding.NextText); }
        if (!string.IsNullOrWhiteSpace(finding.Support)) { Paragraph(html, "For IT support: " + finding.Support); }
        if (!string.IsNullOrWhiteSpace(finding.Record)) { Paragraph(html, finding.Record); }
        if (hasApp)
        {
            html.Append("<table class=\"facts\">\r\n");
            foreach (var (label, value) in AppDecisions.DetailFacts(finding, zone)) { FactRow(html, label, value); }
            html.Append("</table>\r\n");
            if (finding.NameFromEarlierLog) { Paragraph(html, "Name from an earlier IME log."); }
        }
        html.Append("</details>\r\n");
    }

    /// <summary>The state shown for an app: after a remedy what the check after it found, else the observed state.</summary>
    private static string StatusWord(AppFinding finding) => finding.AfterRemedy is PostCheckState state ? PostCheck.Word(state) : AppDecisions.Word(finding);

    private static string Where(AppFinding finding)
    {
        var where = finding.ForBothScopes ? "this device and this user" : finding.App.Scope == "user" ? "this user" : "this device";
        return finding.App.KindWord.Length > 0 ? where + " (" + finding.App.KindWord + ")" : where;
    }

    // The colour only repeats the word next to it.
    private static string AppClass(AppFinding finding) => finding.Observation switch
    {
        AppObservation.Detected => "ok",
        AppObservation.Failed => finding.Severity == FindingSeverity.Error ? "bad" : "warn",
        AppObservation.AwaitingEvidence => "info",
        _ => finding.NeedsAttention ? "warn" : "muted",
    };

    private static string HealthClass(HealthResult row) => row.Status switch
    {
        HealthStatus.Pass => row.Info ? "info" : "ok",
        HealthStatus.Warn => "warn",
        _ => "bad",
    };

    private static void Section(StringBuilder html, string id, string title) => html.Append("<h2 id=\"").Append(id).Append("\">").Append(E(title)).Append("</h2>\r\n");

    private static void Paragraph(StringBuilder html, string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) { html.Append("<p>").Append(E(text.Trim())).Append("</p>\r\n"); }
    }

    /// <summary>The report's "Label : text" lines as a two-column table; an empty line is left out.</summary>
    private static void FactTable(StringBuilder html, IEnumerable<string> lines)
    {
        html.Append("<table class=\"facts\">\r\n");
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var (label, value) = Pair(line);
            FactRow(html, label, value);
        }
        html.Append("</table>\r\n");
    }

    private static void FactRow(StringBuilder html, string label, string value)
        => html.Append("<tr><th>").Append(E(label)).Append("</th><td>").Append(E(value)).Append("</td></tr>\r\n");

    /// <summary>"Label   : text" split at its first colon; a line without one is all text.</summary>
    internal static (string Label, string Value) Pair(string line)
    {
        var colon = (line ?? "").IndexOf(':');
        return colon <= 0 ? ("", (line ?? "").Trim()) : (line.Substring(0, colon).Trim(), line.Substring(colon + 1).Trim());
    }

    /// <summary>HTML-encodes a value, and drops control characters a log value can carry.</summary>
    internal static string E(string text)
    {
        if (string.IsNullOrEmpty(text)) { return ""; }
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            // Control characters except line breaks and tabs, and format characters (a direction
            // override or a zero-width character would make a value read differently than it is).
            if ((!char.IsControl(c) || c == '\n' || c == '\t') && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format) { clean.Append(c); }
        }
        return WebUtility.HtmlEncode(clean.ToString());
    }
}
