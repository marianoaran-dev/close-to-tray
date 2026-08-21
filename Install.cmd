@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "APPDIR=%~dp0"
set "SRC=%APPDIR%CloseToTray.cs"
set "MANIFEST=%APPDIR%CloseToTray.manifest"
set "EXE=%APPDIR%CloseToTray.exe"
set "NEWEXE=%APPDIR%CloseToTray.new.exe"
set "BACKUPEXE=%APPDIR%CloseToTray.previous.exe"
set "LOG=%APPDIR%close-to-tray.log"
set "RUNKEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "RUNVALUE=CloseToTray"
set "DASHBOARDTASK=Celia App - Close to Tray"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
  echo ERROR: Windows C# compiler was not found.
  echo.
  pause
  exit /b 1
)

for %%F in ("%SRC%" "%MANIFEST%") do (
  if not exist "%%~F" (
    echo ERROR: Required file is missing: %%~F
    echo.
    pause
    exit /b 1
  )
)

rem Build and test a staged executable first. The currently working helper stays running
rem until the replacement has compiled and passed all built-in tests.
del /q "%NEWEXE%" >nul 2>&1
echo Building staged Close to Tray...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /win32manifest:"%MANIFEST%" /out:"%NEWEXE%" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.dll "%SRC%"
if errorlevel 1 goto :build_failed

echo Running built-in tests on staged build...
"%NEWEXE%" --self-test
if errorlevel 1 (
  del /q "%NEWEXE%" >nul 2>&1
  echo.
  echo ERROR: Close to Tray self-tests failed. The currently installed helper was left untouched.
  echo.
  pause
  exit /b 1
)

rem Only now stop the current helper so the validated staged build can replace it.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { $e=[Threading.EventWaitHandle]::OpenExisting('Local\CloseToTray.V2.Exit'); $null=$e.Set(); $e.Dispose() } catch {}" >nul 2>&1
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { $e=[Threading.EventWaitHandle]::OpenExisting('Local\CloseToTray.VSCode.Exit'); $null=$e.Set(); $e.Dispose() } catch {}" >nul 2>&1
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { $e=[Threading.EventWaitHandle]::OpenExisting('Local\CloseToTray.OpenWatcher.Exit'); $null=$e.Set(); $e.Dispose() } catch {}" >nul 2>&1

powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$p='%EXE%'; $deadline=(Get-Date).AddSeconds(5); do { $running=@(Get-Process -Name CloseToTray -ErrorAction SilentlyContinue | Where-Object { try { [IO.Path]::GetFullPath($_.Path) -eq [IO.Path]::GetFullPath($p) } catch { $false } }); if ($running.Count -eq 0) { exit 0 }; Start-Sleep -Milliseconds 200 } while ((Get-Date) -lt $deadline); exit 1" >nul 2>&1
if errorlevel 1 (
  del /q "%NEWEXE%" >nul 2>&1
  echo.
  echo ERROR: The existing Close to Tray process did not exit cleanly. It was left in place.
  echo.
  pause
  exit /b 1
)

if exist "%EXE%" copy /y "%EXE%" "%BACKUPEXE%" >nul
move /y "%NEWEXE%" "%EXE%" >nul
if errorlevel 1 (
  echo.
  echo ERROR: The validated executable could not replace the installed executable.
  echo The previous executable remains available as CloseToTray.previous.exe.
  echo.
  pause
  exit /b 1
)

del /q "%LOG%" >nul 2>&1

rem Preserve the dashboard-managed scheduled task if it already owns login startup.
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "if (Get-ScheduledTask -TaskName '%DASHBOARDTASK%' -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }" >nul 2>&1
if errorlevel 1 (
  reg add "%RUNKEY%" /v "%RUNVALUE%" /t REG_SZ /d "%EXE%" /f >nul
  if errorlevel 1 (
    echo.
    echo ERROR: Could not add Close to Tray to current-user startup.
    echo.
    pause
    exit /b 1
  )
) else (
  rem Avoid duplicate startup when the Celia dashboard task is enabled.
  reg delete "%RUNKEY%" /v "%RUNVALUE%" /f >nul 2>&1
)

start "" "%EXE%"
timeout /t 2 /nobreak >nul

if not exist "%LOG%" goto :start_failed
findstr /c:"Starting CloseToTray v0.3.2" "%LOG%" >nul
if errorlevel 1 goto :start_failed

echo.
echo Close to Tray v0.3.2 is installed and running.
echo X hides VS Code to the tray.
echo Explorer only restores hidden VS Code when the opened file is currently associated with VS Code.
echo Folder and unrelated-file double-clicks are left alone.
echo The mouse hook runs on a dedicated thread with automatic health recovery.
echo.
timeout /t 3 /nobreak >nul
exit /b 0

:build_failed
del /q "%NEWEXE%" >nul 2>&1
echo.
echo ERROR: Build failed. The currently installed helper was left untouched.
echo.
pause
exit /b 1

:start_failed
echo.
echo ERROR: The validated v0.3.2 executable did not report a successful startup.
echo The previous executable is preserved as CloseToTray.previous.exe.
echo.
pause
exit /b 1
