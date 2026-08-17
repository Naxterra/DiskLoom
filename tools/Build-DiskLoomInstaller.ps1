#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.9',
    [string]$UpdateManifestUrl = '',
    [string]$PublisherCertificateSha256 = '',
    [string]$CodeSigningCertificateThumbprint = '',
    [switch]$SkipAppBuild
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$installerProject = Join-Path $workspaceRoot 'installer\DiskLoom\DiskLoom.Installer.wixproj'
$publishDirectory = Join-Path $workspaceRoot 'artifacts\DiskLoom\win-x64'
$installerDirectory = Join-Path $workspaceRoot 'artifacts\DiskLoom\installer'

function Find-SignTool {
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    Get-ChildItem -LiteralPath $kitsRoot -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

function Sign-File([string]$Path, [string]$Thumbprint) {
    $signTool = Find-SignTool
    if ([string]::IsNullOrWhiteSpace($signTool)) { throw 'signtool.exe was not found in the Windows SDK.' }
    & $signTool sign /sha1 ($Thumbprint -replace '\s', '') /fd SHA256 /tr 'http://timestamp.digicert.com' /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw "Code signing failed for $Path" }
}

Push-Location $workspaceRoot
try {
    if (-not $SkipAppBuild) {
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Build-DiskLoom.ps1') -Configuration Release -Version $Version -Publish -UpdateManifestUrl $UpdateManifestUrl -PublisherCertificateSha256 $PublisherCertificateSha256
        if ($LASTEXITCODE -ne 0) { throw 'DiskLoom publish failed.' }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'DiskLoom.exe'))) {
        throw "Published DiskLoom application was not found at $publishDirectory."
    }

    if (-not [string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint)) {
        Sign-File (Join-Path $publishDirectory 'DiskLoom.exe') $CodeSigningCertificateThumbprint
        Sign-File (Join-Path $publishDirectory 'DiskLoom.Cli.exe') $CodeSigningCertificateThumbprint
    }

    & dotnet build $installerProject -c Release -p:DiskLoomVersion=$Version
    if ($LASTEXITCODE -ne 0) { throw 'DiskLoom MSI build failed.' }

    $msi = Get-ChildItem -LiteralPath $installerDirectory -Recurse -Filter 'DiskLoom-Setup-x64.msi' |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $msi) { throw "Installer output was not found under $installerDirectory." }

    if (-not [string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint)) {
        Sign-File $msi.FullName $CodeSigningCertificateThumbprint
    }
    Write-Host "Built DiskLoom installer: $($msi.FullName)"
} finally {
    Pop-Location
}
