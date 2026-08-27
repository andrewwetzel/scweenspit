using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ScweenSpit;

/// <summary>
/// Puts ScweenSpit where an installed application lives: its own folder, a Start menu entry, and a
/// line in Apps &amp; Features.
///
/// Running from wherever it was downloaded works, and is how this has always worked — but it leaves
/// the program somewhere that gets tidied away, under a name like "ScweenSpit (5).exe", with nothing
/// to start it by except finding that file again. It also makes every path the app records — the
/// run-at-login entry, the file an update replaces — a path through the Downloads folder.
/// </summary>
internal static class Installer
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "ScweenSpit");

    public static string InstalledExe { get; } = Path.Combine(Folder, "ScweenSpit.exe");

    public static string Shortcut { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ScweenSpit.lnk");

    private const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ScweenSpit";

    /// <summary>Installed, in the sense that the Start menu will find it.</summary>
    public static bool IsInstalled => File.Exists(InstalledExe) && File.Exists(Shortcut);

    /// <summary>
    /// Whether the copy running is the installed one. The shipped file is the launcher, and it is
    /// the launcher that gets installed — the unpacked copy it starts lives elsewhere and is
    /// replaced by every update.
    /// </summary>
    public static bool RunningInstalled =>
        Source() is { } source && string.Equals(source, InstalledExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>The file to install: the launcher the user downloaded, not the copy it unpacked.</summary>
    private static string? Source()
    {
        var launcher = Updater.LauncherPath();
        if (launcher is not null) return launcher;

        // Started directly rather than through the launcher. Installing the unpacked copy would
        // produce something that cannot update itself, so it is not offered.
        return null;
    }

    /// <summary>
    /// Copies the launcher into place, makes the Start menu entry, and registers it for Apps &amp;
    /// Features. Returns the installed path.
    /// </summary>
    public static string Install()
    {
        var source = Source() ?? throw new InvalidOperationException(
            "This is the unpacked copy, started directly rather than through the ScweenSpit.exe you "
          + "downloaded. Run that one and install from it, so the installed copy can update itself.");

        Directory.CreateDirectory(Folder);

        // Not when it is already the file being run: copying a file over itself is the one case this
        // cannot do, and re-installing from the installed copy is a perfectly ordinary thing to ask.
        if (!string.Equals(source, InstalledExe, StringComparison.OrdinalIgnoreCase))
            File.Copy(source, InstalledExe, overwrite: true);

        CreateShortcut(InstalledExe, Shortcut);
        Register();

        Log.Write($"installed to {InstalledExe}");
        return InstalledExe;
    }

    /// <summary>
    /// Takes the Start menu entry and the Apps &amp; Features line away, hands the machine back, and
    /// removes the installed folder. The configuration is left alone: it lives elsewhere, it is the
    /// user's, and reinstalling should find it where it was.
    /// </summary>
    public static void Uninstall()
    {
        Log.Write("--uninstall requested");

        // Before anything is removed: whatever is still running is about to be, and a machine left
        // with the taskbar hidden by a program that no longer exists is the worst of the outcomes
        // this whole area has been about.
        Native.PostMessage(Native.HWND_BROADCAST, SystemRestore.QuitMessage, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(1500);
        SystemRestore.Everything(SplitConfig.Load());

        Try("start menu entry", () => File.Delete(Shortcut));
        Try("uninstall registration", () => Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false));
        Try("run at login", () => Startup.Set(false));

        RemoveFolderAfterExit();
    }

    /// <summary>
    /// Where the launcher unpacks the application it carries. Part of the installation as far as
    /// anyone uninstalling is concerned, even though nothing put it there on purpose.
    /// </summary>
    private static string Unpacked { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScweenSpit", "bin");

    private static void Try(string what, Action action)
    {
        try { action(); Log.Write($"removed {what}"); }
        catch (Exception ex) { Log.Write($"could not remove {what}: {ex.Message}"); }
    }

    /// <summary>
    /// The folder holds the file doing the asking, so it cannot delete itself. A detached shell waits
    /// for this process to be gone and then removes it.
    /// </summary>
    private static void RemoveFolderAfterExit()
    {
        // Guarded rather than trusted: this builds a recursive delete, and the only folder it may
        // ever be pointed at is the one this installs into.
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "ScweenSpit");

        if (!string.Equals(Folder, expected, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(Folder) || !File.Exists(InstalledExe))
        {
            Log.Write($"not removing {Folder}: not the folder this installs into");
            return;
        }

        try
        {
            // The unpacked copy goes too. Both are named literally, and both were checked above.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c ping -n 4 127.0.0.1 >nul & rd /s /q \"{Folder}\" & rd /s /q \"{Unpacked}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            Log.Write($"{Folder} and {Unpacked} will be removed once this process has gone");
        }
        catch (Exception ex) { Log.Write($"could not schedule removal of {Folder}: {ex.Message}"); }
    }

    private static void Register()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            if (key is null) return;

            key.SetValue("DisplayName", "ScweenSpit");
            key.SetValue("DisplayVersion", Updater.Current.ToString());
            key.SetValue("DisplayIcon", $"{InstalledExe},0");
            key.SetValue("InstallLocation", Folder);
            key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            try { key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord); }
            catch { /* a size is a nicety */ }
        }
        catch (Exception ex) { Log.Write($"could not register for Apps & Features: {ex.Message}"); }
    }

    // ---- shortcut ----------------------------------------------------------

    private static void CreateShortcut(string target, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var link = (IShellLinkW)new ShellLinkObject();
        link.SetPath(target);
        link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
        link.SetDescription("Sub-screens, zones and a taskbar of your own");
        link.SetIconLocation(target, 0);

        ((IPersistFile)link).Save(path, fRemember: true);
        Marshal.ReleaseComObject(link);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject { }

    /// <summary>
    /// Declared in vtable order and in full, unlike the read-only copy in <see cref="ShellLink"/>:
    /// COM dispatches by slot, so the methods before the ones being called cannot be left out.
    /// </summary>
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxArgs);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int maxPath, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hWnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder fileName);
    }
}
