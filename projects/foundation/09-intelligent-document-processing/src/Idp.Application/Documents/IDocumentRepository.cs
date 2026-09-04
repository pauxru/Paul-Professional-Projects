using Idp.Application.Abstractions;
using Idp.Application.Validation;
using Idp.Domain.Documents;

namespace Idp.Application.Documents;

/// <summary>Filter for listing documents.</summary>
public sealed record DocumentQuery(
    DocumentType? Type = null,
    PipelineState? State = null,
    int Page = 1,
    int PageSize = 20);

public interface IDocumentRepository
{
    void Add(Document document);
    Task<Document?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Document?> GetByContentHashAsync(string contentHash, CancellationToken ct = default);
    Task<PagedResult<Document>> ListAsync(DocumentQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<Document>> GetAllAsync(CancellationToken ct = default);

    /// <summary>All known (supplier, invoice-number) identities for duplicate detection.</summary>
    Task<IReadOnlyList<ExistingInvoiceKey>> GetInvoiceKeysAsync(CancellationToken ct = default);

    /// <summary>Find a document of a given type whose PO number / reference matches, for 3-way match.</summary>
    Task<Document?> FindByReferenceAsync(
        DocumentType type, string referenceFieldKey, string referenceValue,
        CancellationToken ct = default);
}
