using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace EndpointToolbelt.Platform;

/// <summary>Severity of a line a platform component asks its caller to log.</summary>
public enum LogSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>What one tool run returned.</summary>
public sealed class ProcessResult
{
    /// <summary>The tool's exit code; -1 when it did not start, -2 when it was stopped.</summary>
    public int ExitCode { get; set; } = -1;

    /// <summary>Standard output only: what verdicts and results are read from.</summary>
    public string Output { get; set; } = "";

    /// <summary>Standard error, kept apart so it can never be mistaken for a result.</summary>
    public string ErrorOutput { get; set; } = "";

    public bool TimedOut { get; set; }
    public bool Started { get; set; }

    /// <summary>The Win32 error when the tool could not be started (0 otherwise), e.g. 1260 for a policy block.</summary>
    public int StartError { get; set; }
}

/// <summary>One tool to run: always a full path, never resolved through PATH.</summary>
public sealed class ProcessRequest
{
    public string FilePath { get; set; }
    public string Arguments { get; set; } = "";

    /// <summary>The name the log uses for this run of the tool.</summary>
    public string Label { get; set; }

    /// <summary>Above 0; a tool that runs longer is stopped with its children.</summary>
    public double TimeoutMinutes { get; set; }

    /// <summary>Encoding of standard output; SFC writes UTF-16 when redirected. Null for the default.</summary>
    public Encoding OutputEncoding { get; set; }

    /// <summary>Variables set (or, with a null value, removed) in the tool's environment.</summary>
    public IDictionary<string, string> Environment { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The seam for running external tools; tests replace it with a fake.</summary>
public interface IProcessRunner
{
    ProcessResult Run(ProcessRequest request, Action<LogSeverity, string> log);
}

/// <summary>
/// Runs a tool by full path with standard output and standard error read through pipes while it
/// runs (never through files a user could reach), so a large output cannot block it. The working
/// folder is System32. A tool that runs past its limit is stopped with its children (dism.exe
/// does its work in a DismHost.exe child, which Process.Kill alone would leave running). The
/// tool stays in the caller's Job Object.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public static string System32 => System.Environment.GetFolderPath(System.Environment.SpecialFolder.System);

    /// <summary>Full path of a tool in System32, never resolved through PATH.</summary>
    public static string SystemTool(string name) => Path.Combine(System32, name);

    public ProcessResult Run(ProcessRequest request, Action<LogSeverity, string> log)
    {
        if (request == null) { throw new ArgumentNullException(nameof(request)); }
        if (!(request.TimeoutMinutes >= 0.01 && request.TimeoutMinutes <= 1440)) { throw new ArgumentOutOfRangeException(nameof(request), "TimeoutMinutes must be between 0.01 and 1440."); }
        // Never resolved through PATH or the working folder.
        if (string.IsNullOrEmpty(request.FilePath) || !Path.IsPathRooted(request.FilePath)) { throw new ArgumentException("The tool must be given by its full path.", nameof(request)); }
        log ??= (_, _) => { };
        var result = new ProcessResult();
        var psi = new ProcessStartInfo(request.FilePath, request.Arguments ?? "")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = System32,
        };
        if (request.OutputEncoding != null) { psi.StandardOutputEncoding = request.OutputEncoding; }
        foreach (var pair in request.Environment)
        {
            if (pair.Value == null) { psi.EnvironmentVariables.Remove(pair.Key); }
            else { psi.EnvironmentVariables[pair.Key] = pair.Value; }
        }
        var clock = Stopwatch.StartNew();
        Process process = null;
        try
        {
            process = Process.Start(psi);
            result.Started = true;
            var outTask = process.StandardOutput.ReadToEndAsync();
            var errTask = process.StandardError.ReadToEndAsync();
            if (process.WaitForExit((int)Math.Ceiling(request.TimeoutMinutes * 60000)))
            {
                // The readers are awaited (bounded) below.
                process.WaitForExit();
                result.ExitCode = process.ExitCode;
                log(LogSeverity.Info, string.Format(CultureInfo.InvariantCulture, "{0} finished in {1}, exit {2}.",
                    request.Label, clock.Elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture), TextFormat.ExitCode(process.ExitCode)));
            }
            else
            {
                result.TimedOut = true;
                result.ExitCode = -2;
                log(LogSeverity.Warning, string.Format(CultureInfo.InvariantCulture, "{0} did not finish within {1} {2} and is being stopped.",
                    request.Label, TextFormat.Number(request.TimeoutMinutes, "0.##"), request.TimeoutMinutes == 1 ? "minute" : "minutes"));
                StopProcessTree(process.Id);
                // Checked, not assumed: the next tool must not start behind one that still runs.
                if (!process.WaitForExit(15000))
                {
                    log(LogSeverity.Warning, string.Format(CultureInfo.InvariantCulture, "{0} is still running 15 seconds after the stop request (process {1}).", request.Label, process.Id));
                }
            }
            // A child that outlives the tool can keep the pipe open; never wait on it forever.
            if (outTask.Wait(10000)) { result.Output = (outTask.Result ?? "").Replace("\0", ""); }
            if (errTask.Wait(2000)) { result.ErrorOutput = (errTask.Result ?? "").Replace("\0", ""); }
        }
        catch (Exception ex)
        {
            if (result.Started)
            {
                log(LogSeverity.Warning, string.Format(CultureInfo.InvariantCulture, "{0} failed while running: {1}", request.Label, ex.Message));
            }
            else
            {
                if (ex is Win32Exception win32) { result.StartError = win32.NativeErrorCode; }
                log(LogSeverity.Warning, string.Format(CultureInfo.InvariantCulture, "{0} could not be started: {1}", request.Label, ex.Message));
            }
        }
        finally
        {
            if (process != null)
            {
                // Never leave a tool running unbounded behind an unexpected error.
                try
                {
                    if (!process.HasExited)
                    {
                        StopProcessTree(process.Id);
                        if (!result.TimedOut) { result.ExitCode = -2; result.TimedOut = true; }
                    }
                }
                catch (Exception) { }
                process.Dispose();
            }
        }
        return result;
    }

    /// <summary>
    /// Ends a process and its children. Work already handed to the servicing stack
    /// (TrustedInstaller) is not ours to stop; it finishes or rolls back there.
    /// </summary>
    public static void StopProcessTree(int processId)
    {
        try
        {
            var psi = new ProcessStartInfo(SystemTool("taskkill.exe"), "/PID " + processId.ToString(CultureInfo.InvariantCulture) + " /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = System32,
            };
            using var kill = Process.Start(psi);
            if (!kill.WaitForExit(30000) || kill.ExitCode != 0) { KillOne(processId); }
        }
        catch (Exception)
        {
            KillOne(processId);
        }
    }

    private static void KillOne(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
        }
        catch (Exception) { }
    }
}
