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

    /// <summary>Raised when the user finishes dragging an outer edge: (device, new margins).</summary>
    public event Action<string, Margins>? MarginsEdited;

    /// <summary>A divider drag has started on this display.</summary>
    public event Action<string>? PreviewBegan;

    /// <summary>Where the dividers are mid-drag, often enough to follow but not every pixel.</summary>
    public event Action<string, List<FracRect>>? Previewing;

    /// <summary>The drag is over, committed or not.</summary>
    public event Action<string>? PreviewEnded;

    /// <summary>Raised when a visible overlay is taken down, so callers can restore their own UI.</summary>
    public event Action? Closed;

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

            var form = new OverlayForm(geo, zones.EffectiveWork(geo), rects,
                                       zones.Config.ZonesFor(geo.Device),
                                       zones.Config.LayoutFor(geo.Device).Margins.Copy(),
                                       zones.Config.Padding, mode);
            form.Committed += (device, edited) => ZonesEdited?.Invoke(device, edited);
            form.MarginsCommitted += (device, m) => MarginsEdited?.Invoke(device, m);
            form.PreviewBegan += device => PreviewBegan?.Invoke(device);
            form.Previewing += (device, edited) => Previewing?.Invoke(device, edited);
            form.PreviewEnded += device => PreviewEnded?.Invoke(device);
            form.Dismissed += Hide;
            forms.Add(form);

            // Give the window its rectangle BEFORE the handle exists, so it is created on the
            // target monitor. Created at the default position it would be born on the primary
            // display and then cross a DPI boundary, which makes WinForms rescale it behind us.
            form.Bounds = new Rectangle(geo.Bounds.Left, geo.Bounds.Top, geo.Bounds.Width, geo.Bounds.Height);
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
        bool wasVisible = forms.Count > 0;

        foreach (var f in forms) { f.Close(); f.Dispose(); }
        forms.Clear();

        if (wasVisible) Closed?.Invoke();
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

        private enum Grip { None, Divider, MarginLeft, MarginRight, MarginTop, MarginBottom }

        private readonly MonitorGeometry geo;
        private readonly List<Zone> pixels;
        private readonly List<FracRect> fractions;
        private readonly Margins margins;
        private readonly int padding;
        private readonly OverlayMode mode;

        private RECT? highlight;
        private double? draggingEdge;
        private bool draggingVertical;
        private Grip draggingGrip = Grip.None;
        private double hoverEdge = double.NaN;
        private bool hoverVertical;
        private Grip hoverGrip = Grip.None;
        private bool zonesDirty, marginsDirty;

        public string Device => geo.Device;
        public event Action<string, List<FracRect>>? Committed;
        public event Action<string, Margins>? MarginsCommitted;
        public event Action<string>? PreviewBegan;
        public event Action<string, List<FracRect>>? Previewing;
        public event Action<string>? PreviewEnded;

        /// <summary>
        /// Twenty-five a second. Every one of these moves real windows, and a mouse reports far
        /// faster than any of them can be redrawn — so the extra frames buy nothing and cost the
        /// smoothness they were meant to add.
        /// </summary>
        private const int PreviewMs = 40;
        private long lastPreview;
        public event Action? Dismissed;

        public OverlayForm(MonitorGeometry geo, RECT inner, List<Zone> pixels,
                           List<FracRect> fractions, Margins margins, int padding, OverlayMode mode)
        {
            this.geo = geo;
            this.pixels = pixels;
            this.fractions = ZoneEdges.Clone(fractions);
            // Fit once, here, against the same rule ZoneManager uses. Holding raw values would let
            // the editor show and commit margins the zone math then silently trims.
            this.margins = margins.Fitted(geo.Work.Width, geo.Work.Height);
            _ = inner;
            this.padding = padding;
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
            PlaceNatively();
        }

        /// <summary>WinForms would rescale Bounds by this form's DPI; place it in raw pixels.</summary>
        private void PlaceNatively() =>
            SetWindowPos(Handle, HWND_TOPMOST, geo.Bounds.Left, geo.Bounds.Top, geo.Bounds.Width, geo.Bounds.Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            // Our rectangle is already in physical pixels for this exact monitor. Letting WinForms
            // scale it would move and resize the overlay out from under the zone geometry.
            e.Cancel = true;
            base.OnDpiChanged(e);
            PlaceNatively();
        }

        public void SetHighlight(RECT? target)
        {
            if (Nullable.Equals(highlight, target)) return;
            highlight = target;
            Invalidate();
        }

        // ---- geometry helpers (fraction <-> local pixel) --------------------

        /// <summary>The work area in form-local coordinates. The form now spans the whole monitor,
        /// so this is offset by however much the taskbar takes.</summary>
        private Rectangle WorkLocal => new(
            geo.Work.Left - geo.Bounds.Left, geo.Work.Top - geo.Bounds.Top, geo.Work.Width, geo.Work.Height);

        /// <summary>The laid-out area in form-local coordinates, live as the margins are dragged.</summary>
        private Rectangle Inner()
        {
            var work = WorkLocal;
            var fit = margins.Fitted(work.Width, work.Height);
            return new Rectangle(work.X + fit.Left, work.Y + fit.Top,
                                 Math.Max(1, work.Width - fit.Left - fit.Right),
                                 Math.Max(1, work.Height - fit.Top - fit.Bottom));
        }

        private int XOf(double frac) { var i = Inner(); return i.X + (int)Math.Round(frac * i.Width); }
        private int YOf(double frac) { var i = Inner(); return i.Y + (int)Math.Round(frac * i.Height); }
        private double FracX(int x) { var i = Inner(); return Math.Clamp((double)(x - i.X) / Math.Max(1, i.Width), 0, 1); }
        private double FracY(int y) { var i = Inner(); return Math.Clamp((double)(y - i.Y) / Math.Max(1, i.Height), 0, 1); }

        private Rectangle LocalRect(FracRect f)
        {
            var area = Inner();
            int l = area.X + (int)Math.Round(f.L * area.Width);
            int t = area.Y + (int)Math.Round(f.T * area.Height);
            int r = area.X + (int)Math.Round(f.R * area.Width);
            int b = area.Y + (int)Math.Round(f.B * area.Height);

            // Same rule as ZoneManager: grow over the taskbar only on sides that already reach the
            // edge, so the preview matches what the window will actually get.
            if (f.CoverTaskbar)
            {
                const double edge = 0.001;
                if (f.L <= edge) l = 0;
                if (f.T <= edge) t = 0;
                if (f.R >= 1 - edge) r = Width;
                if (f.B >= 1 - edge) b = Height;
            }

            return new Rectangle(l, t, Math.Max(1, r - l), Math.Max(1, b - t));
        }

        // ---- edit interaction ----------------------------------------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (mode != OverlayMode.Edit) return;

            if (draggingGrip == Grip.Divider && draggingEdge is { } edge)
            {
                double want = draggingVertical ? FracX(e.X) : FracY(e.Y);
                var (min, max) = ZoneEdges.Limits(fractions, edge, draggingVertical);
                double clamped = Math.Clamp(want, min, max);

                if (!ZoneEdges.Near(clamped, edge))
                {
                    ZoneEdges.Move(fractions, edge, clamped, draggingVertical);
                    draggingEdge = clamped;
                    zonesDirty = true;
                    Invalidate();

                    long now = Environment.TickCount64;
                    if (now - lastPreview >= PreviewMs)
                    {
                        lastPreview = now;
                        Previewing?.Invoke(geo.Device, ZoneEdges.Clone(fractions));
                    }
                }
                return;
            }

            if (draggingGrip != Grip.None)
            {
                DragMargin(draggingGrip, e.X, e.Y);
                marginsDirty = true;
                Invalidate();
                return;
            }

            var (grip, near, vertical) = GripUnder(e.X, e.Y);

            // NaN never compares equal to itself, so "no edge under the cursor" has to be tested
            // explicitly - otherwise every mouse move repaints the whole overlay.
            bool sameEdge = double.IsNaN(near)
                ? double.IsNaN(hoverEdge)
                : vertical == hoverVertical && ZoneEdges.Near(near, hoverEdge);
            if (grip != hoverGrip || !sameEdge)
            {
                hoverGrip = grip;
                hoverEdge = near;
                hoverVertical = vertical;
                Cursor = grip switch
                {
                    Grip.None => Cursors.Default,
                    Grip.MarginLeft or Grip.MarginRight => Cursors.SizeWE,
                    Grip.MarginTop or Grip.MarginBottom => Cursors.SizeNS,
                    _ => vertical ? Cursors.SizeWE : Cursors.SizeNS,
                };
                Invalidate();
            }
        }

        private void DragMargin(Grip grip, int x, int y)
        {
            var work = WorkLocal;
            switch (grip)
            {
                case Grip.MarginLeft:
                    margins.Left = Math.Clamp(x - work.Left, 0, Math.Max(0, work.Width - margins.Right - Margins.MinUsable)); break;
                case Grip.MarginRight:
                    margins.Right = Math.Clamp(work.Right - x, 0, Math.Max(0, work.Width - margins.Left - Margins.MinUsable)); break;
                case Grip.MarginTop:
                    margins.Top = Math.Clamp(y - work.Top, 0, Math.Max(0, work.Height - margins.Bottom - Margins.MinUsable)); break;
                case Grip.MarginBottom:
                    margins.Bottom = Math.Clamp(work.Bottom - y, 0, Math.Max(0, work.Height - margins.Top - Margins.MinUsable)); break;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (mode != OverlayMode.Edit) return;

            var (grip, near, vertical) = GripUnder(e.X, e.Y);
            if (grip == Grip.None) { Dismissed?.Invoke(); return; }   // click away to finish

            draggingGrip = grip;
            draggingEdge = double.IsNaN(near) ? null : near;
            draggingVertical = vertical;

            // Which windows are filling which zone has to be settled before the first pixel of the
            // drag, while the answer is still the one the user can see.
            if (grip == Grip.Divider) PreviewBegan?.Invoke(geo.Device);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (mode != OverlayMode.Edit || draggingGrip == Grip.None) return;

            draggingGrip = Grip.None;
            draggingEdge = null;

            if (zonesDirty) { zonesDirty = false; Committed?.Invoke(geo.Device, ZoneEdges.Clone(fractions)); }
            if (marginsDirty) { marginsDirty = false; MarginsCommitted?.Invoke(geo.Device, margins.Copy()); }

            // After the commit, so the windows that were following end up against the layout that
            // was actually saved rather than against the last frame of the drag.
            PreviewEnded?.Invoke(geo.Device);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Escape or Keys.Enter) Dismissed?.Invoke();
        }

        /// <summary>Nearest draggable handle to a point. Outer margins win over inner dividers.</summary>
        private (Grip Grip, double Edge, bool Vertical) GripUnder(int x, int y)
        {
            var i = Inner();
            if (Math.Abs(x - i.Left) <= GrabPixels) return (Grip.MarginLeft, double.NaN, true);
            if (Math.Abs(x - i.Right) <= GrabPixels) return (Grip.MarginRight, double.NaN, true);
            if (Math.Abs(y - i.Top) <= GrabPixels) return (Grip.MarginTop, double.NaN, false);
            if (Math.Abs(y - i.Bottom) <= GrabPixels) return (Grip.MarginBottom, double.NaN, false);

            foreach (var v in ZoneEdges.Vertical(fractions))
                if (Math.Abs(XOf(v) - x) <= GrabPixels) return (Grip.Divider, v, true);

            foreach (var h in ZoneEdges.Horizontal(fractions))
                if (Math.Abs(YOf(h) - y) <= GrabPixels) return (Grip.Divider, h, false);

            return (Grip.None, double.NaN, false);
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

            PaintReserved(g, caption, centred);

            // In edit mode we draw the live fractions; otherwise the already-computed pixel zones.
            var boxes = mode == OverlayMode.Edit
                ? fractions.Select(LocalRect).Select(PadPreview).ToList()
                : pixels.Select(z => new Rectangle(z.Rect.Left - geo.Bounds.Left, z.Rect.Top - geo.Bounds.Top,
                                                  z.Rect.Width, z.Rect.Height)).ToList();

            for (int i = 0; i < boxes.Count; i++)
            {
                var r = Rectangle.Inflate(boxes[i], -4, -4);
                if (r.Width <= 0 || r.Height <= 0) continue;

                g.FillRectangle(fill, r);
                g.DrawRectangle(edge, r);
                g.DrawString($"{i + 1}", index, Brushes.White, r, centred);

                bool covers = mode == OverlayMode.Edit
                    ? i < fractions.Count && fractions[i].CoverTaskbar
                    : i < pixels.Count && pixels[i].CoverTaskbar;

                var sub = new Rectangle(r.X, r.Y + 32, r.Width, r.Height);
                g.DrawString($"{boxes[i].Width} × {boxes[i].Height}{(covers ? "   ·  over taskbar" : "")}",
                             caption, Brushes.White, sub, centred);

                if (covers)
                {
                    using var accent = new Pen(Color.FromArgb(255, 190, 110), 3f) { DashStyle = DashStyle.Dash };
                    g.DrawRectangle(accent, Rectangle.Inflate(r, -3, -3));
                }
            }

            if (highlight is { } h)
            {
                var r = new Rectangle(h.Left - geo.Bounds.Left, h.Top - geo.Bounds.Top, h.Width, h.Height);
                g.FillRectangle(hot, Rectangle.Inflate(r, -4, -4));
                using var thick = new Pen(Color.White, 4f);
                g.DrawRectangle(thick, Rectangle.Inflate(r, -4, -4));
            }

            if (mode == OverlayMode.Edit) PaintHandles(g);
            PaintHeader(g, centred);
        }

        /// <summary>Applies the configured gap, so the editor previews what you will actually get.</summary>
        private Rectangle PadPreview(Rectangle r) =>
            padding > 0 && r.Width > 2 * padding && r.Height > 2 * padding
                ? Rectangle.Inflate(r, -padding, -padding)
                : r;

        /// <summary>Hatches the space the margins keep clear, so reserved area is visibly reserved.</summary>
        private void PaintReserved(Graphics g, Font caption, StringFormat centred)
        {
            var i = Inner();
            if (i.X == 0 && i.Y == 0 && i.Width == Width && i.Height == Height) return;

            using var hatch = new HatchBrush(HatchStyle.WideDownwardDiagonal,
                                             Color.FromArgb(70, 76, 92), Color.FromArgb(30, 33, 41));
            foreach (var band in new[]
            {
                new Rectangle(0, 0, Width, i.Y),                                   // top
                new Rectangle(0, i.Bottom, Width, Height - i.Bottom),              // bottom
                new Rectangle(0, i.Y, i.X, i.Height),                              // left
                new Rectangle(i.Right, i.Y, Width - i.Right, i.Height),            // right
            })
            {
                if (band.Width > 0 && band.Height > 0) g.FillRectangle(hatch, band);
            }

            var label = $"reserved  {margins.Top}/{margins.Bottom}/{margins.Left}/{margins.Right}  (T/B/L/R)";
            g.DrawString(label, caption, Brushes.White, new Rectangle(0, Math.Max(0, i.Y - 22), Width, 20), centred);
        }

        private void PaintHandles(Graphics g)
        {
            using var grip = new Pen(Color.White, 3f) { DashStyle = DashStyle.Dot };
            using var live = new Pen(Color.FromArgb(120, 220, 255), 5f);
            using var outer = new Pen(Color.FromArgb(255, 190, 110), 3f) { DashStyle = DashStyle.Dash };
            using var outerLive = new Pen(Color.FromArgb(255, 210, 140), 5f);

            foreach (var v in ZoneEdges.Vertical(fractions))
            {
                bool active = hoverGrip == Grip.Divider && hoverVertical && ZoneEdges.Near(v, hoverEdge);
                g.DrawLine(active ? live : grip, XOf(v), Inner().Y, XOf(v), Inner().Bottom);
            }
            foreach (var h in ZoneEdges.Horizontal(fractions))
            {
                bool active = hoverGrip == Grip.Divider && !hoverVertical && ZoneEdges.Near(h, hoverEdge);
                g.DrawLine(active ? live : grip, Inner().X, YOf(h), Inner().Right, YOf(h));
            }

            var i = Inner();
            g.DrawLine(hoverGrip == Grip.MarginLeft ? outerLive : outer, i.Left, 0, i.Left, Height);
            g.DrawLine(hoverGrip == Grip.MarginRight ? outerLive : outer, i.Right, 0, i.Right, Height);
            g.DrawLine(hoverGrip == Grip.MarginTop ? outerLive : outer, 0, i.Top, Width, i.Top);
            g.DrawLine(hoverGrip == Grip.MarginBottom ? outerLive : outer, 0, i.Bottom, Width, i.Bottom);
        }

        private void PaintHeader(Graphics g, StringFormat centred)
        {
            string text = mode switch
            {
                OverlayMode.Edit => $"{Device.TrimStart('\\', '.')}  —  drag a divider to resize, an orange edge to reserve space · Esc when done",
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
