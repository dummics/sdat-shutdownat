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

function Get-TaskStartBoundaryLocal {
    param([Parameter(Mandatory)]$Task)
    $trigger = $Task.Triggers | Select-Object -First 1
    if (-not $trigger -or [string]::IsNullOrWhiteSpace($trigger.StartBoundary)) { return $null }
    try {
        return (Convert-ToLocalDateTime -Value ([datetime]::Parse($trigger.StartBoundary, [System.Globalization.CultureInfo]::InvariantCulture)))
    } catch {
        try { return (Convert-ToLocalDateTime -Value ([datetime]::Parse($trigger.StartBoundary))) } catch { return $null }
    }
}

function Unregister-TaskIfExists {
    param([Parameter(Mandatory)][string]$TaskName)
    try { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop | Out-Null } catch { }
}

function Build-ScheduledActionCommand {
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][string]$ModeSwitch,
        [AllowNull()][string]$Profile,
        [switch]$SuspendAction,
        [switch]$RestartAction,
        [switch]$DryRunAction
    )
    $p = $ScriptPath.Replace('"', '""')
    $hiddenLauncher = (Join-Path -Path (Split-Path -Parent $ScriptPath) -ChildPath "lib\RunHidden.vbs").Replace('"', '""')
    $args = @($ModeSwitch)
    $pp = Get-SdatProfileSafe -Profile $Profile
    if ($pp) { $args += @("-Profile", $pp) }
    if ($SuspendAction) { $args += "-Suspend" }
    if ($RestartAction) { $args += "-Restart" }
    if ($DryRunAction) { $args += "-DryRun" }
    return "wscript.exe //B //NoLogo ""$hiddenLauncher"" ""$p"" " + ($args -join " ")
}

function New-SdatScheduledTaskAction {
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][string]$ModeSwitch,
        [AllowNull()][string]$Profile,
        [switch]$SuspendAction,
        [switch]$RestartAction,
        [switch]$DryRunAction
    )

    $command = Build-ScheduledActionCommand -ScriptPath $ScriptPath -ModeSwitch $ModeSwitch -Profile $Profile -SuspendAction:$SuspendAction -RestartAction:$RestartAction -DryRunAction:$DryRunAction
    $prefix = "wscript.exe "
    if (-not $command.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected scheduled action command: $command"
    }
    return New-ScheduledTaskAction -Execute "wscript.exe" -Argument $command.Substring($prefix.Length)
}

function New-SdatScheduledTaskPrincipal {
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    return New-ScheduledTaskPrincipal -UserId $currentSid -LogonType Interactive -RunLevel Limited
}

function Set-SdatTaskDefaultSettings {
    param([Parameter(Mandatory)][string]$TaskName)
    try {
        # Keep SDAT non-intrusive: never wake the PC from sleep for scheduled shutdown jobs.
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
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
        [switch]$SuspendAction,
        [switch]$RestartAction,
        [switch]$DryRunAction
    )
    $names = Get-SdatTaskNames -Profile $Profile
    $tn = $names.Volatile

    Unregister-TaskIfExists -TaskName $tn
    $action = New-SdatScheduledTaskAction -ScriptPath $ScriptPath -ModeSwitch "-RunVolatile" -Profile $Profile -SuspendAction:$SuspendAction -RestartAction:$RestartAction -DryRunAction:$DryRunAction
    $trigger = New-ScheduledTaskTrigger -Once -At $TargetLocal
    $principal = New-SdatScheduledTaskPrincipal
    Register-ScheduledTask -TaskName $tn -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
    Set-SdatTaskDefaultSettings -TaskName $tn
}

function Register-PermanentShutdownTaskDaily {
    param(
        [Parameter(Mandatory)][int]$Hours,
        [Parameter(Mandatory)][int]$Minutes,
        [Parameter(Mandatory)][string]$ScriptPath,
        [AllowNull()][string]$Profile,
        [switch]$SuspendAction,
        [switch]$RestartAction,
        [switch]$DryRunAction
    )
    $names = Get-SdatTaskNames -Profile $Profile
    $tn = $names.Permanent

    Unregister-TaskIfExists -TaskName $tn
    $action = New-SdatScheduledTaskAction -ScriptPath $ScriptPath -ModeSwitch "-RunPermanent" -Profile $Profile -SuspendAction:$SuspendAction -RestartAction:$RestartAction -DryRunAction:$DryRunAction
    $at = [datetime]::Today.AddHours($Hours).AddMinutes($Minutes)
    $trigger = New-ScheduledTaskTrigger -Daily -At $at
    $principal = New-SdatScheduledTaskPrincipal
    Register-ScheduledTask -TaskName $tn -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
    Set-SdatTaskDefaultSettings -TaskName $tn
}
