# ScweenSpit

A tray utility that splits each monitor into virtual sub-screens and keeps windows inside
them. Maximizing a window, or going borderless-fullscreen (Chrome F11, Chrome PWAs,
YouTube, VLC), fills only the sub-screen the window is in instead of the whole display.

## Build

Requires the .NET 8 SDK on Windows.

```
dotnet build -c Release
dotnet run  -c Release
```

Single file, no install:

```
dotnet publish -c Release -r win-x64
```

## Use

The tray menu has:

- **Auto-clamp** — master on/off. Off truly unhooks; nothing is intercepted.
- **Layout ▸ *monitor* ▸ preset** — per-monitor layouts, each independent
  (Full, 70/30, 30/70, 60/40, 50/50, Thirds, Top/Bottom, Quadrants).
  The list is rebuilt each time the menu opens, so hot-plugged displays show up.
- **Reload config** / **Open config file**
- **Exit**

Double-clicking the tray icon toggles auto-clamp.

`Win+Alt+Left` / `Win+Alt+Right` cycle the focused window through the zones of the monitor
it is currently on.

## Config

`%APPDATA%\ScweenSpit\config.json`, written with defaults on first run.

```json
{
  "AutoClamp": true,
  "DebounceMs": 400,
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

Give a monitor a single full-size zone (`0,0,1,1`) to opt it out entirely.

## How it works

| File | Role |
| --- | --- |
| `NativeMethods.cs` | every P/Invoke, struct and constant |
| `SplitConfig.cs` | config model, JSON load/save, tray presets |
| `ZoneManager.cs` | monitor geometry, split math, the clamp |
| `WinEventHookService.cs` | WinEvent hooks, window filtering, reentrancy guard |
| `TrayApplicationContext.cs` | tray icon, menu, hotkey window |
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

Set `SCWEENSPIT_LOG=1` before launching to append clamp decisions to
`%APPDATA%\ScweenSpit\scweenspit.log`. Off by default, and never throws.
