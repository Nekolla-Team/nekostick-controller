using System.Text.Json.Serialization;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Contains .NET runtime telemetry for the controller process.</summary>
public sealed class ControllerRuntimeTelemetryDto
{
    /// <summary>Gets the managed heap bytes currently in use (<see cref="GC.GetTotalMemory(bool)"/>).</summary>
    [JsonPropertyName("managedHeapBytes")] public long ManagedHeapBytes { get; init; }
    /// <summary>Gets the committed managed heap bytes reported by the GC.</summary>
    [JsonPropertyName("heapCommittedBytes")] public long HeapCommittedBytes { get; init; }
    /// <summary>Gets the total bytes allocated on the managed heap since process start.</summary>
    [JsonPropertyName("totalAllocatedBytes")] public long TotalAllocatedBytes { get; init; }
    /// <summary>Gets the total generation 0 collection count since process start.</summary>
    [JsonPropertyName("gen0Collections")] public int Gen0Collections { get; init; }
    /// <summary>Gets the total generation 1 collection count since process start.</summary>
    [JsonPropertyName("gen1Collections")] public int Gen1Collections { get; init; }
    /// <summary>Gets the total generation 2 collection count since process start.</summary>
    [JsonPropertyName("gen2Collections")] public int Gen2Collections { get; init; }
    /// <summary>Gets the percentage of wall-clock time the runtime spent in GC pauses.</summary>
    [JsonPropertyName("pauseTimePercentage")] public double PauseTimePercentage { get; init; }
    /// <summary>Gets the number of live threads in the process.</summary>
    [JsonPropertyName("threadCount")] public int ThreadCount { get; init; }
    /// <summary>Gets the number of open OS handles (file descriptors on POSIX); <see langword="null"/> when the platform cannot report it.</summary>
    [JsonPropertyName("handleCount")] public int? HandleCount { get; init; }
}

/// <summary>Contains OS-level telemetry for the controller process.</summary>
public sealed class ControllerProcessTelemetryDto
{
    /// <summary>Gets the process working set in bytes.</summary>
    [JsonPropertyName("workingSetBytes")] public long WorkingSetBytes { get; init; }
    /// <summary>Gets the process private memory in bytes; <see langword="null"/> when the platform cannot report it.</summary>
    [JsonPropertyName("privateMemoryBytes")] public long? PrivateMemoryBytes { get; init; }
    /// <summary>Gets process CPU utilisation between the previous and current sample as a percentage of one core; <see langword="null"/> for the first sample.</summary>
    [JsonPropertyName("cpuPercent")] public double? CpuPercent { get; init; }
}

/// <summary>Contains host machine telemetry; absent on platforms without a sampler.</summary>
public sealed class ControllerHostTelemetryDto
{
    /// <summary>Gets the total physical memory of the host in bytes.</summary>
    [JsonPropertyName("memoryTotalBytes")] public long MemoryTotalBytes { get; init; }
    /// <summary>Gets the used physical memory of the host in bytes.</summary>
    [JsonPropertyName("memoryUsedBytes")] public long MemoryUsedBytes { get; init; }
    /// <summary>Gets host CPU utilisation between the previous and current sample; <see langword="null"/> for the first sample.</summary>
    [JsonPropertyName("cpuPercent")] public double? CpuPercent { get; init; }
}

/// <summary>Contains a single point-in-time telemetry snapshot for the controller and its host.</summary>
public sealed class ControllerTelemetryDto
{
    /// <summary>Gets the sample time as Unix epoch milliseconds.</summary>
    [JsonPropertyName("timestampUnixMs")] public long TimestampUnixMs { get; init; }
    /// <summary>Gets the process uptime in seconds.</summary>
    [JsonPropertyName("uptimeSeconds")] public double UptimeSeconds { get; init; }
    /// <summary>Gets the .NET runtime telemetry of the controller process.</summary>
    [JsonPropertyName("runtime")] public ControllerRuntimeTelemetryDto Runtime { get; init; } = new();
    /// <summary>Gets the OS-level process telemetry.</summary>
    [JsonPropertyName("process")] public ControllerProcessTelemetryDto Process { get; init; } = new();
    /// <summary>Gets the host machine telemetry, or <see langword="null"/> when the platform is unsupported.</summary>
    [JsonPropertyName("host")] public ControllerHostTelemetryDto? Host { get; init; }
}
