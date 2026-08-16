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
$requiredRuntime = @(
    'Lacertae.Desktop.dll', 'Lacertae.Desktop.deps.json', 'Lacertae.Desktop.runtimeconfig.json',
    'Updater/Lacertae.Updater.dll', 'Updater/Lacertae.Updater.deps.json', 'Updater/Lacertae.Updater.runtimeconfig.json',
    'coreclr.dll', 'hostpolicy.dll', 'Updater/coreclr.dll', 'Updater/hostpolicy.dll'
)
foreach ($relative in $requiredRuntime) {
    $path = Join-Path $root ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "PACKAGE_RUNTIME_FILE_MISSING: $relative" }
}
function Get-RelativePath {
    param([Parameter(Mandatory = $true)] [string]$Path)
    return [IO.Path]::GetRelativePath($root, $Path).Replace('\', '/')
}

function Assert-WinX64Pe {
    param([Parameter(Mandatory = $true)] [string]$RelativePath)
    $path = Join-Path $root ($RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 0x40 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
        throw "PACKAGE_PE_INVALID: $RelativePath (missing MZ)"
    }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length -or $bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45 -or $bytes[$peOffset + 2] -ne 0 -or $bytes[$peOffset + 3] -ne 0) {
        throw "PACKAGE_PE_INVALID: $RelativePath (missing PE signature)"
    }
    $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
    if ($machine -ne 0x8664) { throw "PACKAGE_PE_INVALID: $RelativePath (machine 0x$('{0:X4}' -f $machine), expected x64)" }
}

Assert-WinX64Pe 'Lacertae.Desktop.exe'
Assert-WinX64Pe 'Updater/Lacertae.Updater.exe'
$packageLicenseText = Get-Content -LiteralPath (Join-Path $root 'LICENSE') -Raw
if ($packageLicenseText -notmatch 'Apache License\s+Version 2\.0') {
    throw 'PACKAGE_LICENSE_INVALID: the package root LICENSE must be Apache-2.0.'
}

$forbiddenNames = @('.git', '.superpowers', '@misc', 'lacertae.portable', 'LacertaeData', 'TestResults', 'TestOutput', 'obj', 'bin', 'source', 'sources', 'fixture', 'fixtures', 'test', 'tests', 'oauth', 'signing', 'database', 'databases', 'logs', 'log', 'cache', 'caches')
$files = @(Get-ChildItem -LiteralPath $root -File -Recurse)
$directories = @(Get-ChildItem -LiteralPath $root -Directory -Recurse)
foreach ($entry in @($files + $directories)) {
    $relative = Get-RelativePath $entry.FullName
    if ($entry.Name -match '^(?i:oauth\.local\.json|\.entra-id(?:\..*)?)$') {
        throw "PACKAGE_FORBIDDEN_PATH: $relative"
    }
    foreach ($forbidden in $forbiddenNames) {
        if ($relative -eq $forbidden -or $relative.StartsWith("$forbidden/", [StringComparison]::OrdinalIgnoreCase) -or $entry.Name -eq $forbidden) {
            throw "PACKAGE_FORBIDDEN_PATH: $relative"
        }
    }
    if ($relative -match '(^|/)(.*\.pdb|.*\.deps\.json\.bak|.*\.suo|.*\.trx|.*\.coverage|.*\.db|.*\.sqlite3?|.*\.log|.*\.dmp|.*\.bak)$') {
        throw "PACKAGE_DEBUG_ARTIFACT: $relative"
    }
}

$manifestPath = Join-Path $root 'package-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $null -eq $manifest.files) { throw 'PACKAGE_MANIFEST_INVALID' }
$listed = @{}
foreach ($item in @($manifest.files)) {
    $segments = if ($null -ne $item.path) { ([string]$item.path).Split('/') } else { @() }
    if ($null -eq $item.path -or $item.path.Contains('\') -or $item.path.StartsWith('/') -or @($segments | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }).Count -gt 0 -or $listed.ContainsKey($item.path)) {
        throw "PACKAGE_MANIFEST_PATH_INVALID: $($item.path)"
    }
    if ($item.path -cne ([string]$item.path).Replace('\', '/') -or [string]$item.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [int64]$item.size -lt 0) { throw "PACKAGE_MANIFEST_ENTRY_INVALID: $($item.path)" }
    $listed[$item.path] = $item
}

$manifestPaths = [System.Collections.Generic.List[string]]::new()
foreach ($item in @($manifest.files)) { [void]$manifestPaths.Add([string]$item.path) }
$sortedManifestPaths = [System.Collections.Generic.List[string]]::new()
foreach ($path in $manifestPaths) { [void]$sortedManifestPaths.Add($path) }
$sortedManifestPaths.Sort([StringComparer]::Ordinal)
for ($i = 0; $i -lt $manifestPaths.Count; $i++) {
    if ($manifestPaths[$i] -cne $sortedManifestPaths[$i]) { throw 'PACKAGE_MANIFEST_ORDER_INVALID' }
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

$actual = @($files | ForEach-Object { Get-RelativePath $_.FullName } | Where-Object { $_ -ne 'package-manifest.json' })
if ($actual.Count -ne $listed.Count -or @($actual | Where-Object { -not $listed.ContainsKey($_) }).Count -ne 0) {
    throw 'PACKAGE_MANIFEST_SET_MISMATCH'
}

$patterns = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'secret-patterns.txt') | Where-Object { $_ -and -not $_.StartsWith('#') })
foreach ($file in $files) {
    $reader = [IO.StreamReader]::new($file.FullName, [Text.Encoding]::UTF8, $true, 1MB)
    try {
        $carry = ''
        while (-not $reader.EndOfStream) {
            $chunk = New-Object char[] 1048576
            $count = $reader.Read($chunk, 0, $chunk.Length)
            if ($count -le 0) { break }
            $text = $carry + [string]::new($chunk, 0, $count)
            foreach ($pattern in $patterns) {
                if ($text -match $pattern) { throw "PACKAGE_SECRET_PATTERN: $(Get-RelativePath $file.FullName)" }
            }
            $carry = if ($text.Length -gt 1024) { $text.Substring($text.Length - 1024) } else { $text }
        }
    }
    finally {
        $reader.Dispose()
    }
}

try { $bom = Get-Content -LiteralPath (Join-Path $root 'sbom.cdx.json') -Raw | ConvertFrom-Json }
catch { throw "PACKAGE_SBOM_INVALID: $($_.Exception.Message)" }
if ($bom.bomFormat -ne 'CycloneDX' -or $bom.specVersion -notmatch '^1\.' -or $null -eq $bom.metadata -or $null -eq $bom.components) { throw 'PACKAGE_SBOM_INVALID: invalid CycloneDX document.' }
$noticesText = Get-Content -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.txt') -Raw
foreach ($component in @($bom.components)) {
    if ([string]::IsNullOrWhiteSpace([string]$component.name) -or [string]::IsNullOrWhiteSpace([string]$component.version)) { throw 'PACKAGE_SBOM_INVALID: component lacks name/version.' }
    $markerPattern = "(?im)^Package:\s*$( [regex]::Escape([string]$component.name) )\s*$\r?\nVersion:\s*$( [regex]::Escape([string]$component.version) )\s*$"
    if ($noticesText -notmatch $markerPattern) {
        throw "PACKAGE_LICENSE_COVERAGE: $($component.name) $($component.version)"
    }
}

Write-Output ([ordered]@{ status = 'ok'; packageDirectory = $root; fileCount = $listed.Count } | ConvertTo-Json -Compress)
