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
        private enum EditActionKind { AddColumn, AddRow, Equalise, Done, SplitVertical, SplitHorizontal, RemoveZone }
        private readonly record struct EditButton(Rectangle Bounds, string Label, EditActionKind Kind, int ZoneIndex = -1, bool Primary = false);

        private readonly MonitorGeometry geo;
        private readonly List<Zone> pixels;
        private readonly List<FracRect> fractions;
        private readonly Margins margins;
        private readonly int padding;
        private readonly OverlayMode mode;

        private RECT? highlight;
        private Divider? draggingDivider;
        private Grip draggingGrip = Grip.None;
        private Divider? hoverDivider;
        private Grip hoverGrip = Grip.None;
        private EditButton? hoverButton;
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

        private List<Rectangle> CurrentBoxes() =>
            mode == OverlayMode.Edit
                ? fractions.Select(LocalRect).Select(PadPreview).ToList()
                : pixels.Select(z => new Rectangle(z.Rect.Left - geo.Bounds.Left, z.Rect.Top - geo.Bounds.Top,
                                                  z.Rect.Width, z.Rect.Height)).ToList();

        private List<EditButton> BuildEditButtons(List<Rectangle> boxes)
        {
            var buttons = new List<EditButton>();
            if (mode != OverlayMode.Edit) return buttons;

            // Top quick-action bar below the header plate.
            var topSpecs = new (string Label, EditActionKind Kind, int Width, bool Primary)[]
            {
                ("+ Vertical panel", EditActionKind.AddColumn, 134, true),
                ("+ Horizontal row", EditActionKind.AddRow,    134, false),
                ("Equalise",         EditActionKind.Equalise,   92, false),
                ("Done (Esc)",       EditActionKind.Done,       96, false),
            };
            int gap = 8;
            int totalTopW = topSpecs.Sum(s => s.Width) + gap * (topSpecs.Length - 1);
            int curX = (Width - totalTopW) / 2;
            int topY = 52;
            foreach (var spec in topSpecs)
            {
                buttons.Add(new EditButton(new Rectangle(curX, topY, spec.Width, 28), spec.Label, spec.Kind, -1, spec.Primary));
                curX += spec.Width + gap;
            }

            // Per-zone split / remove pills inside each zone.
            for (int i = 0; i < boxes.Count && i < fractions.Count; i++)
            {
                var r = Rectangle.Inflate(boxes[i], -4, -4);
                if (r.Width < 90 || r.Height < 95) continue;

                bool canV = ZoneEdges.CanSplitZone(fractions, i, vertical: true);
                bool canH = ZoneEdges.CanSplitZone(fractions, i, vertical: false);
                bool canRem = ZoneEdges.CanRemoveZone(fractions, i);

                bool compact = r.Width < 220;
                var zoneSpecs = new List<(string Label, EditActionKind Kind, int Width)>();
                if (canV) zoneSpecs.Add((compact ? "Split |" : "Split L/R", EditActionKind.SplitVertical, compact ? 54 : 74));
                if (canH) zoneSpecs.Add((compact ? "Split —" : "Split T/B", EditActionKind.SplitHorizontal, compact ? 54 : 74));
                if (canRem) zoneSpecs.Add((compact ? "×" : "× Remove", EditActionKind.RemoveZone, compact ? 28 : 74));

                if (zoneSpecs.Count == 0) continue;

                int zGap = 6;
                int rowW = zoneSpecs.Sum(s => s.Width) + zGap * (zoneSpecs.Count - 1);
                if (rowW > r.Width - 8) continue;

                int zx = r.X + (r.Width - rowW) / 2;
                int zy = r.Y + r.Height / 2 + 28;
                if (zy + 26 > r.Bottom - 6) zy = r.Bottom - 32;

                foreach (var zs in zoneSpecs)
                {
                    buttons.Add(new EditButton(new Rectangle(zx, zy, zs.Width, 26), zs.Label, zs.Kind, i));
                    zx += zs.Width + zGap;
                }
            }

            return buttons;
        }

        private EditButton? ButtonUnder(int x, int y)
        {
            foreach (var b in BuildEditButtons(CurrentBoxes()))
                if (b.Bounds.Contains(x, y)) return b;
            return null;
        }

        private void ApplyZonesEdit(List<FracRect> updated)
        {
            ZoneEdges.Canonicalise(updated);
            var sorted = updated.OrderBy(z => z.T).ThenBy(z => z.L).ToList();
            fractions.Clear();
            fractions.AddRange(sorted);

            PreviewBegan?.Invoke(geo.Device);
            Committed?.Invoke(geo.Device, ZoneEdges.Clone(fractions));
            PreviewEnded?.Invoke(geo.Device);
            Invalidate();
        }

        // ---- edit interaction ----------------------------------------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (mode != OverlayMode.Edit) return;

            if (draggingGrip == Grip.Divider && draggingDivider is { } div)
            {
                double want = div.Vertical ? FracX(e.X) : FracY(e.Y);
                var (min, max) = ZoneEdges.Limits(fractions, div);
                double clamped = Math.Clamp(want, min, max);

                if (!ZoneEdges.Near(clamped, div.Position))
                {
                    ZoneEdges.Move(fractions, div, clamped);
                    draggingDivider = div with { Position = clamped };
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

            var btn = ButtonUnder(e.X, e.Y);
            if (btn is not null)
            {
                if (!Nullable.Equals(hoverButton, btn) || hoverGrip != Grip.None)
                {
                    hoverButton = btn;
                    hoverGrip = Grip.None;
                    hoverDivider = null;
                    Cursor = Cursors.Hand;
                    Invalidate();
                }
                return;
            }

            bool shift = (ModifierKeys & Keys.Shift) != 0;
            var (grip, nearDiv) = GripUnder(e.X, e.Y, shift);

            bool sameDiv = nearDiv is null
                ? hoverDivider is null
                : hoverDivider is { } h && nearDiv.Value.SameAs(h);

            if (hoverButton is not null || grip != hoverGrip || !sameDiv)
            {
                hoverButton = null;
                hoverGrip = grip;
                hoverDivider = nearDiv;
                Cursor = grip switch
                {
                    Grip.None => Cursors.Default,
                    Grip.MarginLeft or Grip.MarginRight => Cursors.SizeWE,
                    Grip.MarginTop or Grip.MarginBottom => Cursors.SizeNS,
                    _ => nearDiv is { Vertical: true } ? Cursors.SizeWE : Cursors.SizeNS,
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

            if (e.Button == MouseButtons.Left && ButtonUnder(e.X, e.Y) is { } btn)
            {
                switch (btn.Kind)
                {
                    case EditActionKind.AddColumn:
                        ApplyZonesEdit(ZoneEdges.AddColumn(fractions));
                        break;
                    case EditActionKind.AddRow:
                        ApplyZonesEdit(ZoneEdges.AddRow(fractions));
                        break;
                    case EditActionKind.Equalise:
                        ApplyZonesEdit(ZoneEdges.Equalise(fractions));
                        break;
                    case EditActionKind.Done:
                        Dismissed?.Invoke();
                        break;
                    case EditActionKind.SplitVertical:
                        ApplyZonesEdit(ZoneEdges.SplitZone(fractions, btn.ZoneIndex, vertical: true));
                        break;
                    case EditActionKind.SplitHorizontal:
                        ApplyZonesEdit(ZoneEdges.SplitZone(fractions, btn.ZoneIndex, vertical: false));
                        break;
                    case EditActionKind.RemoveZone:
                        ApplyZonesEdit(ZoneEdges.RemoveZone(fractions, btn.ZoneIndex));
                        break;
                }
                return;
            }

            bool shift = (ModifierKeys & Keys.Shift) != 0;
            var (grip, nearDiv) = GripUnder(e.X, e.Y, shift);

            // Right-clicking a divider collapses it by merging the zones across it.
            if (e.Button == MouseButtons.Right)
            {
                if (grip == Grip.Divider && nearDiv is { } toRemove)
                {
                    var copy = ZoneEdges.Clone(fractions);
                    if (ZoneEdges.RemoveDivider(copy, toRemove))
                        ApplyZonesEdit(copy);
                }
                return;
            }

            if (grip == Grip.None) { Dismissed?.Invoke(); return; }   // click away to finish

            draggingGrip = grip;
            draggingDivider = nearDiv;

            // Which windows are filling which zone has to be settled before the first pixel of the
            // drag, while the answer is still the one the user can see.
            if (grip == Grip.Divider) PreviewBegan?.Invoke(geo.Device);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (mode != OverlayMode.Edit || draggingGrip == Grip.None) return;

            draggingGrip = Grip.None;
            draggingDivider = null;

            if (zonesDirty)
            {
                zonesDirty = false;
                ZoneEdges.Canonicalise(fractions);
                var sorted = fractions.OrderBy(z => z.T).ThenBy(z => z.L).ToList();
                fractions.Clear();
                fractions.AddRange(sorted);
                Committed?.Invoke(geo.Device, ZoneEdges.Clone(fractions));
            }
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
        private (Grip Grip, Divider? Divider) GripUnder(int x, int y, bool shiftHeld = false)
        {
            var i = Inner();
            if (Math.Abs(x - i.Left) <= GrabPixels) return (Grip.MarginLeft, null);
            if (Math.Abs(x - i.Right) <= GrabPixels) return (Grip.MarginRight, null);
            if (Math.Abs(y - i.Top) <= GrabPixels) return (Grip.MarginTop, null);
            if (Math.Abs(y - i.Bottom) <= GrabPixels) return (Grip.MarginBottom, null);

            foreach (var d in ZoneEdges.Dividers(fractions, mergeTouching: !shiftHeld))
            {
                if (d.Vertical)
                {
                    int px = XOf(d.Position), y0 = YOf(d.Start), y1 = YOf(d.End);
                    if (Math.Abs(px - x) <= GrabPixels && y >= y0 - GrabPixels && y <= y1 + GrabPixels)
                        return (Grip.Divider, d);
                }
                else
                {
                    int py = YOf(d.Position), x0 = XOf(d.Start), x1 = XOf(d.End);
                    if (Math.Abs(py - y) <= GrabPixels && x >= x0 - GrabPixels && x <= x1 + GrabPixels)
                        return (Grip.Divider, d);
                }
            }

            return (Grip.None, null);
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
            var boxes = CurrentBoxes();

            for (int i = 0; i < boxes.Count; i++)
            {
                var r = Rectangle.Inflate(boxes[i], -4, -4);
                if (r.Width <= 0 || r.Height <= 0) continue;

                g.FillRectangle(fill, r);
                g.DrawRectangle(edge, r);

                bool covers = mode == OverlayMode.Edit
                    ? i < fractions.Count && fractions[i].CoverTaskbar
                    : i < pixels.Count && pixels[i].CoverTaskbar;

                int shiftY = mode == OverlayMode.Edit && r.Height >= 110 ? -16 : 0;
                var idxRect = new Rectangle(r.X, r.Y + shiftY, r.Width, r.Height);
                g.DrawString($"{i + 1}", index, Brushes.White, idxRect, centred);

                var sub = new Rectangle(r.X, r.Y + shiftY + 32, r.Width, r.Height);
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

            if (mode == OverlayMode.Edit)
            {
                PaintHandles(g);
                PaintEditButtons(g, BuildEditButtons(boxes), centred);
            }
            PaintHeader(g, centred);
        }

        private void PaintEditButtons(Graphics g, List<EditButton> buttons, StringFormat centred)
        {
            using var btnFont = new Font(FontFamily.GenericSansSerif, 13f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var normalBg = new SolidBrush(Color.FromArgb(230, 26, 30, 38));
            using var primaryBg = new SolidBrush(Color.FromArgb(235, 58, 130, 220));
            using var hoverBg = new SolidBrush(Color.FromArgb(245, 74, 158, 255));
            using var removeHoverBg = new SolidBrush(Color.FromArgb(240, 200, 75, 75));
            using var borderPen = new Pen(Color.FromArgb(180, 150, 190, 255), 1.5f);

            foreach (var b in buttons)
            {
                bool hovered = Nullable.Equals(hoverButton, b);
                var brush = hovered
                    ? (b.Kind == EditActionKind.RemoveZone ? removeHoverBg : hoverBg)
                    : (b.Primary ? primaryBg : normalBg);

                g.FillRectangle(brush, b.Bounds);
                g.DrawRectangle(borderPen, b.Bounds);
                g.DrawString(b.Label, btnFont, Brushes.White, b.Bounds, centred);
            }
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

            var activeDiv = draggingDivider ?? hoverDivider;
            foreach (var d in ZoneEdges.Dividers(fractions, mergeTouching: true))
            {
                if (d.Vertical)
                    g.DrawLine(grip, XOf(d.Position), YOf(d.Start), XOf(d.Position), YOf(d.End));
                else
                    g.DrawLine(grip, XOf(d.Start), YOf(d.Position), XOf(d.End), YOf(d.Position));
            }

            if ((draggingGrip == Grip.Divider || hoverGrip == Grip.Divider) && activeDiv is { } ad)
            {
                if (ad.Vertical)
                    g.DrawLine(live, XOf(ad.Position), YOf(ad.Start), XOf(ad.Position), YOf(ad.End));
                else
                    g.DrawLine(live, XOf(ad.Start), YOf(ad.Position), XOf(ad.End), YOf(ad.Position));
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
                OverlayMode.Edit => $"{Device.TrimStart('\\', '.')}  —  drag dividers to resize (Shift-drag to detach, right-click to merge) · split or remove zones · Esc when done",
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
