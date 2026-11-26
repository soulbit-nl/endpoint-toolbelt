using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.Defender;

/// <summary>Whose event it is, for the caller who asked.</summary>
public enum DefenderEventScope
{
    /// <summary>Windows itself or a service (SYSTEM, LOCAL SERVICE, NETWORK SERVICE).</summary>
    Device,

    /// <summary>The user who asked for the check.</summary>
    Caller,

    /// <summary>Another user of the device, an account Windows could not resolve, or an event that names no user: counted, never shown.</summary>
    Other,
}

/// <summary>Which Defender protection wrote an event.</summary>
public enum DefenderFamily
{
    /// <summary>A threat detection (1116).</summary>
    Threat,

    /// <summary>An attack surface reduction rule (1121, 1122).</summary>
    Rule,

    /// <summary>Controlled folder access: a protected folder (1123, 1124) or a disk's sectors (1127, 1128).</summary>
    Folder,

    /// <summary>Network protection: a connection (1126, 1125).</summary>
    Network,
}

/// <summary>Events of one protection, one kind and one subject (the program, app or destination that was stopped), for the device or for the caller.</summary>
public sealed class DefenderGroup
{
    public DefenderFamily Family { get; set; }

    /// <summary>"detection", "block" or "audit".</summary>
    public string Kind { get; set; } = "";

    /// <summary>The rule's ID (lower case) for an attack surface reduction event; "sector" for a controlled folder access sector event; empty otherwise.</summary>
    public string RuleId { get; set; } = "";

    /// <summary>The rule's name, the threat's name, or the destination of a network protection event.</summary>
    public string Name { get; set; } = "";

    /// <summary>The severity Defender gave a detection; empty otherwise.</summary>
    public string Severity { get; set; } = "";

    public DefenderEventScope Scope { get; set; }

    /// <summary>The process of the newest event, cleaned; empty when the event names none.</summary>
    public string Process { get; set; } = "";

    /// <summary>The file or target of the newest event (for controlled folder access, the protected folder), cleaned; empty when the event names none.</summary>
    public string Path { get; set; } = "";

    /// <summary>The different targets (or, for a group by target, processes) of the group's events, cleaned, compared without case.</summary>
    public HashSet<string> Others { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The detection IDs of a detection group's events, newest first.</summary>
    public List<string> DetectionIds { get; } = new();

    /// <summary>What Defender did with the group's newest detection (its action event, cleaned); empty when the log has none for it.</summary>
    public string Action { get; set; } = "";

    /// <summary>The newest detection's action failed (1118, or 1119 for a critical failure).</summary>
    public bool ActionFailed { get; set; }

    /// <summary>What a detection found when it is no file, as a fixed word ("a command line"); empty for a file.</summary>
    public string Resource { get; set; } = "";

    public int Count { get; set; }
    public DateTime NewestUtc { get; set; }
}

/// <summary>The profile folders a path is compared with: the caller's own, the folder they are in, and every other profile of the device.</summary>
public sealed class ProfilePaths
{
    /// <summary>The caller's profile folder without a trailing backslash; empty when unknown.</summary>
    public string Caller { get; set; } = "";

    /// <summary>The folder the profiles are in ("C:\Users").</summary>
    public string Root { get; set; } = "";

    /// <summary>The profile folders of the device's other accounts, without a trailing backslash.</summary>
    public List<string> Others { get; } = new();
}

/// <summary>
/// Check Defender blocks: a read-only check for "something stopped working and nobody knows why".
/// It reads the Microsoft Defender operational log of the last 14 days for threat detections
/// (1116, with what Defender did from 1117), attack surface reduction rules (1121 block, 1122 audit), controlled folder access
/// (1123 block, 1124 audit; 1127 and 1128 for a disk's sectors) and network protection (1126
/// block, 1125 audit). Events are grouped per protection and per thing that was stopped (the
/// program a rule kept from running, the app kept out of a protected folder, the destination of a
/// connection), with a count and the newest time, so a rule that fires fifty times is one finding.
/// The caller sees the device's events and their own; events of other users and events that name
/// no user are counted and never shown, a path below another profile is not written, a
/// destination is reduced to its host name, and only the data values the check uses are read
/// (never a command line). It also reads whether Microsoft Defender Antivirus is the active
/// antivirus, so a passive Defender is named instead of "nothing found", and a few health facts of
/// an active Defender (the last quick and full scan, protection switched off, a change tamper
/// protection blocked, a failed feature or update; see DefenderHealth.cs). It changes nothing and
/// writes no cooldown; its result closes with one <c>defender-check</c> record of counts.
/// </summary>
public sealed partial class DefenderBlocksCheck
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public const int Detection = 1116;

    /// <summary>The action Defender took on a detection; it is matched to its 1116 by the detection ID and never counted on its own.</summary>
    public const int ActionTaken = 1117;

    /// <summary>The action on a detection failed (1118) or failed critically (1119); matched as 1117 is.</summary>
    public const int ActionFailed = 1118;
    public const int ActionCritical = 1119;
    public const int RuleBlock = 1121;
    public const int RuleAudit = 1122;
    public const int FolderBlock = 1123;
    public const int FolderAudit = 1124;
    public const int NetworkAudit = 1125;
    public const int NetworkBlock = 1126;
    public const int SectorBlock = 1127;
    public const int SectorAudit = 1128;

    /// <summary>Every event the check reads.</summary>
    public static readonly IReadOnlyList<int> EventIds = new[] { Detection, ActionTaken, ActionFailed, ActionCritical, RuleBlock, RuleAudit, FolderBlock, FolderAudit, NetworkAudit, NetworkBlock, SectorBlock, SectorAudit };

    /// <summary>How far back the check reads.</summary>
    public const int Days = 14;

    /// <summary>The most events it reads; a log with more says so.</summary>
    public const int MaxEvents = 2000;

    /// <summary>The most groups it names one by one; the rest are counted.</summary>
    public const int MaxGroups = 10;

    /// <summary>A longer path or name is cut.</summary>
    public const int MaxTextLength = 260;

    public const string OtherProfile = "(a file in another user's profile)";

    /// <summary>What every "nothing found" says about what this check does not read.</summary>
    public const string NotRead = "SmartScreen in Microsoft Edge and the firewall are not part of this check.";

    internal const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    private static readonly string[] SystemSids = { "S-1-5-18", "S-1-5-19", "S-1-5-20" };

    // Profile folders that belong to no person.
    private static readonly string[] SharedProfiles = { "Public", "Default", "Default User", "All Users" };

    // Defender in passive mode, in EDR block mode, or side by side with another antivirus.
    private static readonly Regex NotPrimary = new("Passive|EDR", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // A host name or an IP address as network protection names a destination.
    private static readonly Regex HostName = new(@"^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$|^[0-9a-fA-F:.]{2,45}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly char[] UrlBreaks = { '/', '?', '#' };

    /// <summary>The attack surface reduction rules by their IDs, with the names of Microsoft's rules reference.</summary>
    public static readonly IReadOnlyDictionary<string, string> RuleNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["56a863a9-875e-4185-98a7-b882c64b5ce5"] = "Block abuse of exploited vulnerable signed drivers",
        ["7674ba52-37eb-4a4f-a9a1-f0f9a1619a2c"] = "Block Adobe Reader from creating child processes",
        ["d4f940ab-401b-4efc-aadc-ad5f3c50688a"] = "Block all Office applications from creating child processes",
        ["9e6c4e1f-7d60-472f-ba1a-a39ef669e4b2"] = "Block credential stealing from the Windows local security authority subsystem",
        ["be9ba2d9-53ea-4cdc-84e5-9b1eeee46550"] = "Block executable content from email client and webmail",
        ["01443614-cd74-433a-b99e-2ecdc07bfc25"] = "Block executable files from running unless they meet a prevalence, age, or trusted list criterion",
        ["5beb7efe-fd9a-4556-801d-275e5ffc04cc"] = "Block execution of potentially obfuscated scripts",
        ["d3e037e1-3eb8-44c8-a917-57927947596d"] = "Block JavaScript or VBScript from launching downloaded executable content",
        ["3b576869-a4ec-4529-8536-b80a7769e899"] = "Block Office applications from creating executable content",
        ["75668c1f-73b5-4cf0-bb93-3ecf5cb7cc84"] = "Block Office applications from injecting code into other processes",
        ["26190899-1602-49e8-8b27-eb1d0a1ce869"] = "Block Office communication application from creating child processes",
        ["e6db77e5-3df2-4cf1-b95a-636979351e5b"] = "Block persistence through WMI event subscription",
        ["d1e49aac-8f56-4280-b9ba-993a6d77406c"] = "Block process creations originating from PSExec and WMI commands",
        ["33ddedf1-c6e0-47cb-833e-de6133960387"] = "Block rebooting machine in Safe Mode",
        ["b2b3f03d-6a65-4f7b-a9c7-1c7ef74a9ba4"] = "Block untrusted and unsigned processes that run from USB",
        ["c0033c00-d16d-4114-a5a0-dc9b3a7d2ceb"] = "Block use of copied or impersonated system tools",
        ["a8f5898e-1dc8-49a9-9878-85004b8a61e6"] = "Block Webshell creation for Servers",
        ["92e97fa1-2edf-4476-bdd6-9dd0b4dddc7b"] = "Block Win32 API calls from Office macros",
        ["c1db55ab-c21a-4637-bb3f-a12568109d35"] = "Use advanced protection against ransomware",
    };

    /// <summary>
    /// The rules that stop a program from starting: the event's target is that program and its
    /// process is what started it, so the card names the program ("c.exe was blocked ... Started
    /// by explorer.exe").
    /// </summary>
    public static readonly IReadOnlyCollection<string> StartRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "01443614-cd74-433a-b99e-2ecdc07bfc25", "b2b3f03d-6a65-4f7b-a9c7-1c7ef74a9ba4", "d4f940ab-401b-4efc-aadc-ad5f3c50688a",
        "26190899-1602-49e8-8b27-eb1d0a1ce869", "7674ba52-37eb-4a4f-a9a1-f0f9a1619a2c", "d1e49aac-8f56-4280-b9ba-993a6d77406c",
        "d3e037e1-3eb8-44c8-a917-57927947596d", "be9ba2d9-53ea-4cdc-84e5-9b1eeee46550", "c0033c00-d16d-4114-a5a0-dc9b3a7d2ceb",
        "c1db55ab-c21a-4637-bb3f-a12568109d35",
    };

    /// <summary>
    /// The rules that stop a file from being made or used by a process: the event's target is that
    /// file, so the card names the file ("a.js was blocked ... By wscript.exe").
    /// </summary>
    public static readonly IReadOnlyCollection<string> FileRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "3b576869-a4ec-4529-8536-b80a7769e899", "5beb7efe-fd9a-4556-801d-275e5ffc04cc", "56a863a9-875e-4185-98a7-b882c64b5ce5",
        "a8f5898e-1dc8-49a9-9878-85004b8a61e6",
    };

    // The other known rules stop what the process itself does (reading the LSA's memory, injecting
    // code, a WMI subscription, a restart in safe mode, Win32 calls from a macro): the card names the process.

    public DefenderBlocksCheck(RunContext run)
    {
        Run = run ?? throw new ArgumentNullException(nameof(run));
    }

    public RunContext Run { get; }

    private EngineServices Services => Run.Services;

    public int Check()
    {
        Run.StepHeader(1, 1, "Check Defender blocks (read-only)");
        var now = Services.Clock.UtcNow;
        var since = now.AddDays(-Days);
        var info = ReadDefenderInfo();
        var defender = StateOf(info);
        IReadOnlyList<DefenderEvent> events;
        try
        {
            events = Services.DefenderEvents.Read(EventIds, since, MaxEvents + 1);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Run.Log("The Microsoft Defender operational log could not be read: " + ex.GetType().Name + ".", LogSeverity.Warning);
            Run.AddFinding(FindingLevel.Warning, "DEFCHECK-UNREADABLE", "The Microsoft Defender log could not be read",
                "Windows did not give the Microsoft Defender operational log, so this check cannot say whether Defender blocked something. This is common when another security product replaces Microsoft Defender.",
                "Copy details for IT if something stopped working and you do not know why.",
                "Check which security product is active on this device and read its own log; the Microsoft Defender operational log is missing or not readable for SYSTEM.");
            Close("unreadable", defender, new Counts(), 0, 0, false, null);
            return EngineExitCode.Warnings;
        }

        var truncated = events.Count > MaxEvents;
        var read = events.Take(MaxEvents).ToList();
        var profiles = Profiles();
        var callerName = Run.CallerSid.Length > 0 ? Services.DefenderEvents.NameOf(Run.CallerSid) ?? "" : "";
        var sids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, DefenderGroup>(StringComparer.Ordinal);
        var others = 0;
        // What Defender did with each detection, by its detection ID: the newest action event wins,
        // whether it says the action was taken (1117) or failed (1118, 1119).
        var actions = new Dictionary<string, (string Name, bool Failed)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in read.Where(e => IsAction(e.Id)))
        {
            var detectionId = (Value(e, "Detection ID") ?? "").Trim();
            var action = Text(Value(e, "Action Name"));
            if (detectionId.Length > 0 && !actions.ContainsKey(detectionId)) { actions[detectionId] = (action.Length > 0 ? action : "Its action", e.Id != ActionTaken); }
        }
        foreach (var e in read.Where(e => !IsAction(e.Id)))
        {
            var scope = ScopeOf(Value(e, e.Id == Detection ? "Detection User" : "User", "User"), callerName, sids);
            if (scope == DefenderEventScope.Other)
            {
                others++;
                continue;
            }
            var group = GroupOf(e, profiles);
            group.Scope = scope;
            var subject = SubjectOf(group);
            var key = string.Join("|", group.Family.ToString(), group.Kind, group.RuleId, group.Name.ToUpperInvariant(), subject.Key.ToUpperInvariant(), scope.ToString());
            var eventDetectionId = e.Id == Detection ? (Value(e, "Detection ID") ?? "").Trim() : "";
            if (groups.TryGetValue(key, out var known))
            {
                // Newest first: the first event of a group keeps its time, path and process.
                known.Count++;
                if (eventDetectionId.Length > 0) { known.DetectionIds.Add(eventDetectionId); }
                if (subject.Other.Length > 0) { known.Others.Add(subject.Other); }
                continue;
            }
            group.Count = 1;
            group.NewestUtc = e.TimeCreatedUtc;
            if (eventDetectionId.Length > 0) { group.DetectionIds.Add(eventDetectionId); }
            if (subject.Other.Length > 0) { group.Others.Add(subject.Other); }
            groups[key] = group;
        }

        // The action of the group's newest detection only: an older detection's action is not this one's.
        foreach (var group in groups.Values.Where(g => g.Family == DefenderFamily.Threat && g.DetectionIds.Count > 0))
        {
            if (actions.TryGetValue(group.DetectionIds[0], out var action)) { (group.Action, group.ActionFailed) = action; }
        }
        var ordered = groups.Values.OrderBy(g => g.Kind == "detection" ? 0 : g.Kind == "block" ? 1 : 2).ThenByDescending(g => g.NewestUtc).ToList();
        int Sum(Func<DefenderGroup, bool> which) => ordered.Where(which).Sum(g => g.Count);
        var counts = new Counts
        {
            Detections = Sum(g => g.Kind == "detection"),
            RuleBlocks = Sum(g => g.Kind == "block" && g.Family == DefenderFamily.Rule),
            FolderBlocks = Sum(g => g.Kind == "block" && g.Family == DefenderFamily.Folder),
            NetworkBlocks = Sum(g => g.Kind == "block" && g.Family == DefenderFamily.Network),
            Audits = Sum(g => g.Kind == "audit"),
        };
        Run.Log(string.Format(Invariant, "Microsoft Defender log, last {0} days: {1}, {2}, {3}, {4}, {5}; {6} not shown. Microsoft Defender Antivirus: {7}.", Days,
            TextFormat.Count(counts.Detections, "detection"), TextFormat.Count(counts.RuleBlocks, "rule block"), TextFormat.Count(counts.FolderBlocks, "controlled folder access block"),
            TextFormat.Count(counts.NetworkBlocks, "network protection block"), TextFormat.Count(counts.Audits, "audit"),
            TextFormat.Count(others, "event") + " of other users or without a user", defender));

        var notActive = defender is "passive" or "off";
        if (notActive)
        {
            Run.AddFinding(FindingLevel.Info, "DEFCHECK-NOT-ACTIVE", "Microsoft Defender Antivirus is not the active antivirus on this device",
                "It runs in passive mode or is switched off, so its attack surface reduction rules, controlled folder access and network protection do not block here and another security product is in charge. What that product blocks is not in this log.",
                "If something stopped working and you do not know why, copy details for IT.",
                "Microsoft Defender Antivirus mode: " + defender + ". Read the active security product's own log or portal for its blocks.");
        }

        var number = 0;
        foreach (var group in ordered.Take(MaxGroups))
        {
            number++;
            AddGroupFinding(group, number);
        }
        if (ordered.Count > MaxGroups)
        {
            Run.AddFinding(FindingLevel.Info, "DEFCHECK-MORE", TextFormat.Count(ordered.Count - MaxGroups, "more block or detection") + " in the log",
                "Only the " + MaxGroups.ToString(Invariant) + " newest are named here.", "", "The Microsoft Defender portal and the operational log have every event.");
        }
        if (others > 0)
        {
            Run.AddFinding(FindingLevel.Info, "DEFCHECK-OTHER-USERS", TextFormat.Count(others, "event") + " " + (others == 1 ? "is" : "are") + " not shown",
                "This check shows what Defender logged for the device and for you. Events of other users, and events that name no user, are only counted.", "");
        }
        if (truncated)
        {
            Run.AddFinding(FindingLevel.Info, "DEFCHECK-TRUNCATED", "The log holds more events than this check reads",
                "The newest " + MaxEvents.ToString(Invariant) + " events were read; older ones of the last " + Days.ToString(Invariant) + " days are not counted.", "");
        }
        var oldest = Services.DefenderEvents.OldestUtc();
        var empty = oldest == null && read.Count == 0;
        var shortLog = oldest is DateTime first && first > since;
        var reach = shortLog
            ? " since " + RunContext.UserTime(oldest.Value, "yyyy-MM-dd HH:mm") + " (the log reaches no further back)"
            : " of the last " + Days.ToString(Invariant) + " days";
        if (ordered.Count == 0 && empty)
        {
            Run.AddFinding(notActive ? FindingLevel.Info : FindingLevel.Warning, "DEFCHECK-EMPTY", "The Microsoft Defender log holds no events",
                "There is nothing in the log to read, so this check cannot say whether Defender blocked something: the log may have been cleared, or Defender has not written to it.",
                "If something stopped working and you do not know why, copy details for IT.",
                "The Microsoft Defender operational log is empty; check its size and retention, and whether it was cleared.");
        }
        else if (ordered.Count == 0 && defender == "unknown")
        {
            Run.AddFinding(FindingLevel.Info, "DEFCHECK-STATE-UNKNOWN", "Whether Microsoft Defender Antivirus is active could not be read",
                "The Microsoft Defender log" + reach + " has no threat detection and no block, but this check could not read whether Microsoft Defender Antivirus runs on this device, so that does not show that nothing was blocked. " + NotRead,
                "If something stopped working and you do not know why, copy details for IT.",
                "Microsoft Defender Antivirus's status (MSFT_MpComputerStatus) could not be read as SYSTEM: check whether its service runs, or whether another security product replaced it.");
        }
        else if (ordered.Count == 0 && !notActive)
        {
            Run.AddFinding(FindingLevel.Ok, "DEFCHECK-NONE", "No threat detection and no Defender block" + (others > 0 ? " for this device or for you" : ""),
                "The Microsoft Defender log" + reach + " has no threat detection and no block or audit by attack surface reduction rules, controlled folder access or network protection. " + NotRead,
                "Nothing to do for these. If something stopped working and you do not know why, copy details for IT.");
        }
        else if (shortLog)
        {
            Run.AddFinding(FindingLevel.Info, "DEFCHECK-SHORT-LOG", "The log does not reach back " + Days.ToString(Invariant) + " days",
                "Its oldest event is of " + RunContext.UserTime(oldest.Value, "yyyy-MM-dd HH:mm") + "; anything before that is no longer in the log.", "");
        }

        // The health facts of an active Defender (or one whose status could not be read); a passive or
        // switched-off Defender's scans and switches are not this device's protection.
        var health = notActive || empty ? null : Health(info, since, oldest);

        var blocks = counts.RuleBlocks + counts.FolderBlocks + counts.NetworkBlocks;
        var verdict = counts.Detections > 0 ? "detections" : blocks > 0 ? "blocks" : counts.Audits > 0 ? "audits" : notActive ? "not-active" : empty ? "empty" : defender == "unknown" ? "state-unknown" : "none";
        Close(verdict, defender, counts, others, ordered.Count, truncated, health);
        return counts.Detections > 0 || blocks > 0 || verdict == "empty" || health?.Warning == true ? EngineExitCode.Warnings : EngineExitCode.Success;
    }

    /// <summary>The counts of the shown events by protection.</summary>
    private sealed class Counts
    {
        public int Detections;
        public int RuleBlocks;
        public int FolderBlocks;
        public int NetworkBlocks;
        public int Audits;
    }

    /// <summary>Microsoft Defender's status, read once per run; null when it cannot be read.</summary>
    private DefenderInfo ReadDefenderInfo()
    {
        try { return Services.Device.ReadDefender(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>"active", "passive", "off" or "unknown": whether Microsoft Defender Antivirus is the device's antivirus.</summary>
    private static string StateOf(DefenderInfo info)
    {
        if (info == null) { return "unknown"; }
        if (!info.ServiceEnabled) { return "off"; }
        return NotPrimary.IsMatch(info.Mode ?? "") ? "passive" : "active";
    }

    private static string Iso(DateTime? utc) => utc.HasValue ? utc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ", Invariant) : "";

    private void Close(string verdict, string defender, Counts counts, int others, int groups, bool truncated, DefenderHealthFacts health)
    {
        var fields = new List<KeyValuePair<string, object>>
        {
            RunContext.Field("verdict", verdict),
            RunContext.Field("defender", defender),
            RunContext.Field("days", Days),
            RunContext.Field("detections", counts.Detections),
            RunContext.Field("blocks", counts.RuleBlocks),
            RunContext.Field("folderBlocks", counts.FolderBlocks),
            RunContext.Field("networkBlocks", counts.NetworkBlocks),
            RunContext.Field("audits", counts.Audits),
            RunContext.Field("others", others),
            RunContext.Field("groups", groups),
            RunContext.Field("truncated", truncated),
        };
        if (health != null)
        {
            // Device facts only: times, a state word, counts.
            fields.Add(RunContext.Field("quickScan", Iso(health.QuickScanUtc)));
            fields.Add(RunContext.Field("fullScan", Iso(health.FullScanUtc)));
            fields.Add(RunContext.Field("rtp", health.RealTimeProtection));
            fields.Add(RunContext.Field("offNow", health.OffNow));
            fields.Add(RunContext.Field("protectionOff", health.ProtectionOff));
            fields.Add(RunContext.Field("featureFailures", health.FeatureFailures));
            fields.Add(RunContext.Field("tamperBlocked", health.TamperBlocked));
            fields.Add(RunContext.Field("updateFailures", health.UpdateFailures));
            fields.Add(RunContext.Field("updateWarning", health.UpdateWarning));
            fields.Add(RunContext.Field("healthRead", health.Read));
        }
        Run.WriteRecord("defender-check", fields.ToArray());
        Run.PersistentLog(string.Format(Invariant, "EndpointToolbelt:DEFENDER-CHECK verdict={0} defender={1} detections={2} blocks={3} folder-blocks={4} network-blocks={5} audits={6} others={7}",
            verdict, defender, counts.Detections, counts.RuleBlocks, counts.FolderBlocks, counts.NetworkBlocks, counts.Audits, others)
            + (health == null ? "" : string.Format(Invariant, " quick-scan={0} rtp={1} off-now={2} protection-off={3} feature-failures={4} tamper-blocked={5} update-failures={6} health-read={7}",
                health.QuickScanUtc.HasValue ? Iso(health.QuickScanUtc) : "none", health.RealTimeProtection, health.OffNow ? "yes" : "no", health.ProtectionOff, health.FeatureFailures,
                health.TamperBlocked, health.UpdateFailures, health.Read)));
    }

    /// <summary>How a rule's card names what was stopped.</summary>
    private enum Subject
    {
        /// <summary>The program that was kept from starting (the target), started by the process.</summary>
        Started,

        /// <summary>The file that was kept from being made or used (the target), by the process.</summary>
        File,

        /// <summary>The process itself.</summary>
        Process,
    }

    private static Subject SubjectOfRule(DefenderGroup group)
    {
        if (group.Path.Length == 0 || group.Path == OtherProfile) { return Subject.Process; }
        if (StartRules.Contains(group.RuleId)) { return Subject.Started; }
        return FileRules.Contains(group.RuleId) ? Subject.File : Subject.Process;
    }

    /// <summary>What a group is keyed on, and what else it counts: the stopped thing, and the different others.</summary>
    private static (string Key, string Other) SubjectOf(DefenderGroup group) => group.Family switch
    {
        DefenderFamily.Rule when SubjectOfRule(group) != Subject.Process => (group.Path, FileName(group.Process)),
        DefenderFamily.Network => (FileName(group.Process), ""),
        _ => (FileName(group.Process), group.Path),
    };

    private void AddGroupFinding(DefenderGroup group, int number)
    {
        var times = group.Count == 1 ? "once" : group.Count.ToString(Invariant) + " times";
        var when = (group.Count == 1 ? "on " : "last on ") + RunContext.UserTime(group.NewestUtc, "yyyy-MM-dd HH:mm");
        var who = group.Scope == DefenderEventScope.Caller ? "in your session" : "for a Windows or service process";
        var block = group.Kind == "block";
        var id = (group.Kind == "detection" ? "DEFCHECK-DETECTION-" : block ? "DEFCHECK-BLOCK-" : "DEFCHECK-AUDIT-") + number.ToString(Invariant);
        var level = group.Kind == "audit" ? FindingLevel.Info : FindingLevel.Warning;
        var process = FileName(group.Process);
        switch (group.Family)
        {
            case DefenderFamily.Threat:
            {
                var did = group.Action.Length == 0 ? " This check found no action for it."
                    : group.ActionFailed ? " What Defender did: " + group.Action + " failed." : " What Defender did: " + group.Action + ".";
                Run.AddFinding(group.ActionFailed ? FindingLevel.Error : level, id, "Microsoft Defender detected " + group.Name,
                    string.Format(Invariant, "Detected {0}, {1}, {2}.{3}{4}{5}{6}", times, when, who,
                        group.Severity.Length > 0 ? " Severity: " + group.Severity + "." : "", Several(group, "File", group.Path),
                        group.Resource.Length > 0 ? " Found in " + group.Resource + " (not shown)." : "", did),
                    group.ActionFailed ? "Copy details for IT now: Defender could not complete its action, so this device may not be protected."
                        : group.Action.Length > 0 ? "Defender acted on it. Tell your IT support if you did not expect it or it comes back."
                        : "Tell your IT support if you did not expect it or it comes back.",
                    "Threat detection (event 1116)" + (group.Process.Length > 0 ? "; process " + group.Process : "")
                        + (group.ActionFailed ? "; the action failed (event 1118 or 1119): check the device in the Microsoft Defender portal and run a full scan." : ". The Microsoft Defender portal shows the action taken."));
                return;
            }

            case DefenderFamily.Folder:
            {
                var sector = group.RuleId == "sector";
                var app = process.Length > 0 ? process : "An app";
                var what = sector ? "write to a disk's boot sectors" : "change files in a protected folder";
                Run.AddFinding(level, id, (block ? "Controlled folder access blocked " : "Controlled folder access audited, not blocked: ") + (process.Length > 0 ? process : "an app"),
                    string.Format(Invariant, "{0} {1} {2} {3}, {4}, {5}.{6}", app, block ? "was not allowed to" : "would not have been allowed to", what, times, when, who,
                        Several(group, sector ? "Disk" : "Folder", group.Path)),
                    block ? "If this is an app you need for work and it could not save or change a file at that time, copy details for IT: IT support can allow the app."
                        : "Nothing to do: controlled folder access only records here. Your IT support uses this to decide about the setting.",
                    string.Format(Invariant, "Controlled folder access ({0}, event {1}){2}. An app is allowed in the Defender or Intune policy (allowed applications for controlled folder access).",
                        block ? "block mode" : "audit mode", sector ? (block ? SectorBlock : SectorAudit) : (block ? FolderBlock : FolderAudit),
                        group.Process.Length > 0 ? "; process " + group.Process : ""));
                return;
            }

            case DefenderFamily.Network:
            {
                var app = process.Length > 0 ? process : "An app";
                Run.AddFinding(level, id, (block ? "Network protection blocked a connection to " : "Network protection audited, not blocked: a connection to ") + group.Name,
                    string.Format(Invariant, "{0} {1} {2} {3}, {4}, {5}.", app, block ? "could not connect to" : "would not have been allowed to connect to", group.Name, times, when, who),
                    block ? "If you need this site or service for work, copy details for IT: IT support can check it and allow it."
                        : "Nothing to do: network protection only records here.",
                    string.Format(Invariant, "Network protection ({0}, event {1}); destination {2}{3}. A destination is allowed with an indicator in the Microsoft Defender portal. Microsoft Edge is protected by SmartScreen instead, which this check does not read.",
                        block ? "block mode" : "audit mode", block ? NetworkBlock : NetworkAudit, group.Name, group.Process.Length > 0 ? "; process " + group.Process : ""));
                return;
            }
        }

        var subject = SubjectOfRule(group);
        var verb = block ? "was blocked" : "would have been blocked";
        string detail;
        if (!RuleNames.ContainsKey(group.RuleId))
        {
            // A rule this version does not know: what it stops is not known either, so the card says
            // only that it fired and for what.
            detail = string.Format(Invariant, "The rule {0} {1}{2}, {3}, {4}.{5}", block ? "fired" : "fired in audit mode", times,
                process.Length > 0 ? " for " + process : "", when, who,
                string.Equals(group.Path, group.Process, StringComparison.OrdinalIgnoreCase) ? "" : Several(group, "Target", group.Path));
        }
        else if (subject == Subject.Process)
        {
            detail = string.Format(Invariant, "{0} {1} {2}, {3}, {4}.{5}", process.Length > 0 ? process : "A process", verb, times, when, who,
                string.Equals(group.Path, group.Process, StringComparison.OrdinalIgnoreCase) ? "" : Several(group, "Target", group.Path));
        }
        else
        {
            var by = group.Others.Count > 1
                ? string.Format(Invariant, " {0} {1} and {2}.", subject == Subject.Started ? "Started by" : "By", process, TextFormat.Count(group.Others.Count - 1, "other program"))
                : process.Length > 0 ? " " + (subject == Subject.Started ? "Started by " : "By ") + process + "." : "";
            detail = string.Format(Invariant, "{0} {1} {2}, {3}, {4}.{5} File: {6}.", FileName(group.Path), verb, times, when, who, by, group.Path);
        }
        Run.AddFinding(level, id, (block ? "Blocked by Defender: " : "Audited by Defender, not blocked: ") + group.Name, detail,
            block ? "If something you need for work stopped at that time, copy details for IT. Otherwise there is nothing to do."
                : "Nothing to do: the rule only records. Your IT support uses this to decide about the rule.",
            string.Format(Invariant, "Attack surface reduction rule {0} ({1}, event {2}){3}{4}. An exclusion or a change of the rule's mode is made in the Defender or Intune policy.",
                group.RuleId.Length > 0 ? group.RuleId : "with an unknown ID", block ? "block mode" : "audit mode", block ? RuleBlock : RuleAudit,
                group.Process.Length > 0 ? "; process " + group.Process : "", group.Path.Length > 0 ? "; target " + group.Path : ""));
    }

    /// <summary>" File: X." for one; " Newest file: X (3 different)." when the group's events had several.</summary>
    private static string Several(DefenderGroup group, string label, string path)
    {
        if (path.Length == 0) { return ""; }
        if (group.Others.Count <= 1) { return " " + label + ": " + path + "."; }
        return string.Format(Invariant, " Newest {0}: {1} ({2} different).", label.ToLowerInvariant(), path, group.Others.Count);
    }

    private static DefenderGroup GroupOf(DefenderEvent e, ProfilePaths profiles)
    {
        var process = CleanPath(Value(e, "Process Name"), profiles);
        switch (e.Id)
        {
            case Detection:
            {
                var name = Text(Value(e, "Threat Name"));
                return new DefenderGroup
                {
                    Family = DefenderFamily.Threat,
                    Kind = "detection",
                    Name = name.Length > 0 ? name : "a threat it did not name",
                    Severity = Text(Value(e, "Severity Name")),
                    Process = process,
                    Path = CleanPath(FirstResource(Value(e, "Path")), profiles),
                    Resource = ResourceWord(Value(e, "Path")),
                };
            }
            case FolderBlock or FolderAudit or SectorBlock or SectorAudit:
                return new DefenderGroup
                {
                    Family = DefenderFamily.Folder,
                    Kind = e.Id is FolderBlock or SectorBlock ? "block" : "audit",
                    RuleId = e.Id is SectorBlock or SectorAudit ? "sector" : "",
                    Process = process,
                    Path = CleanPath(Value(e, "Path"), profiles),
                };
            case NetworkBlock or NetworkAudit:
                return new DefenderGroup
                {
                    Family = DefenderFamily.Network,
                    Kind = e.Id == NetworkBlock ? "block" : "audit",
                    // Network protection names the destination in one of these values.
                    Name = Destination(Value(e, "Destination", "Path")),
                    Process = process,
                };
            default:
            {
                var id = RuleId(e);
                return new DefenderGroup
                {
                    Family = DefenderFamily.Rule,
                    Kind = e.Id == RuleBlock ? "block" : "audit",
                    RuleId = id,
                    Name = id.Length > 0 && RuleNames.TryGetValue(id, out var name) ? name : id.Length > 0 ? "a rule this version does not know (" + id + ")" : "a rule the event does not name",
                    Process = process,
                    Path = CleanPath(Value(e, "Path"), profiles),
                };
            }
        }
    }

    /// <summary>
    /// A network destination as its host name only: a URL loses its scheme, port, path and query
    /// (they can carry a token or a person's data); anything that is no host name or IP address is
    /// said not to be named.
    /// </summary>
    internal static string Destination(string value)
    {
        var text = Text(value);
        if (text.Length == 0) { return "a destination the event does not name"; }
        if (text.Contains("://"))
        {
            // A URL of any scheme: its host only, or nothing when it has none.
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host.ToLowerInvariant() : "a destination in a form this check does not show";
        }
        // Anything with a space or markup is no destination name: it is not shown.
        if (text.Any(c => char.IsWhiteSpace(c) || c == '<' || c == '>' || c == '"')) { return "a destination in a form this check does not show"; }
        var host = text;
        var cut = host.IndexOfAny(UrlBreaks);
        if (cut >= 0) { host = host.Substring(0, cut); }
        if (host.Count(c => c == ':') == 1) { host = host.Substring(0, host.IndexOf(':')); }
        return HostName.IsMatch(host) ? host.ToLowerInvariant() : "a destination in a form this check does not show";
    }

    /// <summary>The rule's ID in lower case from the "ID" value; empty when the event names none.</summary>
    internal static string RuleId(DefenderEvent e)
    {
        var id = (Value(e, "ID") ?? "").Trim().Trim('{', '}');
        return Guid.TryParseExact(id, "D", out var parsed) ? parsed.ToString("D") : "";
    }

    /// <summary>
    /// Whose event it is, by the SID Windows resolves the event's account to: a system account is the
    /// device, the caller's SID is the caller. Only an account Windows cannot resolve is compared by
    /// name with the name Windows gives the caller's SID. Anything else is another user, also an
    /// event that names no user, so nothing is shown by mistake.
    /// </summary>
    private DefenderEventScope ScopeOf(string account, string callerName, Dictionary<string, string> cache)
    {
        account = (account ?? "").Trim();
        if (account.Length == 0) { return DefenderEventScope.Other; }
        if (!cache.TryGetValue(account, out var sid))
        {
            sid = account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) ? account : Services.DefenderEvents.SidOf(account);
            cache[account] = sid;
        }
        if (string.IsNullOrEmpty(sid))
        {
            return callerName.Length > 0 && string.Equals(account, callerName, StringComparison.OrdinalIgnoreCase) ? DefenderEventScope.Caller : DefenderEventScope.Other;
        }
        if (SystemSids.Contains(sid, StringComparer.OrdinalIgnoreCase)) { return DefenderEventScope.Device; }
        return Run.CallerSid.Length > 0 && string.Equals(sid, Run.CallerSid, StringComparison.OrdinalIgnoreCase) ? DefenderEventScope.Caller : DefenderEventScope.Other;
    }

    /// <summary>The caller's profile folder, the folder the profiles are in, and every other profile folder of the profile list.</summary>
    private ProfilePaths Profiles()
    {
        var profiles = new ProfilePaths { Caller = Run.CallerSid.Length > 0 ? ProfileOf(Run.CallerSid) : "" };
        var cut = profiles.Caller.LastIndexOf('\\');
        profiles.Root = cut > 2 ? profiles.Caller.Substring(0, cut) : (Services.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\') + @"\Users";
        IReadOnlyList<string> sids;
        try { sids = Services.Registry.SubKeyNames(ProfileListKey); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException or ArgumentException)
        {
            sids = Array.Empty<string>();
        }
        foreach (var sid in sids)
        {
            // The caller's own profile, and the system accounts' profiles: their paths are the device's.
            if (string.Equals(sid, Run.CallerSid, StringComparison.OrdinalIgnoreCase) || SystemSids.Contains(sid, StringComparer.OrdinalIgnoreCase)) { continue; }
            var folder = ProfileOf(sid);
            if (folder.Length > 3 && !string.Equals(folder, profiles.Caller, StringComparison.OrdinalIgnoreCase)) { profiles.Others.Add(folder); }
        }
        return profiles;
    }

    /// <summary>A profile folder from the profile list, without a trailing backslash; empty when unknown.</summary>
    private string ProfileOf(string sid)
    {
        try
        {
            var raw = MachineRegistry.AsText(Services.Registry.GetValue(ProfileListKey + @"\" + sid, "ProfileImagePath")) ?? "";
            var drive = (Services.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
            return Normalize(raw.Replace("%SystemDrive%", drive).Replace("%systemdrive%", drive).Replace("%SYSTEMDRIVE%", drive)).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException or ArgumentException)
        {
            return "";
        }
    }

    /// <summary>
    /// A path as the caller may read it: below the caller's own profile it starts with
    /// %USERPROFILE%; below any other profile, or a folder of the profiles' folder that is no shared
    /// profile, it is not written at all; anywhere else it is the path. The long-path prefixes are
    /// dropped first. A path in a form the check cannot compare (a device or volume name) is
    /// written as its file name only, unless it is in another profile. Control and format
    /// characters are dropped and a long path is cut.
    /// </summary>
    internal static string CleanPath(string path, ProfilePaths profiles)
    {
        var text = Text(Normalize(path));
        if (text.Length == 0) { return ""; }
        // Controlled folder access names a protected folder with the profile as a token
        // ("%userprofile%\Documents\"): it holds no profile name, so it is shown whole, in one spelling.
        if (text.StartsWith("%USERPROFILE%", StringComparison.OrdinalIgnoreCase)) { return "%USERPROFILE%" + text.Substring("%USERPROFILE%".Length); }
        var known = text.Length > 2 && text[1] == ':' || text.StartsWith(@"\\", StringComparison.Ordinal);
        // Below the drive: "\Users\name\..." for "C:\Users\name\...", and the same part of a device path.
        var tail = DriveTail(text);
        if (known && Under(text, profiles.Caller)) { return "%USERPROFILE%" + text.Substring(profiles.Caller.Length); }
        if (tail.Length > 0)
        {
            foreach (var other in profiles.Others)
            {
                if (Under(tail, DriveTail(other))) { return OtherProfile; }
            }
            foreach (var root in new[] { DriveTail(profiles.Root), @"\Documents and Settings" })
            {
                if (root.Length < 2 || !tail.StartsWith(root + @"\", StringComparison.OrdinalIgnoreCase)) { continue; }
                var rest = tail.Substring(root.Length + 1);
                var end = rest.IndexOf('\\');
                var folder = end < 0 ? rest : rest.Substring(0, end);
                var own = !known && profiles.Caller.Length > 0 && string.Equals(root + @"\" + folder, DriveTail(profiles.Caller), StringComparison.OrdinalIgnoreCase);
                if (!own && !SharedProfiles.Contains(folder, StringComparer.OrdinalIgnoreCase)) { return OtherProfile; }
            }
        }
        return known ? text : FileName(text);
    }

    /// <summary>The path without the long-path prefixes "\\?\" and "\??\" ("\\?\UNC\server" becomes "\\server").</summary>
    internal static string Normalize(string path)
    {
        var text = (path ?? "").Trim();
        if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) { return @"\\" + text.Substring(8); }
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal) || text.StartsWith(@"\??\", StringComparison.Ordinal) || text.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            var rest = text.Substring(4);
            if (rest.Length > 1 && rest[1] == ':') { return rest; }
            return @"\" + rest;
        }
        return text;
    }

    /// <summary>The part of a path below its drive or volume ("C:\Users\a" and "\Device\HarddiskVolume3\Users\a" give "\Users\a"); empty for a share or a relative path.</summary>
    private static string DriveTail(string path)
    {
        if (string.IsNullOrEmpty(path)) { return ""; }
        if (path.Length > 2 && path[1] == ':') { return path.Substring(2); }
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) { return ""; }
        if (path.StartsWith(@"\", StringComparison.Ordinal))
        {
            // "\Device\HarddiskVolume3\..." or "\Volume{guid}\...": skip the device and volume names.
            var parts = path.Split('\\');
            var skip = parts.Length > 2 && parts[1].Equals("Device", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
            return parts.Length > skip ? @"\" + string.Join(@"\", parts.Skip(skip)) : "";
        }
        return "";
    }

    private static bool Under(string path, string folder)
        => folder.Length > 0 && (path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase));

    /// <summary>The kinds of a detection's resources that are a file; any other kind (a command line, a registry value, a process) is never written.</summary>
    private static readonly string[] FileKinds = { "file", "containerfile", "webfile" };

    /// <summary>
    /// A detection's resources ("file:_C:\a.exe; CmdLine:_C:\b.exe /c x") as kind and value. A part
    /// without a kind mark is never a file: after a resource it is the rest of that resource (a
    /// command line can hold a ";"), and as the first part it has no kind at all.
    /// </summary>
    private static List<(string Kind, string Value)> Resources(string paths)
    {
        var resources = new List<(string Kind, string Value)>();
        foreach (var part in (paths ?? "").Split(';'))
        {
            var resource = part.Trim();
            var mark = resource.IndexOf(":_", StringComparison.Ordinal);
            if (mark > 0 && mark <= 20 && resource.Substring(0, mark).All(char.IsLetter))
            {
                resources.Add((resource.Substring(0, mark).ToLowerInvariant(), resource.Substring(mark + 2)));
            }
            else if (resources.Count > 0)
            {
                var last = resources[resources.Count - 1];
                resources[resources.Count - 1] = (last.Kind, last.Value + ";" + part);
            }
            else if (resource.Length > 0)
            {
                resources.Add(("", resource));
            }
        }
        return resources;
    }

    /// <summary>
    /// The first resource of a file kind in a detection's path list, without what follows a "|" or a
    /// "->"; empty when the detection names no file.
    /// </summary>
    internal static string FirstResource(string paths)
    {
        var first = Resources(paths).FirstOrDefault(r => FileKinds.Contains(r.Kind)).Value ?? "";
        var bar = first.IndexOf('|');
        if (bar >= 0) { first = first.Substring(0, bar); }
        var arrow = first.IndexOf("->", StringComparison.Ordinal);
        return (arrow > 0 ? first.Substring(0, arrow) : first).Trim();
    }

    /// <summary>What a detection found when none of its resources is a file, as a fixed word; empty when one is a file or it names none.</summary>
    internal static string ResourceWord(string paths)
    {
        var all = Resources(paths).ToList();
        if (all.Count == 0 || all.Any(r => FileKinds.Contains(r.Kind))) { return ""; }
        return all[0].Kind switch
        {
            "cmdline" => "a command line",
            "regkey" or "regkeyvalue" => "a registry value",
            "process" => "a process",
            _ => "something that is not a file",
        };
    }

    private static bool IsAction(int id) => id is ActionTaken or ActionFailed or ActionCritical;

    /// <summary>The file name of a cleaned path; the text itself when it has no folder.</summary>
    internal static string FileName(string path)
    {
        if (string.IsNullOrEmpty(path) || path == OtherProfile) { return path ?? ""; }
        var cut = path.LastIndexOf('\\');
        return cut >= 0 && cut < path.Length - 1 ? path.Substring(cut + 1) : path;
    }

    /// <summary>The first of these data values the event has; null when it has none.</summary>
    private static string Value(DefenderEvent e, params string[] names)
    {
        foreach (var name in names)
        {
            if (e.Data.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)) { return value; }
        }
        return null;
    }

    /// <summary>
    /// A value as one line of plain text: no control characters and no format characters (a
    /// direction override, a zero-width character), trimmed, cut at <see cref="MaxTextLength"/>.
    /// </summary>
    internal static string Text(string value)
    {
        if (string.IsNullOrEmpty(value)) { return ""; }
        var clean = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format) { clean.Append(c); }
        }
        var text = clean.ToString().Trim();
        return text.Length > MaxTextLength ? text.Substring(0, MaxTextLength) + "..." : text;
    }
}
