using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScweenSpit;

/// <summary>
/// Signs a passkey challenge through Windows' own WebAuthn API — the one browsers use — so the
/// Windows Hello prompt (PIN, fingerprint, face, a security key, or a phone by QR code) appears
/// over ScweenSpit itself and nothing has to be opened in a browser.
///
/// It sees what Windows sees: passkeys in Windows Hello, on security keys, and on phones. One saved
/// only in a browser's own password manager is not offered.
/// </summary>
internal static class WebAuthn
{
    /// <summary>What a relying party's server needs to verify the assertion, in the JSON shape browsers send.</summary>
    public sealed record Assertion(string CredentialId, string ClientDataJson, string AuthenticatorData,
                                   string Signature, string? UserHandle)
    {
        public object ToJson() => new Dictionary<string, object?>
        {
            ["id"] = CredentialId,
            ["rawId"] = CredentialId,
            ["type"] = "public-key",
            ["response"] = new Dictionary<string, object?>
            {
                ["clientDataJSON"] = ClientDataJson,
                ["authenticatorData"] = AuthenticatorData,
                ["signature"] = Signature,
                ["userHandle"] = UserHandle,
            },
            ["clientExtensionResults"] = new Dictionary<string, object?>(),
        };
    }

    /// <summary>Whether this Windows has the API at all: Windows 10 1903 and later do.</summary>
    public static bool Available
    {
        get
        {
            try { return WebAuthNGetApiVersionNumber() >= 1; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
        }
    }

    /// <summary>
    /// Asks Windows for an assertion over <paramref name="challenge"/> (base64url, as the server
    /// gave it) for <paramref name="rpId"/>, made as if from <paramref name="origin"/>. Blocks
    /// until the person has answered the prompt, so run it off the UI thread; the prompt is owned by
    /// <paramref name="owner"/>. Throws <see cref="OperationCanceledException"/> if they dismiss it.
    /// </summary>
    public static Assertion GetAssertion(IntPtr owner, string rpId, string origin, string challenge,
                                         IReadOnlyList<string> allowCredentialIds, string userVerification,
                                         int timeoutMs)
    {
        var clientData = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "webauthn.get",
            ["challenge"] = challenge.TrimEnd('='),
            ["origin"] = origin,
            ["crossOrigin"] = false,
        });
        byte[] clientDataBytes = Encoding.UTF8.GetBytes(clientData);

        var allocations = new List<IntPtr>();
        IntPtr Pin(byte[] bytes)
        {
            var p = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
            Marshal.Copy(bytes, 0, p, bytes.Length);
            allocations.Add(p);
            return p;
        }
        IntPtr PinString(string s)
        {
            var p = Marshal.StringToHGlobalUni(s);
            allocations.Add(p);
            return p;
        }

        IntPtr assertionPtr = IntPtr.Zero;
        try
        {
            var data = new CLIENT_DATA
            {
                dwVersion = 1,
                cbClientDataJSON = (uint)clientDataBytes.Length,
                pbClientDataJSON = Pin(clientDataBytes),
                pwszHashAlgId = PinString("SHA-256"),
            };

            // No allow-list means any passkey for this site; with one, only those.
            IntPtr creds = IntPtr.Zero;
            int credSize = Marshal.SizeOf<CREDENTIAL>();
            if (allowCredentialIds.Count > 0)
            {
                creds = Marshal.AllocHGlobal(credSize * allowCredentialIds.Count);
                allocations.Add(creds);
                string publicKey = "public-key";
                for (int i = 0; i < allowCredentialIds.Count; i++)
                {
                    var id = FromBase64Url(allowCredentialIds[i]);
                    Marshal.StructureToPtr(new CREDENTIAL
                    {
                        dwVersion = 1,
                        cbId = (uint)id.Length,
                        pbId = Pin(id),
                        pwszCredentialType = PinString(publicKey),
                    }, creds + i * credSize, false);
                }
            }

            var options = new GET_ASSERTION_OPTIONS
            {
                dwVersion = 1,
                dwTimeoutMilliseconds = (uint)Math.Clamp(timeoutMs, 10_000, 600_000),
                CredentialList = new CREDENTIALS { cCredentials = (uint)allowCredentialIds.Count, pCredentials = creds },
                Extensions = new EXTENSIONS(),
                dwAuthenticatorAttachment = 0,   // any: Windows Hello, a key, or a phone
                dwUserVerificationRequirement = userVerification.ToLowerInvariant() switch
                {
                    "required" => 1u,
                    "discouraged" => 3u,
                    _ => 2u,
                },
                dwFlags = 0,
            };

            int hr = WebAuthNAuthenticatorGetAssertion(owner, rpId, ref data, ref options, out assertionPtr);
            if (hr == NTE_USER_CANCELLED) throw new OperationCanceledException("The passkey prompt was dismissed.");
            if (hr != 0 || assertionPtr == IntPtr.Zero)
                throw new InvalidOperationException($"Windows could not sign with a passkey ({ErrorName(hr)}).");

            var a = Marshal.PtrToStructure<ASSERTION>(assertionPtr);
            byte[] authData = Copy(a.pbAuthenticatorData, a.cbAuthenticatorData);
            byte[] signature = Copy(a.pbSignature, a.cbSignature);
            byte[] credentialId = Copy(a.Credential.pbId, a.Credential.cbId);
            byte[] userId = Copy(a.pbUserId, a.cbUserId);

            return new Assertion(ToBase64Url(credentialId), ToBase64Url(clientDataBytes), ToBase64Url(authData),
                                 ToBase64Url(signature), userId.Length > 0 ? ToBase64Url(userId) : null);
        }
        finally
        {
            if (assertionPtr != IntPtr.Zero) WebAuthNFreeAssertion(assertionPtr);
            foreach (var p in allocations) Marshal.FreeHGlobal(p);
        }
    }

    private static byte[] Copy(IntPtr p, uint length)
    {
        var bytes = new byte[length];
        if (length > 0 && p != IntPtr.Zero) Marshal.Copy(p, bytes, 0, (int)length);
        return bytes;
    }

    private static string ErrorName(int hr)
    {
        try
        {
            var name = Marshal.PtrToStringUni(WebAuthNGetErrorName(hr));
            return string.IsNullOrEmpty(name) ? $"0x{hr:X8}" : $"{name}, 0x{hr:X8}";
        }
        catch { return $"0x{hr:X8}"; }
    }

    public static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string text)
    {
        var s = text.Trim().Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    /// <summary>SHA-256 of a string, base64url — for logging which challenge was signed without logging it.</summary>
    public static string Fingerprint(string text) => ToBase64Url(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..10];

    // ---- interop: webauthn.h, version 1 of each structure ---------------------------

    private const int NTE_USER_CANCELLED = unchecked((int)0x80090036);

    [StructLayout(LayoutKind.Sequential)]
    private struct CLIENT_DATA
    {
        public uint dwVersion;
        public uint cbClientDataJSON;
        public IntPtr pbClientDataJSON;
        public IntPtr pwszHashAlgId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public uint dwVersion;
        public uint cbId;
        public IntPtr pbId;
        public IntPtr pwszCredentialType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIALS
    {
        public uint cCredentials;
        public IntPtr pCredentials;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXTENSIONS
    {
        public uint cExtensions;
        public IntPtr pExtensions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GET_ASSERTION_OPTIONS
    {
        public uint dwVersion;
        public uint dwTimeoutMilliseconds;
        public CREDENTIALS CredentialList;
        public EXTENSIONS Extensions;
        public uint dwAuthenticatorAttachment;
        public uint dwUserVerificationRequirement;
        public uint dwFlags;
    }

    // Only the version-1 prefix is read; later versions add fields after these, never before.
    [StructLayout(LayoutKind.Sequential)]
    private struct ASSERTION
    {
        public uint dwVersion;
        public uint cbAuthenticatorData;
        public IntPtr pbAuthenticatorData;
        public uint cbSignature;
        public IntPtr pbSignature;
        public CREDENTIAL Credential;
        public uint cbUserId;
        public IntPtr pbUserId;
    }

    [DllImport("webauthn.dll")]
    private static extern uint WebAuthNGetApiVersionNumber();

    [DllImport("webauthn.dll", CharSet = CharSet.Unicode)]
    private static extern int WebAuthNAuthenticatorGetAssertion(IntPtr hWnd, string rpId, ref CLIENT_DATA clientData,
                                                                ref GET_ASSERTION_OPTIONS options, out IntPtr assertion);

    [DllImport("webauthn.dll")]
    private static extern void WebAuthNFreeAssertion(IntPtr assertion);

    [DllImport("webauthn.dll")]
    private static extern IntPtr WebAuthNGetErrorName(int hr);
}
