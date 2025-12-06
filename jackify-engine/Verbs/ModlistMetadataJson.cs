using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Wabbajack.DTOs;

namespace Wabbajack.CLI.Verbs;

/// <summary>
/// Enhanced modlist metadata for JSON output with pre-constructed image URLs and additional fields
/// </summary>
public class ModlistMetadataJson
{
    // Basic Information
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("author")] public string Author { get; set; } = string.Empty;
    [JsonPropertyName("maintainers")] public string[] Maintainers { get; set; } = Array.Empty<string>();
    [JsonPropertyName("namespacedName")] public string NamespacedName { get; set; } = string.Empty;
    [JsonPropertyName("repositoryName")] public string RepositoryName { get; set; } = string.Empty;
    [JsonPropertyName("machineURL")] public string MachineURL { get; set; } = string.Empty;

    // Game Information
    [JsonPropertyName("game")] public string Game { get; set; } = string.Empty;
    [JsonPropertyName("gameHumanFriendly")] public string GameHumanFriendly { get; set; } = string.Empty;

    // Status Flags
    [JsonPropertyName("official")] public bool Official { get; set; }
    [JsonPropertyName("nsfw")] public bool NSFW { get; set; }
    [JsonPropertyName("utilityList")] public bool UtilityList { get; set; }
    [JsonPropertyName("forceDown")] public bool ForceDown { get; set; }
    [JsonPropertyName("imageContainsTitle")] public bool ImageContainsTitle { get; set; }

    // Version Information
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("displayVersionOnlyInInstallerView")] public bool DisplayVersionOnlyInInstallerView { get; set; }

    // Dates
    [JsonPropertyName("dateCreated")] public DateTime DateCreated { get; set; }
    [JsonPropertyName("dateUpdated")] public DateTime DateUpdated { get; set; }

    // Tags
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();

    // Links
    [JsonPropertyName("links")] public LinksJson Links { get; set; } = new();

    // Size Information
    [JsonPropertyName("sizes")] public SizesJson? Sizes { get; set; }

    // Image URLs (pre-constructed)
    [JsonPropertyName("images")] public ImagesJson Images { get; set; } = new();

    // Validation Status (optional)
    [JsonPropertyName("validation")] public ValidationJson? Validation { get; set; }

    // Search Index Data (optional)
    [JsonPropertyName("mods")] public List<string>? Mods { get; set; }
}

public class LinksJson
{
    [JsonPropertyName("image")] public string ImageUri { get; set; } = string.Empty;
    [JsonPropertyName("readme")] public string Readme { get; set; } = string.Empty;
    [JsonPropertyName("download")] public string Download { get; set; } = string.Empty;
    [JsonPropertyName("discordURL")] public string DiscordURL { get; set; } = string.Empty;
    [JsonPropertyName("websiteURL")] public string WebsiteURL { get; set; } = string.Empty;
}

public class SizesJson
{
    [JsonPropertyName("downloadSize")] public long DownloadSize { get; set; }
    [JsonPropertyName("downloadSizeFormatted")] public string DownloadSizeFormatted { get; set; } = string.Empty;
    [JsonPropertyName("installSize")] public long InstallSize { get; set; }
    [JsonPropertyName("installSizeFormatted")] public string InstallSizeFormatted { get; set; } = string.Empty;
    [JsonPropertyName("totalSize")] public long TotalSize { get; set; }
    [JsonPropertyName("totalSizeFormatted")] public string TotalSizeFormatted { get; set; } = string.Empty;
    [JsonPropertyName("numberOfArchives")] public long NumberOfArchives { get; set; }
    [JsonPropertyName("numberOfInstalledFiles")] public long NumberOfInstalledFiles { get; set; }
}

public class ImagesJson
{
    [JsonPropertyName("small")] public string Small { get; set; } = string.Empty;
    [JsonPropertyName("large")] public string Large { get; set; } = string.Empty;
}

public class ValidationJson
{
    [JsonPropertyName("failed")] public int Failed { get; set; }
    [JsonPropertyName("passed")] public int Passed { get; set; }
    [JsonPropertyName("updating")] public int Updating { get; set; }
    [JsonPropertyName("mirrored")] public int Mirrored { get; set; }
    [JsonPropertyName("modListIsMissing")] public bool ModListIsMissing { get; set; }
    [JsonPropertyName("hasFailures")] public bool HasFailures { get; set; }
}

public class ModlistMetadataResponse
{
    [JsonPropertyName("metadataVersion")] public string MetadataVersion { get; set; } = "1.0";
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("modlists")] public List<ModlistMetadataJson> Modlists { get; set; } = new();
}

