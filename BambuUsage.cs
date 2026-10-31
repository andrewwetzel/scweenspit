using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ScweenSpit;

public enum BambuPrinterState
{
    Offline,
    Idle,
    Preparing,
    Printing,
    Paused,
    Finished,
    Error,
}

public enum BambuSignInStep
{
    SignedIn,
    NeedsEmailCode,
    NeedsTfaCode,
    Failed,
}

/// <summary>Live status of one Bambu Lab printer in the user's configured closet order.</summary>
public sealed record BambuPrinterReading(
    string Id,
    string Label,
    string FullName,
    string? Model,
    BambuPrinterState State,
    int Percent,
    int RemainingMinutes,
    string? JobName,
    int CurrentLayer,
    int TotalLayers,
    double? NozzleTemp,
    double? BedTemp,
    string? ErrorText,
    bool Online);

/// <summary>The combined reading for all enabled Bambu Lab printers.</summary>
public sealed record BambuReading(
    IReadOnlyList<BambuPrinterReading> Printers,
    string? Error,
    bool NeedsAuth,
    bool Transient = false,
    bool MqttConnected = false)
{
    public bool Settled => !Transient;
}

/// <summary>
/// Authenticates with Bambu Lab Cloud (`api.bambulab.com`), discovers bound printers, and streams
/// real-time telemetry over Bambu's Cloud MQTT broker (`us.mqtt.bambulab.com:8883` / `cn.mqtt.bambulab.com:8883`)
/// using a lightweight native MQTT v3.1.1 over TLS client so ScweenSpit needs no external libraries.
/// </summary>
public static class BambuUsage
{
    private const string GlobalApiBase = "https://api.bambulab.com";
    private const string ChinaApiBase = "https://api.bambulab.cn";
    private const string GlobalWebBase = "https://bambulab.com";
    private const string ChinaWebBase = "https://bambulab.cn";
    private const string GlobalMqttHost = "us.mqtt.bambulab.com";
    private const string ChinaMqttHost = "cn.mqtt.bambulab.com";
    private const int MqttPort = 8883;
    private const int MqttPingIntervalSeconds = 25;
    private const int MqttReadTimeoutSeconds = 45;
    private const int MqttWriteTimeoutSeconds = 15;

    // If a printer whose MQTT state is Printing or Preparing delivers no message on its own topic
    // for 120 s, mark it owed a pushall (subject to the 5-minute-per-printer limit). Dead connections
    // are caught by the 45 s read timeout against the 25 s keep-alive ping.
    private const int ActivePrintSilentSeconds = 120;

    // P1/A1 firmware lags when pushall is spammed; limit pushall to once per 5 min per printer
    // once initial state is known.
    private const long PushAllMinIntervalMs = 5 * 60 * 1000;
    private const long TokenRefreshMinIntervalMs = 2 * 60 * 1000;

    // Print error code sent transiently by Bambu firmware when a user manually cancels a job.
    private const long UserCancelledPrintError = 50348044;

    private static readonly HttpClient http = new(new HttpClientHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Mutable live state per printer (`dev_id`), merged across HTTP bind polls and incremental
    /// MQTT delta packets. Bambu printers only broadcast changed fields after the initial `pushall`,
    /// so every field must be retained until explicitly replaced.
    /// </summary>
    private sealed class PrinterLiveState
    {
        public string Id { get; init; } = "";
        public string Name { get; set; } = "";
        public string? Model { get; set; }
        public bool Online { get; set; } = true;
        public string GcodeState { get; set; } = "";
        public string CloudPrintStatus { get; set; } = "";
        public int Percent { get; set; }
        public int RemainingMinutes { get; set; }
        public string? JobName { get; set; }
        public int CurrentLayer { get; set; }
        public int TotalLayers { get; set; }
        public double? NozzleTemp { get; set; }
        public double? BedTemp { get; set; }
        public long PrintError { get; set; }
        public int PreparePercent { get; set; } = -1;
        public int Stage { get; set; } = -1;
        public string? HmsErrorSummary { get; set; }
        public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.MinValue;
    }

    private static readonly object gate = new();
    private static readonly Dictionary<string, PrinterLiveState> liveBySerial = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> lastPushAllBySerial = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> owedPushAllSerials = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> boundSerials = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> subscribedSerials = new(StringComparer.OrdinalIgnoreCase);

    private static BambuSettings? settings;
    private static Action? persist;
    private static CancellationTokenSource? life;
    private static CancellationTokenSource? mqttReconnect;
    private static volatile bool reconnectWanted;
    private static bool wokeBackoffThisOutage;
    private static volatile BambuReading? current;
    private static volatile TaskCompletionSource? nudge;
    private static volatile bool wakeWanted;
    private static volatile bool enabled;
    private static volatile bool mqttConnected;
    private static DateTimeOffset? lastMqttMessageAt;
    private static long lastTokenRefreshTick = -TokenRefreshMinIntervalMs;
    private static long pushSequenceId;
    private static string? lastError;
    private static bool lastNeedsAuth;
    private static bool hasCompletedInitialSync;
    private static bool hasKnownBoundList;

    // Pending two-step login state (for email verification code or authenticator 2FA).
    private static string? pendingEmail;
    private static string? pendingPassword;
    private static string? pendingRegion;
    private static string? pendingTfaKey;
    private static BambuSignInStep pendingStep = BambuSignInStep.Failed;

    /// <summary>The latest reading across all enabled Bambu printers.</summary>
    public static BambuReading? Current => current;

    /// <summary>True when Bambu printer tracking is switched on.</summary>
    public static bool Enabled => enabled;

    /// <summary>True when a Bambu Cloud access token is stored.</summary>
    public static bool HasSignedInAccount
    {
        get
        {
            lock (gate) return !string.IsNullOrWhiteSpace(settings?.AccessToken);
        }
    }

    /// <summary>Email of the signed-in Bambu Cloud account, if known.</summary>
    public static string? SignedInEmail
    {
        get
        {
            lock (gate) return !string.IsNullOrWhiteSpace(settings?.AccessToken) ? settings?.AccountEmail : null;
        }
    }

    /// <summary>True when a sign-in attempt is waiting for an email verification code or 2FA code.</summary>
    public static BambuSignInStep PendingSignInStep
    {
        get
        {
            lock (gate) return pendingStep;
        }
    }

    /// <summary>Number of enabled printers configured for display on the bar.</summary>
    public static int ConfiguredPrinterCount
    {
        get
        {
            lock (gate)
            {
                if (settings is null) return 0;
                return settings.Printers.Count(p => p.Enabled);
            }
        }
    }

    /// <summary>Starts, stops, or updates the Bambu Cloud poller and MQTT stream.</summary>
    public static void Configure(BambuSettings config, Action? save = null)
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
                mqttReconnect?.Cancel();
                mqttReconnect = null;
                subscribedSerials.Clear();
                mqttConnected = false;
                current = null;
                hasCompletedInitialSync = false;
                hasKnownBoundList = false;
                return;
            }

            RebuildCurrentReadingLocked();

            if (life is not null) return;

            life = new CancellationTokenSource();
            var token = life.Token;
            _ = Task.Run(() => PollLoopAsync(token), token);
            _ = Task.Run(() => MqttLoopAsync(token), token);

            ListenPowerEvents();
        }
    }

    /// <summary>Wakes the cloud sync and nudges the MQTT session.</summary>
    public static void Refresh()
    {
        wakeWanted = true;
        nudge?.TrySetResult();
        lock (gate)
        {
            if (!mqttConnected && !wokeBackoffThisOutage && mqttReconnect is not null)
            {
                wokeBackoffThisOutage = true;
                mqttReconnect.Cancel();
            }
        }
    }

    /// <summary>Rebuilds the reading immediately when printers are reordered, renamed, or toggled in Settings.</summary>
    public static void NotifyPrintersEdited()
    {
        lock (gate)
        {
            RebuildCurrentReadingLocked();
            if (!subscribedSerials.SetEquals(GetTargetSerialsLocked()))
            {
                reconnectWanted = true;
                mqttReconnect?.Cancel();
            }
        }
    }

    private static List<string> GetTargetSerialsLocked() =>
        settings?.Printers
            .Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Id) && (!hasKnownBoundList || boundSerials.Contains(p.Id)))
            .Select(p => p.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

    /// <summary>
    /// Raises an already-open Bambu Studio (or OrcaSlicer) window to the foreground, or launches
    /// the slicer application if it is not yet running. Returns true when a slicer was raised or started.
    /// </summary>
    public static bool OpenSlicer()
    {
        try
        {
            var openWindows = WindowList.Enumerate(null);
            var runningBambu = openWindows.FirstOrDefault(w =>
                w.Process.Equals("bambu-studio", StringComparison.OrdinalIgnoreCase) ||
                w.Process.Equals("bambustudio", StringComparison.OrdinalIgnoreCase));
            if (runningBambu is not null)
            {
                WindowList.Raise(runningBambu.Handle);
                return true;
            }

            if (FindSlicerPath(bambuOnly: true) is { } bambuExe)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(bambuExe) { UseShellExecute = true });
                return true;
            }

            var runningOrca = openWindows.FirstOrDefault(w =>
                w.Process.Equals("orca-slicer", StringComparison.OrdinalIgnoreCase) ||
                w.Process.Equals("orcaslicer", StringComparison.OrdinalIgnoreCase));
            if (runningOrca is not null)
            {
                WindowList.Raise(runningOrca.Handle);
                return true;
            }

            if (FindSlicerPath(bambuOnly: false) is { } fallbackExe)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fallbackExe) { UseShellExecute = true });
                return true;
            }

            // Protocol handler registered by Bambu Studio on install.
            using var proto = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("bambustudio");
            if (proto is not null)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("bambustudio://") { UseShellExecute = true });
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"could not open Bambu slicer: {ex.Message}");
        }
        return false;
    }

    private static string? FindSlicerPath(bool bambuOnly)
    {
        var candidates = new List<string>();

        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string commonStart = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        string userStart = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        string commonDesk = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        string userDesk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        if (bambuOnly)
        {
            candidates.Add(Path.Combine(pf, "Bambu Studio", "bambu-studio.exe"));
            candidates.Add(Path.Combine(pf86, "Bambu Studio", "bambu-studio.exe"));
            candidates.Add(Path.Combine(local, "Programs", "Bambu Studio", "bambu-studio.exe"));
            candidates.Add(Path.Combine(local, "Bambu Studio", "bambu-studio.exe"));

            if (ReadRegistryExe(@"bambustudio\shell\open\command") is { } regProto) candidates.Add(regProto);
            if (ReadRegistryValue(Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\bambu-studio.exe") is { } appPathLm) candidates.Add(appPathLm);
            if (ReadRegistryValue(Microsoft.Win32.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\bambu-studio.exe") is { } appPathCu) candidates.Add(appPathCu);

            candidates.Add(Path.Combine(commonStart, "Bambu Studio", "Bambu Studio.lnk"));
            candidates.Add(Path.Combine(userStart, "Bambu Studio", "Bambu Studio.lnk"));
            candidates.Add(Path.Combine(commonDesk, "Bambu Studio.lnk"));
            candidates.Add(Path.Combine(userDesk, "Bambu Studio.lnk"));
        }
        else
        {
            candidates.Add(Path.Combine(pf, "OrcaSlicer", "orca-slicer.exe"));
            candidates.Add(Path.Combine(pf86, "OrcaSlicer", "orca-slicer.exe"));
            candidates.Add(Path.Combine(local, "Programs", "OrcaSlicer", "orca-slicer.exe"));
            candidates.Add(Path.Combine(local, "OrcaSlicer", "orca-slicer.exe"));

            if (ReadRegistryExe(@"orcaslicer\shell\open\command") is { } orcaProto) candidates.Add(orcaProto);
            candidates.Add(Path.Combine(commonStart, "OrcaSlicer", "OrcaSlicer.lnk"));
            candidates.Add(Path.Combine(userStart, "OrcaSlicer", "OrcaSlicer.lnk"));
        }

        foreach (var path in candidates)
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return path;

        return null;
    }

    private static string? ReadRegistryValue(Microsoft.Win32.RegistryKey root, string subKey)
    {
        try
        {
            using var k = root.OpenSubKey(subKey);
            return (k?.GetValue(null) as string)?.Trim('"', ' ');
        }
        catch { return null; }
    }

    private static string? ReadRegistryExe(string classesSubKey)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(classesSubKey);
            if (k?.GetValue(null) is not string raw || string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Trim();
            if (raw.StartsWith('"'))
            {
                int end = raw.IndexOf('"', 1);
                if (end > 1) return raw[1..end];
            }
            int exeIdx = raw.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exeIdx > 0 ? raw[..(exeIdx + 4)].Trim('"', ' ') : null;
        }
        catch { return null; }
    }

    /// <summary>Stops background tasks on exit.</summary>
    public static void Stop()
    {
        lock (gate)
        {
            life?.Cancel();
            life = null;
            mqttReconnect?.Cancel();
            mqttReconnect = null;
            mqttConnected = false;
            enabled = false;
        }

        if (!listeningPower) return;
        listeningPower = false;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerChanged;
    }

    private static bool listeningPower;

    private static void ListenPowerEvents()
    {
        if (listeningPower) return;
        listeningPower = true;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerChanged;
    }

    private static void OnNetworkChanged(object? sender, EventArgs e)
    {
        lock (gate) { reconnectWanted = true; mqttReconnect?.Cancel(); }
        Refresh();
    }

    private static void OnPowerChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        lock (gate) { reconnectWanted = true; mqttReconnect?.Cancel(); }
        Refresh();
    }

    // ---- Cloud Authentication ----------------------------------------------

    private static string ApiBase(string? region) =>
        string.Equals(region, "China", StringComparison.OrdinalIgnoreCase) ? ChinaApiBase : GlobalApiBase;

    private static string WebBase(string? region) =>
        string.Equals(region, "China", StringComparison.OrdinalIgnoreCase) ? ChinaWebBase : GlobalWebBase;

    private static string MqttHost(string? region) =>
        string.Equals(region, "China", StringComparison.OrdinalIgnoreCase) ? ChinaMqttHost : GlobalMqttHost;

    private static HttpRequestMessage CreateBambuRequest(HttpMethod method, string url, string? bearerToken = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "bambu_network_agent/01.09.05.01");
        req.Headers.TryAddWithoutValidation("X-BBL-Client-Name", "OrcaSlicer");
        req.Headers.TryAddWithoutValidation("X-BBL-Client-Type", "slicer");
        req.Headers.TryAddWithoutValidation("X-BBL-Client-Version", "01.09.05.51");
        req.Headers.TryAddWithoutValidation("X-BBL-Language", "en-US");
        req.Headers.TryAddWithoutValidation("X-BBL-OS-Type", "windows");
        req.Headers.TryAddWithoutValidation("X-BBL-OS-Version", "10.0.22631");
        req.Headers.TryAddWithoutValidation("X-BBL-Agent-Version", "01.09.05.01");
        req.Headers.TryAddWithoutValidation("X-BBL-Executable-info", "{}");
        req.Headers.TryAddWithoutValidation("X-BBL-Agent-OS-Type", "windows");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!string.IsNullOrWhiteSpace(bearerToken))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearerToken}");
        return req;
    }

    /// <summary>
    /// Starts signing into Bambu Lab Cloud with email and password. If the account requires an
    /// email verification code (`verifyCode`) or authenticator 2FA (`tfa`), returns that step so
    /// the Settings page can prompt for the 6-digit code.
    /// </summary>
    public static async Task<(BambuSignInStep Step, string Message)> SignInAsync(
        string region, string email, string password)
    {
        email = email.Trim();
        region = string.Equals(region, "China", StringComparison.OrdinalIgnoreCase) ? "China" : "Global";

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return (BambuSignInStep.Failed, "Enter your Bambu Lab email and password.");

        try
        {
            string baseUrl = ApiBase(region);
            using var req = CreateBambuRequest(HttpMethod.Post, $"{baseUrl}/v1/user-service/user/login");
            var payload = JsonSerializer.Serialize(new { account = email, password });
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                // If password login is challenged by CSRF on the web edge, fall back to email code login.
                if ((int)resp.StatusCode == 403 && body.Contains("missing_cookie", StringComparison.OrdinalIgnoreCase))
                {
                    await RequestEmailCodeInternalAsync(region, email);
                    lock (gate)
                    {
                        pendingEmail = email;
                        pendingPassword = password;
                        pendingRegion = region;
                        pendingTfaKey = null;
                        pendingStep = BambuSignInStep.NeedsEmailCode;
                    }
                    return (BambuSignInStep.NeedsEmailCode,
                        $"Sent a 6-digit verification code to {email}. Enter it below.");
                }

                string apiErr = TryReadErrorMessage(body) ?? $"HTTP {(int)resp.StatusCode}";
                return (BambuSignInStep.Failed, $"Sign-in failed: {apiErr}");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? accessToken = Text(root, "accessToken");
            string? refreshToken = Text(root, "refreshToken");
            string? loginType = Text(root, "loginType");
            string? tfaKey = Text(root, "tfaKey");

            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                return await FinalizeSignInAsync(region, email, accessToken, refreshToken);
            }

            if (string.Equals(loginType, "verifyCode", StringComparison.OrdinalIgnoreCase))
            {
                await RequestEmailCodeInternalAsync(region, email);
                lock (gate)
                {
                    pendingEmail = email;
                    pendingPassword = password;
                    pendingRegion = region;
                    pendingTfaKey = null;
                    pendingStep = BambuSignInStep.NeedsEmailCode;
                }
                return (BambuSignInStep.NeedsEmailCode,
                    $"Bambu Lab sent a 6-digit verification code to {email}. Enter it below.");
            }

            if (string.Equals(loginType, "tfa", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(tfaKey))
            {
                lock (gate)
                {
                    pendingEmail = email;
                    pendingPassword = password;
                    pendingRegion = region;
                    pendingTfaKey = tfaKey;
                    pendingStep = BambuSignInStep.NeedsTfaCode;
                }
                return (BambuSignInStep.NeedsTfaCode,
                    "Enter the 6-digit code from your authenticator app below.");
            }

            string detail = TryReadErrorMessage(body) ?? (loginType is not null ? $"loginType={loginType}" : "unexpected response");
            return (BambuSignInStep.Failed, $"Sign-in did not return a token ({detail}).");
        }
        catch (Exception ex)
        {
            Log.Write($"bambu sign-in failed: {ex.Message}");
            return (BambuSignInStep.Failed, $"Sign-in failed: {ex.Message}");
        }
    }

    /// <summary>Requests a fresh 6-digit email login code for the given email address.</summary>
    public static async Task<(bool Ok, string Message)> RequestEmailCodeAsync(string region, string email)
    {
        email = email.Trim();
        region = string.Equals(region, "China", StringComparison.OrdinalIgnoreCase) ? "China" : "Global";
        if (string.IsNullOrWhiteSpace(email))
            return (false, "Enter your Bambu Lab email address first.");

        try
        {
            await RequestEmailCodeInternalAsync(region, email);
            lock (gate)
            {
                pendingEmail = email;
                pendingRegion = region;
                pendingTfaKey = null;
                pendingStep = BambuSignInStep.NeedsEmailCode;
            }
            return (true, $"Sent a 6-digit verification code to {email}.");
        }
        catch (Exception ex)
        {
            return (false, $"Could not request verification code: {ex.Message}");
        }
    }

    private static async Task RequestEmailCodeInternalAsync(string region, string email)
    {
        string baseUrl = ApiBase(region);
        using var req = CreateBambuRequest(HttpMethod.Post, $"{baseUrl}/v1/user-service/user/sendemail/code");
        var payload = JsonSerializer.Serialize(new { email, type = "codeLogin" });
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req);
        _ = await resp.Content.ReadAsStringAsync();
    }

    /// <summary>Completes sign-in using the 6-digit email verification code or 2FA code.</summary>
    public static async Task<(BambuSignInStep Step, string Message)> CompleteVerificationAsync(
        string code, string? emailOverride = null, string? regionOverride = null)
    {
        code = code.Trim().Replace(" ", "");
        if (code.Length == 0)
            return (BambuSignInStep.Failed, "Enter the 6-digit verification code.");

        string email;
        string? password;
        string region;
        string? tfaKey;
        BambuSignInStep step;

        lock (gate)
        {
            email = !string.IsNullOrWhiteSpace(emailOverride) ? emailOverride.Trim() : (pendingEmail ?? settings?.AccountEmail ?? "");
            password = pendingPassword;
            region = !string.IsNullOrWhiteSpace(regionOverride) ? regionOverride : (pendingRegion ?? settings?.Region ?? "Global");
            tfaKey = pendingTfaKey;
            step = pendingStep;
        }

        if (string.IsNullOrWhiteSpace(email))
            return (BambuSignInStep.Failed, "Enter your Bambu Lab email address above first.");

        try
        {
            if (step == BambuSignInStep.NeedsTfaCode && !string.IsNullOrWhiteSpace(tfaKey))
            {
                var (tfaToken, tfaError) = await ExchangeTfaCodeAsync(region, tfaKey, code);
                if (!string.IsNullOrWhiteSpace(tfaToken))
                    return await FinalizeSignInAsync(region, email, tfaToken, refreshToken: null);
                return (BambuSignInStep.NeedsTfaCode, tfaError ?? "2FA verification failed.");
            }

            // Standard email verification code login
            string baseUrl = ApiBase(region);
            using var req = CreateBambuRequest(HttpMethod.Post, $"{baseUrl}/v1/user-service/user/login");
            var payload = !string.IsNullOrWhiteSpace(password)
                ? JsonSerializer.Serialize(new { account = email, password, code })
                : JsonSerializer.Serialize(new { account = email, code });
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                string err = TryReadErrorMessage(body) ?? $"HTTP {(int)resp.StatusCode}";
                return (BambuSignInStep.NeedsEmailCode, $"Verification failed: {err}");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? accessToken = Text(root, "accessToken");
            string? refreshToken = Text(root, "refreshToken");

            if (!string.IsNullOrWhiteSpace(accessToken))
                return await FinalizeSignInAsync(region, email, accessToken, refreshToken);

            // Sometimes login with password+code needs just {account, code}
            if (!string.IsNullOrWhiteSpace(password))
            {
                using var req2 = CreateBambuRequest(HttpMethod.Post, $"{baseUrl}/v1/user-service/user/login");
                req2.Content = new StringContent(JsonSerializer.Serialize(new { account = email, code }), Encoding.UTF8, "application/json");
                using var resp2 = await http.SendAsync(req2);
                var body2 = await resp2.Content.ReadAsStringAsync();
                if (resp2.IsSuccessStatusCode)
                {
                    using var doc2 = JsonDocument.Parse(body2);
                    accessToken = Text(doc2.RootElement, "accessToken");
                    refreshToken = Text(doc2.RootElement, "refreshToken");
                    if (!string.IsNullOrWhiteSpace(accessToken))
                        return await FinalizeSignInAsync(region, email, accessToken, refreshToken);
                }
            }

            return (BambuSignInStep.NeedsEmailCode, "Code was not accepted — check the 6-digit code or request a new one.");
        }
        catch (Exception ex)
        {
            Log.Write($"bambu verification failed: {ex.Message}");
            return (step == BambuSignInStep.NeedsTfaCode ? BambuSignInStep.NeedsTfaCode : BambuSignInStep.NeedsEmailCode,
                $"Verification failed: {ex.Message}");
        }
    }

    private static async Task<(string? Token, string? Error)> ExchangeTfaCodeAsync(string region, string tfaKey, string code)
    {
        try
        {
            string webBase = WebBase(region);
            using var csrfReq = CreateBambuRequest(HttpMethod.Get, $"{webBase}/api/csrf");
            using var csrfResp = await http.SendAsync(csrfReq);

            string? csrfToken = ExtractCookie(csrfResp, "bbl_csrf_token");
            using var tfaReq = CreateBambuRequest(HttpMethod.Post, $"{webBase}/api/sign-in/tfa");
            if (!string.IsNullOrWhiteSpace(csrfToken))
            {
                tfaReq.Headers.TryAddWithoutValidation("x-bbl-csrf-token", csrfToken);
                tfaReq.Headers.TryAddWithoutValidation("Cookie", $"bbl_csrf_token={csrfToken}");
            }
            tfaReq.Content = new StringContent(
                JsonSerializer.Serialize(new { tfaKey, tfaCode = code }),
                Encoding.UTF8, "application/json");

            using var tfaResp = await http.SendAsync(tfaReq);
            var body = await tfaResp.Content.ReadAsStringAsync();

            string? cookieToken = ExtractCookie(tfaResp, "token");
            if (!string.IsNullOrWhiteSpace(cookieToken))
                return (cookieToken, null);

            if (tfaResp.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                string? accessToken = Text(doc.RootElement, "accessToken") ?? Text(doc.RootElement, "token");
                if (!string.IsNullOrWhiteSpace(accessToken))
                    return (accessToken, null);
            }

            return (null, TryReadErrorMessage(body) ?? $"2FA failed (HTTP {(int)tfaResp.StatusCode})");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static async Task<(BambuSignInStep Step, string Message)> FinalizeSignInAsync(
        string region, string email, string accessToken, string? refreshToken)
    {
        string? protectedAccess = Secret.Protect(accessToken);
        if (protectedAccess is null)
            return (BambuSignInStep.Failed, "Could not encrypt access token with Windows DPAPI.");

        string? mqttUser = TryExtractUsernameFromJwt(accessToken)
                           ?? await FetchUsernameFromPreferenceAsync(region, accessToken);

        if (string.IsNullOrWhiteSpace(mqttUser))
            return (BambuSignInStep.Failed, "Signed in, but could not resolve your Bambu user ID for MQTT.");

        Action? save;
        lock (gate)
        {
            if (settings is null) return (BambuSignInStep.Failed, "Bambu settings are not initialised.");
            settings.Region = region;
            settings.AccountEmail = email;
            settings.MqttUsername = mqttUser;
            settings.AccessToken = protectedAccess;
            settings.RefreshToken = !string.IsNullOrWhiteSpace(refreshToken) ? Secret.Protect(refreshToken) : null;

            pendingPassword = null;
            pendingTfaKey = null;
            pendingStep = BambuSignInStep.SignedIn;
            lastError = null;
            lastNeedsAuth = false;
            save = persist;
        }

        // Pull the bound printers immediately so the user can see and reorder them right away.
        var (ok, count, syncErr) = await SyncBoundDevicesAsync(CancellationToken.None);
        save?.Invoke();

        // Restart the MQTT stream with the new credentials.
        lock (gate)
        {
            lastTokenRefreshTick = -TokenRefreshMinIntervalMs;
            reconnectWanted = true;
            mqttReconnect?.Cancel();
        }
        Refresh();

        return ok
            ? (BambuSignInStep.SignedIn, $"Signed in as {email} — found {count} printer{(count == 1 ? "" : "s")}.")
            : (BambuSignInStep.SignedIn, $"Signed in as {email} ({syncErr ?? "syncing printers…"}).");
    }

    /// <summary>Signs out of Bambu Cloud and clears stored tokens.</summary>
    public static void SignOut()
    {
        Action? save;
        lock (gate)
        {
            pendingPassword = null;
            pendingTfaKey = null;
            pendingStep = BambuSignInStep.Failed;
            if (settings is not null)
            {
                settings.AccessToken = null;
                settings.RefreshToken = null;
                settings.MqttUsername = null;
            }
            liveBySerial.Clear();
            lastPushAllBySerial.Clear();
            owedPushAllSerials.Clear();
            boundSerials.Clear();
            subscribedSerials.Clear();
            mqttConnected = false;
            lastMqttMessageAt = null;
            lastError = "Sign in to Bambu Cloud";
            lastNeedsAuth = true;
            hasCompletedInitialSync = false;
            hasKnownBoundList = false;
            RebuildCurrentReadingLocked();
            mqttReconnect?.Cancel();
            save = persist;
        }

        save?.Invoke();
        Refresh();
    }

    private static string? TryExtractUsernameFromJwt(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) return null;
            string payloadB64 = parts[1].Replace('-', '+').Replace('_', '/');
            payloadB64 += new string('=', (4 - payloadB64.Length % 4) % 4);
            var jsonBytes = Convert.FromBase64String(payloadB64);
            using var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;

            if (Text(root, "username") is { Length: > 0 } u)
                return u.StartsWith("u_", StringComparison.Ordinal) ? u : $"u_{u}";

            if (Text(root, "uid") is { Length: > 0 } uid)
                return uid.StartsWith("u_", StringComparison.Ordinal) ? uid : $"u_{uid}";

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> FetchUsernameFromPreferenceAsync(string region, string accessToken)
    {
        try
        {
            using var req = CreateBambuRequest(HttpMethod.Get, $"{ApiBase(region)}/v1/design-user-service/my/preference", accessToken);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? uid = Text(root, "uid");
            if (uid is null && root.TryGetProperty("uid", out var uidEl) && uidEl.ValueKind == JsonValueKind.Number)
                uid = uidEl.GetRawText();

            return !string.IsNullOrWhiteSpace(uid)
                ? (uid.StartsWith("u_", StringComparison.Ordinal) ? uid : $"u_{uid}")
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> TryRefreshTokenAsync(CancellationToken ct)
    {
        string? refreshPlain;
        string region;
        long nowTick = Environment.TickCount64;
        lock (gate)
        {
            if (settings is null || nowTick - lastTokenRefreshTick < TokenRefreshMinIntervalMs)
                return false;
            lastTokenRefreshTick = nowTick;
            refreshPlain = Secret.Unprotect(settings.RefreshToken);
            region = settings.Region;
        }

        if (string.IsNullOrWhiteSpace(refreshPlain)) return false;

        try
        {
            using var req = CreateBambuRequest(HttpMethod.Post, $"{ApiBase(region)}/v1/user-service/user/refreshtoken");
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { refreshToken = refreshPlain }),
                Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return false;

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            string? newAccess = Text(doc.RootElement, "accessToken");
            string? newRefresh = Text(doc.RootElement, "refreshToken");
            if (string.IsNullOrWhiteSpace(newAccess)) return false;

            string? protAccess = Secret.Protect(newAccess);
            if (protAccess is null) return false;

            Action? save;
            lock (gate)
            {
                if (settings is null) return false;
                settings.AccessToken = protAccess;
                if (!string.IsNullOrWhiteSpace(newRefresh) && Secret.Protect(newRefresh) is { } protRefresh)
                    settings.RefreshToken = protRefresh;
                if (TryExtractUsernameFromJwt(newAccess) is { } u)
                    settings.MqttUsername = u;
                save = persist;
            }

            save?.Invoke();
            lock (gate) mqttReconnect?.Cancel();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"bambu token refresh failed: {ex.Message}");
            return false;
        }
    }

    // ---- Cloud Device Discovery & Sync -------------------------------------

    private static async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SyncBoundDevicesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Write($"bambu poll error: {ex.Message}");
            }

            int seconds = 60;
            lock (gate)
            {
                if (settings is not null)
                    seconds = Math.Clamp(settings.RefreshSeconds, 15, 3600);
                if (!hasCompletedInitialSync || lastError is not null)
                    seconds = Math.Min(seconds, 10);
            }

            var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            nudge = wait;
            if (wakeWanted)
            {
                wakeWanted = false;
                continue;
            }

            try
            {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delayTask = Task.Delay(TimeSpan.FromSeconds(seconds), delayCts.Token);
                await Task.WhenAny(delayTask, wait.Task);
                delayCts.Cancel();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            finally
            {
                wakeWanted = false;
            }
        }
    }

    /// <summary>
    /// Queries `/v1/iot-service/api/user/bind` to discover bound printers and sync their online
    /// status, preserving the user's custom closet order in <see cref="BambuSettings.Printers"/>.
    /// </summary>
    public static async Task<(bool Ok, int Count, string? Error)> SyncBoundDevicesAsync(CancellationToken ct)
    {
        string? accessToken;
        string region;
        lock (gate)
        {
            if (settings is null) return (false, 0, "Not configured");
            accessToken = Secret.Unprotect(settings.AccessToken);
            region = settings.Region;
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            lock (gate)
            {
                lastError = "Sign in to Bambu Cloud";
                lastNeedsAuth = true;
                RebuildCurrentReadingLocked();
            }
            return (false, 0, "Not signed in");
        }

        try
        {
            string baseUrl = ApiBase(region);
            using var req = CreateBambuRequest(HttpMethod.Get, $"{baseUrl}/v1/iot-service/api/user/bind", accessToken);
            using var resp = await http.SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (await TryRefreshTokenAsync(ct))
                {
                    lock (gate) accessToken = Secret.Unprotect(settings?.AccessToken);
                    if (!string.IsNullOrWhiteSpace(accessToken))
                    {
                        using var retryReq = CreateBambuRequest(HttpMethod.Get, $"{baseUrl}/v1/iot-service/api/user/bind", accessToken);
                        using var retryResp = await http.SendAsync(retryReq, ct);
                        return await ProcessBindResponseAsync(retryResp, region, accessToken, ct);
                    }
                }

                lock (gate)
                {
                    lastError = "Bambu Cloud session expired — sign in again";
                    lastNeedsAuth = true;
                    hasCompletedInitialSync = true;
                    RebuildCurrentReadingLocked();
                }
                return (false, 0, "Session expired");
            }

            return await ProcessBindResponseAsync(resp, region, accessToken, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                lastError = ex.Message;
                lastNeedsAuth = false;
                RebuildCurrentReadingLocked();
            }
            return (false, 0, ex.Message);
        }
    }

    private static async Task<(bool Ok, int Count, string? Error)> ProcessBindResponseAsync(
        HttpResponseMessage resp, string region, string accessToken, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            string err = TryReadErrorMessage(body) ?? $"HTTP {(int)resp.StatusCode}";
            lock (gate)
            {
                lastError = err;
                lastNeedsAuth = false;
                hasCompletedInitialSync = true;
                RebuildCurrentReadingLocked();
            }
            return (false, 0, err);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var discovered = new List<(string Id, string Name, string? Model, bool Online, string PrintStatus)>();

        if (root.TryGetProperty("devices", out var devicesEl) && devicesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var dev in devicesEl.EnumerateArray())
            {
                string? id = Text(dev, "dev_id");
                if (string.IsNullOrWhiteSpace(id)) continue;

                string name = Text(dev, "name") ?? id;
                string? model = Text(dev, "dev_product_name") ?? Text(dev, "dev_model_name");
                bool online = !dev.TryGetProperty("online", out var onEl)
                              || onEl.ValueKind != JsonValueKind.False;
                string printStatus = Text(dev, "print_status") ?? "";
                discovered.Add((id, name, model, online, printStatus));
            }
        }

        // Also fetch the latest cloud tasks once in a while so job titles and finished/failed states
        // are known even before a printer pushes a full MQTT dump.
        var latestTasks = await FetchLatestTasksByDeviceAsync(region, accessToken, ct);

        bool configChanged = false;
        Action? save = null;

        lock (gate)
        {
            if (settings is null) return (false, 0, "Not configured");

            var now = DateTimeOffset.UtcNow;
            boundSerials.Clear();

            foreach (var d in discovered)
            {
                boundSerials.Add(d.Id);
                var existingCfg = settings.Printers.FirstOrDefault(
                    p => string.Equals(p.Id, d.Id, StringComparison.OrdinalIgnoreCase));

                if (existingCfg is null)
                {
                    settings.Printers.Add(new BambuPrinterConfig
                    {
                        Id = d.Id,
                        Name = d.Name,
                        Model = d.Model,
                        Enabled = true,
                    });
                    configChanged = true;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(d.Name) && existingCfg.Name != d.Name)
                    {
                        existingCfg.Name = d.Name;
                        configChanged = true;
                    }
                    if (!string.IsNullOrWhiteSpace(d.Model) && existingCfg.Model != d.Model)
                    {
                        existingCfg.Model = d.Model;
                        configChanged = true;
                    }
                }

                if (!liveBySerial.TryGetValue(d.Id, out var live))
                {
                    live = new PrinterLiveState { Id = d.Id };
                    liveBySerial[d.Id] = live;
                }

                live.Name = d.Name;
                live.Model = d.Model;
                live.Online = d.Online || (mqttConnected && (now - live.LastUpdated) < TimeSpan.FromSeconds(60));
                live.CloudPrintStatus = d.PrintStatus;

                if (latestTasks.TryGetValue(d.Id, out var taskTitle) && string.IsNullOrWhiteSpace(live.JobName))
                    live.JobName = taskTitle;
            }

            hasKnownBoundList = true;
            if (configChanged) save = persist;
            if (!subscribedSerials.SetEquals(GetTargetSerialsLocked()))
            {
                reconnectWanted = true;
                mqttReconnect?.Cancel();
            }

            lastError = null;
            lastNeedsAuth = false;
            hasCompletedInitialSync = true;
            RebuildCurrentReadingLocked();
        }

        save?.Invoke();
        return (true, discovered.Count, null);
    }

    private static async Task<Dictionary<string, string>> FetchLatestTasksByDeviceAsync(
        string region, string accessToken, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var req = CreateBambuRequest(HttpMethod.Get, $"{ApiBase(region)}/v1/user-service/my/tasks?limit=25", accessToken);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return result;

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var hit in hits.EnumerateArray())
            {
                string? devId = Text(hit, "deviceId");
                string? title = Text(hit, "title");
                if (!string.IsNullOrWhiteSpace(devId) && !string.IsNullOrWhiteSpace(title) && !result.ContainsKey(devId))
                    result[devId] = CleanJobName(title);
            }
        }
        catch
        {
            // Optional enrichment only.
        }
        return result;
    }

    // ---- Native MQTT v3.1.1 over TLS Client --------------------------------

    private static async Task MqttLoopAsync(CancellationToken ct)
    {
        int backoffSeconds = 2;

        while (!ct.IsCancellationRequested)
        {
            string? accessToken;
            string? mqttUser;
            string region;
            List<string> serials;

            lock (gate)
            {
                reconnectWanted = false;
                accessToken = Secret.Unprotect(settings?.AccessToken);
                mqttUser = settings?.MqttUsername;
                region = settings?.Region ?? "Global";
                serials = GetTargetSerialsLocked();
                subscribedSerials.Clear();
                foreach (var s in serials) subscribedSerials.Add(s);
            }

            if (!string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(mqttUser))
            {
                mqttUser = TryExtractUsernameFromJwt(accessToken)
                           ?? await FetchUsernameFromPreferenceAsync(region, accessToken);
                if (!string.IsNullOrWhiteSpace(mqttUser))
                {
                    Action? save;
                    lock (gate)
                    {
                        if (settings is not null) settings.MqttUsername = mqttUser;
                        save = persist;
                    }
                    save?.Invoke();
                }
            }

            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(mqttUser) || serials.Count == 0)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            long startedAt = Environment.TickCount64;

            try
            {
                await RunMqttSessionAsync(
                    MqttHost(region), mqttUser, accessToken, serials, sessionCts);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception) when (sessionCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Intentional reconnect (credentials updated, power resume, newly bound printer, etc.)
                backoffSeconds = 2;
                continue;
            }
            catch (Exception ex)
            {
                Log.Write($"bambu mqtt disconnected: {ex.Message}");
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(mqttReconnect, sessionCts))
                        mqttReconnect = null;
                    mqttConnected = false;
                    RebuildCurrentReadingLocked();
                }
            }

            bool wasEstablished = Environment.TickCount64 - startedAt > 30_000;
            if (wasEstablished)
                backoffSeconds = 2;

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (gate)
            {
                mqttReconnect = delayCts;
                if (reconnectWanted)
                {
                    reconnectWanted = false;
                    delayCts.Cancel();
                }
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(wasEstablished ? 1 : backoffSeconds), delayCts.Token);
                if (!wasEstablished)
                    backoffSeconds = Math.Min(30, backoffSeconds * 2);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                backoffSeconds = 2;
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(mqttReconnect, delayCts))
                        mqttReconnect = null;
                }
            }
        }
    }

    private static async Task RunMqttSessionAsync(
        string host, string username, string password, IReadOnlyList<string> serials,
        CancellationTokenSource sessionCts)
    {
        var ct = sessionCts.Token;
        using var tcp = new TcpClient();
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(12));
            await tcp.ConnectAsync(host, MqttPort, connectCts.Token);
        }

        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        using (var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            authCts.CancelAfter(TimeSpan.FromSeconds(12));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
            }, authCts.Token);
        }

        using var writeLock = new SemaphoreSlim(1, 1);

        // 1. Send MQTT v3.1.1 CONNECT packet
        string clientId = $"scweenspit-{Guid.NewGuid():N}"[..23];
        byte[] connectPacket = BuildMqttConnectPacket(clientId, username, password, keepAliveSeconds: 60);
        await WritePacketAsync(ssl, writeLock, connectPacket, ct);

        // 2. Expect CONNACK (packet type 2)
        var (connType, _, connPayload) = await ReadMqttPacketAsync(ssl, timeoutSeconds: 15, ct);
        if (connType != 2 || connPayload.Length < 2)
            throw new IOException("Invalid MQTT CONNACK from Bambu broker.");

        byte returnCode = connPayload[1];
        if (returnCode != 0)
        {
            if (returnCode is 4 or 5)
            {
                // Bad username/password or not authorized — try refreshing token (rate-limited).
                _ = await TryRefreshTokenAsync(ct);
            }
            throw new IOException($"MQTT broker rejected connection (code {returnCode}).");
        }

        lock (gate)
        {
            mqttReconnect = sessionCts;
            if (reconnectWanted)
            {
                reconnectWanted = false;
                sessionCts.Cancel();
            }
            wokeBackoffThisOutage = false;
            mqttConnected = true;
            lastError = null;
            lastNeedsAuth = false;
            RebuildCurrentReadingLocked();
        }

        // 3. Subscribe to `device/{serial}/report` for each enabled bound printer; `pushall` is sent
        //    once SUBACK confirms the subscription so the state dump cannot race ahead of the broker.
        var pendingSubAcks = new Dictionary<ushort, string>();
        var awaitingInitialPushAll = new HashSet<string>(serials, StringComparer.OrdinalIgnoreCase);
        var activeSerials = new HashSet<string>(serials, StringComparer.OrdinalIgnoreCase);
        ushort packetId = 1;
        foreach (var serial in serials)
        {
            ushort id = packetId++;
            lock (pendingSubAcks) pendingSubAcks[id] = serial;
            byte[] subPacket = BuildMqttSubscribePacket(id, $"device/{serial}/report");
            await WritePacketAsync(ssl, writeLock, subPacket, ct);
        }

        // 4. Start background keep-alive PINGREQ & owed-pushall task
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sessionToken = pingCts.Token;
        Exception? pingError = null;

        var pingTask = Task.Run(async () =>
        {
            byte[] pingReq = [0xC0, 0x00];
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), sessionToken);
                List<string>? unacked = null;
                lock (pendingSubAcks)
                {
                    if (awaitingInitialPushAll.Count > 0)
                    {
                        unacked = awaitingInitialPushAll.ToList();
                        awaitingInitialPushAll.Clear();
                    }
                }
                if (unacked is not null)
                {
                    foreach (var s in unacked)
                        await TrySendPushAllAsync(ssl, writeLock, s, sessionToken);
                }

                int nextDelay = MqttPingIntervalSeconds - 5;
                while (!sessionToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(nextDelay), sessionToken);
                    nextDelay = MqttPingIntervalSeconds;

                    var now = DateTimeOffset.UtcNow;
                    long nowTick = Environment.TickCount64;
                    List<string>? readyPushAlls = null;
                    lock (gate)
                    {
                        foreach (var s in activeSerials)
                        {
                            if (liveBySerial.TryGetValue(s, out var l) && l.Online
                                && l.GcodeState is "PREPARE" or "RUNNING" or "ACTIVE" or "PRINTING"
                                && (now - l.LastUpdated).TotalSeconds >= ActivePrintSilentSeconds)
                            {
                                owedPushAllSerials.Add(s);
                            }
                            if (owedPushAllSerials.Contains(s)
                                && (!lastPushAllBySerial.TryGetValue(s, out long lastTick) || nowTick - lastTick >= PushAllMinIntervalMs))
                            {
                                (readyPushAlls ??= []).Add(s);
                            }
                        }
                    }

                    if (readyPushAlls is not null)
                    {
                        foreach (var s in readyPushAlls)
                            await TrySendPushAllAsync(ssl, writeLock, s, sessionToken);
                    }

                    await WritePacketAsync(ssl, writeLock, pingReq, sessionToken);
                }
            }
            catch (OperationCanceledException) when (sessionToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                pingError = ex;
                pingCts.Cancel();
            }
        }, sessionToken);

        try
        {
            // 5. Read incoming MQTT packets continuously with a 45 s read timeout
            while (!sessionToken.IsCancellationRequested)
            {
                var (packetType, flags, payload) = await ReadMqttPacketAsync(ssl, MqttReadTimeoutSeconds, sessionToken);
                if (packetType == 9) // SUBACK
                {
                    if (payload.Length < 3)
                        throw new IOException("Malformed MQTT SUBACK from Bambu broker.");
                    ushort ackId = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0, 2));
                    string? subSerial;
                    bool shouldSendPushAll = false;
                    lock (pendingSubAcks)
                    {
                        if (pendingSubAcks.Remove(ackId, out subSerial) && subSerial is not null)
                            shouldSendPushAll = awaitingInitialPushAll.Remove(subSerial);
                    }
                    if (payload[2] == 0x80)
                    {
                        if (subSerial is not null)
                        {
                            lock (gate)
                            {
                                activeSerials.Remove(subSerial);
                                owedPushAllSerials.Remove(subSerial);
                            }
                        }
                        Log.WriteOnce($"bambu-sub-{subSerial ?? ackId.ToString()}",
                            $"bambu mqtt subscription rejected (0x80) for {subSerial ?? $"packet {ackId}"}");
                    }
                    else if (shouldSendPushAll && subSerial is not null)
                    {
                        await TrySendPushAllAsync(ssl, writeLock, subSerial, sessionToken);
                    }
                }
                else if (packetType == 3) // PUBLISH
                {
                    await HandleMqttPublishAsync(ssl, writeLock, flags, payload, sessionToken);
                }
            }
        }
        finally
        {
            pingCts.Cancel();
            try { await pingTask; } catch { }
            if (pingError is not null && !ct.IsCancellationRequested)
                throw pingError;
        }
    }

    private static async Task TrySendPushAllAsync(
        Stream stream, SemaphoreSlim writeLock, string serial, CancellationToken ct)
    {
        long seq;
        lock (gate)
        {
            long nowTick = Environment.TickCount64;
            bool hasMqttState = liveBySerial.TryGetValue(serial, out var live) && !string.IsNullOrWhiteSpace(live.GcodeState);
            if (hasMqttState && lastPushAllBySerial.TryGetValue(serial, out long lastTick) && nowTick - lastTick < PushAllMinIntervalMs)
            {
                owedPushAllSerials.Add(serial);
                return;
            }
            owedPushAllSerials.Remove(serial);
            lastPushAllBySerial[serial] = nowTick;
            seq = ++pushSequenceId;
        }

        string pushAllJson = $"{{\"pushing\":{{\"sequence_id\":\"{seq}\",\"command\":\"pushall\",\"version\":1,\"push_target\":1}}}}";
        byte[] pubPacket = BuildMqttPublishPacket($"device/{serial}/request", pushAllJson);
        await WritePacketAsync(stream, writeLock, pubPacket, ct);
    }

    private static async Task HandleMqttPublishAsync(
        Stream stream, SemaphoreSlim writeLock, byte flags, byte[] payload, CancellationToken ct)
    {
        if (payload.Length < 2) return;
        int topicLen = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0, 2));
        if (payload.Length < 2 + topicLen) return;

        string topic = Encoding.UTF8.GetString(payload, 2, topicLen);
        int offset = 2 + topicLen;

        int qos = (flags >> 1) & 0x03;
        if (qos > 0)
        {
            if (payload.Length < offset + 2) return;
            ushort packetId = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
            offset += 2;

            if (qos == 1)
            {
                byte[] pubAck = [0x40, 0x02, (byte)(packetId >> 8), (byte)(packetId & 0xFF)];
                await WritePacketAsync(stream, writeLock, pubAck, ct);
            }
        }

        if (offset >= payload.Length) return;

        // Topic format: device/{serial}/report
        string? serial = TryExtractSerialFromTopic(topic);
        if (serial is null) return;

        try
        {
            using var doc = JsonDocument.Parse(payload.AsMemory(offset, payload.Length - offset));
            var root = doc.RootElement;

            lock (gate)
            {
                var now = DateTimeOffset.UtcNow;
                lastMqttMessageAt = now;

                if (!liveBySerial.TryGetValue(serial, out var live))
                {
                    var cfg = settings?.Printers.FirstOrDefault(
                        p => string.Equals(p.Id, serial, StringComparison.OrdinalIgnoreCase));
                    live = new PrinterLiveState
                    {
                        Id = serial,
                        Name = cfg?.Name ?? serial,
                        Model = cfg?.Model,
                    };
                    liveBySerial[serial] = live;
                }

                if (root.TryGetProperty("event", out var eventObj) && eventObj.ValueKind == JsonValueKind.Object)
                {
                    string? ev = Text(eventObj, "event");
                    if (string.Equals(ev, "client.disconnected", StringComparison.OrdinalIgnoreCase))
                        live.Online = false;
                    else if (string.Equals(ev, "client.connected", StringComparison.OrdinalIgnoreCase))
                        live.Online = true;
                }

                if (root.TryGetProperty("print", out var printObj) && printObj.ValueKind == JsonValueKind.Object)
                {
                    live.Online = true;
                    live.LastUpdated = now;
                    MergePrintTelemetry(live, printObj);
                }

                RebuildCurrentReadingLocked();
            }
        }
        catch
        {
            // Ignore malformed telemetry frames.
        }
    }

    private static void MergePrintTelemetry(PrinterLiveState live, JsonElement print)
    {
        if (Text(print, "gcode_state") is { Length: > 0 } state)
        {
            string next = state.ToUpperInvariant();
            if (next != live.GcodeState && (next == "PREPARE"
                || (next is "RUNNING" or "IDLE" && live.GcodeState is "FINISH" or "SUCCESS" or "IDLE" or "FAILED")))
            {
                live.Percent = 0;
                live.RemainingMinutes = 0;
                live.CurrentLayer = 0;
                live.TotalLayers = 0;
            }
            live.GcodeState = next;
        }

        if (TryGetInt(print, "mc_percent", out int pct))
            live.Percent = Math.Clamp(pct, 0, 100);

        if (TryGetInt(print, "mc_remaining_time", out int rem))
            live.RemainingMinutes = Math.Max(0, rem);

        if (Text(print, "subtask_name") is { Length: > 0 } subtask)
            live.JobName = CleanJobName(subtask);
        else if (Text(print, "gcode_file") is { Length: > 0 } gfile && string.IsNullOrWhiteSpace(live.JobName))
            live.JobName = CleanJobName(gfile);

        if (TryGetInt(print, "layer_num", out int curLayer))
            live.CurrentLayer = Math.Max(0, curLayer);

        if (TryGetInt(print, "total_layer_num", out int totLayer))
            live.TotalLayers = Math.Max(0, totLayer);

        if (TryGetDouble(print, "nozzle_temper", out double nozzle))
            live.NozzleTemp = nozzle;

        if (TryGetDouble(print, "bed_temper", out double bed))
            live.BedTemp = bed;

        if (TryGetLong(print, "print_error", out long printErr))
            live.PrintError = printErr;

        if (TryGetInt(print, "gcode_file_prepare_percent", out int prepPct))
            live.PreparePercent = prepPct;

        if (TryGetInt(print, "stg_cur", out int stage))
            live.Stage = stage;

        if (print.TryGetProperty("hms", out var hmsEl) && hmsEl.ValueKind == JsonValueKind.Array)
        {
            string? seriousCode = null;
            foreach (var item in hmsEl.EnumerateArray())
            {
                if (TryGetLong(item, "attr", out long attr) && TryGetLong(item, "code", out long code) && code != 0)
                {
                    // Severity in Bambu HMS is encoded in (code >> 16): 1 = fatal, 2 = serious error.
                    long severity = (code >> 16) & 0xFFFF;
                    if (severity is 1 or 2)
                    {
                        seriousCode = $"HMS {(attr >> 16) & 0xFFFF:X4}_{attr & 0xFFFF:X4}:{(code >> 16) & 0xFFFF:X4}_{code & 0xFFFF:X4}";
                        break;
                    }
                }
            }
            live.HmsErrorSummary = seriousCode;
        }
    }

    private static void RebuildCurrentReadingLocked()
    {
        if (settings is null || !settings.Enabled)
        {
            current = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            current = new BambuReading([], "Sign in to Bambu Cloud", NeedsAuth: true, MqttConnected: false);
            return;
        }

        var enabledPrinters = settings.Printers.Where(p => p.Enabled).ToList();
        var readings = new List<BambuPrinterReading>(enabledPrinters.Count);

        foreach (var cfg in enabledPrinters)
        {
            liveBySerial.TryGetValue(cfg.Id, out var live);
            readings.Add(BuildPrinterReading(cfg, live));
        }

        current = new BambuReading(
            readings,
            lastError,
            NeedsAuth: lastNeedsAuth,
            Transient: !hasCompletedInitialSync && readings.Count == 0,
            MqttConnected: mqttConnected);
    }

    private static BambuPrinterReading BuildPrinterReading(BambuPrinterConfig cfg, PrinterLiveState? live)
    {
        string label = cfg.DisplayName;
        string fullName = !string.IsNullOrWhiteSpace(cfg.Name) ? cfg.Name : label;
        string? model = cfg.Model ?? live?.Model;

        if (live is null)
        {
            return new BambuPrinterReading(
                cfg.Id, label, fullName, model,
                BambuPrinterState.Idle, 0, 0, null, 0, 0, null, null, null, Online: true);
        }

        bool online = live.Online;
        string rawState = (mqttConnected && !string.IsNullOrWhiteSpace(live.GcodeState))
            ? live.GcodeState.ToUpperInvariant()
            : (!string.IsNullOrWhiteSpace(live.CloudPrintStatus)
                ? live.CloudPrintStatus.ToUpperInvariant()
                : live.GcodeState.ToUpperInvariant());

        bool hasPrintError = live.PrintError != 0 && live.PrintError != UserCancelledPrintError;
        bool hasSeriousHms = !string.IsNullOrWhiteSpace(live.HmsErrorSummary);

        BambuPrinterState state;
        if (!online)
        {
            state = BambuPrinterState.Offline;
        }
        else if (rawState == "FAILED" || hasPrintError || (rawState == "PAUSE" && hasSeriousHms))
        {
            state = BambuPrinterState.Error;
        }
        else if (rawState is "PAUSE" or "PAUSED")
        {
            state = BambuPrinterState.Paused;
        }
        else if (rawState == "PREPARE" || (rawState is "RUNNING" or "ACTIVE" or "PRINTING" && live.Stage is 1 or 2 or 3 or 4 or 7 or 14))
        {
            state = live.Percent > 0 ? BambuPrinterState.Printing : BambuPrinterState.Preparing;
        }
        else if (rawState is "RUNNING" or "ACTIVE" or "PRINTING")
        {
            state = BambuPrinterState.Printing;
        }
        else if (rawState is "FINISH" or "SUCCESS")
        {
            state = BambuPrinterState.Finished;
        }
        else if (!online)
        {
            state = BambuPrinterState.Offline;
        }
        else
        {
            state = BambuPrinterState.Idle;
        }

        int percent = state switch
        {
            BambuPrinterState.Finished => 100,
            BambuPrinterState.Idle or BambuPrinterState.Offline => 0,
            _ => Math.Clamp(live.Percent, 0, 100),
        };

        string? errorText = null;
        if (state == BambuPrinterState.Error)
        {
            errorText = hasPrintError
                ? $"Print error 0x{live.PrintError:X8}"
                : live.HmsErrorSummary ?? DescribeStageIssue(live.Stage) ?? "Print stopped with an error";
        }
        else if (state == BambuPrinterState.Paused && live.Stage >= 0 && DescribeStageIssue(live.Stage) is { } reason)
        {
            errorText = reason;
        }

        return new BambuPrinterReading(
            cfg.Id,
            label,
            fullName,
            model,
            state,
            percent,
            state is BambuPrinterState.Printing or BambuPrinterState.Preparing or BambuPrinterState.Paused
                ? live.RemainingMinutes
                : 0,
            live.JobName,
            live.CurrentLayer,
            live.TotalLayers,
            live.NozzleTemp,
            live.BedTemp,
            errorText,
            online);
    }

    private static string? DescribeStageIssue(int stage) => stage switch
    {
        5 => "Paused for M400",
        6 => "Filament runout paused",
        8 => "Nozzle temperature malfunction",
        9 => "Heatbed temperature malfunction",
        10 => "Paused by user",
        13 => "Homing XY",
        16 => "Paused by user",
        17 => "Front cover fell off",
        19 => "Calibrating extrusion flow",
        20 => "Nozzle temperature malfunction",
        21 => "Heatbed temperature malfunction",
        22 => "Filament unloading",
        24 => "Filament loading",
        26 => "Paused due to AMS lost",
        27 => "Paused due to low fan speed",
        28 => "Chamber temperature malfunction",
        30 => "Paused by G-code",
        31 => "Motor noise calibration",
        32 => "Nozzle filament covered / blob detected",
        33 => "Cutter error",
        34 => "First layer error",
        35 => "Nozzle clogged",
        _ => null,
    };

    // ---- MQTT Packet Framing Helpers ---------------------------------------

    private static byte[] BuildMqttConnectPacket(string clientId, string username, string password, ushort keepAliveSeconds)
    {
        using var body = new MemoryStream();
        // Variable header: Protocol Name ("MQTT"), Level (4 = v3.1.1), Flags (0xC2 = User+Pass+CleanSession), KeepAlive
        WriteMqttString(body, "MQTT");
        body.WriteByte(0x04);
        body.WriteByte(0xC2);
        body.WriteByte((byte)(keepAliveSeconds >> 8));
        body.WriteByte((byte)(keepAliveSeconds & 0xFF));

        // Payload: ClientId, Username, Password
        WriteMqttString(body, clientId);
        WriteMqttString(body, username);
        WriteMqttString(body, password);

        return FrameMqttPacket(0x10, body.ToArray());
    }

    private static byte[] BuildMqttSubscribePacket(ushort packetId, string topicFilter)
    {
        using var body = new MemoryStream();
        body.WriteByte((byte)(packetId >> 8));
        body.WriteByte((byte)(packetId & 0xFF));
        WriteMqttString(body, topicFilter);
        body.WriteByte(0x00); // QoS 0
        return FrameMqttPacket(0x82, body.ToArray());
    }

    private static byte[] BuildMqttPublishPacket(string topic, string payloadJson)
    {
        using var body = new MemoryStream();
        WriteMqttString(body, topic);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        body.Write(payloadBytes, 0, payloadBytes.Length);
        return FrameMqttPacket(0x30, body.ToArray());
    }

    private static byte[] FrameMqttPacket(byte headerByte, byte[] body)
    {
        using var packet = new MemoryStream(body.Length + 5);
        packet.WriteByte(headerByte);
        int rem = body.Length;
        do
        {
            byte encoded = (byte)(rem % 128);
            rem /= 128;
            if (rem > 0) encoded |= 0x80;
            packet.WriteByte(encoded);
        } while (rem > 0);

        packet.Write(body, 0, body.Length);
        return packet.ToArray();
    }

    private static void WriteMqttString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        s.WriteByte((byte)(bytes.Length >> 8));
        s.WriteByte((byte)(bytes.Length & 0xFF));
        s.Write(bytes, 0, bytes.Length);
    }

    private static async Task WritePacketAsync(Stream stream, SemaphoreSlim writeLock, byte[] packet, CancellationToken ct)
    {
        using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        writeCts.CancelAfter(TimeSpan.FromSeconds(MqttWriteTimeoutSeconds));
        await writeLock.WaitAsync(writeCts.Token);
        try
        {
            await stream.WriteAsync(packet, writeCts.Token);
            await stream.FlushAsync(writeCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"MQTT write timed out after {MqttWriteTimeoutSeconds}s.");
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static async Task<(byte PacketType, byte Flags, byte[] Payload)> ReadMqttPacketAsync(
        Stream stream, int timeoutSeconds, CancellationToken ct)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        byte[] one = new byte[1];
        await ReadExactAsync(stream, one, readCts.Token, ct);
        byte header = one[0];

        int multiplier = 1;
        int remainingLength = 0;
        for (int i = 0; i < 4; i++)
        {
            await ReadExactAsync(stream, one, readCts.Token, ct);
            byte encoded = one[0];
            remainingLength += (encoded & 0x7F) * multiplier;
            if ((encoded & 0x80) == 0) break;
            multiplier *= 128;
        }

        if (remainingLength < 0 || remainingLength > 4 * 1024 * 1024)
            throw new IOException($"Invalid MQTT remaining length: {remainingLength}");

        byte[] payload = remainingLength > 0 ? new byte[remainingLength] : [];
        if (remainingLength > 0)
            await ReadExactAsync(stream, payload, readCts.Token, ct);

        return ((byte)(header >> 4), (byte)(header & 0x0F), payload);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken readCt, CancellationToken outerCt)
    {
        try
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), readCt);
                if (read <= 0)
                    throw new EndOfStreamException("MQTT stream closed by remote broker.");
                total += read;
            }
        }
        catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
        {
            throw new TimeoutException("MQTT read timed out.");
        }
    }

    private static string? TryExtractSerialFromTopic(string topic)
    {
        // Expected: device/{serial}/report
        if (!topic.StartsWith("device/", StringComparison.OrdinalIgnoreCase)) return null;
        int secondSlash = topic.IndexOf('/', 7);
        if (secondSlash <= 7) return null;
        return topic[7..secondSlash];
    }

    // ---- Formatting & Diagnostics ------------------------------------------

    public static string FormatRemaining(int minutes)
    {
        if (minutes <= 0) return "";
        if (minutes < 60) return $"{minutes}m";
        int h = minutes / 60;
        int m = minutes % 60;
        return m == 0 ? $"{h}h" : $"{h}h {m}m";
    }

    public static string DescribePrinterState(BambuPrinterReading p)
    {
        string rem = FormatRemaining(p.RemainingMinutes);
        return p.State switch
        {
            BambuPrinterState.Printing => $"{p.Percent}%{(rem.Length > 0 ? $" · {rem} left" : "")}",
            BambuPrinterState.Preparing => $"Preparing{(p.Percent > 0 ? $" {p.Percent}%" : "")}",
            BambuPrinterState.Paused => $"Paused at {p.Percent}%{(p.ErrorText is not null ? $" ({p.ErrorText})" : "")}",
            BambuPrinterState.Error => $"ERROR{(p.Percent > 0 ? $" at {p.Percent}%" : "")}{(p.ErrorText is not null ? $" — {p.ErrorText}" : "")}",
            BambuPrinterState.Finished => "Finished (100%)",
            BambuPrinterState.Offline => "Offline",
            _ => "Idle",
        };
    }

    /// <summary>Runs a safe diagnostic check of Bambu Cloud API and MQTT connectivity.</summary>
    public static async Task<string> SelfTest()
    {
        var report = new StringBuilder();
        string? token;
        string? mqttUser;
        string region;
        int configuredCount;
        DateTimeOffset? lastMsgAt;

        lock (gate)
        {
            token = Secret.Unprotect(settings?.AccessToken);
            mqttUser = settings?.MqttUsername;
            region = settings?.Region ?? "Global";
            configuredCount = settings?.Printers.Count ?? 0;
            lastMsgAt = lastMqttMessageAt;
        }

        report.AppendLine($"Region: {region} ({ApiBase(region)})");
        report.AppendLine($"Stored token: {(string.IsNullOrWhiteSpace(token) ? "none" : "present (DPAPI encrypted)")}");
        report.AppendLine($"MQTT user: {(string.IsNullOrWhiteSpace(mqttUser) ? "not resolved" : "resolved")}");
        report.AppendLine($"Configured printers: {configuredCount}");
        report.AppendLine($"Live MQTT stream: {(mqttConnected ? "connected" : "disconnected")}");
        report.AppendLine($"Last MQTT message: {(lastMsgAt is null ? "none yet" : $"{Math.Max(0, (int)(DateTimeOffset.UtcNow - lastMsgAt.Value).TotalSeconds)}s ago ({lastMsgAt.Value.ToLocalTime():HH:mm:ss})")}");

        if (string.IsNullOrWhiteSpace(token))
        {
            report.AppendLine("Result: Sign in with your Bambu Cloud account above.");
            return report.ToString().TrimEnd();
        }

        try
        {
            var (ok, count, err) = await SyncBoundDevicesAsync(CancellationToken.None);
            report.AppendLine(ok
                ? $"Cloud bind check: OK ({count} bound printer{(count == 1 ? "" : "s")} returned)"
                : $"Cloud bind check: FAILED ({err})");

            var snapshot = Current;
            if (snapshot is not null && snapshot.Printers.Count > 0)
            {
                report.AppendLine();
                for (int i = 0; i < snapshot.Printers.Count; i++)
                {
                    var p = snapshot.Printers[i];
                    report.AppendLine($"{i + 1}. {p.Label} ({p.Model ?? "Bambu"}): {DescribePrinterState(p)}");
                }
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"Cloud check failed: {ex.Message}");
        }

        return report.ToString().TrimEnd();
    }

    // ---- JSON helpers ------------------------------------------------------

    private static string CleanJobName(string raw)
    {
        string name = raw.Trim();
        int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (slash >= 0 && slash + 1 < name.Length) name = name[(slash + 1)..];
        if (name.EndsWith(".gcode.3mf", StringComparison.OrdinalIgnoreCase)) name = name[..^10];
        else if (name.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        else if (name.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
        return name;
    }

    private static string? ExtractCookie(HttpResponseMessage resp, string cookieName)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var cookies)) return null;
        string prefix = cookieName + "=";
        foreach (var raw in cookies)
        {
            foreach (var part in raw.Split(';'))
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return trimmed[prefix.Length..];
            }
        }
        return null;
    }

    private static string? TryReadErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return Text(root, "message") ?? Text(root, "error");
        }
        catch
        {
            return null;
        }
    }

    private static string? Text(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var val)) return null;
        return val.ValueKind == JsonValueKind.String ? val.GetString() : null;
    }

    private static bool TryGetInt(JsonElement obj, string prop, out int value)
    {
        value = 0;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value)) return true;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out value)) return true;
        return false;
    }

    private static bool TryGetLong(JsonElement obj, string prop, out long value)
    {
        value = 0;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out value)) return true;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out value)) return true;
        return false;
    }

    private static bool TryGetDouble(JsonElement obj, string prop, out double value)
    {
        value = 0;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value)) return true;
        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
            return true;
        return false;
    }
}
