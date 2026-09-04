using Microsoft.EntityFrameworkCore;
using ReconEngine.Application.Abstractions;
using ReconEngine.Application.Common;
using ReconEngine.Domain.Entities;

namespace ReconEngine.Infrastructure.Persistence.Stores;

public sealed class RunStore : IRunStore
{
    private readonly AppDbContext _db;
    public RunStore(AppDbContext db) => _db = db;

    public async Task AddAsync(ReconciliationRun run, CancellationToken ct = default)
        => await _db.Runs.AddAsync(run, ct);

    public Task<ReconciliationRun?> GetAsync(Guid id, CancellationToken ct = default)
        => _db.Runs.FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task<PagedResult<ReconciliationRun>> ListAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var total = await _db.Runs.CountAsync(ct);
        var items = await _db.Runs
            .OrderByDescending(x => x.StartedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        return new PagedResult<ReconciliationRun>(items, page, pageSize, total);
    }
}

public sealed class MatchStore : IMatchStore
{
    private readonly AppDbContext _db;
    public MatchStore(AppDbContext db) => _db = db;

    public async Task AddRangeAsync(IEnumerable<Match> matches, CancellationToken ct = default)
        => await _db.Matches.AddRangeAsync(matches, ct);

    public async Task RemoveForRecordsAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken ct = default)
    {
        if (recordIds.Count == 0)
            return;
        var set = recordIds.ToHashSet();

        // A match is stale if any of its entries references a record in the working set.
        var matchIds = await _db.MatchEntries
            .Where(e => set.Contains(e.RecordId))
            .Select(e => e.MatchId)
            .Distinct()
            .ToListAsync(ct);

        if (matchIds.Count == 0)
            return;

        var matches = await _db.Matches
            .Include(m => m.Entries)
            .Where(m => matchIds.Contains(m.Id))
            .ToListAsync(ct);

        _db.Matches.RemoveRange(matches);
    }

    public async Task<IReadOnlyList<Match>> GetByRunAsync(Guid runId, CancellationToken ct = default)
        => await _db.Matches
            .Include(m => m.Entries)
            .Where(m => m.RunId == runId)
            .ToListAsync(ct);
}
