using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Wabbajack.RateLimiter;

public class Resource<T> : IResource<T>
{
    private Channel<PendingReport> _channel;
    private SemaphoreSlim _semaphore;
    private readonly ConcurrentDictionary<ulong, Job<T>> _tasks;
    private ulong _nextId;
    private long _totalUsed;
    public IEnumerable<IJob> Jobs => _tasks.Values;

    public Resource(string? humanName = null, int? maxTasks = null, long maxThroughput = long.MaxValue, CancellationToken? token = null)
    {
        Name = humanName ?? "<unknown>";
        MaxTasks = maxTasks ?? Environment.ProcessorCount;
        MaxThroughput = maxThroughput;
        _semaphore = new SemaphoreSlim(MaxTasks);
        _channel = Channel.CreateBounded<PendingReport>(10);
        _tasks = new ConcurrentDictionary<ulong, Job<T>>();

        var tsk = StartTask(token ?? CancellationToken.None);
    }

    public Resource(string humanName, Func<Task<(int MaxTasks, long MaxThroughput)>> settingGetter, CancellationToken? token = null)
    {
        Name = humanName;
        _tasks = new ConcurrentDictionary<ulong, Job<T>>();
        
        // Initialize with default values to prevent race condition
        MaxTasks = Environment.ProcessorCount;
        MaxThroughput = long.MaxValue;
        _semaphore = new SemaphoreSlim(MaxTasks);
        _channel = Channel.CreateBounded<PendingReport>(10);
        
        // Start the task to update settings asynchronously
        Task.Run(async () =>
        {
            try
            {
                var (maxTasks, maxThroughput) = await settingGetter();
                MaxTasks = maxTasks;
                MaxThroughput = maxThroughput;
                
                // Create new semaphore with updated settings
                var oldSemaphore = _semaphore;
                _semaphore = new SemaphoreSlim(MaxTasks);
                
                // Dispose old semaphore
                oldSemaphore?.Dispose();
                
                // Start the monitoring task (uses existing channel - no need to replace it)
                await StartTask(token ?? CancellationToken.None);
            }
            catch (Exception)
            {
                // If settings loading fails, keep using default values
                // The resource will still work with fallback settings
                
                // Start the monitoring task with default settings
                await StartTask(token ?? CancellationToken.None);
            }
        }, token ?? CancellationToken.None);
    }

    public int MaxTasks { get; set; }
    public long MaxThroughput { get; set; }
    public string Name { get; }

    public async ValueTask<Job<T>> Begin(string jobTitle, long size, CancellationToken token)
    {
        // Ensure semaphore is initialized
        if (_semaphore == null)
        {
            // Fallback initialization if somehow semaphore is still null
            _semaphore = new SemaphoreSlim(Environment.ProcessorCount);
        }
        
        var id = Interlocked.Increment(ref _nextId);
        var job = new Job<T>
        {
            ID = id,
            Description = jobTitle,
            Size = size,
            Resource = this
        };
        _tasks.TryAdd(id, job);
        await _semaphore.WaitAsync(token);
        job.Started = true;
        return job;
    }

    public void ReportNoWait(Job<T> job, int processedSize)
    {
        job.Current += processedSize;
        Interlocked.Add(ref _totalUsed, processedSize);
    }

    public void Finish(Job<T> job)
    {
        _semaphore.Release();
        _tasks.TryRemove(job.ID, out _);
    }

    public async ValueTask Report(Job<T> job, int size, CancellationToken token)
    {
        var tcs = new TaskCompletionSource();
        await _channel.Writer.WriteAsync(new PendingReport
        {
            Job = job,
            Size = size,
            Result = tcs
        }, token);
        await tcs.Task;
    }

    public StatusReport StatusReport =>
        new(_tasks.Count(t => t.Value.Started),
            _tasks.Count(t => !t.Value.Started),
            _totalUsed);

    private async ValueTask StartTask(CancellationToken token)
    {
        // Token bucket for cumulative throughput throttling across all downloads
        long availableTokens = 0;
        DateTime lastTokenUpdate = DateTime.UtcNow;

        await foreach (var item in _channel.Reader.ReadAllAsync(token))
        {
            Interlocked.Add(ref _totalUsed, item.Size);
            if (MaxThroughput is long.MaxValue or 0)
            {
                item.Result.TrySetResult();
                continue;
            }

            // Token bucket algorithm: cumulative bandwidth throttling
            var now = DateTime.UtcNow;
            var elapsed = now - lastTokenUpdate;
            
            // Add tokens based on elapsed time (MaxThroughput bytes per second)
            if (elapsed.TotalSeconds > 0)
            {
                var tokensToAdd = (long)(MaxThroughput * elapsed.TotalSeconds);
                availableTokens = Math.Min(availableTokens + tokensToAdd, MaxThroughput * 2); // Cap at 2x to allow bursts
                lastTokenUpdate = now;
            }
            
            // Consume tokens for this chunk
            if (availableTokens >= item.Size)
            {
                // Enough tokens available, proceed immediately
                availableTokens -= item.Size;
                item.Result.TrySetResult();
            }
            else
            {
                // Not enough tokens, calculate delay needed to accumulate enough
                var tokensNeeded = item.Size - availableTokens;
                var delaySeconds = (double)tokensNeeded / MaxThroughput;
                var delay = TimeSpan.FromSeconds(delaySeconds);
                
                // Delay to accumulate enough tokens
                await Task.Delay(delay, token);
                
                // After delay, update time and add accumulated tokens
                var afterDelay = DateTime.UtcNow;
                var delayElapsed = afterDelay - lastTokenUpdate;
                if (delayElapsed.TotalSeconds > 0)
                {
                    var tokensAccumulated = (long)(MaxThroughput * delayElapsed.TotalSeconds);
                    availableTokens = Math.Min(availableTokens + tokensAccumulated, MaxThroughput * 2);
                    lastTokenUpdate = afterDelay;
                }
                
                // Now consume the tokens
                availableTokens -= item.Size;
                item.Result.TrySetResult();
            }
        }
    }

    private struct PendingReport
    {
        public Job<T> Job { get; set; }
        public int Size { get; set; }
        public TaskCompletionSource Result { get; set; }
    }
}