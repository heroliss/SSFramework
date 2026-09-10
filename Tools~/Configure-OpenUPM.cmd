@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Configure-OpenUPM.ps1" -Interactive
echo.
pause
