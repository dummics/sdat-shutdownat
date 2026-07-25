ShutdownAT for Windows
======================

Install
-------
1. Extract the whole ZIP.
2. Double-click "Install SDAT.cmd".
3. ShutdownAT installs for your Windows account and opens the setup screen.

The installer adds ShutdownAT's dedicated CLI bin directory to PATH. PowerShell
aliases and profile edits are not required.

After installation, search for "ShutdownAT" or "ShutdownAT Terminal" in Start.
You can also run `sdat ui` or `sdat tui` from a terminal.

The slim package downloads official Microsoft runtimes only when they are
missing. An internet connection can therefore be required on first install.

Uninstall
---------
Double-click "Uninstall SDAT.cmd" from this extracted folder, or run:

    sdat uninstall --keep-data

The clickable uninstaller keeps a timestamped backup of schedules and settings.
Use `sdat uninstall` only when you intentionally want to remove local data too.

Advanced use
------------
- Application files are under app\.
- Installer scripts are under scripts\.
- Project documentation and licenses are under docs\.
- The CLI remains available as `sdat`, `ssat`, `sdat tui`, and `sdat ui` after install.
