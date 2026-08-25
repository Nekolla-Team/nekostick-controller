using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Nekolla.Nekostick.Controller.Management;

namespace Nekolla.Nekostick.Controller.Adapters.HttpUnix;

/// <summary>
/// Serves the canonical management API over loopback HTTP/1.1 JSON.
/// </summary>
public sealed class HttpJsonTransportAdapter : IControllerTransportAdapter, IDisposable
{
    private readonly HttpUnixKestrelAdapter _adapter = new(
        ControllerTransport.HttpJson,
        static (kestrel, options) =>
        {
            var port = options.HttpPort ?? throw new InvalidOperationException("The HTTP port is unavailable.");
            kestrel.Listen(IPAddress.Loopback, port, static listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
            kestrel.Listen(IPAddress.IPv6Loopback, port, static listenOptions =>
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
