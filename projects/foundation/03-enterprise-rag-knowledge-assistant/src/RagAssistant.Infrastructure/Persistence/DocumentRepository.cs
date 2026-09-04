using Microsoft.EntityFrameworkCore;
using RagAssistant.Application.Abstractions;
using RagAssistant.Domain.Documents;

namespace RagAssistant.Infrastructure.Persistence;

public sealed class DocumentRepository : IDocumentRepository
{
    private readonly RagDbContext _db;

    public DocumentRepository(RagDbContext db)
    {
        _db = db;
    }

    public async Task<Document?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return await _db.Documents
            .AsSplitQuery()
            .Include(d => d.Chunks)
            .FirstOrDefaultAsync(d => d.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task<Document?> GetByHashAsync(string contentHash, CancellationToken ct)
    {
        return await _db.Documents
            .AsSplitQuery()
            .Include(d => d.Chunks)
            .FirstOrDefaultAsync(d => d.ContentHash == contentHash, ct)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(Document document, CancellationToken ct)
    {
        await _db.Documents.AddAsync(document, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Document document, CancellationToken ct)
    {
        _db.Documents.Update(document);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var document = await _db.Documents.FindAsync([id], ct).ConfigureAwait(false);
        if (document is null)
        {
            return;
        }

        _db.Documents.Remove(document);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Document>> ListForUserAsync(UserPrincipal user, int skip, int take, CancellationToken ct)
    {
        var raw = await _db.Documents
            .OrderByDescending(d => d.UpdatedAt)
            .Skip(skip)
            .Take(Math.Min(take, 500))
            .ToArrayAsync(ct)
            .ConfigureAwait(false);
        return raw.Where(d => d.Acl.Allows(user)).ToArray();
    }

    public async Task<int> CountForUserAsync(UserPrincipal user, CancellationToken ct)
    {
        var raw = await _db.Documents.ToArrayAsync(ct).ConfigureAwait(false);
        return raw.Count(d => d.Acl.Allows(user));
    }
}
