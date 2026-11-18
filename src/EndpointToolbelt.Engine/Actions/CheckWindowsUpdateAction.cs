using System;
using EndpointToolbelt.Engine.WindowsUpdate;

namespace EndpointToolbelt.Engine.Actions;

/// <summary>Check Windows Update, read-only (see <see cref="WindowsUpdateCheck.Check"/>).</summary>
public sealed class CheckWindowsUpdateHandler : IActionHandler
{
    /// <summary>Makes the check for a run; tests hand in one with replaced reads.</summary>
    internal Func<RunContext, WindowsUpdateCheck> Create { get; set; } = run => new WindowsUpdateCheck(run);

    public int? Run(RunContext run) => Create(run).Check();
}
