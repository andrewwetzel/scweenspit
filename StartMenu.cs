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
/// There is no supported way to say where it should go, so the menu is moved after the fact. It is
/// found by taking the foreground: whatever draws the menu on whichever build of Windows this is,
/// opening it moves the focus, and that is the one thing about it that cannot change. Naming the
/// process, the window class or the caption all turned out to be naming this year's implementation.
///
/// Undocumented either way, so every step is conditional and failure is silent — worst case the menu
/// opens where Windows put it, which is where it would have opened anyway.
/// </summary>
public static class StartMenu
{
    /// <summary>Where the menu should go: the button, the bar it is on, and the room around them.</summary>
    public readonly record struct Anchor(Rectangle Button, Rectangle Bar, BarEdge Edge, RECT Monitor, int Gap);

    /// <summary>
    /// Where to put the menu — and when nowhere, why not. The reason matters: "no bar is running"
    /// and "the bar does not want it" look identical from here and from the screen, and only one of
    /// them is a setting the person can change.
    /// </summary>
    public delegate Anchor? AnchorSource(out string why);

    /// <summary>
    /// The shell's hosted applications live here — StartMenuExperienceHost today, whatever replaces
    /// it tomorrow. Matching the folder rather than the file survives being replaced.
    /// </summary>
    private static readonly string SystemApps = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps");

    /// <summary>The shell shows small helper windows too. The menu is not one of them.</summary>
    private const int MinMenu = 200;

    // The menu is shown a beat after it is asked for and then animates into place, so one shot at
    // moving it is not enough; it is nudged until it stays put.
    private const int PollMs = 16;
    private const int GiveUpMs = 2500;

    /// <summary>
    /// A cap, not a plan: the menu is held in place for as long as it is open. Windows re-asserts
    /// its own position more than once while the menu animates in, and a fixed hold that ends before
    /// the last of those loses to it — silently, since by then nothing is watching.
    /// </summary>
    private const int MaxChaseMs = 30_000;

    /// <summary>Enough corrections to call it a losing fight rather than an animation settling.</summary>
    private const int MaxCorrections = 60;

    /// <summary>How long after asking a foreground change is still attributable to us.</summary>
    private const int AskedWindowMs = 2500;

    /// <summary>
    /// What happened last time the menu opened, in one line, for the Diagnostics page. This is the
    /// most undocumented thing the application does, and the difference between not finding the
    /// window, not being allowed to move it, and moving it somewhere unhelpful is invisible from
    /// the outside — but it decides which of three quite different fixes is the right one.
    /// </summary>
    public static string Status { get; private set; } = "not started";

    private static AnchorSource? anchor;
    private static Timer? chase;

    private static WinEventDelegate? callback;
    private static GCHandle pin;
    private static IntPtr hook;

    private static long askedAt;

    /// <summary>
    /// Start following the menu, asking <paramref name="where"/> each time it opens. Null from that
    /// means no bar wants it moved, and the menu is left where Windows put it.
    /// </summary>
    public static void Watch(AnchorSource where)
    {
        anchor = where;
        EnsureWatching();
    }

    /// <summary>
    /// Puts the hook up if it is not already. Cheap enough to call from a bar's ordinary refresh:
    /// it does nothing at all once installed.
    ///
    /// One hook, on foreground changes only. That is a handful of events a minute — unlike
    /// EVENT_OBJECT_SHOW, which fires for every tooltip on the machine.
    /// </summary>
    public static void EnsureWatching()
    {
        if (anchor is null || hook != IntPtr.Zero) return;

        callback ??= OnForeground;
        if (!pin.IsAllocated) pin = GCHandle.Alloc(callback);

        hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                               IntPtr.Zero, callback, 0, 0,
                               WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        Log.Write($"start menu hook: foreground=0x{hook:X}");
        Status = hook != IntPtr.Zero
            ? "watching for the menu; it has not opened yet"
            : "Windows refused the foreground hook";
    }

    public static void Unwatch()
    {
        anchor = null;
        Status = "not following the menu";
        if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        hook = IntPtr.Zero;
        Stop();
    }

    /// <summary>Asks the shell for the menu, and moves it once it appears.</summary>
    public static void Press()
    {
        // Before anything can go wrong, so that a line which never changes at all means the click
        // never arrived — not that the machinery behind it failed quietly.
        Status = "asked the shell for the menu (Ctrl+Esc)…";
        Log.Write("start menu requested");

        askedAt = Environment.TickCount64;
        PressStartShortcut();
        Follow();
    }

    private static void OnForeground(IntPtr h, uint ev, IntPtr hWnd, int idObject, int idChild,
                                     uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hWnd == IntPtr.Zero) return;

        // Either this is unmistakably the Start menu's own host, or we asked for the menu a moment
        // ago and this is what came up. Anything else taking the foreground is somebody's window and
        // none of our business.
        if (!IsStartHost(hWnd) && Environment.TickCount64 - askedAt > AskedWindowMs) return;
        if (!IsMenuWindow(hWnd)) return;

        Follow();
    }

    /// <summary>Nudges the menu towards the button until it stops moving, or until it is gone.</summary>
    private static void Follow()
    {
        // Nothing wants it moved. Leave the menu where Windows put it rather than running a timer
        // forty times over to discover that.
        if (anchor is null) { Status = "not following the menu"; return; }
        if (anchor(out string why) is null) { Status = why; return; }

        Stop();

        long began = Environment.TickCount64, settled = 0;
        int corrections = 0;
        var placed = new Point();

        var timer = new Timer { Interval = PollMs };
        chase = timer;

        timer.Tick += (_, _) =>
        {
            long now = Environment.TickCount64;
            var menu = Find();

            if (menu == IntPtr.Zero)
            {
                // Never shown, or shown and now dismissed. Either way we are done.
                if (now - began > GiveUpMs || settled != 0)
                {
                    if (settled == 0) GaveUp(); else Settled(placed, corrections, "the menu closed");
                    Done(timer);
                }
                return;
            }

            // Asked for each time rather than captured: between one press and the next the pointer
            // may have moved to another display, and the bar there is the one to open against.
            if (anchor is null)
            {
                Status = "not following the menu";
                Done(timer);
                return;
            }
            if (anchor(out string gone) is not { } target)
            {
                Status = gone;
                Done(timer);
                return;
            }

            if (settled == 0) settled = now;
            if (Place(menu, target, out placed)) corrections++;

            // Bounded on both sides. A menu left open all afternoon must not leave a timer running,
            // and a shell determined to have its own way must not be fought to a flicker.
            if (now - began > MaxChaseMs) { Settled(placed, corrections, "gave up watching"); Done(timer); }
            else if (corrections > MaxCorrections)
            {
                Settled(placed, corrections, "Windows keeps putting it back");
                Done(timer);
            }
        };
        timer.Start();
    }

    /// <summary>
    /// Says what was in front instead. Every previous guess at identifying the menu named something
    /// that turned out to be this year's implementation, so when the guess misses, the useful thing
    /// to record is what was actually there.
    /// </summary>
    private static void GaveUp()
    {
        var fg = GetForegroundWindow();
        var what = fg == IntPtr.Zero
            ? "nothing had the foreground"
            : $"the foreground was {ClassNameOf(fg)} " +
              $"from {Path.GetFileName(ExecutablePath(fg))}" +
              (GetWindowRect(fg, out var r) ? $" at {r}" : "");

        Status = $"no menu window found within {GiveUpMs}ms — {what}";
        Log.Write($"start menu not found: {what}");
    }

    /// <summary>
    /// How it ended. Whether the menu stayed put or had to be dragged back repeatedly is the whole
    /// question once the move itself succeeds, and one correction looks exactly like forty from the
    /// outside — both of them just look like a menu in the right place, or not.
    /// </summary>
    private static void Settled(Point placed, int corrections, string ending)
    {
        Status = corrections switch
        {
            0 => $"the menu opened where it was wanted, at {placed.X},{placed.Y} ({ending})",
            1 => $"moved the menu to {placed.X},{placed.Y} and it stayed ({ending})",
            _ => $"moved the menu to {placed.X},{placed.Y} {corrections} times — " +
                 $"Windows keeps moving it back ({ending})",
        };
        Log.WriteOnce($"start-menu-settled-{Math.Min(corrections, 2)}", $"start menu: {Status}");
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

    /// <summary>The menu's window while it is on screen, or zero.</summary>
    private static IntPtr Find()
    {
        // Opening the menu takes the foreground, whatever draws it. That holds for the whole time it
        // is open, so there is no window to remember between one tick and the next.
        var fg = GetForegroundWindow();
        return IsMenuWindow(fg) ? fg : IntPtr.Zero;
    }

    /// <summary>Unmistakably the menu's host, rather than merely a shell window.</summary>
    private static bool IsStartHost(IntPtr hWnd) =>
        Path.GetFileName(ExecutablePath(hWnd)).StartsWith("StartMenu", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Something the shell is showing, big enough to be the menu. Deliberately not a window class or
    /// a process name: both have already changed once under this code.
    /// </summary>
    private static bool IsMenuWindow(IntPtr hWnd)
    {
        if (!Shown(hWnd)) return false;
        if (!GetWindowRect(hWnd, out var r) || r.Width < MinMenu || r.Height < MinMenu) return false;

        var exe = ExecutablePath(hWnd);
        return exe.Length > 0 && exe.StartsWith(SystemApps, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// On screen right now. Closing the menu cloaks its window rather than destroying it, so
    /// visibility on its own would have us chasing a menu that is not there.
    /// </summary>
    private static bool Shown(IntPtr hWnd) =>
        hWnd != IntPtr.Zero && IsWindow(hWnd) && IsWindowVisible(hWnd) && !IsCloaked(hWnd);

    /// <summary>Puts the menu where the anchor wants it. True when it actually had to be moved.</summary>
    private static bool Place(IntPtr menu, Anchor at, out Point placed)
    {
        placed = new Point();
        if (!GetWindowRect(menu, out var window)) return false;

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

        placed = new Point(x, y);
        if (x == painted.Left && y == painted.Top) return false;

        if (SetWindowPos(menu, IntPtr.Zero, x + (window.Left - painted.Left), y + (window.Top - painted.Top),
                         0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))
        {
            Log.WriteOnce("start-menu-placed",
                $"start menu {painted} moved to {x},{y} at {at.Edge}, button {at.Button}");
            return true;
        }

        int err = Marshal.GetLastWin32Error();
        Status = $"Windows refused to move the menu from {painted.Left},{painted.Top} to {x},{y} (error {err})";
        Log.WriteOnce("start-menu-refused",
            $"start menu {painted} would not move to {x},{y}: SetWindowPos refused (err {err})");
        return false;
    }

    /// <summary>Math.Clamp with the low bound winning, for a menu too big for the space it has.</summary>
    private static int Clamp(int value, int low, int high) => Math.Max(low, Math.Min(value, high));
}
