using System.Drawing;
using System.Drawing.Drawing2D;

namespace ScweenSpit;

/// <summary>One thing with a percentage: what it is called, how full it is, and the long form.</summary>
internal readonly record struct Meter(string Label, int Percent, string Detail,
                                      Color? LabelColour = null, bool BoldLabel = false);

/// <summary>
/// A stack of thin labelled bars inside the status cluster.
///
/// Shared by the Claude limits, the Antigravity limits, and the machine's own load so their row
/// heights, typography and track geometry stay aligned.
///
/// The palette thresholds step at 50% (amber) and 85% (red) as in claude-usage-widget by Niccolò
/// Sabato (MIT — see THIRD-PARTY-NOTICES.md), while allowing each service strip to use its own
/// normal (<50%) accent colour and optional header row so Claude and Antigravity are immediately
/// distinguishable side by side.
/// </summary>
internal static class MeterStrip
{
    public static readonly Color Low = Color.FromArgb(0x2A, 0x78, 0xD6);        // blue   — Claude / machine (<50%)
    public static readonly Color VioletLow = Color.FromArgb(0x9D, 0x65, 0xFF);  // violet — Antigravity (<50%)
    private static readonly Color Mid = Color.FromArgb(0xFA, 0xB2, 0x19);       // amber  — over half
    public static readonly Color High = Color.FromArgb(0xD0, 0x3B, 0x3B);       // red    — nearly out

    public const int BarThickness = 6;

    /// <summary>Room for the widest name at the size it is drawn ("Claude 5h" / "Claude 7d").</summary>
    private const int NameWidth = 56;
    private const int FigureWidth = 32;

    /// <summary>
    /// Room the strip needs along the bar, for the tallest stack it will be asked to draw. Wide
    /// enough for a name, a track worth looking at, and a figure — a bar with a name but no room
    /// left to fill is not telling anyone anything.
    /// </summary>
    public static int Extent(bool vertical) => vertical ? 84 : 176;

    /// <summary>The colour a bar takes at a given reading.</summary>
    public static Color Fill(int percent, Color? lowColour = null) =>
        percent >= 85 ? High : percent >= 50 ? Mid : (lowColour ?? Low);

    public static void Paint(Graphics g, Rectangle area, IReadOnlyList<Meter> meters,
                             Color? lowColour = null, string? headerTitle = null,
                             string? headerTag = null, Color? headerColour = null,
                             bool sparkleGlyph = false)
    {
        if (meters.Count == 0) return;

        bool vertical = area.Height > area.Width;
        bool showHeader = !vertical && !string.IsNullOrEmpty(headerTitle);
        int totalRows = meters.Count + (showHeader ? 1 : 0);

        // A vertical bar is too narrow to put a name beside a track, so each meter takes two lines
        // there and one here.
        int rowHeight = Math.Max(10, Math.Min(vertical ? 27 : 15, (area.Height - 4) / totalRows));
        int top = area.Y + Math.Max(2, (area.Height - totalRows * rowHeight) / 2);

        using var nameFont = Theme.Face(vertical ? 6.5f : 7f);
        using var boldNameFont = Theme.Face(vertical ? 6.5f : 7f, FontStyle.Bold);
        using var figureFont = Theme.Face(7.5f, FontStyle.Bold);
        using var muted = new SolidBrush(Theme.Muted);

        int meterStartRow = 0;
        if (showHeader)
        {
            var hdrRow = new Rectangle(area.X + 4, top, area.Width - 8, rowHeight);
            PaintHeaderRow(g, hdrRow, headerTitle!, headerTag, headerColour ?? Theme.Accent,
                           sparkleGlyph, boldNameFont, nameFont, muted);
            meterStartRow = 1;
        }

        for (int i = 0; i < meters.Count; i++)
        {
            var meter = meters[i];
            int r = meterStartRow + i;
            var row = new Rectangle(area.X + 4, top + r * rowHeight, area.Width - 8, rowHeight);

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

            var barColour = Fill(meter.Percent, lowColour);
            Track(g, track, meter.Percent, barColour);

            using var centred = new StringFormat
            {
                Alignment = vertical ? StringAlignment.Center : StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.Character,
                FormatFlags = StringFormatFlags.NoWrap,
            };

            if (meter.LabelColour is { } customLbl)
            {
                using var lblBrush = new SolidBrush(customLbl);
                g.DrawString(meter.Label, meter.BoldLabel ? boldNameFont : nameFont, lblBrush, name, centred);
            }
            else
            {
                g.DrawString(meter.Label, meter.BoldLabel ? boldNameFont : nameFont, muted, name, centred);
            }

            using var brush = new SolidBrush(barColour);
            using var figures = new StringFormat
            {
                Alignment = vertical ? StringAlignment.Center : StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            g.DrawString($"{meter.Percent}%", figureFont, brush, figure, figures);
        }
    }

    private static void PaintHeaderRow(Graphics g, Rectangle row, string title, string? tag,
                                       Color titleColour, bool sparkleGlyph,
                                       Font titleFont, Font tagFont, Brush tagBrush)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var titleBrush = new SolidBrush(titleColour);
        int textX = row.X;

        if (sparkleGlyph)
        {
            // Draw a crisp 4-point Gemini/Antigravity star so it never depends on font coverage.
            float cx = row.X + 4.5f;
            float cy = row.Y + row.Height / 2f;
            float rOuter = 4.2f;
            float rInner = 1.4f;
            var pts = new PointF[]
            {
                new(cx, cy - rOuter),
                new(cx + rInner, cy - rInner),
                new(cx + rOuter, cy),
                new(cx + rInner, cy + rInner),
                new(cx, cy + rOuter),
                new(cx - rInner, cy + rInner),
                new(cx - rOuter, cy),
                new(cx - rInner, cy - rInner),
            };
            g.FillPolygon(titleBrush, pts);
            textX += 11;
        }
        else
        {
            // Small diamond badge for Claude header when fewer than 3 rows are shown.
            float cx = row.X + 4f;
            float cy = row.Y + row.Height / 2f;
            float r = 3.5f;
            var pts = new PointF[]
            {
                new(cx, cy - r),
                new(cx + r, cy),
                new(cx, cy + r),
                new(cx - r, cy),
            };
            g.FillPolygon(titleBrush, pts);
            textX += 10;
        }

        g.SmoothingMode = previous;

        using var left = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(title, titleFont, titleBrush,
                     new Rectangle(textX, row.Y, row.Width - (textX - row.X), row.Height), left);

        if (!string.IsNullOrEmpty(tag))
        {
            using var right = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            g.DrawString(tag, tagFont, tagBrush, row, right);
        }
    }

    /// <summary>One meter: an unfilled track with the used portion drawn over it.</summary>
    public static void Track(Graphics g, Rectangle bar, int percent, Color? fillColour = null)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var trough = new SolidBrush(Theme.Raised))
            Rounded(g, bar, trough);

        if (percent > 0)
        {
            // Always at least a sliver: 1% that renders as nothing reads as 0%.
            int used = Math.Max(BarThickness, (int)Math.Round(bar.Width * percent / 100.0));
            using var fill = new SolidBrush(fillColour ?? Fill(percent));
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
