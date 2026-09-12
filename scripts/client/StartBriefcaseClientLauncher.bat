@echo off
setlocal EnableExtensions

rem Attach to an existing client, or start the official EAC launcher and wait
rem for the exact Deceive Inc. Shipping process before injecting Briefcase.
set "CLIENT_DIR=%~dp0"
set "BRIEFCASE_LAUNCHER=%CLIENT_DIR%Briefcase.Launcher.exe"
set "EAC_LAUNCHER=%CLIENT_DIR%..\..\..\DeceiveInc.exe"
set "CLIENT_EXE=%CLIENT_DIR%DeceiveInc-Win64-Shipping.exe"

if not exist "%BRIEFCASE_LAUNCHER%" (
  echo [ERROR] Briefcase.Launcher.exe was not found:
  echo         %BRIEFCASE_LAUNCHER%
  exit /b 1
)
if not exist "%EAC_LAUNCHER%" (
  echo [ERROR] EAC client launcher was not found:
  echo         %EAC_LAUNCHER%
  exit /b 1
)
if not exist "%CLIENT_EXE%" (
  echo [ERROR] Client Shipping executable was not found:
  echo         %CLIENT_EXE%
  exit /b 1
)

echo [INFO] Attaching to the client if it is already running.
echo [INFO] Otherwise, starting the EAC launcher and waiting for the Shipping process.
echo [INFO] Close the game normally to end this launcher session.
echo.

pushd "%CLIENT_DIR%"
"%BRIEFCASE_LAUNCHER%" --launcher "%EAC_LAUNCHER%" --wait-for "%CLIENT_EXE%"
set "CLIENT_EXIT=%ERRORLEVEL%"
popd

echo.
echo [INFO] Deceive Inc. exited with code %CLIENT_EXIT%.
exit /b %CLIENT_EXIT%
