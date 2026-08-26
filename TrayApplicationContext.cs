using System.Windows.Forms;
using System.Drawing;
using Microsoft.Win32;
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

    // Docking raises several display events in a row, so react once things have settled.
    private readonly System.Windows.Forms.Timer displaySettle = new() { Interval = 1500 };
    private readonly Control marshaller = new();
    private string topology = "";
    private readonly SplitConfig config;
    private SettingsForm? settings;
    private UpdateInfo? pendingUpdate;
    private readonly Action raised;

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
            RefitBars();
            UpdateTrayText();
        };

        // A divider drag moves the windows that were filling the zones either side of it, live. A
        // layout you can see the consequences of is a different thing to judge than one you cannot.
        overlay.PreviewBegan += device =>
        {
            if (MonitorFor(device) is { } geo) hook.BeginZonePreview(geo);
        };

        overlay.Previewing += (device, edited) =>
        {
            // Not saved: this runs twenty-five times a second, and the file is not the point of it.
            config.SetZones(device, edited, save: false);
            if (MonitorFor(device) is { } geo) hook.PreviewZones(geo);
        };

        overlay.PreviewEnded += device =>
        {
            // Once more against the layout that was actually committed, rather than leaving the
            // windows where the last frame of the drag put them — those differ by however far the
            // mouse travelled inside the last forty milliseconds.
            if (MonitorFor(device) is { } geo) hook.PreviewZones(geo);
            hook.EndZonePreview();
        };

        overlay.MarginsEdited += (device, m) =>
        {
            config.SetMargins(device, m);
            Log.Write($"margins on {device}: T{m.Top} B{m.Bottom} L{m.Left} R{m.Right}");
            RefitBars();
        };

        hotkeys = new HotkeyWindow(OnHotkey);
        hotkeys.CreateControl();

        tray = new NotifyIcon
        {
            Icon = Theme.AppIcon(),
            Text = "ScweenSpit",
            Visible = true,
            ContextMenuStrip = Theme.Dress(new ContextMenuStrip()),
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

        bars.PinsChanged += () => config.Save();

        // A window we just brought forward may be one that covers the taskbar, or may be blocked by
        // one that does; either way the answer changes the moment the foreground does.
        raised = () => hook.RefreshTopmost();
        WindowList.Raised += raised;

        bars.MenuRequested += at =>
        {
            BuildMenu();
            tray.ContextMenuStrip!.Show(at);
        };

        foregroundWatch.Tick += (_, _) =>
        {
            TrackForeground();

            // A taskbar-covering window is only held above everything while it is in front, so this
            // has to follow the foreground rather than being decided once when it was placed.
            hook.RefreshTopmost();
        };
        foregroundWatch.Start();

        // Re-assert only the hide. Re-applying auto-hide here would make Explorer re-lay-out every
        // two seconds, which is itself what puts the taskbar back.
        taskbarWatch.Tick += (_, _) => { if (config.HideWindowsTaskbar) Taskbar.Hide(true); };
        reflow.Tick += (_, _) => { reflow.Stop(); ReflowAroundBars(); };

        // Touching Handle rather than CreateControl(): that one returns without doing anything for a
        // control that is not visible, and this one never is. Without a handle the BeginInvoke below
        // throws, into a catch that would swallow it.
        _ = marshaller.Handle;

        // What we started in, so the first real change is compared against the arrangement on screen
        // rather than against the empty string.
        topology = DisplayTopology.Signature();

        displaySettle.Tick += (_, _) => { displaySettle.Stop(); DisplaysChanged(); };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Signing out and shutting down end the message loop without disposing anything, so none of
        // the teardown below runs. Auto-hide is a state Explorer keeps, so it would still be on at
        // the next sign-in — a machine handed back in a state the user never chose.
        SystemEvents.SessionEnding += OnSessionEnding;

        ApplySnapSuppression();
        ApplyTaskbarVisibility();
        ApplyAnimationPreference();
        ApplyUsageTracking();
        bars.Apply(config, zones);
        // Follows the menu however it was opened — the keyboard's Windows key as much as our own
        // button, since Windows puts it in the same wrong place either way.
        StartMenu.Watch(bars.StartAnchor);
        if (config.AutoClamp || config.DragToZone) hook.Start();
        RegisterHotkeys();
        UpdateTrayText();
        Startup.Refresh();
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
        items.Add(LayoutMenu());
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem(overlay.Visible ? "Hide zones" : "Show zones", null, (_, _) => overlay.Toggle(zones)));
        items.Add(new ToolStripMenuItem("Drag zone dividers…", null, (_, _) => overlay.Show(zones, OverlayMode.Edit))
        {
            ToolTipText = "Drag the dividers on screen. Windows filling those zones resize as you " +
                          "drag. Click anywhere else, or press Escape, to finish.",
        });
        items.Add(new ToolStripMenuItem("Auto-clamp", null, (_, _) => { config.AutoClamp = !config.AutoClamp; config.Save(); ApplyChanges(); })
        {
            Checked = config.AutoClamp && hook.Running,
        });
        items.Add(new ToolStripMenuItem("Exclude active window's app", null, (_, _) => ExcludeActiveApp()));
        items.Add(new ToolStripSeparator());

        bool handedBack = config.HandedBack is not null;
        items.Add(new ToolStripMenuItem(handedBack ? "Take ScweenSpit's settings back up" : "Hand back to Windows",
                                        null, (_, _) => { if (handedBack) TakeOverAgain(); else HandBackToWindows(); })
        {
            ToolTipText = handedBack
                ? "Put back the zones, bars and taskbar settings that were on before."
                : "Stock Windows: taskbar shown and staying shown, nothing clamped, no bars, snap "
                  + "handed back. Your layouts are kept for when you want them again.",
        });

        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Exit", null, (_, _) => { tray.Visible = false; ExitThread(); }));
    }

    /// <summary>
    /// The layouts available for the display the menu was opened on. Changing a display's split is
    /// the thing most worth reaching quickly, and it used to live three clicks into the settings
    /// window — behind a window that has to be found first.
    /// </summary>
    private ToolStripMenuItem LayoutMenu()
    {
        var at = Cursor.Position;
        if (!ZoneManager.TryGetMonitorAt(new POINT { X = at.X, Y = at.Y }, out var geo))
            return new ToolStripMenuItem("Layout") { Enabled = false };

        var menu = new ToolStripMenuItem($"Layout for this display ({geo.Describe()})");
        Theme.Dress(menu.DropDown);
        var current = config.ZonesFor(geo.Device);
        bool anyMatched = false;

        foreach (var (name, make) in SplitConfig.Presets)
        {
            bool active = ZoneEdges.Same(current, make());
            anyMatched |= active;

            var device = geo.Device;
            menu.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
            {
                config.SetZones(device, make());
                ApplyChanges();
                overlay.Flash(zones);
            })
            {
                Checked = active,
            });
        }

        // A layout dragged into shape matches no preset, and a menu with nothing ticked reads as a
        // menu that does not know what is going on.
        if (!anyMatched)
        {
            menu.DropDownItems.Insert(0, new ToolStripSeparator());
            menu.DropDownItems.Insert(0, new ToolStripMenuItem($"Custom — {current.Count} zones")
            {
                Checked = true,
                Enabled = false,
            });
        }

        return menu;
    }

    /// <summary>
    /// Stock Windows, without throwing anything away: the taskbar back and staying back, nothing
    /// clamped, no bars, snap and the minimise animation handed over. The zone layouts stay in the
    /// config, unenforced, so taking over again is one click rather than a rebuild.
    /// </summary>
    private void HandBackToWindows()
    {
        var before = new DisplayProfile { Name = "Before handing back" };
        before.CaptureFrom(config);
        config.HandedBack = before;

        DisplayProfile.HandsOff().ApplyTo(config);
        config.Save();
        ApplyChanges();

        Notify("Handed back to Windows. The taskbar is back, and your layouts are kept.");
        Log.Write("handed back to Windows");
    }

    private void TakeOverAgain()
    {
        if (config.HandedBack is not { } before) return;

        before.ApplyTo(config);
        config.HandedBack = null;
        config.Save();
        ApplyChanges();

        Notify("ScweenSpit is managing windows again.");
        Log.Write("took its settings back up");
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

    /// <summary>
    /// Brings the bars back into line with a layout that has just moved under them. A bar scoped to
    /// a zone is measured from that zone, so dragging a divider moves the bar — and the strip it
    /// reserves with it, or windows would be left overlapping wherever the bar used to be.
    ///
    /// Deliberately narrower than <see cref="ApplyChanges"/>: nothing else about the configuration
    /// has changed, and that one would take the hooks down and put them back up mid-edit.
    /// </summary>
    private void RefitBars()
    {
        bars.Reposition();

        // A bar that just moved is now drawn over whatever was already there.
        reflow.Stop();
        reflow.Start();
    }

    /// <summary>Re-reconciles everything with the current config. Safe to call repeatedly.</summary>
    private void ApplyChanges()
    {
        ApplySnapSuppression();

        // Order matters: free the shell taskbar's reserved strip first, then let our bars claim it.
        ApplyTaskbarVisibility();
        ApplyAnimationPreference();
        ApplyUsageTracking();
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
    /// <summary>
    /// Points the usage poller at the current configuration. The save callback runs on the poll
    /// thread — claude.ai rotates the session key mid-session and it has to be written back — so it
    /// is marshalled onto this one, where every other write to the config file happens.
    /// </summary>
    private void ApplyUsageTracking() =>
        ClaudeUsage.Configure(config.Claude, () =>
        {
            if (tray.ContextMenuStrip is { IsHandleCreated: true } menu && menu.InvokeRequired)
                menu.BeginInvoke(config.Save);
            else
                config.Save();
        });

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
        // Unconditional, and not driven by any record: a run that was killed leaves the shell's bar
        // hidden, and a record that was lost with it leaves nothing to notice. Whatever the file
        // believes, if we are not hiding the taskbar then the taskbar should be on screen.
        if (!config.HideWindowsTaskbar && !SystemRestore.Visible())
        {
            Log.Write("taskbar was hidden and nothing here asked for that; showing it");
            Taskbar.SetHidden(false);
        }

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

        // Auto-hide as a setting we hold, rather than one we only ever pushed at Windows. Without a
        // record of it a profile cannot put it back, which is how undocking left a taskbar that only
        // appeared on hover.
        if (!config.HideWindowsTaskbar && config.TaskbarAutoHide is { } wanted && Taskbar.AutoHide != wanted)
            Taskbar.AutoHide = wanted;
    }

    /// <summary>
    /// Raised on a system thread, and several times over while a dock settles. Marshalled onto the
    /// UI thread and debounced, because everything it leads to touches windows.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            marshaller.BeginInvoke(() => { displaySettle.Stop(); displaySettle.Start(); });
        }
        catch (Exception ex) { Log.Write($"display change could not be marshalled: {ex.Message}"); }
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        taskbarWatch.Stop();
        SystemRestore.OnSessionEnding(config);
    }

    /// <summary>The display with this device name, if it is still attached.</summary>
    private static MonitorGeometry? MonitorFor(string device)
    {
        foreach (var geo in ZoneManager.AllMonitors())
            if (geo.Device == device) return geo;
        return null;
    }

    private void DisplaysChanged()
    {
        var now = DisplayTopology.Signature();
        bool different = now != topology;
        topology = now;

        Log.Write($"displays now {now} — {DisplayTopology.Describe()}");

        if (different && config.FollowDisplayChanges && config.Profiles.TryGetValue(now, out var profile))
        {
            profile.ApplyTo(config);
            config.Save();
            Notify($"Switched to {profile.Name ?? now}.");
            Log.Write($"applied profile for {now}");
        }
        else if (different && config.HideWindowsTaskbar)
        {
            // Nothing saved for this arrangement, and the shell's taskbar is still hidden — which on
            // a laptop that has just been undocked is the whole screen's worth of difference between
            // a working machine and one with no way to reach anything. Say so, because the setting
            // that fixes it is two clicks away and impossible to guess.
            Notify(config.FollowDisplayChanges
                ? "New display arrangement. Save settings for it in Settings \u2192 Displays."
                : "Displays changed. Following them is switched off in Settings \u2192 Displays.");
            Log.Write($"no profile for {now}; settings left as they are");
        }

        // Whether or not a profile matched: a display may have gone, and a bar reserving space on it
        // has to go with it. Nothing else in the app watches for that.
        ApplyChanges();
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

    /// <summary>Same bookkeeping as the snap and taskbar settings: remember, change, put back.</summary>
    private void ApplyAnimationPreference()
    {
        if (config.StopMinimiseAnimation)
        {
            if (config.AnimationRestore is null)
            {
                config.AnimationRestore = Taskbar.MinimiseAnimation;
                config.Save();
            }
            Taskbar.MinimiseAnimation = false;
        }
        else if (config.AnimationRestore is { } wasOn)
        {
            Taskbar.MinimiseAnimation = wasOn;
            config.AnimationRestore = null;
            config.Save();
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Before anything else, and before anything that could throw. Handing the machine back
            // is the one part of shutting down that the user cannot do without, and it used to come
            // last — after six disposals, any of which failing would have taken it with them.
            //
            // The watchdog goes first or it puts the taskbar straight back.
            taskbarWatch.Stop();
            SystemRestore.Everything(config);

            UnregisterHotKey(hotkeys.Handle, HotkeyPrev);
            UnregisterHotKey(hotkeys.Handle, HotkeyNext);
            UnregisterHotKey(hotkeys.Handle, HotkeyZones);
            UnregisterHotKey(hotkeys.Handle, HotkeySpan);

            foregroundWatch.Dispose();
            ClaudeUsage.Stop();      // stops the poll loop before the process winds down
            StartMenu.Unwatch();     // before the bars go, since it asks them where to put things
            bars.Dispose();          // releases the appbar reservations
            hook.Dispose();
            overlay.Dispose();
            settings?.Dispose();

            WindowList.Raised -= raised;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            displaySettle.Dispose();
            marshaller.Dispose();
            taskbarWatch.Dispose();
            reflow.Dispose();

            SystemRestore.Everything(config);

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
