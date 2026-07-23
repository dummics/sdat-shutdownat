# Changelog

All notable user-facing changes to SDAT are documented here.

## 2.0.0 - Unreleased

- Restored a richer native C# terminal experience with schedule preview, task management, health/history diagnostics, safe repair, and an interactive no-argument entry path that preserves non-interactive status output.
- Presented the installed Windows utility as ShutdownAT while keeping the compact `sdat` CLI and `SDAT.exe` technical executable names, and added searchable graphical and terminal Start shortcuts.
- Replaced the PowerShell scheduling backend with a native C#/.NET core while preserving the established CLI commands, aliases, Win+R workflow, and Spectre.Console TUI.
- Added authoritative SQLite state, cross-process mutation locking, verified backups, health checks, guarded recovery, and forward-schema fail-safe behavior.
- Added revision-safe Windows Task Scheduler projection and stale-safe task, notification, cancellation, and snooze activations.
- Added the WinUI 3 companion with Overview, Schedule, Notifications, Quick access, Help & recovery, About, and Settings panels.
- Added the configurable quick-scheduler hotkey, native tray menu, critical bottom-center overlay, Windows reminder notifications, and per-user startup registration.
- Added English and Italian MRT Core localization for static and dynamic companion UI, with a persistent Windows-language/Italian/English selector and one-click app restart.
- Rewrote the Windows UI copy and recent-activity labels in plain, user-friendly language with short inline explanations.
- Added configurable reminder offsets and daily-overlap policy; a one-time action can safely skip one nearby daily occurrence without deleting the daily schedule.
- Added versioned machine-readable JSON, side-effect-free preview, database health, reconciliation, and structured diagnostic history commands.
- Added strict v1 state/task migration with preserved rollback evidence and ambiguous-task fail-safe behavior.
- Rebuilt install, update, uninstall, packaging, checksum, and CI flows around native executables, with a compact framework-dependent default and an optional self-contained artifact.
- Removed unused Windows App SDK AI, ML, widgets, and DWrite package dependencies from the shipped runtime graph.
- Added an organized package layout, one-click install/uninstall launchers, prerequisite bootstrap, Start menu shortcuts, and backup-first clickable uninstall.
- Added a dedicated CLI `bin` PATH surface so `sdat` and `ssat` resolve to the installed launchers without PowerShell aliases or collision with `SDAT.exe`.
- Added protected-directory guardrails, transactional update rollback, and recoverable backup of non-package files found during replacement.
- Fixed clickable installer updates under Windows PowerShell 5.1 by removing a PowerShell 7-only path API from the runtime installer.
- Removed the obsolete v1 PowerShell backend and bundled PwshSpectreConsole runtime from the release tree.

## 1.0.2 - 2026-07-14

- Fixed one-time and daily actions being skipped when Task Scheduler started them a few seconds after their target time.
- Added `sdat logs` for a concise diagnostic view with a predictable log location.
- Added automatic 30-day log retention and a 5 MB cap per log file.

## 1.0.1 - 2026-07-14

- Removed the undocumented `-Clean` compatibility alias and its launcher handling.
- Removed automatic backfilling and normalization of older config/state shapes.
- Added strict validation for the single supported config, state, and action schemas.
- Removed the legacy-state regression path from the self-test suite.

## 1.0.0 - 2026-07-14

- Added human-readable commands such as `sdat cancel`, `sdat daily 02:00`, `sdat status`, `sdat update`, and `sdat uninstall` while preserving the short switches.
- Kept shutdown cancellation on the immediate launcher path, before PowerShell and Task Scheduler cleanup.
- Polished the Spectre-powered status and cancellation output for quick Win+R use.
- Added a per-user installer, updater, and uninstaller with no administrator requirement.
- Added reproducible Windows release packages with a bundled, pinned PwshSpectreConsole dependency and SHA256 verification.
- Added GitHub Actions for self-tests and tagged releases.
- Made Task Scheduler registration independent of the Windows date format and display language.
