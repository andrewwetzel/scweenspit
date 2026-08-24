using System.Windows.Forms;

namespace ScweenSpit;

/// <summary>
/// Keeps the set of ScweenSpit taskbars in step with the configuration. Bars reserve screen space
/// from every application on the machine, so they are created and destroyed deliberately rather
/// than rebuilt on every settings change.
/// </summary>
public sealed class BarManager : IDisposable
{
    private readonly record struct Applied(string Edge, int Thickness, bool ThisDisplayOnly);

    private readonly Dictionary<string, TaskbarWindow> bars = [];
    private readonly Dictionary<string, Applied> applied = [];

    public int Count => bars.Count;

    public void Apply(SplitConfig config)
    {
        var monitors = ZoneManager.AllMonitors().ToDictionary(m => m.Device, m => m);

        // Take down bars that are no longer configured, or whose display has gone away.
        foreach (var device in bars.Keys.ToList())
            if (!config.Bars.ContainsKey(device) || !monitors.ContainsKey(device))
                Close(device);

        foreach (var (device, settings) in config.Bars)
        {
            if (!monitors.TryGetValue(device, out var monitor)) continue;

            var wanted = new Applied(settings.Edge, settings.Thickness, settings.ThisDisplayOnly);
            if (applied.TryGetValue(device, out var current) && current == wanted && bars.ContainsKey(device))
                continue;

            Close(device);

            try
            {
                var bar = new TaskbarWindow(monitor, SplitConfig.ParseEdge(settings.Edge),
                                            settings.Thickness, settings.ThisDisplayOnly);
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
