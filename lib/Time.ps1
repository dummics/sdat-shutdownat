Set-StrictMode -Version Latest

function Convert-ToLocalDateTime {
    param([AllowNull()][datetime]$Value)
    if ($null -eq $Value) { return $null }
    if ($Value.Kind -eq [System.DateTimeKind]::Utc) {
        return $Value.ToLocalTime()
    }
    if ($Value.Kind -eq [System.DateTimeKind]::Local) {
        return $Value
    }
    return [datetime]::SpecifyKind($Value, [System.DateTimeKind]::Local)
}

function Parse-HHMM {
    param([Parameter(Mandatory)][string]$Time)
    if ($Time -notmatch '^\d{4}$') { throw "Invalid time format. Use HHMM (e.g., 0030, 1345)." }
    $h = [int]$Time.Substring(0, 2)
    $m = [int]$Time.Substring(2, 2)
    if ($h -gt 23 -or $m -gt 59) { throw "Invalid time: $Time" }
    return [pscustomobject]@{ Hours = $h; Minutes = $m }
}

function Get-NextOccurrenceLocal {
    param(
        [Parameter(Mandatory)][int]$Hours,
        [Parameter(Mandatory)][int]$Minutes,
        [datetime]$Now = (Get-Date)
    )
    $target = $Now.Date.AddHours($Hours).AddMinutes($Minutes)
    if ($target -lt $Now) { $target = $target.AddDays(1) }
    return $target
}

function Format-LocalShort {
    param([Parameter(Mandatory)][datetime]$Value)
    $local = Convert-ToLocalDateTime -Value $Value
    return $local.ToString('yyyy-MM-dd HH:mm')
}

