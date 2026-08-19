using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using static ScweenSpit.Native;

namespace ScweenSpit;

public enum OverlayMode
{
    /// <summary>Passive display: click-through, just shows the layout.</summary>
    Display,
    /// <summary>Shown during a window drag, highlighting the zone the window would land in.</summary>
    Drag,
    /// <summary>Interactive: the dividers between zones can be dragged to resize them.</summary>
    Edit,
}

/// <summary>
/// Draws the zone layout across every monitor. Click-through except in <see cref="OverlayMode.Edit"/>,
/// where the dividers become draggable handles.
/// </summary>
public sealed class ZoneOverlay : IDisposable
{
    private readonly List<OverlayForm> forms = [];
    private readonly System.Windows.Forms.Timer autoHide = new();

    /// <summary>Raised when the user finishes dragging a divider: (device, new fractional zones).</summary>
    public event Action<string, List<FracRect>>? ZonesEdited;

    public ZoneOverlay() => autoHide.Tick += (_, _) => { autoHide.Stop(); Hide(); };

    public bool Visible => forms.Count > 0;
    public OverlayMode Mode { get; private set; } = OverlayMode.Display;

    public void Toggle(ZoneManager zones)
    {
        if (Visible) Hide(); else Show(zones, OverlayMode.Display);
    }

    public void Flash(ZoneManager zones, int ms = 1200)
    {
        Show(zones, OverlayMode.Display);
        autoHide.Interval = ms;
        autoHide.Stop();
        autoHide.Start();
    }

    public void Show(ZoneManager zones, OverlayMode mode)
    {
        Hide();
        Mode = mode;

        foreach (var geo in ZoneManager.AllMonitors())
        {
            var rects = zones.ZonesFor(geo);
            if (rects.Count == 0) continue;

            var form = new OverlayForm(geo, rects, zones.Config.ZonesFor(geo.Device), mode);
            form.Committed += (device, edited) => ZonesEdited?.Invoke(device, edited);
            form.Dismissed += Hide;
            forms.Add(form);
            form.Show();
        }

        if (mode == OverlayMode.Edit && forms.Count > 0) forms[0].Activate();
        Log.Write($"overlay {mode} across {forms.Count} monitor(s)");
    }

    /// <summary>During a drag, paint the rectangle the window would snap to (null clears it).</summary>
    public void Highlight(string device, RECT? target)
    {
        foreach (var f in forms) f.SetHighlight(f.Device == device ? target : null);
    }

    public void Hide()
    {
        autoHide.Stop();
        foreach (var f in forms) { f.Close(); f.Dispose(); }
        forms.Clear();
    }

    public void Dispose() { Hide(); autoHide.Dispose(); }

    // ------------------------------------------------------------------------

    private sealed class OverlayForm : Form
    {
        private const int GrabPixels = 8;

        private static readonly Color Dim = Color.FromArgb(18, 20, 26);
        private static readonly Color ZoneFill = Color.FromArgb(64, 110, 190);
        private static readonly Color ZoneEdge = Color.FromArgb(150, 190, 255);
        private static readonly Color HotFill = Color.FromArgb(90, 170, 255);

        private readonly MonitorGeometry geo;
        private readonly List<RECT> pixels;
        private readonly List<FracRect> fractions;
        private readonly OverlayMode mode;

        private RECT? highlight;
        private double? draggingEdge;
        private bool draggingVertical;
        private double hoverEdge = double.NaN;
        private bool hoverVertical;
        private bool dirty;

        public string Device => geo.Device;
        public event Action<string, List<FracRect>>? Committed;
        public event Action? Dismissed;

        public OverlayForm(MonitorGeometry geo, List<RECT> pixels, List<FracRect> fractions, OverlayMode mode)
        {
            this.geo = geo;
            this.pixels = pixels;
            this.fractions = ZoneEdges.Clone(fractions);
            this.mode = mode;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;   // zone rectangles are already physical pixels
            TopMost = true;
            BackColor = Dim;
            Opacity = mode == OverlayMode.Drag ? 0.32 : 0.5;
            DoubleBuffered = true;
            KeyPreview = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW;
                // Edit mode is the only one that wants the mouse and the keyboard.
                if (mode != OverlayMode.Edit) cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => mode != OverlayMode.Edit;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // WinForms would rescale Bounds by this form's DPI; place it natively instead.
            SetWindowPos(Handle, HWND_TOPMOST, geo.Work.Left, geo.Work.Top, geo.Work.Width, geo.Work.Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        public void SetHighlight(RECT? target)
        {
            if (Nullable.Equals(highlight, target)) return;
            highlight = target;
            Invalidate();
        }

        // ---- geometry helpers (fraction <-> local pixel) --------------------

        private int XOf(double frac) => (int)Math.Round(frac * geo.Work.Width);
        private int YOf(double frac) => (int)Math.Round(frac * geo.Work.Height);
        private double FracX(int x) => Math.Clamp((double)x / Math.Max(1, geo.Work.Width), 0, 1);
        private double FracY(int y) => Math.Clamp((double)y / Math.Max(1, geo.Work.Height), 0, 1);

        private Rectangle LocalRect(FracRect f) => new(
            XOf(f.L), YOf(f.T), Math.Max(1, XOf(f.R) - XOf(f.L)), Math.Max(1, YOf(f.B) - YOf(f.T)));

        // ---- edit interaction ----------------------------------------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (mode != OverlayMode.Edit) return;

            if (draggingEdge is { } edge)
            {
                double want = draggingVertical ? FracX(e.X) : FracY(e.Y);
                var (min, max) = ZoneEdges.Limits(fractions, edge, draggingVertical);
                double clamped = Math.Clamp(want, min, max);

                if (!ZoneEdges.Near(clamped, edge))
                {
                    ZoneEdges.Move(fractions, edge, clamped, draggingVertical);
                    draggingEdge = clamped;
                    dirty = true;
                    Invalidate();
                }
                return;
            }

            var (near, vertical) = EdgeUnder(e.X, e.Y);

            // NaN never compares equal to itself, so "no edge under the cursor" has to be tested
            // explicitly — otherwise every mouse move repaints the whole overlay.
            bool unchanged = (double.IsNaN(near) && double.IsNaN(hoverEdge)) || ZoneEdges.Near(near, hoverEdge);
            if (!unchanged)
            {
                hoverEdge = near;
                hoverVertical = vertical;
                Cursor = double.IsNaN(near) ? Cursors.Default : vertical ? Cursors.SizeWE : Cursors.SizeNS;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (mode != OverlayMode.Edit) return;

            var (near, vertical) = EdgeUnder(e.X, e.Y);
            if (double.IsNaN(near)) { Dismissed?.Invoke(); return; }   // click away to finish

            draggingEdge = near;
            draggingVertical = vertical;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (mode != OverlayMode.Edit || draggingEdge is null) return;

            draggingEdge = null;
            if (dirty)
            {
                dirty = false;
                Committed?.Invoke(geo.Device, ZoneEdges.Clone(fractions));
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Escape or Keys.Enter) Dismissed?.Invoke();
        }

        /// <summary>Nearest draggable divider to a point, or NaN when the point is not near one.</summary>
        private (double Edge, bool Vertical) EdgeUnder(int x, int y)
        {
            foreach (var v in ZoneEdges.Vertical(fractions))
                if (Math.Abs(XOf(v) - x) <= GrabPixels) return (v, true);

            foreach (var h in ZoneEdges.Horizontal(fractions))
                if (Math.Abs(YOf(h) - y) <= GrabPixels) return (h, false);

            return (double.NaN, false);
        }

        // ---- painting -------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Dim);

            using var fill = new SolidBrush(ZoneFill);
            using var hot = new SolidBrush(HotFill);
            using var edge = new Pen(ZoneEdge, 2f);
            using var index = new Font(FontFamily.GenericSansSerif, 40f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var caption = new Font(FontFamily.GenericSansSerif, 15f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            // In edit mode we draw the live fractions; otherwise the already-computed pixel zones.
            var boxes = mode == OverlayMode.Edit
                ? fractions.Select(LocalRect).ToList()
                : pixels.Select(z => new Rectangle(z.Left - geo.Work.Left, z.Top - geo.Work.Top, z.Width, z.Height)).ToList();

            for (int i = 0; i < boxes.Count; i++)
            {
                var r = Rectangle.Inflate(boxes[i], -4, -4);
                if (r.Width <= 0 || r.Height <= 0) continue;

                g.FillRectangle(fill, r);
                g.DrawRectangle(edge, r);
                g.DrawString($"{i + 1}", index, Brushes.White, r, centred);

                var sub = new Rectangle(r.X, r.Y + 32, r.Width, r.Height);
                g.DrawString($"{boxes[i].Width} × {boxes[i].Height}", caption, Brushes.White, sub, centred);
            }

            if (highlight is { } h)
            {
                var r = new Rectangle(h.Left - geo.Work.Left, h.Top - geo.Work.Top, h.Width, h.Height);
                g.FillRectangle(hot, Rectangle.Inflate(r, -4, -4));
                using var thick = new Pen(Color.White, 4f);
                g.DrawRectangle(thick, Rectangle.Inflate(r, -4, -4));
            }

            if (mode == OverlayMode.Edit) PaintHandles(g);
            PaintHeader(g, centred);
        }

        private void PaintHandles(Graphics g)
        {
            using var grip = new Pen(Color.White, 3f) { DashStyle = DashStyle.Dot };
            using var live = new Pen(Color.FromArgb(120, 220, 255), 5f);

            foreach (var v in ZoneEdges.Vertical(fractions))
            {
                bool active = !double.IsNaN(hoverEdge) && hoverVertical && ZoneEdges.Near(v, hoverEdge);
                g.DrawLine(active ? live : grip, XOf(v), 0, XOf(v), Height);
            }
            foreach (var h in ZoneEdges.Horizontal(fractions))
            {
                bool active = !double.IsNaN(hoverEdge) && !hoverVertical && ZoneEdges.Near(h, hoverEdge);
                g.DrawLine(active ? live : grip, 0, YOf(h), Width, YOf(h));
            }
        }

        private void PaintHeader(Graphics g, StringFormat centred)
        {
            string text = mode switch
            {
                OverlayMode.Edit => $"{Device.TrimStart('\\', '.')}  —  drag a divider to resize · Esc when done",
                OverlayMode.Drag => $"{Device.TrimStart('\\', '.')}  —  drop to snap",
                _ => $"{Device.TrimStart('\\', '.')}  —  {pixels.Count} zones",
            };

            using var font = new Font(FontFamily.GenericSansSerif, 17f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var plate = new SolidBrush(Color.FromArgb(10, 12, 18));
            var size = g.MeasureString(text, font);
            var box = new RectangleF((Width - size.Width) / 2 - 14, 10, size.Width + 28, size.Height + 10);

            g.FillRectangle(plate, box);
            g.DrawString(text, font, Brushes.White, box, centred);
        }
    }
}
