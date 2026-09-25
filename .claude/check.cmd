@echo off
REM Project check: compile the CORE Control mod. Build errors and warnings are the only
REM automated gate this repo has (its self-checks run inside the game at plugin load).
setlocal
if "%NUCLEAR_OPTION_DIR%"=="" set "NUCLEAR_OPTION_DIR=D:\Steam\steamapps\common\Nuclear Option"
dotnet build "%~dp0..\COREControl.csproj" -c Release -nologo -v q
exit /b %ERRORLEVEL%
