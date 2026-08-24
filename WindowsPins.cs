using System.Runtime.InteropServices;
using System.Text;

namespace ScweenSpit;

/// <summary>
/// Reads the applications pinned to the Windows taskbar.
///
/// They live as ordinary shortcuts in the Quick Launch folder, so the list itself is easy. The
/// awkward part is the order, which Explorer keeps separately in a registry blob of shell item
/// identifiers — see <see cref="OrderKey"/>.
/// </summary>
public static class WindowsPins
{
    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    public sealed record Pin(string Path, string Name);

    /// <summary>
    /// Every pinned application that resolves to a real executable, in the order Windows shows them.
    /// Store-app pins are skipped: they are shortcuts to an application id rather than a file, and
    /// nothing here could launch or draw one.
    /// </summary>
    public static List<Pin> Read(out int skipped)
    {
        skipped = 0;
        var found = new List<Pin>();

        try
        {
            if (!Directory.Exists(Folder)) return found;

            var order = OrderKey();

            foreach (var shortcut in Directory.GetFiles(Folder, "*.lnk"))
            {
                var target = ShellLink.TargetOf(shortcut);
                if (string.IsNullOrWhiteSpace(target) || !File.Exists(target)) { skipped++; continue; }

                found.Add(new Pin(target, Path.GetFileNameWithoutExtension(shortcut)));
            }

            found = found.OrderBy(p => PinOrder.Rank(order, p.Path)).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            Log.Write($"could not read Windows taskbar pins: {ex.Message}");
        }

        return found;
    }

    /// <summary>
    /// Explorer stores the pin order as a serialised list of shell item identifiers, which is
    /// undocumented and not worth parsing. But the executable paths appear inside it as text, so
    /// where a path first occurs is a good enough proxy for its position.
    /// </summary>
    private static byte[] OrderKey()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");

            return key?.GetValue("FavoritesResolve") as byte[] ?? [];
        }
        catch { return []; }
    }

}

/// <summary>
/// Works out where an application sits in Explorer's pin order.
///
/// Separated from the rest so it can be exercised without a registry or a shell: it is the only
/// part of reading Windows' pins that is guesswork rather than a documented lookup.
/// </summary>
public static class PinOrder
{
    /// <summary>
    /// Position of an executable within Explorer's order blob, or int.MaxValue when it is not
    /// there. The blob is a serialised list of shell item identifiers — undocumented, and not worth
    /// parsing — but the file names appear in it as text, so first occurrence is a fair proxy.
    /// </summary>
    public static int Rank(byte[] blob, string path)
    {
        if (blob.Length == 0 || string.IsNullOrEmpty(path)) return int.MaxValue;

        // The blob mixes encodings; the name shows up in both, so look for either.
        var name = FileName(path);
        if (name.Length == 0) return int.MaxValue;
        int at = IndexOf(blob, Encoding.Unicode.GetBytes(name));
        if (at < 0) at = IndexOf(blob, Encoding.ASCII.GetBytes(name));

        return at < 0 ? int.MaxValue : at;
    }

    /// <summary>
    /// The last path segment, split on either separator rather than the host's. Path.GetFileName
    /// follows the running platform's convention, and these paths are always Windows ones — which
    /// also happens to be what makes this testable anywhere.
    /// </summary>
    public static string FileName(string path)
    {
        int cut = path.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? path : path[(cut + 1)..];
    }

    public static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return -1;

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}

/// <summary>Resolves a .lnk to the file it points at, via the shell's own link object.</summary>
internal static class ShellLink
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject { }

    // Only GetPath is called, and it is the first slot, so the rest of the vtable is not declared.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath,
                     IntPtr findData, uint flags);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
    }

    private const uint STGM_READ = 0;
    private const uint SLGP_RAWPATH = 0x4;

    public static string TargetOf(string shortcut)
    {
        object? link = null;
        try
        {
            link = new ShellLinkObject();
            ((IPersistFile)link).Load(shortcut, STGM_READ);

            var path = new StringBuilder(1024);

            // Raw rather than resolved: resolving hunts for a moved target, which can touch the
            // network and block a settings window for seconds.
            ((IShellLinkW)link).GetPath(path, path.Capacity, IntPtr.Zero, SLGP_RAWPATH);

            return Environment.ExpandEnvironmentVariables(path.ToString());
        }
        catch (Exception ex)
        {
            Log.WriteOnce($"lnk:{shortcut}", $"could not read {shortcut}: {ex.Message}");
            return string.Empty;
        }
        finally
        {
            if (link is not null) Marshal.ReleaseComObject(link);
        }
    }
}
