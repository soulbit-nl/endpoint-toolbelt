using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.WindowsUpdate;

/// <summary>What the update policies on this device say about the source and the timing of quality updates.</summary>
public sealed class WuPolicyState
{
    /// <summary>Quality updates come from an update server of the organization (WSUS).</summary>
    public bool Wsus { get; set; }

    /// <summary>DoNotConnectToWindowsUpdateInternetLocations=1 (Group Policy or the Update CSP).</summary>
    public bool NoInternet { get; set; }

    /// <summary>DisableWindowsUpdateAccess=1: Windows Update cancels every call.</summary>
    public bool AccessOff { get; set; }

    /// <summary>Automatic updates are off by policy (NoAutoUpdate=1, or the Update CSP's AllowAutoUpdate=5).</summary>
    public bool AutoOff { get; set; }

    /// <summary>Quality updates are paused by a policy (Group Policy or the Update CSP).</summary>
    public bool PausedByPolicy { get; set; }

    /// <summary>Quality updates are paused in Settings on this device, until <see cref="PausedUntilUtc"/> when known.</summary>
    public bool PausedByUser { get; set; }

    public DateTime? PausedUntilUtc { get; set; }

    /// <summary>The quality update deferral a policy sets, in days; 0 when none.</summary>
    public int DeferralDays { get; set; }

    /// <summary>Where the deferral comes from: "mdm" (the Update CSP, as an Intune update ring sets it), "group-policy", or "" when none.</summary>
    public string DeferralSource { get; set; } = "";

    /// <summary>A policy stops this device from getting quality updates at all, until it is changed.</summary>
    public bool Blocks => AccessOff || PausedByPolicy || (NoInternet && !Wsus);

    /// <summary>The tokens of the record and the run log line ("wsus", "paused-policy", ...), in a fixed order; none gives "none".</summary>
    public string Tokens()
    {
        var tokens = new List<string>();
        if (Wsus) { tokens.Add("wsus"); }
        if (NoInternet) { tokens.Add("no-internet"); }
        if (AccessOff) { tokens.Add("access-off"); }
        if (AutoOff) { tokens.Add("auto-off"); }
        if (PausedByPolicy) { tokens.Add("paused-policy"); }
        if (PausedByUser) { tokens.Add("paused-user"); }
        if (DeferralDays > 0) { tokens.Add("deferred-" + DeferralDays.ToString(CultureInfo.InvariantCulture)); }
        return tokens.Count == 0 ? "none" : string.Join(",", tokens);
    }
}

/// <summary>
/// Check Windows Update: a read-only check for "Windows won't update". It reads what the
/// repair's diagnosis reads, in seconds: the last successful quality update and how long ago, the
/// update failures logged since then (with the shared code table's plain words), what the update
/// policies say about the source and the timing, the update services' start types, a pending
/// restart and the free space on the system drive. Each finding has one next step, for the user or
/// for IT; Repair Windows Update is the next step only where it can help (never when a policy
/// blocks, a service is disabled or a restart is pending). It searches for no update, starts no
/// worker, changes no service, setting or file and writes no cooldown; its result closes with one
/// <c>wu-check</c> record that names no server, path, user or device.
/// </summary>
public sealed class WindowsUpdateCheck
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal const string GroupPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    internal const string AutoUpdateKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
    internal const string MdmKey = @"SOFTWARE\Microsoft\PolicyManager\current\device\Update";
    internal const string PolicyStateKey = @"SOFTWARE\Microsoft\WindowsUpdate\UpdatePolicy\Settings";
    internal const string UserSettingsKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    /// <summary>The most failure codes the check names one by one; the rest is counted.</summary>
    public const int MaxCodes = 4;

    /// <summary>The next step that sends the user to the repair.</summary>
    public const string RepairStep = "Run Repair Windows Update. It runs only the repair that fits what it finds.";

    public const string ItStep = "Contact your IT support.";

    public WindowsUpdateCheck(RunContext run, WindowsUpdateRepair reads = null)
    {
        Run = run ?? throw new ArgumentNullException(nameof(run));
        Reads = reads ?? new WindowsUpdateRepair(run);
    }

    public RunContext Run { get; }

    /// <summary>The repair's read-only steps, reused (tests replace them).</summary>
    public WindowsUpdateRepair Reads { get; }

    private EngineServices Services => Run.Services;

    public int Check()
    {
        var config = Run.Config;
        Run.StepHeader(1, 1, "Check Windows Update (read-only)");

        // --- What it reads -------------------------------------------------------------------------
        var currency = Reads.Currency(config.WUDeploymentDelayDays);
        var since = currency.LastSuccessUtc ?? Services.Clock.UtcNow.AddDays(-90);
        int? daysAgo = currency.LastSuccessUtc == null ? null : Math.Max(0, (int)Math.Floor((Services.Clock.UtcNow - currency.LastSuccessUtc.Value).TotalDays));
        Run.Log(currency.LastPatchMonth == null
            ? "Last quality update: none found in the update history or the hotfix list."
            : string.Format(Invariant, "Last quality update: {0}, {1} ago (from the {2}); expected by now: {3}.",
                currency.LastPatchMonth.Value.ToString("yyyy-MM", Invariant), daysAgo == null ? "an unknown time" : TextFormat.Count(daysAgo.Value, "day"), currency.Source,
                currency.ExpectedMonth.ToString("yyyy-MM", Invariant)));

        var failed = FailedAttempts(since);
        var found = Reads.FailureCodes(currency.LastSuccessUtc);
        var codes = Codes(found, failed);
        Run.Log(codes.Count == 0 ? "Update failures since then: none." : "Update failures since then: " + string.Join(", ", codes.Select(CodeKnowledge.FormatHex)) + ".",
            codes.Count == 0 ? LogSeverity.Info : LogSeverity.Warning);
        if (found.Unconfirmed.Count > 0)
        {
            Run.Log("Setup log codes not confirmed by the update client or history (not used): " + string.Join(", ", found.Unconfirmed) + ".");
        }

        var policy = Policy();
        Run.Log("Update policy: " + PolicyText(policy) + ".");

        var services = ServiceStates();
        var disabled = services.Where(s => s.Value == "Disabled" || s.Value == "missing").Select(s => s.Key).ToList();
        Run.PersistentLog("Update services: " + string.Join(", ", services.Select(s => s.Key + "=" + s.Value)) + ".");
        Run.Log(disabled.Count == 0 ? "Update services: none disabled." : "Update services: " + TextFormat.Count(disabled.Count, "service") + " disabled or missing.",
            disabled.Count == 0 ? LogSeverity.Info : LogSeverity.Warning);

        var restart = Reads.PendingRestart();
        Run.Log(restart ? "Restart: Windows is waiting for one." : "Restart: none pending.");

        var freeGB = Reads.SystemDriveFreeGB();
        var lowSpace = freeGB != null && freeGB.Value < config.WUMinFreeGB;
        Run.Log(freeGB == null ? "Free space on the system drive: unknown." : string.Format(Invariant, "Free space on the system drive: {0} GB.", TextFormat.Number(freeGB.Value, "0.#")));

        // --- What it means -------------------------------------------------------------------------
        var policyCodes = codes.Where(c => WuCodes.PolicyCodes.Contains(c)).ToList();
        var blocked = policy.Blocks || policyCodes.Count > 0;
        // The repair helps a device that is behind (with or without failures), but not past a
        // policy, a disabled service or a pending restart (it would refuse that run itself).
        var repair = !currency.IsCurrent && !blocked && disabled.Count == 0 && !restart;
        var step = MainStep(blocked, disabled.Count > 0, restart, lowSpace, repair);

        // Problems first, so the status and Copy details for IT name the one that decides.
        AddPolicyFindings(policy, policyCodes, currency);
        foreach (var service in disabled)
        {
            var label = WuCodes.RequiredServices.First(s => s.Key == service).Value;
            var missing = services.First(s => s.Key == service).Value == "missing";
            Run.AddFinding(FindingLevel.Error, "WUCHECK-SERVICE-" + service, string.Format(Invariant, "{0} is {1}", label, missing ? "missing" : "disabled"),
                "Windows Update cannot work without it (0x80070422). Its start type is set by a policy or a tool, so setting it back on this device would be undone.",
                ItStep, "Find the policy or tool that disables it (" + service + ") and set it back to its default start type.");
        }
        if (restart)
        {
            Run.AddFinding(FindingLevel.Warning, "WUCHECK-RESTART", "Windows is waiting for a restart",
                "An update is installed up to the restart. Windows installs no further update until then.",
                "Restart the device when you can, then check again.");
        }
        if (lowSpace)
        {
            Run.AddFinding(FindingLevel.Warning, "WUCHECK-LOW-SPACE", string.Format(Invariant, "Only {0} GB free on the system drive", TextFormat.Number(freeGB.Value, "0.#")),
                string.Format(Invariant, "Updates need room to download and install; less than {0} GB is often too little.", config.WUMinFreeGB),
                "Run Free up disk space, or remove files you no longer need, then check again.");
        }
        AddCurrencyFinding(currency, daysAgo, step);
        AddCodeFindings(codes, failed, currency.IsCurrent, step);
        if (freeGB == null)
        {
            Run.AddFinding(FindingLevel.Info, "WUCHECK-SPACE-UNKNOWN", "The free space on the system drive could not be read", "", "");
        }

        var verdict = Verdict(currency, codes.Count, blocked, disabled.Count > 0, restart, lowSpace);
        Run.WriteRecord("wu-check",
            RunContext.Field("verdict", verdict),
            RunContext.Field("lastmonth", currency.LastPatchMonth?.ToString("yyyy-MM", Invariant) ?? ""),
            RunContext.Field("days", daysAgo.HasValue ? (object)daysAgo.Value : null),
            RunContext.Field("behind", currency.MonthsBehind.HasValue ? (object)Math.Max(0, currency.MonthsBehind.Value) : null),
            RunContext.Field("codes", string.Join(" ", codes.Take(MaxCodes))),
            RunContext.Field("failed", failed.Count),
            RunContext.Field("policy", policy.Tokens()),
            RunContext.Field("disabled", disabled.Count),
            RunContext.Field("restart", restart),
            RunContext.Field("freegb", freeGB.HasValue ? (object)Math.Floor(freeGB.Value) : null),
            RunContext.Field("lowspace", lowSpace),
            RunContext.Field("repair", repair));
        Run.PersistentLog(string.Format(Invariant, "EndpointToolbelt:WU-CHECK verdict={0} policy={1} codes={2} restart={3} repair={4}",
            verdict, policy.Tokens(), codes.Count == 0 ? "none" : string.Join(",", codes), restart ? "yes" : "no", repair ? "suggested" : "not-suggested"));
        // A current device can still have a point to check (a pause in Settings, automatic updates off).
        var open = Run.Findings.Any(f => f.Level == FindingLevel.Error || f.Level == FindingLevel.Warning);
        return verdict == "up-to-date" && !open ? EngineExitCode.Success : EngineExitCode.Warnings;
    }

    /// <summary>The one result word of the check, worst first.</summary>
    internal static string Verdict(WuCurrency currency, int codes, bool blocked, bool servicesDisabled, bool restart, bool lowSpace)
    {
        if (blocked) { return "blocked-by-policy"; }
        if (servicesDisabled) { return "services-disabled"; }
        if (restart) { return "restart-pending"; }
        if (lowSpace) { return "low-space"; }
        if (currency.LastPatchMonth == null) { return "no-history"; }
        if (!currency.IsCurrent) { return codes > 0 ? "failing" : "behind"; }
        return "up-to-date";
    }

    /// <summary>The next step of a device that is behind or failing: the first thing that stands in the way.</summary>
    internal static string MainStep(bool blocked, bool servicesDisabled, bool restart, bool lowSpace, bool repair)
    {
        if (blocked || servicesDisabled) { return ItStep + " A policy or a disabled service on this device decides this; see the other findings."; }
        if (restart) { return "Restart the device first, then check again."; }
        if (lowSpace) { return "Free up disk space first, then check again."; }
        return repair ? RepairStep : ItStep;
    }

    private void AddCurrencyFinding(WuCurrency currency, int? daysAgo, string step)
    {
        var ago = daysAgo == null ? "" : ", " + (daysAgo.Value == 0 ? "today" : TextFormat.Count(daysAgo.Value, "day") + " ago");
        if (currency.LastPatchMonth == null)
        {
            Run.AddFinding(FindingLevel.Warning, "WUCHECK-NO-HISTORY", "No successful quality update found",
                "Neither the update history nor the hotfix list shows a quality update, so the patch level of this device is unknown.", step);
            return;
        }
        var last = string.Format(Invariant, "Last successful quality update: {0} ({1}{2}).", currency.LastPatchMonth.Value.ToString("yyyy-MM", Invariant), currency.LastTitle, ago);
        if (currency.IsCurrent)
        {
            // The installed patch month: it can be newer than the expected one (a deployment delay).
            Run.AddFinding(FindingLevel.Ok, "WUCHECK-CURRENT", string.Format(Invariant, "Windows is up to date (patch month {0})", (currency.LastPatchMonth ?? currency.ExpectedMonth).ToString("yyyy-MM", Invariant)),
                last, "Nothing to do.");
            return;
        }
        var months = currency.MonthsBehind == null ? "an unknown number of months" : TextFormat.Count(Math.Max(1, currency.MonthsBehind.Value), "month");
        Run.AddFinding(FindingLevel.Warning, "WUCHECK-BEHIND", string.Format(Invariant, "Windows is {0} behind on quality updates", months),
            last + string.Format(Invariant, " Expected by now: {0}.", currency.ExpectedMonth.ToString("yyyy-MM", Invariant)), step);
    }

    private void AddCodeFindings(IReadOnlyList<string> codes, IReadOnlyList<WuHistoryEntry> failed, bool current, string step)
    {
        var attempts = failed.Count == 0 ? "" : string.Format(Invariant, " The update history shows {0} since the last successful quality update.", TextFormat.Count(failed.Count, "failed install"));
        var shown = 0;
        foreach (var code in codes)
        {
            if (shown >= MaxCodes) { break; }
            shown++;
            var knowledge = CodeKnowledge.Get(code);
            var hex = CodeKnowledge.FormatHex(code);
            var title = knowledge == null ? string.Format(Invariant, "Update failure {0}: not a recognised code", hex) : string.Format(Invariant, "Update failure {0}: {1}", hex, knowledge.Name);
            var meaning = knowledge == null ? "No documented meaning was found for it." : "Meaning: " + knowledge.Meaning;
            var isPolicy = WuCodes.PolicyCodes.Contains(code);
            // A failure on a device that is current is history; it decides nothing now.
            if (current)
            {
                Run.AddFinding(FindingLevel.Info, "WUCHECK-CODE-" + code, title, "Logged after the last successful quality update, and this device is still up to date. " + meaning,
                    "Nothing to do now. Check again if Windows Update keeps showing an error.");
                continue;
            }
            var next = isPolicy ? ItStep : step;
            var support = isPolicy ? WuCodes.PolicyNames[code] + ". " + (knowledge?.Support ?? "") : knowledge?.Support ?? "";
            Run.AddFinding(FindingLevel.Warning, "WUCHECK-CODE-" + code, title, "Logged since the last successful quality update. " + meaning + attempts, next, support.Trim());
            attempts = "";
        }
        if (codes.Count > shown)
        {
            Run.AddFinding(FindingLevel.Info, "WUCHECK-CODES-MORE", TextFormat.Count(codes.Count - shown, "more failure code") + " in the log", "", "");
        }
    }

    private void AddPolicyFindings(WuPolicyState policy, IReadOnlyList<string> policyCodes, WuCurrency currency)
    {
        if (policy.AccessOff)
        {
            Run.AddFinding(FindingLevel.Error, "WUCHECK-ACCESS-OFF", "A policy switches off access to Windows Update",
                "\"Remove access to use all Windows Update features\" (DisableWindowsUpdateAccess) is set. Windows Update cancels every call, so no repair on this device helps.",
                ItStep, "Find and remove the policy that sets DisableWindowsUpdateAccess.");
        }
        if (policy.PausedByPolicy)
        {
            Run.AddFinding(FindingLevel.Error, "WUCHECK-PAUSED-POLICY", "Quality updates are paused by a policy",
                "Your organization paused quality updates for this device. That is a decision in the update management, not a device fault.",
                ItStep, "Resume the quality updates in the update management (update ring or Autopatch). If the pause stays on the device after it was lifted there, report that.");
        }
        if (policy.NoInternet && !policy.Wsus)
        {
            Run.AddFinding(FindingLevel.Error, "WUCHECK-NO-SOURCE", "A policy forbids Windows Update on the internet, and no update server is set",
                "\"Do not connect to any Windows Update Internet locations\" is on without an update server of the organization, so this device has no place to get updates from.",
                ItStep, "Set an update server for this device, or turn off DoNotConnectToWindowsUpdateInternetLocations.");
        }
        if (policyCodes.Count > 0)
        {
            var named = string.Join("; ", policyCodes.Select(c => CodeKnowledge.FormatHex(c) + ": " + WuCodes.PolicyNames[c]));
            Run.AddFinding(FindingLevel.Warning, "WUCHECK-POLICY-CODE", "Windows Update reported that a policy blocks it",
                "Logged since the last successful quality update: " + named + ". A policy decides this, so no repair on this device gets past it.",
                ItStep, "Check the policy named above in the device management (Intune or Group Policy).");
        }
        if (policy.AutoOff)
        {
            Run.AddFinding(FindingLevel.Warning, "WUCHECK-AUTO-OFF", "Automatic updates are switched off by a policy",
                "The device does not install updates on its own, so it falls behind unless someone installs them.",
                ItStep, "Find and remove the policy that switches off automatic updates (NoAutoUpdate, or the Update CSP's AllowAutoUpdate).");
        }
        if (policy.PausedByUser)
        {
            var until = policy.PausedUntilUtc.HasValue ? " until " + RunContext.UserTime(policy.PausedUntilUtc.Value, "yyyy-MM-dd") : "";
            Run.AddFinding(FindingLevel.Warning, "WUCHECK-PAUSED-USER", "Updates are paused in Settings" + until,
                "Someone on this device paused the updates in Settings > Windows Update. No quality update installs while they are paused.",
                "Open Settings > Windows Update and select Resume updates.");
        }
        if (policy.Wsus)
        {
            Run.AddFinding(FindingLevel.Info, "WUCHECK-WSUS", "This device gets its quality updates from an update server of your organization",
                "A policy points it at an update server (WSUS). Windows Update for Business then offers it no quality updates; that server decides what arrives and when.",
                "Nothing to do yourself.", "If updates do not arrive, check what the update server offers this device.");
        }
        if (policy.DeferralDays > 0) { AddDeferralFinding(policy, currency); }
    }

    /// <summary>
    /// The deferral: who sets it (the device management or Group Policy), and, when the newest
    /// quality update is released but not yet offered because of it, which one and from what day.
    /// The user cannot lift it; IT support can expedite an update without changing the deferral.
    /// </summary>
    private void AddDeferralFinding(WuPolicyState policy, WuCurrency currency)
    {
        var days = TextFormat.Count(policy.DeferralDays, "day");
        var mdm = policy.DeferralSource == "mdm";
        var setBy = mdm ? "your organization's device management (Intune or similar)" : "a Group Policy of your organization";
        var support = mdm
            ? string.Format(Invariant, "Quality update deferral: {0}, from the Update CSP (DeferQualityUpdatesPeriodInDays under HKLM\\{1}), as an Intune update ring sets it. To deliver one update sooner without changing the ring, expedite it (Intune: Windows quality updates, Expedite).", days, MdmKey)
            : string.Format(Invariant, "Quality update deferral: {0}, from Group Policy (DeferQualityUpdates and DeferQualityUpdatesPeriodInDays under HKLM\\{1}).", days, GroupPolicyKey);

        // The newest release: this month's Patch Tuesday once it has passed, else last month's.
        var today = Services.Clock.Now.DateTime.Date;
        var release = WindowsUpdateRepair.PatchTuesday(today.Year, today.Month);
        if (today < release)
        {
            var previous = today.AddMonths(-1);
            release = WindowsUpdateRepair.PatchTuesday(previous.Year, previous.Month);
        }
        var newest = new DateTime(release.Year, release.Month, 1);
        var offered = release.AddDays(policy.DeferralDays);
        var missing = currency?.LastPatchMonth == null || currency.LastPatchMonth.Value < newest;
        if (missing && today <= offered)
        {
            var month = newest.ToString("yyyy-MM", Invariant);
            var from = today == offered ? "today" : offered.ToString("yyyy-MM-dd", Invariant);
            Run.AddFinding(FindingLevel.Info, "WUCHECK-DEFERRED",
                string.Format(Invariant, "The {0} update is offered to this device from {1}", month, from),
                string.Format(Invariant, "It was released on {0}. {1} defers each quality update by {2} after its release, so this device gets it from {3}. That is a choice of your organization, not a fault, and this device cannot lift it.",
                    release.ToString("yyyy-MM-dd", Invariant), Capital(setBy), days, from),
                mdm ? "If you need it sooner, ask IT support: they can send this one update sooner." : "If you need it sooner, ask IT support.",
                support);
            return;
        }
        Run.AddFinding(FindingLevel.Info, "WUCHECK-DEFERRED", string.Format(Invariant, "A policy defers quality updates by {0}", days),
            string.Format(Invariant, "Each quality update arrives {0} after its release. That is set by {1}, as is normal in an update ring.", days, setBy),
            "Nothing to do yourself.", support);
    }

    private static string Capital(string text) => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

    /// <summary>Quality update install attempts in the update history that failed or were aborted after <paramref name="since"/>.</summary>
    internal IReadOnlyList<WuHistoryEntry> FailedAttempts(DateTime since)
    {
        try
        {
            return Reads.HistoryEntries(200)
                .Where(e => (e.ResultCode == 4 || e.ResultCode == 5) && e.DateUtc > since && WindowsUpdateRepair.IsQualityTitle(e.Title))
                .OrderByDescending(e => e.DateUtc).ToList();
        }
        catch (Exception ex)
        {
            Run.Log("The update history could not be read for failed installs: " + ex.Message, LogSeverity.Warning);
            return Array.Empty<WuHistoryEntry>();
        }
    }

    /// <summary>
    /// The failure codes in one list: the recognised ones first, then those on an event or history
    /// entry that names a Windows update. Codes healthy devices log, and 0, are left out.
    /// </summary>
    internal static List<string> Codes(WuFailureCodes found, IEnumerable<WuHistoryEntry> failed)
    {
        var fromHistory = failed.Select(e => CodeKnowledge.Key(e.HResult.ToString(Invariant))).Where(c => c != null && c != "0x00000000" && !WuCodes.NoiseCodes.Contains(c)).ToList();
        return found.Known
            .Concat(fromHistory.Where(c => WuCodes.KnownCodes.Contains(c)))
            .Concat(found.Unknown)
            .Concat(fromHistory.Where(c => !WuCodes.KnownCodes.Contains(c)))
            .Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>What Group Policy, the Update CSP and the device's own settings say about where and when quality updates come from. Read-only.</summary>
    public WuPolicyState Policy()
    {
        var gp = Reads.RegistryValues(GroupPolicyKey);
        var au = Reads.RegistryValues(AutoUpdateKey);
        var mdm = Reads.RegistryValues(MdmKey);
        var state = Reads.RegistryValues(PolicyStateKey);
        var user = Reads.RegistryValues(UserSettingsKey);
        var policy = new WuPolicyState();

        var defaultService = Reads.DefaultService();
        var defaultIsWsus = defaultService != null && defaultService.Item1 == WuCodes.WsusServiceId;
        var serverSet = WindowsUpdateRepair.RegistryInt(au, "UseWUServer") == 1 || defaultIsWsus;
        // A WSUS address with quality updates pointed back at Windows Update is a valid mix.
        var qualityFromWu = !defaultIsWsus && new[] { gp, mdm }.Any(s => WindowsUpdateRepair.RegistryInt(s, "SetPolicyDrivenUpdateSourceForQualityUpdates") == 0);
        policy.Wsus = serverSet && !qualityFromWu;
        policy.NoInternet = new[] { gp, mdm }.Any(s => WindowsUpdateRepair.RegistryInt(s, "DoNotConnectToWindowsUpdateInternetLocations") == 1);
        policy.AccessOff = WindowsUpdateRepair.RegistryInt(gp, "DisableWindowsUpdateAccess") == 1;
        policy.AutoOff = WindowsUpdateRepair.RegistryInt(au, "NoAutoUpdate") == 1 || WindowsUpdateRepair.RegistryInt(mdm, "AllowAutoUpdate") == 5;

        // A pause set by policy: the Update CSP's PauseQualityUpdates, or Group Policy's start time.
        policy.PausedByPolicy = WindowsUpdateRepair.RegistryInt(mdm, "PauseQualityUpdates") == 1
            || (WindowsUpdateRepair.RegistryInt(gp, "PauseQualityUpdates") == 1)
            || (gp.TryGetValue("PauseQualityUpdatesStartTime", out var gpStart) && (MachineRegistry.AsText(gpStart) ?? "").Trim().Length > 0);
        if (!policy.PausedByPolicy)
        {
            // A pause from Settings: an expiry time still ahead, or the start time of a quality pause.
            var until = UtcTime(user, "PauseUpdatesExpiryTime") ?? UtcTime(user, "PauseQualityUpdatesEndTime");
            var now = Services.Clock.UtcNow;
            if (until != null && until.Value > now)
            {
                policy.PausedByUser = true;
                policy.PausedUntilUtc = until;
            }
            else if (until == null && WindowsUpdateRepair.RegistryInt(state, "PausedQualityStatus") == 1)
            {
                // Paused, but neither a policy nor Settings says by whom: IT finds out.
                policy.PausedByPolicy = true;
            }
        }

        var gpDeferral = WindowsUpdateRepair.RegistryInt(gp, "DeferQualityUpdates") == 1 ? WindowsUpdateRepair.RegistryInt(gp, "DeferQualityUpdatesPeriodInDays") ?? 0 : 0;
        var mdmDeferral = WindowsUpdateRepair.RegistryInt(mdm, "DeferQualityUpdatesPeriodInDays") ?? 0;
        policy.DeferralDays = Math.Min(Math.Max(Math.Max(gpDeferral, mdmDeferral), 0), 365);
        // The source that sets the deferral in force; the device management when both set the same.
        policy.DeferralSource = policy.DeferralDays == 0 ? "" : mdmDeferral >= gpDeferral ? "mdm" : "group-policy";
        return policy;
    }

    private static DateTime? UtcTime(IDictionary<string, object> values, string name)
    {
        if (!values.TryGetValue(name, out var value)) { return null; }
        var text = (MachineRegistry.AsText(value) ?? "").Trim();
        if (text.Length == 0) { return null; }
        return DateTime.TryParse(text, Invariant, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc) && utc.Year >= 2000 ? utc : null;
    }

    internal static string PolicyText(WuPolicyState policy)
    {
        var parts = new List<string>();
        if (policy.Wsus) { parts.Add("an update server of the organization (WSUS)"); }
        if (policy.NoInternet) { parts.Add("no Windows Update on the internet"); }
        if (policy.AccessOff) { parts.Add("access to Windows Update switched off"); }
        if (policy.AutoOff) { parts.Add("automatic updates off"); }
        if (policy.PausedByPolicy) { parts.Add("quality updates paused by policy"); }
        if (policy.PausedByUser) { parts.Add("updates paused in Settings"); }
        if (policy.DeferralDays > 0) { parts.Add("quality updates deferred by " + TextFormat.Count(policy.DeferralDays, "day") + (policy.DeferralSource == "mdm" ? " (device management)" : " (Group Policy)")); }
        return parts.Count == 0 ? "no source, pause or deferral set" : string.Join(", ", parts);
    }

    /// <summary>The start type of each service Windows Update needs, or "missing". Read-only: start types belong to policy.</summary>
    internal List<KeyValuePair<string, string>> ServiceStates()
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var service in WuCodes.RequiredServices)
        {
            string startType;
            try { startType = Services.ServiceControl.StartType(service.Key); }
            catch (Exception) { startType = "missing"; }
            list.Add(new KeyValuePair<string, string>(service.Key, startType));
        }
        return list;
    }
}
