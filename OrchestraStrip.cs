using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;

namespace ScweenSpit;

/// <summary>
/// Draws the orchestrator strip: for each feed that has answered, how long the orchestrator has
/// been running and how many children of each kind it has, e.g. "◆ Orch 3:12:05 · ● Claude 3 · ● agy 1".
///
/// The timer is drawn from the local clock on every repaint, so it ticks with the bar's own
/// one-second refresh rather than with the feed's poll.
/// </summary>
internal static class OrchestraStrip
{
    private static readonly Color TitleColour = Color.FromArgb(226, 204, 132);
    private static readonly Color ClaudeColour = Color.FromArgb(130, 185, 250);
    private static readonly Color AgyColour = Color.FromArgb(196, 160, 255);
    private static readonly Color AgyIdeColour = Color.FromArgb(232, 160, 232);
    private static readonly Color WaitingColour = Color.FromArgb(255, 158, 24);
    private static readonly Color CountColour = Color.FromArgb(214, 220, 232);

    private static Color KindColour(string tool) => tool.ToLowerInvariant() switch
    {
        "claude" => ClaudeColour,
        "agy" => AgyColour,
        "agy-ide" => AgyIdeColour,
        _ => Theme.Muted,
    };

    // The bar asks for its extents many times per paint and per mouse move, and measuring text
    // needs a Graphics. Both are kept: the answer only changes with the reading, the bar's shape,
    // or the timer growing a digit. UI thread only, like every caller.
    private static readonly Bitmap measureSurface = new(1, 1);
    private static readonly Graphics measure = Graphics.FromImage(measureSurface);
    private static (OrchestraReading? Reading, bool Vertical, int Rows, int Breadth, int Scale, string Timers, int Extent)? lastExtent;

    /// <summary>Room the strip needs along the bar, sized to what it currently has to say.</summary>
    public static int Extent(bool vertical, int maxRowsPerColumn = 3, int barBreadth = 50, int scalePercent = 100)
    {
        var reading = OrchestraUsage.Current;
        var feeds = reading?.Shown ?? [];
        if (vertical) return Math.Clamp(Math.Max(1, feeds.Count) * 30 + 10, 40, 200);

        double uScale = Math.Clamp(scalePercent, 80, 300) / 100.0;
        if (feeds.Count == 0)
            return (int)Math.Round(72 * Math.Clamp(barBreadth / 50.0, 0.9, 1.85) * uScale);

        long now = Environment.TickCount64;
        string timers = string.Join("|", feeds.Select(f => f.Active && f.Elapsed(now) is { } t
            ? new string(OrchestraUsage.FormatElapsed(t).Select(ch => char.IsDigit(ch) ? '0' : ch).ToArray())
            : f.Active.ToString()));

        if (lastExtent is { } last && ReferenceEquals(last.Reading, reading) && last.Vertical == vertical
            && last.Rows == maxRowsPerColumn && last.Breadth == barBreadth && last.Scale == scalePercent
            && last.Timers == timers)
            return last.Extent;

        int natural = Layout(measure, new Rectangle(0, 0, 4000, Math.Max(24, barBreadth)), feeds, maxRowsPerColumn,
                             now, draw: false, shortLabels: false);

        // Scaled like the other strips, but never below what the words need: a timer cut off
        // mid-digit is worse than a strip a few pixels wider than its neighbours.
        int extent = (int)Math.Ceiling(natural * Math.Max(1.0, uScale));
        lastExtent = (reading, vertical, maxRowsPerColumn, barBreadth, scalePercent, timers, extent);
        return extent;
    }

    public static void Paint(Graphics g, Rectangle area, OrchestraReading? reading, int maxRowsPerColumn = 3)
    {
        var feeds = reading?.Shown ?? [];
        if (feeds.Count == 0) { PaintWaitingForFeed(g, area); return; }

        float glow = OrchestraUsage.AttentionGlowIntensity;
        if (glow > 0.01f) UsageStrip.PaintRemoteChatsAttentionGlow(g, area, glow);

        long now = Environment.TickCount64;
        if (area.Height > area.Width)
        {
            PaintVertical(g, area, feeds, now);
            return;
        }

        // Long names when they fit, "Cl"/"ag" when the strip has been dragged narrower than that.
        bool shortLabels = Layout(g, area, feeds, maxRowsPerColumn, now, draw: false, shortLabels: false) > area.Width;
        Layout(g, area, feeds, maxRowsPerColumn, now, draw: true, shortLabels);
    }

    /// <summary>
    /// Measures, and optionally draws, the horizontal strip; returns the width it wants. One feed
    /// with room to spare gets two rows — title and timer above, child counts below. More feeds, or
    /// a thin bar, get one row each, wrapping into columns past the row limit.
    /// </summary>
    private static int Layout(Graphics g, Rectangle area, IReadOnlyList<OrchestraFeed> feeds, int maxRowsPerColumn,
                              long now, bool draw, bool shortLabels)
    {
        int n = feeds.Count;
        int maxRows = Math.Clamp(maxRowsPerColumn, 1, 6);
        int vPad = area.Height >= 56 ? 6 : 4;
        bool twoRow = n * 2 <= maxRows && (area.Height - vPad) / (n * 2) >= 11;
        int perCol = twoRow ? n : Math.Min(maxRows, n);
        int cols = (int)Math.Ceiling(n / (double)perCol);
        int totalRows = twoRow ? n * 2 : perCol;

        int rowH = Math.Clamp((area.Height - vPad) / totalRows, 8, 38);
        int top = area.Y + Math.Max(1, (area.Height - totalRows * rowH) / 2);
        float scale = Math.Clamp(rowH / 13.0f, 0.72f, 2.0f);
        int pad = Math.Clamp((int)Math.Round(5 * scale), 3, 10);
        int gap = Math.Clamp((int)Math.Round(7 * scale), 4, 14);
        int colGap = cols > 1 ? Math.Clamp((int)Math.Round(12 * scale), 8, 20) : 0;

        using Font titleFont = Theme.Face(Math.Clamp(7.2f * scale, 6.5f, 14f), FontStyle.Bold);
        using Font labelFont = Theme.Face(Math.Clamp(7.2f * scale, 6.5f, 14f));
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

        string TitleFor(OrchestraFeed f) => n == 1 ? "Orch" : RemoteServerRowConfig.ShortServerName(f.Server);

        string TimerFor(OrchestraFeed f) =>
            !f.Active ? "idle" : f.Elapsed(now) is { } t ? OrchestraUsage.FormatElapsed(t) : "—";

        // Digits replaced by the widest one, so the strip does not twitch as the seconds go by.
        float TimerWidth(string timer) =>
            Measure(new string(timer.Select(ch => char.IsDigit(ch) ? '0' : ch).ToArray()), figureFont);

        float ChipsWidth(OrchestraFeed f)
        {
            if (!f.Active) return 0f;
            float w = 0f;
            foreach (var kind in f.Children)
            {
                if (w > 0) w += gap;
                string label = shortLabels ? OrchestraUsage.KindShortLabel(kind.Tool) : OrchestraUsage.KindLabel(kind.Tool);
                w += dot + 3 + Measure(label, labelFont) + 2 + Measure(kind.Total.ToString(), figureFont);
            }
            return w;
        }

        float HeadWidth(OrchestraFeed f) => diamond + Measure(TitleFor(f), titleFont) + gap + TimerWidth(TimerFor(f));

        float FeedWidth(OrchestraFeed f) => twoRow
            ? Math.Max(HeadWidth(f), ChipsWidth(f))
            : HeadWidth(f) + (f.Active && f.Children.Count > 0 ? gap + ChipsWidth(f) : 0f);

        var colWidths = new int[cols];
        for (int i = 0; i < n; i++)
            colWidths[i / perCol] = Math.Max(colWidths[i / perCol], (int)Math.Ceiling(FeedWidth(feeds[i])));

        int natural = pad * 2 + colWidths.Sum() + colGap * (cols - 1);
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
                var f = feeds[i];
                bool dim = f.Stale;

                int headY = top + (twoRow ? r * 2 : r) * rowH;
                var head = new Rectangle(x0, headY, colWidths[c], rowH);

                // Title and diamond.
                using (var title = new SolidBrush(Dim(TitleColour, dim)))
                {
                    float rad = Math.Clamp(3.5f * scale, 3.2f, 6.2f);
                    float cx = head.X + rad + 0.5f, cy = head.Y + head.Height / 2f;
                    g.FillPolygon(title, new PointF[] { new(cx, cy - rad), new(cx + rad, cy), new(cx, cy + rad), new(cx - rad, cy) });
                    g.DrawString(TitleFor(f), titleFont, title, new RectangleF(head.X + diamond, head.Y, head.Width, head.Height), format);
                }

                // Timer: right-aligned in two-row mode, straight after the title otherwise.
                string timer = TimerFor(f);
                float timerX = twoRow
                    ? head.Right - TimerWidth(timer)
                    : head.X + diamond + Measure(TitleFor(f), titleFont) + gap;
                using (var timerBrush = new SolidBrush(Dim(f.Active ? Theme.Text : Theme.Muted, dim)))
                    g.DrawString(timer, figureFont, timerBrush, new RectangleF(timerX, head.Y, head.Right - timerX + 40, head.Height), format);

                if (!f.Active) continue;

                float chipX = twoRow ? head.X : Math.Max(timerX + TimerWidth(timer) + gap, head.Right - ChipsWidth(f));
                int chipY = twoRow ? headY + rowH : headY;
                foreach (var kind in f.Children)
                {
                    bool waiting = kind.Waiting > 0;
                    bool none = kind.Total == 0;
                    var kindColour = Dim(none ? Theme.Muted : KindColour(kind.Tool), dim);

                    var dotBox = new RectangleF(chipX, chipY + (rowH - dot) / 2f, dot, dot);
                    using (var fill = new SolidBrush(kindColour)) g.FillEllipse(fill, dotBox);
                    if (waiting)
                    {
                        using var ring = new Pen(Dim(WaitingColour, dim), Math.Max(1.4f, dot / 5f));
                        g.DrawEllipse(ring, RectangleF.Inflate(dotBox, 1.2f, 1.2f));
                    }
                    chipX += dot + 3;

                    string label = shortLabels ? OrchestraUsage.KindShortLabel(kind.Tool) : OrchestraUsage.KindLabel(kind.Tool);
                    using (var labelBrush = new SolidBrush(Dim(Theme.Muted, dim)))
                        g.DrawString(label, labelFont, labelBrush, new RectangleF(chipX, chipY, 400, rowH), format);
                    chipX += Measure(label, labelFont) + 2;

                    string count = kind.Total.ToString();
                    using (var countBrush = new SolidBrush(Dim(waiting ? WaitingColour : none ? Theme.Muted : CountColour, dim)))
                        g.DrawString(count, figureFont, countBrush, new RectangleF(chipX, chipY, 400, rowH), format);
                    chipX += Measure(count, figureFont) + gap;
                }
            }

            x0 += colWidths[c] + colGap;
        }

        g.Clip = clip;
        g.SmoothingMode = smoothing;
        return natural;
    }

    /// <summary>A vertical bar is about 50 pixels across: the timer, and the counts in short form beneath it.</summary>
    private static void PaintVertical(Graphics g, Rectangle area, IReadOnlyList<OrchestraFeed> feeds, long now)
    {
        int blockH = Math.Max(20, (area.Height - 8) / feeds.Count);
        using var timerFont = Theme.Face(7.4f, FontStyle.Bold);
        using var countFont = Theme.Face(6.5f);
        using var centred = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        for (int i = 0; i < feeds.Count; i++)
        {
            var f = feeds[i];
            bool dim = f.Stale;
            int y = area.Y + 4 + i * blockH;
            var upper = new Rectangle(area.X, y, area.Width, blockH / 2);
            var lower = new Rectangle(area.X, y + blockH / 2, area.Width, blockH - blockH / 2);

            string timer = !f.Active ? "idle" : f.Elapsed(now) is { } t ? OrchestraUsage.FormatElapsedShort(t) : "—";
            using (var brush = new SolidBrush(Dim(f.Active ? TitleColour : Theme.Muted, dim)))
                g.DrawString(timer, timerFont, brush, upper, centred);

            if (!f.Active || f.Children.Count == 0) continue;
            string counts = string.Join(" ", f.Children.Where(c => c.Total > 0)
                                                       .Select(c => OrchestraUsage.KindShortLabel(c.Tool) + c.Total));
            if (counts.Length == 0) counts = "0";
            using var countBrush = new SolidBrush(Dim(f.Waiting > 0 ? WaitingColour : CountColour, dim));
            g.DrawString(counts, countFont, countBrush, lower, centred);
        }
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
        text.AppendLine().AppendLine().Append("Click for orchestrator settings.");
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
