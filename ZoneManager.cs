using static ScweenSpit.Native;

namespace ScweenSpit;

public readonly record struct MonitorGeometry(string Device, RECT Work, RECT Bounds, bool IsPrimary = false)
{
    /// <summary>Something a person can match to a physical screen: "primary", or where it sits.</summary>
    public string Describe() =>
        IsPrimary ? "primary" : $"at {Bounds.Left},{Bounds.Top}";
}

/// <summary>A materialised zone: where it is, and whether windows in it sit over the taskbar.</summary>
public readonly record struct Zone(RECT Rect, bool CoverTaskbar)
{
    public override string ToString() => CoverTaskbar ? $"{Rect} [over taskbar]" : Rect.ToString();
}

/// <summary>Screen geometry, split math and the window clamp itself.</summary>
/// <summary>Geometry helpers that read better as extensions.</summary>
static class RectExtensions
{
    /// <summary>
    /// Grows a zone out over the taskbar on the sides where it already reaches the edge of the work
    /// area, leaving internal edges alone. Measuring the whole zone against the monitor instead
    /// would shift the divider it shares with its neighbours whenever the taskbar is not at the
    /// bottom — the two zones would no longer meet.
    /// </summary>
    public static RECT ExtendedTo(this RECT rect, FracRect f, RECT bounds)
    {
        const double edge = 0.001;
        return new RECT
        {
            Left   = f.L <= edge     ? bounds.Left   : rect.Left,
            Top    = f.T <= edge     ? bounds.Top    : rect.Top,
            Right  = f.R >= 1 - edge ? bounds.Right  : rect.Right,
            Bottom = f.B >= 1 - edge ? bounds.Bottom : rect.Bottom,
        };
    }
}

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

        geo = new MonitorGeometry(mi.szDevice, mi.rcWork, mi.rcMonitor, (mi.dwFlags & 1) != 0);
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
    /// <summary>The zones as laid out, with room taken out for a zone-scoped bar.</summary>
    public List<Zone> ZonesFor(MonitorGeometry geo)
    {
        var zones = RawZonesFor(geo);

        if (Config.Bars.TryGetValue(geo.Device, out var bar) && bar.Zone is int index
            && index >= 0 && index < zones.Count && Strip(zones[index].Rect, bar) is { } strip)
        {
            zones[index] = zones[index] with { Rect = Shorten(zones[index].Rect, strip, SplitConfig.ParseEdge(bar.Edge)) };
        }

        return zones;
    }

    /// <summary>
    /// Where a zone-scoped bar sits: a strip along one edge of its zone. Computed from the zones
    /// BEFORE the bar is subtracted, or the bar would walk itself inwards on every layout pass.
    /// </summary>
    public RECT? BarStrip(MonitorGeometry geo, BarSettings bar)
    {
        if (bar.Zone is not int index) return null;

        var zones = RawZonesFor(geo);
        return index >= 0 && index < zones.Count ? Strip(zones[index].Rect, bar) : null;
    }

    private static RECT? Strip(RECT zone, BarSettings bar)
    {
        int thickness = Math.Min(bar.Thickness, Math.Min(zone.Width, zone.Height) / 2);
        if (thickness <= 0) return null;

        return SplitConfig.ParseEdge(bar.Edge) switch
        {
            BarEdge.Left  => zone with { Right = zone.Left + thickness },
            BarEdge.Right => zone with { Left = zone.Right - thickness },
            BarEdge.Top   => zone with { Bottom = zone.Top + thickness },
            _             => zone with { Top = zone.Bottom - thickness },
        };
    }

    private static RECT Shorten(RECT zone, RECT strip, BarEdge edge) => edge switch
    {
        BarEdge.Left  => zone with { Left = strip.Right },
        BarEdge.Right => zone with { Right = strip.Left },
        BarEdge.Top   => zone with { Top = strip.Bottom },
        _             => zone with { Bottom = strip.Top },
    };

    public List<Zone> RawZonesFor(MonitorGeometry geo)
    {
        var work = EffectiveWork(geo);

        return Config.ZonesFor(geo.Device)
            .Select(f => new Zone(f.CoverTaskbar ? Materialise(f, work).ExtendedTo(f, geo.Bounds)
                                                 : Materialise(f, work), f.CoverTaskbar))
            .Select(z => z with { Rect = Pad(z.Rect) })
            .Where(z => z.Rect.Width > 0 && z.Rect.Height > 0)
            .OrderBy(z => z.Rect.Top).ThenBy(z => z.Rect.Left)
            .ToList();
    }

    private static RECT Materialise(FracRect f, RECT area) => new()
    {
        Left   = area.Left + (int)Math.Round(f.L * area.Width),
        Top    = area.Top  + (int)Math.Round(f.T * area.Height),
        Right  = area.Left + (int)Math.Round(f.R * area.Width),
        Bottom = area.Top  + (int)Math.Round(f.B * area.Height),
    };

    /// <summary>
    /// The area zones are laid out in: what Windows reports as the work area, less the user's own
    /// margins. Margins that would leave nothing usable are ignored rather than obeyed.
    /// </summary>
    public RECT EffectiveWork(MonitorGeometry geo)
    {
        var basis = Basis(geo);

        var m = Config.LayoutFor(geo.Device).Margins;
        if (m is null || !m.Any) return basis;

        var fit = m.Fitted(basis.Width, basis.Height);
        return new RECT
        {
            Left = basis.Left + fit.Left,
            Top = basis.Top + fit.Top,
            Right = basis.Right - fit.Right,
            Bottom = basis.Bottom - fit.Bottom,
        };
    }

    /// <summary>
    /// The area zones are measured against, before margins.
    ///
    /// Normally the work area, so the taskbar is avoided. But with the shell taskbar hidden its
    /// reservation is dead space: the appbar registration survives the window being hidden, so
    /// Windows keeps reporting a reduced work area for a strip nothing is drawn in. Reclaiming it
    /// is the entire point of having hidden the thing.
    /// </summary>
    public RECT Basis(MonitorGeometry geo) => Config.HideWindowsTaskbar ? geo.Bounds : geo.Work;

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

        // "One full-size zone" only means "leave it alone" if that zone really is the whole area we
        // lay out in. With margins reserved there is still work to do, and claiming otherwise would
        // let a maximize be restored and re-placed at the identical rect - silently losing the
        // maximized state for no visible change.
        var eff = EffectiveWork(geo);
        var basis = Basis(geo);
        bool untouched = eff.Left == basis.Left && eff.Top == basis.Top
                      && eff.Right == basis.Right && eff.Bottom == basis.Bottom;

        return wholeArea && untouched;
    }

    /// <summary>Index of the zone containing a point, or -1.</summary>
    public static int ZoneIndexAt(List<Zone> zones, POINT p)
    {
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i].Rect;
            if (p.X >= z.Left && p.X < z.Right && p.Y >= z.Top && p.Y < z.Bottom) return i;
        }
        return -1;
    }

    /// <summary>
    /// The zone under a point, or the nearest one when the point falls in a gap — the gutter left
    /// by Padding, or a reserved margin band. Dropping in a 16px gutter should still land somewhere.
    /// </summary>
    public static int ZoneAtOrNearest(List<Zone> zones, POINT p)
    {
        int hit = ZoneIndexAt(zones, p);
        return hit >= 0 ? hit : PickZoneIndex(zones, new RECT { Left = p.X, Top = p.Y, Right = p.X, Bottom = p.Y });
    }

    /// <summary>Smallest zone covering both — how a window spans several at once.</summary>
    public static Zone Union(Zone a, Zone b) => new(new RECT
    {
        Left = Math.Min(a.Rect.Left, b.Rect.Left),
        Top = Math.Min(a.Rect.Top, b.Rect.Top),
        Right = Math.Max(a.Rect.Right, b.Rect.Right),
        Bottom = Math.Max(a.Rect.Bottom, b.Rect.Bottom),
    }, a.CoverTaskbar || b.CoverTaskbar);

    public static bool TryGetMonitorAt(POINT p, out MonitorGeometry geo) =>
        TryGetMonitorInfo(MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST), out geo);

    /// <summary>Index of the zone holding the window's centre point; nearest zone centre otherwise.</summary>
    public static int PickZoneIndex(List<Zone> zones, RECT win)
    {
        int cx = (win.Left + win.Right) / 2, cy = (win.Top + win.Bottom) / 2;

        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i].Rect;
            if (cx >= z.Left && cx < z.Right && cy >= z.Top && cy < z.Bottom) return i;
        }

        int best = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i].Rect;
            double dx = (z.Left + z.Right) / 2.0 - cx, dy = (z.Top + z.Bottom) / 2.0 - cy;
            double dist = dx * dx + dy * dy;
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        return best;
    }

    // ---- staying on one display --------------------------------------------

    /// <summary>Overlap between two rectangles, in pixels. Zero when they do not touch.</summary>
    public static long OverlapArea(RECT a, RECT b)
    {
        long w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        long h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return w > 0 && h > 0 ? w * h : 0;
    }

    /// <summary>
    /// True when the window has a real presence on a display other than <paramref name="home"/> —
    /// which is what "it opened across all my screens" actually means. A window merely hanging a
    /// few pixels off an edge, or pushed off-screen entirely, is not spanning and is left alone.
    /// </summary>
    public static bool SpansDisplays(RECT win, MonitorGeometry home)
    {
        long area = (long)win.Width * win.Height;
        if (area <= 0) return false;

        // Cheap exit for the overwhelmingly common case: it all fits on its own monitor.
        if (OverlapArea(win, home.Bounds) >= area * 98 / 100) return false;

        long threshold = Math.Max(MinSpanPixels, area * MinSpanPercent / 100);
        foreach (var other in AllMonitors())
        {
            if (other.Device == home.Device) continue;
            if (OverlapArea(win, other.Bounds) >= threshold) return true;
        }
        return false;
    }

    private const long MinSpanPixels = 20_000;   // ~140x140: smaller than this is a stray edge
    private const long MinSpanPercent = 3;

    /// <summary>
    /// Slides the window back inside <paramref name="area"/>, keeping its size where it fits and
    /// shrinking only when it genuinely cannot. Moving beats resizing: an app that reopens across
    /// two screens usually wants its remembered size, just not its remembered position.
    /// </summary>
    public static RECT ContainWithin(RECT win, RECT area)
    {
        int w = Math.Min(win.Width, area.Width);
        int h = Math.Min(win.Height, area.Height);
        int x = Math.Clamp(win.Left, area.Left, area.Right - w);
        int y = Math.Clamp(win.Top, area.Top, area.Bottom - h);
        return new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
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

    /// <summary>
    /// Raises a window above the taskbar, or puts it back among ordinary windows. Required for a
    /// zone that covers the taskbar: the shell is topmost, so a normal window cannot draw over it.
    /// </summary>
    public static void SetTopmost(IntPtr hWnd, bool topmost)
    {
        SetWindowPos(hWnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Log.Write($"  topmost={topmost} for 0x{hWnd:X}");
    }

    private static bool Near(RECT a, RECT b) =>
        Math.Abs(a.Left - b.Left)   <= Tolerance && Math.Abs(a.Top - b.Top)       <= Tolerance &&
        Math.Abs(a.Right - b.Right) <= Tolerance && Math.Abs(a.Bottom - b.Bottom) <= Tolerance;
}
