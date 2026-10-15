using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScweenSpit;

/// <summary>
/// Reads model quota limits from the local Antigravity language server for the strip the bar draws.
///
/// Protocol notes adapted from AntigravityQuota by Henrik-3 and antigravity-usage by skainguyen1412
/// (both MIT licence — full notices in THIRD-PARTY-NOTICES.md): when Antigravity is running, its
/// language server process listens on 127.0.0.1 with a per-launch CSRF token in its command line and
/// answers Connect-RPC `GetUserStatus` on `/exa.language_server_pb.LanguageServerService/GetUserStatus`
/// with each model's `remainingFraction` and `resetTime`.
///
/// Nothing leaves the machine: we only ever talk to 127.0.0.1, and once the running server's PID
/// and port are known we reuse them directly on subsequent polls without re-scanning processes or
/// ports.
/// </summary>
public static class AntigravityUsage
{
    private const string ServicePath = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
    private const string RequestBody =
        "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\"}}";

    // The local language server uses a self-signed certificate on 127.0.0.1, and corporate proxy
    // settings must never intercept loopback traffic.
    private static readonly HttpClient http = new(new HttpClientHandler
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
    private static CancellationTokenSource? life;
    private static volatile UsageReading? current;
    private static UsageReading? lastGood;
    private static ServerEndpoint? cachedEndpoint;

    /// <summary>Wakes the poll loop early, for "check now" and when settings change.</summary>
    private static volatile TaskCompletionSource? nudge;
    private static volatile bool wakeWanted;
    private static volatile bool enabled;

    /// <summary>The latest reading, or null when tracking is off or has never run.</summary>
    public static UsageReading? Current => current;

    /// <summary>True when Antigravity usage tracking is switched on.</summary>
    public static bool Enabled => enabled;

    /// <summary>
    /// Starts, stops or re-points the poller to match the configuration. Safe to call on every
    /// settings change.
    /// </summary>
    public static void Configure(AntigravitySettings config)
    {
        lock (gate)
        {
            settings = config;
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

    /// <summary>
    /// Walks the discovery and query path step by step and reports what it found. Never includes
    /// the CSRF token itself, so the output is safe to paste into a bug report.
    /// </summary>
    public static string SelfTest()
    {
        var report = new StringBuilder();
        bool on;
        lock (gate) on = settings?.Enabled ?? false;

        report.AppendLine(
            settings is null ? "not configured"
            : !on ? "tracking is switched off"
            : "tracking is on");

        var rawProcesses = FindLanguageServerProcesses();
        report.AppendLine(rawProcesses.Count == 0
            ? "processes: no language_server process is running (is Antigravity open?)"
            : $"processes: {rawProcesses.Count} candidate(s) — " +
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

                    var parsed = Parse(reply.Value.Body, showPro: true, showFlash: true, showClaude: true, out var plan);
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

            // When waiting for Antigravity to be launched, check every 15 seconds so the strip comes
            // alive shortly after the IDE opens rather than up to an hour later.
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

        lock (gate)
        {
            if (settings is not { Enabled: true }) return;
            showPro = settings.ShowPro;
            showFlash = settings.ShowFlash;
            showClaude = settings.ShowClaude;
            cached = cachedEndpoint;
        }

        var rawProcesses = FindLanguageServerProcesses();
        if (rawProcesses.Count == 0)
        {
            lock (gate) cachedEndpoint = null;
            PublishStandby();
            return;
        }

        // Fast path: if the language server we talked to last time is still running on the same PID,
        // query its port directly without touching process memory, WMI or the TCP table.
        if (cached is not null && rawProcesses.Any(p => p.Pid == cached.Pid))
        {
            if (QueryPort(cached.Scheme, cached.Port, cached.CsrfToken) is { Status: 200 } fastReply)
            {
                var fastReading = Parse(fastReply.Body, showPro, showFlash, showClaude, out _);
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

                    var reading = Parse(reply.Body, showPro, showFlash, showClaude, out _);
                    if (reading.Error is not null) continue;

                    lock (gate) cachedEndpoint = new ServerEndpoint(candidate.Pid, port, scheme, candidate.CsrfToken);
                    lastGood = reading;
                    current = reading;
                    return;
                }
            }
        }

        // A language_server process is running, but either it has not finished binding its port yet
        // or it belongs to another editor. Keep the bar calm rather than flashing an error.
        PublishStandby();
    }

    /// <summary>
    /// When Antigravity is not running, keep the last reading on screen (dropping any bucket whose
    /// reset time has already passed to 0%) and mark it as standby for the tooltip; if we have never
    /// seen Antigravity run this session, show a quiet "standby" placeholder instead of an error.
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

    // ---- process & port discovery ------------------------------------------

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

        // Prefer processes whose command line explicitly identifies Antigravity over other Codeium
        // language servers that might be running alongside it.
        return candidates
            .OrderByDescending(c => c.IsAntigravity)
            .ThenByDescending(c => c.Pid)
            .ToList();
    }

    /// <summary>
    /// Reads a process's command line. Tries in-process PEB memory first (under a millisecond, no
    /// child process spawned), and falls back to CIM via PowerShell only if that fails.
    /// </summary>
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

            // On 64-bit Windows, PEB + 0x20 holds PRTL_USER_PROCESS_PARAMETERS, and
            // RTL_USER_PROCESS_PARAMETERS + 0x70 holds the CommandLine UNICODE_STRING.
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

    /// <summary>
    /// Finds all TCP ports that <paramref name="pid"/> is listening on, using the Win32 IP Helper
    /// table in-process first and falling back to netstat only if needed.
    /// </summary>
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

            using var reply = http.Send(request, HttpCompletionOption.ResponseContentRead);
            var body = reply.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return ((int)reply.StatusCode, body);
        }
        catch
        {
            return null;
        }
    }

    private static UsageReading Parse(string body, bool showPro, bool showFlash, bool showClaude, out string? plan)
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

                    if (!item.TryGetProperty("quotaInfo", out var quota) || quota.ValueKind != JsonValueKind.Object)
                        continue;

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
                    allModels.Add(new UsageLimit(label!, usedPercent, resetsAt));
                }
            }

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
        catch (Exception ex)
        {
            Log.Write($"antigravity usage reply unreadable: {ex.Message}");
            return new UsageReading([], "Unexpected reply from Antigravity", false);
        }
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
