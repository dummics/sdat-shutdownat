# ShutdownAT (SDAT) — Windows power scheduling without friction

[![CI](https://github.com/dummics/sdat-shutdownat/actions/workflows/ci.yml/badge.svg)](https://github.com/dummics/sdat-shutdownat/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/dummics/sdat-shutdownat)](https://github.com/dummics/sdat-shutdownat/releases/latest)
[![License](https://img.shields.io/github/license/dummics/sdat-shutdownat)](LICENSE)

Schedule a shutdown, restart, or suspend using the fastest surface for the moment: Win+R, a terminal, the interactive TUI, or the Windows 11 app. ShutdownAT is local, open source, account-free, and telemetry-free; `sdat` remains its short CLI command.

```text
sdat 36m
sdat 23:41
sdat daily 02:00
sdat cancel
```

SDAT is useful when a render, export, download, or long task should finish before the PC turns off.

## Install

SDAT supports Windows 10 version 2004 or newer and Windows 11 on x64. The default release is a compact framework-dependent package; its one-click installer adds any missing official Microsoft runtimes, installs SDAT for the current user, and opens the setup screen.

```powershell
irm https://raw.githubusercontent.com/dummics/sdat-shutdownat/main/install.ps1 | iex
```

The installer downloads the latest GitHub Release, verifies its SHA256 checksum, installs to `%LOCALAPPDATA%\Programs\SDAT`, adds searchable **ShutdownAT** and **ShutdownAT Terminal** Start menu shortcuts, and places the dedicated `bin` launcher directory on the user PATH. No PowerShell alias or profile change is required. For the easiest manual install, download `sdat-v*-windows.zip` from the [latest release](https://github.com/dummics/sdat-shutdownat/releases/latest), extract the whole archive, and double-click **Install SDAT.cmd**. Advanced users can run `scripts\install.ps1` directly. A larger `windows-portable` artifact can also be built when an entirely self-contained copy is needed.

## Fast commands

| Command | Result |
|---|---|
| `sdat 3h` | Shut down once in three hours |
| `sdat 90m` | Shut down once in 90 minutes |
| `sdat 23:30` | Shut down once at 23:30 |
| `sdat daily 02:00` | Shut down every day at 02:00 |
| `ssat 45m` | Suspend once in 45 minutes |
| `sdat 01:30 -Restart` | Restart once at 01:30 |
| `sdat` | Open the TUI in an interactive terminal; when redirected, show status |
| `sdat status` | Show active schedules without opening the TUI |
| `sdat cancel` | Abort a Windows countdown and cancel the one-time action |
| `sdat cancel all` | Cancel one-time and daily actions |
| `sdat skip` | Skip the next daily occurrence once |
| `sdat preview 36m` | Parse and preview without changing state |
| `sdat tui` | Open the interactive terminal UI |
| `sdat ui` | Open the ShutdownAT Windows app |
| `sdat logs` | Show recent diagnostic history |
| `sdat health` | Check SQLite and scheduler health |
| `sdat reconcile` | Repair Task Scheduler from SQLite |

Short aliases remain available: `-a`, `-aa`, `-p`, `-s`, `-k`, and `-tui`. Machine clients can add `--json`; responses use an explicitly versioned envelope. This keeps the CLI suitable for scripts, AI agents, and a future MCP surface without giving those clients direct access to Windows power commands.

### Daily overlap safety

By default, a one-time action within two hours of the next daily action skips that daily occurrence. The window is configurable from 0 to 1440 minutes in the companion. Keep both for one command with:

```text
sdat 45m -k
```

## Windows app

Search for **ShutdownAT** in Start, run `sdat ui`, or launch `SDAT.exe` directly. The executable keeps its compact technical name while the installed product is presented as ShutdownAT. Its WinUI 3 shell provides Overview, Schedule, Notifications, Quick access, Help & recovery, About, and Settings panels. Static and dynamic UI text is localized in English and Italian using Windows MRT Core resources. The app follows the Windows language by default, or you can choose Italian or English in Settings and apply the change with a one-click restart.

The richer keyboard-driven terminal interface remains a first-class client of the same C# core. Open it with `sdat`, `sdat tui`, or the **ShutdownAT Terminal** Start shortcut. It includes schedule preview, active-schedule management, daily skip, database health, recent activity, and explicit Task Scheduler repair.

The optional tray companion provides a configurable global hotkey (default `Ctrl+Alt+S`) for the bottom-center quick scheduler. A conflicting hotkey does not take down the tray; SDAT reports the conflict and keeps the previous working combination when possible.

Reminder timing, the critical overlay, startup behavior, overlap policy, and hotkey are local settings. Reminder actions carry the schedule id and revision, so an old notification cannot cancel a newer schedule. Dismiss only closes the reminder; cancelling from the app requires confirmation; Snooze is available for one-time actions.

Windows notification availability and Focus/Do Not Disturb behavior remain controlled by Windows. SDAT does not claim an unconditional bypass. For shutdown and restart, Windows still owns the final 30-second system countdown.

## Durable local state

SQLite at `%LOCALAPPDATA%\SDAT\sdat.db` is authoritative. Windows Task Scheduler is a recoverable projection and is reconciled from the database. Mutations are serialized across processes, schedules use stable ids and monotonically increasing revisions, and stale task or notification activations are ignored.

SDAT keeps up to five verified backups under `%LOCALAPPDATA%\SDAT\backups`. Startup performs database health checks and blocks power execution if state is corrupt or from a newer unsupported schema. A verified compatible backup can be restored when the primary database cannot be opened.

When upgrading from v1, the installer preserves the old JSON state and script under `%LOCALAPPDATA%\SDAT\legacy-v1`. The native migrator imports only recognized state and exact SDAT v1 task signatures; ambiguous tasks are left untouched and reported.

## Update and remove

```text
sdat version
sdat update
sdat uninstall
sdat uninstall --keep-data
```

Update packages are checksum-verified and promoted transactionally with rollback on failure. Unexpected files found inside the install directory are preserved under `%LOCALAPPDATA%\SDAT\install-backups` before a successful replacement. Uninstall removes only a verified SDAT installation, its shortcuts, and owned tasks. The clickable **Uninstall ShutdownAT** shortcut preserves schedules and settings in a timestamped backup; `sdat uninstall --keep-data` does the same from the CLI.

Shutdown and restart force applications to close. Save work before scheduling them.

## Development

Requirements: .NET 10 SDK and a Windows 10/11 x64 development host.

```powershell
dotnet restore SDAT.slnx
dotnet test SDAT.slnx -c Release --no-restore
dotnet build src/Sdat.App/Sdat.App.csproj -c Release --no-restore
pwsh -NoProfile -File ./tools/Build-Package.ps1
# Optional larger package with bundled .NET and Windows App SDK runtimes:
pwsh -NoProfile -File ./tools/Build-Package.ps1 -Flavor Portable
```

Automated tests and package verification never trigger a real shutdown, restart, or suspend. Live power-action testing requires separate explicit authorization.

See [ROADMAP.md](ROADMAP.md) for release gates and intentionally deferred integrations.

## License

SDAT source is available under the [MIT License](LICENSE). Release packages include third-party components governed by their own terms; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the packaged `licenses` directory.
