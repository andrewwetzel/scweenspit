using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScweenSpit;

/// <summary>A zone as a fraction of a monitor's work area. Covers columns, rows and grids alike.</summary>
public sealed class FracRect
{
    public double L { get; set; }
    public double T { get; set; }
    public double R { get; set; } = 1;
    public double B { get; set; } = 1;

    /// <summary>
    /// Lay this zone out against the whole monitor rather than the work area, and keep windows in
    /// it above the taskbar. For a genuinely fullscreen pane on part of a display: the taskbar
    /// stays visible and usable everywhere this zone does not reach.
    /// </summary>
    public bool CoverTaskbar { get; set; }

    public FracRect() { }
    public FracRect(double l, double t, double r, double b, bool coverTaskbar = false)
    { L = l; T = t; R = r; B = b; CoverTaskbar = coverTaskbar; }
}

/// <summary>Space reserved at the edges of a display, in physical pixels.</summary>
public sealed class Margins
{
    public int Top { get; set; }
    public int Bottom { get; set; }
    public int Left { get; set; }
    public int Right { get; set; }

    [JsonIgnore]
    public bool Any => Top != 0 || Bottom != 0 || Left != 0 || Right != 0;

    public Margins Copy() => new() { Top = Top, Bottom = Bottom, Left = Left, Right = Right };

    /// <summary>Smallest usable strip a display may be reduced to, in pixels.</summary>
    public const int MinUsable = 200;

    /// <summary>
    /// The margins actually applied, trimmed to leave <see cref="MinUsable"/> in each axis.
    /// Trimming the facing pair proportionally beats discarding all four: one over-large value
    /// should not silently void a perfectly good margin on the other axis.
    /// </summary>
    public Margins Fitted(int width, int height)
    {
        var (l, r) = FitPair(Left, Right, width);
        var (t, b) = FitPair(Top, Bottom, height);
        return new Margins { Left = l, Right = r, Top = t, Bottom = b };
    }

    private static (int Near, int Far) FitPair(int near, int far, int extent)
    {
        near = Math.Max(0, near);
        far = Math.Max(0, far);

        int budget = extent - MinUsable;
        if (budget <= 0) return (0, 0);

        int total = near + far;
        if (total <= budget) return (near, far);

        // Proportional, floored at zero: subtracting the overflow from one side alone drives the
        // other negative whenever it exceeds the budget by itself, putting zones off the monitor.
        int scaled = (int)Math.Floor(near * (double)budget / total);
        return (Math.Clamp(scaled, 0, budget), budget - Math.Clamp(scaled, 0, budget));
    }
}

/// <summary>A ScweenSpit taskbar docked to one edge of one display.</summary>
public sealed class BarSettings
{
    /// <summary>Left, Top, Right or Bottom.</summary>
    public string Edge { get; set; } = "Right";

    /// <summary>Width of a side bar, or height of a top/bottom one, in pixels.</summary>
    public int Thickness { get; set; } = 50;

    /// <summary>
    /// Sit clear of the edges with every corner rounded, the way a floating dock looks, rather than
    /// flush against the screen. The space is still reserved either way — only the bar moves in.
    /// </summary>
    public bool Floating { get; set; }

    /// <summary>How far a floating bar sits from the side facing the desktop, in pixels.</summary>
    public int FloatMargin { get; set; } = 3;

    /// <summary>
    /// The gap at the two ends of a floating bar. The same as <see cref="FloatMargin"/> by default:
    /// three sides of one rectangle, and an even border is what makes it read as deliberate rather
    /// than as three separate decisions.
    /// </summary>
    public int SideGap { get; set; } = 3;

    /// <summary>
    /// The gap on the docked side only. Kept apart from <see cref="FloatMargin"/> because that side
    /// is not symmetric with the others: whatever Windows already reserves at the screen edge sits
    /// underneath, so an equal gap there reads as roughly twice the one above.
    /// </summary>
    public int EdgeGap { get; set; } = 2;

    /// <summary>
    /// Applications kept on the bar whether or not they are running, in the order they appear.
    /// Stored as full executable paths.
    /// </summary>
    public List<string> Pinned { get; set; } = new();

    /// <summary>A Start button at the leading end, opening the Windows Start menu.</summary>
    public bool ShowStartButton { get; set; } = true;

    /// <summary>
    /// Move the Start menu over the button that opened it. Windows opens it wherever it believes its
    /// own taskbar to be, which need not be this display or this edge.
    ///
    /// Off by default because on Windows 11 build 26200 it does nothing: the menu's window can be
    /// found and moved, and reports the new rectangle afterwards, while what is drawn stays where
    /// the shell wanted it. The window and the pixels are no longer the same thing. Left in as a
    /// switch rather than removed, since it did work on the builds it was written against.
    /// </summary>
    public bool MoveStartMenu { get; set; }

    /// <summary>
    /// Live previews when the pointer rests on a button. Grouping puts six windows behind one icon,
    /// and a list of titles is a poor way to tell six of the same application apart.
    /// </summary>
    public bool ShowPreviews { get; set; } = true;

    /// <summary>
    /// The machine's own load beside the Claude limits: processor, memory, and how full the system
    /// drive is. Off by default — a taskbar is not a monitoring tool unless asked to be.
    /// </summary>
    public bool ShowMachine { get; set; }

    /// <summary>
    /// A count on an icon's corner when an application is announcing one in its window title.
    /// </summary>
    public bool ShowBadges { get; set; } = true;

    /// <summary>Icons alone, the way a real taskbar looks. Titles need roughly four times the room.</summary>
    public bool IconsOnly { get; set; } = true;

    /// <summary>Show battery, network, volume and the clock.</summary>
    public bool ShowStatus { get; set; } = true;

    /// <summary>
    /// Show the claude.ai usage bars in the status cluster. Per-bar rather than global: on a
    /// multi-display setup the same strip repeated on every bar is noise, not information.
    /// Has no effect until an account is configured — see <see cref="ClaudeSettings"/>.
    /// </summary>
    public bool ShowUsage { get; set; }

    /// <summary>
    /// Show the Antigravity model quota bars next to the Claude ones in the status cluster.
    /// Per-bar for the same reason <see cref="ShowUsage"/> is.
    /// </summary>
    public bool ShowAntigravity { get; set; }

    /// <summary>
    /// Show the Bambu Lab printer status bars next to the AI usage strips in the status cluster.
    /// Per-bar for the same reason <see cref="ShowUsage"/> is.
    /// </summary>
    public bool ShowBambu { get; set; }

    /// <summary>
    /// Show live Claude Code and Antigravity remote server chat counts and states next to the AI
    /// usage strips in the status cluster. Per-bar for the same reason <see cref="ShowUsage"/> is.
    /// </summary>
    public bool ShowRemoteChats { get; set; }

    /// <summary>
    /// Automatically stretch the enabled status/usage tracker strips into unused horizontal space on
    /// a wide bar so their progress tracks and figures are larger and easier to read at a glance.
    /// </summary>
    public bool ExpandTrackers { get; set; } = true;

    /// <summary>
    /// Base width scale percentage for each tracker column (80%–300%). Default 125% gives longer
    /// progress bars and breathing room on wide taskbars even before automatic expansion.
    /// </summary>
    public int TrackerScale { get; set; } = 125;

    /// <summary>
    /// Maximum meter rows stacked vertically in a single column on a horizontal bar before wrapping
    /// into side-by-side columns (1–6). Default 3 keeps 5 Bambu printers or multiple remote servers
    /// in 2 columns of 3 rows instead of cramping 5+ tiny rows into one column.
    /// </summary>
    public int TrackerMaxRows { get; set; } = 3;

    /// <summary>
    /// Custom pixel width (or height on a vertical bar) for each tracker strip when dragged on the
    /// taskbar. Null uses automatic sizing.
    /// </summary>
    public int? RemoteChatsWidth { get; set; }
    public int? UsageWidth { get; set; }
    public int? AntigravityWidth { get; set; }
    public int? BambuWidth { get; set; }
    public int? MachineWidth { get; set; }

    /// <summary>List only windows on this display, rather than every window everywhere.</summary>
    public bool ThisDisplayOnly { get; set; } = true;

    /// <summary>
    /// Confine the bar to one zone instead of the whole display edge — for an ultrawide split into
    /// sub-screens, where a bar spanning all of it is not what anyone wants. Null spans the display.
    ///
    /// A zone-scoped bar cannot be an appbar: Windows reserves space as a single rectangle per
    /// monitor, so a partial-width strip is not expressible. The zone is shortened instead, which
    /// keeps windows ScweenSpit places clear of it.
    /// </summary>
    public int? Zone { get; set; }
}

/// <summary>
/// Everything the claude.ai usage strip needs. The account this points at is read-only to us: we
/// ask for usage figures and nothing else.
/// </summary>
public sealed class ClaudeSettings
{
    /// <summary>Master switch. Off means the poller never starts and no request is ever made.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Use the OAuth credentials Claude Code keeps in ~/.claude/.credentials.json when signed in on
    /// this machine. Claude Code renews that token itself as you use it, so unlike a browser cookie
    /// it does not quietly expire every few weeks. When both this and <see cref="SessionKey"/> are
    /// available, this is tried first and the session key is kept as a fallback.
    /// </summary>
    public bool UseClaudeCode { get; set; } = true;

    /// <summary>
    /// The claude.ai session cookie, encrypted to this Windows account by DPAPI — never the raw
    /// key. It is a whole-account credential and this file is hand-edited and pasted into bug
    /// reports, so it must not be legible here. See <see cref="Secret"/>.
    /// </summary>
    public string? SessionKey { get; set; }

    /// <summary>
    /// Optional Claude Code OAuth refresh token encrypted to this Windows account by DPAPI (from
    /// "Sign in with Claude" or mirrored from ~/.claude/.credentials.json) so ScweenSpit can renew
    /// expired access tokens and query remote Claude Code sessions even when the CLI is not running
    /// locally on Windows.
    /// </summary>
    public string? RefreshToken { get; set; }

    /// <summary>Display email for the signed-in Claude OAuth account, if known.</summary>
    public string? AccountEmail { get; set; }

    /// <summary>
    /// Which organisation's usage to read. Resolved from the key on first poll and cached, because
    /// working it out costs two extra requests; cleared whenever the key changes.
    /// </summary>
    public string? OrgId { get; set; }

    /// <summary>
    /// Seconds between polls. Three minutes matches upstream: the figures move slowly and this is
    /// somebody else's server.
    /// </summary>
    public int RefreshSeconds { get; set; } = 180;

    /// <summary>Show the seven-day limit under the session one.</summary>
    public bool ShowWeekly { get; set; } = true;

    /// <summary>Show the weekly per-model limit, when the account has one.</summary>
    public bool ShowModel { get; set; } = true;
}

/// <summary>
/// Everything the Antigravity usage strip needs. Quota figures can be read either directly from
/// Google's Cloud Code API after signing in (which works 24/7 even when Antigravity is closed or
/// running on another machine), or from the local Antigravity language server process while the
/// IDE is open on this machine.
/// </summary>
public sealed class AntigravitySettings
{
    /// <summary>Master switch. Off means we never query Google or look for the language server.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The Google OAuth refresh token for Antigravity's Cloud Code API, encrypted to this Windows
    /// account by DPAPI — never the raw token. Lets ScweenSpit read model quotas even when
    /// Antigravity is not running locally. See <see cref="Secret"/>.
    /// </summary>
    public string? RefreshToken { get; set; }

    /// <summary>Email address of the signed-in Google account, shown on the settings page.</summary>
    public string? AccountEmail { get; set; }

    /// <summary>
    /// Cached Cloud AI Companion project ID resolved from `loadCodeAssist`, so regular polls only
    /// need a single `fetchAvailableModels` request. Cleared whenever the account changes.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Seconds between polls. One minute by default.
    /// </summary>
    public int RefreshSeconds { get; set; } = 60;

    /// <summary>Show the seven-day Gemini (Pro &amp; Flash) limit under the five-hour session one.</summary>
    public bool ShowWeekly { get; set; } = true;
}

/// <summary>
/// One Bambu Lab printer bound to the user's cloud account, kept in the display order the user
/// chooses in Settings (e.g. matching a physical row of printers in a closet).
/// </summary>
public sealed class BambuPrinterConfig
{
    /// <summary>Hardware serial number (`dev_id` from Bambu Cloud).</summary>
    public string Id { get; set; } = "";

    /// <summary>Device name reported by Bambu Cloud.</summary>
    public string Name { get; set; } = "";

    /// <summary>Optional short label override for the taskbar strip; falls back to <see cref="Name"/>.</summary>
    public string? Label { get; set; }

    /// <summary>Product/model name (e.g. "P1S", "X1 Carbon", "A1").</summary>
    public string? Model { get; set; }

    /// <summary>Whether to draw this printer's row in the taskbar strip.</summary>
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(Label) ? Label.Trim()
                               : !string.IsNullOrWhiteSpace(Name) ? Name.Trim()
                               : Id;
}

/// <summary>
/// Everything the Bambu Lab printer monitor strip needs. Authenticates with Bambu Cloud to discover
/// bound printers and streams live telemetry (`gcode_state`, `mc_percent`, `mc_remaining_time`,
/// `print_error`, etc.) over Bambu's Cloud MQTT broker on port 8883.
/// </summary>
public sealed class BambuSettings
{
    /// <summary>Master switch. Off means we never query Bambu Cloud or open an MQTT connection.</summary>
    public bool Enabled { get; set; }

    /// <summary>"Global" (`api.bambulab.com` / `us.mqtt.bambulab.com`) or "China" (`api.bambulab.cn` / `cn.mqtt.bambulab.com`).</summary>
    public string Region { get; set; } = "Global";

    /// <summary>Email address of the signed-in Bambu Lab account.</summary>
    public string? AccountEmail { get; set; }

    /// <summary>Resolved Cloud MQTT username (`u_&lt;uid&gt;`).</summary>
    public string? MqttUsername { get; set; }

    /// <summary>Bambu Cloud JWT access token, encrypted to this Windows account by DPAPI.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Bambu Cloud refresh token (when issued), encrypted to this Windows account by DPAPI.</summary>
    public string? RefreshToken { get; set; }

    /// <summary>
    /// Known printers in the top-to-bottom order they should appear on the bar. Newly discovered
    /// cloud printers are appended so manual reordering in Settings is preserved.
    /// </summary>
    public List<BambuPrinterConfig> Printers { get; set; } = new();

    /// <summary>Seconds between cloud device-list syncs (live status streams continuously over MQTT).</summary>
    public int RefreshSeconds { get; set; } = 60;
}

/// <summary>
/// One server × service row (e.g. "lp-server-vm · Claude" or "hx525 · Antigravity") in the
/// remote server chats taskbar strip, kept in the display order the user chooses in Settings.
/// </summary>
public sealed class RemoteServerRowConfig
{
    /// <summary>Stable key (`"{server}:{service}"`, e.g. `"lp-server-vm:Claude"`).</summary>
    public string Id { get; set; } = "";

    /// <summary>Normalized server name (e.g. `"lp-server-vm"`, `"hx525"`).</summary>
    public string Server { get; set; } = "";

    /// <summary>`"Claude"` or `"Antigravity"`.</summary>
    public string Service { get; set; } = "";

    /// <summary>Optional short label override for the taskbar strip (e.g. `"lp·Cl"`, `"hx525·AG"`).</summary>
    public string? Label { get; set; }

    /// <summary>Whether to draw this row in the taskbar strip.</summary>
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(Label) ? Label.Trim()
                               : DefaultLabel(Server, Service);

    public static string DefaultLabel(string server, string service)
    {
        string shortSrv = ShortServerName(server);
        string shortSvc = string.Equals(service, "Antigravity", StringComparison.OrdinalIgnoreCase) ? "AG" : "Cl";
        return $"{shortSrv}·{shortSvc}";
    }

    public static string ShortServerName(string server)
    {
        var s = (server ?? "").Trim();
        if (s.EndsWith("-headless", StringComparison.OrdinalIgnoreCase))
            s = s[..^9];
        if (s.EndsWith("-server-vm", StringComparison.OrdinalIgnoreCase))
            s = s[..^10];
        else if (s.EndsWith("-vm", StringComparison.OrdinalIgnoreCase))
            s = s[..^3];
        else if (s.EndsWith("-server", StringComparison.OrdinalIgnoreCase))
            s = s[..^7];
        return s.Length > 7 ? s[..7] : (s.Length > 0 ? s : "srv");
    }
}

/// <summary>
/// Everything the remote server chats monitor strip needs. Polls Claude Code Remote Control
/// (`api.anthropic.com/v1/environment_providers` &amp; `/v1/code/sessions`) and Antigravity Remote
/// Control (`cloudcode-pa.googleapis.com/v1internal:listInstances` &amp; `:proxyCommand`) using the
/// credentials already configured under Claude usage and Antigravity usage.
/// </summary>
public sealed class RemoteChatsSettings
{
    /// <summary>Master switch. Off means we never query remote sessions or instances.</summary>
    public bool Enabled { get; set; }

    /// <summary>Seconds between polls. Two seconds keeps chat counts and states near-instantaneous.</summary>
    public int RefreshSeconds { get; set; } = 2;

    /// <summary>
    /// How many hours a recently finished or idle unarchived chat stays counted in the "Done" tally
    /// before aging out into historical history.
    /// </summary>
    public int DoneWindowHours { get; set; } = 12;

    /// <summary>
    /// Known server × service rows in the top-to-bottom order they appear on the bar. Newly
    /// discovered servers are appended automatically so manual ordering and labels are preserved.
    /// </summary>
    public List<RemoteServerRowConfig> Rows { get; set; } = new();
}

public sealed class MonitorLayout
{
    public List<FracRect> Zones { get; set; } = new();

    /// <summary>
    /// Extra space kept clear at the edges, on top of what Windows already reports as the work
    /// area. Use it to dodge an auto-hiding or third-party taskbar, or to reclaim space Windows
    /// reserves but you do not actually need.
    /// </summary>
    public Margins Margins { get; set; } = new();
}

public sealed class SplitConfig
{
    /// <summary>
    /// Schema revision of the file on disk, so a changed default can reach people who already have
    /// the old one written out. Absent from a file written before this existed, which reads back as
    /// 0 — exactly what a migration wants.
    /// </summary>
    public int Version { get; set; }

    /// <summary>Revision <see cref="Normalized"/> brings a file up to.</summary>
    public const int CurrentVersion = 5;

    /// <summary>
    /// Set while ScweenSpit has handed the machine back to Windows, holding what it was doing before
    /// so it can pick it up again. Null means it is managing windows normally.
    /// </summary>
    public DisplayProfile? HandedBack { get; set; }

    /// <summary>
    /// The display arrangement ScweenSpit was in when it handed back, so plugging that monitor back
    /// in picks up where it left off. Without this, undocking hands back safely and re-docking
    /// leaves it handed back — which is safe and also useless.
    /// </summary>
    public string? HandedBackFrom { get; set; }

    /// <summary>
    /// Hand the machine back when the displays change to an arrangement with nothing saved for it.
    ///
    /// The unsafe direction is the automatic one: zones measured against a monitor that has been
    /// unplugged, a bar reserving space on it, and the shell's taskbar hidden in favour of one that
    /// is no longer anywhere. Handing back is always recoverable; being stranded is not.
    /// </summary>
    public bool HandBackOnUnknownDisplays { get; set; } = true;

    /// <summary>Master switch for the automatic maximize / borderless-fullscreen clamp.</summary>
    public bool AutoClamp { get; set; } = true;

    /// <summary>How long (ms) to ignore further events for a window we just moved.</summary>
    public int DebounceMs { get; set; } = 400;

    /// <summary>Gap in pixels left around every zone. 0 makes zones touch edge to edge.</summary>
    public int Padding { get; set; }

    /// <summary>
    /// Hide the shell's taskbars entirely while we run. Restored on exit, and on the next launch if
    /// this one is killed — the same bookkeeping as <see cref="SnapRestore"/>, for the same reason.
    /// </summary>
    public bool HideWindowsTaskbar { get; set; }

    /// <summary>Whether the taskbar was set to auto-hide before we hid it. Bookkeeping, not a preference.</summary>
    public bool? TaskbarRestore { get; set; }

    /// <summary>
    /// Whether the Windows taskbar should hide itself until reached for. Null means ScweenSpit does
    /// not manage it and leaves whatever Windows has.
    ///
    /// Kept here rather than only being pushed at Windows, because a setting with no record of it
    /// cannot be part of a display profile — and "put the taskbar back properly" is precisely what
    /// undocking wants.
    /// </summary>
    public bool? TaskbarAutoHide { get; set; }

    /// <summary>Show ScweenSpit's own bars at all. A master switch, so a profile can stand them
    /// down without discarding how each display is set up.</summary>
    public bool ShowBars { get; set; } = true;

    /// <summary>
    /// Stop Windows animating windows into the taskbar. With the taskbar hidden the animation flies
    /// them at a corner where nothing is, which reads as a glitch.
    /// </summary>
    public bool StopMinimiseAnimation { get; set; }

    /// <summary>Whether the animation was on before we turned it off. Bookkeeping, not a preference.</summary>
    public bool? AnimationRestore { get; set; }

    /// <summary>Disable Windows' own Aero Snap while we run, so it stops competing with our zones.</summary>
    public bool SuppressWindowsSnap { get; set; }

    /// <summary>What Windows' snap settings were before we suppressed them. Bookkeeping, not a preference:
    /// it lets the next launch put them back even if this one is killed rather than closed.</summary>
    public int[]? SnapRestore { get; set; }

    /// <summary>
    /// Pull a window back onto a single display when it appears straddling several. Windows you
    /// drag across a boundary yourself are exempted for as long as they live.
    /// </summary>
    public bool KeepOnOneDisplay { get; set; } = true;

    /// <summary>
    /// Settings remembered per display arrangement, keyed by <see cref="DisplayTopology.Signature"/>.
    /// Applied when the arrangement changes — docking, undocking, unplugging a monitor.
    /// </summary>
    public Dictionary<string, DisplayProfile> Profiles { get; set; } = new();

    /// <summary>Apply the saved profile when the displays change.</summary>
    public bool FollowDisplayChanges { get; set; } = true;

    /// <summary>Look for a newer release on startup, at most once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Where releases are published, as owner/name.</summary>
    public string UpdateRepository { get; set; } = "andrewwetzel/scweenspit";

    /// <summary>A GitHub token, needed only while the repository is private.</summary>
    public string UpdateToken { get; set; } = "";

    /// <summary>When the last check happened, so startup checks stay to one a day.</summary>
    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>Snap a window into a zone when you drop it there while dragging.</summary>
    public bool DragToZone { get; set; } = true;

    /// <summary>Key that must be held for a drag to snap: Shift, Control, Alt, or None for always.</summary>
    public string DragModifier { get; set; } = "Shift";

    /// <summary>Key that, held during a drag, spans the window across every zone touched.</summary>
    public string SpanModifier { get; set; } = "Control";

    /// <summary>Never touch windows of these processes or window classes. Matched case-insensitively,
    /// with or without a .exe suffix — e.g. "vlc", "mpv.exe", "UnityWndClass".</summary>
    public List<string> Exclude { get; set; } = new();

    /// <summary>Keyed by Win32 device name (\\.\DISPLAY1); "*" is the fallback for unlisted monitors.</summary>
    public Dictionary<string, MonitorLayout> Monitors { get; set; } = new();

    /// <summary>
    /// Our own taskbars, keyed by device name. An entry means a bar on that display; no entry means
    /// none. Deliberately not subject to the "*" fallback — a bar appearing on a display you never
    /// asked about would be a surprise, and it reserves screen space.
    /// </summary>
    public Dictionary<string, BarSettings> Bars { get; set; } = new();

    /// <summary>
    /// The claude.ai usage strip. One account for the machine; which bars draw it is per-bar.
    /// Named "Claude" rather than "ClaudeUsage" so it does not shadow the class of that name.
    /// </summary>
    public ClaudeSettings Claude { get; set; } = new();

    /// <summary>
    /// The Antigravity quota strip. Polled once for the machine; which bars draw it is per-bar.
    /// Named "Antigravity" rather than "AntigravityUsage" for the same reason as <see cref="Claude"/>.
    /// </summary>
    public AntigravitySettings Antigravity { get; set; } = new();

    /// <summary>
    /// The Bambu Lab printer monitor strip. Polled and streamed once for the machine; which bars
    /// draw it is per-bar.
    /// </summary>
    public BambuSettings Bambu { get; set; } = new();

    /// <summary>
    /// The remote server chats strip (Claude Code &amp; Antigravity per server). Polled once for
    /// the machine; which bars draw it is per-bar.
    /// </summary>
    public RemoteChatsSettings RemoteChats { get; set; } = new();

    public const string Fallback = "*";

    /// <summary>Virtual-key for a modifier name, or null for "no modifier required".</summary>
    public static int? ModifierKey(string name) => name?.Trim().ToLowerInvariant() switch
    {
        "shift" => Native.VK_SHIFT,
        "control" or "ctrl" => Native.VK_CONTROL,
        "alt" or "menu" => Native.VK_MENU,
        _ => null,
    };

    public static readonly string[] ModifierNames = ["None", "Shift", "Control", "Alt"];

    public static readonly string[] EdgeNames = ["Left", "Top", "Right", "Bottom"];

    public static BarEdge ParseEdge(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "left" => BarEdge.Left,
        "top" => BarEdge.Top,
        "bottom" => BarEdge.Bottom,
        _ => BarEdge.Right,
    };

    /// <summary>True when this window belongs to something the user has opted out of.</summary>
    public bool IsExcluded(string process, string windowClass)
    {
        foreach (var e in Exclude)
        {
            if (e.Length == 0) continue;
            var bare = e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? e[..^4] : e;
            if (bare.Equals(process, StringComparison.OrdinalIgnoreCase)) return true;
            if (e.Equals(windowClass, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ---- presets used by the tray menu and settings ------------------------
    public static readonly (string Name, Func<List<FracRect>> Make)[] Presets =
    {
        ("Full — leave this display alone", () => ZoneEdges.Grid(1, 1)),
        ("70 / 30",                () => new() { new(0, 0, .70, 1), new(.70, 0, 1, 1) }),
        ("30 / 70",                () => new() { new(0, 0, .30, 1), new(.30, 0, 1, 1) }),
        ("60 / 40",                () => new() { new(0, 0, .60, 1), new(.60, 0, 1, 1) }),
        ("50 / 50",                () => ZoneEdges.Grid(2, 1)),
        ("Thirds",                 () => ZoneEdges.Grid(3, 1)),
        ("25 / 50 / 25",           () => ZoneEdges.Columns(25, 50, 25)),
        ("Fourths",                () => ZoneEdges.Grid(4, 1)),
        ("Fifths",                 () => ZoneEdges.Grid(5, 1)),
        ("Sixths",                 () => ZoneEdges.Grid(6, 1)),
        ("Top / Bottom",           () => ZoneEdges.Grid(1, 2)),
        ("Three rows",             () => ZoneEdges.Grid(1, 3)),
        ("Quadrants",              () => ZoneEdges.Grid(2, 2)),
        ("3 × 2 grid",             () => ZoneEdges.Grid(3, 2)),
        ("4 × 2 grid",             () => ZoneEdges.Grid(4, 2)),
        ("Main + 2 stacked",       () => new() { new(0, 0, .60, 1), new(.60, 0, 1, .5), new(.60, .5, 1, 1) }),
        ("Main + 3 stacked",       () => new() { new(0, 0, .60, 1), new(.60, 0, 1, 1/3d), new(.60, 1/3d, 1, 2/3d), new(.60, 2/3d, 1, 1) }),
        ("Centre + Sides stacked", () => new() { new(0, 0, .25, .5), new(.25, 0, .75, 1), new(.75, 0, 1, .5), new(0, .5, .25, 1), new(.75, .5, 1, 1) }),
    };

    // ---- persistence -------------------------------------------------------
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScweenSpit", "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>True when the last <see cref="Load"/> could not read an existing config file.</summary>
    public static bool LastLoadFailed { get; private set; }

    /// <summary>
    /// Set when a config file exists but could not be read, and never cleared for that session.
    ///
    /// Load already refuses to overwrite an unreadable file, but every other save site in the app
    /// would — and the file holds the only record of what this machine had before ScweenSpit changed
    /// it. One spinner nudged after a failed load would write defaults over that record and make the
    /// changes permanent, which is a far worse outcome than a session that cannot save.
    /// </summary>
    public static bool SaveSuppressed { get; private set; }

    public static SplitConfig Load()
    {
        LastLoadFailed = SaveSuppressed = false;
        try
        {
            if (File.Exists(Path))
            {
                var cfg = JsonSerializer.Deserialize<SplitConfig>(File.ReadAllText(Path), JsonOpts);
                if (cfg is not null) return cfg.Normalized();
                LastLoadFailed = SaveSuppressed = true;
            }
            else
            {
                // First run only. The file has to exist for "Open config file" to have something
                // to open, so this seed is deliberate - unlike the unreadable case below.
                var seed = Default();
                seed.Save();
                return seed;
            }
        }
        catch (Exception ex)
        {
            // A corrupt config must never stop the app — but it must not be destroyed either;
            // it is hand-edited, so keep a copy before defaults overwrite it.
            LastLoadFailed = SaveSuppressed = true;
            Log.Write($"config load failed: {ex.Message}");
            try
            {
                var bad = Path + ".bad";
                File.Copy(Path, bad, overwrite: true);
                Log.Write($"unreadable config preserved at {bad}");
            }
            catch (Exception copyEx) { Log.Write($"could not preserve bad config: {copyEx.Message}"); }
        }

        // Deliberately NOT saved: overwriting an unreadable config destroys a hand-edited file the
        // user can still fix. They get working defaults for this session and keep their file.
        return Default();
    }

    public void Save()
    {
        if (SaveSuppressed)
        {
            Log.WriteOnce("save-suppressed",
                $"not saving over an unreadable config; it holds the only record of what this " +
                $"machine had before. Fix or delete {Path} to save again.");
            return;
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            // Write then swap. Saving is now triggered by things like a spinner's auto-repeat, and
            // a half-written config is worse than a stale one.
            // Unique per write: usage polling can rotate the session key and save from its own
            // thread, so two saves may now be in flight at once.
            var staging = $"{Path}.{Environment.CurrentManagedThreadId}.tmp";
            File.WriteAllText(staging, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(staging, Path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"config save failed: {ex.Message}");
        }
    }

    public static SplitConfig Default() => new()
    {
        Version = CurrentVersion,
        Monitors = { [Fallback] = new MonitorLayout { Zones = { new(0, 0, .70, 1), new(.70, 0, 1, 1) } } },
    };

    /// <summary>Guarantees a usable fallback entry and drops degenerate zones.</summary>
    private SplitConfig Normalized()
    {
        if (DebounceMs < 50) DebounceMs = 50;
        if (Padding < 0) Padding = 0;
        Exclude = Exclude.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct().ToList();

        // Not a bar setting, so it comes before them: a record of "everything was off" cannot be
        // what the machine looked like before ScweenSpit, and restoring it is exactly why snapping
        // stops working. Dropped, so the next suppression reads fresh values — which now refuse to
        // believe all-off as well.
        if (Version < 4 && WindowsSnap.IsAllOff(SnapRestore))
        {
            Log.Write("config v4: dropping a snap record of all-off; it cannot be what was there before");
            SnapRestore = null;
        }

        foreach (var bar in Bars.Values)
        {
            // Defaults that moved after files were already written out. Each step only ever brings
            // a gap down, so a value somebody chose smaller is never raised back up to meet it.
            //
            // v1: one number covered all four sides, at 6px, and split three ways the open side
            //     wanted less than that.
            // v2: even less — level with the ends, so the three visible sides match.
            if (Version < 1) bar.FloatMargin = Math.Min(bar.FloatMargin, 4);
            if (Version < 2) bar.FloatMargin = Math.Min(bar.FloatMargin, 3);

            // v3 is not a changed default but an abandoned feature: leaving it on costs a foreground
            // hook and a timer for every opening of a menu it cannot move.
            if (Version < 3) bar.MoveStartMenu = false;

            // A bar thinner than this cannot show anything; a bar wider than a third of a display is
            // almost certainly a typo, and it eats the work area for every app on that screen.
            bar.Thickness = Math.Clamp(bar.Thickness, 28, 600);
            // The gap is added to the thickness rather than taken out of it, so it only needs a
            // sane upper bound of its own.
            bar.FloatMargin = Math.Clamp(bar.FloatMargin, 0, 40);
            bar.SideGap = Math.Clamp(bar.SideGap, 0, 40);
            bar.EdgeGap = Math.Clamp(bar.EdgeGap, 0, 40);
            bar.TrackerScale = bar.TrackerScale <= 0 ? 125 : Math.Clamp(bar.TrackerScale, 80, 300);
            bar.TrackerMaxRows = bar.TrackerMaxRows <= 0 ? 3 : Math.Clamp(bar.TrackerMaxRows, 1, 6);
            bar.RemoteChatsWidth = bar.RemoteChatsWidth is > 0 and var rw ? Math.Clamp(rw, 44, 1200) : null;
            bar.UsageWidth = bar.UsageWidth is > 0 and var uw ? Math.Clamp(uw, 44, 1200) : null;
            bar.AntigravityWidth = bar.AntigravityWidth is > 0 and var aw ? Math.Clamp(aw, 44, 1200) : null;
            bar.BambuWidth = bar.BambuWidth is > 0 and var bw ? Math.Clamp(bw, 44, 1200) : null;
            bar.MachineWidth = bar.MachineWidth is > 0 and var mw ? Math.Clamp(mw, 44, 1200) : null;
            bar.Pinned ??= new();
            if (!EdgeNames.Contains(bar.Edge, StringComparer.OrdinalIgnoreCase)) bar.Edge = "Right";
        }

        int priorVersion = Version;
        Version = CurrentVersion;

        // Below half a minute this stops being a status readout and starts being a scraper.
        Claude ??= new ClaudeSettings();
        Claude.RefreshSeconds = Math.Clamp(Claude.RefreshSeconds, 30, 3600);

        Antigravity ??= new AntigravitySettings();
        Antigravity.RefreshSeconds = Math.Clamp(Antigravity.RefreshSeconds, 15, 3600);

        Bambu ??= new BambuSettings();
        Bambu.RefreshSeconds = Math.Clamp(Bambu.RefreshSeconds, 15, 3600);
        Bambu.Printers ??= new();
        Bambu.Printers.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Id));
        if (!string.Equals(Bambu.Region, "China", StringComparison.OrdinalIgnoreCase))
            Bambu.Region = "Global";
        else
            Bambu.Region = "China";

        RemoteChats ??= new RemoteChatsSettings();
        if (priorVersion < 5 && RemoteChats.RefreshSeconds == 20)
            RemoteChats.RefreshSeconds = 2;
        RemoteChats.RefreshSeconds = Math.Clamp(RemoteChats.RefreshSeconds, 1, 300);
        RemoteChats.DoneWindowHours = Math.Clamp(RemoteChats.DoneWindowHours, 1, 168);
        RemoteChats.Rows ??= new();
        RemoteChats.Rows.RemoveAll(r => r is null || string.IsNullOrWhiteSpace(r.Id));

        foreach (var layout in Monitors.Values)
        {
            // Fractions outside [0,1] would place a zone off the monitor entirely - a window sent
            // there has no reachable titlebar.
            foreach (var z in layout.Zones)
            {
                z.L = Math.Clamp(z.L, 0, 1); z.R = Math.Clamp(z.R, 0, 1);
                z.T = Math.Clamp(z.T, 0, 1); z.B = Math.Clamp(z.B, 0, 1);
            }
            layout.Zones.RemoveAll(z => z.R - z.L <= 0.01 || z.B - z.T <= 0.01);
            ZoneEdges.Canonicalise(layout.Zones);

            // Stored in the same reading order the overlay numbers them in, so "Zone 2" in the
            // settings window is always the 2 painted on screen.
            layout.Zones = layout.Zones.OrderBy(z => z.T).ThenBy(z => z.L).ToList();

            layout.Margins ??= new Margins();
            layout.Margins.Top = Math.Max(0, layout.Margins.Top);
            layout.Margins.Bottom = Math.Max(0, layout.Margins.Bottom);
            layout.Margins.Left = Math.Max(0, layout.Margins.Left);
            layout.Margins.Right = Math.Max(0, layout.Margins.Right);
        }

        if (!Monitors.ContainsKey(Fallback) || Monitors[Fallback].Zones.Count == 0)
            Monitors[Fallback] = Default().Monitors[Fallback];

        // An entry with margins but no zones is a legitimate hand-edit ("reserve space here, keep
        // the shared layout"). Give it the shared zones - cloned, or editing this display would
        // move every other one - rather than deleting the user's margins.
        foreach (var entry in Monitors.Values)
            if (entry.Zones.Count == 0 && entry.Margins.Any)
                entry.Zones = Monitors[Fallback].Zones.Select(z => new FracRect(z.L, z.T, z.R, z.B, z.CoverTaskbar)).ToList();

        foreach (var key in Monitors.Where(kv => kv.Value.Zones.Count == 0 && kv.Key != Fallback)
                                    .Select(kv => kv.Key).ToList())
            Monitors.Remove(key);

        return this;
    }

    /// <summary>The layout a device actually uses, falling back to "*".</summary>
    public MonitorLayout LayoutFor(string device)
    {
        if (Monitors.TryGetValue(device, out var m) && m.Zones.Count > 0) return m;
        if (Monitors.TryGetValue(Fallback, out var f) && f.Zones.Count > 0) return f;
        return Default().Monitors[Fallback];
    }

    public List<FracRect> ZonesFor(string device) => LayoutFor(device).Zones;

    /// <summary>
    /// Gives a device its own entry, cloned from whatever it was inheriting. Editing a monitor that
    /// was using the "*" fallback has to fork, or the edit would silently move every other display.
    /// </summary>
    public MonitorLayout OwnLayout(string device)
    {
        if (Monitors.TryGetValue(device, out var own) && own.Zones.Count > 0) return own;

        var inherited = LayoutFor(device);
        var forked = new MonitorLayout
        {
            Zones = inherited.Zones.Select(z => new FracRect(z.L, z.T, z.R, z.B, z.CoverTaskbar)).ToList(),
            Margins = inherited.Margins.Copy(),
        };
        Monitors[device] = forked;
        return forked;
    }

    public void SetMargins(string device, Margins margins)
    {
        OwnLayout(device).Margins = margins;
        Save();
    }

    /// <summary>
    /// Copies another config's values in place. Reloading by replacing the object would strand every
    /// reference the UI and services already hold, so the instance itself is never swapped out.
    /// </summary>
    public void CopyFrom(SplitConfig o)
    {
        // The revision of the file we are taking these from, so a reload cannot leave the live
        // config claiming to be older than the values it now holds and migrating them twice.
        Version = o.Version;
        HandedBack = o.HandedBack;
        HandedBackFrom = o.HandedBackFrom;
        HandBackOnUnknownDisplays = o.HandBackOnUnknownDisplays;
        AutoClamp = o.AutoClamp;
        DebounceMs = o.DebounceMs;
        Padding = o.Padding;
        SuppressWindowsSnap = o.SuppressWindowsSnap;
        HideWindowsTaskbar = o.HideWindowsTaskbar;
        TaskbarAutoHide = o.TaskbarAutoHide;
        ShowBars = o.ShowBars;
        StopMinimiseAnimation = o.StopMinimiseAnimation;
        // SnapRestore is deliberately NOT copied: it records what this process changed on the
        // system, and the disk copy may be older than what we are currently holding.
        DragToZone = o.DragToZone;
        DragModifier = o.DragModifier;
        SpanModifier = o.SpanModifier;
        Profiles = o.Profiles;
        FollowDisplayChanges = o.FollowDisplayChanges;
        CheckForUpdates = o.CheckForUpdates;
        UpdateRepository = o.UpdateRepository;
        UpdateToken = o.UpdateToken;
        LastUpdateCheck = o.LastUpdateCheck;
        Exclude = o.Exclude;
        Monitors = o.Monitors;
        Bars = o.Bars;
        Claude = o.Claude;
        Antigravity = o.Antigravity;
        Bambu = o.Bambu;
        RemoteChats = o.RemoteChats;
    }

    /// <summary>
    /// Sets a display's zones. <paramref name="save"/> is false while a divider is being dragged:
    /// the layout has to be live for the windows following it to be placed against something, and
    /// writing the file twenty-five times a second to say so is not the way to do that.
    /// </summary>
    public void SetZones(string device, List<FracRect> zones, bool save = true)
    {
        var layout = OwnLayout(device);
        layout.Zones = zones.OrderBy(z => z.T).ThenBy(z => z.L).ToList();
        if (save) Save();
    }

    /// <summary>
    /// Reconciles configured bars and tracker flags against the currently attached monitors.
    /// Windows reassigns <c>\\.\DISPLAYn</c> device names across reboots, dock/undock events, and
    /// sleep/wake cycles (for example, enumerating a single monitor as <c>\\.\DISPLAY2</c> during
    /// an uptime session and <c>\\.\DISPLAY1</c> after a restart). This ensures that bar and tracker
    /// selections made under a previous <c>\\.\DISPLAYn</c> name or on a tracker's settings page
    /// are preserved on the active display after a system restart.
    /// </summary>
    public bool ReconcileMonitorsAndTrackers(IReadOnlyList<MonitorGeometry> monitors)
    {
        bool changed = false;

        static int TrackerCount(BarSettings b) =>
            (b.ShowUsage ? 1 : 0) +
            (b.ShowAntigravity ? 1 : 0) +
            (b.ShowRemoteChats ? 1 : 0) +
            (b.ShowBambu ? 1 : 0) +
            (b.ShowMachine ? 1 : 0);

        if (monitors.Count > 0 && Bars.Count > 0)
        {
            var attachedSet = new HashSet<string>(monitors.Select(m => m.Device), StringComparer.OrdinalIgnoreCase);
            var attachedKeys = Bars.Keys.Where(k => attachedSet.Contains(k)).ToList();
            var unattachedKeys = Bars.Keys.Where(k => !attachedSet.Contains(k)).ToList();

            // 1. If bars are configured only for unattached \\.\DISPLAYn device names (e.g. \\.\DISPLAY2
            //    became \\.\DISPLAY1 after a reboot), migrate the most-configured bar onto the primary
            //    attached display so the taskbar and its trackers do not vanish.
            if (attachedKeys.Count == 0 && unattachedKeys.Count > 0)
            {
                var bestKey = unattachedKeys
                    .OrderByDescending(k => TrackerCount(Bars[k]))
                    .ThenByDescending(k => Bars[k].Pinned.Count)
                    .First();
                var targetDevice = monitors[0].Device;
                Bars[targetDevice] = Bars[bestKey];
                if (monitors.Count == 1)
                {
                    foreach (var oldKey in unattachedKeys)
                        Bars.Remove(oldKey);
                }
                else
                {
                    Bars.Remove(bestKey);
                    unattachedKeys.Remove(bestKey);
                }

                attachedKeys.Add(targetDevice);
                if (monitors.Count == 1) unattachedKeys.Clear();
                changed = true;
                Log.Write($"reconciled bar from {bestKey} to attached display {targetDevice}");
            }

            // 2. If both an attached entry (e.g. an older \\.\DISPLAY1) and an unattached entry
            //    (e.g. \\.\DISPLAY2 where the user enabled trackers or pinned apps) exist, carry
            //    any tracker selections over to the attached bar(s).
            if (attachedKeys.Count > 0 && unattachedKeys.Count > 0)
            {
                var attachedBars = attachedKeys.Select(k => Bars[k]).ToList();
                var unattachedBars = unattachedKeys.Select(k => Bars[k]).ToList();

                int attachedTrackersBefore = attachedBars.Max(TrackerCount);
                var richestUnattached = unattachedBars
                    .OrderByDescending(TrackerCount)
                    .ThenByDescending(b => b.Pinned.Count)
                    .First();

                if (unattachedBars.Any(b => b.ShowUsage) && !attachedBars.Any(b => b.ShowUsage))
                {
                    foreach (var b in attachedBars) b.ShowUsage = true;
                    changed = true;
                }
                if (unattachedBars.Any(b => b.ShowAntigravity) && !attachedBars.Any(b => b.ShowAntigravity))
                {
                    foreach (var b in attachedBars) b.ShowAntigravity = true;
                    changed = true;
                }
                if (unattachedBars.Any(b => b.ShowRemoteChats) && !attachedBars.Any(b => b.ShowRemoteChats))
                {
                    foreach (var b in attachedBars) b.ShowRemoteChats = true;
                    changed = true;
                }
                if (unattachedBars.Any(b => b.ShowBambu) && !attachedBars.Any(b => b.ShowBambu))
                {
                    foreach (var b in attachedBars) b.ShowBambu = true;
                    changed = true;
                }
                if (unattachedBars.Any(b => b.ShowMachine) && !attachedBars.Any(b => b.ShowMachine))
                {
                    foreach (var b in attachedBars) b.ShowMachine = true;
                    changed = true;
                }

                // On a single-monitor setup, any unattached \\.\DISPLAYn entry is a stale alias for
                // the same physical display. Merge pins and tracker layout settings and drop the alias.
                if (monitors.Count == 1)
                {
                    var activeBar = attachedBars[0];
                    if (attachedTrackersBefore == 0 && TrackerCount(richestUnattached) > 0)
                    {
                        activeBar.ExpandTrackers = richestUnattached.ExpandTrackers;
                        activeBar.TrackerScale = richestUnattached.TrackerScale;
                        activeBar.TrackerMaxRows = richestUnattached.TrackerMaxRows;
                    }
                    activeBar.RemoteChatsWidth ??= richestUnattached.RemoteChatsWidth;
                    activeBar.UsageWidth ??= richestUnattached.UsageWidth;
                    activeBar.AntigravityWidth ??= richestUnattached.AntigravityWidth;
                    activeBar.BambuWidth ??= richestUnattached.BambuWidth;
                    activeBar.MachineWidth ??= richestUnattached.MachineWidth;

                    foreach (var ub in unattachedBars)
                    {
                        foreach (var pin in ub.Pinned)
                        {
                            if (!activeBar.Pinned.Any(p => string.Equals(p, pin, StringComparison.OrdinalIgnoreCase)))
                                activeBar.Pinned.Add(pin);
                        }
                    }

                    foreach (var staleKey in unattachedKeys)
                        Bars.Remove(staleKey);

                    changed = true;
                    Log.Write($"merged stale bar entry({string.Join(", ", unattachedKeys)}) into {attachedKeys[0]}");
                }
            }

            // Also migrate per-monitor zone layout if a single monitor's device name changed.
            if (monitors.Count == 1)
            {
                var dev = monitors[0].Device;
                if (!Monitors.ContainsKey(dev))
                {
                    var staleMonitorKey = Monitors.Keys.FirstOrDefault(k => k != Fallback && !attachedSet.Contains(k));
                    if (staleMonitorKey is not null)
                    {
                        Monitors[dev] = Monitors[staleMonitorKey];
                        Monitors.Remove(staleMonitorKey);
                        changed = true;
                    }
                }
            }
        }

        // 3. Synchronize global tracker Enabled switches and per-bar Show* flags so a tracker
        //    selected on its own page is always shown on the active bar(s), and a tracker selected
        //    on the Taskbar page always starts its background poller.
        var activeBars = monitors.Count > 0
            ? monitors.Where(m => Bars.ContainsKey(m.Device)).Select(m => Bars[m.Device]).ToList()
            : Bars.Values.ToList();
        if (activeBars.Count == 0 && Bars.Count > 0)
            activeBars = Bars.Values.ToList();

        if (activeBars.Count > 0)
        {
            if (Claude.Enabled && !activeBars.Any(b => b.ShowUsage))
            {
                foreach (var b in activeBars) b.ShowUsage = true;
                changed = true;
            }
            else if (!Claude.Enabled && activeBars.Any(b => b.ShowUsage) &&
                     (!string.IsNullOrWhiteSpace(Claude.SessionKey)
                      || !string.IsNullOrWhiteSpace(Claude.RefreshToken)
                      || ClaudeUsage.HasClaudeCodeCredentials()))
            {
                Claude.Enabled = true;
                changed = true;
            }

            if (Antigravity.Enabled && !activeBars.Any(b => b.ShowAntigravity))
            {
                foreach (var b in activeBars) b.ShowAntigravity = true;
                changed = true;
            }
            else if (!Antigravity.Enabled && activeBars.Any(b => b.ShowAntigravity))
            {
                Antigravity.Enabled = true;
                changed = true;
            }

            if (RemoteChats.Enabled && !activeBars.Any(b => b.ShowRemoteChats))
            {
                foreach (var b in activeBars) b.ShowRemoteChats = true;
                changed = true;
            }
            else if (!RemoteChats.Enabled && activeBars.Any(b => b.ShowRemoteChats))
            {
                RemoteChats.Enabled = true;
                changed = true;
            }

            if (Bambu.Enabled && !activeBars.Any(b => b.ShowBambu))
            {
                foreach (var b in activeBars) b.ShowBambu = true;
                changed = true;
            }
            else if (!Bambu.Enabled && activeBars.Any(b => b.ShowBambu))
            {
                Bambu.Enabled = true;
                changed = true;
            }
        }

        return changed;
    }
}
