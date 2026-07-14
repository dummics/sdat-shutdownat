Set-StrictMode -Version Latest

function Get-SdatLogsRoot {
    param(
        [Parameter(Mandatory)][string]$Root
    )
    $local = $env:LOCALAPPDATA
    if ([string]::IsNullOrWhiteSpace($local)) {
        try { $local = [Environment]::GetFolderPath("LocalApplicationData") } catch { $local = $null }
    }
    if ([string]::IsNullOrWhiteSpace($local)) {
        $local = $Root
    }
    return (Join-Path -Path (Join-Path -Path $local -ChildPath "SDAT") -ChildPath "logs")
}

function Get-SdatLogsDir {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$Profile
    )
    $base = Get-SdatLogsRoot -Root $Root
    $p = Get-SdatProfileSafe -Profile $Profile
    if ($p) { return (Join-Path -Path $base -ChildPath $p) }
    return $base
}

function Limit-SdatLogFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [long]$MaxBytes = 5MB,
        [int]$TailLines = 2000
    )
    $tempPath = "$Path.trim"
    try {
        $file = Get-Item -LiteralPath $Path -ErrorAction Stop
        if ($file.Length -le $MaxBytes) { return }
        $tail = @(Get-Content -LiteralPath $Path -Tail $TailLines -ErrorAction Stop)
        $tail | Set-Content -LiteralPath $tempPath -Encoding UTF8 -ErrorAction Stop
        Move-Item -LiteralPath $tempPath -Destination $Path -Force -ErrorAction Stop
    } catch {
        # Logging maintenance must never block a shutdown command.
    } finally {
        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-SdatLogMaintenance {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$LogsRoot,
        [int]$RetentionDays = 30,
        [long]$MaxBytes = 5MB,
        [switch]$Force
    )
    try {
        $base = if ([string]::IsNullOrWhiteSpace($LogsRoot)) { Get-SdatLogsRoot -Root $Root } else { $LogsRoot }
        if (-not (Test-Path -LiteralPath $base)) { New-Item -ItemType Directory -Path $base -Force | Out-Null }
        $marker = Join-Path $base ".maintenance"
        if (-not $Force -and (Test-Path -LiteralPath $marker)) {
            $markerInfo = Get-Item -LiteralPath $marker -ErrorAction SilentlyContinue
            if ($markerInfo -and $markerInfo.LastWriteTimeUtc -gt [datetime]::UtcNow.AddHours(-24)) { return }
        }

        $cutoff = (Get-Date).AddDays(-$RetentionDays)
        $files = @(Get-ChildItem -LiteralPath $base -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in @(".log", ".jsonl") })
        foreach ($file in $files) {
            if ($file.LastWriteTime -lt $cutoff) {
                Remove-Item -LiteralPath $file.FullName -Force -ErrorAction SilentlyContinue
            } elseif ($file.Length -gt $MaxBytes) {
                Limit-SdatLogFile -Path $file.FullName -MaxBytes $MaxBytes
            }
        }
        [datetime]::UtcNow.ToString("o", [Globalization.CultureInfo]::InvariantCulture) | Set-Content -LiteralPath $marker -Encoding ASCII -Force
    } catch {
        # Maintenance is best-effort and must not affect command execution.
    }
}

function Get-SdatRecentLogIssues {
    param(
        [Parameter(Mandatory)][string]$Root,
        [int]$Count = 5
    )
    $base = Get-SdatLogsRoot -Root $Root
    if (-not (Test-Path -LiteralPath $base)) { return @() }

    $issues = [System.Collections.Generic.List[object]]::new()
    $files = @(Get-ChildItem -LiteralPath $base -Filter "sdat-*.log" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 7)
    foreach ($file in $files) {
        foreach ($line in @(Get-Content -LiteralPath $file.FullName -Tail 400 -ErrorAction SilentlyContinue)) {
            if ($line -notmatch '^(?<at>\S+) \[(?<level>WARN|ERROR)\] \([^)]*\) (?<message>.*?)(?: \| \{.*\})?$') { continue }
            $issues.Add([pscustomobject]@{
                At = $Matches.at
                Level = $Matches.level
                Message = $Matches.message
            })
        }
    }
    return @($issues | Sort-Object At -Descending | Select-Object -First $Count)
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
    Invoke-SdatLogMaintenance -Root $Root
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
            Limit-SdatLogFile -Path $Ctx.LogPath
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
            Limit-SdatLogFile -Path $Path
            return
        } catch {
            if ($i -lt 2) { Start-Sleep -Milliseconds 80 }
        }
    }
}
