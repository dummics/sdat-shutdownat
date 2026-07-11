<#
    SDAT / shutdownat.ps1

    Usage:
      sdat                # show status
      sdat TIME           # schedule one-time (volatile) shutdown
      sdat TIME -p        # schedule daily (permanent) shutdown
      sdat TIME -Restart  # schedule one-time restart
      ssat TIME           # schedule one-time (volatile) suspend
      ssat TIME -p        # schedule daily (permanent) suspend
      sdat -Test TIME [-p]
      ssat -Test TIME [-p]
      sdat -tui
      sdat t
      sdat tui
      ssat -tui
      sdat -a             # abort Windows shutdown, then cancel one-time task
      sdat -aa            # abort Windows shutdown, then cancel SDAT tasks
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
    [switch]$Restart, # schedule/run restart instead of shutdown
    [Alias('k')][switch]$KeepDaily, # keep daily task even when one-time overlaps it
    [Alias('t')][switch]$Tui,
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

# Emergency cancel path: abort a pending Windows shutdown before loading SDAT modules,
# logs, config, state, or Task Scheduler helpers. The normal cancel branch below
# still handles SDAT task cleanup after this immediate best-effort abort.
if (($A -or $AA -or $Clean) -and -not $DryRun -and -not $SelfTest -and [string]::IsNullOrWhiteSpace($Profile)) {
    & "$env:SystemRoot\System32\shutdown.exe" /a 2>$null | Out-Null
}

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
        "Usage: sdat|ssat [TIME [-p]] | sdat|ssat -Test TIME [-p] | sdat|ssat -tui | sdat t | sdat tui | sdat|ssat -a | sdat|ssat -aa | sdat|ssat -s | sdat|ssat -h",
        "",
        "Commands:",
        "  (no args)            show status, next shutdown time, and quick examples",
        "  -Status              explicit status check (same as no args)",
        "  TIME [-p]            schedule one-time (-p omitted) or daily (-p) shutdown/suspend/restart",
        "  -Test TIME [-p]      dry run the one-time or daily power action",
        "  -Suspend             use suspend action (ssat wrapper always sets this)",
        "  -Restart             use restart action",
        "  -k / -KeepDaily      keep the daily task when one-time is near it",
        "  -tui, -t, t, tui     open the interactive configuration UI",
        "  -a                   abort any pending Windows shutdown and cancel the pending one-time action",
        "  -aa / -Clean         abort any pending Windows shutdown and cancel SDAT scheduled tasks",
        "  -s / -SkipPermanent  toggle skip for the next permanent run",
        "  -h / -Help           show this help output",
        "",
        "TIME examples:",
        "  2330, 23:30          absolute clock time",
        "  2h, 3.5h             relative hours",
        "  45m, mezzora         relative minutes",
        "  60s, 180sec          relative seconds",
        "",
        "Special modes: -NotifyStatus, -RunVolatile, -RunPermanent, -SelfTest"
    )
    Write-SdatHelpView -Lines $lines
}

$script:SdatFromWinR = $null

function Test-SdatWinRProcessChain {
    param(
        [Parameter(Mandatory)]$Wrapper,
        [Parameter(Mandatory)]$Caller
    )
    return (
        $Wrapper.Name -ieq 'cmd.exe' -and
        $Wrapper.CommandLine -match '(?i)(?:^|\s)/c(?:\s|$)' -and
        $Caller.Name -ieq 'explorer.exe'
    )
}

function Test-FromWinR {
    if ($null -ne $script:SdatFromWinR) { return [bool]$script:SdatFromWinR }

    if ($env:SDAT_FROM_WINR -eq '1') {
        $script:SdatFromWinR = $true
        return $true
    }

    $detected = $false
    try {
        if ($env:SDAT_WRAPPER_PROCESS -ne '1') {
            $script:SdatFromWinR = $false
            return $false
        }

        $current = Get-CimInstance Win32_Process -Filter "ProcessId = $PID" -ErrorAction Stop
        $wrapper = Get-CimInstance Win32_Process -Filter "ProcessId = $($current.ParentProcessId)" -ErrorAction Stop
        if ($wrapper.Name -ieq 'cmd.exe') {
            $caller = Get-CimInstance Win32_Process -Filter "ProcessId = $($wrapper.ParentProcessId)" -ErrorAction Stop
            $detected = Test-SdatWinRProcessChain -Wrapper $wrapper -Caller $caller
        }
    } catch {
        $detected = $false
    }

    $script:SdatFromWinR = $detected
    return $detected
}

function Wait-SdatWinRResult {
    if ($Tui -or $RunVolatile -or $RunPermanent -or $NotifyStatus -or $SelfTest) { return }
    if (-not (Test-FromWinR)) { return }
    Start-Sleep -Seconds 6
}

function Send-SdatNotification {
    param([AllowNull()][string]$Message)
    return
}

function Write-Info([string]$Msg) {
    if (Test-FromWinR) {
        Write-SdatCommandResult -Message $Msg
    } else {
        Write-Host $Msg
    }
    Send-SdatNotification -Message $Msg
}

try {
if (Test-FromWinR) {
    try { [Console]::Title = 'SDAT' } catch { }
}

if ($Clean) { $AA = $true }

if ($Time -and $Time.Trim().ToLowerInvariant() -in @("t", "tui") -and -not ($P -or $Test -or $A -or $AA -or $Clean -or $SkipPermanent -or $RunVolatile -or $RunPermanent -or $NotifyStatus -or $SelfTest -or $Status)) {
    $Tui = $true
    $Time = $null
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
    Send-SdatNotification -Message "Usage: sdat|ssat [TIME [-p]] | sdat|ssat -Test TIME [-p] | sdat|ssat -tui | sdat t | sdat tui | sdat|ssat -a | sdat|ssat -aa | sdat|ssat -s | sdat|ssat -h"
    exit 0
}

function Resolve-SdatActionType {
    param(
        [AllowNull()][string]$Value,
        [string]$Default = "shutdown"
    )
    if ([string]::IsNullOrWhiteSpace($Value)) { return $Default }
    $v = $Value.Trim().ToLowerInvariant()
    if ($v -in @("shutdown", "suspend", "restart")) { return $v }
    return $Default
}

function Get-SdatRequestedActionType {
    if ($Restart) { return "restart" }
    if ($Suspend) { return "suspend" }
    return "shutdown"
}

function Get-SdatActionLabel {
    param([Parameter(Mandatory)][string]$ActionType)
    return (Resolve-SdatActionType -Value $ActionType)
}

function Get-NextSdatActionType {
    param([Parameter(Mandatory)][string]$ActionType)
    $current = Resolve-SdatActionType -Value $ActionType
    if ($current -eq "shutdown") { return "suspend" }
    if ($current -eq "suspend") { return "restart" }
    return "shutdown"
}

function Normalize-TimeInput {
    param([Parameter(Mandatory)][string]$Value)
    $raw = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($raw)) {
        throw "Missing time value. Use HHMM/HH:MM or a duration like 2h, 45m, 180s."
    }

    $digits = $null
    if ($raw -match '^\d{3,4}$') {
        $digits = $raw
    } elseif ($raw -match '^\d{1,2}:\d{2}$') {
        $digits = ($raw -replace ':', '')
    } else {
        throw "Invalid time format. Use HHMM/HH:MM or a duration like 2h, 45m, 180s."
    }

    if ($digits.Length -eq 3) { $digits = "0$digits" }
    return $digits
}

function Parse-TimeInput {
    param([Parameter(Mandatory)][string]$Value)
    return Parse-HHMM -Time (Normalize-TimeInput -Value $Value)
}

function Resolve-SdatDurationSeconds {
    param([Parameter(Mandatory)][string]$Value)

    $raw = $Value.Trim().ToLowerInvariant()
    $compact = ($raw -replace "\s+", "")
    if ($compact -in @("mezzora", "mezz'ora", "mezzaora", "mezzhour", "halfhour")) {
        return 1800
    }
    if ($raw -match "^(mezz|mezza|half)\s*(ora|hour)$") {
        return 1800
    }

    $unitPattern = "h|hr|hrs|hour|hours|ora|ore|m|min|mins|minute|minutes|s|sec|secs|second|seconds|secondi"
    $partPattern = "(?<amount>\d+(?:[\.,]\d+)?)\s*(?<unit>$unitPattern)"
    $matches = [regex]::Matches($raw, $partPattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($matches.Count -eq 0) { return $null }

    $consumed = (($matches | ForEach-Object { $_.Value }) -join "") -replace "\s+", ""
    if ($consumed -ne $compact) { return $null }

    [double]$seconds = 0
    foreach ($match in $matches) {
        $amountText = $match.Groups["amount"].Value.Replace(",", ".")
        $amount = [double]::Parse($amountText, [System.Globalization.CultureInfo]::InvariantCulture)
        if ($amount -lt 0) { throw "Duration must be greater than zero." }

        $unit = $match.Groups["unit"].Value.ToLowerInvariant()
        if ($unit -in @("h", "hr", "hrs", "hour", "hours", "ora", "ore")) { $seconds += $amount * 3600 }
        elseif ($unit -in @("m", "min", "mins", "minute", "minutes")) { $seconds += $amount * 60 }
        else { $seconds += $amount }
    }

    $rounded = [int][Math]::Round($seconds, [System.MidpointRounding]::AwayFromZero)
    if ($rounded -le 0) { throw "Duration must be greater than zero." }
    return $rounded
}

function Resolve-SdatTimeInput {
    param(
        [Parameter(Mandatory)][string]$Value,
        [datetime]$Now = (Get-Date)
    )
    $raw = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($raw)) {
        throw "Missing time value. Use 2330, 23:30, 2h, 3.5h, 45m, mezzora, or 180s."
    }

    $seconds = Resolve-SdatDurationSeconds -Value $raw
    if ($null -ne $seconds) {
        $target = $Now.AddSeconds($seconds)
        if ($target.Second -gt 0 -or $target.Millisecond -gt 0) {
            $target = $target.AddMinutes(1).AddSeconds(-$target.Second).AddMilliseconds(-$target.Millisecond)
        }
        return [pscustomobject]@{
            Kind = "relative"
            Raw = $raw
            Hours = $target.Hour
            Minutes = $target.Minute
            TargetLocal = $target
            DurationSeconds = $seconds
            Label = ("in {0}" -f (Format-TimeRemaining -Target $target))
        }
    }

    $parsed = Parse-TimeInput -Value $raw
    $targetLocal = Get-NextOccurrenceLocal -Hours $parsed.Hours -Minutes $parsed.Minutes -Now $Now
    return [pscustomobject]@{
        Kind = "absolute"
        Raw = $raw
        Hours = $parsed.Hours
        Minutes = $parsed.Minutes
        TargetLocal = $targetLocal
        DurationSeconds = $null
        Label = ("at {0}:{1}" -f $parsed.Hours.ToString('D2'), $parsed.Minutes.ToString('D2'))
    }
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

function Format-SdatWhenShort {
    param([Parameter(Mandatory)][datetime]$Value)
    $local = Convert-ToLocalDateTime -Value $Value
    $now = Get-Date
    if ($local.Date -eq $now.Date -or $local.Date -eq $now.Date.AddDays(1)) {
        return $local.ToString("HH:mm", [System.Globalization.CultureInfo]::InvariantCulture)
    }
    return (Format-LocalShort -Value $local)
}

function Test-SdatDryRun {
    if ($DryRun) { return $true }
    if ($SelfTest) { return $true }
    if ($script:sdatProfile -like "selftest*") { return $true }
    if ($env:SDAT_DRYRUN -eq '1') { return $true }
    return $false
}

$script:sdatProfile = Get-SdatProfileSafe -Profile $Profile
if ($DryRun -and [string]::IsNullOrWhiteSpace($script:sdatProfile) -and -not ($SelfTest -or $RunVolatile -or $RunPermanent -or $NotifyStatus)) {
    $script:sdatProfile = "dryrun"
}
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

    if ($action -eq "restart") {
        Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Executing restart" -Data @{ Reason = $Reason; TimeoutSeconds = 30 }
        & "$env:SystemRoot\System32\shutdown.exe" /r /f /t 30
        return
    }

    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Executing shutdown" -Data @{ Reason = $Reason; TimeoutSeconds = 30 }
    & "$env:SystemRoot\System32\shutdown.exe" /s /f /t 30
}

function Invoke-AbortPendingSystemShutdown {
    if (Test-SdatDryRun) {
        Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "DRY RUN: pending system shutdown abort suppressed"
        return $false
    }

    $abortResult = & "$env:SystemRoot\System32\shutdown.exe" /a 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Aborted pending system shutdown"
        return $true
    }

    # Error 1116 means no shutdown is in progress.
    if ($exitCode -ne 1116) {
        Write-SdatLog -Ctx $script:logCtx -Level "WARN" -Message "Failed to abort pending system shutdown (exit code: $exitCode)" -Data @{ Output = ($abortResult -join "`n") }
    }
    return $false
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
        $vol = ("{0} @ {1} (in {2})" -f $volAction, (Format-SdatWhenShort -Value $volRunAt), (Format-TimeRemaining -Target $volRunAt))
    }

    $perm = "none"
    if ($pinfo.Exists -and $pinfo.Info -and $pinfo.Info.NextRunTime -gt [datetime]::MinValue) {
        $permAction = Get-SdatActionLabel -ActionType (Resolve-SdatActionType -Value $State.Permanent.ActionType)
        $permRunAt = Convert-ToLocalDateTime -Value $pinfo.Info.NextRunTime
        $perm = ("{0} @ {1} (in {2})" -f $permAction, (Format-SdatWhenShort -Value $permRunAt), (Format-TimeRemaining -Target $permRunAt))
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
            $suspend = ("{0} [{1}]" -f (Format-SdatWhenShort -Value $sup.Until), $kindLabel)
        }
    }

    $overlap = if (Test-HasProp -Obj $Config -Name "DailyOverlapWindowMinutes") { [int]$Config.DailyOverlapWindowMinutes } else { 120 }
    return "One-time: $vol | Daily: $perm | Suppression: $suspend | Grace: $($Config.GraceMinutes)m | Overlap: $($overlap)m"
}

function Get-SdatStatusModel {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)]$Config
    )
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    $v = Get-TaskInfoSafe -TaskName $names.Volatile
    $pinfo = Get-TaskInfoSafe -TaskName $names.Permanent
    $overlap = if (Test-HasProp -Obj $Config -Name "DailyOverlapWindowMinutes") { [int]$Config.DailyOverlapWindowMinutes } else { 120 }
    $grace = [int]$Config.GraceMinutes

    $oneTime = "none"
    if ($v.Exists -and $v.Info -and $v.Info.NextRunTime -gt [datetime]::MinValue) {
        $volAction = Get-SdatActionLabel -ActionType (Resolve-SdatActionType -Value $State.Volatile.ActionType)
        $volRunAt = Convert-ToLocalDateTime -Value $v.Info.NextRunTime
        $oneTime = ("{0} at {1}  (in {2})" -f $volAction, (Format-SdatWhenShort -Value $volRunAt), (Format-TimeRemaining -Target $volRunAt))
    }

    $daily = "none"
    $skip = "none"
    if ($pinfo.Exists -and $pinfo.Info -and $pinfo.Info.NextRunTime -gt [datetime]::MinValue) {
        $permAction = Get-SdatActionLabel -ActionType (Resolve-SdatActionType -Value $State.Permanent.ActionType)
        $permRunAt = Convert-ToLocalDateTime -Value $pinfo.Info.NextRunTime
        $daily = ("{0} at {1}  (in {2})" -f $permAction, (Format-SdatWhenShort -Value $permRunAt), (Format-TimeRemaining -Target $permRunAt))
        $sup = Get-PermanentSuppressionAt -State $State -Config $Config -AtLocal $permRunAt -VolatileTaskExists:($v.Exists)
        if ($sup.Suppressed -and $sup.Until) {
            $skipKind = if ($sup.Kind -eq "manual-skip") { "manual" } elseif ($sup.Kind -eq "volatile-upcoming") { "one-time nearby" } else { "grace window" }
            $skip = ("next daily skipped until {0}  ({1})" -f (Format-SdatWhenShort -Value $sup.Until), $skipKind)
        }
    } elseif ($pinfo.Exists) {
        $permAction = Get-SdatActionLabel -ActionType (Resolve-SdatActionType -Value $State.Permanent.ActionType)
        $daily = ("{0} active" -f $permAction)
    }

    return [pscustomobject]@{
        OneTime = $oneTime
        Daily = $daily
        Skip = $skip
        GraceMinutes = $grace
        OverlapMinutes = $overlap
    }
}

function Get-SdatStatusViewLines {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)]$Config
    )
    $m = Get-SdatStatusModel -State $State -Config $Config
    $oneColor = if ($m.OneTime -eq "none") { "grey58" } else { "yellow" }
    $skipColor = if ($m.Skip -eq "none") { "grey58" } else { "yellow" }
    return @(
        "[grey58]One-time  [/][$oneColor]$($m.OneTime)[/]",
        "[grey58]Daily    [/][deepskyblue1]$($m.Daily)[/]",
        "[grey58]Skip     [/][$skipColor]$($m.Skip)[/]",
        "[grey58]Rules    [/][grey70]one-time wins within[/] [white]$($m.OverlapMinutes)m[/] [grey70]| grace[/] [white]$($m.GraceMinutes)m[/] [grey70]| use[/] [deepskyblue1]sdat -k <time>[/] [grey70]to keep daily[/]"
    )
}

function Get-SdatTuiHeaderLines {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)]$Config
    )
    $m = Get-SdatStatusModel -State $State -Config $Config
    $oneColor = if ($m.OneTime -eq "none") { "grey58" } else { "yellow" }
    $lines = @(
        "[grey58]Daily    [/][deepskyblue1]$($m.Daily)[/]",
        "[grey58]One-time [/][$oneColor]$($m.OneTime)[/]"
    )
    if ($m.Skip -ne "none") {
        $lines += "[grey58]Pause    [/][yellow]$($m.Skip)[/]"
    }
    return $lines
}

function Get-SdatQuickHints {
    return @(
        "[deepskyblue1]sdat 2330[/]     [grey58]one-time at 23:30[/]",
        "[deepskyblue1]sdat 3.5h[/]     [grey58]one-time in 3h 30m[/]",
        "[deepskyblue1]sdat 45m[/]      [grey58]one-time in 45 minutes[/]",
        "[deepskyblue1]sdat 0200 -p[/]  [grey58]daily at 02:00[/]",
        "[deepskyblue1]sdat -s[/]       [grey58]skip next daily once[/]",
        "[deepskyblue1]sdat -k 45m[/]   [grey58]keep daily even if close[/]"
    )
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
    $state = New-DefaultSdatState
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
    return [pscustomobject]@{ Names = $names }
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
    $action = if ($Restart) { "restart" } elseif ($Suspend) { "suspend" } else { Resolve-SdatActionType -Value $state.Volatile.ActionType }
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
    $action = if ($Restart) { "restart" } elseif ($Suspend) { "suspend" } else { Resolve-SdatActionType -Value $state.Permanent.ActionType }
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
        [Parameter(Mandatory)][ValidateSet("shutdown", "suspend", "restart")][string]$ActionType
    )
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    Register-PermanentShutdownTaskDaily -Hours $Hours -Minutes $Minutes -ScriptPath $PSCommandPath -Profile $script:sdatProfile -SuspendAction:($ActionType -eq "suspend") -RestartAction:($ActionType -eq "restart") -DryRunAction:(Test-SdatDryRun)
    $state.Permanent.ActionType = (Resolve-SdatActionType -Value $ActionType)
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Scheduled daily action" -Data @{ Hours = $Hours; Minutes = $Minutes; Task = $names.Permanent; ActionType = $state.Permanent.ActionType }
    return ("Daily {0} at {1}:{2}" -f (Get-SdatActionLabel -ActionType $state.Permanent.ActionType), $Hours.ToString('D2'), $Minutes.ToString('D2'))
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
    $targetStr = Format-SdatWhenShort -Value $nextRun
    $suppressUntil = $nextRun.AddMinutes(5)
    $state.SuspendPermanentUntil = $suppressUntil.ToString("o", $culture)
    $state.SuspendSetAt = (Get-Date).ToString("o", $culture)
    $state.SuspendReason = ("manual-skip for {0}" -f $targetStr)
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Manual skip scheduled for permanent shutdown" -Data @{
        NextRun = $nextRun.ToString("o", $culture)
        SuppressUntil = $suppressUntil.ToString("o", $culture)
    }
    return "Daily shutdown skipped once at $targetStr."
}

function Clear-VolatileOverlapSuppression {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$Reason
    )
    if ($State.SuspendReason -notlike "one-time overlap*") { return $false }
    $State.SuspendPermanentUntil = $null
    $State.SuspendSetAt = $null
    $State.SuspendReason = $null
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $State
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Cleared one-time overlap skip for daily action" -Data @{ Reason = $Reason }
    return $true
}

function Invoke-ScheduleVolatileOnce {
    param(
        [int]$Hours,
        [int]$Minutes,
        [AllowNull()][datetime]$TargetLocal,
        [Parameter(Mandatory)][ValidateSet("shutdown", "suspend", "restart")][string]$ActionType
    )
    $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    $names = Get-SdatTaskNames -Profile $script:sdatProfile

    $target = if ($TargetLocal) { Convert-ToLocalDateTime -Value $TargetLocal } else { Get-NextOccurrenceLocal -Hours $Hours -Minutes $Minutes }
    $targetStr = Format-SdatWhenShort -Value $target

    Register-VolatileShutdownTask -TargetLocal $target -ScriptPath $PSCommandPath -Profile $script:sdatProfile -SuspendAction:($ActionType -eq "suspend") -RestartAction:($ActionType -eq "restart") -DryRunAction:(Test-SdatDryRun)
    $state.Volatile.ScheduledFor = $target.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    $state.Volatile.CreatedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    $state.Volatile.ActionType = (Resolve-SdatActionType -Value $ActionType)
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Scheduled one-time action" -Data @{ Target = $target.ToString("o"); Task = $names.Volatile; GraceMinutes = [int]$config.GraceMinutes; ActionType = $state.Volatile.ActionType }

    $msg = ("One-time {0} at {1}" -f (Get-SdatActionLabel -ActionType $state.Volatile.ActionType), $targetStr)
    if (-not $KeepDaily) {
        $overlap = Set-DailySkipForVolatileOverlap -TargetLocal $target -State $state -Config $config
        if ($overlap.Applied) {
            $msg += ("; skips daily at {0}" -f (Format-SdatWhenShort -Value $overlap.PermanentRunAt))
        } else {
            $null = Clear-VolatileOverlapSuppression -State $state -Reason "new one-time does not overlap daily"
        }
    } else {
        $null = Clear-VolatileOverlapSuppression -State $state -Reason "KeepDaily"
    }
    return $msg
}

function Set-DailySkipForVolatileOverlap {
    param(
        [Parameter(Mandatory)][datetime]$TargetLocal,
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)]$Config
    )
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    $pinfo = Get-TaskInfoSafe -TaskName $names.Permanent
    if (-not ($pinfo.Exists -and $pinfo.Info -and $pinfo.Info.NextRunTime -gt [datetime]::MinValue)) {
        return [pscustomobject]@{ Applied = $false; PermanentRunAt = $null }
    }

    $nextRun = Convert-ToLocalDateTime -Value $pinfo.Info.NextRunTime
    $windowMinutes = [int]$Config.DailyOverlapWindowMinutes
    if ($windowMinutes -le 0) { return [pscustomobject]@{ Applied = $false; PermanentRunAt = $nextRun } }

    $distance = [Math]::Abs(($nextRun - (Convert-ToLocalDateTime -Value $TargetLocal)).TotalMinutes)
    if ($distance -gt $windowMinutes) {
        return [pscustomobject]@{ Applied = $false; PermanentRunAt = $nextRun }
    }

    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    $until = $nextRun.AddMinutes(5)
    $State.SuspendPermanentUntil = $until.ToString("o", $culture)
    $State.SuspendSetAt = (Get-Date).ToString("o", $culture)
    $State.SuspendReason = ("one-time overlap for {0}" -f (Format-LocalShort -Value (Convert-ToLocalDateTime -Value $TargetLocal)))
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $State
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Daily run skipped once because one-time overlaps it" -Data @{
        VolatileTarget = (Convert-ToLocalDateTime -Value $TargetLocal).ToString("o", $culture)
        PermanentRunAt = $nextRun.ToString("o", $culture)
        WindowMinutes = $windowMinutes
        DistanceMinutes = $distance
    }
    return [pscustomobject]@{ Applied = $true; PermanentRunAt = $nextRun }
}

if ($RunVolatile) { exit (Invoke-RunVolatile) }
if ($RunPermanent) { exit (Invoke-RunPermanent) }

if ($SelfTest) {
    $env:SDAT_DRYRUN = '1'
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

if ($AA) {
    $systemAbortDone = Invoke-AbortPendingSystemShutdown
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    $v = Get-TaskInfoSafe -TaskName $names.Volatile
    $permanentInfo = Get-TaskInfoSafe -TaskName $names.Permanent
    if (-not ($v.Exists -or $permanentInfo.Exists)) {
        $prefix = if ($systemAbortDone) { "Pending Windows shutdown aborted. " } else { "" }
        Write-Info "${prefix}No scheduled tasks to cancel."
        exit 0
    }

    $null = Invoke-CancelAllAndResetState
    $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    $prefix = if ($systemAbortDone) { "Pending Windows shutdown aborted. " } else { "" }
    Write-Info ("{0}Canceled all SDAT scheduled power tasks. Status: {1}" -f $prefix, (Get-SdatStatusText -State $state -Config $config))
    exit 0
}

if ($A) {
    $systemAbortDone = Invoke-AbortPendingSystemShutdown
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    $v = Get-TaskInfoSafe -TaskName $names.Volatile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    $actionLabel = Get-SdatActionLabel -ActionType (Resolve-SdatActionType -Value $state.Volatile.ActionType)
    if (-not $v.Exists) {
        $prefix = if ($systemAbortDone) { "Pending Windows shutdown aborted. " } else { "" }
        Write-Info ("{0}No pending one-time {1} to cancel." -f $prefix, $actionLabel)
        exit 0
    }
    Invoke-CancelVolatile
    $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    $prefix = if ($systemAbortDone) { "Pending Windows shutdown aborted. " } else { "" }
    Write-Info ("{0}Canceled one-time action. Status: {1}" -f $prefix, (Get-SdatStatusText -State $state -Config $config))
    exit 0
}

if ($Tui) {
    $notice = $null
    $taskCache = $null

    function Show-SdatTuiTextScreen {
        param(
            [Parameter(Mandatory)][string]$Title,
            [Parameter(Mandatory)][AllowEmptyString()][string[]]$Lines,
            [switch]$AllowRefresh
        )
        try { [Console]::Clear() } catch { try { Clear-Host } catch { } }
        Write-SdatTuiTitle -Title "SDAT"
        Write-Host ""
        Write-Host $Title -ForegroundColor Gray
        Write-Host ""
        foreach ($ln in $Lines) { Write-Host (Remove-SdatSpectreMarkup -Text $ln) }
        Write-Host ""
        $hint = if ($AllowRefresh) { "R refresh  Enter / Esc back" } else { "Enter / Esc back" }
        Write-Host $hint -ForegroundColor DarkGray
        while ($true) {
            $k = [Console]::ReadKey($true)
            if ($AllowRefresh -and $k.Key -eq [ConsoleKey]::R) { return "refresh" }
            if ($k.Key -eq [ConsoleKey]::Escape -or $k.Key -eq [ConsoleKey]::Enter) { return "back" }
        }
    }

    function Get-SdatTaskSnapshotLines {
        param(
            [Parameter(Mandatory)]$State,
            [Parameter(Mandatory)]$Config
        )
        $m = Get-SdatStatusModel -State $State -Config $Config
        $names = Get-SdatTaskNames -Profile $script:sdatProfile
        $v = Get-TaskInfoSafe -TaskName $names.Volatile
        $permanentInfo = Get-TaskInfoSafe -TaskName $names.Permanent
        $stamp = (Get-Date).ToString("HH:mm:ss", [System.Globalization.CultureInfo]::InvariantCulture)
        $volState = if ($v.Exists -and $v.Task) { $v.Task.State } else { "none" }
        $permState = if ($permanentInfo.Exists -and $permanentInfo.Task) { $permanentInfo.Task.State } else { "none" }
        return @(
            "Updated    $stamp",
            "",
            "One-time   $($m.OneTime)",
            "Daily      $($m.Daily)",
            "Pause      $($m.Skip)",
            "",
            "$($names.Volatile)   $volState",
            "$($names.Permanent)  $permState"
        )
    }

    while ($true) {
        $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile      
        $state = Load-SdatState -Root $root -Profile $script:sdatProfile        
        $header = (Get-SdatTuiHeaderLines -State $state -Config $config) -join "`n"
        $options = @(
            "Shutdown once",
            "Shutdown daily",
            "Suspend once",
            "Suspend daily",
            "Restart once",
            "Restart daily",
            "Tasks"
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
                    $lastRunLabel = try { ([datetimeoffset]$last.startedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") } catch { [string]$last.startedAt }
                    $resultLabel = if ($last.passed) { "PASS" } else { "FAIL" }
                    $diagHeader += "`nLast run: $lastRunLabel`nResult: $resultLabel | $($last.passedCount)/$($last.passedCount + $last.failedCount) passed"
                } else {
                    $diagHeader += "`nLast run: none"
                }

                $dsel = Show-SdatDiagnosticsMenu -Title "SDAT Diagnostics" -Header $diagHeader -Notice $diagNotice
                $diagNotice = $null
                if ($null -eq $dsel -or $dsel -eq 4) { break }

                if ($dsel -eq 0) {
                    $lines = @(
                        "Self-tests are manual-only so SDAT never opens surprise background shells.",
                        "",
                        "Run this from an already-open terminal when needed:",
                        "  sdat -SelfTest -DryRun",
                        "",
                        "Direct script form:",
                        "  pwsh -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -SelfTest -DryRun -Profile $selfTestProfile",
                        "",
                        "Safety: -SelfTest forces dry-run mode for child scheduled-task actions too."
                    )
                    Show-SdatTuiTextScreen -Title "Manual self-test" -Lines $lines
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
                    Show-SdatTuiTextScreen -Title "Self-test summary" -Lines $lines
                    continue
                }

                if ($dsel -eq 2) {
                    $lines = @()
                    if (Test-Path -LiteralPath $paths.Log) { $lines = Get-Content -LiteralPath $paths.Log -Tail 80 -ErrorAction SilentlyContinue }
                    if (-not $lines) { $lines = @("(no log file found)") }
                    Show-SdatTuiTextScreen -Title "Self-test log (tail)" -Lines $lines
                    continue
                }

                if ($dsel -eq 3) {
                    $lines = @()
                    if (Test-Path -LiteralPath $paths.Jsonl) { $lines = Get-Content -LiteralPath $paths.Jsonl -Tail 80 -ErrorAction SilentlyContinue }
                    if (-not $lines) { $lines = @("(no JSONL file found)") }
                    Show-SdatTuiTextScreen -Title "Self-test JSONL (tail)" -Lines $lines
                    continue
                }
            }

            continue
        }

        if ($sel -eq 6) {
            while ($true) {
                if (-not $taskCache -or $taskCache.ExpiresAt -lt (Get-Date)) {
                    $taskCache = [pscustomobject]@{
                        ExpiresAt = (Get-Date).AddSeconds(15)
                        Lines = (Get-SdatTaskSnapshotLines -State $state -Config $config)
                    }
                }
                $taskAction = Show-SdatTuiTextScreen -Title "Tasks" -Lines $taskCache.Lines -AllowRefresh
                if ($taskAction -ne "refresh") { break }
                $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
                $state = Load-SdatState -Root $root -Profile $script:sdatProfile
                $taskCache = $null
            }
            continue
        }

        if ($sel -ge 0 -and $sel -le 5) {
            $actionTypes = @("shutdown", "shutdown", "suspend", "suspend", "restart", "restart")
            $isDaily = ($sel % 2 -eq 1)
            $selectedAction = $actionTypes[$sel]
            $actionLabel = Get-SdatActionLabel -ActionType $selectedAction
            $prompt = if ($isDaily) { ("{0} daily" -f (Get-Culture).TextInfo.ToTitleCase($actionLabel)) } else { ("{0} once" -f (Get-Culture).TextInfo.ToTitleCase($actionLabel)) }
            $emptyAction = if ($isDaily) { ("cancel daily {0}" -f $actionLabel) } else { ("cancel one-time {0}" -f $actionLabel) }
            $examples = if ($isDaily) { "2330  23:30" } else { "2330  23:30  2h  45m" }
            $input = $null
            try {
                $input = Read-LineWithEsc -Title "SDAT" -Header $header -Prompt $prompt -EmptyAction $emptyAction -Examples $examples
            } catch {
                $notice = New-TuiNotice -Kind "error" -Message $_.Exception.Message
                continue
            }
            if ($null -eq $input) { continue }
            if ([string]::IsNullOrWhiteSpace($input)) {
                if ($isDaily) {
                    Invoke-CancelPermanent
                    $notice = New-TuiNotice -Kind "info" -Message ("Daily {0} canceled." -f $actionLabel)
                } else {
                    Invoke-CancelVolatile
                    $notice = New-TuiNotice -Kind "info" -Message ("One-time {0} canceled." -f $actionLabel)
                }
                $taskCache = $null
                continue
            }
            try {
                $resolved = Resolve-SdatTimeInput -Value $input
                if ($isDaily -and $resolved.Kind -ne "absolute") { throw "Daily schedules need a clock time like 0200 or 02:00. Use durations for one-time only." }
                $msg = if ($isDaily) {
                    Invoke-SchedulePermanentDaily -Hours $resolved.Hours -Minutes $resolved.Minutes -ActionType $selectedAction
                } else {
                    Invoke-ScheduleVolatileOnce -TargetLocal $resolved.TargetLocal -ActionType $selectedAction
                }
                $notice = New-TuiNotice -Kind "info" -Message $msg
                $taskCache = $null
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
    Write-SdatStatusView -Lines (Get-SdatStatusViewLines -State $state -Config $config) -Hints (Get-SdatQuickHints)
    exit 0
}

try {
    $resolved = Resolve-SdatTimeInput -Value $Time
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Yellow
    exit 1
}
$config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
$state = Load-SdatState -Root $root -Profile $script:sdatProfile
$names = Get-SdatTaskNames -Profile $script:sdatProfile

if ($P) {
    $requestedAction = Get-SdatRequestedActionType
    if ($resolved.Kind -ne "absolute") {
        Write-Host "Daily schedules need a clock time like 0200 or 02:00. Use durations for one-time only." -ForegroundColor Yellow
        exit 1
    }
    if ($Test) {
        Write-Info ("Would schedule DAILY {0} at {1}:{2} (TEST MODE)" -f (Get-SdatActionLabel -ActionType $requestedAction), $resolved.Hours.ToString('D2'), $resolved.Minutes.ToString('D2'))
        exit 0
    }
    Write-Info (Invoke-SchedulePermanentDaily -Hours $resolved.Hours -Minutes $resolved.Minutes -ActionType $requestedAction)
    exit 0
}

if ($Test) {
    $requestedAction = Get-SdatRequestedActionType
    $target = $resolved.TargetLocal
    $targetStr = Format-SdatWhenShort -Value $target
    $dailyDecision = if ($KeepDaily) {
        "Would keep daily if nearby"
    } else {
        "Would skip nearby daily within $([int]$config.DailyOverlapWindowMinutes)m"
    }
    Write-Info (("Would schedule ONE-TIME {0} at {1} (TEST MODE)" -f (Get-SdatActionLabel -ActionType $requestedAction), $targetStr) + "`n" + $dailyDecision)
    exit 0
}

Write-Info (Invoke-ScheduleVolatileOnce -TargetLocal $resolved.TargetLocal -ActionType (Get-SdatRequestedActionType))
} finally {
    Wait-SdatWinRResult
}
