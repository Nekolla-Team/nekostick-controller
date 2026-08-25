using System.Security.Cryptography;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller.Management;

namespace Nekolla.Nekostick.Controller;

/// <summary>
/// Hosts the controller lifecycle and one shared management dispatcher.
/// Foundation startup hydrates the host-owned settings snapshot, validates admission policy, and
/// composes the configured management transports through the runtime seam.
/// </summary>
public sealed class ControllerEntrypoint : IExtensionEntry, IDisposable
{
    private static readonly HostApiVersion MinimumConfigurationApiVersion = new(1, 3, 0);

    private readonly ControllerOptions? _explicitOptions;
    private readonly IControllerManagementHandlerFactory? _handlerFactory;
    private ControllerOptions? _bootstrapOptions;
    private string? _bootstrapOwnershipMarker;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private ControllerRuntime? _runtime;
    private bool _startupFailed;
    private bool _successfullyStopped;
    private bool _bootstrapSecretEmitted;
    private int _disposed;

    /// <summary>Creates a controller that hydrates options from the host configuration snapshot.</summary>
    public ControllerEntrypoint()
    {
    }

    /// <summary>
    /// Creates a controller with explicit options for an embedding that intentionally supplies its
    /// own configuration source. The host-snapshot constructor remains the default lifecycle path.
    /// </summary>
    /// <param name="options">The local-only options to validate and retain.</param>
    public ControllerEntrypoint(ControllerOptions options)
        : this(options, null)
    {
    }

    /// <summary>
    /// Creates a controller with explicit options and the concrete HostRoute handler factory.
    /// </summary>
    /// <param name="options">The local-only options to validate and retain.</param>
    /// <param name="handlerFactory">The real HostRoute handler factory, when that transport is enabled.</param>
    public ControllerEntrypoint(
        ControllerOptions options,
        IControllerManagementHandlerFactory? handlerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _explicitOptions = options;
        _handlerFactory = handlerFactory;
    }

    /// <summary>
    /// Hydrates or validates options and starts dispatcher admission, enabled listeners, and the
    /// optional HostRoute in that order. A successfully stopped entrypoint cannot be restarted.
    /// </summary>
    /// <param name="context">The Contracts start context.</param>
    /// <param name="cancellationToken">The host lifecycle cancellation token.</param>
    public async ValueTask StartAsync(
        IExtensionStartContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var host13 = EnsureHostApiCompatibility(context);

            if (_successfullyStopped)
            {
                throw new InvalidOperationException("The controller entrypoint cannot be restarted after a successful stop.");
            }

            if (_startupFailed)
            {
                if (_runtime is { } failedRuntime)
                {
                    if (failedRuntime.HasActiveResources)
                    {
                        await failedRuntime.StopAsync(
                            unregisterHandler: false,
                            cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    }

                    if (!failedRuntime.HasRegisteredHandler)
                    {
                        failedRuntime.DisposeStoppedAdapters();
                        _runtime = null;
                    }
                }

                _startupFailed = false;
            }

            if (_runtime is { IsStarted: true })
            {
                return;
            }

            var runtime = _runtime;
            if (runtime is null)
            {
                var hydratedOptions = _explicitOptions ?? await HydrateOptionsAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                if (hydratedOptions is null)
                {
                    throw new InvalidOperationException("Controller configuration is unavailable.");
                }

                var options = GetEffectiveOptions(hydratedOptions);
                if (_explicitOptions is not null && !HasAnyListener(options))
                {
                    ReportDegraded(context, "options.all_listeners_disabled");
                    throw new InvalidOperationException("Controller options leave every listener disabled.");
                }
                var validation = options.Validate();
                if (!validation.IsValid)
                {
                    ReportDegraded(context, $"options.{validation.Error}");
                    throw new InvalidOperationException("Controller options are invalid.");
                }

                var bootstrapMarker = ReferenceEquals(options, _bootstrapOptions)
                    ? _bootstrapOwnershipMarker
                    : null;
                runtime = new ControllerRuntime(
                    new ControllerManagementDispatcher(options, context.Host),
                    options,
                    transportAdapters: null,
                    bootstrapOwnershipMarker: bootstrapMarker,
                    bridge: context.Host);
                _runtime = runtime;
                runtime.Dispatcher.ConfigureRuntimeCallbacks(
                    (expectedVersion, reloadCancellationToken) => ReloadSettingsAsync(context.Host, runtime, expectedVersion, reloadCancellationToken),
                    runtime.GetStateAsync);
            }

            var handlerFactory = _handlerFactory ?? new ControllerManagementHandlerFactory();
            try
            {
                await runtime.StartAsync(
                    context.Registration,
                    handlerFactory,
                    cancellationToken).ConfigureAwait(false);
                if (runtime.IsEphemeralBootstrap && runtime.BootstrapRouteProvisioned)
                {
                    EmitBootstrapSecret(host13);
                }
                context.Host.Status.Report(new ExtensionStatus(ExtensionStatusKind.Healthy, "ready"));
            }
            catch (InvalidOperationException)
            {
                ReportDegraded(context, "startup.failed");
                _startupFailed = true;
                try
                {
                    if (runtime.HasActiveResources)
                    {
                        await runtime.StopAsync(
                            unregisterHandler: false,
                            cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (!runtime.HasActiveResources && !runtime.HasRegisteredHandler)
                    {
                        runtime.DisposeStoppedAdapters();
                        _runtime = null;
                    }
                }

                throw;
            }
            catch
            {
                _startupFailed = true;
                try
                {
                    if (runtime.HasActiveResources)
                    {
                        await runtime.StopAsync(
                            unregisterHandler: false,
                            cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (!runtime.HasActiveResources && !runtime.HasRegisteredHandler)
                    {
                        runtime.DisposeStoppedAdapters();
                        _runtime = null;
                    }
                }

                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async ValueTask<ControllerManagementResponse> ReloadSettingsAsync(
        IExtensionHostBridge bridge,
        ControllerRuntime runtime,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(runtime);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_runtime, runtime) || !runtime.IsStarted)
            {
                return ControllerManagementResponseBuilder.Unavailable;
            }

            return await runtime.ReloadAsync(expectedVersion, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static bool HasAnyListener(ControllerOptions options) =>
        options.EnableHostRoute || options.EnableHttpJson || options.EnableGrpc || options.EnableUnixSocket;

    private ControllerOptions GetEffectiveOptions(ControllerOptions hydratedOptions)
    {
        if (hydratedOptions.EnableHostRoute || hydratedOptions.EnableHttpJson || hydratedOptions.EnableGrpc || hydratedOptions.EnableUnixSocket)
        {
            return hydratedOptions;
        }

        if (_bootstrapOptions is not null)
        {
            return _bootstrapOptions;
        }

        var suffix = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        var secretBytes = new byte[48];
        RandomNumberGenerator.Fill(secretBytes);
        var secret = Convert.ToBase64String(secretBytes);
        CryptographicOperations.ZeroMemory(secretBytes);
        _bootstrapOwnershipMarker = ControllerOptions.BootstrapOwnershipPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _bootstrapOptions = new ControllerOptions
        {
            LoopbackOnly = hydratedOptions.LoopbackOnly,
            EnableHostRoute = true,
            HostRoutePath = "/controller" + suffix,
            ApiKey = secret,
            ApiScope = ControllerApiScope.FullConfiguration
        };
        return _bootstrapOptions;
    }

    private void EmitBootstrapSecret(IExtensionHostBridge13 host13)
    {
        if (_bootstrapSecretEmitted)
        {
            return;
        }

        var options = _bootstrapOptions;
        if (options?.HostRoutePath is not { } routePath || options.ApiKey is not { } apiKey)
        {
            throw new InvalidOperationException("The ephemeral bootstrap secret is unavailable.");
        }

        var message = $"Controller bootstrap route: {routePath}; API key: {apiKey}";
        if (string.IsNullOrWhiteSpace(message) ||
            message.Length > ExtensionLogLimits.MaximumTextLength ||
            message.Any(char.IsControl))
        {
            throw new InvalidOperationException("The ephemeral bootstrap secret message is invalid.");
        }

        // Mark before crossing the host boundary so a failed startup retry cannot duplicate an
        // emission that may already have been accepted by the host logger.
        _bootstrapSecretEmitted = true;
        host13.LogWriter.WriteText(ExtensionLogLevel.Information, message);
    }

    private static IExtensionHostBridge13 EnsureHostApiCompatibility(IExtensionStartContext context)
    {
        if (!ExtensionAbi.IsCompatible(MinimumConfigurationApiVersion, context.Host.ApiVersion) ||
            !ExtensionAbi.IsApi13Supported(context.Host.ApiVersion) ||
            context.Host is not IExtensionHostBridge13 host13 ||
            host13.LogWriter is null)
        {
            ReportDegraded(context, "configuration.host_api_unsupported");
            throw new InvalidOperationException("The controller host API is unsupported.");
        }

        return host13;
    }

    private static async ValueTask<ControllerOptions?> HydrateOptionsAsync(
        IExtensionStartContext context,
        CancellationToken cancellationToken)
    {
        if (!ExtensionAbi.IsCompatible(MinimumConfigurationApiVersion, context.Host.ApiVersion))
        {
            ReportDegraded(context, "configuration.host_api_unsupported");
            return null;
        }

        ConfigurationReadResult<HostConfigurationSnapshot> read;
        try
        {
            read = await context.Host.FullConfiguration
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            ReportDegraded(context, "configuration.read_failed");
            return null;
        }
        catch (NotSupportedException)
        {
            ReportDegraded(context, "configuration.read_failed");
            return null;
        }

        if (!read.IsSuccess || read.Value is not { } snapshot)
        {
            ReportDegraded(context, "configuration.read_failed");
            return null;
        }

        var matchingSettings = snapshot.ExtensionSettings
            .Where(static settings => string.Equals(
                settings.ExtensionId,
                ControllerOptions.ExtensionId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matchingSettings.Length == 0)
        {
            ReportDegraded(context, "configuration.missing");
            return null;
        }

        if (matchingSettings.Length != 1 ||
            !ControllerOptions.TryParseHostSettings(matchingSettings[0], out var options))
        {
            ReportDegraded(context, "configuration.invalid");
            return null;
        }

        return options;
    }

    private static void ReportDegraded(IExtensionStartContext context, string code) =>
        context.Host.Status.Report(new ExtensionStatus(ExtensionStatusKind.Degraded, code));

    /// <summary>
    /// Stops listeners in reverse composition order, then unregisters HostRoute and ends
    /// dispatcher admission. A successful stop makes this entrypoint single-use; cancellation or
    /// failure leaves the runtime available for a later stop retry.
    /// </summary>
    /// <param name="cancellationToken">The host lifecycle cancellation token.</param>
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runtime = _runtime;
            if (runtime is null)
            {
                return;
            }

            var startupCompleted = !_startupFailed;
            var hadRegisteredHandler = runtime.HasRegisteredHandler;
            await runtime.StopAsync(cancellationToken).ConfigureAwait(false);
            runtime.DisposeStoppedAdapters();
            _runtime = null;
            _startupFailed = false;
            if (startupCompleted || hadRegisteredHandler)
            {
                _successfullyStopped = true;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Honors replacement lifecycle cancellation without owning any transport.</summary>
    /// <param name="cancellationToken">The host lifecycle cancellation token.</param>
    public ValueTask OnPreviousStoppedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <summary>Releases the lifecycle gate after a terminal successful stop.</summary>
    void IDisposable.Dispose()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!_lifecycleGate.Wait(0))
        {
            throw new InvalidOperationException("The controller entrypoint lifecycle is active.");
        }

        if (!_successfullyStopped || _runtime is not null)
        {
            _lifecycleGate.Release();
            throw new InvalidOperationException(
                "The controller entrypoint can only be disposed after a successful stop.");
        }

        Volatile.Write(ref _disposed, 1);
        _lifecycleGate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Gets the current shared dispatcher for in-assembly adapters.</summary>
    internal IControllerManagementDispatcher? ManagementDispatcher => _runtime?.Dispatcher;
}
