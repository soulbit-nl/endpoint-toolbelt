using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace EndpointToolbelt.Platform;

/// <summary>One rule of the shared pattern table (Engine\ImeLogPatterns.json).</summary>
[DataContract]
public sealed class ImeLogRule
{
    [DataMember(Name = "id", Order = 1)] public string Id { get; set; }
    [DataMember(Name = "kind", Order = 2)] public string Kind { get; set; }
    [DataMember(Name = "pattern", Order = 3)] public string Pattern { get; set; }
    [DataMember(Name = "error", Order = 4)] public string Error { get; set; }

    /// <summary>App rules (engine only): Start, Download, Hash, Unzip, Install or Detection.</summary>
    [DataMember(Name = "phase", Order = 5)] public string Phase { get; set; }

    /// <summary>App rules (engine only): Started, Progress, Done, Failed or Result.</summary>
    [DataMember(Name = "result", Order = 6)] public string Result { get; set; }
}

[DataContract]
public sealed class ImeLogPatternTable
{
    [DataMember(Name = "version", Order = 1)] public int Version { get; set; }
    [DataMember(Name = "checkInFiles", Order = 2)] public List<string> CheckInFiles { get; set; } = new();
    [DataMember(Name = "checkIn", Order = 3)] public List<ImeLogRule> CheckIn { get; set; } = new();

    /// <summary>The app log files (engine only), in order of preference.</summary>
    [DataMember(Name = "appWorkloadFiles", Order = 4)] public List<string> AppWorkloadFiles { get; set; } = new();

    /// <summary>The app phase rules (engine only).</summary>
    [DataMember(Name = "appWorkload", Order = 5)] public List<ImeLogRule> AppWorkload { get; set; } = new();

    /// <summary>The IME's agent log files, where it writes its detection-script runs (engine only).</summary>
    [DataMember(Name = "detectionFiles", Order = 6)] public List<string> DetectionFiles { get; set; } = new();

    /// <summary>The detection-script rules (engine only): kinds script, bitness, exit and output.</summary>
    [DataMember(Name = "detection", Order = 7)] public List<ImeLogRule> Detection { get; set; } = new();

    /// <summary>The IME logs that hold its Win32 app check-ins with their policy lists (engine only).</summary>
    [DataMember(Name = "assignmentFiles", Order = 8)] public List<string> AssignmentFiles { get; set; } = new();

    /// <summary>The check-in rules that tie a policy list to its scope (engine only): kinds session, required, available and policies.</summary>
    [DataMember(Name = "assignment", Order = 9)] public List<ImeLogRule> Assignment { get; set; } = new();

    /// <summary>The table the engine ships with, or null when it is missing or damaged.</summary>
    public static ImeLogPatternTable Load(string path)
    {
        try
        {
            using var stream = FileSystemSafety.OpenReadNoFollow(path);
            var serializer = new DataContractJsonSerializer(typeof(ImeLogPatternTable), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            var table = (ImeLogPatternTable)serializer.ReadObject(stream);
            return table?.CheckIn == null || table.CheckIn.Count == 0 || table.CheckInFiles == null || table.CheckInFiles.Count == 0 ? null : table;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
