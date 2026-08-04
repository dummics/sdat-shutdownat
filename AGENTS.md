# SDAT workspace instructions

## Installed copy synchronization

- A completed SDAT implementation or fix is not finished when only the repository or package changes. After automated verification, update Dom's current per-user installation at `%LOCALAPPDATA%\Programs\SDAT` from the newly built package in the same task.
- Verify that `Get-Command sdat` still resolves to the expected installed wrapper and that the installed wrapper/binaries contain the behavior introduced by the change. Use safe commands such as `sdat preview`, `sdat status`, or file/hash checks; never schedule, cancel, shut down, restart, or suspend the real PC unless Dom explicitly requests that live action in the current turn.
- If the installed copy cannot be updated safely, report the concrete blocker and do not claim the change works on Dom's PC.

