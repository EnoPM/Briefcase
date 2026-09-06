@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ProvisionAdminAuthentication.ps1" %*
exit /b %ERRORLEVEL%
