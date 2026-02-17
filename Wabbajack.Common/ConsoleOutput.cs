using System;

namespace Wabbajack.Common;

public static class ConsoleOutput
{
    /// <summary>
    /// Controls whether FILE_PROGRESS lines are output.
    /// Default is false (suppressed) - set to true via --show-file-progress flag.
    /// Jackify GUI should always pass --show-file-progress to enable detailed file progress.
    /// </summary>
    public static bool ShowFileProgress { get; set; } = false;

    /// <summary>
    /// Prints a message to stdout.
    /// </summary>
    public static void PrintWithDuration(string message)
    {
        Console.WriteLine(message);
    }

    public static void PrintProgressWithDuration(string message)
    {
        try
        {
            // Clear the current line and write the progress message (uses \r to overwrite same line in terminal)
            Console.Write($"\r\x1b[K{message}");
            Console.Out.Flush();
        }
        catch
        {
            var windowWidth = 120;
            try { windowWidth = Console.WindowWidth; } catch { }
            Console.Write($"\r{message.PadRight(windowWidth)}");
            Console.Out.Flush();
        }
    }

    /// <summary>
    /// Clears the current progress line and moves to the next line
    /// Call this after progress updates to ensure clean output
    /// </summary>
    public static void ClearProgressLine()
    {
        try
        {
            // Clear the current line and move to next line
            Console.Write("\r\x1b[K\n");
            Console.Out.Flush();
        }
        catch
        {
            // Fallback if ANSI escape codes aren't supported
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Returns empty string. Kept for call-site compatibility; callers should be cleaned up.
    /// </summary>
    [Obsolete("Timestamp prefix removed - do not use")]
    public static string GetDurationTimestamp() => "";

    /// <summary>
    /// Prints individual file progress in the format expected by Jackify GUI parser.
    /// Format: [FILE_PROGRESS] Operation: filename (percent%) [speed] (completed/total)
    /// Outputs to stderr to keep it separate from normal stdout.
    /// Uses \r for all progress updates (overwrites same line) to prevent console spam.
    /// Only outputs if ShowFileProgress is true (enabled via --show-file-progress flag).
    /// </summary>
    public static void PrintFileProgress(string operation, string filename, double percent, string speed, int? completed = null, int? total = null)
    {
        // Suppress FILE_PROGRESS output unless --show-file-progress flag is set
        // This keeps manual runs clean while allowing Jackify GUI to enable detailed progress
        if (!ShowFileProgress)
            return;
        
        // Format percent - use integer if whole number, otherwise one decimal place
        var percentStr = percent % 1.0 == 0 ? $"{(int)percent}" : $"{percent:F1}";
        
        // Don't show speed for operations that don't involve transfer or don't have meaningful speed data
        var speedPart = (operation == "Checking existing" || operation == "Converting" || operation == "Building") ? "" : $" [{speed}]";
        
        // Add counter if provided (e.g., " (1232/3927753)")
        var counterPart = (completed.HasValue && total.HasValue) ? $" ({completed.Value}/{total.Value})" : "";
        
        var message = $"[FILE_PROGRESS] {operation}: {filename} ({percentStr}%){speedPart}{counterPart}";
        
        // Use \r for ALL progress updates (including Completed) to overwrite same line and prevent console spam
        Console.Error.Write($"\r{message}");
        Console.Error.Flush();
    }
}
