using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller.Adapters.Grpc;
using Nekolla.Nekostick.Controller.Adapters.HttpUnix;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Coordinates dispatcher, listeners, and the optional HostRoute lifecycle.</summary>
internal sealed class ControllerRuntime
{
    private ControllerOptions _options;
    private readonly List<IControllerTransportAdapter> _transportAdapters;
    private readonly IExtensionHostBridge? _bridge;
    private bool _ephemeralBootstrap;
    private ProvisionedHostRouteIdentity? _bootstrapRouteIdentity;
    private bool _staleBootstrapRoutesCleaned;
    private bool _bootstrapRouteCleanupPending;
    private bool _bootstrapRouteProvisioned;
    private bool _hostRouteRunning;
    private readonly List<IControllerTransportAdapter> _startedAdapters = new();
    private IExtensionRegistration? _registration;
    private string? _handlerId;
    private int _state;

    internal ControllerRuntime(ControllerManagementDispatcher dispatcher, ControllerOptions options)
        : this(dispatcher, options, null, false, null)
    {
    }

    internal ControllerRuntime(
        ControllerManagementDispatcher dispatcher,
        ControllerOptions options,
        IEnumerable<IControllerTransportAdapter>? transportAdapters,
        bool ephemeralBootstrap = false,
        IExtensionHostBridge? bridge = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);

        Dispatcher = dispatcher;
        _options = options;
        _bridge = bridge;
        _ephemeralBootstrap = ephemeralBootstrap;
        _transportAdapters = (transportAdapters ?? CreateDefaultTransportAdapters()).ToList();
        if (_transportAdapters.Any(static adapter => adapter is null))
        {
            throw new ArgumentException("The transport adapter collection contains a null adapter.", nameof(transportAdapters));
        }
    }

    internal ControllerManagementDispatcher Dispatcher { get; }

    internal bool IsStarted => Volatile.Read(ref _state) == 1;

    /// <summary>Gets whether this runtime owns ephemeral bootstrap state.</summary>
    internal bool IsEphemeralBootstrap => _ephemeralBootstrap;

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
            if (_bootstrapRouteIdentity is { } identity)
            {
                var route = snapshot.Routes.SingleOrDefault(candidate => candidate.Id == identity.RouteId);
                var handlerId = _handlerId ?? ControllerManagementApiContract.HandlerId;
                hostRouteRunning = route is not null &&
                    route.Target is ExtensionHandlerRouteTargetConfiguration target &&
                    string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal) &&
                    route.Matcher.Type == RouteMatcherType.Prefix &&
                    string.Equals(route.Matcher.Pattern, identity.CanonicalPath, StringComparison.Ordinal);
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
                        var provisionedIdentity = await Dispatcher.ProvisionBootstrapRouteAsync(
                            _handlerId!,
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
                        var provisionedIdentity = await Dispatcher.ProvisionBootstrapRouteAsync(
                            handler.HandlerId,
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
            if (_bootstrapRouteIdentity is not { } identity)
            {
                throw new InvalidOperationException("The bootstrap route ownership proof is unavailable.");
            }

            var bootstrapRoute = snapshot.Routes.SingleOrDefault(route => route.Id == identity.RouteId);
            var bootstrapPresent = bootstrapRoute is not null &&
                bootstrapRoute.Target is ExtensionHandlerRouteTargetConfiguration target &&
                string.Equals(target.HandlerId, _handlerId, StringComparison.Ordinal) &&
                bootstrapRoute.Matcher.Type == RouteMatcherType.Prefix &&
                string.Equals(bootstrapRoute.Matcher.Pattern, identity.CanonicalPath, StringComparison.Ordinal);
            if (bootstrapRoute is not null && !bootstrapPresent)
            {
                throw new InvalidOperationException("The bootstrap route ownership proof no longer matches.");
            }

            if (candidate.EnableHostRoute)
            {
                await Dispatcher.ReplaceBootstrapWithConfiguredHostRouteAsync(
                    _handlerId,
                    identity,
                    candidate,
                    cancellationToken).ConfigureAwait(false);
                _bootstrapRouteIdentity = null;
                _bootstrapRouteProvisioned = false;
                _bootstrapRouteCleanupPending = false;
                _ephemeralBootstrap = false;
                _hostRouteRunning = true;
            }
            else
            {
                await Dispatcher.RemoveProvisionedHostRouteAsync(
                    _handlerId,
                    identity,
                    cancellationToken).ConfigureAwait(false);
                _bootstrapRouteIdentity = null;
                _bootstrapRouteProvisioned = false;
                _bootstrapRouteCleanupPending = false;
                _ephemeralBootstrap = false;
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
            // Keep any retained registration, route identity, and remaining resources available for retry.
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

        if (_bootstrapRouteIdentity is { } identity)
        {
            await Dispatcher.RemoveProvisionedHostRouteAsync(
                _handlerId,
                identity,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Dispatcher.CleanupStaleBootstrapRoutesAsync(
                _handlerId,
                cancellationToken).ConfigureAwait(false);
        }
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
