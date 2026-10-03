using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Read-only <see cref="Stream" /> that frames a cross-generation service-log feed as Server-Sent Events.</summary>
/// <remarks>The pump keeps one feed read pending while sending a heartbeat after 15 seconds of silence.</remarks>
internal sealed class ServiceLogEventStream : Stream
{
    /// <summary>Idle interval after which a heartbeat comment is emitted.</summary>
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private static readonly byte[] HeartbeatFrame = ": heartbeat\n\n"u8.ToArray();

    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _disposed = new();
    private readonly Task _pump;
    private byte[]? _current;
    private int _currentOffset;
    private int _disposedFlag;

    private ServiceLogEventStream(ControllerServiceLogFeed feed, CancellationToken sessionEnded)
    {
        _pump = Task.Run(() => PumpAsync(feed, sessionEnded), CancellationToken.None);
    }

    /// <summary>Starts framing one subscribed service-log feed; the result owns the feed.</summary>
    /// <param name="feed">The live host feed; ownership transfers to the result.</param>
    /// <param name="sessionEnded">Fires when the admitting session ends and the feed must close.</param>
    public static ServiceLogEventStream Start(ControllerServiceLogFeed feed, CancellationToken sessionEnded) =>
        new(feed ?? throw new ArgumentNullException(nameof(feed)), sessionEnded);

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_current is { } current)
            {
                var count = Math.Min(buffer.Length, current.Length - _currentOffset);
                current.AsSpan(_currentOffset, count).CopyTo(buffer.Span);
                _currentOffset += count;
                if (_currentOffset == current.Length)
                {
                    _current = null;
                    _currentOffset = 0;
                }

                return count;
            }

            byte[] next;
            try
            {
                next = await _frames.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return 0;
            }

            _current = next;
            _currentOffset = 0;
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposedFlag, 1) == 0)
        {
            _frames.Writer.TryComplete();
            try
            {
                _disposed.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The pump already completed and disposed the cancellation source.
            }
        }

        base.Dispose(disposing);
    }

    private async Task PumpAsync(ControllerServiceLogFeed feed, CancellationToken sessionEnded)
    {
        try
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(sessionEnded, _disposed.Token);
            var token = session.Token;
            var readTask = feed.Reader.ReadAsync(token).AsTask();
            while (true)
            {
                var heartbeat = Task.Delay(HeartbeatInterval, token);
                var completed = await Task.WhenAny(readTask, heartbeat).ConfigureAwait(false);
                if (completed == heartbeat)
                {
                    await heartbeat.ConfigureAwait(false);
                    if (!await WriteFrameAsync(HeartbeatFrame, token).ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }

                ControllerServiceLogFeedItem item;
                try
                {
                    item = await readTask.ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    await WriteEndAsync("sessionEnded", token).ConfigureAwait(false);
                    return;
                }

                if (!await WriteFrameAsync(CreateEntryFrame(item.Entry), token).ConfigureAwait(false))
                {
                    return;
                }

                if (string.Equals(item.Entry.Kind, "termination", StringComparison.Ordinal))
                {
                    await WriteEndAsync(item.Entry.TerminationReason ?? "sessionEnded", token).ConfigureAwait(false);
                    return;
                }

                readTask = feed.Reader.ReadAsync(token).AsTask();
            }
        }
        catch (OperationCanceledException) when (
            sessionEnded.IsCancellationRequested || Volatile.Read(ref _disposedFlag) != 0)
        {
            // Session revocation or stream disposal ends the pump without waiting for another entry.
        }
        finally
        {
            _frames.Writer.TryComplete();
            try
            {
                await feed.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _disposed.Dispose();
            }
        }
    }

    private async ValueTask<bool> WriteFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        try
        {
            return await _frames.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false) &&
                _frames.Writer.TryWrite(frame);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private ValueTask<bool> WriteEndAsync(string reason, CancellationToken cancellationToken) =>
        WriteFrameAsync(Encoding.UTF8.GetBytes($"event:end\ndata:{{\"reason\":\"{reason}\"}}\n\n"), cancellationToken);

    private static byte[] CreateEntryFrame(ControllerServiceLogEntryDto entry) =>
        string.Equals(entry.Kind, "output", StringComparison.Ordinal)
            ? CreateOutputFrame(entry)
            : CreateStateFrame(entry);

    private static byte[] CreateOutputFrame(ControllerServiceLogEntryDto entry)
    {
        var data = entry.Data ?? string.Empty;
        var sequenceLength = GetSequenceLength(entry.Sequence);
        var idLength = entry.Sequence.HasValue ? 4 + sequenceLength : 0;
        var frame = new byte[idLength + 5 + Encoding.UTF8.GetByteCount(data) + 2];
        var offset = WriteId(entry.Sequence, frame);
        "data:"u8.CopyTo(frame.AsSpan(offset));
        offset += 5;
        offset += Encoding.UTF8.GetBytes(data.AsSpan(), frame.AsSpan(offset));
        "\n\n"u8.CopyTo(frame.AsSpan(offset));
        return frame;
    }

    private static byte[] CreateStateFrame(ControllerServiceLogEntryDto entry)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(entry, ControllerManagementJson.Options);
        var sequenceLength = GetSequenceLength(entry.Sequence);
        var idLength = entry.Sequence.HasValue ? 4 + sequenceLength : 0;
        var statePrefix = "event:state\ndata:"u8;
        var frame = new byte[idLength + statePrefix.Length + json.Length + 2];
        var offset = WriteId(entry.Sequence, frame);
        statePrefix.CopyTo(frame.AsSpan(offset));
        offset += statePrefix.Length;
        json.AsSpan().CopyTo(frame.AsSpan(offset));
        offset += json.Length;
        "\n\n"u8.CopyTo(frame.AsSpan(offset));
        return frame;
    }

    private static int GetSequenceLength(long? sequence)
    {
        if (sequence is not { } value)
        {
            return 0;
        }

        Span<byte> formatted = stackalloc byte[20];
        if (!Utf8Formatter.TryFormat(value, formatted, out var written))
        {
            throw new InvalidOperationException("The service-log sequence could not be formatted.");
        }

        return written;
    }

    private static int WriteId(long? sequence, Span<byte> destination)
    {
        if (sequence is not { } value)
        {
            return 0;
        }

        "id:"u8.CopyTo(destination);
        var offset = 3;
        if (!Utf8Formatter.TryFormat(value, destination[offset..], out var written))
        {
            throw new InvalidOperationException("The service-log sequence could not be formatted.");
        }

        offset += written;
        destination[offset++] = (byte)'\n';
        return offset;
    }
}
