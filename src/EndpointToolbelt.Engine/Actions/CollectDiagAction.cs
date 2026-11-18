using System;
using EndpointToolbelt.Engine.Support;

namespace EndpointToolbelt.Engine.Actions;

/// <summary>Create support package (see <see cref="SupportPackage"/>).</summary>
public sealed class CollectDiagHandler : IActionHandler
{
    /// <summary>Makes the package for a run; tests hand in one with replaced steps.</summary>
    internal Func<RunContext, SupportPackage> Create { get; set; } = run => new SupportPackage(run);

    public int? Run(RunContext run) => Create(run).CollectDiag();
}
