[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageRoot)

$ErrorActionPreference = "Stop"
$sourceInput = (Resolve-Path -LiteralPath $PackageRoot).Path
$source = if (Test-Path -LiteralPath (Join-Path $sourceInput "scripts\install.ps1")) {
    $sourceInput
} else {
    $candidate = Get-ChildItem -LiteralPath $sourceInput -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "scripts\install.ps1") } |
        Select-Object -First 1
    if (-not $candidate) { throw "Structured SDAT package root was not found." }
    $candidate.FullName
}
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("sdat-package-test-" + [guid]::NewGuid().ToString("N"))
$tempInstall = Join-Path $sandbox "install"
$previousLocalAppData = $env:LOCALAPPDATA
$env:LOCALAPPDATA = Join-Path $sandbox "local-app-data"

try {
    foreach ($required in @("Install SDAT.cmd", "Uninstall SDAT.cmd", "README.txt", "app", "scripts", "docs")) {
        if (-not (Test-Path -LiteralPath (Join-Path $source $required))) { throw "Package root is missing $required" }
    }
    $packageManifest = Get-Content -LiteralPath (Join-Path $source "app\.sdat-package-manifest.json") -Raw | ConvertFrom-Json
    if ($packageManifest.SchemaVersion -ne 2 -or
        [string]::IsNullOrWhiteSpace([string]$packageManifest.DeploymentMode) -or
        [string]$packageManifest.DotNetRuntimeName -ne "Microsoft.NETCore.App" -or
        [version]$packageManifest.DotNetRuntimeVersion -lt [version]"10.0.0") {
        throw "Package deployment metadata is missing or invalid"
    }
    $installLauncher = Get-Content -LiteralPath (Join-Path $source "Install SDAT.cmd") -Raw
    if ($installLauncher -notmatch '\-SourcePath\s+"%~dp0\."\s+\-Launch') {
        throw "Clickable installer does not pass a safely terminated package path before requesting the setup UI"
    }
    if ((Get-Content -LiteralPath (Join-Path $source "Uninstall SDAT.cmd") -Raw) -notmatch '\-KeepData') {
        throw "Clickable uninstaller is not backup-first"
    }
    foreach ($forbidden in @("onnxruntime.dll", "DirectML.dll")) {
        if (Get-ChildItem -LiteralPath (Join-Path $source "app") -Recurse -File -Filter $forbidden -ErrorAction SilentlyContinue) {
            throw "Slim package unexpectedly contains $forbidden"
        }
    }
    foreach ($packageId in @(
        "Microsoft.WindowsAppSDK.Base",
        "Microsoft.WindowsAppSDK.Foundation",
        "Microsoft.WindowsAppSDK.InteractiveExperiences",
        "Microsoft.WindowsAppSDK.WinUI",
        "Microsoft.WindowsAppSDK.Runtime")) {
        if (-not (Test-Path -LiteralPath (Join-Path $source "app\licenses\$packageId-license.txt"))) {
            throw "Package is missing the license for $packageId"
        }
    }
    $cultureDirectories = @(Get-ChildItem -LiteralPath (Join-Path $source "app") -Directory |
        Where-Object { $_.Name -match '^[a-z]{2}(?:-[A-Z]{2})?$' })
    $unexpectedCultures = @($cultureDirectories | Where-Object { $_.Name -notin @("en", "it", "en-US", "it-IT") })
    if ($unexpectedCultures.Count -gt 0) {
        throw "Package contains unexpected satellite cultures: $($unexpectedCultures.Name -join ', ')"
    }

    $packageInstaller = Join-Path $source "scripts\install.ps1"
    & (Join-Path $PSScriptRoot "Test-InstallerInternals.ps1") -PackageRoot $source -SandboxRoot $sandbox

    $unrelated = Join-Path $sandbox "not-sdat"
    New-Item -ItemType Directory -Path $unrelated -Force | Out-Null
    "preserve-me" | Set-Content -LiteralPath (Join-Path $unrelated "sentinel.txt") -Encoding ASCII
    $unsafeInstallRejected = $false
    try {
        & $packageInstaller -SourcePath $source -InstallDir $unrelated -NoPath -NoShortcuts -SkipPrerequisites
    } catch {
        $unsafeInstallRejected = $true
    }
    if (-not $unsafeInstallRejected -or -not (Test-Path -LiteralPath (Join-Path $unrelated "sentinel.txt"))) {
        throw "Installer did not safely reject a non-SDAT target directory"
    }
    $unsafeUninstallRejected = $false
    try {
        & (Join-Path $source "scripts\uninstall.ps1") -InstallDir $unrelated -SkipTaskCleanup -NoPath -NoShortcuts
    } catch {
        $unsafeUninstallRejected = $true
    }
    if (-not $unsafeUninstallRejected -or -not (Test-Path -LiteralPath (Join-Path $unrelated "sentinel.txt"))) {
        throw "Uninstaller did not safely reject a non-SDAT target directory"
    }

    & $packageInstaller -SourcePath $source -InstallDir $tempInstall -NoPath -NoShortcuts -SkipPrerequisites
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
    "preserve-me" | Set-Content -LiteralPath (Join-Path $tempInstall "user-note.txt") -Encoding ASCII
    & $packageInstaller -SourcePath $source -InstallDir $tempInstall -Update -NoPath -NoShortcuts -SkipPrerequisites
    if (Test-Path -LiteralPath (Join-Path $tempInstall "modules\stale-module.txt")) { throw "Upgrade left stale shipped files behind" }
    if (Test-Path -LiteralPath (Join-Path $tempInstall "shutdownat.ps1")) { throw "Upgrade left the v1 backend in the native install" }
    if (-not (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA "SDAT\legacy-v1\data\state.json"))) {
        throw "Upgrade did not preserve v1 migration state"
    }
    $unknownFileBackup = @(Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA "SDAT\install-backups") -Recurse -File -Filter "user-note.txt" -ErrorAction SilentlyContinue |
        Select-Object -First 1)
    if ($unknownFileBackup.Count -ne 1 -or (Get-Content -LiteralPath $unknownFileBackup[0].FullName -Raw).Trim() -ne "preserve-me") {
        throw "Upgrade did not preserve a non-package file in a recoverable backup"
    }

    # This lifecycle deliberately skips shared prerequisite installation. Read
    # the installed PE metadata instead of loading a framework-dependent apphost
    # on a clean CI runner that does not have Windows App Runtime installed.
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $tempInstall "sdat-cli.dll"))
    $versionOutput = @($versionInfo.ProductVersion, $versionInfo.FileVersion) -join " "
    if ($versionOutput -notmatch '\d+\.\d+\.\d+') { throw "Installed version metadata is invalid: $versionOutput" }

    & (Join-Path $tempInstall "uninstall.ps1") -InstallDir $tempInstall -KeepData -SkipTaskCleanup -NoPath -NoShortcuts
    if (Test-Path -LiteralPath $tempInstall) { throw "Uninstaller left the install directory behind" }
    if (-not (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA "SDAT-uninstall-backups"))) {
        throw "Safe uninstall did not preserve a data backup"
    }
    Write-Host "Package layout, install, upgrade, version, and safe uninstall checks passed." -ForegroundColor Green
} finally {
    $env:LOCALAPPDATA = $previousLocalAppData
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue }
}
