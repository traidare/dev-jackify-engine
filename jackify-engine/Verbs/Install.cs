using System;
using System.IO;
using System.Collections.Concurrent;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.NamingConventionBinder;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.CLI.Builder;
using Wabbajack.Common;
using Wabbajack.Downloaders;
using Wabbajack.Downloaders.GameFile;
using Wabbajack.DTOs;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.DTOs.Interventions;
using Wabbajack.Installer;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.Services.OSIntegrated;
using Wabbajack.VFS;
using Wabbajack.Networking.Http.Interfaces;

namespace Wabbajack.CLI.Verbs;

public class Install
{
    private readonly ILogger<Install> _logger;
    private readonly Client _wjClient;
    private readonly DownloadDispatcher _dispatcher;
    private readonly IServiceProvider _serviceProvider;
    private readonly DTOSerializer _dtos;
    private readonly FileHashCache _cache;
    private readonly GameLocator _gameLocator;

    public Install(ILogger<Install> logger, Client wjClient, DownloadDispatcher dispatcher, DTOSerializer dtos, 
        FileHashCache cache, GameLocator gameLocator, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _wjClient = wjClient;
        _dispatcher = dispatcher;
        _dtos = dtos;
        _serviceProvider = serviceProvider;
        _cache = cache;
        _gameLocator = gameLocator;
    }

    public static VerbDefinition Definition = new VerbDefinition("install", "Installs a wabbajack file", new[]
    {
        new OptionDefinition(typeof(AbsolutePath), "w", "wabbajack", "Wabbajack file"),
        new OptionDefinition(typeof(string), "m", "machineUrl", "Machine url to download"),
        new OptionDefinition(typeof(AbsolutePath), "o", "output", "Output path"),
        new OptionDefinition(typeof(AbsolutePath), "d", "downloads", "Downloads path")
    });

    internal async Task<int> Run(AbsolutePath wabbajack, AbsolutePath output, AbsolutePath downloads, string machineUrl, CancellationToken token)
    {
        if (!string.IsNullOrEmpty(machineUrl))
        {
            if (!await DownloadMachineUrl(machineUrl, wabbajack, token))
                return 1;
            
            // Update wabbajack path to the downloaded file if it was empty
            if (wabbajack == AbsolutePath.Empty)
            {
                var fileName = machineUrl.Replace("/", "_@@_") + ".wabbajack";
                var downloadDir = JackifyConfig.GetDataDirectory().Combine("downloaded_mod_lists");
                downloadDir.CreateDirectory();
                wabbajack = downloadDir.Combine(fileName);
            }
        }

        // Print version header (no timestamps - these are informational messages before installation)
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        _logger.LogInformation("jackify-engine v{Version}: Minimal Linux-native modlist installer for Jackify", version);
        _logger.LogInformation("---------------------------------------------------------------");

        // Load modlist with error handling for incomplete files
        ModList modlist;
        try
        {
            modlist = await StandardInstaller.LoadFromFile(_dtos, wabbajack);
        }
        catch (System.IO.InvalidDataException ex) when (ex.Message.Contains("End of Central Directory") || ex.Message.Contains("could not be found"))
        {
            _logger.LogError("The .wabbajack file is incomplete or corrupted. This usually means the download was interrupted.");
            _logger.LogError("Please delete the file and try again: {Path}", wabbajack);
            _logger.LogError("Error: {Error}", ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load .wabbajack file: {Path}", wabbajack);
            return 1;
        }

        var installer = StandardInstaller.Create(_serviceProvider, new InstallerConfiguration
        {
            Downloads = downloads,
            Install = output,
            ModList = modlist,
            Game = modlist.GameType,
            ModlistArchive = wabbajack,
            GameFolder = _gameLocator.GameLocation(modlist.GameType)
        });

        var result = await installer.Begin(token);

        // Check for manual downloads and print summary if any were encountered
        var interventionHandler = _serviceProvider.GetService(typeof(IUserInterventionHandler)) as CLIUserInterventionHandler;
        if (interventionHandler != null && interventionHandler.GetManualDownloads().Any())
        {
            var manualDownloads = interventionHandler.GetManualDownloads();
            
            _logger.LogInformation("");
            _logger.LogInformation("╔══════════════════════════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║                           MANUAL DOWNLOADS REQUIRED                          ║");
            _logger.LogInformation("╚══════════════════════════════════════════════════════════════════════════════╝");
            _logger.LogInformation("");
            _logger.LogInformation("The following {Count} files require manual download. Please download each file and place it in your downloads directory, then run the installation again.", manualDownloads.Count);
            _logger.LogInformation("");
            _logger.LogInformation("Downloads directory: {DownloadsPath}", downloads);
            _logger.LogInformation("");
            
            for (int i = 0; i < manualDownloads.Count; i++)
            {
                var manualDownload = manualDownloads[i];
                var url = manualDownload.Archive.State.PrimaryKeyString;
                
                // Extract just the URL part from the state string
                if (url.Contains("|"))
                {
                    url = url.Split('|').Last();
                }
                
                _logger.LogInformation("{Number}. {FileName}", i + 1, manualDownload.Archive.Name);
                _logger.LogInformation("    URL: {Url}", url);
                _logger.LogInformation("");
            }
            
            _logger.LogInformation("After downloading all files, run the installation command again.");
            _logger.LogInformation("");
            return 1; // Return error code to indicate manual downloads are needed
        }

        // Handle different install results
        return result switch
        {
            InstallResult.Succeeded => 0,  // Success
            InstallResult.DownloadFailed => 1,  // Manual downloads needed
            _ => 2  // Other errors
        };
    }

    private async Task<bool> DownloadMachineUrl(string machineUrl, AbsolutePath wabbajack, CancellationToken token)
    {
        _logger.LogInformation("Downloading {MachineUrl}", machineUrl);

        var lists = await _wjClient.LoadLists();
        var list = lists.FirstOrDefault(l => l.NamespacedName == machineUrl);
        if (list == null)
        {
            _logger.LogInformation("Couldn't find list {MachineUrl}", machineUrl);
            return false;
        }
        
        // Generate a filename from the machine URL if wabbajack path is empty
        if (wabbajack == AbsolutePath.Empty)
        {
            var fileName = machineUrl.Replace("/", "_@@_") + ".wabbajack";
            var downloadDir = JackifyConfig.GetDataDirectory().Combine("downloaded_mod_lists");
            downloadDir.CreateDirectory();
            wabbajack = downloadDir.Combine(fileName);
        }
        
        // Optimize validation: check file size first before expensive hash check
        // If size doesn't match, skip hash check and proceed to download/resume
        if (wabbajack.FileExists())
        {
            var existingSize = wabbajack.Size();
            if (existingSize == list.DownloadMetadata!.Size)
            {
                // Size matches - verify hash to ensure file is correct
                if (await wabbajack.Hash(token) == list.DownloadMetadata.Hash)
                {
                    _logger.LogInformation("File already exists, using cached file");
                    return true;
                }
                // Hash mismatch - file is corrupted, will be redownloaded
                _logger.LogInformation("Existing file hash mismatch, will redownload");
            }
            else
            {
                // Size mismatch - partial download, will resume
                _logger.LogInformation("Existing file size mismatch ({ExistingSize} vs {ExpectedSize}), will resume download", 
                    existingSize.ToFileSizeString(), list.DownloadMetadata.Size.ToFileSizeString());
            }
        }

        var state = _dispatcher.Parse(new Uri(list.Links.Download));
        var archive = new Archive
        {
            Name = wabbajack.FileName.ToString(),
            Hash = list.DownloadMetadata!.Hash,
            Size = list.DownloadMetadata.Size,
            State = state!
        };

        // Use progress callback with rolling average smoothing (standard approach)
        // Most download managers use callbacks but smooth them over a time window
        var totalMB = archive.Size / 1024.0 / 1024.0;
        var samples = new ConcurrentQueue<(DateTime time, long bytes)>();
        const double sampleWindowSeconds = 3.0; // 3-second rolling window for smoothing

        // Initialize with existing file size if resuming
        long initialBytes = wabbajack.FileExists() ? wabbajack.Size() : 0;
        if (initialBytes > 0)
        {
            samples.Enqueue((DateTime.UtcNow, initialBytes));
        }

        // Update display periodically from samples
        var displayCts = new CancellationTokenSource();
        var displayTask = Task.Run(async () =>
        {
            while (!displayCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, displayCts.Token); // Update display every 500ms

                    var now = DateTime.UtcNow;

                    // Remove samples older than our window
                    var cutoffTime = now.AddSeconds(-sampleWindowSeconds);
                    while (samples.TryPeek(out var oldest) && oldest.time < cutoffTime)
                    {
                        samples.TryDequeue(out _);
                    }

                    // Calculate speed from samples in window (oldest to newest)
                    double speedMBps = 0;
                    long currentBytes = initialBytes;
                    var sampleArray = samples.ToArray(); // Thread-safe snapshot

                    if (sampleArray.Length >= 2)
                    {
                        var oldest = sampleArray[0];
                        var newest = sampleArray[sampleArray.Length - 1];
                        var timeSpan = (newest.time - oldest.time).TotalSeconds;
                        var bytesDelta = newest.bytes - oldest.bytes;

                        if (timeSpan > 0.5 && bytesDelta > 0) // Need at least 0.5 seconds of data
                        {
                            speedMBps = (bytesDelta / 1024.0 / 1024.0) / timeSpan;
                        }
                        currentBytes = newest.bytes;
                    }
                    else if (sampleArray.Length == 1)
                    {
                        currentBytes = sampleArray[0].bytes;
                    }

                    var processedMB = currentBytes / 1024.0 / 1024.0;
                    ConsoleOutput.PrintProgressWithDuration($"Downloading .wabbajack ({processedMB:F1}/{totalMB:F1}MB) - {speedMBps:F1}MB/s");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, displayCts.Token);

        try
        {
            // Use progress callback to collect samples (accurate, immediate)
            var downloadedHash = await _dispatcher.Download(archive, wabbajack, token, (processed, total) =>
            {
                // Add sample from callback (this is the accurate bytes downloaded)
                samples.Enqueue((DateTime.UtcNow, processed));
            }, null);
            
            // Verify download completed successfully
            if (downloadedHash == default || downloadedHash != archive.Hash)
            {
                _logger.LogError("Download failed or hash mismatch. Expected: {ExpectedHash}, Got: {DownloadedHash}", 
                    archive.Hash, downloadedHash);
                return false;
            }
            
            // Verify file size matches expected (double-check)
            if (!wabbajack.FileExists() || wabbajack.Size() != archive.Size)
            {
                _logger.LogError("Downloaded file size mismatch. Expected: {ExpectedSize}, Got: {ActualSize}", 
                    archive.Size.ToFileSizeString(), 
                    wabbajack.FileExists() ? wabbajack.Size().ToFileSizeString() : "0 bytes");
                return false;
            }
            
            // Verify file is a valid ZIP (quick integrity check)
            try
            {
                using var zipStream = wabbajack.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
                using var zip = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read);
                // If we can open it and read entries, it's valid
                var entryCount = zip.Entries.Count;
            }
            catch (System.IO.InvalidDataException ex)
            {
                _logger.LogError("Downloaded file is corrupted or incomplete (invalid ZIP). Deleting incomplete file. Error: {Error}", ex.Message);
                try
                {
                    wabbajack.Delete();
                }
                catch { }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate downloaded file as ZIP");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Download cancelled by user");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download failed with exception");
            return false;
        }
        finally
        {
            displayCts.Cancel();
            try
            {
                await displayTask;
            }
            catch (OperationCanceledException)
            {
                // Swallow cancellation
            }
            
            // Clear progress line after completion
            ConsoleOutput.ClearProgressLine();
        }

        return true;
    }
}