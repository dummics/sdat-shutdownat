Set-StrictMode -Version Latest

function Invoke-SdatSelfTest {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$ScriptPath,
        [AllowNull()][string]$Profile,
        $LogCtx
    )

    $profileSafe = Get-SdatProfileSafe -Profile $Profile
    $runId = New-SdatRunId
    $started = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    $resultsPath = Get-SdatTestResultsPath -Root $Root -Profile $profileSafe

    $runRecord = [pscustomobject]@{
        type = "run"
        runId = $runId
        startedAt = $started
        profile = $profileSafe
        computer = $env:COMPUTERNAME
        user = $env:USERNAME
        scriptPath = $ScriptPath
        powershell = $PSVersionTable.PSVersion.ToString()
    }
    try { Write-SdatJsonl -Path $resultsPath -Object $runRecord } catch { }
    try { Write-SdatLog -Ctx $LogCtx -Level "INFO" -Message "SelfTest started" -Data $runRecord } catch { }

    $tests = New-Object 'System.Collections.Generic.List[object]'
    function Add-Test {
        param(
            [Parameter(Mandatory)][string]$Name,
            [Parameter(Mandatory)][scriptblock]$Body
        )
        $null = $tests.Add([pscustomobject]@{ Name = $Name; Body = $Body })
    }

    function Record-TestResult {
        param(
            [Parameter(Mandatory)][string]$Name,
            [Parameter(Mandatory)][bool]$Passed,
            [Parameter(Mandatory)][int]$DurationMs,
            [AllowNull()][string]$Error,
            $Data
        )
        $rec = [pscustomobject]@{
            type = "test"
            runId = $runId
            name = $Name
            passed = $Passed
            durationMs = $DurationMs
            error = $Error
            data = $Data
            at = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
        }
        try { Write-SdatJsonl -Path $resultsPath -Object $rec } catch { }
        try {
            $lvl = if ($Passed) { "INFO" } else { "ERROR" }
            Write-SdatLog -Ctx $LogCtx -Level $lvl -Message ("SelfTest: {0} => {1}" -f $Name, ($(if ($Passed) { "PASS" } else { "FAIL" }))) -Data $rec
        } catch { }
        return $rec
    }

    function Throw-Assert {
        param([Parameter(Mandatory)][string]$Message)
        throw $Message
    }

    function Assert-True {
        param(
            [Parameter(Mandatory)][bool]$Condition,
            [Parameter(Mandatory)][string]$Message
        )
        if (-not $Condition) { Throw-Assert -Message $Message }
    }

    function Get-TaskArguments {
        param([Parameter(Mandatory)][string]$TaskName)
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
        $action = $task.Actions | Select-Object -First 1
        return [pscustomobject]@{
            Execute = $action.Execute
            Arguments = $action.Arguments
        }
    }

    function Invoke-SdatScript {
        param([Parameter(Mandatory)][string[]]$Args)
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Args 2>&1
        $code = $LASTEXITCODE
        return [pscustomobject]@{
            ExitCode = $code
            Output = ($out | Out-String)
        }
    }

    function Get-SoonTimeHHMM {
        param([int]$MinutesAhead)
        $t = (Get-Date).AddMinutes($MinutesAhead)
        return $t.ToString("HHmm", [System.Globalization.CultureInfo]::InvariantCulture)
    }

    $names = Get-SdatTaskNames -Profile $profileSafe

    Add-Test -Name "Clean slate" -Body {
        Unregister-TaskIfExists -TaskName $names.Volatile
        Unregister-TaskIfExists -TaskName $names.Permanent

        $state = New-DefaultSdatState
        Save-SdatState -Root $Root -Profile $profileSafe -State $state

        $config = Load-SdatConfig -Root $Root -Profile $profileSafe
        Assert-True -Condition ($null -ne $config.GraceMinutes) -Message "GraceMinutes missing from config"
    }

    Add-Test -Name "Create daily task via script (dry-run action)" -Body {
        $r = Invoke-SdatScript -Args @("-Profile", $profileSafe, "-DryRun", "-Time", "0200", "-P")
        Assert-True -Condition ($r.ExitCode -eq 0) -Message ("Script exited with {0}: {1}" -f $r.ExitCode, $r.Output)
        $info = Get-TaskInfoSafe -TaskName $names.Permanent
        Assert-True -Condition $info.Exists -Message "Expected $($names.Permanent) to exist"

        $a = Get-TaskArguments -TaskName $names.Permanent
        Assert-True -Condition ($a.Execute -ieq "powershell.exe") -Message "Unexpected Execute: $($a.Execute)"
        Assert-True -Condition ($a.Arguments -like "*-RunPermanent*") -Message "Missing -RunPermanent in Arguments"
        Assert-True -Condition ($a.Arguments -like "*-DryRun*") -Message "Missing -DryRun in Arguments"
        Assert-True -Condition ($a.Arguments -like "*-Profile $profileSafe*") -Message "Missing -Profile in Arguments"
    }

    Add-Test -Name "Create one-time task via script (dry-run action)" -Body {
        $hhmm = Get-SoonTimeHHMM -MinutesAhead 90
        $r = Invoke-SdatScript -Args @("-Profile", $profileSafe, "-DryRun", "-Time", $hhmm)
        Assert-True -Condition ($r.ExitCode -eq 0) -Message ("Script exited with {0}: {1}" -f $r.ExitCode, $r.Output)
        $info = Get-TaskInfoSafe -TaskName $names.Volatile
        Assert-True -Condition $info.Exists -Message "Expected $($names.Volatile) to exist"

        $a = Get-TaskArguments -TaskName $names.Volatile
        Assert-True -Condition ($a.Arguments -like "*-RunVolatile*") -Message "Missing -RunVolatile in Arguments"
        Assert-True -Condition ($a.Arguments -like "*-DryRun*") -Message "Missing -DryRun in Arguments"
        Assert-True -Condition ($a.Arguments -like "*-Profile $profileSafe*") -Message "Missing -Profile in Arguments"
    }

    Add-Test -Name "Run one-time task via Task Scheduler (dry-run)" -Body {
        $null = & schtasks.exe /run /tn $names.Volatile 2>&1
        $deadline = (Get-Date).AddSeconds(8)
        do {
            Start-Sleep -Milliseconds 250
            $state = Load-SdatState -Root $Root -Profile $profileSafe
        } while ([string]::IsNullOrWhiteSpace($state.Volatile.LastExecutedAt) -and (Get-Date) -lt $deadline)
        Assert-True -Condition (-not [string]::IsNullOrWhiteSpace($state.Volatile.LastExecutedAt)) -Message "Expected Volatile.LastExecutedAt to be set"
    }

    Add-Test -Name "Run daily task via Task Scheduler (dry-run)" -Body {
        $null = & schtasks.exe /run /tn $names.Permanent 2>&1
        Start-Sleep -Milliseconds 800
        $info = Get-TaskInfoSafe -TaskName $names.Permanent
        Assert-True -Condition $info.Exists -Message "Expected $($names.Permanent) to still exist after run"
    }

    Add-Test -Name "Suppression: upcoming volatile suppresses permanent (within grace)" -Body {
        $p2 = "${profileSafe}_sup"
        $names2 = Get-SdatTaskNames -Profile $p2
        Unregister-TaskIfExists -TaskName $names2.Volatile
        Unregister-TaskIfExists -TaskName $names2.Permanent
        Save-SdatState -Root $Root -Profile $p2 -State (New-DefaultSdatState)

        $r1 = Invoke-SdatScript -Args @("-Profile", $p2, "-DryRun", "-Time", "0200", "-P")
        Assert-True -Condition ($r1.ExitCode -eq 0) -Message ("Script exited with {0}: {1}" -f $r1.ExitCode, $r1.Output)

        $soon = Get-SoonTimeHHMM -MinutesAhead 1
        $r2 = Invoke-SdatScript -Args @("-Profile", $p2, "-DryRun", "-Time", $soon)
        Assert-True -Condition ($r2.ExitCode -eq 0) -Message ("Script exited with {0}: {1}" -f $r2.ExitCode, $r2.Output)

        $null = & schtasks.exe /run /tn $names2.Permanent 2>&1
        $deadline = (Get-Date).AddSeconds(8)
        do {
            Start-Sleep -Milliseconds 250
            $state = Load-SdatState -Root $Root -Profile $p2
        } while ([string]::IsNullOrWhiteSpace($state.Permanent.LastSkippedAt) -and (Get-Date) -lt $deadline)
        Assert-True -Condition (-not [string]::IsNullOrWhiteSpace($state.Permanent.LastSkippedAt)) -Message "Expected Permanent.LastSkippedAt to be set (suppressed)"

        Unregister-TaskIfExists -TaskName $names2.Permanent
        Unregister-TaskIfExists -TaskName $names2.Volatile
    }

    Add-Test -Name "Regression: cancel volatile clears suppression for permanent" -Body {
        $p2 = "${profileSafe}_cancel"
        $names2 = Get-SdatTaskNames -Profile $p2
        Unregister-TaskIfExists -TaskName $names2.Volatile
        Unregister-TaskIfExists -TaskName $names2.Permanent
        Save-SdatState -Root $Root -Profile $p2 -State (New-DefaultSdatState)

        $r1 = Invoke-SdatScript -Args @("-Profile", $p2, "-DryRun", "-Time", "0200", "-P")
        Assert-True -Condition ($r1.ExitCode -eq 0) -Message ("Script exited with {0}: {1}" -f $r1.ExitCode, $r1.Output)

        $soon = Get-SoonTimeHHMM -MinutesAhead 1
        $r2 = Invoke-SdatScript -Args @("-Profile", $p2, "-DryRun", "-Time", $soon)
        Assert-True -Condition ($r2.ExitCode -eq 0) -Message ("Script exited with {0}: {1}" -f $r2.ExitCode, $r2.Output)

        $r3 = Invoke-SdatScript -Args @("-Profile", $p2, "-A")
        Assert-True -Condition ($r3.ExitCode -eq 0) -Message ("Script exited with {0}: {1}" -f $r3.ExitCode, $r3.Output)

        $state = Load-SdatState -Root $Root -Profile $p2
        Assert-True -Condition ([string]::IsNullOrWhiteSpace($state.Volatile.ScheduledFor)) -Message "Expected Volatile.ScheduledFor cleared by cancel"

        $null = & schtasks.exe /run /tn $names2.Permanent 2>&1
        $deadline = (Get-Date).AddSeconds(8)
        do {
            Start-Sleep -Milliseconds 250
            $state = Load-SdatState -Root $Root -Profile $p2
        } while ([string]::IsNullOrWhiteSpace($state.Permanent.LastExecutedAt) -and (Get-Date) -lt $deadline)

        Assert-True -Condition (-not [string]::IsNullOrWhiteSpace($state.Permanent.LastExecutedAt)) -Message "Expected Permanent.LastExecutedAt to be set (not suppressed)"

        Unregister-TaskIfExists -TaskName $names2.Permanent
        Unregister-TaskIfExists -TaskName $names2.Volatile
    }

    Add-Test -Name "Regression: legacy suspend fields do not block permanent" -Body {
        $p2 = "${profileSafe}_legacy"
        $names2 = Get-SdatTaskNames -Profile $p2
        Unregister-TaskIfExists -TaskName $names2.Volatile
        Unregister-TaskIfExists -TaskName $names2.Permanent
        Save-SdatState -Root $Root -Profile $p2 -State (New-DefaultSdatState)

        $r1 = Invoke-SdatScript -Args @("-Profile", $p2, "-DryRun", "-Time", "0200", "-P")
        Assert-True -Condition ($r1.ExitCode -eq 0) -Message ("Script exited with {0}: {1}" -f $r1.ExitCode, $r1.Output)

        $state = Load-SdatState -Root $Root -Profile $p2
        $state.SuspendPermanentUntil = (Get-Date).AddHours(12).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
        $state.SuspendReason = "legacy-test"
        $state.Volatile.ScheduledFor = $null
        $state.Volatile.CreatedAt = $null
        $state.Volatile.LastExecutedAt = $null
        Save-SdatState -Root $Root -Profile $p2 -State $state

        $null = & schtasks.exe /run /tn $names2.Permanent 2>&1
        $deadline = (Get-Date).AddSeconds(8)
        do {
            Start-Sleep -Milliseconds 250
            $state = Load-SdatState -Root $Root -Profile $p2
        } while ([string]::IsNullOrWhiteSpace($state.Permanent.LastExecutedAt) -and (Get-Date) -lt $deadline)

        Assert-True -Condition (-not [string]::IsNullOrWhiteSpace($state.Permanent.LastExecutedAt)) -Message "Expected Permanent.LastExecutedAt to be set (legacy suspend ignored)"

        Unregister-TaskIfExists -TaskName $names2.Permanent
        Unregister-TaskIfExists -TaskName $names2.Volatile
    }

    Add-Test -Name "Cleanup" -Body {
        Unregister-TaskIfExists -TaskName $names.Permanent
        Unregister-TaskIfExists -TaskName $names.Volatile
        $info1 = Get-TaskInfoSafe -TaskName $names.Permanent
        $info2 = Get-TaskInfoSafe -TaskName $names.Volatile
        Assert-True -Condition (-not $info1.Exists) -Message "Expected $($names.Permanent) removed"
        Assert-True -Condition (-not $info2.Exists) -Message "Expected $($names.Volatile) removed"
    }

    $results = @()
    foreach ($t in $tests) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $err = $null
        $data = $null
        $passed = $false
        try {
            & $t.Body
            $passed = $true
        } catch {
            $err = $_.Exception.Message
        } finally {
            $sw.Stop()
        }
        $results += (Record-TestResult -Name $t.Name -Passed $passed -DurationMs ([int]$sw.ElapsedMilliseconds) -Error $err -Data $data)
    }

    $ended = (Get-Date).ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    $passedCount = @($results | Where-Object { $_.passed }).Count
    $failedCount = @($results | Where-Object { -not $_.passed }).Count
    $summary = [pscustomobject]@{
        type = "summary"
        runId = $runId
        startedAt = $started
        endedAt = $ended
        profile = $profileSafe
        passed = ($failedCount -eq 0)
        passedCount = $passedCount
        failedCount = $failedCount
        resultsPath = $resultsPath
        logPath = if ($LogCtx) { $LogCtx.LogPath } else { (Get-SdatLogFilePath -Root $Root -Profile $profileSafe) }
        tests = $results
    }
    try { Write-SdatJsonl -Path $resultsPath -Object $summary } catch { }
    try { Write-SdatLog -Ctx $LogCtx -Level "INFO" -Message "SelfTest completed" -Data $summary } catch { }
    return $summary
}
