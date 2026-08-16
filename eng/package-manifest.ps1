[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
    throw "Package directory does not exist: $packageRoot"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $packageRoot 'package-manifest.json'
}
$manifestPath = [IO.Path]::GetFullPath($OutputPath)
$manifestDirectory = Split-Path -Parent $manifestPath
New-Item -ItemType Directory -Force -Path $manifestDirectory | Out-Null

$files = [System.Collections.Generic.List[object]]::new()
foreach ($file in (Get-ChildItem -LiteralPath $packageRoot -File -Recurse)) {
    if ($file.FullName -eq $manifestPath) {
        continue
    }

    $relative = [IO.Path]::GetRelativePath($packageRoot, $file.FullName).Replace('\', '/')
    if ($relative -eq '..' -or $relative.StartsWith('../', [StringComparison]::Ordinal)) {
        throw "Package file escaped root: $relative"
    }
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [void]$files.Add([pscustomobject]@{
            path = $relative
            size = [int64]$file.Length
            sha256 = $hash
        })
}

$files.Sort([System.Comparison[object]]{
        param($left, $right)

        [StringComparer]::Ordinal.Compare([string]$left.path, [string]$right.path)
    })

$document = [ordered]@{
    schemaVersion = 1
    files = @($files)
}
$json = $document | ConvertTo-Json -Depth 5 -Compress
$temporary = "$manifestPath.tmp"
[IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporary -Destination $manifestPath -Force
Write-Output $manifestPath
