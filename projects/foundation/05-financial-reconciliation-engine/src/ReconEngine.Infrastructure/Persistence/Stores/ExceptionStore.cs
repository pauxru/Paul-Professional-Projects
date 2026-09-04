using Microsoft.EntityFrameworkCore;
using ReconEngine.Application.Abstractions;
using ReconEngine.Application.Common;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;

namespace ReconEngine.Infrastructure.Persistence.Stores;

public sealed class ExceptionStore : IExceptionStore
{
    private static readonly ExceptionStatus[] OpenStates =
    {
        ExceptionStatus.Open, ExceptionStatus.Assigned, ExceptionStatus.PendingApproval, ExceptionStatus.Reopened,
    };

    private readonly AppDbContext _db;
    public ExceptionStore(AppDbContext db) => _db = db;

    public async Task AddRangeAsync(IEnumerable<ReconciliationException> exceptions, CancellationToken ct = default)
        => await _db.Exceptions.AddRangeAsync(exceptions, ct);

    public Task RemoveRangeAsync(IEnumerable<ReconciliationException> exceptions, CancellationToken ct = default)
    {
        _db.Exceptions.RemoveRange(exceptions);
        return Task.CompletedTask;
    }

    public Task<ReconciliationException?> GetAsync(Guid id, CancellationToken ct = default)
        => _db.Exceptions
            .Include(x => x.Comments)
            .Include(x => x.AuditTrail)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task<PagedResult<ReconciliationException>> QueryAsync(ExceptionQuery query, CancellationToken ct = default)
    {
        var q = _db.Exceptions.AsQueryable();

        if (query.Status is { } st)
            q = q.Where(x => x.Status == st);
        if (query.Type is { } ty)
            q = q.Where(x => x.Type == ty);
        if (query.Severity is { } sv)
            q = q.Where(x => x.Severity == sv);
        if (!string.IsNullOrWhiteSpace(query.Currency))
            q = q.Where(x => x.Currency == query.Currency);
        if (!string.IsNullOrWhiteSpace(query.AssignedTo))
            q = q.Where(x => x.AssignedTo == query.AssignedTo);

        q = (query.SortBy?.ToLowerInvariant()) switch
        {
            "amount" => query.Descending ? q.OrderByDescending(x => x.AmountMinor) : q.OrderBy(x => x.AmountMinor),
            "severity" => query.Descending ? q.OrderByDescending(x => x.Severity) : q.OrderBy(x => x.Severity),
            "status" => query.Descending ? q.OrderByDescending(x => x.Status) : q.OrderBy(x => x.Status),
            _ => query.Descending ? q.OrderByDescending(x => x.CreatedAtUtc) : q.OrderBy(x => x.CreatedAtUtc),
        };

        var total = await q.CountAsync(ct);
        var items = await q.Skip(query.Skip).Take(query.Take).ToListAsync(ct);

        var page = query.Take <= 0 ? 1 : (query.Skip / query.Take) + 1;
        return new PagedResult<ReconciliationException>(items, page, query.Take, total);
    }

    public async Task<IReadOnlyList<ReconciliationException>> GetOpenAsync(CancellationToken ct = default)
        => await _db.Exceptions
            .Where(x => OpenStates.Contains(x.Status))
            .ToListAsync(ct);
}
