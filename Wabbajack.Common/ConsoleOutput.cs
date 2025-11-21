using System;

namespace Wabbajack.Common;

public static class ConsoleOutput
{
    private static readonly DateTime _startTime = DateTime.UtcNow;

    /// <summary>
    /// Prints a message with a duration timestamp (elapsed time since program start)
    /// Format: [HH:MM:SS] message
    /// </summary>
    public static void PrintWithDuration(string message)
    {
        var elapsed = DateTime.UtcNow - _startTime;
        Console.WriteLine($"[{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}] {message}");
    }

    public static void PrintProgressWithDuration(string message)
    {
        var elapsed = DateTime.UtcNow - _startTime;
        var timestamped = $"[{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}] {message}";
        
        try
        {
            // Clear the current line and write the progress message (uses \r to overwrite same line)
            Console.Write($"\r\x1b[K{timestamped}");
            Console.Out.Flush();
        }
        catch
        {
            // Fallback if ANSI escape codes aren't supported
            // Pad with spaces to clear the line, then return to start with \r
            var windowWidth = 120; // Default fallback width
            try { windowWidth = Console.WindowWidth; } catch { }
            Console.Write($"\r{timestamped.PadRight(windowWidth)}");
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
    /// Gets the current duration timestamp as a string
    /// Format: [HH:MM:SS]
    /// </summary>
    public static string GetDurationTimestamp()
    {
        var elapsed = DateTime.UtcNow - _startTime;
        return $"[{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}]";
    }

    /// <summary>
    /// Prints individual file progress in the format expected by Jackify GUI parser.
    /// Format: [FILE_PROGRESS] Operation: filename (percent%) [speed] (completed/total)
    /// Outputs to stderr to keep it separate from normal stdout.
    /// Uses \r for all progress updates (overwrites same line) to prevent console spam.
    /// </summary>
    public static void PrintFileProgress(string operation, string filename, double percent, string speed, int? completed = null, int? total = null)
    {
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
