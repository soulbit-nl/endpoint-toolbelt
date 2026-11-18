using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace EndpointToolbelt.Engine.Ime;

/// <summary>One MSIX or Appx line-of-business app install as the MDM stack holds it.</summary>
public sealed class MsixAppRow
{
    /// <summary>The package family name (Name_PublisherId), as the CSP node is named.</summary>
    public string PackageFamilyName { get; set; } = "";

    /// <summary>"device" or "user" (the caller's own); never the SID itself.</summary>
    public string Scope { get; set; } = "";

    /// <summary>The CSP's Status (0 to 3); null when it was not there.</summary>
    public int? Status { get; set; }

    /// <summary>LastError as 0x plus eight upper-case hex digits; '' for none.</summary>
    public string Code { get; set; } = "";

    public string Verdict { get; set; } = "";
    public string State { get; set; } = "";

    /// <summary>A download failure (ERROR_INSTALL_NETWORK_FAILURE).</summary>
    public bool Download { get; set; }

    /// <summary>The version of this package family installed now (the nonStore inventory); '' when none is installed.</summary>
    public string Version { get; set; } = "";

    /// <summary>When the record was read (UTC): the CSP gives the state as it is now, without a time of its own.</summary>
    public DateTime? ObservedUtc { get; set; }
}

public sealed class MsixAppReadout
{
    /// <summary>The MDM WMI Bridge answered for the device (the class exists and could be queried).</summary>
    public bool Present { get; set; }

    public List<MsixAppRow> Rows { get; } = new();

    /// <summary>The caller's own installs could not be read (their scope was asked and failed).</summary>
    public bool UserUnreadable { get; set; }
}

/// <summary>One AppInstallation node as the MDM WMI Bridge returns it.</summary>
public sealed class ModernAppInstallation
{
    /// <summary>The node's name: the package family name.</summary>
    public string InstanceId { get; set; } = "";

    public int? Status { get; set; }
    public long? LastError { get; set; }
}

/// <summary>Where the MSIX installs are read from; tests replace it.</summary>
public interface IModernAppSource
{
    /// <summary>The AppInstallation nodes of the device (<paramref name="userSid"/> null) or of that user.</summary>
    IReadOnlyList<ModernAppInstallation> Installations(string userSid);

    /// <summary>The version of a package family installed now (the nonStore inventory); '' when none.</summary>
    string InstalledVersion(string userSid, string packageFamilyName);
}

/// <summary>One AppX deployment code in words: its symbolic name, what it means, the step token, the user's step and IT's.</summary>
public sealed class MsixCode
{
    public MsixCode(string name, string meaning, string next, string step, string support)
    {
        Name = name;
        Meaning = meaning;
        Next = next;
        Step = step;
        Support = support;
    }

    public string Name { get; }
    public string Meaning { get; }

    /// <summary>"it", "wait", "connectivity" or "none" (the app's step tokens).</summary>
    public string Next { get; }

    public string Step { get; }
    public string Support { get; }
}

/// <summary>
/// MSIX and Appx line-of-business apps that Intune deploys through the MDM stack (not the IME), read-only.
/// <para>
/// Sources (learn.microsoft.com, read 2026-11-14): "EnterpriseModernAppManagement CSP" (the
/// AppInstallation/{PackageFamilyName} nodes Status, LastError and LastErrorDesc for ./Device and ./User; Status
/// 0 NOT_INSTALLED, 1 INSTALLING, 2 FAILED, 3 INSTALLED; "This element isn't present after the app is
/// installed"); "Manage apps" (Windows client management: line-of-business apps are installed through the
/// AppInstallation node's HostedInstall, ./User for a user and ./Device to provision the device; the node is
/// cleaned up after a successful install; the AppManagement inventory has a nonStore class for apps not from the
/// Microsoft Store); the MDM Bridge WMI Provider classes MDM_EnterpriseModernAppManagement_AppInstallation01_01
/// and MDM_EnterpriseModernAppManagement_AppManagement01_03 (namespace root\cimv2\mdm\dmmap, partitions
/// local-system and local-user); "Using PowerShell scripting with the WMI Bridge Provider" (device settings are
/// read as local system; a user's through the custom context options PolicyPlatformContext_PrincipalContext_Type
/// and _Id with that user's SID); and "Troubleshooting packaging, deployment, and query of Windows apps" (the
/// AppX deployment codes), and the Packaging API's "Packaging Errors" for an archive that is not a package (an
/// unenrolled Windows Sandbox records 0x8051100F for a file that is not a package). No registry location of these
/// nodes is documented, so none is read.
/// </para>
/// <para>
/// Only the device and the caller's own installs are read; other users' are not read at all (the bridge needs
/// one query per user). LastErrorDesc is not read: it is free text from the deployment stack and can name paths.
/// The package's download address is not in these nodes.
/// </para>
/// </summary>
public static class MsixApps
{
    /// <summary>A package family name: the package name (3 to 50 of A-Z, a-z, 0-9, '.', '-'), '_', the 13-character publisher ID.</summary>
    private static readonly Regex FamilyName = new(@"\A[-.A-Za-z0-9]{3,50}_[0-9a-hjkmnp-tv-z]{13}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsFamilyName(string name) => !string.IsNullOrEmpty(name) && FamilyName.IsMatch(name);

    private const string ItStep = "Copy details for IT and send it to your IT support.";
    private const string EventLog = "The AppXDeployment-Server event log (Microsoft-Windows-AppXDeploymentServer/Operational) has the cause.";
    private const string TrustSupport = "Sign the package with a certificate the device trusts, or deploy the signing certificate (Trusted People) or its root (Trusted Root) to the device.";

    /// <summary>
    /// The AppX deployment codes in words, from Microsoft's table ("Troubleshooting packaging, deployment, and
    /// query of Windows apps"). Kept apart from the shared <see cref="CodeKnowledge"/> table: that one words
    /// 0x800B0109 and 0x80073D02 as Windows Update reports them (TLS inspection, a Store app updating), which
    /// would mislead for a package install.
    /// </summary>
    internal static readonly IReadOnlyDictionary<long, MsixCode> Codes = new Dictionary<long, MsixCode>
    {
        [0x8007000BL] = new("ERROR_BAD_FORMAT", "the package is not correctly formatted, or its signing certificate does not match the publisher in its manifest.", "it", ItStep, "Rebuild or sign the package again so the certificate's subject matches the manifest's publisher."),
        [0x80073CF0L] = new("ERROR_INSTALL_OPEN_PACKAGE_FAILED", "the package could not be opened: it is unsigned, its publisher does not match the signing certificate, or it was not found.", "it", ItStep, "Check the package's signature and publisher in Intune; the AppxPackagingOM and AppXDeployment-Server event logs have the detail."),
        [0x80073CF3L] = new("ERROR_INSTALL_RESOLVE_DEPENDENCY_FAILED", "the package failed update, dependency or conflict validation: it conflicts with an installed package, a dependency is missing, or it does not fit this processor.", "it", ItStep, "Check the app's dependencies (frameworks) and architecture in Intune. " + EventLog),
        [0x80073CF4L] = new("ERROR_INSTALL_OUT_OF_DISK_SPACE", "there is not enough disk space.", "none", "Free up space on the system drive, then check the apps again later.", ""),
        [0x80073CF5L] = new("ERROR_INSTALL_NETWORK_FAILURE", "the package could not be downloaded.", "connectivity", "Run Check Microsoft connections, then check the apps again later.", "Check the network path to the package's download location."),
        [0x80073CF6L] = new("ERROR_INSTALL_REGISTRATION_FAILURE", "the package could not be registered.", "it", ItStep, EventLog),
        [0x80073CF9L] = new("ERROR_INSTALL_FAILED", "the package install failed.", "it", ItStep, EventLog + " The app's vendor may need to fix the package."),
        [0x80073CFBL] = new("ERROR_PACKAGE_ALREADY_EXISTS", "this version is already installed, and a package that is not bitwise identical (a rebuilt or re-signed one) cannot replace it.", "it", ItStep, "Increase the package version and sign it again, or remove the installed package for every user first."),
        [0x80073CFDL] = new("ERROR_INSTALL_PREREQUISITE_FAILED", "an install prerequisite of the package could not be met.", "it", ItStep, EventLog),
        [0x80073CFFL] = new("ERROR_INSTALL_POLICY_FAILURE", "this device does not allow the package: sideloading is not allowed, or its signature is not trusted here.", "it", ItStep, "Check the ApplicationManagement/AllowAllTrustedApps policy and that the signing certificate is trusted on the device."),
        [0x80073D01L] = new("ERROR_DEPLOYMENT_BLOCKED_BY_POLICY", "a policy on this device blocks the deployment, such as an application control policy.", "it", ItStep, "Check the application control policies and the \"Allow deployment operations in special profiles\" policy."),
        [0x80073D02L] = new("ERROR_PACKAGES_IN_USE", "the package could not be installed because files it changes are in use, usually because the app is open.", "wait", "Close the app if it is open, then check the apps again later.", ""),
        [0x80073D06L] = new("ERROR_INSTALL_PACKAGE_DOWNGRADE", "a higher version of this package is already installed.", "it", ItStep, "Check the app's version in Intune against the version installed on the device."),
        [0x80073D10L] = new("ERROR_INSTALL_WRONG_PROCESSOR_ARCHITECTURE", "the package is for another processor architecture.", "it", ItStep, "Assign a package for this device's architecture, or a bundle that has it."),
        [0x80073D19L] = new("ERROR_DEPLOYMENT_BLOCKED_BY_USER_LOG_OFF", "the user signed out while it was being installed.", "wait", "Stay signed in, then check the apps again later.", ""),
        [0x800B0100L] = new("TRUST_E_NOSIGNATURE", "the package is not signed, or its signature is not valid.", "it", ItStep, TrustSupport),
        [0x800B0109L] = new("CERT_E_UNTRUSTEDROOT", "the package's signing certificate ends in a root this device does not trust.", "it", ItStep, TrustSupport),
        [0x800B010AL] = new("CERT_E_CHAINING", "the package's signing certificate cannot be chained to a trusted root.", "it", ItStep, TrustSupport),
        // The Packaging API's code when the file is not a complete package archive (learn.microsoft.com, "Packaging Errors").
        [0x8051100FL] = new("OPC_E_ZIP_MISSING_END_OF_CENTRAL_DIRECTORY", "the package file is not a complete package: its archive has no end record, so it is cut off, damaged or not a package at all.", "it", ItStep, "Check that the package file the assignment deploys is a complete .msix or .appx; add it to Intune again."),
    };

    /// <summary>A code in words with its step; an unknown code goes to IT support.</summary>
    public static MsixCode Code(long code)
    {
        if (Codes.TryGetValue(code, out var entry)) { return entry; }
        return new MsixCode("error " + Hex(code), "", "it", ItStep, EventLog);
    }

    internal static string Hex(long code) => "0x" + ((uint)code).ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>The code's name and hex form for the state line ("ERROR_PACKAGES_IN_USE (0x80073D02)").</summary>
    public static string CodeText(long code)
    {
        if (code == 0) { return ""; }
        return Codes.TryGetValue(code, out var entry) ? entry.Name + " (" + Hex(code) + ")" : "error " + Hex(code);
    }

    /// <summary>The CSP's Status in words, with the verdict the app list uses and whether it is about the download.</summary>
    public static (string Verdict, string State, bool Download) Verdict(int? status, long code)
    {
        var text = CodeText(code);
        var withCode = text.Length > 0 ? ": " + text : "";
        switch (status)
        {
            case 0: return ("IN-PROGRESS", "received, not started yet", false);
            case 1: return ("IN-PROGRESS", "installing", false);
            case 2: return ("RETRY-HELPS", (code == 0x80073CF5L ? "the download failed" : "the install failed") + withCode, code == 0x80073CF5L);
            case 3: return ("OK", "installed (the record is cleaned up next)", false);
            case null: return code != 0 ? ("UNKNOWN", "no status recorded" + withCode, false) : ("NO-STATE", "no status recorded", false);
            default: return ("UNKNOWN", "status " + status.Value.ToString(CultureInfo.InvariantCulture) + withCode, false);
        }
    }

    /// <summary>The device's installs and the caller's own; an install that is not a package family name is skipped.</summary>
    public static MsixAppReadout Read(RunContext run) => Read(run, run.Services.ModernApps);

    public static MsixAppReadout Read(RunContext run, IModernAppSource source)
    {
        var result = new MsixAppReadout();
        if (source == null) { return result; }
        var now = run.Services.Clock.UtcNow;
        IReadOnlyList<ModernAppInstallation> device;
        try { device = source.Installations(null) ?? Array.Empty<ModernAppInstallation>(); }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            run.Log("The MSIX line-of-business apps could not be read (" + ex.Message + ").", Platform.LogSeverity.Warning);
            return result;
        }
        result.Present = true;
        Add(run, source, result, device, null, "device", now);

        // The caller's own installs: only with a user SID (never the service's own or a well-known one).
        var sid = run.CallerSid ?? "";
        if (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) || sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase))
        {
            try { Add(run, source, result, source.Installations(sid) ?? Array.Empty<ModernAppInstallation>(), sid, "user", now); }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                run.Log("Your own MSIX line-of-business apps could not be read (" + ex.Message + ").", Platform.LogSeverity.Warning);
                result.UserUnreadable = true;
            }
        }
        return result;
    }

    private static void Add(RunContext run, IModernAppSource source, MsixAppReadout result, IEnumerable<ModernAppInstallation> nodes, string sid, string scope, DateTime now)
    {
        foreach (var node in nodes)
        {
            var family = (node?.InstanceId ?? "").Trim();
            if (!IsFamilyName(family)) { continue; }
            var code = node.LastError ?? 0;
            var (verdict, state, download) = Verdict(node.Status, code);
            var version = "";
            try { version = (source.InstalledVersion(sid, family) ?? "").Trim(); }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                run.Log("The installed version of an MSIX app could not be read (" + ex.Message + ").", Platform.LogSeverity.Warning);
            }
            result.Rows.Add(new MsixAppRow
            {
                PackageFamilyName = family,
                Scope = scope,
                Status = node.Status,
                Code = code != 0 ? Hex(code) : "",
                Verdict = verdict,
                State = state,
                Download = download,
                Version = version.Length <= 64 && version.All(c => char.IsDigit(c) || c == '.') ? version : "",
                ObservedUtc = now,
            });
        }
    }
}

/// <summary>
/// The MDM WMI Bridge Provider (root\cimv2\mdm\dmmap), read as local system: the device's partition, or a
/// user's through Microsoft's documented context options with that user's SID. Read-only: it only enumerates.
/// </summary>
public sealed class WmiModernAppSource : IModernAppSource
{
    private const string Namespace = @"\\.\root\cimv2\mdm\dmmap";
    private const string InventoryParent = "./Vendor/MSFT/EnterpriseModernAppManagement/AppManagement/nonStore/";
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex Sid = new(@"\AS-1-(5-21|12-1)(-[0-9]{1,10}){1,8}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static ManagementNamedValueCollection Context(string userSid)
    {
        if (userSid == null) { return null; }
        if (!Sid.IsMatch(userSid)) { throw new InvalidOperationException("Not a user SID."); }
        var context = new ManagementNamedValueCollection();
        context.Add("PolicyPlatformContext_PrincipalContext_Type", "PolicyPlatform_UserContext");
        context.Add("PolicyPlatformContext_PrincipalContext_Id", userSid);
        return context;
    }

    private static IEnumerable<ManagementBaseObject> Query(string userSid, string query)
    {
        var context = Context(userSid);
        var options = new EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = false, Rewindable = false };
        var connection = new ConnectionOptions { Impersonation = ImpersonationLevel.Impersonate };
        if (context != null)
        {
            options.Context = context;
            connection.Context = context;
        }
        using var searcher = new ManagementObjectSearcher(new ManagementScope(Namespace, connection), new ObjectQuery(query), options);
        using var found = searcher.Get();
        var list = new List<ManagementBaseObject>();
        foreach (var item in found) { list.Add(item); }
        return list;
    }

    private static int? Int(object value) => value switch
    {
        int i => i,
        uint u when u <= int.MaxValue => (int)u,
        _ => null,
    };

    public IReadOnlyList<ModernAppInstallation> Installations(string userSid)
    {
        var result = new List<ModernAppInstallation>();
        try
        {
            foreach (var item in Query(userSid, "SELECT InstanceID, Status, LastError FROM MDM_EnterpriseModernAppManagement_AppInstallation01_01"))
            {
                using (item)
                {
                    result.Add(new ModernAppInstallation
                    {
                        InstanceId = item["InstanceID"] as string ?? "",
                        Status = Int(item["Status"]),
                        LastError = item["LastError"] switch { int i => (long)(uint)i, uint u => u, _ => null },
                    });
                }
            }
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.NotFound)
        {
            // No AppInstallation node at all.
        }
        return result;
    }

    public string InstalledVersion(string userSid, string packageFamilyName)
    {
        if (!MsixApps.IsFamilyName(packageFamilyName)) { return ""; }
        var best = "";
        Version bestVersion = null;
        try
        {
            foreach (var item in Query(userSid, "SELECT InstanceID, Version FROM MDM_EnterpriseModernAppManagement_AppManagement01_03 WHERE ParentID='" + InventoryParent + packageFamilyName + "'"))
            {
                using (item)
                {
                    var text = (item["Version"] as string ?? "").Trim();
                    if (System.Version.TryParse(text, out var version) && (bestVersion == null || version > bestVersion))
                    {
                        bestVersion = version;
                        best = text;
                    }
                }
            }
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.NotFound)
        {
            // Not installed.
        }
        return best;
    }
}
