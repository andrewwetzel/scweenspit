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
    private bool dragSawFullscreen;
    private bool pinnedOverlay;

    // Re-evaluation after a drag that did not snap: a window Aero-snapped to maximized *inside* the
    // move loop is swallowed by the drag guard and would otherwise never be looked at again.
    private readonly System.Windows.Forms.Timer postDrag = new() { Interval = 200 };
    private IntPtr postDragWindow;
    private int anchorZone = -1;
    private string dragDevice = "";
    private Zone? dragTarget;

    /// <summary>Overlay used to show where a dragged window would land. Optional.</summary>
    public ZoneOverlay? Overlay { get; set; }

    // Apps like Chrome re-assert their fullscreen size once, just after we shrink them —
    // inside the debounce window, where we are deaf. One bounded re-check settles it.
    private readonly System.Windows.Forms.Timer verify = new();
    private IntPtr verifyTarget;
    private Zone verifyZone;

    // The delegate must outlive the hook: the GC has no idea user32 holds a pointer to it.
    private WinEventDelegate? callback;
    private GCHandle pin;
    private IntPtr hookLocation, hookMoveEnd, hookShow;

    // Windows we raised above the taskbar for a cover-taskbar zone; restored when they leave one.
    private readonly ConcurrentDictionary<IntPtr, byte> madeTopmost = new();

    // Windows the user personally dragged across a display boundary. Their choice stands.
    private readonly ConcurrentDictionary<IntPtr, byte> allowedToSpan = new();

    // Newly shown windows, checked after a short settle: an app that is still laying itself out
    // must not be fought over its own opening position.
    private readonly ConcurrentDictionary<IntPtr, long> pendingShow = new();
    private readonly System.Windows.Forms.Timer settle = new() { Interval = 100 };
    private const long SettleMs = 350;
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
        postDrag.Tick += (_, _) => { postDrag.Stop(); ReconcileAfterDrag(); };
        settle.Tick += (_, _) => DrainPendingShow();
    }

    public bool Running => hookLocation != IntPtr.Zero || hookMoveEnd != IntPtr.Zero || hookShow != IntPtr.Zero;

    /// <summary>All hooks installed. A partial set still reports Running, but is not well.</summary>
    public bool Healthy => hookLocation != IntPtr.Zero && hookMoveEnd != IntPtr.Zero && hookShow != IntPtr.Zero;

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
        // A window that opens straddling two displays may never move again, so LOCATIONCHANGE alone
        // would never see it.
        hookShow      = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW,
                                        IntPtr.Zero, callback, 0, 0, flags);

        int err = Marshal.GetLastWin32Error();
        Log.Write($"hooks installed: moveend=0x{hookMoveEnd:X} location=0x{hookLocation:X} show=0x{hookShow:X}" +
                  (Running ? "" : $"  *** BOTH FAILED, err={err} ***"));
    }

    public void Stop()
    {
        // Above the guard: the hotkey path arms the verify timer while the hooks are down.
        CancelPending();
        if (!Running) return;

        CancelDrag();
        if (hookLocation != IntPtr.Zero) { UnhookWinEvent(hookLocation); hookLocation = IntPtr.Zero; }
        if (hookMoveEnd  != IntPtr.Zero) { UnhookWinEvent(hookMoveEnd);  hookMoveEnd  = IntPtr.Zero; }
        if (hookShow     != IntPtr.Zero) { UnhookWinEvent(hookShow);     hookShow     = IntPtr.Zero; }
        if (pin.IsAllocated) pin.Free();
        callback = null;
        RestoreTopmost();
        recent.Clear();
        lastNormal.Clear();
        owners.Clear();
        allowedToSpan.Clear();
        pendingShow.Clear();
        settle.Stop();
        Log.Write("hooks removed");
    }

    public void Dispose()
    {
        Stop();
        verify.Dispose();
        dragTick.Dispose();
        postDrag.Dispose();
        settle.Dispose();
    }

    // ---- the callback ------------------------------------------------------

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hWnd,
                            int idObject, int idChild, uint thread, uint time)
    {
        // LOCATIONCHANGE also fires for carets, scrollbars and cursors — cheapest test first.
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hWnd == IntPtr.Zero) return;

        eventsSeen++;
        Heartbeat();

        try
        {
            if (eventType == EVENT_SYSTEM_MOVESIZESTART) { DragBegin(hWnd); return; }
            if (eventType == EVENT_SYSTEM_MOVESIZEEND)   { DragEnd(hWnd); return; }
            if (eventType == EVENT_OBJECT_SHOW)          { QueueShow(hWnd); return; }

            if (IsSuppressed(hWnd) || !IsClampTarget(hWnd)) return;
            targetsSeen++;
            Reconcile(hWnd);
        }
        catch (Exception ex)
        {
            // An exception escaping into unmanaged hook dispatch kills the process.
            Log.Write($"callback error: {ex}");
        }
    }

    /// <summary>Decides what, if anything, to do about one window's current state.</summary>
    private void Reconcile(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out var win)) return;
        if (!ZoneManager.TryGetMonitor(hWnd, out var geo)) return;

        bool dragging = hWnd == dragWindow;

        if (!ZoneManager.NeedsClamp(hWnd, win, geo.Bounds))
        {
            // Recorded even mid-drag and even with clamping off: this is the rectangle that decides
            // which zone a later maximize lands in, and letting it go stale sends the window back
            // to whichever zone owns the middle of the screen.
            lastNormal[hWnd] = win;

            if (!dragging) KeepOnOneDisplay(hWnd, win, geo);
            return;
        }

        // The user is hand-placing it; do not fight them for it. Remember that we looked away, so
        // the end of the drag can take a second look.
        if (dragging) { dragSawFullscreen = true; return; }

        // Hooks may be up purely to serve drag-to-zone.
        if (!zones.Config.AutoClamp) return;

        // Both of these are terminal states: the window stays fullscreen, so an unthrottled line
        // here repeats for the life of the window.
        if (zones.IsOptedOut(geo))
        {
            Log.WriteOnce($"optedout:{geo.Device}", $"{geo.Device} is opted out; leaving windows alone");
            return;
        }

        string owner = OwnerProcess(hWnd), cls = ClassNameOf(hWnd);
        if (zones.Config.IsExcluded(owner, cls))
        {
            Log.WriteOnce($"excluded:{hWnd}", $"excluded: {owner} / {cls}");
            return;
        }

        fullscreenSeen++;
        Log.Write($"fullscreen-ish: hwnd=0x{hWnd:X} proc={owner} class={cls} rect={win} " +
                  $"monitor={geo.Device} bounds={geo.Bounds} work={geo.Work} " +
                  $"maximized={ZoneManager.IsMaximized(hWnd)} style=0x{GetWindowLongPtr(hWnd, GWL_STYLE):X}");

        var zoneRects = zones.ZonesFor(geo);
        if (zoneRects.Count == 0) { Log.Write($"no zones for {geo.Device}"); return; }

        int index = ZoneManager.PickZoneIndex(zoneRects, ReferenceRect(hWnd, win));
        Log.Write($"  -> zone {index} of {zoneRects.Count}: {zoneRects[index]}");
        Apply(hWnd, zoneRects[index]);
    }

    // ---- keeping windows on one display ------------------------------------

    /// <summary>
    /// Pulls a window that appeared straddling several displays back onto the one it mostly
    /// occupies. Windows the user dragged across a boundary themselves are exempt.
    /// </summary>
    private void KeepOnOneDisplay(IntPtr hWnd, RECT win, MonitorGeometry geo)
    {
        if (!zones.Config.KeepOnOneDisplay) return;
        if (allowedToSpan.ContainsKey(hWnd)) return;
        if (!ZoneManager.SpansDisplays(win, geo)) return;
        if (zones.Config.IsExcluded(OwnerProcess(hWnd), ClassNameOf(hWnd))) return;

        var target = ZoneManager.ContainWithin(win, zones.EffectiveWork(geo));
        Log.Write($"spanning: 0x{hWnd:X} {ClassNameOf(hWnd)} {win} -> {target} on {geo.Device}");
        Apply(hWnd, new Zone(target, CoverTaskbar: false));
    }

    /// <summary>Lets a window straddle displays for the rest of its life, or takes that back.</summary>
    public bool ToggleSpanAllowed(IntPtr hWnd)
    {
        if (allowedToSpan.TryRemove(hWnd, out _)) return false;
        allowedToSpan[hWnd] = 0;
        return true;
    }

    private void QueueShow(IntPtr hWnd)
    {
        if (!zones.Config.KeepOnOneDisplay) return;

        pendingShow[hWnd] = Environment.TickCount64 + SettleMs;
        settle.Start();
    }

    /// <summary>
    /// Re-examines recently shown windows once they have settled. Checking on the SHOW event itself
    /// catches apps mid-layout and starts a tug of war over their own opening position.
    /// </summary>
    private void DrainPendingShow()
    {
        long now = Environment.TickCount64;

        foreach (var entry in pendingShow)
        {
            if (now < entry.Value) continue;
            if (!pendingShow.TryRemove(entry.Key, out _)) continue;

            var hWnd = entry.Key;
            if (!IsClampTarget(hWnd)) continue;

            try { Reconcile(hWnd); }
            catch (Exception ex) { Log.Write($"settle reconcile failed: {ex}"); }
        }

        if (pendingShow.IsEmpty) settle.Stop();
    }

    /// <summary>Second look at a window whose fullscreen transition happened during its own drag.</summary>
    private void ReconcileAfterDrag()
    {
        var hWnd = postDragWindow;
        postDragWindow = IntPtr.Zero;

        if (hWnd == IntPtr.Zero || !IsClampTarget(hWnd)) return;
        try { Reconcile(hWnd); }
        catch (Exception ex) { Log.Write($"post-drag reconcile failed: {ex}"); }
    }

    /// <summary>
    /// Places every open window into its zone, once, on request. Deliberately not automatic: doing
    /// this at startup or on every settings change would restore maximized windows and drag a
    /// fullscreen game out of its display the moment anyone touched a spinner.
    /// </summary>
    public int ArrangeAll()
    {
        int moved = 0;

        foreach (var window in WindowList.Enumerate())
        {
            var hWnd = window.Handle;
            if (!IsClampTarget(hWnd)) continue;
            if (zones.Config.IsExcluded(OwnerProcess(hWnd), ClassNameOf(hWnd))) continue;
            if (!GetWindowRect(hWnd, out var rect)) continue;
            if (!ZoneManager.TryGetMonitor(hWnd, out var geo) || zones.IsOptedOut(geo)) continue;

            var rects = zones.ZonesFor(geo);
            if (rects.Count == 0) continue;

            Apply(hWnd, rects[ZoneManager.PickZoneIndex(rects, ReferenceRect(hWnd, rect))]);
            moved++;
        }

        Log.Write($"arrange: {moved} window(s) placed");
        return moved;
    }

    // ---- drag to zone ------------------------------------------------------

    private void DragBegin(IntPtr hWnd)
    {
        if (!zones.Config.DragToZone || Overlay is null) return;
        if (!IsClampTarget(hWnd)) return;
        if (zones.Config.IsExcluded(OwnerProcess(hWnd), ClassNameOf(hWnd))) return;

        // Ask the window itself what is under the cursor: a sizing border means the user is
        // resizing, and snapping that to a zone on mouse-up would destroy the resize. Testing
        // "did the size change?" instead would break maximized and cross-DPI drags, which change
        // size legitimately.
        if (GetCursorPos(out var start) && IsOnSizingBorder(hWnd, start)) return;

        dragWindow = hWnd;
        anchorZone = -1;
        dragDevice = "";
        dragTarget = null;
        dragSawFullscreen = false;

        // A deliberately pinned overlay must come back when the drag preview goes away.
        pinnedOverlay = Overlay is { Visible: true, Mode: OverlayMode.Display };

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

        // A process that dies inside its own move loop never sends MOVESIZEEND, which would leave
        // this timer running forever, re-showing a full-screen overlay on every modifier press.
        if (!IsWindow(dragWindow) || !zones.Config.DragToZone) { CancelDrag(); return; }

        // Never fight a deliberately opened overlay.
        if (Overlay.Visible && Overlay.Mode == OverlayMode.Edit) return;

        // The modifier is a live gate: release it mid-drag and the overlay gets out of the way.
        if (!ModifierHeld(zones.Config.DragModifier)) { HideDragOverlay(); ClearDragTarget(); return; }

        if (!GetCursorPos(out var cursor)) { ClearDragTarget(); return; }
        if (!ZoneManager.TryGetMonitorAt(cursor, out var geo)) { ClearDragTarget(); return; }

        // A monitor opted out of zoning must not inherit the last target from another one.
        if (zones.IsOptedOut(geo)) { ClearDragTarget(); return; }

        var rects = zones.ZonesFor(geo);
        if (rects.Count == 0) { ClearDragTarget(); return; }

        // Nearest rather than exact: with Padding on, the gutters between zones belong to no zone,
        // and losing the target every time the cursor crosses one makes the gesture feel broken.
        int hit = ZoneManager.ZoneAtOrNearest(rects, cursor);

        if (!Overlay.Visible || Overlay.Mode != OverlayMode.Drag) Overlay.Show(zones, OverlayMode.Drag);

        if (anchorZone < 0 || dragDevice != geo.Device) { anchorZone = hit; dragDevice = geo.Device; }

        // Holding the span modifier grows the target across every zone between anchor and cursor.
        bool span = SpanHeld(zones.Config.SpanModifier) && anchorZone < rects.Count;
        dragTarget = span ? ZoneManager.Union(rects[anchorZone], rects[hit]) : rects[hit];
        if (!span) anchorZone = hit;

        Overlay.Highlight(geo.Device, dragTarget?.Rect);
    }

    private void ClearDragTarget()
    {
        dragTarget = null;
        anchorZone = -1;
        Overlay?.Highlight("", null);   // clears every monitor; Hide() would tear down all modes
    }

    /// <summary>
    /// Hides the overlay only if it is the drag preview, and puts back a pinned Win+Alt+Z overlay
    /// that the preview replaced. Not restored on teardown: the hooks are going down with it.
    /// </summary>
    private void HideDragOverlay(bool restorePinned = true)
    {
        if (Overlay is not { Visible: true, Mode: OverlayMode.Drag }) return;

        Overlay.Hide();
        if (restorePinned && pinnedOverlay) Overlay.Show(zones, OverlayMode.Display);
    }

    private void CancelDrag()
    {
        dragTick.Stop();
        dragWindow = IntPtr.Zero;
        dragTarget = null;
        anchorZone = -1;
        dragDevice = "";
        dragSawFullscreen = false;
        pinnedOverlay = false;
        postDrag.Stop();
        postDragWindow = IntPtr.Zero;
        HideDragOverlay(restorePinned: false);
    }

    private void DragEnd(IntPtr hWnd)
    {
        var target = dragTarget;
        var dragged = dragWindow;

        bool sawFullscreen = dragSawFullscreen;

        dragTick.Stop();
        dragWindow = IntPtr.Zero;
        dragTarget = null;
        anchorZone = -1;
        dragSawFullscreen = false;
        HideDragOverlay();
        pinnedOverlay = false;

        // Whatever else happens, a boundary crossed by hand is a decision, not an accident.
        if (dragged != IntPtr.Zero && dragged == hWnd
            && GetWindowRect(hWnd, out var dropped) && ZoneManager.TryGetMonitor(hWnd, out var onto)
            && ZoneManager.SpansDisplays(dropped, onto))
        {
            allowedToSpan[hWnd] = 0;
            Log.Write($"0x{hWnd:X} dragged across displays; allowed to span");
        }

        if (dragged == IntPtr.Zero || dragged != hWnd || target is null)
        {
            ArmPostDrag(dragged, sawFullscreen);
            return;
        }
        if (!ModifierHeld(zones.Config.DragModifier)) { ArmPostDrag(dragged, sawFullscreen); return; }

        // A resize is not a move: only snap when the window actually travelled.
        if (GetWindowRect(hWnd, out var now) && now.Left == dragStartRect.Left && now.Top == dragStartRect.Top
            && now.Width == dragStartRect.Width && now.Height == dragStartRect.Height)
            return;

        Log.Write($"drag-to-zone: 0x{hWnd:X} -> {target.Value}");
        Apply(hWnd, target.Value);
    }

    /// <summary>
    /// Schedules a second look at a window that went fullscreen during its own drag — Aero-snapping
    /// it to the top edge, for instance. The drag guard deliberately ignored that transition, and
    /// no further event is guaranteed to arrive.
    /// </summary>
    private void ArmPostDrag(IntPtr hWnd, bool sawFullscreen)
    {
        if (!sawFullscreen || hWnd == IntPtr.Zero || !zones.Config.AutoClamp) return;

        postDragWindow = hWnd;
        postDrag.Stop();
        postDrag.Start();
    }

    /// <summary>A gate: "None" means no key is required, so it is always satisfied.</summary>
    private static bool ModifierHeld(string name)
    {
        var vk = SplitConfig.ModifierKey(name);
        return vk is null || IsKeyDown(vk.Value);
    }

    /// <summary>
    /// An opt-in: "None" means never, not always. Spanning every drag because the span modifier
    /// reads as "off" would be the exact opposite of what the setting says.
    /// </summary>
    private static bool SpanHeld(string name) =>
        SplitConfig.ModifierKey(name) is { } vk && IsKeyDown(vk);

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
    public void Apply(IntPtr hWnd, Zone zone)
    {
        Touch(hWnd);

        // Z-order is decided even when the rectangle is already right: a window can be in the
        // correct place and still be sitting underneath the taskbar.
        ApplyTopmost(hWnd, zone.CoverTaskbar);

        if (!ZoneManager.ClampToZone(hWnd, zone.Rect)) { Log.Write("  -> ClampToZone declined (already in place?)"); return; }
        clamps++;

        // The window now lives here and is no longer fullscreen, so this IS its last normal
        // position. Without this the next maximize resolves against the pre-move rectangle and
        // flies back to the zone it came from, permanently.
        lastNormal[hWnd] = zone.Rect;
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
        if (ZoneManager.ClampToZone(hWnd, verifyZone.Rect)) lastNormal[hWnd] = verifyZone.Rect;
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

    /// <summary>
    /// Drops any scheduled follow-up work. The verify timer holds a single pixel rectangle, which a
    /// config reload or an on-screen zone edit invalidates.
    /// </summary>
    public void CancelPending()
    {
        verify.Stop();
        verifyTarget = IntPtr.Zero;
        postDrag.Stop();
        postDragWindow = IntPtr.Zero;
        pendingShow.Clear();
        settle.Stop();
    }

    /// <summary>Periodic one-liner: the fastest way to see which stage is starving.</summary>
    private void Heartbeat()
    {
        long now = Environment.TickCount64;
        if (now - lastBeat < 5_000) return;
        lastBeat = now;
        Log.Write($"[beat] events={eventsSeen} targets={targetsSeen} fullscreen={fullscreenSeen} clamps={clamps} " +
                  $"hooks={(Healthy ? "up" : Running ? "PARTIAL" : "DOWN")}");
    }

    /// <summary>
    /// Raises a window over the taskbar, or puts it back. Only ever undone for windows this app
    /// raised: an app that was already always-on-top for its own reasons keeps that.
    /// </summary>
    private void ApplyTopmost(IntPtr hWnd, bool wanted)
    {
        bool have = madeTopmost.ContainsKey(hWnd);
        if (wanted == have) return;

        ZoneManager.SetTopmost(hWnd, wanted);
        if (wanted) madeTopmost[hWnd] = 0;
        else madeTopmost.TryRemove(hWnd, out _);
    }

    /// <summary>Drops every window we raised back to normal z-order.</summary>
    private void RestoreTopmost()
    {
        foreach (var hWnd in madeTopmost.Keys)
        {
            if (IsWindow(hWnd)) ZoneManager.SetTopmost(hWnd, false);
            madeTopmost.TryRemove(hWnd, out _);
        }
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
            foreach (var key in allowedToSpan.Keys)
                if (!IsWindow(key)) allowedToSpan.TryRemove(key, out _);
            foreach (var key in madeTopmost.Keys)
                if (!IsWindow(key)) madeTopmost.TryRemove(key, out _);
        }

        // WINEVENT_SKIPOWNPROCESS does not help here: we move *other* processes' windows,
        // so their LOCATIONCHANGE comes straight back at us.
        return recent.TryGetValue(hWnd, out long stamp) && now - stamp < zones.Config.DebounceMs;
    }

    // ---- filtering ---------------------------------------------------------

    public static bool IsClampTarget(IntPtr hWnd)
    {
        if (!IsWindow(hWnd) || !IsWindowVisible(hWnd)) return false;

        // A minimized window still reports visible, but its rectangle is the iconic (-32000,-32000)
        // placeholder. Recording that as a last-known position resolves every later maximize to
        // zone 0, and nothing ever heals it.
        if (IsIconic(hWnd)) return false;
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
