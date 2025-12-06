using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.NamingConventionBinder;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP.Helpers;
using Microsoft.Extensions.Logging;
using Wabbajack.CLI.Builder;
using Wabbajack.DTOs;
using Wabbajack.Networking.WabbajackClientApi;

namespace Wabbajack.CLI.Verbs;

public class ListModlists
{
    private readonly ILogger<ListCreationClubContent> _logger;
    private readonly Client _client;

    public ListModlists(ILogger<ListCreationClubContent> logger, Client wjClient)
    {
        _logger = logger;
        _client = wjClient;
    }

    public static VerbDefinition Definition =
        new("list-modlists", "Lists all known modlists", new[]
        {
            new OptionDefinition(typeof(string), "g", "game", "Filter by game name"),
            new OptionDefinition(typeof(string), "a", "author", "Filter by author name"),
            new OptionDefinition(typeof(string), "s", "search", "Search in title and description"),
            new OptionDefinition(typeof(string), "n", "name", "Find modlist by exact name match"),
            new OptionDefinition(typeof(bool), "show-author", "show-author", "Show author names in output"),
            new OptionDefinition(typeof(bool), "show-install-size", "show-install-size", "Show install size instead of download size"),
            new OptionDefinition(typeof(bool), "show-all-sizes", "show-all-sizes", "Show download|install|total sizes"),
            new OptionDefinition(typeof(bool), "show-machine-url", "show-machine-url", "Show machineURL for each modlist"),
            new OptionDefinition(typeof(bool), "json", "json", "Output as JSON with full metadata"),
            new OptionDefinition(typeof(bool), "include-validation-status", "include-validation-status", "Include validation status in JSON output"),
            new OptionDefinition(typeof(bool), "include-search-index", "include-search-index", "Include mod search index data in JSON output"),
            new OptionDefinition(typeof(string), "sort-by", "sort-by", "Sort by: title, size, date (default: title)")
        });

    public async Task<int> Run(string? game, string? author, string? search, string? name, bool showAuthor, bool showInstallSize, bool showAllSizes, bool showMachineUrl, bool json, bool includeValidationStatus, bool includeSearchIndex, string? sortBy, CancellationToken token)
    {
        _logger.LogInformation("Loading all modlist definitions");
        var modlists = await _client.LoadLists();
        _logger.LogInformation("Loaded {Count} lists", modlists.Length);

        // Load optional data if requested
        ModListSummary[]? validationSummaries = null;
        Dictionary<string, ModListSummary>? validationLookup = null;
        if (json && includeValidationStatus)
        {
            _logger.LogInformation("Loading validation status data");
            validationSummaries = await _client.GetListStatuses();
            validationLookup = validationSummaries.ToDictionary(s => s.MachineURL, StringComparer.OrdinalIgnoreCase);
        }

        SearchIndex? searchIndex = null;
        Dictionary<string, HashSet<string>>? modsPerList = null;
        if (json && includeSearchIndex)
        {
            _logger.LogInformation("Loading search index data");
            searchIndex = await _client.LoadSearchIndex();
            modsPerList = searchIndex.ModsPerList;
        }

        // Apply filters
        var filteredModlists = modlists.AsEnumerable();
        
        if (!string.IsNullOrEmpty(game))
        {
            filteredModlists = filteredModlists.Where(m => 
            {
                var gameName = m.Game.MetaData().HumanFriendlyGameName;
                // Use exact matching only - no partial matches
                // e.g., "Oblivion" should not match "Oblivion Remastered"
                return string.Equals(gameName, game, StringComparison.OrdinalIgnoreCase);
            });
        }
        
        if (!string.IsNullOrEmpty(author))
        {
            filteredModlists = filteredModlists.Where(m => 
                m.Author.Contains(author, StringComparison.OrdinalIgnoreCase));
        }
        
        if (!string.IsNullOrEmpty(search))
        {
            filteredModlists = filteredModlists.Where(m => 
                (m.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (m.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (m.NamespacedName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        
        if (!string.IsNullOrEmpty(name))
        {
            filteredModlists = filteredModlists.Where(m => 
                string.Equals(m.Title, name, StringComparison.OrdinalIgnoreCase));
        }

        var finalModlists = filteredModlists.ToArray();
        _logger.LogInformation("Showing {Count} modlists after filtering", finalModlists.Length);

        // Apply sorting
        IOrderedEnumerable<ModlistMetadata> sortedModlists = sortBy?.ToLowerInvariant() switch
        {
            "size" => finalModlists.OrderBy(m => m.DownloadMetadata?.TotalSize ?? 0),
            "date" => finalModlists.OrderByDescending(m => m.DateUpdated),
            "title" or null => finalModlists.OrderBy(l => l.Title ?? l.NamespacedName),
            _ => finalModlists.OrderBy(l => l.Title ?? l.NamespacedName)
        };

        // JSON output
        if (json)
        {
            var jsonModlists = sortedModlists.Select(m => ConvertToJson(m, validationLookup, modsPerList)).ToList();
            
            var response = new ModlistMetadataResponse
            {
                MetadataVersion = "1.0",
                Timestamp = DateTime.UtcNow,
                Count = jsonModlists.Count,
                Modlists = jsonModlists
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            Console.WriteLine(JsonSerializer.Serialize(response, options));
            return 0;
        }

        // Text output (existing behavior)
        foreach (var modlist in sortedModlists)
        {
            var displayTitle = string.IsNullOrEmpty(modlist.Title) ? modlist.NamespacedName : modlist.Title;
            
            // Add status indicators to title
            if (modlist.ForceDown)
            {
                displayTitle = $"[DOWN] {displayTitle}";
            }
            
            if (modlist.NSFW)
            {
                displayTitle = $"[NSFW] {displayTitle}";
            }
            
            string sizeDisplay;
            if (showAllSizes)
            {
                var downloadSize = modlist.DownloadMetadata!.SizeOfArchives.FileSizeToString();
                var installSize = modlist.DownloadMetadata!.SizeOfInstalledFiles.FileSizeToString();
                var totalSize = modlist.DownloadMetadata!.TotalSize.FileSizeToString();
                sizeDisplay = $"{downloadSize}|{installSize}|{totalSize}";
            }
            else
            {
                var sizeToShow = showInstallSize ? modlist.DownloadMetadata!.SizeOfInstalledFiles : modlist.DownloadMetadata!.SizeOfArchives;
                sizeDisplay = sizeToShow.FileSizeToString();
            }
            
            if (showMachineUrl)
            {
                if (showAuthor)
                {
                    var displayAuthor = string.IsNullOrEmpty(modlist.Author) ? "Unknown" : modlist.Author;
                    var line = $"{displayTitle} ({displayAuthor}) - {modlist.Game.MetaData().HumanFriendlyGameName} - {sizeDisplay} - {modlist.NamespacedName}";
                    Console.WriteLine(line);
                }
                else
                {
                    var line = $"{displayTitle} - {modlist.Game.MetaData().HumanFriendlyGameName} - {sizeDisplay} - {modlist.NamespacedName}";
                    Console.WriteLine(line);
                }
            }
            else
            {
                if (showAuthor)
                {
                    var displayAuthor = string.IsNullOrEmpty(modlist.Author) ? "Unknown" : modlist.Author;
                    _logger.LogInformation("{Title} ({Author}) - {Game} - {Size}", displayTitle, displayAuthor, modlist.Game.MetaData().HumanFriendlyGameName, sizeDisplay);
                }
                else
                {
                    _logger.LogInformation("{Title} - {Game} - {Size}", displayTitle, modlist.Game.MetaData().HumanFriendlyGameName, sizeDisplay);
                }
            }
        }
        
        return 0;
    }

    private static ModlistMetadataJson ConvertToJson(ModlistMetadata metadata, Dictionary<string, ModListSummary>? validationLookup, Dictionary<string, HashSet<string>>? modsPerList)
    {
        var gameMeta = metadata.Game.MetaData();
        
        var json = new ModlistMetadataJson
        {
            Title = metadata.Title,
            Description = metadata.Description,
            Author = metadata.Author,
            Maintainers = metadata.Maintainers,
            NamespacedName = metadata.NamespacedName,
            RepositoryName = metadata.RepositoryName,
            MachineURL = metadata.Links.MachineURL,
            Game = metadata.Game.ToString(),
            GameHumanFriendly = gameMeta.HumanFriendlyGameName,
            Official = metadata.Official,
            NSFW = metadata.NSFW,
            UtilityList = metadata.UtilityList,
            ForceDown = metadata.ForceDown,
            ImageContainsTitle = metadata.ImageContainsTitle,
            Version = metadata.Version?.ToString(),
            DisplayVersionOnlyInInstallerView = metadata.DisplayVersionOnlyInInstallerView,
            DateCreated = metadata.DateCreated,
            DateUpdated = metadata.DateUpdated,
            Tags = metadata.Tags,
            Links = new LinksJson
            {
                ImageUri = metadata.Links.ImageUri,
                Readme = metadata.Links.Readme,
                Download = metadata.Links.Download,
                DiscordURL = metadata.Links.DiscordURL,
                WebsiteURL = metadata.Links.WebsiteURL
            },
            Images = new ImagesJson
            {
                Small = GetSmallImageUri(metadata),
                Large = GetLargeImageUri(metadata)
            }
        };

        // Add size information
        if (metadata.DownloadMetadata != null)
        {
            json.Sizes = new SizesJson
            {
                DownloadSize = metadata.DownloadMetadata.SizeOfArchives,
                DownloadSizeFormatted = metadata.DownloadMetadata.SizeOfArchives.FileSizeToString(),
                InstallSize = metadata.DownloadMetadata.SizeOfInstalledFiles,
                InstallSizeFormatted = metadata.DownloadMetadata.SizeOfInstalledFiles.FileSizeToString(),
                TotalSize = metadata.DownloadMetadata.TotalSize,
                TotalSizeFormatted = metadata.DownloadMetadata.TotalSize.FileSizeToString(),
                NumberOfArchives = metadata.DownloadMetadata.NumberOfArchives,
                NumberOfInstalledFiles = metadata.DownloadMetadata.NumberOfInstalledFiles
            };
        }

        // Add validation status if available
        if (validationLookup != null && validationLookup.TryGetValue(metadata.Links.MachineURL, out var summary))
        {
            json.Validation = new ValidationJson
            {
                Failed = summary.Failed,
                Passed = summary.Passed,
                Updating = summary.Updating,
                Mirrored = summary.Mirrored,
                ModListIsMissing = summary.ModListIsMissing,
                HasFailures = summary.HasFailures
            };
        }

        // Add mod search index data if available
        if (modsPerList != null && modsPerList.TryGetValue(metadata.Links.MachineURL, out var mods))
        {
            json.Mods = mods.OrderBy(m => m).ToList();
        }

        return json;
    }

    private static string GetSmallImageUri(ModlistMetadata metadata)
    {
        var fileName = metadata.Links.MachineURL + "_small.webp";
        return $"https://raw.githubusercontent.com/wabbajack-tools/mod-lists/master/reports/{metadata.RepositoryName}/{fileName}";
    }

    private static string GetLargeImageUri(ModlistMetadata metadata)
    {
        var fileName = metadata.Links.MachineURL + "_large.webp";
        return $"https://raw.githubusercontent.com/wabbajack-tools/mod-lists/master/reports/{metadata.RepositoryName}/{fileName}";
    }
}