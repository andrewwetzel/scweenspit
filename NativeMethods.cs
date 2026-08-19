using System.Runtime.InteropServices;
using System.Text;

namespace ScweenSpit;

/// <summary>All P/Invoke signatures, structs and constants. Nothing else lives here.</summary>
public static class Native
{
    // ---- events / hooks ----------------------------------------------------
    public const uint EVENT_SYSTEM_MOVESIZESTART    = 0x000A;
    public const uint EVENT_SYSTEM_MOVESIZEEND      = 0x000B;
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

        int code = hit.ToInt32();
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

    public const uint ABM_GETTASKBARPOS = 0x00000005;
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
