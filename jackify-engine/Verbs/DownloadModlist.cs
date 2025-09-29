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

        // Progress printer: current/total and MB/s like install
        var started = DateTime.UtcNow;
        await _dispatcher.Download(archive, output, token, (processed, total) =>
        {
            var elapsed = DateTime.UtcNow - started;
            var speedMBps = elapsed.TotalSeconds > 0 ? (processed / 1024.0 / 1024.0) / elapsed.TotalSeconds : 0;
            var totalMB = total / 1024.0 / 1024.0;
            var processedMB = processed / 1024.0 / 1024.0;
            ConsoleOutput.PrintProgressWithDuration($"Downloading .wabbajack ({processedMB:F1}/{totalMB:F1}MB) - {speedMBps:F1}MB/s");
        });

        // Clear progress line after completion
        ConsoleOutput.ClearProgressLine();

        _logger.LogInformation("Saved file to {Output} in {Seconds:F1}s", output, (DateTime.UtcNow - started).TotalSeconds);
        Console.WriteLine(output.ToString());
        return 0;
    }
}


