using System.Drawing;
using System.Windows.Forms;

namespace ScweenSpit;

/// <summary>One place for the dark palette and the control styling, so the window stays coherent.</summary>
internal static class Theme
{
    public static readonly Color Window  = Color.FromArgb(22, 24, 29);
    public static readonly Color Panel   = Color.FromArgb(30, 33, 40);
    public static readonly Color Raised  = Color.FromArgb(38, 42, 51);
    public static readonly Color Accent  = Color.FromArgb(74, 158, 255);
    public static readonly Color Text    = Color.FromArgb(230, 232, 236);
    public static readonly Color Muted   = Color.FromArgb(150, 157, 170);
    public static readonly Color Divider = Color.FromArgb(48, 52, 62);

    public static Font Face(float size = 9.5f, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", size, style);

    public static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Text, Font = Face(15f, FontStyle.Bold),
        Margin = new Padding(0, 0, 0, 2),
    };

    public static Label Caption(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Muted, Font = Face(9f),
        MaximumSize = new Size(430, 0), Margin = new Padding(0, 0, 0, 14),
    };

    public static CheckBox Toggle(string text, bool value, string? hint = null) => new()
    {
        Text = text, Checked = value, AutoSize = true, ForeColor = Text, Font = Face(),
        FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 4, 0, hint is null ? 8 : 0),
        Cursor = Cursors.Hand,
    };

    public static Button Action(string text, bool primary = false)
    {
        var b = new Button
        {
            Text = text, AutoSize = false, Size = new Size(170, 32), Font = Face(),
            FlatStyle = FlatStyle.Flat, ForeColor = primary ? Color.White : Text,
            BackColor = primary ? Accent : Raised, Cursor = Cursors.Hand,
            Margin = new Padding(0, 4, 8, 4), TextAlign = ContentAlignment.MiddleCenter,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Accent, 0.15f) : Divider;
        return b;
    }

    public static ComboBox Choice(string[] items, string selected)
    {
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            BackColor = Raised, ForeColor = Text, Font = Face(), Width = 150,
            Margin = new Padding(0, 2, 0, 10),
        };
        c.Items.AddRange(items);
        c.SelectedItem = items.FirstOrDefault(i => i.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        return c;
    }

    public static NumericUpDown Number(int value, int min, int max, int step = 1) => new()
    {
        Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Increment = step,
        BackColor = Raised, ForeColor = Text, Font = Face(), Width = 100,
        BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 2, 0, 10),
    };

    private static Icon? appIcon;

    /// <summary>
    /// The 70/30 glyph, drawn at runtime so there is no .ico to ship. Shared by the tray and the
    /// settings window: a window with no icon does not look like an application, and this one is
    /// meant to sit in the taskbar and Alt+Tab like any other.
    /// </summary>
    public static Icon AppIcon()
    {
        if (appIcon is not null) return appIcon;

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.FillRectangle(Brushes.DodgerBlue, 2, 6, 19, 20);
            g.FillRectangle(Brushes.Gainsboro, 23, 6, 7, 20);
        }

        return appIcon = Icon.FromHandle(bmp.GetHicon());   // process-lifetime handle
    }
    /// <summary>
    /// Asks DWM for a dark title bar so the frame matches the content. The attribute number changed
    /// during Windows 10: builds before 18985 use 19, later ones 20. Getting it wrong leaves a white
    /// caption bar on a near-black window, silently.
    /// </summary>
    public static void DarkTitleBar(IntPtr handle)
    {
        int attribute = Environment.OSVersion.Version.Build >= 18985
            ? Native.DWMWA_USE_IMMERSIVE_DARK_MODE
            : Native.DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1;

        int on = 1;
        int hr = Native.DwmSetWindowAttribute(handle, attribute, ref on, sizeof(int));
        if (hr != 0) Log.Write($"dark title bar unavailable (attr {attribute}, hr 0x{hr:X})");
    }
}
