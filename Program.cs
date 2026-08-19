using System.Windows.Forms;

namespace ScweenSpit;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Belt and braces: the manifest already declares PerMonitorV2.
        try { Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { /* older Windows: manifest still applies */ }

        using var single = new Mutex(true, @"Local\ScweenSpit.SingleInstance", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
