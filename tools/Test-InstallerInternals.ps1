[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [Parameter(Mandatory)][string]$SandboxRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$installer = Join-Path $PackageRoot "scripts\install.ps1"
. $installer -ImportOnly
$manifest = Get-Content -LiteralPath (Join-Path $PackageRoot "app\.sdat-package-manifest.json") -Raw | ConvertFrom-Json
$frameworkManifest = $manifest.PSObject.Copy()
$frameworkManifest.DeploymentMode = "FrameworkDependent"

foreach ($protectedPath in @(
    [IO.Path]::GetPathRoot([Environment]::GetFolderPath("UserProfile")),
    [Environment]::GetFolderPath("UserProfile"))) {
    $rejected = $false
    try { Assert-SafeSdatInstallTarget -Path $protectedPath } catch { $rejected = $true }
    if (-not $rejected) { throw "Installer accepted protected target directory: $protectedPath" }
}

. (Join-Path $PackageRoot "scripts\uninstall.ps1") -ImportOnly
foreach ($protectedPath in @(
    [IO.Path]::GetPathRoot([Environment]::GetFolderPath("UserProfile")),
    [Environment]::GetFolderPath("UserProfile"),
    [Environment]::GetFolderPath("LocalApplicationData"),
    [Environment]::GetFolderPath("Desktop"),
    [Environment]::GetFolderPath("MyDocuments"))) {
    $rejected = $false
    try { Assert-SdatInstalledDirectory -Path $protectedPath } catch { $rejected = $true }
    if (-not $rejected) { throw "Uninstaller accepted protected target directory: $protectedPath" }
}

$savedLocalAppData = $env:LOCALAPPDATA
try {
    foreach ($case in @("parent", "equal", "child")) {
        $env:LOCALAPPDATA = Join-Path $SandboxRoot "overlap-$case"
        $dataRoot = Join-Path $env:LOCALAPPDATA "SDAT"
        $target = switch ($case) {
            "parent" { $env:LOCALAPPDATA }
            "equal" { $dataRoot }
            "child" { Join-Path $dataRoot "runtime" }
        }
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        '{}' | Set-Content -LiteralPath (Join-Path $target ".sdat-install.json") -Encoding UTF8
        [pscustomobject]@{ InstallDir = [IO.Path]::GetFullPath($target) } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target ".sdat-install.json") -Encoding UTF8
        "marker" | Set-Content -LiteralPath (Join-Path $target "SDAT.exe") -Encoding ASCII
        "marker" | Set-Content -LiteralPath (Join-Path $target "uninstall.ps1") -Encoding ASCII

        $installRejected = $false
        try { Assert-SafeSdatInstallTarget -Path $target } catch { $installRejected = $true }
        $uninstallRejected = $false
        try { Assert-SdatInstalledDirectory -Path $target } catch { $uninstallRejected = $true }
        if (-not $installRejected -or -not $uninstallRejected) {
            throw "Installer or uninstaller accepted a $case path overlapping SDAT runtime data."
        }
    }
} finally {
    $env:LOCALAPPDATA = $savedLocalAppData
}

if (Test-DotNetRuntimeList `
        -Runtimes @('Microsoft.NETCore.App 11.0.0 [C:\dotnet\shared\Microsoft.NETCore.App]') `
        -FrameworkName 'Microsoft.NETCore.App' `
        -MinimumVersion ([version]'10.0.0')) {
    throw ".NET runtime detection incorrectly accepted a later major version."
}
if (-not (Test-DotNetRuntimeList `
        -Runtimes @('Microsoft.NETCore.App 10.0.1 [C:\dotnet\shared\Microsoft.NETCore.App]') `
        -FrameworkName 'Microsoft.NETCore.App' `
        -MinimumVersion ([version]'10.0.0'))) {
    throw ".NET runtime detection rejected a compatible patch version."
}

# A framework-dependent package with both runtimes already installed must be a no-op.
function Test-DotNetRuntime { param($FrameworkName, $MinimumVersion) return $true }
function Test-WindowsAppRuntime { param($MinimumVersion) return $true }
function Invoke-VerifiedMicrosoftInstaller { throw "Prerequisite installer should not run when runtimes are present." }
Install-SdatPrerequisites -Manifest $frameworkManifest

# A missing .NET runtime must select the smaller runtime declared by runtimeconfig.json.
$script:dotNetChecks = 0
$script:installerCalls = @()
function Test-DotNetRuntime {
    param($FrameworkName, $MinimumVersion)
    $script:dotNetChecks++
    return $script:dotNetChecks -gt 1
}
function Test-WindowsAppRuntime { param($MinimumVersion) return $true }
function Invoke-VerifiedMicrosoftInstaller {
    param($Uri, $FileName, $Arguments, [switch]$Elevate)
    $script:installerCalls += [pscustomobject]@{ Uri = $Uri; FileName = $FileName; Elevate = [bool]$Elevate }
}
Install-SdatPrerequisites -Manifest $frameworkManifest
if ($script:installerCalls.Count -ne 1 -or
    $script:installerCalls[0].Uri -notmatch '/dotnet-runtime-win-x64\.exe$' -or
    $script:installerCalls[0].Uri -match 'windowsdesktop') {
    throw "Missing .NET runtime planning selected the wrong installer."
}

# A missing Windows App SDK runtime must use the pinned official aka.ms route.
$script:windowsRuntimeChecks = 0
$script:installerCalls = @()
function Test-DotNetRuntime { param($FrameworkName, $MinimumVersion) return $true }
function Test-WindowsAppRuntime {
    param($MinimumVersion)
    $script:windowsRuntimeChecks++
    return $script:windowsRuntimeChecks -gt 1
}
Install-SdatPrerequisites -Manifest $frameworkManifest
if ($script:installerCalls.Count -ne 1 -or
    $script:installerCalls[0].Uri -notmatch '^https://aka\.ms/windowsappsdk/2\.3/2\.3\.1/windowsappruntimeinstall-x64\.exe$') {
    throw "Missing Windows App SDK runtime planning selected the wrong installer."
}

# A portable package must not probe or install shared prerequisites.
$portableManifest = $frameworkManifest.PSObject.Copy()
$portableManifest.DeploymentMode = "SelfContained"
function Test-DotNetRuntime { throw "Portable package unexpectedly probed .NET." }
function Test-WindowsAppRuntime { throw "Portable package unexpectedly probed Windows App SDK." }
function Invoke-VerifiedMicrosoftInstaller { throw "Portable package unexpectedly installed a prerequisite." }
Install-SdatPrerequisites -Manifest $portableManifest

# Publisher matching must be exact; a valid-looking subject containing the word
# Microsoft is not sufficient for an elevated executable.
$rsa = [Security.Cryptography.RSA]::Create(2048)
try {
    $badRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        "CN=Microsoft Example LLC",
        $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $badCertificate = $badRequest.CreateSelfSigned((Get-Date).AddMinutes(-1), (Get-Date).AddMinutes(5))
    $badSignature = [pscustomobject]@{ Status = "Valid"; SignerCertificate = $badCertificate }
    if (Test-MicrosoftAuthenticodeSignature -Signature $badSignature) {
        throw "Authenticode publisher validation accepted a non-Microsoft organization."
    }
} finally {
    $rsa.Dispose()
}

Assert-SupportedInstallerExitCode -ExitCode 0
Assert-SupportedInstallerExitCode -ExitCode 3010
$exitCodeRejected = $false
try { Assert-SupportedInstallerExitCode -ExitCode 5 } catch { $exitCodeRejected = $true }
if (-not $exitCodeRejected) { throw "Prerequisite installer failure exit code was accepted." }

# Fault injection during promotion must restore the previous verified install.
$transactionRoot = Join-Path $SandboxRoot "transaction-rollback"
$oldInstall = Join-Path $transactionRoot "install"
New-Item -ItemType Directory -Path $oldInstall -Force | Out-Null
'{}' | Set-Content -LiteralPath (Join-Path $oldInstall ".sdat-install.json") -Encoding UTF8
'old' | Set-Content -LiteralPath (Join-Path $oldInstall "SDAT.exe") -Encoding ASCII
'old' | Set-Content -LiteralPath (Join-Path $oldInstall "uninstall.ps1") -Encoding ASCII
'preserve-me' | Set-Content -LiteralPath (Join-Path $oldInstall "sentinel.txt") -Encoding ASCII

$script:moveCalls = 0
function Move-Item {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$LiteralPath,
        [Parameter(Mandatory)][string]$Destination,
        [switch]$Force
    )
    $script:moveCalls++
    if ($script:moveCalls -eq 2) { throw "Injected promotion failure" }
    Microsoft.PowerShell.Management\Move-Item @PSBoundParameters
}
$rollbackObserved = $false
try {
    Install-SdatPayloadTransaction -SourcePath (Join-Path $PackageRoot "app") -InstallPath $oldInstall | Out-Null
} catch {
    $rollbackObserved = $_.Exception.Message -match "Injected promotion failure"
} finally {
    Remove-Item -LiteralPath Function:\Move-Item -ErrorAction SilentlyContinue
}
if (-not $rollbackObserved -or
    -not (Test-Path -LiteralPath (Join-Path $oldInstall "sentinel.txt")) -or
    (Get-Content -LiteralPath (Join-Path $oldInstall "sentinel.txt") -Raw).Trim() -ne "preserve-me") {
    throw "Transactional update did not restore the previous installation after promotion failure."
}

Write-Host "Installer prerequisite and transactional rollback checks passed." -ForegroundColor Green
