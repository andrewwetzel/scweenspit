using System.Runtime.InteropServices;
using System.Text;

namespace ScweenSpit;

/// <summary>
/// Keeps a credential in the config file without leaving it readable.
///
/// DPAPI ties the ciphertext to the Windows account, so config.json copied off the machine — or
/// read by another user on it — carries nothing usable. That matters more here than it looks: a
/// claude.ai session key is not scoped to usage figures, it is the whole account, and config.json
/// is a hand-editable file people paste into bug reports.
///
/// Failing to protect a value returns null rather than throwing, and the caller stores nothing:
/// a key that cannot be encrypted is one we would rather not keep at all.
/// </summary>
internal static class Secret
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB input, string? description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB input, IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    /// <summary>Never let DPAPI put a dialog on screen: this runs on a background poll.</summary>
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    /// <summary>Encrypts for the current user, as base64, or null if it could not be done.</summary>
    public static string? Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;

        var bytes = Encoding.UTF8.GetBytes(plain);
        var input = new DATA_BLOB();
        var output = new DATA_BLOB();
        try
        {
            input.cbData = bytes.Length;
            input.pbData = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, input.pbData, bytes.Length);

            if (!CryptProtectData(ref input, "ScweenSpit", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                  CRYPTPROTECT_UI_FORBIDDEN, out output))
            {
                Log.Write($"could not protect credential: win32 {Marshal.GetLastWin32Error()}");
                return null;
            }

            var cipher = new byte[output.cbData];
            Marshal.Copy(output.pbData, cipher, 0, output.cbData);
            return Convert.ToBase64String(cipher);
        }
        catch (Exception ex)
        {
            Log.Write($"could not protect credential: {ex.Message}");
            return null;
        }
        finally
        {
            Array.Clear(bytes);
            Release(ref input, marshalled: true);
            Release(ref output, marshalled: false);
        }
    }

    /// <summary>Decrypts what <see cref="Protect"/> wrote, or null if it is not ours to read.</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;

        var input = new DATA_BLOB();
        var output = new DATA_BLOB();
        try
        {
            var bytes = Convert.FromBase64String(stored);
            input.cbData = bytes.Length;
            input.pbData = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, input.pbData, bytes.Length);

            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                    CRYPTPROTECT_UI_FORBIDDEN, out output))
            {
                // Expected after a profile change or when the file came from another machine.
                Log.WriteOnce("unprotect", $"stored credential is not readable by this account " +
                                           $"(win32 {Marshal.GetLastWin32Error()})");
                return null;
            }

            var plain = new byte[output.cbData];
            Marshal.Copy(output.pbData, plain, 0, output.cbData);
            var text = Encoding.UTF8.GetString(plain);
            Array.Clear(plain);
            return text;
        }
        catch (Exception ex)
        {
            Log.WriteOnce("unprotect", $"stored credential unreadable: {ex.Message}");
            return null;
        }
        finally
        {
            Release(ref input, marshalled: true);
            Release(ref output, marshalled: false);
        }
    }

    /// <summary>
    /// Frees a blob. Ours came from AllocHGlobal; the one DPAPI hands back is its own allocation
    /// and has to go back through LocalFree, or the plaintext stays in the heap.
    /// </summary>
    private static void Release(ref DATA_BLOB blob, bool marshalled)
    {
        if (blob.pbData == IntPtr.Zero) return;

        // Wipe before releasing: both directions of this call have a credential in them.
        for (int i = 0; i < blob.cbData; i++) Marshal.WriteByte(blob.pbData, i, 0);

        if (marshalled) Marshal.FreeHGlobal(blob.pbData);
        else LocalFree(blob.pbData);

        blob.pbData = IntPtr.Zero;
        blob.cbData = 0;
    }
}
