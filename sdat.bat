@echo off
:: Usage:
::   sdat
::   sdat HHMM [-Test]
::   sdat HHMM -p [-Test]
::   sdat -tui
::   sdat -A   (alias: -Clean)

:: When launched from Win+R (cmd.exe /c), the console closes immediately and can kill child console processes.
:: For `sdat` with no args, launch a detached notification via wscript.exe.
set "SDAT_FROM_WINR="
if defined CMDCMDLINE (
  echo %CMDCMDLINE% | findstr /i " /c " >nul && (
    set "SDAT_FROM_WINR=-FromWinR"
  )
)

if "%~1"=="" (
  if defined SDAT_FROM_WINR (
    wscript.exe "%~dp0\\tools\\notify-status.vbs" "%~dp0\\shutdownat.ps1"
    exit /b 0
  )
)

:: When launched from Win+R (cmd.exe /c), open a dedicated PowerShell window for self-test output.
if defined SDAT_FROM_WINR (
  echo %* | findstr /i /c:"-SelfTest" >nul && (
    start "SDAT SelfTest" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0\\tools\\selftest-window.ps1" %*
    exit /b 0
  )
)

powershell -ExecutionPolicy Bypass -File "%~dp0\\shutdownat.ps1" %SDAT_FROM_WINR% %*
