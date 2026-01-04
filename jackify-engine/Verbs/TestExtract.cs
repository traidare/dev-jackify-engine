using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.CLI.Builder;
using Wabbajack.Common;
using Wabbajack.FileExtractor;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;

namespace Wabbajack.CLI.Verbs;

/// <summary>
/// Tests archive extraction using the same logic as installs (pattern matching, Proton fallback, etc.)
/// Useful for debugging extraction issues with specific archives.
/// </summary>
public class TestExtract
{
    private readonly ILogger<TestExtract> _logger;
    private readonly FileExtractor.FileExtractor _extractor;

    public TestExtract(ILogger<TestExtract> logger, FileExtractor.FileExtractor extractor)
    {
        _logger = logger;
        _extractor = extractor;
    }

    public static VerbDefinition Definition = new("test-extract",
        "Test archive extraction using the same logic as installs (pattern matching, Proton fallback, etc.)", new[]
        {
            new OptionDefinition(typeof(AbsolutePath), "a", "archive", "Archive file to extract"),
            new OptionDefinition(typeof(string), "f", "files", "Specific files to extract, comma-separated (optional, extracts all if not specified)")
        });

    internal async Task<int> Run(AbsolutePath archive, string? files, CancellationToken token)
    {
        if (!archive.FileExists())
        {
            _logger.LogError("Archive not found: {Archive}", archive);
            return 1;
        }

        _logger.LogInformation("=== Testing Archive Extraction ===");
        _logger.LogInformation("Archive: {Archive}", archive);
        _logger.LogInformation("");

        try
        {
            var streamFactory = new NativeFileStreamFactory(archive);
            HashSet<RelativePath>? onlyFiles = null;

            if (!string.IsNullOrWhiteSpace(files))
            {
                var fileList = files.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (fileList.Length > 0)
                {
                    _logger.LogInformation("Extracting {Count} specific files:", fileList.Length);
                    onlyFiles = new HashSet<RelativePath>(fileList.Select(f => (RelativePath)f));
                    foreach (var file in onlyFiles)
                    {
                        _logger.LogInformation("  - {File}", file);
                    }
                    _logger.LogInformation("");
                }
            }
            else
            {
                _logger.LogInformation("Extracting all files from archive");
                _logger.LogInformation("");
            }

            // Use the same extraction logic as installs
            var results = await _extractor.GatheringExtract<RelativePath>(
                streamFactory,
                _ => true, // Extract all files
                async (path, extractedFile) =>
                {
                    // Just return the path as the result
                    _logger.LogInformation("✓ Extracted: {Path}", path);
                    await Task.CompletedTask;
                    return path;
                },
                token,
                onlyFiles
            );

            _logger.LogInformation("");
            _logger.LogInformation("=== Extraction Complete ===");
            _logger.LogInformation("Successfully extracted {Count} file(s)", results.Count);

            if (onlyFiles != null && results.Count != onlyFiles.Count)
            {
                _logger.LogWarning("Expected {Expected} files, but extracted {Actual} files", onlyFiles.Count, results.Count);
                
                var missing = onlyFiles.Where(f => !results.ContainsKey(f)).ToList();
                if (missing.Count > 0)
                {
                    _logger.LogWarning("Missing files:");
                    foreach (var missingFile in missing)
                    {
                        _logger.LogWarning("  - {File}", missingFile);
                    }
                }

                var extra = results.Keys.Where(f => !onlyFiles.Contains(f)).ToList();
                if (extra.Count > 0)
                {
                    _logger.LogWarning("Unexpected files extracted:");
                    foreach (var extraFile in extra)
                    {
                        _logger.LogWarning("  - {File}", extraFile);
                    }
                }

                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Extraction failed");
            return 1;
        }
    }
}

