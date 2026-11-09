@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\workspace.ps1" %*
set "NADELAB_EXIT_CODE=%ERRORLEVEL%"
if not "%NADELAB_EXIT_CODE%"=="0" pause
exit /b %NADELAB_EXIT_CODE%
