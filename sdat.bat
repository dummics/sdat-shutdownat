@echo off
:: Use:
::   sdat
::   sdat HHMM [-Test]
::   sdat HHMM -p [-Test]
::   sdat -tui
::   sdat -a            (cancel one-time/volatile)
::   sdat -aa           (cancel all; alias: -Clean)

:: This is a wrapper used for WIN+R calls

:: When launched from Win+R (cmd.exe /c), the console closes immediately and can kill child console processes.
:: For `sdat` with no args, launch a detached notification via wscript.exe.
if "%~1"=="" (
  if defined CMDCMDLINE (
    echo %CMDCMDLINE% | findstr /i " /c " >nul && (
      wscript.exe "%~dp0tools\\notify-status.vbs" "%~dp0shutdownat.ps1"
      exit /b 0
    )
  )
)

:: When launched from Win+R (cmd.exe /c), open a dedicated PowerShell window for self-test output.
if defined CMDCMDLINE (
  echo %CMDCMDLINE% | findstr /i " /c " >nul && (
    echo %* | findstr /i /c:"-SelfTest" >nul && (
      start "SDAT SelfTest" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\\selftest-window.ps1" %*
      exit /b 0
    )
  )
)

powershell -ExecutionPolicy Bypass -File "%~dp0shutdownat.ps1" %*
