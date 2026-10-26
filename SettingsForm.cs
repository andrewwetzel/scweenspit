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
    private readonly FlowLayoutPanel nav = new() { Dock = DockStyle.Left, Width = 180, BackColor = Theme.Panel, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12, 20, 12, 12), WrapContents = false };

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
        AddNav("Antigravity usage", ShowAntigravity);
        AddNav("Remote chats", ShowRemoteChats);
        AddNav("Orchestrator", ShowOrchestra);
        AddNav("Bambu printers", ShowBambu);
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
            Text = "   " + title, Width = 156, Height = 36, FlatStyle = FlatStyle.Flat,
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

        page.Controls.Add(new Label
        {
            Text = "Installing", AutoSize = true, ForeColor = Theme.Text,
            Font = Theme.Face(11f, FontStyle.Bold), Margin = new Padding(0, 20, 0, 2),
        });

        if (Installer.RunningInstalled)
        {
            page.Controls.Add(Theme.Caption(
                $"Installed, and running from there.\n{Installer.InstalledExe}\n\n" +
                "It is in the Start menu, and in Apps & features to remove."));
        }
        else
        {
            page.Controls.Add(Theme.Caption(
                Installer.IsInstalled
                    ? "Already installed, but this is not the installed copy. Installing again "
                      + "replaces it with this one and restarts from there."
                    : "Copies ScweenSpit into its own folder, puts it in the Start menu, and lists "
                      + "it in Apps & features. Running it from the Downloads folder works, but "
                      + "leaves nothing to start it by except finding that file again — and every "
                      + "path it records, including the one it starts itself from at login, then "
                      + "runs through Downloads."));

            var install = Theme.Action("Install and restart from there", primary: true);
            install.AutoSize = true;
            install.Click += (_, _) =>
            {
                install.Enabled = false;
                try
                {
                    var installed = Installer.Install();

                    // Through the same handover an update uses: it waits for this process to let go
                    // of the unpacked copy before starting its own.
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installed)
                    {
                        Arguments = $"--replacing {Environment.ProcessId}",
                        UseShellExecute = true,
                    });
                    quit();
                }
                catch (Exception ex)
                {
                    install.Enabled = true;
                    Say(ex.Message, problem: true);
                    Log.Write($"install failed: {ex}");
                }
            };
            page.Controls.Add(install);
        }

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
        var page = Page("Layouts",
            "Each display keeps its own layout. Pick a preset, build a custom grid, split or merge individual zones, or drag dividers on screen.");

        foreach (var geo in ZoneManager.AllMonitors())
        {
            var zoneDevice = geo.Device;
            var rects = zones.ZonesFor(geo);
            var current = config.ZonesFor(zoneDevice);

            void ApplyLayout(List<FracRect> updated)
            {
                config.SetZones(zoneDevice, updated);
                applyChanges();
                overlay.Flash(zones);
                BeginInvoke(ShowLayouts);
            }

            page.Controls.Add(new Label
            {
                Text = $"{zoneDevice.TrimStart('\\', '.')}   ·   {geo.Bounds.Width}×{geo.Bounds.Height}   ·   " +
                       $"{geo.Describe()}   ·   {rects.Count} zones",
                AutoSize = true, ForeColor = Theme.Text, Font = Theme.Face(11f, FontStyle.Bold),
                Margin = new Padding(0, 14, 0, 6),
            });

            page.Controls.Add(CreateLayoutPreview(current, ApplyLayout));
            page.Controls.Add(Theme.Caption(
                "Tip: click a zone in the preview above to split it vertically, right-click to split it horizontally, or middle-click to remove it."));

            var presets = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Width = 560, Margin = new Padding(0, 0, 0, 6) };
            foreach (var (name, make) in SplitConfig.Presets)
            {
                bool active = ZoneEdges.Same(current, make());
                var b = Theme.Action(name, active);
                b.AutoSize = true;
                b.MinimumSize = new Size(105, 30);
                b.Padding = new Padding(6, 0, 6, 0);
                b.Click += (_, _) => ApplyLayout(make());
                presets.Controls.Add(b);
            }
            page.Controls.Add(presets);

            // Quick layout actions: add vertical column, add horizontal row, equalise, or build an N x M grid.
            var quickRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Width = 560, Margin = new Padding(0, 4, 0, 4) };

            var addCol = Theme.Action("+ Add vertical panel", primary: true);
            addCol.AutoSize = true;
            addCol.Click += (_, _) => ApplyLayout(ZoneEdges.AddColumn(config.ZonesFor(zoneDevice)));
            quickRow.Controls.Add(addCol);

            var addRow = Theme.Action("+ Add horizontal row");
            addRow.AutoSize = true;
            addRow.Click += (_, _) => ApplyLayout(ZoneEdges.AddRow(config.ZonesFor(zoneDevice)));
            quickRow.Controls.Add(addRow);

            var equalise = Theme.Action("Equalise sizes");
            equalise.AutoSize = true;
            equalise.Enabled = current.Count > 1;
            equalise.Click += (_, _) => ApplyLayout(ZoneEdges.Equalise(config.ZonesFor(zoneDevice)));
            quickRow.Controls.Add(equalise);

            page.Controls.Add(quickRow);

            var gridRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 8) };
            int curCols = ZoneEdges.Vertical(current).Count + 1;
            int curRows = ZoneEdges.Horizontal(current).Count + 1;

            gridRow.Controls.Add(new Label
            {
                Text = "Custom grid:   Columns", AutoSize = true, ForeColor = Theme.Text, Font = Theme.Face(),
                Margin = new Padding(0, 7, 6, 0),
            });
            var colBox = Theme.Number(curCols, 1, 16, 1);
            colBox.Width = 60;
            colBox.Margin = new Padding(0, 2, 10, 2);
            gridRow.Controls.Add(colBox);

            gridRow.Controls.Add(new Label
            {
                Text = "×   Rows", AutoSize = true, ForeColor = Theme.Text, Font = Theme.Face(),
                Margin = new Padding(0, 7, 6, 0),
            });
            var rowBox = Theme.Number(curRows, 1, 12, 1);
            rowBox.Width = 60;
            rowBox.Margin = new Padding(0, 2, 12, 2);
            gridRow.Controls.Add(rowBox);

            var buildGrid = Theme.Action("Build grid");
            buildGrid.Size = new Size(105, 28);
            buildGrid.Margin = new Padding(0, 1, 0, 2);
            buildGrid.Click += (_, _) => ApplyLayout(ZoneEdges.Grid((int)colBox.Value, (int)rowBox.Value));
            gridRow.Controls.Add(buildGrid);

            page.Controls.Add(gridRow);

            page.Controls.Add(new Label
            {
                Text = "Zones (split or remove individual panels to build asymmetric layouts)",
                AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Face(9f),
                Margin = new Padding(0, 6, 0, 4),
            });

            var frac = config.ZonesFor(zoneDevice);
            for (int i = 0; i < frac.Count; i++)
            {
                int index = i;
                var z = frac[i];
                int wPct = (int)Math.Round((z.R - z.L) * 100);
                int hPct = (int)Math.Round((z.B - z.T) * 100);

                var zRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 1, 0, 2) };

                zRow.Controls.Add(new Label
                {
                    Text = $"Zone {i + 1}  ({wPct}% × {hPct}%)",
                    Width = 148, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Theme.Text, Font = Theme.Face(9f, FontStyle.Bold),
                    Margin = new Padding(0, 4, 4, 0),
                });

                var splitV = Theme.Action("Split L/R");
                splitV.Size = new Size(78, 26);
                splitV.Font = Theme.Face(8.5f);
                splitV.Margin = new Padding(0, 1, 4, 1);
                splitV.Enabled = ZoneEdges.CanSplitZone(frac, index, vertical: true);
                splitV.Click += (_, _) => ApplyLayout(ZoneEdges.SplitZone(config.ZonesFor(zoneDevice), index, vertical: true));
                zRow.Controls.Add(splitV);

                var splitH = Theme.Action("Split T/B");
                splitH.Size = new Size(78, 26);
                splitH.Font = Theme.Face(8.5f);
                splitH.Margin = new Padding(0, 1, 4, 1);
                splitH.Enabled = ZoneEdges.CanSplitZone(frac, index, vertical: false);
                splitH.Click += (_, _) => ApplyLayout(ZoneEdges.SplitZone(config.ZonesFor(zoneDevice), index, vertical: false));
                zRow.Controls.Add(splitH);

                var removeBtn = Theme.Action("Remove");
                removeBtn.Size = new Size(72, 26);
                removeBtn.Font = Theme.Face(8.5f);
                removeBtn.Margin = new Padding(0, 1, 10, 1);
                removeBtn.Enabled = ZoneEdges.CanRemoveZone(frac, index);
                removeBtn.Click += (_, _) => ApplyLayout(ZoneEdges.RemoveZone(config.ZonesFor(zoneDevice), index));
                zRow.Controls.Add(removeBtn);

                var cover = Theme.Toggle("Over taskbar", z.CoverTaskbar);
                cover.Font = Theme.Face(9f);
                cover.Margin = new Padding(0, 4, 0, 0);
                cover.CheckedChanged += (_, _) =>
                {
                    var edited = ZoneEdges.Clone(config.ZonesFor(zoneDevice));
                    if (index >= edited.Count) return;
                    edited[index].CoverTaskbar = cover.Checked;
                    config.SetZones(zoneDevice, edited);
                    applyChanges();
                    overlay.Flash(zones);
                    BeginInvoke(ShowLayouts);
                };
                zRow.Controls.Add(cover);

                page.Controls.Add(zRow);
            }
            page.Controls.Add(Theme.Caption(
                "\"Over taskbar\" measures a zone against the whole display rather than the work area, " +
                "and keeps windows placed in it above the taskbar — genuinely fullscreen over its part of " +
                "the screen while the taskbar stays visible and usable everywhere else."));

            // Nothing to cover means nothing to see, and that is worth saying out loud rather than
            // leaving someone to conclude the setting is broken.
            var basis = zones.Basis(geo);
            if (basis.Left == geo.Bounds.Left && basis.Top == geo.Bounds.Top
                && basis.Right == geo.Bounds.Right && basis.Bottom == geo.Bounds.Bottom
                && zones.ActiveBarStrip(geo) is null)
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

        var edit = Theme.Action("Edit & drag zones on screen…", primary: true);
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

    private static Panel CreateLayoutPreview(List<FracRect> zonesList, Action<List<FracRect>> onEdited)
    {
        var snapshot = ZoneEdges.Clone(zonesList);
        int hoveredIndex = -1;

        var panel = new Panel
        {
            Size = new Size(560, 116),
            BackColor = Theme.Panel,
            Margin = new Padding(0, 2, 0, 4),
            Cursor = Cursors.Hand,
        };

        Rectangle BoxFor(FracRect f)
        {
            int pad = 6;
            int w = panel.Width - pad * 2;
            int h = panel.Height - pad * 2;
            int l = pad + (int)Math.Round(f.L * w);
            int t = pad + (int)Math.Round(f.T * h);
            int r = pad + (int)Math.Round(f.R * w);
            int b = pad + (int)Math.Round(f.B * h);
            return new Rectangle(l, t, Math.Max(2, r - l), Math.Max(2, b - t));
        }

        int HitZone(Point pt)
        {
            for (int i = 0; i < snapshot.Count; i++)
                if (BoxFor(snapshot[i]).Contains(pt)) return i;
            return -1;
        }

        panel.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using var fill = new SolidBrush(Color.FromArgb(52, 92, 160));
            using var hoverFill = new SolidBrush(Color.FromArgb(74, 132, 220));
            using var border = new Pen(Color.FromArgb(150, 190, 255), 1.5f);
            using var coverPen = new Pen(Color.FromArgb(255, 190, 110), 1.5f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            using var numFont = Theme.Face(11f, FontStyle.Bold);
            using var subFont = Theme.Face(8f);
            using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            for (int i = 0; i < snapshot.Count; i++)
            {
                var r = Rectangle.Inflate(BoxFor(snapshot[i]), -2, -2);
                if (r.Width <= 0 || r.Height <= 0) continue;

                g.FillRectangle(i == hoveredIndex ? hoverFill : fill, r);
                g.DrawRectangle(border, r);
                if (snapshot[i].CoverTaskbar && r.Width > 6 && r.Height > 6)
                    g.DrawRectangle(coverPen, Rectangle.Inflate(r, -2, -2));

                int wPct = (int)Math.Round((snapshot[i].R - snapshot[i].L) * 100);
                int hPct = (int)Math.Round((snapshot[i].B - snapshot[i].T) * 100);

                if (r.Height >= 38 && r.Width >= 46)
                {
                    var topRect = new Rectangle(r.X, r.Y + r.Height / 2 - 16, r.Width, 18);
                    var botRect = new Rectangle(r.X, r.Y + r.Height / 2 + 1, r.Width, 16);
                    g.DrawString($"{i + 1}", numFont, Brushes.White, topRect, centred);
                    g.DrawString($"{wPct}%×{hPct}%", subFont, Brushes.White, botRect, centred);
                }
                else
                {
                    g.DrawString($"{i + 1}", subFont, Brushes.White, r, centred);
                }
            }
        };

        panel.MouseMove += (_, e) =>
        {
            int hit = HitZone(e.Location);
            if (hit != hoveredIndex)
            {
                hoveredIndex = hit;
                panel.Invalidate();
            }
        };

        panel.MouseLeave += (_, _) =>
        {
            if (hoveredIndex != -1)
            {
                hoveredIndex = -1;
                panel.Invalidate();
            }
        };

        panel.MouseDown += (_, e) =>
        {
            int hit = HitZone(e.Location);
            if (hit < 0) return;

            if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Shift) != 0))
            {
                if (ZoneEdges.CanRemoveZone(snapshot, hit))
                    onEdited(ZoneEdges.RemoveZone(snapshot, hit));
            }
            else if (e.Button == MouseButtons.Left)
            {
                if (ZoneEdges.CanSplitZone(snapshot, hit, vertical: true))
                    onEdited(ZoneEdges.SplitZone(snapshot, hit, vertical: true));
            }
            else if (e.Button == MouseButtons.Right)
            {
                if (ZoneEdges.CanSplitZone(snapshot, hit, vertical: false))
                    onEdited(ZoneEdges.SplitZone(snapshot, hit, vertical: false));
            }
        };

        return panel;
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
            "yours docked to the same edge sits flush against it rather than above a gap. Put back " +
            "when ScweenSpit exits, when Windows signs out, and on the next launch if this switch " +
            "is off.\n\n" +
            "If ScweenSpit is ever killed while the taskbar is hidden, run ScweenSpit.exe with " +
            "--restore to put it back without starting the app. Task Manager's Run New Task opens " +
            "on Ctrl+Shift+Esc with no taskbar at all, which is what makes that reachable.\n\n" +
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
                if (enabled.Checked)
                {
                    config.Bars[device] = settings ?? new BarSettings
                    {
                        ShowUsage = config.Claude.Enabled || config.Bars.Values.Any(b => b.ShowUsage),
                        ShowAntigravity = config.Antigravity.Enabled || config.Bars.Values.Any(b => b.ShowAntigravity),
                        ShowRemoteChats = config.RemoteChats.Enabled || config.Bars.Values.Any(b => b.ShowRemoteChats),
                        ShowBambu = config.Bambu.Enabled || config.Bars.Values.Any(b => b.ShowBambu),
                        ShowMachine = config.Bars.Values.Any(b => b.ShowMachine),
                    };
                }
                else
                {
                    config.Bars.Remove(device);
                }
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
                var gap = Theme.Number(settings.FloatMargin, 0, 40, 1);
                gap.ValueChanged += (_, _) => { settings.FloatMargin = (int)gap.Value; Save(); };
                Row(page, "Gap on the open side (pixels)", gap);

                var sideGap = Theme.Number(settings.SideGap, 0, 40, 1);
                sideGap.ValueChanged += (_, _) => { settings.SideGap = (int)sideGap.Value; Save(); };
                Row(page, "Gap at the ends (pixels)", sideGap);

                var edgeGap = Theme.Number(settings.EdgeGap, 0, 40, 1);
                edgeGap.ValueChanged += (_, _) => { settings.EdgeGap = (int)edgeGap.Value; Save(); };
                Row(page, "Gap from the screen edge (pixels)", edgeGap);
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

            var start = Theme.Toggle("Start button at the leading end", settings.ShowStartButton);
            start.CheckedChanged += (_, _) => { settings.ShowStartButton = start.Checked; Save(); };
            page.Controls.Add(start);

            var moveMenu = Theme.Toggle("Open the Start menu over the button", settings.MoveStartMenu);
            moveMenu.CheckedChanged += (_, _) => { settings.MoveStartMenu = moveMenu.Checked; Save(); };
            page.Controls.Add(moveMenu);
            page.Controls.Add(Theme.Caption(
                "No effect on Windows 11 build 26200 and later: the menu's window can be moved, and " +
                "reports its new position afterwards, but what is drawn stays where the shell put it."));

            var badges = Theme.Toggle("Show notification counts on icons", settings.ShowBadges);
            badges.CheckedChanged += (_, _) => { settings.ShowBadges = badges.Checked; Save(); };
            page.Controls.Add(badges);
            page.Controls.Add(Theme.Caption(
                "Read from the count an application puts at the front of its own window title — " +
                "\"(3) Microsoft Teams\". The badge Explorer's taskbar draws comes from an API that " +
                "hands it to the shell and never hands it back, so a taskbar that is not Explorer's " +
                "cannot ask what Explorer was told. Applications that count only in that badge, and " +
                "not in their title, will not show one here."));

            var machine = Theme.Toggle("Show this machine's load (CPU, RAM, disk)", settings.ShowMachine);
            machine.CheckedChanged += (_, _) => { settings.ShowMachine = machine.Checked; Save(); };
            page.Controls.Add(machine);
            page.Controls.Add(Theme.Caption(
                "Processor and memory as they are now, and how full the system drive is. Hover for " +
                "the figures in full; clicking opens Task Manager."));

            var previews = Theme.Toggle("Live previews on hover", settings.ShowPreviews);
            previews.CheckedChanged += (_, _) => { settings.ShowPreviews = previews.Checked; Save(); };
            page.Controls.Add(previews);

            var iconsOnly = Theme.Toggle("Icons only, no window titles", settings.IconsOnly);
            iconsOnly.CheckedChanged += (_, _) => { settings.IconsOnly = iconsOnly.Checked; Save(); };
            page.Controls.Add(iconsOnly);

            var status = Theme.Toggle("Show volume, network, battery and the clock", settings.ShowStatus);
            status.CheckedChanged += (_, _) => { settings.ShowStatus = status.Checked; Save(); };
            page.Controls.Add(status);

            var usage = Theme.Toggle("Show Claude usage bars", settings.ShowUsage);
            usage.CheckedChanged += (_, _) =>
            {
                settings.ShowUsage = usage.Checked;
                if (usage.Checked) config.Claude.Enabled = true;
                else if (!config.Bars.Values.Any(b => b.ShowUsage)) config.Claude.Enabled = false;
                Save();
                if (config.Claude.Enabled) ClaudeUsage.Refresh();
                BeginInvoke(ShowTaskbar);
            };
            page.Controls.Add(usage);

            // A toggle that appears to do nothing is worse than one that is not there. Say what is
            // still missing, and offer the page that fixes it.
            if (settings.ShowUsage && !ClaudeUsage.Enabled)
            {
                page.Controls.Add(new Label
                {
                    Text = "⚠  The strip is on the bar, but has nothing to show until Claude usage "
                         + "tracking is switched on and either Claude Code is signed in or a session key is saved.",
                    AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Color.FromArgb(235, 185, 110),
                    Font = Theme.Face(9f), Margin = new Padding(0, 2, 0, 4),
                });

                var setUp = Theme.Action("Set up Claude usage", primary: true);
                setUp.AutoSize = true;
                setUp.Click += (_, _) => BeginInvoke(() => GoTo("Claude usage"));
                page.Controls.Add(setUp);
            }

            if (!ClaudeUsage.Enabled)
                page.Controls.Add(Theme.Caption(
                    "Nothing is drawn for Claude until tracking is switched on under Claude usage."));

            var antigravity = Theme.Toggle("Show Antigravity usage bars", settings.ShowAntigravity);
            antigravity.CheckedChanged += (_, _) =>
            {
                settings.ShowAntigravity = antigravity.Checked;
                if (antigravity.Checked) config.Antigravity.Enabled = true;
                else if (!config.Bars.Values.Any(b => b.ShowAntigravity)) config.Antigravity.Enabled = false;
                Save();
                if (config.Antigravity.Enabled) AntigravityUsage.Refresh();
                BeginInvoke(ShowTaskbar);
            };
            page.Controls.Add(antigravity);

            if (settings.ShowAntigravity && !AntigravityUsage.Enabled)
            {
                page.Controls.Add(new Label
                {
                    Text = "⚠  The Antigravity strip is on the bar, but tracking is still switched "
                         + "off under Antigravity usage.",
                    AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Color.FromArgb(235, 185, 110),
                    Font = Theme.Face(9f), Margin = new Padding(0, 2, 0, 4),
                });

                var setUpAg = Theme.Action("Set up Antigravity usage", primary: true);
                setUpAg.AutoSize = true;
                setUpAg.Click += (_, _) => BeginInvoke(() => GoTo("Antigravity usage"));
                page.Controls.Add(setUpAg);
            }

            if (!AntigravityUsage.Enabled)
                page.Controls.Add(Theme.Caption(
                    "Drawn next to the Claude bars; reads from Google's Cloud Code API or the local Antigravity language server once enabled."));

            var remoteChats = Theme.Toggle("Show remote server chat status (Claude & Antigravity)", settings.ShowRemoteChats);
            remoteChats.CheckedChanged += (_, _) =>
            {
                settings.ShowRemoteChats = remoteChats.Checked;
                if (remoteChats.Checked) config.RemoteChats.Enabled = true;
                else if (!config.Bars.Values.Any(b => b.ShowRemoteChats)) config.RemoteChats.Enabled = false;
                Save();
                if (config.RemoteChats.Enabled) RemoteChatUsage.Refresh();
                BeginInvoke(ShowTaskbar);
            };
            page.Controls.Add(remoteChats);

            if (settings.ShowRemoteChats && !RemoteChatUsage.Enabled)
            {
                page.Controls.Add(new Label
                {
                    Text = "⚠  The remote chat strip is on the bar, but tracking is still switched "
                         + "off under Remote chats.",
                    AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Color.FromArgb(235, 185, 110),
                    Font = Theme.Face(9f), Margin = new Padding(0, 2, 0, 4),
                });

                var setUpRemote = Theme.Action("Set up Remote chats", primary: true);
                setUpRemote.AutoSize = true;
                setUpRemote.Click += (_, _) => BeginInvoke(() => GoTo("Remote chats"));
                page.Controls.Add(setUpRemote);
            }

            if (!RemoteChatUsage.Enabled)
                page.Controls.Add(Theme.Caption(
                    "Grouped with the Claude & Antigravity usage bars; shows live chat counts, " +
                    "chats needing interaction, and completed sessions per remote server."));

            var orchestra = Theme.Toggle("Show orchestrator status (once a feed answers)",
                                         settings.ShowOrchestra ?? geo.IsPrimary);
            orchestra.CheckedChanged += (_, _) =>
            {
                settings.ShowOrchestra = orchestra.Checked;
                if (orchestra.Checked && !config.Orchestra.Enabled)
                {
                    config.Orchestra.Enabled = true;
                    OrchestraUsage.Configure(config.Orchestra);
                }
                Save();
            };
            page.Controls.Add(orchestra);
            page.Controls.Add(Theme.Caption(
                "How long the orchestrator has been running and how many child sessions of each kind it " +
                "has. Nothing is drawn until one of the feeds under Orchestrator answers."));

            var bambu = Theme.Toggle("Show Bambu printer status bars", settings.ShowBambu);
            bambu.CheckedChanged += (_, _) =>
            {
                settings.ShowBambu = bambu.Checked;
                if (bambu.Checked) config.Bambu.Enabled = true;
                else if (!config.Bars.Values.Any(b => b.ShowBambu)) config.Bambu.Enabled = false;
                Save();
                if (config.Bambu.Enabled) BambuUsage.Refresh();
                BeginInvoke(ShowTaskbar);
            };
            page.Controls.Add(bambu);

            if (settings.ShowBambu && !BambuUsage.Enabled)
            {
                page.Controls.Add(new Label
                {
                    Text = "⚠  The Bambu printer strip is on the bar, but tracking is still switched "
                         + "off under Bambu printers.",
                    AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Color.FromArgb(235, 185, 110),
                    Font = Theme.Face(9f), Margin = new Padding(0, 2, 0, 4),
                });

                var setUpBambu = Theme.Action("Set up Bambu printers", primary: true);
                setUpBambu.AutoSize = true;
                setUpBambu.Click += (_, _) => BeginInvoke(() => GoTo("Bambu printers"));
                page.Controls.Add(setUpBambu);
            }

            if (!BambuUsage.Enabled)
                page.Controls.Add(Theme.Caption(
                    "Drawn next to the AI usage bars; streams live print status from Bambu Cloud once enabled."));

            page.Controls.Add(new Label
            {
                Text = "Tracker size & layout", AutoSize = true, ForeColor = Theme.Text,
                Font = Theme.Face(10.5f, FontStyle.Bold), Margin = new Padding(0, 12, 0, 2),
            });

            var expandTrackers = Theme.Toggle("Expand trackers into unused width on wide horizontal bars", settings.ExpandTrackers);
            expandTrackers.CheckedChanged += (_, _) => { settings.ExpandTrackers = expandTrackers.Checked; Save(); };
            page.Controls.Add(expandTrackers);

            var trackerScale = Theme.Number(settings.TrackerScale, 80, 300, 10);
            trackerScale.ValueChanged += (_, _) => { settings.TrackerScale = (int)trackerScale.Value; Save(); };
            Row(page, "Tracker column width scale (%)", trackerScale);

            var trackerRows = Theme.Number(settings.TrackerMaxRows, 1, 6, 1);
            trackerRows.ValueChanged += (_, _) => { settings.TrackerMaxRows = (int)trackerRows.Value; Save(); };
            Row(page, "Max rows per tracker column (horizontal bar)", trackerRows);
            page.Controls.Add(Theme.Caption(
                "You can also drag the dividers on the taskbar to resize individual trackers (e.g. making " +
                "Computer usage smaller), or double-click a divider to reset it. Drag a tracker itself " +
                "along the bar to change the order they sit in. " +
                "3 rows per column puts 5 Bambu printers or multiple server rows into 2 side-by-side " +
                "columns. Raising the bar's Thickness above 50 px also scales up tracker fonts and bars."));

            bool anyCustomWidths = barSettings.RemoteChatsWidth is not null
                || barSettings.AntigravityChatsWidth is not null
                || barSettings.UsageWidth is not null
                || barSettings.AntigravityWidth is not null
                || barSettings.BambuWidth is not null
                || barSettings.MachineWidth is not null
                || barSettings.OrchestraWidth is not null;

            if (anyCustomWidths)
            {
                var resetWidths = Theme.Action("Reset dragged tracker sizes");
                resetWidths.AutoSize = true;
                resetWidths.Margin = new Padding(0, 4, 0, 4);
                resetWidths.Click += (_, _) =>
                {
                    barSettings.RemoteChatsWidth = null;
                    barSettings.AntigravityChatsWidth = null;
                    barSettings.UsageWidth = null;
                    barSettings.AntigravityWidth = null;
                    barSettings.BambuWidth = null;
                    barSettings.MachineWidth = null;
                    barSettings.OrchestraWidth = null;
                    Save();
                    BeginInvoke(ShowTaskbar);
                };
                page.Controls.Add(resetWidths);
            }

            if (barSettings.TrackerOrder is not null)
            {
                var resetOrder = Theme.Action("Reset dragged tracker order");
                resetOrder.AutoSize = true;
                resetOrder.Margin = new Padding(0, 4, 0, 4);
                resetOrder.Click += (_, _) =>
                {
                    barSettings.TrackerOrder = null;
                    Save();
                    BeginInvoke(ShowTaskbar);
                };
                page.Controls.Add(resetOrder);
            }
        }
    }

    // ---- claude & antigravity usage ----------------------------------------

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

    /// <summary>
    /// Ensures that turning a tracker on from its own settings page also turns its strip on for the
    /// active bar(s), and turning it off clears the per-bar flag so the two settings never disagree.
    /// </summary>
    private void SyncTrackerToBars(bool enabled, Func<BarSettings, bool> getter, Action<BarSettings, bool> setter)
    {
        if (!enabled)
        {
            foreach (var b in config.Bars.Values) setter(b, false);
            return;
        }

        var attached = ZoneManager.AllMonitors()
            .Where(m => config.Bars.ContainsKey(m.Device))
            .Select(m => config.Bars[m.Device])
            .ToList();
        var targets = attached.Count > 0 ? attached : config.Bars.Values.ToList();
        if (targets.Count > 0 && !targets.Any(getter))
        {
            foreach (var b in targets) setter(b, true);
        }
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
        on.CheckedChanged += (_, _) =>
        {
            claude.Enabled = on.Checked;
            SyncTrackerToBars(claude.Enabled, b => b.ShowUsage, (b, v) => b.ShowUsage = v);
            Save();
            if (claude.Enabled) ClaudeUsage.Refresh();
            BeginInvoke(ShowClaude);
        };
        page.Controls.Add(on);

        if (!claude.Enabled)
        {
            page.Controls.Add(Theme.Caption(
                "While this is off, no credential is read and no request is ever made to Anthropic."));
            return;
        }

        // ---- persistent login via Claude OAuth / Claude Code
        bool haveClaudeCode = ClaudeUsage.HasClaudeCodeCredentials();
        bool signedInOAuth = ClaudeUsage.HasSignedInOAuth;
        string? oauthEmail = ClaudeUsage.SignedInEmail;

        var useClaudeCode = Theme.Toggle("Use Claude OAuth / Claude Code login when available (recommended — auto-renews & never expires)",
                                         claude.UseClaudeCode);
        useClaudeCode.CheckedChanged += (_, _) =>
        {
            claude.UseClaudeCode = useClaudeCode.Checked;
            Save();
            ClaudeUsage.Refresh();
            RemoteChatUsage.Refresh();
            BeginInvoke(ShowClaude);
        };
        page.Controls.Add(useClaudeCode);

        page.Controls.Add(new Label
        {
            Text = signedInOAuth && !string.IsNullOrWhiteSpace(oauthEmail)
                ? $"● Signed in to Claude as {oauthEmail} (auto-renewing OAuth)"
                : haveClaudeCode
                    ? $"● Claude Code login detected at {ClaudeUsage.CredentialsPath} (auto-renewing OAuth)"
                    : "○ Not signed in via Claude OAuth — click 'Sign in with Claude' below or paste a session key",
            AutoSize = true, MaximumSize = new Size(470, 0),
            ForeColor = (haveClaudeCode || signedInOAuth) && claude.UseClaudeCode ? Color.FromArgb(110, 210, 140) : Theme.Muted,
            Font = Theme.Face(9f, FontStyle.Bold), Margin = new Padding(0, 2, 0, 4),
        });

        var oauthButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 2, 0, 4) };
        var signInClaude = Theme.Action(signedInOAuth || haveClaudeCode ? "Re-sign in with Claude" : "Sign in with Claude", primary: !(signedInOAuth || haveClaudeCode));
        signInClaude.AutoSize = true;
        signInClaude.Click += async (_, _) =>
        {
            signInClaude.Enabled = false;
            Say("Finish signing in to Claude in your browser…");
            try
            {
                var (ok, message) = await ClaudeUsage.SignInWithClaudeAsync();
                Say(message, problem: !ok);
                if (ok && !IsDisposed) BeginInvoke(ShowClaude);
            }
            finally
            {
                if (!signInClaude.IsDisposed) signInClaude.Enabled = true;
            }
        };
        oauthButtons.Controls.Add(signInClaude);

        if (signedInOAuth)
        {
            var signOutClaude = Theme.Action("Sign out OAuth");
            signOutClaude.AutoSize = true;
            signOutClaude.Click += (_, _) =>
            {
                ClaudeUsage.SignOutOAuth();
                Say("Signed out of Claude OAuth.");
                BeginInvoke(ShowClaude);
            };
            oauthButtons.Controls.Add(signOutClaude);
        }
        page.Controls.Add(oauthButtons);

        page.Controls.Add(Theme.Caption(
            "Signing in with Claude stores an OAuth refresh token encrypted to this Windows account (and " +
            "syncs ~/.claude/.credentials.json) with full usage + remote session permissions, renewing it " +
            "automatically so neither the usage tracker nor the remote chat tracker expires. If you also " +
            "save a browser session key below, ScweenSpit keeps both as a fallback."));

        // ---- the browser session key (primary if Claude Code is not installed, or fallback)
        bool haveKey = !string.IsNullOrWhiteSpace(claude.SessionKey);
        page.Controls.Add(Theme.Caption(
            "Or sign in to claude.ai in your browser, press F12 and open Application ▸ Cookies ▸ " +
            "https://claude.ai. Copy the value of the sessionKey row and paste it below. It starts " +
            "with sk-ant-. ScweenSpit adopts rotated cookies automatically, though claude.ai still " +
            "ends browser sessions after a few weeks."));

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
            "Walks the same path a refresh takes and says where it stops. Credentials are never "
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

    private void ShowAntigravity()
    {
        var antigravity = config.Antigravity;
        var page = Page("Antigravity usage",
            "Model quota limits from Antigravity, drawn right next to the Claude usage bars in the " +
            "status cluster of any bar that asks for them.");

        page.Controls.Add(Theme.Caption(
            "Protocol adapted from AntigravityQuota by Henrik-3 and antigravity-usage by skainguyen1412 " +
            "(MIT). Sign in with Google below so ScweenSpit can read your quotas directly from Google's " +
            "Cloud Code API 24/7 — even when Antigravity is closed on Windows or running in WSL — or " +
            "leave it unsigned-in to read only from the local language server while the IDE is open."));

        var on = Theme.Toggle("Track Antigravity usage", antigravity.Enabled);
        on.CheckedChanged += (_, _) =>
        {
            antigravity.Enabled = on.Checked;
            SyncTrackerToBars(antigravity.Enabled, b => b.ShowAntigravity, (b, v) => b.ShowAntigravity = v);
            Save();
            if (antigravity.Enabled) AntigravityUsage.Refresh();
            BeginInvoke(ShowAntigravity);
        };
        page.Controls.Add(on);

        if (!antigravity.Enabled)
        {
            page.Controls.Add(Theme.Caption(
                "While this is off, ScweenSpit never queries Google or looks for the local language server."));
            return;
        }

        // ---- Google Cloud Code sign-in (works without Antigravity running locally)
        bool signedIn = AntigravityUsage.HasSignedInAccount;
        bool hasCliFile = AntigravityUsage.HasCliTokenFile();
        string? email = AntigravityUsage.SignedInEmail;

        page.Controls.Add(new Label
        {
            Text = signedIn
                ? $"● Signed in to Google{(email is not null ? $" as {email}" : "")} — works without Antigravity open locally"
                : hasCliFile
                    ? $"● Antigravity CLI login detected at {AntigravityUsage.CliTokenPath}"
                    : "○ Not signed in to Google — only works while Antigravity IDE is open on Windows",
            AutoSize = true, MaximumSize = new Size(470, 0),
            ForeColor = signedIn || hasCliFile ? Color.FromArgb(110, 210, 140) : Theme.Muted,
            Font = Theme.Face(9f, FontStyle.Bold), Margin = new Padding(0, 6, 0, 4),
        });

        var authButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 2, 0, 4) };

        var signIn = Theme.Action(signedIn ? "Switch Google account" : "Sign in with Google", primary: !signedIn);
        signIn.AutoSize = true;
        signIn.Click += async (_, _) =>
        {
            signIn.Enabled = false;
            Say("Finish signing in to Google in your browser…");
            try
            {
                var (ok, message) = await AntigravityUsage.SignInWithGoogleAsync();
                Say(message, problem: !ok);
                if (ok && !IsDisposed) BeginInvoke(ShowAntigravity);
            }
            finally
            {
                if (!signIn.IsDisposed) signIn.Enabled = true;
            }
        };
        authButtons.Controls.Add(signIn);

        if (signedIn)
        {
            var signOut = Theme.Action("Sign out");
            signOut.AutoSize = true;
            signOut.Click += (_, _) =>
            {
                AntigravityUsage.SignOut();
                Say("Signed out of Google.");
                BeginInvoke(ShowAntigravity);
            };
            authButtons.Controls.Add(signOut);
        }

        page.Controls.Add(authButtons);
        page.Controls.Add(Theme.Caption(
            "Signing in stores a Google OAuth refresh token encrypted to this Windows account with " +
            "DPAPI. Because refresh tokens do not expire on their own, the strip stays live without " +
            "needing Antigravity open on this machine."));

        var weekly = Theme.Toggle("Show the seven-day limit", antigravity.ShowWeekly);
        weekly.CheckedChanged += (_, _) => { antigravity.ShowWeekly = weekly.Checked; Save(); AntigravityUsage.Refresh(); };
        page.Controls.Add(weekly);

        page.Controls.Add(Theme.Caption(
            "Shows both Antigravity quota pools side-by-side under '✦ Antigravity Pro·Flash': your " +
            "IDE production pool (IDE 5h / IDE 7d) and your CLI / Remote Control pool (CLI 5h / CLI 7d)."));

        var interval = Theme.Number(antigravity.RefreshSeconds, 15, 3600, 15);
        interval.ValueChanged += (_, _) => { antigravity.RefreshSeconds = (int)interval.Value; Save(); };
        Row(page, "Seconds between checks", interval);

        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 10, 0, 4) };

        var check = Theme.Action("Check now", primary: signedIn);
        check.AutoSize = true;
        check.Click += (_, _) => AntigravityUsage.Refresh();
        actions.Controls.Add(check);

        var diagnosis = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Visible = false,
            Width = 460, Height = 130, BackColor = Theme.Raised, ForeColor = Theme.Muted,
            Font = new Font(FontFamily.GenericMonospace, 8.5f), BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 8, 0, 8),
        };

        var test = Theme.Action("Test connection");
        test.AutoSize = true;
        test.Click += async (_, _) =>
        {
            test.Enabled = false;
            diagnosis.Visible = true;
            diagnosis.Text = "Checking Antigravity connection…";
            try
            {
                diagnosis.Text = await Task.Run(AntigravityUsage.SelfTest);
            }
            catch (Exception ex) { diagnosis.Text = ex.Message; }
            finally { test.Enabled = true; }
        };
        actions.Controls.Add(test);
        page.Controls.Add(actions);

        page.Controls.Add(Theme.Caption(
            "Tests Google Cloud Code API (if signed in) and the local language_server process, and " +
            "reports what it found without including any OAuth or CSRF token."));
        page.Controls.Add(diagnosis);

        var state = new Label
        {
            AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Face(9f),
            MaximumSize = new Size(460, 0), Margin = new Padding(0, 12, 0, 8),
        };
        page.Controls.Add(state);

        void Report() => state.Text = UsageStrip.AntigravityTip(AntigravityUsage.Current);
        Report();

        var watch = new System.Windows.Forms.Timer { Interval = 1500 };
        watch.Tick += (_, _) => Report();
        watch.Start();
        page.Disposed += (_, _) => { watch.Stop(); watch.Dispose(); };

        page.Controls.Add(Theme.Caption(
            "Turn the bars on per display under Taskbar ▸ Show Antigravity usage bars."));
    }

    private void ShowBambu()
    {
        var bambu = config.Bambu;
        var page = Page("Bambu printers",
            "Live status of your Bambu Lab printers from Bambu Cloud, drawn as a stack of rows " +
            "right next to your AI session monitors on any bar that asks for them.");

        page.Controls.Add(Theme.Caption(
            "Connects to Bambu Cloud (api.bambulab.com + MQTT over TLS) to discover your bound " +
            "printers and stream live progress, remaining time, and error/finish states in real time. " +
            "Reorder your printers below so the lines on the bar match their order in your closet."));

        var on = Theme.Toggle("Track Bambu Lab printers", bambu.Enabled);
        on.CheckedChanged += (_, _) =>
        {
            bambu.Enabled = on.Checked;
            SyncTrackerToBars(bambu.Enabled, b => b.ShowBambu, (b, v) => b.ShowBambu = v);
            Save();
            if (bambu.Enabled) BambuUsage.Refresh();
            BeginInvoke(ShowBambu);
        };
        page.Controls.Add(on);

        if (!bambu.Enabled)
        {
            page.Controls.Add(Theme.Caption(
                "While this is off, ScweenSpit never connects to Bambu Cloud or its MQTT broker."));
            return;
        }

        bool signedIn = BambuUsage.HasSignedInAccount;
        string? signedEmail = BambuUsage.SignedInEmail;
        bool mqttUp = BambuUsage.Current?.MqttConnected == true;

        page.Controls.Add(new Label
        {
            Text = signedIn
                ? $"● Signed in to Bambu Cloud{(signedEmail is not null ? $" as {signedEmail}" : "")}" +
                  (mqttUp ? " — live MQTT stream connected" : "")
                : "○ Not signed in to Bambu Cloud — sign in below to load your printers",
            AutoSize = true, MaximumSize = new Size(490, 0),
            ForeColor = signedIn ? Color.FromArgb(110, 210, 140) : Theme.Muted,
            Font = Theme.Face(9f, FontStyle.Bold), Margin = new Padding(0, 6, 0, 6),
        });

        var region = Theme.Choice(["Global", "China"], bambu.Region);
        region.SelectedIndexChanged += (_, _) =>
        {
            if (region.SelectedItem is string r)
            {
                bambu.Region = r;
                Save();
            }
        };
        Row(page, "Bambu Cloud region", region);

        var emailBox = new TextBox
        {
            Width = 300, BackColor = Theme.Raised, ForeColor = Theme.Text,
            Font = Theme.Face(), BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 2, 0, 6),
            Text = bambu.AccountEmail ?? "",
            PlaceholderText = "you@example.com",
        };
        Row(page, "Bambu account email", emailBox);

        var passwordBox = new TextBox
        {
            Width = 300, UseSystemPasswordChar = true, BackColor = Theme.Raised, ForeColor = Theme.Text,
            Font = Theme.Face(), BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 2, 0, 6),
            PlaceholderText = signedIn ? "Signed in — enter password only to sign in again" : "Password",
        };
        Row(page, "Password", passwordBox);

        var codePanel = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 2, 0, 6),
            Visible = BambuUsage.PendingSignInStep is BambuSignInStep.NeedsEmailCode or BambuSignInStep.NeedsTfaCode,
        };

        var codeLabel = new Label
        {
            Text = BambuUsage.PendingSignInStep == BambuSignInStep.NeedsTfaCode
                ? "2FA code"
                : "6-digit email code",
            AutoSize = true, ForeColor = Color.FromArgb(235, 185, 110),
            Font = Theme.Face(9f, FontStyle.Bold), Margin = new Padding(0, 7, 10, 0),
        };
        codePanel.Controls.Add(codeLabel);

        var codeBox = new TextBox
        {
            Width = 120, BackColor = Theme.Raised, ForeColor = Theme.Text,
            Font = Theme.Face(), BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 4, 8, 4), PlaceholderText = "123456",
        };
        codePanel.Controls.Add(codeBox);

        var verifyBtn = Theme.Action("Verify code", primary: true);
        verifyBtn.Width = 125;
        verifyBtn.Click += async (_, _) =>
        {
            verifyBtn.Enabled = false;
            Say("Verifying code with Bambu Cloud…");
            try
            {
                string selectedRegion = region.SelectedItem as string ?? bambu.Region;
                var (step, msg) = await BambuUsage.CompleteVerificationAsync(
                    codeBox.Text, emailBox.Text, selectedRegion);
                Say(msg, problem: step != BambuSignInStep.SignedIn);
                if (step == BambuSignInStep.SignedIn && !IsDisposed)
                {
                    Save();
                    BeginInvoke(ShowBambu);
                }
            }
            finally
            {
                if (!verifyBtn.IsDisposed) verifyBtn.Enabled = true;
            }
        };
        codePanel.Controls.Add(verifyBtn);

        var authButtons = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 2, 0, 4),
        };

        var signInBtn = Theme.Action(signedIn ? "Re-sign in" : "Sign in", primary: !signedIn);
        signInBtn.AutoSize = true;
        signInBtn.Click += async (_, _) =>
        {
            signInBtn.Enabled = false;
            Say("Signing in to Bambu Cloud…");
            try
            {
                string selectedRegion = region.SelectedItem as string ?? bambu.Region;
                var (step, msg) = await BambuUsage.SignInAsync(
                    selectedRegion, emailBox.Text, passwordBox.Text);
                Say(msg, problem: step == BambuSignInStep.Failed);

                if (step == BambuSignInStep.SignedIn && !IsDisposed)
                {
                    Save();
                    BeginInvoke(ShowBambu);
                }
                else if (step is BambuSignInStep.NeedsEmailCode or BambuSignInStep.NeedsTfaCode && !codePanel.IsDisposed)
                {
                    codeLabel.Text = step == BambuSignInStep.NeedsTfaCode ? "2FA code" : "6-digit email code";
                    codePanel.Visible = true;
                    codeBox.Focus();
                }
            }
            finally
            {
                if (!signInBtn.IsDisposed) signInBtn.Enabled = true;
            }
        };
        authButtons.Controls.Add(signInBtn);

        var sendCodeBtn = Theme.Action("Send email code");
        sendCodeBtn.AutoSize = true;
        sendCodeBtn.Click += async (_, _) =>
        {
            sendCodeBtn.Enabled = false;
            try
            {
                string selectedRegion = region.SelectedItem as string ?? bambu.Region;
                var (ok, msg) = await BambuUsage.RequestEmailCodeAsync(selectedRegion, emailBox.Text);
                Say(msg, problem: !ok);
                if (ok && !codePanel.IsDisposed)
                {
                    codeLabel.Text = "6-digit email code";
                    codePanel.Visible = true;
                    codeBox.Focus();
                }
            }
            finally
            {
                if (!sendCodeBtn.IsDisposed) sendCodeBtn.Enabled = true;
            }
        };
        authButtons.Controls.Add(sendCodeBtn);

        if (signedIn)
        {
            var signOutBtn = Theme.Action("Sign out");
            signOutBtn.AutoSize = true;
            signOutBtn.Click += (_, _) =>
            {
                BambuUsage.SignOut();
                Say("Signed out of Bambu Cloud.");
                BeginInvoke(ShowBambu);
            };
            authButtons.Controls.Add(signOutBtn);
        }

        page.Controls.Add(authButtons);
        page.Controls.Add(codePanel);

        page.Controls.Add(Theme.Caption(
            "Bambu Lab normally sends a 6-digit verification code to your email on first sign-in. " +
            "Your token is encrypted to this Windows account with DPAPI and refreshed automatically."));

        // ---- Printer order & custom short labels ---------------------------
        page.Controls.Add(new Label
        {
            Text = "Closet printer order & bar labels (top to bottom)",
            AutoSize = true, Font = Theme.Face(11f, FontStyle.Bold),
            ForeColor = Theme.Text, Margin = new Padding(0, 8, 0, 2),
        });

        page.Controls.Add(Theme.Caption(
            "Use ↑ and ↓ to match the physical row in your closet. Each enabled printer gets one " +
            "horizontal line on the taskbar. Keep the short label brief (2–6 characters, e.g. P1–P5) " +
            "so the progress bar and time remaining have plenty of room."));

        if (bambu.Printers.Count == 0)
        {
            page.Controls.Add(new Label
            {
                Text = signedIn
                    ? "No bound printers loaded yet — click 'Check now' below to sync from Bambu Cloud."
                    : "Sign in above to load your 5 printers and arrange their order.",
                AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Face(9f),
                Margin = new Padding(0, 2, 0, 10),
            });
        }
        else
        {
            var liveMap = BambuUsage.Current?.Printers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < bambu.Printers.Count; i++)
            {
                int index = i;
                var printer = bambu.Printers[index];

                var row = new FlowLayoutPanel
                {
                    AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false, Margin = new Padding(0, 1, 0, 2),
                };

                var posLabel = new Label
                {
                    Text = $"{index + 1}.", Width = 22, AutoSize = false,
                    TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Muted,
                    Font = Theme.Face(9f, FontStyle.Bold), Margin = new Padding(0, 5, 4, 0),
                };
                row.Controls.Add(posLabel);

                var toggle = new CheckBox
                {
                    Checked = printer.Enabled, AutoSize = true, ForeColor = Theme.Text,
                    FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                    Margin = new Padding(0, 6, 4, 0),
                };
                toggle.CheckedChanged += (_, _) =>
                {
                    printer.Enabled = toggle.Checked;
                    Save();
                    BambuUsage.NotifyPrintersEdited();
                };
                row.Controls.Add(toggle);

                var upBtn = Theme.Action("↑");
                upBtn.Size = new Size(30, 26);
                upBtn.Margin = new Padding(0, 2, 3, 2);
                upBtn.Enabled = index > 0;
                upBtn.Click += (_, _) =>
                {
                    if (index <= 0 || index >= bambu.Printers.Count) return;
                    (bambu.Printers[index - 1], bambu.Printers[index]) = (bambu.Printers[index], bambu.Printers[index - 1]);
                    Save();
                    BambuUsage.NotifyPrintersEdited();
                    BeginInvoke(ShowBambu);
                };
                row.Controls.Add(upBtn);

                var downBtn = Theme.Action("↓");
                downBtn.Size = new Size(30, 26);
                downBtn.Margin = new Padding(0, 2, 8, 2);
                downBtn.Enabled = index < bambu.Printers.Count - 1;
                downBtn.Click += (_, _) =>
                {
                    if (index < 0 || index >= bambu.Printers.Count - 1) return;
                    (bambu.Printers[index + 1], bambu.Printers[index]) = (bambu.Printers[index], bambu.Printers[index + 1]);
                    Save();
                    BambuUsage.NotifyPrintersEdited();
                    BeginInvoke(ShowBambu);
                };
                row.Controls.Add(downBtn);

                var labelBox = new TextBox
                {
                    Text = printer.Label ?? "", Width = 64, BackColor = Theme.Raised, ForeColor = Theme.Text,
                    Font = Theme.Face(9f), BorderStyle = BorderStyle.FixedSingle,
                    Margin = new Padding(0, 3, 8, 2),
                    PlaceholderText = $"P{index + 1}",
                };
                labelBox.TextChanged += (_, _) =>
                {
                    printer.Label = (labelBox.Text ?? "").Trim();
                    Save();
                    BambuUsage.NotifyPrintersEdited();
                };
                row.Controls.Add(labelBox);

                string statusSummary = "";
                if (liveMap is not null && liveMap.TryGetValue(printer.Id, out var live))
                {
                    statusSummary = live.State switch
                    {
                        BambuPrinterState.Printing => $" — Printing {live.Percent}% ({live.RemainingMinutes}m left)",
                        BambuPrinterState.Preparing => $" — Preparing ({live.Percent}%)",
                        BambuPrinterState.Error => $" — ERROR ({live.ErrorText ?? "alert"})",
                        BambuPrinterState.Paused => $" — Paused ({live.Percent}%)",
                        BambuPrinterState.Finished => " — Finished (100%)",
                        BambuPrinterState.Idle => " — Idle",
                        _ => " — Offline",
                    };
                }

                string modelPart = !string.IsNullOrWhiteSpace(printer.Model) ? $" ({printer.Model})" : "";
                var infoLabel = new Label
                {
                    Text = $"{printer.Name}{modelPart}{statusSummary}",
                    AutoSize = true, ForeColor = Theme.Text, Font = Theme.Face(9f),
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 6, 0, 0),
                };
                infoLabel.Click += (_, _) =>
                {
                    if (!BambuUsage.OpenSlicer())
                        Say("Could not find Bambu Studio (or OrcaSlicer) on this machine.", problem: true);
                };
                row.Controls.Add(infoLabel);

                page.Controls.Add(row);
            }
        }

        var interval = Theme.Number(bambu.RefreshSeconds, 15, 600, 15);
        interval.ValueChanged += (_, _) => { bambu.RefreshSeconds = (int)interval.Value; Save(); };
        Row(page, "Seconds between cloud syncs", interval);

        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 10, 0, 4) };

        var check = Theme.Action("Check now", primary: signedIn);
        check.AutoSize = true;
        check.Click += (_, _) =>
        {
            BambuUsage.Refresh();
            Say("Syncing printers with Bambu Cloud…");
        };
        actions.Controls.Add(check);

        var openSlicer = Theme.Action("Open Bambu Studio");
        openSlicer.AutoSize = true;
        openSlicer.Click += (_, _) =>
        {
            if (!BambuUsage.OpenSlicer())
                Say("Could not find Bambu Studio (or OrcaSlicer) on this machine.", problem: true);
        };
        actions.Controls.Add(openSlicer);

        var diagnosis = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Visible = false,
            Width = 460, Height = 140, BackColor = Theme.Raised, ForeColor = Theme.Muted,
            Font = new Font(FontFamily.GenericMonospace, 8.5f), BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 8, 0, 8),
        };

        var test = Theme.Action("Test connection");
        test.AutoSize = true;
        test.Click += async (_, _) =>
        {
            test.Enabled = false;
            diagnosis.Visible = true;
            diagnosis.Text = "Checking Bambu Cloud & MQTT connection…";
            try
            {
                diagnosis.Text = await Task.Run(BambuUsage.SelfTest);
            }
            catch (Exception ex) { diagnosis.Text = ex.Message; }
            finally { test.Enabled = true; }
        };
        actions.Controls.Add(test);
        page.Controls.Add(actions);

        page.Controls.Add(Theme.Caption(
            "Verifies your Bambu Cloud token, lists bound printers, and reports the live MQTT state " +
            "for each printer without including any token or password."));
        page.Controls.Add(diagnosis);

        var state = new Label
        {
            AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Face(9f),
            MaximumSize = new Size(460, 0), Margin = new Padding(0, 12, 0, 8),
        };
        page.Controls.Add(state);

        int lastPrinterCount = bambu.Printers.Count;
        void Report()
        {
            state.Text = UsageStrip.BambuTip(BambuUsage.Current);
            if (bambu.Printers.Count != lastPrinterCount && !IsDisposed)
            {
                lastPrinterCount = bambu.Printers.Count;
                BeginInvoke(ShowBambu);
            }
        }
        Report();

        var watch = new System.Windows.Forms.Timer { Interval = 1500 };
        watch.Tick += (_, _) => Report();
        watch.Start();
        page.Disposed += (_, _) => { watch.Stop(); watch.Dispose(); };

        page.Controls.Add(Theme.Caption(
            "Turn the bars on per display under Taskbar ▸ Show Bambu printer status bars."));
    }

    private void ShowRemoteChats()
    {
        var page = Page("Remote server chat status",
            "Track live Claude Code and Antigravity remote control chats across your servers " +
            "(e.g. work vs. personal), showing how many chats are open, running, waiting on your " +
            "input, or recently finished.");

        var remote = config.RemoteChats;

        var on = Theme.Toggle("Track remote Claude Code & Antigravity chats", remote.Enabled);
        on.CheckedChanged += (_, _) =>
        {
            remote.Enabled = on.Checked;
            SyncTrackerToBars(remote.Enabled, b => b.ShowRemoteChats, (b, v) => b.ShowRemoteChats = v);
            Save();
            if (remote.Enabled) RemoteChatUsage.Refresh();
            BeginInvoke(ShowRemoteChats);
        };
        page.Controls.Add(on);

        if (!remote.Enabled)
        {
            page.Controls.Add(Theme.Caption(
                "While this is off, ScweenSpit does not poll Claude Code Remote Control or Antigravity Remote Control."));
            return;
        }

        page.Controls.Add(Theme.Caption(
            "Reuses your signed-in Claude OAuth / Claude Code credentials (or claude.ai session key) " +
            "and Google Cloud Code OAuth token. You can also sign in or refresh either account directly below:"));

        var authRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 2, 0, 6) };

        bool claudeSignedIn = ClaudeUsage.HasSignedInOAuth || ClaudeUsage.HasClaudeCodeCredentials() || ClaudeUsage.HasStoredSessionKey;
        var signInClaudeBtn = Theme.Action(claudeSignedIn ? "Re-sign in with Claude" : "Sign in with Claude", primary: !claudeSignedIn);
        signInClaudeBtn.AutoSize = true;
        signInClaudeBtn.Click += async (_, _) =>
        {
            signInClaudeBtn.Enabled = false;
            Say("Finish signing in to Claude in your browser…");
            try
            {
                var (ok, message) = await ClaudeUsage.SignInWithClaudeAsync();
                Say(message, problem: !ok);
                if (ok && !IsDisposed) BeginInvoke(ShowRemoteChats);
            }
            finally
            {
                if (!signInClaudeBtn.IsDisposed) signInClaudeBtn.Enabled = true;
            }
        };
        authRow.Controls.Add(signInClaudeBtn);

        bool agSignedIn = AntigravityUsage.HasSignedInAccount || AntigravityUsage.HasCliTokenFile();
        var signInAgBtn = Theme.Action(agSignedIn ? "Re-sign in with Google (Antigravity)" : "Sign in with Google (Antigravity)", primary: !agSignedIn);
        signInAgBtn.AutoSize = true;
        signInAgBtn.Click += async (_, _) =>
        {
            signInAgBtn.Enabled = false;
            Say("Finish signing in to Google in your browser…");
            try
            {
                var (ok, message) = await AntigravityUsage.SignInWithGoogleAsync();
                Say(message, problem: !ok);
                if (ok && !IsDisposed) BeginInvoke(ShowRemoteChats);
            }
            finally
            {
                if (!signInAgBtn.IsDisposed) signInAgBtn.Enabled = true;
            }
        };
        authRow.Controls.Add(signInAgBtn);
        page.Controls.Add(authRow);

        // ---- Server × service row order & custom labels ---------------------
        page.Controls.Add(new Label
        {
            Text = "Server × service rows & bar labels (top to bottom)",
            AutoSize = true, Font = Theme.Face(11f, FontStyle.Bold),
            ForeColor = Theme.Text, Margin = new Padding(0, 8, 0, 2),
        });

        page.Controls.Add(Theme.Caption(
            "Use ↑ and ↓ to order rows on the taskbar strip. Uncheck any server × service pair you don't " +
            "want displayed (for example if a server only runs Antigravity or only runs Claude). Keep " +
            "short labels brief (e.g. lp·Cl, lp·AG, hx525·AG) so status badges stay compact."));

        if (remote.Rows.Count == 0)
        {
            page.Controls.Add(new Label
            {
                Text = "No remote servers discovered yet — click 'Check now' below to scan your Claude & Antigravity remote instances.",
                AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Face(9f),
                Margin = new Padding(0, 2, 0, 10),
            });
        }
        else
        {
            var liveMap = RemoteChatUsage.Current?.Rows.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < remote.Rows.Count; i++)
            {
                int index = i;
                var rowCfg = remote.Rows[index];

                var row = new FlowLayoutPanel
                {
                    AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false, Margin = new Padding(0, 1, 0, 2),
                };

                var posLabel = new Label
                {
                    Text = $"{index + 1}.", Width = 22, AutoSize = false,
                    TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Muted,
                    Font = Theme.Face(9f, FontStyle.Bold), Margin = new Padding(0, 5, 4, 0),
                };
                row.Controls.Add(posLabel);

                var toggle = new CheckBox
                {
                    Checked = rowCfg.Enabled, AutoSize = true, ForeColor = Theme.Text,
                    FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                    Margin = new Padding(0, 6, 4, 0),
                };
                toggle.CheckedChanged += (_, _) =>
                {
                    rowCfg.Enabled = toggle.Checked;
                    Save();
                    RemoteChatUsage.NotifyRowsEdited();
                };
                row.Controls.Add(toggle);

                var upBtn = Theme.Action("↑");
                upBtn.Size = new Size(30, 26);
                upBtn.Margin = new Padding(0, 2, 3, 2);
                upBtn.Enabled = index > 0;
                upBtn.Click += (_, _) =>
                {
                    if (index <= 0 || index >= remote.Rows.Count) return;
                    (remote.Rows[index - 1], remote.Rows[index]) = (remote.Rows[index], remote.Rows[index - 1]);
                    Save();
                    RemoteChatUsage.NotifyRowsEdited();
                    BeginInvoke(ShowRemoteChats);
                };
                row.Controls.Add(upBtn);

                var downBtn = Theme.Action("↓");
                downBtn.Size = new Size(30, 26);
                downBtn.Margin = new Padding(0, 2, 8, 2);
                downBtn.Enabled = index < remote.Rows.Count - 1;
                downBtn.Click += (_, _) =>
                {
                    if (index < 0 || index >= remote.Rows.Count - 1) return;
                    (remote.Rows[index + 1], remote.Rows[index]) = (remote.Rows[index], remote.Rows[index + 1]);
                    Save();
                    RemoteChatUsage.NotifyRowsEdited();
                    BeginInvoke(ShowRemoteChats);
                };
                row.Controls.Add(downBtn);

                string defaultShort = RemoteServerRowConfig.DefaultLabel(rowCfg.Server, rowCfg.Service);
                var labelBox = new TextBox
                {
                    Text = rowCfg.Label ?? "", Width = 86, BackColor = Theme.Raised, ForeColor = Theme.Text,
                    Font = Theme.Face(9f), BorderStyle = BorderStyle.FixedSingle,
                    Margin = new Padding(0, 3, 8, 2),
                    PlaceholderText = defaultShort,
                };
                labelBox.TextChanged += (_, _) =>
                {
                    rowCfg.Label = (labelBox.Text ?? "").Trim();
                    Save();
                    RemoteChatUsage.NotifyRowsEdited();
                };
                row.Controls.Add(labelBox);

                string statusSummary = "";
                if (liveMap is not null && liveMap.TryGetValue(rowCfg.Id, out var live))
                {
                    if (!live.Online)
                        statusSummary = " — offline";
                    else if (live.TotalCount == 0)
                        statusSummary = " — 0 open chats";
                    else
                    {
                        var parts = new List<string>();
                        if (live.WaitingCount > 0) parts.Add($"{live.WaitingCount} need input");
                        if (live.RunningCount > 0) parts.Add($"{live.RunningCount} running");
                        if (live.DoneCount > 0) parts.Add($"{live.DoneCount} done/idle");
                        statusSummary = $" — {live.TotalCount} open ({string.Join(", ", parts)})";
                    }
                }

                bool isAg = string.Equals(rowCfg.Service, "Antigravity", StringComparison.OrdinalIgnoreCase);
                string svcName = isAg ? "Antigravity" : "Claude Code";
                var infoLabel = new Label
                {
                    Text = $"{rowCfg.Server} · {svcName}{statusSummary}",
                    AutoSize = true, ForeColor = Theme.Text, Font = Theme.Face(9f),
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 6, 0, 0),
                };
                infoLabel.Click += (_, _) =>
                {
                    var svc = isAg ? RemoteChatService.Antigravity : RemoteChatService.Claude;
                    RemoteChatUsage.OpenService(svc);
                };
                row.Controls.Add(infoLabel);

                page.Controls.Add(row);
            }
        }

        var doneHours = Theme.Number(remote.DoneWindowHours, 1, 168, 1);
        doneHours.ValueChanged += (_, _) => { remote.DoneWindowHours = (int)doneHours.Value; Save(); };
        Row(page, "Hours to keep idle/finished chats in live count", doneHours);

        var interval = Theme.Number(remote.RefreshSeconds, 1, 300, 1);
        interval.ValueChanged += (_, _) => { remote.RefreshSeconds = (int)interval.Value; Save(); };
        Row(page, "Seconds between remote chat checks", interval);

        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 10, 0, 4) };

        var check = Theme.Action("Check now", primary: true);
        check.AutoSize = true;
        check.Click += (_, _) =>
        {
            RemoteChatUsage.Refresh();
            Say("Checking remote Claude & Antigravity chats…");
        };
        actions.Controls.Add(check);

        var openClaude = Theme.Action("Open claude.ai/code");
        openClaude.AutoSize = true;
        openClaude.Click += (_, _) => RemoteChatUsage.OpenService(RemoteChatService.Claude);
        actions.Controls.Add(openClaude);

        var openAg = Theme.Action("Open antigravity.google");
        openAg.AutoSize = true;
        openAg.Click += (_, _) => RemoteChatUsage.OpenService(RemoteChatService.Antigravity);
        actions.Controls.Add(openAg);

        var diagnosis = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Visible = false,
            Width = 460, Height = 160, BackColor = Theme.Raised, ForeColor = Theme.Muted,
            Font = new Font(FontFamily.GenericMonospace, 8.5f), BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 8, 0, 8),
        };

        var test = Theme.Action("Test connection");
        test.AutoSize = true;
        test.Click += async (_, _) =>
        {
            test.Enabled = false;
            diagnosis.Visible = true;
            diagnosis.Text = "Querying Claude Code & Antigravity remote control endpoints…";
            try
            {
                diagnosis.Text = await Task.Run(RemoteChatUsage.SelfTest);
            }
            catch (Exception ex) { diagnosis.Text = ex.Message; }
            finally { test.Enabled = true; }
        };
        actions.Controls.Add(test);
        page.Controls.Add(actions);

        page.Controls.Add(Theme.Caption(
            "Lists discovered remote bridges/instances and every live session on each server without " +
            "exposing any OAuth token or session key."));
        page.Controls.Add(diagnosis);

        var state = new Label
        {
            AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Face(9f),
            MaximumSize = new Size(460, 0), Margin = new Padding(0, 12, 0, 8),
        };
        page.Controls.Add(state);

        int lastRowCount = remote.Rows.Count;
        void Report()
        {
            state.Text = UsageStrip.RemoteChatsTip(RemoteChatUsage.Current);
            if (remote.Rows.Count != lastRowCount && !IsDisposed)
            {
                lastRowCount = remote.Rows.Count;
                BeginInvoke(ShowRemoteChats);
            }
        }
        Report();

        var watch = new System.Windows.Forms.Timer { Interval = 1500 };
        watch.Tick += (_, _) => Report();
        watch.Start();
        page.Disposed += (_, _) => { watch.Stop(); watch.Dispose(); };

        page.Controls.Add(Theme.Caption(
            "Turn the strip on per display under Taskbar ▸ Show remote server chat status."));
    }

    private void ShowOrchestra()
    {
        var page = Page("Orchestrator",
            "Live status from an orchestrator's read-only orchestra.json feed: how long it has been " +
            "running, and how many Claude Code and Antigravity child sessions it has, by state.");

        var orchestra = config.Orchestra;

        var on = Theme.Toggle("Watch orchestrator feeds", orchestra.Enabled);
        on.CheckedChanged += (_, _) =>
        {
            orchestra.Enabled = on.Checked;
            Save();
            OrchestraUsage.Configure(orchestra);
            BeginInvoke(ShowOrchestra);
        };
        page.Controls.Add(on);

        if (!orchestra.Enabled)
        {
            page.Controls.Add(Theme.Caption("While this is off, ScweenSpit does not ask any feed."));
            return;
        }

        var showTimer = Theme.Toggle("Show elapsed running time on the bar", orchestra.ShowTimer);
        showTimer.CheckedChanged += (_, _) =>
        {
            orchestra.ShowTimer = showTimer.Checked;
            Save();
            OrchestraUsage.Configure(orchestra);
        };
        page.Controls.Add(showTimer);

        var indicator = Theme.Choice(NeedsIndicator.Choices, orchestra.NeedsIndicator);
        var decision = Theme.Caption("");
        void ShowDecision() => decision.Text =
            "When the orchestrator is waiting on you for something — an approval, a merge, a sign-in — " +
            "the strip gets a Needs row and this shows the count until it is dealt with. Taskbar is a " +
            "badged button that flashes when something arrives; Floating is a pill you can drag anywhere. " +
            "Automatic floats on a laptop-sized screen, or when the taskbar is hidden or compact." +
            (NeedsIndicator.LastDecision is { Length: > 0 } why ? $" Right now: {why}." : "");
        indicator.SelectedIndexChanged += (_, _) =>
        {
            orchestra.NeedsIndicator = (string)indicator.SelectedItem!;
            Save();
            ShowDecision();
        };
        Row(page, "Show what needs you", indicator);
        ShowDecision();
        page.Controls.Add(decision);

        var toast = Theme.Toggle("Notify me when something new needs me", orchestra.NeedsToast);
        toast.CheckedChanged += (_, _) => { orchestra.NeedsToast = toast.Checked; Save(); };
        page.Controls.Add(toast);

        var preview = Theme.Action("Preview: show a test need for 2 minutes");
        preview.AutoSize = true;
        preview.Click += (_, _) =>
        {
            OrchestraUsage.Preview(TimeSpan.FromMinutes(2));
            Say("Showing a made-up approval for 2 minutes — look at the taskbar, or for the floating pill");
        };
        page.Controls.Add(preview);

        if (orchestra.NeedsPillX is not null)
        {
            var corner = Theme.Action("Put the floating pill back by the clock");
            corner.AutoSize = true;
            corner.Click += (_, _) =>
            {
                orchestra.NeedsPillX = orchestra.NeedsPillY = null;
                Save();
                corner.Enabled = false;
            };
            page.Controls.Add(corner);
        }

        page.Controls.Add(Theme.Caption(
            "The strip appears on the bar only once a feed answers, so a machine that cannot reach " +
            "the orchestrator shows nothing. A feed that has never answered is asked every 30 seconds; " +
            "one that stops answering is dimmed after 30 seconds rather than removed."));

        var feeds = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true,
            Width = 460, Height = 72, BackColor = Theme.Raised, ForeColor = Theme.Text,
            Font = new Font(FontFamily.GenericMonospace, 9f), BorderStyle = BorderStyle.FixedSingle,
            Text = string.Join(Environment.NewLine, orchestra.Feeds),
        };
        Row(page, "Feed URLs, one per line (one per orchestrator, in bar order)", feeds);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 4) };

        var apply = Theme.Action("Save feeds", primary: true);
        apply.AutoSize = true;
        apply.Click += (_, _) =>
        {
            var lines = feeds.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var bad = lines.FirstOrDefault(l => !OrchestraUsage.IsFeedUrl(l));
            if (bad is not null)
            {
                Say($"Not an http(s) URL: {bad}", problem: true);
                return;
            }
            orchestra.Feeds = lines.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Save();
            OrchestraUsage.Configure(orchestra);
            Say(lines.Count == 0 ? "No feeds — nothing will be asked" : "Feeds saved; asking them now");
        };
        buttons.Controls.Add(apply);

        var reset = Theme.Action("Use the default feed");
        reset.AutoSize = true;
        reset.Click += (_, _) => feeds.Text = OrchestraSettings.DefaultFeed;
        buttons.Controls.Add(reset);

        var check = Theme.Action("Check now");
        check.AutoSize = true;
        check.Click += (_, _) => { OrchestraUsage.Refresh(); Say("Asking every feed now"); };
        buttons.Controls.Add(check);

        page.Controls.Add(buttons);

        var interval = Theme.Number(orchestra.RefreshSeconds, 2, 300, 1);
        interval.ValueChanged += (_, _) => { orchestra.RefreshSeconds = (int)interval.Value; Save(); };
        Row(page, "Seconds between checks (while a feed answers)", interval);

        var state = new Label
        {
            AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Face(9f),
            MaximumSize = new Size(460, 0), Margin = new Padding(0, 12, 0, 8),
        };
        page.Controls.Add(state);

        void Report()
        {
            var feedsNow = OrchestraUsage.Current?.Feeds ?? [];
            long now = Environment.TickCount64;
            state.Text = feedsNow.Count == 0
                ? "No feeds configured."
                : string.Join(Environment.NewLine + Environment.NewLine,
                              feedsNow.Where(f => f.Url != OrchestraUsage.PreviewFeedUrl)
                                      .Select(f => $"{f.Url}{Environment.NewLine}{OrchestraStrip.Describe(f, now)}"));

            // What ScweenSpit believes is waiting, so a missing indicator can be told from an empty list.
            var needsNow = OrchestraUsage.Current?.Needs ?? [];
            state.Text += Environment.NewLine + Environment.NewLine + (needsNow.Count == 0
                ? "Needs you: nothing listed."
                : "Needs you:" + string.Concat(needsNow.Select(i =>
                    $"{Environment.NewLine}  {(i.Need.Blocking ? "⚠" : "•")} {OrchestraUsage.NeedKindLabel(i.Need.Kind)}: {i.Need.Title}" +
                    (i.Done ? " (done, confirming)" : "") + $" [{i.Need.Id}]")));
            if (NeedsIndicator.LastDecision is { Length: > 0 } why)
                state.Text += $"{Environment.NewLine}Indicator: {why}.";
        }
        Report();

        var watch = new System.Windows.Forms.Timer { Interval = 1000 };
        watch.Tick += (_, _) => Report();
        watch.Start();
        page.Disposed += (_, _) => { watch.Stop(); watch.Dispose(); };

        page.Controls.Add(Theme.Caption(
            "Choose which displays draw it under Taskbar ▸ Show orchestrator status. The feed contract " +
            "is in the README, under \"Orchestrator status in the bar\"."));
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

        page.Controls.Add(new Label
        {
            Text = "Start menu", AutoSize = true, Font = Theme.Face(11f, FontStyle.Bold),
            ForeColor = Theme.Text, Margin = new Padding(0, 20, 0, 2),
        });

        // Moving another process's menu is the most undocumented thing here, and from the outside
        // every way it can fail looks the same. Say which one it was.
        var state = new Label
        {
            Text = StartMenu.Status, AutoSize = true, MaximumSize = new Size(520, 0),
            ForeColor = Theme.Muted, Font = Theme.Face(9f), Margin = new Padding(0, 0, 0, 4),
        };
        page.Controls.Add(state);

        // Live, because opening the menu takes the foreground away from this window: by the time
        // anyone is looking at the line again the attempt is long over, and a line that only
        // refreshes on arrival would still be showing the state before it.
        var watch = new System.Windows.Forms.Timer { Interval = 500 };
        watch.Tick += (_, _) =>
        {
            if (state.IsDisposed || state.FindForm() is null) { watch.Stop(); watch.Dispose(); return; }
            if (state.Text != StartMenu.Status) state.Text = StartMenu.Status;
        };
        watch.Start();

        var recheck = Theme.Action("Open the Start menu and report");
        recheck.Width = 260;
        recheck.Click += (_, _) => StartMenu.Press();
        page.Controls.Add(recheck);

        page.Controls.Add(Theme.Caption(
            "Press the Windows key, or the button above, and this line updates with what happened."));

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

        var standDown = Theme.Toggle("Stand down when the displays change to something unsaved",
                                     config.HandBackOnUnknownDisplays);
        standDown.CheckedChanged += (_, _) => { config.HandBackOnUnknownDisplays = standDown.Checked; Save(); };
        page.Controls.Add(standDown);
        page.Controls.Add(Theme.Caption(
            "Unplug a monitor and ScweenSpit hands the machine back rather than carrying on with " +
            "zones measured against a display that has gone, a bar reserving space on it, and the " +
            "Windows taskbar hidden in favour of one that is nowhere. Plug the display back in and " +
            "it picks up exactly where it left off. Nothing is thrown away either way."));

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

        // One button, not two. Finding out that an update exists and then being asked whether to
        // have it is a question with one sensible answer, and it was being asked every time.
        var check = Theme.Action("Check and update", primary: true);
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

                if (update is null)
                {
                    status.Text = $"Version {Updater.Current} is the latest.";
                    check.Enabled = true;
                    return;
                }

                status.ForeColor = Theme.Accent;
                status.Text = $"Downloading {update.Version}…";
                check.Text = "Downloading…";

                // To the log rather than the window: the window is about to be replaced along with
                // everything else, and nobody reads release notes in the second before a restart.
                if (!string.IsNullOrWhiteSpace(update.Notes))
                    Log.Write($"update {update.Version} notes: {update.Notes.Trim()}");

                var replaced = await Updater.ApplyAsync(update);

                status.Text = $"Installing {update.Version} and restarting…";

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
                status.ForeColor = Color.FromArgb(235, 140, 140);
                status.Text = ex.Message;
                check.Enabled = true;
                check.Text = "Check and update";
                Log.Write($"update failed: {ex}");
            }
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
