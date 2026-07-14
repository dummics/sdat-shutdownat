Set-StrictMode -Version Latest

function New-DefaultSdatState {
    return [pscustomobject]@{
        Version = 1
        Volatile = [pscustomobject]@{
            ScheduledFor = $null
            CreatedAt = $null
            ActionType = $null
            LastExecutedAt = $null
            LastMissedAt = $null
        }
        Permanent = [pscustomobject]@{
            ActionType = $null
            LastExecutedAt = $null
            LastSkippedAt = $null
        }
        SuspendPermanentUntil = $null
        SuspendSetAt = $null
        SuspendReason = $null
    }
}

function Assert-SdatStateSchema {
    param([Parameter(Mandatory)]$State)

    Assert-SdatPropertySet -Value $State -Expected @("Version", "Volatile", "Permanent", "SuspendPermanentUntil", "SuspendSetAt", "SuspendReason") -Context "state"
    Assert-SdatPropertySet -Value $State.Volatile -Expected @("ScheduledFor", "CreatedAt", "ActionType", "LastExecutedAt", "LastMissedAt") -Context "state.Volatile"
    Assert-SdatPropertySet -Value $State.Permanent -Expected @("ActionType", "LastExecutedAt", "LastSkippedAt") -Context "state.Permanent"

    if ([int]$State.Version -ne 1) { throw "Unsupported SDAT state version: $($State.Version)." }
    foreach ($actionType in @($State.Volatile.ActionType, $State.Permanent.ActionType)) {
        if (-not [string]::IsNullOrWhiteSpace($actionType) -and $actionType -notin @("shutdown", "suspend", "restart")) {
            throw "Unsupported SDAT state action type: $actionType."
        }
    }
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
    if ($null -eq $state) { throw "SDAT state is empty: $($paths.StatePath)" }
    Assert-SdatStateSchema -State $state
    return $state
}

function Save-SdatState {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowNull()][string]$Profile,
        [Parameter(Mandatory)]$State
    )
    Assert-SdatStateSchema -State $State
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
