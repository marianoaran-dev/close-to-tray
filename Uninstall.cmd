@echo off
setlocal EnableExtensions

set "RUNKEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "RUNVALUE=CloseToTray"

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { $e=[Threading.EventWaitHandle]::OpenExisting('Local\CloseToTray.V2.Exit'); $null=$e.Set(); $e.Dispose() } catch {}" >nul 2>&1
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { $e=[Threading.EventWaitHandle]::OpenExisting('Local\CloseToTray.VSCode.Exit'); $null=$e.Set(); $e.Dispose() } catch {}" >nul 2>&1
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { $e=[Threading.EventWaitHandle]::OpenExisting('Local\CloseToTray.OpenWatcher.Exit'); $null=$e.Set(); $e.Dispose() } catch {}" >nul 2>&1

reg delete "%RUNKEY%" /v "%RUNVALUE%" /f >nul 2>&1

echo Close to Tray has been removed from the legacy Windows Run startup entry and stopped.
timeout /t 2 /nobreak >nul
endlocal
