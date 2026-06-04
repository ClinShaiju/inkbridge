@echo off
REM Stop the Windows side of inkbridge. The rMPP daemon detects the dropped
REM connection and resumes xochitl (unfreezes the reMarkable screen).
taskkill /f /im OpenTabletDriver.Daemon.exe >nul 2>&1
taskkill /f /im OpenTabletDriver.UX.Wpf.exe >nul 2>&1
echo inkbridge stopped. The reMarkable will resume normal use in a moment.
