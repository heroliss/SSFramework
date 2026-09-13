@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Check-SSFrameworkProject.ps1" -Interactive %*
set "SSFRAMEWORK_CHECK_EXIT=%ERRORLEVEL%"
echo.
pause
exit /b %SSFRAMEWORK_CHECK_EXIT%
