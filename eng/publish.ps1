[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$Version = '0.1.0-test'
)

$ErrorActionPreference = 'Stop'
$releaseEpoch = 0L
if ([string]::IsNullOrWhiteSpace($env:SOURCE_DATE_EPOCH) -or -not [long]::TryParse($env:SOURCE_DATE_EPOCH, [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$releaseEpoch) -or $releaseEpoch -lt 0) {
    throw 'SOURCE_DATE_EPOCH_REQUIRED: set a non-negative Unix timestamp for reproducible release output.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$repoFull = [IO.Path]::GetFullPath($repoRoot)
if ([IO.Path]::GetPathRoot($outputRoot) -eq $outputRoot -or $outputRoot -eq $repoFull) {
    throw 'OUTPUT_DIRECTORY_TOO_BROAD'
}

$packageRoot = Join-Path $outputRoot 'package'
$workRoot = Join-Path $outputRoot '.work'
$sourceRoot = Join-Path $workRoot 'source'
$appPublish = Join-Path $workRoot 'publish/app'
$updaterPublish = Join-Path $workRoot 'publish/updater'

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $packageRoot, $sourceRoot, $appPublish, $updaterPublish | Out-Null

$license = Join-Path $repoRoot 'LICENSE'
if (-not (Test-Path -LiteralPath $license -PathType Leaf)) {
    throw 'PROJECT_LICENSE_REQUIRED: add the approved root LICENSE before publishing.'
}
$licenseText = Get-Content -LiteralPath $license -Raw
if ($licenseText -notmatch 'Apache License\s+Version 2\.0') {
    throw 'PROJECT_LICENSE_INVALID: the root LICENSE must be Apache-2.0.'
}

$excludedDirectoryNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in @('bin', 'obj', '.git', '.superpowers', '@misc', 'artifacts')) {
    [void]$excludedDirectoryNames.Add($name)
}

function Copy-SourceTree {
    param(
        [Parameter(Mandatory = $true)] [string]$Source,
        [Parameter(Mandatory = $true)] [string]$Destination
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $Source -Force) {
        if ($entry.PSIsContainer -and $excludedDirectoryNames.Contains($entry.Name)) {
            continue
        }

        $target = Join-Path $Destination $entry.Name
        if ($entry.PSIsContainer) {
            Copy-SourceTree -Source $entry.FullName -Destination $target
        }
        else {
            Copy-Item -LiteralPath $entry.FullName -Destination $target -Force
        }
    }
}

function Get-LockTargetJson {
    param([Parameter(Mandatory = $true)] [string]$Path)

    $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $target = $document.dependencies.PSObject.Properties['net10.0']
    if ($null -eq $target) {
        throw "PUBLISH_LOCK_BASELINE_INVALID: net10.0 target missing in $Path"
    }

    return $target.Value | ConvertTo-Json -Depth 100 -Compress
}

function Assert-LockBaselines {
    param(
        [Parameter(Mandatory = $true)] [string]$TemporarySourceRoot,
        [Parameter(Mandatory = $true)] [string]$OriginalRoot,
        [Parameter(Mandatory = $true)] [string]$RuntimeIdentifier
    )

    foreach ($temporaryLock in Get-ChildItem -LiteralPath $TemporarySourceRoot -File -Recurse -Filter 'packages.lock.json') {
        $relative = [IO.Path]::GetRelativePath($TemporarySourceRoot, $temporaryLock.FullName)
        $originalLock = Join-Path $OriginalRoot $relative
        if (-not (Test-Path -LiteralPath $originalLock -PathType Leaf)) {
            throw "PUBLISH_LOCK_BASELINE_UNEXPECTED: $relative"
        }

        if ((Get-LockTargetJson -Path $temporaryLock.FullName) -cne (Get-LockTargetJson -Path $originalLock)) {
            throw "PUBLISH_LOCK_BASELINE_CHANGED: $relative"
        }

        $document = Get-Content -LiteralPath $temporaryLock.FullName -Raw | ConvertFrom-Json
        $runtimeTarget = $document.dependencies.PSObject.Properties["net10.0/$RuntimeIdentifier"]
        if ($null -eq $runtimeTarget) {
            throw "PUBLISH_LOCK_RUNTIME_TARGET_MISSING: $relative"
        }
    }
}

Copy-Item -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Destination (Join-Path $sourceRoot 'Directory.Build.props') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'Directory.Packages.props') -Destination (Join-Path $sourceRoot 'Directory.Packages.props') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'global.json') -Destination (Join-Path $sourceRoot 'global.json') -Force
$nugetConfig = Join-Path $repoRoot 'NuGet.Config'
if (Test-Path -LiteralPath $nugetConfig -PathType Leaf) {
    Copy-Item -LiteralPath $nugetConfig -Destination (Join-Path $sourceRoot 'NuGet.Config') -Force
}
Copy-SourceTree -Source (Join-Path $repoRoot 'src') -Destination (Join-Path $sourceRoot 'src')

$sourceDesktopProject = Join-Path $sourceRoot 'src/Lacertae.Desktop/Lacertae.Desktop.csproj'
$sourceUpdaterProject = Join-Path $sourceRoot 'src/Lacertae.Updater/Lacertae.Updater.csproj'

Push-Location $sourceRoot
try {
    dotnet restore $sourceDesktopProject -r $Runtime --force-evaluate
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish-source restore failed.' }
    dotnet restore $sourceUpdaterProject -r $Runtime --force-evaluate
    if ($LASTEXITCODE -ne 0) { throw 'Updater publish-source restore failed.' }
    Assert-LockBaselines -TemporarySourceRoot $sourceRoot -OriginalRoot $repoRoot -RuntimeIdentifier $Runtime

    dotnet publish $sourceDesktopProject -c Release -r $Runtime --self-contained true --no-restore -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version -o $appPublish
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
    dotnet publish $sourceUpdaterProject -c Release -r $Runtime --self-contained true --no-restore -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version -o $updaterPublish
    if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed.' }
}
finally {
    Pop-Location
}

Copy-Item -Path (Join-Path $appPublish '*') -Destination $packageRoot -Recurse -Force
$updaterRoot = Join-Path $packageRoot 'Updater'
New-Item -ItemType Directory -Force -Path $updaterRoot | Out-Null
Copy-Item -Path (Join-Path $updaterPublish '*') -Destination $updaterRoot -Recurse -Force
# Some native/UI dependencies publish their own symbols even when the application
# uses DebugType=None. Symbols are not needed at runtime and must not enter release ZIPs.
Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Filter '*.pdb' | Remove-Item -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $packageRoot 'THIRD-PARTY-NOTICES.txt') -Force
Copy-Item -LiteralPath $license -Destination (Join-Path $packageRoot 'LICENSE') -Force

# Generate release metadata from the production lock graph. The source lock files
# remain untouched; the temporary publish source carries the RID-specific graph.
& (Join-Path $PSScriptRoot 'generate-sbom.ps1') `
    -LockRoot (Join-Path $repoRoot 'src') `
    -SbomPath (Join-Path $packageRoot 'sbom.cdx.json') `
    -NoticesPath (Join-Path $packageRoot 'THIRD-PARTY-NOTICES.txt') `
    -ApplicationVersion $Version | Out-Null

& (Join-Path $PSScriptRoot 'package-manifest.ps1') -PackageDirectory $packageRoot | Out-Null
$zipPath = Join-Path $outputRoot ("Lacertae-$Runtime-$Version.zip")
Add-Type -AssemblyName System.IO.Compression
$zipTimestamp = [DateTimeOffset]::FromUnixTimeSeconds($releaseEpoch)
if ($zipTimestamp -lt [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)) { $zipTimestamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero) }
$zipStream = [IO.File]::Create($zipPath)
$archive = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create, $false)
try {
    $packageFiles = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse)
    [Array]::Sort($packageFiles, [System.Comparison[object]]{ param($left, $right)
        [StringComparer]::Ordinal.Compare(
            [IO.Path]::GetRelativePath($packageRoot, $left.FullName).Replace('\', '/'),
            [IO.Path]::GetRelativePath($packageRoot, $right.FullName).Replace('\', '/'))
    })
    foreach ($file in $packageFiles) {
        $entryName = [IO.Path]::GetRelativePath($packageRoot, $file.FullName).Replace('\', '/')
        $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $zipTimestamp
        $input = [IO.File]::OpenRead($file.FullName)
        try { $output = $entry.Open(); try { $input.CopyTo($output) } finally { $output.Dispose() } } finally { $input.Dispose() }
    }
}
finally {
    $archive.Dispose()
    $zipStream.Dispose()
}

Remove-Item -LiteralPath $workRoot -Recurse -Force

Write-Output ([ordered]@{
    runtime = $Runtime
    version = $Version
    packageDirectory = $packageRoot
    zip = $zipPath
} | ConvertTo-Json -Compress)
