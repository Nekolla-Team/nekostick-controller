using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Serves the HostRoute management API with bounded streaming request and response bodies.</summary>
internal sealed class ControllerStreamingManagementHandler : IExtensionStreamingHandler
{
    private static readonly string[] HtmlContentType = new[] { "text/html" };
    private static readonly string[] EventStreamContentType = new[] { ControllerManagementApiContract.ServiceOutputEventStreamMediaType };
    private static readonly string[] NoStore = new[] { "no-store" };
    private static readonly string[] ZeroContentLength = new[] { "0" };
    private readonly IControllerManagementDispatcher _dispatcher;
    private readonly ControllerOptions _startupOptions;
    private readonly ControllerManagementHandler _bufferedHandler;

    /// <summary>Initializes a streaming handler over the shared dispatcher.</summary>
    /// <param name="dispatcher">Dispatcher that processes admitted management requests.</param>
    /// <param name="options">Controller options used as the startup fallback.</param>
    internal ControllerStreamingManagementHandler(
        IControllerManagementDispatcher dispatcher,
        ControllerOptions options)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _startupOptions = options ?? throw new ArgumentNullException(nameof(options));
        _bufferedHandler = new ControllerManagementHandler(_dispatcher, _startupOptions);
    }

    /// <summary>Gets the stable handler identifier shared with the buffered HostRoute handler.</summary>
    public string HandlerId => ControllerManagementApiContract.HandlerId;

    /// <summary>Handles one HostRoute request with a bounded request stream.</summary>
    /// <param name="request">Host-owned streaming request.</param>
    /// <param name="cancellationToken">Token used to cancel request dispatch.</param>
    /// <returns>A response whose body stream ownership transfers to the Host.</returns>
    public async ValueTask<ExtensionStreamingResponse> HandleStreamingAsync(
        ExtensionStreamingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currentOptions = ControllerManagementDispatcherOptions.GetCurrent(_dispatcher, _startupOptions);
        if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
            currentOptions.EnableWebUi &&
            currentOptions.HostRoutePath is { } hostRoutePath)
        {
            if (ControllerWebUiResource.IsHostRouteRootPath(request.Path, hostRoutePath))
            {
                // The shell addresses its assets relatively, so the document URL must end in a
                // slash for them to resolve under the HostRoute prefix.
                if (!request.Path.EndsWith('/'))
                {
                    return CreateRedirectResponse(request.Path + "/");
                }

                var resource = ControllerWebUiResource.OpenRead();
                if (resource is not null)
                {
                    return CreateWebUiResponse(resource);
                }
            }
            else if (ControllerWebUiResource.TryGetAssetFileName(request.Path, hostRoutePath, out var assetName) &&
                ControllerWebUiResource.OpenAsset(assetName) is { } asset)
            {
                return CreateAssetResponse(asset, assetName);
            }
        }

        if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase) &&
            currentOptions.HostRoutePath is { } installPrefix &&
            string.Equals(request.Path, installPrefix + ControllerManagementApiContract.ExtensionsInstallPath, StringComparison.Ordinal))
        {
            return await HandleInstallAsync(request, currentOptions, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
            currentOptions.HostRoutePath is { } streamPrefix &&
            IsServiceOutputStreamPath(request.Path, streamPrefix, out var logicalPath))
        {
            // The output-stream endpoint is long-lived; it never reaches the buffered fallback.
            return await HandleServiceOutputStreamAsync(request, currentOptions, logicalPath, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
            currentOptions.HostRoutePath is { } feedPrefix &&
            string.Equals(request.Path, feedPrefix + ControllerManagementApiContract.ServiceRuntimeFeedPath, StringComparison.Ordinal))
        {
            // The runtime-state feed is long-lived; it never reaches the buffered fallback.
            return await HandleServiceRuntimeFeedAsync(request, currentOptions, cancellationToken).ConfigureAwait(false);
        }

        var rentedBody = ArrayPool<byte>.Shared.Rent(ControllerAdmissionLimits.MaximumRequestBodyBytes + 1);
        try
        {
            var bodyLength = await ReadBodyAsync(request.BodyStream, rentedBody, cancellationToken).ConfigureAwait(false);
            if (bodyLength < 0)
            {
                return ToStreamingResponse(ControllerManagementResponse.InvalidRequest);
            }

            var bufferedRequest = new ExtensionHandlerRequest(
                request.Method,
                request.Path,
                request.Headers.Select(static pair => new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value)),
                rentedBody.AsMemory(0, bodyLength),
                request.IsHttps);

            var response = await _bufferedHandler.HandleAsync(bufferedRequest, cancellationToken).ConfigureAwait(false);
            return ToStreamingResponse(response);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBody, clearArray: true);
        }
    }

    private async ValueTask<ExtensionStreamingResponse> HandleInstallAsync(
        ExtensionStreamingRequest request,
        ControllerOptions currentOptions,
        CancellationToken cancellationToken)
    {
        var headerDictionary = request.Headers
            .GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.SelectMany(static pair => pair.Value).ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase);
        var corsOrigin = ControllerManagementHandler.MatchAllowedOrigin(currentOptions, headerDictionary);
        var presentedKey = ControllerManagementHandler.ReadApiKey(headerDictionary);
        if (!ControllerAdmissionLimits.TryCreateRequest(
                ControllerTransport.HostRoute,
                request.Method,
                request.Path,
                presentedKey,
                request.Headers.Select(static pair => new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value)),
                ReadOnlyMemory<byte>.Empty,
                out var installRequest) ||
            installRequest is null)
        {
            return ToStreamingResponse(ControllerManagementResponse.InvalidRequest, corsOrigin);
        }

        ControllerManagementResponse response;
        try
        {
            response = await _dispatcher.DispatchStreamingAsync(installRequest, request.BodyStream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            response = ControllerManagementResponse.Unavailable;
        }

        return ToStreamingResponse(response, corsOrigin);
    }

    /// <summary>
    /// Serves the service-output stream endpoint as Server-Sent Events. HostRoute cannot express a
    /// WebSocket upgrade, so this transport always answers with an SSE stream.
    /// </summary>
    private async ValueTask<ExtensionStreamingResponse> HandleServiceOutputStreamAsync(
        ExtensionStreamingRequest request,
        ControllerOptions currentOptions,
        string logicalPath,
        CancellationToken cancellationToken)
    {
        var headerDictionary = request.Headers
            .GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.SelectMany(static pair => pair.Value).ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase);
        var corsOrigin = ControllerManagementHandler.MatchAllowedOrigin(currentOptions, headerDictionary);

        if (!TryParseServiceOutputStreamRequest(request.Path, logicalPath, out var serviceId, out var outputStream, out var sinceSequence))
        {
            return ToStreamingResponse(ControllerManagementResponse.InvalidRequest, corsOrigin);
        }

        if (sinceSequence is null &&
            headerDictionary.TryGetValue("Last-Event-ID", out var lastEventIds) &&
            lastEventIds.Length == 1 &&
            long.TryParse(lastEventIds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var headerSequence) &&
            headerSequence >= 0)
        {
            sinceSequence = headerSequence;
        }

        var presentedKey = ControllerManagementHandler.ReadApiKey(headerDictionary);
        if (!ControllerAdmissionLimits.TryCreateRequest(
                ControllerTransport.HostRoute,
                request.Method,
                logicalPath,
                presentedKey,
                request.Headers.Select(static pair => new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value)),
                ReadOnlyMemory<byte>.Empty,
                out var admittedRequest) ||
            admittedRequest is null)
        {
            return ToStreamingResponse(ControllerManagementResponse.InvalidRequest, corsOrigin);
        }

        ControllerServiceLogFeedResult logResult;
        try
        {
            logResult = await _dispatcher.DispatchServiceLogFeedAsync(admittedRequest, serviceId, sinceSequence, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
                return ToStreamingResponse(logResult.Rejection ?? ControllerManagementResponse.Unavailable, corsOrigin);
            }

            ServiceLogEventStream? eventStream = null;
            try
            {
                eventStream = ServiceLogEventStream.Start(feed, logResult.SessionEnded);
                var headers = new List<KeyValuePair<string, IEnumerable<string>>>(4)
                {
                    new("content-type", EventStreamContentType),
                    new("cache-control", NoStore)
                };
                if (corsOrigin is not null)
                {
                    ControllerManagementHandler.AppendCorsHeaders(headers, corsOrigin);
                }

                return new ExtensionStreamingResponse(StatusCodes.Status200OK, headers, eventStream);
            }
            catch
            {
                if (eventStream is null)
                {
                    await feed.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    await eventStream.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
        }

        ControllerServiceOutputStreamResult result;
        try
        {
            result = await _dispatcher.DispatchServiceOutputStreamAsync(admittedRequest, serviceId, outputStream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            result = ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponse.Unavailable);
        }

        if (result.Stream is not { } output)
        {
            return ToStreamingResponse(result.Rejection ?? ControllerManagementResponse.Unavailable, corsOrigin);
        }

        try
        {
            var eventStream = ServiceOutputEventStream.Start(output, result.SessionEnded);
            var headers = new List<KeyValuePair<string, IEnumerable<string>>>(4)
            {
                new("content-type", EventStreamContentType),
                new("cache-control", NoStore)
            };
            if (corsOrigin is not null)
            {
                ControllerManagementHandler.AppendCorsHeaders(headers, corsOrigin);
            }

            return new ExtensionStreamingResponse(StatusCodes.Status200OK, headers, eventStream);
        }
        catch
        {
            await output.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Serves the node-local runtime-state feed as Server-Sent Events over HostRoute. The host
    /// copies the returned stream to the client; disposing it detaches the feed view.
    /// </summary>
    private async ValueTask<ExtensionStreamingResponse> HandleServiceRuntimeFeedAsync(
        ExtensionStreamingRequest request,
        ControllerOptions currentOptions,
        CancellationToken cancellationToken)
    {
        var headerDictionary = request.Headers
            .GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.SelectMany(static pair => pair.Value).ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase);
        var corsOrigin = ControllerManagementHandler.MatchAllowedOrigin(currentOptions, headerDictionary);

        var presentedKey = ControllerManagementHandler.ReadApiKey(headerDictionary);
        if (!ControllerAdmissionLimits.TryCreateRequest(
                ControllerTransport.HostRoute,
                "GET",
                ControllerManagementApiContract.ServiceRuntimeFeedPath,
                presentedKey,
                request.Headers.Select(static pair => new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value)),
                ReadOnlyMemory<byte>.Empty,
                out var admittedRequest) ||
            admittedRequest is null)
        {
            return ToStreamingResponse(ControllerManagementResponse.InvalidRequest, corsOrigin);
        }

        ControllerRuntimeFeedResult result;
        try
        {
            result = _dispatcher.DispatchServiceRuntimeFeed(admittedRequest, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            result = ControllerRuntimeFeedResult.Rejected(ControllerManagementResponse.Unavailable);
        }

        if (result.View is not { } view)
        {
            return ToStreamingResponse(result.Rejection ?? ControllerManagementResponse.Unavailable, corsOrigin);
        }

        try
        {
            var eventStream = ControllerRuntimeFeedEventStream.Start(view, result.SessionEnded);
            var headers = new List<KeyValuePair<string, IEnumerable<string>>>(4)
            {
                new("content-type", EventStreamContentType),
                new("cache-control", NoStore)
            };
            if (corsOrigin is not null)
            {
                ControllerManagementHandler.AppendCorsHeaders(headers, corsOrigin);
            }

            return new ExtensionStreamingResponse(StatusCodes.Status200OK, headers, eventStream);
        }
        catch
        {
            view.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Determines whether one HostRoute path (query included) structurally targets the
    /// service-output stream endpoint, and yields the prefix-free logical path.
    /// </summary>
    private static bool IsServiceOutputStreamPath(string requestPath, string hostRoutePrefix, out string logicalPath)
    {
        logicalPath = string.Empty;
        if (!requestPath.StartsWith(hostRoutePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = requestPath[hostRoutePrefix.Length..];
        var queryIndex = remainder.IndexOf('?');
        var pathOnly = queryIndex < 0 ? remainder : remainder[..queryIndex];
        if (!pathOnly.StartsWith(ControllerManagementApiContract.ServicesPath + "/", StringComparison.Ordinal) ||
            !pathOnly.EndsWith(ControllerManagementApiContract.ServiceOutputStreamSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        logicalPath = pathOnly;
        return true;
    }

    /// <summary>Parses the service identifier, stream selection, and resume cursor from one output-stream path.</summary>
    private static bool TryParseServiceOutputStreamRequest(
        string requestPath,
        string logicalPath,
        out Guid serviceId,
        out ExtensionServiceOutputStream outputStream,
        out long? sinceSequence)
    {
        serviceId = Guid.Empty;
        outputStream = ExtensionServiceOutputStream.Stdout;
        sinceSequence = null;

        var servicesPrefix = ControllerManagementApiContract.ServicesPath + "/";
        var idText = logicalPath[servicesPrefix.Length..^ControllerManagementApiContract.ServiceOutputStreamSuffix.Length];
        if (!Guid.TryParseExact(idText, "D", out serviceId))
        {
            return false;
        }

        var queryIndex = requestPath.IndexOf('?');
        if (queryIndex < 0)
        {
            return true;
        }

        var seenStream = false;
        var seenSince = false;
        foreach (var pair in requestPath[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            if (string.Equals(name, "stream", StringComparison.OrdinalIgnoreCase))
            {
                if (seenStream || separator < 0)
                {
                    return false;
                }

                seenStream = true;
                var value = pair[(separator + 1)..];
                if (string.Equals(value, "stdout", StringComparison.OrdinalIgnoreCase))
                {
                    outputStream = ExtensionServiceOutputStream.Stdout;
                }
                else if (string.Equals(value, "stderr", StringComparison.OrdinalIgnoreCase))
                {
                    outputStream = ExtensionServiceOutputStream.Stderr;
                }
                else
                {
                    return false;
                }

                continue;
            }

            if (!string.Equals(name, "since", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seenSince || separator < 0)
            {
                return false;
            }

            seenSince = true;
            if (!long.TryParse(pair[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence < 0)
            {
                return false;
            }

            sinceSequence = sequence;
        }

        return true;
    }

    private static async ValueTask<int> ReadBodyAsync(
        Stream bodyStream,
        byte[] destination,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = await bodyStream.ReadAsync(
                destination.AsMemory(total, destination.Length - total),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            total += read;
        }

        return -1;
    }

    private static ExtensionStreamingResponse CreateWebUiResponse(Stream resource) =>
        CreateResourceResponse(resource, HtmlContentType, cacheControl: null);

    private static ExtensionStreamingResponse CreateAssetResponse(Stream resource, string fileName) =>
        CreateResourceResponse(
            resource,
            new[] { ControllerWebUiResource.ContentTypeFor(fileName) },
            ControllerWebUiResource.AssetCacheControl);

    private static ExtensionStreamingResponse CreateResourceResponse(
        Stream resource,
        string[] contentType,
        string? cacheControl)
    {
        try
        {
            var headers = new List<KeyValuePair<string, IEnumerable<string>>>(3)
            {
                new("content-type", contentType),
                new("content-length", new[] { resource.Length.ToString(CultureInfo.InvariantCulture) })
            };
            if (cacheControl is not null)
            {
                headers.Add(new KeyValuePair<string, IEnumerable<string>>("cache-control", new[] { cacheControl }));
            }

            return new ExtensionStreamingResponse(StatusCodes.Status200OK, headers, resource);
        }
        catch
        {
            resource.Dispose();
            throw;
        }
    }

    private static ExtensionStreamingResponse CreateRedirectResponse(string location)
    {
        var headers = new[]
        {
            new KeyValuePair<string, IEnumerable<string>>("location", new[] { location }),
            new KeyValuePair<string, IEnumerable<string>>("content-length", ZeroContentLength)
        };
        return new ExtensionStreamingResponse(StatusCodes.Status302Found, headers, Stream.Null);
    }

    /// <summary>Converts a management response for the install branch, appending CORS headers.</summary>
    private static ExtensionStreamingResponse ToStreamingResponse(ControllerManagementResponse response, string? corsOrigin)
    {
        if (corsOrigin is null)
        {
            return ToStreamingResponse(response);
        }

        var headers = response.Headers.Select(static pair => new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value)).ToList();
        ControllerManagementHandler.AppendCorsHeaders(headers, corsOrigin);
        var body = new MemoryStream(response.Body.ToArray(), writable: false);
        return new ExtensionStreamingResponse(response.StatusCode, headers, body);
    }

    private static ExtensionStreamingResponse ToStreamingResponse(ExtensionHandlerResponse response)
    {
        var headers = response.Headers.Select(static pair =>
            new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value));
        var body = new MemoryStream(response.Body.ToArray(), writable: false);
        return new ExtensionStreamingResponse(response.StatusCode, headers, body);
    }
    private static ExtensionStreamingResponse ToStreamingResponse(ControllerManagementResponse response)
    {
        var headers = response.Headers.Select(static pair =>
            new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value));
        var body = new MemoryStream(response.Body.ToArray(), writable: false);
        return new ExtensionStreamingResponse(response.StatusCode, headers, body);
    }
}
