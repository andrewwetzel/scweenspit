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

    /// <summary>Room the strip needs along the bar.</summary>
    public static int Extent(bool vertical) => MeterStrip.UsageExtent(vertical);

    /// <summary>Room the Bambu printer strip needs along the bar.</summary>
    public static int BambuExtent(bool vertical) =>
        MeterStrip.BambuExtent(vertical, BambuUsage.ConfiguredPrinterCount);

    public static void Paint(Graphics g, Rectangle area, UsageReading? reading)
    {
        var limits = reading?.Limits ?? [];
        if (limits.Count == 0) { PaintState(g, area, reading, isAntigravity: false); return; }

        bool vertical = area.Height > area.Width;
        bool useHeaderRow = !vertical && limits.Count <= 2;

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
            sparkleGlyph: false);
    }

    public static void PaintAntigravity(Graphics g, Rectangle area, UsageReading? reading)
    {
        var limits = reading?.Limits ?? [];
        if (limits.Count == 0) { PaintState(g, area, reading, isAntigravity: true); return; }

        bool vertical = area.Height > area.Width;

        var meters = limits.Select(l =>
        {
            string displayLabel = vertical
                ? (l.Label.Equals("Session", StringComparison.OrdinalIgnoreCase) ? "AG 5h"
                 : l.Label.Equals("Weekly", StringComparison.OrdinalIgnoreCase) ? "AG 7d"
                 : l.Label)
                : l.Label;

            string detail = ClaudeUsage.Countdown(l.ResetsAt) is { Length: > 0 } left
                ? $"{l.Label}: {l.Percent}%  ({left})"
                : $"{l.Label}: {l.Percent}%";

            return new Meter(
                displayLabel,
                l.Percent,
                detail,
                Subtext: ClaudeUsage.ShortCountdown(l.ResetsAt));
        }).ToList();

        MeterStrip.Paint(
            g, area, meters,
            lowColour: MeterStrip.VioletLow,
            headerTitle: "Antigravity",
            headerTag: "Pro·Flash",
            headerColour: AntigravityTitleColour,
            sparkleGlyph: true);
    }

    public static void PaintBambu(Graphics g, Rectangle area, BambuReading? reading)
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
            sparkleGlyph: false);
    }

    /// <summary>Nothing to plot yet: say which of the reasons it is, in the space a limit would use.</summary>
    private static void PaintState(Graphics g, Rectangle area, UsageReading? reading, bool isAntigravity)
    {
        using var font = Theme.Face(7.5f, FontStyle.Bold);
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

        int width = Math.Max(16, area.Width - 12);
        var label = new Rectangle(area.X, area.Y, area.Width, area.Height - MeterStrip.BarThickness - 4);

        using var brush = new SolidBrush(colour);
        g.DrawString(text, font, brush, label, centred);

        MeterStrip.Placeholder(g, new Rectangle(area.X + (area.Width - width) / 2,
                                                area.Bottom - MeterStrip.BarThickness - 4, width,
                                                MeterStrip.BarThickness));
    }

    private static void PaintBambuState(Graphics g, Rectangle area, BambuReading? reading)
    {
        using var font = Theme.Face(7.5f, FontStyle.Bold);
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

        int width = Math.Max(16, area.Width - 12);
        var label = new Rectangle(area.X, area.Y, area.Width, area.Height - MeterStrip.BarThickness - 4);

        using var brush = new SolidBrush(colour);
        g.DrawString(text, font, brush, label, centred);

        MeterStrip.Placeholder(g, new Rectangle(area.X + (area.Width - width) / 2,
                                                area.Bottom - MeterStrip.BarThickness - 4, width,
                                                MeterStrip.BarThickness));
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
}
