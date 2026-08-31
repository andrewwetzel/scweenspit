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

    private readonly TaskbarPreview preview = new();

    /// <summary>
    /// Long enough that running the pointer along the bar does not fire eight of them, short enough
    /// that resting on a button feels like asking.
    /// </summary>
    private readonly System.Windows.Forms.Timer hoverDelay = new() { Interval = 350 };

    /// <summary>The button the pending preview is for.</summary>
    private int pending = -1;

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

        /// <summary>
        /// A name for a person. The shell's, where the application has an id it knows — the process
        /// name is the browser's for anything hosted in one, so a PWA would read as "chrome".
        /// Otherwise the process, since an application id is not a name.
        /// </summary>
        public string Label =>
            First is { AppId.Length: > 0 } w && ShellIcon.NameForAppId(w.AppId) is { } known ? known
            : First?.Process is { Length: > 0 } name ? name
            : NameOf(Id);

        /// <summary>
        /// What every window of this application is announcing, added up. Six Chrome windows behind
        /// one icon are one button, and two waiting messages in each of them are four waiting
        /// messages — a badge showing whichever window happened to be first would be a smaller
        /// number than the truth.
        /// </summary>
        public int Badge => Windows.Sum(w => Badges.Count(w.Title) ?? 0);
    }

    private List<TaskWindow> windows = [];
    private List<Button> buttons = [];

    // Arrival order, kept across rebuilds. EnumWindows returns z-order, which changes every time
    // anything is activated or minimised, so using it directly makes the buttons reshuffle
    // underneath the pointer.
    private readonly List<IntPtr> order = [];

    private readonly Dictionary<IntPtr, Bitmap> icons = [];

    /// <summary>Handles wearing a stand-in, and how many times we have looked for the real thing.</summary>
    private readonly Dictionary<IntPtr, int> standIn = [];

    /// <summary>Ten seconds of asking. An application without an icon is not going to grow one.</summary>
    private const int IconAttempts = 10;

    // Which window of each group is next. Clicking a grouped icon walks its windows, the way a
    // Plasma task manager does, rather than always raising the same one.
    private readonly Dictionary<string, int> cycle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> fileIcons = new(StringComparer.OrdinalIgnoreCase);
    private int hovered = -1, hoveredStatus = -1;
    private bool hoveredStart;
    private bool hoveredUsage;
    private bool hoveredMachine;
    private long lastStatusPoll, lastMachinePoll;

    /// <summary>The machine's last reading. Kept, because the processor figure is a rate: asking for
    /// it at paint time would difference two readings a few milliseconds apart and report noise.</summary>
    private IReadOnlyList<Meter> machine = [];

    private (int Percent, bool Charging)? battery;
    private (int Percent, bool Muted)? volume;
    private LinkKind link;

    public BarEdge Edge { get; }
    public string Device => monitor.Device;

    /// <summary>True when this bar wants the Start menu brought over to it.</summary>
    public bool AnchorsStartMenu => settings.ShowStartButton && settings.MoveStartMenu;

    /// <summary>Where this bar would like the Start menu, in screen pixels.</summary>
    public StartMenu.Anchor StartAnchor => new(RectangleToScreen(StartSlot), Bounds, Edge, monitor.Bounds,
        settings.Floating ? Math.Max(4, settings.FloatMargin) : 4);

    /// <summary>True when <paramref name="p"/> is on this bar's display.</summary>
    public bool Covers(Point p) =>
        p.X >= monitor.Bounds.Left && p.X < monitor.Bounds.Right &&
        p.Y >= monitor.Bounds.Top && p.Y < monitor.Bounds.Bottom;

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
        preview.Chosen += window => { WindowList.Raise(window.Handle); Rebuild(); };
        hoverDelay.Tick += (_, _) => { hoverDelay.Stop(); ShowPreview(pending); };

        refresh.Tick += (_, _) =>
        {
            Rebuild();
            // Piggybacked rather than given a timer of its own: it is one window lookup, and it does
            // nothing at all unless the shell has restarted since the hook went on.
            if (AnchorsStartMenu) StartMenu.EnsureWatching();
        };
        refresh.Start();
    }

    /// <summary>
    /// Re-negotiates the reserved strip. Needed whenever the space available changes underneath us —
    /// most obviously when the shell's taskbar is hidden and its reservation goes away, which would
    /// otherwise leave our bar stranded above an empty band where the old taskbar used to be.
    /// </summary>
    public void Reposition()
    {
        int open = settings.Floating ? settings.FloatMargin : 0;
        int ends = settings.Floating ? settings.SideGap : 0;
        int edgeGap = settings.Floating ? settings.EdgeGap : 0;

        if (zones.BarStrip(monitor, settings) is { } strip)
        {
            // Not an appbar: Windows reserves space as one rectangle per monitor, so a bar across
            // part of an edge cannot be expressed that way. The zone is shortened for it instead.
            var placed = BarGeometry.Deflate(strip, Edge, open, ends, edgeGap);
            SetWindowPos(Handle, HWND_TOPMOST, placed.Left, placed.Top, placed.Width, placed.Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Log.Write($"bar on {monitor.Device} zone {settings.Zone}: {placed}");
            return;
        }

        // Reserve the bar plus the two gaps along the docking axis; the window is then inset back
        // to the thickness asked for. The gap at the ends runs the other way and costs nothing.
        appBar.Reserve(monitor.Bounds, Edge, settings.Thickness + open + edgeGap, open, ends, edgeGap);
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

    /// <summary>
    /// Where an icon sits in a slot. Shared, so the Start glyph cannot drift a pixel or two out of
    /// line with the applications beside it — which is the one place on a bar it would be noticed.
    /// </summary>
    private Rectangle IconBox(Rectangle slot)
    {
        int size = IconSize;
        var within = IconArea(slot);
        return settings.IconsOnly
            ? new Rectangle(within.X + (within.Width - size) / 2, within.Y + (within.Height - size) / 2, size, size)
            : new Rectangle(within.X + 8, within.Y + (within.Height - size) / 2, size, size);
    }

    // ---- contents ----------------------------------------------------------

    private void Rebuild()
    {
        windows = WindowList.Enumerate(settings.ThisDisplayOnly ? monitor.Device : null);

        order.RemoveAll(h => windows.All(w => w.Handle != h));
        foreach (var w in windows)
            if (!order.Contains(w.Handle)) order.Add(w.Handle);

        buttons = LayOutButtons();

        foreach (var w in windows) Adopt(w);

        foreach (var button in buttons)
            if (!button.Running && !fileIcons.ContainsKey(button.Id))
                fileIcons[button.Id] = WindowList.IconForFile(button.Id)
                                       ?? ShellIcon.ForAppId(button.Id)
                                       ?? LetterTile(button.Label);

        foreach (var stale in icons.Keys.Where(h => !IsWindow(h)).ToList())
        {
            icons[stale].Dispose();
            icons.Remove(stale);
            standIn.Remove(stale);
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

        // On its own schedule, and only while it is being shown: the processor figure is the
        // difference between consecutive readings, so how often it is taken decides what it means.
        // Two seconds is slow enough to read and long enough to average a spike into something true.
        if (MachineVisible && now - lastMachinePoll > 2000)
        {
            lastMachinePoll = now;
            machine = MachineLoad.Read();
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
    private bool MachineVisible => settings.ShowMachine;

    private int UsageExtent => UsageVisible ? UsageStrip.Extent(Vertical) : 0;
    private int MachineExtent => MachineVisible ? MeterStrip.Extent(Vertical) : 0;

    /// <summary>Room the cluster needs, so window buttons know where to stop.</summary>
    private int StatusExtent => UsageExtent + MachineExtent + TrayItems().Count * StatusIcon + ClockExtent;

    private int StatusStart => Math.Max(0, (Vertical ? Height : Width) - StatusExtent);

    /// <summary>The strip leads the cluster, so the icons and clock keep the positions they had.</summary>
    private Rectangle UsageArea => Vertical
        ? new Rectangle(0, StatusStart, Width, UsageExtent)
        : new Rectangle(StatusStart, 0, UsageExtent, Height);

    private Rectangle MachineArea => Vertical
        ? new Rectangle(0, StatusStart + UsageExtent, Width, MachineExtent)
        : new Rectangle(StatusStart + UsageExtent, 0, MachineExtent, Height);

    private int TrayStart => StatusStart + UsageExtent + MachineExtent;

    private Rectangle TrayAt(int index) => Vertical
        ? new Rectangle(0, TrayStart + index * StatusIcon, Width, StatusIcon)
        : new Rectangle(TrayStart + index * StatusIcon, 0, StatusIcon, Height);

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
    /// <summary>
    /// Takes the best icon a window has to offer, and keeps asking while it is only offering a
    /// stand-in. Applications commonly set their icon a moment after the window appears, so the
    /// first look is often too early — and this used to be the only look there was.
    /// </summary>
    private void Adopt(TaskWindow window)
    {
        bool known = icons.ContainsKey(window.Handle);
        int looks = standIn.GetValueOrDefault(window.Handle);

        if (known && looks == 0) return;                // settled on the window's own icon
        if (known && looks >= IconAttempts) return;     // asked enough; it is not going to have one

        var (art, real) = Artwork(window);

        if (known && !real)
        {
            // No better than what is already there. Count the look and keep the one we have, rather
            // than swapping one stand-in for an identical one every second.
            art.Dispose();
            standIn[window.Handle] = looks + 1;
            return;
        }

        if (known) icons[window.Handle].Dispose();
        icons[window.Handle] = art;

        if (real) standIn.Remove(window.Handle);
        else standIn[window.Handle] = 1;
    }

    /// <summary>
    /// The best icon a window has, and whether it is really the window's own. In order of how
    /// specific each source is — a blank square used to be the last resort, which is how an
    /// application with no window icon ended up as an empty space on the bar.
    /// </summary>
    private (Bitmap Art, bool Real) Artwork(TaskWindow window)
    {
        if (WindowList.IconFor(window.Handle) is { } own) return (own, true);
        if (ShellIcon.ForAppId(window.AppId) is { } registered) return (registered, true);

        // Not the executable's icon when the window declares an application id of its own: a PWA
        // whose icon cannot be found is still not the browser, and wearing the browser's icon is
        // exactly the confusion that grouping by application id exists to prevent.
        if (window.AppId.Length == 0 && WindowList.IconForFile(window.Path) is { } file)
            return (file, true);

        // Named after itself, not after its host: a stand-in reading "C" beside the browser's own
        // "C" identifies nothing.
        var named = window.AppId.Length > 0
            ? ShellIcon.NameForAppId(window.AppId) ?? window.Title
            : window.Process is { Length: > 0 } p ? p : window.Title;

        return (LetterTile(named), false);
    }

    /// <summary>
    /// A count on the corner of an icon, the way every taskbar has drawn one. Sized from the icon
    /// rather than fixed, so it stays in proportion on a bar of any thickness.
    /// </summary>
    private void PaintBadge(Graphics g, Rectangle icon, int count)
    {
        var text = Badges.Text(count);

        int height = Math.Clamp(icon.Height * 5 / 9, 12, 22);
        using var font = Theme.Face(height * 0.46f, FontStyle.Bold);

        // Wide enough for what is in it: a circle for one digit, a lozenge for more, because two
        // digits squeezed into a circle stop being readable at this size.
        int width = Math.Max(height, (int)Math.Ceiling(g.MeasureString(text, font).Width) + height / 2);

        // Top-right, and allowed to overhang: the corner of an icon is the least of it, and keeping
        // the badge wholly inside would shrink it below reading size.
        var box = new Rectangle(icon.Right - width + height / 4, icon.Top - height / 4, width, height);

        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var ring = new SolidBrush(Theme.Panel))
            Lozenge(g, Rectangle.Inflate(box, 2, 2), ring);   // a gap, so it reads as sitting on top

        using (var fill = new SolidBrush(Badge))
            Lozenge(g, box, fill);

        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        using var ink = new SolidBrush(Color.White);
        g.DrawString(text, font, ink, box, format);

        g.SmoothingMode = previous;
    }

    /// <summary>The colour of a waiting count. Loud on purpose — that is the whole job.</summary>
    private static readonly Color Badge = Color.FromArgb(0xE0, 0x3B, 0x3B);

    private static void Lozenge(Graphics g, Rectangle box, Brush brush)
    {
        int radius = box.Height / 2;
        if (radius <= 1) { g.FillRectangle(brush, box); return; }

        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(box.X, box.Y, d, d, 90, 180);
        path.AddArc(box.Right - d, box.Y, d, d, 270, 180);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

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

    /// <summary>Slots taken before the window buttons begin.</summary>
    private int Leading => settings.ShowStartButton ? 1 : 0;

    private Rectangle StartSlot => SlotAt(0);

    private int Capacity => Math.Max(0, StatusStart / Math.Max(1, Slot) - Leading);

    private int SlotUnder(Point p)
    {
        for (int i = 0; i < Math.Min(buttons.Count, Capacity); i++)
            if (SlotAt(i + Leading).Contains(p)) return i;
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
        bool load = MachineVisible && MachineArea.Contains(e.Location);
        bool start = settings.ShowStartButton && StartSlot.Contains(e.Location);
        if (under == hovered && tray == hoveredStatus && usage == hoveredUsage
            && load == hoveredMachine && start == hoveredStart) return;

        hovered = under;
        hoveredStatus = tray;
        hoveredUsage = usage;
        hoveredMachine = load;
        hoveredStart = start;
        Cursor = under >= 0 || tray >= 0 || usage || load || start ? Cursors.Hand : Cursors.Default;

        // An icons-only bar is unreadable without these. The strip needs one whatever the bar looks
        // like: five pixels of colour cannot say which limit it is, or when it resets. A button
        // showing a preview needs no tooltip — and would otherwise show both at once.
        bool previewing = Previewable(under);
        tips.SetToolTip(this, start ? "Start"
                            : load ? MachineTip()
                            : usage ? UsageStrip.Tip(ClaudeUsage.Current)
                            : tray >= 0 ? TrayTip(TrayItems()[tray])
                            : under >= 0 && under < buttons.Count && !previewing ? Describe(buttons[under])
                            : string.Empty);

        TrackPreview(under, previewing);
        Invalidate();
    }

    /// <summary>Worth a preview: a running application, and previews turned on.</summary>
    private bool Previewable(int slot) =>
        settings.ShowPreviews && slot >= 0 && slot < buttons.Count && buttons[slot].Running;

    /// <summary>
    /// Opens, moves, or closes the preview as the pointer travels along the bar. Moving between two
    /// buttons swaps it at once — the delay is there to stop a pointer crossing the bar from opening
    /// one at all, and having served that it would only be in the way.
    /// </summary>
    private void TrackPreview(int slot, bool previewable)
    {
        if (!previewable)
        {
            hoverDelay.Stop();
            pending = -1;
            if (preview.Visible) preview.Dismiss();
            return;
        }

        if (slot == pending) return;
        pending = slot;

        if (preview.Visible) ShowPreview(slot);
        else { hoverDelay.Stop(); hoverDelay.Start(); }
    }

    private void ShowPreview(int slot)
    {
        if (!Previewable(slot)) return;

        preview.Open(buttons[slot].Windows, RectangleToScreen(SlotAt(slot + Leading)), Bounds, Edge,
                     monitor.Bounds, settings.Floating ? Math.Max(4, settings.FloatMargin) : 4);
    }

    /// <summary>The machine's reading in words, where the figures beside the bars cannot fit.</summary>
    private string MachineTip() =>
        machine.Count == 0 ? "This machine — reading…" : string.Join(Environment.NewLine, machine.Select(m => m.Detail));

    private static string Describe(Button b)
    {
        var text = b.Windows.Count switch
        {
            0 => $"{b.Label}  (not running)",
            1 => b.Windows[0].Minimised ? $"{b.Windows[0].Title}  (minimised)" : b.Windows[0].Title,
            _ => $"{b.Label} — {b.Windows.Count} windows\n" +
                 string.Join("\n", b.Windows.Take(6).Select(w => "  " + w.Title)),
        };

        // Said in words as well as drawn: "3" on a corner does not say three of what, and the title
        // it was read from is right there in the same tooltip to make the connection.
        return b.Badge > 0 ? $"{text}\n\n{b.Badge} waiting" : text;
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
        hoveredStart = false;
        hoveredUsage = false;
        hoveredMachine = false;

        // The preview closes itself once the pointer is over neither it nor the button: leaving the
        // bar is how you reach it, so a leave here cannot mean it is finished with.
        hoverDelay.Stop();
        pending = -1;

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

        if (settings.ShowStartButton && StartSlot.Contains(e.Location))
        {
            StartMenu.Press();
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

        if (MachineVisible && MachineArea.Contains(e.Location))
        {
            // Task Manager is where anyone would go next, and it opens with no taskbar at all.
            SystemStatus.Open("taskmgr.exe");
            return;
        }

        if (ClockArea.Contains(e.Location)) { SystemStatus.Open("ms-settings:dateandtime"); return; }

        int under = SlotUnder(e.Location);
        if (under < 0 || under >= buttons.Count) return;

        var button = buttons[under];

        // Whatever the click does next, it is not "keep showing me what this window looks like".
        preview.Dismiss();

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

    /// <summary>
    /// Right-click: the windows themselves, then pin, then close. Grouping hides the individual
    /// windows behind one icon, so a grouped button has to give a way back to them — going through
    /// them one click at a time is fine for two and useless for six.
    /// </summary>
    private void ShowButtonMenu(Button button, Point at)
    {
        var menu = Theme.Menu();
        bool pinned = settings.Pinned.Any(p => Same(p, button.Id));

        if (button.Windows.Count > 1)
        {
            foreach (var window in button.Windows)
            {
                var title = window.Title is { Length: > 0 } t ? t : button.Label;
                var item = new ToolStripMenuItem(Shorten(title), null, (_, _) => WindowList.Raise(window.Handle));
                if (window.Minimised) item.ForeColor = Theme.Muted;
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
        }

        if (!string.IsNullOrWhiteSpace(button.Id))
        {
            menu.Items.Add(new ToolStripMenuItem("New window", null, (_, _) => Launch(button.Id)));

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

        if (menu.Items.Count > 0) menu.Show(at); else menu.Dispose();
    }

    /// <summary>A window title is a sentence often enough that a menu built from them is unusable.</summary>
    private static string Shorten(string title) =>
        title.Length <= 60 ? title : title[..57].TrimEnd() + "…";

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

        if (settings.ShowStartButton) PaintStart(g, hot);

        int shown = Math.Min(buttons.Count, Capacity);
        for (int i = 0; i < shown; i++)
        {
            var slot = SlotAt(i + Leading);
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

            int badge = settings.ShowBadges ? button.Badge : 0;

            if (icon is not null)
            {
                var box = IconBox(slot);

                // A pinned app that is not running, and a minimised window, are both "not here";
                // fading the icon says so without needing a legend.
                float opacity = !button.Running ? 0.45f
                              : button.Windows.All(x => x.Minimised) ? 0.62f
                              : 1f;
                DrawIcon(g, icon, box, opacity);

                // Over the icon, not beside it: the badge belongs to the application, and a slot has
                // no room to put anything next to an icon that already fills it.
                if (badge > 0) PaintBadge(g, box, badge);
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

    /// <summary>
    /// The Windows logo: four panes, the way it has looked since Windows 11. Drawn into the same box
    /// an application icon would get, at the same size, so the row reads as one row.
    /// </summary>
    private void PaintStart(Graphics g, Brush hot)
    {
        var slot = StartSlot;
        if (hoveredStart) g.FillRectangle(hot, slot);

        var box = IconBox(slot);

        // The gutter scales with the glyph; fixed, it swallows a small logo and vanishes in a large
        // one. Odd sizes go to the panes rather than the gutter, so the four stay square and equal.
        int gutter = Math.Max(2, box.Width / 8);
        int pane = (box.Width - gutter) / 2;
        int drawn = 2 * pane + gutter;

        int x = box.X + (box.Width - drawn) / 2;
        int y = box.Y + (box.Height - drawn) / 2;

        using var brush = new SolidBrush(hoveredStart ? Theme.Text : Theme.Muted);
        g.FillRectangle(brush, x, y, pane, pane);
        g.FillRectangle(brush, x + pane + gutter, y, pane, pane);
        g.FillRectangle(brush, x, y + pane + gutter, pane, pane);
        g.FillRectangle(brush, x + pane + gutter, y + pane + gutter, pane, pane);
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

        if (MachineVisible)
        {
            var strip = MachineArea;
            if (hoveredMachine) g.FillRectangle(hot, strip);
            MeterStrip.Paint(g, strip, machine);
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
            hoverDelay.Dispose();
            tips.Dispose();
            preview.Dispose();         // unregisters its thumbnails with it
            appBar.Dispose();          // must happen, or the desktop stays short of the strip
            foreach (var icon in icons.Values) icon.Dispose();
            icons.Clear();
            foreach (var icon in fileIcons.Values) icon.Dispose();
            fileIcons.Clear();
        }
        base.Dispose(disposing);
    }
}
