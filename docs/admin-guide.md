# Admin guide

For administrators who deploy Endpoint Toolbelt and set it up for their organization. How
employees use it is in the [user guide](user-guide.md); how IT support reads what users send is
in the [IT support guide](it-support-guide.md). The details of the MSI, the logs and the Windows
Update repair are in [deployment.md](deployment.md).

- [Before a broad rollout](#before-a-broad-rollout)
- [Requirements](#requirements)
- [Install and uninstall](#install-and-uninstall)
- [Deploy with Microsoft Intune](#deploy-with-microsoft-intune)
- [Configuration](#configuration)
- [Security products: attack surface reduction rules and App Control](#security-products-attack-surface-reduction-rules-and-app-control)
- [Logs and what the Toolbelt records](#logs-and-what-the-toolbelt-records)
- [Checklist for a first rollout](#checklist-for-a-first-rollout)

## Before a broad rollout

Endpoint Toolbelt is a local support tool for Intune-managed Windows devices, with offline
diagnosis, targeted repairs and a handover to IT. Plainly:

- **No central view.** IT sees only what the user hands over: the Copy details for IT text or the support
  package in Downloads. A central view of devices and runs is Toolbelt Connect's, a separate product.
- **Not repaired here:** a broken Intune enrollment, an Office repair or the new Outlook's cache.
  The app never restarts the device; it only says when a restart is waiting.
- **Conditional Access:** a local tool cannot tell why a sign-in was blocked; that needs the
  tenant's sign-in logs.
- **Not all confirmed on real devices yet:** the IME
  log patterns, every route of Repair Windows Update, hybrid-joined devices, non-English Windows
  and a proxy-only network.
- **English only.** The window and its texts are in English; there is no other language.
- **x64 and Intune-enrolled devices only**; there is no ADMX template.

### Without a connection

| What | Without a connection |
|---|---|
| Reading local state, existing logs and the recorded app state; the device report | Mostly works |
| Bring my window back, start an Office app in safe mode | Works, with locally installed apps |
| Clearing caches, Reset OneDrive | The local step works; signing in or syncing again needs a connection |
| Intune sync, downloading app content, backing up the recovery key to Microsoft Entra ID | Needs the service to be reachable |
| Windows Update, Defender definitions, Sync clock | Needs a reachable source, sometimes one inside the organization |

The app has no telemetry and no back end. Its network requests are the connection checks and, at
most once a day, the check for a newer version: one GET of a static file,
`https://soulbit.app/endpoint-toolbelt/latest.json`, made by the app as the signed-in user. It carries no
data about the device or the user; the site sees what every web server sees (the device's public
internet address, the time, and "Endpoint Toolbelt" as the user agent). Through an authenticating
proxy, the user's Windows sign-in goes to that proxy only, as for the user's other apps. Off means
no request by itself; **Check now** in About is the user's own click and makes one request also then.
It is decided in this order: the policy value `UpdateCheck` = 0 switches it off for every user,
cannot be changed in the app and hides Check now, so no request is made at all (deploy it before the
version with the check if you do not want the request at all); otherwise the user's own choice ("Check for new versions at
start-up" in About); otherwise the device's value from the installer (the same check box on the
first screen of an install a person starts, or `UPDATECHECK=0` on a silent install); otherwise on. The app never downloads or installs a new
version: it only says one is available, and you deploy it.

## Requirements

- Windows 10 22H2 or Windows 11, x64, enrolled in Microsoft Intune
- .NET Framework 4.8 (in-box on Windows 10 22H2 and Windows 11)
- Windows PowerShell 5.1 (in-box), only for three in-box cmdlets the product calls; the product installs no script file
- Installation with administrator or SYSTEM rights (per-machine MSI)

Azure Virtual Desktop multi-session hosts are supported. Actions that affect every
session ask for an explicit acknowledgement, and organizations can block them. The
Windows Update download cache is never cleared on a shared device.

## Install and uninstall

```cmd
:: Install (silent)
msiexec /i EndpointToolbelt-<version>-x64.msi /qn /l*v "%TEMP%\EndpointToolbelt-install.log"

:: Install with configuration
msiexec /i EndpointToolbelt-<version>-x64.msi /qn SUPPORTTEXT="SoulBit Service Desk - ext. 5555" SUPPORTURL="https://support.soulbit.example"

:: Uninstall (silent)
msiexec /x {ProductCode} /qn
```

Started by hand (a double-click), an install shows "Endpoint Toolbelt is ready to install." with
the check box "Check for new versions at start-up" (checked) and Install while nobody chose for the
device yet: on a first install, and on an upgrade of a device without that choice. Install writes
the choice for the device (`UPDATECHECK` 1 or 0, and a mark in
`HKLM\SOFTWARE\EndpointToolbelt\Setup` that every user may read, so the screen does not ask again).
Then come its progress and "Endpoint Toolbelt is installed." with Finish; an upgrade of a device
that has the choice starts at the progress. `/qn` shows nothing, as Intune runs it. The setup
never starts the app itself (from an elevated prompt the app would then run as that
administrator): the user opens it from the Start menu. An administrative
install (`msiexec /a`) only unpacks the package into a folder and ends on "Endpoint Toolbelt is
unpacked."

These are `cmd` command lines. In PowerShell, quote the product code (`'{ProductCode}'`)
and use `$env:TEMP` instead of `%TEMP%`.

The installer creates the app and the service in `C:\Program Files\SoulBit\Endpoint Toolbelt`
and a Start Menu shortcut "Endpoint Toolbelt".

**The app does not start, or the install fails with error 1920?** Error 1920 only says that the
service did not start. Confirm the cause before you allow anything. Service Control Manager event
7000 for Endpoint Toolbelt Service with "Access is denied" in the System log is the symptom of a
block; Microsoft Defender event 1121 for the Toolbelt confirms that an attack surface reduction
rule caused it. Without event 1121, read the MSI log first (`/l*v`, above). With it: on devices
with Microsoft Defender's attack surface reduction rules, the rule "Block executable files from
running unless they meet a prevalence, age, or trusted list criterion" can block a new release on
its first days.
[asr.md](asr.md) explains how to allow the Toolbelt without switching the
rule off.

Uninstall removes the program, the service, the service's working folder, the
`Config` and `State` keys and the registry root `HKLM\SOFTWARE\EndpointToolbelt`, the
event source and event log registration, and the shortcut. It leaves:

- each user's own data in `%LOCALAPPDATA%\EndpointToolbelt`;
- the product's logs in the IME Logs folder
  (`%ProgramData%\Microsoft\IntuneManagementExtension\Logs\EndpointToolbelt*.log`);
- the event log file (`.evtx`) under `%SystemRoot%\System32\winevt\Logs`;
- any policy values an administrator set under `HKLM\SOFTWARE\Policies\EndpointToolbelt`.

## Deploy with Microsoft Intune

Deploy the MSI as a **Windows app (Win32)**:

1. Wrap the MSI with the Microsoft Win32 Content Prep Tool (`IntuneWinAppUtil.exe`).
2. Install command: `msiexec /i EndpointToolbelt-<version>-x64.msi /qn`
   Uninstall command: `msiexec /x {ProductCode} /qn`
3. Install behavior: **System**.
4. Return codes: keep the defaults.
5. Detection rule: **MSI**, with the product code of this version. Leave the version
   check off. Each version has a new product code. It is in the release notes of the
   version on GitHub. On a device where the version is installed, PowerShell shows it
   (`PSChildName` is the product code):

   ```powershell
   Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' |
       Where-Object DisplayName -eq 'Endpoint Toolbelt' | Select-Object PSChildName, DisplayVersion
   ```

6. For a new version, create a new Win32 app and set **supersedence** on the old one
   with *Uninstall previous version* = **No**. The MSI upgrades in place. Remove
   *Required* assignments from the old app.

Details: [deployment.md](deployment.md).

## Configuration

Settings are read only by the service (64-bit registry view). Precedence:
`HKLM\SOFTWARE\Policies\EndpointToolbelt` > `HKLM\SOFTWARE\EndpointToolbelt\Config` >
built-in default.

- **Install command line:** pass the MSI properties below; the installer writes them to the
  `Config` key, and values set at install time survive upgrades. A repair never changes them, and an empty value on the command line does not remove a stored one.
- **Policy key:** write the values under `HKLM\SOFTWARE\Policies\EndpointToolbelt` with
  Group Policy (registry preferences), an Intune remediation or platform script, or any
  registry tool. The product never writes this key and ships no ADMX template.

The service re-reads the configuration at most every 30 seconds, so a change applies to the
next action without a restart.

| Value | MSI property | Type and valid range | Default | Meaning |
|---|---|---|---|---|
| `FullResetCooldownHours` | `FULLRESETCOOLDOWNHOURS` | number, 0-168 | 1 | Minimum hours between two full IME resets; 0 switches the cooldown off |
| `WURepairCooldownHours` | `WUREPAIRCOOLDOWNHOURS` | number, 0-720 | 4 | Minimum hours between two Windows Update repairs that changed the device; 0 switches the cooldown off |
| `WUDeploymentDelayDays` | `WUDEPLOYMENTDELAYDAYS` | number, 1-60 | 10 | Days after Patch Tuesday before a month's update counts as expected. Set it to at least your ring deferral + deadline + grace period |
| `WUMinFreeGB` | `WUMINFREEGB` | number, 1-500 | 15 | Free space below which the update repair cleans up the component store first |
| `WURemoveStalePackages` | `WUREMOVESTALEPACKAGES` | number, 0 or 1 | 0 | 1 allows removal of stale servicing packages (destructive, off by default) |
| `SupportText` | `SUPPORTTEXT` | text, at most 200 characters, no control characters | empty | Text on Home under "Need a person?" and on "My problem is not listed", e.g. `SoulBit Service Desk - ext. 5555`. Recommended with `SupportUrl`: a result that sends the user to IT support points to it |
| `SupportUrl` | `SUPPORTURL` | absolute `https://` URL, at most 500 characters | empty | Contact IT on Home and on "My problem is not listed", e.g. `https://support.soulbit.example`. Recommended: without it the app can only say "send it the way you normally reach IT support" |
| `UserRateLimitPerHour` | `USERRATELIMIT` | number, 1-60 | 20 | Maximum actions per user per hour that change the device; an action counts once the service accepts it. Creating a support package, Check Windows Update and the app diagnosis do not count. Cannot be switched off |
| `AllowMachineWideOnMultiSession` | `ALLOWMULTISESSIONRESET` | number, 0 or 1 | 1 | 0 blocks Full IME reset, Repair Windows Update, Free up disk space and Get a new IP address while other users are signed in (they are refused anyway when the sessions cannot be counted) |
| `TrustedServiceCaller` | none (policy key only) | a Windows service name: letters, digits and `_.-`, at most 80 characters | empty | Names one service of the organization that may call the service on IT support's behalf over its own pipe, with the same actions, confirmations and limits as a user. Off when empty. See [Trusted service caller](architecture.md#trusted-service-caller) |
| `DisabledTools` | none (policy key only) | `REG_MULTI_SZ` of tool names (a `REG_SZ` with names separated by commas or semicolons is read too), case-insensitive | empty | Switches single tools of For IT support off for the signed-in user. The app hides them (group lists, tile counts, search, problem paths, Home's step, fix links) and the service refuses a service tool to a user with `ToolSwitchedOff` (24) and event 1018; the trusted service caller may still run it. Service tools by their action names: `ClearAppCache`, `FullReset`, `UpdateDefender`, `CheckDefenderBlocks`, `BackupRecoveryKey`, `SyncClock`, `CheckWindowsUpdate`, `RepairWU`, `FreeDiskSpace`, `RenewIpAddress`. App tools: `OpenImeLogs`, `ResetCompanyPortal`, `RefreshSignIn`, `ResetSignInCache`, `ClearTeamsCache`, `WordSafeMode`, `ExcelSafeMode`, `OutlookSafeMode`, `PowerPointSafeMode`, `CheckConnections`, `BringWindowBack`, `CheckCompliance`, `ResetOneDrive`; these run as the user, so the policy hides them but cannot stop a user from doing the same by hand. An unknown name is ignored and logged once. Retry work apps and settings, the support package and the app diagnosis cannot be switched off; Retry stays in For IT's group Apps too. |
| `UpdateCheck` | `UPDATECHECK` | `REG_DWORD` (or `REG_SZ`), 0 or 1 | 1 (on) | `0` switches off the check for a newer version: the app then does not read `https://soulbit.app/endpoint-toolbelt/latest.json` by itself, shows no notice at start-up and names no newer version in About; only About's Check now, the user's own click, reads it once. As a policy value it locks the check off for every user and hides About's check box and Check now, so no request is made at all. As the installer's value (`UPDATECHECK=0`, kept on an upgrade; also written when the check box on the first screen of an install a person starts is cleared) it is the device's default: a user's own choice in About comes before it. With the check on, users see once per new version a notice with one link, "Release notes", to the product's page on soulbit.app. Any other value, or no value, leaves it on (at most once a day, as the signed-in user). The app never downloads or installs a version itself. |

- Numbers may be `REG_DWORD` or `REG_SZ` (for example `"8"`); text values are `REG_SZ`.
- An empty value counts as not set: the built-in default applies.
- Support packages do not count toward `UserRateLimitPerHour`. On a shared device (a
  multi-session host, or while other users are signed in or cannot be counted) each user can
  create at most 4 per hour; this limit is fixed. On a single-user device there is none.
- An invalid value (wrong type, out of range, too long, not an https URL) is ignored and
  logged in the service log, and the built-in default applies. An invalid policy value
  does not fall back to the `Config` value; it gives the default.


## Security products: attack surface reduction rules and App Control

The MSI, the three executables and the two assemblies are signed by SoulBit with a timestamp.
Check a file with:

```powershell
Get-AuthenticodeSignature 'C:\Program Files\SoulBit\Endpoint Toolbelt\EndpointToolbelt.exe' |
    Format-List Status, SignerCertificate, TimeStamperCertificate
```

- **Attack surface reduction rules.** The rule "Block executable files from running unless they
  meet a prevalence, age, or trusted list criterion" can block a new release on its first days
  (Defender event 1121). [asr.md](asr.md) shows how to allow the Toolbelt without switching the
  rule off.
- **App Control for Business.** The product installs no script file and its engine is an
  executable, so it runs with script enforcement on. Allow it by its publisher; organizations
  that build it themselves sign it with their own certificate ([deployment.md](deployment.md#signing)).

## Logs and what the Toolbelt records

| What | Where | Kept |
|---|---|---|
| The service's and the engine's logs | `%ProgramData%\Microsoft\IntuneManagementExtension\Logs\EndpointToolbelt*.log`, so Intune's *Collect diagnostics* takes them | see [deployment.md](deployment.md#logs-and-intune-collect-diagnostics) |
| Events | the event log `Endpoint Toolbelt` (not part of *Collect diagnostics*) | by the event log's size |
| Each user's run history | `%LOCALAPPDATA%\EndpointToolbelt\runs` | the newest 50, at most 20 MB |
| A failed or warned run, for support | `EndpointToolbelt_<Action>_<run ID>.log` in the user's Downloads folder | until the user deletes it |
| Support packages | `Toolbelt_yyyyMMdd.zip` in the user's Downloads folder (`-1`, `-2` for a second and third that day) | until the user deletes it |

The run logs, the history and the copied texts never hold a password, a token, a recovery key,
an IP address, a Wi-Fi name, a window title, a document name or another user's apps. A support
package also holds Windows' own MDM diagnostics and the IME logs as they are on the device; what
it holds is in the [IT support guide](it-support-guide.md). The details of the log files are in
[deployment.md](deployment.md#logs-and-intune-collect-diagnostics).

## Checklist for a first rollout

1. Set `SupportUrl` (and `SupportText`) so every "contact IT support" points somewhere.
2. Set `WUDeploymentDelayDays` to at least your update ring's deferral plus deadline plus grace
   period, or the Windows Update check reports a month as late too early.
3. On Azure Virtual Desktop multi-session hosts, decide on `AllowMachineWideOnMultiSession`.
4. Switch off with `DisabledTools` any tool your support process does not want users to run.
5. Deploy the Win32 app as Available to a pilot group first, as System, with the MSI detection
   rule.
6. Check that attack surface reduction rules and App Control allow it on a pilot device
   ([asr.md](asr.md)).
7. Tell IT support where tickets with "Copy details for IT" text and support packages arrive, and
   point them to the [IT support guide](it-support-guide.md).
