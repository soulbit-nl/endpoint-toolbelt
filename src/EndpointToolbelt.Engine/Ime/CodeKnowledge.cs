using System.Collections.Generic;
using System.Globalization;

namespace EndpointToolbelt.Engine.Ime;

/// <summary>One entry of the shared code table.</summary>
public sealed class CodeEntry
{
    public CodeEntry(string name, string meaning, string nextStep, string support = "")
    {
        Name = name;
        Meaning = meaning;
        NextStep = nextStep;
        Support = support;
    }

    /// <summary>The code as '0x' plus eight lowercase hex digits; set by the lookup.</summary>
    public string Code { get; private set; } = "";

    public string Name { get; }
    public string Meaning { get; }
    public string NextStep { get; }

    /// <summary>What only IT support can do; '' for an entry without a support step.</summary>
    public string Support { get; }

    internal CodeEntry WithCode(string code) => new(Name, Meaning, NextStep, Support) { Code = code };
}

/// <summary>
/// Shared table of result codes (HRESULTs): what a code means in plain words and the next step
/// Microsoft documents for it. One table for every action, so the MDM sync, the Windows Update
/// repair and the app readout never describe the same code differently. It explains; it never
/// routes. A code without a documented meaning is left out on purpose.
/// </summary>
public static class CodeKnowledge
{
    internal static readonly IReadOnlyDictionary<string, CodeEntry> Table = new Dictionary<string, CodeEntry>
    {
        ["0x80070002"] = new("ERROR_FILE_NOT_FOUND", "a file the update needs is missing, often left behind by an earlier incomplete update.", "Restart the device and run the repair again; if it fails again, contact your IT support.", "The CBS log names the missing file (it is in the support package). A download that fails with this right after a cache reset usually succeeds on the next scan."),
        ["0x80070003"] = new("ERROR_PATH_NOT_FOUND", "servicing could not reach a path.", "Restart the device and run the repair again; if it fails again, contact your IT support.", "The CBS log names the path; it is in the support package."),
        ["0x80070005"] = new("E_ACCESSDENIED", "access was denied: permissions on WinSxS or SoftwareDistribution, a security product holding files, or a policy.", "Contact your IT support.", "Check security software exclusions. Microsoft then documents resetting the permissions on WinSxS and SoftwareDistribution."),
        ["0x8007000d"] = new("ERROR_INVALID_DATA", "the downloaded content is invalid or damaged.", "Run the repair again: the update is downloaded again."),
        ["0x80070020"] = new("ERROR_SHARING_VIOLATION", "a file the update needs is in use, usually by a non-Microsoft filter driver such as antivirus.", "Restart the device and run the repair again; if it fails again, contact your IT support.", "Check the security software and other non-Microsoft filter drivers."),
        ["0x80070070"] = new("ERROR_DISK_FULL", "there is not enough disk space.", "Free up space on the system drive."),
        ["0x80070422"] = new("ERROR_SERVICE_DISABLED", "a service Windows Update needs is disabled.", "Contact your IT support.", "Find the policy that disables it; the diagnosis lists the start types of the update services."),
        ["0x80070570"] = new("ERROR_FILE_CORRUPT", "a file or folder is damaged and cannot be read. For an update, Microsoft names component store damage.", "Restart the device and run the repair again; if it fails again, contact your IT support.", "For Windows Update: DISM RestoreHealth and SFC; the CBS log in the support package names the file."),
        ["0x800705b4"] = new("ERROR_TIMEOUT", "an operation timed out. Microsoft has no Windows Update-specific documentation for it.", "Run the repair again when the device is idle and on a stable network."),
        ["0x80070642"] = new("MSI 1602, the installation was canceled before it finished", "the installation was stopped before it finished, by a person or by something that closed the installer.", "Try the install again; if it is canceled again, contact your IT support.", "Microsoft's advice: let the installation run to its end, and where possible install the app for the system rather than for the user."),
        ["0x80070643"] = new("MSI 1603, fatal error during installation", "the installer itself failed; the reason is in the installer's own log, not in the IME log.", "Read the installer's own log. Retry only once the cause is known."),
        ["0x8007064c"] = new("Machine already enrolled", "a stale enrollment certificate or registry state was left behind, for example by a cloned image.", "Your IT support repairs the enrollment."),
        ["0x80070652"] = new("MSI 1618, another installation is running", "Windows Installer was busy with another installation.", "Nothing to fix on the app; the IME retries it once the other installation has finished."),
        ["0x80070653"] = new("MSI 1619, package could not be opened", "the installer package is missing, incomplete or not a valid package.", "Check that the package content is complete. Clearing downloaded app content makes the IME fetch it again."),
        ["0x80070659"] = new("MSI 1625, installation forbidden by system policy", "a policy on this device blocks the installation.", "Find the policy that restricts Windows Installer on this device; repeating the install does not help."),
        ["0x80070666"] = new("MSI 1638, another version is already installed", "a different version of the product is installed and blocks this one.", "Remove the other version or make the package upgrade it; check supersedence in Intune."),
        ["0x80070669"] = new("MSI 1641, restart started by the installer", "the installation succeeded and the installer restarted the device.", "Nothing to fix; this is a success code."),
        ["0x80070bc2"] = new("3010, restart required", "the installation or update succeeded and needs a restart to complete.", "Restart the device."),
        ["0x80070bc9"] = new("ERROR_FAIL_REBOOT_REQUIRED", "an update failed and needs a restart to roll back. Microsoft names a policy that sets the Windows module installer (TrustedInstaller) to Manual as the usual cause.", "Restart the device and run the repair again; if it fails again, contact your IT support.", "Check the start type of TrustedInstaller in the diagnosis and the policy that sets it."),
        ["0x800719e4"] = new("ERROR_LOG_FULL", "the servicing transaction log is full. Microsoft documents two causes: a transaction log that has filled up, and a queue of driver operations that keeps growing because of disconnected (ghost) network adapters, seen on virtual machines in Azure that are deallocated often.", "Contact your IT support: a restart alone does not clear a full transaction log.", "This repair removes the disconnected network adapters only when the CBS log shows the driver operations queue failing with this code and many such adapters of a virtual machine are present; otherwise it reports them with counts per bus. Removing them (pnputil /remove-device) and restarting is the documented fix for that case."),
        ["0x80072ee2"] = new("ERROR_WINHTTP_TIMEOUT", "the request timed out.", "Check the network path and proxy to the Microsoft endpoints; try another network."),
        ["0x80072ee7"] = new("ERROR_WINHTTP_NAME_NOT_RESOLVED", "the server name could not be resolved (DNS).", "Check DNS and the network connection."),
        ["0x80072efd"] = new("ERROR_WINHTTP_CANNOT_CONNECT", "no connection to the server could be made.", "Check the firewall, the proxy and the network connection."),
        ["0x80072efe"] = new("ERROR_WINHTTP_CONNECTION_ERROR", "the other side reset or closed the connection.", "Usually a proxy or TLS inspection in the network path; check that path."),
        ["0x80072f0d"] = new("ERROR_WINHTTP_SECURE_INVALID_CA", "the server certificate chain ends in an untrusted root.", "This points at TLS inspection on the network: exclude the Microsoft endpoints from it."),
        ["0x80072f8f"] = new("ERROR_WINHTTP_SECURE_FAILURE", "the secure connection failed on the server certificate.", "Check the device clock first, then TLS inspection on the network."),
        ["0x800736b3"] = new("ERROR_SXS_ASSEMBLY_NOT_FOUND", "a component the update refers to is not installed. Microsoft has no Windows Update-specific documentation for it.", "Treated as component store damage: this repair checks the store."),
        ["0x80073701"] = new("ERROR_SXS_ASSEMBLY_MISSING", "a component was left partially installed.", "This repair checks and repairs the component store on this code."),
        ["0x80073712"] = new("ERROR_SXS_COMPONENT_STORE_CORRUPT", "the component store is damaged. Also one of the codes of the 2026 known issue for devices upgraded to 24H2 or 25H2.", "This repair checks and repairs the component store on this code."),
        ["0x8007371b"] = new("ERROR_SXS_TRANSACTION_CLOSURE_INCOMPLETE", "component store damage.", "This repair checks and repairs the component store on this code."),
        ["0x80073cf0"] = new("ERROR_INSTALL_OPEN_PACKAGE_FAILED", "an MSIX or Appx package could not be opened: unsigned, a publisher mismatch or a wrong path.", "Check the package signature and publisher; the AppXDeployment-Server event log has the detail."),
        ["0x80073cf3"] = new("ERROR_INSTALL_RESOLVE_DEPENDENCY_FAILED", "an MSIX or Appx package conflicts with an installed one, lacks a dependency or has the wrong architecture.", "The AppXDeployment-Server event log names the dependency."),
        ["0x80073cf9"] = new("ERROR_INSTALL_FAILED", "an MSIX or Appx installation failed.", "The AppXDeployment-Server event log has the actual cause."),
        ["0x80073d02"] = new("ERROR_PACKAGES_IN_USE", "a Store app could not update because it was running. This is not a Windows update failure.", "Nothing to fix; the app updates once it is closed."),
        ["0x800b0109"] = new("CERT_E_UNTRUSTEDROOT", "a certificate chain ends in an untrusted root.", "Restart the device and run the repair again; if it fails again, contact your IT support.", "Check TLS inspection on the network."),
        ["0x800f081f"] = new("CBS_E_SOURCE_MISSING", "files the update needs are not in the component store, and the repair source did not have them either.", "If the update keeps failing with this code, contact your IT support.", "DISM RestoreHealth repairs from the repair source set by policy, or otherwise from Windows Update. On a device that gets its updates from WSUS, configure a repair source (policy: Specify settings for optional component installation and component repair). If neither can supply the files, the device needs an in-place upgrade, which keeps apps and files."),
        ["0x800f0821"] = new("CBS_E_ABORT", "the servicing watchdog stopped an update transaction that took longer than allowed, typically on a slow or overloaded device or virtual machine.", "Run the repair again when the device is idle.", "On a virtual machine, give it more CPU and memory; 0x800F0920 can follow this code."),
        ["0x800f0825"] = new("CBS_E_CANNOT_UNINSTALL", "an update package cannot be removed: component store damage.", "This repair checks and repairs the component store on this code."),
        ["0x800f0831"] = new("CBS_E_STORE_CORRUPTION", "the manifest of an earlier update is missing from the component store.", "Restart the device and run the repair again; if it fails again, contact your IT support.", "DISM RestoreHealth and SFC, or install the missing update from the Microsoft Update Catalog, then retry."),
        ["0x800f0906"] = new("CBS_E_DOWNLOAD_FAILURE", "the repair files could not be downloaded.", "Check the network connection and run the repair again; if it fails again, contact your IT support.", "The device needs access to Windows Update or a configured repair source (policy: Specify settings for optional component installation and component repair)."),
        ["0x800f0907"] = new("CBS_E_GROUPPOLICY_DISALLOWED", "a policy prevents using Windows Update as the repair source.", "Contact your IT support.", "Configure a repair source in the policy Specify settings for optional component installation and component repair, or allow Windows Update for repair content in it."),
        ["0x800f0915"] = new("Repair content not found", "not documented by Microsoft; DISM reports that it could not find the repair content.", "If the update keeps failing with this code, contact your IT support.", "An in-place upgrade, which keeps apps and files, when neither the repair source set by policy nor Windows Update can supply the content."),
        ["0x800f0920"] = new("CBS_E_HANG_DETECTED", "the servicing watchdog stopped an operation that took too long, typically on a slow or overloaded device or virtual machine.", "Run the repair again when the device is idle.", "On a virtual machine, give it more CPU and memory."),
        ["0x800f0922"] = new("CBS_E_INSTALLERS_FAILED", "an installer inside the update failed. Microsoft documents two causes: too little free space on the EFI system partition (the cumulative update rolls back at 35-36% during the restart; a May 2026 issue fixed by KB5089573) and a licensing token update that could not be written.", "Free up space on the system drive if it is low; this repair also treats the code as a space problem. If it fails again, contact your IT support.", "Check the EFI system partition free space in the diagnosis: 10 MB or less causes the rollback. For the licensing token cause (the CBS log shows the License and Product key tokens failing to update; documented for Windows Server 2016), Microsoft documents giving the User and Network Service accounts write permission on C:\\Windows\\System32\\spp."),
        ["0x800f0983"] = new("Servicing: matching component directory missing", "no Microsoft page documents this code.", "Treated as component store damage: this repair checks the store."),
        ["0x800f0984"] = new("PSFX_E_MATCHING_BINARY_MISSING", "a component directory exists, but its file is missing: component store damage.", "This repair checks and repairs the component store on this code."),
        ["0x800f0986"] = new("PSFX_E_APPLY_FORWARD_DELTA_FAILED", "an update file could not be applied: component store damage.", "This repair checks and repairs the component store on this code."),
        ["0x800f0993"] = new("Servicing: rebase hydration candidates missing", "a Microsoft known issue (2026) for some devices upgraded to 24H2 or 25H2 from an older version: the cumulative update cannot install. Published in the Microsoft 365 admin center only; public detail comes from press coverage.", "Contact your IT support: the fix needs an administrator.", "The reported mitigation removes the stale baseline RollupFix package with DISM, otherwise an in-place upgrade. This repair reports such a package; removing it is an administrator setting (WURemoveStalePackages)."),
        ["0x80180002"] = new("MENROLL_E_DEVICE_AUTHENTICATION_ERROR", "the device could not authenticate to the management service.", "Your IT support checks the device in Intune and Entra ID."),
        ["0x80180014"] = new("MENROLL_E_DEVICENOTSUPPORTED", "an enrollment restriction blocks Windows enrollment.", "Allow Windows (MDM) in the Intune enrollment restrictions for this user."),
        ["0x80180018"] = new("MENROLL_E_USERLICENSE", "the user's Intune license is missing or in a bad state.", "Check the user's license assignment."),
        ["0x80180026"] = new("MENROLL_E_DEVICE_MANAGEMENT_BLOCKED", "management is blocked on this device, for example by a policy or by the legacy Intune PC agent.", "Find the policy or agent that blocks MDM enrollment."),
        ["0x8018002b"] = new("UPN not usable for enrollment", "the user's UPN is unverified or ends in .local, or the MDM user scope is None.", "Correct the UPN suffix or the MDM user scope in Entra ID."),
        ["0x801901ad"] = new("HTTP 429, too many requests", "the service is rate limiting this device.", "No action needed: Windows retries by itself, and requesting again only lengthens the wait."),
        ["0x80200053"] = new("BG_E_VALIDATION_FAILED", "a downloaded file did not pass validation; Microsoft names a firewall that filters the downloads.", "Contact your IT support.", "Check the firewall or web filter for Windows Update downloads."),
        ["0x8024000b"] = new("WU_E_CALL_CANCELLED", "the operation was cancelled.", "Nothing to fix; run the repair again if needed."),
        ["0x8024000e"] = new("WU_E_XML_INVALID", "the update's description data is invalid.", "Restart the device and run the repair again; if it fails again, contact your IT support.", "Microsoft documents making sure the latest Windows Update Agent is installed."),
        ["0x80240016"] = new("WU_E_INSTALL_NOT_ALLOWED", "another installation is running or a restart is pending; temporary.", "Restart the device or wait, then run the repair again."),
        ["0x80240017"] = new("WU_E_NOT_APPLICABLE", "the update does not apply (superseded, already installed or another version).", "Nothing to fix."),
        ["0x80240020"] = new("WU_E_NO_INTERACTIVE_USER", "the operation needs a signed-in user.", "Sign in and allow the restart."),
        ["0x80240022"] = new("WU_E_ALL_UPDATES_FAILED", "every update in the operation failed; Microsoft names security software that blocks SoftwareDistribution as the most common cause.", "Contact your IT support.", "Check security software exclusions; the CBS log has the inner code."),
        ["0x8024002e"] = new("WU_E_WU_DISABLED", "access to an unmanaged update server is not allowed: typically a policy that allows only the managed update source, such as a WSUS server together with \"Do not connect to any Windows Update Internet locations\".", "Contact your IT support.", "Check the policy settings that restrict the update source (UseWUServer, DoNotConnectToWindowsUpdateInternetLocations) and allow the source this device should use."),
        ["0x8024002f"] = new("WU_E_CALL_CANCELLED_BY_POLICY", "a policy blocks access to Windows Update, for example \"Remove access to use all Windows Update features\" (DisableWindowsUpdateAccess).", "Contact your IT support.", "Find and remove the policy that sets DisableWindowsUpdateAccess; clearing it on the device would be undone at the next policy refresh."),
        ["0x80240034"] = new("WU_E_DOWNLOAD_FAILED", "the download failed.", "Check the network connection and run the repair again; if it fails again, contact your IT support.", "Check the proxy and the network path to the update endpoints."),
        ["0x80240438"] = new("Endpoint unreachable", "not on any Microsoft page; commonly the last logged error on healthy, up-to-date devices.", "Usually nothing to fix."),
        ["0x80242006"] = new("WU_E_UH_INVALIDMETADATA", "the update's metadata is invalid.", "Restart the device and run the repair again; if it fails again, contact your IT support.", "Microsoft documents renaming the SoftwareDistribution and catroot2 folders, a reset of the Windows Update cache."),
        ["0x8024200b"] = new("WU_E_UH_INSTALLERFAILURE", "an installer failed. This is a wrapper code: the cause is the servicing code logged next to it.", "Restart the device and run the repair again; if it fails again, contact your IT support.", "Look at the other codes of the same attempt; the CBS log in the support package has the servicing code."),
        ["0x80242014"] = new("WU_E_UH_POSTREBOOTSTILLPENDING", "an update still has work to finish after the restart.", "Restart the device, then run the repair again."),
        ["0x80242016"] = new("WU_E_UH_POSTREBOOTUNEXPECTEDSTATE", "an update was in an unexpected state after the restart.", "Restart the device and run the repair again; if it fails again, contact your IT support.", "The support package has the Windows Update and CBS logs."),
        ["0x80244007"] = new("WU_E_PT_SOAPCLIENT_SOAPFAULT", "the update service returned a SOAP fault, typically a WSUS cookie problem.", "Contact your IT support.", "Check whether the device is pointed at a WSUS server (see the update source in the diagnosis)."),
        ["0x80244018"] = new("WU_E_PT_HTTP_STATUS_FORBIDDEN", "HTTP 403: a proxy or web filter refused the request.", "Contact your IT support.", "Check the proxy or web filter for the Windows Update endpoints."),
        ["0x8024401b"] = new("WU_E_PT_HTTP_STATUS_PROXY_AUTH_REQ", "HTTP 407: the proxy asks for authentication.", "Contact your IT support.", "Allow the Windows Update endpoints through the proxy without authentication, or bypass the proxy for them."),
        ["0x8024401c"] = new("WU_E_PT_HTTP_STATUS_REQUEST_TIMEOUT", "HTTP 408 from the update service; temporary.", "Run the repair again later."),
        ["0x80244022"] = new("WU_E_PT_HTTP_STATUS_SERVICE_UNAVAIL", "the update service answered HTTP 503; often temporary.", "Run the repair again later; if it fails again, contact your IT support.", "Check the network and proxy path to the update service."),
        ["0x8024402c"] = new("WU_E_PT_WINHTTP_NAME_NOT_RESOLVED", "the proxy or update server name could not be resolved.", "Check the network connection and run the repair again; if it fails again, contact your IT support.", "Check DNS and the proxy."),
        ["0x8024402f"] = new("WU_E_PT_ECP_SUCCEEDED_WITH_ERRORS", "external cab processing completed with errors. Seen on healthy devices; Microsoft names a web filter as one cause.", "Usually nothing to fix."),
        ["0x80246007"] = new("WU_E_DM_NOTDOWNLOADED", "the update was not downloaded.", "Check the network connection and run the repair again; if it fails again, contact your IT support.", "Check the network path for downloads."),
        ["0x80246010"] = new("Download sandbox not found", "not documented by Microsoft.", "Run the repair again: it clears the download cache on this code."),
        ["0x80246017"] = new("WU_E_DM_UNAUTHORIZED_LOCAL_USER", "the signed-in user is not allowed to download this update; it needs a local administrator.", "Contact your IT support."),
        ["0x8024a000"] = new("WU_E_AU_NOSERVICE", "the Automatic Updates service could not handle the request; often temporary.", "Run the repair again later."),
        ["0x8024a10a"] = new("USO_E_SERVICE_SHUTTING_DOWN", "the Windows Update service stopped after a long time without activity.", "Nothing to fix; run the repair again if needed."),
        ["0x8024d009"] = new("WU_E_SETUP_SKIP_UPDATE", "an update of the Windows Update Agent itself was skipped.", "Contact your IT support.", "Microsoft documents this when a WSUS server does not send the agent's self-update; check the WSUS server."),
        ["0x80d02002"] = new("TIME_OUT_ERRORS", "the download timed out; Microsoft names a firewall or proxy that blocks the Microsoft download addresses.", "Check the network path and proxy to the Microsoft endpoints; try another network."),
        // WinGet's own codes, for Microsoft Store apps the IME installs through WinGet (microsoft/winget-cli, AppInstallerErrors.h).
        ["0x8a150008"] = new("WinGet: download failed", "the app's installer could not be downloaded.", "Run Check Microsoft connections. When the connection works, Retry work apps and settings starts a new attempt.", "Check the network path to the Microsoft Store and the app's download (proxy, firewall)."),
        ["0x8a15000b"] = new("WinGet: sources not valid", "the app sources WinGet uses on this device are not set up correctly.", "Contact your IT support.", "Check the WinGet sources on this device, in particular that the msstore source is present and enabled."),
        ["0x8a15000f"] = new("WinGet: source data missing", "WinGet has no data for its app source yet.", "Run Check Microsoft connections, then Retry work apps and settings.", "Check that this device can reach the Microsoft Store source."),
        ["0x8a150010"] = new("WinGet: no applicable installer", "the app has no installer that fits this device (architecture, installer type or Windows version).", "Contact your IT support.", "Check the app in Intune: whether the Store app supports this device's architecture and Windows version."),
        ["0x8a150011"] = new("WinGet: installer hash mismatch", "the downloaded installer does not match the expected file.", "Retry work apps and settings downloads it again. If it fails again, contact your IT support.", "A proxy or a security product may change the download; check the download path."),
        ["0x8a150014"] = new("WinGet: package not found in source", "the app was not found in the app source (the Microsoft Store).", "Contact your IT support.", "Check that the app is still available in the Microsoft Store for this region and that the msstore source is enabled on this device."),
        ["0x8a15001b"] = new("WinGet: the Microsoft Store is blocked by policy", "a policy on this device blocks the Microsoft Store as an app source.", "Contact your IT support.", "Check the policies that turn off the Microsoft Store or its app source; Intune installs Store apps through it."),
        ["0x8a15001c"] = new("WinGet: this Store app is blocked by policy", "a policy on this device blocks this Microsoft Store app.", "Contact your IT support.", "Check the policies that block Store apps on this device."),
        ["0x8a15001e"] = new("WinGet: Microsoft Store install failed", "the Microsoft Store could not install the app.", "Retry work apps and settings starts a new attempt. If it fails again, contact your IT support.", "The AppXDeployment-Server event log has the cause."),
        ["0x8a150045"] = new("WinGet: source could not be opened", "WinGet could not open its app source.", "Run Check Microsoft connections, then Retry work apps and settings.", "Check the network path to the Microsoft Store source."),
        ["0x8a150061"] = new("WinGet: already installed", "the app is already installed.", "Nothing to fix; check the apps again."),
        ["0x8a150080"] = new("WinGet: no applicable Store package", "the Microsoft Store has no package of this app that fits this device.", "Contact your IT support.", "Check whether the Store app supports this device."),
        ["0x8a150082"] = new("WinGet: no applicable Store package", "the Microsoft Store has no package of this app that fits this device.", "Contact your IT support.", "Check whether the Store app supports this device."),
        ["0x8a150101"] = new("WinGet: app in use", "the app was running, so it could not be installed or updated.", "Close the app; Windows tries again by itself."),
        ["0x8a150102"] = new("WinGet: another install in progress", "another installation was running.", "Nothing to fix on the app; it is tried again once the other installation has finished."),
        ["0x8a150105"] = new("WinGet: disk full", "there is not enough disk space.", "Free up disk space, then Retry work apps and settings."),
        ["0x8a150109"] = new("WinGet: restart required", "the install needs a restart to finish.", "Restart the device."),
        ["0x8a15010f"] = new("WinGet: install blocked by policy", "a policy on this device blocks the install.", "Contact your IT support.", "Check the policies that restrict app installs on this device."),
        ["0x87d1041c"] = new("Installed, not detected", "the installer reported success, but the app's detection rule did not find the app afterwards.", "Compare the detection rule with what the installer leaves on the device (path, version, product code). Another attempt gives the same result."),
        ["0x87d300c9"] = new("Install timed out (IME)", "the installer was still running when the app's install time limit ran out. Microsoft documents the time limit (60 minutes by default), not this code.", "Check whether the installer waits for input or hangs. Raise the install time limit in Intune only for an installation that really takes that long."),
        ["0xc1900101"] = new("Setup: rolled back (driver)", "a feature update rolled back, usually because of a driver.", "Contact your IT support.", "Update or remove drivers, firmware and security software; the Panther setup logs show which phase failed."),
        ["0xc190020e"] = new("Setup: not enough free space", "there is not enough free space for the upgrade.", "Free up space on the system drive."),
    };

    /// <summary>A code as '0x' plus eight lowercase hex digits, from hex, signed or unsigned decimal text; null when it is not a 32-bit code.</summary>
    public static string Key(string code)
    {
        var value = StatusCode.Parse(code);
        return value == null ? null : "0x" + value.Value.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>A code for display as 0x plus eight upper-case hex digits; '' when the text is not a 32-bit code.</summary>
    public static string FormatHex(string code)
    {
        var key = Key(code);
        return key == null ? "" : "0x" + key.Substring(2).ToUpperInvariant();
    }

    /// <summary>The table entry for a code (any of the forms above), or null when it is unknown.</summary>
    public static CodeEntry Get(string code)
    {
        var key = Key(code);
        return key != null && Table.TryGetValue(key, out var entry) ? entry.WithCode(key) : null;
    }

    /// <summary>' Code 0x... (name): meaning' for a known code, or '' so callers can always append it.</summary>
    public static string Format(string code)
    {
        var entry = Get(code);
        return entry == null ? "" : " Code " + entry.Code + " (" + entry.Name + "): " + entry.Meaning;
    }
}

/// <summary>Status codes as event data and the IME write them.</summary>
public static class StatusCode
{
    private static readonly System.Text.RegularExpressions.Regex Hex = new(@"\A0[xX][0-9A-Fa-f]{1,8}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant, System.TimeSpan.FromSeconds(1));

    /// <summary>
    /// A status from text (hex, signed or unsigned decimal) as an unsigned 32-bit value in an
    /// Int64, or null when the text is not a status: an unreadable field never reads as success.
    /// </summary>
    public static long? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        var trimmed = text.Trim();
        if (Hex.IsMatch(trimmed)) { return long.Parse(trimmed.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture); }
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            if (value < -2147483648L || value > 4294967295L) { return null; }
            return value < 0 ? value + 4294967296L : value;
        }
        return null;
    }

    /// <summary>WinHTTP status codes are 0x80190000 + the HTTP status (0x801901AD is 429); 0 for anything else.</summary>
    public static int HttpStatus(long code) => ((code >> 16) & 0xFFFF) == 0x8019 ? (int)(code & 0xFFFF) : 0;
}
