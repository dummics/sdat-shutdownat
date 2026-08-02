@echo off
:: SDAT launcher (wrapper)

setlocal
set "SDAT_DIR=%~dp0"
if not exist "%SDAT_DIR%sdat-cli.exe" set "SDAT_DIR=%SDAT_DIR%..\"

set "SDAT_CANCEL_FAST=0"
if /I "%~1"=="cancel" set "SDAT_CANCEL_FAST=1"
for %%A in (%*) do (
    if /I "%%~A"=="-a" set "SDAT_CANCEL_FAST=1"
    if /I "%%~A"=="-aa" set "SDAT_CANCEL_FAST=1"
)
if "%SDAT_CANCEL_FAST%"=="1" call :SDAT_ABORT_WINDOWS_COUNTDOWN

:: Let the native CLI distinguish Win+R's transient cmd from a real terminal.
set "SDAT_WRAPPER_PROCESS=1"

"%SDAT_DIR%sdat-cli.exe" %*
exit /b %ERRORLEVEL%

:SDAT_ABORT_WINDOWS_COUNTDOWN
"%SystemRoot%\System32\shutdown.exe" /a >nul 2>nul
set "SDAT_FAST_ABORT_EXIT_CODE=%ERRORLEVEL%"
set "SDAT_FAST_ABORT_ATTEMPTED=1"
if "%SDAT_FAST_ABORT_EXIT_CODE%"=="0" (
    set "SDAT_FAST_ABORT_SUCCEEDED=1"
) else (
    set "SDAT_FAST_ABORT_SUCCEEDED=0"
)
exit /b 0
