using System.Collections.Immutable;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>
/// Contains one transport-neutral management request. Adapters translate their protocol into
/// this DTO; no ASP.NET, gRPC, socket, process, or other host object crosses the seam.
/// </summary>
public sealed class ControllerManagementRequest
{
    /// <summary>Creates a request from host-bounded immutable headers.</summary>
    /// <param name="transport">The adapter transport.</param>
    /// <param name="method">The normalized operation method.</param>
    /// <param name="path">The normalized operation path or gRPC method.</param>
    /// <param name="apiKey">The bounded presented key, if one was supplied.</param>
    /// <param name="headers">The immutable bounded protocol metadata.</param>
    /// <param name="body">The copied request body.</param>
    internal ControllerManagementRequest(
        ControllerTransport transport,
        string method,
        string path,
        string? apiKey,
        ImmutableDictionary<string, ImmutableArray<string>> headers,
        ReadOnlyMemory<byte> body)
    {
        if (string.IsNullOrWhiteSpace(method) || method.Length > 32)
        {
            throw new ArgumentException("A management method is required.", nameof(method));
        }

        if (string.IsNullOrWhiteSpace(path) || path.Length > 8192)
        {
            throw new ArgumentException("A management path is required.", nameof(path));
        }

        if (body.Length > ControllerAdmissionLimits.MaximumRequestBodyBytes)
        {
            throw new ArgumentException("The management request body is too large.", nameof(body));
        }

        if (apiKey is { Length: > ControllerOptions.MaximumApiKeyLength })
        {
            throw new ArgumentException("The management API key is too large.", nameof(apiKey));
        }

        Transport = transport;
        Method = method;
        Path = path;
        ApiKey = apiKey;
        Headers = headers ?? throw new ArgumentNullException(nameof(headers));
        Body = body.IsEmpty ? ImmutableArray<byte>.Empty : ImmutableArray.CreateRange(body.ToArray());
    }

    /// <summary>Creates a bounded transport-neutral management request.</summary>
    /// <param name="transport">The adapter transport.</param>
    /// <param name="method">The normalized operation method.</param>
    /// <param name="path">The normalized operation path or gRPC method.</param>
    /// <param name="apiKey">The presented key, if the adapter received one. It is never logged.</param>
    /// <param name="headers">The copied protocol metadata.</param>
    /// <param name="body">The copied request body.</param>
    public ControllerManagementRequest(
        ControllerTransport transport,
        string method,
        string path,
        string? apiKey = null,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers = null,
        ReadOnlyMemory<byte> body = default)
        : this(
            transport,
            method,
            path,
            apiKey,
            ControllerAdmissionLimits.CopyRequestHeaders(method, path, apiKey, headers, body),
            body)
    {
    }

    /// <summary>Gets the transport that supplied the request.</summary>
    public ControllerTransport Transport { get; }

    /// <summary>Gets the normalized method or operation verb.</summary>
    public string Method { get; }

    /// <summary>Gets the normalized path or protocol method.</summary>
    public string Path { get; }

    /// <summary>Gets the presented API key. Consumers must not log or persist it.</summary>
    public string? ApiKey { get; }

    /// <summary>Gets copied protocol metadata.</summary>
    public IReadOnlyDictionary<string, ImmutableArray<string>> Headers { get; }

    /// <summary>Gets an immutable copy of the request body.</summary>
    public ImmutableArray<byte> Body { get; }
}
