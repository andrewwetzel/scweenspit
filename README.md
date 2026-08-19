# ScweenSpit

A tray utility that splits each monitor into virtual sub-screens and keeps windows inside
them. Maximizing a window, or going borderless-fullscreen (Chrome F11, Chrome PWAs,
YouTube, VLC), fills only the sub-screen the window is in instead of the whole display.

## Get a binary

Every push to `main` builds on `windows-latest` and attaches `ScweenSpit.exe` to the run as an
artifact; tagging `v*` publishes it to a GitHub Release. The build is **win-x64, self-contained**
(~145 MB) — nothing to install, just run it.

```
gh release download <tag> -p '*win-x64.exe'
```

## Build

The .NET 8 SDK is all you need — and thanks to `EnableWindowsTargeting`, the project compiles
(and cross-publishes runnable Windows binaries) from Linux and macOS too. Only *running* it
needs Windows.

```
dotnet build -c Release          # typechecks anywhere
./scripts/publish.sh             # the Windows binary, from any OS
dotnet run -c Release            # Windows only
```

## Use

Left-click the tray icon to open the settings window. It has four pages — General, Layouts,
Exclusions, Diagnostics — and **closing it hides it back to the tray** rather than quitting; Exit
lives in the tray's right-click menu.

### Getting a window into a zone

Three ways, in rough order of how often you'll reach for them:

- **Drag it there.** Hold **Shift** while dragging a window and the zone overlay appears with the
  target zone lit up; drop to snap. Hold **Ctrl** as well to **span** — the target grows to cover
  every zone between where you started and where the cursor is now, so two columns become one wide
  slot. Both modifiers are configurable, and setting the drag modifier to *None* makes every drag
  snap.
- **Maximize it.** A window that maximizes or goes borderless-fullscreen gets clamped into whichever
  zone it was already living in.
- **Hotkeys.**

| Hotkey | Action |
| --- | --- |
| `Win+Alt+Left` / `Win+Alt+Right` | cycle the focused window through its monitor's zones |
| `Win+Alt+Z` | show/hide the zone overlay |

### Adjusting the zones

**Layouts → Drag dividers on screen…** puts the overlay into edit mode: every divider between zones
becomes a draggable handle, the layout reflows live as you drag, and it's saved when you let go.
`Esc` or a click on empty space finishes. Zones that share an edge move together, so a 70/30 split
stays a clean split — no gaps, no overlaps — and nothing can be squeezed below 5% of the display.

Presets are still there (70/30, thirds, quadrants and so on) if you'd rather not fiddle.

## Keeping Windows out of the way

Windows has its own opinions about window placement, and they compete with these zones. **Suppress
Windows snap** turns off the three that actually interfere:

| Setting | What it stopped doing |
| --- | --- |
| `SPI_SETWINARRANGING` | Aero Snap — dragging to an edge half-tiles the window |
| `SPI_SETSNAPSIZING` | edge-resize snapping |
| `SPI_SETDOCKMOVING` | dock-on-move |

These are **per-user system settings, not app settings**. Turning them off changes your desktop for
every app, so the previous values are written to `SnapRestore` in the config and put back on exit —
and, if the process is killed rather than closed, on the next launch. It defaults to off.

Two things it deliberately does *not* touch, because they need registry edits and an Explorer
restart: the Windows 11 **Snap Layouts** flyout (hovering the maximize button) and **Snap Assist**.
Turn those off in Settings → System → Multitasking if they bother you.

If you also run PowerToys FancyZones, disable one or the other — two tools racing to place the same
window will fight, and the loser retries.

## Config

`%APPDATA%\ScweenSpit\config.json`, written with defaults on first run.

```json
{
  "AutoClamp": true,
  "DebounceMs": 400,
  "Padding": 0,
  "SuppressWindowsSnap": false,
  "DragToZone": true,
  "DragModifier": "Shift",
  "SpanModifier": "Control",
  "Exclude": ["vlc", "mpv.exe", "UnityWndClass"],
  "Monitors": {
    "*": {
      "Zones": [
        { "L": 0.0, "T": 0.0, "R": 0.70, "B": 1.0 },
        { "L": 0.70, "T": 0.0, "R": 1.0, "B": 1.0 }
      ]
    },
    "\\\\.\\DISPLAY2": {
      "Zones": [
        { "L": 0.0, "T": 0.0, "R": 1.0, "B": 0.5 },
        { "L": 0.0, "T": 0.5, "R": 1.0, "B": 1.0 }
      ]
    }
  }
}
```

A zone is a fraction of the monitor's **work area** (taskbar already excluded), so one
shape covers columns, rows and grids. Keys are Win32 device names; `"*"` is the fallback
for any monitor without its own entry. Hand-edit and hit **Reload config** — no restart.
Zones are numbered in reading order: top-to-bottom, then left-to-right.

- Give a monitor a single full-size zone (`0,0,1,1`) to opt it out entirely.
- `Padding` insets every zone by that many pixels, so tiled windows don't touch.
- `Exclude` lists processes (with or without `.exe`) or window classes to leave alone — games and
  video players are the usual candidates, since forcing them out of fullscreen is rarely wanted.

An unreadable config is copied to `config.json.bad` before defaults replace it, so a typo never
costs you a hand-tuned layout.

## How it works

| File | Role |
| --- | --- |
| `NativeMethods.cs` | every P/Invoke, struct and constant |
| `SplitConfig.cs` | config model, JSON load/save, tray presets |
| `ZoneManager.cs` | monitor geometry, split math, the clamp |
| `WinEventHookService.cs` | WinEvent hooks, window filtering, reentrancy guard |
| `TrayApplicationContext.cs` | tray icon, menu, hotkey window |
| `ZoneOverlay.cs` | the zone visualiser: display, drag-target and edit modes |
| `ZoneEdges.cs` | shared-edge algebra that keeps an edited layout tiling |
| `SettingsForm.cs` / `Theme.cs` | the settings window and its dark palette |
| `WindowsSnap.cs` | suppressing Windows' own snap behaviour |
| `Startup.cs` | run-at-login registry entry |
| `Program.cs` | DPI awareness, single-instance guard, message loop |

Three details that this design lives or dies by:

- **Delegate lifetime.** The `WinEventDelegate` is held in a field *and* pinned with
  `GCHandle` for the life of the hook. user32 holds a raw pointer the GC knows nothing about.
- **Reentrancy.** `SetWindowPos` on another process's window echoes straight back as
  `EVENT_OBJECT_LOCATIONCHANGE`. Every window we touch is timestamped in a
  `ConcurrentDictionary` *before* the move and ignored for `DebounceMs` afterwards.
  Raise `DebounceMs` if you ever see a flicker.
- **Zone memory.** Once a window covers the whole monitor, its own rectangle no longer says
  which zone you had it in — the centre of a maximized window is always the centre of the
  screen. Each window's last non-fullscreen rectangle is remembered (falling back to
  `WINDOWPLACEMENT.rcNormalPosition`), so F11 in the 30% zone stays in the 30% zone.
- **One re-check, never a loop.** Some apps re-assert their fullscreen size just after we
  shrink them, during the debounce window where we are deliberately deaf. A single timer
  fires once after the debounce and corrects it. It never re-schedules itself, so a
  stubborn app costs one extra correction rather than a ping-pong fight.
- **Per-monitor geometry.** Zones come from `GetMonitorInfo().rcWork`, not
  `SystemInformation.WorkingArea` — the latter is primary-monitor-only and DPI-scaled,
  which breaks mixed-DPI multi-monitor setups. The process is PerMonitorV2 aware, so all
  Win32 rectangles are real physical pixels.

## Diagnostics

Logging is **on by default** — every startup, monitor layout, zone rectangle and clamp decision is
appended to `%APPDATA%\ScweenSpit\scweenspit.log` (also reachable from the tray menu). Set
`SCWEENSPIT_LOG=0` to silence it. It self-truncates past 5 MB and never throws.

A `[beat]` line every five seconds carries the counters that localise a fault:

```
[beat] events=1843 targets=52 fullscreen=1 clamps=1 hooks=up
```

`events=0` means the hook never fired. `targets=0` with events flowing means every window was
filtered out. `fullscreen=0` means nothing was recognised as maximized or borderless. `clamps=0`
with `fullscreen>0` means the move itself was refused.

## If nothing seems to happen

In rough order of likelihood:

1. **Check `AutoClamp` in the config.** `%APPDATA%\ScweenSpit\config.json` — if it says
   `"AutoClamp": false`, clamping is switched off and stays off across upgrades, because the config
   outlives the executable. Set it to `true`, or just delete the file to start fresh.
2. **Check the layout isn't "Full — leave this display alone".** A monitor whose layout is a single
   full-size zone is deliberately opted out.
3. **Check the tray tooltip.** Hover the icon: it reads either *clamping, N zones* or *clamping OFF*.
   That is the one status channel Focus Assist cannot suppress.
4. **Check for a second copy already running.** A single-instance mutex makes a second launch exit
   immediately and silently — the icon you can see may belong to an older build.
5. **Read the log.** `%APPDATA%\ScweenSpit\scweenspit.log`, or Diagnostics → Open log.

Note that an unelevated ScweenSpit cannot move windows owned by an elevated process — Windows
blocks it (UIPI). The log says so explicitly when it happens.
