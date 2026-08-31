@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0_INSTALL_CHASTER_ADDON.ps1"
if errorlevel 1 (
  echo.
  echo INSTALL FAILED. No need to guess: read the error above.
  pause
  exit /b 1
)
echo.
pause
