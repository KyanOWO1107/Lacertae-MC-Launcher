[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    dotnet restore Lacertae.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed' }

    dotnet build Lacertae.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

    dotnet test Lacertae.slnx -c Release --no-build --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed' }

    dotnet format Lacertae.slnx --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet format failed' }

    git diff --check
    if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed' }
}
finally {
    Pop-Location
}
