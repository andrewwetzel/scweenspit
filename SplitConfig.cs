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

    public FracRect() { }
    public FracRect(double l, double t, double r, double b) { L = l; T = t; R = r; B = b; }
}

public sealed class MonitorLayout
{
    public List<FracRect> Zones { get; set; } = new();
}

public sealed class SplitConfig
{
    /// <summary>Master switch for the automatic maximize / borderless-fullscreen clamp.</summary>
    public bool AutoClamp { get; set; } = true;

    /// <summary>How long (ms) to ignore further events for a window we just moved.</summary>
    public int DebounceMs { get; set; } = 400;

    /// <summary>Gap in pixels left around every zone. 0 makes zones touch edge to edge.</summary>
    public int Padding { get; set; }

    /// <summary>Disable Windows' own Aero Snap while we run, so it stops competing with our zones.</summary>
    public bool SuppressWindowsSnap { get; set; }

    /// <summary>What Windows' snap settings were before we suppressed them. Bookkeeping, not a preference:
    /// it lets the next launch put them back even if this one is killed rather than closed.</summary>
    public int[]? SnapRestore { get; set; }

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

    public static SplitConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var cfg = JsonSerializer.Deserialize<SplitConfig>(File.ReadAllText(Path), JsonOpts);
                if (cfg is not null) return cfg.Normalized();
            }
        }
        catch (Exception ex)
        {
            // A corrupt config must never stop the app — but it must not be destroyed either;
            // it is hand-edited, so keep a copy before defaults overwrite it.
            Log.Write($"config load failed: {ex.Message}");
            try
            {
                var bad = Path + ".bad";
                File.Copy(Path, bad, overwrite: true);
                Log.Write($"unreadable config preserved at {bad}");
            }
            catch (Exception copyEx) { Log.Write($"could not preserve bad config: {copyEx.Message}"); }
        }

        var fresh = Default();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, JsonOpts));
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
        }

        foreach (var key in Monitors.Where(kv => kv.Value.Zones.Count == 0).Select(kv => kv.Key).ToList())
            Monitors.Remove(key);

        if (!Monitors.ContainsKey(Fallback))
            Monitors[Fallback] = Default().Monitors[Fallback];

        return this;
    }

    /// <summary>Zones for a device, falling back to "*".</summary>
    public List<FracRect> ZonesFor(string device)
    {
        if (Monitors.TryGetValue(device, out var m) && m.Zones.Count > 0) return m.Zones;
        if (Monitors.TryGetValue(Fallback, out var f) && f.Zones.Count > 0) return f.Zones;
        return Default().Monitors[Fallback].Zones;
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
        SnapRestore = o.SnapRestore;
        DragToZone = o.DragToZone;
        DragModifier = o.DragModifier;
        SpanModifier = o.SpanModifier;
        Exclude = o.Exclude;
        Monitors = o.Monitors;
    }

    public void SetZones(string device, List<FracRect> zones)
    {
        Monitors[device] = new MonitorLayout { Zones = zones };
        Save();
    }
}
