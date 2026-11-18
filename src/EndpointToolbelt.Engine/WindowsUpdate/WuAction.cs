using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.WindowsUpdate;

// Repair Windows Update.
//
// Exit codes: 0 done, 2 done with warnings (a step reported a problem, or a finding still needs
// attention), 69001 cooldown (nothing changed), 69002 a restart is pending (nothing changed), 69003
// nothing to repair (the device is current).
//
// Routes, in this order; the first that matches runs:
//   1. Transaction log full (0x800719e4): remove disconnected network adapters of a virtual
//      machine, then restart, but only when every condition of AssessGhosts holds; otherwise report
//      the disconnected devices and check the component store.
//   2. Servicing failure: report stale baseline packages (remove them only when the
//      WURemoveStalePackages setting allows it), check the component store.
//   3. Space (a space code, or less free space than WUMinFreeGB): clear the download cache, remove
//      superseded components, then search and install when there is room.
//   4. Source missing: report that the store lacks files, or that the repair files could not be
//      downloaded (0x800F0906; this product passes no repair source of its own), then search and
//      install once more.
//   5. Delivery codes: a network path problem; clear the download cache, then search and install.
//   6. Unrecognised codes on a Windows update: check the component store.
//   7. Policy codes only (0x800F0907, 0x8024002E, 0x8024002F): report the policy; nothing is
//      repaired or installed, because nothing on the device gets past a policy.
//   8. Behind with nothing failing: search first; clear the download cache only when the search fails.
// A policy code is reported on every route (WU-POLICY-BLOCKS) and never starts a store repair. A
// route that checked the store and repaired nothing ends with a search and install as well. The
// cache reset clears SoftwareDistribution\Download only; the update database (DataStore) only on
// evidence (a 0x80248xxx code, or a search that still fails right after the download cache was
// cleared). It is never done on a device that other users share.
public partial class WindowsUpdateRepair
{
    private const string StateName = "LastWURepair";

    // The run's state, through methods the tests replace.
    public virtual bool PendingRestart() => Run.State.PendingRestart();

    public virtual bool CooldownAllows(int hours) => Run.State.CooldownAllows(StateName, hours, "Windows Update repair");

    public virtual string StampProvisionally() => Run.State.StampProvisionally(StateName);

    public virtual void Stamp() => Run.State.Stamp(StateName);

    public virtual void RestoreStamp(string previous) => Run.State.Restore(StateName, previous);

    public virtual void MarkRestartRequired(string causedBy) => Run.State.MarkRestartRequired(causedBy);

    private static bool HasCode(IReadOnlyList<string> codes, IEnumerable<string> set) => codes.Any(set.Contains);

    /// <summary>Repair Windows Update: diagnosis, then the one repair that addresses what was found.</summary>
    public virtual int RepairWU()
    {
        var config = Run.Config;
        var cooldownHours = config.WURepairCooldownHours;
        var minFreeGB = config.WUMinFreeGB;

        if (PendingRestart())
        {
            Run.Log("A restart is pending. Repairing on top of a half-installed update is unsafe; restart the device first.", LogSeverity.Warning);
            Run.AddFinding(FindingLevel.Warning, "WU-RESTART-PENDING", "Windows is waiting for a restart",
                "An update is installed up to the restart. Repairing now could break that half-finished install, and the restart may be all that is missing.",
                "Restart the device. Run this repair again only if updates still fail afterwards. Nothing was changed now.");
            return EngineExitCode.RestartPending;
        }

        ApiHung = false;
        var warnings = 0;
        var restartNeeded = false;
        // Whether this run changed the device. Only then does the cooldown start: it exists to keep
        // a device from being repaired again and again, not to ration a read-only diagnosis or a
        // search that found nothing.
        var deviceChanged = false;

        // --- Step 1: diagnosis (seconds, no changes) ---------------------------------------------
        Run.StepHeader(1, 3, "Check Windows Update (read-only)");
        var currency = Currency(config.WUDeploymentDelayDays);
        if (currency.LastPatchMonth != null)
        {
            Run.Log(string.Format(Invariant, "Last quality update: {0} ({1}), from the {2}.", currency.LastPatchMonth.Value.ToString("yyyy-MM", Invariant), currency.LastTitle, currency.Source));
        }
        else
        {
            Run.Log("Last quality update: none found in the update history or the hotfix list.");
        }
        Run.Log(string.Format(Invariant, "Expected patch month: {0}.", currency.ExpectedMonth.ToString("yyyy-MM", Invariant)));
        Run.Log(string.Format(Invariant, "Months behind: {0}.", currency.MonthsBehind?.ToString(Invariant) ?? "unknown"));
        // Before the gate, so a run that ends in 69003 also shows what happened since the last one.
        WriteHistory(null, 6, "Recent update history (newest first, definition updates left out)");

        // A "current" verdict from the hotfix list is checked before it ends the run: the newest KB
        // there can be a .NET or servicing stack update of the right month. An offered quality update
        // means the device is not current; that search is then reused below.
        var hotfixVerified = false;
        WuSearch hotfixSearch = null;
        if (currency.IsCurrent && currency.Source == "hotfix")
        {
            Run.Log("The \"current\" verdict comes from the hotfix list, not the update history; checking it with a read-only search.");
            var verify = Search();
            if (verify.Ok)
            {
                hotfixVerified = true;
                var offered = verify.Install.Where(u => IsQualityTitle(u.Title, forCurrency: true)).ToList();
                if (offered.Count > 0)
                {
                    Run.Log(string.Format(Invariant, "Windows Update offers {0}, so the device is not current: {1}", TextFormat.Count(offered.Count, "quality update"), string.Join("; ", offered.Select(u => u.Title))));
                    currency.IsCurrent = false;
                    currency.MonthsBehind = null;
                    currency.Source = "hotfix list, contradicted by a search that offers a quality update";
                    hotfixSearch = verify;
                }
                else
                {
                    Run.Log("The search offers no quality update; the verdict stands.");
                }
            }
            else
            {
                Run.Log(string.Format(Invariant, "The checking search failed ({0}); the hotfix verdict is used as it is.", verify.ErrorCode), LogSeverity.Warning);
            }
        }

        if (currency.IsCurrent)
        {
            var sourceText = "update history";
            if (currency.Source == "hotfix")
            {
                sourceText = hotfixVerified ? "hotfix list, confirmed by a search" : "hotfix list; the search that should confirm it failed, so this is unconfirmed";
            }
            // The installed patch month: it can be newer than the expected one (a deployment delay).
            Run.AddFinding(FindingLevel.Ok, "WU-CURRENT", string.Format(Invariant, "Windows is up to date (patch month {0})", (currency.LastPatchMonth ?? currency.ExpectedMonth).ToString("yyyy-MM", Invariant)),
                string.Format(Invariant, "Last quality update: {0}, read from the {1}.", currency.LastTitle, sourceText),
                "Nothing to repair. An error shown in Settings > Windows Update on a current device is almost always a passing client message.");
            Run.Log("This device is on the expected quality update; there is nothing to repair.");
            // No cooldown: this changed nothing.
            return EngineExitCode.NothingToRepair;
        }

        var found = FailureCodes(currency.LastSuccessUtc);
        var codes = found.Known;
        var unknown = found.Unknown;
        GhostListFailed = false;
        var ghosts = GhostDevices();
        var freeGB = SystemDriveFreeGB();
        var freeText = freeGB == null ? "unknown" : TextFormat.Number(freeGB.Value, "0.##") + " GB";
        if (codes.Count > 0) { Run.Log("Failure codes: " + string.Join(", ", codes) + ".", LogSeverity.Warning); } else { Run.Log("Failure codes: none."); }
        if (unknown.Count > 0) { Run.Log("Unrecognised codes: " + string.Join(", ", unknown) + ".", LogSeverity.Warning); } else { Run.Log("Unrecognised codes: none."); }
        // Codes on events that name no Windows update (a Store app in use, for one); they route
        // nothing and are shown so nobody wonders where a code went.
        Run.Log("Not update failures: " + (found.Ignored.Count > 0 ? string.Join(", ", found.Ignored) : "none") + ".");
        // Codes only in the Setup log, which any signed-in user can write; they route nothing.
        if (found.Unconfirmed.Count > 0)
        {
            Run.Log("Setup log codes not confirmed by the update client or history (not used): " + string.Join(", ", found.Unconfirmed) + ".");
        }
        // A 0x80248xxx code (the update database) is the evidence that allows the DataStore reset.
        // Codes on events that name no update count too: a failed search names none.
        var dataStoreEvidence = HasDataStoreCode(codes.Concat(unknown).Concat(found.Ignored));
        if (dataStoreEvidence) { Run.Log("An update database (DataStore) code was logged; a cache reset on this run includes the database."); }
        if (GhostListFailed)
        {
            Run.Log("Disconnected network and system devices: could not be listed.", LogSeverity.Warning);
        }
        else
        {
            Run.Log(string.Format(Invariant, "Disconnected network and system devices: {0}.", ghosts.Count));
        }
        Run.Log(string.Format(Invariant, "Free space on {0}: {1}.", SystemDrive, freeText));
        var source = UpdateSource();
        RepairSourcePolicy(source.WsusPolicy);
        PolicyState();
        var espFreeMB = EspFreeMB();

        // Findings of the diagnosis, before the cooldown, so a run inside the cooldown still shows them.
        if (currency.LastPatchMonth != null)
        {
            var monthsText = currency.MonthsBehind == null ? "an unknown number of months" : TextFormat.Count(currency.MonthsBehind.Value, "month");
            Run.AddFinding(FindingLevel.Warning, "WU-BEHIND", string.Format(Invariant, "Windows is {0} behind on quality updates", monthsText),
                string.Format(Invariant, "Last quality update {0} ({1}, from the {2}); expected by now: {3}.", currency.LastPatchMonth.Value.ToString("yyyy-MM", Invariant),
                    currency.LastTitle, currency.Source, currency.ExpectedMonth.ToString("yyyy-MM", Invariant)));
        }
        else
        {
            Run.AddFinding(FindingLevel.Warning, "WU-NO-HISTORY", "No quality update found in the update history or the hotfix list",
                "The patch level cannot be read from this device, so it is treated as behind.");
        }
        AddCodeFindings(codes.Concat(unknown).ToList());
        // A policy code names what blocks the update; it is reported, never repaired on the device.
        var policyFound = codes.Where(c => WuCodes.PolicyCodes.Contains(c)).ToList();
        if (policyFound.Count > 0)
        {
            var policyText = string.Join("; ", policyFound.Select(c => CodeKnowledge.FormatHex(c) + ": " + WuCodes.PolicyNames[c]));
            Run.Log("A policy blocks Windows Update or its repair source: " + policyText + ".", LogSeverity.Warning);
            var supports = string.Join(" ", policyFound.Select(c => CodeKnowledge.Get(c)?.Support).Where(s => !string.IsNullOrEmpty(s)));
            Run.AddFinding(FindingLevel.Warning, "WU-POLICY-BLOCKS", "A policy blocks Windows Update or its repair source",
                string.Format(Invariant, "Logged since the last successful quality update: {0}. A policy decides this, so this repair does not try to work around it on the device.", policyText),
                "Contact your IT support.",
                "Check the policy named above in the device management (Intune or Group Policy) and allow what this device needs; changing it on the device would be undone at the next policy refresh. " + supports);
        }
        var lowSpace = freeGB != null && freeGB.Value < minFreeGB;
        if (lowSpace)
        {
            Run.AddFinding(FindingLevel.Warning, "WU-LOW-SPACE", string.Format(Invariant, "Only {0} free on {1}", freeText, SystemDrive),
                string.Format(Invariant, "Updates need room to download and stage; this repair treats less than {0} GB as the cause.", minFreeGB));
        }
        // 10 MB is Microsoft's figure for the 0x800F0922 rollback.
        if (espFreeMB != null && espFreeMB.Value <= 10)
        {
            Run.AddFinding(FindingLevel.Warning, "WU-ESP-FULL", string.Format(Invariant, "The EFI system partition has only {0} MB free", TextFormat.Number(espFreeMB.Value)),
                "Microsoft documents 10 MB or less there as a cause of 0x800F0922: the cumulative update rolls back at 35-36% during the restart. Freeing space on the system drive does not help with this.",
                "Contact your IT support.",
                "Fixed by KB5089573 (May 2026) and later cumulative updates. Where a device cannot get there, Microsoft documents a registry workaround (Bfsvc EspPaddingPercent=0).");
        }

        // The cooldown rations the repair, not the diagnosis above.
        if (!CooldownAllows(cooldownHours))
        {
            Run.Log("The diagnosis above is current; the repair itself waits until the cooldown has passed.");
            return EngineExitCode.CooldownActive;
        }

        // --- Step 2: targeted repair -------------------------------------------------------------
        // "Changed" and "restart needed" follow what a step really did; the steps that change the
        // device mark it as they go (ChangedOwed, RestartOwed), and the close-out below runs even when
        // a step fails unexpectedly: a repair that changed the device must still start the cooldown
        // and leave the restart marker. A read-only check that changed nothing (a clean ScanHealth, a
        // search that offers nothing) does not start the cooldown. It is stamped before the repair
        // and put back in the close-out when nothing changed, so a run stopped halfway (no close-out)
        // still starts it.
        var previousStamp = StampProvisionally();
        RestartOwed = false;
        ChangedOwed = false;
        ServicingBusy = null;
        Exception failure = null;
        try
        {
            WuSearchAndInstall wuRun = null;
            ComponentRepairResult componentRepair = null;
            // The step title names the route in plain words, so it is chosen first; the branches
            // below take the same decisions in the same order. The assessment only reads. When it
            // fails, nothing is removed, the step header still says what follows, in general words,
            // and the route continues as for an assessment that does not allow the removal.
            GhostAssessment assessment = null;
            var assessmentFailed = false;
            if (HasCode(codes, WuCodes.TxLogCodes))
            {
                try
                {
                    assessment = AssessGhosts(ghosts, currency.LastSuccessUtc);
                }
                catch (Exception ex)
                {
                    assessmentFailed = true;
                    Run.Log(string.Format(Invariant, "The disconnected devices could not be assessed ({0}); none are removed.", ex.Message), LogSeverity.Warning);
                    assessment = new GhostAssessment { Reasons = { string.Format(Invariant, "the assessment failed ({0})", ex.Message) } };
                }
            }
            var routeTitle = "Look for missing updates and install them";
            if (assessmentFailed) { routeTitle = "Fix what the diagnosis found"; }
            else if (assessment != null && assessment.Eligible) { routeTitle = "Remove disconnected network adapters"; }
            else if (assessment != null || HasCode(codes, WuCodes.ServicingCodes)) { routeTitle = "Check and repair system files (DISM and SFC)"; }
            else if (HasCode(codes, WuCodes.SpaceCodes) || lowSpace) { routeTitle = "Free up space, then look for missing updates"; }
            else if (HasCode(codes, WuCodes.SourceCodes)) { routeTitle = "Install the missing updates"; }
            else if (HasCode(codes, WuCodes.DeliveryCodes)) { routeTitle = "Clear the update download cache, then look for missing updates"; }
            else if (unknown.Count > 0) { routeTitle = "Check and repair system files (DISM and SFC)"; }
            else if (HasCode(codes, WuCodes.PolicyCodes)) { routeTitle = "Report the policy that blocks the update"; }
            Run.StepHeader(2, 3, "Repair: " + routeTitle);

            // Remembered, so the cache reset does not wait for the servicing stack a second time.
            ServicingBusy = !WaitServicingIdle();
            if (ServicingBusy.Value)
            {
                Run.Log("Continuing with the repair; the update cache is not cleared while the servicing stack is busy.");
            }

            if (HasCode(codes, WuCodes.TxLogCodes))
            {
                Run.Log("Diagnosis: the servicing transaction log is full (0x800719e4).");
                var busText = string.Join(", ", assessment.ByEnumerator.Select(p => p.Key + " " + p.Value.ToString(Invariant)));
                if (busText.Length > 0) { Run.Log("Disconnected network and system devices by bus: " + busText + "."); }
                if (assessment.Eligible)
                {
                    var candidates = assessment.Candidates;
                    Run.Log(string.Format(Invariant, "Every condition for the documented fix holds: removing {0} (the device node only).", TextFormat.Count(candidates.Count, "disconnected network adapter")));
                    var removal = RemoveGhostDevices(candidates);
                    var summary = string.Format(Invariant, "Removed {0}; {1}.", TextFormat.Count(removal.Removed, "device"), TextFormat.Count(removal.Failed, "failure"));
                    Run.Log(summary, removal.Failed > 0 || removal.Removed == 0 ? LogSeverity.Warning : LogSeverity.Info);
                    // A restart is needed only when something was removed.
                    if (removal.Removed > 0)
                    {
                        deviceChanged = true;
                        restartNeeded = true;
                        Run.AddChange(string.Format(Invariant, "Removed {0} that filled the servicing transaction log", TextFormat.Count(removal.Removed, "disconnected (ghost) network adapter")));
                    }
                    if (removal.Skipped > 0)
                    {
                        Run.Log(TextFormat.Count(removal.Skipped, "device was", "devices were") + " left alone: present again, not readable, or already gone right before the removal.");
                    }
                    // Every candidate skipped: nothing changed, so no restart and no cooldown, and the
                    // cause of the full transaction log was not addressed.
                    if (removal.Removed == 0 && removal.Skipped > 0 && removal.Skipped == candidates.Count)
                    {
                        warnings++;
                        Run.AddFinding(FindingLevel.Warning, "WU-GHOST-NONE-REMOVED", "No disconnected network adapter was removed",
                            string.Format(Invariant, "All {0} selected for removal were present again, not readable, or already gone when the removal ran, so nothing was changed and the cause of 0x800719E4 was not addressed.", TextFormat.Count(candidates.Count, "adapter")),
                            "Restart the device and run the repair again. If updates keep failing with 0x800719E4, contact your IT support.",
                            "Check the CBS log for the driver operations queue failing with 0x800719e4, and list the disconnected network adapters (pnputil /enum-devices /disconnected). Adapters that come back on their own are not the ones that fill the queue.");
                    }
                    if (removal.Failed > 0)
                    {
                        Run.Log("Could not remove " + TextFormat.Count(removal.Failed, "device") + ".", LogSeverity.Warning);
                        warnings++;
                    }
                    if (removal.NotTried > 0)
                    {
                        Run.Log(TextFormat.Count(removal.NotTried, "device was", "devices were") + " not tried: this run has no time left. Run the repair again after the restart to remove the rest.", LogSeverity.Warning);
                        warnings++;
                    }
                }
                else
                {
                    if (ghosts.Count > 0)
                    {
                        var reasonText = string.Join("; ", assessment.Reasons);
                        Run.Log("The disconnected devices are not removed: " + reasonText + ".");
                        Run.AddFinding(FindingLevel.Warning, "WU-GHOST-DEVICES", TextFormat.Count(ghosts.Count, "disconnected network or system device") + " found, not removed",
                            string.Format(Invariant, "By bus: {0}. They are removed automatically only in the case Microsoft documents for 0x800719e4 (many disconnected network adapters of a virtual machine, and the driver operations queue failing in the CBS log). Not met: {1}.", busText, reasonText),
                            "Contact your IT support; restarting alone does not fix a full transaction log.",
                            "Check the CBS log for \"driver operations queue\" failures with 0x800719e4. If disconnected devices fill it, remove them with pnputil /remove-device (the device node only) and restart; never remove USB, Bluetooth or System class devices that may come back.");
                    }
                    else if (GhostListFailed)
                    {
                        Run.Log("The devices could not be listed, so whether disconnected devices cause this is not known.", LogSeverity.Warning);
                    }
                    else
                    {
                        Run.Log("No disconnected devices are present, so they are not the cause here.");
                    }
                    Run.Log("Checking the component store instead.");
                    componentRepair = ComponentRepair();
                }
            }
            else if (HasCode(codes, WuCodes.ServicingCodes))
            {
                Run.Log("Diagnosis: a servicing failure. Checking the servicing package inventory.");
                var stale = StaleRollupFixes();
                if (stale.Count == 0) { Run.Log("No stale baseline package found."); }
                var removeAllowed = config.WURemoveStalePackages;
                foreach (var package in stale)
                {
                    var stateText = "0x" + package.State.ToString("X2", Invariant);
                    if (!removeAllowed)
                    {
                        Run.Log(string.Format(Invariant, "Baseline package older than the installed one, state {0}: {1}", stateText, package.Name));
                        continue;
                    }
                    deviceChanged = true;
                    if (RemoveStalePackage(package.Name))
                    {
                        restartNeeded = true;
                        Run.AddChange("Removed the stale servicing package " + package.Name);
                    }
                    else
                    {
                        Run.Log("Could not remove the package " + package.Name + ".", LogSeverity.Warning);
                        warnings++;
                    }
                }
                if (stale.Count > 0 && !removeAllowed)
                {
                    Run.Log("Not removed: the WURemoveStalePackages setting is off. Removing such a package has not been shown to fix this failure.");
                    var detail = "Reported, not removed: removing one has not been shown to fix this failure, and devices have been seen updating normally with one in place.";
                    if (stale.Any(p => p.Name.IndexOf("26100.1742", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        detail += " One of them is 26100.1742, the package the reported mitigation for Microsoft's 2026 known issue removes.";
                    }
                    Run.AddFinding(FindingLevel.Info, "WU-STALE-PACKAGE", TextFormat.Count(stale.Count, "older baseline update package") + " left in a staged state",
                        detail,
                        "Nothing to do yourself.",
                        "To try the removal on one device, turn on the WURemoveStalePackages setting for it and run the repair again.");
                }
                componentRepair = ComponentRepair();
            }
            else if (HasCode(codes, WuCodes.SpaceCodes) || lowSpace)
            {
                Run.Log("Diagnosis: not enough space. Reclaiming what the update stack itself holds.");
                var reset = CacheReset(dataStoreEvidence);
                warnings += reset.Warnings;
                if (reset.Changed) { deviceChanged = true; }
                var limit = Run.StepLimit(WuCodes.CleanupMinutes, "DISM StartComponentCleanup");
                if (limit <= 0)
                {
                    warnings++;
                }
                else
                {
                    Run.Log("Removing superseded Windows components (DISM StartComponentCleanup).");
                    var cleanup = Run.RunTool(new ProcessRequest { FilePath = Dism, Arguments = "/Online /English /Cleanup-Image /StartComponentCleanup", Label = "DISM StartComponentCleanup", TimeoutMinutes = limit });
                    if (cleanup.Started) { deviceChanged = true; }
                    if (!cleanup.TimedOut && cleanup.Started && (cleanup.ExitCode == 0 || cleanup.ExitCode == 3010))
                    {
                        Run.AddChange("Removed superseded Windows components (DISM StartComponentCleanup)");
                    }
                    else
                    {
                        Run.Log("DISM StartComponentCleanup failed or did not finish.", LogSeverity.Warning);
                        warnings++;
                    }
                }
                var after = SystemDriveFreeGB();
                if (after != null)
                {
                    Run.Log(string.Format(Invariant, "Free space now {0} GB (was {1}).", TextFormat.Number(after.Value, "0.##"), freeText));
                }
                if (after != null && after.Value < minFreeGB)
                {
                    Run.AddFinding(FindingLevel.Error, "WU-SPACE-STILL-LOW", string.Format(Invariant, "Still only {0} GB free on {1} after the cleanup", TextFormat.Number(after.Value, "0.##"), SystemDrive),
                        string.Format(Invariant, "The update stack has given back what it held (was {0}). Updates cannot stage without more room.", freeText),
                        string.Format(Invariant, "Free up at least {0} GB (Downloads, the Recycle Bin, large local caches), then run the repair again.", TextFormat.Number(Math.Ceiling(minFreeGB - after.Value), "0")));
                    Run.Log("No updates are installed on this run: there is not enough space for them to stage.", LogSeverity.Warning);
                    warnings++;
                }
                else
                {
                    wuRun = SearchAndInstall();
                }
            }
            else if (HasCode(codes, WuCodes.SourceCodes))
            {
                // 0x800F081F and 0x800F0915: the files are missing (usually a damaged store);
                // 0x800F0906: their download failed (the connection or the repair source).
                var sourceFound = codes.Where(c => WuCodes.SourceCodes.Contains(c)).ToList();
                var storeFound = sourceFound.Where(c => !WuCodes.SourceDownloadCodes.Contains(c)).ToList();
                var downloadFound = sourceFound.Where(c => WuCodes.SourceDownloadCodes.Contains(c)).ToList();
                var sourceText = string.Join(", ", sourceFound.Select(CodeKnowledge.FormatHex));
                var os = OsVersion();
                var title = "The component store lacks files the update needs";
                var sourceNext = "If the install below fails again with the same code, contact your IT support.";
                var parts = new List<string>();
                if (storeFound.Count > 0)
                {
                    Run.Log("Diagnosis: the component store lacks files the update needs (source missing). This is usually a damaged store, not a connection problem.");
                    parts.Add("Files the update needs are not on the device, and DISM did not find them in the repair source set by policy, if any, otherwise Windows Update.");
                }
                if (downloadFound.Count > 0)
                {
                    Run.Log("Diagnosis: the repair files could not be downloaded (0x800F0906). That points at the network connection or the repair source (Windows Update, or the one set by policy), not at a damaged store.");
                    parts.Add("The repair files could not be downloaded from the repair source set by policy, if any, otherwise Windows Update: check the network connection and that repair source.");
                    sourceNext = "Check the network connection. If the install below fails again with the same code, contact your IT support.";
                    if (storeFound.Count == 0) { title = "The repair files for the update could not be downloaded"; }
                }
                Run.AddFinding(FindingLevel.Warning, "WU-NO-REPAIR-SOURCE", title,
                    string.Format(Invariant, "{0} ({1}, build {2}.{3}): {4} This product passes no repair source of its own.", sourceText, os.DisplayVersion, os.Build, os.Ubr, string.Join(" ", parts)),
                    sourceNext,
                    "Configure a repair source (policy: Specify settings for optional component installation and component repair), or do an in-place upgrade, which keeps apps and files. A device without access to Windows Update or a repair source cannot have its component store repaired here.");
                Run.Log("Trying the install once more. If it fails with the same code, the device needs an in-place upgrade.");
                wuRun = SearchAndInstall();
            }
            else if (HasCode(codes, WuCodes.DeliveryCodes))
            {
                Run.Log("Diagnosis: the device cannot reach or complete downloads from the update service. That is the network path, so the component store is not touched.");
                Run.AddFinding(FindingLevel.Warning, "WU-DELIVERY", "This device could not download from the update service",
                    string.Format(Invariant, "Download and connection codes were logged ({0}). That points at the network path, not a damaged Windows, so the component store is not touched.", string.Join(", ", codes.Where(c => WuCodes.DeliveryCodes.Contains(c)))),
                    "If the install below fails again, try another network (or without VPN) and contact your IT support if it keeps failing.",
                    "Check the proxy, firewall and VPN path to the Microsoft update endpoints from where this device is.");
                var reset = CacheReset(dataStoreEvidence);
                warnings += reset.Warnings;
                if (reset.Changed) { deviceChanged = true; }
                wuRun = SearchAndInstall();
            }
            else if (unknown.Count > 0)
            {
                // The only route on which the store check runs without a recognised code. A
                // recognised code always gets its own route above.
                Run.Log(string.Format(Invariant, "Diagnosis: behind, with unrecognised codes ({0}). Checking the component store.", string.Join(", ", unknown)));
                componentRepair = ComponentRepair();
            }
            else if (HasCode(codes, WuCodes.PolicyCodes))
            {
                // Report only: a policy decides this, and no repair on the device gets past it.
                Run.Log("Diagnosis: a policy blocks Windows Update or its repair source (see the findings). Nothing on this device can lift a policy, so nothing is repaired or installed on this run.");
                warnings++;
            }
            else
            {
                Run.Log("Diagnosis: behind on updates, with no update failure logged. Asking Windows Update what it offers before changing anything.");
                wuRun = SearchAndInstall(resetOnSearchFailure: true, searchResult: hotfixSearch, dataStoreEvidence: dataStoreEvidence);
            }

            if (componentRepair != null)
            {
                warnings += componentRepair.Warnings;
                if (componentRepair.Changed) { deviceChanged = true; }
                if (componentRepair.RestartNeeded)
                {
                    restartNeeded = true;
                    deviceChanged = true;
                }
                else if (!componentRepair.Completed)
                {
                    // No search behind a check that could not finish: the servicing stack may still be busy.
                    Run.Log("The component store check did not complete, so nothing further is attempted on this run. Run the repair again.", LogSeverity.Warning);
                }
                else if (!restartNeeded)
                {
                    Run.Log("Nothing was repaired that needs a restart. Asking Windows Update what it offers this device.");
                    wuRun = SearchAndInstall();
                }
            }

            if (wuRun != null)
            {
                warnings += wuRun.Warnings;
                if (wuRun.RebootRequired) { restartNeeded = true; }
                if (wuRun.ChangedDevice) { deviceChanged = true; }
                if (wuRun.NothingOffered)
                {
                    // A warning, not a success: the device stays behind and someone has to act, only
                    // not on this device.
                    if (wuRun.HiddenCount > 0)
                    {
                        Run.Log(TextFormat.Count(wuRun.HiddenCount, "update is", "updates are") + " hidden on this device. If the missing quality update is among them, that is why it is not offered.");
                    }
                    var wsusText = source.UsesWsus ? " This device is pointed at a WSUS server for its quality updates, which is the first thing to check." : "";
                    var logged = codes.Concat(unknown).ToList();
                    if (logged.Count > 0)
                    {
                        Run.AddFinding(FindingLevel.Warning, "WU-OFFERED-NOTHING", "Windows Update offers this device nothing, although it is behind",
                            string.Format(Invariant, "Update failures were logged earlier ({0}), but there is no update to retry now.{1}", string.Join(", ", logged), wsusText),
                            "Contact your IT support.",
                            "Check the update management: the update ring and its deferral, a pause, the update source policy and, if used, the Autopatch registration of this device.");
                    }
                    else
                    {
                        Run.AddFinding(FindingLevel.Warning, "WU-NO-DEVICE-FAULT", "Behind, but Windows Update offers nothing and no failure was found",
                            "No update failure is logged and the search returned nothing to install. That points at the service side; it does not rule out every local cause." + wsusText,
                            "Contact your IT support.",
                            "Check the update management: has the ring released the update yet (deferral), are updates paused, and is the device assigned the right policy (and, if used, registered in Autopatch).");
                    }
                    warnings++;
                }
                if (wuRun.Installed > 0 && !ApiHung)
                {
                    var after = Currency(config.WUDeploymentDelayDays);
                    if (after.LastPatchMonth != null)
                    {
                        Run.Log(string.Format(Invariant, "Last quality update now: {0} ({1}).", after.LastPatchMonth.Value.ToString("yyyy-MM", Invariant), after.LastTitle));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // --- Step 3: close-out -------------------------------------------------------------------
        Run.StepHeader(3, 3, "Finish");
        if (RestartOwed) { restartNeeded = true; }
        if (ChangedOwed) { deviceChanged = true; }
        if (deviceChanged)
        {
            Stamp();
        }
        else
        {
            RestoreStamp(previousStamp);
            Run.Log("Nothing on this device was changed, so the repair cooldown is not started.");
        }
        if (restartNeeded)
        {
            MarkRestartRequired("Repair Windows Update");
            Run.Log("The repair finished. Restart the device, then check for updates again.");
            Run.AddFinding(FindingLevel.Warning, "WU-RESTART-REQUIRED", "Restart the device to finish",
                "What this repair changed takes effect only after a restart.",
                "Restart now, then check for updates in Settings > Windows Update. Run this repair again only if updates still fail.");
        }
        else if (failure == null)
        {
            Run.Log("The repair finished; no restart is needed.");
        }
        // The run frame reports the unexpected error (exit 1) after the close-out above.
        if (failure != null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
        // A finding that still needs attention (a disabled update service, a pause, a full EFI
        // partition) is never shown with a plain success. Not counted: the starting state the repair
        // addressed (behind, no history, low space, the logged codes) and the restart.
        var starting = new[] { "WU-BEHIND", "WU-NO-HISTORY", "WU-LOW-SPACE", "WU-RESTART-REQUIRED" };
        var open = Run.Findings.Count(f => (f.Level == FindingLevel.Error || f.Level == FindingLevel.Warning) && !starting.Contains(f.Id)
            && !f.Id.StartsWith("WU-CODE-", StringComparison.OrdinalIgnoreCase));
        if (warnings == 0 && open > 0)
        {
            Run.Log(string.Format(Invariant, "Completed. Findings that still need attention: {0}.", open), LogSeverity.Warning);
            return EngineExitCode.Warnings;
        }
        if (warnings > 0)
        {
            Run.Log("Completed with " + TextFormat.Count(warnings, "warning") + ".", LogSeverity.Warning);
            return EngineExitCode.Warnings;
        }
        return EngineExitCode.Success;
    }
}
