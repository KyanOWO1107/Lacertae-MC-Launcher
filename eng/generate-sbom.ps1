[CmdletBinding()]
param(
    [string]$LockRoot,
    [string]$NuGetCache,
    [Parameter(Mandatory = $true)]
    [string]$SbomPath,
    [Parameter(Mandatory = $true)]
    [string]$NoticesPath,
    [string]$ApplicationVersion = '0.0.0',
    [string]$Timestamp
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Get-ReleaseTimestamp {
    param([string]$Requested)
    $value = if (-not [string]::IsNullOrWhiteSpace($Requested)) { $Requested } else { $env:SOURCE_DATE_EPOCH }
    if ([string]::IsNullOrWhiteSpace($value)) {
        return [DateTimeOffset]::UtcNow.ToString('o')
    }
    [long]$seconds = 0
    if (-not [long]::TryParse($value, [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$seconds) -or $seconds -lt 0) {
        throw 'SOURCE_DATE_EPOCH_INVALID: expected a non-negative Unix timestamp.'
    }
    return [DateTimeOffset]::FromUnixTimeSeconds($seconds).ToString('o')
}

$releaseTimestamp = Get-ReleaseTimestamp $Timestamp

function Get-FullPath {
    param([Parameter(Mandatory = $true)] [string]$Path)

    return [IO.Path]::GetFullPath($Path)
}

function Sort-Ordinal {
    param([Parameter(Mandatory = $true)] [System.Collections.Generic.List[object]]$Items)

    $comparison = [System.Comparison[object]]{
        param($left, $right)

        $idComparison = [StringComparer]::OrdinalIgnoreCase.Compare([string]$left.id, [string]$right.id)
        if ($idComparison -ne 0) {
            return $idComparison
        }

        return [StringComparer]::Ordinal.Compare([string]$left.version, [string]$right.version)
    }
    $Items.Sort($comparison)
    return $Items
}

function Resolve-NuGetCache {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = Get-FullPath $RequestedPath
        if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
            throw "NUGET_CACHE_MISSING: $resolved"
        }
        return $resolved
    }

    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        $fromEnvironment = Get-FullPath $env:NUGET_PACKAGES
        if (Test-Path -LiteralPath $fromEnvironment -PathType Container) {
            return $fromEnvironment
        }
    }

    $nugetLocals = & dotnet nuget locals global-packages --list 2>$null
    if ($LASTEXITCODE -eq 0) {
        $line = @($nugetLocals | Where-Object { $_ -match '^global-packages\s*:' }) | Select-Object -First 1
        if ($null -ne $line) {
            $candidate = ($line -replace '^global-packages\s*:\s*', '').Trim()
            if (-not [string]::IsNullOrWhiteSpace($candidate)) {
                $resolvedCandidate = Get-FullPath $candidate
                if (Test-Path -LiteralPath $resolvedCandidate -PathType Container) {
                    return $resolvedCandidate
                }
            }
        }
    }

    throw 'NUGET_CACHE_MISSING: restore the locked solution before generating release metadata.'
}

function Get-PackageMetadata {
    param(
        [Parameter(Mandatory = $true)] [string]$Id,
        [Parameter(Mandatory = $true)] [string]$Version,
        [Parameter(Mandatory = $true)] [string]$CacheRoot
    )

    $packageRoot = Join-Path $CacheRoot $Id.ToLowerInvariant()
    $versionRoot = Join-Path $packageRoot $Version.ToLowerInvariant()
    if (-not (Test-Path -LiteralPath $versionRoot -PathType Container)) {
        throw "NUGET_PACKAGE_MISSING: $Id $Version ($versionRoot)"
    }

    $nuspec = Get-ChildItem -LiteralPath $versionRoot -File -Filter '*.nuspec' |
        Sort-Object -Property Name |
        Select-Object -First 1
    if ($null -eq $nuspec) {
        throw "NUGET_METADATA_MISSING: $Id $Version"
    }

    try {
        [xml]$document = Get-Content -LiteralPath $nuspec.FullName -Raw -Encoding UTF8
    }
    catch {
        throw "NUGET_METADATA_INVALID: $Id $Version ($($_.Exception.Message))"
    }

    $metadata = $document.package.metadata
    if ($null -eq $metadata) {
        throw "NUGET_METADATA_INVALID: $Id $Version has no metadata"
    }

    $licenseNode = $metadata.license
    $license = if ($null -ne $licenseNode) { ([string]$licenseNode.InnerText).Trim() } else { '' }
    $licenseType = if ($null -ne $licenseNode) { ([string]$licenseNode.type).Trim() } else { '' }
    $licenseUrl = ([string]$metadata.licenseUrl).Trim()
    if ([string]::IsNullOrWhiteSpace($license)) {
        if (-not [string]::IsNullOrWhiteSpace($licenseUrl)) {
            $license = 'SEE LICENSE URL'
        }
        else {
            throw "NUGET_LICENSE_MISSING: $Id $Version"
        }
    }

    $projectUrl = ([string]$metadata.projectUrl).Trim()
    if ([string]::IsNullOrWhiteSpace($projectUrl)) {
        $projectUrl = "https://www.nuget.org/packages/$Id/$Version"
    }

    [ordered]@{
        license = $license
        licenseType = $licenseType
        licenseUrl = $licenseUrl
        projectUrl = $projectUrl
        authors = ([string]$metadata.authors).Trim()
    }
}

if ([string]::IsNullOrWhiteSpace($LockRoot)) {
    $LockRoot = Join-Path $repoRoot 'src'
}
$lockRoot = Get-FullPath $LockRoot
if (-not (Test-Path -LiteralPath $lockRoot -PathType Container)) {
    throw "PACKAGE_LOCK_ROOT_MISSING: $lockRoot"
}

$sbomPath = Get-FullPath $SbomPath
$noticesPath = Get-FullPath $NoticesPath
$lockFiles = @(Get-ChildItem -LiteralPath $lockRoot -Recurse -File -Filter 'packages.lock.json' | Sort-Object -Property FullName)
if ($lockFiles.Count -eq 0) {
    throw "PACKAGE_LOCK_FILES_MISSING: $lockRoot"
}

$packageMap = @{}
foreach ($lockFile in $lockFiles) {
    try {
        $lock = Get-Content -LiteralPath $lockFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "PACKAGE_LOCK_INVALID: $($lockFile.FullName) ($($_.Exception.Message))"
    }

    if ($null -eq $lock.dependencies) {
        throw "PACKAGE_LOCK_INVALID: $($lockFile.FullName) has no dependencies"
    }

    foreach ($framework in ($lock.dependencies.PSObject.Properties | Sort-Object -Property Name)) {
        foreach ($packageProperty in ($framework.Value.PSObject.Properties | Sort-Object -Property Name)) {
            $entry = $packageProperty.Value
            if ([string]$entry.type -eq 'Project') {
                continue
            }
            $id = [string]$packageProperty.Name
            $version = ([string]$entry.resolved).Trim()
            if ([string]::IsNullOrWhiteSpace($version)) {
                throw "PACKAGE_LOCK_INVALID: $($lockFile.FullName) package $id has no resolved version"
            }

            $key = "$($id.ToLowerInvariant())|$version"
            if (-not $packageMap.ContainsKey($key)) {
                $packageMap[$key] = [ordered]@{
                    id = $id
                    version = $version
                    contentHash = ([string]$entry.contentHash).Trim()
                    direct = ([string]$entry.type -eq 'Direct')
                    types = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                    lockFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                }
            }
            else {
                $packageMap[$key].direct = $packageMap[$key].direct -or ([string]$entry.type -eq 'Direct')
            }
            [void]$packageMap[$key].types.Add(([string]$entry.type).Trim())
            [void]$packageMap[$key].lockFiles.Add($lockFile.FullName)
        }
    }
}

$cacheRoot = Resolve-NuGetCache $NuGetCache
$packages = [System.Collections.Generic.List[object]]::new()
foreach ($package in $packageMap.Values) {
    $metadata = Get-PackageMetadata -Id $package.id -Version $package.version -CacheRoot $cacheRoot
    $package.metadata = $metadata
    [void]$packages.Add([pscustomobject]$package)
}
Sort-Ordinal $packages | Out-Null

$components = [System.Collections.Generic.List[object]]::new()
$notice = [Text.StringBuilder]::new()
[void]$notice.AppendLine('Lacertae Minecraft Launcher - third-party notices')
[void]$notice.AppendLine('')
[void]$notice.AppendLine('This inventory is generated from production src/**/packages.lock.json files and local NuGet .nuspec metadata.')
[void]$notice.AppendLine('Test-only dependencies are intentionally excluded from the release package.')
[void]$notice.AppendLine('')

foreach ($package in $packages) {
    $ref = "pkg:nuget/$($package.id.ToLowerInvariant())@$($package.version)"
    $licenseName = [string]$package.metadata.license
    $licenseUrl = [string]$package.metadata.licenseUrl
    $componentLicense = [ordered]@{ name = $licenseName }
    if (-not [string]::IsNullOrWhiteSpace($licenseUrl)) {
        $componentLicense.url = $licenseUrl
    }
    $component = [ordered]@{
        type = 'library'
        name = $package.id
        version = $package.version
        'bom-ref' = $ref
        purl = $ref
        scope = 'required'
        licenses = @([ordered]@{ license = $componentLicense })
        externalReferences = @([ordered]@{ type = 'website'; url = $package.metadata.projectUrl })
            properties = @(
            [ordered]@{ name = 'lacertae.lock.type'; value = (($package.types | Sort-Object) -join ',') },
            [ordered]@{ name = 'nuget.contentHash'; value = $package.contentHash }
        )
    }
    if (-not [string]::IsNullOrWhiteSpace($package.contentHash)) {
        try {
            $hashBytes = [Convert]::FromBase64String($package.contentHash)
            if ($hashBytes.Length -eq 64) {
                $component.hashes = @([ordered]@{ alg = 'SHA-512'; content = ([BitConverter]::ToString($hashBytes).Replace('-', '').ToLowerInvariant()) })
            }
        }
        catch {
            throw "PACKAGE_LOCK_INVALID: contentHash for $($package.id) $($package.version) is not valid base64"
        }
    }
    [void]$components.Add($component)

    [void]$notice.AppendLine("Package: $($package.id)")
    [void]$notice.AppendLine("Version: $($package.version)")
    [void]$notice.AppendLine("License: $licenseName")
    if (-not [string]::IsNullOrWhiteSpace($licenseUrl)) {
        [void]$notice.AppendLine("License URL: $licenseUrl")
    }
    [void]$notice.AppendLine("Source: $($package.metadata.projectUrl)")
    [void]$notice.AppendLine('')
}

$bom = [ordered]@{
    bomFormat = 'CycloneDX'
    specVersion = '1.5'
    version = 1
    metadata = [ordered]@{
        timestamp = $releaseTimestamp
        component = [ordered]@{
            type = 'application'
            name = 'Lacertae Minecraft Launcher'
            version = $ApplicationVersion
        }
    }
    components = @($components)
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $sbomPath), (Split-Path -Parent $noticesPath) | Out-Null
$json = $bom | ConvertTo-Json -Depth 12 -Compress
[IO.File]::WriteAllText($sbomPath, $json, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($noticesPath, $notice.ToString(), [Text.UTF8Encoding]::new($false))

Write-Output ([ordered]@{
    sbom = $sbomPath
    notices = $noticesPath
    packageCount = $packages.Count
} | ConvertTo-Json -Compress)
