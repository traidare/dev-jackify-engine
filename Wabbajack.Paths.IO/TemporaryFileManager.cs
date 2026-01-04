using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using shortid;
using shortid.Configuration;
namespace Wabbajack.Paths.IO;

public class TemporaryFileManager : IDisposable, IAsyncDisposable
{
    private readonly AbsolutePath _basePath;
    private readonly bool _deleteOnDispose;
    private GenerationOptions _options = new(
        useNumbers: true,
        useSpecialCharacters:false,
        length: 8);

    public TemporaryFileManager() : this(GetJackifyDataDirectory().Combine("temp"))
    {
    }
    
    /// <summary>
    /// Gets the Jackify data directory from ~/.config/jackify/config.json, falling back to ~/Jackify.
    /// Inlined here to avoid circular dependency with Wabbajack.Common.
    /// </summary>
    private static AbsolutePath GetJackifyDataDirectory()
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AbsolutePath configDir;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                configDir = (home + "/.config/jackify").ToAbsolutePath();
            }
            else
            {
                // Non-Linux fallback
                configDir = KnownFolders.WabbajackAppLocal.Combine("jackify");
            }

            var configPath = configDir.Combine("config.json");
            if (configPath.FileExists())
            {
                var json = File.ReadAllText(configPath.ToString());
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("jackify_data_dir", out var dataDirProp) && dataDirProp.ValueKind == JsonValueKind.String)
                {
                    var dataDir = dataDirProp.GetString();
                    if (!string.IsNullOrWhiteSpace(dataDir))
                    {
                        return dataDir!.ToAbsolutePath();
                    }
                }
            }
        }
        catch
        {
            // Ignore and fall through to default
        }

        // Fallback to ~/Jackify (capitalized, not lowercase)
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).ToAbsolutePath().Combine("Jackify");
    }

    public TemporaryFileManager(AbsolutePath basePath, bool deleteOnDispose = true)
    {
        _deleteOnDispose = deleteOnDispose;
        _basePath = basePath;
        _basePath.CreateDirectory();
    }

    public void Dispose()
    {
        if (!_deleteOnDispose) return;
        for (var retries = 0; retries < 10; retries++)
        {
            try
            {
                if (!_basePath.DirectoryExists())
                    return;
                _basePath.DeleteDirectory();
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(1000);
            }
        }
    }
    
    
    public async ValueTask DisposeAsync()
    {
        if (!_deleteOnDispose) return;
        for (var retries = 0; retries < 10; retries++)
        {
            try
            {
                if (!_basePath.DirectoryExists())
                    return;
                _basePath.DeleteDirectory();
                return;
            }
            catch (IOException)
            {
                await Task.Delay(1000);
            }
        }
    }

    public TemporaryPath CreateFile(Extension? ext = default, bool deleteOnDispose = true)
    {
        //Changed this from GUID to reduce the file path footprint of temporary files
        //to avoid the `MAX_PATH` limit from causing issues.
        var path = _basePath.Combine(ShortId.Generate(_options));
        if (path.Extension != default)
            path = path.WithExtension(ext);
        return new TemporaryPath(path);
    }

    public TemporaryPath CreateFolder()
    {
        //Changed this from GUID to reduce the file path footprint of temporary files
        //to avoid the `MAX_PATH` limit from causing issues.
        var path = _basePath.Combine(ShortId.Generate(_options));
        path.CreateDirectory();
        return new TemporaryPath(path);
    }

}