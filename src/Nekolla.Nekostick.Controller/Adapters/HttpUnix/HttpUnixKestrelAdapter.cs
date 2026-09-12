using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Nekolla.Nekostick.Controller.Management;

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
