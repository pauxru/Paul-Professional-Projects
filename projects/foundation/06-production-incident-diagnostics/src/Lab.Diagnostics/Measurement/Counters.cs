namespace Lab.Diagnostics.Measurement;

public sealed class RequestCounter
{
    private long _started;
    private long _completed;
    private long _failed;

    public long Started => Interlocked.Read(ref _started);

    public long Completed => Interlocked.Read(ref _completed);

    public long Failed => Interlocked.Read(ref _failed);

    public void MarkStarted() => Interlocked.Increment(ref _started);

    public void MarkCompleted() => Interlocked.Increment(ref _completed);

    public void MarkFailed() => Interlocked.Increment(ref _failed);
}

public sealed class OutboundCallCounter
{
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public long Increment() => Interlocked.Increment(ref _count);

    public void Reset() => Interlocked.Exchange(ref _count, 0);
}
