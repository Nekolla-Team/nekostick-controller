using Microsoft.AspNetCore.Server.Kestrel.Core;
using Nekolla.Nekostick.Controller.Management;

namespace Nekolla.Nekostick.Controller.Adapters.HttpUnix;

/// <summary>
/// Serves the canonical management API over a local Unix-domain HTTP/1.1 socket.
/// </summary>
public sealed class UnixSocketTransportAdapter : IControllerTransportAdapter, IDisposable
{
    private readonly HttpUnixKestrelAdapter _adapter = new(
        ControllerTransport.UnixSocket,
        static (kestrel, options) =>
        {
            var path = options.UnixSocketPath ?? throw new InvalidOperationException("The Unix socket path is unavailable.");
            kestrel.ListenUnixSocket(path, static listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });

    /// <inheritdoc />
    public ControllerTransport Transport => _adapter.Transport;

    /// <inheritdoc />
    public bool IsStarted => _adapter.IsStarted;

    /// <inheritdoc />
    public ValueTask StartAsync(
        IControllerManagementDispatcher dispatcher,
        ControllerOptions options,
        CancellationToken cancellationToken = default) =>
        _adapter.StartAsync(dispatcher, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
        _adapter.StopAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _adapter.Dispose();
}
