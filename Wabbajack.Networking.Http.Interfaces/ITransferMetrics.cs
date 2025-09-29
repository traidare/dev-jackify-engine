using System;

namespace Wabbajack.Networking.Http.Interfaces;

public interface ITransferMetrics
{
    /// <summary>
    /// Record a number of bytes just written/read by the application.
    /// </summary>
    /// <param name="bytes">Number of bytes transferred.</param>
    void Record(long bytes);

    /// <summary>
    /// Total bytes recorded since process start.
    /// </summary>
    long TotalBytes { get; }

    /// <summary>
    /// Approximate instantaneous bytes/sec over the last window.
    /// </summary>
    double BytesPerSecond1s { get; }

    /// <summary>
    /// Smoothed bytes/sec over ~5 seconds (EWMA).
    /// </summary>
    double BytesPerSecondSmoothed { get; }
}


