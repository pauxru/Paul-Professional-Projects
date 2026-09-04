namespace ExampleBank.Ledger.Application.Abstractions;

/// <summary>
/// A single ledger transaction scope. Each command creates its own unit of work (backed by its own
/// DbContext), so that operations running in parallel do not share change-tracking state. The
/// concurrency guarantees come from the account locks plus the serialized/transactional writes here.
/// </summary>
public interface ILedgerUnitOfWork : IAsyncDisposable
{
    IAccountRepository Accounts { get; }
    IJournalRepository Journal { get; }
    IHoldRepository Holds { get; }
    IFeeScheduleRepository FeeSchedules { get; }
    IInterestAccrualRepository InterestAccruals { get; }
    IIdempotencyRepository Idempotency { get; }

    Task BeginTransactionAsync(CancellationToken cancellationToken);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

/// <summary>Creates a fresh <see cref="ILedgerUnitOfWork"/> (and DbContext) per operation.</summary>
public interface ILedgerUnitOfWorkFactory
{
    Task<ILedgerUnitOfWork> CreateAsync(CancellationToken cancellationToken);
}
