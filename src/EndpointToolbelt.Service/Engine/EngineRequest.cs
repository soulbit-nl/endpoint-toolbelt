using System.Runtime.Serialization;
using EndpointToolbelt.Service.Configuration;

namespace EndpointToolbelt.Service.Engine;

/// <summary>
/// The request file the service writes for one engine run, in the SYSTEM-only working folder.
/// Nothing in it comes from the user except the action, the session numbers and the chosen
/// network adapter's interface index.
/// </summary>
[DataContract]
public sealed class EngineRequest
{
    public const int CurrentContractVersion = 1;

    [DataMember(Name = "contractVersion", Order = 1)] public int ContractVersion { get; set; } = CurrentContractVersion;
    [DataMember(Name = "runId", Order = 2)] public string RunId { get; set; }
    [DataMember(Name = "action", Order = 3)] public string Action { get; set; }
    [DataMember(Name = "productVersion", Order = 4)] public string ProductVersion { get; set; }
    [DataMember(Name = "outputDirectory", Order = 5)] public string OutputDirectory { get; set; }
    [DataMember(Name = "logDirectory", Order = 6)] public string LogDirectory { get; set; }
    [DataMember(Name = "callerSessionId", Order = 7)] public int CallerSessionId { get; set; }
    [DataMember(Name = "otherSessions", Order = 8)] public int OtherSessions { get; set; }
    [DataMember(Name = "config", Order = 9)] public EngineConfig Config { get; set; }

    /// <summary>
    /// The hard limit after which the service stops the engine, in minutes. The engine plans
    /// its long steps within it, so it can finish and report before it is stopped.
    /// </summary>
    [DataMember(Name = "timeLimitMinutes", Order = 10)] public int TimeLimitMinutes { get; set; }

    /// <summary>
    /// The SID of the user who asked for this run, from their own token. The engine keeps that
    /// user's data (and the device's) in what it returns, and leaves other users' data out.
    /// </summary>
    [DataMember(Name = "callerSid", Order = 11)] public string CallerSid { get; set; }

    /// <summary>
    /// Get a new IP address only: the interface index of the adapter the user chose (a number the
    /// policy checked to be positive); the engine renews only a physical adapter it finds under it.
    /// </summary>
    [DataMember(Name = "interfaceIndex", Order = 12)] public int InterfaceIndex { get; set; }

    /// <summary>
    /// The app name cache in this SYSTEM-only working folder (<see cref="ServicePaths.AppNameCacheFile"/>);
    /// the engine reads and fills it when it looks up app names. A service path, never from the user;
    /// an engine that does not know the field ignores it. Order 16: the highest on main (12) plus 4.
    /// </summary>
    [DataMember(Name = "appNameCache", Order = 16, EmitDefaultValue = false)] public string AppNameCache { get; set; }
}

[DataContract]
public sealed class EngineConfig
{
    [DataMember(Name = "fullResetCooldownHours", Order = 1)] public int FullResetCooldownHours { get; set; }
    [DataMember(Name = "wuRepairCooldownHours", Order = 2)] public int WURepairCooldownHours { get; set; }
    [DataMember(Name = "wuDeploymentDelayDays", Order = 3)] public int WUDeploymentDelayDays { get; set; }
    [DataMember(Name = "wuMinFreeGB", Order = 4)] public int WUMinFreeGB { get; set; }
    [DataMember(Name = "wuRemoveStalePackages", Order = 5)] public bool WURemoveStalePackages { get; set; }

    public static EngineConfig From(ServiceConfig c) => new()
    {
        FullResetCooldownHours = c.FullResetCooldownHours,
        WURepairCooldownHours = c.WURepairCooldownHours,
        WUDeploymentDelayDays = c.WUDeploymentDelayDays,
        WUMinFreeGB = c.WUMinFreeGB,
        WURemoveStalePackages = c.WURemoveStalePackages,
    };
}

/// <summary>The fields of an engine record the service itself needs.</summary>
[DataContract]
public sealed class EngineRecordHeader
{
    [DataMember(Name = "type")] public string Type { get; set; }

    /// <summary>The run ID from the request file. Lines without the current run ID are ignored.</summary>
    [DataMember(Name = "run")] public string Run { get; set; }

    /// <summary>For type "file": the file name inside the run's output directory.</summary>
    [DataMember(Name = "name")] public string Name { get; set; }

    /// <summary>"info", "warning" or "error" for progress records; read separately (see <see cref="EngineRecordSeverity"/>).</summary>
    [IgnoreDataMember] public string Severity { get; set; }
}

/// <summary>
/// The severity of a record, read on its own so that an unexpected value can never make the
/// record header unreadable.
/// </summary>
[DataContract]
public sealed class EngineRecordSeverity
{
    [DataMember(Name = "severity")] public string Severity { get; set; }
}
