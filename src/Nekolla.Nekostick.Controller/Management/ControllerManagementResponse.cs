using System.Collections.Immutable;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Contains one safe response from the shared management dispatcher.</summary>
public sealed class ControllerManagementResponse
{
    /// <summary>Creates a response with a protocol-independent status code and body.</summary>
    /// <param name="statusCode">The HTTP-compatible status code for adapters.</param>
    /// <param name="code">The safe dispatch category.</param>
    /// <param name="headers">The response metadata.</param>
    /// <param name="body">The response body bytes.</param>
    public ControllerManagementResponse(
        int statusCode,
        ControllerDispatchCode code,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers = null,
        ReadOnlyMemory<byte> body = default)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        StatusCode = statusCode;
        Code = code;
        Headers = CopyHeaders(headers);
        Body = body.IsEmpty ? ImmutableArray<byte>.Empty : ImmutableArray.CreateRange(body.ToArray());
    }

    /// <summary>Gets the HTTP-compatible status code.</summary>
    public int StatusCode { get; }

    /// <summary>Gets the safe dispatch category.</summary>
    public ControllerDispatchCode Code { get; }

    /// <summary>Gets copied response metadata.</summary>
    public IReadOnlyDictionary<string, ImmutableArray<string>> Headers { get; }

    /// <summary>Gets an immutable copy of the response body.</summary>
    public ImmutableArray<byte> Body { get; }

    private static readonly KeyValuePair<string, IEnumerable<string>>[] NoStoreHeader =
        { new("cache-control", new[] { "no-store" }) };

    /// <summary>Creates an empty invalid-request response.</summary>
    public static ControllerManagementResponse InvalidRequest =>
        new(400, ControllerDispatchCode.InvalidRequest, NoStoreHeader);

    /// <summary>Creates an empty unavailable response.</summary>
    public static ControllerManagementResponse Unavailable =>
        new(503, ControllerDispatchCode.Unavailable, NoStoreHeader);

    /// <summary>Creates an empty unauthorized response.</summary>
    public static ControllerManagementResponse Unauthorized =>
        new(401, ControllerDispatchCode.Unauthorized, NoStoreHeader);

    /// <summary>Creates an empty transport-disabled response.</summary>
    public static ControllerManagementResponse TransportDisabled =>
        new(404, ControllerDispatchCode.TransportDisabled, NoStoreHeader);

    /// <summary>Creates an empty not-found response.</summary>
    public static ControllerManagementResponse NotFound =>
        new(404, ControllerDispatchCode.NotFound, NoStoreHeader);

    private static ImmutableDictionary<string, ImmutableArray<string>> CopyHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers)
    {
        var result = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(
            StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return result.ToImmutable();
        }

        foreach (var pair in headers)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 256 || result.ContainsKey(pair.Key))
            {
                throw new ArgumentException("Management response headers are invalid.", nameof(headers));
            }

            var values = pair.Value?.ToImmutableArray()
                ?? throw new ArgumentException("Management response headers are invalid.", nameof(headers));
            if (values.Any(static value => value is null || value.Length > 16 * 1024))
            {
                throw new ArgumentException("Management response headers are invalid.", nameof(headers));
            }

            result.Add(pair.Key, values);
        }

        return result.ToImmutable();
    }
}
