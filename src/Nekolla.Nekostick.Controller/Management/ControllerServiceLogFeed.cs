using System.Collections.Immutable;
using System.Threading.Channels;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>
/// One consumer subscription over the host service-log feed (Contracts 1.4 preview log API). The
/// host pushes ordered entries for one service across process generations; the sink maps them to
/// transport-neutral DTOs into a bounded channel. The whole type touches preview-only contract
/// members, so it is only constructed behind
/// <see cref="ExtensionHostApiSupport.ServiceLogFeedAvailable" /> from a non-inlined caller.
/// </summary>
internal sealed class ControllerServiceLogFeed : IAsyncDisposable
{
    /// <summary>Maximum buffered entries per subscription before the feed ends itself.</summary>
    internal const int ChannelCapacity = 256;

    private readonly Channel<ControllerServiceLogFeedItem> _channel = Channel.CreateBounded<ControllerServiceLogFeedItem>(
        new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    private readonly Sink _sink;
    private IExtensionServiceLogSubscription? _subscription;

    private ControllerServiceLogFeed()
    {
        _sink = new Sink(this);
    }

    /// <summary>Gets the sink handed to the host subscribe call.</summary>
    internal IExtensionServiceLogSink LogSink => _sink;

    /// <summary>Gets the ordered entry stream; completes on host completion, overflow, or disposal.</summary>
    internal ChannelReader<ControllerServiceLogFeedItem> Reader => _channel.Reader;

    /// <summary>Creates an unattached feed; the caller attaches the accepted subscription.</summary>
    internal static ControllerServiceLogFeed Create() => new();

    /// <summary>Attaches the accepted host subscription so disposal releases it.</summary>
    internal void Attach(IExtensionServiceLogSubscription subscription) =>
        _subscription = subscription ?? throw new ArgumentNullException(nameof(subscription));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        if (_subscription is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            _subscription?.Dispose();
        }
    }

    private void Publish(ExtensionServiceLogEntry entry)
    {
        if (!_channel.Writer.TryWrite(MapEntry(entry)))
        {
            // Slow consumer: end the feed; the client reconnects and resumes with its last sequence.
            _channel.Writer.TryComplete();
        }
    }

    private void Complete() => _channel.Writer.TryComplete();

    private static ControllerServiceLogFeedItem MapEntry(ExtensionServiceLogEntry entry)
    {
        var hasData = !entry.Data.IsDefaultOrEmpty;
        var data = hasData ? entry.Data : ImmutableArray<byte>.Empty;
        var dto = new ControllerServiceLogEntryDto
        {
            Kind = entry.Kind switch
            {
                ExtensionServiceLogEntryKind.Output => "output",
                ExtensionServiceLogEntryKind.GenerationStarted => "generationStarted",
                ExtensionServiceLogEntryKind.ProcessExited => "processExited",
                ExtensionServiceLogEntryKind.StartupFailed => "startupFailed",
                ExtensionServiceLogEntryKind.CurrentState => "currentState",
                ExtensionServiceLogEntryKind.Gap => "gap",
                ExtensionServiceLogEntryKind.Termination => "termination",
                _ => "unknown"
            },
            Sequence = entry.Sequence,
            Timestamp = entry.Timestamp,
            Stream = entry.Stream switch
            {
                ExtensionServiceOutputStream.Stdout => "stdout",
                ExtensionServiceOutputStream.Stderr => "stderr",
                _ => null
            },
            Data = hasData ? Convert.ToBase64String(data.AsSpan()) : null,
            ProcessInstanceId = entry.ProcessInstanceId,
            AttemptNumber = entry.AttemptNumber,
            ProcessExitCode = entry.ProcessExitCode,
            LifecycleState = entry.LifecycleState?.ToString(),
            FailureStage = entry.FailureStage == ExtensionServiceFailureStage.None ? null : entry.FailureStage.ToString(),
            FailureCode = entry.FailureCode == ExtensionServiceFailureCode.None ? null : entry.FailureCode.ToString(),
            FailureReason = entry.FailureReason,
            FirstMissingSequence = entry.FirstMissingSequence,
            LastMissingSequence = entry.LastMissingSequence,
            TerminationReason = entry.TerminationReason switch
            {
                ExtensionServiceLogTerminationReason.ExtensionUnloaded => "extensionUnloaded",
                ExtensionServiceLogTerminationReason.ServiceDisabled => "serviceDisabled",
                ExtensionServiceLogTerminationReason.ServiceRemoved => "serviceRemoved",
                ExtensionServiceLogTerminationReason.HostShutdown => "hostShutdown",
                _ => null
            }
        };
        return new ControllerServiceLogFeedItem(dto, data);
    }

    private sealed class Sink(ControllerServiceLogFeed feed) : IExtensionServiceLogSink
    {
        public void OnEntry(ExtensionServiceLogEntry entry) => feed.Publish(entry);

        public void OnCompleted() => feed.Complete();
    }
}

/// <summary>One buffered feed entry: the serialized DTO plus the raw output bytes when present.</summary>
internal readonly struct ControllerServiceLogFeedItem
{
    internal ControllerServiceLogFeedItem(ControllerServiceLogEntryDto entry, ImmutableArray<byte> data)
    {
        Entry = entry;
        Data = data;
    }

    /// <summary>Gets the JSON representation (output bytes base64-encoded in <c>data</c>).</summary>
    internal ControllerServiceLogEntryDto Entry { get; }

    /// <summary>Gets the raw output bytes; empty for non-output entries.</summary>
    internal ImmutableArray<byte> Data { get; }
}
