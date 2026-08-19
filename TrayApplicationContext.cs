using System.Windows.Forms;
using System.Drawing;
using static ScweenSpit.Native;

namespace ScweenSpit;

/// <summary>Tray presence, global hotkeys, and the lifetime of everything else.</summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private const int HotkeyPrev = 1, HotkeyNext = 2, HotkeyZones = 3;

    private readonly NotifyIcon tray;
    private readonly HotkeyWindow hotkeys;
    private readonly ZoneManager zones;
    private readonly WinEventHookService hook;
    private readonly ZoneOverlay overlay = new();
    private readonly SplitConfig config;
    private SettingsForm? settings;

    public TrayApplicationContext()
    {
        config = SplitConfig.Load();
        zones  = new ZoneManager(config);
        hook   = new WinEventHookService(zones) { Overlay = overlay };

        overlay.ZonesEdited += (device, edited) =>
        {
            config.SetZones(device, edited);
            Log.Write($"zones resized on {device}: {edited.Count} zones");
            UpdateTrayText();
        };

        overlay.MarginsEdited += (device, m) =>
        {
            config.SetMargins(device, m);
            Log.Write($"margins on {device}: T{m.Top} B{m.Bottom} L{m.Left} R{m.Right}");
        };

        hotkeys = new HotkeyWindow(OnHotkey);
        hotkeys.CreateControl();

        tray = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "ScweenSpit",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        tray.ContextMenuStrip.Opening += (_, e) =>
        {
            BuildMenu();
            // WinForms pre-sets Cancel from DisplayedItems.Count, which is still 0 the first time
            // the strip is shown. Without this the very first right-click silently does nothing.
            e.Cancel = tray.ContextMenuStrip!.Items.Count == 0;
        };
        BuildMenu();
        tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) OpenSettings(); };

        ApplySnapSuppression();
        if (config.AutoClamp) hook.Start();
        RegisterHotkeys();
        UpdateTrayText();
        LogStartup();
    }

    // ---- tray menu ---------------------------------------------------------

    private void BuildMenu()
    {
        var items = tray.ContextMenuStrip!.Items;
        items.Clear();

        items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()) { Font = new Font("Segoe UI", 9f, FontStyle.Bold) });
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem(overlay.Visible ? "Hide zones" : "Show zones", null, (_, _) => overlay.Toggle(zones)));
        items.Add(new ToolStripMenuItem("Auto-clamp", null, (_, _) => { config.AutoClamp = !config.AutoClamp; config.Save(); ApplyChanges(); })
        {
            Checked = hook.Running,
        });
        items.Add(new ToolStripMenuItem("Exclude active window's app", null, (_, _) => ExcludeActiveApp()));
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Exit", null, (_, _) => { tray.Visible = false; ExitThread(); }));
    }

    private void OpenSettings()
    {
        settings ??= new SettingsForm(config, zones, overlay, ApplyChanges, ReloadConfig, () => hook.Running);
        settings.Reveal();
    }

    // ---- state changes -----------------------------------------------------

    /// <summary>Re-reconciles everything with the current config. Safe to call repeatedly.</summary>
    private void ApplyChanges()
    {
        ApplySnapSuppression();
        if (config.AutoClamp) hook.Start(); else hook.Stop();
        UpdateTrayText();
    }

    /// <summary>The tooltip is the one status channel Windows cannot suppress.</summary>
    private void UpdateTrayText()
    {
        int zoneCount = ZoneManager.AllMonitors().Sum(g => zones.ZonesFor(g).Count);
        var text = hook.Running ? $"ScweenSpit — clamping, {zoneCount} zones" : "ScweenSpit — clamping OFF";
        tray.Text = text.Length > 63 ? text[..63] : text;   // NotifyIcon.Text is capped at 63 chars
    }

    private void ReloadConfig()
    {
        config.CopyFrom(SplitConfig.Load());
        ApplyChanges();
        overlay.Flash(zones);
        Notify("Config reloaded");
    }

    /// <summary>
    /// Reconciles Windows' snap settings with the preference. Restoring from a persisted backup
    /// covers a previous run that was killed rather than closed.
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

    // ---- diagnostics -------------------------------------------------------

    private void LogStartup()
    {
        var monitors = ZoneManager.AllMonitors();
        Log.Write($"autoclamp={config.AutoClamp} drag={config.DragToZone}/{config.DragModifier} " +
                  $"debounce={config.DebounceMs}ms hooks={(hook.Running ? "up" : "DOWN")} monitors={monitors.Count}");

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
            ? "No monitors detected — see the log."
            : $"{monitors.Count} monitor(s), {total} zones. Clamping {(hook.Running ? "active" : "off")}.");
    }

    private void Notify(string message) =>
        tray.ShowBalloonTip(3000, "ScweenSpit", message, ToolTipIcon.Info);

    // ---- hotkeys -----------------------------------------------------------

    private void RegisterHotkeys()
    {
        const uint mods = MOD_WIN | MOD_ALT | MOD_NOREPEAT;
        bool ok = RegisterHotKey(hotkeys.Handle, HotkeyPrev,  mods, VK_LEFT)
                & RegisterHotKey(hotkeys.Handle, HotkeyNext,  mods, VK_RIGHT)
                & RegisterHotKey(hotkeys.Handle, HotkeyZones, mods, VK_Z);

        if (!ok) Notify("Some Win+Alt hotkeys could not be registered (already in use).");
    }

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
            settings?.Dispose();

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
