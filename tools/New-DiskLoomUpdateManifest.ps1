#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [Parameter(Mandatory)]
    [ValidatePattern('^https://')]
    [string]$InstallerUrl,
    [string]$InstallerPath = '',
    [string]$ReleaseNotes = '',
    [string]$ReleaseNotesUrl = '',
    [switch]$Mandatory,
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $workspaceRoot 'artifacts\DiskLoom\installer\DiskLoom-Setup-x64.msi'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $workspaceRoot 'artifacts\DiskLoom\installer\stable.json'
}
if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
    throw "Installer not found: $InstallerPath"
}

$manifest = [ordered]@{
    version = $Version
    installerUrl = $InstallerUrl
    sha256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash
    releaseNotes = $ReleaseNotes
    releaseNotesUrl = $ReleaseNotesUrl
    publishedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    mandatory = [bool]$Mandatory
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Host "Wrote update manifest: $OutputPath"
