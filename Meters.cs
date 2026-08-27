using System.Drawing;
using System.Drawing.Drawing2D;

namespace ScweenSpit;

/// <summary>One thing with a percentage: what it is called, how full it is, and the long form.</summary>
internal readonly record struct Meter(string Label, int Percent, string Detail);

/// <summary>
/// A stack of thin labelled bars inside the status cluster.
///
/// Shared, because the Claude limits and the machine's own load are the same picture drawn from
/// different numbers, and two copies of it would drift apart the first time either was adjusted.
///
/// The palette and the thresholds it steps at come from claude-usage-widget by Niccolò Sabato
/// (MIT — see THIRD-PARTY-NOTICES.md): every bar is coloured by its own reading rather than by which
/// bar it is, which is what lets three of them be read at once without a legend.
/// </summary>
internal static class MeterStrip
{
    private static readonly Color Low = Color.FromArgb(0x2A, 0x78, 0xD6);   // blue  — plenty left
    private static readonly Color Mid = Color.FromArgb(0xFA, 0xB2, 0x19);   // amber — over half
    public static readonly Color High = Color.FromArgb(0xD0, 0x3B, 0x3B);   // red   — nearly out

    public const int BarThickness = 6;

    /// <summary>Room for the widest name at the size it is drawn, measured once.</summary>
    private const int NameWidth = 48;
    private const int FigureWidth = 32;

    /// <summary>
    /// Room the strip needs along the bar, for the tallest stack it will be asked to draw. Wide
    /// enough for a name, a track worth looking at, and a figure — a bar with a name but no room
    /// left to fill is not telling anyone anything.
    /// </summary>
    public static int Extent(bool vertical) => vertical ? 84 : 168;

    /// <summary>The colour a bar takes at a given reading.</summary>
    public static Color Fill(int percent) => percent >= 85 ? High : percent >= 50 ? Mid : Low;

    public static void Paint(Graphics g, Rectangle area, IReadOnlyList<Meter> meters)
    {
        if (meters.Count == 0) return;

        bool vertical = area.Height > area.Width;
        int rows = meters.Count;

        // A vertical bar is too narrow to put a name beside a track, so each meter takes two lines
        // there and one here.
        int rowHeight = Math.Max(10, Math.Min(vertical ? 27 : 16, (area.Height - 4) / rows));
        int top = area.Y + Math.Max(2, (area.Height - rows * rowHeight) / 2);

        using var nameFont = Theme.Face(vertical ? 6.5f : 7f);
        using var figureFont = Theme.Face(7.5f, FontStyle.Bold);
        using var muted = new SolidBrush(Theme.Muted);

        for (int i = 0; i < rows; i++)
        {
            var meter = meters[i];
            var row = new Rectangle(area.X + 4, top + i * rowHeight, area.Width - 8, rowHeight);

            Rectangle name, track, figure;
            if (vertical)
            {
                name   = new Rectangle(row.X, row.Y, row.Width, 10);
                track  = new Rectangle(row.X, row.Y + 11, row.Width, BarThickness);
                figure = new Rectangle(row.X, row.Y + 11 + BarThickness, row.Width, rowHeight - BarThickness - 12);
            }
            else
            {
                name   = new Rectangle(row.X, row.Y, NameWidth, rowHeight);
                track  = new Rectangle(row.X + NameWidth, row.Y + (rowHeight - BarThickness) / 2,
                                       Math.Max(8, row.Width - NameWidth - FigureWidth - 4), BarThickness);
                figure = new Rectangle(row.Right - FigureWidth, row.Y, FigureWidth, rowHeight);
            }

            Track(g, track, meter.Percent);

            using var centred = new StringFormat
            {
                Alignment = vertical ? StringAlignment.Center : StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.Character,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            g.DrawString(meter.Label, nameFont, muted, name, centred);

            using var brush = new SolidBrush(Fill(meter.Percent));
            using var figures = new StringFormat
            {
                Alignment = vertical ? StringAlignment.Center : StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            g.DrawString($"{meter.Percent}%", figureFont, brush, figure, figures);
        }
    }

    /// <summary>One meter: an unfilled track with the used portion drawn over it.</summary>
    public static void Track(Graphics g, Rectangle bar, int percent)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var trough = new SolidBrush(Theme.Raised))
            Rounded(g, bar, trough);

        if (percent > 0)
        {
            // Always at least a sliver: 1% that renders as nothing reads as 0%.
            int used = Math.Max(BarThickness, (int)Math.Round(bar.Width * percent / 100.0));
            using var fill = new SolidBrush(Fill(percent));
            Rounded(g, new Rectangle(bar.X, bar.Y, Math.Min(used, bar.Width), bar.Height), fill);
        }

        g.SmoothingMode = previous;
    }

    /// <summary>A track with nothing in it yet, drawn light enough to be visible on the bar.</summary>
    public static void Placeholder(Graphics g, Rectangle bar)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var trough = new SolidBrush(Theme.Divider))
            Rounded(g, bar, trough);

        g.SmoothingMode = previous;
    }

    private static void Rounded(Graphics g, Rectangle box, Brush brush)
    {
        int radius = Math.Min(box.Height, box.Width) / 2;
        if (radius <= 1) { g.FillRectangle(brush, box); return; }

        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(box.X, box.Y, d, d, 90, 180);
        path.AddArc(box.Right - d, box.Y, d, d, 270, 180);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
