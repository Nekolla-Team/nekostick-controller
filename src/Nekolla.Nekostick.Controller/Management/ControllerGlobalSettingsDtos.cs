using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Read representation of global settings. Version is server-owned.</summary>
public sealed class ControllerGlobalSettingsReadDto
{
    /// <summary>Server-owned global-settings version.</summary>
    [JsonPropertyName("version")] public long Version { get; init; }
    /// <summary>Beginning of the automatic service port range.</summary>
    [JsonPropertyName("autoPortRangeStart")] public int AutoPortRangeStart { get; init; }
    /// <summary>End of the automatic service port range.</summary>
    [JsonPropertyName("autoPortRangeEnd")] public int AutoPortRangeEnd { get; init; }
    /// <summary>Maximum request body size in bytes.</summary>
    [JsonPropertyName("maxRequestBodyBytes")] public long MaxRequestBodyBytes { get; init; }
    /// <summary>Maximum request header size in bytes.</summary>
    [JsonPropertyName("maxRequestHeaderBytes")] public long MaxRequestHeaderBytes { get; init; }
    /// <summary>Maximum number of concurrent requests.</summary>
    [JsonPropertyName("maxConcurrentRequests")] public int MaxConcurrentRequests { get; init; }
    /// <summary>Request-read timeout in milliseconds.</summary>
    [JsonPropertyName("requestReadTimeoutMs")] public long RequestReadTimeoutMs { get; init; }
    /// <summary>Configuration polling interval in milliseconds.</summary>
    [JsonPropertyName("configurationPollIntervalMs")] public long ConfigurationPollIntervalMs { get; init; }
    /// <summary>Trusted proxy network ranges in CIDR notation.</summary>
    [JsonPropertyName("trustedProxyCidrs")] public ImmutableArray<string> TrustedProxyCidrs { get; init; } = ImmutableArray<string>.Empty;
    /// <summary>Proxy operation timeouts.</summary>
    [JsonPropertyName("proxyTimeouts")] public ControllerProxyTimeoutDto ProxyTimeouts { get; init; } = new();
    /// <summary>Optional client-IP rate-limit policy.</summary>
    [JsonPropertyName("clientIpRatePolicy")] public ControllerClientIpRatePolicyDto? ClientIpRatePolicy { get; init; }
    /// <summary>Proxy retry policy.</summary>
    [JsonPropertyName("proxyRetries")] public ControllerProxyRetryDto ProxyRetries { get; init; } = new();
}

/// <summary>Write representation of global settings. It deliberately has no identity fields.</summary>
public sealed class ControllerGlobalSettingsWriteDto
{
    /// <summary>Beginning of the automatic service port range.</summary>
    [JsonPropertyName("autoPortRangeStart")] public int AutoPortRangeStart { get; init; }
    /// <summary>End of the automatic service port range.</summary>
    [JsonPropertyName("autoPortRangeEnd")] public int AutoPortRangeEnd { get; init; }
    /// <summary>Maximum request body size in bytes.</summary>
    [JsonPropertyName("maxRequestBodyBytes")] public long MaxRequestBodyBytes { get; init; }
    /// <summary>Maximum request header size in bytes.</summary>
    [JsonPropertyName("maxRequestHeaderBytes")] public long MaxRequestHeaderBytes { get; init; }
    /// <summary>Maximum number of concurrent requests.</summary>
    [JsonPropertyName("maxConcurrentRequests")] public int MaxConcurrentRequests { get; init; }
    /// <summary>Request-read timeout in milliseconds.</summary>
    [JsonPropertyName("requestReadTimeoutMs")] public long RequestReadTimeoutMs { get; init; }
    /// <summary>Configuration polling interval in milliseconds.</summary>
    [JsonPropertyName("configurationPollIntervalMs")] public long ConfigurationPollIntervalMs { get; init; }
    /// <summary>Trusted proxy network ranges in CIDR notation.</summary>
    [JsonPropertyName("trustedProxyCidrs")] public ImmutableArray<string> TrustedProxyCidrs { get; init; } = ImmutableArray<string>.Empty;
    /// <summary>Proxy operation timeouts.</summary>
    [JsonPropertyName("proxyTimeouts")] public ControllerProxyTimeoutDto ProxyTimeouts { get; init; } = new();
    /// <summary>Optional client-IP rate-limit policy.</summary>
    [JsonPropertyName("clientIpRatePolicy")] public ControllerClientIpRatePolicyDto? ClientIpRatePolicy { get; init; }
    /// <summary>Proxy retry policy.</summary>
    [JsonPropertyName("proxyRetries")] public ControllerProxyRetryDto ProxyRetries { get; init; } = new();
}
