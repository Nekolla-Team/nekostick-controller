using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Provides the Host route handler over the shared dispatcher.</summary>
public sealed class ControllerManagementHandler : IExtensionHandler
{
    private const string OriginHeaderName = "Origin";
    private const string CorsRequestMethodHeaderName = "Access-Control-Request-Method";
    private const string CorsAllowOriginHeaderName = "Access-Control-Allow-Origin";
    private const string CorsAllowMethodsHeaderName = "Access-Control-Allow-Methods";
    private const string CorsAllowHeadersHeaderName = "Access-Control-Allow-Headers";
    private const string CorsExposeHeadersHeaderName = "Access-Control-Expose-Headers";
    private const string VaryHeaderName = "Vary";
    private const string CorsAllowedMethods = "GET, POST, PUT, DELETE, PATCH";
    private static readonly string CorsAllowedHeaders =
        "content-type, " +
        ControllerManagementApiContract.IfMatchHeaderName +
        ", " +
        ControllerManagementApiContract.ApiKeyHeaderName;
    private static readonly string CorsExposedHeaders =
        ControllerManagementApiContract.ETagHeaderName +
        ", " +
        ControllerManagementApiContract.LocationHeaderName;

    private readonly IControllerManagementDispatcher _dispatcher;
    private readonly ControllerOptions _options;

    /// <summary>Initializes a management handler over the supplied dispatcher.</summary>
    /// <param name="dispatcher">Dispatcher that processes admitted management requests.</param>
    /// <param name="options">Controller options used by the handler.</param>
    public ControllerManagementHandler(IControllerManagementDispatcher dispatcher, ControllerOptions options) { _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)); _options = options ?? throw new ArgumentNullException(nameof(options)); }
    /// <summary>Stable identifier advertised by this handler.</summary>
    public string HandlerId => ControllerManagementApiContract.HandlerId;
    /// <summary>Handles one extension-handler request.</summary>
    /// <param name="request">Incoming extension-handler request.</param>
    /// <param name="cancellationToken">Token used to cancel request dispatch.</param>
    /// <returns>The extension-handler response produced by the management dispatcher.</returns>
    public async ValueTask<ExtensionHandlerResponse> HandleAsync(ExtensionHandlerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Browser preflight is answered before admission so cross-origin probes never need an
        // API key; every later response carries the CORS headers so browsers can read errors too.
        var corsOrigin = MatchAllowedOrigin(_options, request.Headers);
        if (corsOrigin is not null &&
            string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase) &&
            request.Headers.ContainsKey(CorsRequestMethodHeaderName))
        {
            return CorsPreflightResponse(corsOrigin);
        }

        var presentedKey = ReadApiKey(request.Headers);
        if (!ControllerAdmissionLimits.TryCreateRequest(ControllerTransport.HostRoute, request.Method, request.Path, presentedKey,
            request.Headers.Select(static pair => new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value)), ControllerManagementJson.AsReadOnlyMemory(request.Body), out var managementRequest) || managementRequest is null)
            return ToExtensionResponse(ControllerManagementResponseBuilder.InvalidRequest, corsOrigin);
        var response = await _dispatcher.DispatchAsync(managementRequest, cancellationToken).ConfigureAwait(false);
        return ToExtensionResponse(response, corsOrigin);
    }

    /// <summary>Returns the Origin header value when it is allowed, or the wildcard marker.</summary>
    internal static string? MatchAllowedOrigin(ControllerOptions options, IReadOnlyDictionary<string, ImmutableArray<string>> headers)
    {
        if (options.CorsAllowedOrigins.IsEmpty ||
            !headers.TryGetValue(OriginHeaderName, out var origins) ||
            origins.Length != 1)
        {
            return null;
        }

        if (options.CorsAllowedOrigins.Contains("*", StringComparer.Ordinal))
        {
            return "*";
        }

        var origin = origins[0];
        return options.CorsAllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase) ? origin : null;
    }

    /// <summary>Appends the CORS actual-response headers for an allowed origin.</summary>
    internal static void AppendCorsHeaders(List<KeyValuePair<string, IEnumerable<string>>> headers, string corsOrigin)
    {
        headers.Add(new KeyValuePair<string, IEnumerable<string>>(CorsAllowOriginHeaderName, new[] { corsOrigin }));
        headers.Add(new KeyValuePair<string, IEnumerable<string>>(CorsExposeHeadersHeaderName, new[] { CorsExposedHeaders }));
        if (!string.Equals(corsOrigin, "*", StringComparison.Ordinal))
        {
            headers.Add(new KeyValuePair<string, IEnumerable<string>>(VaryHeaderName, new[] { OriginHeaderName }));
        }
    }

    private static ExtensionHandlerResponse CorsPreflightResponse(string allowedOrigin)
    {
        var headers = new List<KeyValuePair<string, IEnumerable<string>>>
        {
            new(CorsAllowOriginHeaderName, new[] { allowedOrigin }),
            new(CorsAllowMethodsHeaderName, new[] { CorsAllowedMethods }),
            new(CorsAllowHeadersHeaderName, new[] { CorsAllowedHeaders })
        };
        if (!string.Equals(allowedOrigin, "*", StringComparison.Ordinal))
        {
            headers.Add(new KeyValuePair<string, IEnumerable<string>>(VaryHeaderName, new[] { OriginHeaderName }));
        }

        return new ExtensionHandlerResponse(204, headers, ReadOnlyMemory<byte>.Empty);
    }
    /// <summary>Reads the single presented API key value, when exactly one non-empty value exists.</summary>
    internal static string? ReadApiKey(IReadOnlyDictionary<string, ImmutableArray<string>> headers) => headers.TryGetValue(ControllerManagementApiContract.ApiKeyHeaderName, out var values) && values.Length == 1 && !string.IsNullOrEmpty(values[0]) ? values[0] : null;

    private static ExtensionHandlerResponse ToExtensionResponse(ControllerManagementResponse response, string? corsOrigin)
    {
        var headers = response.Headers.Select(static pair => new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value)).ToList();
        if (corsOrigin is not null)
        {
            AppendCorsHeaders(headers, corsOrigin);
        }

        return new ExtensionHandlerResponse(response.StatusCode, headers, ControllerManagementJson.AsReadOnlyMemory(response.Body));
    }
}

/// <summary>Creates management handlers for controller transports.</summary>
public sealed class ControllerManagementHandlerFactory : IControllerManagementHandlerFactory
{
    /// <summary>Creates a management handler using the supplied dispatcher and options.</summary>
    /// <param name="dispatcher">Dispatcher that processes management requests.</param>
    /// <param name="options">Controller options used by the handler.</param>
    /// <returns>A configured management handler.</returns>
    public IExtensionHandler Create(IControllerManagementDispatcher dispatcher, ControllerOptions options) => new ControllerManagementHandler(dispatcher, options);
    /// <summary>Creates the streaming management handler after the runtime host-version gate.</summary>
    /// <param name="dispatcher">Dispatcher that processes management requests.</param>
    /// <param name="options">Controller options used by the handler.</param>
    /// <returns>The streaming management handler.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IExtensionStreamingHandler? CreateStreaming(
        IControllerManagementDispatcher dispatcher,
        ControllerOptions options) => new ControllerStreamingManagementHandler(dispatcher, options);
}
