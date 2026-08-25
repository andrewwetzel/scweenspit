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
