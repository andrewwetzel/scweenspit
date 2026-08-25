using static ScweenSpit.Native;

namespace ScweenSpit;

public enum BarEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

/// <summary>
/// Where a bar sits inside the strip Windows reserves for it. Pure rectangle arithmetic, kept apart
/// from <see cref="AppBar"/> so it can be exercised without a window to hang it on.
/// </summary>
public static class BarGeometry
{
    /// <summary>
    /// Shrinks a reserved strip down to the bar that floats inside it: <paramref name="by"/> on the
    /// free sides, and <paramref name="edgeGap"/> on the side it is docked to.
    ///
    /// The two are deliberately not the same number. Whatever the screen edge already holds — the
    /// shell's own reserved band, a monitor bezel, the rounded corner of the display — sits directly
    /// under the docked side, so a gap equal to the one above reads as roughly twice as wide.
    ///
    /// Refuses to collapse the rectangle.
    /// </summary>
    public static RECT Deflate(RECT r, int by, BarEdge edge, int edgeGap)
    {
        if (by <= 0 && edgeGap <= 0) return r;
        if (r.Width <= 2 * by + 8 || r.Height <= 2 * by + 8) return r;

        int left = by, top = by, right = by, bottom = by;
        switch (edge)
        {
            case BarEdge.Bottom: bottom = edgeGap; break;
            case BarEdge.Top: top = edgeGap; break;
            case BarEdge.Left: left = edgeGap; break;
            default: right = edgeGap; break;
        }

        return new RECT { Left = r.Left + left, Top = r.Top + top, Right = r.Right - right, Bottom = r.Bottom - bottom };
    }
}
