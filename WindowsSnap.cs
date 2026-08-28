using static ScweenSpit.Native;

namespace ScweenSpit;

/// <summary>
/// Turns off Windows' own window arranging (Aero Snap edge-snapping, snap sizing, and dock-moving)
/// so it stops fighting our zones — dragging a window to a screen edge no longer half-tiles it, and
/// Win+Arrow stops re-tiling behind our back.
///
/// These are per-user SYSTEM settings, not app settings: if we turn them off and then die without
/// restoring, the user is left with a changed desktop. So the original values are handed back to the
/// caller to persist, and restored on the next launch even after a crash.
/// </summary>
public static class WindowsSnap
{
    private static readonly (uint Get, uint Set, string Name)[] Settings =
    [
        (SPI_GETWINARRANGING, SPI_SETWINARRANGING, "WinArranging"),
        (SPI_GETSNAPSIZING,   SPI_SETSNAPSIZING,   "SnapSizing"),
        (SPI_GETDOCKMOVING,   SPI_SETDOCKMOVING,   "DockMoving"),
    ];

    /// <summary>Reads the current values, then disables all three. Returns what they were.</summary>
    public static int[] Suppress()
    {
        var original = new int[Settings.Length];
        for (int i = 0; i < Settings.Length; i++)
        {
            original[i] = Read(Settings[i].Get);
            Write(Settings[i].Set, 0);
        }

        // All three off is almost certainly a previous run of this program that died before putting
        // them back, not a preference. Windows ships them on; turning off Snap in Settings clears
        // the first of the three and leaves the others alone; and nothing in the user interface
        // turns off all three together except this. Recording that as "how the machine was" is how
        // snapping stays broken for good — every restore afterwards faithfully puts back "off".
        if (original.All(v => v == 0))
        {
            Log.Write("windows snap was already fully off — treating that as our own leftover, "
                    + "and recording the Windows defaults to restore instead");
            for (int i = 0; i < original.Length; i++) original[i] = 1;
            return original;
        }

        Log.Write($"windows snap suppressed (was: {string.Join(",", original)})");
        return original;
    }

    /// <summary>
    /// Whether a recorded set of originals says "everything off" — which no user chose, and which
    /// would restore to a machine that cannot snap.
    /// </summary>
    public static bool IsAllOff(int[]? original) =>
        original is { Length: > 0 } && original.All(v => v == 0);

    /// <summary>Puts back whatever <see cref="Suppress"/> reported. Tolerates a short/garbled array.</summary>
    public static void Restore(int[]? original)
    {
        for (int i = 0; i < Settings.Length; i++)
            Write(Settings[i].Set, original is not null && i < original.Length ? original[i] : 1);

        Log.Write("windows snap restored");
    }

    private static int Read(uint action)
    {
        int value = 1;   // Windows ships these enabled; assume that if the query fails
        if (!SystemParametersInfoGet(action, 0, ref value, 0))
            Log.Write($"SystemParametersInfo get 0x{action:X} failed err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        return value;
    }

    private static void Write(uint action, int value)
    {
        if (!SystemParametersInfoSet(action, 0, value, SPIF_SENDCHANGE))
            Log.Write($"SystemParametersInfo set 0x{action:X}={value} failed err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }
}
