using System;
using System.Collections.Generic;
using System.IO;
using EndpointToolbelt.Engine;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine.TestHost;

/// <summary>
/// Test only, never shipped: runs the real engine entry point and run frame with harmless
/// handlers, so the service contract test can start a real engine process without changing
/// anything on the machine. The shipped engine has no test hook.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--redirection-guard")
        {
            // In a process of its own: the mitigation cannot be turned off again once it is on.
            var before = NativeFile.IsRedirectionGuardEnabled();
            NativeFile.TryEnableRedirectionGuard();
            Console.WriteLine(before + " " + NativeFile.IsRedirectionGuardEnabled());
            return 0;
        }
        var handlers = new Dictionary<string, IActionHandler>(StringComparer.Ordinal)
        {
            [nameof(ToolbeltAction.CollectDiag)] = new ContractPackageHandler(),
        };
        return EngineEntry.Run(args, handlers);
    }

    /// <summary>A support package handler that writes an empty zip into the run's output folder.</summary>
    private sealed class ContractPackageHandler : IActionHandler
    {
        public int? Run(RunContext run)
        {
            run.AddFinding(FindingLevel.Info, "CONTRACT-TEST", "Contract test handler");
            var path = Path.Combine(run.OutputDirectory, "contract.zip");
            File.WriteAllBytes(path, new byte[] { 80, 75, 5, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            run.WriteRecord("file", RunContext.Field("name", "contract.zip"));
            return EngineExitCode.Success;
        }
    }
}
