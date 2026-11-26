using System;
using EndpointToolbelt.Engine.Defender;

namespace EndpointToolbelt.Engine.Actions;

/// <summary>Check Defender blocks, read-only (see <see cref="DefenderBlocksCheck.Check"/>).</summary>
public sealed class CheckDefenderBlocksHandler : IActionHandler
{
    /// <summary>Makes the check for a run; tests hand in one of their own.</summary>
    internal Func<RunContext, DefenderBlocksCheck> Create { get; set; } = run => new DefenderBlocksCheck(run);

    public int? Run(RunContext run) => Create(run).Check();
}
