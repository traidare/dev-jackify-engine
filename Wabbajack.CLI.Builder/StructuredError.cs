using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Wabbajack.CLI.Builder;

/// <summary>
/// Emits structured JSON error lines to stderr for the Jackify Python frontend.
/// Format: {"je":"1","level":"error","type":"...","message":"...","context":{...}}
/// </summary>
public static class StructuredError
{
    public static class ErrorType
    {
        public const string AuthFailed       = "auth_failed";
        public const string PremiumRequired  = "premium_required";
        public const string NetworkError     = "network_error";
        public const string DiskFull         = "disk_full";
        public const string PermissionDenied = "permission_denied";
        public const string ArchiveCorrupt   = "archive_corrupt";
        public const string FileNotFound     = "file_not_found";
        public const string DownloadStalled  = "download_stalled";
        public const string ValidationFailed = "validation_failed";
        public const string EngineError      = "engine_error";
    }

    /// <summary>
    /// Returns the exit code for a given error type per the jackify-engine structured error spec.
    /// 2=auth, 3=network, 4=disk/IO, 5=validation, 6=engine_error, 1=fallback/unknown.
    /// </summary>
    public static int ExitCodeFor(string type) => type switch
    {
        ErrorType.AuthFailed or ErrorType.PremiumRequired                                => 2,
        ErrorType.NetworkError or ErrorType.DownloadStalled                              => 3,
        ErrorType.DiskFull or ErrorType.PermissionDenied                                 => 4,
        ErrorType.ArchiveCorrupt or ErrorType.ValidationFailed or ErrorType.FileNotFound => 5,
        _                                                                                 => 6,
    };

    /// <summary>Writes a structured error line to stderr.</summary>
    public static void WriteError(string type, string message, Dictionary<string, object?>? context = null)
        => Emit("error", type, message, context);

    /// <summary>Writes a structured warning line to stderr.</summary>
    public static void WriteWarning(string type, string message, Dictionary<string, object?>? context = null)
        => Emit("warning", type, message, context);

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = false };

    private static void Emit(string level, string type, string message, Dictionary<string, object?>? context)
    {
        var obj = new Dictionary<string, object?>
        {
            ["je"]      = "1",
            ["level"]   = level,
            ["type"]    = type,
            ["message"] = message,
        };
        if (context is { Count: > 0 })
            obj["context"] = context;
        Console.Error.WriteLine(JsonSerializer.Serialize(obj, _opts));
    }
}
