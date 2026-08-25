using System.Drawing;
using System.Windows.Forms;

namespace ScweenSpit;

/// <summary>
/// The app's real UI. Closing it hides it back to the tray rather than exiting — the tool is
/// meant to live in the background, so the window is a place you visit, not the app itself.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly SplitConfig config;
    private readonly ZoneManager zones;
    private readonly ZoneOverlay overlay;
    private readonly Action applyChanges;
    private readonly Action reloadFromDisk;
    private readonly Func<bool> hooksUp;
    private readonly Func<int> arrangeWindows;
    private readonly Action quit;

    private readonly Panel content = new() { Dock = DockStyle.Fill, BackColor = Theme.Window, Padding = new Padding(28, 24, 28, 24), AutoScroll = true };
    private readonly FlowLayoutPanel nav = new() { Dock = DockStyle.Left, Width = 172, BackColor = Theme.Panel, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12, 20, 12, 12), WrapContents = false };

    // Results belong in the window, not in a dialog. An unowned message box lands behind whatever
    // has focus, which reads as the app having frozen rather than as a message waiting.
    private readonly Label status = new()
    {
        Dock = DockStyle.Bottom, Height = 34, BackColor = Theme.Panel, ForeColor = Theme.Muted,
        TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 16, 0), Text = "",
    };
    private readonly System.Windows.Forms.Timer statusFade = new() { Interval = 9000 };
    private readonly List<Button> navButtons = [];
    private readonly Dictionary<string, Button> navByTitle = new(StringComparer.OrdinalIgnoreCase);

    private ListBox? excludeList;

    public SettingsForm(SplitConfig config, ZoneManager zones, ZoneOverlay overlay,
                        Action applyChanges, Action reloadFromDisk, Func<bool> hooksUp,
                        Func<int> arrangeWindows, Action quit)
    {
        this.reloadFromDisk = reloadFromDisk;
        this.arrangeWindows = arrangeWindows;
        this.quit = quit;
        this.config = config;
        this.zones = zones;
        this.overlay = overlay;
        this.applyChanges = applyChanges;
        this.hooksUp = hooksUp;

        Text = "ScweenSpit";
        Icon = Theme.AppIcon();
        ShowInTaskbar = true;
        // Laid out in raw pixels while Theme sizes fonts in points: without this the text grows at
        // high DPI but the window does not, and button captions clip.
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 600);
        Size = new Size(880, 640);
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Font = Theme.Face();

        status.Font = Theme.Face(9.5f);
        statusFade.Tick += (_, _) => { statusFade.Stop(); status.Text = ""; };

        // Added last so it is laid out first and spans the full width, beneath the nav as well.
        Controls.Add(content);
        Controls.Add(nav);
        Controls.Add(status);

        AddNav("General", ShowGeneral);
        AddNav("Layouts", ShowLayouts);
        AddNav("Taskbar", ShowTaskbar);
        AddNav("Claude usage", ShowClaude);
        AddNav("Exclusions", ShowExclusions);
        AddNav("Displays", ShowDisplays);
        AddNav("Updates", ShowUpdates);
        AddNav("Diagnostics", ShowDiagnostics);

        ShowGeneral();
        Select(navButtons[0]);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    /// <summary>Closing means "get out of my way", not "quit". Exit lives in the tray menu.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) statusFade.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    public void Reveal()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;

        // After the click that asked for this has finished. A tray menu or a bar button is still
        // dismissing at this point and the foreground window moves underneath us, so raising now
        // races whatever Windows does next.
        BeginInvoke(() =>
        {
            BringToFront();
            Activate();

            // Activate() alone is not enough: the click came from the tray or from a bar that never
            // takes focus, so this process may not hold the foreground right.
            WindowList.Raise(Handle);

            // And nothing ordinary rises past an always-on-top window. A window clamped into a zone
            // that covers the taskbar is exactly that, so the settings window would open behind it
            // however legitimate the foreground call was. Topmost briefly, then back to the normal
            // band so it does not sit over everything afterwards.
            TopMost = true;
            TopMost = false;
        });
    }

    // ---- nav ---------------------------------------------------------------

    private void AddNav(string title, Action show)
    {
        var b = new Button
        {
            Text = "   " + title, Width = 148, Height = 36, FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, BackColor = Theme.Panel,
            Font = Theme.Face(10f), Cursor = Cursors.Hand, Margin = new Padding(0, 0, 0, 4),
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Theme.Raised;
        b.Click += (_, _) => { show(); Select(b); };

        navButtons.Add(b);
        navByTitle[title] = b;
        nav.Controls.Add(b);
    }

    /// <summary>Opens one of the pages as though its nav entry had been clicked.</summary>
    public void GoTo(string title)
    {
        if (navByTitle.TryGetValue(title, out var button)) button.PerformClick();
    }

    private void Select(Button active)
    {
        foreach (var b in navButtons)
        {
            bool on = b == active;
            b.BackColor = on ? Theme.Raised : Theme.Panel;
            b.ForeColor = on ? Theme.Text : Theme.Muted;
            b.Font = Theme.Face(10f, on ? FontStyle.Bold : FontStyle.Regular);
        }
    }

    private FlowLayoutPanel Page(string heading, string caption)
    {
        // Dispose rather than Clear: Clear only re-parents, and a NumericUpDown subscribes to
        // SystemEvents.UserPreferenceChanged for as long as it has a handle - so every rebuild of
        // the Layouts page would strand its spin boxes and their HWNDs for the life of the process.
        while (content.Controls.Count > 0) content.Controls[0].Dispose();
        excludeList = null;
        var page = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true,
        };
        page.Controls.Add(Theme.Heading(heading));
        page.Controls.Add(Theme.Caption(caption));
        content.Controls.Add(page);
        return page;
    }

    private static void Row(FlowLayoutPanel page, string label, Control control)
    {
        page.Controls.Add(new Label
        {
            Text = label, AutoSize = true, ForeColor = Theme.Text, Font = Theme.Face(),
            Margin = new Padding(0, 8, 0, 2),
        });
        page.Controls.Add(control);
    }

    /// <summary>Reports the outcome of an action in the window itself.</summary>
    private void Say(string message, bool problem = false)
    {
        status.ForeColor = problem ? Color.FromArgb(235, 140, 140) : Theme.Accent;
        status.Text = message;

        statusFade.Stop();
        statusFade.Start();
    }

    private void Save()
    {
        config.Save();
        applyChanges();
    }

    // ---- pages -------------------------------------------------------------

    private void ShowGeneral()
    {
        var page = Page("General", "How windows get placed, and what the tool is allowed to take over.");

        var clamp = Theme.Toggle("Clamp maximized and fullscreen windows into zones", config.AutoClamp);
        clamp.CheckedChanged += (_, _) => { config.AutoClamp = clamp.Checked; Save(); };
        page.Controls.Add(clamp);

        var drag = Theme.Toggle("Snap windows into a zone when dropped there", config.DragToZone);
        drag.CheckedChanged += (_, _) => { config.DragToZone = drag.Checked; Save(); };
        page.Controls.Add(drag);

        var dragMod = Theme.Choice(SplitConfig.ModifierNames, config.DragModifier);
        dragMod.SelectedIndexChanged += (_, _) => { config.DragModifier = (string)dragMod.SelectedItem!; Save(); };
        Row(page, "Hold to snap while dragging", dragMod);

        var spanMod = Theme.Choice(SplitConfig.ModifierNames, config.SpanModifier);
        spanMod.SelectedIndexChanged += (_, _) => { config.SpanModifier = (string)spanMod.SelectedItem!; Save(); };
        Row(page, "Hold to span several zones", spanMod);

        var oneDisplay = Theme.Toggle("Keep windows on one display", config.KeepOnOneDisplay);
        oneDisplay.CheckedChanged += (_, _) => { config.KeepOnOneDisplay = oneDisplay.Checked; Save(); };
        page.Controls.Add(oneDisplay);
        page.Controls.Add(Theme.Caption(
            "Apps that reopen straddling several screens get pulled back onto the one they mostly " +
            "occupy, keeping their size. Drag a window across a boundary yourself and it stays put — " +
            "that is a decision, not an accident. Win+Alt+S toggles the exemption for the window in " +
            "front, and anything on the Exclusions page is left alone entirely."));

        var snap = Theme.Toggle("Suppress Windows' own snapping while running", config.SuppressWindowsSnap);
        snap.CheckedChanged += (_, _) => { config.SuppressWindowsSnap = snap.Checked; Save(); };
        page.Controls.Add(snap);
        page.Controls.Add(Theme.Caption(
            "Turns off Aero Snap, snap sizing and dock-moving so they stop competing for the same window. " +
            "These are system-wide settings; the originals are restored when ScweenSpit exits."));

        var startup = Theme.Toggle("Start with Windows", Startup.Enabled);
        startup.CheckedChanged += (_, _) => Startup.Set(startup.Checked);
        page.Controls.Add(startup);

        var padding = Theme.Number(config.Padding, 0, 200, 2);
        padding.ValueChanged += (_, _) => { config.Padding = (int)padding.Value; Save(); };
        Row(page, "Gap around each zone (pixels)", padding);

        var debounce = Theme.Number(config.DebounceMs, 50, 5000, 50);
        debounce.ValueChanged += (_, _) => { config.DebounceMs = (int)debounce.Value; Save(); };
        Row(page, "Settle time after moving a window (ms)", debounce);
        page.Controls.Add(Theme.Caption(
            "Raise this if a window flickers between sizes — some apps re-assert their own bounds after being moved."));
    }

    private void ShowLayouts()
    {
        var page = Page("Layouts", "Each display keeps its own layout. Pick a preset, or drag the dividers on screen.");

        foreach (var geo in ZoneManager.AllMonitors())
        {
            var rects = zones.ZonesFor(geo);
            page.Controls.Add(new Label
            {
                Text = $"{geo.Device.TrimStart('\\', '.')}   ·   {geo.Bounds.Width}×{geo.Bounds.Height}   ·   " +
                       $"{geo.Describe()}   ·   {rects.Count} zones",
                AutoSize = true, ForeColor = Theme.Text, Font = Theme.Face(11f, FontStyle.Bold),
                Margin = new Padding(0, 14, 0, 6),
            });

            var presets = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Width = 560, Margin = new Padding(0, 0, 0, 4) };
            var current = config.ZonesFor(geo.Device);

            foreach (var (name, make) in SplitConfig.Presets)
            {
                bool active = SameZones(current, make());
                var b = Theme.Action(name, active);
                b.AutoSize = true;
                b.MinimumSize = new Size(130, 32);
                b.Click += (_, _) =>
                {
                    config.SetZones(geo.Device, make());
                    applyChanges();
                    overlay.Flash(zones);
                    BeginInvoke(ShowLayouts);
                };
                presets.Controls.Add(b);
            }
            page.Controls.Add(presets);


            var frac = config.ZonesFor(geo.Device);
            for (int i = 0; i < frac.Count; i++)
            {
                int index = i;
                var zoneDevice = geo.Device;
                var cover = Theme.Toggle($"Zone {i + 1}: fill the whole display height, over the taskbar",
                                         frac[i].CoverTaskbar);
                cover.Margin = new Padding(0, 2, 0, 0);
                cover.CheckedChanged += (_, _) =>
                {
                    var edited = ZoneEdges.Clone(config.ZonesFor(zoneDevice));
                    if (index >= edited.Count) return;
                    edited[index].CoverTaskbar = cover.Checked;
                    config.SetZones(zoneDevice, edited);
                    applyChanges();
                    overlay.Flash(zones);
                };
                page.Controls.Add(cover);
            }
            page.Controls.Add(Theme.Caption(
                "A zone set this way is measured against the whole display rather than the work area, " +
                "and windows placed in it are kept above the taskbar — so it is genuinely fullscreen " +
                "over its part of the screen while the taskbar stays visible and usable everywhere else."));

            // Nothing to cover means nothing to see, and that is worth saying out loud rather than
            // leaving someone to conclude the setting is broken.
            if (geo.Work.Left == geo.Bounds.Left && geo.Work.Top == geo.Bounds.Top
                && geo.Work.Right == geo.Bounds.Right && geo.Work.Bottom == geo.Bounds.Bottom)
            {
                page.Controls.Add(new Label
                {
                    Text = "⚠  Nothing is reserving space on this display right now — the taskbar is " +
                           "hidden or set to auto-hide — so this setting has no visible effect until " +
                           "something is.",
                    AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Color.FromArgb(235, 185, 110),
                    Font = Theme.Face(9f), Margin = new Padding(0, 0, 0, 10),
                });
            }

            page.Controls.Add(new Label
            {
                Text = "Reserved space (pixels kept clear at each edge)",
                AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Face(9f),
                Margin = new Padding(0, 12, 0, 4),
            });

            var margins = config.LayoutFor(geo.Device).Margins;
            var strip = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
            var device = geo.Device;

            foreach (var (name, get, set) in new (string, Func<Margins, int>, Action<Margins, int>)[]
            {
                ("Top",    m => m.Top,    (m, v) => m.Top = v),
                ("Bottom", m => m.Bottom, (m, v) => m.Bottom = v),
                ("Left",   m => m.Left,   (m, v) => m.Left = v),
                ("Right",  m => m.Right,  (m, v) => m.Right = v),
            })
            {
                strip.Controls.Add(new Label
                {
                    Text = name, AutoSize = true, ForeColor = Theme.Text, Font = Theme.Face(),
                    Margin = new Padding(0, 8, 4, 0),
                });

                var box = Theme.Number(get(margins), 0, 2000, 4);
                box.Width = 70;
                box.Margin = new Padding(0, 2, 16, 4);
                var setter = set;
                box.ValueChanged += (_, _) =>
                {
                    var edited = config.LayoutFor(device).Margins.Copy();
                    setter(edited, (int)box.Value);
                    config.SetMargins(device, edited);
                    applyChanges();
                };
                strip.Controls.Add(box);
            }
            page.Controls.Add(strip);
        }

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Width = 560, Margin = new Padding(0, 18, 0, 0) };

        var edit = Theme.Action("Drag dividers on screen…", primary: true);
        edit.AutoSize = true;
        edit.Click += (_, _) => OpenZoneEditor();
        buttons.Controls.Add(edit);

        var show = Theme.Action("Show all zones (Win+Alt+Z)");
        show.AutoSize = true;
        show.Click += (_, _) => overlay.Flash(zones, 2500);
        buttons.Controls.Add(show);

        var which = Theme.Action("Identify displays");
        which.AutoSize = true;
        which.Click += (_, _) => DisplayIdentifier.Flash();
        buttons.Controls.Add(which);

        var arrange = Theme.Action("Arrange open windows now");
        arrange.AutoSize = true;
        arrange.Click += (_, _) =>
        {
            int moved = arrangeWindows();
            Say($"Placed {moved} window{(moved == 1 ? "" : "s")}.");
        };
        buttons.Controls.Add(arrange);

        page.Controls.Add(buttons);
    }

    private void ShowTaskbar()
    {
        var page = Page("Taskbar",
            "Zones are laid out inside the Windows work area, so the taskbar is already avoided. " +
            "This is for moving the taskbar itself, and for telling ScweenSpit to keep clear of " +
            "anything Windows does not report — an auto-hiding or third-party bar.");

        var current = Taskbar.Current();
        page.Controls.Add(new Label
        {
            Text = current is null ? "Current position: unknown" : $"Current position: {current}",
            AutoSize = true, ForeColor = Theme.Text, Font = Theme.Face(11f, FontStyle.Bold),
            Margin = new Padding(0, 4, 0, 10),
        });

        var autoHide = Theme.Toggle("Hide the taskbar until I reach for it",
                                    config.TaskbarAutoHide ?? Taskbar.AutoHide);
        autoHide.CheckedChanged += (_, _) => { config.TaskbarAutoHide = autoHide.Checked; Save(); };
        page.Controls.Add(autoHide);
        page.Controls.Add(Theme.Caption(
            "Takes effect immediately, needs no restart, and — unlike moving the taskbar — still " +
            "works on Windows 11. Paired with a zone set to cover the taskbar, it gives you the " +
            "whole display and the bar on demand."));

        var hide = Theme.Toggle("Hide the Windows taskbar entirely", config.HideWindowsTaskbar);
        hide.CheckedChanged += (_, _) =>
        {
            config.HideWindowsTaskbar = hide.Checked;
            Save();
            BeginInvoke(ShowTaskbar);
        };
        page.Controls.Add(hide);
        page.Controls.Add(Theme.Caption(
            "Takes the shell's taskbars off screen and gives their reserved strip back, so a bar of " +
            "yours docked to the same edge sits flush against it rather than above a gap. Restored " +
            "when ScweenSpit exits, and on the next launch if this one is killed.\n\n" +
            "Note that third-party notification icons — Discord, Steam, and anything else living in " +
            "the system tray — go with it. The indicators below are ours; the tray itself belongs to " +
            "Explorer and cannot be borrowed."));

        var animation = Theme.Toggle("Stop windows flying into the taskbar when minimised",
                                     config.StopMinimiseAnimation);
        animation.CheckedChanged += (_, _) =>
        {
            config.StopMinimiseAnimation = animation.Checked;
            Save();
        };
        page.Controls.Add(animation);
        page.Controls.Add(Theme.Caption(
            "With the taskbar hidden, Windows still animates a minimised window towards where its " +
            "button would have been — a corner with nothing in it. This turns that animation off. " +
            "It is a system setting and is restored when ScweenSpit exits."));

        if (!Taskbar.CanMove)
        {
            page.Controls.Add(new Label
            {
                Text = "⚠  Windows 11 removed taskbar repositioning in build 22000. The buttons below " +
                       "still write the setting, and Explorer still ignores it — the bar stays at the " +
                       "bottom. Nothing breaks; it simply will not move. Auto-hide above, or a zone set " +
                       "to cover the taskbar, are the ways to reclaim that space.",
                AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Color.FromArgb(235, 185, 110),
                Font = Theme.Face(9f), Margin = new Padding(0, 8, 0, 12),
            });
        }

        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Width = 560 };
        foreach (var edge in new[] { TaskbarEdge.Bottom, TaskbarEdge.Top, TaskbarEdge.Left, TaskbarEdge.Right })
        {
            var b = Theme.Action($"Move to {edge}", primary: current == edge);
            b.Width = 130;
            var target = edge;
            b.Click += (_, _) => MoveTaskbar(target);
            row.Controls.Add(b);
        }
        page.Controls.Add(row);

        page.Controls.Add(Theme.Caption(
            "Moving the taskbar restarts Windows Explorer, which briefly blanks the desktop and closes " +
            "any open File Explorer windows. Only the primary display's taskbar is moved; secondary " +
            "displays keep their own. Per-display reserved space lives on the Layouts page."));

        page.Controls.Add(new Label
        {
            Text = "A bar of our own", AutoSize = true, ForeColor = Theme.Text,
            Font = Theme.Face(13f, FontStyle.Bold), Margin = new Padding(0, 22, 0, 2),
        });
        page.Controls.Add(Theme.Caption(
            "Windows will not put its taskbar anywhere but the bottom — but nothing stops us docking " +
            "one of ours to any edge. It registers as a Win32 appbar, so Windows reserves the space " +
            "and every application keeps clear of it, exactly as it does for the real taskbar. Each " +
            "display is a separate switch, so a bar on one screen and nothing on the others is fine."));

        var identify = Theme.Action("Identify displays", primary: true);
        identify.AutoSize = true;
        identify.Click += (_, _) => DisplayIdentifier.Flash();
        page.Controls.Add(identify);

        foreach (var geo in ZoneManager.AllMonitors())
        {
            var device = geo.Device;
            config.Bars.TryGetValue(device, out var settings);

            page.Controls.Add(new Label
            {
                Text = $"{device.TrimStart('\\', '.')}   ·   {geo.Bounds.Width}×{geo.Bounds.Height}   ·   {geo.Describe()}",
                AutoSize = true, ForeColor = Theme.Text, Font = Theme.Face(11f, FontStyle.Bold),
                Margin = new Padding(0, 14, 0, 6),
            });

            var enabled = Theme.Toggle("Show a ScweenSpit bar on this display", settings is not null);
            enabled.CheckedChanged += (_, _) =>
            {
                if (enabled.Checked) config.Bars[device] = settings ?? new BarSettings();
                else config.Bars.Remove(device);
                Save();
                BeginInvoke(ShowTaskbar);
            };
            page.Controls.Add(enabled);

            if (settings is null) continue;

            var edge = Theme.Choice(SplitConfig.EdgeNames, settings.Edge);
            edge.SelectedIndexChanged += (_, _) => { settings.Edge = (string)edge.SelectedItem!; Save(); };
            Row(page, "Docked to", edge);

            var thickness = Theme.Number(settings.Thickness, 28, 600, 10);
            thickness.ValueChanged += (_, _) => { settings.Thickness = (int)thickness.Value; Save(); };
            Row(page, "Thickness (pixels)", thickness);

            // On a wide display split into zones, a bar across the whole edge is rarely the point.
            var zoneCount = config.ZonesFor(device).Count;
            var spans = new[] { "Whole display" }
                .Concat(Enumerable.Range(1, zoneCount).Select(i => $"Zone {i}")).ToArray();

            var span = Theme.Choice(spans, settings.Zone is int z && z < zoneCount ? $"Zone {z + 1}" : spans[0]);
            span.SelectedIndexChanged += (_, _) =>
            {
                settings.Zone = span.SelectedIndex == 0 ? null : span.SelectedIndex - 1;
                Save();
                overlay.Flash(zones);
            };
            Row(page, "Across", span);

            if (settings.Zone is not null)
                page.Controls.Add(Theme.Caption(
                    "A bar confined to one zone is placed by ScweenSpit rather than reserved from " +
                    "Windows — the work area is one rectangle per display, so part of an edge cannot " +
                    "be reserved. Windows ScweenSpit places keep clear of it; anything maximised by " +
                    "Windows itself will not."));

            var onlyHere = Theme.Toggle("List only windows on this display", settings.ThisDisplayOnly);
            onlyHere.CheckedChanged += (_, _) => { settings.ThisDisplayOnly = onlyHere.Checked; Save(); };
            page.Controls.Add(onlyHere);

            var floating = Theme.Toggle("Float clear of the edges, rounded on every corner", settings.Floating);
            floating.CheckedChanged += (_, _) => { settings.Floating = floating.Checked; Save(); };
            page.Controls.Add(floating);

            if (settings.Floating)
            {
                var gap = Theme.Number(settings.FloatMargin, 0, 60, 2);
                gap.ValueChanged += (_, _) => { settings.FloatMargin = (int)gap.Value; Save(); };
                Row(page, "Gap from the edges (pixels)", gap);
            }

            var barSettings = settings;
            page.Controls.Add(new Label
            {
                Text = barSettings.Pinned.Count == 0
                    ? "No pinned applications — right-click any button on the bar to pin it."
                    : $"{barSettings.Pinned.Count} pinned application{(barSettings.Pinned.Count == 1 ? "" : "s")}",
                AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Face(9f),
                Margin = new Padding(0, 12, 0, 4),
            });

            var pinButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Width = 500 };

            var import = Theme.Action("Import from the Windows taskbar");
            import.AutoSize = true;
            import.Click += (_, _) => ImportWindowsPins(barSettings);
            pinButtons.Controls.Add(import);

            if (barSettings.Pinned.Count > 0)
            {
                var clear = Theme.Action("Unpin all");
                clear.AutoSize = true;
                clear.Click += (_, _) => { barSettings.Pinned.Clear(); Save(); BeginInvoke(ShowTaskbar); };
                pinButtons.Controls.Add(clear);
            }
            page.Controls.Add(pinButtons);

            var iconsOnly = Theme.Toggle("Icons only, no window titles", settings.IconsOnly);
            iconsOnly.CheckedChanged += (_, _) => { settings.IconsOnly = iconsOnly.Checked; Save(); };
            page.Controls.Add(iconsOnly);

            var status = Theme.Toggle("Show volume, network, battery and the clock", settings.ShowStatus);
            status.CheckedChanged += (_, _) => { settings.ShowStatus = status.Checked; Save(); };
            page.Controls.Add(status);

            var usage = Theme.Toggle("Show Claude usage bars", settings.ShowUsage);
            usage.CheckedChanged += (_, _) => { settings.ShowUsage = usage.Checked; Save(); BeginInvoke(ShowTaskbar); };
            page.Controls.Add(usage);

            // A toggle that appears to do nothing is worse than one that is not there. Say what is
            // still missing, and offer the page that fixes it.
            if (settings.ShowUsage && !ClaudeUsage.Enabled)
            {
                page.Controls.Add(new Label
                {
                    Text = "⚠  The strip is on the bar, but has nothing to show until usage tracking "
                         + "is switched on and a session key is saved.",
                    AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Color.FromArgb(235, 185, 110),
                    Font = Theme.Face(9f), Margin = new Padding(0, 2, 0, 4),
                });

                var setUp = Theme.Action("Set up Claude usage", primary: true);
                setUp.AutoSize = true;
                setUp.Click += (_, _) => BeginInvoke(() => GoTo("Claude usage"));
                page.Controls.Add(setUp);
            }

            if (!config.Claude.Enabled || string.IsNullOrWhiteSpace(config.Claude.SessionKey))
                page.Controls.Add(Theme.Caption(
                    "Nothing is drawn until an account is set up under Claude usage."));
        }
    }

    // ---- claude usage ------------------------------------------------------

    /// <summary>
    /// Copies the Windows taskbar's pinned applications onto this bar, keeping anything already
    /// pinned. Store apps are counted rather than imported: they are pinned as an application id
    /// rather than a file, so there is no executable to launch or take an icon from.
    /// </summary>
    private void ImportWindowsPins(BarSettings bar)
    {
        var pins = WindowsPins.Read(out int skipped);

        int added = 0;
        foreach (var pin in pins)
        {
            if (bar.Pinned.Any(p => string.Equals(p, pin.Id, StringComparison.OrdinalIgnoreCase))) continue;

            bar.Pinned.Add(pin.Id);
            added++;
        }

        if (added > 0) Save();

        var summary = pins.Count == 0
            ? "Found nothing pinned to the Windows taskbar."
            : $"Added {added} of {pins.Count} pinned application{(pins.Count == 1 ? "" : "s")}"
              + (added < pins.Count ? " — the rest were already there" : "")
              + (skipped > 0 ? $".\n\n{skipped} Store app{(skipped == 1 ? " was" : "s were")} skipped: "
                             + "they are pinned as an application id rather than a file, so there is "
                             + "nothing to launch or take an icon from." : ".");

        Say(summary.Replace("\n\n", "  "));
        BeginInvoke(ShowTaskbar);
    }

    private void ShowClaude()
    {
        var claude = config.Claude;
        var page = Page("Claude usage",
            "Session and weekly limits from claude.ai, drawn into the status cluster of any bar " +
            "that asks for them.");

        page.Controls.Add(Theme.Caption(
            "Adapted from claude-usage-widget by Niccolò Sabato (MIT). ScweenSpit is not affiliated " +
            "with Anthropic; the figures are the ones claude.ai reports for your own account."));

        var on = Theme.Toggle("Track claude.ai usage", claude.Enabled);
        // Rebuilt after the event unwinds: Page() disposes this checkbox, and doing that
        // inside its own handler leaves WinForms raising events on a dead control.
        on.CheckedChanged += (_, _) => { claude.Enabled = on.Checked; Save(); BeginInvoke(ShowClaude); };
        page.Controls.Add(on);

        if (!claude.Enabled)
        {
            page.Controls.Add(Theme.Caption(
                "While this is off, no session key is read and no request is ever made to claude.ai."));
            return;
        }

        // ---- the key
        bool haveKey = !string.IsNullOrWhiteSpace(claude.SessionKey);
        page.Controls.Add(Theme.Caption(
            "Sign in to claude.ai in your browser, then press F12 and open Application ▸ Cookies ▸ " +
            "https://claude.ai. Copy the value of the sessionKey row and paste it below. It starts " +
            "with sk-ant- and it expires every few weeks, so this needs redoing occasionally."));

        page.Controls.Add(Theme.Caption(
            "That cookie is your whole claude.ai account, not just its usage figures. ScweenSpit " +
            "encrypts it to this Windows account before it touches the config file, sends it only " +
            "to claude.ai, and never writes it to the log."));

        var key = new TextBox
        {
            Width = 430, UseSystemPasswordChar = true, BackColor = Theme.Raised, ForeColor = Theme.Text,
            Font = Theme.Face(), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 2, 0, 6),
            PlaceholderText = haveKey ? "A key is stored — paste a new one to replace it" : "sk-ant-…",
        };
        Row(page, haveKey ? "Session key (stored)" : "Session key", key);

        var keyButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 4) };

        var save = Theme.Action("Save key", primary: true);
        save.Click += (_, _) =>
        {
            if (ClaudeUsage.SetKey(key.Text))
            {
                key.Clear();
                BeginInvoke(ShowClaude);
            }
            else
            {
                Say("That does not look like a session key — it starts with sk-ant- and is a long "
                  + "string. Copy the whole value of the sessionKey cookie.", problem: true);
            }
        };
        keyButtons.Controls.Add(save);

        if (haveKey)
        {
            var forget = Theme.Action("Forget key");
            forget.Click += (_, _) =>
            {
                ClaudeUsage.SetKey(null);
                BeginInvoke(ShowClaude);
            };
            keyButtons.Controls.Add(forget);
        }

        var check = Theme.Action("Check now");
        check.Click += (_, _) => ClaudeUsage.Refresh();
        keyButtons.Controls.Add(check);

        page.Controls.Add(keyButtons);

        // ---- what to show
        var weekly = Theme.Toggle("Show the seven-day limit", claude.ShowWeekly);
        weekly.CheckedChanged += (_, _) => { claude.ShowWeekly = weekly.Checked; Save(); ClaudeUsage.Refresh(); };
        page.Controls.Add(weekly);

        var model = Theme.Toggle("Show the weekly per-model limit", claude.ShowModel);
        model.CheckedChanged += (_, _) => { claude.ShowModel = model.Checked; Save(); ClaudeUsage.Refresh(); };
        page.Controls.Add(model);

        var interval = Theme.Number(claude.RefreshSeconds, 30, 3600, 30);
        interval.ValueChanged += (_, _) => { claude.RefreshSeconds = (int)interval.Value; Save(); };
        Row(page, "Seconds between checks", interval);

        // ---- one place to look when it is not working
        var diagnosis = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Visible = false,
            Width = 460, Height = 120, BackColor = Theme.Raised, ForeColor = Theme.Muted,
            Font = new Font(FontFamily.GenericMonospace, 8.5f), BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 8, 0, 8),
        };

        var test = Theme.Action("Test connection");
        test.AutoSize = true;
        test.Click += async (_, _) =>
        {
            test.Enabled = false;
            diagnosis.Visible = true;
            diagnosis.Text = "Checking…";
            try
            {
                // Off the UI thread: this makes real requests to claude.ai.
                diagnosis.Text = await Task.Run(ClaudeUsage.SelfTest);
            }
            catch (Exception ex) { diagnosis.Text = ex.Message; }
            finally { test.Enabled = true; }
        };
        page.Controls.Add(test);
        page.Controls.Add(Theme.Caption(
            "Walks the same path a refresh takes and says where it stops. The key itself is never "
          + "included, so the result is safe to paste into a bug report."));
        page.Controls.Add(diagnosis);

        // ---- what it is currently reading
        var state = new Label
        {
            AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Face(9f),
            MaximumSize = new Size(430, 0), Margin = new Padding(0, 12, 0, 8),
        };
        page.Controls.Add(state);

        void Report() => state.Text = UsageStrip.Tip(ClaudeUsage.Current);
        Report();

        // The poll runs on its own thread, so the page watches rather than being told.
        var watch = new System.Windows.Forms.Timer { Interval = 1500 };
        watch.Tick += (_, _) => Report();
        watch.Start();
        page.Disposed += (_, _) => { watch.Stop(); watch.Dispose(); };

        var open = Theme.Action("Open usage on claude.ai");
        open.Width = 220;
        open.Click += (_, _) => SystemStatus.Open(ClaudeUsage.UsagePage);
        page.Controls.Add(open);

        page.Controls.Add(Theme.Caption(
            "Turn the bars on per display under Taskbar. Clicking the strip opens the same page."));
    }

    private void MoveTaskbar(TaskbarEdge edge)
    {
        var before = Taskbar.Current();
        // Owned by this window: an unowned dialog can be raised behind it and look like a hang.
        var answer = MessageBox.Show(this,
            $"Move the taskbar to the {edge.ToString().ToLowerInvariant()} edge?\n\n" +
            "This restarts Windows Explorer: the desktop will blank for a moment and open File " +
            "Explorer windows will close." +
            (Taskbar.CanMove ? "" : "\n\nOn Windows 11 this is very likely to have no effect."),
            "ScweenSpit", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);

        if (answer != DialogResult.OK) return;

        if (!Taskbar.Move(edge))
        {
            Say("Could not write the taskbar setting — see the log.", problem: true);
        }
        else if (Taskbar.Current() is { } now && now != edge)
        {
            // Say so rather than leaving a button that looks like it worked.
            Say($"The setting was written, but the taskbar is still docked {now} — Windows ignored "
              + "it, which is expected on Windows 11.", problem: true);
        }

        _ = before;
        BeginInvoke(ShowTaskbar);
    }

    /// <summary>
    /// Hands the screen over to the zone editor and takes it back afterwards. Without the return
    /// path the settings window is hidden with no taskbar button, so finishing the edit leaves the
    /// user staring at a bare desktop.
    /// </summary>
    private void OpenZoneEditor()
    {
        Hide();
        overlay.Show(zones, OverlayMode.Edit);

        Action? back = null;
        back = () =>
        {
            overlay.Closed -= back!;
            Reveal();
            BeginInvoke(ShowLayouts);   // reflect whatever was just dragged
        };
        overlay.Closed += back;
    }

    private static bool SameZones(List<FracRect> a, List<FracRect> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            Math.Abs(p.First.L - p.Second.L) < 0.005 && Math.Abs(p.First.T - p.Second.T) < 0.005 &&
            Math.Abs(p.First.R - p.Second.R) < 0.005 && Math.Abs(p.First.B - p.Second.B) < 0.005);

    private void ShowExclusions()
    {
        var page = Page("Exclusions",
            "Windows belonging to these processes or window classes are left completely alone — " +
            "games and video players are the usual candidates.");

        excludeList = new ListBox
        {
            Width = 420, Height = 220, BackColor = Theme.Raised, ForeColor = Theme.Text,
            Font = Theme.Face(), BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, 10),
        };
        RefreshExclusions();
        page.Controls.Add(excludeList);

        var entry = new TextBox
        {
            Width = 280, BackColor = Theme.Raised, ForeColor = Theme.Text, Font = Theme.Face(),
            BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "process name or window class",
            Margin = new Padding(0, 0, 0, 10),
        };
        page.Controls.Add(entry);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Width = 560 };

        var add = Theme.Action("Add", primary: true);
        add.Width = 110;
        add.Click += (_, _) =>
        {
            var name = entry.Text.Trim();
            if (name.Length == 0 || config.Exclude.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
            config.Exclude.Add(name);
            entry.Clear();
            Save();
            RefreshExclusions();
        };
        buttons.Controls.Add(add);

        var remove = Theme.Action("Remove selected");
        remove.Width = 160;
        remove.Click += (_, _) =>
        {
            if (excludeList?.SelectedItem is not string sel) return;
            config.Exclude.RemoveAll(e => e.Equals(sel, StringComparison.OrdinalIgnoreCase));
            Save();
            RefreshExclusions();
        };
        buttons.Controls.Add(remove);

        page.Controls.Add(buttons);
    }

    private void RefreshExclusions()
    {
        if (excludeList is null) return;
        excludeList.Items.Clear();
        foreach (var e in config.Exclude) excludeList.Items.Add(e);
        if (config.Exclude.Count == 0) excludeList.Items.Add("(nothing excluded)");
    }

    private void ShowDiagnostics()
    {
        var page = Page("Diagnostics", "Where things are, and whether the hooks are alive.");

        page.Controls.Add(new Label
        {
            Text = hooksUp() ? "● Hooks active" : "● Hooks not running",
            AutoSize = true, Font = Theme.Face(11f, FontStyle.Bold),
            ForeColor = hooksUp() ? Color.FromArgb(110, 210, 140) : Color.FromArgb(230, 120, 120),
            Margin = new Padding(0, 4, 0, 12),
        });

        foreach (var (label, path) in new[] { ("Config", SplitConfig.Path), ("Log", Log.LogPath) })
        {
            page.Controls.Add(new Label
            {
                Text = $"{label}: {path}", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Face(9f),
                Margin = new Padding(0, 0, 0, 4),
            });

            var open = Theme.Action($"Open {label.ToLowerInvariant()}");
            open.Width = 150;
            open.Click += (_, _) => OpenPath(path);
            page.Controls.Add(open);
        }

        var reload = Theme.Action("Reload config from disk", primary: true);
        reload.Width = 220;
        reload.Margin = new Padding(0, 16, 0, 0);
        reload.Click += (_, _) => { reloadFromDisk(); BeginInvoke(ShowDiagnostics); };
        page.Controls.Add(reload);
    }

    private void ShowDisplays()
    {
        var page = Page("Displays",
            "Settings can follow your hardware. Dock and undock, and the arrangement you are in " +
            "brings its own back with it.");

        var signature = DisplayTopology.Signature();
        page.Controls.Add(new Label
        {
            Text = DisplayTopology.Describe(),
            AutoSize = true, ForeColor = Theme.Text, Font = Theme.Face(12f, FontStyle.Bold),
            Margin = new Padding(0, 4, 0, 2),
        });
        page.Controls.Add(Theme.Caption($"Recognised as: {signature}"));

        var follow = Theme.Toggle("Apply the saved settings when the displays change", config.FollowDisplayChanges);
        follow.CheckedChanged += (_, _) => { config.FollowDisplayChanges = follow.Checked; Save(); };
        page.Controls.Add(follow);

        bool saved = config.Profiles.ContainsKey(signature);

        var name = new TextBox
        {
            Text = saved ? config.Profiles[signature].Name ?? "" : "",
            PlaceholderText = "A name for this arrangement, e.g. \"At the desk\"",
            Width = 340, BackColor = Theme.Raised, ForeColor = Theme.Text, Font = Theme.Face(),
            BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 12, 0, 8),
        };
        page.Controls.Add(name);

        var handsOff = Theme.Action("Remember it with ScweenSpit standing down");
        handsOff.AutoSize = true;
        handsOff.Click += (_, _) =>
        {
            var profile = DisplayProfile.HandsOff(string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim());
            config.Profiles[signature] = profile;
            profile.ApplyTo(config);
            Save();
            Say("Saved. On this arrangement ScweenSpit leaves the machine to Windows.");
            BeginInvoke(ShowDisplays);
        };

        var save = Theme.Action(saved ? "Update this arrangement" : "Remember this arrangement", primary: true);
        save.AutoSize = true;
        save.Click += (_, _) =>
        {
            var profile = config.Profiles.TryGetValue(signature, out var existing) ? existing : new DisplayProfile();
            profile.CaptureFrom(config);
            profile.Name = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim();
            config.Profiles[signature] = profile;
            Save();
            BeginInvoke(ShowDisplays);
        };
        page.Controls.Add(save);
        page.Controls.Add(handsOff);
        page.Controls.Add(Theme.Caption(
            "Standing down turns off clamping, drag-to-zone, keeping windows on one display and our " +
            "own bars, and puts the Windows taskbar back on screen and out of auto-hide — everything " +
            "goes back to how Windows behaves on its own. Useful for the laptop screen.\n\n" +
            "Remembering captures the current state instead: clamping, drag-to-zone, keeping windows " +
            "on one display, the Windows taskbar and its auto-hide, our bars, snap suppression and " +
            "the minimise animation.\n\n" +
            "Zone layouts and bars are already remembered per display, so they follow on their own: " +
            "a bar configured for a monitor simply is not there when that monitor is not."));

        if (config.Profiles.Count == 0) return;

        page.Controls.Add(new Label
        {
            Text = "Saved arrangements", AutoSize = true, ForeColor = Theme.Text,
            Font = Theme.Face(13f, FontStyle.Bold), Margin = new Padding(0, 22, 0, 6),
        });

        foreach (var (key, profile) in config.Profiles.OrderBy(p => p.Key, StringComparer.Ordinal).ToList())
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            row.Controls.Add(new Label
            {
                Text = (profile.Name is { Length: > 0 } n ? $"{n}   ·   " : "") + key +
                       (key == signature ? "   ·   in use now" : ""),
                AutoSize = true, ForeColor = key == signature ? Theme.Accent : Theme.Muted,
                Font = Theme.Face(9.5f), Margin = new Padding(0, 8, 12, 0),
            });

            var forget = Theme.Action("Forget");
            forget.AutoSize = true;
            var doomed = key;
            forget.Click += (_, _) => { config.Profiles.Remove(doomed); Save(); BeginInvoke(ShowDisplays); };
            row.Controls.Add(forget);

            page.Controls.Add(row);
        }
    }

    private void ShowUpdates()
    {
        var page = Page("Updates", "ScweenSpit replaces the file you downloaded and restarts itself.");

        page.Controls.Add(new Label
        {
            Text = $"Running version {Updater.Current}",
            AutoSize = true, ForeColor = Theme.Text, Font = Theme.Face(12f, FontStyle.Bold),
            Margin = new Padding(0, 4, 0, 4),
        });

        if (Updater.LauncherPath() is not { } launcher)
        {
            page.Controls.Add(new Label
            {
                Text = "⚠  This is the unpacked copy, started directly rather than through the " +
                       "ScweenSpit.exe you downloaded. There is nothing here to replace, so updates " +
                       "cannot be installed from it — run the downloaded file instead.",
                AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Color.FromArgb(235, 185, 110),
                Font = Theme.Face(9f), Margin = new Padding(0, 0, 0, 12),
            });
        }
        else
        {
            page.Controls.Add(Theme.Caption($"Updating: {launcher}"));
        }

        var auto = Theme.Toggle("Look for updates on startup", config.CheckForUpdates);
        auto.CheckedChanged += (_, _) => { config.CheckForUpdates = auto.Checked; Save(); };
        page.Controls.Add(auto);
        page.Controls.Add(Theme.Caption("At most one check a day, and it never installs anything on its own."));

        var status = new Label
        {
            Text = config.LastUpdateCheck is { } when ? $"Last checked {when:d MMM HH:mm}" : "Not checked yet",
            AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Theme.Muted,
            Font = Theme.Face(9.5f), Margin = new Padding(0, 10, 0, 8),
        };
        page.Controls.Add(status);

        var check = Theme.Action("Check for updates", primary: true);
        check.AutoSize = true;
        check.Click += async (_, _) =>
        {
            check.Enabled = false;
            status.ForeColor = Theme.Muted;
            status.Text = "Checking…";
            try
            {
                var update = await Updater.CheckAsync(config);
                config.LastUpdateCheck = DateTime.Now;
                config.Save();

                if (update is null) { status.Text = $"Version {Updater.Current} is the latest."; return; }

                status.ForeColor = Theme.Accent;
                status.Text = $"Version {update.Version} is available.";
                Offer(page, update);
            }
            catch (Exception ex)
            {
                status.ForeColor = Color.FromArgb(235, 140, 140);
                status.Text = ex.Message;
                Log.Write($"update check failed: {ex}");
            }
            finally { check.Enabled = true; }
        };
        page.Controls.Add(check);

        page.Controls.Add(new Label
        {
            Text = "Released from", AutoSize = true, ForeColor = Theme.Text,
            Font = Theme.Face(), Margin = new Padding(0, 18, 0, 2),
        });
        var repo = new TextBox
        {
            Text = config.UpdateRepository, Width = 320, BackColor = Theme.Raised, ForeColor = Theme.Text,
            Font = Theme.Face(), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, 10),
        };
        repo.TextChanged += (_, _) => { config.UpdateRepository = repo.Text.Trim(); Save(); };
        page.Controls.Add(repo);

        page.Controls.Add(new Label
        {
            Text = "Access token (only needed while the repository is private)", AutoSize = true,
            ForeColor = Theme.Muted, Font = Theme.Face(9f), Margin = new Padding(0, 0, 0, 2),
        });
        var token = new TextBox
        {
            Text = config.UpdateToken, Width = 320, UseSystemPasswordChar = true,
            BackColor = Theme.Raised, ForeColor = Theme.Text, Font = Theme.Face(),
            BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, 10),
        };
        token.TextChanged += (_, _) => { config.UpdateToken = token.Text.Trim(); Save(); };
        page.Controls.Add(token);
        page.Controls.Add(Theme.Caption(
            "Stored as plain text in config.json. A public repository needs no token at all."));
    }

    /// <summary>Shows the release notes and an install button once an update has been found.</summary>
    private void Offer(FlowLayoutPanel page, UpdateInfo update)
    {
        if (!string.IsNullOrWhiteSpace(update.Notes))
        {
            page.Controls.Add(new TextBox
            {
                Text = update.Notes.Trim(), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Width = 460, Height = 130, BackColor = Theme.Raised, ForeColor = Theme.Muted,
                Font = Theme.Face(9f), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 10, 0, 8),
            });
        }

        var install = Theme.Action($"Install {update.Version} and restart", primary: true);
        install.AutoSize = true;
        install.Click += async (_, _) =>
        {
            install.Enabled = false;
            install.Text = "Downloading…";
            try
            {
                var replaced = await Updater.ApplyAsync(update);

                // Hand it our process id: it has to wait for us to let go of the unpacked copy
                // before it can replace it, or the update lands on disk and never runs.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(replaced)
                {
                    Arguments = $"--replacing {Environment.ProcessId}",
                    UseShellExecute = true,
                });
                quit();
            }
            catch (Exception ex)
            {
                install.Enabled = true;
                install.Text = "Install and restart";
                Say(ex.Message, problem: true);
                Log.Write($"update install failed: {ex}");
            }
        };
        page.Controls.Add(install);
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write($"open {path} failed: {ex.Message}"); }
    }
}
