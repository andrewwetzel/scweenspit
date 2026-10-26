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
    private readonly System.Windows.Forms.Timer remoteGlowTimer = new() { Interval = 33 };
    private readonly ToolTip tips = new() { InitialDelay = 180, ReshowDelay = 80 };

    private readonly TaskbarPreview preview = new();

    /// <summary>
    /// Long enough that running the pointer along the bar does not fire eight of them, short enough
    /// that resting on a button feels like asking.
    /// </summary>
    private readonly System.Windows.Forms.Timer hoverDelay = new() { Interval = 350 };

    /// <summary>The button the pending preview is for.</summary>
    private int pending = -1;

    private readonly MonitorGeometry monitor;
    private BarSettings settings;
    private readonly ZoneManager zones;

    public void UpdateSettings(BarSettings updated)
    {
        settings = updated;
        Invalidate();
    }

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
    private enum TrackerSection { Usage, ClaudeChats, Antigravity, AntigravityChats, Orchestra, Bambu, Machine }
    private const int DividerGrabRadius = 5;
    private const int MinTrackerExtent = 48;
    private const int MaxTrackerExtent = 1200;

    private int hovered = -1, hoveredStatus = -1;
    private bool hoveredStart;
    private bool hoveredUsage;
    private bool hoveredClaudeChats;
    private bool hoveredAntigravity;
    private bool hoveredAntigravityChats;
    private string? hoveredRemoteChatId;
    private bool hoveredOrchestra;
    private string? hoveredCard;
    private bool hoveredBambu;
    private bool hoveredMachine;
    private TrackerSection? hoveredDivider;
    private TrackerSection? draggingDivider;

    /// <summary>
    /// A left press on a strip, not yet acted on: it is a click if released where it started, and
    /// the start of dragging the strip somewhere else if the pointer moves first.
    /// </summary>
    private (TrackerSection Section, MouseEventArgs Press)? pendingStripPress;
    private TrackerSection? draggingStrip;
    private int stripGrabOffset, stripDragCoord;
    private const int StripDragThreshold = 6;

    /// <summary>Whether the press that is pending closed the remote chats menu on its way down.</summary>
    private bool remoteMenuJustClosed;
    private int dragStartCoord;
    private int dragStartExtent;
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

    /// <summary>Raised when a strip on the bar wants the settings window opened to a specific page.</summary>
    public event Action<string>? SettingsRequested;

    /// <summary>
    /// Raised when the orchestrator strip is clicked while something needs you, with where it was
    /// clicked and whether it was the list that was asked for (right-click) rather than the need.
    /// </summary>
    public event Action<Point, bool>? NeedsRequested;

    /// <summary>A need's card on the orchestrator strip was clicked: the need, where, and whether it was a right-click.</summary>
    public event Action<OrchestraNeedItem, Point, bool>? NeedCardClicked;

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

        remoteGlowTimer.Tick += (_, _) =>
        {
            if (IsDisposed || !(RemoteChatsVisible || OrchestraVisible))
            {
                remoteGlowTimer.Stop();
                return;
            }
            if (UsageVisible && settings.ShowRemoteChats) Invalidate(UsageArea);
            if (AntigravityVisible && settings.ShowRemoteChats) Invalidate(AntigravityArea);
            if (ClaudeChatsVisible) Invalidate(ClaudeChatsArea);
            if (AntigravityChatsVisible) Invalidate(AntigravityChatsArea);
            if (OrchestraVisible) Invalidate(OrchestraArea);
            if (!RemoteChatUsage.IsAttentionGlowing && !OrchestraUsage.IsAttentionGlowing)
                remoteGlowTimer.Stop();
        };

        RemoteChatUsage.Changed += OnRemoteChatsChanged;
        OrchestraUsage.Changed += OnOrchestraChanged;
    }

    /// <summary>
    /// A feed answering for the first time changes the layout, not just the strip: the strip only
    /// exists once one has. So the whole bar repaints, not just the strip's area.
    /// </summary>
    private void OnOrchestraChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                if (OrchestraVisible && OrchestraUsage.IsAttentionGlowing && !remoteGlowTimer.Enabled)
                    remoteGlowTimer.Start();
                Invalidate();
            });
        }
        catch { }
    }

    private void OnRemoteChatsChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed || !settings.ShowRemoteChats) return;
                if (RemoteChatUsage.IsAttentionGlowing && !remoteGlowTimer.Enabled)
                    remoteGlowTimer.Start();
                Invalidate();
            });
        }
        catch { }
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
        appBar.Register();
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

    private int StatusIconSize => Vertical ? StatusIcon : Math.Clamp(Breadth * 3 / 5, 30, 44);

    private int ClockExtent => settings.ShowStatus
        ? (Vertical ? 48 : Math.Clamp((int)Math.Round(Breadth * 1.76), 88, 136))
        : 0;

    /// <summary>
    /// Shown whenever the bar is set to show it. Deliberately not also gated on usage tracking being
    /// configured: hiding it until a session key exists means ticking the box appears to do nothing,
    /// with no hint that anything further is needed. The strip renders its own unconfigured state.
    /// </summary>
    private bool UsageVisible => settings.ShowUsage;
    private bool ClaudeChatsVisible => !UsageVisible && settings.ShowRemoteChats && RemoteChatUsage.ShouldShowServiceStrip(RemoteChatService.Claude);
    private bool AntigravityVisible => settings.ShowAntigravity;
    private bool AntigravityChatsVisible => !AntigravityVisible && settings.ShowRemoteChats && RemoteChatUsage.ShouldShowServiceStrip(RemoteChatService.Antigravity);
    private bool RemoteChatsVisible => settings.ShowRemoteChats && (UsageVisible || AntigravityVisible || ClaudeChatsVisible || AntigravityChatsVisible);
    private bool BambuVisible => settings.ShowBambu;
    private bool MachineVisible => settings.ShowMachine;

    /// <summary>Whether this bar draws the orchestrator strip whenever there is something to put in it.</summary>
    public bool ShowsOrchestra => (settings.ShowOrchestra ?? monitor.IsPrimary) && OrchestraUsage.Enabled;

    /// <summary>
    /// Shown only while an orchestrator feed has answered and has an active run in flight, or is
    /// listing something that needs a person (0px otherwise). Unset means the primary display's bar only.
    /// </summary>

    private bool OrchestraVisible => (settings.ShowOrchestra ?? monitor.IsPrimary)
                                     && OrchestraUsage.Current is { AnythingToShow: true };

    private (int Usage, int ClaudeChats, int Antigravity, int AntigravityChats, int Orchestra, int Bambu, int Machine) DefaultTrackerExtents()
    {
        int maxRows = Math.Clamp(settings.TrackerMaxRows, 1, 6);
        int scale = Math.Clamp(settings.TrackerScale, 80, 300);

        int u = UsageVisible ? UsageStrip.Extent(Vertical, maxRows, Breadth, scale) : 0;
        int cc = ClaudeChatsVisible ? UsageStrip.RemoteChatsExtent(Vertical, maxRows, Breadth, scale, RemoteChatService.Claude) : 0;
        int a = AntigravityVisible ? UsageStrip.AntigravityExtent(Vertical, maxRows, Breadth, scale) : 0;
        int ac = AntigravityChatsVisible ? UsageStrip.RemoteChatsExtent(Vertical, maxRows, Breadth, scale, RemoteChatService.Antigravity) : 0;
        int o = OrchestraVisible ? OrchestraStrip.Extent(Vertical, maxRows, Breadth, scale) : 0;
        int b = BambuVisible ? UsageStrip.BambuExtent(Vertical, maxRows, Breadth, scale) : 0;
        int m = MachineVisible ? MeterStrip.Extent(Vertical, Math.Max(3, machine.Count), maxRows, Breadth, scale) : 0;

        int baseTotal = u + cc + a + ac + o + b + m;
        if (!Vertical && baseTotal > 0 && Width > 0)
        {
            int trayAndClock = TrayItems().Count * StatusIconSize + ClockExtent;
            int leftSlots = Math.Max(Leading + 4, Leading + buttons.Count + 2);
            int available = Width - leftSlots * Slot - trayAndClock;

            if (settings.ExpandTrackers && available > baseTotal)
            {
                int barBase = u + a + b + m;
                if (barBase > 0)
                {
                    double factor = Math.Min(1.65, Math.Max(1.0, (available - cc - ac - o) / (double)barBase));
                    u = (int)Math.Round(u * factor);
                    a = (int)Math.Round(a * factor);
                    b = (int)Math.Round(b * factor);
                    m = (int)Math.Round(m * factor);
                }
            }
            else if (available > 0 && available < baseTotal)
            {
                double factor = Math.Max(0.68, available / (double)baseTotal);
                u = (int)Math.Round(u * factor);
                cc = (int)Math.Round(cc * factor);
                a = (int)Math.Round(a * factor);
                ac = (int)Math.Round(ac * factor);
                o = (int)Math.Round(o * factor);
                b = (int)Math.Round(b * factor);
                m = (int)Math.Round(m * factor);
            }
        }

        return (u, cc, a, ac, o, b, m);
    }

    private (int Usage, int ClaudeChats, int Antigravity, int AntigravityChats, int Orchestra, int Bambu, int Machine) TrackerExtents()
    {
        var (u, cc, a, ac, o, b, m) = DefaultTrackerExtents();

        if (UsageVisible && settings.UsageWidth is > 0 and var uw)
            u = Math.Clamp(uw, MinTrackerExtent, MaxTrackerExtent);
        if (ClaudeChatsVisible && settings.RemoteChatsWidth is > 0 and var ccw)
            cc = Math.Clamp(ccw, MinTrackerExtent, MaxTrackerExtent);
        if (AntigravityVisible && settings.AntigravityWidth is > 0 and var aw)
            a = Math.Clamp(aw, MinTrackerExtent, MaxTrackerExtent);
        if (AntigravityChatsVisible && settings.AntigravityChatsWidth is > 0 and var acw)
            ac = Math.Clamp(acw, MinTrackerExtent, MaxTrackerExtent);
        if (OrchestraVisible && settings.OrchestraWidth is > 0 and var ow)
            // A width dragged by hand still makes room for the need cards: they must never be
            // squeezed over whatever sits next to the strip.
            o = Math.Max(Math.Clamp(ow, MinTrackerExtent, MaxTrackerExtent),
                         OrchestraUsage.Current is { Needs.Count: > 0 } ? o : 0);
        if (BambuVisible && UsageStrip.HasActiveBambuPrinters && settings.BambuWidth is > 0 and var bw)
            b = Math.Clamp(bw, MinTrackerExtent, MaxTrackerExtent);
        if (MachineVisible && settings.MachineWidth is > 0 and var mw)
            m = Math.Clamp(mw, MinTrackerExtent, MaxTrackerExtent);

        int barLength = Vertical ? Height : Width;
        if (barLength > 0)
        {
            int trayAndClock = TrayItems().Count * StatusIconSize + ClockExtent;
            int minLeft = (Leading + 1) * Slot;
            int maxCluster = Math.Max(MinTrackerExtent, barLength - trayAndClock - minLeft);
            int total = u + cc + a + ac + o + b + m;
            if (total > maxCluster && total > 0)
            {
                double shrink = maxCluster / (double)total;
                u = (int)Math.Round(u * shrink);
                cc = (int)Math.Round(cc * shrink);
                a = (int)Math.Round(a * shrink);
                ac = (int)Math.Round(ac * shrink);
                o = (int)Math.Round(o * shrink);
                b = (int)Math.Round(b * shrink);
                m = (int)Math.Round(m * shrink);
            }
        }

        return (u, cc, a, ac, o, b, m);
    }

    private int ExtentForTracker(TrackerSection section) => section switch
    {
        TrackerSection.Usage => UsageExtent,
        TrackerSection.ClaudeChats => ClaudeChatsExtent,
        TrackerSection.Antigravity => AntigravityExtent,
        TrackerSection.AntigravityChats => AntigravityChatsExtent,
        TrackerSection.Orchestra => OrchestraExtent,
        TrackerSection.Bambu => BambuExtent,
        _ => MachineExtent,
    };

    private Rectangle AreaForTracker(TrackerSection section) => section switch
    {
        TrackerSection.Usage => UsageArea,
        TrackerSection.ClaudeChats => ClaudeChatsArea,
        TrackerSection.Antigravity => AntigravityArea,
        TrackerSection.AntigravityChats => AntigravityChatsArea,
        TrackerSection.Orchestra => OrchestraArea,
        TrackerSection.Bambu => BambuArea,
        _ => MachineArea,
    };

    private static string NameForTracker(TrackerSection section) => section switch
    {
        TrackerSection.Usage => "Claude usage",
        TrackerSection.ClaudeChats => "Claude remote chats",
        TrackerSection.Antigravity => "Antigravity usage",
        TrackerSection.AntigravityChats => "Antigravity remote chats",
        TrackerSection.Orchestra => "Orchestrator",
        TrackerSection.Bambu => "Bambu printers",
        _ => "Computer usage",
    };

    private int? CustomWidthForTracker(TrackerSection section) => section switch
    {
        TrackerSection.Usage => settings.UsageWidth,
        TrackerSection.ClaudeChats => settings.RemoteChatsWidth,
        TrackerSection.Antigravity => settings.AntigravityWidth,
        TrackerSection.AntigravityChats => settings.AntigravityChatsWidth,
        TrackerSection.Orchestra => settings.OrchestraWidth,
        TrackerSection.Bambu => settings.BambuWidth,
        _ => settings.MachineWidth,
    };

    private void SetCustomWidthForTracker(TrackerSection section, int? width)
    {
        int? clamped = width is > 0 and var w ? Math.Clamp(w, MinTrackerExtent, MaxTrackerExtent) : null;
        switch (section)
        {
            case TrackerSection.Usage: settings.UsageWidth = clamped; break;
            case TrackerSection.ClaudeChats: settings.RemoteChatsWidth = clamped; break;
            case TrackerSection.Antigravity: settings.AntigravityWidth = clamped; break;
            case TrackerSection.AntigravityChats: settings.AntigravityChatsWidth = clamped; break;
            case TrackerSection.Orchestra: settings.OrchestraWidth = clamped; break;
            case TrackerSection.Bambu: settings.BambuWidth = clamped; break;
            case TrackerSection.Machine: settings.MachineWidth = clamped; break;
        }
    }

    private void ResetAllTrackerWidths()
    {
        settings.UsageWidth = null;
        settings.RemoteChatsWidth = null;
        settings.AntigravityWidth = null;
        settings.AntigravityChatsWidth = null;
        settings.OrchestraWidth = null;
        settings.BambuWidth = null;
        settings.MachineWidth = null;
    }

    private int MaxAllowedExtentFor(TrackerSection section)
    {
        int barLength = Vertical ? Height : Width;
        if (barLength <= 0) return MaxTrackerExtent;

        var (u, cc, a, ac, o, b, m) = TrackerExtents();
        int current = section switch
        {
            TrackerSection.Usage => u,
            TrackerSection.ClaudeChats => cc,
            TrackerSection.Antigravity => a,
            TrackerSection.AntigravityChats => ac,
            TrackerSection.Orchestra => o,
            TrackerSection.Bambu => b,
            _ => m,
        };
        int others = (u + cc + a + ac + o + b + m) - current;
        int trayAndClock = TrayItems().Count * StatusIconSize + ClockExtent;
        int minLeft = (Leading + 1) * Slot;
        return Math.Clamp(barLength - trayAndClock - minLeft - others, MinTrackerExtent, MaxTrackerExtent);
    }

    private bool TrackerVisible(TrackerSection section) => section switch
    {
        TrackerSection.Usage => UsageVisible,
        TrackerSection.ClaudeChats => ClaudeChatsVisible,
        TrackerSection.Antigravity => AntigravityVisible,
        TrackerSection.AntigravityChats => AntigravityChatsVisible,
        TrackerSection.Orchestra => OrchestraVisible,
        TrackerSection.Bambu => BambuVisible,
        _ => MachineVisible,
    };

    /// <summary>The strips being shown, in the saved order (not the live one mid-drag, which is built from this).</summary>
    private List<TrackerSection> VisibleTrackers() => SavedTrackerOrder().Where(TrackerVisible).ToList();

    private TrackerSection? DividerUnder(Point p)
    {
        if (Vertical ? (p.X < 0 || p.X >= Width) : (p.Y < 0 || p.Y >= Height))
            return null;

        int coord = Vertical ? p.Y : p.X;
        TrackerSection? best = null;
        int bestDist = DividerGrabRadius + 1;

        foreach (var section in VisibleTrackers())
        {
            var area = AreaForTracker(section);
            int divCoord = Vertical ? area.Y : area.X;
            int dist = Math.Abs(coord - divCoord);
            if (dist <= DividerGrabRadius && dist < bestDist)
            {
                bestDist = dist;
                best = section;
            }
        }

        return best;
    }

    private static readonly TrackerSection[] DefaultTrackerOrder =
    [
        TrackerSection.Usage, TrackerSection.ClaudeChats, TrackerSection.Antigravity, TrackerSection.AntigravityChats,
        TrackerSection.Orchestra, TrackerSection.Bambu, TrackerSection.Machine,
    ];

    /// <summary>
    /// The strips in the order this bar was told, every one of them, shown or not. A strip the saved
    /// order does not mention — new since it was saved — goes in after the one it follows by default,
    /// rather than at the end where nobody would look for it.
    /// </summary>
    private List<TrackerSection> SavedTrackerOrder()
    {
        var order = new List<TrackerSection>(DefaultTrackerOrder.Length);
        foreach (var name in settings.TrackerOrder ?? [])
            if (Enum.TryParse<TrackerSection>(name, out var section) && !order.Contains(section))
                order.Add(section);

        for (int i = 0; i < DefaultTrackerOrder.Length; i++)
        {
            var section = DefaultTrackerOrder[i];
            if (order.Contains(section)) continue;
            int after = i == 0 ? -1 : order.IndexOf(DefaultTrackerOrder[i - 1]);
            order.Insert(after + 1, section);
        }
        return order;
    }

    /// <summary>
    /// The order strips are laid out in right now: the saved one, or while a strip is being dragged,
    /// that order with the strip moved to where it would land — so the others make room as it goes.
    /// </summary>
    private List<TrackerSection> TrackerOrder()
    {
        var order = SavedTrackerOrder();
        if (draggingStrip is not { } dragged) return order;

        var ext = TrackerExtents();
        var visible = new HashSet<TrackerSection>(VisibleTrackers());
        var others = order.Where(s => s != dragged && visible.Contains(s)).ToList();

        // Measured against the others laid out without it, so where it lands depends only on the
        // pointer and not on where it was a moment ago — no flicker between two places.
        int centre = stripDragCoord - stripGrabOffset + ExtentOf(ext, dragged) / 2;
        int pos = StatusStart, index = 0;
        foreach (var other in others)
        {
            int extent = ExtentOf(ext, other);
            if (pos + extent / 2 < centre) index++;
            pos += extent;
        }

        order.Remove(dragged);
        if (index < others.Count) order.Insert(order.IndexOf(others[index]), dragged);
        else if (others.Count > 0) order.Insert(order.IndexOf(others[^1]) + 1, dragged);
        else order.Add(dragged);
        return order;
    }

    private static int ExtentOf((int Usage, int ClaudeChats, int Antigravity, int AntigravityChats, int Orchestra, int Bambu, int Machine) e,
                                TrackerSection section) => section switch
    {
        TrackerSection.Usage => e.Usage,
        TrackerSection.ClaudeChats => e.ClaudeChats,
        TrackerSection.Antigravity => e.Antigravity,
        TrackerSection.AntigravityChats => e.AntigravityChats,
        TrackerSection.Orchestra => e.Orchestra,
        TrackerSection.Bambu => e.Bambu,
        _ => e.Machine,
    };

    /// <summary>Where a strip begins along the bar. Hidden strips take no room, so they can be walked past.</summary>
    private int StartFor(TrackerSection section)
    {
        var ext = TrackerExtents();
        int pos = StatusStart;
        foreach (var s in TrackerOrder())
        {
            if (s == section) return pos;
            pos += ExtentOf(ext, s);
        }
        return pos;
    }

    private Rectangle StripAt(TrackerSection section)
    {
        int start = StartFor(section), extent = ExtentForTracker(section);
        return Vertical ? new Rectangle(0, start, Width, extent) : new Rectangle(start, 0, extent, Height);
    }

    private TrackerSection? TrackerUnder(Point p)
    {
        foreach (var section in VisibleTrackers())
            if (AreaForTracker(section).Contains(p)) return section;
        return null;
    }

    /// <summary>The two AI services' strips read as one group each when they sit side by side.</summary>
    private static int? AiFamily(TrackerSection section) => section switch
    {
        TrackerSection.Usage or TrackerSection.ClaudeChats => 0,
        TrackerSection.Antigravity or TrackerSection.AntigravityChats => 1,
        _ => null,
    };

    /// <summary>Puts the dragged strip down where it was last shown, and keeps that order.</summary>
    private void DropStrip()
    {
        if (draggingStrip is null) return;
        var order = TrackerOrder();
        draggingStrip = null;
        settings.TrackerOrder = order.Select(s => s.ToString()).ToList();
        if (order.SequenceEqual(DefaultTrackerOrder)) settings.TrackerOrder = null;
        Cursor = Cursors.Default;
        PinsChanged?.Invoke();
        Invalidate();
    }

    private int UsageExtent => TrackerExtents().Usage;
    private int ClaudeChatsExtent => TrackerExtents().ClaudeChats;
    private int AntigravityExtent => TrackerExtents().Antigravity;
    private int AntigravityChatsExtent => TrackerExtents().AntigravityChats;
    private int OrchestraExtent => TrackerExtents().Orchestra;
    private int BambuExtent => TrackerExtents().Bambu;
    private int MachineExtent => TrackerExtents().Machine;

    /// <summary>Room the cluster needs, so window buttons know where to stop.</summary>
    private int StatusExtent
    {
        get
        {
            var (u, cc, a, ac, o, b, m) = TrackerExtents();
            return u + cc + a + ac + o + b + m + TrayItems().Count * StatusIconSize + ClockExtent;
        }
    }

    private int StatusStart => Math.Max(0, (Vertical ? Height : Width) - StatusExtent);

    /// <summary>
    /// The strips run in the order this bar keeps (dragging one moves it); by default
    ///   [◆ Claude Usage │ ◆ Claude Chats]  ║  [✦ Antigravity Usage │ ✦ Antigravity Chats]
    /// followed by the orchestrator, Bambu printers and Computer usage. The tray comes after them all.
    /// </summary>
    private int TrayStart
    {
        get
        {
            var (u, cc, a, ac, o, b, m) = TrackerExtents();
            return StatusStart + u + cc + a + ac + o + b + m;
        }
    }

    private Rectangle UsageArea => StripAt(TrackerSection.Usage);
    private Rectangle ClaudeChatsArea => StripAt(TrackerSection.ClaudeChats);
    private Rectangle AntigravityArea => StripAt(TrackerSection.Antigravity);
    private Rectangle AntigravityChatsArea => StripAt(TrackerSection.AntigravityChats);
    private Rectangle OrchestraArea => StripAt(TrackerSection.Orchestra);
    private Rectangle BambuArea => StripAt(TrackerSection.Bambu);
    private Rectangle MachineArea => StripAt(TrackerSection.Machine);

    private Rectangle TrayAt(int index) => Vertical
        ? new Rectangle(0, TrayStart + index * StatusIconSize, Width, StatusIconSize)
        : new Rectangle(TrayStart + index * StatusIconSize, 0, StatusIconSize, Height);

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

        if (draggingDivider is { } activeDrag)
        {
            int coord = Vertical ? e.Y : e.X;
            int delta = coord - dragStartCoord;
            int maxAllowed = MaxAllowedExtentFor(activeDrag);
            int nextExtent = Math.Clamp(dragStartExtent - delta, MinTrackerExtent, maxAllowed);
            if (CustomWidthForTracker(activeDrag) != nextExtent)
            {
                SetCustomWidthForTracker(activeDrag, nextExtent);
                Invalidate();
                Update();
            }
            return;
        }

        if (draggingStrip is not null)
        {
            stripDragCoord = Vertical ? e.Y : e.X;
            Invalidate();
            return;
        }

        if (pendingStripPress is { } press && (e.Button & MouseButtons.Left) != 0)
        {
            int coord = Vertical ? e.Y : e.X;
            int pressed = Vertical ? press.Press.Y : press.Press.X;
            if (Math.Abs(coord - pressed) < StripDragThreshold) return;

            // Far enough that it is not a click with a shaky hand: pick the strip up.
            pendingStripPress = null;
            draggingStrip = press.Section;
            stripGrabOffset = pressed - StartFor(press.Section);
            stripDragCoord = coord;
            Capture = true;
            Cursor = Vertical ? Cursors.SizeNS : Cursors.SizeWE;
            tips.SetToolTip(this, string.Empty);
            TrackPreview(-1, false);
            Invalidate();
            return;
        }

        var div = DividerUnder(e.Location);
        if (div is not null)
        {
            if (hoveredDivider != div || hovered != -1 || hoveredStatus != -1 || hoveredUsage
                || hoveredClaudeChats || hoveredAntigravity || hoveredAntigravityChats
                || hoveredRemoteChatId is not null || hoveredOrchestra || hoveredBambu || hoveredMachine || hoveredStart)
            {
                hoveredDivider = div;
                hovered = -1;
                hoveredStatus = -1;
                hoveredUsage = false;
                hoveredClaudeChats = false;
                hoveredAntigravity = false;
                hoveredAntigravityChats = false;
                hoveredRemoteChatId = null;
                hoveredOrchestra = false;
                hoveredCard = OrchestraStrip.hoveredCard = null;
                hoveredBambu = false;
                hoveredMachine = false;
                hoveredStart = false;
                Cursor = Vertical ? Cursors.HSplit : Cursors.VSplit;
                tips.SetToolTip(this, $"{NameForTracker(div.Value)} — drag divider to resize, double-click to reset");
                TrackPreview(-1, false);
                Invalidate();
            }
            return;
        }

        int under = SlotUnder(e.Location), tray = TrayUnder(e.Location);
        int maxRows = Math.Clamp(settings.TrackerMaxRows, 1, 6);
        bool usage = UsageVisible && UsageArea.Contains(e.Location);
        bool claudeChats = ClaudeChatsVisible && ClaudeChatsArea.Contains(e.Location);
        bool antigravity = AntigravityVisible && AntigravityArea.Contains(e.Location);
        bool agChats = AntigravityChatsVisible && AntigravityChatsArea.Contains(e.Location);
        RemoteChatItem? remoteChatCircle = (usage && settings.ShowRemoteChats)
            ? UsageStrip.HitTestHeaderChat(UsageArea, RemoteChatService.Claude, e.Location, maxRows)
            : (antigravity && settings.ShowRemoteChats)
                ? UsageStrip.HitTestHeaderChat(AntigravityArea, RemoteChatService.Antigravity, e.Location, maxRows)
                : claudeChats
                    ? UsageStrip.HitTestRemoteChat(ClaudeChatsArea, RemoteChatUsage.Current, e.Location, maxRows, RemoteChatService.Claude)
                    : agChats
                        ? UsageStrip.HitTestRemoteChat(AntigravityChatsArea, RemoteChatUsage.Current, e.Location, maxRows, RemoteChatService.Antigravity)
                        : null;
        string? remoteChatCircleId = remoteChatCircle?.Id;
        bool orchestra = OrchestraVisible && OrchestraArea.Contains(e.Location);
        var card = orchestra ? OrchestraStrip.CardAt(OrchestraArea, e.Location, out _) : null;
        string? cardKey = card is null ? null : OrchestraStrip.CardKey(card);
        if (cardKey != hoveredCard)
        {
            // Card to card within the strip changes nothing else, so it is handled before the early return.
            hoveredCard = cardKey;
            OrchestraStrip.hoveredCard = cardKey;
            if (orchestra == hoveredOrchestra) tips.SetToolTip(this, card is not null ? OrchestraStrip.CardTip(card) : OrchestraStrip.Tip(OrchestraUsage.Current));
            Invalidate(OrchestraArea);
        }
        bool bambu = BambuVisible && BambuArea.Contains(e.Location);
        bool load = MachineVisible && MachineArea.Contains(e.Location);
        bool start = settings.ShowStartButton && StartSlot.Contains(e.Location);
        if (hoveredDivider is null && under == hovered && tray == hoveredStatus && usage == hoveredUsage
            && claudeChats == hoveredClaudeChats && antigravity == hoveredAntigravity && agChats == hoveredAntigravityChats
            && remoteChatCircleId == hoveredRemoteChatId
            && orchestra == hoveredOrchestra && bambu == hoveredBambu && load == hoveredMachine && start == hoveredStart) return;

        hoveredDivider = null;
        hovered = under;
        hoveredStatus = tray;
        hoveredUsage = usage;
        hoveredClaudeChats = claudeChats;
        hoveredAntigravity = antigravity;
        hoveredAntigravityChats = agChats;
        hoveredRemoteChatId = remoteChatCircleId;
        hoveredOrchestra = orchestra;
        hoveredBambu = bambu;
        hoveredMachine = load;
        hoveredStart = start;
        Cursor = under >= 0 || tray >= 0 || usage || claudeChats || antigravity || agChats || orchestra || bambu || load || start
            ? Cursors.Hand : Cursors.Default;

        // An icons-only bar is unreadable without these. The strip needs one whatever the bar looks
        // like: five pixels of colour cannot say which limit it is, or when it resets. A button
        // showing a preview needs no tooltip — and would otherwise show both at once.
        bool previewing = Previewable(under);
        tips.SetToolTip(this, start ? "Start"
                            : load ? MachineTip()
                            : bambu ? UsageStrip.BambuTip(BambuUsage.Current)
                            : orchestra ? (card is not null ? OrchestraStrip.CardTip(card) : OrchestraStrip.Tip(OrchestraUsage.Current))
                            : remoteChatCircle is not null ? UsageStrip.RemoteChatItemTip(remoteChatCircle)
                            : claudeChats ? UsageStrip.RemoteChatsTip(RemoteChatUsage.Current, RemoteChatService.Claude)
                            : agChats ? UsageStrip.RemoteChatsTip(RemoteChatUsage.Current, RemoteChatService.Antigravity)
                            : antigravity ? UsageStrip.AntigravityTip(AntigravityUsage.Current)
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
        if (draggingDivider is not null) return;

        hovered = hoveredStatus = -1;
        hoveredStart = false;
        hoveredUsage = false;
        hoveredClaudeChats = false;
        hoveredAntigravity = false;
        hoveredAntigravityChats = false;
        hoveredRemoteChatId = null;
        hoveredOrchestra = false;
        hoveredCard = OrchestraStrip.hoveredCard = null;
        hoveredBambu = false;
        hoveredMachine = false;
        hoveredDivider = null;
        Cursor = Cursors.Default;

        // The preview closes itself once the pointer is over neither it nor the button: leaving the
        // bar is how you reach it, so a leave here cannot mean it is finished with.
        hoverDelay.Stop();
        pending = -1;

        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (draggingDivider is not null)
        {
            draggingDivider = null;
            Capture = false;
            PinsChanged?.Invoke();
            Invalidate();
        }

        if (draggingStrip is not null)
        {
            DropStrip();
            Capture = false;
        }
        else if (pendingStripPress is { } press && e.Button == MouseButtons.Left)
        {
            pendingStripPress = null;
            if (AreaForTracker(press.Section).Contains(e.Location)) ClickTracker(press.Section, press.Press);
        }
        remoteMenuJustClosed = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && draggingDivider is not null)
        {
            draggingDivider = null;
            PinsChanged?.Invoke();
            Invalidate();
        }

        // Losing the pointer mid-drag — a window popping up, say — puts the strip down where it was shown.
        if (!Capture && draggingStrip is not null) DropStrip();
    }

    private void ShowDividerMenu(TrackerSection section, Point at)
    {
        var menu = Theme.Menu("Divider");
        string name = NameForTracker(section);

        menu.Items.Add(new ToolStripMenuItem($"Reset {name} size", null, (_, _) =>
        {
            SetCustomWidthForTracker(section, null);
            PinsChanged?.Invoke();
            Invalidate();
        })
        {
            Enabled = CustomWidthForTracker(section) is not null,
        });

        bool anyCustom = settings.RemoteChatsWidth is not null
            || settings.AntigravityChatsWidth is not null
            || settings.UsageWidth is not null
            || settings.AntigravityWidth is not null
            || settings.OrchestraWidth is not null
            || settings.BambuWidth is not null
            || settings.MachineWidth is not null;

        menu.Items.Add(new ToolStripMenuItem("Reset all tracker sizes", null, (_, _) =>
        {
            ResetAllTrackerWidths();
            PinsChanged?.Invoke();
            Invalidate();
        })
        {
            Enabled = anyCustom,
        });

        menu.Items.Add(new ToolStripMenuItem("Reset tracker order", null, (_, _) =>
        {
            settings.TrackerOrder = null;
            PinsChanged?.Invoke();
            Invalidate();
        })
        {
            Enabled = settings.TrackerOrder is not null,
        });

        menu.Show(at);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        Theme.DismissOpenMenusOutside(Cursor.Position);

        if (DividerUnder(e.Location) is { } div)
        {
            preview.Dismiss();
            if (e.Button == MouseButtons.Right)
            {
                ShowDividerMenu(div, Cursor.Position);
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                if (e.Clicks >= 2)
                {
                    SetCustomWidthForTracker(div, null);
                    PinsChanged?.Invoke();
                    Invalidate();
                    return;
                }

                draggingDivider = div;
                hoveredDivider = div;
                dragStartCoord = Vertical ? e.Y : e.X;
                dragStartExtent = ExtentForTracker(div);
                Capture = true;
                Invalidate();
                return;
            }
        }

        // Checked now, while the press that may have closed it is still recent; the click it
        // turns into is only known on release.
        remoteMenuJustClosed = Theme.WasJustDismissedAt("RemoteChats", Cursor.Position);

        if (TrackerUnder(e.Location) is { } strip)
        {
            preview.Dismiss();

            // A left press may be the start of dragging the strip elsewhere, so what it does is
            // decided when it is released. The other buttons act at once, as they always have.
            if (e.Button == MouseButtons.Left)
                pendingStripPress = (strip, e);
            else
                ClickTracker(strip, e);
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
        var menu = Theme.Menu("Button");
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

    /// <summary>What a click on each strip does.</summary>
    private void ClickTracker(TrackerSection section, MouseEventArgs e)
    {
        switch (section)
        {
            case TrackerSection.Usage:
                if (settings.ShowRemoteChats && RemoteChatUsage.Enabled)
                {
                    int maxR = Math.Clamp(settings.TrackerMaxRows, 1, 6);
                    if (e.Button == MouseButtons.Left
                        && UsageStrip.HitTestHeaderChat(UsageArea, RemoteChatService.Claude, e.Location, maxR) is { } clickedClaudeChat)
                    {
                        RemoteChatUsage.OpenChat(clickedClaudeChat);
                        break;
                    }
                    if (e.Button == MouseButtons.Right
                        || UsageStrip.IsPointInHeaderRow(UsageArea, RemoteChatService.Claude, e.Location, maxR)
                        || RemoteChatUsage.GetStripChatsForService(RemoteChatService.Claude).Count > 0)
                    {
                        ShowRemoteChatsMenu(Cursor.Position, RemoteChatService.Claude);
                        break;
                    }
                }
                // Not set up yet (or expired): the useful destination is our own settings page, not the
                // website that cannot tell ScweenSpit anything until a credential is in place.
                if (ClaudeUsage.Enabled && ClaudeUsage.Current is not { NeedsKey: true })
                    SystemStatus.Open(ClaudeUsage.UsagePage);
                else
                    OpenSettingsOrMenu("Claude usage");
                break;

            case TrackerSection.ClaudeChats:
                HandleRemoteChatsClick(e, ClaudeChatsArea, RemoteChatService.Claude);
                break;

            case TrackerSection.Antigravity:
                if (settings.ShowRemoteChats && RemoteChatUsage.Enabled)
                {
                    int maxR = Math.Clamp(settings.TrackerMaxRows, 1, 6);
                    if (e.Button == MouseButtons.Left
                        && UsageStrip.HitTestHeaderChat(AntigravityArea, RemoteChatService.Antigravity, e.Location, maxR) is { } clickedAgChat)
                    {
                        RemoteChatUsage.OpenChat(clickedAgChat);
                        break;
                    }
                    if (e.Button == MouseButtons.Right
                        || UsageStrip.IsPointInHeaderRow(AntigravityArea, RemoteChatService.Antigravity, e.Location, maxR)
                        || RemoteChatUsage.GetStripChatsForService(RemoteChatService.Antigravity).Count > 0)
                    {
                        ShowRemoteChatsMenu(Cursor.Position, RemoteChatService.Antigravity);
                        break;
                    }
                }
                OpenSettingsOrMenu("Antigravity usage");
                break;

            case TrackerSection.AntigravityChats:
                HandleRemoteChatsClick(e, AntigravityChatsArea, RemoteChatService.Antigravity);
                break;

            case TrackerSection.Orchestra:
                if (OrchestraStrip.CardAt(OrchestraArea, e.Location, out bool moreCards) is { } clickedCard
                    && NeedCardClicked is not null)
                {
                    NeedCardClicked(clickedCard, Cursor.Position, e.Button == MouseButtons.Right);
                    break;
                }
                if (NeedsRequested is not null && OrchestraUsage.Current is { Needs.Count: > 0 })
                {
                    NeedsRequested(Cursor.Position, moreCards || e.Button == MouseButtons.Right);
                    break;
                }
                OpenSettingsOrMenu("Orchestrator");
                break;

            case TrackerSection.Bambu:
                if (e.Button != MouseButtons.Right && BambuUsage.Enabled && BambuUsage.Current is not { NeedsAuth: true }
                    && BambuUsage.OpenSlicer())
                    break;
                OpenSettingsOrMenu("Bambu printers");
                break;

            case TrackerSection.Machine:
                // Task Manager is where anyone would go next, and it opens with no taskbar at all.
                SystemStatus.Open("taskmgr.exe");
                break;
        }
    }

    private void OpenSettingsOrMenu(string page)
    {
        if (SettingsRequested is not null)
            SettingsRequested.Invoke(page);
        else
            MenuRequested?.Invoke(Cursor.Position);
    }

    private void HandleRemoteChatsClick(MouseEventArgs e, Rectangle stripArea, RemoteChatService service)
    {
        if (!RemoteChatUsage.Enabled || RemoteChatUsage.Current is { NeedsAuth: true })
        {
            if (SettingsRequested is not null)
                SettingsRequested.Invoke("Remote chats");
            else
                MenuRequested?.Invoke(Cursor.Position);
            return;
        }

        if (e.Button == MouseButtons.Left
            && UsageStrip.HitTestRemoteChat(stripArea, RemoteChatUsage.Current, e.Location, Math.Clamp(settings.TrackerMaxRows, 1, 6), service) is { } clickedChat)
        {
            RemoteChatUsage.OpenChat(clickedChat);
            return;
        }

        ShowRemoteChatsMenu(Cursor.Position, service);
    }

    /// <summary>
    /// Popup menu when a remote server chats strip is clicked, showing every server × service and
    /// each live, waiting-for-input, or finished chat so any chat can be opened directly.
    /// When opened from a specific AI's chat strip, that AI's server rows are listed first.
    /// </summary>
    private void ShowRemoteChatsMenu(Point at, RemoteChatService? preferredService = null)
    {
        if (remoteMenuJustClosed || Theme.WasJustDismissedAt("RemoteChats", at))
        {
            remoteMenuJustClosed = false;
            return;
        }

        var menu = Theme.Menu("RemoteChats");
        var reading = RemoteChatUsage.Current;
        IReadOnlyList<RemoteServerRow> allRows = reading?.Rows ?? [];
        var rows = preferredService is { } svc
            ? allRows.OrderBy(r => r.Service == svc ? 0 : 1).ToList()
            : allRows.ToList();

        if (rows.Count == 0)
        {
            var empty = new ToolStripMenuItem(reading?.Error ?? "No remote chats found yet")
            {
                Enabled = false,
                ForeColor = Theme.Muted,
            };
            menu.Items.Add(empty);
        }
        else
        {
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                if (r > 0) menu.Items.Add(new ToolStripSeparator());

                string svcName = row.Service == RemoteChatService.Antigravity ? "Antigravity" : "Claude";
                var header = new ToolStripMenuItem(
                    $"{row.Server} · {svcName} — {RemoteChatUsage.DescribeRow(row)}",
                    null,
                    (_, _) => RemoteChatUsage.OpenService(row.Service))
                {
                    Font = Theme.Face(9f, FontStyle.Bold),
                    ForeColor = row.WaitingCount > 0
                        ? Color.FromArgb(255, 210, 115)
                        : row.Service == RemoteChatService.Antigravity
                            ? Color.FromArgb(196, 160, 255)
                            : Color.FromArgb(130, 185, 250),
                };
                menu.Items.Add(header);

                foreach (var chat in row.Chats.Take(10))
                {
                    var captured = chat;
                    string prefix = chat.State switch
                    {
                        RemoteChatState.Waiting => "  ⚠ [Needs input] ",
                        RemoteChatState.Running => "  ● [Running] ",
                        _ => "  ✓ [Done] ",
                    };
                    string repo = !string.IsNullOrWhiteSpace(chat.Repo) ? $"({chat.Repo}) " : "";
                    var item = new ToolStripMenuItem(
                        prefix + repo + Shorten(chat.Title),
                        null,
                        (_, _) => RemoteChatUsage.OpenChat(captured))
                    {
                        ForeColor = chat.State switch
                        {
                            RemoteChatState.Waiting => Color.FromArgb(255, 210, 115),
                            RemoteChatState.Running => Theme.Text,
                            _ => Color.FromArgb(145, 220, 195),
                        },
                        ToolTipText = !string.IsNullOrWhiteSpace(chat.Detail)
                            ? $"{chat.Title}\n{chat.Detail}"
                            : chat.Title,
                    };
                    menu.Items.Add(item);
                }

                if (row.Chats.Count > 10)
                {
                    menu.Items.Add(new ToolStripMenuItem($"  +{row.Chats.Count - 10} more…", null,
                        (_, _) => RemoteChatUsage.OpenService(row.Service))
                    {
                        ForeColor = Theme.Muted,
                    });
                }
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        if (preferredService == RemoteChatService.Antigravity)
        {
            menu.Items.Add(new ToolStripMenuItem("Open Antigravity", null,
                (_, _) => RemoteChatUsage.OpenService(RemoteChatService.Antigravity)));
            menu.Items.Add(new ToolStripMenuItem("Antigravity usage settings…", null,
                (_, _) => OpenSettingsOrMenu("Antigravity usage")));
        }
        else
        {
            menu.Items.Add(new ToolStripMenuItem("Open Claude Code web (claude.ai/code)", null,
                (_, _) => SystemStatus.Open(RemoteChatUsage.ClaudeCodeWebUrl)));
            menu.Items.Add(new ToolStripMenuItem("Open Claude usage (claude.ai)", null,
                (_, _) => SystemStatus.Open(ClaudeUsage.UsagePage)));
            menu.Items.Add(new ToolStripMenuItem("Claude usage settings…", null,
                (_, _) => OpenSettingsOrMenu("Claude usage")));
        }
        menu.Items.Add(new ToolStripMenuItem("Refresh remote chats now", null,
            (_, _) => RemoteChatUsage.Refresh()));
        menu.Items.Add(new ToolStripMenuItem("Remote chats settings…", null, (_, _) =>
        {
            if (SettingsRequested is not null)
                SettingsRequested.Invoke("Remote chats");
            else
                MenuRequested?.Invoke(Cursor.Position);
        }));

        menu.Show(at);
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
        using var resizeWash = new SolidBrush(Color.FromArgb(24, 74, 158, 255));
        using var divider = new Pen(Theme.Divider, 1f);
        using var innerGroupDivider = new Pen(Color.FromArgb(38, 44, 56), 1f);
        using var activeDivider = new Pen(Theme.Accent, 2f);
        using var gripBrush = new SolidBrush(Color.FromArgb(95, 110, 132));
        using var innerGripBrush = new SolidBrush(Color.FromArgb(62, 72, 90));
        using var activeGripBrush = new SolidBrush(Theme.Accent);
        using var claudeGroupAccent = new SolidBrush(Color.FromArgb(115, 130, 185, 250));
        using var agGroupAccent = new SolidBrush(Color.FromArgb(115, 196, 160, 255));

        void DrawStripSeparator(Rectangle strip, TrackerSection section, bool innerAiPair = false)
        {
            bool active = draggingDivider == section || hoveredDivider == section;
            if (active)
                g.FillRectangle(resizeWash, strip);

            int inset = innerAiPair && !active ? 10 : 5;
            var linePen = active ? activeDivider : (innerAiPair ? innerGroupDivider : divider);
            var pillBrush = active ? activeGripBrush : (innerAiPair ? innerGripBrush : gripBrush);

            if (Vertical)
            {
                g.DrawLine(linePen, strip.X + inset, strip.Y, strip.Right - inset, strip.Y);
                int pillW = active ? 16 : (innerAiPair ? 7 : 10);
                int pillH = active ? 3 : 2;
                g.FillRectangle(pillBrush,
                                strip.X + (strip.Width - pillW) / 2, strip.Y - pillH / 2, pillW, pillH);
            }
            else
            {
                g.DrawLine(linePen, strip.X, strip.Y + inset, strip.X, strip.Bottom - inset);
                int pillW = active ? 3 : 2;
                int pillH = active ? 16 : (innerAiPair ? 7 : 10);
                g.FillRectangle(pillBrush,
                                strip.X - pillW / 2, strip.Y + (strip.Height - pillH) / 2, pillW, pillH);
            }
        }

        void DrawAiGroupAccent(int start, int extent, Brush accentBrush)
        {
            if (extent <= 8) return;
            if (Vertical)
                g.FillRectangle(accentBrush, 0, start + 3, 2, extent - 6);
            else
                g.FillRectangle(accentBrush, start + 3, 0, extent - 6, 2);
        }

        int maxRows = Math.Clamp(settings.TrackerMaxRows, 1, 6);

        // Grouping follows the strips wherever they have been dragged: a Claude strip next to the
        // other Claude strip shares its accent and gets the lighter inner divider, one moved away
        // from it stands alone.
        var shown = TrackerOrder().Where(TrackerVisible).ToList();
        bool PairedWithPrevious(TrackerSection section)
        {
            int at = shown.IndexOf(section);
            return at > 0 && AiFamily(section) is { } family && AiFamily(shown[at - 1]) == family;
        }

        if (UsageVisible)
        {
            var strip = UsageArea;
            if (hoveredUsage) g.FillRectangle(hot, strip);
            string? claudeHdrHover = hoveredUsage ? hoveredRemoteChatId : null;
            UsageStrip.Paint(g, strip, ClaudeUsage.Current, maxRows, settings.ShowRemoteChats, claudeHdrHover);
            DrawStripSeparator(strip, TrackerSection.Usage, innerAiPair: PairedWithPrevious(TrackerSection.Usage));
        }

        if (ClaudeChatsVisible)
        {
            var strip = ClaudeChatsArea;
            if (hoveredClaudeChats) g.FillRectangle(hot, strip);
            string? claudeHoverId = hoveredClaudeChats ? hoveredRemoteChatId : null;
            UsageStrip.PaintRemoteChats(g, strip, RemoteChatUsage.Current, maxRows, claudeHoverId, hoveredClaudeChats, RemoteChatService.Claude);
            DrawStripSeparator(strip, TrackerSection.ClaudeChats, innerAiPair: PairedWithPrevious(TrackerSection.ClaudeChats));
        }

        if (AntigravityVisible)
        {
            var strip = AntigravityArea;
            if (hoveredAntigravity) g.FillRectangle(hot, strip);
            string? agHdrHover = hoveredAntigravity ? hoveredRemoteChatId : null;
            UsageStrip.PaintAntigravity(g, strip, AntigravityUsage.Current, maxRows, settings.ShowRemoteChats, agHdrHover);
            DrawStripSeparator(strip, TrackerSection.Antigravity, innerAiPair: PairedWithPrevious(TrackerSection.Antigravity));
        }

        if (AntigravityChatsVisible)
        {
            var strip = AntigravityChatsArea;
            if (hoveredAntigravityChats) g.FillRectangle(hot, strip);
            string? agHoverId = hoveredAntigravityChats ? hoveredRemoteChatId : null;
            UsageStrip.PaintRemoteChats(g, strip, RemoteChatUsage.Current, maxRows, agHoverId, hoveredAntigravityChats, RemoteChatService.Antigravity);
            DrawStripSeparator(strip, TrackerSection.AntigravityChats, innerAiPair: PairedWithPrevious(TrackerSection.AntigravityChats));
        }

        // One accent across each run of same-service strips.
        for (int i = 0; i < shown.Count; i++)
        {
            if (AiFamily(shown[i]) is not { } family) continue;
            int j = i;
            while (j + 1 < shown.Count && AiFamily(shown[j + 1]) == family) j++;
            var first = AreaForTracker(shown[i]);
            var last = AreaForTracker(shown[j]);
            int start = Vertical ? first.Y : first.X;
            int end = Vertical ? last.Bottom : last.Right;
            DrawAiGroupAccent(start, end - start, family == 0 ? claudeGroupAccent : agGroupAccent);
            i = j;
        }

        if (OrchestraVisible)
        {
            var strip = OrchestraArea;
            if (hoveredOrchestra) g.FillRectangle(hot, strip);
            OrchestraStrip.Paint(g, strip, OrchestraUsage.Current, maxRows);
            DrawStripSeparator(strip, TrackerSection.Orchestra);
        }

        if (BambuVisible)
        {
            var strip = BambuArea;
            if (hoveredBambu) g.FillRectangle(hot, strip);
            UsageStrip.PaintBambu(g, strip, BambuUsage.Current, maxRows);
            DrawStripSeparator(strip, TrackerSection.Bambu);
        }

        if (MachineVisible)
        {
            var strip = MachineArea;
            if (hoveredMachine) g.FillRectangle(hot, strip);
            MeterStrip.Paint(g, strip, machine, maxRowsPerColumn: maxRows);
            DrawStripSeparator(strip, TrackerSection.Machine);
        }

        // The strip in hand, wherever it would land if let go now.
        if (draggingStrip is { } dragged && TrackerVisible(dragged))
        {
            var box = AreaForTracker(dragged);
            box.Inflate(-1, -1);
            g.FillRectangle(resizeWash, box);
            using var outline = new Pen(Theme.Accent, 2f);
            g.DrawRectangle(outline, box.X, box.Y, box.Width - 1, box.Height - 1);
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
        int size = Math.Clamp(Math.Min(area.Width, area.Height) / 2, 12, 28);
        var box = new Rectangle(area.X + (area.Width - size) / 2, area.Y + (area.Height - size) / 2, size, size);

        int split = (int)Math.Round(box.Width * 0.68);
        using var major = new SolidBrush(Theme.Accent);
        using var minor = new SolidBrush(Theme.Muted);

        g.FillRectangle(major, box.X, box.Y, split - 1, box.Height);
        g.FillRectangle(minor, box.X + split + 1, box.Y, box.Width - split - 1, box.Height);
    }

    private void PaintClock(Graphics g)
    {
        float clockScale = Vertical ? 1f : Math.Clamp(Height / 50f, 0.9f, 1.45f);
        using var time = Theme.Face(Vertical ? 12f : 13f * clockScale, FontStyle.Bold);
        using var date = Theme.Face(Vertical ? 8f : 9f * clockScale);
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
            RemoteChatUsage.Changed -= OnRemoteChatsChanged;
            OrchestraUsage.Changed -= OnOrchestraChanged;
            remoteGlowTimer.Dispose();
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
