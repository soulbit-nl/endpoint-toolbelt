using System;
using System.IO;

namespace EndpointToolbelt.Service;

/// <summary>Folders the service uses. The working root is SYSTEM-only.</summary>
public sealed class ServicePaths
{
    /// <param name="engineExecutable">The compiled engine; by default in the parent of the engine folder (the install folder).</param>
    public ServicePaths(string workingRoot, string engineFolder, string engineExecutable = null)
    {
        WorkingRoot = Path.GetFullPath(workingRoot);
        EngineFolder = Path.GetFullPath(engineFolder);
        EngineExecutable = Path.GetFullPath(engineExecutable ?? Path.Combine(Path.GetDirectoryName(EngineFolder), ProductInfo.EngineExecutable));
    }

    /// <summary>
    /// The default for the service: %LOCALAPPDATA% of LocalSystem, i.e.
    /// %SystemRoot%\System32\config\systemprofile\AppData\Local\EndpointToolbelt.
    /// </summary>
    public static ServicePaths ForService() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.WorkingFolderName),
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ProductInfo.EngineFolderName));

    public string WorkingRoot { get; }
    public string EngineFolder { get; }
    public string WorkFolder => Path.Combine(WorkingRoot, "Work");
    public string ResultsFolder => Path.Combine(WorkingRoot, "Results");
    public string FallbackLogFolder => Path.Combine(WorkingRoot, "Logs");
    /// <summary>The per-user rate limits, kept across service restarts.</summary>
    public string LimitsFolder => Path.Combine(WorkingRoot, "Limits");

    /// <summary>The app name cache (app ID to name, first and last seen), directly in the SYSTEM-only working root.</summary>
    public string AppNameCacheFile => Path.Combine(WorkingRoot, EndpointToolbelt.Platform.AppNameCache.FileName);

    /// <summary>The compiled engine.</summary>
    public string EngineExecutable { get; }
}
