using FraudPipeline.Application.Abstractions;
using FraudPipeline.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FraudPipeline.Infrastructure.Persistence;

public sealed class TransactionRepository : ITransactionRepository
{
    private readonly FraudDbContext _db;
    public TransactionRepository(FraudDbContext db) => _db = db;

    public async Task AddAsync(Transaction transaction, CancellationToken ct = default)
        => await _db.Transactions.AddAsync(transaction, ct);

    public Task<Transaction?> GetByRefAsync(string transactionRef, CancellationToken ct = default)
        => _db.Transactions.FirstOrDefaultAsync(t => t.TransactionRef == transactionRef, ct);

    public Task<Transaction?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _db.Transactions.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<IReadOnlyList<Transaction>> ListAsync(int page, int pageSize, CancellationToken ct = default)
        => await _db.Transactions.OrderByDescending(t => t.OccurredAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

    public Task<int> CountAsync(CancellationToken ct = default)
        => _db.Transactions.CountAsync(ct);

    public async Task<IReadOnlyList<Transaction>> ListRecentByCustomerAsync(string customerId, int limit, CancellationToken ct = default)
        => await _db.Transactions
            .Where(t => t.CustomerId == customerId)
            .OrderByDescending(t => t.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Transaction>> ListAllForReplayAsync(CancellationToken ct = default)
        => await _db.Transactions.OrderBy(t => t.OccurredAt).ToListAsync(ct);

    public Task SaveAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public sealed class ScoringDecisionRepository : IScoringDecisionRepository
{
    private readonly FraudDbContext _db;
    public ScoringDecisionRepository(FraudDbContext db) => _db = db;

    public async Task AddAsync(ScoringDecision decision, CancellationToken ct = default)
        => await _db.Decisions.AddAsync(decision, ct);

    public Task<ScoringDecision?> GetByTransactionRefAsync(string transactionRef, bool shadow, CancellationToken ct = default)
        => _db.Decisions.FirstOrDefaultAsync(d => d.TransactionRef == transactionRef && d.Shadow == shadow, ct);

    public async Task<IReadOnlyList<ScoringDecision>> ListForTransactionAsync(Guid transactionId, CancellationToken ct = default)
        => await _db.Decisions.Where(d => d.TransactionId == transactionId).OrderByDescending(d => d.DecidedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<ScoringDecision>> ListRecentAsync(int limit, bool shadow, CancellationToken ct = default)
        => await _db.Decisions.Where(d => d.Shadow == shadow)
            .OrderByDescending(d => d.DecidedAt).Take(limit).ToListAsync(ct);

    public Task SaveAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public sealed class RulesetRepository : IRulesetRepository
{
    private readonly FraudDbContext _db;
    public RulesetRepository(FraudDbContext db) => _db = db;

    public Task<Ruleset?> GetActiveAsync(CancellationToken ct = default)
        => _db.Rulesets.FirstOrDefaultAsync(r => r.IsActive, ct);

    public Task<Ruleset?> GetShadowAsync(CancellationToken ct = default)
        => _db.Rulesets.FirstOrDefaultAsync(r => r.IsShadow, ct);

    public Task<Ruleset?> GetByVersionAsync(string version, CancellationToken ct = default)
        => _db.Rulesets.FirstOrDefaultAsync(r => r.Version == version, ct);

    public async Task<IReadOnlyList<Ruleset>> ListAsync(CancellationToken ct = default)
        => await _db.Rulesets.OrderByDescending(r => r.CreatedAt).ToListAsync(ct);

    public async Task AddAsync(Ruleset ruleset, CancellationToken ct = default)
        => await _db.Rulesets.AddAsync(ruleset, ct);

    public Task SaveAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public sealed class AlertRepository : IAlertRepository
{
    private readonly FraudDbContext _db;
    public AlertRepository(FraudDbContext db) => _db = db;

    public async Task AddAsync(Alert alert, CancellationToken ct = default) => await _db.Alerts.AddAsync(alert, ct);
    public async Task<IReadOnlyList<Alert>> ListAsync(int page, int pageSize, CancellationToken ct = default)
        => await _db.Alerts.OrderByDescending(a => a.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
    public Task<Alert?> GetByIdAsync(Guid id, CancellationToken ct = default) => _db.Alerts.FirstOrDefaultAsync(a => a.Id == id, ct);
    public Task<int> CountAsync(CancellationToken ct = default) => _db.Alerts.CountAsync(ct);
    public Task SaveAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public sealed class CaseRepository : ICaseRepository
{
    private readonly FraudDbContext _db;
    public CaseRepository(FraudDbContext db) => _db = db;

    public async Task AddAsync(Case caseAggregate, CancellationToken ct = default) => await _db.Cases.AddAsync(caseAggregate, ct);
    public Task<Case?> GetByIdAsync(Guid id, CancellationToken ct = default) => _db.Cases.FirstOrDefaultAsync(c => c.Id == id, ct);
    public Task<Case?> GetOpenByEntityKeyAsync(string primaryEntityKey, CancellationToken ct = default)
        => _db.Cases.FirstOrDefaultAsync(c => c.PrimaryEntityKey == primaryEntityKey && c.Status != Domain.Entities.CaseStatus.Disposed, ct);
    public async Task<IReadOnlyList<Case>> ListAsync(int page, int pageSize, CancellationToken ct = default)
        => await _db.Cases.OrderByDescending(c => c.LastActivityAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
    public Task<int> CountAsync(CancellationToken ct = default) => _db.Cases.CountAsync(ct);
    public Task SaveAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public sealed class ListEntryRepository : IListEntryRepository
{
    private readonly FraudDbContext _db;
    public ListEntryRepository(FraudDbContext db) => _db = db;

    public async Task<IReadOnlyList<ListEntry>> ListAsync(CancellationToken ct = default)
        => await _db.ListEntries.ToListAsync(ct);

    public async Task AddAsync(ListEntry entry, CancellationToken ct = default)
        => await _db.ListEntries.AddAsync(entry, ct);

    public Task SaveAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public sealed class DeadLetterRepository : IDeadLetterRepository
{
    private readonly FraudDbContext _db;
    public DeadLetterRepository(FraudDbContext db) => _db = db;
    public async Task AddAsync(DeadLetterEvent evt, CancellationToken ct = default) => await _db.DeadLetterEvents.AddAsync(evt, ct);
    public async Task<IReadOnlyList<DeadLetterEvent>> ListAsync(int page, int pageSize, CancellationToken ct = default)
        => await _db.DeadLetterEvents.OrderByDescending(e => e.ReceivedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
    public Task<int> CountAsync(CancellationToken ct = default) => _db.DeadLetterEvents.CountAsync(ct);
    public Task SaveAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
