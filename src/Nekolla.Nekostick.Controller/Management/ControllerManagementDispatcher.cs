using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Provides the foundation dispatcher and admission checks.</summary>
public sealed class ControllerManagementDispatcher : IControllerManagementDispatcher, IControllerManagementOptionsAccessor, IDisposable

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
    // Never disposed: live WebSocket pumps link tokens lazily, and disposing a source while a
    // linked registration is in flight would fault the pump instead of ending it.
    private CancellationTokenSource _sessionSource = new();
    private int _inFlight;
    private readonly object _drainSync = new();
    private TaskCompletionSource? _drain;
    private DispatcherConfiguration _configuration;
    private Func<long, CancellationToken, ValueTask<ControllerManagementResponse>>? _reloadHandler;
    private Func<CancellationToken, ValueTask<ControllerStateDto?>>? _stateProvider;
    private Func<CancellationToken, ValueTask<ControllerTelemetryDto>>? _telemetryProvider;
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

    /// <summary>Configures runtime reload, fresh state, and telemetry callbacks.</summary>
    internal void ConfigureRuntimeCallbacks(
        Func<long, CancellationToken, ValueTask<ControllerManagementResponse>> reloadHandler,
        Func<CancellationToken, ValueTask<ControllerStateDto?>> stateProvider,
        Func<CancellationToken, ValueTask<ControllerTelemetryDto>>? telemetryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(reloadHandler);
        ArgumentNullException.ThrowIfNull(stateProvider);
        Volatile.Write(ref _reloadHandler, reloadHandler);
        Volatile.Write(ref _stateProvider, stateProvider);
        Volatile.Write(ref _telemetryProvider, telemetryProvider);
        var current = Volatile.Read(ref _configuration);
        Volatile.Write(ref _configuration, new DispatcherConfiguration(
            current.Options,
            new ControllerManagementCore(current.Options, _bridge, reloadHandler, stateProvider, _mutationGate, admissionProbe: () => IsStarted, telemetryProvider)));
    }

    /// <summary>Atomically swaps the options used by admission and core dispatch.</summary>
    internal void SwapOptions(ControllerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var current = Volatile.Read(ref _configuration);
        var reloadHandler = Volatile.Read(ref _reloadHandler);
        var stateProvider = Volatile.Read(ref _stateProvider);
        var core = new ControllerManagementCore(options, _bridge, reloadHandler, stateProvider, _mutationGate, admissionProbe: () => IsStarted, Volatile.Read(ref _telemetryProvider));
        Volatile.Write(ref _configuration, new DispatcherConfiguration(options, core));
        if (!string.Equals(current.Options.ApiKey, options.ApiKey, StringComparison.Ordinal))
        {
            // A rotated API key revokes every session admitted under the old one; live output
            // streams observe the cancellation and close instead of outliving the credential.
            var sessionSource = Volatile.Read(ref _sessionSource);
            sessionSource.Cancel();
            Interlocked.CompareExchange(ref _sessionSource, new CancellationTokenSource(), sessionSource);
        }
    }

    /// <summary>Gets the active dispatcher options for runtime reconciliation.</summary>
    internal ControllerOptions Options => Volatile.Read(ref _configuration).Options;

    ControllerOptions IControllerManagementOptionsAccessor.CurrentOptions => Options;

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
        var sessionSource = Volatile.Read(ref _sessionSource);
        if (sessionSource.IsCancellationRequested)
        {
            Interlocked.CompareExchange(ref _sessionSource, new CancellationTokenSource(), sessionSource);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Stops admission idempotently.</summary>
    /// <remarks>
    /// Reload stops admission transiently and must not disturb admitted sessions, so ending
    /// sessions is a separate explicit operation reserved for teardown and key rotation.
    /// </remarks>
    internal ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _state, 0);
        return ValueTask.CompletedTask;
    }

    /// <summary>Ends every admitted session; used when the runtime is torn down for good.</summary>
    internal void EndSessions() => Volatile.Read(ref _sessionSource).Cancel();

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
            if (Admit(request, configuration.Options) is { } rejection)
            {
                return rejection;
            }

            return await configuration.Core.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndDispatch();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ControllerManagementResponse> DispatchStreamingAsync(
        ControllerManagementRequest request,
        Stream body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(body);
        Interlocked.Increment(ref _inFlight);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsStarted)
            {
                return ControllerManagementResponseBuilder.Unavailable;
            }

            var configuration = Volatile.Read(ref _configuration);
            if (Admit(request, configuration.Options) is { } rejection)
            {
                return rejection;
            }

            return await configuration.Core.DispatchStreamingAsync(request, body, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndDispatch();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ControllerServiceOutputStreamResult> DispatchServiceOutputStreamAsync(
        ControllerManagementRequest request,
        Guid serviceId,
        ExtensionServiceOutputStream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Interlocked.Increment(ref _inFlight);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsStarted)
            {
                return ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponseBuilder.Unavailable);
            }

            var configuration = Volatile.Read(ref _configuration);
            if (Admit(request, configuration.Options) is { } rejection)
            {
                return ControllerServiceOutputStreamResult.Rejected(rejection);
            }

            var sessionEnded = Volatile.Read(ref _sessionSource).Token;
            var result = await configuration.Core
                .OpenServiceOutputStreamAsync(serviceId, stream, cancellationToken)
                .ConfigureAwait(false);
            return result.Stream is { } opened
                ? ControllerServiceOutputStreamResult.Opened(opened, sessionEnded)
                : result;
        }
        finally
        {
            EndDispatch();
        }
    }

    private static ControllerManagementResponse? Admit(ControllerManagementRequest request, ControllerOptions options)
    {
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

        return null;
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
        CancellationToken cancellationToken) =>
        Volatile.Read(ref _configuration).Core.ProvisionHostRouteAsync(handlerId, options, cancellationToken);

    /// <summary>Provisions an ephemeral private route and returns its in-memory ownership identity.</summary>
    internal ValueTask<ProvisionedHostRouteIdentity?> ProvisionBootstrapRouteAsync(
        string handlerId,
        CancellationToken cancellationToken) =>
        Volatile.Read(ref _configuration).Core.ProvisionBootstrapRouteAsync(handlerId, cancellationToken);

    /// <summary>Atomically replaces a verified bootstrap route with a configured route.</summary>
    internal ValueTask ReplaceBootstrapWithConfiguredHostRouteAsync(
        string handlerId,
        ProvisionedHostRouteIdentity identity,
        ControllerOptions options,
        CancellationToken cancellationToken) =>
        Volatile.Read(ref _configuration).Core.ReplaceBootstrapWithConfiguredHostRouteAsync(handlerId, identity, options, cancellationToken);

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

    /// <summary>Removes only an identity-proven private bootstrap route.</summary>
    internal ValueTask RemoveProvisionedHostRouteAsync(
        string handlerId,
        ProvisionedHostRouteIdentity identity,
        CancellationToken cancellationToken) =>
        Volatile.Read(ref _configuration).Core.RemoveProvisionedHostRouteAsync(handlerId, identity, cancellationToken);

    /// <summary>Removes only stale private bootstrap routes for this controller handler.</summary>
    internal ValueTask CleanupStaleBootstrapRoutesAsync(string handlerId, CancellationToken cancellationToken) =>
        Volatile.Read(ref _configuration).Core.CleanupStaleBootstrapRoutesAsync(handlerId, cancellationToken);

}
