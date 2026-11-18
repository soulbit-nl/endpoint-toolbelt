# Deployment

> **Status:** the MSI is built by `build\build.ps1` (WiX 5.0.2, `installer\`); its install,
> repair, upgrade, downgrade refusal and uninstall are tested in Windows Sandbox. The Intune
> section has not been tried against a tenant yet.

## What IT support should expect

Before a broad rollout, read [What IT support should expect](admin-guide.md#before-a-broad-rollout)
in the README: no central view, what is not repaired, what
works without a connection, English only, and what is not confirmed on real devices yet.
## Package

- `EndpointToolbelt-<version>-x64.msi`: per-machine, x64, Windows Installer 5.0.
- Manufacturer: SoulBit. Product name: Endpoint Toolbelt.
- A new product code for every build, one fixed upgrade code. Major upgrades
  replace the previous version in place, and a build of the same version replaces the
  installed build the same way; downgrades are blocked.

## Install, upgrade, repair, uninstall

```cmd
msiexec /i EndpointToolbelt-<version>-x64.msi /qn /l*v "%TEMP%\EndpointToolbelt-install.log"
:: repair
msiexec /fa {ProductCode} /qn
:: uninstall
msiexec /x {ProductCode} /qn
```

In PowerShell, quote the product code (`'{ProductCode}'`) and use `$env:TEMP`.

- **Install** creates the program folder, the service (Automatic, Delayed Start),
  the service security descriptor and recovery actions, the registry root
  `HKLM\SOFTWARE\EndpointToolbelt` with its `Config` and `State` keys (SYSTEM and
  Administrators only), the `Endpoint Toolbelt` event log with its source, and the
  Start Menu shortcut "Endpoint Toolbelt".
- **Upgrade** keeps configuration values, cooldown state and per-user data. Values
  passed on the command line replace the stored ones. Open app windows are closed
  through Restart Manager, and the service also asks connected apps to exit.
  The service stops within 25 seconds. An action that is still running is stopped and
  reported to its user as *interrupted*; work it already handed to Windows may finish.
- **Repair** restores files, the service, security descriptors and registry values.
  A repair never changes configuration values.
- **Uninstall** removes the program, the service, the service's SYSTEM-only working
  folder, the `Config` and `State` keys and the registry root, the event source and the
  event log registration, and the shortcut. It leaves each user's
  `%LOCALAPPDATA%\EndpointToolbelt` folder, the product's logs in the IME Logs folder
  (`EndpointToolbelt*.log`, `.lo_`), the event log file (`.evtx`) under
  `%SystemRoot%\System32\winevt\Logs`, and any policy values under
  `HKLM\SOFTWARE\Policies\EndpointToolbelt`. The uninstall never touches user profiles.

### What the MSI does

The service and the engine rely on these points (`installer\Package.wxs`, `Config.wxs`; core
WiX elements only):

- **Binaries:** from `artifacts\staging\bin` (`-p:BinDir`) and the engine's data from
  `artifacts\staging\Engine` (`-p:EngineDir`); see [building.md](building.md).
  The MSI always installs to `C:\Program Files\SoulBit\Endpoint Toolbelt` (below the
  publisher's folder): the folder cannot be
  changed on the command line (`INSTALLFOLDER` is ignored). Keep the inherited Program Files
  ACL: the service refuses to start when anyone but SYSTEM, Administrators or
  TrustedInstaller can change its folder or a file in it, and before every run it refuses an
  engine executable or folder with the same problem (both event 1020). The install folder
  holds the app, the service, the compiled engine `EndpointToolbelt.Engine.exe` and the
  shared `EndpointToolbelt.Contracts.dll` and `EndpointToolbelt.Platform.dll`; its `Engine`
  folder holds only the IME log pattern table. The MSI installs no script file. Three
  actions call one in-box cmdlet each through `powershell.exe` (Back up recovery key, Free
  up disk space, and Reset Company Portal data in the app); see
  [architecture.md](architecture.md#compiled-engine).
- **Service:** `EndpointToolbeltService` (`EndpointToolbelt.Service.exe`), LocalSystem,
  Automatic with delayed start, stopped at install and uninstall, removed at uninstall.
  Its security descriptor (core `PermissionEx`) replaces the full DACL with
  `D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)`,
  so interactive users may start the service (the app does when it is stopped) but not
  stop or reconfigure it.
- **Registry:** create `HKLM\SOFTWARE\EndpointToolbelt` with its `Config` and `State` keys
  (64-bit view) with the protected ACL `D:PAI(A;CI;KA;;;SY)(A;CI;KA;;;BA)` (SYSTEM and
  Administrators full control, inherited by subkeys, no entries for users). Windows
  Installer applies that ACL only to `State`: it creates the root and `Config` while it
  writes their first value, and then ignores the key's own permission entry. The service
  therefore enforces the ACL on all three keys at every start (creating any that are
  missing). The engine still creates `State` when it is missing; it then inherits the
  root's ACL. The root holds
  one value, `InstalledVersion` (the product version, for inventory). Configuration
  properties are written as `REG_SZ` to `Config` at install and upgrade only, and only when
  given (remember-property pattern: a value on the command line wins and stored values
  survive an upgrade). They are not marked `Secure`, so a repair a standard user starts
  cannot pass new values. A value cannot be removed by passing an empty property; set
  another value, or override it with the policy key.
  The MSI never touches `HKLM\SOFTWARE\Policies\EndpointToolbelt`.
- **Event log:** register the log `Endpoint Toolbelt` with the source `Endpoint Toolbelt`
  (`HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Endpoint Toolbelt\Endpoint Toolbelt`; installed names keep their spelling).
  The service writes one insertion string per event (`ReportEvent`), so the source's
  `EventMessageFile` must be one whose messages are just `%1`, such as .NET's
  `%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll`. The log's
  `CustomSD` allows writes only to SYSTEM and Administrators:
  `O:BAG:SYD:(A;;0x7;;;SY)(A;;0x7;;;BA)(A;;0x1;;;S-1-5-32-573)` (Event Log Readers may read).
  The service never creates a source (it uses `RegisterEventSource`, never
  `EventLog.WriteEntry`): it writes events only while that key exists, checked every
  10 minutes, and otherwise skips them (the CMTrace service log still has every line).
  Windows requires
  the first eight characters of a classic event log name to be unique, so the log cannot
  be registered on a device that already has another custom log whose name starts with
  `Endpoint`; the service then works without events.
- **Recovery:** the MSI sets `FailureActionsWhen="failedToStopOrReturnedError"` (WiX
  `ServiceConfig`, with the delayed start). When the service cannot keep its pipe
  available, it stops with exit code 1064 (`ERROR_EXCEPTION_IN_SERVICE`); without that
  setting the service control manager would not run the recovery actions for such a stop.
  A fault stop does not ask the apps to close, because the service comes back.
  The restart actions themselves (restart after 1, 1 and 5 minutes; failure count reset
  after a day) are set **by the service at every start**. Windows Installer cannot set
  them: `MsiServiceConfigFailureActions` opens the service without `SERVICE_START`, which
  Windows requires for a restart action, and the install fails with error 1939 (access
  denied). WiX warns about this (WIX1149) and offers only its Util extension's custom
  action, which this product does not use (core WiX elements only). A changed or repaired service gets its restart actions back at the next start.
- **Same-version upgrade**: every build gets its own product code, so two builds of one
  version (two test builds, for example) are different products. `MajorUpgrade` has
  `AllowSameVersionUpgrades="yes"`: a build of the same version replaces the installed one
  exactly like an upgrade to a newer version (the old one is removed first without the
  remove-state step, so results, state and configuration stay). Without it a newer build of the
  same version installed beside the installed one, and `msiexec /x` of the newer MSI could not
  remove the installed build. An older version is still refused.
- **ICE validation** runs on every build; ICE43 and ICE57 are suppressed because they assume
  a per-user Programs folder for the shortcut, which a per-machine package never uses, and
  ICE61 because its warning (the upgrade range includes the package's own version) is the
  same-version upgrade above.
- **Uninstall:** run `EndpointToolbelt.Service.exe --remove-state` as a custom action
  only on a real uninstall (condition `REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`, so a
  major upgrade keeps results and state), deferred and not impersonated (SYSTEM),
  sequenced after `StopServices` and `DeleteServices` (the service must not be running)
  and after `RemoveRegistryValues` (its own `Config` values are gone), and before
  `RemoveFiles` (the executable must still exist); ignore its exit code. It has no rollback: an uninstall that fails later leaves the product without its last results, rate-limit counts and cooldown stamps. It first waits (at
  most 20 seconds) until the stopped service's process has exited, because that process can
  still hold its log file open right after the service reported Stopped, and retries a delete
  that hits a file in use for up to 10 seconds. It finds the working folder from the fixed
  LocalSystem profile path, not from the shell's folder API, which inside a Windows Installer
  custom action did not return SYSTEM's profile (found in the Sandbox test). It deletes the
  working folder (only an absolute path under `%SystemRoot%\System32\config\systemprofile`,
  without following links), the `State` key, and the registry root when nothing but an
  empty `Config` key is left. It returns 0 and never fails the uninstall; when it is not
  run as SYSTEM it touches nothing. Problems go to standard error (the MSI log) and, while
  the source still exists, to event 1020.
- **Shortcut:** "Endpoint Toolbelt" in the root of the all-users Start Menu Programs
  folder (the app's window title is "Endpoint Toolbelt").

## Microsoft Intune (Win32 app)

1. Wrap the MSI with `IntuneWinAppUtil.exe`.
2. Install command `msiexec /i EndpointToolbelt-<version>-x64.msi /qn`,
   uninstall command `msiexec /x {ProductCode} /qn`, install behavior **System**.
3. Detection: rule type **MSI** with the product code of this version (the release notes of the version name it), version check
   off.
4. Return codes: defaults.
5. New version: new Win32 app, supersedence on the previous app with
   *Uninstall previous version* = **No**; remove *Required* assignments from the old
   app so Intune does not try to reinstall it.
6. Configuration: pass MSI properties on the install command line, or set values
   under `HKLM\SOFTWARE\Policies\EndpointToolbelt` with an Intune remediation or platform
   script, Group Policy registry preferences or any registry tool (the product ships no
   ADMX template). Policy values win over install values; the service picks up a change
   within 30 seconds. Example:

   ```cmd
   msiexec /i EndpointToolbelt-<version>-x64.msi /qn SUPPORTTEXT="SoulBit Service Desk - ext. 5555" SUPPORTURL="https://support.soulbit.example" WUDEPLOYMENTDELAYDAYS=14
   ```

7. Set both `SupportUrl` and `SupportText` (recommended). Many results tell the user to
   contact IT support, and the app then says how: with `SupportUrl` it shows Contact IT on Home
   and on "My problem is not listed" and points to Contact IT on Home; with
   only `SupportText` it points to that text; with neither it says "Copy details for IT, then send it the
   way you normally reach IT support", which works but leaves the user to find the way.

## Logs and Intune *Collect diagnostics*

The engine and service logs are written to
`%ProgramData%\Microsoft\IntuneManagementExtension\Logs\` (`EndpointToolbelt.log`,
`EndpointToolbeltService.log`). Microsoft documents that the Intune *Collect
diagnostics* device action collects `%ProgramData%\Microsoft\IntuneManagementExtension\Logs\*.*`
([Device Action: Collect Diagnostics, Microsoft Learn](https://learn.microsoft.com/en-us/intune/device-management/actions/collect-diagnostics),
*Data collected* > *Files*, page updated 2026-09-28). Both log files are readable by SYSTEM and Administrators only. Both logs are therefore part of
a remote diagnostics collection. *Collect diagnostics* is available for
corporate-owned Windows devices and can be disabled under *Tenant administration* >
*Device diagnostics*.

Before writing there, the service checks that the folder is a real directory owned by
SYSTEM, Administrators or TrustedInstaller that nobody else can write to, and gives the
engine the folder it checked. The service opens its log file with the reparse-point and
hard-link checks; the engine refuses a log file that is a link, but checks it by path
only (no hard-link count) and relies on the folder check. If the folder check fails, both
logs go to the service's SYSTEM-only working folder instead and the service writes event
1015.

The same page lists the event logs that are collected (`Application`, `Setup`,
`System` and several Microsoft channels). The product's own event log (name
`Endpoint Toolbelt`, source `Endpoint Toolbelt`, stored as an `.evtx` file under
`%SystemRoot%\System32\winevt\Logs`) is **not** in that list, so its events are not part of *Collect
diagnostics*. Forward them with your own event collection if you need them centrally.

## Windows Update repair without a repair source

Endpoint Toolbelt does not pass a repair source to the component store repair
(`DISM /RestoreHealth`) and has no setting for one. DISM then uses the device's
component-repair policy if one is set (*Specify settings for optional component
installation and component repair*), otherwise Windows Update.

**A device without access to Windows Update (and without a policy-configured repair
source) cannot have its component store repaired and gets the finding
`WU-NO-REPAIR-SOURCE`** (failure codes `0x800f081f` and `0x800f0915`: the files are
missing; `0x800f0906`: their download failed, which points at the connection or the
repair source). The repair then continues with an update search and install. The
diagnosis reads the repair-source policy (`RepairContentServerSource`, `LocalSourcePath`)
and adds the Info finding `WU-REPAIR-SOURCE-UNSET` when a WSUS policy is set and no repair
source is configured.

**Policy codes are reported, never repaired.** `0x800f0907` (the component-repair policy
does not allow Windows Update as the repair source), `0x8024002e` (only the managed update
source is allowed) and `0x8024002f` (*Remove access to use all Windows Update features*)
give the finding `WU-POLICY-BLOCKS`, which names the policy. These codes alone start no
DISM or SFC repair, no cache reset and no install: the policy has to be changed in your
device management.

## What the Windows Update repair changes

- **Update cache:** only the contents of `SoftwareDistribution\Download` are emptied
  (with `wuauserv`, `bits` and Delivery Optimization (`DoSvc`) stopped and started again;
  none is stopped when one of them has other running dependents; up to three delete
  passes, then what is still in use is reported), on the delivery and space routes and
  after a failed update search. The servicing, unrecognised-code and `0x800719e4` routes
  never reset the cache. `SoftwareDistribution\DataStore` is emptied too only on evidence
  (a `0x80248xxx` code, or a search that still fails right after a Download-only reset);
  that loses the local update history. `catroot2` and Windows Installer are never
  touched. Nothing is reset while an update install runs (the Windows module installer,
  TiWorker, is running after a bounded wait, or the update client reports `IsBusy`; the
  Update Orchestrator has no documented read-only install state and is not asked), when
  the run has too little time left, or on a shared device (other users signed in or not
  countable, or a multi-session host: EditionID `ServerRdsh` or a `*MultiSession*` edition,
  or Windows Server with the RD Session Host role, recognised the same way as the service's
  package limit does); the finding `WU-CACHE-RESET-SKIPPED` then says
  what IT support can do.
- **Time:** the service stops the repair after 130 minutes; the engine plans each step
  (with its own limit) to finish with 10 minutes left for the result, and starts no step
  it cannot finish.
- **Ghost devices:** removed automatically only for the documented case behind
  `0x800719e4`: the CBS log shows the driver-operations-queue signature within the
  diagnosis window, and at least 50 non-present network adapters (class Net) on the PCI or
  VMBUS bus with the `netvsc` or Mellanox (`mlx*`) driver (typical of frequently
  deallocated Azure VMs). USB, Bluetooth, software and root-enumerated devices and the
  System class are never removed; other cases are reported with counts per bus
  (`WU-GHOST-DEVICES`). When every selected adapter is present again or already gone at
  removal time, nothing is changed and the warning `WU-GHOST-NONE-REMOVED` says so.
- **Restart:** after the component store repair, a restart is asked for only when DISM
  RestoreHealth exits with 3010 or SFC repaired files. It is also asked for after ghost
  devices or stale servicing packages were removed, and when an installed update needs
  one. The product never restarts the device itself.

`WUDeploymentDelayDays` must be at least your update ring's deferral + deadline +
grace period. Within that window a device that has not installed the newest
update yet is correctly reported as current (exit 69003).

## Signing

The release MSI and the executables and assemblies in it are **signed by SoulBit**, with a
timestamp (Artifact Signing, from 1.0.0); the release notes show how to check it with
`Get-AuthenticodeSignature`. A build you make yourself is unsigned unless you sign it (below).
The product installs no script file, so the PowerShell execution policy (including AllSigned)
does not apply to it.

Under **App Control for Business** or **AppLocker** with executable rules, Windows starts
only binaries the policy allows. When the policy refuses the engine executable, the service
reports `EngineNotSigned` (event 1013) with the text "A device policy (App Control, AppLocker
or a Microsoft Defender attack surface reduction rule) or security software blocked the repair
engine": it recognizes the refusal from the Windows error of the start
(`ERROR_INVALID_IMAGE_HASH` 577 for App Control, `ERROR_ACCESS_DISABLED_BY_POLICY` 1260 for
AppLocker; `ERROR_ACCESS_DENIED` 5 for attack surface reduction rules and security
software). Nothing fails silently. The three in-box cmdlets run through
`powershell.exe` with a constant `-Command` that also works in ConstrainedLanguage mode; a
policy that refuses Windows PowerShell for one of them is named in the action's result
(`ENGINE-POLICY-BLOCKED`, or the manual step in Settings for the Company Portal reset).

To allow the product under such a policy, either:

- add a rule for SoulBit's signature on the release (the release certificates are renewed
  daily and valid for 72 hours, so trust the signature, not one certificate's thumbprint; for
  App Control, Microsoft names the certificate profile's EKU as the identity that stays the
  same; read it from a signed file as below), or, for a build of your own, sign it with your
  own code-signing certificate and add a publisher rule for that, or
- deploy the MSI with Intune and make Intune a managed installer in the App Control policy.

The certificate profile's EKU is one of the signing certificate's enhanced key usages, with an
OID that starts with `1.3.6.1.4.1.311.97.` and continues with numbers unique to the profile. The
certificate has a second usage with that prefix, `1.3.6.1.4.1.311.97.1.0`: every Artifact Signing
public trust certificate has it, so it does not identify SoulBit. Use the longer one:

```powershell
(Get-AuthenticodeSignature 'C:\Program Files\SoulBit\Endpoint Toolbelt\EndpointToolbelt.exe').SignerCertificate.EnhancedKeyUsageList |
  Where-Object { $_.ObjectId -like '1.3.6.1.4.1.311.97.*' -and $_.ObjectId -ne '1.3.6.1.4.1.311.97.1.0' }
```

It stays the same for every release signed with the same certificate profile.

To sign with your own certificate:

1. Build with `.\build\build.ps1 -SignCertificateThumbprint <thumbprint> -TimestampUrl <url>`.
   This signs every executable and assembly the MSI installs (in a staged copy, never in
   the source tree) and then the MSI itself. Signatures are always timestamped.
2. Trust the certificate in the policy (publisher rule) and, for the signature to verify,
   deploy its chain to the devices (**Trusted Root Certification Authorities**, and the
   intermediates).

## Attack surface reduction rules

Microsoft Defender's attack surface reduction (ASR) rule **"Block executable files from running
unless they meet a prevalence, age, or trusted list criterion"**
(`01443614-cd74-433a-b99e-2ecdc07bfc25`) refuses the product in block mode: a new build has no
prevalence or age yet, and every build has new file hashes. Signing alone does not help, because
a certificate without reputation does not meet the criterion either.

The symptoms (the app does not start, the install rolls back with error 1920, Defender event 1121)
and how to allow the product without switching the rule off are in
[Endpoint Toolbelt and attack surface reduction rules](asr.md): a per-rule exclusion for the
install folder, which lasts across versions, or an Allow certificate indicator per release.
