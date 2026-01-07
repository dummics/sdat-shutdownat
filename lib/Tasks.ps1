Set-StrictMode -Version Latest

function Get-SdatTaskNames {
    param([AllowNull()][string]$Profile)
    $p = Get-SdatProfileSafe -Profile $Profile
    $prefix = if ($p) { "SDAT_${p}" } else { "SDAT" }
    return [pscustomobject]@{
        Volatile = "${prefix}_Volatile"
        Permanent = "${prefix}_Permanent"
    }
}

function Get-TaskInfoSafe {
    param([Parameter(Mandatory)][string]$TaskName)
    try {
        $t = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
        $i = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
        return [pscustomobject]@{
            Exists = $true
            Task = $t
            Info = $i
        }
    } catch {
        return [pscustomobject]@{ Exists = $false; Task = $null; Info = $null }
    }
}

function Unregister-TaskIfExists {
    param([Parameter(Mandatory)][string]$TaskName)
    try { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop | Out-Null } catch { }
}

function Remove-LegacyShutdownAtTasks {
    param([switch]$Force)
    if (-not $Force) { return 0 }
    $tasks = Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'ShutdownAt*' }
    if (-not $tasks) { return 0 }
    foreach ($t in $tasks) {
        try { Unregister-ScheduledTask -TaskName $t.TaskName -Confirm:$false -ErrorAction Stop | Out-Null } catch { }
    }
    return ($tasks | Measure-Object).Count
}

function Build-ScheduledActionCommand {
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][string]$ModeSwitch,
        [AllowNull()][string]$Profile,
        [switch]$DryRunAction
    )
    $p = $ScriptPath.Replace('"', '""')
    $args = @($ModeSwitch)
    $pp = Get-SdatProfileSafe -Profile $Profile
    if ($pp) { $args += @("-Profile", $pp) }
    if ($DryRunAction) { $args += "-DryRun" }
    return "powershell.exe -NoProfile -ExecutionPolicy Bypass -File ""$p"" " + ($args -join " ")
}

function Set-SdatTaskDefaultSettings {
    param([Parameter(Mandatory)][string]$TaskName)
    try {
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -WakeToRun
        Set-ScheduledTask -TaskName $TaskName -Settings $settings | Out-Null
    } catch {
        # Ignore settings failures; task still exists.
    }
}

function Register-VolatileShutdownTask {
    param(
        [Parameter(Mandatory)][datetime]$TargetLocal,
        [Parameter(Mandatory)][string]$ScriptPath,
        [AllowNull()][string]$Profile,
        [switch]$DryRunAction
    )
    $names = Get-SdatTaskNames -Profile $Profile
    $tn = $names.Volatile

    Unregister-TaskIfExists -TaskName $tn
    $st = $TargetLocal.ToString('HH:mm')
    $sd = $TargetLocal.ToString('dd/MM/yyyy')
    $tr = Build-ScheduledActionCommand -ScriptPath $ScriptPath -ModeSwitch "-RunVolatile" -Profile $Profile -DryRunAction:$DryRunAction

    $out = & schtasks.exe /create /tn $tn /tr $tr /sc once /st $st /sd $sd /ru $env:USERNAME /f 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($out | Out-String) }
    Set-SdatTaskDefaultSettings -TaskName $tn
}

function Register-PermanentShutdownTaskDaily {
    param(
        [Parameter(Mandatory)][int]$Hours,
        [Parameter(Mandatory)][int]$Minutes,
        [Parameter(Mandatory)][string]$ScriptPath,
        [AllowNull()][string]$Profile,
        [switch]$DryRunAction
    )
    $names = Get-SdatTaskNames -Profile $Profile
    $tn = $names.Permanent

    Unregister-TaskIfExists -TaskName $tn
    $st = ("{0:D2}:{1:D2}" -f $Hours, $Minutes)
    $tr = Build-ScheduledActionCommand -ScriptPath $ScriptPath -ModeSwitch "-RunPermanent" -Profile $Profile -DryRunAction:$DryRunAction

    $out = & schtasks.exe /create /tn $tn /tr $tr /sc daily /st $st /ru $env:USERNAME /f 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($out | Out-String) }
    Set-SdatTaskDefaultSettings -TaskName $tn
}
