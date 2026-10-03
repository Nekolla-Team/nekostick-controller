using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Nekolla.Nekostick.Controller.Management;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Adapters.HttpUnix;

/// <summary>
/// Owns one Kestrel host and keeps the HTTP and Unix transport lifecycle behavior identical.
/// </summary>
internal sealed class HttpUnixKestrelAdapter : IDisposable
{
    private const string ApiKeyHeaderName = "x-nekostick-controller-key";
    private const string WebUiHtmlContentType = "text/html";
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailer",
        "transfer-encoding",
        "upgrade",
        "proxy-connection"
    };

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ControllerTransport _transport;
    private readonly Action<KestrelServerOptions, ControllerOptions> _configureListeners;
    private IHost? _host;
    private string? _ownedSocketPath;
    private int _started;
    private int _disposed;

    internal HttpUnixKestrelAdapter(
        ControllerTransport transport,
        Action<KestrelServerOptions, ControllerOptions> configureListeners)
    {
        _transport = transport;
        _configureListeners = configureListeners;
    }

    internal ControllerTransport Transport => _transport;

    internal bool IsStarted => Volatile.Read(ref _started) == 1;

    internal async ValueTask StartAsync(
        IControllerManagementDispatcher dispatcher,
        ControllerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_host is not null)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // A disabled adapter is deliberately a no-op. This lets Wave 3 compose all
            // adapters without accidentally starting a listener for an unset option.
            if (!options.IsTransportEnabled(_transport))
            {
                return;
            }

            var validation = options.Validate();
            if (!validation.IsValid)
            {
                throw new InvalidOperationException("Controller options are invalid.");
            }
            if (_transport == ControllerTransport.UnixSocket && OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Unix-domain socket mode enforcement is unavailable on this platform.");
            }
            if (_ownedSocketPath is not null)
            {
                if (!DeleteOwnedSocket())
                {
                    throw new InvalidOperationException("The previous Unix socket path is unsafe or occupied.");
                }

                _ownedSocketPath = null;
            }

            var socketPath = _transport == ControllerTransport.UnixSocket
                ? options.UnixSocketPath ?? throw new InvalidOperationException("The Unix socket path is unavailable.")
                : null;
            var socketWasAbsent = false;
            if (socketPath is not null)
            {
                EnsureSocketPathIsUnused(socketPath);
                socketWasAbsent = true;
            }

            IHost? host = null;
            try
            {
                host = BuildHost(dispatcher, options);
                if (socketPath is not null)
                {
                    // Revalidate immediately before Kestrel can bind the pathname.
                    EnsureSocketParentChainIsTrusted(socketPath);
                }

                await host.StartAsync(cancellationToken).ConfigureAwait(false);

                if (socketPath is not null)
                {
                    // Kestrel has successfully bound this previously absent path. Record it as
                    // a candidate before verification so failed startup can retry safe cleanup.
                    _ownedSocketPath = socketPath;
                    EnsureSocketWasCreated(socketPath, socketWasAbsent);
                    SetAndVerifySocketMode(socketPath);
                }

                cancellationToken.ThrowIfCancellationRequested();
                _host = host;
                Volatile.Write(ref _started, 1);
            }
            catch
            {
                if (host is not null)
                {
                    try
                    {
                        await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Startup must report the original failure; disposal below still
                        // releases the host even if a partially started server cannot stop.
                    }

                    try
                    {
                        host.Dispose();
                    }
                    catch
                    {
                        // Preserve the original startup failure and retain any socket marker.
                    }
                }

                var socketCleanupFailed = false;
                if (_ownedSocketPath is not null)
                {
                    if (DeleteOwnedSocket())
                    {
                        _ownedSocketPath = null;
                    }
                    else
                    {
                        socketCleanupFailed = true;
                    }
                }

                // A retained marker means rollback is non-terminal. Runtime teardown can retry
                // StopAsync without ever unlinking a path that fails the ownership checks.
                Volatile.Write(ref _started, socketCleanupFailed ? 1 : 0);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var host = _host;
            if (host is null)
            {
                if (_ownedSocketPath is not null)
                {
                    if (!DeleteOwnedSocket())
                    {
                        Volatile.Write(ref _started, 1);
                        throw new InvalidOperationException("The owned Unix socket could not be safely removed.");
                    }

                    _ownedSocketPath = null;
                }

                Volatile.Write(ref _started, 0);
                return;
            }

            // Passing the caller token to Kestrel is intentional. If it is cancelled, retain
            // the host and ownership markers so a later StopAsync can retry the active adapter.
            await host.StopAsync(cancellationToken).ConfigureAwait(false);
            host.Dispose();
            _host = null;

            if (_ownedSocketPath is not null)
            {
                if (!DeleteOwnedSocket())
                {
                    // The listener is stopped, but ownership cannot be released safely. Keep
                    // the non-terminal lifecycle state so a later stop can retry cleanup.
                    Volatile.Write(ref _started, 1);
                    throw new InvalidOperationException("The owned Unix socket could not be safely removed.");
                }

                _ownedSocketPath = null;
            }

            Volatile.Write(ref _started, 0);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        if (!_lifecycleGate.Wait(0))
        {
            Volatile.Write(ref _disposed, 0);
            throw new InvalidOperationException("The HTTP/Unix transport lifecycle is still active.");
        }

        try
        {
            if (_host is not null || _ownedSocketPath is not null)
            {
                Volatile.Write(ref _disposed, 0);
                throw new InvalidOperationException("StopAsync must complete before the HTTP/Unix transport is disposed.");
            }

            Volatile.Write(ref _started, 0);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        _lifecycleGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private IHost BuildHost(
        IControllerManagementDispatcher dispatcher,
        ControllerOptions options)
    {
        // CORS only matters for browser clients of the HTTP listener; the Unix socket transport
        // is unreachable from browsers, so preflight handling stays disabled there.
        var corsOrigins = _transport == ControllerTransport.HttpJson
            ? options.CorsAllowedOrigins
            : ImmutableArray<string>.Empty;

        var builder = new HostBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureAppConfiguration((_, configuration) => configuration.Sources.Clear());
                if (!corsOrigins.IsEmpty)
                {
                    webBuilder.ConfigureServices(services => services.AddCors());
                }

                webBuilder.UseKestrel(kestrel =>
                {
                    kestrel.Limits.MaxRequestBodySize = ControllerAdmissionLimits.MaximumRequestBodyBytes;
                    kestrel.Limits.MaxRequestHeadersTotalSize = ControllerAdmissionLimits.MaximumAggregateHeaderBytes;
                    kestrel.Limits.MaxRequestHeaderCount = ControllerAdmissionLimits.MaximumHeaderCount;
                    kestrel.Limits.MaxRequestLineSize = 16 * 1024;
                    _configureListeners(kestrel, options);
                });
                webBuilder.Configure(application =>
                {
                    if (!corsOrigins.IsEmpty)
                    {
                        // Preflight OPTIONS is answered by the middleware before the terminal
                        // handler, so cross-origin probes never reach the API key check. The
                        // unauthenticated Web UI shell is intentionally outside CORS handling.
                        application.UseWhen(
                            context => !ControllerWebUiResource.IsRootPath(context.Request.Path.Value ?? string.Empty),
                            branch =>
                            {
                                branch.UseCors(policy =>
                                {
                                    if (corsOrigins.Contains("*", StringComparer.Ordinal))
                                    {
                                        policy.AllowAnyOrigin();
                                    }
                                    else
                                    {
                                        policy.WithOrigins(corsOrigins.ToArray());
                                    }

                                    policy.WithMethods("GET", "POST", "PUT", "DELETE", "PATCH");
                                    policy.WithHeaders(
                                        "Content-Type",
                                        ControllerManagementApiContract.IfMatchHeaderName,
                                        ControllerManagementApiContract.ApiKeyHeaderName);
                                    policy.WithExposedHeaders(
                                        ControllerManagementApiContract.ETagHeaderName,
                                        ControllerManagementApiContract.LocationHeaderName);
                                });
                            });
                    }

                    // Installs the upgrade feature the service-output endpoint accepts with;
                    // without it Kestrel never reports IsWebSocketRequest. Keep-alive bounds how
                    // long a half-open peer can pin a Host output subscription.
                    application.UseWebSockets(new WebSocketOptions
                    {
                        KeepAliveInterval = TimeSpan.FromSeconds(30),
                        KeepAliveTimeout = TimeSpan.FromSeconds(30)
                    });

                    application.Run(context => HandleRequestAsync(context, dispatcher, options));
                });
            });

        return builder.Build();
    }

    private async Task HandleRequestAsync(
        HttpContext context,
        IControllerManagementDispatcher dispatcher,
        ControllerOptions startupOptions)
    {
        var cancellationToken = context.RequestAborted;
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var requestPath = context.Request.Path.Value ?? string.Empty;
        if (string.Equals(context.Request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
            ControllerManagementDispatcherOptions.GetCurrent(dispatcher, startupOptions).EnableWebUi)
        {
            if (ControllerWebUiResource.IsRootPath(requestPath))
            {
                var shell = ControllerWebUiResource.OpenRead();
                if (shell is not null)
                {
                    await WriteWebUiResourceAsync(
                        context,
                        shell,
                        WebUiHtmlContentType,
                        cacheControl: null,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else if (ControllerWebUiResource.TryGetAssetFileName(requestPath, hostRoutePath: null, out var assetName) &&
                ControllerWebUiResource.OpenAsset(assetName) is { } asset)
            {
                await WriteWebUiResourceAsync(
                    context,
                    asset,
                    ControllerWebUiResource.ContentTypeFor(assetName),
                    ControllerWebUiResource.AssetCacheControl,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }

            // The extension install endpoint carries a package larger than the shared 1 MiB
            // buffered ceiling, so it is dispatched directly on the request stream before the
            // buffered-content-length gate applies.
            if (string.Equals(context.Request.Method, "POST", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(requestPath, ControllerManagementApiContract.ExtensionsInstallPath, StringComparison.Ordinal))
            {
                await HandleInstallAsync(context, dispatcher, requestPath, cancellationToken).ConfigureAwait(false);
                return;
            }

        // The service-output WebSocket endpoint upgrades the connection and must run before the
        // buffered-body admission path; the upgrade has no request body to bound.
        if (string.Equals(context.Request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
            TryParseServiceOutputStreamPath(requestPath, out var outputServiceId))
        {
            await HandleServiceOutputStreamAsync(context, dispatcher, outputServiceId, requestPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        // The runtime-state feed is a plain SSE GET; it never upgrades and has no request body to
        // bound, so it runs before the buffered-body admission path.
        if (string.Equals(context.Request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(requestPath, ControllerManagementApiContract.ServiceRuntimeFeedPath, StringComparison.Ordinal))
        {
            await HandleServiceRuntimeFeedAsync(context, dispatcher, requestPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (context.Request.ContentLength is > ControllerAdmissionLimits.MaximumRequestBodyBytes)
        {
            await WriteResponseAsync(context, ControllerManagementResponse.InvalidRequest, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!TryCopyHeaders(context.Request.Headers, out var headers, out var apiKey))
        {
            await WriteResponseAsync(context, ControllerManagementResponse.InvalidRequest, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var path = requestPath;
        if (context.Request.QueryString.HasValue)
        {
            // Preserve the query in the transport-neutral path. The canonical core currently
            // rejects query-bearing management paths rather than silently changing their meaning.
            path += context.Request.QueryString.Value;
        }

        var rentedBody = ArrayPool<byte>.Shared.Rent(ControllerAdmissionLimits.MaximumRequestBodyBytes + 1);
        try
        {
            var bodyLength = await ReadBodyAsync(context.Request, rentedBody, cancellationToken).ConfigureAwait(false);
            if (bodyLength < 0 ||
                !ControllerAdmissionLimits.TryCreateRequest(
                    _transport,
                    context.Request.Method,
                    path,
                    apiKey,
                    headers,
                    rentedBody.AsMemory(0, bodyLength),
                    out var request) ||
                request is null)
            {
                await WriteResponseAsync(context, ControllerManagementResponse.InvalidRequest, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            ControllerManagementResponse response;
            try
            {
                response = await dispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                response = ControllerManagementResponse.Unavailable;
            }

            await WriteResponseAsync(context, response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The peer disconnected or Kestrel is stopping; no response can be written.
        }
        catch (IOException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await WriteResponseAsync(context, ControllerManagementResponse.InvalidRequest, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBody, clearArray: true);
        }
    }

    private async Task HandleInstallAsync(
        HttpContext context,
        IControllerManagementDispatcher dispatcher,
        string requestPath,
        CancellationToken cancellationToken)
    {
        if (!TryCopyHeaders(context.Request.Headers, out var headers, out var apiKey) ||
            !ControllerAdmissionLimits.TryCreateRequest(
                _transport,
                "POST",
                requestPath,
                apiKey,
                headers!,
                ReadOnlyMemory<byte>.Empty,
                out var request) ||
            request is null)
        {
            await WriteResponseAsync(context, ControllerManagementResponse.InvalidRequest, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Package bounds are enforced by the installer's bounded copy, which turns oversized
        // uploads into a 400 instead of a transport-level abort.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySizeFeature)
        {
            bodySizeFeature.MaxRequestBodySize = null;
        }

        ControllerManagementResponse response;
        try
        {
            response = await dispatcher.DispatchStreamingAsync(request, context.Request.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            response = ControllerManagementResponse.Unavailable;
        }

        await WriteResponseAsync(context, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleServiceOutputStreamAsync(
        HttpContext context,
        IControllerManagementDispatcher dispatcher,
        Guid serviceId,
        string requestPath,
        CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            // The endpoint exists only as a WebSocket upgrade; plain GET carries no meaning.
            await WriteResponseAsync(context, ControllerManagementResponse.InvalidRequest, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryParseOutputStreamQuery(context.Request.Query, out var outputStream, out var sinceSequence) ||
            !TryCopyHeaders(context.Request.Headers, out var headers, out var apiKey))
        {
            await WriteResponseAsync(context, ControllerManagementResponse.InvalidRequest, cancellationToken).ConfigureAwait(false);
            return;
        }

        var effectiveKey = ResolveOutputStreamApiKey(context.WebSockets.WebSocketRequestedProtocols, apiKey);
        if (apiKey is null && effectiveKey is not null)
        {
            // Admission cross-checks the key against the canonical header channel; surface the
            // subprotocol credential there so browser clients authenticate identically.
            headers.Add(new KeyValuePair<string, IEnumerable<string>>(
                ControllerManagementApiContract.ApiKeyHeaderName,
                new[] { effectiveKey }));
        }

        if (!ControllerAdmissionLimits.TryCreateRequest(
                _transport,
                "GET",
                requestPath,
                effectiveKey,
                headers,
                ReadOnlyMemory<byte>.Empty,
                out var request) ||
            request is null)
        {
            await WriteResponseAsync(context, ControllerManagementResponse.InvalidRequest, cancellationToken).ConfigureAwait(false);
            return;
        }

        ControllerServiceLogFeedResult logResult;
        try
        {
            logResult = await dispatcher.DispatchServiceLogFeedAsync(request, serviceId, sinceSequence, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            logResult = ControllerServiceLogFeedResult.Rejected(ControllerManagementResponse.Unavailable);
        }

        if (logResult.Rejection?.Code == ControllerDispatchCode.Unsupported)
        {
            if (logResult.Feed is { } unsupportedFeed)
            {
                await unsupportedFeed.DisposeAsync().ConfigureAwait(false);
            }
        }
        else
        {
            if (logResult.Feed is not { } feed)
            {
                await WriteResponseAsync(
                    context,
                    logResult.Rejection ?? ControllerManagementResponse.Unavailable,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                await PumpServiceLogAsync(context, feed, logResult.SessionEnded).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The peer dropped between the open and the upgrade; the pump already detached the feed.
            }
            catch (WebSocketException)
            {
                // The upgrade failed after the feed opened; the pump's async-disposal scope detached it.
            }
            catch (IOException)
            {
                // The connection was torn down while the feed was being upgraded or closed.
            }

            return;
        }

        ControllerServiceOutputStreamResult result;
        try
        {
            result = await dispatcher.DispatchServiceOutputStreamAsync(request, serviceId, outputStream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            result = ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponse.Unavailable);
        }

        if (result.Stream is not { } output)
        {
            await WriteResponseAsync(
                context,
                result.Rejection ?? ControllerManagementResponse.Unavailable,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await PumpServiceOutputAsync(context, output, result.SessionEnded, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The peer dropped between the open and the upgrade; the stream is already disposed.
        }
        catch (WebSocketException)
        {
            // The upgrade failed after the stream opened; the pump's using scope detached it.
        }
    }

    /// <summary>
    /// Pumps the node-local runtime-state feed into one SSE response until the session ends, the
    /// peer disconnects, or the listener stops. The feed view detaches when the event stream is
    /// disposed on the way out.
    /// </summary>
    private async Task HandleServiceRuntimeFeedAsync(
        HttpContext context,
        IControllerManagementDispatcher dispatcher,
        string requestPath,
        CancellationToken cancellationToken)
    {
        if (!TryCopyHeaders(context.Request.Headers, out var headers, out var apiKey))
        {
            await WriteResponseAsync(context, ControllerManagementResponse.InvalidRequest, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!ControllerAdmissionLimits.TryCreateRequest(
                _transport,
                "GET",
                requestPath,
                apiKey,
                headers,
                ReadOnlyMemory<byte>.Empty,
                out var request) ||
            request is null)
        {
            await WriteResponseAsync(context, ControllerManagementResponse.InvalidRequest, cancellationToken).ConfigureAwait(false);
            return;
        }

        ControllerRuntimeFeedResult result;
        try
        {
            result = dispatcher.DispatchServiceRuntimeFeed(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            result = ControllerRuntimeFeedResult.Rejected(ControllerManagementResponse.Unavailable);
        }

        if (result.View is not { } view)
        {
            await WriteResponseAsync(context, result.Rejection ?? ControllerManagementResponse.Unavailable, cancellationToken).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = ControllerManagementApiContract.ServiceOutputEventStreamMediaType;
        context.Response.Headers.CacheControl = "no-store";

        using var aborted = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.RequestAborted);
        try
        {
            await using var eventStream = ControllerRuntimeFeedEventStream.Start(view, result.SessionEnded);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await eventStream.ReadAsync(buffer, aborted.Token).ConfigureAwait(false)) > 0)
            {
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), aborted.Token).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(aborted.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            // Client disconnect or listener stop; the stream disposal already detached the view.
        }
    }

    /// <summary>
    /// Pumps one opened Host output stream into WebSocket binary messages until the process
    /// generation ends, the peer disconnects, or the listener stops. Disposing the stream on the
    /// way out detaches the Host subscription.
    /// </summary>
    private static async Task PumpServiceOutputAsync(
        HttpContext context,
        Stream output,
        CancellationToken sessionEnded,
        CancellationToken cancellationToken)
    {
        // Ownership is taken before the upgrade so a failed accept still detaches the Host stream.
        await using (output.ConfigureAwait(false))
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var stopping = context.RequestServices
                .GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
            // hardStop: peer disconnect, listener stop, or session revocation. closeSource adds the
            // peer's polite close frame. Sends use hardStop so a close frame arriving mid-send does
            // not abort the socket; the Host-stream read uses closeSource so a polite close unblocks
            // an idle pump. The socket close-watch receives on watchStop instead: canceling a
            // pending ManagedWebSocket receive aborts the connection, which would destroy the
            // graceful close handshake the finally block below still has to send.
            using var hardStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping, sessionEnded);
            using var closeSource = CancellationTokenSource.CreateLinkedTokenSource(hardStop.Token);
            using var watchStop = new CancellationTokenSource();
            var closeTask = ReceiveClientCloseAsync(socket, closeSource, watchStop.Token);
            var closeStatus = WebSocketCloseStatus.NormalClosure;
            var closeDescription = "The service process exited.";
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                while (!closeSource.IsCancellationRequested)
                {
                    int read;
                    try
                    {
                        read = await output.ReadAsync(buffer.AsMemory(), closeSource.Token).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // Host teardown or a fan-out fault ends the stream with an IOException.
                        closeStatus = WebSocketCloseStatus.InternalServerError;
                        closeDescription = "The service output stream faulted.";
                        break;
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    await socket.SendAsync(
                        buffer.AsMemory(0, read),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        hardStop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (closeSource.IsCancellationRequested)
            {
                // The peer closed, the session ended, or the transport is stopping.
            }
            catch (WebSocketException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                if (!cancellationToken.IsCancellationRequested)
                {
                    if (closeSource.IsCancellationRequested && closeStatus == WebSocketCloseStatus.NormalClosure)
                    {
                        if (sessionEnded.IsCancellationRequested)
                        {
                            closeStatus = WebSocketCloseStatus.EndpointUnavailable;
                            closeDescription = "The management session ended.";
                        }
                        else if (stopping.IsCancellationRequested)
                        {
                            closeStatus = WebSocketCloseStatus.EndpointUnavailable;
                            closeDescription = "The transport is shutting down.";
                        }
                        else
                        {
                            closeDescription = "The client closed the session.";
                        }
                    }

                    // CloseOutputAsync only SENDS the close frame: the pending receive in closeTask
                    // then observes the peer's answer and completes the handshake. CloseAsync would
                    // contend with that in-flight receive and canceling the receive first would
                    // abort the upgraded connection, so the ordering here is deliberate. The sends
                    // are bounded so a non-reading peer cannot pin the Host stream forever.
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        if (socket.State == WebSocketState.Open)
                        {
                            await socket.CloseOutputAsync(closeStatus, closeDescription, closeTimeout.Token).ConfigureAwait(false);
                        }
                        else if (socket.State == WebSocketState.CloseReceived)
                        {
                            // The receive task already consumed the peer's close frame; answering it
                            // completes the handshake without another receive.
                            await socket.CloseAsync(closeStatus, closeDescription, closeTimeout.Token).ConfigureAwait(false);
                        }
                    }
                    catch (WebSocketException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                    catch (OperationCanceledException)
                    {
                        // The peer stopped reading; the close frame could not leave in time.
                    }
                }

                try
                {
                    await closeTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The peer never answered the close frame; cancel the abandoned receive.
                }
                catch (WebSocketException)
                {
                }
                catch (IOException)
                {
                }

                watchStop.Cancel();
                closeSource.Cancel();
                try
                {
                    await closeTask.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // The receive observes a torn-down connection; nothing left to recover.
                }
            }
        }
    }

    /// <summary>
    /// Pumps one cross-generation service-log feed into WebSocket frames until its termination
    /// entry, the peer disconnects, the session ends, or the listener stops.
    /// </summary>
    private static async Task PumpServiceLogAsync(
        HttpContext context,
        ControllerServiceLogFeed feed,
        CancellationToken sessionEnded)
    {
        await using (feed.ConfigureAwait(false))
        {
            var stopping = context.RequestServices
                .GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
            using var hardStop = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted,
                stopping,
                sessionEnded);
            if (hardStop.IsCancellationRequested)
            {
                return;
            }

            WebSocket socket;
            try
            {
                socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (hardStop.IsCancellationRequested)
            {
                return;
            }

            using var socketScope = socket;
            using var closeSource = CancellationTokenSource.CreateLinkedTokenSource(hardStop.Token);
            using var watchStop = new CancellationTokenSource();
            var closeTask = ReceiveClientCloseAsync(socket, closeSource, watchStop.Token);
            var closeStatus = WebSocketCloseStatus.NormalClosure;
            var closeDescription = "ended";
            try
            {
                while (!closeSource.IsCancellationRequested)
                {
                    ControllerServiceLogFeedItem item;
                    try
                    {
                        item = await feed.Reader.ReadAsync(closeSource.Token).ConfigureAwait(false);
                    }
                    catch (ChannelClosedException)
                    {
                        break;
                    }

                    if (string.Equals(item.Entry.Kind, "output", StringComparison.Ordinal))
                    {
                        if (item.Data.IsDefaultOrEmpty)
                        {
                            continue;
                        }

                        var data = ImmutableCollectionsMarshal.AsArray(item.Data)!;
                        await socket.SendAsync(
                            data.AsMemory(0, item.Data.Length),
                            WebSocketMessageType.Binary,
                            endOfMessage: true,
                            hardStop.Token).ConfigureAwait(false);
                        continue;
                    }

                    var json = JsonSerializer.SerializeToUtf8Bytes(item.Entry, ControllerManagementJson.Options);
                    await socket.SendAsync(
                        json.AsMemory(),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        hardStop.Token).ConfigureAwait(false);
                    if (string.Equals(item.Entry.Kind, "termination", StringComparison.Ordinal))
                    {
                        closeDescription = item.Entry.TerminationReason ?? "ended";
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (closeSource.IsCancellationRequested)
            {
                // The peer closed, the session ended, or the listener stopped.
            }
            catch (WebSocketException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                if (!hardStop.IsCancellationRequested)
                {
                    if (closeSource.IsCancellationRequested)
                    {
                        closeDescription = "The client closed the session.";
                    }

                    using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(hardStop.Token);
                    closeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        if (socket.State == WebSocketState.Open)
                        {
                            await socket.CloseOutputAsync(closeStatus, closeDescription, closeTimeout.Token).ConfigureAwait(false);
                        }
                        else if (socket.State == WebSocketState.CloseReceived)
                        {
                            await socket.CloseAsync(closeStatus, closeDescription, closeTimeout.Token).ConfigureAwait(false);
                        }
                    }
                    catch (WebSocketException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                    catch (OperationCanceledException)
                    {
                        // The peer stopped reading; the close frame could not leave in time.
                    }
                }

                if (hardStop.IsCancellationRequested)
                {
                    watchStop.Cancel();
                }

                try
                {
                    await closeTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The peer never answered the close frame; cancel the abandoned receive.
                }
                catch (WebSocketException)
                {
                }
                catch (IOException)
                {
                }

                watchStop.Cancel();
                closeSource.Cancel();
                try
                {
                    await closeTask.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // The receive observes a torn-down connection; nothing left to recover.
                }
            }
        }
    }

    /// <summary>
    /// Watches for the peer's close frame so a polite disconnect cancels the pump. The receive runs
    /// on <paramref name="watchToken"/> rather than <paramref name="closeSource"/>: canceling a
    /// pending ManagedWebSocket receive aborts the connection, so the watch is only canceled after
    /// the close handshake completed.
    /// </summary>
    private static async Task ReceiveClientCloseAsync(
        WebSocket socket,
        CancellationTokenSource closeSource,
        CancellationToken watchToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(256);
        try
        {
            while (!closeSource.IsCancellationRequested && socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(), watchToken).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    closeSource.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (watchToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
            closeSource.Cancel();
        }
        catch (IOException)
        {
            closeSource.Cancel();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Falls back to the browser WebSocket credential channel when the API key header is absent:
    /// browsers cannot set request headers on an upgrade, so a Web UI client presents the key as
    /// one base64url-encoded <c>Sec-WebSocket-Protocol</c> token. Anything absent or malformed is
    /// simply "no key" and admission answers 401.
    /// </summary>
    private static string? ResolveOutputStreamApiKey(IList<string> requestedProtocols, string? headerKey)
    {
        if (!string.IsNullOrEmpty(headerKey))
        {
            // The header channel wins; the subprotocol fallback only applies when it is absent.
            return headerKey;
        }

        string? key = null;
        foreach (var protocol in requestedProtocols)
        {
            if (!protocol.StartsWith(
                    ControllerManagementApiContract.ServiceOutputKeySubProtocolPrefix,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (key is not null)
            {
                // Two key tokens are the duplicate-credential case, which admission rejects.
                return null;
            }

            key = DecodeBase64UrlKey(
                protocol[ControllerManagementApiContract.ServiceOutputKeySubProtocolPrefix.Length..]);
        }

        return key;
    }

    /// <summary>Decodes one base64url credential token; malformed input means no key.</summary>
    private static string? DecodeBase64UrlKey(string encoded)
    {
        // The key contract bounds credentials to 4096 characters, so 8192 is a generous token cap.
        if (encoded.Length == 0 || encoded.Length > 8192)
        {
            return null;
        }

        var base64 = encoded.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2:
                base64 += "==";
                break;
            case 3:
                base64 += "=";
                break;
            case 0:
                break;
            default:
                return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool TryParseServiceOutputStreamPath(string path, out Guid serviceId)
    {
        serviceId = default;
        const string prefix = ControllerManagementApiContract.ServicesPath + "/";
        const string suffix = "/output/stream";
        if (path.Length <= prefix.Length + suffix.Length ||
            !path.StartsWith(prefix, StringComparison.Ordinal) ||
            !path.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var idText = path.AsSpan(prefix.Length, path.Length - prefix.Length - suffix.Length);
        return Guid.TryParse(idText, out serviceId);
    }

    private static bool TryParseOutputStreamQuery(
        IQueryCollection query,
        out ExtensionServiceOutputStream stream,
        out long? sinceSequence)
    {
        stream = ExtensionServiceOutputStream.Stdout;
        sinceSequence = null;
        var streamValues = query[ControllerManagementApiContract.ServiceOutputStreamQueryParameter];
        if (streamValues.Count > 1)
        {
            return false;
        }

        if (streamValues.Count == 1)
        {
            if (string.Equals(streamValues[0], "stderr", StringComparison.OrdinalIgnoreCase))
            {
                stream = ExtensionServiceOutputStream.Stderr;
            }
            else if (!string.Equals(streamValues[0], "stdout", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var sinceValues = query["since"];
        if (sinceValues.Count > 1)
        {
            return false;
        }

        if (sinceValues.Count == 1)
        {
            if (!long.TryParse(sinceValues[0], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence < 0)
            {
                return false;
            }

            sinceSequence = sequence;
        }

        return true;
    }

    private static async ValueTask WriteWebUiResourceAsync(
        HttpContext context,
        Stream resource,
        string contentType,
        string? cacheControl,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (resource.ConfigureAwait(false))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = contentType;
                context.Response.ContentLength = resource.Length;
                if (cacheControl is not null)
                {
                    context.Response.Headers.CacheControl = cacheControl;
                }

                await resource.CopyToAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async ValueTask<int> ReadBodyAsync(
        HttpRequest request,
        byte[] destination,
        CancellationToken cancellationToken)
    {
        var total = 0;
        var boundedLength = ControllerAdmissionLimits.MaximumRequestBodyBytes + 1;
        while (total < boundedLength)
        {
            var read = await request.Body.ReadAsync(
                destination.AsMemory(total, boundedLength - total),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            total += read;
        }

        // Reading exactly MaximumRequestBodyBytes + 1 proves that the body exceeded the
        // admission limit without allocating based on an untrusted Content-Length.
        return -1;
    }

    private static bool TryCopyHeaders(
        IHeaderDictionary source,
        out List<KeyValuePair<string, IEnumerable<string>>> headers,
        out string? apiKey)
    {
        headers = new List<KeyValuePair<string, IEnumerable<string>>>();
        apiKey = null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aggregateLength = 0L;

        foreach (var pair in source)
        {
            if (headers.Count >= ControllerAdmissionLimits.MaximumHeaderCount ||
                string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Key.Length > ControllerAdmissionLimits.MaximumHeaderNameLength ||
                !names.Add(pair.Key))
            {
                headers.Clear();
                return false;
            }

            aggregateLength += System.Text.Encoding.UTF8.GetByteCount(pair.Key);
            if (aggregateLength > ControllerAdmissionLimits.MaximumAggregateHeaderBytes)
            {
                headers.Clear();
                return false;
            }

            var values = pair.Value;
            if (values.Count > ControllerAdmissionLimits.MaximumHeaderValues)
            {
                headers.Clear();
                return false;
            }

            var copiedValues = new string[values.Count];
            for (var index = 0; index < copiedValues.Length; index++)
            {
                var value = values[index];
                if (value is null || value.Length > ControllerAdmissionLimits.MaximumHeaderValueLength)
                {
                    headers.Clear();
                    return false;
                }

                aggregateLength += System.Text.Encoding.UTF8.GetByteCount(value);
                if (aggregateLength > ControllerAdmissionLimits.MaximumAggregateHeaderBytes)
                {
                    headers.Clear();
                    return false;
                }

                copiedValues[index] = value;
            }

            if (string.Equals(pair.Key, ApiKeyHeaderName, StringComparison.OrdinalIgnoreCase))
            {
                // A repeated key is deliberately represented as absent. The shared dispatcher
                // then returns its ordinary unauthorized result without revealing key details.
                apiKey = copiedValues.Length == 1 ? copiedValues[0] : null;
            }

            headers.Add(new KeyValuePair<string, IEnumerable<string>>(pair.Key, copiedValues));
        }

        return true;
    }
    private static async ValueTask WriteResponseAsync(
        HttpContext context,
        ControllerManagementResponse response,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var hopByHopHeaders = new HashSet<string>(HopByHopHeaders, StringComparer.OrdinalIgnoreCase);
        if (response.Headers.TryGetValue("connection", out var connectionValues))
        {
            foreach (var connectionValue in connectionValues)
            {
                foreach (var token in connectionValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    hopByHopHeaders.Add(token);
                }
            }
        }

        context.Response.StatusCode = response.StatusCode;
        foreach (var header in response.Headers)
        {
            if (hopByHopHeaders.Contains(header.Key) ||
                string.Equals(header.Key, "content-length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                try
                {
                    context.Response.Headers.Append(header.Key, value);
                }
                catch (InvalidOperationException)
                {
                    // A transport-invalid response header must not prevent the canonical status
                    // from reaching the caller.
                }
                catch (FormatException)
                {
                    // Header values supplied by a dispatcher are bounded but still protocol data.
                }
                catch (ArgumentException)
                {
                    // Kestrel rejects malformed protocol header names or values.
                }
            }
        }

        context.Response.ContentLength = response.Body.Length;
        var responseBody = ImmutableCollectionsMarshal.AsArray(response.Body);
        if (responseBody is not null)
        {
            await context.Response.Body.WriteAsync(responseBody.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void EnsureSocketPathIsUnused(string path)
    {
        EnsureSocketParentChainIsTrusted(path);

        try
        {
            var entry = new FileInfo(path);
            if (entry.LinkTarget is not null || entry.Exists || Directory.Exists(path))
            {
                throw new InvalidOperationException("The configured Unix socket path is occupied.");
            }

            _ = File.GetAttributes(path);
            throw new InvalidOperationException("The configured Unix socket path is occupied.");
        }
        catch (FileNotFoundException)
        {
            // The final component is absent; Kestrel may bind it.
        }
        catch (DirectoryNotFoundException)
        {
            // A parent can disappear after validation; Kestrel will fail without deleting anything.
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The configured Unix socket path cannot be inspected.");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("The configured Unix socket path cannot be inspected.");
        }
    }

    private enum SocketParentChainState
    {
        Trusted,
        Missing,
        Unsafe
    }

    private const UnixFileMode DisallowedSocketParentWriteModes =
        UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
    private static bool IsDisallowedSocketParentPath(string path) =>
        string.Equals(path, "/tmp", StringComparison.Ordinal) ||
        string.Equals(path, "/private/tmp", StringComparison.Ordinal);

    private static void EnsureSocketParentChainIsTrusted(string path)
    {
        if (InspectSocketParentChain(path) != SocketParentChainState.Trusted)
        {
            throw new InvalidOperationException("The configured Unix socket parent path is unsafe or unavailable.");
        }
    }

    private static SocketParentChainState InspectSocketParentChain(string path)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(path) || path[0] != Path.DirectorySeparatorChar)
        {
            return SocketParentChainState.Unsafe;
        }

        string? root;
        try
        {
            root = Path.GetPathRoot(path);
        }
        catch (ArgumentException)
        {
            return SocketParentChainState.Unsafe;
        }
        catch (NotSupportedException)
        {
            return SocketParentChainState.Unsafe;
        }

        if (root is null || !string.Equals(root, Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            return SocketParentChainState.Unsafe;
        }

        var state = InspectSocketParentDirectory(root);
        // Walk the raw components so lexical normalization cannot erase a symlink before it is checked.
        // A component is inspected before it can be traversed; dot segments only refer to directories
        // already inspected on the path to root.
        if (state != SocketParentChainState.Trusted)
        {
            return state;
        }

        var finalSeparator = path.LastIndexOf(Path.DirectorySeparatorChar);
        if (finalSeparator <= 0)
        {
            return SocketParentChainState.Trusted;
        }

        var parentPath = path[..finalSeparator];
        var currentPath = root;
        var componentStart = root.Length;
        while (componentStart < parentPath.Length)
        {
            var separator = parentPath.IndexOf(Path.DirectorySeparatorChar, componentStart);
            if (separator < 0)
            {
                separator = parentPath.Length;
            }

            var component = parentPath[componentStart..separator];
            componentStart = separator + 1;
            if (component.Length == 0 || string.Equals(component, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(component, "..", StringComparison.Ordinal))
            {
                if (currentPath.Length > root.Length)
                {
                    var parentSeparator = currentPath.LastIndexOf(Path.DirectorySeparatorChar);
                    currentPath = parentSeparator <= 0
                        ? root
                        : currentPath[..parentSeparator];
                }

                continue;
            }

            currentPath = currentPath == root
                ? root + component
                : currentPath + Path.DirectorySeparatorChar + component;
            if (IsDisallowedSocketParentPath(currentPath))
            {
                return SocketParentChainState.Unsafe;
            }
            state = InspectSocketParentDirectory(currentPath);
            if (state != SocketParentChainState.Trusted)
            {
                return state;
            }
        }

        return SocketParentChainState.Trusted;
    }

    private static SocketParentChainState InspectSocketParentDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return SocketParentChainState.Unsafe;
        }

        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0 ||
                new DirectoryInfo(path).LinkTarget is not null)
            {
                return SocketParentChainState.Unsafe;
            }

            var mode = File.GetUnixFileMode(path);
            return (mode & DisallowedSocketParentWriteModes) == 0
                ? SocketParentChainState.Trusted
                : SocketParentChainState.Unsafe;
        }
        catch (FileNotFoundException)
        {
            return SocketParentChainState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return SocketParentChainState.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return SocketParentChainState.Unsafe;
        }
        catch (IOException)
        {
            return SocketParentChainState.Unsafe;
        }
        catch (ArgumentException)
        {
            return SocketParentChainState.Unsafe;
        }
        catch (NotSupportedException)
        {
            return SocketParentChainState.Unsafe;
        }
        catch (System.Security.SecurityException)
        {
            return SocketParentChainState.Unsafe;
        }
    }

    private static void EnsureSocketWasCreated(string path, bool wasAbsent)
    {
        var entry = new FileInfo(path);
        if (!wasAbsent || !entry.Exists || entry.LinkTarget is not null || Directory.Exists(path))
        {
            throw new InvalidOperationException("The Unix socket was not created at the configured path.");
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The configured Unix socket path is unsafe.");
        }
    }

    private static void SetAndVerifySocketMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Unix socket mode enforcement is unavailable on this platform.");
        }

        var expected = (UnixFileMode)ControllerOptions.RequiredUnixSocketMode;
        EnsureSocketWasCreated(path, wasAbsent: true);
        EnsureSocketParentChainIsTrusted(path);
        File.SetUnixFileMode(path, expected);
        if (File.GetUnixFileMode(path) != expected)
        {
            throw new InvalidOperationException("The Unix socket mode could not be verified.");
        }
    }

    private bool DeleteOwnedSocket()
    {
        var path = _ownedSocketPath;
        if (path is null)
        {
            return true;
        }

        var initialParentState = InspectSocketParentChain(path);
        if (initialParentState == SocketParentChainState.Missing)
        {
            return true;
        }

        if (initialParentState != SocketParentChainState.Trusted)
        {
            return false;
        }

        try
        {
            var entry = new FileInfo(path);
            if (entry.LinkTarget is not null)
            {
                return false;
            }

            if (!entry.Exists)
            {
                return true;
            }

            if (Directory.Exists(path))
            {
                return false;
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                OperatingSystem.IsWindows() ||
                File.GetUnixFileMode(path) != (UnixFileMode)ControllerOptions.RequiredUnixSocketMode)
            {
                return false;
            }

            // The ownership marker is written only after Kestrel bound this previously absent
            // path. Never follow a replacement symlink or remove an occupied path preflight.
            var finalParentState = InspectSocketParentChain(path);
            if (finalParentState == SocketParentChainState.Missing)
            {
                return true;
            }

            if (finalParentState != SocketParentChainState.Trusted)
            {
                return false;
            }

            File.Delete(path);
            var remaining = new FileInfo(path);
            return !remaining.Exists && remaining.LinkTarget is null;
        }
        catch (FileNotFoundException)
        {
            // It was already removed by the operating system or an administrator.
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            // Its parent was removed; there is no path to clean.
            return true;
        }
        catch (IOException)
        {
            // Never unlink an entry that cannot be proven to remain the adapter's plain socket path.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Refuse to remove an entry whose ownership/path safety cannot be established.
            return false;
        }
    }
}
