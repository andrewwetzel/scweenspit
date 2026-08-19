using System.Windows.Forms;

namespace ScweenSpit;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        bool dpi = false;
        try { dpi = Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch (Exception ex) { Log.Write($"SetProcessDpiAwarenessContext threw: {ex.Message}"); }

        Log.Banner();
        Log.Write($"dpi per-monitor-v2: {dpi}");

        using var single = new Mutex(true, @"Local\ScweenSpit.SingleInstance", out bool first);
        if (!first)
        {
            Log.Write("another instance already holds the mutex - exiting");
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
