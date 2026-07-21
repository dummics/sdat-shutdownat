<# Remove the packaged SDAT installation for the current user. #>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\SDAT"),
    [switch]$KeepData,
    [switch]$SkipTaskCleanup,
    [switch]$NoPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Send-EnvironmentChanged {
    try {
        if (-not ("Sdat.UninstallNativeMethods" -as [type])) {
            Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace Sdat {
    public static class UninstallNativeMethods {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
    }
}
"@
        }
        $result = [UIntPtr]::Zero
        $null = [Sdat.UninstallNativeMethods]::SendMessageTimeout([IntPtr]0xffff, 0x001A, [UIntPtr]::Zero, "Environment", 0x0002, 5000, [ref]$result)
    } catch { }
}

function Remove-SdatFromUserPath {
    param([Parameter(Mandatory)][string]$Path)
    $normalized = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $remaining = @($userPath -split ';' | Where-Object {
        if ([string]::IsNullOrWhiteSpace($_)) { return $false }
        try { return ([IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($_.Trim('"'))).TrimEnd('\') -ine $normalized) } catch { return $true }
    })
    [Environment]::SetEnvironmentVariable("Path", ($remaining -join ';'), "User")
    Send-EnvironmentChanged
}

function Start-SdatDeferredRemoval {
    param([Parameter(Mandatory)][string]$Path)

    $escapedPath = $Path.Replace("'", "''")
    $command = "Start-Sleep -Milliseconds 500; Remove-Item -LiteralPath '$escapedPath' -Recurse -Force -ErrorAction SilentlyContinue"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $powerShell = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    if (-not $powerShell) { $powerShell = Get-Command powershell.exe -ErrorAction Stop }
    Start-Process -FilePath $powerShell.Source -ArgumentList @("-NoProfile", "-NonInteractive", "-EncodedCommand", $encoded) -WindowStyle Hidden | Out-Null
}

$installFull = [IO.Path]::GetFullPath($InstallDir)
$dataRoot = Join-Path $env:LOCALAPPDATA "SDAT"
if (-not $SkipTaskCleanup) {
    & "$env:SystemRoot\System32\shutdown.exe" /a 2>$null | Out-Null
    Get-ScheduledTask -ErrorAction SilentlyContinue |
        Where-Object { $_.TaskPath -eq '\' -and $_.TaskName -match '^SDAT(?:_|$)' } |
        Unregister-ScheduledTask -Confirm:$false -ErrorAction SilentlyContinue
}

try {
    $runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    $startupValue = Get-ItemPropertyValue -LiteralPath $runKey -Name "SDAT" -ErrorAction SilentlyContinue
    if ($startupValue -and $startupValue -like "*$installFull*") {
        Remove-ItemProperty -LiteralPath $runKey -Name "SDAT" -ErrorAction SilentlyContinue
    }
} catch { }

$backupPath = $null
if ($KeepData -and (Test-Path -LiteralPath $dataRoot)) {
    $backupRoot = Join-Path $env:LOCALAPPDATA "SDAT-uninstall-backups"
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $backupPath = Join-Path $backupRoot (Get-Date -Format "yyyyMMdd-HHmmss")
    Move-Item -LiteralPath $dataRoot -Destination $backupPath -Force
} elseif (Test-Path -LiteralPath $dataRoot) {
    Remove-Item -LiteralPath $dataRoot -Recurse -Force
}

if (-not $NoPath) { Remove-SdatFromUserPath -Path $installFull }
if (Test-Path -LiteralPath $installFull) {
    if ($env:SDAT_WRAPPER_PROCESS -eq "1") {
        Start-SdatDeferredRemoval -Path $installFull
    } else {
        Remove-Item -LiteralPath $installFull -Recurse -Force
    }
}

Write-Host "SDAT was removed." -ForegroundColor Green
if ($backupPath) { Write-Host "Data backup: $backupPath" -ForegroundColor Gray }
