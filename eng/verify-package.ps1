[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PackageDirectory)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Package directory does not exist: $root"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$license = Join-Path $repoRoot 'LICENSE'
if (-not (Test-Path -LiteralPath $license -PathType Leaf)) {
    throw 'PROJECT_LICENSE_REQUIRED: add the approved root LICENSE before publishing.'
}
$licenseText = Get-Content -LiteralPath $license -Raw
if ($licenseText -notmatch 'Apache License\s+Version 2\.0') {
    throw 'PROJECT_LICENSE_INVALID: the root LICENSE must be Apache-2.0.'
}

$required = @('Lacertae.Desktop.exe', 'Updater/Lacertae.Updater.exe', 'package-manifest.json', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'sbom.cdx.json')
foreach ($relative in $required) {
    $path = Join-Path $root ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "PACKAGE_REQUIRED_FILE_MISSING: $relative" }
}

$forbiddenNames = @('.git', '.superpowers', '@misc', 'lacertae.portable', 'LacertaeData', 'TestResults', 'obj', 'bin')
$files = @(Get-ChildItem -LiteralPath $root -File -Recurse)
$directories = @(Get-ChildItem -LiteralPath $root -Directory -Recurse)
foreach ($entry in @($files + $directories)) {
    $relative = [IO.Path]::GetRelativePath($root, $entry.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/')
    foreach ($forbidden in $forbiddenNames) {
        if ($relative -eq $forbidden -or $relative.StartsWith("$forbidden/", [StringComparison]::OrdinalIgnoreCase) -or $entry.Name -eq $forbidden) {
            throw "PACKAGE_FORBIDDEN_PATH: $relative"
        }
    }
    if ($relative -match '(^|/)(.*\.pdb|.*\.deps\.json\.bak|.*\.suo)$') {
        throw "PACKAGE_DEBUG_ARTIFACT: $relative"
    }
}

$manifestPath = Join-Path $root 'package-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $null -eq $manifest.files) { throw 'PACKAGE_MANIFEST_INVALID' }
$listed = @{}
foreach ($item in @($manifest.files)) {
    if ($null -eq $item.path -or $item.path.Contains('\') -or $item.path.StartsWith('/') -or $item.path.Contains('..') -or $listed.ContainsKey($item.path)) {
        throw "PACKAGE_MANIFEST_PATH_INVALID: $($item.path)"
    }
    $listed[$item.path] = $item
}

foreach ($relative in $listed.Keys) {
    $path = Join-Path $root ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "PACKAGE_MANIFEST_FILE_MISSING: $relative" }
    $info = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([int64]$info.Length -ne [int64]$listed[$relative].size -or $hash -ne $listed[$relative].sha256.ToLowerInvariant()) {
        throw "PACKAGE_MANIFEST_HASH_MISMATCH: $relative"
    }
}

$actual = @($files | ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/') } | Where-Object { $_ -ne 'package-manifest.json' })
if ($actual.Count -ne $listed.Count -or @($actual | Where-Object { -not $listed.ContainsKey($_) }).Count -ne 0) {
    throw 'PACKAGE_MANIFEST_SET_MISMATCH'
}

$patterns = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'secret-patterns.txt') | Where-Object { $_ -and -not $_.StartsWith('#') })
foreach ($file in $files) {
    if ($file.Length -gt 5MB) { continue }
    $text = [IO.File]::ReadAllText($file.FullName)
    foreach ($pattern in $patterns) {
        if ($text -match $pattern) { throw "PACKAGE_SECRET_PATTERN: $([IO.Path]::GetRelativePath($root, $file.FullName))" }
    }
}

Write-Output ([ordered]@{ status = 'ok'; packageDirectory = $root; fileCount = $listed.Count } | ConvertTo-Json -Compress)
