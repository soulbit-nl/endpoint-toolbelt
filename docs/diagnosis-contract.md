# The device diagnosis contract

> **Status:** design, not implemented. This page fixes the contract before the
> first version is built; the first release that carries it will be named here.

The device diagnosis is one read-only analysis of an Intune-managed device. The engine reads the
device's own evidence (the Intune Management Extension logs, the MDM admin event log, the app
state), decides what each observation means, and returns typed facts. The app's For IT support
page and a trusted service caller ([trusted-service-caller.md](trusted-service-caller.md)) read
the same answer; neither one decides on its own what a log line means.

## Versions

Three versions are kept apart:

| Version | Where | Goes up when |
|---|---|---|
| Protocol version | `ProtocolInfo.Version`, `StatusInfo.ProtocolVersion` | the pipe framing or an existing request breaks (the rule in trusted-service-caller.md). Adding the diagnosis requests below is not breaking. |
| Schema version | `schemaVersion` in every diagnosis answer, `StatusInfo.DiagnosisSchemaVersion` | `"major.minor"`. Minor: a field, an enum value or an issue type added. Major: a field removed, renamed or given another meaning. |
| Rules version | `rulesVersion` in every diagnosis answer | the recognition rules change (a pattern, a meaning, an evidence rule). It says which rule set produced the answer; it never changes the shape. |

A reader checks `StatusInfo.DiagnosisSchemaVersion` first. An empty value means the service has no
diagnosis; a major version the reader does not know makes only the diagnosis unavailable, never
the rest of the pipe. A reader ignores fields and enum values it does not know.

## Requests

All three are read-only, need no confirmation and run no repair.

| Request | Parameters | Answer |
|---|---|---|
| `StartDiagnosis` | `Depth`: `recent` (the current evidence) or `further` with the `AnalysisId` of an analysis to extend into older evidence; `Reuse` (boolean, for `recent`) | `Accepted` with the `AnalysisId` at once, then, when the step ends, one `Diagnosis` message on the same connection; or `Rejected` (`Busy`, `RateLimited`, `NotSupported`) |
| `GetDiagnosis` | `AnalysisId` | one `Diagnosis` message: the header, the issues and the coverage (no events). While a step of the analysis still runs: the header only, with `status` `running` |
| `GetEvidence` | `AnalysisId`, `SubjectKey` or a list of `EventId`s (at most 100), `Cursor` (empty for the first page) | one `EvidencePage` message: events in source order and the next cursor (empty after the last page) |

A caller that cannot keep the connection open until the step ends asks `GetDiagnosis` until the
status is no longer `running`; that request counts against the same read-only rate limit as
`GetStatus`.

Results of these requests, as `Rejected` with a result code (codes added to the protocol, not a
breaking change):

| Code | When |
|---|---|
| `NotSupported` | the service has no diagnosis, or not this schema's major version |
| `NotFound` | the ID is not an analysis of this caller and scope, or never existed |
| `NoLongerAvailable` | the analysis ended (expired, replaced, a scope change, a service restart), or an evidence reference points to a file or log generation that is gone |
| `Busy`, `RateLimited` | as for the other requests |

Rules:

- An analysis belongs to the caller that started it and to the scope the service granted at that
  moment (below). Another caller, or the same caller with another scope, gets `NotFound` for its
  ID. Every caller of the trusted caller pipe is one caller to the service: a remote reader that
  serves several people binds an analysis to one of them itself.
- With `Reuse`, the service answers with the caller's newest analysis of the same scope when it is
  younger than 5 minutes, and starts no run.
- The service keeps at most two analyses per caller, each for 30 minutes, and none after a
  restart. A third `StartDiagnosis` that starts a run ends the caller's oldest analysis; its ID
  then answers `NoLongerAvailable`. A change of the scope it would grant now (another signed-in
  user, a second user signing in) ends the caller's analyses the same way. A cursor works only
  inside its own analysis.
- Evidence answers come from what the analysis recorded. An event that refers to a file or a log
  generation that has since rotated, been cleared or replaced makes the request answer `NoLongerAvailable`;
  a newer file never answers for an older reference.
- Each step (`recent`, or one `further` step) is one bounded engine run that holds the service's
  single action worker for at most 30 seconds (the target that is measured before release);
  a repair can run between two steps.
- The diagnosis is never part of `GetLastResult` or the per-caller last run.

## Formats of the parameters

Every parameter has a fixed form, so a caller can check it as a typed value before it sends it:

| Parameter | Form | At most |
|---|---|---|
| `AnalysisId` | 32 lowercase hex characters: `^[0-9a-f]{32}$` | 32 |
| `EventId` | opaque: `^[A-Za-z0-9_-]{1,64}$` | 64 |
| `Cursor` | opaque, valid only in its analysis: `^[A-Za-z0-9_-]{1,512}$` | 512 |
| `SubjectKey` | the grammar below | 200 |
| `Depth` | `recent` or `further` | |

`SubjectKey`, schema 1.0 (a later minor version may add forms; a reader that does not know a form
treats the key as opaque):

```
app:<guid>:device | app:<guid>:user
imeCheckIn:device
mdmSync:<guid>
setting:<guid>:<hash>:<operation>

<guid>       36 lowercase characters: ^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$ (the app ID, the enrollment ID)
<hash>       32 lowercase hex characters (the first 16 bytes of SHA-256 over the setting's full path)
<operation>  add | replace | delete | exec | get
```

## Size limits

Measured after serialization, as UTF-8 bytes of the whole message:

| Message | At most |
|---|---|
| `Diagnosis` | 256 KiB; issues beyond it are left out, counted and marked in the header (`issuesTruncated`) |
| `EvidencePage` | 128 KiB and 100 events |
| One string field | 2,000 characters |

A byte limit is never more characters than bytes, so every answer also stays under 256 Ki
characters, inside a relay's 512 Ki character pass-through and the 1 MiB pipe frame, with room
for an envelope.

The JSON holds objects, arrays, strings, integers and booleans only: no floating-point numbers, no
duplicate keys, at most 8 levels deep. Times are ISO 8601 UTC instants (`"o"` format).

## The header

| Field | Meaning |
|---|---|
| `schemaVersion`, `rulesVersion` | as above |
| `productVersion` | the Endpoint Toolbelt version that produced it |
| `analysisId` | 32 lowercase hex characters |
| `measuredUtc` | when the engine read the evidence |
| `scope` | `device`, or `deviceAndUser` (the requesting user, or the console user for a trusted caller, as in [trusted-service-caller.md](trusted-service-caller.md)) |
| `otherUsers` | how many other user scopes the evidence held: a count only |
| `status` | `running` (a step still runs; the header only), `complete`, `partial` (coverage says why) or `failed` |
| `issuesTruncated` | how many issues were left out for the size limit |

## Issues

One issue per subject that needs attention or was confirmed resolved in the analysis.

| Field | Meaning |
|---|---|
| `subjectKey` | stable inside the scope: `app:<appId>:<device|user>`, `imeCheckIn:device`, `mdmSync:<enrollment>`, `setting:<enrollment>:<settingHash>:<operation>`. A setting's hash is taken over the full path, so the identity holds while the shown token is masked. |
| `type` | `win32Install`, `win32Removal`, `win32Detection`, `imeCheckIn`, `mdmSync`, `configurationSetting` (later versions add types; a minor schema change) |
| `title` | one plain sentence, from a fixed text per rule (no log text) |
| `name` | the app's name (a visible scope only), or the setting's shown token (no GUID segment, at most 80 characters, as on the health view) |
| `revision`, `intent` | for an app: the revision and the wanted state (`install` or `remove`) |
| `lastFailure` | the last failed attempt: `utc`, `phase`, `codes` (each with `value` and `meaning`), `eventId` |
| `lastConfirmed` | the last confirmed state of the subject: `utc`, `state`, `evidenceLevel`, `eventId`; empty when none |
| `postCheck` | after a step from this issue: `actionRunId` (the run ID of that `RunAction`, the same the caller sees in its `Accepted` and `Completed` and as `StatusInfo.LastResultRunId`), `startedUtc`, `state` (`confirmed`, `notConfirmed`, `unknown`), `reason`, `measuredUtc` |
| `state` | `failing`, `lastFailedStateUnknown`, `waiting`, `inProgress`, `resolved`, `resolvedAfterStep` |
| `evidenceLevel` | `confirmed` (a positive observation of the wanted state), `observed` (from a trusted source, not yet confirmed), `advisory` (a source whose writer cannot be verified; it never closes an issue on its own) |
| `attempts` | distinct failed attempts (a repeated line inside one attempt counts once); `attemptsLowerBound` when coverage was incomplete |
| `firstFailureUtc`, `lastFailureUtc` | over the read range |
| `eventIds` | the events behind the issue (at most 20; `GetEvidence` by `SubjectKey` pages through all of them) |
| `next` | at most one: `{ "kind": "action", "action": ... }`, `{ "kind": "wait", "untilUtc": ... }`, `{ "kind": "handOver", "reason": ... }` or `{ "kind": "none" }`. `action` is always an action name of the `RunAction` contract; in schema 1.0 only `RetryAppsAndPolicies`. A later minor version may name other `RunAction` actions; a reader offers only actions it allows and shows the others as no step. |

## Events

| Field | Meaning |
|---|---|
| `eventId` | unique inside the analysis |
| `subjectKey`, `attemptId` | the subject, and the attempt or session the event belongs to (empty when it cannot be assigned) |
| `utc`, `sourceOrder` | the event's own time, and its position in the source (for equal times) |
| `source` | `imeLog`, `mdmAdminLog`, `appState` |
| `sourceRef` | a file: its family, the file's generation (name, creation time, length when read) and the line's byte offset. An event log: the channel, the log's generation and the record ID. |
| `phase`, `outcome` | from the rule (`download`, `install`, `detect`, `checkIn`, `syncSession`, `apply`, ...; `succeeded`, `failed`, `started`, `retryPlanned`, ...) |
| `ruleId`, `confidence` | the rule that recognised it, and `high`, `medium` or `low` |
| `parts` | the parts of the line a rule allows to be shown: each `{ "kind": ..., "value": ... }` with kinds such as `component`, `code`, `bytes`, `percent`, `appName`, `settingToken`, `operation`, `enrollment`. Never free text. |
| `withheld` | `true` when the line matched no rule that allows parts: only the time, source and reference are given |

## Coverage

One entry per source family:

| Field | Meaning |
|---|---|
| `source` | `imeLog/<family>` (for example `imeLog/AppWorkload`), `mdmAdminLog`, `appState` |
| `availableOldestUtc`, `availableNewestUtc` | what the device still holds, when known |
| `readOldestUtc`, `readNewestUtc` | what this analysis read |
| `filesRead`, `filesSkipped`, `bytesRead`, `recordsRead`, `recordsSkipped` | as counted |
| `skipped` | reasons with counts: `rotated`, `readError`, `timeLimit`, `volumeLimit`, `unknownFormat`, `privacyScope`, `transportLimit`, `notReadYet` |
| `unassignedErrorLines` | error lines no rule could assign: quality information about the source, never an issue |
| `lowerBound` | `true` when a count above is known to be incomplete |

## The snapshot in the support package

The support package holds the same analysis as one file, for looking back when the device is
offline: `diagnosis/analysis.json`, the header, the issues, the coverage and the events of the
issues, in this schema, for the package's own scope (the requesting user and the device). At most
4 MiB before compression; above it the oldest events are left out and counted in the header. The
entry is compressed with Deflate (ZIP method 8), as every other entry of the package. A reader of
the package reads this file and never classifies the logs itself, so there is one rule set.

## What the answer never holds

Command lines, script content, script output, policy values, tokens, full URLs, user profile
paths, user names or other identifying user data, the evidence of another user's scope, and the
raw text of a line. The support package stays the explicit route to raw files
([it-support-guide.md](it-support-guide.md)).
