[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$scriptPath = Join-Path $repoRoot 'eng/prepare-oauth-local.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "lacertae-entra-registration-$([Guid]::NewGuid().ToString('N'))"

function Assert-True {
    param(
        [Parameter(Mandatory = $true)] [bool]$Condition,
        [Parameter(Mandatory = $true)] [string]$Message
    )

    if (-not $Condition) {
        throw "ASSERTION_FAILED: $Message"
    }
}

function Assert-FailsWith {
    param(
        [Parameter(Mandatory = $true)] [scriptblock]$Action,
        [Parameter(Mandatory = $true)] [string]$Pattern
    )

    try {
        & $Action
    }
    catch {
        Assert-True ($_.Exception.Message -match $Pattern) "Expected '$Pattern', got '$($_.Exception.Message)'"
        return
    }

    throw "ASSERTION_FAILED: expected failure matching '$Pattern'"
}

try {
    New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
    $inputPath = Join-Path $testRoot '.entra-id'
    $outputDirectory = Join-Path $testRoot 'package'
    [IO.File]::WriteAllText(
        $inputPath,
        @'
显示名称
Lacertae Launcher
应用程序(客户端) ID
11111111-1111-1111-1111-111111111111
对象 ID
22222222-2222-2222-2222-222222222222
目录(租户) ID
33333333-3333-3333-3333-333333333333
'@,
        [Text.UTF8Encoding]::new($false))

    $result = & $scriptPath -InputPath $inputPath -ExecutableDirectory $outputDirectory | ConvertFrom-Json
    Assert-True ($result.runtimeFields -join ',' -eq 'clientId,authority') 'Runtime config must expose only the approved fields.'
    $runtime = Get-Content -LiteralPath (Join-Path $outputDirectory 'oauth.local.json') -Raw | ConvertFrom-Json
    Assert-True ($runtime.clientId -eq '11111111-1111-1111-1111-111111111111') 'Client ID must be normalized into runtime config.'
    Assert-True ($runtime.authority -eq 'https://login.microsoftonline.com/consumers') 'Runtime authority must be fixed to consumers.'
    Assert-True ($null -eq $runtime.tenantId) 'Tenant ID must not be copied into runtime config.'
    Assert-FailsWith {
        & $scriptPath -InputPath $inputPath -ExecutableDirectory $outputDirectory
    } 'OAUTH_LOCAL_EXISTS'

    $invalidPath = Join-Path $testRoot 'invalid-entraid'
    [IO.File]::WriteAllText($invalidPath, "应用程序(客户端) ID`nnot-a-guid`n目录(租户) ID`n33333333-3333-3333-3333-333333333333", [Text.UTF8Encoding]::new($false))
    Assert-FailsWith {
        & $scriptPath -InputPath $invalidPath -ExecutableDirectory (Join-Path $testRoot 'invalid-output')
    } 'ENTRA_CLIENT_ID_INVALID'

    Write-Output 'entra registration tests: passed'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
