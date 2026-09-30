namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Carries either one opened live service-output stream or the canonical rejection envelope.</summary>
/// <remarks>
/// The stream is caller-owned: the transport that accepted the upgrade disposes it when the
/// WebSocket session ends, which detaches the underlying Host subscription.
/// </remarks>
public sealed class ControllerServiceOutputStreamResult
{
    private ControllerServiceOutputStreamResult(
        Stream? stream,
        ControllerManagementResponse? rejection,
        CancellationToken sessionEnded)
    {
        Stream = stream;
        Rejection = rejection;
        SessionEnded = sessionEnded;
    }

    /// <summary>Gets the caller-owned live output stream when the open succeeded.</summary>
    public Stream? Stream { get; }

    /// <summary>Gets the canonical rejection envelope when the open failed.</summary>
    public ControllerManagementResponse? Rejection { get; }

    /// <summary>
    /// Gets the token that fires when the admitting session ends (dispatcher stop or API-key
    /// rotation). The transport must link it so a revoked session stops streaming promptly.
    /// </summary>
    public CancellationToken SessionEnded { get; }

    /// <summary>Creates a successful result over the opened stream.</summary>
    /// <param name="stream">The readable live output stream.</param>
    /// <param name="sessionEnded">Fires when the admitting session ends and the stream must close.</param>
    public static ControllerServiceOutputStreamResult Opened(Stream stream, CancellationToken sessionEnded = default) =>
        new(stream ?? throw new ArgumentNullException(nameof(stream)), null, sessionEnded);

    /// <summary>Creates a rejected result over a canonical management response.</summary>
    /// <param name="rejection">The envelope describing why the stream was not opened.</param>
    public static ControllerServiceOutputStreamResult Rejected(ControllerManagementResponse rejection) =>
        new(null, rejection ?? throw new ArgumentNullException(nameof(rejection)), default);
}
