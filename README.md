# close-to-tray

A small Windows helper that changes one behaviour in Visual Studio Code: clicking the window **X** hides VS Code to the notification area instead of closing it.

## Behaviour

- Click VS Code's **X**: hide that VS Code window to the system tray.
- Double-click the tray icon: restore hidden VS Code windows.
- Right-click the tray icon: **Show VS Code** or **Exit Close to Tray**.
- **File > Exit** and **Alt+F4** are left alone, so they remain deliberate ways to close VS Code normally.
- Exiting the helper first restores any VS Code windows it has hidden.

The helper only targets `Code.exe` / VS Code Electron top-level windows. It does not change the close behaviour of other applications.

## Install

Run `Install.cmd` once. It:

1. starts the helper invisibly;
2. adds it to the current user's Windows startup using `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

No administrator rights are required.

## Uninstall

Run `Uninstall.cmd`. It removes the startup entry and signals the running helper to exit cleanly. Any hidden VS Code windows are restored first.

## Files

- `CloseToTray.ps1`: helper logic and tray UI. It compiles a small C# WinForms component in memory using Windows PowerShell.
- `Start-CloseToTray.vbs`: launches the PowerShell helper with no console window.
- `Install.cmd`: current-user install/startup setup.
- `Uninstall.cmd`: removes startup and exits the helper.

## How it works

The helper installs a Windows low-level mouse hook. When the left mouse button is pressed over the close button of a top-level VS Code window, the click is swallowed and the window is hidden with the Win32 `ShowWindow` API. The window remains alive, with its open editors and Markdown previews intact.

Close-button detection first uses `WM_NCHITTEST` and falls back to a DPI-aware top-right caption-button region for VS Code/Electron title bars.

## Scope

Initial target: Windows 11 with current Visual Studio Code. VS Code does not currently provide native close-to-tray behaviour; the long-standing upstream request remains out of scope: microsoft/vscode#11723.

Version: **0.1.0**
