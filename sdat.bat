@echo off
:: Use: sdat HHMM [-Test] [-Clean]

:: This is a wrapper used for WIN+R calls

powershell -ExecutionPolicy Bypass -File "%~dp0shutdownat.ps1" %*