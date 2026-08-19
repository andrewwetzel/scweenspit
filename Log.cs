using System.Reflection;

namespace ScweenSpit;

/// <summary>
/// Diagnostics. On by default (set SCWEENSPIT_LOG=0 to silence) because this thing has no UI to
/// speak of — when it misbehaves the log is the only way to see what it decided. Never throws.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SCWEENSPIT_LOG") != "0";

    public static readonly string LogPath = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(SplitConfig.Path)!, "scweenspit.log");

    private static readonly object Gate = new();

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                if (System.IO.File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                    System.IO.File.Delete(LogPath);
                System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { /* diagnostics must never break the app */ }
    }

    /// <summary>Banner written once at startup so every log says what produced it.</summary>
    public static void Banner()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        Write(new string('-', 60));
        Write($"ScweenSpit {v} starting  pid={Environment.ProcessId}  os={Environment.OSVersion.Version}  " +
              $"arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}  " +
              $"64bit={Environment.Is64BitProcess}");
        Write($"config: {SplitConfig.Path}");
        Write($"log:    {LogPath}");
    }
}
