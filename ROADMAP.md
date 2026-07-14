# Roadmap

## Public v1

- Human time input from Win+R: `sdat 3h`, `sdat 90m`, or `sdat 23:30`.
- Readable status and cancellation commands with short aliases preserved.
- Per-user installer, updater, uninstaller, versioned ZIP, checksums, and Windows CI.

## Next

- Optional tray companion using the same state and scheduling engine.
- WinGet distribution after the public installer format has proven stable.
- Research an explicit `arm -> done` completion signal for render/export integrations.

## Deliberately not planned

SDAT will not infer job completion from generic CPU or GPU utilization. Those signals vary by application and can produce unsafe false positives. Application support should use explicit completion hooks or separately maintained adapters.
