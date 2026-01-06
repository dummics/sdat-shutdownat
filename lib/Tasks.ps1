Set-StrictMode -Version Latest

$script:SdatVolatileTaskName = "SDAT_Volatile"
$script:SdatPermanentTaskName = "SDAT_Permanent"

function Get-SdatTaskNames {
    return [pscustomobject]@{
        Volatile = $script:SdatVolatileTaskName
        Permanent = $script:SdatPermanentTaskName
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
        [Parameter(Mandatory)][string]$ModeSwitch
    )
    $p = $ScriptPath.Replace('"', '""')
    return "powershell.exe -NoProfile -ExecutionPolicy Bypass -File ""$p"" $ModeSwitch"
}

function Register-VolatileShutdownTask {
    param(
        [Parameter(Mandatory)][datetime]$TargetLocal,
        [Parameter(Mandatory)][string]$ScriptPath
    )
    $names = Get-SdatTaskNames
    $tn = $names.Volatile

    Unregister-TaskIfExists -TaskName $tn
    $st = $TargetLocal.ToString('HH:mm')
    $sd = $TargetLocal.ToString('dd/MM/yyyy')
    $tr = Build-ScheduledActionCommand -ScriptPath $ScriptPath -ModeSwitch "-RunVolatile"

    $out = & schtasks.exe /create /tn $tn /tr $tr /sc once /st $st /sd $sd /ru $env:USERNAME /f 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($out | Out-String) }
}

function Register-PermanentShutdownTaskDaily {
    param(
        [Parameter(Mandatory)][int]$Hours,
        [Parameter(Mandatory)][int]$Minutes,
        [Parameter(Mandatory)][string]$ScriptPath
    )
    $names = Get-SdatTaskNames
    $tn = $names.Permanent

    Unregister-TaskIfExists -TaskName $tn
    $st = ("{0:D2}:{1:D2}" -f $Hours, $Minutes)
    $tr = Build-ScheduledActionCommand -ScriptPath $ScriptPath -ModeSwitch "-RunPermanent"

    $out = & schtasks.exe /create /tn $tn /tr $tr /sc daily /st $st /ru $env:USERNAME /f 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($out | Out-String) }
}
