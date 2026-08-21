# close-to-tray

A small Windows helper for Visual Studio Code.

## Behaviour

- Click VS Code's **X**: hide that VS Code window instead of closing it.
- Double-click the tray icon: restore hidden VS Code windows.
- Right-click the tray icon: **Show VS Code** or **Exit Close to Tray**.
- **File > Exit** and **Alt+F4** are left alone, so they remain deliberate ways to quit VS Code normally.
- While VS Code is hidden, double-clicking a file in Windows Explorer restores VS Code only when Windows currently associates that file type with VS Code.
- Double-clicking folders or files associated with another application does not restore VS Code.

## v0.3.2 design

The active implementation is one small Windows executable with a low-level mouse hook.

1. the X-button click is intercepted only for VS Code windows and the window is hidden;
2. while at least one VS Code window is hidden, two Explorer clicks are compared using Windows' own double-click time and rectangle metrics;
3. after a qualifying Explorer double-click, the helper resolves the selected item for that exact Explorer window;
4. folders, shell-only items and missing/non-file items fail closed;
5. for a real file, Windows is asked for the current associated executable using `AssocQueryString`;
6. the short restore timer starts only when that executable is VS Code;
7. the Explorer click is never swallowed, so Explorer keeps opening the selected item normally.

This fixes the v0.3.1 false-restore behaviour where any Explorer double-click could reveal VS Code.

The selection and association checks run on the UI thread, not inside the low-level hook callback. If the helper cannot safely determine what Explorer is opening, it does nothing and leaves VS Code hidden.

No file-association changes, WMI watcher, administrator rights, background service or third-party utility are required.

## Hook reliability

The mouse hook runs on a dedicated thread with its own Windows message loop. A watchdog detects a lost hook and requests a bounded re-hook. Resume and session-unlock events also refresh the hook.

## DPI handling

`CloseToTray.manifest` declares Per-Monitor DPI v2 awareness. This keeps low-level mouse coordinates and VS Code window rectangles in the same coordinate space on mixed-scaling multi-monitor setups.

## Built-in tests

The executable supports:

```text
CloseToTray.exe --self-test
```

The self-test checks double-click timing/distance rules, close-button geometry including a 150% DPI case, hook-recovery logic, and VS Code executable-name recognition. It does not depend on the user's live file associations.

## Install / repair

Run `Install.cmd`.

It:

1. compiles a staged executable from `CloseToTray.cs`;
2. runs the built-in self-test before touching the running helper;
3. asks the current helper to exit cleanly;
4. keeps the previous executable as `CloseToTray.previous.exe`;
5. installs the validated build;
6. preserves the Celia dashboard scheduled task when that task owns login startup, otherwise uses the current-user Windows Run key;
7. starts the helper and verifies that the startup log reports v0.3.2.

No administrator rights are required.

## Uninstall

Run `Uninstall.cmd`. It removes the legacy Run-key startup entry and asks current and legacy helper processes to exit cleanly. Any windows hidden by the current helper are restored on exit.

## Diagnostics

`close-to-tray.log` records only operational information needed to troubleshoot the helper, including startup, hook installation/recovery, intercepted VS Code close clicks, Explorer eligibility decisions, restore events and fatal errors. It does not record keystrokes, filenames, file contents or general mouse activity.

## Canonical source

GitHub `marianoaran-dev/close-to-tray` `main` is the canonical source.

Active files:

- `CloseToTray.cs`: active implementation.
- `CloseToTray.manifest`: Per-Monitor DPI v2 manifest.
- `Install.cmd`: compile, self-test, install and start.
- `Uninstall.cmd`: stop and remove the legacy Run-key startup entry.
- `.github/workflows/windows-compile.yml`: Windows compile and self-test CI.
- `.gitignore`: excludes generated executables, logs, Inbox packs and one-off local updater artefacts.

The original PowerShell/VBS implementation remains available in Git history and is not part of the active install path.

Version: **0.3.2**
