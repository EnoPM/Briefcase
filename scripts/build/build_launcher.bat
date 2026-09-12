@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BuildLauncher.ps1" %*
exit /b %ERRORLEVEL%
