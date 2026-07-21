@echo off
:: SDAT TUI launcher (wrapper)

setlocal
set "SDAT_DIR=%~dp0"
"%SDAT_DIR%sdat-cli.exe" tui %*
exit /b %ERRORLEVEL%
