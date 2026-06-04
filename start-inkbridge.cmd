@echo off
setlocal EnableDelayedExpansion
REM Start the Windows side of inkbridge in mouse/cursor mode.
REM The rMPP daemon must already be running (systemd service inkbridge-daemon).
REM Draw on the rMPP afterward and your Windows cursor should track the pen.

REM Find OpenTabletDriver (it's extract-and-run, so the path varies). Priority:
REM   OTD_DIR env var -> on PATH -> common extract locations. install.cmd sets OTD_DIR for you.
set "DIR="
if defined OTD_DIR if exist "%OTD_DIR%\OpenTabletDriver.Daemon.exe" set "DIR=%OTD_DIR%"
if not defined DIR for /f "delims=" %%P in ('where OpenTabletDriver.Daemon.exe 2^>nul') do if not defined DIR set "DIR=%%~dpP"
if defined DIR if "!DIR:~-1!"=="\" set "DIR=!DIR:~0,-1!"
if not defined DIR for %%D in (
  "%USERPROFILE%\OpenTabletDriver"
  "%USERPROFILE%\Downloads\OpenTabletDriver"
  "%USERPROFILE%\Desktop\OpenTabletDriver"
  "C:\OpenTabletDriver"
  "%ProgramFiles%\OpenTabletDriver"
) do if not defined DIR if exist "%%~D\OpenTabletDriver.Daemon.exe" set "DIR=%%~D"
if not defined DIR (
  echo ERROR: OpenTabletDriver not found. Set OTD_DIR to the folder containing
  echo        OpenTabletDriver.Daemon.exe, e.g.:  setx OTD_DIR "C:\path\to\OpenTabletDriver"
  pause
  exit /b 1
)

REM Settings file: repo layout (otd-plugin\...) or release-zip layout (alongside this script).
set "CFG=%~dp0otd-plugin\mouse-mode-settings.json"
if not exist "%CFG%" set "CFG=%~dp0mouse-mode-settings.json"

echo Stopping any existing OpenTabletDriver...
taskkill /f /im OpenTabletDriver.Daemon.exe >nul 2>&1
taskkill /f /im OpenTabletDriver.UX.Wpf.exe >nul 2>&1
timeout /t 1 >nul

echo Starting OpenTabletDriver daemon...
start "" /b "%DIR%\OpenTabletDriver.Daemon.exe"
timeout /t 5 >nul

echo Applying inkbridge mouse-mode settings (connects to rMPP at 10.11.99.1:9292)...
"%DIR%\OpenTabletDriver.Console.exe" loadsettings "%CFG%"

echo.
echo ===========================================================
echo  inkbridge is running. Draw on the reMarkable - the Windows
echo  cursor should track the pen. The rMPP keeps working normally
echo  (xochitl is never paused); you will just see ink strokes on
echo  the e-ink as you draw - that is cosmetic and harmless.
echo.
echo  To STOP: run  stop-inkbridge.cmd   (or close the daemon).
echo ===========================================================
