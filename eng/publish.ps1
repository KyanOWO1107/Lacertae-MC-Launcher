[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$Version = '0.1.0-test'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$repoFull = [IO.Path]::GetFullPath($repoRoot)
if ([IO.Path]::GetPathRoot($outputRoot) -eq $outputRoot -or $outputRoot -eq $repoFull) {
    throw 'OUTPUT_DIRECTORY_TOO_BROAD'
}
$packageRoot = Join-Path $outputRoot 'package'
$appPublish = Join-Path $outputRoot 'publish/app'
$updaterPublish = Join-Path $outputRoot 'publish/updater'

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $packageRoot, $appPublish, $updaterPublish | Out-Null

$license = Join-Path $repoRoot 'LICENSE'
if (-not (Test-Path -LiteralPath $license -PathType Leaf)) {
    throw 'OWNER_LICENSE_REQUIRED: add the owner-approved LICENSE before publishing.'
}

Push-Location $repoRoot
try {
    dotnet publish src/Lacertae.Desktop/Lacertae.Desktop.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version -o $appPublish
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
    dotnet publish src/Lacertae.Updater/Lacertae.Updater.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version -o $updaterPublish
    if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed.' }
}
finally {
    Pop-Location
}

Copy-Item -Path (Join-Path $appPublish '*') -Destination $packageRoot -Recurse -Force
$updaterRoot = Join-Path $packageRoot 'Updater'
New-Item -ItemType Directory -Force -Path $updaterRoot | Out-Null
Copy-Item -Path (Join-Path $updaterPublish '*') -Destination $updaterRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $packageRoot 'THIRD-PARTY-NOTICES.txt') -Force
Copy-Item -LiteralPath $license -Destination (Join-Path $packageRoot 'LICENSE') -Force

$sbom = [ordered]@{
    bomFormat = 'CycloneDX'
    specVersion = '1.5'
    version = 1
    metadata = [ordered]@{
        timestamp = [DateTimeOffset]::UtcNow.ToString('o')
        component = [ordered]@{ type = 'application'; name = 'Lacertae Minecraft Launcher'; version = $Version }
    }
    components = @()
}
$sbom | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $packageRoot 'sbom.cdx.json') -Encoding UTF8

& (Join-Path $PSScriptRoot 'package-manifest.ps1') -PackageDirectory $packageRoot | Out-Null
$zipPath = Join-Path $outputRoot ("Lacertae-$Runtime-$Version.zip")
Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Output ([ordered]@{
    runtime = $Runtime
    version = $Version
    packageDirectory = $packageRoot
    zip = $zipPath
} | ConvertTo-Json -Compress)
