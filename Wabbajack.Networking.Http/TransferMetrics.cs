using System;
using System.IO;
using System.Threading;
using Wabbajack.Networking.Http.Interfaces;

namespace Wabbajack.Networking.Http;

/// <summary>
/// NIC-based transfer metrics. Reads /proc/net/dev byte counters on a 500ms timer so
/// reported speed is accurate regardless of connection speed or chunk size. Interface is
/// detected via /proc/net/route (default-route interface is unambiguous).
/// </summary>
public class TransferMetrics : ITransferMetrics, IDisposable
{
    private long _totalBytes;
    private readonly string? _iface;
    private long _lastNicBytes;
    private DateTime _lastSampleTime;
    private double _lastInstant;
    private double _ewma;
    private readonly object _lock = new();
    private readonly Timer? _timer;

    public TransferMetrics()
    {
        _iface = DetectDefaultInterface();
        if (_iface != null)
        {
            _lastNicBytes = ReadNicRxBytes(_iface);
            _lastSampleTime = DateTime.UtcNow;
            _timer = new Timer(Sample, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        }
    }

    private void Sample(object? _)
    {
        if (_iface == null) return;

        var nowBytes = ReadNicRxBytes(_iface);
        var now = DateTime.UtcNow;

        lock (_lock)
        {
            var elapsed = (now - _lastSampleTime).TotalSeconds;
            if (elapsed > 0)
            {
                var delta = nowBytes - _lastNicBytes;
                if (delta >= 0) // guard against counter reset or interface restart
                {
                    var instant = delta / elapsed;
                    _lastInstant = instant;
                    const double alpha = 0.3;
                    _ewma = _ewma <= 0 ? instant : (alpha * instant + (1 - alpha) * _ewma);
                }

                _lastNicBytes = nowBytes;
                _lastSampleTime = now;
            }
        }
    }

    // Find the interface that carries the default route by looking for destination 00000000.
    private static string? DetectDefaultInterface()
    {
        try
        {
            var lines = File.ReadAllLines("/proc/net/route");
            foreach (var line in lines)
            {
                var fields = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 2 && fields[1].Trim() == "00000000")
                    return fields[0].Trim();
            }
        }
        catch { /* non-Linux or permission issue */ }
        return null;
    }

    // Read receive-bytes counter for the named interface from /proc/net/dev.
    private static long ReadNicRxBytes(string iface)
    {
        try
        {
            var lines = File.ReadAllLines("/proc/net/dev");
            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith(iface + ":", StringComparison.Ordinal)) continue;

                var data = trimmed.AsSpan(trimmed.IndexOf(':') + 1).TrimStart();
                var spaceIdx = data.IndexOf(' ');
                var token = spaceIdx >= 0 ? data[..spaceIdx] : data;
                if (long.TryParse(token, out var bytes))
                    return bytes;
            }
        }
        catch { }
        return 0;
    }

    public void Record(long bytes)
    {
        if (bytes > 0)
            Interlocked.Add(ref _totalBytes, bytes);
    }

    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public double BytesPerSecond1s
    {
        get { lock (_lock) return _lastInstant; }
    }

    public double BytesPerSecondSmoothed
    {
        get { lock (_lock) return _ewma; }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
