#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.17',
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

    $resolvedWorkspace = [IO.Path]::GetFullPath($workspaceRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resolvedInstallerDirectory = [IO.Path]::GetFullPath($installerDirectory)
    if (-not $resolvedInstallerDirectory.StartsWith($resolvedWorkspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an installer directory outside the workspace: $resolvedInstallerDirectory"
    }
    if (Test-Path -LiteralPath $resolvedInstallerDirectory) {
        Remove-Item -LiteralPath $resolvedInstallerDirectory -Recurse -Force
    }

    & dotnet build $installerProject -c Release -p:DiskLoomVersion=$Version
    if ($LASTEXITCODE -ne 0) { throw 'DiskLoom MSI build failed.' }

    $englishMsi = Get-ChildItem -LiteralPath (Join-Path $installerDirectory 'en-US') -Filter 'DiskLoom-Setup-x64.msi' |
        Select-Object -First 1
    $germanMsi = Get-ChildItem -LiteralPath (Join-Path $installerDirectory 'de-DE') -Filter 'DiskLoom-Setup-x64.msi' |
        Select-Object -First 1
    if ($null -eq $englishMsi -or $null -eq $germanMsi) {
        throw "English and German installer outputs were not both found under $installerDirectory."
    }

    $englishOutput = Join-Path $installerDirectory 'DiskLoom-Setup-x64.msi'
    $germanOutput = Join-Path $installerDirectory 'DiskLoom-Setup-x64-de-DE.msi'
    Copy-Item -LiteralPath $englishMsi.FullName -Destination $englishOutput -Force
    Copy-Item -LiteralPath $germanMsi.FullName -Destination $germanOutput -Force

    if (-not [string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint)) {
        Sign-File $englishOutput $CodeSigningCertificateThumbprint
        Sign-File $germanOutput $CodeSigningCertificateThumbprint
    }
    Write-Host "Built English DiskLoom installer: $englishOutput"
    Write-Host "Built German DiskLoom installer: $germanOutput"
} finally {
    Pop-Location
}
