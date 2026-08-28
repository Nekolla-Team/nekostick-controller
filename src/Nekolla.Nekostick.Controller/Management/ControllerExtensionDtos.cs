using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Read representation of a registered extension.</summary>
public sealed class ControllerExtensionRecordReadDto
{
    /// <summary>Extension identifier.</summary>
    [JsonPropertyName("extensionId")] public string ExtensionId { get; init; } = string.Empty;
    /// <summary>Extension version.</summary>
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
    /// <summary>Current extension load state.</summary>
    [JsonPropertyName("loadState")] public ControllerExtensionLoadState LoadState { get; init; }
    /// <summary>Time at which the extension record was created.</summary>
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Time at which the extension record was last updated.</summary>
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Current version of the extension record.</summary>
    [JsonPropertyName("recordVersion")] public long RecordVersion { get; init; }
    /// <summary>Whether a loaded generation of the extension is currently running.</summary>
    [JsonPropertyName("isRunning")] public bool IsRunning { get; init; }
    /// <summary>Manifest version observed by the latest directory scan; null when the manifest is missing.</summary>
    [JsonPropertyName("manifestVersion")] public string? ManifestVersion { get; init; }
}

/// <summary>Read representation of an extension directory refresh summary.</summary>
public sealed class ControllerExtensionRefreshReadDto
{
    /// <summary>Extension identifiers added to the persistent records.</summary>
    [JsonPropertyName("added")] public ImmutableArray<string> Added { get; init; } = ImmutableArray<string>.Empty;
    /// <summary>Extension identifiers whose installed version was updated.</summary>
    [JsonPropertyName("versionUpdated")] public ImmutableArray<string> VersionUpdated { get; init; } = ImmutableArray<string>.Empty;
    /// <summary>Extension identifiers whose manifest was missing from the scan.</summary>
    [JsonPropertyName("missing")] public ImmutableArray<string> Missing { get; init; } = ImmutableArray<string>.Empty;
}

/// <summary>Read representation of extension settings.</summary>
public sealed class ControllerExtensionSettingsReadDto
{
    /// <summary>Extension identifier.</summary>
    [JsonPropertyName("extensionId")] public string ExtensionId { get; init; } = string.Empty;
    /// <summary>Version of the extension settings schema.</summary>
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    /// <summary>Extension settings JSON value.</summary>
    [JsonPropertyName("settings")] public JsonElement Settings { get; init; }
    /// <summary>Current extension-settings version.</summary>
    [JsonPropertyName("version")] public long Version { get; init; }
}

/// <summary>Write representation of extension settings.</summary>
public sealed class ControllerExtensionSettingsWriteDto
{
    /// <summary>Version of the extension settings schema.</summary>
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    /// <summary>Extension settings JSON value.</summary>
    [JsonPropertyName("settings")] public JsonElement Settings { get; init; }
}

/// <summary>Aggregate root representation. Sensitive service environments and settings are not embedded.</summary>
public sealed class ControllerApiRootDto
{
    /// <summary>Current aggregate version.</summary>
    [JsonPropertyName("version")] public long Version { get; init; }
    /// <summary>Current global settings.</summary>
    [JsonPropertyName("globalSettings")] public ControllerGlobalSettingsReadDto GlobalSettings { get; init; } = new();
    /// <summary>Routes currently configured in the controller.</summary>
    [JsonPropertyName("routes")] public ImmutableArray<ControllerRouteReadDto> Routes { get; init; } = ImmutableArray<ControllerRouteReadDto>.Empty;
    /// <summary>Services currently configured in the controller.</summary>
    [JsonPropertyName("services")] public ImmutableArray<ControllerServiceReadDto> Services { get; init; } = ImmutableArray<ControllerServiceReadDto>.Empty;
    /// <summary>Extensions currently registered with the controller.</summary>
    [JsonPropertyName("extensions")] public ImmutableArray<ControllerExtensionRecordReadDto> Extensions { get; init; } = ImmutableArray<ControllerExtensionRecordReadDto>.Empty;
}
