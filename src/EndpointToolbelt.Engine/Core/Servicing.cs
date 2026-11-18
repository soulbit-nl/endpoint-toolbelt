using System;
using System.Globalization;
using System.Linq;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine;

/// <summary>Shared steps around Windows servicing and the in-box cmdlets.</summary>
public static class Servicing
{
    /// <summary>Longest wait for a busy servicing stack, in minutes.</summary>
    public const int ServicingIdleMinutes = 10;

    /// <summary>
    /// Waits (bounded) while the servicing stack is busy, for example with work an earlier,
    /// stopped run handed it. Stacking a second servicing operation on a running one only makes
    /// both slower. Returns false when it is still busy after the wait; the caller decides
    /// whether to go on.
    /// </summary>
    public static bool WaitIdle(RunContext run)
    {
        var limit = Math.Min(ServicingIdleMinutes, Math.Max(0, run.MinutesLeft() - RunContext.MinStepMinutes));
        var clock = run.Services.Clock;
        var deadline = clock.Elapsed + TimeSpan.FromMinutes(limit);
        var logged = false;
        while (run.Services.IsProcessRunning("TiWorker"))
        {
            if (!logged)
            {
                run.Log(string.Format(CultureInfo.InvariantCulture,
                    "The servicing stack is busy: the Windows module installer (TiWorker, which applies updates) is running. Waiting up to {0} for it.",
                    TextFormat.Count(limit, "minute")));
                logged = true;
            }
            if (clock.Elapsed >= deadline)
            {
                run.Log("The servicing stack is still busy after the wait.", LogSeverity.Warning);
                return false;
            }
            clock.Sleep(TimeSpan.FromSeconds(15));
        }
        if (logged) { run.Log("The servicing stack is idle."); }
        return true;
    }

    /// <summary>
    /// A refusal of an in-box cmdlet by a device policy is never a silent failure: this
    /// finding names the policies that can refuse it and the cmdlet to allow.
    /// </summary>
    public static void AddPolicyRefusal(RunContext run, string cmdlet) =>
        run.AddFinding(FindingLevel.Warning, "ENGINE-POLICY-BLOCKED", "A device policy blocked a Windows command this action needs",
            cmdlet + " is a command that comes with Windows. App Control for Business, AppLocker or the PowerShell execution policy refused it on this device, so this step did not run.",
            "Contact your IT support.",
            "Allow Windows PowerShell to run the in-box cmdlet " + cmdlet + " as SYSTEM in the device's App Control or AppLocker policy; see App Control in the deployment documentation.");

    /// <summary>Why an in-box cmdlet did not complete, in one line for the log.</summary>
    public static string Describe(InboxResult result)
    {
        var line = (result.ErrorOutput ?? "").Split('\r', '\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line != null) { return line.Length > 300 ? line.Substring(0, 300) : line; }
        return result.Outcome switch
        {
            InboxOutcome.TimedOut => "it did not finish in time",
            InboxOutcome.NotStarted => "Windows PowerShell could not be started",
            InboxOutcome.NotAvailable => "the cmdlet is not available on this device",
            InboxOutcome.PolicyRefused => "a device policy refused it",
            _ => "exit " + TextFormat.ExitCode(result.ExitCode),
        };
    }
}
