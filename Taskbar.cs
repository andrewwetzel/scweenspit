using Microsoft.Win32;
using static ScweenSpit.Native;

namespace ScweenSpit;

public enum TaskbarEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

/// <summary>
/// Reads and relocates the Windows taskbar.
///
/// Reading is a supported API. Moving is not: there is no public API for it, so the edge is written
/// into Explorer's own StuckRects3 blob and Explorer is restarted to pick it up. On Windows 11
/// Microsoft removed taskbar repositioning entirely, and Explorer simply ignores the value — see
/// <see cref="CanMove"/>.
///
/// Only the PRIMARY display's taskbar is affected. Secondary-display bars live under MMStuckRects3,
/// keyed per monitor in an undocumented blob, and are deliberately left alone.
/// </summary>
public static class Taskbar
{
    private const string StuckRects = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3";
    private const int EdgeOffset = 12;   // byte 12 of the Settings blob holds the docked edge

    /// <summary>Windows 11 (build 22000+) dropped support for anything but the bottom edge.</summary>
    public static bool CanMove => Environment.OSVersion.Version.Build < 22000;

    /// <summary>Where the taskbar actually is right now, via the documented shell API.</summary>
    public static TaskbarEdge? Current()
    {
        var data = new APPBARDATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == IntPtr.Zero)
        {
            Log.Write("ABM_GETTASKBARPOS failed");
            return null;
        }
        return (TaskbarEdge)data.uEdge;
    }

    /// <summary>
    /// Writes the requested edge and restarts Explorer. Returns false if the registry value could
    /// not be read or written; a true result does not guarantee Windows honoured it.
    /// </summary>
    public static bool Move(TaskbarEdge edge)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StuckRects, writable: true);
            if (key?.GetValue("Settings") is not byte[] blob || blob.Length <= EdgeOffset)
            {
                Log.Write($"taskbar: {StuckRects}\\Settings missing or too short");
                return false;
            }

            // Compare against where the taskbar ACTUALLY is, not against the byte we last wrote.
            // On Windows 11 the write is ignored, so the stored byte says "done" while the bar has
            // not moved - which made every press after the first a silent no-op.
            if (Current() == edge)
            {
                Log.Write($"taskbar already docked {edge}");
                return true;
            }

            blob[EdgeOffset] = (byte)edge;
            key.SetValue("Settings", blob, RegistryValueKind.Binary);
            Log.Write($"taskbar edge set to {edge}; restarting Explorer");

            RestartExplorer();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"taskbar move failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Explorer only reads StuckRects at startup. Windows normally relaunches the shell by itself,
    /// but not in every configuration, so it is started explicitly if it stays down.
    /// </summary>
    private static void RestartExplorer()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); p.WaitForExit(4000); } catch { /* another session's shell */ }
                finally { p.Dispose(); }
            }

            Thread.Sleep(700);
            if (System.Diagnostics.Process.GetProcessesByName("explorer").Length == 0)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"explorer restart failed: {ex.Message}");
        }
    }
}
