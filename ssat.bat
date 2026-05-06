@echo off
:: SSAT launcher (wrapper)
:: Calls the public repo at ..\_sdat\

setlocal
set "ROOT=%~dp0"
set "SDAT_DIR=%ROOT%..\_sdat"
set "PS_EXE=powershell.exe"
where pwsh.exe >nul 2>nul && set "PS_EXE=pwsh.exe"

:: Some shells invoke .bat files through cmd.exe /c, so do not short-circuit no-arg status here.
:: Let shutdownat.ps1 print the useful status view and decide whether a notification is useful.
set "SDAT_FROM_WINR=0"
if defined CMDCMDLINE (
  echo %CMDCMDLINE% | findstr /i " /c " >nul && (
    set "SDAT_FROM_WINR=1"
  )
)

"%PS_EXE%" -ExecutionPolicy Bypass -File "%SDAT_DIR%\shutdownat.ps1" -Suspend %*
