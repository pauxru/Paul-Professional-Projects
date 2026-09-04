namespace Collab.Domain.Crdt;

/// <summary>
/// A Lamport logical clock. Each replica keeps one. It is incremented when the replica generates
/// an operation and fast-forwarded when it observes a remote operation, guaranteeing that an
/// operation's timestamp is strictly greater than every operation that causally precedes it on
/// that replica. This is what lets the causal buffer safely order out-of-order arrivals.
/// </summary>
public sealed class LamportClock
{
    private long _value;

    public LamportClock(long start = 0) => _value = start;

    public long Value => Interlocked.Read(ref _value);

    /// <summary>Increment and return the next timestamp for a locally-generated operation.</summary>
    public long Tick() => Interlocked.Increment(ref _value);

    /// <summary>
    /// Observe a remote timestamp. The local clock jumps to at least the observed value so that the
    /// next locally-generated timestamp is strictly greater than anything seen so far.
    /// </summary>
    public void Observe(long remote)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _value);
            if (remote <= current) return;
        }
        while (Interlocked.CompareExchange(ref _value, remote, current) != current);
    }
}
