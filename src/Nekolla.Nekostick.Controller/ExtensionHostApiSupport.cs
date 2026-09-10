using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller;

/// <summary>
/// Declares the caller-side host API capability checks for this extension. The Contracts package
/// intentionally ships no per-release helper members; each caller declares the host version that
/// introduced the capability it requires and checks it via <see cref="ExtensionAbi.IsCompatible" />.
/// </summary>
internal static class ExtensionHostApiSupport
{
    /// <summary>Gets the minimum host API version that exposes the API 1.3 sibling bridge.</summary>
    internal static readonly HostApiVersion Api13MinimumHostVersion = new(1, 3, 1);
    /// <summary>Gets the minimum host API version that exposes streaming extension handlers.</summary>
    internal static readonly HostApiVersion StreamingMinimumHostVersion = new(1, 3, 2);

    /// <summary>Gets the minimum host API version that exposes API 1.3.3 node-local lifecycle and host-info members.</summary>
    internal static readonly HostApiVersion Api133MinimumHostVersion = new(1, 3, 3);

    /// <summary>Gets the minimum host API version whose refresh summaries expose skipped scan directories.</summary>
    internal static readonly HostApiVersion Api134MinimumHostVersion = new(1, 3, 4);

    /// <summary>Determines whether the negotiated host exposes streaming extension handlers.</summary>
    /// <param name="host">The negotiated host API version.</param>
    /// <returns><see langword="true" /> only for a compatible API 1.3.2-or-later host in major generation 1.</returns>
    internal static bool IsStreamingSupported(HostApiVersion host) =>
        ExtensionAbi.IsCompatible(StreamingMinimumHostVersion, host);

    /// <summary>Determines whether the negotiated host exposes API 1.3.3 node-local lifecycle and host-info members.</summary>
    /// <param name="host">The negotiated host API version.</param>
    /// <returns><see langword="true" /> only for a compatible API 1.3.3-or-later host in major generation 1.</returns>
    internal static bool IsApi133Supported(HostApiVersion host) =>
        ExtensionAbi.IsCompatible(Api133MinimumHostVersion, host);

    /// <summary>Determines whether the negotiated host exposes skipped scan directories in refresh summaries.</summary>
    /// <param name="host">The negotiated host API version.</param>
    /// <returns><see langword="true" /> only for a compatible API 1.3.4-or-later host in major generation 1.</returns>
    internal static bool IsApi134Supported(HostApiVersion host) =>
        ExtensionAbi.IsCompatible(Api134MinimumHostVersion, host);

    /// <summary>Determines whether the negotiated host exposes the API 1.3 sibling bridge.</summary>
    /// <param name="host">The negotiated host API version.</param>
    /// <returns><see langword="true" /> only for a compatible API 1.3.1-or-later host in major generation 1.</returns>
    internal static bool IsApi13Supported(HostApiVersion host) =>
        ExtensionAbi.IsCompatible(Api13MinimumHostVersion, host);
}
