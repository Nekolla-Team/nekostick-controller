using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

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
