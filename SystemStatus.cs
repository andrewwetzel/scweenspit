using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace ScweenSpit;

public enum LinkKind { None, Wired, Wireless }

/// <summary>
/// The readings a taskbar's status area shows: battery, network and volume.
///
/// These are read from documented APIs rather than scraped from the shell, because the real
/// notification area cannot be borrowed — see <see cref="TaskbarWindow"/>. Every reading degrades
/// to "unknown" rather than throwing; a status area that can fail is a bar that can stop painting.
/// </summary>
public static class SystemStatus
{
    // ---- battery -----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 1 = on mains
        public byte BatteryFlag;         // 128 = no battery, 8 = charging
        public byte BatteryLifePercent;  // 255 = unknown
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>Charge percentage and whether it is charging, or null on a desktop.</summary>
    public static (int Percent, bool Charging)? Battery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var s)) return null;
            if ((s.BatteryFlag & 128) != 0) return null;              // no battery present
            if (s.BatteryLifePercent > 100) return null;              // 255 means unknown

            return (s.BatteryLifePercent, s.ACLineStatus == 1);
        }
        catch { return null; }
    }

    // ---- network -----------------------------------------------------------

    /// <summary>What kind of connection is actually carrying traffic, if any.</summary>
    public static LinkKind Link()
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable()) return LinkKind.None;

            var live = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                         && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToList();

            if (live.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                           || n.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet))
                return LinkKind.Wired;

            return live.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                ? LinkKind.Wireless
                : LinkKind.None;
        }
        catch { return LinkKind.None; }
    }

    // ---- volume ------------------------------------------------------------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints();                                   // slot 0, unused
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                      [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    // Every preceding method has to be declared to keep the vtable slots aligned, even unused.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify();
        void UnregisterControlChangeNotify();
        void GetChannelCount();
        void SetMasterVolumeLevel();
        void SetMasterVolumeLevelScalar();
        void GetMasterVolumeLevel();
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel();
        void SetChannelVolumeLevelScalar();
        void GetChannelVolumeLevel();
        void GetChannelVolumeLevelScalar();
        void SetMute();
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    private static readonly Guid AudioEndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    /// <summary>Master output level and mute state, or null if audio cannot be queried.</summary>
    public static (int Percent, bool Muted)? Volume()
    {
        object? enumerator = null, endpoint = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            ((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device);

            var iid = AudioEndpointVolumeId;
            device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out endpoint);

            var volume = (IAudioEndpointVolume)endpoint;
            volume.GetMasterVolumeLevelScalar(out float level);
            volume.GetMute(out bool muted);

            return ((int)Math.Round(level * 100), muted);
        }
        catch (Exception ex)
        {
            Log.WriteOnce("volume", $"volume unavailable: {ex.Message}");
            return null;
        }
        finally
        {
            if (endpoint is not null) Marshal.ReleaseComObject(endpoint);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Opens the Windows settings page behind a status icon.</summary>
    public static void Open(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write($"could not open {uri}: {ex.Message}"); }
    }
}
