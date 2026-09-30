using System.Runtime.InteropServices;

namespace Nekolla.Nekostick.Controller.Management.Telemetry;

/// <summary>Samples host memory via sysinfo(2) and CPU ticks via /proc/stat.</summary>
internal sealed partial class LinuxHostMetricsSampler : CpuTickMetricsSampler
{
    protected override bool TryReadMemoryBytes(out long totalBytes, out long usedBytes)
    {
        totalBytes = 0;
        usedBytes = 0;
        var info = new SysInfo();
        if (NativeMethods.SysInfo(ref info) != 0 || info.MemoryUnit == 0)
        {
            return false;
        }
        var unit = (ulong)info.MemoryUnit;
        var total = info.TotalRam * unit;
        // sysinfo has no MemAvailable equivalent; buffers are reclaimable, so treat
        // free + buffers as available and count the rest (including tmpfs) as used.
        var available = (info.FreeRam + info.BufferRam) * unit;
        totalBytes = checked((long)total);
        usedBytes = checked((long)(total - Math.Min(total, available)));
        return true;
    }

    protected override bool TryReadCpuTicks(out ulong busyTicks, out ulong totalTicks)
    {
        busyTicks = 0;
        totalTicks = 0;
        string line;
        try
        {
            using var reader = new StreamReader("/proc/stat");
            line = reader.ReadLine() ?? string.Empty;
        }
        catch (IOException)
        {
            return false;
        }
        // "cpu  user nice system idle iowait irq softirq steal guest guest_nice"; guest
        // values are already inside user/nice, so only the first 8 fields count.
        var remainder = line.AsSpan();
        if (!remainder.StartsWith("cpu ", StringComparison.Ordinal))
        {
            return false;
        }
        remainder = remainder[4..];
        var fields = 0;
        while (fields < 8)
        {
            remainder = remainder.TrimStart(' ');
            var end = remainder.IndexOf(' ');
            var field = end < 0 ? remainder : remainder[..end];
            remainder = end < 0 ? ReadOnlySpan<char>.Empty : remainder[end..];
            if (!ulong.TryParse(field, out var value))
            {
                return false;
            }
            if (fields is not 3 and not 4)
            {
                busyTicks += value;
            }
            totalTicks += value;
            fields++;
            if (remainder.IsEmpty)
            {
                break;
            }
        }
        return fields >= 8 && totalTicks > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SysInfo
    {
        internal long Uptime;
#pragma warning disable CS0649 // Populated by the sysinfo(2) call.
        internal ulong LoadAverage1;
        internal ulong LoadAverage5;
        internal ulong LoadAverage15;
#pragma warning restore CS0649
        internal ulong TotalRam;
        internal ulong FreeRam;
        internal ulong SharedRam;
        internal ulong BufferRam;
        internal ulong TotalSwap;
        internal ulong FreeSwap;
        internal ushort ProcessCount;
        internal ushort Padding;
        internal ulong TotalHigh;
        internal ulong FreeHigh;
        internal int MemoryUnit;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libc", EntryPoint = "sysinfo")]
        internal static partial int SysInfo(ref SysInfo info);
    }
}
