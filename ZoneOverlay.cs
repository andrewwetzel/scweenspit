using System.Drawing;
using System.Windows.Forms;
using static ScweenSpit.Native;

namespace ScweenSpit;

/// <summary>
/// Draws the current zone layout over every monitor: a dimmed screen with each zone outlined,
/// numbered and captioned with its pixel size. Click-through and never focusable, so it can be
/// left up while you work. Also doubles as the fastest sanity check that the geometry is right.
/// </summary>
public sealed class ZoneOverlay : IDisposable
{
    private readonly List<OverlayForm> forms = [];
    private readonly System.Windows.Forms.Timer autoHide = new();

    public ZoneOverlay() => autoHide.Tick += (_, _) => { autoHide.Stop(); Hide(); };

    public bool Visible => forms.Count > 0;

    public void Toggle(ZoneManager zones)
    {
        if (Visible) Hide(); else Show(zones);
    }

    /// <summary>Show briefly — used to confirm a layout change without leaving clutter behind.</summary>
    public void Flash(ZoneManager zones, int ms = 1200)
    {
        Show(zones);
        autoHide.Interval = ms;
        autoHide.Stop();
        autoHide.Start();
    }

    public void Show(ZoneManager zones)
    {
        Hide();
        foreach (var geo in ZoneManager.AllMonitors())
        {
            var rects = zones.ZonesFor(geo);
            if (rects.Count == 0) continue;

            var form = new OverlayForm(geo, rects);
            forms.Add(form);
            form.Show();
        }
        Log.Write($"overlay shown across {forms.Count} monitor(s)");
    }

    public void Hide()
    {
        autoHide.Stop();
        foreach (var f in forms) { f.Close(); f.Dispose(); }
        forms.Clear();
    }

    public void Dispose() { Hide(); autoHide.Dispose(); }

    private sealed class OverlayForm : Form
    {
        private readonly MonitorGeometry geo;
        private readonly List<RECT> zones;

        public OverlayForm(MonitorGeometry geo, List<RECT> zones)
        {
            this.geo = geo;
            this.zones = zones;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;   // we place it in physical pixels ourselves
            TopMost = true;
            BackColor = Color.Black;
            Opacity = 0.45;
            DoubleBuffered = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // WinForms would rescale Bounds by this form's DPI; the zone rectangles are already
            // physical pixels, so position the window natively and skip the scaling entirely.
            SetWindowPos(Handle, HWND_TOPMOST, geo.Work.Left, geo.Work.Top, geo.Work.Width, geo.Work.Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Black);

            using var fill = new SolidBrush(Color.FromArgb(70, 120, 200));
            using var edge = new Pen(Color.White, 3f);
            using var index = new Font(FontFamily.GenericSansSerif, 44f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var caption = new Font(FontFamily.GenericSansSerif, 16f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var centred = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                // 4px inset so neighbouring zones read as separate blocks rather than one field
                var r = new Rectangle(z.Left - geo.Work.Left + 4, z.Top - geo.Work.Top + 4,
                                      Math.Max(1, z.Width - 8), Math.Max(1, z.Height - 8));

                g.FillRectangle(fill, r);
                g.DrawRectangle(edge, r);

                var label = new Rectangle(r.X, r.Y, r.Width, r.Height);
                g.DrawString($"{i + 1}", index, Brushes.White, label, centred);

                var sub = new Rectangle(r.X, r.Y + 34, r.Width, r.Height);
                g.DrawString($"{z.Width} × {z.Height}", caption, Brushes.White, sub, centred);
            }

            var header = new Rectangle(0, 8, Width, 30);
            using var head = new Font(FontFamily.GenericSansSerif, 18f, FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString($"{geo.Device.TrimStart('\\', '.')}  —  {zones.Count} zones", head, Brushes.White, header, centred);
        }
    }
}
