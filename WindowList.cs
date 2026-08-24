using System.Drawing;
using static ScweenSpit.Native;

namespace ScweenSpit;

public sealed record TaskWindow(IntPtr Handle, string Title, string Process, string Path, bool Minimised);

/// <summary>
/// Works out which windows belong on a taskbar. This is the same judgement Alt+Tab makes, and it is
/// fiddlier than it looks: owned dialogs, tool windows, and the invisible host windows every UWP app
/// leaves lying around all have to be filtered out, or the bar fills up with things that are not
/// applications.
/// </summary>
public static class WindowList
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "Windows.UI.Core.CoreWindow",
        "ApplicationFrameWindow", "TaskListThumbnailWnd", "Windows.Internal.Shell.TabProxyWindow",
        "MultitaskingViewFrame", "ForegroundStaging", "XamlExplorerHostIslandWindow",
    };

    /// <summary>Every application window, in z-order, optionally limited to one display.</summary>
    public static List<TaskWindow> Enumerate(string? device = null)
    {
        var found = new List<TaskWindow>();
        uint self = (uint)Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (!IsTaskWindow(hWnd)) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == self) return true;                       // our own bar and overlays

            if (device is not null)
            {
                if (!ZoneManager.TryGetMonitor(hWnd, out var geo)) return true;
                if (!string.Equals(geo.Device, device, StringComparison.OrdinalIgnoreCase)) return true;
            }

            var title = WindowTitle(hWnd);
            if (title.Length == 0) return true;                 // nothing to label a button with

            found.Add(new TaskWindow(hWnd, title, WinEventHookService.OwnerProcessOf(hWnd),
                                     ExecutablePath(hWnd), IsIconic(hWnd)));
            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// The Alt+Tab test. A window qualifies when it is the last active popup of its own root owner —
    /// that is what keeps a modal dialog from adding a second button for its parent application.
    /// </summary>
    public static bool IsTaskWindow(IntPtr hWnd)
    {
        if (!IsWindowVisible(hWnd)) return false;

        long exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;

        long style = GetWindowLongPtr(hWnd, GWL_STYLE);
        if ((style & WS_CHILD) != 0) return false;

        if (Ignored.Contains(ClassNameOf(hWnd))) return false;

        // Cloaked windows are the invisible husks UWP apps leave behind when suspended.
        if (IsCloaked(hWnd)) return false;

        IntPtr walk = IntPtr.Zero, next = GetAncestor(hWnd, GA_ROOTOWNER);
        while (next != walk)
        {
            walk = next;
            next = GetLastActivePopup(walk);
            if (IsWindowVisible(next)) break;
        }
        return walk == hWnd;
    }

    /// <summary>
    /// The window's own icon. The handle belongs to the other application, so it is wrapped without
    /// taking ownership and copied into a bitmap we do own.
    /// </summary>
    public static Bitmap? IconFor(IntPtr hWnd)
    {
        IntPtr handle = Ask(hWnd, ICON_BIG);
        if (handle == IntPtr.Zero) handle = Ask(hWnd, ICON_SMALL2);
        if (handle == IntPtr.Zero) handle = Ask(hWnd, ICON_SMALL);
        if (handle == IntPtr.Zero) handle = GetClassLongPtr(hWnd, GCLP_HICON);
        if (handle == IntPtr.Zero) handle = GetClassLongPtr(hWnd, GCLP_HICONSM);
        if (handle == IntPtr.Zero) return null;

        try
        {
            // Kept at 32px and scaled down when drawn: a 16px source looks smeared on a taskbar
            // sized for touch, and the caller decides how large the buttons are.
            using var icon = Icon.FromHandle(handle);
            using var bitmap = icon.ToBitmap();
            return new Bitmap(bitmap, 32, 32);
        }
        catch { return null; }
    }

    /// <summary>
    /// Asks a window for its icon without hanging if it is not answering: a taskbar that blocks on
    /// an unresponsive application is a taskbar that stops repainting.
    /// </summary>
    private static IntPtr Ask(IntPtr hWnd, int which) =>
        SendMessageTimeout(hWnd, WM_GETICON, new IntPtr(which), IntPtr.Zero, SMTO_ABORTIFHUNG, 120, out var result) != IntPtr.Zero
            ? result : IntPtr.Zero;

    /// <summary>Asks a window to close, the way its own close button would.</summary>
    public static void Close(IntPtr hWnd) => PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>The icon of an application that is not running, taken from its executable.</summary>
    public static Bitmap? IconForFile(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return null;

            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;

            using var bitmap = icon.ToBitmap();
            return new Bitmap(bitmap, 32, 32);
        }
        catch { return null; }
    }

    /// <summary>Brings a window forward, or puts it away if it is already in front.</summary>
    public static void Toggle(IntPtr hWnd)
    {
        if (GetForegroundWindow() == hWnd && !IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_MINIMIZE);
            return;
        }

        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
        SetForegroundWindow(hWnd);
    }
}
