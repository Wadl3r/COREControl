@echo off
REM Release build. Double-click wrapper for build-and-install.ps1 (normal plugins layout).
REM Builds the mod, installs the whole output folder into BepInEx\plugins\COREControl, removes any
REM hot-reload copy from BepInEx\scripts, then offers to launch the game. Quit the game first.
REM For the no-restart development loop use build-dev.bat instead.
setlocal
cd /d "%~dp0"
if "%NUCLEAR_OPTION_DIR%"=="" set "NUCLEAR_OPTION_DIR=D:\Steam\steamapps\common\Nuclear Option"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-and-install.ps1" %*
if errorlevel 1 (
    echo.
    echo BUILD OR INSTALL FAILED. See the messages above.
    pause
    exit /b 1
)

echo.
choice /C YN /T 10 /D Y /M "Launch Nuclear Option now (auto-yes in 10s)"
if errorlevel 2 exit /b 0
start "" "steam://rungameid/2168680"
exit /b 0
