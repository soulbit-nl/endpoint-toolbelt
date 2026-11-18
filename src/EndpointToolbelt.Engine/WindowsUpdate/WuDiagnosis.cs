using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.WindowsUpdate;

/// <summary>
/// Windows Update: diagnosis and targeted repair.
///
/// Diagnosis first, in seconds and without changes: is the device on the quality update it should
/// have by now, which update failures were logged since the last successful one, and what does the
/// device itself say about its update source and policy. Only then does a repair run, and only the
/// one that addresses what was found. A fixed ladder (cache reset, DISM and SFC on every device)
/// repairs a component store that was never broken on most devices, and cannot fix the failures
/// that need something else (a full transaction log, a delivery path).
///
/// One instance per run. The steps are virtual, so the tests can replace a step; the steps call each other
/// through these methods.
/// </summary>
public partial class WindowsUpdateRepair
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public WindowsUpdateRepair(RunContext run)
    {
        Run = run ?? throw new ArgumentNullException(nameof(run));
    }

    public RunContext Run { get; }

    /// <summary>Set when a worker had to be stopped: no further in-process Windows Update API calls on this run.</summary>
    public bool ApiHung { get; set; }

    /// <summary>Set as soon as a step ran that needs a restart, so the close-out still marks it when a later step fails.</summary>
    public bool RestartOwed { get; set; }

    /// <summary>Set when a step changed the device, so the close-out still starts the cooldown when a later step fails.</summary>
    public bool ChangedOwed { get; set; }

    /// <summary>Whether the servicing stack was still busy after the wait at the start of the repair step; null: not checked yet.</summary>
    public bool? ServicingBusy { get; set; }

    /// <summary>Set when the device list could not be read (an empty list then means unknown).</summary>
    public bool GhostListFailed { get; set; }

    /// <summary>How much of the end of CBS.log is read.</summary>
    public long CbsTailBytes { get; set; } = WuCodes.CbsTailBytes;

    protected EngineServices Services => Run.Services;

    private string SystemRoot => Services.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

    internal string SystemDrive => Services.GetEnvironmentVariable("SystemDrive") ?? "C:";

    internal string CbsLogPath => Path.Combine(SystemRoot, @"Logs\CBS\CBS.log");

    // --- Diagnosis ------------------------------------------------------------------------------

    /// <summary>The second Tuesday of a month.</summary>
    public static DateTime PatchTuesday(int year, int month)
    {
        var first = new DateTime(year, month, 1);
        var offset = (2 - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 7);
    }

    /// <summary>The newest entries of the Windows Update history (UTC dates). Local and read-only; throws when unreadable.</summary>
    public virtual IReadOnlyList<WuHistoryEntry> HistoryEntries(int max) => Services.WindowsUpdate.History(max);

    /// <summary>Installed KBs with a date, newest first: survives a truncated update history.</summary>
    public virtual IReadOnlyList<WuHotFix> HotFixEntries() =>
        Services.WindowsUpdate.HotFixes()
            .Where(h => WuCodes.HotFixId.IsMatch(h.Id ?? "") && h.InstalledOn != null)
            .OrderByDescending(h => h.InstalledOn.Value).ToList();

    /// <summary>
    /// True for a Windows quality update title. forCurrency leaves out a standalone servicing stack
    /// update: it can install in a month whose cumulative update keeps failing, and must not make
    /// that device look current.
    /// </summary>
    public static bool IsQualityTitle(string title, bool forCurrency = false)
    {
        if (string.IsNullOrEmpty(title) || !WuCodes.QualityTitle.IsMatch(title) || WuCodes.NotQualityTitle.IsMatch(title)) { return false; }
        if (forCurrency && WuCodes.ServicingStackTitle.IsMatch(title) && !WuCodes.CumulativeOrHotpatchTitle.IsMatch(title)) { return false; }
        return true;
    }

    /// <summary>
    /// Is this device on the quality update it should have by now? Read from the install history,
    /// never from the build number (a hotpatch does not raise it). The patch month comes from the
    /// update title ("2026-08 Security Update ..."), because an update is often installed in the
    /// month after its release. deploymentDelayDays must be at least the update ring's deferral
    /// plus deadline plus grace period, or a device is judged before its ring offered the update.
    /// </summary>
    public virtual WuCurrency Currency(int deploymentDelayDays, DateTime? now = null)
    {
        var today = now ?? Services.Clock.Now.DateTime;
        var patchTuesday = PatchTuesday(today.Year, today.Month);
        var expected = new DateTime(today.Year, today.Month, 1);
        if (today.Date < patchTuesday.AddDays(deploymentDelayDays)) { expected = expected.AddMonths(-1); }

        DateTime? lastDate = null;
        string lastTitle = null;
        var source = "none";
        try
        {
            // Deep on purpose: definition updates arrive several times a day, so a shallow read
            // holds no quality update at all.
            var hit = HistoryEntries(500).Where(e => e.ResultCode == 2 && IsQualityTitle(e.Title, forCurrency: true))
                .OrderByDescending(e => e.DateUtc).FirstOrDefault();
            if (hit != null)
            {
                lastDate = hit.DateUtc;
                lastTitle = hit.Title;
                source = "history";
            }
        }
        catch (Exception ex)
        {
            Run.Log("The update history could not be read: " + ex.Message, LogSeverity.Warning);
        }
        if (lastDate == null)
        {
            try
            {
                var hotfixes = HotFixEntries();
                var hotfix = hotfixes.Count > 0 ? hotfixes[0] : null;
                if (hotfix != null)
                {
                    lastDate = DateTime.SpecifyKind(hotfix.InstalledOn.Value, DateTimeKind.Utc);
                    lastTitle = hotfix.Id;
                    source = "hotfix";
                }
            }
            catch (Exception ex)
            {
                Run.Log("The hotfix list could not be read: " + ex.Message, LogSeverity.Warning);
            }
        }

        DateTime? lastMonth = null;
        var monthMatch = lastTitle == null ? Match.Empty : WuCodes.TitleMonth.Match(lastTitle);
        if (monthMatch.Success)
        {
            var year = int.Parse(monthMatch.Groups[1].Value, Invariant);
            var month = int.Parse(monthMatch.Groups[2].Value, Invariant);
            if (month >= 1 && month <= 12) { lastMonth = new DateTime(year, month, 1); }
        }
        if (lastMonth == null && lastDate != null)
        {
            var date = lastDate.Value;
            lastMonth = new DateTime(date.Year, date.Month, 1);
            if (date.Date < PatchTuesday(date.Year, date.Month)) { lastMonth = lastMonth.Value.AddMonths(-1); }
        }

        int? behind = null;
        if (lastMonth != null) { behind = (expected.Year * 12 + expected.Month) - (lastMonth.Value.Year * 12 + lastMonth.Value.Month); }
        return new WuCurrency
        {
            ExpectedMonth = expected,
            LastPatchMonth = lastMonth,
            LastSuccessUtc = lastDate,
            LastTitle = lastTitle,
            MonthsBehind = behind,
            IsCurrent = behind != null && behind <= 0,
            Source = source,
        };
    }

    /// <summary>
    /// A numeric event value with the high bit set is an HRESULT (the client stores its error code
    /// as a number) and is returned as 0x........; any other value as text.
    /// </summary>
    public static string EventValueText(object value)
    {
        if (value is uint || value is int || value is long || value is ulong)
        {
            var number = Convert.ToInt64(value, Invariant);
            if (number < 0) { number += 4294967296L; }
            if (number >= 0x80000000L && number <= 0xFFFFFFFFL) { return "0x" + number.ToString("x8", Invariant); }
        }
        return MachineRegistry.AsText(value) ?? "";
    }

    /// <summary>
    /// Update failure events of the last 90 days, filtered by log, provider and ID: Windows Update
    /// client (Operational 20/25/31 and System 20/25; source "client") and servicing (Setup 3;
    /// source "setup"). Unreadable channels give nothing.
    /// </summary>
    private static readonly Tuple<string, string, string, int[]>[] FailureEventSources =
    {
        Tuple.Create("client", "Microsoft-Windows-WindowsUpdateClient/Operational", "Microsoft-Windows-WindowsUpdateClient", new[] { 20, 25, 31 }),
        Tuple.Create("client", "System", "Microsoft-Windows-WindowsUpdateClient", new[] { 20, 25 }),
        Tuple.Create("setup", "Setup", "Microsoft-Windows-Servicing", new[] { 3 }),
    };

    public virtual IReadOnlyList<WuFailureEvent> ReadFailureEvents()
    {
        var list = new List<WuFailureEvent>();
        var since = Services.Clock.UtcNow.AddDays(-90);
        foreach (var spec in FailureEventSources)
        {
            try
            {
                foreach (var record in Services.WindowsUpdate.Events(spec.Item2, spec.Item3, spec.Item4, since, 80))
                {
                    list.Add(new WuFailureEvent
                    {
                        TimeCreatedUtc = record.TimeCreatedUtc,
                        Values = record.Properties.Select(EventValueText).ToList(),
                        Message = record.Message ?? "",
                        Source = spec.Item1,
                    });
                }
            }
            catch (Exception ex)
            {
                Run.Log(string.Format(Invariant, "Could not read the {0} event log: {1}", spec.Item2, ex.Message), LogSeverity.Warning);
            }
        }
        return list;
    }

    /// <summary>
    /// Failure codes logged since the last successful quality update, split into Known (the codes
    /// that route), Unknown (unrecognised codes on an event that names a Windows update: a KB
    /// number or a quality-update title) and Ignored (codes on events that name no Windows update,
    /// such as a Store app in use; for the log only). Codes that appear on healthy devices are
    /// dropped, and 0x00000000 is not a failure. The Setup log can be written by any signed-in
    /// user, so a code seen only there routes nothing unless the Windows Update client channel
    /// (writable by the system only) or the update history confirms it; otherwise it is returned
    /// as Unconfirmed, for the log only.
    /// </summary>
    public virtual WuFailureCodes FailureCodes(DateTime? since)
    {
        var entries = new List<Tuple<string, bool, string>>();
        var confirmed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in ReadFailureEvents())
        {
            if (since != null && record.TimeCreatedUtc <= since.Value) { continue; }
            string code = null;
            var namesUpdate = false;
            foreach (var value in record.Values)
            {
                if (code == null && WuCodes.CodeValue.IsMatch(value ?? "")) { code = value.ToLowerInvariant(); }
                if (WuCodes.KbNumber.IsMatch(value ?? "") || WuCodes.QualityTitle.IsMatch(value ?? "")) { namesUpdate = true; }
            }
            if (code == null)
            {
                var inMessage = WuCodes.CodeInText.Match(record.Message ?? "");
                if (inMessage.Success) { code = inMessage.Groups[1].Value.ToLowerInvariant(); }
            }
            if (code == null || code == "0x00000000") { continue; }
            var source = string.IsNullOrEmpty(record.Source) ? "client" : record.Source;
            if (source != "setup") { confirmed.Add(code); }
            entries.Add(Tuple.Create(code, namesUpdate, source));
        }
        if (entries.Any(e => e.Item3 == "setup" && !confirmed.Contains(e.Item1)))
        {
            try
            {
                foreach (var entry in HistoryEntries(200))
                {
                    if (entry.ResultCode != 4 && entry.ResultCode != 5) { continue; }
                    if (since != null && entry.DateUtc <= since.Value) { continue; }
                    var key = CodeKnowledge.Key(entry.HResult.ToString(Invariant));
                    if (key != null) { confirmed.Add(key); }
                }
            }
            catch (Exception ex)
            {
                Run.Log("The update history could not be read to confirm Setup log codes: " + ex.Message, LogSeverity.Warning);
            }
        }

        var result = new WuFailureCodes();
        foreach (var entry in entries)
        {
            var code = entry.Item1;
            if (WuCodes.NoiseCodes.Contains(code)) { continue; }
            if (entry.Item3 == "setup" && !confirmed.Contains(code)) { result.Unconfirmed.Add(code); continue; }
            if (WuCodes.KnownCodes.Contains(code)) { result.Known.Add(code); }
            else if (entry.Item2) { result.Unknown.Add(code); }
            else { result.Ignored.Add(code); }
        }
        result.Known = result.Known.Distinct().ToList();
        result.Unknown = result.Unknown.Distinct().ToList();
        result.Ignored = result.Ignored.Distinct().ToList();
        result.Unconfirmed = result.Unconfirmed.Distinct().ToList();
        return result;
    }

    /// <summary>One finding per distinct failure code (at most four), with what the code table says.</summary>
    public virtual void AddCodeFindings(IEnumerable<string> codes, int max = 4)
    {
        var distinct = codes.Where(c => !string.IsNullOrEmpty(c)).Select(c => c.ToLowerInvariant()).Distinct().ToList();
        var shown = 0;
        foreach (var code in distinct)
        {
            if (shown >= max) { break; }
            var knowledge = CodeKnowledge.Get(code);
            if (knowledge != null)
            {
                Run.AddFinding(FindingLevel.Warning, "WU-CODE-" + code, string.Format(Invariant, "Update failure {0}: {1}", code, knowledge.Name),
                    "Logged since the last successful quality update. Meaning: " + knowledge.Meaning, knowledge.NextStep, knowledge.Support);
            }
            else
            {
                Run.AddFinding(FindingLevel.Warning, "WU-CODE-" + code, string.Format(Invariant, "Update failure {0}: not a recognised code", code),
                    "Logged for a Windows update since the last successful quality update. No documented meaning was found for it. This repair checks the component store for it.",
                    "If it returns, create a support package and contact your IT support.",
                    "The CBS log in the support package has the context of the code.");
            }
            shown++;
        }
        if (distinct.Count > shown)
        {
            Run.AddFinding(FindingLevel.Info, "WU-CODES-MORE", TextFormat.Count(distinct.Count - shown, "failure code") + " more in the log");
        }
    }

    /// <summary>True when a code of the update database (DataStore) class, 0x80248xxx, is among the codes.</summary>
    public static bool HasDataStoreCode(IEnumerable<string> codes) =>
        codes.Any(c => !string.IsNullOrEmpty(c) && WuCodes.DataStoreCode.IsMatch(c.ToLowerInvariant()));

    /// <summary>
    /// Disconnected (not present) Net and System class devices, for the diagnosis; an attached
    /// device is never selected. Which of them may be removed is decided by GhostAssessmentOf.
    /// When the list cannot be read, GhostListFailed says so (an empty list then means unknown).
    /// </summary>
    public virtual IReadOnlyList<PnpDeviceInfo> GhostDevices()
    {
        GhostListFailed = false;
        try
        {
            return Services.WindowsUpdate.Devices()
                .Where(d => !d.Present && (string.Equals(d.Class, "Net", StringComparison.OrdinalIgnoreCase) || string.Equals(d.Class, "System", StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        catch (Exception ex)
        {
            Run.Log("Could not list the devices: " + ex.Message, LogSeverity.Warning);
            GhostListFailed = true;
            return Array.Empty<PnpDeviceInfo>();
        }
    }

    /// <summary>
    /// The last lines of CBS.log (at most CbsTailBytes), read by handle without following a link.
    /// Null when the log cannot be read (missing, locked, a link).
    /// </summary>
    public virtual IReadOnlyList<string> ReadCbsTail(string path = null)
    {
        path ??= CbsLogPath;
        try
        {
            using var stream = NativeFile.OpenReadNoFollow(path);
            var skipFirst = false;
            if (stream.Length > CbsTailBytes)
            {
                stream.Seek(-CbsTailBytes, SeekOrigin.End);
                skipFirst = true;
            }
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>();
            // The first line after a seek is usually cut off.
            if (skipFirst) { reader.ReadLine(); }
            for (var line = reader.ReadLine(); line != null; line = reader.ReadLine()) { lines.Add(line); }
            return lines;
        }
        catch (Exception ex)
        {
            Run.Log("Could not read the servicing log (CBS.log): " + ex.Message, LogSeverity.Warning);
            return null;
        }
    }

    /// <summary>
    /// True when CBS.log shows the driver operations queue failing with 0x800719e4 after since
    /// (UTC; the diagnosis window: the last successful quality update, otherwise the last 90
    /// days). A line counts only with its own timestamp (CBS writes local time, "yyyy-MM-dd
    /// HH:mm:ss, ...") inside that window, so an old failure does not decide.
    /// </summary>
    public virtual bool DriverQueueSignature(DateTime? since)
    {
        var from = since?.ToUniversalTime() ?? Services.Clock.UtcNow.AddDays(-90);
        var lines = ReadCbsTail();
        if (lines == null) { return false; }
        foreach (var line in lines)
        {
            if (line.IndexOf("0x800719e4", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
            var hasPhrase = WuCodes.CbsQueuePhrases.Any(p => line.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!hasPhrase || line.Length < 19) { continue; }
            if (!DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", Invariant, DateTimeStyles.AssumeLocal, out var stamp)) { continue; }
            if (stamp.ToUniversalTime() <= from) { continue; }
            Run.PersistentLog("CBS.log: " + line.Trim());
            return true;
        }
        return false;
    }

    /// <summary>
    /// Decides whether disconnected devices may be removed on a 0x800719e4 failure. Removal is
    /// allowed only when every condition holds: CBS.log shows the driver operations queue failing
    /// with 0x800719e4 inside the diagnosis window; and at least GhostMinCount disconnected devices
    /// are of class Net, on the PCI or VMBUS bus, with the driver netvsc or mlx*. A USB, Bluetooth
    /// (BTH), software (SWD) or ROOT device, and any System class device, is never a candidate.
    /// </summary>
    public virtual GhostAssessment AssessGhosts(IReadOnlyList<PnpDeviceInfo> devices, DateTime? since)
    {
        var list = devices.Where(d => d != null).ToList();
        var result = new GhostAssessment();
        foreach (var device in list)
        {
            var id = device.InstanceId ?? "";
            var enumerator = id.Split('\\')[0].ToUpperInvariant();
            if (enumerator.Length == 0) { enumerator = "UNKNOWN"; }
            var index = result.ByEnumerator.FindIndex(p => p.Key == enumerator);
            if (index >= 0) { result.ByEnumerator[index] = new KeyValuePair<string, int>(enumerator, result.ByEnumerator[index].Value + 1); }
            else { result.ByEnumerator.Add(new KeyValuePair<string, int>(enumerator, 1)); }
            if (WuCodes.GhostNeverEnumerators.Contains(enumerator)) { continue; }
            if (!string.Equals(device.Class ?? "", "Net", StringComparison.OrdinalIgnoreCase)) { continue; }
            if (!WuCodes.GhostEnumerators.Contains(enumerator)) { continue; }
            if (!WuCodes.GhostService.IsMatch(device.Service ?? "")) { continue; }
            result.Candidates.Add(device);
        }
        if (list.Count > 0)
        {
            result.Signature = DriverQueueSignature(since);
            if (!result.Signature) { result.Reasons.Add("the servicing log (CBS.log) does not show the driver operations queue failing with 0x800719e4"); }
        }
        if (result.Candidates.Count < WuCodes.GhostMinCount)
        {
            result.Reasons.Add(string.Format(Invariant, "{0} of a virtual machine (class Net, PCI or VMBUS bus, driver netvsc or mlx); at least {1} are needed",
                TextFormat.Count(result.Candidates.Count, "disconnected network adapter"), WuCodes.GhostMinCount));
        }
        result.Eligible = result.Signature && result.Candidates.Count >= WuCodes.GhostMinCount;
        return result;
    }

    /// <summary>
    /// Baseline RollupFix packages left Staged (0x40) or InstallPending (0x60) below the newest
    /// Installed (0x70) one. Such a package alone is no evidence of a problem; it is only
    /// considered together with a servicing failure code.
    /// </summary>
    public virtual IReadOnlyList<StaleRollup> StaleRollupFixes()
    {
        try
        {
            const string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages";
            var packages = new List<StaleRollup>();
            foreach (var name in Services.Registry.SubKeyNames(root))
            {
                if (!name.StartsWith("Package_for_RollupFix", StringComparison.OrdinalIgnoreCase)) { continue; }
                object state;
                try { state = Services.Registry.GetValue(root + "\\" + name, "CurrentState"); }
                catch (Exception) { state = null; }
                if (state == null) { continue; }
                int? ubr = null;
                var match = WuCodes.PackageVersion.Match(name);
                if (match.Success) { ubr = int.Parse(match.Groups[2].Value, Invariant); }
                packages.Add(new StaleRollup { Name = name, Ubr = ubr, State = Convert.ToInt32(state, Invariant) });
            }
            var installed = packages.Where(p => p.State == 0x70 && p.Ubr != null).OrderByDescending(p => p.Ubr.Value).ToList();
            if (installed.Count == 0) { return Array.Empty<StaleRollup>(); }
            var highest = installed[0].Ubr.Value;
            return packages.Where(p => (p.State == 0x40 || p.State == 0x60) && p.Ubr != null && p.Ubr.Value < highest).ToList();
        }
        catch (Exception ex)
        {
            Run.Log("Could not read the servicing package inventory: " + ex.Message, LogSeverity.Warning);
            return Array.Empty<StaleRollup>();
        }
    }

    /// <summary>Free space on the system drive in GB, or null when it cannot be read.</summary>
    public virtual double? SystemDriveFreeGB()
    {
        var bytes = Services.FreeBytes(SystemDrive);
        return bytes == null ? null : Math.Round(bytes.Value / 1073741824.0, 2);
    }

    /// <summary>
    /// Free space on the EFI system partition in MB, or null. Microsoft documents 10 MB or less
    /// there as a cause of 0x800F0922 (a cumulative update rolls back during the restart). Read-only.
    /// </summary>
    public virtual double? EspFreeMB()
    {
        try
        {
            var esp = Services.WindowsUpdate.Esp();
            if (esp == null)
            {
                Run.Log("EFI system partition: not found (not a GPT disk, or not readable).");
                return null;
            }
            var freeMB = Math.Round(esp.FreeBytes / 1048576.0, 1);
            Run.Log(string.Format(Invariant, "EFI system partition: {0} MB free of {1} MB.", TextFormat.Number(freeMB), TextFormat.Number(esp.SizeBytes / 1048576.0, "0")));
            return freeMB;
        }
        catch (Exception ex)
        {
            Run.Log(string.Format(Invariant, "EFI system partition: could not be read ({0}).", ex.Message), LogSeverity.Warning);
            return null;
        }
    }

    /// <summary>The default automatic-update service as (ID, name), or null.</summary>
    public virtual Tuple<string, string> DefaultService()
    {
        try
        {
            return Services.WindowsUpdate.DefaultService();
        }
        catch (Exception ex)
        {
            Run.Log(string.Format(Invariant, "Update source: could not be read ({0}).", ex.Message), LogSeverity.Warning);
            return null;
        }
    }

    /// <summary>The values of one HKLM key, or none when the key is missing.</summary>
    public virtual IDictionary<string, object> RegistryValues(string subKey)
    {
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var name in Services.Registry.ValueNames(subKey)) { values[name] = Services.Registry.GetValue(subKey, name); }
        }
        catch (Exception)
        {
            // A missing key has no values.
        }
        return values;
    }

    /// <summary>A registry value as a whole number, or null when it is missing or not a number.</summary>
    public static int? RegistryInt(IDictionary<string, object> values, string name)
    {
        if (!values.TryGetValue(name, out var value)) { return null; }
        // As Windows PowerShell's [string] shows it: an array as its items joined with a space.
        var text = value is byte[] bytes ? string.Join(" ", bytes.Select(b => b.ToString(Invariant))) : MachineRegistry.AsText(value);
        return int.TryParse(text, NumberStyles.AllowLeadingSign, Invariant, out var number) ? number : null;
    }

    /// <summary>A registry value for the log, as Windows PowerShell joined an array with the separator.</summary>
    private static string JoinValue(object value, string separator) => value switch
    {
        string[] lines => string.Join(separator, lines),
        byte[] bytes => string.Join(separator, bytes.Select(b => b.ToString(Invariant))),
        _ => MachineRegistry.AsText(value) ?? "",
    };

    /// <summary>
    /// Where this device is told to get its updates, and the update services' start types.
    /// Read-only on purpose: these values belong to policy, and clearing one here would be undone
    /// at the next policy refresh while hiding what set it. Returns what later steps need: UsesWsus
    /// (the device is pointed at WSUS for its quality updates) and WsusPolicy (a WSUS server is set
    /// at all, which is also where DISM looks for repair content unless a repair source says otherwise).
    /// </summary>
    public virtual UpdateSourceState UpdateSource()
    {
        var state = new UpdateSourceState();
        var defaultService = DefaultService();
        if (defaultService != null)
        {
            var label = WuCodes.ServiceNames.TryGetValue(defaultService.Item1, out var known) ? known : defaultService.Item2;
            Run.Log(string.Format(Invariant, "Update source: {0} ({1}).", label, defaultService.Item1));
        }

        var policy = RegistryValues(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
        var au = RegistryValues(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU");
        var mdm = RegistryValues(@"SOFTWARE\Microsoft\PolicyManager\current\device\Update");
        var useWsus = RegistryInt(au, "UseWUServer");
        var wsusUrl = policy.TryGetValue("WUServer", out var server) ? MachineRegistry.AsText(server) ?? "" : "";

        // With a WSUS address set, Windows 11 sends every update class to WSUS unless the class is
        // pointed back at Windows Update (SetPolicyDrivenUpdateSourceFor<class>Updates = 0); that
        // mix is a valid configuration. The value can come from Group Policy or the Update CSP.
        var qualityFromWu = new[] { policy, mdm }.Any(s => RegistryInt(s, "SetPolicyDrivenUpdateSourceForQualityUpdates") == 0);
        var defaultIsWsus = defaultService != null && defaultService.Item1 == WuCodes.WsusServiceId;
        if (defaultIsWsus) { qualityFromWu = false; }

        var useWsusText = useWsus?.ToString(Invariant) ?? "";
        if (useWsus == 1 || defaultIsWsus)
        {
            state.WsusPolicy = true;
            Run.Log(string.Format(Invariant, "WSUS policy (an update server of the organisation): set. UseWUServer={0}, WUServer={1}, quality updates from Windows Update={2}.",
                useWsusText, wsusUrl, qualityFromWu ? "True" : "False"));
            if (qualityFromWu)
            {
                Run.Log("Quality updates are explicitly sent to Windows Update, so the WSUS address does not affect them.");
            }
            else
            {
                state.UsesWsus = true;
                state.WsusServer = wsusUrl;
                // Informational: using WSUS can be deliberate (co-management). Later steps say more
                // when the device is behind and offered nothing.
                Run.AddFinding(FindingLevel.Info, "WU-WSUS-POLICY", "This device gets its quality updates from a WSUS server",
                    string.Format(Invariant, "UseWUServer={0}, WUServer={1}. Windows Update for Business then offers this device no quality updates.", useWsusText, wsusUrl),
                    "Nothing to do yourself.",
                    "Only if the device should get its updates from Windows Update: find and remove the policy that sets the WSUS server. Clearing it on the device would be undone at the next policy refresh.");
            }
        }
        else
        {
            Run.Log("WSUS policy (an update server of the organisation): not set.");
        }

        if (RegistryInt(policy, "DisableWindowsUpdateAccess") == 1)
        {
            Run.Log("Update access: disabled by policy (DisableWindowsUpdateAccess=1).", LogSeverity.Warning);
            Run.AddFinding(FindingLevel.Error, "WU-ACCESS-DISABLED", "Access to Windows Update is switched off by policy",
                "DisableWindowsUpdateAccess=1 under the Windows Update policy key (\"Remove access to use all Windows Update features\"); Windows Update then cancels calls with 0x8024002F (WU_E_CALL_CANCELLED_BY_POLICY).",
                "Contact your IT support.",
                "Find and remove the policy that sets it; clearing it on the device would be undone.");
        }
        if (RegistryInt(au, "NoAutoUpdate") == 1)
        {
            Run.Log("Automatic updates: disabled by policy (NoAutoUpdate=1).", LogSeverity.Warning);
            Run.AddFinding(FindingLevel.Warning, "WU-AUTOUPDATE-OFF", "Automatic updates are switched off by policy",
                "NoAutoUpdate=1: the device does not install updates on its own, so it falls behind between repairs.",
                "Contact your IT support.",
                "Find and remove the policy that sets it.");
        }

        try
        {
            var wlid = Services.ServiceControl.StartType("wlidsvc");
            if (wlid == "Disabled")
            {
                // Microsoft documents the effect as feature updates never being offered; quality
                // and definition updates still arrive.
                Run.Log("Microsoft account sign-in assistant (wlidsvc): disabled. Feature updates are never offered to this device; quality updates are not affected.", LogSeverity.Warning);
                Run.AddFinding(FindingLevel.Warning, "WU-WLIDSVC-DISABLED", "Feature updates can never reach this device: the Microsoft account sign-in assistant is disabled",
                    "With the Microsoft account sign-in assistant (wlidsvc) disabled, the feature update service cannot identify the device. Quality updates are not affected.",
                    "Contact your IT support.",
                    "Set the Accounts/AllowMicrosoftAccountSignInAssistant policy back to Not configured (start type Manual).");
            }
            else
            {
                Run.Log(string.Format(Invariant, "Microsoft account sign-in assistant (wlidsvc): {0}.", wlid));
            }
        }
        catch (Exception)
        {
            Run.Log("Microsoft account sign-in assistant (wlidsvc): not present.");
        }

        // Services Windows Update needs. Read-only: start types belong to policy.
        var text = new List<string>();
        var disabled = 0;
        foreach (var service in WuCodes.RequiredServices)
        {
            try
            {
                var startType = Services.ServiceControl.StartType(service.Key);
                text.Add(service.Key + "=" + startType);
                if (startType == "Disabled")
                {
                    disabled++;
                    Run.AddFinding(FindingLevel.Error, "WU-SERVICE-DISABLED-" + service.Key, string.Format(Invariant, "{0} ({1}) is disabled", service.Value, service.Key),
                        "Windows Update cannot work without it (0x80070422).",
                        "Contact your IT support.",
                        "Find the policy or tool that disables it; setting it back on the device would be undone.");
                }
            }
            catch (Exception)
            {
                text.Add(service.Key + "=missing");
            }
        }
        // The start types are evidence for the log; the progress line says only what matters.
        Run.PersistentLog("Update services: " + string.Join(", ", text) + ".");
        if (disabled > 0)
        {
            Run.Log(string.Format(Invariant, "Update services: {0} disabled (see the findings).", TextFormat.Count(disabled, "service")), LogSeverity.Warning);
        }
        else
        {
            Run.Log("Update services: none disabled.");
        }
        return state;
    }

    /// <summary>
    /// The repair source DISM RestoreHealth uses, as set by policy ("Specify settings for optional
    /// component installation and component repair"): RepairContentServerSource=2 sends DISM to
    /// Windows Update instead of WSUS, LocalSourcePath names other repair content. Read-only. On a
    /// device with a WSUS server and neither value, RestoreHealth asks WSUS, which usually has no
    /// repair content, and fails with 0x800f081f.
    /// </summary>
    public virtual bool RepairSourcePolicy(bool wsusPolicy)
    {
        var values = RegistryValues(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Servicing");
        var server = RegistryInt(values, "RepairContentServerSource");
        var local = values.TryGetValue("LocalSourcePath", out var path) ? JoinValue(path, ";").Trim() : "";
        Run.PersistentLog(string.Format(Invariant, "Repair source policy: RepairContentServerSource={0}, LocalSourcePath={1}.", server?.ToString(Invariant) ?? "", local));
        var fromWu = server == 2;
        var isSet = fromWu || local.Length > 0;
        if (isSet)
        {
            var parts = new List<string>();
            if (fromWu) { parts.Add("Windows Update"); }
            if (local.Length > 0) { parts.Add("a local source path"); }
            Run.Log(string.Format(Invariant, "Repair source for DISM (the Windows image servicing tool): set by policy ({0}).", string.Join(" and ", parts)));
        }
        else
        {
            Run.Log("Repair source for DISM (the Windows image servicing tool): not set by policy.");
        }
        if (wsusPolicy && !isSet)
        {
            Run.AddFinding(FindingLevel.Info, "WU-REPAIR-SOURCE-UNSET", "No repair source is set for this device, which uses a WSUS server",
                "Without a repair source, DISM RestoreHealth asks the WSUS server for repair content, which it usually does not have. A component store repair may then fail with 0x800f081f.",
                "Nothing to do yourself.",
                "Configure the policy \"Specify settings for optional component installation and component repair\": download repair content directly from Windows Update, or name an alternate source path.");
        }
        return isSet;
    }

    /// <summary>
    /// Pause, deferral and target-version values that decide whether Windows Update offers this
    /// device anything. Selected by name pattern, so a value this product does not know yet still
    /// shows. Read-only. Only the documented pause values are interpreted.
    /// </summary>
    public virtual bool PolicyState()
    {
        var keys = new[]
        {
            Tuple.Create("MDM policy", @"SOFTWARE\Microsoft\PolicyManager\current\device\Update"),
            Tuple.Create("Group Policy", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate"),
            Tuple.Create("Policy state", @"SOFTWARE\Microsoft\WindowsUpdate\UpdatePolicy\Settings"),
            Tuple.Create("User setting", @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings"),
        };
        var shown = 0;
        var paused = false;
        foreach (var key in keys)
        {
            var values = RegistryValues(key.Item2);
            foreach (var name in values.Keys.OrderBy(n => n, StringComparer.InvariantCultureIgnoreCase))
            {
                // _WinningProvider and _ProviderSet say who set a value, not what it is.
                if (!WuCodes.PolicyValueName.IsMatch(name) || WuCodes.ProviderValueName.IsMatch(name)) { continue; }
                var text = JoinValue(values[name], ",");
                Run.PersistentLog(string.Format(Invariant, "Update policy: {0}, {1} = {2}.", key.Item1, name, text));
                shown++;
                if ((string.Equals(name, "PauseQualityUpdates", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "PausedQualityStatus", StringComparison.OrdinalIgnoreCase)) && text == "1") { paused = true; }
            }
        }
        if (shown == 0)
        {
            Run.Log("Update policy: no pause, deferral or target version value on this device.");
        }
        else
        {
            Run.Log(string.Format(Invariant, "Update policy: {0} for pause, deferral or target version (written to the log).", TextFormat.Count(shown, "value")));
        }
        if (paused)
        {
            Run.Log("Update policy: quality updates are paused.", LogSeverity.Warning);
            Run.AddFinding(FindingLevel.Warning, "WU-PAUSED", "Quality updates are paused on this device",
                "A paused device is offered no quality update. That is policy state, not a device fault.",
                "Contact your IT support.",
                "Resume the updates in your update management (update ring or Autopatch). If the pause stays on the device after it was lifted there, report that.");
        }
        return paused;
    }

    /// <summary>
    /// Update history entries for the log: without updateIds the newest ones that are not
    /// definition updates (what happened since the last run, and which client did it); with
    /// updateIds the entries of the updates this run attempted. Read-only.
    /// </summary>
    public virtual void WriteHistory(IReadOnlyList<string> updateIds = null, int newest = 6, string heading = "Recent update history")
    {
        if (ApiHung)
        {
            Run.Log(heading + " is not read: a Windows Update call already had to be stopped on this run.");
            return;
        }
        try
        {
            var entries = HistoryEntries(200);
            if (entries.Count == 0)
            {
                Run.Log(heading + ": the update history is empty.");
                return;
            }
            var wanted = new HashSet<string>((updateIds ?? Array.Empty<string>()).Where(i => !string.IsNullOrEmpty(i)).Select(i => i.ToLowerInvariant()), StringComparer.Ordinal);
            var shown = 0;
            var failed = 0;
            foreach (var entry in entries)
            {
                if (wanted.Count > 0)
                {
                    if (!wanted.Contains((entry.UpdateId ?? "").ToLowerInvariant())) { continue; }
                }
                else if (WuCodes.DefinitionTitle.IsMatch(entry.Title ?? ""))
                {
                    continue;
                }
                if (shown == 0) { Run.PersistentLog(heading + ":"); }
                var operation = entry.Operation switch
                {
                    1 => "install",
                    2 => "uninstall",
                    3 => "other",
                    _ => "operation " + entry.Operation.ToString(Invariant),
                };
                // The entries are evidence: they go to the log, the progress shows a summary.
                Run.PersistentLog(string.Format(Invariant, "  {0} UTC {1}, {2}, {3}, by {4}: {5}", entry.DateUtc.ToString("yyyy-MM-dd HH:mm", Invariant),
                    operation, ResultText(entry.ResultCode), CodeKnowledge.Key(entry.HResult.ToString(Invariant)), entry.Client, entry.Title));
                if (entry.ResultCode == 4 || entry.ResultCode == 5) { failed++; }
                shown++;
                if (wanted.Count == 0 && shown >= newest) { break; }
            }
            if (shown == 0)
            {
                Run.Log(heading + ": no matching entry.");
            }
            else
            {
                var summary = string.Format(Invariant, "{0}: {1}, of which {2} failed or aborted (details in the log).", heading, TextFormat.Count(shown, "entry", "entries"), failed);
                Run.Log(summary, failed > 0 ? LogSeverity.Warning : LogSeverity.Info);
            }
        }
        catch (Exception ex)
        {
            Run.Log(string.Format(Invariant, "{0} could not be read ({1}); the run continues.", heading, ex.Message), LogSeverity.Warning);
        }
    }

    /// <summary>An OperationResultCode of the Windows Update API in words.</summary>
    public static string ResultText(int code) => code switch
    {
        0 => "not started",
        1 => "in progress",
        2 => "succeeded",
        3 => "succeeded with errors",
        4 => "failed",
        5 => "aborted",
        _ => "result " + code.ToString(Invariant),
    };
}
