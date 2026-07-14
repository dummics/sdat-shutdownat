Set-StrictMode -Version Latest

function Assert-SdatPropertySet {
    param(
        [AllowNull()]$Value,
        [Parameter(Mandatory)][string[]]$Expected,
        [Parameter(Mandatory)][string]$Context
    )
    if ($null -eq $Value) { throw "SDAT $Context is null." }

    $actual = @($Value.PSObject.Properties.Name)
    $missing = @($Expected | Where-Object { $_ -notin $actual })
    $unexpected = @($actual | Where-Object { $_ -notin $Expected })
    if ($missing.Count -gt 0 -or $unexpected.Count -gt 0) {
        $missingText = if ($missing.Count -gt 0) { $missing -join ", " } else { "none" }
        $unexpectedText = if ($unexpected.Count -gt 0) { $unexpected -join ", " } else { "none" }
        throw "Unsupported SDAT $Context schema. Missing: $missingText. Unexpected: $unexpectedText."
    }
}

function Assert-SdatConfigSchema {
    param([Parameter(Mandatory)]$Config)

    $expected = @(
        "GraceMinutes",
        "DailyOverlapWindowMinutes",
        "MissedVolatileShutdownMaxDelayMinutes",
        "MissedPermanentShutdownMaxDelayMinutes"
    )
    Assert-SdatPropertySet -Value $Config -Expected $expected -Context "config"

    foreach ($name in $expected) {
        $parsed = 0
        if ($null -eq $Config.$name -or -not [int]::TryParse([string]$Config.$name, [ref]$parsed) -or $parsed -lt 0) {
            throw "SDAT config field '$name' must be a non-negative integer."
        }
    }
}

function Get-SdatProfileSafe {
    param([AllowNull()][string]$Profile)
    if ([string]::IsNullOrWhiteSpace($Profile)) { return "" }
    $p = $Profile.Trim()
    $p = ($p -replace "[^a-zA-Z0-9_-]", "_")
    return $p
}

function Get-SdatDataDir {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$Profile
    )
    $base = Join-Path -Path $Root -ChildPath "data"
    $p = Get-SdatProfileSafe -Profile $Profile
    if ($p) { return (Join-Path -Path (Join-Path -Path $base -ChildPath "profiles") -ChildPath $p) }
    return $base
}

function Get-SdatConfigPaths {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$Profile
    )
    $dataDir = Get-SdatDataDir -Root $Root -Profile $Profile
    $templateDir = Join-Path -Path $Root -ChildPath "data"
    return [pscustomobject]@{
        DataDir = $dataDir
        ConfigTemplatePath = (Join-Path -Path $templateDir -ChildPath "config.template.json")
        ConfigPath = (Join-Path -Path $dataDir -ChildPath "config.json")
        StatePath = (Join-Path -Path $dataDir -ChildPath "state.json")
    }
}

function Ensure-SdatDataDir {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$Profile
    )
    $dataDir = Get-SdatDataDir -Root $Root -Profile $Profile
    if (-not (Test-Path -LiteralPath $dataDir)) {
        New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    }
}

function Copy-FileAtomic {
    param(
        [Parameter(Mandatory)][string]$From,
        [Parameter(Mandatory)][string]$To
    )
    $tmp = "${To}.tmp"
    Copy-Item -LiteralPath $From -Destination $tmp -Force
    Move-Item -LiteralPath $tmp -Destination $To -Force
}

function Ensure-SdatConfigExists {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$Profile
    )
    Ensure-SdatDataDir -Root $Root -Profile $Profile
    $paths = Get-SdatConfigPaths -Root $Root -Profile $Profile
    if (-not (Test-Path -LiteralPath $paths.ConfigPath)) {
        if (-not (Test-Path -LiteralPath $paths.ConfigTemplatePath)) {
            throw "Missing config template: $($paths.ConfigTemplatePath)"
        }
        Copy-FileAtomic -From $paths.ConfigTemplatePath -To $paths.ConfigPath
    }
}

function Read-JsonFileOrNull {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $raw = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    return ($raw | ConvertFrom-Json -ErrorAction Stop)
}

function Load-SdatConfig {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$Profile
    )
    Ensure-SdatConfigExists -Root $Root -Profile $Profile
    $paths = Get-SdatConfigPaths -Root $Root -Profile $Profile

    $config = Read-JsonFileOrNull -Path $paths.ConfigPath
    if ($null -eq $config) { throw "SDAT config is empty: $($paths.ConfigPath)" }
    Assert-SdatConfigSchema -Config $config
    return $config
}

function Save-SdatConfig {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$Profile,
        [Parameter(Mandatory)]$Config
    )
    Assert-SdatConfigSchema -Config $Config
    Ensure-SdatDataDir -Root $Root -Profile $Profile
    $paths = Get-SdatConfigPaths -Root $Root -Profile $Profile
    $tmp = "$($paths.ConfigPath).tmp"
    $Config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $paths.ConfigPath -Force
}
