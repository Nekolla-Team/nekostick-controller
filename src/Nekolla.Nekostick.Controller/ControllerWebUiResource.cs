namespace Nekolla.Nekostick.Controller;

/// <summary>Provides fresh streams for the optional embedded Web UI resources.</summary>
internal static class ControllerWebUiResource
{
    /// <summary>Gets the embedded logical name of the Web UI shell.</summary>
    internal const string LogicalName = "Nekolla.Nekostick.Controller.WebUi.index.html";

    /// <summary>Gets the cache policy for Web UI assets, whose file names carry a content hash.</summary>
    internal const string AssetCacheControl = "public, max-age=31536000, immutable";

    private const string AssetLogicalNamePrefix = "Nekolla.Nekostick.Controller.WebUi.assets.";
    private const int MaximumAssetNameLength = 128;

    private static readonly Func<string, Stream?> AssemblyStreamFactory = OpenAssemblyResource;
    private static Func<string, Stream?> _streamFactory = AssemblyStreamFactory;

    /// <summary>Gets or sets the factory that opens one embedded resource by logical name.</summary>
    /// <remarks>The setter exists for isolated tests and must return a new readable stream per call.</remarks>
    internal static Func<string, Stream?> StreamFactory
    {
        get => Volatile.Read(ref _streamFactory);
        set => Volatile.Write(ref _streamFactory, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Gets whether the embedded shell resource is available in the controller assembly.</summary>
    internal static bool IsEmbedded => Length is not null;

    /// <summary>Gets the embedded shell resource length, or <see langword="null" /> when absent.</summary>
    internal static long? Length
    {
        get
        {
            using var stream = OpenRead();
            return stream?.Length;
        }
    }

    /// <summary>Opens a new stream for the embedded shell, or returns null when it is absent.</summary>
    internal static Stream? OpenRead() => Volatile.Read(ref _streamFactory)(LogicalName);

    /// <summary>Opens a new stream for one embedded Web UI asset, or returns null when it is absent.</summary>
    /// <param name="fileName">The validated asset file name.</param>
    internal static Stream? OpenAsset(string fileName) =>
        Volatile.Read(ref _streamFactory)(AssetLogicalName(fileName));

    /// <summary>Gets the embedded logical name of one Web UI asset file.</summary>
    /// <param name="fileName">The asset file name.</param>
    internal static string AssetLogicalName(string fileName) => AssetLogicalNamePrefix + fileName;

    /// <summary>Restores assembly resource lookup after a test override.</summary>
    internal static void ResetStreamFactory() => Volatile.Write(ref _streamFactory, AssemblyStreamFactory);

    /// <summary>Determines whether a request path is exactly the controller listener root.</summary>
    internal static bool IsRootPath(string path) =>
        string.Equals(path, "/", StringComparison.Ordinal);

    /// <summary>Determines whether a request path is the configured HostRoute root.</summary>
    /// <remarks>The HostRoute root accepts its exact path and one trailing slash.</remarks>
    internal static bool IsHostRouteRootPath(string path, string hostRoutePath)
    {
        if (string.Equals(path, hostRoutePath, StringComparison.Ordinal))
        {
            return true;
        }

        return path.Length == hostRoutePath.Length + 1 &&
            path[^1] == '/' &&
            path.StartsWith(hostRoutePath, StringComparison.Ordinal);
    }

    /// <summary>Extracts the asset file name when a request path addresses one file beside the shell.</summary>
    /// <param name="path">The absolute request path.</param>
    /// <param name="hostRoutePath">The configured HostRoute prefix, or <see langword="null" /> for a listener root.</param>
    /// <param name="fileName">The accepted file name, or an empty string when the path is not an asset path.</param>
    /// <returns><see langword="true" /> when the path is shaped like one Web UI asset file.</returns>
    /// <remarks>
    /// Only a single, characters-restricted segment is accepted, so the name can never address a
    /// different embedded resource or traverse out of the Web UI namespace.
    /// </remarks>
    internal static bool TryGetAssetFileName(string path, string? hostRoutePath, out string fileName)
    {
        fileName = string.Empty;
        ReadOnlySpan<char> remainder;
        if (hostRoutePath is null)
        {
            if (path.Length < 2 || path[0] != '/')
            {
                return false;
            }

            remainder = path.AsSpan(1);
        }
        else
        {
            if (path.Length <= hostRoutePath.Length + 1 ||
                path[hostRoutePath.Length] != '/' ||
                !path.StartsWith(hostRoutePath, StringComparison.Ordinal))
            {
                return false;
            }

            remainder = path.AsSpan(hostRoutePath.Length + 1);
        }

        if (remainder.IsEmpty || remainder.Length > MaximumAssetNameLength)
        {
            return false;
        }

        foreach (var character in remainder)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))
            {
                return false;
            }
        }

        // A file extension keeps API paths such as /v1 out of the asset namespace.
        if (Path.GetExtension(remainder).Length < 2)
        {
            return false;
        }

        fileName = remainder.ToString();
        return true;
    }

    /// <summary>Maps an embedded asset file name to its response content type.</summary>
    /// <param name="fileName">The asset file name.</param>
    internal static string ContentTypeFor(string fileName) =>
        Path.GetExtension(fileName) switch
        {
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" or ".map" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".ttf" => "font/ttf",
            _ => "application/octet-stream"
        };

    private static Stream? OpenAssemblyResource(string logicalName) =>
        typeof(ControllerWebUiResource).Assembly.GetManifestResourceStream(logicalName);
}
