@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0DeployFramework.ps1" %*
exit /b %ERRORLEVEL%
