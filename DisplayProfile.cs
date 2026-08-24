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
