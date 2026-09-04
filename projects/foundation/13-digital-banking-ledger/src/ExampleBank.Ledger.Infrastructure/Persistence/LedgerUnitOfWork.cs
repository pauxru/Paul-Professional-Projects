using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ExampleBank.Ledger.Infrastructure.Persistence;

/// <summary>
/// A unit of work over a single <see cref="LedgerDbContext"/> and database transaction. Translates
/// SQLite storage failures into the application-level exceptions the command executor understands
/// (duplicate idempotency key, transient busy/locked, optimistic-concurrency conflict).
/// </summary>
internal sealed class LedgerUnitOfWork : ILedgerUnitOfWork
{
    private readonly LedgerDbContext _db;
    private IDbContextTransaction? _transaction;

    public LedgerUnitOfWork(LedgerDbContext db)
    {
        _db = db;
        Accounts = new AccountRepository(db);
        Journal = new JournalRepository(db);
        Holds = new HoldRepository(db);
        FeeSchedules = new FeeScheduleRepository(db);
        InterestAccruals = new InterestAccrualRepository(db);
        Idempotency = new IdempotencyRepository(db);
    }

    public IAccountRepository Accounts { get; }
    public IJournalRepository Journal { get; }
    public IHoldRepository Holds { get; }
    public IFeeScheduleRepository FeeSchedules { get; }
    public IInterestAccrualRepository InterestAccruals { get; }
    public IIdempotencyRepository Idempotency { get; }

    public async Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        _transaction ??= await _db.Database.BeginTransactionAsync(cancellationToken);
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException(ex.Message);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqlite)
        {
            throw Translate(sqlite);
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_transaction is not null)
        {
            await _transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
        }

        await _db.DisposeAsync();
    }

    private static Exception Translate(SqliteException sqlite) => sqlite.SqliteErrorCode switch
    {
        // 19: constraint violation (our only unique constraints are idempotency key + natural keys).
        19 => new DuplicateKeyException(sqlite.Message),
        // 5: database busy, 6: database table locked — transient under concurrent writers.
        5 or 6 => new TransientStorageException(sqlite.Message),
        _ => sqlite,
    };
}

internal sealed class LedgerUnitOfWorkFactory : ILedgerUnitOfWorkFactory
{
    private readonly IDbContextFactory<LedgerDbContext> _contextFactory;

    public LedgerUnitOfWorkFactory(IDbContextFactory<LedgerDbContext> contextFactory) =>
        _contextFactory = contextFactory;

    public async Task<ILedgerUnitOfWork> CreateAsync(CancellationToken cancellationToken)
    {
        var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return new LedgerUnitOfWork(context);
    }
}
