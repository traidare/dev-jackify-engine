using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wabbajack.Common;
using Wabbajack.Downloaders;
using Wabbajack.Downloaders.GameFile;
using Wabbajack.DTOs;
using Wabbajack.DTOs.BSA.FileStates;
using Wabbajack.DTOs.Directives;
using Wabbajack.DTOs.DownloadStates;
using Wabbajack.DTOs.Interventions;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.FileExtractor.ExtractedFiles;
using Wabbajack.Hashing.PHash;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Installer.Utilities;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.RateLimiter;
using Wabbajack.VFS;

namespace Wabbajack.Installer;

public record StatusUpdate(string StatusCategory, string StatusText, Percent StepsProgress, Percent StepProgress, int CurrentStep)
{
}

public interface IInstaller
{
    Task<bool> Begin(CancellationToken token);
}

public abstract class AInstaller<T>
    where T : AInstaller<T>
{
    private const int _limitMS = 100;

    private static readonly Regex NoDeleteRegex = new(@"(?i)[\\\/]\[NoDelete\]", RegexOptions.Compiled);

    protected readonly InstallerConfiguration _configuration;
    protected readonly DownloadDispatcher _downloadDispatcher;
    private readonly FileExtractor.FileExtractor _extractor;
    protected readonly FileHashCache FileHashCache;
    protected readonly IGameLocator _gameLocator;
    private readonly DTOSerializer _jsonSerializer;
    protected readonly ILogger<T> _logger;
    protected readonly TemporaryFileManager _manager;
    protected readonly ParallelOptions _parallelOptions;
    private readonly Context _vfs;
    protected readonly Client _wjClient;
    private int _currentStep;
    private long _currentStepProgress;


    protected long MaxStepProgress { get; set; }
    private string _statusCategory;
    private string _statusText;
    private readonly Stopwatch _updateStopWatch = new();
    protected readonly Stopwatch _installationStopWatch = new();

    public Action<StatusUpdate>? OnStatusUpdate;
    protected readonly IResource<IInstaller> _limiter;
    protected readonly IServiceProvider _serviceProvider;
    private Func<long, string> _statusFormatter = x => x.ToString();


    public AInstaller(ILogger<T> logger, InstallerConfiguration config, IGameLocator gameLocator,
        FileExtractor.FileExtractor extractor,
        DTOSerializer jsonSerializer, Context vfs, FileHashCache fileHashCache,
        DownloadDispatcher downloadDispatcher,
        ParallelOptions parallelOptions,
        IResource<IInstaller> limiter,
        Client wjClient,
        IImageLoader imageLoader,
        IServiceProvider serviceProvider)
    {
        _limiter = limiter;
        _serviceProvider = serviceProvider;
        _manager = new TemporaryFileManager(config.Install.Combine("__temp__"));
        ExtractedModlistFolder = _manager.CreateFolder();
        _configuration = config;
        _logger = logger;
        _extractor = extractor.WithTemporaryFileManager(_manager);
        _jsonSerializer = jsonSerializer;
        _vfs = vfs.WithTemporaryFileManager(_manager);
        FileHashCache = fileHashCache;
        _downloadDispatcher = downloadDispatcher;
        _parallelOptions = parallelOptions;
        _gameLocator = gameLocator;
        _wjClient = wjClient;
        ImageLoader = imageLoader;
    }

    public IImageLoader ImageLoader { get; }

    protected long MaxSteps { get; set; }

    public Dictionary<Hash, AbsolutePath> HashedArchives { get; set; } = new();
    public bool UseCompression { get; set; }

    public TemporaryPath ExtractedModlistFolder { get; set; }

    public ModList ModList => _configuration.ModList;
    public Directive[] UnoptimizedDirectives { get; set; }
    public Archive[] UnoptimizedArchives { get; set; }

    public void NextStep(string statusCategory, string statusText, long maxStepProgress, Func<long, string>? formatter = null)
    {
        _updateStopWatch.Restart();
        MaxStepProgress = maxStepProgress;
        _currentStep += 1;
        _currentStepProgress = 0;
        _statusText = statusText;
        _statusCategory = statusCategory;
        _statusFormatter = formatter ?? (x => x.ToString());
        
        // Add blank line before section header for better visual separation
        // Only add blank line for major sections, not for sub-sections like "Installing Included Files"
        if (!statusText.Contains("Included Files") && !statusText.Contains("BSAs"))
        {
            Console.WriteLine();
        }
        
        // Format: === Configuring Installer ===
        var sectionHeader = $"=== {statusText} ===";
        _logger.LogInformation(sectionHeader);

        OnStatusUpdate?.Invoke(new StatusUpdate(statusCategory, statusText,
            Percent.FactoryPutInRange(_currentStep, MaxSteps), Percent.Zero, _currentStep));
    }

    public void UpdateProgress(long stepProgress)
    {
        Interlocked.Add(ref _currentStepProgress, stepProgress);

        OnStatusUpdate?.Invoke(new StatusUpdate(_statusCategory, $"[{_currentStep}/{MaxSteps}] {_statusText} ({_statusFormatter(_currentStepProgress)}/{_statusFormatter(MaxStepProgress)})",
            Percent.FactoryPutInRange(_currentStep, MaxSteps),
            Percent.FactoryPutInRange(_currentStepProgress, MaxStepProgress), _currentStep));
    }

    public abstract Task<InstallResult> Begin(CancellationToken token);

    protected async Task ExtractModlist(CancellationToken token)
    {
        ExtractedModlistFolder = _manager.CreateFolder();
        await using var stream = _configuration.ModlistArchive.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        NextStep(Consts.StepPreparing, "Extracting Modlist", archive.Entries.Count);
        
        // Track extraction progress for individual file progress output
        using var fileProgressTracker = new FileProgressTracker();
        var lastFileProgressOutput = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();
        var totalEntries = archive.Entries.Count;
        var processedEntries = 0;
        
        // Display task for extraction progress
        var displayCts = new CancellationTokenSource();
        var displayTask = Task.Run(async () =>
        {
            while (!displayCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, displayCts.Token); // Update every 1 second
                    
                    var activeFiles = fileProgressTracker.GetActiveFiles();
                    var now = DateTime.UtcNow;
                    
                    // Read current processed count (may be slightly stale, but fine for progress display)
                    var currentProcessed = processedEntries;
                    
                    foreach (var (filename, progress) in activeFiles)
                    {
                        var shouldOutput = progress.IsCompleted || 
                            !lastFileProgressOutput.TryGetValue(filename, out var lastOutput) || 
                            (now - lastOutput).TotalMilliseconds >= 200;
                        
                        if (shouldOutput)
                        {
                            var percent = progress.GetPercent();
                            var speed = progress.GetSpeedString();
                            var operation = progress.IsCompleted ? "Completed" : progress.Operation;
                            
                            // Include counter for extraction operations (show completed/total)
                            var counterCompleted = progress.Operation == "Extracting" ? currentProcessed : (int?)null;
                            var counterTotal = progress.Operation == "Extracting" ? totalEntries : (int?)null;
                            
                            ConsoleOutput.PrintFileProgress(operation, filename, percent, speed, counterCompleted, counterTotal);
                            lastFileProgressOutput[filename] = now;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, displayCts.Token);
        
        foreach (var entry in archive.Entries)
        {
            var filename = entry.FullName;
            var entrySize = entry.Length;
            var startTime = DateTime.UtcNow;
            long bytesCopied = 0;
            
            // Add to tracker at start
            fileProgressTracker.UpdateProgress(filename, "Extracting", 0, entrySize, startTime);
            
            var path = entry.FullName.ToRelativePath().RelativeTo(ExtractedModlistFolder);
            path.Parent.CreateDirectory();
            await using var of = path.Open(FileMode.Create, FileAccess.Write, FileShare.None);
            
            // Copy with progress tracking
            await using var sourceStream = entry.Open();
            var buffer = new byte[81920]; // 80KB buffer
            int bytesRead;
            while ((bytesRead = await sourceStream.ReadAsync(buffer, token)) > 0)
            {
                await of.WriteAsync(buffer.AsMemory(0, bytesRead), token);
                bytesCopied += bytesRead;
                
                // Update progress periodically (every 1MB or 5% change)
                var updateThreshold = Math.Max(1024 * 1024, entrySize / 20);
                if (bytesCopied % updateThreshold < bytesRead || bytesCopied == entrySize)
                {
                    fileProgressTracker.UpdateProgress(filename, "Extracting", bytesCopied, entrySize, DateTime.UtcNow);
                }
            }
            
            // Mark as completed
            fileProgressTracker.MarkCompleted(filename);
            var completedInfo = fileProgressTracker.GetFileInfo(filename);
            if (completedInfo != null && completedInfo.IsCompleted)
            {
                var percent = completedInfo.GetPercent();
                var speed = completedInfo.GetSpeedString();
                // Include counter showing completed/total entries
                ConsoleOutput.PrintFileProgress("Completed", filename, percent, speed, processedEntries + 1, totalEntries);
            }
            
            processedEntries++;
            UpdateProgress(1);
        }
        
        // Clean up display task
        displayCts.Cancel();
        try { await displayTask; } catch (OperationCanceledException) { }
    }

    public async Task<byte[]> LoadBytesFromPath(RelativePath path)
    {
        var fullPath = ExtractedModlistFolder.Path.Combine(path);
        if (!fullPath.FileExists())
            throw new Exception($"Cannot load inlined data {path} file does not exist");

        return await fullPath.ReadAllBytesAsync();
    }

    public Task<Stream> InlinedFileStream(RelativePath inlinedFile)
    {
        var fullPath = ExtractedModlistFolder.Path.Combine(inlinedFile);
        return Task.FromResult(fullPath.Open(FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    public static async Task<ModList> LoadFromFile(DTOSerializer serializer, AbsolutePath path)
    {
        await using var fs = path.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        using var ar = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = ar.GetEntry("modlist");
        if (entry == null)
        {
            entry = ar.GetEntry("modlist.json");
            if (entry == null)
                throw new Exception("Invalid Wabbajack Installer");
            await using var e = entry.Open();
            return (await serializer.DeserializeAsync<ModList>(e))!;
        }

        await using (var e = entry.Open())
        {
            return (await serializer.DeserializeAsync<ModList>(e))!;
        }
    }

    public static async Task<Stream?> ModListImageStream(AbsolutePath path)
    {
        await using var fs = path.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        using var ar = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = ar.GetEntry("modlist-image.png");
        if (entry == null)
            return null;
        return new MemoryStream(await entry.Open().ReadAllAsync());
    }

    /// <summary>
    ///     We don't want to make the installer index all the archives, that's just a waste of time, so instead
    ///     we'll pass just enough information to VFS to let it know about the files we have.
    /// </summary>
    protected async Task PrimeVFS()
    {
        NextStep(Consts.StepPreparing, "Priming VFS", 0);
        _vfs.AddKnown(_configuration.ModList.Directives.OfType<FromArchive>().Select(d => d.ArchiveHashPath),
            HashedArchives);

        try
        {
            await _vfs.BackfillMissing();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Missing archive with hash"))
        {
            // Extract the hash from the error message and try to find the friendly name
            var hashMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"Missing archive with hash ([A-Za-z0-9+/=]+)");
            if (hashMatch.Success)
            {
                try
                {
                    var missingHash = Hash.FromBase64(hashMatch.Groups[1].Value);
                    var archive = ModList.Archives.FirstOrDefault(a => a.Hash == missingHash);
                    var archiveName = archive?.Name ?? "Unknown";

                    // Try to diagnose the issue
                    string diagnostic = "";
                    if (archive != null)
                    {
                        var expectedFile = _configuration.Downloads.Combine(archiveName);
                        if (expectedFile.FileExists())
                        {
                            var actualSize = expectedFile.Size();
                            if (actualSize != archive.Size)
                            {
                                diagnostic = $" File exists but wrong size: expected {archive.Size} bytes, found {actualSize} bytes (corrupted download).";
                            }
                            else
                            {
                                diagnostic = " File exists with correct size but wrong hash (corrupted or modified).";
                            }
                        }
                        else
                        {
                            diagnostic = " File not found in downloads directory.";
                        }
                    }

                    _logger.LogError("VFS priming failed: Archive '{ArchiveName}' (hash: {Hash}) not found in HashedArchives.{Diagnostic}",
                        archiveName, missingHash, diagnostic);
                    throw new InvalidOperationException($"Installation failed: Required archive '{archiveName}' could not be located.{diagnostic} " +
                                                      "Please re-download this file and retry installation.", ex);
                }
                catch (Exception parseEx)
                {
                    _logger.LogError("Failed to parse hash from error message: {ParseError}", parseEx.Message);
                    _logger.LogError("VFS priming failed due to missing archives. Installation cannot continue.");
                    throw new InvalidOperationException("Installation failed: Required archives are missing from downloads directory. " +
                                                      "Please ensure all files have been downloaded successfully before retrying installation.", ex);
                }
            }
            else
            {
                _logger.LogError("VFS priming failed due to missing archives. Installation cannot continue.");
                throw new InvalidOperationException("Installation failed: Required archives are missing from downloads directory. " +
                                                  "Please ensure all files have been downloaded successfully before retrying installation.", ex);
            }
        }
    }

    public Task BuildFolderStructure()
    {
        NextStep(Consts.StepPreparing, "Building Folder Structure", 0);
        _logger.LogInformation("{Duration} Building Folder Structure", ConsoleOutput.GetDurationTimestamp());
        ModList.Directives
            .Where(d => d.To.Depth > 1)
            .Select(d => _configuration.Install.Combine(d.To.Parent))
            .Distinct()
            .Do(f => f.CreateDirectory());
        return Task.CompletedTask;
    }

    public async Task InstallArchives(CancellationToken token)
    {
        NextStep(Consts.StepInstalling, "Installing files", ModList.Directives.Sum(d => d.Size), x => x.ToFileSizeString());
        
        // Count total files and texture files for progress tracking
        var allDirectives = ModList.Directives.OfType<FromArchive>().ToList();
        var textureDirectives = allDirectives.OfType<TransformedTexture>().ToList();
        var totalFiles = allDirectives.Count;
        var totalTextures = textureDirectives.Count;
        
        // Print starting installation message
        _logger.LogInformation("{Duration} Starting installation: {TotalFiles} files to process, {TotalTextures} textures to recompress",
            ConsoleOutput.GetDurationTimestamp(), totalFiles, totalTextures);
        
        // Progress tracking variables
        long processedFiles = 0;
        long processedTextures = 0;
        var processedSize = 0L;
        var totalSize = allDirectives.Sum(d => d.Size);
        
        // Track installation progress for individual file progress output
        using var fileProgressTracker = new FileProgressTracker();
        var lastFileProgressOutput = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();
        
        // Display task for installation progress
        var displayCts = new CancellationTokenSource();
        var displayTask = Task.Run(async () =>
        {
            while (!displayCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, displayCts.Token); // Update every 1 second
                    
                    var activeFiles = fileProgressTracker.GetActiveFiles();
                    var now = DateTime.UtcNow;
                    
                    // Read current processed counts (may be slightly stale, but fine for progress display)
                    var currentProcessedFiles = (int)Interlocked.Read(ref processedFiles);
                    var currentProcessedTextures = (int)Interlocked.Read(ref processedTextures);
                    
                    // Print overall progress counter (single line that updates in place with \r)
                    var currentProcessedSize = Interlocked.Read(ref processedSize);
                    var processedSizeMB = currentProcessedSize / 1024.0 / 1024.0;
                    var totalSizeMB = totalSize / 1024.0 / 1024.0;
                    string processedSizeStr, totalSizeStr;
                    if (processedSizeMB >= 1024.0)
                    {
                        processedSizeStr = $"{processedSizeMB / 1024.0:F1}GB";
                    }
                    else
                    {
                        processedSizeStr = $"{processedSizeMB:F1}MB";
                    }
                    if (totalSizeMB >= 1024.0)
                    {
                        totalSizeStr = $"{totalSizeMB / 1024.0:F1}GB";
                    }
                    else
                    {
                        totalSizeStr = $"{totalSizeMB:F1}MB";
                    }
                    var overallProgressMessage = $"Installing files {currentProcessedFiles}/{totalFiles} ({processedSizeStr}/{totalSizeStr}) - Converting textures: {currentProcessedTextures}/{totalTextures}";
                    ConsoleOutput.PrintProgressWithDuration(overallProgressMessage);
                    
                    foreach (var (filename, progress) in activeFiles)
                    {
                        var shouldOutput = progress.IsCompleted || 
                            !lastFileProgressOutput.TryGetValue(filename, out var lastOutput) || 
                            (now - lastOutput).TotalMilliseconds >= 200;
                        
                        if (shouldOutput)
                        {
                            var percent = progress.GetPercent();
                            var speed = progress.GetSpeedString();
                            var operation = progress.IsCompleted ? "Completed" : progress.Operation;
                            
                            // Include counter: use texture counts for "Converting", file counts for "Installing"
                            int? counterCompleted = null;
                            int? counterTotal = null;
                            if (progress.Operation == "Converting")
                            {
                                counterCompleted = currentProcessedTextures;
                                counterTotal = totalTextures;
                            }
                            else if (progress.Operation == "Installing")
                            {
                                counterCompleted = currentProcessedFiles;
                                counterTotal = totalFiles;
                            }
                            
                            ConsoleOutput.PrintFileProgress(operation, filename, percent, speed, counterCompleted, counterTotal);
                            lastFileProgressOutput[filename] = now;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, displayCts.Token);
        
        var grouped = allDirectives
            .Select(a => {
                try
                {
                    return new { VF = _vfs.Index.FileForArchiveHashPath(a.ArchiveHashPath), Directive = a };
                }
                catch(Exception)
                {
                    _logger.LogError("Failed to look up file {file} by hash {hash}", a.To.FileName.ToString(), a.Hash.ToString());
                    throw;
                }
            })
            .GroupBy(a => a.VF)
            .ToDictionary(a => a.Key);

        if (grouped.Count == 0) return;
        if (token.IsCancellationRequested) return;

        await _vfs.Extract(grouped.Keys.ToHashSet(), async (vf, sf) =>
        {
            var directives = grouped[vf];
            using var job = await _limiter.Begin($"Installing files from {vf.Name}", directives.Sum(f => f.VF.Size),
                token);
            foreach (var directive in directives)
            {
                if (token.IsCancellationRequested) return;
                var file = directive.Directive;
                
                // Track individual file installation progress
                var filename = file.To.FileName.ToString();
                var fileSize = file.Size;
                var startTime = DateTime.UtcNow;
                
                // Determine operation type - textures show as "Converting", others as "Installing"
                var operation = file is TransformedTexture ? "Converting" : "Installing";
                
                // Add to tracker at start
                fileProgressTracker.UpdateProgress(filename, operation, 0, fileSize, startTime);
                
                // Update progress tracking
                Interlocked.Increment(ref processedFiles);
                Interlocked.Add(ref processedSize, file.Size);
                
                // Overall progress counter is now printed by the display task (every 1 second)
                // No need to print it here for every file - that causes console spam
                
                var destPath = file.To.RelativeTo(_configuration.Install);
                switch (file)
                {
                    case PatchedFromArchive pfa:
                    {
                        await using var s = await sf.GetStream();
                        s.Position = 0;
                        await using var patchDataStream = await InlinedFileStream(pfa.PatchID);
                        {
                            destPath.Parent.CreateDirectory();
                            await using var os = destPath.Open(FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                            var hash = await BinaryPatching.ApplyPatch(s, patchDataStream, os);
                            ThrowOnNonMatchingHash(file, hash);
                        }
                    }
                        break;

                    case TransformedTexture tt:
                    {
                        await using var s = await sf.GetStream();
                        await using var of = destPath.Open(FileMode.Create, FileAccess.Write);
                        
                        // Update texture progress before conversion
                        Interlocked.Increment(ref processedTextures);
                        
                        // Overall progress counter is now printed by the display task (every 1 second)
                        // No need to print it here for every texture - that causes console spam
                        
                        // Only log individual texture recompression in debug mode
                        _logger.LogDebug("Recompressing {Filename}", tt.To.FileName);
                        await ImageLoader.Recompress(s, tt.ImageState.Width, tt.ImageState.Height, tt.ImageState.MipLevels, tt.ImageState.Format,
                            of, token);
                    }
                        break;

                    case FromArchive _:
                        if (grouped[vf].Count() == 1)
                        {
                            var hash = await sf.MoveHashedAsync(destPath, token);
                            ThrowOnNonMatchingHash(file, hash);
                        }
                        else
                        {
                            await using var s = await sf.GetStream();
                            var hash = await destPath.WriteAllHashedAsync(s, token, false);
                            ThrowOnNonMatchingHash(file, hash);
                        }

                        break;
                    default:
                        throw new Exception($"No handler for {directive}");
                }
                
                // Mark file installation as completed
                fileProgressTracker.MarkCompleted(filename);
                var completedInfo = fileProgressTracker.GetFileInfo(filename);
                if (completedInfo != null && completedInfo.IsCompleted)
                {
                    var percent = completedInfo.GetPercent();
                    var speed = completedInfo.GetSpeedString();
                    // Include counter: use texture counts for texture conversions, file counts for regular installations
                    int? counterCompleted = null;
                    int? counterTotal = null;
                    if (file is TransformedTexture)
                    {
                        counterCompleted = (int)Interlocked.Read(ref processedTextures);
                        counterTotal = totalTextures;
                    }
                    else
                    {
                        counterCompleted = (int)Interlocked.Read(ref processedFiles);
                        counterTotal = totalFiles;
                    }
                    ConsoleOutput.PrintFileProgress("Completed", filename, percent, speed, counterCompleted, counterTotal);
                }
                
                await FileHashCache.FileHashWriteCache(destPath, file.Hash);

                await job.Report((int) directive.VF.Size, token);
            }
        }, token);
        
        // Clean up display task
        displayCts.Cancel();
        try { await displayTask; } catch (OperationCanceledException) { }
    }

    protected void ThrowOnNonMatchingHash(Directive file, Hash gotHash)
    {
        if (file.Hash != gotHash)
            ThrowNonMatchingError(file, gotHash);
    }
    private void ThrowNonMatchingError(Directive file, Hash gotHash)
    {
        _logger.LogError("Hashes for {Path} did not match, expected {Expected} got {Got}", file.To, file.Hash, gotHash);
        throw new Exception($"Hashes for {file.To} did not match, expected {file.Hash} got {gotHash}");
    }
    
    
    protected void ThrowOnNonMatchingHash(CreateBSA bsa, Directive directive, AFile state, Hash hash)
    {
        if (hash == directive.Hash) return;
        _logger.LogError("Hashes for BSA don't match after extraction, {BSA}, {Directive}, {ExpectedHash}, {Hash}", bsa.To, directive.To, directive.Hash, hash);
        throw new Exception($"Hashes for {bsa.To} file {directive.To} did not match, expected {directive.Hash} got {hash}");
    }

    public async Task DownloadArchives(CancellationToken token)
    {
        var missing = ModList.Archives.Where(a => !HashedArchives.ContainsKey(a.Hash)).ToList();
        _logger.LogInformation("{Duration} Missing {count} archives", ConsoleOutput.GetDurationTimestamp(), missing.Count);

        var dispatchers = missing.Select(m => _downloadDispatcher.Downloader(m))
            .Distinct()
            .ToList();

        await Task.WhenAll(dispatchers.Select(d => d.Prepare()));

        _logger.LogInformation("{Duration} Downloading validation data", ConsoleOutput.GetDurationTimestamp());
        var validationData = await _wjClient.LoadDownloadAllowList();
        var mirrors = (await _wjClient.LoadMirrors()).ToLookup(m => m.Hash);

        _logger.LogInformation("{Duration} Validating Archives", ConsoleOutput.GetDurationTimestamp());

        foreach (var archive in missing)
        {
            var matches = mirrors[archive.Hash].ToArray();
            if (!matches.Any()) continue;
            
            archive.State = matches.First().State;
            _ = _wjClient.SendMetric("rerouted", archive.Hash.ToString());
            _logger.LogInformation("Rerouted {Archive} to {Mirror}", archive.Name,
                matches.First().State.PrimaryKeyString);
        }
        
        
        foreach (var archive in missing.Where(archive =>
                     !_downloadDispatcher.Downloader(archive).IsAllowed(validationData, archive.State)))
        {
            _logger.LogCritical("File {primaryKeyString} failed validation", archive.State.PrimaryKeyString);
            return;
        }

        _logger.LogInformation("{Duration} Downloading missing archives", ConsoleOutput.GetDurationTimestamp());
        await DownloadMissingArchives(missing, token);
    }

    public async Task DownloadMissingArchives(List<Archive> missing, CancellationToken token, bool download = true)
    {
        _logger.LogInformation("{Duration} Downloading {Count} archives", ConsoleOutput.GetDurationTimestamp(), missing.Count.ToString());
        NextStep(Consts.StepDownloading, "Downloading files", missing.Count);

        missing = await missing
            .SelectAsync(async m => await _downloadDispatcher.MaybeProxy(m, token))
            .ToList();

        if (download)
        {
            var result = SendDownloadMetrics(missing);
            foreach (var a in missing.Where(a => a.State is Manual))
            {
                var outputPath = _configuration.Downloads.Combine(a.Name);
                await DownloadArchive(a, true, token, outputPath);
                UpdateProgress(1);
            }
        }

        var nonManualCount = missing.Count(a => a.State is not Manual);

        // Only setup bandwidth monitoring and progress display if there are automated downloads
        BandwidthMonitor? bandwidthMonitor = null;
        CancellationTokenSource? displayCts = null;
        Task? displayTask = null;
        FileProgressTracker? fileProgressTracker = null;
        var completedCount = 0;
        var lastFileProgressOutput = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();

        if (nonManualCount > 0)
        {
            // Professional bandwidth monitoring setup
            bandwidthMonitor = new BandwidthMonitor(sampleWindowSeconds: 5); // 5-second rolling window
            
            // File progress tracking for individual file progress output
            fileProgressTracker = new FileProgressTracker();

            // Update display every 1 second for professional feel
            displayCts = new CancellationTokenSource();
            displayTask = Task.Run(async () =>
            {
                while (!displayCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(1000, displayCts.Token); // Update every 1 second

                        var currentBandwidthMBps = bandwidthMonitor.GetCurrentBandwidthMBps();
                        var currentCompleted = Interlocked.CompareExchange(ref completedCount, 0, 0);

                        ConsoleOutput.PrintProgressWithDuration($"Downloading Mod Archives ({currentCompleted}/{nonManualCount}) - {currentBandwidthMBps:F1}MB/s");
                        
                        // Output individual file progress for all active files (with throttling)
                        if (fileProgressTracker != null)
                        {
                            var activeFiles = fileProgressTracker.GetActiveFiles();
                            var now = DateTime.UtcNow;
                            
                            foreach (var (filename, progress) in activeFiles)
                            {
                                // Always output completed files immediately (so GUI sees completion)
                                // For active files, throttle: only output if >200ms since last output
                                var shouldOutput = progress.IsCompleted || 
                                    !lastFileProgressOutput.TryGetValue(filename, out var lastOutput) || 
                                    (now - lastOutput).TotalMilliseconds >= 200;
                                
                                if (shouldOutput)
                                {
                                    var percent = progress.GetPercent();
                                    var speed = progress.GetSpeedString();
                                    
                                    // Show "Completed" status for finished files
                                    var operation = progress.IsCompleted ? "Completed" : progress.Operation;
                                    
                                    ConsoleOutput.PrintFileProgress(operation, filename, percent, speed);
                                    lastFileProgressOutput[filename] = now;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, displayCts.Token);
        }
        else if (missing.Any(a => a.State is Manual))
        {
            // All downloads are manual - inform user
            ConsoleOutput.PrintWithDuration("All remaining downloads require Nexus Premium");
        }

        await missing
            .Shuffle()
            .Where(a => a.State is not Manual)
            .PDoAll(async archive =>
            {
                var outputPath = _configuration.Downloads.Combine(archive.Name);
                
                try
                {
                    // Download using standard method with file progress tracking
                    var hash = await _downloadDispatcher.Download(archive, outputPath, token, null, fileProgressTracker);
                    
                    // Update completed count
                    Interlocked.Increment(ref completedCount);
                    UpdateProgress(1);
                }
                catch (Exception ex)
                {
                    // Mark as failed in tracker
                    fileProgressTracker?.MarkCompleted(archive.Name);
                    
                    // Provide concise user-facing context and a URL in non-debug, with full details in debug
                    var modInfo = GetModInfoFromArchive(archive);
                    var sourceUrl = GetSourceUrlFromArchive(archive);
                    if (!string.IsNullOrEmpty(sourceUrl))
                    {
                        _logger.LogError("Failed to download '{FileName}'{ModInfo} — {ErrorMessage}\n    URL: {Url}",
                            archive.Name,
                            modInfo,
                            ex.Message,
                            sourceUrl);
                    }
                    else
                    {
                        _logger.LogError("Failed to download '{FileName}'{ModInfo} — {ErrorMessage}",
                            archive.Name,
                            modInfo,
                            ex.Message);
                    }
                    _logger.LogDebug(ex, "Full download error details for {FileName}", archive.Name);
                    // Do not rethrow; continue other downloads and let the end-of-phase summary report remaining failures
                }
            });

        // Clean up display task and bandwidth monitor if they were created
        if (displayCts != null && displayTask != null)
        {
            displayCts.Cancel();
            try { await displayTask; } catch (OperationCanceledException) { }

            // Clear the progress line after downloads complete
            ConsoleOutput.ClearProgressLine();
        }

        bandwidthMonitor?.Dispose();
        fileProgressTracker?.Dispose();
    }

    private string GetModInfoFromArchive(Archive archive)
    {
        try
        {
            // Try to extract meaningful mod information from the archive state
            switch (archive.State)
            {
                case DTOs.DownloadStates.Nexus nexus:
                    return $" from Nexus (Game: {nexus.Game}, ModID: {nexus.ModID}, FileID: {nexus.FileID})";
                case DTOs.DownloadStates.GoogleDrive gdrive:
                    return $" from Google Drive (ID: {gdrive.Id})";
                case DTOs.DownloadStates.Http http:
                    return $" from {http.Url.Host}";
                case DTOs.DownloadStates.Mega mega:
                    return $" from Mega (URL: {mega.Url})";
                case DTOs.DownloadStates.MediaFire mediafire:
                    return $" from MediaFire (URL: {mediafire.Url})";
                case DTOs.DownloadStates.Manual manual:
                    return $" (Manual download from: {manual.Url})";
                default:
                    return $" (Source: {archive.State.GetType().Name})";
            }
        }
        catch
        {
            return "";
        }
    }

    private string GetSourceUrlFromArchive(Archive archive)
    {
        try
        {
            switch (archive.State)
            {
                case DTOs.DownloadStates.Nexus nexus:
                    var nx = nexus.Game.MetaData().NexusName;
                    return $"https://www.nexusmods.com/{nx}/mods/{nexus.ModID}?tab=files&file_id={nexus.FileID}";
                case DTOs.DownloadStates.GoogleDrive gdrive:
                    return $"https://drive.google.com/uc?id={gdrive.Id}&export=download";
                case DTOs.DownloadStates.Http http:
                    return http.Url.ToString();
                case DTOs.DownloadStates.Mega mega:
                    return mega.Url.ToString();
                case DTOs.DownloadStates.MediaFire mediafire:
                    return mediafire.Url.ToString();
                case DTOs.DownloadStates.WabbajackCDN cdn:
                    return cdn.Url.ToString();
                default:
                    return string.Empty;
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task SendDownloadMetrics(List<Archive> missing)
    {
        var grouped = missing.GroupBy(m => m.State.GetType());
        foreach (var group in grouped)
            await _wjClient.SendMetric($"downloading_{group.Key.FullName!.Split(".").Last().Split("+").First()}",
                group.Sum(g => g.Size).ToString());
    }

    public async Task<bool> DownloadArchive(Archive archive, bool download, CancellationToken token,
        AbsolutePath? destination = null)
    {
        try
        {
            destination ??= _configuration.Downloads.Combine(archive.Name);

            var (result, hash) =
                await _downloadDispatcher.DownloadWithPossibleUpgrade(archive, destination.Value, token);
            if (token.IsCancellationRequested)
            {
                return false;
            }

            if (hash != archive.Hash)
            {
                // Empty hash (default) means download didn't complete - don't delete partial file, allow resume
                if (hash == default)
                {
                    _logger.LogDebug("Download incomplete for {name} - partial file will be resumed on next attempt", archive.Name);
                    return false;
                }
                
                // Non-empty hash mismatch means file is corrupted - delete it
                if (destination!.Value.FileExists())
                {
                    _logger.LogError("Hash mismatch for existing file {name}: expected {Expected}, got {Downloaded}. The file appears to be corrupted or outdated.", 
                        archive.Name, archive.Hash, hash);
                    destination!.Value.Delete();
                }
                else
                {
                    _logger.LogError("Downloaded hash {Downloaded} does not match expected hash: {Expected}", hash,
                        archive.Hash);
                }

                return false;
            }

            if (hash != default)
                await FileHashCache.FileHashWriteCache(destination.Value, hash);

            if (result == DownloadResult.Update)
                await destination.Value.MoveToAsync(destination.Value.Parent.Combine(archive.Hash.ToHex()), true,
                    token);
                    
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // No actual error. User canceled downloads.
        }
        catch (NotImplementedException) when (archive.State is GameFileSource)
        {
            _logger.LogError("Missing game file {name}. This could be caused by missing DLC or a modified installation.", archive.Name);
        }
        catch (ManualDownloadRequiredException)
        {
            // Manual downloads are handled by the intervention handler, not here
            // Don't log this as an error since it's expected behavior
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download error for file {name}", archive.Name);
        }

        return false;
    }

    public async Task HashArchives(CancellationToken token)
    {
        NextStep(Consts.StepHashing, "Hashing Archives", 0);
        _logger.LogInformation("{Duration} Looking for files to hash", ConsoleOutput.GetDurationTimestamp());

        var allFiles = _configuration.Downloads.EnumerateFiles()
            .Concat(_gameLocator.GameLocation(_configuration.Game).EnumerateFiles())
            .ToList();

        _logger.LogInformation("{Duration} Getting archive sizes", ConsoleOutput.GetDurationTimestamp());
        var hashDict = (await allFiles.PMapAllBatched(_limiter, x => (x, x.Size())).ToList())
            .GroupBy(f => f.Item2)
            .ToDictionary(g => g.Key, g => g.Select(v => v.x));

        _logger.LogInformation("{Duration} Linking archives to downloads", ConsoleOutput.GetDurationTimestamp());
        // Only hash files that exactly match expected size - exclude partial downloads
        var toHash = ModList.Archives.Where(a => hashDict.ContainsKey(a.Size))
            .SelectMany(a => hashDict[a.Size].Where(f => f.Size() == a.Size)) // Only files with exact size match
            .ToList();

        // Log any archives that weren't found in the filesystem
        var missingArchives = ModList.Archives.Where(a => !hashDict.ContainsKey(a.Size)).ToList();
        if (missingArchives.Any())
        {
            _logger.LogDebug("{Duration} {count} archives not found by size match:", ConsoleOutput.GetDurationTimestamp(), missingArchives.Count);
            foreach (var missing in missingArchives.Take(10))
            {
                _logger.LogDebug("  - {name} (hash: {hash}, size: {size}, state: {state})",
                    missing.Name, missing.Hash, missing.Size, missing.State.GetType().Name);
            }
            if (missingArchives.Count > 10)
            {
                _logger.LogDebug("  ... and {more} more", missingArchives.Count - 10);
            }
        }

        NextStep(Consts.StepPreparing, "Hashing downloads", toHash.Count);
        _logger.LogInformation("{Duration} Found {count} total files, {hashedCount} matching filesize", ConsoleOutput.GetDurationTimestamp(), allFiles.Count,
            toHash.Count);

        // Track validation progress for individual file progress output
        using var fileProgressTracker = new FileProgressTracker();
        var lastFileProgressOutput = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();
        
        // Display task for validation progress
        var displayCts = new CancellationTokenSource();
        var displayTask = Task.Run(async () =>
        {
            while (!displayCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, displayCts.Token); // Update every 1 second
                    
                    var activeFiles = fileProgressTracker.GetActiveFiles();
                    var now = DateTime.UtcNow;
                    
                    foreach (var (filename, progress) in activeFiles)
                    {
                        var shouldOutput = progress.IsCompleted || 
                            !lastFileProgressOutput.TryGetValue(filename, out var lastOutput) || 
                            (now - lastOutput).TotalMilliseconds >= 200;
                        
                        if (shouldOutput)
                        {
                            var percent = progress.GetPercent();
                            var speed = progress.GetSpeedString();
                            var operation = progress.IsCompleted ? "Completed" : progress.Operation;
                            
                            ConsoleOutput.PrintFileProgress(operation, filename, percent, speed);
                            lastFileProgressOutput[filename] = now;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, displayCts.Token);

        var hashResults = await
            toHash
                .PMapAll(async e =>
                {
                    var filename = e.FileName.ToString();
                    var fileSize = e.Size();
                    var startTime = DateTime.UtcNow;
                    
                    // Add to tracker at start (validation doesn't have byte-level progress, so we'll track by time)
                    fileProgressTracker.UpdateProgress(filename, "Validating", 0, fileSize, startTime);
                    
                    // Hash the file
                    var hash = await FileHashCache.FileHashCachedAsync(e, token);
                    
                    // Mark as completed
                    fileProgressTracker.MarkCompleted(filename);
                    var completedInfo = fileProgressTracker.GetFileInfo(filename);
                    if (completedInfo != null && completedInfo.IsCompleted)
                    {
                        var percent = completedInfo.GetPercent();
                        var speed = completedInfo.GetSpeedString();
                        ConsoleOutput.PrintFileProgress("Completed", filename, percent, speed);
                    }
                    
                    UpdateProgress(1);
                    return (hash, e);
                })
                .ToList();
        
        // Clean up display task
        displayCts.Cancel();
        try { await displayTask; } catch (OperationCanceledException) { }

        HashedArchives = hashResults
            .OrderByDescending(e => e.Item2.LastModified())
            .GroupBy(e => e.Item1)
            .Select(e => e.First())
            .Where(x => x.Item1 != default)
            .ToDictionary(kv => kv.Item1, kv => kv.e);
    }


    /// <summary>
    ///     The user may already have some files in the _configuration.Install. If so we can go through these and
    ///     figure out which need to be updated, deleted, or left alone
    /// </summary>
    protected async Task OptimizeModlist(CancellationToken token)
    {
        _logger.LogInformation("{Duration} Optimizing ModList directives", ConsoleOutput.GetDurationTimestamp());
        UnoptimizedArchives = ModList.Archives;
        UnoptimizedDirectives = ModList.Directives;
        
        var indexed = ModList.Directives.ToDictionary(d => d.To);

        var bsasToBuild = await ModList.Directives
            .OfType<CreateBSA>()
            .PMapAll(async b =>
            {
                var file = _configuration.Install.Combine(b.To);
                if (!file.FileExists())
                    return (true, b);
                return (b.Hash != await FileHashCache.FileHashCachedAsync(file, token), b);
            })
            .ToArray();

        var bsasToNotBuild = bsasToBuild
            .Where(b => b.Item1 == false).Select(t => t.b.TempID).ToHashSet();

        var bsaPathsToNotBuild = bsasToBuild
            .Where(b => b.Item1 == false).Select(t => t.b.To.RelativeTo(_configuration.Install))
            .ToHashSet();

        indexed = indexed.Values
            .Where(d =>
            {
                return d switch
                {
                    CreateBSA bsa => !bsasToNotBuild.Contains(bsa.TempID),
                    FromArchive a when a.To.StartsWith($"{Consts.BSACreationDir}") => !bsasToNotBuild.Any(b =>
                        a.To.RelativeTo(_configuration.Install).InFolder(_configuration.Install.Combine(Consts.BSACreationDir, b))),
                    _ => true
                };
            }).ToDictionary(d => d.To);


        var profileFolder = _configuration.Install.Combine("profiles");
        var savePath = (RelativePath) "saves";

        NextStep(Consts.StepPreparing, "Looking for files to delete", 0);
        await _configuration.Install.EnumerateFiles()
            .PMapAllBatched(_limiter,  f =>
            {
                var relativeTo = f.RelativeTo(_configuration.Install);
                if (indexed.ContainsKey(relativeTo) || f.InFolder(_configuration.Downloads))
                    return f;

                if (f.InFolder(profileFolder) && f.Parent.FileName == savePath) return f;
                var fNoSpaces = new string(f.ToString().Where(c => !Char.IsWhiteSpace(c)).ToArray());
                if (NoDeleteRegex.IsMatch(fNoSpaces))
                    return f;

                if (bsaPathsToNotBuild.Contains(f))
                    return f;

                //_logger.LogInformation("Deleting {RelativePath} it's not part of this ModList", relativeTo);
                f.Delete();
                return f;
            }).Sink();

        NextStep(Consts.StepPreparing, "Cleaning empty folders", 0);
        var expectedFolders = indexed.Keys
            .Select(f => f.RelativeTo(_configuration.Install))
            // We ignore the last part of the path, so we need a dummy file name
            .Append(_configuration.Downloads.Combine("_"))
            .Where(f => f.InFolder(_configuration.Install))
            .SelectMany(path =>
            {
                // Get all the folders and all the folder parents
                // so for foo\bar\baz\qux.txt this emits ["foo", "foo\\bar", "foo\\bar\\baz"]
                var split = ((string) path.RelativeTo(_configuration.Install)).Split('\\');
                return Enumerable.Range(1, split.Length - 1).Select(t => string.Join("\\", split.Take(t)));
            })
            .Distinct()
            .Select(p => _configuration.Install.Combine(p))
            .ToHashSet();

        try
        {
            var toDelete = _configuration.Install.EnumerateDirectories(true)
                .Where(p => !expectedFolders.Contains(p))
                .OrderByDescending(p => p.ToString().Length)
                .ToList();
            foreach (var dir in toDelete)
            {
                dir.DeleteDirectory(dontDeleteIfNotEmpty: true);
            }
        }
        catch (Exception)
        {
            // ignored because it's not worth throwing a fit over
            _logger.LogInformation("Error when trying to clean empty folders. This doesn't really matter.");
        }

        var existingfiles = _configuration.Install.EnumerateFiles().ToHashSet();

        NextStep(Consts.StepPreparing, "Looking for unmodified files", indexed.Values.Count);
        await indexed.Values.PMapAllBatchedAsync(_limiter, async d =>
            {
                // Bit backwards, but we want to return null for 
                // all files we *want* installed. We return the files
                // to remove from the install list.
                var path = _configuration.Install.Combine(d.To);
                if (!existingfiles.Contains(path)) return null;

                try
                {
                    return await FileHashCache.FileHashCachedAsync(path, token) == d.Hash ? d : null;
                }
                catch (FileNotFoundException)
                {
                    // File was deleted between enumeration and hash check - treat as if it doesn't exist
                    _logger.LogDebug("File {File} was deleted during optimization, treating as missing", path);
                    return null;
                }
            })
            .Do(d =>
            {
                UpdateProgress(1);
                if (d != null)
                {
                    indexed.Remove(d.To);
                }
            });

        NextStep(Consts.StepPreparing, "Updating ModList", 0);
        _logger.LogInformation("{Duration} Optimized {From} directives to {To} required", ConsoleOutput.GetDurationTimestamp(), ModList.Directives.Length, indexed.Count);
        var requiredArchives = indexed.Values.OfType<FromArchive>()
            .GroupBy(d => d.ArchiveHashPath.Hash)
            .Select(d => d.Key)
            .ToHashSet();
        
        ModList.Archives = ModList.Archives.Where(a => requiredArchives.Contains(a.Hash)).ToArray();
        ModList.Directives = indexed.Values.ToArray();
    }


}