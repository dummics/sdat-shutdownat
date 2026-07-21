# Third-party notices

SDAT self-contained release packages include third-party components. Their licenses remain their own and are not replaced by SDAT's MIT license.

Primary direct/runtime dependencies include:

- Microsoft Windows App SDK 2.3.1 — Microsoft Software License Terms; the release package includes the upstream `license.txt` and `NOTICE.txt` in `licenses/`.
- Microsoft.Data.Sqlite 10.0.10 — MIT License.
- SQLitePCLRaw 3.0.4 and SourceGear SQLite — Apache-2.0 and upstream SQLite distribution terms.
- TaskScheduler 2.12.2 by David Hall — MIT License.
- Spectre.Console 0.57.2 by Patrik Svensson and contributors — MIT License.
- The self-contained .NET runtime and its dependencies — their respective Microsoft and third-party license terms.

Project links:

- https://github.com/microsoft/WindowsAppSDK
- https://github.com/dotnet/efcore
- https://github.com/ericsink/SQLitePCL.raw
- https://github.com/dahall/TaskScheduler
- https://github.com/spectreconsole/spectre.console
- https://github.com/dotnet/runtime

Exact resolved dependency versions are recorded in `Directory.Packages.props` and generated project assets. The release package manifest records the shipped file set. Upstream copyright and license notices apply.
