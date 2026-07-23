@echo off
setlocal
title Uninstall ShutdownAT
set "SDAT_WRAPPER_PROCESS=1"
set "SDAT_UNINSTALL=%~dp0scripts\uninstall.ps1"
if not exist "%SDAT_UNINSTALL%" set "SDAT_UNINSTALL=%~dp0uninstall.ps1"
set "SDAT_INSTALLED_WRAPPER=0"
if exist "%~dp0SDAT.exe" set "SDAT_INSTALLED_WRAPPER=1"
cd /d "%TEMP%"

echo.
echo  ShutdownAT uninstaller
echo  ----------------------
echo  Schedules and settings will be moved to a timestamped backup.
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SDAT_UNINSTALL%" -KeepData
if errorlevel 1 (
    echo.
    echo  Uninstall failed. The message above contains the reason.
    echo.
    pause
    exit /b 1
)

echo.
echo  ShutdownAT was removed safely. You can close this window.
echo.
if "%SDAT_INSTALLED_WRAPPER%"=="1" exit /b 0
pause
exit /b 0
