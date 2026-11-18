using System;
using EndpointToolbelt.Engine.WindowsUpdate;

namespace EndpointToolbelt.Engine.Actions;

/// <summary>Repair Windows Update (see <see cref="WindowsUpdateRepair.RepairWU"/>).</summary>
public sealed class RepairWUHandler : IActionHandler
{
    /// <summary>Makes the repair for a run; tests hand in one with replaced steps.</summary>
    internal Func<RunContext, WindowsUpdateRepair> Create { get; set; } = run => new WindowsUpdateRepair(run);

    public int? Run(RunContext run) => Create(run).RepairWU();
}
