using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;

namespace ScweenSpit;

/// <summary>
/// Draws the claude.ai usage limits as a stack of thin bars inside the status cluster.
///
/// The palette and the thresholds it steps at are taken from claude-usage-widget by Niccolò Sabato
/// (MIT — see THIRD-PARTY-NOTICES.md), so a glance carries the same meaning here as it does there:
/// every bar is coloured by its own consumption rather than by which limit it is, which is what lets
/// three bars be read at once without a legend.
///
/// Drawn rather than hosted. The original is a tkinter window that can be parked over the taskbar;
/// embedding it in this bar would mean reparenting a foreign HWND into a surface that repaints every
/// second — and a Python runtime to keep alive besides. Twenty lines of GDI+ inherit the bar's
/// theme, DPI and hover states for free.
/// </summary>
internal static class UsageStrip
{
    private static readonly Color Low = Color.FromArgb(0x2A, 0x78, 0xD6);   // blue  — plenty left
    private static readonly Color Mid = Color.FromArgb(0xFA, 0xB2, 0x19);   // amber — over half
    private static readonly Color High = Color.FromArgb(0xD0, 0x3B, 0x3B);  // red   — nearly out

    private const int BarThickness = 5;
    private const int BarGap = 4;
    private const int LabelHeight = 14;

    /// <summary>Room the strip needs along the bar. Nothing when there is nothing to show.</summary>
    public static int Extent(bool vertical) => vertical ? 46 : 78;

    /// <summary>The colour a bar takes at a given consumption.</summary>
    private static Color Fill(int percent) => percent >= 85 ? High : percent >= 50 ? Mid : Low;

    public static void Paint(Graphics g, Rectangle area, UsageReading? reading)
    {
        var limits = reading?.Limits ?? [];
        int count = Math.Max(1, limits.Count);

        int width = Math.Max(16, area.Width - 12);
        int content = LabelHeight + count * BarThickness + (count - 1) * BarGap;

        int x = area.X + (area.Width - width) / 2;
        int y = area.Y + Math.Max(2, (area.Height - content) / 2);

        // The headline figure is the session limit — the one that actually stops you working.
        var headline = limits.Count > 0 ? limits[0] : null;
        DrawLabel(g, new Rectangle(area.X, y, area.Width, LabelHeight), headline, reading);

        y += LabelHeight;

        if (limits.Count == 0)
        {
            // An empty track still says "this is a usage strip", where a blank gap says nothing —
            // but only if it can be seen. The ordinary trough is barely a shade off the bar itself.
            Placeholder(g, new Rectangle(x, y, width, BarThickness));
            return;
        }

        foreach (var limit in limits)
        {
            Track(g, new Rectangle(x, y, width, BarThickness), limit);
            y += BarThickness + BarGap;
        }
    }

    private static void DrawLabel(Graphics g, Rectangle area, UsageLimit? headline, UsageReading? reading)
    {
        using var font = Theme.Face(8f, FontStyle.Bold);
        using var centred = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        // Nothing here has room for a sentence, so each state gets the shortest phrase that still
        // means something on its own. "key" was a fragment of an explanation living in the tooltip,
        // which reads as a rendering fault rather than as a prompt.
        var (text, colour) = headline is not null
            ? ($"{headline.Percent}%", Fill(headline.Percent))
            : reading is { NeedsKey: true } ? ("set up", Theme.Accent)
            : reading is { Error: not null } ? ("error", High)
            : ("···", Theme.Muted);

        using var brush = new SolidBrush(colour);
        g.DrawString(text, font, brush, area, centred);
    }

    /// <summary>A track with nothing in it yet, drawn light enough to be visible on the bar.</summary>
    private static void Placeholder(Graphics g, Rectangle bar)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var trough = new SolidBrush(Theme.Divider))
            Rounded(g, bar, trough);

        g.SmoothingMode = previous;
    }

    /// <summary>One limit: an unfilled track with the used portion drawn over it.</summary>
    private static void Track(Graphics g, Rectangle bar, UsageLimit? limit)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var trough = new SolidBrush(Theme.Raised))
            Rounded(g, bar, trough);

        if (limit is not null && limit.Percent > 0)
        {
            // Always at least a sliver: 1% that renders as nothing reads as 0%.
            int used = Math.Max(BarThickness, (int)Math.Round(bar.Width * limit.Percent / 100.0));
            using var fill = new SolidBrush(Fill(limit.Percent));
            Rounded(g, new Rectangle(bar.X, bar.Y, Math.Min(used, bar.Width), bar.Height), fill);
        }

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

    /// <summary>The whole reading in words, for the tooltip — where the numbers actually live.</summary>
    public static string Tip(UsageReading? reading)
    {
        if (reading is null) return "Claude usage — starting up";
        if (reading.NeedsKey) return "Claude usage — a session key is needed (Settings ▸ Claude usage)";
        if (reading.Error is not null) return $"Claude usage — {reading.Error}";
        if (reading.Limits.Count == 0) return "Claude usage — no limits reported";

        var text = new StringBuilder();
        foreach (var limit in reading.Limits)
        {
            if (text.Length > 0) text.AppendLine();
            text.Append($"{limit.Label}: {limit.Percent}%");

            var countdown = ClaudeUsage.Countdown(limit.ResetsAt);
            if (countdown.Length > 0) text.Append($"  ({countdown})");
        }

        return text.ToString();
    }
}
