@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BuildFramework.ps1" %*
exit /b %ERRORLEVEL%
