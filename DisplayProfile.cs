namespace ScweenSpit;

/// <summary>
/// Settings remembered for one arrangement of displays. Every field is optional: null means "leave
/// whatever is set", so a profile can change only the handful of things that actually differ between
/// docked and undocked without dragging the rest along with it.
/// </summary>
public sealed class DisplayProfile
{
    /// <summary>What the user called this arrangement, purely for the settings list.</summary>
    public string? Name { get; set; }

    public bool? AutoClamp { get; set; }
    public bool? DragToZone { get; set; }
    public bool? KeepOnOneDisplay { get; set; }
    public bool? HideWindowsTaskbar { get; set; }
    public bool? SuppressWindowsSnap { get; set; }
    public bool? StopMinimiseAnimation { get; set; }

    public void CaptureFrom(SplitConfig config)
    {
        AutoClamp = config.AutoClamp;
        DragToZone = config.DragToZone;
        KeepOnOneDisplay = config.KeepOnOneDisplay;
        HideWindowsTaskbar = config.HideWindowsTaskbar;
        SuppressWindowsSnap = config.SuppressWindowsSnap;
        StopMinimiseAnimation = config.StopMinimiseAnimation;
    }

    public void ApplyTo(SplitConfig config)
    {
        if (AutoClamp is { } a) config.AutoClamp = a;
        if (DragToZone is { } d) config.DragToZone = d;
        if (KeepOnOneDisplay is { } k) config.KeepOnOneDisplay = k;
        if (HideWindowsTaskbar is { } h) config.HideWindowsTaskbar = h;
        if (SuppressWindowsSnap is { } s) config.SuppressWindowsSnap = s;
        if (StopMinimiseAnimation is { } m) config.StopMinimiseAnimation = m;
    }
}

/// <summary>Identifies an arrangement of displays, so settings can follow the hardware.</summary>
public static class DisplayTopology
{
    /// <summary>
    /// A key for the current arrangement: how many displays and at what sizes, sorted so the same
    /// hardware always produces the same string.
    ///
    /// Deliberately not built from device names — Windows reassigns \\.\DISPLAYn between docks, so a
    /// name-based key would fail at exactly the moment it was needed. Two different displays of the
    /// same size do collide, which is the price of that.
    /// </summary>
    public static string Signature() => Signature(ZoneManager.AllMonitors());

    public static string Signature(List<MonitorGeometry> monitors) =>
        monitors.Count == 0
            ? "none"
            : string.Join("+", monitors.Select(m => $"{m.Bounds.Width}x{m.Bounds.Height}")
                                       .OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>The same thing said in a sentence, for the settings window.</summary>
    public static string Describe()
    {
        var monitors = ZoneManager.AllMonitors();
        return monitors.Count switch
        {
            0 => "no displays",
            1 => $"one display, {monitors[0].Bounds.Width}×{monitors[0].Bounds.Height}",
            _ => $"{monitors.Count} displays: " +
                 string.Join(" + ", monitors.Select(m => $"{m.Bounds.Width}×{m.Bounds.Height}")),
        };
    }
}
