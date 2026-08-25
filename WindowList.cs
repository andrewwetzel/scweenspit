using System.Drawing;
using System.Runtime.InteropServices;
using static ScweenSpit.Native;

namespace ScweenSpit;

public sealed record TaskWindow(IntPtr Handle, string Title, string Process, string Path,
                               string AppId, bool Minimised)
{
    /// <summary>
    /// What decides which button this window belongs to. Windows' own application id when the
    /// window carries one, which is how a Chrome PWA is a separate application from the browser
    /// despite both being chrome.exe — the shell groups the real taskbar exactly this way.
    /// </summary>
    public string GroupId => AppId.Length > 0 ? AppId : Path;
}

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
        EnumWindows((hWnd, _) =>
        {
            if (!IsTaskWindow(hWnd)) return true;

            if (device is not null)
            {
                if (!ZoneManager.TryGetMonitor(hWnd, out var geo)) return true;
                if (!string.Equals(geo.Device, device, StringComparison.OrdinalIgnoreCase)) return true;
            }

            var title = WindowTitle(hWnd);
            if (title.Length == 0) return true;                 // nothing to label a button with

            found.Add(new TaskWindow(hWnd, title, WinEventHookService.OwnerProcessOf(hWnd),
                                     ExecutablePath(hWnd), AppIdOf(hWnd), IsIconic(hWnd)));
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

    // ---- application identity ----------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public uint PropertyId; }

    // Only the first field is read, and only when it says the value is a string.
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant { public ushort Type; ushort a, b, c; public IntPtr Value, Value2; }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hWnd, ref Guid iid, out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    private static readonly Guid PropertyStoreId = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    private static readonly PropertyKey AppUserModelId = new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        PropertyId = 5,
    };

    private const ushort VariantString = 31;   // VT_LPWSTR

    /// <summary>
    /// The application id a window declares, or empty when it declares none.
    ///
    /// This is how Windows tells one application from another when several share an executable: a
    /// Chrome PWA sets its own id, so the shell lists it separately from the browser. Grouping on
    /// the executable alone puts them under one icon, which is exactly wrong for a PWA.
    /// </summary>
    public static string AppIdOf(IntPtr hWnd)
    {
        IPropertyStore? store = null;
        var value = new PropVariant();

        try
        {
            var iid = PropertyStoreId;
            if (SHGetPropertyStoreForWindow(hWnd, ref iid, out store) != 0 || store is null) return "";

            var key = AppUserModelId;
            store.GetValue(ref key, out value);

            return value.Type == VariantString ? Marshal.PtrToStringUni(value.Value) ?? "" : "";
        }
        catch (Exception ex)
        {
            Log.WriteOnce("appid", $"could not read an application id: {ex.Message}");
            return "";
        }
        finally
        {
            try { PropVariantClear(ref value); } catch { }
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

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

        Raise(hWnd);
    }

    /// <summary>
    /// Brings a window to the front, past Windows' foreground lock.
    ///
    /// A process may only hand the foreground to a window if it already holds it, or was the last
    /// to receive input. A taskbar holds neither: its own window is deliberately never activated,
    /// so SetForegroundWindow is refused and the window is raised only as far as the top of its own
    /// z-order band — behind whatever was maximised, which is exactly what it looks like.
    ///
    /// Attaching our input queue to the outgoing foreground thread makes the call legitimate for as
    /// long as it takes to make it. The attachment is always undone: leaving two input queues joined
    /// makes both applications feel wrong.
    /// </summary>
    /// <summary>
    /// Raised just after a window is brought forward, so anything holding a z-order policy can
    /// follow the change immediately rather than waiting to notice it.
    /// </summary>
    public static event Action? Raised;

    public static void Raise(IntPtr hWnd)
    {
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

        var foreground = GetForegroundWindow();
        if (foreground == hWnd) return;

        uint self = GetCurrentThreadId();
        uint holder = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;
        uint owner = GetWindowThreadProcessId(hWnd, out _);

        bool joinedHolder = holder != 0 && holder != self && AttachThreadInput(holder, self, true);
        bool joinedOwner = owner != 0 && owner != self && owner != holder && AttachThreadInput(owner, self, true);

        try
        {
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (joinedOwner) AttachThreadInput(owner, self, false);
            if (joinedHolder) AttachThreadInput(holder, self, false);
        }

        Raised?.Invoke();

        // If it still is not in front, something is holding the position - most likely a window
        // marked always-on-top, which nothing ordinary can be raised past.
        if (GetForegroundWindow() != hWnd)
            Log.WriteOnce($"raise:{hWnd}", $"0x{hWnd:X} ({ClassNameOf(hWnd)}) would not come forward");
    }
}
