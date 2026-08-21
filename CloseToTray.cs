using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CloseToTray
{
    internal static class Program
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_QUIT = 0x0012;
        private const int WM_APP_REHOOK = 0x8000 + 41;
        private const int PM_NOREMOVE = 0x0000;
        private const int GA_ROOT = 2;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SM_CXDOUBLECLK = 36;
        private const int SM_CYDOUBLECLK = 37;
        private const int ASSOCSTR_EXECUTABLE = 2;

        private static readonly HashSet<IntPtr> HiddenWindows = new HashSet<IntPtr>();
        private static readonly LowLevelMouseProc MouseProc = HookCallback;
        private static readonly object LogLock = new object();
        private static readonly object HookLifecycleLock = new object();

        private static IntPtr _mouseHook = IntPtr.Zero;
        private static Thread _hookThread;
        private static uint _hookThreadId;
        private static ManualResetEvent _hookThreadReady;
        private static int _hookThreadStartError;
        private static long _hookHeartbeat;

        private static NotifyIcon _tray;
        private static Control _dispatcher;
        private static Mutex _mutex;
        private static EventWaitHandle _exitEvent;
        private static System.Windows.Forms.Timer _exitTimer;
        private static System.Windows.Forms.Timer _restoreTimer;
        private static System.Windows.Forms.Timer _watchdogTimer;
        private static System.Windows.Forms.Timer _snapshotTimer;

        private static int _swallowNextLeftUp;
        private static int _hiddenWindowCount;
        private static bool _exiting;
        private static bool _systemEventsSubscribed;
        private static string _logPath;

        private static volatile IntPtr[] _vsCodeWindowSnapshot = new IntPtr[0];
        private static volatile int[] _explorerPidSnapshot = new int[0];

        private static bool _haveExplorerClick;
        private static IntPtr _lastExplorerRoot = IntPtr.Zero;
        private static POINT _lastExplorerPoint;
        private static uint _lastExplorerClickTime;

        private static bool _haveWatchdogCursor;
        private static POINT _lastWatchdogCursor;
        private static long _lastWatchdogHeartbeat;
        private static DateTime _nextRecoveryAllowed = DateTime.MinValue;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args != null && args.Length == 1 && String.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                return RunSelfTests() ? 0 : 1;
            }

            _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "close-to-tray.log");

            try
            {
                Log("Starting CloseToTray v0.3.2");

                bool createdNew;
                _mutex = new Mutex(true, "Local\\CloseToTray.V2", out createdNew);
                if (!createdNew)
                {
                    Log("Another CloseToTray instance is already running; exiting this copy.");
                    _mutex.Dispose();
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                SetupDispatcher();
                SetupTray();
                RefreshProcessSnapshots();
                SetupTimersAndSignals();
                SubscribeSystemEvents();
                StartHookThread(true);

                Application.Run();
                return 0;
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex.ToString());
                MessageBox.Show(
                    "Close to Tray could not start.\r\n\r\n" + ex.Message + "\r\n\r\nSee close-to-tray.log in the application folder.",
                    "Close to Tray",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                Cleanup();
                Log("CloseToTray stopped.");
            }
        }

        private static bool RunSelfTests()
        {
            IntPtr windowA = new IntPtr(1);
            IntPtr windowB = new IntPtr(2);
            POINT p1 = new POINT { X = 100, Y = 100 };
            POINT p2 = new POINT { X = 102, Y = 101 };
            POINT far = new POINT { X = 140, Y = 140 };

            if (!IsDoubleClickCandidate(windowA, p1, 1000, windowA, p2, 1200, 500, 8, 8)) return false;
            if (IsDoubleClickCandidate(windowA, p1, 1000, windowA, p2, 1600, 500, 8, 8)) return false;
            if (IsDoubleClickCandidate(windowA, p1, 1000, windowA, far, 1200, 500, 8, 8)) return false;
            if (IsDoubleClickCandidate(windowA, p1, 1000, windowB, p2, 1200, 500, 8, 8)) return false;

            RECT rect = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            if (!PointInCloseButtonArea(rect, new POINT { X = 1900, Y = 20 }, 96)) return false;
            if (PointInCloseButtonArea(rect, new POINT { X = 1700, Y = 20 }, 96)) return false;

            RECT scaledRect = new RECT { Left = 3000, Top = 100, Right = 6660, Bottom = 2200 };
            if (!PointInCloseButtonArea(scaledRect, new POINT { X = 6630, Y = 140 }, 144)) return false;

            if (!ShouldRecoverHook(true, 10, 10)) return false;
            if (ShouldRecoverHook(false, 10, 10)) return false;
            if (ShouldRecoverHook(true, 11, 10)) return false;

            if (!IsVsCodeExecutablePath(@"C:\Users\example\AppData\Local\Programs\Microsoft VS Code\Code.exe")) return false;
            if (!IsVsCodeExecutablePath(@"C:\Users\example\AppData\Local\Programs\Microsoft VS Code Insiders\Code - Insiders.exe")) return false;
            if (IsVsCodeExecutablePath(@"C:\Windows\System32\notepad.exe")) return false;
            if (IsVsCodeExecutablePath("")) return false;

            return true;
        }

        private static void SetupDispatcher()
        {
            _dispatcher = new Control();
            IntPtr ignored = _dispatcher.Handle;
        }

        private static void SetupTray()
        {
            _tray = new NotifyIcon();
            _tray.Text = "VS Code Close to Tray";
            _tray.Icon = GetTrayIcon();
            _tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem show = new ToolStripMenuItem("Show VS Code");
            show.Click += delegate { ShowHiddenWindows(); };
            menu.Items.Add(show);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("Exit Close to Tray");
            exit.Click += delegate { ExitHelper(); };
            menu.Items.Add(exit);

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowHiddenWindows(); };
        }

        private static void SetupTimersAndSignals()
        {
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CloseToTray.V2.Exit");

            _exitTimer = new System.Windows.Forms.Timer();
            _exitTimer.Interval = 300;
            _exitTimer.Tick += delegate
            {
                if (_exitEvent.WaitOne(0))
                {
                    Log("Exit signal received.");
                    ExitHelper();
                }
            };
            _exitTimer.Start();

            _restoreTimer = new System.Windows.Forms.Timer();
            _restoreTimer.Interval = 180;
            _restoreTimer.Tick += delegate
            {
                _restoreTimer.Stop();
                if (HiddenWindows.Count > 0)
                {
                    Log("Restoring VS Code after eligible Explorer double-click.");
                    ShowHiddenWindows();
                }
            };

            _snapshotTimer = new System.Windows.Forms.Timer();
            _snapshotTimer.Interval = 2000;
            _snapshotTimer.Tick += delegate { RefreshProcessSnapshots(); };
            _snapshotTimer.Start();

            _watchdogTimer = new System.Windows.Forms.Timer();
            _watchdogTimer.Interval = 500;
            _watchdogTimer.Tick += delegate { CheckHookHealth(); };
            _watchdogTimer.Start();
        }

        private static void SubscribeSystemEvents()
        {
            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                SystemEvents.SessionSwitch += OnSessionSwitch;
                _systemEventsSubscribed = true;
            }
            catch (Exception ex)
            {
                Log("System resume/unlock monitoring unavailable: " + ex.Message);
            }
        }

        private static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                QueueUi(delegate
                {
                    RefreshProcessSnapshots();
                    RequestHookRefresh("system resume");
                });
            }
        }

        private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                QueueUi(delegate
                {
                    RefreshProcessSnapshots();
                    RequestHookRefresh("session unlock");
                });
            }
        }

        private static void StartHookThread(bool throwOnFailure)
        {
            lock (HookLifecycleLock)
            {
                if (_hookThread != null && _hookThread.IsAlive) return;

                _hookThreadReady = new ManualResetEvent(false);
                _hookThreadStartError = 0;

                _hookThread = new Thread(HookThreadMain);
                _hookThread.IsBackground = true;
                _hookThread.Name = "CloseToTray Mouse Hook";
                _hookThread.Start();
            }

            if (!_hookThreadReady.WaitOne(5000))
            {
                if (throwOnFailure) throw new InvalidOperationException("Mouse hook thread did not become ready.");
                Log("Mouse hook recovery thread did not become ready.");
                return;
            }

            if (_hookThreadStartError != 0)
            {
                if (throwOnFailure)
                {
                    throw new Win32Exception(_hookThreadStartError, "Could not install the Windows mouse hook.");
                }

                Log("Mouse hook recovery failed with Windows error " + _hookThreadStartError.ToString() + ".");
                return;
            }

            Log("Mouse hook thread ready.");
        }

        private static void HookThreadMain()
        {
            _hookThreadId = GetCurrentThreadId();

            MSG seed;
            PeekMessage(out seed, IntPtr.Zero, 0, 0, PM_NOREMOVE);

            if (!InstallMouseHookOnHookThread())
            {
                _hookThreadStartError = Marshal.GetLastWin32Error();
                _hookThreadReady.Set();
                _hookThreadId = 0;
                return;
            }

            _hookThreadReady.Set();
            QueueLog("Mouse hook installed successfully on dedicated thread: " + _mouseHook.ToString());

            try
            {
                MSG message;
                while (true)
                {
                    int result = GetMessage(out message, IntPtr.Zero, 0, 0);
                    if (result <= 0) break;

                    if (message.message == WM_APP_REHOOK)
                    {
                        ReinstallMouseHookOnHookThread();
                        continue;
                    }

                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
            finally
            {
                if (_mouseHook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_mouseHook);
                    _mouseHook = IntPtr.Zero;
                }
                _hookThreadId = 0;
            }
        }

        private static bool InstallMouseHookOnHookThread()
        {
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, MouseProc, GetModuleHandle(null), 0);
            return _mouseHook != IntPtr.Zero;
        }

        private static void ReinstallMouseHookOnHookThread()
        {
            IntPtr oldHook = _mouseHook;
            _mouseHook = IntPtr.Zero;

            if (oldHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(oldHook);
            }

            if (InstallMouseHookOnHookThread())
            {
                Interlocked.Increment(ref _hookHeartbeat);
                QueueLog("Mouse hook refreshed successfully: " + _mouseHook.ToString());
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                QueueLog("Mouse hook refresh failed with Windows error " + error.ToString() + ".");
            }
        }

        private static void RequestHookRefresh(string reason)
        {
            if (_exiting) return;

            Thread thread = _hookThread;
            if (thread == null || !thread.IsAlive || _hookThreadId == 0)
            {
                Log("Mouse hook thread was not alive; restarting it. Reason: " + reason + ".");
                StartHookThread(false);
                return;
            }

            if (DateTime.UtcNow < _nextRecoveryAllowed) return;
            _nextRecoveryAllowed = DateTime.UtcNow.AddSeconds(1);

            Log("Refreshing mouse hook. Reason: " + reason + ".");
            if (!PostThreadMessage(_hookThreadId, WM_APP_REHOOK, UIntPtr.Zero, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                Log("Could not request mouse hook refresh; Windows error " + error.ToString() + ".");
            }
        }

        private static void CheckHookHealth()
        {
            if (_exiting) return;

            Thread thread = _hookThread;
            if (thread == null || !thread.IsAlive)
            {
                RequestHookRefresh("hook thread stopped");
                return;
            }

            POINT cursor;
            if (!GetCursorPos(out cursor)) return;

            long heartbeat = Interlocked.Read(ref _hookHeartbeat);

            if (_haveWatchdogCursor)
            {
                bool cursorMoved = cursor.X != _lastWatchdogCursor.X || cursor.Y != _lastWatchdogCursor.Y;
                if (ShouldRecoverHook(cursorMoved, heartbeat, _lastWatchdogHeartbeat))
                {
                    RequestHookRefresh("cursor moved without a hook heartbeat");
                }
            }

            _lastWatchdogCursor = cursor;
            _lastWatchdogHeartbeat = heartbeat;
            _haveWatchdogCursor = true;
        }

        private static bool ShouldRecoverHook(bool cursorMoved, long heartbeat, long previousHeartbeat)
        {
            return cursorMoved && heartbeat == previousHeartbeat;
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                Interlocked.Increment(ref _hookHeartbeat);
            }

            if (!_exiting && nCode >= 0)
            {
                try
                {
                    if (wParam == (IntPtr)WM_LBUTTONDOWN)
                    {
                        MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));

                        IntPtr clickedRoot = GetAncestor(WindowFromPoint(data.pt), GA_ROOT);
                        IntPtr target = clickedRoot;

                        if (!IsCachedVsCodeWindow(target))
                        {
                            target = GetAncestor(GetForegroundWindow(), GA_ROOT);
                        }

                        if (target != IntPtr.Zero && IsCachedVsCodeWindow(target) && IsCloseButtonArea(target, data.pt))
                        {
                            Interlocked.Exchange(ref _swallowNextLeftUp, 1);
                            QueueHide(target, data.pt);
                            return (IntPtr)1;
                        }

                        if (Interlocked.CompareExchange(ref _hiddenWindowCount, 0, 0) > 0 && IsCachedExplorerWindow(clickedRoot))
                        {
                            QueueExplorerClick(clickedRoot, data.pt, data.time);
                        }
                    }
                    else if (wParam == (IntPtr)WM_LBUTTONUP && Interlocked.Exchange(ref _swallowNextLeftUp, 0) == 1)
                    {
                        return (IntPtr)1;
                    }
                }
                catch
                {
                    // The low-level hook must return immediately.
                }
            }

            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private static void QueueHide(IntPtr target, POINT point)
        {
            QueueUi(delegate
            {
                if (_exiting || target == IntPtr.Zero || !IsWindow(target)) return;

                Log("Intercepted VS Code close click. hwnd=" + target.ToString() + ", point=" + point.X.ToString() + "," + point.Y.ToString());
                ShowWindow(target, SW_HIDE);
                HiddenWindows.Add(target);
                Interlocked.Exchange(ref _hiddenWindowCount, HiddenWindows.Count);
                ResetExplorerClick();
            });
        }

        private static void QueueExplorerClick(IntPtr root, POINT point, uint time)
        {
            QueueUi(delegate { HandleExplorerClick(root, point, time); });
        }

        private static void QueueLog(string message)
        {
            QueueUi(delegate { Log(message); });
        }

        private static void QueueUi(MethodInvoker action)
        {
            Control dispatcher = _dispatcher;
            if (dispatcher == null || dispatcher.IsDisposed || !dispatcher.IsHandleCreated) return;

            try
            {
                dispatcher.BeginInvoke(action);
            }
            catch
            {
            }
        }

        private static void HandleExplorerClick(IntPtr root, POINT point, uint time)
        {
            if (HiddenWindows.Count == 0)
            {
                Interlocked.Exchange(ref _hiddenWindowCount, 0);
                ResetExplorerClick();
                return;
            }

            if (!IsCachedExplorerWindow(root))
            {
                ResetExplorerClick();
                return;
            }

            int maxTime = unchecked((int)GetDoubleClickTime());
            int width = Math.Max(2, GetSystemMetrics(SM_CXDOUBLECLK));
            int height = Math.Max(2, GetSystemMetrics(SM_CYDOUBLECLK));

            if (_haveExplorerClick && IsDoubleClickCandidate(
                _lastExplorerRoot,
                _lastExplorerPoint,
                _lastExplorerClickTime,
                root,
                point,
                time,
                maxTime,
                width,
                height))
            {
                ResetExplorerClick();

                string reason;
                if (!ShouldRestoreForExplorerDoubleClick(root, out reason))
                {
                    Log("Explorer double-click ignored while VS Code is hidden: " + reason + ".");
                    return;
                }

                Log("Eligible VS Code-associated Explorer file double-click detected.");
                _restoreTimer.Stop();
                _restoreTimer.Start();
                return;
            }

            _haveExplorerClick = true;
            _lastExplorerRoot = root;
            _lastExplorerPoint = point;
            _lastExplorerClickTime = time;
        }

        private static bool ShouldRestoreForExplorerDoubleClick(IntPtr explorerRoot, out string reason)
        {
            reason = "eligibility could not be determined";

            string selectedPath;
            if (!TryGetExplorerSelectedPath(explorerRoot, out selectedPath))
            {
                reason = "Explorer selection could not be resolved";
                return false;
            }

            if (Directory.Exists(selectedPath))
            {
                reason = "selected item is a folder";
                return false;
            }

            if (!File.Exists(selectedPath))
            {
                reason = "selected item is not a regular file";
                return false;
            }

            string associatedExecutable;
            if (!TryGetAssociatedExecutable(selectedPath, out associatedExecutable))
            {
                reason = "default file association could not be resolved";
                return false;
            }

            if (!IsVsCodeExecutablePath(associatedExecutable))
            {
                reason = "selected file is not associated with VS Code";
                return false;
            }

            reason = "selected file is associated with VS Code";
            return true;
        }

        private static bool TryGetExplorerSelectedPath(IntPtr explorerRoot, out string selectedPath)
        {
            selectedPath = null;

            object shell = null;
            object windows = null;
            object matchedWindow = null;
            object document = null;
            object selectedItems = null;
            object selectedItem = null;

            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return false;

                shell = Activator.CreateInstance(shellType);
                if (shell == null) return false;

                windows = InvokeComMethod(shell, "Windows", null);
                if (windows == null) return false;

                int count = Convert.ToInt32(GetComProperty(windows, "Count"));
                for (int i = 0; i < count; i++)
                {
                    object candidate = null;
                    try
                    {
                        candidate = InvokeComMethod(windows, "Item", new object[] { i });
                        if (candidate == null) continue;

                        object hwndValue = GetComProperty(candidate, "HWND");
                        IntPtr candidateHwnd = new IntPtr(Convert.ToInt64(hwndValue));
                        if (candidateHwnd == explorerRoot)
                        {
                            matchedWindow = candidate;
                            candidate = null;
                            break;
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        ReleaseComObject(candidate);
                    }
                }

                if (matchedWindow == null) return false;

                document = GetComProperty(matchedWindow, "Document");
                if (document == null) return false;

                selectedItems = InvokeComMethod(document, "SelectedItems", null);
                if (selectedItems == null) return false;

                int selectedCount = Convert.ToInt32(GetComProperty(selectedItems, "Count"));
                if (selectedCount != 1) return false;

                selectedItem = InvokeComMethod(selectedItems, "Item", new object[] { 0 });
                if (selectedItem == null) return false;

                object pathValue = GetComProperty(selectedItem, "Path");
                selectedPath = Convert.ToString(pathValue);
                return !String.IsNullOrWhiteSpace(selectedPath);
            }
            catch
            {
                selectedPath = null;
                return false;
            }
            finally
            {
                ReleaseComObject(selectedItem);
                ReleaseComObject(selectedItems);
                ReleaseComObject(document);
                ReleaseComObject(matchedWindow);
                ReleaseComObject(windows);
                ReleaseComObject(shell);
            }
        }

        private static object GetComProperty(object target, string propertyName)
        {
            return target.GetType().InvokeMember(
                propertyName,
                BindingFlags.GetProperty,
                null,
                target,
                null);
        }

        private static object InvokeComMethod(object target, string methodName, object[] args)
        {
            return target.GetType().InvokeMember(
                methodName,
                BindingFlags.InvokeMethod,
                null,
                target,
                args);
        }

        private static void ReleaseComObject(object value)
        {
            if (value == null) return;

            try
            {
                if (Marshal.IsComObject(value))
                {
                    Marshal.FinalReleaseComObject(value);
                }
            }
            catch
            {
            }
        }

        private static bool TryGetAssociatedExecutable(string filePath, out string executablePath)
        {
            executablePath = null;

            try
            {
                string extension = Path.GetExtension(filePath);
                if (String.IsNullOrWhiteSpace(extension)) return false;

                const int capacity = 2048;
                StringBuilder buffer = new StringBuilder(capacity);
                uint length = capacity;

                uint result = AssocQueryString(
                    0,
                    ASSOCSTR_EXECUTABLE,
                    extension,
                    null,
                    buffer,
                    ref length);

                if (result != 0 || buffer.Length == 0) return false;

                executablePath = buffer.ToString();
                return !String.IsNullOrWhiteSpace(executablePath);
            }
            catch
            {
                executablePath = null;
                return false;
            }
        }

        private static bool IsVsCodeExecutablePath(string executablePath)
        {
            if (String.IsNullOrWhiteSpace(executablePath)) return false;

            try
            {
                string name = Path.GetFileName(executablePath);
                return String.Equals(name, "Code.exe", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(name, "Code - Insiders.exe", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDoubleClickCandidate(
            IntPtr firstRoot,
            POINT firstPoint,
            uint firstTime,
            IntPtr secondRoot,
            POINT secondPoint,
            uint secondTime,
            int maxTimeMs,
            int doubleClickWidth,
            int doubleClickHeight)
        {
            if (firstRoot == IntPtr.Zero || firstRoot != secondRoot) return false;

            uint elapsed = unchecked(secondTime - firstTime);
            if (elapsed > (uint)Math.Max(1, maxTimeMs)) return false;

            int halfWidth = Math.Max(1, doubleClickWidth / 2);
            int halfHeight = Math.Max(1, doubleClickHeight / 2);

            return Math.Abs(secondPoint.X - firstPoint.X) <= halfWidth
                && Math.Abs(secondPoint.Y - firstPoint.Y) <= halfHeight;
        }

        private static void ResetExplorerClick()
        {
            _haveExplorerClick = false;
            _lastExplorerRoot = IntPtr.Zero;
            _lastExplorerPoint = new POINT();
            _lastExplorerClickTime = 0;
        }

        private static void RefreshProcessSnapshots()
        {
            try
            {
                HashSet<int> codePids = new HashSet<int>();
                AddProcessIds("Code", codePids);
                AddProcessIds("Code - Insiders", codePids);

                HashSet<int> explorerPids = new HashSet<int>();
                AddProcessIds("explorer", explorerPids);

                List<IntPtr> windows = new List<IntPtr>();
                EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
                {
                    uint pid;
                    GetWindowThreadProcessId(hwnd, out pid);
                    if (pid != 0 && codePids.Contains((int)pid))
                    {
                        windows.Add(hwnd);
                    }
                    return true;
                }, IntPtr.Zero);

                _vsCodeWindowSnapshot = windows.ToArray();

                int[] explorerSnapshot = new int[explorerPids.Count];
                explorerPids.CopyTo(explorerSnapshot);
                _explorerPidSnapshot = explorerSnapshot;
            }
            catch (Exception ex)
            {
                Log("Process snapshot refresh error: " + ex.Message);
            }
        }

        private static void AddProcessIds(string processName, HashSet<int> target)
        {
            Process[] processes = new Process[0];
            try
            {
                processes = Process.GetProcessesByName(processName);
                foreach (Process process in processes)
                {
                    target.Add(process.Id);
                }
            }
            catch
            {
            }
            finally
            {
                foreach (Process process in processes)
                {
                    try { process.Dispose(); } catch { }
                }
            }
        }

        private static bool IsCachedVsCodeWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            IntPtr[] windows = _vsCodeWindowSnapshot;
            for (int i = 0; i < windows.Length; i++)
            {
                if (windows[i] == hwnd) return true;
            }

            return false;
        }

        private static bool IsCachedExplorerWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;

            uint processId;
            GetWindowThreadProcessId(hwnd, out processId);
            if (processId == 0) return false;

            int[] pids = _explorerPidSnapshot;
            for (int i = 0; i < pids.Length; i++)
            {
                if ((uint)pids[i] == processId) return true;
            }

            return false;
        }

        private static bool IsCloseButtonArea(IntPtr hwnd, POINT point)
        {
            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) return false;

            int dpi = 96;
            try
            {
                uint windowDpi = GetDpiForWindow(hwnd);
                if (windowDpi > 0) dpi = (int)windowDpi;
            }
            catch
            {
            }

            return PointInCloseButtonArea(rect, point, dpi);
        }

        private static bool PointInCloseButtonArea(RECT rect, POINT point, int dpi)
        {
            int closeWidth = Scale(50, dpi);
            int captionHeight = Scale(48, dpi);

            return point.X >= rect.Right - closeWidth
                && point.X < rect.Right
                && point.Y >= rect.Top
                && point.Y < rect.Top + captionHeight;
        }

        private static int Scale(int value, int dpi)
        {
            return (int)Math.Round(value * (dpi / 96.0));
        }

        private static void ShowHiddenWindows()
        {
            List<IntPtr> windows = new List<IntPtr>(HiddenWindows);
            HiddenWindows.Clear();
            Interlocked.Exchange(ref _hiddenWindowCount, 0);

            IntPtr last = IntPtr.Zero;
            foreach (IntPtr hwnd in windows)
            {
                if (IsWindow(hwnd))
                {
                    ShowWindow(hwnd, SW_SHOW);
                    last = hwnd;
                    Log("Restored VS Code window hwnd=" + hwnd.ToString());
                }
            }

            if (last != IntPtr.Zero)
            {
                SetForegroundWindow(last);
            }
        }

        private static void ExitHelper()
        {
            if (_exiting) return;

            _exiting = true;
            ShowHiddenWindows();
            Application.ExitThread();
        }

        private static Icon GetTrayIcon()
        {
            try
            {
                string codePath = FindCodeExe();
                if (!String.IsNullOrEmpty(codePath))
                {
                    Icon icon = Icon.ExtractAssociatedIcon(codePath);
                    if (icon != null) return icon;
                }
            }
            catch
            {
            }

            return SystemIcons.Application;
        }

        private static string FindCodeExe()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            string[] candidates = new string[]
            {
                Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"),
                Path.Combine(local, "Programs", "Microsoft VS Code Insiders", "Code - Insiders.exe"),
                Path.Combine(programFiles, "Microsoft VS Code", "Code.exe"),
                Path.Combine(programFiles, "Microsoft VS Code Insiders", "Code - Insiders.exe")
            };

            foreach (string path in candidates)
            {
                if (File.Exists(path)) return path;
            }

            return null;
        }

        private static void Cleanup()
        {
            try
            {
                if (_systemEventsSubscribed)
                {
                    SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                    SystemEvents.SessionSwitch -= OnSessionSwitch;
                    _systemEventsSubscribed = false;
                }
            }
            catch { }

            try
            {
                if (_watchdogTimer != null)
                {
                    _watchdogTimer.Stop();
                    _watchdogTimer.Dispose();
                }
            }
            catch { }

            try
            {
                if (_snapshotTimer != null)
                {
                    _snapshotTimer.Stop();
                    _snapshotTimer.Dispose();
                }
            }
            catch { }

            try
            {
                if (_exitTimer != null)
                {
                    _exitTimer.Stop();
                    _exitTimer.Dispose();
                }
            }
            catch { }

            try
            {
                if (_restoreTimer != null)
                {
                    _restoreTimer.Stop();
                    _restoreTimer.Dispose();
                }
            }
            catch { }

            StopHookThread();

            try
            {
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
            }
            catch { }

            try
            {
                if (_dispatcher != null) _dispatcher.Dispose();
            }
            catch { }

            try
            {
                if (_exitEvent != null) _exitEvent.Dispose();
            }
            catch { }

            try
            {
                if (_mutex != null)
                {
                    _mutex.ReleaseMutex();
                    _mutex.Dispose();
                }
            }
            catch { }

            try
            {
                if (_hookThreadReady != null) _hookThreadReady.Dispose();
            }
            catch { }
        }

        private static void StopHookThread()
        {
            try
            {
                uint threadId = _hookThreadId;
                Thread thread = _hookThread;

                if (threadId != 0)
                {
                    PostThreadMessage(threadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
                }

                if (thread != null && thread.IsAlive)
                {
                    thread.Join(2000);
                }
            }
            catch
            {
            }
        }

        private static void Log(string message)
        {
            if (String.IsNullOrEmpty(_logPath)) return;

            try
            {
                lock (LogLock)
                {
                    File.AppendAllText(
                        _logPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public UIntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, int gaFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern uint AssocQueryString(
            uint flags,
            int str,
            string pszAssoc,
            string pszExtra,
            StringBuilder pszOut,
            ref uint pcchOut);
    }
}
