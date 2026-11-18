# Building and testing

## Prerequisites

- Windows 10 or 11 x64
- .NET SDK 10 (builds the .NET Framework 4.8 projects). `global.json` requires SDK 10.0.100 or
  later within 10.0 (`rollForward: latestFeature`).
- Windows PowerShell 5.1 or PowerShell 7, for the build script (a developer tool that never
  reaches a device)

Do not install the MSI on your development machine; test it in Windows Sandbox or a virtual
machine.

## Repository layout

```
src/EndpointToolbelt.Contracts/   protocol types, action enum, result and exit codes
src/EndpointToolbelt.Platform/    code the service, the engine and the app share
src/EndpointToolbelt.Service/     Windows service: pipe server, rules, engine runner
src/EndpointToolbelt.Engine/      the engine: EndpointToolbelt.Engine.exe
src/EndpointToolbelt.App/         WPF app
src/Engine/                       the engine's data: the IME log pattern table (no code)
installer/                        WiX 5 MSI project
tests/EndpointToolbelt.Tests/     xUnit tests
tests/EndpointToolbelt.Engine.TestHost/  test-only exe: the real engine run frame with a
                                  harmless handler, for the contract tests (never shipped)
build/build.ps1                   build entry point
```

## Build

```powershell
.\build\build.ps1                       # Release build, all tests, staging and the MSI
.\build\build.ps1 -Configuration Debug  # no staging of binaries, no MSI
.\build\build.ps1 -SkipTests
.\build\build.ps1 -Coverage             # as CI runs it, with code coverage
```

The build deletes and recreates `artifacts\` and never modifies the source tree. Steps: read the
version; `dotnet build` and `dotnet test` of the solution; stage the binaries; stage the engine's
data (`src\Engine\ImeLogPatterns.json`) to `artifacts\staging\Engine`; optionally sign every
staged executable and assembly (`-SignCertificateThumbprint` with `-TimestampUrl`); build the
MSI, check its file list for script files (the build fails on a `.ps1`, `.psm1`, `.psd1` or
`.ps1xml`) and optionally sign it. The MSI and its `.sha256` file go to `artifacts\`.

Binaries are staged from Release builds only: `EndpointToolbelt.exe`,
`EndpointToolbelt.Service.exe`, `EndpointToolbelt.Engine.exe`, their `.config` files,
`EndpointToolbelt.Contracts.dll` and `EndpointToolbelt.Platform.dll` (no `.pdb`) go to
`artifacts\staging\bin`. The build fails when any other `.dll` sits next to the app, the service
or the engine (an allow-list, so nothing the MSI would miss slips through) or when they were
built against different copies of a shared assembly.

Run the tests on their own:

```powershell
dotnet test .\EndpointToolbelt.slnx -c Release
```

## Version

The version lives only in `Directory.Build.props`. The build passes it to the assemblies and the
MSI. Every build that reaches a device needs a new three-part version, because the MSI upgrade
logic compares only the first three fields.

## Tests

The xUnit tests (`tests/EndpointToolbelt.Tests`) cover the protocol, confirmations, rate limits,
configuration precedence, path safety, dispatch completeness (every action enum value has
exactly one handler), the pipe server and engine runner, stop order and interrupted runs,
`--remove-state` and the app's presentation logic. The engine's actions run against hand-written
fakes for the registry, clock, services, event logs, devices, the process runner and the
Windows Update API. Guards check that only the in-box cmdlet runner names `powershell.exe`, that
nothing names `pwsh.exe` or `System.Management.Automation` or generates code at run time, that
no script file is under `src`, and the contract between the real service coordinator and a real
engine process.

The MSI (install, repair, upgrade, downgrade refusal, uninstall), the service as SYSTEM and the
app's windows are tested end to end in Windows Sandbox before a release.

## Continuous integration

`.github/workflows/build.yml` runs on pull requests and on version tags (`v*`): the build, the
tests with coverage and the MSI on Windows, and Semgrep (community rules for C#, secrets and
GitHub Actions) on Linux. A version tag also signs the tested executables and assemblies with
SoulBit's Artifact Signing certificate profile, builds the MSI from them
(`build.ps1 -PackageOnly`), signs the MSI, checks every signature with `build/Test-Signature.ps1`
(valid, signed by SoulBit, timestamped, also the files inside the MSI) and publishes a GitHub
release with the signed MSI and its SHA-256. Only the sign job can sign: it runs in the
environment `release`, which only `v*` tags may use, and logs in to Azure over OpenID Connect
without a secret. `.github/workflows/codeql.yml` runs CodeQL code scanning.

