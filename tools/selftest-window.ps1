param(
    [AllowNull()][string]$Profile,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ForwardArgs
)

Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
. (Join-Path -Path $root -ChildPath "lib\\Config.ps1")
. (Join-Path -Path $root -ChildPath "lib\\Log.ps1")

$profileSafe = Get-SdatProfileSafe -Profile $Profile
if ([string]::IsNullOrWhiteSpace($profileSafe)) { $profileSafe = "selftest" }

function Remove-Arg {
    param(
        [Parameter(Mandatory)][string[]]$Args,
        [Parameter(Mandatory)][string[]]$Names
    )
    $out = @()
    foreach ($a in $Args) {
        $skip = $false
        foreach ($n in $Names) {
            if ($a -ieq $n) { $skip = $true; break }
        }
        if (-not $skip) { $out += $a }
    }
    return $out
}

$forward = @()
if ($ForwardArgs) {
    $forward = Remove-Arg -Args $ForwardArgs -Names @("-SelfTest", "-selftest")
}

$scriptPath = Join-Path -Path $root -ChildPath "shutdownat.ps1"
$logPath = Get-SdatLogFilePath -Root $root -Profile $profileSafe
$jsonlPath = Get-SdatTestResultsPath -Root $root -Profile $profileSafe

Write-Host ""
Write-Host "SDAT self-test (dry-run)" -ForegroundColor Cyan
Write-Host ("Profile: {0}" -f $profileSafe) -ForegroundColor DarkGray
Write-Host ""
Write-Host "Running... (this can take ~20-30 seconds)" -ForegroundColor Gray
Write-Host ""

$out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath -SelfTest -Profile $profileSafe @forward 2>&1
$exitCode = $LASTEXITCODE

Write-Host ($out | Out-String)
Write-Host ("Exit code: {0}" -f $exitCode) -ForegroundColor DarkGray

Write-Host ""
Write-Host ("Log (tail): {0}" -f $logPath) -ForegroundColor Cyan
if (Test-Path -LiteralPath $logPath) {
    Get-Content -LiteralPath $logPath -Tail 120 -ErrorAction SilentlyContinue
} else {
    Write-Host "(log file not found)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host ("JSONL (tail): {0}" -f $jsonlPath) -ForegroundColor Cyan
if (Test-Path -LiteralPath $jsonlPath) {
    Get-Content -LiteralPath $jsonlPath -Tail 120 -ErrorAction SilentlyContinue
} else {
    Write-Host "(JSONL file not found)" -ForegroundColor Yellow
}

Write-Host ""
$null = Read-Host "Press Enter to close"
exit $exitCode

