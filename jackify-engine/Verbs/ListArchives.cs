using System;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.CLI.Builder;
using Wabbajack.DTOs;
using Wabbajack.DTOs.DownloadStates;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.Installer;
using Wabbajack.Paths;

namespace Wabbajack.CLI.Verbs;

public class ListArchives
{
    private readonly ILogger<ListArchives> _logger;
    private readonly DTOSerializer _dtos;

    public ListArchives(ILogger<ListArchives> logger, DTOSerializer dtos)
    {
        _logger = logger;
        _dtos = dtos;
    }

    public static VerbDefinition Definition = new("list-archives",
        "Lists archives in a .wabbajack with display name and URL",
        new[]
        {
            new OptionDefinition(typeof(AbsolutePath), "i", "input", "Input .wabbajack file (required)"),
            new OptionDefinition(typeof(string), "s", "search", "Optional case-insensitive filter on display or file name"),
            new OptionDefinition(typeof(string), "f", "format", "Output format: text|json (default: text)")
        });

    internal async Task<int> Run(AbsolutePath input, string? search, string? format, CancellationToken token)
    {
        if (input == AbsolutePath.Empty)
        {
            _logger.LogError("Input .wabbajack path is required");
            return 1;
        }

        var modlist = await StandardInstaller.LoadFromFile(_dtos, input);

        var archives = modlist.Archives.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            archives = archives.Where(a => a.Name.Contains(search!, StringComparison.OrdinalIgnoreCase) ||
                                           (a.State is IMetaState ms && (ms.Name?.Contains(search!, StringComparison.OrdinalIgnoreCase) ?? false)));
        }

        var rows = archives.Select(a => new
        {
            displayName = a.State is IMetaState ms && !string.IsNullOrEmpty(ms.Name) ? ms.Name : a.Name,
            fileName = a.Name,
            url = a.State switch
            {
                IMetaState ims => ims.LinkUrl,
                Manual m => m.Url,
                Http h => h.Url,
                Mega me => me.Url,
                MediaFire mf => mf.Url,
                _ => null
            },
            hash = a.Hash.ToString(),
            size = a.Size,
            sourceType = a.State.GetType().Name
        }).ToArray();

        var fmt = (format ?? "text").ToLowerInvariant();
        if (fmt == "json")
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            foreach (var r in rows)
            {
                Console.WriteLine($"- {r.displayName}");
                Console.WriteLine($"  File: {r.fileName}");
                Console.WriteLine($"  URL: {(r.url?.ToString() ?? "<none>")}");
            }
        }

        if (!rows.Any() && !string.IsNullOrWhiteSpace(search))
            return 2; // no matches

        return 0;
    }
}
