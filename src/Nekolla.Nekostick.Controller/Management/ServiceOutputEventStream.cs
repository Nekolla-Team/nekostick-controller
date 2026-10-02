using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Threading.Channels;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>
/// Read-only <see cref="Stream" /> that frames one live service-output stream as Server-Sent
/// Events for the HostRoute streaming handler. The Host copies this stream to the client; disposing
/// it (client disconnect or route teardown) detaches the underlying Host subscription.
/// </summary>
/// <remarks>
/// Frames: <c>data:&lt;base64&gt;\n\n</c> per upstream chunk, <c>: heartbeat\n\n</c> after
/// <see cref="HeartbeatInterval" /> of silence, and a terminal
/// <c>event:end\ndata:{"reason":"..."}\n\n</c> before completion. The pump never cancels a pending
/// upstream read for a heartbeat.
/// </remarks>
internal sealed class ServiceOutputEventStream : Stream
{
    /// <summary>Idle interval after which a heartbeat comment is emitted.</summary>
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private static readonly byte[] HeartbeatFrame = ": heartbeat\n\n"u8.ToArray();

    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _disposed = new();
    private readonly Stream _upstream;
    private readonly Task _pump;
    private byte[]? _current;
    private int _currentOffset;
    private int _disposedFlag;

    private ServiceOutputEventStream(Stream upstream, CancellationToken sessionEnded)
    {
        _upstream = upstream;
        _pump = Task.Run(() => PumpAsync(upstream, sessionEnded), CancellationToken.None);
    }

    /// <summary>Starts framing one opened service-output stream; the result owns the stream.</summary>
    /// <param name="upstream">The live host output stream; ownership transfers to the result.</param>
    /// <param name="sessionEnded">Fires when the admitting session ends and the stream must close.</param>
    public static ServiceOutputEventStream Start(Stream upstream, CancellationToken sessionEnded) =>
        new(upstream ?? throw new ArgumentNullException(nameof(upstream)), sessionEnded);

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
            _disposed.Cancel();
            // Tearing down the upstream read is intentional here: disposal means the client is
            // gone, so the Host subscription must detach immediately instead of drifting.
            _upstream.Dispose();
            _frames.Writer.TryComplete();
            _disposed.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task PumpAsync(Stream upstream, CancellationToken sessionEnded)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(sessionEnded, _disposed.Token);
        var token = session.Token;
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var readTask = upstream.ReadAsync(buffer.AsMemory(), token).AsTask();
            while (true)
            {
                var heartbeat = Task.Delay(HeartbeatInterval, token);
                var completed = await Task.WhenAny(readTask, heartbeat).ConfigureAwait(false);
                if (completed == heartbeat)
                {
                    await heartbeat; // observe cancellation
                    if (!await WriteFrameAsync(HeartbeatFrame, token).ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }

                var read = await readTask; // observe failure/cancellation
                if (read == 0)
                {
                    await WriteEndAsync("processExited", token).ConfigureAwait(false);
                    return;
                }

                if (!await WriteFrameAsync(CreateDataFrame(buffer.AsSpan(0, read)), token).ConfigureAwait(false))
                {
                    return;
                }

                readTask = upstream.ReadAsync(buffer.AsMemory(), token).AsTask();
            }
        }
        catch (OperationCanceledException) when (sessionEnded.IsCancellationRequested)
        {
            await WriteEndAsync("sessionEnded", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) when (_disposed.IsCancellationRequested)
        {
            // Client disconnect: the transport disposed us; complete silently.
        }
        catch (Exception)
        {
            // Host teardown or a fan-out fault ends the stream.
            await WriteEndAsync("fault", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _frames.Writer.TryComplete();
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

    private async ValueTask WriteEndAsync(string reason, CancellationToken cancellationToken)
    {
        var frame = Encoding.UTF8.GetBytes($"event:end\ndata:{{\"reason\":\"{reason}\"}}\n\n");
        await WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] CreateDataFrame(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[5 + ((payload.Length + 2) / 3) * 4 + 2];
        "data:"u8.CopyTo(frame);
        Base64.EncodeToUtf8(payload, frame.AsSpan(5), out _, out var written);
        "\n\n"u8.CopyTo(frame.AsSpan(5 + written));
        return frame;
    }
}
