using System;
using System.CommandLine;
using System.CommandLine.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using NLog.Targets;
using Octokit;
using Wabbajack.DTOs.Interventions;
using Wabbajack.Networking.Http;
using Wabbajack.Networking.Http.Interfaces;
using Wabbajack.Paths.IO;
using Wabbajack.Server.Lib;
using Wabbajack.Services.OSIntegrated;
using Wabbajack.VFS;
using Client = Wabbajack.Networking.GitHub.Client;
using Wabbajack.CLI.Builder;
using CG.Web.MegaApiClient;

namespace Wabbajack.CLI;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Catch any exception that escapes the CLI pipeline (e.g. during DI host construction
        // or unobserved task faults). Emit a structured line before the process terminates.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            var msg = ex != null
                ? $"{ex.GetType().Name}: {ex.Message}"
                : "Unknown unhandled exception";
            var ctx = ex?.StackTrace is { } st
                ? new System.Collections.Generic.Dictionary<string, object?> { ["detail"] = st }
                : null;
            Wabbajack.CLI.Builder.StructuredError.WriteError(
                Wabbajack.CLI.Builder.StructuredError.ErrorType.EngineError, msg, ctx);
            Environment.Exit(6);
        };

        // Check for debug mode
        bool debugMode = Array.IndexOf(args, "--debug") >= 0;
        
        // Check for show-file-progress flag (enables FILE_PROGRESS output for Jackify GUI)
        bool showFileProgress = Array.IndexOf(args, "--show-file-progress") >= 0;
        Wabbajack.Common.ConsoleOutput.ShowFileProgress = showFileProgress;

        // Check for disable-gpu-texconv flag (fallback to CPU-only texconv behavior)
        // This allows Jackify to provide an escape hatch if GPU acceleration ever
        // causes hash mismatches or stability issues on specific systems.
        bool disableGpuTexconv = Array.IndexOf(args, "--disable-gpu-texconv") >= 0;
        Wabbajack.Common.TexconvConfig.DisableGpuTexconv = disableGpuTexconv;

        // Remove engine-only flags before passing args to System.CommandLine
        // to avoid unknown option errors in the CLI verbs.
        var filteredArgs = args
            .Where(a => a != "--show-file-progress" && a != "--disable-gpu-texconv")
            .ToArray();
        
        var host = Host.CreateDefaultBuilder(Array.Empty<string>())
            .ConfigureLogging(builder => AddLogging(builder, debugMode))
            .ConfigureServices((host, services) =>
            {
                services.AddSingleton(new JsonSerializerOptions());
                services.AddSingleton<HttpClient, HttpClient>();
                services.AddResumableHttpDownloader();
                services.AddSingleton<IConsole, SystemConsole>();
                services.AddSingleton<CommandLineBuilder, CommandLineBuilder>();
                services.AddSingleton<TemporaryFileManager>();
                services.AddSingleton<FileExtractor.FileExtractor>();
                services.AddSingleton(new ParallelOptions {MaxDegreeOfParallelism = Environment.ProcessorCount});
                services.AddSingleton<Client>();
                services.AddSingleton<Networking.WabbajackClientApi.Client>();
                services.AddSingleton(s => new GitHubClient(new ProductHeaderValue("wabbajack")));
                services.AddSingleton<TemporaryFileManager>();
                services.AddSingleton<MegaApiClient>();
                services.AddSingleton<IUserInterventionHandler, CLIUserInterventionHandler>();

                services.AddOSIntegrated();
                services.AddServerLib();


                services.AddTransient<Context>();
                
                services.AddSingleton<CommandLineBuilder>();
                services.AddCLIVerbs();
            }).Build();

        var service = host.Services.GetService<CommandLineBuilder>();
        return await service!.Run(filteredArgs);
    }
    
    private static void AddLogging(ILoggingBuilder loggingBuilder, bool debugMode = false)
    {
        var config = new NLog.Config.LoggingConfiguration();

        var fileTarget = new FileTarget("file")
        {
            FileName = "logs/wabbajack-cli.current.log",
            ArchiveFileName = "logs/wabbajack-cli.{##}.log",
            ArchiveOldFileOnStartup = true,
            MaxArchiveFiles = 10,
            Layout = "${processtime} [${level:uppercase=true}] (${logger}) ${message:withexception=true}",
            Header = "############ Wabbajack log file - ${longdate} ############"
        };

        var consoleTarget = new ConsoleTarget("console")
        {
            Layout = "${message:withexception=true}",
        };
        
        config.AddRuleForAllLevels(fileTarget);

        // Block Microsoft.Extensions.Http cleanup messages (HttpMessageHandler cleanup cycle spam)
        // These are debug-level messages from HttpClientFactory that spam the logs
        // Use dot notation to match the namespace and set level to Off (blocks all messages)
        // Rules are evaluated in order, so add blocking rules before general rules
        var httpFileBlockRule = new NLog.Config.LoggingRule("Microsoft.Extensions.Http.*", NLog.LogLevel.Off, NLog.LogLevel.Off, fileTarget);
        httpFileBlockRule.Final = true; // Stop processing if this rule matches
        config.LoggingRules.Insert(0, httpFileBlockRule);
        
        var httpConsoleBlockRule = new NLog.Config.LoggingRule("Microsoft.Extensions.Http.*", NLog.LogLevel.Off, NLog.LogLevel.Off, consoleTarget);
        httpConsoleBlockRule.Final = true; // Stop processing if this rule matches
        config.LoggingRules.Insert(1, httpConsoleBlockRule);

        if (debugMode)
        {
            // In debug mode, show all log levels on console
            config.AddRuleForAllLevels(consoleTarget);
        }
        else
        {
            // In non-debug mode, show info, warnings and errors on console
            var consoleRule = new NLog.Config.LoggingRule("*", NLog.LogLevel.Info, NLog.LogLevel.Fatal, consoleTarget);
            config.LoggingRules.Add(consoleRule);
        }

        loggingBuilder.ClearProviders();
        loggingBuilder.SetMinimumLevel(LogLevel.Trace);
        loggingBuilder.AddNLog(config);
    }
}