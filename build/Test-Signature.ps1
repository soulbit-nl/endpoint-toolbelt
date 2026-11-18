<#
.SYNOPSIS
    Checks that release files carry a valid, timestamped Authenticode signature of the publisher.

.DESCRIPTION
    For every file: the signature status is Valid, the signer certificate's organization (O=) is
    the publisher, and the signature has a timestamp. A timestamp is required: the certificates of
    an Artifact Signing profile are valid for a few days only, so an untimestamped signature
    would stop being valid soon after the release.

    With -MsiContents each MSI is also unpacked with an administrative install (msiexec /a, which
    writes the files to a folder and changes nothing on the machine), and every executable and
    assembly in it is checked the same way. The script stops with an error that names every file
    that fails.

.PARAMETER Path
    The files to check.

.PARAMETER Publisher
    The organization (O=) the signer certificate must name. Default: SoulBit.

.PARAMETER MsiContents
    Also check the executables and assemblies inside each MSI.

.EXAMPLE
    .\build\Test-Signature.ps1 -Path .\artifacts\EndpointToolbelt-1.0.1-x64.msi -MsiContents
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path,
    [string]$Publisher = 'SoulBit',
    [switch]$MsiContents
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Test-OneSignature {
    # Returns the reason a file fails, or $null when its signature is good.
    param([string]$File)
    $signature = Get-AuthenticodeSignature -LiteralPath $File
    if ($signature.Status -ne 'Valid') { return "status $($signature.Status): $($signature.StatusMessage)" }
    $subject = $signature.SignerCertificate.Subject
    # The parsed name, one attribute per line: a value that only contains "O=..." cannot pass.
    $attributes = $signature.SignerCertificate.SubjectName.Format($true) -split "`r?`n" | ForEach-Object { $_.Trim() }
    if (@($attributes | Where-Object { $_ -ceq "O=$Publisher" }).Count -ne 1) { return "signed by '$subject', not by $Publisher" }
    if (-not $signature.TimeStamperCertificate) { return 'no timestamp' }
    return $null
}

$failures = New-Object System.Collections.Generic.List[string]
$checked = 0
foreach ($file in $Path) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { $failures.Add("${file}: not found"); continue }
    $reason = Test-OneSignature -File $file
    $checked++
    if ($reason) { $failures.Add("$(Split-Path -Leaf $file): $reason") }

    if ($MsiContents -and [System.IO.Path]::GetExtension($file) -eq '.msi') {
        $unpacked = Join-Path ([System.IO.Path]::GetTempPath()) ('et-msi-' + [guid]::NewGuid().ToString('N'))
        try {
            $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/a', ('"{0}"' -f (Resolve-Path -LiteralPath $file).Path), '/qn', ('TARGETDIR="{0}"' -f $unpacked)) -Wait -PassThru
            if ($process.ExitCode -ne 0) { throw "msiexec /a of $(Split-Path -Leaf $file) failed with exit code $($process.ExitCode)." }
            $inner = @(Get-ChildItem -LiteralPath $unpacked -Recurse -File | Where-Object { $_.Extension -in '.exe', '.dll' })
            if ($inner.Count -eq 0) { $failures.Add("$(Split-Path -Leaf $file): no executable or assembly found inside") }
            foreach ($item in $inner) {
                $innerReason = Test-OneSignature -File $item.FullName
                $checked++
                if ($innerReason) { $failures.Add("$(Split-Path -Leaf $file) > $($item.Name): $innerReason") }
            }
        } finally {
            if (Test-Path -LiteralPath $unpacked) { Remove-Item -LiteralPath $unpacked -Recurse -Force }
        }
    }
}

if ($failures.Count -gt 0) {
    throw "Signature check failed for $($failures.Count) of $checked files:`n  $($failures -join "`n  ")"
}
Write-Output "Signatures checked: $checked; each one valid, timestamped and from $Publisher."
