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
    public bool? TaskbarAutoHide { get; set; }
    public bool? ShowBars { get; set; }
    public bool? SuppressWindowsSnap { get; set; }
    public bool? StopMinimiseAnimation { get; set; }

    /// <summary>
    /// A profile that leaves the machine to Windows: nothing clamped, nothing snapped for you, no
    /// bars, and the taskbar back where it was and staying there. What undocking usually wants.
    /// </summary>
    public static DisplayProfile HandsOff(string? name = null) => new()
    {
        Name = name,
        AutoClamp = false,
        DragToZone = false,
        KeepOnOneDisplay = false,
        HideWindowsTaskbar = false,
        TaskbarAutoHide = false,
        ShowBars = false,
        SuppressWindowsSnap = false,
        StopMinimiseAnimation = false,
    };

    public void CaptureFrom(SplitConfig config)
    {
        AutoClamp = config.AutoClamp;
        DragToZone = config.DragToZone;
        KeepOnOneDisplay = config.KeepOnOneDisplay;
        HideWindowsTaskbar = config.HideWindowsTaskbar;
        TaskbarAutoHide = config.TaskbarAutoHide;
        ShowBars = config.ShowBars;
        SuppressWindowsSnap = config.SuppressWindowsSnap;
        StopMinimiseAnimation = config.StopMinimiseAnimation;
    }

    public void ApplyTo(SplitConfig config)
    {
        if (AutoClamp is { } a) config.AutoClamp = a;
        if (DragToZone is { } d) config.DragToZone = d;
        if (KeepOnOneDisplay is { } k) config.KeepOnOneDisplay = k;
        if (HideWindowsTaskbar is { } h) config.HideWindowsTaskbar = h;
        if (TaskbarAutoHide is { } autoHide) config.TaskbarAutoHide = autoHide;
        if (ShowBars is { } bars) config.ShowBars = bars;
        if (SuppressWindowsSnap is { } s) config.SuppressWindowsSnap = s;
        if (StopMinimiseAnimation is { } m) config.StopMinimiseAnimation = m;
    }
}
