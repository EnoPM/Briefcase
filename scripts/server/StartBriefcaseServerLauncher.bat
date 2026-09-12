@echo off
setlocal EnableExtensions

rem Attach to an existing server, or start the EAC launcher and wait for the
rem exact Shipping server process. This deliberately avoids the version.dll proxy.
set "SERVER_DIR=%~dp0"
set "LAUNCHER=%SERVER_DIR%Briefcase.Launcher.exe"
set "EAC_LAUNCHER=%SERVER_DIR%..\..\..\DeceiveIncServer.exe"
set "SERVER_EXE=%SERVER_DIR%DeceiveIncServer-Win64-Shipping.exe"
set "SERVER_INI=%SERVER_DIR%..\..\Saved\Config\WindowsServer\TripwireServer.ini"

if not exist "%LAUNCHER%" (
  echo [ERROR] Briefcase.Launcher.exe was not found:
  echo         %LAUNCHER%
  exit /b 1
)
if not exist "%SERVER_EXE%" (
  echo [ERROR] Dedicated-server executable was not found:
  echo         %SERVER_EXE%
  exit /b 1
)
if not exist "%EAC_LAUNCHER%" (
  echo [ERROR] EAC server launcher was not found:
  echo         %EAC_LAUNCHER%
  exit /b 1
)

set "GAME_PORT=50000"
set "QUERY_PORT=50001"
if exist "%SERVER_INI%" (
  for /F "tokens=1,* delims==" %%A in ('findstr /B /I /C:"GamePort=" /C:"QueryPort=" "%SERVER_INI%"') do (
    if /I "%%A"=="GamePort" set "GAME_PORT=%%B"
    if /I "%%A"=="QueryPort" set "QUERY_PORT=%%B"
  )
)

echo [INFO] Attaching to the server if it is already running.
echo [INFO] Otherwise, starting the EAC launcher and waiting for the Shipping server.
echo [INFO] Game port: %GAME_PORT%  Query port: %QUERY_PORT%
echo [INFO] Press Ctrl+C to stop the server.
echo.

pushd "%SERVER_DIR%"
"%LAUNCHER%" --launcher "%EAC_LAUNCHER%" --wait-for "%SERVER_EXE%"
set "SERVER_EXIT=%ERRORLEVEL%"
popd

echo.
echo [INFO] Dedicated server exited with code %SERVER_EXIT%.
exit /b %SERVER_EXIT%
