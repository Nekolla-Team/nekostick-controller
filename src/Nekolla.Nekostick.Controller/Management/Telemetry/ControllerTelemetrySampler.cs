using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Nekolla.Nekostick.Controller.Management.Telemetry;

/// <summary>Samples .NET runtime and process metrics plus host metrics from a platform sampler.</summary>
internal sealed class ControllerTelemetrySampler
{
    private readonly IHostMetricsSampler? _hostSampler;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Lock _sampleLock = new();
    private long _previousSampleTimestamp;
    private TimeSpan _previousProcessorTime;

    internal ControllerTelemetrySampler(IHostMetricsSampler? hostSampler)
    {
        _hostSampler = hostSampler;
    }

    /// <summary>Captures one telemetry snapshot; process and host CPU percentages need a previous sample and are null on first call.</summary>
    internal ControllerTelemetryDto Sample()
    {
        lock (_sampleLock)
        {
            _process.Refresh();
            var now = Stopwatch.GetTimestamp();
            var processorTime = _process.TotalProcessorTime;
            double? processCpuPercent = null;
            if (_previousSampleTimestamp != 0)
            {
                var wallSeconds = Stopwatch.GetElapsedTime(_previousSampleTimestamp, now).TotalSeconds;
                if (wallSeconds > 0)
                {
                    // CPU seconds spent per wall-clock second, normalised to the share of total
                    // machine CPU capacity so the value stays within 0-100 on any core count.
                    processCpuPercent = Math.Min(
                        100d,
                        Math.Max(0d, (processorTime - _previousProcessorTime).TotalSeconds / wallSeconds * 100d / Environment.ProcessorCount));
                }
            }
            _previousSampleTimestamp = now;
            _previousProcessorTime = processorTime;

            var gcInfo = GC.GetGCMemoryInfo();
            var host = _hostSampler?.Sample();
            return new ControllerTelemetryDto
            {
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                UptimeSeconds = (DateTime.UtcNow - _process.StartTime.ToUniversalTime()).TotalSeconds,
                Runtime = new ControllerRuntimeTelemetryDto
                {
                    ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                    HeapCommittedBytes = gcInfo.HeapSizeBytes,
                    TotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false),
                    Gen0Collections = GC.CollectionCount(0),
                    Gen1Collections = GC.CollectionCount(1),
                    Gen2Collections = GC.CollectionCount(2),
                    PauseTimePercentage = gcInfo.PauseTimePercentage,
                    ThreadCount = _process.Threads.Count,
                    // macOS reports no handle count; null says "unsupported" better than a
                    // permanent zero.
                    HandleCount = _process.HandleCount > 0 ? _process.HandleCount : null,
                },
                Process = new ControllerProcessTelemetryDto
                {
                    WorkingSetBytes = _process.WorkingSet64,
                    PrivateMemoryBytes = _process.PrivateMemorySize64 > 0 ? _process.PrivateMemorySize64 : null,
                    CpuPercent = processCpuPercent,
                },
                Host = host is null
                    ? null
                    : new ControllerHostTelemetryDto
                    {
                        MemoryTotalBytes = host.MemoryTotalBytes,
                        MemoryUsedBytes = host.MemoryUsedBytes,
                        CpuPercent = host.CpuPercent,
                    },
            };
        }
    }

    /// <summary>Creates the host metrics sampler for the current platform; nekostick targets POSIX only.</summary>
    internal static IHostMetricsSampler? CreatePlatformSampler()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new DarwinHostMetricsSampler();
        }
        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? new LinuxHostMetricsSampler() : null;
    }
}
