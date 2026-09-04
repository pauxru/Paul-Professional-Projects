using ReconEngine.Application.Common;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;

namespace ReconEngine.Application.Abstractions;

/// <summary>Commits all pending changes as one transaction.</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public interface IImportStore
{
    Task AddBatchAsync(ImportBatch batch, CancellationToken ct = default);
    Task AddRecordsAsync(IReadOnlyList<ReconRecord> records, CancellationToken ct = default);
    Task<ImportBatch?> GetBatchAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ImportBatch>> ListBatchesAsync(int skip, int take, CancellationToken ct = default);
    Task<int> CountBatchesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ImportRejection>> GetRejectionsAsync(Guid batchId, CancellationToken ct = default);
    Task<bool> ChecksumExistsAsync(string checksum, CancellationToken ct = default);
}

public interface IRuleSetStore
{
    Task AddAsync(MatchingRuleSet ruleSet, CancellationToken ct = default);
    Task<MatchingRuleSet?> GetAsync(Guid id, CancellationToken ct = default);
    Task<MatchingRuleSet?> GetActiveAsync(CancellationToken ct = default);
    Task<MatchingRuleSet?> GetLatestByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<MatchingRuleSet>> ListAsync(CancellationToken ct = default);
    Task<int> MaxVersionAsync(string name, CancellationToken ct = default);
    Task DeactivateAllAsync(CancellationToken ct = default);
}

public interface IRecordStore
{
    /// <summary>Records eligible for a run: not yet matched, within the optional value-date window.</summary>
    Task<IReadOnlyList<ReconRecord>> GetWorkingSetAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<IReadOnlyList<ReconRecord>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    /// <summary>All records whose most recent run is the given run (used for per-run value reports).</summary>
    Task<IReadOnlyList<ReconRecord>> GetByLastRunAsync(Guid runId, CancellationToken ct = default);
    Task<PagedResult<ReconRecord>> QueryAsync(RecordQuery query, CancellationToken ct = default);
}

public interface IRunStore
{
    Task AddAsync(ReconciliationRun run, CancellationToken ct = default);
    Task<ReconciliationRun?> GetAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<ReconciliationRun>> ListAsync(int page, int pageSize, CancellationToken ct = default);
}

public interface IMatchStore
{
    Task AddRangeAsync(IEnumerable<Match> matches, CancellationToken ct = default);
    Task RemoveForRecordsAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken ct = default);
    Task<IReadOnlyList<Match>> GetByRunAsync(Guid runId, CancellationToken ct = default);
}

public interface IExceptionStore
{
    Task AddRangeAsync(IEnumerable<ReconciliationException> exceptions, CancellationToken ct = default);
    Task RemoveRangeAsync(IEnumerable<ReconciliationException> exceptions, CancellationToken ct = default);
    Task<ReconciliationException?> GetAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<ReconciliationException>> QueryAsync(ExceptionQuery query, CancellationToken ct = default);
    /// <summary>All exceptions not in the Resolved state (used for idempotent reconciliation and aging).</summary>
    Task<IReadOnlyList<ReconciliationException>> GetOpenAsync(CancellationToken ct = default);
}
