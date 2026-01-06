@echo off
:: Use:
::   sdat
::   sdat HHMM [-Test]
::   sdat HHMM -p [-Test]
::   sdat -tui
::   sdat -a            (cancel one-time/volatile)
::   sdat -aa           (cancel all; alias: -Clean)

:: This is a wrapper used for WIN+R calls

powershell -ExecutionPolicy Bypass -File "%~dp0shutdownat.ps1" %*
