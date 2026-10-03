namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Result of opening one service-log feed subscription.</summary>
public sealed class ControllerServiceLogFeedResult
{
    private ControllerServiceLogFeedResult(
        ControllerServiceLogFeed? feed,
        ControllerManagementResponse? rejection,
        CancellationToken sessionEnded)
    {
        Feed = feed;
        Rejection = rejection;
        SessionEnded = sessionEnded;
    }

    /// <summary>Gets the caller-owned feed when the subscribe succeeded.</summary>
    internal ControllerServiceLogFeed? Feed { get; }

    /// <summary>Gets the canonical rejection envelope when the subscribe failed.</summary>
    public ControllerManagementResponse? Rejection { get; }

    /// <summary>
    /// Gets the token that fires when the admitting session ends (dispatcher stop or API-key
    /// rotation). The transport must link it so a revoked session stops streaming promptly.
    /// </summary>
    public CancellationToken SessionEnded { get; }

    /// <summary>Creates a successful result over the accepted feed.</summary>
    internal static ControllerServiceLogFeedResult Opened(
        ControllerServiceLogFeed feed,
        CancellationToken sessionEnded = default) =>
        new(feed ?? throw new ArgumentNullException(nameof(feed)), null, sessionEnded);

    /// <summary>Creates a rejected result over a canonical management response.</summary>
    public static ControllerServiceLogFeedResult Rejected(ControllerManagementResponse rejection) =>
        new(null, rejection ?? throw new ArgumentNullException(nameof(rejection)), CancellationToken.None);
}
