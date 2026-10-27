using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;

namespace ScweenSpit;

/// <summary>
/// Draws the Claude and Antigravity usage limits as stacks of thin bars inside the status cluster.
///
/// The palette thresholds (amber at 50%, red at 85%) come from claude-usage-widget by Niccolò
/// Sabato (MIT — see THIRD-PARTY-NOTICES.md), while Claude and Antigravity use distinct normal
/// (<50%) colours and explicit service headers/labels so the two strips are immediately
/// distinguishable side by side.
/// </summary>
internal static class UsageStrip
{
    internal static readonly Color ClaudeTitleColour = Color.FromArgb(130, 185, 250);
    internal static readonly Color AntigravityTitleColour = Color.FromArgb(196, 160, 255);
    private static readonly Color BambuTitleColour = Color.FromArgb(120, 232, 160);
    private static readonly Color RemoteChatsTitleColour = Color.FromArgb(145, 215, 255);

    /// <summary>Room the Claude strip needs along the bar (1 column: paired 5h/7d row plus any optional model row).</summary>
    public static int Extent(bool vertical, int maxRowsPerColumn = 3, int barBreadth = 50, int scalePercent = 100)
    {
        int rowCount = GetClaudeRowCount(ClaudeUsage.Current?.Limits);
        return MeterStrip.UsageExtent(vertical, rowCount, maxRowsPerColumn, barBreadth, scalePercent);
    }

    /// <summary>Room the Antigravity strip needs along the bar (1 column: IDE 5h/7d row + CLI 5h/7d row).</summary>
    public static int AntigravityExtent(bool vertical, int maxRowsPerColumn = 3, int barBreadth = 50, int scalePercent = 100)
    {
        int rowCount = GetAntigravityRowCount(AntigravityUsage.Current?.Limits);
        return MeterStrip.UsageExtent(vertical, rowCount, maxRowsPerColumn, barBreadth, scalePercent);
    }

    private static int GetClaudeRowCount(IReadOnlyList<UsageLimit>? limits)
    {
        if (limits is null || limits.Count == 0) return 1;
        bool hasSessionOrWeekly = limits.Any(l =>
            l.Label.Equals("Session", StringComparison.OrdinalIgnoreCase) ||
            l.Label.Equals("Weekly", StringComparison.OrdinalIgnoreCase));
        int extra = limits.Count(l =>
            !l.Label.Equals("Session", StringComparison.OrdinalIgnoreCase) &&
            !l.Label.Equals("Weekly", StringComparison.OrdinalIgnoreCase));
        return Math.Max(1, (hasSessionOrWeekly ? 1 : 0) + extra);
    }

    private static int GetAntigravityRowCount(IReadOnlyList<UsageLimit>? limits)
    {
        if (limits is null || limits.Count == 0) return 2;
        bool hasIde = limits.Any(l => l.Label.StartsWith("IDE", StringComparison.OrdinalIgnoreCase));
        bool hasCli = limits.Any(l => l.Label.StartsWith("CLI", StringComparison.OrdinalIgnoreCase));
        if (hasIde || hasCli) return (hasIde ? 1 : 0) + (hasCli ? 1 : 0);
        return Math.Min(2, limits.Count);
    }

    private static bool IsActiveBambuPrinter(BambuPrinterReading p) =>
        p.State is BambuPrinterState.Printing or BambuPrinterState.Preparing
                or BambuPrinterState.Paused or BambuPrinterState.Error;

    public static bool HasActiveBambuPrinters =>
        BambuUsage.Current?.Printers is { Count: > 0 } printers && printers.Any(IsActiveBambuPrinter);

    /// <summary>Room the Bambu printer strip needs along the bar (compact when all idle; expanded for active printers).</summary>
    public static int BambuExtent(bool vertical, int maxRowsPerColumn = 3, int barBreadth = 50, int scalePercent = 100)
    {
        var printers = BambuUsage.Current?.Printers;
        int activeCount = printers is { Count: > 0 } ? printers.Count(IsActiveBambuPrinter) : 0;
        if (activeCount == 0)
        {
            if (vertical) return 68;
            double hScale = Math.Clamp(barBreadth / 50.0, 0.9, 1.85);
            double uScale = Math.Clamp(scalePercent, 80, 300) / 100.0;
            return (int)Math.Round(96 * hScale * uScale);
        }

        return MeterStrip.BambuExtent(vertical, activeCount, maxRowsPerColumn, barBreadth, scalePercent);
    }

    /// <summary>Room a remote server chats strip needs along the bar, sized tightly to the live chat count.</summary>
    public static int RemoteChatsExtent(
        bool vertical,
        int maxRowsPerColumn = 3,
        int barBreadth = 50,
        int scalePercent = 100,
        RemoteChatService? serviceFilter = null)
    {
        IReadOnlyList<RemoteServerRow> allRows = RemoteChatUsage.Current?.Rows ?? [];
        IReadOnlyList<RemoteServerRow> rows = serviceFilter is { } svc
            ? allRows.Where(r => r.Service == svc).ToList()
            : allRows;

        if (vertical)
        {
            int count = rows.Count > 0
                ? rows.Count
                : serviceFilter is { } f ? RemoteChatUsage.ConfiguredRowCountFor(f) : RemoteChatUsage.ConfiguredRowCount;
            return Math.Clamp(Math.Max(2, count) * 24 + 8, 72, 160);
        }

        double uScale = Math.Clamp(scalePercent, 80, 300) / 100.0;
        if (rows.Count == 0)
        {
            double hScale = Math.Clamp(barBreadth / 50.0, 0.9, 1.85);
            return (int)Math.Round((serviceFilter is null ? 108 : 88) * hScale * uScale);
        }

        int virtualH = Math.Max(24, barBreadth);
        RenderOrHitTestRemoteChats(
            g: null,
            new Rectangle(0, 0, 2000, virtualH),
            rows,
            maxRowsPerColumn,
            hoveredChatId: null,
            stripHovered: false,
            hitPoint: null,
            serviceFilter,
            out _,
            out int intrinsicWidth);

        return (int)Math.Ceiling(intrinsicWidth * uScale);
    }

    public static void Paint(
        Graphics g,
        Rectangle area,
        UsageReading? reading,
        int maxRowsPerColumn = 3,
        bool showRemoteChats = false,
        string? hoveredChatId = null)
    {
        var limits = reading?.Limits ?? [];
        if (limits.Count == 0) { PaintState(g, area, reading, isAntigravity: false); return; }

        if (showRemoteChats && RemoteChatUsage.Enabled)
        {
            float glow = RemoteChatUsage.GetAttentionGlowIntensity(RemoteChatService.Claude);
            if (glow > 0.01f)
                PaintRemoteChatsAttentionGlow(g, area, glow);
        }

        var meters = BuildClaudeMeters(limits);

        var stripChats = showRemoteChats && RemoteChatUsage.Enabled
            ? RemoteChatUsage.GetStripChatsForService(RemoteChatService.Claude)
            : [];
        bool hasWaiting = stripChats.Any(c => c.State == RemoteChatState.Waiting);
        string hdrTitle = GetClaudeHeaderTitle(area.Width, stripChats.Count);

        MeterStrip.Paint(
            g, area, meters,
            lowColour: MeterStrip.Low,
            headerTitle: hdrTitle,
            headerTag: null,
            headerColour: hasWaiting ? Color.FromArgb(255, 210, 115) : ClaudeTitleColour,
            sparkleGlyph: false,
            maxRowsPerColumn: maxRowsPerColumn,
            paintHeaderRight: stripChats.Count > 0
                ? (hg, rightBox, hdrScale) => PaintOrHitTestHeaderCapsule(hg, rightBox, stripChats, hoveredChatId, null, hdrScale, out _)
                : null);
    }

    private static string GetClaudeHeaderTitle(int stripWidth, int chatCount) =>
        (chatCount >= 4 && stripWidth < 165) || (chatCount > 0 && stripWidth < 130) ? "Cl" : "Claude";

    private static List<Meter> BuildClaudeMeters(IReadOnlyList<UsageLimit> limits)
    {
        var session = limits.FirstOrDefault(l => l.Label.Equals("Session", StringComparison.OrdinalIgnoreCase));
        var weekly = limits.FirstOrDefault(l => l.Label.Equals("Weekly", StringComparison.OrdinalIgnoreCase));
        var extras = limits.Where(l =>
            !l.Label.Equals("Session", StringComparison.OrdinalIgnoreCase) &&
            !l.Label.Equals("Weekly", StringComparison.OrdinalIgnoreCase)).ToList();

        var meters = new List<Meter>(2);
        if (session is not null || weekly is not null)
        {
            var p1 = session ?? weekly!;
            var p2 = session is not null ? weekly : null;
            string p1Tag = session is not null ? "5h" : "7d";
            string detail = p2 is not null
                ? $"Session (5h): {p1.Percent}% · Weekly (7d): {p2.Percent}%"
                : $"{p1.Label}: {p1.Percent}%";

            meters.Add(new Meter(
                p1Tag,
                p1.Percent,
                detail,
                Subtext: ClaudeUsage.ShortCountdown(p1.ResetsAt),
                FigureText: $"{p1.Percent}%",
                SecondPercent: p2?.Percent,
                SecondFigureText: p2 is not null ? $"{p2.Percent}%" : null,
                SecondLabel: p2 is not null ? "7d" : null,
                SecondSubtext: p2 is not null ? ClaudeUsage.ShortCountdown(p2.ResetsAt) : null));
        }

        foreach (var l in extras)
        {
            string detail = ClaudeUsage.Countdown(l.ResetsAt) is { Length: > 0 } left
                ? $"{l.Label}: {l.Percent}%  ({left})"
                : $"{l.Label}: {l.Percent}%";

            meters.Add(new Meter(
                l.Label,
                l.Percent,
                detail,
                Subtext: ClaudeUsage.ShortCountdown(l.ResetsAt)));
        }

        return meters;
    }

    public static void PaintAntigravity(
        Graphics g,
        Rectangle area,
        UsageReading? reading,
        int maxRowsPerColumn = 3,
        bool showRemoteChats = false,
        string? hoveredChatId = null)
    {
        var limits = reading?.Limits ?? [];
        if (limits.Count == 0) { PaintState(g, area, reading, isAntigravity: true); return; }

        if (showRemoteChats && RemoteChatUsage.Enabled)
        {
            float glow = RemoteChatUsage.GetAttentionGlowIntensity(RemoteChatService.Antigravity);
            if (glow > 0.01f)
                PaintRemoteChatsAttentionGlow(g, area, glow);
        }

        bool vertical = area.Height > area.Width;
        var meters = BuildAntigravityMeters(limits, vertical, maxRowsPerColumn, area.Height);

        var stripChats = showRemoteChats && RemoteChatUsage.Enabled
            ? RemoteChatUsage.GetStripChatsForService(RemoteChatService.Antigravity)
            : [];
        bool hasWaiting = stripChats.Any(c => c.State == RemoteChatState.Waiting);
        string hdrTitle = GetAntigravityHeaderTitle(area.Width, stripChats.Count);

        MeterStrip.Paint(
            g, area, meters,
            lowColour: MeterStrip.VioletLow,
            headerTitle: hdrTitle,
            headerTag: null,
            headerColour: hasWaiting ? Color.FromArgb(255, 210, 115) : AntigravityTitleColour,
            sparkleGlyph: true,
            maxRowsPerColumn: maxRowsPerColumn,
            paintHeaderRight: stripChats.Count > 0
                ? (hg, rightBox, hdrScale) => PaintOrHitTestHeaderCapsule(hg, rightBox, stripChats, hoveredChatId, null, hdrScale, out _)
                : null);
    }

    private static string GetAntigravityHeaderTitle(int stripWidth, int chatCount) =>
        (chatCount >= 3 || (chatCount > 0 && stripWidth < 185)) ? "AG" : "Antigravity";

    private static List<Meter> BuildAntigravityMeters(IReadOnlyList<UsageLimit> limits, bool vertical, int maxRowsPerColumn, int areaHeight)
    {
        var ide5h = limits.FirstOrDefault(l => l.Label.Equals("IDE 5h", StringComparison.OrdinalIgnoreCase));
        var ide7d = limits.FirstOrDefault(l => l.Label.Equals("IDE 7d", StringComparison.OrdinalIgnoreCase));
        var cli5h = limits.FirstOrDefault(l => l.Label.Equals("CLI 5h", StringComparison.OrdinalIgnoreCase));
        var cli7d = limits.FirstOrDefault(l => l.Label.Equals("CLI 7d", StringComparison.OrdinalIgnoreCase));

        if (ide5h is not null || ide7d is not null || cli5h is not null || cli7d is not null)
        {
            var paired = new List<Meter>(2);
            if (ide5h is not null || ide7d is not null)
            {
                var p1 = ide5h ?? ide7d!;
                var p2 = ide5h is not null ? ide7d : null;
                paired.Add(new Meter(
                    "IDE",
                    p1.Percent,
                    $"IDE: {p1.Percent}% 5h" + (p2 is not null ? $" · {p2.Percent}% 7d" : ""),
                    Subtext: ClaudeUsage.ShortCountdown(p1.ResetsAt),
                    FigureText: $"{p1.Percent}%",
                    SecondPercent: p2?.Percent,
                    SecondFigureText: p2 is not null ? $"{p2.Percent}%" : null,
                    SecondSubtext: p2 is not null ? ClaudeUsage.ShortCountdown(p2.ResetsAt) : null));
            }
            if (cli5h is not null || cli7d is not null)
            {
                var p1 = cli5h ?? cli7d!;
                var p2 = cli5h is not null ? cli7d : null;
                paired.Add(new Meter(
                    "CLI",
                    p1.Percent,
                    $"CLI: {p1.Percent}% 5h" + (p2 is not null ? $" · {p2.Percent}% 7d" : ""),
                    Subtext: ClaudeUsage.ShortCountdown(p1.ResetsAt),
                    FigureText: $"{p1.Percent}%",
                    SecondPercent: p2?.Percent,
                    SecondFigureText: p2 is not null ? $"{p2.Percent}%" : null,
                    SecondSubtext: p2 is not null ? ClaudeUsage.ShortCountdown(p2.ResetsAt) : null));
            }
            return paired;
        }

        int maxRows = Math.Clamp(maxRowsPerColumn, 1, 8);
        int cols = MeterStrip.ColumnsFor(vertical, limits.Count, maxRows);
        int rowsPerCol = (int)Math.Ceiling(limits.Count / (double)cols);
        bool useHeaderRow = !vertical && (rowsPerCol + 1 <= maxRows) && (areaHeight - 2) / (rowsPerCol + 1) >= 11;

        var meters = new List<Meter>(limits.Count);
        for (int i = 0; i < limits.Count; i++)
        {
            var l = limits[i];
            string displayLabel = vertical || !useHeaderRow
                ? (l.Label.Equals("Session", StringComparison.OrdinalIgnoreCase) ? "AG 5h"
                 : l.Label.Equals("Weekly", StringComparison.OrdinalIgnoreCase) ? "AG 7d"
                 : l.Label)
                : (l.Label.Equals("Session", StringComparison.OrdinalIgnoreCase) ? "5h"
                 : l.Label.Equals("Weekly", StringComparison.OrdinalIgnoreCase) ? "7d"
                 : l.Label);

            bool highlightFirst = !vertical && !useHeaderRow && i == 0;
            string detail = ClaudeUsage.Countdown(l.ResetsAt) is { Length: > 0 } left
                ? $"{l.Label}: {l.Percent}%  ({left})"
                : $"{l.Label}: {l.Percent}%";

            meters.Add(new Meter(
                displayLabel,
                l.Percent,
                detail,
                LabelColour: highlightFirst ? AntigravityTitleColour : null,
                BoldLabel: highlightFirst,
                Subtext: ClaudeUsage.ShortCountdown(l.ResetsAt)));
        }
        return meters;
    }

    public static void PaintBambu(Graphics g, Rectangle area, BambuReading? reading, int maxRowsPerColumn = 3)
    {
        var printers = reading?.Printers ?? [];
        if (printers.Count == 0) { PaintBambuState(g, area, reading); return; }

        var activePrinters = printers.Where(IsActiveBambuPrinter).ToList();
        if (activePrinters.Count == 0)
        {
            PaintBambuCompactIdle(g, area, printers);
            return;
        }

        var meters = new List<Meter>(activePrinters.Count);
        foreach (var p in activePrinters)
        {
            string rem = BambuUsage.FormatRemaining(p.RemainingMinutes);
            string detail = $"{p.Label}: {BambuUsage.DescribePrinterState(p)}";

            var (pct, barCol, lblCol, bold, figText, subText, subCol) = p.State switch
            {
                BambuPrinterState.Printing => (
                    Math.Clamp(p.Percent, 1, 100),
                    MeterStrip.BambuLow,
                    (Color?)BambuTitleColour,
                    true,
                    $"{p.Percent}%",
                    rem.Length > 0 ? rem : "Run",
                    (Color?)null),

                BambuPrinterState.Preparing => (
                    Math.Clamp(p.Percent, 1, 100),
                    MeterStrip.BambuLow,
                    (Color?)BambuTitleColour,
                    true,
                    p.Percent > 0 ? $"{p.Percent}%" : "Prep",
                    rem.Length > 0 ? rem : "Prep",
                    (Color?)null),

                BambuPrinterState.Error => (
                    Math.Clamp(p.Percent > 0 ? p.Percent : 100, 12, 100),
                    MeterStrip.High,
                    (Color?)Color.FromArgb(255, 130, 130),
                    true,
                    p.Percent > 0 ? $"{p.Percent}%" : "ERR",
                    "ERR",
                    (Color?)Color.FromArgb(255, 120, 120)),

                _ => (
                    Math.Clamp(p.Percent, 8, 100),
                    MeterStrip.Mid,
                    (Color?)Color.FromArgb(250, 205, 110),
                    true,
                    $"{p.Percent}%",
                    "Pause",
                    (Color?)MeterStrip.Mid),
            };

            meters.Add(new Meter(
                p.Label,
                pct,
                detail,
                LabelColour: lblCol,
                BoldLabel: bold,
                Subtext: subText,
                BarColour: barCol,
                FigureText: figText,
                SubtextColour: subCol));
        }

        MeterStrip.Paint(
            g, area, meters,
            lowColour: MeterStrip.BambuLow,
            headerTitle: "Bambu Lab",
            headerTag: $"{activePrinters.Count}/{printers.Count}",
            headerColour: BambuTitleColour,
            sparkleGlyph: false,
            maxRowsPerColumn: maxRowsPerColumn);
    }

    private static void PaintBambuCompactIdle(Graphics g, Rectangle area, IReadOnlyList<BambuPrinterReading> printers)
    {
        bool vertical = area.Height > area.Width;
        int totalRows = 2;
        int vPad = area.Height >= 56 ? 8 : 6;
        int rowHeight = Math.Clamp((area.Height - vPad) / totalRows, 10, 26);
        int top = area.Y + Math.Max(1, (area.Height - totalRows * rowHeight) / 2);
        float scale = vertical ? 1f : Math.Clamp(rowHeight / 13.0f, 0.78f, 1.75f);
        int outerPad = vertical ? 3 : Math.Clamp((int)Math.Round(5 * scale), 4, 8);

        using var boldFont = Theme.Face(Math.Clamp(7.2f * scale, 6.5f, 13f), FontStyle.Bold);
        using var tagFont = Theme.Face(Math.Clamp(6.8f * scale, 6.2f, 12f), FontStyle.Bold);
        using var mutedBrush = new SolidBrush(Theme.Muted);

        var hdrRow = new Rectangle(area.X + outerPad, top, Math.Max(12, area.Width - outerPad * 2), rowHeight);
        PaintRemoteHeaderRow(g, hdrRow, vertical ? "Bambu" : "Bambu", $"{printers.Count}", BambuTitleColour,
                             boldFont, tagFont, mutedBrush, scale, sparkleGlyph: false);

        var dotRow = new Rectangle(area.X + outerPad, top + rowHeight, Math.Max(12, area.Width - outerPad * 2), rowHeight);
        int capsuleH = Math.Clamp((int)Math.Round(rowHeight * 0.72), 8, 15);
        int dia = Math.Clamp(capsuleH - 4, 5, 9);
        int dotGap = Math.Max(2, dia / 3);
        int padX = Math.Clamp(capsuleH / 4, 3, 5);
        int reqCapW = padX * 2 + printers.Count * dia + Math.Max(0, printers.Count - 1) * dotGap;
        int capW = Math.Min(dotRow.Width, reqCapW);
        var capRect = new Rectangle(dotRow.X, dotRow.Y + (dotRow.Height - capsuleH) / 2, capW, capsuleH);

        var prev = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(CapsuleFill))
        using (var border = new Pen(CapsuleBorder, 1f))
        {
            FillRoundedCapsule(g, capRect, fill, border);
        }

        using var doneFill = new SolidBrush(MeterStrip.BambuDone);
        using var idleFill = new SolidBrush(Color.FromArgb(72, 104, 88));
        using var offPen = new Pen(Color.FromArgb(78, 86, 102), 1.1f);
        float cy = capRect.Y + capRect.Height / 2f;
        float x = capRect.X + padX;
        int doneCount = 0;

        foreach (var p in printers)
        {
            if (x + dia > capRect.Right - 2) break;
            var box = new RectangleF(x, cy - dia / 2f, dia, dia);
            if (p.State == BambuPrinterState.Finished)
            {
                doneCount++;
                g.FillEllipse(doneFill, box);
            }
            else if (p.State == BambuPrinterState.Offline)
            {
                g.DrawEllipse(offPen, box);
            }
            else
            {
                g.FillEllipse(idleFill, box);
            }
            x += dia + dotGap;
        }
        g.SmoothingMode = prev;

        int textLeft = capRect.Right + 4;
        if (!vertical && dotRow.Right - textLeft >= 18)
        {
            string statusTxt = doneCount > 0 ? $"{doneCount}✓" : "idle";
            using var statusBrush = new SolidBrush(doneCount > 0 ? Color.FromArgb(130, 220, 190) : Theme.Muted);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
            };
            g.DrawString(statusTxt, tagFont, statusBrush, new Rectangle(textLeft, dotRow.Y, dotRow.Right - textLeft, dotRow.Height), fmt);
        }
    }

    private static readonly Color WaitingDotFill = Color.FromArgb(255, 158, 24);
    private static readonly Color WaitingDotRim = Color.FromArgb(255, 235, 175);
    private static readonly Color RunningDotFill = Color.FromArgb(56, 152, 255);
    private static readonly Color RunningDotRim = Color.FromArgb(145, 205, 255);
    private static readonly Color DoneDotFill = Color.FromArgb(40, 200, 128);
    private static readonly Color DoneReadDotFill = Color.FromArgb(32, 162, 104);
    private static readonly Color DoneDotRim = Color.FromArgb(110, 225, 170);
    private static readonly Color CapsuleFill = Color.FromArgb(32, 37, 46);
    private static readonly Color CapsuleBorder = Color.FromArgb(54, 62, 76);
    private static readonly Color SectionDividerColour = Color.FromArgb(124, 138, 164);

    public static void PaintRemoteChats(
        Graphics g,
        Rectangle area,
        RemoteChatsReading? reading,
        int maxRowsPerColumn = 3,
        string? hoveredChatId = null,
        bool stripHovered = false,
        RemoteChatService? serviceFilter = null)
    {
        IReadOnlyList<RemoteServerRow> allRows = reading?.Rows ?? [];
        IReadOnlyList<RemoteServerRow> rows = serviceFilter is { } svc
            ? allRows.Where(r => r.Service == svc).ToList()
            : allRows;
        if (rows.Count == 0) { PaintRemoteChatsState(g, area, reading, serviceFilter); return; }

        float glow = RemoteChatUsage.GetAttentionGlowIntensity(serviceFilter);
        if (glow > 0.01f)
            PaintRemoteChatsAttentionGlow(g, area, glow);

        RenderOrHitTestRemoteChats(g, area, rows, maxRowsPerColumn, hoveredChatId,
                                   stripHovered || hoveredChatId is not null,
                                   hitPoint: null, serviceFilter, out _, out _);
    }

    internal static void PaintRemoteChatsAttentionGlow(Graphics g, Rectangle area, float glow)
    {
        var box = new Rectangle(area.X + 2, area.Y + 2, Math.Max(8, area.Width - 4), Math.Max(8, area.Height - 4));
        var prev = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = RoundedRectPath(box, 6);
        int fillAlpha = (int)Math.Clamp(Math.Round(78 * glow), 0, 255);
        int outerAlpha = (int)Math.Clamp(Math.Round(150 * glow), 0, 255);
        int rimAlpha = (int)Math.Clamp(Math.Round(240 * glow), 0, 255);

        using (var wash = new SolidBrush(Color.FromArgb(fillAlpha, 255, 168, 24)))
            g.FillPath(wash, path);

        using (var halo = new Pen(Color.FromArgb(outerAlpha, 255, 158, 24), 2.8f))
            g.DrawPath(halo, path);

        using (var rim = new Pen(Color.FromArgb(rimAlpha, 255, 224, 130), 1.2f))
            g.DrawPath(rim, path);

        g.SmoothingMode = prev;
    }

    internal static GraphicsPath RoundedRectPath(Rectangle box, int radius)
    {
        var path = new GraphicsPath();
        int r = Math.Min(radius, Math.Min(box.Width, box.Height) / 2);
        if (r <= 1)
        {
            path.AddRectangle(box);
            return path;
        }
        int d = r * 2;
        path.AddArc(box.X, box.Y, d, d, 180, 90);
        path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
        path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
        path.AddArc(box.X, box.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Returns true when <paramref name="pt"/> falls within the header row of the Claude or Antigravity usage strip.
    /// </summary>
    public static bool IsPointInHeaderRow(
        Rectangle area,
        RemoteChatService service,
        Point pt,
        int maxRowsPerColumn = 3)
    {
        if (!area.Contains(pt)) return false;
        int meterCount = service == RemoteChatService.Antigravity
            ? GetAntigravityRowCount(AntigravityUsage.Current?.Limits)
            : GetClaudeRowCount(ClaudeUsage.Current?.Limits);
        return MeterStrip.GetHeaderRow(area, meterCount, maxRowsPerColumn, out _) is { } hdr && hdr.Contains(pt);
    }

    /// <summary>
    /// Returns the individual chat circle under <paramref name="pt"/> inside the header row of the
    /// Claude or Antigravity usage strip, or null if none.
    /// </summary>
    public static RemoteChatItem? HitTestHeaderChat(
        Rectangle area,
        RemoteChatService service,
        Point pt,
        int maxRowsPerColumn = 3)
    {
        if (!area.Contains(pt) || !RemoteChatUsage.Enabled) return null;
        var stripChats = RemoteChatUsage.GetStripChatsForService(service);
        if (stripChats.Count == 0) return null;

        int meterCount = service == RemoteChatService.Antigravity
            ? GetAntigravityRowCount(AntigravityUsage.Current?.Limits)
            : GetClaudeRowCount(ClaudeUsage.Current?.Limits);

        if (MeterStrip.GetHeaderRow(area, meterCount, maxRowsPerColumn, out float scale) is not { } hdrRow)
            return null;
        if (!hdrRow.Contains(pt)) return null;

        bool compact = hdrRow.Height <= 9;
        float namePt = compact ? 6.3f : Math.Clamp(7.2f * scale, 6.5f, 14f);
        using var measureBmp = new Bitmap(1, 1);
        using var measureG = Graphics.FromImage(measureBmp);
        using var boldNameFont = Theme.Face(namePt, FontStyle.Bold);

        bool sparkleGlyph = service == RemoteChatService.Antigravity;
        string title = sparkleGlyph
            ? GetAntigravityHeaderTitle(area.Width, stripChats.Count)
            : GetClaudeHeaderTitle(area.Width, stripChats.Count);
        int glyphW = sparkleGlyph
            ? (int)Math.Ceiling(Math.Clamp(4.2f * scale, 3.8f, 7.5f) * 2 + 3 * scale)
            : (int)Math.Ceiling(Math.Clamp(3.5f * scale, 3.2f, 6.2f) * 2 + 3 * scale);
        int textX = hdrRow.X + glyphW;
        int availTextW = Math.Max(8, hdrRow.Right - textX);
        int titleWInt = Math.Min(availTextW, (int)Math.Ceiling(measureG.MeasureString(title, boldNameFont).Width) + 2);
        int rightX = textX + titleWInt + (int)Math.Round(3 * scale);
        if (hdrRow.Right - rightX < 14) return null;

        var rightBox = new Rectangle(rightX, hdrRow.Y, hdrRow.Right - rightX, hdrRow.Height);
        PaintOrHitTestHeaderCapsule(g: null, rightBox, stripChats, hoveredChatId: null, pt, scale, out var hit);
        return hit;
    }

    private static bool PaintOrHitTestHeaderCapsule(
        Graphics? g,
        Rectangle rightBox,
        IReadOnlyList<RemoteChatItem> stripChats,
        string? hoveredChatId,
        Point? hitPoint,
        float scale,
        out RemoteChatItem? hitChat)
    {
        hitChat = null;
        if (stripChats.Count == 0 || rightBox.Width < 12) return false;

        int waiting = stripChats.Count(c => c.State == RemoteChatState.Waiting);
        int running = stripChats.Count(c => c.State == RemoteChatState.Running);
        int done = stripChats.Count(c => c.State == RemoteChatState.Done);
        var synthRow = new RemoteServerRow(
            "hdr", "", "", stripChats[0].Service, Online: true,
            waiting, running, done, stripChats);

        int capsuleH = Math.Clamp((int)Math.Round(rightBox.Height * 0.78), 8, 16);
        int idealDia = Math.Clamp(capsuleH - 4, 6, 11);
        int padX = Math.Clamp(capsuleH / 4, 3, 5);
        int desiredCapW = MeasureRowCapsuleWidth(synthRow, idealDia, padX, maxCircles: 8);

        float subPt = Math.Clamp(6.6f * scale, 6.0f, 12f);
        using var subFont = Theme.Face(subPt, FontStyle.Bold);
        using var overflowFont = Theme.Face(Math.Max(5.8f, subPt - 0.6f), FontStyle.Bold);

        string badgeText = FormatRowBadgeText(synthRow);
        int badgeW = 0;
        if (badgeText.Length > 0)
        {
            using var measureBmp = g is null ? new Bitmap(1, 1) : null;
            using var ownedG = g is null ? Graphics.FromImage(measureBmp!) : null;
            var mg = g ?? ownedG!;
            badgeW = (int)Math.Ceiling(mg.MeasureString(badgeText, subFont).Width) + 2;
        }

        bool showBadge = badgeW > 0 && rightBox.Width >= desiredCapW + badgeW + 5;
        int capW = showBadge
            ? desiredCapW
            : Math.Min(rightBox.Width, desiredCapW);
        int capX = showBadge
            ? rightBox.Right - badgeW - 3 - capW
            : rightBox.Right - capW;

        var trackRect = new Rectangle(
            Math.Max(rightBox.X, capX),
            rightBox.Y + (rightBox.Height - capsuleH) / 2,
            capW,
            capsuleH);

        if (PaintOrHitTestRowCircles(g, trackRect, synthRow, hoveredChatId, hitPoint, overflowFont, out hitChat))
            return true;

        if (g is not null && showBadge)
        {
            var (_, subCol, _) = RowColours(synthRow);
            using var subBrush = new SolidBrush(subCol);
            using var rightFmt = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
            };
            var subRect = new Rectangle(trackRect.Right + 2, rightBox.Y, rightBox.Right - (trackRect.Right + 2), rightBox.Height);
            g.DrawString(badgeText, subFont, subBrush, subRect, rightFmt);
        }

        return false;
    }

    /// <summary>Returns the individual chat circle under <paramref name="pt"/>, or null if none.</summary>
    public static RemoteChatItem? HitTestRemoteChat(
        Rectangle area,
        RemoteChatsReading? reading,
        Point pt,
        int maxRowsPerColumn = 3,
        RemoteChatService? serviceFilter = null)
    {
        IReadOnlyList<RemoteServerRow> allRows = reading?.Rows ?? [];
        IReadOnlyList<RemoteServerRow> rows = serviceFilter is { } svc
            ? allRows.Where(r => r.Service == svc).ToList()
            : allRows;
        if (rows.Count == 0 || !area.Contains(pt)) return null;

        RenderOrHitTestRemoteChats(g: null, area, rows, maxRowsPerColumn, hoveredChatId: null,
                                   stripHovered: false, hitPoint: pt, serviceFilter, out var hit, out _);
        return hit;
    }

    private static void RenderOrHitTestRemoteChats(
        Graphics? g,
        Rectangle area,
        IReadOnlyList<RemoteServerRow> rows,
        int maxRowsPerColumn,
        string? hoveredChatId,
        bool stripHovered,
        Point? hitPoint,
        RemoteChatService? serviceFilter,
        out RemoteChatItem? hitChat,
        out int intrinsicWidth)
    {
        hitChat = null;

        using var measureBmp = g is null ? new Bitmap(1, 1) : null;
        using var ownedMeasureG = g is null ? Graphics.FromImage(measureBmp!) : null;
        var measureG = g ?? ownedMeasureG!;

        bool vertical = area.Height > area.Width;
        int maxRows = vertical ? rows.Count : Math.Clamp(maxRowsPerColumn, 1, 8);
        int cols = vertical ? 1 : MeterStrip.ColumnsFor(false, rows.Count, maxRows);
        int rowsPerCol = (int)Math.Ceiling(rows.Count / (double)cols);

        bool showHeader = !vertical && (rowsPerCol + 1 <= maxRows) && (area.Height - 2) / (rowsPerCol + 1) >= 11;
        int totalRows = rowsPerCol + (showHeader ? 1 : 0);

        int minRowH = vertical ? 18 : (totalRows >= 4 ? 8 : 10);
        int maxRowH = vertical ? 34 : 38;
        int vPad = vertical ? 4 : (totalRows >= 5 ? 2 : (area.Height >= 56 ? 6 : 4));
        int availH = Math.Max(totalRows * minRowH, area.Height - vPad);
        int rowHeight = Math.Clamp(availH / totalRows, minRowH, maxRowH);
        int top = area.Y + Math.Max(1, (area.Height - totalRows * rowHeight) / 2);

        bool compact = !vertical && rowHeight <= 9;
        float scale = vertical ? 1f : Math.Clamp(rowHeight / 13.0f, 0.72f, 2.0f);
        float namePt = vertical ? 6.8f : (compact ? 6.3f : Math.Clamp(7.2f * scale, 6.5f, 14f));
        float subPt = compact ? 6.2f : Math.Clamp(6.8f * scale, 6.2f, 13f);

        int outerPad = vertical ? 4 : Math.Clamp((int)Math.Round(5 * scale), 4, 10);
        int colGap = cols > 1 ? Math.Clamp((int)Math.Round(10 * scale), 8, 16) : 0;
        int trackGap = Math.Clamp((int)Math.Round(4 * scale), 3, 6);

        int capsuleH = vertical
            ? Math.Clamp(rowHeight / 2, 8, 14)
            : Math.Clamp((int)Math.Round(rowHeight * 0.78), 8, 16);
        int idealDia = Math.Clamp(capsuleH - 4, 6, 12);
        int padX = Math.Clamp(capsuleH / 4, 3, 5);

        int totalWaiting = 0, totalRunning = 0, totalChats = 0;
        foreach (var r in rows)
        {
            totalWaiting += r.WaitingCount;
            totalRunning += r.RunningCount;
            totalChats += r.TotalCount;
        }

        using var nameFont = Theme.Face(namePt);
        using var boldNameFont = Theme.Face(namePt, FontStyle.Bold);
        using var subFont = Theme.Face(subPt, FontStyle.Bold);
        using var overflowFont = Theme.Face(Math.Max(5.8f, subPt - 0.6f), FontStyle.Bold);
        using var defaultLblBrush = new SolidBrush(Color.FromArgb(184, 192, 206));
        using var mutedBrush = new SolidBrush(Theme.Muted);
        using var innerSepPen = new Pen(Color.FromArgb(42, 46, 56), 1f);

        // Pre-measure each column's tight width based on its labels, per-row circle capsules, and count badges.
        var colNameW = new int[cols];
        var colWidths = new int[cols];
        var rowCapWidths = new int[rows.Count];
        var rowSubWidths = new int[rows.Count];

        for (int c = 0; c < cols; c++)
        {
            int startIdx = c * rowsPerCol;
            int countInCol = Math.Min(rowsPerCol, rows.Count - startIdx);
            if (countInCol <= 0) continue;

            float maxLblW = 0f;
            for (int j = 0; j < countInCol; j++)
            {
                var r = rows[startIdx + j];
                bool bold = r.WaitingCount > 0 || r.RunningCount > 0;
                float lw = measureG.MeasureString(r.Label, bold ? boldNameFont : nameFont).Width;
                if (lw > maxLblW) maxLblW = lw;
            }

            int nameW = Math.Max((int)Math.Round(28 * scale), (int)Math.Ceiling(maxLblW) + 2);
            colNameW[c] = nameW;

            int maxRowW = nameW + 36;
            for (int j = 0; j < countInCol; j++)
            {
                int idx = startIdx + j;
                var r = rows[idx];
                int capW = MeasureRowCapsuleWidth(r, idealDia, padX, maxCircles: 10);
                string st = FormatRowBadgeText(r);
                int subW = (int)Math.Ceiling(measureG.MeasureString(st, subFont).Width) + 2;

                rowCapWidths[idx] = capW;
                rowSubWidths[idx] = subW;

                int rw = nameW + trackGap + capW + trackGap + subW;
                if (rw > maxRowW) maxRowW = rw;
            }

            colWidths[c] = maxRowW;
        }

        int totalColsW = colWidths.Sum() + Math.Max(0, cols - 1) * colGap;
        int minHeaderW = showHeader ? (int)Math.Ceiling((serviceFilter is null ? 166 : 88) * scale) : 60;
        int contentW = Math.Max(totalColsW, minHeaderW);
        intrinsicWidth = contentW + outerPad * 2;

        // If the header is slightly wider than a single short column, distribute the extra pixels across columns.
        if (!vertical && totalColsW < contentW && cols > 0)
        {
            int extraPerCol = (contentW - totalColsW) / cols;
            for (int c = 0; c < cols; c++)
                colWidths[c] += extraPerCol;
        }

        // Scale column widths proportionally to fit the actual strip width (whether dragged narrower or wider).
        int availInnerW = Math.Max(24, area.Width - outerPad * 2);
        bool measuringIntrinsic = g is null && hitPoint is null;
        if (!vertical && !measuringIntrinsic && cols > 0)
        {
            int currentSum = colWidths.Sum();
            int targetSum = Math.Max(cols * 32, availInnerW - Math.Max(0, cols - 1) * colGap);
            if (currentSum > 0 && targetSum != currentSum)
            {
                double ratio = targetSum / (double)currentSum;
                for (int c = 0; c < cols; c++)
                    colWidths[c] = Math.Max(32, (int)Math.Round(colWidths[c] * ratio));
            }
        }

        int meterStartRow = 0;
        if (showHeader)
        {
            if (g is not null)
            {
                string headerTag = totalWaiting > 0
                    ? $"{totalWaiting}!"
                    : totalRunning > 0
                        ? $"{totalRunning} run"
                        : $"{totalChats} live";
                if (serviceFilter is null)
                {
                    headerTag = totalWaiting > 0
                        ? $"{totalWaiting} help · {totalChats}"
                        : totalRunning > 0
                            ? $"{totalRunning} run · {totalChats}"
                            : $"{totalChats} live";
                }

                Color baseHdrCol = serviceFilter switch
                {
                    RemoteChatService.Claude => ClaudeTitleColour,
                    RemoteChatService.Antigravity => AntigravityTitleColour,
                    _ => RemoteChatsTitleColour,
                };
                Color hdrCol = totalWaiting > 0 ? Color.FromArgb(255, 210, 115) : baseHdrCol;
                string hdrTitle = serviceFilter switch
                {
                    RemoteChatService.Claude => "Chats",
                    RemoteChatService.Antigravity => "Chats",
                    _ => "Remote Chats",
                };
                bool sparkleGlyph = serviceFilter == RemoteChatService.Antigravity;
                var hdrRow = new Rectangle(area.X + outerPad, top, availInnerW, rowHeight);
                if (stripHovered && availInnerW >= 72)
                {
                    PaintRemoteKeyHeaderRow(g, hdrRow, subFont, scale);
                }
                else
                {
                    PaintRemoteHeaderRow(g, hdrRow, hdrTitle, headerTag, hdrCol, boldNameFont, nameFont, mutedBrush, scale, sparkleGlyph);
                }
            }
            meterStartRow = 1;
        }

        using var leftFmt = new StringFormat
        {
            Alignment = vertical ? StringAlignment.Center : StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };

        int curColX = area.X + outerPad;
        for (int c = 0; c < cols; c++)
        {
            int startIdx = c * rowsPerCol;
            int countInCol = Math.Min(rowsPerCol, rows.Count - startIdx);
            if (countInCol <= 0) break;

            int colX = curColX;
            int colW = vertical ? availInnerW : colWidths[c];
            curColX += colW + colGap;

            if (g is not null && c > 0 && !vertical)
            {
                int sepX = colX - colGap / 2;
                int sepTop = top + meterStartRow * rowHeight + 2;
                int sepBot = top + (meterStartRow + rowsPerCol) * rowHeight - 2;
                if (sepBot > sepTop)
                    g.DrawLine(innerSepPen, sepX, sepTop, sepX, sepBot);
            }

            int nameW = vertical ? colW : Math.Min(colNameW[c], colW * 45 / 100);

            for (int j = 0; j < countInCol; j++)
            {
                int idx = startIdx + j;
                var rowData = rows[idx];
                int rIdx = meterStartRow + j;
                var rowRect = new Rectangle(colX, top + rIdx * rowHeight, colW, rowHeight);

                Rectangle nameRect, trackRect, subRect;
                if (vertical)
                {
                    int nameH = Math.Max(9, (rowHeight - capsuleH) / 2);
                    nameRect = new Rectangle(rowRect.X, rowRect.Y, rowRect.Width, nameH);
                    trackRect = new Rectangle(rowRect.X, rowRect.Y + nameH, rowRect.Width, capsuleH);
                    subRect = new Rectangle(rowRect.X, rowRect.Y + nameH + capsuleH, rowRect.Width, Math.Max(8, rowHeight - capsuleH - nameH));
                }
                else
                {
                    nameRect = new Rectangle(rowRect.X, rowRect.Y, nameW, rowHeight);
                    int subW = rowSubWidths[idx];
                    int trackX = rowRect.X + nameW + trackGap;
                    int maxTrackW = Math.Max(10, rowRect.Right - trackX - trackGap - subW);
                    int desiredCapW = area.Width > intrinsicWidth
                        ? MeasureRowCapsuleWidth(rowData, idealDia, padX, maxCircles: Math.Max(10, rowData.Chats.Count))
                        : rowCapWidths[idx];
                    int tightTrackW = Math.Min(desiredCapW, maxTrackW);
                    trackRect = new Rectangle(trackX, rowRect.Y + (rowHeight - capsuleH) / 2, tightTrackW, capsuleH);
                    subRect = new Rectangle(trackRect.Right + trackGap, rowRect.Y, Math.Max(8, rowRect.Right - (trackRect.Right + trackGap)), rowHeight);
                }

                if (PaintOrHitTestRowCircles(g, trackRect, rowData, hoveredChatId, hitPoint, overflowFont, out var rowHit))
                {
                    hitChat = rowHit;
                    return;
                }

                if (g is not null)
                {
                    var (lblCol, subCol, bold) = RowColours(rowData);
                    if (lblCol is { } customLbl)
                    {
                        using var lblBrush = new SolidBrush(customLbl);
                        g.DrawString(rowData.Label, bold ? boldNameFont : nameFont, lblBrush, nameRect, leftFmt);
                    }
                    else
                    {
                        g.DrawString(rowData.Label, bold ? boldNameFont : nameFont, defaultLblBrush, nameRect, leftFmt);
                    }

                    string summaryText = FormatRowBadgeText(rowData);
                    using var subBrush = new SolidBrush(subCol);
                    g.DrawString(summaryText, subFont, subBrush, subRect, leftFmt);
                }
            }
        }
    }

    private static int MeasureRowCapsuleWidth(RemoteServerRow row, int dia, int padX, int maxCircles = 10)
    {
        int count = row.Chats.Count;
        if (count == 0)
            return padX * 2 + Math.Max(6, dia - 1);

        if (count <= maxCircles)
            return padX * 2 + MeasureCirclesWidth(row.Chats, count, dia);

        return padX * 2 + MeasureCirclesWidth(row.Chats, maxCircles, dia) + 18;
    }

    private static string FormatRowBadgeText(RemoteServerRow r)
    {
        if (!r.Online && r.TotalCount == 0) return "Off";
        if (r.TotalCount == 0) return "";

        var parts = new List<string>(3);
        if (r.WaitingCount > 0) parts.Add($"{r.WaitingCount}!");
        if (r.RunningCount > 0) parts.Add($"{r.RunningCount}r");
        if (r.DoneCount > 0) parts.Add($"{r.DoneCount}✓");
        return string.Join(" ", parts);
    }

    private static (Color? LabelColour, Color SubColour, bool Bold) RowColours(RemoteServerRow r)
    {
        Color svcTitle = r.Service == RemoteChatService.Antigravity ? AntigravityTitleColour : ClaudeTitleColour;
        if (r.WaitingCount > 0)
            return (Color.FromArgb(255, 210, 115), WaitingDotFill, true);
        if (r.RunningCount > 0)
            return (svcTitle, svcTitle, true);
        if (r.DoneCount > 0)
            return (Color.FromArgb(145, 220, 195), Color.FromArgb(130, 220, 190), false);
        return (null, Theme.Muted, false);
    }

    /// <summary>
    /// Draws (or hit-tests) the capsule of per-chat circles for a single server × service row,
    /// grouped into Waiting │ Running │ Done sections with vertical section dividers.
    /// </summary>
    private static bool PaintOrHitTestRowCircles(
        Graphics? g,
        Rectangle track,
        RemoteServerRow row,
        string? hoveredChatId,
        Point? hitPoint,
        Font overflowFont,
        out RemoteChatItem? hitChat)
    {
        hitChat = null;

        if (g is not null)
        {
            var prev = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var fill = new SolidBrush(CapsuleFill))
            using (var border = new Pen(CapsuleBorder, 1f))
            {
                FillRoundedCapsule(g, track, fill, border);
            }
            g.SmoothingMode = prev;
        }

        int count = row.Chats.Count;
        int padX = Math.Clamp(track.Height / 4, 3, 5);
        float cy = track.Y + track.Height / 2f;

        if (count == 0)
        {
            if (g is not null && row.Online)
            {
                var prev = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float emptyDia = Math.Clamp(track.Height - 5, 5, 8);
                using var idlePen = new Pen(Color.FromArgb(86, 98, 118), 1.2f);
                g.DrawEllipse(idlePen, track.X + padX, cy - emptyDia / 2f, emptyDia, emptyDia);
                g.SmoothingMode = prev;
            }
            return false;
        }

        int availW = Math.Max(8, track.Width - padX * 2);
        int maxDia = Math.Clamp(track.Height - 4, 6, 12);
        int minDia = Math.Min(maxDia, 6);

        // Choose the largest circle diameter that fits all chats (including section dividers),
        // or fall back to minDia and show as many circles as fit plus a "+N" badge.
        int chosenDia = minDia;
        int shownCount = count;

        for (int d = maxDia; d >= minDia; d--)
        {
            int reqW = MeasureCirclesWidth(row.Chats, count, d);
            if (reqW <= availW)
            {
                chosenDia = d;
                shownCount = count;
                break;
            }
        }

        int overflow = 0;
        if (MeasureCirclesWidth(row.Chats, count, chosenDia) > availW)
        {
            int badgeReserve = 16;
            int fitW = Math.Max(chosenDia, availW - badgeReserve);
            shownCount = 1;
            for (int n = count - 1; n >= 1; n--)
            {
                if (MeasureCirclesWidth(row.Chats, n, chosenDia) <= fitW)
                {
                    shownCount = n;
                    break;
                }
            }
            overflow = count - shownCount;
        }

        int dotGap = Math.Max(2, chosenDia / 4);
        int divAdvance = dotGap * 2 + 3;
        float x = track.X + padX;

        var prevMode = g?.SmoothingMode ?? SmoothingMode.Default;
        if (g is not null) g.SmoothingMode = SmoothingMode.AntiAlias;

        using var sepPen = g is not null ? new Pen(SectionDividerColour, 1.2f) : null;
        using var waitFill = g is not null ? new SolidBrush(WaitingDotFill) : null;
        using var waitRim = g is not null ? new Pen(WaitingDotRim, 1.2f) : null;
        using var waitPip = g is not null ? new SolidBrush(Color.FromArgb(255, 248, 225)) : null;
        using var runFill = g is not null ? new SolidBrush(RunningDotFill) : null;
        using var runRim = g is not null ? new Pen(RunningDotRim, 1f) : null;
        using var doneFill = g is not null ? new SolidBrush(DoneDotFill) : null;
        using var doneReadFill = g is not null ? new SolidBrush(DoneReadDotFill) : null;
        using var doneRim = g is not null ? new Pen(DoneDotRim, 1f) : null;
        using var hoverRing = g is not null ? new Pen(Color.White, 1.6f) : null;

        RemoteChatState? prevState = null;

        for (int i = 0; i < shownCount; i++)
        {
            var chat = row.Chats[i];
            if (prevState is not null && chat.State != prevState.Value)
            {
                // Draw a vertical section separator line between status sections (Waiting │ Running │ Done)
                float sepX = x - dotGap + divAdvance / 2f;
                if (g is not null && sepPen is not null)
                    g.DrawLine(sepPen, sepX, track.Y + 2f, sepX, track.Bottom - 2f);
                x += divAdvance - dotGap;
            }

            var hitBox = new Rectangle(
                (int)Math.Floor(x - 1),
                track.Y,
                chosenDia + 2,
                track.Height);

            if (hitPoint is { } pt && hitBox.Contains(pt))
            {
                hitChat = chat;
                if (g is not null) g.SmoothingMode = prevMode;
                return true;
            }

            if (g is not null)
            {
                bool isHovered = hoveredChatId is not null
                    && string.Equals(chat.Id, hoveredChatId, StringComparison.Ordinal);
                float drawDia = isHovered ? chosenDia + 1.4f : chosenDia;
                var dotRect = new RectangleF(x - (drawDia - chosenDia) / 2f, cy - drawDia / 2f, drawDia, drawDia);

                switch (chat.State)
                {
                    case RemoteChatState.Waiting:
                        g.FillEllipse(waitFill!, dotRect);
                        g.DrawEllipse(waitRim!, dotRect);
                        if (chosenDia >= 7)
                        {
                            float pip = Math.Max(2f, chosenDia * 0.26f);
                            g.FillEllipse(waitPip!, dotRect.X + (dotRect.Width - pip) / 2f,
                                          dotRect.Y + (dotRect.Height - pip) / 2f, pip, pip);
                        }
                        break;

                    case RemoteChatState.Running:
                        g.FillEllipse(runFill!, dotRect);
                        g.DrawEllipse(runRim!, dotRect);
                        break;

                    default:
                        g.FillEllipse(chat.Unread ? doneFill! : doneReadFill!, dotRect);
                        g.DrawEllipse(doneRim!, dotRect);
                        break;
                }

                if (isHovered && hoverRing is not null)
                {
                    var ringRect = RectangleF.Inflate(dotRect, 1.2f, 1.2f);
                    g.DrawEllipse(hoverRing, ringRect.X, ringRect.Y, ringRect.Width, ringRect.Height);
                }
            }

            x += chosenDia + dotGap;
            prevState = chat.State;
        }

        if (overflow > 0 && g is not null)
        {
            using var ovBrush = new SolidBrush(Color.FromArgb(190, 200, 218));
            using var ovFmt = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
            };
            var ovRect = new Rectangle((int)x, track.Y, Math.Max(10, track.Right - (int)x - 2), track.Height);
            g.DrawString($"+{overflow}", overflowFont, ovBrush, ovRect, ovFmt);
        }

        if (g is not null) g.SmoothingMode = prevMode;
        return false;
    }

    private static int MeasureCirclesWidth(IReadOnlyList<RemoteChatItem> chats, int count, int dia)
    {
        if (count <= 0) return 0;
        int dotGap = Math.Max(2, dia / 4);
        int divAdvance = dotGap * 2 + 3;

        int sections = 1;
        RemoteChatState prev = chats[0].State;
        for (int i = 1; i < count; i++)
        {
            if (chats[i].State != prev)
            {
                sections++;
                prev = chats[i].State;
            }
        }

        int dividers = sections - 1;
        int sameSectionGaps = Math.Max(0, count - 1 - dividers);
        return count * dia + sameSectionGaps * dotGap + dividers * divAdvance;
    }

    private static void FillRoundedCapsule(Graphics g, Rectangle box, Brush fill, Pen border)
    {
        int radius = Math.Min(box.Height, box.Width) / 2;
        if (radius <= 1)
        {
            g.FillRectangle(fill, box);
            g.DrawRectangle(border, box);
            return;
        }

        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(box.X, box.Y, d, d, 90, 180);
        path.AddArc(box.Right - d, box.Y, d, d, 270, 180);
        path.CloseFigure();
        g.FillPath(fill, path);
        g.DrawPath(border, path);
    }

    /// <summary>
    /// Draws a visual colour-coded Key in the header row when the user hovers over a chat circle or the strip:
    ///   [🟠] !=help  │  [🔵] r=run  │  [🟢] ✓=done
    /// </summary>
    private static void PaintRemoteKeyHeaderRow(Graphics g, Rectangle row, Font keyFont, float scale)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        bool compactKey = row.Width < (int)Math.Round(132 * scale);
        float dia = Math.Clamp(row.Height * 0.48f, 6f, 10f);
        float cy = row.Y + row.Height / 2f;
        float x = row.X + 1f;

        using var waitFill = new SolidBrush(WaitingDotFill);
        using var waitRim = new Pen(WaitingDotRim, 1.1f);
        using var waitText = new SolidBrush(Color.FromArgb(255, 215, 130));
        using var runFill = new SolidBrush(RunningDotFill);
        using var runRim = new Pen(RunningDotRim, 1f);
        using var runText = new SolidBrush(Color.FromArgb(145, 205, 255));
        using var doneFill = new SolidBrush(DoneDotFill);
        using var doneRim = new Pen(DoneDotRim, 1f);
        using var doneText = new SolidBrush(Color.FromArgb(135, 225, 185));
        using var sepPen = new Pen(SectionDividerColour, 1.1f);
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };

        void DrawItem(Brush dotFill, Pen dotRim, string label, Brush textBrush, bool withPip, bool withSepAfter)
        {
            var dotRect = new RectangleF(x, cy - dia / 2f, dia, dia);
            g.FillEllipse(dotFill, dotRect);
            g.DrawEllipse(dotRim, dotRect);
            if (withPip && dia >= 6.5f)
            {
                float pip = Math.Max(1.8f, dia * 0.26f);
                using var pipBrush = new SolidBrush(Color.FromArgb(255, 248, 225));
                g.FillEllipse(pipBrush, dotRect.X + (dotRect.Width - pip) / 2f,
                              dotRect.Y + (dotRect.Height - pip) / 2f, pip, pip);
            }
            x += dia + (compactKey ? 2f : 3f) * scale;

            float tw = g.MeasureString(label, keyFont).Width;
            g.DrawString(label, keyFont, textBrush, new RectangleF(x, row.Y, tw + 4f, row.Height), fmt);
            x += tw + (compactKey ? 2f : 3f) * scale;

            if (withSepAfter)
            {
                float sepX = x + 1f;
                g.DrawLine(sepPen, sepX, row.Y + 3f, sepX, row.Bottom - 3f);
                x += (compactKey ? 4f : 5f) * scale;
            }
        }

        DrawItem(waitFill, waitRim, compactKey ? "! " : "! help", waitText, withPip: true, withSepAfter: true);
        DrawItem(runFill, runRim, compactKey ? "r" : "r run", runText, withPip: false, withSepAfter: true);
        DrawItem(doneFill, doneRim, compactKey ? "✓" : "✓ done", doneText, withPip: false, withSepAfter: false);

        g.SmoothingMode = previous;
    }

    private static void PaintRemoteHeaderRow(Graphics g, Rectangle row, string title, string? tag,
                                             Color titleColour, Font titleFont, Font tagFont,
                                             Brush tagBrush, float scale, bool sparkleGlyph = false)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var titleBrush = new SolidBrush(titleColour);
        int textX;
        if (sparkleGlyph)
        {
            float r = Math.Clamp(4.0f * scale, 3.6f, 7.0f);
            float inner = r * 0.34f;
            float cx = row.X + r + 0.5f;
            float cy = row.Y + row.Height / 2f;
            var pts = new PointF[]
            {
                new(cx, cy - r),
                new(cx + inner, cy - inner),
                new(cx + r, cy),
                new(cx + inner, cy + inner),
                new(cx, cy + r),
                new(cx - inner, cy + inner),
                new(cx - r, cy),
                new(cx - inner, cy - inner),
            };
            g.FillPolygon(titleBrush, pts);
            textX = row.X + (int)Math.Ceiling(r * 2 + 3 * scale);
        }
        else
        {
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
            textX = row.X + (int)Math.Ceiling(r * 2 + 3 * scale);
        }

        g.SmoothingMode = previous;

        int availTextW = Math.Max(8, row.Right - textX);
        float titleW = g.MeasureString(title, titleFont).Width;
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

    private const string RemoteChatLegendLine = "Key: 🟠 (!) Needs help  │  🔵 (r) Running  │  🟢 (✓) Done";

    /// <summary>Tooltip for an individual chat circle when hovering over it on the taskbar.</summary>
    public static string RemoteChatItemTip(RemoteChatItem chat)
    {
        string svcName = chat.Service == RemoteChatService.Antigravity ? "Antigravity" : "Claude";
        string stateText = chat.State switch
        {
            RemoteChatState.Waiting => "🟠 Needs input / help (!)",
            RemoteChatState.Running => "🔵 Running (r)",
            _ => "🟢 Done (✓)",
        };

        var sb = new StringBuilder($"{chat.Server} · {svcName} — {stateText}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(chat.Repo))
            sb.Append($"[{chat.Repo}] ");
        sb.Append(chat.Title);

        if (!string.IsNullOrWhiteSpace(chat.Detail))
        {
            sb.AppendLine();
            sb.Append(chat.Detail);
        }

        sb.AppendLine();
        sb.AppendLine(RemoteChatLegendLine);
        sb.Append("Left-click circle to open chat · Right-click for all chats");
        return sb.ToString();
    }

    /// <summary>Nothing to plot yet: say which of the reasons it is, in the space a limit would use.</summary>
    private static void PaintState(Graphics g, Rectangle area, UsageReading? reading, bool isAntigravity)
    {
        float fontPt = Math.Clamp(area.Height * 0.16f, 7.5f, 12f);
        int barThick = Math.Clamp(area.Height / 8, MeterStrip.BarThickness, 12);
        using var font = Theme.Face(fontPt, FontStyle.Bold);
        using var centred = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        (string text, Color colour) = isAntigravity
            ? (!AntigravityUsage.Enabled ? ("AG · set up", AntigravityTitleColour)
               : reading is { Standby: true } ? ("AG · standby", Theme.Muted)
               : reading is { Error: not null, Settled: true } ? ("AG · error", MeterStrip.High)
               : ("AG ···", AntigravityTitleColour))
            : (reading is { NeedsKey: true } ? (ClaudeUsage.HasKey ? "Claude · expired" : "Claude · set up", ClaudeTitleColour)
               : reading is { Error: not null, Settled: true } ? ("Claude · error", MeterStrip.High)
               : ("Claude ···", ClaudeTitleColour));

        int width = Math.Max(16, area.Width - 16);
        var label = new Rectangle(area.X, area.Y, area.Width, area.Height - barThick - 6);

        using var brush = new SolidBrush(colour);
        g.DrawString(text, font, brush, label, centred);

        MeterStrip.Placeholder(g, new Rectangle(area.X + (area.Width - width) / 2,
                                                area.Bottom - barThick - 5, width,
                                                barThick));
    }

    private static void PaintBambuState(Graphics g, Rectangle area, BambuReading? reading)
    {
        float fontPt = Math.Clamp(area.Height * 0.16f, 7.5f, 12f);
        int barThick = Math.Clamp(area.Height / 8, MeterStrip.BarThickness, 12);
        using var font = Theme.Face(fontPt, FontStyle.Bold);
        using var centred = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        (string text, Color colour) = !BambuUsage.Enabled
            ? ("Bambu · set up", BambuTitleColour)
            : reading is { NeedsAuth: true }
                ? (BambuUsage.HasSignedInAccount ? "Bambu · expired" : "Bambu · sign in", BambuTitleColour)
                : reading is { Error: not null, Settled: true }
                    ? ("Bambu · error", MeterStrip.High)
                    : ("Bambu ···", BambuTitleColour);

        int width = Math.Max(16, area.Width - 16);
        var label = new Rectangle(area.X, area.Y, area.Width, area.Height - barThick - 6);

        using var brush = new SolidBrush(colour);
        g.DrawString(text, font, brush, label, centred);

        MeterStrip.Placeholder(g, new Rectangle(area.X + (area.Width - width) / 2,
                                                area.Bottom - barThick - 5, width,
                                                barThick));
    }

    private static void PaintRemoteChatsState(Graphics g, Rectangle area, RemoteChatsReading? reading, RemoteChatService? serviceFilter = null)
    {
        float fontPt = Math.Clamp(area.Height * 0.16f, 7.5f, 12f);
        int barThick = Math.Clamp(area.Height / 8, MeterStrip.BarThickness, 12);
        using var font = Theme.Face(fontPt, FontStyle.Bold);
        using var centred = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        Color baseCol = serviceFilter switch
        {
            RemoteChatService.Claude => ClaudeTitleColour,
            RemoteChatService.Antigravity => AntigravityTitleColour,
            _ => RemoteChatsTitleColour,
        };

        (string text, Color colour) = !RemoteChatUsage.Enabled
            ? ("Chats · set up", baseCol)
            : reading is { NeedsAuth: true }
                ? ("Chats · sign in", baseCol)
                : reading is { Error: not null, Settled: true }
                    ? ("Chats · error", MeterStrip.High)
                    : ("Chats ···", baseCol);

        int width = Math.Max(16, area.Width - 16);
        var label = new Rectangle(area.X, area.Y, area.Width, area.Height - barThick - 6);

        using var brush = new SolidBrush(colour);
        g.DrawString(text, font, brush, label, centred);

        MeterStrip.Placeholder(g, new Rectangle(area.X + (area.Width - width) / 2,
                                                area.Bottom - barThick - 5, width,
                                                barThick));
    }

    /// <summary>The whole Claude reading in words, for the tooltip — where the numbers actually live.</summary>
    public static string Tip(UsageReading? reading)
    {
        if (reading is null) return "Claude usage — starting up";
        if (reading.NeedsKey)
            return $"Claude usage — {reading.Error ?? "a session key is needed"} (Settings ▸ Claude usage)";

        if (reading.Error is not null) return $"Claude usage — {reading.Error}";
        if (reading.Limits.Count == 0) return "Claude usage — no limits reported";

        var text = new StringBuilder("Claude (claude.ai)");
        foreach (var limit in reading.Limits)
        {
            text.AppendLine();
            string friendlyLabel = limit.Label switch
            {
                "Session" => "Session (5h)",
                "Weekly" => "Weekly (7d)",
                _ => limit.Label,
            };
            text.Append($"{friendlyLabel}: {limit.Percent}%");

            var countdown = ClaudeUsage.Countdown(limit.ResetsAt);
            if (countdown.Length > 0) text.Append($"  ({countdown})");
        }

        AppendServiceRemoteChatsTip(text, RemoteChatService.Claude);
        return text.ToString();
    }

    /// <summary>The whole Antigravity reading in words (both IDE and CLI/Remote Gemini Pro &amp; Flash session and weekly limits).</summary>
    public static string AntigravityTip(UsageReading? reading)
    {
        if (!AntigravityUsage.Enabled)
            return "Antigravity usage — switched off (Settings ▸ Antigravity usage)";
        if (reading is null)
            return "Antigravity usage — starting up";
        if (reading is { Standby: true, Limits.Count: 0 })
            return "Antigravity usage — sign in under Settings ▸ Antigravity usage, or open Antigravity";
        if (reading.Error is not null)
            return $"Antigravity usage — {reading.Error}";
        if (reading.Limits.Count == 0)
            return "Antigravity usage — no Gemini quotas reported";

        var text = new StringBuilder(reading.Standby
            ? "Antigravity — Gemini Pro & Flash (standby — last reading)"
            : "Antigravity — Gemini Pro & Flash (IDE + CLI/Remote)");

        foreach (var limit in reading.Limits)
        {
            text.AppendLine();
            string friendlyLabel = limit.Label switch
            {
                "IDE 5h" => "IDE Session (5h)",
                "IDE 7d" => "IDE Weekly (7d)",
                "CLI 5h" => "CLI / Remote Session (5h)",
                "CLI 7d" => "CLI / Remote Weekly (7d)",
                _ => limit.Label,
            };
            text.Append($"{friendlyLabel}: {limit.Percent}%");

            var countdown = ClaudeUsage.Countdown(limit.ResetsAt);
            if (countdown.Length > 0) text.Append($"  ({countdown})");
        }

        AppendServiceRemoteChatsTip(text, RemoteChatService.Antigravity);
        return text.ToString();
    }

    private static void AppendServiceRemoteChatsTip(StringBuilder text, RemoteChatService service)
    {
        if (!RemoteChatUsage.Enabled || RemoteChatUsage.Current is not { } reading)
            return;

        var rows = reading.Rows.Where(r => r.Service == service).ToList();
        if (rows.Count == 0) return;

        int waiting = rows.Sum(r => r.WaitingCount);
        int running = rows.Sum(r => r.RunningCount);
        int done = rows.Sum(r => r.DoneCount);

        var summary = new List<string>();
        if (waiting > 0) summary.Add($"{waiting} need input");
        if (running > 0) summary.Add($"{running} running");
        if (done > 0) summary.Add($"{done} done");
        if (summary.Count == 0) summary.Add("all idle");

        text.AppendLine();
        text.AppendLine();
        text.Append($"Remote Chats — {string.Join(", ", summary)} (click for menu)");
        foreach (var c in rows.SelectMany(r => r.Chats).OrderBy(c => c.State).ThenByDescending(c => c.LastUpdated).Take(4))
        {
            text.AppendLine();
            string badge = c.State switch
            {
                RemoteChatState.Waiting => "⚠ WAIT",
                RemoteChatState.Running => "● RUN",
                _ => "✓ DONE",
            };
            string repoPrefix = !string.IsNullOrWhiteSpace(c.Repo) ? $"[{c.Repo}] " : "";
            string shortTitle = c.Title.Length <= 44 ? c.Title : c.Title[..41].TrimEnd() + "…";
            text.Append($"  {badge} ({c.Server}): {repoPrefix}{shortTitle}");
        }
    }

    /// <summary>Full tooltip breakdown for all enabled Bambu Lab printers in closet order.</summary>
    public static string BambuTip(BambuReading? reading)
    {
        if (!BambuUsage.Enabled)
            return "Bambu printers — switched off (Settings ▸ Bambu printers)";
        if (reading is null)
            return "Bambu printers — starting up";
        if (reading.NeedsAuth)
            return $"Bambu printers — {reading.Error ?? "sign in under Settings ▸ Bambu printers"}";
        if (reading.Printers.Count == 0 && reading.Error is not null)
            return $"Bambu printers — {reading.Error}";
        if (reading.Printers.Count == 0)
            return "Bambu printers — no enabled printers found on your account";

        int printing = reading.Printers.Count(p => p.State is BambuPrinterState.Printing or BambuPrinterState.Preparing);
        int errored = reading.Printers.Count(p => p.State == BambuPrinterState.Error);
        int paused = reading.Printers.Count(p => p.State == BambuPrinterState.Paused);
        int finished = reading.Printers.Count(p => p.State == BambuPrinterState.Finished);

        var summary = new List<string>();
        if (printing > 0) summary.Add($"{printing} printing");
        if (errored > 0) summary.Add($"{errored} error");
        if (paused > 0) summary.Add($"{paused} paused");
        if (finished > 0) summary.Add($"{finished} done");
        if (summary.Count == 0) summary.Add("all idle");

        var text = new StringBuilder($"Bambu Lab — {string.Join(", ", summary)}");
        for (int i = 0; i < reading.Printers.Count; i++)
        {
            var p = reading.Printers[i];
            text.AppendLine();
            text.Append($"{i + 1}. {p.Label}");
            if (!string.IsNullOrWhiteSpace(p.Model) && !p.Label.Contains(p.Model, StringComparison.OrdinalIgnoreCase))
                text.Append($" ({p.Model})");
            text.Append($": {BambuUsage.DescribePrinterState(p)}");

            if (!string.IsNullOrWhiteSpace(p.JobName) && p.State is not (BambuPrinterState.Idle or BambuPrinterState.Offline))
                text.Append($" — \"{p.JobName}\"");

            if (p.TotalLayers > 0 && p.State is BambuPrinterState.Printing or BambuPrinterState.Paused or BambuPrinterState.Error)
                text.Append($" [layer {p.CurrentLayer}/{p.TotalLayers}]");

            if (p.NozzleTemp is > 0 && p.BedTemp is > 0 && p.State is BambuPrinterState.Printing or BambuPrinterState.Preparing)
                text.Append($" [{p.NozzleTemp:0}°C / {p.BedTemp:0}°C]");
        }

        return text.ToString();
    }

    /// <summary>Full tooltip breakdown of remote server chats (optionally filtered to Claude or Antigravity).</summary>
    public static string RemoteChatsTip(RemoteChatsReading? reading, RemoteChatService? serviceFilter = null)
    {
        string prefix = serviceFilter switch
        {
            RemoteChatService.Claude => "Claude remote chats",
            RemoteChatService.Antigravity => "Antigravity remote chats",
            _ => "Remote server chats",
        };

        if (!RemoteChatUsage.Enabled)
            return $"{prefix} — switched off (Settings ▸ Remote chats)";
        if (reading is null)
            return $"{prefix} — starting up";
        if (reading.NeedsAuth)
            return $"{prefix} — {reading.Error ?? "sign in under Claude usage or Antigravity usage"}";

        IReadOnlyList<RemoteServerRow> rows = serviceFilter is { } svc
            ? reading.Rows.Where(r => r.Service == svc).ToList()
            : reading.Rows;

        if (rows.Count == 0 && reading.Error is not null)
            return $"{prefix} — {reading.Error}";
        if (rows.Count == 0)
            return $"{prefix} — no enabled servers found yet";

        int waiting = rows.Sum(r => r.WaitingCount);
        int running = rows.Sum(r => r.RunningCount);
        int done = rows.Sum(r => r.DoneCount);

        var summary = new List<string>();
        if (waiting > 0) summary.Add($"{waiting} need input");
        if (running > 0) summary.Add($"{running} running");
        if (done > 0) summary.Add($"{done} done");
        if (summary.Count == 0) summary.Add("all idle");

        string headerTitle = serviceFilter switch
        {
            RemoteChatService.Claude => "Claude Remote Chats",
            RemoteChatService.Antigravity => "Antigravity Remote Chats",
            _ => "Remote Chats",
        };
        var text = new StringBuilder($"{headerTitle} — {string.Join(", ", summary)} (click for menu)");
        text.AppendLine();
        text.Append(RemoteChatLegendLine);
        foreach (var row in rows)
        {
            text.AppendLine();
            string svcName = row.Service == RemoteChatService.Antigravity ? "Antigravity" : "Claude";
            text.Append($"• {row.Server} · {svcName}: {RemoteChatUsage.DescribeRow(row)}");

            foreach (var c in row.Chats.Take(4))
            {
                text.AppendLine();
                string badge = c.State switch
                {
                    RemoteChatState.Waiting => "⚠ WAIT",
                    RemoteChatState.Running => "● RUN",
                    _ => "✓ DONE",
                };
                string repoPrefix = !string.IsNullOrWhiteSpace(c.Repo) ? $"[{c.Repo}] " : "";
                string shortTitle = c.Title.Length <= 48 ? c.Title : c.Title[..45].TrimEnd() + "…";
                text.Append($"    {badge}: {repoPrefix}{shortTitle}");
            }

            if (row.Chats.Count > 4)
            {
                text.AppendLine();
                text.Append($"    +{row.Chats.Count - 4} more…");
            }
        }

        if (!string.IsNullOrWhiteSpace(reading.Error))
        {
            text.AppendLine();
            text.Append($"⚠ {reading.Error}");
        }

        return text.ToString();
    }
}
