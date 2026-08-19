using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static ScweenSpit.Native;

namespace ScweenSpit;

/// <summary>
/// Listens for window moves and clamps anything that grabs a whole monitor back into its zone.
/// Must be constructed and started on the UI thread: out-of-context hook callbacks are delivered
/// through that thread's message pump.
/// </summary>
public sealed class WinEventHookService : IDisposable
{
    /// <summary>Classes that are never ours to move.</summary>
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
        "TaskListThumbnailWnd", "tooltips_class32", "Windows.UI.Core.CoreWindow",
        "Button", "ForegroundStaging", "MultitaskingViewFrame", "XamlExplorerHostIslandWindow",
    };

    private const long PruneIntervalMs = 5_000;

    private readonly ZoneManager zones;
    private readonly ConcurrentDictionary<IntPtr, long> recent = new();

    // Last known non-fullscreen rectangle per window: once a window covers the whole monitor its
    // own rect no longer says which zone the user had it in. Pruned when the window dies.
    private readonly ConcurrentDictionary<IntPtr, RECT> lastNormal = new();

    // Apps like Chrome re-assert their fullscreen size once, just after we shrink them —
    // inside the debounce window, where we are deaf. One bounded re-check settles it.
    private readonly System.Windows.Forms.Timer verify = new();
    private IntPtr verifyTarget;
    private RECT verifyZone;

    // The delegate must outlive the hook: the GC has no idea user32 holds a pointer to it.
    private WinEventDelegate? callback;
    private GCHandle pin;
    private IntPtr hookLocation, hookMoveEnd;
    private long lastPrune;

    public WinEventHookService(ZoneManager zones)
    {
        this.zones = zones;
        verify.Tick += (_, _) => { verify.Stop(); ReVerify(); };
    }

    public bool Running => hookLocation != IntPtr.Zero || hookMoveEnd != IntPtr.Zero;

    public void Start()
    {
        if (Running) return;

        callback = OnWinEvent;
        pin = GCHandle.Alloc(callback);

        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        hookMoveEnd   = SetWinEventHook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND,
                                        IntPtr.Zero, callback, 0, 0, flags);
        hookLocation  = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
                                        IntPtr.Zero, callback, 0, 0, flags);

        Log.Write($"hooks installed: moveend={hookMoveEnd:X} location={hookLocation:X}");
    }

    public void Stop()
    {
        if (hookLocation != IntPtr.Zero) { UnhookWinEvent(hookLocation); hookLocation = IntPtr.Zero; }
        if (hookMoveEnd  != IntPtr.Zero) { UnhookWinEvent(hookMoveEnd);  hookMoveEnd  = IntPtr.Zero; }
        if (pin.IsAllocated) pin.Free();
        callback = null;
        recent.Clear();
        lastNormal.Clear();
        Log.Write("hooks removed");
    }

    public void Dispose()
    {
        Stop();
        verify.Dispose();
    }

    // ---- the callback ------------------------------------------------------

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hWnd,
                            int idObject, int idChild, uint thread, uint time)
    {
        // LOCATIONCHANGE also fires for carets, scrollbars and cursors — cheapest test first.
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hWnd == IntPtr.Zero) return;
        if (IsSuppressed(hWnd) || !IsClampTarget(hWnd)) return;

        try
        {
            if (!GetWindowRect(hWnd, out var win)) return;
            if (!ZoneManager.TryGetMonitor(hWnd, out var geo)) return;
            if (!ZoneManager.NeedsClamp(hWnd, win, geo.Bounds))
            {
                lastNormal[hWnd] = win;          // remember where it lived before it went big
                return;
            }

            if (zones.IsOptedOut(geo)) return;   // monitor configured as a single full-size zone

            var zoneRects = zones.ZonesFor(geo);
            if (zoneRects.Count == 0) return;

            Apply(hWnd, zoneRects[ZoneManager.PickZoneIndex(zoneRects, ReferenceRect(hWnd, win))]);
        }
        catch (Exception ex)
        {
            // An exception escaping into unmanaged hook dispatch kills the process.
            Log.Write($"callback error: {ex}");
        }
    }

    /// <summary>
    /// Which rectangle should decide the zone: the last normal position if we saw one, else the
    /// pre-maximize rectangle, else the window as it stands (whole-monitor, so centre-of-screen).
    /// </summary>
    private RECT ReferenceRect(IntPtr hWnd, RECT win)
    {
        if (lastNormal.TryGetValue(hWnd, out var normal)) return normal;
        return ZoneManager.TryGetRestoreRect(hWnd, out var restore) ? restore : win;
    }

    /// <summary>Stamps the reentrancy guard, then moves the window. The order matters.</summary>
    public void Apply(IntPtr hWnd, RECT zone)
    {
        Touch(hWnd);
        if (!ZoneManager.ClampToZone(hWnd, zone)) return;
        Touch(hWnd); // restamp: the move itself is about to echo back as LOCATIONCHANGE

        verifyTarget = hWnd;
        verifyZone   = zone;
        verify.Interval = zones.Config.DebounceMs + 60;
        verify.Stop();
        verify.Start();
    }

    /// <summary>
    /// Fires once just after the debounce expires. Never re-schedules itself, so a window that
    /// insists on being fullscreen costs exactly one extra correction, not a ping-pong loop.
    /// </summary>
    private void ReVerify()
    {
        var hWnd = verifyTarget;
        verifyTarget = IntPtr.Zero;

        if (hWnd == IntPtr.Zero || !IsClampTarget(hWnd)) return;
        if (!GetWindowRect(hWnd, out var win)) return;
        if (!ZoneManager.TryGetMonitor(hWnd, out var geo)) return;
        if (!ZoneManager.NeedsClamp(hWnd, win, geo.Bounds)) return;

        Log.Write($"re-verify {hWnd:X}: still fullscreen, re-clamping");
        Touch(hWnd);
        ZoneManager.ClampToZone(hWnd, verifyZone);
        Touch(hWnd);
    }

    // ---- reentrancy guard --------------------------------------------------

    private void Touch(IntPtr hWnd) => recent[hWnd] = Environment.TickCount64;

    private bool IsSuppressed(IntPtr hWnd)
    {
        long now = Environment.TickCount64;

        if (now - lastPrune > PruneIntervalMs)
        {
            lastPrune = now;
            foreach (var kv in recent)
                if (now - kv.Value > PruneIntervalMs) recent.TryRemove(kv.Key, out _);
            foreach (var key in lastNormal.Keys)
                if (!IsWindow(key)) lastNormal.TryRemove(key, out _);
        }

        // WINEVENT_SKIPOWNPROCESS does not help here: we move *other* processes' windows,
        // so their LOCATIONCHANGE comes straight back at us.
        return recent.TryGetValue(hWnd, out long stamp) && now - stamp < zones.Config.DebounceMs;
    }

    // ---- filtering ---------------------------------------------------------

    public static bool IsClampTarget(IntPtr hWnd)
    {
        if (!IsWindow(hWnd) || !IsWindowVisible(hWnd)) return false;
        if (GetAncestor(hWnd, GA_ROOT) != hWnd) return false;      // not a top-level window

        long style = GetWindowLongPtr(hWnd, GWL_STYLE);
        if ((style & WS_CHILD) != 0) return false;

        long exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;

        if (IgnoredClasses.Contains(ClassNameOf(hWnd))) return false;
        if (IsCloaked(hWnd)) return false;                          // phantom UWP windows

        return true;
    }
}
