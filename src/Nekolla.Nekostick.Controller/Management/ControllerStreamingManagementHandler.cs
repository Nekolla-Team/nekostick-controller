using System.Buffers;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Serves the HostRoute management API with bounded streaming request and response bodies.</summary>
internal sealed class ControllerStreamingManagementHandler : IExtensionStreamingHandler
{
    private static readonly string[] HtmlContentType = new[] { "text/html" };
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
            currentOptions.HostRoutePath is { } hostRoutePath &&
            ControllerWebUiResource.IsHostRouteRootPath(request.Path, hostRoutePath))
        {
            var resource = ControllerWebUiResource.OpenRead();
            if (resource is not null)
            {
                return CreateWebUiResponse(resource);
            }
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

    private static ExtensionStreamingResponse CreateWebUiResponse(Stream resource)
    {
        try
        {
            var headers = new[]
            {
                new KeyValuePair<string, IEnumerable<string>>("content-type", HtmlContentType),
                new KeyValuePair<string, IEnumerable<string>>("content-length", new[] { resource.Length.ToString(CultureInfo.InvariantCulture) })
            };
            return new ExtensionStreamingResponse(StatusCodes.Status200OK, headers, resource);
        }
        catch
        {
            resource.Dispose();
            throw;
        }
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
