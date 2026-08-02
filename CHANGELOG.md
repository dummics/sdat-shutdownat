# Changelog

All notable user-facing changes to SDAT are documented here.

## 2.0.0 - Unreleased

- Restored centered Spectre.Console result panels for interactive CLI scheduling, preview, status, cancellation, and errors; transient Win+R consoles now remain readable for six seconds while existing terminals and redirected/JSON clients return immediately.
- Unified cancellation across toast, critical overlay, tray, palette, TUI, and CLI with a revision-safe local cross-process signal plus authoritative schedule polling, so a successful external cancel closes the matching persistent countdown popup.
- Restored a richer native C# terminal experience with schedule preview, task management, health/history diagnostics, safe repair, and an interactive no-argument entry path that preserves non-interactive status output.
- Presented the installed Windows utility as ShutdownAT while keeping the compact `sdat` CLI and `SDAT.exe` technical executable names, and added searchable graphical and terminal Start shortcuts.
- Replaced the PowerShell scheduling backend with a native C#/.NET core while preserving the established CLI commands, aliases, Win+R workflow, and Spectre.Console TUI.
- Added authoritative SQLite state, cross-process mutation locking, verified backups, health checks, guarded recovery, and forward-schema fail-safe behavior.
- Added revision-safe Windows Task Scheduler projection and stale-safe task, notification, cancellation, and snooze activations.
- Added the WinUI 3 companion with Overview, Schedule, and one unified Settings surface for notifications, language, quick access, diagnostics, logging, developer tools, and product information.
- Added side-effect-free live schedule previews to the app and quick palette, friendly localized validation backed by stable core error codes, and contextual primary actions that show the resolved time before saving.
- Made Overview and the tray operational views of authoritative schedule state, with contextual +10 minute, modify, cancel, skip-next, and disable actions; countdown controls now name the exact power action they stop.
- Consolidated Notifications and About into the single scrollable Settings page, and added a one-time background hint that shows the configured quick-scheduler shortcut when the main window is first closed to the tray.
- Reworked Settings into always-open scrollable sections with a persistent bottom-right Save action, and added a safe quick-palette preview to Developer mode.
- Added the configurable quick-scheduler hotkey, native tray menu, compact critical overlay with configurable edge/corner placement, Windows reminder notifications, and per-user startup registration with a persistent single-instance background companion.
- Made the quick-scheduler hotkey available whenever ShutdownAT is open, turned it into an open/close toggle, and tightened the palette with reduced-motion-aware fade transitions.
- Refined the quick palette with reliable foreground text focus, global Escape and click-away dismissal, a fixed non-draggable layout, persistent desktop acrylic when unfocused, no bright window outline, a clean acrylic edge, stronger entrance/exit motion, and transient success feedback that does not reappear on reopen.
- Refined quick-palette opening so its acrylic surface and controls fade in as one pre-rendered window, clipped the native surface to the same rounded shape, widened the compact layout, and moved transient feedback above the controls to prevent clipping or overlap.
- Restored the native Windows 11 border and DWM shadow around the quick palette, and adopted the official ShutdownAT logo for the executable, Start shortcuts, tray icon, package documentation, and repository README.
- Enlarged the generated Windows icon artwork to nearly double its previous occupied area so the app and tray glyph remain legible at small sizes.
- Added an easy-to-find developer section with a backend-enforced safe test mode, synthetic notification/countdown previews, configurable rolling local logging, log/data shortcuts, and a compact diagnostic report.
- Made in-app status messages compact, dismissible, and self-closing, and made synthetic Windows test notifications transient.
- Kept Windows reminder actions registered across scheduled task processes so Cancel and Open ShutdownAT remain actionable.
- Made quick-palette feedback morph into the free screen direction without moving the command row, size itself to the measured message instead of leaving reserved empty space, and use a snappy 100 ms low-overhead resize sequence; palette fade-in/fade-out now complete in 140/100 ms. Also added configurable edge/corner placement with vertical side layouts and shortened the primary action to Schedule/Pianifica.
- Kept the quick palette open after scheduling with an action-and-time-specific cancel control, added a visible final 30-second overlay for shutdown and restart, and made CLI, notification, palette, and overlay cancellation share the native Windows countdown abort result.
- Fixed `sdat cancel` and `sdat -a` reporting `Nothing to cancel` after the launcher had already stopped an active Windows shutdown countdown.
- Added English and Italian MRT Core localization for static and dynamic companion UI, with a persistent Windows-language/Italian/English selector and one-click app restart.
- Rewrote the Windows UI copy and recent-activity labels in plain, user-friendly language with short inline explanations.
- Reduced settings-field width and text density, hid unavailable cancel actions, and added a confirmation step before cancelling a schedule from the Overview.
- Added configurable reminder offsets and daily-overlap policy; a one-time action can safely skip one nearby daily occurrence without deleting the daily schedule.
- Added versioned machine-readable JSON, side-effect-free preview, database health, reconciliation, and structured diagnostic history commands.
- Added strict v1 state/task migration with preserved rollback evidence and ambiguous-task fail-safe behavior.
- Rebuilt install, update, uninstall, packaging, checksum, and CI flows around native executables, with a compact framework-dependent default and an optional self-contained artifact.
- Made the installed app reconcile its saved startup preference with the Windows Run entry on launch, repairing missing or stale registrations automatically.
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
