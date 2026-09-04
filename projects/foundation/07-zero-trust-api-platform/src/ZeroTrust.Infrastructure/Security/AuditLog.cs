using Microsoft.EntityFrameworkCore;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Audit;
using ZeroTrust.Infrastructure.Persistence;

namespace ZeroTrust.Infrastructure.Security;

public sealed class AuditLog : IAuditLog
{
    private readonly ZeroTrustDbContext _db;
    private readonly IClock _clock;
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private const string Genesis = "GENESIS";

    public AuditLog(ZeroTrustDbContext db, IClock clock) { _db = db; _clock = clock; }

    public async Task<AuditRecord> AppendAsync(AuditKind kind, string actor, string action, string resource,
        string correlationId, string sourceIp, string userAgent, string detail, bool allowed, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var last = await _db.AuditRecords.OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
            var seq = (last?.Sequence ?? 0) + 1;
            var previousHash = last?.Hash ?? Genesis;
            var now = _clock.UtcNow;
            // Use ticks (a long) to avoid ISO-8601 formatting differences and DateTimeKind
            // being lost when EF Sqlite round-trips DateTime.
            var payload = $"{seq}|{kind}|{actor}|{action}|{resource}|{correlationId}|{sourceIp}|{userAgent}|{detail}|{allowed}|{now.Ticks}|{previousHash}";
            var hash = SecretHasher.Sha256Hex(payload);
            var record = new AuditRecord(seq, kind, actor ?? string.Empty, action ?? string.Empty, resource ?? string.Empty,
                correlationId ?? string.Empty, sourceIp ?? string.Empty, userAgent ?? string.Empty,
                detail ?? string.Empty, allowed, previousHash, hash, now);
            _db.AuditRecords.Add(record);
            await _db.SaveChangesAsync(ct);
            return record;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool ok, string? failingRow)> VerifyChainAsync(CancellationToken ct)
    {
        var rows = await _db.AuditRecords.OrderBy(x => x.Sequence).AsNoTracking().ToListAsync(ct);
        var previousHash = Genesis;
        foreach (var r in rows)
        {
            var payload = $"{r.Sequence}|{r.Kind}|{r.Actor}|{r.Action}|{r.Resource}|{r.CorrelationId}|{r.SourceIp}|{r.UserAgent}|{r.Detail}|{r.Allowed}|{r.CreatedAtUtc.Ticks}|{previousHash}";
            var expected = SecretHasher.Sha256Hex(payload);
            if (!string.Equals(expected, r.Hash, StringComparison.Ordinal))
                return (false, $"seq={r.Sequence}");
            if (!string.Equals(previousHash, r.PreviousHash, StringComparison.Ordinal))
                return (false, $"seq={r.Sequence} (previous-hash mismatch)");
            previousHash = r.Hash;
        }
        return (true, null);
    }
}
