using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using static ScweenSpit.Native;

namespace ScweenSpit;

/// <summary>Tray icon, menu, and the hidden window that owns the global hotkeys.</summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private const int HotkeyPrev = 1, HotkeyNext = 2, HotkeyZones = 3;

    private readonly NotifyIcon tray;
    private readonly HotkeyWindow hotkeys;
    private readonly ZoneManager zones;
    private readonly WinEventHookService hook;
    private readonly ZoneOverlay overlay = new();
    private SplitConfig config;

    public TrayApplicationContext()
    {
        config = SplitConfig.Load();
        zones  = new ZoneManager(config);
        hook   = new WinEventHookService(zones);

        hotkeys = new HotkeyWindow(OnHotkey);
        hotkeys.CreateControl();

        tray = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "ScweenSpit",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        tray.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        BuildMenu();

        // Left-click opens the menu too — one less way to be stuck with an icon that seems inert.
        tray.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            BuildMenu();
            tray.ContextMenuStrip!.Show(Cursor.Position);
        };

        ApplySnapSuppression();

        if (config.AutoClamp) hook.Start();
        RegisterHotkeys();
        LogStartup();
    }

    /// <summary>Dumps the whole decision surface once, so a single run explains itself.</summary>
    private void LogStartup()
    {
        var monitors = ZoneManager.AllMonitors();
        Log.Write($"autoclamp={config.AutoClamp} debounce={config.DebounceMs}ms " +
                  $"hooks={(hook.Running ? "up" : "DOWN")} monitors={monitors.Count}");

        int total = 0;
        foreach (var geo in monitors)
        {
            var rects = zones.ZonesFor(geo);
            total += rects.Count;
            Log.Write($"monitor {geo.Device} bounds={geo.Bounds} work={geo.Work} optedOut={zones.IsOptedOut(geo)}");
            for (int i = 0; i < rects.Count; i++)
                Log.Write($"    zone {i}: {rects[i]}  ({rects[i].Width}x{rects[i].Height})");
        }

        Notify(monitors.Count == 0
            ? "No monitors detected - see the log."
            : $"{monitors.Count} monitor(s), {total} zones. Clamping {(hook.Running ? "active" : "OFF")}.");
    }

    // ---- menu --------------------------------------------------------------

    private void BuildMenu()
    {
        var items = tray.ContextMenuStrip!.Items;
        items.Clear();

        var clamp = new ToolStripMenuItem("Auto-clamp", null, (_, _) => ToggleAutoClamp())
        {
            Checked = config.AutoClamp,
            CheckOnClick = false,
        };
        items.Add(clamp);
        items.Add(new ToolStripSeparator());

        items.Add(new ToolStripMenuItem(overlay.Visible ? "Hide zones" : "Show zones", null,
            (_, _) => overlay.Toggle(zones)));

        items.Add(new ToolStripSeparator());

        var layouts = new ToolStripMenuItem("Layout");
        foreach (var geo in ZoneManager.AllMonitors())
            layouts.DropDownItems.Add(MonitorMenu(geo));
        items.Add(layouts);

        items.Add(new ToolStripMenuItem("Suppress Windows snap", null, (_, _) => ToggleSnapSuppression())
        {
            Checked = config.SuppressWindowsSnap,
            ToolTipText = "Stops Aero Snap and Win+Arrow from competing with these zones.",
        });

        items.Add(new ToolStripMenuItem("Start with Windows", null,
            (_, _) => Startup.Set(!Startup.Enabled)) { Checked = Startup.Enabled });

        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Exclude active window's app", null, (_, _) => ExcludeActiveApp()));
        items.Add(new ToolStripMenuItem("Reload config", null, (_, _) => ReloadConfig()));
        items.Add(new ToolStripMenuItem("Open config file", null, (_, _) => OpenConfig()));
        items.Add(new ToolStripMenuItem("Open log file", null, (_, _) => Open(Log.LogPath)));
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Exit", null, (_, _) => { tray.Visible = false; ExitThread(); }));
    }

    private ToolStripMenuItem MonitorMenu(MonitorGeometry geo)
    {
        string label = $"{geo.Device.TrimStart('\\', '.')}  {geo.Bounds.Width}×{geo.Bounds.Height}";
        var item = new ToolStripMenuItem(label);
        var current = config.ZonesFor(geo.Device);

        foreach (var (name, make) in SplitConfig.Presets)
        {
            var preset = make();
            item.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
            {
                config.SetZones(geo.Device, make());
                Log.Write($"layout {geo.Device} -> {name}");
                overlay.Flash(zones);
            })
            { Checked = SameZones(current, preset) });
        }
        return item;
    }

    private static bool SameZones(List<FracRect> a, List<FracRect> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            Math.Abs(p.First.L - p.Second.L) < 0.005 && Math.Abs(p.First.T - p.Second.T) < 0.005 &&
            Math.Abs(p.First.R - p.Second.R) < 0.005 && Math.Abs(p.First.B - p.Second.B) < 0.005);

    // ---- actions -----------------------------------------------------------

    private void ToggleAutoClamp()
    {
        config.AutoClamp = !config.AutoClamp;
        config.Save();

        // "off" means genuinely unhooked — zero callback traffic, not an early return.
        if (config.AutoClamp) hook.Start(); else hook.Stop();
    }

    /// <summary>Adds whatever is in front to the exclusion list — the quick way to stop the tool
    /// interfering with a game or a video player without hand-editing JSON.</summary>
    private void ExcludeActiveApp()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero) { Notify("No active window."); return; }

        var name = WinEventHookService.OwnerProcessOf(hWnd);
        if (name is "?" or "ScweenSpit") { Notify($"Cannot exclude '{name}'."); return; }

        if (config.Exclude.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            config.Exclude.RemoveAll(e => e.Equals(name, StringComparison.OrdinalIgnoreCase));
            Notify($"{name} is no longer excluded.");
        }
        else
        {
            config.Exclude.Add(name);
            Notify($"{name} will no longer be clamped.");
        }
        config.Save();
    }

    private void ToggleSnapSuppression()
    {
        config.SuppressWindowsSnap = !config.SuppressWindowsSnap;
        ApplySnapSuppression();
        config.Save();
    }

    /// <summary>
    /// Reconciles Windows' snap settings with the preference. Restoring from a persisted backup
    /// covers the case where a previous run was killed rather than closed.
    /// </summary>
    private void ApplySnapSuppression()
    {
        if (config.SuppressWindowsSnap)
        {
            config.SnapRestore ??= WindowsSnap.Suppress();
        }
        else if (config.SnapRestore is { } saved)
        {
            WindowsSnap.Restore(saved);
            config.SnapRestore = null;
        }
    }

    private void ReloadConfig()
    {
        config = SplitConfig.Load();
        zones.Config = config;

        if (config.AutoClamp) hook.Start(); else hook.Stop();
        ApplySnapSuppression();
        overlay.Flash(zones);
        Notify("Config reloaded");
    }

    private void OpenConfig()
    {
        if (!File.Exists(SplitConfig.Path)) config.Save();
        Open(SplitConfig.Path);
    }

    private void Open(string path)
    {
        try
        {
            if (!File.Exists(path)) { Notify($"Not created yet: {path}"); return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify($"Could not open {path}: {ex.Message}");
        }
    }

    // ---- hotkeys -----------------------------------------------------------

    private void RegisterHotkeys()
    {
        const uint mods = MOD_WIN | MOD_ALT | MOD_NOREPEAT;
        bool ok = RegisterHotKey(hotkeys.Handle, HotkeyPrev,  mods, VK_LEFT)
                & RegisterHotKey(hotkeys.Handle, HotkeyNext,  mods, VK_RIGHT)
                & RegisterHotKey(hotkeys.Handle, HotkeyZones, mods, VK_Z);

        if (!ok) Notify("Some Win+Alt hotkeys could not be registered (already in use).");
    }

    /// <summary>Cycles the foreground window through the zones of the monitor it is on.</summary>
    private void OnHotkey(int id)
    {
        if (id == HotkeyZones) { overlay.Toggle(zones); return; }

        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero || !WinEventHookService.IsClampTarget(hWnd)) return;
        if (!GetWindowRect(hWnd, out var win) || !ZoneManager.TryGetMonitor(hWnd, out var geo)) return;

        var rects = zones.ZonesFor(geo);
        if (rects.Count == 0) return;

        int delta = id == HotkeyNext ? 1 : -1;
        int index = (ZoneManager.PickZoneIndex(rects, win) + delta + rects.Count) % rects.Count;

        hook.Apply(hWnd, rects[index]);
    }

    // ---- plumbing ----------------------------------------------------------

    private void Notify(string message) =>
        tray.ShowBalloonTip(3000, "ScweenSpit", message, ToolTipIcon.Info);

    /// <summary>A 70/30 split drawn at runtime, so there is no .ico asset to ship.</summary>
    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.FillRectangle(Brushes.DodgerBlue, 2, 6, 19, 20);
            g.FillRectangle(Brushes.Gainsboro, 23, 6, 7, 20);
        }
        return Icon.FromHandle(bmp.GetHicon()); // process-lifetime handle
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UnregisterHotKey(hotkeys.Handle, HotkeyPrev);
            UnregisterHotKey(hotkeys.Handle, HotkeyNext);
            UnregisterHotKey(hotkeys.Handle, HotkeyZones);
            hook.Dispose();
            overlay.Dispose();

            if (config.SnapRestore is { } saved)
            {
                WindowsSnap.Restore(saved);
                config.SnapRestore = null;
                config.Save();
            }

            tray.Visible = false;
            tray.Dispose();
            hotkeys.DestroyHandle();
        }
        base.Dispose(disposing);
    }

    /// <summary>Hidden window: RegisterHotKey needs an HWND and a WndProc to deliver WM_HOTKEY to.</summary>
    private sealed class HotkeyWindow(Action<int> onHotkey) : NativeWindow
    {
        public void CreateControl() => CreateHandle(new CreateParams());

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) onHotkey((int)m.WParam);
            base.WndProc(ref m);
        }
    }
}
