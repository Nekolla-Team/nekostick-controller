using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Describes the criteria used to match a route.</summary>
public sealed class ControllerRouteMatcherDto
{
    /// <summary>Path matching strategy.</summary>
    [JsonPropertyName("type")] public ControllerRouteMatcherType Type { get; init; }
    /// <summary>Path pattern evaluated by the matcher.</summary>
    [JsonPropertyName("pattern")] public string Pattern { get; init; } = string.Empty;
    /// <summary>Host patterns that constrain route matching.</summary>
    [JsonPropertyName("hostPatterns")] public ImmutableArray<string> HostPatterns { get; init; } = ImmutableArray<string>.Empty;
    /// <summary>HTTP methods that constrain route matching.</summary>
    [JsonPropertyName("methods")] public ImmutableArray<string> Methods { get; init; } = ImmutableArray<string>.Empty;
}

/// <summary>Describes the destination selected by a route.</summary>
public sealed class ControllerRouteTargetDto
{
    /// <summary>Destination kind.</summary>
    [JsonPropertyName("type")] public ControllerRouteTargetKind Type { get; init; }
    /// <summary>Identifier of the target microservice, when applicable.</summary>
    [JsonPropertyName("serviceId")] public Guid? ServiceId { get; init; }
    /// <summary>Static-file root path, when applicable.</summary>
    [JsonPropertyName("rootPath")] public string? RootPath { get; init; }
    /// <summary>Extension handler identifier, when applicable.</summary>
    [JsonPropertyName("handlerId")] public string? HandlerId { get; init; }
}

/// <summary>Describes a request or response header rewrite.</summary>
public sealed class ControllerHeaderRewriteDto
{
    /// <summary>Rewrite operation.</summary>
    [JsonPropertyName("operation")] public ControllerHeaderRewriteOperation Operation { get; init; }
    /// <summary>Name of the header to rewrite.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    /// <summary>Value used by operations that provide a header value.</summary>
    [JsonPropertyName("value")] public string? Value { get; init; }
}

/// <summary>Describes how a matched request path is forwarded.</summary>
public sealed class ControllerForwardingDto
{
    /// <summary>Path forwarding mode.</summary>
    [JsonPropertyName("mode")] public ControllerForwardingMode Mode { get; init; }
    /// <summary>Template used when the forwarding mode replaces the path.</summary>
    [JsonPropertyName("replaceTemplate")] public string? ReplaceTemplate { get; init; }
}

/// <summary>Describes request rate limiting by client IP.</summary>
public sealed class ControllerClientIpRatePolicyDto
{
    /// <summary>Maximum number of tokens the bucket can hold.</summary>
    [JsonPropertyName("tokenLimit")] public long TokenLimit { get; init; }
    /// <summary>Number of tokens added during each replenishment period.</summary>
    [JsonPropertyName("tokensPerPeriod")] public long TokensPerPeriod { get; init; }
    /// <summary>Token replenishment period in milliseconds.</summary>
    [JsonPropertyName("replenishmentPeriodMs")] public long ReplenishmentPeriodMs { get; init; }
    /// <summary>Maximum number of requests that may wait in the queue.</summary>
    [JsonPropertyName("queueLimit")] public int QueueLimit { get; init; }
    /// <summary>Behavior applied when a request cannot be admitted immediately.</summary>
    [JsonPropertyName("rejectionBehavior")] public ControllerRateLimitRejectionBehavior RejectionBehavior { get; init; }
    /// <summary>Behavior used to determine retry-after information.</summary>
    [JsonPropertyName("retryAfterBehavior")] public ControllerRateLimitRetryAfterBehavior RetryAfterBehavior { get; init; }
}

/// <summary>Describes proxy operation time limits.</summary>
public sealed class ControllerProxyTimeoutDto
{
    /// <summary>Connection timeout in milliseconds.</summary>
    [JsonPropertyName("connectTimeoutMs")] public long ConnectTimeoutMs { get; init; }
    /// <summary>Timeout for an individual HTTP activity in milliseconds.</summary>
    [JsonPropertyName("httpActivityTimeoutMs")] public long HttpActivityTimeoutMs { get; init; }
    /// <summary>Total HTTP operation timeout in milliseconds.</summary>
    [JsonPropertyName("httpTotalTimeoutMs")] public long HttpTotalTimeoutMs { get; init; }
    /// <summary>WebSocket idle timeout in milliseconds.</summary>
    [JsonPropertyName("webSocketIdleTimeoutMs")] public long WebSocketIdleTimeoutMs { get; init; }
}

/// <summary>Describes proxy retry behavior.</summary>
public sealed class ControllerProxyRetryDto
{
    /// <summary>Maximum number of retries.</summary>
    [JsonPropertyName("maxRetries")] public int MaxRetries { get; init; }
    /// <summary>Initial retry backoff in milliseconds.</summary>
    [JsonPropertyName("initialBackoffMs")] public long InitialBackoffMs { get; init; }
    /// <summary>Maximum retry backoff in milliseconds.</summary>
    [JsonPropertyName("maximumBackoffMs")] public long MaximumBackoffMs { get; init; }
    /// <summary>Whether connection failures are retried.</summary>
    [JsonPropertyName("retryOnConnectionFailure")] public bool RetryOnConnectionFailure { get; init; }
    /// <summary>Whether upstream disconnects are retried.</summary>
    [JsonPropertyName("retryOnUpstreamDisconnect")] public bool RetryOnUpstreamDisconnect { get; init; }
}

/// <summary>Read representation of a route, including server-owned metadata.</summary>
public sealed class ControllerRouteReadDto
{
    /// <summary>Unique route identifier.</summary>
    [JsonPropertyName("id")] public Guid Id { get; init; }
    /// <summary>Whether the route is enabled.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    /// <summary>Route matching criteria.</summary>
    [JsonPropertyName("matcher")] public ControllerRouteMatcherDto Matcher { get; init; } = new();
    /// <summary>Route destination.</summary>
    [JsonPropertyName("target")] public ControllerRouteTargetDto Target { get; init; } = new();
    /// <summary>Route priority used during route selection.</summary>
    [JsonPropertyName("priority")] public int Priority { get; init; }
    /// <summary>Path forwarding configuration.</summary>
    [JsonPropertyName("forwarding")] public ControllerForwardingDto Forwarding { get; init; } = new();
    /// <summary>Rewrites applied to request headers.</summary>
    [JsonPropertyName("requestHeaderRewrites")] public ImmutableArray<ControllerHeaderRewriteDto> RequestHeaderRewrites { get; init; } = ImmutableArray<ControllerHeaderRewriteDto>.Empty;
    /// <summary>Rewrites applied to response headers.</summary>
    [JsonPropertyName("responseHeaderRewrites")] public ImmutableArray<ControllerHeaderRewriteDto> ResponseHeaderRewrites { get; init; } = ImmutableArray<ControllerHeaderRewriteDto>.Empty;
    /// <summary>Embedded route metadata as a JSON document.</summary>
    [JsonPropertyName("metadataJson")] public string MetadataJson { get; init; } = string.Empty;
    /// <summary>Time at which the route was created.</summary>
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Time at which the route was last updated.</summary>
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Current route version.</summary>
    [JsonPropertyName("version")] public long Version { get; init; }
    /// <summary>Optional client-IP rate-limit policy for the route.</summary>
    [JsonPropertyName("clientIpRatePolicy")] public ControllerClientIpRatePolicyDto? ClientIpRatePolicy { get; init; }
    /// <summary>Optional route-specific request body limit in bytes.</summary>
    [JsonPropertyName("maxRequestBodyBytes")] public long? MaxRequestBodyBytes { get; init; }
    /// <summary>Optional route-specific request header limit in bytes.</summary>
    [JsonPropertyName("maxRequestHeaderBytes")] public long? MaxRequestHeaderBytes { get; init; }
    /// <summary>Optional route-specific concurrent request limit.</summary>
    [JsonPropertyName("maxConcurrentRequests")] public int? MaxConcurrentRequests { get; init; }
    /// <summary>Optional route-specific request-read timeout in milliseconds.</summary>
    [JsonPropertyName("requestReadTimeoutMs")] public long? RequestReadTimeoutMs { get; init; }
    /// <summary>Optional route-specific proxy retry policy.</summary>
    [JsonPropertyName("proxyRetries")] public ControllerProxyRetryDto? ProxyRetries { get; init; }
}

/// <summary>Write representation for route create/patch. Identity and server fields are absent.</summary>
public sealed class ControllerRouteWriteDto
{
    /// <summary>Whether the route is enabled.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    /// <summary>Route matching criteria.</summary>
    [JsonPropertyName("matcher")] public ControllerRouteMatcherDto? Matcher { get; init; }
    /// <summary>Route destination.</summary>
    [JsonPropertyName("target")] public ControllerRouteTargetDto? Target { get; init; }
    /// <summary>Route priority used during route selection.</summary>
    [JsonPropertyName("priority")] public int Priority { get; init; }
    /// <summary>Path forwarding configuration.</summary>
    [JsonPropertyName("forwarding")] public ControllerForwardingDto? Forwarding { get; init; }
    /// <summary>Rewrites applied to request headers.</summary>
    [JsonPropertyName("requestHeaderRewrites")] public ImmutableArray<ControllerHeaderRewriteDto> RequestHeaderRewrites { get; init; } = ImmutableArray<ControllerHeaderRewriteDto>.Empty;
    /// <summary>Rewrites applied to response headers.</summary>
    [JsonPropertyName("responseHeaderRewrites")] public ImmutableArray<ControllerHeaderRewriteDto> ResponseHeaderRewrites { get; init; } = ImmutableArray<ControllerHeaderRewriteDto>.Empty;
    /// <summary>Embedded route metadata as a JSON document.</summary>
    [JsonPropertyName("metadataJson")] public string MetadataJson { get; init; } = string.Empty;
    /// <summary>Optional client-IP rate-limit policy for the route.</summary>
    [JsonPropertyName("clientIpRatePolicy")] public ControllerClientIpRatePolicyDto? ClientIpRatePolicy { get; init; }
    /// <summary>Optional route-specific request body limit in bytes.</summary>
    [JsonPropertyName("maxRequestBodyBytes")] public long? MaxRequestBodyBytes { get; init; }
    /// <summary>Optional route-specific request header limit in bytes.</summary>
    [JsonPropertyName("maxRequestHeaderBytes")] public long? MaxRequestHeaderBytes { get; init; }
    /// <summary>Optional route-specific concurrent request limit.</summary>
    [JsonPropertyName("maxConcurrentRequests")] public int? MaxConcurrentRequests { get; init; }
    /// <summary>Optional route-specific request-read timeout in milliseconds.</summary>
    [JsonPropertyName("requestReadTimeoutMs")] public long? RequestReadTimeoutMs { get; init; }
    /// <summary>Optional route-specific proxy retry policy.</summary>
    [JsonPropertyName("proxyRetries")] public ControllerProxyRetryDto? ProxyRetries { get; init; }
}
