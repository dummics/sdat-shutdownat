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
    [switch]$NoPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repository = "dummics/sdat-shutdownat"
$requiredFiles = @("VERSION", "shutdownat.ps1", "sdat.bat", "ssat.bat", "sdatui.bat", "uninstall.ps1", "lib", "data\config.template.json", "modules\PwshSpectreConsole\2.6.3\PwshSpectreConsole.psd1")
$tempRoot = $null

function Write-InstallStep {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host ("  {0}" -f $Message) -ForegroundColor Cyan
}

function Test-SdatPackageRoot {
    param([Parameter(Mandatory)][string]$Path)
    foreach ($relative in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $relative))) { return $false }
    }
    return $true
}

function Find-SdatPackageRoot {
    param([Parameter(Mandatory)][string]$Path)
    if (Test-SdatPackageRoot -Path $Path) { return (Resolve-Path -LiteralPath $Path).Path }
    $candidate = Get-ChildItem -LiteralPath $Path -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-SdatPackageRoot -Path $_.FullName } |
        Select-Object -First 1
    if ($candidate) { return $candidate.FullName }
    throw "The package does not contain the required SDAT runtime files."
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

function Add-SdatToUserPath {
    param([Parameter(Mandatory)][string]$Path)
    $normalized = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $parts = @($userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $matching = @($parts | Where-Object {
        try { [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($_.Trim('"'))).TrimEnd('\') -ieq $normalized } catch { $false }
    })
    if ($matching.Count -eq 0) {
        [Environment]::SetEnvironmentVariable("Path", (($normalized) + $(if ($parts.Count) { ";" + ($parts -join ';') } else { "" })), "User")
    }
    if (-not (($env:Path -split ';') | Where-Object { $_.TrimEnd('\') -ieq $normalized })) {
        $env:Path = "$normalized;$env:Path"
    }
    Send-EnvironmentChanged
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

    $script:tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("sdat-install-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $script:tempRoot -Force | Out-Null
    $zipPath = Join-Path $script:tempRoot $zipAsset.name
    $hashPath = "$zipPath.sha256"
    Invoke-WebRequest -Uri $zipAsset.browser_download_url -Headers $headers -OutFile $zipPath
    Invoke-WebRequest -Uri $hashAsset.browser_download_url -Headers $headers -OutFile $hashPath

    $expectedHash = ([regex]::Match((Get-Content -LiteralPath $hashPath -Raw), '[A-Fa-f0-9]{64}')).Value.ToUpperInvariant()
    $actualHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ([string]::IsNullOrWhiteSpace($expectedHash) -or $actualHash -ne $expectedHash) {
        throw "Package checksum validation failed."
    }

    $expanded = Join-Path $script:tempRoot "package"
    Expand-Archive -LiteralPath $zipPath -DestinationPath $expanded -Force
    return (Find-SdatPackageRoot -Path $expanded)
}

try {
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { throw "LOCALAPPDATA is unavailable." }
    $packageRoot = if (-not [string]::IsNullOrWhiteSpace($SourcePath)) {
        Find-SdatPackageRoot -Path (Resolve-Path -LiteralPath $SourcePath).Path
    } elseif (-not $Update -and -not [string]::IsNullOrWhiteSpace($PSScriptRoot) -and (Test-SdatPackageRoot -Path $PSScriptRoot)) {
        (Resolve-Path -LiteralPath $PSScriptRoot).Path
    } else {
        Get-RemotePackage
    }

    $installFull = [IO.Path]::GetFullPath($InstallDir)
    $sourceFull = [IO.Path]::GetFullPath($packageRoot)
    Write-Host "SDAT installer" -ForegroundColor White
    Write-InstallStep "Installing to $installFull"
    New-Item -ItemType Directory -Path $installFull -Force | Out-Null

    if ($sourceFull.TrimEnd('\') -ine $installFull.TrimEnd('\')) {
        # Replace shipped files as a unit so updates cannot leave removed code or modules behind.
        # Runtime state lives under data\ and is intentionally preserved.
        $shippedEntries = @(
            "VERSION", "LICENSE", "README.md", "CHANGELOG.md", "ROADMAP.md", "SECURITY.md",
            "THIRD-PARTY-NOTICES.md", "install.ps1", "uninstall.ps1", "shutdownat.ps1",
            "sdat.bat", "ssat.bat", "sdatui.bat", "lib", "modules"
        )
        foreach ($entry in $shippedEntries) {
            $installedEntry = Join-Path $installFull $entry
            if (Test-Path -LiteralPath $installedEntry) {
                Remove-Item -LiteralPath $installedEntry -Recurse -Force
            }
        }

        foreach ($item in Get-ChildItem -LiteralPath $sourceFull -Force) {
            Copy-Item -LiteralPath $item.FullName -Destination $installFull -Recurse -Force
        }
    }

    $installedVersion = (Get-Content -LiteralPath (Join-Path $installFull "VERSION") -Raw).Trim()
    [pscustomobject]@{
        Version = $installedVersion
        InstalledAt = (Get-Date).ToString("o", [Globalization.CultureInfo]::InvariantCulture)
        InstallDir = $installFull
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $installFull ".sdat-install.json") -Encoding UTF8

    if (-not $NoPath) {
        Add-SdatToUserPath -Path $installFull
        Write-InstallStep "Added SDAT to the current-user PATH"
    }

    Write-Host "SDAT $installedVersion is ready." -ForegroundColor Green
    Write-Host "Open Win+R and try: sdat 3h" -ForegroundColor Gray
} finally {
    if ($tempRoot -and (Test-Path -LiteralPath $tempRoot)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
