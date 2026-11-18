using System.Collections.Generic;
using System.Runtime.Serialization;

namespace EndpointToolbelt.Protocol;

public static class ProtocolInfo
{
    /// <summary>Incremented on every incompatible change of the messages below.</summary>
    public const int Version = 1;

    /// <summary>Largest request the service accepts.</summary>
    public const int MaxRequestBytes = 64 * 1024;

    /// <summary>Largest single response message the client accepts.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>Raw bytes per file chunk (base64 in the message).</summary>
    public const int FileChunkBytes = 32 * 1024;
}

public enum RequestType
{
    GetStatus = 1,
    RunAction = 2,
    GetLastResult = 3,
    ReportClientEvent = 4,

    /// <summary>
    /// Keeps the connection open; the service sends ShuttingDown before it stops (for example
    /// during an upgrade), so the app can close itself.
    /// </summary>
    Watch = 5,
}

public enum ResponseType
{
    Status = 1,
    Accepted = 2,
    Rejected = 3,
    Record = 4,
    FileStart = 5,
    FileChunk = 6,
    FileEnd = 7,
    Completed = 8,
    LastResult = 9,
    Ack = 10,
    ShuttingDown = 11,
}

[DataContract]
public sealed class Request
{
    [DataMember(Order = 1)] public int ProtocolVersion { get; set; } = ProtocolInfo.Version;
    [DataMember(Order = 2)] public RequestType Type { get; set; }
    [DataMember(Order = 3)] public ToolbeltAction Action { get; set; }

    /// <summary>The user confirmed the action in the app.</summary>
    [DataMember(Order = 4)] public bool Confirmed { get; set; }

    /// <summary>
    /// Number of other user sessions the user was warned about, as reported by the service
    /// in the status. -1 when no warning was shown.
    /// </summary>
    [DataMember(Order = 5)] public int ConfirmedOtherSessions { get; set; } = -1;

    /// <summary>Client-reported event: one of the client event IDs (1003-1005).</summary>
    [DataMember(Order = 6)] public int ClientEventId { get; set; }
    [DataMember(Order = 7)] public string ClientEventText { get; set; }

    /// <summary>
    /// The nonce of the status the caller showed its user before confirming. Required for
    /// an action that needs a confirmation or the other-sessions acknowledgement: the service
    /// accepts only a nonce it issued to this caller in the last 15 minutes, once.
    /// </summary>
    [DataMember(Order = 8)] public string ConfirmationNonce { get; set; }

    /// <summary>
    /// <see cref="ToolbeltAction.RenewIpAddress"/> only: the interface index of the network
    /// adapter the user chose (a number, nothing else). The engine renews only a physical adapter it
    /// finds itself under this index. An addition: an older service ignores it (and refuses the
    /// action it does not know); 0 for every other action.
    /// </summary>
    [DataMember(Order = 9)] public int InterfaceIndex { get; set; }
}

[DataContract]
public sealed class Response
{
    [DataMember(Order = 1)] public ResponseType Type { get; set; }
    [DataMember(Order = 2)] public ResultCode Result { get; set; }
    [DataMember(Order = 3)] public int? ExitCode { get; set; }
    [DataMember(Order = 4)] public string RunId { get; set; }
    [DataMember(Order = 5)] public ToolbeltAction? Action { get; set; }

    /// <summary>One engine record (a JSON object as text).</summary>
    [DataMember(Order = 6)] public string Record { get; set; }

    [DataMember(Order = 7)] public string FileName { get; set; }
    [DataMember(Order = 8)] public long FileLength { get; set; }
    [DataMember(Order = 9)] public string FileSha256 { get; set; }

    /// <summary>Base64 file chunk.</summary>
    [DataMember(Order = 10)] public string Data { get; set; }

    [DataMember(Order = 11)] public StatusInfo Status { get; set; }
    [DataMember(Order = 12)] public LastResultInfo LastResult { get; set; }

    /// <summary>Plain-language detail for rejections and errors. Never contains other users' data.</summary>
    [DataMember(Order = 13)] public string Message { get; set; }
}

[DataContract]
public sealed class StatusInfo
{
    [DataMember(Order = 1)] public int ProtocolVersion { get; set; }
    [DataMember(Order = 2)] public string ServiceVersion { get; set; }

    /// <summary>Action currently running for any user, or null. Never says which user.</summary>
    [DataMember(Order = 3)] public ToolbeltAction? RunningAction { get; set; }
    [DataMember(Order = 4)] public string RunningSinceUtc { get; set; }

    /// <summary>Signed-in user sessions other than the caller's; -1 when they could not be counted.</summary>
    [DataMember(Order = 5)] public int OtherSessions { get; set; }
    [DataMember(Order = 6)] public bool AllowMachineWideOnMultiSession { get; set; }

    [DataMember(Order = 7)] public string SupportText { get; set; }
    [DataMember(Order = 8)] public string SupportUrl { get; set; }

    /// <summary>The device has exactly one active MDM enrollment.</summary>
    [DataMember(Order = 9)] public bool? Enrolled { get; set; }

    /// <summary>Set when a repair needs a restart and the device has not restarted since.</summary>
    [DataMember(Order = 10)] public string RestartRequiredSinceUtc { get; set; }
    [DataMember(Order = 11)] public string RestartRequiredBy { get; set; }

    [DataMember(Order = 12)] public List<CooldownInfo> Cooldowns { get; set; } = new();

    /// <summary>The caller has a stored result from an earlier run.</summary>
    [DataMember(Order = 13)] public bool HasLastResult { get; set; }

    /// <summary>
    /// The newest event in the MDM session log (UTC, within 30 days); null when none was found or
    /// the log could not be read.
    /// </summary>
    [DataMember(Order = 14)] public string LastSyncUtc { get; set; }

    /// <summary>The caller's stored result: which action, which run, and whether it still runs.</summary>
    [DataMember(Order = 15)] public ToolbeltAction? LastResultAction { get; set; }
    [DataMember(Order = 16)] public string LastResultRunId { get; set; }
    [DataMember(Order = 17)] public bool LastResultInProgress { get; set; }

    /// <summary>Which log <see cref="LastSyncUtc"/> came from, or "unreadable" when the logs could not be read.</summary>
    [DataMember(Order = 18)] public string LastSyncSource { get; set; }

    /// <summary>How the caller's stored run ended (when it no longer runs): the result and the engine exit code.</summary>
    [DataMember(Order = 19)] public ResultCode? LastResultCode { get; set; }
    [DataMember(Order = 20)] public int? LastResultExitCode { get; set; }

    /// <summary>The IME's newest successful check-in with the Intune service (UTC, from its own log, within 30 days); null when none was found.</summary>
    [DataMember(Order = 21)] public string LastImeCheckInUtc { get; set; }

    /// <summary>A failed IME check-in newer than the last success (UTC), or null.</summary>
    [DataMember(Order = 22)] public string ImeCheckInErrorUtc { get; set; }

    /// <summary>The error of that failure: an HTTP status ("401"), "proxy" or "token"; empty when the log names none.</summary>
    [DataMember(Order = 23)] public string ImeCheckInError { get; set; }

    /// <summary>Why the check-in is unknown: "missing" (no IME log) or "unreadable"; null when the log was read.</summary>
    [DataMember(Order = 24)] public string ImeCheckInUnavailable { get; set; }

    /// <summary>The device-wide readouts the service computes; null when not available.</summary>
    [DataMember(Order = 25)] public DeviceReadouts Readouts { get; set; }

    /// <summary>
    /// A nonce for the caller's next confirmed action: the caller sends it back in
    /// <see cref="Request.ConfirmationNonce"/> after it showed this status's warning.
    /// </summary>
    [DataMember(Order = 26)] public string ConfirmationNonce { get; set; }

    // Order 27 held HelperCharacterOff in test builds that were never released. It was removed
    // (the helper is part of the brand and cannot be turned off). The number is not used again.

    /// <summary>
    /// The IME's newest start (UTC, from its own log): the record in which it says it delays its
    /// first check-in after a start or restart; null when none was seen. An addition: an
    /// older service leaves it null and the app judges the check-in as before.
    /// </summary>
    [DataMember(Order = 28)] public string ImeStartUtc { get; set; }

    /// <summary>When the IME said it would do its first check-in after that start (UTC); null when no start was seen.</summary>
    [DataMember(Order = 29)] public string ImeFirstCheckInDueUtc { get; set; }

    /// <summary>
    /// The MDM enrollment ID (EntDMID, the device ID Intune gave this device at enrollment) of the
    /// one active Intune enrollment, as a GUID; null when there is none, more than one, or it could not
    /// be read. An addition: an older service leaves it null and the facts say it is unknown.
    /// </summary>
    [DataMember(Order = 30)] public string MdmEnrollmentId { get; set; }

    /// <summary>
    /// The tools the organization switched off with the policy value <c>DisabledTools</c>, by
    /// their <see cref="ToolPolicy"/> names; empty when none is off. An addition: an older service
    /// leaves it null, and the app then reads the policy value itself.
    /// </summary>
    [DataMember(Order = 31)] public List<string> DisabledTools { get; set; }
}

/// <summary>
/// Device-wide readouts computed by the service (SYSTEM) and cached. Only enumerated tokens,
/// times, counts and short code-like values: nothing about a user, no log text.
/// </summary>
[DataContract]
public sealed class DeviceReadouts
{
    [DataMember(Order = 1)] public ComplianceReadout Compliance { get; set; }
    [DataMember(Order = 2)] public DefenderReadout Defender { get; set; }
    [DataMember(Order = 3)] public ClockReadout Clock { get; set; }
    [DataMember(Order = 4)] public ProfileErrorsReadout Profiles { get; set; }
    [DataMember(Order = 5)] public IntuneConnectionReadout IntuneConnection { get; set; }

    /// <summary>What kind of machine this is (a virtual machine, a multi-session host). An addition: an older service leaves it null.</summary>
    [DataMember(Order = 6)] public MachineReadout Machine { get; set; }
}

/// <summary>
/// What kind of machine the device is, as far as it changes what a health row means. Each
/// value is null when it could not be read.
/// </summary>
[DataContract]
public sealed class MachineReadout
{
    /// <summary>A Microsoft Hyper-V virtual machine (also Azure and Windows Sandbox): Win32_ComputerSystem says "Microsoft Corporation", "Virtual Machine".</summary>
    [DataMember(Order = 1)] public bool? VirtualMachine { get; set; }

    /// <summary>A multi-session host (Windows Enterprise multi-session): EditionID "ServerRdsh" or a product name with "multi-session".</summary>
    [DataMember(Order = 2)] public bool? MultiSession { get; set; }
}

/// <summary>
/// The local settings Intune compliance policies usually check. Never a compliance
/// verdict. Each value is a token: "on", "off" or "unknown", plus the ones named per field.
/// </summary>
[DataContract]
public sealed class ComplianceReadout
{
    /// <summary>BitLocker on the OS volume: "on", "off", "suspended", "encrypting", "decrypting" or "unknown".</summary>
    [DataMember(Order = 1)] public string BitLocker { get; set; }

    /// <summary>The OS volume has a recovery-password protector (what can be backed up to Entra ID); null when unknown.</summary>
    [DataMember(Order = 2)] public bool? RecoveryPassword { get; set; }

    /// <summary>"on", "off", "unsupported" (no UEFI Secure Boot) or "unknown".</summary>
    [DataMember(Order = 3)] public string SecureBoot { get; set; }

    /// <summary>"ready", "not-ready", "missing" or "unknown".</summary>
    [DataMember(Order = 4)] public string Tpm { get; set; }

    /// <summary>The TPM specification version, as "2.0"; null when unknown.</summary>
    [DataMember(Order = 5)] public string TpmVersion { get; set; }

    /// <summary>Firewall per profile: "on", "off" or "unknown".</summary>
    [DataMember(Order = 6)] public string FirewallDomain { get; set; }
    [DataMember(Order = 7)] public string FirewallPrivate { get; set; }
    [DataMember(Order = 8)] public string FirewallPublic { get; set; }

    /// <summary>The OS build as "10.0.26100.1234" and the display version as "24H2"; null when unknown.</summary>
    [DataMember(Order = 9)] public string OsBuild { get; set; }
    [DataMember(Order = 10)] public string OsVersion { get; set; }
}

/// <summary>Microsoft Defender Antivirus.</summary>
[DataContract]
public sealed class DefenderReadout
{
    /// <summary>"normal", "passive", "edr-block", "off" (service not running) or "missing"; "unknown" when unreadable.</summary>
    [DataMember(Order = 1)] public string Mode { get; set; }
    [DataMember(Order = 2)] public bool? RealTimeProtection { get; set; }

    /// <summary>When the security intelligence was last updated (UTC); null when unknown.</summary>
    [DataMember(Order = 3)] public string SignatureUpdatedUtc { get; set; }

    /// <summary>The security intelligence version, as "1.421.123.0"; null when unknown.</summary>
    [DataMember(Order = 4)] public string SignatureVersion { get; set; }
}

/// <summary>The clock against the Windows Time service's source.</summary>
[DataContract]
public sealed class ClockReadout
{
    /// <summary>The offset to the source in seconds (positive: this device is behind); null when not measured.</summary>
    [DataMember(Order = 1)] public double? OffsetSeconds { get; set; }

    /// <summary>"domain" (domain hierarchy), "ntp" (a configured server), "local" (the local clock), "none" or "unknown".</summary>
    [DataMember(Order = 2)] public string Source { get; set; }

    /// <summary>Why the offset is not known: "not-running" (Windows Time stopped), "no-source", "no-answer" or "unreadable"; null when measured.</summary>
    [DataMember(Order = 3)] public string Unavailable { get; set; }

    /// <summary>
    /// The device is a virtual machine whose Windows Time has the Hyper-V time provider on
    /// (VMICTimeProvider, Enabled = 1), so its host keeps its clock. Null from an older service.
    /// </summary>
    [DataMember(Order = 4)] public bool? HostTimeProvider { get; set; }
}

/// <summary>MDM policy settings that failed in the last 7 days.</summary>
[DataContract]
public sealed class ProfileErrorsReadout
{
    [DataMember(Order = 1)] public int Errors { get; set; }
    [DataMember(Order = 2)] public int Warnings { get; set; }

    /// <summary>The failed settings, newest first, at most 10, as "Area/Setting" (from the CSP path).</summary>
    [DataMember(Order = 3)] public List<string> Settings { get; set; } = new();

    /// <summary>"unreadable" when the log could not be read; null when it was.</summary>
    [DataMember(Order = 4)] public string Unavailable { get; set; }

    /// <summary>Events about settings Windows reports on every device without a consequence (not counted above).</summary>
    [DataMember(Order = 5)] public int KnownHarmless { get; set; }

    /// <summary>
    /// Failed settings that cannot apply on this device (Windows Hello for Business on a
    /// virtual machine or a multi-session host), newest first, at most 10, in the form of
    /// <see cref="Settings"/>. Their events are not counted in <see cref="Errors"/>.
    /// </summary>
    [DataMember(Order = 6)] public List<string> NotApplicable { get; set; } = new();

    /// <summary>The error events of <see cref="NotApplicable"/>.</summary>
    [DataMember(Order = 7)] public int NotApplicableErrors { get; set; }

    /// <summary>
    /// The settings of <see cref="Settings"/> and <see cref="NotApplicable"/> whose newest failure
    /// was a removal (the MDM client's Delete), not an Add or Replace. Null from an older service.
    /// </summary>
    [DataMember(Order = 8)] public List<string> Removals { get; set; } = new();

    /// <summary>
    /// When each setting of <see cref="Settings"/> last failed (UTC), in the same order; empty from an
    /// older service, and <see cref="System.DateTime.MinValue"/> where the event had no time.
    /// </summary>
    [DataMember(Order = 9)] public List<System.DateTime> SettingTimes { get; set; } = new();
}

/// <summary>The Intune enrollment's scheduled tasks.</summary>
[DataContract]
public sealed class IntuneConnectionReadout
{
    [DataMember(Order = 1)] public int Tasks { get; set; }
    [DataMember(Order = 2)] public int TasksEnabled { get; set; }

    /// <summary>The newest run of any of these tasks (UTC) and its result as "0x00000000"; null when none ran.</summary>
    [DataMember(Order = 3)] public string LastRunUtc { get; set; }
    [DataMember(Order = 4)] public string LastResult { get; set; }

    /// <summary>"not-enrolled" (no single Intune enrollment) or "unreadable"; null when read.</summary>
    [DataMember(Order = 5)] public string Unavailable { get; set; }

    /// <summary>
    /// The names of the disabled tasks (the name only, never the folder path), at most 5, cleaned
    /// to plain text without a GUID. Null from an older service.
    /// </summary>
    [DataMember(Order = 6)] public List<string> DisabledTasks { get; set; } = new();
}

[DataContract]
public sealed class CooldownInfo
{
    [DataMember(Order = 1)] public ToolbeltAction Action { get; set; }
    [DataMember(Order = 2)] public string AvailableAtUtc { get; set; }
}

[DataContract]
public sealed class LastResultInfo
{
    [DataMember(Order = 1)] public ToolbeltAction Action { get; set; }
    [DataMember(Order = 2)] public string RunId { get; set; }
    [DataMember(Order = 3)] public string StartedUtc { get; set; }
    [DataMember(Order = 4)] public string EndedUtc { get; set; }
    [DataMember(Order = 5)] public ResultCode Result { get; set; }
    [DataMember(Order = 6)] public int? ExitCode { get; set; }
    /// <summary>
    /// Stored records. Not sent inside the LastResult message: the service sends them as
    /// separate Record messages after it (see <see cref="RecordCount"/>), so no single message
    /// grows beyond the size limit.
    /// </summary>
    [DataMember(Order = 7)] public List<string> Records { get; set; } = new();
    [DataMember(Order = 8)] public string FileName { get; set; }

    /// <summary>The run is still in progress; the records are what arrived so far.</summary>
    [DataMember(Order = 9)] public bool InProgress { get; set; }

    /// <summary>Number of Record messages that follow the LastResult message.</summary>
    [DataMember(Order = 10)] public int RecordCount { get; set; }

    /// <summary>SHA-256 of the stored package, computed when it was stored.</summary>
    [DataMember(Order = 11)] public string FileSha256 { get; set; }

    /// <summary>Records were dropped because the run produced more than the stored limit.</summary>
    [DataMember(Order = 12)] public bool RecordsTruncated { get; set; }
}
