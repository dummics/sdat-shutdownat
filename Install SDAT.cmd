@echo off
setlocal
title Install ShutdownAT

echo.
echo  ShutdownAT installer
echo  --------------------
echo  This installs ShutdownAT for your Windows account and opens setup when finished.
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1" -SourcePath "%~dp0." -Launch
if errorlevel 1 (
    echo.
    echo  Installation failed. The message above contains the reason.
    echo.
    pause
    exit /b 1
)

exit /b 0
