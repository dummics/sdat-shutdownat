<#
        shutdownat.ps1 - Schedule a one-time shutdown at a specified HHmm time.

        Usage:
            sdat 0030               # schedule shutdown at 00:30
            sdat -Test 0030         # print when it would run, don't actually schedule
            sdat -A                 # cancel all scheduled shutdowns

        The scheduled task created is a single-use task named 'ShutdownAtHHmm'.
        Its action unregisters itself before issuing a forced shutdown to ensure
        no duplicate tasks remain.
#>
param(
    [Parameter(Position=0)]
    [string]$Time,        # Time format HHmm e.g. 0030
    [switch]$Test,        # Test mode (no actual shutdown will be performed)
    [switch]$A,           # Cancel all scheduled shutdowns: sdat -a
    [Parameter(ValueFromRemainingArguments=$true)]
    [string[]]$ExtraArgs  # Per catturare parametri extra
)

# If there are unrecognized arguments (e.g. undeclared params), fail gracefully
if ($ExtraArgs -and $ExtraArgs.Count -gt 0) {
    Write-Host "Parametro non supportato: $($ExtraArgs -join ' ')" -ForegroundColor Yellow
    Write-Host "Uso: sdat HHMM | sdat -Test | sdat -a"
    exit 2
}

# ----------------- Helper functions -----------------
function Write-Info($msg){ Write-Host $msg }

<#
    Remove-ScheduledShutdowns
    Remove any scheduled tasks that match the ShutdownAt* pattern.
    This helper is used both by the `-A` cancel mode and before scheduling a new shutdown
    to avoid multiple pending timers.
#>
function Remove-ScheduledShutdowns {
    $tasks = Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'ShutdownAt*' }
    if (-not $tasks){ Write-Info "Nessuno spegnimento presente, non ho cancellato."; return }
    $tasks | ForEach-Object { Unregister-ScheduledTask -TaskName $_.TaskName -Confirm:$false }
    Write-Info "Annullati $(($tasks|Measure-Object).Count) spegnimenti programmati."
}

# ----------------- Cancel mode -----------------
if ($A) { Remove-ScheduledShutdowns; exit }

# ----------------- Input validation -----------------
if (-not $Time) {
    $tasks = Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'ShutdownAt*' }
    if (-not $tasks) { Write-Info "Nessuno spegnimento programmato."; exit 0 }
    Write-Info "Spegnimenti programmati:"
    foreach ($task in $tasks) {
        $info = Get-ScheduledTaskInfo -TaskName $task.TaskName -ErrorAction SilentlyContinue
        $next = if ($info -and $info.NextRunTime -gt [datetime]::MinValue) { $info.NextRunTime.ToString('yyyy-MM-dd HH:mm') } else { 'N/D' }
        Write-Info "  $($task.TaskName): $next"
    }
    exit 0
}
if ($Time -notmatch '^\d{4}$') { Write-Info "Formato orario non valido. Usa HHMM (es. 0030, 1345)."; exit 1 }

$h = [int]$Time.Substring(0,2)
$m = [int]$Time.Substring(2,2)
if ($h -gt 23 -or $m -gt 59) { Write-Info "Orario non valido: $Time"; exit 1 }

# ----------------- Calculate target time -----------------
$target = (Get-Date).Date.AddHours($h).AddMinutes($m)
if ($target -lt (Get-Date)) { $target = $target.AddDays(1) }  # schedule for tomorrow if the time already passed today

# Unique task name for this scheduled shutdown
$taskName = "ShutdownAt$Time"

# Human-friendly string representation of the target time
$targetStr = $target.ToString('yyyy-MM-dd HH:mm')

# ----------------- Task action -----------------
# The action scheduled by the task will unregister the task itself and then
# issue a forced system shutdown. We keep the inline command short to avoid
# having to write a separate script for the single-use shutdown.
$inline = @"
Unregister-ScheduledTask -TaskName '$taskName' -Confirm:\$false; Start-Sleep -Milliseconds 150; shutdown /s /f
"@

# ----------------- Test mode -----------------
if ($Test) {
    Write-Info "Avrei programmato lo spegnimento alle $targetStr (TEST MODE)"
    exit 0
}

# ----------------- Create scheduled task -----------------
# We use schtasks.exe to create a single-run task. The task action is an inline
# PowerShell command that unregisters the task and then performs a forced shutdown.
# Heavy quoting is necessary to embed the inline command in the schtasks call.
# Cancel previous timers so we only allow one pending shutdown at a time
Remove-ScheduledShutdowns
try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue } catch {}

try {
    $st = $target.ToString('HH:mm')
    $sd = $target.ToString('dd/MM/yyyy')
    $cmd = "schtasks /create /tn `"$taskName`" /tr `"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \`"`"$inline\`"`"`" /sc once /st $st /sd $sd /ru `"$env:USERNAME`" /f"
    Invoke-Expression $cmd
    Write-Info "Spegnimento programmato: $targetStr  (task: $taskName)"
}
catch {
    throw "Errore nella creazione del task: $($_.Exception.Message)"
}
