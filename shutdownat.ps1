<#
    SDAT / shutdownat.ps1

    Usage:
      sdat                # show status
      sdat HHMM           # schedule one-time (volatile) shutdown
      sdat HHMM -p        # schedule daily (permanent) shutdown
      sdat -Test HHMM [-p]
      sdat -tui
      sdat -A             # cancel all SDAT + legacy tasks
#>

param(
    [Parameter(Position = 0)]
    [string]$Time,

    [switch]$Test,
    [switch]$A,
    [switch]$Clean,  # alias for -A (kept for wrapper compatibility)
    [switch]$P,      # permanent (daily)
    [switch]$Tui,

    [switch]$RunVolatile,
    [switch]$RunPermanent,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraArgs
)

Set-StrictMode -Version Latest

if ($ExtraArgs -and $ExtraArgs.Count -gt 0) {
    Write-Host "Unsupported parameter: $($ExtraArgs -join ' ')" -ForegroundColor Yellow
    Write-Host "Usage: sdat [HHMM [-p]] | sdat -Test HHMM [-p] | sdat -tui | sdat -A"
    exit 2
}

$root = Split-Path -Parent $PSCommandPath
. (Join-Path -Path $root -ChildPath "lib\\Config.ps1")
. (Join-Path -Path $root -ChildPath "lib\\State.ps1")
. (Join-Path -Path $root -ChildPath "lib\\Time.ps1")
. (Join-Path -Path $root -ChildPath "lib\\Tasks.ps1")
. (Join-Path -Path $root -ChildPath "lib\\Tui.ps1")

function Write-Info([string]$Msg) { Write-Host $Msg }

function Get-SdatStatusText {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)]$Config
    )
    $names = Get-SdatTaskNames
    $v = Get-TaskInfoSafe -TaskName $names.Volatile
    $pinfo = Get-TaskInfoSafe -TaskName $names.Permanent

    $lines = @()
    $lines += "GraceMinutes: $($Config.GraceMinutes)"

    if ($v.Exists -and $v.Info -and $v.Info.NextRunTime -gt [datetime]::MinValue) {
        $lines += "Volatile: scheduled for $(Format-LocalShort -Value $v.Info.NextRunTime)"
    } else {
        $lines += "Volatile: none"
    }

    if ($pinfo.Exists -and $pinfo.Info -and $pinfo.Info.NextRunTime -gt [datetime]::MinValue) {
        $lines += "Permanent: next run $(Format-LocalShort -Value $pinfo.Info.NextRunTime)"
    } elseif ($pinfo.Exists) {
        $lines += "Permanent: active"
    } else {
        $lines += "Permanent: none"
    }

    $suspendUntil = Parse-LocalDateTimeOrNull -Value $State.SuspendPermanentUntil
    if ($suspendUntil) {
        $remaining = $suspendUntil - (Get-Date)
        if ($remaining.TotalSeconds -gt 0) {
            $mins = [Math]::Ceiling($remaining.TotalMinutes)
            $lines += "Permanent suspend until: $(Format-LocalShort -Value $suspendUntil) (~${mins}m)"
        } else {
            $lines += "Permanent suspend until: $(Format-LocalShort -Value $suspendUntil) (expired)"
        }
    } else {
        $lines += "Permanent suspend until: none"
    }

    $legacyTasks = Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'ShutdownAt*' }
    $legacyCount = if ($legacyTasks) { ($legacyTasks | Measure-Object).Count } else { 0 }
    if ($legacyCount -gt 0) {
        $lines += "Legacy tasks present: ${legacyCount} (use: sdat -A)"
    }

    return ($lines -join "`n")
}

function Set-PermanentSuspendWindowFromVolatile {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][datetime]$VolatileTargetLocal,
        [Parameter(Mandatory)][int]$GraceMinutes
    )
    $until = $VolatileTargetLocal.AddMinutes($GraceMinutes)
    $State.SuspendPermanentUntil = $until.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    $State.SuspendSetAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    $State.SuspendReason = "volatile"
}

function Invoke-CancelAllAndResetState {
    $names = Get-SdatTaskNames
    Unregister-TaskIfExists -TaskName $names.Volatile
    Unregister-TaskIfExists -TaskName $names.Permanent
    $legacy = Remove-LegacyShutdownAtTasks
    $state = New-DefaultSdatState
    Save-SdatState -Root $root -State $state
    return [pscustomobject]@{ LegacyRemoved = $legacy; Names = $names }
}

function Invoke-RunVolatile {
    $config = Load-SdatConfig -Root $root
    $state = Load-SdatState -Root $root
    $names = Get-SdatTaskNames

    try { Unregister-TaskIfExists -TaskName $names.Volatile } catch { }

    $scheduledFor = Parse-LocalDateTimeOrNull -Value $state.Volatile.ScheduledFor
    if ($scheduledFor) {
        $maxDelay = [int]$config.MissedVolatileShutdownMaxDelayMinutes
        if ((Get-Date) -gt $scheduledFor.AddMinutes($maxDelay)) {
            $state.Volatile.LastMissedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
            $state.Volatile.ScheduledFor = $null
            Save-SdatState -Root $root -State $state
            exit 0
        }
    }

    $state.Volatile.LastExecutedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    $state.Volatile.ScheduledFor = $null
    $state.Volatile.CreatedAt = $null
    Save-SdatState -Root $root -State $state

    shutdown /s /f
}

function Invoke-RunPermanent {
    $config = Load-SdatConfig -Root $root
    $state = Load-SdatState -Root $root

    $suspendUntil = Parse-LocalDateTimeOrNull -Value $state.SuspendPermanentUntil
    if ($suspendUntil -and (Get-Date) -lt $suspendUntil) {
        $state.Permanent.LastSkippedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
        Save-SdatState -Root $root -State $state
        exit 0
    }

    $state.Permanent.LastExecutedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    Save-SdatState -Root $root -State $state
    shutdown /s /f
}

function Invoke-CancelVolatile {
    $names = Get-SdatTaskNames
    Unregister-TaskIfExists -TaskName $names.Volatile
    $state = Load-SdatState -Root $root
    $state.Volatile.ScheduledFor = $null
    $state.Volatile.CreatedAt = $null
    Save-SdatState -Root $root -State $state
}

function Invoke-CancelPermanent {
    $names = Get-SdatTaskNames
    Unregister-TaskIfExists -TaskName $names.Permanent
}

function Invoke-SchedulePermanentDaily {
    param([Parameter(Mandatory)][int]$Hours, [Parameter(Mandatory)][int]$Minutes)
    $names = Get-SdatTaskNames
    $null = Remove-LegacyShutdownAtTasks
    Register-PermanentShutdownTaskDaily -Hours $Hours -Minutes $Minutes -ScriptPath $PSCommandPath
    return "Permanent shutdown scheduled daily at $($Hours.ToString('D2')):$($Minutes.ToString('D2')) (task: $($names.Permanent))"
}

function Invoke-ScheduleVolatileOnce {
    param([Parameter(Mandatory)][int]$Hours, [Parameter(Mandatory)][int]$Minutes)
    $config = Load-SdatConfig -Root $root
    $state = Load-SdatState -Root $root
    $names = Get-SdatTaskNames

    $target = Get-NextOccurrenceLocal -Hours $Hours -Minutes $Minutes
    $targetStr = Format-LocalShort -Value $target

    $null = Remove-LegacyShutdownAtTasks
    Register-VolatileShutdownTask -TargetLocal $target -ScriptPath $PSCommandPath
    $state.Volatile.ScheduledFor = $target.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    $state.Volatile.CreatedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    Set-PermanentSuspendWindowFromVolatile -State $state -VolatileTargetLocal $target -GraceMinutes ([int]$config.GraceMinutes)
    Save-SdatState -Root $root -State $state

    return "Volatile shutdown scheduled: ${targetStr} (task: $($names.Volatile))"
}

if ($RunVolatile) { Invoke-RunVolatile; exit }
if ($RunPermanent) { Invoke-RunPermanent; exit }

if ($Clean) { $A = $true }

if ($A) {
    $result = Invoke-CancelAllAndResetState
    Write-Info "Canceled: $($result.Names.Volatile), $($result.Names.Permanent) + legacy=$($result.LegacyRemoved)"
    exit 0
}

if ($Tui) {
    $notice = $null
    while ($true) {
        $config = Load-SdatConfig -Root $root
        $state = Load-SdatState -Root $root
        $header = Get-SdatStatusText -State $state -Config $config

        $sel = Show-SdatMainMenu -Title "SDAT" -Header $header -Notice $notice
        $notice = $null
        if ($null -eq $sel) { exit 0 }

        if ($sel -eq 0) {
            $input = Read-LineWithEsc -Title "SDAT" -Header $header -Prompt "Set one-time (volatile) shutdown."
            if ($null -eq $input) { continue }
            if ([string]::IsNullOrWhiteSpace($input)) {
                Invoke-CancelVolatile
                $notice = New-TuiNotice -Kind "info" -Message "One-time shutdown canceled."
                continue
            }
            try {
                $parsed = Parse-HHMM -Time $input
                $msg = Invoke-ScheduleVolatileOnce -Hours $parsed.Hours -Minutes $parsed.Minutes
                $notice = New-TuiNotice -Kind "info" -Message $msg
            } catch {
                $notice = New-TuiNotice -Kind "error" -Message $_.Exception.Message
            }
            continue
        }

        if ($sel -eq 1) {
            $input = Read-LineWithEsc -Title "SDAT" -Header $header -Prompt "Set daily (permanent) shutdown."
            if ($null -eq $input) { continue }
            if ([string]::IsNullOrWhiteSpace($input)) {
                Invoke-CancelPermanent
                $notice = New-TuiNotice -Kind "info" -Message "Daily shutdown canceled."
                continue
            }
            try {
                $parsed = Parse-HHMM -Time $input
                $msg = Invoke-SchedulePermanentDaily -Hours $parsed.Hours -Minutes $parsed.Minutes
                $notice = New-TuiNotice -Kind "info" -Message $msg
            } catch {
                $notice = New-TuiNotice -Kind "error" -Message $_.Exception.Message
            }
            continue
        }
    }
    exit 0
}

if (-not $Time) {
    $config = Load-SdatConfig -Root $root
    $state = Load-SdatState -Root $root
    Write-Info (Get-SdatStatusText -State $state -Config $config)
    exit 0
}

try {
    $parsed = Parse-HHMM -Time $Time
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Yellow
    exit 1
}
$config = Load-SdatConfig -Root $root
$state = Load-SdatState -Root $root
$names = Get-SdatTaskNames

if ($P) {
    if ($Test) {
        Write-Info "Would schedule PERMANENT daily shutdown at $($parsed.Hours.ToString('D2')):$($parsed.Minutes.ToString('D2')) (TEST MODE)"
        exit 0
    }
    Write-Info (Invoke-SchedulePermanentDaily -Hours $parsed.Hours -Minutes $parsed.Minutes)
    exit 0
}

if ($Test) {
    $target = Get-NextOccurrenceLocal -Hours $parsed.Hours -Minutes $parsed.Minutes
    $targetStr = Format-LocalShort -Value $target
    Write-Info "Would schedule VOLATILE shutdown at ${targetStr} (TEST MODE)"
    Write-Info "Would suspend permanent until $(Format-LocalShort -Value $target.AddMinutes([int]$config.GraceMinutes))"
    exit 0
}

Write-Info (Invoke-ScheduleVolatileOnce -Hours $parsed.Hours -Minutes $parsed.Minutes)
