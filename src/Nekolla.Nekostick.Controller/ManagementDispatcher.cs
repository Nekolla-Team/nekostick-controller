using Nekolla.Nekostick.Contracts;
using System.Collections.Immutable;
using System.Text;
using Nekolla.Nekostick.Controller.Adapters.Grpc;
using Nekolla.Nekostick.Controller.Adapters.HttpUnix;

namespace Nekolla.Nekostick.Controller;

/// <summary>Defines bounded request admission before transport-neutral DTO construction.</summary>
public static class ControllerAdmissionLimits
{
    /// <summary>The maximum request body copied into a management request.</summary>
    public const int MaximumRequestBodyBytes = 1024 * 1024;

    /// <summary>The maximum number of request header names.</summary>
    public const int MaximumHeaderCount = 64;

    /// <summary>The maximum number of values for one request header name.</summary>
    public const int MaximumHeaderValues = 64;

    /// <summary>The maximum request header-name length.</summary>
    public const int MaximumHeaderNameLength = 256;

    /// <summary>The maximum request header-value length.</summary>
    public const int MaximumHeaderValueLength = 16 * 1024;

    /// <summary>The maximum aggregate request header name/value length.</summary>
    public const int MaximumAggregateHeaderBytes = 64 * 1024;

    /// <summary>Checks bounded request inputs without constructing a management request.</summary>
    public static bool IsWithinLimits(
        string? apiKey,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers,
        ReadOnlyMemory<byte> body)
    {
        if (body.Length > MaximumRequestBodyBytes ||
            apiKey is { Length: > ControllerOptions.MaximumApiKeyLength })
        {
            return false;
        }

        try
        {
            _ = CopyHeaders(headers);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Attempts bounded construction for adapters before dispatch admission.</summary>
    public static bool TryCreateRequest(
        ControllerTransport transport,
        string method,
        string path,
        string? apiKey,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers,
        ReadOnlyMemory<byte> body,
        out ControllerManagementRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(method) || method.Length > 32 ||
            string.IsNullOrWhiteSpace(path) || path.Length > 8192 ||
            body.Length > MaximumRequestBodyBytes ||
            apiKey is { Length: > ControllerOptions.MaximumApiKeyLength })
        {
            return false;
        }


        try
        {
            var boundedHeaders = CopyHeaders(headers);
            request = new ControllerManagementRequest(
                transport,
                method,
                path,
                apiKey,
                boundedHeaders,
                body);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }


    internal static ImmutableDictionary<string, ImmutableArray<string>> CopyRequestHeaders(
        string method,
        string path,
        string? apiKey,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers,
        ReadOnlyMemory<byte> body)
    {
        if (string.IsNullOrWhiteSpace(method) || method.Length > 32)
        {
            throw new ArgumentException("A management method is required.", nameof(method));
        }

        if (string.IsNullOrWhiteSpace(path) || path.Length > 8192)
        {
            throw new ArgumentException("A management path is required.", nameof(path));
        }

        if (body.Length > MaximumRequestBodyBytes)
        {
            throw new ArgumentException("The management request body is too large.", nameof(body));
        }

        if (apiKey is { Length: > ControllerOptions.MaximumApiKeyLength })
        {
            throw new ArgumentException("The management API key is too large.", nameof(apiKey));
        }

        return CopyHeaders(headers);
    }

    internal static ImmutableDictionary<string, ImmutableArray<string>> CopyHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers)
    {
        var result = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(
            StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return result.ToImmutable();
        }

        var headerCount = 0;
        var aggregateLength = 0L;
        foreach (var pair in headers)
        {
            if (++headerCount > MaximumHeaderCount ||
                string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Key.Length > MaximumHeaderNameLength ||
                result.ContainsKey(pair.Key))
            {
                throw new ArgumentException("Management request headers are invalid.", nameof(headers));
            }

            aggregateLength += Encoding.UTF8.GetByteCount(pair.Key);
            if (aggregateLength > MaximumAggregateHeaderBytes)
            {
                throw new ArgumentException("Management request headers are too large.", nameof(headers));
            }

            if (pair.Value is null)
            {
                throw new ArgumentException("Management request headers are invalid.", nameof(headers));
            }

            var values = ImmutableArray.CreateBuilder<string>();
            var valueCount = 0;
            foreach (var value in pair.Value)
            {
                if (++valueCount > MaximumHeaderValues ||
                    value is null ||
                    value.Length > MaximumHeaderValueLength)
                {
                    throw new ArgumentException("Management request headers are invalid.", nameof(headers));
                }

                aggregateLength += Encoding.UTF8.GetByteCount(value);
                if (aggregateLength > MaximumAggregateHeaderBytes)
                {
                    throw new ArgumentException("Management request headers are too large.", nameof(headers));
                }

                values.Add(value);
            }

            result.Add(pair.Key, values.ToImmutable());
        }

        return result.ToImmutable();
    }
}


/// <summary>Identifies safe results from the shared management dispatcher.</summary>
public enum ControllerDispatchCode
{
    /// <summary>The request was accepted by a later management operation.</summary>
    Success,

    /// <summary>The request did not contain valid transport-neutral data.</summary>
    InvalidRequest,

    /// <summary>The configured API key was absent or did not match.</summary>
    Unauthorized,

    /// <summary>The selected management transport is disabled.</summary>
    TransportDisabled,

    /// <summary>The requested management resource or operation was not found.</summary>
    NotFound,

    /// <summary>The request version conflicts with current controller state.</summary>
    Conflict,

    /// <summary>The requested capability is not supported by the upstream contract.</summary>
    Unsupported,

    /// <summary>The controller is not accepting requests.</summary>
    Unavailable
}

/// <summary>
/// Contains one transport-neutral management request. Adapters translate their protocol into
/// this DTO; no ASP.NET, gRPC, socket, process, or other host object crosses the seam.
/// </summary>
public sealed class ControllerManagementRequest
{
    /// <summary>Creates a request from host-bounded immutable headers.</summary>
    /// <param name="transport">The adapter transport.</param>
    /// <param name="method">The normalized operation method.</param>
    /// <param name="path">The normalized operation path or gRPC method.</param>
    /// <param name="apiKey">The bounded presented key, if one was supplied.</param>
    /// <param name="headers">The immutable bounded protocol metadata.</param>
    /// <param name="body">The copied request body.</param>
    internal ControllerManagementRequest(
        ControllerTransport transport,
        string method,
        string path,
        string? apiKey,
        ImmutableDictionary<string, ImmutableArray<string>> headers,
        ReadOnlyMemory<byte> body)
    {
        if (string.IsNullOrWhiteSpace(method) || method.Length > 32)
        {
            throw new ArgumentException("A management method is required.", nameof(method));
        }

        if (string.IsNullOrWhiteSpace(path) || path.Length > 8192)
        {
            throw new ArgumentException("A management path is required.", nameof(path));
        }

        if (body.Length > ControllerAdmissionLimits.MaximumRequestBodyBytes)
        {
            throw new ArgumentException("The management request body is too large.", nameof(body));
        }

        if (apiKey is { Length: > ControllerOptions.MaximumApiKeyLength })
        {
            throw new ArgumentException("The management API key is too large.", nameof(apiKey));
        }

        Transport = transport;
        Method = method;
        Path = path;
        ApiKey = apiKey;
        Headers = headers ?? throw new ArgumentNullException(nameof(headers));
        Body = body.IsEmpty ? ImmutableArray<byte>.Empty : ImmutableArray.CreateRange(body.ToArray());
    }

    /// <summary>Creates a bounded transport-neutral management request.</summary>
    /// <param name="transport">The adapter transport.</param>
    /// <param name="method">The normalized operation method.</param>
    /// <param name="path">The normalized operation path or gRPC method.</param>
    /// <param name="apiKey">The presented key, if the adapter received one. It is never logged.</param>
    /// <param name="headers">The copied protocol metadata.</param>
    /// <param name="body">The copied request body.</param>
    public ControllerManagementRequest(
        ControllerTransport transport,
        string method,
        string path,
        string? apiKey = null,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers = null,
        ReadOnlyMemory<byte> body = default)
        : this(
            transport,
            method,
            path,
            apiKey,
            ControllerAdmissionLimits.CopyRequestHeaders(method, path, apiKey, headers, body),
            body)
    {
    }

    /// <summary>Gets the transport that supplied the request.</summary>
    public ControllerTransport Transport { get; }

    /// <summary>Gets the normalized method or operation verb.</summary>
    public string Method { get; }

    /// <summary>Gets the normalized path or protocol method.</summary>
    public string Path { get; }

    /// <summary>Gets the presented API key. Consumers must not log or persist it.</summary>
    public string? ApiKey { get; }

    /// <summary>Gets copied protocol metadata.</summary>
    public IReadOnlyDictionary<string, ImmutableArray<string>> Headers { get; }

    /// <summary>Gets an immutable copy of the request body.</summary>
    public ImmutableArray<byte> Body { get; }
}


/// <summary>Contains one safe response from the shared management dispatcher.</summary>
public sealed class ControllerManagementResponse
{
    /// <summary>Creates a response with a protocol-independent status code and body.</summary>
    /// <param name="statusCode">The HTTP-compatible status code for adapters.</param>
    /// <param name="code">The safe dispatch category.</param>
    /// <param name="headers">The response metadata.</param>
    /// <param name="body">The response body bytes.</param>
    public ControllerManagementResponse(
        int statusCode,
        ControllerDispatchCode code,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers = null,
        ReadOnlyMemory<byte> body = default)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        StatusCode = statusCode;
        Code = code;
        Headers = CopyHeaders(headers);
        Body = body.IsEmpty ? ImmutableArray<byte>.Empty : ImmutableArray.CreateRange(body.ToArray());
    }

    /// <summary>Gets the HTTP-compatible status code.</summary>
    public int StatusCode { get; }

    /// <summary>Gets the safe dispatch category.</summary>
    public ControllerDispatchCode Code { get; }

    /// <summary>Gets copied response metadata.</summary>
    public IReadOnlyDictionary<string, ImmutableArray<string>> Headers { get; }

    /// <summary>Gets an immutable copy of the response body.</summary>
    public ImmutableArray<byte> Body { get; }

    /// <summary>Creates an empty invalid-request response.</summary>
    public static ControllerManagementResponse InvalidRequest =>
        new(400, ControllerDispatchCode.InvalidRequest);

    /// <summary>Creates an empty unavailable response.</summary>
    public static ControllerManagementResponse Unavailable =>
        new(503, ControllerDispatchCode.Unavailable);

    /// <summary>Creates an empty unauthorized response.</summary>
    public static ControllerManagementResponse Unauthorized =>
        new(401, ControllerDispatchCode.Unauthorized);

    /// <summary>Creates an empty transport-disabled response.</summary>
    public static ControllerManagementResponse TransportDisabled =>
        new(404, ControllerDispatchCode.TransportDisabled);

    /// <summary>Creates an empty not-found response.</summary>
    public static ControllerManagementResponse NotFound =>
        new(404, ControllerDispatchCode.NotFound);

    private static ImmutableDictionary<string, ImmutableArray<string>> CopyHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers)
    {
        var result = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(
            StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return result.ToImmutable();
        }

        foreach (var pair in headers)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 256 || result.ContainsKey(pair.Key))
            {
                throw new ArgumentException("Management response headers are invalid.", nameof(headers));
            }

            var values = pair.Value?.ToImmutableArray()
                ?? throw new ArgumentException("Management response headers are invalid.", nameof(headers));
            if (values.Any(static value => value is null || value.Length > 16 * 1024))
            {
                throw new ArgumentException("Management response headers are invalid.", nameof(headers));
            }

            result.Add(pair.Key, values);
        }

        return result.ToImmutable();
    }
}

/// <summary>Dispatches one bounded, transport-neutral management request.</summary>
public interface IControllerManagementDispatcher
{
    /// <summary>Dispatches a request and returns a safe canonical response.</summary>
    ValueTask<ControllerManagementResponse> DispatchAsync(
        ControllerManagementRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Defines the lifecycle contract implemented by future transport adapters.
/// </summary>
public interface IControllerTransportAdapter
{
    /// <summary>Gets the transport owned by the adapter.</summary>
    ControllerTransport Transport { get; }

    /// <summary>
    /// Gets whether startup completed and the adapter is accepting requests. Implementations must
    /// expose false while starting, stopping, or after a failed start rollback.
    /// </summary>
    bool IsStarted { get; }

    /// <summary>
    /// Starts the adapter against one shared dispatcher. Start is idempotent while active. A
    /// cancellation or exception must release every resource acquired by this call and leave the
    /// adapter stopped; a later start may retry. The adapter must not start a second listener.
    /// </summary>
    ValueTask StartAsync(
        IControllerManagementDispatcher dispatcher,
        ControllerOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the adapter idempotently. A successful stop leaves it stopped and future dispatches
    /// unavailable; cancellation before completion leaves the active adapter intact for retry.
    /// </summary>
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates the concrete HostRoute handler when Exposure supplies its management implementation.
/// Foundation requires a real handler from this seam and never installs a no-op substitute.
/// </summary>
public interface IControllerManagementHandlerFactory
{
    /// <summary>Creates a handler over the shared dispatcher and validated options.</summary>
    IExtensionHandler Create(
        IControllerManagementDispatcher dispatcher,
        ControllerOptions options);
}

/// <summary>Provides the foundation dispatcher and admission checks.</summary>
public sealed class ControllerManagementDispatcher : IControllerManagementDispatcher, IDisposable
{
    private sealed class DispatcherConfiguration
    {
        internal DispatcherConfiguration(ControllerOptions options, ControllerManagementCore core)
        {
            Options = options;
            Core = core;
        }

        internal ControllerOptions Options { get; }
        internal ControllerManagementCore Core { get; }
    }

    private readonly IExtensionHostBridge? _bridge;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private int _inFlight;
    private readonly object _drainSync = new();
    private TaskCompletionSource? _drain;
    private DispatcherConfiguration _configuration;
    private Func<long, CancellationToken, ValueTask<ControllerManagementResponse>>? _reloadHandler;
    private Func<CancellationToken, ValueTask<ControllerStateDto?>>? _stateProvider;
    private int _state;

    /// <summary>Creates an admission dispatcher without a Host bridge.</summary>
    public ControllerManagementDispatcher(ControllerOptions options)
        : this(options, null)
    {
    }

    /// <summary>Creates a dispatcher over the trusted Host bridge.</summary>
    internal ControllerManagementDispatcher(ControllerOptions options, IExtensionHostBridge? bridge)
    {
        ArgumentNullException.ThrowIfNull(options);
        _bridge = bridge;
        _configuration = new DispatcherConfiguration(options, new ControllerManagementCore(options, bridge, mutationGate: _mutationGate, admissionProbe: () => IsStarted));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _mutationGate.Dispose();
    }

    /// <summary>Gets whether the dispatcher is accepting authenticated requests.</summary>
    public bool IsStarted => Volatile.Read(ref _state) == 1;

    /// <summary>Configures runtime reload and fresh state callbacks.</summary>
    internal void ConfigureRuntimeCallbacks(
        Func<long, CancellationToken, ValueTask<ControllerManagementResponse>> reloadHandler,
        Func<CancellationToken, ValueTask<ControllerStateDto?>> stateProvider)
    {
        ArgumentNullException.ThrowIfNull(reloadHandler);
        ArgumentNullException.ThrowIfNull(stateProvider);
        Volatile.Write(ref _reloadHandler, reloadHandler);
        Volatile.Write(ref _stateProvider, stateProvider);
        var current = Volatile.Read(ref _configuration);
        Volatile.Write(ref _configuration, new DispatcherConfiguration(
            current.Options,
            new ControllerManagementCore(current.Options, _bridge, reloadHandler, stateProvider, _mutationGate, admissionProbe: () => IsStarted)));
    }

    /// <summary>Atomically swaps the options used by admission and core dispatch.</summary>
    internal void SwapOptions(ControllerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var reloadHandler = Volatile.Read(ref _reloadHandler);
        var stateProvider = Volatile.Read(ref _stateProvider);
        var core = new ControllerManagementCore(options, _bridge, reloadHandler, stateProvider, _mutationGate, admissionProbe: () => IsStarted);
        Volatile.Write(ref _configuration, new DispatcherConfiguration(options, core));
    }

    /// <summary>Gets the active dispatcher options for runtime reconciliation.</summary>
    internal ControllerOptions Options => Volatile.Read(ref _configuration).Options;

    /// <summary>Gets the shared gate serializing configuration mutations and reload.</summary>
    internal SemaphoreSlim MutationGate => _mutationGate;

    /// <summary>Starts admission without binding any listener.</summary>
    internal ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = Volatile.Read(ref _configuration);
        var validation = configuration.Options.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("Controller options are invalid.");
        }

        Interlocked.Exchange(ref _state, 1);
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops admission idempotently.</summary>
    internal ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _state, 0);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<ControllerManagementResponse> DispatchAsync(
        ControllerManagementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Interlocked.Increment(ref _inFlight);
        try
        {
            // Increment-before-state-read pairs with QuiesceAsync's state-write-before-count-read:
            // a dispatch the drain could miss is guaranteed to observe closed admission first.
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsStarted)
            {
                return ControllerManagementResponseBuilder.Unavailable;
            }

            var configuration = Volatile.Read(ref _configuration);
            var options = configuration.Options;
            if (!options.IsTransportEnabled(request.Transport))
            {
                return ControllerManagementResponseBuilder.TransportDisabled;
            }

            if (!options.IsApiKeyValid(request.ApiKey) ||
                !request.Headers.TryGetValue(ControllerManagementApiContract.ApiKeyHeaderName, out var keyValues) ||
                keyValues.Length != 1 ||
                !options.IsApiKeyValid(keyValues[0]) ||
                !string.Equals(request.ApiKey, keyValues[0], StringComparison.Ordinal))
            {
                return ControllerManagementResponseBuilder.Unauthorized;
            }

            return await configuration.Core.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndDispatch();
        }
    }
    private void EndDispatch()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0)
        {
            lock (_drainSync)
            {
                _drain?.TrySetResult();
                _drain = null;
            }
        }
    }

    /// <summary>Waits for all admitted dispatches to finish after admission is closed.</summary>
    internal ValueTask QuiesceAsync()
    {
        lock (_drainSync)
        {
            if (Volatile.Read(ref _inFlight) == 0)
            {
                return ValueTask.CompletedTask;
            }

            _drain ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new ValueTask(_drain.Task);
        }
    }

    internal ValueTask ProvisionHostRouteAsync(
        string handlerId,
        CancellationToken cancellationToken) =>
        Volatile.Read(ref _configuration).Core.ProvisionHostRouteAsync(handlerId, cancellationToken);

    /// <summary>Provisions a HostRoute using candidate options before an atomic options swap.</summary>
    internal ValueTask<ProvisionedHostRouteIdentity?> ProvisionHostRouteAsync(
        string handlerId,
        ControllerOptions options,
        string? ownershipMarker,
        CancellationToken cancellationToken) =>
        Volatile.Read(ref _configuration).Core.ProvisionHostRouteAsync(handlerId, options, ownershipMarker, cancellationToken);

    /// <summary>Provisions an ephemeral private route with an internal ownership marker.</summary>
    internal ValueTask<ProvisionedHostRouteIdentity?> ProvisionHostRouteAsync(
        string handlerId,
        string? ownershipMarker,
        CancellationToken cancellationToken) =>
        Volatile.Read(ref _configuration).Core.ProvisionHostRouteAsync(handlerId, ownershipMarker, cancellationToken);

    /// <summary>Atomically replaces a verified bootstrap route with a configured route.</summary>
    internal ValueTask ReplaceBootstrapWithConfiguredHostRouteAsync(
        string handlerId,
        ProvisionedHostRouteIdentity identity,
        string ownershipMarker,
        ControllerOptions options,
        CancellationToken cancellationToken) =>
        Volatile.Read(ref _configuration).Core.ReplaceBootstrapWithConfiguredHostRouteAsync(handlerId, identity, ownershipMarker, options, cancellationToken);

    /// <summary>Atomically ensures a configured route at its desired path.</summary>
    internal ValueTask EnsureConfiguredHostRouteAsync(
        string handlerId,
        string? oldCanonicalPath,
        ControllerOptions options,
        CancellationToken cancellationToken) =>
        Volatile.Read(ref _configuration).Core.EnsureConfiguredHostRouteAsync(handlerId, oldCanonicalPath, options, cancellationToken);

    /// <summary>Removes only the exact configured controller route identity.</summary>
    internal ValueTask RemoveConfiguredHostRouteAsync(
        string handlerId,
        string canonicalPath,
        CancellationToken cancellationToken) =>
        Volatile.Read(ref _configuration).Core.RemoveConfiguredHostRouteAsync(handlerId, canonicalPath, cancellationToken);

    /// <summary>Removes only an identity- and marker-proven private bootstrap route.</summary>
    internal ValueTask RemoveProvisionedHostRouteAsync(
        string handlerId,
        ProvisionedHostRouteIdentity identity,
        string ownershipMarker,
        CancellationToken cancellationToken) =>
        Volatile.Read(ref _configuration).Core.RemoveProvisionedHostRouteAsync(handlerId, identity, ownershipMarker, cancellationToken);

    /// <summary>Removes only stale private bootstrap routes for this controller handler.</summary>
    internal ValueTask CleanupStaleBootstrapRoutesAsync(string handlerId, CancellationToken cancellationToken) =>
        Volatile.Read(ref _configuration).Core.CleanupStaleBootstrapRoutesAsync(handlerId, cancellationToken);

}

/// <summary>Coordinates dispatcher, listeners, and the optional HostRoute lifecycle.</summary>
internal sealed class ControllerRuntime
{
    private ControllerOptions _options;
    private readonly List<IControllerTransportAdapter> _transportAdapters;
    private readonly IExtensionHostBridge? _bridge;
    private ProvisionedHostRouteIdentity? _bootstrapRouteIdentity;
    private string? _bootstrapOwnershipMarker;
    private bool _staleBootstrapRoutesCleaned;
    private bool _bootstrapRouteCleanupPending;
    private bool _bootstrapRouteProvisioned;
    private bool _hostRouteRunning;
    private readonly List<IControllerTransportAdapter> _startedAdapters = new();
    private IExtensionRegistration? _registration;
    private string? _handlerId;
    private int _state;

    internal ControllerRuntime(ControllerManagementDispatcher dispatcher, ControllerOptions options)
        : this(dispatcher, options, null, null, null)
    {
    }

    internal ControllerRuntime(
        ControllerManagementDispatcher dispatcher,
        ControllerOptions options,
        IEnumerable<IControllerTransportAdapter>? transportAdapters,
        string? bootstrapOwnershipMarker = null,
        IExtensionHostBridge? bridge = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        if (bootstrapOwnershipMarker is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(bootstrapOwnershipMarker);
        }

        Dispatcher = dispatcher;
        _options = options;
        _bridge = bridge;
        _bootstrapOwnershipMarker = bootstrapOwnershipMarker;
        _transportAdapters = (transportAdapters ?? CreateDefaultTransportAdapters()).ToList();
        if (_transportAdapters.Any(static adapter => adapter is null))
        {
            throw new ArgumentException("The transport adapter collection contains a null adapter.", nameof(transportAdapters));
        }
    }

    internal ControllerManagementDispatcher Dispatcher { get; }

    internal bool IsStarted => Volatile.Read(ref _state) == 1;

    /// <summary>Gets whether this runtime owns ephemeral bootstrap state.</summary>
    internal bool IsEphemeralBootstrap => _bootstrapOwnershipMarker is not null;

    /// <summary>Gets whether the temporary bootstrap route was provisioned successfully.</summary>
    internal bool BootstrapRouteProvisioned => _bootstrapRouteProvisioned;

    /// <summary>Gets whether a HostRoute handler is registered and owned by this runtime.</summary>
    internal bool HasRegisteredHandler => _registration is not null && _handlerId is not null;

    /// <summary>Gets whether listeners or dispatcher admission still require cleanup.</summary>
    internal bool HasActiveResources =>
        IsStarted || _startedAdapters.Count != 0 || Dispatcher.IsStarted || _bootstrapRouteCleanupPending;

    /// <summary>Returns a non-secret snapshot for the controller state endpoint.</summary>
    internal ControllerStateDto GetState() => BuildState(Dispatcher.Options, _hostRouteRunning);

    /// <summary>Reads current HostRoute ownership before returning non-secret runtime state.</summary>
    internal async ValueTask<ControllerStateDto?> GetStateAsync(CancellationToken cancellationToken)
    {
        if (_bridge is null)
        {
            return GetState();
        }

        ConfigurationReadResult<HostConfigurationSnapshot> read;
        try
        {
            read = await _bridge.FullConfiguration.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }

        if (!read.IsSuccess || read.Value is not { } snapshot)
        {
            return null;
        }

        var options = Dispatcher.Options;
        var hostRouteRunning = false;
        if (IsEphemeralBootstrap)
        {
            if (_bootstrapRouteIdentity is { } identity && _bootstrapOwnershipMarker is { } marker)
            {
                var route = snapshot.Routes.SingleOrDefault(candidate => candidate.Id == identity.RouteId);
                hostRouteRunning = route is not null && ControllerManagementCore.IsBootstrapRoute(route, identity, _handlerId ?? ControllerManagementApiContract.HandlerId, marker);
            }
        }
        else if (options.EnableHostRoute && _handlerId is { } handlerId && options.HostRoutePath is { } path)
        {
            hostRouteRunning = snapshot.Routes.Any(route =>
                route.Target is ExtensionHandlerRouteTargetConfiguration target &&
                string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal) &&
                route.Matcher.Type == RouteMatcherType.Prefix &&
                string.Equals(route.Matcher.Pattern, path, StringComparison.Ordinal));
        }

        return BuildState(options, hostRouteRunning);
    }

    private ControllerStateDto BuildState(ControllerOptions options, bool hostRouteRunning) => new()
    {
        BootstrapMode = IsEphemeralBootstrap,
        Listeners = new ControllerListenersStateDto
        {
            HostRoute = new ControllerListenerStateDto { Enabled = options.EnableHostRoute, Running = hostRouteRunning },
            HttpJson = new ControllerListenerStateDto { Enabled = options.EnableHttpJson, Running = IsAdapterStarted(ControllerTransport.HttpJson) },
            Grpc = new ControllerListenerStateDto { Enabled = options.EnableGrpc, Running = IsAdapterStarted(ControllerTransport.Grpc) },
            UnixSocket = new ControllerListenerStateDto { Enabled = options.EnableUnixSocket, Running = IsAdapterStarted(ControllerTransport.UnixSocket) }
        }
    };

    private bool IsAdapterStarted(ControllerTransport transport) =>
        _transportAdapters.Any(adapter => adapter.Transport == transport && adapter.IsStarted);


    internal async ValueTask StartAsync(
        IExtensionRegistration? registration,
        IControllerManagementHandlerFactory? handlerFactory,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _state) == 1)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _state, 2, 0) != 0)
        {
            throw new InvalidOperationException("Controller runtime lifecycle is busy.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.StartAsync(cancellationToken).ConfigureAwait(false);

            foreach (var adapter in _transportAdapters)
            {
                if (!_options.IsTransportEnabled(adapter.Transport))
                {
                    continue;
                }

                try
                {
                    await adapter.StartAsync(Dispatcher, _options, cancellationToken)
                        .ConfigureAwait(false);
                    if (!adapter.IsStarted)
                    {
                        throw new InvalidOperationException("The enabled transport did not start.");
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    if (adapter.IsStarted && !_startedAdapters.Contains(adapter))
                    {
                        _startedAdapters.Add(adapter);
                    }
                }
            }

            if (_options.EnableHostRoute)
            {
                if (HasRegisteredHandler)
                {
                    if (IsEphemeralBootstrap)
                    {
                        if (!_staleBootstrapRoutesCleaned)
                        {
                            await Dispatcher.CleanupStaleBootstrapRoutesAsync(
                                _handlerId!,
                                cancellationToken).ConfigureAwait(false);
                            _staleBootstrapRoutesCleaned = true;
                        }

                        _bootstrapRouteCleanupPending = true;
                        var provisionedIdentity = await Dispatcher.ProvisionHostRouteAsync(
                            _handlerId!,
                            _bootstrapOwnershipMarker,
                            cancellationToken).ConfigureAwait(false);
                        if (provisionedIdentity is not { } identity)
                        {
                            throw new InvalidOperationException("The bootstrap route provisioning identity is unavailable.");
                        }

                        _bootstrapRouteIdentity = identity;
                        _bootstrapRouteProvisioned = true;
                        _hostRouteRunning = true;

                    }
                    else
                    {
                        await Dispatcher.ProvisionHostRouteAsync(_handlerId!, cancellationToken)
                            .ConfigureAwait(false);
                        _hostRouteRunning = true;
                    }
                }
                else
                {
                    if (registration is null || handlerFactory is null)
                    {
                        throw new InvalidOperationException("HostRoute requires a management handler registration seam.");
                    }

                    var handler = handlerFactory.Create(Dispatcher, _options)
                        ?? throw new InvalidOperationException("The management handler factory returned no handler.");
                    if (string.IsNullOrWhiteSpace(handler.HandlerId))
                    {
                        throw new InvalidOperationException("The management handler has no stable identifier.");
                    }

                    if (IsEphemeralBootstrap && !string.Equals(handler.HandlerId, ControllerManagementApiContract.HandlerId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The ephemeral bootstrap handler identity is not fixed.");
                    }


                    if (!registration.TryRegisterHandler(handler))
                    {
                        throw new InvalidOperationException("The management handler registration was rejected.");
                    }

                    _registration = registration;
                    _handlerId = handler.HandlerId;
                    if (IsEphemeralBootstrap)
                    {
                        if (!_staleBootstrapRoutesCleaned)
                        {
                            await Dispatcher.CleanupStaleBootstrapRoutesAsync(
                                handler.HandlerId,
                                cancellationToken).ConfigureAwait(false);
                            _staleBootstrapRoutesCleaned = true;
                        }

                        _bootstrapRouteCleanupPending = true;
                        var provisionedIdentity = await Dispatcher.ProvisionHostRouteAsync(
                            handler.HandlerId,
                            _bootstrapOwnershipMarker,
                            cancellationToken).ConfigureAwait(false);
                        if (provisionedIdentity is not { } identity)
                        {
                            throw new InvalidOperationException("The bootstrap route provisioning identity is unavailable.");
                        }

                        _bootstrapRouteIdentity = identity;
                        _bootstrapRouteProvisioned = true;
                        _hostRouteRunning = true;
                    }
                    else
                    {
                        await Dispatcher.ProvisionHostRouteAsync(handler.HandlerId, cancellationToken)
                            .ConfigureAwait(false);
                        _hostRouteRunning = true;
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref _state, 1);
        }
        catch
        {
            var rollbackComplete = await RollbackStartupAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _state, rollbackComplete ? 0 : 1);
            throw;
        }
    }

    /// <summary>Reloads validated Host settings and reconciles listeners before swapping admission options.</summary>
    internal async ValueTask<ControllerManagementResponse> ReloadAsync(
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        if (!IsStarted || _bridge is null)
        {
            return ControllerManagementResponseBuilder.Unavailable;
        }

        await Dispatcher.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReloadCoreAsync(expectedVersion, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Dispatcher.MutationGate.Release();
        }
    }

    private async ValueTask<ControllerManagementResponse> ReloadCoreAsync(
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        if (!IsStarted || _bridge is null)
        {
            return ControllerManagementResponseBuilder.Unavailable;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ConfigurationReadResult<HostConfigurationSnapshot> read;
        try
        {
            read = await _bridge.FullConfiguration.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            return ControllerManagementResponseBuilder.Unavailable;
        }
        catch (NotSupportedException)
        {
            return ControllerManagementResponseBuilder.Unsupported;
        }

        if (!read.IsSuccess || read.Value is not { } snapshot)
        {
            return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        }

        if (expectedVersion != snapshot.Version)
        {
            return ControllerManagementResponseBuilder.PreconditionFailed;
        }

        var matchingSettings = snapshot.ExtensionSettings
            .Where(static settings => string.Equals(settings.ExtensionId, ControllerOptions.ExtensionId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matchingSettings.Length != 1 || !ControllerOptions.TryParseHostSettings(matchingSettings[0], out var candidate) || candidate is null)
        {
            return ControllerManagementResponseBuilder.InvalidRequest;
        }

        var validation = candidate.Validate();
        if (!validation.IsValid)
        {
            return ControllerManagementResponseBuilder.InvalidRequest;
        }

        if (IsEphemeralBootstrap && !HasAnyListener(candidate))
        {
            return ControllerManagementResponseBuilder.InvalidRequest;
        }
        var current = Volatile.Read(ref _options);
        var optionsSwapped = false;
        try
        {
            await ReconcileAdaptersAsync(current, candidate, cancellationToken).ConfigureAwait(false);
            await ReconcileHostRouteAsync(current, candidate, snapshot, cancellationToken).ConfigureAwait(false);

            // All listener and private-route changes completed successfully while admission was
            // stopped. Publish one immutable dispatcher configuration only after that point.
            Dispatcher.SwapOptions(candidate);
            optionsSwapped = true;
            Volatile.Write(ref _options, candidate);
            await Dispatcher.StartAsync(CancellationToken.None).ConfigureAwait(false);
            Interlocked.Exchange(ref _state, 1);
            return ControllerManagementResponseBuilder.Success(GetState(), snapshot.Version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (optionsSwapped)
            {
                Volatile.Write(ref _options, current);
                Dispatcher.SwapOptions(current);
            }

            if (!await RecoverAfterReloadFailureAsync(candidate, current).ConfigureAwait(false))
            {
                await FailClosedAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch
        {
            if (optionsSwapped)
            {
                Volatile.Write(ref _options, current);
                Dispatcher.SwapOptions(current);
            }

            if (!await RecoverAfterReloadFailureAsync(candidate, current).ConfigureAwait(false))
            {
                await FailClosedAsync().ConfigureAwait(false);
            }

            return ControllerManagementResponseBuilder.Unavailable;
        }
    }

    private async ValueTask<bool> RecoverAfterReloadFailureAsync(
        ControllerOptions candidate,
        ControllerOptions current)
    {
        try
        {
            await ReconcileAdaptersAsync(candidate, current, CancellationToken.None).ConfigureAwait(false);
            await Dispatcher.StartAsync(CancellationToken.None).ConfigureAwait(false);
            Interlocked.Exchange(ref _state, 1);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async ValueTask ReconcileAdaptersAsync(
        ControllerOptions current,
        ControllerOptions candidate,
        CancellationToken cancellationToken)
    {
        await Dispatcher.StopAsync(CancellationToken.None).ConfigureAwait(false);

        for (var index = 0; index < _transportAdapters.Count; index++)
        {
            var adapter = _transportAdapters[index];
            var wasEnabled = current.IsTransportEnabled(adapter.Transport);
            var willBeEnabled = candidate.IsTransportEnabled(adapter.Transport);
            var endpointChanged = wasEnabled && willBeEnabled && !HasSameEndpoint(current, candidate, adapter.Transport);
            if (!wasEnabled || (!endpointChanged && willBeEnabled))
            {
                continue;
            }

            await StopAdapterAsync(adapter, cancellationToken).ConfigureAwait(false);
            if (endpointChanged)
            {
                var replacement = CreateReplacementAdapter(adapter);
                _transportAdapters[index] = replacement;
                if (adapter is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        foreach (var adapter in _transportAdapters)
        {
            if (!candidate.IsTransportEnabled(adapter.Transport) || adapter.IsStarted)
            {
                continue;
            }

            await adapter.StartAsync(Dispatcher, candidate, cancellationToken).ConfigureAwait(false);
            if (!adapter.IsStarted)
            {
                throw new InvalidOperationException("The enabled transport did not start during reload.");
            }

            if (!_startedAdapters.Contains(adapter))
            {
                _startedAdapters.Add(adapter);
            }
        }
    }

    private async ValueTask ReconcileHostRouteAsync(
        ControllerOptions current,
        ControllerOptions candidate,
        HostConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!current.EnableHostRoute && !candidate.EnableHostRoute && !IsEphemeralBootstrap)
        {
            _hostRouteRunning = false;
            return;
        }

        if (_handlerId is null)
        {
            throw new InvalidOperationException("The controller HostRoute handler identity is unavailable.");
        }

        if (IsEphemeralBootstrap)
        {
            if (_bootstrapRouteIdentity is not { } identity || _bootstrapOwnershipMarker is not { } marker)
            {
                throw new InvalidOperationException("The bootstrap route ownership proof is unavailable.");
            }

            var bootstrapRoute = snapshot.Routes.SingleOrDefault(route => route.Id == identity.RouteId);
            var bootstrapPresent = bootstrapRoute is not null && ControllerManagementCore.IsBootstrapRoute(bootstrapRoute, identity, _handlerId, marker);
            if (bootstrapRoute is not null && !bootstrapPresent)
            {
                throw new InvalidOperationException("The bootstrap route ownership proof no longer matches.");
            }

            if (candidate.EnableHostRoute)
            {
                await Dispatcher.ReplaceBootstrapWithConfiguredHostRouteAsync(
                    _handlerId,
                    identity,
                    marker,
                    candidate,
                    cancellationToken).ConfigureAwait(false);
                _bootstrapRouteIdentity = null;
                _bootstrapRouteProvisioned = false;
                _bootstrapRouteCleanupPending = false;
                _bootstrapOwnershipMarker = null;
                _hostRouteRunning = true;
            }
            else
            {
                await Dispatcher.RemoveProvisionedHostRouteAsync(
                    _handlerId,
                    identity,
                    marker,
                    cancellationToken).ConfigureAwait(false);
                _bootstrapRouteIdentity = null;
                _bootstrapRouteProvisioned = false;
                _bootstrapRouteCleanupPending = false;
                _bootstrapOwnershipMarker = null;
                _hostRouteRunning = false;
            }

            return;
        }

        var currentPath = current.HostRoutePath;
        var verifiedCurrent = current.EnableHostRoute && currentPath is not null && snapshot.Routes.Any(route =>
            route.Target is ExtensionHandlerRouteTargetConfiguration target &&
            string.Equals(target.HandlerId, _handlerId, StringComparison.Ordinal) &&
            route.Matcher.Type == RouteMatcherType.Prefix &&
            string.Equals(route.Matcher.Pattern, currentPath, StringComparison.Ordinal));
        var pathChanged = !string.Equals(currentPath, candidate.HostRoutePath, StringComparison.Ordinal);
        if (candidate.EnableHostRoute && (!current.EnableHostRoute || !verifiedCurrent || pathChanged))
        {
            await Dispatcher.EnsureConfiguredHostRouteAsync(
                _handlerId,
                current.EnableHostRoute ? currentPath : null,
                candidate,
                cancellationToken).ConfigureAwait(false);
            _hostRouteRunning = true;
        }
        else if (!candidate.EnableHostRoute && verifiedCurrent)
        {
            await Dispatcher.RemoveConfiguredHostRouteAsync(
                _handlerId,
                currentPath!,
                cancellationToken).ConfigureAwait(false);
            _hostRouteRunning = false;
        }
        else
        {
            _hostRouteRunning = candidate.EnableHostRoute && verifiedCurrent;
        }
    }

    private async ValueTask StopAdapterAsync(
        IControllerTransportAdapter adapter,
        CancellationToken cancellationToken)
    {
        if (adapter.IsStarted)
        {
            await adapter.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        _startedAdapters.Remove(adapter);
        if (adapter.IsStarted)
        {
            throw new InvalidOperationException("The transport did not stop during reload.");
        }
    }

    private static bool HasAnyListener(ControllerOptions options) =>
        options.EnableHostRoute || options.EnableHttpJson || options.EnableGrpc || options.EnableUnixSocket;

    private static bool HasSameEndpoint(ControllerOptions left, ControllerOptions right, ControllerTransport transport) => transport switch
    {
        ControllerTransport.HostRoute => left.EnableHostRoute == right.EnableHostRoute && string.Equals(left.HostRoutePath, right.HostRoutePath, StringComparison.Ordinal),
        ControllerTransport.HttpJson => left.EnableHttpJson == right.EnableHttpJson && left.HttpPort == right.HttpPort,
        ControllerTransport.Grpc => left.EnableGrpc == right.EnableGrpc && left.GrpcPort == right.GrpcPort,
        ControllerTransport.UnixSocket => left.EnableUnixSocket == right.EnableUnixSocket && left.UnixSocketMode == right.UnixSocketMode && string.Equals(left.UnixSocketPath, right.UnixSocketPath, StringComparison.Ordinal),
        _ => false
    };

    private static IControllerTransportAdapter CreateReplacementAdapter(IControllerTransportAdapter adapter) => adapter.Transport switch
    {
        ControllerTransport.HttpJson => new HttpJsonTransportAdapter(),
        ControllerTransport.Grpc => new GrpcControllerManagementAdapter(),
        ControllerTransport.UnixSocket => new UnixSocketTransportAdapter(),
        _ => throw new InvalidOperationException("The transport adapter cannot be replaced safely.")
    };

    private async ValueTask FailClosedAsync()
    {
        _hostRouteRunning = false;
        try
        {
            await StopAllAdaptersAsync().ConfigureAwait(false);
        }
        catch
        {
            // Continue to close dispatcher admission even when one adapter retains a resource.
        }

        try
        {
            await Dispatcher.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The dispatcher is already fail-closed when this operation cannot complete.
        }

        Interlocked.Exchange(ref _state, 0);
    }

    private async ValueTask StopAllAdaptersAsync()
    {
        Exception? firstFailure = null;
        for (var index = _transportAdapters.Count - 1; index >= 0; index--)
        {
            var adapter = _transportAdapters[index];
            if (!adapter.IsStarted)
            {
                _startedAdapters.Remove(adapter);
                continue;
            }

            try
            {
                await adapter.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
            }

            if (!adapter.IsStarted)
            {
                _startedAdapters.Remove(adapter);
            }
        }

        if (firstFailure is not null)
        {
            throw firstFailure;
        }
    }

    internal ValueTask StopAsync(CancellationToken cancellationToken) =>
        StopAsync(unregisterHandler: true, cancellationToken: cancellationToken);

    internal async ValueTask StopAsync(
        bool unregisterHandler,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = Volatile.Read(ref _state);
        if (state == 0 && (!unregisterHandler || !HasRegisteredHandler))
        {
            // A runtime closed by fail-closed reload recovery can still have admitted dispatches
            // draining; terminal disposal requires their leases to complete first.
            await Dispatcher.QuiesceAsync().ConfigureAwait(false);
            return;
        }

        if (state == 2)
        {
            throw new InvalidOperationException("Controller runtime lifecycle is busy.");
        }

        if (Interlocked.CompareExchange(ref _state, 2, state) != state)
        {
            throw new InvalidOperationException("Controller runtime lifecycle is busy.");
        }

        try
        {
            _hostRouteRunning = false;
            await StopStartedAdaptersAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await RemoveBootstrapRouteAsync(CancellationToken.None).ConfigureAwait(false);
            if (unregisterHandler)
            {
                UnregisterHandler();
            }

            // The cancellation check above makes terminal cleanup deterministic; no resource
            // remains that should be left live once the handler has been tombstoned.
            await Dispatcher.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await Dispatcher.QuiesceAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _state, 0);
        }
        catch
        {
            // Keep any retained registration, route marker, and remaining resources available for retry.
            Interlocked.Exchange(ref _state, 1);
            throw;
        }
    }

    private static IControllerTransportAdapter[] CreateDefaultTransportAdapters() =>
        new IControllerTransportAdapter[]
        {
            new HttpJsonTransportAdapter(),
            new GrpcControllerManagementAdapter(),
            new UnixSocketTransportAdapter()
        };

    /// <summary>Disposes stopped concrete adapters after the runtime has become terminal.</summary>
    internal void DisposeStoppedAdapters()
    {
        if (Volatile.Read(ref _state) != 0 ||
            _startedAdapters.Count != 0 ||
            _registration is not null ||
            _handlerId is not null ||
            Dispatcher.IsStarted ||
            _bootstrapRouteCleanupPending)
        {
            return;
        }

        foreach (var adapter in _transportAdapters.OfType<IDisposable>())
        {
            adapter.Dispose();
        }

        // Terminal runtimes never dispatch again, so the shared mutation gate can be released.
        Dispatcher.Dispose();
    }

    private async ValueTask<bool> RollbackStartupAsync()
    {
        try
        {
            await StopStartedAdaptersAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Continue rollback so every successfully started adapter is attempted.
        }

        try
        {
            await RemoveBootstrapRouteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original startup failure; a later lifecycle call can retry cleanup.
        }

        // A successful registration is intentionally retained. The upstream host permanently
        // tombstones a handler ID after unregistering it, so retryable startup failures must not
        // unregister the fixed handler.
        try
        {
            await Dispatcher.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original startup failure; the dispatcher stop is best effort here.
        }
        await Dispatcher.QuiesceAsync().ConfigureAwait(false);

        return _startedAdapters.Count == 0 &&
            !Dispatcher.IsStarted &&
            !_bootstrapRouteCleanupPending;
    }

    private async ValueTask RemoveBootstrapRouteAsync(CancellationToken cancellationToken)
    {
        if (!IsEphemeralBootstrap || !_bootstrapRouteCleanupPending)
        {
            return;
        }

        if (_handlerId is null)
        {
            throw new InvalidOperationException("The bootstrap handler identity is unavailable for route cleanup.");
        }

        if (_bootstrapRouteIdentity is not { } identity)
        {
            throw new InvalidOperationException("The bootstrap route provisioning identity is unavailable for cleanup.");
        }

        await Dispatcher.RemoveProvisionedHostRouteAsync(
            _handlerId,
            identity,
            _bootstrapOwnershipMarker!,
            cancellationToken).ConfigureAwait(false);
        _bootstrapRouteIdentity = null;
        _bootstrapRouteProvisioned = false;
        _bootstrapRouteCleanupPending = false;
        _hostRouteRunning = false;
    }

    private async ValueTask StopStartedAdaptersAsync(CancellationToken cancellationToken)
    {
        Exception? firstFailure = null;
        for (var index = _startedAdapters.Count - 1; index >= 0; index--)
        {
            var adapter = _startedAdapters[index];
            try
            {
                await adapter.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
            }

            if (!adapter.IsStarted)
            {
                _startedAdapters.RemoveAt(index);
            }
        }

        if (firstFailure is not null)
        {
            throw firstFailure;
        }
    }

    private void UnregisterHandler()
    {
        if (_registration is null || _handlerId is null)
        {
            return;
        }

        var registration = _registration;
        var handlerId = _handlerId;
        if (!registration.TryUnregisterHandler(handlerId))
        {
            throw new InvalidOperationException("The management handler could not be unregistered.");
        }

        _registration = null;
        _handlerId = null;
    }
}
