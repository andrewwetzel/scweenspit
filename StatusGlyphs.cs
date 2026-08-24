using System.Drawing;
using System.Drawing.Drawing2D;

namespace ScweenSpit;

/// <summary>
/// Draws the status indicators as vector shapes.
///
/// The obvious alternative is a glyph font — Segoe MDL2 Assets or Segoe Fluent Icons — but which of
/// those exists, and which code point means what, varies by Windows version, and a missing glyph
/// renders as a hollow box with no warning. Twenty lines of GDI+ per icon always look right and
/// always match the theme.
/// </summary>
internal static class StatusGlyphs
{
    public static void Battery(Graphics g, Rectangle area, int percent, bool charging, Color colour)
    {
        int w = 22, h = 11;
        var body = new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);

        using var pen = new Pen(colour, 1.4f);
        g.DrawRectangle(pen, body);
        g.FillRectangle(new SolidBrush(colour), body.Right + 1, body.Y + 3, 2, h - 6);

        int fill = Math.Max(1, (int)Math.Round((body.Width - 4) * percent / 100.0));
        var level = percent <= 15 && !charging ? Color.FromArgb(235, 120, 120)
                  : charging ? Color.FromArgb(120, 210, 150)
                  : colour;

        using var brush = new SolidBrush(level);
        g.FillRectangle(brush, body.X + 2, body.Y + 2, fill, body.Height - 4);

        if (!charging) return;

        // A bolt, so a charging battery reads as charging at a glance rather than just "full".
        using var bolt = new SolidBrush(Color.FromArgb(250, 250, 250));
        var cx = body.X + body.Width / 2; var cy = body.Y + body.Height / 2;
        g.FillPolygon(bolt, new[]
        {
            new Point(cx + 1, cy - 5), new Point(cx - 3, cy + 1),
            new Point(cx, cy + 1), new Point(cx - 1, cy + 5),
            new Point(cx + 3, cy - 1), new Point(cx, cy - 1),
        });
    }

    public static void Network(Graphics g, Rectangle area, LinkKind link, Color colour)
    {
        var faint = Color.FromArgb(90, colour);
        int cx = area.X + area.Width / 2, bottom = area.Y + area.Height / 2 + 6;

        if (link == LinkKind.Wired)
        {
            using var pen = new Pen(colour, 1.4f);
            var box = new Rectangle(cx - 8, bottom - 11, 16, 11);
            g.DrawRectangle(pen, box);
            g.DrawLine(pen, cx, box.Bottom, cx, box.Bottom + 3);
            g.DrawLine(pen, cx - 5, box.Bottom + 3, cx + 5, box.Bottom + 3);
            return;
        }

        // Ascending bars: the familiar wireless shape, greyed out when nothing is connected.
        for (int i = 0; i < 4; i++)
        {
            int height = 3 + i * 3;
            var bar = new Rectangle(cx - 9 + i * 5, bottom - height, 3, height);
            using var brush = new SolidBrush(link == LinkKind.Wireless ? colour : faint);
            g.FillRectangle(brush, bar);
        }

        if (link != LinkKind.None) return;

        using var slash = new Pen(Color.FromArgb(235, 120, 120), 1.6f);
        g.DrawLine(slash, cx - 9, bottom - 12, cx + 9, bottom + 2);
    }

    public static void Volume(Graphics g, Rectangle area, int percent, bool muted, Color colour)
    {
        int cx = area.X + area.Width / 2 - 3, cy = area.Y + area.Height / 2;
        using var brush = new SolidBrush(colour);

        g.FillPolygon(brush, new[]
        {
            new Point(cx - 7, cy - 3), new Point(cx - 3, cy - 3), new Point(cx + 1, cy - 8),
            new Point(cx + 1, cy + 8), new Point(cx - 3, cy + 3), new Point(cx - 7, cy + 3),
        });

        if (muted)
        {
            using var slash = new Pen(Color.FromArgb(235, 120, 120), 1.6f);
            g.DrawLine(slash, cx + 3, cy - 6, cx + 11, cy + 6);
            return;
        }

        // One arc per third of the range, so the icon carries a rough level as well as a state.
        using var arc = new Pen(colour, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        int arcs = percent <= 0 ? 0 : percent < 34 ? 1 : percent < 67 ? 2 : 3;
        for (int i = 0; i < arcs; i++)
        {
            int r = 4 + i * 4;
            g.DrawArc(arc, cx + 2 - r, cy - r, r * 2, r * 2, -55, 110);
        }
    }
}
