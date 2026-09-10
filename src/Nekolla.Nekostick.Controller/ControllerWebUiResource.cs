
namespace Nekolla.Nekostick.Controller;

/// <summary>Provides fresh streams for the optional embedded Web UI resource.</summary>
internal static class ControllerWebUiResource
{
    internal const string LogicalName = "Nekolla.Nekostick.Controller.WebUi.index.html";

    private static readonly Func<Stream?> AssemblyStreamFactory = OpenAssemblyResource;
    private static Func<Stream?> _streamFactory = AssemblyStreamFactory;

    /// <summary>Gets or sets the stream factory used by resource lookups.</summary>
    /// <remarks>The setter exists for isolated tests and must return a new readable stream per call.</remarks>
    internal static Func<Stream?> StreamFactory
    {
        get => Volatile.Read(ref _streamFactory);
        set => Volatile.Write(ref _streamFactory, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Gets whether the embedded resource is available in the controller assembly.</summary>
    internal static bool IsEmbedded => Length is not null;

    /// <summary>Gets the embedded resource length, or <see langword="null" /> when absent.</summary>
    internal static long? Length
    {
        get
        {
            using var stream = OpenRead();
            return stream?.Length;
        }
    }

    /// <summary>Opens a new stream for the embedded resource, or returns null when it is absent.</summary>
    internal static Stream? OpenRead() => Volatile.Read(ref _streamFactory)();

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

    private static Stream? OpenAssemblyResource() =>
        typeof(ControllerWebUiResource).Assembly.GetManifestResourceStream(LogicalName);
}
