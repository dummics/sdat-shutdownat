@echo off
:: SDAT TUI launcher (wrapper)
:: Calls the public repo at ..\_sdat\

setlocal
set "ROOT=%~dp0"
set "SDAT_DIR=%ROOT%..\_sdat"
set "PS_EXE=powershell.exe"
where pwsh.exe >nul 2>nul && set "PS_EXE=pwsh.exe"

:: Keep wrappers quiet: no background notification process by default.
set "SDAT_FROM_WINR=0"

"%PS_EXE%" -ExecutionPolicy Bypass -File "%SDAT_DIR%\shutdownat.ps1" -Tui %*
