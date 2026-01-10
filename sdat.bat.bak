@echo off
:: SDAT launcher (wrapper)
:: Calls the public repo at ..\_sdat\

setlocal
set "ROOT=%~dp0"
set "SDAT_DIR=%ROOT%..\_sdat"

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
    wscript.exe "%SDAT_DIR%\tools\notify-status.vbs" "%SDAT_DIR%\shutdownat.ps1"
    exit /b 0
  )
)

:: When launched from Win+R (cmd.exe /c), open a dedicated PowerShell window for self-test output.
if defined SDAT_FROM_WINR (
  echo %* | findstr /i /c:"-SelfTest" >nul && (
    start "SDAT SelfTest" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SDAT_DIR%\tools\selftest-window.ps1" %*
    exit /b 0
  )
)

powershell -ExecutionPolicy Bypass -File "%SDAT_DIR%\shutdownat.ps1" %SDAT_FROM_WINR% %*
