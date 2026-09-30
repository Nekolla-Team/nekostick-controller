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
}
