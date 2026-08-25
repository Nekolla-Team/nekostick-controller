using System.Collections.Immutable;
using System.Text;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Defines bounded request admission before transport-neutral DTO construction.</summary>
public static class ControllerAdmissionLimits
{
    /// <summary>The maximum request body copied into a management request.</summary>
    public const int MaximumRequestBodyBytes = 1024 * 1024;

    /// <summary>The maximum number of request header names.</summary>
    public const int MaximumHeaderCount = 64;

    /// <summary>The maximum number of values for one request header name.</summary>
    public const int MaximumHeaderValues = 64;

    /// <summary>The maximum request header-name length.</summary>
    public const int MaximumHeaderNameLength = 256;

    /// <summary>The maximum request header-value length.</summary>
    public const int MaximumHeaderValueLength = 16 * 1024;

    /// <summary>The maximum aggregate request header name/value length.</summary>
    public const int MaximumAggregateHeaderBytes = 64 * 1024;

    /// <summary>Checks bounded request inputs without constructing a management request.</summary>
    public static bool IsWithinLimits(
        string? apiKey,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers,
        ReadOnlyMemory<byte> body)
    {
        if (body.Length > MaximumRequestBodyBytes ||
            apiKey is { Length: > ControllerOptions.MaximumApiKeyLength })
        {
            return false;
        }

        try
        {
            _ = CopyHeaders(headers);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Attempts bounded construction for adapters before dispatch admission.</summary>
    public static bool TryCreateRequest(
        ControllerTransport transport,
        string method,
        string path,
        string? apiKey,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers,
        ReadOnlyMemory<byte> body,
        out ControllerManagementRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(method) || method.Length > 32 ||
            string.IsNullOrWhiteSpace(path) || path.Length > 8192 ||
            body.Length > MaximumRequestBodyBytes ||
            apiKey is { Length: > ControllerOptions.MaximumApiKeyLength })
        {
            return false;
        }


        try
        {
            var boundedHeaders = CopyHeaders(headers);
            request = new ControllerManagementRequest(
                transport,
                method,
                path,
                apiKey,
                boundedHeaders,
                body);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }


    internal static ImmutableDictionary<string, ImmutableArray<string>> CopyRequestHeaders(
        string method,
        string path,
        string? apiKey,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers,
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

        if (body.Length > MaximumRequestBodyBytes)
        {
            throw new ArgumentException("The management request body is too large.", nameof(body));
        }

        if (apiKey is { Length: > ControllerOptions.MaximumApiKeyLength })
        {
            throw new ArgumentException("The management API key is too large.", nameof(apiKey));
        }

        return CopyHeaders(headers);
    }

    internal static ImmutableDictionary<string, ImmutableArray<string>> CopyHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers)
    {
        var result = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(
            StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return result.ToImmutable();
        }

        var headerCount = 0;
        var aggregateLength = 0L;
        foreach (var pair in headers)
        {
            if (++headerCount > MaximumHeaderCount ||
                string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Key.Length > MaximumHeaderNameLength ||
                result.ContainsKey(pair.Key))
            {
                throw new ArgumentException("Management request headers are invalid.", nameof(headers));
            }

            aggregateLength += Encoding.UTF8.GetByteCount(pair.Key);
            if (aggregateLength > MaximumAggregateHeaderBytes)
            {
                throw new ArgumentException("Management request headers are too large.", nameof(headers));
            }

            if (pair.Value is null)
            {
                throw new ArgumentException("Management request headers are invalid.", nameof(headers));
            }

            var values = ImmutableArray.CreateBuilder<string>();
            var valueCount = 0;
            foreach (var value in pair.Value)
            {
                if (++valueCount > MaximumHeaderValues ||
                    value is null ||
                    value.Length > MaximumHeaderValueLength)
                {
                    throw new ArgumentException("Management request headers are invalid.", nameof(headers));
                }

                aggregateLength += Encoding.UTF8.GetByteCount(value);
                if (aggregateLength > MaximumAggregateHeaderBytes)
                {
                    throw new ArgumentException("Management request headers are too large.", nameof(headers));
                }

                values.Add(value);
            }

            result.Add(pair.Key, values.ToImmutable());
        }

        return result.ToImmutable();
    }
}
