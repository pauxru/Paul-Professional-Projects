using Idp.Application.Abstractions;
using Idp.Application.Documents;
using Idp.Application.Extraction;
using Idp.Application.Validation;
using Idp.Domain.Documents;
using Microsoft.EntityFrameworkCore;

namespace Idp.Infrastructure.Persistence;

public sealed class DocumentRepository : IDocumentRepository
{
    private readonly IdpDbContext _db;
    public DocumentRepository(IdpDbContext db) => _db = db;

    public void Add(Document document) => _db.Documents.Add(document);

    public Task<Document?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.Documents
            .Include(d => d.Fields)
            .Include(d => d.LineItems)
            .Include(d => d.Transitions)
            .Include(d => d.Validations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(d => d.Id == id, ct);

    public Task<Document?> GetByContentHashAsync(string contentHash, CancellationToken ct = default) =>
        _db.Documents.FirstOrDefaultAsync(d => d.ContentHash == contentHash, ct);

    public async Task<PagedResult<Document>> ListAsync(
        DocumentQuery query, CancellationToken ct = default)
    {
        var q = _db.Documents.AsQueryable();
        if (query.Type is { } type) q = q.Where(d => d.DocumentType == type);
        if (query.State is { } state) q = q.Where(d => d.State == state);

        var total = await q.CountAsync(ct);
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 100);
        var items = await q
            .OrderByDescending(d => d.CreatedAtUtc)
            .Skip((page - 1) * size)
            .Take(size)
            .Include(d => d.Validations)
            .AsSplitQuery()
            .ToListAsync(ct);
        return new PagedResult<Document>(items, page, size, total);
    }

    public async Task<IReadOnlyList<Document>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Documents
            .Include(d => d.Fields)
            .Include(d => d.LineItems)
            .Include(d => d.Validations)
            .AsSplitQuery()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ExistingInvoiceKey>> GetInvoiceKeysAsync(
        CancellationToken ct = default)
    {
        var invoices = await _db.Documents
            .Where(d => d.DocumentType == DocumentType.Invoice)
            .Include(d => d.Fields)
            .AsSplitQuery()
            .ToListAsync(ct);

        var keys = new List<ExistingInvoiceKey>();
        foreach (var doc in invoices)
        {
            var number = doc.Text(FieldKeys.InvoiceNumber);
            if (string.IsNullOrWhiteSpace(number)) continue;
            keys.Add(new ExistingInvoiceKey(
                DuplicateInvoiceRule.SupplierKey(doc), number, doc.Id));
        }
        return keys;
    }

    public async Task<Document?> FindByReferenceAsync(
        DocumentType type, string referenceFieldKey, string referenceValue,
        CancellationToken ct = default)
    {
        var docId = await _db.ExtractedFields
            .Where(f => f.FieldKey == referenceFieldKey &&
                        (f.NormalizedValue == referenceValue || f.RawValue == referenceValue))
            .Join(_db.Documents.Where(d => d.DocumentType == type),
                f => f.DocumentId, d => d.Id, (f, d) => d.Id)
            .FirstOrDefaultAsync(ct);

        return docId == Guid.Empty ? null : await GetAsync(docId, ct);
    }
}
