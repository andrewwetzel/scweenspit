using System.Drawing;
using System.Drawing.Drawing2D;

namespace ScweenSpit;

/// <summary>One thing with a percentage: what it is called, how full it is, and the long form.</summary>
internal readonly record struct Meter(string Label, int Percent, string Detail,
                                      Color? LabelColour = null, bool BoldLabel = false,
                                      string? Subtext = null, Color? BarColour = null,
                                      string? FigureText = null, Color? SubtextColour = null);

/// <summary>
/// A stack of thin labelled bars inside the status cluster.
///
/// Shared by the Claude limits, the Antigravity limits, the Bambu Lab printer strip, and the
/// machine's own load so their row heights, typography and track geometry stay aligned.
///
/// The palette thresholds step at 50% (amber) and 85% (red) as in claude-usage-widget by Niccolò
/// Sabato (MIT — see THIRD-PARTY-NOTICES.md), while allowing each service strip to use its own
/// normal (&lt;50%) accent colour, optional header row, and right-hand reset/status column.
/// </summary>
internal static class MeterStrip
{
    public static readonly Color Low = Color.FromArgb(0x2A, 0x78, 0xD6);        // blue   — Claude / machine (<50%)
    public static readonly Color VioletLow = Color.FromArgb(0x9D, 0x65, 0xFF);  // violet — Antigravity (<50%)
    public static readonly Color BambuLow = Color.FromArgb(0x24, 0xB8, 0x58);   // green  — Bambu printing
    public static readonly Color BambuDone = Color.FromArgb(0x2E, 0xA8, 0x8A);  // teal   — Bambu finished
    public static readonly Color Mid = Color.FromArgb(0xFA, 0xB2, 0x19);        // amber  — over half / paused
    public static readonly Color High = Color.FromArgb(0xD0, 0x3B, 0x3B);       // red    — nearly out / error
    private static readonly Color DefaultLabelColour = Color.FromArgb(184, 192, 206);
    private static readonly Color SubtextColour = Color.FromArgb(176, 184, 202);
    private static readonly Color TrackTrough = Color.FromArgb(46, 52, 64);
    private static readonly Color InnerDivider = Color.FromArgb(42, 46, 56);

    public const int BarThickness = 6;

    /// <summary>How many side-by-side columns a horizontal strip uses for <paramref name="itemCount"/> rows.</summary>
    public static int ColumnsFor(bool vertical, int itemCount, int maxRowsPerColumn = 3)
    {
        if (vertical) return 1;
        int maxRows = Math.Clamp(maxRowsPerColumn, 1, 8);
        return Math.Max(1, (int)Math.Ceiling(Math.Max(1, itemCount) / (double)maxRows));
    }

    /// <summary>Room a plain meter strip (CPU, RAM, disk) needs along the bar.</summary>
    public static int Extent(bool vertical, int itemCount = 3, int maxRowsPerColumn = 3,
                             int barBreadth = 50, int scalePercent = 100)
    {
        if (vertical) return 84;
        int cols = ColumnsFor(false, itemCount, maxRowsPerColumn);
        double hScale = Math.Clamp(barBreadth / 50.0, 0.9, 1.85);
        double uScale = Math.Clamp(scalePercent, 80, 300) / 100.0;
        int colW = (int)Math.Round(164 * hScale * uScale);
        return cols * colW - (cols - 1) * 8;
    }

    /// <summary>Room a usage strip with a reset-countdown column needs along the bar.</summary>
    public static int UsageExtent(bool vertical, int itemCount = 2, int maxRowsPerColumn = 3,
                                  int barBreadth = 50, int scalePercent = 100)
    {
        if (vertical) return 96;
        int cols = ColumnsFor(false, itemCount, maxRowsPerColumn);
        double hScale = Math.Clamp(barBreadth / 50.0, 0.9, 1.85);
        double uScale = Math.Clamp(scalePercent, 80, 300) / 100.0;
        int colW = (int)Math.Round(216 * hScale * uScale);
        return cols * colW - (cols - 1) * 8;
    }

    /// <summary>Room the Bambu printer strip needs along the bar for <paramref name="printerCount"/> printers.</summary>
    public static int BambuExtent(bool vertical, int printerCount, int maxRowsPerColumn = 3,
                                  int barBreadth = 50, int scalePercent = 100)
    {
        if (vertical)
        {
            int rows = Math.Max(2, printerCount);
            return Math.Clamp(rows * 24 + 8, 72, 160);
        }

        return UsageExtent(false, Math.Max(2, printerCount), maxRowsPerColumn, barBreadth, scalePercent);
    }

    /// <summary>The colour a bar takes at a given reading.</summary>
    public static Color Fill(int percent, Color? lowColour = null) =>
        percent >= 85 ? High : percent >= 50 ? Mid : (lowColour ?? Low);

    public static void Paint(Graphics g, Rectangle area, IReadOnlyList<Meter> meters,
                             Color? lowColour = null, string? headerTitle = null,
                             string? headerTag = null, Color? headerColour = null,
                             bool sparkleGlyph = false, int maxRowsPerColumn = 3)
    {
        if (meters.Count == 0) return;

        bool vertical = area.Height > area.Width;
        bool hasSubtext = meters.Any(m => !string.IsNullOrEmpty(m.Subtext));

        int maxRows = vertical ? meters.Count : Math.Clamp(maxRowsPerColumn, 1, 8);
        int cols = vertical ? 1 : ColumnsFor(false, meters.Count, maxRows);
        int meterRowsPerCol = (int)Math.Ceiling(meters.Count / (double)cols);

        // Include the header row when it fits inside maxRows (e.g. Antigravity's 2 limits + 1 header
        // = 3 rows, or 4 rows across 2 columns = 2 rows/col + 1 header = 3 rows) and each row gets
        // enough vertical height to stay crisp.
        bool showHeader = !vertical && !string.IsNullOrEmpty(headerTitle)
                          && (meterRowsPerCol + 1 <= maxRows)
                          && (area.Height - 2) / (meterRowsPerCol + 1) >= 11;
        int totalRows = meterRowsPerCol + (showHeader ? 1 : 0);

        int minRowH = vertical ? 18 : (totalRows >= 4 ? 8 : 10);
        int maxRowH = vertical ? 34 : 38;
        int vPad = vertical ? 4 : (totalRows >= 5 ? 2 : (area.Height >= 56 ? 6 : 4));
        int availH = Math.Max(totalRows * minRowH, area.Height - vPad);
        int rowHeight = Math.Clamp(availH / totalRows, minRowH, maxRowH);
        int top = area.Y + Math.Max(1, (area.Height - totalRows * rowHeight) / 2);
        int barThick = vertical
            ? Math.Clamp(rowHeight / 3, 6, 12)
            : Math.Clamp((int)Math.Round(rowHeight * 0.50), 5, 16);

        bool compact = !vertical && rowHeight <= 9;
        float scale = vertical ? 1f : Math.Clamp(rowHeight / 13.0f, 0.72f, 2.0f);
        float namePt = vertical ? 6.8f : (compact ? 6.3f : Math.Clamp(7.2f * scale, 6.5f, 14f));
        float figPt = vertical && hasSubtext ? 6.5f : (compact ? 6.4f : Math.Clamp(7.6f * scale, 6.8f, 14.5f));
        float subPt = compact ? 6.2f : Math.Clamp(6.8f * scale, 6.2f, 13f);

        using var nameFont = Theme.Face(namePt);
        using var boldNameFont = Theme.Face(namePt, FontStyle.Bold);
        using var figureFont = Theme.Face(figPt, FontStyle.Bold);
        using var subFont = Theme.Face(subPt);
        using var defaultLblBrush = new SolidBrush(DefaultLabelColour);
        using var muted = new SolidBrush(Theme.Muted);
        using var subBrush = new SolidBrush(SubtextColour);
        using var innerSepPen = new Pen(InnerDivider, 1f);

        int outerPad = vertical ? 4 : Math.Clamp((int)Math.Round(5 * scale), 4, 10);
        int colGap = cols > 1 ? Math.Clamp((int)Math.Round(12 * scale), 10, 20) : 0;

        int meterStartRow = 0;
        if (showHeader)
        {
            var hdrRow = new Rectangle(area.X + outerPad, top, Math.Max(16, area.Width - outerPad * 2), rowHeight);
            PaintHeaderRow(g, hdrRow, headerTitle!, headerTag, headerColour ?? Theme.Accent,
                           sparkleGlyph, boldNameFont, nameFont, muted, scale);
            meterStartRow = 1;
        }

        int totalInnerW = Math.Max(16, area.Width - outerPad * 2);
        int colWidth = cols == 1 ? totalInnerW : Math.Max(40, (totalInnerW - colGap * (cols - 1)) / cols);

        using var centred = new StringFormat
        {
            Alignment = vertical ? StringAlignment.Center : StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };
        using var figures = new StringFormat
        {
            Alignment = vertical ? StringAlignment.Center : StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };

        for (int c = 0; c < cols; c++)
        {
            int startIdx = c * meterRowsPerCol;
            int countInCol = Math.Min(meterRowsPerCol, meters.Count - startIdx);
            if (countInCol <= 0) break;

            int colX = area.X + outerPad + c * (colWidth + colGap);

            if (c > 0 && !vertical)
            {
                int sepX = colX - colGap / 2;
                int sepTop = top + meterStartRow * rowHeight + 2;
                int sepBot = top + (meterStartRow + meterRowsPerCol) * rowHeight - 2;
                if (sepBot > sepTop)
                    g.DrawLine(innerSepPen, sepX, sepTop, sepX, sepBot);
            }

            // Measure the widest label, figure, and subtext in this column so larger fonts never clip
            // and short labels (like "A1M" or "CPU") donate their spare width to the progress bar.
            bool colHasSubtext = false;
            float maxLblW = 0f, maxFigW = g.MeasureString("100%", figureFont).Width, maxSubW = 0f;
            for (int j = 0; j < countInCol; j++)
            {
                var m = meters[startIdx + j];
                float lw = g.MeasureString(m.Label, m.BoldLabel ? boldNameFont : nameFont).Width;
                if (lw > maxLblW) maxLblW = lw;

                string ft = m.FigureText ?? $"{m.Percent}%";
                float fw = g.MeasureString(ft, figureFont).Width;
                if (fw > maxFigW) maxFigW = fw;

                if (!string.IsNullOrEmpty(m.Subtext))
                {
                    colHasSubtext = true;
                    float sw = g.MeasureString(m.Subtext, subFont).Width;
                    if (sw > maxSubW) maxSubW = sw;
                }
            }

            int minNameW = (int)Math.Round((colHasSubtext ? 38 : 32) * scale);
            int nameW = Math.Clamp(Math.Max(minNameW, (int)Math.Ceiling(maxLblW) + 4),
                                   28, Math.Max(34, colWidth * 42 / 100));

            int minFigW = (int)Math.Round(28 * scale);
            int figW = Math.Clamp(Math.Max(minFigW, (int)Math.Ceiling(maxFigW) + 3),
                                  26, Math.Max(30, colWidth * 24 / 100));

            int subW = 0;
            if (colHasSubtext)
            {
                int minSubW = (int)Math.Round(40 * scale);
                subW = Math.Clamp(Math.Max(minSubW, (int)Math.Ceiling(maxSubW) + 4),
                                  36, Math.Max(40, colWidth * 30 / 100));
            }

            int trackGap = Math.Clamp((int)Math.Round(4 * scale), 4, 8);

            for (int j = 0; j < countInCol; j++)
            {
                var meter = meters[startIdx + j];
                int r = meterStartRow + j;
                var row = new Rectangle(colX, top + r * rowHeight, colWidth, rowHeight);

                Rectangle name, track, figure, sub = Rectangle.Empty;
                if (vertical)
                {
                    int nameH = Math.Max(10, (rowHeight - barThick) / 2);
                    name   = new Rectangle(row.X, row.Y, row.Width, nameH);
                    track  = new Rectangle(row.X, row.Y + nameH, row.Width, barThick);
                    figure = new Rectangle(row.X, row.Y + nameH + barThick, row.Width, Math.Max(8, rowHeight - barThick - nameH));
                }
                else if (colHasSubtext)
                {
                    name   = new Rectangle(row.X, row.Y, nameW, rowHeight);
                    sub    = new Rectangle(row.Right - subW, row.Y, subW, rowHeight);
                    figure = new Rectangle(sub.X - figW - 2, row.Y, figW, rowHeight);
                    track  = new Rectangle(row.X + nameW + trackGap / 2, row.Y + (rowHeight - barThick) / 2,
                                           Math.Max(10, figure.X - (row.X + nameW) - trackGap), barThick);
                }
                else
                {
                    name   = new Rectangle(row.X, row.Y, nameW, rowHeight);
                    figure = new Rectangle(row.Right - figW, row.Y, figW, rowHeight);
                    track  = new Rectangle(row.X + nameW + trackGap / 2, row.Y + (rowHeight - barThick) / 2,
                                           Math.Max(10, figure.X - (row.X + nameW) - trackGap), barThick);
                }

                var barColour = meter.BarColour ?? Fill(meter.Percent, lowColour);
                Track(g, track, meter.Percent, barColour);

                if (meter.LabelColour is { } customLbl)
                {
                    using var lblBrush = new SolidBrush(customLbl);
                    g.DrawString(meter.Label, meter.BoldLabel ? boldNameFont : nameFont, lblBrush, name, centred);
                }
                else
                {
                    g.DrawString(meter.Label, meter.BoldLabel ? boldNameFont : nameFont, defaultLblBrush, name, centred);
                }

                using var brush = new SolidBrush(barColour);
                string figText = meter.FigureText ?? $"{meter.Percent}%";

                if (vertical && !string.IsNullOrEmpty(meter.Subtext))
                {
                    string combined = figText == "—" ? meter.Subtext : $"{figText} · {meter.Subtext}";
                    g.DrawString(combined, figureFont, brush, figure, figures);
                }
                else
                {
                    g.DrawString(figText, figureFont, brush, figure, figures);
                    if (!vertical && !string.IsNullOrEmpty(meter.Subtext))
                    {
                        if (meter.SubtextColour is { } customSub)
                        {
                            using var customSubBrush = new SolidBrush(customSub);
                            g.DrawString(meter.Subtext, subFont, customSubBrush, sub, figures);
                        }
                        else
                        {
                            g.DrawString(meter.Subtext, subFont, subBrush, sub, figures);
                        }
                    }
                }
            }
        }
    }

    private static void PaintHeaderRow(Graphics g, Rectangle row, string title, string? tag,
                                       Color titleColour, bool sparkleGlyph,
                                       Font titleFont, Font tagFont, Brush tagBrush, float scale)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var titleBrush = new SolidBrush(titleColour);
        int textX = row.X;

        if (sparkleGlyph)
        {
            // Draw a crisp 4-point Gemini/Antigravity star so it never depends on font coverage.
            float rOuter = Math.Clamp(4.2f * scale, 3.8f, 7.5f);
            float rInner = rOuter * 0.34f;
            float cx = row.X + rOuter + 0.5f;
            float cy = row.Y + row.Height / 2f;
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
            textX += (int)Math.Ceiling(rOuter * 2 + 3 * scale);
        }
        else
        {
            // Small diamond badge for section headers.
            float r = Math.Clamp(3.5f * scale, 3.2f, 6.2f);
            float cx = row.X + r + 0.5f;
            float cy = row.Y + row.Height / 2f;
            var pts = new PointF[]
            {
                new(cx, cy - r),
                new(cx + r, cy),
                new(cx, cy + r),
                new(cx - r, cy),
            };
            g.FillPolygon(titleBrush, pts);
            textX += (int)Math.Ceiling(r * 2 + 3 * scale);
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

        using (var trough = new SolidBrush(TrackTrough))
            Rounded(g, bar, trough);

        if (percent > 0)
        {
            // Always at least a sliver: 1% that renders as nothing reads as 0%.
            int minUsed = Math.Max(BarThickness, bar.Height);
            int used = Math.Max(minUsed, (int)Math.Round(bar.Width * percent / 100.0));
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
