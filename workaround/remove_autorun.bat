@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0remove_autorun.ps1"
set "REMOVE_EXIT=%ERRORLEVEL%"
if not "%REMOVE_EXIT%"=="0" pause
exit /b %REMOVE_EXIT%
