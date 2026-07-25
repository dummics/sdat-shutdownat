# Roadmap

## SDAT 2.0 release gate

- Complete native C#/.NET cutover with SQLite as authoritative state.
- Preserve CLI aliases and the Spectre.Console TUI alongside the WinUI 3 companion.
- Ship revision-safe Task Scheduler projection, notifications, overlay actions, recovery, and verified v1 migration.
- Verify the compact per-user package, optional self-contained artifact, update path, backup-first uninstall path, checksums, and CI.
- Complete a real Windows visual/runtime review of the shell, palette, tray, toast registration, and overlay before release.
- Merge and publish only after owner approval.

## After 2.0

- Evaluate WinGet distribution after the installer format has proven stable.
- Design an optional local MCP server over the versioned CLI contract, with explicit confirmation and cancellation safety.
- Research opt-in remote control and Wake-on-LAN without accounts or mandatory cloud infrastructure.
- Add explicit completion adapters for render/export applications when they expose reliable completion signals.

## Deliberately not planned

SDAT will not infer job completion from generic CPU or GPU utilization. Those signals vary by application and can produce unsafe false positives. Integrations should use explicit completion hooks or separately maintained adapters.
