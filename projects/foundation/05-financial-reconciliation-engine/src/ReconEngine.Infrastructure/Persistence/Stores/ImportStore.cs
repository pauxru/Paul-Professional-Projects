using Microsoft.EntityFrameworkCore;
using ReconEngine.Application.Abstractions;
using ReconEngine.Domain.Entities;

namespace ReconEngine.Infrastructure.Persistence.Stores;

public sealed class ImportStore : IImportStore
{
    private readonly AppDbContext _db;
    public ImportStore(AppDbContext db) => _db = db;

    public async Task AddBatchAsync(ImportBatch batch, CancellationToken ct = default)
        => await _db.ImportBatches.AddAsync(batch, ct);

    public async Task AddRecordsAsync(IReadOnlyList<ReconRecord> records, CancellationToken ct = default)
        => await _db.Records.AddRangeAsync(records, ct);

    public Task<ImportBatch?> GetBatchAsync(Guid id, CancellationToken ct = default)
        => _db.ImportBatches.Include(x => x.Rejections).FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<ImportBatch>> ListBatchesAsync(int skip, int take, CancellationToken ct = default)
        => await _db.ImportBatches
            .OrderByDescending(x => x.CreatedAtUtc)
            .Skip(skip).Take(take)
            .ToListAsync(ct);

    public Task<int> CountBatchesAsync(CancellationToken ct = default)
        => _db.ImportBatches.CountAsync(ct);

    public async Task<IReadOnlyList<ImportRejection>> GetRejectionsAsync(Guid batchId, CancellationToken ct = default)
        => await _db.ImportRejections
            .Where(x => x.ImportBatchId == batchId)
            .OrderBy(x => x.LineNumber)
            .ToListAsync(ct);

    public Task<bool> ChecksumExistsAsync(string checksum, CancellationToken ct = default)
        => _db.ImportBatches.AnyAsync(x => x.FileChecksum == checksum, ct);
}
