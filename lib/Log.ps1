Set-StrictMode -Version Latest

function Get-SdatLogsDir {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$Profile
    )
    $local = $env:LOCALAPPDATA
    if ([string]::IsNullOrWhiteSpace($local)) {
        try { $local = [Environment]::GetFolderPath("LocalApplicationData") } catch { $local = $null }
    }
    if ([string]::IsNullOrWhiteSpace($local)) {
        $local = $Root
    }
    $base = Join-Path -Path (Join-Path -Path $local -ChildPath "SDAT") -ChildPath "logs"
    $p = Get-SdatProfileSafe -Profile $Profile
    if ($p) { return (Join-Path -Path $base -ChildPath $p) }
    return $base
}

function Ensure-SdatLogsDir {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$Profile
    )
    $dir = Get-SdatLogsDir -Root $Root -Profile $Profile
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    return $dir
}

function Get-SdatLogFilePath {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$Profile
    )
    $dir = Ensure-SdatLogsDir -Root $Root -Profile $Profile
    $name = "sdat-{0}.log" -f (Get-Date).ToString("yyyy-MM-dd")
    return (Join-Path -Path $dir -ChildPath $name)
}

function New-SdatRunId {
    try { return ([Guid]::NewGuid().ToString("n")) } catch { return ([DateTime]::UtcNow.Ticks.ToString()) }
}

function New-SdatLogContext {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$Profile,
        [Parameter(Mandatory)][string]$Mode
    )
    $p = Get-SdatProfileSafe -Profile $Profile
    $logPath = Get-SdatLogFilePath -Root $Root -Profile $p
    return [pscustomobject]@{
        Root = $Root
        Profile = $p
        Mode = $Mode
        RunId = (New-SdatRunId)
        LogPath = $logPath
        StartedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    }
}

function Convert-ToJsonCompact {
    param($Value)
    if ($null -eq $Value) { return "" }
    try { return ($Value | ConvertTo-Json -Depth 10 -Compress) } catch { return "" }
}

function Write-SdatLog {
    param(
        [Parameter(Mandatory)]$Ctx,
        [Parameter(Mandatory)][ValidateSet('DEBUG','INFO','WARN','ERROR')][string]$Level,
        [Parameter(Mandatory)][string]$Message,
        $Data
    )
    if ($null -eq $Ctx -or [string]::IsNullOrWhiteSpace($Ctx.LogPath)) { return }

    $ts = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    $suffix = ""
    if ($PSBoundParameters.ContainsKey('Data') -and $null -ne $Data) {
        $json = Convert-ToJsonCompact -Value $Data
        if (-not [string]::IsNullOrWhiteSpace($json)) { $suffix = " | " + $json }
    }
    $line = "{0} [{1}] ({2}/{3}) {4}{5}" -f $ts, $Level, $Ctx.Mode, $Ctx.RunId, $Message, $suffix
    for ($i = 0; $i -lt 3; $i++) {
        try {
            Add-Content -LiteralPath $Ctx.LogPath -Value $line -Encoding UTF8 -ErrorAction Stop
            return
        } catch {
            if ($i -lt 2) { Start-Sleep -Milliseconds 80 }
        }
    }
}

function Get-SdatTestResultsPath {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$Profile
    )
    $dir = Ensure-SdatLogsDir -Root $Root -Profile $Profile
    return (Join-Path -Path $dir -ChildPath "tests.jsonl")
}

function Write-SdatJsonl {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Object
    )
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $line = ($Object | ConvertTo-Json -Depth 10 -Compress)
    for ($i = 0; $i -lt 3; $i++) {
        try {
            Add-Content -LiteralPath $Path -Value $line -Encoding UTF8 -ErrorAction Stop
            return
        } catch {
            if ($i -lt 2) { Start-Sleep -Milliseconds 80 }
        }
    }
}
