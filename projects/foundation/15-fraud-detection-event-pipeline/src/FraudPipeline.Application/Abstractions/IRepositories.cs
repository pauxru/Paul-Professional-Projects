using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.Rules;
using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.Application.Abstractions;

public interface ITransactionRepository
{
    Task AddAsync(Transaction transaction, CancellationToken ct = default);
    Task<Transaction?> GetByRefAsync(string transactionRef, CancellationToken ct = default);
    Task<Transaction?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Transaction>> ListAsync(int page, int pageSize, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Transaction>> ListRecentByCustomerAsync(string customerId, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<Transaction>> ListAllForReplayAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}

public interface IScoringDecisionRepository
{
    Task AddAsync(ScoringDecision decision, CancellationToken ct = default);
    Task<ScoringDecision?> GetByTransactionRefAsync(string transactionRef, bool shadow, CancellationToken ct = default);
    Task<IReadOnlyList<ScoringDecision>> ListForTransactionAsync(Guid transactionId, CancellationToken ct = default);
    Task<IReadOnlyList<ScoringDecision>> ListRecentAsync(int limit, bool shadow, CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}

public interface IRulesetRepository
{
    Task<Ruleset?> GetActiveAsync(CancellationToken ct = default);
    Task<Ruleset?> GetShadowAsync(CancellationToken ct = default);
    Task<Ruleset?> GetByVersionAsync(string version, CancellationToken ct = default);
    Task<IReadOnlyList<Ruleset>> ListAsync(CancellationToken ct = default);
    Task AddAsync(Ruleset ruleset, CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}

public interface IAlertRepository
{
    Task AddAsync(Alert alert, CancellationToken ct = default);
    Task<IReadOnlyList<Alert>> ListAsync(int page, int pageSize, CancellationToken ct = default);
    Task<Alert?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}

public interface ICaseRepository
{
    Task AddAsync(Case caseAggregate, CancellationToken ct = default);
    Task<Case?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Case?> GetOpenByEntityKeyAsync(string primaryEntityKey, CancellationToken ct = default);
    Task<IReadOnlyList<Case>> ListAsync(int page, int pageSize, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}

public interface IListEntryRepository
{
    Task<IReadOnlyList<ListEntry>> ListAsync(CancellationToken ct = default);
    Task AddAsync(ListEntry entry, CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}

public interface IDeadLetterRepository
{
    Task AddAsync(DeadLetterEvent evt, CancellationToken ct = default);
    Task<IReadOnlyList<DeadLetterEvent>> ListAsync(int page, int pageSize, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}
