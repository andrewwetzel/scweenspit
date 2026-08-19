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

    private readonly Panel content = new() { Dock = DockStyle.Fill, BackColor = Theme.Window, Padding = new Padding(28, 24, 28, 24), AutoScroll = true };
    private readonly FlowLayoutPanel nav = new() { Dock = DockStyle.Left, Width = 172, BackColor = Theme.Panel, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12, 20, 12, 12), WrapContents = false };
    private readonly List<Button> navButtons = [];

    private ListBox? excludeList;

    public SettingsForm(SplitConfig config, ZoneManager zones, ZoneOverlay overlay,
                        Action applyChanges, Action reloadFromDisk, Func<bool> hooksUp)
    {
        this.reloadFromDisk = reloadFromDisk;
        this.config = config;
        this.zones = zones;
        this.overlay = overlay;
        this.applyChanges = applyChanges;
        this.hooksUp = hooksUp;

        Text = "ScweenSpit";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 600);
        Size = new Size(880, 640);
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Font = Theme.Face();

        Controls.Add(content);
        Controls.Add(nav);

        AddNav("General", ShowGeneral);
        AddNav("Layouts", ShowLayouts);
        AddNav("Taskbar", ShowTaskbar);
        AddNav("Exclusions", ShowExclusions);
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
        BringToFront();
        Activate();
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
        nav.Controls.Add(b);
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
        content.Controls.Clear();
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
                Text = $"{geo.Device.TrimStart('\\', '.')}   ·   {geo.Bounds.Width}×{geo.Bounds.Height}   ·   {rects.Count} zones",
                AutoSize = true, ForeColor = Theme.Text, Font = Theme.Face(11f, FontStyle.Bold),
                Margin = new Padding(0, 14, 0, 6),
            });

            var presets = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Width = 560, Margin = new Padding(0, 0, 0, 4) };
            var current = config.ZonesFor(geo.Device);

            foreach (var (name, make) in SplitConfig.Presets)
            {
                bool active = SameZones(current, make());
                var b = Theme.Action(name, active);
                b.Width = 130;
                b.Click += (_, _) =>
                {
                    config.SetZones(geo.Device, make());
                    applyChanges();
                    overlay.Flash(zones);
                    ShowLayouts();
                };
                presets.Controls.Add(b);
            }
            page.Controls.Add(presets);

            var edit = Theme.Action("Drag dividers on screen…");
            edit.Width = 200;
            edit.Click += (_, _) => { Hide(); overlay.Show(zones, OverlayMode.Edit); };
            page.Controls.Add(edit);

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

        var show = Theme.Action("Show all zones (Win+Alt+Z)");
        show.Width = 220;
        show.Click += (_, _) => overlay.Flash(zones, 2500);
        page.Controls.Add(show);
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

        if (!Taskbar.CanMove)
        {
            page.Controls.Add(new Label
            {
                Text = "⚠  Windows 11 removed taskbar repositioning. The buttons below still write the " +
                       "setting, but Explorer ignores it and the bar stays at the bottom. Nothing here " +
                       "will break — it simply will not move.",
                AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Color.FromArgb(235, 185, 110),
                Font = Theme.Face(9f), Margin = new Padding(0, 0, 0, 12),
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
            "any open File Explorer windows. Per-display reserved space lives on the Layouts page."));
    }

    private void MoveTaskbar(TaskbarEdge edge)
    {
        var answer = MessageBox.Show(
            $"Move the taskbar to the {edge.ToString().ToLowerInvariant()} edge?\n\n" +
            "This restarts Windows Explorer: the desktop will blank for a moment and open File " +
            "Explorer windows will close." +
            (Taskbar.CanMove ? "" : "\n\nOn Windows 11 this is very likely to have no effect."),
            "ScweenSpit", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);

        if (answer != DialogResult.OK) return;

        if (!Taskbar.Move(edge))
            MessageBox.Show("Could not write the taskbar setting — see the log.", "ScweenSpit",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        ShowTaskbar();
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
        reload.Click += (_, _) => { reloadFromDisk(); ShowDiagnostics(); };
        page.Controls.Add(reload);
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
