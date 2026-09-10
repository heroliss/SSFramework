@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-SSFramework.ps1" -Interactive
set "SSFRAMEWORK_SETUP_EXIT=%ERRORLEVEL%"
echo.
pause
exit /b %SSFRAMEWORK_SETUP_EXIT%
