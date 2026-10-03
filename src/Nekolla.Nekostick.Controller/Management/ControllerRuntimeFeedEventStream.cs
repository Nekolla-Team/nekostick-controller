using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>
/// Read-only <see cref="Stream" /> that frames the node-local service runtime-state feed as
/// Server-Sent Events. Works on both SSE-capable transports: the Kestrel adapter copies it into the
/// response body, the HostRoute streaming handler hands it to the host. Disposing the stream detaches
/// the feed view.
/// </summary>
/// <remarks>
/// Frames: <c>id:&lt;sequence&gt;\nevent:state\ndata:&lt;json&gt;\n\n</c> per change (the subscribe-time
/// snapshot set replays first), <c>: heartbeat\n\n</c> after 15s of silence, and a terminal
/// <c>event:end\ndata:{"reason":"..."}\n\n</c> before completion.
/// </remarks>
internal sealed class ControllerRuntimeFeedEventStream : Stream
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly byte[] HeartbeatFrame = ": heartbeat\n\n"u8.ToArray();

    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _disposed = new();
    private readonly ControllerRuntimeStateFeed.View _view;
    private readonly Task _pump;
    private byte[]? _current;
    private int _currentOffset;
    private int _disposedFlag;

    private ControllerRuntimeFeedEventStream(ControllerRuntimeStateFeed.View view, CancellationToken sessionEnded)
    {
        _view = view;
        _pump = Task.Run(() => PumpAsync(view, sessionEnded), CancellationToken.None);
    }

    /// <summary>Starts framing one subscribed feed view; the result owns the view.</summary>
    /// <param name="view">The subscribed feed view; ownership transfers to the result.</param>
    /// <param name="sessionEnded">Fires when the admitting session ends and the stream must close.</param>
    internal static ControllerRuntimeFeedEventStream Start(
        ControllerRuntimeStateFeed.View view,
        CancellationToken sessionEnded) =>
        new(view ?? throw new ArgumentNullException(nameof(view)), sessionEnded);

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
            _view.Dispose();
            _frames.Writer.TryComplete();
            _disposed.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task PumpAsync(ControllerRuntimeStateFeed.View view, CancellationToken sessionEnded)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(sessionEnded, _disposed.Token);
        var token = session.Token;
        try
        {
            foreach (var snapshot in view.Snapshot.OrderBy(static change => change.Sequence))
            {
                if (!await WriteFrameAsync(CreateStateFrame(snapshot), token).ConfigureAwait(false))
                {
                    return;
                }
            }

            var readTask = view.Reader.ReadAsync(token).AsTask();
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

                ExtensionServiceRuntimeStateChange change;
                try
                {
                    change = await readTask; // observe failure/cancellation
                }
                catch (ChannelClosedException)
                {
                    // The feed completed (runtime teardown); consumers resubscribe for fresh state.
                    await WriteEndAsync("sessionEnded", CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                if (!await WriteFrameAsync(CreateStateFrame(change), token).ConfigureAwait(false))
                {
                    return;
                }

                readTask = view.Reader.ReadAsync(token).AsTask();
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
            await WriteEndAsync("fault", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
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

    private static byte[] CreateStateFrame(ExtensionServiceRuntimeStateChange change)
    {
        var entry = ToFeedEntry(change);
        var json = JsonSerializer.Serialize(entry, ControllerManagementJson.Options);
        return Encoding.UTF8.GetBytes($"id:{entry.Sequence}\nevent:state\ndata:{json}\n\n");
    }

    /// <summary>NoInlining: touches contract members that postdate the API 1.4.0 baseline.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ControllerServiceRuntimeFeedEntryDto ToFeedEntry(ExtensionServiceRuntimeStateChange change) =>
        new()
        {
            Sequence = change.Sequence,
            Kind = change.Kind == ExtensionServiceRuntimeStateChangeKind.Removed ? "removed" : "snapshot",
            ServiceId = change.ServiceId,
            IsInitialSnapshot = change.IsInitialSnapshot,
            OwnerExtensionId = change.OwnerExtensionId,
            Snapshot = change.Kind == ExtensionServiceRuntimeStateChangeKind.Removed || change.Snapshot is not { } snapshot
                ? null
                : ControllerContractMapper.ToRead(snapshot)
        };
}
