using System;
using System.Text.RegularExpressions;

namespace EndpointToolbelt.Platform;

/// <summary>
/// Matching with a time limit: every <see cref="Regex"/> in the product has one.
/// A match that runs out of time counts as no match, so a check that relies on it fails closed.
/// </summary>
public static class TimedRegex
{
    /// <summary>The time limit for the short, anchored patterns on short input that the product uses for checks.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);

    /// <summary>Whether <paramref name="input"/> matches; false when the match runs out of time.</summary>
    public static bool IsMatch(Regex pattern, string input)
    {
        if (pattern == null) { throw new ArgumentNullException(nameof(pattern)); }
        if (input == null) { return false; }
        try { return pattern.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>The first match; an unsuccessful match when there is none or the match runs out of time.</summary>
    public static Match MatchOrEmpty(Regex pattern, string input)
    {
        if (pattern == null) { throw new ArgumentNullException(nameof(pattern)); }
        if (input == null) { return System.Text.RegularExpressions.Match.Empty; }
        try { return pattern.Match(input); }
        catch (RegexMatchTimeoutException) { return System.Text.RegularExpressions.Match.Empty; }
    }
}
