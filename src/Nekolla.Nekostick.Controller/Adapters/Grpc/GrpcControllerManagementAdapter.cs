using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Nekolla.Nekostick.Controller.Adapters.Grpc;

/// <summary>
/// Self-hosts the controller management gRPC service on a loopback-only HTTP/2 endpoint.
/// </summary>
public sealed class GrpcControllerManagementAdapter : IControllerTransportAdapter, IDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _application;
    private int _started;
    private int _disposed;
    /// <summary>Releases the lifecycle gate after the listener has been stopped.</summary>
    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        if (Volatile.Read(ref _application) is not null || IsStarted)
        {
            throw new InvalidOperationException("The gRPC adapter must be stopped before disposal.");
        }

        bool gateTaken;
        try
        {
            gateTaken = _lifecycle.Wait(0);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        if (!gateTaken)
        {
            throw new InvalidOperationException("The gRPC adapter lifecycle is busy.");
        }

        try
        {
            if (_application is not null || IsStarted)
            {
                throw new InvalidOperationException("The gRPC adapter must be stopped before disposal.");
            }

            Volatile.Write(ref _disposed, 1);
            _lifecycle.Dispose();
        }
        catch
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _lifecycle.Release();
            }

            throw;
        }

        GC.SuppressFinalize(this);
    }


    /// <summary>Gets the transport owned by this adapter.</summary>
    public ControllerTransport Transport => ControllerTransport.Grpc;

    /// <summary>Gets whether the loopback gRPC listener is accepting requests.</summary>
    public bool IsStarted => Volatile.Read(ref _started) == 1;

    /// <inheritdoc />
    public async ValueTask StartAsync(
        IControllerManagementDispatcher dispatcher,
        ControllerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsStarted)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!options.EnableGrpc)
            {
                // A disabled transport is deliberately a stopped adapter: no host or listener is
                // created, leaving Wave 3 free to compose only explicitly enabled transports.
                return;
            }

            var validation = options.Validate();
            if (!validation.IsValid || options.GrpcPort is not int port)
            {
                throw new InvalidOperationException("Controller gRPC options are invalid.");
            }

            WebApplication? application = null;
            try
            {
                application = await BuildApplication(dispatcher, port).ConfigureAwait(false);
                await application.StartAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _application = application;
                Volatile.Write(ref _started, 1);
            }
            catch
            {
                if (application is not null)
                {
                    await RollbackAsync(application).ConfigureAwait(false);
                }

                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsStarted)
            {
                return;
            }

            var application = _application;
            if (application is null)
            {
                // Keep the state conservative if an invariant is ever broken. A later stop/start
                // call can still recover without exposing a partially initialized listener.
                Volatile.Write(ref _started, 0);
                return;
            }

            await application.StopAsync(cancellationToken).ConfigureAwait(false);
            await application.DisposeAsync().ConfigureAwait(false);
            _application = null;
            Volatile.Write(ref _started, 0);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private static async ValueTask<WebApplication> BuildApplication(
        IControllerManagementDispatcher dispatcher,
        int port)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(GrpcControllerManagementAdapter).Assembly.GetName().Name
                ?? "Nekolla.Nekostick.Controller",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
            Args = Array.Empty<string>()
        });

        // Do not allow ambient Kestrel endpoint configuration to add a non-loopback listener.
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.Limits.MaxRequestBodySize = ControllerAdmissionLimits.MaximumRequestBodyBytes;
            serverOptions.Limits.MaxRequestHeaderCount = ControllerAdmissionLimits.MaximumHeaderCount;
            serverOptions.Limits.MaxRequestHeadersTotalSize = ControllerAdmissionLimits.MaximumAggregateHeaderBytes;
            serverOptions.Listen(IPAddress.Loopback, port, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
            });
            serverOptions.Listen(IPAddress.IPv6Loopback, port, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
            });
        });

        builder.Services.AddGrpc(grpcOptions =>
        {
            // The canonical JSON body is capped at 1 MiB. gRPC message limits cover the
            // serialized InvokeRequest/InvokeResponse envelopes too, so reserve the bounded
            // header budget plus method/path and per-field protobuf framing overhead without
            // raising the inner admission caps.
            grpcOptions.MaxReceiveMessageSize = ControllerAdmissionLimits.MaximumRequestBodyBytes +
                ControllerAdmissionLimits.MaximumAggregateHeaderBytes + (32 * 1024);
            grpcOptions.MaxSendMessageSize = ControllerManagementJson.MaximumResponseBodyBytes +
                ControllerAdmissionLimits.MaximumAggregateHeaderBytes + (32 * 1024);
            grpcOptions.EnableDetailedErrors = false;
        });
        builder.Services.AddSingleton(new ControllerManagementGrpcService(dispatcher));

        var application = builder.Build();
        try
        {
            application.MapGrpcService<ControllerManagementGrpcService>();
            return application;
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask RollbackAsync(WebApplication application)
    {
        try
        {
            await application.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original start/bind failure; rollback remains best effort.
        }

        try
        {
            await application.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original start/bind failure; rollback remains best effort.
        }
    }
}
