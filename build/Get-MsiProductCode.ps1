<#
.SYNOPSIS
    Writes the product code of an MSI package, for example {12345678-ABCD-...}.

.DESCRIPTION
    Reads the ProductCode property from the package's Property table with the Windows Installer
    automation interface, read-only: nothing is installed and the file is not changed. Every build
    of Endpoint Toolbelt has its own product code, which an Intune MSI detection rule and the
    uninstall command need. The script stops with an error when the value is not a product code
    in braces.

.PARAMETER Path
    The MSI package.

.EXAMPLE
    .\build\Get-MsiProductCode.ps1 -Path .\artifacts\EndpointToolbelt-1.0.0-x64.msi
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'
$full = (Resolve-Path -LiteralPath $Path).ProviderPath
$installer = New-Object -ComObject WindowsInstaller.Installer
# Open mode 0: read-only.
$database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($full, 0))
$view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @("SELECT `Value` FROM `Property` WHERE `Property` = 'ProductCode'"))
try
{
    [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    if ($null -eq $record) { throw "No ProductCode property in $full." }
    $code = [string]$record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
}
finally
{
    [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
    [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}

if ($code -notmatch '^\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}$')
{
    throw "The ProductCode of $full is not a product code in braces: '$code'."
}
$code
