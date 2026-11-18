using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.Actions;

// Low-risk device actions of 1.0: back up the BitLocker recovery key, sync
// the clock, update Defender's security intelligence, free up disk space. Each reports success
// only on evidence that the effect happened.

/// <summary>Shared checks of the device actions.</summary>
internal static class DeviceChecks
{
    private static readonly Regex AzureAdJoined = new(@"(?m)^\s*AzureAdJoined\s*:\s*YES\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>The system drive as "C:", from the engine's environment.</summary>
    public static string SystemDrive(RunContext run) => run.Services.GetEnvironmentVariable("SystemDrive") ?? "C:";

    /// <summary>The device is joined to Microsoft Entra ID, as dsregcmd reports it to SYSTEM.</summary>
    public static bool IsEntraJoined(RunContext run)
    {
        var result = run.RunTool(new ProcessRequest { FilePath = ProcessRunner.SystemTool("dsregcmd.exe"), Arguments = "/status", Label = "dsregcmd /status", TimeoutMinutes = 1 });
        return result.Started && !result.TimedOut && AzureAdJoined.IsMatch(result.Output ?? "");
    }
}

/// <summary>Backs up the BitLocker recovery key to Entra ID.</summary>
public sealed class BackupRecoveryKeyHandler : IActionHandler
{
    private const string ManagementLog = "Microsoft-Windows-BitLocker/BitLocker Management";

    /// <summary>Each backup call's own time limit, in minutes (the action's limit is 10).</summary>
    internal const double BackupCallMinutes = 2;

    /// <summary>
    /// Backs up the OS volume's existing recovery-password protectors to Entra ID with
    /// BackupToAAD-BitLockerKeyProtector, through the in-box cmdlet runner. Never adds, removes or
    /// suspends a protector, and never reads the recovery password itself: only protector IDs and
    /// types are used.
    /// </summary>
    public int? Run(RunContext run)
    {
        var drive = DeviceChecks.SystemDrive(run);
        run.StepHeader(1, 2, "Check BitLocker on the system drive");
        BitLockerVolumeInfo volume;
        try
        {
            volume = run.Services.Device.ReadBitLocker(drive);
        }
        catch (BitLockerUnavailableException)
        {
            // The text for no BitLocker namespace, class or volume.
            run.AddFinding(FindingLevel.Warning, "BITLOCKER-NOT-AVAILABLE", "BitLocker is not available on this device",
                "Windows reports no BitLocker volume here, so there is no recovery key to back up. Nothing was changed.",
                "Contact your IT support if your organization expects BitLocker on this device.");
            return EngineExitCode.Warnings;
        }
        catch (Exception ex)
        {
            run.AddFinding(FindingLevel.Error, "BITLOCKER-UNREADABLE", "BitLocker could not be read", ex.Message, "Contact your IT support.");
            return EngineExitCode.Failed;
        }
        var ids = volume.RecoveryPasswordIds;
        run.Log(string.Format(CultureInfo.InvariantCulture, "BitLocker on {0}: volume status {1}, protection {2}, {3}.",
            drive, volume.VolumeStatus, volume.ProtectionStatus, TextFormat.Count(ids.Count, "recovery password")));
        if (volume.VolumeStatus == "FullyDecrypted")
        {
            run.AddFinding(FindingLevel.Warning, "BITLOCKER-OFF", "BitLocker is off on the system drive",
                "There is no recovery key to back up. Nothing was changed.",
                "Contact your IT support: turning BitLocker on is done by your organization's policy.");
            return EngineExitCode.Warnings;
        }
        if (ids.Count == 0)
        {
            run.AddFinding(FindingLevel.Warning, "BITLOCKER-NO-RECOVERY-PASSWORD", "The system drive has no recovery password to back up",
                "BitLocker protects the drive without a recovery password protector. This action only backs up existing ones; it never adds one. Nothing was changed.",
                "Contact your IT support.", "Add a recovery password protector through your BitLocker policy, then run the backup again.");
            return EngineExitCode.Warnings;
        }
        if (!DeviceChecks.IsEntraJoined(run))
        {
            run.AddFinding(FindingLevel.Warning, "BITLOCKER-NOT-JOINED", "This device is not joined to Microsoft Entra ID",
                "A recovery key can only be backed up to Entra ID from a joined device. Nothing was changed.",
                "Contact your IT support.");
            return EngineExitCode.Warnings;
        }

        run.StepHeader(2, 2, "Back up the recovery key to Microsoft Entra ID");
        var start = run.Services.Clock.UtcNow.AddSeconds(-5);
        var failed = 0;
        var blocked = false;
        foreach (var id in ids)
        {
            InboxResult result;
            try
            {
                result = run.RunInboxCmdlet(InboxCommand.BackupBitLockerKeyProtector, TimeSpan.FromMinutes(BackupCallMinutes), drive, id);
            }
            catch (ArgumentException)
            {
                // A drive or protector ID that is not exactly of its kind is never passed on.
                failed++;
                run.Log("The backup of a protector was not requested: its ID is not a valid protector ID.", LogSeverity.Warning);
                continue;
            }
            if (result.Outcome == InboxOutcome.Done)
            {
                run.Log(string.Format(CultureInfo.InvariantCulture, "Backup requested for protector {0}.", id));
                continue;
            }
            failed++;
            if (result.Outcome == InboxOutcome.PolicyRefused) { blocked = true; }
            run.Log(string.Format(CultureInfo.InvariantCulture, "The backup of protector {0} failed: {1}", id, Servicing.Describe(result)), LogSeverity.Warning);
        }
        if (blocked) { Servicing.AddPolicyRefusal(run, "BackupToAAD-BitLockerKeyProtector"); }

        // BitLocker writes its event shortly after the call returns.
        IReadOnlyList<EventInfo> events = Array.Empty<EventInfo>();
        for (var i = 0; i < 6; i++)
        {
            run.Services.Clock.Sleep(TimeSpan.FromSeconds(5));
            events = ReadManagementEvents(run, start);
            if (events.Count > 0) { break; }
        }
        var errorEvents = events.Where(e => e.Level <= 2).OrderBy(e => e.TimeCreatedUtc ?? DateTime.MinValue).ToList();
        var errors = errorEvents.Count;
        var informational = events.Count(e => e.Level == 4);
        run.Log(string.Format(CultureInfo.InvariantCulture, "BitLocker Management log since the start: {0}, {1}.",
            TextFormat.Count(informational, "information event"), TextFormat.Count(errors, "error")));
        foreach (var e in errorEvents)
        {
            var text = BitLockerEventText.Clean(e.Message);
            run.Log(string.Format(CultureInfo.InvariantCulture, "BitLocker error event {0}: {1}", e.Id, text.Length > 0 ? text : "(no message)"), LogSeverity.Warning);
        }
        if (failed > 0 || errors > 0)
        {
            var autoInit = AutomaticEncryptionEvents(run);
            NoteAutomaticEncryptionThisStart(run, autoInit, errorEvents, drive);
            var telling = BitLockerEventText.MostTelling(errorEvents);
            var eventLine = telling == null ? "" : string.Format(CultureInfo.InvariantCulture, "Event {0}: {1}", telling.Id, BitLockerEventText.Clean(telling.Message));
            if (errorEvents.Any(e => BitLockerEventText.IsKeyLimit(e.Message)))
            {
                run.AddFinding(FindingLevel.Error, "BITLOCKER-KEY-LIMIT", "Microsoft Entra ID holds the maximum number of recovery keys for this device",
                    "Trying again does not help: this device already has the maximum number of BitLocker recovery keys stored in Microsoft Entra ID, so the new one was refused. Nothing on the drive was changed.",
                    "Contact your IT support.",
                    KeyLimitSupport(autoInit, eventLine));
                return EngineExitCode.Failed;
            }
            run.AddFinding(FindingLevel.Error, "BITLOCKER-BACKUP-FAILED", "The recovery key could not be backed up to Entra ID",
                string.Format(CultureInfo.InvariantCulture, "{0} of {1} failed or BitLocker reported an error. Nothing on the drive was changed.", failed, TextFormat.Count(ids.Count, "backup")),
                "Try again later while connected to the network. If it keeps failing, contact your IT support.",
                eventLine.Length > 0 ? eventLine : "The BitLocker Management event log on the device has the error.");
            return EngineExitCode.Failed;
        }
        if (informational == 0)
        {
            run.AddFinding(FindingLevel.Warning, "BITLOCKER-BACKUP-UNVERIFIED", "The backup was requested, but not confirmed",
                "BitLocker accepted the request without an error, but wrote no event that confirms the backup within 30 seconds.",
                "If your IT support still cannot see the key, run this again later.");
            return EngineExitCode.Warnings;
        }
        run.AddChange(string.Format(CultureInfo.InvariantCulture, "Backed up {0} of the system drive to Microsoft Entra ID", TextFormat.Count(ids.Count, "BitLocker recovery key")));
        run.AddFinding(FindingLevel.Ok, "BITLOCKER-BACKUP-DONE", "The recovery key is backed up to Microsoft Entra ID",
            "BitLocker confirmed the backup. Nothing on the drive was changed.", "Nothing else to do.");
        return EngineExitCode.Success;
    }

    /// <summary>How far back the automatic device encryption events are counted.</summary>
    internal const int AutomaticEncryptionDays = 30;

    /// <summary>
    /// The BitLocker Management log's events since a moment. Microsoft documents no event IDs for
    /// the backup, so success and failure rest on the levels only; the messages are shown to explain
    /// a failure.
    /// </summary>
    private static IReadOnlyList<EventInfo> ReadManagementEvents(RunContext run, DateTime sinceUtc, int eventId = 0)
    {
        try
        {
            return run.Services.EventLog.Read(ManagementLog, sinceUtc, 200, eventId);
        }
        catch (Exception ex)
        {
            run.Log("Could not read the BitLocker Management log: " + ex.Message, LogSeverity.Warning);
            return Array.Empty<EventInfo>();
        }
    }

    /// <summary>
    /// The events "Device Encryption initialized automatically for volume X:" (4146) of the last
    /// days, each with the volume its message names. Every such initialization adds a recovery key.
    /// </summary>
    private static List<KeyValuePair<string, EventInfo>> AutomaticEncryptionEvents(RunContext run)
    {
        var since = run.Services.Clock.UtcNow.AddDays(-AutomaticEncryptionDays);
        return ReadManagementEvents(run, since, BitLockerEventText.AutomaticEncryptionEventId)
            .Where(e => e.Id == BitLockerEventText.AutomaticEncryptionEventId)
            .Select(e => new KeyValuePair<string, EventInfo>(BitLockerEventText.VolumeOf(e.Message), e))
            .Where(p => p.Key.Length > 0)
            .ToList();
    }

    /// <summary>Logs when another volume was encrypted automatically in the same start of Windows as the failed backup.</summary>
    private static void NoteAutomaticEncryptionThisStart(RunContext run, List<KeyValuePair<string, EventInfo>> autoInit, IReadOnlyList<EventInfo> errorEvents, string drive)
    {
        DateTime? boot;
        try { boot = run.Services.BootUtc(); } catch (Exception) { boot = null; }
        if (boot is not DateTime start) { return; }
        var failedVolume = errorEvents.Select(e => BitLockerEventText.VolumeOf(e.Message)).FirstOrDefault(v => v.Length > 0) ?? drive;
        var others = autoInit
            .Where(p => p.Value.TimeCreatedUtc is DateTime t && t >= start && !string.Equals(p.Key, failedVolume, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (others.Count == 0) { return; }
        run.Log(string.Format(CultureInfo.InvariantCulture,
            "The backup of {0} failed in the same start of Windows in which device encryption was initialized automatically for {1} {2} (event {3}).",
            failedVolume, others.Count == 1 ? "volume" : "volumes", string.Join(", ", others), BitLockerEventText.AutomaticEncryptionEventId), LogSeverity.Warning);
    }

    /// <summary>The line for IT support when Entra ID refused the key because the device has the maximum number.</summary>
    private static string KeyLimitSupport(List<KeyValuePair<string, EventInfo>> autoInit, string eventLine)
    {
        var parts = new List<string> { "Remove old recovery keys of this device in Microsoft Entra ID, or find what creates new ones." };
        foreach (var group in autoInit.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture,
                "Event {0} shows that Windows initialized device encryption automatically for volume {1} {2} in the last {3} days, and each time adds a recovery key: if it is a disk that is encrypted again after every start, such as the temporary disk of a virtual machine, exclude it from automatic device encryption.",
                BitLockerEventText.AutomaticEncryptionEventId, group.Key, TextFormat.Count(group.Count(), "time"), AutomaticEncryptionDays));
        }
        if (eventLine.Length > 0) { parts.Add(eventLine); }
        return string.Join(" ", parts);
    }
}

/// <summary>
/// The useful part of a BitLocker Management event's message, as one line: without trace and
/// request IDs, without the device and tenant IDs Entra ID names, and without any other GUID.
/// </summary>
internal static class BitLockerEventText
{
    /// <summary>"Device Encryption initialized automatically for volume X:".</summary>
    internal const int AutomaticEncryptionEventId = 4146;

    internal const int MaxLength = 400;

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);
    private static readonly Regex IdField = new(@"\b(?:Trace|Request|Correlation|Activity)\s?Id\s*:\s*\S*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);
    private static readonly Regex DeviceOrTenant = new(@"\s*\b(?:for|in)\s+(?:device|tenant)\s*:\s*(?:'[^'\r\n]*'|\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);
    private static readonly Regex AnyGuid = new(@"\{?\b[0-9A-Fa-f]{8}-(?:[0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}\b\}?", RegexOptions.CultureInvariant, Limit);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, Limit);
    private static readonly Regex Volume = new(@"\bvolume\s+([A-Za-z]:)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    /// <summary>The message as one cleaned line, or "" when there is none or it cannot be cleaned in time.</summary>
    public static string Clean(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) { return ""; }
        string text;
        try
        {
            text = IdField.Replace(message, "");
            text = DeviceOrTenant.Replace(text, "");
            text = AnyGuid.Replace(text, "(ID left out)");
            text = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
            text = Whitespace.Replace(text, " ").Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            return "";
        }
        return text.Length > MaxLength ? text.Substring(0, MaxLength - 3).TrimEnd() + "..." : text;
    }

    /// <summary>Entra ID refused the key because the device already has the maximum number stored.</summary>
    public static bool IsKeyLimit(string message) =>
        message != null && (message.IndexOf("error_keys_exceed_max_limit", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf("maximum number of recovery passwords", StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>The volume the message names ("D:"), or "".</summary>
    public static string VolumeOf(string message)
    {
        var match = TimedRegex.MatchOrEmpty(Volume, message);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : "";
    }

    /// <summary>
    /// The error that explains the most: Entra ID's own error code for the key limit first, then
    /// the plain key-limit text, then any message with an error code, then any message at all.
    /// </summary>
    public static EventInfo MostTelling(IReadOnlyList<EventInfo> errors) =>
        errors.Select((e, i) => new { e, i, score = Score(e.Message) })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.i)
            .Select(x => x.e)
            .FirstOrDefault();

    private static int Score(string message)
    {
        if (Clean(message).Length == 0) { return 0; }
        if (message.IndexOf("error_keys_exceed_max_limit", StringComparison.OrdinalIgnoreCase) >= 0) { return 4; }
        if (IsKeyLimit(message)) { return 3; }
        if (message.IndexOf("Error Code", StringComparison.OrdinalIgnoreCase) >= 0) { return 2; }
        return 1;
    }
}

/// <summary>Syncs the clock.</summary>
public sealed class SyncClockHandler : IActionHandler
{
    internal const double ClockOkSeconds = 60;
    private const string ParametersKey = @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters";

    private static readonly Regex HostNamePattern = new(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,62}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,62}[A-Za-z0-9])?){0,10}$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex StripchartOffset = new(@",\s*([+-]?[0-9]{1,9}(?:[.,][0-9]{1,9})?)s\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly char[] Whitespace = { ' ', '\t', '\r', '\n' };

    /// <summary>
    /// Starts the Windows Time service when it is stopped and asks it to resynchronise. Never
    /// changes the time source or any time configuration. Success only when the offset afterwards
    /// is below one minute.
    /// </summary>
    public int? Run(RunContext run)
    {
        run.StepHeader(1, 3, "Measure the clock");
        var running = run.Services.ServiceControl.IsRunning("W32Time");
        var hostName = ClockSourceHost(run, running);
        double? before = hostName != null ? ClockOffset(run, hostName) : null;
        if (before != null) { run.Log(string.Format(CultureInfo.InvariantCulture, "Offset before: {0} seconds to {1}.", TextFormat.Number(before.Value, "0.###"), hostName)); }
        else { run.Log("The offset before the sync could not be measured."); }

        run.StepHeader(2, 3, "Synchronise with the time source");
        if (!running)
        {
            try
            {
                run.Services.ServiceControl.Start("W32Time", TimeSpan.FromMinutes(1));
                run.AddChange("Started the Windows Time service");
            }
            catch (Exception ex)
            {
                run.AddFinding(FindingLevel.Error, "CLOCK-SERVICE-NOT-STARTED", "The Windows Time service could not be started", ex.Message, "Contact your IT support.");
                return EngineExitCode.Failed;
            }
        }
        var resync = run.RunTool(new ProcessRequest { FilePath = ProcessRunner.SystemTool("w32tm.exe"), Arguments = "/resync", Label = "w32tm /resync", TimeoutMinutes = 2 });
        var resynced = resync.Started && !resync.TimedOut && resync.ExitCode == 0;

        run.StepHeader(3, 3, "Check the clock again");
        hostName ??= ClockSourceHost(run, true);
        double? after = hostName != null ? ClockOffset(run, hostName) : null;
        if (after != null && Math.Abs(after.Value) < ClockOkSeconds)
        {
            if (resynced) { run.AddChange("Synchronised the clock with its time source"); }
            run.AddFinding(FindingLevel.Ok, "CLOCK-OK", "The clock is correct",
                string.Format(CultureInfo.InvariantCulture, "It is {0} seconds off its time source.", TextFormat.Number(Math.Abs(after.Value), "0.#")), "Nothing else to do.");
            return EngineExitCode.Success;
        }
        if (after != null)
        {
            run.AddFinding(FindingLevel.Error, "CLOCK-STILL-OFF", "The clock is still off after the sync",
                string.Format(CultureInfo.InvariantCulture, "It is {0} seconds off its time source. The time source or the network may block the correction.", TextFormat.Number(Math.Abs(after.Value), "0")),
                "Contact your IT support.", "Check the time source and that UDP 123 to it is open.");
            return EngineExitCode.Failed;
        }
        if (resynced)
        {
            run.AddFinding(FindingLevel.Warning, "CLOCK-NOT-VERIFIED", "The clock was synchronised, but could not be checked",
                "The time service accepted the sync, but the offset to the time source could not be measured afterwards.",
                "Check the Clock row in the device health view in a few minutes.");
            return EngineExitCode.Warnings;
        }
        run.AddFinding(FindingLevel.Error, "CLOCK-SYNC-FAILED", "The clock could not be synchronised",
            string.Format(CultureInfo.InvariantCulture, "w32tm /resync ended with {0}, and the offset could not be measured.", TextFormat.ExitCode(resync.ExitCode)),
            "Contact your IT support.", "Check the time source and that UDP 123 to it is open.");
        return EngineExitCode.Failed;
    }

    /// <summary>
    /// The host to compare the clock with: the running time service's current source, or the
    /// first configured time server when the service is stopped (normal outside a domain). Only a
    /// plain host name is returned; it becomes an argument of w32tm.
    /// </summary>
    internal static string ClockSourceHost(RunContext run, bool running)
    {
        string hostName = null;
        if (running)
        {
            var query = run.RunTool(new ProcessRequest { FilePath = ProcessRunner.SystemTool("w32tm.exe"), Arguments = "/query /source", Label = "w32tm /query /source", TimeoutMinutes = 0.5 });
            var first = (query.Output ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (first != null) { hostName = first.Split(',')[0].Trim(); }
        }
        if (!IsPlainHost(hostName))
        {
            hostName = null;
            try
            {
                var type = MachineRegistry.AsText(run.Services.Registry.GetValue(ParametersKey, "Type")) ?? "";
                if (type.ToUpperInvariant() is "NTP" or "ALLSYNC")
                {
                    var servers = MachineRegistry.AsText(run.Services.Registry.GetValue(ParametersKey, "NtpServer")) ?? "";
                    var first = servers.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (first != null) { hostName = first.Split(',')[0].Trim(); }
                }
            }
            catch (Exception)
            {
                hostName = null;
            }
        }
        return IsPlainHost(hostName) ? hostName : null;
    }

    private static bool IsPlainHost(string hostName) => !string.IsNullOrEmpty(hostName) && hostName.IndexOf('.') >= 0 && HostNamePattern.IsMatch(hostName);

    /// <summary>The offset in seconds from "hh:mm:ss, +00.0123456s" (either decimal separator), or null.</summary>
    internal static double? ParseStripchart(string text)
    {
        if (string.IsNullOrEmpty(text)) { return null; }
        var m = StripchartOffset.Match(text);
        if (!m.Success) { return null; }
        return double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>The clock's offset to a host in seconds, measured with one sample, or null.</summary>
    private static double? ClockOffset(RunContext run, string hostName)
    {
        var result = run.RunTool(new ProcessRequest
        {
            FilePath = ProcessRunner.SystemTool("w32tm.exe"),
            Arguments = "/stripchart /computer:" + hostName + " /dataonly /samples:1",
            Label = "w32tm /stripchart",
            TimeoutMinutes = 0.5,
        });
        if (!result.Started || result.TimedOut) { return null; }
        return ParseStripchart(result.Output);
    }
}

/// <summary>Updates Defender's security intelligence.</summary>
public sealed class UpdateDefenderHandler : IActionHandler
{
    private static readonly Regex OtherAntivirus = new("Passive|EDR", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex BuildSuffix = new(@"-[0-9]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Finds Defender's update tool; tests replace it.</summary>
    internal Func<RunContext, string> FindMpCmdRun { get; set; } = MpCmdRunPath;

    /// <summary>
    /// Asks Microsoft Defender Antivirus to update its security intelligence. Changes no Defender
    /// setting. When another antivirus is in charge (Defender passive, in EDR block mode or off) it
    /// changes nothing. Success only when the version or its update time changed.
    /// </summary>
    public int? Run(RunContext run)
    {
        run.StepHeader(1, 2, "Check Microsoft Defender");
        var before = run.Services.Device.ReadDefender();
        if (before == null)
        {
            run.AddFinding(FindingLevel.Warning, "DEFENDER-MISSING", "Microsoft Defender Antivirus could not be found",
                "Its status cannot be read on this device. Nothing was changed.", "Contact your IT support if this device should use Microsoft Defender.");
            return EngineExitCode.NothingToRepair;
        }
        run.Log(string.Format(CultureInfo.InvariantCulture, "Defender: mode \"{0}\", service {1}, security intelligence {2}.", before.Mode, before.ServiceEnabled ? "on" : "off", before.Version));
        if (!before.ServiceEnabled || OtherAntivirus.IsMatch(before.Mode ?? ""))
        {
            run.AddFinding(FindingLevel.Info, "DEFENDER-NOT-PRIMARY", "Another antivirus is in charge on this device",
                "Microsoft Defender Antivirus is not the active antivirus, so its security intelligence is not updated here. Nothing was changed.",
                "Nothing to do here; your other antivirus updates itself.");
            return EngineExitCode.NothingToRepair;
        }
        var tool = FindMpCmdRun(run);
        if (tool == null)
        {
            run.AddFinding(FindingLevel.Error, "DEFENDER-TOOL-UNTRUSTED", "Defender's update tool could not be used",
                "MpCmdRun.exe was not found in a protected folder with a valid Microsoft signature. Nothing was changed.", "Contact your IT support.");
            return EngineExitCode.Failed;
        }

        run.StepHeader(2, 2, "Update the security intelligence");
        var limit = run.StepLimit(10, "The Defender update");
        if (limit <= 0) { return EngineExitCode.Failed; }
        var result = run.RunTool(new ProcessRequest { FilePath = tool, Arguments = "-SignatureUpdate", Label = "MpCmdRun -SignatureUpdate", TimeoutMinutes = limit });
        var after = run.Services.Device.ReadDefender();
        var changed = after != null && (!string.Equals(after.Version, before.Version, StringComparison.OrdinalIgnoreCase) ||
            (after.UpdatedUtc != null && (before.UpdatedUtc == null || after.UpdatedUtc > before.UpdatedUtc)));
        if (changed)
        {
            run.AddChange("Updated Microsoft Defender security intelligence to " + after.Version);
            run.AddFinding(FindingLevel.Ok, "DEFENDER-UPDATED", "Defender's security intelligence is up to date",
                string.Format(CultureInfo.InvariantCulture, "Version {0} (was {1}).", after.Version, before.Version), "Nothing else to do.");
            return EngineExitCode.Success;
        }
        if (result.Started && !result.TimedOut && result.ExitCode == 0)
        {
            run.AddFinding(FindingLevel.Info, "DEFENDER-ALREADY-CURRENT", "Defender's security intelligence was already the newest",
                string.Format(CultureInfo.InvariantCulture, "Version {0}; the update found nothing newer. Nothing was changed.", before.Version), "Nothing else to do.");
            return EngineExitCode.NothingToRepair;
        }
        run.AddFinding(FindingLevel.Error, "DEFENDER-UPDATE-FAILED", "Defender's security intelligence could not be updated",
            "MpCmdRun ended with " + TextFormat.ExitCode(result.ExitCode) + ".",
            "Check the network connection and try again later. If it keeps failing, contact your IT support.",
            "The update source of Defender (Windows Update, WSUS, a file share) may be unreachable from this device.");
        return EngineExitCode.Failed;
    }

    /// <summary>
    /// MpCmdRun.exe from the newest version folder under Defender's Platform folder, where
    /// Microsoft documents the current copy, else from its Program Files folder. Refused unless the
    /// folder and the file are writable only by SYSTEM, Administrators or TrustedInstaller and the
    /// file carries a valid Microsoft signature.
    /// </summary>
    internal static string MpCmdRunPath(RunContext run)
    {
        var candidates = new List<string>();
        var platform = Path.Combine(run.Services.GetEnvironmentVariable("ProgramData") ?? "", "Microsoft", "Windows Defender", "Platform");
        if (Directory.Exists(platform))
        {
            IEnumerable<string> folders;
            try { folders = Directory.GetDirectories(platform); }
            catch (Exception) { folders = Array.Empty<string>(); }
            var versioned = new List<(Version Version, string Path)>();
            foreach (var folder in folders)
            {
                if (Version.TryParse(BuildSuffix.Replace(Path.GetFileName(folder), ""), out var version)) { versioned.Add((version, folder)); }
            }
            candidates.AddRange(versioned.OrderByDescending(v => v.Version).Select(v => Path.Combine(v.Path, "MpCmdRun.exe")));
        }
        candidates.Add(Path.Combine(run.Services.GetEnvironmentVariable("ProgramFiles") ?? "", "Windows Defender", "MpCmdRun.exe"));
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) { continue; }
            if (!FileSystemSafety.IsAdminOnlyPath(Path.GetDirectoryName(path)) || !FileSystemSafety.IsAdminOnlyPath(path))
            {
                run.Log(path + " is not protected; not used.", LogSeverity.Warning);
                continue;
            }
            if (!Authenticode.IsMicrosoftSigned(path))
            {
                run.Log(path + " has no valid Microsoft signature; not used.", LogSeverity.Warning);
                continue;
            }
            return path;
        }
        return null;
    }
}

/// <summary>Frees up disk space.</summary>
public sealed class FreeDiskSpaceHandler : IActionHandler
{
    private const double GB = 1024d * 1024 * 1024;
    private const long MB = 1024L * 1024;

    /// <summary>A size as people read it: GB from 1 GB up ("12.4 GB"), else MB ("350 MB").</summary>
    internal static string Size(long bytes)
        => bytes >= GB
            ? TextFormat.Number(bytes / GB, "0.#") + " GB"
            : TextFormat.Number(Math.Max(0, bytes) / (double)MB, "0") + " MB";

    /// <summary>
    /// Clears the Delivery Optimization cache with its own cmdlet (through the in-box cmdlet
    /// runner) and cleans up the component store (StartComponentCleanup, never /ResetBase).
    /// Deletes nothing itself, and never in a folder a standard user can change. Success only on
    /// bytes freed on the system drive.
    /// </summary>
    public int? Run(RunContext run)
    {
        var drive = DeviceChecks.SystemDrive(run);
        var before = run.Services.FreeBytes(drive);
        var problems = 0;

        run.StepHeader(1, 2, "Clear the Delivery Optimization cache");
        var cacheLimit = run.StepLimit(10, "The Delivery Optimization cache cleanup");
        if (cacheLimit <= 0)
        {
            problems++;
        }
        else
        {
            var cache = run.RunInboxCmdlet(InboxCommand.DeleteDeliveryOptimizationCache, TimeSpan.FromMinutes(cacheLimit));
            switch (cache.Outcome)
            {
                case InboxOutcome.Done:
                    run.Log("The Delivery Optimization cache was cleared.");
                    break;
                case InboxOutcome.NotAvailable:
                    run.Log("Delivery Optimization has no cache cmdlet on this device; skipped.");
                    break;
                default:
                    problems++;
                    run.Log("The Delivery Optimization cache could not be cleared: " + Servicing.Describe(cache), LogSeverity.Warning);
                    if (cache.Outcome == InboxOutcome.PolicyRefused) { Servicing.AddPolicyRefusal(run, "Delete-DeliveryOptimizationCache"); }
                    break;
            }
        }

        run.StepHeader(2, 2, "Clean up the component store (DISM)");
        if (!Servicing.WaitIdle(run))
        {
            problems++;
            run.Log("The servicing stack is busy; the component store cleanup was not started.", LogSeverity.Warning);
        }
        else
        {
            var limit = run.StepLimit(45, "The component store cleanup");
            if (limit > 0)
            {
                run.Log("Removing superseded Windows components with DISM StartComponentCleanup; this can take 10 to 30 minutes.");
                var dism = run.RunTool(new ProcessRequest
                {
                    FilePath = ProcessRunner.SystemTool("dism.exe"),
                    Arguments = "/Online /English /Cleanup-Image /StartComponentCleanup",
                    Label = "DISM StartComponentCleanup",
                    TimeoutMinutes = limit,
                });
                if (!dism.Started || dism.TimedOut || dism.ExitCode != 0) { problems++; }
            }
            else
            {
                problems++;
            }
        }

        var after = run.Services.FreeBytes(drive);
        long freed = before != null && after != null ? after.Value - before.Value : 0;
        run.Log(string.Format(CultureInfo.InvariantCulture, "Free space on {0}: {1} GB before, {2} GB after.",
            drive, TextFormat.Number((before ?? 0) / GB, "0.##"), TextFormat.Number((after ?? 0) / GB, "0.##")));
        // Before and after in every result, so the user sees what the cleanup did to the drive.
        var label = "drive " + drive.TrimEnd('\\');
        var beforeAfter = before == null || after == null
            ? ""
            : freed >= MB
                ? string.Format(CultureInfo.InvariantCulture, " Free space on {0} was {1} before and is {2} now.", label, Size(before.Value), Size(after.Value))
                : string.Format(CultureInfo.InvariantCulture, " Free space on {0} is {1}, as before.", label, Size(after.Value));
        if (freed >= MB)
        {
            run.AddChange(string.Format(CultureInfo.InvariantCulture, "Freed {0} on {1} (Delivery Optimization cache and old Windows components)", Size(freed), drive));
            run.AddFinding(FindingLevel.Ok, "DISK-FREED", Size(freed) + " freed on the system drive",
                beforeAfter.TrimStart(), "Nothing else to do.");
            return problems > 0 ? EngineExitCode.Warnings : EngineExitCode.Success;
        }
        if (problems > 0)
        {
            run.AddFinding(FindingLevel.Warning, "DISK-CLEANUP-INCOMPLETE", "The cleanup did not finish, and no space was freed",
                "See the technical details for the step that stopped." + beforeAfter, "Try again later. If the disk stays full, contact your IT support.");
            return EngineExitCode.Warnings;
        }
        run.AddFinding(FindingLevel.Info, "DISK-NOTHING-TO-FREE", "Nothing more could be freed by Windows",
            "The Delivery Optimization cache and the component store were already clean." + beforeAfter,
            "Empty the Recycle Bin and remove large personal files you no longer need, such as old downloads.");
        return EngineExitCode.NothingToRepair;
    }
}
