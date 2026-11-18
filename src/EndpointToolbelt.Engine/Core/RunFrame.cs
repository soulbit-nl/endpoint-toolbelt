using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine;

/// <summary>The code of one action. Returns the engine exit code, or null when it has no result (a defect).</summary>
public interface IActionHandler
{
    int? Run(RunContext run);
}

/// <summary>The run frame: read the request, check it, dispatch, summarise, return the exit code.</summary>
public static class RunFrame
{
    public const int ContractVersion = 1;
    public const string InvalidRunId = "invalid0";

    private static readonly Regex RunIdPattern = new(@"\A[A-Za-z0-9]{1,32}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // [0-9], not \d: .NET's \d also matches other scripts' digits, which no SID contains.
    private static readonly Regex SidPattern = new(@"\AS-1-[0-9]{1,3}(-[0-9]{1,10}){1,15}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The cache path when it is absolute and names the cache file itself; empty otherwise.</summary>
    internal static string AppNameCachePath(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0) { return ""; }
        try
        {
            return System.IO.Path.IsPathRooted(text) && !text.StartsWith(@"\\", StringComparison.Ordinal)
                && string.Equals(System.IO.Path.GetFileName(text), AppNameCache.FileName, StringComparison.OrdinalIgnoreCase)
                ? System.IO.Path.GetFullPath(text) : "";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException or System.Security.SecurityException)
        {
            return "";
        }
    }

    private sealed class Holder
    {
        public RunContext Run { get; set; }
    }

    /// <summary>
    /// Runs one action. Never throws: whatever goes wrong, the run ends with a summary record and
    /// an exit code. <paramref name="handlers"/> holds exactly one handler per action this engine
    /// runs, matched case-sensitively; any other action is an invalid request (87).
    /// </summary>
    public static int Run(string action, string requestFile, IReadOnlyDictionary<string, IActionHandler> handlers, EngineServices services)
    {
        var holder = new Holder();
        try
        {
            return RunCore(action, requestFile, handlers, services, holder);
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            try
            {
                var run = holder.Run ?? new RunContext(services, InvalidRunId, action);
                run.AddFinding(FindingLevel.Error, "ENGINE-ERROR", "The repair engine stopped with an unexpected error",
                    RunContext.PlainText(message), "Create a support package and contact your IT support.");
                WriteSummary(run, EngineExitCode.Failed);
            }
            catch (Exception)
            {
                services.ErrorOutput("The engine could not report an error: " + message);
            }
            return EngineExitCode.Failed;
        }
    }

    private static int RunCore(string action, string requestFile, IReadOnlyDictionary<string, IActionHandler> handlers, EngineServices services, Holder holder)
    {
        var request = RequestFile.Read(requestFile);
        var runId = InvalidRunId;
        if (RequestFile.Has(request, "runId") && RunIdPattern.IsMatch(RequestFile.Text(request, "runId"))) { runId = RequestFile.Text(request, "runId"); }

        var run = new RunContext(services, runId, action);
        if (request != null)
        {
            if (RequestFile.Has(request, "logDirectory")) { run.LogDirectory = RequestFile.Text(request, "logDirectory"); }
            if (RequestFile.Has(request, "outputDirectory")) { run.OutputDirectory = RequestFile.Text(request, "outputDirectory"); }
            // Unparsable counts become -1 (unknown), which every gate treats as "others may be signed in".
            if (RequestFile.Has(request, "callerSessionId")) { run.CallerSessionId = RequestFile.ToInt(request.Get("callerSessionId"), -1); }
            if (RequestFile.Has(request, "otherSessions")) { run.OtherSessions = RequestFile.ToInt(request.Get("otherSessions"), -1); }
            if (RequestFile.Has(request, "config")) { run.Config = EngineSettings.Resolve(request.Get("config")); }
            if (RequestFile.Has(request, "productVersion")) { run.ProductVersion = RequestFile.Text(request, "productVersion"); }
            if (RequestFile.Has(request, "callerSid") && SidPattern.IsMatch(RequestFile.Text(request, "callerSid"))) { run.CallerSid = RequestFile.Text(request, "callerSid"); }
            if (RequestFile.Has(request, "timeLimitMinutes")) { run.TimeLimitMinutes = Math.Max(0, RequestFile.ToInt(request.Get("timeLimitMinutes"), 0)); }
            // A number only; the handler finds the adapter itself under it.
            if (RequestFile.Has(request, "interfaceIndex")) { run.InterfaceIndex = Math.Max(0, RequestFile.ToInt(request.Get("interfaceIndex"), 0)); }
            // An absolute path to the cache file by its own name only; the cache checks its folder before it writes.
            if (RequestFile.Has(request, "appNameCache")) { run.AppNameCacheFile = AppNameCachePath(RequestFile.Text(request, "appNameCache")); }
        }
        holder.Run = run;

        // Validation comes first, before any other work. Exact, case-sensitive match against the
        // table, and the request must name the same action.
        var valid = request != null &&
            RequestFile.Has(request, "contractVersion") && RequestFile.ToInt(request.Get("contractVersion"), -1) == ContractVersion &&
            RequestFile.Has(request, "action") && string.Equals(RequestFile.Text(request, "action"), action, StringComparison.Ordinal) &&
            handlers.Keys.Contains(action, StringComparer.Ordinal);
        if (!valid)
        {
            run.Log(string.Format(CultureInfo.InvariantCulture, "Invalid request for action \"{0}\"; nothing was run.", RunContext.PlainText(action)), LogSeverity.Error);
            run.AddFinding(FindingLevel.Error, "ENGINE-INVALID-REQUEST", "The repair engine received an invalid request",
                "The action or the request file did not match what this version of the engine accepts. Nothing on this device was changed.",
                "Contact your IT support.",
                "Repair or reinstall " + ProductInfo.DisplayName + " on this device. If it repeats, the app, the service and the engine are probably from different versions.");
            WriteSummary(run, EngineExitCode.InvalidAction);
            return EngineExitCode.InvalidAction;
        }

        var os = services.ReadOsVersion(services);
        run.Log(string.Format(CultureInfo.InvariantCulture, ProductInfo.DisplayName + " {0} engine started. Action: {1}. Run {2}.", run.ProductVersion, action, runId));
        run.Log(string.Format(CultureInfo.InvariantCulture, "Device: {0}; {1}; edition {2}.", services.GetEnvironmentVariable("COMPUTERNAME"), os.Text, os.Edition));
        run.Log(string.Format(CultureInfo.InvariantCulture, "Other signed-in user sessions: {0}.",
            run.OtherSessions < 0 ? "unknown" : run.OtherSessions.ToString(CultureInfo.InvariantCulture)));

        int? exitCode;
        try
        {
            exitCode = handlers.First(h => string.Equals(h.Key, action, StringComparison.Ordinal)).Value.Run(run);
        }
        catch (Exception ex)
        {
            run.Log("The action stopped with an unexpected error: " + ex.Message, LogSeverity.Error);
            run.AddFinding(FindingLevel.Error, "ENGINE-ERROR", "The action stopped with an unexpected error",
                RunContext.PlainText(ex.Message), "Create a support package and contact your IT support.");
            exitCode = EngineExitCode.Failed;
        }

        // A handler without an exit code is a defect, never a success. It ends as Failed, not as
        // an invalid request (87): the action ran and may have changed the device.
        if (exitCode == null)
        {
            run.Log(string.Format(CultureInfo.InvariantCulture, "The handler for {0} returned 0 exit codes instead of one.", action), LogSeverity.Error);
            run.AddFinding(FindingLevel.Error, "ENGINE-NO-RESULT", "The action ended without a clear result",
                "This is a defect in the repair engine. The action ran but did not report one result, so it may have changed this device partly or completely.",
                "Create a support package and contact your IT support.",
                "Report this as a defect of " + ProductInfo.DisplayName + ", with the support package. The log shows which steps ran and what they changed.");
            exitCode = EngineExitCode.Failed;
        }

        WriteSummary(run, exitCode.Value);
        return exitCode.Value;
    }

    internal static void WriteSummary(RunContext run, int exitCode)
    {
        var errors = run.CountFindings(FindingLevel.Error);
        var warnings = run.CountFindings(FindingLevel.Warning);
        if (run.Changes.Count == 0) { run.Log("Nothing on this device was changed by this run."); }
        run.WriteRecord("summary",
            RunContext.Field("exit", exitCode),
            RunContext.Field("findings", run.Findings.Count),
            RunContext.Field("errors", errors),
            RunContext.Field("warnings", warnings),
            RunContext.Field("changes", run.Changes.Count),
            RunContext.Field("restart", run.RestartRequired));
        run.PersistentLog(string.Format(CultureInfo.InvariantCulture,
            "EndpointToolbelt:SUMMARY exit={0} findings={1} errors={2} warnings={3} changes={4} restart={5} ids={6}",
            exitCode, run.Findings.Count, errors, warnings, run.Changes.Count, run.RestartRequired ? "yes" : "no",
            string.Join(",", run.Findings.Select(f => f.Id))));
    }
}
