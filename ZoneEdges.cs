namespace ScweenSpit;

/// <summary>
/// Treats a layout as rectangles that share edge coordinates, which is what makes the zones
/// resizable: dragging one edge moves every zone that references it, so a 70/30 split stays a
/// clean split instead of developing a gap or an overlap.
/// </summary>
public static class ZoneEdges
{
    /// <summary>"Is this the same edge?" — used when matching an edge to move.</summary>
    public const double Epsilon = 0.0005;

    /// <summary>
    /// "Was this meant to be the same edge?" — used when cleaning up a config. Deliberately wider
    /// than <see cref="Epsilon"/>: values closer together than this are indistinguishable on screen
    /// (0.002 is under 4px on a 1920px display) but far enough apart to escape Epsilon matching,
    /// which is precisely the gap that produced one draggable divider moving only half the zones.
    /// </summary>
    public const double MergeTolerance = 0.002;

    /// <summary>Smallest a zone may be squeezed to, as a fraction of the monitor.</summary>
    public const double MinExtent = 0.05;

    public static List<double> Vertical(List<FracRect> zones) => Internal(zones.SelectMany(z => new[] { z.L, z.R }));

    public static List<double> Horizontal(List<FracRect> zones) => Internal(zones.SelectMany(z => new[] { z.T, z.B }));

    private static List<double> Internal(IEnumerable<double> all)
    {
        // Collapsed by the same tolerance the matcher uses, rather than by fixed rounding buckets:
        // bucketing can split two values that Near() considers identical, and join two it does not.
        var sorted = all.Where(v => v > Epsilon && v < 1 - Epsilon).OrderBy(v => v);

        var distinct = new List<double>();
        foreach (var v in sorted)
            if (distinct.Count == 0 || v - distinct[^1] >= Epsilon) distinct.Add(v);

        return distinct;
    }

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

        // Zones narrower than MinExtent (a hand-written many-column layout, or plain floating point
        // at exactly 20 equal columns) make the bounds cross. Pin to the edge itself: the divider
        // simply will not move. Using the midpoint instead can land outside [0,1] and get committed.
        if (min > max) min = max = edge;

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

    /// <summary>
    /// Collapses edges that are within <see cref="Epsilon"/> of each other onto a single shared
    /// value, so two nearly-identical hand-written coordinates cannot present as one draggable
    /// divider that only moves half the zones — the exact overlap this class exists to prevent.
    /// Runs snap onto a real member rather than their mean: a mean can land outside Near() of the
    /// values it replaced.
    /// </summary>
    public static void Canonicalise(List<FracRect> zones)
    {
        Snap(zones, vertical: true);
        Snap(zones, vertical: false);
    }

    private static void Snap(List<FracRect> zones, bool vertical)
    {
        var values = zones
            .SelectMany(z => vertical ? new[] { z.L, z.R } : new[] { z.T, z.B })
            .Distinct().OrderBy(v => v).ToList();
        if (values.Count == 0) return;

        var canonical = new List<double> { values[0] };
        foreach (var v in values.Skip(1))
            if (v - canonical[^1] > MergeTolerance) canonical.Add(v);

        double Map(double v)
        {
            double best = canonical[0];
            foreach (var c in canonical)
                if (Math.Abs(c - v) < Math.Abs(best - v)) best = c;
            return best;
        }

        foreach (var z in zones)
        {
            if (vertical) { z.L = Map(z.L); z.R = Map(z.R); }
            else          { z.T = Map(z.T); z.B = Map(z.B); }
        }
    }

    public static List<FracRect> Clone(List<FracRect> zones) =>
        zones.Select(z => new FracRect(z.L, z.T, z.R, z.B)).ToList();
}
