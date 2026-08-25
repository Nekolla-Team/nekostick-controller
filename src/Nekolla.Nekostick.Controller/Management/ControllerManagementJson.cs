using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Bounded JSON serialization and deserialization boundary.</summary>
public static class ControllerManagementJson
{
    /// <summary>Maximum serialized response body size in bytes.</summary>
    public const int MaximumResponseBodyBytes = 1024 * 1024;
    /// <summary>Maximum size in bytes of embedded JSON values.</summary>
    public const int MaximumEmbeddedJsonBytes = MaximumResponseBodyBytes;
    /// <summary>Serializer options used by the management API.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    internal static ReadOnlyMemory<byte> ResponseTooLargeBody => ResponseTooLargeEnvelopeBytes;

    /// <summary>Attempts to deserialize a management API JSON body.</summary>
    /// <typeparam name="T">Expected model type.</typeparam>
    /// <param name="body">UTF-8 JSON body to deserialize.</param>
    /// <param name="value">Deserialized value when successful; otherwise the default value.</param>
    /// <returns><see langword="true"/> when the body is valid and produces a value; otherwise <see langword="false"/>.</returns>
    public static bool TryDeserialize<T>(ReadOnlyMemory<byte> body, out T? value)
    {
        value = default;
        if (body.IsEmpty || body.Length > ControllerAdmissionLimits.MaximumRequestBodyBytes) return false;
        try { value = JsonSerializer.Deserialize<T>(body.Span, Options); return value is not null; }
        catch (JsonException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (ArgumentException) { return false; }
    }

    internal static ReadOnlyMemory<byte> AsReadOnlyMemory(ImmutableArray<byte> body) =>
        ImmutableCollectionsMarshal.AsArray(body) ?? ReadOnlyMemory<byte>.Empty;

    internal static bool TrySerialize(ControllerResponseEnvelope response, out byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(response);
        payload = Array.Empty<byte>();
        using var buffer = new BoundedBufferWriter(MaximumResponseBodyBytes);
        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                JsonSerializer.Serialize(writer, response, Options);
                writer.Flush();
            }
            payload = buffer.ToArray();
            return true;
        }
        catch (ResponseTooLargeException) { return false; }
    }

    /// <summary>Serializes a response envelope, returning a bounded error envelope if it is too large.</summary>
    /// <param name="response">Response envelope to serialize.</param>
    /// <returns>UTF-8 JSON representation of the response envelope.</returns>
    public static byte[] Serialize(ControllerResponseEnvelope response) =>
        TrySerialize(response, out var payload) ? payload : ResponseTooLargeBody.ToArray();

    private static readonly byte[] ResponseTooLargeEnvelopeBytes = Encoding.UTF8.GetBytes(
        $"{{\"apiVersion\":{ControllerManagementApiContract.Version},\"ok\":false,\"code\":\"response_too_large\",\"message\":\"The management response is too large.\",\"data\":null,\"version\":null}}");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private sealed class BoundedBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private readonly int _maximumLength;
        private byte[]? _buffer;
        private int _written;
        internal BoundedBufferWriter(int maximumLength) { _maximumLength = maximumLength; _buffer = ArrayPool<byte>.Shared.Rent(maximumLength); }
        public void Advance(int count)
        {
            if (count < 0 || count > _maximumLength - _written) throw new ResponseTooLargeException();
            _written += count;
        }
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            ValidateSizeHint(sizeHint);
            var buffer = _buffer ?? throw new ObjectDisposedException(nameof(BoundedBufferWriter));
            var remaining = _maximumLength - _written;
            if (remaining == 0) throw new ResponseTooLargeException();
            return buffer.AsMemory(_written, remaining);
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        internal byte[] ToArray() => (_buffer ?? throw new ObjectDisposedException(nameof(BoundedBufferWriter))).AsSpan(0, _written).ToArray();
        public void Dispose()
        {
            if (_buffer is not { } buffer) return;
            _buffer = null; _written = 0; ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
        private void ValidateSizeHint(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            if (sizeHint > _maximumLength - _written) throw new ResponseTooLargeException();
        }
    }
    private sealed class ResponseTooLargeException : Exception { }
}
