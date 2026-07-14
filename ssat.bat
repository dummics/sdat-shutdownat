@echo off
:: SSAT launcher (wrapper)

setlocal
set "SDAT_DIR=%~dp0"

set "SDAT_CANCEL_FAST=0"
if /I "%~1"=="cancel" set "SDAT_CANCEL_FAST=1"
for %%A in (%*) do (
    if /I "%%~A"=="-a" set "SDAT_CANCEL_FAST=1"
    if /I "%%~A"=="-aa" set "SDAT_CANCEL_FAST=1"
)
if "%SDAT_CANCEL_FAST%"=="1" (
    "%SystemRoot%\System32\shutdown.exe" /a >nul 2>nul
    set "SDAT_FAST_ABORT_ATTEMPTED=1"
    if errorlevel 1 (
        set "SDAT_FAST_ABORT_SUCCEEDED=0"
    ) else (
        set "SDAT_FAST_ABORT_SUCCEEDED=1"
    )
)

set "PS_EXE=powershell.exe"
where pwsh.exe >nul 2>nul && set "PS_EXE=pwsh.exe"

:: Let shutdownat.ps1 distinguish a transient Win+R console from a real terminal.
set "SDAT_WRAPPER_PROCESS=1"

"%PS_EXE%" -NoProfile -ExecutionPolicy Bypass -File "%SDAT_DIR%shutdownat.ps1" -Suspend %*
exit /b %ERRORLEVEL%
