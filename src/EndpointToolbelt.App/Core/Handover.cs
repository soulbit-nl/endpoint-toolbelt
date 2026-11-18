using System;
using System.Collections.Generic;
using System.Linq;

namespace EndpointToolbelt.App.Core;

/// <summary>The user's answer to "Does it work now?" after a repair on a problem path.</summary>
public enum WorksNowAnswer
{
    Yes,
    NotYet,
    NotTried,
}

/// <summary>
/// The handover to IT support: the way there, also when the organization set no support link;
/// "Does it work now?" after a repair on a problem path, kept apart from the run's own result;
/// "My problem is not listed", the way in for a problem that has no row.
/// </summary>
public static class Handover
{
    /// <summary>
    /// The organization's support link as a link the window may show: an https address without a user
    /// name part ("https://name@host" reads as a different host than the one it opens); null otherwise.
    /// The service accepts only https addresses; this checks again before it becomes a link.
    /// </summary>
    public static Uri SupportLink(string url)
        => Uri.TryCreate(url ?? "", UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) ? uri : null;

    /// <summary>The words that end a handover when no support link and no support text are set.</summary>
    public const string NormalWay = "send it the way you normally reach IT support";

    /// <summary>
    /// The one line under a result that names IT support: Copy details for IT, then the way to IT support
    /// this organization set (its link, its text) or, without either, the way the user normally takes.
    /// </summary>
    public static string ResultLine(bool hasLink, string supportText)
        => "Copy details for IT, then " + Way(hasLink, supportText) + ".";

    /// <summary>
    /// The end of Home's "Need a person?" when the organization set neither a link nor a text (the support
    /// text and Contact IT have a line of their own below it); empty otherwise.
    /// </summary>
    public static string HomeEnd(bool hasLink, bool hasText) => hasLink || hasText ? "" : "Then " + NormalWay + ".";

    /// <summary>The way to IT support: the organization's own words, else Contact IT on Home, else the user's normal way.</summary>
    private static string Way(bool hasLink, string supportText)
        => Text(supportText) is string text ? "reach IT support: " + text
            : hasLink ? "use Contact IT on Home" : NormalWay;

    /// <summary>The support text without a closing full stop (a sentence adds its own); null when there is none.</summary>
    private static string Text(string supportText)
        => string.IsNullOrWhiteSpace(supportText) ? null : supportText.Trim().TrimEnd('.');

    /// <summary>A text that sends the user to IT support ("Contact your IT support.", "ask IT support").</summary>
    public static bool NamesItSupport(string text) => !string.IsNullOrEmpty(text) && text.IndexOf("IT support", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// The result shown names IT support in its status line or in a card's title, detail or next
    /// step (a card's line for IT support itself does not count: it is for them, not a step to them).
    /// </summary>
    public static bool ResultNamesItSupport(string status, IEnumerable<ResultCard> cards)
        => NamesItSupport(status) || (cards ?? Enumerable.Empty<ResultCard>()).Any(c => c != null && (NamesItSupport(c.Title) || NamesItSupport(c.Detail) || NamesItSupport(c.NextStep)));

    // --- Does it work now? -----------------------------------------------------------------------------

    public const string Question = "Does it work now?";

    /// <summary>
    /// The question follows a repair that ran on a problem path (the path pages; the app path has
    /// its own Check again) and ended with a result that is not a problem: a tool that changes something,
    /// never a safe-mode start (its own question) and never a tool that only reads or opens.
    /// </summary>
    public static bool AsksWorksNow(Problem? path, ToolInfo tool, RunVerdict verdict)
        => path is Problem p && p != Problem.AppWontInstall
            && tool != null && tool.Changes.Length > 0 && tool.Office == null && !tool.OpensOnly
            && verdict is RunVerdict.Ok or RunVerdict.Check or RunVerdict.Requested or RunVerdict.Ended;

    /// <summary>The answer as the case file's Tried line says it, apart from the run's outcome.</summary>
    public static string AnswerWords(WorksNowAnswer answer) => "Works now: " + answer switch
    {
        WorksNowAnswer.Yes => "yes",
        WorksNowAnswer.NotYet => "not yet",
        _ => "not tried",
    };

    /// <summary>The line that replaces the question once it is answered.</summary>
    public static string AfterAnswer(WorksNowAnswer answer, bool hasLink, string supportText) => answer switch
    {
        WorksNowAnswer.Yes => "Good. Your answer is in Copy details for IT with this run.",
        WorksNowAnswer.NotYet => "Your answer is in Copy details for IT with this run. " + ResultLine(hasLink, supportText),
        _ => "Try it when you can. Your answer is in Copy details for IT with this run.",
    };

    // --- My problem is not listed ---------------------------------------------------------------------

    public const string NotListedTitle = "My problem is not listed";
    public const string NotListedSubtitle = "Tell IT support in your own words";

    /// <summary>The row's automation name, "&lt;title&gt;. &lt;subtitle&gt;" as every row of Choose a problem.</summary>
    public const string NotListedName = NotListedTitle + ". " + NotListedSubtitle;

    /// <summary>The page's line under its title: the first step is a person, not a tool.</summary>
    public const string NotListedIntro = "Contact your IT support and tell them in your own words what goes wrong.";

    /// <summary>
    /// The way to IT support on "My problem is not listed", beside Contact IT when there is a link: the
    /// organization's own words; without a link or a text, the user's own way; empty with a link only.
    /// </summary>
    public static string NotListedWay(bool hasLink, string supportText)
        => Text(supportText) is string text ? "Reach them: " + text + "."
            : hasLink ? "" : "Reach them the way you normally do.";

    /// <summary>The line above the three steps: each runs only when IT support asks for it.</summary>
    public const string NotListedAsk = "They may ask you to:";

    public const string NotListedCopyText = "Copy this device's facts and health checks, to paste into your message.";
    public const string NotListedPackageText = "Collect logs into a zip file in your Downloads folder, when they ask for logs.";
    public const string NotListedItText = "Run a tool in For IT support, only the one they name.";

    /// <summary>The ticket's outcome line: no verdict, the user's own words follow.</summary>
    public const string NotListedOutcome = "Not one of the listed problems; the user describes it in their own words. Health checks: ";
}
