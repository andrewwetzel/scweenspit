using System.Diagnostics;
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
/// edge whether or not the shell's bar is still shown there. With a bar of our own somewhere else,
/// the menu lands nowhere near the button that asked for it, and the keyboard opens it in that same
/// wrong place.
///
/// There is no supported way to say where it should go, so the menu is moved after the fact. The
/// window is identified by the process that drew it rather than by its class or its caption: the
/// menu has been rebuilt more than once and those change with it, while the shell that hosts it has
/// been StartMenuExperienceHost throughout. Undocumented either way, so every step is conditional
/// and failure is silent — worst case the menu opens where Windows put it, which is where it would
/// have opened anyway.
/// </summary>
public static class StartMenu
{
    /// <summary>Where the menu should go: the button, the bar it is on, and the room around them.</summary>
    public readonly record struct Anchor(Rectangle Button, Rectangle Bar, BarEdge Edge, RECT Monitor, int Gap);

    /// <summary>The shells that have drawn the menu, newest first.</summary>
    private static readonly string[] Hosts = ["StartMenuExperienceHost", "ShellExperienceHost"];

    /// <summary>The host owns small helper windows too. The menu is not one of them.</summary>
    private const int MinMenu = 200;

    // The menu is shown a beat after it is asked for and then animates into place, so one shot at
    // moving it is not enough; it is nudged until it stays put.
    private const int PollMs = 16;
    private const int GiveUpMs = 2500;

    /// <summary>Long enough to outlast the opening animation, which slides the menu back if it is
    /// moved too early and then left alone.</summary>
    private const int HoldMs = 700;

    private static Func<Anchor?>? anchor;
    private static Timer? chase;

    private static WinEventDelegate? callback;
    private static GCHandle pin;
    private static IntPtr hookShow, hookUncloak;

    /// <summary>
    /// What happened last time the menu opened, in one line, for the Diagnostics page. This is the
    /// most undocumented thing the application does, and the difference between not finding the
    /// window, not being allowed to move it, and moving it somewhere unhelpful is invisible from
    /// the outside — but it decides which of three quite different fixes is the right one.
    /// </summary>
    public static string Status { get; private set; } = "not started";

    private static uint watched;
    /// <summary>Any window of the watched process, purely to notice when the shell has restarted.</summary>
    private static IntPtr hostAny;
    /// <summary>Last window identified as the menu, so a chase is not an enumeration per tick.</summary>
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
    /// a bar's ordinary refresh: one liveness check, and nothing at all while that holds.
    /// </summary>
    public static void EnsureWatching()
    {
        if (anchor is null) return;
        if (watched != 0 && hostAny != IntPtr.Zero && IsWindow(hostAny)) return;

        uint pid = HostPid();
        if (pid == 0)
        {
            Status = "no Start menu host process is running";
            Log.WriteOnce("start-menu-host-missing",
                $"no Start menu host running ({string.Join(", ", Hosts)}); leaving the menu alone");
            return;
        }

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
        hostAny = watched != 0 ? AnyWindowOf(pid) : IntPtr.Zero;
        Log.Write($"start menu hooks: pid={pid} show=0x{hookShow:X} uncloak=0x{hookUncloak:X}");

        Status = watched != 0
            ? $"watching host {pid}; the menu has not opened yet"
            : $"found host {pid} but Windows refused the hook";
    }

    public static void Unwatch()
    {
        anchor = null;
        Status = "not following the menu";
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
        Follow();
    }

    /// <summary>Nudges the menu towards the button until it stops moving, or until it is gone.</summary>
    private static void Follow()
    {
        // Nothing wants it moved — no bar, or none with the button. Leave it where Windows put it
        // rather than running a timer to discover that forty times over.
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
                    if (settled == 0)
                    {
                        Status = $"the menu never appeared within {GiveUpMs}ms (host {watched})";
                        Log.WriteOnce("start-menu-missing",
                            $"start menu did not appear within {GiveUpMs}ms of being asked for " +
                            $"(host pid {watched}); leaving it where Windows put it");
                    }
                    Done(timer);
                }
                return;
            }

            // Asked for each time rather than captured: between one press and the next the pointer
            // may have moved to another display, and the bar there is the one to open against.
            if (anchor?.Invoke() is not { } target)
            {
                Status = "no bar is asking for the menu";
                Done(timer);
                return;
            }

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
        foreach (var name in Hosts)
        {
            var running = Process.GetProcessesByName(name);
            try { if (running.Length > 0) return (uint)running[0].Id; }
            catch (InvalidOperationException) { /* exited between the listing and the read */ }
            finally { foreach (var p in running) p.Dispose(); }
        }
        return 0;
    }

    /// <summary>The menu's window while it is on screen, or zero.</summary>
    private static IntPtr Find()
    {
        uint pid = watched != 0 ? watched : HostPid();
        if (pid == 0) return IntPtr.Zero;

        // The remembered one first: a chase asks sixty times a second, and the window survives from
        // one opening to the next.
        if (Shown(located) && OwnedBy(located, pid)) return located;

        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!OwnedBy(h, pid) || !Shown(h)) return true;

            // The host owns more than one window and shows only one of them at a time; a stray small
            // one would otherwise be dragged around the screen in the menu's place.
            if (!GetWindowRect(h, out var r) || r.Width < MinMenu || r.Height < MinMenu) return true;

            found = h;
            return false;
        }, IntPtr.Zero);

        if (found != IntPtr.Zero)
        {
            located = found;
            Log.WriteOnce("start-menu-window",
                $"start menu window found: class={ClassNameOf(found)} title='{WindowTitle(found)}'");
        }
        return found;
    }

    /// <summary>Any top-level window of that process, as a handle on whether it is still alive.</summary>
    private static IntPtr AnyWindowOf(uint pid)
    {
        IntPtr any = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!OwnedBy(h, pid)) return true;
            any = h;
            return false;
        }, IntPtr.Zero);
        return any;
    }

    private static bool OwnedBy(IntPtr hWnd, uint pid)
    {
        GetWindowThreadProcessId(hWnd, out uint owner);
        return owner == pid;
    }

    /// <summary>
    /// On screen right now. Closing the menu cloaks its window rather than destroying it, so
    /// visibility on its own would have us chasing a menu that is not there.
    /// </summary>
    private static bool Shown(IntPtr hWnd) =>
        hWnd != IntPtr.Zero && IsWindow(hWnd) && IsWindowVisible(hWnd) && !IsCloaked(hWnd);

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

        if (x == painted.Left && y == painted.Top)
        {
            if (log) Status = $"the menu opened where it was wanted, at {x},{y}";
            return;
        }

        bool moved = SetWindowPos(menu, IntPtr.Zero, x + (window.Left - painted.Left), y + (window.Top - painted.Top),
            0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        // Once a session either way. This is undocumented enough to be worth a line in the log, and
        // repetitive enough that a line per opening would bury everything else in it.
        if (!log) return;

        if (moved)
        {
            Status = $"moved the menu from {painted.Left},{painted.Top} to {x},{y} " +
                     $"({painted.Width}x{painted.Height}, button at {at.Button.Left},{at.Button.Top}, {at.Edge})";
            Log.WriteOnce("start-menu-placed", $"start menu {painted} moved to {x},{y} at {at.Edge}");
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            Status = $"Windows refused to move the menu from {painted.Left},{painted.Top} to {x},{y} (error {err})";
            Log.WriteOnce("start-menu-refused",
                $"start menu {painted} would not move to {x},{y}: SetWindowPos refused (err {err})");
        }
    }

    private static void Unhook()
    {
        if (hookShow != IntPtr.Zero) UnhookWinEvent(hookShow);
        if (hookUncloak != IntPtr.Zero) UnhookWinEvent(hookUncloak);
        hookShow = hookUncloak = IntPtr.Zero;
        watched = 0;
        hostAny = located = IntPtr.Zero;
    }

    /// <summary>Math.Clamp with the low bound winning, for a menu too big for the space it has.</summary>
    private static int Clamp(int value, int low, int high) => Math.Max(low, Math.Min(value, high));
}
