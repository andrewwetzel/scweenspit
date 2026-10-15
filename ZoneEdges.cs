namespace ScweenSpit;

/// <summary>
/// A draggable internal divider segment. <see cref="Position"/> is the X coordinate when
/// <see cref="Vertical"/> is true (spanning Y from <see cref="Start"/> to <see cref="End"/>), or
/// the Y coordinate when horizontal (spanning X from <see cref="Start"/> to <see cref="End"/>).
/// </summary>
public readonly record struct Divider(bool Vertical, double Position, double Start, double End)
{
    public bool SameAs(Divider other) =>
        Vertical == other.Vertical &&
        ZoneEdges.Near(Position, other.Position) &&
        ZoneEdges.Near(Start, other.Start) &&
        ZoneEdges.Near(End, other.End);
}

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
    /// Returns every draggable internal divider segment in <paramref name="zones"/>. Disjoint
    /// collinear splits (for instance, horizontal splits inside column 1 and column 4 with
    /// unsplit columns between them) become separate <see cref="Divider"/>s so each only draws and
    /// moves within the columns it actually divides.
    ///
    /// When <paramref name="mergeTouching"/> is false, segments that merely meet at a 4-way cross
    /// junction are kept separate so Shift-dragging can move one half independently.
    /// </summary>
    public static List<Divider> Dividers(List<FracRect> zones, bool mergeTouching = true)
    {
        var list = new List<Divider>();
        AddDividersForAxis(zones, vertical: true, mergeTouching, list);
        AddDividersForAxis(zones, vertical: false, mergeTouching, list);
        return list;
    }

    private static void AddDividersForAxis(List<FracRect> zones, bool vertical, bool mergeTouching, List<Divider> result)
    {
        var coords = vertical ? Vertical(zones) : Horizontal(zones);
        foreach (var pos in coords)
        {
            var spans = zones
                .Where(z => vertical ? (Near(z.L, pos) || Near(z.R, pos))
                                     : (Near(z.T, pos) || Near(z.B, pos)))
                .Select(z => vertical ? (Start: Math.Min(z.T, z.B), End: Math.Max(z.T, z.B))
                                      : (Start: Math.Min(z.L, z.R), End: Math.Max(z.L, z.R)))
                .Where(s => s.End - s.Start > Epsilon)
                .OrderBy(s => s.Start)
                .ThenBy(s => s.End)
                .ToList();

            if (spans.Count == 0) continue;

            double curStart = spans[0].Start, curEnd = spans[0].End;
            for (int i = 1; i < spans.Count; i++)
            {
                var (s, e) = spans[i];
                bool connected = mergeTouching
                    ? s <= curEnd + MergeTolerance
                    : s < curEnd - Epsilon;

                if (connected)
                {
                    curEnd = Math.Max(curEnd, e);
                }
                else
                {
                    result.Add(new Divider(vertical, pos, curStart, curEnd));
                    curStart = s;
                    curEnd = e;
                }
            }
            result.Add(new Divider(vertical, pos, curStart, curEnd));
        }
    }

    private static bool Participates(FracRect z, Divider d)
    {
        double pLo = d.Vertical ? z.T : z.L;
        double pHi = d.Vertical ? z.B : z.R;
        return Math.Min(pHi, d.End) - Math.Max(pLo, d.Start) > Epsilon;
    }

    /// <summary>
    /// How far a divider may travel before it would crush a neighbouring zone. Returns the inclusive
    /// range the edge is allowed to move within.
    /// </summary>
    public static (double Min, double Max) Limits(List<FracRect> zones, Divider d)
    {
        double min = 0, max = 1;
        foreach (var z in zones)
        {
            if (!Participates(z, d)) continue;
            double lo = d.Vertical ? z.L : z.T, hi = d.Vertical ? z.R : z.B;

            // A zone whose far side is this edge may not be squashed below MinExtent...
            if (Near(hi, d.Position)) min = Math.Max(min, lo + MinExtent);
            // ...nor one whose near side is this edge.
            if (Near(lo, d.Position)) max = Math.Min(max, hi - MinExtent);
        }

        // Zones narrower than MinExtent (a hand-written many-column layout, or plain floating point
        // at exactly 20 equal columns) make the bounds cross. Pin to the edge itself: the divider
        // simply will not move. Using the midpoint instead can land outside [0,1] and get committed.
        if (min > max) min = max = d.Position;

        return (min, max);
    }

    public static (double Min, double Max) Limits(List<FracRect> zones, double edge, bool vertical) =>
        Limits(zones, new Divider(vertical, edge, 0, 1));

    /// <summary>Moves every reference to <paramref name="d"/> within its span to <paramref name="to"/>.</summary>
    public static void Move(List<FracRect> zones, Divider d, double to)
    {
        foreach (var z in zones)
        {
            if (!Participates(z, d)) continue;
            if (d.Vertical)
            {
                if (Near(z.L, d.Position)) z.L = to;
                if (Near(z.R, d.Position)) z.R = to;
            }
            else
            {
                if (Near(z.T, d.Position)) z.T = to;
                if (Near(z.B, d.Position)) z.B = to;
            }
        }
    }

    /// <summary>Moves every reference to <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static void Move(List<FracRect> zones, double from, double to, bool vertical) =>
        Move(zones, new Divider(vertical, from, 0, 1), to);

    public static bool Near(double a, double b) => Math.Abs(a - b) < Epsilon;

    /// <summary>Builds a regular <paramref name="columns"/> × <paramref name="rows"/> grid of zones.</summary>
    public static List<FracRect> Grid(int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, 16);
        rows = Math.Clamp(rows, 1, 12);

        double[] xs = new double[columns + 1];
        double[] ys = new double[rows + 1];
        for (int c = 0; c <= columns; c++) xs[c] = c == columns ? 1.0 : (double)c / columns;
        for (int r = 0; r <= rows; r++) ys[r] = r == rows ? 1.0 : (double)r / rows;

        var result = new List<FracRect>(columns * rows);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
                result.Add(new FracRect(xs[c], ys[r], xs[c + 1], ys[r + 1]));

        return result;
    }

    /// <summary>Builds full-height vertical columns from relative weights (e.g. 25, 50, 25).</summary>
    public static List<FracRect> Columns(params double[] weights)
    {
        if (weights is null || weights.Length == 0) return Grid(1, 1);
        double total = weights.Sum();
        if (total <= 0) return Grid(weights.Length, 1);

        double[] edges = new double[weights.Length + 1];
        double acc = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            edges[i] = acc / total;
            acc += weights[i];
        }
        edges[weights.Length] = 1.0;

        var list = new List<FracRect>(weights.Length);
        for (int i = 0; i < weights.Length; i++)
            list.Add(new FracRect(edges[i], 0, edges[i + 1], 1));
        return list;
    }

    /// <summary>
    /// Adds a new full-height vertical column on the right and scales existing zones horizontally
    /// so an N-column layout cleanly becomes an (N+1)-column layout.
    /// </summary>
    public static List<FracRect> AddColumn(List<FracRect> zones)
    {
        var copy = Clone(zones);
        if (copy.Count == 0) return Grid(2, 1);

        int cols = Vertical(copy).Count + 1;
        if (1.0 / (cols + 1) < MinExtent) return copy;

        double scale = (double)cols / (cols + 1);
        foreach (var z in copy)
        {
            z.L *= scale;
            z.R = Near(z.R, 1.0) ? scale : z.R * scale;
        }
        copy.Add(new FracRect(scale, 0, 1.0, 1.0));
        Canonicalise(copy);
        return copy.OrderBy(z => z.T).ThenBy(z => z.L).ToList();
    }

    /// <summary>
    /// Adds a new full-width horizontal row at the bottom and scales existing zones vertically
    /// so an M-row layout cleanly becomes an (M+1)-row layout.
    /// </summary>
    public static List<FracRect> AddRow(List<FracRect> zones)
    {
        var copy = Clone(zones);
        if (copy.Count == 0) return Grid(1, 2);

        int rows = Horizontal(copy).Count + 1;
        if (1.0 / (rows + 1) < MinExtent) return copy;

        double scale = (double)rows / (rows + 1);
        foreach (var z in copy)
        {
            z.T *= scale;
            z.B = Near(z.B, 1.0) ? scale : z.B * scale;
        }
        copy.Add(new FracRect(0, scale, 1.0, 1.0));
        Canonicalise(copy);
        return copy.OrderBy(z => z.T).ThenBy(z => z.L).ToList();
    }

    /// <summary>
    /// Splits <c>zones[index]</c> into two equal halves: left/right when <paramref name="vertical"/>
    /// is true, or top/bottom when false.
    /// </summary>
    public static bool CanSplitZone(List<FracRect> zones, int index, bool vertical)
    {
        if (index < 0 || index >= zones.Count) return false;
        var z = zones[index];
        return (vertical ? z.R - z.L : z.B - z.T) >= 2 * MinExtent;
    }

    public static List<FracRect> SplitZone(List<FracRect> zones, int index, bool vertical)
    {
        var copy = Clone(zones);
        if (!CanSplitZone(copy, index, vertical)) return copy;

        var z = copy[index];
        copy.RemoveAt(index);

        if (vertical)
        {
            double mid = (z.L + z.R) / 2.0;
            copy.Add(new FracRect(z.L, z.T, mid, z.B, z.CoverTaskbar));
            copy.Add(new FracRect(mid, z.T, z.R, z.B, z.CoverTaskbar));
        }
        else
        {
            double mid = (z.T + z.B) / 2.0;
            copy.Add(new FracRect(z.L, z.T, z.R, mid, z.CoverTaskbar));
            copy.Add(new FracRect(z.L, mid, z.R, z.B, z.CoverTaskbar));
        }

        Canonicalise(copy);
        return copy.OrderBy(r => r.T).ThenBy(r => r.L).ToList();
    }

    /// <summary>True when <c>zones[index]</c> can be removed by expanding adjacent zones into its space.</summary>
    public static bool CanRemoveZone(List<FracRect> zones, int index) =>
        zones.Count > 1 && index >= 0 && index < zones.Count && TryRemoveZone(Clone(zones), index);

    /// <summary>Removes <c>zones[index]</c> and expands its best matching neighbour(s) to keep the screen tiled.</summary>
    public static List<FracRect> RemoveZone(List<FracRect> zones, int index)
    {
        var copy = Clone(zones);
        if (copy.Count <= 1 || index < 0 || index >= copy.Count) return copy;
        if (!TryRemoveZone(copy, index)) return Clone(zones);
        Canonicalise(copy);
        return copy.OrderBy(z => z.T).ThenBy(z => z.L).ToList();
    }

    private static bool TryRemoveZone(List<FracRect> zones, int index)
    {
        var target = zones[index];
        double width = target.R - target.L;
        double height = target.B - target.T;

        var left = zones.Where((z, i) => i != index && Near(z.R, target.L)
                                      && z.T >= target.T - MergeTolerance && z.B <= target.B + MergeTolerance).ToList();
        var right = zones.Where((z, i) => i != index && Near(z.L, target.R)
                                       && z.T >= target.T - MergeTolerance && z.B <= target.B + MergeTolerance).ToList();
        var top = zones.Where((z, i) => i != index && Near(z.B, target.T)
                                     && z.L >= target.L - MergeTolerance && z.R <= target.R + MergeTolerance).ToList();
        var bottom = zones.Where((z, i) => i != index && Near(z.T, target.B)
                                        && z.L >= target.L - MergeTolerance && z.R <= target.R + MergeTolerance).ToList();

        var options = new List<(List<FracRect> Neighbours, Action Expand, int Priority, double Area)>();

        if (left.Count > 0 && Math.Abs(left.Sum(z => z.B - z.T) - height) <= MergeTolerance * (left.Count + 1))
            options.Add((left, () => { foreach (var z in left) z.R = target.R; },
                         Priority(left, verticalMerge: true, width, height), left.Sum(Area)));

        if (right.Count > 0 && Math.Abs(right.Sum(z => z.B - z.T) - height) <= MergeTolerance * (right.Count + 1))
            options.Add((right, () => { foreach (var z in right) z.L = target.L; },
                         Priority(right, verticalMerge: true, width, height), right.Sum(Area)));

        if (top.Count > 0 && Math.Abs(top.Sum(z => z.R - z.L) - width) <= MergeTolerance * (top.Count + 1))
            options.Add((top, () => { foreach (var z in top) z.B = target.B; },
                         Priority(top, verticalMerge: false, width, height), top.Sum(Area)));

        if (bottom.Count > 0 && Math.Abs(bottom.Sum(z => z.R - z.L) - width) <= MergeTolerance * (bottom.Count + 1))
            options.Add((bottom, () => { foreach (var z in bottom) z.T = target.T; },
                         Priority(bottom, verticalMerge: false, width, height), bottom.Sum(Area)));

        if (options.Count == 0) return false;

        var chosen = options.OrderBy(o => o.Priority).ThenBy(o => o.Area).First();
        chosen.Expand();
        zones.RemoveAt(index);
        return true;

        static double Area(FracRect z) => (z.R - z.L) * (z.B - z.T);
        static int Priority(List<FracRect> n, bool verticalMerge, double w, double h)
        {
            // Prefer merging into a single exact sibling (count == 1), and prefer merging along the
            // axis where the removed zone was narrower (undoing a sub-split before eating a column).
            int p = n.Count == 1 ? 0 : 10;
            bool narrowerAlongMerge = verticalMerge ? w <= h + Epsilon : h <= w + Epsilon;
            if (!narrowerAlongMerge) p += 5;
            return p;
        }
    }

    /// <summary>
    /// Removes a divider by merging the zones on either side of it. Returns true if the divider
    /// could be cleanly collapsed without leaving gaps.
    /// </summary>
    public static bool RemoveDivider(List<FracRect> zones, Divider d)
    {
        var nearSide = zones.Where(z => Participates(z, d) && Near(d.Vertical ? z.R : z.B, d.Position))
                            .OrderBy(z => d.Vertical ? z.T : z.L).ToList();
        var farSide = zones.Where(z => Participates(z, d) && Near(d.Vertical ? z.L : z.T, d.Position))
                           .OrderBy(z => d.Vertical ? z.T : z.L).ToList();

        if (nearSide.Count == 0 || farSide.Count == 0) return false;

        // 1-to-1 matching pairs across the divider.
        if (nearSide.Count == farSide.Count && nearSide.Zip(farSide).All(p =>
            Near(d.Vertical ? p.First.T : p.First.L, d.Vertical ? p.Second.T : p.Second.L) &&
            Near(d.Vertical ? p.First.B : p.First.R, d.Vertical ? p.Second.B : p.Second.R)))
        {
            for (int i = 0; i < nearSide.Count; i++)
            {
                if (d.Vertical) nearSide[i].R = farSide[i].R;
                else nearSide[i].B = farSide[i].B;
                nearSide[i].CoverTaskbar |= farSide[i].CoverTaskbar;
                zones.Remove(farSide[i]);
            }
            Canonicalise(zones);
            return true;
        }

        // One zone on the near side spanning all zones on the far side.
        if (nearSide.Count == 1)
        {
            var single = nearSide[0];
            foreach (var f in farSide)
            {
                if (d.Vertical) f.L = single.L;
                else f.T = single.T;
            }
            zones.Remove(single);
            Canonicalise(zones);
            return true;
        }

        // One zone on the far side spanning all zones on the near side.
        if (farSide.Count == 1)
        {
            var single = farSide[0];
            foreach (var n in nearSide)
            {
                if (d.Vertical) n.R = single.R;
                else n.B = single.B;
            }
            zones.Remove(single);
            Canonicalise(zones);
            return true;
        }

        return false;
    }

    /// <summary>Spaces every vertical and horizontal divider evenly across the display.</summary>
    public static List<FracRect> Equalise(List<FracRect> zones)
    {
        var copy = Clone(zones);
        if (copy.Count <= 1) return copy;

        Canonicalise(copy);
        EqualiseAxis(copy, vertical: true);
        EqualiseAxis(copy, vertical: false);
        return copy.OrderBy(z => z.T).ThenBy(z => z.L).ToList();
    }

    private static void EqualiseAxis(List<FracRect> zones, bool vertical)
    {
        var internalCoords = vertical ? Vertical(zones) : Horizontal(zones);
        if (internalCoords.Count == 0) return;

        int bands = internalCoords.Count + 1;
        var oldEdges = new List<double>(bands + 1) { 0.0 };
        oldEdges.AddRange(internalCoords);
        oldEdges.Add(1.0);

        double Map(double v)
        {
            int bestIdx = 0;
            for (int i = 1; i < oldEdges.Count; i++)
                if (Math.Abs(oldEdges[i] - v) < Math.Abs(oldEdges[bestIdx] - v))
                    bestIdx = i;
            return bestIdx == bands ? 1.0 : (double)bestIdx / bands;
        }

        foreach (var z in zones)
        {
            if (vertical) { z.L = Map(z.L); z.R = Map(z.R); }
            else          { z.T = Map(z.T); z.B = Map(z.B); }
        }
    }

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

    /// <summary>
    /// Whether two layouts are the same to within a rounding error. Compared loosely on purpose:
    /// a layout that came back from a preset and one that came back from JSON differ in the last
    /// decimal place, and an exact test would say a preset is never the one currently applied.
    /// </summary>
    public static bool Same(List<FracRect> a, List<FracRect> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            Near(p.First.L, p.Second.L) && Near(p.First.T, p.Second.T) &&
            Near(p.First.R, p.Second.R) && Near(p.First.B, p.Second.B));

    public static List<FracRect> Clone(List<FracRect> zones) =>
        zones.Select(z => new FracRect(z.L, z.T, z.R, z.B, z.CoverTaskbar)).ToList();
}
