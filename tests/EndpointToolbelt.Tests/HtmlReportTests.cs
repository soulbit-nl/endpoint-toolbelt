using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using EndpointToolbelt.App.Core;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The device report as a web page: one file that loads and runs nothing, encodes every value,
/// and carries the device facts, the health checks, every work app of the diagnosis and the
/// user's recent actions.
/// </summary>
public class HtmlReportTests
{
    private static readonly DateTimeOffset Generated = new(2026, 11, 19, 16, 20, 38, TimeSpan.Zero);

    private static AppFinding App(string id, string name, string verdict, AppObservation observation, string scope = "device")
        => new()
        {
            App = new AppEvidence { AppId = id, Name = name, Scope = scope, Verdict = verdict },
            Observation = observation,
            Severity = observation == AppObservation.Failed ? FindingSeverity.Error : FindingSeverity.Ok,
            Title = name + " title",
            Text = name + " text",
            NextText = observation == AppObservation.Failed ? "Hand this over to IT support" : "",
            Support = observation == AppObservation.Failed ? "Check the install command" : "",
        };

    private static AppDiagnosis Diagnosis(params AppFinding[] apps)
    {
        var diagnosis = new AppDiagnosis { Run = DiagnosisRunState.Finished, CapturedUtc = Generated.UtcDateTime, Present = true, Records = apps.Length, Listed = apps.Length };
        diagnosis.Apps.AddRange(apps);
        return diagnosis;
    }

    private static string Page(IEnumerable<HealthResult> health = null, IEnumerable<string> history = null, AppDiagnosis apps = null)
        => HtmlReport.Build(new DeviceFacts { ComputerName = "PC1" }, null, health ?? new List<HealthResult>(), history ?? Array.Empty<string>(), Generated, "1.2.3", null, TimeZoneInfo.Utc, null, apps);

    [Fact]
    public void The_page_is_one_file_that_runs_no_script_and_loads_nothing()
    {
        var page = Page(apps: Diagnosis(App("11111111-2222-3333-4444-555555555555", "Contoso Reader", "PACKAGING", AppObservation.Failed)));
        Assert.StartsWith("<!DOCTYPE html>", page);
        Assert.Contains("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">", page);
        Assert.Contains("<meta charset=\"utf-8\">", page);
        foreach (var never in new[] { "<script", "<link", "<img", "<iframe", "<form", "src=", "url(", "@import", "javascript:", "http://", "https://", " on" + "click", " on" + "load", " on" + "error" })
        {
            Assert.DoesNotContain(never, page, StringComparison.OrdinalIgnoreCase);
        }
        // Its only links point inside the page: the contents and "Back to contents".
        var links = System.Text.RegularExpressions.Regex.Matches(page, "<a [^>]*>").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).ToList();
        Assert.NotEmpty(links);
        Assert.All(links, l => Assert.Matches("^<a href=\"#[a-z0-9-]+\">$", l));
        Assert.Equal(links.Count, System.Text.RegularExpressions.Regex.Matches(page, "href=").Count);
    }

    [Fact]
    public void Every_value_from_outside_is_encoded()
    {
        var health = new List<HealthResult> { new() { Name = "Clock", Status = HealthStatus.Fail, Detail = "<b>off</b> & late", Support = "<i>look</i>" } };
        var history = new[] { "2026-11-19 16:00:00 +00:00;user;<u>Tool</u>;<s>done</s>" };
        var app = App("11111111-2222-3333-4444-555555555555", "<script>alert(1)</script>", "PACKAGING", AppObservation.Failed);
        app.Record = "</details><p>";
        var page = Page(health, history, Diagnosis(app));
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", page);
        Assert.Contains("&lt;b&gt;off&lt;/b&gt; &amp; late", page);
        Assert.Contains("&lt;i&gt;look&lt;/i&gt;", page);
        Assert.Contains("&lt;u&gt;Tool&lt;/u&gt;", page);
        Assert.Contains("&lt;/details&gt;&lt;p&gt;", page);
        foreach (var raw in new[] { "<script>", "<b>off", "<i>look", "<u>Tool", "<s>done", "</details><p>" }) { Assert.DoesNotContain(raw, page); }
        // Control characters of a log value are dropped; a quote cannot end an attribute.
        Assert.Equal("ab", HtmlReport.E("a\0\u001b\rb"));
        // Format characters (a direction override, a zero-width space) are dropped too.
        Assert.Equal("setupexe.txt", HtmlReport.E("setup‮exe.txt​"));
        Assert.DoesNotContain("\"", HtmlReport.E("a\"b"));
        Assert.Equal("", HtmlReport.E(null));
    }

    [Fact]
    public void The_device_facts_and_the_health_checks_are_those_of_the_text_report()
    {
        var health = new List<HealthResult>
        {
            new() { Name = "Clock", Status = HealthStatus.Fail, Detail = "10 minutes off" },
            new() { Name = "Disk space", Status = HealthStatus.Pass, Detail = "40 GB free" },
        };
        var page = Page(health);
        Assert.Contains("<h1>Endpoint Toolbelt device report</h1>", page);
        Assert.Contains("<tr><th>Generated</th><td>2026-11-19 16:20:38 +00:00 (Endpoint Toolbelt 1.2.3)</td></tr>", page);
        Assert.Contains("<tr><th>Device</th><td>PC1  |  Serial: Unknown  |  Model: Unknown</td></tr>", page);
        Assert.Contains("<tr><th>Service</th><td>not available</td></tr>", page);
        Assert.Contains("<p>Problems: " + HealthChecks.ReportName("Clock") + "</p>", page);
        Assert.Contains("<tr><td class=\"s bad\">Problem</td><td>" + HealthChecks.ReportName("Clock") + "</td><td>10 minutes off</td></tr>", page);
        Assert.Contains("<tr><td class=\"s ok\">OK</td><td>" + HealthChecks.ReportName("Disk space") + "</td><td>40 GB free</td></tr>", page);
        // Without the service there is no Intune connection section; the history says when it is empty.
        Assert.DoesNotContain("Intune connection</h2>", page);
        Assert.DoesNotContain("href=\"#intune\"", page);
        Assert.Contains("<p>No actions recorded yet.</p>", page);
        Assert.Contains("<p>The history could not be read.</p>", HtmlReport.Build(new DeviceFacts(), null, health, null, Generated, "1.2.3", null, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Every_app_is_listed_and_the_ones_that_need_attention_come_first_and_open()
    {
        var failed = App("11111111-2222-3333-4444-555555555555", "Contoso Reader", "PACKAGING", AppObservation.Failed);
        var fine = App("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "Adatum Viewer", "INSTALLED", AppObservation.Detected, "user");
        var diagnosis = Diagnosis(fine, failed);
        diagnosis.OtherScopes = 2;
        var page = Page(apps: diagnosis);
        Assert.Contains("<p>2 apps listed, 1 needs attention. Apps of 2 other users of this device are not shown.</p>", page);
        Assert.Contains("<tr><td class=\"s bad\">Failed</td><td>Contoso Reader</td><td>this device</td><td>Hand this over to IT support</td></tr>", page);
        Assert.Contains("<tr><td class=\"s ok\">Detected</td><td>Adatum Viewer</td><td>this user</td><td></td></tr>", page);
        Assert.True(page.IndexOf("<td>Contoso Reader</td>", StringComparison.Ordinal) < page.IndexOf("<td>Adatum Viewer</td>", StringComparison.Ordinal));
        Assert.Contains("<details open><summary>Contoso Reader <span class=\"bad\">(Failed)</span></summary>", page);
        Assert.Contains("<details><summary>Adatum Viewer <span class=\"ok\">(Detected)</span></summary>", page);
        Assert.Contains("<p>Contoso Reader text</p>", page);
        Assert.Contains("<p>Next step: Hand this over to IT support</p>", page);
        Assert.Contains("<p>For IT support: Check the install command</p>", page);
        Assert.Contains("<tr><th>App ID</th><td>11111111-2222-3333-4444-555555555555</td></tr>", page);
        Assert.Contains("<tr><th>Assigned to</th><td>you</td></tr>", page);
    }

    [Fact]
    public void A_report_made_before_the_apps_were_read_says_so()
    {
        Assert.Contains("The work apps were not read before this report was made.", Page());
        Assert.Contains("The work apps were not read before this report was made.", Page(apps: new AppDiagnosis { Run = DiagnosisRunState.Running }));
        Assert.DoesNotContain("<details", Page());
        // A diagnosis that could not read the apps shows its own finding.
        var offline = new AppDiagnosis { Run = DiagnosisRunState.Interrupted, Problem = new AppFinding { Title = "The apps could not be read", Text = "The service did not answer." } };
        var page = Page(apps: offline);
        Assert.Contains("<details open><summary>The apps could not be read</summary>", page);
        Assert.Contains("<p>The service did not answer.</p>", page);
    }

    [Fact]
    public void The_file_name_carries_the_time_and_a_line_splits_at_its_first_colon()
    {
        Assert.Equal("EndpointToolbelt-Report-20261119-162038", HtmlReport.FileName(new DateTime(2026, 11, 19, 16, 20, 38)));
        Assert.Equal(("Device", "PC1  |  Serial: 1"), HtmlReport.Pair("Device    : PC1  |  Serial: 1"));
        Assert.Equal(("", "no colon here"), HtmlReport.Pair(" no colon here "));
        Assert.Equal(("", ""), HtmlReport.Pair(null));
    }

    [Fact]
    public void For_IT_has_the_button_next_to_the_text_report()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EndpointToolbelt.slnx"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        var xaml = XDocument.Load(Path.Combine(dir.FullName, "src", "EndpointToolbelt.App", "Ui", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var button = xaml.Descendants().Single(e => (string)e.Attribute(x + "Name") == "HtmlReportButton");
        Assert.Equal("Export HTML", (string)button.Attribute("Content"));
        Assert.StartsWith("Export report as HTML: ", (string)button.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.HelpText"));
        Assert.Equal("OnHtmlReport", (string)button.Attribute("Click"));
        Assert.Contains("Downloads", (string)button.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.HelpText"));
        Assert.Equal("ReportButton", (string)button.ElementsBeforeSelf().Last().Attribute(x + "Name"));
        // The three report actions side by side in one row of equal widths (a uniform grid of three).
        var row = button.Parent;
        Assert.Equal("UniformGrid", row.Name.LocalName);
        Assert.Equal("3", (string)row.Attribute("Columns"));
        Assert.Equal(new[] { "ReportButton", "HtmlReportButton", "PackageButton" }, row.Elements().Select(e => (string)e.Attribute(x + "Name")));
        // A text wraps inside its button when the window is narrow; the row never breaks.
        Assert.All(row.Elements(), b => Assert.Equal("{StaticResource RowButton}", (string)b.Attribute("Style")));
    }

    private static List<ReportCheck> Checks()
    {
        var defender = ReportCheck.Ran("Check Defender blocks", "Microsoft Defender logged 2 rule blocks in the last 14 days.", Tone.Warning,
            new[]
            {
                ResultCard.From(FindingSeverity.Warning, "Blocked by Defender: <rule>", "c.exe was blocked twice.", "Copy details for IT.", "Rule c0033c00"),
                ResultCard.From(FindingSeverity.Ok, "Last quick scan: 2026-11-19 03:34"),
                ResultCard.NothingChanged(),
            }, Generated.UtcDateTime.AddMinutes(-2));
        var stored = new ReportCheck { Name = "Check Windows Update", NotRunBecause = "the hourly limit of read-only checks was reached", RanUtc = Generated.UtcDateTime.AddHours(-3), Status = "Windows Update works.", Tone = Tone.Success };
        var never = ReportCheck.Stored("Check compliance settings", ReportCheck.SwitchedOff, null);
        return new List<ReportCheck> { stored, defender, never };
    }

    [Fact]
    public void The_page_starts_with_its_contents_and_a_summary_that_links_to_each_section()
    {
        var failed = App("11111111-2222-3333-4444-555555555555", "Contoso Reader", "PACKAGING", AppObservation.Failed);
        var health = new List<HealthResult> { new() { Name = "Clock", Status = HealthStatus.Fail, Detail = "off" }, new() { Name = "Disk space", Status = HealthStatus.Pass, Detail = "ok" } };
        var page = HtmlReport.Build(new DeviceFacts { ComputerName = "PC1" }, null, health, Array.Empty<string>(), Generated, "1.2.3", null, TimeZoneInfo.Utc, null, Diagnosis(failed), Checks());
        Assert.Contains("<nav id=\"contents\"><h2>Contents</h2>", page);
        foreach (var id in new[] { "summary", "device", "health", "checks", "apps", "office", "recent" })
        {
            Assert.Contains("<li><a href=\"#" + id + "\">", page);
            Assert.Contains("<h2 id=\"" + id + "\">", page);
        }
        // The contents come first, then the summary, then the sections.
        Assert.True(page.IndexOf("id=\"contents\"", StringComparison.Ordinal) < page.IndexOf("id=\"summary\"", StringComparison.Ordinal));
        Assert.True(page.IndexOf("id=\"summary\"", StringComparison.Ordinal) < page.IndexOf("id=\"device\"", StringComparison.Ordinal));
        Assert.Contains("<tr><th><a href=\"#health\">Health checks</a></th><td class=\"bad\">1 OK, 0 warnings, 1 problem</td></tr>", page);
        Assert.Contains("<tr><th><a href=\"#apps\">Work apps</a></th><td class=\"warn\">1 app, 1 needs attention</td></tr>", page);
        Assert.Contains("<tr><th><a href=\"#check-check-defender-blocks\">Check Defender blocks</a></th><td class=\"warn\">Microsoft Defender logged 2 rule blocks in the last 14 days. (today 16:18)</td></tr>", page);
        Assert.Contains("<tr><th><a href=\"#check-check-windows-update\">Check Windows Update</a></th><td class=\"ok\">Windows Update works. (today 13:20, not run again)</td></tr>", page);
        Assert.Contains("<p class=\"top\"><a href=\"#contents\">Back to contents</a></p>", page);
    }

    [Fact]
    public void Each_read_only_check_shows_when_it_ran_or_why_not_and_its_cards()
    {
        var page = HtmlReport.Build(new DeviceFacts(), null, new List<HealthResult>(), Array.Empty<string>(), Generated, "1.2.3", null, TimeZoneInfo.Utc, null, null, Checks());
        Assert.Contains("<h3 id=\"check-check-defender-blocks\">Check Defender blocks</h3>", page);
        Assert.Contains("<p class=\"note\">Run for this report, today 16:18.</p>", page);
        Assert.Contains("<p class=\"warn\">Microsoft Defender logged 2 rule blocks in the last 14 days.</p>", page);
        // A warning is open, a good card folded; a note about the run is not a card.
        Assert.Contains("<details open><summary><span class=\"warn\">WARNING</span> Blocked by Defender: &lt;rule&gt;</summary>", page);
        Assert.Contains("<p>Next step: Copy details for IT.</p>", page);
        Assert.Contains("<p>For IT support: Rule c0033c00</p>", page);
        Assert.Contains("<details><summary><span class=\"ok\">OK</span> Last quick scan: 2026-11-19 03:34</summary>", page);
        Assert.DoesNotContain(ResultCard.NothingChangedText, page);
        Assert.Contains("<p class=\"note\">Not run again for this report: the hourly limit of read-only checks was reached. The result below is the newest in your run history, from today 13:20.</p>", page);
        Assert.Contains("<p class=\"note\">Not run for this report: your organization switched this check off.</p>", page);
        // Without checks the section is not there.
        Assert.DoesNotContain("id=\"checks\"", Page());
    }

    [Fact]
    public void Apps_not_read_again_for_the_report_say_so_in_the_summary_and_never_read_as_a_fresh_OK()
    {
        // The service was busy: the export kept the window's reading of three hours before.
        var fine = App("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "Adatum Viewer", "INSTALLED", AppObservation.Detected);
        var earlier = Diagnosis(fine);
        earlier.CapturedUtc = Generated.UtcDateTime.AddHours(-3);
        var stale = HtmlReport.Build(new DeviceFacts(), null, new List<HealthResult>(), Array.Empty<string>(), Generated, "1.2.3", null, TimeZoneInfo.Utc, null, earlier, null, "another action was running");
        Assert.Contains("<tr><th><a href=\"#apps\">Work apps</a></th><td class=\"muted\">1 app, none needs attention (today 13:20, not read again)</td></tr>", stale);
        // Read for the report: green as before.
        var fresh = HtmlReport.Build(new DeviceFacts(), null, new List<HealthResult>(), Array.Empty<string>(), Generated, "1.2.3", null, TimeZoneInfo.Utc, null, earlier);
        Assert.Contains("<tr><th><a href=\"#apps\">Work apps</a></th><td class=\"ok\">1 app, none needs attention</td></tr>", fresh);
    }

    [Fact]
    public void The_sentence_above_the_checks_names_a_stored_result_when_there_is_one()
    {
        var mixed = HtmlReport.Build(new DeviceFacts(), null, new List<HealthResult>(), Array.Empty<string>(), Generated, "1.2.3", null, TimeZoneInfo.Utc, null, null, Checks());
        Assert.Contains("<p>The read-only checks the export ran before it wrote this page; for a check it could not run, the newest result from the run history, with when and why. They change nothing on the device.</p>", mixed);
        var allRan = new List<ReportCheck> { ReportCheck.Ran("Check Microsoft connections", "The 5 Microsoft services answered.", Tone.Success, null, Generated.UtcDateTime) };
        Assert.Contains("<p>The read-only checks the export ran before it wrote this page. They change nothing on the device.</p>",
            HtmlReport.Build(new DeviceFacts(), null, new List<HealthResult>(), Array.Empty<string>(), Generated, "1.2.3", null, TimeZoneInfo.Utc, null, null, allRan));
    }

    [Fact]
    public void The_apps_that_need_attention_come_first_and_the_rest_is_folded()
    {
        var failed = App("11111111-2222-3333-4444-555555555555", "Contoso Reader", "PACKAGING", AppObservation.Failed);
        var fine = App("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "Adatum Viewer", "INSTALLED", AppObservation.Detected, "user");
        var page = Page(apps: Diagnosis(fine, failed));
        Assert.Contains("<details class=\"rest\"><summary>1 other app that needs no attention</summary>", page);
        Assert.True(page.IndexOf("<details class=\"rest\">", StringComparison.Ordinal) > page.IndexOf("<td>Contoso Reader</td>", StringComparison.Ordinal));
        Assert.True(page.IndexOf("<details class=\"rest\">", StringComparison.Ordinal) < page.IndexOf("<td>Adatum Viewer</td>", StringComparison.Ordinal));
        // With nothing that needs attention, the list is open.
        Assert.Contains("<details class=\"rest\" open><summary>1 app, none needs attention</summary>", Page(apps: Diagnosis(fine)));
        // An app diagnosis the service did not run again says so above the list.
        var notRun = HtmlReport.Build(new DeviceFacts(), null, new List<HealthResult>(), Array.Empty<string>(), Generated, "1.2.3", null, TimeZoneInfo.Utc, null, Diagnosis(fine), null, "another action was running");
        Assert.Contains("<p>The apps were not read again for this report: another action was running. The list below is the newest the app has.</p>", notRun);
    }
}
