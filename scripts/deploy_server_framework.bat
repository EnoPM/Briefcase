@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0DeployServerFramework.ps1" %*
exit /b %ERRORLEVEL%
