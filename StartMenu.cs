using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;
using static ScweenSpit.Native;

namespace ScweenSpit;

/// <summary>
/// Opens the Windows Start menu and brings it over to our own Start button.
///
/// Windows puts the menu wherever it believes the taskbar to be, which is the primary display's
/// bottom edge whether or not the shell's bar is still shown there. With a bar of our own on some
/// other edge — or some other screen — the menu lands nowhere near the button that asked for it.
///
/// There is no supported way to say where it should go, so the menu is moved after the fact: its
/// window belongs to StartMenuExperienceHost and is an ordinary top-level window, whatever is drawn
/// inside it. Undocumented, so every step is conditional and failure is silent — worst case the
/// menu opens where Windows put it, which is where it would have opened anyway.
/// </summary>
public static class StartMenu
{
    private const string HostClass = "Windows.UI.Core.CoreWindow";
    private const string HostTitle = "Start";

    /// <summary>The two shells that have hosted the menu. Anything else by that name is not it.</summary>
    private static readonly string[] Hosts = ["StartMenuExperienceHost.exe", "ShellExperienceHost.exe"];

    // The menu is shown a beat after the keystroke and then animates into place, so one shot at
    // moving it is not enough; it is nudged until it stays put. Short enough that a keystroke the
    // shell ignored does not leave a timer running.
    private const int PollMs = 16;
    private const int GiveUpMs = 900;
    private const int HoldMs = 400;

    private static Timer? chase;

    /// <summary>
    /// Asks for the menu and, if <paramref name="reposition"/>, walks it over to the button.
    /// </summary>
    /// <param name="anchor">The Start button, in screen pixels.</param>
    /// <param name="bar">The bar the button is on, in screen pixels.</param>
    public static void Open(Rectangle anchor, Rectangle bar, BarEdge edge, RECT monitor, int gap, bool reposition)
    {
        PressStartShortcut();
        if (!reposition) return;

        chase?.Stop();
        chase?.Dispose();

        long began = Environment.TickCount64, settled = 0;
        var timer = new Timer { Interval = PollMs };
        chase = timer;

        timer.Tick += (_, _) =>
        {
            long now = Environment.TickCount64;
            var menu = Find();

            if (menu == IntPtr.Zero)
            {
                // Never shown, or shown and already dismissed. Either way we are done.
                if (now - began > GiveUpMs || settled != 0) Done(timer);
                return;
            }

            if (settled == 0) settled = now;
            Place(menu, anchor, bar, edge, monitor, gap);
            if (now - settled > HoldMs) Done(timer);
        };
        timer.Start();
    }

    private static void Done(Timer timer)
    {
        timer.Stop();
        timer.Dispose();
        if (ReferenceEquals(chase, timer)) chase = null;
    }

    /// <summary>The menu's window, or zero while it is not on screen.</summary>
    private static IntPtr Find()
    {
        var hWnd = FindWindow(HostClass, HostTitle);
        if (hWnd == IntPtr.Zero) return IntPtr.Zero;

        // The window outlives the menu: closing it cloaks the window rather than destroying it, so
        // visibility alone would have us chasing a menu that is not there.
        if (!IsWindowVisible(hWnd) || IsCloaked(hWnd)) return IntPtr.Zero;

        // A window of that class and title from anything else is not the Start menu. An unreadable
        // path is not evidence either way, so it is not held against it.
        var exe = ExecutablePath(hWnd);
        if (exe.Length > 0 && !Hosts.Any(h => exe.EndsWith(h, StringComparison.OrdinalIgnoreCase)))
            return IntPtr.Zero;

        return hWnd;
    }

    private static void Place(IntPtr menu, Rectangle anchor, Rectangle bar, BarEdge edge, RECT monitor, int gap)
    {
        if (!GetWindowRect(menu, out var window)) return;

        // The menu's window is larger than the menu: it carries a drop shadow outside its frame. Line
        // up what is painted, then move the window by the difference between the two.
        var painted = window;
        if (DwmGetWindowRect(menu, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) == 0
            && frame.Width > 0 && frame.Height > 0)
            painted = frame;

        int width = painted.Width, height = painted.Height;

        // Along the bar, the menu starts where the button does. Away from it, the menu sits just
        // clear of the bar, on the side the desktop is.
        (int x, int y) = edge switch
        {
            BarEdge.Bottom => (anchor.Left, bar.Top - gap - height),
            BarEdge.Top => (anchor.Left, bar.Bottom + gap),
            BarEdge.Left => (bar.Right + gap, anchor.Top),
            _ => (bar.Left - gap - width, anchor.Top),
        };

        // A menu taller than the space above the bar must still land on this display, not the one
        // next door and not off the top of the desktop.
        x = Clamp(x, monitor.Left + gap, monitor.Right - gap - width);
        y = Clamp(y, monitor.Top + gap, monitor.Bottom - gap - height);

        if (x == painted.Left && y == painted.Top) return;

        SetWindowPos(menu, IntPtr.Zero, x + (window.Left - painted.Left), y + (window.Top - painted.Top),
            0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Math.Clamp with the low bound winning, for a menu too big for the space it has.</summary>
    private static int Clamp(int value, int low, int high) => Math.Max(low, Math.Min(value, high));
}
