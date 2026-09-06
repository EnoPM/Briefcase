@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BuildDevSample.ps1" %*
exit /b %ERRORLEVEL%
