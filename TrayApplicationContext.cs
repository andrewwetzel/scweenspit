using System.Windows.Forms;
using System.Drawing;
using static ScweenSpit.Native;

namespace ScweenSpit;

/// <summary>Tray presence, global hotkeys, and the lifetime of everything else.</summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private const int HotkeyPrev = 1, HotkeyNext = 2, HotkeyZones = 3, HotkeySpan = 4;

    private readonly NotifyIcon tray;
    private readonly HotkeyWindow hotkeys;
    private readonly ZoneManager zones;
    private readonly WinEventHookService hook;
    private readonly ZoneOverlay overlay = new();
    private readonly BarManager bars = new();

    // Sampling the foreground window when the menu item is clicked always returns our own window:
    // NotifyIcon calls SetForegroundWindow on its hidden window before showing the menu. So track
    // it continuously instead.
    private readonly System.Windows.Forms.Timer foregroundWatch = new() { Interval = 400 };
    private IntPtr lastForeground;

    // Explorer puts an auto-hidden taskbar back on screen whenever the pointer reaches its edge,
    // so keeping it hidden means saying so repeatedly rather than once.
    private readonly System.Windows.Forms.Timer taskbarWatch = new() { Interval = 2000 };

    // Debounced: a thickness spinner fires on every step, and re-placing every window under the bar
    // on each one would be both slow and unpleasant to watch.
    private readonly System.Windows.Forms.Timer reflow = new() { Interval = 600 };
    private readonly SplitConfig config;
    private SettingsForm? settings;
    private UpdateInfo? pendingUpdate;

    public TrayApplicationContext()
    {
        config = SplitConfig.Load();
        zones  = new ZoneManager(config);
        hook   = new WinEventHookService(zones) { Overlay = overlay };

        overlay.ZonesEdited += (device, edited) =>
        {
            config.SetZones(device, edited);
            Log.Write($"zones resized on {device}: {edited.Count} zones");
            hook.CancelPending();
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

        bars.MenuRequested += at =>
        {
            BuildMenu();
            tray.ContextMenuStrip!.Show(at);
        };

        foregroundWatch.Tick += (_, _) => TrackForeground();
        foregroundWatch.Start();

        // Re-assert only the hide. Re-applying auto-hide here would make Explorer re-lay-out every
        // two seconds, which is itself what puts the taskbar back.
        taskbarWatch.Tick += (_, _) => { if (config.HideWindowsTaskbar) Taskbar.Hide(true); };
        reflow.Tick += (_, _) => { reflow.Stop(); ReflowAroundBars(); };

        ApplySnapSuppression();
        ApplyTaskbarVisibility();
        bars.Apply(config, zones);
        if (config.AutoClamp || config.DragToZone) hook.Start();
        RegisterHotkeys();
        UpdateTrayText();
        LogStartup();
        CheckForUpdatesQuietly();
    }

    // ---- tray menu ---------------------------------------------------------

    private void BuildMenu()
    {
        var items = tray.ContextMenuStrip!.Items;
        items.Clear();

        items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()) { Font = new Font("Segoe UI", 9f, FontStyle.Bold) });

        if (pendingUpdate is { } update)
            items.Add(new ToolStripMenuItem($"Update to {update.Version}…", null, (_, _) => OpenSettings())
            {
                ForeColor = Theme.Accent,
            });
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem(overlay.Visible ? "Hide zones" : "Show zones", null, (_, _) => overlay.Toggle(zones)));
        items.Add(new ToolStripMenuItem("Auto-clamp", null, (_, _) => { config.AutoClamp = !config.AutoClamp; config.Save(); ApplyChanges(); })
        {
            Checked = config.AutoClamp && hook.Running,
        });
        items.Add(new ToolStripMenuItem("Exclude active window's app", null, (_, _) => ExcludeActiveApp()));
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Exit", null, (_, _) => { tray.Visible = false; ExitThread(); }));
    }

    private void OpenSettings()
    {
        // An external WM_CLOSE (Task Manager "End task", taskbar Close) reports a CloseReason that
        // OnFormClosing does not cancel, so the instance can genuinely be disposed underneath us.
        if (settings is null || settings.IsDisposed)
            settings = new SettingsForm(config, zones, overlay, ApplyChanges, ReloadConfig,
                                        () => hook.Running, hook.ArrangeAll,
                                        () => { tray.Visible = false; ExitThread(); });
        settings.Reveal();
    }

    // ---- state changes -----------------------------------------------------

    /// <summary>Re-reconciles everything with the current config. Safe to call repeatedly.</summary>
    private void ApplyChanges()
    {
        ApplySnapSuppression();

        // Order matters: free the shell taskbar's reserved strip first, then let our bars claim it.
        ApplyTaskbarVisibility();
        bars.Apply(config, zones);
        bars.Reposition();

        // A bar that just grew is now drawn over whatever was already there.
        reflow.Stop();
        reflow.Start();

        // Drag-to-zone rides the same hooks as clamping, so the hooks must stay up for either.
        if (config.AutoClamp || config.DragToZone) hook.Start(); else hook.Stop();
        UpdateTrayText();
    }

    /// <summary>The tooltip is the one status channel Windows cannot suppress.</summary>
    private void UpdateTrayText()
    {
        int zoneCount = ZoneManager.AllMonitors().Sum(g => zones.ZonesFor(g).Count);

        // hook.Running no longer means "clamping": the hooks also serve drag-to-zone. Report the
        // preference, but let a failed Start() override it rather than lying about being on.
        var text = !config.AutoClamp && !config.DragToZone ? $"ScweenSpit — hotkeys only, {zoneCount} zones"
                 : !hook.Running ? "ScweenSpit — hooks DOWN"
                 : config.AutoClamp ? $"ScweenSpit — clamping, {zoneCount} zones"
                 : $"ScweenSpit — drag-to-zone only, {zoneCount} zones";

        tray.Text = text.Length > 63 ? text[..63] : text;   // NotifyIcon.Text is capped at 63 chars
    }

    private void ReloadConfig()
    {
        var loaded = SplitConfig.Load();
        if (SplitConfig.LastLoadFailed)
        {
            // Replacing a working setup with defaults because of one typo - and handing Aero Snap
            // back system-wide as a side effect - is far worse than doing nothing.
            Notify("config.json could not be read. Keeping the running settings; a copy of the "
                 + "broken file is at config.json.bad.");
            return;
        }

        // The live backup outranks whatever is on disk: this process is holding the suppression,
        // and the on-disk value may predate it. Losing it would strand the user's real settings.
        var liveSnapRestore = config.SnapRestore;
        config.CopyFrom(loaded);
        config.SnapRestore ??= liveSnapRestore;

        hook.CancelPending();   // any armed re-check holds pixel rects from the old layout
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
            if (config.SnapRestore is null)
            {
                // Persist immediately. If this is written only by a later Save(), a kill in between
                // leaves the file saying "nothing to restore" while snap is actually off, and the
                // next launch adopts the already-zeroed values as the originals - permanently.
                config.SnapRestore = WindowsSnap.Suppress();
                config.Save();
            }
        }
        else if (config.SnapRestore is { } saved)
        {
            WindowsSnap.Restore(saved);
            config.SnapRestore = null;
            config.Save();
        }
    }

    private void TrackForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return;

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == Environment.ProcessId) return;          // our own tray/settings/overlay windows
        if (!WinEventHookService.IsClampTarget(fg)) return;

        lastForeground = fg;
    }

    /// <summary>
    /// Reconciles the shell taskbar with the preference, remembering its previous auto-hide state so
    /// it can be put back — including after a launch that was killed rather than closed.
    /// </summary>
    private void ApplyTaskbarVisibility()
    {
        if (config.HideWindowsTaskbar)
        {
            if (config.TaskbarRestore is null)
            {
                config.TaskbarRestore = Taskbar.AutoHide;
                config.Save();          // persisted before we change anything, not after
            }
            Taskbar.SetHidden(true);
            taskbarWatch.Start();
        }
        else if (config.TaskbarRestore is { } wasAutoHidden)
        {
            taskbarWatch.Stop();
            Taskbar.SetHidden(false);
            Taskbar.AutoHide = wasAutoHidden;
            config.TaskbarRestore = null;
            config.Save();
        }
    }

    /// <summary>Moves windows out from under any bar that has taken space they were occupying.</summary>
    private void ReflowAroundBars()
    {
        foreach (var geo in ZoneManager.AllMonitors())
        {
            if (!config.Bars.TryGetValue(geo.Device, out var bar)) continue;

            // Only zone-scoped bars need this. A full-display bar is an appbar, so Windows shrinks
            // the work area and applications move themselves.
            if (zones.BarStrip(geo, bar) is { } strip) hook.ArrangeOverlapping(strip);
        }
    }

    private void ExcludeActiveApp()
    {
        var hWnd = lastForeground;
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) { Notify("No recent window to exclude."); return; }

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
                Log.Write($"    zone {i}: {rects[i]}  ({rects[i].Rect.Width}x{rects[i].Rect.Height})");
        }

        Notify(monitors.Count == 0
            ? "No monitors detected — see the log."
            : $"{monitors.Count} monitor(s), {total} zones. Clamping {(hook.Running ? "active" : "off")}.");
    }

    /// <summary>
    /// Looks for a newer release in the background, at most once a day, and does no more than say so.
    /// Installing is always a deliberate act — an app that replaces itself unasked is a liability.
    /// </summary>
    private async void CheckForUpdatesQuietly()
    {
        if (!config.CheckForUpdates) return;
        if (config.LastUpdateCheck is { } last && DateTime.Now - last < TimeSpan.FromDays(1)) return;

        try
        {
            var update = await Updater.CheckAsync(config);
            config.LastUpdateCheck = DateTime.Now;
            config.Save();

            if (update is null) return;

            pendingUpdate = update;
            Notify($"Version {update.Version} is available — open Settings to install it.");
        }
        catch (Exception ex)
        {
            // A failed check is not worth interrupting anyone over.
            Log.WriteOnce("update-check", $"update check failed: {ex.Message}");
        }
    }

    private void Notify(string message) =>
        tray.ShowBalloonTip(3000, "ScweenSpit", message, ToolTipIcon.Info);

    // ---- hotkeys -----------------------------------------------------------

    private void RegisterHotkeys()
    {
        const uint mods = MOD_WIN | MOD_ALT | MOD_NOREPEAT;
        bool ok = RegisterHotKey(hotkeys.Handle, HotkeyPrev,  mods, VK_LEFT)
                & RegisterHotKey(hotkeys.Handle, HotkeyNext,  mods, VK_RIGHT)
                & RegisterHotKey(hotkeys.Handle, HotkeyZones, mods, VK_Z)
                & RegisterHotKey(hotkeys.Handle, HotkeySpan,  mods, VK_S);

        if (!ok) Notify("Some Win+Alt hotkeys could not be registered (already in use).");
    }

    private void OnHotkey(int id)
    {
        if (id == HotkeyZones) { overlay.Toggle(zones); return; }

        if (id == HotkeySpan)
        {
            var target = GetForegroundWindow();
            if (target == IntPtr.Zero || !WinEventHookService.IsClampTarget(target)) return;

            bool allowed = hook.ToggleSpanAllowed(target);
            Notify(allowed
                ? "This window may span displays."
                : "This window will be kept on one display.");
            return;
        }

        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero || !WinEventHookService.IsClampTarget(hWnd)) return;
        if (!GetWindowRect(hWnd, out var win) || !ZoneManager.TryGetMonitor(hWnd, out var geo)) return;

        // The preset's own label promises inaction; an explicit gesture should not override that.
        if (zones.IsOptedOut(geo)) { Notify($"{geo.Device.TrimStart('\\', '.')} is set to be left alone."); return; }
        if (config.IsExcluded(WinEventHookService.OwnerProcessOf(hWnd), Native.ClassNameOf(hWnd))) return;

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
            UnregisterHotKey(hotkeys.Handle, HotkeySpan);

            foregroundWatch.Dispose();
            bars.Dispose();          // releases the appbar reservations
            hook.Dispose();
            overlay.Dispose();
            settings?.Dispose();

            taskbarWatch.Dispose();
            reflow.Dispose();
            if (config.TaskbarRestore is { } wasAutoHidden)
            {
                Taskbar.SetHidden(false);
                Taskbar.AutoHide = wasAutoHidden;
                config.TaskbarRestore = null;
                config.Save();
            }

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
