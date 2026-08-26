# ScweenSpit

A tray utility that splits each monitor into virtual sub-screens and keeps windows inside
them. Maximizing a window, or going borderless-fullscreen (Chrome F11, Chrome PWAs,
YouTube, VLC), fills only the sub-screen the window is in instead of the whole display.

## Get a binary

Every push to `main` builds on `windows-latest`; tagging `v*` publishes a release. There is one
download, **`ScweenSpit.exe`**, about 1.9 MB. Run it and you are done — it installs the .NET Desktop
Runtime first if your machine does not have it.

That size is the point. The app is ~250 KB; bundling the runtime with it costs **63 MB**, almost
none of which is ours. `Microsoft.WindowsDesktop.App` ships WinForms and WPF as one indivisible
pack, so 37 MB of that is WPF this app never touches, and `NETSDK1175` forbids trimming for Windows
Forms outright. So the runtime is shared instead, and the download carries only what is missing.

The exe you run is compiled ahead of time to native code, because an app that needs the runtime
cannot be the thing that checks for it. On launch it looks for `Microsoft.WindowsDesktop.App` 8.x in
the shared-framework folder — rather than shelling out to `dotnet`, which is exactly what may be
absent — and if it is missing, asks first, downloads Microsoft's official installer, and runs it
(Windows will prompt for permission). Then it unpacks the app to `%LOCALAPPDATA%\ScweenSpit\bin`
and starts it. Every later launch finds the runtime, finds the unpacked copy already current, and
goes straight through.

It downloads through `curl.exe` (in System32 since Windows 10 1803), falling back to urlmon. Using
`HttpClient` would have linked the managed socket and TLS stack in statically — 5.1 MB rather than
1.9 MB, for a download that happens at most once per machine.

Keep the file wherever you like: **Start with Windows** registers *it*, not the unpacked copy, so it
re-checks the runtime and repairs the unpacked app on every login.

## Build

The .NET 8 SDK is all you need — and thanks to `EnableWindowsTargeting`, the project compiles
(and cross-publishes runnable Windows binaries) from Linux and macOS too. Only *running* it
needs Windows.

```
dotnet build -c Release          # typechecks anywhere
./scripts/publish.sh             # the Windows binary, from any OS
dotnet run -c Release            # Windows only
```

The shipping exe is the exception: Native AOT cannot be cross-compiled, so only the Windows CI job
builds it. `scripts/publish.sh` produces the inner app alone.

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

### The menu on the bar

Clicking the ScweenSpit icon — on the bar, or in the notification area — opens the menu that has to
carry everything when the Windows taskbar is hidden, since there is nothing else to reach the app by.

**Layout for this display** switches the split on whichever display the pointer is on, with the
current one ticked. A layout dragged into shape matches no preset, so it says *Custom* and how many
zones it has rather than leaving nothing ticked, which reads as a menu that does not know.

**Hand back to Windows** is stock Windows in one click: taskbar shown and staying shown, nothing
clamped, no bars, snap and the minimise animation given back. Nothing is thrown away — the layouts
stay in the config, unenforced — and the item becomes **Take ScweenSpit's settings back up**, which
puts back exactly what was on before. That is the same thing an undocking profile does, reachable
without knowing that profiles exist.

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

A window in such a zone is held above everything **only while it is the one in front**. That
distinction matters more than it sounds: leaving it always-on-top permanently makes every other
window unreachable, because nothing ordinary can be raised past an always-on-top window — clicking
another application's taskbar button appears to do nothing at all. Covering the taskbar is only
wanted while the window is in use, which is the same moment the taskbar would otherwise be in the
way.

With the Windows taskbar hidden there is nothing to cover, so nothing is raised above anything.
Windows raised this way are put back to normal z-order when they lose focus, when they leave such a
zone, and when ScweenSpit exits.

### Following the displays

Docking and undocking is a different machine each time, and the settings that suit an ultrawide are
not the ones that suit a laptop panel. **Displays** saves a profile per arrangement — recognised by
the set of displays present, not by their order — and applies it when that arrangement comes back.

The arrangement is watched through `SystemEvents.DisplaySettingsChanged`, debounced, because a dock
reports several changes while it settles and everything downstream touches windows. Whether or not a
profile matches, the bars are re-applied: a display may have gone, and a bar reserving space on it
has to go with it.

Undocking to an arrangement with nothing saved for it leaves the settings alone — including a hidden
taskbar, which on a laptop with nowhere else to go is the difference between a working machine and
one you cannot reach anything from. So it says so, because the setting that fixes it is two clicks
away and impossible to guess.

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

### Handing the machine back

ScweenSpit changes things that outlive it: the shell's taskbar is hidden, its auto-hide state is
switched, Windows' snap settings are turned off, the minimise animation is turned off. All four are
put back when it exits, when Windows signs out, and on the next launch when the setting that asked
for them is off.

The restore now runs **first** in the teardown, before anything else is disposed, and each of the
four is independent — undoing four things where a failure in the first must not decide the fate of
the other three is the whole job. It used to run last, after six disposals.

If ScweenSpit is killed while the taskbar is hidden, the taskbar comes back on hover — Explorer's
auto-hide survives, the hide does not — but that is not the same as having it back. Run
`ScweenSpit.exe --restore` to put everything back without starting the app; it also switches off
hiding the taskbar, so the next ordinary launch does not immediately undo the rescue. Task Manager's
**Run new task** opens on Ctrl+Shift+Esc with no taskbar at all, which is what makes that reachable.

An unreadable `config.json` is never written over. The file holds the only record of what the
machine looked like before ScweenSpit touched it, and one spinner nudged after a failed load would
put defaults on top of it and make the changes permanent. Saving is switched off for that session
instead.

### Running one copy

A second copy exits rather than fighting the first over the same reservations — but it says so now,
naming the executable and version that holds the machine. Silence was the wrong answer: the copy
already running is often an older one started at login, and a newly downloaded exe that exits
without a word looks exactly like an exe that does not work.

**Start with Windows** writes the path of the copy that turned it on. Download the next version
somewhere else and every login goes on starting the old one, which then holds the machine against
the new — while the switch reads as off, because the path no longer matches, so nothing looks wrong
until you go looking. The registration is now re-pointed at startup to whichever copy is running.

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

**On Windows 11 this will not work.** Microsoft removed taskbar repositioning in build 22000 — the
code that drew a taskbar on the top or sides is gone from the shell. The `StuckRects3` edge byte is
still readable and writable, and Explorer simply ignores it. No registry edit brings it back. The
page says so up front rather than letting you find out by restarting your shell for nothing, and if
the bar does not move it tells you that too, instead of leaving a button that looks like it worked.

What does work on Windows 11:

- **Hide the taskbar until I reach for it** — auto-hide, on the same page. This one is a supported
  API (`ABM_SETSTATE`), applies immediately with no restart, and was never removed. Pair it with a
  zone set to cover the taskbar and you get the whole display, with the bar on demand.
- **Reserved margins** (Layouts) to keep a strip clear on any edge, for a third-party dock.

### A bar of our own

Windows will not put *its* taskbar anywhere but the bottom. Nothing stops us docking one of ours to
any edge — which is exactly how the commercial tools do it.

Turn one on per display under **Taskbar** — each display is a separate switch, so you can have a bar
on one screen and nothing on the others. Pick an edge and a thickness; it defaults to 50px of icons,
the proportions of a real taskbar.

**Across** decides how much of the edge it takes. *Whole display* is the default. On an ultrawide
split into sub-screens, pick a zone instead and the bar occupies only that zone's edge — a bar along
the bottom of the right-hand 40%, say, with the left 60% untouched.

A zone-scoped bar is placed by ScweenSpit rather than reserved from Windows, because a work area is
one rectangle per display and part of an edge cannot be expressed in it. The zone is shortened for
the bar instead, so windows ScweenSpit places keep clear of it; anything maximised by Windows itself
will not.

Because such a bar is measured from its zone, dragging a divider moves it: the bar re-fits as soon
as the drag is committed, and the windows around it are reflowed out of wherever it landed.

It registers as a Win32 **appbar**, the same mechanism the shell's own taskbar uses, so Windows
shrinks the work area and every application on the machine keeps clear of it — not just windows
ScweenSpit manages. Zones follow automatically, because they are measured from the work area.

**Float clear of the edges** makes it a free-standing dock: it sits inside its reserved strip with a
gap all round and every corner rounded. The gap is added to the thickness rather than taken out of
it, so a 50px bar is 50px whether it floats or not. The space stays reserved either way — only the
bar moves in, so nothing creeps underneath it. Docked instead, it rounds the corners facing the desktop and keeps
the ones against the screen edge square, which is how Windows 11 treats a docked surface.

The gap is three numbers rather than one. **Gap on the open side** and **Gap at the ends** are the
three sides you see, and match by default, because an even border reads as one deliberate decision
rather than three separate ones. **Gap from the screen edge** is the docked side and is smaller:
whatever that edge already holds — a band Windows still reserves, the bezel, the rounded corner of
the panel — sits directly beneath it, so a gap equal to the others looks about twice as wide. Only
the open side and the docked one cost reserved space; the ends run along an edge that was spanned
end to end anyway.

The defaults have come down twice since bars were added, and a config written by an older build
still holds the old numbers. They are brought down on load, once per revision, rather than leaving
the change to reach new installs only. Each step only ever lowers a gap, so a smaller one you chose
yourself is never raised to meet it — and a number set after the migration stays set.

**Start button** puts a Windows logo at the leading end, where the shell's own Start button sits. It
opens the real Start menu, by the Ctrl+Esc the shell has always answered rather than by poking at a
taskbar window ScweenSpit may have hidden.

Windows then opens that menu wherever it believes its taskbar to be — the primary display's bottom
edge, whether or not the shell's bar is still shown there, and nowhere near a bar of ours on another
screen.

**On Windows 11 build 26200 and later this cannot be fixed from outside the shell, and the switch
that tries is off by default.** The menu's window can be found, moved, and will report its new
rectangle for as long as you care to ask — while what is drawn stays exactly where the shell put it.
The window and the pixels are no longer the same thing, so there is nothing left to move. What
remains would mean loading code into the shell's own process to intercept its positioning, which is
a different category of thing than this program does, and it would break on every Windows update.

The switch is kept because it did work on the builds it was written against. **Open the Start menu
over the button** moves the menu after the fact: the menu is an ordinary top-level window belonging to
StartMenuExperienceHost, whatever is drawn inside it. It is held there for as long as it stays
open, rather than placed once and left: Windows re-asserts its own position more than once while the
menu animates in, and a fixed hold that ends before the last of those loses to it silently, since by
then nothing is watching. Both ends are bounded — a menu left open all afternoon does not leave a
timer running, and a shell determined to have its own way is not fought to a flicker.

The keyboard's Windows key opens it in that same wrong place, so the menu is followed however it was
asked for rather than only when the button is clicked. That means watching one process — a hook
scoped to the shell that draws the menu, not to the desktop, since `EVENT_OBJECT_SHOW` across the
whole machine is one of the busiest events there is and none of the rest of it could ever be the
Start menu. With bars on several displays it opens on the one the pointer is on.

The window is identified by taking the foreground. Naming the window class, the caption, and then
the process that owns it each turned out to be naming that year's implementation — on Windows 11
build 26200 the menu is not an ordinary window of `StartMenuExperienceHost` at all. What cannot
change is that opening the menu moves the focus, so the menu is whatever comes to the front, is
bigger than a helper window, and belongs to an application under `%WINDIR%\SystemApps`. One hook,
on foreground changes only — a handful of events a minute, rather than `EVENT_OBJECT_SHOW` firing
for every tooltip on the machine.

When that misses, Diagnostics reports what was in front instead, since the useful thing to record
about a guess that missed is what was actually there.

All of this is undocumented, hence the switch: turn it off and the menu opens where Windows put it.
**Diagnostics** reports what happened last time it opened — whether the window was found, whether
Windows allowed the move, and where it went — because from the outside every one of those failures
looks identical. **Open the Start menu and report** does it on demand.

Applications are told apart by the id their windows declare, not by their executable — which is how
a Chrome PWA gets its own icon rather than disappearing into the browser's, despite both being
`chrome.exe`. It is the same mechanism the Windows taskbar groups by. Where a window declares no id,
the executable is used instead.

That id is also where the icon and the name come from when the window has neither. A PWA window
carries no icon to ask for and its executable belongs to the browser, so both of the ordinary
answers are wrong — one blank, the other Chrome's. The shell is asked what the id looks like and
what it is called instead, which is what the Start menu shows for the same application. Failing
even that, a lettered tile named after the application rather than after its host: a stand-in
reading "C" beside the browser's own "C" identifies nothing.

Icons are also asked for again while a window is only wearing a stand-in. Applications commonly set
their icon a moment after the window appears, and the first look — which used to be the only look —
is often too early.

Clicking a button brings its window past whatever is maximised. Windows only lets a process hand the
foreground to a window if it already holds the foreground or was last to receive input, and a taskbar
holds neither — its own window is deliberately never activated. So the request is refused and the
window rises only within its own z-order band, which looks exactly like nothing happening until you
minimise whatever is on top. ScweenSpit briefly attaches its input queue to the outgoing foreground
window's thread, which makes the call legitimate, and detaches immediately afterwards.

ScweenSpit's own settings window is listed like any other application, which matters once the Windows
taskbar is hidden: it would otherwise be the one window with no way back to it.

The Start glyph is drawn into the same box an application icon gets, at the same size, so the row
reads as one row — a couple of pixels out of line is invisible anywhere else on a bar and obvious
here. It shows one icon per application, the way a Plasma task manager does — six Chrome windows are one
thing you switch to, not six. The underline splits into a segment per open window, so a glance says
both *running* and *how many*, and clicking walks through them rather than always raising the same
one. A single window still toggles: raise it, click again to put it away.

Resting on a button shows **live previews** — one tile per window in the group, each a
desktop-composited view of the real window rather than a screenshot, so it keeps updating while you
look at it. This is the answer to the question grouping asks: a window title is a poor way to tell
six Chrome windows apart, and that is exactly the case grouping creates. Click a tile to raise that
window, middle-click to close it. Minimised windows have nothing to compose, so they show their icon
instead — a blank rectangle would read as a broken preview.

Right-click a button for the windows themselves, a new window, pin or unpin, and close. The window
list only appears when there is more than one, since walking a group one click at a time is fine for
two and useless for six.

It accents the foreground application and fades one whose windows are all minimised. Hovering names the window; clicking brings it forward, or minimises it if it is already in
front. Turn off *Icons only* for titles as well, and give it more room to put them in.

**Import from the Windows taskbar** (Settings → Taskbar) copies whatever is pinned there onto the
bar, in the same order. The pins themselves are ordinary shortcuts in the Quick Launch folder, so
reading them is straightforward; the order is not, since Explorer keeps it in an undocumented blob of
shell item identifiers. Rather than parse that, ScweenSpit looks for each executable's name inside it
and sorts by where it first appears — close enough, and it degrades to alphabetical when a name is
not found. Store apps are skipped and counted: they are pinned as an application id rather than a
file, so there is nothing to launch or take an icon from.

**Right-click any button to pin it.** A pinned application keeps its place whether or not it is
running: faded and unmarked when it is not, and clicking launches it. Everything unpinned follows in
the order it appeared, and stays there — `EnumWindows` reports z-order, which changes every time
anything is activated, so using it directly makes the buttons reshuffle under the pointer. Right-click
also closes a window.

A pin is matched to a running application in two passes: an exact match on the declared application
id wins outright, and only then may a pin that is a plain executable claim an application by its
file. Each application is claimed once. That ordering is what lets a pinned browser and a pinned web
app — both `chrome.exe` — end up on their own buttons, rather than one of them sitting there as
permanently not running beside the other.

A pinned application identified by an id rather than a file has nothing to take an icon from until it
runs, so it shows its initial on a disc until then.

**Stop windows flying into the taskbar when minimised** is on the same page. With the taskbar hidden
Windows still animates a minimised window towards where its button would have been — a corner with
nothing in it — which reads as a glitch. It is a system setting, so it is restored on exit.

**Show Claude usage bars** puts the claude.ai usage strip at the near end of the status cluster, a
row per limit with its own figure beside it — one headline percentage cannot say which limit it
belongs to, and bars without numbers cannot say how full they are. It
appears as soon as you tick it, showing an empty track until usage tracking is switched on and a
session key is saved under Settings → Claude usage — hiding it until then made the toggle look
broken. Clicking it goes to the settings page while it is unconfigured, and to claude.ai once it is.

The status cluster at the far end carries the **ScweenSpit button**, then **volume, network, battery
and a clock**. Each opens the matching Windows settings page; ScweenSpit opens its own menu, which
matters once the Windows taskbar is hidden — our tray icon goes with it, and without this there
would be no way back to Settings or Exit. They are drawn as vector shapes rather than glyph-font characters,
because which icon font exists and which code point means what varies by Windows version, and a
missing glyph renders as a hollow box with no warning.

Two identical screens look the same in a list of device names, so each display is labelled with its
position and there is an **Identify displays** button that flashes a big number on each — the same
idea as the one in Windows' own display settings.

**Hide the Windows taskbar entirely** is on the same page. It takes the shell's bars off screen and
gives their reserved strip back, so a bar of yours on the same edge sits flush rather than floating
above a dead band — and it re-asserts, because Explorer puts an auto-hidden taskbar back whenever the
pointer reaches its edge. Everything is restored when ScweenSpit exits, and on the next launch if
this one is killed.

Hiding the Windows taskbar also hands its space to the zones. Windows keeps reporting a reduced work
area even with the bar hidden — the appbar registration outlives the window — so ScweenSpit measures
against the full display instead once you have hidden it. Otherwise a bar docked to the bottom would
float above a strip of dead space where the taskbar used to be.

Windows are placed so their **visible** edges land on the zone. `GetWindowRect` includes an invisible
resize border that DWM does not paint — around 7px at the left, right and bottom of an ordinary
Windows 11 window — so a window placed at a zone's rectangle appears inset by that much on three
sides. ScweenSpit measures the painted frame (`DWMWA_EXTENDED_FRAME_BOUNDS`) and grows the target to
compensate, which is why tiled windows here touch each other and the screen edge instead of floating
in a moat of unused pixels.

Growing a bar takes room from its zone, and windows already sitting there do not move on their own,
so ScweenSpit re-places the ones the bar would cover. It waits until you stop adjusting first — a
thickness spinner fires on every step, and re-placing everything on each one is unpleasant to watch.

### Claude usage in the bar

The status cluster can carry your **claude.ai usage limits**: the session (5-hour) limit, the weekly
one, and the weekly per-model limit, drawn as three thin bars with the session percentage above them.
Each bar is coloured by its own consumption — blue, amber past half, red past 85% — so three of them
read at a glance without a legend. Hovering gives the figures and when each one resets; clicking
opens the usage page on claude.ai.

Set the account up under **Claude usage**, then switch the bars on per display under **Taskbar** —
on a multi-monitor desk the same strip repeated on every bar is noise rather than information.

It needs your claude.ai **session cookie**, which you copy out of the browser: sign in to claude.ai,
press F12, and take the value of the `sessionKey` row under Application ▸ Cookies. It expires every
few weeks, so this needs redoing occasionally — the strip says `key` when it does.

That cookie is your whole claude.ai account, not just its usage figures, so it is treated as a
credential rather than as a setting: **DPAPI-encrypted to your Windows account before it reaches
`config.json`**, never written to the log, and passed to the curl fallback on stdin rather than in a
command line other processes can read. Copying `config.json` to another machine leaves the key
behind, unreadable.

Nothing is requested while the feature is off. With it on, ScweenSpit polls claude.ai every three
minutes by default — the figures move slowly and it is somebody else's server. claude.ai rotates the
session cookie from time to time and the replacement is stored automatically; without that, a key
that was working quietly stops.

This is adapted from [claude-usage-widget](https://github.com/niccolo-sabato/claude-usage-widget) by
Niccolò Sabato, under the MIT licence — see [Third-party code](#third-party-code).

### Why "fill over the taskbar" sometimes does nothing

That setting grows a zone into the space the taskbar reserves. If nothing is reserving space — the
taskbar is hidden, or set to auto-hide — there is nothing to grow into and the setting has no visible
effect. The Layouts page says so when it detects that.

It also only applies when a window is next placed. **Arrange open windows now**, on the same page,
applies the current layout to everything already open, so you can see a change without hunting for a
window to maximize.

### What our bar cannot do

**Third-party notification icons.** Discord, Steam, backup agents — anything that calls
`Shell_NotifyIcon` — send their icons to the window of class `Shell_TrayWnd`, which belongs to
Explorer. There is no supported way for another process to receive them; a program can only have
them by *being* the shell, which means replacing Explorer outright. So hiding the Windows taskbar
hides those icons with it.

If you need them, use **auto-hide** instead of **hide**: the shell bar stays one mouse-move away at
its edge, while your bar owns the space the rest of the time.

Deciding what belongs on a taskbar is the same judgement Alt+Tab makes, and it is fiddlier than it
looks: owned dialogs, tool windows, and the cloaked husks suspended UWP apps leave behind all have
to be filtered out. A window qualifies when it is the last active popup of its own root owner.

The reservation is released when ScweenSpit exits. That matters more than it sounds — an appbar
registration that outlives its process leaves the desktop permanently short of that strip until you
sign out.

If you want the *Windows* taskbar itself moved, that still takes a shell modification —
ExplorerPatcher, a Windhawk mod, StartAllBack, or a replacement such as RetroBar. Those patch or
replace Explorer and tend to break on feature updates; that is the trade, and it is outside what
this tool should be doing to your machine.

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
  "Claude": {
    "Enabled": false,
    "SessionKey": null,
    "OrgId": null,
    "RefreshSeconds": 180,
    "ShowWeekly": true,
    "ShowModel": true
  },
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
- `Claude` configures the usage strip. `SessionKey` holds DPAPI ciphertext, not the key — paste the
  key in Settings rather than here, since only ScweenSpit can encrypt one that this machine will
  read back. `OrgId` is worked out from the key on first poll and cached; clear it to re-resolve.

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
| `Taskbar.cs` | reading, relocating and auto-hiding the Windows taskbar |
| `AppBar.cs` | Win32 appbar registration — reserving screen space system-wide |
| `TaskbarWindow.cs` / `BarManager.cs` | our own dockable taskbar |
| `WindowList.cs` | deciding which windows belong on a taskbar |
| `ClaudeUsage.cs` / `UsageStrip.cs` | claude.ai usage: polling, and the bars it draws |
| `Secret.cs` | DPAPI, so a credential never sits in the config in plain text |
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

## Docking and undocking

**Settings → Displays.** Set things up the way you want them for the arrangement you are in, then
**Remember this arrangement**. Do the same undocked, and each one comes back with its hardware — the
Windows taskbar restored on the laptop panel, hidden again at the desk.

**Remember it with ScweenSpit standing down** is the one you want for the laptop screen: clamping,
drag-to-zone, keeping windows on one display and our own bars all off, the Windows taskbar back on
screen and out of auto-hide, Aero Snap and the minimise animation handed back. Undock, and the
machine behaves the way Windows behaves on its own.

A profile otherwise captures the current state: clamping, drag-to-zone, keeping windows on one
display, the Windows taskbar and whether it auto-hides, our bars, snap suppression and the minimise
animation. Anything it does not name is left alone.

Auto-hide is a setting ScweenSpit holds rather than one it only pushes at Windows. That distinction
matters: a setting with no record of it cannot be part of a profile, which is why undocking used to
leave a taskbar that came back only when the pointer reached the edge.

Zone layouts and bars need no profile — they are already stored per display, so they follow on their
own. A bar configured for a monitor simply is not there when that monitor is not.

An arrangement is recognised by how many displays are attached and at what sizes, not by their device
names: Windows reassigns `\\.\DISPLAY`*n* between docks, so a name-based key would fail at exactly
the moment it mattered. The cost is that two different displays of the same size look alike.

The display change is also what prompts a general tidy-up — a bar reserving space on a monitor that
has just been unplugged goes with it. Nothing else in the app watches for that.

## Updating

**Settings → Updates → Check and update.** One button: if there is a newer release it downloads it,
replaces the `ScweenSpit.exe` you keep, and restarts. Finding out that an update exists and then
being asked whether to have it is a question with one sensible answer, and it was being asked every
time. The release notes go to the log rather than the window, since the window is about to be
replaced along with everything else.

This is simpler here than it usually is, because of the shape of the thing: the file you keep is the
native launcher, and it exits the moment it has started the app. So the file being replaced is never
the file that is running — no rename dance, no helper process, no reboot. The old copy is kept as
`ScweenSpit.exe.previous` in case the new one will not start.

The download is checked against a SHA-256 published beside it in the release. That catches a
truncated or corrupted transfer. It is **not** a signature and does not prove the release is genuine
— there is no code signing here.

Installing hands the new launcher this process's id, and it waits for us to exit **before unpacking
anything** — Windows refuses to overwrite a running executable image, and reports that refusal as
access denied rather than a sharing violation, so both have to be handled or it surfaces as a crash.
That matters more than it sounds: the app runs from the unpacked copy the new launcher needs to
replace, so a launcher that pressed on regardless would find the file locked, keep the old payload,
see an instance already running, and quietly exit — leaving the update on disk and nothing running.
A launcher handed no process id still waits a few seconds before assuming a running copy means to
stay, so an update from a version predating this still lands. If the unpacked copy cannot be replaced
at all, an existing one is run rather than failing outright, and the reason is written to
`%APPDATA%\ScweenSpit\launcher.log`.

On startup it looks for a newer release at most once a day and, if it finds one, says so and stops
there. It never installs anything on its own.

Updates come from the GitHub Releases feed of whatever repository is named on that page. A public
repository needs no configuration. A private one needs an access token, which is stored as plain
text in `config.json` — the page says so.

Running the unpacked copy in `%LOCALAPPDATA%\ScweenSpit\bin` directly rather than the file you
downloaded means there is nothing to replace, and the page says that too.

## Third-party code

The claude.ai usage strip is derived from
[claude-usage-widget](https://github.com/niccolo-sabato/claude-usage-widget) by Niccolò Sabato, used
under the MIT licence. What is taken from it is the protocol knowledge — which endpoints carry the
figures, the headers they expect, how an organisation is chosen, the shape of the reply, and the
session-cookie rotation that has to be written back — plus the colour scale the bars are drawn on.
The code itself is ours; the original is Python/tkinter and draws its own window.

The full licence text, and a point-by-point account of what was derived, is in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). If the usage strip is useful to you, the upstream
project is worth a star — and it does a good deal more than this strip does, including notifications,
multiple accounts and a browser extension that fetches the key for you.

ScweenSpit is not affiliated with, endorsed by, or sponsored by Anthropic.

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
