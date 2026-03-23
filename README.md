# SDAT / SSAT / shutdownat.ps1

A small PowerShell helper script and wrapper intended to schedule power actions using Windows Scheduled Tasks.

It supports:
- a **volatile** (one-use) power action time (single task)
- a **permanent** (daily) power action time (single task)
- two wrappers with the same syntax:
  - `sdat` for **shutdown**
  - `ssat` for **suspend**

## Purpose

- Schedule a shutdown/suspend at a specified time (HHmm, 24-hour format).
- Keep tasks unique (never multiple volatile/permanent tasks).
- Provide a simple wrapper for launching via Windows Run (WIN+R) or from other scripts.
- Allow a smart suspend window so a manual/volatile trigger can temporarily suppress the permanent schedule.

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

This also shows a small, dismissible Windows notification with the current scheduled status (best-effort; falls back to console output if notifications are unavailable).

Tip: when launching from Win+R, the `sdat.bat` wrapper starts the notification in a detached GUI host so it can still appear even though the console closes immediately.

- Schedule a **volatile** shutdown at 00:30 (one-use):

```powershell
sdat 0030
```

- Schedule a **volatile** suspend at 00:30 (one-use):

```powershell
ssat 0030
```

- Schedule a **permanent** daily shutdown at 03:00:

```powershell
sdat 0300 -p
```

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

- Show this help output (includes the `-s` option):

```powershell
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

## Behavior

- Volatile task name: `SDAT_Volatile` (one-shot).
- Permanent task name: `SDAT_Permanent` (daily).
- The latest command wins: scheduling with `sdat` or `ssat` updates the same volatile/permanent task slot, so there is never more than one volatile and one permanent task.
- If the provided time is in the past for the current day, the volatile action schedules for the next day.
- When a volatile action is scheduled, the permanent schedule is suspended until `volatile + GraceMinutes` (configurable) to avoid unwanted actions near manual usage.
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
