using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;

namespace EndpointToolbelt.App.Core;

/// <summary>A step the user took from an app finding; the post-check judges what followed.</summary>
public enum RemedyKind
{
    RetryAppsAndPolicies,
    ConnectivityTest,

    /// <summary>The finding said to restart the device (the app restarts nothing).</summary>
    RestartAdvice,
    CompanyPortal,

    /// <summary>The finding was handed to IT with Copy details for IT.</summary>
    CopyForIt,
}

/// <summary>
/// What the post-check says about an app after a remedy. Kept apart from the state of the run
/// that read it, as the app diagnosis keeps its run and the app's observed state apart.
/// </summary>
public enum PostCheckState
{
    /// <summary>No new measurement of the same app yet: the step's result is still to come.</summary>
    AwaitingEvidence,

    /// <summary>A new, traceable measurement of the same app says it is installed and detected.</summary>
    Detected,

    /// <summary>A new measurement of the same app says it failed.</summary>
    Failed,

    /// <summary>The new diagnosis cannot be tied to the app of the remedy.</summary>
    Unknown,
}

/// <summary>Why the post-check came to its state.</summary>
public enum PostCheckReason
{
    /// <summary>No diagnosis after the remedy has been compared yet.</summary>
    NotCheckedYet,

    /// <summary>The same record: its observation time is not later than the remedy or the previous observation.</summary>
    Unchanged,

    /// <summary>The record has no observation time, so it cannot count as newer.</summary>
    TimeUnknown,
    NewerDetected,

    /// <summary>
    /// The record's time did not move, but it now says installed and detected: the IME rewrote its
    /// assignment or status report without a new time (seen on a shared virtual desktop).
    /// </summary>
    NowDetected,
    NewerFailure,

    /// <summary>A newer record that is still on its way (in progress, waiting for a restart or the user).</summary>
    NewerInProgress,

    /// <summary>A newer record whose state says nothing certain (unknown, nothing to do, an uninstall).</summary>
    NewerUnclear,

    /// <summary>The app is no longer in the list.</summary>
    Missing,

    /// <summary>The app is in the list, but only for another scope (device instead of user, or the other way round).</summary>
    OtherScope,

    /// <summary>The revision changed: it is a different deployment of the app.</summary>
    RevisionChanged,

    /// <summary>More than one record matches the app ID and scope.</summary>
    TwoCandidates,
}

/// <summary>
/// One remedy: which diagnosis and app it came from (diagnosis ID, app ID, scope, revision and
/// the observation the finding was based on), what was done and when (UTC), the remedy's run ID when
/// it was a run, and the newest post-check result. Kept per user in the user's own data folder.
/// </summary>
[DataContract]
public sealed class RemedyRecord
{
    /// <summary>The SID of the user whose record this is; another user's record is never read.</summary>
    [DataMember(Name = "user", Order = 1)] public string User { get; set; } = "";

    /// <summary>The diagnosis the finding came from (its run ID).</summary>
    [DataMember(Name = "diagnosis", Order = 2)] public string DiagnosisId { get; set; } = "";
    [DataMember(Name = "app", Order = 3)] public string AppId { get; set; } = "";
    [DataMember(Name = "name", Order = 4)] public string Name { get; set; } = "";
    [DataMember(Name = "scope", Order = 5)] public string Scope { get; set; } = "";
    [DataMember(Name = "revision", Order = 6)] public int? Revision { get; set; }

    /// <summary>When the IME last updated the record the finding was based on (UTC); empty when unknown.</summary>
    [DataMember(Name = "observed", Order = 7)] public string ObservedText { get; set; } = "";
    [DataMember(Name = "code", Order = 8)] public string Code { get; set; } = "";
    [DataMember(Name = "remedy", Order = 9)] public string RemedyText { get; set; } = "";

    /// <summary>When the remedy was taken (UTC).</summary>
    [DataMember(Name = "at", Order = 10)] public string AtText { get; set; } = "";

    /// <summary>The remedy's run ID when it was a run (a service run or a check in the app); empty otherwise.</summary>
    [DataMember(Name = "run", Order = 11)] public string RunId { get; set; } = "";
    [DataMember(Name = "state", Order = 12)] public string StateText { get; set; } = nameof(PostCheckState.AwaitingEvidence);
    [DataMember(Name = "reason", Order = 13)] public string ReasonText { get; set; } = nameof(PostCheckReason.NotCheckedYet);

    /// <summary>The diagnosis that made the newest post-check; empty before the first.</summary>
    [DataMember(Name = "check", Order = 14)] public string CheckDiagnosisId { get; set; } = "";
    [DataMember(Name = "checkobserved", Order = 15)] public string CheckObservedText { get; set; } = "";
    [DataMember(Name = "checkcode", Order = 16)] public string CheckCode { get; set; } = "";
    [DataMember(Name = "checkedat", Order = 17)] public string CheckedAtText { get; set; } = "";

    public RemedyKind? Kind => Enum.TryParse<RemedyKind>(RemedyText, false, out var kind) && Enum.IsDefined(typeof(RemedyKind), kind) ? kind : null;

    public DateTime? AtUtc => TimeText.ParseUtc(AtText);

    public DateTime? ObservedUtc => TimeText.ParseUtc(ObservedText);

    public DateTime? CheckObservedUtc => TimeText.ParseUtc(CheckObservedText);

    public PostCheckState State
    {
        get => Enum.TryParse<PostCheckState>(StateText, false, out var state) && Enum.IsDefined(typeof(PostCheckState), state) ? state : PostCheckState.Unknown;
        set => StateText = value.ToString();
    }

    public PostCheckReason Reason
    {
        get => Enum.TryParse<PostCheckReason>(ReasonText, false, out var reason) && Enum.IsDefined(typeof(PostCheckReason), reason) ? reason : PostCheckReason.NotCheckedYet;
        set => ReasonText = value.ToString();
    }

    /// <summary>The name to show: the app's name, or "App" and the start of its ID.</summary>
    public string Label => !string.IsNullOrWhiteSpace(Name) ? Name.Trim() : "App " + (AppId.Length >= 8 ? AppId.Substring(0, 8) : AppId);

    internal static string Utc(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}

/// <summary>
/// The post-check: ties a remedy and a later observation to the diagnosis it came from, and
/// says "App detected" only on a new, traceable, positive measurement of the same app. Pure, so
/// every comparison rule is tested without a window.
/// </summary>
public static class PostCheck
{
    /// <summary>A remedy older than this is history: not shown, not compared, not used for the Retry status.</summary>
    public static readonly TimeSpan HistoricalAfter = TimeSpan.FromDays(7);

    /// <summary>A remedy dated later than now by more than this (the clock went back) is not trusted.</summary>
    private static readonly TimeSpan FutureSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The cases Retry work apps and settings retries: apps whose last attempt failed and that the IME
    /// tries again (not an app offered in Company Portal, whose next attempt is the user's, and not
    /// one whose requirements keep it off the device).
    /// </summary>
    private static readonly AppCase[] RetriedCases = { AppCase.InstallFailed, AppCase.RemovalFailed, AppCase.RetryPlanned, AppCase.DownloadError, AppCase.InstalledNotDetected, AppCase.DependencyBlocked };

    /// <summary>The remedy taken from a finding, or null for a finding without an app or a diagnosis.</summary>
    public static RemedyRecord Remedy(AppFinding finding, RemedyKind kind, DateTime atUtc, string user, string runId = "")
    {
        if (finding?.App == null || string.IsNullOrEmpty(finding.App.AppId) || string.IsNullOrEmpty(finding.DiagnosisId)) { return null; }
        var app = finding.App;
        return new RemedyRecord
        {
            User = user ?? "",
            DiagnosisId = finding.DiagnosisId,
            AppId = app.AppId,
            Name = app.Name ?? "",
            Scope = app.Scope ?? "",
            Revision = Rev(app.Revision),
            ObservedText = app.ObservedUtc is DateTime observed ? RemedyRecord.Utc(observed) : "",
            Code = app.Code ?? "",
            RemedyText = kind.ToString(),
            AtText = RemedyRecord.Utc(atUtc),
            RunId = runId ?? "",
        };
    }

    /// <summary>The same step from the same finding (a second copy, the same advice shown again): kept once.</summary>
    public static bool SameRemedy(RemedyRecord a, RemedyRecord b)
        => a.DiagnosisId == b.DiagnosisId && SameApp(a.AppId, b.AppId) && a.Scope == b.Scope && a.RemedyText == b.RemedyText && (a.RunId ?? "") == (b.RunId ?? "");

    /// <summary>Not historical: taken in the last 7 days, and not in the future.</summary>
    public static bool IsActive(RemedyRecord remedy, DateTime nowUtc)
        => remedy?.AtUtc is DateTime at && remedy.Kind.HasValue && at <= nowUtc + FutureSkew && nowUtc - at <= HistoricalAfter;

    /// <summary>The newest active remedy for each app and scope.</summary>
    public static List<RemedyRecord> Newest(IEnumerable<RemedyRecord> records, DateTime nowUtc)
        => (records ?? Enumerable.Empty<RemedyRecord>())
            .Where(r => IsActive(r, nowUtc))
            .GroupBy(r => r.AppId.ToUpperInvariant() + "|" + r.Scope, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(r => r.AtUtc).First())
            .ToList();

    /// <summary>The apps a Retry work apps and settings retries, from a finished diagnosis (the base findings, not a post-check view).</summary>
    public static List<AppFinding> Retried(AppDiagnosis diagnosis)
        => diagnosis == null || diagnosis.Run != DiagnosisRunState.Finished
            ? new List<AppFinding>()
            // An MSI line-of-business app is not retried by Retry work apps and settings (the MDM stack retries it), nor is an MSIX one.
            : diagnosis.Apps.Where(a => a.Case != AppCase.Unmatched && a.Observation == AppObservation.Failed && RetriedCases.Contains(a.Case) && !string.IsNullOrEmpty(a.App.AppId) && !a.App.IsMdmLob).ToList();

    /// <summary>
    /// Compares the remedy with a newer diagnosis. Only a finished diagnosis other than the one the
    /// remedy came from counts. Then, in this order: an app ID that is not in the list, or only for
    /// another scope, or twice for the same scope, or with another revision is "unknown"; a record
    /// whose observation time is unknown, or not later than both the remedy and the observation the
    /// finding was based on, is unchanged and stays "awaiting evidence"; a newer record is
    /// "detected" only when it says installed and detected, "failed" when its attempt failed,
    /// "awaiting evidence" while it is still on its way, and "unknown" otherwise. The absence of an
    /// error is never a success.
    /// </summary>
    public static (PostCheckState State, PostCheckReason Reason, AppFinding Match) Compare(RemedyRecord remedy, AppDiagnosis newer)
    {
        if (remedy == null || newer == null || newer.Run != DiagnosisRunState.Finished || string.IsNullOrEmpty(newer.DiagnosisId) || newer.DiagnosisId == remedy.DiagnosisId)
        {
            return (PostCheckState.AwaitingEvidence, PostCheckReason.NotCheckedYet, null);
        }
        var sameId = newer.Apps.Where(a => a.Case != AppCase.Unmatched && SameApp(a.App.AppId, remedy.AppId)).ToList();
        if (sameId.Count == 0) { return (PostCheckState.Unknown, PostCheckReason.Missing, null); }
        var same = sameId.Where(a => a.App.Scope == remedy.Scope || a.ForBothScopes).ToList();
        if (same.Count == 0) { return (PostCheckState.Unknown, PostCheckReason.OtherScope, null); }
        if (same.Count > 1) { return (PostCheckState.Unknown, PostCheckReason.TwoCandidates, null); }
        var match = same[0];
        if (Rev(match.App.Revision) != Rev(remedy.Revision)) { return (PostCheckState.Unknown, PostCheckReason.RevisionChanged, match); }
        var detected = match.Case == AppCase.Detected && match.Observation == AppObservation.Detected && match.App.Intent != "uninstall";
        if (match.App.ObservedUtc is not DateTime observed)
        {
            return detected ? (PostCheckState.Detected, PostCheckReason.NowDetected, match) : (PostCheckState.AwaitingEvidence, PostCheckReason.TimeUnknown, match);
        }
        if (remedy.AtUtc is not DateTime at || observed <= at || (remedy.ObservedUtc is DateTime before && observed <= before))
        {
            // The same time, but a finding the remedy could only follow when it was not detected now
            // says installed and detected: the IME changed the record's content, not its time.
            return detected ? (PostCheckState.Detected, PostCheckReason.NowDetected, match) : (PostCheckState.AwaitingEvidence, PostCheckReason.Unchanged, match);
        }
        if (detected)
        {
            return (PostCheckState.Detected, PostCheckReason.NewerDetected, match);
        }
        return match.Observation switch
        {
            AppObservation.Failed => (PostCheckState.Failed, PostCheckReason.NewerFailure, match),
            AppObservation.AwaitingEvidence => (PostCheckState.AwaitingEvidence, PostCheckReason.NewerInProgress, match),
            _ => (PostCheckState.Unknown, PostCheckReason.NewerUnclear, match),
        };
    }

    /// <summary>
    /// Compares every active remedy with a newer finished diagnosis and keeps the result in the
    /// record (the diagnosis that checked it, the observation and code it found). Returns the
    /// remedies this diagnosis checked; a diagnosis that did not finish checks none.
    /// </summary>
    public static List<RemedyRecord> Check(IEnumerable<RemedyRecord> records, AppDiagnosis diagnosis, DateTime nowUtc)
    {
        var checkedNow = new List<RemedyRecord>();
        if (diagnosis == null || diagnosis.Run != DiagnosisRunState.Finished || string.IsNullOrEmpty(diagnosis.DiagnosisId)) { return checkedNow; }
        foreach (var remedy in (records ?? Enumerable.Empty<RemedyRecord>()).Where(r => IsActive(r, nowUtc)))
        {
            // A diagnosis that read the apps before the remedy was taken says nothing about it.
            if (remedy.DiagnosisId == diagnosis.DiagnosisId || (diagnosis.CapturedUtc is DateTime captured && captured < remedy.AtUtc)) { continue; }
            var (state, reason, match) = Compare(remedy, diagnosis);
            remedy.State = state;
            remedy.Reason = reason;
            remedy.CheckDiagnosisId = diagnosis.DiagnosisId;
            remedy.CheckObservedText = match?.App.ObservedUtc is DateTime observed ? RemedyRecord.Utc(observed) : "";
            remedy.CheckCode = match?.App.Code ?? "";
            remedy.CheckedAtText = RemedyRecord.Utc(diagnosis.CapturedUtc ?? nowUtc);
            checkedNow.Add(remedy);
        }
        return checkedNow;
    }

    /// <summary>
    /// The diagnosis as the window shows it after remedies: each app with an active remedy is
    /// shown with its post-check state (awaiting evidence with "Check again", "App detected" with its
    /// evidence, failed with the new code, or unknown with the handover), and an app of a remedy that
    /// a finished diagnosis cannot find is added as "unknown". The diagnosis itself is not changed.
    /// </summary>
    /// <summary>
    /// The steps a Full IME reset ends: taken at or before it. The reset throws away the IME's app records, so
    /// a step before it can no longer be checked; it would read "the result is not known" for every such app.
    /// </summary>
    public static List<RemedyRecord> BeforeReset(IEnumerable<RemedyRecord> records, DateTime? resetUtc)
        => resetUtc is DateTime reset ? (records ?? Enumerable.Empty<RemedyRecord>()).Where(r => r != null && (r.AtUtc is not DateTime at || at <= reset)).ToList() : new List<RemedyRecord>();

    public static AppDiagnosis Overlay(AppDiagnosis diagnosis, IEnumerable<RemedyRecord> records, DateTime nowUtc, TimeZoneInfo zone = null)
    {
        if (diagnosis == null) { return null; }
        var view = new AppDiagnosis
        {
            DiagnosisId = diagnosis.DiagnosisId,
            Run = diagnosis.Run,
            CapturedUtc = diagnosis.CapturedUtc,
            Present = diagnosis.Present,
            Records = diagnosis.Records,
            Listed = diagnosis.Listed,
            OtherScopes = diagnosis.OtherScopes,
            UserScope = diagnosis.UserScope,
            Problem = diagnosis.Problem,
        };
        var remedies = Newest(records, nowUtc);
        var used = new HashSet<RemedyRecord>();
        foreach (var app in diagnosis.Apps)
        {
            var remedy = remedies.FirstOrDefault(r => SameApp(r.AppId, app.App.AppId) && (r.Scope == app.App.Scope || app.ForBothScopes));
            if (remedy == null || string.IsNullOrEmpty(app.App.AppId))
            {
                view.Apps.Add(app);
                continue;
            }
            used.Add(remedy);
            var (state, reason, _) = Compare(remedy, diagnosis);
            view.Apps.Add(Shown(app, remedy, state, reason, zone));
        }
        if (diagnosis.Run == DiagnosisRunState.Finished)
        {
            // The app of a remedy that this diagnosis does not have (for this scope): unknown, with the handover.
            foreach (var remedy in remedies.Where(r => !used.Contains(r)))
            {
                var (state, reason, _) = Compare(remedy, diagnosis);
                if (reason is not (PostCheckReason.Missing or PostCheckReason.OtherScope)) { continue; }
                view.Apps.Add(Unmatched(remedy, diagnosis.DiagnosisId, state, reason, zone));
            }
        }
        return view;
    }

    /// <summary>A copy of the finding with its post-check state.</summary>
    private static AppFinding Shown(AppFinding app, RemedyRecord remedy, PostCheckState state, PostCheckReason reason, TimeZoneInfo zone)
    {
        var f = new AppFinding
        {
            App = app.App,
            DiagnosisId = app.DiagnosisId,
            Observation = app.Observation,
            Case = app.Case,
            Next = app.Next,
            Severity = app.Severity,
            Title = app.Title,
            Text = app.Text,
            NextText = app.NextText,
            Caveat = app.Caveat,
            Support = app.Support,
            Uncertainty = app.Uncertainty,
            Evidence = app.Evidence,
            Record = app.Record,
            SameNameAsAnother = app.SameNameAsAnother,
            Remedy = remedy,
            AfterRemedy = state,
            AfterRemedyReason = reason,
        };
        var label = app.App.Label;
        var after = "After " + RemedyName(remedy) + " (" + Taken(remedy, zone) + ")";
        switch (state)
        {
            case PostCheckState.Detected:
                f.Observation = AppObservation.Detected;
                f.Severity = FindingSeverity.Ok;
                f.Next = AppNextStep.None;
                f.Title = "App detected: " + label;
                f.Text = reason == PostCheckReason.NowDetected
                    ? after + ", a new reading shows it installed and detected: the Intune Management Extension now reports it installed, though its record still carries the time "
                        + (app.App.ObservedUtc is DateTime kept ? AppDecisions.When(kept, zone) : "it had") + " (diagnosis " + app.DiagnosisId + ")."
                    : after + ", a new reading shows it installed and detected: the Intune Management Extension recorded it at "
                        + (app.App.ObservedUtc is DateTime seen ? AppDecisions.When(seen, zone) : "an unknown time") + " (diagnosis " + app.DiagnosisId + ").";
                f.NextText = "";
                f.Caveat = "";
                f.Uncertainty = "";
                break;
            case PostCheckState.Failed:
                // The finding's own decision for the new failure, with the remedy it followed: said plainly, the
                // code, the times and the diagnosis in the technical details.
                var again = "It failed again" + (app.App.ObservedUtc is DateTime failedAt ? " " + ToolRuns.ShortWhen(failedAt, zone, DateTime.UtcNow) : "")
                    + ", after " + (remedy.Kind == RemedyKind.CopyForIt ? "it was sent to IT support" : RemedyName(remedy))
                    + (remedy.AtUtc is DateTime takenAt ? " " + ToolRuns.ShortWhen(takenAt, zone, DateTime.UtcNow) : "") + ".";
                f.Text = again + " " + app.Text;
                f.Evidence = (string.IsNullOrEmpty(f.Evidence) ? "" : f.Evidence.TrimEnd() + " ") + (after + ", a newer attempt failed" + (string.IsNullOrEmpty(app.App.Code) ? "" : " with " + app.App.Code)
                    + " (recorded " + (app.App.ObservedUtc is DateTime recordedAt ? AppDecisions.When(recordedAt, zone) : "at an unknown time") + ", diagnosis " + app.DiagnosisId + ").");
                // Already with IT support: a retry promises nothing new; they need the newer failure.
                if (remedy.Kind == RemedyKind.CopyForIt)
                {
                    f.Next = AppNextStep.CopyForIt;
                    f.Title = label + ": still failing, IT support has it";
                    f.NextText = "Retrying gives the same result. Copy details for IT again, so IT support sees that it still fails.";
                    f.Caveat = "";
                    break;
                }
                // After Retry the same failure again: another retry will not help; the code is the lead for IT support.
                if (remedy.Kind == RemedyKind.RetryAppsAndPolicies && !string.IsNullOrEmpty(remedy.Code)
                    && string.Equals(app.App.Code, remedy.Code, StringComparison.OrdinalIgnoreCase))
                {
                    f.Next = AppNextStep.CopyForIt;
                    f.Title = label + ": failed again with the same code";
                    f.NextText = "It failed again with the same code after Retry work apps and settings, so another retry will not help. Copy details for IT and send it to your IT support.";
                    f.Caveat = "";
                    var same = "The same code (" + app.App.Code + ") before and after a retry: the installer or its detection rule needs a look, not another attempt.";
                    f.Support = string.IsNullOrEmpty(f.Support) ? same : f.Support + " " + same;
                }
                break;
            case PostCheckState.AwaitingEvidence:
                f.Observation = AppObservation.AwaitingEvidence;
                f.Severity = FindingSeverity.Info;
                f.Next = AppNextStep.CheckAgain;
                f.Title = label + ": awaiting evidence";
                // Nothing new yet is said plainly; the record's own time stays in View details
                // (Line) and Copy details for IT (TicketLine).
                f.Text = reason is PostCheckReason.Unchanged or PostCheckReason.NotCheckedYet
                    ? "Nothing new from this app since " + Since(remedy) + " (" + Taken(remedy, zone) + ")."
                    : after + ", " + Why(reason, app, zone);
                f.NextText = AfterText(remedy.Kind);
                f.Caveat = "";
                break;
            default:
                Unknown(f, label, after, reason);
                break;
        }
        return f;
    }

    /// <summary>The app of a remedy that a finished diagnosis does not have for the same scope.</summary>
    private static AppFinding Unmatched(RemedyRecord remedy, string diagnosisId, PostCheckState state, PostCheckReason reason, TimeZoneInfo zone)
    {
        var f = new AppFinding
        {
            App = new AppEvidence { DiagnosisId = diagnosisId, AppId = remedy.AppId, Name = remedy.Name, Scope = remedy.Scope, Revision = remedy.Revision ?? -1 },
            DiagnosisId = diagnosisId,
            Case = AppCase.Unmatched,
            Remedy = remedy,
            AfterRemedy = state,
            AfterRemedyReason = reason,
        };
        Unknown(f, remedy.Label, "After " + RemedyName(remedy) + " (" + Taken(remedy, zone) + ")", reason);
        return f;
    }

    private static void Unknown(AppFinding f, string label, string after, PostCheckReason reason)
    {
        f.Observation = AppObservation.Unknown;
        f.Severity = FindingSeverity.Warning;
        f.Next = AppNextStep.CopyForIt;
        f.Title = label + ": the result is not known";
        f.Text = after + ", " + reason switch
        {
            PostCheckReason.Missing => "this app is no longer in the list of this device, so nothing here shows whether the step worked.",
            PostCheckReason.OtherScope => "this app is only listed for another assignment (" + OtherScope(f.App.Scope) + "), so it is not the same record.",
            PostCheckReason.RevisionChanged => "the app has a new revision: it is a different deployment, so its record says nothing about the step taken.",
            PostCheckReason.TwoCandidates => "more than one record matches this app, so neither is taken as its result.",
            _ => "the newest record of this app does not say whether it is installed.",
        };
        f.NextText = "Copy details for IT and send it to your IT support.";
        f.Caveat = "";
        f.Uncertainty = "A missing error is not a success: only a new record that says installed and detected counts.";
    }

    private static string OtherScope(string scope) => scope == "user" ? "for this device, not for you" : "for you, not for this device";

    private static string Why(PostCheckReason reason, AppFinding app, TimeZoneInfo zone) => reason switch
    {
        PostCheckReason.NotCheckedYet => "this app has not been checked again yet.",
        PostCheckReason.TimeUnknown => "the newest record of this app has no time, so it cannot count as a new measurement.",
        PostCheckReason.NewerInProgress => "the Intune Management Extension is working on it (" + (string.IsNullOrEmpty(app.App.State) ? "no state recorded" : app.App.State) + ").",
        _ => "the newest record of this app is from " + (app.App.ObservedUtc is DateTime seen ? AppDecisions.When(seen, zone) : "an unknown time") + ", not later than the step, so it says nothing about the result yet.",
    };

    /// <summary>What to do while the result of a remedy is awaited.</summary>
    public static string AfterText(RemedyKind? kind) => kind switch
    {
        RemedyKind.RetryAppsAndPolicies => "The Intune Management Extension retries in the background, which can take a while. Choose Check this app again in a few minutes; nothing is changed by the check.",
        RemedyKind.ConnectivityTest => "When the connection works, Retry work apps and settings below starts a new attempt. Then choose Check this app again.",
        RemedyKind.RestartAdvice => "Restart the device if you have not done so yet, then choose Check this app again.",
        RemedyKind.CompanyPortal => "Choose Install there, then Check this app again.",
        RemedyKind.CopyForIt => "Your IT support has the details. Choose Check this app again once they say it is fixed.",
        _ => "Choose Check this app again later.",
    };

    /// <summary>The remedy as the moment since which nothing is new ("you opened Company Portal").</summary>
    public static string Since(RemedyRecord remedy) => remedy?.Kind switch
    {
        RemedyKind.RetryAppsAndPolicies => "Retry work apps and settings",
        RemedyKind.ConnectivityTest => "Check Microsoft connections",
        RemedyKind.RestartAdvice => "the advice to restart",
        RemedyKind.CompanyPortal => "you opened Company Portal",
        RemedyKind.CopyForIt => "you copied it for IT",
        _ => "the step taken",
    };

    /// <summary>The remedy in words.</summary>
    public static string RemedyName(RemedyRecord remedy) => remedy?.Kind switch
    {
        RemedyKind.RetryAppsAndPolicies => "Retry work apps and settings",
        RemedyKind.ConnectivityTest => "Check Microsoft connections",
        RemedyKind.RestartAdvice => "the advice to restart",
        RemedyKind.CompanyPortal => "opening Company Portal",
        RemedyKind.CopyForIt => "the handover to IT",
        _ => "the step taken",
    };

    private static string Taken(RemedyRecord remedy, TimeZoneInfo zone) => remedy.AtUtc is DateTime at ? AppDecisions.When(at, zone) : "time unknown";

    /// <summary>The post-check state in words ("App detected" for a positive one).</summary>
    public static string Word(PostCheckState state) => state switch
    {
        PostCheckState.AwaitingEvidence => "Awaiting evidence",
        PostCheckState.Detected => "App detected",
        PostCheckState.Failed => "Failed",
        _ => "Unknown",
    };

    /// <summary>The post-check line of the app's details: the state, the remedy and the evidence.</summary>
    public static string Line(AppFinding finding, TimeZoneInfo zone = null)
    {
        if (finding?.Remedy == null || finding.AfterRemedy is not PostCheckState state) { return ""; }
        var remedy = finding.Remedy;
        var line = "Post-check: " + Word(state) + " · " + Capital(RemedyName(remedy)) + " at " + Taken(remedy, zone) + " · from diagnosis " + remedy.DiagnosisId;
        if (finding.AfterRemedyReason == PostCheckReason.NotCheckedYet) { return line + " · not checked again yet"; }
        var seen = finding.App.ObservedUtc is DateTime observed ? ", recorded " + AppDecisions.When(observed, zone) : "";
        return line + " · checked in diagnosis " + finding.DiagnosisId + seen;
    }

    /// <summary>
    /// The post-check line of Copy details for IT: the original diagnosis ID, the remedy (with its run
    /// ID when it was a run) and the post-check result with the diagnosis that made it.
    /// </summary>
    public static string TicketLine(AppFinding finding, TimeZoneInfo zone = null)
    {
        if (finding?.Remedy == null || finding.AfterRemedy is not PostCheckState state) { return null; }
        var remedy = finding.Remedy;
        var run = string.IsNullOrEmpty(remedy.RunId) ? "" : " (run " + remedy.RunId + ")";
        var result = finding.AfterRemedyReason == PostCheckReason.NotCheckedYet
            ? Word(state) + ", not checked again yet"
            : Word(state) + (state == PostCheckState.Failed && !string.IsNullOrEmpty(finding.App.Code) ? " " + finding.App.Code : "")
                + " in diagnosis " + finding.DiagnosisId
                + (finding.App.ObservedUtc is DateTime observed ? ", recorded " + AppDecisions.When(observed, zone) : ", record time unknown")
                + (state == PostCheckState.Unknown ? " (" + Reason(finding.AfterRemedyReason) + ")" : "");
        return "Post-check" + RunTicket.Separator + "Original diagnosis " + remedy.DiagnosisId + RunTicket.Separator + "Remedy " + Capital(RemedyName(remedy)) + run + " at " + Taken(remedy, zone)
            + RunTicket.Separator + "Result " + result;
    }

    private static string Reason(PostCheckReason reason) => reason switch
    {
        PostCheckReason.Missing => "app not in the list",
        PostCheckReason.OtherScope => "only another scope listed",
        PostCheckReason.RevisionChanged => "revision changed",
        PostCheckReason.TwoCandidates => "two matching records",
        _ => "state not recognised",
    };

    private static string Capital(string text) => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

    /// <summary>The remedies of the newest active Retry work apps and settings run (one record per retried app).</summary>
    public static List<RemedyRecord> RetryGroup(IEnumerable<RemedyRecord> records, DateTime nowUtc, string runId = null)
    {
        var retries = (records ?? Enumerable.Empty<RemedyRecord>()).Where(r => IsActive(r, nowUtc) && r.Kind == RemedyKind.RetryAppsAndPolicies).ToList();
        if (retries.Count == 0) { return retries; }
        var newest = runId ?? retries.OrderByDescending(r => r.AtUtc).First().RunId ?? "";
        var group = retries.Where(r => (r.RunId ?? "") == newest).ToList();
        if (newest.Length == 0)
        {
            // A run without an ID: its records share their time.
            var at = group.OrderByDescending(r => r.AtUtc).FirstOrDefault()?.AtText;
            group = group.Where(r => r.AtText == at).ToList();
        }
        return group;
    }

    /// <summary>
    /// What is known about the retried apps: all detected only when every retried app had
    /// a new, positive measurement; some failing when one failed again; unknown otherwise (no
    /// retried app known, or one still awaiting evidence or not traceable).
    /// </summary>
    public static AppsAfterRetry AfterRetry(IReadOnlyCollection<RemedyRecord> group)
    {
        if (group == null || group.Count == 0) { return AppsAfterRetry.Unknown; }
        if (group.Any(r => r.State == PostCheckState.Failed)) { return AppsAfterRetry.SomeFailing; }
        return group.All(r => r.State == PostCheckState.Detected) ? AppsAfterRetry.AllDetected : AppsAfterRetry.Unknown;
    }

    /// <summary>The status line after a diagnosis that checked remedies other than a Retry run's.</summary>
    public static (string Text, Tone Tone) Status(IReadOnlyCollection<RemedyRecord> checkedNow)
    {
        var list = (checkedNow ?? Array.Empty<RemedyRecord>()).ToList();
        if (list.Count == 0) { return ("", Tone.Neutral); }
        var detected = list.Where(r => r.State == PostCheckState.Detected).ToList();
        var failed = list.Where(r => r.State == PostCheckState.Failed).ToList();
        var unknown = list.Where(r => r.State == PostCheckState.Unknown).ToList();
        var waiting = list.Where(r => r.State == PostCheckState.AwaitingEvidence).ToList();
        var parts = new List<string>();
        if (detected.Count > 0) { parts.Add("App detected: " + Names(detected) + "."); }
        if (failed.Count > 0) { parts.Add("Failed again: " + Names(failed) + "."); }
        if (unknown.Count > 0) { parts.Add("Result not known: " + Names(unknown) + "; copy details for IT."); }
        if (waiting.Count > 0) { parts.Add("Awaiting evidence: " + Names(waiting) + "; check again later."); }
        var tone = failed.Count > 0 || unknown.Count > 0 ? Tone.Warning : waiting.Count > 0 ? Tone.Neutral : Tone.Success;
        return ("Post-check: " + string.Join(" ", parts) + " Nothing was changed.", tone);
    }

    private static string Names(IEnumerable<RemedyRecord> records) => string.Join(", ", records.Select(r => r.Label).Distinct(StringComparer.CurrentCulture));

    /// <summary>A post-check the service did not run (busy, or its hourly limit): the previous list stays, and this says why.</summary>
    public static string NotChecked(ResultCode result) => result switch
    {
        ResultCode.Busy => "The apps were not checked again: another action is running on this device. Choose Check this app again when it has finished. Nothing was changed.",
        ResultCode.RateLimited => "The apps were not checked again: they were checked too often in the last hour (at most 30 times an hour). Try again later. Nothing was changed.",
        _ => "The apps were not checked again. Try again later. Nothing was changed.",
    };

    private static bool SameApp(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(a);

    private static int? Rev(int? revision) => revision is int r && r >= 0 ? r : null;
}
