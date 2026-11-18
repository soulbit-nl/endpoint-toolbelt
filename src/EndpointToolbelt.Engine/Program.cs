namespace EndpointToolbelt.Engine;

/// <summary>The compiled repair engine. See <see cref="EngineEntry"/>.</summary>
public static class Program
{
    public static int Main(string[] args) => EngineEntry.Run(args, ActionHandlers.Shipped);
}
