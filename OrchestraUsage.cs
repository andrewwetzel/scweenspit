using System.Net;
using System.Text.Json;

namespace ScweenSpit;

/// <summary>How many children of one kind (`claude`, `agy`, `agy-ide`, …) an orchestrator is running.</summary>
public sealed record OrchestraToolCount(string Tool, int Running, int Waiting, int Idle)
{
    public int Total => Running + Waiting + Idle;
}

/// <summary>One unfinished orchestrator run and its children.</summary>
public sealed record OrchestraRun(
    string Name,
    string? Repo,
    string? Base,
    DateTimeOffset? StartedAt,
    IReadOnlyList<OrchestraToolCount> Children);

/// <summary>
/// The last thing one feed said, and whether it is still saying it. A feed that stops answering
/// keeps its last good reading, so the strip can dim rather than vanish under the pointer.
/// </summary>
public sealed record OrchestraFeed(
    string Url,
    string Server,
    bool Answered,
    bool Active,
    DateTimeOffset? StartedAt,
    DateTimeOffset GeneratedAt,
    long ReceivedTick,
    IReadOnlyList<OrchestraToolCount> Children,
    IReadOnlyList<(string Tool, int Cap)> Caps,
    IReadOnlyList<OrchestraRun> Runs,
    string? Error,
    long LastAnswerTick)
{
    /// <summary>Not heard from for long enough that the figures may no longer be true.</summary>
    public bool Stale => Answered && Environment.TickCount64 - LastAnswerTick > OrchestraUsage.StaleAfterMs;

    public int Waiting => Children.Sum(c => c.Waiting);

    /// <summary>
    /// How long the orchestrator has been running, as of <paramref name="nowTick"/>. Measured on the
    /// server's clock up to the moment it answered, and on ours since: the two machines need not
    /// agree on the time for the figure to be right.
    /// </summary>
    public TimeSpan? Elapsed(long nowTick)
    {
        if (!Active || StartedAt is not { } started) return null;
        var elapsed = GeneratedAt - started + TimeSpan.FromMilliseconds(Math.Max(0, nowTick - ReceivedTick));
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    /// <summary>Elapsed time from a run's own start, on the same clock as <see cref="Elapsed"/>.</summary>
    public TimeSpan? ElapsedFor(OrchestraRun run, long nowTick)
    {
        if (run.StartedAt is not { } started) return null;
        var elapsed = GeneratedAt - started + TimeSpan.FromMilliseconds(Math.Max(0, nowTick - ReceivedTick));
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }
}

/// <summary>
/// What one piece of the strip stands for: the runs of one orchestrator in one repo, or the
/// orchestrator as a whole when its feed does not say which repos its runs are in.
/// </summary>
public sealed record OrchestraChunk(string Title, bool Active, TimeSpan? Elapsed,
                                    IReadOnlyList<OrchestraToolCount> Children, bool Stale);

/// <summary>Every configured feed, in the order Settings lists them.</summary>
public sealed record OrchestraReading(IReadOnlyList<OrchestraFeed> Feeds)
{
    /// <summary>The feeds worth drawing: ones that have answered at least once since startup.</summary>
    public IReadOnlyList<OrchestraFeed> Shown => Feeds.Where(f => f.Answered).ToList();
}

/// <summary>
/// Polls the read-only `orchestra.json` feeds an orchestrator publishes on the tailnet: when it
/// started, and how many child sessions of each kind it is running. Nothing to sign in to and
/// nothing written back — the feed is one GET that the server computes per request.
///
/// A feed that has never answered is asked rarely and drawn not at all, so a machine that cannot
/// reach the tailnet sees nothing and costs nothing.
/// </summary>
public static class OrchestraUsage
{
    public const int SchemaVersion = 1;
    public const int StaleAfterMs = 30_000;

    /// <summary>How often a feed that has never answered is tried again.</summary>
    private const int UnansweredRetryMs = 30_000;

    private static readonly HttpClient http = new(new HttpClientHandler
    {
        UseCookies = false,
        UseProxy = false,   // tailnet addresses are direct peers; a system proxy cannot reach them
        AutomaticDecompression = DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(4) };

    private static readonly object gate = new();
    private static OrchestraSettings? settings;
    private static CancellationTokenSource? life;
    private static volatile TaskCompletionSource? nudge;
    private static volatile bool wakeWanted;
    private static OrchestraReading? current;

    private static readonly Dictionary<string, OrchestraFeed> byUrl = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> nextAttemptTick = new(StringComparer.OrdinalIgnoreCase);
    private static List<string> configuredUrls = [];
    private static long glowStartTick;

    /// <summary>Raised whenever <see cref="Current"/> changes so bars can repaint immediately.</summary>
    public static event Action? Changed;

    /// <summary>The latest reading across every configured feed, or null while switched off.</summary>
    public static OrchestraReading? Current => current;

    public static bool Enabled { get; private set; }

    /// <summary>Glow intensity after any feed goes from no child waiting to at least one.</summary>
    public static float AttentionGlowIntensity => RemoteChatUsage.GlowIntensityFromTick(Interlocked.Read(ref glowStartTick));

    public static bool IsAttentionGlowing => AttentionGlowIntensity > 0f;

    /// <summary>Starts, stops, or updates the feed poller.</summary>
    public static void Configure(OrchestraSettings config)
    {
        lock (gate)
        {
            settings = config;
            Enabled = config.Enabled;

            if (!config.Enabled)
            {
                life?.Cancel();
                life = null;
                configuredUrls = [];
                byUrl.Clear();
                nextAttemptTick.Clear();
                current = null;
                Interlocked.Exchange(ref glowStartTick, 0);
                FireChanged();
                return;
            }

            // Forget feeds that are no longer listed, and ask new ones straight away. Every save in
            // Settings comes through here, so an unchanged list is left to its own schedule.
            var list = Urls(config);
            bool listChanged = !list.SequenceEqual(configuredUrls, StringComparer.OrdinalIgnoreCase);
            configuredUrls = list;
            var urls = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            foreach (var gone in byUrl.Keys.Where(u => !urls.Contains(u)).ToList()) byUrl.Remove(gone);
            foreach (var gone in nextAttemptTick.Keys.Where(u => !urls.Contains(u)).ToList()) nextAttemptTick.Remove(gone);
            RebuildLocked();

            if (life is not null && !listChanged) return;

            if (life is null)
            {
                life = new CancellationTokenSource();
                var token = life.Token;
                _ = Task.Run(() => LoopAsync(token), token);
                ListenEvents();
            }
        }
        Refresh();
    }

    /// <summary>Asks every feed now, including ones that have not answered yet.</summary>
    public static void Refresh()
    {
        lock (gate) nextAttemptTick.Clear();
        wakeWanted = true;
        nudge?.TrySetResult();
    }

    public static void Stop()
    {
        lock (gate)
        {
            life?.Cancel();
            life = null;
            Enabled = false;
        }

        if (!listening) return;
        listening = false;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerChanged;
    }

    private static bool listening;

    // The tailnet coming up is a network change, and that is exactly when a silent feed is worth asking.
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

    /// <summary>The configured feed URLs, trimmed and de-duplicated, in order.</summary>
    public static List<string> Urls(OrchestraSettings config) =>
        config.Feeds.Select(f => f?.Trim() ?? "").Where(f => f.Length > 0)
              .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True for an absolute http(s) URL — the only thing worth polling.</summary>
    public static bool IsFeedUrl(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    // ---- polling ------------------------------------------------------------

    private static async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            wakeWanted = false;

            List<string> due;
            int seconds;
            lock (gate)
            {
                if (settings is not { Enabled: true }) return;
                seconds = Math.Clamp(settings.RefreshSeconds, 2, 300);
                long now = Environment.TickCount64;
                due = Urls(settings).Where(u => !nextAttemptTick.TryGetValue(u, out var at) || now >= at).ToList();
            }

            try
            {
                await Task.WhenAll(due.Select(u => PollFeedAsync(u, seconds, ct)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Write($"orchestra poll failed: {ex.Message}");
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

    private static async Task PollFeedAsync(string url, int seconds, CancellationToken ct)
    {
        string? body = null, error = null;
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
                body = await response.Content.ReadAsStringAsync(ct);
            else
                error = $"HTTP {(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            error = "timed out";
        }
        catch (HttpRequestException ex)
        {
            error = ex.InnerException?.Message ?? ex.Message;
        }

        long tick = Environment.TickCount64;
        OrchestraFeed? parsed = null;
        if (body is not null)
        {
            parsed = Parse(url, body, tick, DateTimeOffset.UtcNow, out error);
            if (parsed is null) Log.WriteOnce($"orchestra-bad:{url}", $"orchestra feed {url}: {error}");
        }

        lock (gate)
        {
            if (settings is not { Enabled: true } || !Urls(settings).Contains(url, StringComparer.OrdinalIgnoreCase))
                return;

            byUrl.TryGetValue(url, out var previous);
            if (parsed is not null)
            {
                // Glow only on a change seen live, not on the first answer after startup.
                if (previous is { Answered: true, Waiting: 0 } && parsed.Waiting > 0)
                    Interlocked.Exchange(ref glowStartTick, tick);
                byUrl[url] = parsed;
            }
            else
            {
                byUrl[url] = previous is { Answered: true }
                    ? previous with { Error = error }
                    : Unanswered(url, error);
            }

            bool answered = byUrl[url].Answered;
            nextAttemptTick[url] = tick + (answered ? seconds * 1000L : UnansweredRetryMs);
            RebuildLocked();
        }
    }

    private static OrchestraFeed Unanswered(string url, string? error) => new(
        url, HostOf(url), Answered: false, Active: false, StartedAt: null, GeneratedAt: DateTimeOffset.MinValue,
        ReceivedTick: 0, Children: [], Caps: [], Runs: [], Error: error, LastAnswerTick: 0);

    private static void RebuildLocked()
    {
        var previous = current;
        var feeds = settings is null
            ? []
            : Urls(settings).Select(u => byUrl.TryGetValue(u, out var f) ? f : Unanswered(u, null)).ToList();
        current = new OrchestraReading(feeds);

        if (!Same(previous, current)) FireChanged();
    }

    /// <summary>
    /// Whether two readings would draw the same. The server's timestamp moves on every answer and
    /// the timer is drawn from the clock anyway, so neither is worth a repaint of its own.
    /// </summary>
    private static bool Same(OrchestraReading? a, OrchestraReading? b)
    {
        if (a is null || b is null) return ReferenceEquals(a, b);
        if (a.Feeds.Count != b.Feeds.Count) return false;
        for (int i = 0; i < a.Feeds.Count; i++)
        {
            var x = a.Feeds[i];
            var y = b.Feeds[i];
            if (x.Url != y.Url || x.Server != y.Server || x.Answered != y.Answered || x.Active != y.Active
                || x.StartedAt != y.StartedAt || x.Error != y.Error
                || !x.Children.SequenceEqual(y.Children) || x.Runs.Count != y.Runs.Count)
                return false;
            for (int r = 0; r < x.Runs.Count; r++)
                if (x.Runs[r].Name != y.Runs[r].Name || x.Runs[r].StartedAt != y.Runs[r].StartedAt
                    || !x.Runs[r].Children.SequenceEqual(y.Runs[r].Children))
                    return false;
        }
        return true;
    }

    private static void FireChanged()
    {
        var handler = Changed;
        if (handler is null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { handler(); } catch { }
        });
    }

    // ---- the feed itself ----------------------------------------------------

    /// <summary>
    /// Reads one answer from a feed, or returns null with <paramref name="error"/> saying why not.
    /// Tolerant by design: unknown fields are ignored, unknown child kinds are kept, and a missing
    /// count is a zero — the contract only ever grows within a schema.
    /// </summary>
    internal static OrchestraFeed? Parse(string url, string json, long receivedTick, DateTimeOffset receivedUtc,
                                         out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "not a JSON object"; return null; }

            if (root.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Number
                && schema.TryGetInt32(out int version) && version != SchemaVersion)
            {
                error = $"feed speaks schema {version}; this ScweenSpit understands {SchemaVersion}";
                return null;
            }

            string server = Text(root, "server") is { Length: > 0 } s ? s : HostOf(url);
            var generated = Time(root, "generated_at") ?? receivedUtc;
            var children = Counts(root, "children");
            var runs = new List<OrchestraRun>();
            if (root.TryGetProperty("runs", out var runsEl) && runsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in runsEl.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    runs.Add(new OrchestraRun(
                        Text(r, "run") ?? "run",
                        Text(r, "repo"),
                        Text(r, "base"),
                        Time(r, "started_at"),
                        Counts(r, "children")));
                }
            }

            DateTimeOffset? started = null;
            if (root.TryGetProperty("orchestrator", out var orch) && orch.ValueKind == JsonValueKind.Object)
                started = Time(orch, "started_at");
            started ??= runs.Where(r => r.StartedAt is not null).Select(r => r.StartedAt).Min();

            bool active = root.TryGetProperty("active", out var act) && act.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? act.GetBoolean()
                : started is not null;

            var caps = new List<(string, int)>();
            if (root.TryGetProperty("caps", out var capsEl) && capsEl.ValueKind == JsonValueKind.Object)
                foreach (var c in capsEl.EnumerateObject())
                    if (c.Value.ValueKind == JsonValueKind.Number && c.Value.TryGetInt32(out int cap))
                        caps.Add((c.Name, cap));

            return new OrchestraFeed(url, server, Answered: true, active, active ? started : null, generated,
                                     receivedTick, children, caps, runs, Error: null, LastAnswerTick: receivedTick);
        }
        catch (JsonException ex)
        {
            error = $"not valid JSON ({ex.Message})";
            return null;
        }
    }

    /// <summary>
    /// Child counts keyed by kind, the known kinds first in a fixed order so the strip does not
    /// reshuffle when one of them drops to zero and back.
    /// </summary>
    private static IReadOnlyList<OrchestraToolCount> Counts(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object) return [];

        var list = new List<OrchestraToolCount>();
        foreach (var p in el.EnumerateObject())
        {
            var v = p.Value;
            list.Add(v.ValueKind switch
            {
                JsonValueKind.Object => new OrchestraToolCount(p.Name, Int(v, "running"), Int(v, "waiting"), Int(v, "idle")),
                // A bare number is a count with no breakdown; call it running rather than drop it.
                JsonValueKind.Number when v.TryGetInt32(out int n) => new OrchestraToolCount(p.Name, Math.Max(0, n), 0, 0),
                _ => new OrchestraToolCount(p.Name, 0, 0, 0),
            });
        }

        return Ordered(list);
    }

    private static List<OrchestraToolCount> Ordered(IEnumerable<OrchestraToolCount> counts) =>
        counts.OrderBy(c => KindOrder(c.Tool)).ThenBy(c => c.Tool, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Several runs' counts added up per kind, in the same fixed order as a feed's own.</summary>
    public static IReadOnlyList<OrchestraToolCount> Sum(IEnumerable<OrchestraToolCount> counts) =>
        Ordered(counts.GroupBy(c => c.Tool, StringComparer.OrdinalIgnoreCase)
                      .Select(g => new OrchestraToolCount(g.First().Tool, g.Sum(c => c.Running),
                                                          g.Sum(c => c.Waiting), g.Sum(c => c.Idle))));

    private static int KindOrder(string tool) => tool.ToLowerInvariant() switch
    {
        "claude" => 0,
        "agy" => 1,
        "agy-ide" => 2,
        _ => 3,
    };

    /// <summary>What a person calls a child kind.</summary>
    public static string KindLabel(string tool) => tool.ToLowerInvariant() switch
    {
        "claude" => "Claude",
        "agy" => "agy",
        "agy-ide" => "agy IDE",
        _ => tool,
    };

    private static int Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)
            ? Math.Max(0, n) : 0;

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Time(JsonElement obj, string name) =>
        Text(obj, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                                                            System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t : null;

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    public static List<OrchestraChunk> Chunks(IReadOnlyList<OrchestraFeed> feeds, long now)
    {
        var chunks = new List<OrchestraChunk>();
        foreach (var f in feeds)
        {
            // With one orchestrator "Orch" says enough; with several, the server says which.
            string whole = feeds.Count > 1 ? RemoteServerRowConfig.ShortServerName(f.Server) : "Orch";

            var inRepos = f.Active ? f.Runs.Where(r => !string.IsNullOrWhiteSpace(r.Repo)).ToList() : [];
            if (inRepos.Count == 0)
            {
                chunks.Add(new OrchestraChunk(whole, f.Active, f.Elapsed(now), f.Children, f.Stale));
                continue;
            }

            // In the order the feed lists them, the timer from the longest-running run in each.
            foreach (var repo in inRepos.GroupBy(r => RepoName(r.Repo!), StringComparer.OrdinalIgnoreCase))
                chunks.Add(new OrchestraChunk(RepoShort(repo.Key), true,
                                     repo.Select(r => f.ElapsedFor(r, now)).Max() ?? f.Elapsed(now),
                                     Sum(repo.SelectMany(r => r.Children)), f.Stale));

            var elsewhere = f.Runs.Where(r => string.IsNullOrWhiteSpace(r.Repo)).ToList();
            if (elsewhere.Any(r => r.Children.Any(c => c.Total > 0)))
                chunks.Add(new OrchestraChunk(whole, true, elsewhere.Select(r => f.ElapsedFor(r, now)).Max() ?? f.Elapsed(now),
                                     Sum(elsewhere.SelectMany(r => r.Children)), f.Stale));
        }
        return chunks;
    }

    /// <summary>"owner/monopoly.git" → "monopoly".</summary>
    private static string RepoName(string repo)
    {
        var name = repo.Trim().TrimEnd('/', '\\');
        int slash = name.LastIndexOfAny(['/', '\\', ':']);
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length > 0 ? name : repo.Trim();
    }

    /// <summary>The first few letters of a repo's name: enough to tell "mono" from "scwe" at a glance.</summary>
    public static string RepoShort(string repo)
    {
        var name = RepoName(repo);
        return name.Length > 4 ? name[..4] : name;
    }

    /// <summary>"12:05" under an hour, "3:12:05" under a day, "2d 03:12" beyond.</summary>
    public static string FormatElapsed(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours:00}:{t.Minutes:00}";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>The same, in at most five characters, for a vertical bar.</summary>
    public static string FormatElapsedShort(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d{t.Hours:00}";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:00}";
        return $"{t.Minutes}m";
    }
}
