using System;
using System.Collections.Concurrent;
using System.Threading;
using Wabbajack.Networking.Http.Interfaces;

namespace Wabbajack.Networking.Http;

/// <summary>
/// Lightweight, lock-free-ish transfer metrics aggregator. Stores a small ring of recent samples
/// to compute instantaneous rate and an EWMA for stability. Thread-safe.
/// </summary>
public class TransferMetrics : ITransferMetrics
{
    private const int SampleCapacity = 128; // enough for ~few seconds of samples under high concurrency
    private readonly object _lock = new();
    private readonly (long bytes, long ticks)[] _samples = new (long, long)[SampleCapacity];
    private int _head;
    private long _totalBytes;
    private double _ewmaBytesPerSecond;

    public void Record(long bytes)
    {
        if (bytes <= 0) return;
        var nowTicks = DateTime.UtcNow.Ticks;
        Interlocked.Add(ref _totalBytes, bytes);

        lock (_lock)
        {
            _samples[_head] = (bytes, nowTicks);
            _head = (_head + 1) % SampleCapacity;

            // Update EWMA with alpha tuned for ~5s horizon at ~10Hz sampling
            var alpha = 0.2; // conservative smoothing
            var instant = ComputeInstantaneous(nowTicks);
            _ewmaBytesPerSecond = _ewmaBytesPerSecond <= 0 ? instant : (alpha * instant + (1 - alpha) * _ewmaBytesPerSecond);
        }
    }

    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public double BytesPerSecond1s
    {
        get
        {
            lock (_lock)
            {
                return ComputeInstantaneous(DateTime.UtcNow.Ticks);
            }
        }
    }

    public double BytesPerSecondSmoothed
    {
        get
        {
            lock (_lock)
            {
                return _ewmaBytesPerSecond;
            }
        }
    }

    private double ComputeInstantaneous(long nowTicks)
    {
        // Sum samples within the last ~1s window
        long windowTicks = TimeSpan.FromSeconds(1).Ticks;
        long cutoff = nowTicks - windowTicks;
        long bytes = 0;

        for (int i = 0; i < SampleCapacity; i++)
        {
            var (b, t) = _samples[i];
            if (t >= cutoff && b > 0)
            {
                bytes += b;
            }
        }

        return bytes / 1.0; // bytes per second over ~1s window
    }
}


