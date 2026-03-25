<#
    SDAT / shutdownat.ps1

    Usage:
      sdat                # show status
      sdat HHMM           # schedule one-time (volatile) shutdown
      sdat HHMM -p        # schedule daily (permanent) shutdown
      ssat HHMM           # schedule one-time (volatile) suspend
      ssat HHMM -p        # schedule daily (permanent) suspend
      sdat -Test HHMM [-p]
      ssat -Test HHMM [-p]
      sdat -tui
      ssat -tui
      sdat -a             # cancel one-time (volatile) task
      sdat -aa            # cancel all SDAT + legacy tasks
      sdat -s             # toggle skip for next permanent run
      sdat -h             # help
#>

param(
    [Parameter(Position = 0)]
    [string]$Time,

    [switch]$Test,
    [switch]$A,      # cancel volatile only
    [switch]$AA,     # cancel all
    [switch]$Clean,  # alias for -AA (kept for wrapper compatibility)
    [switch]$P,      # permanent (daily)
    [switch]$Suspend, # schedule/run suspend instead of shutdown
    [switch]$Tui,
    [Alias('S')][switch]$SkipPermanent,     # skip next permanent run once
    [Alias('h')][switch]$Help,
    [Alias('f')][switch]$Force,
    [switch]$Status,

    [switch]$RunVolatile,
    [switch]$RunPermanent,
    [switch]$NotifyStatus,
    [switch]$DryRun,
    [switch]$SelfTest,

    # Internal/testing: use a separate task+data namespace.
    [string]$Profile,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraArgs
)

Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSCommandPath
. (Join-Path -Path $root -ChildPath "lib\\Config.ps1")
. (Join-Path -Path $root -ChildPath "lib\\State.ps1")
. (Join-Path -Path $root -ChildPath "lib\\Time.ps1")
. (Join-Path -Path $root -ChildPath "lib\\Tasks.ps1")
. (Join-Path -Path $root -ChildPath "lib\\Tui.ps1")
. (Join-Path -Path $root -ChildPath "lib\\Notify.ps1")
. (Join-Path -Path $root -ChildPath "lib\\Log.ps1")
. (Join-Path -Path $root -ChildPath "lib\\SelfTest.ps1")

function Show-SdatHelp {
    $lines = @(
        "Usage: sdat|ssat [HHMM / HH:MM [-p]] | sdat|ssat -Test HHMM [-p] | sdat|ssat -tui | sdat|ssat -a | sdat|ssat -aa | sdat|ssat -s | sdat|ssat -h",
        "",
        "Commands:",
        "  (no args)            show status and a notification",
        "  -Status              explicit status check (same as no args)",
        "  HHMM / HH:MM [-p]    schedule a volatile (-p omitted) or permanent (-p) shutdown/suspend",
        "  -Test HHMM [-p]      dry run the volatile or permanent power action",
        "  -Suspend             use suspend action (ssat wrapper always sets this)",
        "  -tui                 open the interactive configuration UI",
        "  -a                   cancel the pending one-time action (use -f / -Force to skip confirm)",
        "  -aa / -Clean         cancel every SDAT and legacy scheduled task (use -f / -Force to skip confirm)",
        "  -s / -SkipPermanent  toggle skip for the next permanent run",
        "  -h / -Help           show this help output",
        "",
        "Special modes: -NotifyStatus, -RunVolatile, -RunPermanent, -SelfTest"
    )
    foreach ($line in $lines) { Write-Host $line }
}

function Test-FromWinR { return ($env:SDAT_FROM_WINR -eq '1') }

function Send-SdatNotification {
    param([AllowNull()][string]$Message)
    if (-not (Test-FromWinR)) { return }
    $title = "SDAT"
    $msg = Truncate-NotificationText -Text (Convert-ToSingleLine -Text $Message) -MaxLength 240
    if ([string]::IsNullOrWhiteSpace($msg)) { return }
    $scriptRoot = Split-Path -Parent $PSCommandPath
    $notifyScript = Join-Path -Path $scriptRoot -ChildPath "tools\\notify-message.ps1"
    $args = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $notifyScript,
        "-Title", $title,
        "-Message", $msg,
        "-TimeoutMs", "6500"
    )
    Start-Process -WindowStyle Hidden -FilePath "powershell.exe" -ArgumentList $args | Out-Null
}

function Write-Info([string]$Msg) {
    Write-Host $Msg
    Send-SdatNotification -Message $Msg
}

if ($ExtraArgs -and $ExtraArgs.Count -gt 0) {
    Write-Host "Unsupported parameter: $($ExtraArgs -join ' ')" -ForegroundColor Yellow
    Show-SdatHelp
    Send-SdatNotification -Message "Unsupported parameter. Use: sdat -h"
    exit 2
}

if ($Status -and $Time) {
    Write-Host "Cannot use -Status with a time argument. Use one command at a time." -ForegroundColor Yellow
    Show-SdatHelp
    exit 2
}

if ($Help) {
    Show-SdatHelp
    Send-SdatNotification -Message "Usage: sdat|ssat [HHMM / HH:MM [-p]] | sdat|ssat -Test HHMM [-p] | sdat|ssat -tui | sdat|ssat -a | sdat|ssat -aa | sdat|ssat -s | sdat|ssat -h"
    exit 0
}

function Resolve-SdatActionType {
    param(
        [AllowNull()][string]$Value,
        [string]$Default = "shutdown"
    )
    if ([string]::IsNullOrWhiteSpace($Value)) { return $Default }
    $v = $Value.Trim().ToLowerInvariant()
    if ($v -in @("shutdown", "suspend")) { return $v }
    return $Default
}

function Get-SdatRequestedActionType {
    if ($Suspend) { return "suspend" }
    return "shutdown"
}

function Get-SdatActionLabel {
    param([Parameter(Mandatory)][string]$ActionType)
    if ((Resolve-SdatActionType -Value $ActionType) -eq "suspend") { return "suspend" }
    return "shutdown"
}

function Normalize-TimeInput {
    param([Parameter(Mandatory)][string]$Value)
    $raw = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($raw)) {
        throw "Missing time value. Use HHMM (e.g., 0930) or HH:MM."
    }

    $digits = $null
    if ($raw -match '^\d{3,4}$') {
        $digits = $raw
    } elseif ($raw -match '^\d{1,2}:\d{2}$') {
        $digits = ($raw -replace ':', '')
    } else {
        throw "Invalid time format. Use HHMM (e.g., 0930) or HH:MM."
    }

    if ($digits.Length -eq 3) { $digits = "0$digits" }
    return $digits
}

function Parse-TimeInput {
    param([Parameter(Mandatory)][string]$Value)
    return Parse-HHMM -Time (Normalize-TimeInput -Value $Value)
}

function Format-TimeRemaining {
    param([Parameter(Mandatory)][datetime]$Target)
    $targetLocal = Convert-ToLocalDateTime -Value $Target
    $nowOffset = [System.DateTimeOffset]::Now
    $remaining = ([System.DateTimeOffset]$targetLocal) - $nowOffset
    if ($remaining.TotalSeconds -le 0) { return "now" }

    $totalMinutes = [int][Math]::Ceiling($remaining.TotalMinutes)
    if ($totalMinutes -le 1) { return "<1m" }

    $days = [int][Math]::Floor($totalMinutes / 1440)
    $hours = [int][Math]::Floor(($totalMinutes % 1440) / 60)
    $mins = [int]($totalMinutes % 60)

    if ($days -gt 0) { return "{0}d {1}h {2}m" -f $days, $hours, $mins }
    if ($hours -gt 0) { return "{0}h {1}m" -f $hours, $mins }
    return "{0}m" -f $totalMinutes
}

function Ask-Confirmation {
    param([Parameter(Mandatory)][string]$Prompt)
    if ($Force) { return $true }
    $reply = $null
    try {
        $reply = Read-Host "$Prompt (y/N)"
    } catch {
        return $false
    }
    return $reply -match '^(?i:y|yes)$'
}

function Test-SdatDryRun {
    if ($DryRun) { return $true }
    if ($env:SDAT_DRYRUN -eq '1') { return $true }
    return $false
}

$script:sdatProfile = Get-SdatProfileSafe -Profile $Profile
$script:logMode = if ($SelfTest) { "selftest" }
elseif ($RunVolatile) { "run-volatile" }
elseif ($RunPermanent) { "run-permanent" }
elseif ($NotifyStatus) { "notify" }
elseif ($Tui) { "tui" }
elseif ($AA) { "cancel-all" }
elseif ($A) { "cancel-volatile" }
elseif ($P) { "schedule-permanent" }
elseif ($Time) { "schedule-volatile" }
else { "status" }

$script:logCtx = $null
try {
    $script:logCtx = New-SdatLogContext -Root $root -Profile $script:sdatProfile -Mode $script:logMode
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "START" -Data @{
        Profile = $script:sdatProfile
        Args = $MyInvocation.Line
        DryRun = (Test-SdatDryRun)
    }
} catch { }

function Invoke-SdatShutdown {
    param(
        [Parameter(Mandatory)][string]$Reason,
        [Parameter(Mandatory)][string]$ActionType
    )
    $action = Resolve-SdatActionType -Value $ActionType
    $dry = Test-SdatDryRun
    if ($dry) {
        Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "DRY RUN: power action suppressed" -Data @{ Reason = $Reason; ActionType = $action }
        return
    }

    if ($action -eq "suspend") {
        Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Executing suspend" -Data @{ Reason = $Reason }
        $ok = $false
        try {
            Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop | Out-Null
            $ok = [System.Windows.Forms.Application]::SetSuspendState([System.Windows.Forms.PowerState]::Suspend, $false, $false)
        } catch { }
        if (-not $ok) {
            cmd /c "rundll32.exe powrprof.dll,SetSuspendState 0,0,0" | Out-Null
        }
        return
    }

    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Executing shutdown" -Data @{ Reason = $Reason }
    shutdown /s /f
}

function Invoke-CleanupStaleVolatile {
    $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    $v = Get-TaskInfoSafe -TaskName $names.Volatile

    $scheduledFor = Parse-LocalDateTimeOrNull -Value $state.Volatile.ScheduledFor
    if (-not $scheduledFor) { return }

    $maxDelay = [int]$config.MissedVolatileShutdownMaxDelayMinutes
    if ((Get-Date) -le $scheduledFor.AddMinutes($maxDelay)) { return }

    if ($v.Exists) { Unregister-TaskIfExists -TaskName $names.Volatile }
    $state.Volatile.LastMissedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    $state.Volatile.ScheduledFor = $null
    $state.Volatile.CreatedAt = $null
    $state.Volatile.ActionType = $null
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
    Write-SdatLog -Ctx $script:logCtx -Level "WARN" -Message "Volatile cleanup (missed); task removed" -Data @{
        ScheduledFor = $scheduledFor
        MaxDelayMinutes = $maxDelay
    }
}

if (-not $RunVolatile -and -not $RunPermanent) {
    try { Invoke-CleanupStaleVolatile } catch { }
}

function Get-SdatStatusText {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)]$Config
    )
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    $v = Get-TaskInfoSafe -TaskName $names.Volatile
    $pinfo = Get-TaskInfoSafe -TaskName $names.Permanent

    $vol = "none"
    if ($v.Exists -and $v.Info -and $v.Info.NextRunTime -gt [datetime]::MinValue) {
        $volAction = Get-SdatActionLabel -ActionType (Resolve-SdatActionType -Value $State.Volatile.ActionType)
        $volRunAt = Convert-ToLocalDateTime -Value $v.Info.NextRunTime
        $vol = ("{0} @ {1} (in {2})" -f $volAction, (Format-LocalShort -Value $volRunAt), (Format-TimeRemaining -Target $volRunAt))
    }

    $perm = "none"
    if ($pinfo.Exists -and $pinfo.Info -and $pinfo.Info.NextRunTime -gt [datetime]::MinValue) {
        $permAction = Get-SdatActionLabel -ActionType (Resolve-SdatActionType -Value $State.Permanent.ActionType)
        $permRunAt = Convert-ToLocalDateTime -Value $pinfo.Info.NextRunTime
        $perm = ("{0} @ {1} (in {2})" -f $permAction, (Format-LocalShort -Value $permRunAt), (Format-TimeRemaining -Target $permRunAt))
    } elseif ($pinfo.Exists) {
        $permAction = Get-SdatActionLabel -ActionType (Resolve-SdatActionType -Value $State.Permanent.ActionType)
        $perm = ("{0} @ active" -f $permAction)
    }

    $suspend = "none"
    if ($pinfo.Exists) {
        $at = $null
        if ($pinfo.Info -and $pinfo.Info.NextRunTime -gt [datetime]::MinValue) { $at = (Convert-ToLocalDateTime -Value $pinfo.Info.NextRunTime) }
        $sup = Get-PermanentSuppressionAt -State $State -Config $Config -AtLocal $at -VolatileTaskExists:($v.Exists)
        if ($sup.Suppressed -and $sup.Until) {
            $kindLabel = if ($sup.Kind -eq "manual-skip") {
                "manual"
            } elseif ($sup.Kind -eq "volatile-upcoming") {
                "volatile-window"
            } else {
                "grace"
            }
            $suspend = ("{0} [{1}]" -f (Format-LocalShort -Value $sup.Until), $kindLabel)
        }
    }

    $legacyTasks = Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'ShutdownAt*' }
    $legacyCount = if ($legacyTasks) { ($legacyTasks | Measure-Object).Count } else { 0 }
    $legacy = if ($legacyCount -gt 0) { " | Legacy: ${legacyCount}" } else { "" }

    return "One-time: $vol | Daily: $perm | Suppression: $suspend | Grace: $($Config.GraceMinutes)m${legacy}"
}

function Get-SdatStatusSummaryLine {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)]$Config
    )
    return (Get-SdatStatusText -State $State -Config $Config)
}

function Get-PermanentSuppressionAt {
    param(
        [Parameter(Mandatory)]$State,
    [Parameter(Mandatory)]$Config,
    [AllowNull()][datetime]$AtLocal,
    [switch]$VolatileTaskExists
)

    $graceMinutes = [int]$Config.GraceMinutes
    $at = if ($AtLocal) { Convert-ToLocalDateTime -Value $AtLocal } else { Get-Date }
    $manualUntil = Parse-LocalDateTimeOrNull -Value $State.SuspendPermanentUntil
    if ($manualUntil -and $at -lt $manualUntil) {
        return [pscustomobject]@{
            Suppressed = $true
            Kind = "manual-skip"
            Until = $manualUntil
            Data = @{
                Reason = $State.SuspendReason
                SetAt = $State.SuspendSetAt
                SuppressUntil = $manualUntil.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
            }
        }
    }

    $vScheduled = if ($VolatileTaskExists) { Parse-LocalDateTimeOrNull -Value $State.Volatile.ScheduledFor } else { $null }
    if ($vScheduled -and $at -lt $vScheduled) {
        $deltaMin = ($vScheduled - $at).TotalMinutes
        if ($deltaMin -le $graceMinutes) {
            return [pscustomobject]@{
                Suppressed = $true
                Kind = "volatile-upcoming"
                Until = $vScheduled.AddMinutes($graceMinutes)
                Data = @{ VolatileScheduledFor = $vScheduled; DeltaMinutes = $deltaMin; GraceMinutes = $graceMinutes }
            }
        }
    }

    $vLast = Parse-LocalDateTimeOrNull -Value $State.Volatile.LastExecutedAt
    if ($vLast -and $vLast -lt $at) {
        $deltaMin = ($at - $vLast).TotalMinutes
        if ($deltaMin -le $graceMinutes) {
            return [pscustomobject]@{
                Suppressed = $true
                Kind = "volatile-recent"
                Until = $vLast.AddMinutes($graceMinutes)
                Data = @{ VolatileLastExecutedAt = $vLast; DeltaMinutes = $deltaMin; GraceMinutes = $graceMinutes }
            }
        }
    }

    return [pscustomobject]@{ Suppressed = $false; Kind = "none"; Until = $null; Data = $null }
}

function Invoke-CancelAllAndResetState {
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    Unregister-TaskIfExists -TaskName $names.Volatile
    Unregister-TaskIfExists -TaskName $names.Permanent
    $legacy = Remove-LegacyShutdownAtTasks -Force:([string]::IsNullOrWhiteSpace($script:sdatProfile))
    $state = New-DefaultSdatState
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
    return [pscustomobject]@{ LegacyRemoved = $legacy; Names = $names }
}

function Invoke-RunVolatile {
    $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Volatile run started"

    try { Unregister-TaskIfExists -TaskName $names.Volatile } catch { }

    $scheduledFor = Parse-LocalDateTimeOrNull -Value $state.Volatile.ScheduledFor
    if ($scheduledFor) {
        $maxDelay = [int]$config.MissedVolatileShutdownMaxDelayMinutes
        if ((Get-Date) -gt $scheduledFor.AddMinutes($maxDelay)) {
            $state.Volatile.LastMissedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
            $state.Volatile.ScheduledFor = $null
            $state.Volatile.ActionType = $null
            Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
            Write-SdatLog -Ctx $script:logCtx -Level "WARN" -Message "Volatile missed (too late); no action executed" -Data @{ ScheduledFor = $scheduledFor; MaxDelayMinutes = $maxDelay }
            return 0
        }
    }

    $state.Volatile.LastExecutedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    $state.Volatile.ScheduledFor = $null
    $state.Volatile.CreatedAt = $null
    $action = if ($Suspend) { "suspend" } else { Resolve-SdatActionType -Value $state.Volatile.ActionType }
    $state.Volatile.ActionType = $null
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $state

    Invoke-SdatShutdown -Reason "volatile" -ActionType $action
    return 0
}

function Invoke-RunPermanent {
    $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Permanent run started"

    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    $v = Get-TaskInfoSafe -TaskName $names.Volatile
    if (-not $v.Exists -and -not [string]::IsNullOrWhiteSpace($state.Volatile.ScheduledFor)) {
        $state.Volatile.ScheduledFor = $null
        $state.Volatile.CreatedAt = $null
        $state.Volatile.ActionType = $null
        Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
        Write-SdatLog -Ctx $script:logCtx -Level "WARN" -Message "Cleared stale volatile schedule from state (task missing)"
    }

    $sup = Get-PermanentSuppressionAt -State $state -Config $config -AtLocal (Get-Date) -VolatileTaskExists:($v.Exists)
    if ($sup.Suppressed) {
        $state.Permanent.LastSkippedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
        if ($sup.Kind -eq "manual-skip") {
            $state.SuspendPermanentUntil = $null
            $state.SuspendSetAt = $null
            $state.SuspendReason = $null
        }
        Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
        Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message ("Permanent skipped ({0})" -f $sup.Kind) -Data $sup.Data
        return 0
    }

    $pinfo = Get-TaskInfoSafe -TaskName $names.Permanent
    $scheduledAt = if ($pinfo.Task) { Get-TaskStartBoundaryLocal -Task $pinfo.Task } else { $null }
    $maxDelay = [int]$config.MissedPermanentShutdownMaxDelayMinutes
    if ($scheduledAt -and $maxDelay -ge 0) {
        $now = Get-Date
        $scheduledToday = $now.Date.Add($scheduledAt.TimeOfDay)
        $scheduledMostRecent = if ($now -lt $scheduledToday) { $scheduledToday.AddDays(-1) } else { $scheduledToday }
        $lateMinutes = ($now - $scheduledMostRecent).TotalMinutes
        if ($lateMinutes -gt $maxDelay) {
            $state.Permanent.LastSkippedAt = $now.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
            Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
            Write-SdatLog -Ctx $script:logCtx -Level "WARN" -Message "Permanent missed (too late); no action executed" -Data @{
                ScheduledFor = $scheduledMostRecent
                LateMinutes = $lateMinutes
                MaxDelayMinutes = $maxDelay
            }
            return 0
        }
    }

    $state.Permanent.LastExecutedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    $action = if ($Suspend) { "suspend" } else { Resolve-SdatActionType -Value $state.Permanent.ActionType }
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
    Invoke-SdatShutdown -Reason "permanent" -ActionType $action
    return 0
}

function Invoke-CancelVolatile {
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    Unregister-TaskIfExists -TaskName $names.Volatile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    $state.Volatile.ScheduledFor = $null
    $state.Volatile.CreatedAt = $null
    $state.Volatile.ActionType = $null
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
    $abortResult = cmd /c "shutdown /a 2>&1"
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Aborted pending system shutdown"
    } else {
        # Error 1116 means no shutdown is in progress - this is expected when canceling already-canceled tasks
        # Suppress the error message and log silently
        if ($exitCode -ne 1116) {
            Write-SdatLog -Ctx $script:logCtx -Level "WARN" -Message "Failed to abort pending system shutdown (exit code: $exitCode)"
        }
    }
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Canceled one-time action"
}

function Invoke-CancelPermanent {
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    Unregister-TaskIfExists -TaskName $names.Permanent
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Canceled daily action"
}

function Invoke-SchedulePermanentDaily {
    param(
        [Parameter(Mandatory)][int]$Hours,
        [Parameter(Mandatory)][int]$Minutes,
        [Parameter(Mandatory)][ValidateSet("shutdown", "suspend")][string]$ActionType
    )
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    $null = Remove-LegacyShutdownAtTasks -Force:([string]::IsNullOrWhiteSpace($script:sdatProfile))
    Register-PermanentShutdownTaskDaily -Hours $Hours -Minutes $Minutes -ScriptPath $PSCommandPath -Profile $script:sdatProfile -SuspendAction:($ActionType -eq "suspend") -DryRunAction:(Test-SdatDryRun)
    $state.Permanent.ActionType = (Resolve-SdatActionType -Value $ActionType)
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Scheduled daily action" -Data @{ Hours = $Hours; Minutes = $Minutes; Task = $names.Permanent; ActionType = $state.Permanent.ActionType }
    return ("Permanent {0} scheduled daily at {1}:{2} (task: {3})" -f (Get-SdatActionLabel -ActionType $state.Permanent.ActionType), $Hours.ToString('D2'), $Minutes.ToString('D2'), $names.Permanent)
}

function Invoke-SkipPermanentOnce {
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    $manualUntil = Parse-LocalDateTimeOrNull -Value $state.SuspendPermanentUntil
    if ($manualUntil -and (Get-Date) -lt $manualUntil) {
        $state.SuspendPermanentUntil = $null
        $state.SuspendSetAt = $null
        $state.SuspendReason = $null
        Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
        Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Manual skip cleared for permanent shutdown"
        return "Permanent shutdown skip cleared (daily schedule re-enabled)."
    }

    $info = Get-TaskInfoSafe -TaskName $names.Permanent
    if (-not $info.Exists) {
        return "No permanent shutdown task found to skip."
    }
    $nextRun = Convert-ToLocalDateTime -Value $info.Info.NextRunTime
    if (-not $nextRun -or $nextRun -le [datetime]::MinValue) {
        $nextRun = (Get-Date).AddDays(1)
    }
    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    $targetStr = Format-LocalShort -Value $nextRun
    $suppressUntil = $nextRun.AddMinutes(5)
    $state.SuspendPermanentUntil = $suppressUntil.ToString("o", $culture)
    $state.SuspendSetAt = (Get-Date).ToString("o", $culture)
    $state.SuspendReason = ("manual-skip for {0}" -f $targetStr)
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Manual skip scheduled for permanent shutdown" -Data @{
        NextRun = $nextRun.ToString("o", $culture)
        SuppressUntil = $suppressUntil.ToString("o", $culture)
    }
    return "Permanent shutdown at $targetStr will be skipped once."
}

function Invoke-ScheduleVolatileOnce {
    param(
        [Parameter(Mandatory)][int]$Hours,
        [Parameter(Mandatory)][int]$Minutes,
        [Parameter(Mandatory)][ValidateSet("shutdown", "suspend")][string]$ActionType
    )
    $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    $names = Get-SdatTaskNames -Profile $script:sdatProfile

    $target = Get-NextOccurrenceLocal -Hours $Hours -Minutes $Minutes
    $targetStr = Format-LocalShort -Value $target

    $null = Remove-LegacyShutdownAtTasks -Force:([string]::IsNullOrWhiteSpace($script:sdatProfile))
    Register-VolatileShutdownTask -TargetLocal $target -ScriptPath $PSCommandPath -Profile $script:sdatProfile -SuspendAction:($ActionType -eq "suspend") -DryRunAction:(Test-SdatDryRun)
    $state.Volatile.ScheduledFor = $target.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    $state.Volatile.CreatedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    $state.Volatile.ActionType = (Resolve-SdatActionType -Value $ActionType)
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Scheduled one-time action" -Data @{ Target = $target.ToString("o"); Task = $names.Volatile; GraceMinutes = [int]$config.GraceMinutes; ActionType = $state.Volatile.ActionType }

    return ("Volatile {0} scheduled: {1} (task: {2})" -f (Get-SdatActionLabel -ActionType $state.Volatile.ActionType), $targetStr, $names.Volatile)
}

if ($RunVolatile) { exit (Invoke-RunVolatile) }
if ($RunPermanent) { exit (Invoke-RunPermanent) }

if ($SelfTest) {
    $profile = if ([string]::IsNullOrWhiteSpace($script:sdatProfile)) { "selftest" } else { $script:sdatProfile }
    $summary = Invoke-SdatSelfTest -Root $root -ScriptPath $PSCommandPath -Profile $profile -LogCtx $script:logCtx
    $summary | ConvertTo-Json -Depth 20 | Write-Output
    exit $(if ($summary -and $summary.passed) { 0 } else { 1 })
}

if ($NotifyStatus) {
    $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    $title = "SDAT"
    $msg = Get-SdatStatusSummaryLine -State $state -Config $config
    $msg = Truncate-NotificationText -Text (Convert-ToSingleLine -Text $msg) -MaxLength 240
    $ok = Show-WindowsBalloonNotification -Title $title -Message $msg -TimeoutMs 6500
    if (-not $ok) {
        Write-Info (Get-SdatStatusText -State $state -Config $config)
    }
    exit 0
}

if ($SkipPermanent) {
    $msg = Invoke-SkipPermanentOnce
    Write-Info $msg
    exit 0
}

if ($Clean) { $AA = $true }

if ($AA) {
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    $v = Get-TaskInfoSafe -TaskName $names.Volatile
    $p = Get-TaskInfoSafe -TaskName $names.Permanent
    $legacyTasks = Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'ShutdownAt*' }
    $legacyCount = if ($legacyTasks) { ($legacyTasks | Measure-Object).Count } else { 0 }
    if (-not ($v.Exists -or $p.Exists -or $legacyCount -gt 0)) {
        Write-Info "No scheduled tasks to cancel."
        exit 0
    }

    if (-not (Ask-Confirmation -Prompt "Remove all SDAT + legacy scheduled tasks?")) {
        Write-Info "Cancellation aborted."
        exit 0
    }

    $result = Invoke-CancelAllAndResetState
    $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    Write-Info ("Canceled all scheduled power tasks. Removed legacy tasks: {0}. Status: {1}" -f $result.LegacyRemoved, (Get-SdatStatusText -State $state -Config $config))
    exit 0
}

if ($A) {
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    $v = Get-TaskInfoSafe -TaskName $names.Volatile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    $actionLabel = Get-SdatActionLabel -ActionType (Resolve-SdatActionType -Value $state.Volatile.ActionType)
    if (-not $v.Exists) {
        Write-Info ("No pending one-time {0} to cancel." -f $actionLabel)
        exit 0
    }
    if (-not (Ask-Confirmation -Prompt ("Cancel one-time scheduled {0}?" -f $actionLabel))) {
        Write-Info "Cancellation aborted."
        exit 0
    }
    Invoke-CancelVolatile
    $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    Write-Info ("Canceled one-time action. Status: {0}" -f (Get-SdatStatusText -State $state -Config $config))
    exit 0
}

if ($Tui) {
    $notice = $null
    $tuiActionType = Get-SdatRequestedActionType
    while ($true) {
        $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile      
        $state = Load-SdatState -Root $root -Profile $script:sdatProfile        
        $header = Get-SdatStatusText -State $state -Config $config
        $manualUntil = Parse-LocalDateTimeOrNull -Value $state.SuspendPermanentUntil
        $skipActive = ($manualUntil -and (Get-Date) -lt $manualUntil)
        $skipLabel = if ($skipActive) { "Toggle skip next permanent (ON)" } else { "Toggle skip next permanent (off)" }
        $actionLabel = (Get-SdatActionLabel -ActionType $tuiActionType)
        $options = @(
            "One-time (volatile) [$actionLabel]",
            "Daily (permanent) [$actionLabel]",
            "Toggle action mode ($actionLabel)",
            $skipLabel
        )

        $sel = Show-SdatMainMenu -Title "SDAT" -Header $header -Notice $notice -Options $options  
        $notice = $null
        if ($null -eq $sel) { exit 0 }

        if ($sel -eq 99) {
            function Read-LastSelfTestSummary {
                param([Parameter(Mandatory)][string]$JsonlPath)
                if (-not (Test-Path -LiteralPath $JsonlPath)) { return $null }
                $lines = Get-Content -LiteralPath $JsonlPath -Tail 200 -ErrorAction SilentlyContinue
                if (-not $lines) { return $null }
                $summaries = @()
                foreach ($l in $lines) {
                    if ([string]::IsNullOrWhiteSpace($l)) { continue }
                    try {
                        $obj = $l | ConvertFrom-Json -ErrorAction Stop
                        if ($obj.type -eq "summary") { $summaries += $obj }
                    } catch { }
                }
                if ($summaries.Count -eq 0) { return $null }
                return $summaries[-1]
            }

            function Show-TextScreen {
                param(
                    [Parameter(Mandatory)][string]$Title,
                    [Parameter(Mandatory)][string[]]$Lines
                )
                while ($true) {
                    try { [Console]::Clear() } catch { try { Clear-Host } catch { } }
                    Write-Host $Title -ForegroundColor Cyan
                    Write-Hr
                    foreach ($ln in $Lines) { Write-Host $ln }
                    Write-Hr
                    Write-Host "Esc=back" -ForegroundColor DarkGray
                    $k = [Console]::ReadKey($true)
                    if ($k.Key -eq [ConsoleKey]::Escape) { return }
                }
            }

            $diagNotice = $null
            $selfTestProfile = "selftest"
            while ($true) {
                $paths = [pscustomobject]@{
                    Jsonl = (Get-SdatTestResultsPath -Root $root -Profile $selfTestProfile)
                    Log = (Get-SdatLogFilePath -Root $root -Profile $selfTestProfile)
                }
                $last = Read-LastSelfTestSummary -JsonlPath $paths.Jsonl
                $diagHeader = "Self-test profile: $selfTestProfile"
                if ($last) {
                    $diagHeader += "`nLast run: $($last.startedAt) => $($last.endedAt) | Passed: $($last.passed) ($($last.passedCount)/$($last.passedCount + $last.failedCount))"
                } else {
                    $diagHeader += "`nLast run: none"
                }

                $dsel = Show-SdatDiagnosticsMenu -Title "SDAT Diagnostics" -Header $diagHeader -Notice $diagNotice
                $diagNotice = $null
                if ($null -eq $dsel -or $dsel -eq 4) { break }

                if ($dsel -eq 0) {
                    $testCtx = New-SdatLogContext -Root $root -Profile $selfTestProfile -Mode "selftest"
                    $summary = Invoke-SdatSelfTest -Root $root -ScriptPath $PSCommandPath -Profile $selfTestProfile -LogCtx $testCtx
                    $diagNotice = New-TuiNotice -Kind (if ($summary.passed) { "info" } else { "error" }) -Message ("Self-test {0}: {1} passed, {2} failed" -f ($(if ($summary.passed) { "PASS" } else { "FAIL" })), $summary.passedCount, $summary.failedCount)
                    continue
                }

                if ($dsel -eq 1) {
                    if (-not $last) {
                        $diagNotice = New-TuiNotice -Kind "error" -Message "No self-test summary found."
                        continue
                    }
                    $lines = @(
                        "RunId: $($last.runId)",
                        "Started: $($last.startedAt)",
                        "Ended:   $($last.endedAt)",
                        "Passed:  $($last.passed) ($($last.passedCount)/$($last.passedCount + $last.failedCount))",
                        "Log:     $($last.logPath)",
                        "JSONL:   $($last.resultsPath)"
                    )
                    Show-TextScreen -Title "Self-test summary" -Lines $lines
                    continue
                }

                if ($dsel -eq 2) {
                    $lines = @()
                    if (Test-Path -LiteralPath $paths.Log) { $lines = Get-Content -LiteralPath $paths.Log -Tail 80 -ErrorAction SilentlyContinue }
                    if (-not $lines) { $lines = @("(no log file found)") }
                    Show-TextScreen -Title "Self-test log (tail)" -Lines $lines
                    continue
                }

                if ($dsel -eq 3) {
                    $lines = @()
                    if (Test-Path -LiteralPath $paths.Jsonl) { $lines = Get-Content -LiteralPath $paths.Jsonl -Tail 80 -ErrorAction SilentlyContinue }
                    if (-not $lines) { $lines = @("(no JSONL file found)") }
                    Show-TextScreen -Title "Self-test JSONL (tail)" -Lines $lines
                    continue
                }
            }

            continue
        }

        if ($sel -eq 0) {
            $input = $null
            try {
                $input = Read-LineWithEsc -Title "SDAT" -Header $header -Prompt ("Set one-time (volatile) {0}." -f $actionLabel)
            } catch {
                $notice = New-TuiNotice -Kind "error" -Message $_.Exception.Message
                continue
            }
            if ($null -eq $input) { continue }
            if ([string]::IsNullOrWhiteSpace($input)) {
                Invoke-CancelVolatile
                $notice = New-TuiNotice -Kind "info" -Message ("One-time {0} canceled." -f $actionLabel)
                continue
            }
            try {
                $parsed = Parse-TimeInput -Value $input
                $msg = Invoke-ScheduleVolatileOnce -Hours $parsed.Hours -Minutes $parsed.Minutes -ActionType $tuiActionType
                $notice = New-TuiNotice -Kind "info" -Message $msg
            } catch {
                $notice = New-TuiNotice -Kind "error" -Message $_.Exception.Message
            }
            continue
        }

        if ($sel -eq 1) {
            $input = $null
            try {
                $input = Read-LineWithEsc -Title "SDAT" -Header $header -Prompt ("Set daily (permanent) {0}." -f $actionLabel)
            } catch {
                $notice = New-TuiNotice -Kind "error" -Message $_.Exception.Message
                continue
            }
            if ($null -eq $input) { continue }
            if ([string]::IsNullOrWhiteSpace($input)) {
                Invoke-CancelPermanent
                $notice = New-TuiNotice -Kind "info" -Message ("Daily {0} canceled." -f $actionLabel)
                continue
            }
            try {
                $parsed = Parse-TimeInput -Value $input
                $msg = Invoke-SchedulePermanentDaily -Hours $parsed.Hours -Minutes $parsed.Minutes -ActionType $tuiActionType
                $notice = New-TuiNotice -Kind "info" -Message $msg
            } catch {
                $notice = New-TuiNotice -Kind "error" -Message $_.Exception.Message
            }
            continue
        }

        if ($sel -eq 2) {
            $tuiActionType = if ($tuiActionType -eq "shutdown") { "suspend" } else { "shutdown" }
            $notice = New-TuiNotice -Kind "info" -Message ("Action mode set to {0}." -f (Get-SdatActionLabel -ActionType $tuiActionType))
            continue
        }

        if ($sel -eq 3) {
            try {
                $msg = Invoke-SkipPermanentOnce
                $notice = New-TuiNotice -Kind "info" -Message $msg
            } catch {
                $notice = New-TuiNotice -Kind "error" -Message $_.Exception.Message
            }
            continue
        }
    }
    exit 0
}

if ($Status -or -not $Time) {
    $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    try {
        Start-Process -WindowStyle Hidden -FilePath "powershell.exe" -ArgumentList @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", $PSCommandPath,
            "-NotifyStatus",
            "-Profile", $script:sdatProfile
        ) | Out-Null
    } catch {
        Write-Host "Could not open background notification process." -ForegroundColor Yellow
    }
    Write-Info (Get-SdatStatusText -State $state -Config $config)
    exit 0
}

try {
    $parsed = Parse-TimeInput -Value $Time
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Yellow
    exit 1
}
$config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
$state = Load-SdatState -Root $root -Profile $script:sdatProfile
$names = Get-SdatTaskNames -Profile $script:sdatProfile

if ($P) {
    $requestedAction = Get-SdatRequestedActionType
    if ($Test) {
        Write-Info ("Would schedule PERMANENT daily {0} at {1}:{2} (TEST MODE)" -f (Get-SdatActionLabel -ActionType $requestedAction), $parsed.Hours.ToString('D2'), $parsed.Minutes.ToString('D2'))
        exit 0
    }
    Write-Info (Invoke-SchedulePermanentDaily -Hours $parsed.Hours -Minutes $parsed.Minutes -ActionType $requestedAction)
    exit 0
}

if ($Test) {
    $requestedAction = Get-SdatRequestedActionType
    $target = Get-NextOccurrenceLocal -Hours $parsed.Hours -Minutes $parsed.Minutes
    $targetStr = Format-LocalShort -Value $target
    Write-Info ("Would schedule VOLATILE {0} at {1} (TEST MODE)" -f (Get-SdatActionLabel -ActionType $requestedAction), $targetStr)
    Write-Info "Would suppress daily action if within $([int]$config.GraceMinutes)m grace window"
    exit 0
}

Write-Info (Invoke-ScheduleVolatileOnce -Hours $parsed.Hours -Minutes $parsed.Minutes -ActionType (Get-SdatRequestedActionType))
