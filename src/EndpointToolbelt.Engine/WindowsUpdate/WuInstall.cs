using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using EndpointToolbelt.Engine.Ime;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.WindowsUpdate;

// The Windows Update API through the worker: search, install one update at a time, and the route
// end that searches and installs.
public partial class WindowsUpdateRepair
{
    /// <summary>This engine's own executable, which runs the worker (<c>--wu-worker</c>).</summary>
    public virtual string WorkerPath => typeof(WindowsUpdateRepair).Assembly.Location;

    /// <summary>
    /// Runs one Windows Update API operation in its own process (this executable in worker mode),
    /// so a hung call can be stopped on its time limit. Returns the worker's result line, or a
    /// reply with Ok false and the reason (TimedOut when it was stopped). Throws, before anything
    /// runs, for an update ID that is not a GUID.
    /// </summary>
    public virtual WuWorkerReply RunWorker(string operation, string updateId, int timeoutMinutes)
    {
        var arguments = WuWorker.Switch + " " + operation;
        if (!string.IsNullOrEmpty(updateId))
        {
            if (!WuCodes.UpdateId.IsMatch(updateId)) { throw new InvalidOperationException("invalid update ID"); }
            arguments += " " + updateId;
        }
        var result = Run.RunTool(new ProcessRequest
        {
            FilePath = WorkerPath,
            Arguments = arguments,
            Label = "Windows Update " + operation.ToLowerInvariant(),
            TimeoutMinutes = timeoutMinutes,
            OutputEncoding = new UTF8Encoding(false),
        });
        // Started false means the worker never ran, so nothing was asked of Windows Update.
        if (!result.Started) { return new WuWorkerReply { Started = false, Error = "the worker could not be started" }; }
        if (result.TimedOut)
        {
            // The update service may be hung; later in-process API calls could block on it too.
            ApiHung = true;
            return new WuWorkerReply { TimedOut = true, Error = "stopped after " + TextFormat.Count(timeoutMinutes, "minute") };
        }
        var line = (result.Output ?? "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("{", StringComparison.Ordinal));
        if (line == null) { return new WuWorkerReply { Error = "the worker returned no result" }; }
        try
        {
            var reply = WuWorkerReply.Parse(line);
            reply.TimedOut = false;
            reply.Started = true;
            return reply;
        }
        catch (Exception)
        {
            return new WuWorkerReply { Error = "the worker result could not be read" };
        }
    }

    /// <summary>
    /// What this device is offered: quality and security updates to install, other offered updates
    /// and hidden updates by title. Language-independent criteria: not installed, not hidden, not
    /// optional, software only.
    /// </summary>
    public virtual WuSearch Search()
    {
        var search = new WuSearch();
        var limit = Run.StepLimit(WuCodes.SearchMinutes, "The Windows Update search");
        if (limit <= 0)
        {
            search.NotStarted = true;
            search.Error = "no time left in this run";
            return search;
        }
        Run.Log("Asking Windows Update what this device is offered (this can take several minutes).");
        var raw = RunWorker("Search", "", limit);
        search.NotStarted = !raw.Started;
        search.TimedOut = raw.TimedOut;
        search.ErrorCode = raw.ErrorCode;
        search.Error = raw.Error;
        if (!raw.Ok) { return search; }
        search.Ok = true;
        search.Install = raw.Install;
        search.Other = raw.Other;
        search.Hidden = raw.Hidden;
        return search;
    }

    /// <summary>
    /// Downloads and installs the offered updates one at a time, each in its own worker, so one bad
    /// update costs only itself and the log says which. Before each update the run's remaining time
    /// is checked, and Windows is asked whether it wants a restart first.
    /// </summary>
    public virtual WuInstallSet InstallUpdates(IReadOnlyList<WuOffer> updates)
    {
        var result = new WuInstallSet();
        var index = 0;
        var restartFirst = false;
        foreach (var update in updates)
        {
            index++;
            Run.Log(string.Format(Invariant, "  [{0}/{1}] {2}", index, updates.Count, update.Title));
            if (restartFirst)
            {
                // Windows already said it wants a restart first; asking again per update only costs time.
                Run.Log("      not attempted: Windows requires a restart first.");
                result.NotAttempted++;
                continue;
            }
            var left = Run.MinutesLeft();
            if (left < WuCodes.InstallMinMinutes)
            {
                Run.Log(string.Format(Invariant, "      not attempted: {0} of this run left, and an install can need {1}.",
                    TextFormat.Count(Math.Max(0, left), "minute"), TextFormat.Count(WuCodes.InstallMinMinutes, "minute")));
                result.NotAttempted++;
                continue;
            }
            WuWorkerReply raw;
            try
            {
                raw = RunWorker("Install", update.Id, Math.Min(WuCodes.InstallMinutes, left));
            }
            catch (Exception ex)
            {
                // Raised before the worker started (for example an unusable update ID): nothing ran.
                Run.Log("      not attempted: " + ex.Message, LogSeverity.Warning);
                result.NotAttempted++;
                continue;
            }
            if (raw.RestartFirst)
            {
                Run.Log("      not attempted: Windows requires a restart before it installs anything else.");
                result.NotAttempted++;
                result.RebootRequired = true;
                restartFirst = true;
                continue;
            }
            // Nothing was downloaded or installed for these, so they change nothing on the device.
            if (!raw.Started)
            {
                Run.Log("      not attempted: the Windows Update worker could not be started.", LogSeverity.Warning);
                result.NotAttempted++;
                continue;
            }
            if (raw.NotOffered)
            {
                Run.Log("      not attempted: the update is no longer offered.");
                result.NotAttempted++;
                continue;
            }
            if (raw.Busy)
            {
                Run.Log("      not attempted: another installation is running.");
                result.NotAttempted++;
                continue;
            }
            if (raw.TimedOut)
            {
                // Stopping the worker ends our wait, not the work the update service was given.
                Run.Log("      stopped on its time limit; Windows Update may still be working on it.", LogSeverity.Warning);
                result.Attempted.Add(update.Id);
                result.TimedOut++;
                result.Failed++;
                result.FailedTitles.Add(update.Title);
                continue;
            }
            result.Attempted.Add(update.Id);
            if (raw.DownloadResult == 3)
            {
                Run.Log(string.Format(Invariant, "      download completed with errors ({0}); the install was tried and its result decides.", raw.DownloadCode), LogSeverity.Warning);
                result.DownloadErrors++;
                if (raw.DownloadCode.Length > 0 && raw.DownloadCode != "0x00000000") { result.Codes.Add(raw.DownloadCode); }
            }
            if (!raw.Ok)
            {
                var code = raw.ErrorCode;
                if (code.Length == 0 && raw.DownloadResult >= 0) { code = raw.DownloadCode; }
                var reason = raw.Error;
                if (reason.Length == 0 && raw.DownloadResult >= 0) { reason = "download " + ResultText(raw.DownloadResult); }
                Run.Log(string.Format(Invariant, "      failed ({0}): {1}", code, reason), LogSeverity.Warning);
                result.Failed++;
                result.FailedTitles.Add(update.Title);
                if (code.Length > 0 && code != "0x00000000") { result.Codes.Add(code); }
                continue;
            }
            if (raw.InstallResult == 2 || raw.InstallResult == 3)
            {
                result.Installed++;
                result.InstalledTitles.Add(update.Title);
                var state = "installed";
                if (raw.InstallResult == 3)
                {
                    state = string.Format(Invariant, "installed with errors ({0})", raw.InstallCode);
                    result.WithErrors++;
                    if (raw.InstallCode.Length > 0 && raw.InstallCode != "0x00000000") { result.Codes.Add(raw.InstallCode); }
                }
                if (raw.RebootRequired)
                {
                    result.RebootRequired = true;
                    state += "; restart required";
                }
                Run.Log("      " + state);
            }
            else
            {
                Run.Log(string.Format(Invariant, "      install failed ({0}; {1})", raw.InstallCode, ResultText(raw.InstallResult)), LogSeverity.Warning);
                result.Failed++;
                result.FailedTitles.Add(update.Title);
                if (raw.InstallCode.Length > 0 && raw.InstallCode != "0x00000000") { result.Codes.Add(raw.InstallCode); }
            }
        }
        return result;
    }

    /// <summary>Up to four items joined for a finding, with "..." when there are more.</summary>
    public static string JoinShortList(IReadOnlyList<string> items)
    {
        var text = string.Join("; ", items.Take(4));
        if (items.Count > 4) { text += "; ..."; }
        return text;
    }

    /// <summary>
    /// How every route that does not end in a restart finishes: search, then install what is
    /// offered. ChangedDevice says whether this changed the device (a cache reset, or at least one
    /// update that was downloaded or installed, successfully or not); only then does the caller
    /// start the repair cooldown. A search that finds nothing changes nothing.
    /// resetOnSearchFailure: on the "behind, nothing failing" route a failing search is the first
    /// evidence of a local fault, and so the first reason for a cache reset: the download cache
    /// first; the update database (DataStore) only when the search still fails right after that,
    /// or when dataStoreEvidence (a 0x80248xxx code) says so. searchResult: a search this run
    /// already made, used instead of searching again.
    /// </summary>
    public virtual WuSearchAndInstall SearchAndInstall(bool resetOnSearchFailure = false, WuSearch searchResult = null, bool dataStoreEvidence = false)
    {
        var result = new WuSearchAndInstall();
        var cooldownHours = Run.Config.WURepairCooldownHours;

        WuSearch search;
        if (searchResult != null)
        {
            Run.Log("Using the search this run already made; nothing has changed since.");
            search = searchResult;
        }
        else
        {
            search = Search();
        }
        // A search that failed or hung is evidence of a local fault; one that never started is not.
        if (!search.Ok && !search.NotStarted && resetOnSearchFailure)
        {
            Run.Log(string.Format(Invariant, "The Windows Update search failed: {0} {1}", search.ErrorCode, search.Error), LogSeverity.Warning);
            var withDataStore = dataStoreEvidence || HasDataStoreCode(new[] { search.ErrorCode });
            Run.Log("The update client itself fails, which is the first evidence of a local fault on this route. Clearing the update cache, then searching once more.");
            var reset = CacheReset(withDataStore);
            result.Warnings += reset.Warnings;
            if (reset.Changed) { result.ChangedDevice = true; }
            if (reset.Done)
            {
                search = Search();
                if (!search.Ok && !search.NotStarted && !withDataStore)
                {
                    Run.Log("The search still fails right after the download cache was cleared. Resetting the update database (DataStore) as well, then searching once more.", LogSeverity.Warning);
                    reset = CacheReset(true);
                    result.Warnings += reset.Warnings;
                    if (reset.Changed) { result.ChangedDevice = true; }
                    if (reset.Done) { search = Search(); }
                }
            }
        }
        if (!search.Ok && search.NotStarted)
        {
            Run.Log(string.Format(Invariant, "Windows Update was not asked what it offers: {0}.", search.Error), LogSeverity.Warning);
            Run.AddFinding(FindingLevel.Warning, "WU-SEARCH-NOT-STARTED", "Windows Update was not asked what it offers this device",
                string.Format(Invariant, "The search did not start ({0}), so nothing was installed on this run.", search.Error),
                "Run the repair again; the diagnosis above stays valid.");
            result.Warnings++;
            return result;
        }
        if (!search.Ok)
        {
            Run.Log(string.Format(Invariant, "The Windows Update search failed: {0} {1}", search.ErrorCode, search.Error), LogSeverity.Warning);
            var detail = string.Format(Invariant, "The search ended with {0}: {1}", search.ErrorCode, search.Error);
            var next = "Check the network connection and run the repair again. If it keeps failing, create a support package and contact your IT support.";
            var support = "Run Check Microsoft connections (on I can't connect). If the update endpoints are reachable, the support package has the Windows Update logs.";
            if (search.TimedOut)
            {
                detail = string.Format(Invariant, "The search did not finish within its time limit ({0}).", search.Error);
                next = "Run the repair again when the device is idle and on a stable network.";
            }
            else
            {
                var key = CodeKnowledge.Key(search.ErrorCode);
                if (key != null && WuCodes.DeliveryCodes.Contains(key))
                {
                    Run.Log("That code means the device cannot reach the update service: a network or proxy problem, not something to repair on the device.");
                }
                var knowledge = CodeKnowledge.Get(search.ErrorCode);
                if (knowledge != null)
                {
                    detail = string.Format(Invariant, "The search ended with {0} ({1}): {2}", knowledge.Code, knowledge.Name, knowledge.Meaning);
                    next = knowledge.NextStep;
                    support = knowledge.Support;
                }
            }
            Run.AddFinding(FindingLevel.Error, "WU-SEARCH-FAILED", "Windows Update could not be asked what it offers this device", detail, next, support);
            result.SearchFailed = true;
            result.Warnings++;
            return result;
        }

        foreach (var title in search.Other) { Run.PersistentLog("Offered, left to Windows: " + title); }
        if (search.Other.Count > 0)
        {
            Run.AddFinding(FindingLevel.Info, "WU-OFFERED-OTHER", TextFormat.Count(search.Other.Count, "other update") + " offered and left to Windows",
                JoinShortList(search.Other),
                "Nothing to do: this repair installs quality and security updates only. Feature updates and drivers follow the update settings of your organisation.");
        }
        result.HiddenCount = search.Hidden.Count;
        foreach (var title in search.Hidden.Take(10)) { Run.PersistentLog("Hidden on this device, so never offered or installed by this repair: " + title); }
        if (result.HiddenCount > 0)
        {
            var hidden = TextFormat.Count(result.HiddenCount, "update is", "updates are");
            Run.Log(hidden + " hidden on this device and never offered (titles in the log).", LogSeverity.Warning);
            Run.AddFinding(FindingLevel.Warning, "WU-HIDDEN-UPDATES", hidden + " hidden on this device",
                JoinShortList(search.Hidden),
                "If updates stay missing, contact your IT support.",
                "A hidden update is never offered. If a cumulative update is among them, find out who hid it, and why, before unhiding it.");
        }
        if (search.Install.Count == 0)
        {
            result.NothingOffered = true;
            Run.Log("Windows Update offers this device no quality update to install.");
            return result;
        }

        var left = Run.MinutesLeft();
        if (left < WuCodes.InstallMinMinutes)
        {
            Run.Log(string.Format(Invariant, "Windows Update offers {0}, but {1} of this run left and an install can need {2}. Nothing is installed now; run the repair again.",
                TextFormat.Count(search.Install.Count, "update"), TextFormat.Count(Math.Max(0, left), "minute"), TextFormat.Count(WuCodes.InstallMinMinutes, "minute")), LogSeverity.Warning);
            foreach (var update in search.Install) { Run.Log("  Offered: " + update.Title); }
            result.Warnings++;
            return result;
        }

        Run.Log(string.Format(Invariant, "Installing {0}, one at a time. Download and install can take 10 to 40 minutes.", TextFormat.Count(search.Install.Count, "offered update")));
        var install = InstallUpdates(search.Install);
        if (install.Attempted.Count > 0)
        {
            WriteHistory(install.Attempted, heading: "Update history for the updates this run attempted");
        }
        foreach (var title in install.InstalledTitles) { Run.AddChange("Installed " + title); }
        if (install.Installed > 0)
        {
            Run.AddFinding(FindingLevel.Ok, "WU-INSTALLED", "Installed " + TextFormat.Count(install.Installed, "update"), string.Join("; ", install.InstalledTitles));
        }
        if (install.Failed > 0)
        {
            var codes = install.Codes.Distinct().ToList();
            var codeParts = codes.Select(c =>
            {
                var k = CodeKnowledge.Get(c);
                return k != null ? string.Format(Invariant, "{0} ({1})", c, k.Name) : c;
            }).ToList();
            var next = "Restart, then run the repair again: a recognised code gets its own repair.";
            if (cooldownHours > 0)
            {
                var availableAt = RunContext.UserTime(Services.Clock.UtcNow.AddHours(cooldownHours), "yyyy-MM-dd HH:mm");
                next = string.Format(Invariant, "Restart, then run the repair again: a recognised code gets its own repair. The {0} cooldown allows that from about {1}.",
                    TextFormat.Count(cooldownHours, "hour"), availableAt);
            }
            var support = "";
            if (codes.Count > 0)
            {
                var first = CodeKnowledge.Get(codes[0]);
                if (first != null)
                {
                    next = first.NextStep + " " + next;
                    support = first.Support;
                }
            }
            var codeText = codeParts.Count > 0 ? "codes " + string.Join(", ", codeParts) : "no error code was returned";
            if (install.TimedOut > 0) { codeText += string.Format(Invariant, "; {0} stopped on the time limit", install.TimedOut); }
            Run.AddFinding(FindingLevel.Error, "WU-INSTALL-FAILED", TextFormat.Count(install.Failed, "update failed", "updates failed") + " to install",
                string.Format(Invariant, "{0}; {1}.", string.Join("; ", install.FailedTitles), codeText), next, support);
        }
        if (install.NotAttempted > 0)
        {
            Run.AddFinding(FindingLevel.Info, "WU-NOT-ATTEMPTED", TextFormat.Count(install.NotAttempted, "offered update was", "offered updates were") + " not attempted",
                "Windows wanted a restart first, or this run had no time left for another install.",
                "They stay offered: Windows installs them, or run the repair again after a restart.");
            Run.Log("The updates that were not attempted stay offered; Windows Update or the next run of this repair installs them.");
            result.Warnings++;
        }
        result.Installed = install.Installed;
        result.RebootRequired = install.RebootRequired;
        if (install.Attempted.Count > 0) { result.ChangedDevice = true; }
        var summary = string.Format(Invariant, "Installed {0} (of which {1} with errors), failed {2}, not attempted {3}.", install.Installed, install.WithErrors, install.Failed, install.NotAttempted);
        Run.Log(summary, install.Failed > 0 || install.WithErrors > 0 ? LogSeverity.Warning : LogSeverity.Info);
        result.Warnings += install.Failed + install.WithErrors + install.DownloadErrors;
        if (install.Codes.Count > 0)
        {
            var codeList = string.Join(", ", install.Codes.Distinct());
            if (cooldownHours > 0)
            {
                Run.Log(string.Format(Invariant, "Codes: {0}. Run the repair again after a restart; the {1} cooldown allows that from about {2}, and the diagnosis can be read at any time.",
                    codeList, TextFormat.Count(cooldownHours, "hour"), RunContext.UserTime(Services.Clock.UtcNow.AddHours(cooldownHours), "yyyy-MM-dd HH:mm")));
            }
            else
            {
                Run.Log(string.Format(Invariant, "Codes: {0}. Run the repair again after a restart.", codeList));
            }
        }
        return result;
    }
}
