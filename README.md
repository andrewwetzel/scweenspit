# ScweenSpit

A tray utility that splits each monitor into virtual sub-screens and keeps windows inside
them. Maximizing a window, or going borderless-fullscreen (Chrome F11, Chrome PWAs,
YouTube, VLC), fills only the sub-screen the window is in instead of the whole display.

## Get a binary

Every push to `main` builds on `windows-latest` and attaches the executables to the run; tagging
`v*` publishes them to a GitHub Release. Two of them:

| Download | Size | Use it when |
| --- | --- | --- |
| `ScweenSpit-Setup.exe` | a few MB | you are not sure whether you have .NET — it checks, offers to install the runtime, then runs the app |
| `ScweenSpit.exe` | ~250 KB | you already have the .NET 8 Desktop Runtime |

The app itself is a couple of hundred kilobytes. Bundling the runtime with it costs **63 MB**, almost
none of which is ours: `Microsoft.WindowsDesktop.App` ships WinForms and WPF as one indivisible pack,
so 37 MB of that is WPF this app never touches. Trimming would cut it right down, but `NETSDK1175`
forbids trimming for Windows Forms outright.

So the runtime is shared instead. `ScweenSpit-Setup.exe` is compiled ahead of time to native code —
it has to start on a machine with no .NET at all, which is the one thing the app itself cannot do.
It looks for `Microsoft.WindowsDesktop.App` 8.x, and if it is missing, asks first, downloads the
official installer from Microsoft, runs it (UAC will prompt), then unpacks the app to
`%LOCALAPPDATA%\ScweenSpit\bin` and starts it. On every later launch it finds the runtime, skips
straight to the end, and costs nothing.

If you would rather have the old no-dependencies build, it is one commented-out line in
`scripts/publish.sh`.

## Build

The .NET 8 SDK is all you need — and thanks to `EnableWindowsTargeting`, the project compiles
(and cross-publishes runnable Windows binaries) from Linux and macOS too. Only *running* it
needs Windows.

```
dotnet build -c Release          # typechecks anywhere
./scripts/publish.sh             # the Windows binary, from any OS
dotnet run -c Release            # Windows only
```

`ScweenSpit-Setup.exe` is the exception: Native AOT cannot be cross-compiled, so only the Windows
CI job builds it.

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
| `Win+Alt+S` | let the focused window span displays, or stop it |

### A zone that covers the taskbar

Zones normally live inside the work area, so they stop short of the taskbar — and an ordinary window
cannot draw over it anyway, because the shell is always-on-top.

Tick **Zone N: fill the whole display height, over the taskbar** under Layouts and that zone grows
out over the taskbar, with windows placed in it kept above it. The taskbar stays visible and usable
everywhere the zone does not reach.

This is what you want for a remote-desktop or VNC client — a Kasm PWA, say — that should be
genuinely fullscreen across the left 70% of a display while the taskbar remains available on the
right 30%. Set a 70/30 split, tick the box on zone 1, then let the app go fullscreen as normal
(`F11`); it gets clamped into the zone at full display height.

Only the outer edges grow. A zone already touching the left, top and bottom of the work area extends
to the display on those three sides, while the divider it shares with its neighbour stays exactly
where it was — so the two zones still meet, even with the taskbar on the left or top.

Windows raised this way are put back to normal z-order when they leave such a zone, and when
ScweenSpit exits.

### Keeping windows on one display

Some apps reopen at whatever rectangle they last remembered, which on a multi-monitor desktop often
means straddling two screens. **Keep windows on one display** (General, on by default) pulls those
back onto the display they mostly occupy, keeping their size and only moving them.

It distinguishes accidents from intent: a window you **drag** across a boundary yourself is exempted
for as long as it lives. `Win+Alt+S` toggles that exemption for the window in front, and anything on
the Exclusions page is never touched.

Newly opened windows are checked a third of a second after they appear rather than immediately —
an app still laying itself out should not be fought over its own opening position.

### Adjusting the zones

**Layouts → Drag dividers on screen…** puts the overlay into edit mode: every divider between zones
becomes a draggable handle, the layout reflows live as you drag, and it's saved when you let go.
`Esc` or a click on empty space finishes. Zones that share an edge move together, so a 70/30 split
stays a clean split — no gaps, no overlaps — and nothing can be squeezed below 5% of the display.

Presets are still there (70/30, thirds, quadrants and so on) if you'd rather not fiddle.

### Reserving space at the screen edges

Zones are laid out inside the Windows **work area**, so the taskbar is already excluded. When that
isn't the whole story — an auto-hiding taskbar, a third-party dock, a bezel you want to stay clear
of — each display takes its own **reserved margins** in pixels.

Set them numerically under Layouts, or drag the **orange outer edges** in the zone editor: the
reserved band is hatched, the zones reflow live into whatever is left, and margins that would leave
under 200px of usable space are ignored rather than obeyed.

Margins are per display and fork automatically, so adjusting one screen never moves the others.

### Moving the taskbar itself

The **Taskbar** page reports where the taskbar actually is (via the shell's `ABM_GETTASKBARPOS`) and
can dock it to any edge.

Be aware of what that costs: there is no public API for moving the taskbar, so it writes Explorer's
`StuckRects3` blob and restarts Explorer — the desktop blanks for a moment and open File Explorer
windows close. You're asked to confirm first.

Only the **primary** display's taskbar is moved; secondary-display bars live in a separate
undocumented blob and are deliberately left alone.

**On Windows 11 this will not work.** Microsoft removed taskbar repositioning in build 22000; the
setting is still written and Explorer still ignores it. The page says so up front rather than
letting you find out by restarting your shell for nothing — and if the bar does not move, it tells
you that too instead of leaving a button that looks like it worked.

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
  "KeepOnOneDisplay": true,
  "DragToZone": true,
  "DragModifier": "Shift",
  "SpanModifier": "Control",
  "Exclude": ["vlc", "mpv.exe", "UnityWndClass"],
  "Monitors": {
    "*": {
      "Zones": [
        { "L": 0.0, "T": 0.0, "R": 0.70, "B": 1.0, "CoverTaskbar": false },
        { "L": 0.70, "T": 0.0, "R": 1.0, "B": 1.0 }
      ],
      "Margins": { "Top": 0, "Bottom": 0, "Left": 0, "Right": 0 }
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

An unreadable config is copied to `config.json.bad` and **left in place** — the running settings
keep working and Reload tells you what happened, rather than one typo silently replacing every
layout, margin and exclusion you had.

Over-large margins are trimmed to leave 200px usable rather than being discarded, and each axis is
fitted independently, so an absurd left margin cannot void a perfectly good top one.

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
| `Taskbar.cs` | reading and relocating the Windows taskbar |
| `Program.cs` | DPI awareness, single-instance guard, message loop |
| `launcher/` | the native self-installing launcher (separate project) |

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
3. **Check the tray tooltip.** Hover the icon: it reads *clamping*, *drag-to-zone only*,
   *hotkeys only*, or *hooks DOWN*. That is the one status channel Focus Assist cannot suppress, and
   it distinguishes "you switched it off" from "the hooks failed to install".
4. **Check for a second copy already running.** A single-instance mutex makes a second launch exit
   immediately and silently — the icon you can see may belong to an older build.
5. **Read the log.** `%APPDATA%\ScweenSpit\scweenspit.log`, or Diagnostics → Open log.

Note that an unelevated ScweenSpit cannot move windows owned by an elevated process — Windows
blocks it (UIPI). The log says so explicitly when it happens.
