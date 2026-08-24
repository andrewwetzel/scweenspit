using System.Windows.Forms;

namespace ScweenSpit;

/// <summary>
/// Keeps the set of ScweenSpit taskbars in step with the configuration. Bars reserve screen space
/// from every application on the machine, so they are created and destroyed deliberately rather
/// than rebuilt on every settings change.
/// </summary>
public sealed class BarManager : IDisposable
{
    private readonly record struct Applied(string Edge, int Thickness, bool ThisDisplayOnly,
                                           bool IconsOnly, bool ShowStatus, int? Zone);

    private readonly Dictionary<string, TaskbarWindow> bars = [];
    private readonly Dictionary<string, Applied> applied = [];

    public int Count => bars.Count;

    /// <summary>Raised when a bar's ScweenSpit button is clicked, with the screen position to open at.</summary>
    public event Action<System.Drawing.Point>? MenuRequested;

    private ZoneManager? zones;

    public void Apply(SplitConfig config, ZoneManager zoneManager)
    {
        zones = zoneManager;
        var monitors = ZoneManager.AllMonitors().ToDictionary(m => m.Device, m => m);

        // Take down bars that are no longer configured, or whose display has gone away.
        foreach (var device in bars.Keys.ToList())
            if (!config.Bars.ContainsKey(device) || !monitors.ContainsKey(device))
                Close(device);

        foreach (var (device, settings) in config.Bars)
        {
            if (!monitors.TryGetValue(device, out var monitor)) continue;

            var wanted = new Applied(settings.Edge, settings.Thickness, settings.ThisDisplayOnly,
                                     settings.IconsOnly, settings.ShowStatus, settings.Zone);
            if (applied.TryGetValue(device, out var current) && current == wanted && bars.ContainsKey(device))
                continue;

            Close(device);

            try
            {
                var bar = new TaskbarWindow(monitor, settings, zoneManager);
                bar.MenuRequested += p => MenuRequested?.Invoke(p);

                // Give it its rectangle before the handle exists, so it is created on the display it
                // belongs to rather than being born on the primary and moved afterwards.
                bar.Bounds = new System.Drawing.Rectangle(
                    monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, monitor.Bounds.Height);

                bars[device] = bar;
                applied[device] = wanted;
                bar.Show();
                Log.Write($"bar on {device}: {settings.Edge} {settings.Thickness}px");
            }
            catch (Exception ex)
            {
                Log.Write($"could not create bar on {device}: {ex}");
            }
        }
    }

    /// <summary>
    /// Re-negotiates every bar's strip. Called when the space available changes for reasons the
    /// appbar protocol will not tell us about — hiding the shell's taskbar, most of all, which frees
    /// its reservation and would otherwise leave our bar floating above the gap it left behind.
    /// </summary>
    public void Reposition()
    {
        foreach (var bar in bars.Values)
        {
            try { bar.Reposition(); }
            catch (Exception ex) { Log.Write($"reposition failed on {bar.Device}: {ex.Message}"); }
        }
    }

    private void Close(string device)
    {
        if (!bars.Remove(device, out var bar)) return;

        // Disposing releases the appbar registration; skipping it leaves the desktop permanently
        // short of that strip until the next sign-in.
        bar.Close();
        bar.Dispose();
        applied.Remove(device);
        Log.Write($"bar removed from {device}");
    }

    public void Dispose()
    {
        foreach (var device in bars.Keys.ToList()) Close(device);
    }
}
