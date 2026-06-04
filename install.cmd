@echo off
setlocal EnableDelayedExpansion
REM ===========================================================================
REM  inkbridge - Windows-side one-shot setup
REM
REM  Installs the OpenTabletDriver plugin + tablet config, finds your
REM  OpenTabletDriver folder (extract-and-run, so it varies) and remembers it,
REM  and optionally pushes the daemon to the reMarkable over SSH.
REM
REM  Works from a checked-out repo OR the flattened release zip - each file is
REM  resolved from a list of candidate locations.
REM
REM  Just double-click it, or run:  install.cmd          (plugin only)
REM                                 install.cmd /daemon  (also install daemon)
REM ===========================================================================
set "ROOT=%~dp0"
set "DEVHOST=10.11.99.1"
set "DEVUSER=root"
set "DODAEMON="
if /i "%~1"=="/daemon" set "DODAEMON=1"

echo inkbridge - Windows setup
echo.

REM --- 1. OpenTabletDriver plugin + tablet config (installs under %LOCALAPPDATA%) ---
echo ==^> Installing the OpenTabletDriver plugin
call :find DLL "Inkbridge.dll" "otd-plugin\bin\Release\Inkbridge.dll"
call :find CFG "tablet-spec.json" "otd-plugin\tablet-spec.json"
if not defined DLL (
  echo     [error] Inkbridge.dll not found - build it, or use the release zip.
  goto :fail
)
if not defined CFG (
  echo     [error] tablet-spec.json not found.
  goto :fail
)

set "OTDDATA=%LOCALAPPDATA%\OpenTabletDriver"
if not exist "%OTDDATA%\Plugins\Inkbridge" mkdir "%OTDDATA%\Plugins\Inkbridge" >nul 2>&1
if not exist "%OTDDATA%\Configurations" mkdir "%OTDDATA%\Configurations" >nul 2>&1
copy /y "%DLL%" "%OTDDATA%\Plugins\Inkbridge\Inkbridge.dll" >nul
copy /y "%CFG%" "%OTDDATA%\Configurations\inkbridge.json" >nul
echo     plugin  -^> %OTDDATA%\Plugins\Inkbridge
echo     config  -^> %OTDDATA%\Configurations\inkbridge.json

REM --- 2. Locate OpenTabletDriver and remember it (OTD_DIR user env var) ---
echo.
echo ==^> Locating OpenTabletDriver
set "FOUND="
if defined OTD_DIR if exist "%OTD_DIR%\OpenTabletDriver.Daemon.exe" set "FOUND=%OTD_DIR%"
if not defined FOUND for /f "delims=" %%P in ('where OpenTabletDriver.Daemon.exe 2^>nul') do if not defined FOUND set "FOUND=%%~dpP"
if defined FOUND if "!FOUND:~-1!"=="\" set "FOUND=!FOUND:~0,-1!"
if not defined FOUND for %%D in (
  "%USERPROFILE%\OpenTabletDriver"
  "%USERPROFILE%\Downloads\OpenTabletDriver"
  "%USERPROFILE%\Desktop\OpenTabletDriver"
  "C:\OpenTabletDriver"
  "%ProgramFiles%\OpenTabletDriver"
) do if not defined FOUND if exist "%%~D\OpenTabletDriver.Daemon.exe" set "FOUND=%%~D"
if not defined FOUND (
  echo     Couldn't auto-detect OpenTabletDriver.
  set /p "FOUND=    Enter the folder with OpenTabletDriver.Daemon.exe (blank to skip): "
)
if defined FOUND if not exist "!FOUND!\OpenTabletDriver.Daemon.exe" set "FOUND="

if defined FOUND (
  setx OTD_DIR "!FOUND!" >nul
  set "OTD_DIR=!FOUND!"
  echo     OpenTabletDriver: !FOUND!
  echo     saved OTD_DIR so start-inkbridge.cmd can find it
) else (
  echo     [warn] OTD not located. Set it later with:
  echo            setx OTD_DIR "C:\path\to\OpenTabletDriver"
)

REM --- 3. (optional) install the daemon on the tablet over SSH ---
echo.
if not defined DODAEMON (
  set /p "ans=Install the daemon on the tablet over SSH now? (y/N): "
  if /i "!ans!"=="y" set "DODAEMON=1"
)
if defined DODAEMON (
  echo ==^> Installing the daemon on the reMarkable ^(%DEVUSER%@%DEVHOST%^)
  where ssh >nul 2>&1
  if errorlevel 1 (
    echo     [error] OpenSSH client not found. Enable it via Settings ^> Optional features ^> OpenSSH Client,
    echo             or run daemon\deploy.py instead ^(reads .env^).
  ) else (
    call :find BIN  "inkbridge-daemon" "daemon\target\aarch64-unknown-linux-musl\release\inkbridge-daemon"
    call :find UNIT "inkbridge-daemon.service" "daemon\inkbridge-daemon.service"
    call :find SVC  "install-service.sh" "daemon\install-service.sh"
    if defined BIN if defined UNIT if defined SVC (
      echo     You'll be prompted for the device root password unless you use an SSH key.
      ssh %DEVUSER%@%DEVHOST% "mkdir -p /home/root/inkbridge"
      scp "!BIN!" "!UNIT!" "!SVC!" %DEVUSER%@%DEVHOST%:/home/root/inkbridge/
      ssh %DEVUSER%@%DEVHOST% "chmod +x /home/root/inkbridge/inkbridge-daemon /home/root/inkbridge/install-service.sh && sh /home/root/inkbridge/install-service.sh"
      echo     daemon installed as a systemd service ^(:9292 pen, :9293 control^)
    ) else (
      echo     [error] daemon files missing from this package - skipping.
    )
  )
) else (
  echo ==^> Daemon ^(tablet side^) - skipped
  echo     Re-run with  install.cmd /daemon  to push it, or use daemon\deploy.py ^(reads .env^).
)

REM --- Next steps that need you (driver + GUI) ---
echo.
echo ==^> Almost there - two manual pieces enable pressure on Windows:
echo.
echo     1^) VMulti driver : install the X9VoiD fork
echo                        https://github.com/X9VoiD/vmulti-bin/releases/latest
echo     2^) Windows Ink   : in OpenTabletDriver's Plugin Manager, install "VoiDPlugins / Windows Ink"
echo.
echo     Then in OpenTabletDriver:
echo       - Enable the 'inkbridge' tool ^(Inkbridge.InkbridgeTool^) and APPLY SETTINGS TWICE.
echo       - Output mode -^> Windows Ink Absolute Mode; bind the pen tip to the Windows Ink 'Pen Tip'.
echo.
echo     Finally, run:  start-inkbridge.cmd
echo.
set /p "openvm=Open the VMulti download page now? (y/N): "
if /i "!openvm!"=="y" start "" "https://github.com/X9VoiD/vmulti-bin/releases/latest"
echo.
echo Done.
pause
exit /b 0

:fail
echo.
echo Setup did not complete. See the error above.
pause
exit /b 1

REM --- :find VARNAME candidate1 [candidate2 ...] -> first existing (ROOT-relative) path ---
:find
set "_n=%~1"
set "%_n%="
shift
:find_loop
if "%~1"=="" exit /b 0
if exist "%ROOT%%~1" (
  set "%_n%=%ROOT%%~1"
  exit /b 0
)
shift
goto :find_loop
