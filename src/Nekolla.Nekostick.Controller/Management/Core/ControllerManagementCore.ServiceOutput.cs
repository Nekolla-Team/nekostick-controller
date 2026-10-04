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
            return ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponseBuilder.Unavailable(reason: "bridge_unavailable", message: "The host management bridge is unavailable."));
        }

        if (!HasFullConfigurationScope() || !ExtensionHostApiSupport.IsApi13Supported(_bridge.ApiVersion))
        {
            return ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponseBuilder.Unsupported(reason: "host_api_unsupported", message: "The host does not support service output streaming."));
        }

        if (_bridge is not IExtensionHostBridge14 bridge14 || !ExtensionHostApiSupport.IsApi14Supported(bridge14.ApiVersion))
        {
            return ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponseBuilder.Unsupported(reason: "host_api_unsupported", message: "The host does not support service output streaming."));
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
                ExtensionServiceOutputCode.NotFound => ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponseBuilder.NotFound(reason: "unknown_service", message: $"Service '{serviceId}' was not found.", parameter: serviceId.ToString())),
                ExtensionServiceOutputCode.NotRunning => ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponseBuilder.ServiceNotRunning(reason: "not_running", message: $"Service '{serviceId}' is not running and has no live output to stream.", parameter: serviceId.ToString())),
                ExtensionServiceOutputCode.Unsupported => ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponseBuilder.Unsupported(reason: "operation_not_supported", message: "The host does not support the requested service output stream.")),
                _ => ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponseBuilder.Unavailable(reason: "stream_open_failed", message: $"The host could not open a service output stream for service '{serviceId}'.", parameter: serviceId.ToString()))
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            return ControllerServiceOutputStreamResult.Rejected(ControllerManagementResponseBuilder.Unsupported(reason: "host_api_unsupported", message: "The host does not support the requested service output stream."));
        }
        catch (Exception exception)
        {
            return ControllerServiceOutputStreamResult.Rejected(UnexpectedFailure("GET", $"service {serviceId} output stream", exception));
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
            return ControllerServiceLogFeedResult.Rejected(ControllerManagementResponseBuilder.Unavailable(reason: "bridge_unavailable", message: "The host management bridge is unavailable."));
        }

        if (!HasFullConfigurationScope() || !ExtensionHostApiSupport.IsApi13Supported(_bridge.ApiVersion))
        {
            return ControllerServiceLogFeedResult.Rejected(ControllerManagementResponseBuilder.Unsupported(reason: "host_api_unsupported", message: "The host does not support service log feeds."));
        }

        if (_bridge is not IExtensionHostBridge14 bridge14 || !ExtensionHostApiSupport.IsApi14Supported(bridge14.ApiVersion) || !ExtensionHostApiSupport.ServiceLogFeedAvailable)
        {
            return ControllerServiceLogFeedResult.Rejected(ControllerManagementResponseBuilder.Unsupported(reason: "host_api_unsupported", message: "The host does not support service log feeds."));
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
            return ControllerServiceLogFeedResult.Rejected(ControllerManagementResponseBuilder.Unsupported(reason: "host_api_unsupported", message: "The host does not support the requested service log feed."));
        }
        catch (Exception exception)
        {
            return ControllerServiceLogFeedResult.Rejected(UnexpectedFailure("GET", $"service {serviceId} log feed", exception));
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
                ExtensionServiceLogCode.NotFound => ControllerServiceLogFeedResult.Rejected(ControllerManagementResponseBuilder.NotFound(reason: "unknown_service", message: $"Service '{serviceId}' was not found.", parameter: serviceId.ToString())),
                ExtensionServiceLogCode.InvalidArgument or ExtensionServiceLogCode.InvalidCursor => ControllerServiceLogFeedResult.Rejected(ControllerManagementResponseBuilder.InvalidRequest("invalid_argument", "The 'sinceSequence' parameter must be a non-negative log sequence number.", "sinceSequence")),
                ExtensionServiceLogCode.Unsupported => ControllerServiceLogFeedResult.Rejected(ControllerManagementResponseBuilder.Unsupported(reason: "operation_not_supported", message: "The host does not support the requested service log feed.")),
                _ => ControllerServiceLogFeedResult.Rejected(ControllerManagementResponseBuilder.Unavailable(reason: "stream_open_failed", message: $"The host could not open a service log feed for service '{serviceId}'.", parameter: serviceId.ToString()))
            };
        }

        feed.Attach(result.Subscription);
        // The dispatcher attaches the session-lifetime token; the open token is not it.
        return ControllerServiceLogFeedResult.Opened(feed, CancellationToken.None);
    }
}
