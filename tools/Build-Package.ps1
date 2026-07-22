[CmdletBinding()]
param(
    [string]$OutputDir,
    [ValidateSet("Slim", "Portable")]
    [string]$Flavor = "Slim"
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
$payloadRoot = Join-Path $tempRoot "payload"
$payloadBinRoot = Join-Path $payloadRoot "bin"
$packageAppRoot = Join-Path $packageRoot "app"
$packageScriptsRoot = Join-Path $packageRoot "scripts"
$packageDocsRoot = Join-Path $packageRoot "docs"
$deploymentMode = if ($Flavor -eq "Slim") { "FrameworkDependent" } else { "SelfContained" }

function Invoke-SdatPublish {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Destination
    )

    $arguments = @(
        "publish", $Project,
        "-c", "Release",
        "-r", "win-x64",
        "-o", $Destination,
        "-p:SatelliteResourceLanguages=en%3Bit"
    )
    if ($Flavor -eq "Slim") {
        $arguments += @("--self-contained", "false", "-p:WindowsAppSDKSelfContained=false")
    } else {
        $arguments += @("--self-contained", "true")
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "$Project publish failed with exit code $LASTEXITCODE." }

    # The portable Windows App SDK output carries every upstream UI language by
    # default. SDAT currently ships English and Italian, with English fallback.
    $destinationFull = [IO.Path]::GetFullPath($Destination)
    $tempFull = [IO.Path]::GetFullPath($tempRoot).TrimEnd('\') + '\'
    if (-not $destinationFull.StartsWith($tempFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to filter satellite resources outside the package staging directory."
    }
    Get-ChildItem -LiteralPath $destinationFull -Directory -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -match '^[a-z]{2}(?:-[A-Za-z]{2})?$' -and
            $_.Name -notin @("en", "it", "en-US", "it-IT")
        } |
        Remove-Item -Recurse -Force
}

try {
    New-Item -ItemType Directory -Path $packageRoot, $payloadRoot, $payloadBinRoot, $packageAppRoot, $packageScriptsRoot, $packageDocsRoot -Force | Out-Null
    $appPublish = Join-Path $tempRoot "publish-app"
    $cliPublish = Join-Path $tempRoot "publish-cli"
    Invoke-SdatPublish -Project (Join-Path $root "src\Sdat.App\Sdat.App.csproj") -Destination $appPublish
    Invoke-SdatPublish -Project (Join-Path $root "src\Sdat.Cli\Sdat.Cli.csproj") -Destination $cliPublish

    Copy-Item -Path (Join-Path $appPublish '*') -Destination $payloadRoot -Recurse -Force
    Copy-Item -Path (Join-Path $cliPublish '*') -Destination $payloadRoot -Recurse -Force

    foreach ($file in @("VERSION", "LICENSE", "THIRD-PARTY-NOTICES.md", "install.ps1", "uninstall.ps1", "sdat.bat", "ssat.bat", "sdatui.bat")) {
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination $payloadRoot -Force
    }
    foreach ($file in @("sdat.bat", "ssat.bat")) {
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination $payloadBinRoot -Force
    }
    Copy-Item -LiteralPath (Join-Path $root "Uninstall SDAT.cmd") -Destination $payloadRoot -Force
    foreach ($file in @("README.md", "CHANGELOG.md", "ROADMAP.md", "SECURITY.md", "LICENSE", "THIRD-PARTY-NOTICES.md")) {
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination $packageDocsRoot -Force
    }
    Copy-Item -LiteralPath (Join-Path $root "install.ps1") -Destination $packageScriptsRoot -Force
    Copy-Item -LiteralPath (Join-Path $root "uninstall.ps1") -Destination $packageScriptsRoot -Force
    Copy-Item -LiteralPath (Join-Path $root "Install SDAT.cmd") -Destination $packageRoot -Force
    Copy-Item -LiteralPath (Join-Path $root "Uninstall SDAT.cmd") -Destination $packageRoot -Force
    Copy-Item -LiteralPath (Join-Path $root "PACKAGE-README.txt") -Destination (Join-Path $packageRoot "README.txt") -Force
    Copy-Item -LiteralPath (Join-Path $root "VERSION") -Destination $packageRoot -Force

    $packageVersions = [xml](Get-Content -LiteralPath (Join-Path $root "Directory.Packages.props") -Raw)
    $runtimeVersion = @($packageVersions.Project.ItemGroup.PackageVersion |
        Where-Object { $_.Include -eq "Microsoft.WindowsAppSDK.Runtime" })[0].Version
    $nugetRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        Join-Path ([Environment]::GetFolderPath("UserProfile")) ".nuget\packages"
    } else {
        $env:NUGET_PACKAGES
    }
    $licensesRoot = Join-Path $payloadRoot "licenses"
    New-Item -ItemType Directory -Path $licensesRoot -Force | Out-Null
    $windowsAppSdkPackages = @($packageVersions.Project.ItemGroup.PackageVersion |
        Where-Object { $_.Include -like "Microsoft.WindowsAppSDK.*" })
    foreach ($package in $windowsAppSdkPackages) {
        $packageId = [string]$package.Include
        $packageVersion = [string]$package.Version
        $packagePath = Join-Path $nugetRoot "$($packageId.ToLowerInvariant())\$packageVersion"
        $licenseSource = Join-Path $packagePath "license.txt"
        if (-not (Test-Path -LiteralPath $licenseSource)) { throw "Missing $packageId license.txt at $licenseSource" }
        Copy-Item -LiteralPath $licenseSource -Destination (Join-Path $licensesRoot "$packageId-license.txt") -Force
        $noticeSource = Join-Path $packagePath "NOTICE.txt"
        if (Test-Path -LiteralPath $noticeSource) {
            Copy-Item -LiteralPath $noticeSource -Destination (Join-Path $licensesRoot "$packageId-NOTICE.txt") -Force
        }
    }

    $runtimeConfig = Get-Content -LiteralPath (Join-Path $payloadRoot "SDAT.runtimeconfig.json") -Raw | ConvertFrom-Json
    $runtimeOptions = $runtimeConfig.runtimeOptions
    $dotNetFramework = if ($runtimeOptions.PSObject.Properties.Name -contains "framework") {
        $runtimeOptions.framework
    } elseif ($runtimeOptions.PSObject.Properties.Name -contains "includedFrameworks") {
        @($runtimeOptions.includedFrameworks) |
            Where-Object { [string]$_.name -eq "Microsoft.NETCore.App" } |
            Select-Object -First 1
    }
    if (-not $dotNetFramework -or [string]::IsNullOrWhiteSpace([string]$dotNetFramework.name) -or
        [string]::IsNullOrWhiteSpace([string]$dotNetFramework.version)) {
        throw "SDAT.runtimeconfig.json does not declare the required .NET framework."
    }

    $manifestFiles = Get-ChildItem -LiteralPath $payloadRoot -File -Recurse |
        ForEach-Object { [IO.Path]::GetRelativePath($payloadRoot, $_.FullName) } |
        Sort-Object
    [pscustomobject]@{
        SchemaVersion = 2
        Version = $version
        Architecture = "win-x64"
        DeploymentMode = $deploymentMode
        DotNetRuntimeName = [string]$dotNetFramework.name
        DotNetRuntimeVersion = [string]$dotNetFramework.version
        WindowsAppSdkRuntimeVersion = $runtimeVersion
        Files = @($manifestFiles)
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $payloadRoot ".sdat-package-manifest.json") -Encoding UTF8

    Copy-Item -Path (Join-Path $payloadRoot '*') -Destination $packageAppRoot -Recurse -Force

    New-Item -ItemType Directory -Path $outputFull -Force | Out-Null
    $suffix = if ($Flavor -eq "Slim") { "windows" } else { "windows-portable" }
    $zipName = "sdat-v$version-$suffix.zip"
    $zipPath = Join-Path $outputFull $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $zipName" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ASCII

    $payloadFiles = @(Get-ChildItem -LiteralPath $payloadRoot -File -Recurse)
    [pscustomobject]@{
        Version = $version
        Flavor = $Flavor
        DeploymentMode = $deploymentMode
        PayloadFiles = $payloadFiles.Count
        ExpandedMiB = [math]::Round((($payloadFiles | Measure-Object Length -Sum).Sum / 1MB), 2)
        PackageMiB = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 2)
        Package = $zipPath
        Checksum = "$zipPath.sha256"
        Sha256 = $hash
    }
} finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
