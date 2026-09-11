using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Outcome of one extension package install attempt.</summary>
internal enum ControllerExtensionInstallOutcome
{
    /// <summary>The package was installed or fully replaced the previous install.</summary>
    Installed,

    /// <summary>The uploaded package is not a readable zip with a valid root manifest.</summary>
    InvalidPackage,

    /// <summary>The installed version is newer than the uploaded package.</summary>
    DowngradeForbidden,

    /// <summary>The extensions root could not be resolved or written.</summary>
    StorageUnavailable
}

/// <summary>Result of one extension package install attempt.</summary>
/// <param name="Outcome">The install outcome.</param>
/// <param name="Id">The manifest identifier when the package parsed.</param>
/// <param name="Version">The manifest version when the package parsed.</param>
/// <param name="Replaced">Whether an existing extension directory was fully replaced.</param>
/// <param name="RestoreSucceeded">
/// When the install failed after the previous directory had been moved aside, whether restoring
/// that backup succeeded; <see langword="null" /> when no restore step was attempted.
/// </param>
internal sealed record ControllerExtensionInstallResult(
    ControllerExtensionInstallOutcome Outcome,
    string? Id,
    string? Version,
    bool Replaced,
    bool? RestoreSucceeded);

/// <summary>
/// Installs uploaded extension zip packages into the host extensions root. The root is derived
/// from the controller assembly location: the host loads every extension from
/// <c>&lt;extensionsRoot&gt;/&lt;id&gt;/</c>, so the parent of the controller's own directory is
/// the scan root regardless of host configuration. Extraction stages into a sibling directory;
/// an existing install is moved to a <c>&lt;id&gt;.bak</c> sibling before the new directory moves
/// into place, the backup is deleted on success, and a failed swap restores the backup.
/// </summary>
internal static class ControllerExtensionInstaller
{
    /// <summary>The maximum accepted package size; larger uploads are rejected while streaming.</summary>
    internal const long MaximumPackageBytes = 64L * 1024 * 1024;

    /// <summary>The maximum total uncompressed payload; a guard against zip bombs.</summary>
    private const long MaximumExtractedBytes = 256L * 1024 * 1024;

    private const string ManifestFileName = "manifest.json";

    private const string BackupSuffix = ".bak";

    private static Func<string?>? _rootPathOverride;

    /// <summary>Overrides the extensions root for tests; pass <see langword="null" /> to restore derivation.</summary>
    internal static void SetRootPathOverride(Func<string?>? rootPathOverride) => _rootPathOverride = rootPathOverride;

    /// <summary>Streams one uploaded package to disk and installs it under the extensions root.</summary>
    /// <param name="package">The readable request body stream carrying the zip package.</param>
    /// <param name="cancellationToken">Cancels the copy and extraction.</param>
    internal static async ValueTask<ControllerExtensionInstallResult> InstallAsync(
        Stream package,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (ResolveExtensionsRoot() is not { } root)
        {
            return new ControllerExtensionInstallResult(ControllerExtensionInstallOutcome.StorageUnavailable, null, null, false, null);
        }

        var stagingPath = Path.Combine(Path.GetTempPath(), "nekostick-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (!await TryCopyBoundedAsync(package, stagingPath, cancellationToken).ConfigureAwait(false))
            {
                return new ControllerExtensionInstallResult(ControllerExtensionInstallOutcome.InvalidPackage, null, null, false, null);
            }

            using var archive = OpenArchive(stagingPath);
            if (archive is null)
            {
                return new ControllerExtensionInstallResult(ControllerExtensionInstallOutcome.InvalidPackage, null, null, false, null);
            }

            if (!TryReadManifest(archive, out var id, out var version))
            {
                return new ControllerExtensionInstallResult(ControllerExtensionInstallOutcome.InvalidPackage, null, null, false, null);
            }

            var targetPath = Path.Combine(root, id);
            if (Directory.Exists(targetPath) &&
                TryReadInstalledVersion(targetPath, out var installedVersion) &&
                CompareVersions(installedVersion, version) > 0)
            {
                return new ControllerExtensionInstallResult(ControllerExtensionInstallOutcome.DowngradeForbidden, id, version, false, null);
            }

            var extractOutcome = TryExtract(archive, root, targetPath, out var replaced, out var restoreSucceeded);
            if (extractOutcome != ControllerExtensionInstallOutcome.Installed)
            {
                return new ControllerExtensionInstallResult(extractOutcome, id, version, false, restoreSucceeded);
            }

            return new ControllerExtensionInstallResult(ControllerExtensionInstallOutcome.Installed, id, version, replaced, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            return new ControllerExtensionInstallResult(ControllerExtensionInstallOutcome.StorageUnavailable, null, null, false, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new ControllerExtensionInstallResult(ControllerExtensionInstallOutcome.StorageUnavailable, null, null, false, null);
        }
        finally
        {
            try
            {
                if (File.Exists(stagingPath))
                {
                    File.Delete(stagingPath);
                }
            }
            catch (IOException)
            {
                // Best-effort staging cleanup; the temp directory reaper owns the residue.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string? ResolveExtensionsRoot()
    {
        if (_rootPathOverride is { } rootOverride)
        {
            return rootOverride();
        }

        var location = typeof(ControllerEntrypoint).Assembly.Location;
        if (string.IsNullOrEmpty(location))
        {
            return null;
        }

        var extensionDirectory = Path.GetDirectoryName(location);
        return extensionDirectory is null ? null : Path.GetDirectoryName(extensionDirectory);
    }

    private static async ValueTask<bool> TryCopyBoundedAsync(Stream package, string stagingPath, CancellationToken cancellationToken)
    {
        await using var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await package.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return true;
            }

            total += read;
            if (total > MaximumPackageBytes)
            {
                return false;
            }

            await staging.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static ZipArchive? OpenArchive(string stagingPath)
    {
        try
        {
            return new ZipArchive(File.OpenRead(stagingPath), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static bool TryReadManifest(ZipArchive archive, out string id, out string version)
    {
        id = string.Empty;
        version = string.Empty;
        var entry = archive.GetEntry(ManifestFileName);
        if (entry is null || entry.Length > ControllerAdmissionLimits.MaximumRequestBodyBytes)
        {
            return false;
        }

        try
        {
            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String ||
                !document.RootElement.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            id = idElement.GetString() ?? string.Empty;
            version = versionElement.GetString() ?? string.Empty;
            return IsValidExtensionId(id) && TrySplitVersion(version, out _, out _);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsValidExtensionId(string value)
    {
        // Mirrors the host manifest identifier syntax: dot/dash separated lowercase alphanumeric
        // segments, at most 128 characters, no empty segments.
        if (string.IsNullOrEmpty(value) || value.Length > 128)
        {
            return false;
        }

        var segmentLength = 0;
        foreach (var character in value)
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                segmentLength++;
                continue;
            }

            if (character is '.' or '-')
            {
                if (segmentLength == 0)
                {
                    return false;
                }

                segmentLength = 0;
                continue;
            }

            return false;
        }

        return segmentLength > 0;
    }

    private static bool TryReadInstalledVersion(string targetPath, out string version)
    {
        version = string.Empty;
        try
        {
            var manifestPath = Path.Combine(targetPath, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.String ||
                versionElement.GetString() is not { } parsed ||
                !TrySplitVersion(parsed, out _, out _))
            {
                return false;
            }

            version = parsed;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ControllerExtensionInstallOutcome TryExtract(
        ZipArchive archive,
        string root,
        string targetPath,
        out bool replaced,
        out bool? restoreSucceeded)
    {
        replaced = false;
        restoreSucceeded = null;
        var stagingDirectory = Path.Combine(root, ".install-" + Guid.NewGuid().ToString("N"));
        try
        {
            long extractedTotal = 0;
            var stagingRoot = Path.GetFullPath(stagingDirectory);
            foreach (var entry in archive.Entries)
            {
                var relativePath = entry.FullName.Replace('\\', '/');
                if (relativePath.EndsWith('/'))
                {
                    continue;
                }

                var destination = Path.GetFullPath(Path.Combine(stagingRoot, relativePath));
                if (!destination.StartsWith(stagingRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    return ControllerExtensionInstallOutcome.InvalidPackage;
                }

                extractedTotal += entry.Length;
                if (extractedTotal > MaximumExtractedBytes)
                {
                    return ControllerExtensionInstallOutcome.InvalidPackage;
                }

                var parent = Path.GetDirectoryName(destination);
                if (parent is not null)
                {
                    Directory.CreateDirectory(parent);
                }

                entry.ExtractToFile(destination, overwrite: false);
            }

            replaced = Directory.Exists(targetPath);
            string? backupPath = null;
            if (replaced)
            {
                backupPath = targetPath + BackupSuffix;
                try
                {
                    // Clear residue from an earlier interrupted install so the backup rename succeeds.
                    if (Directory.Exists(backupPath))
                    {
                        Directory.Delete(backupPath, recursive: true);
                    }
                    else if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }

                    Directory.Move(targetPath, backupPath);
                }
                catch (Exception)
                {
                    // The backup rename failed before anything moved, so the previous installation
                    // is still in place and no restore step is needed.
                    return ControllerExtensionInstallOutcome.StorageUnavailable;
                }
            }

            try
            {
                Directory.Move(stagingDirectory, targetPath);
            }
            catch (Exception)
            {
                if (backupPath is not null)
                {
                    restoreSucceeded = TryRestoreBackup(backupPath, targetPath);
                }

                return ControllerExtensionInstallOutcome.StorageUnavailable;
            }

            if (backupPath is not null)
            {
                TryDeleteBackup(backupPath);
            }

            return ControllerExtensionInstallOutcome.Installed;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort staging cleanup.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool TryRestoreBackup(string backupPath, string targetPath)
    {
        try
        {
            if (Directory.Exists(targetPath))
            {
                Directory.Delete(targetPath, recursive: true);
            }

            Directory.Move(backupPath, targetPath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDeleteBackup(string backupPath)
    {
        try
        {
            if (Directory.Exists(backupPath))
            {
                Directory.Delete(backupPath, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort backup cleanup; the residue is overwritten by the next install.
        }
    }

    /// <summary>Compares two semantic versions; returns negative/zero/positive like CompareTo.</summary>
    internal static int CompareVersions(string left, string right)
    {
        TrySplitVersion(left, out var leftCore, out var leftPrerelease);
        TrySplitVersion(right, out var rightCore, out var rightPrerelease);
        for (var index = 0; index < 3; index++)
        {
            var comparison = leftCore[index].CompareTo(rightCore[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        // A prerelease is older than the same core without one; identifiers compare per semver:
        // numeric identifiers below alphanumeric, numeric compared numerically, shorter set first.
        if (leftPrerelease.Length == 0 || rightPrerelease.Length == 0)
        {
            return leftPrerelease.Length == rightPrerelease.Length ? 0 : leftPrerelease.Length == 0 ? 1 : -1;
        }

        var leftParts = leftPrerelease.Split('.');
        var rightParts = rightPrerelease.Split('.');
        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            if (index >= leftParts.Length)
            {
                return -1;
            }

            if (index >= rightParts.Length)
            {
                return 1;
            }

            var leftNumeric = long.TryParse(leftParts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = long.TryParse(rightParts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            var comparison = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(leftParts[index], rightParts[index])
            };
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static bool TrySplitVersion(string value, out long[] core, out string prerelease)
    {
        core = new long[3];
        prerelease = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var withoutBuild = value.Split('+', 2)[0];
        var parts = withoutBuild.Split('-', 2);
        var coreParts = parts[0].Split('.');
        if (coreParts.Length is < 1 or > 3)
        {
            return false;
        }

        for (var index = 0; index < coreParts.Length; index++)
        {
            if (!long.TryParse(coreParts[index], NumberStyles.None, CultureInfo.InvariantCulture, out core[index]))
            {
                return false;
            }
        }

        prerelease = parts.Length == 2 ? parts[1] : string.Empty;
        return true;
    }
}
