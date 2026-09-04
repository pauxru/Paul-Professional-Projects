using FraudPipeline.Application.Abstractions;
using FraudPipeline.Domain.Entities;

namespace FraudPipeline.UnitTests.Fakes;

public sealed class InMemoryTransactionRepository : ITransactionRepository
{
    private readonly List<Transaction> _items = new();
    public Task AddAsync(Transaction transaction, CancellationToken ct = default) { _items.Add(transaction); return Task.CompletedTask; }
    public Task<Transaction?> GetByRefAsync(string transactionRef, CancellationToken ct = default) => Task.FromResult(_items.FirstOrDefault(t => t.TransactionRef == transactionRef));
    public Task<Transaction?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_items.FirstOrDefault(t => t.Id == id));
    public Task<IReadOnlyList<Transaction>> ListAsync(int page, int pageSize, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Transaction>>(_items.OrderByDescending(t => t.OccurredAt).Skip((page - 1) * pageSize).Take(pageSize).ToList());
    public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(_items.Count);
    public Task<IReadOnlyList<Transaction>> ListRecentByCustomerAsync(string customerId, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Transaction>>(_items.Where(t => t.CustomerId == customerId).OrderByDescending(t => t.OccurredAt).Take(limit).ToList());
    public Task<IReadOnlyList<Transaction>> ListAllForReplayAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Transaction>>(_items.OrderBy(t => t.OccurredAt).ToList());
    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<Transaction> Items => _items;
}

public sealed class InMemoryScoringDecisionRepository : IScoringDecisionRepository
{
    private readonly List<ScoringDecision> _items = new();
    public Task AddAsync(ScoringDecision decision, CancellationToken ct = default) { _items.Add(decision); return Task.CompletedTask; }
    public Task<ScoringDecision?> GetByTransactionRefAsync(string transactionRef, bool shadow, CancellationToken ct = default)
        => Task.FromResult(_items.FirstOrDefault(d => d.TransactionRef == transactionRef && d.Shadow == shadow));
    public Task<IReadOnlyList<ScoringDecision>> ListForTransactionAsync(Guid transactionId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ScoringDecision>>(_items.Where(d => d.TransactionId == transactionId).ToList());
    public Task<IReadOnlyList<ScoringDecision>> ListRecentAsync(int limit, bool shadow, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ScoringDecision>>(_items.Where(d => d.Shadow == shadow).OrderByDescending(d => d.DecidedAt).Take(limit).ToList());
    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<ScoringDecision> Items => _items;
}

public sealed class InMemoryRulesetRepository : IRulesetRepository
{
    private readonly List<Ruleset> _items = new();
    public Task<Ruleset?> GetActiveAsync(CancellationToken ct = default) => Task.FromResult(_items.FirstOrDefault(r => r.IsActive));
    public Task<Ruleset?> GetShadowAsync(CancellationToken ct = default) => Task.FromResult(_items.FirstOrDefault(r => r.IsShadow));
    public Task<Ruleset?> GetByVersionAsync(string version, CancellationToken ct = default) => Task.FromResult(_items.FirstOrDefault(r => r.Version == version));
    public Task<IReadOnlyList<Ruleset>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Ruleset>>(_items.ToList());
    public Task AddAsync(Ruleset ruleset, CancellationToken ct = default) { _items.Add(ruleset); return Task.CompletedTask; }
    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class InMemoryListRepository : IListEntryRepository
{
    private readonly List<ListEntry> _items = new();
    public Task<IReadOnlyList<ListEntry>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ListEntry>>(_items.ToList());
    public Task AddAsync(ListEntry entry, CancellationToken ct = default) { _items.Add(entry); return Task.CompletedTask; }
    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class InMemoryAlertRepository : IAlertRepository
{
    private readonly List<Alert> _items = new();
    public Task AddAsync(Alert alert, CancellationToken ct = default) { _items.Add(alert); return Task.CompletedTask; }
    public Task<IReadOnlyList<Alert>> ListAsync(int page, int pageSize, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Alert>>(_items.ToList());
    public Task<Alert?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_items.FirstOrDefault(a => a.Id == id));
    public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(_items.Count);
    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<Alert> Items => _items;
}

public sealed class InMemoryCaseRepository : ICaseRepository
{
    private readonly List<Case> _items = new();
    public Task AddAsync(Case caseAggregate, CancellationToken ct = default) { _items.Add(caseAggregate); return Task.CompletedTask; }
    public Task<Case?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_items.FirstOrDefault(c => c.Id == id));
    public Task<Case?> GetOpenByEntityKeyAsync(string primaryEntityKey, CancellationToken ct = default)
        => Task.FromResult(_items.FirstOrDefault(c => c.PrimaryEntityKey == primaryEntityKey && c.Status != CaseStatus.Disposed));
    public Task<IReadOnlyList<Case>> ListAsync(int page, int pageSize, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Case>>(_items.ToList());
    public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(_items.Count);
    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<Case> Items => _items;
}

public sealed class InMemoryDeadLetterRepository : IDeadLetterRepository
{
    private readonly List<DeadLetterEvent> _items = new();
    public Task AddAsync(DeadLetterEvent evt, CancellationToken ct = default) { _items.Add(evt); return Task.CompletedTask; }
    public Task<IReadOnlyList<DeadLetterEvent>> ListAsync(int page, int pageSize, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DeadLetterEvent>>(_items.ToList());
    public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(_items.Count);
    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<DeadLetterEvent> Items => _items;
}
