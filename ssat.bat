@echo off
:: SSAT launcher (wrapper)
:: Calls the public repo at ..\_sdat\

setlocal
set "ROOT=%~dp0"
set "SDAT_DIR=%ROOT%..\_sdat"
set "PS_EXE=powershell.exe"
where pwsh.exe >nul 2>nul && set "PS_EXE=pwsh.exe"

:: When launched from Win+R (cmd.exe /c), the console closes immediately and can kill child console processes.
:: For `ssat` with no args, launch a detached notification via wscript.exe.
set "SDAT_FROM_WINR=0"
if defined CMDCMDLINE (
  echo %CMDCMDLINE% | findstr /i " /c " >nul && (
    set "SDAT_FROM_WINR=1"
  )
)

if "%~1"=="" (
  if "%SDAT_FROM_WINR%"=="1" (
    wscript.exe "%SDAT_DIR%\tools\notify-status.vbs" "%SDAT_DIR%\shutdownat.ps1"
    exit /b 0
  )
)

:: When launched from Win+R (cmd.exe /c), open a dedicated PowerShell window for self-test output.
if "%SDAT_FROM_WINR%"=="1" (
  echo %* | findstr /i /c:"-SelfTest" >nul && (
    start "SDAT SelfTest" "%PS_EXE%" -NoProfile -ExecutionPolicy Bypass -File "%SDAT_DIR%\tools\selftest-window.ps1" -Suspend %*
    exit /b 0
  )
)

"%PS_EXE%" -ExecutionPolicy Bypass -File "%SDAT_DIR%\shutdownat.ps1" -Suspend %*
