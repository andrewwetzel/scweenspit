using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static ScweenSpit.Native;
using Timer = System.Windows.Forms.Timer;

namespace ScweenSpit;

/// <summary>
/// Puts the Windows Start menu where the Start button is.
///
/// Windows opens it wherever it believes the taskbar to be, which is the primary display's bottom
/// edge whether or not the shell's bar is still shown there. With a bar of our own on some other
/// edge — or some other screen — the menu lands nowhere near the button that asked for it, and the
/// keyboard opens it in that same wrong place.
///
/// There is no supported way to say where it should go, so the menu is moved after the fact: its
/// window belongs to StartMenuExperienceHost and is an ordinary top-level window, whatever is drawn
/// inside it. Undocumented, so every step is conditional and failure is silent — worst case the
/// menu opens where Windows put it, which is where it would have opened anyway.
/// </summary>
public static class StartMenu
{
    /// <summary>Where the menu should go: the button, the bar it is on, and the room around them.</summary>
    public readonly record struct Anchor(Rectangle Button, Rectangle Bar, BarEdge Edge, RECT Monitor, int Gap);

    private const string HostClass = "Windows.UI.Core.CoreWindow";
    private const string HostTitle = "Start";

    /// <summary>The two shells that have hosted the menu. Anything else by that name is not it.</summary>
    private static readonly string[] Hosts = ["StartMenuExperienceHost.exe", "ShellExperienceHost.exe"];

    // The menu is shown a beat after it is asked for and then animates into place, so one shot at
    // moving it is not enough; it is nudged until it stays put.
    private const int PollMs = 16;
    private const int GiveUpMs = 2500;
    // Long enough to outlast the opening animation, which slides the menu back if it is moved too
    // early and then left alone.
    private const int HoldMs = 700;

    private static Func<Anchor?>? anchor;
    private static Timer? chase;

    private static WinEventDelegate? callback;
    private static GCHandle pin;
    private static IntPtr hookShow, hookUncloak;
    private static uint watched;

    /// <summary>Last window we identified as the menu. It outlives each opening, so it is worth
    /// keeping: finding it again means a walk of every top-level window on the desktop.</summary>
    private static IntPtr located;

    /// <summary>
    /// Start following the menu, asking <paramref name="where"/> each time it opens. Null from that
    /// means no bar wants it moved, and the menu is left where Windows put it.
    /// </summary>
    public static void Watch(Func<Anchor?> where)
    {
        anchor = where;
        EnsureWatching();
    }

    /// <summary>
    /// Re-arms the hook if the shell has restarted since it was installed. Cheap enough to call from
    /// a bar's ordinary refresh: one window lookup, and nothing at all once the process id matches.
    /// </summary>
    public static void EnsureWatching()
    {
        if (anchor is null) return;

        uint pid = HostPid();
        if (pid == 0 || pid == watched) return;

        Unhook();

        // Hooked to that one process rather than the whole desktop. EVENT_OBJECT_SHOW is one of the
        // busiest events there is — every tooltip and menu on the machine — and none of the rest of
        // it could ever be the Start menu.
        callback ??= OnShown;
        if (!pin.IsAllocated) pin = GCHandle.Alloc(callback);

        hookShow = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW,
                                   IntPtr.Zero, callback, pid, 0, WINEVENT_OUTOFCONTEXT);
        // Closing the menu cloaks its window rather than destroying it, so on every open after the
        // first there is no SHOW to see — only an uncloak.
        hookUncloak = SetWinEventHook(EVENT_OBJECT_UNCLOAKED, EVENT_OBJECT_UNCLOAKED,
                                      IntPtr.Zero, callback, pid, 0, WINEVENT_OUTOFCONTEXT);

        watched = hookShow != IntPtr.Zero || hookUncloak != IntPtr.Zero ? pid : 0;
        Log.Write($"start menu hooks: pid={pid} show=0x{hookShow:X} uncloak=0x{hookUncloak:X}");
    }

    public static void Unwatch()
    {
        anchor = null;
        Unhook();
        Stop();
    }

    /// <summary>Asks the shell for the menu, and moves it once it appears.</summary>
    public static void Press()
    {
        PressStartShortcut();

        // The hook covers the keyboard, and would cover this too — but not the very first open after
        // a cold shell, when there was no process to hook yet. This costs one timer either way.
        Follow();
    }

    private static void OnShown(IntPtr hook, uint ev, IntPtr hWnd, int idObject, int idChild,
                                uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hWnd == IntPtr.Zero) return;
        if (!IsStartWindow(hWnd)) return;
        Follow();
    }

    /// <summary>Nudges the menu towards the button until it stops moving, or until it is gone.</summary>
    private static void Follow()
    {
        // Nothing wants it moved — no bar, or none with the button. Leave it where Windows put it
        // rather than running a timer to discover that thirty times over.
        if (anchor?.Invoke() is null) return;

        Stop();

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
                if (now - began > GiveUpMs || settled != 0)
                {
                    if (settled == 0) Log.WriteOnce("start-menu-missing",
                        "start menu did not appear within the time allowed; leaving it where Windows put it");
                    Done(timer);
                }
                return;
            }

            // Asked for each time rather than captured: between one press and the next the pointer
            // may have moved to another display, and the bar there is the one to open against.
            if (anchor?.Invoke() is not { } target) { Done(timer); return; }

            if (settled == 0) settled = now;
            Place(menu, target, log: settled == now);
            if (now - settled > HoldMs) Done(timer);
        };
        timer.Start();
    }

    private static void Stop()
    {
        if (chase is { } running) Done(running);
    }

    private static void Done(Timer timer)
    {
        timer.Stop();
        timer.Dispose();
        if (ReferenceEquals(chase, timer)) chase = null;
    }

    /// <summary>The process drawing the menu, whether or not it is on screen just now.</summary>
    private static uint HostPid()
    {
        var hWnd = Locate();
        if (hWnd == IntPtr.Zero) return 0;

        GetWindowThreadProcessId(hWnd, out uint pid);
        return pid;
    }

    /// <summary>The menu's window while it is on screen, or zero.</summary>
    private static IntPtr Find()
    {
        // The remembered one first: the window outlives each opening, so it is usually still right.
        if (Shown(located)) return located;

        var byTitle = FindWindow(HostClass, HostTitle);
        if (Shown(byTitle) && IsStartWindow(byTitle)) return located = byTitle;

        // The host owns more than one window of that class and only ever shows one of them, so the
        // search is for a window that is on screen — not merely for one belonging to the host, which
        // would settle on whichever the shell happened to create first and then never see the menu.
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!Shown(h) || !IsStartWindow(h)) return true;
            found = h;
            return false;
        }, IntPtr.Zero);

        if (found != IntPtr.Zero) located = found;
        return found;
    }

    /// <summary>
    /// On screen right now. Closing the menu cloaks its window rather than destroying it, so
    /// visibility on its own would have us chasing a menu that is not there.
    /// </summary>
    private static bool Shown(IntPtr hWnd) =>
        hWnd != IntPtr.Zero && IsWindow(hWnd) && IsWindowVisible(hWnd) && !IsCloaked(hWnd);

    /// <summary>The menu's window whether it is on screen or not, cached between openings.</summary>
    private static IntPtr Locate()
    {
        if (located != IntPtr.Zero && IsWindow(located) && IsCoreWindow(located)) return located;

        var byTitle = FindWindow(HostClass, HostTitle);
        if (byTitle != IntPtr.Zero && IsStartWindow(byTitle)) return located = byTitle;

        // The caption is how the window is found on every build we have seen, but it is not ours to
        // rely on. Failing that, ask the shells that host it which window is theirs.
        located = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsStartWindow(h)) return true;
            located = h;
            return false;
        }, IntPtr.Zero);

        return located;
    }

    private static bool IsCoreWindow(IntPtr hWnd) =>
        string.Equals(ClassNameOf(hWnd), HostClass, StringComparison.Ordinal);

    /// <summary>
    /// A window of that class from anything else is not the Start menu, so the deciding evidence is
    /// which process drew it. Where that cannot be read, the caption is all there is to go on.
    /// </summary>
    private static bool IsStartWindow(IntPtr hWnd)
    {
        if (!IsCoreWindow(hWnd)) return false;

        var exe = ExecutablePath(hWnd);
        return exe.Length > 0
            ? Hosts.Any(h => exe.EndsWith(h, StringComparison.OrdinalIgnoreCase))
            : string.Equals(WindowTitle(hWnd), HostTitle, StringComparison.Ordinal);
    }

    private static void Place(IntPtr menu, Anchor at, bool log)
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
        (int x, int y) = at.Edge switch
        {
            BarEdge.Bottom => (at.Button.Left, at.Bar.Top - at.Gap - height),
            BarEdge.Top => (at.Button.Left, at.Bar.Bottom + at.Gap),
            BarEdge.Left => (at.Bar.Right + at.Gap, at.Button.Top),
            _ => (at.Bar.Left - at.Gap - width, at.Button.Top),
        };

        // A menu taller than the space above the bar must still land on this display, not the one
        // next door and not off the top of the desktop.
        x = Clamp(x, at.Monitor.Left + at.Gap, at.Monitor.Right - at.Gap - width);
        y = Clamp(y, at.Monitor.Top + at.Gap, at.Monitor.Bottom - at.Gap - height);

        if (x == painted.Left && y == painted.Top) return;

        bool moved = SetWindowPos(menu, IntPtr.Zero, x + (window.Left - painted.Left), y + (window.Top - painted.Top),
            0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        // Once a session either way. This is undocumented enough to be worth a line in the log, and
        // repetitive enough that a line per opening would bury everything else in it.
        if (!log) return;
        if (moved) Log.WriteOnce("start-menu-placed", $"start menu {painted} moved to {x},{y} at {at.Edge}");
        else Log.WriteOnce("start-menu-refused",
            $"start menu {painted} would not move to {x},{y}: SetWindowPos refused (err {Marshal.GetLastWin32Error()})");
    }

    private static void Unhook()
    {
        if (hookShow != IntPtr.Zero) UnhookWinEvent(hookShow);
        if (hookUncloak != IntPtr.Zero) UnhookWinEvent(hookUncloak);
        hookShow = hookUncloak = IntPtr.Zero;
        watched = 0;
        located = IntPtr.Zero;
    }

    /// <summary>Math.Clamp with the low bound winning, for a menu too big for the space it has.</summary>
    private static int Clamp(int value, int low, int high) => Math.Max(low, Math.Min(value, high));
}
