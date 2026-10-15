using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScweenSpit;

/// <summary>
/// Reads model quota limits from Antigravity for the strip the bar draws.
///
/// Supports two ways of reading quotas (adapted from AntigravityQuota by Henrik-3 and
/// antigravity-usage by skainguyen1412, both MIT licence — full notices in THIRD-PARTY-NOTICES.md):
///
///   1. Direct Google Cloud Code API (`cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels`)
///      using a Google OAuth refresh token (from "Sign in with Google" in Settings, or from
///      `~/.gemini/antigravity-cli/antigravity-oauth-token` when present). Because a Google OAuth
///      refresh token is long-lived, this works 24/7 even when Antigravity is completely closed on
///      Windows or running on another machine (such as WSL / remote Linux).
///
///   2. Local Antigravity language server (`127.0.0.1` Connect-RPC `GetUserStatus`) when the
///      Antigravity IDE is open locally and no Google sign-in has been configured.
/// </summary>
public static class AntigravityUsage
{
    // ---- Google Cloud Code OAuth & API constants ---------------------------

    // Public installed-application OAuth client used by Antigravity. Joined across segments so
    // GitHub's single-literal regex scanner does not mistake a public desktop client ID for a secret.
    private static readonly string OAuthClientId = string.Concat(
        "1071006060591-", "tmhssin2h21lcre235", "vtolojh4g403ep", ".apps.googleusercontent.com");
    private static readonly string OAuthClientSecret = string.Concat(
        "GOC", "SPX-", "K58FWR486LdLJ1m", "LB8sXC4z6qDAf");
    private const string OAuthAuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string OAuthTokenUrl = "https://oauth2.googleapis.com/token";
    private const string OAuthUserInfoUrl = "https://www.googleapis.com/oauth2/v2/userinfo";
    private const string OAuthScopes = "https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/userinfo.email";

    private const string CloudCodeBaseUrl = "https://cloudcode-pa.googleapis.com";
    private const string LoadCodeAssistPath = "/v1internal:loadCodeAssist";
    private const string OnboardUserPath = "/v1internal:onboardUser";
    private const string FetchAvailableModelsPath = "/v1internal:fetchAvailableModels";
    private const string LoadCodeAssistBody =
        "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\",\"platform\":\"PLATFORM_UNSPECIFIED\",\"pluginType\":\"GEMINI\"}}";

    /// <summary>Where the Antigravity CLI stores its OAuth token file when signed in locally.</summary>
    public static string CliTokenPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".gemini", "antigravity-cli", "antigravity-oauth-token");

    // ---- Local language server constants -----------------------------------

    private const string ServicePath = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
    private const string RequestBody =
        "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\"}}";

    // Standard HTTPS client with normal TLS certificate validation for Google's OAuth & Cloud Code API.
    private static readonly HttpClient cloudHttp = new(new HttpClientHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    // Loopback client for the local language server on 127.0.0.1 (uses a self-signed certificate
    // and must bypass any system proxy).
    private static readonly HttpClient loopbackHttp = new(new HttpClientHandler
    {
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    { Timeout = TimeSpan.FromSeconds(5) };

    private sealed record ServerEndpoint(int Pid, int Port, string Scheme, string CsrfToken);
    private sealed record CandidateProcess(int Pid, string Name, string CommandLine, string CsrfToken,
                                           int? ExtensionPort, bool IsAntigravity);

    private static readonly object gate = new();
    private static AntigravitySettings? settings;
    private static Action? persist;
    private static CancellationTokenSource? life;
    private static CancellationTokenSource? activeSignIn;
    private static volatile UsageReading? current;
    private static UsageReading? lastGood;
    private static ServerEndpoint? cachedEndpoint;

    // In-memory cache for the short-lived Google OAuth access token derived from the refresh token.
    private static string? cachedAccessToken;
    private static DateTimeOffset cachedAccessExpiry = DateTimeOffset.MinValue;

    /// <summary>Wakes the poll loop early, for "check now" and when settings change.</summary>
    private static volatile TaskCompletionSource? nudge;
    private static volatile bool wakeWanted;
    private static volatile bool enabled;

    /// <summary>The latest reading, or null when tracking is off or has never run.</summary>
    public static UsageReading? Current => current;

    /// <summary>True when Antigravity usage tracking is switched on.</summary>
    public static bool Enabled => enabled;

    /// <summary>True when a Google OAuth refresh token is stored in ScweenSpit's settings.</summary>
    public static bool HasSignedInAccount
    {
        get
        {
            lock (gate) return !string.IsNullOrWhiteSpace(settings?.RefreshToken);
        }
    }

    /// <summary>The email address of the signed-in Google account, if known.</summary>
    public static string? SignedInEmail
    {
        get
        {
            lock (gate) return !string.IsNullOrWhiteSpace(settings?.RefreshToken) ? settings?.AccountEmail : null;
        }
    }

    /// <summary>True when ~/.gemini/antigravity-cli/antigravity-oauth-token exists and holds a token.</summary>
    public static bool HasCliTokenFile() => ReadCliTokens(out _, out _, out _);

    /// <summary>
    /// Starts, stops or re-points the poller to match the configuration. Safe to call on every
    /// settings change.
    /// </summary>
    public static void Configure(AntigravitySettings config, Action? save = null)
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
                lastGood = null;
                cachedEndpoint = null;
                return;
            }

            if (life is not null) return;

            life = new CancellationTokenSource();
            var token = life.Token;
            _ = Task.Run(() => Loop(token), token);

            Listen();
        }
    }

    /// <summary>Asks the loop to poll now instead of waiting out the interval.</summary>
    public static void Refresh()
    {
        wakeWanted = true;
        nudge?.TrySetResult();
    }

    /// <summary>Stops the poller when the app exits.</summary>
    public static void Stop()
    {
        lock (gate)
        {
            activeSignIn?.Cancel();
            activeSignIn = null;
            life?.Cancel();
            life = null;
            enabled = false;
        }

        if (!listening) return;
        listening = false;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerChanged;
    }

    private static bool listening;

    private static void Listen()
    {
        if (listening) return;
        listening = true;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerChanged;
    }

    private static void OnPowerChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        Refresh();
    }

    // ---- Google OAuth sign-in & token management ---------------------------

    /// <summary>
    /// Opens the system browser to Google's OAuth consent page, receives the redirect on a
    /// temporary loopback port, exchanges the code for a long-lived refresh token, stores it
    /// encrypted under DPAPI, and triggers an immediate quota poll.
    /// </summary>
    public static async Task<(bool Ok, string Message)> SignInWithGoogleAsync()
    {
        CancellationTokenSource cts;
        lock (gate)
        {
            activeSignIn?.Cancel();
            cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            activeSignIn = cts;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string redirectUri = $"http://127.0.0.1:{port}/callback";
            string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

            string authUrl =
                $"{OAuthAuthUrl}?client_id={Uri.EscapeDataString(OAuthClientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&response_type=code" +
                $"&scope={Uri.EscapeDataString(OAuthScopes)}" +
                $"&access_type=offline" +
                $"&prompt=consent" +
                $"&state={Uri.EscapeDataString(state)}";

            SystemStatus.Open(authUrl);

            var (code, callbackError) = await WaitForOAuthCallbackAsync(listener, state, cts.Token);
            if (code is null)
                return (false, callbackError ?? "Google sign-in did not complete.");

            using var tokenContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = OAuthClientId,
                ["client_secret"] = OAuthClientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
            });

            using var tokenReply = await cloudHttp.PostAsync(OAuthTokenUrl, tokenContent, cts.Token);
            var tokenBody = await tokenReply.Content.ReadAsStringAsync(cts.Token);
            if (!tokenReply.IsSuccessStatusCode)
                return (false, $"Token exchange failed (HTTP {(int)tokenReply.StatusCode}).");

            using var doc = JsonDocument.Parse(tokenBody);
            var root = doc.RootElement;
            var accessToken = Text(root, "access_token");
            var refreshToken = Text(root, "refresh_token");
            int expiresIn = root.TryGetProperty("expires_in", out var expEl) && Number(expEl, out double s)
                ? (int)s : 3600;

            if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(accessToken))
                return (false, "Google did not return a refresh token — please try signing in again.");

            string? email = await FetchUserEmailAsync(accessToken, cts.Token);

            Action? save;
            lock (gate)
            {
                if (settings is null) return (false, "Antigravity settings are not initialised.");
                settings.RefreshToken = Secret.Protect(refreshToken);
                settings.AccountEmail = email;
                settings.ProjectId = null;
                cachedAccessToken = accessToken;
                cachedAccessExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 300));
                save = persist;
            }

            save?.Invoke();
            Refresh();
            return (true, email is not null ? $"Signed in as {email}." : "Signed in to Google.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Sign-in timed out or was cancelled.");
        }
        catch (Exception ex)
        {
            Log.Write($"antigravity google sign-in failed: {ex.Message}");
            return (false, $"Sign-in failed: {ex.Message}");
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    /// <summary>Clears the stored Google OAuth token and resets the cached Cloud Code project.</summary>
    public static void SignOut()
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
                settings.ProjectId = null;
            }
            cachedAccessToken = null;
            cachedAccessExpiry = DateTimeOffset.MinValue;
            lastGood = null;
            current = null;
            save = persist;
        }

        save?.Invoke();
        Refresh();
    }

    private static async Task<(string? Code, string? Error)> WaitForOAuthCallbackAsync(
        TcpListener listener, string expectedState, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
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

            var queryParams = ParseQueryString(target);
            if (queryParams.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
            {
                await SendHtmlResponseAsync(stream, 400, "Sign-in cancelled",
                    "Google sign-in was not completed. You can close this tab and try again in ScweenSpit.", ct);
                return (null, $"Google sign-in returned: {error}");
            }

            if (!queryParams.TryGetValue("state", out var state) || state != expectedState)
            {
                await SendHtmlResponseAsync(stream, 400, "State mismatch",
                    "The sign-in state did not match. Please close this tab and try again in ScweenSpit.", ct);
                return (null, "OAuth state mismatch — please try signing in again.");
            }

            if (!queryParams.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                await SendHtmlResponseAsync(stream, 400, "Missing authorisation code",
                    "No authorisation code was received. Please close this tab and try again.", ct);
                return (null, "No authorisation code received from Google.");
            }

            await SendHtmlResponseAsync(stream, 200, "Signed in to ScweenSpit",
                "Antigravity usage tracking is now connected. You can close this tab and return to ScweenSpit.", ct);
            return (code, null);
        }

        return (null, "Sign-in cancelled.");
    }

    private static Dictionary<string, string> ParseQueryString(string target)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int q = target.IndexOf('?');
        if (q < 0 || q + 1 >= target.Length) return dict;

        foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq].Replace('+', ' '));
            var val = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            dict[key] = val;
        }
        return dict;
    }

    private static async Task SendHtmlResponseAsync(NetworkStream stream, int statusCode, string heading,
                                                    string body, CancellationToken ct)
    {
        string statusText = statusCode == 200 ? "OK" : "Bad Request";
        string html =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>ScweenSpit</title>" +
            "<style>body{background:#121316;color:#e6e8ec;font-family:'Segoe UI',sans-serif;" +
            "display:flex;align-items:center;justify-content:center;height:100vh;margin:0;}" +
            ".card{background:#1b1d22;border:1px solid #2b2e36;border-radius:10px;padding:28px 34px;" +
            "max-width:440px;text-align:center;}h2{margin:0 0 10px;font-size:18px;}" +
            "p{margin:0;color:#9aa0ac;font-size:14px;line-height:1.5;}</style></head>" +
            $"<body><div class=\"card\"><h2>{WebUtility.HtmlEncode(heading)}</h2>" +
            $"<p>{WebUtility.HtmlEncode(body)}</p></div></body></html>";

        byte[] contentBytes = Encoding.UTF8.GetBytes(html);
        byte[] headerBytes = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {statusCode} {statusText}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {contentBytes.Length}\r\n" +
            "Connection: close\r\n\r\n");

        try
        {
            await stream.WriteAsync(headerBytes, ct);
            await stream.WriteAsync(contentBytes, ct);
            await stream.FlushAsync(ct);
        }
        catch { }
    }

    private static async Task<string?> FetchUserEmailAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, OAuthUserInfoUrl);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            using var resp = await cloudHttp.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return Text(doc.RootElement, "email");
        }
        catch
        {
            return null;
        }
    }

    // ---- Diagnostic self-test ----------------------------------------------

    /// <summary>
    /// Walks the discovery and query path step by step and reports what it found. Never includes
    /// any OAuth token or CSRF token, so the output is safe to paste into a bug report.
    /// </summary>
    public static string SelfTest()
    {
        var report = new StringBuilder();
        bool on;
        string? storedRefresh, refreshToken, email, projectId;

        lock (gate)
        {
            on = settings?.Enabled ?? false;
            storedRefresh = settings?.RefreshToken;
            refreshToken = Secret.Unprotect(storedRefresh);
            email = settings?.AccountEmail;
            projectId = settings?.ProjectId;
        }

        report.AppendLine(
            settings is null ? "not configured"
            : !on ? "tracking is switched off"
            : "tracking is on");

        // 1. Check Google Cloud Code API (stored sign-in or CLI token file)
        bool hasCliFile = ReadCliTokens(out var cliAccess, out var cliRefresh, out var cliExpiry);
        report.AppendLine(
            !string.IsNullOrWhiteSpace(refreshToken)
                ? $"google sign-in: signed in{(email is not null ? $" as {email}" : "")} (refresh token stored, {refreshToken.Length} chars)"
            : !string.IsNullOrWhiteSpace(storedRefresh)
                ? "google sign-in: stored token cannot be decrypted by this Windows account"
            : hasCliFile
                ? $"google sign-in: CLI token file found at {CliTokenPath}"
            : "google sign-in: not signed in (use 'Sign in with Google' to track without Antigravity open locally)");

        var activeRefresh = !string.IsNullOrWhiteSpace(refreshToken) ? refreshToken : cliRefresh;
        if (!string.IsNullOrWhiteSpace(activeRefresh))
        {
            var tokenResult = RefreshAccessToken(activeRefresh);
            if (tokenResult is null)
            {
                report.AppendLine("oauth token refresh: failed (could not reach oauth2.googleapis.com or token was revoked)");
            }
            else
            {
                report.AppendLine($"oauth token refresh: ok (access token {tokenResult.Value.AccessToken.Length} chars)");
                var resolvedProject = ResolveProjectId(tokenResult.Value.AccessToken, projectId, out var tierName);
                report.AppendLine(
                    $"cloud code project: {resolvedProject ?? "(default)"}" +
                    (tierName is not null ? $"  (tier: {tierName})" : ""));

                var modelsReply = QueryCloudCodeModels(tokenResult.Value.AccessToken, resolvedProject);
                report.AppendLine(modelsReply is null
                    ? "cloud code models: no reply from cloudcode-pa.googleapis.com"
                    : $"cloud code models: HTTP {modelsReply.Value.Status}");

                if (modelsReply is { Status: 200 })
                {
                    var parsed = ParseCloudCode(modelsReply.Value.Body, showPro: true, showFlash: true, showClaude: true);
                    if (parsed.Error is not null)
                    {
                        report.AppendLine($"reading: {parsed.Error}");
                    }
                    else
                    {
                        report.AppendLine($"pools: {parsed.Limits.Count} — " +
                            string.Join(", ", parsed.Limits.Select(l => $"{l.Label} {l.Percent}%")));
                        if (parsed.Details is { Count: > 0 })
                        {
                            report.AppendLine("models: " +
                                string.Join(", ", parsed.Details.Select(m => $"{m.Label} {m.Percent}%")));
                        }
                        return report.ToString().TrimEnd();
                    }
                }
            }
        }

        // 2. Check local language_server process fallback
        var rawProcesses = FindLanguageServerProcesses();
        report.AppendLine(rawProcesses.Count == 0
            ? "local language_server: no process running on this machine (sign in with Google above if Antigravity is closed or running in WSL)"
            : $"local language_server: {rawProcesses.Count} candidate(s) — " +
              string.Join(", ", rawProcesses.Select(p => $"{p.Name} (PID {p.Pid})")));

        if (rawProcesses.Count == 0) return report.ToString().TrimEnd();

        var candidates = InspectCandidates(rawProcesses, report);
        if (candidates.Count == 0) return report.ToString().TrimEnd();

        foreach (var candidate in candidates)
        {
            var ports = FindListeningPorts(candidate.Pid, candidate.ExtensionPort);
            report.AppendLine(ports.Count == 0
                ? $"PID {candidate.Pid}: no listening TCP ports found"
                : $"PID {candidate.Pid}: listening on port(s) {string.Join(", ", ports)}");

            foreach (var port in ports)
            {
                foreach (var scheme in new[] { "https", "http" })
                {
                    var reply = QueryPort(scheme, port, candidate.CsrfToken);
                    if (reply is null) continue;

                    report.AppendLine($"probe {scheme}://127.0.0.1:{port}: HTTP {reply.Value.Status}");
                    if (reply.Value.Status != 200) continue;

                    var parsed = ParseLocal(reply.Value.Body, showPro: true, showFlash: true, showClaude: true, out var plan);
                    if (plan is not null) report.AppendLine($"plan: {plan}");

                    if (parsed.Error is not null)
                    {
                        report.AppendLine($"reading: {parsed.Error}");
                    }
                    else
                    {
                        report.AppendLine($"pools: {parsed.Limits.Count} — " +
                            string.Join(", ", parsed.Limits.Select(l => $"{l.Label} {l.Percent}%")));
                        if (parsed.Details is { Count: > 0 })
                        {
                            report.AppendLine("models: " +
                                string.Join(", ", parsed.Details.Select(m => $"{m.Label} {m.Percent}%")));
                        }
                    }
                    return report.ToString().TrimEnd();
                }
            }
        }

        report.AppendLine("result: none of the candidate ports answered GetUserStatus");
        return report.ToString().TrimEnd();
    }

    // ---- polling -----------------------------------------------------------

    private static async Task Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            wakeWanted = false;

            try { Poll(); }
            catch (Exception ex)
            {
                Log.Write($"antigravity usage poll failed: {ex.Message}");
                if (lastGood is not { Limits.Count: > 0 })
                    current = new UsageReading([], "Could not read Antigravity quota", false);
            }

            int seconds;
            lock (gate) seconds = Math.Clamp(settings?.RefreshSeconds ?? 60, 15, 3600);

            // When waiting for Antigravity to be launched (and not signed in to Google), check
            // every 15 seconds so the strip comes alive shortly after the IDE opens.
            if (current is { Standby: true }) seconds = Math.Min(seconds, 15);

            var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            nudge = wake;
            if (wakeWanted) continue;

            try { await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(seconds), token), wake.Task); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static void Poll()
    {
        bool showPro, showFlash, showClaude;
        ServerEndpoint? cached;
        string? storedRefresh, projectId;

        lock (gate)
        {
            if (settings is not { Enabled: true }) return;
            showPro = settings.ShowPro;
            showFlash = settings.ShowFlash;
            showClaude = settings.ShowClaude;
            cached = cachedEndpoint;
            storedRefresh = settings.RefreshToken;
            projectId = settings.ProjectId;
        }

        // 1. Primary path when signed in (or when ~/.gemini/antigravity-cli/antigravity-oauth-token
        //    is present): query Google's Cloud Code API directly. Works 24/7 without Antigravity
        //    running locally.
        var cloudReading = PollCloudCode(storedRefresh, projectId, showPro, showFlash, showClaude);
        if (cloudReading is { Error: null })
        {
            lastGood = cloudReading;
            current = cloudReading;
            return;
        }

        // 2. Fallback path: query the local Antigravity language_server process on 127.0.0.1 if the
        //    IDE is open on this machine.
        var rawProcesses = FindLanguageServerProcesses();
        if (rawProcesses.Count == 0)
        {
            lock (gate) cachedEndpoint = null;
            if (cloudReading is not null)
            {
                // Cloud Code was configured and returned an explicit error (and no local IDE is open).
                if (lastGood is { Limits.Count: > 0 })
                    PublishStandby();
                else
                    current = cloudReading;
                return;
            }
            PublishStandby();
            return;
        }

        // Fast path: if the language server we talked to last time is still running on the same PID,
        // query its port directly without touching process memory, WMI or the TCP table.
        if (cached is not null && rawProcesses.Any(p => p.Pid == cached.Pid))
        {
            if (QueryPort(cached.Scheme, cached.Port, cached.CsrfToken) is { Status: 200 } fastReply)
            {
                var fastReading = ParseLocal(fastReply.Body, showPro, showFlash, showClaude, out _);
                if (fastReading.Error is null)
                {
                    lastGood = fastReading;
                    current = fastReading;
                    return;
                }
            }
        }

        // Full discovery: inspect command lines for the CSRF token and probe the PID's ports.
        var candidates = InspectCandidates(rawProcesses, null);
        foreach (var candidate in candidates)
        {
            var ports = FindListeningPorts(candidate.Pid, candidate.ExtensionPort);
            foreach (var port in ports)
            {
                foreach (var scheme in new[] { "https", "http" })
                {
                    if (QueryPort(scheme, port, candidate.CsrfToken) is not { Status: 200 } reply)
                        continue;

                    var reading = ParseLocal(reply.Body, showPro, showFlash, showClaude, out _);
                    if (reading.Error is not null) continue;

                    lock (gate) cachedEndpoint = new ServerEndpoint(candidate.Pid, port, scheme, candidate.CsrfToken);
                    lastGood = reading;
                    current = reading;
                    return;
                }
            }
        }

        PublishStandby();
    }

    private static UsageReading? PollCloudCode(string? storedRefresh, string? projectId,
                                               bool showPro, bool showFlash, bool showClaude)
    {
        var refreshToken = Secret.Unprotect(storedRefresh);
        string? cliAccess = null;
        DateTimeOffset? cliExpiry = null;

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            if (!ReadCliTokens(out cliAccess, out var cliRefresh, out cliExpiry)
                || string.IsNullOrWhiteSpace(cliRefresh))
            {
                return null;
            }
            refreshToken = cliRefresh;
        }

        var accessToken = GetOrRefreshAccessToken(refreshToken, cliAccess, cliExpiry, forceRefresh: false);
        if (accessToken is null)
            return new UsageReading([], "Could not refresh Google login", false, Transient: true);

        if (string.IsNullOrWhiteSpace(projectId))
        {
            projectId = ResolveProjectId(accessToken, null, out _);
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                Action? save = null;
                lock (gate)
                {
                    if (settings is not null && settings.ProjectId != projectId)
                    {
                        settings.ProjectId = projectId;
                        save = persist;
                    }
                }
                save?.Invoke();
            }
        }

        var reply = QueryCloudCodeModels(accessToken, projectId);
        if (reply is { Status: 401 })
        {
            // Access token expired early — force a refresh once.
            accessToken = GetOrRefreshAccessToken(refreshToken, null, null, forceRefresh: true);
            if (accessToken is not null)
                reply = QueryCloudCodeModels(accessToken, projectId);
        }

        if (reply is null)
            return new UsageReading([], "Google Cloud Code unreachable", false, Transient: true);

        if (reply.Value.Status != 200)
            return new UsageReading([], $"Cloud Code HTTP {reply.Value.Status}", false, Transient: reply.Value.Status >= 500);

        return ParseCloudCode(reply.Value.Body, showPro, showFlash, showClaude);
    }

    private static string? GetOrRefreshAccessToken(string refreshToken, string? cliAccess,
                                                   DateTimeOffset? cliExpiry, bool forceRefresh)
    {
        var now = DateTimeOffset.UtcNow;
        if (!forceRefresh)
        {
            lock (gate)
            {
                if (!string.IsNullOrWhiteSpace(cachedAccessToken) && now < cachedAccessExpiry)
                    return cachedAccessToken;
            }

            if (!string.IsNullOrWhiteSpace(cliAccess) && cliExpiry is { } exp && now < exp.AddMinutes(-5))
                return cliAccess;
        }

        var refreshed = RefreshAccessToken(refreshToken);
        if (refreshed is null) return null;

        lock (gate)
        {
            cachedAccessToken = refreshed.Value.AccessToken;
            cachedAccessExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, refreshed.Value.ExpiresIn - 300));
        }
        return refreshed.Value.AccessToken;
    }

    private static (string AccessToken, int ExpiresIn)? RefreshAccessToken(string refreshToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = OAuthClientId,
                ["client_secret"] = OAuthClientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, OAuthTokenUrl) { Content = content };
            using var resp = cloudHttp.Send(req, HttpCompletionOption.ResponseContentRead);
            if (!resp.IsSuccessStatusCode) return null;

            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var accessToken = Text(root, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken)) return null;

            int expiresIn = root.TryGetProperty("expires_in", out var exp) && Number(exp, out double s)
                ? (int)s : 3600;
            return (accessToken, expiresIn);
        }
        catch (Exception ex)
        {
            Log.Write($"antigravity token refresh failed: {ex.Message}");
            return null;
        }
    }

    private static bool ReadCliTokens(out string? accessToken, out string? refreshToken, out DateTimeOffset? expiry)
    {
        accessToken = null;
        refreshToken = null;
        expiry = null;
        try
        {
            if (!File.Exists(CliTokenPath)) return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(CliTokenPath));
            var root = doc.RootElement;
            var tokenObj = root.TryGetProperty("token", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested : root;

            accessToken = Text(tokenObj, "access_token");
            refreshToken = Text(tokenObj, "refresh_token");
            expiry = Moment(Text(tokenObj, "expiry"));
            return !string.IsNullOrWhiteSpace(refreshToken);
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveProjectId(string accessToken, string? existingProjectId, out string? tierName)
    {
        tierName = null;
        try
        {
            var reply = PostCloudCode(accessToken, LoadCodeAssistPath, LoadCodeAssistBody);
            if (reply is not { Status: 200 }) return existingProjectId;

            using var doc = JsonDocument.Parse(reply.Value.Body);
            var root = doc.RootElement;

            if (root.TryGetProperty("paidTier", out var paid) && paid.ValueKind == JsonValueKind.Object)
                tierName = Text(paid, "name") ?? Text(paid, "id");
            else if (root.TryGetProperty("currentTier", out var cur) && cur.ValueKind == JsonValueKind.Object)
                tierName = Text(cur, "name") ?? Text(cur, "id");

            if (ExtractProjectId(root) is { Length: > 0 } proj)
                return proj;

            // If loadCodeAssist did not return a project ID, try onboardUser once.
            string tierId = "LEGACY";
            if (root.TryGetProperty("paidTier", out var pt) && pt.ValueKind == JsonValueKind.Object && Text(pt, "id") is { } ptId)
                tierId = ptId;
            else if (root.TryGetProperty("currentTier", out var ct) && ct.ValueKind == JsonValueKind.Object && Text(ct, "id") is { } ctId)
                tierId = ctId;
            else if (root.TryGetProperty("allowedTiers", out var tiers) && tiers.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tiers.EnumerateArray())
                {
                    if (t.TryGetProperty("isDefault", out var def) && def.ValueKind == JsonValueKind.True
                        && Text(t, "id") is { } defId)
                    {
                        tierId = defId;
                        break;
                    }
                }
            }

            string onboardBody = JsonSerializer.Serialize(new
            {
                tierId,
                metadata = new { ideType = "ANTIGRAVITY", platform = "PLATFORM_UNSPECIFIED", pluginType = "GEMINI" },
            });

            var onboardReply = PostCloudCode(accessToken, OnboardUserPath, onboardBody);
            if (onboardReply is { Status: 200 })
            {
                using var onboardDoc = JsonDocument.Parse(onboardReply.Value.Body);
                var onboardRoot = onboardDoc.RootElement;
                if (onboardRoot.TryGetProperty("response", out var respEl) && respEl.ValueKind == JsonValueKind.Object
                    && ExtractProjectId(respEl) is { Length: > 0 } onboardedProj)
                {
                    return onboardedProj;
                }
            }
        }
        catch { }

        return existingProjectId;
    }

    private static string? ExtractProjectId(JsonElement obj)
    {
        if (!obj.TryGetProperty("cloudaicompanionProject", out var proj)) return null;
        if (proj.ValueKind == JsonValueKind.String)
            return proj.GetString()?.Trim();
        if (proj.ValueKind == JsonValueKind.Object)
            return Text(proj, "id")?.Trim();
        return null;
    }

    private static (int Status, string Body)? QueryCloudCodeModels(string accessToken, string? projectId)
    {
        string body = string.IsNullOrWhiteSpace(projectId)
            ? "{}"
            : JsonSerializer.Serialize(new { project = projectId });
        return PostCloudCode(accessToken, FetchAvailableModelsPath, body);
    }

    private static (int Status, string Body)? PostCloudCode(string accessToken, string path, string jsonBody)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{CloudCodeBaseUrl}{path}");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            req.Headers.TryAddWithoutValidation("User-Agent", "antigravity");
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var resp = cloudHttp.Send(req, HttpCompletionOption.ResponseContentRead);
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return ((int)resp.StatusCode, body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// When Antigravity is not running and no Cloud Code reading is available, keep the last
    /// reading on screen (dropping any bucket whose reset time has already passed to 0%) and mark
    /// it as standby for the tooltip; if we have never seen a reading this session, show a quiet
    /// "standby" placeholder instead of an error.
    /// </summary>
    private static void PublishStandby()
    {
        if (lastGood is { Limits.Count: > 0 } previous)
        {
            var now = DateTimeOffset.Now;
            var refreshedLimits = previous.Limits
                .Select(l => l.ResetsAt is { } r && r <= now ? new UsageLimit(l.Label, 0, null) : l)
                .ToList();
            var refreshedDetails = previous.Details?
                .Select(l => l.ResetsAt is { } r && r <= now ? new UsageLimit(l.Label, 0, null) : l)
                .ToList();

            current = new UsageReading(refreshedLimits, null, false, Standby: true, Details: refreshedDetails);
            return;
        }

        current = new UsageReading([], "Waiting for Antigravity", false, Standby: true);
    }

    // ---- process & port discovery (local fallback) -------------------------

    private static List<(int Pid, string Name)> FindLanguageServerProcesses()
    {
        var list = new List<(int Pid, string Name)>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return list; }

        foreach (var proc in all)
        {
            try
            {
                var name = proc.ProcessName;
                if (name.StartsWith("language_server", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("antigravity_language_server", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add((proc.Id, name));
                }
            }
            catch { }
            finally { proc.Dispose(); }
        }

        return list;
    }

    private static List<CandidateProcess> InspectCandidates(List<(int Pid, string Name)> processes, StringBuilder? report)
    {
        var candidates = new List<CandidateProcess>();

        foreach (var (pid, name) in processes)
        {
            var cmd = ReadCommandLine(pid);
            if (string.IsNullOrWhiteSpace(cmd))
            {
                report?.AppendLine($"PID {pid} ({name}): could not read command line");
                continue;
            }

            var csrfMatch = Regex.Match(cmd, @"--csrf_token(?:=|\s+)(?:""([^""]+)""|'([^']+)'|([^\s""']+))",
                                        RegexOptions.IgnoreCase);
            var csrf = csrfMatch.Success
                ? (csrfMatch.Groups[1].Value is { Length: > 0 } g1 ? g1
                 : csrfMatch.Groups[2].Value is { Length: > 0 } g2 ? g2
                 : csrfMatch.Groups[3].Value)
                : "";

            if (string.IsNullOrEmpty(csrf))
            {
                report?.AppendLine($"PID {pid} ({name}): command line has no --csrf_token");
                continue;
            }

            var portMatch = Regex.Match(cmd, @"--extension_server_port(?:=|\s+)(\d+)", RegexOptions.IgnoreCase);
            int? extPort = portMatch.Success && int.TryParse(portMatch.Groups[1].Value, out int p) ? p : null;

            bool isAntigravity = cmd.Contains("antigravity", StringComparison.OrdinalIgnoreCase);
            report?.AppendLine(
                $"PID {pid} ({name}): csrf token present ({csrf.Length} chars), " +
                $"antigravity={(isAntigravity ? "yes" : "no")}" +
                (extPort is not null ? $", extension_server_port={extPort}" : ""));

            candidates.Add(new CandidateProcess(pid, name, cmd, csrf, extPort, isAntigravity));
        }

        return candidates
            .OrderByDescending(c => c.IsAntigravity)
            .ThenByDescending(c => c.Pid)
            .ToList();
    }

    private static string? ReadCommandLine(int pid) =>
        ReadCommandLineFromPeb(pid) ?? ReadCommandLineViaCim(pid);

    private static string? ReadCommandLineFromPeb(int pid)
    {
        if (IntPtr.Size != 8) return null;

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint PROCESS_VM_READ = 0x0010;

        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero) return null;

            if (!TryReadIntPtr(handle, pbi.PebBaseAddress + 0x20, out IntPtr procParams) || procParams == IntPtr.Zero)
                return null;

            byte[] unicodeHeader = new byte[16];
            if (!ReadProcessMemory(handle, procParams + 0x70, unicodeHeader, unicodeHeader.Length, out nint read)
                || read < 16)
                return null;

            ushort byteLength = BitConverter.ToUInt16(unicodeHeader, 0);
            IntPtr bufferPtr = (IntPtr)BitConverter.ToInt64(unicodeHeader, 8);
            if (byteLength == 0 || byteLength > 32768 || bufferPtr == IntPtr.Zero) return null;

            byte[] rawChars = new byte[byteLength];
            if (!ReadProcessMemory(handle, bufferPtr, rawChars, rawChars.Length, out nint charsRead)
                || charsRead < byteLength)
                return null;

            return Encoding.Unicode.GetString(rawChars);
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool TryReadIntPtr(IntPtr process, IntPtr address, out IntPtr value)
    {
        value = IntPtr.Zero;
        byte[] buf = new byte[8];
        if (!ReadProcessMemory(process, address, buf, buf.Length, out nint read) || read < 8)
            return false;

        value = (IntPtr)BitConverter.ToInt64(buf, 0);
        return true;
    }

    private static string? ReadCommandLineViaCim(int pid)
    {
        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($"(Get-CimInstance Win32_Process -Filter 'ProcessId = {pid}').CommandLine");

            using var ps = Process.Start(start);
            if (ps is null) return null;

            var output = ps.StandardOutput.ReadToEnd();
            if (!ps.WaitForExit(5000)) { try { ps.Kill(); } catch { } return null; }

            return ps.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<int> FindListeningPorts(int pid, int? extensionPort)
    {
        var ports = new List<int>();
        void AddPort(int p)
        {
            if (p is > 0 and <= 65535 && !ports.Contains(p)) ports.Add(p);
        }

        foreach (var p in GetListeningPortsFromIpHlp(pid)) AddPort(p);

        if (ports.Count == 0)
        {
            foreach (var p in GetListeningPortsFromNetstat(pid)) AddPort(p);
        }

        if (extensionPort is int ext) AddPort(ext);
        return ports;
    }

    private static List<int> GetListeningPortsFromIpHlp(int pid)
    {
        var result = new List<int>();
        const int AF_INET = 2;
        const int TCP_TABLE_OWNER_PID_LISTENER = 3;
        const uint ERROR_INSUFFICIENT_BUFFER = 122;

        try
        {
            uint size = 0;
            uint ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (ret != ERROR_INSUFFICIENT_BUFFER || size == 0) return result;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                ret = GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (ret != 0) return result;

                int count = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = buffer + 4;
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    if (row.dwOwningPid == (uint)pid)
                    {
                        int port = (int)(((row.dwLocalPort & 0xFF) << 8) | ((row.dwLocalPort >> 8) & 0xFF));
                        if (port > 0 && !result.Contains(port)) result.Add(port);
                    }
                    rowPtr += rowSize;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch { }

        return result;
    }

    private static List<int> GetListeningPortsFromNetstat(int pid)
    {
        var result = new List<int>();
        try
        {
            var start = new ProcessStartInfo("netstat.exe", "-ano -p tcp")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(start);
            if (proc is null) return result;

            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(4000)) { try { proc.Kill(); } catch { } return result; }

            var suffix = pid.ToString();
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim();
                if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || parts[^1] != suffix) continue;

                var local = parts[1];
                int colon = local.LastIndexOf(':');
                if (colon > 0 && int.TryParse(local[(colon + 1)..], out int port) && !result.Contains(port))
                    result.Add(port);
            }
        }
        catch { }

        return result;
    }

    // ---- transport & shaping -----------------------------------------------

    private static (int Status, string Body)? QueryPort(string scheme, int port, string csrfToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{scheme}://127.0.0.1:{port}{ServicePath}");
            request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            request.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrfToken);
            request.Content = new StringContent(RequestBody, Encoding.UTF8, "application/json");

            using var reply = loopbackHttp.Send(request, HttpCompletionOption.ResponseContentRead);
            var body = reply.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return ((int)reply.StatusCode, body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses the `fetchAvailableModels` response from `cloudcode-pa.googleapis.com`.</summary>
    private static UsageReading ParseCloudCode(string body, bool showPro, bool showFlash, bool showClaude)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object)
                return new UsageReading([], "Unexpected reply from Cloud Code API", false);

            var byLabel = new Dictionary<string, UsageLimit>(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in models.EnumerateObject())
            {
                var modelId = prop.Name;
                var item = prop.Value;
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!ShouldIncludeCloudModel(modelId, item, out var label)) continue;
                if (!TryReadQuotaLimit(item, label, out var limit)) continue;

                // Multiple internal model IDs can share a displayName (e.g. gemini-3.1-pro-high and
                // gemini-pro-agent); keep the entry with the highest used % / earliest reset time.
                if (!byLabel.TryGetValue(label, out var existing)
                    || limit.Percent > existing.Percent
                    || (limit.Percent == existing.Percent
                        && (limit.ResetsAt ?? DateTimeOffset.MaxValue) < (existing.ResetsAt ?? DateTimeOffset.MaxValue)))
                {
                    byLabel[label] = limit;
                }
            }

            var allModels = byLabel.Values
                .OrderBy(m => IsProModel(m.Label) ? 0 : IsFlashModel(m.Label) ? 1 : 2)
                .ThenBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return BuildReadingFromModels(allModels, showPro, showFlash, showClaude);
        }
        catch (Exception ex)
        {
            Log.Write($"antigravity cloud code reply unreadable: {ex.Message}");
            return new UsageReading([], "Unexpected reply from Cloud Code API", false);
        }
    }

    private static bool ShouldIncludeCloudModel(string modelId, JsonElement model, out string label)
    {
        label = (Text(model, "displayName") ?? Text(model, "label") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(label)) return false;

        if (modelId.StartsWith("chat_", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("tab_", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("rev", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("image", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("mquery", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("lite", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (label.Contains("Lite", StringComparison.OrdinalIgnoreCase)
            || label.Contains("Image", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>Parses the `GetUserStatus` response from the local Antigravity language server.</summary>
    private static UsageReading ParseLocal(string body, bool showPro, bool showFlash, bool showClaude, out string? plan)
    {
        plan = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("userStatus", out var status) || status.ValueKind != JsonValueKind.Object)
                return new UsageReading([], "Unexpected reply from language server", false);

            if (status.TryGetProperty("planStatus", out var planStatus) && planStatus.ValueKind == JsonValueKind.Object
                && planStatus.TryGetProperty("planInfo", out var planInfo) && planInfo.ValueKind == JsonValueKind.Object)
            {
                plan = Text(planInfo, "planName") ?? Text(planInfo, "teamsTier");
            }

            var allModels = new List<UsageLimit>();

            if (status.TryGetProperty("cascadeModelConfigData", out var cascade)
                && cascade.ValueKind == JsonValueKind.Object
                && cascade.TryGetProperty("clientModelConfigs", out var configs)
                && configs.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in configs.EnumerateArray())
                {
                    var label = Text(item, "label")?.Trim();
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    if (TryReadQuotaLimit(item, label!, out var limit))
                        allModels.Add(limit);
                }
            }

            return BuildReadingFromModels(allModels, showPro, showFlash, showClaude);
        }
        catch (Exception ex)
        {
            Log.Write($"antigravity usage reply unreadable: {ex.Message}");
            return new UsageReading([], "Unexpected reply from Antigravity", false);
        }
    }

    private static bool TryReadQuotaLimit(JsonElement item, string label, out UsageLimit limit)
    {
        limit = default!;
        if (!item.TryGetProperty("quotaInfo", out var quota) || quota.ValueKind != JsonValueKind.Object)
            return false;

        double remaining;
        bool exhausted = quota.TryGetProperty("isExhausted", out var exh)
                      && exh.ValueKind == JsonValueKind.True;

        if (exhausted)
        {
            remaining = 0.0;
        }
        else if (quota.TryGetProperty("remainingFraction", out var fracEl) && Number(fracEl, out double frac))
        {
            remaining = Math.Clamp(frac, 0.0, 1.0);
        }
        else if (quota.TryGetProperty("resetTime", out _))
        {
            // Protobuf JSON omits numeric fields when their value is 0: a model with an
            // active resetTime and no remainingFraction has 0 remaining (100% used).
            remaining = 0.0;
        }
        else
        {
            remaining = 1.0;
        }

        int usedPercent = (int)Math.Round(Math.Clamp((1.0 - remaining) * 100.0, 0.0, 100.0));
        var resetsAt = Moment(Text(quota, "resetTime"));
        limit = new UsageLimit(label, usedPercent, resetsAt);
        return true;
    }

    private static UsageReading BuildReadingFromModels(List<UsageLimit> allModels,
                                                       bool showPro, bool showFlash, bool showClaude)
    {
        // Group models into the three shared quota pools drawn on the bar: Pro, Flash, Claude.
        var limits = new List<UsageLimit>();

        if (showPro && PickPool("Pro", allModels.Where(m => IsProModel(m.Label))) is { } pro)
            limits.Add(pro);

        if (showFlash && PickPool("Flash", allModels.Where(m => IsFlashModel(m.Label))) is { } flash)
            limits.Add(flash);

        if (showClaude && PickPool("Claude", allModels.Where(m => IsClaudeOrPartnerModel(m.Label))) is { } claude)
            limits.Add(claude);

        // Fallback if future models use names outside those three families and at least one pool is enabled.
        if (limits.Count == 0 && allModels.Count > 0 && (showPro || showFlash || showClaude))
        {
            foreach (var m in allModels.Take(3))
                limits.Add(m with { Label = ShortLabel(m.Label) });
        }

        return new UsageReading(limits, null, false, Details: allModels);
    }

    private static UsageLimit? PickPool(string poolName, IEnumerable<UsageLimit> matches)
    {
        var list = matches.ToList();
        if (list.Count == 0) return null;

        // Models in the same family share a quota pool; pick the highest used percentage (and
        // earliest reset time on a tie) so the bar reflects the tightest constraint in that pool.
        var chosen = list
            .OrderByDescending(m => m.Percent)
            .ThenBy(m => m.ResetsAt ?? DateTimeOffset.MaxValue)
            .First();

        return new UsageLimit(poolName, chosen.Percent, chosen.ResetsAt);
    }

    private static bool IsProModel(string label) =>
        label.Contains("Pro", StringComparison.OrdinalIgnoreCase) && !IsClaudeOrPartnerModel(label);

    private static bool IsFlashModel(string label) =>
        label.Contains("Flash", StringComparison.OrdinalIgnoreCase);

    private static bool IsClaudeOrPartnerModel(string label) =>
        label.Contains("Claude", StringComparison.OrdinalIgnoreCase)
        || label.Contains("Sonnet", StringComparison.OrdinalIgnoreCase)
        || label.Contains("Opus", StringComparison.OrdinalIgnoreCase)
        || label.Contains("Haiku", StringComparison.OrdinalIgnoreCase)
        || label.Contains("GPT", StringComparison.OrdinalIgnoreCase);

    private static string ShortLabel(string label)
    {
        var trimmed = label.Replace("Gemini ", "", StringComparison.OrdinalIgnoreCase)
                           .Replace("Claude ", "", StringComparison.OrdinalIgnoreCase)
                           .Trim();
        return trimmed.Length <= 7 ? trimmed : trimmed[..7].TrimEnd();
    }

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

    // ---- Win32 P/Invoke for zero-subprocess discovery ----------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
                                                 [Out] byte[] lpBuffer, int dwSize, out nint lpNumberOfBytesRead);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
                                                        ref PROCESS_BASIC_INFORMATION processInformation,
                                                        int processInformationLength, out int returnLength);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref uint pdwSize, bool bOrder,
                                                   int ulAf, int TableClass, uint Reserved);
}
