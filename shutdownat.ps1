<#
    SDAT / shutdownat.ps1

    Usage:
      sdat                # show status
      sdat HHMM           # schedule one-time (volatile) shutdown
      sdat HHMM -p        # schedule daily (permanent) shutdown
      sdat -Test HHMM [-p]
      sdat -tui
      sdat -a             # cancel one-time (volatile) shutdown
      sdat -aa            # cancel all SDAT + legacy tasks
#>

param(
    [Parameter(Position = 0)]
    [string]$Time,

    [switch]$Test,
    [switch]$A,      # cancel volatile only
    [switch]$AA,     # cancel all
    [switch]$Clean,  # alias for -AA (kept for wrapper compatibility)
    [switch]$P,      # permanent (daily)
    [switch]$Tui,

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

if ($ExtraArgs -and $ExtraArgs.Count -gt 0) {
    Write-Host "Unsupported parameter: $($ExtraArgs -join ' ')" -ForegroundColor Yellow
    Write-Host "Usage: sdat [HHMM [-p]] | sdat -Test HHMM [-p] | sdat -tui | sdat -a | sdat -aa"
    exit 2
}

$root = Split-Path -Parent $PSCommandPath
. (Join-Path -Path $root -ChildPath "lib\\Config.ps1")
. (Join-Path -Path $root -ChildPath "lib\\State.ps1")
. (Join-Path -Path $root -ChildPath "lib\\Time.ps1")
. (Join-Path -Path $root -ChildPath "lib\\Tasks.ps1")
. (Join-Path -Path $root -ChildPath "lib\\Tui.ps1")
. (Join-Path -Path $root -ChildPath "lib\\Notify.ps1")
. (Join-Path -Path $root -ChildPath "lib\\Log.ps1")
. (Join-Path -Path $root -ChildPath "lib\\SelfTest.ps1")

function Write-Info([string]$Msg) { Write-Host $Msg }

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
    param([Parameter(Mandatory)][string]$Reason)
    $dry = Test-SdatDryRun
    if ($dry) {
        Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "DRY RUN: shutdown suppressed" -Data @{ Reason = $Reason }
        return
    }
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Executing shutdown" -Data @{ Reason = $Reason }
    shutdown /s /f
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
        $vol = (Format-LocalShort -Value $v.Info.NextRunTime)
    }

    $perm = "none"
    if ($pinfo.Exists -and $pinfo.Info -and $pinfo.Info.NextRunTime -gt [datetime]::MinValue) {
        $perm = (Format-LocalShort -Value $pinfo.Info.NextRunTime)
    } elseif ($pinfo.Exists) {
        $perm = "active"
    }

    $suspend = "none"
    if ($pinfo.Exists) {
        $at = $null
        if ($pinfo.Info -and $pinfo.Info.NextRunTime -gt [datetime]::MinValue) { $at = $pinfo.Info.NextRunTime }
        $sup = Get-PermanentSuppressionAt -State $State -Config $Config -AtLocal $at -VolatileTaskExists:($v.Exists)
        if ($sup.Suppressed -and $sup.Until) { $suspend = (Format-LocalShort -Value $sup.Until) }
    }

    $legacyTasks = Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'ShutdownAt*' }
    $legacyCount = if ($legacyTasks) { ($legacyTasks | Measure-Object).Count } else { 0 }
    $legacy = if ($legacyCount -gt 0) { " | Legacy: ${legacyCount}" } else { "" }

    return "One-time: $vol | Daily: $perm | SuppressedUntil: $suspend | Grace: $($Config.GraceMinutes)m${legacy}"
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
    $at = if ($AtLocal) { $AtLocal } else { Get-Date }

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
            Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
            Write-SdatLog -Ctx $script:logCtx -Level "WARN" -Message "Volatile missed (too late); no shutdown executed" -Data @{ ScheduledFor = $scheduledFor; MaxDelayMinutes = $maxDelay }
            return 0
        }
    }

    $state.Volatile.LastExecutedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    $state.Volatile.ScheduledFor = $null
    $state.Volatile.CreatedAt = $null
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $state

    Invoke-SdatShutdown -Reason "volatile"
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
        Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
        Write-SdatLog -Ctx $script:logCtx -Level "WARN" -Message "Cleared stale volatile schedule from state (task missing)"
    }

    $sup = Get-PermanentSuppressionAt -State $state -Config $config -AtLocal (Get-Date) -VolatileTaskExists:($v.Exists)
    if ($sup.Suppressed) {
        $state.Permanent.LastSkippedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
        Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
        Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message ("Permanent skipped ({0})" -f $sup.Kind) -Data $sup.Data
        return 0
    }

    $state.Permanent.LastExecutedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
    Invoke-SdatShutdown -Reason "permanent"
    return 0
}

function Invoke-CancelVolatile {
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    Unregister-TaskIfExists -TaskName $names.Volatile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    $state.Volatile.ScheduledFor = $null
    $state.Volatile.CreatedAt = $null
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Canceled one-time shutdown"
}

function Invoke-CancelPermanent {
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    Unregister-TaskIfExists -TaskName $names.Permanent
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Canceled daily shutdown"
}

function Invoke-SchedulePermanentDaily {
    param([Parameter(Mandatory)][int]$Hours, [Parameter(Mandatory)][int]$Minutes)
    $names = Get-SdatTaskNames -Profile $script:sdatProfile
    $null = Remove-LegacyShutdownAtTasks -Force:([string]::IsNullOrWhiteSpace($script:sdatProfile))
    Register-PermanentShutdownTaskDaily -Hours $Hours -Minutes $Minutes -ScriptPath $PSCommandPath -Profile $script:sdatProfile -DryRunAction:(Test-SdatDryRun)
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Scheduled daily shutdown" -Data @{ Hours = $Hours; Minutes = $Minutes; Task = $names.Permanent }
    return "Permanent shutdown scheduled daily at $($Hours.ToString('D2')):$($Minutes.ToString('D2')) (task: $($names.Permanent))"
}

function Invoke-ScheduleVolatileOnce {
    param([Parameter(Mandatory)][int]$Hours, [Parameter(Mandatory)][int]$Minutes)
    $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    $names = Get-SdatTaskNames -Profile $script:sdatProfile

    $target = Get-NextOccurrenceLocal -Hours $Hours -Minutes $Minutes
    $targetStr = Format-LocalShort -Value $target

    $null = Remove-LegacyShutdownAtTasks -Force:([string]::IsNullOrWhiteSpace($script:sdatProfile))
    Register-VolatileShutdownTask -TargetLocal $target -ScriptPath $PSCommandPath -Profile $script:sdatProfile -DryRunAction:(Test-SdatDryRun)
    $state.Volatile.ScheduledFor = $target.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    $state.Volatile.CreatedAt = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    Save-SdatState -Root $root -Profile $script:sdatProfile -State $state
    Write-SdatLog -Ctx $script:logCtx -Level "INFO" -Message "Scheduled one-time shutdown" -Data @{ Target = $target.ToString("o"); Task = $names.Volatile; GraceMinutes = [int]$config.GraceMinutes }

    return "Volatile shutdown scheduled: ${targetStr} (task: $($names.Volatile))"
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

if ($Clean) { $AA = $true }

if ($AA) {
    $result = Invoke-CancelAllAndResetState
    $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    Write-Info ("Canceled all scheduled shutdown tasks. Removed legacy tasks: {0}. Status: {1}" -f $result.LegacyRemoved, (Get-SdatStatusText -State $state -Config $config))
    exit 0
}

if ($A) {
    Invoke-CancelVolatile
    $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
    $state = Load-SdatState -Root $root -Profile $script:sdatProfile
    Write-Info ("Canceled one-time shutdown. Status: {0}" -f (Get-SdatStatusText -State $state -Config $config))
    exit 0
}

if ($Tui) {
    $notice = $null
    while ($true) {
        $config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
        $state = Load-SdatState -Root $root -Profile $script:sdatProfile
        $header = Get-SdatStatusText -State $state -Config $config

        $sel = Show-SdatMainMenu -Title "SDAT" -Header $header -Notice $notice
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
                $input = Read-LineWithEsc -Title "SDAT" -Header $header -Prompt "Set one-time (volatile) shutdown."
            } catch {
                $notice = New-TuiNotice -Kind "error" -Message $_.Exception.Message
                continue
            }
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
            $input = $null
            try {
                $input = Read-LineWithEsc -Title "SDAT" -Header $header -Prompt "Set daily (permanent) shutdown."
            } catch {
                $notice = New-TuiNotice -Kind "error" -Message $_.Exception.Message
                continue
            }
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
    } catch { }
    Write-Info (Get-SdatStatusText -State $state -Config $config)
    exit 0
}

try {
    $parsed = Parse-HHMM -Time $Time
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Yellow
    exit 1
}
$config = Load-SdatConfig -Root $root -Profile $script:sdatProfile
$state = Load-SdatState -Root $root -Profile $script:sdatProfile
$names = Get-SdatTaskNames -Profile $script:sdatProfile

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
    Write-Info "Would suppress daily shutdown if within $([int]$config.GraceMinutes)m grace window"
    exit 0
}

Write-Info (Invoke-ScheduleVolatileOnce -Hours $parsed.Hours -Minutes $parsed.Minutes)
