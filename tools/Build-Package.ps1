[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$SpectreSourcePath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $root "VERSION") -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Invalid VERSION: $version" }
if ([string]::IsNullOrWhiteSpace($OutputDir)) { $OutputDir = Join-Path $root "dist" }
$outputFull = [IO.Path]::GetFullPath($OutputDir)
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("sdat-package-" + [guid]::NewGuid().ToString("N"))
$packageRoot = Join-Path $tempRoot "SDAT"

try {
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    $files = @("VERSION", "LICENSE", "README.md", "CHANGELOG.md", "ROADMAP.md", "SECURITY.md", "THIRD-PARTY-NOTICES.md", "install.ps1", "uninstall.ps1", "shutdownat.ps1", "sdat.bat", "ssat.bat", "sdatui.bat")
    foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $root $file) -Destination $packageRoot -Force }
    Copy-Item -LiteralPath (Join-Path $root "lib") -Destination $packageRoot -Recurse -Force
    New-Item -ItemType Directory -Path (Join-Path $packageRoot "data") -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root "data\config.template.json") -Destination (Join-Path $packageRoot "data") -Force

    $moduleDestination = Join-Path $packageRoot "modules"
    if (-not [string]::IsNullOrWhiteSpace($SpectreSourcePath)) {
        $source = (Resolve-Path -LiteralPath $SpectreSourcePath).Path
        New-Item -ItemType Directory -Path (Join-Path $moduleDestination "PwshSpectreConsole") -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination (Join-Path $moduleDestination "PwshSpectreConsole\2.6.3") -Recurse -Force
    } else {
        Save-Module -Name PwshSpectreConsole -RequiredVersion 2.6.3 -Repository PSGallery -Path $moduleDestination -Force
    }

    New-Item -ItemType Directory -Path $outputFull -Force | Out-Null
    $zipName = "sdat-v$version-windows.zip"
    $zipPath = Join-Path $outputFull $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $zipName" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ASCII
    [pscustomobject]@{ Version = $version; Package = $zipPath; Checksum = "$zipPath.sha256"; Sha256 = $hash }
} finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
