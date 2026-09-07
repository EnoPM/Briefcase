@echo off
setlocal
set "CONFIGURATION=%~1"
if "%CONFIGURATION%"=="" set "CONFIGURATION=Release"
pushd "%~dp0..\.."
dotnet run --project "validation\Briefcase.GameThread.Validation\Briefcase.GameThread.Validation.csproj" -c "%CONFIGURATION%"
set "EXIT_CODE=%ERRORLEVEL%"
popd
exit /b %EXIT_CODE%
