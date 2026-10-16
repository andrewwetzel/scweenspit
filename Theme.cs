using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScweenSpit;

/// <summary>One place for the dark palette and the control styling, so the window stays coherent.</summary>
internal static class Theme
{
    public static readonly Color Window  = Color.FromArgb(22, 24, 29);
    public static readonly Color Panel   = Color.FromArgb(30, 33, 40);
    public static readonly Color Raised  = Color.FromArgb(38, 42, 51);
    public static readonly Color Accent  = Color.FromArgb(74, 158, 255);
    public static readonly Color Text    = Color.FromArgb(230, 232, 236);
    public static readonly Color Muted   = Color.FromArgb(150, 157, 170);
    public static readonly Color Divider = Color.FromArgb(48, 52, 62);

    private static readonly HashSet<ContextMenuStrip> wiredMenus = [];
    private static readonly List<ContextMenuStrip> openMenus = [];
    private static readonly System.Windows.Forms.Timer menuWatch = new() { Interval = 15 };
    private static readonly Native.LowLevelMouseProc mouseHookProc = OnMouseHook;
    private static IntPtr mouseHook = IntPtr.Zero;
    private static bool prevLeft, prevRight, prevMiddle, prevEsc, prevWin;
    private static long lastOutsideDismissTick;
    private static string? lastOutsideDismissTag;
    private static Point lastOutsideDismissPoint;

    static Theme()
    {
        menuWatch.Tick += (_, _) => OnMenuWatchTick();
    }

    public static Font Face(float size = 9.5f, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", size, style);

    public static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Text, Font = Face(15f, FontStyle.Bold),
        Margin = new Padding(0, 0, 0, 2),
    };

    public static Label Caption(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Muted, Font = Face(9f),
        MaximumSize = new Size(430, 0), Margin = new Padding(0, 0, 0, 14),
    };

    public static CheckBox Toggle(string text, bool value, string? hint = null) => new()
    {
        Text = text, Checked = value, AutoSize = true, ForeColor = Text, Font = Face(),
        FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 4, 0, hint is null ? 8 : 0),
        Cursor = Cursors.Hand,
    };

    public static Button Action(string text, bool primary = false)
    {
        var b = new Button
        {
            Text = text, AutoSize = false, Size = new Size(170, 32), Font = Face(),
            FlatStyle = FlatStyle.Flat, ForeColor = primary ? Color.White : Text,
            BackColor = primary ? Accent : Raised, Cursor = Cursors.Hand,
            Margin = new Padding(0, 4, 8, 4), TextAlign = ContentAlignment.MiddleCenter,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Accent, 0.15f) : Divider;
        return b;
    }

    public static ComboBox Choice(string[] items, string selected)
    {
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            BackColor = Raised, ForeColor = Text, Font = Face(), Width = 150,
            Margin = new Padding(0, 2, 0, 10),
        };
        c.Items.AddRange(items);
        c.SelectedItem = items.FirstOrDefault(i => i.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        return c;
    }

    public static NumericUpDown Number(int value, int min, int max, int step = 1) => new()
    {
        Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Increment = step,
        BackColor = Raised, ForeColor = Text, Font = Face(), Width = 100,
        BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 2, 0, 10),
    };

    private static Icon? appIcon;

    /// <summary>
    /// A menu in the same palette as everything else. Windows renders a stock ContextMenuStrip in
    /// system light grey whatever the surface it opens from, which on a dark bar reads as a bug.
    /// </summary>
    public static ContextMenuStrip Menu(string? tag = null)
    {
        var menu = Dress(new ContextMenuStrip { Tag = tag });

        // A menu that outlives its click is a handle held for the life of the process, once per
        // right-click. Nothing refers to it afterwards — but not from inside Closed, which is still
        // running through the menu it would be disposing.
        menu.Closed += (_, _) =>
        {
            if (menu.IsHandleCreated) menu.BeginInvoke(menu.Dispose);
            else menu.Dispose();
        };
        return menu;
    }

    /// <summary>
    /// Puts a menu into the same palette. Submenus too: a drop-down takes its renderer from the
    /// ToolStripManager rather than from the menu it hangs off, so dressing the parent alone leaves
    /// every submenu rendering in system light grey.
    ///
    /// The image margin is deliberately left on. It is the gutter check marks are drawn in, and
    /// turning it off silently makes every ticked item look unticked.
    /// </summary>
    public static T Dress<T>(T menu) where T : ToolStripDropDown
    {
        menu.BackColor = Panel;
        menu.ForeColor = Text;
        menu.Font = Face();
        menu.Renderer = new ToolStripProfessionalRenderer(new MenuColours()) { RoundedEdges = false };

        if (menu is ContextMenuStrip root && wiredMenus.Add(root))
        {
            root.Opened += (_, _) => OnMenuOpened(root);
            root.Closed += (_, _) => OnMenuClosed(root);
            root.Disposed += (_, _) =>
            {
                wiredMenus.Remove(root);
                OnMenuClosed(root);
            };
        }

        return menu;
    }

    private static void OnMenuOpened(ContextMenuStrip root)
    {
        // Close any other menu that was already open so two popup menus never stack.
        foreach (var existing in openMenus.ToArray())
        {
            if (!ReferenceEquals(existing, root))
            {
                openMenus.Remove(existing);
                try { existing.Close(ToolStripDropDownCloseReason.AppClicked); } catch { }
            }
        }

        if (!openMenus.Contains(root))
            openMenus.Add(root);

        prevLeft = Native.IsKeyDown(Native.VK_LBUTTON);
        prevRight = Native.IsKeyDown(Native.VK_RBUTTON);
        prevMiddle = Native.IsKeyDown(Native.VK_MBUTTON);
        prevEsc = Native.IsKeyDown(Native.VK_ESCAPE);
        prevWin = Native.IsKeyDown(Native.VK_LWIN) || Native.IsKeyDown(Native.VK_RWIN);

        if (mouseHook == IntPtr.Zero)
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseHookProc, IntPtr.Zero, 0);

        if (!menuWatch.Enabled)
            menuWatch.Start();
    }

    private static void OnMenuClosed(ContextMenuStrip root)
    {
        openMenus.Remove(root);
        if (openMenus.Count == 0)
            StopWatchingMenus();
    }

    private static void StopWatchingMenus()
    {
        menuWatch.Stop();
        if (mouseHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(mouseHook);
            mouseHook = IntPtr.Zero;
        }
    }

    private static IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var hook = mouseHook;
        if (nCode >= 0 && openMenus.Count > 0)
        {
            int msg = wParam.ToInt32();
            if (msg is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN or Native.WM_XBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                DismissOpenMenusOutside(new Point(info.pt.X, info.pt.Y));
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    private static void OnMenuWatchTick()
    {
        openMenus.RemoveAll(m => m.IsDisposed || !m.Visible);
        if (openMenus.Count == 0)
        {
            StopWatchingMenus();
            return;
        }

        bool left = Native.IsKeyDown(Native.VK_LBUTTON);
        bool right = Native.IsKeyDown(Native.VK_RBUTTON);
        bool middle = Native.IsKeyDown(Native.VK_MBUTTON);
        bool esc = Native.IsKeyDown(Native.VK_ESCAPE);
        bool win = Native.IsKeyDown(Native.VK_LWIN) || Native.IsKeyDown(Native.VK_RWIN);

        bool escPressed = esc && !prevEsc;
        bool winPressed = win && !prevWin;
        bool mousePressed = (left && !prevLeft) || (right && !prevRight) || (middle && !prevMiddle);

        prevLeft = left;
        prevRight = right;
        prevMiddle = middle;
        prevEsc = esc;
        prevWin = win;

        if (escPressed || winPressed)
        {
            foreach (var menu in openMenus.ToArray())
            {
                openMenus.Remove(menu);
                try { menu.Close(ToolStripDropDownCloseReason.Keyboard); } catch { }
            }
            StopWatchingMenus();
            return;
        }

        if (mousePressed)
            DismissOpenMenusOutside(Cursor.Position);
    }

    /// <summary>
    /// Closes any open <see cref="ContextMenuStrip"/> when <paramref name="screenPt"/> lies outside
    /// the menu and all of its currently open submenus.
    /// </summary>
    public static bool DismissOpenMenusOutside(Point screenPt)
    {
        bool dismissedAny = false;
        foreach (var menu in openMenus.ToArray())
        {
            if (menu.IsDisposed || !menu.Visible)
            {
                openMenus.Remove(menu);
                continue;
            }

            if (!IsPointInsideMenuTree(menu, screenPt))
            {
                lastOutsideDismissTick = Environment.TickCount64;
                lastOutsideDismissTag = menu.Tag as string;
                lastOutsideDismissPoint = screenPt;
                openMenus.Remove(menu);
                dismissedAny = true;
                try { menu.Close(ToolStripDropDownCloseReason.AppClicked); } catch { }
            }
        }

        if (openMenus.Count == 0)
            StopWatchingMenus();

        return dismissedAny;
    }

    /// <summary>
    /// True when the menu identified by <paramref name="tag"/> was just closed by the very click at
    /// <paramref name="screenPt"/>, so clicking a taskbar button to dismiss its open menu does not
    /// immediately pop the same menu back open.
    /// </summary>
    public static bool WasJustDismissedAt(string tag, Point screenPt)
    {
        if (!string.Equals(lastOutsideDismissTag, tag, StringComparison.Ordinal))
            return false;

        long elapsed = Environment.TickCount64 - lastOutsideDismissTick;
        if (elapsed <= 120
            && Math.Abs(screenPt.X - lastOutsideDismissPoint.X) <= 8
            && Math.Abs(screenPt.Y - lastOutsideDismissPoint.Y) <= 8)
        {
            lastOutsideDismissTag = null;
            return true;
        }

        return false;
    }

    private static bool IsPointInsideMenuTree(ToolStripDropDown menu, Point screenPt)
    {
        if (menu.IsDisposed || !menu.Visible) return false;
        if (ContainsScreenPoint(menu, screenPt)) return true;

        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripDropDownItem dd && dd.HasDropDownItems && dd.DropDown is { Visible: true } sub)
            {
                if (IsPointInsideMenuTree(sub, screenPt))
                    return true;
            }
        }

        return false;
    }

    private static bool ContainsScreenPoint(ToolStripDropDown dropDown, Point screenPt)
    {
        if (dropDown.Bounds.Contains(screenPt)) return true;
        if (dropDown.IsHandleCreated && Native.GetWindowRect(dropDown.Handle, out var r))
        {
            if (screenPt.X >= r.Left && screenPt.X <= r.Right && screenPt.Y >= r.Top && screenPt.Y <= r.Bottom)
                return true;
        }
        return false;
    }

    private sealed class MenuColours : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Panel;
        public override Color MenuItemSelected => Raised;
        public override Color MenuItemSelectedGradientBegin => Raised;
        public override Color MenuItemSelectedGradientEnd => Raised;
        public override Color MenuItemBorder => Raised;
        public override Color MenuBorder => Divider;
        public override Color SeparatorDark => Divider;
        public override Color SeparatorLight => Divider;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
    }

    public static Icon AppIcon()
    {
        if (appIcon is not null) return appIcon;

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.FillRectangle(Brushes.DodgerBlue, 2, 6, 19, 20);
            g.FillRectangle(Brushes.Gainsboro, 23, 6, 7, 20);
        }

        return appIcon = Icon.FromHandle(bmp.GetHicon());   // process-lifetime handle
    }
    /// <summary>
    /// Asks DWM for a dark title bar so the frame matches the content. The attribute number changed
    /// during Windows 10: builds before 18985 use 19, later ones 20. Getting it wrong leaves a white
    /// caption bar on a near-black window, silently.
    /// </summary>
    public static void DarkTitleBar(IntPtr handle)
    {
        int attribute = Environment.OSVersion.Version.Build >= 18985
            ? Native.DWMWA_USE_IMMERSIVE_DARK_MODE
            : Native.DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1;

        int on = 1;
        int hr = Native.DwmSetWindowAttribute(handle, attribute, ref on, sizeof(int));
        if (hr != 0) Log.Write($"dark title bar unavailable (attr {attribute}, hr 0x{hr:X})");
    }
}
