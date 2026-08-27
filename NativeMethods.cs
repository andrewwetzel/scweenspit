using System.Runtime.InteropServices;
using System.Text;

namespace ScweenSpit;

/// <summary>All P/Invoke signatures, structs and constants. Nothing else lives here.</summary>
public static class Native
{
    // ---- events / hooks ----------------------------------------------------
    public const uint EVENT_SYSTEM_MOVESIZESTART    = 0x000A;
    public const uint EVENT_SYSTEM_MOVESIZEEND      = 0x000B;
    public const uint EVENT_OBJECT_SHOW             = 0x8002;
    public const uint EVENT_OBJECT_UNCLOAKED        = 0x8018;
    public const uint EVENT_SYSTEM_FOREGROUND       = 0x0003;
    public const uint EVENT_OBJECT_LOCATIONCHANGE   = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT         = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS       = 0x0002;
    public const int  OBJID_WINDOW                  = 0;
    public const int  CHILDID_SELF                  = 0;

    // ---- window styles -----------------------------------------------------
    public const int GWL_STYLE   = -16;
    public const int GWL_EXSTYLE = -20;

    public const long WS_CAPTION       = 0x00C00000L;
    public const long WS_THICKFRAME    = 0x00040000L;
    public const long WS_CHILD         = 0x40000000L;
    public const int  WS_EX_TOOLWINDOW  = 0x00000080;
    public const int  WS_EX_TRANSPARENT = 0x00000020;   // click-through
    public const int  WS_EX_NOACTIVATE  = 0x08000000;   // never takes focus

    // ---- ShowWindow / placement -------------------------------------------
    public const int SW_RESTORE        = 9;
    public const int SW_SHOWMAXIMIZED  = 3;

    // ---- SetWindowPos ------------------------------------------------------
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOACTIVATE   = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW   = 0x0040;
    public const uint SWP_NOZORDER     = 0x0004;
    public const uint SWP_NOSIZE       = 0x0001;
    public const uint SWP_NOMOVE       = 0x0002;

    // ---- hit testing (telling a resize from a move) ------------------------
    public const uint WM_NCHITTEST = 0x0084;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const int HTSIZE = 4, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                     HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
                                                   uint flags, uint timeoutMs, out IntPtr result);

    /// <summary>True when the point is on a sizing border rather than the body of the window.</summary>
    public static bool IsOnSizingBorder(IntPtr hWnd, POINT screen)
    {
        var lParam = (IntPtr)((screen.Y << 16) | (screen.X & 0xFFFF));
        if (SendMessageTimeout(hWnd, WM_NCHITTEST, IntPtr.Zero, lParam, SMTO_ABORTIFHUNG, 200, out var hit) == IntPtr.Zero)
            return false;   // hung or unresponsive: assume a move, the modifier still gates us

        long code = hit.ToInt64();   // a foreign window's LRESULT need not fit in 32 bits
        return code is HTSIZE or HTLEFT or HTRIGHT or HTTOP or HTTOPLEFT
                    or HTTOPRIGHT or HTBOTTOM or HTBOTTOMLEFT or HTBOTTOMRIGHT;
    }

    // ---- monitors ----------------------------------------------------------
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // ---- hotkeys -----------------------------------------------------------
    public const int WM_HOTKEY   = 0x0312;
    public const uint MOD_ALT      = 0x0001;
    public const uint MOD_WIN      = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint VK_LEFT  = 0x25;
    public const uint VK_RIGHT = 0x27;
    public const uint VK_Z     = 0x5A;
    public const uint VK_HOME  = 0x24;
    public const uint VK_END   = 0x23;
    public const uint VK_S     = 0x53;
    public const uint WM_APP = 0x8000;
    public const uint WM_GETICON = 0x007F;
    public const int  ICON_SMALL2 = 2, ICON_SMALL = 0, ICON_BIG = 1;
    public const int  GCLP_HICON = -14, GCLP_HICONSM = -34;
    public const uint GA_ROOTOWNER = 3;
    public const uint GW_OWNER = 4;
    public const int  SW_MINIMIZE = 6, SW_SHOWNA = 8;

    public const int  VK_SHIFT   = 0x10;
    public const int  VK_CONTROL = 0x11;
    public const int  VK_MENU    = 0x12;   // Alt

    // ---- system parameters (Windows' own snap behaviour) --------------------
    public const uint SPI_GETWINARRANGING = 0x0082;
    public const uint SPI_SETWINARRANGING = 0x0083;
    public const uint SPI_GETSNAPSIZING   = 0x008C;
    public const uint SPI_SETSNAPSIZING   = 0x008D;
    public const uint SPI_GETDOCKMOVING   = 0x0090;
    public const uint SPI_SETDOCKMOVING   = 0x0091;
    public const uint SPIF_SENDCHANGE     = 0x0002;

    // ---- DWM ---------------------------------------------------------------
    public const int DWMWA_CLOAKED = 14;

    /// <summary>The rectangle the window is actually PAINTED in, without its invisible resize border.</summary>
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1 = 19;

    // ---- DPI ---------------------------------------------------------------
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width  => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int Width, Height; }

    // ---- DWM thumbnails ----------------------------------------------------
    // A live view of another window's content, composited by the desktop manager. The only way to
    // show what a window looks like without asking it to redraw itself into a bitmap.

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        public bool fVisible;
        public bool fSourceClientAreaOnly;
    }

    public const int DWM_TNP_RECTDESTINATION      = 0x00000001;
    public const int DWM_TNP_VISIBLE              = 0x00000008;
    public const int DWM_TNP_OPACITY              = 0x00000004;
    public const int DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    [DllImport("dwmapi.dll")]
    public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUnregisterThumbnail(IntPtr thumb);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);

    [DllImport("dwmapi.dll")]
    public static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT  rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int  cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;   // LPARAM: 8 bytes on x64, so IntPtr rather than int
    }

    public const uint ABM_NEW               = 0x00000000;
    public const uint ABM_REMOVE            = 0x00000001;
    public const uint ABM_QUERYPOS          = 0x00000002;
    public const uint ABM_SETPOS            = 0x00000003;
    public const uint ABM_ACTIVATE          = 0x00000006;
    public const uint ABM_WINDOWPOSCHANGED  = 0x00000009;
    public const int  ABN_STATECHANGE       = 0x0000000;
    public const int  ABN_POSCHANGED        = 0x0000001;
    public const int  ABN_FULLSCREENAPP     = 0x0000002;
    public const int  ABN_WINDOWARRANGE     = 0x0000003;
    public const uint ABM_GETTASKBARPOS = 0x00000005;
    public const uint ABM_GETSTATE       = 0x00000004;
    public const uint ABM_SETSTATE       = 0x0000000A;
    public const int  ABS_AUTOHIDE       = 0x00000001;
    public const int  ABS_ALWAYSONTOP    = 0x00000002;
    public const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;

    [DllImport("shell32.dll", SetLastError = true)]
    public static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    public delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    public const uint GA_ROOT = 2;

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    /// <summary>32/64-bit safe GetWindowLongPtr. The 64-bit entry point is absent on x86.</summary>
    public static long GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex).ToInt64() : GetWindowLong32(hWnd, nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    public static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetLastActivePopup(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, IntPtr extra);

    private const byte VkControl = 0x11, VkEscape = 0x1B;
    private const uint KeyUp = 0x0002;

    /// <summary>
    /// Presses Ctrl+Esc, which asks the shell for the Start menu.
    ///
    /// The documented shortcut rather than a message to the shell: SC_TASKLIST goes to the taskbar
    /// window, which this application may well have hidden, whereas the keystroke is handled however
    /// the shell is currently arranged. Ctrl+Esc rather than the Windows key itself because a lost
    /// key-up on that one leaves it stuck down, turning every later keystroke into a shortcut.
    /// </summary>
    public static void PressStartShortcut()
    {
        keybd_event(VkControl, 0, 0, IntPtr.Zero);
        keybd_event(VkEscape, 0, 0, IntPtr.Zero);
        keybd_event(VkEscape, 0, KeyUp, IntPtr.Zero);
        keybd_event(VkControl, 0, KeyUp, IntPtr.Zero);
    }
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool join);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);

    public const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    /// <summary>
    /// A message number the whole desktop agrees on for a given name, so one copy of this program
    /// can say something to another without either knowing the other's window.
    /// </summary>
    public static uint RegisterWindowMessage(string name) => RegisterWindowMessageW(name);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags,
                                                         System.Text.StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Full path of the executable behind a window. Uses the limited-information right rather than
    /// Process.MainModule, which is refused across integrity levels for exactly the applications a
    /// taskbar most needs to identify.
    /// </summary>
    public static string ExecutablePath(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0) return string.Empty;

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return string.Empty;

        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : string.Empty;
        }
        finally { CloseHandle(handle); }
    }

    // ---- machine load ------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME { public uint Low, High; }

    /// <summary>Totals since boot. A rate comes from the difference between two readings.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys;
        public ulong ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    // ---- animation ---------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct ANIMATIONINFO { public uint cbSize; public int iMinAnimate; }

    public const uint SPI_GETANIMATION = 0x0048, SPI_SETANIMATION = 0x0049;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    public static extern bool SystemParametersInfoAnimation(uint action, uint param,
                                                            ref ANIMATIONINFO info, uint winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
    private static extern uint GetClassLong32(IntPtr hWnd, int index);

    public static IntPtr GetClassLongPtr(IntPtr hWnd, int index) =>
        IntPtr.Size == 8 ? GetClassLongPtr64(hWnd, index) : new IntPtr(GetClassLong32(hWnd, index));

    public static string WindowTitle(IntPtr hWnd)
    {
        int length = GetWindowTextLength(hWnd);
        if (length <= 0) return string.Empty;

        var sb = new System.Text.StringBuilder(length + 1);
        return GetWindowText(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    public static void SetWindowLongPtr(IntPtr hWnd, int nIndex, long value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, nIndex, new IntPtr(value));
        else SetWindowLong32(hWnd, nIndex, (int)value);
    }

    /// <summary>True while the key is physically down right now.</summary>
    public static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Distinctly named: an overload differing only by out-parameter type is ambiguous
    /// at any call site using "out var".</summary>
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static extern int DwmGetWindowRect(IntPtr hwnd, int attr, out RECT value, int size);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int w, int h);

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);

    /// <summary>Reading form: pvParam is a pointer to the value.</summary>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    public static extern bool SystemParametersInfoGet(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    /// <summary>Writing form: for the BOOL settings pvParam carries the value itself, not a pointer.</summary>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    public static extern bool SystemParametersInfoSet(uint uiAction, uint uiParam, nint pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    public static string ClassNameOf(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    public static bool IsCloaked(IntPtr hWnd) =>
        DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
}
