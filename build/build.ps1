<#
.SYNOPSIS
    Builds, tests and packages Endpoint Toolbelt.

.DESCRIPTION
    Steps, each skipped when its part of the repository does not exist yet:
      1. read the version from Directory.Build.props (single source of truth)
      2. build and test the .NET projects (dotnet build / dotnet test) and stage the
         installed binaries (three executables, their .config files, the contracts
         and platform assemblies; no .pdb) to artifacts\staging\bin
      3. stage the engine's data (the IME log pattern table) to artifacts\staging\Engine; the
         product installs no script file
      4. optionally sign every staged executable and assembly with an organization's own
         certificate
      5. build the MSI from the staged binaries and engine data, check that it holds no script
         file, optionally sign it, and write it with a .sha256 file to artifacts\

    The source tree is never modified.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER SignCertificateThumbprint
    Thumbprint of a code-signing certificate in CurrentUser\My or LocalMachine\My, for an
    organization that builds and signs the Toolbelt itself. Signing is off by default. SoulBit's
    releases are signed by the release workflow of the public repository with Artifact Signing,
    outside this script (see -PackageOnly).

.PARAMETER PackageOnly
    Build only the MSI, from the binaries already in artifacts\staging\bin (for example after
    they were signed): no .NET build, no tests, no new staging of the binaries. The staged set
    must be exactly the files the MSI installs.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server. Required when signing, because signatures are
    always timestamped.

.PARAMETER SkipTests
    Build without running tests.

.PARAMETER Coverage
    Also measure code coverage of the xUnit tests through coverlet (Cobertura), written to
    artifacts\coverage. Used by CI; informational, no threshold.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$SignCertificateThumbprint,
    [string]$TimestampUrl,
    [switch]$SkipTests,
    [switch]$Coverage,
    [switch]$PackageOnly
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$artifacts = Join-Path $root 'artifacts'
$staging = Join-Path $artifacts 'staging'

function Write-Step { param([string]$Text) Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Skip { param([string]$Text) Write-Host "    skipped: $Text" -ForegroundColor DarkGray }

function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE" }
}

# 0. Validate signing input before any work is done --------------------------------------
$cert = $null
if ($SignCertificateThumbprint) {
    if (-not $TimestampUrl) { throw 'Signing requires -TimestampUrl; signatures are always timestamped.' }
    $thumb = ($SignCertificateThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $cert = @(Get-ChildItem -Path Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert |
        Where-Object { $_.Thumbprint -eq $thumb }) | Select-Object -First 1
    if (-not $cert) { throw "Code-signing certificate $thumb not found in CurrentUser\My or LocalMachine\My." }
}

# 1. Version ---------------------------------------------------------------------------------
Write-Step 'Version'
$props = New-Object System.Xml.XmlDocument
$props.Load((Join-Path $root 'Directory.Build.props'))
$versionNodes = @($props.SelectNodes('/Project/PropertyGroup/Version'))
if ($versionNodes.Count -ne 1) { throw "Directory.Build.props must define Version exactly once (found $($versionNodes.Count))." }
$version = $versionNodes[0].InnerText.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$version' in Directory.Build.props is not a three-part version." }
Write-Host "    $version"

if ($PackageOnly) {
    # Keep the staged binaries; only an earlier MSI and its output folder go.
    if ($Configuration -ne 'Release') { throw 'The MSI is built from Release binaries only; run without -Configuration Debug.' }
    $null = New-Item -ItemType Directory -Path $artifacts -Force
    Get-ChildItem -LiteralPath $artifacts -File -Filter 'EndpointToolbelt-*-x64.msi*' | Remove-Item -Force
    $msiOld = Join-Path $artifacts 'msi'
    if (Test-Path -LiteralPath $msiOld) { Remove-Item -LiteralPath $msiOld -Recurse -Force }
} else {
    if (Test-Path -LiteralPath $artifacts) { Remove-Item -LiteralPath $artifacts -Recurse -Force }
    $null = New-Item -ItemType Directory -Path $artifacts
}

# 2. .NET projects -----------------------------------------------------------------------------
Write-Step '.NET build'
$solutions = @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Extension -in '.sln', '.slnx' })
if ($solutions.Count -gt 1) { throw 'More than one solution file in the repository root.' }
if ($PackageOnly) {
    Write-Skip 'package only: the staged binaries are used as they are'
} elseif ($solutions.Count -eq 1) {
    Invoke-Native dotnet @('build', $solutions[0].FullName, '-c', $Configuration, '-nologo')
    if (-not $SkipTests) {
        Write-Step '.NET tests'
        $testArgs = @('test', $solutions[0].FullName, '-c', $Configuration, '--no-build', '-nologo')
        if ($Coverage) { $testArgs += @('--collect', 'XPlat Code Coverage', '--results-directory', (Join-Path $artifacts 'coverage\dotnet')) }
        Invoke-Native dotnet $testArgs
    }
} else {
    Write-Skip 'no solution file yet'
}

# 2b. Stage the .NET binaries for the MSI -------------------------------------------------------
# Only the files the MSI installs: the three executables, their .config files and the shared
# contracts and platform assemblies. No .pdb or test files.
Write-Step 'Stage binaries'
$binStaged = Join-Path $staging 'bin'
$binSources = [ordered]@{
    'EndpointToolbelt.exe'                = "src\EndpointToolbelt.App\bin\$Configuration\net48"
    'EndpointToolbelt.exe.config'         = "src\EndpointToolbelt.App\bin\$Configuration\net48"
    'EndpointToolbelt.Service.exe'        = "src\EndpointToolbelt.Service\bin\$Configuration\net48"
    'EndpointToolbelt.Service.exe.config' = "src\EndpointToolbelt.Service\bin\$Configuration\net48"
    'EndpointToolbelt.Engine.exe'         = "src\EndpointToolbelt.Engine\bin\$Configuration\net48"
    'EndpointToolbelt.Engine.exe.config'  = "src\EndpointToolbelt.Engine\bin\$Configuration\net48"
    'EndpointToolbelt.Contracts.dll'      = "src\EndpointToolbelt.Contracts\bin\$Configuration\net48"
    'EndpointToolbelt.Platform.dll'       = "src\EndpointToolbelt.Platform\bin\$Configuration\net48"
}
# The projects whose output folder is checked, and the assemblies allowed next to their
# executables; anything else would be missing from the MSI.
$binProjects = @('EndpointToolbelt.App', 'EndpointToolbelt.Service', 'EndpointToolbelt.Engine')
$allowedDlls = @('EndpointToolbelt.Contracts.dll', 'EndpointToolbelt.Platform.dll')
if ($PackageOnly) {
    # Exactly the files the MSI installs, nothing missing and nothing added.
    if (-not (Test-Path -LiteralPath $binStaged -PathType Container)) { throw "No staged binaries in $binStaged." }
    $present = @(Get-ChildItem -LiteralPath $binStaged -File | ForEach-Object Name | Sort-Object)
    $expected = @($binSources.Keys | Sort-Object)
    $difference = @(Compare-Object -ReferenceObject $expected -DifferenceObject $present)
    if ($difference.Count -gt 0) {
        throw "The staged binaries in $binStaged differ from the files the MSI installs: $(($difference | ForEach-Object { $_.SideIndicator + ' ' + $_.InputObject }) -join ', ')"
    }
    Write-Host "    using $($present.Count) staged files in $binStaged"
} elseif ($solutions.Count -ne 1) {
    Write-Skip 'no .NET build'
} elseif ($Configuration -ne 'Release') {
    Write-Skip "staging is for Release builds only (this is $Configuration); no MSI will be built"
} else {
    if (Test-Path -LiteralPath $binStaged) { Remove-Item -LiteralPath $binStaged -Recurse -Force }
    $null = New-Item -ItemType Directory -Path $binStaged -Force
    foreach ($project in $binProjects) {
        $outDir = Join-Path $root "src\$project\bin\$Configuration\net48"
        $unexpected = @(Get-ChildItem -LiteralPath $outDir -Filter '*.dll' -File | Where-Object { $allowedDlls -notcontains $_.Name })
        if ($unexpected.Count -gt 0) {
            throw "Unexpected assemblies next to $project (add them to the staging list first): $(($unexpected | ForEach-Object Name) -join ', ')"
        }
    }
    foreach ($name in $binSources.Keys) {
        $source = Join-Path (Join-Path $root $binSources[$name]) $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Build output $source not found." }
        Copy-Item -LiteralPath $source -Destination (Join-Path $binStaged $name)
    }
    # Every executable must have been built against the same shared assemblies as those staged.
    foreach ($dll in $allowedDlls) {
        $stagedHash = (Get-FileHash -LiteralPath (Join-Path $binStaged $dll) -Algorithm SHA256).Hash
        foreach ($project in $binProjects) {
            $copy = Join-Path $root "src\$project\bin\$Configuration\net48\$dll"
            if ((Test-Path -LiteralPath $copy) -and (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne $stagedHash) {
                throw "$project was built with a different $dll than the one staged."
            }
        }
    }
    Write-Host "    staged $($binSources.Count) files to $binStaged"
}

# 2c. Installer art ---------------------------------------------------------------------------------
# Bit on the installer's screens, drawn by the app's own helper character through one test
# (InstallerArtTests), so the installer never shows another Bit than the app. Made also with
# -SkipTests: the MSI needs the bitmaps.
Write-Step 'Installer art'
$artStaged = Join-Path $staging 'installer-art'
$artFiles = @('bit-working.bmp', 'bit-done.bmp', 'bit-idle.bmp', 'bit-error.bmp', 'bit-attention.bmp')
if ($PackageOnly) {
    $missing = @($artFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $artStaged $_) -PathType Leaf) })
    if ($missing.Count -gt 0) { throw "No staged installer art in ${artStaged}: $($missing -join ', ')" }
    Write-Host "    using the staged installer art in $artStaged"
} elseif ($solutions.Count -ne 1 -or $Configuration -ne 'Release') {
    Write-Skip 'no Release build, so no MSI'
} else {
    if (Test-Path -LiteralPath $artStaged) { Remove-Item -LiteralPath $artStaged -Recurse -Force }
    $null = New-Item -ItemType Directory -Path $artStaged -Force
    $testProject = Join-Path $root 'tests\EndpointToolbelt.Tests\EndpointToolbelt.Tests.csproj'
    $env:ETB_INSTALLER_ART = $artStaged
    try {
        Invoke-Native dotnet @('test', $testProject, '-c', $Configuration, '--no-build', '-nologo', '--filter', 'FullyQualifiedName~EndpointToolbelt.Tests.InstallerArtTests')
    } finally {
        Remove-Item -Path Env:\ETB_INSTALLER_ART -ErrorAction SilentlyContinue
    }
    $missing = @($artFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $artStaged $_) -PathType Leaf) })
    if ($missing.Count -gt 0) { throw "The installer art was not made: $($missing -join ', ')" }
    Write-Host "    staged $($artFiles.Count) bitmaps to $artStaged"
}

# Script files the product never installs; the staging and the MSI are checked for them.
$scriptExtensions = @('.ps1', '.psm1', '.psd1', '.ps1xml')

function Invoke-Sign {
    # Authenticode signature with a timestamp; any file Set-AuthenticodeSignature can sign (PE, MSI).
    param([string]$Path)
    $sig = Set-AuthenticodeSignature -FilePath $Path -Certificate $cert -TimestampServer $TimestampUrl -HashAlgorithm SHA256
    if ($sig.Status -ne 'Valid') { throw "Signing failed for $(Split-Path -Leaf $Path): $($sig.StatusMessage)" }
}

# 3. Engine data (staged copy) -----------------------------------------------------------------------
Write-Step 'Engine data'
$engineStaged = Join-Path $staging 'Engine'
$patterns = Join-Path $root 'src\Engine\ImeLogPatterns.json'
if (-not (Test-Path -LiteralPath $patterns -PathType Leaf)) { throw "The IME log pattern table $patterns is missing." }
$null = New-Item -ItemType Directory -Path $engineStaged -Force
Copy-Item -LiteralPath $patterns -Destination $engineStaged
$scripts = @(Get-ChildItem -LiteralPath $staging -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $scriptExtensions -contains $_.Extension.ToLowerInvariant() })
if ($scripts.Count -gt 0) { throw "Script files in the staging folder: $(($scripts | ForEach-Object Name) -join ', ')" }
Write-Host "    staged ImeLogPatterns.json"

# 4. Optional signing of the staged binaries --------------------------------------------------------
Write-Step 'Signing'
if ($cert) {
    $files = @(Get-ChildItem -LiteralPath $binStaged -File | Where-Object { $_.Extension -in '.exe', '.dll' })
    foreach ($f in $files) { Invoke-Sign $f.FullName }
    Write-Host "    signed $($files.Count) staged executables and assemblies"
} else {
    Write-Skip 'no certificate given (unsigned build)'
}

# 5. MSI ------------------------------------------------------------------------------------------------
Write-Step 'MSI'
$wixproj = Join-Path $root 'installer\EndpointToolbelt.Installer.wixproj'
if (Test-Path -LiteralPath $wixproj) {
    if ($Configuration -ne 'Release') { throw 'The MSI is built from Release binaries only; run without -Configuration Debug.' }
    if (-not (Test-Path -LiteralPath (Join-Path $binStaged 'EndpointToolbelt.Service.exe'))) { throw "No staged binaries in $binStaged." }
    $msiOut = Join-Path $artifacts 'msi'
    # Never incremental: a changed version alone does not make MSBuild rebuild the package, and
    # an MSI with an old version and product code would install as a repair, not an upgrade.
    Invoke-Native dotnet @('build', $wixproj, '-c', $Configuration, '-nologo', '--no-incremental', '-o', $msiOut,
        "-p:ProductVersion=$version", "-p:EngineDir=$engineStaged", "-p:BinDir=$binStaged", "-p:ArtDir=$artStaged")
    $msis = @(Get-ChildItem -LiteralPath $msiOut -Filter '*.msi' -File)
    if ($msis.Count -ne 1) { throw "Expected exactly one MSI in $msiOut, found $($msis.Count)." }
    $target = Join-Path $artifacts ("EndpointToolbelt-{0}-x64.msi" -f $version)
    Copy-Item -LiteralPath $msis[0].FullName -Destination $target
    # Guard: the MSI's own file list holds no script file.
    $installer = New-Object -ComObject WindowsInstaller.Installer
    # Typed arguments: Windows Installer's IDispatch refuses PowerShell's wrapped values.
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, [object[]]@([string]$target, [int]0))
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, [object[]]@([string]'SELECT `FileName` FROM `File`'))
    $none = [object[]]@()
    [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $none)
    $installed = New-Object System.Collections.Generic.List[string]
    while ($null -ne ($record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $none))) {
        # "SHORT~1.EXT|Long name.ext": the long name is the one installed.
        $installed.Add((($record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, [object[]]@([int]1))) -split '\|')[-1])
    }
    [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $none)
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($view)
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($database)
    $scripts = @($installed | Where-Object { $scriptExtensions -contains [System.IO.Path]::GetExtension($_).ToLowerInvariant() })
    if ($scripts.Count -gt 0) { throw "The MSI would install script files: $($scripts -join ', ')" }
    if ($installed.Count -eq 0) { throw 'The MSI file list could not be read.' }
    Write-Host "    $($installed.Count) files, no script file"
    if ($cert) { Invoke-Sign $target; Write-Host '    signed the MSI' }
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$target.sha256", ("{0}  {1}`n" -f $hash, (Split-Path -Leaf $target)), [System.Text.Encoding]::ASCII)
    Write-Host "    $target"
} else {
    Write-Skip 'no installer project yet'
}

Write-Step "Done (version $version, $Configuration)"
