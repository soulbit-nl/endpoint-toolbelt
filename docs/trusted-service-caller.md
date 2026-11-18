# The trusted service caller contract

This page is the contract between Endpoint Toolbelt and the one Windows service an
administrator names in policy as the trusted service caller: today Toolbelt Connect's agent.
Everything a caller may rely on is here; what is not here is not promised. The design decision
and the service-side checks are in [architecture.md](architecture.md#trusted-service-caller)
and [security.md](security.md#trusted-service-caller).

## Protocol version

| Item | Value |
|---|---|
| Protocol version | **1** (`ProtocolInfo.Version`) |
| First Endpoint Toolbelt release with the caller pipe | 1.0.0 |
| Where the version travels | `Request.ProtocolVersion` (sent by the caller), `StatusInfo.ProtocolVersion` (returned by the service) |

The service refuses a request whose `ProtocolVersion` differs from its own with `Rejected` /
`ProtocolMismatch` (11), and its `Message` names the service's version. A caller therefore
checks `StatusInfo.ProtocolVersion` and `StatusInfo.ServiceVersion` from a `GetStatus` first and
stops, with a clear message, when the version is not one it knows.

**Versioning rule.** The protocol version goes up by one on every breaking change: a field or
enum value removed or renamed, a field's meaning or type changed, a message's order or framing
changed, a request type or action a caller had taken away, a limit made smaller. Adding a field
(with a `DataMember` order after the existing ones), a response type, a result code, an action
or an event is not breaking: a caller ignores fields, types and codes it does not know, and the
JSON serializer leaves unknown members alone. The CHANGELOG names every change of the protocol
version, and Toolbelt Connect's `MinToolbeltVersion` names the first Endpoint Toolbelt version
it needs.

## Turning the pipe on

- The policy value `HKLM\SOFTWARE\Policies\EndpointToolbelt\TrustedServiceCaller` (REG_SZ)
  names one service: letters, digits and `_.-`, at most 80 characters, not the product's own
  service. A value under the product's configuration key is ignored.
- The service reads the policy at start and every minute. Only while the value names an
  installed service does `\\.\pipe\EndpointToolbelt.Caller` exist; when the value is removed
  or changed, the pipe closes within a minute. Without the value nothing changes.
- The pipe's DACL grants SYSTEM full control and the named service's SID (`NT SERVICE\<name>`)
  read and write data only; NETWORK is denied; `PIPE_REJECT_REMOTE_CLIENTS` is set.
- After the first request is read, the client process must be the named service's own running
  process, in session 0, with its program file in a folder that only SYSTEM, Administrators or
  TrustedInstaller can change. Otherwise the connection is closed without an answer and event
  1017 is written (at most 10 per 10 minutes). Event 1016 reports the pipe opening and closing.

## Framing and limits

| Item | Value |
|---|---|
| Transport | one named pipe connection per request; byte mode, duplex |
| Message | UTF-8 JSON (`DataContractJsonSerializer`, `DataMember` order), preceded by a 4-byte little-endian length |
| Largest request | 64 KB (`ProtocolInfo.MaxRequestBytes`); a larger length is refused before any buffer is allocated |
| Largest response message | 1 MB (`ProtocolInfo.MaxResponseBytes`); the service keeps every message below it by splitting records and files |
| File chunk | 32 KB of raw bytes per `FileChunk`, base64 in the message |
| Time to send the request | 5 seconds after connecting; a silent client is dropped |
| Open connections | at most 128 in all, at most 4 per account (the caller service's SID counts as one account) |
| Invalid UTF-8, invalid JSON, a length of 0 or above the limit | `Rejected` / `InvalidRequest` (13), or the connection closes |

Strings are plain text without control or format characters. Times are ISO 8601 UTC
instants (`"o"` format); the caller renders local time.

## Requests a caller may send

| `Request.Type` | Value | What it does | Limit |
|---|---|---|---|
| `GetStatus` | 1 | one `Status` response with `StatusInfo` | 60 per minute per caller |
| `RunAction` | 2 | starts one action; the run's messages follow on the same connection | the action's rules below |
| `GetLastResult` | 3 | the caller's own last run: `LastResult`, then its `Record` messages, then the stored package if any | 20 per minute per caller |

Everything else is refused with `Rejected` / `InvalidRequest` (13) and the message "This
request is not available to the trusted service caller": `ReportClientEvent` (4), `Watch` (5)
and any unknown type.

`Request` fields: `ProtocolVersion`, `Type`, `Action` (for `RunAction`), `Confirmed`,
`ConfirmedOtherSessions` (the number of other sessions the status reported when the caller
asked its user to confirm; -1 when it did not), `ConfirmationNonce` (below), `InterfaceIndex`
(order 9: for `RenewIpAddress` only, the interface index of the network adapter to renew;
0 for every other action), and `ClientEventId` / `ClientEventText`, which the caller never uses.

## The confirmation nonce

Every `Status` answer carries `StatusInfo.ConfirmationNonce`, a nonce for this caller. A
`RunAction` that needs a confirmation (`Confirmed`) or the other-sessions acknowledgement
(`ConfirmedOtherSessions`) must send one of the caller's nonces back in
`Request.ConfirmationNonce`; the run spends it. A nonce is valid for 15 minutes, for the
account it was issued to, once; the service keeps the newest 8 per caller. Without a valid
nonce the answer is `Rejected` / `ConfirmationRequired` (21) with a message that names the
status. So the caller's flow is: `GetStatus`, show the warning (and the other-sessions count)
to its user, `RunAction` with `Confirmed`, `ConfirmedOtherSessions` and the nonce. Actions
without a warning (and a machine-wide action while nobody else is signed in) need no nonce.
The fields are additive (orders 8 and 26): the protocol version stays 1, and an older service
ignores the request field.

## Actions and gates

The caller gets the same action enum and the same rules as a signed-in user: not more, not
less, with one exception: a tool the organization switched off with the policy value
`DisabledTools` is refused to a user (`ToolSwitchedOff`, 24) but not to the caller, so the
service desk keeps it. The service enforces every other gate; the caller cannot skip one.

| Action | Value | Confirmation (`Confirmed`) | Machine-wide gate | Counts toward the rate limit | Produces a file |
|---|---|---|---|---|---|
| `RetryAppsAndPolicies` | 1 | no | no | yes | no |
| `ClearAppCache` | 2 | yes | no | yes | no |
| `FullReset` | 3 | yes | yes | yes | no |
| `RepairWU` | 4 | yes | yes | yes | no |
| `CollectDiag` | 5 | no | no | no (package limit instead) | yes |
| `BackupRecoveryKey` | 6 | no | no | yes | no |
| `SyncClock` | 7 | no | no | yes | no |
| `UpdateDefender` | 8 | no | no | yes | no |
| `FreeDiskSpace` | 9 | yes | yes | yes | no |
| `DiagnoseApps` | 10 | no | no | no (read-only limit instead) | no |
| `RenewIpAddress` | 11 | yes | yes | yes | no |
| `CheckWindowsUpdate` | 12 | no | no | no (read-only limit instead) | no |

`DiagnoseApps` is read-only; see
[The read-only app diagnosis](#the-read-only-app-diagnosis) below. `RenewIpAddress`
renews the DHCP lease of one physical network adapter, the one `InterfaceIndex` names; see
[Get a new IP address](#get-a-new-ip-address) below. `CheckWindowsUpdate` is read-only;
see [Check Windows Update](#check-windows-update) below.

Gates, in the order the service applies them, each answered with `Rejected` and a result code:

1. `ProtocolMismatch` (11): the protocol version differs.
2. `InvalidRequest` (13): an action outside the table, or `RenewIpAddress` without a
   positive `InterfaceIndex`, or another action with one.
   (here a user gets `ToolSwitchedOff` (24) for a tool the organization switched off; the
   caller never does, so a caller that does not know code 24 never sees it.)
3. `Busy` (20): another action is running (one worker per device; `StatusInfo.RunningAction`
   says which).
4. `ConfirmationRequired` (21): the action needs `Confirmed` and it is false.
5. `MultiSessionBlocked` (23): a machine-wide action while the other sessions cannot be counted,
   or while other users are signed in and the device's configuration
   `AllowMachineWideOnMultiSession` is off.
6. `ConfirmationRequired` (21): a machine-wide action with other users signed in and
   `ConfirmedOtherSessions` lower than the number of other sessions right now (the caller
   showed an older count, or none).
7. `ConfirmationRequired` (21): the action needed a warning (a confirmation, or other users
   signed in) and `ConfirmationNonce` is not a valid nonce of this caller.
8. `RateLimited` (22): more than `UserRateLimitPerHour` (default 20, 1-60) counted actions in
   the last hour for this caller, or more than 4 support packages per hour on a shared device,
   or more than 30 read-only runs per hour for this caller (`DiagnoseApps`, and
   `CheckWindowsUpdate`, together).
9. The engine's cooldowns are checked by the engine after its read-only diagnosis: a run that
   is accepted can still end with exit code 69001 (`CooldownActive`) and the next time in
   `StatusInfo.Cooldowns`. 69002 is a pending restart, 69003 nothing to repair.

For the caller, "other sessions" are counted relative to the console user when that user is
the only one signed in, otherwise every user session is an other session. The caller's rate
limits and stored results are kept under the caller service's SID, apart from every local
user's; the engine's cooldowns are per device and shared by everyone.

## The read-only app diagnosis

`DiagnoseApps` (value 10) reads what the IME last recorded per Win32 app and changes nothing:
no sync, no reset, no retry-timer clear, no IME restart, no support package, no cooldown, no
restart marker. It is a `RunAction` like the others (one worker, `Busy` while another action
runs; stored as the caller's last result), with a time limit of 5 minutes, no confirmation and no
machine-wide gate. It does not count toward `UserRateLimitPerHour`; its own limit is 30 per
hour per caller (`RateLimited`, 22).

**Whose apps.** The device scope, and the scope of the user the run is for (the console user
when that user is the only one signed in, see [What the caller never gets](#what-the-caller-never-gets));
apps of other users are only counted (`otherscopes`). A user whose account has no Entra object
ID (a local or Active Directory account) gets the device scope only (`userscope` false).

**Records**, besides the usual `progress`, `finding` and `summary` (the summary has
`changes` 0):

- `app-diagnosis`, one per app record, at most 150, sorted by verdict (the ones another attempt
  does not fix first): `diagnosis` (the run ID, which identifies this diagnosis), `app` (the
  app ID), `name` (from the IME's own policy lines; empty when not found), `scope` (`device` or
  `user`, never the user's ID), `intent` (`required`, `available`, `uninstall`, `dependency`,
  or empty), `revision` (number, -1 when unknown), `verdict` (`PACKAGING`, `RESTART-FIRST`,
  `NOT-APPLICABLE`, `DEPENDENCY`, `RETRY-HELPS`, `UNKNOWN`, `OK`, `IN-PROGRESS`, `PENDING`,
  `NO-STATE`, `AVAILABLE`, `NO-ACTION`), `state` (the recorded state in words), `enforcement`
  (the IME's enforcement state number, or null), `code` (the error code as `0x` and eight hex
  digits, or empty), `installed` (`yes`, `no` or empty), `observed` (when the IME last updated
  the record, ISO 8601 UTC; empty when unknown), `phase` and `attempt` (how far the newest
  attempt in the IME log got, as a phase name and in words; empty without log evidence),
  `attemptutc` (the newest log line of that attempt, UTC), `nextretry` (the IME's own next
  attempt, estimated as 24 hours after the failure it recorded, UTC; empty when unknown),
  `download` (true when the failure is a download or network problem), `shared` (true when
  another user on the device has the same app, so no log evidence is given). It adds `kind`:
  `win32`, `store` (a Microsoft Store app the IME installs through WinGet, known by a WinGet code
  `0x8A15xxxx` or the IME log's WinGet lines), `msi` or `msix`; a caller that ignores it
  reads the record as before. For `store` and `msi`, `codemeaning` (the code in words from the shared table),
  `codestep`, `codesupport` and, for `store`, `codenext` (`connectivity`, `retry`, `it`,
  `restart`, `wait` or `none`).
- `app-diagnosis` with `kind` `msi`, one per MSI line-of-business app the MDM stack recorded
  (EnterpriseDesktopAppManagement) for the device or the caller (other users only counted), after
  the IME's apps: `app` (the MSI product code, lower case, without braces; not an Intune app ID),
  `name` (from Windows Installer's uninstall key when installed, else empty), `namesource`
  (`installer` with a name, else empty), `scope`, `intent` empty, `revision` null,
  `verdict` (from the CSP's `Status`: 70 `OK`; 10, 20, 40, 50 and the retries 25 and 55
  `IN-PROGRESS`; 48 `PENDING`; 30 and 60 `RETRY-HELPS`; 60 with 3010 or 1641
  `RESTART-FIRST`; another value `UNKNOWN`; none `NO-STATE`), `state` (the status in words with
  the code's name), `code` (`LastError`), `observed` (when the app's key was last written),
  `download` (statuses 25 and 30), `msistatus`, `version`, `downloadhost` (the host of the
  download address only, never its path or query), `retryindex` and `retrycount`.
- `app-diagnosis` with `kind` `msix`, one per MSIX or Appx line-of-business app install the
  MDM stack holds (EnterpriseModernAppManagement AppInstallation, through the MDM WMI Bridge) for
  the device or the caller (other users are not read), after the MSI apps: `app` (the package
  family name), `name` and `namesource` empty, `scope`, `intent` empty, `revision` null,
  `verdict` (from the CSP's `Status`: 0 and 1 `IN-PROGRESS`, 2 `RETRY-HELPS`, 3 `OK`, another
  value `UNKNOWN`, none `NO-STATE`), `state` (the status in words with the code's name), `code`
  (`LastError`), `observed` (when it was read: the CSP gives the state as it is now), `download`
  (0x80073CF5), `codemeaning`, `codestep`, `codesupport` and `codenext` (`it`, `wait`,
  `connectivity` or `none`) from the AppX code table, `msistatus` null, `version` (the version
  installed now, empty when none), `downloadhost` empty, `retryindex` and `retrycount` null.
- `apps-diagnosis`, once, after the list: `diagnosis`, `present` (the IME has app state on this
  device), `records` (app records read), `listed` (records sent), `scopes`, `otherscopes`,
  `userscope`, `logevidence` (the IME log could be read); `msi` (MSI app records sent) and
  `msiotherscopes` (other users with MSI apps, counted only); `msix` (MSIX app install
  records sent).

Exit code 0 when the state was read (also when there is none), 1 when it could not be read
(finding `DIAG-APPS-UNREADABLE`). A service that does not know an action answers with
`Rejected` / `InvalidRequest` (13), the gate for an action outside its table, before anything
runs. New actions and new record types are additions: the protocol version stays 1.

## Get a new IP address

`RenewIpAddress` (value 11) renews the DHCP lease of one network adapter: the physical adapter
whose interface index the request names in `InterfaceIndex` (a positive number; the service
refuses the action without one, and refuses an index on any other action). The engine reads the
adapters itself and renews nothing when no physical adapter has that index. It asks Windows to
renew that adapter's lease (`Win32_NetworkAdapterConfiguration.RenewDHCPLease`, no `cmd.exe`,
no `ipconfig`), then reads the adapter again for up to 15 seconds. It needs a confirmation and
it is machine-wide (the connection on that adapter drops for a moment for everyone on the
device), with a time limit of 5 minutes, and it counts toward the rate limit.

**Records**, besides the usual `progress`, `finding`, `change` and `summary`: one
`ip-renew` with `result` (`got-usable-address`, `no-answer`, `renew-failed`,
`fixed-address`, `disabled`, `no-driver`, `no-media` or `not-found`), `kind`
(`ethernet`, `wi-fi`, `other` or `unknown`), `lease` (`yes` when the adapter has a usable
IPv4 address afterwards) and `renew` (`done`, `timeout`, `failed`, or empty when nothing was
renewed). No record carries the adapter's name, an address or a Wi-Fi network name. Exit code 0
for a usable address, 1 for none (or the adapter not found), 2 when the adapter is turned off,
has no driver or no cable or Wi-Fi connection, 69003 for a fixed address (nothing to renew). A
service that does not know the action answers `Rejected` / `InvalidRequest` (13), as above.

## Check Windows Update

`CheckWindowsUpdate` (value 12) reads why Windows does not update and changes nothing: the last
successful quality update and how long ago, the update failures logged since then (with the
shared code table's plain words), what Group Policy, the Update CSP and Settings say about the
update source and timing (an update server, "Do not connect to any Windows Update Internet
locations", access switched off, automatic updates off, a pause, a deferral), the start types of
the services Windows Update needs, a pending restart and the free space on the system drive. It
searches for no update and starts no worker. No confirmation, not machine-wide, a time limit of
5 minutes, and it shares the read-only limit of `DiagnoseApps` (30 per caller per hour).

**Records**, besides the usual `progress`, `finding` and `summary`: one `wu-check` with
`verdict` (`up-to-date`, `behind`, `failing`, `no-history`, `blocked-by-policy`,
`services-disabled`, `restart-pending` or `low-space`), `lastmonth` (the patch month of the last
successful quality update, `yyyy-MM`, or empty), `days` (days since then, or null), `behind`
(months behind, or null), `codes` (up to four failure codes, `0x` plus eight hex digits,
separated by spaces), `failed` (failed quality update installs in the history since then),
`policy` (comma-separated tokens: `wsus`, `no-internet`, `access-off`, `auto-off`,
`paused-policy`, `paused-user`, `deferred-<days>`, or `none`), `disabled` (update services that
are disabled or missing), `restart`, `freegb` (whole GB, or null), `lowspace` and `repair`
(true when Repair Windows Update can help: the device is behind and no policy, disabled service
or pending restart stands in the way). Each finding carries one next step. No record names the
update server, a path, a user or the device. Exit code 0 when the device is up to date and no
finding needs checking, 2 otherwise. A service that does not know the action answers `Rejected` /
`InvalidRequest` (13).

## The messages of a run

After `RunAction` the service answers on the same connection, in this order:

1. `Accepted` (2) with `Result = Accepted` (1), `Action` and `RunId`, or `Rejected` (3) with
   one of the codes above and a `Message`.
2. Zero or more `Record` (4): one engine record each, a JSON object as text in `Record`
   (`type` progress, finding, change or result; `run`; `ts`; the fields of its type). The
   records are the only detail a caller shows; it never parses the service's `Message` texts.
3. For a run that produced a file: `FileStart` (5) with `FileName`, `FileLength` and
   `FileSha256`, then `FileChunk` (6) messages with base64 `Data`, then `FileEnd` (7). The
   caller checks the hash.
4. `Completed` (8) with `Result` and `ExitCode`: `Completed` (0) when the engine ran (then the
   exit code says how it ended: 0 success, 1 failed, 2 with warnings, 87 invalid action, the
   69001-69003 codes above), or `EngineNotSigned` (30), `EngineTimeout` (31), `EngineCrashed`
   (32), `EngineDidNotStart` (33), `Interrupted` (34) when it did not.

A caller that loses the connection during a run asks `GetLastResult` later: the run goes on in
the service, the result is stored under the caller's SID, and `LastResultInfo.InProgress` says
whether it is still running. The last result holds at most one run per caller.

## What the status returns

`StatusInfo`: `ProtocolVersion`, `ServiceVersion`, `RunningAction` and `RunningSinceUtc`,
`OtherSessions`, `AllowMachineWideOnMultiSession`, `SupportText` and `SupportUrl` (the
device's configured IT support text and link), `Enrolled`, `RestartRequiredSinceUtc` and
`RestartRequiredBy`, `Cooldowns` (action and `AvailableAtUtc`), the caller's own last result
summary (`HasLastResult`, `LastResultAction`, `LastResultRunId`, `LastResultInProgress`,
`LastResultCode`, `LastResultExitCode`), the last MDM sync (`LastSyncUtc`, `LastSyncSource`),
the IME check-in (`LastImeCheckInUtc`, `ImeCheckInErrorUtc`, `ImeCheckInError`,
`ImeCheckInUnavailable`) and the device `Readouts` (compliance settings, Defender, clock,
profile errors, Intune connection: tokens, times and counts only), `ConfirmationNonce`,
and the IME's newest start (`ImeStartUtc`, `ImeFirstCheckInDueUtc`; when the IME said it
would do its first check-in after a start or restart, null from a service that predates it), and
`MdmEnrollmentId` (the EntDMID of the one active Intune enrollment, as a GUID; null without
exactly one enrollment, when it cannot be read, or from a service that predates it), and
`DisabledTools` (order 31: the tools the organization switched off with the policy value of
that name, the service tools by their action names and the app tools by names of their own;
empty when none is off, null from a service that predates it). The caller may still run a
switched-off tool; it can use the list to tell its operator that the user does not see it.

## What the caller never gets

- Another account's data: results, packages and rate limits are per SID; a local user never
  sees the caller's results and the caller never sees a user's.
- A user's data from a run unless that user is at the console and the only one signed in
  (then the engine includes that user's scope, as for a local run); otherwise the device
  scope only.
- Client events (`ReportClientEvent`), the shutdown watch (`Watch`), anything on the user pipe
  `\\.\pipe\EndpointToolbelt`, or any request from a session other than 0 or from a process
  other than the named service's.
- A way around a gate: confirmations (with their nonce), the multi-session rules, the rate
  limits, the single worker and the cooldowns apply to the caller as to a user. The one gate
  that does not apply to the caller is a tool switched off by `DisabledTools`.
- Free text into the service: a request is an enum, flags and counts; nothing the caller sends
  becomes a path, an argument or a script.
- A stable `Message` text: the plain-language texts in `Rejected` and `Completed` may change
  without a protocol version change; a caller branches on `Result`, `ExitCode` and the
  records' `type` and ids, never on text.