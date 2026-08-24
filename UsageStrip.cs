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

    private const int BarThickness = 6;

    /// <summary>Room the strip needs along the bar. Wide enough to put a figure beside every limit.</summary>
    public static int Extent(bool vertical) => vertical ? 68 : 112;

    /// <summary>The colour a bar takes at a given consumption.</summary>
    private static Color Fill(int percent) => percent >= 85 ? High : percent >= 50 ? Mid : Low;

    public static void Paint(Graphics g, Rectangle area, UsageReading? reading)
    {
        var limits = reading?.Limits ?? [];

        if (limits.Count == 0) { PaintState(g, area, reading); return; }

        // A row per limit: the track, and its own figure beside or beneath it. One headline
        // percentage cannot say which limit it belongs to, and three bars without numbers cannot
        // say how full they are.
        bool vertical = area.Height > area.Width;
        int rows = limits.Count;
        int rowHeight = Math.Max(10, Math.Min(vertical ? 22 : 15, (area.Height - 4) / rows));
        int top = area.Y + Math.Max(2, (area.Height - rows * rowHeight) / 2);

        using var font = Theme.Face(7.5f, FontStyle.Bold);

        for (int i = 0; i < rows; i++)
        {
            var limit = limits[i];
            var row = new Rectangle(area.X + 4, top + i * rowHeight, area.Width - 8, rowHeight);

            Rectangle track, label;
            if (vertical)
            {
                // Too narrow to sit side by side, so the figure goes under its track.
                track = new Rectangle(row.X, row.Y + 1, row.Width, BarThickness);
                label = new Rectangle(row.X, row.Y + BarThickness + 1, row.Width, rowHeight - BarThickness - 2);
            }
            else
            {
                const int figure = 30;
                track = new Rectangle(row.X, row.Y + (rowHeight - BarThickness) / 2,
                                      Math.Max(8, row.Width - figure - 4), BarThickness);
                label = new Rectangle(row.Right - figure, row.Y, figure, rowHeight);
            }

            Track(g, track, limit);

            using var brush = new SolidBrush(Fill(limit.Percent));
            using var format = new StringFormat
            {
                Alignment = vertical ? StringAlignment.Center : StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            g.DrawString($"{limit.Percent}%", font, brush, label, format);
        }
    }

    /// <summary>Nothing to plot yet: say which of the reasons it is, in the space a limit would use.</summary>
    private static void PaintState(Graphics g, Rectangle area, UsageReading? reading)
    {
        using var font = Theme.Face(8f, FontStyle.Bold);
        using var centred = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        var (text, colour) = reading is { NeedsKey: true }
            ? (ClaudeUsage.HasKey ? "expired" : "set up", Theme.Accent)
            : reading is { Error: not null } ? ("error", High)
            : ("···", Theme.Muted);

        int width = Math.Max(16, area.Width - 12);
        var label = new Rectangle(area.X, area.Y, area.Width, area.Height - BarThickness - 4);

        using var brush = new SolidBrush(colour);
        g.DrawString(text, font, brush, label, centred);

        Placeholder(g, new Rectangle(area.X + (area.Width - width) / 2,
                                     area.Bottom - BarThickness - 4, width, BarThickness));
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
        // The specific reason first: "expired" and "never entered" are the same symptom with very
        // different answers, and collapsing them sends people to re-paste a key that is already there.
        if (reading.NeedsKey)
            return $"Claude usage — {reading.Error ?? "a session key is needed"} (Settings ▸ Claude usage)";

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
