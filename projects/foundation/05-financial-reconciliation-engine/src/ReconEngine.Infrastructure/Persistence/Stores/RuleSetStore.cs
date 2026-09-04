using Microsoft.EntityFrameworkCore;
using ReconEngine.Application.Abstractions;
using ReconEngine.Domain.Entities;

namespace ReconEngine.Infrastructure.Persistence.Stores;

public sealed class RuleSetStore : IRuleSetStore
{
    private readonly AppDbContext _db;
    public RuleSetStore(AppDbContext db) => _db = db;

    public async Task AddAsync(MatchingRuleSet ruleSet, CancellationToken ct = default)
        => await _db.RuleSets.AddAsync(ruleSet, ct);

    public Task<MatchingRuleSet?> GetAsync(Guid id, CancellationToken ct = default)
        => _db.RuleSets.FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<MatchingRuleSet?> GetActiveAsync(CancellationToken ct = default)
        => _db.RuleSets.Where(x => x.IsActive)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(ct);

    public Task<MatchingRuleSet?> GetLatestByNameAsync(string name, CancellationToken ct = default)
        => _db.RuleSets.Where(x => x.Name == name)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<MatchingRuleSet>> ListAsync(CancellationToken ct = default)
        => await _db.RuleSets
            .OrderBy(x => x.Name).ThenByDescending(x => x.Version)
            .ToListAsync(ct);

    public async Task<int> MaxVersionAsync(string name, CancellationToken ct = default)
    {
        var versions = await _db.RuleSets.Where(x => x.Name == name).Select(x => x.Version).ToListAsync(ct);
        return versions.Count == 0 ? 0 : versions.Max();
    }

    public async Task DeactivateAllAsync(CancellationToken ct = default)
    {
        var active = await _db.RuleSets.Where(x => x.IsActive).ToListAsync(ct);
        foreach (var rs in active)
            rs.IsActive = false;
    }
}
