[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "lacertae-release-tooling-$([Guid]::NewGuid().ToString('N'))"

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

    $lockRoot = Join-Path $testRoot 'src'
    $cacheRoot = Join-Path $testRoot 'nuget'
    New-Item -ItemType Directory -Force -Path $lockRoot, $cacheRoot | Out-Null
    $packageRoot = Join-Path $cacheRoot 'sample.package/1.2.3'
    New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $packageRoot 'sample.package.nuspec'),
        @'
<?xml version="1.0" encoding="utf-8"?>
<package>
  <metadata>
    <id>Sample.Package</id>
    <version>1.2.3</version>
    <authors>Example</authors>
    <description>Fixture</description>
    <license type="expression">MIT</license>
    <projectUrl>https://example.invalid/sample</projectUrl>
  </metadata>
</package>
'@,
        [Text.UTF8Encoding]::new($false))
    New-Item -ItemType Directory -Force -Path (Join-Path $lockRoot 'Sample') | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $lockRoot 'Sample/packages.lock.json'),
        @'
{
  "version": 2,
  "dependencies": {
    "net10.0": {
      "Sample.Package": {
        "type": "Direct",
        "requested": "[1.2.3, )",
        "resolved": "1.2.3",
        "contentHash": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
      }
    }
  }
}
'@,
        [Text.UTF8Encoding]::new($false))

    $sbomPath = Join-Path $testRoot 'sbom.cdx.json'
    $noticesPath = Join-Path $testRoot 'THIRD-PARTY-NOTICES.txt'
    & (Join-Path $repoRoot 'eng/generate-sbom.ps1') -LockRoot $lockRoot -NuGetCache $cacheRoot -SbomPath $sbomPath -NoticesPath $noticesPath | Out-Null
    $fixtureBom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json
    Assert-True (@($fixtureBom.components).Count -eq 1) 'SBOM must contain the locked production package.'
    $fixtureNotices = Get-Content -LiteralPath $noticesPath -Raw
    Assert-True ($fixtureNotices -match 'Package: Sample\.Package\r?\nVersion: 1\.2\.3') 'Notices must contain package ID and version.'

    $manifestPackage = Join-Path $testRoot 'manifest-order'
    New-Item -ItemType Directory -Force -Path $manifestPackage | Out-Null
    foreach ($name in @('B.txt', 'a.txt', 'Z.txt', 'é.txt')) {
        [IO.File]::WriteAllText((Join-Path $manifestPackage $name), $name, [Text.UTF8Encoding]::new($false))
    }
    & (Join-Path $repoRoot 'eng/package-manifest.ps1') -PackageDirectory $manifestPackage | Out-Null
    $manifest = Get-Content -LiteralPath (Join-Path $manifestPackage 'package-manifest.json') -Raw | ConvertFrom-Json
    $manifestPaths = @($manifest.files | ForEach-Object path)
    Assert-True (($manifestPaths -join '|') -eq 'B.txt|Z.txt|a.txt|é.txt') 'Manifest paths must be sorted with ordinal comparison.'

    $invalidPackage = Join-Path $testRoot 'invalid-package'
    New-Item -ItemType Directory -Force -Path (Join-Path $invalidPackage 'Updater') | Out-Null
    foreach ($path in @(
            'Lacertae.Desktop.exe',
            'Lacertae.Desktop.dll',
            'Lacertae.Desktop.deps.json',
            'Lacertae.Desktop.runtimeconfig.json',
            'Updater/Lacertae.Updater.exe',
            'Updater/Lacertae.Updater.dll',
            'Updater/Lacertae.Updater.deps.json',
            'Updater/Lacertae.Updater.runtimeconfig.json',
            'coreclr.dll',
            'hostpolicy.dll',
            'Updater/coreclr.dll',
            'Updater/hostpolicy.dll',
            'LICENSE',
            'THIRD-PARTY-NOTICES.txt',
            'sbom.cdx.json')) {
        $target = Join-Path $invalidPackage ($path.Replace('/', [IO.Path]::DirectorySeparatorChar))
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        [IO.File]::WriteAllText($target, 'fixture', [Text.UTF8Encoding]::new($false))
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $invalidPackage 'LICENSE') -Force
    $pe = [byte[]](0..511 | ForEach-Object { 0 })
    $pe[0] = 0x4d; $pe[1] = 0x5a
    [BitConverter]::GetBytes([int]0x80).CopyTo($pe, 0x3c)
    $pe[0x80] = 0x50; $pe[0x81] = 0x45
    [BitConverter]::GetBytes([uint16]0x8664).CopyTo($pe, 0x84)
    [IO.File]::WriteAllBytes((Join-Path $invalidPackage 'Lacertae.Desktop.exe'), $pe)
    [IO.File]::WriteAllBytes((Join-Path $invalidPackage 'Updater/Lacertae.Updater.exe'), $pe)
    [IO.File]::WriteAllText((Join-Path $invalidPackage 'THIRD-PARTY-NOTICES.txt'), 'Lacertae notices', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $invalidPackage 'sbom.cdx.json'), '{"bomFormat":"CycloneDX","specVersion":"1.5","version":1,"metadata":{"timestamp":"2023-01-01T00:00:00Z"},"components":[]}', [Text.UTF8Encoding]::new($false))
    & (Join-Path $repoRoot 'eng/package-manifest.ps1') -PackageDirectory $invalidPackage | Out-Null
    $validResult = & (Join-Path $repoRoot 'eng/verify-package.ps1') -PackageDirectory $invalidPackage | ConvertFrom-Json
    Assert-True ($validResult.status -eq 'ok') 'A valid minimal x64 package must pass verification.'
    [IO.File]::WriteAllText((Join-Path $invalidPackage 'oauth.local.json'), '{"clientId":"11111111-1111-1111-1111-111111111111"}', [Text.UTF8Encoding]::new($false))
    & (Join-Path $repoRoot 'eng/package-manifest.ps1') -PackageDirectory $invalidPackage | Out-Null
    Assert-FailsWith {
        & (Join-Path $repoRoot 'eng/verify-package.ps1') -PackageDirectory $invalidPackage
    } 'PACKAGE_FORBIDDEN_PATH'
    Remove-Item -LiteralPath (Join-Path $invalidPackage 'oauth.local.json') -Force
    [IO.File]::WriteAllText((Join-Path $invalidPackage 'secret.txt'), 'Bearer abcdefghijklmnop', [Text.UTF8Encoding]::new($false))
    & (Join-Path $repoRoot 'eng/package-manifest.ps1') -PackageDirectory $invalidPackage | Out-Null
    Assert-FailsWith {
        & (Join-Path $repoRoot 'eng/verify-package.ps1') -PackageDirectory $invalidPackage
    } 'PACKAGE_SECRET_PATTERN'

    Remove-Item -LiteralPath (Join-Path $invalidPackage 'secret.txt') -Force
    Remove-Item -LiteralPath (Join-Path $invalidPackage 'Updater/Lacertae.Updater.dll') -Force
    & (Join-Path $repoRoot 'eng/package-manifest.ps1') -PackageDirectory $invalidPackage | Out-Null
    Assert-FailsWith { & (Join-Path $repoRoot 'eng/verify-package.ps1') -PackageDirectory $invalidPackage } 'PACKAGE_RUNTIME_FILE_MISSING'

    $publishScript = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/publish.ps1') -Raw
    Assert-True ($publishScript -match 'Copy-SourceTree') 'Publish must build from an isolated source copy.'
    Assert-True ($publishScript -match 'Assert-LockBaselines') 'Publish must compare the temporary lock graph with the repository baseline.'
    Assert-True ($publishScript -match 'ZipArchive') 'Publish must use the deterministic ZipArchive path.'
    Assert-True ($publishScript -match 'SOURCE_DATE_EPOCH_REQUIRED') 'Publish must require a deterministic timestamp.'
    $workflow = Get-Content -LiteralPath (Join-Path $repoRoot '.github/workflows/release-candidate.yml') -Raw
    Assert-True ($workflow -match 'source_date_epoch') 'Release workflow must provide SOURCE_DATE_EPOCH.'

    Write-Output 'release tooling tests: RED checks passed'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
