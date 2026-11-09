@echo off
setlocal
cd /d "%~dp0"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\release\NadeLab.ps1" %*
if errorlevel 1 (
  echo.
  echo NadeLab could not complete this action. Read the message above.
  pause
  exit /b 1
)
exit /b 0
