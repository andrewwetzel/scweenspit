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
    private static readonly Color ClaudeTitleColour = Color.FromArgb(130, 185, 250);
    private static readonly Color AntigravityTitleColour = Color.FromArgb(196, 160, 255);
    private static readonly Color BambuTitleColour = Color.FromArgb(120, 232, 160);
    private static readonly Color RemoteChatsTitleColour = Color.FromArgb(145, 215, 255);

    /// <summary>Room the Claude strip needs along the bar.</summary>
    public static int Extent(bool vertical, int maxRowsPerColumn = 3, int barBreadth = 50, int scalePercent = 100)
    {
        int count = ClaudeUsage.Current?.Limits.Count is > 0 and var c ? c : 2;
        return MeterStrip.UsageExtent(vertical, count, maxRowsPerColumn, barBreadth, scalePercent);
    }

    /// <summary>Room the Antigravity strip needs along the bar.</summary>
    public static int AntigravityExtent(bool vertical, int maxRowsPerColumn = 3, int barBreadth = 50, int scalePercent = 100)
    {
        int count = AntigravityUsage.Current?.Limits.Count is > 0 and var c ? c : 2;
        return MeterStrip.UsageExtent(vertical, count, maxRowsPerColumn, barBreadth, scalePercent);
    }

    /// <summary>Room the Bambu printer strip needs along the bar.</summary>
    public static int BambuExtent(bool vertical, int maxRowsPerColumn = 3, int barBreadth = 50, int scalePercent = 100)
    {
        int count = BambuUsage.Current?.Printers.Count is > 0 and var c ? c : BambuUsage.ConfiguredPrinterCount;
        return MeterStrip.BambuExtent(vertical, count, maxRowsPerColumn, barBreadth, scalePercent);
    }

    /// <summary>Room the remote server chats strip needs along the bar.</summary>
    public static int RemoteChatsExtent(bool vertical, int maxRowsPerColumn = 3, int barBreadth = 50, int scalePercent = 100)
    {
        int count = RemoteChatUsage.Current?.Rows.Count is > 0 and var c ? c : RemoteChatUsage.ConfiguredRowCount;
        return MeterStrip.BambuExtent(vertical, count, maxRowsPerColumn, barBreadth, scalePercent);
    }

    public static void Paint(Graphics g, Rectangle area, UsageReading? reading, int maxRowsPerColumn = 3)
    {
        var limits = reading?.Limits ?? [];
        if (limits.Count == 0) { PaintState(g, area, reading, isAntigravity: false); return; }

        bool vertical = area.Height > area.Width;
        int maxRows = Math.Clamp(maxRowsPerColumn, 1, 8);
        int cols = MeterStrip.ColumnsFor(vertical, limits.Count, maxRows);
        int rowsPerCol = (int)Math.Ceiling(limits.Count / (double)cols);
        bool useHeaderRow = !vertical && (rowsPerCol + 1 <= maxRows) && (area.Height - 2) / (rowsPerCol + 1) >= 11;

        var meters = new List<Meter>(limits.Count);
        for (int i = 0; i < limits.Count; i++)
        {
            var l = limits[i];
            string displayLabel = vertical
                ? (l.Label.Equals("Session", StringComparison.OrdinalIgnoreCase) ? "Cl 5h"
                 : l.Label.Equals("Weekly", StringComparison.OrdinalIgnoreCase) ? "Cl 7d"
                 : l.Label)
                : useHeaderRow
                    ? l.Label
                    : (l.Label.Equals("Session", StringComparison.OrdinalIgnoreCase) ? "Claude 5h"
                     : l.Label.Equals("Weekly", StringComparison.OrdinalIgnoreCase) ? "Claude 7d"
                     : l.Label);

            bool highlightFirst = !vertical && !useHeaderRow && i == 0;
            string detail = ClaudeUsage.Countdown(l.ResetsAt) is { Length: > 0 } left
                ? $"{l.Label}: {l.Percent}%  ({left})"
                : $"{l.Label}: {l.Percent}%";

            meters.Add(new Meter(
                displayLabel,
                l.Percent,
                detail,
                LabelColour: highlightFirst ? ClaudeTitleColour : null,
                BoldLabel: highlightFirst,
                Subtext: ClaudeUsage.ShortCountdown(l.ResetsAt)));
        }

        MeterStrip.Paint(
            g, area, meters,
            lowColour: MeterStrip.Low,
            headerTitle: useHeaderRow ? "Claude" : null,
            headerTag: null,
            headerColour: ClaudeTitleColour,
            sparkleGlyph: false,
            maxRowsPerColumn: maxRowsPerColumn);
    }

    public static void PaintAntigravity(Graphics g, Rectangle area, UsageReading? reading, int maxRowsPerColumn = 3)
    {
        var limits = reading?.Limits ?? [];
        if (limits.Count == 0) { PaintState(g, area, reading, isAntigravity: true); return; }

        bool vertical = area.Height > area.Width;
        int maxRows = Math.Clamp(maxRowsPerColumn, 1, 8);
        int cols = MeterStrip.ColumnsFor(vertical, limits.Count, maxRows);
        int rowsPerCol = (int)Math.Ceiling(limits.Count / (double)cols);
        bool useHeaderRow = !vertical && (rowsPerCol + 1 <= maxRows) && (area.Height - 2) / (rowsPerCol + 1) >= 11;

        var meters = new List<Meter>(limits.Count);
        for (int i = 0; i < limits.Count; i++)
        {
            var l = limits[i];
            string displayLabel = vertical || !useHeaderRow
                ? (l.Label.Equals("Session", StringComparison.OrdinalIgnoreCase) ? "AG 5h"
                 : l.Label.Equals("Weekly", StringComparison.OrdinalIgnoreCase) ? "AG 7d"
                 : l.Label)
                : l.Label;

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

        MeterStrip.Paint(
            g, area, meters,
            lowColour: MeterStrip.VioletLow,
            headerTitle: "Antigravity",
            headerTag: "Pro·Flash",
            headerColour: AntigravityTitleColour,
            sparkleGlyph: true,
            maxRowsPerColumn: maxRowsPerColumn);
    }

    public static void PaintBambu(Graphics g, Rectangle area, BambuReading? reading, int maxRowsPerColumn = 3)
    {
        var printers = reading?.Printers ?? [];
        if (printers.Count == 0) { PaintBambuState(g, area, reading); return; }

        var meters = new List<Meter>(printers.Count);
        foreach (var p in printers)
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

                BambuPrinterState.Paused => (
                    Math.Clamp(p.Percent, 8, 100),
                    MeterStrip.Mid,
                    (Color?)Color.FromArgb(250, 205, 110),
                    true,
                    $"{p.Percent}%",
                    "Pause",
                    (Color?)MeterStrip.Mid),

                BambuPrinterState.Finished => (
                    100,
                    MeterStrip.BambuDone,
                    (Color?)Color.FromArgb(145, 220, 195),
                    false,
                    "100%",
                    "Done",
                    (Color?)Color.FromArgb(130, 220, 190)),

                BambuPrinterState.Offline => (
                    0,
                    Theme.Muted,
                    (Color?)null,
                    false,
                    "—",
                    "Off",
                    (Color?)Theme.Muted),

                _ => (
                    0,
                    Theme.Muted,
                    (Color?)null,
                    false,
                    "—",
                    "Idle",
                    (Color?)Theme.Muted),
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
            headerTag: $"{printers.Count}",
            headerColour: BambuTitleColour,
            sparkleGlyph: false,
            maxRowsPerColumn: maxRowsPerColumn);
    }

    public static void PaintRemoteChats(Graphics g, Rectangle area, RemoteChatsReading? reading, int maxRowsPerColumn = 3)
    {
        var rows = reading?.Rows ?? [];
        if (rows.Count == 0) { PaintRemoteChatsState(g, area, reading); return; }

        int totalWaiting = 0, totalChats = 0;
        var meters = new List<Meter>(rows.Count);

        foreach (var r in rows)
        {
            totalWaiting += r.WaitingCount;
            totalChats += r.TotalCount;

            string detail = $"{r.Label}: {RemoteChatUsage.DescribeRow(r)}";
            Color svcBar = r.Service == RemoteChatService.Antigravity ? MeterStrip.VioletLow : MeterStrip.Low;
            Color svcTitle = r.Service == RemoteChatService.Antigravity ? AntigravityTitleColour : ClaudeTitleColour;

            int pct;
            Color barCol;
            Color? lblCol;
            bool bold;
            string figText;
            string subText;
            Color? subCol;

            if (r.WaitingCount > 0)
            {
                pct = 100;
                barCol = MeterStrip.Mid;
                lblCol = Color.FromArgb(255, 210, 115);
                bold = true;
                figText = $"{r.TotalCount}!";
                subText = r.RunningCount > 0 ? $"{r.WaitingCount}! {r.RunningCount}r"
                        : r.DoneCount > 0 ? $"{r.WaitingCount}! {r.DoneCount}✓"
                        : $"{r.WaitingCount} wait";
                subCol = MeterStrip.Mid;
            }
            else if (r.RunningCount > 0)
            {
                pct = Math.Clamp(r.RunningCount * 35 + r.DoneCount * 15, 25, 100);
                barCol = svcBar;
                lblCol = svcTitle;
                bold = true;
                figText = $"{r.TotalCount}";
                subText = r.DoneCount > 0 ? $"{r.RunningCount}r {r.DoneCount}✓" : $"{r.RunningCount} run";
                subCol = svcTitle;
            }
            else if (r.DoneCount > 0)
            {
                pct = 100;
                barCol = MeterStrip.BambuDone;
                lblCol = Color.FromArgb(145, 220, 195);
                bold = false;
                figText = $"{r.TotalCount}";
                subText = $"{r.DoneCount} done";
                subCol = Color.FromArgb(130, 220, 190);
            }
            else if (r.Online)
            {
                pct = 0;
                barCol = Theme.Muted;
                lblCol = null;
                bold = false;
                figText = "0";
                subText = "Idle";
                subCol = Theme.Muted;
            }
            else
            {
                pct = 0;
                barCol = Theme.Muted;
                lblCol = null;
                bold = false;
                figText = "—";
                subText = "Off";
                subCol = Theme.Muted;
            }

            meters.Add(new Meter(
                r.Label,
                pct,
                detail,
                LabelColour: lblCol,
                BoldLabel: bold,
                Subtext: subText,
                BarColour: barCol,
                FigureText: figText,
                SubtextColour: subCol));
        }

        string headerTag = totalWaiting > 0
            ? $"{totalWaiting}! · {totalChats}"
            : $"{totalChats} live";
        Color hdrCol = totalWaiting > 0 ? Color.FromArgb(255, 210, 115) : RemoteChatsTitleColour;

        MeterStrip.Paint(
            g, area, meters,
            lowColour: MeterStrip.Low,
            headerTitle: "Remote Chats",
            headerTag: headerTag,
            headerColour: hdrCol,
            sparkleGlyph: false,
            maxRowsPerColumn: maxRowsPerColumn);
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

    private static void PaintRemoteChatsState(Graphics g, Rectangle area, RemoteChatsReading? reading)
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

        (string text, Color colour) = !RemoteChatUsage.Enabled
            ? ("Chats · set up", RemoteChatsTitleColour)
            : reading is { NeedsAuth: true }
                ? ("Chats · sign in", RemoteChatsTitleColour)
                : reading is { Error: not null, Settled: true }
                    ? ("Chats · error", MeterStrip.High)
                    : ("Chats ···", RemoteChatsTitleColour);

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
            text.Append($"{limit.Label}: {limit.Percent}%");

            var countdown = ClaudeUsage.Countdown(limit.ResetsAt);
            if (countdown.Length > 0) text.Append($"  ({countdown})");
        }

        return text.ToString();
    }

    /// <summary>The whole Antigravity reading in words (Gemini Pro &amp; Flash session and weekly limits).</summary>
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
            : "Antigravity — Gemini Pro & Flash");

        foreach (var limit in reading.Limits)
        {
            text.AppendLine();
            text.Append($"{limit.Label}: {limit.Percent}%");

            var countdown = ClaudeUsage.Countdown(limit.ResetsAt);
            if (countdown.Length > 0) text.Append($"  ({countdown})");
        }

        return text.ToString();
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

    /// <summary>Full tooltip breakdown of remote server chats across Claude Code and Antigravity.</summary>
    public static string RemoteChatsTip(RemoteChatsReading? reading)
    {
        if (!RemoteChatUsage.Enabled)
            return "Remote server chats — switched off (Settings ▸ Remote chats)";
        if (reading is null)
            return "Remote server chats — starting up";
        if (reading.NeedsAuth)
            return $"Remote server chats — {reading.Error ?? "sign in under Claude usage or Antigravity usage"}";
        if (reading.Rows.Count == 0 && reading.Error is not null)
            return $"Remote server chats — {reading.Error}";
        if (reading.Rows.Count == 0)
            return "Remote server chats — no enabled servers found yet";

        int waiting = reading.Rows.Sum(r => r.WaitingCount);
        int running = reading.Rows.Sum(r => r.RunningCount);
        int done = reading.Rows.Sum(r => r.DoneCount);

        var summary = new List<string>();
        if (waiting > 0) summary.Add($"{waiting} need input");
        if (running > 0) summary.Add($"{running} running");
        if (done > 0) summary.Add($"{done} done");
        if (summary.Count == 0) summary.Add("all idle");

        var text = new StringBuilder($"Remote Chats — {string.Join(", ", summary)} (click for menu)");
        foreach (var row in reading.Rows)
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

        return text.ToString();
    }
}
