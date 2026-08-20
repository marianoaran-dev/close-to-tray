# Close to Tray for Visual Studio Code
# v0.1.0
# Windows 11, current-user helper. No admin rights required.

$ErrorActionPreference = 'Stop'

$source = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CloseToTray
{
    public static class App
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLOSE = 20;
        private const int GA_ROOT = 2;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SM_CXSIZE = 30;
        private const int SM_CYSIZE = 31;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        private static readonly HashSet<IntPtr> HiddenWindows = new HashSet<IntPtr>();
        private static readonly LowLevelMouseProc MouseProc = HookCallback;
        private static IntPtr _mouseHook = IntPtr.Zero;
        private static NotifyIcon _tray;
        private static Mutex _mutex;
        private static EventWaitHandle _exitEvent;
        private static System.Windows.Forms.Timer _exitTimer;
        private static bool _swallowNextLeftUp;
        private static bool _exiting;

        [STAThread]
        public static void Run()
        {
            bool createdNew;
            _mutex = new Mutex(true, "Local\\CloseToTray.VSCode", out createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            SetupTray();
            SetupExitSignal();
            _mouseHook = SetHook(MouseProc);

            Application.Run();

            Cleanup();
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

        private static void SetupExitSignal()
        {
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CloseToTray.VSCode.Exit");
            _exitTimer = new System.Windows.Forms.Timer();
            _exitTimer.Interval = 400;
            _exitTimer.Tick += delegate
            {
                if (_exitEvent.WaitOne(0))
                {
                    ExitHelper();
                }
            };
            _exitTimer.Start();
        }

        private static Icon GetTrayIcon()
        {
            try
            {
                string codePath = FindCodeExe();
                if (!String.IsNullOrEmpty(codePath))
                {
                    Icon icon = Icon.ExtractAssociatedIcon(codePath);
                    if (icon != null)
                    {
                        return icon;
                    }
                }
            }
            catch { }

            return SystemIcons.Application;
        }

        private static string FindCodeExe()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] candidates = new string[]
            {
                System.IO.Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"),
                System.IO.Path.Combine(programFiles, "Microsoft VS Code", "Code.exe")
            };

            foreach (string path in candidates)
            {
                if (System.IO.File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private static IntPtr SetHook(LowLevelMouseProc proc)
        {
            using (Process currentProcess = Process.GetCurrentProcess())
            using (ProcessModule currentModule = currentProcess.MainModule)
            {
                IntPtr module = GetModuleHandle(currentModule.ModuleName);
                return SetWindowsHookEx(WH_MOUSE_LL, proc, module, 0);
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (!_exiting && nCode >= 0)
            {
                if (wParam == (IntPtr)WM_LBUTTONDOWN)
                {
                    MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    IntPtr root = GetAncestor(WindowFromPoint(data.pt), GA_ROOT);

                    if (root != IntPtr.Zero && IsVsCodeWindow(root) && IsCloseButtonHit(root, data.pt))
                    {
                        ShowWindow(root, SW_HIDE);
                        HiddenWindows.Add(root);
                        _swallowNextLeftUp = true;
                        return (IntPtr)1;
                    }
                }
                else if (wParam == (IntPtr)WM_LBUTTONUP && _swallowNextLeftUp)
                {
                    _swallowNextLeftUp = false;
                    return (IntPtr)1;
                }
            }

            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private static bool IsVsCodeWindow(IntPtr hwnd)
        {
            if (!IsWindow(hwnd))
            {
                return false;
            }

            StringBuilder className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            if (!className.ToString().StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal))
            {
                return false;
            }

            uint processId;
            GetWindowThreadProcessId(hwnd, out processId);
            if (processId == 0)
            {
                return false;
            }

            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    string name = process.ProcessName;
                    return String.Equals(name, "Code", StringComparison.OrdinalIgnoreCase)
                        || String.Equals(name, "Code - Insiders", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsCloseButtonHit(IntPtr hwnd, POINT point)
        {
            IntPtr result;
            UIntPtr ignored;
            IntPtr packedPoint = MakeLParam(point.X, point.Y);

            if (SendMessageTimeout(hwnd, WM_NCHITTEST, IntPtr.Zero, packedPoint, SMTO_ABORTIFHUNG, 60, out ignored) != IntPtr.Zero)
            {
                result = new IntPtr(unchecked((long)ignored.ToUInt64()));
                if (result.ToInt32() == HTCLOSE)
                {
                    return true;
                }
            }

            RECT rect;
            if (!GetWindowRect(hwnd, out rect))
            {
                return false;
            }

            int dpi = 96;
            try
            {
                uint windowDpi = GetDpiForWindow(hwnd);
                if (windowDpi > 0)
                {
                    dpi = (int)windowDpi;
                }
            }
            catch { }

            int closeWidth = Scale(46, dpi);
            int captionHeight = Scale(38, dpi);

            try
            {
                closeWidth = Math.Max(closeWidth, GetSystemMetricsForDpi(SM_CXSIZE, (uint)dpi));
                captionHeight = Math.Max(captionHeight, GetSystemMetricsForDpi(SM_CYSIZE, (uint)dpi));
            }
            catch { }

            return point.X >= rect.Right - closeWidth
                && point.X <= rect.Right
                && point.Y >= rect.Top
                && point.Y <= rect.Top + captionHeight;
        }

        private static int Scale(int value, int dpi)
        {
            return (int)Math.Round(value * (dpi / 96.0));
        }

        private static IntPtr MakeLParam(int x, int y)
        {
            int packed = ((y & 0xFFFF) << 16) | (x & 0xFFFF);
            return new IntPtr(packed);
        }

        private static void ShowHiddenWindows()
        {
            List<IntPtr> windows = new List<IntPtr>(HiddenWindows);
            HiddenWindows.Clear();

            IntPtr last = IntPtr.Zero;
            foreach (IntPtr hwnd in windows)
            {
                if (IsWindow(hwnd))
                {
                    ShowWindow(hwnd, SW_SHOW);
                    last = hwnd;
                }
            }

            if (last != IntPtr.Zero)
            {
                SetForegroundWindow(last);
            }
        }

        private static void ExitHelper()
        {
            if (_exiting)
            {
                return;
            }

            _exiting = true;
            ShowHiddenWindows();
            Application.ExitThread();
        }

        private static void Cleanup()
        {
            try
            {
                if (_mouseHook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_mouseHook);
                    _mouseHook = IntPtr.Zero;
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
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
            }
            catch { }

            try
            {
                if (_exitEvent != null)
                {
                    _exitEvent.Dispose();
                }
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
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, int gaFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);
    }
}
'@

Add-Type -TypeDefinition $source -ReferencedAssemblies 'System.Windows.Forms','System.Drawing'
[CloseToTray.App]::Run()
