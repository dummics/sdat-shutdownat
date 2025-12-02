# SDAT / shutdownat.ps1

A small PowerShell helper script and wrapper intended to schedule a one-off shutdown using a Windows Scheduled Task.

This folder contains a light CLI wrapper (`sdat.bat`) and a PowerShell script (`shutdownat.ps1`) which creates a single-use scheduled task to shut the machine down at a specified time.

## Purpose

- Schedule a single shutdown at a specified time (HHmm, 24-hour format).
- Keep scheduled tasks single-use to avoid duplicates.
- Provide a simple wrapper for launching via Windows Run (WIN+R) or from other scripts.

## Files

- `shutdownat.ps1` - The main PowerShell script that creates a scheduled task named `ShutdownAtHHmm` and registers the action to unregister itself before issuing a shutdown.
- `sdat.bat` - A small wrapper batch to run the PowerShell script with `poweshell.exe` from arbitrary places, such as Win+R.

## Usage

Open a Command Prompt, PowerShell, or Win+R and run:

- Schedule a shutdown at 00:30:

```powershell
sdat 0030
```

- Dry run / test mode (no task will be created):

```powershell
sdat -Test 0030
```

- List scheduled decay tasks created by this tool or cancel all scheduled shutdowns:

```powershell
sdat  # shows scheduled shutdowns
sdat -A  # cancels all scheduled shutdowns previously created by this script
```

## Behavior

- If the provided time is in the past for the current day, the script schedules the shutdown for the next day.
- The scheduled task created is single-use: its action unregisters the task, waits 150ms and then issues `shutdown /s /f`.
- The script also offers a validation of the `HHmm` format and shows human-friendly messages.

## Notes & Security

- The scheduled command runs as the current user (`/ru "%USERNAME%"`). On systems with additional restrictions or elevated UAC prompts, the task creation may still succeed, but shutdown operations depend on system policies.
- This script forces shutdown with `/f`. Warn users to save work before scheduling.

## License

See the `LICENSE` file in this folder for license details.
