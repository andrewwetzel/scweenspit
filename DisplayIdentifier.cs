using System.Drawing;
using System.Windows.Forms;
using static ScweenSpit.Native;

namespace ScweenSpit;

/// <summary>
/// Flashes a large label on each display, the way Windows' own "Identify" button does. Device names
/// like \\.\DISPLAY2 mean nothing to a person, and two identical screens are indistinguishable in a
/// settings list — so the settings window offers to show you which is which.
/// </summary>
public static class DisplayIdentifier
{
    public static void Flash(int milliseconds = 2500)
    {
        var forms = new List<Form>();

        int index = 1;
        foreach (var geo in ZoneManager.AllMonitors())
            forms.Add(Card(geo, index++));

        var timer = new System.Windows.Forms.Timer { Interval = milliseconds };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            foreach (var f in forms) { f.Close(); f.Dispose(); }
        };
        timer.Start();
    }

    private static Form Card(MonitorGeometry geo, int number)
    {
        var form = new IdentifyForm(geo, number)
        {
            Bounds = new Rectangle(geo.Bounds.Left, geo.Bounds.Top, geo.Bounds.Width, geo.Bounds.Height),
        };
        form.Show();
        return form;
    }

    private sealed class IdentifyForm : Form
    {
        private readonly MonitorGeometry geo;
        private readonly int number;

        public IdentifyForm(MonitorGeometry geo, int number)
        {
            this.geo = geo;
            this.number = number;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            BackColor = Theme.Window;
            Opacity = 0.82;
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
            SetWindowPos(Handle, HWND_TOPMOST, geo.Bounds.Left, geo.Bounds.Top, geo.Bounds.Width, geo.Bounds.Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Theme.Window);

            using var big = new Font(FontFamily.GenericSansSerif, Math.Max(96, Height / 5), FontStyle.Bold, GraphicsUnit.Pixel);
            using var small = new Font(FontFamily.GenericSansSerif, 26f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            g.DrawString(number.ToString(), big, Brushes.White,
                         new Rectangle(0, 0, Width, Height - 80), centred);
            g.DrawString($"{geo.Device.TrimStart('\\', '.')}   ·   {geo.Bounds.Width}×{geo.Bounds.Height}   ·   {geo.Describe()}",
                         small, new SolidBrush(Theme.Muted),
                         new Rectangle(0, Height - 120, Width, 60), centred);
        }
    }
}
