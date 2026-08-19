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

    /// <summary>Never evict a stamp that the debounce still needs — otherwise raising DebounceMs
    /// past the prune interval silently stops working, which is the opposite of what the docs say.</summary>
    private long RetainMs => Math.Max(PruneIntervalMs, zones.Config.DebounceMs + 1_000);

    private readonly ZoneManager zones;
    private readonly ConcurrentDictionary<IntPtr, long> recent = new();

    // Last known non-fullscreen rectangle per window: once a window covers the whole monitor its
    // own rect no longer says which zone the user had it in. Pruned when the window dies.
    private readonly ConcurrentDictionary<IntPtr, RECT> lastNormal = new();

    // Owning process per window. Resolving a pid to a name is comparatively expensive and the answer
    // never changes for a given hwnd, so it is worth remembering.
    private readonly ConcurrentDictionary<IntPtr, string> owners = new();

    // ---- drag-to-zone session ----------------------------------------------
    private readonly System.Windows.Forms.Timer dragTick = new() { Interval = 40 };
    private IntPtr dragWindow;
    private RECT dragStartRect;
    private int anchorZone = -1;
    private string dragDevice = "";
    private RECT? dragTarget;

    /// <summary>Overlay used to show where a dragged window would land. Optional.</summary>
    public ZoneOverlay? Overlay { get; set; }

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

    // Counters, reported on a heartbeat. They are what tells the three failure modes apart:
    // no events at all / events but everything filtered out / detected but the move did nothing.
    private long eventsSeen, targetsSeen, fullscreenSeen, clamps;
    private long lastBeat;

    public WinEventHookService(ZoneManager zones)
    {
        this.zones = zones;
        verify.Tick += (_, _) => { verify.Stop(); ReVerify(); };
        dragTick.Tick += (_, _) => DragUpdate();
    }

    public bool Running => hookLocation != IntPtr.Zero || hookMoveEnd != IntPtr.Zero;

    public void Start()
    {
        if (Running) return;

        callback = OnWinEvent;
        pin = GCHandle.Alloc(callback);

        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        // MOVESIZESTART and MOVESIZEEND are adjacent, so one hook covers the whole drag.
        hookMoveEnd   = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND,
                                        IntPtr.Zero, callback, 0, 0, flags);
        hookLocation  = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
                                        IntPtr.Zero, callback, 0, 0, flags);

        int err = Marshal.GetLastWin32Error();
        Log.Write($"hooks installed: moveend=0x{hookMoveEnd:X} location=0x{hookLocation:X}" +
                  (Running ? "" : $"  *** BOTH FAILED, err={err} ***"));
    }

    public void Stop()
    {
        if (hookLocation != IntPtr.Zero) { UnhookWinEvent(hookLocation); hookLocation = IntPtr.Zero; }
        if (hookMoveEnd  != IntPtr.Zero) { UnhookWinEvent(hookMoveEnd);  hookMoveEnd  = IntPtr.Zero; }
        if (pin.IsAllocated) pin.Free();
        callback = null;
        recent.Clear();
        lastNormal.Clear();
        owners.Clear();
        Log.Write("hooks removed");
    }

    public void Dispose()
    {
        Stop();
        verify.Dispose();
        dragTick.Dispose();
    }

    // ---- the callback ------------------------------------------------------

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hWnd,
                            int idObject, int idChild, uint thread, uint time)
    {
        // LOCATIONCHANGE also fires for carets, scrollbars and cursors — cheapest test first.
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hWnd == IntPtr.Zero) return;

        eventsSeen++;
        Heartbeat();

        if (eventType == EVENT_SYSTEM_MOVESIZESTART) { DragBegin(hWnd); return; }
        if (eventType == EVENT_SYSTEM_MOVESIZEEND)   { DragEnd(hWnd); return; }

        // Leave a window alone while the user is hand-placing it.
        if (hWnd == dragWindow) return;

        if (IsSuppressed(hWnd) || !IsClampTarget(hWnd)) return;
        targetsSeen++;

        try
        {
            if (!GetWindowRect(hWnd, out var win)) return;
            if (!ZoneManager.TryGetMonitor(hWnd, out var geo)) return;
            if (!ZoneManager.NeedsClamp(hWnd, win, geo.Bounds))
            {
                lastNormal[hWnd] = win;          // remember where it lived before it went big
                return;
            }

            string owner = OwnerProcess(hWnd), cls = ClassNameOf(hWnd);
            if (zones.Config.IsExcluded(owner, cls))
            {
                Log.Write($"excluded: {owner} / {cls}");
                return;
            }

            fullscreenSeen++;
            Log.Write($"fullscreen-ish: hwnd=0x{hWnd:X} proc={owner} class={cls} rect={win} " +
                      $"monitor={geo.Device} bounds={geo.Bounds} work={geo.Work} " +
                      $"maximized={ZoneManager.IsMaximized(hWnd)} style=0x{GetWindowLongPtr(hWnd, GWL_STYLE):X}");

            if (zones.IsOptedOut(geo)) return;   // monitor configured as a single full-size zone

            var zoneRects = zones.ZonesFor(geo);
            if (zoneRects.Count == 0) { Log.Write($"no zones for {geo.Device}"); return; }

            int index = ZoneManager.PickZoneIndex(zoneRects, ReferenceRect(hWnd, win));
            Log.Write($"  -> zone {index} of {zoneRects.Count}: {zoneRects[index]}");
            Apply(hWnd, zoneRects[index]);
        }
        catch (Exception ex)
        {
            // An exception escaping into unmanaged hook dispatch kills the process.
            Log.Write($"callback error: {ex}");
        }
    }

    // ---- drag to zone ------------------------------------------------------

    private void DragBegin(IntPtr hWnd)
    {
        if (!zones.Config.DragToZone || Overlay is null) return;
        if (!IsClampTarget(hWnd)) return;
        if (zones.Config.IsExcluded(OwnerProcess(hWnd), ClassNameOf(hWnd))) return;

        dragWindow = hWnd;
        anchorZone = -1;
        dragDevice = "";
        dragTarget = null;
        GetWindowRect(hWnd, out dragStartRect);
        dragTick.Start();
    }

    /// <summary>
    /// Polled rather than event-driven: the cursor moves far more smoothly than LOCATIONCHANGE
    /// arrives, and the highlight has to follow the cursor rather than the window.
    /// </summary>
    private void DragUpdate()
    {
        if (dragWindow == IntPtr.Zero || Overlay is null) { dragTick.Stop(); return; }

        // The modifier is a live gate: release it mid-drag and the overlay gets out of the way.
        if (!ModifierHeld(zones.Config.DragModifier))
        {
            if (Overlay.Visible && Overlay.Mode == OverlayMode.Drag) Overlay.Hide();
            dragTarget = null;
            anchorZone = -1;
            return;
        }

        if (!GetCursorPos(out var cursor)) return;
        if (!ZoneManager.TryGetMonitorAt(cursor, out var geo)) return;
        if (zones.IsOptedOut(geo)) return;

        var rects = zones.ZonesFor(geo);
        int hit = ZoneManager.ZoneIndexAt(rects, cursor);
        if (hit < 0) return;

        if (!Overlay.Visible || Overlay.Mode != OverlayMode.Drag) Overlay.Show(zones, OverlayMode.Drag);

        if (anchorZone < 0 || dragDevice != geo.Device) { anchorZone = hit; dragDevice = geo.Device; }

        // Holding the span modifier grows the target across every zone between anchor and cursor.
        bool span = ModifierHeld(zones.Config.SpanModifier) && anchorZone < rects.Count;
        dragTarget = span ? ZoneManager.Union(rects[anchorZone], rects[hit]) : rects[hit];
        if (!span) anchorZone = hit;

        Overlay.Highlight(geo.Device, dragTarget);
    }

    private void DragEnd(IntPtr hWnd)
    {
        dragTick.Stop();
        var target = dragTarget;
        var dragged = dragWindow;

        dragWindow = IntPtr.Zero;
        dragTarget = null;
        anchorZone = -1;
        Overlay?.Hide();

        if (dragged == IntPtr.Zero || dragged != hWnd || target is null) return;
        if (!ModifierHeld(zones.Config.DragModifier)) return;

        // A resize is not a move: only snap when the window actually travelled.
        if (GetWindowRect(hWnd, out var now) && now.Left == dragStartRect.Left && now.Top == dragStartRect.Top
            && now.Width == dragStartRect.Width && now.Height == dragStartRect.Height)
            return;

        Log.Write($"drag-to-zone: 0x{hWnd:X} -> {target.Value}");
        Apply(hWnd, target.Value);
    }

    private static bool ModifierHeld(string name)
    {
        var vk = SplitConfig.ModifierKey(name);
        return vk is null || IsKeyDown(vk.Value);
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
        if (!ZoneManager.ClampToZone(hWnd, zone)) { Log.Write("  -> ClampToZone declined (already in place?)"); return; }
        clamps++;
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

    /// <summary>Process name that owns a window, or "?" if it cannot be resolved.</summary>
    public static string OwnerProcessOf(IntPtr hWnd)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return "?";
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch { return "?"; }
    }

    private string OwnerProcess(IntPtr hWnd) => owners.GetOrAdd(hWnd, OwnerProcessOf);

    /// <summary>Periodic one-liner: the fastest way to see which stage is starving.</summary>
    private void Heartbeat()
    {
        long now = Environment.TickCount64;
        if (now - lastBeat < 5_000) return;
        lastBeat = now;
        Log.Write($"[beat] events={eventsSeen} targets={targetsSeen} fullscreen={fullscreenSeen} clamps={clamps} " +
                  $"hooks={(Running ? "up" : "DOWN")}");
    }

    // ---- reentrancy guard --------------------------------------------------

    private void Touch(IntPtr hWnd) => recent[hWnd] = Environment.TickCount64;

    private bool IsSuppressed(IntPtr hWnd)
    {
        long now = Environment.TickCount64;

        if (now - lastPrune > PruneIntervalMs)
        {
            lastPrune = now;
            long retain = RetainMs;
            foreach (var kv in recent)
                if (now - kv.Value > retain) recent.TryRemove(kv.Key, out _);
            foreach (var key in lastNormal.Keys)
                if (!IsWindow(key)) lastNormal.TryRemove(key, out _);
            foreach (var key in owners.Keys)
                if (!IsWindow(key)) owners.TryRemove(key, out _);
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
