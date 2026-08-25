using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using static ScweenSpit.Native;
using Timer = System.Windows.Forms.Timer;

namespace ScweenSpit;

/// <summary>
/// The live previews a taskbar button shows on hover. One tile per window in the group, each a
/// desktop-composited view of the real window rather than a screenshot, so it keeps updating while
/// you look at it.
///
/// A window title is a poor way to tell six Chrome windows apart, which is exactly the case grouping
/// creates. This is the answer to the question grouping asks.
/// </summary>
public sealed class TaskbarPreview : Form
{
    private const int TileWidth = 208;
    private const int TileHeight = 124;
    private const int TitleHeight = 20;
    private const int Pad = 8;

    /// <summary>Beyond this the strip is wider than it is useful; the button still cycles them all.</summary>
    private const int MaxTiles = 8;

    private sealed record Tile(TaskWindow Window, Rectangle Bounds, IntPtr Thumb, Bitmap? Icon);

    private readonly List<Tile> tiles = [];
    private readonly Timer leaveWatch = new() { Interval = 150 };

    /// <summary>The button this is showing for, so the pointer moving between the two is not a leave.</summary>
    private Rectangle anchor;
    private int hovered = -1;

    /// <summary>Raised when a tile is clicked, with the window it belongs to.</summary>
    public event Action<TaskWindow>? Chosen;

    public TaskbarPreview()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Panel;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;

        leaveWatch.Tick += (_, _) =>
        {
            // Polled rather than driven by mouse-leave: this window never takes focus, and the
            // pointer crossing the gap between the bar and the preview would otherwise close it.
            var p = Cursor.Position;
            if (!Bounds.Contains(p) && !anchor.Contains(p)) Dismiss();
        };
    }

    /// <summary>Never takes the foreground: a preview that steals focus would minimise what it shows.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// Shows previews of <paramref name="windows"/> beside <paramref name="button"/>, on the side of
    /// the bar the desktop is.
    /// </summary>
    public void Open(IReadOnlyList<TaskWindow> windows, Rectangle button, Rectangle bar,
                     BarEdge edge, RECT monitor, int gap)
    {
        Clear();
        anchor = button;

        int room = Math.Max(1, (monitor.Width - 2 * Pad) / (TileWidth + Pad));
        int count = Math.Min(Math.Min(windows.Count, MaxTiles), room);
        if (count == 0) { Dismiss(); return; }

        int width = count * TileWidth + (count + 1) * Pad;
        int height = TileHeight + TitleHeight + 2 * Pad;

        var at = BarGeometry.Beside(new Size(width, height), button, bar, edge, monitor, gap);

        // Shown through WinForms first, or it never learns the window is up: Visible would stay
        // false and Hide would have nothing to do. ShowWithoutActivation keeps the focus where it is.
        if (!Visible) Show();

        // Then placed natively, before the thumbnails are registered: they are positioned in client
        // coordinates, so the window has to be its final size first.
        SetWindowPos(Handle, HWND_TOPMOST, at.X, at.Y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        RoundCorners();

        for (int i = 0; i < count; i++)
        {
            var window = windows[i];
            var box = new Rectangle(Pad + i * (TileWidth + Pad), Pad, TileWidth, TileHeight);
            tiles.Add(new Tile(window, box, Register(window, box), IconOf(window)));
        }

        hovered = -1;
        leaveWatch.Start();
        Invalidate();
    }

    /// <summary>
    /// A composited view of the window, fitted to its tile. Minimised windows have nothing to
    /// compose, so they get their icon instead — a blank rectangle would read as a broken preview.
    /// </summary>
    private IntPtr Register(TaskWindow window, Rectangle box)
    {
        if (window.Minimised) return IntPtr.Zero;
        if (DwmRegisterThumbnail(Handle, window.Handle, out var thumb) != 0 || thumb == IntPtr.Zero)
            return IntPtr.Zero;

        var into = box;
        if (DwmQueryThumbnailSourceSize(thumb, out var size) == 0 && size.Width > 0 && size.Height > 0)
        {
            // Letterboxed rather than stretched: a 32:9 window squashed into a 5:3 tile is not a
            // preview of anything.
            double scale = Math.Min((double)box.Width / size.Width, (double)box.Height / size.Height);
            int w = Math.Max(1, (int)(size.Width * scale)), h = Math.Max(1, (int)(size.Height * scale));
            into = new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
        }

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT { Left = into.Left, Top = into.Top, Right = into.Right, Bottom = into.Bottom },
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = false,
        };
        DwmUpdateThumbnailProperties(thumb, ref props);
        return thumb;
    }

    private static Bitmap? IconOf(TaskWindow window)
    {
        try { return WindowList.IconFor(window.Handle); }
        catch { return null; }
    }

    public void Dismiss()
    {
        leaveWatch.Stop();
        Clear();
        if (Visible) Hide();
    }

    private void Clear()
    {
        foreach (var tile in tiles)
        {
            if (tile.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(tile.Thumb);
            tile.Icon?.Dispose();
        }
        tiles.Clear();
    }

    private void RoundCorners()
    {
        var region = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 12, 12);
        if (region != IntPtr.Zero) SetWindowRgn(Handle, region, true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int under = tiles.FindIndex(t => Frame(t).Contains(e.Location));
        if (under == hovered) return;

        hovered = under;
        Cursor = under >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int under = tiles.FindIndex(t => Frame(t).Contains(e.Location));
        if (under < 0) return;

        var window = tiles[under].Window;
        if (e.Button == MouseButtons.Middle) { WindowList.Close(window.Handle); Dismiss(); return; }
        if (e.Button != MouseButtons.Left) return;

        Dismiss();
        Chosen?.Invoke(window);
    }

    /// <summary>A tile's whole area, thumbnail and title together.</summary>
    private static Rectangle Frame(Tile tile) =>
        new(tile.Bounds.X, tile.Bounds.Y, tile.Bounds.Width, tile.Bounds.Height + TitleHeight);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Panel);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var border = new Pen(Theme.Divider);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        using var hot = new SolidBrush(Theme.Raised);
        using var text = new SolidBrush(Theme.Text);
        using var muted = new SolidBrush(Theme.Muted);
        using var font = Theme.Face(8.5f);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (i == hovered) g.FillRectangle(hot, Frame(tile));

            // The thumbnail is composited over this window by the desktop manager, so nothing is
            // drawn where one is registered — only where there is none.
            if (tile.Thumb == IntPtr.Zero && tile.Icon is { } icon)
            {
                int size = Math.Min(48, Math.Min(tile.Bounds.Width, tile.Bounds.Height) / 2);
                g.DrawImage(icon, tile.Bounds.X + (tile.Bounds.Width - size) / 2,
                                  tile.Bounds.Y + (tile.Bounds.Height - size) / 2, size, size);
            }

            var caption = new Rectangle(tile.Bounds.X, tile.Bounds.Bottom, tile.Bounds.Width, TitleHeight);
            var title = tile.Window.Title is { Length: > 0 } t ? t : tile.Window.Process;
            g.DrawString(title, font, i == hovered ? text : muted, caption, format);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Clear(); leaveWatch.Dispose(); }
        base.Dispose(disposing);
    }
}
