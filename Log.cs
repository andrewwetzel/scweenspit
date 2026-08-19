namespace ScweenSpit;

/// <summary>Opt-in diagnostics. Off unless SCWEENSPIT_LOG is set; never throws.</summary>
internal static class Log
{
    private static readonly bool Enabled =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCWEENSPIT_LOG"));

    private static readonly string LogPath = System.IO.Path.Combine(
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
                System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { /* diagnostics must never break the app */ }
    }
}
