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

    /// <summary>
    /// A slot on the bar: one application, with however many windows it has open. Grouping by
    /// application rather than by window is what keeps a bar of icons readable — six Chrome windows
    /// are one thing you switch to, not six.
    /// </summary>
    private sealed record Button(string Id, List<TaskWindow> Windows, bool Pinned)
    {
        public TaskWindow? First => Windows.Count > 0 ? Windows[0] : null;
        public bool Running => Windows.Count > 0;

        /// <summary>A name for a person. An application id is not one, so prefer the process.</summary>
        public string Label => First?.Process is { Length: > 0 } name ? name : NameOf(Id);
    }

    private List<TaskWindow> windows = [];
    private List<Button> buttons = [];

    // Arrival order, kept across rebuilds. EnumWindows returns z-order, which changes every time
    // anything is activated or minimised, so using it directly makes the buttons reshuffle
    // underneath the pointer.
    private readonly List<IntPtr> order = [];

    private readonly Dictionary<IntPtr, Bitmap> icons = [];

    // Which window of each group is next. Clicking a grouped icon walks its windows, the way a
    // Plasma task manager does, rather than always raising the same one.
    private readonly Dictionary<string, int> cycle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> fileIcons = new(StringComparer.OrdinalIgnoreCase);
    private int hovered = -1, hoveredStatus = -1;
    private bool hoveredUsage;
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

        // Reserve the bar plus its gap; the window is then inset back to the thickness asked for.
        appBar.Reserve(monitor.Bounds, Edge, settings.Thickness + 2 * inset, inset);
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
    /// <summary>Thin strip at the docked edge that the window marks live in.</summary>
    private const int MarkStrip = 5;

    /// <summary>
    /// Icons take most of the bar rather than half of it. Half leaves as much empty space as icon,
    /// which reads as a gap rather than as breathing room, and the marks at the docked edge tip the
    /// balance further towards that edge.
    /// </summary>
    private int IconSize => Math.Clamp((Breadth - MarkStrip) * 5 / 8, 16, 40);

    /// <summary>
    /// The part of a slot the icon is centred in: everything except the mark strip. Centring on the
    /// whole slot pushes the icon towards the docked edge visually, because the marks fill the space
    /// on that side and nothing fills it on the other.
    /// </summary>
    private Rectangle IconArea(Rectangle slot) => Edge switch
    {
        BarEdge.Bottom => slot with { Height = slot.Height - MarkStrip },
        BarEdge.Top => new Rectangle(slot.X, slot.Y + MarkStrip, slot.Width, slot.Height - MarkStrip),
        BarEdge.Right => slot with { Width = slot.Width - MarkStrip },
        _ => new Rectangle(slot.X + MarkStrip, slot.Y, slot.Width - MarkStrip, slot.Height),
    };

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
            if (!button.Running && !fileIcons.ContainsKey(button.Id))
                fileIcons[button.Id] = WindowList.IconForFile(button.Id) ?? LetterTile(button.Label);

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

    /// <summary>
    /// Whether the claude.ai strip is drawn here: this bar has to want it, and the account has to be
    /// configured. An enabled-but-keyless strip would be a permanent placeholder taking up room.
    /// </summary>
    /// <summary>
    /// Shown whenever the bar is set to show it. Deliberately not also gated on usage tracking being
    /// configured: hiding it until a session key exists means ticking the box appears to do nothing,
    /// with no hint that anything further is needed. The strip renders its own unconfigured state.
    /// </summary>
    private bool UsageVisible => settings.ShowUsage;

    private int UsageExtent => UsageVisible ? UsageStrip.Extent(Vertical) : 0;

    /// <summary>Room the cluster needs, so window buttons know where to stop.</summary>
    private int StatusExtent => UsageExtent + TrayItems().Count * StatusIcon + ClockExtent;

    private int StatusStart => Math.Max(0, (Vertical ? Height : Width) - StatusExtent);

    /// <summary>The strip leads the cluster, so the icons and clock keep the positions they had.</summary>
    private Rectangle UsageArea => Vertical
        ? new Rectangle(0, StatusStart, Width, UsageExtent)
        : new Rectangle(StatusStart, 0, UsageExtent, Height);

    private Rectangle TrayAt(int index) => Vertical
        ? new Rectangle(0, StatusStart + UsageExtent + index * StatusIcon, Width, StatusIcon)
        : new Rectangle(StatusStart + UsageExtent + index * StatusIcon, 0, StatusIcon, Height);

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

        // Group the running windows by application first, keeping the order they appeared in.
        var groups = new List<(string Id, List<TaskWindow> Windows)>();
        foreach (var w in live)
        {
            var existing = groups.FindIndex(g => Same(g.Id, w.GroupId));
            if (existing >= 0) groups[existing].Windows.Add(w);
            else groups.Add((w.GroupId, [w]));
        }

        // Pins are matched in two passes. An exact match on the declared application id wins
        // outright; only then may a pin that is a plain executable claim a group by its file. That
        // ordering is what lets a pinned browser and a pinned web app that are both chrome.exe end
        // up on their own buttons rather than fighting over the first one.
        var claimed = new bool[groups.Count];
        var forPin = new int[settings.Pinned.Count];
        Array.Fill(forPin, -1);

        for (int p = 0; p < settings.Pinned.Count; p++)
        {
            int at = groups.FindIndex(g => Same(g.Id, settings.Pinned[p]));
            if (at >= 0 && !claimed[at]) { forPin[p] = at; claimed[at] = true; }
        }

        for (int p = 0; p < settings.Pinned.Count; p++)
        {
            if (forPin[p] >= 0) continue;

            int at = -1;
            for (int g = 0; g < groups.Count && at < 0; g++)
                if (!claimed[g] && groups[g].Windows.Count > 0
                    && Same(groups[g].Windows[0].Path, settings.Pinned[p])) at = g;

            if (at >= 0) { forPin[p] = at; claimed[at] = true; }
        }

        var laid = new List<Button>();

        for (int p = 0; p < settings.Pinned.Count; p++)
            laid.Add(new Button(settings.Pinned[p],
                                forPin[p] >= 0 ? groups[forPin[p]].Windows : [], true));

        for (int i = 0; i < groups.Count; i++)
            if (!claimed[i]) laid.Add(new Button(groups[i].Id, groups[i].Windows, false));

        return laid;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A readable name for a pinned entry. Usually a file, but an application id for something like
    /// a web app — and the leading segment of one of those is the closest thing it has to a name.
    /// </summary>
    private static string NameOf(string id)
    {
        try
        {
            if (File.Exists(id) || id.Contains('\\') || id.Contains('/'))
                return Path.GetFileNameWithoutExtension(id);

            var head = id.Split('.')[0];
            return head.Length > 0 ? head : id;
        }
        catch { return id; }
    }

    /// <summary>
    /// Stands in for an icon we cannot get. A pinned web app is identified by an application id
    /// rather than a file, so there is nothing to extract from until it is running — and a blank
    /// square says less than a letter does.
    /// </summary>
    private static Bitmap LetterTile(string label)
    {
        var tile = new Bitmap(32, 32);
        using var g = Graphics.FromImage(tile);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        using var back = new SolidBrush(Theme.Raised);
        g.FillEllipse(back, 1, 1, 30, 30);

        var letter = (label.Length > 0 ? label[..1] : "?").ToUpperInvariant();
        using var font = new Font(FontFamily.GenericSansSerif, 17f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Theme.Muted);
        using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        g.DrawString(letter, font, ink, new Rectangle(0, 0, 32, 32), centred);
        return tile;
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
        bool usage = UsageVisible && UsageArea.Contains(e.Location);
        if (under == hovered && tray == hoveredStatus && usage == hoveredUsage) return;

        hovered = under;
        hoveredStatus = tray;
        hoveredUsage = usage;
        Cursor = under >= 0 || tray >= 0 || usage ? Cursors.Hand : Cursors.Default;

        // An icons-only bar is unreadable without these. The strip needs one whatever the bar looks
        // like: five pixels of colour cannot say which limit it is, or when it resets.
        tips.SetToolTip(this, usage ? UsageStrip.Tip(ClaudeUsage.Current)
                            : tray >= 0 ? TrayTip(TrayItems()[tray])
                            : under >= 0 && under < buttons.Count ? Describe(buttons[under])
                            : string.Empty);
        Invalidate();
    }

    private static string Describe(Button b) => b.Windows.Count switch
    {
        0 => $"{b.Label}  (not running)",
        1 => b.Windows[0].Minimised ? $"{b.Windows[0].Title}  (minimised)" : b.Windows[0].Title,
        _ => $"{b.Label} — {b.Windows.Count} windows\n" +
             string.Join("\n", b.Windows.Take(6).Select(w => "  " + w.Title)),
    };

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
        hoveredUsage = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (UsageVisible && UsageArea.Contains(e.Location))
        {
            // Not set up yet: the useful destination is our own settings, not the website that
            // cannot tell you anything until a key is in place.
            if (ClaudeUsage.Enabled) SystemStatus.Open(ClaudeUsage.UsagePage);
            else MenuRequested?.Invoke(Cursor.Position);
            return;
        }

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

        Activate(button);
        Rebuild();
    }

    /// <summary>
    /// One window behaves like a taskbar button — raise it, or put it away if it is already in
    /// front. Several, and each click moves to the next: minimising the group on every second click
    /// would make cycling through three windows take six.
    /// </summary>
    private void Activate(Button button)
    {
        if (!button.Running) { Launch(button.Id); return; }
        if (button.Windows.Count == 1) { WindowList.Toggle(button.Windows[0].Handle); return; }

        var foreground = GetForegroundWindow();
        int current = button.Windows.FindIndex(w => w.Handle == foreground);

        int next = current >= 0
            ? (current + 1) % button.Windows.Count
            : cycle.TryGetValue(button.Id, out var last) ? last % button.Windows.Count : 0;

        cycle[button.Id] = next;

        WindowList.Raise(button.Windows[next].Handle);
    }

    /// <summary>
    /// Starts a pinned entry. It is a file for an ordinary application, and an application id for
    /// something like a PWA — which the shell can start from its apps folder, where a bare path
    /// would only launch the browser it happens to live in.
    /// </summary>
    private static void Launch(string id)
    {
        try
        {
            var target = File.Exists(id) ? id : $"shell:AppsFolder\\{id}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write($"could not launch {id}: {ex.Message}"); }
    }

    /// <summary>Right-click: pin, unpin, or close — the three things a taskbar button owes you.</summary>
    private void ShowButtonMenu(Button button, Point at)
    {
        var menu = new ContextMenuStrip();
        bool pinned = settings.Pinned.Any(p => Same(p, button.Id));

        if (!string.IsNullOrWhiteSpace(button.Id))
        {
            menu.Items.Add(new ToolStripMenuItem(pinned ? "Unpin from bar" : "Pin to bar", null, (_, _) =>
            {
                if (pinned) settings.Pinned.RemoveAll(p => Same(p, button.Id));
                else settings.Pinned.Add(button.Id);

                PinsChanged?.Invoke();
                Rebuild();
            }));
        }

        if (button.Running)
        {
            menu.Items.Add(new ToolStripSeparator());

            var label = button.Windows.Count == 1 ? "Close window" : $"Close all {button.Windows.Count} windows";
            menu.Items.Add(new ToolStripMenuItem(label, null, (_, _) =>
            {
                foreach (var w in button.Windows) WindowList.Close(w.Handle);
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
            var w = button.First;

            bool front = button.Windows.Any(x => x.Handle == foreground);
            if (front) g.FillRectangle(active, slot);
            else if (i == hovered) g.FillRectangle(hot, slot);

            // One mark per window, up to four: a glance says both "running" and "how many", which
            // is the whole point of grouping them behind one icon.
            if (button.Running) PaintWindowMarks(g, slot, button, front, accent, dim);

            var icon = button.Running
                ? icons.GetValueOrDefault(w!.Handle)
                : fileIcons.GetValueOrDefault(button.Id);

            if (icon is not null)
            {
                int size = IconSize;
                var within = IconArea(slot);
                var box = settings.IconsOnly
                    ? new Rectangle(within.X + (within.Width - size) / 2, within.Y + (within.Height - size) / 2, size, size)
                    : new Rectangle(within.X + 8, within.Y + (within.Height - size) / 2, size, size);

                // A pinned app that is not running, and a minimised window, are both "not here";
                // fading the icon says so without needing a legend.
                float opacity = !button.Running ? 0.45f
                              : button.Windows.All(x => x.Minimised) ? 0.62f
                              : 1f;
                DrawIcon(g, icon, box, opacity);
            }

            if (settings.IconsOnly) continue;

            var within2 = IconArea(slot);
            var caption = new Rectangle(within2.X + IconSize + 14, within2.Y,
                                        within2.Width - IconSize - 20, within2.Height);
            using var format = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            var caption2 = button.Windows.Count > 1
                ? $"{button.Label} ({button.Windows.Count})"
                : w?.Title ?? button.Label;

            g.DrawString(caption2, label, !button.Running || w!.Minimised ? dim : text, caption, format);
        }

        PaintTray(g);
    }

    /// <summary>
    /// The underline, split into one segment per open window. Plasma does this and it reads
    /// instantly: a single bar is one window, three stubs are three.
    /// </summary>
    private void PaintWindowMarks(Graphics g, Rectangle slot, Button button, bool front,
                                  Brush accent, Brush dim)
    {
        int count = Math.Min(button.Windows.Count, 4);
        int total = front ? Slot / 2 : Slot / 3;
        int gap = count > 1 ? 3 : 0;
        int each = Math.Max(3, (total - gap * (count - 1)) / count);
        int span = each * count + gap * (count - 1);

        for (int i = 0; i < count; i++)
        {
            int offset = i * (each + gap);
            var mark = Vertical
                ? new Rectangle(Edge == BarEdge.Left ? 0 : Width - 3,
                                slot.Y + (slot.Height - span) / 2 + offset, 3, each)
                : new Rectangle(slot.X + (slot.Width - span) / 2 + offset,
                                Edge == BarEdge.Top ? 0 : Height - 3, each, 3);

            g.FillRectangle(front ? accent : dim, mark);
        }
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

        if (UsageVisible)
        {
            var strip = UsageArea;
            if (hoveredUsage) g.FillRectangle(hot, strip);
            UsageStrip.Paint(g, strip, ClaudeUsage.Current);
        }

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
