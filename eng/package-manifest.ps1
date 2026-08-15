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

$files = @(
    Get-ChildItem -LiteralPath $packageRoot -File -Recurse |
        Where-Object { $_.FullName -ne $manifestPath } |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/')
            if ($relative.Contains('..')) { throw "Package file escaped root: $relative" }
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            [pscustomobject]@{
                path = $relative
                size = [int64]$_.Length
                sha256 = $hash
            }
        } |
        Sort-Object -Property path
)

$document = [ordered]@{
    schemaVersion = 1
    files = @($files)
}
$json = $document | ConvertTo-Json -Depth 5 -Compress
$temporary = "$manifestPath.tmp"
[IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporary -Destination $manifestPath -Force
Write-Output $manifestPath
