using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScweenSpit;

/// <summary>One limit window claude.ai or Antigravity reports, as a percentage and the moment it resets.</summary>
public sealed record UsageLimit(string Label, int Percent, DateTimeOffset? ResetsAt);

/// <summary>
/// The result of the last poll. An empty <see cref="Limits"/> with no <see cref="Error"/> means we
/// have not managed a first read yet, which the strip draws as a placeholder rather than as zero.
/// </summary>
public sealed record UsageReading(IReadOnlyList<UsageLimit> Limits, string? Error, bool NeedsKey,
                                  bool Transient = false, bool Standby = false,
                                  IReadOnlyList<UsageLimit>? Details = null)
{
    /// <summary>
    /// Something being waited on rather than something wrong. A red "error" on the bar for a
    /// connection that has not finished coming back is a lie about a machine that is fine.
    /// </summary>
    public bool Settled => !Transient;
}

/// <summary>
/// Reads claude.ai usage limits for the strip the bar draws.
///
/// Derived from claude-usage-widget by Niccolò Sabato (MIT licence — the full notice is in
/// THIRD-PARTY-NOTICES.md). What is taken from it is the protocol knowledge, which is the part that
/// cannot be worked out from the outside: which endpoints carry the figures, the browser-shaped
/// headers the edge expects, how an organisation is chosen when an account has several, that the
/// weekly per-model limit moved out of `seven_day_sonnet` into the `limits` list, that the session
/// cookie rotates mid-session and has to be written back, and that Claude Code's own OAuth token in
/// ~/.claude/.credentials.json can answer the same question through api.anthropic.com without
/// relying on a browser cookie that expires every few weeks.
///
/// The code is ours: the original is Python/tkinter and draws its own window, whereas this feeds a
/// segment of a bar that is already painting. Two deliberate departures from upstream: the session
/// key is kept under DPAPI rather than in plain config (see <see cref="Secret"/>), and credentials
/// are passed to the curl fallback on stdin rather than in argv, where any process on the machine
/// can read them.
/// </summary>
public static class ClaudeUsage
{
    // A session key is a whole-account credential, so both checks below are deliberately strict.
    // Refusing a real key costs a missed rotation and the current one keeps working; accepting a
    // cleared cookie ("sessionKey=" with Max-Age=0, which is how the server ENDS a session)
    // overwrites the only good copy and locks the user out.
    private const string KeyPrefix = "sk-ant-";
    private const int KeyMinLength = 40;

    /// <summary>
    /// claude.ai sits behind an edge that fingerprints the TLS handshake, so the request has to look
    /// like a browser all the way down, not just in its headers.
    /// </summary>
    private const string BrowserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36";

    private const string Origin = "https://claude.ai";
    private const string OAuthUsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthBetaHeader = "oauth-2025-04-20";
    public const string UsagePage = "https://claude.ai/settings/usage";

    /// <summary>Where Claude Code (CLI and VS Code extension) stores its OAuth login on disk.</summary>
    public static string CredentialsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    // UseCookies is off so the Cookie header is ours to set and Set-Cookie is ours to read: the
    // handler's own jar would swallow the rotated key before we ever saw it.
    private static readonly HttpClient http = new(new HttpClientHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly object gate = new();
    private static ClaudeSettings? settings;
    private static Action? persist;
    private static CancellationTokenSource? life;
    private static volatile UsageReading? current;

    /// <summary>Wakes the poll loop early, for "check now" and for a key that just changed.</summary>
    private static volatile TaskCompletionSource? nudge;

    /// <summary>Survives a Refresh() that lands while a poll is already running.</summary>
    private static volatile bool wakeWanted;

    /// <summary>
    /// Mirrors <see cref="Enabled"/> outside the lock. Every bar asks this while laying out, once a
    /// second, and the answer must not be able to wait on the poll thread.
    /// </summary>
    private static volatile bool enabled;

    /// <summary>Mirrors whether a session key or Claude Code login is present, for the same reason.</summary>
    private static volatile bool hasKey;

    /// <summary>
    /// When the OAuth usage endpoint returns 429, pause OAuth requests for 15 minutes rather than
    /// treating a rate limit as an expired login. Matches upstream v2.9.1.
    /// </summary>
    private static DateTimeOffset oauthRateLimitedUntil = DateTimeOffset.MinValue;

    /// <summary>The latest reading, or null when usage tracking is off or has never run.</summary>
    public static UsageReading? Current => current;

    /// <summary>True when the feature is switched on and has a credential worth sending.</summary>
    public static bool Enabled => enabled;

    /// <summary>
    /// True when a session key or Claude Code login is present. Distinguishes "nothing has been
    /// entered" from "what was entered is no longer accepted" — the same symptom otherwise, and
    /// very different answers.
    /// </summary>
    public static bool HasKey => hasKey;

    /// <summary>True if a value looks like a session key rather than a cleared or bogus cookie.</summary>
    public static bool Plausible(string? key) =>
        key is not null && key.StartsWith(KeyPrefix, StringComparison.Ordinal) && key.Length >= KeyMinLength;

    /// <summary>True when ~/.claude/.credentials.json exists and holds an OAuth access token.</summary>
    public static bool HasClaudeCodeCredentials() => ReadClaudeCodeToken(out _) is not null;

    /// <summary>
    /// Starts, stops or re-points the poller to match the configuration. Safe to call on every
    /// settings change; the loop is only torn down when it actually has to be.
    /// </summary>
    public static void Configure(ClaudeSettings config, Action save)
    {
        lock (gate)
        {
            settings = config;
            persist = save;
            hasKey = !string.IsNullOrWhiteSpace(config.SessionKey)
                  || (config.UseClaudeCode && HasClaudeCodeCredentials());
            enabled = config.Enabled && hasKey;

            if (!config.Enabled)
            {
                life?.Cancel();
                life = null;
                current = null;
                return;
            }

            // Deliberately no poll here: this is called for every settings change in the app, and
            // refreshing on each one would spend a request on somebody toggling an unrelated switch.
            // The explicit paths — a new key, "Check now", a changed bar selection — ask for one.
            if (life is not null) return;

            life = new CancellationTokenSource();
            var token = life.Token;
            _ = Task.Run(() => Loop(token), token);

            Listen();
        }
    }

    /// <summary>
    /// Walks the same path a poll takes and reports where it stops, so a failure can be read rather
    /// than guessed at. Never includes any key or token — this text is meant to be pasted into a
    /// bug report.
    /// </summary>
    public static string SelfTest()
    {
        var report = new StringBuilder();
        string? stored, key, org;
        bool useClaudeCode;

        lock (gate)
        {
            stored = settings?.SessionKey;
            key = Secret.Unprotect(stored);
            org = settings?.OrgId;
            useClaudeCode = settings?.UseClaudeCode ?? true;
        }

        report.AppendLine(
            settings is null ? "not configured"
            : !settings.Enabled ? "tracking is switched off"
            : "tracking is on");

        var oauthToken = ReadClaudeCodeToken(out var plan);
        report.AppendLine(!useClaudeCode
            ? "claude code login: switched off in settings"
            : oauthToken is null
                ? $"claude code login: not found at {CredentialsPath}"
                : $"claude code login: token present ({oauthToken.Length} chars{(plan is not null ? $", {plan}" : "")})");

        if (useClaudeCode && oauthToken is not null)
        {
            var oauth = GetOAuthUsage(oauthToken);
            report.AppendLine(oauth is null
                ? "oauth usage: no reply — api.anthropic.com unreachable"
                : $"oauth usage: HTTP {oauth.Status}" +
                  (oauth.Status is 401 or 403 ? "  (token was refused)"
                 : oauth.Status == 429 ? "  (rate-limited)" : ""));

            if (oauth is { Status: < 400 })
            {
                var parsed = Parse(oauth.Body, true, true);
                report.AppendLine(parsed.Error is not null
                    ? $"oauth reading: {parsed.Error}"
                    : $"oauth reading: {parsed.Limits.Count} limit(s) — " +
                      string.Join(", ", parsed.Limits.Select(l => $"{l.Label} {l.Percent}%")));
            }
        }

        report.AppendLine(
            string.IsNullOrWhiteSpace(stored) ? "session key: none stored"
            : key is null ? "session key: stored, but this Windows account cannot decrypt it"
            : !Plausible(key) ? $"session key: decrypts to {key.Length} characters that do not look like a key"
            : $"session key: stored and readable ({key.Length} characters)");

        if (key is null || !Plausible(key)) return report.ToString().TrimEnd();

        var orgs = Get($"{Origin}/api/organizations", key);
        report.AppendLine(orgs is null
            ? "organisations: no reply — claude.ai unreachable"
            : $"organisations: HTTP {orgs.Status}{(IsAuthRejection(orgs) ? "  (the key was refused)" : orgs.Status == 403 ? "  (challenged by Cloudflare)" : "")}");

        if (orgs is null || orgs.Status >= 400) return report.ToString().TrimEnd();

        var workingKey = key;
        AdoptRotatedKey(orgs, ref workingKey);

        if (string.IsNullOrWhiteSpace(org)) org = ResolveOrg(ref workingKey);
        report.AppendLine(org is null ? "organisation: could not be determined" : $"organisation: {org}");
        if (org is null) return report.ToString().TrimEnd();

        var usage = Get($"{Origin}/api/organizations/{org}/usage", workingKey, org);
        report.AppendLine(usage is null
            ? "usage: no reply"
            : $"usage: HTTP {usage.Status}{(IsAuthRejection(usage) ? "  (the key was refused)" : usage.Status == 403 ? "  (challenged by Cloudflare)" : "")}");

        if (usage is { Status: < 400 })
        {
            AdoptRotatedKey(usage, ref workingKey);
            var parsed = Parse(usage.Body, true, true);
            report.AppendLine(parsed.Error is not null
                ? $"reading: {parsed.Error}"
                : $"reading: {parsed.Limits.Count} limit(s) — " +
                  string.Join(", ", parsed.Limits.Select(l => $"{l.Label} {l.Percent}%")));
        }

        return report.ToString().TrimEnd();
    }

    /// <summary>Asks the loop to poll now instead of waiting out the interval.</summary>
    public static void Refresh()
    {
        // The flag as well as the signal: a request arriving while Poll is already running would
        // otherwise be lost, and saving a key would appear to do nothing for three minutes.
        // A fresh judgement, not a continuation of a failing streak: an explicit request — a new key,
        // "Check now", waking up — should need its own two rejections before anything is called
        // expired, or one stale failure decides the answer to a question just asked.
        failures = 0;
        oauthRateLimitedUntil = DateTimeOffset.MinValue;

        wakeWanted = true;
        nudge?.TrySetResult();
    }

    /// <summary>
    /// Stores a new session key. Returns false if it does not look like one — better to reject it
    /// at the point the user can see why than to spend every refresh on a 401.
    /// </summary>
    public static bool SetKey(string? plain)
    {
        var key = plain?.Trim();

        lock (gate)
        {
            if (settings is null) return false;

            if (string.IsNullOrEmpty(key))
            {
                settings.SessionKey = null;
                settings.OrgId = null;
                hasKey = settings.UseClaudeCode && HasClaudeCodeCredentials();
                enabled = settings.Enabled && hasKey;
                if (!hasKey) current = null;
                persist?.Invoke();
                if (enabled) Refresh();
                return true;
            }

            if (!Plausible(key)) return false;

            var stored = Secret.Protect(key);
            if (stored is null) return false;      // unencryptable: keep nothing rather than plaintext

            settings.SessionKey = stored;
            settings.OrgId = null;                 // a different key may be a different account
            hasKey = true;
            enabled = settings.Enabled;
            current = null;
            persist?.Invoke();
        }

        Refresh();
        return true;
    }

    /// <summary>Stops the poller. Called when the app exits.</summary>
    public static void Stop()
    {
        lock (gate)
        {
            life?.Cancel();
            life = null;
            enabled = false;
        }

        if (!listening) return;
        listening = false;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerChanged;
    }

    private static bool listening;

    /// <summary>
    /// Asks for a poll when the machine comes back, rather than waiting out an interval that started
    /// before it went away. Coming out of sleep or on to a different network is exactly when the
    /// last reading is both stale and wrong, and it is the moment somebody looks at the bar.
    /// </summary>
    private static void Listen()
    {
        if (listening) return;
        listening = true;

        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerChanged;
    }

    private static void OnNetworkChanged(object? sender, EventArgs e)
    {
        Log.Write("usage: the network changed; asking for a fresh reading");
        Refresh();
    }

    private static void OnPowerChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        Log.Write("usage: resumed; asking for a fresh reading");
        Refresh();
    }

    // ---- polling -----------------------------------------------------------

    /// <summary>
    /// How many polls in a row have come back wrong. Resets on the first that does not.
    /// </summary>
    private static int failures;

    /// <summary>
    /// How long to wait after a failed poll. Not the configured interval: coming back from sleep or
    /// on to a different network fails once and then works, and waiting three minutes to discover
    /// that leaves a wrong answer on screen for three minutes. Backs off to the interval so a
    /// genuinely dead connection is not asked every ten seconds all afternoon.
    /// </summary>
    private static int RetrySeconds(int interval) =>
        Math.Min(interval, failures switch { <= 1 => 10, 2 => 30, 3 => 60, _ => interval });

    private static async Task Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            wakeWanted = false;

            try { Poll(); }
            catch (Exception ex)
            {
                // The loop must outlive any single failure: a bar that stops updating looks broken
                // in a way that a bar showing a stale figure does not.
                Log.Write($"claude usage poll failed: {ex.Message}");
                Failed(new UsageReading([], "Could not read usage", false));
            }

            int seconds;
            lock (gate) seconds = Math.Clamp(settings?.RefreshSeconds ?? 180, 30, 3600);

            if (failures > 0) seconds = RetrySeconds(seconds);

            var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            nudge = wake;
            if (wakeWanted) continue;              // asked for while the poll above was running

            try { await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(seconds), token), wake.Task); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static void Poll()
    {
        string? stored;
        string? key;
        string? org;
        bool weekly, model, useClaudeCode;

        lock (gate)
        {
            if (settings is not { Enabled: true }) return;
            stored = settings.SessionKey;
            key = Secret.Unprotect(stored);
            org = settings.OrgId;
            weekly = settings.ShowWeekly;
            model = settings.ShowModel;
            useClaudeCode = settings.UseClaudeCode;
        }

        bool haveSessionKey = Plausible(key);
        var oauthToken = useClaudeCode ? ReadClaudeCodeToken(out _) : null;
        bool haveOAuth = oauthToken is not null;

        hasKey = !string.IsNullOrWhiteSpace(stored) || haveOAuth;
        enabled = hasKey;

        // 1. Try Claude Code's self-renewing OAuth token first when available and not rate-limited.
        //    Unlike a browser cookie, Claude Code refreshes this token itself, so it stays alive
        //    without manual intervention. If it fails or is rate-limited and a session key is also
        //    stored, we fall through to the session key seamlessly.
        if (haveOAuth && DateTimeOffset.UtcNow >= oauthRateLimitedUntil)
        {
            var oauthReply = GetOAuthUsage(oauthToken!);
            if (oauthReply is { Status: < 400 })
            {
                var parsed = Parse(oauthReply.Body, weekly, model);
                if (parsed.Error is null)
                {
                    failures = 0;
                    current = parsed;
                    return;
                }
            }
            else if (oauthReply is { Status: 429 })
            {
                // A 429 on the OAuth usage endpoint is a rate limit, not an expired login. Pause
                // OAuth polling for 15 minutes and either fall back to the session key or keep the
                // last reading on screen.
                oauthRateLimitedUntil = DateTimeOffset.UtcNow.AddMinutes(15);
                Log.Write("usage: Claude Code OAuth endpoint returned 429; pausing OAuth checks for 15m");

                if (!haveSessionKey)
                {
                    failures = 0;
                    if (current is not { Limits.Count: > 0 })
                        current = new UsageReading([], "Rate-limited — waiting to retry", false, Transient: true);
                    return;
                }
            }
            else if (!haveSessionKey)
            {
                if (oauthReply is null)
                {
                    Failed(new UsageReading([], "api.anthropic.com unreachable", false));
                    return;
                }
                if (oauthReply.Status is 401 or 403)
                {
                    Log.WriteOnce($"usage-oauth-{oauthReply.Status}",
                        $"usage: Claude Code OAuth token was refused ({oauthReply.Status})");
                    Rejected("Claude Code login expired");
                    return;
                }

                Failed(new UsageReading([], $"api.anthropic.com returned {oauthReply.Status}", false));
                return;
            }
        }
        else if (haveOAuth && !haveSessionKey)
        {
            // Rate-limited on OAuth and no session key to fall back to: hold the last reading steady.
            if (current is not { Limits.Count: > 0 })
                current = new UsageReading([], "Rate-limited — waiting to retry", false, Transient: true);
            return;
        }

        // 2. Browser session key path (either primary or fallback from OAuth).
        if (!haveSessionKey)
        {
            // Nothing worth sending. Say so rather than spending a request every interval on a 401
            // whose answer is already known.
            bool unreadableKey = !string.IsNullOrWhiteSpace(stored);
            Log.WriteOnce("usage-nokey", unreadableKey
                ? "usage: a key is stored but could not be decrypted or no longer looks like a key"
                : "usage: no Claude Code login or session key found");

            current = new UsageReading([], unreadableKey ? "Stored key is unreadable" : "Session key needed", true);
            return;
        }

        var activeKey = key!;

        if (string.IsNullOrWhiteSpace(org))
        {
            org = ResolveOrg(ref activeKey);
            if (org is null) return;               // ResolveOrg has already published the reason

            lock (gate)
            {
                if (settings is not null) { settings.OrgId = org; persist?.Invoke(); }
            }
        }

        var reply = Get($"{Origin}/api/organizations/{org}/usage", activeKey, org);
        if (reply is null) { Failed(new UsageReading([], "claude.ai unreachable", false)); return; }

        // Only from a reply that worked: an authentication failure can carry a session cookie of
        // its own, and adopting that would overwrite the user's good key with a rejected one -
        // making a freshly pasted key fail permanently from the very first poll.
        if (reply.Status < 400) AdoptRotatedKey(reply, ref activeKey);

        if (IsAuthRejection(reply))
        {
            Log.WriteOnce($"usage-{reply.Status}", $"usage: claude.ai rejected the key ({reply.Status})");
            Rejected("Session key expired");
            return;
        }

        if (reply.Status >= 400)
        {
            // A 404 here usually means the stored org is stale — drop it so the next poll re-resolves.
            if (reply.Status == 404) lock (gate) { if (settings is not null) settings.OrgId = null; }

            // A 403 whose body is not an Anthropic JSON error is a Cloudflare edge challenge rather
            // than an expired key — treat it as a transient hiccup so we never tell the user their
            // key expired when it did not.
            var reason = reply.Status == 403
                ? "claude.ai challenged the request"
                : $"claude.ai returned {reply.Status}";
            Failed(new UsageReading([], reason, false, Transient: reply.Status is 403 or 429 or >= 500));
            return;
        }

        failures = 0;
        current = Parse(reply.Body, weekly, model);
    }

    /// <summary>
    /// Records a failed poll and shortens the retry. If we already have a reading on screen, keep
    /// its bars visible across the first two transient failures so a brief network blip on wake does
    /// not flash "error" over a working strip.
    /// </summary>
    private static void Failed(UsageReading reading)
    {
        failures++;
        if (failures <= 2 && current is { Limits.Count: > 0 }) return;
        current = reading;
    }

    /// <summary>
    /// The server refused the credential. Not conclusive on its own: the same 401 can come back
    /// from a network that has not finished connecting, and announcing an expired key over that
    /// sends people to re-paste one that was never wrong. Said out loud only once it has happened
    /// twice, and until then the retry is seconds away rather than an interval.
    /// </summary>
    private static void Rejected(string message)
    {
        failures++;

        if (failures == 1 && current is { Limits.Count: > 0 }) return;

        current = failures >= 2
            ? new UsageReading([], message, true)
            : new UsageReading([], "Credential refused once; checking again shortly", false, Transient: true);
    }

    /// <summary>
    /// Distinguishes a genuine authentication refusal from a Cloudflare interstitial challenge.
    /// A 401 is always an auth refusal; a 403 is only one when the body is Anthropic's JSON error
    /// object rather than an HTML challenge page.
    /// </summary>
    private static bool IsAuthRejection(Response reply)
    {
        if (reply.Status == 401) return true;
        if (reply.Status != 403) return false;

        var trimmed = reply.Body.TrimStart();
        return trimmed.StartsWith('{') && trimmed.Contains("\"error\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the OAuth access token from Claude Code's ~/.claude/.credentials.json, if present.
    /// Read fresh on each poll because Claude Code rewrites this file whenever it refreshes its
    /// token in the background.
    /// </summary>
    internal static string? ReadClaudeCodeToken(out string? plan)
    {
        plan = null;
        try
        {
            if (!File.Exists(CredentialsPath)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || oauth.ValueKind != JsonValueKind.Object)
                return null;

            var token = Text(oauth, "accessToken")?.Trim();
            if (string.IsNullOrEmpty(token)) return null;

            var sub = Text(oauth, "subscriptionType");
            var tier = Text(oauth, "rateLimitTier");
            plan = !string.IsNullOrWhiteSpace(sub) && !string.IsNullOrWhiteSpace(tier) ? $"{sub} / {tier}"
                 : !string.IsNullOrWhiteSpace(sub) ? sub
                 : tier;

            return token;
        }
        catch (Exception ex)
        {
            Log.WriteOnce("claude-code-creds", $"could not read Claude Code credentials: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Performs an authenticated GET against claude.ai using the stored browser session key (if any),
    /// adopting any rotated session cookie along the way.
    /// </summary>
    internal static (int Status, string Body)? GetWithStoredSessionKey(string pathAndQuery)
    {
        string? stored, key, org;
        lock (gate)
        {
            stored = settings?.SessionKey;
            key = Secret.Unprotect(stored);
            org = settings?.OrgId;
        }

        if (!Plausible(key)) return null;

        var activeKey = key!;
        if (string.IsNullOrWhiteSpace(org))
        {
            org = ResolveOrg(ref activeKey);
            if (!string.IsNullOrWhiteSpace(org))
            {
                lock (gate)
                {
                    if (settings is not null) { settings.OrgId = org; persist?.Invoke(); }
                }
            }
        }

        var reply = Get($"{Origin}{pathAndQuery}", activeKey, org);
        if (reply is null) return null;
        if (reply.Status < 400) AdoptRotatedKey(reply, ref activeKey);
        return (reply.Status, reply.Body);
    }

    /// <summary>
    /// Works out which organisation's usage to read.
    ///
    /// Only orgs exposing 'chat' have usage worth reading — a Console-only org would leave every bar
    /// at zero — and when several qualify, the one claude.ai itself last routed to is the one whose
    /// figures match what the website shows.
    /// </summary>
    private static string? ResolveOrg(ref string key)
    {
        var listing = Get($"{Origin}/api/organizations", key);
        if (listing is null) { Failed(new UsageReading([], "claude.ai unreachable", false)); return null; }

        if (IsAuthRejection(listing))
        {
            Rejected("Session key expired");
            return null;
        }

        if (listing.Status >= 400)
        {
            Failed(new UsageReading([], $"claude.ai returned {listing.Status}", false));
            return null;
        }

        AdoptRotatedKey(listing, ref key);

        try
        {
            using var doc = JsonDocument.Parse(listing.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                current = new UsageReading([], "No organisation found", false);
                return null;
            }

            // The browser's own last-active org wins outright when we can get it.
            var active = ActiveOrg(ref key);
            if (!string.IsNullOrWhiteSpace(active)) return active;

            string? first = null;
            foreach (var org in doc.RootElement.EnumerateArray())
            {
                var id = Text(org, "uuid") ?? Text(org, "id");
                if (id is null) continue;

                first ??= id;
                if (org.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array
                    && caps.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "chat"))
                    return id;
            }

            if (first is null) current = new UsageReading([], "No organisation found", false);
            return first;
        }
        catch (Exception ex)
        {
            Log.Write($"claude org lookup failed: {ex.Message}");
            current = new UsageReading([], "Unexpected reply from claude.ai", false);
            return null;
        }
    }

    /// <summary>
    /// The org claude.ai currently routes the account to, or null if it will not say.
    /// Also adopts any session key rotated on /api/bootstrap — that endpoint is where claude.ai
    /// most often rotates cookies, and dropping it leaves the stored key stale.
    /// </summary>
    private static string? ActiveOrg(ref string key)
    {
        try
        {
            var boot = Get($"{Origin}/api/bootstrap", key);
            if (boot is null || boot.Status >= 400) return null;

            AdoptRotatedKey(boot, ref key);

            using var doc = JsonDocument.Parse(boot.Body);
            return doc.RootElement.TryGetProperty("account", out var account)
                ? Text(account, "lastActiveOrgId")
                : null;
        }
        catch (Exception ex)
        {
            Log.WriteOnce("claude-bootstrap", $"claude bootstrap unavailable: {ex.Message}");
            return null;
        }
    }

    // ---- shaping -----------------------------------------------------------

    private static UsageReading Parse(string body, bool weekly, bool model)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var limits = new List<UsageLimit>();

            var sessionWindow = Window(root, "five_hour", "utilization", "session");
            var weeklyWindow = Window(root, "seven_day", "utilization", "weekly_all");

            if (sessionWindow is { } session)
                limits.Add(session with { Label = "Session" });

            if (weekly && weeklyWindow is { } week)
                limits.Add(week with { Label = "Weekly" });

            if (model && ScopedModel(root, weeklyWindow?.ResetsAt) is { } scoped)
                limits.Add(scoped);

            return new UsageReading(limits, null, false);
        }
        catch (Exception ex)
        {
            Log.Write($"claude usage reply unreadable: {ex.Message}");
            return new UsageReading([], "Unexpected reply from claude.ai", false);
        }
    }

    /// <summary>A named limit bucket, or null when this account does not have one.</summary>
    private static UsageLimit? Window(JsonElement root, string name, string field, string? limitsKind = null)
    {
        if (!root.TryGetProperty(name, out var bucket) || bucket.ValueKind != JsonValueKind.Object)
            return null;

        if (!bucket.TryGetProperty(field, out var used) || !Number(used, out double percent))
            return null;

        var resetsAt = Moment(Text(bucket, "resets_at") ?? Text(bucket, "reset_at"));
        if (resetsAt is null && limitsKind is not null)
            resetsAt = FindLimitReset(root, limitsKind);

        return new UsageLimit(name, Clamp(percent), resetsAt);
    }

    private static DateTimeOffset? FindLimitReset(JsonElement root, string kind)
    {
        if (!root.TryGetProperty("limits", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(Text(item, "kind"), kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (Moment(Text(item, "resets_at") ?? Text(item, "reset_at")) is { } when)
                return when;
        }

        return null;
    }

    /// <summary>
    /// The weekly per-model limit. claude.ai moved this out of `seven_day_sonnet` and into the
    /// `limits` list, whose entry names the model it is scoped to — so read that when it is there
    /// and the bar follows whichever model the limit applies to, rather than naming a fixed one.
    /// </summary>
    private static UsageLimit? ScopedModel(JsonElement root, DateTimeOffset? fallbackWeeklyReset)
    {
        if (root.TryGetProperty("limits", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in list.EnumerateArray())
            {
                if (!limit.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object) continue;
                if (!scope.TryGetProperty("model", out var m) || m.ValueKind != JsonValueKind.Object) continue;

                var name = Text(m, "display_name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!limit.TryGetProperty("percent", out var pct) || !Number(pct, out double percent)) continue;

                var resetsAt = Moment(Text(limit, "resets_at") ?? Text(limit, "reset_at")) ?? fallbackWeeklyReset;
                return new UsageLimit(name!, Clamp(percent), resetsAt);
            }
        }

        return Window(root, "seven_day_sonnet", "utilization") is { } legacy
            ? legacy with { Label = "Sonnet", ResetsAt = legacy.ResetsAt ?? fallbackWeeklyReset }
            : null;
    }

    private static int Clamp(double percent) => (int)Math.Round(Math.Clamp(percent, 0, 100));

    private static bool Number(JsonElement e, out double value)
    {
        value = 0;
        if (e.ValueKind == JsonValueKind.Number) { value = e.GetDouble(); return true; }
        return e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), out value);
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Moment(string? iso) =>
        DateTimeOffset.TryParse(iso, out var when) ? when : null;

    /// <summary>How long until a limit resets, in the shortest form that still reads clearly.</summary>
    public static string Countdown(DateTimeOffset? resets)
    {
        if (resets is not { } when) return "";

        var left = when - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero) return "resets any moment";

        return left.TotalDays >= 1  ? $"resets in {(int)left.TotalDays}d {left.Hours}h"
             : left.TotalHours >= 1 ? $"resets in {(int)left.TotalHours}h {left.Minutes:00}m"
             : $"resets in {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
    }

    /// <summary>Compact time-to-reset ("2h 21m", "5d 18h", "42m") for drawing directly on a bar row.</summary>
    public static string ShortCountdown(DateTimeOffset? resets)
    {
        if (resets is not { } when) return "—";

        var left = when - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero) return "now";

        return left.TotalDays >= 1  ? $"{(int)left.TotalDays}d {left.Hours}h"
             : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes:00}m"
             : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
    }

    // ---- transport ---------------------------------------------------------

    private sealed record Response(int Status, string Headers, string Body);

    /// <summary>
    /// Queries Anthropic's OAuth usage endpoint using Claude Code's Bearer token. This endpoint sits
    /// on api.anthropic.com rather than behind claude.ai's browser challenge, and returns the same
    /// five_hour / seven_day / limits JSON structure.
    /// </summary>
    private static Response? GetOAuthUsage(string token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, OAuthUsageUrl);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBetaHeader);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var reply = http.Send(request, HttpCompletionOption.ResponseContentRead);
            var body = reply.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return new Response((int)reply.StatusCode, "", body);
        }
        catch (Exception ex)
        {
            Log.WriteOnce("claude-oauth-http", $"OAuth usage request failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// An authenticated GET to claude.ai, or null if the request could not be made at all.
    ///
    /// .NET's TLS goes through SChannel on Windows, the same stack the browsers use, so the
    /// handshake normally passes the edge's fingerprint check. Normally — the cipher ordering is not
    /// identical, so curl.exe (which has shipped with Windows since 1803 and is what upstream uses
    /// throughout) goes first, with a retry disabling Schannel revocation checks if OCSP/CRL is
    /// unreachable, and .NET's HttpClient as a final fallback.
    /// </summary>
    private static Response? Get(string url, string key, string? org = null)
    {
        // claude.ai wants the active organisation alongside the session key on org-scoped calls.
        // Sending only the key gets a 401 that is indistinguishable from an expired one, which is
        // exactly how this presented: a freshly pasted key reported as no longer accepted.
        var cookie = string.IsNullOrWhiteSpace(org)
            ? $"sessionKey={key}"
            : $"sessionKey={key}; lastActiveOrg={org}";

        var viaCurl = ViaCurl(url, cookie, ignoreRevocation: false)
                   ?? ViaCurl(url, cookie, ignoreRevocation: true);
        if (viaCurl is not null) return viaCurl;

        Log.WriteOnce("claude-transport",
            "curl was unavailable or failed; falling back to the .NET client, which Cloudflare "
          + "may challenge regardless of the key");

        return ViaHttpClient(url, cookie);
    }

    private static Response? ViaHttpClient(string url, string cookie)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
            request.Headers.TryAddWithoutValidation("anthropic-client-platform", "web_claude_ai");
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            request.Headers.TryAddWithoutValidation("Referer", Origin + "/");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);

            using var reply = http.Send(request, HttpCompletionOption.ResponseContentRead);
            var body = reply.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            var setCookies = reply.Headers.TryGetValues("Set-Cookie", out var values)
                ? string.Join("\n", values.Select(v => "Set-Cookie: " + v))
                : "";

            return new Response((int)reply.StatusCode, setCookies, body);
        }
        catch (Exception ex)
        {
            Log.WriteOnce("claude-http", $"direct request to claude.ai failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The same GET through curl.exe.
    ///
    /// The options go in on stdin (-K -) rather than as arguments, because the cookie is a
    /// whole-account credential and a command line is readable by every process on the machine.
    /// </summary>
    private static Response? ViaCurl(string url, string cookie, bool ignoreRevocation)
    {
        try
        {
            var start = new ProcessStartInfo("curl.exe")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-sS");
            if (ignoreRevocation) start.ArgumentList.Add("--ssl-no-revoke");
            start.ArgumentList.Add("-K");
            start.ArgumentList.Add("-");

            using var curl = Process.Start(start);
            if (curl is null) return null;

            // -D - puts the response headers on stdout ahead of the body, which is the only way to
            // see the status: a 401 error payload is valid JSON and would otherwise parse as data.
            var config = new StringBuilder()
                .AppendLine("-D -")
                .AppendLine("--max-time 20")
                .AppendLine($"--url \"{Escape(url)}\"")
                .AppendLine($"-H \"User-Agent: {Escape(BrowserAgent)}\"")
                .AppendLine("-H \"anthropic-client-platform: web_claude_ai\"")
                .AppendLine($"-H \"Cookie: {Escape(cookie)}\"")
                .ToString();

            curl.StandardInput.Write(config);
            curl.StandardInput.Close();

            var output = curl.StandardOutput.ReadToEnd();
            var error = curl.StandardError.ReadToEnd();
            if (!curl.WaitForExit(30_000)) { try { curl.Kill(); } catch { } return null; }

            if (curl.ExitCode != 0)
            {
                if (ignoreRevocation)
                    Log.WriteOnce("claude-curl", $"curl to claude.ai failed ({curl.ExitCode}): {error.Trim()}");
                return null;
            }

            // Split on the LAST blank line: a redirect or a 100-continue puts more than one header
            // block in front of the body.
            var separator = output.Contains("\r\n\r\n") ? "\r\n\r\n" : "\n\n";
            int split = output.LastIndexOf(separator, StringComparison.Ordinal);
            var headers = split < 0 ? output : output[..split];
            var body = split < 0 ? output : output[(split + separator.Length)..];

            var codes = Regex.Matches(headers, @"HTTP/[\d.]+ (\d+)");
            int status = codes.Count > 0 ? int.Parse(codes[^1].Groups[1].Value) : 0;

            return new Response(status, headers, body.Trim());
        }
        catch (Exception ex)
        {
            Log.WriteOnce("claude-curl", $"could not run curl: {ex.Message}");
            return null;
        }
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Writes back a session key claude.ai rotated mid-session, and updates <paramref name="currentKey"/>
    /// in place so subsequent requests in the same poll use the new key rather than the old one.
    /// Skipping this is invisible for weeks and then shows up as a key that expired for no reason.
    ///
    /// Only a real Set-Cookie line counts, and only a value that still looks like a key: the same
    /// cookie is how the server ends a session, and taking `sessionKey=""` literally would destroy
    /// the working credential.
    /// </summary>
    private static void AdoptRotatedKey(Response reply, ref string currentKey)
    {
        if (string.IsNullOrEmpty(reply.Headers)) return;

        foreach (var line in reply.Headers.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("set-cookie:", StringComparison.OrdinalIgnoreCase)) continue;

            var match = Regex.Match(line, @"sessionKey=(""?)([^;\s""]*)\1");
            if (!match.Success) continue;

            var fresh = match.Groups[2].Value;
            if (fresh == currentKey) continue;

            if (!Plausible(fresh))
            {
                // Logged because this is the one check that can be wrong in a costly direction: if
                // the cookie ever changes shape, every rotation would be dropped silently.
                Log.WriteOnce("claude-rotation",
                    $"ignored a session cookie that is not a key ({fresh.Length} chars)");
                continue;
            }

            currentKey = fresh;

            var stored = Secret.Protect(fresh);
            if (stored is null) return;

            lock (gate)
            {
                if (settings is null) return;
                settings.SessionKey = stored;
                persist?.Invoke();
            }

            Log.Write("claude.ai rotated the session key; the new one has been stored");
            return;
        }
    }
}
