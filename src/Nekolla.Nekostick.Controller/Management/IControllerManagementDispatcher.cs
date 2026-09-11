using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Dispatches one bounded, transport-neutral management request.</summary>
public interface IControllerManagementDispatcher
{
    /// <summary>Dispatches a request and returns a safe canonical response.</summary>
    ValueTask<ControllerManagementResponse> DispatchAsync(
        ControllerManagementRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches a request whose body is a live transport-owned stream. Only streaming-capable
    /// endpoints (currently the extension package install) are reachable; the stream is read but
    /// never disposed by the dispatcher.
    /// </summary>
    /// <param name="request">The admitted transport-neutral request metadata with an empty body.</param>
    /// <param name="body">The live request body stream owned by the transport.</param>
    /// <param name="cancellationToken">Cancels the dispatch.</param>
    ValueTask<ControllerManagementResponse> DispatchStreamingAsync(
        ControllerManagementRequest request,
        Stream body,
        CancellationToken cancellationToken = default);
}

/// <summary>Exposes the atomically active immutable options to transport request handlers.</summary>
internal interface IControllerManagementOptionsAccessor
{
    /// <summary>Gets the current options snapshot.</summary>
    ControllerOptions CurrentOptions { get; }
}

/// <summary>Reads the active options through the concrete dispatcher when available.</summary>
internal static class ControllerManagementDispatcherOptions
{
    /// <summary>Returns the active dispatcher options, or the startup fallback for test seams.</summary>
    internal static ControllerOptions GetCurrent(
        IControllerManagementDispatcher dispatcher,
        ControllerOptions fallback)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(fallback);
        return dispatcher is IControllerManagementOptionsAccessor accessor
            ? accessor.CurrentOptions
            : fallback;
    }
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

    /// <summary>Creates a streaming handler when the host supports the streaming registration API.</summary>
    /// <param name="dispatcher">Dispatcher that processes management requests.</param>
    /// <param name="options">Controller options used by the handler.</param>
    /// <returns>A streaming handler, or <see langword="null" /> when streaming is unavailable.</returns>
    IExtensionStreamingHandler? CreateStreaming(
        IControllerManagementDispatcher dispatcher,
        ControllerOptions options) => null;
}
