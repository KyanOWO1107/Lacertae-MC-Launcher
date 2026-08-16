[CmdletBinding()]
param(
    [string]$InputPath,
    [Parameter(Mandatory = $true)]
    [string]$ExecutableDirectory,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($InputPath)) {
    $InputPath = Join-Path $repoRoot '.entra-id'
}

$inputFullPath = [IO.Path]::GetFullPath($InputPath)
$outputDirectory = [IO.Path]::GetFullPath($ExecutableDirectory)
if (-not (Test-Path -LiteralPath $inputFullPath -PathType Leaf)) {
    throw "ENTRA_INPUT_MISSING: $inputFullPath"
}
if ([IO.Path]::GetPathRoot($outputDirectory) -eq $outputDirectory) {
    throw 'OAUTH_OUTPUT_DIRECTORY_TOO_BROAD'
}
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

function Convert-ToGuidText {
    param(
        [Parameter(Mandatory = $true)] [string]$Name,
        [Parameter(Mandatory = $true)] [string]$Value
    )

    [Guid]$guid = [Guid]::Empty
    if (-not [Guid]::TryParse($Value.Trim(), [ref]$guid) -or $guid -eq [Guid]::Empty) {
        throw "ENTRA_$($Name.ToUpperInvariant())_INVALID"
    }

    return $guid.ToString('D')
}

function Get-TextLabelValue {
    param(
        [Parameter(Mandatory = $true)] [string[]]$Lines,
        [Parameter(Mandatory = $true)] [string[]]$Labels,
        [Parameter(Mandatory = $true)] [string]$Name,
        [switch]$Required
    )

    $labelIndex = -1
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        $candidate = $Lines[$index].Trim()
        if ($Labels -contains $candidate) {
            if ($labelIndex -ge 0) {
                throw "ENTRA_$($Name.ToUpperInvariant())_DUPLICATE"
            }

            $labelIndex = $index
        }
    }

    if ($labelIndex -lt 0) {
        if ($Required) {
            throw "ENTRA_$($Name.ToUpperInvariant())_MISSING"
        }

        return $null
    }

    for ($index = $labelIndex + 1; $index -lt $Lines.Count; $index++) {
        $value = $Lines[$index].Trim()
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }
        if ($Labels -contains $value) {
            break
        }

        return $value
    }

    if ($Required) {
        throw "ENTRA_$($Name.ToUpperInvariant())_VALUE_MISSING"
    }

    return $null
}

$raw = Get-Content -LiteralPath $inputFullPath -Raw -Encoding UTF8
$clientId = $null
$tenantId = $null
$objectId = $null
$displayName = $null
if ($raw.TrimStart().StartsWith('{', [StringComparison]::Ordinal)) {
    try {
        $document = $raw | ConvertFrom-Json
    }
    catch {
        throw "ENTRA_INPUT_INVALID: $($_.Exception.Message)"
    }

    $clientId = [string]$document.clientId
    $tenantId = [string]$document.tenantId
    $objectId = [string]$document.objectId
    $displayName = [string]$document.displayName
}
else {
    $lines = @($raw -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $clientId = Get-TextLabelValue -Lines $lines -Labels @('应用程序(客户端) ID', '应用程序 (客户端) ID') -Name 'client_id' -Required
    $tenantId = Get-TextLabelValue -Lines $lines -Labels @('目录(租户) ID', '目录 (租户) ID') -Name 'tenant_id' -Required
    $objectId = Get-TextLabelValue -Lines $lines -Labels @('对象 ID') -Name 'object_id'
    $displayName = Get-TextLabelValue -Lines $lines -Labels @('显示名称') -Name 'display_name'
}

$clientId = Convert-ToGuidText -Name 'client_id' -Value ([string]$clientId)
$tenantId = Convert-ToGuidText -Name 'tenant_id' -Value ([string]$tenantId)
if (-not [string]::IsNullOrWhiteSpace([string]$objectId)) {
    $objectId = Convert-ToGuidText -Name 'object_id' -Value ([string]$objectId)
}

$outputPath = Join-Path $outputDirectory 'oauth.local.json'
if ((Test-Path -LiteralPath $outputPath -PathType Leaf) -and -not $Force) {
    throw "OAUTH_LOCAL_EXISTS: use -Force only when intentionally replacing $outputPath"
}

# The runtime loader deliberately accepts only the public client ID and the
# fixed consumer authority. Tenant/object IDs are retained in the ignored
# source export for the AppID review form, but must not enter runtime config.
$runtimeDocument = [ordered]@{
    clientId = $clientId
    authority = 'https://login.microsoftonline.com/consumers'
}
$temporaryPath = Join-Path $outputDirectory ('.oauth.local.' + [Guid]::NewGuid().ToString('N') + '.tmp')
[IO.File]::WriteAllText(
    $temporaryPath,
    ($runtimeDocument | ConvertTo-Json -Depth 3),
    [Text.UTF8Encoding]::new($false))
try {
    Move-Item -LiteralPath $temporaryPath -Destination $outputPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

Write-Output ([ordered]@{
    outputPath = $outputPath
    displayName = if ([string]::IsNullOrWhiteSpace([string]$displayName)) { $null } else { [string]$displayName }
    clientIdPresent = $true
    tenantIdPresent = $true
    objectIdPresent = -not [string]::IsNullOrWhiteSpace([string]$objectId)
    runtimeFields = @('clientId', 'authority')
} | ConvertTo-Json -Compress)
