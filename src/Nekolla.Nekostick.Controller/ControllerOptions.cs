using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller;

/// <summary>Identifies the controller transports that may be enabled explicitly.</summary>
public enum ControllerTransport
{
    /// <summary>The Host-owned route adapter.</summary>
    HostRoute,

    /// <summary>A self-hosted loopback HTTP/JSON adapter.</summary>
    HttpJson,

    /// <summary>A self-hosted loopback local gRPC adapter.</summary>
    Grpc,

    /// <summary>A local Unix-domain-socket adapter.</summary>
    UnixSocket
}

/// <summary>Identifies the management capability groups exposed by the controller.</summary>
[Flags]
public enum ControllerApiScope
{
    /// <summary>No management capabilities.</summary>
    None = 0,

    /// <summary>The complete persisted Host configuration API.</summary>
    FullConfiguration = 1
}

/// <summary>Identifies a safe controller options validation result.</summary>
public enum ControllerOptionsError
{
    /// <summary>The options are valid.</summary>
    None,

    /// <summary>All controller bindings must remain loopback/local.</summary>
    NonLocalBinding,

    /// <summary>An enabled transport has no configured API scope.</summary>
    ApiScopeRequired,

    /// <summary>The API scope contains an unsupported capability flag.</summary>
    ApiScopeInvalid,

    /// <summary>An enabled protected transport has no API key.</summary>
    ApiKeyRequired,

    /// <summary>The configured API key does not meet the minimum strength policy.</summary>
    ApiKeyWeak,

    /// <summary>The configured HTTP port is missing.</summary>
    HttpPortRequired,

    /// <summary>The configured HTTP port is outside the TCP port range.</summary>
    HttpPortInvalid,

    /// <summary>The configured gRPC port is missing.</summary>
    GrpcPortRequired,

    /// <summary>The configured gRPC port is outside the TCP port range.</summary>
    GrpcPortInvalid,

    /// <summary>The Unix socket path is missing.</summary>
    UnixSocketPathRequired,

    /// <summary>The Unix socket path is not an absolute local path.</summary>
    UnixSocketPathInvalid,

    /// <summary>The Host route path is missing when HostRoute is enabled.</summary>
    HostRoutePathRequired,

    /// <summary>The Host route path is not a safe absolute management path.</summary>
    HostRoutePathInvalid,

    /// <summary>The Unix socket mode is not exactly owner read/write only.</summary>
    UnixSocketModeInvalid,

    /// <summary>A configured CORS origin is not an exact scheme://host[:port] origin.</summary>
    CorsOriginInvalid
}

/// <summary>Contains a safe validation result without echoing configuration or secrets.</summary>
public readonly record struct ControllerOptionsValidationResult(
    bool IsValid,
    ControllerOptionsError Error)
{
    /// <summary>Creates a successful validation result.</summary>
    public static ControllerOptionsValidationResult Valid => new(true, ControllerOptionsError.None);

    /// <summary>Creates a failed validation result.</summary>
    public static ControllerOptionsValidationResult Invalid(ControllerOptionsError error) =>
        new(false, error == ControllerOptionsError.None
            ? throw new ArgumentOutOfRangeException(nameof(error))
            : error);
}

/// <summary>
/// Defines the controller's explicit, local-only transport and capability configuration.
/// All transport enablement defaults to disabled; no endpoint, port, socket, or secret is invented.
/// </summary>
public sealed class ControllerOptions
{
    /// <summary>The stable extension identifier used for host configuration lookup.</summary>
    public const string ExtensionId = "nekolla.nekostick.controller";

    /// <summary>The supported extension settings schema version.</summary>
    public const int ConfigurationSchemaVersion = 1;
    /// <summary>The maximum accepted API-key length in UTF-16 characters.</summary>
    public const int MaximumApiKeyLength = 4096;

    /// <summary>The maximum accepted API-key length in UTF-8 bytes.</summary>
    public const int MaximumApiKeyBytes = MaximumApiKeyLength * 4;

    /// <summary>The maximum host settings JSON document length.</summary>
    public const int MaximumConfigurationJsonLength = 64 * 1024;

    /// <summary>The minimum API-key length accepted by protected transports.</summary>
    public const int MinimumApiKeyLength = 32;

    /// <summary>The exact Unix socket file mode required by the local transport.</summary>
    public const int RequiredUnixSocketMode = 0x180; // 0600

    /// <summary>Gets the safe configuration with every external listener disabled.</summary>
    public static ControllerOptions SafeDefaults { get; } = new();

    /// <summary>Gets whether all enabled bindings are constrained to loopback/local resources.</summary>
    /// <remarks>Setting this to <see langword="false" /> is rejected; remote binding is out of scope.</remarks>
    public bool LoopbackOnly { get; init; } = true;

    /// <summary>Gets whether the embedded single-file Web UI is served by supported transports.</summary>
    public bool EnableWebUi { get; init; }

    /// <summary>Gets whether the Host-owned route adapter is enabled.</summary>
    public bool EnableHostRoute { get; init; }

    /// <summary>Gets whether the self-hosted HTTP/JSON adapter is enabled.</summary>
    public bool EnableHttpJson { get; init; }

    /// <summary>Gets whether the local gRPC adapter is enabled.</summary>
    public bool EnableGrpc { get; init; }

    /// <summary>Gets whether the Unix-domain-socket adapter is enabled.</summary>
    public bool EnableUnixSocket { get; init; }

    /// <summary>Gets the explicit loopback HTTP port, or <see langword="null" /> when unset.</summary>
    public int? HttpPort { get; init; }

    /// <summary>Gets the explicit loopback gRPC port, or <see langword="null" /> when unset.</summary>
    public int? GrpcPort { get; init; }

    /// <summary>Gets the explicit Unix socket path, or <see langword="null" /> when unset.</summary>
    public string? UnixSocketPath { get; init; }

    /// <summary>Gets the explicit Host-owned management route path, or null when unset.</summary>
    public string? HostRoutePath { get; init; }
    /// <summary>
    /// Gets the Unix socket file mode. The only accepted value is <c>0600</c> so callers on the
    /// same machine cannot read or write the socket through group/other permissions.
    /// </summary>
    public int UnixSocketMode { get; init; } = RequiredUnixSocketMode;

    /// <summary>
    /// Gets the configured API key. This value is never included in validation results, status,
    /// logs, exceptions, or management responses.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>Gets the explicitly selected management capability groups.</summary>
    public ControllerApiScope ApiScope { get; init; } = ControllerApiScope.FullConfiguration;
    /// <summary>
    /// Gets the browser origins allowed to call browser-facing transports cross-origin. Defaults
    /// to <c>*</c> (any origin) so fresh and bootstrap installs stay reachable from a hosted web
    /// UI; set an explicit allowlist to restrict, or an empty array to disable CORS entirely.
    /// Entries must be exact <c>scheme://host[:port]</c> origins without path, query, fragment,
    /// or credentials.
    /// </summary>
    public ImmutableArray<string> CorsAllowedOrigins { get; init; } = ImmutableArray.Create("*");

    /// <summary>Gets whether any protected transport has been explicitly enabled.</summary>
    public bool RequiresApiKey =>
        EnableHostRoute || EnableHttpJson || EnableGrpc || EnableUnixSocket;

    /// <summary>Gets whether the API key meets this foundation's minimum policy.</summary>
    public bool HasStrongApiKey => IsStrongApiKey(ApiKey);

    /// <summary>Determines whether a transport is explicitly enabled.</summary>
    /// <param name="transport">The transport kind.</param>
    /// <returns><see langword="true" /> when its enablement flag is set.</returns>
    public bool IsTransportEnabled(ControllerTransport transport) => transport switch
    {
        ControllerTransport.HostRoute => EnableHostRoute,
        ControllerTransport.HttpJson => EnableHttpJson,
        ControllerTransport.Grpc => EnableGrpc,
        ControllerTransport.UnixSocket => EnableUnixSocket,
        _ => false
    };

    /// <summary>
    /// Compares a presented key without logging or exposing the configured key. Candidates longer
    /// than <see cref="MaximumApiKeyLength" /> are rejected before UTF-8 buffers are allocated.
    /// </summary>
    /// <param name="presentedKey">The transport-provided key.</param>
    /// <returns><see langword="true" /> only for an exact configured key.</returns>
    public bool IsApiKeyValid(string? presentedKey)
    {
        if (!HasStrongApiKey || string.IsNullOrEmpty(presentedKey) ||
            presentedKey.Length > MaximumApiKeyLength ||
            Encoding.UTF8.GetByteCount(presentedKey) > MaximumApiKeyBytes)
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(ApiKey!);
        var actual = Encoding.UTF8.GetBytes(presentedKey);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>
    /// Parses the controller's versioned host settings document without exposing configuration
    /// values in an exception or validation result.
    /// </summary>
    /// <param name="settings">The host-owned extension settings DTO.</param>
    /// <param name="options">The parsed options when the source is valid.</param>
    /// <returns><see langword="true" /> when the settings identify this extension and parse cleanly.</returns>
    public static bool TryParseHostSettings(
        ExtensionSettingsConfiguration settings,
        out ControllerOptions? options)
    {
        ArgumentNullException.ThrowIfNull(settings);
        options = null;

        if (!string.Equals(settings.ExtensionId, ExtensionId, StringComparison.Ordinal) ||
            settings.SchemaVersion != ConfigurationSchemaVersion ||
            settings.SettingsJson.Length > MaximumConfigurationJsonLength)
        {
            return false;
        }

        try
        {
            var document = JsonSerializer.Deserialize<ControllerSettingsDocument>(
                settings.SettingsJson,
                SettingsJsonOptions);
            if (document is null)
            {
                return false;
            }

            options = new ControllerOptions
            {
                HostRoutePath = document.HostRoutePath,
                LoopbackOnly = document.LoopbackOnly,
                EnableWebUi = document.EnableWebUi,
                EnableHostRoute = document.EnableHostRoute,
                EnableHttpJson = document.EnableHttpJson,
                EnableGrpc = document.EnableGrpc,
                EnableUnixSocket = document.EnableUnixSocket,

                HttpPort = document.HttpPort,
                GrpcPort = document.GrpcPort,
                UnixSocketPath = document.UnixSocketPath,
                UnixSocketMode = document.UnixSocketMode,
                ApiKey = document.ApiKey,
                ApiScope = document.ApiScope,
                CorsAllowedOrigins = document.CorsAllowedOrigins is null
                    ? ImmutableArray.Create("*")
                    : ImmutableArray.CreateRange(document.CorsAllowedOrigins)
            };

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }


    /// <summary>Validates the local-only and fail-closed configuration.</summary>
    /// <returns>A safe error code that never contains option values.</returns>
    public ControllerOptionsValidationResult Validate()
    {
        if (!LoopbackOnly)
        {
            return ControllerOptionsValidationResult.Invalid(ControllerOptionsError.NonLocalBinding);
        }

        const ControllerApiScope supportedScopes = ControllerApiScope.FullConfiguration;
        if ((ApiScope & ~supportedScopes) != ControllerApiScope.None)
        {
            return ControllerOptionsValidationResult.Invalid(ControllerOptionsError.ApiScopeInvalid);
        }

        if (ApiScope == ControllerApiScope.None && RequiresApiKey)
        {
            return ControllerOptionsValidationResult.Invalid(ControllerOptionsError.ApiScopeRequired);
        }

        if (EnableHostRoute && (ApiScope & ControllerApiScope.FullConfiguration) == 0)
        {
            return ControllerOptionsValidationResult.Invalid(ControllerOptionsError.ApiScopeRequired);
        }

        if (RequiresApiKey)
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                return ControllerOptionsValidationResult.Invalid(ControllerOptionsError.ApiKeyRequired);
            }
            if (!HasStrongApiKey)
            {
                return ControllerOptionsValidationResult.Invalid(ControllerOptionsError.ApiKeyWeak);
            }
        }

        if (CorsAllowedOrigins.Any(static origin => !IsValidCorsOrigin(origin)))
        {
            return ControllerOptionsValidationResult.Invalid(ControllerOptionsError.CorsOriginInvalid);
        }

        if (EnableHostRoute && string.IsNullOrWhiteSpace(HostRoutePath))
        {
            return ControllerOptionsValidationResult.Invalid(ControllerOptionsError.HostRoutePathRequired);
        }

        if (EnableHostRoute && (HostRoutePath is null || !IsCanonicalManagementPath(HostRoutePath)))
        {
            return ControllerOptionsValidationResult.Invalid(ControllerOptionsError.HostRoutePathInvalid);
        }

        if (HttpPort is <= 0 or > 65535)
        {
            return ControllerOptionsValidationResult.Invalid(ControllerOptionsError.HttpPortInvalid);
        }

        if (EnableHttpJson && HttpPort is null)
        {
            return ControllerOptionsValidationResult.Invalid(ControllerOptionsError.HttpPortRequired);
        }

        if (GrpcPort is <= 0 or > 65535)
        {
            return ControllerOptionsValidationResult.Invalid(ControllerOptionsError.GrpcPortInvalid);
        }

        if (EnableGrpc && GrpcPort is null)
        {
            return ControllerOptionsValidationResult.Invalid(ControllerOptionsError.GrpcPortRequired);
        }

        var socketPath = UnixSocketPath;
        if (EnableUnixSocket && string.IsNullOrWhiteSpace(socketPath))
        {
            return ControllerOptionsValidationResult.Invalid(ControllerOptionsError.UnixSocketPathRequired);
        }

        if (EnableUnixSocket && (socketPath is null || !IsValidUnixSocketPath(socketPath)))
        {
            return ControllerOptionsValidationResult.Invalid(ControllerOptionsError.UnixSocketPathInvalid);
        }

        if (EnableUnixSocket && UnixSocketMode != RequiredUnixSocketMode)
        {
            return ControllerOptionsValidationResult.Invalid(ControllerOptionsError.UnixSocketModeInvalid);
        }

        return ControllerOptionsValidationResult.Valid;
    }

    private static bool IsValidCorsOrigin(string origin)
    {
        if (string.Equals(origin, "*", StringComparison.Ordinal))
        {
            return true;
        }

        return origin.Length is > 0 and <= 255 &&
            !origin.EndsWith('/') &&
            Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            !string.IsNullOrEmpty(uri.Host) &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal) &&
            string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment);
    }

    private static bool IsStrongApiKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        key.Length >= MinimumApiKeyLength &&
        key.Length <= MaximumApiKeyLength &&
        key.All(static character => !char.IsWhiteSpace(character));

    private sealed class ControllerSettingsDocument
    {
        [JsonPropertyName("loopbackOnly")]
        public bool LoopbackOnly { get; init; } = true;

        [JsonPropertyName("enableWebUi")]
        public bool EnableWebUi { get; init; }

        [JsonPropertyName("enableHostRoute")]
        public bool EnableHostRoute { get; init; }

        [JsonPropertyName("enableHttpJson")]
        public bool EnableHttpJson { get; init; }

        [JsonPropertyName("enableGrpc")]
        public bool EnableGrpc { get; init; }

        [JsonPropertyName("enableUnixSocket")]
        public bool EnableUnixSocket { get; init; }

        [JsonPropertyName("httpPort")]
        public int? HttpPort { get; init; }

        [JsonPropertyName("grpcPort")]
        public int? GrpcPort { get; init; }

        [JsonPropertyName("unixSocketPath")]
        public string? UnixSocketPath { get; init; }

        [JsonPropertyName("hostRoutePath")]
        public string? HostRoutePath { get; init; }

        [JsonPropertyName("unixSocketMode")]
        public int UnixSocketMode { get; init; } = RequiredUnixSocketMode;

        [JsonPropertyName("apiKey")]
        public string? ApiKey { get; init; }

        [JsonPropertyName("apiScope")]
        public ControllerApiScope ApiScope { get; init; } = ControllerApiScope.FullConfiguration;
        [JsonPropertyName("corsAllowedOrigins")]
        public string[]? CorsAllowedOrigins { get; init; }
    }

    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static bool IsCanonicalManagementPath(string path) =>
        path.Length is > 1 and <= 1024 &&
        path[0] == '/' &&
        !path.EndsWith('/') &&
        !path.Contains("//", StringComparison.Ordinal) &&
        path.IndexOfAny(['?', '#', '\0', '\r', '\n']) < 0;

    private static bool IsValidUnixSocketPath(string path)
    {
        try
        {
            if (!Path.IsPathRooted(path))
            {
                return false;
            }

            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) &&
                !string.Equals(root, path, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
