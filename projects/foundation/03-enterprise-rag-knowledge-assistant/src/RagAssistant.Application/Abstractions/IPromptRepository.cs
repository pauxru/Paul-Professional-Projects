using RagAssistant.Domain.Prompts;

namespace RagAssistant.Application.Abstractions;

public interface IPromptRepository
{
    Task<PromptTemplate?> GetActiveAsync(string name, CancellationToken ct);
    Task<PromptTemplate?> GetByIdentifierAsync(string name, string version, CancellationToken ct);
    Task<IReadOnlyList<PromptTemplate>> ListAsync(CancellationToken ct);
    Task AddAsync(PromptTemplate template, CancellationToken ct);
    Task DeactivateOthersAsync(string name, Guid activeId, CancellationToken ct);
}
