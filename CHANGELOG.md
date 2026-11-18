# Changelog

All notable changes to this project are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [Semantic Versioning](https://semver.org/).

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
