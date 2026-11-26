using System;
using System.Globalization;
using System.Linq;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// Retry work apps and settings as a tool of For IT support (group Apps): the same action as the
/// app path's Retry, pressed at once. It cannot be switched off: Home, the app path and the
/// health rows offer it as their step. Its detail page says in one sentence how the work apps
/// stood at the last diagnosis in this window, with a link to the app path.
/// </summary>
public static class RetryTool
{
    /// <summary>The x:Name of the tool's button on its detail page.</summary>
    public const string Button = "RetryToolButton";

    /// <summary>Why it cannot run without the app installer: the app path offers no Retry then either.</summary>
    public const string ImeMissingText = "Not available: the app installer (IME) is not installed on this PC yet";

    /// <summary>The link after the sentence: it opens the app path.</summary>
    public const string ShowAppsText = "Show the apps";

    /// <summary>The names an attention sentence lists before "and N more".</summary>
    public const int NamesListed = 2;

    /// <summary>
    /// One sentence on the work apps of the diagnosis: "2 of 14 work apps need attention (Contoso
    /// Agent, Contoso VPN), read today 09:12." It counts as the app path does, so Show the apps opens
    /// the same numbers: every app of the diagnosis, and those of its short list (what needs attention,
    /// what is on its way, an app a step was taken for). It never names more than two apps, each name once.
    /// </summary>
    public static string Summary(AppDiagnosis diagnosis, TimeZoneInfo zone, DateTime nowUtc)
    {
        if (diagnosis == null || diagnosis.Run == DiagnosisRunState.NotRun) { return "The work apps were not read in this window yet."; }
        if (diagnosis.Run == DiagnosisRunState.Running) { return "Reading the work apps..."; }
        if (diagnosis.Run != DiagnosisRunState.Finished) { return "The work apps could not be read."; }
        var read = diagnosis.CapturedUtc is DateTime captured ? ", read " + ToolRuns.ShortWhen(captured, zone, nowUtc) : "";
        var all = diagnosis.Apps.Count;
        var attention = diagnosis.Apps.Where(AppDecisions.InShortList).Select(a => a.App.Label).ToList();
        var named = attention.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (all == 0) { return "No work apps were found" + read + "."; }
        if (attention.Count == 0)
        {
            return (all == 1 ? "The one work app needs no attention" : "None of the " + Words.Count(all, "work app") + " needs attention") + read + ".";
        }
        var names = string.Join(", ", named.Take(NamesListed).Select(ShortName))
            + (named.Count > NamesListed ? " and " + (named.Count - NamesListed).ToString(CultureInfo.InvariantCulture) + " more" : "");
        return attention.Count.ToString(CultureInfo.InvariantCulture) + " of " + Words.Count(all, "work app") + " "
            + (attention.Count == 1 ? "needs" : "need") + " attention (" + names + ")" + read + ".";
    }

    /// <summary>The longest name the sentence says whole; a longer one is cut with "…" (Show the apps lists it whole).</summary>
    public const int NameChars = 30;

    /// <summary>A name as the sentence says it: whole up to <see cref="NameChars"/> characters, else cut at a word with "…", so the page fits.</summary>
    public static string ShortName(string name)
    {
        var text = (name ?? "").Trim();
        if (text.Length <= NameChars) { return text; }
        var cut = text.LastIndexOf(' ', NameChars - 1);
        var length = cut > NameChars / 2 ? cut : NameChars - 1;
        // Never half a character: a cut inside a surrogate pair (an emoji) steps back one.
        if (char.IsHighSurrogate(text[length - 1])) { length--; }
        return text.Substring(0, length).TrimEnd(' ', ',', '/', '-') + "\u2026";
    }
}
