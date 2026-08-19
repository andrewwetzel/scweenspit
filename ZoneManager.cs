using static ScweenSpit.Native;

namespace ScweenSpit;

public readonly record struct MonitorGeometry(string Device, RECT Work, RECT Bounds);

/// <summary>Screen geometry, split math and the window clamp itself.</summary>
public sealed class ZoneManager(SplitConfig config)
{
    /// <summary>Slack (px) for "already where we want it" and "covers the whole monitor" tests.</summary>
    private const int Tolerance = 2;
    private const int CoverSlack = 4;

    public SplitConfig Config { get; set; } = config;

    private static bool monitorInfoFailureLogged;

    // ---- monitors ----------------------------------------------------------

    public static bool TryGetMonitor(IntPtr hWnd, out MonitorGeometry geo)
    {
        var hMon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        return TryGetMonitorInfo(hMon, out geo);
    }

    public static bool TryGetMonitorInfo(IntPtr hMonitor, out MonitorGeometry geo)
    {
        geo = default;
        if (hMonitor == IntPtr.Zero) return false;

        var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(hMonitor, ref mi))
        {
            // A wrong cbSize is the classic cause here, and it would silently kill every clamp.
            if (!monitorInfoFailureLogged)
            {
                monitorInfoFailureLogged = true;
                Log.Write($"GetMonitorInfo FAILED (cbSize={mi.cbSize}, " +
                          $"err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            }
            return false;
        }

        geo = new MonitorGeometry(mi.szDevice, mi.rcWork, mi.rcMonitor);
        return true;
    }

    public static List<MonitorGeometry> AllMonitors()
    {
        var found = new List<MonitorGeometry>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
        {
            if (TryGetMonitorInfo(hMon, out var geo)) found.Add(geo);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ---- split math --------------------------------------------------------

    /// <summary>
    /// Materializes this monitor's fractional zones into pixel rectangles against its work area
    /// (which already excludes the taskbar), ordered left-to-right then top-to-bottom.
    /// Shared fractional edges round to identical pixels, so zones never gap or overlap.
    /// </summary>
    public List<RECT> ZonesFor(MonitorGeometry geo)
    {
        var work = geo.Work;
        int w = work.Width, h = work.Height;

        return Config.ZonesFor(geo.Device)
            .Select(f => new RECT
            {
                Left   = work.Left + (int)Math.Round(f.L * w),
                Top    = work.Top  + (int)Math.Round(f.T * h),
                Right  = work.Left + (int)Math.Round(f.R * w),
                Bottom = work.Top  + (int)Math.Round(f.B * h),
            })
            .Where(r => r.Width > 0 && r.Height > 0)
            .OrderBy(r => r.Left).ThenBy(r => r.Top)
            .ToList();
    }

    /// <summary>A single zone spanning the whole work area means "leave this monitor alone".</summary>
    public bool IsOptedOut(MonitorGeometry geo)
    {
        var frac = Config.ZonesFor(geo.Device);
        return frac.Count == 1 && frac[0].L <= 0.001 && frac[0].T <= 0.001
                               && frac[0].R >= 0.999 && frac[0].B >= 0.999;
    }

    /// <summary>Index of the zone holding the window's centre point; nearest zone centre otherwise.</summary>
    public static int PickZoneIndex(List<RECT> zones, RECT win)
    {
        int cx = (win.Left + win.Right) / 2, cy = (win.Top + win.Bottom) / 2;

        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            if (cx >= z.Left && cx < z.Right && cy >= z.Top && cy < z.Bottom) return i;
        }

        int best = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            double dx = (z.Left + z.Right) / 2.0 - cx, dy = (z.Top + z.Bottom) / 2.0 - cy;
            double dist = dx * dx + dy * dy;
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        return best;
    }

    // ---- detection ---------------------------------------------------------

    public static bool IsMaximized(IntPtr hWnd)
    {
        var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        return GetWindowPlacement(hWnd, ref wp) && wp.showCmd == SW_SHOWMAXIMIZED;
    }

    /// <summary>
    /// The window's pre-maximize rectangle. Lets a maximized window clamp into the zone the user
    /// actually had it in, rather than whichever zone owns the monitor centre.
    /// </summary>
    public static bool TryGetRestoreRect(IntPtr hWnd, out RECT rect)
    {
        var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        rect = default;
        if (!GetWindowPlacement(hWnd, ref wp) || wp.showCmd != SW_SHOWMAXIMIZED) return false;

        rect = wp.rcNormalPosition;
        return rect.Width > 0 && rect.Height > 0;
    }

    /// <summary>Chromeless window that covers the monitor's full bounds — i.e. borderless fullscreen.</summary>
    public static bool IsBorderlessFullscreen(IntPtr hWnd, RECT win, RECT monitorBounds)
    {
        long style = GetWindowLongPtr(hWnd, GWL_STYLE);
        if ((style & (WS_CAPTION | WS_THICKFRAME)) != 0) return false;

        return win.Left   <= monitorBounds.Left   + CoverSlack
            && win.Top    <= monitorBounds.Top    + CoverSlack
            && win.Right  >= monitorBounds.Right  - CoverSlack
            && win.Bottom >= monitorBounds.Bottom - CoverSlack;
    }

    /// <summary>True when this window is currently claiming a whole monitor one way or the other.</summary>
    public static bool NeedsClamp(IntPtr hWnd, RECT win, RECT monitorBounds) =>
        IsMaximized(hWnd) || IsBorderlessFullscreen(hWnd, win, monitorBounds);

    // ---- the clamp ---------------------------------------------------------

    /// <summary>
    /// Moves the window into <paramref name="zone"/>. Returns false when it was already there,
    /// which keeps us from emitting a pointless LOCATIONCHANGE storm.
    /// Callers must stamp the reentrancy guard *before* calling this.
    /// </summary>
    public static bool ClampToZone(IntPtr hWnd, RECT zone)
    {
        if (!GetWindowRect(hWnd, out var win)) return false;

        bool maximized = IsMaximized(hWnd);
        if (!maximized && Near(win, zone)) return false;

        if (maximized) ShowWindow(hWnd, SW_RESTORE);

        SetWindowPos(hWnd, HWND_TOP, zone.Left, zone.Top, zone.Width, zone.Height,
            SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOZORDER);

        Log.Write($"clamp {hWnd:X} -> {zone} (maximized={maximized})");
        return true;
    }

    private static bool Near(RECT a, RECT b) =>
        Math.Abs(a.Left - b.Left)   <= Tolerance && Math.Abs(a.Top - b.Top)       <= Tolerance &&
        Math.Abs(a.Right - b.Right) <= Tolerance && Math.Abs(a.Bottom - b.Bottom) <= Tolerance;
}
