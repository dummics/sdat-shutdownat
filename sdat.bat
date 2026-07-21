@echo off
:: SDAT launcher (wrapper)

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

"%SDAT_DIR%sdat-cli.exe" %*
exit /b %ERRORLEVEL%
