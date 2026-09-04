using ReconEngine.Application.Abstractions;
using ReconEngine.Application.Common;
using ReconEngine.Domain.Abstractions;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.ValueObjects;

namespace ReconEngine.Application.RuleSets;

/// <summary>
/// Manages the lifecycle of data-driven matching rulesets. Rulesets are immutable and versioned:
/// creating a ruleset with an existing name produces a new version rather than mutating the old one,
/// so every run can cite the exact ruleset (name + version) that produced its matches.
/// </summary>
public sealed class RuleSetService
{
    private readonly IRuleSetStore _store;
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;

    public RuleSetService(IRuleSetStore store, IUnitOfWork uow, IClock clock)
    {
        _store = store;
        _uow = uow;
        _clock = clock;
    }

    /// <summary>Creates the next version of a named ruleset and (optionally) activates it.</summary>
    public async Task<MatchingRuleSet> CreateVersionAsync(
        string name,
        MatchingRuleSetDefinition definition,
        string description,
        string createdBy,
        bool activate,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Ruleset name is required.", nameof(name));

        var nextVersion = await _store.MaxVersionAsync(name, ct) + 1;

        var ruleSet = new MatchingRuleSet
        {
            Id = Guid.NewGuid(),
            Name = name,
            Version = nextVersion,
            Description = description,
            CreatedAtUtc = _clock.UtcNow,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? "system" : createdBy,
        };
        ruleSet.SetDefinition(definition);

        if (activate)
        {
            await _store.DeactivateAllAsync(ct);
            ruleSet.IsActive = true;
        }

        await _store.AddAsync(ruleSet, ct);
        await _uow.SaveChangesAsync(ct);
        return ruleSet;
    }

    public async Task<MatchingRuleSet> ActivateAsync(Guid id, CancellationToken ct = default)
    {
        var ruleSet = await _store.GetAsync(id, ct)
            ?? throw new NotFoundException($"Ruleset {id} not found.");

        await _store.DeactivateAllAsync(ct);
        ruleSet.IsActive = true;
        await _uow.SaveChangesAsync(ct);
        return ruleSet;
    }

    public Task<MatchingRuleSet?> GetAsync(Guid id, CancellationToken ct = default) => _store.GetAsync(id, ct);

    public Task<MatchingRuleSet?> GetActiveAsync(CancellationToken ct = default) => _store.GetActiveAsync(ct);

    public Task<IReadOnlyList<MatchingRuleSet>> ListAsync(CancellationToken ct = default) => _store.ListAsync(ct);

    /// <summary>Ensures a default, active ruleset exists; used by seeding and first-run bootstrap.</summary>
    public async Task<MatchingRuleSet> EnsureDefaultAsync(string name, CancellationToken ct = default)
    {
        var existing = await _store.GetActiveAsync(ct);
        if (existing is not null)
            return existing;

        return await CreateVersionAsync(name, MatchingRuleSetDefinition.Default,
            "Seeded default ruleset.", "system", activate: true, ct);
    }
}
