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

    /// <summary>
    /// Keeps an existing registration pointing at the copy that is actually running.
    ///
    /// The path is written once, when the switch is turned on, and the file it names is wherever it
    /// was downloaded to that day. Download the next version somewhere else — or to the same folder
    /// under a version-stamped name — and every login goes on starting the old one, which then holds
    /// the single-instance mutex against the new. The switch reads as off in the meantime, because
    /// the path no longer matches, so nothing about it looks wrong until you go looking.
    /// </summary>
    public static void Refresh()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            if (k?.GetValue(Name) is not string existing) return;   // never turned on: leave it off

            var current = ExePath;
            if (current.Length == 0) return;
            if (existing.Trim('"').Equals(current, StringComparison.OrdinalIgnoreCase)) return;

            k.SetValue(Name, $"\"{current}\"");
            Log.Write($"start with windows re-pointed: {existing} -> {current}");
        }
        catch (Exception ex) { Log.Write($"startup refresh failed: {ex.Message}"); }
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
