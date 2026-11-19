@echo off
rem Builds and starts the development version of Rimlight.
rem Double-click this file. Rimlight appears in the system tray (bottom-right, maybe under the ^ arrow).
setlocal
set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
cd /d "%~dp0"

echo Closing any running Rimlight...
taskkill /im Rimlight.exe /f >nul 2>&1

echo Building...
"%DOTNET_ROOT%\dotnet.exe" build src\Rimlight -c Debug -v q -nologo
if errorlevel 1 (
  echo.
  echo Build failed - see the messages above.
  pause
  exit /b 1
)

echo Starting Rimlight...
start "" "src\Rimlight\bin\Debug\net10.0-windows10.0.19041.0\Rimlight.exe" %*
