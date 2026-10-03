using System.Runtime.CompilerServices;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Live service-output open operation backing the WebSocket transport endpoint.</summary>
internal sealed partial class ControllerManagementCore
{
    /// <summary>
    /// Opens one live service-output stream on the Host API 1.4 bridge. The returned stream is
    /// caller-owned; disposing it detaches the Host subscription. Reads are not gated on the
    /// mutation gate because the operation is read-only and long-lived by design.
    /// </summary>
    internal async ValueTask<ControllerServiceOutputStreamResult> OpenServiceOutputStreamAsync(
        Guid serviceId,
        ExtensionServiceOutputStream stream,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_bridge is null)
        {
            return ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponseBuilder.Unavailable);
        }

        if (!HasFullConfigurationScope() || !ExtensionHostApiSupport.IsApi13Supported(_bridge.ApiVersion))
        {
            return ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponseBuilder.Unsupported);
        }

        if (_bridge is not IExtensionHostBridge14 bridge14 ||
            !ExtensionHostApiSupport.IsApi14Supported(bridge14.ApiVersion))
        {
            return ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponseBuilder.Unsupported);
        }

        try
        {
            var result = await bridge14.ServiceOutput
                .OpenStreamAsync(serviceId, stream, cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded && result.Stream is { } output)
            {
                // The dispatcher attaches the session-lifetime token; the open token is not it.
                return ControllerServiceOutputStreamResult.Opened(output, CancellationToken.None);
            }

            return result.Code switch
            {
                ExtensionServiceOutputCode.NotFound => ControllerServiceOutputStreamResult.Rejected(
                    ControllerManagementResponseBuilder.NotFound),
                ExtensionServiceOutputCode.NotRunning => ControllerServiceOutputStreamResult.Rejected(
                    ControllerManagementResponseBuilder.ServiceNotRunning),
                ExtensionServiceOutputCode.Unsupported => ControllerServiceOutputStreamResult.Rejected(
                    ControllerManagementResponseBuilder.Unsupported),
                _ => ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponseBuilder.Unavailable)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            return ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponseBuilder.Unsupported);
        }
        catch (Exception exception)
        {
            LogDispatchFailure("GET", $"service {serviceId} output stream", exception);
            return ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponseBuilder.Unavailable);
        }
    }

    /// <summary>
    /// Opens one cross-generation service-log feed on the Host API 1.4 bridge (Contracts preview
    /// log API). The returned feed is caller-owned; disposing it detaches the Host subscription.
    /// Rejections carrying <see cref="ControllerDispatchCode.Unsupported" /> mean the host lacks
    /// the log API and callers should fall back to the single-generation output stream.
    /// </summary>
    internal async ValueTask<ControllerServiceLogFeedResult> OpenServiceLogFeedAsync(
        Guid serviceId,
        long? sinceSequence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_bridge is null)
        {
            return ControllerServiceLogFeedResult.Rejected(ControllerManagementResponseBuilder.Unavailable);
        }

        if (!HasFullConfigurationScope() || !ExtensionHostApiSupport.IsApi13Supported(_bridge.ApiVersion))
        {
            return ControllerServiceLogFeedResult.Rejected(ControllerManagementResponseBuilder.Unsupported);
        }

        if (_bridge is not IExtensionHostBridge14 bridge14 ||
            !ExtensionHostApiSupport.IsApi14Supported(bridge14.ApiVersion) ||
            !ExtensionHostApiSupport.ServiceLogFeedAvailable)
        {
            return ControllerServiceLogFeedResult.Rejected(ControllerManagementResponseBuilder.Unsupported);
        }

        try
        {
            return await SubscribeServiceLogFeedAsync(bridge14, serviceId, sinceSequence, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            return ControllerServiceLogFeedResult.Rejected(ControllerManagementResponseBuilder.Unsupported);
        }
        catch (Exception exception)
        {
            LogDispatchFailure("GET", $"service {serviceId} log feed", exception);
            return ControllerServiceLogFeedResult.Rejected(ControllerManagementResponseBuilder.Unavailable);
        }
    }

    // Non-inlined: touches preview-only contract members; only reachable behind ServiceLogFeedAvailable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async ValueTask<ControllerServiceLogFeedResult> SubscribeServiceLogFeedAsync(
        IExtensionHostBridge14 bridge,
        Guid serviceId,
        long? sinceSequence,
        CancellationToken cancellationToken)
    {
        var feed = ControllerServiceLogFeed.Create();
        var result = await bridge.ServiceOutput
            .SubscribeAsync(serviceId, feed.LogSink, sinceSequence, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded || result.Subscription is null)
        {
            return result.Code switch
            {
                ExtensionServiceLogCode.NotFound => ControllerServiceLogFeedResult.Rejected(
                    ControllerManagementResponseBuilder.NotFound),
                ExtensionServiceLogCode.InvalidArgument or ExtensionServiceLogCode.InvalidCursor =>
                    ControllerServiceLogFeedResult.Rejected(ControllerManagementResponseBuilder.InvalidRequest),
                ExtensionServiceLogCode.Unsupported => ControllerServiceLogFeedResult.Rejected(
                    ControllerManagementResponseBuilder.Unsupported),
                _ => ControllerServiceLogFeedResult.Rejected(ControllerManagementResponseBuilder.Unavailable)
            };
        }

        feed.Attach(result.Subscription);
        // The dispatcher attaches the session-lifetime token; the open token is not it.
        return ControllerServiceLogFeedResult.Opened(feed, CancellationToken.None);
    }
}
