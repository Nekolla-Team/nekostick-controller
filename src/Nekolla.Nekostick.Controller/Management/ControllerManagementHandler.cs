using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Provides the Host route handler over the shared dispatcher.</summary>
public sealed class ControllerManagementHandler : IExtensionHandler
{
    private readonly IControllerManagementDispatcher _dispatcher;
    /// <summary>Initializes a management handler over the supplied dispatcher.</summary>
    /// <param name="dispatcher">Dispatcher that processes admitted management requests.</param>
    /// <param name="options">Controller options used by the handler.</param>
    public ControllerManagementHandler(IControllerManagementDispatcher dispatcher, ControllerOptions options) { _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)); ArgumentNullException.ThrowIfNull(options); }
    /// <summary>Stable identifier advertised by this handler.</summary>
    public string HandlerId => ControllerManagementApiContract.HandlerId;
    /// <summary>Handles one extension-handler request.</summary>
    /// <param name="request">Incoming extension-handler request.</param>
    /// <param name="cancellationToken">Token used to cancel request dispatch.</param>
    /// <returns>The extension-handler response produced by the management dispatcher.</returns>
    public async ValueTask<ExtensionHandlerResponse> HandleAsync(ExtensionHandlerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var presentedKey = ReadApiKey(request.Headers);
        if (!ControllerAdmissionLimits.TryCreateRequest(ControllerTransport.HostRoute, request.Method, request.Path, presentedKey,
            request.Headers.Select(static pair => new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value)), ControllerManagementJson.AsReadOnlyMemory(request.Body), out var managementRequest) || managementRequest is null)
            return ToExtensionResponse(ControllerManagementResponseBuilder.InvalidRequest);
        var response = await _dispatcher.DispatchAsync(managementRequest, cancellationToken).ConfigureAwait(false);
        return ToExtensionResponse(response);
    }
    private static string? ReadApiKey(IReadOnlyDictionary<string, ImmutableArray<string>> headers) => headers.TryGetValue(ControllerManagementApiContract.ApiKeyHeaderName, out var values) && values.Length == 1 && !string.IsNullOrEmpty(values[0]) ? values[0] : null;
    private static ExtensionHandlerResponse ToExtensionResponse(ControllerManagementResponse response) => new(response.StatusCode, response.Headers.Select(static pair => new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value)), ControllerManagementJson.AsReadOnlyMemory(response.Body));
}

/// <summary>Creates management handlers for controller transports.</summary>
public sealed class ControllerManagementHandlerFactory : IControllerManagementHandlerFactory
{
    /// <summary>Creates a management handler using the supplied dispatcher and options.</summary>
    /// <param name="dispatcher">Dispatcher that processes management requests.</param>
    /// <param name="options">Controller options used by the handler.</param>
    /// <returns>A configured management handler.</returns>
    public IExtensionHandler Create(IControllerManagementDispatcher dispatcher, ControllerOptions options) => new ControllerManagementHandler(dispatcher, options);
}
