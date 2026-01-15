Set-StrictMode -Version Latest

param(
    [Parameter(Mandatory)][string]$Title,
    [Parameter(Mandatory)][string]$Message,
    [int]$TimeoutMs = 6500
)

$scriptRoot = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent $scriptRoot
Dot-Source (Join-Path -Path $repoRoot -ChildPath "lib\\Notify.ps1")

$msg = Truncate-NotificationText -Text (Convert-ToSingleLine -Text $Message) -MaxLength 240
if (-not [string]::IsNullOrWhiteSpace($msg)) {
    Show-WindowsBalloonNotification -Title $Title -Message $msg -TimeoutMs $TimeoutMs | Out-Null
}
