namespace EndpointToolbelt;

/// <summary>
/// Names of every technical item. Technical names always use "EndpointToolbelt"; the names a
/// person reads say "Endpoint Toolbelt" (see docs/naming.md), the spelling the names
/// installed on devices (event log and source, install folder) always had.
/// </summary>
public static class ProductInfo
{
    /// <summary>The product name as people read it.</summary>
    public const string DisplayName = "Endpoint Toolbelt";
    /// <summary>
    /// Window and dialog titles. The full name: "Toolbelt" alone is never used as a name, because
    /// other software marks contain that word.
    /// </summary>
    public const string ShortDisplayName = DisplayName;
    public const string Publisher = "SoulBit";

    /// <summary>The name with its publisher line, as every SoulBit product writes it ("Dael by SoulBit").</summary>
    public const string NameWithPublisher = DisplayName + " by " + Publisher;
    /// <summary>One line on what the product does, below the name. Intune only as an adjective.</summary>
    public const string Description = "Self-service repair for Intune-managed Windows devices";
    public const string TrademarkNotice = "Microsoft, Intune and Windows are trademarks of the Microsoft group of companies.";

    /// <summary>
    /// The publisher's site, linked from the About dialog: SoulBit's own redirect for this product,
    /// which leads to the start page with all products. The address is fixed in a signed program,
    /// so it goes through a redirect that outlives a change of the site. It carries no version and
    /// nothing about the device.
    /// </summary>
    public const string ProductsUrl = "https://soulbit.app/go/endpoint-toolbelt";

    /// <summary>The source code and the releases, linked from the About dialog.</summary>
    public const string SourceUrl = "https://github.com/soulbit-nl/endpoint-toolbelt";

    public const string ServiceName = "EndpointToolbeltService";
    public const string ServiceDisplayName = DisplayName + " Service";
    public const string PipeName = "EndpointToolbelt";

    /// <summary>
    /// Pipe for the one trusted service caller an administrator names in policy. It exists
    /// only while the policy value is set.
    /// </summary>
    public const string CallerPipeName = "EndpointToolbelt.Caller";

    /// <summary>Policy value (under <see cref="PolicyKey"/> only) that names the trusted service caller.</summary>
    public const string TrustedServiceCallerValue = "TrustedServiceCaller";

    public const string RegistryRoot = @"SOFTWARE\EndpointToolbelt";
    public const string ConfigKey = RegistryRoot + @"\Config";
    public const string StateKey = RegistryRoot + @"\State";
    public const string PolicyKey = @"SOFTWARE\Policies\EndpointToolbelt";

    /// <summary>Installed name: the event log keeps the spelling it was registered with.</summary>
    public const string EventLogName = "Endpoint Toolbelt";
    /// <summary>Installed name: the event source keeps the spelling it was registered with.</summary>
    public const string EventSource = "Endpoint Toolbelt";

    /// <summary>Installed name: the install folder below the publisher folder keeps its spelling.</summary>
    public const string InstallFolderName = "Endpoint Toolbelt";

    public const string WorkingFolderName = "EndpointToolbelt";
    public const string EngineFolderName = "Engine";

    /// <summary>The compiled engine, in the install folder next to the service.</summary>
    public const string EngineExecutable = "EndpointToolbelt.Engine.exe";
    public const string EngineLogFileName = "EndpointToolbelt.log";
    public const string ServiceLogFileName = "EndpointToolbeltService.log";
}
