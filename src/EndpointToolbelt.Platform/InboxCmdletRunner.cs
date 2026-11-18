using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace EndpointToolbelt.Platform;

/// <summary>The only Windows PowerShell commands the product ever runs.</summary>
public enum InboxCommand
{
    /// <summary>BackupToAAD-BitLockerKeyProtector for one protector (engine, as SYSTEM).</summary>
    BackupBitLockerKeyProtector,

    /// <summary>Delete-DeliveryOptimizationCache (engine, as SYSTEM).</summary>
    DeleteDeliveryOptimizationCache,

    /// <summary>Get-AppxPackage with Reset-AppxPackage for Company Portal (app, as the user).</summary>
    ResetCompanyPortal,
}

/// <summary>A value the caller inserts into a command; each kind is checked by the runner itself.</summary>
public enum InboxValueKind
{
    /// <summary>A volume as a drive letter and a colon, e.g. "C:".</summary>
    DriveLetter,

    /// <summary>A GUID in braces, e.g. a BitLocker key protector ID.</summary>
    BracedGuid,
}

public enum InboxOutcome
{
    /// <summary>Exit 0: the cmdlet completed.</summary>
    Done,

    /// <summary>Exit 3: the thing to act on is not there (for example the app is not installed).</summary>
    NotPresent,

    /// <summary>Exit 4: the cmdlet is not available on this Windows build.</summary>
    NotAvailable,

    /// <summary>Exit 1 (or any other code): the cmdlet failed.</summary>
    Failed,

    /// <summary>Windows refused PowerShell or the command because of a policy (App Control, AppLocker, execution policy).</summary>
    PolicyRefused,

    /// <summary>The call ran past its time limit and was stopped.</summary>
    TimedOut,

    /// <summary>powershell.exe could not be started for another reason.</summary>
    NotStarted,
}

public sealed class InboxResult
{
    public InboxOutcome Outcome { get; set; }
    public int ExitCode { get; set; }

    /// <summary>Standard error, for the log only. Standard output is ignored.</summary>
    public string ErrorOutput { get; set; } = "";
}

/// <summary>
/// The one place that starts powershell.exe, shared by the engine and the app. It runs only the
/// constant commands of a fixed allow-list, each calling in-box cmdlets only, with the full
/// System32 path and -NoLogo -NoProfile -NonInteractive -Command; never -File, -EncodedCommand,
/// -ExecutionPolicy, Invoke-Expression, Add-Type, type literals or .NET method calls, so every
/// command also works in ConstrainedLanguage mode (where App Control runs -Command text). The
/// only inserted values are those the runner checks itself. The result comes back through the
/// exit code: 0 done, 3 not present, 4 cmdlet not available, 1 failed.
/// </summary>
public sealed class InboxCmdletRunner
{
    public const int ExitDone = 0;
    public const int ExitFailed = 1;
    public const int ExitNotPresent = 3;
    public const int ExitNotAvailable = 4;

    /// <summary>CreateProcess errors that mean a policy blocked powershell.exe: ERROR_ACCESS_DISABLED_BY_POLICY (AppLocker) and ERROR_INVALID_IMAGE_HASH (App Control).</summary>
    private static readonly int[] PolicyStartErrors = { 1260, 577 };

    private static readonly char[] LineBreaks = { '\r', '\n' };

    internal sealed class Definition
    {
        public Definition(string label, string text, params InboxValueKind[] values)
        {
            Label = label;
            Text = text;
            Values = values;
        }

        public string Label { get; }

        /// <summary>The command; <c>%1</c>, <c>%2</c> mark the inserted values, in order.</summary>
        public string Text { get; }

        public IReadOnlyList<InboxValueKind> Values { get; }
    }

    /// <summary>The allow-list. Changing it is a design decision.</summary>
    internal static readonly IReadOnlyDictionary<InboxCommand, Definition> AllowList = new Dictionary<InboxCommand, Definition>
    {
        [InboxCommand.BackupBitLockerKeyProtector] = new(
            "BackupToAAD-BitLockerKeyProtector",
            "$ProgressPreference='SilentlyContinue'; if (-not (Get-Command BackupToAAD-BitLockerKeyProtector -ErrorAction SilentlyContinue)) { exit 4 }; " +
            "$null = BackupToAAD-BitLockerKeyProtector -MountPoint '%1' -KeyProtectorId '%2' -ErrorAction Stop; exit 0",
            InboxValueKind.DriveLetter, InboxValueKind.BracedGuid),
        [InboxCommand.DeleteDeliveryOptimizationCache] = new(
            "Delete-DeliveryOptimizationCache",
            "$ProgressPreference='SilentlyContinue'; if (-not (Get-Command Delete-DeliveryOptimizationCache -ErrorAction SilentlyContinue)) { exit 4 }; " +
            "Delete-DeliveryOptimizationCache -Force -ErrorAction Stop; exit 0"),
        [InboxCommand.ResetCompanyPortal] = new(
            "Reset-AppxPackage",
            "$ProgressPreference='SilentlyContinue'; $p = Get-AppxPackage -Name 'Microsoft.CompanyPortal'; if (-not $p) { exit 3 }; " +
            "if (-not (Get-Command Reset-AppxPackage -ErrorAction SilentlyContinue)) { exit 4 }; $p | Reset-AppxPackage -ErrorAction Stop; exit 0"),
    };

    private static readonly Regex DriveLetter = new(@"\A[A-Za-z]:\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex BracedGuid = new(@"\A\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly IProcessRunner _runner;

    public InboxCmdletRunner(IProcessRunner runner = null) => _runner = runner ?? new ProcessRunner();

    public static string PowerShellPath => Path.Combine(ProcessRunner.System32, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// The command text with its values inserted. Throws when the command is not on the
    /// allow-list or a value is not exactly of its kind.
    /// </summary>
    public static string BuildCommand(InboxCommand command, params string[] values)
    {
        if (!AllowList.TryGetValue(command, out var definition)) { throw new ArgumentOutOfRangeException(nameof(command), "The command is not on the allow-list."); }
        values ??= Array.Empty<string>();
        if (values.Length != definition.Values.Count) { throw new ArgumentException("The command takes " + definition.Values.Count.ToString(CultureInfo.InvariantCulture) + " value(s).", nameof(values)); }
        var text = definition.Text;
        for (var i = 0; i < values.Length; i++)
        {
            var pattern = definition.Values[i] == InboxValueKind.DriveLetter ? DriveLetter : BracedGuid;
            if (values[i] == null || !pattern.IsMatch(values[i])) { throw new ArgumentException("A value is not a valid " + definition.Values[i] + ".", nameof(values)); }
            text = text.Replace("%" + (i + 1).ToString(CultureInfo.InvariantCulture), values[i]);
        }
        return text;
    }

    /// <summary>The arguments for powershell.exe. The command never contains a double quote.</summary>
    public static string BuildArguments(string commandText)
    {
        if (commandText.IndexOf('"') >= 0 || commandText.EndsWith("\\", StringComparison.Ordinal)) { throw new ArgumentException("The command cannot be quoted safely.", nameof(commandText)); }
        return "-NoLogo -NoProfile -NonInteractive -Command \"" + commandText + "\"";
    }

    /// <summary>
    /// Runs one allow-listed command within <paramref name="timeLimit"/>. Never throws for what the
    /// command does; the outcome says what happened, and standard error is returned for the log.
    /// </summary>
    public InboxResult Run(InboxCommand command, TimeSpan timeLimit, Action<LogSeverity, string> log, params string[] values)
    {
        // A limit of 0 means "no room left; do not start" (RunContext.StepLimit): never start then.
        if (timeLimit.TotalMinutes < 0.01 || timeLimit.TotalMinutes > 1440) { throw new ArgumentOutOfRangeException(nameof(timeLimit), "The time limit must be between 0.01 and 1440 minutes."); }
        var text = BuildCommand(command, values);
        var request = new ProcessRequest
        {
            FilePath = PowerShellPath,
            Arguments = BuildArguments(text),
            Label = AllowList[command].Label,
            TimeoutMinutes = timeLimit.TotalMinutes,
        };
        // Only the in-box modules; never module folders other software may have added.
        request.Environment["PSModulePath"] = Path.Combine(ProcessRunner.System32, "WindowsPowerShell", "v1.0", "Modules");
        var run = _runner.Run(request, log);
        return Classify(run);
    }

    internal static InboxResult Classify(ProcessResult run)
    {
        var result = new InboxResult { ExitCode = run.ExitCode, ErrorOutput = run.ErrorOutput ?? "" };
        var errorLines = result.ErrorOutput.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries);
        if (!run.Started)
        {
            result.Outcome = PolicyStartErrors.Contains(run.StartError) ? InboxOutcome.PolicyRefused : InboxOutcome.NotStarted;
        }
        else if (run.TimedOut)
        {
            result.Outcome = InboxOutcome.TimedOut;
        }
        else if (run.ExitCode != ExitDone && ScriptPolicy.IsPolicyFailure(errorLines))
        {
            // A refusal by policy is never a silent failure: the caller names the policy.
            result.Outcome = InboxOutcome.PolicyRefused;
        }
        else
        {
            result.Outcome = run.ExitCode switch
            {
                ExitDone => InboxOutcome.Done,
                ExitNotPresent => InboxOutcome.NotPresent,
                ExitNotAvailable => InboxOutcome.NotAvailable,
                _ => InboxOutcome.Failed,
            };
        }
        return result;
    }
}
