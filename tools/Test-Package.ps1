[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageRoot)

$ErrorActionPreference = "Stop"
$source = (Resolve-Path -LiteralPath $PackageRoot).Path
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("sdat-package-test-" + [guid]::NewGuid().ToString("N"))
$tempInstall = Join-Path $sandbox "install"
$previousLocalAppData = $env:LOCALAPPDATA
$env:LOCALAPPDATA = Join-Path $sandbox "local-app-data"

try {
    & (Join-Path $source "install.ps1") -SourcePath $source -InstallDir $tempInstall -NoPath
    foreach ($required in @("VERSION", "SDAT.exe", "SDAT.pri", "sdat-cli.exe", "sdat.bat", ".sdat-package-manifest.json")) {
        if (-not (Test-Path -LiteralPath (Join-Path $tempInstall $required))) { throw "Installed package is missing $required" }
    }

    # Simulate the known v1 footprint and verify the native update preserves migration evidence.
    New-Item -ItemType Directory -Path (Join-Path $tempInstall "data") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $tempInstall "modules") -Force | Out-Null
    '{"Version":1,"Volatile":null,"Permanent":null,"SuspendPermanentUntil":null,"SuspendSetAt":null,"SuspendReason":null}' |
        Set-Content -LiteralPath (Join-Path $tempInstall "data\state.json") -Encoding UTF8
    "# legacy v1 marker" | Set-Content -LiteralPath (Join-Path $tempInstall "shutdownat.ps1") -Encoding ASCII
    "remove-me" | Set-Content -LiteralPath (Join-Path $tempInstall "modules\stale-module.txt") -Encoding ASCII
    & (Join-Path $source "install.ps1") -SourcePath $source -InstallDir $tempInstall -Update -NoPath
    if (Test-Path -LiteralPath (Join-Path $tempInstall "modules\stale-module.txt")) { throw "Upgrade left stale shipped files behind" }
    if (Test-Path -LiteralPath (Join-Path $tempInstall "shutdownat.ps1")) { throw "Upgrade left the v1 backend in the native install" }
    if (-not (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA "SDAT\legacy-v1\data\state.json"))) {
        throw "Upgrade did not preserve v1 migration state"
    }

    $versionOutput = & (Join-Path $tempInstall "sdat.bat") version 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $versionOutput -notmatch '\d+\.\d+\.\d+') { throw "Installed version command failed: $versionOutput" }

    & (Join-Path $tempInstall "uninstall.ps1") -InstallDir $tempInstall -SkipTaskCleanup -NoPath
    if (Test-Path -LiteralPath $tempInstall) { throw "Uninstaller left the install directory behind" }
    Write-Host "Package install, upgrade, version, and uninstall checks passed." -ForegroundColor Green
} finally {
    $env:LOCALAPPDATA = $previousLocalAppData
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue }
}
