@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BuildServerAdminControlServer.ps1" %*
exit /b %errorlevel%
