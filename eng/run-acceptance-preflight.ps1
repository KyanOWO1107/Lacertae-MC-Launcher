[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AcceptanceRoot,
    [string]$PackagePath,
    [string]$PackageSha256
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($AcceptanceRoot)
if ([IO.Path]::GetPathRoot($root) -eq $root) { throw 'ACCEPTANCE_ROOT_TOO_BROAD' }
New-Item -ItemType Directory -Force -Path $root | Out-Null

$os = Get-CimInstance Win32_OperatingSystem
$architecture = (Get-CimInstance Win32_OperatingSystem).OSArchitecture
$isX64 = [Environment]::Is64BitOperatingSystem -and $env:PROCESSOR_ARCHITECTURE -match 'AMD64|ARM64'
$freeBytes = (Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$([IO.Path]::GetPathRoot($root).TrimEnd('\'))'").FreeSpace
$package = $null
if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
    $packageFull = [IO.Path]::GetFullPath($PackagePath)
    if (-not (Test-Path -LiteralPath $packageFull -PathType Leaf)) { throw "ACCEPTANCE_PACKAGE_MISSING: $packageFull" }
    $hash = (Get-FileHash -LiteralPath $packageFull -Algorithm SHA256).Hash.ToLowerInvariant()
    $package = [ordered]@{ path = $packageFull; sha256 = $hash; matchesExpected = ([string]::IsNullOrWhiteSpace($PackageSha256) -or $hash -eq $PackageSha256.ToLowerInvariant()) }
    if (-not $package.matchesExpected) { throw 'ACCEPTANCE_PACKAGE_HASH_MISMATCH' }
}

$processNames = @('Lacertae.Desktop', 'Lacertae.Updater')
$running = @(Get-Process -Name $processNames -ErrorAction SilentlyContinue | Select-Object -ExpandProperty ProcessName)
$java = @(Get-Command java -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
$result = [ordered]@{
    status = if ($os.Caption -match 'Windows 10|Windows 11' -and $isX64 -and $running.Count -eq 0) { 'ready' } else { 'blocked' }
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    operatingSystem = $os.Caption
    build = $os.BuildNumber
    architecture = $architecture
    powershell = $PSVersionTable.PSVersion.ToString()
    acceptanceRoot = $root
    freeBytes = [int64]$freeBytes
    package = $package
    runningLauncherProcesses = $running
    javaOnPath = $java
    note = 'This preflight creates only the explicit acceptance root and never removes game or AppData content.'
}
$result | ConvertTo-Json -Depth 8
