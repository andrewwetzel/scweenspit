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
    /// <summary>Room the strip needs along the bar.</summary>
    public static int Extent(bool vertical) => MeterStrip.Extent(vertical);

    public static void Paint(Graphics g, Rectangle area, UsageReading? reading)
    {
        var limits = reading?.Limits ?? [];
        if (limits.Count == 0) { PaintState(g, area, reading); return; }

        // The names carry the reading as much as the bars do: three tracks and three percentages
        // cannot say which limit is which, and the one that matters is usually not the fullest.
        MeterStrip.Paint(g, area, limits.Select(l => new Meter(
            l.Label,
            l.Percent,
            ClaudeUsage.Countdown(l.ResetsAt) is { Length: > 0 } left ? $"{l.Label}: {l.Percent}%  ({left})"
                                                                      : $"{l.Label}: {l.Percent}%")).ToList());
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
            : reading is { Error: not null, Settled: true } ? ("error", MeterStrip.High)
            : ("···", Theme.Muted);

        int width = Math.Max(16, area.Width - 12);
        var label = new Rectangle(area.X, area.Y, area.Width, area.Height - MeterStrip.BarThickness - 4);

        using var brush = new SolidBrush(colour);
        g.DrawString(text, font, brush, label, centred);

        MeterStrip.Placeholder(g, new Rectangle(area.X + (area.Width - width) / 2,
                                                area.Bottom - MeterStrip.BarThickness - 4, width,
                                                MeterStrip.BarThickness));
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
