using System.Drawing;
using System.Drawing.Drawing2D;

namespace ScweenSpit;

/// <summary>One thing with a percentage: what it is called, how full it is, and the long form.</summary>
internal readonly record struct Meter(string Label, int Percent, string Detail,
                                      Color? LabelColour = null, bool BoldLabel = false,
                                      string? Subtext = null, Color? BarColour = null,
                                      string? FigureText = null, Color? SubtextColour = null,
                                      int? SecondPercent = null, string? SecondFigureText = null,
                                      Color? SecondBarColour = null, string? SecondLabel = null,
                                      string? SecondSubtext = null);

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

    /// <summary>Room a usage strip with a reset-countdown column or paired 5h/7d bars needs along the bar.</summary>
    public static int UsageExtent(bool vertical, int itemCount = 2, int maxRowsPerColumn = 3,
                                  int barBreadth = 50, int scalePercent = 100)
    {
        if (vertical) return 96;
        int cols = ColumnsFor(false, itemCount, maxRowsPerColumn);
        double hScale = Math.Clamp(barBreadth / 50.0, 0.9, 1.85);
        double uScale = Math.Clamp(scalePercent, 80, 300) / 100.0;
        int colW = (int)Math.Round(256 * hScale * uScale);
        return cols * colW - (cols - 1) * 8;
    }

    /// <summary>
    /// Computes the header row rectangle and scale for a horizontal strip with <paramref name="meterCount"/>
    /// rows, or null if the strip is vertical or too short for a header row.
    /// </summary>
    public static Rectangle? GetHeaderRow(Rectangle area, int meterCount, int maxRowsPerColumn, out float scale)
    {
        scale = 1f;
        if (meterCount <= 0 || area.Height > area.Width) return null;

        int maxRows = Math.Clamp(maxRowsPerColumn, 1, 8);
        int cols = ColumnsFor(false, meterCount, maxRows);
        int meterRowsPerCol = (int)Math.Ceiling(meterCount / (double)cols);
        bool showHeader = (meterRowsPerCol + 1 <= maxRows) && (area.Height - 2) / (meterRowsPerCol + 1) >= 11;
        if (!showHeader) return null;

        int totalRows = meterRowsPerCol + 1;
        int minRowH = totalRows >= 4 ? 8 : 10;
        int maxRowH = 38;
        int vPad = totalRows >= 5 ? 2 : (area.Height >= 56 ? 6 : 4);
        int availH = Math.Max(totalRows * minRowH, area.Height - vPad);
        int rowHeight = Math.Clamp(availH / totalRows, minRowH, maxRowH);
        int top = area.Y + Math.Max(1, (area.Height - totalRows * rowHeight) / 2);
        scale = Math.Clamp(rowHeight / 13.0f, 0.72f, 2.0f);
        int outerPad = area.Width < 96 ? 3 : Math.Clamp((int)Math.Round(5 * scale), 4, 10);
        return new Rectangle(area.X + outerPad, top, Math.Max(16, area.Width - outerPad * 2), rowHeight);
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
                             bool sparkleGlyph = false, int maxRowsPerColumn = 3,
                             Action<Graphics, Rectangle, float>? paintHeaderRight = null)
    {
        if (meters.Count == 0) return;

        bool vertical = area.Height > area.Width;
        bool hasSubtext = meters.Any(m => !string.IsNullOrEmpty(m.Subtext) || !string.IsNullOrEmpty(m.SecondSubtext));

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

        int outerPad = vertical ? 4 : (area.Width < 96 ? 3 : Math.Clamp((int)Math.Round(5 * scale), 4, 10));
        int colGap = cols > 1 ? (area.Width < 180 ? 6 : Math.Clamp((int)Math.Round(12 * scale), 10, 20)) : 0;

        int meterStartRow = 0;
        if (showHeader)
        {
            var hdrRow = new Rectangle(area.X + outerPad, top, Math.Max(16, area.Width - outerPad * 2), rowHeight);
            PaintHeaderRow(g, hdrRow, headerTitle!, headerTag, headerColour ?? Theme.Accent,
                           sparkleGlyph, boldNameFont, nameFont, muted, scale, paintHeaderRight);
            meterStartRow = 1;
        }

        int totalInnerW = Math.Max(16, area.Width - outerPad * 2);
        int colWidth = cols == 1 ? totalInnerW : Math.Max(24, (totalInnerW - colGap * (cols - 1)) / cols);

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
            bool colHasPaired = false;
            bool colHasPairSubtext = false;
            float maxLblW = 0f, maxFigW = 0f, maxSubW = 0f;
            float maxPairFig1W = 0f, maxPairSub1W = 0f;
            float maxPairFig2W = 0f, maxPairSub2W = 0f;
            for (int j = 0; j < countInCol; j++)
            {
                var m = meters[startIdx + j];
                float lw = g.MeasureString(m.Label, m.BoldLabel ? boldNameFont : nameFont).Width;
                if (lw > maxLblW) maxLblW = lw;

                string ft = m.FigureText ?? $"{m.Percent}%";
                float fw = g.MeasureString(ft, figureFont).Width;
                if (fw > maxFigW) maxFigW = fw;

                if (m.SecondPercent is { } p2)
                {
                    colHasPaired = true;
                    if (fw > maxPairFig1W) maxPairFig1W = fw;

                    string ft2 = m.SecondFigureText ?? $"{p2}%";
                    float fw2 = g.MeasureString(ft2, figureFont).Width;
                    if (fw2 > maxFigW) maxFigW = fw2;
                    if (fw2 > maxPairFig2W) maxPairFig2W = fw2;

                    if (!string.IsNullOrEmpty(m.Subtext))
                    {
                        colHasPairSubtext = true;
                        float sw1 = g.MeasureString(m.Subtext, subFont).Width;
                        if (sw1 > maxPairSub1W) maxPairSub1W = sw1;
                    }
                    if (!string.IsNullOrEmpty(m.SecondSubtext))
                    {
                        colHasPairSubtext = true;
                        float sw2 = g.MeasureString(m.SecondSubtext, subFont).Width;
                        if (sw2 > maxPairSub2W) maxPairSub2W = sw2;
                    }
                }
                else if (!string.IsNullOrEmpty(m.Subtext))
                {
                    colHasSubtext = true;
                    float sw = g.MeasureString(m.Subtext, subFont).Width;
                    if (sw > maxSubW) maxSubW = sw;
                }
            }

            int naturalLblW = (int)Math.Ceiling(maxLblW) + 2;
            int naturalFigW = (int)Math.Ceiling(maxFigW) + 2;
            int naturalSubW = (int)Math.Ceiling(maxSubW) + 3;

            bool showSubInCol = colHasSubtext
                && colWidth >= naturalLblW + naturalFigW + naturalSubW + 20
                && colWidth >= 112;

            // If subtext is omitted because the column was dragged narrow, any row whose figure was a
            // placeholder "—" (e.g. Bambu "Idle" / "Off") promotes its subtext into the figure slot.
            if (!showSubInCol && colHasSubtext)
            {
                for (int j = 0; j < countInCol; j++)
                {
                    var m = meters[startIdx + j];
                    if (m.FigureText == "—" && !string.IsNullOrEmpty(m.Subtext))
                    {
                        float fw = g.MeasureString(m.Subtext, figureFont).Width;
                        if (fw > maxFigW) maxFigW = fw;
                    }
                }
                naturalFigW = (int)Math.Ceiling(maxFigW) + 2;
            }

            bool tightCol = colWidth < 115 || colHasPaired;
            int minNameW = tightCol ? naturalLblW : (int)Math.Round((showSubInCol ? 36 : 28) * scale);
            int nameW = Math.Clamp(Math.Max(minNameW, naturalLblW),
                                   16, Math.Max(20, colWidth * 44 / 100));

            int minFigW = tightCol ? naturalFigW : (int)Math.Round(26 * scale);
            int figW = Math.Clamp(Math.Max(minFigW, naturalFigW),
                                  18, Math.Max(22, colWidth * 36 / 100));

            int subW = 0;
            if (showSubInCol)
            {
                int minSubW = (int)Math.Round(36 * scale);
                subW = Math.Clamp(Math.Max(minSubW, naturalSubW),
                                  30, Math.Max(34, colWidth * 30 / 100));
            }

            int trackGap = tightCol ? 3 : Math.Clamp((int)Math.Round(4 * scale), 4, 8);

            for (int j = 0; j < countInCol; j++)
            {
                var meter = meters[startIdx + j];
                int r = meterStartRow + j;
                var row = new Rectangle(colX, top + r * rowHeight, colWidth, rowHeight);

                if (meter.SecondPercent is { } secondPct)
                {
                    Rectangle nameRect;
                    if (vertical)
                    {
                        int nameH = Math.Max(10, (rowHeight - barThick) / 2);
                        nameRect = new Rectangle(row.X, row.Y, row.Width, nameH);
                        int halfTrackW = Math.Max(4, (row.Width - 3) / 2);
                        var track1 = new Rectangle(row.X, row.Y + nameH, halfTrackW, barThick);
                        var track2 = new Rectangle(row.X + halfTrackW + 3, row.Y + nameH, Math.Max(4, row.Right - (row.X + halfTrackW + 3)), barThick);
                        var figRect = new Rectangle(row.X, row.Y + nameH + barThick, row.Width, Math.Max(8, rowHeight - barThick - nameH));

                        var c1 = meter.BarColour ?? Fill(meter.Percent, lowColour);
                        var c2 = meter.SecondBarColour ?? Fill(secondPct, lowColour);
                        Track(g, track1, meter.Percent, c1);
                        Track(g, track2, secondPct, c2);

                        if (meter.LabelColour is { } customLblV)
                        {
                            using var lblBrushV = new SolidBrush(customLblV);
                            g.DrawString(meter.Label, meter.BoldLabel ? boldNameFont : nameFont, lblBrushV, nameRect, centred);
                        }
                        else
                        {
                            g.DrawString(meter.Label, meter.BoldLabel ? boldNameFont : nameFont, defaultLblBrush, nameRect, centred);
                        }

                        using var b1 = new SolidBrush(secondPct > meter.Percent ? c2 : c1);
                        string vText = (!string.IsNullOrEmpty(meter.Subtext) || !string.IsNullOrEmpty(meter.SecondSubtext))
                            ? $"{meter.Percent}% {meter.Subtext?.Replace(" ", "")} · {secondPct}% {meter.SecondSubtext?.Replace(" ", "")}".Trim()
                            : $"{meter.Percent}·{secondPct}%";
                        if (g.MeasureString(vText, subFont).Width <= figRect.Width + 2)
                            g.DrawString(vText, subFont, b1, figRect, figures);
                        else
                            g.DrawString($"{meter.Percent}·{secondPct}%", figureFont, b1, figRect, figures);
                    }
                    else
                    {
                        nameRect = new Rectangle(row.X, row.Y, nameW, rowHeight);
                        if (meter.LabelColour is { } customLblP)
                        {
                            using var lblBrushP = new SolidBrush(customLblP);
                            g.DrawString(meter.Label, meter.BoldLabel ? boldNameFont : nameFont, lblBrushP, nameRect, centred);
                        }
                        else
                        {
                            g.DrawString(meter.Label, meter.BoldLabel ? boldNameFont : nameFont, defaultLblBrush, nameRect, centred);
                        }

                        int remX = row.X + nameW + trackGap;
                        int remW = Math.Max(16, row.Right - remX);
                        int pairGap = Math.Clamp((int)Math.Round(6 * scale), 4, 10);

                        int pFig1W = Math.Max(16, (int)Math.Ceiling(maxPairFig1W) + 2);
                        int pFig2W = Math.Max(16, (int)Math.Ceiling(maxPairFig2W) + 2);
                        int pSub1W = maxPairSub1W > 0 ? (int)Math.Ceiling(maxPairSub1W) + 2 : 0;
                        int pSub2W = maxPairSub2W > 0 ? (int)Math.Ceiling(maxPairSub2W) + 2 : 0;

                        int secondLblW = 0;
                        if (!string.IsNullOrEmpty(meter.SecondLabel))
                        {
                            float lw2 = g.MeasureString(meter.SecondLabel, meter.BoldLabel ? boldNameFont : nameFont).Width;
                            int candW = (int)Math.Ceiling(lw2) + 2;
                            int minNeededForLbl = colHasPairSubtext
                                ? candW + trackGap + pairGap + pFig1W + pSub1W + pFig2W + pSub2W + 24
                                : candW + trackGap + pairGap + pFig1W + pFig2W + 28;
                            if (remW >= minNeededForLbl)
                                secondLblW = candW;
                        }

                        int slotsAvailW = secondLblW > 0
                            ? remW - pairGap - secondLblW - trackGap
                            : remW - pairGap;

                        bool showPairSub = colHasPairSubtext
                            && slotsAvailW >= pFig1W + pSub1W + pFig2W + pSub2W + 16;

                        int t1 = pFig1W + (showPairSub && pSub1W > 0 ? pSub1W + 1 : 0);
                        int t2 = pFig2W + (showPairSub && pSub2W > 0 ? pSub2W + 1 : 0);
                        int spareTrackW = Math.Max(0, slotsAvailW - t1 - t2);
                        int slot1W = showPairSub
                            ? Math.Clamp(t1 + spareTrackW / 2, 8, Math.Max(8, slotsAvailW - 8))
                            : Math.Max(8, slotsAvailW / 2);

                        int x1 = remX;
                        Rectangle fig1, sub1 = Rectangle.Empty, track1;
                        if (showPairSub && pSub1W > 0)
                        {
                            sub1 = new Rectangle(x1 + slot1W - pSub1W, row.Y, pSub1W, rowHeight);
                            fig1 = new Rectangle(sub1.X - pFig1W - 1, row.Y, pFig1W, rowHeight);
                            track1 = new Rectangle(x1, row.Y + (rowHeight - barThick) / 2, Math.Max(0, fig1.X - x1 - 2), barThick);
                        }
                        else
                        {
                            int pairFig1W = Math.Min(pFig1W, Math.Max(16, slot1W * 60 / 100));
                            fig1 = new Rectangle(x1 + slot1W - pairFig1W, row.Y, pairFig1W, rowHeight);
                            track1 = new Rectangle(x1, row.Y + (rowHeight - barThick) / 2, Math.Max(0, fig1.X - x1 - 2), barThick);
                        }

                        int x2 = x1 + slot1W + pairGap;
                        if (secondLblW > 0)
                        {
                            var name2Rect = new Rectangle(x2, row.Y, secondLblW, rowHeight);
                            if (meter.LabelColour is { } customLbl2)
                            {
                                using var lblBrush2 = new SolidBrush(customLbl2);
                                g.DrawString(meter.SecondLabel, meter.BoldLabel ? boldNameFont : nameFont, lblBrush2, name2Rect, centred);
                            }
                            else
                            {
                                g.DrawString(meter.SecondLabel, meter.BoldLabel ? boldNameFont : nameFont, defaultLblBrush, name2Rect, centred);
                            }
                            x2 += secondLblW + trackGap;
                        }

                        int slot2W = Math.Max(8, row.Right - x2);
                        Rectangle fig2, sub2 = Rectangle.Empty, track2;
                        if (showPairSub && pSub2W > 0)
                        {
                            sub2 = new Rectangle(row.Right - pSub2W, row.Y, pSub2W, rowHeight);
                            fig2 = new Rectangle(sub2.X - pFig2W - 1, row.Y, pFig2W, rowHeight);
                            track2 = new Rectangle(x2, row.Y + (rowHeight - barThick) / 2, Math.Max(0, fig2.X - x2 - 2), barThick);
                        }
                        else
                        {
                            int pairFig2W = Math.Min(pFig2W, Math.Max(16, slot2W * 60 / 100));
                            fig2 = new Rectangle(row.Right - pairFig2W, row.Y, pairFig2W, rowHeight);
                            track2 = new Rectangle(x2, row.Y + (rowHeight - barThick) / 2, Math.Max(0, fig2.X - x2 - 2), barThick);
                        }

                        var c1 = meter.BarColour ?? Fill(meter.Percent, lowColour);
                        var c2 = meter.SecondBarColour ?? Fill(secondPct, lowColour);
                        if (track1.Width >= 6) Track(g, track1, meter.Percent, c1);
                        if (track2.Width >= 6) Track(g, track2, secondPct, c2);

                        using var b1 = new SolidBrush(c1);
                        using var b2 = new SolidBrush(c2);
                        string ft1 = meter.FigureText ?? $"{meter.Percent}%";
                        string ft2 = meter.SecondFigureText ?? $"{secondPct}%";
                        g.DrawString(ft1, figureFont, b1, fig1, figures);
                        g.DrawString(ft2, figureFont, b2, fig2, figures);

                        if (showPairSub)
                        {
                            if (!string.IsNullOrEmpty(meter.Subtext) && sub1.Width > 0)
                                g.DrawString(meter.Subtext, subFont, subBrush, sub1, figures);
                            if (!string.IsNullOrEmpty(meter.SecondSubtext) && sub2.Width > 0)
                                g.DrawString(meter.SecondSubtext, subFont, subBrush, sub2, figures);
                        }
                    }
                    continue;
                }

                Rectangle name, track, figure, sub = Rectangle.Empty;
                if (vertical)
                {
                    int nameH = Math.Max(10, (rowHeight - barThick) / 2);
                    name   = new Rectangle(row.X, row.Y, row.Width, nameH);
                    track  = new Rectangle(row.X, row.Y + nameH, row.Width, barThick);
                    figure = new Rectangle(row.X, row.Y + nameH + barThick, row.Width, Math.Max(8, rowHeight - barThick - nameH));
                }
                else if (showSubInCol)
                {
                    name   = new Rectangle(row.X, row.Y, nameW, rowHeight);
                    sub    = new Rectangle(row.Right - subW, row.Y, subW, rowHeight);
                    figure = new Rectangle(sub.X - figW - 2, row.Y, figW, rowHeight);
                    int trackX = row.X + nameW + trackGap / 2;
                    int trackW = Math.Max(0, figure.X - trackX - trackGap / 2);
                    track  = new Rectangle(trackX, row.Y + (rowHeight - barThick) / 2, trackW, barThick);
                }
                else
                {
                    name   = new Rectangle(row.X, row.Y, nameW, rowHeight);
                    figure = new Rectangle(row.Right - figW, row.Y, figW, rowHeight);
                    int trackX = row.X + nameW + trackGap / 2;
                    int trackW = Math.Max(0, figure.X - trackX - trackGap / 2);
                    track  = new Rectangle(trackX, row.Y + (rowHeight - barThick) / 2, trackW, barThick);
                }

                var barColour = meter.BarColour ?? Fill(meter.Percent, lowColour);
                if (vertical || track.Width >= 6)
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
                    string shownFig = (!showSubInCol && figText == "—" && !string.IsNullOrEmpty(meter.Subtext))
                        ? meter.Subtext!
                        : figText;
                    g.DrawString(shownFig, figureFont, brush, figure, figures);
                    if (!vertical && showSubInCol && !string.IsNullOrEmpty(meter.Subtext))
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
                                       Font titleFont, Font tagFont, Brush tagBrush, float scale,
                                       Action<Graphics, Rectangle, float>? paintHeaderRight = null)
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

        int availTextW = Math.Max(8, row.Right - textX);
        float titleW = g.MeasureString(title, titleFont).Width;

        if (paintHeaderRight is not null)
        {
            using var leftCustom = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            int titleWInt = Math.Min(availTextW, (int)Math.Ceiling(titleW) + 2);
            g.DrawString(title, titleFont, titleBrush,
                         new Rectangle(textX, row.Y, titleWInt, row.Height), leftCustom);

            int rightX = textX + titleWInt + (int)Math.Round(3 * scale);
            if (row.Right - rightX >= 14)
            {
                paintHeaderRight(g, new Rectangle(rightX, row.Y, row.Right - rightX, row.Height), scale);
            }
            return;
        }

        float tagW = !string.IsNullOrEmpty(tag) ? g.MeasureString(tag, tagFont).Width : 0f;
        bool drawTag = !string.IsNullOrEmpty(tag) && availTextW >= titleW + tagW + 4f;

        using var left = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        int titleBoxW = drawTag ? Math.Max(8, availTextW - (int)Math.Ceiling(tagW) - 2) : availTextW;
        g.DrawString(title, titleFont, titleBrush,
                     new Rectangle(textX, row.Y, titleBoxW, row.Height), left);

        if (drawTag)
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
