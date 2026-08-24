using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScweenSpit;

public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string? ChecksumUrl, string Notes);

/// <summary>
/// Self-update, which this app's shape makes unusually simple: the file the user keeps is the native
/// launcher, and it exits the moment it has started the app. So the file being replaced is never the
/// file that is running, and it can simply be overwritten.
/// </summary>
public static class Updater
{
    private static readonly HttpClient Http = Build();

    public static Version Current => Normalise(Assembly.GetExecutingAssembly().GetName().Version);

    /// <summary>
    /// Flattens to major.minor.build. Version.TryParse("0.9.2") leaves Revision at -1 while an
    /// assembly version carries 0, and -1 sorts BELOW 0 — so without this a genuinely newer release
    /// compares as older than what is already running, and the update never offers itself.
    /// </summary>
    private static Version Normalise(Version? v) =>
        v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));

    private static HttpClient Build()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub rejects requests without one.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ScweenSpit", Current.ToString()));
        return http;
    }

    /// <summary>The newest published release, or null when there is nothing newer than what is running.</summary>
    public static async Task<UpdateInfo?> CheckAsync(SplitConfig config)
    {
        var url = $"https://api.github.com/repos/{config.UpdateRepository}/releases/latest";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(config.UpdateToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.UpdateToken.Trim());

        using var response = await Http.SendAsync(request);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                "The release feed returned 404. A private repository needs an access token, or the "
              + "repository has to be public for updates to be visible.");

        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseTag(tag, out var version)) throw new InvalidOperationException($"Unreadable release tag '{tag}'.");

        string? download = null, checksum = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var href = asset.GetProperty("browser_download_url").GetString();

            if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)) checksum = href;
            else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) download = href;
        }

        if (download is null) throw new InvalidOperationException($"Release {tag} publishes no .exe.");

        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        Log.Write($"update check: running {Current}, latest {version} ({tag})");

        return version > Current ? new UpdateInfo(version, tag, download, checksum, notes) : null;
    }

    /// <summary>"v0.9.1" and "0.9.1" both mean the same thing.</summary>
    private static bool TryParseTag(string tag, out Version version)
    {
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed)) { version = new Version(0, 0, 0); return false; }
        version = Normalise(parsed);
        return true;
    }

    /// <summary>
    /// Downloads the release, checks it against the published digest, and puts it in place of the
    /// launcher. Returns the path to run; the caller restarts and exits.
    /// </summary>
    public static async Task<string> ApplyAsync(UpdateInfo update)
    {
        var target = LauncherPath()
            ?? throw new InvalidOperationException(
                "This is the unpacked copy, not the launcher, so there is nothing here to replace. "
              + "Run the ScweenSpit.exe you downloaded and update from that.");

        var staging = Path.Combine(Path.GetTempPath(), $"ScweenSpit-{update.Tag}.exe");
        await Download(update.DownloadUrl, staging);

        if (update.ChecksumUrl is not null) await Verify(staging, update.ChecksumUrl);
        else Log.Write("update: no checksum published, skipping verification");

        // The launcher is not running - it started the app and exited - so its file is not locked.
        // The old copy is kept aside rather than deleted, in case the new one will not start.
        var previous = target + ".previous";
        try { if (File.Exists(previous)) File.Delete(previous); } catch { }
        try { File.Move(target, previous, overwrite: true); } catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not move the current version aside: {ex.Message}");
        }

        try
        {
            File.Move(staging, target, overwrite: true);
        }
        catch
        {
            File.Move(previous, target, overwrite: true);   // put it back rather than leave nothing
            throw;
        }

        Log.Write($"update: {Current} -> {update.Version} at {target}");
        return target;
    }

    /// <summary>The launcher handed us its own path; without it we are the unpacked copy.</summary>
    public static string? LauncherPath()
    {
        var path = Environment.GetEnvironmentVariable("SCWEENSPIT_LAUNCHER");
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    private static async Task Download(string url, string destination)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync();
        await using var file = File.Create(destination);
        await body.CopyToAsync(file);
    }

    /// <summary>
    /// Compares the download against the digest published beside it. This catches a truncated or
    /// corrupted transfer; it is not a signature, and does not prove the release itself is genuine.
    /// </summary>
    private static async Task Verify(string file, string checksumUrl)
    {
        var published = (await Http.GetStringAsync(checksumUrl)).Trim().Split(' ', '\t')[0];

        await using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));

        if (!actual.Equals(published, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(file); } catch { }
            throw new InvalidOperationException("The download did not match its published checksum.");
        }

        Log.Write("update: checksum verified");
    }
}
