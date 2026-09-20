@echo off
setlocal

set "MSI_FIX_EXE=%~dp0..\dist\MSIThrottleFix.exe"

if not exist "%MSI_FIX_EXE%" (
    echo ERROR: MSIThrottleFix.exe was not found at:
    echo %MSI_FIX_EXE%
    echo Build or publish the project first.
    pause
    exit /b 2
)

fltmc >nul 2>&1
if errorlevel 1 (
    echo ERROR: Administrator privileges are required.
    echo Right-click run_workaround.bat and select "Run as administrator".
    pause
    exit /b 1
)

"%MSI_FIX_EXE%" cycle --balanced-ms 750 --extreme-ms 4250 --tray
set "MSI_FIX_EXIT=%ERRORLEVEL%"

if not "%MSI_FIX_EXIT%"=="0" (
    echo.
    echo MSI Throttle Fix exited with code %MSI_FIX_EXIT%.
    pause
)

exit /b %MSI_FIX_EXIT%
