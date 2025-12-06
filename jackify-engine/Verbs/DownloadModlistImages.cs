using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.CLI.Builder;
using Wabbajack.DTOs;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;

namespace Wabbajack.CLI.Verbs;

public class DownloadModlistImages
{
    private readonly ILogger<DownloadModlistImages> _logger;
    private readonly Client _client;
    private readonly HttpClient _httpClient;

    public DownloadModlistImages(ILogger<DownloadModlistImages> logger, Client wjClient, HttpClient httpClient)
    {
        _logger = logger;
        _client = wjClient;
        _httpClient = httpClient;
    }

    public static VerbDefinition Definition =
        new("download-modlist-images", "Downloads modlist images to a local directory", new[]
        {
            new OptionDefinition(typeof(string), "o", "output", "Output directory for images (required)"),
            new OptionDefinition(typeof(string), "g", "game", "Filter by game name"),
            new OptionDefinition(typeof(string), "a", "author", "Filter by author name"),
            new OptionDefinition(typeof(string), "s", "search", "Search in title and description"),
            new OptionDefinition(typeof(string), "size", "size", "Image size: small, large, or both (default: both)"),
            new OptionDefinition(typeof(bool), "overwrite", "overwrite", "Overwrite existing images")
        });

    public async Task<int> Run(string output, string? game, string? author, string? search, string? size, bool overwrite, CancellationToken token)
    {
        if (string.IsNullOrEmpty(output))
        {
            _logger.LogError("Output directory is required. Use --output or -o to specify a directory.");
            return 1;
        }

        var outputDir = (AbsolutePath)output;
        outputDir.CreateDirectory();

        _logger.LogInformation("Loading modlist definitions");
        var modlists = await _client.LoadLists();
        _logger.LogInformation("Loaded {Count} modlists", modlists.Length);

        // Apply filters
        var filteredModlists = modlists.AsEnumerable();

        if (!string.IsNullOrEmpty(game))
        {
            filteredModlists = filteredModlists.Where(m =>
                string.Equals(m.Game.MetaData().HumanFriendlyGameName, game, StringComparison.OrdinalIgnoreCase));
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
                (m.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var finalModlists = filteredModlists.ToArray();
        _logger.LogInformation("Downloading images for {Count} modlists", finalModlists.Length);

        var imageSize = (size ?? "both").ToLowerInvariant();
        var downloadSmall = imageSize == "small" || imageSize == "both";
        var downloadLarge = imageSize == "large" || imageSize == "both";

        int downloaded = 0;
        int skipped = 0;
        int failed = 0;

        foreach (var modlist in finalModlists)
        {
            try
            {
                if (downloadSmall)
                {
                    var smallUrl = GetSmallImageUri(modlist);
                    var smallPath = outputDir.Combine($"{modlist.Links.MachineURL}_small.webp");
                    
                    if (await DownloadImage(smallUrl, smallPath, overwrite, token))
                        downloaded++;
                    else
                        skipped++;
                }

                if (downloadLarge)
                {
                    var largeUrl = GetLargeImageUri(modlist);
                    var largePath = outputDir.Combine($"{modlist.Links.MachineURL}_large.webp");
                    
                    if (await DownloadImage(largeUrl, largePath, overwrite, token))
                        downloaded++;
                    else
                        skipped++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download images for {Modlist}", modlist.NamespacedName);
                failed++;
            }
        }

        _logger.LogInformation("Download complete: {Downloaded} downloaded, {Skipped} skipped, {Failed} failed", downloaded, skipped, failed);
        return failed > 0 ? 1 : 0;
    }

    private async Task<bool> DownloadImage(string url, AbsolutePath path, bool overwrite, CancellationToken token)
    {
        if (path.FileExists() && !overwrite)
        {
            _logger.LogDebug("Skipping {Path} (already exists)", path.FileName);
            return false;
        }

        try
        {
            _logger.LogDebug("Downloading {Url} to {Path}", url, path.FileName);
            var response = await _httpClient.GetAsync(url, token);
            response.EnsureSuccessStatusCode();

            await using var fileStream = path.Open(FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fileStream, token);

            _logger.LogDebug("Downloaded {Path}", path.FileName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download {Url}", url);
            return false;
        }
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

