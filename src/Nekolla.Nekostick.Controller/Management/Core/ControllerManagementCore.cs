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
    private readonly SemaphoreSlim? _mutationGate;
    private readonly Func<bool>? _admissionProbe;
    private static readonly SearchValues<char> InvalidPathCharacters = SearchValues.Create("?#\0");

    internal ControllerManagementCore(
        ControllerOptions options,
        IExtensionHostBridge? bridge,
        Func<long, CancellationToken, ValueTask<ControllerManagementResponse>>? reloadHandler = null,
        Func<CancellationToken, ValueTask<ControllerStateDto?>>? stateProvider = null,
        SemaphoreSlim? mutationGate = null,
        Func<bool>? admissionProbe = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _bridge = bridge;
        _reloadHandler = reloadHandler;
        _stateProvider = stateProvider;
        _mutationGate = mutationGate;
        _admissionProbe = admissionProbe;
    }

    internal async ValueTask<ControllerManagementResponse> DispatchAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_bridge is null) return ControllerManagementResponseBuilder.Unavailable;
        if (!HasFullConfigurationScope() || !ExtensionAbi.IsApi13Supported(_bridge.ApiVersion)) return ControllerManagementResponseBuilder.Unsupported;

        var method = request.Method.Trim().ToUpperInvariant();
        var path = NormalizePath(request.Path, request.Transport);
        if (path is null || method.Length == 0) return ControllerManagementResponseBuilder.InvalidRequest;

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
                    return ControllerManagementResponseBuilder.Unavailable;
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
        catch (ArgumentException)
        {
            return ControllerManagementResponseBuilder.InvalidRequest;
        }
        catch (NotSupportedException)
        {
            return ControllerManagementResponseBuilder.Unsupported;
        }
        catch (InvalidOperationException)
        {
            return ControllerManagementResponseBuilder.Unavailable;
        }
        catch
        {
            return ControllerManagementResponseBuilder.Unavailable;
        }
    }

    private async ValueTask<ControllerManagementResponse> RouteAsync(
        string path,
        string method,
        ControllerManagementRequest request,
        CancellationToken cancellationToken)
    {
        if (path == ControllerManagementApiContract.StatePath)
            return method == "GET" ? await ReadStateAsync(request, cancellationToken).ConfigureAwait(false) : ControllerManagementResponseBuilder.MethodNotAllowed;
        if (path == ControllerManagementApiContract.ReloadSettingsPath)
            return method == "POST" ? await ReloadSettingsAsync(request, cancellationToken).ConfigureAwait(false) : ControllerManagementResponseBuilder.MethodNotAllowed;
        if (path == ControllerManagementApiContract.RootPath)
            return method == "GET" ? await ReadRootAsync(request, cancellationToken).ConfigureAwait(false) : ControllerManagementResponseBuilder.MethodNotAllowed;
        if (path == ControllerManagementApiContract.GlobalSettingsPath)
            return method switch
            {
                "GET" => await ReadGlobalSettingsAsync(request, cancellationToken).ConfigureAwait(false),
                "PATCH" => await PatchGlobalSettingsAsync(request, cancellationToken).ConfigureAwait(false),
                _ => ControllerManagementResponseBuilder.MethodNotAllowed
            };
        if (path == ControllerManagementApiContract.RoutesPath)
            return method switch
            {
                "GET" => await ReadRoutesAsync(request, cancellationToken).ConfigureAwait(false),
                "POST" => await CreateRouteAsync(request, cancellationToken).ConfigureAwait(false),
                _ => ControllerManagementResponseBuilder.MethodNotAllowed
            };
        if (path == ControllerManagementApiContract.ServicesRuntimePath)
            return method == "GET" ? await ReadServiceRuntimesAsync(request, cancellationToken).ConfigureAwait(false) : ControllerManagementResponseBuilder.MethodNotAllowed;

        if (path == ControllerManagementApiContract.ServicesPath)
            return method switch
            {
                "GET" => await ReadServicesAsync(request, cancellationToken).ConfigureAwait(false),
                "POST" => await CreateServiceAsync(request, cancellationToken).ConfigureAwait(false),
                _ => ControllerManagementResponseBuilder.MethodNotAllowed
            };
        if (path == ControllerManagementApiContract.ExtensionsPath)
            return method == "GET" ? await ReadExtensionsAsync(request, cancellationToken).ConfigureAwait(false) : ControllerManagementResponseBuilder.MethodNotAllowed;

        if (TryGetEnvironmentPath(path, out var environmentServiceId))
            return method switch
            {
                "GET" => await ReadEnvironmentAsync(request, environmentServiceId, cancellationToken).ConfigureAwait(false),
                "PUT" => await PutEnvironmentAsync(request, environmentServiceId, cancellationToken).ConfigureAwait(false),
                "DELETE" => await DeleteEnvironmentAsync(request, environmentServiceId, cancellationToken).ConfigureAwait(false),
                _ => ControllerManagementResponseBuilder.MethodNotAllowed
            };
        if (TryGetSettingsPath(path, out var settingsExtensionId))
            return method switch
            {
                "GET" => await ReadExtensionSettingsAsync(request, settingsExtensionId, cancellationToken).ConfigureAwait(false),
                "PUT" => await PutExtensionSettingsAsync(request, settingsExtensionId, cancellationToken).ConfigureAwait(false),
                "DELETE" => await DeleteExtensionSettingsAsync(request, settingsExtensionId, cancellationToken).ConfigureAwait(false),
                _ => ControllerManagementResponseBuilder.MethodNotAllowed
            };
        if (TryGetMemberPath(path, ControllerManagementApiContract.RoutesPath, out var routeId))
            return method switch
            {
                "GET" => await ReadRouteAsync(request, routeId, cancellationToken).ConfigureAwait(false),
                "PATCH" => await PatchRouteAsync(request, routeId, cancellationToken).ConfigureAwait(false),
                "DELETE" => await DeleteRouteAsync(request, routeId, cancellationToken).ConfigureAwait(false),
                _ => ControllerManagementResponseBuilder.MethodNotAllowed
            };
        if (TryGetServiceRuntimePath(path, out var runtimeServiceId))
            return method == "GET" ? await ReadServiceRuntimeAsync(request, runtimeServiceId, cancellationToken).ConfigureAwait(false) : ControllerManagementResponseBuilder.MethodNotAllowed;

        if (TryGetMemberPath(path, ControllerManagementApiContract.ServicesPath, out var serviceId))
            return method switch
            {
                "GET" => await ReadServiceAsync(request, serviceId, cancellationToken).ConfigureAwait(false),
                "PATCH" => await PatchServiceAsync(request, serviceId, cancellationToken).ConfigureAwait(false),
                "DELETE" => await DeleteServiceAsync(request, serviceId, cancellationToken).ConfigureAwait(false),
                _ => ControllerManagementResponseBuilder.MethodNotAllowed
            };
        if (path == ControllerManagementApiContract.ExtensionsRefreshPath)
            return method == "POST" ? await RefreshExtensionsAsync(request, cancellationToken).ConfigureAwait(false) : ControllerManagementResponseBuilder.MethodNotAllowed;

        if (TryGetExtensionActionPath(path, out var actionExtensionId, out var extensionAction))
            return await WriteExtensionLifecycleAsync(request, method, actionExtensionId, extensionAction, cancellationToken).ConfigureAwait(false);

        if (TryGetExtensionMemberPath(path, out var extensionId))
            return method == "GET" ? await ReadExtensionAsync(request, extensionId, cancellationToken).ConfigureAwait(false) : ControllerManagementResponseBuilder.MethodNotAllowed;

        return ControllerManagementResponseBuilder.NotFound;
    }

    private async ValueTask<ControllerManagementResponse> ReadStateAsync(
        ControllerManagementRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!RequireNoIfMatch(request) || !RequireEmptyBody(request))
        {
            return ControllerManagementResponseBuilder.InvalidRequest;
        }

        var state = _stateProvider is null
            ? null
            : await _stateProvider(cancellationToken).ConfigureAwait(false);
        return state is null
            ? ControllerManagementResponseBuilder.Unavailable
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

        if (!RequireEmptyBody(request))
        {
            return ControllerManagementResponseBuilder.InvalidRequest;
        }

        return _reloadHandler is null
            ? ControllerManagementResponseBuilder.Unavailable
            : await _reloadHandler(expectedVersion, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadSnapshotAsync(CancellationToken cancellationToken) => _bridge!.FullConfiguration.ReadAsync(cancellationToken);
    private ValueTask<ConfigurationWriteResult> ReplaceAsync(long expectedVersion, ConfigurationChangeSet changes, CancellationToken cancellationToken) => _bridge!.FullConfiguration.ReplaceAsync(expectedVersion, changes, cancellationToken);
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

    private static bool HasJsonBody(ControllerManagementRequest request, bool mergePatch = false)
    {
        if (request.Body.IsEmpty || request.Body.Length > ControllerAdmissionLimits.MaximumRequestBodyBytes || !request.Headers.TryGetValue("content-type", out var values) || values.Length != 1) return false;
        var mediaType = values[0].Split(';', 2)[0].Trim();
        return string.Equals(mediaType, ControllerManagementApiContract.JsonMediaType, StringComparison.OrdinalIgnoreCase) || (mergePatch && string.Equals(mediaType, "application/merge-patch+json", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadIfMatch(ControllerManagementRequest request, out long version, out ControllerManagementResponse? error)
    {
        version = 0;
        error = null;
        if (!request.Headers.TryGetValue(ControllerManagementApiContract.IfMatchHeaderName, out var values)) { error = ControllerManagementResponseBuilder.PreconditionRequired; return false; }
        if (values.Length != 1) { error = ControllerManagementResponseBuilder.InvalidRequest; return false; }
        var value = values[0];
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') { error = ControllerManagementResponseBuilder.InvalidRequest; return false; }
        var text = value[1..^1];
        if (text.Length == 0 || (text.Length > 1 && text[0] == '0')) { error = ControllerManagementResponseBuilder.InvalidRequest; return false; }
        foreach (var character in text) if (character is < '0' or > '9') { error = ControllerManagementResponseBuilder.InvalidRequest; return false; }
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out version)) { error = ControllerManagementResponseBuilder.InvalidRequest; return false; }
        return true;
    }

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

}
