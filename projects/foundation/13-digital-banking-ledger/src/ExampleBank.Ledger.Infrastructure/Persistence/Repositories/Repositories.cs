using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Domain.Accounts;
using ExampleBank.Ledger.Domain.Fees;
using ExampleBank.Ledger.Domain.Holds;
using ExampleBank.Ledger.Domain.Interest;
using ExampleBank.Ledger.Domain.Journal;
using Microsoft.EntityFrameworkCore;

namespace ExampleBank.Ledger.Infrastructure.Persistence.Repositories;

internal sealed class AccountRepository : IAccountRepository
{
    private readonly LedgerDbContext _db;
    public AccountRepository(LedgerDbContext db) => _db = db;

    public Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Accounts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<Account?> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        _db.Accounts.FirstOrDefaultAsync(a => a.Code == code, cancellationToken);

    public async Task<IReadOnlyList<Account>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var set = ids.ToHashSet();
        return await _db.Accounts.Where(a => set.Contains(a.Id)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Account>> GetChildrenAsync(Guid parentId, CancellationToken cancellationToken) =>
        await _db.Accounts.Where(a => a.ParentId == parentId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Account>> ListAsync(CancellationToken cancellationToken) =>
        await _db.Accounts.OrderBy(a => a.Code).ToListAsync(cancellationToken);

    public async Task AddAsync(Account account, CancellationToken cancellationToken) =>
        await _db.Accounts.AddAsync(account, cancellationToken);
}

internal sealed class JournalRepository : IJournalRepository
{
    private readonly LedgerDbContext _db;
    public JournalRepository(LedgerDbContext db) => _db = db;

    public async Task AddAsync(JournalEntry entry, CancellationToken cancellationToken) =>
        await _db.JournalEntries.AddAsync(entry, cancellationToken);

    public Task<JournalEntry?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.JournalEntries.Include(e => e.Postings).FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<(long Sequence, string Hash)> GetChainHeadAsync(CancellationToken cancellationToken)
    {
        var head = await _db.JournalEntries
            .OrderByDescending(e => e.SequenceNumber)
            .Select(e => new { e.SequenceNumber, e.Hash })
            .FirstOrDefaultAsync(cancellationToken);

        return head is null ? (0, LedgerHash.GenesisHash) : (head.SequenceNumber, head.Hash);
    }

    public async Task<IReadOnlyList<JournalEntry>> GetChainAsync(CancellationToken cancellationToken) =>
        await _db.JournalEntries.Include(e => e.Postings)
            .OrderBy(e => e.SequenceNumber)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<JournalEntry>> ListAsync(PageRequest page, CancellationToken cancellationToken) =>
        await _db.JournalEntries.Include(e => e.Postings)
            .OrderByDescending(e => e.SequenceNumber)
            .Skip((page.Page - 1) * page.PageSize)
            .Take(page.PageSize)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken) =>
        _db.JournalEntries.CountAsync(cancellationToken);

    public async Task<IReadOnlyList<JournalEntry>> GetReversalsOfAsync(Guid originalEntryId, CancellationToken cancellationToken) =>
        await _db.JournalEntries.Include(e => e.Postings)
            .Where(e => e.ReversalOfEntryId == originalEntryId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Posting>> GetPostingsForAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        await _db.Postings.Where(p => p.AccountId == accountId).AsNoTracking().ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Posting>> GetPostingsForAccountInPeriodAsync(
        Guid accountId, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken)
    {
        var query =
            from p in _db.Postings
            join e in _db.JournalEntries on p.JournalEntryId equals e.Id
            where p.AccountId == accountId && e.ValueDate >= fromInclusive && e.ValueDate <= toInclusive
            orderby e.ValueDate, e.SequenceNumber, p.Sequence
            select p;

        return await query.AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AccountMovement>> GetAccountMovementsAsync(
        Guid accountId, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken)
    {
        var query =
            from p in _db.Postings
            join e in _db.JournalEntries on p.JournalEntryId equals e.Id
            where p.AccountId == accountId && e.ValueDate >= fromInclusive && e.ValueDate <= toInclusive
            orderby e.ValueDate, e.SequenceNumber, p.Sequence
            select new AccountMovement(
                e.Id, e.SequenceNumber, e.ValueDate, e.BookingTimestamp, e.Description, e.Reference,
                p.Direction.ToString(), p.AmountMinor, p.Currency);

        return await query.AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<(long TotalDebits, long TotalCredits)> SumPostingsForAccountAsync(
        Guid accountId, CancellationToken cancellationToken)
    {
        long debits = await _db.Postings
            .Where(p => p.AccountId == accountId && p.Direction == PostingDirection.Debit)
            .SumAsync(p => (long?)p.AmountMinor, cancellationToken) ?? 0;
        long credits = await _db.Postings
            .Where(p => p.AccountId == accountId && p.Direction == PostingDirection.Credit)
            .SumAsync(p => (long?)p.AmountMinor, cancellationToken) ?? 0;
        return (debits, credits);
    }

    public async Task<(long TotalDebits, long TotalCredits)> SumPostingsForAccountBeforeAsync(
        Guid accountId, DateOnly beforeValueDate, CancellationToken cancellationToken)
    {
        var movements =
            from p in _db.Postings
            join e in _db.JournalEntries on p.JournalEntryId equals e.Id
            where p.AccountId == accountId && e.ValueDate < beforeValueDate
            select new { p.Direction, p.AmountMinor };

        long debits = await movements.Where(m => m.Direction == PostingDirection.Debit)
            .SumAsync(m => (long?)m.AmountMinor, cancellationToken) ?? 0;
        long credits = await movements.Where(m => m.Direction == PostingDirection.Credit)
            .SumAsync(m => (long?)m.AmountMinor, cancellationToken) ?? 0;
        return (debits, credits);
    }
}

internal sealed class HoldRepository : IHoldRepository
{
    private readonly LedgerDbContext _db;
    public HoldRepository(LedgerDbContext db) => _db = db;

    public async Task AddAsync(Hold hold, CancellationToken cancellationToken) =>
        await _db.Holds.AddAsync(hold, cancellationToken);

    public Task<Hold?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Holds.FirstOrDefaultAsync(h => h.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Hold>> GetActiveByAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        await _db.Holds.Where(h => h.AccountId == accountId && h.Status == HoldStatus.Active)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Hold>> GetExpiredActiveAsync(DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        // Filter status in SQL; evaluate the DateTimeOffset expiry in memory (SQLite cannot order
        // DateTimeOffset reliably). Active holds are few, so this is inexpensive.
        var active = await _db.Holds
            .Where(h => h.Status == HoldStatus.Active)
            .ToListAsync(cancellationToken);
        return active.Where(h => h.ExpiresAt <= asOf).ToList();
    }

    public async Task<long> SumActiveHoldsAsync(Guid accountId, CancellationToken cancellationToken) =>
        await _db.Holds.Where(h => h.AccountId == accountId && h.Status == HoldStatus.Active)
            .SumAsync(h => (long?)h.AmountMinor, cancellationToken) ?? 0;
}

internal sealed class FeeScheduleRepository : IFeeScheduleRepository
{
    private readonly LedgerDbContext _db;
    public FeeScheduleRepository(LedgerDbContext db) => _db = db;

    public Task<FeeSchedule?> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        _db.FeeSchedules.Include(f => f.Tiers).FirstOrDefaultAsync(f => f.Code == code, cancellationToken);

    public Task<FeeSchedule?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.FeeSchedules.Include(f => f.Tiers).FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

    public async Task AddAsync(FeeSchedule schedule, CancellationToken cancellationToken) =>
        await _db.FeeSchedules.AddAsync(schedule, cancellationToken);
}

internal sealed class InterestAccrualRepository : IInterestAccrualRepository
{
    private readonly LedgerDbContext _db;
    public InterestAccrualRepository(LedgerDbContext db) => _db = db;

    public Task<InterestAccrual?> GetByAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        _db.InterestAccruals.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);

    public async Task<IReadOnlyList<InterestAccrual>> ListAsync(CancellationToken cancellationToken) =>
        await _db.InterestAccruals.ToListAsync(cancellationToken);

    public async Task AddAsync(InterestAccrual accrual, CancellationToken cancellationToken) =>
        await _db.InterestAccruals.AddAsync(accrual, cancellationToken);
}

internal sealed class IdempotencyRepository : IIdempotencyRepository
{
    private readonly LedgerDbContext _db;
    public IdempotencyRepository(LedgerDbContext db) => _db = db;

    public Task<IdempotencyRecord?> FindAsync(string key, CancellationToken cancellationToken) =>
        _db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, cancellationToken);

    public async Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken) =>
        await _db.IdempotencyRecords.AddAsync(record, cancellationToken);
}
