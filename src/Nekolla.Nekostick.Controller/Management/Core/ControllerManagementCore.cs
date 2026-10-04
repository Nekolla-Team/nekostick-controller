using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Translates REST resource requests into the Host 1.3 full-configuration facade.</summary>
internal sealed partial class ControllerManagementCore
{
    private readonly ControllerOptions _options;
    private readonly IExtensionHostBridge? _bridge;
    private readonly Func<long, CancellationToken, ValueTask<ControllerManagementResponse>>? _reloadHandler;
    private readonly Func<CancellationToken, ValueTask<ControllerStateDto?>>? _stateProvider;
    private readonly Func<CancellationToken, ValueTask<ControllerTelemetryDto>>? _telemetryProvider;
    private readonly SemaphoreSlim? _mutationGate;
    private readonly Func<ControllerRuntimeStateFeed?>? _runtimeFeedProvider;
    private readonly Func<bool>? _admissionProbe;
    private const int MaximumLoggedDispatchFailures = 256;
    private readonly HashSet<(string Method, string? Path, string ExceptionType)> _loggedDispatchFailures = new();
    private static readonly SearchValues<char> InvalidPathCharacters = SearchValues.Create("?#\0");

    internal ControllerManagementCore(
        ControllerOptions options,
        IExtensionHostBridge? bridge,
        Func<long, CancellationToken, ValueTask<ControllerManagementResponse>>? reloadHandler = null,
        Func<CancellationToken, ValueTask<ControllerStateDto?>>? stateProvider = null,
        SemaphoreSlim? mutationGate = null,
        Func<bool>? admissionProbe = null,
        Func<CancellationToken, ValueTask<ControllerTelemetryDto>>? telemetryProvider = null,
        Func<ControllerRuntimeStateFeed?>? runtimeFeedProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _bridge = bridge;
        _reloadHandler = reloadHandler;
        _stateProvider = stateProvider;
        _mutationGate = mutationGate;
        _admissionProbe = admissionProbe;
        _telemetryProvider = telemetryProvider;
        _runtimeFeedProvider = runtimeFeedProvider;
    }
    /// <summary>
    /// Writes a correlated summary for every dispatch failure and a full trace for the first
    /// occurrence of each method, path, and exception type.
    /// </summary>
    private void LogDispatchFailure(string method, string? path, string traceId, Exception exception)
    {
        if (_bridge is not IExtensionHostBridge13 { LogWriter: { } logWriter }) return;
        var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        var key = (method, path, exceptionType);
        var firstForKey = false;
        lock (_loggedDispatchFailures)
        {
            if (!_loggedDispatchFailures.Contains(key))
            {
                if (_loggedDispatchFailures.Count >= MaximumLoggedDispatchFailures)
                {
                    _loggedDispatchFailures.Clear();
                }

                _loggedDispatchFailures.Add(key);
                firstForKey = true;
            }
        }

        logWriter.WriteText(
            ExtensionLogLevel.Warning,
            $"Management dispatch failed; traceId={traceId}; method={SingleLine(method)}; path={SingleLine(path ?? "<unknown>")}; exceptionType={exceptionType}");
        if (firstForKey)
        {
            logWriter.WriteText(ExtensionLogLevel.Warning, $"First failure for {exceptionType}; traceId={traceId}: {exception}");
        }
    }

    private static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    internal static string CreateTraceId()
    {
        var activityTraceId = System.Diagnostics.Activity.Current?.TraceId;
        return activityTraceId is { } traceId && !traceId.Equals(default(System.Diagnostics.ActivityTraceId))
            ? traceId.ToString()
            : Guid.NewGuid().ToString("N");
    }

    private ControllerManagementResponse UnexpectedFailure(string method, string? path, Exception exception)
    {
        var traceId = CreateTraceId();
        LogDispatchFailure(method, path, traceId, exception);
        return ControllerManagementResponseBuilder.Error(503, ControllerDispatchCode.Unavailable, "unavailable", $"The {method} management request for '{path ?? "the requested path"}' failed unexpectedly. Use traceId '{traceId}' to find the server log entry.", details: new ControllerErrorDetails { Reason = "unexpected_exception", TraceId = traceId });
    }

    private static ControllerManagementResponse InvalidArgumentFailure(ArgumentException exception)
    {
        var parameter = string.IsNullOrEmpty(exception.ParamName) ? null : exception.ParamName;
        var message = parameter is null
            ? "A management request argument is invalid."
            : $"The '{parameter}' parameter is invalid.";
        return ControllerManagementResponseBuilder.InvalidRequest("invalid_argument", message, parameter);
    }

    internal async ValueTask<ControllerManagementResponse> DispatchAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_bridge is null) return ControllerManagementResponseBuilder.Unavailable(reason: "bridge_unavailable", message: "The host management bridge is unavailable.");
        if (!HasFullConfigurationScope() || !ExtensionHostApiSupport.IsApi13Supported(_bridge.ApiVersion)) return ControllerManagementResponseBuilder.Unsupported(reason: "host_api_unsupported", message: "This host does not support full-configuration management.");

        var method = request.Method.Trim().ToUpperInvariant();
        var path = NormalizePath(request.Path, request.Transport);
        if (path is null) return ControllerManagementResponseBuilder.InvalidRequest("invalid_path", "The request path must be a valid absolute management path.", "path");
        if (method.Length == 0) return ControllerManagementResponseBuilder.InvalidRequest("missing_method", "The management method is required.", "method");

        try
        {
            if (string.Equals(method, "GET", StringComparison.Ordinal) ||
                string.Equals(path, ControllerManagementApiContract.ReloadSettingsPath, StringComparison.Ordinal) ||
                _mutationGate is null)
            {
                return await RouteAsync(path, method, request, cancellationToken).ConfigureAwait(false);
            }

            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_admissionProbe is not null && !_admissionProbe())
                {
                    return ControllerManagementResponseBuilder.Unavailable(reason: "admission_closed", message: "The controller is not accepting management mutations.");
                }

                return await RouteAsync(path, method, request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _mutationGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            return InvalidArgumentFailure(exception);
        }
        catch (NotSupportedException)
        {
            return ControllerManagementResponseBuilder.Unsupported(reason: "host_api_unsupported", message: "The requested management operation is not supported by this host.");
        }
        catch (Exception exception)
        {
            return UnexpectedFailure(method, path, exception);
        }
    }

    /// <summary>
    /// Dispatches one streaming management request whose body is a live transport stream. Only the
    /// extension install endpoint is reachable here; every other path is invalid on this channel.
    /// </summary>
    internal async ValueTask<ControllerManagementResponse> DispatchStreamingAsync(
        ControllerManagementRequest request,
        Stream body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(body);
        cancellationToken.ThrowIfCancellationRequested();
        if (_bridge is null) return ControllerManagementResponseBuilder.Unavailable(reason: "bridge_unavailable", message: "The host extension-management bridge is unavailable.");
        if (!HasFullConfigurationScope() || !ExtensionHostApiSupport.IsApi13Supported(_bridge.ApiVersion)) return ControllerManagementResponseBuilder.Unsupported(reason: "host_api_unsupported", message: "This host does not support extension installation through the management API.");

        var method = request.Method.Trim().ToUpperInvariant();
        var path = NormalizePath(request.Path, request.Transport);
        if (path is null) return ControllerManagementResponseBuilder.InvalidRequest("invalid_path", "The request path must be a valid absolute management path.", "path");
        if (method.Length == 0) return ControllerManagementResponseBuilder.InvalidRequest("missing_method", "The management method is required.", "method");
        if (path != ControllerManagementApiContract.ExtensionsInstallPath) return ControllerManagementResponseBuilder.NotFound(reason: "unknown_path", message: $"The streaming install endpoint is not available at '{path}'.", parameter: path);
        if (!string.Equals(method, "POST", StringComparison.Ordinal)) return MethodNotAllowedResponse(method);

        try
        {
            if (_mutationGate is not null)
            {
                await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                if (_admissionProbe is not null && !_admissionProbe())
                {
                    return ControllerManagementResponseBuilder.Unavailable(reason: "admission_closed", message: "The controller is not accepting management mutations.");
                }

                return await InstallExtensionAsync(body, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _mutationGate?.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            return InvalidArgumentFailure(exception);
        }
        catch (NotSupportedException)
        {
            return ControllerManagementResponseBuilder.Unsupported(reason: "host_api_unsupported", message: "The requested extension installation operation is not supported by this host.");
        }
        catch (Exception exception)
        {
            return UnexpectedFailure(method, path, exception);
        }
    }

    private async ValueTask<ControllerManagementResponse> RouteAsync(
        string path,
        string method,
        ControllerManagementRequest request,
        CancellationToken cancellationToken)
    {
        if (path == ControllerManagementApiContract.StatePath)
            return method == "GET" ? await ReadStateAsync(request, cancellationToken).ConfigureAwait(false) : MethodNotAllowedResponse(method);
        if (path == ControllerManagementApiContract.TelemetryPath)
            return method == "GET" ? await ReadTelemetryAsync(request, cancellationToken).ConfigureAwait(false) : MethodNotAllowedResponse(method);
        if (path == ControllerManagementApiContract.ReloadSettingsPath)
            return method == "POST" ? await ReloadSettingsAsync(request, cancellationToken).ConfigureAwait(false) : MethodNotAllowedResponse(method);
        if (path == ControllerManagementApiContract.RootPath)
            return method == "GET" ? await ReadRootAsync(request, cancellationToken).ConfigureAwait(false) : MethodNotAllowedResponse(method);
        if (path == ControllerManagementApiContract.GlobalSettingsPath)
            return method switch
            {
                "GET" => await ReadGlobalSettingsAsync(request, cancellationToken).ConfigureAwait(false),
                "PATCH" => await PatchGlobalSettingsAsync(request, cancellationToken).ConfigureAwait(false),
                _ => MethodNotAllowedResponse(method)
            };
        if (path == ControllerManagementApiContract.RoutesPath)
            return method switch
            {
                "GET" => await ReadRoutesAsync(request, cancellationToken).ConfigureAwait(false),
                "POST" => await CreateRouteAsync(request, cancellationToken).ConfigureAwait(false),
                _ => MethodNotAllowedResponse(method)
            };
        if (path == ControllerManagementApiContract.ServicesRuntimePath)
            return method == "GET" ? await ReadServiceRuntimesAsync(request, cancellationToken).ConfigureAwait(false) : MethodNotAllowedResponse(method);

        if (path == ControllerManagementApiContract.ServicesPath)
            return method switch
            {
                "GET" => await ReadServicesAsync(request, cancellationToken).ConfigureAwait(false),
                "POST" => await CreateServiceAsync(request, cancellationToken).ConfigureAwait(false),
                _ => MethodNotAllowedResponse(method)
            };
        if (path == ControllerManagementApiContract.ExtensionsPath)
            return method == "GET" ? await ReadExtensionsAsync(request, cancellationToken).ConfigureAwait(false) : MethodNotAllowedResponse(method);

        if (TryGetEnvironmentPath(path, out var environmentServiceId))
            return method switch
            {
                "GET" => await ReadEnvironmentAsync(request, environmentServiceId, cancellationToken).ConfigureAwait(false),
                "PUT" => await PutEnvironmentAsync(request, environmentServiceId, cancellationToken).ConfigureAwait(false),
                "DELETE" => await DeleteEnvironmentAsync(request, environmentServiceId, cancellationToken).ConfigureAwait(false),
                _ => MethodNotAllowedResponse(method)
            };
        if (TryGetSettingsPath(path, out var settingsExtensionId))
            return method switch
            {
                "GET" => await ReadExtensionSettingsAsync(request, settingsExtensionId, cancellationToken).ConfigureAwait(false),
                "PUT" => await PutExtensionSettingsAsync(request, settingsExtensionId, cancellationToken).ConfigureAwait(false),
                "DELETE" => await DeleteExtensionSettingsAsync(request, settingsExtensionId, cancellationToken).ConfigureAwait(false),
                _ => MethodNotAllowedResponse(method)
            };
        if (TryGetMemberPath(path, ControllerManagementApiContract.RoutesPath, out var routeId))
            return method switch
            {
                "GET" => await ReadRouteAsync(request, routeId, cancellationToken).ConfigureAwait(false),
                "PATCH" => await PatchRouteAsync(request, routeId, cancellationToken).ConfigureAwait(false),
                "DELETE" => await DeleteRouteAsync(request, routeId, cancellationToken).ConfigureAwait(false),
                _ => MethodNotAllowedResponse(method)
            };
        if (TryGetServiceRuntimeActionPath(path, out var runtimeActionServiceId, out var runtimeAction))
            return await WriteServiceRuntimeAsync(request, method, runtimeActionServiceId, runtimeAction, cancellationToken).ConfigureAwait(false);
        if (TryGetServiceRuntimePath(path, out var runtimeServiceId))
            return method == "GET" ? await ReadServiceRuntimeAsync(request, runtimeServiceId, cancellationToken).ConfigureAwait(false) : MethodNotAllowedResponse(method);

        if (TryGetMemberPath(path, ControllerManagementApiContract.ServicesPath, out var serviceId))
            return method switch
            {
                "GET" => await ReadServiceAsync(request, serviceId, cancellationToken).ConfigureAwait(false),
                "PATCH" => await PatchServiceAsync(request, serviceId, cancellationToken).ConfigureAwait(false),
                "DELETE" => await DeleteServiceAsync(request, serviceId, cancellationToken).ConfigureAwait(false),
                _ => MethodNotAllowedResponse(method)
            };
        if (path == ControllerManagementApiContract.ExtensionsRefreshPath)
            return method == "POST" ? await RefreshExtensionsAsync(request, cancellationToken).ConfigureAwait(false) : MethodNotAllowedResponse(method);
        if (path == ControllerManagementApiContract.ExtensionsInstallPath)
            return method == "POST"
                ? ControllerManagementResponseBuilder.Unsupported(reason: "streaming_required", message: "Extension package installation requires a streaming request.")
                : MethodNotAllowedResponse(method);

        if (TryGetExtensionActionPath(path, out var actionExtensionId, out var extensionAction))
            return await WriteExtensionLifecycleAsync(request, method, actionExtensionId, extensionAction, cancellationToken).ConfigureAwait(false);

        if (TryGetExtensionMemberPath(path, out var extensionId))
            return method == "GET" ? await ReadExtensionAsync(request, extensionId, cancellationToken).ConfigureAwait(false) : MethodNotAllowedResponse(method);

        if (HasInvalidRouteId(path)) return ControllerManagementResponseBuilder.InvalidRequest("invalid_id", "The route identifier must be a valid GUID.", "id");
        if (HasInvalidServiceId(path)) return ControllerManagementResponseBuilder.InvalidRequest("invalid_id", "The service identifier must be a valid GUID.", "id");
        return ControllerManagementResponseBuilder.NotFound(reason: "unknown_path", message: $"No management operation is registered for path '{path}'.", parameter: path);
    }

    private async ValueTask<ControllerManagementResponse> ReadStateAsync(
        ControllerManagementRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ValidateNoBodyRequest(request) is { } validationError) return validationError;

        var state = _stateProvider is null
            ? null
            : await _stateProvider(cancellationToken).ConfigureAwait(false);
        return state is null
            ? ControllerManagementResponseBuilder.Unavailable(reason: "state_provider_unavailable", message: "The controller runtime state provider is unavailable.")
            : ControllerManagementResponseBuilder.SuccessUnversioned(state);
    }

    private async ValueTask<ControllerManagementResponse> ReloadSettingsAsync(
        ControllerManagementRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition))
        {
            return precondition!;
        }

        if (!RequireEmptyBody(request)) return UnexpectedBodyResponse();

        return _reloadHandler is null
            ? ControllerManagementResponseBuilder.Unavailable(reason: "reload_handler_unavailable", message: "The controller reload handler is unavailable.")
            : await _reloadHandler(expectedVersion, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadSnapshotAsync(CancellationToken cancellationToken) => _bridge!.FullConfiguration.ReadAsync(cancellationToken);
    private ValueTask<ConfigurationWriteResult> ReplaceAsync(long expectedVersion, ConfigurationChangeSet changes, CancellationToken cancellationToken) => _bridge!.FullConfiguration.ReplaceAsync(expectedVersion, changes, cancellationToken);

    private async ValueTask<ControllerManagementResponse> ReadTelemetryAsync(
        ControllerManagementRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ValidateNoBodyRequest(request) is { } validationError) return validationError;

        return _telemetryProvider is null
            ? ControllerManagementResponseBuilder.Unavailable(reason: "telemetry_provider_unavailable", message: "The controller telemetry provider is unavailable.")
            : ControllerManagementResponseBuilder.SuccessUnversioned(await _telemetryProvider(cancellationToken).ConfigureAwait(false));
    }
    private static ConfigurationChangeSet NewChanges(HostConfigurationSnapshot snapshot, GlobalSettingsConfiguration? globalSettings = null, ImmutableArray<RouteConfiguration>? routes = null, ImmutableArray<ServiceConfiguration>? services = null, ImmutableArray<ExtensionRecordConfiguration>? extensionRecords = null, ImmutableArray<ExtensionSettingsConfiguration>? extensionSettings = null) => new(globalSettings ?? snapshot.GlobalSettings, routes ?? snapshot.Routes, services ?? snapshot.Services, extensionRecords ?? snapshot.ExtensionRecords, extensionSettings ?? snapshot.ExtensionSettings);
    private bool HasFullConfigurationScope() => HasFullConfigurationScope(_options);
    private static bool HasFullConfigurationScope(ControllerOptions options) =>
        (options.ApiScope & ControllerApiScope.FullConfiguration) == ControllerApiScope.FullConfiguration;

    private string? NormalizePath(string rawPath, ControllerTransport transport)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Length > 8192 || rawPath.AsSpan().IndexOfAny(InvalidPathCharacters) >= 0 || !rawPath.StartsWith('/')) return null;
        var path = rawPath;
        while (path.Length > 1 && path.EndsWith('/')) path = path[..^1];
        if (transport == ControllerTransport.HostRoute && _options.EnableHostRoute)
        {
            var hostPrefix = _options.HostRoutePath;
            if (hostPrefix is null || !ControllerOptions.IsCanonicalManagementPath(hostPrefix)) return null;
            if (!string.Equals(hostPrefix, ControllerManagementApiContract.RootPath, StringComparison.Ordinal))
            {
                var prefixedPath = hostPrefix + ControllerManagementApiContract.RootPath;
                if (string.Equals(path, prefixedPath, StringComparison.Ordinal)) path = ControllerManagementApiContract.RootPath;
                else if (path.StartsWith(prefixedPath + "/", StringComparison.Ordinal)) path = path[hostPrefix.Length..];
            }
        }
        return path;
    }

    private bool IsReservedRoute(RouteConfiguration route)
    {
        if (route.Target is ExtensionHandlerRouteTargetConfiguration handler && string.Equals(handler.HandlerId, ControllerManagementApiContract.HandlerId, StringComparison.Ordinal)) return true;
        return _options.HostRoutePath is { } reservedPath && (string.Equals(route.Matcher.Pattern, reservedPath, StringComparison.Ordinal) || route.Matcher.Pattern.StartsWith(reservedPath + "/", StringComparison.Ordinal));
    }

    private bool IsReservedRoutePayload(ControllerRouteWriteDto route)
    {
        if (route.Target is { Type: ControllerRouteTargetKind.ExtensionHandler, HandlerId: not null } && string.Equals(route.Target.HandlerId, ControllerManagementApiContract.HandlerId, StringComparison.Ordinal)) return true;
        return _options.HostRoutePath is { } reservedPath && route.Matcher is not null && (string.Equals(route.Matcher.Pattern, reservedPath, StringComparison.Ordinal) || route.Matcher.Pattern.StartsWith(reservedPath + "/", StringComparison.Ordinal));
    }

    private static bool RequireEmptyBody(ControllerManagementRequest request) => request.Body.IsEmpty;
    private static bool RequireNoIfMatch(ControllerManagementRequest request) => !request.Headers.ContainsKey(ControllerManagementApiContract.IfMatchHeaderName);
    private static ControllerManagementResponse UnexpectedBodyResponse() => ControllerManagementResponseBuilder.InvalidRequest("unexpected_body", "This management operation does not accept a request body.", "body");
    private static ControllerManagementResponse? ValidateNoBodyRequest(ControllerManagementRequest request)
    {
        if (!RequireNoIfMatch(request)) return ControllerManagementResponseBuilder.InvalidRequest("invalid_header", "The If-Match header is not allowed for this read operation.", "If-Match");
        return RequireEmptyBody(request) ? null : UnexpectedBodyResponse();
    }
    private static ControllerManagementResponse? ValidateJsonBody(ControllerManagementRequest request, bool mergePatch = false)
    {
        if (request.Body.IsEmpty) return ControllerManagementResponseBuilder.InvalidRequest("empty_body", "The request body must contain a JSON document.", "body");
        if (request.Body.Length > ControllerAdmissionLimits.MaximumRequestBodyBytes) return ControllerManagementResponseBuilder.InvalidRequest("body_too_large", $"The request body must not exceed {ControllerAdmissionLimits.MaximumRequestBodyBytes} bytes.", "body");
        if (!request.Headers.TryGetValue("content-type", out var values)) return ControllerManagementResponseBuilder.InvalidRequest("invalid_header", "The Content-Type header is required for a JSON request body.", "Content-Type");
        if (values.Length != 1) return ControllerManagementResponseBuilder.InvalidRequest("invalid_header", "The Content-Type header must contain exactly one value.", "Content-Type");
        var mediaType = values[0].Split(';', 2)[0].Trim();
        return string.Equals(mediaType, ControllerManagementApiContract.JsonMediaType, StringComparison.OrdinalIgnoreCase) || (mergePatch && string.Equals(mediaType, "application/merge-patch+json", StringComparison.OrdinalIgnoreCase))
            ? null
            : ControllerManagementResponseBuilder.InvalidRequest("unsupported_media_type", $"The Content-Type header must be {ControllerManagementApiContract.JsonMediaType}{(mergePatch ? " or application/merge-patch+json" : string.Empty)}.", "Content-Type");
    }

    private static ControllerManagementResponse MethodNotAllowedResponse(string method) => ControllerManagementResponseBuilder.MethodNotAllowed(message: $"The {method} method is not allowed for this management path.", parameter: method);

    private static bool TryReadIfMatch(ControllerManagementRequest request, out long version, out ControllerManagementResponse? error)
    {
        version = 0;
        error = null;
        if (!request.Headers.TryGetValue(ControllerManagementApiContract.IfMatchHeaderName, out var values))
        {
            error = ControllerManagementResponseBuilder.PreconditionRequired("if_match_missing", "The If-Match header is required for this versioned operation.", "If-Match");
            return false;
        }
        if (values.Length > 1)
        {
            error = ControllerManagementResponseBuilder.InvalidRequest("if_match_multiple", "The If-Match header must contain exactly one version.", "If-Match");
            return false;
        }
        if (values.Length == 0)
        {
            error = ControllerManagementResponseBuilder.InvalidRequest("if_match_malformed", "The If-Match header must contain one quoted version.", "If-Match");
            return false;
        }
        var value = values[0];
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') { error = MalformedIfMatch(); return false; }
        var text = value[1..^1];
        if (text.Length == 0 || (text.Length > 1 && text[0] == '0')) { error = MalformedIfMatch(); return false; }
        foreach (var character in text) if (character is < '0' or > '9') { error = MalformedIfMatch(); return false; }
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out version)) { error = MalformedIfMatch(); return false; }
        return true;
    }
    private static ControllerManagementResponse MalformedIfMatch() => ControllerManagementResponseBuilder.InvalidRequest("if_match_malformed", "The If-Match header must contain one quoted, non-negative version.", "If-Match");
    private static ControllerManagementResponse InvalidJsonBodyResponse(ImmutableArray<byte> body, string resource) =>
        IsValidJsonBody(body)
            ? ControllerManagementResponseBuilder.InvalidRequest("invalid_body", $"The JSON body does not match the expected {resource} request fields.", "body")
            : ControllerManagementResponseBuilder.InvalidRequest("malformed_json", $"The request body must contain valid JSON for a {resource} resource.", "body");
    private static ControllerManagementResponse InvalidMergePatchResponse(ImmutableArray<byte> body, string resource) =>
        IsValidJsonBody(body)
            ? ControllerManagementResponseBuilder.InvalidRequest("invalid_body", $"The merge-patch body must be a JSON object containing supported {resource} fields.", "body")
            : ControllerManagementResponseBuilder.InvalidRequest("malformed_json", $"The merge-patch body for {resource} must be valid JSON.", "body");
    private static bool IsValidJsonBody(ImmutableArray<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(ControllerManagementJson.AsReadOnlyMemory(body), new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            return true;
        }
        catch (JsonException) { return false; }
    }
    private static ControllerManagementResponse MissingFieldResponse(string field) => ControllerManagementResponseBuilder.InvalidRequest("missing_field", $"The '{field}' field is required.", field);

    private static bool PatchContainsProperty(ImmutableArray<byte> body, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(ControllerManagementJson.AsReadOnlyMemory(body), new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.EnumerateObject().Any(property => string.Equals(property.Name, propertyName, StringComparison.Ordinal));
        }
        catch (JsonException) { return false; }
    }

    private static bool TryMergePatch<T>(T current, ReadOnlyMemory<byte> patchBytes, out T? result)
    {
        result = default;
        try
        {
            var patch = JsonNode.Parse(Encoding.UTF8.GetString(patchBytes.Span), nodeOptions: null, documentOptions: new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (patch is not JsonObject) return false;
            var target = JsonSerializer.SerializeToNode(current, ControllerManagementJson.Options);
            if (target is not JsonObject targetObject || !ValidatePatchShape(patch.AsObject(), targetObject)) return false;
            var merged = MergePatch(targetObject, patch);
            if (merged is not JsonObject) return false;
            var bytes = Encoding.UTF8.GetBytes(merged.ToJsonString(ControllerManagementJson.Options));
            return ControllerManagementJson.TryDeserialize(bytes, out result);
        }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static JsonNode? MergePatch(JsonNode? target, JsonNode? patch)
    {
        if (patch is null || patch is not JsonObject patchObject) return patch?.DeepClone();
        var targetObject = target as JsonObject ?? new JsonObject();
        foreach (var property in patchObject)
        {
            if (property.Value is null) targetObject.Remove(property.Key);
            else if (property.Value is JsonObject) targetObject[property.Key] = MergePatch(targetObject[property.Key], property.Value);
            else targetObject[property.Key] = property.Value.DeepClone();
        }
        return targetObject;
    }

    private static bool ValidatePatchShape(JsonObject patch, JsonObject target)
    {
        foreach (var property in patch)
        {
            if (!target.ContainsKey(property.Key)) return false;
            if (property.Value is JsonObject patchObject && target[property.Key] is JsonObject targetObject && !ValidatePatchShape(patchObject, targetObject)) return false;
        }
        return true;
    }

    private static bool TryGetMemberPath(string path, string prefix, out Guid id)
    {
        id = default;
        if (!path.StartsWith(prefix + "/", StringComparison.Ordinal)) return false;
        var suffix = path[(prefix.Length + 1)..];
        return suffix.Length > 0 && !suffix.Contains('/', StringComparison.Ordinal) && Guid.TryParse(suffix, out id);
    }

    private static bool TryGetServiceRuntimeActionPath(string path, out Guid id, out string action)
    {
        id = default;
        action = string.Empty;
        var prefix = ControllerManagementApiContract.ServicesPath + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = path[prefix.Length..];
        const string tail = "/runtime/";
        var separator = suffix.IndexOf(tail, StringComparison.Ordinal);
        if (separator <= 0) return false;
        var idText = suffix[..separator];
        var actionText = suffix[(separator + tail.Length)..];
        if (idText.Contains('/', StringComparison.Ordinal) || !Guid.TryParse(idText, out id)) return false;
        if (actionText is not ("resume" or "restart") || actionText.Contains('/', StringComparison.Ordinal))
        {
            id = default;
            return false;
        }

        action = actionText;
        return true;
    }

    private static bool TryGetServiceRuntimePath(string path, out Guid id)
    {
        id = default;
        var prefix = ControllerManagementApiContract.ServicesPath + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = path[prefix.Length..];
        const string tail = "/runtime";
        if (!suffix.EndsWith(tail, StringComparison.Ordinal)) return false;
        var idText = suffix[..^tail.Length];
        return idText.Length > 0 && !idText.Contains('/', StringComparison.Ordinal) && Guid.TryParse(idText, out id);
    }

    private static bool TryGetEnvironmentPath(string path, out Guid id)
    {
        id = default;
        var prefix = ControllerManagementApiContract.ServicesPath + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = path[prefix.Length..];
        const string tail = "/environment";
        if (!suffix.EndsWith(tail, StringComparison.Ordinal)) return false;
        var idText = suffix[..^tail.Length];
        return idText.Length > 0 && !idText.Contains('/', StringComparison.Ordinal) && Guid.TryParse(idText, out id);
    }

    private static bool TryGetSettingsPath(string path, out string extensionId)
    {
        extensionId = string.Empty;
        var prefix = ControllerManagementApiContract.ExtensionsPath + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = path[prefix.Length..];
        const string tail = "/settings";
        if (!suffix.EndsWith(tail, StringComparison.Ordinal)) return false;
        extensionId = suffix[..^tail.Length];
        return extensionId.Length > 0 && !extensionId.Contains('/', StringComparison.Ordinal);
    }

    private static bool TryGetExtensionMemberPath(string path, out string extensionId)
    {
        extensionId = string.Empty;
        var prefix = ControllerManagementApiContract.ExtensionsPath + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        extensionId = path[prefix.Length..];
        return extensionId.Length > 0 && !extensionId.Contains('/', StringComparison.Ordinal);
    }

    private static bool HasInvalidRouteId(string path)
    {
        var prefix = ControllerManagementApiContract.RoutesPath + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var id = path[prefix.Length..];
        return id.Length > 0 && !id.Contains('/', StringComparison.Ordinal) && !Guid.TryParse(id, out _);
    }
    private static bool HasInvalidServiceId(string path)
    {
        var prefix = ControllerManagementApiContract.ServicesPath + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = path[prefix.Length..];
        var separator = suffix.IndexOf('/');
        var id = separator < 0 ? suffix : suffix[..separator];
        var remainder = separator < 0 ? string.Empty : suffix[separator..];
        if (id.Length == 0 || Guid.TryParse(id, out _)) return false;
        return remainder is "" or "/environment" or "/runtime" or "/runtime/resume" or "/runtime/restart";
    }
}
