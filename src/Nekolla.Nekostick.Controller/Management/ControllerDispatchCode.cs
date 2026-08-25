namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Identifies safe results from the shared management dispatcher.</summary>
public enum ControllerDispatchCode
{
    /// <summary>The request was accepted by a later management operation.</summary>
    Success,

    /// <summary>The request did not contain valid transport-neutral data.</summary>
    InvalidRequest,

    /// <summary>The configured API key was absent or did not match.</summary>
    Unauthorized,

    /// <summary>The selected management transport is disabled.</summary>
    TransportDisabled,

    /// <summary>The requested management resource or operation was not found.</summary>
    NotFound,

    /// <summary>The request version conflicts with current controller state.</summary>
    Conflict,

    /// <summary>The requested capability is not supported by the upstream contract.</summary>
    Unsupported,

    /// <summary>The controller is not accepting requests.</summary>
    Unavailable
}
