using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EndpointToolbelt.Engine.WindowsUpdate;

/// <summary>The fixed tables of Repair Windows Update: codes by route, limits, title patterns.</summary>
public static class WuCodes
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);

    // Titles that mark a Windows quality update, and titles that match those words but are not
    // one (.NET, Office, Edge and definition updates also say "Security Update"). One definition
    // for the currency check and the failure collector. English titles only.
    public static readonly Regex QualityTitle = new("Cumulative Update|Security Update|Hotpatch|Servicing Stack Update", Options, Limit);
    public static readonly Regex NotQualityTitle = new(@"Defender|Security Intelligence|Malicious Software|Driver Update|Intelligence Update|\.NET|Framework|Silverlight|Microsoft Edge|Visual Studio|Office", Options, Limit);
    public static readonly Regex ServicingStackTitle = new("Servicing Stack Update", Options, Limit);
    public static readonly Regex CumulativeOrHotpatchTitle = new("Cumulative Update|Hotpatch", Options, Limit);
    public static readonly Regex DefinitionTitle = new("Security Intelligence|Defender Antivirus|Definition Update", Options, Limit);
    public static readonly Regex TitleMonth = new("([0-9]{4})-([0-9]{2})", Options, Limit);
    public static readonly Regex HotFixId = new("^KB[0-9]+$", Options, Limit);
    public static readonly Regex KbNumber = new("KB[0-9]{6,7}", Options, Limit);
    public static readonly Regex CodeValue = new("^0x[0-9A-Fa-f]{8}$", Options, Limit);
    public static readonly Regex CodeInText = new("(0x[0-9A-Fa-f]{8})", Options, Limit);
    public static readonly Regex DataStoreCode = new("^0x80248[0-9a-f]{3}$", RegexOptions.CultureInvariant, Limit);
    public static readonly Regex PackageVersion = new(@"~~?([0-9]+)\.([0-9]+)", Options, Limit);
    public static readonly Regex StalePackageName = new(@"^Package_for_RollupFix[A-Za-z0-9_~\.\-]*$", RegexOptions.CultureInvariant, Limit);
    public static readonly Regex PolicyValueName = new("Pause|Defer|TargetRelease|ProductVersion|PolicyDrivenUpdateSource|UseUpdateClassPolicySource|Hotpatch|Rebootless", Options, Limit);
    public static readonly Regex ProviderValueName = new("_(WinningProvider|ProviderSet)$", Options, Limit);
    public static readonly Regex UpdateId = new("^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$", RegexOptions.CultureInvariant, Limit);
    public static readonly Regex OldCacheFolder = new(@"\.old-[0-9]{8}-[0-9]{6}$", Options, Limit);

    // Failure codes, grouped by the repair they lead to. These sets route; the code table only explains.
    public static readonly string[] ServicingCodes = { "0x800f0993", "0x80073712", "0x800f0983", "0x800f0984" };
    public static readonly string[] TxLogCodes = { "0x800719e4" };
    public static readonly string[] SpaceCodes = { "0x800f0922", "0xc190020e" };

    // Source missing: the component store lacks files the update needs. 0x800f081f and 0x800f0915
    // usually mean a damaged store, not a connection, so they are not part of the delivery group;
    // 0x800f0906 is the download of the repair files failing (connection or repair source).
    public static readonly string[] SourceCodes = { "0x800f081f", "0x800f0915", "0x800f0906" };
    public static readonly string[] SourceDownloadCodes = { "0x800f0906" };
    public static readonly string[] DeliveryCodes = { "0x80244022", "0x8024402c", "0x80072ee2", "0x80246007", "0x80246010" };

    // A policy blocks Windows Update or its use as the repair source. Reported only: nothing on the
    // device fixes a policy, and a store repair cannot get past it. Each with the policy it points
    // at, for the finding.
    public static readonly string[] PolicyCodes = { "0x800f0907", "0x8024002e", "0x8024002f" };

    public static readonly IReadOnlyDictionary<string, string> PolicyNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["0x800f0907"] = "Specify settings for optional component installation and component repair (it does not allow Windows Update as the repair source)",
        ["0x8024002e"] = "the update source policy (a WSUS server with \"Do not connect to any Windows Update Internet locations\")",
        ["0x8024002f"] = "Remove access to use all Windows Update features (DisableWindowsUpdateAccess)",
    };

    // Codes that are routinely logged on fully patched, healthy devices. Never used to route.
    public static readonly string[] NoiseCodes = { "0x8024402f", "0x80240438", "0x80240016", "0x8024200b" };

    public static readonly string[] KnownCodes = ServicingCodes.Concat(TxLogCodes).Concat(SpaceCodes).Concat(SourceCodes).Concat(DeliveryCodes).Concat(PolicyCodes).ToArray();

    // DISM's plain exit codes (Win32 errors, not HRESULTs), for the ScanHealth and RestoreHealth
    // failure paths only. Not in the shared code table: an app installer's exit 87 or 1726 must
    // never be explained as a DISM failure. Same entry form as the shared table.
    public static readonly IReadOnlyDictionary<string, string[]> DismExitKnowledge = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["0x00000057"] = new[] { "ERROR_INVALID_PARAMETER", "DISM rejected its arguments as invalid; this is an internal error of this tool, not a problem of the device.", "Create a support package and contact your IT support.", "Report it as a defect of " + ProductInfo.DisplayName + ", with the support package." },
        ["0x000002e4"] = new[] { "ERROR_ELEVATION_REQUIRED", "DISM asked for administrator rights; this should not happen, because the repair runs as SYSTEM.", "Create a support package and contact your IT support.", "Report it as a defect of " + ProductInfo.DisplayName + ", with the support package." },
        ["0x000006be"] = new[] { "RPC_S_CALL_FAILED", "the servicing stack was busy or restarted while DISM was using it.", "Restart the device and run the repair again." },
    };

    // Step limits in minutes. Chosen, not measured on many devices: a step that needs longer is
    // stopped and reported instead of silently outliving the run.
    public const int ScanHealthMinutes = 30;
    public const int RestoreHealthMinutes = 45;
    public const int SfcMinutes = 30;
    public const int CleanupMinutes = 30;
    public const int GhostRemoveMinutes = 2;
    public const int SearchMinutes = 30;
    public const int InstallMinutes = 90;

    /// <summary>An install starts only with at least this much of the run left.</summary>
    public const int InstallMinMinutes = 45;

    /// <summary>Asking the update client whether it is installing.</summary>
    public const int BusyCheckMinutes = 2;

    // Disconnected network adapters are removed automatically only in the case Microsoft documents
    // for 0x800719e4: a virtual machine's synthetic or accelerated network adapters (PCI or VMBUS,
    // driver netvsc or Mellanox mlx*) piling up after frequent deallocation, with the CBS log
    // showing the driver operations queue failing. Everything else is reported, never removed.
    public const int GhostMinCount = 50;
    public static readonly string[] GhostEnumerators = { "PCI", "VMBUS" };
    public static readonly string[] GhostNeverEnumerators = { "USB", "BTH", "SWD", "ROOT" };
    public static readonly Regex GhostService = new("^(netvsc|mlx4_bus|mlx5|mlx.*)$", Options, Limit);
    public const long CbsTailBytes = 8L * 1024 * 1024;

    /// <summary>The phrases of the CBS log lines that show the driver operations queue failing.</summary>
    public static readonly string[] CbsQueuePhrases = { "non-critical driver operations queue", "Failed uninstalling driver updates" };

    /// <summary>
    /// Services that hold SoftwareDistribution\Download (and DataStore): Windows Update, BITS
    /// (Background Intelligent Transfer Service) and Delivery Optimization.
    /// </summary>
    public static readonly string[] CacheServices = { "wuauserv", "bits", "DoSvc" };

    /// <summary>Minutes a cache reset needs beyond the minimum step time before it stops any service.</summary>
    public const int CacheResetMarginMinutes = 5;

    /// <summary>Services Windows Update needs, in the order they are reported.</summary>
    public static readonly KeyValuePair<string, string>[] RequiredServices =
    {
        new("wuauserv", "Windows Update"),
        new("bits", "Background Intelligent Transfer Service"),
        new("cryptsvc", "Cryptographic Services"),
        new("TrustedInstaller", "Windows Modules Installer"),
        new("UsoSvc", "Update Orchestrator Service"),
    };

    // Update services by ServiceID (the names Windows reports are localised).
    public const string WsusServiceId = "3da21691-e39d-4da6-8a4b-b43877bcb1b7";

    public static readonly IReadOnlyDictionary<string, string> ServiceNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["9482f4b4-e343-43b6-b170-9a65bc822c77"] = "Windows Update",
        ["7971f918-a847-4430-9279-4a52d1efe18d"] = "Microsoft Update",
        [WsusServiceId] = "WSUS",
        ["855e8a7c-ecb4-4ca3-b045-1dfa50104289"] = "Microsoft Store",
    };
}
