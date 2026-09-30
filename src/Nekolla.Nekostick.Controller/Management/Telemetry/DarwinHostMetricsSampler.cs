using System.Runtime.InteropServices;

namespace Nekolla.Nekostick.Controller.Management.Telemetry;

/// <summary>Samples host memory and CPU ticks via mach host_statistics64 on macOS.</summary>
internal sealed partial class DarwinHostMetricsSampler : CpuTickMetricsSampler
{
    private const int CpuLoadInfoFlavor = 3;
    private const int CpuLoadInfoCount = 4;
    private const int VmInfo64Flavor = 4;
    private const int VmInfo64Count = 40; // sizeof(vm_statistics64) / sizeof(natural_t), incl. padding
    private const int KernSuccess = 0;

    private readonly nint _hostPort = NativeMethods.MachHostSelf();

    protected override bool TryReadMemoryBytes(out long totalBytes, out long usedBytes)
    {
        totalBytes = 0;
        usedBytes = 0;
        var size = (nint)sizeof(long);
        if (NativeMethods.SysctlByName("hw.memsize", out var physicalMemory, ref size, 0, 0) != 0 || physicalMemory <= 0)
        {
            return false;
        }
        size = (nint)sizeof(long);
        if (NativeMethods.SysctlByName("hw.pagesize", out var pageSize, ref size, 0, 0) != 0 || pageSize <= 0)
        {
            return false;
        }
        var statistics = new VmStatistics64();
        var count = VmInfo64Count;
        if (_hostPort == 0 || NativeMethods.HostStatistics64(_hostPort, VmInfo64Flavor, ref statistics, ref count) != KernSuccess)
        {
            return false;
        }
        // Activity Monitor's "memory used": app memory (internal minus purgeable) + wired
        // + compressor, all in pages.
        var speculativeExcluded = statistics.InternalPageCount >= statistics.PurgeablePageCount
            ? statistics.InternalPageCount - statistics.PurgeablePageCount
            : 0;
        var usedPages = (ulong)speculativeExcluded + statistics.WireCount + statistics.CompressorPageCount;
        totalBytes = physicalMemory;
        usedBytes = checked((long)(usedPages * (ulong)pageSize));
        return true;
    }

    protected override bool TryReadCpuTicks(out ulong busyTicks, out ulong totalTicks)
    {
        busyTicks = 0;
        totalTicks = 0;
        if (_hostPort == 0)
        {
            return false;
        }
        var load = new HostCpuLoadInfo();
        var count = CpuLoadInfoCount;
        if (NativeMethods.HostStatistics64(_hostPort, CpuLoadInfoFlavor, ref load, ref count) != KernSuccess)
        {
            return false;
        }
        busyTicks = load.UserTicks + load.SystemTicks + load.NiceTicks;
        totalTicks = busyTicks + load.IdleTicks;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HostCpuLoadInfo
    {
#pragma warning disable CS0649 // Populated by host_statistics64.
        internal uint UserTicks;
        internal uint SystemTicks;
        internal uint IdleTicks;
        internal uint NiceTicks;
#pragma warning restore CS0649
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VmStatistics64
    {
#pragma warning disable CS0649 // Populated by host_statistics64.
        internal uint FreePageCount;
        internal uint ActivePageCount;
        internal uint InactivePageCount;
        internal uint WireCount;
        internal ulong ZeroFillCount;
        internal ulong Reactivations;
        internal ulong PageIns;
        internal ulong PageOuts;
        internal ulong Faults;
        internal ulong CowFaults;
        internal ulong Lookups;
        internal ulong Hits;
        internal ulong Purges;
        internal uint PurgeablePageCount;
        internal ulong SpeculativePageCount;
        internal ulong Decompressions;
        internal ulong Compressions;
        internal ulong SwapIns;
        internal ulong SwapOuts;
        internal uint CompressorPageCount;
        internal uint ThrottledPageCount;
        internal uint ExternalPageCount;
        internal uint InternalPageCount;
        internal ulong TotalUncompressedPagesInCompressor;
#pragma warning restore CS0649
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libSystem", EntryPoint = "mach_host_self")]
        internal static partial nint MachHostSelf();

        [LibraryImport("libSystem", EntryPoint = "host_statistics64")]
        internal static partial int HostStatistics64(nint host, int flavor, ref VmStatistics64 info, ref int count);

        [LibraryImport("libSystem", EntryPoint = "host_statistics64")]
        internal static partial int HostStatistics64(nint host, int flavor, ref HostCpuLoadInfo info, ref int count);

        [LibraryImport("libSystem", EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int SysctlByName(string name, out long oldValue, ref nint oldSize, nint newValue, nint newValueSize);
    }
}
