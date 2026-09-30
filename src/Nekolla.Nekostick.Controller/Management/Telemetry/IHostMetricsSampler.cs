using System.Runtime.InteropServices;

namespace Nekolla.Nekostick.Controller.Management.Telemetry;

/// <summary>One host machine metrics sample.</summary>
internal sealed record HostMetricsSample
{
    /// <summary>Gets the total physical memory of the host in bytes.</summary>
    internal required long MemoryTotalBytes { get; init; }
    /// <summary>Gets the used physical memory of the host in bytes.</summary>
    internal required long MemoryUsedBytes { get; init; }
    /// <summary>Gets host CPU utilisation since the previous sample; null on the first sample.</summary>
    internal double? CpuPercent { get; init; }
}

/// <summary>Samples host machine memory and CPU utilisation.</summary>
internal interface IHostMetricsSampler
{
    /// <summary>Takes one sample, or returns null when the sample could not be taken.</summary>
    HostMetricsSample? Sample();
}

/// <summary>Shared CPU-tick delta computation used by per-platform host samplers.</summary>
internal abstract class CpuTickMetricsSampler : IHostMetricsSampler
{
    private readonly Lock _sampleLock = new();
    private ulong _previousBusyTicks;
    private ulong _previousTotalTicks;

    public HostMetricsSample? Sample()
    {
        lock (_sampleLock)
        {
            double? cpuPercent = null;
            if (TryReadCpuTicks(out var busyTicks, out var totalTicks))
            {
                if (_previousTotalTicks != 0 && totalTicks > _previousTotalTicks)
                {
                    cpuPercent = Math.Min(
                        100d,
                        Math.Max(0d, (double)(busyTicks - _previousBusyTicks) / (totalTicks - _previousTotalTicks) * 100d));
                }
                _previousBusyTicks = busyTicks;
                _previousTotalTicks = totalTicks;
            }
            return TryReadMemoryBytes(out var totalBytes, out var usedBytes) && totalBytes > 0
                ? new HostMetricsSample
                {
                    MemoryTotalBytes = totalBytes,
                    MemoryUsedBytes = Math.Clamp(usedBytes, 0, totalBytes),
                    CpuPercent = cpuPercent,
                }
                : null;
        }
    }

    /// <summary>Reads total and used physical memory in bytes.</summary>
    protected abstract bool TryReadMemoryBytes(out long totalBytes, out long usedBytes);

    /// <summary>Reads cumulative busy and total CPU ticks aggregated over all cores.</summary>
    protected abstract bool TryReadCpuTicks(out ulong busyTicks, out ulong totalTicks);
}
