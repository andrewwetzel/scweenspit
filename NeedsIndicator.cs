using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScweenSpit;

/// <summary>
/// Keeps what the orchestrator is waiting on a person for in sight while ScweenSpit runs: a button
/// in the Windows taskbar, badged with the count and flashed when something new arrives, or on a
/// screen too small to give the taskbar anything more to carry, a small pill that floats over
/// everything. Either opens <see cref="NeedsPanel"/>, the list itself. The bar's orchestrator strip
/// carries its own Needs row besides, wherever it is drawn.
///
/// Gone whenever nothing is outstanding: an indicator that is always there stops being looked at.
/// </summary>
public sealed class NeedsIndicator : IDisposable
{
    public enum Mode { Off, Taskbar, Floating }

    /// <summary>The choices Settings offers, in the order it offers them.</summary>
    public static readonly string[] Choices = ["Automatic", "Taskbar", "Floating", "Off"];

    /// <summary>
    /// Below this, in pixels at 100 % scale, a display is laptop-sized: the work area of a 1080p
    /// panel at 125 % or more, or a 1366×768 one at any scale.
    /// </summary>
    private const int SmallWidth = 1600, SmallHeight = 900;

    /// <summary>A taskbar thinner than this at 100 % scale has small buttons, too small to badge legibly.</summary>
    private const int CompactTaskbar = 40;

    private readonly SplitConfig config;
    private readonly Func<bool> barShowsOrchestra;
    private readonly Action save;
    private readonly Action<string> openSettings;
    private readonly Action<Action> post;

    // Rare, and only while something is waiting: auto-hide can be switched on without any event
    // reaching us, and that is enough to change the answer.
    private readonly System.Windows.Forms.Timer recheck = new() { Interval = 10_000 };

    private NeedsPanel? panel;
    private NeedsPill? pill;
    private Mode mode = Mode.Off;

    /// <summary>What the last decision was and why, for the settings page.</summary>
    public static string LastDecision { get; private set; } = "";

    public NeedsIndicator(SplitConfig config, Func<bool> barShowsOrchestra, Action save,
                          Action<string> openSettings, Action<Action> post)
    {
        this.config = config;
        this.barShowsOrchestra = barShowsOrchestra;
        this.save = save;
        this.openSettings = openSettings;
        this.post = post;

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

        var (wanted, why) = Decide();
        LastDecision = why;
        if (wanted != mode)
        {
            Log.Write($"needs indicator: {mode} → {wanted} ({why})");
            mode = wanted;
            pill?.Dispose();
            pill = null;
            // A panel open on screen is left alone; it goes when it is next put away.
            if (panel is { } p && !p.IsOpen) { p.Dispose(); panel = null; }
        }

        recheck.Enabled = open > 0;

        if (mode == Mode.Taskbar && open > 0)
            PanelFor(inTaskbar: true).Park(open, blocking);
        else
            panel?.Retire();

        if (mode == Mode.Floating && open > 0)
        {
            pill ??= NewPill();
            pill.Set(open, blocking);
            pill.PlaceAt(config.Orchestra.NeedsPillX, config.Orchestra.NeedsPillY);
        }
        else
        {
            pill?.Hide();
        }

        panel?.Refill();
    }

    /// <summary>Opens the list near <paramref name="near"/>, or in the corner by the clock.</summary>
    public void OpenPanel(Point? near)
    {
        PanelFor(inTaskbar: mode == Mode.Taskbar).Open(near);
    }

    /// <summary>
    /// Needs whose page has already been opened by a click on the indicator. The first click goes
    /// straight to the page; the next one opens the list, where Done and everything else lives.
    /// </summary>
    private readonly HashSet<string> shortcutTaken = new(StringComparer.Ordinal);

    /// <summary>
    /// A click on the pill, the taskbar button or the strip: with exactly one need outstanding and a
    /// page to open for it — and nothing to do for it in the list itself — that page; otherwise the list. Returns true when it opened the page.
    /// </summary>
    public bool Activate(Point? near)
    {
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

    /// <summary>
    /// A need's own card was clicked: a small popup with just that need and what can be done
    /// about it. A right-click opens the whole list.
    /// </summary>
    public void ActOn(OrchestraNeedItem item, Point near, bool list)
    {
        PanelFor(inTaskbar: mode == Mode.Taskbar).Open(near, list ? null : NeedActions.Key(item));
    }

    /// <summary>Something new arrived: draw the eye to wherever the indicator is.</summary>
    public void Announce()
    {
        if (mode == Mode.Taskbar) panel?.Flash();
        if (mode == Mode.Floating) pill?.Pulse();
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
            var open = OrchestraUsage.Current?.Needs.Where(i => i.Waiting).ToList() ?? [];
            shortcutTaken.RemoveWhere(k => !open.Any(i => NeedActions.Key(i) == k));
            if (open.Count != 1 || !NeedActions.GoesToPage(open[0].Need) || !shortcutTaken.Add(NeedActions.Key(open[0])))
                return false;
            NeedActions.OpenLatest(open[0]);
            return true;
        };
        return panel;
    }

    private NeedsPill NewPill()
    {
        var made = new NeedsPill();
        made.Clicked += () => Activate(Cursor.Position);
        made.ListRequested += () => OpenPanel(Cursor.Position);
        made.Moved += at =>
        {
            config.Orchestra.NeedsPillX = at.X;
            config.Orchestra.NeedsPillY = at.Y;
            save();
        };
        return made;
    }

    /// <summary>Where the indicator should be, and a sentence saying why.</summary>
    private (Mode Mode, string Why) Decide()
    {
        switch (config.Orchestra.NeedsIndicator)
        {
            case "Off": return (Mode.Off, "switched off");
            case "Taskbar": return (Mode.Taskbar, "set to the taskbar");
            case "Floating": return (Mode.Floating, "set to float");
        }

        if (!OrchestraUsage.Enabled) return (Mode.Off, "orchestrator feeds are switched off");

        var screen = Screen.PrimaryScreen;
        if (screen is null) return (Mode.Floating, "no primary display");
        double scale = ScaleOf(screen);
        int w = (int)Math.Round(screen.WorkingArea.Width / scale), h = (int)Math.Round(screen.WorkingArea.Height / scale);
        if (w < SmallWidth || h < SmallHeight)
            return (Mode.Floating, $"floating: the main display is laptop-sized ({w}×{h} at {scale * 100:0} %)");

        if (barShowsOrchestra())
            return (Mode.Taskbar, "taskbar: a ScweenSpit bar is showing the orchestrator strip");

        if (!SystemRestore.Visible())
            return (Mode.Floating, "floating: the Windows taskbar is hidden");
        if (Taskbar.AutoHide)
            return (Mode.Floating, "floating: the Windows taskbar hides itself");
        if (TaskbarThickness(scale) is { } thick && thick < CompactTaskbar)
            return (Mode.Floating, $"floating: the Windows taskbar is compact ({thick} px)");

        return (Mode.Taskbar, $"taskbar: the main display has room ({w}×{h} at {scale * 100:0} %)");
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
        OrchestraUsage.Changed -= OnChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        recheck.Dispose();
        pill?.Dispose();
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
    private readonly List<(Label Label, OrchestraNeedItem Item)> ageLabels = [];
    private readonly HashSet<string> sending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> failed = new(StringComparer.Ordinal);

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
        if (holdOpen) return;
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
        string key = (single ? "1:" : "") + string.Join("|", items.Select(i => $"{Key(i)}:{i.Done}:{i.Feed.Stale}:{i.Need.GetHashCode()}"))
                     + $"#{string.Join(",", sending)}#{string.Join(",", failed.Select(f => f.Key + f.Value))}#{DeviceDpi}";
        if (!force && key == shownKey) return;
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
        ageLabels.Clear();

        int width = S(460) - S(20) - SystemInformation.VerticalScrollBarWidth - S(16);
        if (items.Count == 0)
        {
            list.Controls.Add(new Label
            {
                Text = "The orchestrator is not waiting on you for anything.", AutoSize = true,
                ForeColor = Theme.Muted, Font = Px(13), Margin = new Padding(S(4), S(8), 0, 0),
            });
        }
        foreach (var item in items) list.Controls.Add(Row(item, width));
        list.ResumeLayout(true);

        if (IsOpen)
        {
            // Grow or shrink with the list, keeping the edge nearest the taskbar where it was.
            var work = Screen.FromControl(this).WorkingArea;
            int bottom = Bottom;
            int height = Math.Max(S(only is null ? 180 : 120), Math.Min(PreferredHeight(), (int)(work.Height * 0.7)));
            bool anchoredLow = Top + Height / 2 > work.Top + work.Height / 2;
            Height = height;
            if (anchoredLow) Top = Math.Max(work.Top + S(10), bottom - height);
        }
    }

    private static string Key(OrchestraNeedItem i) => NeedActions.Key(i);

    private Control Row(OrchestraNeedItem item, int width)
    {
        var need = item.Need;
        bool done = item.Done;
        var accent = done ? Theme.Divider : need.Blocking ? OrchestraStrip.BlockingColour : OrchestraStrip.NeedColour;

        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Panel,
            Padding = new Padding(S(12), S(8), S(10), S(8)), Margin = new Padding(0, 0, 0, S(6)),
            MinimumSize = new Size(width, 0), MaximumSize = new Size(width, 0),
        };
        row.Paint += (_, e) =>
        {
            using var bar = new SolidBrush(accent);
            e.Graphics.FillRectangle(bar, 0, 0, S(4), row.Height);
        };
        int inner = width - row.Padding.Horizontal;

        // Kind, and whether something is waiting on it.
        var chips = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, S(3)), BackColor = Theme.Panel };
        chips.Controls.Add(Chip(OrchestraUsage.NeedKindLabel(need.Kind).ToUpperInvariant(), done ? Theme.Muted : OrchestraStrip.NeedColour));
        if (need.Blocking && !done) chips.Controls.Add(Chip("BLOCKING", OrchestraStrip.BlockingColour));
        if (done) chips.Controls.Add(Chip("DONE", Theme.Muted));
        row.Controls.Add(chips);

        row.Controls.Add(new Label
        {
            Text = need.Title, AutoSize = true, MaximumSize = new Size(inner, 0),
            ForeColor = done ? Theme.Muted : Theme.Text, Font = Px(14, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, S(2)), UseMnemonic = false,
        });

        var meta = new Label
        {
            AutoSize = true, MaximumSize = new Size(inner, 0), ForeColor = Theme.Muted, Font = Px(12),
            Margin = new Padding(0, 0, 0, S(2)), UseMnemonic = false,
        };
        meta.Text = MetaText(item);
        ageLabels.Add((meta, item));
        row.Controls.Add(meta);

        if (need.Detail is { } detail)
            row.Controls.Add(new Label
            {
                Text = detail, AutoSize = true, MaximumSize = new Size(inner, 0), ForeColor = Theme.Muted,
                Font = Px(12), Margin = new Padding(0, 0, 0, S(4)), UseMnemonic = false,
            });

        if (need.Command is { } command)
            row.Controls.Add(new TextBox
            {
                Text = command, ReadOnly = true, Width = inner, BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.Raised, ForeColor = Theme.Text,
                Font = Px(12, FontStyle.Regular, "Consolas"),
                Margin = new Padding(0, S(2), 0, S(4)),
            });

        string key = Key(item);
        bool busy = sending.Contains(key);

        // A question: its choices are buttons right here, so answering it needs nothing else open.
        if (need.Question is { } question && !done)
        {
            var picked = picks.TryGetValue(key, out var set) ? set : picks[key] = new HashSet<string>(StringComparer.Ordinal);
            foreach (var choice in question.Choices)
            {
                if (question.Multi)
                {
                    var tick = new CheckBox
                    {
                        Text = choice.Label, Checked = picked.Contains(choice.Id), AutoSize = true, Enabled = !busy,
                        ForeColor = Theme.Text, Font = Px(13), FlatStyle = FlatStyle.Flat, UseMnemonic = false,
                        Margin = new Padding(0, S(2), 0, choice.Description is null ? S(2) : 0), Cursor = Cursors.Hand,
                    };
                    tick.CheckedChanged += (_, _) => { if (tick.Checked) picked.Add(choice.Id); else picked.Remove(choice.Id); };
                    row.Controls.Add(tick);
                }
                else
                {
                    var pick = ChoiceButton(choice.Label, inner);
                    pick.Enabled = !busy;
                    pick.Click += (_, _) => Run(key, () => OrchestraUsage.AnswerAsync(item, [choice.Id], Draft(key)));
                    row.Controls.Add(pick);
                }
                if (choice.Description is { } about)
                    row.Controls.Add(new Label
                    {
                        Text = about, AutoSize = true, MaximumSize = new Size(inner - S(8), 0), ForeColor = Theme.Muted,
                        Font = Px(11.5f), Margin = new Padding(S(question.Multi ? 20 : 8), 0, 0, S(4)), UseMnemonic = false,
                    });
            }

            if (question.AllowText)
            {
                var typed = new TextBox
                {
                    Text = Draft(key) ?? "", Width = inner, BorderStyle = BorderStyle.FixedSingle, Enabled = !busy,
                    BackColor = Theme.Raised, ForeColor = Theme.Text, Font = Px(13),
                    PlaceholderText = question.Choices.Count > 0 ? "Or type an answer…" : "Type an answer…",
                    Margin = new Padding(0, S(4), 0, S(2)),
                };
                typed.TextChanged += (_, _) => drafts[key] = typed.Text;
                typed.KeyDown += (_, e) =>
                {
                    if (e.KeyCode != Keys.Enter || string.IsNullOrWhiteSpace(typed.Text)) return;
                    e.SuppressKeyPress = true;
                    Run(key, () => OrchestraUsage.AnswerAsync(item, question.Multi ? picked.ToList() : [], typed.Text));
                };
                row.Controls.Add(typed);
            }
        }

        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(inner, 0), Margin = new Padding(0, S(2), 0, 0), BackColor = Theme.Panel };
        bool approval = NeedActions.IsApproval(need);

        if (need.Question is { } q && !done && (q.Multi || q.AllowText))
        {
            var send = ActionButton(busy ? "Sending…" : "Send", primary: true);
            send.Enabled = !busy;
            send.Click += (_, _) =>
            {
                var chosen = q.Multi && picks.TryGetValue(key, out var p) ? p.ToList() : [];
                if (chosen.Count == 0 && string.IsNullOrWhiteSpace(Draft(key))) return;
                Run(key, () => OrchestraUsage.AnswerAsync(item, chosen, Draft(key)));
            };
            actions.Controls.Add(send);
        }

        // Approved right here: Windows Hello signs the console's challenge, no browser involved.
        bool native = approval && need.ApproveApi && !done;
        if (native)
        {
            var approve = ActionButton(busy ? "Waiting for passkey…" : "Approve with passkey", primary: true);
            approve.Width = S(190);
            approve.Enabled = !busy;
            approve.Click += (_, _) => Run(key, () => OrchestraUsage.ApproveWithPasskeyAsync(item, Handle), holdOpen: true);
            actions.Controls.Add(approve);
        }

        if (NeedActions.Url(need) is not null)
        {
            // Without a native approval, an approval's whole point is that page, so it is the big
            // button and the row itself opens it too. The URL is looked up again on click: it
            // changes as the request appears.
            var openButton = ActionButton(native ? "Open in console" : approval ? "Approve →" : "Open",
                                          primary: !native && (approval || (!done && need.Command is null && need.Question is null)));
            if (approval) openButton.Width = S(native ? 140 : 124);
            openButton.Click += (_, _) => NeedActions.OpenLatest(item);
            actions.Controls.Add(openButton);

            if (approval && !native)
            {
                void OpenFromRow(object? sender, EventArgs e) => NeedActions.OpenLatest(item);
                row.Cursor = Cursors.Hand;
                row.Click += OpenFromRow;
                foreach (Control c in row.Controls)
                {
                    if (c is TextBox) continue;
                    c.Cursor = Cursors.Hand;
                    c.Click += OpenFromRow;
                    foreach (Control chip in c.Controls) { chip.Cursor = Cursors.Hand; chip.Click += OpenFromRow; }
                }
            }
        }
        if (need.Command is { } toCopy)
        {
            var copy = ActionButton("Copy", primary: !done);
            copy.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(toCopy);
                    copy.Text = "Copied";
                }
                catch (Exception ex)
                {
                    Log.Write($"could not copy need command: {ex.Message}");
                    copy.Text = "Copy failed";
                }
            };
            actions.Controls.Add(copy);
        }

        if (done)
        {
            actions.Controls.Add(new Label
            {
                Text = "Done, waiting for the orchestrator to confirm", AutoSize = true, ForeColor = Theme.Muted,
                Font = Px(12, FontStyle.Italic), Margin = new Padding(0, S(7), 0, 0),
            });
        }
        else
        {
            var doneButton = ActionButton(busy && !native ? "Sending…" : "Done", primary: false);
            doneButton.Enabled = !busy;
            doneButton.Click += (_, _) => Run(key, () => OrchestraUsage.MarkDoneAsync(item));
            actions.Controls.Add(doneButton);
        }
        row.Controls.Add(actions);

        if (failed.TryGetValue(key, out var why))
            row.Controls.Add(new Label
            {
                Text = why == "cancelled" ? "Passkey prompt dismissed — nothing was sent." : $"Did not go through: {why}",
                AutoSize = true, MaximumSize = new Size(inner, 0),
                ForeColor = OrchestraStrip.BlockingColour, Font = Px(12), Margin = new Padding(0, S(4), 0, 0),
            });

        return row;
    }

    /// <summary>Typed answers and ticked choices, kept across rebuilds of the rows until sent.</summary>
    private readonly Dictionary<string, string> drafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> picks = new(StringComparer.Ordinal);

    /// <summary>
    /// Set while a passkey prompt is up: it takes the focus, and the list must not put itself away
    /// underneath the prompt it owns.
    /// </summary>
    private bool holdOpen;

    private string? Draft(string key) => drafts.TryGetValue(key, out var d) && !string.IsNullOrWhiteSpace(d) ? d : null;

    /// <summary>Runs one of a row's actions, showing it as in progress and any failure on the row.</summary>
    private async void Run(string key, Func<Task<string?>> action, bool holdOpen = false)
    {
        if (!sending.Add(key)) return;
        failed.Remove(key);
        this.holdOpen |= holdOpen;
        // Not straight away: the rebuild disposes the very button being clicked.
        BeginInvoke(() => Refill());
        string? error;
        try { error = await action(); }
        catch (Exception ex) { error = ex.Message; }
        sending.Remove(key);
        if (holdOpen) this.holdOpen = false;
        if (error is null) { drafts.Remove(key); picks.Remove(key); }
        else failed[key] = error;
        if (IsDisposed) return;
        Refill();
        if (holdOpen && error is null && !ContainsFocus) Activate();
    }

    private Button ChoiceButton(string text, int width)
    {
        var b = new Button
        {
            Text = text, AutoSize = false, Size = new Size(width, S(32)), Font = Px(13, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat, ForeColor = Theme.Text, BackColor = Theme.Raised, Cursor = Cursors.Hand,
            Margin = new Padding(0, S(3), 0, S(1)), TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(S(8), 0, 0, 0), UseMnemonic = false,
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Theme.Divider;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(Theme.Accent, 0.1f);
        return b;
    }

    private static string MetaText(OrchestraNeedItem item)
    {
        var parts = new List<string>(3);
        if (item.Need.Where is { Length: > 0 } where) parts.Add(where);
        if (item.Need.Task is { } task) parts.Add(task);
        if (item.Age(Environment.TickCount64) is { } age)
            parts.Add(age.TotalMinutes < 1 ? "just now" : $"{OrchestraUsage.FormatAge(age)} ago");
        if (item.Feed.Stale) parts.Add("feed not answering");
        return parts.Count == 0 ? item.Feed.Server : string.Join(" · ", parts);
    }

    private void RefreshAges()
    {
        foreach (var (label, item) in ageLabels)
            if (!label.IsDisposed) label.Text = MetaText(item);
    }

    private Label Chip(string text, Color colour) => new()
    {
        Text = text, AutoSize = true, ForeColor = Theme.Window, BackColor = colour, Font = Px(10.5f, FontStyle.Bold),
        Padding = new Padding(S(4), S(1), S(4), S(1)), Margin = new Padding(0, 0, S(6), 0), UseMnemonic = false,
    };

    private Button ActionButton(string text, bool primary)
    {
        var b = new Button
        {
            Text = text, AutoSize = false, Size = new Size(S(96), S(28)), Font = Px(13),
            FlatStyle = FlatStyle.Flat, ForeColor = primary ? Color.White : Theme.Text,
            BackColor = primary ? Theme.Accent : Theme.Raised, Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, S(8), 0), TextAlign = ContentAlignment.MiddleCenter,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Theme.Accent, 0.15f) : Theme.Divider;
        return b;
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
/// The floating indicator: "! 2 need you", always on top, never taking focus. Click it for the list;
/// drag it out of the way and it stays where it was put.
/// </summary>
internal sealed class NeedsPill : Form
{
    private const int DragThreshold = 4;

    private readonly ToolTip tip = new() { InitialDelay = 300 };
    private readonly System.Windows.Forms.Timer pulse = new() { Interval = 33 };
    private long pulseStart;
    private string text = "";
    private bool blocking;
    private Point? pressAt;
    private Point grabOffset;
    private bool dragging;
    private (int? X, int? Y) placedFrom = (int.MinValue, int.MinValue);

    /// <summary>Clicked without being dragged.</summary>
    public event Action? Clicked;

    /// <summary>Right-clicked: the list, whatever there is to go straight to.</summary>
    public event Action? ListRequested;

    /// <summary>Dropped somewhere new, with its top-left corner in screen pixels.</summary>
    public event Action<Point>? Moved;

    public NeedsPill()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Theme.Panel;
        Cursor = Cursors.Hand;
        tip.SetToolTip(this, "Click for what needs you (straight to its page when there is just one); right-click for the list; drag to move");

        pulse.Tick += (_, _) =>
        {
            if (Environment.TickCount64 - pulseStart > 6000) pulse.Stop();
            Invalidate();
        };
        DpiChanged += (_, _) => Fit();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    private int S(int px) => (int)Math.Round(px * DeviceDpi / 96.0);

    public void Set(int open, bool anyBlocking)
    {
        string wanted = $"{open} need{(open == 1 ? "s" : "")} you";
        if (wanted != text || anyBlocking != blocking)
        {
            text = wanted;
            blocking = anyBlocking;
            Fit();
            Invalidate();
        }
        if (!Visible) Show();
    }

    /// <summary>Puts it where it was left, or in the corner above the clock — on screen either way.</summary>
    public void PlaceAt(int? x, int? y)
    {
        if (dragging || placedFrom == (x, y)) return;
        placedFrom = (x, y);

        Rectangle work;
        Point at;
        if (x is { } px && y is { } py && Screen.AllScreens.Any(s => s.WorkingArea.Contains(new Point(px + Width / 2, py + Height / 2))))
        {
            at = new Point(px, py);
            work = Screen.FromPoint(at).WorkingArea;
        }
        else
        {
            work = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
            at = new Point(work.Right - Width - S(16), work.Bottom - Height - S(16));
        }

        Location = new Point(Math.Clamp(at.X, work.Left, Math.Max(work.Left, work.Right - Width)),
                             Math.Clamp(at.Y, work.Top, Math.Max(work.Top, work.Bottom - Height)));
    }

    /// <summary>Glows for a few seconds: something new has arrived.</summary>
    public void Pulse()
    {
        pulseStart = Environment.TickCount64;
        pulse.Start();
        Invalidate();
    }

    private void Fit()
    {
        using var font = PillFont();
        int textWidth = TextRenderer.MeasureText(text, font).Width;
        Size = new Size(S(12) + S(14) + S(6) + textWidth + S(10), S(30));
        var region = Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, Height, Height);
        if (region != IntPtr.Zero && IsHandleCreated) Native.SetWindowRgn(Handle, region, true);
        placedFrom = (int.MinValue, int.MinValue);   // re-clamp at the new size
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Fit();
    }

    private Font PillFont() => new("Segoe UI", (float)(13 * DeviceDpi / 96.0), FontStyle.Bold, GraphicsUnit.Pixel);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var colour = blocking ? OrchestraStrip.BlockingColour : OrchestraStrip.NeedColour;

        float glow = pulse.Enabled ? (float)(0.5 + 0.5 * Math.Sin((Environment.TickCount64 - pulseStart) / 160.0)) : 0f;
        using (var back = new SolidBrush(glow > 0 ? Blend(Theme.Panel, colour, 0.12f + 0.25f * glow) : Theme.Panel))
            g.FillRectangle(back, ClientRectangle);
        using (var rim = new Pen(colour, S(2)))
            g.DrawPath(rim, Capsule(new RectangleF(S(1), S(1), Width - S(2) - 1, Height - S(2) - 1)));

        int mark = S(14);
        OrchestraStrip.PaintNeedsMark(g, new RectangleF(S(10), (Height - mark) / 2f, mark, mark), colour);

        using var font = PillFont();
        TextRenderer.DrawText(g, text, font, new Rectangle(S(10) + mark + S(6), 0, Width, Height), Theme.Text,
                              TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }

    private static GraphicsPath Capsule(RectangleF r)
    {
        var path = new GraphicsPath();
        float d = r.Height;
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }

    private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        pressAt = Cursor.Position;
        grabOffset = new Point(Cursor.Position.X - Left, Cursor.Position.Y - Top);
        dragging = false;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (pressAt is not { } start) return;
        var now = Cursor.Position;
        if (!dragging && Math.Abs(now.X - start.X) + Math.Abs(now.Y - start.Y) < DragThreshold) return;
        dragging = true;
        Cursor = Cursors.SizeAll;
        Location = new Point(now.X - grabOffset.X, now.Y - grabOffset.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right && pressAt is null)
        {
            ListRequested?.Invoke();
            return;
        }
        if (pressAt is null) return;
        pressAt = null;
        Capture = false;
        Cursor = Cursors.Hand;

        if (dragging)
        {
            dragging = false;
            // Kept on the display it was dropped on, whole.
            var work = Screen.FromPoint(new Point(Left + Width / 2, Top + Height / 2)).WorkingArea;
            Location = new Point(Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width)),
                                 Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height)));
            placedFrom = (Left, Top);
            Moved?.Invoke(Location);
        }
        else if (e.Button == MouseButtons.Left)
        {
            Clicked?.Invoke();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            pulse.Dispose();
            tip.Dispose();
        }
        base.Dispose(disposing);
    }
}
