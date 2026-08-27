@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0sign-local.ps1" %*
exit /b %ERRORLEVEL%
