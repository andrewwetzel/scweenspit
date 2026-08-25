using System.Runtime.InteropServices;
using System.Windows.Forms;
using static ScweenSpit.Native;

namespace ScweenSpit;

/// <summary>
/// Registers a window as a Win32 application desktop toolbar, so Windows shrinks the work area to
/// make room for it and every other app keeps clear — the same mechanism the shell's own taskbar
/// uses, and the reason a bar of our own can sit on an edge Windows 11 refuses to put its taskbar on.
///
/// Reserving space is the whole point, so removal has to be reliable: a registration that outlives
/// the process would leave the desktop permanently short of that strip.
/// </summary>
public sealed class AppBar : IDisposable
{
    private const uint CallbackMessage = WM_APP + 0x41;

    private readonly Form owner;
    private bool registered;

    public BarEdge Edge { get; private set; }
    public int Thickness { get; private set; }
    public RECT Monitor { get; private set; }

    /// <summary>Raised when Windows tells us the reserved rectangle has to change.</summary>
    public event Action? PositionChanged;

    public AppBar(Form owner) => this.owner = owner;

    public bool Register()
    {
        if (registered) return true;

        var data = New(owner.Handle);
        data.uCallbackMessage = CallbackMessage;

        registered = SHAppBarMessage(ABM_NEW, ref data) != IntPtr.Zero;
        Log.Write($"appbar register: {registered}");
        return registered;
    }

    /// <summary>
    /// Asks Windows where the bar may sit, then claims it. Windows may move the proposed rectangle
    /// out of the way of other appbars, so the answer to ABM_QUERYPOS is authoritative, not our
    /// request — and the window has to be placed where the answer says.
    /// </summary>
    public void Reserve(RECT monitor, BarEdge edge, int thickness, int open = 0, int ends = 0, int edgeGap = 0)
    {
        if (!registered) return;

        Monitor = monitor;
        Edge = edge;
        Thickness = Math.Max(16, thickness);

        var data = New(owner.Handle);
        data.uEdge = (uint)edge;
        data.rc = Proposed();

        SHAppBarMessage(ABM_QUERYPOS, ref data);
        data.rc = Squeeze(data.rc, edge);   // QUERYPOS only adjusts the docking axis
        SHAppBarMessage(ABM_SETPOS, ref data);

        // Windows keeps the whole strip reserved; only the visible bar moves inside it, which is
        // what makes a floating bar float without applications creeping under it.
        var r = BarGeometry.Deflate(data.rc, edge, open, ends, edgeGap);
        SetWindowPos(owner.Handle, HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref data);
        Log.Write($"appbar reserved {edge} {data.rc} on {monitor}, bar at {r}");
    }

    /// <summary>The strip we would like, spanning the chosen edge of this display.</summary>
    private RECT Proposed() => Edge switch
    {
        BarEdge.Left   => new RECT { Left = Monitor.Left, Top = Monitor.Top, Right = Monitor.Left + Thickness, Bottom = Monitor.Bottom },
        BarEdge.Right  => new RECT { Left = Monitor.Right - Thickness, Top = Monitor.Top, Right = Monitor.Right, Bottom = Monitor.Bottom },
        BarEdge.Top    => new RECT { Left = Monitor.Left, Top = Monitor.Top, Right = Monitor.Right, Bottom = Monitor.Top + Thickness },
        _              => new RECT { Left = Monitor.Left, Top = Monitor.Bottom - Thickness, Right = Monitor.Right, Bottom = Monitor.Bottom },
    };

    /// <summary>Pins the returned rectangle back to our thickness along the docking axis.</summary>
    private RECT Squeeze(RECT r, BarEdge edge)
    {
        switch (edge)
        {
            case BarEdge.Left:   r.Right = r.Left + Thickness; break;
            case BarEdge.Right:  r.Left = r.Right - Thickness; break;
            case BarEdge.Top:    r.Bottom = r.Top + Thickness; break;
            default:             r.Top = r.Bottom - Thickness; break;
        }
        return r;
    }

    /// <summary>Call from the owning window's WndProc. Returns true when the message was ours.</summary>
    public bool HandleMessage(ref Message m)
    {
        if (m.Msg != (int)CallbackMessage) return false;

        switch (m.WParam.ToInt32())
        {
            case ABN_POSCHANGED:
            case ABN_FULLSCREENAPP:
                PositionChanged?.Invoke();
                break;
        }
        return true;
    }

    public void Dispose()
    {
        if (!registered) return;

        var data = New(owner.Handle);
        SHAppBarMessage(ABM_REMOVE, ref data);
        registered = false;
        Log.Write("appbar removed");
    }

    private static APPBARDATA New(IntPtr hWnd) => new()
    {
        cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
        hWnd = hWnd,
    };
}
