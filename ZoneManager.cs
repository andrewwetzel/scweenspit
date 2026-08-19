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
    /// (which already excludes the taskbar), in reading order: top-to-bottom, then left-to-right,
    /// so hotkey cycling walks Quadrants as TL, TR, BL, BR rather than down the columns.
    /// Shared fractional edges round to identical pixels, so zones never gap or overlap.
    /// </summary>
    public List<RECT> ZonesFor(MonitorGeometry geo)
    {
        var work = EffectiveWork(geo);
        int w = work.Width, h = work.Height;

        return Config.ZonesFor(geo.Device)
            .Select(f => new RECT
            {
                Left   = work.Left + (int)Math.Round(f.L * w),
                Top    = work.Top  + (int)Math.Round(f.T * h),
                Right  = work.Left + (int)Math.Round(f.R * w),
                Bottom = work.Top  + (int)Math.Round(f.B * h),
            })
            .Select(Pad)
            .Where(r => r.Width > 0 && r.Height > 0)
            .OrderBy(r => r.Top).ThenBy(r => r.Left)
            .ToList();
    }

    /// <summary>
    /// The area zones are laid out in: what Windows reports as the work area, less the user's own
    /// margins. Margins that would leave nothing usable are ignored rather than obeyed.
    /// </summary>
    public RECT EffectiveWork(MonitorGeometry geo)
    {
        var m = Config.LayoutFor(geo.Device).Margins;
        if (m is null || !m.Any) return geo.Work;

        var fit = m.Fitted(geo.Work.Width, geo.Work.Height);
        return new RECT
        {
            Left = geo.Work.Left + fit.Left,
            Top = geo.Work.Top + fit.Top,
            Right = geo.Work.Right - fit.Right,
            Bottom = geo.Work.Bottom - fit.Bottom,
        };
    }

    private RECT Pad(RECT r)
    {
        int p = Config.Padding;
        if (p <= 0) return r;
        // Refuse to pad a zone out of existence.
        if (r.Width <= 2 * p || r.Height <= 2 * p) return r;
        return new RECT { Left = r.Left + p, Top = r.Top + p, Right = r.Right - p, Bottom = r.Bottom - p };
    }

    /// <summary>A single zone spanning the whole work area means "leave this monitor alone".</summary>
    public bool IsOptedOut(MonitorGeometry geo)
    {
        var frac = Config.ZonesFor(geo.Device);
        bool wholeArea = frac.Count == 1 && frac[0].L <= 0.001 && frac[0].T <= 0.001
                                         && frac[0].R >= 0.999 && frac[0].B >= 0.999;

        // "One full-size zone" only means "leave it alone" if that zone really is the whole work
        // area. With margins reserved there is still work to do, and claiming otherwise would let
        // a maximize be restored and re-placed at the identical rect - silently losing the
        // maximized state for no visible change.
        var eff = EffectiveWork(geo);
        bool untouched = eff.Left == geo.Work.Left && eff.Top == geo.Work.Top
                      && eff.Right == geo.Work.Right && eff.Bottom == geo.Work.Bottom;

        return wholeArea && untouched;
    }

    /// <summary>Index of the zone containing a point, or -1.</summary>
    public static int ZoneIndexAt(List<RECT> zones, POINT p)
    {
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            if (p.X >= z.Left && p.X < z.Right && p.Y >= z.Top && p.Y < z.Bottom) return i;
        }
        return -1;
    }

    /// <summary>
    /// The zone under a point, or the nearest one when the point falls in a gap — the gutter left
    /// by Padding, or a reserved margin band. Dropping in a 16px gutter should still land somewhere.
    /// </summary>
    public static int ZoneAtOrNearest(List<RECT> zones, POINT p)
    {
        int hit = ZoneIndexAt(zones, p);
        return hit >= 0 ? hit : PickZoneIndex(zones, new RECT { Left = p.X, Top = p.Y, Right = p.X, Bottom = p.Y });
    }

    /// <summary>Smallest rectangle covering both zones — how a window spans several at once.</summary>
    public static RECT Union(RECT a, RECT b) => new()
    {
        Left = Math.Min(a.Left, b.Left),
        Top = Math.Min(a.Top, b.Top),
        Right = Math.Max(a.Right, b.Right),
        Bottom = Math.Max(a.Bottom, b.Bottom),
    };

    public static bool TryGetMonitorAt(POINT p, out MonitorGeometry geo) =>
        TryGetMonitorInfo(MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST), out geo);

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
        if (rect.Width <= 0 || rect.Height <= 0) return false;

        // WINDOWPLACEMENT is documented in *workspace* coordinates, which differ from screen
        // coordinates whenever the taskbar sits at the top or left of the monitor.
        if (TryGetMonitor(hWnd, out var geo))
        {
            int dx = geo.Work.Left - geo.Bounds.Left, dy = geo.Work.Top - geo.Bounds.Top;
            rect.Left += dx; rect.Right += dx;
            rect.Top += dy; rect.Bottom += dy;
        }
        return true;
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

        if (maximized && !ShowWindow(hWnd, SW_RESTORE))
            Log.Write($"  ShowWindow(SW_RESTORE) returned false for 0x{hWnd:X}");

        // The BOOL matters: UIPI refuses cross-integrity moves (an elevated window from an
        // unelevated us) and returns FALSE. Treating that as success would report a healthy
        // clamp in the log while nothing on screen moved.
        if (!SetWindowPos(hWnd, HWND_TOP, zone.Left, zone.Top, zone.Width, zone.Height,
                SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOZORDER))
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Log.Write($"  SetWindowPos FAILED for 0x{hWnd:X} err={err}" +
                      (err == 5 ? " (ACCESS_DENIED - target window is elevated, we are not)" : ""));
            return false;
        }

        Log.Write($"clamp {hWnd:X} -> {zone} (maximized={maximized})");
        return true;
    }

    private static bool Near(RECT a, RECT b) =>
        Math.Abs(a.Left - b.Left)   <= Tolerance && Math.Abs(a.Top - b.Top)       <= Tolerance &&
        Math.Abs(a.Right - b.Right) <= Tolerance && Math.Abs(a.Bottom - b.Bottom) <= Tolerance;
}
