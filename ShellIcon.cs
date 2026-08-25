using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScweenSpit;

/// <summary>
/// Icons from the shell's own catalogue of installed applications, for the ones whose windows carry
/// no icon of their own.
///
/// A Chrome PWA is the case that forces this. Its window has no icon to ask for, and its executable
/// is chrome.exe — so both of the ordinary answers are wrong: one blank, the other the browser's.
/// What it does have is an application id, which is the same identity the taskbar groups it by, and
/// the shell can be asked what that id looks like.
/// </summary>
internal static class ShellIcon
{
    /// <summary>Matches the size the other icon sources are kept at, and scaled down when drawn.</summary>
    private const int Size = 32;

    private const int SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const int SIIGBF_ICONONLY     = 0x00000004;

    private const int SIGDN_NORMALDISPLAY = 0x00000000;

    /// <summary>
    /// Names, cached. The bar rebuilds every second and this is a COM round trip; the display name
    /// of an installed application does not change while it is running. A cached null is an answer
    /// too — most windows have no application id and must not be asked about repeatedly.
    /// </summary>
    private static readonly Dictionary<string, string?> Names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What an application id is called, the way the Start menu says it. Anything hosted inside a
    /// browser reports the browser's process name, so a PWA reads as "chrome" everywhere its name
    /// appears — on a tooltip, in a right-click menu, and on a bar showing titles.
    /// </summary>
    public static string? NameForAppId(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        if (Names.TryGetValue(appId, out var cached)) return cached;

        string? name = null;
        try
        {
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName($"shell:AppsFolder\\{appId}", IntPtr.Zero, ref iid, out var created);

            var item = (IShellItem)created;
            item.GetDisplayName(SIGDN_NORMALDISPLAY, out name);
            Marshal.ReleaseComObject(item);
        }
        catch (Exception ex)
        {
            Log.WriteOnce($"shell-name-{appId}", $"no shell name for {appId}: {ex.Message}");
        }

        return Names[appId] = string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>What an application id looks like, or null when the shell does not know it.</summary>
    public static Bitmap? ForAppId(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;

        // The same parsing name the bar already launches pinned entries by, so an id that can be
        // started can also be drawn.
        return Load($"shell:AppsFolder\\{appId}");
    }

    private static Bitmap? Load(string parsingName)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var created);

            var factory = (IShellItemImageFactory)created;
            factory.GetImage(new Native.SIZE { Width = Size, Height = Size },
                             SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out handle);

            Marshal.ReleaseComObject(factory);
            return handle == IntPtr.Zero ? null : Convert(handle);
        }
        catch (Exception ex)
        {
            // An id the shell has no entry for is the ordinary case, not a fault.
            Log.WriteOnce($"shell-icon-{parsingName}", $"no shell icon for {parsingName}: {ex.Message}");
            return null;
        }
        finally { if (handle != IntPtr.Zero) DeleteObject(handle); }
    }

    /// <summary>
    /// A GDI bitmap turned into one we own. The direct route through Image.FromHbitmap drops the
    /// alpha channel, which leaves a black square around every icon that is not square — so the
    /// pixels are wrapped where they lie and copied out with their transparency intact.
    /// </summary>
    private static Bitmap? Convert(IntPtr handle)
    {
        var info = new BITMAP();
        if (GetObject(handle, Marshal.SizeOf<BITMAP>(), ref info) != 0
            && info.bmBitsPixel == 32 && info.bmBits != IntPtr.Zero)
        {
            try
            {
                using var wrapped = new Bitmap(info.bmWidth, info.bmHeight, info.bmWidthBytes,
                                               PixelFormat.Format32bppPArgb, info.bmBits);
                return new Bitmap(wrapped);   // owns its pixels; the shell's bitmap is freed after
            }
            catch { /* fall through to the lossy route rather than to nothing */ }
        }

        try
        {
            using var raw = Image.FromHbitmap(handle);
            return new Bitmap(raw);
        }
        catch { return null; }
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(Native.SIZE size, int flags, out IntPtr bitmap);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(int form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    /// <summary>
    /// Declared once, returning the interface asked for as an object. Two of these differing only by
    /// the type of an out parameter are ambiguous at every call site.
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindContext, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object item);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr handle, int size, ref BITMAP bitmap);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
