Set-StrictMode -Version Latest

function Convert-ToSingleLine {
    param([AllowNull()][string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return "" }
    $s = $Text -replace "`r?`n", "  |  "
    $s = ($s -replace "\s{2,}", " ").Trim()
    return $s
}

function Truncate-NotificationText {
    param(
        [AllowNull()][string]$Text,
        [int]$MaxLength = 240
    )
    if ([string]::IsNullOrEmpty($Text)) { return "" }
    if ($Text.Length -le $MaxLength) { return $Text }
    if ($MaxLength -le 1) { return $Text.Substring(0, 1) }
    return ($Text.Substring(0, $MaxLength - 1) + "…")
}

function Show-WindowsBalloonNotification {
    param(
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][string]$Message,
        [int]$TimeoutMs = 6000
    )

    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        Add-Type -AssemblyName System.Drawing -ErrorAction Stop
    } catch {
        return $false
    }

    $notify = $null
    try {
        $notify = New-Object System.Windows.Forms.NotifyIcon
        $notify.Icon = [System.Drawing.SystemIcons]::Information
        $notify.Visible = $true
        $notify.BalloonTipTitle = $Title
        $notify.BalloonTipText = $Message
        $notify.BalloonTipIcon = [System.Windows.Forms.ToolTipIcon]::Info
        $notify.ShowBalloonTip($TimeoutMs)
        Start-Sleep -Milliseconds ([Math]::Max(500, $TimeoutMs + 250))
        return $true
    } finally {
        if ($notify) { $notify.Dispose() }
    }
}

