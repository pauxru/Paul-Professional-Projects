using System.Diagnostics;

namespace Lab.Diagnostics.Measurement;

public sealed record MemorySnapshot(
    long ManagedHeapBytes,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections)
{
    public static MemorySnapshot Capture(bool forceCollection = false)
    {
        if (forceCollection)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        return new MemorySnapshot(
            GC.GetTotalMemory(forceFullCollection: false),
            GC.GetTotalAllocatedBytes(precise: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }
}

public sealed record MemoryDelta(
    long ManagedHeapBytes,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

public sealed record ThreadPoolSnapshot(
    int AvailableWorkerThreads,
    int MaximumWorkerThreads,
    int BusyWorkerThreads,
    int AvailableCompletionPortThreads,
    int MaximumCompletionPortThreads)
{
    public static ThreadPoolSnapshot Capture()
    {
        ThreadPool.GetAvailableThreads(out var availableWorkers, out var availableCompletionPorts);
        ThreadPool.GetMaxThreads(out var maximumWorkers, out var maximumCompletionPorts);

        return new ThreadPoolSnapshot(
            availableWorkers,
            maximumWorkers,
            maximumWorkers - availableWorkers,
            availableCompletionPorts,
            maximumCompletionPorts);
    }
}

public sealed class ThreadPoolSampler
{
    private readonly object _gate = new();
    private readonly List<ThreadPoolSnapshot> _samples = [];

    public void Sample()
    {
        lock (_gate)
        {
            _samples.Add(ThreadPoolSnapshot.Capture());
        }
    }

    public ThreadPoolSummary Snapshot()
    {
        lock (_gate)
        {
            if (_samples.Count == 0)
            {
                return new ThreadPoolSummary(0, 0, 0, 0);
            }

            return new ThreadPoolSummary(
                _samples.Count,
                _samples.Min(x => x.AvailableWorkerThreads),
                _samples.Max(x => x.BusyWorkerThreads),
                _samples[^1].AvailableWorkerThreads);
        }
    }
}

public sealed record ThreadPoolSummary(
    int SampleCount,
    int MinimumAvailableWorkerThreads,
    int PeakBusyWorkerThreads,
    int EndingAvailableWorkerThreads);

public sealed class MeasurementSession
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public MeasurementSession()
    {
        Before = MemorySnapshot.Capture();
        ThreadPoolBefore = ThreadPoolSnapshot.Capture();
    }

    public MemorySnapshot Before { get; }

    public ThreadPoolSnapshot ThreadPoolBefore { get; }

    public MeasurementOutcome Complete(bool forceCollection = false)
    {
        _stopwatch.Stop();
        var after = MemorySnapshot.Capture(forceCollection);
        var threadPoolAfter = ThreadPoolSnapshot.Capture();

        return new MeasurementOutcome(
            _stopwatch.Elapsed,
            Before,
            after,
            new MemoryDelta(
                after.ManagedHeapBytes - Before.ManagedHeapBytes,
                after.AllocatedBytes - Before.AllocatedBytes,
                after.Gen0Collections - Before.Gen0Collections,
                after.Gen1Collections - Before.Gen1Collections,
                after.Gen2Collections - Before.Gen2Collections),
            ThreadPoolBefore,
            threadPoolAfter);
    }
}

public sealed record MeasurementOutcome(
    TimeSpan Elapsed,
    MemorySnapshot Before,
    MemorySnapshot After,
    MemoryDelta Delta,
    ThreadPoolSnapshot ThreadPoolBefore,
    ThreadPoolSnapshot ThreadPoolAfter);
