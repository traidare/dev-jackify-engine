using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Wabbajack.Paths.IO;

public static class AbsolutePathExtensions
{
    public const int BufferSize = 1024 * 128;

    public static Stream Open(this AbsolutePath file, FileMode mode, FileAccess access = FileAccess.Read,
        FileShare share = FileShare.ReadWrite)
    {
        return File.Open(file.ToNativePath(), mode, access, share);
    }

    public static void Delete(this AbsolutePath file)
    {
        var path = file.ToNativePath();
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (UnauthorizedAccessException)
            {
                var fi = new FileInfo(path);
                if (fi.IsReadOnly)
                {
                    fi.IsReadOnly = false;
                    File.Delete(path);
                }
                else
                {
                    throw;
                }
            }
            catch (IOException ex)
            {
                if (ex.Message.Contains("because it is being used by another process"))
                {
                    Thread.Sleep(1000);
                    File.Delete(path);
                }
                else
                {
                    throw;
                }
            }
        }
        if (Directory.Exists(path))
            file.DeleteDirectory();
    }

    public static long Size(this AbsolutePath file)
    {
        return new FileInfo(file.ToNativePath()).Length;
    }

    public static DateTime LastModifiedUtc(this AbsolutePath file)
    {
        return new FileInfo(file.ToNativePath()).LastWriteTimeUtc;
    }
    
    public static DateTime CreatedUtc(this AbsolutePath file)
    {
        return new FileInfo(file.ToNativePath()).CreationTimeUtc;
    }

    public static DateTime LastModified(this AbsolutePath file)
    {
        return new FileInfo(file.ToNativePath()).LastWriteTime;
    }
    
    public static void Touch(this AbsolutePath file)
    {
        new FileInfo(file.ToNativePath()).LastWriteTime = DateTime.Now;
    }

    public static byte[] ReadAllBytes(this AbsolutePath file)
    {
        using var s = File.Open(file.ToNativePath(), FileMode.Open, FileAccess.Read, FileShare.Read);
        var remain = s.Length;
        var length = remain;
        var bytes = new byte[length];

        while (remain > 0) remain -= s.Read(bytes, (int) Math.Min(length - remain, 1024 * 1024), bytes.Length);

        return bytes;
    }

    public static string ReadAllText(this AbsolutePath file)
    {
        return Encoding.UTF8.GetString(file.ReadAllBytes());
    }

    public static async IAsyncEnumerable<string> ReadAllLinesAsync(this AbsolutePath file)
    {
        await using var fs = file.Open(FileMode.Open);
        var sr = new StreamReader(fs);
        while (true)
        {
            var line = await sr.ReadLineAsync();
            if (line == null) break;
            yield return line;
        }
    }

    public static IEnumerable<string> ReadAllLines(this AbsolutePath file)
    {
        using var fs = file.Open(FileMode.Open);
        var sr = new StreamReader(fs);
        while (true)
        {
            var line = sr.ReadLine();
            if (line == null) break;
            yield return line;
        }
    }

    public static async Task<string> ReadAllTextAsync(this AbsolutePath file)
    {
        return Encoding.UTF8.GetString(await file.ReadAllBytesAsync());
    }

    public static async ValueTask<byte[]> ReadAllBytesAsync(this AbsolutePath file,
        CancellationToken token = default)
    {
        await using var s = File.Open(file.ToNativePath(), FileMode.Open, FileAccess.Read, FileShare.Read);
        var remain = s.Length;
        var length = remain;
        var bytes = new byte[length];

        while (remain > 0)
            remain -= await s.ReadAsync(bytes.AsMemory((int) Math.Min(length - remain, 1024 * 1024), bytes.Length),
                token);

        return bytes;
    }

    public static void WriteAllBytes(this AbsolutePath file, ReadOnlySpan<byte> data)
    {
        using var s = file.Open(FileMode.Create, FileAccess.Write, FileShare.None);
        s.Write(data);
    }

    public static async Task WriteAllAsync(this AbsolutePath file, Stream srcStream, CancellationToken token,
        bool closeWhenDone = true)
    {
        var buff = new byte[BufferSize];
        await using var dest = file.Open(FileMode.Create, FileAccess.Write, FileShare.None);
        while (true)
        {
            var read = await srcStream.ReadAsync(buff.AsMemory(0, BufferSize), token);
            if (read == 0)
                break;
            await dest.WriteAsync(buff.AsMemory(0, read), token);
        }

        if (closeWhenDone)
            await srcStream.DisposeAsync();
    }

    public static async Task WriteAllLinesAsync(this AbsolutePath file, IEnumerable<string> src,
        CancellationToken token, bool closeWhenDone = true)
    {
        await using var dest = file.Open(FileMode.Create, FileAccess.Write, FileShare.None);
        await using var sw = new StreamWriter(dest, Encoding.UTF8);

        foreach (var line in src) await sw.WriteLineAsync(line);

        await sw.DisposeAsync();
    }
    
    public static async Task WriteAllLinesAsync(this AbsolutePath file, IEnumerable<string> src,
        FileMode fileMode, CancellationToken token)
    {
        await using var dest = file.Open(fileMode, FileAccess.Write, FileShare.None);
        await using var sw = new StreamWriter(dest, Encoding.UTF8);
        foreach (var line in src) await sw.WriteLineAsync(line);
        await sw.DisposeAsync();
    }

    public static async ValueTask WriteAllBytesAsync(this AbsolutePath file, Memory<byte> data,
        CancellationToken token = default)
    {
        await using var s = file.Open(FileMode.Create, FileAccess.Write, FileShare.None);
        await s.WriteAsync(data, token);
    }

    public static async ValueTask MoveToAsync(this AbsolutePath src, AbsolutePath dest, bool overwrite,
        CancellationToken token)
    {
        // TODO: Make this async
        var srcStr = src.ToString();
        var destStr = dest.ToString();
        
        // Linux-specific fix: Handle file attributes that may not be compatible
        try
        {
            var fi = new FileInfo(srcStr);
            if (fi.IsReadOnly)
                fi.IsReadOnly = false;
        }
        catch (ArgumentException)
        {
            // Ignore file attribute errors on Linux - they're not critical
        }

        try
        {
            var fid = new FileInfo(destStr);
            if (dest.FileExists() && fid.IsReadOnly)
            {
                fid.IsReadOnly = false;
            }
        }
        catch (ArgumentException)
        {
            // Ignore file attribute errors on Linux - they're not critical
        }

        var retries = 0;
        while (true)
        {
            try
            {
                File.Move(srcStr, destStr, overwrite);
                return;
            }
            catch (Exception)
            {
                if (retries > 10)
                    throw;
                retries++;
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
    }

    public static async ValueTask CopyToAsync(this AbsolutePath src, AbsolutePath dest,
        CancellationToken token)
    {
        await using var inf = src.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var ouf = dest.Open(FileMode.Create, FileAccess.Write, FileShare.Read);
        await inf.CopyToAsync(ouf, token);
    }

    public static void WriteAllText(this AbsolutePath file, string str)
    {
        file.WriteAllBytes(Encoding.UTF8.GetBytes(str));
    }

    public static async Task WriteAllTextAsync(this AbsolutePath file, string str,
        CancellationToken token = default)
    {
        await file.WriteAllBytesAsync(Encoding.UTF8.GetBytes(str), token);
    }

    private static string ToNativePath(this AbsolutePath file)
    {
        return file.ToString();
    }

    public static async Task CopyToAsync(this Stream from, AbsolutePath path, CancellationToken token = default)
    {
        await using var to = path.Open(FileMode.Create, FileAccess.Write, FileShare.None);
        await from.CopyToAsync(to, token);
    }
    
    public static async Task CopyToAsync(this AbsolutePath from, Stream to, CancellationToken token = default)
    {
        await using var fromStream = from.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        await fromStream.CopyToAsync(to, token);
    }

    #region Directories

    public static void CreateDirectory(this AbsolutePath path)
    {
        if (path.Depth > 1 && !path.Parent.DirectoryExists())
            path.Parent.CreateDirectory();

        // Fast path: Check if directory already exists with exact casing (99% of cases)
        var nativePath = ToNativePath(path);
        if (Directory.Exists(nativePath))
            return;

        // Handle case-insensitive directory lookup on Linux (e.g., BA2 archives with inconsistent case)
        // Only check for case differences when exact match doesn't exist (slow path, rare)
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) && path.Depth > 0)
        {
            var parent = path.Parent;
            if (parent.Depth > 0 && parent.DirectoryExists())
            {
                var dirName = path.FileName.ToString();
                var parentPath = parent.ToString();
                try
                {
                    // Check if a directory with different case already exists
                    foreach (var dir in Directory.GetDirectories(parentPath))
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        if (string.Equals(dirInfo.Name, dirName, StringComparison.OrdinalIgnoreCase))
                        {
                            // Directory with different case exists - no need to create
                            return;
                        }
                    }
                }
                catch
                {
                    // If enumeration fails, continue with normal creation
                }
            }
        }

        Directory.CreateDirectory(nativePath);
    }

    public static void DeleteDirectory(this AbsolutePath path, bool dontDeleteIfNotEmpty = false)
    {
        if (!path.DirectoryExists()) return;
        if (dontDeleteIfNotEmpty && (path.EnumerateFiles().Any() || path.EnumerateDirectories().Any())) return;
      
        foreach (var directory in Directory.GetDirectories(path.ToString()))
        {
            var diChild = new DirectoryInfo(directory);
            // Skip symlinked directories to avoid traversing host mounts like Proton dosdevices (e.g., z: -> /)
            if (diChild.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            DeleteDirectory(directory.ToAbsolutePath(), dontDeleteIfNotEmpty);
        }
        try
        {
            var di = new DirectoryInfo(path.ToString());
            if (di.Attributes.HasFlag(FileAttributes.ReadOnly))
                di.Attributes &= ~FileAttributes.ReadOnly;
            Directory.Delete(path.ToString(), true);
        }
        catch (UnauthorizedAccessException)
        {
            Directory.Delete(path.ToString(), true);
        }
    }

    public static bool DirectoryExists(this AbsolutePath path)
    {
        return path != default && Directory.Exists(path.ToNativePath());
    }

    public static bool FileExists(this AbsolutePath path)
    {
        if (path == default) return false;
        return File.Exists(path.ToNativePath());
    }

    public static IEnumerable<AbsolutePath> EnumerateFiles(this AbsolutePath path, string pattern = "*",
        bool recursive = true)
    {
        try
        {
            return Directory.EnumerateFiles(path.ToString(), pattern,
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(file => !string.IsNullOrEmpty(file))
                .Select(file => file.ToAbsolutePath());
        }
        catch (DirectoryNotFoundException)
        {
            return Enumerable.Empty<AbsolutePath>();
        }
        catch (UnauthorizedAccessException)
        {
            return Enumerable.Empty<AbsolutePath>();
        }
        catch (ArgumentException)
        {
            return Enumerable.Empty<AbsolutePath>();
        }
        catch (IOException)
        {
            return Enumerable.Empty<AbsolutePath>();
        }
    }


    public static IEnumerable<AbsolutePath> EnumerateFiles(this AbsolutePath path, Extension pattern,
        bool recursive = true)
    {
        return Directory.EnumerateFiles(path.ToString(), "*" + pattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Select(file => file.ToAbsolutePath());
    }


    public static IEnumerable<AbsolutePath> EnumerateDirectories(this AbsolutePath path, bool recursive = true)
    {
        if (!path.DirectoryExists()) return Array.Empty<AbsolutePath>();
        return Directory.EnumerateDirectories(path.ToString(), "*",
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Select(p => (AbsolutePath) p);
    }

    /// <summary>
    /// Finds a file or directory using case-insensitive path matching on Linux.
    /// Returns the actual path with correct casing, or null if not found.
    /// Example: path="Foo/Bar/file.txt" finds "foo/BAR/FILE.TXT" if it exists.
    ///
    /// TODO: Consolidate all case-insensitive path handling to use this method:
    ///  - AInstaller.NormalizePathToCaseInsensitive() (line 647)
    ///  - ExtractedMemoryfile.FindExistingCaseVariant() (line 69)
    ///  - ExtractedNativeFile.FindExistingCaseVariant() (line 73)
    /// Currently these duplicate implementations exist for historical reasons.
    /// </summary>
    public static AbsolutePath? FindCaseInsensitive(this AbsolutePath baseDir, RelativePath relativePath)
    {
        var parts = relativePath.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = baseDir;

        foreach (var part in parts)
        {
            if (!currentPath.DirectoryExists())
                return null;

            // Try exact match first (fast path)
            var exactMatch = currentPath.Combine(part);
            if (exactMatch.DirectoryExists() || exactMatch.FileExists())
            {
                currentPath = exactMatch;
                continue;
            }

            // Case-insensitive search (slow path)
            AbsolutePath? match = null;
            try
            {
                // Check directories
                foreach (var dir in currentPath.EnumerateDirectories(recursive: false))
                {
                    if (dir.FileName.ToString().Equals(part, StringComparison.OrdinalIgnoreCase))
                    {
                        match = dir;
                        break;
                    }
                }

                // Check files if no directory matched
                if (match == null)
                {
                    foreach (var file in currentPath.EnumerateFiles("*", recursive: false))
                    {
                        if (file.FileName.ToString().Equals(part, StringComparison.OrdinalIgnoreCase))
                        {
                            match = file;
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }

            if (match == null)
                return null;

            currentPath = match.Value;
        }

        return currentPath.FileExists() || currentPath.DirectoryExists() ? currentPath : null;
    }

    #endregion
}