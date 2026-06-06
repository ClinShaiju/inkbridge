@echo off
setlocal EnableDelayedExpansion
REM ===========================================================================
REM  Package the Windows release zip: dist\inkbridge-windows.zip
REM  Requires the prebuilt binaries to exist:
REM    - otd-plugin\bin\Release\Inkbridge.dll        (dotnet build otd-plugin -c Release)
REM    - daemon\target\aarch64-unknown-linux-musl\release\inkbridge-daemon
REM      (cargo build --release --target aarch64-unknown-linux-musl --manifest-path daemon\Cargo.toml)
REM ===========================================================================
set "ROOT=%~dp0"
set "STAGE=%ROOT%dist\inkbridge-windows"
set "OUT=%ROOT%dist\inkbridge-windows.zip"
set "DAEMONBIN=%ROOT%daemon\target\aarch64-unknown-linux-musl\release\inkbridge-daemon"
set "DLL=%ROOT%otd-plugin\bin\Release\Inkbridge.dll"

if not exist "%DLL%" echo [error] missing %DLL% - run: dotnet build otd-plugin -c Release & exit /b 1
if not exist "%DAEMONBIN%" echo [error] missing %DAEMONBIN% - cross-compile the daemon first & exit /b 1

echo Staging into %STAGE% ...
if exist "%ROOT%dist" rmdir /s /q "%ROOT%dist"
mkdir "%STAGE%\appload\backend"

REM --- Windows-side scripts + config ---
copy /y "%ROOT%install.cmd"                     "%STAGE%\" >nul
copy /y "%ROOT%.env.example"                    "%STAGE%\" >nul
copy /y "%DLL%"                                 "%STAGE%\" >nul
copy /y "%ROOT%otd-plugin\tablet-spec.json"     "%STAGE%\" >nul

REM --- tablet-side daemon ---
copy /y "%DAEMONBIN%"                           "%STAGE%\" >nul
copy /y "%ROOT%daemon\inkbridge-daemon.service" "%STAGE%\" >nul
copy /y "%ROOT%daemon\install-service.sh"       "%STAGE%\" >nul

REM --- optional on-device visualizer ---
copy /y "%ROOT%appload\manifest.json"           "%STAGE%\appload\" >nul
copy /y "%ROOT%appload\resources.rcc"           "%STAGE%\appload\" >nul
copy /y "%ROOT%appload\area.json"               "%STAGE%\appload\" >nul
copy /y "%ROOT%appload\icon.png"                "%STAGE%\appload\" >nul
copy /y "%ROOT%appload\deploy.py"               "%STAGE%\appload\" >nul
copy /y "%ROOT%appload\backend\entry"           "%STAGE%\appload\backend\" >nul

copy /y "%ROOT%RELEASE.txt"                      "%STAGE%\README.txt" >nul 2>&1

echo Zipping to %OUT% ...
powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%OUT%' -Force"
if errorlevel 1 echo [error] zip failed & exit /b 1
echo Done: %OUT%
endlocal
