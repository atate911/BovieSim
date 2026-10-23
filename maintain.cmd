@echo off
rem Bovie Sim maintenance: "maintain help" lists the commands. Double-click for a menu.
setlocal
if "%~1"=="" goto menu
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\maintain.ps1" %*
exit /b %ERRORLEVEL%
:menu
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\maintain.ps1" menu
set RC=%ERRORLEVEL%
echo.
pause
exit /b %RC%
