namespace EndpointToolbelt;

/// <summary>Exit codes of an engine run.</summary>
public static class EngineExitCode
{
    public const int Success = 0;
    public const int Failed = 1;
    public const int Warnings = 2;
    public const int InvalidAction = 87;
    public const int CooldownActive = 69001;
    public const int RestartPending = 69002;
    public const int NothingToRepair = 69003;
}

/// <summary>Outcome of a request at service level, before or around an engine run.</summary>
public enum ResultCode
{
    /// <summary>The engine ran; see the engine exit code.</summary>
    Completed = 0,
    Accepted = 1,
    ServiceUnavailable = 10,
    ProtocolMismatch = 11,
    ServerIdentityMismatch = 12,
    InvalidRequest = 13,
    Busy = 20,
    ConfirmationRequired = 21,
    RateLimited = 22,
    MultiSessionBlocked = 23,

    /// <summary>
    /// The organization switched this tool off with the policy value <c>DisabledTools</c>. The
    /// user pipe gets it; the trusted service caller may still run the tool. An addition.
    /// </summary>
    ToolSwitchedOff = 24,

    /// <summary>A device policy (App Control, AppLocker, an attack surface reduction rule) or security software blocked the engine executable (the name is kept so the protocol does not change).</summary>
    EngineNotSigned = 30,
    EngineTimeout = 31,
    EngineCrashed = 32,
    EngineDidNotStart = 33,

    /// <summary>
    /// The run ended without a result because the service stopped while it ran (device restart,
    /// upgrade or power loss). Its records up to that point are kept.
    /// </summary>
    Interrupted = 34,
    NoResult = 40,
}

/// <summary>Event IDs in the product's event log.</summary>
public static class EventId
{
    public const int ActionStarted = 1000;
    public const int ActionCompleted = 1001;
    public const int ActionFailed = 1002;
    public const int ClientHealthCounts = 1003;
    public const int ClientPackageCreated = 1004;
    public const int ClientActionNotStarted = 1005;
    public const int ConfirmationMissing = 1010;
    public const int RateLimited = 1011;
    public const int MultiSessionBlocked = 1012;
    /// <summary>A device policy blocked the engine executable (any policy or security software; the name is kept).</summary>
    public const int EngineNotSigned = 1013;
    public const int PipeNameInUse = 1014;
    public const int LogFolderRejected = 1015;
    public const int TrustedCallerPipe = 1016;
    public const int TrustedCallerRefused = 1017;

    /// <summary>A user asked for a tool the organization switched off; the service refused it.</summary>
    public const int ToolSwitchedOff = 1018;
    public const int ServiceError = 1020;
}
