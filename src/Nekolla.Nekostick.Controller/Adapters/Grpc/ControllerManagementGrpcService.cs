using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Nekolla.Nekostick.Controller.Grpc;

namespace Nekolla.Nekostick.Controller.Adapters.Grpc;

/// <summary>
/// Bridges the generated unary gRPC service to the transport-neutral management dispatcher.
/// </summary>
public sealed class ControllerManagementGrpcService : ControllerManagement.ControllerManagementBase
{
    private const string ApiKeyHeaderName = "x-nekostick-controller-key";

    private readonly IControllerManagementDispatcher _dispatcher;

    /// <summary>Creates a bridge over the shared management dispatcher.</summary>
    public ControllerManagementGrpcService(IControllerManagementDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <inheritdoc />
    public override async Task<InvokeResponse> Invoke(
        InvokeRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();

        var apiKeyResult = TryExtractApiKey(context.RequestHeaders, out var apiKey);
        if (apiKeyResult == ApiKeyMetadataResult.Unauthorized)
        {
            return ToInvokeResponse(ControllerManagementResponseBuilder.Unauthorized);
        }

        if (apiKeyResult == ApiKeyMetadataResult.InvalidRequest ||
            request.Body.Length > ControllerAdmissionLimits.MaximumRequestBodyBytes ||
            !TryCreateEnvelopeHeaders(request, apiKey, out var headers))
        {
            return ToInvokeResponse(ControllerManagementResponseBuilder.InvalidRequest);
        }

        // Copy only after every transport-native and admission bound has been checked. The
        // transport-neutral request constructor makes its own immutable copy for dispatch.
        var body = request.Body.ToByteArray();
        if (!ControllerAdmissionLimits.TryCreateRequest(
                ControllerTransport.Grpc,
                request.Method,
                request.Path,
                apiKey,
                headers,
                body,
                out var managementRequest) ||
            managementRequest is null)
        {
            return ToInvokeResponse(ControllerManagementResponseBuilder.InvalidRequest);
        }

        ControllerManagementResponse response;
        try
        {
            response = await _dispatcher.DispatchAsync(
                managementRequest,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Cancellation is a transport-level event and must not be converted into a response
            // envelope after the caller has gone away.
            throw;
        }
        catch
        {
            // Dispatcher implementations should return canonical responses. Preserve the gRPC
            // envelope contract if an implementation fails outside that contract.
            response = ControllerManagementResponseBuilder.Unavailable;
        }

        return ToInvokeResponse(response);
    }

    private enum ApiKeyMetadataResult
    {
        Valid,
        Unauthorized,
        InvalidRequest
    }

    private static ApiKeyMetadataResult TryExtractApiKey(Metadata metadata, out string? apiKey)
    {
        apiKey = null;
        string? candidate = null;
        var authenticationHeaderSeen = false;
        var authenticationHeaderInvalid = false;
        var headerCount = 0;
        var aggregateLength = 0L;

        try
        {
            foreach (var entry in metadata)
            {
                if (++headerCount > ControllerAdmissionLimits.MaximumHeaderCount ||
                    string.IsNullOrWhiteSpace(entry.Key) ||
                    entry.Key.Length > ControllerAdmissionLimits.MaximumHeaderNameLength)
                {
                    return ApiKeyMetadataResult.InvalidRequest;
                }

                var isApiKey = string.Equals(entry.Key, ApiKeyHeaderName, StringComparison.OrdinalIgnoreCase);
                aggregateLength += Encoding.UTF8.GetByteCount(entry.Key);
                if (entry.IsBinary)
                {
                    if (entry.ValueBytes is not { Length: <= ControllerAdmissionLimits.MaximumHeaderValueLength } valueBytes)
                    {
                        return isApiKey
                            ? ApiKeyMetadataResult.Unauthorized
                            : ApiKeyMetadataResult.InvalidRequest;
                    }

                    aggregateLength += valueBytes.Length;
                    if (aggregateLength > ControllerAdmissionLimits.MaximumAggregateHeaderBytes)
                    {
                        return ApiKeyMetadataResult.InvalidRequest;
                    }

                    if (isApiKey)
                    {
                        authenticationHeaderSeen = true;
                        authenticationHeaderInvalid = true;
                    }

                    continue;
                }

                var value = entry.Value;
                if (value is null || value.Length > ControllerAdmissionLimits.MaximumHeaderValueLength)
                {
                    return isApiKey
                        ? ApiKeyMetadataResult.Unauthorized
                        : ApiKeyMetadataResult.InvalidRequest;
                }

                aggregateLength += Encoding.UTF8.GetByteCount(value);
                if (aggregateLength > ControllerAdmissionLimits.MaximumAggregateHeaderBytes)
                {
                    return ApiKeyMetadataResult.InvalidRequest;
                }

                if (!isApiKey)
                {
                    continue;
                }

                if (authenticationHeaderSeen ||
                    string.IsNullOrWhiteSpace(value) ||
                    value.Length > ControllerOptions.MaximumApiKeyLength)
                {
                    authenticationHeaderInvalid = true;
                }
                else
                {
                    candidate = value;
                }

                authenticationHeaderSeen = true;
            }
        }
        catch (ArgumentException)
        {
            return ApiKeyMetadataResult.InvalidRequest;
        }

        if (!authenticationHeaderSeen || authenticationHeaderInvalid || candidate is null)
        {
            return ApiKeyMetadataResult.Unauthorized;
        }

        apiKey = candidate;
        return ApiKeyMetadataResult.Valid;
    }


    private static bool TryCreateEnvelopeHeaders(
        InvokeRequest request,
        string? apiKey,
        out List<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        headers = new List<KeyValuePair<string, IEnumerable<string>>>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aggregateLength = 0L;

        // Reserve one bounded request header for the authentication value extracted from gRPC
        // metadata. An API-key header in the protobuf envelope is never accepted.
        foreach (var envelopeHeader in request.Headers)
        {
            if (envelopeHeader is null ||
                headers.Count >= ControllerAdmissionLimits.MaximumHeaderCount - 1 ||
                string.IsNullOrWhiteSpace(envelopeHeader.Name) ||
                envelopeHeader.Name.Length > ControllerAdmissionLimits.MaximumHeaderNameLength ||
                !names.Add(envelopeHeader.Name) ||
                string.Equals(envelopeHeader.Name, ApiKeyHeaderName, StringComparison.OrdinalIgnoreCase) ||
                envelopeHeader.Values.Count > ControllerAdmissionLimits.MaximumHeaderValues)
            {
                return false;
            }

            aggregateLength += Encoding.UTF8.GetByteCount(envelopeHeader.Name);
            if (aggregateLength > ControllerAdmissionLimits.MaximumAggregateHeaderBytes)
            {
                return false;
            }

            var values = new string[envelopeHeader.Values.Count];
            for (var index = 0; index < envelopeHeader.Values.Count; index++)
            {
                var value = envelopeHeader.Values[index];
                if (value is null || value.Length > ControllerAdmissionLimits.MaximumHeaderValueLength)
                {
                    return false;
                }

                aggregateLength += Encoding.UTF8.GetByteCount(value);
                if (aggregateLength > ControllerAdmissionLimits.MaximumAggregateHeaderBytes)
                {
                    return false;
                }

                values[index] = value;
            }

            headers.Add(new KeyValuePair<string, IEnumerable<string>>(envelopeHeader.Name, values));
        }

        if (!names.Add(ApiKeyHeaderName))
        {
            return false;
        }

        var boundedApiKey = apiKey ?? string.Empty;
        if (boundedApiKey.Length > ControllerOptions.MaximumApiKeyLength)
        {
            return false;
        }

        aggregateLength += Encoding.UTF8.GetByteCount(ApiKeyHeaderName);
        aggregateLength += Encoding.UTF8.GetByteCount(boundedApiKey);
        if (aggregateLength > ControllerAdmissionLimits.MaximumAggregateHeaderBytes)
        {
            return false;
        }

        headers.Add(new KeyValuePair<string, IEnumerable<string>>(
            ApiKeyHeaderName,
            new[] { boundedApiKey }));
        return true;
    }

    private static InvokeResponse ToInvokeResponse(ControllerManagementResponse response)
    {
        if (response is null || !TryValidateResponse(response))
        {
            response = ControllerManagementResponseBuilder.Unavailable;
        }

        var result = new InvokeResponse
        {
            StatusCode = response.StatusCode,
            Code = ToDispatchCode(response.Code),
            Body = ByteString.CopyFrom(response.Body.ToArray())
        };

        foreach (var pair in response.Headers)
        {
            var header = new Header { Name = pair.Key };
            header.Values.AddRange(pair.Value);
            result.Headers.Add(header);
        }

        return result;
    }

    private static bool TryValidateResponse(ControllerManagementResponse response)
    {
        if (response.Body.Length > ControllerAdmissionLimits.MaximumRequestBodyBytes)
        {
            return false;
        }

        var headerCount = 0;
        var aggregateLength = 0L;
        foreach (var pair in response.Headers)
        {
            if (++headerCount > ControllerAdmissionLimits.MaximumHeaderCount ||
                string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Key.Length > ControllerAdmissionLimits.MaximumHeaderNameLength ||
                pair.Value.Length > ControllerAdmissionLimits.MaximumHeaderValues)
            {
                return false;
            }

            aggregateLength += Encoding.UTF8.GetByteCount(pair.Key);
            if (aggregateLength > ControllerAdmissionLimits.MaximumAggregateHeaderBytes)
            {
                return false;
            }

            foreach (var value in pair.Value)
            {
                if (value is null || value.Length > ControllerAdmissionLimits.MaximumHeaderValueLength)
                {
                    return false;
                }

                aggregateLength += Encoding.UTF8.GetByteCount(value);
                if (aggregateLength > ControllerAdmissionLimits.MaximumAggregateHeaderBytes)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string ToDispatchCode(ControllerDispatchCode code) => code switch
    {
        ControllerDispatchCode.Success => "success",
        ControllerDispatchCode.InvalidRequest => "invalid_request",
        ControllerDispatchCode.Unauthorized => "unauthorized",
        ControllerDispatchCode.NotFound => "not_found",
        ControllerDispatchCode.TransportDisabled => "transport_disabled",
        ControllerDispatchCode.Conflict => "conflict",
        ControllerDispatchCode.Unsupported => "unsupported",
        ControllerDispatchCode.Unavailable => "unavailable",
        _ => "unavailable"
    };

}
