using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using static ScweenSpit.Native;

namespace ScweenSpit;

/// <summary>
/// A taskbar of our own, docked to an edge of one display as a Win32 appbar. Windows reserves the
/// space for it exactly as it does for the shell's own bar, so every application keeps clear — which
/// is what makes a vertical bar possible on Windows 11, where the shell refuses to put its taskbar
/// anywhere but the bottom.
/// </summary>
public sealed class TaskbarWindow : Form
{
    private const int MinSlot = 34;
    private const int StatusIcon = 30;

    private readonly AppBar appBar;
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    private readonly ToolTip tips = new() { InitialDelay = 400, ReshowDelay = 120 };

    private readonly MonitorGeometry monitor;
    private readonly BarSettings settings;
    private readonly ZoneManager zones;

    /// <summary>A slot on the bar: a running window, a pinned application, or a pinned one running.</summary>
    private sealed record Button(string Path, TaskWindow? Window, bool Pinned);

    private List<TaskWindow> windows = [];
    private List<Button> buttons = [];

    // Arrival order, kept across rebuilds. EnumWindows returns z-order, which changes every time
    // anything is activated or minimised, so using it directly makes the buttons reshuffle
    // underneath the pointer.
    private readonly List<IntPtr> order = [];

    private readonly Dictionary<IntPtr, Bitmap> icons = [];
    private readonly Dictionary<string, Bitmap> fileIcons = new(StringComparer.OrdinalIgnoreCase);
    private int hovered = -1, hoveredStatus = -1;
    private long lastStatusPoll;

    private (int Percent, bool Charging)? battery;
    private (int Percent, bool Muted)? volume;
    private LinkKind link;

    public BarEdge Edge { get; }
    public string Device => monitor.Device;

    /// <summary>
    /// Raised when the ScweenSpit button is clicked. Hiding the Windows taskbar takes our own tray
    /// icon with it, so the bar has to carry a way back to Settings and Exit or the app becomes
    /// unreachable.
    /// </summary>
    public event Action<Point>? MenuRequested;

    /// <summary>Raised when the pinned list changes, so it can be written to the config file.</summary>
    public event Action? PinsChanged;

    public TaskbarWindow(MonitorGeometry monitor, BarSettings settings, ZoneManager zones)
    {
        this.monitor = monitor;
        this.settings = settings;
        this.zones = zones;
        Edge = SplitConfig.ParseEdge(settings.Edge);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        BackColor = Theme.Panel;
        DoubleBuffered = true;

        appBar = new AppBar(this);
        appBar.PositionChanged += Reposition;

        // Only a full-display bar can be an appbar; a zone-scoped one places itself.
        Load += (_, _) =>
        {
            if (settings.Zone is null) appBar.Register();
            Reposition();
            Rebuild();
        };
        refresh.Tick += (_, _) => Rebuild();
        refresh.Start();
    }

    /// <summary>
    /// Re-negotiates the reserved strip. Needed whenever the space available changes underneath us —
    /// most obviously when the shell's taskbar is hidden and its reservation goes away, which would
    /// otherwise leave our bar stranded above an empty band where the old taskbar used to be.
    /// </summary>
    public void Reposition()
    {
        int inset = settings.Floating ? settings.FloatMargin : 0;

        if (zones.BarStrip(monitor, settings) is { } strip)
        {
            // Not an appbar: Windows reserves space as one rectangle per monitor, so a bar across
            // part of an edge cannot be expressed that way. The zone is shortened for it instead.
            var placed = AppBar.Deflate(strip, inset);
            SetWindowPos(Handle, HWND_TOPMOST, placed.Left, placed.Top, placed.Width, placed.Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Log.Write($"bar on {monitor.Device} zone {settings.Zone}: {placed}");
            return;
        }

        appBar.Reserve(monitor.Bounds, Edge, settings.Thickness, inset);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>Clicking a button must not steal focus from the window it is about to activate.</summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// Rounds the corners facing the desktop, leaving the ones against the screen edge square — the
    /// region is built oversized on the docked side so that rounding falls outside the window.
    /// Windows 11 rounds free-floating surfaces, not ones flush against an edge.
    /// </summary>
    private void RoundCorners()
    {
        const int radius = 10;
        int w = Width + 1, h = Height + 1, r = radius * 2;

        // A floating bar is a free surface, so every corner rounds. A docked one keeps the corners
        // against the screen edge square, which is how Windows 11 treats a docked surface.
        var region = settings.Floating
            ? CreateRoundRectRgn(0, 0, w, h, r, r)
            : Edge switch
            {
                BarEdge.Bottom => CreateRoundRectRgn(0, 0, w, h + radius, r, r),
                BarEdge.Top    => CreateRoundRectRgn(0, -radius, w, h, r, r),
                BarEdge.Left   => CreateRoundRectRgn(-radius, 0, w, h, r, r),
                _              => CreateRoundRectRgn(0, 0, w + radius, h, r, r),
            };

        // SetWindowRgn takes ownership; the previous region is released by the system.
        if (region != IntPtr.Zero) SetWindowRgn(Handle, region, true);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated) RoundCorners();
    }

    protected override void WndProc(ref Message m)
    {
        if (appBar.HandleMessage(ref m)) return;
        base.WndProc(ref m);
    }

    private bool Vertical => Edge is BarEdge.Left or BarEdge.Right;
    private int Breadth => Vertical ? Width : Height;
    private int Slot => Math.Max(MinSlot, Math.Min(Breadth, settings.IconsOnly ? Breadth : 200));
    private int IconSize => Math.Clamp(Breadth / 2, 16, 32);

    // ---- contents ----------------------------------------------------------

    private void Rebuild()
    {
        windows = WindowList.Enumerate(settings.ThisDisplayOnly ? monitor.Device : null);

        order.RemoveAll(h => windows.All(w => w.Handle != h));
        foreach (var w in windows)
            if (!order.Contains(w.Handle)) order.Add(w.Handle);

        buttons = LayOutButtons();

        foreach (var w in windows)
            if (!icons.ContainsKey(w.Handle))
                icons[w.Handle] = WindowList.IconFor(w.Handle) ?? new Bitmap(32, 32);

        foreach (var button in buttons)
            if (button.Window is null && !fileIcons.ContainsKey(button.Path))
                fileIcons[button.Path] = WindowList.IconForFile(button.Path) ?? new Bitmap(32, 32);

        foreach (var stale in icons.Keys.Where(h => !IsWindow(h)).ToList())
        {
            icons[stale].Dispose();
            icons.Remove(stale);
        }

        // Volume goes through COM, so it is polled less often than the clock ticks.
        long now = Environment.TickCount64;
        if (settings.ShowStatus && now - lastStatusPoll > 2000)
        {
            lastStatusPoll = now;
            battery = SystemStatus.Battery();
            volume = SystemStatus.Volume();
            link = SystemStatus.Link();
        }

        Invalidate();
    }

    /// <summary>
    /// The cluster at the far end, in reading order. ScweenSpit sits with the other status icons
    /// rather than in the corner: it is a background app, and that is where background apps live.
    /// </summary>
    private enum Tray { Home, Volume, Network, Battery }

    private List<Tray> TrayItems()
    {
        var items = new List<Tray> { Tray.Home };
        if (!settings.ShowStatus) return items;

        items.Add(Tray.Volume);
        items.Add(Tray.Network);
        if (battery is not null) items.Add(Tray.Battery);
        return items;
    }

    private int ClockExtent => settings.ShowStatus ? (Vertical ? 48 : 88) : 0;

    /// <summary>Room the cluster needs, so window buttons know where to stop.</summary>
    private int StatusExtent => TrayItems().Count * StatusIcon + ClockExtent;

    private int StatusStart => Math.Max(0, (Vertical ? Height : Width) - StatusExtent);

    private Rectangle TrayAt(int index) => Vertical
        ? new Rectangle(0, StatusStart + index * StatusIcon, Width, StatusIcon)
        : new Rectangle(StatusStart + index * StatusIcon, 0, StatusIcon, Height);

    private Rectangle ClockArea => Vertical
        ? new Rectangle(0, Height - ClockExtent, Width, ClockExtent)
        : new Rectangle(Width - ClockExtent, 0, ClockExtent, Height);

    /// <summary>
    /// Pinned applications hold their positions whether or not they are running; everything else
    /// follows in the order it appeared. This is what makes the bar stop moving under the pointer.
    /// </summary>
    private List<Button> LayOutButtons()
    {
        var live = order.Select(h => windows.FirstOrDefault(w => w.Handle == h))
                        .Where(w => w is not null).Select(w => w!).ToList();

        var laid = new List<Button>();
        var placed = new HashSet<IntPtr>();

        foreach (var pin in settings.Pinned)
        {
            var running = live.Where(w => Same(w.Path, pin)).ToList();
            if (running.Count == 0) { laid.Add(new Button(pin, null, true)); continue; }

            foreach (var w in running) { laid.Add(new Button(pin, w, true)); placed.Add(w.Handle); }
        }

        foreach (var w in live)
            if (!placed.Contains(w.Handle)) laid.Add(new Button(w.Path, w, false));

        return laid;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string NameOf(string path)
    {
        try { return Path.GetFileNameWithoutExtension(path); } catch { return path; }
    }

    private Rectangle SlotAt(int index) => Vertical
        ? new Rectangle(0, index * Slot, Width, Slot)
        : new Rectangle(index * Slot, 0, Slot, Height);

    private int Capacity => Math.Max(0, StatusStart / Math.Max(1, Slot));

    private int SlotUnder(Point p)
    {
        for (int i = 0; i < Math.Min(buttons.Count, Capacity); i++)
            if (SlotAt(i).Contains(p)) return i;
        return -1;
    }

    private int TrayUnder(Point p)
    {
        var items = TrayItems();
        for (int i = 0; i < items.Count; i++)
            if (TrayAt(i).Contains(p)) return i;
        return -1;
    }

    // ---- interaction -------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int under = SlotUnder(e.Location), tray = TrayUnder(e.Location);
        if (under == hovered && tray == hoveredStatus) return;

        hovered = under;
        hoveredStatus = tray;
        Cursor = under >= 0 || tray >= 0 ? Cursors.Hand : Cursors.Default;

        // An icons-only bar is unreadable without these.
        tips.SetToolTip(this, tray >= 0 ? TrayTip(TrayItems()[tray])
                            : under >= 0 && under < buttons.Count ? Describe(buttons[under])
                            : string.Empty);
        Invalidate();
    }

    private static string Describe(Button b) => b.Window is { } w
        ? (w.Minimised ? $"{w.Title}  (minimised)" : w.Title)
        : $"{NameOf(b.Path)}  (not running)";

    private string TrayTip(Tray item) => item switch
    {
        Tray.Home => "ScweenSpit — settings",
        Tray.Volume => volume is { } v ? (v.Muted ? "Muted" : $"Volume {v.Percent}%") : "Volume",
        Tray.Network => link switch
        {
            LinkKind.Wired => "Wired network",
            LinkKind.Wireless => "Wi-Fi",
            _ => "No network",
        },
        _ => battery is { } b ? $"Battery {b.Percent}%{(b.Charging ? ", charging" : "")}" : "Power",
    };

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hovered = hoveredStatus = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        int tray = TrayUnder(e.Location);
        if (tray >= 0)
        {
            switch (TrayItems()[tray])
            {
                case Tray.Home: MenuRequested?.Invoke(Cursor.Position); break;
                case Tray.Volume: SystemStatus.Open("ms-settings:sound"); break;
                case Tray.Network: SystemStatus.Open("ms-settings:network"); break;
                case Tray.Battery: SystemStatus.Open("ms-settings:batterysaver"); break;
            }
            return;
        }

        if (ClockArea.Contains(e.Location)) { SystemStatus.Open("ms-settings:dateandtime"); return; }

        int under = SlotUnder(e.Location);
        if (under < 0 || under >= buttons.Count) return;

        var button = buttons[under];
        if (e.Button == MouseButtons.Right) { ShowButtonMenu(button, Cursor.Position); return; }
        if (e.Button != MouseButtons.Left) return;

        if (button.Window is { } window) WindowList.Toggle(window.Handle);
        else Launch(button.Path);

        Rebuild();
    }

    private static void Launch(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write($"could not launch {path}: {ex.Message}"); }
    }

    /// <summary>Right-click: pin, unpin, or close — the three things a taskbar button owes you.</summary>
    private void ShowButtonMenu(Button button, Point at)
    {
        var menu = new ContextMenuStrip();
        bool pinned = settings.Pinned.Any(p => Same(p, button.Path));

        if (!string.IsNullOrWhiteSpace(button.Path))
        {
            menu.Items.Add(new ToolStripMenuItem(pinned ? "Unpin from bar" : "Pin to bar", null, (_, _) =>
            {
                if (pinned) settings.Pinned.RemoveAll(p => Same(p, button.Path));
                else settings.Pinned.Add(button.Path);

                PinsChanged?.Invoke();
                Rebuild();
            }));
        }

        if (button.Window is { } window)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Close window", null, (_, _) =>
            {
                WindowList.Close(window.Handle);
                Rebuild();
            }));
        }

        if (menu.Items.Count > 0) menu.Show(at);
    }

    // ---- painting ----------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Panel);

        var foreground = GetForegroundWindow();
        using var hot = new SolidBrush(Theme.Raised);
        using var active = new SolidBrush(Color.FromArgb(46, 74, 158, 255));
        using var accent = new SolidBrush(Theme.Accent);
        using var label = Theme.Face(9f);
        using var text = new SolidBrush(Theme.Text);
        using var dim = new SolidBrush(Theme.Muted);

        int shown = Math.Min(buttons.Count, Capacity);
        for (int i = 0; i < shown; i++)
        {
            var slot = SlotAt(i);
            var button = buttons[i];
            var w = button.Window;

            bool front = w is not null && w.Handle == foreground;
            if (front) g.FillRectangle(active, slot);
            else if (i == hovered) g.FillRectangle(hot, slot);

            // Three states worth telling apart at a glance: in front, running, and merely pinned.
            if (w is not null)
            {
                int len = front ? Slot / 2 : Slot / 5;
                var mark = Vertical
                    ? new Rectangle(Edge == BarEdge.Left ? 0 : Width - 3, slot.Y + (slot.Height - len) / 2, 3, len)
                    : new Rectangle(slot.X + (slot.Width - len) / 2, Edge == BarEdge.Top ? 0 : Height - 3, len, 3);
                g.FillRectangle(front ? accent : dim, mark);
            }

            var icon = w is not null
                ? icons.GetValueOrDefault(w.Handle)
                : fileIcons.GetValueOrDefault(button.Path);

            if (icon is not null)
            {
                int size = IconSize;
                var box = settings.IconsOnly
                    ? new Rectangle(slot.X + (slot.Width - size) / 2, slot.Y + (slot.Height - size) / 2, size, size)
                    : new Rectangle(slot.X + 8, slot.Y + (slot.Height - size) / 2, size, size);

                // A pinned app that is not running, and a minimised window, are both "not here";
                // fading the icon says so without needing a legend.
                float opacity = w is null ? 0.45f : w.Minimised ? 0.62f : 1f;
                DrawIcon(g, icon, box, opacity);
            }

            if (settings.IconsOnly) continue;

            var caption = new Rectangle(slot.X + IconSize + 14, slot.Y, slot.Width - IconSize - 20, slot.Height);
            using var format = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            g.DrawString(w?.Title ?? NameOf(button.Path), label, w is null || w.Minimised ? dim : text,
                         caption, format);
        }

        PaintTray(g);
    }

    private static void DrawIcon(Graphics g, Bitmap icon, Rectangle box, float opacity)
    {
        if (opacity >= 1f) { g.DrawImage(icon, box); return; }

        var matrix = new System.Drawing.Imaging.ColorMatrix { Matrix33 = opacity };
        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        attributes.SetColorMatrix(matrix);

        g.DrawImage(icon, box, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, attributes);
    }

    private void PaintTray(Graphics g)
    {
        using var hot = new SolidBrush(Theme.Raised);
        var items = TrayItems();

        for (int i = 0; i < items.Count; i++)
        {
            var area = TrayAt(i);
            if (i == hoveredStatus) g.FillRectangle(hot, area);

            switch (items[i])
            {
                case Tray.Home: PaintHome(g, area); break;
                case Tray.Volume when volume is { } v: StatusGlyphs.Volume(g, area, v.Percent, v.Muted, Theme.Text); break;
                case Tray.Network: StatusGlyphs.Network(g, area, link, Theme.Text); break;
                case Tray.Battery when battery is { } b: StatusGlyphs.Battery(g, area, b.Percent, b.Charging, Theme.Text); break;
            }
        }

        if (settings.ShowStatus) PaintClock(g);
    }

    /// <summary>Our own split-screen glyph, the same one the tray icon uses.</summary>
    private void PaintHome(Graphics g, Rectangle area)
    {
        int size = Math.Clamp(Math.Min(area.Width, area.Height) / 2, 12, 22);
        var box = new Rectangle(area.X + (area.Width - size) / 2, area.Y + (area.Height - size) / 2, size, size);

        int split = (int)Math.Round(box.Width * 0.68);
        using var major = new SolidBrush(Theme.Accent);
        using var minor = new SolidBrush(Theme.Muted);

        g.FillRectangle(major, box.X, box.Y, split - 1, box.Height);
        g.FillRectangle(minor, box.X + split + 1, box.Y, box.Width - split - 1, box.Height);
    }

    private void PaintClock(Graphics g)
    {
        using var time = Theme.Face(Vertical ? 12f : 13f, FontStyle.Bold);
        using var date = Theme.Face(Vertical ? 8f : 9f);
        using var brush = new SolidBrush(Theme.Text);
        using var faint = new SolidBrush(Theme.Muted);
        using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        var area = ClockArea;
        var now = DateTime.Now;

        // Two thirds to the time, one to the date: the time is what anyone is actually reading.
        int split = (int)(area.Height * 0.58);
        g.DrawString(now.ToString("HH:mm"), time, brush,
                     new Rectangle(area.X, area.Y + 2, area.Width, split), centred);
        g.DrawString(now.ToString(Vertical ? "d MMM" : "ddd d MMM"), date, faint,
                     new Rectangle(area.X, area.Y + split, area.Width, area.Height - split - 2), centred);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refresh.Dispose();
            tips.Dispose();
            appBar.Dispose();          // must happen, or the desktop stays short of the strip
            foreach (var icon in icons.Values) icon.Dispose();
            icons.Clear();
            foreach (var icon in fileIcons.Values) icon.Dispose();
            fileIcons.Clear();
        }
        base.Dispose(disposing);
    }
}
