using System;
using System.Collections.Generic;
using System.IO;

namespace EndpointToolbelt.Service.Engine;

/// <summary>Builds the engine command line and its clean environment.</summary>
public static class EngineLaunch
{
    /// <summary>Variables copied from the service's own (SYSTEM) environment; everything else is dropped.</summary>
    private static readonly string[] CopiedVariables =
    {
        "ALLUSERSPROFILE", "APPDATA", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
        "COMPUTERNAME", "ComSpec", "LOCALAPPDATA", "NUMBER_OF_PROCESSORS", "OS", "PATHEXT",
        "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION",
        "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "PUBLIC",
        "SystemDrive", "SystemRoot", "USERDOMAIN", "USERNAME", "USERPROFILE", "windir",
    };

    /// <summary>
    /// CreateProcess errors that mean a device policy refused the engine executable:
    /// ERROR_INVALID_IMAGE_HASH from App Control for Business and ERROR_ACCESS_DISABLED_BY_POLICY
    /// from AppLocker, the same codes the in-box cmdlet runner reads for powershell.exe; and
    /// ERROR_ACCESS_DENIED, which a Microsoft Defender attack surface reduction rule or other
    /// security software returns. The service runs as SYSTEM and checks before every run
    /// that only SYSTEM and Administrators can change the engine's files, so access denied on
    /// starting it means something on the device refused it.
    /// </summary>
    private static readonly int[] PolicyBlockErrors = { 577, 1260, 5 };

    public static bool IsPolicyBlock(int startErrorCode) => Array.IndexOf(PolicyBlockErrors, startErrorCode) >= 0;

    /// <summary>
    /// The compiled engine with fixed arguments: <c>-Action &lt;name&gt; -RequestFile &lt;path&gt;</c>,
    /// started in its own folder with a clean environment: the copied variables, PATH with System32
    /// and Wbem only, and TEMP in the run's own folder.
    /// </summary>
    public static EngineStartInfo Create(string engineExecutable, ToolbeltAction action, string requestFile, string tempFolder, TimeSpan timeLimit)
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in CopiedVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) { env[name] = value; }
        }
        env["PATH"] = string.Join(";", system32, Path.Combine(system32, "Wbem"));
        env["TEMP"] = tempFolder;
        env["TMP"] = tempFolder;
        env["ENDPOINTTOOLBELT_RUN"] = "1";

        return new EngineStartInfo
        {
            FileName = engineExecutable,
            Arguments = new List<string> { "-Action", action.ToString(), "-RequestFile", requestFile },
            WorkingDirectory = Path.GetDirectoryName(engineExecutable),
            Environment = env,
            TimeLimit = timeLimit,
        };
    }
}
