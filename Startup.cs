using Microsoft.Win32;

namespace ScweenSpit;

/// <summary>Run-at-login, via the per-user Run key. No installer, no scheduled task, no elevation.</summary>
internal static class Startup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "ScweenSpit";

    /// <summary>
    /// What to run at login. When started through the single-file launcher that is the launcher
    /// itself: it is the file the user actually downloaded, it repairs the unpacked copy, and it
    /// re-checks the runtime — none of which the unpacked copy can do for itself.
    /// </summary>
    private static string ExePath
    {
        get
        {
            var launcher = Environment.GetEnvironmentVariable("SCWEENSPIT_LAUNCHER");
            return !string.IsNullOrWhiteSpace(launcher) && File.Exists(launcher)
                ? launcher
                : Environment.ProcessPath ?? "";
        }
    }

    public static bool Enabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                return k?.GetValue(Name) is string s && s.Trim('"').Equals(ExePath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) { Log.Write($"startup read failed: {ex.Message}"); return false; }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (k is null) return;

            if (enabled) k.SetValue(Name, $"\"{ExePath}\"");
            else k.DeleteValue(Name, throwOnMissingValue: false);

            Log.Write($"start with windows: {enabled} ({ExePath})");
        }
        catch (Exception ex) { Log.Write($"startup write failed: {ex.Message}"); }
    }
}
