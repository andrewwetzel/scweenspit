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

    /// <summary>Keyed by Win32 device name (\\.\DISPLAY1); "*" is the fallback for unlisted monitors.</summary>
    public Dictionary<string, MonitorLayout> Monitors { get; set; } = new();

    public const string Fallback = "*";

    // ---- presets used by the tray menu -------------------------------------
    public static readonly (string Name, Func<List<FracRect>> Make)[] Presets =
    {
        ("Full (no split)", () => new() { new(0, 0, 1, 1) }),
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
            // A corrupt config must never stop the app; fall through to defaults.
            Log.Write($"config load failed: {ex.Message}");
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

        foreach (var layout in Monitors.Values)
            layout.Zones.RemoveAll(z => z.R - z.L <= 0.01 || z.B - z.T <= 0.01);

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

    public void SetZones(string device, List<FracRect> zones)
    {
        Monitors[device] = new MonitorLayout { Zones = zones };
        Save();
    }
}
