# Architecture

This document describes the design of Endpoint Toolbelt 1.0.

## Components

```
 User session (standard user)             Session 0 (LocalSystem)
+--------------------------------+       +------------------------------------+
| EndpointToolbelt.exe (WPF)     |       | EndpointToolbeltService            |
|  - health view (user context)  | pipe  |  - pipe server, caller identity    |
|  - actions, results, history   |<----->|  - action whitelist (10 actions)   |
|  - support package > Downloads |       |  - confirmations, rate limits,     |
+--------------------------------+       |    multi-session gate              |
                                         |  - one worker, explicit Busy       |
  pipe = \\.\pipe\EndpointToolbelt       |  - engine runner (Job Object)      |
                                         +-----------------+------------------+
                                                           | stdout: JSON lines
                                                           v
                                         +------------------------------------+
                                         | EndpointToolbelt.Engine.exe  |
                                         |  one handler per action            |
                                         |  in-box cmdlet runner (3 commands) |
                                         |  cooldowns, restart marker         |
                                         +------------------------------------+
```

| Component | Technology | Runs as |
|---|---|---|
| `EndpointToolbelt.exe` | WPF on .NET Framework 4.8 | the signed-in user |
| `EndpointToolbelt.Service.exe` (`EndpointToolbeltService`) | .NET Framework 4.8 Windows service, Automatic (Delayed Start) | LocalSystem |
| `EndpointToolbelt.Contracts.dll` | shared protocol types, action enum, result and exit codes | both |
| `EndpointToolbelt.Platform.dll` | code the service, the engine and the app share: CMTrace writer, link-safe file API, registry keys, IME log pattern table, process runner, in-box cmdlet runner | service, engine, app |
| `EndpointToolbelt.Engine.exe` | the engine, .NET Framework 4.8; started by the service for every action, and by itself in worker mode for the Windows Update API (see [Compiled engine](#compiled-engine)) | LocalSystem, child process of the service |
| `Engine\ImeLogPatterns.json` | the IME log pattern table the service and the engine read | data |
| MSI | WiX Toolset 5 (core elements only); installs no script file | - |

.NET Framework 4.8 and Windows PowerShell 5.1 are in-box on Windows 10 22H2 and
Windows 11, so the product deploys no runtime. Windows PowerShell runs only three in-box
cmdlets, through the in-box cmdlet runner.

## Actions

| Action enum | Button | Cooldown | Gates | Time limit | Sync failed or not started |
|---|---|---|---|---|---|
| `RetryAppsAndPolicies` | Retry work apps and settings | none | IME restart and retry-timer clear skipped while other users are signed in (or cannot be counted) | 15 min | exit 1, on purpose: the sync is part of what this action is for |
| `ClearAppCache` | Clear downloaded app content | none | confirmation; folders with open files are skipped | 15 min | no sync |
| `FullReset` | Full IME reset | `FullResetCooldownHours` (default 1; 0 = off), stamped before the wipe and put back when nothing was wiped, so a run stopped halfway keeps it | confirmation; other-sessions acknowledgement; `AllowMachineWideOnMultiSession` | 20 min | exit 2, on purpose: the purpose is the wipe, the sync only its follow-up |
| `RepairWU` | Repair Windows Update | `WURepairCooldownHours` (default 4; 0 = off), checked after diagnosis, stamped before the repair and put back when the device was not changed, so a run stopped halfway keeps it | confirmation; other-sessions acknowledgement; `AllowMachineWideOnMultiSession`; pending restart (69002); device current (69003) | 130 min | no sync |
| `CollectDiag` | Create support package | none | 4 per user per hour on a shared device; not counted toward `UserRateLimitPerHour` | 30 min | no sync |
| `BackupRecoveryKey` | Back up recovery key | none (it changes nothing on the volume) | none | 10 min | no sync |
| `SyncClock` | Sync clock | none | none | 10 min | no sync |
| `UpdateDefender` | Update Defender definitions | none (Defender updates itself anyway) | none | 25 min | no sync |
| `FreeDiskSpace` | Free up disk space | none (both steps are idempotent; a repeat finishes fast) | confirmation; other-sessions acknowledgement; `AllowMachineWideOnMultiSession` | 60 min | no sync |
| `DiagnoseApps` | Check my apps or Review affected apps / An app won't install | none (read-only) | 30 per caller per hour, apart from `UserRateLimitPerHour` | 5 min | no sync: it changes nothing |
| `RenewIpAddress` | Get a new IP address (the adapter the user chose, by its interface index) | none | confirmation; other-sessions acknowledgement; `AllowMachineWideOnMultiSession`; a physical adapter under that index; a fixed address is not renewed (69003) | 5 min | no sync |
| `CheckWindowsUpdate` | Check Windows Update (Windows won't update and For IT) | none (read-only) | shares the read-only limit of `DiagnoseApps`: 30 per caller per hour together, apart from `UserRateLimitPerHour` | 5 min | no sync: it changes nothing |

All twelve are available to every interactive user. The service enforces the same
confirmations the app asks for, so a request without them is refused; the flags are
asserted by the client, though (see [security.md](security.md)). The time limit is the
service's hard limit; the engine plans its steps to end 10 minutes before it.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | failed |
| 2 | completed with warnings (includes a queued or unverified sync) |
| 87 | invalid or unknown action; nothing ran |
| 69001 | cooldown active; nothing changed |
| 69002 | blocked by a pending restart |
| 69003 | nothing to repair; the device is current |

Service-level result codes:
- no engine run: `ServiceUnavailable`, `ProtocolMismatch`, `ServerIdentityMismatch`,
  `InvalidRequest`, `Busy`, `ConfirmationRequired`, `RateLimited`, `MultiSessionBlocked`,
  `EngineDidNotStart`, `NoResult` (no stored result), and `EngineNotSigned` when App Control,
  AppLocker, a Microsoft Defender attack surface reduction rule or other security software
  refused to start the engine executable;
- an engine run that ended without an exit code of its own: `EngineTimeout` (time
  limit), `EngineCrashed` (unexpected service error) and `Interrupted` (the service stopped
  during the run, for a restart or an upgrade, or a run was found still in progress when
  the service started). These runs keep the records they produced and appear in the history.

## Request flow

1. The app connects to `\\.\pipe\EndpointToolbelt` at Identification level and
   verifies that the pipe server process is `EndpointToolbeltService` in session 0
   (`GetNamedPipeServerProcessId` against `QueryServiceStatusEx`). If the service is
   stopped, the app starts it (standard users have `SERVICE_START` on this service).
2. The app sends one request per connection, framed as a 4-byte length plus UTF-8
   JSON (at most 64 KB): protocol version, request type, action enum, `confirmed`,
   and `confirmedOtherSessions` (the session count the service reported).
3. After reading the request, the service identifies the caller (`RunAsClient`, SID;
   `GetNamedPipeClientSessionId`), applies the confirmation and multi-session rules,
   the single-worker rule and the per-user rate limit (kept in `Limits\` in the working folder,
   so a service restart does not reset it), and replies `Accepted` or a
   result code. Cooldowns are not checked here: the engine owns the single cooldown
   mechanism, because some cooldowns are decided only after a read-only diagnosis.
   Creating a support package, Check Windows Update and the app diagnosis do not count toward
   `UserRateLimitPerHour`. On a shared device (a multi-session host, or other user sessions
   present or uncountable) support packages have their own limit of 4 per user per hour, and on a
   single-user device none.
4. The service writes a SYSTEM-only request file (`Work\<runId>\request.json`) with
   the action, run ID, product version, output folder, the log folder it checked, the
   caller's session ID and SID, the number of other sessions, the time limit and a
   configuration snapshot, then starts the engine:
   `"<install>\EndpointToolbelt.Engine.exe" -Action <enum> -RequestFile <path>`
   suspended, assigns it to a Job Object (kill on close), then resumes it. The
   environment is rebuilt from a fixed list of variables, with PATH holding System32 and
   Wbem only.
5. The engine writes JSON lines to stdout: `progress`, `finding`, `change`, `summary`,
   `mdm-result`, `mdm-queued`, `restart-required`, `apps-state`, `remediations-state`,
   `app-diagnosis` and `apps-diagnosis` (the read-only app diagnosis),
   `warning`, and `file` (name of a package in its output folder). The service forwards
   them to the requesting connection only.
   **Engine output contract:** the engine writes UTF-8 (without BOM, ASCII in practice:
   other characters are escaped) to standard output; every record is one line, at most 64 KB, a
   JSON object with `"type"`, `"run"` (the run ID from the request file) and `"ts"` (the
   UTC moment the record was written, `yyyy-MM-ddTHH:mm:ss.fffZ`; the app shows it in
   the user's own time zone). A `finding` carries `severity`, `id`, `title`, `detail`,
   `next` (what the user can do) and `support` (what only IT support can do; empty when
   there is nothing). Lines that are not such objects, or carry another run ID, are
   ignored. Only the first `file` record counts. Temporary files go to the run's own
   `temp` folder (`TEMP`/`TMP`).
6. A produced package is checked (plain `.zip` name, no link, one hard link), moved to
   the user's SYSTEM-only result folder and streamed over the pipe in 32 KB chunks with
   its SHA-256. The app writes it to the user's Downloads folder as the user.
7. The last result (and package) per SID is kept in the service's SYSTEM-only working
   folder, so the app can show it again after it was closed during a long run or
   after a restart. Other users only see that a run is in progress, never its content.
   A run is stored as "in progress" when it is accepted, and saved again (at most every
   5 seconds, written through to disk) after a `finding`, `change`, `restart-required`,
   `mdm-queued`, `summary` or `file` record arrives; a pending save goes out with a later
   record or with the final result. A stored run still in progress when the service
   starts becomes `Interrupted`, with the records saved up to then (after a power loss,
   records of the last few seconds may be missing; progress lines between saves too).
   Stored records are capped at 256 KB per run; the closing records (`summary`,
   `restart-required`) always have 8 KB of their own. When the cap is reached, older
   records of a lower rank make room, oldest first and in order: info progress, then
   warning or error progress, then `warning` records; only for a closing record, as a
   last resort, also the oldest other records (findings, changes and the rest). Other
   records never push out a finding or change. All records are still streamed live.
8. Stopping the service takes at most about 23 seconds (under the 25 seconds an upgrade
   allows): a running engine is cancelled first, then new connections are refused,
   `ShuttingDown` is sent to all connected apps in parallel (3 seconds in total), the
   service waits until the run's result is stored (15 seconds from the cancel), and the
   connections close. When the service stops because its pipe failed, it exits with 1064
   so the recovery actions restart it, and it does not tell the apps to close; at system
   shutdown it does not tell them either (they end with the session).

Other request types:
- `GetStatus` (open to every user, 60 per minute): running action without the user,
  other sessions, enrollment, the last MDM session (the newest event of the MDM provider in
  the session log, or in the admin log on builds without one, with its source; event IDs
  differ between Windows builds and are not filtered on), the product's restart marker, active cooldowns, support text
  and URL, the caller's own stored result (action, run ID, still running or not), the
  IME's last check-in (see [IME log evidence](#ime-log-evidence)), and the device settings the
  health checks read (compliance settings, Defender, the clock, the MDM policy errors, the Intune
  scheduled tasks and the machine kind).
- `GetLastResult`: the caller's own run in progress or stored result. The `LastResult`
  message carries the summary and `RecordCount`; the records follow as separate
  `Record` messages, then the stored package (if any).
- `ReportClientEvent`: app-side events 1003-1005, written as client-reported.
- `Watch`: an open connection on which the service sends `ShuttingDown` before it
  stops (for example during an upgrade), so the app can close itself.

## Trusted service caller

An organization can let one Windows service of its own (for example an agent through which
its IT support runs the same actions remotely) call the service. Off by default; nothing
changes while the policy value is absent.
The contract a caller may rely on (messages, limits, protocol version, actions and gates, what
the caller never gets, the versioning rule) is [trusted-service-caller.md](trusted-service-caller.md).

- **Turned on by policy only:** `HKLM\SOFTWARE\Policies\EndpointToolbelt\TrustedServiceCaller`
  (REG_SZ) names one service (letters, digits and `_.-`, at most 80 characters, not this
  service). A value under the configuration key, which the MSI writes, is ignored. The service
  reads the policy at start and every minute.
- **Only while it is set** the service creates a second pipe, `\\.\pipe\EndpointToolbelt.Caller`,
  with `FILE_FLAG_FIRST_PIPE_INSTANCE`, `PIPE_REJECT_REMOTE_CLIENTS` and a DACL that grants
  SYSTEM full control (the service creates the instances) and the named service's SID
  (`NT SERVICE\<name>`) read/write data only; NETWORK is denied; interactive users and
  administrators get nothing. When the value is removed or changed, the pipe closes within a
  minute. When the named service is not installed, or the pipe name is taken, no pipe is created
  (event 1016 or 1014).
- **Client check after each request is read:** the client process must be the named service's
  running process (`GetNamedPipeClientProcessId` against `QueryServiceStatusEx`), in session 0,
  and its program file must lie in a folder that only SYSTEM, Administrators or TrustedInstaller
  can change (the same check the service applies to its own install folder). Otherwise the
  connection is closed (event 1017, at most 10 per 10 minutes).
- **Same rules as a user:** only `GetStatus`, `RunAction` and `GetLastResult`, with the same
  action enum, confirmation flags, other-sessions acknowledgement, `AllowMachineWideOnMultiSession`,
  rate limits and the engine's cooldowns. The caller's rate limits and stored results are kept
  under the caller service's SID (`S-1-5-80-...`), so a local user never sees the caller's results
  and the caller never sees a local user's.
- **One exception:** a tool the organization switched off with the policy value
  `DisabledTools` is refused to a user (`ToolSwitchedOff`, 24) but may still be run by the trusted
  service caller, so the service desk keeps it. Every other rule applies to the caller as to a user.
- **Whose data a run returns:** the user at the console when that user is the only one signed
  in (the engine then includes that user's scope, as for a local run); otherwise the device scope
  only. Other sessions are counted relative to that user (or, for the device scope, all user
  sessions count as other sessions), so the multi-session gates apply as for a local user.

## IME log evidence

The IME's own logs say more about an app than its registry state: how far the newest
attempt got and whether it is stuck. Their wording is not documented and changes between
IME versions, so:

- **One pattern table**, `Engine\ImeLogPatterns.json`, read by the engine (app phases,
  check-ins for the package) and the service (check-in for the health view). Each rule
  is a .NET regular expression (the same dialect in both) matched case-insensitively
  against one CMTrace record's message; the first matching rule wins. A record no rule
  matches is no evidence; no evidence is never shown as OK.
- **Bounded reads:** the newest two or three files of a kind, the last 4 MB of each, records
  of the last 7 days (app phases) or 30 days (check-in), a time cap that covers reading and
  matching (checked while records are parsed), and for app phases at most the newest
  30,000 records. Logs are cut at each record start and parsed one record at a time, so
  the work stays linear; a pattern timeout costs only that record or file. Every file is
  opened by handle without following a link and refused when it is a link or has more
  than one hard link; the IME can keep writing meanwhile (read, write and delete sharing).
- **App phases:** a block starts at the IME's "Processing subgraph with app ids" line; it
  becomes an app's newest attempt only once it records a step beyond its start (the IME
  goes through every block at each check-in and skips apps whose retry timer has not
  expired, and such a pass must not hide the failure before it). A line without an app ID
  belongs to the app its thread last named. Phases: download (bytes), hash check,
  unpacking, install (exit code; the IME's own mapping to success or failure) and detection
  after the install (a detection before the install does not count). A late line of an
  earlier phase never takes an attempt back; a step that starts again is a new try. Only
  the apps of the device scope and the requesting user's own scope are looked up, and an
  app that another user on the device also has gets no log evidence at all, because a log
  line does not always say whose attempt it is. Times in results are UTC tokens the app
  shows in the user's time zone.
- **Stuck download:** the byte count has not moved for 15 minutes (and the download does
  not have all its bytes), or a hash check or unpacking had no further recognised line for
  that app for 30 minutes. Measured against the newest line of the log, not the clock, so
  nothing counts as stuck while the IME was not running. Next step: Clear downloaded app
  content, then Retry work apps and settings. The support package shows this in its tab only.
- **Next automatic retry:** read from the IME's retry timers
  (`Win32Apps\<scope>\GRS\<hash>`; the app ID is in a value name or the key name, the
  failure time in the value), estimated as 24 hours after the recorded failure. Retry
  apps & policies reports it only when it did not clear the timers (other users signed in,
  or the clear failed); Full IME reset only when the state was not wiped.
- **Check-in (health view):** the service reads the IME's own log and app log for the
  newest successful policy fetch and a failure after it (token, proxy, HTTP 401/403/5xx or
  the network error the log names), caches the answer for 2 minutes and sends only times
  and a short code-like error token in the status, never log text. The app shows it as
  "Last IME check-in" with the same age thresholds as the last sync (24 hours, 7 days);
  a failure newer than the last success is at least Check. The rules are evaluated in one
  place for the service and the engine (`ImeCheckInEvidence`); the IME's start lines ("Delaying
  ... checkins by seconds = N in the first check in on start/restart", kind `start`) give its
  newest start and the first check-in it announced (`ImeStartUtc`, `ImeFirstCheckInDueUtc` in the
  status); a failure written while it starts is not a failed check-in, and until 5 minutes after
  the announced check-in the row is OK, "The app installer (IME) started again; first check-in in about N min" (after a device restart: "Starting after the device restarted; first check-in in a few minutes")
  (also for a device that started less than 15 minutes ago with nothing in the IME log yet).
- **Support package:** the tab "IME log summary" in `IntuneState.xlsx` holds, per app of the
  visible scopes, the newest attempt's phase, exit code, download bytes, last seen and
  estimated next retry; the last check-in and a failure after it; and per IME log
  (`IntuneManagementExtension`, `AppWorkload`, `AgentExecutor`, `ClientHealth`) and
  component the number of errors and warnings of the last 7 days. No log text, install
  command line or script reaches the sheet.

## Storage

| Item | Location | Access |
|---|---|---|
| Program files | `C:\Program Files\SoulBit\Endpoint Toolbelt\` | default Program Files ACL |
| Service working folder | `%SystemRoot%\System32\config\systemprofile\AppData\Local\EndpointToolbelt\` (`Work\`, `Results\`, `Limits\`) | SYSTEM and Administrators only; created born-locked and validated by the service |
| Configuration | `HKLM\SOFTWARE\EndpointToolbelt\Config` | SYSTEM and Administrators only; enforced by the service at every start (see [security.md](security.md)) |
| State (cooldowns, restart marker) | `HKLM\SOFTWARE\EndpointToolbelt\State`: `LastFullReset`, `LastWURepair`, `RestartRequiredSince` (ISO 8601 with offset), `RestartRequiredBy`; created by the MSI, written by the engine (which creates it if it is missing), read by the service; removed at uninstall | SYSTEM and Administrators only |
| Policy | `HKLM\SOFTWARE\Policies\EndpointToolbelt` | owned by the administrator; never written by the product |
| Engine log | `%ProgramData%\Microsoft\IntuneManagementExtension\Logs\EndpointToolbelt.log` | CMTrace format, 4 MB rollover; written only after the folder is verified (see below); the file gets a protected ACL: SYSTEM and Administrators only |
| Service log | `%ProgramData%\Microsoft\IntuneManagementExtension\Logs\EndpointToolbeltService.log` | CMTrace format, 4 MB rollover; same verification and the same protected ACL |
| Event log | log `Endpoint Toolbelt`, source `Endpoint Toolbelt` (installed names); registered by the MSI, never by the service (events are skipped while the source is missing) | write: SYSTEM and Administrators only |
| Per-user data | `%LOCALAPPDATA%\EndpointToolbelt\` (`settings.json`, `history.log`, `HealthReport.txt`, and `last-run.log`: a copy of the run log opened last with "Open full log") | the user |
| Run history | `%LOCALAPPDATA%\EndpointToolbelt\runs\<start in UTC>_<Action>_<run ID>.log` (for example `20261015-120000Z_RetryAppsAndPolicies_ab12cd34.log`; a check in the app uses its `local-` ID): one log per run, written by the app after every run the engine was asked to do and every check in the app, and when the user chooses "Open full log" (a run that still goes on is written as it is then, and its file is replaced when it ends). Each write goes through a temporary file that is swapped in. Kept: the newest 50 logs, at most 20 MB together; older ones are deleted after each write. A log whose name has no run ID is kept as history with its unknown fields left empty | the user |
| Support package | `<Downloads>\Toolbelt_yyyyMMdd[-n].zip` | the user |
| Run log | `<Downloads>\EndpointToolbelt_<Action>_<run ID>.log` (`EndpointToolbelt_<Action>_<start in UTC>[_n].log` for a run without an ID), written by the app (off its window thread, through a temporary file) after a run that failed, ended with warnings (exit 1 or 2; not when the only point is a sync Windows queued), left a problem, or was stopped: a header (app version, device, user, time zone and offset, action, run ID, service result, exit code, start, and the end only when the run's summary confirms it, else "Captured at", in local time and UTC), the result cards and the timeline in the user's time zone; every run has a file of its own | the user |

The IME Logs folder belongs to the Intune Management Extension, not to this product.
Before writing there, the service verifies that the folder is a real directory (no
reparse point in its path) owned by SYSTEM, Administrators or TrustedInstaller that
nobody else can write to, re-checks it at least every minute, and passes the folder it
checked to the engine in the request file. The service opens its log file with the
reparse-point and hard-link checks; the engine refuses a log file that is a link but
checks it by path only. If the folder check fails, both logs go to the SYSTEM-only
working folder and the service writes event 1015.

### Event IDs

| ID | Level | Meaning |
|---|---|---|
| 1000 | Information | action started; also exit 69003 |
| 1001 | Information / Warning | exit 0 / exit 2, 69001, 69002 |
| 1002 | Warning | action failed or invalid; also an interrupted run (written when the service stops during it or finds it at start) |
| 1003 | Information | client-reported: health check counts at app start |
| 1004 | Information | client-reported: support package created |
| 1005 | Warning | client-reported: action did not start |
| 1010 | Warning | confirmation missing |
| 1011 | Warning | rate limit reached |
| 1012 | Warning | multi-session block |
| 1013 | Error | a device policy (App Control, AppLocker, an attack surface reduction rule) or security software blocked the engine executable |
| 1014 | Error | pipe name already in use |
| 1015 | Warning | the IME Logs folder failed its check; logs go to the working folder |
| 1016 | Information / Warning | the trusted service caller pipe opened or closed / the named service is not installed or the pipe could not be opened |
| 1017 | Warning | a client of the trusted service caller pipe was refused |
| 1018 | Warning | a user asked for a tool the organization switched off with `DisabledTools`; the service refused it |
| 1020 | Error | service error (start failure, engine files not protected, pipe not available); Warning when `--remove-state` could not remove something at uninstall |

Only the service writes events, through `RegisterEventSource`/`ReportEvent` and only while
the MSI's source is registered; the app's events (1003-1005) reach it over the pipe and
are written as client-reported. The engine writes no events. Rejection events (1010-1012, 1018)
are throttled per user. Messages carry the action and the run ID, never a user's identity.

## Configuration

See the table in the [admin guide](admin-guide.md#configuration). Precedence: policy key >
config key > built-in default. Invalid values fall back to the default and are
logged. Only the service reads configuration; the app receives display values
over the pipe.

## Compiled engine

**Why.** Some organizations run App Control for Business with script enforcement and an
AllSigned policy, and allow no script file to be added to a device. The engine is therefore an
executable, `EndpointToolbelt.Engine.exe` (.NET Framework 4.8), and the product installs and
writes no script file.

**How it runs.** The service starts the engine for every action as a child process in a Job
Object, with a request file, records on standard output (JSON lines), exit codes, cooldowns and
a log. Every action has exactly one handler; an action without one is answered with 87. Before
every run the service checks that the executable exists and that the install folder and every
file in it pass the install-folder check, without links or hard links (event 1020 otherwise).
The `Engine` install folder holds the IME log pattern table only, which the service and the
engine read.

**In-box cmdlets.** The product hosts no PowerShell runspace. Three of Windows' own cmdlets have
no documented alternative, so `powershell.exe` is started only for a fixed allow-list of them:

- `BackupToAAD-BitLockerKeyProtector` and `Delete-DeliveryOptimizationCache`, from the
  engine as SYSTEM;
- `Get-AppxPackage` with `Reset-AppxPackage`, from the app as the user.

These calls go through one runner (`InboxCmdletRunner` in `EndpointToolbelt.Platform.dll`): the
full System32 path, `PSModulePath` set to the in-box modules only, a constant `-Command` that
works in ConstrainedLanguage mode, its own time limit, and the result as an exit code (0 done,
3 not present, 4 not available, 1 failed). A device policy that refuses one of them (App
Control, AppLocker or the execution policy) is named in a finding (`ENGINE-POLICY-BLOCKED` in
the engine) with the cmdlet to allow.

**Windows APIs instead of cmdlets.** Everything else is read through Windows' own APIs: WMI
(`Win32_EncryptableVolume`, `MSFT_MpComputerStatus`, `Win32_QuickFixEngineering`,
`MSFT_Partition` and `MSFT_Volume`), `EventLogReader` for the MDM, BitLocker and Windows Update
event logs, the service control manager, the Task Scheduler's COM API and the Windows Update
API (late bound through IDispatch, no generated interop assembly), and the configuration
manager (cfgmgr32) for disconnected devices. MpCmdRun's signature is checked with WinVerifyTrust
and its signer certificate; only an embedded signature counts, so the check fails closed.

**Windows Update worker.** The engine executable runs itself in worker mode
(`EndpointToolbelt.Engine.exe --wu-worker Search|Install|Busy [UpdateId]`), started by full path
from the checked install folder, in the same Job Object and with its own time limit. An
operation outside the three or an update ID that is not a GUID is refused before anything runs.

**Profile cache delete.** A Full IME reset deletes Company Portal's local app status cache file
in each user profile with a handle-relative delete: every folder below the profile root is
opened relative to its parent handle with `OBJ_DONT_REPARSE`, links, hard links and another
volume are refused, and RedirectionGuard is turned on first (see [security.md](security.md)).

**Time.** The engine emits UTC instants: CMTrace records are converted with the bias in the
record or the device's own offset, and deadlines use the run's monotonic clock.

**Signing and refusal.** `build.ps1 -SignCertificateThumbprint` signs every staged executable
and assembly and then the MSI; the build fails when the MSI's file list holds a script file.
`EngineNotSigned` and event 1013 mean that App Control or AppLocker refused the engine
executable (start error 577 or 1260), or that attack surface reduction rules or security
software denied it (5).

**Guards.** Tests check that every action has a handler, that only the in-box cmdlet runner
names `powershell.exe`, that nothing under `src` names `pwsh.exe` or
`System.Management.Automation`, generates code at run time or is a script file, and that every
allow-listed command uses only what the runner allows.
