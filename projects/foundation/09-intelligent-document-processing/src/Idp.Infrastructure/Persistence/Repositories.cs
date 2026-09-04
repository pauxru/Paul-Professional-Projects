using Idp.Application.Abstractions;
using Idp.Application.Exporting;
using Idp.Application.Review;
using Idp.Application.Suppliers;
using Idp.Domain.Audit;
using Idp.Domain.Exports;
using Idp.Domain.Review;
using Idp.Domain.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace Idp.Infrastructure.Persistence;

public sealed class SupplierRepository : ISupplierRepository
{
    private readonly IdpDbContext _db;
    public SupplierRepository(IdpDbContext db) => _db = db;

    public void Add(Supplier supplier) => _db.Suppliers.Add(supplier);

    public Task<Supplier?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.Suppliers.Include(s => s.Hints).FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<Supplier>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Suppliers.Include(s => s.Hints).AsSplitQuery().ToListAsync(ct);
}

public sealed class ReviewRepository : IReviewRepository
{
    private readonly IdpDbContext _db;
    public ReviewRepository(IdpDbContext db) => _db = db;

    public void Add(ReviewTask task) => _db.ReviewTasks.Add(task);
    public void AddCorrection(Correction correction) => _db.Corrections.Add(correction);

    public Task<ReviewTask?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.ReviewTasks.FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<ReviewTask?> GetByDocumentAsync(Guid documentId, CancellationToken ct = default) =>
        _db.ReviewTasks.FirstOrDefaultAsync(r => r.DocumentId == documentId, ct);

    public async Task<IReadOnlyList<ReviewTask>> GetQueueAsync(int limit, CancellationToken ct = default) =>
        await _db.ReviewTasks
            .Where(r => r.Status != ReviewStatus.Completed)
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Correction>> GetCorrectionsAsync(
        Guid documentId, CancellationToken ct = default) =>
        await _db.Corrections
            .Where(c => c.DocumentId == documentId)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(ct);

    public Task<int> CountOpenAsync(CancellationToken ct = default) =>
        _db.ReviewTasks.CountAsync(r => r.Status != ReviewStatus.Completed, ct);
}

public sealed class ExportRepository : IExportRepository
{
    private readonly IdpDbContext _db;
    public ExportRepository(IdpDbContext db) => _db = db;

    public void Add(ExportRecord record) => _db.ExportRecords.Add(record);

    public Task<ExportRecord?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.ExportRecords.FirstOrDefaultAsync(e => e.Id == id, ct);

    public Task<ExportRecord?> GetByDocumentAsync(Guid documentId, CancellationToken ct = default) =>
        _db.ExportRecords.FirstOrDefaultAsync(e => e.DocumentId == documentId, ct);

    public async Task<IReadOnlyList<ExportRecord>> ListAsync(
        ExportStatus? status, CancellationToken ct = default)
    {
        var q = _db.ExportRecords.AsQueryable();
        if (status is { } s) q = q.Where(e => e.Status == s);
        return await q.OrderByDescending(e => e.UpdatedAtUtc).ToListAsync(ct);
    }
}

public sealed class AuditRepository : IAuditRepository
{
    private readonly IdpDbContext _db;
    public AuditRepository(IdpDbContext db) => _db = db;

    public void Add(AuditEntry entry) => _db.AuditEntries.Add(entry);

    public async Task<IReadOnlyList<AuditEntry>> RecentAsync(int limit, CancellationToken ct = default) =>
        await _db.AuditEntries
            .OrderByDescending(a => a.TimestampUtc)
            .Take(limit)
            .ToListAsync(ct);
}

public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly IdpDbContext _db;
    public EfUnitOfWork(IdpDbContext db) => _db = db;

    public Task<int> SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
