# Changelog

All notable changes to this project are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [Semantic Versioning](https://semver.org/).

## [1.1.2] - 2026-11-26

### Added

- **Retry work apps and settings in For IT support:** the first tool of the group Apps, so it can
  be started at once instead of only from the app path. Its page says in one sentence how the work
  apps stood at the last check, for example "2 of 14 work apps need attention (Contoso Agent,
  Contoso VPN), read today 09:12.", with **Show the apps** to the full list and Back to this page.
  It cannot be switched off, and it is off without the app installer (IME), as on the app path. A
  Retry started before the app read the work apps (from here, Home or a fix link) now reads them
  first, so it is checked afterwards. Retry runs count on the Apps tile.
- **A newer version is said:** once a day the app reads one small file from the publisher's
  website to see whether a newer version of Endpoint Toolbelt exists. If so, once per version a
  small notice at start-up says it, with a link to the release notes on the product's page, and
  About names it. The app never downloads or installs anything itself. The request carries no data
  about the device or the user; the site sees the internet address and the time, as every website
  does. "Check for new versions at start-up" switches the look off and on: in About for one user,
  and for the device on the installer's first screen (checked by default; on a first install and on
  an upgrade of a device where nobody chose yet) or with the MSI property `UPDATECHECK=0` on a
  silent install; the policy value `UpdateCheck` = 0 switches it off for everyone. Off means no
  request at all. **Check now** in About looks at once and says what it found, with a link to the
  Release notes when a newer version exists.
- **Export report as HTML** (For IT support): saves the device report as one web page in the
  Downloads folder and opens it. It holds the device facts, every health check, every work app
  with what the device recorded, its next step and what IT support needs to find it in Intune,
  and the newest 20 actions. The page is a single file without script that loads nothing from
  elsewhere, and drops the invisible characters that can make a name read differently.
- **Check Defender blocks** (For IT support, Defender & security): reads what Microsoft Defender
  logged in the last 14 days, without changing anything: threat detections, attack surface
  reduction rules, controlled folder access (an app kept out of a protected folder) and network
  protection (a blocked connection), each blocked or only audited. One card per thing that was
  stopped, such as "c.exe was blocked 4 times ... Started by explorer.exe", with a count, the
  newest time and what IT support needs for an exclusion, an allowed app or an indicator. A
  destination is shown by its host name only, and a detection says what Defender did with it
  (or that its action failed, as an error for IT support). A result that found nothing says that SmartScreen in
  Microsoft Edge and the firewall are not part of the check. An empty log is a warning, and a
  Microsoft Defender Antivirus in passive mode or switched off is named instead of "nothing found". The user sees the device's
  events and their own; other users' events and events without a user are counted only, and a
  path in another profile is not shown in any of its forms. A new read-only service action,
  `CheckDefenderBlocks` (13), which `DisabledTools` can switch off.
- **Install screens:** with Bit, the app's helper, an install started by hand shows its progress and then "Endpoint Toolbelt is
  installed." with Finish (the app is opened from the Start menu); the same package again says it is already installed,
  and a failed install points to the install log. An administrative install (`msiexec /a`) says
  the package is unpacked. A silent install (`/qn`, Intune) shows no screen.

### Changed

- **Check Defender blocks: a short status.** With blocks or detections the status reads "Microsoft
  Defender blocked or detected something in the last 14 days. See the details."; the counts are in
  the details. For IT support no longer shows the line "Copy details for IT, then ..." under a
  status, so the page fits the window without scrolling (Copy details for IT is in the details).
- **A check's answer shows at once.** Check Defender blocks, Check Windows Update, Check Microsoft
  connections and Check compliance settings say their answer in a sentence below the result when
  they end, also when all is well, instead of only "completed"; **View details** shows the cards.
  The details have their own **✕**, which closes them and not the app.
- **Tool rows and tiles** show a result of the last 24 hours only; an older run, which may no
  longer be true, shows nothing there (Recent runs still lists it), and a tool that never ran no
  longer says "Not run yet". A run that found nothing to repair reads OK.
- **Check Defender blocks: health facts.** With Microsoft Defender Antivirus active, the result
  also says when the last quick and full scan ended, whether real-time protection, the antivirus
  or the antispyware was switched off in the last 14 days (and whether it is on now), a change
  tamper protection blocked, a real-time protection feature that failed, and a failed update (a
  warning until a later update succeeded). A good result reads, for example, "Last quick scan
  today 03:34. Protection was not switched off in the last 14 days." A controlled folder access
  card names the protected folder, as Defender logs it, instead of calling it a file.
- **Export report as HTML** runs the read-only checks first (the app diagnosis, Check Windows
  Update, Check Defender blocks, Check Microsoft connections, Check compliance settings), so the
  page always holds results of this moment, each with its time. When the service does not run one
  (its hourly limit of read-only checks, another action), the page shows the newest stored result
  and says so; the export is never refused. The page starts with a table of contents and a
  summary that link to each section; the work apps that need attention come first and the rest
  is folded.
- **For IT support:** six groups instead of four, so every group fits the window with a run's
  status shown and has room for new tools: Apps, Sign-in, Microsoft 365 (Clear Teams cache and the
  four Office safe modes), Windows (Sync clock, Check and Repair Windows Update, Bring my window
  back), Defender & security (Update Defender definitions, Check Defender blocks, Back up recovery
  key, Check compliance settings), and Network & storage. Each group's tile has an icon in a colour
  of its own (never the green, amber or red of a result; the system's colours in high contrast).
  The three report buttons read **View report**, **Export HTML** and **Support package**, side by
  side in one row; the page lists the three most recent runs, and All runs opens the rest.
- **Upgrade with the app open:** the setup says Endpoint Toolbelt is open and offers **Close it
  and continue**, instead of Windows' Retry, Ignore and Exit.
- **Work apps:** when no app needs attention, the page says so and lists the apps at once, instead
  of an empty list with only "Show all".
- **Configuration profiles:** a failed command on an Office installation that succeeded on the
  device (the MDM client sends it again) is no longer a failed setting; the report counts it apart.
  A real Office installation failure reads "Installing Office (Microsoft 365 Apps)".
- **Repair Windows Update:** follows Microsoft's "Common Windows Update errors" page for more
  codes. Five component store codes (`0x800F0831`, `0x80073701`, `0x8007371B`, `0x80070570`,
  `0x800F0825`) take the system file repair; six network codes (`0x80072EFD`, `0x80072EFE`,
  `0x80D02002`, `0x8024401B`, `0x80072F8F`, `0x80200053`) take the download route and no longer
  start DISM and SFC as unknown codes did. Twelve codes the app did not explain now have plain
  words and a next step, and `0x800F0922` names the documented licensing token fix for IT
  support next to the EFI system partition.

### Fixed

- **Apps offered in Company Portal no longer wait for evidence after a Retry:** Retry work
  apps and settings recorded a step for every failed app, also for apps assigned as available. The
  app installer (IME) never starts their next attempt (only Install or Retry in Company Portal
  does), so they read "Awaiting evidence" until the step was a week old. Retry now leaves them out
  (an app also required for the device or the user stays in), and a step recorded for them is
  removed at the next check: they show their own finding and next step again. When none of the
  apps is one Retry retries, its status says so instead of "the IME retries them".
- **The status after a post-check of the apps** no longer reads OK while the same app diagnosis
  found other apps that need attention: after a Retry work apps and settings, or a step for one app,
  it says that the checked app is detected and how many other apps need attention, as a warning.
  Recent runs already said Problem for such a run.
- **Export HTML:** when the app diagnosis could not run again for the report (another action, the
  hourly limit, no service), the summary no longer shows the apps as a fresh green result: it says
  from when the reading is and that it was not read again.

## [1.0.1] - 2026-11-18

Fixes from an external review of 1.0.0, and links in Programs and Features.

### Added

- **Programs and Features links:** the MSI sets `ARPURLINFOABOUT` (`https://soulbit.app/go/endpoint-toolbelt`)
  and `ARPHELPLINK` (`https://github.com/soulbit-nl/endpoint-toolbelt`), the About dialog's two
  links, so Windows and packaging tools can find the documentation.

### Changed

- **README:** the introduction names the handover to IT support with the evidence, instead of a
  sign-in example.

### Fixed

- **Device health:** when Windows servicing's restart state cannot be read, the restart row is a
  warning, "Restart status unknown", instead of "No restart pending". A restart this app's own
  repair asked for still shows as such. The unknown state is not shown as a restart that waits.
- **Retry work apps and settings:** while other people are signed in, the IME restart and the
  timer clear are skipped. The run's log now says so instead of "The IME restarted", and the
  status says "Sync requested" instead of "Retry requested". The exit code stays 0.
- **Docs:** the admin guide asks for evidence before an attack surface reduction rule is treated as
  the cause of error 1920. Service Control Manager event 7000 with "Access is denied" is the
  symptom; Defender event 1121 confirms the rule; without it, read the MSI log first. The deployment
  status says the MSI was installed through Intune; supersedence is not tried yet.

## [1.0.0] - 2026-11-18

The first public release.

### Added

- **Home:** one verdict in plain words, the device health as a count with Show all (the ten checks
  on a view of their own), the most common problems as tiles, and "Need a person?" with Create
  support package, Copy details for IT and Contact IT.
- **Device health view** (read-only, as the signed-in user): ten checks in plain words: the link
  to the organization (Intune connection), the work app installer (IME), the work account, Windows
  Hello, the connection to work services, the work settings (configuration profiles), a pending
  restart, free disk space, the clock and Microsoft Defender.
- **Problem paths:** "App won't install", "Keeps asking to sign in", "Can't connect", "An Office
  app closes", "OneDrive doesn't sync", "My device is blocked or 'not compliant'", "Windows Update
  fails" and "A window is off the screen": each checks first, changes nothing until the user
  chooses a step, asks "Does it work now?" after a repair, and gives Copy details for IT with the
  whole case. "My problem is not listed" hands over to IT support.
- **Apps on this device:** per app what the device recorded, how far its newest attempt got (with
  the installer's exit code in plain words) and one next step; a search over every app; one row
  per app assigned to both the device and the user. After a step the app is "awaiting evidence"
  until a new record shows it detected. A record Intune no longer assigns reads "No longer
  assigned to this device" or "to you", only on proof from the Intune Management Extension's own
  check-ins; nothing is changed or removed.
- **Repair actions** run by the service as SYSTEM: Retry work apps and settings, Clear downloaded app
  content, Full IME reset, Repair Windows Update, Back up recovery key, Sync clock, Update
  Defender definitions, Free up disk space and Get a new IP address; read-only Check Windows
  Update and App diagnosis; Create support package, saved as `Toolbelt_yyyyMMdd.zip` in Downloads
  with an index of what it took and left out.
- **User-context tools:** Check Microsoft connections, Reset Company Portal data, refresh or
  reset the work account sign-in, Clear Teams cache, Office apps in safe mode, Reset OneDrive,
  Check compliance settings and Bring my window back (windows off the screen listed first).
- **For IT support:** every tool in four groups with a search, the device report, the run
  history and the technical log of each run.
- **Configuration** through the MSI or Intune and Group Policy (`HKLM\SOFTWARE\Policies\EndpointToolbelt`):
  cooldowns, rate limits, the support link and text, the multi-session gate, a trusted service
  caller, and `DisabledTools` to switch single tools off.
- **Security:** a standard user can only request a fixed set of actions; the service enforces
  confirmations, cooldowns, rate limits and the multi-session gate, never follows links, and
  logs to the event log. Every regular expression has a time limit. The engine is an executable and the product installs no script file,
  so it runs under App Control for Business with script enforcement.
- **Installer:** a per-machine x64 MSI (WiX 5, core elements only) with upgrade, repair,
  downgrade refusal and a clean uninstall. The MSI and the executables and assemblies in it are
  signed by SoulBit, with a timestamp.
- **Guides:** a user guide, an IT support guide and an admin guide.

### Known limitations

- English only, x64 only, no ADMX template.
- On a virtual machine that does not run on Microsoft Hyper-V, the Windows Hello and clock checks
  can show a warning that does not apply to a virtual desktop.
- The attack surface reduction rule for prevalence and age can block a new release on its first
  days; see [docs/asr.md](docs/asr.md).

[1.0.1]: https://github.com/soulbit-nl/endpoint-toolbelt/releases/tag/v1.0.1
[1.0.0]: https://github.com/soulbit-nl/endpoint-toolbelt/releases/tag/v1.0.0
