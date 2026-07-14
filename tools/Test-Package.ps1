[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageRoot)

$ErrorActionPreference = "Stop"
$source = (Resolve-Path -LiteralPath $PackageRoot).Path
$tempInstall = Join-Path ([IO.Path]::GetTempPath()) ("sdat-package-test-" + [guid]::NewGuid().ToString("N"))

try {
    & (Join-Path $source "install.ps1") -SourcePath $source -InstallDir $tempInstall -NoPath
    foreach ($required in @("VERSION", "shutdownat.ps1", "sdat.bat", "modules\PwshSpectreConsole\2.6.3\PwshSpectreConsole.psd1")) {
        if (-not (Test-Path -LiteralPath (Join-Path $tempInstall $required))) { throw "Installed package is missing $required" }
    }
    "preserve-me" | Set-Content -LiteralPath (Join-Path $tempInstall "data\upgrade-marker.txt") -Encoding ASCII
    "remove-me" | Set-Content -LiteralPath (Join-Path $tempInstall "modules\stale-module.txt") -Encoding ASCII
    & (Join-Path $source "install.ps1") -SourcePath $source -InstallDir $tempInstall -Update -NoPath
    if ((Get-Content -LiteralPath (Join-Path $tempInstall "data\upgrade-marker.txt") -Raw).Trim() -ne "preserve-me") { throw "Upgrade did not preserve runtime data" }
    if (Test-Path -LiteralPath (Join-Path $tempInstall "modules\stale-module.txt")) { throw "Upgrade left stale shipped files behind" }

    $versionOutput = & (Join-Path $tempInstall "sdat.bat") version 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $versionOutput -notmatch 'SDAT\s+\d+\.\d+\.\d+') { throw "Installed version command failed: $versionOutput" }

    $uninstallOutput = & (Join-Path $tempInstall "sdat.bat") uninstall -SkipTaskCleanup -NoPath 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $uninstallOutput -notmatch 'SDAT was removed') { throw "Installed uninstall command failed: $uninstallOutput" }
    $deadline = (Get-Date).AddSeconds(5)
    while ((Test-Path -LiteralPath $tempInstall) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (Test-Path -LiteralPath $tempInstall) { throw "Uninstaller left the install directory behind" }
    Write-Host "Package install, upgrade, version, and uninstall checks passed." -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $tempInstall) { Remove-Item -LiteralPath $tempInstall -Recurse -Force -ErrorAction SilentlyContinue }
}
