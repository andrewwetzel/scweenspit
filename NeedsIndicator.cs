using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScweenSpit;

/// <summary>
/// Keeps what the orchestrator is waiting on a person for in sight while ScweenSpit runs. Each need
/// is a small card of its own, floating above the taskbar by the clock: the newest lines up on the
/// left of the last, each as wide as it needs to say what it is, all of them narrower when there are
/// more. Where the screen has room, a button in the Windows taskbar carries the count as well, and
/// flashes when something new arrives. A card opens a popup for its need; the button and a
/// right-click open <see cref="NeedsPanel"/>, the whole list.
///
/// Gone whenever nothing is outstanding: an indicator that is always there stops being looked at.
/// </summary>
public sealed class NeedsIndicator : IDisposable
{
    /// <summary>The choices Settings offers, in the order it offers them.</summary>
    public static readonly string[] Choices = ["Automatic", "Cards", "Taskbar", "Off"];

    /// <summary>
    /// Below this, in pixels at 100 % scale, a display is laptop-sized: the work area of a 1080p
    /// panel at 125 % or more, or a 1366×768 one at any scale.
    /// </summary>
    private const int SmallWidth = 1600, SmallHeight = 900;

    /// <summary>A taskbar thinner than this at 100 % scale has small buttons, too small to badge legibly.</summary>
    private const int CompactTaskbar = 40;

    /// <summary>Card sizes at 100 % scale: never narrower than enough for a few words, never wider than a glance.</summary>
    private const int CardWidth = 360, CardGap = 10, CardMargin = 12;

    /// <summary>How much of the screen's width the cards may take, so they never become a wall.</summary>
    private const double CardShare = 0.65;

    private readonly SplitConfig config;
    private readonly Func<bool> barShowsOrchestra;
    private readonly Action<string> openSettings;
    private readonly Action<Action> post;

    // Rare, and only while something is waiting: auto-hide can be switched on without any event
    // reaching us, and that is enough to change the answer. It also moves the cards' ages on.
    private readonly System.Windows.Forms.Timer recheck = new() { Interval = 10_000 };

    private NeedsPanel? panel;
    private bool taskbar;
    private readonly Dictionary<string, NeedCard> cards = new(StringComparer.Ordinal);
    private NeedCard? moreCard;
    private double cardScale;
    private bool belowOverlay;
    private static NeedsIndicator? current;

    /// <summary>Needs given a card before, so only ones that really are new announce themselves.</summary>
    private readonly HashSet<string> carded = new(StringComparer.Ordinal);
    private bool firstUpdate = true;

    /// <summary>What the last decision was and why, for the settings page.</summary>
    public static string LastDecision { get; private set; } = "";

    public NeedsIndicator(SplitConfig config, Func<bool> barShowsOrchestra,
                          Action<string> openSettings, Action<Action> post)
    {
        this.config = config;
        this.barShowsOrchestra = barShowsOrchestra;
        this.openSettings = openSettings;
        this.post = post;
        current = this;

        recheck.Tick += (_, _) => Update();
        OrchestraUsage.Changed += OnChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    private void OnChanged() => post(Update);

    private void OnPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) => post(Update);

    /// <summary>
    /// Re-decides where the indicator goes and brings it up to date. Called on every change to the
    /// reading, the settings and the displays — the size or scale of a screen being one of those.
    /// </summary>
    public void Update()
    {
        var reading = OrchestraUsage.Current;
        int open = reading?.Outstanding ?? 0;
        bool blocking = (reading?.OutstandingBlocking ?? 0) > 0;

        var (wantCards, wantTaskbar, why) = Decide();
        LastDecision = why;
        if (wantTaskbar != taskbar)
        {
            Log.Write($"needs indicator: taskbar {taskbar} → {wantTaskbar} ({why})");
            taskbar = wantTaskbar;
            // A panel open on screen is left alone; it goes when it is next put away.
            if (panel is { } p && !p.IsOpen) { p.Dispose(); panel = null; }
        }

        recheck.Enabled = open > 0;

        if (taskbar && open > 0)
            PanelFor(inTaskbar: true).Park(open, blocking);
        else
            panel?.Retire();

        LayOutCards(wantCards ? reading?.Needs.Where(i => i.Waiting).ToList() ?? [] : []);
        firstUpdate = false;

        panel?.Refill();
    }

    /// <summary>Opens the list near <paramref name="near"/>, or in the corner by the clock.</summary>
    public void OpenPanel(Point? near)
    {
        PanelFor(inTaskbar: taskbar).Open(near);
    }

    /// <summary>
    /// Needs whose page has already been opened by a click on the taskbar button. The first click
    /// goes straight to the page; the next one opens the list, where Done and everything else lives.
    /// </summary>
    private readonly HashSet<string> shortcutTaken = new(StringComparer.Ordinal);

    /// <summary>
    /// A click on the taskbar button or the strip: the cards, raised and glowing, when they are up.
    /// Otherwise, with exactly one need outstanding and a page to open for it — and nothing to do
    /// for it in the list itself — that page; otherwise the list. Returns true unless it opened the list.
    /// </summary>
    public bool Activate(Point? near)
    {
        if (CardsShowing)
        {
            PointAtCards();
            return true;
        }

        var open = OrchestraUsage.Current?.Needs.Where(i => i.Waiting).ToList() ?? [];
        shortcutTaken.RemoveWhere(k => !open.Any(i => NeedActions.Key(i) == k));
        if (open.Count == 1 && NeedActions.GoesToPage(open[0].Need) && shortcutTaken.Add(NeedActions.Key(open[0])))
        {
            NeedActions.OpenLatest(open[0]);
            return true;
        }

        OpenPanel(near);
        return false;
    }

    /// <summary>Something new arrived: draw the eye to the taskbar button. New cards glow on their own.</summary>
    public void Announce()
    {
        if (taskbar) panel?.Flash();
    }

    private NeedsPanel PanelFor(bool inTaskbar)
    {
        // One open in front of somebody is kept even if it is the wrong kind; it is swapped once put away.
        if (panel is { IsDisposed: false } existing && (existing.InTaskbar == inTaskbar || existing.IsOpen)) return existing;
        panel?.Dispose();
        panel = new NeedsPanel(inTaskbar);
        panel.SettingsRequested += () => openSettings("Orchestrator");
        // Clicking the taskbar button: the page itself when there is exactly one, else the list.
        panel.Restoring += () =>
        {
            // The cards are where things get done; the button points at them rather than opening a window.
            if (CardsShowing)
            {
                PointAtCards();
                return true;
            }

            var open = OrchestraUsage.Current?.Needs.Where(i => i.Waiting).ToList() ?? [];
            shortcutTaken.RemoveWhere(k => !open.Any(i => NeedActions.Key(i) == k));
            if (open.Count != 1 || !NeedActions.GoesToPage(open[0].Need) || !shortcutTaken.Add(NeedActions.Key(open[0])))
                return false;
            NeedActions.OpenLatest(open[0]);
            return true;
        };
        return panel;
    }

    // ---- cards ------------------------------------------------------------------

    /// <summary>
    /// One card per need above the clock end of the main display's work area — so above the
    /// taskbar, or above a ScweenSpit bar. They fill a row leftwards, the newest furthest left, then
    /// a row above that, up to about two-thirds of the width and half the height; whatever does not
    /// fit after that is a "+N" card that opens the list.
    /// </summary>
    private void LayOutCards(List<OrchestraNeedItem> waiting)
    {
        // Oldest first: the first card sits by the clock, each newer one lines up on its left.
        var items = waiting.OrderBy(i => i.Need.CreatedAt ?? DateTimeOffset.MinValue).ToList();
        NeedStates.Keep((OrchestraUsage.Current?.Needs ?? []).Select(NeedActions.Key));
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault();
        if (screen is null || items.Count == 0)
        {
            ClearCards();
            if (screen is not null) carded.IntersectWith(waiting.Select(NeedActions.Key));
            return;
        }

        double scale = ScaleOf(screen);
        if (scale != cardScale)
        {
            // Sized for the old scale throughout; cheaper to make again than to resize every part.
            ClearCards();
            cardScale = scale;
        }
        int S(int px) => (int)Math.Round(px * scale);

        var keys = items.Select(NeedActions.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in cards.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            cards[gone].Dispose();
            cards.Remove(gone);
        }
        carded.IntersectWith(keys);

        int cardWidth = S(CardWidth);
        foreach (var item in items)
        {
            string key = NeedActions.Key(item);
            if (!cards.TryGetValue(key, out var card))
            {
                card = cards[key] = new NeedCard(scale, cardWidth);
                card.Resized += () => post(() => PlaceCards());
                if (carded.Add(key) && !firstUpdate) card.Pulse();
            }
            card.Set(item);
            card.RefreshAge();
        }

        order = items.Select(NeedActions.Key).ToList();
        PlaceCards();
    }

    /// <summary>The cards' keys, oldest first, as last laid out.</summary>
    private List<string> order = [];

    /// <summary>Puts every card where it goes, given the heights they now want.</summary>
    private void PlaceCards()
    {
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault();
        if (screen is null || cards.Count == 0) return;
        double scale = cardScale;
        int S(int px) => (int)Math.Round(px * scale);

        var work = screen.WorkingArea;
        int gap = S(CardGap), margin = S(CardMargin), cardWidth = S(CardWidth), moreWidth = S(64);
        int perRow = Math.Max(1, ((int)(work.Width * CardShare) + gap) / (cardWidth + gap));
        int lowest = work.Top + (int)(work.Height * 0.5);

        var overlay = WinEventHookService.ScreenCaptureOverlay();
        var ordered = order.Where(cards.ContainsKey).Select(k => cards[k]).ToList();
        int bottom = work.Bottom - margin;
        int placed = 0;
        while (placed < ordered.Count)
        {
            var row = ordered.Skip(placed).Take(perRow).ToList();
            int height = row.Max(c => c.Height);
            // A row that would reach too far up the screen is not started: the rest fold into "+N".
            if (placed > 0 && bottom - height < lowest) break;

            int x = work.Right - margin;
            foreach (var card in row)
            {
                x -= card.Width;
                card.Location = new Point(x, bottom - card.Height);
                if (!card.Visible) ShowCard(card, overlay);
                x -= gap;
            }
            placed += row.Count;
            bottom -= height + gap;
        }

        foreach (var card in ordered.Skip(placed)) card.Hide();
        if (placed < ordered.Count)
        {
            if (moreCard is null)
            {
                moreCard = new NeedCard(scale, moreWidth);
                moreCard.MoreClicked += () => OpenPanel(null);
            }
            moreCard.SetMore(ordered.Count - placed);
            // Above the top row, at the clock end.
            moreCard.Location = new Point(work.Right - margin - moreCard.Width, bottom - moreCard.Height);
            if (!moreCard.Visible) ShowCard(moreCard, overlay);
        }
        else
        {
            moreCard?.Dispose();
            moreCard = null;
        }

        SyncZOrder(overlay);
    }

    private static void ShowCard(NeedCard card, IntPtr overlay)
    {
        if (overlay == IntPtr.Zero) { card.Show(); return; }
        Native.SetWindowPos(card.Handle, overlay, card.Left, card.Top, card.Width, card.Height,
            Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE);
    }

    private void ClearCards()
    {
        foreach (var card in cards.Values) card.Dispose();
        cards.Clear();
        moreCard?.Dispose();
        moreCard = null;
    }

    /// <summary>Whether any card is up — what a click on the strip or the taskbar button then points at.</summary>
    private bool CardsShowing => cards.Values.Any(c => c.Visible);

    /// <summary>Raises the cards over anything that has crept over them, and makes them glow.</summary>
    private void PointAtCards()
    {
        var overlay = WinEventHookService.ScreenCaptureOverlay();
        foreach (var card in cards.Values.Where(c => c.Visible))
        {
            if (overlay == IntPtr.Zero) card.BringToFront();
            card.Pulse();
        }
        if (overlay != IntPtr.Zero) SyncZOrder(overlay);
    }

    public static void SyncAllZOrder(IntPtr overlay, bool reassert = false) => current?.SyncZOrder(overlay, reassert);

    /// <summary>Keeps visible need cards and the open list behind an active capture overlay.</summary>
    public void SyncZOrder(IntPtr overlay, bool reassert = false)
    {
        if (overlay == IntPtr.Zero && !belowOverlay && !reassert) return;
        belowOverlay = overlay != IntPtr.Zero;

        void Step(IntPtr hWnd) =>
            _ = overlay != IntPtr.Zero
                ? WinEventHookService.PlaceBelowOverlay(hWnd, overlay)
                : Native.SetWindowPos(hWnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        foreach (var card in cards.Values.Where(c => c.Visible && c.IsHandleCreated)) Step(card.Handle);
        if (moreCard is { Visible: true, IsHandleCreated: true }) Step(moreCard.Handle);
        if (panel is { IsOpen: true, IsHandleCreated: true }) Step(panel.Handle);
    }

    /// <summary>Where the indicator should be, and a sentence saying why.</summary>
    private (bool Cards, bool Taskbar, string Why) Decide()
    {
        switch (config.Orchestra.NeedsIndicator)
        {
            case "Off": return (false, false, "switched off");
            case "Taskbar": return (false, true, "taskbar button only");
            case "Cards": return (true, false, "cards only");
        }

        if (!OrchestraUsage.Enabled) return (false, false, "orchestrator feeds are switched off");

        var screen = Screen.PrimaryScreen;
        if (screen is null) return (true, false, "cards: no primary display");
        double scale = ScaleOf(screen);
        int w = (int)Math.Round(screen.WorkingArea.Width / scale), h = (int)Math.Round(screen.WorkingArea.Height / scale);
        if (w < SmallWidth || h < SmallHeight)
            return (true, false, $"cards only: the main display is laptop-sized ({w}×{h} at {scale * 100:0} %)");

        if (barShowsOrchestra())
            return (true, true, "cards, and a taskbar button: a ScweenSpit bar is showing the orchestrator strip");

        if (!SystemRestore.Visible())
            return (true, false, "cards only: the Windows taskbar is hidden");
        if (Taskbar.AutoHide)
            return (true, false, "cards only: the Windows taskbar hides itself");
        if (TaskbarThickness(scale) is { } thick && thick < CompactTaskbar)
            return (true, false, $"cards only: the Windows taskbar is compact ({thick} px)");

        return (true, true, $"cards, and a taskbar button: the main display has room ({w}×{h} at {scale * 100:0} %)");
    }

    /// <summary>The shell taskbar's thickness at 100 % scale, or null when there is none to measure.</summary>
    private static int? TaskbarThickness(double scale)
    {
        var tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !Native.GetWindowRect(tray, out var r)) return null;
        int thick = Math.Min(r.Right - r.Left, r.Bottom - r.Top);
        return thick <= 0 ? null : (int)Math.Round(thick / scale);
    }

    internal static double ScaleOf(Screen screen)
    {
        try
        {
            var centre = new Native.POINT
            {
                X = screen.Bounds.Left + screen.Bounds.Width / 2,
                Y = screen.Bounds.Top + screen.Bounds.Height / 2,
            };
            var monitor = Native.MonitorFromPoint(centre, Native.MONITOR_DEFAULTTONEAREST);
            if (GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0) return dpi / 96.0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return 1.0;
    }

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    public void Dispose()
    {
        if (ReferenceEquals(current, this)) current = null;
        OrchestraUsage.Changed -= OnChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        recheck.Dispose();
        ClearCards();
        panel?.Dispose();
    }
}

/// <summary>What clicking a need does: open its page, always as the feed gives it now.</summary>
internal static class NeedActions
{
    public static string Key(OrchestraNeedItem i) => i.Feed.Url + "\n" + i.Need.Id;

    public static bool IsApproval(OrchestraNeed need) => need.Kind.Equals("approve", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a click on the indicator should open the need's page. Not when it can be dealt with
    /// in the list itself — a question with choices, or an approval signed with a passkey right
    /// here — since the list is then where it gets done.
    /// </summary>
    public static bool GoesToPage(OrchestraNeed need) =>
        Url(need) is not null && need.Question is null && !(IsApproval(need) && need.ApproveApi);

    /// <summary>In plain words, what a need wants of the person — the line that says what each card is for.</summary>
    public static string WhatToDo(OrchestraNeed need)
    {
        bool page = Url(need) is not null;
        switch (need.Kind.ToLowerInvariant())
        {
            case "approve":
                return need.ApproveApi
                    ? "Approve it with your passkey, right here."
                    : page ? "Approve it in the Release Console. The button opens the request." : "Approve it in the Release Console.";
            case "merge":
                return page ? "Merge it by hand. The button opens it; press Mark done after." : "Merge it by hand, then press Mark done.";
            case "login":
                return page ? "Sign in again. The button opens the sign-in; press Mark done after." : "Sign in again, then press Mark done.";
            case "answer":
                if (need.Question is { Choices.Count: > 0 }) return "Pick an answer. It goes straight to the orchestrator.";
                if (need.Question is { AllowText: true }) return "Type your answer and send it to the orchestrator.";
                return "Answer it in the orchestrator's Claude session, then press Mark done.";
        }
        if (need.Command is not null) return "Run the command below (Copy command puts it on your clipboard), then press Mark done.";
        if (page) return "Do it on the page the button opens, then press Mark done.";
        return "Do this by hand, then press Mark done.";
    }

    /// <summary>The need's page, if it has one worth opening: an absolute http(s) URL, fragment and all.</summary>
    public static Uri? Url(OrchestraNeed need) =>
        need.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri : null;

    /// <summary>
    /// Opens the page the feed names for this need now, not the one it named when the row was
    /// drawn: an approval's URL becomes the request's own deep link a moment after it appears.
    /// </summary>
    public static bool OpenLatest(OrchestraNeedItem item)
    {
        var latest = OrchestraUsage.Current?.Needs.FirstOrDefault(i => Key(i) == Key(item)) ?? item;
        if (Url(latest.Need) is not { } uri) return false;
        // AbsoluteUri keeps the fragment — #approval/<id> is the whole point — and shell-executing an
        // http URL hands it to the default browser intact.
        SystemStatus.Open(uri.AbsoluteUri);
        return true;
    }

    /// <summary>Opens the page for a need named by feed and id, if it is still listed.</summary>
    public static bool OpenLatest(string feedUrl, string id)
    {
        var item = OrchestraUsage.Current?.Needs.FirstOrDefault(i => i.Feed.Url == feedUrl && i.Need.Id == id);
        return item is not null && OpenLatest(item);
    }
}

/// <summary>
/// The list of what the orchestrator is waiting on a person for: one row each, blocking ones first,
/// with whatever it takes to deal with it a click away — Open for the page, Copy for the command,
/// and Done to tell the orchestrator, which then checks the work and closes it.
///
/// In taskbar mode it is also the taskbar button: parked minimised, badged with the count, and
/// put back down rather than closed, so the button stays for as long as anything is outstanding.
/// </summary>
internal sealed class NeedsPanel : Form
{
    private static readonly uint TaskbarButtonCreated = Native.RegisterWindowMessage("TaskbarButtonCreated");

    private readonly FlowLayoutPanel list;
    private readonly Label header;
    private readonly LinkLabel showAll;
    private readonly System.Windows.Forms.Timer ages = new() { Interval = 30_000 };

    private ITaskbarList3? taskbarList;
    private bool buttonReady;
    private IntPtr overlay;
    private (int Count, bool Blocking)? badge;

    public bool InTaskbar { get; }

    public event Action? SettingsRequested;

    /// <summary>
    /// The taskbar button was clicked while parked. Returning true means the click was dealt with
    /// — the need's page opened — and the list stays parked.
    /// </summary>
    public event Func<bool>? Restoring;

    /// <summary>On screen, not parked in the taskbar.</summary>
    public bool IsOpen => Visible && WindowState != FormWindowState.Minimized;

    public NeedsPanel(bool inTaskbar)
    {
        InTaskbar = inTaskbar;
        Text = "Needs you — ScweenSpit";
        Icon = Theme.AppIcon();
        ShowInTaskbar = inTaskbar;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = inTaskbar;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Window;
        ForeColor = Theme.Text;

        header = new Label
        {
            Dock = DockStyle.Top, ForeColor = Theme.Text, BackColor = Theme.Window,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, BackColor = Theme.Window,
        };
        var settingsLink = new LinkLabel
        {
            Dock = DockStyle.Bottom, Text = "Orchestrator settings…", TextAlign = ContentAlignment.MiddleRight,
            LinkColor = Theme.Muted, ActiveLinkColor = Theme.Text, BackColor = Theme.Window,
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        settingsLink.LinkClicked += (_, _) => { Stow(); SettingsRequested?.Invoke(); };
        showAll = new LinkLabel
        {
            Dock = DockStyle.Bottom, TextAlign = ContentAlignment.MiddleLeft, Visible = false,
            LinkColor = Theme.Accent, ActiveLinkColor = Theme.Text, BackColor = Theme.Window,
            LinkBehavior = LinkBehavior.HoverUnderline, Padding = new Padding(12, 0, 0, 0),
        };
        showAll.LinkClicked += (_, _) => { only = null; Refill(force: true); Place(Location + new Size(Width / 2, Height)); };

        Controls.Add(list);
        Controls.Add(header);
        Controls.Add(settingsLink);
        Controls.Add(showAll);

        HandleCreated += (_, _) => Theme.DarkTitleBar(Handle);
        Activated += (_, _) => ages.Start();
        Deactivate += (_, _) => Stow();
        Resize += (_, _) => { if (WindowState == FormWindowState.Normal && Visible) Refill(); };
        DpiChanged += (_, _) => Refill(force: true);
        ages.Tick += (_, _) => RefreshAges();
    }

    private int S(int px) => (int)Math.Round(px * DeviceDpi / 96.0);

    // Labels do not dispose the fonts they are given, and the rows are rebuilt on every change, so
    // the fonts are made once per size and scale and kept.
    private readonly Dictionary<(string, float, FontStyle, int), Font> fonts = [];

    private Font Px(float px, FontStyle style = FontStyle.Regular, string face = "Segoe UI")
    {
        var key = (face, px, style, DeviceDpi);
        if (!fonts.TryGetValue(key, out var font))
            fonts[key] = font = new Font(face, (float)(px * DeviceDpi / 96.0), style, GraphicsUnit.Pixel);
        return font;
    }

    // ---- showing and hiding ---------------------------------------------------

    /// <summary>When set, the one need the popup shows; the rest are a link away.</summary>
    private string? only;

    /// <summary>
    /// Brings the list up near <paramref name="near"/> and gives it the keyboard — just the need
    /// keyed <paramref name="onlyKey"/> when one is given.
    /// </summary>
    public void Open(Point? near, string? onlyKey = null)
    {
        only = onlyKey;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Refill(force: true);
        Place(near);
        if (!Visible) Show();
        Activate();
    }

    /// <summary>
    /// Taskbar mode: makes sure the button is there and says how many. Parked without being
    /// activated — something arriving must not pull the list over whatever is being worked on.
    /// </summary>
    public void Park(int open, bool blocking)
    {
        Text = $"({open}) Needs you — ScweenSpit";
        SetBadge(open, blocking);
        if (Visible) return;

        Refill(force: true);
        Place(null);
        WindowState = FormWindowState.Minimized;
        Native.ShowWindow(Handle, SW_SHOWMINNOACTIVE);
    }

    /// <summary>Nothing outstanding any more: goes, unless it is open in front of somebody.</summary>
    public void Retire()
    {
        SetBadge(0, false);
        if (InTaskbar) Text = "Needs you — ScweenSpit";
        if (Visible && !(IsOpen && ContainsFocus)) Hide();
    }

    /// <summary>Put away rather than closed: back into the taskbar while anything is outstanding.</summary>
    private void Stow()
    {
        if (NeedStates.Prompting > 0) return;
        ages.Stop();
        only = null;
        if (!Visible || WindowState == FormWindowState.Minimized) return;
        if (InTaskbar && (OrchestraUsage.Current?.Outstanding ?? 0) > 0)
            WindowState = FormWindowState.Minimized;
        else
            Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Stow();
            return;
        }
        base.OnFormClosing(e);
    }

    /// <summary>Flashes the taskbar button until it is clicked, the way a waiting message does.</summary>
    public void Flash()
    {
        if (!InTaskbar || !IsHandleCreated || IsOpen) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = Handle,
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
            uCount = 0,
            dwTimeout = 0,
        };
        FlashWindowEx(ref info);
    }

    /// <summary>Next to the point it was asked from, kept inside that display's work area.</summary>
    private void Place(Point? near)
    {
        var anchor = near ?? DefaultAnchor();
        var work = Screen.FromPoint(anchor).WorkingArea;
        int margin = S(10);

        int height = Math.Min(PreferredHeight(), (int)(work.Height * 0.7));
        Size = new Size(S(460), Math.Max(S(only is null ? 180 : 120), height));

        int x = Math.Clamp(anchor.X - Width / 2, work.Left + margin, Math.Max(work.Left + margin, work.Right - Width - margin));
        int y = anchor.Y > work.Top + work.Height / 2 ? anchor.Y - Height - margin : anchor.Y + margin;
        y = Math.Clamp(y, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - Height - margin));
        Location = new Point(x, y);
    }

    /// <summary>By the clock: the corner where the taskbar's own notifications appear.</summary>
    private static Point DefaultAnchor()
    {
        var work = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        return new Point(work.Right - 240, work.Bottom - 4);
    }

    private int PreferredHeight()
    {
        int rows = list.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical);
        int chrome = Height - ClientSize.Height;
        return rows + header.Height + S(26) + (showAll.Visible ? showAll.Height : 0) + list.Padding.Vertical + chrome + S(8);
    }

    // ---- the taskbar button -----------------------------------------------------

    protected override void WndProc(ref Message m)
    {
        if (m.Msg != 0 && (uint)m.Msg == TaskbarButtonCreated)
        {
            buttonReady = true;
            ApplyBadge();
        }

        // The shell restores a minimised window by asking it to restore itself, which is the
        // chance to go straight to the page instead.
        if (m.Msg == WM_SYSCOMMAND && ((long)m.WParam & 0xFFF0) == SC_RESTORE
            && WindowState == FormWindowState.Minimized && Restoring?.Invoke() == true)
        {
            StopFlashing();
            return;
        }
        base.WndProc(ref m);
    }

    private void StopFlashing()
    {
        var info = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = Handle, dwFlags = FLASHW_STOP };
        FlashWindowEx(ref info);
    }

    private void SetBadge(int count, bool blocking)
    {
        if (!InTaskbar) return;
        var wanted = count > 0 ? (count, blocking) : ((int, bool)?)null;
        if (badge == wanted) return;
        badge = wanted;
        ApplyBadge();
    }

    /// <summary>
    /// A count on the corner of the button. Only once the shell has said the button exists — an
    /// overlay set before that is silently dropped.
    /// </summary>
    private void ApplyBadge()
    {
        if (!buttonReady || !IsHandleCreated) return;
        try
        {
            taskbarList ??= CreateTaskbarList();
            if (taskbarList is null) return;

            var previous = overlay;
            overlay = badge is { } b ? BadgeIcon(b.Count, b.Blocking) : IntPtr.Zero;
            taskbarList.SetOverlayIcon(Handle, overlay,
                badge is { } d ? $"{d.Count} need{(d.Count == 1 ? "s" : "")} you" : null);
            if (previous != IntPtr.Zero) DestroyIcon(previous);
        }
        catch (Exception ex)
        {
            Log.WriteOnce("needs-overlay", $"taskbar overlay unavailable: {ex.Message}");
        }
    }

    private static ITaskbarList3? CreateTaskbarList()
    {
        var made = (ITaskbarList3)new TaskbarListClass();
        made.HrInit();
        return made;
    }

    /// <summary>A disc in the strip's colour for needs, red when any of them is blocking, with the count on it.</summary>
    private static IntPtr BadgeIcon(int count, bool blocking)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var fill = new SolidBrush(blocking ? OrchestraStrip.BlockingColour : OrchestraStrip.NeedColour);
            g.FillEllipse(fill, 0, 0, 15, 15);
            string text = count > 9 ? "9+" : count.ToString();
            using var font = new Font("Segoe UI", text.Length > 1 ? 7f : 9f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.Black, new RectangleF(0, 0.5f, 16, 16), centred);
        }
        return bmp.GetHicon();
    }

    // ---- contents -----------------------------------------------------------

    private string shownKey = "";

    /// <summary>Rebuilds the rows when what they would say has changed.</summary>
    public void Refill(bool force = false)
    {
        if (IsDisposed) return;
        var all = OrchestraUsage.Current?.Needs ?? [];
        // One need, when that is what was clicked and it is still listed.
        var items = only is { } one && all.Any(i => Key(i) == one) ? all.Where(i => Key(i) == one).ToList() : all;
        bool single = items.Count == 1 && all.Count > 1 && only is not null;
        showAll.Visible = single;
        showAll.Text = $"All {all.Count} waiting…";
        string key = (single ? "1:" : "") + string.Join("|", items.Select(Key)) + $"#{DeviceDpi}";
        if (!force && key == shownKey)
        {
            // The same needs: each view brings itself up to date, keeping whatever is typed in it.
            foreach (var view in list.Controls.OfType<NeedView>())
                if (items.FirstOrDefault(i => Key(i) == Key(view.Item)) is { } fresh) view.Set(fresh);
            return;
        }
        shownKey = key;

        header.Font = Px(15, FontStyle.Bold);
        header.Height = S(36);
        header.Padding = new Padding(S(12), 0, S(12), 0);
        int open = items.Count(i => i.Waiting);
        header.Text = only is not null && items.Count == 1
            ? $"{OrchestraUsage.NeedKindLabel(items[0].Need.Kind)}{(items[0].Need.Blocking ? " — blocking" : "")}"
            : items.Count == 0 ? "Nothing needs you" : open == 0 ? "Done — waiting for the orchestrator" : $"{open} need{(open == 1 ? "s" : "")} you";
        list.Padding = new Padding(S(10), S(2), S(10), S(6));

        list.SuspendLayout();
        while (list.Controls.Count > 0) list.Controls[0].Dispose();

        int width = S(460) - S(20) - SystemInformation.VerticalScrollBarWidth - S(16);
        if (items.Count == 0)
        {
            list.Controls.Add(new Label
            {
                Text = "The orchestrator is not waiting on you for anything.", AutoSize = true,
                ForeColor = Theme.Muted, Font = Px(13), Margin = new Padding(S(4), S(8), 0, 0),
            });
        }
        foreach (var item in items)
        {
            var view = new NeedView(item, width, DeviceDpi / 96.0) { Margin = new Padding(0, 0, 0, S(8)) };
            view.Rebuilt += () => { if (IsOpen) FitHeight(); };
            list.Controls.Add(view);
        }
        list.ResumeLayout(true);

        if (IsOpen) FitHeight();
    }

    /// <summary>Grows or shrinks with the list, keeping the edge nearest the taskbar where it was.</summary>
    private void FitHeight()
    {
        {
            var work = Screen.FromControl(this).WorkingArea;
            int bottom = Bottom;
            int height = Math.Max(S(only is null ? 180 : 120), Math.Min(PreferredHeight(), (int)(work.Height * 0.7)));
            bool anchoredLow = Top + Height / 2 > work.Top + work.Height / 2;
            Height = height;
            if (anchoredLow) Top = Math.Max(work.Top + S(10), bottom - height);
        }
    }

    private static string Key(OrchestraNeedItem i) => NeedActions.Key(i);

    private void RefreshAges()
    {
        foreach (var view in list.Controls.OfType<NeedView>()) view.RefreshAge();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ages.Dispose();
            foreach (var font in fonts.Values) font.Dispose();
            fonts.Clear();
            if (taskbarList is not null)
            {
                try { Marshal.ReleaseComObject(taskbarList); } catch { }
                taskbarList = null;
            }
        }
        if (overlay != IntPtr.Zero)
        {
            DestroyIcon(overlay);
            overlay = IntPtr.Zero;
        }
        base.Dispose(disposing);
    }

    // ---- interop --------------------------------------------------------------

    private const int SW_SHOWMINNOACTIVE = 7;
    private const uint FLASHW_STOP = 0x0, FLASHW_TRAY = 0x2, FLASHW_TIMERNOFG = 0xC;
    private const int WM_SYSCOMMAND = 0x0112, SC_RESTORE = 0xF120;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    // Every method of the interfaces it extends, in order: COM calls by slot, not by name, so
    // leaving any out would send SetOverlayIcon somewhere else entirely.
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
        void RegisterTab(IntPtr tab, IntPtr mdi);
        void UnregisterTab(IntPtr tab);
        void SetTabOrder(IntPtr tab, IntPtr insertBefore);
        void SetTabActive(IntPtr tab, IntPtr mdi, uint reserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint count, IntPtr buttons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint count, IntPtr buttons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
        void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? tip);
        void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListClass { }
}

/// <summary>
/// One need, floating above the taskbar by the clock: a <see cref="NeedView"/> in a frame edged
/// in its colour, acted on right where it is. Appears without taking the focus; clicking into it
/// takes it, as typing an answer needs. Also the "+N" card for needs that did not fit.
/// </summary>
internal sealed class NeedCard : Form
{
    private readonly double scale;
    private readonly int width;
    private readonly System.Windows.Forms.Timer pulse = new() { Interval = 33 };
    private long pulseStart;
    private NeedView? view;
    private int more;

    public OrchestraNeedItem? Item => view?.Item;

    /// <summary>The card wants a different height: its details opened or closed, or it was rebuilt.</summary>
    public event Action? Resized;

    /// <summary>The "+N" card was clicked.</summary>
    public event Action? MoreClicked;

    public NeedCard(double scale, int width)
    {
        this.scale = scale;
        this.width = width;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Theme.Panel;
        Padding = new Padding(S(2));

        pulse.Tick += (_, _) =>
        {
            if (Environment.TickCount64 - pulseStart > 6000) pulse.Stop();
            Invalidate();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    private int S(int px) => (int)Math.Round(px * scale);

    public void Set(OrchestraNeedItem item)
    {
        more = 0;
        if (view is null)
        {
            view = new NeedView(item, width - Padding.Horizontal, scale) { Location = new Point(Padding.Left, Padding.Top) };
            view.Rebuilt += Fit;
            view.SizeChanged += (_, _) => Fit();
            Controls.Add(view);
            Fit();
        }
        else
        {
            view.Set(item);
        }
    }

    public void SetMore(int count)
    {
        more = count;
        Cursor = Cursors.Hand;
        Size = new Size(S(64), S(64));
        Invalidate();
    }

    public void RefreshAge() => view?.RefreshAge();

    private void Fit()
    {
        if (view is null) return;
        var wanted = new Size(width, view.PreferredSize.Height + Padding.Vertical);
        if (Size == wanted) return;
        Size = wanted;
        Resized?.Invoke();
    }

    /// <summary>Glows for a few seconds: it has just arrived, or somebody asked where the cards are.</summary>
    public void Pulse()
    {
        pulseStart = Environment.TickCount64;
        pulse.Start();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!IsHandleCreated) return;
        var region = Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, S(12), S(12));
        if (region != IntPtr.Zero) Native.SetWindowRgn(Handle, region, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        OnResize(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var accent = Item is not { } item ? Theme.Muted
                   : !item.Waiting ? Theme.Muted : item.Need.Blocking ? OrchestraStrip.BlockingColour : OrchestraStrip.NeedColour;
        float glow = pulse.Enabled ? (float)(0.5 + 0.5 * Math.Sin((Environment.TickCount64 - pulseStart) / 160.0)) : 0f;

        using (var fill = new SolidBrush(glow > 0 ? Blend(accent, Color.White, 0.35f * glow) : accent))
            g.FillRectangle(fill, ClientRectangle);
        if (view is not null) return;

        using (var back = new SolidBrush(Theme.Panel))
            g.FillRectangle(back, Rectangle.Inflate(ClientRectangle, -S(2), -S(2)));
        using var font = new Font("Segoe UI", (float)(15 * scale), FontStyle.Bold, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, $"+{more}\nmore", font, ClientRectangle, Theme.Text,
                              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (view is null && e.Button == MouseButtons.Left) MoreClicked?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) pulse.Dispose();
        base.Dispose(disposing);
    }
}
