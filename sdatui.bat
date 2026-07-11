@echo off
:: SDAT TUI launcher (wrapper)

setlocal
set "SDAT_DIR=%~dp0"
set "PS_EXE=powershell.exe"
where pwsh.exe >nul 2>nul && set "PS_EXE=pwsh.exe"

set "SDAT_WRAPPER_PROCESS=1"

"%PS_EXE%" -NoProfile -ExecutionPolicy Bypass -File "%SDAT_DIR%shutdownat.ps1" -Tui %*
exit /b %ERRORLEVEL%
