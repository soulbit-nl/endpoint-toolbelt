using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EndpointToolbelt.Engine.WindowsUpdate;

// The results the steps of Repair Windows Update hand each other.

/// <summary>Is this device on the quality update it should have by now?</summary>
public sealed class WuCurrency
{
    public DateTime ExpectedMonth { get; set; }
    public DateTime? LastPatchMonth { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public string LastTitle { get; set; }
    public int? MonthsBehind { get; set; }
    public bool IsCurrent { get; set; }
    public string Source { get; set; } = "none";
}

/// <summary>One update failure event: its UTC time, its values as text, its message and its log ("client" or "setup").</summary>
public sealed class WuFailureEvent
{
    public DateTime TimeCreatedUtc { get; set; }
    public IReadOnlyList<string> Values { get; set; } = Array.Empty<string>();
    public string Message { get; set; } = "";
    public string Source { get; set; } = "client";
}

/// <summary>Failure codes logged since the last successful quality update, by what they route.</summary>
public sealed class WuFailureCodes
{
    public List<string> Known { get; set; } = new();
    public List<string> Unknown { get; set; } = new();
    public List<string> Ignored { get; set; } = new();
    public List<string> Unconfirmed { get; set; } = new();
}

/// <summary>Whether disconnected devices may be removed on a 0x800719e4 failure.</summary>
public sealed class GhostAssessment
{
    public bool Eligible { get; set; }
    public List<PnpDeviceInfo> Candidates { get; set; } = new();
    public bool Signature { get; set; }
    public List<string> Reasons { get; set; } = new();

    /// <summary>Counts per bus (the first part of the instance ID), in the order they were first seen.</summary>
    public List<KeyValuePair<string, int>> ByEnumerator { get; set; } = new();

    public int Count(string enumerator) => ByEnumerator.Where(p => p.Key == enumerator).Select(p => p.Value).FirstOrDefault();
}

/// <summary>A baseline RollupFix package of the servicing inventory.</summary>
public sealed class StaleRollup
{
    public string Name { get; set; } = "";
    public int? Ubr { get; set; }
    public int State { get; set; }
}

/// <summary>Where the device is told to get its updates.</summary>
public sealed class UpdateSourceState
{
    public bool UsesWsus { get; set; }
    public string WsusServer { get; set; } = "";
    public bool WsusPolicy { get; set; }
}

/// <summary>An offered update this repair installs.</summary>
public sealed class WuOffer
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
}

/// <summary>
/// What this device is offered. NotStarted means nothing was asked (no time left, or the worker
/// could not start); that is no evidence of a fault on the device.
/// </summary>
public sealed class WuSearch
{
    public bool Ok { get; set; }
    public bool NotStarted { get; set; }
    public bool TimedOut { get; set; }
    public string ErrorCode { get; set; } = "";
    public string Error { get; set; } = "";
    public List<WuOffer> Install { get; set; } = new();
    public List<string> Other { get; set; } = new();
    public List<string> Hidden { get; set; } = new();
}

/// <summary>The outcome of installing the offered updates one at a time.</summary>
public sealed class WuInstallSet
{
    public int Installed { get; set; }
    public int WithErrors { get; set; }
    public int Failed { get; set; }
    public int NotAttempted { get; set; }
    public int DownloadErrors { get; set; }
    public int TimedOut { get; set; }
    public bool RebootRequired { get; set; }
    public List<string> Codes { get; } = new();
    public List<string> Attempted { get; } = new();
    public List<string> InstalledTitles { get; } = new();
    public List<string> FailedTitles { get; } = new();
}

/// <summary>How a route that ends in a search and install finished.</summary>
public sealed class WuSearchAndInstall
{
    public int Warnings { get; set; }
    public bool RebootRequired { get; set; }
    public int Installed { get; set; }
    public bool NothingOffered { get; set; }
    public bool SearchFailed { get; set; }
    public bool ChangedDevice { get; set; }
    public int HiddenCount { get; set; }
}

/// <summary>The outcome of a cache reset: Changed (something was deleted), Done (it completed), Skipped (it did not start).</summary>
public sealed class CacheResetResult
{
    public int Warnings { get; set; }
    public bool Changed { get; set; }
    public bool Done { get; set; }
    public bool Skipped { get; set; }
}

/// <summary>One folder cleared: items deleted, items left, and what stopped it ("", "time" or "services").</summary>
public sealed class FolderClearResult
{
    public int Removed { get; set; }
    public int Remaining { get; set; }
    public string StoppedBy { get; set; } = "";
}

/// <summary>Whether an update is being installed right now, and why.</summary>
public sealed class InstallBusy
{
    public bool Busy { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>The component store check and repair.</summary>
public sealed class ComponentRepairResult
{
    public int Warnings { get; set; }
    public bool RestartNeeded { get; set; }
    public bool Changed { get; set; }

    /// <summary>Clean, Repairable, NotRepairable, Unclear, Failed, TimedOut or NotRun.</summary>
    public string Verdict { get; set; } = "NotRun";

    public bool Completed { get; set; }
}

/// <summary>The SFC verdict read from the [SR] lines of CBS.log.</summary>
public sealed class SfcCbsVerdict
{
    public int Lines { get; set; }
    public bool Repaired { get; set; }
    public bool Unfixed { get; set; }
    public bool Verified { get; set; }

    /// <summary>Unfixed, Repaired, Clean or Unclear.</summary>
    public string Verdict { get; set; } = "Unclear";
}

/// <summary>The SFC verdict from CBS.log, its exit code and its own text together.</summary>
public sealed class SfcVerdict
{
    public string Verdict { get; set; } = "Unclear";
    public bool AlsoRepaired { get; set; }
    public bool FromCbs { get; set; }
}

/// <summary>The removal of disconnected devices.</summary>
public sealed class GhostRemoval
{
    public int Removed { get; set; }
    public int Failed { get; set; }
    public int NotTried { get; set; }
    public int Skipped { get; set; }
}

/// <summary>A code table entry of a DISM exit (same form as the shared code table).</summary>
public sealed class DismExitEntry
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Meaning { get; set; } = "";
    public string NextStep { get; set; } = "";
    public string Support { get; set; } = "";
}

/// <summary>
/// The JSON line of the Windows Update worker, with the defaults for a missing field:
/// Started true, the result codes -1, everything else empty.
/// </summary>
public sealed class WuWorkerReply
{
    public bool Ok { get; set; }
    public bool Started { get; set; } = true;
    public bool TimedOut { get; set; }
    public string ErrorCode { get; set; } = "";
    public string Error { get; set; } = "";
    public bool NotOffered { get; set; }
    public bool Busy { get; set; }
    public bool RestartFirst { get; set; }
    public int DownloadResult { get; set; } = -1;
    public string DownloadCode { get; set; } = "";
    public int InstallResult { get; set; } = -1;
    public string InstallCode { get; set; } = "";
    public bool RebootRequired { get; set; }
    public List<WuOffer> Install { get; set; } = new();
    public List<string> Other { get; set; } = new();
    public List<string> Hidden { get; set; } = new();

    /// <summary>Reads one worker line; throws when it is not JSON.</summary>
    public static WuWorkerReply Parse(string line)
    {
        var json = JsonNode.Parse(line);
        var reply = new WuWorkerReply
        {
            Ok = Flag(json, "ok", false),
            // The engine's own fields: a worker line that carries them is read as it says.
            Started = Flag(json, "started", true),
            TimedOut = Flag(json, "timedOut", false),
            ErrorCode = Text(json, "errorCode"),
            Error = Text(json, "error"),
            NotOffered = Flag(json, "notOffered", false),
            Busy = Flag(json, "busy", false),
            RestartFirst = Flag(json, "restartFirst", false),
            DownloadResult = Number(json, "downloadResult"),
            DownloadCode = Text(json, "downloadCode"),
            InstallResult = Number(json, "installResult"),
            InstallCode = Text(json, "installCode"),
            RebootRequired = Flag(json, "rebootRequired", false),
        };
        var install = json.Get("install");
        if (install != null)
        {
            reply.Install = install.Items.Where(i => i.Kind == JsonNode.NodeKind.Map)
                .Select(i => new WuOffer { Id = i.Get("id")?.AsText() ?? "", Title = i.Get("title")?.AsText() ?? "" })
                .Where(o => o.Id.Length > 0).ToList();
        }
        reply.Other = Texts(json.Get("other"));
        reply.Hidden = Texts(json.Get("hidden"));
        return reply;
    }

    private static List<string> Texts(JsonNode array) =>
        array == null ? new List<string>() : array.Items.Where(i => i.Kind != JsonNode.NodeKind.Null && i.AsText().Length > 0).Select(i => i.AsText()).ToList();

    private static string Text(JsonNode json, string name) => json.Get(name)?.AsText() ?? "";

    private static bool Flag(JsonNode json, string name, bool fallback)
    {
        var node = json.Get(name);
        if (node == null) { return fallback; }
        return node.Kind == JsonNode.NodeKind.Boolean ? node.Text == "true" : node.AsText().Length > 0;
    }

    private static int Number(JsonNode json, string name)
    {
        var node = json.Get(name);
        if (node == null) { return -1; }
        return int.TryParse(node.AsText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : -1;
    }
}
