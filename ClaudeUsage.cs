using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const string AnthropicApiOrigin = "https://api.anthropic.com";
    private const string OAuthUsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthProfileUrl = "https://api.anthropic.com/api/oauth/profile";
    private const string OAuthBetaHeader = "oauth-2025-04-20";
    private const string OAuthAuthorizeUrl = "https://claude.ai/oauth/authorize";
    private const string OAuthTokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string OAuthSuccessUrl = "https://platform.claude.com/oauth/code/success?app=claude-code";
    private static readonly string OAuthClientId = string.Concat(
        "9d1c250a-", "e61b-44d9-", "88ed-", "5944d1962f5e");
    private const string OAuthRefreshScopes =
        "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";
    private const string OAuthLoginScopes =
        "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";
    private const string SessionsScope = "user:sessions:claude_code";
    private const string RemoteBetaHeader = "ccr-byoc-2025-07-29,environments-2025-11-01";
    private const string AnthropicVersionHeader = "2023-06-01";
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
    private static readonly object oauthRefreshLock = new();
    private static ClaudeSettings? settings;
    private static Action? persist;
    private static CancellationTokenSource? life;
    private static CancellationTokenSource? activeSignIn;
    private static volatile UsageReading? current;

    // In-memory cache of the most recently refreshed OAuth token so we never lose a refreshed
    // token if ~/.claude/.credentials.json is locked or not writable on Windows.
    private static string? cachedOAuthAccessToken;
    private static string? cachedOAuthRefreshToken;
    private static long cachedOAuthExpiresAt;
    private static List<string> cachedOAuthScopes = new();
    private static string? cachedOAuthPlan;
    private static string? failedRefreshToken;
    private static DateTimeOffset oauthRefreshBackoffUntil = DateTimeOffset.MinValue;

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

    /// <summary>True when ~/.claude/.credentials.json or a stored OAuth refresh token is available.</summary>
    public static bool HasClaudeCodeCredentials() =>
        ReadClaudeCodeToken(out _, forceRefresh: false, requireSessionsScope: false, allowNetworkRefresh: false) is not null
        || HasSignedInOAuth;

    /// <summary>True when an OAuth refresh token is stored directly in ScweenSpit's settings.</summary>
    public static bool HasSignedInOAuth
    {
        get
        {
            lock (gate) return !string.IsNullOrWhiteSpace(settings?.RefreshToken);
        }
    }

    /// <summary>Email address of the signed-in Claude OAuth account, if known.</summary>
    public static string? SignedInEmail
    {
        get
        {
            lock (gate) return settings?.AccountEmail;
        }
    }

    /// <summary>True when a plausible browser session key cookie is stored in settings.</summary>
    internal static bool HasStoredSessionKey
    {
        get
        {
            lock (gate) return Plausible(Secret.Unprotect(settings?.SessionKey));
        }
    }

    /// <summary>True when Claude Code OAuth login is enabled in settings.</summary>
    internal static bool UseClaudeCodeEnabled
    {
        get
        {
            lock (gate) return settings?.UseClaudeCode ?? true;
        }
    }

    /// <summary>Returns the currently configured or resolved organisation UUID, if any.</summary>
    internal static string? GetConfiguredOrgId()
    {
        lock (gate) return settings?.OrgId;
    }

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
                  || !string.IsNullOrWhiteSpace(config.RefreshToken)
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

        var oauthToken = ReadClaudeCodeToken(out var plan, forceRefresh: false, requireSessionsScope: false);
        report.AppendLine(!useClaudeCode
            ? "claude code login: switched off in settings"
            : oauthToken is null
                ? $"claude code login: not found or expired at {CredentialsPath}"
                : $"claude code login: token present ({oauthToken.Length} chars{(plan is not null ? $", {plan}" : "")})");

        if (useClaudeCode && oauthToken is not null)
        {
            var oauth = GetOAuthUsage(oauthToken);
            if (oauth is { Status: 401 or 403 })
            {
                var refreshed = ReadClaudeCodeToken(out plan, forceRefresh: true, requireSessionsScope: false);
                if (!string.IsNullOrEmpty(refreshed) && refreshed != oauthToken)
                {
                    oauthToken = refreshed;
                    oauth = GetOAuthUsage(oauthToken);
                }
            }

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
        oauthRefreshBackoffUntil = DateTimeOffset.MinValue;
        failedRefreshToken = null;

        wakeWanted = true;
        nudge?.TrySetResult();
    }

    /// <summary>
    /// Opens the system browser to Claude's OAuth 2.0 PKCE authorization page, receives the redirect
    /// on a temporary loopback port, exchanges the code for an OAuth access &amp; refresh token with
    /// full Claude Code session scopes (`user:sessions:claude_code`), stores the credentials in both
    /// `~/.claude/.credentials.json` and ScweenSpit's DPAPI-encrypted settings, and refreshes both
    /// the usage and remote chat trackers.
    /// </summary>
    public static async Task<(bool Ok, string Message)> SignInWithClaudeAsync()
    {
        CancellationTokenSource cts;
        lock (gate)
        {
            activeSignIn?.Cancel();
            cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            activeSignIn = cts;
        }

        using var v4Listener = new TcpListener(IPAddress.Loopback, 0);
        TcpListener? v6Listener = null;
        try
        {
            v4Listener.Start();
            int port = ((IPEndPoint)v4Listener.LocalEndpoint).Port;
            try
            {
                v6Listener = new TcpListener(IPAddress.IPv6Loopback, port);
                v6Listener.Start();
            }
            catch
            {
                v6Listener = null;
            }

            string redirectUri = $"http://localhost:{port}/callback";
            string codeVerifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            string codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
            string state = Base64Url(RandomNumberGenerator.GetBytes(32));

            string authUrl =
                $"{OAuthAuthorizeUrl}?code=true" +
                $"&client_id={Uri.EscapeDataString(OAuthClientId)}" +
                $"&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&scope={Uri.EscapeDataString(OAuthLoginScopes)}" +
                $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
                $"&code_challenge_method=S256" +
                $"&state={Uri.EscapeDataString(state)}";

            SystemStatus.Open(authUrl);

            var (code, callbackError) = await WaitForClaudeOAuthCallbackAsync(v4Listener, v6Listener, state, cts.Token);
            if (code is null)
                return (false, callbackError ?? "Claude sign-in did not complete.");

            string exchangeJson = JsonSerializer.Serialize(new
            {
                grant_type = "authorization_code",
                code,
                redirect_uri = redirectUri,
                client_id = OAuthClientId,
                code_verifier = codeVerifier,
                state,
            });

            using var content = new StringContent(exchangeJson, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, OAuthTokenUrl) { Content = content };
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var tokenReply = await http.SendAsync(req, cts.Token);
            var tokenBody = await tokenReply.Content.ReadAsStringAsync(cts.Token);
            if (!tokenReply.IsSuccessStatusCode)
                return (false, $"Token exchange failed (HTTP {(int)tokenReply.StatusCode}).");

            using var doc = JsonDocument.Parse(tokenBody);
            var root = doc.RootElement;
            var accessToken = Text(root, "access_token");
            var refreshToken = Text(root, "refresh_token");
            int expiresIn = root.TryGetProperty("expires_in", out var expEl) && Number(expEl, out double s)
                ? (int)s : 28800;
            var scopeStr = Text(root, "scope") ?? OAuthRefreshScopes;
            var scopes = scopeStr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            if (string.IsNullOrWhiteSpace(accessToken))
                return (false, "Claude did not return an access token — please try signing in again.");

            string? email = null;
            string? orgUuid = null;
            if (root.TryGetProperty("account", out var acct) && acct.ValueKind == JsonValueKind.Object)
                email = Text(acct, "email_address") ?? Text(acct, "email");
            if (root.TryGetProperty("organization", out var orgEl) && orgEl.ValueKind == JsonValueKind.Object)
                orgUuid = Text(orgEl, "uuid");

            string? subType = null;
            string? rateTier = null;
            FetchOAuthProfileMetadata(accessToken, ref orgUuid, ref email, out subType, out rateTier);

            long expiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (expiresIn * 1000L);
            SaveOAuthCredentialsToDiskAndCache(accessToken, refreshToken, expiresAtMs, scopes, subType, rateTier, orgUuid, email);

            Action? save;
            lock (gate)
            {
                if (settings is not null)
                {
                    if (!string.IsNullOrWhiteSpace(refreshToken))
                        settings.RefreshToken = Secret.Protect(refreshToken);
                    if (!string.IsNullOrWhiteSpace(email))
                        settings.AccountEmail = email;
                    if (!string.IsNullOrWhiteSpace(orgUuid))
                        settings.OrgId = orgUuid;
                    settings.UseClaudeCode = true;
                    hasKey = true;
                    enabled = settings.Enabled;
                }
                save = persist;
            }

            save?.Invoke();
            Refresh();
            RemoteChatUsage.Refresh();
            return (true, email is not null ? $"Signed in to Claude as {email}." : "Signed in to Claude.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Claude sign-in timed out or was cancelled.");
        }
        catch (Exception ex)
        {
            Log.Write($"claude oauth sign-in failed: {ex.Message}");
            return (false, $"Sign-in failed: {ex.Message}");
        }
        finally
        {
            try { v4Listener.Stop(); } catch { }
            try { v6Listener?.Stop(); } catch { }
        }
    }

    /// <summary>Clears any OAuth refresh token stored directly in ScweenSpit's settings.</summary>
    public static void SignOutOAuth()
    {
        Action? save;
        lock (gate)
        {
            activeSignIn?.Cancel();
            activeSignIn = null;
            if (settings is not null)
            {
                settings.RefreshToken = null;
                settings.AccountEmail = null;
            }
            cachedOAuthAccessToken = null;
            cachedOAuthRefreshToken = null;
            cachedOAuthExpiresAt = 0;
            cachedOAuthScopes.Clear();
            failedRefreshToken = null;
            save = persist;
        }
        save?.Invoke();
        Refresh();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<(string? Code, string? Error)> WaitForClaudeOAuthCallbackAsync(
        TcpListener v4, TcpListener? v6, string expectedState, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            if (v6 is not null)
            {
                var t4 = v4.AcceptTcpClientAsync(ct).AsTask();
                var t6 = v6.AcceptTcpClientAsync(ct).AsTask();
                var done = await Task.WhenAny(t4, t6);
                client = await done;
            }
            else
            {
                client = await v4.AcceptTcpClientAsync(ct);
            }

            using (client)
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;

                await using var stream = client.GetStream();
                byte[] buffer = new byte[8192];
                int read;
                try
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(TimeSpan.FromSeconds(5));
                    read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token);
                }
                catch
                {
                    continue;
                }

                if (read <= 0) continue;

                var requestText = Encoding.UTF8.GetString(buffer, 0, read);
                int lineEnd = requestText.IndexOf("\r\n", StringComparison.Ordinal);
                var firstLine = lineEnd >= 0 ? requestText[..lineEnd] : requestText;
                var parts = firstLine.Split(' ');
                if (parts.Length < 2) continue;

                var target = parts[1];
                if (!target.StartsWith("/callback", StringComparison.OrdinalIgnoreCase))
                {
                    var notFound = Encoding.UTF8.GetBytes(
                        "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(notFound, ct);
                    continue;
                }

                int q = target.IndexOf('?');
                var query = q >= 0 ? target[(q + 1)..] : "";
                var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0)
                    {
                        var k = Uri.UnescapeDataString(pair[..eq]);
                        var v = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                        parameters[k] = v;
                    }
                }

                if (parameters.TryGetValue("error", out var oauthErr))
                {
                    await SendRedirectOrHtmlAsync(stream, OAuthSuccessUrl, ct);
                    return (null, $"Claude returned: {oauthErr}");
                }

                if (!parameters.TryGetValue("state", out var state) ||
                    !string.Equals(state, expectedState, StringComparison.Ordinal))
                {
                    await SendRedirectOrHtmlAsync(stream, OAuthSuccessUrl, ct);
                    return (null, "OAuth state mismatch — please try signing in again.");
                }

                if (!parameters.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                {
                    await SendRedirectOrHtmlAsync(stream, OAuthSuccessUrl, ct);
                    return (null, "No authorization code received from Claude.");
                }

                await SendRedirectOrHtmlAsync(stream, OAuthSuccessUrl, ct);
                return (code, null);
            }
        }

        return (null, "Claude sign-in was cancelled.");
    }

    private static async Task SendRedirectOrHtmlAsync(NetworkStream stream, string location, CancellationToken ct)
    {
        var resp =
            $"HTTP/1.1 302 Found\r\n" +
            $"Location: {location}\r\n" +
            $"Content-Length: 0\r\n" +
            $"Connection: close\r\n\r\n";
        var bytes = Encoding.UTF8.GetBytes(resp);
        try { await stream.WriteAsync(bytes, ct); } catch { }
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
                hasKey = (settings.UseClaudeCode && HasClaudeCodeCredentials())
                      || !string.IsNullOrWhiteSpace(settings.RefreshToken);
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
        RemoteChatUsage.Refresh();
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
        //    If the access token has expired on disk, ReadClaudeCodeToken automatically refreshes it
        //    using the refresh token in ~/.claude/.credentials.json (or settings). If the endpoint
        //    still returns 401/403, we force one refresh before falling back to the session key.
        if (haveOAuth && DateTimeOffset.UtcNow >= oauthRateLimitedUntil)
        {
            var oauthReply = GetOAuthUsage(oauthToken!);
            if (oauthReply is { Status: 401 or 403 })
            {
                var refreshed = ReadClaudeCodeToken(out _, forceRefresh: true);
                if (!string.IsNullOrEmpty(refreshed) && refreshed != oauthToken)
                {
                    oauthToken = refreshed;
                    oauthReply = GetOAuthUsage(oauthToken);
                }
            }

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
    private static bool IsAuthRejection(Response reply) => IsAuthRejection(reply.Status, reply.Body);

    internal static bool IsAuthRejection(int status, string? body)
    {
        if (status == 401) return true;
        if (status != 403) return false;

        var trimmed = (body ?? "").TrimStart();
        return trimmed.StartsWith('{') && trimmed.Contains("\"error\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the OAuth access token from Claude Code's ~/.claude/.credentials.json (or ScweenSpit's
    /// stored OAuth refresh token), automatically renewing it via platform.claude.com/v1/oauth/token
    /// when expired or when <paramref name="requireSessionsScope"/> is requested and the current
    /// token lacks `user:sessions:claude_code`.
    /// </summary>
    internal static string? ReadClaudeCodeToken(
        out string? plan,
        bool forceRefresh = false,
        bool requireSessionsScope = false,
        bool allowNetworkRefresh = true)
    {
        plan = null;
        lock (oauthRefreshLock)
        {
            string? diskAccess = null;
            string? diskRefresh = null;
            long diskExpiresAt = 0;
            var diskScopes = new List<string>();
            string? sub = null;
            string? tier = null;

            try
            {
                if (File.Exists(CredentialsPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
                    if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                        && oauth.ValueKind == JsonValueKind.Object)
                    {
                        diskAccess = Text(oauth, "accessToken")?.Trim();
                        diskRefresh = Text(oauth, "refreshToken")?.Trim();
                        if (oauth.TryGetProperty("expiresAt", out var expEl) && Number(expEl, out double expMs))
                            diskExpiresAt = (long)expMs;

                        if (oauth.TryGetProperty("scopes", out var scEl) && scEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in scEl.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                                    diskScopes.Add(s);
                            }
                        }

                        sub = Text(oauth, "subscriptionType");
                        tier = Text(oauth, "rateLimitTier");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WriteOnce("claude-code-creds", $"could not read Claude Code credentials: {ex.Message}");
            }

            string? storedRefresh = null;
            lock (gate)
            {
                storedRefresh = Secret.Unprotect(settings?.RefreshToken)?.Trim();
            }

            // Pick whichever token state (in-memory refreshed cache vs disk file) is newer.
            string? accessToken = diskAccess;
            string? refreshToken = !string.IsNullOrWhiteSpace(diskRefresh) ? diskRefresh : storedRefresh;
            long expiresAt = diskExpiresAt;
            List<string> scopes = diskScopes;

            if (!string.IsNullOrWhiteSpace(cachedOAuthAccessToken) &&
                (string.IsNullOrWhiteSpace(accessToken) || cachedOAuthExpiresAt >= diskExpiresAt))
            {
                accessToken = cachedOAuthAccessToken;
                expiresAt = cachedOAuthExpiresAt;
                if (cachedOAuthScopes.Count > 0) scopes = cachedOAuthScopes;
                if (!string.IsNullOrWhiteSpace(cachedOAuthRefreshToken))
                    refreshToken = cachedOAuthRefreshToken;
            }

            plan = !string.IsNullOrWhiteSpace(sub) && !string.IsNullOrWhiteSpace(tier) ? $"{sub} / {tier}"
                 : !string.IsNullOrWhiteSpace(sub) ? sub
                 : !string.IsNullOrWhiteSpace(tier) ? tier
                 : cachedOAuthPlan;

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool strictlyExpired = expiresAt > 0 && nowMs >= expiresAt;
            bool expiringSoon = expiresAt > 0 && (nowMs + 5 * 60 * 1000L) >= expiresAt;
            bool missingSessionsScope = requireSessionsScope
                && scopes.Count > 0
                && !scopes.Any(s => string.Equals(s, SessionsScope, StringComparison.OrdinalIgnoreCase));

            bool needsRefresh = string.IsNullOrWhiteSpace(accessToken)
                             || expiringSoon
                             || forceRefresh
                             || missingSessionsScope;

            if (needsRefresh && allowNetworkRefresh && !string.IsNullOrWhiteSpace(refreshToken))
            {
                bool canAttemptRefresh =
                    !string.Equals(refreshToken, failedRefreshToken, StringComparison.Ordinal) &&
                    (forceRefresh || DateTimeOffset.UtcNow >= oauthRefreshBackoffUntil);

                if (canAttemptRefresh &&
                    TryRefreshOAuthToken(refreshToken!, sub, tier, out var refreshedAccess, out var refreshedPlan))
                {
                    plan = refreshedPlan ?? plan;
                    return refreshedAccess;
                }
            }

            // Never hand back a token that is already past its expiry timestamp or that just failed a
            // forced refresh — returning null lets callers fall back cleanly to the browser session key.
            if (strictlyExpired || forceRefresh)
                return null;

            return !string.IsNullOrWhiteSpace(accessToken) ? accessToken : null;
        }
    }

    private static bool TryRefreshOAuthToken(
        string refreshToken,
        string? existingSub,
        string? existingTier,
        out string? newAccessToken,
        out string? plan)
    {
        newAccessToken = null;
        plan = null;

        try
        {
            // Request the full Claude AI OAuth scope set (including user:sessions:claude_code).
            // Anthropic's token endpoint allows scope expansion on refresh_token grants.
            var (status, body) = PostOAuthTokenRefresh(refreshToken, OAuthRefreshScopes);
            if (status == 400 && body.Contains("scope", StringComparison.OrdinalIgnoreCase))
            {
                // Fallback without explicit scope expansion if the token was issued with restricted scopes.
                (status, body) = PostOAuthTokenRefresh(refreshToken, null);
            }

            if (status is 400 or 401)
            {
                failedRefreshToken = refreshToken;
                Log.WriteOnce("claude-oauth-refresh-rejected",
                    $"claude oauth refresh token was rejected (HTTP {status})");
                return false;
            }

            if (status != 200)
            {
                oauthRefreshBackoffUntil = DateTimeOffset.UtcNow.AddMinutes(2);
                Log.WriteOnce("claude-oauth-refresh-err",
                    $"claude oauth refresh returned HTTP {status}");
                return false;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var access = Text(root, "access_token")?.Trim();
            if (string.IsNullOrWhiteSpace(access))
            {
                oauthRefreshBackoffUntil = DateTimeOffset.UtcNow.AddMinutes(2);
                return false;
            }

            var rotatedRefresh = Text(root, "refresh_token")?.Trim();
            if (string.IsNullOrWhiteSpace(rotatedRefresh)) rotatedRefresh = refreshToken;

            int expiresIn = root.TryGetProperty("expires_in", out var expEl) && Number(expEl, out double s)
                ? (int)s : 28800;
            long expiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (expiresIn * 1000L);

            var scopeStr = Text(root, "scope") ?? OAuthRefreshScopes;
            var scopes = scopeStr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            string? orgUuid = null;
            string? email = null;
            if (root.TryGetProperty("organization", out var orgEl) && orgEl.ValueKind == JsonValueKind.Object)
                orgUuid = Text(orgEl, "uuid");
            if (root.TryGetProperty("account", out var acctEl) && acctEl.ValueKind == JsonValueKind.Object)
                email = Text(acctEl, "email_address") ?? Text(acctEl, "email");

            string? sub = existingSub;
            string? tier = existingTier;
            if (string.IsNullOrWhiteSpace(orgUuid) || string.IsNullOrWhiteSpace(sub))
            {
                FetchOAuthProfileMetadata(access, ref orgUuid, ref email, out var profSub, out var profTier);
                sub ??= profSub;
                tier ??= profTier;
            }

            SaveOAuthCredentialsToDiskAndCache(access, rotatedRefresh, expiresAtMs, scopes, sub, tier, orgUuid, email);

            Action? save = null;
            lock (gate)
            {
                if (settings is not null)
                {
                    if (!string.IsNullOrWhiteSpace(rotatedRefresh))
                        settings.RefreshToken = Secret.Protect(rotatedRefresh);
                    if (!string.IsNullOrWhiteSpace(email))
                        settings.AccountEmail = email;
                    if (!string.IsNullOrWhiteSpace(orgUuid) && string.IsNullOrWhiteSpace(settings.OrgId))
                        settings.OrgId = orgUuid;
                    save = persist;
                }
            }
            save?.Invoke();

            newAccessToken = access;
            plan = cachedOAuthPlan;
            failedRefreshToken = null;
            Log.Write("claude oauth access token refreshed successfully");
            return true;
        }
        catch (Exception ex)
        {
            oauthRefreshBackoffUntil = DateTimeOffset.UtcNow.AddMinutes(1);
            Log.WriteOnce("claude-oauth-refresh-ex", $"claude oauth refresh failed: {ex.Message}");
            return false;
        }
    }

    private static (int Status, string Body) PostOAuthTokenRefresh(string refreshToken, string? scope)
    {
        object payload = scope is not null
            ? new
            {
                grant_type = "refresh_token",
                refresh_token = refreshToken,
                client_id = OAuthClientId,
                scope,
            }
            : new
            {
                grant_type = "refresh_token",
                refresh_token = refreshToken,
                client_id = OAuthClientId,
            };

        string json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, OAuthTokenUrl) { Content = content };
        req.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var resp = http.Send(req, HttpCompletionOption.ResponseContentRead);
        var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return ((int)resp.StatusCode, body);
    }

    private static void FetchOAuthProfileMetadata(
        string accessToken,
        ref string? orgUuid,
        ref string? email,
        out string? subscriptionType,
        out string? rateLimitTier)
    {
        subscriptionType = null;
        rateLimitTier = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, OAuthProfileUrl);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            req.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBetaHeader);
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var resp = http.Send(req, HttpCompletionOption.ResponseContentRead);
            if (!resp.IsSuccessStatusCode) return;

            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("account", out var acct) && acct.ValueKind == JsonValueKind.Object)
                email ??= Text(acct, "email") ?? Text(acct, "email_address");

            if (root.TryGetProperty("organization", out var org) && org.ValueKind == JsonValueKind.Object)
            {
                orgUuid ??= Text(org, "uuid");
                rateLimitTier = Text(org, "rate_limit_tier");
                subscriptionType = Text(org, "organization_type") switch
                {
                    "claude_max" => "max",
                    "claude_pro" => "pro",
                    "claude_enterprise" => "enterprise",
                    "claude_team" => "team",
                    var other => other,
                };
            }
        }
        catch { }
    }

    private static void SaveOAuthCredentialsToDiskAndCache(
        string accessToken,
        string? refreshToken,
        long expiresAtMs,
        List<string> scopes,
        string? subType,
        string? rateTier,
        string? orgUuid,
        string? email)
    {
        cachedOAuthAccessToken = accessToken;
        cachedOAuthRefreshToken = refreshToken;
        cachedOAuthExpiresAt = expiresAtMs;
        cachedOAuthScopes = scopes;
        cachedOAuthPlan = !string.IsNullOrWhiteSpace(subType) && !string.IsNullOrWhiteSpace(rateTier)
            ? $"{subType} / {rateTier}"
            : !string.IsNullOrWhiteSpace(subType) ? subType : rateTier;

        try
        {
            var dir = Path.GetDirectoryName(CredentialsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            JsonObject rootObj = new();
            if (File.Exists(CredentialsPath))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(CredentialsPath)) is JsonObject parsed)
                        rootObj = parsed;
                }
                catch { }
            }

            var oauthObj = rootObj["claudeAiOauth"] as JsonObject ?? new JsonObject();
            oauthObj["accessToken"] = accessToken;
            if (!string.IsNullOrWhiteSpace(refreshToken))
                oauthObj["refreshToken"] = refreshToken;
            oauthObj["expiresAt"] = expiresAtMs;

            var scopesArr = new JsonArray();
            foreach (var s in scopes) scopesArr.Add(s);
            oauthObj["scopes"] = scopesArr;

            if (!string.IsNullOrWhiteSpace(subType))
                oauthObj["subscriptionType"] = subType;
            if (!string.IsNullOrWhiteSpace(rateTier))
                oauthObj["rateLimitTier"] = rateTier;

            rootObj["claudeAiOauth"] = oauthObj;
            File.WriteAllText(CredentialsPath, rootObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.WriteOnce("claude-creds-write", $"could not update {CredentialsPath}: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(orgUuid))
        {
            try
            {
                string globalConfigPath = RemoteChatUsage.ClaudeGlobalConfigPath;
                JsonObject cfgObj = new();
                if (File.Exists(globalConfigPath))
                {
                    try
                    {
                        if (JsonNode.Parse(File.ReadAllText(globalConfigPath)) is JsonObject parsed)
                            cfgObj = parsed;
                    }
                    catch { }
                }

                var acctObj = cfgObj["oauthAccount"] as JsonObject ?? new JsonObject();
                acctObj["organizationUuid"] = orgUuid;
                if (!string.IsNullOrWhiteSpace(email))
                    acctObj["emailAddress"] = email;
                cfgObj["oauthAccount"] = acctObj;
                File.WriteAllText(globalConfigPath, cfgObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    /// <summary>
    /// Performs an authenticated GET for Claude Code remote session/environment endpoints (`/v1/...`)
    /// using the stored browser session key (if any), including the required organisation UUID,
    /// `anthropic-version`, and `anthropic-beta` headers, and adopting any rotated session cookie.
    /// </summary>
    internal static (int Status, string Body)? GetWithStoredSessionKey(string pathAndQuery, string? fallbackOrgUuid = null)
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
            org = ResolveOrg(ref activeKey) ?? fallbackOrgUuid;
            if (!string.IsNullOrWhiteSpace(org))
            {
                lock (gate)
                {
                    if (settings is not null) { settings.OrgId = org; persist?.Invoke(); }
                }
            }
        }

        var extraHeaders = new List<(string Name, string Value)>
        {
            ("anthropic-version", AnthropicVersionHeader),
            ("anthropic-beta", RemoteBetaHeader),
            ("Accept", "application/json"),
            ("Referer", Origin + "/code"),
        };
        if (!string.IsNullOrWhiteSpace(org))
            extraHeaders.Add(("x-organization-uuid", org));

        // 1. Try claude.ai first (via curl.exe + SChannel) so rotated session cookies are adopted.
        var reply = Get($"{Origin}{pathAndQuery}", activeKey, org, extraHeaders);
        if (reply is not null && reply.Status < 400)
        {
            AdoptRotatedKey(reply, ref activeKey);
            return (reply.Status, reply.Body);
        }

        // 2. Also try api.anthropic.com directly (which is never behind claude.ai's Cloudflare HTML
        //    challenge and accepts the same sessionKey cookie + x-organization-uuid header).
        var direct = Get($"{AnthropicApiOrigin}{pathAndQuery}", activeKey, org, extraHeaders);
        if (direct is not null && (reply is null || direct.Status < 400 || !IsAuthRejection(reply)))
        {
            if (direct.Status < 400) AdoptRotatedKey(direct, ref activeKey);
            return (direct.Status, direct.Body);
        }

        return reply is not null ? (reply.Status, reply.Body) : null;
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
    private static Response? Get(
        string url,
        string key,
        string? org = null,
        IReadOnlyList<(string Name, string Value)>? extraHeaders = null)
    {
        // claude.ai wants the active organisation alongside the session key on org-scoped calls.
        // Sending only the key gets a 401 that is indistinguishable from an expired one, which is
        // exactly how this presented: a freshly pasted key reported as no longer accepted.
        var cookie = string.IsNullOrWhiteSpace(org)
            ? $"sessionKey={key}"
            : $"sessionKey={key}; lastActiveOrg={org}";

        var viaCurl = ViaCurl(url, cookie, ignoreRevocation: false, extraHeaders)
                   ?? ViaCurl(url, cookie, ignoreRevocation: true, extraHeaders);
        if (viaCurl is not null) return viaCurl;

        Log.WriteOnce("claude-transport",
            "curl was unavailable or failed; falling back to the .NET client, which Cloudflare "
          + "may challenge regardless of the key");

        return ViaHttpClient(url, cookie, extraHeaders);
    }

    private static Response? ViaHttpClient(
        string url,
        string cookie,
        IReadOnlyList<(string Name, string Value)>? extraHeaders = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
            request.Headers.TryAddWithoutValidation("anthropic-client-platform", "web_claude_ai");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);

            bool hasAccept = false;
            bool hasReferer = false;
            if (extraHeaders is not null)
            {
                foreach (var (name, value) in extraHeaders)
                {
                    if (string.Equals(name, "Accept", StringComparison.OrdinalIgnoreCase)) hasAccept = true;
                    if (string.Equals(name, "Referer", StringComparison.OrdinalIgnoreCase)) hasReferer = true;
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }
            if (!hasAccept) request.Headers.TryAddWithoutValidation("Accept", "*/*");
            if (!hasReferer) request.Headers.TryAddWithoutValidation("Referer", Origin + "/");

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
    private static Response? ViaCurl(
        string url,
        string cookie,
        bool ignoreRevocation,
        IReadOnlyList<(string Name, string Value)>? extraHeaders = null)
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
                .AppendLine($"-H \"Cookie: {Escape(cookie)}\"");

            if (extraHeaders is not null)
            {
                foreach (var (name, value) in extraHeaders)
                    config.AppendLine($"-H \"{Escape(name)}: {Escape(value)}\"");
            }

            curl.StandardInput.Write(config.ToString());
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
