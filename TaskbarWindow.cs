using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using static ScweenSpit.Native;

namespace ScweenSpit;

/// <summary>
/// A taskbar of our own, docked to an edge of one display as a Win32 appbar. Windows reserves the
/// space for it exactly as it does for the shell's own bar, so every application keeps clear —
/// which is what makes a vertical bar possible on Windows 11, where the shell refuses to put its
/// taskbar anywhere but the bottom.
/// </summary>
public sealed class TaskbarWindow : Form
{
    private const int ButtonExtent = 34;   // height of a button on a side bar, width on a top/bottom one
    private const int ClockExtent = 44;
    private const int Inset = 4;

    private readonly AppBar appBar;
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    private readonly string device;
    private readonly bool thisDisplayOnly;

    private List<TaskWindow> windows = [];
    private readonly Dictionary<IntPtr, Bitmap> icons = [];
    private int hovered = -1;

    public BarEdge Edge { get; }

    public TaskbarWindow(MonitorGeometry monitor, BarEdge edge, int thickness, bool thisDisplayOnly)
    {
        Edge = edge;
        device = monitor.Device;
        this.thisDisplayOnly = thisDisplayOnly;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        BackColor = Theme.Panel;
        DoubleBuffered = true;

        appBar = new AppBar(this);
        appBar.PositionChanged += () => appBar.Reserve(monitor.Bounds, edge, thickness);

        Load += (_, _) =>
        {
            appBar.Register();
            appBar.Reserve(monitor.Bounds, edge, thickness);
            Rebuild();
        };

        refresh.Tick += (_, _) => Rebuild();
        refresh.Start();
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

    protected override void WndProc(ref Message m)
    {
        if (appBar.HandleMessage(ref m)) return;
        base.WndProc(ref m);
    }

    private bool Vertical => Edge is BarEdge.Left or BarEdge.Right;

    // ---- contents ----------------------------------------------------------

    private void Rebuild()
    {
        var fresh = WindowList.Enumerate(thisDisplayOnly ? device : null);

        bool changed = fresh.Count != windows.Count;
        if (!changed)
            for (int i = 0; i < fresh.Count; i++)
                if (fresh[i].Handle != windows[i].Handle || fresh[i].Title != windows[i].Title
                    || fresh[i].Minimised != windows[i].Minimised) { changed = true; break; }

        windows = fresh;

        foreach (var w in windows)
            if (!icons.ContainsKey(w.Handle))
                icons[w.Handle] = WindowList.IconFor(w.Handle) ?? new Bitmap(16, 16);

        foreach (var stale in icons.Keys.Where(h => !IsWindow(h)).ToList())
        {
            icons[stale].Dispose();
            icons.Remove(stale);
        }

        // The clock ticks even when nothing else moves.
        Invalidate();
        _ = changed;
    }

    private Rectangle SlotAt(int index) => Vertical
        ? new Rectangle(Inset, Inset + index * ButtonExtent, Width - 2 * Inset, ButtonExtent - 2)
        : new Rectangle(Inset + index * 180, Inset, 178, Height - 2 * Inset);

    private int SlotUnder(Point p)
    {
        for (int i = 0; i < windows.Count; i++)
            if (SlotAt(i).Contains(p)) return i;
        return -1;
    }

    // ---- interaction -------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int under = SlotUnder(e.Location);
        if (under == hovered) return;

        hovered = under;
        Cursor = under >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hovered = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
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
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Panel);

        using var label = Theme.Face(9f);
        using var idle = new SolidBrush(Theme.Text);
        using var dim = new SolidBrush(Theme.Muted);
        using var hot = new SolidBrush(Theme.Raised);
        using var active = new SolidBrush(Color.FromArgb(48, 74, 158, 255));
        using var accent = new SolidBrush(Theme.Accent);

        var foreground = GetForegroundWindow();

        for (int i = 0; i < windows.Count; i++)
        {
            var slot = SlotAt(i);
            if (Vertical ? slot.Bottom > Height - ClockExtent : slot.Right > Width - 90) break;

            var w = windows[i];
            if (w.Handle == foreground) g.FillRectangle(active, slot);
            else if (i == hovered) g.FillRectangle(hot, slot);

            // A thin accent marks the window you are actually looking at.
            if (w.Handle == foreground)
                g.FillRectangle(accent, Vertical ? new Rectangle(slot.X, slot.Y, 3, slot.Height)
                                                 : new Rectangle(slot.X, slot.Bottom - 3, slot.Width, 3));

            if (icons.TryGetValue(w.Handle, out var icon))
                g.DrawImage(icon, slot.X + 9, slot.Y + (slot.Height - 16) / 2, 16, 16);

            var text = new Rectangle(slot.X + 31, slot.Y, slot.Width - 36, slot.Height);
            using var format = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            g.DrawString(w.Title, label, w.Minimised ? dim : idle, text, format);
        }

        PaintClock(g);
    }

    private void PaintClock(Graphics g)
    {
        using var time = Theme.Face(11f, FontStyle.Bold);
        using var date = Theme.Face(8f);
        using var brush = new SolidBrush(Theme.Text);
        using var faint = new SolidBrush(Theme.Muted);
        using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        var now = DateTime.Now;
        var area = Vertical
            ? new Rectangle(0, Height - ClockExtent, Width, ClockExtent)
            : new Rectangle(Width - 88, 0, 84, Height);

        using var divider = new Pen(Theme.Divider);
        if (Vertical) g.DrawLine(divider, 6, area.Top, Width - 6, area.Top);
        else g.DrawLine(divider, area.Left, 6, area.Left, Height - 6);

        g.DrawString(now.ToString("HH:mm"), time, brush,
                     new Rectangle(area.X, area.Y + 4, area.Width, 18), centred);
        g.DrawString(now.ToString("ddd d MMM"), date, faint,
                     new Rectangle(area.X, area.Y + 22, area.Width, 14), centred);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refresh.Dispose();
            appBar.Dispose();          // must happen, or the desktop stays short of the strip
            foreach (var icon in icons.Values) icon.Dispose();
            icons.Clear();
        }
        base.Dispose(disposing);
    }
}
