using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ScweenSpit;

public enum RemoteChatService
{
    Claude,
    Antigravity,
}

public enum RemoteChatState
{
    Waiting,
    Running,
    Done,
}

/// <summary>One live or recently completed chat on a remote Claude Code or Antigravity server.</summary>
public sealed record RemoteChatItem(
    string Id,
    string Title,
    string Server,
    RemoteChatService Service,
    RemoteChatState State,
    string? Repo,
    string? Detail,
    bool Unread,
    DateTimeOffset LastUpdated,
    string WebUrl);

/// <summary>Aggregated live chat status for one server × service row on the taskbar strip.</summary>
public sealed record RemoteServerRow(
    string Id,
    string Server,
    string Label,
    RemoteChatService Service,
    bool Online,
    int WaitingCount,
    int RunningCount,
    int DoneCount,
    IReadOnlyList<RemoteChatItem> Chats)
{
    public int TotalCount => WaitingCount + RunningCount + DoneCount;
}

/// <summary>The combined reading across all enabled server × service rows.</summary>
public sealed record RemoteChatsReading(
    IReadOnlyList<RemoteServerRow> Rows,
    string? Error,
    bool NeedsAuth,
    bool Transient = false)
{
    public bool Settled => !Transient;
}

/// <summary>
/// Polls live remote server chat status for both Claude Code Remote Control
/// (`api.anthropic.com/v1/environment_providers` &amp; `/v1/code/sessions`) and Antigravity Remote
/// Control (`cloudcode-pa.googleapis.com/v1internal:listInstances` &amp; `:proxyCommand`), grouping
/// chats by server (e.g. `lp-server-vm` vs `hx525`) and service (`Claude` vs `Antigravity`).
/// </summary>
public static class RemoteChatUsage
{
    public const string ClaudeCodeWebUrl = "https://claude.ai/code";
    public const string AntigravityWebUrl = "https://antigravity.google";

    private const string AnthropicApiBase = "https://api.anthropic.com";
    private const string AnthropicBetaHeader = "ccr-byoc-2025-07-29,environments-2025-11-01,oauth-2025-04-20";
    private const string AnthropicVersionHeader = "2023-06-01";
    private const string ClaudeCodeUserAgent = "claude-code/2.1.284";

    private const string CloudCodeBaseUrl = "https://cloudcode-pa.googleapis.com";
    private const string ListInstancesPath = "/v1internal:listInstances";
    private const string ProxyCommandPath = "/v1internal:proxyCommand";
    private const string StreamProxyCommandPath = "/v1internal:streamProxyCommand?alt=sse";
    private const string GetTrajectoriesRpc = "exa.language_server_pb.LanguageServerService/GetAllCascadeTrajectories";
    private const string SubscribeSummariesRpc = "exa.language_server_pb.LanguageServerService/JetboxSubscribeToSummaries";
    // Base64 of {"excludeSubtrajectories":true}
    private const string GetTrajectoriesPayloadBase64 = "eyJleGNsdWRlU3VidHJhamVjdG9yaWVzIjp0cnVlfQ==";
    // Base64 of Connect frame envelope (\x00\x00\x00\x00\x02) + "{}"
    private const string SubscribeSummariesPayloadBase64 = "AAAAAAJ7fQ==";

    /// <summary>Where Claude Code stores its global CLI state (including `oauthAccount.organizationUuid`).</summary>
    public static string ClaudeGlobalConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    private static readonly HttpClient http = new(new HttpClientHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(12) };

    private static readonly HttpClient streamHttp = new(new HttpClientHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private sealed class ServerBucket
    {
        public string Id { get; init; } = "";
        public string Server { get; init; } = "";
        public RemoteChatService Service { get; init; }
        public bool Online { get; set; }
        public List<RemoteChatItem> Chats { get; set; } = new();
    }

    private sealed record AntigravityTrajectorySummary(
        string Id,
        string Title,
        string Server,
        string Status,
        bool NotFullyIdle,
        bool HasActiveChildren,
        bool Interrupted,
        int WaitingSteps,
        bool Killed,
        bool Archived,
        bool Unread,
        string? Repo,
        DateTimeOffset LastModified);

    private sealed class AntigravityInstanceStreamState : IDisposable
    {
        public required AntigravityInstance Instance { get; set; }
        public CancellationTokenSource Cts { get; } = new();
        public Dictionary<string, AntigravityTrajectorySummary> Trajectories { get; } = new(StringComparer.Ordinal);
        public bool StreamRunning { get; set; }
        public bool StreamConnected { get; set; }
        public bool HasSnapshot { get; set; }
        public DateTimeOffset LastUnaryFetchUtc { get; set; } = DateTimeOffset.MinValue;

        public void Dispose()
        {
            try { Cts.Cancel(); } catch { }
        }
    }

    private static readonly object gate = new();
    private static readonly object agStreamLock = new();
    private static readonly Dictionary<string, ServerBucket> liveByRowId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, AntigravityInstanceStreamState> agStreamsByUuid = new(StringComparer.Ordinal);

    private static List<AntigravityInstance> cachedAgInstances = new();
    private static DateTimeOffset lastAgListInstancesUtc = DateTimeOffset.MinValue;

    private static string? cachedClaudeEnvsBody;
    private static DateTimeOffset cachedClaudeEnvsUtc = DateTimeOffset.MinValue;
    private static DateTime cachedClaudeGlobalConfigWriteUtc = DateTime.MinValue;

    private static PollServiceResult? lastClaudePoll;
    private static PollServiceResult? lastAgPoll;

    private static RemoteChatsSettings? settings;
    private static Action? persist;
    private static CancellationTokenSource? life;
    private static volatile RemoteChatsReading? current;
    private static volatile TaskCompletionSource? nudge;
    private static volatile bool wakeWanted;
    private static volatile bool enabled;
    private static string? cachedClaudeOrgUuid;
    private static string? lastError;
    private static bool lastNeedsAuth;
    private static bool hasCompletedInitialSync;
    private const int AttentionGlowDurationMs = 2600;
    private static readonly TimeSpan PostTurnWaitingWindow = TimeSpan.FromMinutes(5);
    private static int? lastSettledWaitingCount;
    private static int? lastClaudeWaitingCount;
    private static int? lastAgWaitingCount;
    private static long attentionGlowStartTick;
    private static long claudeGlowStartTick;
    private static long agGlowStartTick;

    /// <summary>Raised whenever <see cref="Current"/> changes so bars can repaint immediately.</summary>
    public static event Action? Changed;

    internal static float GlowIntensityFromTick(long start)
    {
        if (start == 0) return 0f;
        long elapsed = Environment.TickCount64 - start;
        if (elapsed < 0 || elapsed >= AttentionGlowDurationMs) return 0f;

        // Two smooth warm amber pulses over ~2.6 seconds, fading cleanly to 0.
        double t = elapsed / (double)AttentionGlowDurationMs;
        double pulse = 0.5 - 0.5 * Math.Cos(t * Math.PI * 4.0);
        double envelope = 1.0 - t * 0.35;
        return (float)Math.Clamp(pulse * envelope, 0.0, 1.0);
    }

    /// <summary>
    /// Normalized [0..1] glow intensity when the tracker has just transitioned from 0 chats
    /// needing help to at least 1 chat needing help, or 0 when not glowing.
    /// </summary>
    public static float AttentionGlowIntensity => GetAttentionGlowIntensity(null);

    public static float GetAttentionGlowIntensity(RemoteChatService? service) => service switch
    {
        RemoteChatService.Claude => GlowIntensityFromTick(Interlocked.Read(ref claudeGlowStartTick)),
        RemoteChatService.Antigravity => GlowIntensityFromTick(Interlocked.Read(ref agGlowStartTick)),
        _ => Math.Max(
            GlowIntensityFromTick(Interlocked.Read(ref attentionGlowStartTick)),
            Math.Max(
                GlowIntensityFromTick(Interlocked.Read(ref claudeGlowStartTick)),
                GlowIntensityFromTick(Interlocked.Read(ref agGlowStartTick)))),
    };

    /// <summary>True while the brief attention glow animation is active.</summary>
    public static bool IsAttentionGlowing => AttentionGlowIntensity > 0f;

    /// <summary>The latest reading across all enabled server × service rows.</summary>
    public static RemoteChatsReading? Current => current;

    /// <summary>True when remote server chat tracking is switched on.</summary>
    public static bool Enabled => enabled;

    /// <summary>Number of enabled server × service rows configured for display on the bar.</summary>
    public static int ConfiguredRowCount
    {
        get
        {
            lock (gate)
            {
                if (settings is null) return 0;
                return settings.Rows.Count(r => r.Enabled);
            }
        }
    }

    public static int ConfiguredRowCountFor(RemoteChatService service)
    {
        lock (gate)
        {
            if (settings is null) return 0;
            bool wantAg = service == RemoteChatService.Antigravity;
            return settings.Rows.Count(r =>
                r.Enabled &&
                string.Equals(r.Service, "Antigravity", StringComparison.OrdinalIgnoreCase) == wantAg);
        }
    }

    public static bool ShouldShowServiceStrip(RemoteChatService service)
    {
        lock (gate)
        {
            if (settings is null || !settings.Enabled || settings.Rows.Count == 0)
                return true;

            bool wantAg = service == RemoteChatService.Antigravity;
            bool anyForService = settings.Rows.Any(r =>
                string.Equals(r.Service, "Antigravity", StringComparison.OrdinalIgnoreCase) == wantAg);
            if (!anyForService)
                return true;

            return settings.Rows.Any(r =>
                r.Enabled &&
                string.Equals(r.Service, "Antigravity", StringComparison.OrdinalIgnoreCase) == wantAg);
        }
    }

    /// <summary>Starts, stops, or updates the remote chat poller.</summary>
    public static void Configure(RemoteChatsSettings config, Action? save = null)
    {
        lock (gate)
        {
            settings = config;
            if (save is not null) persist = save;
            enabled = config.Enabled;

            if (!config.Enabled)
            {
                life?.Cancel();
                life = null;
                current = null;
                hasCompletedInitialSync = false;
                lastSettledWaitingCount = null;
                lastClaudeWaitingCount = null;
                lastAgWaitingCount = null;
                Interlocked.Exchange(ref attentionGlowStartTick, 0);
                Interlocked.Exchange(ref claudeGlowStartTick, 0);
                Interlocked.Exchange(ref agGlowStartTick, 0);
                StopAllAntigravityStreams();
                return;
            }

            RebuildCurrentReadingLocked();

            if (life is not null) return;

            life = new CancellationTokenSource();
            var token = life.Token;
            _ = Task.Run(() => LoopAsync(token), token);

            ListenEvents();
        }
    }

    /// <summary>Wakes the poll loop immediately.</summary>
    public static void Refresh()
    {
        wakeWanted = true;
        nudge?.TrySetResult();
    }

    /// <summary>Rebuilds the reading immediately when rows are reordered, renamed, or toggled in Settings.</summary>
    public static void NotifyRowsEdited()
    {
        lock (gate)
        {
            RebuildCurrentReadingLocked();
        }
    }

    /// <summary>Stops the background poller on exit.</summary>
    public static void Stop()
    {
        lock (gate)
        {
            life?.Cancel();
            life = null;
            enabled = false;
        }

        StopAllAntigravityStreams();

        if (!listening) return;
        listening = false;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerChanged;
    }

    private static void StopAllAntigravityStreams()
    {
        lock (agStreamLock)
        {
            foreach (var s in agStreamsByUuid.Values)
                s.Dispose();
            agStreamsByUuid.Clear();
            cachedAgInstances.Clear();
            lastAgListInstancesUtc = DateTimeOffset.MinValue;
        }
    }

    private static bool listening;

    private static void ListenEvents()
    {
        if (listening) return;
        listening = true;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerChanged;
    }

    private static void OnNetworkChanged(object? sender, EventArgs e) => Refresh();

    private static void OnPowerChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) Refresh();
    }

    /// <summary>
    /// Opens a specific chat or service in the browser (or raises a local Antigravity window if
    /// one is open on this machine).
    /// </summary>
    public static void OpenChat(RemoteChatItem chat)
    {
        if (chat.Service == RemoteChatService.Claude)
        {
            SystemStatus.Open(!string.IsNullOrWhiteSpace(chat.WebUrl) ? chat.WebUrl : ClaudeCodeWebUrl);
            return;
        }

        if (!TryRaiseAntigravityWindow())
            SystemStatus.Open(AntigravityWebUrl);
    }

    public static void OpenService(RemoteChatService service)
    {
        if (service == RemoteChatService.Claude)
        {
            SystemStatus.Open(ClaudeCodeWebUrl);
            return;
        }

        if (!TryRaiseAntigravityWindow())
            SystemStatus.Open(AntigravityWebUrl);
    }

    private static bool TryRaiseAntigravityWindow()
    {
        try
        {
            var openWindows = WindowList.Enumerate(null);
            var agWin = openWindows.FirstOrDefault(w =>
                w.Process.Equals("antigravity", StringComparison.OrdinalIgnoreCase) ||
                w.Title.Contains("Antigravity", StringComparison.OrdinalIgnoreCase));
            if (agWin is not null)
            {
                WindowList.Raise(agWin.Handle);
                return true;
            }
        }
        catch { }
        return false;
    }

    // ---- Background polling loop -------------------------------------------

    private static async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool forceFullSync = wakeWanted || !hasCompletedInitialSync;
            wakeWanted = false;

            try
            {
                await PollOnceAsync(ct, forceFullSync);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Write($"remote chats poll failed: {ex.Message}");
                lock (gate)
                {
                    lastError = "Could not query remote chats";
                    RebuildCurrentReadingLocked();
                }
            }

            int seconds;
            lock (gate)
            {
                seconds = Math.Clamp(settings?.RefreshSeconds ?? 2, 1, 300);
                if (!hasCompletedInitialSync || lastError is not null)
                    seconds = Math.Min(seconds, 5);
            }

            var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            nudge = wake;
            if (wakeWanted) continue;

            try
            {
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(seconds), ct), wake.Task);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task PollOnceAsync(CancellationToken ct, bool forceFullSync)
    {
        int doneWindowHours;
        lock (gate)
        {
            if (settings is not { Enabled: true }) return;
            doneWindowHours = Math.Clamp(settings.DoneWindowHours, 1, 168);
        }

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(doneWindowHours);

        // Run Claude and Antigravity checks concurrently, but apply each service's result
        // as soon as it arrives so a fast ~200ms Claude session check never waits on Antigravity.
        var claudeTask = Task.Run(() =>
        {
            var res = PollClaude(cutoff, forceFullSync);
            ApplyServiceResult(RemoteChatService.Claude, res);
        }, ct);

        var agTask = Task.Run(() =>
        {
            var res = PollAntigravity(cutoff, forceFullSync, ct);
            ApplyServiceResult(RemoteChatService.Antigravity, res);
        }, ct);

        await Task.WhenAll(claudeTask, agTask);
    }

    private static void ApplyServiceResult(RemoteChatService service, PollServiceResult res)
    {
        Action? save = null;
        lock (gate)
        {
            if (settings is null) return;

            if (service == RemoteChatService.Claude)
                lastClaudePoll = res;
            else
                lastAgPoll = res;

            if (res.Error is null || res.Buckets.Count > 0)
            {
                foreach (var oldKey in liveByRowId.Where(kv => kv.Value.Service == service)
                                                  .Select(kv => kv.Key).ToList())
                {
                    if (!res.Buckets.ContainsKey(oldKey))
                        liveByRowId.Remove(oldKey);
                }

                foreach (var (id, bucket) in res.Buckets)
                    liveByRowId[id] = bucket;
            }

            bool changedConfig = SyncDiscoveredRowsLocked();
            if (changedConfig) save = persist;

            UpdateErrorStateLocked();
            if (lastClaudePoll is not null && lastAgPoll is not null)
                hasCompletedInitialSync = true;

            RebuildCurrentReadingLocked();
        }

        save?.Invoke();
    }

    private static void UpdateErrorStateLocked()
    {
        var claudeRes = lastClaudePoll;
        var agRes = lastAgPoll;

        bool claudeNeedsAuth = claudeRes?.NeedsAuth ?? false;
        bool agNeedsAuth = agRes?.NeedsAuth ?? false;
        bool bothNeedAuth = claudeNeedsAuth && agNeedsAuth;

        bool hasClaudeBuckets = liveByRowId.Values.Any(b => b.Service == RemoteChatService.Claude);
        bool hasAgBuckets = liveByRowId.Values.Any(b => b.Service == RemoteChatService.Antigravity);

        lastNeedsAuth = bothNeedAuth && liveByRowId.Count == 0;
        if (bothNeedAuth && liveByRowId.Count == 0)
        {
            lastError = "Sign in under Claude usage or Antigravity usage";
        }
        else if (liveByRowId.Count == 0)
        {
            lastError = (claudeRes?.Error, agRes?.Error) switch
            {
                (not null, not null) => $"{claudeRes.Error}; {agRes.Error}",
                (not null, null) => claudeRes.Error,
                (null, not null) => agRes.Error,
                _ => null,
            };
        }
        else if (claudeRes?.Error is not null && !hasClaudeBuckets && !claudeRes.NeedsAuth)
        {
            lastError = $"Claude: {claudeRes.Error}";
        }
        else if (claudeRes?.Error is not null && !hasClaudeBuckets && ClaudeUsage.Enabled)
        {
            lastError = $"Claude: {claudeRes.Error}";
        }
        else if (agRes?.Error is not null && !hasAgBuckets && AntigravityUsage.Enabled)
        {
            lastError = $"Antigravity: {agRes.Error}";
        }
        else
        {
            lastError = null;
        }
    }

    private static bool SyncDiscoveredRowsLocked()
    {
        if (settings is null) return false;
        bool changed = false;

        // If a previous poll fell back to the generic "remote:Claude" row before environment metadata
        // was resolved, remove that placeholder once real named Claude servers are discovered.
        bool hasNamedClaudeServer = liveByRowId.Values.Any(b =>
            b.Service == RemoteChatService.Claude &&
            !string.Equals(b.Server, "remote", StringComparison.OrdinalIgnoreCase));
        if (hasNamedClaudeServer && !liveByRowId.ContainsKey("remote:Claude"))
        {
            int removed = settings.Rows.RemoveAll(r =>
                string.Equals(r.Id, "remote:Claude", StringComparison.OrdinalIgnoreCase));
            if (removed > 0) changed = true;
        }

        // Order newly discovered buckets by server name, then Claude before Antigravity.
        var orderedBuckets = liveByRowId.Values
            .OrderBy(b => b.Server, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.Service)
            .ToList();

        foreach (var bucket in orderedBuckets)
        {
            string serviceStr = bucket.Service == RemoteChatService.Antigravity ? "Antigravity" : "Claude";
            var existing = settings.Rows.FirstOrDefault(r =>
                string.Equals(r.Id, bucket.Id, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                var newRow = new RemoteServerRowConfig
                {
                    Id = bucket.Id,
                    Server = bucket.Server,
                    Service = serviceStr,
                    Enabled = true,
                };

                // If another row for the same server already exists, insert right after it so
                // a server's Claude and Antigravity rows stay adjacent by default.
                int lastSameServer = settings.Rows.FindLastIndex(r =>
                    string.Equals(r.Server, bucket.Server, StringComparison.OrdinalIgnoreCase));
                if (lastSameServer >= 0 && lastSameServer + 1 <= settings.Rows.Count)
                    settings.Rows.Insert(lastSameServer + 1, newRow);
                else
                    settings.Rows.Add(newRow);

                changed = true;
            }
            else
            {
                if (!string.Equals(existing.Server, bucket.Server, StringComparison.Ordinal))
                {
                    existing.Server = bucket.Server;
                    changed = true;
                }
                if (!string.Equals(existing.Service, serviceStr, StringComparison.Ordinal))
                {
                    existing.Service = serviceStr;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private static void RebuildCurrentReadingLocked()
    {
        var previous = current;
        if (settings is null || !settings.Enabled)
        {
            current = null;
            if (previous is not null) FireChangedAsync();
            return;
        }

        var rows = new List<RemoteServerRow>();
        foreach (var cfg in settings.Rows)
        {
            if (!cfg.Enabled) continue;

            var svc = string.Equals(cfg.Service, "Antigravity", StringComparison.OrdinalIgnoreCase)
                ? RemoteChatService.Antigravity
                : RemoteChatService.Claude;

            if (liveByRowId.TryGetValue(cfg.Id, out var bucket))
            {
                int waiting = bucket.Chats.Count(c => c.State == RemoteChatState.Waiting);
                int running = bucket.Chats.Count(c => c.State == RemoteChatState.Running);
                int done = bucket.Chats.Count(c => c.State == RemoteChatState.Done);
                rows.Add(new RemoteServerRow(
                    cfg.Id,
                    cfg.Server,
                    cfg.DisplayName,
                    svc,
                    bucket.Online,
                    waiting,
                    running,
                    done,
                    bucket.Chats.ToList()));
            }
            else
            {
                rows.Add(new RemoteServerRow(
                    cfg.Id,
                    cfg.Server,
                    cfg.DisplayName,
                    svc,
                    Online: !hasCompletedInitialSync,
                    WaitingCount: 0,
                    RunningCount: 0,
                    DoneCount: 0,
                    Chats: []));
            }
        }

        var next = new RemoteChatsReading(
            rows,
            lastError,
            lastNeedsAuth,
            Transient: !hasCompletedInitialSync && rows.Count == 0 && lastError is null);

        int totalWaiting = rows.Sum(r => r.WaitingCount);
        int claudeWaiting = rows.Where(r => r.Service == RemoteChatService.Claude).Sum(r => r.WaitingCount);
        int agWaiting = rows.Where(r => r.Service == RemoteChatService.Antigravity).Sum(r => r.WaitingCount);
        bool glowTriggered = false;
        if (hasCompletedInitialSync)
        {
            long nowTick = Environment.TickCount64;
            if (lastSettledWaitingCount is 0 && totalWaiting > 0)
            {
                Interlocked.Exchange(ref attentionGlowStartTick, nowTick);
                glowTriggered = true;
            }
            if (lastClaudeWaitingCount is 0 && claudeWaiting > 0)
            {
                Interlocked.Exchange(ref claudeGlowStartTick, nowTick);
                glowTriggered = true;
            }
            if (lastAgWaitingCount is 0 && agWaiting > 0)
            {
                Interlocked.Exchange(ref agGlowStartTick, nowTick);
                glowTriggered = true;
            }
            lastSettledWaitingCount = totalWaiting;
            lastClaudeWaitingCount = claudeWaiting;
            lastAgWaitingCount = agWaiting;
        }

        current = next;
        if (glowTriggered || !ReadingsEqual(previous, next))
            FireChangedAsync();
    }

    private static void FireChangedAsync()
    {
        var handler = Changed;
        if (handler is null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { handler(); } catch { }
        });
    }

    private static bool ReadingsEqual(RemoteChatsReading? a, RemoteChatsReading? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Error != b.Error || a.NeedsAuth != b.NeedsAuth || a.Transient != b.Transient) return false;
        if (a.Rows.Count != b.Rows.Count) return false;

        for (int i = 0; i < a.Rows.Count; i++)
        {
            var ra = a.Rows[i];
            var rb = b.Rows[i];
            if (ra.Id != rb.Id || ra.Server != rb.Server || ra.Label != rb.Label ||
                ra.Service != rb.Service || ra.Online != rb.Online ||
                ra.WaitingCount != rb.WaitingCount || ra.RunningCount != rb.RunningCount ||
                ra.DoneCount != rb.DoneCount || ra.Chats.Count != rb.Chats.Count)
                return false;

            for (int j = 0; j < ra.Chats.Count; j++)
            {
                var ca = ra.Chats[j];
                var cb = rb.Chats[j];
                if (ca.Id != cb.Id || ca.Title != cb.Title || ca.State != cb.State ||
                    ca.Repo != cb.Repo || ca.Detail != cb.Detail || ca.Unread != cb.Unread)
                    return false;
            }
        }

        return true;
    }

    // ---- Claude Code Remote Control polling --------------------------------

    private sealed record PollServiceResult(
        Dictionary<string, ServerBucket> Buckets,
        string? Error,
        bool NeedsAuth,
        string? Diagnostic = null);

    private sealed record ClaudeEnvInfo(string Server, string? Repo, bool Online, bool Active);

    private static PollServiceResult PollClaude(DateTimeOffset doneCutoff, bool forceFullSync = false)
    {
        var buckets = new Dictionary<string, ServerBucket>(StringComparer.OrdinalIgnoreCase);
        var diag = new List<string>();

        bool allowOAuth = ClaudeUsage.UseClaudeCodeEnabled || ClaudeUsage.HasSignedInOAuth;
        string? oauthToken = allowOAuth
            ? ClaudeUsage.ReadClaudeCodeToken(out _, forceRefresh: false, requireSessionsScope: true)
            : null;
        bool hasSessionKey = ClaudeUsage.HasStoredSessionKey;

        string? orgUuid = ResolveClaudeOrgUuid(oauthToken);

        (int Status, string Body)? envsReply = null;
        (int Status, string Body)? sessionsReply = null;

        bool canUseCachedEnvs = !forceFullSync &&
            !string.IsNullOrWhiteSpace(cachedClaudeEnvsBody) &&
            (DateTimeOffset.UtcNow - cachedClaudeEnvsUtc) < TimeSpan.FromSeconds(15);

        if (canUseCachedEnvs)
            envsReply = (200, cachedClaudeEnvsBody!);

        // 1. Try OAuth token (from ~/.claude/.credentials.json or ScweenSpit's 1-click Claude sign-in).
        //    On regular 2s polls where environments were fetched within the last 15s, only fetch
        //    `/v1/code/sessions?limit=50` (~200ms). When both are needed, fetch them in parallel.
        if (!string.IsNullOrWhiteSpace(oauthToken))
        {
            if (envsReply is null)
            {
                var envTask = Task.Run(() => GetAnthropicOAuth("/v1/environment_providers", oauthToken, orgUuid));
                var sessTask = Task.Run(() => GetAnthropicOAuth("/v1/code/sessions?limit=50", oauthToken, orgUuid));
                Task.WhenAll(envTask, sessTask).GetAwaiter().GetResult();
                envsReply = envTask.Result;
                sessionsReply = sessTask.Result;
            }
            else
            {
                sessionsReply = GetAnthropicOAuth("/v1/code/sessions?limit=50", oauthToken, orgUuid);
            }

            if (envsReply is { Status: 401 or 403 } || sessionsReply is { Status: 401 or 403 })
            {
                var refreshed = ClaudeUsage.ReadClaudeCodeToken(out _, forceRefresh: true, requireSessionsScope: true);
                if (!string.IsNullOrWhiteSpace(refreshed) && refreshed != oauthToken)
                {
                    oauthToken = refreshed;
                    orgUuid = ResolveClaudeOrgUuid(oauthToken) ?? orgUuid;
                    if (envsReply is not { Status: >= 200 and < 300 })
                        envsReply = GetAnthropicOAuth("/v1/environment_providers", oauthToken, orgUuid);
                    if (sessionsReply is not { Status: >= 200 and < 300 })
                        sessionsReply = GetAnthropicOAuth("/v1/code/sessions?limit=50", oauthToken, orgUuid);
                }
            }

            if (envsReply is not { Status: >= 200 and < 300 })
            {
                var v1Envs = GetAnthropicOAuth("/v1/environments", oauthToken, orgUuid);
                if (v1Envs is { Status: >= 200 and < 300 } || envsReply is null)
                    envsReply = v1Envs;
            }

            if (sessionsReply is not { Status: >= 200 and < 300 })
            {
                var v1Sessions = GetAnthropicOAuth("/v1/sessions", oauthToken, orgUuid);
                if (v1Sessions is { Status: >= 200 and < 300 } || sessionsReply is null)
                    sessionsReply = v1Sessions;
            }

            diag.Add($"OAuth: envs={envsReply?.Status.ToString() ?? "unreachable"}, sessions={sessionsReply?.Status.ToString() ?? "unreachable"}");
        }

        // 2. Fall back to the stored browser sessionKey cookie whenever OAuth was unavailable or did not
        //    return 2xx.
        if (hasSessionKey &&
            (envsReply is not { Status: >= 200 and < 300 } || sessionsReply is not { Status: >= 200 and < 300 }))
        {
            if (envsReply is not { Status: >= 200 and < 300 })
            {
                var keyEnvs = ClaudeUsage.GetWithStoredSessionKey("/v1/environment_providers", orgUuid);
                if (keyEnvs is not { Status: >= 200 and < 300 })
                {
                    var altKeyEnvs = ClaudeUsage.GetWithStoredSessionKey("/v1/environments", orgUuid);
                    if (altKeyEnvs is { Status: >= 200 and < 300 } || keyEnvs is null)
                        keyEnvs = altKeyEnvs;
                }
                if (keyEnvs is not null && (envsReply is null || keyEnvs.Value.Status < 400))
                    envsReply = keyEnvs;
            }

            if (sessionsReply is not { Status: >= 200 and < 300 })
            {
                var keySessions = ClaudeUsage.GetWithStoredSessionKey("/v1/code/sessions?limit=50", orgUuid);
                if (keySessions is not { Status: >= 200 and < 300 })
                {
                    var altKeySessions = ClaudeUsage.GetWithStoredSessionKey("/v1/sessions", orgUuid);
                    if (altKeySessions is { Status: >= 200 and < 300 } || keySessions is null)
                        keySessions = altKeySessions;
                }
                if (keySessions is not null && (sessionsReply is null || keySessions.Value.Status < 400))
                    sessionsReply = keySessions;
            }

            diag.Add($"SessionKey fallback: envs={envsReply?.Status.ToString() ?? "unreachable"}, sessions={sessionsReply?.Status.ToString() ?? "unreachable"}");
        }

        if (string.IsNullOrWhiteSpace(oauthToken) && !hasSessionKey)
            return new PollServiceResult(buckets, "No Claude login configured", NeedsAuth: true, string.Join(" | ", diag));

        if (sessionsReply is null && envsReply is null)
            return new PollServiceResult(buckets, "api.anthropic.com unreachable", NeedsAuth: false, string.Join(" | ", diag));

        bool envsOk = envsReply is { Status: >= 200 and < 300 };
        bool sessionsOk = sessionsReply is { Status: >= 200 and < 300 };

        if (envsOk && !canUseCachedEnvs)
        {
            cachedClaudeEnvsBody = envsReply!.Value.Body;
            cachedClaudeEnvsUtc = DateTimeOffset.UtcNow;
        }

        if (!envsOk && !sessionsOk)
        {
            bool envsAuthRefused = envsReply is { } er && ClaudeUsage.IsAuthRejection(er.Status, er.Body);
            bool sessionsAuthRefused = sessionsReply is { } sr && ClaudeUsage.IsAuthRejection(sr.Status, sr.Body);

            if (envsAuthRefused || sessionsAuthRefused)
            {
                string combinedBody = $"{envsReply?.Body} {sessionsReply?.Body}";
                bool isScopeErr = (envsReply?.Status == 403 || sessionsReply?.Status == 403)
                    && (combinedBody.Contains("scope", StringComparison.OrdinalIgnoreCase)
                     || combinedBody.Contains("permission", StringComparison.OrdinalIgnoreCase));

                string msg = isScopeErr
                    ? "Claude login needs sessions permission — click 'Sign in with Claude' in Settings"
                    : "Claude login expired — click 'Sign in with Claude' in Settings";
                return new PollServiceResult(buckets, msg, NeedsAuth: true, string.Join(" | ", diag));
            }

            if (envsReply?.Status == 403 || sessionsReply?.Status == 403)
                return new PollServiceResult(buckets, "claude.ai challenged the request", NeedsAuth: false, string.Join(" | ", diag));

            int errCode = sessionsReply?.Status ?? envsReply?.Status ?? 0;
            return new PollServiceResult(buckets, $"Claude remote HTTP {errCode}", NeedsAuth: false, string.Join(" | ", diag));
        }

        var envById = new Dictionary<string, ClaudeEnvInfo>(StringComparer.Ordinal);
        var envByRepo = new Dictionary<string, ClaudeEnvInfo>(StringComparer.OrdinalIgnoreCase);

        if (envsOk)
            ParseClaudeEnvironments(envsReply!.Value.Body, envById, envByRepo, buckets);

        if (sessionsOk)
        {
            // If we used cached environments and a newly started session references an unknown
            // environment_id, refresh `/v1/environment_providers` once so it maps to its server name.
            if (canUseCachedEnvs && HasUnmappedConnectedSessionEnv(sessionsReply!.Value.Body, envById))
            {
                var freshEnvs = !string.IsNullOrWhiteSpace(oauthToken)
                    ? GetAnthropicOAuth("/v1/environment_providers", oauthToken, orgUuid)
                    : ClaudeUsage.GetWithStoredSessionKey("/v1/environment_providers", orgUuid);
                if (freshEnvs is { Status: >= 200 and < 300 })
                {
                    cachedClaudeEnvsBody = freshEnvs.Value.Body;
                    cachedClaudeEnvsUtc = DateTimeOffset.UtcNow;
                    envById.Clear();
                    envByRepo.Clear();
                    buckets.Clear();
                    ParseClaudeEnvironments(freshEnvs.Value.Body, envById, envByRepo, buckets);
                }
            }

            ParseClaudeSessions(sessionsReply!.Value.Body, envById, envByRepo, buckets, doneCutoff);
        }
        else if (sessionsReply is not null && buckets.Count == 0)
        {
            return new PollServiceResult(buckets, $"Claude sessions HTTP {sessionsReply.Value.Status}", NeedsAuth: false, string.Join(" | ", diag));
        }

        foreach (var b in buckets.Values)
        {
            b.Chats = b.Chats
                .OrderBy(c => c.State) // Waiting (0), Running (1), Done (2)
                .ThenByDescending(c => c.LastUpdated)
                .ToList();
        }

        return new PollServiceResult(buckets, null, NeedsAuth: false, string.Join(" | ", diag));
    }

    private static bool HasUnmappedConnectedSessionEnv(string sessionsBody, Dictionary<string, ClaudeEnvInfo> envById)
    {
        try
        {
            using var doc = JsonDocument.Parse(sessionsBody);
            if (!doc.RootElement.TryGetProperty("data", out var list) || list.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var s in list.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                var conn = Text(s, "connection_status");
                if (!string.Equals(conn, "connected", StringComparison.OrdinalIgnoreCase)) continue;
                var envId = Text(s, "environment_id");
                if (!string.IsNullOrWhiteSpace(envId) && !envById.ContainsKey(envId))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static void ParseClaudeEnvironments(
        string body,
        Dictionary<string, ClaudeEnvInfo> envById,
        Dictionary<string, ClaudeEnvInfo> envByRepo,
        Dictionary<string, ServerBucket> buckets)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            JsonElement envs;
            if (!doc.RootElement.TryGetProperty("environments", out envs) &&
                !doc.RootElement.TryGetProperty("data", out envs))
                return;

            if (envs.ValueKind != JsonValueKind.Array)
                return;

            foreach (var env in envs.EnumerateArray())
            {
                if (env.ValueKind != JsonValueKind.Object) continue;

                // Support both `/v1/environment_providers` (`kind: "bridge"`, `environment_id`, `bridge_info`)
                // and `/v1/environments` (`config.type: "bridge"`, `id`, `config.machine_name`).
                var kind = Text(env, "kind");
                JsonElement cfgObj = default;
                bool hasCfgObj = env.TryGetProperty("config", out cfgObj) && cfgObj.ValueKind == JsonValueKind.Object;
                if (string.IsNullOrWhiteSpace(kind) && hasCfgObj)
                    kind = Text(cfgObj, "type");

                if (!string.Equals(kind, "bridge", StringComparison.OrdinalIgnoreCase)) continue;

                var state = Text(env, "state");
                bool activeEnv = string.Equals(state, "active", StringComparison.OrdinalIgnoreCase);

                var envId = Text(env, "environment_id") ?? Text(env, "id");
                if (string.IsNullOrWhiteSpace(envId)) continue;

                string? machineName = null;
                string? directory = null;
                string? gitRepoUrl = null;
                bool online = false;

                if (env.TryGetProperty("bridge_info", out var bridge) && bridge.ValueKind == JsonValueKind.Object)
                {
                    machineName = Text(bridge, "machine_name");
                    directory = Text(bridge, "directory");
                    gitRepoUrl = Text(bridge, "git_repo_url");
                    online = Bool(bridge, "online");
                }
                else if (hasCfgObj)
                {
                    machineName = Text(cfgObj, "machine_name");
                    directory = Text(cfgObj, "directory");
                    gitRepoUrl = Text(cfgObj, "git_repo_url");
                    online = activeEnv;
                }

                var envName = Text(env, "name");
                if (string.IsNullOrWhiteSpace(machineName) && !string.IsNullOrWhiteSpace(envName))
                {
                    var parts = envName.Split(':');
                    if (parts.Length > 0) machineName = parts[0];
                    if (string.IsNullOrWhiteSpace(directory) && parts.Length > 1) directory = parts[1];
                }

                if (string.IsNullOrWhiteSpace(machineName)) continue;

                string server = NormalizeServerName(machineName);
                string? repo = ShortRepoName(directory) ?? ShortRepoName(NormalizeGitRepoSlug(gitRepoUrl));
                var info = new ClaudeEnvInfo(server, repo, online, activeEnv);
                envById[envId] = info;

                // Index by git repository slug ("owner/repo") and short folder name ("repo") so
                // CLI sessions started with `remote-control-auto` (which omit `environment_id` in
                // `/v1/code/sessions` but carry `config.sources` / `current_branches`) still map
                // back to their server (`hx525`, `lp-server-vm`, etc.) rather than "remote".
                var slug = NormalizeGitRepoSlug(gitRepoUrl);
                if (!string.IsNullOrWhiteSpace(slug))
                {
                    if (!envByRepo.TryGetValue(slug, out var prev) || (activeEnv && !prev.Active) || online)
                        envByRepo[slug] = info;
                }
                if (!string.IsNullOrWhiteSpace(repo))
                {
                    if (!envByRepo.TryGetValue(repo, out var prev) || (activeEnv && !prev.Active) || online)
                        envByRepo[repo] = info;
                }

                if (activeEnv)
                {
                    string rowId = RowKey(server, RemoteChatService.Claude);
                    if (!buckets.TryGetValue(rowId, out var bucket))
                    {
                        bucket = new ServerBucket
                        {
                            Id = rowId,
                            Server = server,
                            Service = RemoteChatService.Claude,
                            Online = online,
                        };
                        buckets[rowId] = bucket;
                    }
                    else if (online)
                    {
                        bucket.Online = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.WriteOnce("claude-envs-parse", $"could not parse Claude environments: {ex.Message}");
        }
    }

    private static void ParseClaudeSessions(
        string body,
        Dictionary<string, ClaudeEnvInfo> envById,
        Dictionary<string, ClaudeEnvInfo> envByRepo,
        Dictionary<string, ServerBucket> buckets,
        DateTimeOffset doneCutoff)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var list) || list.ValueKind != JsonValueKind.Array)
                return;

            foreach (var s in list.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;

                var id = Text(s, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;

                var title = (Text(s, "title") ?? "").Trim();
                if (IsInternalClaudeSessionTitle(title)) continue;
                if (string.IsNullOrWhiteSpace(title)) title = "Untitled session";

                // Support both `/v1/code/sessions` (`status` + `worker_status` + `connection_status`)
                // and `/v1/sessions` (`session_status`).
                var status = Text(s, "status") ?? "";
                var sessionStatus = Text(s, "session_status") ?? "";
                var conn = Text(s, "connection_status") ?? "";
                var worker = Text(s, "worker_status") ?? "";
                var bucketKind = Text(s, "status_bucket") ?? "";
                bool unread = Bool(s, "unread");

                bool isArchived = string.Equals(status, "archived", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(status, "deleted", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(sessionStatus, "archived", StringComparison.OrdinalIgnoreCase);
                bool isActive = !isArchived && (
                    string.Equals(status, "active", StringComparison.OrdinalIgnoreCase) ||
                    (string.IsNullOrEmpty(status) && !string.IsNullOrEmpty(sessionStatus)));
                bool isConnected = string.IsNullOrEmpty(conn) ||
                    string.Equals(conn, "connected", StringComparison.OrdinalIgnoreCase);

                string? taskSummary = null;
                string? needsAction = null;
                string? statusCategory = null;
                string? statusDetail = null;
                string? pendingActionDetail = null;
                bool hasPendingActionObj = false;

                if (s.TryGetProperty("requires_action_details", out var rad) && rad.ValueKind == JsonValueKind.Object)
                {
                    hasPendingActionObj = true;
                    pendingActionDetail = Text(rad, "action_description")
                                       ?? Text(rad, "description")
                                       ?? Text(rad, "tool_name");
                }
                else if (s.TryGetProperty("requires_action_details_list", out var radList)
                      && radList.ValueKind == JsonValueKind.Array
                      && radList.GetArrayLength() > 0)
                {
                    hasPendingActionObj = true;
                    var firstRad = radList[0];
                    if (firstRad.ValueKind == JsonValueKind.Object)
                    {
                        pendingActionDetail = Text(firstRad, "action_description")
                                           ?? Text(firstRad, "description")
                                           ?? Text(firstRad, "tool_name");
                    }
                }

                if (s.TryGetProperty("external_metadata", out var ext) && ext.ValueKind == JsonValueKind.Object)
                {
                    taskSummary = Text(ext, "task_summary");
                    if (ext.TryGetProperty("pending_action", out var pa) && pa.ValueKind == JsonValueKind.Object)
                    {
                        hasPendingActionObj = true;
                        pendingActionDetail ??= Text(pa, "action_description")
                                             ?? Text(pa, "description")
                                             ?? Text(pa, "tool_name");
                    }
                    else if (ext.TryGetProperty("pending_actions", out var pas)
                          && pas.ValueKind == JsonValueKind.Array
                          && pas.GetArrayLength() > 0)
                    {
                        hasPendingActionObj = true;
                        var firstPa = pas[0];
                        if (firstPa.ValueKind == JsonValueKind.Object)
                        {
                            pendingActionDetail ??= Text(firstPa, "action_description")
                                                 ?? Text(firstPa, "description")
                                                 ?? Text(firstPa, "tool_name");
                        }
                    }
                    if (ext.TryGetProperty("post_turn_summary", out var pts) && pts.ValueKind == JsonValueKind.Object)
                    {
                        needsAction = Text(pts, "needs_action");
                        statusCategory = Text(pts, "status_category");
                        statusDetail = Text(pts, "status_detail") ?? Text(pts, "recent_action");
                    }
                }

                if (s.TryGetProperty("post_turn_summary", out var topPts) && topPts.ValueKind == JsonValueKind.Object)
                {
                    needsAction ??= Text(topPts, "needs_action");
                    statusCategory ??= Text(topPts, "status_category");
                    statusDetail ??= Text(topPts, "status_detail") ?? Text(topPts, "recent_action");
                }

                var updated = ParseIso(Text(s, "last_event_at"))
                           ?? ParseIso(Text(s, "updated_at"))
                           ?? ParseIso(Text(s, "created_at"))
                           ?? DateTimeOffset.MinValue;

                var postTurnWaitingCutoff = DateTimeOffset.UtcNow - PostTurnWaitingWindow;

                bool isWorkerExplicitlyIdle = string.Equals(worker, "idle", StringComparison.OrdinalIgnoreCase);
                bool isWorkerExplicitlyRunning =
                    string.Equals(worker, "running", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(worker, "starting", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(sessionStatus, "running", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(sessionStatus, "starting", StringComparison.OrdinalIgnoreCase);

                // 1. Mid-turn permission / tool / question block: worker is explicitly paused waiting on user action.
                //    Do NOT include `bucketKind == "blocked"` here because Claude's backend also sets
                //    `status_bucket = "blocked"` on idle completed turns when `post_turn_summary` is `need_input`.
                bool isBlockedMidTurn = isActive && isConnected && updated >= doneCutoff && !isWorkerExplicitlyRunning && (
                    string.Equals(worker, "requires_action", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(sessionStatus, "requires_action", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bucketKind, "requires_action", StringComparison.OrdinalIgnoreCase) ||
                    (!isWorkerExplicitlyIdle && hasPendingActionObj));

                // 2. Actively executing a turn: takes priority over `post_turn_summary` from the previous
                //    turn so a resolved chat turns blue immediately when the worker resumes running,
                //    rather than staying yellow until the new turn finishes and overwrites `post_turn_summary`.
                bool isWorkerRunning = !isBlockedMidTurn && isActive && isConnected && (
                    isWorkerExplicitlyRunning ||
                    (!isWorkerExplicitlyIdle &&
                     !string.Equals(worker, "WORKER_STATUS_UNSPECIFIED", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(bucketKind, "working", StringComparison.OrdinalIgnoreCase)));

                // 3. Post-turn waiting for input: turn finished (worker idle) and asked the user a question.
                //    Only flag as needing help (`!`) briefly right after the turn finishes; once it has been
                //    sitting idle past `PostTurnWaitingWindow` (or marked read), settle it to Done (`✓`).
                bool isWaitingPostTurn = !isBlockedMidTurn && !isWorkerRunning && isActive && isConnected &&
                    unread && updated >= postTurnWaitingCutoff && (
                    string.Equals(statusCategory, "need_input", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(statusCategory, "needs_input", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(statusCategory, "blocked", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bucketKind, "blocked", StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrWhiteSpace(needsAction));

                bool isWaiting = isBlockedMidTurn || isWaitingPostTurn;
                bool isRunning = isWorkerRunning;

                bool isDone = !isWaiting && !isRunning && isActive && updated >= doneCutoff;

                if (!isWaiting && !isRunning && !isDone) continue;

                var envId = Text(s, "environment_id") ?? "";
                string? sessionRepoSlug = ExtractClaudeSessionRepoSlug(s);
                string? sessionShortRepo = ShortRepoName(sessionRepoSlug);

                string server;
                string? repo = sessionShortRepo;
                bool envOnline = isConnected;

                if (!string.IsNullOrWhiteSpace(envId) && envById.TryGetValue(envId, out var envInfo))
                {
                    server = envInfo.Server;
                    repo = envInfo.Repo ?? sessionShortRepo;
                    envOnline |= envInfo.Online;
                }
                else if (!string.IsNullOrWhiteSpace(sessionRepoSlug) && envByRepo.TryGetValue(sessionRepoSlug, out var bySlug))
                {
                    server = bySlug.Server;
                    repo = bySlug.Repo ?? sessionShortRepo;
                    envOnline |= bySlug.Online;
                }
                else if (!string.IsNullOrWhiteSpace(sessionShortRepo) && envByRepo.TryGetValue(sessionShortRepo, out var byShort))
                {
                    server = byShort.Server;
                    repo = byShort.Repo ?? sessionShortRepo;
                    envOnline |= byShort.Online;
                }
                else
                {
                    // Skip orphaned sessions that cannot be mapped to a known server unless currently connected.
                    if (!isConnected) continue;
                    server = buckets.Count == 1 ? buckets.Values.First().Server : "remote";
                }

                var chatState = isWaiting ? RemoteChatState.Waiting
                              : isRunning ? RemoteChatState.Running
                              : RemoteChatState.Done;

                string? detail = isWaiting ? (pendingActionDetail ?? needsAction ?? statusDetail ?? "Waiting for input")
                               : isRunning ? (taskSummary ?? statusDetail ?? "Working")
                               : statusDetail;

                string rowId = RowKey(server, RemoteChatService.Claude);
                if (!buckets.TryGetValue(rowId, out var serverBucket))
                {
                    serverBucket = new ServerBucket
                    {
                        Id = rowId,
                        Server = server,
                        Service = RemoteChatService.Claude,
                        Online = envOnline,
                    };
                    buckets[rowId] = serverBucket;
                }
                else if (envOnline)
                {
                    serverBucket.Online = true;
                }

                string webSessionId = ToWebSessionId(id);
                serverBucket.Chats.Add(new RemoteChatItem(
                    id,
                    CleanTitle(title) ?? "Untitled chat",
                    server,
                    RemoteChatService.Claude,
                    chatState,
                    repo,
                    CleanTitle(detail),
                    unread,
                    updated,
                    $"{ClaudeCodeWebUrl}/{webSessionId}"));
            }
        }
        catch (Exception ex)
        {
            Log.WriteOnce("claude-sessions-parse", $"could not parse Claude sessions: {ex.Message}");
        }
    }

    private static string ToWebSessionId(string id) =>
        id.StartsWith("cse_", StringComparison.Ordinal) ? "session_" + id[4..] : id;

    private static string? ExtractClaudeSessionRepoSlug(JsonElement session)
    {
        if (session.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object
            && ExtractRepoFromSources(cfg) is { Length: > 0 } s1)
            return s1;

        if (session.TryGetProperty("session_context", out var ctx) && ctx.ValueKind == JsonValueKind.Object
            && ExtractRepoFromSources(ctx) is { Length: > 0 } s2)
            return s2;

        if (session.TryGetProperty("external_metadata", out var ext) && ext.ValueKind == JsonValueKind.Object
            && ext.TryGetProperty("current_branches", out var branches) && branches.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in branches.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(prop.Name))
                    return NormalizeGitRepoSlug(prop.Name);
            }
        }

        return null;
    }

    private static string? ExtractRepoFromSources(JsonElement container)
    {
        if (!container.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var src in sources.EnumerateArray())
        {
            if (src.ValueKind != JsonValueKind.Object) continue;
            if (Text(src, "url") is { Length: > 0 } url)
                return NormalizeGitRepoSlug(url);
        }
        return null;
    }

    private static string? NormalizeGitRepoSlug(string? rawUrlOrSlug)
    {
        if (string.IsNullOrWhiteSpace(rawUrlOrSlug)) return null;
        var s = rawUrlOrSlug.Trim().TrimEnd('/');
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            s = s[..^4];

        int colonSlash = s.IndexOf("://", StringComparison.Ordinal);
        if (colonSlash >= 0)
        {
            int firstSlashAfterHost = s.IndexOf('/', colonSlash + 3);
            if (firstSlashAfterHost >= 0 && firstSlashAfterHost + 1 < s.Length)
                s = s[(firstSlashAfterHost + 1)..];
        }
        else if (s.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            int colon = s.IndexOf(':');
            if (colon >= 0 && colon + 1 < s.Length)
                s = s[(colon + 1)..];
        }

        return s.Length > 0 ? s : null;
    }

    private static bool IsInternalClaudeSessionTitle(string title) =>
        title.Contains("__CBU_POOLED__", StringComparison.OrdinalIgnoreCase) ||
        title.Equals("__warming__", StringComparison.OrdinalIgnoreCase) ||
        title.StartsWith("ditto:", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveClaudeOrgUuid(string? oauthToken)
    {
        if (ClaudeUsage.GetConfiguredOrgId() is { Length: > 0 } configuredOrg)
        {
            cachedClaudeOrgUuid = configuredOrg;
            return configuredOrg;
        }

        try
        {
            if (File.Exists(ClaudeGlobalConfigPath))
            {
                var writeUtc = File.GetLastWriteTimeUtc(ClaudeGlobalConfigPath);
                if (writeUtc != cachedClaudeGlobalConfigWriteUtc || string.IsNullOrWhiteSpace(cachedClaudeOrgUuid))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(ClaudeGlobalConfigPath));
                    if (doc.RootElement.TryGetProperty("oauthAccount", out var acct)
                        && acct.ValueKind == JsonValueKind.Object
                        && Text(acct, "organizationUuid") is { Length: > 0 } fileOrg)
                    {
                        cachedClaudeOrgUuid = fileOrg;
                        cachedClaudeGlobalConfigWriteUtc = writeUtc;
                        return fileOrg;
                    }
                }
            }
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(cachedClaudeOrgUuid))
            return cachedClaudeOrgUuid;

        if (!string.IsNullOrWhiteSpace(oauthToken))
        {
            var profile = GetAnthropicOAuth("/api/oauth/profile", oauthToken, null);
            if (profile is { Status: 200 })
            {
                try
                {
                    using var doc = JsonDocument.Parse(profile.Value.Body);
                    if (doc.RootElement.TryGetProperty("organization", out var org)
                        && org.ValueKind == JsonValueKind.Object
                        && Text(org, "uuid") is { Length: > 0 } uuid)
                    {
                        cachedClaudeOrgUuid = uuid;
                        return uuid;
                    }
                }
                catch { }
            }
        }

        return null;
    }

    private static (int Status, string Body)? GetAnthropicOAuth(string pathAndQuery, string token, string? orgUuid)
    {
        try
        {
            string betaHeader = pathAndQuery.StartsWith("/v1/environments", StringComparison.OrdinalIgnoreCase)
                ? "environments-2025-11-01,oauth-2025-04-20"
                : AnthropicBetaHeader;

            using var req = new HttpRequestMessage(HttpMethod.Get, AnthropicApiBase + pathAndQuery);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            req.Headers.TryAddWithoutValidation("anthropic-beta", betaHeader);
            req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersionHeader);
            if (!string.IsNullOrWhiteSpace(orgUuid))
                req.Headers.TryAddWithoutValidation("x-organization-uuid", orgUuid);
            req.Headers.TryAddWithoutValidation("User-Agent", ClaudeCodeUserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var resp = http.Send(req, HttpCompletionOption.ResponseContentRead);
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return ((int)resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            Log.WriteOnce("claude-remote-http", $"Claude remote request failed ({pathAndQuery}): {ex.Message}");
            return null;
        }
    }

    // ---- Antigravity Remote Control polling & real-time SSE streaming ------

    private sealed record AntigravityInstance(string Uuid, string RawName, string Server, bool Connected);

    private static PollServiceResult PollAntigravity(DateTimeOffset doneCutoff, bool forceFullSync = false, CancellationToken ct = default)
    {
        var buckets = new Dictionary<string, ServerBucket>(StringComparer.OrdinalIgnoreCase);

        var accessToken = AntigravityUsage.GetValidAccessToken(forceRefresh: false);
        if (string.IsNullOrWhiteSpace(accessToken))
            return new PollServiceResult(buckets, "No Google login configured for Antigravity", NeedsAuth: true);

        List<AntigravityInstance> instances;
        bool needInstanceListFetch;
        lock (agStreamLock)
        {
            needInstanceListFetch = forceFullSync ||
                cachedAgInstances.Count == 0 ||
                (DateTimeOffset.UtcNow - lastAgListInstancesUtc) >= TimeSpan.FromSeconds(12);
            instances = cachedAgInstances.ToList();
        }

        if (needInstanceListFetch)
        {
            var listReply = GetCloudCode(accessToken, ListInstancesPath);
            if (listReply is { Status: 401 })
            {
                accessToken = AntigravityUsage.GetValidAccessToken(forceRefresh: true);
                if (!string.IsNullOrWhiteSpace(accessToken))
                    listReply = GetCloudCode(accessToken, ListInstancesPath);
            }

            if (listReply is null)
            {
                if (instances.Count == 0)
                    return new PollServiceResult(buckets, "cloudcode-pa.googleapis.com unreachable", NeedsAuth: false);
            }
            else if (listReply.Value.Status is 401 or 403)
            {
                return new PollServiceResult(buckets, "Google login for Antigravity expired", NeedsAuth: true);
            }
            else if (listReply.Value.Status != 200)
            {
                if (instances.Count == 0)
                    return new PollServiceResult(buckets, $"Antigravity listInstances HTTP {listReply.Value.Status}", NeedsAuth: false);
            }
            else
            {
                instances = ParseAntigravityInstances(listReply.Value.Body);
                lock (agStreamLock)
                {
                    cachedAgInstances = instances;
                    lastAgListInstancesUtc = DateTimeOffset.UtcNow;
                }
            }
        }

        var connected = instances.Where(i => i.Connected).ToList();
        var needUnaryFetch = new List<AntigravityInstanceStreamState>();

        lock (agStreamLock)
        {
            var connectedUuids = connected.Select(i => i.Uuid).ToHashSet(StringComparer.Ordinal);
            foreach (var staleUuid in agStreamsByUuid.Keys.Where(u => !connectedUuids.Contains(u)).ToList())
            {
                agStreamsByUuid[staleUuid].Dispose();
                agStreamsByUuid.Remove(staleUuid);
            }

            foreach (var inst in connected)
            {
                if (!agStreamsByUuid.TryGetValue(inst.Uuid, out var state))
                {
                    state = new AntigravityInstanceStreamState { Instance = inst };
                    agStreamsByUuid[inst.Uuid] = state;
                }
                else
                {
                    state.Instance = inst;
                }

                if (!state.StreamRunning && enabled && !ct.IsCancellationRequested)
                {
                    state.StreamRunning = true;
                    var capturedState = state;
                    _ = Task.Run(() => RunAntigravityStreamLoopAsync(capturedState, ct), ct);
                }

                // Only perform the heavy unary GetAllCascadeTrajectories call when we don't yet have
                // a snapshot or if the real-time SSE stream is currently disconnected.
                bool unaryStale = (DateTimeOffset.UtcNow - state.LastUnaryFetchUtc) >= TimeSpan.FromSeconds(12);
                if (!state.HasSnapshot || (!state.StreamConnected && (forceFullSync || unaryStale)))
                {
                    needUnaryFetch.Add(state);
                }
            }
        }

        if (needUnaryFetch.Count > 0 && !string.IsNullOrWhiteSpace(accessToken))
        {
            Parallel.ForEach(needUnaryFetch, state =>
            {
                FetchAntigravityInstanceTrajectoriesUnary(accessToken!, state);
            });
        }

        BuildAntigravityBuckets(doneCutoff, buckets);
        return new PollServiceResult(buckets, null, NeedsAuth: false);
    }

    private static void BuildAntigravityBuckets(DateTimeOffset doneCutoff, Dictionary<string, ServerBucket> buckets)
    {
        lock (agStreamLock)
        {
            foreach (var state in agStreamsByUuid.Values)
            {
                string rowId = RowKey(state.Instance.Server, RemoteChatService.Antigravity);
                if (!buckets.TryGetValue(rowId, out var bucket))
                {
                    bucket = new ServerBucket
                    {
                        Id = rowId,
                        Server = state.Instance.Server,
                        Service = RemoteChatService.Antigravity,
                        Online = true,
                    };
                    buckets[rowId] = bucket;
                }

                foreach (var t in state.Trajectories.Values)
                {
                    var item = ToAntigravityChatItem(t, doneCutoff);
                    if (item is not null)
                        bucket.Chats.Add(item);
                }
            }
        }

        foreach (var b in buckets.Values)
        {
            b.Chats = b.Chats
                .OrderBy(c => c.State)
                .ThenByDescending(c => c.LastUpdated)
                .ToList();
        }
    }

    private static void PublishAntigravityStreamUpdate()
    {
        int doneWindowHours;
        lock (gate)
        {
            if (settings is not { Enabled: true }) return;
            doneWindowHours = Math.Clamp(settings.DoneWindowHours, 1, 168);
        }

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(doneWindowHours);
        var buckets = new Dictionary<string, ServerBucket>(StringComparer.OrdinalIgnoreCase);
        BuildAntigravityBuckets(cutoff, buckets);
        ApplyServiceResult(RemoteChatService.Antigravity, new PollServiceResult(buckets, null, NeedsAuth: false));
    }

    private static async Task RunAntigravityStreamLoopAsync(AntigravityInstanceStreamState state, CancellationToken parentCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(parentCt, state.Cts.Token);
        var ct = linked.Token;

        try
        {
            while (!ct.IsCancellationRequested && enabled)
            {
                try
                {
                    var accessToken = AntigravityUsage.GetValidAccessToken(forceRefresh: false);
                    if (string.IsNullOrWhiteSpace(accessToken))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3), ct);
                        continue;
                    }

                    string reqBody = JsonSerializer.Serialize(new
                    {
                        targetInstanceUuid = state.Instance.Uuid,
                        rpcPath = SubscribeSummariesRpc,
                        payload = SubscribeSummariesPayloadBase64,
                        headers = new Dictionary<string, string>
                        {
                            ["Content-Type"] = "application/connect+json",
                            ["Connect-Protocol-Version"] = "1",
                        },
                    });

                    using var content = new StringContent(reqBody, Encoding.UTF8, "application/json");
                    using var req = new HttpRequestMessage(HttpMethod.Post, CloudCodeBaseUrl + StreamProxyCommandPath)
                    {
                        Content = content,
                    };
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
                    req.Headers.TryAddWithoutValidation("User-Agent", "antigravity");
                    req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

                    using var resp = await streamHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        AntigravityUsage.GetValidAccessToken(forceRefresh: true);
                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                        continue;
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(4), ct);
                        continue;
                    }

                    lock (agStreamLock)
                    {
                        state.StreamConnected = true;
                    }

                    using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    var frameBuffer = new List<byte>(65536);
                    bool isFirstFrame = true;

                    while (!ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(ct);
                        if (line is null) break;
                        if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                        var dataJson = line[5..].Trim();
                        if (!dataJson.StartsWith('{')) continue;

                        string? payloadB64 = null;
                        using (var evtDoc = JsonDocument.Parse(dataJson))
                        {
                            payloadB64 = Text(evtDoc.RootElement, "payload");
                        }

                        if (string.IsNullOrEmpty(payloadB64)) continue;

                        byte[] chunk = Convert.FromBase64String(payloadB64);
                        frameBuffer.AddRange(chunk);

                        bool endOfStream = false;
                        while (frameBuffer.Count >= 5)
                        {
                            byte flags = frameBuffer[0];
                            int frameLen = (frameBuffer[1] << 24)
                                         | (frameBuffer[2] << 16)
                                         | (frameBuffer[3] << 8)
                                         | frameBuffer[4];

                            if (frameLen < 0 || frameLen > 32 * 1024 * 1024)
                            {
                                endOfStream = true;
                                break;
                            }

                            if (frameBuffer.Count < 5 + frameLen)
                                break;

                            byte[] frameBytes = frameBuffer.GetRange(5, frameLen).ToArray();
                            frameBuffer.RemoveRange(0, 5 + frameLen);

                            if ((flags & 0x02) != 0)
                            {
                                endOfStream = true;
                                break;
                            }

                            if (flags == 0 && frameBytes.Length > 0)
                            {
                                ApplyAntigravityStreamFrame(state, frameBytes, isFirstFrame);
                                isFirstFrame = false;
                            }
                        }

                        if (endOfStream) break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.WriteOnce($"ag-stream-{state.Instance.Server}",
                        $"Antigravity summary stream reconnecting for {state.Instance.Server}: {ex.Message}");
                }
                finally
                {
                    lock (agStreamLock)
                    {
                        state.StreamConnected = false;
                    }
                }

                if (!ct.IsCancellationRequested)
                    await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (agStreamLock)
            {
                state.StreamConnected = false;
                state.StreamRunning = false;
            }
        }
    }

    private static void ApplyAntigravityStreamFrame(
        AntigravityInstanceStreamState state,
        byte[] frameBytes,
        bool isFirstFrame)
    {
        bool changed = false;
        try
        {
            using var doc = JsonDocument.Parse(frameBytes);
            var root = doc.RootElement;

            lock (agStreamLock)
            {
                if (isFirstFrame && root.TryGetProperty("updates", out _))
                {
                    state.Trajectories.Clear();
                    changed = true;
                }

                if (root.TryGetProperty("updates", out var updates) && updates.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in updates.EnumerateObject())
                    {
                        var parsed = ParseAntigravityTrajectorySummary(prop.Name, prop.Value, state.Instance.Server);
                        if (parsed is null || parsed.Killed || parsed.Archived)
                        {
                            if (state.Trajectories.Remove(prop.Name))
                                changed = true;
                        }
                        else
                        {
                            state.Trajectories[prop.Name] = parsed;
                            changed = true;
                        }
                    }
                }

                if (root.TryGetProperty("deletes", out var deletes) && deletes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var del in deletes.EnumerateArray())
                    {
                        if (del.ValueKind == JsonValueKind.String && del.GetString() is { Length: > 0 } id)
                        {
                            if (state.Trajectories.Remove(id))
                                changed = true;
                        }
                    }
                }

                state.HasSnapshot = true;
                state.LastUnaryFetchUtc = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex)
        {
            Log.WriteOnce($"ag-stream-parse-{state.Instance.Server}",
                $"could not parse Antigravity stream frame for {state.Instance.Server}: {ex.Message}");
        }

        if (changed)
            PublishAntigravityStreamUpdate();
    }

    private static List<AntigravityInstance> ParseAntigravityInstances(string body)
    {
        var result = new List<AntigravityInstance>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("instances", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var inst in arr.EnumerateArray())
            {
                if (inst.ValueKind != JsonValueKind.Object) continue;
                var uuid = Text(inst, "uuid");
                if (string.IsNullOrWhiteSpace(uuid)) continue;

                string? rawName = null;
                if (inst.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                    rawName = Text(meta, "name");

                if (string.IsNullOrWhiteSpace(rawName)) rawName = uuid[..Math.Min(8, uuid.Length)];

                var status = Text(inst, "status") ?? "";
                bool connected = string.Equals(status, "INSTANCE_STATUS_CONNECTED", StringComparison.OrdinalIgnoreCase);

                result.Add(new AntigravityInstance(uuid, rawName, NormalizeServerName(rawName), connected));
            }
        }
        catch (Exception ex)
        {
            Log.WriteOnce("ag-instances-parse", $"could not parse Antigravity instances: {ex.Message}");
        }
        return result;
    }

    private static void FetchAntigravityInstanceTrajectoriesUnary(
        string accessToken,
        AntigravityInstanceStreamState state)
    {
        try
        {
            string reqBody = JsonSerializer.Serialize(new
            {
                targetInstanceUuid = state.Instance.Uuid,
                rpcPath = GetTrajectoriesRpc,
                payload = GetTrajectoriesPayloadBase64,
                headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["Connect-Protocol-Version"] = "1",
                },
            });

            var reply = PostCloudCode(accessToken, ProxyCommandPath, reqBody);
            if (reply is not { Status: 200 }) return;

            using var outerDoc = JsonDocument.Parse(reply.Value.Body);
            var payloadB64 = Text(outerDoc.RootElement, "payload");
            if (string.IsNullOrWhiteSpace(payloadB64)) return;

            byte[] decoded = Convert.FromBase64String(payloadB64);
            using var innerDoc = JsonDocument.Parse(decoded);
            if (!innerDoc.RootElement.TryGetProperty("trajectorySummaries", out var summaries)
                || summaries.ValueKind != JsonValueKind.Object)
                return;

            var parsedMap = new Dictionary<string, AntigravityTrajectorySummary>(StringComparer.Ordinal);
            foreach (var prop in summaries.EnumerateObject())
            {
                var summary = ParseAntigravityTrajectorySummary(prop.Name, prop.Value, state.Instance.Server);
                if (summary is not null && !summary.Killed && !summary.Archived)
                    parsedMap[prop.Name] = summary;
            }

            lock (agStreamLock)
            {
                // Only overwrite with the unary snapshot if the real-time stream hasn't already
                // taken over while the unary HTTP call was in flight.
                if (!state.StreamConnected || !state.HasSnapshot)
                {
                    state.Trajectories.Clear();
                    foreach (var (id, s) in parsedMap)
                        state.Trajectories[id] = s;
                }
                state.HasSnapshot = true;
                state.LastUnaryFetchUtc = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex)
        {
            Log.WriteOnce($"ag-proxy-{state.Instance.Server}",
                $"Antigravity proxyCommand failed for {state.Instance.Server}: {ex.Message}");
        }
    }

    private static AntigravityTrajectorySummary? ParseAntigravityTrajectorySummary(
        string id,
        JsonElement t,
        string server)
    {
        if (t.ValueKind != JsonValueKind.Object) return null;

        bool killed = Bool(t, "killed");
        bool archived = false;
        string? annTitle = null;
        DateTimeOffset? lastViewed = null;

        if (t.TryGetProperty("annotations", out var ann) && ann.ValueKind == JsonValueKind.Object)
        {
            archived = Bool(ann, "archived");
            annTitle = Text(ann, "title");
            lastViewed = ParseIso(Text(ann, "lastUserViewTime"));
        }

        string title = CleanTitle(annTitle ?? Text(t, "summary")) ?? "Untitled chat";
        var status = Text(t, "status") ?? "";
        bool notFullyIdle = Bool(t, "notFullyIdle");
        bool hasActiveChildren = Bool(t, "hasActiveChildren");
        bool interrupted = Bool(t, "interrupted");

        int waitingSteps = 0;
        if (t.TryGetProperty("waitingSteps", out var ws) && ws.ValueKind == JsonValueKind.Array)
            waitingSteps = ws.GetArrayLength();

        var lastModified = ParseIso(Text(t, "lastModifiedTime"))
                        ?? ParseIso(Text(t, "lastUserInputTime"))
                        ?? DateTimeOffset.MinValue;

        bool unread = lastViewed is null || lastModified > lastViewed.Value.AddSeconds(2);
        string? repo = ExtractAntigravityRepo(t);

        return new AntigravityTrajectorySummary(
            id,
            title,
            server,
            status,
            notFullyIdle,
            hasActiveChildren,
            interrupted,
            waitingSteps,
            killed,
            archived,
            unread,
            repo,
            lastModified);
    }

    private static RemoteChatItem? ToAntigravityChatItem(
        AntigravityTrajectorySummary t,
        DateTimeOffset doneCutoff)
    {
        if (t.Killed || t.Archived) return null;

        bool isWaiting = !t.Interrupted && t.LastModified >= doneCutoff && (
            t.WaitingSteps > 0 ||
            t.Status.Contains("WAITING", StringComparison.OrdinalIgnoreCase));

        bool isRunning = !isWaiting && (
            string.Equals(t.Status, "CASCADE_RUN_STATUS_RUNNING", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Status, "CASCADE_RUN_STATUS_BUSY", StringComparison.OrdinalIgnoreCase) ||
            t.NotFullyIdle ||
            t.HasActiveChildren);

        bool isDone = !isWaiting && !isRunning && t.LastModified >= doneCutoff;
        if (!isWaiting && !isRunning && !isDone) return null;

        var chatState = isWaiting ? RemoteChatState.Waiting
                      : isRunning ? RemoteChatState.Running
                      : RemoteChatState.Done;

        string? detail = isWaiting ? "Waiting for approval / input"
                       : isRunning ? (t.HasActiveChildren ? "Running with subagents" : "Running")
                       : t.Interrupted ? "Interrupted"
                       : t.Unread ? "Finished (unread)"
                       : "Ready";

        return new RemoteChatItem(
            t.Id,
            t.Title,
            t.Server,
            RemoteChatService.Antigravity,
            chatState,
            t.Repo,
            detail,
            t.Unread,
            t.LastModified,
            AntigravityWebUrl);
    }

    private static string? ExtractAntigravityRepo(JsonElement traj)
    {
        if (!traj.TryGetProperty("workspaces", out var ws) || ws.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var w in ws.EnumerateArray())
        {
            if (w.ValueKind != JsonValueKind.Object) continue;
            if (w.TryGetProperty("repository", out var repoObj) && repoObj.ValueKind == JsonValueKind.Object
                && Text(repoObj, "computedName") is { Length: > 0 } comp)
            {
                return ShortRepoName(comp);
            }
            if (Text(w, "workspaceFolderAbsoluteUri") is { Length: > 0 } uri)
            {
                return ShortRepoName(uri);
            }
        }
        return null;
    }

    private static (int Status, string Body)? GetCloudCode(string accessToken, string path)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, CloudCodeBaseUrl + path);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            req.Headers.TryAddWithoutValidation("User-Agent", "antigravity");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var resp = http.Send(req, HttpCompletionOption.ResponseContentRead);
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return ((int)resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            Log.WriteOnce("ag-remote-get", $"Cloud Code GET {path} failed: {ex.Message}");
            return null;
        }
    }

    private static (int Status, string Body)? PostCloudCode(string accessToken, string path, string jsonBody)
    {
        try
        {
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, CloudCodeBaseUrl + path) { Content = content };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            req.Headers.TryAddWithoutValidation("User-Agent", "antigravity");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var resp = http.Send(req, HttpCompletionOption.ResponseContentRead);
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return ((int)resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            Log.WriteOnce("ag-remote-post", $"Cloud Code POST {path} failed: {ex.Message}");
            return null;
        }
    }

    // ---- Diagnostic self-test ----------------------------------------------

    /// <summary>
    /// Walks both the Claude Code Remote Control and Antigravity Remote Control APIs and reports
    /// every discovered server and live chat state. Never includes any OAuth token or session key.
    /// </summary>
    public static string SelfTest()
    {
        var report = new StringBuilder();
        bool on;
        int doneHours;
        lock (gate)
        {
            on = settings?.Enabled ?? false;
            doneHours = Math.Clamp(settings?.DoneWindowHours ?? 12, 1, 168);
        }

        report.AppendLine(on ? $"remote chats tracking: on (done window: {doneHours}h)" : "remote chats tracking: switched off");
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(doneHours);

        // 1. Claude Code Remote Control
        report.AppendLine();
        report.AppendLine("--- Claude Code Remote Control ---");
        var claudeRes = PollClaude(cutoff, forceFullSync: true);
        if (!string.IsNullOrWhiteSpace(claudeRes.Diagnostic))
            report.AppendLine($"transport: {claudeRes.Diagnostic}");
        if (claudeRes.Error is not null && claudeRes.Buckets.Count == 0)
        {
            report.AppendLine($"status: {claudeRes.Error}");
        }
        else
        {
            report.AppendLine($"servers discovered: {claudeRes.Buckets.Count}");
            foreach (var b in claudeRes.Buckets.Values.OrderBy(x => x.Server))
            {
                int w = b.Chats.Count(c => c.State == RemoteChatState.Waiting);
                int r = b.Chats.Count(c => c.State == RemoteChatState.Running);
                int d = b.Chats.Count(c => c.State == RemoteChatState.Done);
                report.AppendLine($"  • {b.Server} ({(b.Online ? "online" : "offline")}): {b.Chats.Count} live — {w} waiting, {r} running, {d} done");
                foreach (var c in b.Chats.Take(5))
                    report.AppendLine($"      [{c.State}] {(c.Repo is not null ? $"({c.Repo}) " : "")}{c.Title}");
            }
        }

        // 2. Antigravity Remote Control
        report.AppendLine();
        report.AppendLine("--- Antigravity Remote Control ---");
        var agRes = PollAntigravity(cutoff, forceFullSync: true);
        if (agRes.Error is not null && agRes.Buckets.Count == 0)
        {
            report.AppendLine($"status: {agRes.Error}");
        }
        else
        {
            report.AppendLine($"connected instances: {agRes.Buckets.Count}");
            foreach (var b in agRes.Buckets.Values.OrderBy(x => x.Server))
            {
                bool streamLive;
                lock (agStreamLock)
                {
                    streamLive = agStreamsByUuid.Values.Any(s =>
                        string.Equals(s.Instance.Server, b.Server, StringComparison.OrdinalIgnoreCase) &&
                        s.StreamConnected);
                }
                string transportMode = streamLive ? "online, SSE live stream" : (b.Online ? "online, polling" : "offline");
                int w = b.Chats.Count(c => c.State == RemoteChatState.Waiting);
                int r = b.Chats.Count(c => c.State == RemoteChatState.Running);
                int d = b.Chats.Count(c => c.State == RemoteChatState.Done);
                report.AppendLine($"  • {b.Server} ({transportMode}): {b.Chats.Count} live — {w} waiting, {r} running, {d} done");
                foreach (var c in b.Chats.Take(5))
                    report.AppendLine($"      [{c.State}] {(c.Repo is not null ? $"({c.Repo}) " : "")}{c.Title}");
            }
        }

        return report.ToString().TrimEnd();
    }

    // ---- Formatting & utility helpers --------------------------------------

    public static string DescribeRow(RemoteServerRow row)
    {
        if (!row.Online && row.TotalCount == 0)
            return "Offline";
        if (row.TotalCount == 0)
            return "Idle (0 active)";

        var parts = new List<string>();
        if (row.WaitingCount > 0) parts.Add($"{row.WaitingCount} need input");
        if (row.RunningCount > 0) parts.Add($"{row.RunningCount} running");
        if (row.DoneCount > 0) parts.Add($"{row.DoneCount} done");
        return $"{row.TotalCount} chat{(row.TotalCount == 1 ? "" : "s")} ({string.Join(", ", parts)})";
    }

    public static string NormalizeServerName(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.EndsWith("-headless", StringComparison.OrdinalIgnoreCase))
            s = s[..^9];
        return s.Length > 0 ? s : "remote";
    }

    public static string RowKey(string server, RemoteChatService service) =>
        $"{NormalizeServerName(server)}:{(service == RemoteChatService.Antigravity ? "Antigravity" : "Claude")}";

    private static string? ShortRepoName(string? pathOrUri)
    {
        if (string.IsNullOrWhiteSpace(pathOrUri)) return null;
        var s = pathOrUri.Trim().TrimEnd('/', '\\');
        int slash = Math.Max(s.LastIndexOf('/'), s.LastIndexOf('\\'));
        var leaf = slash >= 0 && slash + 1 < s.Length ? s[(slash + 1)..] : s;
        return leaf.Length > 0 ? leaf : null;
    }

    private static string? CleanTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var singleLine = string.Join(" ", raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return singleLine.Length <= 80 ? singleLine : singleLine[..77].TrimEnd() + "…";
    }

    private static string? Text(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? ParseIso(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            return dt;

        // Trim nanosecond fractional precision (>7 digits) if a platform parser rejects it.
        int dot = iso.IndexOf('.');
        if (dot > 0)
        {
            int tz = iso.IndexOfAny(['Z', 'z', '+', '-'], dot + 1);
            if (tz > dot + 8)
            {
                var trimmed = iso[..(dot + 8)] + iso[tz..];
                if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dt))
                    return dt;
            }
        }
        return null;
    }
}
