<#
    Install or update SDAT for the current Windows user.

    Fresh install from an extracted release:
      powershell -ExecutionPolicy Bypass -File .\install.ps1

    Install from a local checkout/package (used by tests):
      .\install.ps1 -SourcePath .
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\SDAT"),
    [string]$Version = "latest",
    [string]$SourcePath,
    [switch]$Update,
    [switch]$NoPath,
    [switch]$NoShortcuts,
    [switch]$Launch,
    [switch]$SkipPrerequisites,
    [switch]$ImportOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repository = "dummics/sdat-shutdownat"
$requiredFiles = @("VERSION", "SDAT.exe", "sdat-cli.exe", "bin\sdat.bat", "bin\ssat.bat", "sdatui.bat", "uninstall.ps1", ".sdat-package-manifest.json")
$tempRoot = $null

function Write-InstallStep {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host ("  {0}" -f $Message) -ForegroundColor Cyan
}

function Test-SdatPayloadRoot {
    param([Parameter(Mandatory)][string]$Path)
    foreach ($relative in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $relative))) { return $false }
    }
    return $true
}

function Find-SdatPackageRoot {
    param([Parameter(Mandatory)][string]$Path)
    if ((Test-SdatPayloadRoot -Path $Path) -or (Test-SdatPayloadRoot -Path (Join-Path $Path "app"))) {
        return (Resolve-Path -LiteralPath $Path).Path
    }
    $candidate = Get-ChildItem -LiteralPath $Path -Directory -ErrorAction SilentlyContinue |
        Where-Object { (Test-SdatPayloadRoot -Path $_.FullName) -or (Test-SdatPayloadRoot -Path (Join-Path $_.FullName "app")) } |
        Select-Object -First 1
    if ($candidate) { return $candidate.FullName }
    throw "The package does not contain the required SDAT runtime files."
}

function Get-SdatPayloadRoot {
    param([Parameter(Mandatory)][string]$PackageRoot)
    $structuredPayload = Join-Path $PackageRoot "app"
    if (Test-SdatPayloadRoot -Path $structuredPayload) { return [IO.Path]::GetFullPath($structuredPayload) }
    if (Test-SdatPayloadRoot -Path $PackageRoot) { return [IO.Path]::GetFullPath($PackageRoot) }
    throw "The SDAT install payload is missing or incomplete."
}

function Get-SdatTempRoot {
    if (-not $script:tempRoot) {
        $script:tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("sdat-install-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $script:tempRoot -Force | Out-Null
    }
    return $script:tempRoot
}

function Test-SdatV2InstallRoot {
    param([Parameter(Mandatory)][string]$Path)
    return (Test-Path -LiteralPath (Join-Path $Path ".sdat-install.json")) -and
        (Test-Path -LiteralPath (Join-Path $Path "SDAT.exe")) -and
        (Test-Path -LiteralPath (Join-Path $Path "uninstall.ps1"))
}

function Test-SdatV1InstallRoot {
    param([Parameter(Mandatory)][string]$Path)
    return (Test-Path -LiteralPath (Join-Path $Path "shutdownat.ps1")) -and
        (Test-Path -LiteralPath (Join-Path $Path "data\state.json"))
}

function Test-SdatPathOverlap {
    param(
        [Parameter(Mandatory)][string]$First,
        [Parameter(Mandatory)][string]$Second
    )
    $firstFull = [IO.Path]::GetFullPath($First).TrimEnd('\')
    $secondFull = [IO.Path]::GetFullPath($Second).TrimEnd('\')
    return $firstFull -ieq $secondFull -or
        $firstFull.StartsWith(($secondFull + '\'), [StringComparison]::OrdinalIgnoreCase) -or
        $secondFull.StartsWith(($firstFull + '\'), [StringComparison]::OrdinalIgnoreCase)
}

function Assert-SafeSdatInstallTarget {
    param([Parameter(Mandatory)][string]$Path)

    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [IO.Path]::GetPathRoot($full).TrimEnd('\')
    $protected = @(
        [Environment]::GetFolderPath("UserProfile"),
        [Environment]::GetFolderPath("LocalApplicationData"),
        [Environment]::GetFolderPath("ApplicationData"),
        [Environment]::GetFolderPath("Desktop"),
        [Environment]::GetFolderPath("MyDocuments"),
        $env:SystemRoot,
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)}
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') }

    if ($full -ieq $root -or @($protected | Where-Object { $_ -ieq $full }).Count -gt 0) {
        throw "Refusing to install SDAT into a protected broad directory: $full"
    }
    foreach ($dataPath in @(
        (Join-Path $env:LOCALAPPDATA "SDAT"),
        (Join-Path $env:LOCALAPPDATA "SDAT-uninstall-backups"))) {
        if (Test-SdatPathOverlap -First $full -Second $dataPath) {
            throw "Refusing an install directory that overlaps SDAT runtime data or backups: $full"
        }
    }
    if (-not (Test-Path -LiteralPath $full)) { return }
    $entries = @(Get-ChildItem -LiteralPath $full -Force -ErrorAction Stop)
    if ($entries.Count -eq 0) { return }
    if ((Test-SdatV2InstallRoot -Path $full) -or (Test-SdatV1InstallRoot -Path $full)) { return }
    throw "Refusing to replace a non-empty directory that is not a verified SDAT installation: $full"
}

function Send-EnvironmentChanged {
    try {
        if (-not ("Sdat.NativeMethods" -as [type])) {
            Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace Sdat {
    public static class NativeMethods {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
    }
}
"@
        }
        $result = [UIntPtr]::Zero
        $null = [Sdat.NativeMethods]::SendMessageTimeout([IntPtr]0xffff, 0x001A, [UIntPtr]::Zero, "Environment", 0x0002, 5000, [ref]$result)
    } catch { }
}

function Get-SdatUpdatedPathValue {
    param(
        [AllowEmptyString()][string]$CurrentValue,
        [Parameter(Mandatory)][string]$PreferredPath,
        [string[]]$ReplacedPath = @()
    )

    $preferred = [IO.Path]::GetFullPath($PreferredPath).TrimEnd('\')
    $excluded = @($preferred) + @($ReplacedPath |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') })
    $remaining = @($CurrentValue -split ';' | Where-Object {
        if ([string]::IsNullOrWhiteSpace($_)) { return $false }
        try {
            $candidate = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($_.Trim('"'))).TrimEnd('\')
            return -not ($excluded -icontains $candidate)
        } catch { return $true }
    })
    return (@($preferred) + $remaining) -join ';'
}

function Add-SdatToUserPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string[]]$ReplacedPath = @()
    )

    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $updatedUserPath = Get-SdatUpdatedPathValue -CurrentValue $userPath -PreferredPath $Path -ReplacedPath $ReplacedPath
    if ($updatedUserPath -cne $userPath) {
        [Environment]::SetEnvironmentVariable("Path", $updatedUserPath, "User")
    }
    $env:Path = Get-SdatUpdatedPathValue -CurrentValue $env:Path -PreferredPath $Path -ReplacedPath $ReplacedPath
    Send-EnvironmentChanged
}

function Install-SdatShortcuts {
    param([Parameter(Mandatory)][string]$InstallPath)

    $programsRoot = [Environment]::GetFolderPath("Programs")
    if ([string]::IsNullOrWhiteSpace($programsRoot)) { return }
    $shortcutRoot = Join-Path $programsRoot "SDAT"
    New-Item -ItemType Directory -Path $shortcutRoot -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell

    $appShortcut = $shell.CreateShortcut((Join-Path $shortcutRoot "SDAT.lnk"))
    $appShortcut.TargetPath = Join-Path $InstallPath "SDAT.exe"
    $appShortcut.WorkingDirectory = $InstallPath
    $appShortcut.Description = "Open SDAT"
    $appShortcut.Save()

    $uninstallShortcut = $shell.CreateShortcut((Join-Path $shortcutRoot "Uninstall SDAT.lnk"))
    $uninstallShortcut.TargetPath = Join-Path $InstallPath "Uninstall SDAT.cmd"
    $uninstallShortcut.WorkingDirectory = $InstallPath
    $uninstallShortcut.Description = "Remove SDAT and preserve a data backup"
    $uninstallShortcut.Save()
}

function Test-DotNetRuntimeList {
    param(
        [Parameter(Mandatory)][object[]]$Runtimes,
        [Parameter(Mandatory)][string]$FrameworkName,
        [Parameter(Mandatory)][version]$MinimumVersion
    )

    foreach ($runtime in $Runtimes) {
        $match = [regex]::Match([string]$runtime, '^(?<name>\S+)\s+(?<version>\d+(?:\.\d+){1,3})\s+\[')
        if (-not $match.Success -or $match.Groups['name'].Value -ne $FrameworkName) { continue }
        $availableVersion = [version]$match.Groups['version'].Value
        if ($availableVersion.Major -eq $MinimumVersion.Major -and $availableVersion -ge $MinimumVersion) {
            return $true
        }
    }
    return $false
}

function Test-DotNetRuntime {
    param(
        [Parameter(Mandatory)][string]$FrameworkName,
        [Parameter(Mandatory)][version]$MinimumVersion
    )

    $candidates = @(
        (Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
        (Join-Path $env:ProgramFiles "dotnet\dotnet.exe")
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
    foreach ($dotnetPath in $candidates) {
        $runtimes = & $dotnetPath --list-runtimes 2>$null
        if (Test-DotNetRuntimeList -Runtimes @($runtimes) -FrameworkName $FrameworkName -MinimumVersion $MinimumVersion) {
            return $true
        }
    }
    return $false
}

function Test-WindowsAppRuntime {
    param([Parameter(Mandatory)][version]$MinimumVersion)

    $packages = @(Get-AppxPackage -Name "Microsoft.WindowsAppRuntime.2" -ErrorAction SilentlyContinue |
        Where-Object { $_.Architecture -eq "X64" -and ([version]$_.Version -ge $MinimumVersion) })
    return $packages.Count -gt 0
}

function Test-MicrosoftAuthenticodeSignature {
    param([Parameter(Mandatory)]$Signature)

    if ($Signature.Status -ne "Valid" -or -not $Signature.SignerCertificate) { return $false }
    $publisher = $Signature.SignerCertificate.GetNameInfo(
        [Security.Cryptography.X509Certificates.X509NameType]::SimpleName,
        $false)
    return $publisher -ceq "Microsoft Corporation"
}

function Assert-SupportedInstallerExitCode {
    param([Parameter(Mandatory)][int]$ExitCode)
    if ($ExitCode -notin @(0, 3010)) { throw "Prerequisite installer failed with exit code $ExitCode." }
}

function Invoke-VerifiedMicrosoftInstaller {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$Elevate
    )

    $installerPath = Join-Path (Get-SdatTempRoot) $FileName
    Invoke-WebRequest -Uri $Uri -OutFile $installerPath -UseBasicParsing
    $signature = Get-AuthenticodeSignature -FilePath $installerPath
    if (-not (Test-MicrosoftAuthenticodeSignature -Signature $signature)) {
        throw "Microsoft signature validation failed for $FileName."
    }

    $startArguments = @{
        FilePath = $installerPath
        ArgumentList = $Arguments
        Wait = $true
        PassThru = $true
    }
    if ($Elevate) { $startArguments.Verb = "RunAs" }
    $process = Start-Process @startArguments
    Assert-SupportedInstallerExitCode -ExitCode $process.ExitCode
}

function Install-SdatPrerequisites {
    param([Parameter(Mandatory)]$Manifest)

    if ([string]$Manifest.DeploymentMode -ne "FrameworkDependent") { return }

    $dotNetFramework = [string]$Manifest.DotNetRuntimeName
    $dotNetVersion = [version]$Manifest.DotNetRuntimeVersion
    $dotNetInstallerKind = switch ($dotNetFramework) {
        "Microsoft.NETCore.App" { "dotnet-runtime" }
        "Microsoft.WindowsDesktop.App" { "windowsdesktop-runtime" }
        default { throw "Unsupported .NET runtime framework in package manifest: $dotNetFramework" }
    }
    if (-not (Test-DotNetRuntime -FrameworkName $dotNetFramework -MinimumVersion $dotNetVersion)) {
        Write-InstallStep "Installing the official $dotNetFramework $dotNetVersion runtime"
        Invoke-VerifiedMicrosoftInstaller `
            -Uri "https://aka.ms/dotnet/$($dotNetVersion.Major).0/$dotNetInstallerKind-win-x64.exe" `
            -FileName "$dotNetInstallerKind-$($dotNetVersion.Major)-win-x64.exe" `
            -Arguments @("/install", "/quiet", "/norestart") `
            -Elevate
        if (-not (Test-DotNetRuntime -FrameworkName $dotNetFramework -MinimumVersion $dotNetVersion)) {
            throw "$dotNetFramework $dotNetVersion installation could not be verified."
        }
    }

    $windowsAppRuntimeVersion = [version]$Manifest.WindowsAppSdkRuntimeVersion
    if (-not (Test-WindowsAppRuntime -MinimumVersion $windowsAppRuntimeVersion)) {
        Write-InstallStep "Installing the official Windows App SDK runtime $windowsAppRuntimeVersion"
        $runtimeUri = "https://aka.ms/windowsappsdk/$($windowsAppRuntimeVersion.Major).$($windowsAppRuntimeVersion.Minor)/$($windowsAppRuntimeVersion.Major).$($windowsAppRuntimeVersion.Minor).$($windowsAppRuntimeVersion.Build)/windowsappruntimeinstall-x64.exe"
        Invoke-VerifiedMicrosoftInstaller `
            -Uri $runtimeUri `
            -FileName "WindowsAppRuntimeInstall-x64.exe" `
            -Arguments @("--quiet")
        if (-not (Test-WindowsAppRuntime -MinimumVersion $windowsAppRuntimeVersion)) {
            throw "Windows App SDK runtime $windowsAppRuntimeVersion installation could not be verified."
        }
    }
}

function Stop-SdatInstalledCompanion {
    param([Parameter(Mandatory)][string]$InstallPath)

    $companionPath = [IO.Path]::GetFullPath((Join-Path $InstallPath 'SDAT.exe'))
    $processes = @(Get-Process -Name 'SDAT' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and ([IO.Path]::GetFullPath($_.Path) -ieq $companionPath) } catch { $false }
    })
    if ($processes.Count -gt 0) {
        $processes | Stop-Process -Force -ErrorAction Stop
        $processes | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }
}

function Backup-SdatUnknownInstallFiles {
    param([Parameter(Mandatory)][string]$InstallPath)

    $installFull = [IO.Path]::GetFullPath($InstallPath).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $installFull)) { return $null }

    $owned = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in @(".sdat-package-manifest.json", ".sdat-install.json")) {
        [void]$owned.Add($relative)
    }
    $manifestPath = Join-Path $installFull ".sdat-package-manifest.json"
    if (Test-Path -LiteralPath $manifestPath) {
        $previousManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
        foreach ($relativeValue in @($previousManifest.Files)) {
            $relative = ([string]$relativeValue).Replace('/', '\').TrimStart('\')
            $target = [IO.Path]::GetFullPath((Join-Path $installFull $relative))
            if ($target.StartsWith(($installFull + '\'), [StringComparison]::OrdinalIgnoreCase)) {
                [void]$owned.Add($relative)
            }
        }
    }

    $unknownFiles = @(Get-ChildItem -LiteralPath $installFull -File -Recurse -Force | Where-Object {
        $relative = [IO.Path]::GetRelativePath($installFull, $_.FullName).Replace('/', '\')
        -not $owned.Contains($relative)
    })
    if ($unknownFiles.Count -eq 0) { return $null }

    $backupRoot = Join-Path $env:LOCALAPPDATA "SDAT\install-backups"
    $backupPath = Join-Path $backupRoot ((Get-Date -Format "yyyyMMdd-HHmmss") + "-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
    foreach ($file in $unknownFiles) {
        $relative = [IO.Path]::GetRelativePath($installFull, $file.FullName)
        $destination = Join-Path $backupPath $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force -ErrorAction Stop
    }
    return $backupPath
}

function Install-SdatPayloadTransaction {
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$InstallPath
    )

    $sourceFull = [IO.Path]::GetFullPath($SourcePath)
    $installFull = [IO.Path]::GetFullPath($InstallPath).TrimEnd('\')
    $parentFull = [IO.Path]::GetFullPath((Split-Path -Parent $installFull)).TrimEnd('\')
    New-Item -ItemType Directory -Path $parentFull -Force | Out-Null
    $transactionId = [guid]::NewGuid().ToString("N")
    $stagingPath = Join-Path $parentFull ".SDAT-stage-$transactionId"
    $rollbackPath = Join-Path $parentFull ".SDAT-rollback-$transactionId"
    $hadExistingInstall = Test-Path -LiteralPath $installFull
    $oldMoved = $false

    try {
        New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
        foreach ($item in Get-ChildItem -LiteralPath $sourceFull -Force) {
            Copy-Item -LiteralPath $item.FullName -Destination $stagingPath -Recurse -Force -ErrorAction Stop
        }
        if (-not (Test-SdatPayloadRoot -Path $stagingPath)) {
            throw "The staged SDAT payload failed validation."
        }

        $stagedVersion = (Get-Content -LiteralPath (Join-Path $stagingPath "VERSION") -Raw).Trim()
        [pscustomobject]@{
            Version = $stagedVersion
            InstalledAt = (Get-Date).ToString("o", [Globalization.CultureInfo]::InvariantCulture)
            InstallDir = $installFull
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stagingPath ".sdat-install.json") -Encoding UTF8

        if ($hadExistingInstall) {
            Move-Item -LiteralPath $installFull -Destination $rollbackPath -ErrorAction Stop
            $oldMoved = $true
        }
        try {
            Move-Item -LiteralPath $stagingPath -Destination $installFull -ErrorAction Stop
            if (-not (Test-SdatV2InstallRoot -Path $installFull)) {
                throw "The promoted SDAT installation failed validation."
            }
        } catch {
            if (Test-Path -LiteralPath $installFull) {
                $failedPath = Join-Path $parentFull ".SDAT-failed-$transactionId"
                Move-Item -LiteralPath $installFull -Destination $failedPath -ErrorAction SilentlyContinue
            }
            if ($oldMoved -and (Test-Path -LiteralPath $rollbackPath)) {
                Move-Item -LiteralPath $rollbackPath -Destination $installFull -ErrorAction Stop
                $oldMoved = $false
            }
            throw
        }
    } finally {
        if (Test-Path -LiteralPath $stagingPath) {
            Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    if ($oldMoved -and (Test-Path -LiteralPath $rollbackPath)) {
        try {
            Remove-Item -LiteralPath $rollbackPath -Recurse -Force -ErrorAction Stop
        } catch {
            Write-Warning "SDAT was updated, but the rollback directory could not be removed: $rollbackPath"
        }
    }
    return (Get-Content -LiteralPath (Join-Path $installFull "VERSION") -Raw).Trim()
}

function Get-RemotePackage {
    $headers = @{ "User-Agent" = "SDAT-Installer"; "Accept" = "application/vnd.github+json" }
    $releaseUri = if ($Version -eq "latest") {
        "https://api.github.com/repos/$repository/releases/latest"
    } else {
        $tag = if ($Version.StartsWith("v")) { $Version } else { "v$Version" }
        "https://api.github.com/repos/$repository/releases/tags/$tag"
    }
    Write-InstallStep "Resolving release $Version"
    $release = Invoke-RestMethod -Uri $releaseUri -Headers $headers
    $zipAsset = @($release.assets | Where-Object { $_.name -match '^sdat-v[0-9].*-windows\.zip$' }) | Select-Object -First 1
    if (-not $zipAsset) { throw "Release $($release.tag_name) has no Windows package." }
    $hashAsset = @($release.assets | Where-Object { $_.name -eq "$($zipAsset.name).sha256" }) | Select-Object -First 1
    if (-not $hashAsset) { throw "Release $($release.tag_name) has no SHA256 checksum." }

    $downloadRoot = Get-SdatTempRoot
    $zipPath = Join-Path $downloadRoot $zipAsset.name
    $hashPath = "$zipPath.sha256"
    Invoke-WebRequest -Uri $zipAsset.browser_download_url -Headers $headers -OutFile $zipPath
    Invoke-WebRequest -Uri $hashAsset.browser_download_url -Headers $headers -OutFile $hashPath

    $expectedHash = ([regex]::Match((Get-Content -LiteralPath $hashPath -Raw), '[A-Fa-f0-9]{64}')).Value.ToUpperInvariant()
    $actualHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ([string]::IsNullOrWhiteSpace($expectedHash) -or $actualHash -ne $expectedHash) {
        throw "Package checksum validation failed."
    }

    $expanded = Join-Path $downloadRoot "package"
    Expand-Archive -LiteralPath $zipPath -DestinationPath $expanded -Force
    return (Find-SdatPackageRoot -Path $expanded)
}

if ($ImportOnly) { return }

try {
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { throw "LOCALAPPDATA is unavailable." }
    $localPackageRoot = $null
    if (-not $Update -and -not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        foreach ($candidate in @($PSScriptRoot, (Split-Path -Parent $PSScriptRoot))) {
            if ($candidate -and ((Test-SdatPayloadRoot -Path $candidate) -or (Test-SdatPayloadRoot -Path (Join-Path $candidate "app")))) {
                $localPackageRoot = (Resolve-Path -LiteralPath $candidate).Path
                break
            }
        }
    }
    $packageRoot = if (-not [string]::IsNullOrWhiteSpace($SourcePath)) {
        Find-SdatPackageRoot -Path (Resolve-Path -LiteralPath $SourcePath).Path
    } elseif ($localPackageRoot) {
        $localPackageRoot
    } else {
        Get-RemotePackage
    }

    $installFull = [IO.Path]::GetFullPath($InstallDir)
    Assert-SafeSdatInstallTarget -Path $installFull
    $sourceFull = Get-SdatPayloadRoot -PackageRoot $packageRoot
    $packageManifest = Get-Content -LiteralPath (Join-Path $sourceFull ".sdat-package-manifest.json") -Raw | ConvertFrom-Json -ErrorAction Stop
    Write-Host "SDAT installer" -ForegroundColor White
    if (-not $SkipPrerequisites) {
        Install-SdatPrerequisites -Manifest $packageManifest
    }
    Stop-SdatInstalledCompanion -InstallPath $installFull
    Write-InstallStep "Installing to $installFull"

    # Preserve the last v1 JSON state for the native first-run migrator. The
    # complete old install also remains available until the atomic promotion.
    if (Test-SdatV1InstallRoot -Path $installFull) {
        $legacyBackup = Join-Path $env:LOCALAPPDATA "SDAT\legacy-v1"
        if (-not (Test-Path -LiteralPath $legacyBackup)) {
            New-Item -ItemType Directory -Path $legacyBackup -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $installFull "data") -Destination $legacyBackup -Recurse -Force
            Copy-Item -LiteralPath (Join-Path $installFull "shutdownat.ps1") -Destination $legacyBackup -Force
            Write-InstallStep "Preserved v1 state for native migration"
        }
    }

    $unknownFilesBackup = Backup-SdatUnknownInstallFiles -InstallPath $installFull
    if ($unknownFilesBackup) {
        Write-InstallStep "Preserved non-package files at $unknownFilesBackup"
    }

    $installedVersion = Install-SdatPayloadTransaction -SourcePath $sourceFull -InstallPath $installFull

    if (-not $NoPath) {
        Add-SdatToUserPath -Path (Join-Path $installFull "bin") -ReplacedPath @($installFull)
        Write-InstallStep "Added the SDAT CLI to the current-user PATH"
    }
    if (-not $NoShortcuts) {
        Install-SdatShortcuts -InstallPath $installFull
        Write-InstallStep "Added Start menu shortcuts"
    }

    Write-Host "SDAT $installedVersion is ready." -ForegroundColor Green
    Write-Host "Open Win+R and try: sdat 3h" -ForegroundColor Gray
    if ($Launch) {
        Write-InstallStep "Opening SDAT setup"
        Start-Process -FilePath (Join-Path $installFull "SDAT.exe") | Out-Null
    }
} finally {
    if ($tempRoot -and (Test-Path -LiteralPath $tempRoot)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
