using Microsoft.Win32;

namespace ScweenSpit;

/// <summary>
/// Puts back everything ScweenSpit changes about the machine itself, as opposed to about its own
/// windows: the shell's taskbar, its auto-hide state, Windows' snap settings, and the minimise
/// animation.
///
/// Kept apart from the tray context and from any window, so it can run when neither exists — at the
/// end of a session, or from a command line when a previous run died without doing it. Someone whose
/// taskbar is missing has no tray icon left to click, which is the whole reason this is separate.
///
/// Every step is independent. Undoing four things one at a time, where a failure in the first must
/// not decide the fate of the other three, is the entire job.
/// </summary>
public static class SystemRestore
{
    /// <summary>
    /// Broadcast by a copy started with --restore, so a copy already running stands down rather than
    /// spending the next two seconds putting back what the first one just undid.
    /// </summary>
    public static readonly uint StandDownMessage = Native.RegisterWindowMessage("ScweenSpit.StandDown");

    /// <summary>
    /// Broadcast by a copy being uninstalled. Standing down is not enough there: the program is
    /// being removed, and an uninstall that reports success while it is still running is a lie.
    /// </summary>
    public static readonly uint QuitMessage = Native.RegisterWindowMessage("ScweenSpit.Quit");

    /// <summary>
    /// Undoes whatever <paramref name="config"/> records as outstanding, and clears the records it
    /// managed to act on. Returns the number of changes put back.
    /// </summary>
    public static int Everything(SplitConfig config)
    {
        int done = 0;

        // The animation first: it is the cheapest, and it cannot be affected by anything below.
        if (config.AnimationRestore is { } animationWasOn
            && Attempt("minimise animation", () => Taskbar.MinimiseAnimation = animationWasOn))
        {
            config.AnimationRestore = null;
            done++;
        }

        if (config.TaskbarRestore is { } wasAutoHidden
            && Attempt("taskbar", () => ShowTaskbar(config, wasAutoHidden)))
        {
            config.TaskbarRestore = null;
            done++;
        }

        if (config.SnapRestore is { } snap
            && Attempt("windows snap", () => WindowsSnap.Restore(snap)))
        {
            config.SnapRestore = null;
            done++;
        }

        if (done > 0) config.Save();
        return done;
    }

    /// <summary>
    /// Brings the shell's taskbar back and puts its auto-hide state back the way it was found.
    ///
    /// The auto-hide half matters as much as the showing: a taskbar that is visible only while the
    /// pointer is in the last row of pixels is, to anyone looking for it, still gone.
    /// </summary>
    private static void ShowTaskbar(SplitConfig config, bool wasAutoHidden)
    {
        Taskbar.SetHidden(false);

        // The user's own preference wins over what was found on the way in. A previous run that died
        // without restoring leaves auto-hide on, and the run after it records that as "how the
        // machine was" — so trusting the found value alone makes one lost restore permanent.
        Taskbar.AutoHide = config.TaskbarAutoHide ?? wasAutoHidden;

        if (!Visible()) Log.Write("taskbar still not visible after being shown; Explorer may be busy");
    }

    /// <summary>Whether any shell taskbar is on screen. The point of all of this.</summary>
    public static bool Visible()
    {
        var primary = Native.FindWindow("Shell_TrayWnd", null);
        return primary != IntPtr.Zero && Native.IsWindowVisible(primary);
    }

    /// <summary>Anything outstanding to put back, according to the record.</summary>
    public static bool Outstanding(SplitConfig config) =>
        config.AnimationRestore is not null || config.TaskbarRestore is not null
        || config.SnapRestore is not null;

    private static bool Attempt(string what, Action action)
    {
        try
        {
            action();
            Log.Write($"restored {what}");
            return true;
        }
        catch (Exception ex)
        {
            // Deliberately swallowed, and deliberately not rethrown: the next restore is more
            // important than this one's failure, and the record is left in place so a later run can
            // try again.
            Log.Write($"could not restore {what}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The command-line escape hatch: put the machine back without starting the app.
    ///
    /// For the case the rest of this cannot cover — a run that was killed, crashed, or lost to a
    /// power cut, leaving no taskbar to reach anything by. Task Manager's Run New Task still opens
    /// on Ctrl+Shift+Esc with no shell at all, which makes this reachable when nothing else is.
    /// </summary>
    public static void RunFromCommandLine()
    {
        Log.Write("--restore requested");

        // Anything already running has a watchdog that re-hides the taskbar every two seconds for as
        // long as its own settings say to — so telling it to stop comes before putting anything back,
        // or the rescue is undone before the message box has finished being read.
        if (StandDownMessage != 0)
        {
            Native.PostMessage(Native.HWND_BROADCAST, StandDownMessage, IntPtr.Zero, IntPtr.Zero);
            Log.Write("asked any running copy to stand down");
            Thread.Sleep(1200);   // long enough for it to save; it is a keystroke's worth of wait
        }

        var config = SplitConfig.Load();

        // Not merely what the record says: a record can be lost, and the taskbar is the thing that
        // strands people. If it is hidden, show it, whatever the file believes.
        int done = Everything(config);

        if (!Visible())
        {
            Attempt("taskbar (unrecorded)", () =>
            {
                Taskbar.SetHidden(false);
                Taskbar.AutoHide = config.TaskbarAutoHide ?? false;
            });
            done++;
        }

        // So the next ordinary launch does not immediately hide it again, which would make this look
        // like it had not worked.
        if (config.HideWindowsTaskbar)
        {
            config.HideWindowsTaskbar = false;
            config.Save();
            Log.Write("--restore also turned off 'hide the Windows taskbar'");
        }

        Report(done);
    }

    private static void Report(int done)
    {
        var message = done > 0
            ? $"ScweenSpit put {done} system {(done == 1 ? "setting" : "settings")} back.\n\n" +
              "The Windows taskbar should be visible again, and hiding it is now switched off."
            : "Nothing needed putting back — no ScweenSpit changes were outstanding.";

        try
        {
            System.Windows.Forms.MessageBox.Show(message, "ScweenSpit",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information,
                System.Windows.Forms.MessageBoxDefaultButton.Button1,
                System.Windows.Forms.MessageBoxOptions.DefaultDesktopOnly);
        }
        catch (Exception ex) { Log.Write($"could not report the restore: {ex.Message}"); }
    }

    /// <summary>
    /// Puts everything back when Windows is signing out or shutting down. The message loop ends
    /// without disposing anything, so nothing else here runs — and auto-hide is a state Explorer
    /// keeps, so it would still be on at the next sign-in.
    /// </summary>
    public static void OnSessionEnding(SplitConfig config)
    {
        Log.Write("session ending; restoring");
        Everything(config);
    }
}
