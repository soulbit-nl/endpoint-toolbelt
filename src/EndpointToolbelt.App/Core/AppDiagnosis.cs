using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// The app's observed state, as the IME last recorded it. Kept apart from the state of the
/// run that read it (<see cref="DiagnosisRunState"/>): the two are never merged.
/// </summary>
public enum AppObservation
{
    /// <summary>Not installed yet, and nothing failed: the IME is working on it, waits for a restart, or waits for the user.</summary>
    AwaitingEvidence,

    /// <summary>Installed and found by its detection rule.</summary>
    Detected,

    /// <summary>The last attempt failed, or the app will not install on this device.</summary>
    Failed,

    /// <summary>The record does not say, or there is no record.</summary>
    Unknown,

    /// <summary>The IME says the app does not apply to this device (a requirement is not met); nothing to do.</summary>
    NotApplicable,

    /// <summary>Not on this device, and that is what its assignment asks (to be removed, or only a dependency); nothing to do.</summary>
    NotInstalledAsAssigned,

    /// <summary>The record is still on this device, but the IME's check-ins prove Intune no longer sends the app here.</summary>
    NoLongerAssigned,
}

/// <summary>The state of the diagnosis run itself, never the state of an app.</summary>
public enum DiagnosisRunState
{
    /// <summary>No diagnosis has run in this window yet.</summary>
    NotRun,
    Running,

    /// <summary>The run ended with its summary.</summary>
    Finished,

    /// <summary>The run ended without a result (the service stopped, the time limit, a crash), or did not start.</summary>
    Interrupted,
}

/// <summary>Which decision rule applies to an app.</summary>
public enum AppCase
{
    InstalledNotDetected,
    DownloadError,
    InstallFailed,
    RetryPlanned,
    AvailableFailed,
    RestartNeeded,
    DependencyBlocked,
    RequirementsNotMet,
    UnknownState,
    InProgress,
    Offered,
    Detected,
    NothingToDo,
    NotListed,
    ServiceOffline,
    ServiceTooOld,
    DiagnosisFailed,

    /// <summary>The app of a remedy that a newer diagnosis does not list for the same scope.</summary>
    Unmatched,

    /// <summary>An install finished with success and a detection run after it still says the app is not there.</summary>
    NotDetectedByIntune,

    /// <summary>The IME says the app does not apply to this device.</summary>
    NotApplicable,

    /// <summary>Assigned to be removed (or only a dependency), and not on this device.</summary>
    NotInstalledAsAssigned,

    /// <summary>Assigned to be removed and still on this device; no removal attempt yet.</summary>
    WaitingForRemoval,

    /// <summary>Assigned to be removed, and the removal failed.</summary>
    RemovalFailed,

    /// <summary>The record is still on this device, and the IME's check-ins prove the app is no longer assigned to its scope.</summary>
    NoLongerAssigned,
}

/// <summary>The one primary next step of a finding (at most one).</summary>
public enum AppNextStep
{
    None,

    /// <summary>Hand over to IT: "Copy details for IT" with this finding.</summary>
    CopyForIt,
    ConnectivityTest,
    RetryAppsAndPolicies,
    OpenCompanyPortal,

    /// <summary>Restart the device (said, not done: the app restarts nothing).</summary>
    Restart,

    /// <summary>Wait for the IME's own attempt.</summary>
    Wait,

    /// <summary>Two apps have the same name: the user chooses which one is meant.</summary>
    ChooseApp,

    /// <summary>After a remedy: read the app state again to see whether it worked (read-only).</summary>
    CheckAgain,
}

/// <summary>One app record of an <c>app-diagnosis</c> record, as the engine wrote it.</summary>
[DataContract]
public sealed class AppEvidence
{
    [DataMember(Name = "diagnosis")] public string DiagnosisId { get; set; } = "";
    [DataMember(Name = "app")] public string AppId { get; set; } = "";
    [DataMember(Name = "name")] public string Name { get; set; } = "";

    /// <summary>Where the name came from: "log" (the current IME logs), "cache" (an earlier IME log, kept in the name cache) or "".</summary>
    [DataMember(Name = "namesource")] public string NameSource { get; set; } = "";

    [DataMember(Name = "scope")] public string Scope { get; set; } = "";
    [DataMember(Name = "intent")] public string Intent { get; set; } = "";
    [DataMember(Name = "revision")] public int? Revision { get; set; }
    [DataMember(Name = "verdict")] public string Verdict { get; set; } = "";
    [DataMember(Name = "state")] public string State { get; set; } = "";
    [DataMember(Name = "enforcement")] public int? Enforcement { get; set; }
    [DataMember(Name = "code")] public string Code { get; set; } = "";
    [DataMember(Name = "installed")] public string Installed { get; set; } = "";
    [DataMember(Name = "observed")] public string ObservedText { get; set; } = "";
    [DataMember(Name = "phase")] public string Phase { get; set; } = "";
    [DataMember(Name = "attempt")] public string Attempt { get; set; } = "";
    [DataMember(Name = "attemptutc")] public string AttemptText { get; set; } = "";
    [DataMember(Name = "nextretry")] public string NextRetryText { get; set; } = "";
    [DataMember(Name = "download")] public bool Download { get; set; }
    [DataMember(Name = "shared")] public bool Shared { get; set; }

    /// <summary>An earlier revision's failed attempt, when this revision has none: its revision, code and record time.</summary>
    [DataMember(Name = "earlierrev")] public int? EarlierRevision { get; set; }
    [DataMember(Name = "earliercode")] public string EarlierCode { get; set; } = "";
    [DataMember(Name = "earlierutc")] public string EarlierText { get; set; } = "";

    /// <summary>When the newest attempt's install finished with success (the IME log); '' without that proof.</summary>
    [DataMember(Name = "installokutc")] public string InstallOkText { get; set; } = "";

    /// <summary>The newest detection-script run: when, its revision, "64" or "32", its exit code and "yes" or "no" output.</summary>
    [DataMember(Name = "detectutc")] public string DetectText { get; set; } = "";
    [DataMember(Name = "detectrev")] public int? DetectRevision { get; set; }
    [DataMember(Name = "detectbits")] public string DetectBits { get; set; } = "";
    [DataMember(Name = "detectexit")] public string DetectExit { get; set; } = "";
    [DataMember(Name = "detectoutput")] public string DetectOutput { get; set; } = "";

    /// <summary>How Intune deploys it: "win32" (or '' from an older service), "store" (WinGet, through the IME), "msi" or "msix" (MDM, not the IME).</summary>
    [DataMember(Name = "kind")] public string Kind { get; set; } = "";

    /// <summary>The code in words from the engine's shared table ("WinGet: package not found in source: ..."), its step, IT's step and the step token.</summary>
    [DataMember(Name = "codemeaning")] public string CodeMeaning { get; set; } = "";
    [DataMember(Name = "codestep")] public string CodeStep { get; set; } = "";
    [DataMember(Name = "codesupport")] public string CodeSupport { get; set; } = "";
    [DataMember(Name = "codenext")] public string CodeNext { get; set; } = "";

    /// <summary>An MSI app's CSP status (10 to 70), its version, the host it downloads from and its retries.</summary>
    [DataMember(Name = "msistatus")] public int? MsiStatus { get; set; }
    [DataMember(Name = "version")] public string Version { get; set; } = "";
    [DataMember(Name = "downloadhost")] public string DownloadHost { get; set; } = "";
    [DataMember(Name = "retryindex")] public int? RetryIndex { get; set; }
    [DataMember(Name = "retrycount")] public int? RetryCount { get; set; }

    /// <summary>
    /// "gone" when the IME's own check-ins prove the app is no longer assigned to this scope, with the
    /// first and the last of those check-ins (UTC) and how many there were; '' otherwise (no proof).
    /// </summary>
    [DataMember(Name = "assignment")] public string Assignment { get; set; } = "";
    [DataMember(Name = "assignfirst")] public string AssignFirstText { get; set; } = "";
    [DataMember(Name = "assignlast")] public string AssignLastText { get; set; } = "";
    [DataMember(Name = "assigncount")] public int? AssignCount { get; set; }

    public DateTime? AssignFirstUtc => TimeText.ParseUtc(AssignFirstText);
    public DateTime? AssignLastUtc => TimeText.ParseUtc(AssignLastText);

    /// <summary>The engine proved the app no longer assigned: the flag and both check-in times.</summary>
    public bool NoLongerAssigned => Assignment == "gone" && !IsMdmLob && AssignFirstUtc.HasValue && AssignLastUtc.HasValue;

    public bool IsMsi => Kind == "msi";

    /// <summary>An MSIX or Appx line-of-business app; its ID is the package family name.</summary>
    public bool IsMsix => Kind == "msix";

    /// <summary>A line-of-business app of the MDM stack (MSI or MSIX): no Intune app ID, no Company Portal page, not retried by the IME.</summary>
    public bool IsMdmLob => IsMsi || IsMsix;

    /// <summary>The kind in a word for the list: "MSI" or "Store"; '' for a Win32 app (the usual kind).</summary>
    public string KindWord => Kind switch { "msi" => "MSI", "msix" => "MSIX", "store" => "Store", _ => "" };

    public DateTime? EarlierUtc => TimeText.ParseUtc(EarlierText);
    public DateTime? InstallOkUtc => TimeText.ParseUtc(InstallOkText);
    public DateTime? DetectUtc => TimeText.ParseUtc(DetectText);

    /// <summary>When the IME last updated the record (UTC); null when unknown.</summary>
    public DateTime? ObservedUtc => TimeText.ParseUtc(ObservedText);

    /// <summary>The IME's own next attempt (UTC, an estimate); null when unknown.</summary>
    public DateTime? NextRetryUtc => TimeText.ParseUtc(NextRetryText);

    /// <summary>The name to show: the app's name, or "App" and the start of its ID.</summary>
    public string Label => !string.IsNullOrWhiteSpace(Name) ? Name.Trim()
        // An MSIX app without a name is called by its package name (the family name before "_").
        : IsMsix ? "MSIX app " + PackageName(AppId)
        : (IsMsi ? "MSI app " : "App ") + (AppId.Length >= 8 ? AppId.Substring(0, 8) : AppId);

    /// <summary>The package name part of a package family name ("Contoso.Viewer" of "Contoso.Viewer_abcdefghjkmnp").</summary>
    public static string PackageName(string familyName)
    {
        var name = familyName ?? "";
        var cut = name.LastIndexOf('_');
        return cut > 0 ? name.Substring(0, cut) : name;
    }

    /// <summary>The verdicts that need attention (the engine's order: the ones another attempt does not fix first).</summary>
    public bool NeedsAttention => Verdict is "PACKAGING" or "RESTART-FIRST" or "NOT-APPLICABLE" or "DEPENDENCY" or "RETRY-HELPS" or "UNKNOWN"
        || (Download && Verdict is "IN-PROGRESS" or "PENDING");
}

/// <summary>
/// What the window shows for one app: the observed state, the case, one primary next step
/// and the texts. It keeps the diagnosis ID, the app ID, its scope and revision, so a later check
/// (P0-3) can tie a new observation to this finding.
/// </summary>
public sealed class AppFinding
{
    public AppEvidence App { get; set; } = new();
    public string DiagnosisId { get; set; } = "";
    public AppObservation Observation { get; set; }
    public AppCase Case { get; set; }
    public AppNextStep Next { get; set; }
    public FindingSeverity Severity { get; set; } = FindingSeverity.Info;
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>What the primary step means for the user, in one sentence.</summary>
    public string NextText { get; set; } = "";

    /// <summary>
    /// The same app with the same outcome for the device and for the user: shown once, from the
    /// newer of the two records, and said to be for both.
    /// </summary>
    public bool ForBothScopes { get; set; }

    /// <summary>A warning said before the step (Retry work apps and settings affects more than this app).</summary>
    public string Caveat { get; set; } = "";

    /// <summary>What only IT support can do; empty when there is nothing.</summary>
    public string Support { get; set; } = "";

    /// <summary>Why this finding is not certain (stale or unknown record time, a shared app, two apps of one name); empty when it is.</summary>
    public string Uncertainty { get; set; } = "";

    /// <summary>Another app in the list has the same name: the user picks which one is meant.</summary>
    public bool SameNameAsAnother { get; set; }

    /// <summary>The active remedy taken for this app; null when none.</summary>
    public RemedyRecord Remedy { get; set; }

    /// <summary>The post-check state after <see cref="Remedy"/>; null without a remedy.</summary>
    public PostCheckState? AfterRemedy { get; set; }

    public PostCheckReason AfterRemedyReason { get; set; }

    /// <summary>An app with a remedy needs attention until a post-check detected it; otherwise its verdict decides.</summary>
    public bool NeedsAttention => Case != AppCase.NoLongerAssigned
        && ((AfterRemedy is PostCheckState state ? state != PostCheckState.Detected : App.NeedsAttention)
            || Case is AppCase.NotListed or AppCase.ServiceOffline or AppCase.ServiceTooOld or AppCase.DiagnosisFailed or AppCase.Unmatched or AppCase.NotDetectedByIntune);

    /// <summary>
    /// Facts that are evidence, not the verdict: an earlier version's failed attempt, the last
    /// detection-script run, the revision and whether Intune may have a newer one. Shown on the
    /// app's card and carried by Copy details for IT.
    /// </summary>
    public string Evidence { get; set; } = "";

    /// <summary>The raw IME record for IT support (such as a requirement code); in View details and Copy details for IT only.</summary>
    public string Record { get; set; } = "";

    /// <summary>The finding as a result card (drawer, run log, Copy summary, Copy details for IT).</summary>
    public ResultCard Card()
    {
        var detail = string.Join(" ", new[] { Text, Evidence, Uncertainty, Caveat }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return ResultCard.From(Severity, Title, detail, NextText, Join(Join(Support, Record), Identity));
    }

    /// <summary>
    /// What IT support needs to find the app in Intune: its ID, scope and revision (also the seam a
    /// later check uses to tie a new observation to this finding); empty for a finding without an app.
    /// </summary>
    public string Identity
    {
        get
        {
            if (string.IsNullOrEmpty(App.AppId)) { return ""; }
            // An MSI line-of-business app has no Intune app ID or revision on the device: its product code and version.
            if (App.IsMsi)
            {
                return "MSI product code " + AppDecisions.MsiProductCode(App.AppId) + (App.Version.Length > 0 ? ", version " + App.Version : "")
                    + " (" + (App.Scope == "user" ? "user" : "device") + " assignment).";
            }
            // An MSIX line-of-business app: its package family name (and the version installed now, if any).
            if (App.IsMsix)
            {
                return "MSIX package family " + App.AppId + (App.Version.Length > 0 ? ", version " + App.Version + " installed" : "")
                    + " (" + (App.Scope == "user" ? "user" : "device") + " assignment).";
            }
            var revision = App.Revision is int r && r >= 0 ? ", revision " + r.ToString(CultureInfo.InvariantCulture) : "";
            // A name the current IME logs no longer hold came from the name cache; said here only, for IT.
            var source = NameFromEarlierLog ? " Name from an earlier IME log." : "";
            return "App ID " + App.AppId + " (" + (App.Scope == "user" ? "user" : "device") + " assignment" + revision + ")." + source;
        }
    }

    private static string Join(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : string.IsNullOrWhiteSpace(b) ? a : ResultCard.Sentence(a.Trim()) + " " + b;

    /// <summary>The shown name came from the name cache (an earlier IME log), not from the current logs.</summary>
    public bool NameFromEarlierLog => App.NameSource == "cache" && !string.IsNullOrWhiteSpace(App.Name);
}

/// <summary>
/// One app diagnosis: the run that read it and what it found. Home and "An app won't
/// install" both show this object, so they always show the same diagnosis for the same data.
/// </summary>
public sealed class AppDiagnosis
{
    /// <summary>The run ID of the diagnosis; it identifies this diagnosis (a later check refers to it).</summary>
    public string DiagnosisId { get; set; } = "";

    public DiagnosisRunState Run { get; set; } = DiagnosisRunState.NotRun;

    /// <summary>When the records were read (UTC): the summary's time, or the newest record's.</summary>
    public DateTime? CapturedUtc { get; set; }

    /// <summary>The IME has app state on this device.</summary>
    public bool Present { get; set; }

    /// <summary>App records read, and how many of them are listed (the engine lists at most 150).</summary>
    public int Records { get; set; }
    public int Listed { get; set; }

    /// <summary>Other users' scopes on this device (counted, never shown).</summary>
    public int OtherScopes { get; set; }

    /// <summary>The user's own apps could be read (an account Entra can match).</summary>
    public bool UserScope { get; set; } = true;

    public List<AppFinding> Apps { get; } = new();

    /// <summary>A finding about the diagnosis itself (service offline or too old, a run that did not finish); null when the apps were read.</summary>
    public AppFinding Problem { get; set; }

    public IEnumerable<AppFinding> Attention => Apps.Where(a => a.NeedsAttention);

    /// <summary>
    /// The main finding: the diagnosis's own problem, else the first app that needs attention (the
    /// engine sorts them, the ones another attempt does not fix first); null when nothing does.
    /// </summary>
    public AppFinding Main => Problem ?? Attention.FirstOrDefault();

    /// <summary>The cards of the diagnosis: its problem, then every app that needs attention.</summary>
    public List<ResultCard> Cards()
    {
        var cards = new List<ResultCard>();
        if (Problem != null) { cards.Add(Problem.Card()); }
        cards.AddRange(Attention.Select(a => a.Card()));
        if (cards.Count == 0 && Run == DiagnosisRunState.Finished)
        {
            cards.Add(ResultCard.From(FindingSeverity.Ok, Present ? "No app needs attention" : "No Win32 app state on this device",
                Present
                    ? Words.Count(Records, "app record") + " read; each is installed, on its way, or has nothing to do on this device."
                    : "The IME has not processed a Win32 app here yet, or its state was cleared.",
                "If an app you need is missing, choose \"My app is not in this list\"."));
        }
        return cards;
    }
}

/// <summary>
/// The decision rules of the app diagnosis: each engine verdict, with what the record says
/// about the error, the intent and the time, becomes one observed state, one case, one primary
/// next step and the user's texts. Pure, so every rule is tested without a window.
/// </summary>
public static class AppDecisions
{
    /// <summary>The app list shows its search box from this many apps on.</summary>
    public const int SearchFrom = 6;

    /// <summary>The rows the short list shows before Show all, so the app page fits its window.</summary>
    public const int ShortListRows = 3;

    /// <summary>
    /// The short app list: what needs attention, what is on its way, and an app the user took a step
    /// for. An app that is only offered in Company Portal and was never tried is not a problem: it is
    /// in the full list and the search finds it.
    /// </summary>
    public static bool InShortList(AppFinding app)
        => app != null && app.Case != AppCase.NoLongerAssigned && (app.NeedsAttention || app.Remedy != null
            || (app.Observation == AppObservation.AwaitingEvidence && app.Case != AppCase.Offered));

    /// <summary>
    /// The apps whose name (or ID) holds every word of the query, in their list order, over all apps
    /// (also those installed or with nothing to do); every app for an empty query.
    /// </summary>
    public static List<AppFinding> Search(IEnumerable<AppFinding> apps, string query)
    {
        var words = (query ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        return (apps ?? Enumerable.Empty<AppFinding>()).Where(a => a?.App != null && words.All(w =>
            (a.App.Label ?? "").IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0
            || (a.App.AppId ?? "").StartsWith(w, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>A record older than this may be out of date: the finding says so and offers the handover.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    /// <summary>The engine's state for an app Intune wants absent that is absent (an uninstall assignment, or a superseded app).</summary>
    internal const string NotPresentAsAssigned = "not present, as assigned";

    /// <summary>The engine's state for an MSI app recorded as installed whose product Windows Installer no longer has.</summary>
    internal const string NoLongerInstalled = "recorded as installed; Windows Installer no longer has it";

    /// <summary>Said before Retry work apps and settings is offered for one app: it is not only for that app.</summary>
    public const string RetryCaveat = "Retry work apps and settings is not only for this app: it restarts the Intune Management Extension, makes it try every failed app again and asks Intune to apply all policies again.";

    public const string NotDetectedCode = "0x87D1041C";

    /// <summary>The diagnosis from a run's records (also a run in progress or one that did not run).</summary>
    public static AppDiagnosis From(RunOutcome outcome, DateTime nowUtc, TimeZoneInfo zone = null, DateTime? lastImeCheckInUtc = null)
    {
        var diagnosis = new AppDiagnosis { DiagnosisId = outcome?.RunId ?? "" };
        if (outcome == null)
        {
            diagnosis.Run = DiagnosisRunState.Interrupted;
            diagnosis.Problem = Special(AppCase.ServiceOffline, diagnosis.DiagnosisId);
            return diagnosis;
        }
        var evidence = new List<AppEvidence>();
        EngineRecord close = null;
        foreach (var record in outcome.Records)
        {
            if (record?.Type == "app-diagnosis" && record.App != null) { evidence.Add(record.App); }
            else if (record?.Type == "apps-diagnosis") { close = record; }
        }
        var summary = outcome.Records.LastOrDefault(r => r?.Type == "summary");
        diagnosis.CapturedUtc = summary?.Utc ?? outcome.Records.LastOrDefault(r => r?.Utc != null)?.Utc;
        if (close?.Closing != null)
        {
            diagnosis.Present = close.Closing.Present;
            diagnosis.Records = close.Closing.Records;
            diagnosis.Listed = close.Closing.Listed;
            diagnosis.OtherScopes = close.Closing.OtherScopes;
            diagnosis.UserScope = close.Closing.UserScope;
        }

        if (outcome.InProgress)
        {
            diagnosis.Run = DiagnosisRunState.Running;
        }
        else if (outcome.Result == ResultCode.Completed && summary != null && close != null && outcome.ExitCode == EngineExitCode.Success)
        {
            diagnosis.Run = DiagnosisRunState.Finished;
        }
        else
        {
            diagnosis.Run = DiagnosisRunState.Interrupted;
            diagnosis.Problem = outcome.Result switch
            {
                ResultCode.ServiceUnavailable or ResultCode.ServerIdentityMismatch => Special(AppCase.ServiceOffline, diagnosis.DiagnosisId),
                // An older service does not know the action: its gate for an action outside its table.
                ResultCode.InvalidRequest or ResultCode.ProtocolMismatch => Special(AppCase.ServiceTooOld, diagnosis.DiagnosisId),
                _ => Special(AppCase.DiagnosisFailed, diagnosis.DiagnosisId, outcome),
            };
        }

        // A name conflict is two different apps (app IDs) of one name; one app listed for the
        // device and for you (two scopes) is the same app, each row with its own state.
        var names = evidence.Where(e => !string.IsNullOrWhiteSpace(e.Name)).GroupBy(e => e.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Where(g => g.Select(e => (e.AppId ?? "").ToLowerInvariant()).Distinct(StringComparer.Ordinal).Count() > 1).Select(g => g.Key).ToList();
        foreach (var app in evidence)
        {
            var finding = Decide(app, evidence, nowUtc, zone, lastImeCheckInUtc);
            if (!string.IsNullOrWhiteSpace(app.Name) && names.Contains(app.Name.Trim(), StringComparer.CurrentCultureIgnoreCase))
            {
                finding.SameNameAsAnother = true;
                finding.Uncertainty = Join(finding.Uncertainty, "Another app on this device has the same name; this one is " + Identify(app, zone) + ".");
            }
            diagnosis.Apps.Add(finding);
        }
        MergeScopes(diagnosis.Apps);
        return diagnosis;
    }

    /// <summary>
    /// One app with the same case for the device and for the user is one problem, not two: the
    /// newer record stays (its facts are the latest), marked as for both, and the other goes. A
    /// different case in each scope stays two rows, each with its own next step.
    /// </summary>
    internal static void MergeScopes(List<AppFinding> apps)
    {
        var pairs = apps.Where(a => !string.IsNullOrEmpty(a.App.AppId) && a.App.Scope is "user" or "device")
            .GroupBy(a => a.App.AppId.ToLowerInvariant())
            // A record no longer assigned says to whom it is no longer assigned: never merged.
            .Where(g => g.Count() == 2 && g.Select(a => a.App.Scope).Distinct().Count() == 2 && g.Select(a => a.Case).Distinct().Count() == 1 && g.All(a => a.Case != AppCase.NoLongerAssigned))
            .ToList();
        foreach (var pair in pairs)
        {
            var keep = pair.OrderByDescending(a => a.App.ObservedUtc ?? DateTime.MinValue).First();
            keep.ForBothScopes = true;
            foreach (var other in pair.Where(a => !ReferenceEquals(a, keep))) { apps.Remove(other); }
        }
    }

    /// <summary>"the one for this device, revision 3, ID 1a2b3c4d": what tells two apps of one name apart.</summary>
    public static string Identify(AppEvidence app, TimeZoneInfo zone = null)
    {
        var scope = app.Scope == "user" ? "the one assigned to you" : "the one for this device";
        // An MSI line-of-business app is known by its product code (and its version), not an Intune app ID.
        if (app.IsMsi)
        {
            return "MSI app, " + scope + (app.Version.Length > 0 ? ", version " + app.Version : "") + ", product code " + MsiProductCode(app.AppId);
        }
        // An MSIX line-of-business app is known by its package family name.
        if (app.IsMsix)
        {
            return "MSIX app, " + scope + (app.Version.Length > 0 ? ", version " + app.Version + " installed" : "") + ", package family " + app.AppId;
        }
        if (app.Kind == "store") { scope = "Microsoft Store app, " + scope; }
        // The revision with the day the device last checked it (a newer one may exist in Intune),
        // and the full app ID, so IT support can find the app in the Intune admin center.
        var revision = app.Revision is int r && r >= 0
            ? ", revision " + r.ToString(CultureInfo.InvariantCulture) + (app.ObservedUtc is DateTime seen ? ", last checked by the device on " + Day(seen, zone) : "")
            : "";
        return scope + revision + ", ID " + app.AppId;
    }

    /// <summary>A day in the user's own time, as "11 Nov".</summary>
    public static string Day(DateTime utc, TimeZoneInfo zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeText.TryLocal(utc, zone, out var l) ? l : utc;
        return local.ToString("d MMM", CultureInfo.InvariantCulture);
    }

    /// <summary>PSAppDeployToolkit's own exit codes (the toolkit failed, not the vendor's installer).</summary>
    public const int PsadtFirst = 60000;
    public const int PsadtLast = 68999;

    /// <summary>
    /// The installer's exit code inside an IME error code: an HRESULT 0x8007xxxx for a Win32 app
    /// carries the installer's exit code (0x8007EA68 is 60008); null for any other code.
    /// </summary>
    public static int? InstallerExitCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) { return null; }
        var text = code.Trim();
        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || !uint.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) { return null; }
        return (value >> 16) == 0x8007 ? (int)(value & 0xFFFF) : null;
    }

    /// <summary>An error code in words: "exit code 60008" for an installer's exit code, else "error 0x...".</summary>
    public static string CodeWords(string code)
        => InstallerExitCode(code) is int exit ? "exit code " + exit.ToString(CultureInfo.InvariantCulture) : "error " + code;

    /// <summary>The plain sentence for a code in PSAppDeployToolkit's range; '' for any other code. No meaning per code is given.</summary>
    public static string PsadtSentence(string code)
        => InstallerExitCode(code) is int exit && exit >= PsadtFirst && exit <= PsadtLast
            ? "Exit code " + exit.ToString(CultureInfo.InvariantCulture) + " is in PSAppDeployToolkit's own range (" + PsadtFirst.ToString(CultureInfo.InvariantCulture) + " to " + PsadtLast.ToString(CultureInfo.InvariantCulture) + "): the deployment toolkit stopped the install, not the app's own installer."
            : "";

    /// <summary>IT support's step for a code in PSAppDeployToolkit's range.</summary>
    public const string PsadtSupport = @"Check the PSAppDeployToolkit log of that install (by default in %ProgramData%\Logs\Software or %WINDIR%\Logs\Software).";

    /// <summary>
    /// The Intune Management Extension's own codes (0x87D...) that Microsoft documents. The engine's
    /// code table holds the same ones (a test keeps the two equal); any other 0x87D code is one
    /// Microsoft does not describe, and the app says so instead of guessing.
    /// </summary>
    public static readonly string[] DocumentedIntuneCodes = { NotDetectedCode, "0x87D300C9" };

    /// <summary>An Intune Management Extension code (0x87D...) that Microsoft does not document.</summary>
    public static bool IsUndocumentedIntuneCode(string code)
        => !string.IsNullOrWhiteSpace(code) && code.Trim().StartsWith("0x87D", StringComparison.OrdinalIgnoreCase)
            && !DocumentedIntuneCodes.Any(c => string.Equals(c, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Windows Installer's 1602 (ERROR_INSTALL_USEREXIT): the installation was canceled.</summary>
    public const int CanceledExitCode = 1602;

    /// <summary>The first words of a failed install: a canceled one (1602) says so, any other failure says it failed.</summary>
    private static string Failure(string code)
        => InstallerExitCode(code) == CanceledExitCode ? "The installation was canceled before it finished" : "The install failed on this device";

    /// <summary>The decision for one app, with the other apps of the diagnosis (for a dependency).</summary>
    public static AppFinding Decide(AppEvidence app, IReadOnlyList<AppEvidence> all, DateTime nowUtc, TimeZoneInfo zone = null, DateTime? lastImeCheckInUtc = null)
    {
        var f = new AppFinding { App = app, DiagnosisId = app.DiagnosisId };
        var name = app.Label;
        // An installer's exit code inside the IME's code is said as such ("exit code 60008, 0x8007EA68").
        var code = string.IsNullOrEmpty(app.Code) ? ""
            : InstallerExitCode(app.Code) is int exitCode ? " (exit code " + exitCode.ToString(CultureInfo.InvariantCulture) + ", " + app.Code + ")"
            : " (error " + app.Code + ")";
        // The IME log's newest attempt ("started, no step recorded yet") is a fact for Show technical details, not the story.
        var attempt = "";
        var retryAt = app.NextRetryUtc is DateTime next && next > nowUtc ? next : (DateTime?)null;
        var retryText = retryAt.HasValue ? " The Intune Management Extension tries again by itself around " + When(retryAt.Value, zone) + " (an estimate)." : "";

        // An MSI line-of-business app is the MDM stack's, not the IME's: its own wording and steps.
        if (app.IsMsi) { DecideMsi(f, app, name, code); }
        // An MSIX line-of-business app is the MDM stack's too: the AppX deployment code decides its step.
        else if (app.IsMsix) { DecideMsix(f, app, name); }
        else switch (app.Verdict)
        {
            case "PACKAGING":
                Set(f, AppCase.InstalledNotDetected, AppObservation.Failed, FindingSeverity.Error, AppNextStep.CopyForIt,
                    name + ": installed, but not detected",
                    "The installer finished, but the detection rule Intune uses for this app does not find it, so the install counts as failed. Trying again repeats exactly that; the packaging team must check the package and its detection rule.",
                    "Copy details for IT and send it to your IT support. Do not retry: it gives the same result.",
                    "Check the app's package and its detection rule against what the installer installs (" + (app.Code.Length > 0 ? app.Code : NotDetectedCode) + ").");
                break;
            case "RETRY-HELPS" when app.Intent == "available":
                Set(f, AppCase.AvailableFailed, AppObservation.Failed, FindingSeverity.Warning, AppNextStep.OpenCompanyPortal,
                    name + ": the install from Company Portal failed",
                    "This app is offered to you in Company Portal" + code + ". The next attempt is yours, from its page in Company Portal." + attempt,
                    "Open the app's page in Company Portal and choose Install or Retry there.",
                    "");
                break;
            case "RETRY-HELPS" when app.Download:
            case "IN-PROGRESS" when app.Download:
            case "PENDING" when app.Download:
                Set(f, AppCase.DownloadError, AppObservation.Failed, FindingSeverity.Warning, AppNextStep.ConnectivityTest,
                    name + ": the download did not work",
                    "The app's content could not be downloaded" + code + ". That is often temporary: the network, a VPN or a proxy." + attempt + retryText,
                    "Run Check Microsoft connections. When the connection works, Retry work apps and settings starts a new attempt.",
                    "Check Delivery Optimization and the network path to the content (proxy, VPN split tunnel, bandwidth limits).");
                f.Caveat = RetryCaveat;
                break;
            case "RETRY-HELPS" when IsUndocumentedIntuneCode(app.Code):
                // Microsoft describes no meaning for the code: said plainly, and handed to IT, not retried blindly.
                Set(f, AppCase.InstallFailed, AppObservation.Failed, FindingSeverity.Warning, AppNextStep.CopyForIt,
                    name + ": the install failed",
                    "The install failed on this device. Intune reported an error that Microsoft does not describe (" + app.Code.Trim() + ")." + attempt + retryText,
                    "Use Copy details for IT so IT support can check it.",
                    "Microsoft documents no meaning for this code. The IME logs in a support package show what happened before it.");
                break;
            case "RETRY-HELPS" when retryAt.HasValue:
                Set(f, AppCase.RetryPlanned, AppObservation.Failed, FindingSeverity.Warning, AppNextStep.Wait,
                    name + ": the install failed; another attempt is planned",
                    Failure(app.Code) + code + "." + attempt + retryText,
                    "Wait for that attempt. To try now instead, use Retry work apps and settings.",
                    "If it fails again with the same code, the code is the lead; the IME logs in a support package show the installer run.");
                f.Caveat = RetryCaveat;
                break;
            case "RETRY-HELPS":
                Set(f, AppCase.InstallFailed, AppObservation.Failed, FindingSeverity.Warning, AppNextStep.RetryAppsAndPolicies,
                    name + ": the install failed",
                    Failure(app.Code) + code + ". A new attempt is the right next step." + attempt,
                    "Use Retry work apps and settings. If it fails again with the same code, copy details for IT.",
                    "If it fails again with the same code, the code is the lead; the IME logs in a support package show the installer run.");
                f.Caveat = RetryCaveat;
                break;
            case "RESTART-FIRST":
                Set(f, AppCase.RestartNeeded, AppObservation.AwaitingEvidence, FindingSeverity.Warning, AppNextStep.Restart,
                    name + ": waits for a restart",
                    "This app finishes only after the device restarts (" + app.State + "). No retry or reset changes that before the restart.",
                    "Restart the device, then check the apps again.",
                    "");
                break;
            case "DEPENDENCY":
                var dependencies = all.Where(o => !ReferenceEquals(o, app) && o.Intent == "dependency" && o.NeedsAttention).ToList();
                Set(f, AppCase.DependencyBlocked, AppObservation.Failed, FindingSeverity.Warning, dependencies.Count == 0 ? AppNextStep.CopyForIt : AppNextStep.None,
                    name + ": blocked by an app it depends on",
                    dependencies.Count == 0
                        ? "It waits for an app it depends on. This device does not record which app that is; your IT support can see it in Intune."
                        : "It depends on " + string.Join(", ", dependencies.Select(d => d.Label)) + ", which " + (dependencies.Count == 1 ? "does" : "do") + " not install either. That app is the one to fix first: it is in this list.",
                    dependencies.Count == 0 ? "Copy details for IT and send it to your IT support." : "Select " + dependencies[0].Label + " in this list for its next step.",
                    "Check the dependencies of this app in Intune and why the first of them does not install.");
                break;
            case "NOT-APPLICABLE":
                Set(f, AppCase.RequirementsNotMet, AppObservation.Failed, FindingSeverity.Info, AppNextStep.CopyForIt,
                    name + ": does not apply to this device",
                    "Its requirement rules or dependencies keep it off this device (" + app.State + "). That is not a fault on this device, and nothing here changes it. Whether it should apply is for your IT support to decide.",
                    "If you need this app, copy details for IT and send it to your IT support.",
                    "Check the requirement rules and the assignment of this app in Intune.");
                break;
            case "UNKNOWN":
                Set(f, AppCase.UnknownState, AppObservation.Unknown, FindingSeverity.Warning, AppNextStep.CopyForIt,
                    name + ": in a state that is not recognised",
                    "The Intune Management Extension recorded an error" + code + " in a state " + ProductInfo.DisplayName + " cannot classify, so the right next step is not certain." + attempt,
                    "Copy details for IT and send it to your IT support.",
                    "Read the error code and the IME logs in a support package.");
                f.Uncertainty = "The state of this app is not certain.";
                break;
            case "IN-PROGRESS":
            case "PENDING":
                Set(f, AppCase.InProgress, AppObservation.AwaitingEvidence, FindingSeverity.Info, AppNextStep.Wait,
                    name + ": being installed or waiting its turn",
                    "The Intune Management Extension is working on it or has not started it yet (" + app.State + ")." + attempt + retryText,
                    "Wait; large apps can take a while. Check the apps again later.",
                    "");
                break;
            case "AVAILABLE":
                Set(f, AppCase.Offered, AppObservation.AwaitingEvidence, FindingSeverity.Info, AppNextStep.OpenCompanyPortal,
                    name + ": offered in Company Portal, not installed",
                    "This app is offered to you; it installs when you choose Install in Company Portal.",
                    "Open the app's page in Company Portal and choose Install.",
                    "");
                break;
            case "OK":
                Set(f, AppCase.Detected, AppObservation.Detected, FindingSeverity.Ok, AppNextStep.None,
                    name + (app.Intent == "uninstall" ? ": removed as assigned" : ": installed and detected"),
                    "The Intune Management Extension found it with its detection rule (" + app.State + ").",
                    "", "");
                break;
            default:
                if (NotApplicableReason(app.State) is string requirement)
                {
                    // The IME says the app does not apply here: said plainly, the raw code only
                    // in View details and Copy details for IT.
                    Set(f, AppCase.NotApplicable, AppObservation.NotApplicable, FindingSeverity.Info, AppNextStep.None,
                        name + ": not meant for this device", NotApplicableText(requirement), "", "");
                    f.Record = "IME record: " + app.State + ".";
                    break;
                }
                if (app.Verdict == "NO-ACTION" && (app.State ?? "").StartsWith(NotPresentAsAssigned, StringComparison.OrdinalIgnoreCase))
                {
                    // Intune wants it absent here (a newer app supersedes it, or it is assigned away) and it is absent.
                    Set(f, AppCase.NotInstalledAsAssigned, AppObservation.NotInstalledAsAssigned, FindingSeverity.Info, AppNextStep.None,
                        name + ": not installed (as assigned)",
                        "Intune wants this app absent on this device, as for an app a newer one replaces, and it is not here. Nothing to do.", "", "");
                    f.Record = "IME record: " + app.State + ".";
                    break;
                }
                if (app.Verdict == "NO-ACTION" && (app.State ?? "").StartsWith(NoLongerInstalled, StringComparison.OrdinalIgnoreCase))
                {
                    // An MSI app recorded as installed whose product Windows Installer no longer has.
                    Set(f, AppCase.NotInstalledAsAssigned, AppObservation.NotInstalledAsAssigned, FindingSeverity.Info, AppNextStep.None,
                        name + ": not installed (replaced or removed)",
                        "Intune still has a record of this app as installed, but Windows Installer no longer has it on this device: a newer version replaced it, or it was removed. Nothing to do.", "", "");
                    f.Record = "IME record: " + app.State + ".";
                    break;
                }
                if (app.Verdict == "NO-ACTION" && (app.State ?? "").StartsWith("not installed; a dependency", StringComparison.OrdinalIgnoreCase))
                {
                    // A dependency that is not assigned itself installs only with the app that needs it.
                    Set(f, AppCase.NotInstalledAsAssigned, AppObservation.NotInstalledAsAssigned, FindingSeverity.Info, AppNextStep.None,
                        name + ": not installed (a dependency)",
                        "This app installs only together with an app that needs it, and it is not on this device. Nothing to do.", "", "");
                    f.Record = "IME record: " + app.State + ".";
                    break;
                }
                Set(f, AppCase.NothingToDo, AppObservation.Unknown, FindingSeverity.Info, AppNextStep.None,
                    name + ": nothing to do on this device",
                    "The record says: " + (string.IsNullOrEmpty(app.State) ? "no state recorded" : app.State) + ".",
                    "", "");
                break;
        }
        if (app.Intent == "uninstall") { Uninstall(f, app, name, code, attempt); }

        // A Microsoft Store app with a WinGet code: the code in plain words, and the step it leads to.
        if (app.Kind == "store" && app.CodeMeaning.Length > 0 && f.Observation != AppObservation.Detected)
        {
            f.Text = Join(f.Text, "WinGet says: " + app.CodeMeaning);
            if (app.CodeNext.Length > 0 && f.Case is AppCase.InstallFailed or AppCase.RetryPlanned or AppCase.UnknownState or AppCase.DownloadError)
            {
                (f.Next, f.Caveat) = app.CodeNext switch
                {
                    "retry" => (AppNextStep.RetryAppsAndPolicies, RetryCaveat),
                    "connectivity" => (AppNextStep.ConnectivityTest, RetryCaveat),
                    "restart" => (AppNextStep.Restart, ""),
                    "wait" => (AppNextStep.Wait, ""),
                    "none" => (AppNextStep.None, ""),
                    _ => (AppNextStep.CopyForIt, ""),
                };
                if (app.CodeStep.Length > 0) { f.NextText = app.CodeStep; }
                if (app.CodeSupport.Length > 0) { f.Support = app.CodeSupport; }
            }
        }

        if (ProvenNotDetected(app))
        {
            // Only on proof: an install of this revision finished with success and a detection
            // run after it still says the app is not there.
            Set(f, AppCase.NotDetectedByIntune, AppObservation.Failed, FindingSeverity.Warning, AppNextStep.CopyForIt,
                name + ": Intune does not detect this app",
                "Intune decides \"installed\" with a detection rule, and that rule says the app is not there. The install finished at "
                    + When(app.InstallOkUtc.Value, zone) + "; the detection ran after it.",
                "If the app is installed and works, there is nothing to do for you; tell your IT support.",
                "Check the app's detection rule in Intune. The script ran as " + (app.DetectBits == "32" ? "32-bit" : "64-bit") + " and found nothing"
                    + (app.DetectBits == "32" ? "; check the paths and the version it expects." : "; for a 32-bit app check the 32-bit setting, WOW6432Node and Program Files (x86) paths, and the version it expects."));
        }
        f.Evidence = Join(f.Evidence, EarlierAttempt(app, zone));
        if (PsadtSentence(app.EarlierCode).Length > 0 && app.Enforcement == null) { f.Support = Join(f.Support, PsadtSupport); }
        if (PsadtSentence(app.Code).Length > 0)
        {
            f.Evidence = Join(f.Evidence, PsadtSentence(app.Code));
            f.Support = Join(f.Support, PsadtSupport);
        }
        f.Evidence = Join(f.Evidence, DetectionFacts(app, zone));
        if (!string.IsNullOrEmpty(app.Attempt)) { f.Evidence = Join(f.Evidence, "Newest attempt in the IME log: " + app.Attempt + "."); }
        if (app.Intent == "available" && app.ObservedUtc is DateTime checkedAt && lastImeCheckInUtc is DateTime checkIn && checkedAt < checkIn && f.Case != AppCase.Detected)
        {
            f.Evidence = Join(f.Evidence, NewerVersionLine);
        }

        if (f.Case is not (AppCase.Detected or AppCase.NothingToDo or AppCase.NotApplicable or AppCase.NotInstalledAsAssigned))
        {
            if (app.ObservedUtc is not DateTime observed)
            {
                f.Uncertainty = Join(f.Uncertainty, "The time of this record is not known, so it may be out of date.");
            }
            else if (nowUtc - observed > StaleAfter && app.NeedsAttention)
            {
                // A stale measurement: say so and hand over, rather than act on old evidence.
                f.Uncertainty = Join(f.Uncertainty, "This record is from " + When(observed, zone) + " and may be out of date.");
                if (f.Next is not (AppNextStep.CopyForIt or AppNextStep.None))
                {
                    f.NextText = "Check the apps again later. If it stays like this, copy details for IT and send it to your IT support.";
                    f.Next = AppNextStep.CopyForIt;
                }
            }
            else if (nowUtc - observed > StaleAfter && f.Case == AppCase.Offered)
            {
                // An offered app's record is renewed when the user's apps are fetched again, which on
                // a shared or virtual desktop can wait until Company Portal is opened: the next step
                // stays Company Portal, which also brings the record up to date.
                f.Uncertainty = Join(f.Uncertainty, "This record is from " + When(observed, zone) + " and may be out of date: opening Company Portal brings it up to date, and the app may turn out to be installed or required by now.");
            }
        }
        if (app.Shared && app.NeedsAttention)
        {
            f.Uncertainty = Join(f.Uncertainty, "Another user on this device has this app too, so the IME log is not used as evidence for it.");
        }
        if (app.NoLongerAssigned) { NoLongerAssigned(f, app, name, zone); }
        return f;
    }

    /// <summary>The list's word for a record no longer assigned to the device.</summary>
    public const string NoLongerAssignedDevice = "No longer assigned to this device";

    /// <summary>The list's word for a record no longer assigned to the user.</summary>
    public const string NoLongerAssignedUser = "No longer assigned to you";

    /// <summary>The list's word for a record no longer assigned, by its scope.</summary>
    public static string NoLongerAssignedWord(AppEvidence app) => app?.Scope == "user" ? NoLongerAssignedUser : NoLongerAssignedDevice;

    /// <summary>
    /// A record the engine proved no longer assigned: it is still on this device, Intune no longer
    /// sends the app here, and the two check-in times are the evidence. No step: no retry and no
    /// Company Portal link (nothing here changes an assignment); what the record read before stays for IT.
    /// </summary>
    private static void NoLongerAssigned(AppFinding f, AppEvidence app, string name, TimeZoneInfo zone)
    {
        var whom = app.Scope == "user" ? "you" : "this device";
        var record = (app.State ?? "").Length > 0 ? "IME record: " + app.State + "." : "";
        Set(f, AppCase.NoLongerAssigned, AppObservation.NoLongerAssigned, FindingSeverity.Info, AppNextStep.None,
            name + ": no longer assigned to " + whom,
            "The record of this app is still on this device, but Intune no longer sends the app to " + whom + ". Removing an assignment does not remove the app itself, so it may still be installed. Nothing on this device was changed.",
            "If you still need this app, copy details for IT and send it to your IT support.",
            "Intune no longer assigns this app to this " + (app.Scope == "user" ? "user" : "device") + "; the Intune Management Extension keeps the record. If the app should still be there, check its assignments in Intune.");
        f.Evidence = AssignmentEvidence(app, zone);
        f.Caveat = "";
        f.Uncertainty = "";
        f.Record = record;
    }

    /// <summary>
    /// "Evidence: the Intune Management Extension's check-ins for required apps at ... and at ...
    /// (3 in all) did not list this app." The two times in the user's zone.
    /// </summary>
    public static string AssignmentEvidence(AppEvidence app, TimeZoneInfo zone = null)
    {
        if (app?.AssignFirstUtc is not DateTime first || app.AssignLastUtc is not DateTime last) { return ""; }
        var count = app.AssignCount is int n && n > 2 ? " (" + n.ToString(CultureInfo.InvariantCulture) + " in all)" : "";
        return "Evidence: the Intune Management Extension's check-ins for required apps at " + When(first, zone) + " and at " + When(last, zone) + count + " did not list this app.";
    }

    /// <summary>
    /// The detail card links to the app's page in Company Portal: only an app assigned to the
    /// user (a device-scoped app often has no page for the user, and "not found" is worse than no
    /// link), with an app ID that is a GUID (the documented companyportal:ApplicationId= link).
    /// </summary>
    public static bool HasPortalPage(AppFinding finding)
        => finding?.App != null && finding.App.Scope == "user" && !finding.App.IsMdmLob && finding.App.Intent != "uninstall" && finding.Case != AppCase.NoLongerAssigned && Guid.TryParse(finding.App.AppId ?? "", out _);

    /// <summary>
    /// An uninstall assignment in its own words. Not on the device (or removed): "Not installed
    /// (as assigned)", nothing to do; still detected with no attempt: waiting to be removed; a failed
    /// attempt: the removal failed, with its code. A restart it waits for keeps its own words.
    /// </summary>
    private static void Uninstall(AppFinding f, AppEvidence app, string name, string code, string attempt)
    {
        switch (f.Case)
        {
            case AppCase.Detected when (app.State ?? "").StartsWith(NotPresentAsAssigned, StringComparison.OrdinalIgnoreCase):
                // Wanted absent and not there: nothing was removed by this run, and nothing is to do.
                Set(f, AppCase.NotInstalledAsAssigned, AppObservation.NotInstalledAsAssigned, FindingSeverity.Ok, AppNextStep.None,
                    name + ": not installed (as assigned)",
                    "This app is assigned to be removed, and it is not on this device. Nothing to do.", "", "");
                f.Record = "IME record: " + app.State + ".";
                break;
            case AppCase.Detected:
                Set(f, AppCase.NotInstalledAsAssigned, AppObservation.NotInstalledAsAssigned, FindingSeverity.Ok, AppNextStep.None,
                    name + ": removed as assigned",
                    "This app is assigned to be removed, and the Intune Management Extension removed it (" + app.State + "). Nothing to do.", "", "");
                break;
            case AppCase.NothingToDo:
                Set(f, AppCase.NotInstalledAsAssigned, AppObservation.NotInstalledAsAssigned, FindingSeverity.Info, AppNextStep.None,
                    name + ": not installed (as assigned)",
                    "This app is assigned to be removed, and it is not on this device. Nothing to do.", "", "");
                f.Record = "IME record: " + app.State + ".";
                break;
            case AppCase.InProgress:
                Set(f, AppCase.WaitingForRemoval, AppObservation.AwaitingEvidence, FindingSeverity.Info, AppNextStep.Wait,
                    name + ": waiting to be removed",
                    "This app is assigned to be removed and is still on this device; the Intune Management Extension has not removed it yet (" + app.State + ")." + attempt,
                    "Wait; check the apps again later.", "");
                break;
            case AppCase.InstallFailed:
            case AppCase.RetryPlanned:
            case AppCase.UnknownState:
                f.Case = AppCase.RemovalFailed;
                f.Title = name + ": the removal failed";
                f.Text = "Removing this app failed on this device" + code + "." + attempt;
                break;
        }
    }

    /// <summary>
    /// An MSI line-of-business app, as the MDM stack recorded it (EnterpriseDesktopAppManagement). Its ID is
    /// the MSI product code, not the Intune app ID, so it has no Company Portal link, and Retry work apps and settings
    /// is not its step: the MDM stack retries it by itself, as often as Intune set.
    /// </summary>
    private static void DecideMsi(AppFinding f, AppEvidence app, string name, string code)
    {
        var meaning = app.CodeMeaning.Length > 0 ? " " + app.CodeMeaning : "";
        var retries = app.RetryIndex is int index && app.RetryCount is int count && count > 0
            ? " Attempt " + Math.Min(index + 1, count).ToString(CultureInfo.InvariantCulture) + " of " + count.ToString(CultureInfo.InvariantCulture) + "."
            : "";
        var host = app.DownloadHost.Length > 0 ? " (from " + app.DownloadHost + ")" : "";
        var support = app.CodeSupport.Length > 0 ? app.CodeSupport + " " : "";
        switch (app.Verdict)
        {
            case "OK":
                Set(f, AppCase.Detected, AppObservation.Detected, FindingSeverity.Ok, AppNextStep.None,
                    name + ": installed",
                    "Windows Installer finished the install Intune sent (" + app.State + ").", "", "");
                break;
            case "RETRY-HELPS" when app.Download:
            case "IN-PROGRESS" when app.Download:
                Set(f, AppCase.DownloadError, AppObservation.Failed, FindingSeverity.Warning, AppNextStep.ConnectivityTest,
                    name + ": the download did not work",
                    "The MSI file could not be downloaded" + host + code + "." + meaning + retries
                        + (app.Verdict == "IN-PROGRESS" ? " Windows tries the download again by itself." : ""),
                    "Run Check Microsoft connections. When the connection works, Intune sends the app again; check the apps later.",
                    support + "Check the network path to the app's download" + host + ": proxy, firewall, VPN split tunnel.");
                break;
            case "RETRY-HELPS" when app.Code.Equals("0x80070652", StringComparison.OrdinalIgnoreCase):
                Set(f, AppCase.InProgress, AppObservation.AwaitingEvidence, FindingSeverity.Info, AppNextStep.Wait,
                    name + ": another installation was running",
                    "Windows Installer was busy with another installation" + code + "." + meaning + retries,
                    "Wait; Intune sends the app again. Check the apps later.",
                    "");
                break;
            case "RETRY-HELPS":
                Set(f, AppCase.InstallFailed, AppObservation.Failed, FindingSeverity.Warning, AppNextStep.CopyForIt,
                    name + ": the install failed",
                    "Windows Installer could not install it (" + app.State + ")." + meaning + retries,
                    "Copy details for IT and send it to your IT support.",
                    support + "Windows Installer's own log has the reason: the Application event log (MsiInstaller) for this product code, or an install with logging (msiexec /l*v).");
                break;
            case "RESTART-FIRST":
                Set(f, AppCase.RestartNeeded, AppObservation.AwaitingEvidence, FindingSeverity.Warning, AppNextStep.Restart,
                    name + ": waits for a restart",
                    "Windows Installer finished and asks for a restart (" + app.State + ").",
                    "Restart the device, then check the apps again.",
                    "");
                break;
            case "IN-PROGRESS":
            case "PENDING":
                Set(f, AppCase.InProgress, AppObservation.AwaitingEvidence, FindingSeverity.Info, AppNextStep.Wait,
                    name + ": being installed or waiting its turn",
                    "Windows is working on it (" + app.State + ")." + retries,
                    "Wait. Check the apps again later.",
                    "");
                break;
            case "UNKNOWN":
                Set(f, AppCase.UnknownState, AppObservation.Unknown, FindingSeverity.Warning, AppNextStep.CopyForIt,
                    name + ": in a state that is not recognised",
                    "The MDM app management recorded " + app.State + ", which " + ProductInfo.DisplayName + " cannot classify.",
                    "Copy details for IT and send it to your IT support.",
                    support + "Read the app's EnterpriseDesktopAppManagement record and the DeviceManagement-Enterprise-Diagnostics-Provider Admin event log.");
                f.Uncertainty = "The state of this app is not certain.";
                break;
            default:
                Set(f, AppCase.NothingToDo, AppObservation.Unknown, FindingSeverity.Info, AppNextStep.None,
                    name + ": nothing to do on this device",
                    "The record says: " + (string.IsNullOrEmpty(app.State) ? "no status recorded" : app.State) + ".", "", "");
                break;
        }
        // For a handover: the card says the code in words, and IT support gets the record with
        // its code in View details and Copy details for IT, next to the product code (a Win32 app's "IME record").
        if (f.Next == AppNextStep.CopyForIt && !string.IsNullOrEmpty(app.State)) { f.Record = "MSI record: " + app.State + "."; }
    }

    /// <summary>
    /// An MSIX or Appx line-of-business app, as the MDM stack holds its install (EnterpriseModernAppManagement
    /// AppInstallation; the node is gone once the install succeeded). Its ID is the package family name, so it has
    /// no Company Portal link, and Retry work apps and settings is never its step: the IME does not install it, and
    /// Microsoft documents no retry of these installs. A failure's step comes from its AppX deployment code: IT
    /// support for a signature, dependency, policy or package problem; close the app and wait when it was in use;
    /// the connection check for a download failure; free disk space when it is full.
    /// </summary>
    private static void DecideMsix(AppFinding f, AppEvidence app, string name)
    {
        var meaning = app.CodeMeaning.Length > 0 ? " " + ResultCard.Sentence(app.CodeMeaning.Trim()) : "";
        var step = app.CodeStep.Length > 0 ? app.CodeStep : "Copy details for IT and send it to your IT support.";
        switch (app.Verdict)
        {
            case "OK":
                Set(f, AppCase.Detected, AppObservation.Detected, FindingSeverity.Ok, AppNextStep.None,
                    name + ": installed",
                    "Windows finished the install Intune sent (" + app.State + ").", "", "");
                break;
            case "IN-PROGRESS":
                Set(f, AppCase.InProgress, AppObservation.AwaitingEvidence, FindingSeverity.Info, AppNextStep.Wait,
                    name + ": being installed or waiting its turn",
                    "Windows is working on it (" + app.State + ").",
                    "Wait. Check the apps again later.",
                    "");
                break;
            case "RETRY-HELPS":
                switch (app.CodeNext)
                {
                    case "connectivity":
                        Set(f, AppCase.DownloadError, AppObservation.Failed, FindingSeverity.Warning, AppNextStep.ConnectivityTest,
                            name + ": the download did not work",
                            "The package could not be downloaded (" + app.State + ")." + meaning, step, app.CodeSupport);
                        break;
                    case "wait":
                        Set(f, AppCase.InstallFailed, AppObservation.Failed, FindingSeverity.Warning, AppNextStep.Wait,
                            name + ": the install could not finish",
                            "Windows could not install the package (" + app.State + ")." + meaning, step, app.CodeSupport);
                        break;
                    case "none":
                        Set(f, AppCase.InstallFailed, AppObservation.Failed, FindingSeverity.Warning, AppNextStep.None,
                            name + ": the install failed",
                            "Windows could not install the package (" + app.State + ")." + meaning, step, app.CodeSupport);
                        break;
                    default:
                        Set(f, AppCase.InstallFailed, AppObservation.Failed, FindingSeverity.Warning, AppNextStep.CopyForIt,
                            name + ": the install failed",
                            "Windows could not install the package (" + app.State + ")." + meaning,
                            step,
                            app.CodeSupport.Length > 0 ? app.CodeSupport : "The AppXDeployment-Server event log (Microsoft-Windows-AppXDeploymentServer/Operational) has the cause.");
                        break;
                }
                break;
            case "UNKNOWN":
                Set(f, AppCase.UnknownState, AppObservation.Unknown, FindingSeverity.Warning, AppNextStep.CopyForIt,
                    name + ": in a state that is not recognised",
                    "The MDM app management recorded " + app.State + ", which " + ProductInfo.DisplayName + " cannot classify.",
                    "Copy details for IT and send it to your IT support.",
                    "Read the app's EnterpriseModernAppManagement AppInstallation record and the AppXDeployment-Server event log.");
                f.Uncertainty = "The state of this app is not certain.";
                break;
            default:
                Set(f, AppCase.NothingToDo, AppObservation.Unknown, FindingSeverity.Info, AppNextStep.None,
                    name + ": nothing to do on this device",
                    "The record says: " + (string.IsNullOrEmpty(app.State) ? "no status recorded" : app.State) + ".", "", "");
                break;
        }
        // As for an MSI app: a handover gives IT support the record with its code, next to the package family name.
        if (f.Next == AppNextStep.CopyForIt && !string.IsNullOrEmpty(app.State)) { f.Record = "MSIX record: " + app.State + "."; }
    }

    /// <summary>
    /// The standing Company Portal link shows on the card of an app with a page there, in every
    /// state, except while the card's step button is that same action (<paramref name="shownStep"/>).
    /// </summary>
    public static bool ShowPortalLink(AppFinding finding, AppNextStep shownStep)
        => HasPortalPage(finding) && shownStep != AppNextStep.OpenCompanyPortal;

    /// <summary>
    /// The facts of an app's detail card, one short line each: its status (with when it was
    /// recorded), whom it is assigned to, its revision (with the day the device last checked it), its
    /// full app ID and, when the name is not on this device, that. A finding without an app (the
    /// service is missing, "my app is not in this list") has its status only.
    /// </summary>
    public static List<(string Label, string Value)> DetailFacts(AppFinding finding, TimeZoneInfo zone = null)
    {
        var facts = new List<(string Label, string Value)>();
        if (finding?.App == null) { return facts; }
        var app = finding.App;
        var hasApp = !string.IsNullOrEmpty(app.AppId);
        var status = Word(finding);
        if (app.ObservedUtc is DateTime observed) { status += ", recorded " + When(observed, zone); }
        else if (hasApp) { status += ", time of record unknown"; }
        facts.Add(("Status", status));
        if (!hasApp) { return facts; }
        facts.Add(("Assigned to", app.Scope == "user" ? "you" : "this device"));
        // An MSI line-of-business app is known by its version and product code (no revision, no
        // Intune app ID); a Microsoft Store app says its type and keeps the Intune facts.
        if (app.IsMsi)
        {
            facts.Add(("Type", "MSI line-of-business app"));
            if (app.Version.Length > 0) { facts.Add(("Version", app.Version)); }
            facts.Add(("Product code", MsiProductCode(app.AppId)));
        }
        else if (app.IsMsix)
        {
            // An MSIX line-of-business app: its package family name and the version installed now (the
            // install record keeps no version; none is shown when no version is installed yet).
            facts.Add(("Type", "MSIX line-of-business app"));
            facts.Add(("Package family", app.AppId));
            if (app.Version.Length > 0) { facts.Add(("Version installed", app.Version)); }
        }
        else
        {
            if (app.Kind == "store") { facts.Add(("Type", "Microsoft Store app")); }
            if (app.Revision is int r && r >= 0)
            {
                facts.Add(("Revision", r.ToString(CultureInfo.InvariantCulture) + (app.ObservedUtc is DateTime seen ? ", last checked by the device on " + Day(seen, zone) : "")));
            }
            facts.Add(("App ID", app.AppId));
        }
        if (string.IsNullOrWhiteSpace(app.Name)) { facts.Add(("Name", "not known on this device")); }
        return facts;
    }

    /// <summary>An MSI product code as Windows Installer writes it: upper case, in braces.</summary>
    public static string MsiProductCode(string appId) => "{" + (appId ?? "").Trim('{', '}').ToUpperInvariant() + "}";

    /// <summary>The facts of an app's detail card as short lines ("Status: Failed, recorded ...").</summary>
    public static string DetailState(AppFinding finding, TimeZoneInfo zone = null)
        => string.Join("\n", DetailFacts(finding, zone).Select(f => f.Label + ": " + f.Value));

    /// <summary>The Company Portal link's own label; it says what the link shows when the name is not on this device.</summary>
    public static string PortalLinkText(AppFinding finding)
        => finding?.App != null && string.IsNullOrWhiteSpace(finding.App.Name) ? "Open in Company Portal to see it" : "Open in Company Portal";

    /// <summary>For an available app the device has not checked since its last IME check-in.</summary>
    public const string NewerVersionLine = "Intune may have a newer version of this app; the device fetches it when you choose Install in Company Portal.";

    /// <summary>
    /// "Last attempt (an earlier version of this app, 11 Nov): failed, exit code 60008." when
    /// this revision has no attempt of its own and an earlier one failed; with the PSAppDeployToolkit
    /// sentence for a code in its range. '' otherwise.
    /// </summary>
    public static string EarlierAttempt(AppEvidence app, TimeZoneInfo zone = null)
    {
        if (app?.EarlierRevision is not int || app.Enforcement != null) { return ""; }
        var when = app.EarlierUtc is DateTime at ? ", " + Day(at, zone) : "";
        var line = "Last attempt (an earlier version of this app" + when + "): failed" + (string.IsNullOrEmpty(app.EarlierCode) ? "" : ", " + CodeWords(app.EarlierCode)) + ".";
        var psadt = PsadtSentence(app.EarlierCode);
        return psadt.Length == 0 ? line : line + " " + psadt;
    }

    /// <summary>"Last detection run: 11 Nov 10:12, 64-bit, exit code 1, no output."; '' without a run.</summary>
    public static string DetectionFacts(AppEvidence app, TimeZoneInfo zone = null)
    {
        if (app?.DetectUtc is not DateTime at) { return ""; }
        var parts = new List<string> { When(at, zone) };
        if (app.DetectBits is "64" or "32") { parts.Add(app.DetectBits + "-bit"); }
        if (!string.IsNullOrEmpty(app.DetectExit)) { parts.Add("exit code " + app.DetectExit); }
        if (app.DetectOutput == "yes") { parts.Add("with output"); }
        else if (app.DetectOutput == "no") { parts.Add("no output"); }
        return "Last detection run: " + string.Join(", ", parts) + ".";
    }

    /// <summary>
    /// Proof that Intune does not detect an installed app: the newest attempt's install finished
    /// with success, and a detection run of the same revision after it said "not there" (a non-zero
    /// exit code or no output). The IME's own "installed, but not detected" code keeps its verdict.
    /// </summary>
    public static bool ProvenNotDetected(AppEvidence app)
    {
        if (app == null || app.Verdict is "PACKAGING" or "OK" || app.Intent == "uninstall") { return false; }
        if (app.InstallOkUtc is not DateTime installed || app.DetectUtc is not DateTime detected || detected <= installed) { return false; }
        if (app.DetectRevision is not int revision || app.Revision is not int current || revision != current) { return false; }
        return (!string.IsNullOrEmpty(app.DetectExit) && app.DetectExit != "0") || app.DetectOutput == "no";
    }

    /// <summary>
    /// The requirement an IME state names when the app does not apply to this device ("not
    /// applicable: ScriptRequirementRuleNotMet" gives "ScriptRequirementRuleNotMet"); "" when it
    /// names none; null when the state does not say "not applicable".
    /// </summary>
    public static string NotApplicableReason(string state)
    {
        const string prefix = "not applicable";
        if (string.IsNullOrWhiteSpace(state) || !state.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { return null; }
        var rest = state.Trim().Substring(prefix.Length).TrimStart(':', ' ');
        // "not applicable (IME status)" names no requirement.
        return rest.StartsWith("(", StringComparison.Ordinal) ? "" : rest;
    }

    /// <summary>
    /// The plain sentence for a requirement the app does not meet, from the words in its code
    /// (never more than the code says); the generic sentence for any other code.
    /// </summary>
    public static string NotApplicableText(string requirement)
    {
        var r = requirement ?? "";
        bool Has(string word) => r.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        if (Has("Script")) { return "Not meant for this device: the app's requirement script said it does not apply here. Nothing to do."; }
        if (Has("OSVersion") || Has("OperatingSystem") || Has("MinimumOS") || Has("OSBuild")) { return "Not meant for this device: the app needs another Windows version. Nothing to do."; }
        if (Has("Architecture") || Has("Processor") || Has("Platform")) { return "Not meant for this device: the app is for another processor architecture. Nothing to do."; }
        if (Has("DiskSpace") || Has("FreeSpace") || Has("Disk")) { return "Not meant for this device: the app needs more free disk space than its requirement allows here. Nothing to do."; }
        if (Has("Memory") || Has("RAM")) { return "Not meant for this device: the app needs more memory than this device has. Nothing to do."; }
        if (Has("File")) { return "Not meant for this device: the app's file requirement is not met here. Nothing to do."; }
        if (Has("Registry")) { return "Not meant for this device: the app's registry requirement is not met here. Nothing to do."; }
        if (Has("Product") || Has("Msi")) { return "Not meant for this device: the app's product requirement is not met here. Nothing to do."; }
        return "Not meant for this device: one of the app's requirements is not met. Nothing to do.";
    }

    /// <summary>"Your app is not in this list": the user's app has no record here.</summary>
    public static AppFinding NotListed(AppDiagnosis diagnosis)
    {
        var f = Special(AppCase.NotListed, diagnosis?.DiagnosisId ?? "");
        if (diagnosis != null && !diagnosis.UserScope)
        {
            f.Uncertainty = Join(f.Uncertainty, "Only the apps of this device could be read: your own apps cannot be matched to the account you are signed in with.");
        }
        if (diagnosis != null && diagnosis.Listed < diagnosis.Records)
        {
            f.Uncertainty = Join(f.Uncertainty, Words.Count(diagnosis.Records - diagnosis.Listed, "app record") + " are not listed here; a support package lists every app.");
        }
        return f;
    }

    private static AppFinding Special(AppCase which, string diagnosisId, RunOutcome outcome = null)
    {
        var f = new AppFinding { DiagnosisId = diagnosisId, App = new AppEvidence { DiagnosisId = diagnosisId } };
        switch (which)
        {
            case AppCase.NotListed:
                Set(f, which, AppObservation.Unknown, FindingSeverity.Warning, AppNextStep.CopyForIt,
                    "Your app is not in this list",
                    "This device has no record of it, for you or for the device. It may not be assigned to you or to this device, Intune may not have sent it yet, or it is not a Win32 app (a Store or Microsoft 365 app is not listed here).",
                    "Copy details for IT and send it to your IT support, with the name of the app.",
                    "Check the app's assignment for this user and device, and whether the device has checked in since.");
                f.Uncertainty = "Without a record, nothing on this device says why.";
                break;
            case AppCase.ServiceOffline:
                Set(f, which, AppObservation.Unknown, FindingSeverity.Warning, AppNextStep.CopyForIt,
                    "The apps on this device could not be checked",
                    "The " + ProductInfo.DisplayName + " service is not available, so the app state could not be read. Nothing is known about your app from here.",
                    "Restart the device and try again. If it repeats, copy details for IT and send it to your IT support.",
                    "Check that the " + ProductInfo.DisplayName + " service is installed and running.");
                f.Uncertainty = "The state of every app is unknown.";
                break;
            case AppCase.ServiceTooOld:
                Set(f, which, AppObservation.Unknown, FindingSeverity.Warning, AppNextStep.CopyForIt,
                    "The app diagnosis is not available on this device yet",
                    "The " + ProductInfo.DisplayName + " service on this device is an older version without the app diagnosis. The other checks and tools still work.",
                    "Copy details for IT and send it to your IT support.",
                    "Update " + ProductInfo.DisplayName + " on this device: the app diagnosis needs the service of the same version as the app.");
                f.Uncertainty = "The state of every app is unknown.";
                break;
            default:
                Set(f, AppCase.DiagnosisFailed, AppObservation.Unknown, FindingSeverity.Warning, outcome?.Result == ResultCode.Busy || outcome?.Result == ResultCode.RateLimited ? AppNextStep.None : AppNextStep.CopyForIt,
                    "The app diagnosis did not finish",
                    outcome?.Result == ResultCode.Busy
                        ? "Another action is running on this device. Check the apps again when it has finished."
                        : outcome?.Result == ResultCode.RateLimited
                            ? "The apps were checked too often in the last hour. Try again later."
                            : "The app state could not be read completely" + (outcome != null && outcome.Result != ResultCode.Completed ? " (" + RunWords.Service(outcome.Result) + ")" : "") + ".",
                    outcome?.Result == ResultCode.Busy || outcome?.Result == ResultCode.RateLimited ? "Try again later." : "Try again. If it repeats, copy details for IT and send it to your IT support.",
                    "The run log of this reference has the details.");
                f.Uncertainty = "The state of the apps is not known from this run.";
                break;
        }
        return f;
    }

    private static void Set(AppFinding f, AppCase which, AppObservation observation, FindingSeverity severity, AppNextStep next, string title, string text, string nextText, string support)
    {
        f.Case = which;
        f.Observation = observation;
        f.Severity = severity;
        f.Next = next;
        f.Title = title;
        f.Text = text;
        f.NextText = nextText;
        f.Support = support;
    }

    private static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b : a + " " + b;

    /// <summary>A moment in the user's own time, with the zone: "2026-11-10 14:00 (UTC+01:00)".</summary>
    public static string When(DateTime utc, TimeZoneInfo zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        if (!TimeText.TryLocal(utc, zone, out var local)) { return utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"; }
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (UTC" + TimeText.Offset(zone.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc))) + ")";
    }

    /// <summary>The observed state in words (one of four; never the run's state).</summary>
    public static string Word(AppObservation observation) => observation switch
    {
        AppObservation.AwaitingEvidence => "Awaiting evidence",
        AppObservation.Detected => "Detected",
        AppObservation.Failed => "Failed",
        AppObservation.NotApplicable => "Not applicable",
        AppObservation.NotInstalledAsAssigned => "Not installed (as assigned)",
        AppObservation.NoLongerAssigned => NoLongerAssignedDevice,
        _ => "Unknown",
    };

    /// <summary>A finding's observed state in words; a record no longer assigned says to whom.</summary>
    public static string Word(AppFinding finding)
        => finding?.Observation == AppObservation.NoLongerAssigned ? NoLongerAssignedWord(finding.App) : Word(finding?.Observation ?? AppObservation.Unknown);

    /// <summary>The run's state in words, with the time of the reading in the user's zone.</summary>
    public static string RunLine(AppDiagnosis diagnosis, TimeZoneInfo zone = null)
    {
        if (diagnosis == null || diagnosis.Run == DiagnosisRunState.NotRun) { return "Diagnosis: not run yet. Use Reload the app list to read the apps (nothing is changed)."; }
        // No reference in this status line; it stays in View details, the run log and Copy details for IT.
        var at = diagnosis.CapturedUtc.HasValue ? " " + ZoneTime(diagnosis.CapturedUtc.Value, zone) : "";
        return diagnosis.Run switch
        {
            DiagnosisRunState.Running => "Diagnosis: running (read-only).",
            DiagnosisRunState.Finished => "Diagnosis: finished at" + at + "; nothing on this device was changed.",
            _ => "Diagnosis: interrupted" + (at.Length > 0 ? " at" + at : "") + "; the list may be incomplete.",
        };
    }

    /// <summary>
    /// After a Full IME reset, until the IME checked in again: the app list is still being read again, so it
    /// says so instead of counting apps. Empty without a reset, or once the IME checked in after it.
    /// </summary>
    public static string ResetLine(DateTime? resetUtc, DateTime? lastImeCheckInUtc, TimeZoneInfo zone, DateTime nowUtc)
    {
        if (resetUtc is not DateTime reset || (lastImeCheckInUtc is DateTime checkIn && checkIn > reset)) { return ""; }
        var when = ToolRuns.ClockTime(reset, zone, nowUtc);
        return "The app installer was reset " + (when.IndexOf(' ') < 0 ? "at " : "") + when + ". It reads your apps again at its next check-ins, usually within an hour. Check again later.";
    }

    /// <summary>
    /// The run's state as the page shows it, short: "Checked at 14:03" (today), "Checked yesterday 14:03",
    /// or the date and time for an older day. The whole <see cref="RunLine"/> (date, zone, nothing changed) is its tooltip
    /// and automation name. A run that did not finish keeps a line that says so.
    /// </summary>
    public static string ShortRunLine(AppDiagnosis diagnosis, TimeZoneInfo zone, DateTime nowUtc)
    {
        if (diagnosis == null || diagnosis.Run == DiagnosisRunState.NotRun) { return RunLine(diagnosis, zone); }
        var when = diagnosis.CapturedUtc is DateTime utc ? ToolRuns.ClockTime(utc, zone, nowUtc) : "";
        // ClockTime is "14:03" today; another day has its day first ("yesterday 14:03").
        var at = when.Length == 0 ? "" : when.IndexOf(' ') < 0 ? " at " + when : " " + when;
        return diagnosis.Run switch
        {
            DiagnosisRunState.Running => "Checking your apps...",
            DiagnosisRunState.Finished => "Checked" + at,
            _ => "Interrupted" + at + "; the list may be incomplete.",
        };
    }

    /// <summary>"14:03, local time, UTC+01:00 (W. Europe Standard Time)".</summary>
    private static string ZoneTime(DateTime utc, TimeZoneInfo zone)
    {
        zone ??= TimeZoneInfo.Local;
        return TimeText.TryLocal(utc, zone, out var local)
            ? local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (" + TimeText.ZoneLabel(zone, utc) + ")"
            : utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
    }

    /// <summary>The status line after a diagnosis run.</summary>
    public static (string Text, Tone Tone) Status(AppDiagnosis diagnosis)
    {
        if (diagnosis.Problem != null) { return ("App diagnosis: " + diagnosis.Problem.Title + ".", Tone.Warning); }
        if (diagnosis.Run == DiagnosisRunState.Running) { return ("Checking the apps on this device (read-only)...", Tone.Neutral); }
        var attention = diagnosis.Attention.Count();
        if (attention == 0)
        {
            return (diagnosis.Present ? "App diagnosis finished: no app needs attention. Nothing was changed." : "App diagnosis finished: this device has no Win32 app state yet. Nothing was changed.", Tone.Success);
        }
        return ("App diagnosis finished: " + Words.Count(attention, "app") + (attention == 1 ? " needs" : " need") + " attention. Nothing was changed.", Tone.Warning);
    }
}
