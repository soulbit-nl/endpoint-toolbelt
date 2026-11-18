using System;
using System.Globalization;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// Times the engine reports, shown in the user's own clock. The engine runs as SYSTEM and does
/// not share the user's time zone (for example with time zone redirection on a session host),
/// so it writes moments as [[t:&lt;UTC&gt;;&lt;format&gt;]] tokens, converted here.
/// </summary>
public static class TimeText
{
    private static readonly Regex Token = new(@"\[\[t:(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z);([^\]]{1,40})\]\]", RegexOptions.CultureInvariant, TimedRegex.Limit);

    /// <summary>Replaces every time token by the local time in its format.</summary>
    public static string ToLocal(string text, TimeZoneInfo zone = null)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf("[[t:", StringComparison.Ordinal) < 0) { return text; }
        zone ??= TimeZoneInfo.Local;
        try
        {
            return ReplaceTokens(text, zone);
        }
        catch (RegexMatchTimeoutException)
        {
            return text;
        }
    }

    private static string ReplaceTokens(string text, TimeZoneInfo zone)
    {
        return Token.Replace(text, m =>
        {
            if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
            {
                return m.Value;
            }
            try
            {
                return TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString(m.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return m.Value;
            }
        });
    }

    /// <summary>An ISO 8601 UTC time from the service, or null.</summary>
    public static DateTime? ParseUtc(string text)
    {
        if (string.IsNullOrEmpty(text)) { return null; }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.UtcDateTime
            : null;
    }

    /// <summary>"12 min ago", "5 hr ago", "3 days ago" (rounded).</summary>
    public static string Age(TimeSpan age)
    {
        if (age.TotalHours < 1) { return string.Format(CultureInfo.InvariantCulture, "{0} min ago", Math.Max(0, (int)Math.Round(age.TotalMinutes))); }
        if (age.TotalHours < 48) { return string.Format(CultureInfo.InvariantCulture, "{0} hr ago", (int)Math.Round(age.TotalHours)); }
        return string.Format(CultureInfo.InvariantCulture, "{0} days ago", (int)Math.Round(age.TotalDays));
    }

    /// <summary>A local time for display, in a fixed format.</summary>
    public static string InLocalTime(DateTime utc, string format = "yyyy-MM-dd HH:mm", TimeZoneInfo zone = null)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone ?? TimeZoneInfo.Local).ToString(format, CultureInfo.InvariantCulture);

    /// <summary>A UTC moment in a zone; false for a moment the calendar cannot hold there (year 1 or 9999).</summary>
    public static bool TryLocal(DateTime utc, TimeZoneInfo zone, out DateTime local)
    {
        try
        {
            local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone ?? TimeZoneInfo.Local);
            return true;
        }
        catch (ArgumentException)
        {
            local = default;
            return false;
        }
    }

    /// <summary>"+02:00" or "-05:30".</summary>
    public static string Offset(TimeSpan offset)
        => (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// What a local time means at a moment: "local time, UTC+02:00 (W. Europe Daylight Time)",
    /// with the zone's name and offset of that moment (summer or winter time).
    /// </summary>
    public static string ZoneLabel(TimeZoneInfo zone, DateTime utc)
    {
        zone ??= TimeZoneInfo.Local;
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var daylight = zone.SupportsDaylightSavingTime && zone.IsDaylightSavingTime(utc);
        return "local time, UTC" + Offset(zone.GetUtcOffset(utc)) + " (" + (daylight ? zone.DaylightName : zone.StandardName) + ")";
    }
}
