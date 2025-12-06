using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.NamingConventionBinder;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.CLI.Builder;
using Wabbajack.Common;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.Downloaders;
using Wabbajack.DTOs;

namespace Wabbajack.CLI.Verbs;

public class DownloadWabbajackFile
{
    private readonly ILogger<DownloadWabbajackFile> _logger;
    private readonly Client _client;
    private readonly HttpClient _httpClient;
    private readonly DownloadDispatcher _dispatcher;

    public DownloadWabbajackFile(ILogger<DownloadWabbajackFile> logger, Client wjClient, HttpClient httpClient, DownloadDispatcher dispatcher)
    {
        _logger = logger;
        _client = wjClient;
        _httpClient = httpClient;
        _dispatcher = dispatcher;
    }

    public static VerbDefinition Definition = new(
        "download-wabbajack-file",
        "Downloads a .wabbajack file by machineURL (e.g. Author/ListName)",
        new[]
        {
            new OptionDefinition(typeof(string), "m", "machineUrl", "machineURL (namespaced name), e.g. Author/ListName"),
            new OptionDefinition(typeof(AbsolutePath), "o", "output", "Output .wabbajack path")
        });

    public async Task<int> Run(string machineUrl, AbsolutePath output, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(machineUrl))
        {
            _logger.LogError("machineURL is required. Use --machineUrl or -m.");
            return 1;
        }
        if (output == AbsolutePath.Empty)
        {
            _logger.LogError("Output path is required. Use --output or -o.");
            return 1;
        }

        _logger.LogInformation("Loading modlists to resolve {MachineUrl}", machineUrl);
        var lists = await _client.LoadLists();
        var list = lists.FirstOrDefault(l => string.Equals(l.NamespacedName, machineUrl, StringComparison.OrdinalIgnoreCase));
        if (list == null)
        {
            _logger.LogError("No modlist found for machineURL {MachineUrl}", machineUrl);
            return 1;
        }

        // Use the same dispatcher pipeline as install to handle mirrors/cdn/auth
        var state = _dispatcher.Parse(new Uri(list.Links.Download));
        if (state == null)
        {
            _logger.LogError("Failed to parse download URL for {MachineUrl}", machineUrl);
            return 1;
        }

        var archive = new Archive
        {
            Name = output.FileName.ToString(),
            Hash = list.DownloadMetadata!.Hash,
            Size = list.DownloadMetadata.Size,
            State = state
        };

        output.Parent.CreateDirectory();

        // Use progress callback with rolling average smoothing (standard approach)
        // Most download managers use callbacks but smooth them over a time window
        var started = DateTime.UtcNow;
        var totalMB = archive.Size / 1024.0 / 1024.0;
        var samples = new System.Collections.Generic.Queue<(DateTime time, long bytes)>();
        const double sampleWindowSeconds = 3.0; // 3-second rolling window for smoothing
        
        // Initialize with existing file size if resuming
        long initialBytes = output.FileExists() ? output.Size() : 0;
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
                    while (samples.Count > 0 && samples.Peek().time < cutoffTime)
                    {
                        samples.Dequeue();
                    }
                    
                    // Calculate speed from samples in window (oldest to newest)
                    double speedMBps = 0;
                    long currentBytes = initialBytes;
                    if (samples.Count >= 2)
                    {
                        var oldest = samples.Peek();
                        // Get newest by converting to array (Queue doesn't have Last())
                        var sampleArray = samples.ToArray();
                        var newest = sampleArray[sampleArray.Length - 1];
                        var timeSpan = (newest.time - oldest.time).TotalSeconds;
                        var bytesDelta = newest.bytes - oldest.bytes;
                        
                        if (timeSpan > 0.5 && bytesDelta > 0) // Need at least 0.5 seconds of data
                        {
                            speedMBps = (bytesDelta / 1024.0 / 1024.0) / timeSpan;
                        }
                        currentBytes = newest.bytes;
                    }
                    else if (samples.Count == 1)
                    {
                        currentBytes = samples.Peek().bytes;
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
            await _dispatcher.Download(archive, output, token, (processed, total) =>
            {
                // Add sample from callback (this is the accurate bytes downloaded)
                samples.Enqueue((DateTime.UtcNow, processed));
            }, null);
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
        }

        // Clear progress line after completion
        ConsoleOutput.ClearProgressLine();

        _logger.LogInformation("Saved file to {Output} in {Seconds:F1}s", output, (DateTime.UtcNow - started).TotalSeconds);
        Console.WriteLine(output.ToString());
        return 0;
    }
}


