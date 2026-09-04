using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Common;
using RagAssistant.Domain.Common;
using RagAssistant.Domain.Prompts;

namespace RagAssistant.Application.Prompts;

public sealed record RegisterPromptRequest(string Name, string Version, string Body);

public sealed class PromptService
{
    private readonly IPromptRepository _repository;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public PromptService(IPromptRepository repository, IClock clock, IIdGenerator ids)
    {
        _repository = repository;
        _clock = clock;
        _ids = ids;
    }

    public async Task<PromptTemplate> RegisterAsync(RegisterPromptRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hash = Hashing.ComputePromptHash(request.Body);

        var existing = await _repository.GetByIdentifierAsync(request.Name, request.Version, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.Hash, hash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Prompt {request.Name}@{request.Version} already exists with a different body.");
            }

            return existing;
        }

        var template = new PromptTemplate(
            _ids.NewId(),
            request.Name,
            request.Version,
            request.Body,
            hash,
            _clock.UtcNow,
            isActive: true);

        await _repository.AddAsync(template, ct).ConfigureAwait(false);
        await _repository.DeactivateOthersAsync(request.Name, template.Id, ct).ConfigureAwait(false);
        return template;
    }

    public Task<PromptTemplate?> GetActiveAsync(string name, CancellationToken ct)
        => _repository.GetActiveAsync(name, ct);

    public Task<IReadOnlyList<PromptTemplate>> ListAsync(CancellationToken ct)
        => _repository.ListAsync(ct);
}
