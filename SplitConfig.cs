using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScweenSpit;

/// <summary>A zone as a fraction of a monitor's work area. Covers columns, rows and grids alike.</summary>
public sealed class FracRect
{
    public double L { get; set; }
    public double T { get; set; }
    public double R { get; set; } = 1;
    public double B { get; set; } = 1;

    /// <summary>
    /// Lay this zone out against the whole monitor rather than the work area, and keep windows in
    /// it above the taskbar. For a genuinely fullscreen pane on part of a display: the taskbar
    /// stays visible and usable everywhere this zone does not reach.
    /// </summary>
    public bool CoverTaskbar { get; set; }

    public FracRect() { }
    public FracRect(double l, double t, double r, double b, bool coverTaskbar = false)
    { L = l; T = t; R = r; B = b; CoverTaskbar = coverTaskbar; }
}

/// <summary>Space reserved at the edges of a display, in physical pixels.</summary>
public sealed class Margins
{
    public int Top { get; set; }
    public int Bottom { get; set; }
    public int Left { get; set; }
    public int Right { get; set; }

    [JsonIgnore]
    public bool Any => Top != 0 || Bottom != 0 || Left != 0 || Right != 0;

    public Margins Copy() => new() { Top = Top, Bottom = Bottom, Left = Left, Right = Right };

    /// <summary>Smallest usable strip a display may be reduced to, in pixels.</summary>
    public const int MinUsable = 200;

    /// <summary>
    /// The margins actually applied, trimmed to leave <see cref="MinUsable"/> in each axis.
    /// Trimming the facing pair proportionally beats discarding all four: one over-large value
    /// should not silently void a perfectly good margin on the other axis.
    /// </summary>
    public Margins Fitted(int width, int height)
    {
        var (l, r) = FitPair(Left, Right, width);
        var (t, b) = FitPair(Top, Bottom, height);
        return new Margins { Left = l, Right = r, Top = t, Bottom = b };
    }

    private static (int Near, int Far) FitPair(int near, int far, int extent)
    {
        near = Math.Max(0, near);
        far = Math.Max(0, far);

        int budget = extent - MinUsable;
        if (budget <= 0) return (0, 0);

        int total = near + far;
        if (total <= budget) return (near, far);

        // Proportional, floored at zero: subtracting the overflow from one side alone drives the
        // other negative whenever it exceeds the budget by itself, putting zones off the monitor.
        int scaled = (int)Math.Floor(near * (double)budget / total);
        return (Math.Clamp(scaled, 0, budget), budget - Math.Clamp(scaled, 0, budget));
    }
}

/// <summary>A ScweenSpit taskbar docked to one edge of one display.</summary>
public sealed class BarSettings
{
    /// <summary>Left, Top, Right or Bottom.</summary>
    public string Edge { get; set; } = "Right";

    /// <summary>Width of a side bar, or height of a top/bottom one, in pixels.</summary>
    public int Thickness { get; set; } = 50;

    /// <summary>
    /// Sit clear of the edges with every corner rounded, the way a floating dock looks, rather than
    /// flush against the screen. The space is still reserved either way — only the bar moves in.
    /// </summary>
    public bool Floating { get; set; }

    /// <summary>How far a floating bar sits from the edges of its reserved strip, in pixels.</summary>
    public int FloatMargin { get; set; } = 10;

    /// <summary>
    /// Applications kept on the bar whether or not they are running, in the order they appear.
    /// Stored as full executable paths.
    /// </summary>
    public List<string> Pinned { get; set; } = new();

    /// <summary>Icons alone, the way a real taskbar looks. Titles need roughly four times the room.</summary>
    public bool IconsOnly { get; set; } = true;

    /// <summary>Show battery, network, volume and the clock.</summary>
    public bool ShowStatus { get; set; } = true;

    /// <summary>List only windows on this display, rather than every window everywhere.</summary>
    public bool ThisDisplayOnly { get; set; } = true;

    /// <summary>
    /// Confine the bar to one zone instead of the whole display edge — for an ultrawide split into
    /// sub-screens, where a bar spanning all of it is not what anyone wants. Null spans the display.
    ///
    /// A zone-scoped bar cannot be an appbar: Windows reserves space as a single rectangle per
    /// monitor, so a partial-width strip is not expressible. The zone is shortened instead, which
    /// keeps windows ScweenSpit places clear of it.
    /// </summary>
    public int? Zone { get; set; }
}

public sealed class MonitorLayout
{
    public List<FracRect> Zones { get; set; } = new();

    /// <summary>
    /// Extra space kept clear at the edges, on top of what Windows already reports as the work
    /// area. Use it to dodge an auto-hiding or third-party taskbar, or to reclaim space Windows
    /// reserves but you do not actually need.
    /// </summary>
    public Margins Margins { get; set; } = new();
}

public sealed class SplitConfig
{
    /// <summary>Master switch for the automatic maximize / borderless-fullscreen clamp.</summary>
    public bool AutoClamp { get; set; } = true;

    /// <summary>How long (ms) to ignore further events for a window we just moved.</summary>
    public int DebounceMs { get; set; } = 400;

    /// <summary>Gap in pixels left around every zone. 0 makes zones touch edge to edge.</summary>
    public int Padding { get; set; }

    /// <summary>
    /// Hide the shell's taskbars entirely while we run. Restored on exit, and on the next launch if
    /// this one is killed — the same bookkeeping as <see cref="SnapRestore"/>, for the same reason.
    /// </summary>
    public bool HideWindowsTaskbar { get; set; }

    /// <summary>Whether the taskbar was set to auto-hide before we hid it. Bookkeeping, not a preference.</summary>
    public bool? TaskbarRestore { get; set; }

    /// <summary>
    /// Stop Windows animating windows into the taskbar. With the taskbar hidden the animation flies
    /// them at a corner where nothing is, which reads as a glitch.
    /// </summary>
    public bool StopMinimiseAnimation { get; set; }

    /// <summary>Whether the animation was on before we turned it off. Bookkeeping, not a preference.</summary>
    public bool? AnimationRestore { get; set; }

    /// <summary>Disable Windows' own Aero Snap while we run, so it stops competing with our zones.</summary>
    public bool SuppressWindowsSnap { get; set; }

    /// <summary>What Windows' snap settings were before we suppressed them. Bookkeeping, not a preference:
    /// it lets the next launch put them back even if this one is killed rather than closed.</summary>
    public int[]? SnapRestore { get; set; }

    /// <summary>
    /// Pull a window back onto a single display when it appears straddling several. Windows you
    /// drag across a boundary yourself are exempted for as long as they live.
    /// </summary>
    public bool KeepOnOneDisplay { get; set; } = true;

    /// <summary>Look for a newer release on startup, at most once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Where releases are published, as owner/name.</summary>
    public string UpdateRepository { get; set; } = "andrewwetzel/scweenspit";

    /// <summary>A GitHub token, needed only while the repository is private.</summary>
    public string UpdateToken { get; set; } = "";

    /// <summary>When the last check happened, so startup checks stay to one a day.</summary>
    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>Snap a window into a zone when you drop it there while dragging.</summary>
    public bool DragToZone { get; set; } = true;

    /// <summary>Key that must be held for a drag to snap: Shift, Control, Alt, or None for always.</summary>
    public string DragModifier { get; set; } = "Shift";

    /// <summary>Key that, held during a drag, spans the window across every zone touched.</summary>
    public string SpanModifier { get; set; } = "Control";

    /// <summary>Never touch windows of these processes or window classes. Matched case-insensitively,
    /// with or without a .exe suffix — e.g. "vlc", "mpv.exe", "UnityWndClass".</summary>
    public List<string> Exclude { get; set; } = new();

    /// <summary>Keyed by Win32 device name (\\.\DISPLAY1); "*" is the fallback for unlisted monitors.</summary>
    public Dictionary<string, MonitorLayout> Monitors { get; set; } = new();

    /// <summary>
    /// Our own taskbars, keyed by device name. An entry means a bar on that display; no entry means
    /// none. Deliberately not subject to the "*" fallback — a bar appearing on a display you never
    /// asked about would be a surprise, and it reserves screen space.
    /// </summary>
    public Dictionary<string, BarSettings> Bars { get; set; } = new();

    public const string Fallback = "*";

    /// <summary>Virtual-key for a modifier name, or null for "no modifier required".</summary>
    public static int? ModifierKey(string name) => name?.Trim().ToLowerInvariant() switch
    {
        "shift" => Native.VK_SHIFT,
        "control" or "ctrl" => Native.VK_CONTROL,
        "alt" or "menu" => Native.VK_MENU,
        _ => null,
    };

    public static readonly string[] ModifierNames = ["None", "Shift", "Control", "Alt"];

    public static readonly string[] EdgeNames = ["Left", "Top", "Right", "Bottom"];

    public static BarEdge ParseEdge(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "left" => BarEdge.Left,
        "top" => BarEdge.Top,
        "bottom" => BarEdge.Bottom,
        _ => BarEdge.Right,
    };

    /// <summary>True when this window belongs to something the user has opted out of.</summary>
    public bool IsExcluded(string process, string windowClass)
    {
        foreach (var e in Exclude)
        {
            if (e.Length == 0) continue;
            var bare = e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? e[..^4] : e;
            if (bare.Equals(process, StringComparison.OrdinalIgnoreCase)) return true;
            if (e.Equals(windowClass, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ---- presets used by the tray menu -------------------------------------
    public static readonly (string Name, Func<List<FracRect>> Make)[] Presets =
    {
        ("Full — leave this display alone", () => new() { new(0, 0, 1, 1) }),
        ("70 / 30",         () => new() { new(0, 0, .70, 1), new(.70, 0, 1, 1) }),
        ("30 / 70",         () => new() { new(0, 0, .30, 1), new(.30, 0, 1, 1) }),
        ("60 / 40",         () => new() { new(0, 0, .60, 1), new(.60, 0, 1, 1) }),
        ("50 / 50",         () => new() { new(0, 0, .50, 1), new(.50, 0, 1, 1) }),
        ("Thirds",          () => new() { new(0, 0, 1/3d, 1), new(1/3d, 0, 2/3d, 1), new(2/3d, 0, 1, 1) }),
        ("Top / Bottom",    () => new() { new(0, 0, 1, .5), new(0, .5, 1, 1) }),
        ("Quadrants",       () => new() { new(0, 0, .5, .5), new(.5, 0, 1, .5),
                                          new(0, .5, .5, 1), new(.5, .5, 1, 1) }),
    };

    // ---- persistence -------------------------------------------------------
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScweenSpit", "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>True when the last <see cref="Load"/> could not read an existing config file.</summary>
    public static bool LastLoadFailed { get; private set; }

    public static SplitConfig Load()
    {
        LastLoadFailed = false;
        try
        {
            if (File.Exists(Path))
            {
                var cfg = JsonSerializer.Deserialize<SplitConfig>(File.ReadAllText(Path), JsonOpts);
                if (cfg is not null) return cfg.Normalized();
                LastLoadFailed = true;
            }
            else
            {
                // First run only. The file has to exist for "Open config file" to have something
                // to open, so this seed is deliberate - unlike the unreadable case below.
                var seed = Default();
                seed.Save();
                return seed;
            }
        }
        catch (Exception ex)
        {
            // A corrupt config must never stop the app — but it must not be destroyed either;
            // it is hand-edited, so keep a copy before defaults overwrite it.
            LastLoadFailed = true;
            Log.Write($"config load failed: {ex.Message}");
            try
            {
                var bad = Path + ".bad";
                File.Copy(Path, bad, overwrite: true);
                Log.Write($"unreadable config preserved at {bad}");
            }
            catch (Exception copyEx) { Log.Write($"could not preserve bad config: {copyEx.Message}"); }
        }

        // Deliberately NOT saved: overwriting an unreadable config destroys a hand-edited file the
        // user can still fix. They get working defaults for this session and keep their file.
        return Default();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            // Write then swap. Saving is now triggered by things like a spinner's auto-repeat, and
            // a half-written config is worse than a stale one.
            var staging = Path + ".tmp";
            File.WriteAllText(staging, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(staging, Path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"config save failed: {ex.Message}");
        }
    }

    public static SplitConfig Default() => new()
    {
        Monitors = { [Fallback] = new MonitorLayout { Zones = { new(0, 0, .70, 1), new(.70, 0, 1, 1) } } },
    };

    /// <summary>Guarantees a usable fallback entry and drops degenerate zones.</summary>
    private SplitConfig Normalized()
    {
        if (DebounceMs < 50) DebounceMs = 50;
        if (Padding < 0) Padding = 0;
        Exclude = Exclude.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct().ToList();

        foreach (var bar in Bars.Values)
        {
            // A bar thinner than this cannot show anything; a bar wider than a third of a display is
            // almost certainly a typo, and it eats the work area for every app on that screen.
            bar.Thickness = Math.Clamp(bar.Thickness, 28, 600);
            bar.FloatMargin = Math.Clamp(bar.FloatMargin, 0, Math.Max(0, bar.Thickness / 2 - 8));
            if (!EdgeNames.Contains(bar.Edge, StringComparer.OrdinalIgnoreCase)) bar.Edge = "Right";
        }

        foreach (var layout in Monitors.Values)
        {
            // Fractions outside [0,1] would place a zone off the monitor entirely - a window sent
            // there has no reachable titlebar.
            foreach (var z in layout.Zones)
            {
                z.L = Math.Clamp(z.L, 0, 1); z.R = Math.Clamp(z.R, 0, 1);
                z.T = Math.Clamp(z.T, 0, 1); z.B = Math.Clamp(z.B, 0, 1);
            }
            layout.Zones.RemoveAll(z => z.R - z.L <= 0.01 || z.B - z.T <= 0.01);
            ZoneEdges.Canonicalise(layout.Zones);

            // Stored in the same reading order the overlay numbers them in, so "Zone 2" in the
            // settings window is always the 2 painted on screen.
            layout.Zones = layout.Zones.OrderBy(z => z.T).ThenBy(z => z.L).ToList();

            layout.Margins ??= new Margins();
            layout.Margins.Top = Math.Max(0, layout.Margins.Top);
            layout.Margins.Bottom = Math.Max(0, layout.Margins.Bottom);
            layout.Margins.Left = Math.Max(0, layout.Margins.Left);
            layout.Margins.Right = Math.Max(0, layout.Margins.Right);
        }

        if (!Monitors.ContainsKey(Fallback) || Monitors[Fallback].Zones.Count == 0)
            Monitors[Fallback] = Default().Monitors[Fallback];

        // An entry with margins but no zones is a legitimate hand-edit ("reserve space here, keep
        // the shared layout"). Give it the shared zones - cloned, or editing this display would
        // move every other one - rather than deleting the user's margins.
        foreach (var entry in Monitors.Values)
            if (entry.Zones.Count == 0 && entry.Margins.Any)
                entry.Zones = Monitors[Fallback].Zones.Select(z => new FracRect(z.L, z.T, z.R, z.B, z.CoverTaskbar)).ToList();

        foreach (var key in Monitors.Where(kv => kv.Value.Zones.Count == 0 && kv.Key != Fallback)
                                    .Select(kv => kv.Key).ToList())
            Monitors.Remove(key);

        return this;
    }

    /// <summary>The layout a device actually uses, falling back to "*".</summary>
    public MonitorLayout LayoutFor(string device)
    {
        if (Monitors.TryGetValue(device, out var m) && m.Zones.Count > 0) return m;
        if (Monitors.TryGetValue(Fallback, out var f) && f.Zones.Count > 0) return f;
        return Default().Monitors[Fallback];
    }

    public List<FracRect> ZonesFor(string device) => LayoutFor(device).Zones;

    /// <summary>
    /// Gives a device its own entry, cloned from whatever it was inheriting. Editing a monitor that
    /// was using the "*" fallback has to fork, or the edit would silently move every other display.
    /// </summary>
    public MonitorLayout OwnLayout(string device)
    {
        if (Monitors.TryGetValue(device, out var own) && own.Zones.Count > 0) return own;

        var inherited = LayoutFor(device);
        var forked = new MonitorLayout
        {
            Zones = inherited.Zones.Select(z => new FracRect(z.L, z.T, z.R, z.B, z.CoverTaskbar)).ToList(),
            Margins = inherited.Margins.Copy(),
        };
        Monitors[device] = forked;
        return forked;
    }

    public void SetMargins(string device, Margins margins)
    {
        OwnLayout(device).Margins = margins;
        Save();
    }

    /// <summary>
    /// Copies another config's values in place. Reloading by replacing the object would strand every
    /// reference the UI and services already hold, so the instance itself is never swapped out.
    /// </summary>
    public void CopyFrom(SplitConfig o)
    {
        AutoClamp = o.AutoClamp;
        DebounceMs = o.DebounceMs;
        Padding = o.Padding;
        SuppressWindowsSnap = o.SuppressWindowsSnap;
        HideWindowsTaskbar = o.HideWindowsTaskbar;
        StopMinimiseAnimation = o.StopMinimiseAnimation;
        // SnapRestore is deliberately NOT copied: it records what this process changed on the
        // system, and the disk copy may be older than what we are currently holding.
        DragToZone = o.DragToZone;
        DragModifier = o.DragModifier;
        SpanModifier = o.SpanModifier;
        CheckForUpdates = o.CheckForUpdates;
        UpdateRepository = o.UpdateRepository;
        UpdateToken = o.UpdateToken;
        LastUpdateCheck = o.LastUpdateCheck;
        Exclude = o.Exclude;
        Monitors = o.Monitors;
        Bars = o.Bars;
    }

    public void SetZones(string device, List<FracRect> zones)
    {
        var layout = OwnLayout(device);
        layout.Zones = zones.OrderBy(z => z.T).ThenBy(z => z.L).ToList();
        Save();
    }
}
