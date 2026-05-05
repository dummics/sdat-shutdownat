# SDAT / SSAT / shutdownat.ps1

A small PowerShell helper script and wrapper intended to schedule power actions using Windows Scheduled Tasks.

It supports:
- a **volatile** (one-use) power action time (single task)
- a **permanent** (daily) power action time (single task)
- two wrappers with the same syntax:
  - `sdat` for **shutdown**
  - `ssat` for **suspend**

## Purpose

- Schedule a shutdown/suspend at a clock time (HHmm / HH:mm) or after a short duration.
- Keep tasks unique (never multiple volatile/permanent tasks).
- Provide a simple wrapper for launching via Windows Run (WIN+R) or from other scripts.
- Allow a smart overlap window so a one-time trigger can temporarily suppress the next daily schedule.

## Files

- `shutdownat.ps1` - Main entrypoint. Creates/updates scheduled tasks and runs the selected power action when invoked by Task Scheduler.
- `sdat.bat` - A small wrapper batch to run the PowerShell script with `poweshell.exe` from arbitrary places, such as Win+R.
- `ssat.bat` - Same syntax as `sdat.bat`, but schedules/runs suspend.
- `data/config.template.json` - Default config template (versioned).
- `data/config.json` - Local config generated from template (not versioned).
- `data/state.json` - Local runtime state (not versioned).

## Usage

Open a Command Prompt, PowerShell, or Win+R and run:

- Show status:

```powershell
sdat
ssat
```

In a terminal this shows a compact status view. When PowerShell 7 is available, the wrapper uses `PwshSpectreConsole` for a cleaner terminal panel; it falls back to plain console output when needed.

Tip: when launching from Win+R, the `sdat.bat` wrapper starts the notification in a detached GUI host so it can still appear even though the console closes immediately.

- Schedule a **volatile** shutdown at 00:30 (one-use):

```powershell
sdat 0030
```

- Schedule a **volatile** shutdown after a duration:

```powershell
sdat 2h
sdat 45m
sdat 180s
```

- Schedule a **volatile** suspend at 00:30 (one-use):

```powershell
ssat 0030
```

- Schedule a **permanent** daily shutdown at 03:00:

```powershell
sdat 0300 -p
```

Daily schedules use clock times only. Durations such as `2h` are intentionally limited to one-time actions.

- Schedule a **permanent** daily suspend at 03:00:

```powershell
ssat 0300 -p
```

- Skip the next scheduled permanent shutdown once (run again to re-enable):

```powershell
sdat -s
```
- Dry run / test mode (no task will be created):

```powershell
sdat -Test 0030
sdat -Test 0300 -p
ssat -Test 0030
ssat -Test 0300 -p
```

- Show status explicitly with `-Status` (or no args), and get help:

```powershell
sdat -Status
sdat -h
ssat -h
```

- Open a minimal configuration TUI:

```powershell
sdat -tui
ssat -tui
```

- Cancel tasks created by this tool (and legacy `ShutdownAt*` tasks):

```powershell
sdat -aa
```

- Cancel only the one-time (volatile) shutdown:

```powershell
sdat -a
```

- Skip confirmations with `-f` / `-Force`:

```powershell
sdat -a -f
sdat -aa -f
```

- Accept HHMM and HH:MM formats:

```powershell
sdat 9:30
sdat 1130
```

- Keep the daily schedule even when a one-time action is close to it:

```powershell
sdat 45m -k
sdat 45m -KeepDaily
```

## Behavior

- Volatile task name: `SDAT_Volatile` (one-shot).
- Permanent task name: `SDAT_Permanent` (daily).
- The latest command wins: scheduling with `sdat` or `ssat` updates the same volatile/permanent task slot, so there is never more than one volatile and one permanent task.
- If the provided time is in the past for the current day, the volatile action schedules for the next day.
- When a one-time action is scheduled near the next daily action, the one-time action wins by default and the next daily action is skipped once. The overlap window is controlled by `DailyOverlapWindowMinutes` (default: 120). Use `-k` / `-KeepDaily` to keep both.
- `GraceMinutes` still protects the daily action around recent or upcoming one-time runs.
- Use `sdat -s` to toggle suppression of the next permanent shutdown; running it again clears the skip, and the normal daily schedule resumes after the skipped run.
- In TUI (`-tui`) you can quickly toggle action mode between shutdown/suspend without leaving the menu.
- The volatile execution cleans up after itself (task + volatile state), so `sdat` shows "Volatile: none" after it runs.
- If a volatile run is triggered late (for example after sleep), it is skipped when more than `MissedVolatileShutdownMaxDelayMinutes` minutes late (default: 0, which means never run late).
- If a permanent run is triggered late (for example after sleep), it is skipped when more than `MissedPermanentShutdownMaxDelayMinutes` minutes late (default: 0, which means never run late).

## Notes & Security

- The scheduled command runs as the current user (`/ru "%USERNAME%"`). On systems with additional restrictions or elevated UAC prompts, the task creation may still succeed, but shutdown operations depend on system policies.
- This script forces shutdown with `/f`. Warn users to save work before scheduling.

## License

See the `LICENSE` file in this folder for license details.
