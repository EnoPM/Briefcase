@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0validate_persisted_sdk_emitter.ps1" %*
exit /b %ERRORLEVEL%
