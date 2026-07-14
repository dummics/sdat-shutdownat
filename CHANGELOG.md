# Changelog

All notable user-facing changes to SDAT are documented here.

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
