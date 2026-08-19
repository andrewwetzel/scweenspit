using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using static ScweenSpit.Native;

namespace ScweenSpit;

/// <summary>Tray icon, menu, and the hidden window that owns the global hotkeys.</summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private const int HotkeyPrev = 1, HotkeyNext = 2;

    private readonly NotifyIcon tray;
    private readonly HotkeyWindow hotkeys;
    private readonly ZoneManager zones;
    private readonly WinEventHookService hook;
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
        tray.DoubleClick += (_, _) => ToggleAutoClamp();

        if (config.AutoClamp) hook.Start();
        RegisterHotkeys();
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

        var layouts = new ToolStripMenuItem("Layout");
        foreach (var geo in ZoneManager.AllMonitors())
            layouts.DropDownItems.Add(MonitorMenu(geo));
        items.Add(layouts);

        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Reload config", null, (_, _) => ReloadConfig()));
        items.Add(new ToolStripMenuItem("Open config file", null, (_, _) => OpenConfig()));
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

    private void ReloadConfig()
    {
        config = SplitConfig.Load();
        zones.Config = config;

        if (config.AutoClamp) hook.Start(); else hook.Stop();
        Notify("Config reloaded");
    }

    private void OpenConfig()
    {
        try
        {
            if (!File.Exists(SplitConfig.Path)) config.Save();
            Process.Start(new ProcessStartInfo(SplitConfig.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify($"Could not open config: {ex.Message}");
        }
    }

    // ---- hotkeys -----------------------------------------------------------

    private void RegisterHotkeys()
    {
        const uint mods = MOD_WIN | MOD_ALT | MOD_NOREPEAT;
        bool ok = RegisterHotKey(hotkeys.Handle, HotkeyPrev, mods, VK_LEFT)
                & RegisterHotKey(hotkeys.Handle, HotkeyNext, mods, VK_RIGHT);

        if (!ok) Notify("Win+Alt+Left/Right could not be registered (already in use).");
    }

    /// <summary>Cycles the foreground window through the zones of the monitor it is on.</summary>
    private void OnHotkey(int id)
    {
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
            hook.Dispose();
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
