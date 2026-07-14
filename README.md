# SDAT — shut down Windows at a human time

[![CI](https://github.com/dummics/sdat-shutdownat/actions/workflows/ci.yml/badge.svg)](https://github.com/dummics/sdat-shutdownat/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/dummics/sdat-shutdownat)](https://github.com/dummics/sdat-shutdownat/releases/latest)
[![License](https://img.shields.io/github/license/dummics/sdat-shutdownat)](LICENSE)

Schedule, inspect, or cancel a Windows shutdown directly from **Win+R**. No second calculations, no account, no telemetry, no background service.

```text
sdat 3h
sdat 23:30
sdat cancel
```

SDAT is useful when a render, export, download, or long task should finish before the PC turns off.

## Install

Windows 10/11, current user only, no administrator prompt:

```powershell
irm https://raw.githubusercontent.com/dummics/sdat-shutdownat/main/install.ps1 | iex
```

The installer downloads the latest GitHub Release, validates its SHA256 checksum, installs to `%LOCALAPPDATA%\Programs\SDAT`, and adds `sdat` to the user PATH. Open a new terminal or Win+R after installation.

Prefer a manual install? Download `sdat-v*-windows.zip` from the [latest release](https://github.com/dummics/sdat-shutdownat/releases/latest), extract it, and run `install.ps1`.

## Use

| Command | Result |
|---|---|
| `sdat 3h` | Shut down once in three hours |
| `sdat 90m` | Shut down once in 90 minutes |
| `sdat 23:30` | Shut down once at 23:30 |
| `sdat daily 02:00` | Shut down every day at 02:00 |
| `sdat` or `sdat status` | Show the current schedule at a glance |
| `sdat cancel` | Abort Windows shutdown and remove the one-time action |
| `sdat cancel all` | Abort Windows shutdown and remove one-time and daily actions |
| `sdat skip` | Skip the next daily action once |
| `sdat tui` | Open the interactive terminal UI |
| `sdat logs` | Show the log folder and recent warnings/errors |

The short aliases remain available: `-a`, `-aa`, `-p`, `-s`, `-k`, and `-tui`. Existing scripts do not need to change.

### Other power actions

`ssat` uses the same syntax for suspend:

```text
ssat 45m
ssat daily 02:00
```

Restart uses the existing switch:

```text
sdat 01:30 -Restart
```

### Win+R behavior

Run `sdat` from the Windows Run dialog for the fastest workflow. Status and result panels remain visible briefly and close without pause prompts. `sdat cancel` calls Windows' emergency abort before PowerShell or Task Scheduler cleanup, so a live countdown is stopped immediately.

### Daily overlap safety

A nearby one-time action wins over the next daily action by default. Keep both explicitly with:

```text
sdat 45m -k
```

Shutdown and restart currently force applications to close. Save work before scheduling them.

## Update and remove

```text
sdat version
sdat update
sdat uninstall
```

Updates preserve `data/config.json`, `data/state.json`, and profile data. To keep a backup of those files when removing SDAT, run `sdat uninstall -KeepData`.

## How it works

SDAT uses two current-user Windows Scheduled Tasks: one one-time slot and one daily slot. The latest command replaces the matching slot, so schedules never accumulate silently. Runtime logs are stored under `%LOCALAPPDATA%\SDAT\logs`; `sdat logs` shows the location and recent problems without dumping internal JSON. Log files are kept for 30 days and capped at 5 MB each.

The release package bundles the pinned terminal rendering dependency. If rich rendering is unavailable, SDAT falls back to plain console output.

## Development

Run the complete self-test without triggering a real power action:

```powershell
pwsh -NoProfile -File ./shutdownat.ps1 -SelfTest -DryRun
```

Build the same ZIP published by GitHub Releases:

```powershell
pwsh -NoProfile -File ./tools/Build-Package.ps1
```

See [ROADMAP.md](ROADMAP.md) for the optional tray companion and future explicit render-completion integrations. Generic CPU/GPU-idle shutdown is deliberately excluded because it cannot identify job completion reliably across applications.

## License

SDAT is available under the [MIT License](LICENSE). Release packages include additional MIT-licensed components listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
