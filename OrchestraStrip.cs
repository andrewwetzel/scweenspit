using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;

namespace ScweenSpit;

/// <summary>
/// Draws the orchestrator strip: one chunk per repo each orchestrator is working in, with how long
/// it has been at it and how many children of each kind it has there, e.g. "◆ mono 3:12:05 ● 3 ● 1".
/// The counts carry no names: each is drawn in its service's own colour — Claude's blue,
/// Antigravity's violet — the same ones the usage strips beside it use.
///
/// The timer is drawn from the local clock on every repaint, so it ticks with the bar's own
/// one-second refresh rather than with the feed's poll.
///
/// Beneath the chunks, a "Needs you" row counts what the orchestrator is waiting on a person for —
/// "! 2 need you", red when any of it is blocking a run — and is the strip's whole content while
/// no run is in flight. Clicking the strip then opens the list itself.
/// </summary>
internal static class OrchestraStrip
{
    private static readonly Color TitleColour = Color.FromArgb(226, 204, 132);
    private static readonly Color AgyIdeColour = Color.FromArgb(232, 160, 232);
    private static readonly Color WaitingColour = Color.FromArgb(255, 158, 24);
    private static readonly Color CountColour = Color.FromArgb(214, 220, 232);
    public static readonly Color BlockingColour = Color.FromArgb(255, 99, 99);
    public static readonly Color NeedColour = WaitingColour;

    /// <summary>
    /// What the Needs row says, or null when nothing is listed. Needs already marked done but not
    /// yet closed by the orchestrator are still counted, quietly, so the row does not vanish and
    /// reappear while it checks.
    /// </summary>
    public static (string Text, Color Colour)? NeedsLine(OrchestraReading? reading)
    {
        var needs = reading?.Needs ?? [];
        if (needs.Count == 0) return null;
        int open = reading!.Outstanding, blocking = reading.OutstandingBlocking;
        if (open == 0) return ($"✓ {needs.Count} confirming", Theme.Muted);
        return ($"{open} need{(open == 1 ? "s" : "")} you", blocking > 0 ? BlockingColour : NeedColour);
    }

    private static Color KindColour(string tool) => tool.ToLowerInvariant() switch
    {
        "claude" => UsageStrip.ClaudeTitleColour,
        "agy" => UsageStrip.AntigravityTitleColour,
        "agy-ide" => AgyIdeColour,
        _ => CountColour,
    };

    private static string TimerText(OrchestraChunk c) =>
        !c.Active ? "idle" : c.Elapsed is { } t ? OrchestraUsage.FormatElapsed(t) : "—";

    // The bar asks for its extents many times per paint and per mouse move, and measuring text
    // needs a Graphics. Both are kept: the answer only changes with the reading, the bar's shape,
    // or the timer growing a digit. UI thread only, like every caller.
    private static readonly Bitmap measureSurface = new(1, 1);
    private static readonly Graphics measure = Graphics.FromImage(measureSurface);
    private static (OrchestraReading? Reading, bool Vertical, int Rows, int Breadth, int Scale, bool ShowTimer, string Timers, int Extent)? lastExtent;

    /// <summary>Room the strip needs along the bar, sized to what it currently has to say.</summary>
    public static int Extent(bool vertical, int maxRowsPerColumn = 3, int barBreadth = 50, int scalePercent = 100)
    {
        var reading = OrchestraUsage.Current;
        var feeds = reading?.Shown ?? [];
        long now = Environment.TickCount64;
        var chunks = OrchestraUsage.Chunks(feeds, now);
        var needs = NeedsLine(reading);
        bool showTimer = OrchestraUsage.ShowTimer;
        if (vertical)
            return Math.Clamp(Math.Max(1, chunks.Count) * (showTimer ? 42 : 32) + (needs is null ? 0 : 30) + 8, 44, 290);

        double uScale = Math.Clamp(scalePercent, 80, 300) / 100.0;
        if (chunks.Count == 0 && needs is null)
            return (int)Math.Round(72 * Math.Clamp(barBreadth / 50.0, 0.9, 1.85) * uScale);

        string timers = showTimer ? string.Join("|", chunks.Select(c => Digitless(TimerText(c)))) : "";

        if (lastExtent is { } last && ReferenceEquals(last.Reading, reading) && last.Vertical == vertical
            && last.Rows == maxRowsPerColumn && last.Breadth == barBreadth && last.Scale == scalePercent
            && last.ShowTimer == showTimer && last.Timers == timers)
            return last.Extent;

        int natural = Layout(measure, new Rectangle(0, 0, 4000, Math.Max(24, barBreadth)), chunks, needs, maxRowsPerColumn, showTimer, draw: false);

        // Scaled like the other strips, but never below what the words need: a timer cut off
        // mid-digit is worse than a strip a few pixels wider than its neighbours.
        int extent = (int)Math.Ceiling(natural * Math.Max(1.0, uScale));
        lastExtent = (reading, vertical, maxRowsPerColumn, barBreadth, scalePercent, showTimer, timers, extent);
        return extent;
    }

    /// <summary>Digits replaced by the widest one, so the strip does not twitch as the seconds go by.</summary>
    private static string Digitless(string text) => new(text.Select(ch => char.IsDigit(ch) ? '0' : ch).ToArray());

    public static void Paint(Graphics g, Rectangle area, OrchestraReading? reading, int maxRowsPerColumn = 3)
    {
        var chunks = OrchestraUsage.Chunks(reading?.Shown ?? [], Environment.TickCount64);
        var needs = NeedsLine(reading);
        if (chunks.Count == 0 && needs is null) { PaintWaitingForFeed(g, area); return; }

        float glow = OrchestraUsage.AttentionGlowIntensity;
        if (glow > 0.01f) UsageStrip.PaintRemoteChatsAttentionGlow(g, area, glow);

        bool showTimer = OrchestraUsage.ShowTimer;
        if (area.Height > area.Width)
            PaintVertical(g, area, chunks, needs, showTimer);
        else
            Layout(g, area, chunks, needs, maxRowsPerColumn, showTimer, draw: true);
    }

    /// <summary>
    /// Measures, and optionally draws, the horizontal strip; returns the width it wants. One chunk
    /// with room to spare gets two rows — repo (and optional timer) above, child counts below. More
    /// chunks, or a thin bar, get one row each, wrapping into columns past the row limit. The
    /// Needs row, when there is one, goes beneath them all and takes one of the rows.
    /// </summary>
    private static int Layout(Graphics g, Rectangle area, IReadOnlyList<OrchestraChunk> chunks,
                              (string Text, Color Colour)? needs, int maxRowsPerColumn, bool showTimer, bool draw)
    {
        int n = chunks.Count;
        int needRows = needs is null ? 0 : 1;
        int maxRows = Math.Clamp(maxRowsPerColumn, 1, 6);
        int vPad = area.Height >= 56 ? 6 : 4;
        bool twoRow = n > 0 && n * 2 + needRows <= maxRows && (area.Height - vPad) / (n * 2 + needRows) >= 11;
        int perCol = n == 0 ? 1 : twoRow ? n : Math.Min(Math.Max(1, maxRows - needRows), n);
        int cols = (int)Math.Ceiling(n / (double)perCol);
        int totalRows = (n == 0 ? 0 : twoRow ? n * 2 : perCol) + needRows;

        int rowH = Math.Clamp((area.Height - vPad) / totalRows, 8, 38);
        int top = area.Y + Math.Max(1, (area.Height - totalRows * rowH) / 2);
        float scale = Math.Clamp(rowH / 13.0f, 0.72f, 2.0f);
        int pad = Math.Clamp((int)Math.Round(5 * scale), 3, 10);
        int gap = Math.Clamp((int)Math.Round(7 * scale), 4, 14);
        int colGap = cols > 1 ? Math.Clamp((int)Math.Round(12 * scale), 8, 20) : 0;

        using Font titleFont = Theme.Face(Math.Clamp(7.2f * scale, 6.5f, 14f), FontStyle.Bold);
        using Font figureFont = Theme.Face(Math.Clamp(7.6f * scale, 6.8f, 14.5f), FontStyle.Bold);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };

        float Measure(string text, Font font) => text.Length == 0 ? 0f : g.MeasureString(text, font, PointF.Empty, format).Width;

        int diamond = (int)Math.Ceiling(Math.Clamp(3.5f * scale, 3.2f, 6.2f) * 2 + 3 * scale);
        int dot = Math.Clamp((int)Math.Round(rowH * 0.42), 5, 12);

        float TimerWidth(string timer) => Measure(Digitless(timer), figureFont);

        float CountsWidth(OrchestraChunk c)
        {
            if (!c.Active) return 0f;
            float w = 0f;
            foreach (var kind in c.Children)
            {
                if (w > 0) w += gap;
                w += dot + 2 + Measure(kind.Total.ToString(), figureFont);
            }
            return w;
        }

        float HeadWidth(OrchestraChunk c) => diamond + Measure(c.Title, titleFont)
            + (showTimer ? gap + TimerWidth(TimerText(c)) : 0f);

        float ChunkWidth(OrchestraChunk c) => twoRow
            ? Math.Max(HeadWidth(c), CountsWidth(c))
            : HeadWidth(c) + (c.Active && c.Children.Count > 0 ? gap + CountsWidth(c) : 0f);

        float NeedsWidth() => needs is { } line ? dot + 4 + Measure(line.Text, titleFont) : 0f;

        var colWidths = new int[cols];
        for (int i = 0; i < n; i++)
            colWidths[i / perCol] = Math.Max(colWidths[i / perCol], (int)Math.Ceiling(ChunkWidth(chunks[i])));

        int natural = pad * 2 + Math.Max(colWidths.Sum() + colGap * Math.Max(0, cols - 1), (int)Math.Ceiling(NeedsWidth()));
        if (!draw) return natural;

        // A strip given more room than it asked for — scaled, expanded or dragged wider — spreads
        // its columns across it rather than leaving the slack on one side.
        int spare = area.Width - natural;
        if (spare > 0)
            for (int c = 0; c < cols; c++) colWidths[c] += spare / cols;

        var smoothing = g.SmoothingMode;
        using var clip = g.Clip;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.SetClip(area);

        int x0 = area.X + pad;
        for (int c = 0; c < cols; c++)
        {
            if (c > 0)
            {
                int sepX = x0 - colGap / 2;
                using var sep = new Pen(Color.FromArgb(42, 46, 56), 1f);
                g.DrawLine(sep, sepX, top + 2, sepX, top + totalRows * rowH - 2);
            }

            for (int r = 0; r < perCol; r++)
            {
                int i = c * perCol + r;
                if (i >= n) break;
                var chunk = chunks[i];
                bool dim = chunk.Stale;

                int headY = top + (twoRow ? r * 2 : r) * rowH;
                var head = new Rectangle(x0, headY, colWidths[c], rowH);

                // Repo (or "Orch") and diamond.
                using (var title = new SolidBrush(Dim(TitleColour, dim)))
                {
                    float rad = Math.Clamp(3.5f * scale, 3.2f, 6.2f);
                    float cx = head.X + rad + 0.5f, cy = head.Y + head.Height / 2f;
                    g.FillPolygon(title, new PointF[] { new(cx, cy - rad), new(cx + rad, cy), new(cx, cy + rad), new(cx - rad, cy) });
                    g.DrawString(chunk.Title, titleFont, title, new RectangleF(head.X + diamond, head.Y, head.Width, head.Height), format);
                }

                float afterHeadX = head.X + diamond + Measure(chunk.Title, titleFont);
                if (showTimer)
                {
                    // Timer: right-aligned in two-row mode, straight after the title otherwise.
                    string timer = TimerText(chunk);
                    float timerW = TimerWidth(timer);
                    float timerX = twoRow
                        ? head.Right - timerW
                        : afterHeadX + gap;
                    using (var timerBrush = new SolidBrush(Dim(chunk.Active ? Theme.Text : Theme.Muted, dim)))
                        g.DrawString(timer, figureFont, timerBrush, new RectangleF(timerX, head.Y, head.Right - timerX + 40, head.Height), format);
                    afterHeadX = timerX + timerW;
                }

                if (!chunk.Active) continue;

                float countX = twoRow ? head.X : Math.Max(afterHeadX + gap, head.Right - CountsWidth(chunk));
                int countY = twoRow ? headY + rowH : headY;
                foreach (var kind in chunk.Children)
                {
                    var colour = Dim(kind.Total == 0 ? Theme.Muted : KindColour(kind.Tool), dim);

                    var dotBox = new RectangleF(countX, countY + (rowH - dot) / 2f, dot, dot);
                    using (var fill = new SolidBrush(colour)) g.FillEllipse(fill, dotBox);
                    if (kind.Waiting > 0)
                    {
                        using var ring = new Pen(Dim(WaitingColour, dim), Math.Max(1.4f, dot / 5f));
                        g.DrawEllipse(ring, RectangleF.Inflate(dotBox, 1.2f, 1.2f));
                    }
                    countX += dot + 2;

                    string count = kind.Total.ToString();
                    using (var countBrush = new SolidBrush(colour))
                        g.DrawString(count, figureFont, countBrush, new RectangleF(countX, countY, 400, rowH), format);
                    countX += Measure(count, figureFont) + gap;
                }
            }

            x0 += colWidths[c] + colGap;
        }

        if (needs is { } need)
        {
            int y = top + (totalRows - 1) * rowH;
            PaintNeedsMark(g, new RectangleF(area.X + pad, y + (rowH - dot) / 2f, dot, dot), need.Colour);
            using var brush = new SolidBrush(need.Colour);
            g.DrawString(need.Text, titleFont, brush, new RectangleF(area.X + pad + dot + 4, y, area.Width, rowH), format);
        }

        g.Clip = clip;
        g.SmoothingMode = smoothing;
        return natural;
    }

    /// <summary>
    /// A vertical bar is about 50 pixels across, so each chunk stacks: repo, optional timer, then
    /// the counts side by side in their colours.
    /// </summary>
    private static void PaintVertical(Graphics g, Rectangle area, IReadOnlyList<OrchestraChunk> chunks,
                                      (string Text, Color Colour)? needs, bool showTimer)
    {
        int minBlockH = showTimer ? 30 : 22;
        int needsH = needs is null ? 0 : 28;
        int blockH = chunks.Count == 0 ? 0 : Math.Max(minBlockH, (area.Height - 8 - needsH) / chunks.Count);
        using Font titleFont = Theme.Face(6.8f, FontStyle.Bold);
        using Font timerFont = Theme.Face(7.4f, FontStyle.Bold);
        using Font countFont = Theme.Face(7f, FontStyle.Bold);
        using var centred = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        using var near = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            bool dim = chunk.Stale;
            int y = area.Y + 4 + i * blockH;
            int lineH = blockH / (showTimer ? 3 : 2);
            var titleRow = new Rectangle(area.X, y, area.Width, lineH);
            var countRow = showTimer
                ? new Rectangle(area.X, y + 2 * lineH, area.Width, blockH - 2 * lineH)
                : new Rectangle(area.X, y + lineH, area.Width, blockH - lineH);

            using (var title = new SolidBrush(Dim(TitleColour, dim)))
                g.DrawString(chunk.Title, titleFont, title, titleRow, centred);

            if (showTimer)
            {
                var timerRow = new Rectangle(area.X, y + lineH, area.Width, lineH);
                string timer = !chunk.Active ? "idle" : chunk.Elapsed is { } t ? OrchestraUsage.FormatElapsedShort(t) : "—";
                using (var brush = new SolidBrush(Dim(chunk.Active ? Theme.Text : Theme.Muted, dim)))
                    g.DrawString(timer, timerFont, brush, timerRow, centred);
            }

            var shown = chunk.Active ? chunk.Children.Where(c => c.Total > 0).ToList() : [];
            if (shown.Count == 0) continue;

            // Centred as a group: each count in its own colour, so they are measured and placed one by one.
            const float space = 4f;
            var widths = shown.Select(c => g.MeasureString(c.Total.ToString(), countFont, PointF.Empty, near).Width).ToList();
            float x = countRow.X + (countRow.Width - (widths.Sum() + space * (shown.Count - 1))) / 2f;
            for (int k = 0; k < shown.Count; k++)
            {
                var kind = shown[k];
                var colour = Dim(kind.Waiting > 0 ? WaitingColour : KindColour(kind.Tool), dim);
                using var brush = new SolidBrush(colour);
                g.DrawString(kind.Total.ToString(), countFont, brush, new RectangleF(x, countRow.Y, widths[k] + 2, countRow.Height), near);
                x += widths[k] + space;
            }
        }

        if (needs is { } need)
        {
            // Just the count under the mark: "2 need you" does not fit across fifty pixels.
            int y = area.Y + 4 + chunks.Count * blockH;
            var open = OrchestraUsage.Current?.Outstanding ?? 0;
            const int dot = 9;
            PaintNeedsMark(g, new RectangleF(area.X + (area.Width - dot) / 2f, y + 2, dot, dot), need.Colour);
            using var brush = new SolidBrush(need.Colour);
            g.DrawString(open > 0 ? open.ToString() : "✓", countFont, brush, new Rectangle(area.X, y + dot + 3, area.Width, needsH - dot - 3), centred);
        }
    }

    /// <summary>A filled disc with an exclamation mark cut into it: the same sign wherever a need is counted.</summary>
    public static void PaintNeedsMark(Graphics g, RectangleF box, Color colour)
    {
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(colour)) g.FillEllipse(fill, box);
        using var ink = new SolidBrush(Theme.Panel);
        float w = Math.Max(1.4f, box.Width / 6f), cx = box.X + box.Width / 2f;
        g.FillRectangle(ink, cx - w / 2f, box.Y + box.Height * 0.2f, w, box.Height * 0.38f);
        g.FillEllipse(ink, cx - w / 2f, box.Y + box.Height * 0.66f, w, w);
        g.SmoothingMode = smoothing;
    }

    /// <summary>Only reached for a moment: the strip is hidden until some feed has answered.</summary>
    private static void PaintWaitingForFeed(Graphics g, Rectangle area)
    {
        using var font = Theme.Face(Math.Clamp(area.Height * 0.16f, 7.5f, 12f), FontStyle.Bold);
        using var brush = new SolidBrush(Theme.Muted);
        using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("Orch ···", font, brush, area, centred);
    }

    /// <summary>A stale reading is drawn the way a disabled control is: there, but not to be trusted.</summary>
    private static Color Dim(Color c, bool dim) => dim ? Color.FromArgb(110, c) : c;

    // ---- tooltip -------------------------------------------------------------

    public static string Tip(OrchestraReading? reading)
    {
        if (!OrchestraUsage.Enabled) return "Orchestrator — switched off (Settings ▸ Orchestrator)";
        var feeds = reading?.Shown ?? [];
        if (feeds.Count == 0) return "Orchestrator — no feed has answered yet";

        long now = Environment.TickCount64;
        var text = new StringBuilder();
        foreach (var f in feeds)
        {
            if (text.Length > 0) text.AppendLine().AppendLine();
            text.Append(Describe(f, now));
        }

        var needs = reading?.Needs ?? [];
        if (needs.Count > 0)
        {
            text.AppendLine().AppendLine().Append("Needs you:");
            foreach (var item in needs)
            {
                text.AppendLine().Append(item.Need.Blocking ? "  ⚠ " : "  • ")
                    .Append($"{OrchestraUsage.NeedKindLabel(item.Need.Kind)}: {item.Need.Title}");
                if (item.Done) text.Append(" (done, confirming)");
            }
            text.AppendLine().AppendLine().Append(OrchestraUsage.Current?.Outstanding == 1
                ? "Click to go straight to it; right-click for the list."
                : "Click or right-click for the list.");
        }
        else
        {
            text.AppendLine().AppendLine().Append("Click for orchestrator settings.");
        }
        return text.ToString();
    }

    /// <summary>One feed in words: the figures the strip has no room for.</summary>
    public static string Describe(OrchestraFeed f, long now)
    {
        var text = new StringBuilder($"Orchestrator on {f.Server}");
        if (!f.Answered)
        {
            text.Append(f.Error is { } never ? $" — not answering ({never})" : " — asking…");
            return text.ToString();
        }

        if (!f.Active)
            text.Append(" — idle, no unfinished run");
        else if (f.Elapsed(now) is { } t)
            text.Append($" — running {OrchestraUsage.FormatElapsed(t)}")
                .Append(f.StartedAt is { } s ? $" (since {s.ToLocalTime():ddd HH:mm})" : "");

        if (f.Active)
        {
            foreach (var c in f.Children)
                text.AppendLine().Append($"  {OrchestraUsage.KindLabel(c.Tool)}: {Breakdown(c)}");

            if (f.Caps.Count > 0)
                text.AppendLine().Append("  Caps: ").Append(string.Join(" · ", f.Caps.Select(c => $"{c.Tool} {c.Cap}")));

            foreach (var run in f.Runs)
            {
                text.AppendLine().Append($"  ▸ {run.Name}");
                string where = (run.Repo, run.Base) switch
                {
                    ({ } repo, { } b) => $" ({repo} @ {b})",
                    ({ } repo, null) => $" ({repo})",
                    (null, { } b) => $" (@ {b})",
                    _ => "",
                };
                text.Append(where);
                if (f.ElapsedFor(run, now) is { } rt) text.Append($" — {OrchestraUsage.FormatElapsed(rt)}");
                var kinds = run.Children.Where(c => c.Total > 0).ToList();
                if (kinds.Count > 0)
                    text.AppendLine().Append("      ").Append(string.Join("; ",
                        kinds.Select(c => $"{OrchestraUsage.KindLabel(c.Tool)} {Breakdown(c)}")));
            }
        }

        if (f.Stale)
        {
            long seconds = Math.Max(0, now - f.LastAnswerTick) / 1000;
            text.AppendLine().Append($"  ⚠ Not answering for {seconds} s")
                .Append(f.Error is { } e ? $" ({e})" : "")
                .Append(" — figures may be out of date");
        }

        return text.ToString();
    }

    private static string Breakdown(OrchestraToolCount c)
    {
        var parts = new List<string>(3);
        if (c.Running > 0) parts.Add($"{c.Running} running");
        if (c.Waiting > 0) parts.Add($"{c.Waiting} waiting");
        if (c.Idle > 0) parts.Add($"{c.Idle} idle");
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }
}
