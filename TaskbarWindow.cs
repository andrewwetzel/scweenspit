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

    private List<TaskWindow> windows = [];
    private readonly Dictionary<IntPtr, Bitmap> icons = [];
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

    public TaskbarWindow(MonitorGeometry monitor, BarSettings settings)
    {
        this.monitor = monitor;
        this.settings = settings;
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

        Load += (_, _) => { appBar.Register(); Reposition(); Rebuild(); };
        refresh.Tick += (_, _) => Rebuild();
        refresh.Start();
    }

    /// <summary>
    /// Re-negotiates the reserved strip. Needed whenever the space available changes underneath us —
    /// most obviously when the shell's taskbar is hidden and its reservation goes away, which would
    /// otherwise leave our bar stranded above an empty band where the old taskbar used to be.
    /// </summary>
    public void Reposition() => appBar.Reserve(monitor.Bounds, Edge, settings.Thickness);

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

        foreach (var w in windows)
            if (!icons.ContainsKey(w.Handle))
                icons[w.Handle] = WindowList.IconFor(w.Handle) ?? new Bitmap(32, 32);

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

    private Rectangle SlotAt(int index) => Vertical
        ? new Rectangle(0, index * Slot, Width, Slot)
        : new Rectangle(index * Slot, 0, Slot, Height);

    private int Capacity => Math.Max(0, StatusStart / Math.Max(1, Slot));

    private int SlotUnder(Point p)
    {
        for (int i = 0; i < Math.Min(windows.Count, Capacity); i++)
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
                            : under >= 0 && under < windows.Count ? windows[under].Title
                            : string.Empty);
        Invalidate();
    }

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
        if (under < 0 || under >= windows.Count) return;

        WindowList.Toggle(windows[under].Handle);
        Rebuild();
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

        int shown = Math.Min(windows.Count, Capacity);
        for (int i = 0; i < shown; i++)
        {
            var slot = SlotAt(i);
            var w = windows[i];

            if (w.Handle == foreground) g.FillRectangle(active, slot);
            else if (i == hovered) g.FillRectangle(hot, slot);

            if (w.Handle == foreground || !w.Minimised)
            {
                // Running windows get an underline; the foreground one gets a brighter, longer one.
                int len = w.Handle == foreground ? Slot / 2 : Slot / 5;
                var mark = Vertical
                    ? new Rectangle(Edge == BarEdge.Left ? 0 : Width - 3, slot.Y + (slot.Height - len) / 2, 3, len)
                    : new Rectangle(slot.X + (slot.Width - len) / 2, Edge == BarEdge.Top ? 0 : Height - 3, len, 3);
                g.FillRectangle(w.Handle == foreground ? accent : dim, mark);
            }

            if (icons.TryGetValue(w.Handle, out var icon))
            {
                int size = IconSize;
                var box = settings.IconsOnly
                    ? new Rectangle(slot.X + (slot.Width - size) / 2, slot.Y + (slot.Height - size) / 2, size, size)
                    : new Rectangle(slot.X + 8, slot.Y + (slot.Height - size) / 2, size, size);
                g.DrawImage(icon, box);
            }

            if (settings.IconsOnly) continue;

            var caption = Vertical
                ? new Rectangle(slot.X + IconSize + 14, slot.Y, slot.Width - IconSize - 20, slot.Height)
                : new Rectangle(slot.X + IconSize + 14, slot.Y, slot.Width - IconSize - 20, slot.Height);
            using var format = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            g.DrawString(w.Title, label, w.Minimised ? dim : text, caption, format);
        }

        PaintTray(g);
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
        }
        base.Dispose(disposing);
    }
}
