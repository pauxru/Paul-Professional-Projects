namespace JobScheduler.Domain.Entities;

/// <summary>
/// The single-row leader lease. Exactly one node may hold it at a time. The lease is time-boxed
/// (TTL) and extended by heartbeat; a monotonic <see cref="FencingToken"/> guarantees that a
/// revived old leader cannot act after a new leader has taken over (split-brain protection).
/// </summary>
public sealed class LeaderLease
{
    /// <summary>There is only ever one leadership row, keyed by this constant.</summary>
    public const string SingletonKey = "scheduler-leader";

    private LeaderLease() { }

    public string Key { get; private set; } = SingletonKey;
    public string? Owner { get; private set; }
    public Guid Token { get; private set; }
    public long FencingToken { get; private set; }
    public DateTimeOffset? AcquiredAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public int Version { get; private set; }

    public static LeaderLease CreateVacant() => new()
    {
        Key = SingletonKey,
        Owner = null,
        Token = Guid.Empty,
        FencingToken = 0,
        ExpiresAt = DateTimeOffset.MinValue
    };

    public bool IsHeld(DateTimeOffset now) => Owner is not null && ExpiresAt > now;

    public bool IsHeldBy(string nodeId, DateTimeOffset now) =>
        IsHeld(now) && string.Equals(Owner, nodeId, StringComparison.Ordinal);

    /// <summary>
    /// Acquires (or takes over an expired) lease for <paramref name="nodeId"/>. Only valid when the
    /// lease is vacant or expired; bumps the fencing token so any prior leader is fenced out.
    /// </summary>
    public void Acquire(string nodeId, Guid token, DateTimeOffset now, TimeSpan ttl)
    {
        if (IsHeld(now) && !string.Equals(Owner, nodeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Leader lease is currently held by another node.");
        }

        Owner = Guard.NotBlank(nodeId, nameof(nodeId));
        Token = token;
        FencingToken++;
        AcquiredAt = now;
        ExpiresAt = now + ttl;
        Version++;
    }

    /// <summary>Extends the lease if held by this node with a matching token.</summary>
    public bool Renew(string nodeId, Guid token, DateTimeOffset now, TimeSpan ttl)
    {
        if (Owner != nodeId || Token != token || ExpiresAt <= now)
        {
            return false;
        }
        ExpiresAt = now + ttl;
        Version++;
        return true;
    }

    public void Release(string nodeId, Guid token)
    {
        if (Owner == nodeId && Token == token)
        {
            Owner = null;
            Token = Guid.Empty;
            ExpiresAt = DateTimeOffset.MinValue;
            Version++;
        }
    }
}
