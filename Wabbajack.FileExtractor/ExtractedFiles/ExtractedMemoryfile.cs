using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wabbajack.DTOs.Streams;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;

namespace Wabbajack.FileExtractor.ExtractedFiles;

public class ExtractedMemoryFile : IExtractedFile
{
    private readonly IStreamFactory _factory;
    private bool _disposed = false;

    public ExtractedMemoryFile(IStreamFactory factory)
    {
        _factory = factory;
    }


    public ValueTask<Stream> GetStream()
    {
        return _factory.GetStream();
    }

    public DateTime LastModifiedUtc => _factory.LastModifiedUtc;
    public IPath Name => _factory.Name;

    public async ValueTask Move(AbsolutePath newPath, CancellationToken token)
    {
        await using var stream = await _factory.GetStream();

        try
        {
            // Normal path - try to write directly
            newPath.Parent.CreateDirectory();
            await newPath.WriteAllAsync(stream, token);
            _disposed = true;
        }
        catch (DirectoryNotFoundException)
        {
            // Case sensitivity issue: directive says "scripts" but "Scripts" exists
            // Find which case variant actually exists and use that
            stream.Position = 0; // Reset stream for retry

            var normalizedPath = FindExistingCaseVariant(newPath);
            if (normalizedPath.HasValue)
            {
                normalizedPath.Value.Parent.CreateDirectory();
                await normalizedPath.Value.WriteAllAsync(stream, token);
                _disposed = true;
            }
            else
            {
                // No case variant found - this is a genuine error
                throw;
            }
        }
    }

    /// <summary>
    /// Finds the case variant of a path that actually exists on disk.
    /// Walks from root to leaf, using existing directory case when found.
    /// Example: "/foo/scripts/file.pex" requested but "/foo/Scripts/" exists -> returns "/foo/Scripts/file.pex"
    /// This ensures all files for the same logical directory end up in the same physical directory.
    /// </summary>
    private static AbsolutePath? FindExistingCaseVariant(AbsolutePath path)
    {
        try
        {
            // Split path into components
            var parts = path.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return path;

            // Start from root
            var currentPath = "/".ToAbsolutePath();

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                var isLastPart = (i == parts.Length - 1);

                // For the last part (filename), don't look for existing files, just build the path
                if (isLastPart)
                {
                    currentPath = currentPath.Combine(part);
                    break;
                }

                // For directory parts, check if case-variant exists
                var nextPath = currentPath.Combine(part);
                if (nextPath.DirectoryExists())
                {
                    // Exact case match exists, use it
                    currentPath = nextPath;
                }
                else if (currentPath.DirectoryExists())
                {
                    // Check for case-insensitive match
                    var existingDirs = currentPath.EnumerateDirectories().ToList();
                    var caseInsensitiveMatch = existingDirs.FirstOrDefault(d =>
                        d.FileName.ToString().Equals(part, StringComparison.OrdinalIgnoreCase));

                    if (caseInsensitiveMatch != default(AbsolutePath))
                    {
                        // Use the existing directory's case
                        currentPath = caseInsensitiveMatch;
                    }
                    else
                    {
                        // No match found, use requested case (will create new directory)
                        currentPath = nextPath;
                    }
                }
                else
                {
                    // Parent doesn't exist yet, just build path with requested case
                    currentPath = nextPath;
                }
            }

            return currentPath;
        }
        catch
        {
            // If lookup fails for any reason, return null to indicate no variant found
            return null;
        }
    }

    public bool CanMove { get; set; } = true;

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            // Memory files don't need cleanup
        }
    }
}