Set-StrictMode -Version Latest

function New-DefaultSdatState {
    return [pscustomobject]@{
        Version = 1
        Volatile = [pscustomobject]@{
            ScheduledFor = $null
            CreatedAt = $null
            LastExecutedAt = $null
            LastMissedAt = $null
        }
        Permanent = [pscustomobject]@{
            LastExecutedAt = $null
            LastSkippedAt = $null
        }
        SuspendPermanentUntil = $null
        SuspendSetAt = $null
        SuspendReason = $null
    }
}

function Normalize-SdatState {
    param([Parameter(Mandatory)]$State)

    function Test-HasProp {
        param(
            [Parameter(Mandatory)]$Obj,
            [Parameter(Mandatory)][string]$Name
        )
        return $null -ne ($Obj | Get-Member -Name $Name -MemberType NoteProperty -ErrorAction SilentlyContinue)
    }

    if (-not (Test-HasProp -Obj $State -Name "Version")) { $State | Add-Member -NotePropertyName Version -NotePropertyValue 1 }

    if (-not (Test-HasProp -Obj $State -Name "Volatile") -or $null -eq $State.Volatile) { $State | Add-Member -NotePropertyName Volatile -NotePropertyValue ([pscustomobject]@{}) -Force }
    if (-not (Test-HasProp -Obj $State.Volatile -Name "ScheduledFor")) { $State.Volatile | Add-Member -NotePropertyName ScheduledFor -NotePropertyValue $null }
    if (-not (Test-HasProp -Obj $State.Volatile -Name "CreatedAt")) { $State.Volatile | Add-Member -NotePropertyName CreatedAt -NotePropertyValue $null }
    if (-not (Test-HasProp -Obj $State.Volatile -Name "LastExecutedAt")) { $State.Volatile | Add-Member -NotePropertyName LastExecutedAt -NotePropertyValue $null }
    if (-not (Test-HasProp -Obj $State.Volatile -Name "LastMissedAt")) { $State.Volatile | Add-Member -NotePropertyName LastMissedAt -NotePropertyValue $null }

    if (-not (Test-HasProp -Obj $State -Name "Permanent") -or $null -eq $State.Permanent) { $State | Add-Member -NotePropertyName Permanent -NotePropertyValue ([pscustomobject]@{}) -Force }
    if (-not (Test-HasProp -Obj $State.Permanent -Name "LastExecutedAt")) { $State.Permanent | Add-Member -NotePropertyName LastExecutedAt -NotePropertyValue $null }
    if (-not (Test-HasProp -Obj $State.Permanent -Name "LastSkippedAt")) { $State.Permanent | Add-Member -NotePropertyName LastSkippedAt -NotePropertyValue $null }

    if (-not (Test-HasProp -Obj $State -Name "SuspendPermanentUntil")) { $State | Add-Member -NotePropertyName SuspendPermanentUntil -NotePropertyValue $null }
    if (-not (Test-HasProp -Obj $State -Name "SuspendSetAt")) { $State | Add-Member -NotePropertyName SuspendSetAt -NotePropertyValue $null }
    if (-not (Test-HasProp -Obj $State -Name "SuspendReason")) { $State | Add-Member -NotePropertyName SuspendReason -NotePropertyValue $null }

    return $State
}

function Ensure-SdatStateExists {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$Profile
    )
    Ensure-SdatDataDir -Root $Root -Profile $Profile
    $paths = Get-SdatConfigPaths -Root $Root -Profile $Profile
    if (-not (Test-Path -LiteralPath $paths.StatePath)) {
        $state = New-DefaultSdatState
        Save-SdatState -Root $Root -Profile $Profile -State $state
    }
}

function Load-SdatState {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$Profile
    )
    Ensure-SdatStateExists -Root $Root -Profile $Profile
    $paths = Get-SdatConfigPaths -Root $Root -Profile $Profile
    $state = Read-JsonFileOrNull -Path $paths.StatePath
    if ($null -eq $state) { return (New-DefaultSdatState) }
    return (Normalize-SdatState -State $state)
}

function Save-SdatState {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$Profile,
        [Parameter(Mandatory)]$State
    )
    Ensure-SdatDataDir -Root $Root -Profile $Profile
    $paths = Get-SdatConfigPaths -Root $Root -Profile $Profile
    $tmp = "$($paths.StatePath).tmp"
    $State | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $paths.StatePath -Force
}

function Parse-LocalDateTimeOrNull {
    param([AllowNull()][string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    try { return [datetime]::Parse($Value, [System.Globalization.CultureInfo]::InvariantCulture) } catch { return $null }
}
