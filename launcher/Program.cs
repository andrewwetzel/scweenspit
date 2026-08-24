using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ScweenSpit.Launcher;

/// <summary>
/// ScweenSpit's front door: one file you can hand to anyone.
///
/// The app is ~250 KB against the shared .NET Desktop Runtime, but an app that needs the runtime
/// cannot be the thing that checks for it. So this is compiled ahead of time to native code — it
/// starts on a machine with no .NET at all, installs the runtime if it is missing, then unpacks the
/// app beside your settings and runs it.
///
/// Hosting the runtime in-process (via hostfxr) would keep it to literally one process rather than
/// two, but it is a great deal of interop to get wrong for no visible difference, so the app is
/// started as a child instead.
/// </summary>
internal static partial class Program
{
    private const int RequiredMajor = 8;
    private const string RuntimeUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";
    private const string Caption = "ScweenSpit";

    /// <summary>Told by an updating instance to wait for it to exit before taking over.</summary>
    private const string ReplacingFlag = "--replacing";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (!HasDesktopRuntime(RequiredMajor) && !InstallRuntime()) return 1;

            var exe = UnpackedPath();

            // Before touching the file, not after: an update hands over while the old app is still
            // running out of the very copy we are about to replace, and Windows refuses to overwrite
            // a running image.
            WaitForHandover(args, exe);

            if (!Unpack(exe) && !File.Exists(exe))
            {
                Warn($"Could not unpack the application to:\n{exe}\n\nSee {LogPath}");
                return 1;
            }

            // Already running: say nothing and get out of the way. The app keeps a single-instance
            // mutex of its own, but it would exit silently and look like nothing happened.
            if (AlreadyRunning(exe)) return 0;

            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                Arguments = string.Join(' ', Forwarded(args).Select(Quote)),
            };

            // So "Start with Windows" registers this file rather than the unpacked copy: this is
            // the one the user actually has, and it repairs the unpacked copy on every launch.
            start.Environment["SCWEENSPIT_LAUNCHER"] = Environment.ProcessPath ?? "";

            Process.Start(start);
            return 0;
        }
        catch (Exception ex)
        {
            Note($"fatal: {ex}");
            Warn($"Could not start ScweenSpit.\n\n{ex.GetType().Name}: {ex.Message}"
               + $"\n\nDetails were written to:\n{LogPath}");
            return 1;
        }
    }

    private static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScweenSpit", "launcher.log");

    /// <summary>
    /// Records what went wrong. The launcher has no window and no console, so without this the only
    /// evidence of a failure is whatever the user manages to read off a message box.
    /// </summary>
    private static void Note(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* nothing left to try */ }
    }

    private static readonly TimeSpan AnnouncedHandover = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan UnannouncedHandover = TimeSpan.FromSeconds(8);

    /// <summary>Our own flags are consumed here, not passed on to the application.</summary>
    private static IEnumerable<string> Forwarded(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(ReplacingFlag, StringComparison.OrdinalIgnoreCase)) { yield return args[i]; continue; }
            i++;   // skip the pid that follows
        }
    }

    /// <summary>
    /// Waits for the instance that launched us to exit. An updater names its own process id; one
    /// predating that flag does not, so a running copy is given a short grace period before we
    /// assume it means to stay.
    /// </summary>
    private static void WaitForHandover(string[] args, string exe)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals(ReplacingFlag, StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(args[i + 1], out int pid)) continue;

            WaitFor(() => !IsAlive(pid), AnnouncedHandover);
            return;
        }

        if (AlreadyRunning(exe)) WaitFor(() => !AlreadyRunning(exe), UnannouncedHandover);
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }   // already gone
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(150);
        }
        return condition();
    }

    private static bool AlreadyRunning(string exe)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            try
            {
                if (p.Id != Environment.ProcessId &&
                    string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { /* another user's process, or exited while we looked */ }
            finally { p.Dispose(); }
        }
        return false;
    }

    // ---- runtime detection -------------------------------------------------

    /// <summary>
    /// Looks for an installed Microsoft.WindowsDesktop.App of the required major version. The
    /// shared-framework folder is the ground truth; asking "dotnet --list-runtimes" would need
    /// dotnet on PATH, which is exactly what may be missing.
    /// </summary>
    private static bool HasDesktopRuntime(int major)
    {
        foreach (var root in DotnetRoots())
        {
            var shared = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(shared)) continue;

            foreach (var dir in Directory.GetDirectories(shared))
                if (Version.TryParse(Path.GetFileName(dir), out var v) && v.Major == major)
                    return true;
        }
        return false;
    }

    private static IEnumerable<string> DotnetRoots()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot)) yield return explicitRoot;

        // ProgramW6432 is the 64-bit Program Files even when this process is 32-bit.
        var wow = Environment.GetEnvironmentVariable("ProgramW6432");
        if (!string.IsNullOrWhiteSpace(wow)) yield return Path.Combine(wow, "dotnet");

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(pf)) yield return Path.Combine(pf, "dotnet");
    }

    // ---- runtime install ---------------------------------------------------

    private static bool InstallRuntime()
    {
        if (Ask($"ScweenSpit needs the .NET {RequiredMajor} Desktop Runtime, which is not installed.\n\n"
              + "Download it from Microsoft and install it now?\n\n"
              + "It is about 58 MB, so this may take a few minutes with no visible progress. "
              + "Windows will ask for permission before installing.") != IdYes)
            return false;

        var installer = Path.Combine(Path.GetTempPath(), $"windowsdesktop-runtime-{RequiredMajor}-win-x64.exe");

        try
        {
            Download(RuntimeUrl, installer);
        }
        catch (Exception ex)
        {
            Warn($"Could not download the .NET Desktop Runtime.\n\n{ex.Message}\n\n"
               + "You can install it manually from https://dotnet.microsoft.com/download/dotnet/8.0");
            return false;
        }

        // /passive shows progress but asks nothing; runas raises the UAC prompt the installer needs.
        var run = Process.Start(new ProcessStartInfo(installer)
        {
            Arguments = "/install /passive /norestart",
            UseShellExecute = true,
            Verb = "runas",
        });

        if (run is null) { Warn("The installer did not start."); return false; }
        run.WaitForExit();

        try { File.Delete(installer); } catch { /* temp file; not worth reporting */ }

        // 3010 and 1641 both mean "installed, wants a reboot".
        if (run.ExitCode is 0 or 3010 or 1641) return true;

        Warn(run.ExitCode == 1602
            ? "Installation was cancelled."
            : $"The .NET Desktop Runtime installer failed (code {run.ExitCode}).");
        return false;
    }

    /// <summary>
    /// Fetches the installer without linking a managed HTTP stack. HttpClient would drag the whole
    /// managed socket and TLS implementation into the binary — several megabytes, statically, for
    /// one download that happens at most once per machine. Windows already ships two downloaders.
    /// </summary>
    private static void Download(string url, string destination)
    {
        var partial = destination + ".part";
        try { File.Delete(partial); } catch { }

        if (!TryCurl(url, partial) && !TryUrlMon(url, partial))
            throw new IOException("Neither curl nor urlmon could fetch the installer.");

        // A truncated or error-page download would otherwise be handed to the shell as an installer.
        var length = new FileInfo(partial).Length;
        if (length < 10 * 1024 * 1024)
            throw new IOException($"The download stopped early ({length / 1024} KB).");

        File.Move(partial, destination, overwrite: true);
    }

    /// <summary>curl.exe has shipped in System32 since Windows 10 1803, and follows redirects.</summary>
    private static bool TryCurl(string url, string destination)
    {
        var curl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
        if (!File.Exists(curl)) return false;

        var run = Process.Start(new ProcessStartInfo(curl)
        {
            Arguments = $"-L --fail --silent --show-error --output \"{destination}\" \"{url}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (run is null) return false;

        run.WaitForExit();
        return run.ExitCode == 0 && File.Exists(destination);
    }

    /// <summary>Fallback for Windows builds older than curl: one call, no managed networking.</summary>
    private static bool TryUrlMon(string url, string destination) =>
        URLDownloadToFile(IntPtr.Zero, url, destination, 0, IntPtr.Zero) == 0 && File.Exists(destination);

    [LibraryImport("urlmon.dll", EntryPoint = "URLDownloadToFileW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int URLDownloadToFile(IntPtr caller, string url, string file, uint reserved, IntPtr callback);

    // ---- unpacking ---------------------------------------------------------

    /// <summary>Where the application is kept once unpacked. No I/O, so it is safe to ask early.</summary>
    private static string UnpackedPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScweenSpit", "bin", "ScweenSpit.exe");

    /// <summary>
    /// Writes the embedded application out, and only when it differs from what is already there.
    /// Comparing content hashes rather than file lengths means an update that happens to produce the
    /// same size still replaces the unpacked copy.
    ///
    /// Returns false when it could not be written. That is not necessarily fatal: an existing copy
    /// is worth running, and the caller decides.
    /// </summary>
    private static bool Unpack(string exe)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("ScweenSpit.exe");
        if (payload is null)
        {
            Warn("This build has no application inside it — the payload was not embedded.");
            return false;
        }

        var stampFile = exe + ".stamp";
        var stamp = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        payload.Position = 0;

        if (File.Exists(exe) && ReadStamp(stampFile) == stamp) return true;

        var staging = exe + ".new";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);

            using (var file = File.Create(staging)) payload.CopyTo(file);

            // Windows refuses to overwrite a running image, and reports it as access denied rather
            // than a sharing violation - so both have to be caught, or this surfaces as a crash.
            File.Move(staging, exe, overwrite: true);
            File.WriteAllText(stampFile, stamp);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Note($"could not replace {exe}: {ex.GetType().Name}: {ex.Message}");
            try { File.Delete(staging); } catch { }
            return false;
        }
    }

    private static string? ReadStamp(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    // ---- messages ----------------------------------------------------------

    private const uint MbYesNo = 0x00000004, MbIconQuestion = 0x00000020, MbIconWarning = 0x00000030;

    // The launcher owns no window, so an ordinary message box can open behind whatever has focus
    // and look like nothing happened — which is exactly how a failed update presents itself.
    private const uint MbTopMost = 0x00040000, MbSetForeground = 0x00010000;
    private const int IdYes = 6;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private static int Ask(string text) =>
        MessageBox(IntPtr.Zero, text, Caption, MbYesNo | MbIconQuestion | MbTopMost | MbSetForeground);

    private static void Warn(string text) =>
        MessageBox(IntPtr.Zero, text, Caption, MbIconWarning | MbTopMost | MbSetForeground);
}
