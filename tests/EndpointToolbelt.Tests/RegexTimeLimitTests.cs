using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The engineering rule that every <see cref="Regex"/> gets a time limit, held by a test instead of
/// only by review and Semgrep: a pattern without one can stall the service or the engine on odd
/// input. A match that runs out of time counts as no match (<see cref="TimedRegex"/>).
/// </summary>
public class RegexTimeLimitTests
{
    /// <summary>The start of a construction or a static call that takes the pattern and options itself.</summary>
    private static readonly Regex Start = new(
        @"(?<![\w.])(?:new\s+Regex\s*\(|Regex\.(?:IsMatch|Match|Matches|Replace|Split)\s*\(|Regex\??\s+\w+\s*=\s*new\s*\()",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    /// <summary>What counts as a time limit in the arguments: a TimeSpan or a named limit or timeout.</summary>
    private static readonly Regex TimeLimit = new(@"TimeSpan\.|\b\w*(?:Limit|Timeout)\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    [Fact]
    public void Every_regular_expression_below_src_has_a_time_limit()
    {
        var root = EngineHarness.RepositoryRoot();
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
        {
            var text = File.ReadAllText(file);
            foreach (Match start in Start.Matches(text))
            {
                var arguments = Arguments(text, start.Index + start.Length);
                if (!TimeLimit.IsMatch(arguments))
                {
                    var line = text.Take(start.Index).Count(c => c == '\n') + 1;
                    missing.Add(file.Substring(root.Length + 1) + ":" + line);
                }
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void The_guard_sees_a_pattern_without_a_time_limit_and_accepts_one_with_it()
    {
        Assert.Equal(new[] { false, false, false, true, true, true }, new[]
        {
            "var ok = Regex.IsMatch(value, @\"^\\d{1,6}$\");",
            "private static readonly Regex P = new(\"(a)\", RegexOptions.CultureInvariant);",
            "var p = new Regex(\"x(\", RegexOptions.None);",
            "var ok = Regex.IsMatch(value, \"^a$\", RegexOptions.None, TimeSpan.FromSeconds(1));",
            "private static readonly Regex P = new(\"(a)\", RegexOptions.None, TimedRegex.Limit);",
            "var m = rule.Regex.Match(record.Message);",
        }.Select(Passes));

        bool Passes(string code)
        {
            var start = Start.Match(code);
            return !start.Success || TimeLimit.IsMatch(Arguments(code, start.Index + start.Length));
        }
    }

    [Fact]
    public void A_match_that_runs_out_of_time_counts_as_no_match()
    {
        // Nested quantifiers on a string that almost matches: exponential backtracking.
        var slow = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(1));
        var input = new string('a', 40) + "!";
        Assert.False(TimedRegex.IsMatch(slow, input));
        Assert.False(TimedRegex.MatchOrEmpty(slow, input).Success);
    }

    [Fact]
    public void Matches_as_before_within_the_limit()
    {
        var digits = new Regex(@"^\d{1,6}$", RegexOptions.None, TimedRegex.Limit);
        Assert.True(TimedRegex.IsMatch(digits, "22631"));
        Assert.False(TimedRegex.IsMatch(digits, "22631x"));
        Assert.False(TimedRegex.IsMatch(digits, null));
        Assert.Equal("2.0", TimedRegex.MatchOrEmpty(new Regex(@"^(\d\.\d)", RegexOptions.None, TimedRegex.Limit), "2.0, 0, 1.38").Groups[1].Value);
        Assert.False(TimedRegex.MatchOrEmpty(digits, null).Success);
        Assert.Throws<ArgumentNullException>(() => TimedRegex.IsMatch(null, "1"));
    }

    /// <summary>The argument text up to the closing parenthesis, skipping string and character literals.</summary>
    private static string Arguments(string text, int from)
    {
        var depth = 1;
        var i = from;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                var verbatim = (i >= 1 && text[i - 1] == '@') || (i >= 2 && text[i - 2] == '@');
                i++;
                while (i < text.Length)
                {
                    if (verbatim && text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }
                    if (!verbatim && text[i] == '\\') { i += 2; continue; }
                    if (text[i] == '"') { break; }
                    i++;
                }
            }
            else if (c == '\'')
            {
                i++;
                while (i < text.Length && text[i] != '\'') { i += text[i] == '\\' ? 2 : 1; }
            }
            else if (c == '(') { depth++; }
            else if (c == ')' && --depth == 0) { return text.Substring(from, i - from); }
            i++;
        }
        return text.Substring(from);
    }
}
