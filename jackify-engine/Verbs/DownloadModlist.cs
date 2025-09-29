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
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths;

namespace Wabbajack.CLI.Verbs;

public class DownloadWabbajackFile
{
    private readonly ILogger<DownloadWabbajackFile> _logger;
    private readonly Client _client;
    private readonly HttpClient _httpClient;

    public DownloadWabbajackFile(ILogger<DownloadWabbajackFile> logger, Client wjClient, HttpClient httpClient)
    {
        _logger = logger;
        _client = wjClient;
        _httpClient = httpClient;
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

        var downloadUrl = list.Links.Download;
        if (string.IsNullOrEmpty(downloadUrl))
        {
            _logger.LogError("Modlist {MachineUrl} does not provide a download link", machineUrl);
            return 1;
        }

        _logger.LogInformation("Downloading .wabbajack from {Url} -> {Output}", downloadUrl, output);
        // Ensure output directory exists (use Wabbajack path helpers)
        output.Parent.CreateDirectory();

        using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(token);
        // Write directly using AbsolutePath helper
        await output.WriteAllAsync(src, token);

        _logger.LogInformation("Saved file to {Output}", output);
        Console.WriteLine(output.ToString());
        return 0;
    }
}


