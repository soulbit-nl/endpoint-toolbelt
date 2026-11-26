using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// A run as a timeline, for the technical log: every progress line, warning, finding and change
/// in the order the engine wrote them, each with its time in the user's own time zone
/// (HH:mm:ss). A record without a time gets no time rather than the one before it, so no line
/// claims a moment it does not have. When the date or the UTC offset changes during a run (past
/// midnight, or a change to or from summer time), one marker line says so before the next line.
/// </summary>
/// <remarks>
/// Records are added from a cursor, so a live run that only grows is formatted once per record.
/// The lines of the same records are always the same, so the lines of a run so far are the start
/// of the lines of the whole run.
/// </remarks>
public sealed class Timeline
{
    private readonly TimeZoneInfo _zone;
    private int _cursor;
    private TimeSpan? _offset;
    private DateTime? _date;

    public Timeline(TimeZoneInfo zone = null) => _zone = zone ?? TimeZoneInfo.Local;

    public List<string> Lines { get; } = new();

    /// <summary>Adds the records from the cursor on: the ones before it were added by an earlier call.</summary>
    public Timeline Add(IReadOnlyList<EngineRecord> records)
    {
        for (; _cursor < records.Count; _cursor++)
        {
            if (records[_cursor] != null) { AddRecord(records[_cursor]); }
        }
        return this;
    }

    private void AddRecord(EngineRecord r)
    {
        switch (r.Type)
        {
            case "progress":
                var prefix = r.Severity is "warning" ? "WARNING: " : r.Severity is "error" ? "ERROR: " : "";
                Lines.Add(Time(r) + prefix + TimeText.ToLocal(r.Text, _zone));
                break;
            case "warning":
                Lines.Add(Time(r) + "WARNING: " + TimeText.ToLocal(r.Text, _zone));
                break;
            case "change":
                Lines.Add(Time(r) + "CHANGE: " + TimeText.ToLocal(r.Text, _zone));
                break;
            case "mdm-queued":
                var at = TimeText.ParseUtc(r.At);
                Lines.Add(Time(r) + (at.HasValue && TimeText.TryLocal(at.Value, _zone, out var local)
                    ? "Intune sync queued: Windows retries it at about " + local.ToString("HH:mm", CultureInfo.InvariantCulture) + "."
                    : "Intune sync queued: Windows retries it automatically."));
                break;
            case "app-diagnosis" when r.App != null:
                // One line per app of the diagnosis, with the record's own time in the user's zone.
                var app = r.App;
                var observed = app.ObservedUtc is DateTime seen && TimeText.TryLocal(seen, _zone, out var seenLocal)
                    ? "recorded " + seenLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : "time of record unknown";
                Lines.Add(Time(r) + "APP " + app.Verdict + " " + app.Label + " [" + app.Scope + (app.Intent.Length > 0 ? ", " + app.Intent : "") + "]: " + app.State
                    + (app.Code.Length > 0 ? "; error " + app.Code : "") + "; " + observed + (app.Attempt.Length > 0 ? "; newest attempt: " + app.Attempt : ""));
                break;
            case "ip-renew" when r.Renewal != null:
                // The adapter's kind and whether it has a usable lease; never its name or an address.
                Lines.Add(Time(r) + IpRenewal.LogLine(r.Run, r.Renewal));
                break;
            case "wu-check" when r.WuCheck != null:
                // The check's verdict and facts as tokens; never a server, a path or a name.
                Lines.Add(Time(r) + WuCheck.LogLine(r.Run, r.WuCheck));
                break;
            case "defender-check" when r.DefenderCheck != null:
                // The check's verdict and counts as tokens; never a file, a rule or a name.
                Lines.Add(Time(r) + DefenderCheck.LogLine(r.Run, r.DefenderCheck));
                break;
            case "finding":
                var time = Time(r);
                var word = FindingSeverityText.Word(FindingSeverityText.Parse(r.Severity));
                var id = string.IsNullOrEmpty(r.Id) ? "" : " " + r.Id;
                Lines.Add(time + "FINDING " + word + id + ": " + TimeText.ToLocal(r.Title, _zone));
                // The finding's other parts are indented under it, past the time.
                var indent = new string(' ', time.Length + 4);
                foreach (var line in SplitLines(TimeText.ToLocal(r.Detail, _zone))) { Lines.Add(indent + line); }
                if (!string.IsNullOrWhiteSpace(r.NextStep)) { Lines.Add(indent + "Next step: " + TimeText.ToLocal(r.NextStep, _zone)); }
                if (!string.IsNullOrWhiteSpace(r.Support)) { Lines.Add(indent + "For IT support: " + TimeText.ToLocal(r.Support, _zone)); }
                break;
        }
    }

    /// <summary>The record's local time and a space, or nothing; adds the marker line when the date or offset changed.</summary>
    private string Time(EngineRecord record)
    {
        if (!record.Utc.HasValue || !TimeText.TryLocal(record.Utc.Value, _zone, out var local)) { return ""; }
        var offset = _zone.GetUtcOffset(DateTime.SpecifyKind(record.Utc.Value, DateTimeKind.Utc));
        if (_offset.HasValue && (offset != _offset.Value || local.Date != _date))
        {
            Lines.Add("----- " + local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ", " + TimeText.ZoneLabel(_zone, record.Utc.Value) + " -----");
        }
        _offset = offset;
        _date = local.Date;
        return local.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " ";
    }

    private static IEnumerable<string> SplitLines(string text)
        => string.IsNullOrWhiteSpace(text) ? Enumerable.Empty<string>() : text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0);
}

/// <summary>How the shown log follows a new set of lines.</summary>
public static class LogUpdate
{
    /// <summary>
    /// The lines to append when <paramref name="next"/> starts with exactly what is shown; null
    /// when it does not (a stored result can drop records in the middle), and the log is then
    /// rebuilt from <paramref name="next"/>.
    /// </summary>
    public static IReadOnlyList<string> Append(IReadOnlyList<string> shown, IReadOnlyList<string> next)
    {
        if (next.Count < shown.Count) { return null; }
        for (var i = shown.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(shown[i], next[i], StringComparison.Ordinal)) { return null; }
        }
        return next.Skip(shown.Count).ToList();
    }
}
