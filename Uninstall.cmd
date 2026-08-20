@echo off
setlocal
set "RUNKEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "RUNVALUE=CloseToTray"

reg delete "%RUNKEY%" /v "%RUNVALUE%" /f >nul 2>&1

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { $e=[System.Threading.EventWaitHandle]::OpenExisting('Local\CloseToTray.VSCode.Exit'); [void]$e.Set(); $e.Dispose() } catch {}"

echo Close to Tray removed from Windows startup and the running helper was asked to exit.
echo Any hidden VS Code windows will be restored before the helper closes.
timeout /t 2 /nobreak >nul
endlocal
