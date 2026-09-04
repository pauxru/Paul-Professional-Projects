using Microsoft.EntityFrameworkCore;
using ReconEngine.Application.Abstractions;
using ReconEngine.Application.Common;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;

namespace ReconEngine.Infrastructure.Persistence.Stores;

public sealed class RecordStore : IRecordStore
{
    private readonly AppDbContext _db;
    public RecordStore(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<ReconRecord>> GetWorkingSetAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var q = _db.Records.Where(r => r.ReconStatus != ReconStatus.Matched);
        if (from is { } f)
            q = q.Where(r => r.ValueDate >= f);
        if (to is { } t)
            q = q.Where(r => r.ValueDate <= t);
        return await q.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ReconRecord>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return Array.Empty<ReconRecord>();
        var set = ids.ToHashSet();
        return await _db.Records.Where(r => set.Contains(r.Id)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ReconRecord>> GetByLastRunAsync(Guid runId, CancellationToken ct = default)
        => await _db.Records.Where(r => r.LastRunId == runId).ToListAsync(ct);

    public async Task<PagedResult<ReconRecord>> QueryAsync(RecordQuery query, CancellationToken ct = default)
    {
        var q = _db.Records.AsQueryable();
        if (query.Source is { } src)
            q = q.Where(r => r.Source == src);
        if (!string.IsNullOrWhiteSpace(query.Currency))
            q = q.Where(r => r.Currency == query.Currency);
        if (query.Status is { } st)
            q = q.Where(r => r.ReconStatus == st);

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(r => r.ValueDate).ThenBy(r => r.Id)
            .Skip(query.Skip).Take(query.Take)
            .ToListAsync(ct);

        var page = query.Take <= 0 ? 1 : (query.Skip / query.Take) + 1;
        return new PagedResult<ReconRecord>(items, page, query.Take, total);
    }
}
