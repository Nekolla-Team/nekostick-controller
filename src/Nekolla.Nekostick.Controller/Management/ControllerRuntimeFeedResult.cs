namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Result of opening one consumer view over the node-local service runtime-state feed.</summary>
public sealed class ControllerRuntimeFeedResult
{
    private ControllerRuntimeFeedResult(
        ControllerRuntimeStateFeed.View? view,
        ControllerManagementResponse? rejection,
        CancellationToken sessionEnded)
    {
        View = view;
        Rejection = rejection;
        SessionEnded = sessionEnded;
    }

    /// <summary>Gets the caller-owned feed view when the subscribe succeeded.</summary>
    internal ControllerRuntimeStateFeed.View? View { get; }

    /// <summary>Gets the canonical rejection envelope when the subscribe failed.</summary>
    public ControllerManagementResponse? Rejection { get; }

    /// <summary>
    /// Gets the token that fires when the admitting session ends (dispatcher stop or API-key
    /// rotation). The transport must link it so a revoked session stops streaming promptly.
    /// </summary>
    public CancellationToken SessionEnded { get; }

    /// <summary>Creates a successful result over the subscribed feed view.</summary>
    internal static ControllerRuntimeFeedResult Opened(
        ControllerRuntimeStateFeed.View view,
        CancellationToken sessionEnded = default) =>
        new(view ?? throw new ArgumentNullException(nameof(view)), null, sessionEnded);

    /// <summary>Creates a rejected result over a canonical management response.</summary>
    public static ControllerRuntimeFeedResult Rejected(ControllerManagementResponse rejection) =>
        new(null, rejection ?? throw new ArgumentNullException(nameof(rejection)), CancellationToken.None);
}
