using Microsoft.Win32;
using static ScweenSpit.Native;

namespace ScweenSpit;

public enum TaskbarEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

/// <summary>
/// Reads and relocates the Windows taskbar.
///
/// Reading is a supported API. Moving is not: there is no public API for it, so the edge is written
/// into Explorer's own StuckRects3 blob and Explorer is restarted to pick it up. On Windows 11
/// Microsoft removed taskbar repositioning entirely, and Explorer simply ignores the value — see
/// <see cref="CanMove"/>.
///
/// Only the PRIMARY display's taskbar is affected. Secondary-display bars live under MMStuckRects3,
/// keyed per monitor in an undocumented blob, and are deliberately left alone.
/// </summary>
public static class Taskbar
{
    private const string StuckRects = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3";
    private const int EdgeOffset = 12;   // byte 12 of the Settings blob holds the docked edge

    /// <summary>Windows 11 (build 22000+) dropped support for anything but the bottom edge.</summary>
    public static bool CanMove => Environment.OSVersion.Version.Build < 22000;

    /// <summary>
    /// Whether the taskbar hides itself until you reach for it. Unlike moving it, this is a
    /// supported API, takes effect immediately, and still works on Windows 11 — which makes it the
    /// practical way to get the screen back when the bar cannot be relocated.
    /// </summary>
    public static bool AutoHide
    {
        get
        {
            var data = new APPBARDATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>() };
            return (SHAppBarMessage(ABM_GETSTATE, ref data).ToInt64() & ABS_AUTOHIDE) != 0;
        }
        set
        {
            // ABM_SETSTATE replaces the whole state word, so always-on-top has to be carried over
            // rather than assumed - dropping it would quietly un-pin the taskbar.
            var current = new APPBARDATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>() };
            long state = SHAppBarMessage(ABM_GETSTATE, ref current).ToInt64();

            long wanted = value ? state | ABS_AUTOHIDE : state & ~(long)ABS_AUTOHIDE;

            var data = new APPBARDATA
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(),
                lParam = (IntPtr)wanted,
            };
            SHAppBarMessage(ABM_SETSTATE, ref data);
            Log.Write($"taskbar auto-hide set to {value} (state 0x{wanted:X})");
        }
    }

    /// <summary>
    /// Hides or restores the shell's taskbars — the primary one and any on secondary displays.
    ///
    /// Hiding the window on its own is not enough: the taskbar's appbar registration still reserves
    /// its strip, so the desktop would keep a dead band along the bottom. Auto-hide shrinks that
    /// reservation to a couple of pixels, so the two are done together.
    /// </summary>
    public static void SetHidden(bool hidden)
    {
        // Auto-hide FIRST. Changing the state makes Explorer re-lay-out its taskbar, which puts it
        // straight back on screen — so doing it after the hide simply undoes the hide.
        if (hidden) AutoHide = true;

        Hide(hidden);
    }

    /// <summary>
    /// Shows or hides the shell's taskbar windows, and reports what actually happened. Separate from
    /// <see cref="SetHidden"/> so the watchdog can re-assert the hide without touching the auto-hide
    /// state again — repeating that would make Explorer re-lay-out, and re-show the bar, every time.
    /// </summary>
    public static void Hide(bool hidden)
    {
        int found = 0, changed = 0, refused = 0;

        foreach (var bar in ShellBars())
        {
            found++;
            if (IsWindowVisible(bar) != hidden) continue;   // already in the state we want

            ShowWindow(bar, hidden ? 0 /* SW_HIDE */ : 5 /* SW_SHOW */);

            if (IsWindowVisible(bar) == hidden)
            {
                refused++;
                Log.WriteOnce($"taskbar-refused:{bar}",
                    $"ShowWindow was ignored for 0x{bar:X} ({ClassNameOf(bar)}) — " +
                    $"err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            }
            else changed++;
        }

        if (found == 0) Log.WriteOnce("taskbar-missing", "no Shell_TrayWnd found");
        if (changed > 0 || refused > 0)
            Log.Write($"shell taskbars: {found} found, {changed} {(hidden ? "hidden" : "shown")}, {refused} refused");
    }

    private static IEnumerable<IntPtr> ShellBars()
    {
        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero) yield return primary;

        var secondary = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            if (ClassNameOf(hWnd) == "Shell_SecondaryTrayWnd") secondary.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        foreach (var bar in secondary) yield return bar;
    }

    /// <summary>
    /// Whether Windows animates windows into and out of the taskbar.
    ///
    /// With the taskbar hidden the animation still plays, flying the window toward where its button
    /// would have been — usually the bottom-left corner — which looks like a glitch because there is
    /// nothing there. Turning it off is a per-user system setting, so it is restored on exit.
    /// </summary>
    public static bool MinimiseAnimation
    {
        get
        {
            var info = new ANIMATIONINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ANIMATIONINFO>() };
            return SystemParametersInfoAnimation(SPI_GETANIMATION, info.cbSize, ref info, 0) && info.iMinAnimate != 0;
        }
        set
        {
            var info = new ANIMATIONINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ANIMATIONINFO>(),
                iMinAnimate = value ? 1 : 0,
            };
            if (!SystemParametersInfoAnimation(SPI_SETANIMATION, info.cbSize, ref info, SPIF_SENDCHANGE))
                Log.Write($"could not set minimise animation: err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            else Log.Write($"minimise animation set to {value}");
        }
    }

    /// <summary>Where the taskbar actually is right now, via the documented shell API.</summary>
    public static TaskbarEdge? Current()
    {
        var data = new APPBARDATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == IntPtr.Zero)
        {
            Log.Write("ABM_GETTASKBARPOS failed");
            return null;
        }
        return (TaskbarEdge)data.uEdge;
    }

    /// <summary>
    /// Writes the requested edge and restarts Explorer. Returns false if the registry value could
    /// not be read or written; a true result does not guarantee Windows honoured it.
    /// </summary>
    public static bool Move(TaskbarEdge edge)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StuckRects, writable: true);
            if (key?.GetValue("Settings") is not byte[] blob || blob.Length <= EdgeOffset)
            {
                Log.Write($"taskbar: {StuckRects}\\Settings missing or too short");
                return false;
            }

            // Compare against where the taskbar ACTUALLY is, not against the byte we last wrote.
            // On Windows 11 the write is ignored, so the stored byte says "done" while the bar has
            // not moved - which made every press after the first a silent no-op.
            if (Current() == edge)
            {
                Log.Write($"taskbar already docked {edge}");
                return true;
            }

            blob[EdgeOffset] = (byte)edge;
            key.SetValue("Settings", blob, RegistryValueKind.Binary);
            Log.Write($"taskbar edge set to {edge}; restarting Explorer");

            RestartExplorer();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"taskbar move failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Explorer only reads StuckRects at startup. Windows normally relaunches the shell by itself,
    /// but not in every configuration, so it is started explicitly if it stays down.
    /// </summary>
    private static void RestartExplorer()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); p.WaitForExit(4000); } catch { /* another session's shell */ }
                finally { p.Dispose(); }
            }

            Thread.Sleep(700);
            if (System.Diagnostics.Process.GetProcessesByName("explorer").Length == 0)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"explorer restart failed: {ex.Message}");
        }
    }
}
