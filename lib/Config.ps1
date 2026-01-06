Set-StrictMode -Version Latest

function Get-SdatDataDir {
    param([Parameter(Mandatory)][string]$Root)
    return (Join-Path -Path $Root -ChildPath "data")
}

function Get-SdatConfigPaths {
    param([Parameter(Mandatory)][string]$Root)
    $dataDir = Get-SdatDataDir -Root $Root
    return [pscustomobject]@{
        DataDir = $dataDir
        ConfigTemplatePath = (Join-Path -Path $dataDir -ChildPath "config.template.json")
        ConfigPath = (Join-Path -Path $dataDir -ChildPath "config.json")
        StatePath = (Join-Path -Path $dataDir -ChildPath "state.json")
    }
}

function Ensure-SdatDataDir {
    param([Parameter(Mandatory)][string]$Root)
    $dataDir = Get-SdatDataDir -Root $Root
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
    param([Parameter(Mandatory)][string]$Root)
    Ensure-SdatDataDir -Root $Root
    $paths = Get-SdatConfigPaths -Root $Root
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
    param([Parameter(Mandatory)][string]$Root)
    Ensure-SdatConfigExists -Root $Root
    $paths = Get-SdatConfigPaths -Root $Root

    $config = Read-JsonFileOrNull -Path $paths.ConfigPath
    if ($null -eq $config) { $config = [pscustomobject]@{} }

    if ($null -eq $config.GraceMinutes) { $config | Add-Member -NotePropertyName GraceMinutes -NotePropertyValue 60 }
    if ($null -eq $config.MissedVolatileShutdownMaxDelayMinutes) { $config | Add-Member -NotePropertyName MissedVolatileShutdownMaxDelayMinutes -NotePropertyValue 5 }

    return $config
}

function Save-SdatConfig {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)]$Config
    )
    Ensure-SdatDataDir -Root $Root
    $paths = Get-SdatConfigPaths -Root $Root
    $tmp = "$($paths.ConfigPath).tmp"
    $Config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $paths.ConfigPath -Force
}

