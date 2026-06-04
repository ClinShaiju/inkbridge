@echo off
REM Stop the Windows side of inkbridge. The rMPP daemon detects the dropped
REM connection and releases its wakelock, letting the device sleep normally
REM again (xochitl was never paused, so there is nothing to "resume").
taskkill /f /im OpenTabletDriver.Daemon.exe >nul 2>&1
taskkill /f /im OpenTabletDriver.UX.Wpf.exe >nul 2>&1
echo inkbridge stopped. The reMarkable can sleep normally again.
