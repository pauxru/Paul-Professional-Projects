using RagAssistant.Domain.Documents;

namespace RagAssistant.Application.Abstractions;

public interface IDocumentRepository
{
    Task<Document?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Document?> GetByHashAsync(string contentHash, CancellationToken ct);
    Task AddAsync(Document document, CancellationToken ct);
    Task UpdateAsync(Document document, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Document>> ListForUserAsync(UserPrincipal user, int skip, int take, CancellationToken ct);
    Task<int> CountForUserAsync(UserPrincipal user, CancellationToken ct);
}
