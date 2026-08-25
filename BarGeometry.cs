using System.Drawing;
using static ScweenSpit.Native;

namespace ScweenSpit;

public enum BarEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

/// <summary>
/// Where a bar sits inside the strip Windows reserves for it. Pure rectangle arithmetic, kept apart
/// from <see cref="AppBar"/> so it can be exercised without a window to hang it on.
/// </summary>
public static class BarGeometry
{
    /// <summary>Nothing may shrink a bar below this, however cramped the strip it sits in.</summary>
    private const int MinExtent = 8;

    /// <summary>
    /// Where a panel belonging to a button on a bar goes: lined up with the button along the bar,
    /// clear of the bar on the side the desktop is, and never off the display it came from.
    ///
    /// Shared, because a hover preview and the Start menu are the same problem — and a panel that
    /// runs off the edge of an ultrawide is the failure both of them have.
    /// </summary>
    public static Point Beside(Size panel, Rectangle button, Rectangle bar, BarEdge edge,
                               RECT monitor, int gap)
    {
        (int x, int y) = edge switch
        {
            BarEdge.Bottom => (button.Left, bar.Top - gap - panel.Height),
            BarEdge.Top => (button.Left, bar.Bottom + gap),
            BarEdge.Left => (bar.Right + gap, button.Top),
            _ => (bar.Left - gap - panel.Width, button.Top),
        };

        // A panel too big for the space it has must still land on this display, not the one next
        // door and not off the top of the desktop. The low bound wins, so it is never pushed to a
        // negative width's worth of room.
        return new Point(Fit(x, monitor.Left + gap, monitor.Right - gap - panel.Width),
                         Fit(y, monitor.Top + gap, monitor.Bottom - gap - panel.Height));
    }

    private static int Fit(int value, int low, int high) => Math.Max(low, Math.Min(value, high));

    /// <summary>
    /// Shrinks a reserved strip down to the bar that floats inside it. The three sides get their
    /// own gap, because they are not doing the same job:
    ///
    /// <paramref name="edgeGap"/> is the side docked to the screen. Whatever the edge already holds
    /// — a band Windows still reserves, the bezel, the rounded corner of the panel — sits directly
    /// under it, so a gap equal to the others reads as roughly twice as wide.
    ///
    /// <paramref name="open"/> is the side facing the desktop, the only one seen against a window.
    ///
    /// <paramref name="ends"/> is the two ends of the strip, which have nothing beside them at all
    /// and so need the least.
    ///
    /// Refuses to collapse the rectangle.
    /// </summary>
    public static RECT Deflate(RECT r, BarEdge edge, int open, int ends, int edgeGap)
    {
        if (open <= 0 && ends <= 0 && edgeGap <= 0) return r;

        int left, top, right, bottom;
        switch (edge)
        {
            case BarEdge.Bottom: (left, top, right, bottom) = (ends, open, ends, edgeGap); break;
            case BarEdge.Top: (left, top, right, bottom) = (ends, edgeGap, ends, open); break;
            case BarEdge.Left: (left, top, right, bottom) = (edgeGap, ends, open, ends); break;
            default: (left, top, right, bottom) = (open, ends, edgeGap, ends); break;
        }

        if (r.Width - left - right < MinExtent || r.Height - top - bottom < MinExtent) return r;

        return new RECT { Left = r.Left + left, Top = r.Top + top, Right = r.Right - right, Bottom = r.Bottom - bottom };
    }
}
