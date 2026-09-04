using Microsoft.EntityFrameworkCore;
using RagAssistant.Application.Abstractions;
using RagAssistant.Domain.Prompts;

namespace RagAssistant.Infrastructure.Persistence;

public sealed class PromptRepository : IPromptRepository
{
    private readonly RagDbContext _db;

    public PromptRepository(RagDbContext db)
    {
        _db = db;
    }

    public async Task<PromptTemplate?> GetActiveAsync(string name, CancellationToken ct)
    {
        return await _db.Prompts
            .Where(p => p.Name == name && p.IsActive)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<PromptTemplate?> GetByIdentifierAsync(string name, string version, CancellationToken ct)
    {
        return await _db.Prompts
            .FirstOrDefaultAsync(p => p.Name == name && p.Version == version, ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PromptTemplate>> ListAsync(CancellationToken ct)
    {
        return await _db.Prompts
            .OrderBy(p => p.Name)
            .ThenByDescending(p => p.CreatedAt)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(PromptTemplate template, CancellationToken ct)
    {
        await _db.Prompts.AddAsync(template, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeactivateOthersAsync(string name, Guid activeId, CancellationToken ct)
    {
        var others = await _db.Prompts
            .Where(p => p.Name == name && p.Id != activeId && p.IsActive)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        foreach (var other in others)
        {
            other.Deactivate();
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
