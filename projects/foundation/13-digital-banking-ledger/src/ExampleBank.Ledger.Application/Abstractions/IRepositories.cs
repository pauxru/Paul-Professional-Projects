using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Domain.Accounts;
using ExampleBank.Ledger.Domain.Fees;
using ExampleBank.Ledger.Domain.Holds;
using ExampleBank.Ledger.Domain.Interest;
using ExampleBank.Ledger.Domain.Journal;

namespace ExampleBank.Ledger.Application.Abstractions;

public interface IAccountRepository
{
    Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<Account?> GetByCodeAsync(string code, CancellationToken cancellationToken);
    Task<IReadOnlyList<Account>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken);
    Task<IReadOnlyList<Account>> GetChildrenAsync(Guid parentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Account>> ListAsync(CancellationToken cancellationToken);
    Task AddAsync(Account account, CancellationToken cancellationToken);
}

public interface IJournalRepository
{
    Task AddAsync(JournalEntry entry, CancellationToken cancellationToken);
    Task<JournalEntry?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Returns the sequence number and hash of the current chain head (0 / genesis if empty).</summary>
    Task<(long Sequence, string Hash)> GetChainHeadAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<JournalEntry>> GetChainAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<JournalEntry>> ListAsync(PageRequest page, CancellationToken cancellationToken);
    Task<int> CountAsync(CancellationToken cancellationToken);

    /// <summary>Total reversed (in original terms) already booked against an entry, per posting.</summary>
    Task<IReadOnlyList<JournalEntry>> GetReversalsOfAsync(Guid originalEntryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Posting>> GetPostingsForAccountAsync(Guid accountId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Posting>> GetPostingsForAccountInPeriodAsync(
        Guid accountId, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken);

    /// <summary>Movements (postings enriched with entry fields) for an account within a value-date period, ordered chronologically.</summary>
    Task<IReadOnlyList<AccountMovement>> GetAccountMovementsAsync(
        Guid accountId, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken);

    Task<(long TotalDebits, long TotalCredits)> SumPostingsForAccountAsync(
        Guid accountId, CancellationToken cancellationToken);

    /// <summary>Sum of debits and credits for movements strictly before a value date, for a statement opening balance.</summary>
    Task<(long TotalDebits, long TotalCredits)> SumPostingsForAccountBeforeAsync(
        Guid accountId, DateOnly beforeValueDate, CancellationToken cancellationToken);
}

public interface IHoldRepository
{
    Task AddAsync(Hold hold, CancellationToken cancellationToken);
    Task<Hold?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<Hold>> GetActiveByAccountAsync(Guid accountId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Hold>> GetExpiredActiveAsync(DateTimeOffset asOf, CancellationToken cancellationToken);
    Task<long> SumActiveHoldsAsync(Guid accountId, CancellationToken cancellationToken);
}

public interface IFeeScheduleRepository
{
    Task<FeeSchedule?> GetByCodeAsync(string code, CancellationToken cancellationToken);
    Task<FeeSchedule?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(FeeSchedule schedule, CancellationToken cancellationToken);
}

public interface IInterestAccrualRepository
{
    Task<InterestAccrual?> GetByAccountAsync(Guid accountId, CancellationToken cancellationToken);
    Task<IReadOnlyList<InterestAccrual>> ListAsync(CancellationToken cancellationToken);
    Task AddAsync(InterestAccrual accrual, CancellationToken cancellationToken);
}

public interface IIdempotencyRepository
{
    Task<IdempotencyRecord?> FindAsync(string key, CancellationToken cancellationToken);
    Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken);
}
