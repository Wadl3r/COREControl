@echo off
REM Hot-reload build. Builds the mod and copies it into the game's BepInEx\scripts folder, where
REM the ScriptEngine plugin reloads it inside the running game about 3 seconds later (F6 forces it).
REM The release copy in BepInEx\plugins\COREControl is removed so the mod never loads twice.
REM
REM First time only: quit the game before running this, because the release DLL is locked while
REM loaded. After that, run it with the game open and watch the BepInEx console for
REM "Unloading old plugin instances" followed by the mod's "loaded" line.
setlocal
cd /d "%~dp0"
if "%NUCLEAR_OPTION_DIR%"=="" set "NUCLEAR_OPTION_DIR=D:\Steam\steamapps\common\Nuclear Option"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-and-install.ps1" -Dev %*
if errorlevel 1 (
    echo.
    echo BUILD OR INSTALL FAILED. See the messages above.
    echo If the error is about removing plugins\COREControl, the game is holding the release
    echo copy: quit the game once, rerun this, then relaunch.
    pause
    exit /b 1
)
exit /b 0
