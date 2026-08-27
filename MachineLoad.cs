using static ScweenSpit.Native;

namespace ScweenSpit;

/// <summary>
/// What the machine itself is doing: processor, memory, and how full the system drive is.
///
/// Read from the three Win32 calls that answer directly, rather than through performance counters —
/// those are a separate package on .NET 8, and this application has no dependencies on purpose.
/// Processor time is the one that needs work: Windows reports totals since boot, so a rate only
/// exists as the difference between two readings.
/// </summary>
internal static class MachineLoad
{
    private static ulong lastIdle, lastTotal;

    /// <summary>The current reading, or an empty list before there is one.</summary>
    public static IReadOnlyList<Meter> Read()
    {
        var meters = new List<Meter>(3);

        if (Processor() is { } cpu) meters.Add(cpu);
        if (Memory() is { } ram) meters.Add(ram);
        if (Disk() is { } disk) meters.Add(disk);

        return meters;
    }

    private static Meter? Processor()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime)) return null;

        ulong idle = Ticks(idleTime);

        // Kernel time already includes idle time, so these two together are the elapsed total across
        // every processor — not the busy part of it. The busy part is what is left after idle.
        ulong total = Ticks(kernelTime) + Ticks(userTime);

        ulong idleDelta = idle - lastIdle;
        ulong totalDelta = total - lastTotal;

        lastIdle = idle;
        lastTotal = total;

        if (BusyPercent(idleDelta, totalDelta) is not { } percent)
            return new Meter("CPU", 0, "CPU: measuring…");

        return new Meter("CPU", percent, $"CPU: {percent}% busy");
    }

    private static Meter? Memory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return null;

        int percent = Math.Clamp((int)status.dwMemoryLoad, 0, 100);
        return new Meter("RAM", percent,
            $"RAM: {percent}% used  ({Gigabytes(status.ullTotalPhys - status.ullAvailPhys)} of {Gigabytes(status.ullTotalPhys)})");
    }

    private static Meter? Disk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(root)) return null;

            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0) return null;

            ulong used = (ulong)(drive.TotalSize - drive.TotalFreeSpace);
            int percent = Math.Clamp((int)Math.Round(100.0 * used / drive.TotalSize), 0, 100);

            // How full, not how busy: activity needs a performance counter, and how full is the one
            // of the two that strands you.
            return new Meter("Disk", percent,
                $"Disk {drive.Name.TrimEnd('\\')} {percent}% full  ({Gigabytes((ulong)drive.TotalFreeSpace)} free)");
        }
        catch (Exception ex)
        {
            Log.WriteOnce("disk-meter", $"could not read the system drive: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Busy time as a percentage of elapsed time, or null when the pair cannot be believed: the
    /// first reading has nothing to difference against, and more idle than elapsed means the
    /// counters moved backwards under us — a resume, or a processor coming back online.
    /// </summary>
    internal static int? BusyPercent(ulong idleDelta, ulong totalDelta) =>
        totalDelta == 0 || idleDelta > totalDelta
            ? null
            : Math.Clamp((int)Math.Round(100.0 * (totalDelta - idleDelta) / totalDelta), 0, 100);

    private static ulong Ticks(FILETIME time) => ((ulong)time.High << 32) | time.Low;

    private static string Gigabytes(ulong bytes) => $"{bytes / 1024.0 / 1024 / 1024:0.#} GB";
}
