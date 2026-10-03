using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>
/// Maintains a node-local mirror of host service runtime state by subscribing to the host's
/// runtime-state feed (Contracts members added after the API 1.4.0 baseline). Late consumers receive
/// the full cached state on subscribe and ordered changes afterwards. All contract-new types are only
/// touched behind <see cref="ExtensionHostApiSupport.ServiceRuntimeStateFeedAvailable" /> and
/// NoInlining boundaries so the controller still loads against older Contracts assemblies.
/// </summary>
internal sealed class ControllerRuntimeStateFeed : IAsyncDisposable
{
    private const int SubscriberChannelCapacity = 256;

    private readonly ConcurrentDictionary<Guid, ExtensionServiceRuntimeStateChange> _latest = new();
    private readonly List<Channel<ExtensionServiceRuntimeStateChange>> _subscribers = [];
    private readonly object _gate = new();
    private IExtensionServiceRuntimeStateSubscription? _subscription;
    private bool _completed;

    private ControllerRuntimeStateFeed() { }

    /// <summary>
    /// Creates and starts a feed against the 1.4 bridge, or returns null when the host lacks the
    /// runtime-state capability. Rejection codes other than success leave no partially started feed.
    /// </summary>
    internal static async ValueTask<ControllerRuntimeStateFeed?> TryStartAsync(
        IExtensionHostBridge bridge,
        CancellationToken cancellationToken)
    {
        if (bridge is not IExtensionHostBridge14 bridge14 ||
            !ExtensionHostApiSupport.IsApi14Supported(bridge14.ApiVersion) ||
            !ExtensionHostApiSupport.ServiceRuntimeStateFeedAvailable)
        {
            return null;
        }

        var feed = new ControllerRuntimeStateFeed();
        ExtensionServiceRuntimeStateSubscriptionResult result;
        try
        {
            result = await feed.SubscribeApi14Async(bridge14, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        if (!result.Succeeded || result.Subscription is null)
        {
            return null;
        }

        feed._subscription = result.Subscription;
        return feed;
    }

    /// <summary>NoInlining: touches contract members that postdate the API 1.4.0 baseline.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask<ExtensionServiceRuntimeStateSubscriptionResult> SubscribeApi14Async(
        IExtensionHostBridge14 bridge14,
        CancellationToken cancellationToken) =>
        bridge14.ServiceRuntimeState.SubscribeStatesAsync(new Sink(this), cancellationToken);

    /// <summary>
    /// Captures the current state and registers a live channel. Consumers must dispose the returned
    /// view exactly once. Writes that outrun a slow consumer terminate that consumer's channel so it
    /// resubscribes and replays instead of silently missing removals.
    /// </summary>
    internal View Subscribe()
    {
        var channel = Channel.CreateBounded<ExtensionServiceRuntimeStateChange>(new BoundedChannelOptions(SubscriberChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        ExtensionServiceRuntimeStateChange[] snapshot;
        lock (_gate)
        {
            if (_completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _subscribers.Add(channel);
            }
            snapshot = [.. _latest.Values];
        }
        return new View(this, channel, snapshot);
    }

    private void Publish(ExtensionServiceRuntimeStateChange change)
    {
        if (change.Kind == ExtensionServiceRuntimeStateChangeKind.Removed)
        {
            _latest.TryRemove(change.ServiceId, out _);
        }
        else
        {
            _latest[change.ServiceId] = change;
        }

        lock (_gate)
        {
            for (var index = _subscribers.Count - 1; index >= 0; index--)
            {
                if (!_subscribers[index].Writer.TryWrite(change))
                {
                    _subscribers[index].Writer.TryComplete();
                    _subscribers.RemoveAt(index);
                }
            }
        }
    }

    private void Unsubscribe(Channel<ExtensionServiceRuntimeStateChange> channel)
    {
        lock (_gate)
        {
            _subscribers.Remove(channel);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _completed = true;
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }
            _subscribers.Clear();
        }

        if (_subscription is not null)
        {
            await _subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>NoInlining: the sink interface postdates the API 1.4.0 contract baseline.</summary>
    private sealed class Sink(ControllerRuntimeStateFeed feed) : IExtensionServiceRuntimeStateSink
    {
        public void OnStateChanged(ExtensionServiceRuntimeStateChange change)
        {
            feed.Publish(change);
        }
    }

    /// <summary>One consumer's captured state plus live change channel.</summary>
    internal sealed class View : IDisposable
    {
        private readonly ControllerRuntimeStateFeed _feed;
        private readonly Channel<ExtensionServiceRuntimeStateChange> _channel;

        internal View(
            ControllerRuntimeStateFeed feed,
            Channel<ExtensionServiceRuntimeStateChange> channel,
            ExtensionServiceRuntimeStateChange[] snapshot)
        {
            _feed = feed;
            _channel = channel;
            Snapshot = snapshot;
        }

        /// <summary>Gets the state captured at subscribe time.</summary>
        internal ExtensionServiceRuntimeStateChange[] Snapshot { get; }

        /// <summary>Gets the ordered live changes published after the snapshot.</summary>
        internal ChannelReader<ExtensionServiceRuntimeStateChange> Reader => _channel.Reader;

        public void Dispose() => _feed.Unsubscribe(_channel);
    }
}
