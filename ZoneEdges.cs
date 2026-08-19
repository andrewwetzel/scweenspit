namespace ScweenSpit;

/// <summary>
/// Treats a layout as rectangles that share edge coordinates, which is what makes the zones
/// resizable: dragging one edge moves every zone that references it, so a 70/30 split stays a
/// clean split instead of developing a gap or an overlap.
/// </summary>
public static class ZoneEdges
{
    private const double Epsilon = 0.0005;

    /// <summary>Smallest a zone may be squeezed to, as a fraction of the monitor.</summary>
    public const double MinExtent = 0.05;

    public static List<double> Vertical(List<FracRect> zones) => Internal(zones.SelectMany(z => new[] { z.L, z.R }));

    public static List<double> Horizontal(List<FracRect> zones) => Internal(zones.SelectMany(z => new[] { z.T, z.B }));

    private static List<double> Internal(IEnumerable<double> all) =>
        all.Where(v => v > Epsilon && v < 1 - Epsilon)
           .GroupBy(v => Math.Round(v, 3))
           .Select(g => g.First())
           .OrderBy(v => v)
           .ToList();

    /// <summary>
    /// How far an edge may travel before it would crush a neighbouring zone. Returns the inclusive
    /// range the edge is allowed to move within.
    /// </summary>
    public static (double Min, double Max) Limits(List<FracRect> zones, double edge, bool vertical)
    {
        double min = 0, max = 1;
        foreach (var z in zones)
        {
            double lo = vertical ? z.L : z.T, hi = vertical ? z.R : z.B;

            // A zone whose far side is this edge may not be squashed below MinExtent...
            if (Near(hi, edge)) min = Math.Max(min, lo + MinExtent);
            // ...nor one whose near side is this edge.
            if (Near(lo, edge)) max = Math.Min(max, hi - MinExtent);
        }
        return (min, max);
    }

    /// <summary>Moves every reference to <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static void Move(List<FracRect> zones, double from, double to, bool vertical)
    {
        foreach (var z in zones)
        {
            if (vertical)
            {
                if (Near(z.L, from)) z.L = to;
                if (Near(z.R, from)) z.R = to;
            }
            else
            {
                if (Near(z.T, from)) z.T = to;
                if (Near(z.B, from)) z.B = to;
            }
        }
    }

    public static bool Near(double a, double b) => Math.Abs(a - b) < Epsilon;

    public static List<FracRect> Clone(List<FracRect> zones) =>
        zones.Select(z => new FracRect(z.L, z.T, z.R, z.B)).ToList();
}
