using System.Windows.Forms;

namespace ScweenSpit;

internal static class Program
{
    /// <summary>Which copy of ScweenSpit is already running, as something a person can act on.</summary>
    private static string Running()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("ScweenSpit"))
                using (p)
                {
                    if (p.Id == Environment.ProcessId) continue;

                    var path = Native.ExecutablePath(p.MainWindowHandle) is { Length: > 0 } byWindow
                        ? byWindow
                        : p.MainModule?.FileName ?? "";
                    var version = path.Length > 0
                        ? System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion
                        : null;

                    return version is { Length: > 0 }
                        ? $"version {version}\n{path}"
                        : path.Length > 0 ? path : $"process {p.Id}";
                }
        }
        catch (Exception ex) { Log.Write($"could not identify the running instance: {ex.Message}"); }

        return "another process";
    }

    /// <summary>
    /// A message box that comes to the front. Ours may be the only window without a taskbar button —
    /// this runs before there is a tray icon — so one opening behind whatever is maximised is one
    /// nobody sees.
    /// </summary>
    private static void Tell(string message)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            using var owner = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(-32000, -32000),
                Size = new System.Drawing.Size(1, 1),
                ShowInTaskbar = false,
                TopMost = true,
            };
            owner.Show();
            MessageBox.Show(owner, message, "ScweenSpit", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { Log.Write($"could not show the already-running notice: {ex.Message}"); }
    }

    [STAThread]
    private static void Main()
    {
        bool dpi = false;
        try { dpi = Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch (Exception ex) { Log.Write($"SetProcessDpiAwarenessContext threw: {ex.Message}"); }

        Log.Banner();
        Log.Write($"dpi per-monitor-v2: {dpi}");

        // Before the single-instance check, and before anything else: this exists for the machine
        // whose taskbar is missing, and the copy that hid it may still be running or may be long
        // gone. Either way there is nothing to coordinate with — it only puts settings back.
        var args = Environment.GetCommandLineArgs();

        if (args.Any(a => a.Equals("--restore", StringComparison.OrdinalIgnoreCase)))
        {
            ApplicationConfiguration.Initialize();
            SystemRestore.RunFromCommandLine();
            return;
        }

        // Run by Apps & Features. Puts the machine back before removing the program that changed it.
        if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            ApplicationConfiguration.Initialize();
            Installer.Uninstall();
            return;
        }

        using var single = new Mutex(true, @"Local\ScweenSpit.SingleInstance", out bool first);
        if (!first)
        {
            var other = Running();
            Log.Write($"another instance already holds the mutex - exiting (running: {other})");

            // Saying nothing is the wrong answer here. The copy holding the mutex is often an older
            // one started at login from wherever it was first downloaded, and a newly downloaded exe
            // that exits without a word looks exactly like an exe that does not work.
            Tell($"ScweenSpit is already running:\n\n{other}\n\n" +
                 "Exit that copy from its tray icon, then start this one again.");
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write($"UI thread exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"unhandled: {e.ExceptionObject}");

        ApplicationConfiguration.Initialize();

        try
        {
            Application.Run(new TrayApplicationContext());
        }
        catch (Exception ex)
        {
            Log.Write($"FATAL: {ex}");
            throw;
        }
        finally
        {
            Log.Write("exiting");
        }
    }
}
