@echo off
REM Start the Windows side of inkbridge in mouse/cursor mode.
REM The rMPP daemon must already be running (systemd service inkbridge-daemon).
REM Draw on the rMPP afterward and your Windows cursor should track the pen.

REM Set OTD_DIR to your OpenTabletDriver install folder, or edit the default below.
if "%OTD_DIR%"=="" set OTD_DIR=C:\OpenTabletDriver
set DIR=%OTD_DIR%
set CFG=%~dp0otd-plugin\mouse-mode-settings.json

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
echo  cursor should track the pen. (The rMPP screen freezes while
echo  connected; that is normal - xochitl is paused.)
echo.
echo  To STOP: run  stop-inkbridge.cmd   (or close the daemon).
echo ===========================================================
