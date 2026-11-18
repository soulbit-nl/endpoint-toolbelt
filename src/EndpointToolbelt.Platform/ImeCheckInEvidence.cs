using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace EndpointToolbelt.Platform;

/// <summary>
/// What the IME's own log says about its check-ins with the Intune service: the newest success,
/// the newest failure after it, and its newest start. The service (the health view) and the
/// engine (the support package) read the same table through this one evaluation.
/// </summary>
public sealed class ImeCheckInFacts
{
    /// <summary>The newest successful check-in (UTC), or null when the log shows none.</summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>The newest failed check-in (UTC), only when it is newer than the last success.</summary>
    public DateTime? LastFailureUtc { get; set; }

    /// <summary>The error of that failure as the rule gives it (a code group, else the rule's error); not cleaned.</summary>
    public string Error { get; set; } = "";

    /// <summary>The newest start of the IME (UTC): the record that says it delays its first check-in; null when none was seen.</summary>
    public DateTime? StartUtc { get; set; }

    /// <summary>When the IME said it would do its first check-ins after that start (UTC, the latest of them); null when no start was seen.</summary>
    public DateTime? FirstCheckInDueUtc { get; set; }
}

/// <summary>One check-in rule ready to match: its kind ("success", "failure", "start", "recovered", "attempt" or "ignored"), its error and its pattern.</summary>
public sealed class ImeCheckInMatcher
{
    public string Kind { get; set; } = "";
    public string Error { get; set; } = "";
    public Regex Regex { get; set; }
}

public static class ImeCheckInEvidence
{
    /// <summary>
    /// Records of one start (the IME writes its delay lines one after the other) lie at most this far
    /// apart; and a failure written this long before a start line belongs to that start too (the
    /// agent's first lines come just before it says it delays its check-ins).
    /// </summary>
    public static readonly TimeSpan StartSpread = TimeSpan.FromMinutes(2);

    /// <summary>A delay the IME names is believed up to this long; a larger number is not a start-up delay.</summary>
    public const int MaxDelaySeconds = 3600;

    /// <summary>The rules of the shared table, compiled with a time limit.</summary>
    public static List<ImeCheckInMatcher> Matchers(IEnumerable<ImeLogRule> rules)
        => rules.Select(r => new ImeCheckInMatcher
        {
            Kind = r.Kind ?? "",
            Error = r.Error ?? "",
            Regex = new Regex(r.Pattern ?? "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)),
        }).ToList();

    /// <summary>
    /// Evaluates records (time in UTC and message), in any order and from any number of files. The
    /// first rule that matches a record decides what it is; a record no rule matches is no evidence.
    /// Only a "failure" rule makes a failure: a line that merely contains "failed" is nothing. An
    /// "ignored" rule claims its record before the failure rules and makes it no evidence (the
    /// IME's own malformed address: no request left the device). A failure written while the IME was starting (from just before a start until the first
    /// check-in it announced) is not a failed check-in: the IME had not tried to check in yet.
    /// </summary>
    public static ImeCheckInFacts Evaluate(IEnumerable<(DateTime Utc, string Message)> records, IReadOnlyList<ImeCheckInMatcher> rules)
    {
        var facts = new ImeCheckInFacts();
        var failures = new List<(DateTime Utc, string Error)>();
        var starts = new List<(DateTime Utc, int Seconds)>();
        var recoveries = new List<DateTime>();
        var successes = new List<(DateTime Utc, string Workload)>();
        var attempts = new List<(DateTime Utc, string Workload)>();
        foreach (var (utc, message) in records)
        {
            foreach (var rule in rules)
            {
                Match hit;
                try { hit = rule.Regex.Match(message ?? ""); }
                catch (RegexMatchTimeoutException) { continue; }
                if (!hit.Success) { continue; }
                var workload = hit.Groups["workload"] is Group w && w.Success ? w.Value : "";
                if (string.Equals(rule.Kind, "success", StringComparison.OrdinalIgnoreCase))
                {
                    successes.Add((utc, workload));
                }
                else if (string.Equals(rule.Kind, "attempt", StringComparison.OrdinalIgnoreCase))
                {
                    // A workload's check-in begins ("[Win32AppAsync] Starting app check in").
                    attempts.Add((utc, workload));
                }
                else if (string.Equals(rule.Kind, "failure", StringComparison.OrdinalIgnoreCase))
                {
                    var code = hit.Groups["code"];
                    failures.Add((utc, code.Success && code.Value.Length > 0 ? code.Value : rule.Error ?? ""));
                }
                else if (string.Equals(rule.Kind, "ignored", StringComparison.OrdinalIgnoreCase))
                {
                    // Claimed so no later rule reads it as a failure; no evidence of any kind.
                }
                else if (string.Equals(rule.Kind, "recovered", StringComparison.OrdinalIgnoreCase))
                {
                    recoveries.Add(utc);
                }
                else if (string.Equals(rule.Kind, "start", StringComparison.OrdinalIgnoreCase))
                {
                    var seconds = hit.Groups["seconds"];
                    if (seconds.Success && int.TryParse(seconds.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s <= MaxDelaySeconds)
                    {
                        starts.Add((utc, s));
                    }
                }
                break;
            }
        }

        // The starts: one start is a group of delay lines close together; each has a window from just
        // before its first line until the latest check-in it announced.
        starts.Sort((a, b) => a.Utc.CompareTo(b.Utc));
        var windows = new List<(DateTime From, DateTime Due)>();
        foreach (var (utc, seconds) in starts)
        {
            var due = utc.AddSeconds(seconds);
            if (windows.Count > 0 && utc - windows[windows.Count - 1].From <= StartSpread)
            {
                var last = windows[windows.Count - 1];
                windows[windows.Count - 1] = (last.From, due > last.Due ? due : last.Due);
            }
            else
            {
                windows.Add((utc, due));
            }
        }
        if (windows.Count > 0)
        {
            var newest = windows[windows.Count - 1];
            facts.StartUtc = newest.From;
            facts.FirstCheckInDueUtc = newest.Due;
        }

        DateTime? failureUtc = null;
        var failureError = "";
        var effective = new List<DateTime>();
        foreach (var (utc, error) in failures)
        {
            if (windows.Any(w => utc >= w.From - StartSpread && utc < w.Due)) { continue; }
            // A failure the location service recovered from (its fallback to another address in
            // the same second, or a later call that succeeded) is not a failed check-in. A record of
            // kind "recovered" is no check-in itself.
            if (recoveries.Any(r => r >= utc)) { continue; }
            effective.Add(utc);
            if (failureUtc == null || utc >= failureUtc)
            {
                failureUtc = utc;
                failureError = error;
            }
        }

        // A success that ends a workload's check-in ("[Win32AppAsync] End app check in") counts
        // only when no failure was written between that workload's start of the check-in and its end:
        // the IME also writes the end line after a check-in that failed. The log does not say which
        // workload a failure belongs to, so any failure in between keeps it (the cautious reading).
        attempts.Sort((a, b) => a.Utc.CompareTo(b.Utc));
        foreach (var (utc, workload) in successes)
        {
            if (workload.Length > 0)
            {
                var began = attempts.LastOrDefault(a => a.Utc <= utc && string.Equals(a.Workload, workload, StringComparison.OrdinalIgnoreCase));
                if (began.Workload != null && effective.Any(f => f >= began.Utc && f <= utc)) { continue; }
            }
            if (facts.LastSuccessUtc == null || utc > facts.LastSuccessUtc) { facts.LastSuccessUtc = utc; }
        }
        if (failureUtc != null && (facts.LastSuccessUtc == null || failureUtc > facts.LastSuccessUtc))
        {
            facts.LastFailureUtc = failureUtc;
            facts.Error = failureError;
        }
        return facts;
    }
}
