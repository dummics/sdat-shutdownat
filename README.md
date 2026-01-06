# SDAT / shutdownat.ps1

A small PowerShell helper script and wrapper intended to schedule shutdowns using Windows Scheduled Tasks.

It supports:
- a **volatile** (one-use) shutdown time (single task)
- a **permanent** (daily) shutdown time (single task)

## Purpose

- Schedule a shutdown at a specified time (HHmm, 24-hour format).
- Keep tasks unique (never multiple volatile/permanent tasks).
- Provide a simple wrapper for launching via Windows Run (WIN+R) or from other scripts.
- Allow a smart suspend window so a manual/volatile trigger can temporarily suppress the permanent schedule.

## Files

- `shutdownat.ps1` - Main entrypoint. Creates/updates scheduled tasks and runs the shutdown logic when invoked by Task Scheduler.
- `sdat.bat` - A small wrapper batch to run the PowerShell script with `poweshell.exe` from arbitrary places, such as Win+R.
- `data/config.template.json` - Default config template (versioned).
- `data/config.json` - Local config generated from template (not versioned).
- `data/state.json` - Local runtime state (not versioned).

## Usage

Open a Command Prompt, PowerShell, or Win+R and run:

- Show status:

```powershell
sdat
```

- Schedule a **volatile** shutdown at 00:30 (one-use):

```powershell
sdat 0030
```

- Schedule a **permanent** daily shutdown at 03:00:

```powershell
sdat 0300 -p
```

- Dry run / test mode (no task will be created):

```powershell
sdat -Test 0030
sdat -Test 0300 -p
```

- Open a minimal configuration TUI:

```powershell
sdat -tui
```

- Cancel tasks created by this tool (and legacy `ShutdownAt*` tasks):

```powershell
sdat -A
```

## Behavior

- Volatile task name: `SDAT_Volatile` (one-shot).
- Permanent task name: `SDAT_Permanent` (daily).
- If the provided time is in the past for the current day, the volatile shutdown schedules for the next day.
- When a volatile shutdown is scheduled, the permanent schedule is suspended until `volatile + GraceMinutes` (configurable) to avoid unwanted shutdowns near manual usage.
- The volatile execution cleans up after itself (task + volatile state), so `sdat` shows "Volatile: none" after it runs.

## Notes & Security

- The scheduled command runs as the current user (`/ru "%USERNAME%"`). On systems with additional restrictions or elevated UAC prompts, the task creation may still succeed, but shutdown operations depend on system policies.
- This script forces shutdown with `/f`. Warn users to save work before scheduling.

## License

See the `LICENSE` file in this folder for license details.
