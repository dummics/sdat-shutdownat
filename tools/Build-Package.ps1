[CmdletBinding()]
param(
    [string]$OutputDir
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
    $appPublish = Join-Path $tempRoot "publish-app"
    $cliPublish = Join-Path $tempRoot "publish-cli"
    & dotnet publish (Join-Path $root "src\Sdat.App\Sdat.App.csproj") -c Release -r win-x64 --self-contained true -o $appPublish
    if ($LASTEXITCODE -ne 0) { throw "SDAT app publish failed with exit code $LASTEXITCODE." }
    & dotnet publish (Join-Path $root "src\Sdat.Cli\Sdat.Cli.csproj") -c Release -r win-x64 --self-contained true -o $cliPublish
    if ($LASTEXITCODE -ne 0) { throw "SDAT CLI publish failed with exit code $LASTEXITCODE." }

    Copy-Item -Path (Join-Path $appPublish '*') -Destination $packageRoot -Recurse -Force
    Copy-Item -Path (Join-Path $cliPublish '*') -Destination $packageRoot -Recurse -Force

    $files = @("VERSION", "LICENSE", "README.md", "CHANGELOG.md", "ROADMAP.md", "SECURITY.md", "THIRD-PARTY-NOTICES.md", "install.ps1", "uninstall.ps1", "sdat.bat", "ssat.bat", "sdatui.bat")
    foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $root $file) -Destination $packageRoot -Force }

    $packageVersions = [xml](Get-Content -LiteralPath (Join-Path $root "Directory.Packages.props") -Raw)
    $windowsAppSdkVersion = @($packageVersions.Project.ItemGroup.PackageVersion |
        Where-Object { $_.Include -eq "Microsoft.WindowsAppSDK" })[0].Version
    $nugetRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        Join-Path ([Environment]::GetFolderPath("UserProfile")) ".nuget\packages"
    } else {
        $env:NUGET_PACKAGES
    }
    $windowsAppSdkRoot = Join-Path $nugetRoot "microsoft.windowsappsdk\$windowsAppSdkVersion"
    $licensesRoot = Join-Path $packageRoot "licenses"
    New-Item -ItemType Directory -Path $licensesRoot -Force | Out-Null
    foreach ($licenseFile in @("license.txt", "NOTICE.txt")) {
        $licenseSource = Join-Path $windowsAppSdkRoot $licenseFile
        if (-not (Test-Path -LiteralPath $licenseSource)) { throw "Missing Windows App SDK $licenseFile at $licenseSource" }
        Copy-Item -LiteralPath $licenseSource -Destination (Join-Path $licensesRoot "Microsoft.WindowsAppSDK-$licenseFile") -Force
    }

    $manifestFiles = Get-ChildItem -LiteralPath $packageRoot -File -Recurse |
        ForEach-Object { [IO.Path]::GetRelativePath($packageRoot, $_.FullName) } |
        Sort-Object
    [pscustomobject]@{
        SchemaVersion = 1
        Version = $version
        Files = @($manifestFiles)
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $packageRoot ".sdat-package-manifest.json") -Encoding UTF8

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
