@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BuildServerAdminControlClient.ps1" %*
exit /b %errorlevel%
