@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BuildServerFramework.ps1" %*
exit /b %errorlevel%
