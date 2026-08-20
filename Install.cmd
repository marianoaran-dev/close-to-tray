@echo off
setlocal
set "APPDIR=%~dp0"
set "RUNKEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "RUNVALUE=CloseToTray"
set "LAUNCHER=%APPDIR%Start-CloseToTray.vbs"

reg add "%RUNKEY%" /v "%RUNVALUE%" /t REG_SZ /d "wscript.exe \"%LAUNCHER%\"" /f >nul
if errorlevel 1 (
  echo Failed to add Close to Tray to Windows startup.
  exit /b 1
)

start "" wscript.exe "%LAUNCHER%"
echo Close to Tray installed and started.
echo Visual Studio Code's X button will now hide VS Code to the system tray.
timeout /t 2 /nobreak >nul
endlocal
