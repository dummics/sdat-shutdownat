# SDAT / SSAT

Small Windows power-action scheduler built around PowerShell and Scheduled Tasks.

It supports:
- a **volatile** (one-use) power action time (single task)
- a **permanent** (daily) power action time (single task)
- two wrappers with the same syntax:
  - `sdat` for **shutdown** (or restart with `-Restart`)
  - `ssat` for **suspend**

## Purpose

- Schedule a shutdown/suspend/restart at a clock time (`HHmm` / `HH:mm`) or after a duration.
- Keep tasks unique (never multiple volatile/permanent tasks).
- Provide a simple wrapper for terminal use or scripts, without detached helper windows.
- Allow a smart overlap window so a one-time trigger can temporarily suppress the next daily schedule.

## Files

- `shutdownat.ps1` - Main entrypoint. Creates/updates scheduled tasks and runs the selected power action when invoked by Task Scheduler.
- `sdat.bat` - A small wrapper batch to run the PowerShell script with `pwsh.exe` when available, or Windows PowerShell otherwise.
- `ssat.bat` - Same syntax as `sdat.bat`, but schedules/runs suspend.
- `sdatui.bat` - Opens the interactive terminal UI directly.
- `data/config.template.json` - Default config template (versioned).
- `data/config.json` - Local config generated from template (not versioned).
- `data/state.json` - Local runtime state (not versioned).

## Usage

Open a Command Prompt or PowerShell and run:

### Win+R

`sdat` and `ssat` are designed to work directly from the Windows Run dialog. Status and command results remain visible for about six seconds, then the transient window closes silently. There is no countdown, pause message, or "press any key" prompt.

Commands launched from an existing terminal return immediately. Interactive TUI commands (`sdat t`, `sdat tui`, or `sdat -tui`) remain open until you exit them.

- Show status:

```powershell
sdat
ssat
```

In a terminal this shows a compact status view. When PowerShell 7 is available, the wrapper uses `PwshSpectreConsole` for a cleaner terminal panel; it falls back to plain console output when needed.

- Schedule a **volatile** shutdown at 00:30 (one-use):

```powershell
sdat 0030
```

- Schedule a **volatile** shutdown after a duration:

```powershell
sdat 2h
sdat 3.5h
sdat 1h30m
sdat 45m
sdat mezzora
sdat 180s
```

- Schedule a **volatile** suspend at 00:30 (one-use):

```powershell
ssat 0030
```

- Schedule a **volatile** restart at 00:30 (one-use):

```powershell
sdat 0030 -Restart
```

- Schedule a **permanent** daily shutdown at 03:00:

```powershell
sdat 0300 -p
```

Daily schedules use clock times only. Durations such as `2h`, `3.5h`, or `45m` are intentionally limited to one-time actions.

- Schedule a **permanent** daily suspend at 03:00:

```powershell
ssat 0300 -p
```

- Schedule a **permanent** daily restart at 03:00:

```powershell
sdat 0300 -p -Restart
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
sdat -t
sdat t
sdat tui
sdatui
ssat -tui
```

TUI controls:

- Use the arrow keys or `W` / `A` / `S` / `D` to move; `Home` and `End` jump to the first and last action.
- Press `Enter` to select and `Esc` to go back or exit. `Ctrl+T` opens diagnostics from the main screen.
- The Tasks screen supports `R` to refresh immediately.
- On a schedule input screen, pressing `Enter` with an empty value cancels that one-time or daily schedule; the consequence is shown before submission.

- Run the self-test manually when needed. The runner stays attached to the current terminal, while child PowerShell runs and Task Scheduler actions stay hidden so they do not open helper windows:

```powershell
sdat -SelfTest -DryRun
```

- Emergency-cancel any Windows shutdown already in progress, then cancel SDAT scheduled tasks:

```powershell
sdat -aa
```

- Emergency-cancel any Windows shutdown already in progress, then cancel only the one-time (volatile) shutdown:

```powershell
sdat -a
```

- Shutdown/restart execution uses a 30-second Windows timeout, so `sdat -a` / `sdat -aa` can still abort an accidentally triggered power action.

## Time Input

Clock times work for one-time and daily schedules:

```powershell
sdat 9:30
sdat 1130
sdat 0300 -p
```

Durations work for one-time schedules only:

```powershell
sdat 3.5h
sdat 1,5h
sdat 1h30m
sdat 90m
sdat mezzora
sdat mezza ora
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
- In TUI (`-tui`) scheduling is mode-aware: choose shutdown/suspend/restart and once/daily directly. The `Tasks` row is read-only and uses a short in-memory cache while the TUI is open.
- Runtime logs are written outside the repo at `%LOCALAPPDATA%\SDAT\logs` so command history survives normal repo updates and stays untracked.
- Plain `-DryRun` commands without an explicit `-Profile` use an isolated `dryrun` profile, so verification commands cannot overwrite real `SDAT_Volatile` / `SDAT_Permanent` tasks.
- The volatile execution cleans up after itself (task + volatile state), so `sdat` shows "Volatile: none" after it runs.
- If a volatile run is triggered late (for example after sleep), it is skipped when more than `MissedVolatileShutdownMaxDelayMinutes` minutes late (default: 0, which means never run late).
- If a permanent run is triggered late (for example after sleep), it is skipped when more than `MissedPermanentShutdownMaxDelayMinutes` minutes late (default: 0, which means never run late).

## Notes & Security

- The scheduled command runs as the current user (`/ru "%USERNAME%"`). On systems with additional restrictions or elevated UAC prompts, the task creation may still succeed, but shutdown operations depend on system policies.
- Shutdown and restart are forced with `/f`. Save work before scheduling.

## License

See the `LICENSE` file in this folder for license details.
