using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScweenSpit;

/// <summary>One limit window claude.ai reports, as a percentage and the moment it resets.</summary>
public sealed record UsageLimit(string Label, int Percent, DateTimeOffset? ResetsAt);

/// <summary>
/// The result of the last poll. An empty <see cref="Limits"/> with no <see cref="Error"/> means we
/// have not managed a first read yet, which the strip draws as a placeholder rather than as zero.
/// </summary>
public sealed record UsageReading(IReadOnlyList<UsageLimit> Limits, string? Error, bool NeedsKey);

/// <summary>
/// Reads claude.ai usage limits for the strip the bar draws.
///
/// Derived from claude-usage-widget by Niccolò Sabato (MIT licence — the full notice is in
/// THIRD-PARTY-NOTICES.md). What is taken from it is the protocol knowledge, which is the part that
/// cannot be worked out from the outside: which endpoints carry the figures, the browser-shaped
/// headers the edge expects, how an organisation is chosen when an account has several, that the
/// weekly per-model limit moved out of `seven_day_sonnet` into the `limits` list, and that the
/// session cookie rotates mid-session and has to be written back or the key quietly expires.
///
/// The code is ours: the original is Python/tkinter and draws its own window, whereas this feeds a
/// segment of a bar that is already painting. Two deliberate departures from upstream: the key is
/// kept under DPAPI rather than in plain config (see <see cref="Secret"/>), and it is passed to the
/// curl fallback on stdin rather than in argv, where any process on the machine can read it.
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
    public const string UsagePage = "https://claude.ai/settings/usage";

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

    /// <summary>The latest reading, or null when usage tracking is off or has never run.</summary>
    public static UsageReading? Current => current;

    /// <summary>True when the feature is switched on and has a key worth sending.</summary>
    public static bool Enabled => enabled;

    /// <summary>True if a value looks like a session key rather than a cleared or bogus cookie.</summary>
    public static bool Plausible(string? key) =>
        key is not null && key.StartsWith(KeyPrefix, StringComparison.Ordinal) && key.Length >= KeyMinLength;

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
            enabled = config.Enabled && !string.IsNullOrWhiteSpace(config.SessionKey);

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
        }
    }

    /// <summary>Asks the loop to poll now instead of waiting out the interval.</summary>
    public static void Refresh()
    {
        // The flag as well as the signal: a request arriving while Poll is already running would
        // otherwise be lost, and saving a key would appear to do nothing for three minutes.
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
                enabled = false;
                current = null;
                persist?.Invoke();
                return true;
            }

            if (!Plausible(key)) return false;

            var stored = Secret.Protect(key);
            if (stored is null) return false;      // unencryptable: keep nothing rather than plaintext

            settings.SessionKey = stored;
            settings.OrgId = null;                 // a different key may be a different account
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
                // The loop must outlive any single failure: a bar that stops updating looks broken
                // in a way that a bar showing a stale figure does not.
                Log.Write($"claude usage poll failed: {ex.Message}");
                current = new UsageReading([], "Could not read usage", false);
            }

            int seconds;
            lock (gate) seconds = Math.Clamp(settings?.RefreshSeconds ?? 180, 30, 3600);

            var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            nudge = wake;
            if (wakeWanted) continue;              // asked for while the poll above was running

            try { await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(seconds), token), wake.Task); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static void Poll()
    {
        string? key;
        string? org;
        bool weekly, model;

        lock (gate)
        {
            if (settings is not { Enabled: true }) return;
            key = Secret.Unprotect(settings.SessionKey);
            org = settings.OrgId;
            weekly = settings.ShowWeekly;
            model = settings.ShowModel;
        }

        if (!Plausible(key))
        {
            // Nothing worth sending. Say so rather than spending a request every interval on a 401
            // whose answer is already known.
            current = new UsageReading([], "Session key needed", true);
            return;
        }

        if (string.IsNullOrWhiteSpace(org))
        {
            org = ResolveOrg(key!);
            if (org is null) return;               // ResolveOrg has already published the reason

            lock (gate)
            {
                if (settings is not null) { settings.OrgId = org; persist?.Invoke(); }
            }
        }

        var reply = Get($"{Origin}/api/organizations/{org}/usage", key!);
        if (reply is null) { current = new UsageReading([], "claude.ai unreachable", false); return; }

        AdoptRotatedKey(reply, key!);

        if (reply.Status is 401 or 403)
        {
            current = new UsageReading([], "Session key expired", true);
            return;
        }

        if (reply.Status >= 400)
        {
            // A 404 here usually means the stored org is stale — drop it so the next poll re-resolves.
            if (reply.Status == 404) lock (gate) { if (settings is not null) settings.OrgId = null; }
            current = new UsageReading([], $"claude.ai returned {reply.Status}", false);
            return;
        }

        current = Parse(reply.Body, weekly, model);
    }

    /// <summary>
    /// Works out which organisation's usage to read.
    ///
    /// Only orgs exposing 'chat' have usage worth reading — a Console-only org would leave every bar
    /// at zero — and when several qualify, the one claude.ai itself last routed to is the one whose
    /// figures match what the website shows.
    /// </summary>
    private static string? ResolveOrg(string key)
    {
        var listing = Get($"{Origin}/api/organizations", key);
        if (listing is null) { current = new UsageReading([], "claude.ai unreachable", false); return null; }

        if (listing.Status is 401 or 403)
        {
            current = new UsageReading([], "Session key expired", true);
            return null;
        }

        AdoptRotatedKey(listing, key);

        try
        {
            using var doc = JsonDocument.Parse(listing.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                current = new UsageReading([], "No organisation found", false);
                return null;
            }

            // The browser's own last-active org wins outright when we can get it.
            var active = ActiveOrg(key);
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

    /// <summary>The org claude.ai currently routes the account to, or null if it will not say.</summary>
    private static string? ActiveOrg(string key)
    {
        try
        {
            var boot = Get($"{Origin}/api/bootstrap", key);
            if (boot is null || boot.Status >= 400) return null;

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

            if (Window(root, "five_hour", "utilization") is { } session)
                limits.Add(session with { Label = "Session" });

            if (weekly && Window(root, "seven_day", "utilization") is { } week)
                limits.Add(week with { Label = "Weekly" });

            if (model && ScopedModel(root) is { } scoped)
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
    private static UsageLimit? Window(JsonElement root, string name, string field)
    {
        if (!root.TryGetProperty(name, out var bucket) || bucket.ValueKind != JsonValueKind.Object)
            return null;

        if (!bucket.TryGetProperty(field, out var used) || !Number(used, out double percent))
            return null;

        return new UsageLimit(name, Clamp(percent), Moment(Text(bucket, "resets_at")));
    }

    /// <summary>
    /// The weekly per-model limit. claude.ai moved this out of `seven_day_sonnet` and into the
    /// `limits` list, whose entry names the model it is scoped to — so read that when it is there
    /// and the bar follows whichever model the limit applies to, rather than naming a fixed one.
    /// </summary>
    private static UsageLimit? ScopedModel(JsonElement root)
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

                return new UsageLimit(name!, Clamp(percent), Moment(Text(limit, "resets_at")));
            }
        }

        return Window(root, "seven_day_sonnet", "utilization") is { } legacy
            ? legacy with { Label = "Sonnet" }
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

        return left.TotalHours >= 48 ? $"resets in {(int)left.TotalDays}d {left.Hours}h"
             : left.TotalHours >= 1  ? $"resets in {(int)left.TotalHours}h {left.Minutes:00}m"
             : $"resets in {Math.Max(1, (int)left.TotalMinutes)}m";
    }

    // ---- transport ---------------------------------------------------------

    private sealed record Response(int Status, string Headers, string Body);

    /// <summary>
    /// An authenticated GET, or null if the request could not be made at all.
    ///
    /// .NET's TLS goes through SChannel on Windows, the same stack the browsers use, so the
    /// handshake normally passes the edge's fingerprint check. Normally — the cipher ordering is not
    /// identical, so a 403 with no body is treated as "challenged, not refused" and retried through
    /// curl.exe, which has shipped with Windows since 1803 and is what upstream uses throughout.
    /// </summary>
    private static Response? Get(string url, string key)
    {
        var cookie = $"sessionKey={key}";
        var direct = ViaHttpClient(url, cookie);

        if (direct is { Status: 403 } && direct.Body.Length < 4096 && !direct.Body.TrimStart().StartsWith('{'))
        {
            Log.WriteOnce("claude-tls", "claude.ai challenged the direct request; falling back to curl");
            return ViaCurl(url, cookie) ?? direct;
        }

        return direct ?? ViaCurl(url, cookie);
    }

    private static Response? ViaHttpClient(string url, string cookie)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
            request.Headers.TryAddWithoutValidation("anthropic-client-platform", "web_claude_ai");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
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
    private static Response? ViaCurl(string url, string cookie)
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
                .AppendLine("-H \"Accept: application/json\"")
                .AppendLine($"-H \"Cookie: {Escape(cookie)}\"")
                .ToString();

            curl.StandardInput.Write(config);
            curl.StandardInput.Close();

            var output = curl.StandardOutput.ReadToEnd();
            var error = curl.StandardError.ReadToEnd();
            if (!curl.WaitForExit(30_000)) { try { curl.Kill(); } catch { } return null; }

            if (curl.ExitCode != 0)
            {
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
    /// Writes back a session key claude.ai rotated mid-session. Skipping this is invisible for
    /// weeks and then shows up as a key that expired for no reason.
    ///
    /// Only a real Set-Cookie line counts, and only a value that still looks like a key: the same
    /// cookie is how the server ends a session, and taking `sessionKey=""` literally would destroy
    /// the working credential.
    /// </summary>
    private static void AdoptRotatedKey(Response reply, string currentKey)
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
