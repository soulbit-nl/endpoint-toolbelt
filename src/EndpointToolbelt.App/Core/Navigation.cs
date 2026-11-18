using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EndpointToolbelt.App.Core;

/// <summary>The four groups of the tools in "For IT"; each tool is in exactly one.</summary>
public enum ToolGroup
{
    Apps,
    SignIn,
    WindowsSecurity,
    NetworkStorage,
}

/// <summary>One tool of "For IT": the button it has in the window, its texts and what a search finds it by.</summary>
public sealed class ToolInfo
{
    internal ToolInfo(string button, ToolGroup group, string name, string line, params string[] keywords)
    {
        Button = button;
        Group = group;
        Name = name;
        _line = line;
        Keywords = keywords;
    }

    /// <summary>The x:Name of the tool's button in the main window.</summary>
    public string Button { get; }
    public ToolGroup Group { get; }
    public string Name { get; }

    private readonly string _line;

    /// <summary>
    /// The tool's line on its row and detail page. A tool no problem page offers ends in
    /// <see cref="ToolCatalog.ItOnlyNote"/>: the other tools are also self-service on Choose a problem.
    /// </summary>
    public string Line => ToolCatalog.ForItOnly(this) ? _line + " " + ToolCatalog.ItOnlyNote : _line;

    /// <summary>
    /// The text of the tool's button, on its detail page and on its row: the action it
    /// names ("Sync clock", "Check connections", "Choose a window"), never "Run" or "Open" alone. The
    /// tool's name when not set.
    /// </summary>
    public string ButtonText
    {
        get => _buttonText ?? Name;
        internal set => _buttonText = value;
    }

    private string _buttonText;

    /// <summary>
    /// The automation name of the tool's row button: its text and the tool's name ("Check connections:
    /// Check Microsoft connections", "Sync clock: Sync clock"), so it never shares a name with the tool's own
    /// button on its detail page.
    /// </summary>
    public string ButtonName => ButtonText + ": " + Name;

    /// <summary>Other words people use for it (aliases), found by the search too.</summary>
    public IReadOnlyList<string> Keywords { get; }

    /// <summary>The service action it runs (its run logs carry this name); null for a tool that runs in the app.</summary>
    public ToolbeltAction? Action { get; internal set; }

    /// <summary>The name a tool that runs in the app gives its run, so its run logs can be found; empty for a service action or a tool without a run.</summary>
    public string CheckName { get; internal set; } = "";

    /// <summary>Runs in the service (as SYSTEM) rather than as the signed-in user.</summary>
    public bool InService => Action.HasValue;

    /// <summary>
    /// The detail page's facts: how long it usually takes, only when that is a minute or
    /// longer (empty otherwise), and what it closes or changes, only when it closes or changes
    /// something (empty for a tool that only opens a folder or tests).
    /// </summary>
    public string Time { get; internal set; } = "";
    public string Changes { get; internal set; } = "";

    /// <summary>One of the two heavy tools (Full IME reset, Repair Windows Update): its buttons carry the red outline.</summary>
    public bool Heavy { get; internal set; }

    /// <summary>The tool opens something and leaves no run (Open IME logs).</summary>
    public bool OpensOnly => !Action.HasValue && CheckName.Length == 0;

    /// <summary>The tool opens a page of its own where the user chooses before anything runs (Bring my window back); its button says Open.</summary>
    public bool OpensPage { get; internal set; }

    /// <summary>The name its run logs have in the run history, or empty when it leaves none.</summary>
    public string LogKey => Action.HasValue ? Action.Value.ToString() : RunLogStore.KeyFor(CheckName);

    /// <summary>The names its run logs had under an earlier name of the tool, so its last run is still found.</summary>
    public IReadOnlyList<string> EarlierLogKeys { get; internal set; } = Array.Empty<string>();

    /// <summary>
    /// The tool's name in the policy value <c>DisabledTools</c> (<see cref="ToolPolicy"/>): the
    /// contract's action name for a tool that runs in the service, a name of its own otherwise.
    /// </summary>
    public string PolicyName
    {
        get => Action.HasValue ? Action.Value.ToString() : _policyName ?? "";
        internal set => _policyName = value;
    }

    private string _policyName;

    /// <summary>The health row (<see cref="HealthView"/> name) this tool acts on; the result says its new state after the checks ran again. Empty for none.</summary>
    public string HealthRow { get; internal set; } = "";

    /// <summary>The Office desktop app the tool starts; it is offered only when that app is installed.</summary>
    public OfficeApp? Office { get; internal set; }

    /// <summary>The tool needs the OneDrive sync app; it is off and says so when OneDrive is not installed.</summary>
    public bool NeedsOneDrive { get; internal set; }

    /// <summary>The tool's name in Recent runs, where the outcome follows it ("Word" for Start Word in safe mode, "OneDrive" for Reset OneDrive); its name otherwise.</summary>
    public string ShortName
    {
        get => _shortName ?? Name;
        internal set => _shortName = value;
    }

    private string _shortName;

    /// <summary>The run log name belongs to this tool: its own or an earlier one.</summary>
    public bool OwnsLogKey(string key) => !string.IsNullOrEmpty(key) && LogKey.Length > 0 && (LogKey == key || EarlierLogKeys.Contains(key));
}

/// <summary>
/// The tools: each in exactly one group, each with one button in "For IT", and a
/// search that finds a tool by its name, its line or an alias ("Company Portal", "IME",
/// "BitLocker", "clock"). A query word matches the start of a word, so "ime" does not find "time".
/// </summary>
public static class ToolCatalog
{
    /// <summary>The words that end the line of a tool that only For IT support offers.</summary>
    public const string ItOnlyNote = "Use it when IT support asks.";

    /// <summary>The intro of For IT support: every tool is here; the ones for IT support only say so on their line.</summary>
    public const string ItIntro = "Every tool in one place. Some are for IT support only; those say so.";

    /// <summary>
    /// A tool no problem page offers: not among the paths' tools, not the tool of the window or the network
    /// adapter page, and not a health row's fix (Home and Device health offer those). Its line says to use it
    /// when IT support asks. Every heavy tool (a long repair) says so too: a problem page shows it in its group "With IT support".
    /// </summary>
    public static bool ForItOnly(ToolInfo tool) => tool != null && (tool.Heavy || !SelfService.Value.Contains(tool.Button));

    private static readonly Lazy<HashSet<string>> SelfService = new(() => new HashSet<string>(
        ProblemPaths.All.SelectMany(p => p.Tools).Concat(new[] { "WindowBackButton", "RenewIpButton" })
            .Concat(Enum.GetValues(typeof(HealthFix)).Cast<HealthFix>().Select(ForFix).Where(t => t != null).Select(t => t.Button)), StringComparer.Ordinal));

    // The words every safe-mode tool is found by; before All, which uses it while it is set up.
    private static readonly string[] OfficeWords = { "Office", "Microsoft 365", "crash", "closes", "safe mode", "add-in" };

    public static readonly IReadOnlyList<ToolInfo> All = new[]
    {
        new ToolInfo("ClearCacheButton", ToolGroup.Apps, "Clear downloaded app content", "Removes app content the Intune Management Extension unpacked, so it downloads it again.",
            "IMECache", "cache", "download", "content", "app")
            { Action = ToolbeltAction.ClearAppCache, ButtonText = "Clear app content", Changes = "Removes downloaded app content; apps download it again when they next install." },
        new ToolInfo("LogsButton", ToolGroup.Apps, "Open IME logs", "Opens the folder with the Intune Management Extension's logs.",
            "IME", "logs", "AppWorkload", "IntuneManagementExtension")
            { PolicyName = ToolPolicy.OpenImeLogs },
        new ToolInfo("FullResetButton", ToolGroup.Apps, "Full IME reset", "Resets the Intune Management Extension so it checks every app again.",
            "IME", "reset", "apps", "Win32")
            { Action = ToolbeltAction.FullReset, ButtonText = "Start full reset", Heavy = true, Time = "Several minutes.", Changes = "Removes all Intune app state on this device; every app is detected again. Asks before it closes Company Portal (never by force).", HealthRow = HealthView.AppInstaller },
        new ToolInfo("PortalButton", ToolGroup.Apps, "Reset Company Portal data", "Clears the Company Portal app's local data, as Reset in its app settings does.",
            "Company Portal", "portal", "store", "reset")
            { CheckName = "Reset Company Portal data", ButtonText = "Reset Company Portal", PolicyName = ToolPolicy.ResetCompanyPortal, Changes = "Asks before it closes Company Portal (never by force), clears its data and signs you out of it. Installed apps stay." },
        new ToolInfo("RefreshSignInButton", ToolGroup.SignIn, "Refresh work account sign-in", "Renews your work account sign-in now.",
            "sign-in", "PRT", "token", "Entra", "account", "login")
            { CheckName = "Refresh work account sign-in", ButtonText = "Refresh sign-in", PolicyName = ToolPolicy.RefreshSignIn, Changes = "Renews your work account sign-in; nothing is removed.", HealthRow = HealthView.WorkAccount },
        new ToolInfo("ResetSignInButton", ToolGroup.SignIn, "Reset sign-in cache", "Removes the cached sign-in of your Microsoft 365 apps. Close them first.",
            "sign-in", "Office", "Outlook", "Microsoft 365", "token", "login", "password")
            { CheckName = "Reset sign-in cache", PolicyName = ToolPolicy.ResetSignInCache, Changes = "Asks before it closes open Microsoft 365 apps (they get up to 20 seconds); removes your cached work account sign-in.", HealthRow = HealthView.WorkAccount },
        new ToolInfo("TeamsCacheButton", ToolGroup.SignIn, "Clear Teams cache", "Closes Teams and clears its cache in your profile.",
            "Teams", "cache", "Microsoft 365", "sign-in")
            { CheckName = "Clear Teams cache", PolicyName = ToolPolicy.ClearTeamsCache, Changes = "Closes Teams and clears its cache in your profile. Chats and files stay in Microsoft 365." },
        // Offered only when the Office desktop app is installed; they run as the user, in the app.
        SafeModeTool(OfficeApp.Word, "Word"),
        SafeModeTool(OfficeApp.Excel, "Excel"),
        SafeModeTool(OfficeApp.Outlook, "Outlook", "mail", "classic"),
        SafeModeTool(OfficeApp.PowerPoint, "PowerPoint", "slides", "presentation"),
        new ToolInfo("UpdateDefenderButton", ToolGroup.WindowsSecurity, "Update Defender definitions", "Gets Microsoft Defender's latest security intelligence now.",
            "Defender", "antivirus", "security", "signatures", "virus")
            { Action = ToolbeltAction.UpdateDefender, ButtonText = "Update definitions", Time = "A few minutes.", Changes = "Updates Microsoft Defender's security intelligence.", HealthRow = HealthChecks.Defender },
        new ToolInfo("BackupKeyButton", ToolGroup.WindowsSecurity, "Back up recovery key", "Saves the BitLocker recovery key to your organization's Microsoft Entra ID.",
            "BitLocker", "recovery", "encryption", "key")
            { Action = ToolbeltAction.BackupRecoveryKey, Changes = "Saves the drive's existing recovery key to Microsoft Entra ID; nothing on the drive changes." },
        new ToolInfo("SyncClockButton", ToolGroup.WindowsSecurity, "Sync clock", "Sets the clock from its time source.",
            "clock", "time", "date", "w32tm")
            { Action = ToolbeltAction.SyncClock, Changes = "Sets the clock from its existing time source.", HealthRow = HealthChecks.Clock },
        // Read-only; it says why Windows does not update and which next step fits, and never repairs.
        new ToolInfo("CheckWuButton", ToolGroup.WindowsSecurity, WuCheck.Name, "Reads why Windows does not update: the last update, failures, policies, services, a waiting restart and disk space. Changes nothing.",
            "Windows Update", "update", "updates", "patch", "cumulative", "WSUS", "policy", "pause", "check")
            { Action = ToolbeltAction.CheckWindowsUpdate },
        new ToolInfo("RepairWuButton", ToolGroup.WindowsSecurity, "Repair Windows Update", "Checks Windows Update and repairs what is broken. Can take up to about two hours.",
            "Windows Update", "update", "updates", "patch", "cumulative")
            { Action = ToolbeltAction.RepairWU, Heavy = true, Time = "Seconds when nothing is wrong; usually 15 to 60 minutes for a repair or an install.", Changes = "Only the repair that fits what it finds; it may install updates and ask for a restart.", HealthRow = HealthChecks.PendingRestart },
        new ToolInfo("ConnectivityButton", ToolGroup.NetworkStorage, ConnectivityTest.Name, "Checks whether device management, work account sign-in, device registration, download optimization and Outlook on the web answer this PC over a secure connection.",
            "network", "connect", "connection", "connectivity", "internet", "proxy", "VPN", "firewall", "offline", "Intune", "Outlook")
            { CheckName = ConnectivityTest.Name, ButtonText = "Check connections", PolicyName = ToolPolicy.CheckConnections, EarlierLogKeys = new[] { RunLogStore.KeyFor(ConnectivityTest.EarlierName) }, HealthRow = HealthView.Network },
        new ToolInfo("WindowBackButton", ToolGroup.WindowsSecurity, WindowRescue.Name, "Moves an open app window that is off-screen, for example after undocking, back onto your screen.",
            "window", "off-screen", "offscreen", "monitor", "screen", "display", "dock", "undock", "missing", "lost")
            { CheckName = WindowRescue.Name, PolicyName = ToolPolicy.BringWindowBack, OpensPage = true, ButtonText = "Choose a window" },
        // The read-only check of "My device is blocked or 'not compliant'"; it runs in the app.
        new ToolInfo("ComplianceButton", ToolGroup.WindowsSecurity, CompliancePath.CheckName, "Reads the settings compliance policies usually check on this device and gives each one that is off its next step. It cannot see the compliance verdict.",
            "compliance", "compliant", "not compliant", "blocked", "Conditional Access", "requirements", "BitLocker", "firewall", "Secure Boot", "TPM", "Company Portal")
            { CheckName = CompliancePath.CheckName, PolicyName = ToolPolicy.CheckCompliance, ButtonText = "Check settings" },
        new ToolInfo("FreeSpaceButton", ToolGroup.NetworkStorage, "Free up disk space", "Removes old temporary files and Windows components that newer updates replaced.",
            "disk", "storage", "space", "temp", "full", "update")
            { Action = ToolbeltAction.FreeDiskSpace, Time = "10 to 30 minutes.", Changes = "Removes old temporary files, the Delivery Optimization cache and Windows components that newer updates replaced.", HealthRow = HealthChecks.FreeSpace },
        // The user chooses the adapter on its own page; the service renews only that one.
        new ToolInfo("RenewIpButton", ToolGroup.NetworkStorage, IpRenewal.Name, "Asks the network for a new address for the adapter you choose, as when it has none or only 169.254.x.x.",
            "IP", "address", "DHCP", "lease", "renew", "ipconfig", "169.254", "APIPA", "network", "connect", "Wi-Fi", "Ethernet")
            { Action = ToolbeltAction.RenewIpAddress, OpensPage = true, ButtonText = "Choose an adapter", Changes = "Renews the chosen adapter's address; its connection drops for a moment.", HealthRow = HealthView.Network },
        // Microsoft's documented OneDrive reset, as the user, after a confirmation.
        new ToolInfo("ResetOneDriveButton", ToolGroup.NetworkStorage, OneDriveReset.ToolName, "Resets the OneDrive sync app, so every account syncs again from the start. Files stay where they are.",
            "OneDrive", "sync", "syncing", "pending", "upload", "red", "cross", "files", "reset", "Microsoft 365")
            { CheckName = OneDriveReset.ToolName, PolicyName = ToolPolicy.ResetOneDrive, NeedsOneDrive = true, ShortName = OneDriveReset.ShortName, Time = "A few minutes; the sync after it can take longer.",
              Changes = "Disconnects and reconnects every OneDrive account, work and personal; they sync again from the start. Files stay where they are." },
    };

    /// <summary>The safe-mode tool of one Office app, in Sign-in &amp; Microsoft 365.</summary>
    private static ToolInfo SafeModeTool(OfficeApp app, params string[] words)
    {
        var name = OfficeSafeMode.AppName(app);
        var keywords = words.Concat(OfficeWords).ToArray();
        return new ToolInfo(OfficeSafeMode.Button(app), ToolGroup.SignIn, OfficeSafeMode.ToolName(app),
            "Starts " + name + " without add-ins and with default settings, to see whether " + name + " works that way.", keywords)
            // The row and the detail page already name the app; the button names the action.
            { CheckName = OfficeSafeMode.ToolName(app), Office = app, ShortName = name, PolicyName = SafeModePolicyName(app), ButtonText = "Start in safe mode" };
    }

    /// <summary>The policy name of an Office app's safe-mode tool.</summary>
    private static string SafeModePolicyName(OfficeApp app) => app switch
    {
        OfficeApp.Word => ToolPolicy.WordSafeMode,
        OfficeApp.Excel => ToolPolicy.ExcelSafeMode,
        OfficeApp.Outlook => ToolPolicy.OutlookSafeMode,
        _ => ToolPolicy.PowerPointSafeMode,
    };

    /// <summary>The tool is offered: the organization did not switch it off (<paramref name="switchedOff"/> null: none is off).</summary>
    public static bool Offered(ToolInfo tool, IEnumerable<string> switchedOff)
        => tool != null && !ToolPolicy.IsOff(switchedOff, tool.PolicyName);

    /// <summary>The tool with this button is offered; false for a button that is no tool.</summary>
    public static bool Offered(string button, IEnumerable<string> switchedOff) => Offered(ByButton(button), switchedOff);

    /// <summary>The tool a health row's fix link presses, or null (Retry work apps and settings is no tool of For IT and cannot be switched off).</summary>
    public static ToolInfo ForFix(HealthFix fix) => fix switch
    {
        HealthFix.ConnectivityTest => ByButton("ConnectivityButton"),
        HealthFix.SyncClock => ByButton("SyncClockButton"),
        HealthFix.UpdateDefender => ByButton("UpdateDefenderButton"),
        HealthFix.FreeDiskSpace => ByButton("FreeSpaceButton"),
        HealthFix.RefreshSignIn => ByButton("RefreshSignInButton"),
        _ => null,
    };

    /// <summary>The fix presses a tool the organization switched off.</summary>
    public static bool FixOff(HealthFix fix, IEnumerable<string> switchedOff) => ForFix(fix) is ToolInfo tool && !Offered(tool, switchedOff);

    /// <summary>The groups that have at least one tool on; a group whose tools are all off shows no tile.</summary>
    public static bool GroupOffered(ToolGroup group, IEnumerable<string> switchedOff) => InGroup(group, switchedOff).Count > 0;

    /// <summary>A group's heading as the window shows it, with its number of tools (the ones on).</summary>
    public static string Heading(ToolGroup group, IEnumerable<string> switchedOff = null) => GroupName(group) + " (" + InGroup(group, switchedOff).Count.ToString(CultureInfo.InvariantCulture) + ")";

    public static string GroupName(ToolGroup group) => group switch
    {
        ToolGroup.Apps => "Apps",
        ToolGroup.SignIn => "Sign-in & Microsoft 365",
        ToolGroup.WindowsSecurity => "Windows & security",
        _ => "Network & storage",
    };

    public static ToolInfo ByButton(string button) => All.FirstOrDefault(t => t.Button == button);

    /// <summary>The tools of one group, in the order the window shows them, without the ones the organization switched off.</summary>
    public static List<ToolInfo> InGroup(ToolGroup group, IEnumerable<string> switchedOff = null) => All.Where(t => t.Group == group && Offered(t, switchedOff)).ToList();

    /// <summary>The tool whose run logs carry this name, or null.</summary>
    public static ToolInfo ByLogKey(string key) => string.IsNullOrEmpty(key) ? null : All.FirstOrDefault(t => t.OwnsLogKey(key));

    /// <summary>The tool a service action belongs to, or null (Retry work apps and settings, the support package and the app diagnosis are not tools of For IT).</summary>
    public static ToolInfo ByAction(ToolbeltAction action) => All.FirstOrDefault(t => t.Action == action);

    /// <summary>The tool that runs in the app under this check name, or null.</summary>
    public static ToolInfo ByCheckName(string name) => string.IsNullOrEmpty(name) ? null : All.FirstOrDefault(t => t.CheckName == name);

    /// <summary>
    /// The facts a tool's detail page shows: its typical time and what it closes or changes,
    /// each only when it says something; none shows no facts row. There is no admin-rights fact:
    /// no tool ever needs admin rights.
    /// </summary>
    public static List<(string Label, string Text)> Facts(ToolInfo tool)
    {
        var facts = new List<(string Label, string Text)>();
        if (tool.Time.Length > 0) { facts.Add(("TYPICAL TIME", tool.Time)); }
        if (tool.Changes.Length > 0) { facts.Add(("CLOSES OR CHANGES", tool.Changes)); }
        return facts;
    }

    /// <summary>
    /// The tools a query finds, best first: every word of the query must start a word of the tool's
    /// name, line, group or one of its aliases; a tool whose name matches comes before one that
    /// matches only elsewhere. An empty query finds every tool.
    /// </summary>
    public static List<ToolInfo> Search(string query, IEnumerable<string> switchedOff = null)
    {
        // A tool the organization switched off is never found.
        var tools = All.Where(t => Offered(t, switchedOff)).ToList();
        var terms = WordsOf(query).ToList();
        if (terms.Count == 0) { return tools; }
        return tools
            .Select((tool, index) => (Tool: tool, Index: index, Score: Score(tool, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => x.Tool)
            .ToList();
    }

    private static int Score(ToolInfo tool, List<string> terms)
    {
        var name = WordsOf(tool.Name).ToList();
        var other = WordsOf(tool.Line).Concat(WordsOf(GroupName(tool.Group))).Concat(tool.Keywords.SelectMany(WordsOf)).ToList();
        // The symptom words of the problem rows that lead to the tool find it too ("pending", "offline",
        // "monitor"), after a tool its own words find.
        var symptoms = ProblemPaths.LeadingTo(tool.Button).SelectMany(p => WordsOf(p.Title).Concat(WordsOf(p.Subtitle))).ToList();
        var score = 0;
        foreach (var term in terms)
        {
            if (name.Any(w => w.StartsWith(term, StringComparison.Ordinal))) { score += 4; }
            else if (other.Any(w => w.StartsWith(term, StringComparison.Ordinal))) { score += 2; }
            else if (symptoms.Any(w => w.StartsWith(term, StringComparison.Ordinal))) { score += 1; }
            else { return 0; }
        }
        return score;
    }

    /// <summary>The words of a text, lower case: letters and digits; "sign-in" is one word and "sign" and "in" too.</summary>
    private static IEnumerable<string> WordsOf(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { yield break; }
        var current = new System.Text.StringBuilder();
        var hyphenated = new System.Text.StringBuilder();
        foreach (var raw in text + " ")
        {
            var c = char.ToLowerInvariant(raw);
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
                hyphenated.Append(c);
                continue;
            }
            if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
            if (c == '-' && hyphenated.Length > 0) { hyphenated.Append(c); continue; }
            if (hyphenated.Length > 0)
            {
                var whole = hyphenated.ToString().TrimEnd('-');
                if (whole.IndexOf('-') > 0) { yield return whole; }
                hyphenated.Clear();
            }
        }
    }
}

/// <summary>
/// The problems of "Choose a problem" (equal rows, from most to least common). The
/// order of the values is the order of the rows.
/// </summary>
public enum Problem
{
    AppWontInstall,
    CantSignIn,
    CantConnect,

    /// <summary>Shown only when at least one supported Office app is installed.</summary>
    OfficeCloses,

    /// <summary>Microsoft's documented OneDrive reset.</summary>
    OneDriveSync,

    /// <summary>"My device is blocked or 'not compliant'" (<see cref="CompliancePath"/>); a path of Choose a problem, not a Home tile.</summary>
    DeviceBlocked,

    WindowsWontUpdate,

    /// <summary>Bring my window back; its row opens the window page.</summary>
    WindowOffScreen,
}

/// <summary>What one problem path shows: its health rows and the tools it leads to.</summary>
public sealed class ProblemPath
{
    internal ProblemPath(Problem problem, string title, string subtitle, string intro, string[] tools, params string[] rows)
    {
        Problem = problem;
        Title = title;
        Subtitle = subtitle;
        Intro = intro;
        Tools = tools;
        Rows = rows;
    }

    public Problem Problem { get; }

    /// <summary>The row's title on Choose a problem, the path page's title, and "Copy details for IT: &lt;title&gt;".</summary>
    public string Title { get; }

    /// <summary>The row's one-line subtitle on Choose a problem.</summary>
    public string Subtitle { get; }

    public string Intro { get; }

    /// <summary>The row's automation name, "&lt;title&gt;. &lt;subtitle&gt;".</summary>
    public string AccessibleName => Title + ". " + Subtitle;

    /// <summary>
    /// The tools this problem leads to, by their button in "For IT" (each tool lives there only; the
    /// path links to it). The app path and the window path have their own pages and list none.
    /// </summary>
    public IReadOnlyList<string> Tools { get; }

    /// <summary>
    /// The tools the For IT search finds by this row's symptom words: its tools, or, for the two
    /// rows with a page of their own, the tools of that page.
    /// </summary>
    public IReadOnlyList<string> SearchTools { get; internal set; }

    /// <summary>The health rows (<see cref="HealthView"/> names) this problem looks at.</summary>
    public IReadOnlyList<string> Rows { get; }
}

public static class ProblemPaths
{
    /// <summary>The rows of Choose a problem in their order, with their titles and subtitles.</summary>
    public static readonly IReadOnlyList<ProblemPath> All = new[]
    {
        new ProblemPath(Problem.AppWontInstall, "An app from Company Portal won't install", "Stuck, failed, or not in the list",
            "Shows what Intune recorded for your apps. Nothing is changed.",
            Array.Empty<string>(), HealthView.AppInstaller, HealthView.IntuneConnection, HealthChecks.FreeSpace, HealthChecks.PendingRestart)
            { SearchTools = new[] { "ClearCacheButton", "LogsButton", "FullResetButton", "PortalButton" } },
        new ProblemPath(Problem.CantSignIn, "Teams or Office keeps asking me to sign in", "Or says your work account has a problem",
            "Your work account, the clock (a wrong time breaks sign-in) and the connection to the sign-in service.",
            // Three tools at most; Sync clock and Check Microsoft connections are the fix links of the Clock and
            // network rows above them, and the search still finds all five by the row's words.
            new[] { "RefreshSignInButton", "ResetSignInButton", "TeamsCacheButton" }, HealthView.WorkAccount, HealthChecks.Clock, HealthView.Network)
            { SearchTools = new[] { "RefreshSignInButton", "ResetSignInButton", "TeamsCacheButton", "SyncClockButton", "ConnectivityButton" } },
        new ProblemPath(Problem.CantConnect, "No internet, or work apps can't connect", "Wi-Fi, network cable, or Teams and Outlook stay offline",
            "Whether this device reaches the Intune and Microsoft sign-in services, directly and through a proxy.",
            new[] { "ConnectivityButton", "RenewIpButton" }, HealthView.Network, HealthView.IntuneConnection),
        new ProblemPath(Problem.OfficeCloses, OfficeSafeMode.ProblemTitle, OfficeSafeMode.ProblemSubtitle,
            "Safe mode starts the app without add-ins and with default settings, for one start. Nothing is changed.",
            OfficeSafeMode.Apps.Select(OfficeSafeMode.Button).ToArray()),
        new ProblemPath(Problem.OneDriveSync, OneDriveReset.ProblemTitle, OneDriveReset.ProblemSubtitle,
            OneDriveReset.ResumeHint,
            new[] { "ResetOneDriveButton" }),
        // Its rows are its own (CompliancePath.Rows), one per item the device can read itself.
        new ProblemPath(Problem.DeviceBlocked, CompliancePath.Title, CompliancePath.Subtitle, CompliancePath.NotKnown,
            new[] { "ComplianceButton", "UpdateDefenderButton", "RepairWuButton" }, CompliancePath.RowNames.ToArray()),
        new ProblemPath(Problem.WindowsWontUpdate, "Windows Update keeps failing", "Or the same update comes back every time",
            "Check Windows Update reads why without changing anything and names the next step.",
            new[] { "CheckWuButton", "RepairWuButton", "FreeSpaceButton" }, HealthChecks.FreeSpace, HealthChecks.PendingRestart, HealthView.Network),
        new ProblemPath(Problem.WindowOffScreen, WindowRescue.ProblemTitle, WindowRescue.ProblemSubtitle,
            "Choose the window you can't see. Only that window is moved onto your screen; nothing else changes.",
            Array.Empty<string>())
            { SearchTools = new[] { "WindowBackButton" } },
    };

    /// <summary>The four problems Home shows as tiles under "Something not working?", with the same titles.</summary>
    public static readonly IReadOnlyList<Problem> HomeTiles = new[] { Problem.AppWontInstall, Problem.CantSignIn, Problem.CantConnect, Problem.WindowsWontUpdate };

    /// <summary>The paths Home shows as tiles (the compliance path is on Choose a problem only).</summary>
    public static IReadOnlyList<ProblemPath> OnHome => HomeTiles.Select(Get).ToList();

    /// <summary>The Office path (kept for the callers that name it).</summary>
    public static ProblemPath Office => Get(Problem.OfficeCloses);

    /// <summary>The usual install blockers, which "An app won't install" shows above its app list.</summary>
    public static readonly IReadOnlyList<string> InstallBlockers = new[] { HealthChecks.FreeSpace, HealthChecks.PendingRestart };

    public static ProblemPath Get(Problem problem) => All.First(p => p.Problem == problem);

    /// <summary>The rows whose symptom words find this tool in the For IT search.</summary>
    public static IEnumerable<ProblemPath> LeadingTo(string button) => All.Where(p => (p.SearchTools ?? p.Tools).Contains(button));

    /// <summary>
    /// The tools a path shows now: the safe-mode tools only for an installed app, and Get a new IP
    /// address on "I can't connect" only while an adapter needs a new address. <paramref name="offered"/>
    /// answers that per tool button. Never a tool the organization switched off.
    /// </summary>
    public static List<string> ToolsShown(ProblemPath path, Func<string, bool> offered, IEnumerable<string> switchedOff = null)
        => path.Tools.Where(b => (offered == null || offered(b)) && ToolCatalog.Offered(b, switchedOff)).ToList();

    /// <summary>
    /// A path shows its checks first, then at most this many tools as rows. The Office path is the
    /// one exception: one row per installed Office app (up to four), the same safe-mode tool for each app.
    /// </summary>
    public const int MaxTools = 3;

    /// <summary>
    /// A path's verdict from its health rows: what needs attention, or that these checks found
    /// nothing (the path still leads to its tools when everything is green). Once Check Windows
    /// Update has run, "Windows Update keeps failing" says what it found and its step first
    /// (<paramref name="wuCheck"/>), never "no problem" next to a check that found one.
    /// </summary>
    public static (string Text, Tone Tone) Verdict(ProblemPath path, IReadOnlyList<HealthResult> rows, WuPathCheck wuCheck = null)
    {
        // Its own rows (CompliancePath.Rows) and its own words: never a verdict of compliance.
        if (path.Problem == Problem.DeviceBlocked) { return CompliancePath.Verdict(rows); }
        var check = path.Problem == Problem.WindowsWontUpdate ? wuCheck : null;
        if (check is { Clean: false }) { return (check.Text, check.Tone); }
        var mine = (rows ?? Array.Empty<HealthResult>()).Where(r => path.Rows.Contains(r.Name)).ToList();
        if (mine.Count == 0) { return ("These checks have not run yet. Use Check this PC again on Home.", Tone.Neutral); }
        var fails = mine.Where(r => r.Status == HealthStatus.Fail).ToList();
        var warns = mine.Where(r => r.Status == HealthStatus.Warn).ToList();
        if (fails.Count > 0) { return ("Problem: " + string.Join(", ", fails.Select(r => r.Name)) + ". " + Hint(fails[0]), Tone.Error); }
        if (warns.Count > 0) { return ("To check: " + string.Join(", ", warns.Select(r => r.Name)) + ". " + Hint(warns[0]), Tone.Warning); }
        if (check != null) { return (WuCheck.Name + " and these checks found no problem. If it still does not work, copy details for IT.", Tone.Success); }
        return ("These checks found no problem. If it still does not work, the tools below may help, or copy details for IT.", Tone.Success);
    }

    private static string Hint(HealthResult row) => string.IsNullOrEmpty(row.HintBelow) ? "" : row.HintBelow;
}

/// <summary>What the primary button of Home does.</summary>
public enum HomeStep
{
    None,

    /// <summary>The app finding's own step (<see cref="HomeFinding.AppStep"/>).</summary>
    App,

    /// <summary>A health row's fix (<see cref="HomeFinding.Fix"/>).</summary>
    HealthFix,
}

/// <summary>The link beside Check again on Home's status card: only one that fits the finding.</summary>
public enum HomeLink
{
    None,

    /// <summary>Review affected apps: for an app finding, the app installer or the Intune connection.</summary>
    ReviewApps,

    /// <summary>Check my apps: the same app check when nothing was found (or the checks still run), in words that claim no problem.</summary>
    CheckApps,

    /// <summary>View failed settings: opens the Configuration profiles row with its failed settings.</summary>
    ViewFailedSettings,
}

/// <summary>Home: one main finding and at most one primary next step.</summary>
public sealed class HomeFinding
{
    /// <summary>The link beside Check again; none when no link fits the finding.</summary>
    public HomeLink Link { get; set; } = HomeLink.ReviewApps;

    /// <summary>
    /// The step is the dark primary button. False where Retry work apps and settings is unlikely to help
    /// (configuration profiles): then Check again is the primary button and the step a plain link.
    /// </summary>
    public bool StepIsPrimary { get; set; } = true;

    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public Tone Tone { get; set; }
    public HomeStep Step { get; set; }
    public AppNextStep AppStep { get; set; }
    public HealthFix Fix { get; set; }

    /// <summary>The primary button's text; empty when there is no step to press.</summary>
    public string StepText { get; set; } = "";

    /// <summary>The app finding Home shows (the same object "An app won't install" shows); null for a health finding.</summary>
    public AppFinding App { get; set; }

    /// <summary>The health row (a <see cref="HealthView"/> name) a health finding is about; null otherwise.</summary>
    public string Row { get; set; }

    /// <summary>The row's status (fail or warn) for a health finding.</summary>
    public HealthStatus RowStatus { get; set; }

    /// <summary>The row's own plain title and line for this state (<see cref="HealthResult.HomeTitle"/>); empty for the row's usual words.</summary>
    public string RowTitle { get; set; } = "";
    public string RowLine { get; set; } = "";

    /// <summary>The row is about the missing app installer (IME), which no action here installs.</summary>
    public bool RowImeNotInstalled { get; set; }

    /// <summary>The service cannot be reached.</summary>
    public bool ServiceMissing { get; set; }
}

public static class HomeSummary
{
    /// <summary>
    /// The main finding: the app diagnosis's own finding when one ran and something needs attention
    /// (the user asked about apps), otherwise the worst health row, otherwise that nothing was found.
    /// The service being offline comes first: then nothing else is known.
    /// </summary>
    /// <param name="switchedOff">The tools the organization switched off; Home never offers one as its step.</param>
    public static HomeFinding Build(IReadOnlyList<HealthResult> rows, AppDiagnosis diagnosis, bool serviceMissing, IEnumerable<string> switchedOff = null)
    {
        if (serviceMissing)
        {
            return new HomeFinding
            {
                Title = "The " + ProductInfo.DisplayName + " service is not available",
                Text = "The checks that need it, the app diagnosis and the repairs cannot run. Restart the device; if it stays like this, copy details for IT and send it to your IT support.",
                Tone = Tone.Error,
                Step = HomeStep.App,
                AppStep = AppNextStep.CopyForIt,
                StepText = StepText(AppNextStep.CopyForIt),
                Link = HomeLink.None,
                ServiceMissing = true,
            };
        }
        var app = diagnosis?.Main;
        if (app != null)
        {
            var choose = app.SameNameAsAnother;
            var step = choose ? AppNextStep.ChooseApp : app.Next;
            // No step that points to a tool the organization switched off.
            if (step == AppNextStep.ConnectivityTest && ToolCatalog.FixOff(HealthFix.ConnectivityTest, switchedOff)) { step = AppNextStep.None; }
            return new HomeFinding
            {
                Title = choose ? "More than one app is called " + app.App.Label : app.Title,
                Text = choose ? "Open the list and choose the one you mean; each has its own next step." : app.Text,
                Tone = app.Severity == FindingSeverity.Error ? Tone.Error : app.AfterRemedy == PostCheckState.AwaitingEvidence ? Tone.Neutral : Tone.Warning,
                Step = HomeStep.App,
                AppStep = step,
                // Home names Retry work apps and settings in plain words; the app page keeps its name.
                StepText = step == AppNextStep.RetryAppsAndPolicies ? Words.RetryStep : StepText(step),
                App = app,
            };
        }
        var list = rows ?? Array.Empty<HealthResult>();
        var worst = list.FirstOrDefault(r => r.Status == HealthStatus.Fail) ?? list.FirstOrDefault(r => r.Status == HealthStatus.Warn);
        if (worst != null)
        {
            // No fix that presses a tool the organization switched off.
            var fix = ToolCatalog.FixOff(worst.Fix, switchedOff) ? HealthFix.None : worst.Fix;
            return new HomeFinding
            {
                Title = worst.Name + ": " + worst.StatusWord,
                Text = string.IsNullOrEmpty(worst.HintBelow) ? worst.Detail : worst.HintBelow,
                Tone = worst.Status == HealthStatus.Fail ? Tone.Error : Tone.Warning,
                Step = fix == HealthFix.None ? HomeStep.None : HomeStep.HealthFix,
                Fix = fix,
                StepText = Words.FixText(fix),
                Link = LinkFor(worst.Name),
                Row = worst.Name,
                RowStatus = worst.Status,
                RowTitle = worst.HomeTitle ?? "",
                RowLine = worst.HomeLine ?? "",
                RowImeNotInstalled = worst.ImeNotInstalled,
                StepIsPrimary = fix != HealthFix.RetryAppsAndPolicies || RetryHelps(worst.Name),
            };
        }
        var appsChecked = diagnosis != null && diagnosis.Run == DiagnosisRunState.Finished;
        return new HomeFinding
        {
            // The title claims only what was checked, never "Everything looks fine" before the
            // app diagnosis ran; the line says what was not checked and the way in for a
            // complaint that is still there.
            Title = list.Count == 0 ? "Checking this device..." : CleanTitle,
            Text = list.Count == 0 ? "" : appsChecked ? CleanAppsChecked : CleanAppsNotChecked,
            Tone = list.Count == 0 ? Tone.Neutral : Tone.Success,
            Link = HomeLink.CheckApps,
        };
    }

    /// <summary>Home's title when the checks found nothing: it claims only these checks.</summary>
    public const string CleanTitle = "No problems found in these checks";

    /// <summary>The line under it once the app check ran too.</summary>
    public const string CleanAppsChecked = "No app needs attention either. Still a problem? Choose it below.";

    /// <summary>The line under it while the apps were not checked.</summary>
    public const string CleanAppsNotChecked = "Your apps are not checked yet: choose Check my apps. Still a problem? Choose it below.";

    /// <summary>
    /// Retry work apps and settings is the primary button only where it can help: the app installer
    /// (IME) and the Intune connection (and an app finding, whose own step it is). For configuration
    /// profiles a sync is unlikely to help, so Check again is primary and Retry a plain link.
    /// </summary>
    public static bool RetryHelps(string row) => row is HealthView.AppInstaller or HealthView.IntuneConnection;

    /// <summary>The link that fits a health finding: Review affected apps for the rows about apps, View failed settings for profiles, else none.</summary>
    public static HomeLink LinkFor(string row) => row switch
    {
        HealthView.AppInstaller or HealthView.IntuneConnection => HomeLink.ReviewApps,
        HealthChecks.Profiles => HomeLink.ViewFailedSettings,
        _ => HomeLink.None,
    };

    /// <summary>
    /// Words a person who knows nothing about computers does not know; never on Home's first
    /// screen (its title and line). They stay in the rows' details, the help texts and For IT support.
    /// </summary>
    public static readonly IReadOnlyList<string> TechnicalWords = new[] { "IME", "MDM", "PRT", "Intune", "Entra", "Management Extension", "OMA", "CSP", "dsreg", "0x" };

    /// <summary>The text holds one of <see cref="TechnicalWords"/> (as a word; "0x" as the start of a code).</summary>
    public static bool HasTechnicalWords(string text)
    {
        if (string.IsNullOrEmpty(text)) { return false; }
        foreach (var word in TechnicalWords)
        {
            for (var at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
            {
                var before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
                var end = at + word.Length;
                var after = word == "0x" || end >= text.Length || !char.IsLetter(text[end]);
                if (before && after) { return true; }
            }
        }
        return false;
    }

    /// <summary>
    /// Home's title in plain words: a health finding by what it means for the user ("Your PC needs
    /// a restart"), an app finding by its own title unless that names a technical word, the rest as built.
    /// </summary>
    public static string PlainTitle(HomeFinding finding)
    {
        if (finding == null) { return ""; }
        if (finding.Row != null) { return finding.RowTitle.Length > 0 ? finding.RowTitle : PlainRow(finding.Row, finding.RowStatus, finding.RowImeNotInstalled).Title; }
        if (finding.App != null && HasTechnicalWords(finding.Title))
        {
            var label = finding.App.App?.Label;
            return string.IsNullOrEmpty(label) ? "An app needs attention" : label + ": needs attention";
        }
        return finding.Title;
    }

    /// <summary>
    /// Home's one line in plain words: for a health finding what it means; for an app finding its
    /// next step's first sentence; otherwise the finding's first sentence; empty when that line would
    /// name a technical word.
    /// </summary>
    public static string PlainText(HomeFinding finding)
    {
        if (finding == null) { return ""; }
        if (finding.Row != null) { return finding.RowTitle.Length > 0 ? finding.RowLine : PlainRow(finding.Row, finding.RowStatus, finding.RowImeNotInstalled).Line; }
        var text = finding.App != null && !string.IsNullOrEmpty(finding.App.NextText) ? finding.App.NextText : finding.Text;
        var line = HealthResult.FirstSentence(text ?? "");
        return HasTechnicalWords(line) ? "" : line;
    }

    /// <summary>
    /// A health row's plain title and line on Home, by its status (a problem, or one to check). While the
    /// app installer (IME) is not installed no action here helps, so its row never asks for a new try.
    /// </summary>
    public static (string Title, string Line) PlainRow(string row, HealthStatus status, bool imeNotInstalled = false)
    {
        if (imeNotInstalled)
        {
            return ("Work apps can't be installed yet", "Your organization sets this up with the first work app it sends, so there is nothing to do on this PC.");
        }
        var fail = status == HealthStatus.Fail;
        return row switch
        {
            HealthView.IntuneConnection => (fail ? "This PC has lost its link to your organization" : "This PC has not checked in with your organization lately",
                "Work apps and settings may not arrive until this is fixed."),
            HealthView.AppInstaller => (fail ? "Work apps can't be installed right now" : "Work apps may install slowly right now",
                "The part of Windows that installs work apps needs a new try."),
            HealthView.WorkAccount => (fail ? "Your work account has a sign-in problem" : "Your work account needs a check",
                "Teams, Outlook and other work apps may keep asking you to sign in."),
            // The row's own words come from the row (WorkAccountRows.Hello); this is the fallback.
            HealthChecks.WindowsHello => ("Windows Hello needs a check", "Your IT support can see what is missing."),
            HealthView.Network => (fail ? "This PC can't reach work services" : "This PC has trouble reaching work services",
                "Check your Wi-Fi or network cable first."),
            HealthChecks.Profiles => (fail ? "Some work settings were not applied" : "Some work settings need a check",
                "Your IT support can see which ones."),
            HealthChecks.FreeSpace => (fail ? "Your disk is almost full" : "Your disk is getting full",
                "Free up space so apps and updates can install."),
            HealthChecks.PendingRestart => ("Your PC needs a restart",
                "Restart when you can, so updates and apps can finish."),
            HealthChecks.Clock => (fail ? "Your PC's clock is wrong" : "Your PC's clock is a bit off",
                "A wrong time can stop you from signing in."),
            HealthChecks.Defender => (fail ? "Virus protection needs attention" : "Virus protection needs a check",
                "Its protection may be out of date."),
            _ => (HealthView.PlainName(row) + (fail ? ": a problem" : ": " + Words.AWarning), ""),
        };
    }

    /// <summary>
    /// The severity word of Home's status card, written out so the coloured edge never
    /// carries it alone: "PROBLEM" for an error, "WARNING" for a warning (the word a row's badge and the count use), none otherwise.
    /// </summary>
    public static string SeverityWord(HomeFinding finding) => finding?.Tone switch
    {
        Tone.Error => "PROBLEM",
        Tone.Warning => Words.WarningBadge,
        _ => "",
    };

    /// <summary>The app page's button that reads the app again, and Home's step of the same name.</summary>
    public const string CheckAppAgain = "Check this app again";

    /// <summary>The primary button's text for an app step; empty for a step that is not a button.</summary>
    public static string StepText(AppNextStep step) => step switch
    {
        AppNextStep.CopyForIt => "Copy details for IT",
        AppNextStep.ConnectivityTest => "Check Microsoft connections",
        AppNextStep.RetryAppsAndPolicies => "Retry work apps and settings",
        AppNextStep.OpenCompanyPortal => "Open in Company Portal",
        AppNextStep.ChooseApp => "Choose the app",
        AppNextStep.CheckAgain => CheckAppAgain,
        _ => "",
    };
}
