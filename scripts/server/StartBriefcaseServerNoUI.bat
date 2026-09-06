@echo off
setlocal EnableExtensions

rem This file is installed beside DeceiveIncServer-Win64-Shipping.exe.
rem It bypasses DeceiveIncServer.exe, which is the graphical configuration UI.
set "SERVER_DIR=%~dp0"
set "SERVER_EXE=%SERVER_DIR%DeceiveIncServer-Win64-Shipping.exe"
set "SERVER_INI=%SERVER_DIR%..\..\Saved\Config\WindowsServer\TripwireServer.ini"

if not exist "%SERVER_EXE%" (
  echo [ERROR] Dedicated-server executable not found:
  echo         %SERVER_EXE%
  exit /b 1
)

rem CSV output keeps the complete image name; the default table truncates long names.
tasklist /FI "IMAGENAME eq DeceiveIncServer-Win64-Shipping.exe" /FO CSV /NH 2>NUL | find /I "DeceiveIncServer-Win64-Shipping.exe" >NUL
if not errorlevel 1 (
  echo [ERROR] A Deceive Inc. dedicated server is already running.
  echo         Stop it before starting another instance.
  exit /b 2
)

set "GAME_PORT=50000"
set "QUERY_PORT=50001"
if exist "%SERVER_INI%" (
  for /F "tokens=1,* delims==" %%A in ('findstr /B /I /C:"GamePort=" /C:"QueryPort=" "%SERVER_INI%"') do (
    if /I "%%A"=="GamePort" set "GAME_PORT=%%B"
    if /I "%%A"=="QueryPort" set "QUERY_PORT=%%B"
  )
)

echo [INFO] Starting Briefcase dedicated server without the configuration UI.
echo [INFO] Game port: %GAME_PORT%  Query port: %QUERY_PORT%
echo [INFO] Press Ctrl+C to stop the server.
echo.

pushd "%SERVER_DIR%"
"%SERVER_EXE%" -unattended -NoSplash -stdout -FullStdOutLogOutput -Port=%GAME_PORT% -QueryPort=%QUERY_PORT%
set "SERVER_EXIT=%ERRORLEVEL%"
popd

echo.
echo [INFO] Dedicated server exited with code %SERVER_EXIT%.
exit /b %SERVER_EXIT%
