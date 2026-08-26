#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.11',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [switch]$Publish,
    [string]$UpdateManifestUrl = '',
    [string]$PublisherCertificateSha256 = ''
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $workspaceRoot 'src\DiskLoom\DiskLoom.csproj'
$cliProject = Join-Path $workspaceRoot 'src\DiskLoom.Cli\DiskLoom.Cli.csproj'
$testsProject = Join-Path $workspaceRoot 'tests\DiskLoom.Core.SmokeTests\DiskLoom.Core.SmokeTests.csproj'

Push-Location $workspaceRoot
try {
    & dotnet restore $appProject
    if ($LASTEXITCODE -ne 0) { throw 'DiskLoom restore failed.' }

    & dotnet build $appProject -c $Configuration --no-restore -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw 'DiskLoom desktop build failed.' }

    & dotnet build $cliProject -c $Configuration -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw 'DiskLoom CLI build failed.' }

    & dotnet run --project $testsProject -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'DiskLoom smoke tests failed.' }

    if ($Publish) {
        $publishDirectory = Join-Path $workspaceRoot "artifacts\DiskLoom\$RuntimeIdentifier"
        $resolvedWorkspace = [IO.Path]::GetFullPath($workspaceRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $resolvedPublish = [IO.Path]::GetFullPath($publishDirectory)
        if (-not $resolvedPublish.StartsWith($resolvedWorkspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a publish directory outside the workspace: $resolvedPublish"
        }
        if (Test-Path -LiteralPath $resolvedPublish) {
            Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
        }

        & dotnet publish $appProject -c Release -r $RuntimeIdentifier --self-contained true -o $resolvedPublish -p:Version=$Version
        if ($LASTEXITCODE -ne 0) { throw 'DiskLoom desktop publish failed.' }
        & dotnet publish $cliProject -c Release -r $RuntimeIdentifier --self-contained true -o $resolvedPublish -p:Version=$Version
        if ($LASTEXITCODE -ne 0) { throw 'DiskLoom CLI publish failed.' }

        $updateConfiguration = [ordered]@{
            githubRepository = 'Naxterra/DiskLoom'
            manifestUrl = $UpdateManifestUrl
            publisherCertificateSha256 = ($PublisherCertificateSha256 -replace '\s', '').ToUpperInvariant()
            checkOnStartup = $true
            checkIntervalHours = 24
        }
        $updateConfiguration | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resolvedPublish 'update-config.json') -Encoding utf8NoBOM
        Write-Host "Published DiskLoom to $resolvedPublish"
    }
} finally {
    Pop-Location
}
