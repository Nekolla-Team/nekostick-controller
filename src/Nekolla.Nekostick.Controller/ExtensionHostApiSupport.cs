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
    internal static readonly HostApiVersion Api13MinimumHostVersion = new(1, 3, 2);

    /// <summary>Determines whether the negotiated host exposes the API 1.3 sibling bridge.</summary>
    /// <param name="host">The negotiated host API version.</param>
    /// <returns><see langword="true" /> only for a compatible API 1.3.2-or-later host in major generation 1.</returns>
    internal static bool IsApi13Supported(HostApiVersion host) =>
        ExtensionAbi.IsCompatible(Api13MinimumHostVersion, host);
}
