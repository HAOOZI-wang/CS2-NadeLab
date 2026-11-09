@echo off
cd /d "%~dp0"
python scripts\local-server.py start
if errorlevel 1 goto failed
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\start-input.ps1
if errorlevel 1 goto failed
echo.
echo Server is READY. Enter this in the CS2 console:
echo Use the connect command printed above. Personal bindings are unchanged.
echo F1 menu: wheel or arrows select, WASD move, E confirm, R back, V fly.
pause
exit /b 0
:failed
echo.
echo Server is NOT ready. Check server\logs before connecting.
pause
exit /b 1
